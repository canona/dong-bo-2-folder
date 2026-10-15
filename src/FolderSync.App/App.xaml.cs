using System.IO;
using System.Threading;
using System.Windows;

namespace FolderSync.App;

/// <summary>
/// Khoi dong ung dung:
/// - CHI 1 phien ban: 2 phien ban cung mo 1 state.db se co 2 JobRunner cho cung 1 Job -> chay chong,
///   giam len nhau. Mo lan 2 (vd vua tu chay cung Windows vua bam icon) chi bao phien ban dau hien cua so.
/// - Tham so: --tray = khoi dong an o khay (dung khi chay cung Windows); --mini = mo che do Mini.
/// - ShutdownMode = OnExplicitShutdown: an cua so chinh xuong khay KHONG duoc lam thoat app.
/// Ten mutex trung voi AppMutex trong packaging/FolderSync.iss (bo cai cho dong app truoc khi cai de).
/// </summary>
public partial class App : Application
{
    public const string MutexName = "FolderSyncSingleInstance";
    private const string ShowEventName = "FolderSyncShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Crash khong de lai stack trace trong Event Log (tung gap khi phan tich folder rat lon)
        // -> tu ghi crash.log canh state.db de con dau vet ma dieu tra.
        DispatcherUnhandledException += (_, ev) => WriteCrashLog("UI thread", ev.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => WriteCrashLog("AppDomain", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) => WriteCrashLog("Task", ev.Exception);
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Da co FolderSync dang chay: nho no hien cua so roi thoat
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var startTray = e.Args.Any(a => a.Equals(StartupManager.TrayArg, StringComparison.OrdinalIgnoreCase));
        var startMini = e.Args.Any(a => a.Equals("--mini", StringComparison.OrdinalIgnoreCase));

        var main = new MainWindow();
        MainWindow = main;
        main.Start(startTray ? StartMode.Tray : startMini ? StartMode.Mini : StartMode.LastUsed);

        // Phien ban thu 2 bao hien cua so -> mo che do day du
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var waiter = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() => main.ShowFull());
        }) { IsBackground = true, Name = "FolderSync.ShowSignal" };
        waiter.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }
}

public partial class App
{
    /// <summary>Thu muc du lieu giong MainWindow.ResolveDbPath (portable.flag -> canh exe).</summary>
    public static string DataDir => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"))
        ? AppContext.BaseDirectory
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FolderSync");

    private static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(Path.Combine(DataDir, "crash.log"),
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] RAM {Environment.WorkingSet / 1048576} MB{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch (Exception) { /* dang crash - khong de loi ghi log che loi that */ }
    }
}

public enum StartMode { LastUsed, Tray, Mini }
