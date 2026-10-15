using System.Collections.Concurrent;
using System.Diagnostics;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;

namespace FolderSync.Core.Engine;

public enum JobRunState { Idle, Watching, Analyzing, Running }

/// <summary>Ket qua 1 lan chay (thu cong hoac tu dong) - Outcome null khi co Error.</summary>
public sealed record JobRunReport(string JobId, string Trigger, SessionOutcome? Outcome, Exception? Error);

/// <summary>
/// Dieu phoi 1 Job DOC LAP voi cac Job khac: phan tich / dong bo thu cong, dong bo lien tuc khi file
/// thay doi (FileSystemWatcher + tre debounce), chay dinh ky, Scripts truoc/sau, don history.
///
/// Nguyen tac:
/// - Moi Job 1 khoa rieng (theo Id, dung chung giua cac instance) -> 2 Job chay song song duoc,
///   nhung 1 Job khong bao gio chay 2 phien chong nhau (ke ca khi UI tao lai runner luc sua Job).
/// - Thay doi do CHINH lan chay ghi ra (copy/xoa/doi ten o dich, .tmp-sync, history) khong kich hoat
///   lai Job - neu khong che do lien tuc se tu goi minh mai mai. Nhan dien bang ANH CHUP file sau khi
///   ghi (co/khong, size, mtime), khong bang thoi gian: nguoi dung sua dung file do ngay sau khi
///   app vua chep thi anh chup lech -> van duoc dong bo.
/// - Thay doi THAT xay ra trong luc dang chay khong bi mat: danh dau va chay lai sau khi xong.
/// - Watcher loi (tran bo dem, rut USB) -> go watcher, thu gan lai dinh ky, quet lai toan bo khi co lai.
///   Moi chot an toan cua SyncSession van ap dung (goc mat -> huy, khong xoa nham).
/// Su kien (Log/StateChanged/Started/Finished) ban tu THREAD NEN - UI tu chuyen ve UI thread.
/// </summary>
public sealed class JobRunner : IDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    private static readonly TimeSpan OwnDirGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RewatchInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryWhenBusy = TimeSpan.FromSeconds(3);

    private readonly SqliteStateStore _store;
    private readonly SemaphoreSlim _gate;
    private readonly ScanCache _cache = new();
    private readonly SyncFilter _filter;
    private readonly object _lock = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    // Duong dan lan chay nay se ghi (tu Planned) - su kien cua chung luc dang ghi do bi bo qua
    private readonly HashSet<string> _plannedPaths = new(StringComparer.OrdinalIgnoreCase);
    // Anh chup file SAU khi lan chay ghi xong (tu Completed) - su kien khop anh chup = do app ghi
    private readonly Dictionary<string, FileSnapshot> _written = new(StringComparer.OrdinalIgnoreCase);
    // Thu muc cha cua cac duong dan tren (Executor tao/don thu muc) - bo qua trong luc chay + an han
    private readonly HashSet<string> _ownDirs = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct FileSnapshot(bool Exists, long Length, DateTime MtimeUtc)
    {
        public static FileSnapshot Of(string full)
        {
            try
            {
                var fi = new FileInfo(full);
                return fi.Exists ? new(true, fi.Length, fi.LastWriteTimeUtc) : new(false, 0, default);
            }
            catch (Exception) { return new(false, -1, default); } // khong doc duoc -> khong khop -> coi la thay doi that
        }
    }
    private Timer? _debounce, _periodic, _rewatch;
    private bool _autoStarted, _running, _dirtyDuringRun, _disposed;
    private DateTime _ignoreOwnUntilUtc;

    public JobRunner(JobRecord job, SqliteStateStore store)
    {
        Job = job;
        Options = JobOptions.FromJson(job.SettingsJson);
        _store = store;
        _filter = Options.BuildFilter();
        _gate = Gates.GetOrAdd(job.Id, _ => new SemaphoreSlim(1, 1));
    }

    public JobRecord Job { get; }
    public JobOptions Options { get; }
    public string Id => Job.Id;
    /// <summary>
    /// Runner dang giu khoa chay cua tung Job (theo Id). Sua Job khi dang chay = runner MOI thay runner cu,
    /// runner cu van chay tiep toi het (khoa theo Id) -> runner moi phai bao trang thai / Huy / Tam dung
    /// cua lan chay do. Truoc day runner moi bao "ranh": UI an nut Huy + thanh tien trinh, Huy khong tac dung.
    /// </summary>
    private static readonly ConcurrentDictionary<string, JobRunner> Active = new();

    private JobRunState _state;
    private readonly PauseGate _pause = new();

    /// <summary>Trang thai cua Job - neu runner cu (da bi thay) con dang chay thi la trang thai cua no.</summary>
    public JobRunState State => ActiveOther() is { } other ? other._state : _state;
    public bool IsBusy => State is JobRunState.Analyzing or JobRunState.Running;
    /// <summary>Lan dong bo dang chay cua Job nay dang tam dung.</summary>
    public bool IsPaused => (ActiveOther() ?? this) is var r && r._state == JobRunState.Running && r._pause.IsPaused;

    private JobRunner? ActiveOther()
        => Active.TryGetValue(Job.Id, out var a) && !ReferenceEquals(a, this) ? a : null;

    private void EnterActive() => Active[Job.Id] = this;
    private void LeaveActive() => Active.TryRemove(new KeyValuePair<string, JobRunner>(Job.Id, this));
    public bool AutoEnabled => Options.AutoOnFileChange || Options.AutoPeriodMinutes > 0;
    public DateTime? LastFinishedLocal { get; private set; }
    public string? LastSummary { get; private set; }
    /// <summary>So file loi cua lan chay gan nhat (UI to do Job co loi).</summary>
    public int LastErrorCount { get; private set; }

    /// <summary>Noi nhan tien trinh cua cac lan chay TU DONG (UI gan 1 Progress&lt;T&gt; tao tren UI thread).</summary>
    public IProgress<SyncProgress>? AutoProgress { get; set; }

    public event Action<JobRunner, string>? Log;
    public event Action<JobRunner>? StateChanged;
    /// <summary>1 lan dong bo bat dau (truoc khi quet) - kem ly do kich hoat.</summary>
    public event Action<JobRunner, string>? Started;
    public event Action<JobRunner, JobRunReport>? Finished;

    /// <summary>So lan dong bo da chay xong (dung cho test va hien thi).</summary>
    public int RunCount { get; private set; }

    public SyncSession CreateSession() => new(
        Job.Id, Job.LeftRoot, Job.RightRoot, Job.Mode, _store,
        options: new SyncOptions(
            MaxDeletePercent: Options.SafetyMaxDeletePercent,
            CopyCreationTime: Options.CopyCreationTime,
            CopyAttributes: Options.CopyAttributes,
            MaxBytesPerSecond: Options.MaxCopyBytesPerSecond),
        policy: Job.Policy,
        filter: _filter,
        detectRenames: Options.DetectRenames,
        computeHashes: Options.CompareChecksums,
        hashAllFiles: Options.CompareChecksums && Options.FullContentCompare,
        backupPropagateDeletes: Options.PropagateDeletes,
        timeTolerance: Options.TimeTolerance,
        createMissingRoots: Options.CreateFoldersIfMissing,
        scanCache: _cache);

    // ================= Thu cong =================

    /// <summary>Phan tich (khong dong vao file). null = Job dang ban (dang chay phien khac).</summary>
    /// <param name="progress">Nhan Scanning / Hashing de UI hien "dang quet n file" thay vi dung im.</param>
    /// <returns>null = Job dang ban HOAC nguoi dung bam Huy.</returns>
    public async Task<SessionOutcome?> AnalyzeAsync(IProgress<SyncProgress>? progress = null)
    {
        if (!await _gate.WaitAsync(0)) { Log?.Invoke(this, $"Job '{Job.Name}' dang chay - bo qua yeu cau phan tich."); return null; }
        var cts = BeginCancelable();
        try
        {
            EnterActive();
            SetState(JobRunState.Analyzing);
            return await Task.Run(() =>
            {
                RunScript(Options.ScriptPreAnalyze, "", null);
                return CreateSession().AnalyzeOnly(progress, cts.Token);
            });
        }
        catch (OperationCanceledException)
        {
            Log?.Invoke(this, $"Da HUY phan tich Job '{Job.Name}' theo yeu cau.");
            return null;
        }
        finally
        {
            EndCancelable(cts);
            SetState(_autoStarted ? JobRunState.Watching : JobRunState.Idle);
            LeaveActive();
            _gate.Release();
            StateChanged?.Invoke(this); // runner moi (neu Job vua bi sua) doi trang thai theo
        }
    }

    // ================= Huy =================

    private CancellationTokenSource? _cts;

    private CancellationTokenSource BeginCancelable()
    {
        var cts = new CancellationTokenSource();
        lock (_lock) _cts = cts;
        return cts;
    }

    private void EndCancelable(CancellationTokenSource cts)
    {
        lock (_lock) { if (ReferenceEquals(_cts, cts)) _cts = null; }
        cts.Dispose();
    }

    /// <summary>
    /// Huy phan tich / dong bo dang chay cua Job nay: dung o file tiep theo; file dang chep do bi bo
    /// (xoa file tam), khong luu state. Khong co gi dang chay thi khong lam gi.
    /// </summary>
    public void Cancel()
    {
        var target = ActiveOther() ?? this;
        lock (target._lock)
        {
            try { target._cts?.Cancel(); } catch (ObjectDisposedException) { }
        }
        // Dang tam dung: WaitWhilePaused cung nhan ct nen thoat ngay; mo gate de lan chay sau khong bi ket
        target._pause.Resume();
    }

    /// <summary>Tam dung lan dong bo dang chay (dung o diem an toan, xem PauseGate). Khong chay thi khong lam gi.</summary>
    public void Pause()
    {
        var target = ActiveOther() ?? this;
        if (target._state != JobRunState.Running) return;
        target._pause.Pause();
        target.StateChanged?.Invoke(target);
        if (!ReferenceEquals(target, this)) StateChanged?.Invoke(this);
    }

    public void Resume()
    {
        var target = ActiveOther() ?? this;
        target._pause.Resume();
        target.StateChanged?.Invoke(target);
        if (!ReferenceEquals(target, this)) StateChanged?.Invoke(this);
    }

    /// <summary>
    /// Dong bo 1 lan. null = khong chay vi Job dang ban (lan kich hoat tu dong thi duoc danh dau de
    /// chay lai ngay sau phien hien tai). Khong bao gio nem ngoai le - loi nam trong JobRunReport.
    /// </summary>
    public async Task<SessionOutcome?> RunAsync(string trigger, IProgress<SyncProgress>? progress = null)
    {
        if (_disposed) return null;
        if (!await _gate.WaitAsync(0))
        {
            bool retry;
            lock (_lock) { if (_running) _dirtyDuringRun = true; retry = !_running; }
            // Khoa dang bi giu boi lan PHAN TICH hoac runner CU (Job vua bi sua, da Dispose nen khong tu chay
            // lai): thu lai sau, neu khong thay doi file xay ra trong luc do bi bo sot toi lan thay doi tiep theo.
            // ScheduleRun tu bo qua khi Job khong bat tu dong.
            if (retry) ScheduleRun(RetryWhenBusy, trigger);
            return null;
        }

        SessionOutcome? outcome = null;
        Exception? error = null;
        bool rerun;
        try
        {
            lock (_lock)
            {
                _running = true;
                _dirtyDuringRun = false;
                _plannedPaths.Clear();
                _written.Clear();
                _ownDirs.Clear();
            }
            _pause.Resume();
            EnterActive();
            SetState(JobRunState.Running);
            Started?.Invoke(this, trigger);

            var sink = new OwnPathRecorder(this, progress);
            var cts = BeginCancelable();
            try
            {
                outcome = await Task.Run(() =>
                {
                    RunScript(Options.ScriptPreAnalyze, "", null);
                    var oc = CreateSession().Run(sink, cts.Token, _pause);
                    RunScript(Options.ScriptPostSync, oc.Result!.Aborted ? "aborted" : "", oc.Result);
                    CleanupHistory();
                    return oc;
                });
            }
            finally { EndCancelable(cts); }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            lock (_lock)
            {
                _running = false;
                _ignoreOwnUntilUtc = DateTime.UtcNow + OwnDirGrace;
                // Nguoi dung vua bam Huy -> khong tu chay lai ngay (lan thay doi file tiep theo moi chay)
                bool canceled = error is OperationCanceledException || outcome?.Result?.Canceled == true;
                rerun = _dirtyDuringRun && _autoStarted && !_disposed && !canceled;
            }
            RunCount++;
            LastFinishedLocal = DateTime.Now;
            LastSummary = Summarize(outcome, error);
            LastErrorCount = error is not null ? 1 : outcome?.Result?.Errors.Count ?? 0;
            _pause.Resume();
            SetState(_autoStarted ? JobRunState.Watching : JobRunState.Idle);
            LeaveActive();
            _gate.Release();
            StateChanged?.Invoke(this); // runner moi (neu Job vua bi sua) doi trang thai theo
        }

        Finished?.Invoke(this, new JobRunReport(Job.Id, trigger, outcome, error));

        // Lan chay vua tao folder con thieu (CreateFoldersIfMissing) -> gan watcher ngay, khong doi chu ky thu lai
        bool needWatch;
        lock (_lock) needWatch = _autoStarted && !_disposed && Options.AutoOnFileChange && _watchers.Count == 0;
        if (needWatch && WatchedRoots().All(Directory.Exists))
        {
            AttachWatchers();
            // Thay doi xay ra giua luc lan chay tao folder va luc gan watcher khong co ai nghe -> chay bat kip
            // 1 lan (CI cham tung bo sot: sua file o dich ngay sau khi tao folder ma Job khong chay lai)
            lock (_lock) needWatch = _watchers.Count > 0;
            if (needWatch) ScheduleDebounced();
        }

        if (rerun)
        {
            Log?.Invoke(this, $"Job '{Job.Name}': co thay doi trong luc dang chay - se dong bo lai.");
            ScheduleDebounced();
        }
        return outcome;
    }

    private static string Summarize(SessionOutcome? o, Exception? error)
    {
        if (error is OperationCanceledException) return "Da huy theo yeu cau";
        if (error is not null) return "Loi: " + error.Message;
        var r = o?.Result;
        if (r is null) return "";
        if (r.Canceled) return $"Da huy giua chung (da xong copy {r.Copied}, xoa {r.Deleted})";
        if (r.Aborted) return "Da huy (chot an toan)";
        int changed = r.Copied + r.Deleted + r.Moved;
        return changed == 0 && r.Errors.Count == 0 && r.Conflicts == 0
            ? "Da dong bo, khong co thay doi"
            : $"copy {r.Copied}, xoa {r.Deleted}, doi ten {r.Moved}" +
              (r.Conflicts > 0 ? $", xung dot {r.Conflicts}" : "") + (r.Errors.Count > 0 ? $", loi {r.Errors.Count}" : "");
    }

    // ================= Tu dong =================

    /// <summary>
    /// Bat che do tu dong theo Options: dong bo lien tuc (watcher) va/hoac dinh ky.
    /// Dong bo lien tuc chay 1 lan ngay khi bat de bat kip thay doi xay ra luc app dang tat.
    /// </summary>
    public void StartAuto(bool appStarting)
    {
        if (_disposed || !AutoEnabled) return;
        lock (_lock) _autoStarted = true;

        if (Options.AutoOnFileChange) AttachWatchers();
        if (Options.AutoPeriodMinutes > 0)
        {
            var period = TimeSpan.FromMinutes(Options.AutoPeriodMinutes);
            _periodic = new Timer(_ => _ = RunAsync("dinh ky", AutoProgress), null, period, period);
        }
        if (!IsBusy) SetState(JobRunState.Watching);

        if (Options.AutoOnFileChange || (appStarting && Options.AutoOnStart))
            ScheduleRun(TimeSpan.FromSeconds(1), appStarting ? "khi mo app" : "bat dong bo lien tuc");
    }

    public void StopAuto()
    {
        lock (_lock) _autoStarted = false;
        DetachWatchers();
        _periodic?.Dispose(); _periodic = null;
        _rewatch?.Dispose(); _rewatch = null;
        _debounce?.Dispose(); _debounce = null;
        if (!IsBusy) SetState(JobRunState.Idle);
    }

    private IEnumerable<string> WatchedRoots()
    {
        // Moi che do deu theo doi CA 2 phia: Backup cung phai chep lai khi ai do xoa/sua file o dich
        // (ban dau chi theo doi nguon -> nguoi dung xoa file o dich ma Job khong chay lai).
        // File do chinh app ghi da bi bo qua bang anh chup nen khong tu kich hoat vong lap.
        yield return Job.LeftRoot;
        yield return Job.RightRoot;
    }

    private void AttachWatchers()
    {
        DetachWatchers();
        var missing = WatchedRoots().Where(r => !Directory.Exists(r)).ToList();
        if (missing.Count > 0)
        {
            // Job tu tao folder thieu o lan chay dau -> se gan watcher ngay sau lan chay do, khong can bao
            if (!Options.CreateFoldersIfMissing)
                Log?.Invoke(this, $"Job '{Job.Name}': chua theo doi duoc (khong thay {string.Join(", ", missing)}) - se thu lai.");
            StartRewatch();
            return;
        }
        try
        {
            foreach (var root in WatchedRoots().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                   | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                var capturedRoot = root;
                w.Created += (_, e) => OnFsEvent(capturedRoot, e.FullPath, e.ChangeType);
                w.Changed += (_, e) => OnFsEvent(capturedRoot, e.FullPath, e.ChangeType);
                w.Deleted += (_, e) => OnFsEvent(capturedRoot, e.FullPath, e.ChangeType);
                w.Renamed += (_, e) =>
                {
                    OnFsEvent(capturedRoot, e.OldFullPath, WatcherChangeTypes.Deleted);
                    OnFsEvent(capturedRoot, e.FullPath, WatcherChangeTypes.Created);
                };
                w.Error += (_, e) => OnWatcherError(e.GetException());
                w.EnableRaisingEvents = true;
                lock (_lock) _watchers.Add(w);
            }
            _rewatch?.Dispose(); _rewatch = null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log?.Invoke(this, $"Job '{Job.Name}': khong tao duoc watcher ({ex.Message}) - se thu lai.");
            DetachWatchers();
            StartRewatch();
        }
    }

    private void DetachWatchers()
    {
        List<FileSystemWatcher> old;
        lock (_lock) { old = _watchers.ToList(); _watchers.Clear(); }
        foreach (var w in old)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch (Exception) { /* da hong san */ }
        }
    }

    private void StartRewatch()
    {
        if (_rewatch is not null || _disposed) return;
        _rewatch = new Timer(_ =>
        {
            bool auto;
            lock (_lock) auto = _autoStarted;
            if (!auto || !WatchedRoots().All(Directory.Exists)) return;
            Log?.Invoke(this, $"Job '{Job.Name}': folder da san sang lai - tiep tuc theo doi va quet lai.");
            AttachWatchers();
            ScheduleDebounced();
        }, null, RewatchInterval, RewatchInterval);
    }

    private void OnWatcherError(Exception ex)
    {
        Log?.Invoke(this, $"Job '{Job.Name}': theo doi thay doi bi loi ({ex.Message}) - se quet lai toan bo.");
        DetachWatchers();
        StartRewatch();
        ScheduleDebounced();
    }

    private void OnFsEvent(string root, string fullPath, WatcherChangeTypes change)
    {
        if (_disposed) return;
        var full = Normalize(fullPath);
        if (full.Contains(Path.DirectorySeparatorChar + HistoryService.HistoryFolderName, StringComparison.OrdinalIgnoreCase)
            || full.EndsWith(".tmp-sync", StringComparison.OrdinalIgnoreCase)) return;
        // Tao/sua THU MUC khong tu no can dong bo (file ben trong se ban su kien rieng)
        if (change is WatcherChangeTypes.Created or WatcherChangeTypes.Changed && Directory.Exists(full)) return;
        var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
        if (rel.StartsWith("..", StringComparison.Ordinal) || !_filter.IsIncluded(rel)) return;

        lock (_lock)
        {
            if (_ownDirs.Contains(full) && (_running || DateTime.UtcNow < _ignoreOwnUntilUtc)) return;
            if (_written.TryGetValue(full, out var snap))
            {
                if (FileSnapshot.Of(full) == snap) return; // dung nhu app vua ghi
            }
            else if (_running && _plannedPaths.Contains(full)) return; // app dang ghi file nay
            if (_running) { _dirtyDuringRun = true; return; }
        }
        ScheduleDebounced();
    }

    private void ScheduleDebounced()
        => ScheduleRun(TimeSpan.FromSeconds(Math.Max(1, Options.AutoFileChangeDelaySec)), "file thay doi");

    private void ScheduleRun(TimeSpan delay, string trigger)
    {
        lock (_lock)
        {
            if (_disposed || !_autoStarted) return;
            _debounce?.Dispose();
            _debounce = new Timer(_ => _ = RunAsync(trigger, AutoProgress), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
        catch (Exception) { return path; }
    }

    /// <summary>
    /// Boc IProgress cua UI (Executor goi DONG BO tren thread cua no, truoc/sau khi dong vao file):
    /// Planned -> ghi lai moi duong dan se bi ghi/xoa/doi ten + thu muc cha;
    /// Completed/Failed -> chup lai trang thai that cua cac duong dan do sau khi ghi.
    /// </summary>
    private sealed class OwnPathRecorder(JobRunner owner, IProgress<SyncProgress>? inner) : IProgress<SyncProgress>
    {
        public void Report(SyncProgress value)
        {
            if (value.Stage == SyncProgressStage.Planned && value.Plan is not null)
            {
                var paths = value.Plan.SelectMany(owner.TouchedPaths).Select(Normalize).ToList();
                lock (owner._lock)
                {
                    foreach (var full in paths)
                    {
                        owner._plannedPaths.Add(full);
                        var dir = Path.GetDirectoryName(full);
                        while (dir is not null && dir.Length > 3 && owner._ownDirs.Add(dir))
                            dir = Path.GetDirectoryName(dir);
                    }
                }
            }
            else if (value.Stage is SyncProgressStage.Completed or SyncProgressStage.Failed && value.Action is not null)
            {
                var snaps = owner.TouchedPaths(value.Action).Select(Normalize)
                    .Select(full => (full, snap: FileSnapshot.Of(full))).ToList();
                lock (owner._lock)
                {
                    foreach (var (full, snap) in snaps) owner._written[full] = snap;
                }
            }
            inner?.Report(value);
        }
    }

    private IEnumerable<string> TouchedPaths(SyncAction a)
    {
        string L(string rel) => Path.Combine(Job.LeftRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        string R(string rel) => Path.Combine(Job.RightRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        switch (a.Kind)
        {
            case SyncActionKind.CopyLeftToRight: yield return R(a.TargetPath ?? a.RelativePath); break;
            case SyncActionKind.CopyRightToLeft: yield return L(a.TargetPath ?? a.RelativePath); break;
            case SyncActionKind.DeleteLeft: yield return L(a.RelativePath); break;
            case SyncActionKind.DeleteRight: yield return R(a.RelativePath); break;
            case SyncActionKind.MoveLeft: yield return L(a.RelativePath); yield return L(a.TargetPath!); break;
            case SyncActionKind.MoveRight: yield return R(a.RelativePath); yield return R(a.TargetPath!); break;
        }
    }

    // ================= Scripts + don history =================

    private void RunScript(string command, string result, SyncResult? r)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        int changed = r is null ? 0 : r.Copied + r.Deleted + r.Moved;
        var expanded = command
            .Replace("%JOBNAME%", Job.Name)
            .Replace("%RESULT%", result)
            .Replace("%CHANGED%", changed.ToString())
            .Replace("%ERRORS%", (r?.Errors.Count ?? 0).ToString())
            .Replace("%CONFLICTS%", (r?.Conflicts ?? 0).ToString())
            .Replace("%LF%", Job.LeftRoot)
            .Replace("%RF%", Job.RightRoot);
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/C " + expanded) { CreateNoWindow = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            if (p is not null && !p.WaitForExit(30_000))
                Log?.Invoke(this, $"Script chay qua 30 giay, bo qua cho: {command}");
        }
        catch (Exception ex) { Log?.Invoke(this, "Script loi: " + ex.Message); }
    }

    private void CleanupHistory()
    {
        if (!Options.CleanupHistory) return;
        try
        {
            int n = HistoryService.Cleanup(Job.LeftRoot, Options.HistoryRetentionDays)
                  + HistoryService.Cleanup(Job.RightRoot, Options.HistoryRetentionDays);
            if (n > 0) Log?.Invoke(this, $"Da don {n} muc history cu (qua {Options.HistoryRetentionDays} ngay) cua Job '{Job.Name}'.");
        }
        catch (Exception ex) { Log?.Invoke(this, "Don history loi: " + ex.Message); }
    }

    private void SetState(JobRunState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this);
    }

    /// <summary>
    /// Runner moi (tao lai khi Job bi sua) giu lich su hien thi cua runner cu - neu khong, danh sach Job
    /// mat dong "lan chay cuoi" moi lan doi cau hinh.
    /// </summary>
    public void InheritHistory(JobRunner previous)
    {
        LastFinishedLocal = previous.LastFinishedLocal;
        LastSummary = previous.LastSummary;
        LastErrorCount = previous.LastErrorCount;
        RunCount = previous.RunCount;
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopAuto();
        lock (_lock) _disposed = true;
    }
}
