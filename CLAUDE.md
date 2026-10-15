# CLAUDE.md — FolderSync (đồng bộ 2 folder)

Hướng dẫn cho AI agent / lập trình viên tiếp tục dự án. Đọc hết file này trước khi sửa code.
Lịch sử quyết định, lỗi đã sửa và lý do: [docs/NHAT-KY-LAP-TRINH.md](docs/NHAT-KY-LAP-TRINH.md).
Tóm tắt toàn dự án cho mọi AI agent (tính năng, kiến trúc, trạng thái, việc tiếp theo): [AGENTS.md](AGENTS.md).

## Ứng dụng là gì

Phần mềm desktop Windows đồng bộ dữ liệu giữa 2 folder (cùng máy hoặc ổ USB), mô phỏng GoodSync.
Mỗi Job chọn 1 chế độ: **Backup 1 chiều** (Trái→Phải, mặc định không xóa ở đích), **Mirror 1 chiều**
(Phải giống hệt Trái, có xóa), **Sync 2 chiều** (lan truyền thêm/sửa/xóa cả 2 phía, có xử lý xung đột).
Dùng nội bộ/cá nhân: không tài khoản, không server, không cơ chế kích hoạt bản quyền (license key). Bản quyền © 2026 VTC Digital.

Stack: C# / .NET 8 (`net8.0`, `net8.0-windows`), WPF, SQLite (`Microsoft.Data.Sqlite` 8.0.10),
đóng gói Inno Setup 6 + bản portable. Chỉ Windows 10 64-bit trở lên.

## Lệnh thường dùng

```powershell
# Test engine (harness tự viết, KHONG phai xUnit) - dong cuoi phai la: TONG: <n> PASS, 0 FAIL
dotnet run --project tests\FolderSync.Core.Harness

# Build / chay app WPF
dotnet build src\FolderSync.App\FolderSync.App.csproj
dotnet run   --project src\FolderSync.App\FolderSync.App.csproj

# Ban phat hanh -> dist\ : FolderSync-Setup-x.y.z.exe (Inno Setup 6) + FolderSync-portable.zip
# (self-contained single-file: kem .NET runtime + SQLite native). O C: chat thi them -PackagesDir "D:\..."
powershell -ExecutionPolicy Bypass -File .\packaging\build-release.ps1 -IsccPath "D:\Tools\InnoSetup6\ISCC.exe"
```

- `FolderSync.slnx` là định dạng solution mới, cần .NET SDK ≥ 9.0.200. Máy chỉ có SDK 8 thì build từng `.csproj` như trên.
- Đang chạy app thì `dotnet build` App sẽ lỗi khóa file exe → đóng app trước (`taskkill /IM FolderSync.App.exe /F`).
- Build WPF bị ngắt giữa chừng có thể để lại `src/FolderSync.App/FolderSync.App_*_wpftmp.csproj` → xóa, không commit.
- Hiện tại: **156/156 PASS** (20 P0 + 15 P1 + 13 P2 + 26 RUN + 33 SAFE + 30 OPT + 12 PERF + 7 BW). Harness tự dọn thư mục tạm `%TEMP%\foldersync-*` khi chạy xong. Build App 0 error / 0 warning.

## Cấu trúc

```
src/FolderSync.Core/            Engine, KHONG phu thuoc UI - moi logic dong bo nam o day
  Models/SyncModels.cs          SyncMode, ConflictPolicy, FileEntry, SyncAction(Kind)
  Models/JobOptions.cs          Tuy chon Job (7 tab kieu GoodSync), luu JSON; Legacy() cho Job cu
  Engine/FolderScanner.cs       Quet de quy -> FileEntry (+SHA-256), FailedPaths, RootMissing
  Engine/SyncFilter.cs          Loc mac dinh + glob Include/Exclude; IsIncluded (file) / IsDirectoryIncluded (thu muc)
  Engine/ThreeWayAnalyzer.cs    So sanh 3 chieu State-Trai-Phai -> danh sach SyncAction (chi PHAN TICH)
  Engine/RenameDetector.cs      Ghep Delete+Copy cung noi dung thanh Move
  Engine/ConflictResolver.cs    Bien Conflict thanh hanh dong theo ConflictPolicy
  Engine/SyncExecutor.cs        Thuc thi: copy nguyen tu, history, chot an toan xoa, bao tien trinh (SyncProgress)
  Engine/SyncSession.cs         Dieu phoi 1 lan chay: AnalyzeOnly() / Run(progress); ScanCache (cache hash)
  Engine/JobRunner.cs           1 Job doc lap: khoa rieng, dong bo lien tuc (watcher), dinh ky, Scripts, don history
  Engine/HistoryService.cs      Liet ke / trang thai so voi file hien tai / khoi phuc nhieu file / don .foldersync_history
  Engine/FileHasher.cs          SHA-256 (co ban huy duoc + bao so byte)
  Engine/BandwidthThrottle.cs   Gioi han toc do chep (token bucket) cho 1 lan chay cua 1 Job
  Engine/PauseGate.cs           Tam dung / tiep tuc 1 lan chay (dung giua 2 khoi ghi file tam)
  Engine/HashPlanner.cs         Chon file CAN hash (cung size khac mtime; ung vien doi ten) - khong hash moi file
  Storage/SqliteStateStore.cs   Bang jobs (+settings_json), file_state, sync_runs; migration
src/FolderSync.App/             WPF: MainWindow (danh sach Job + JobView rieng tung Job, cay so sanh, tien trinh), JobOptionsWindow, HistoryWindow
  App.xaml.cs                   Khoi dong: 1 phien ban (mutex FolderSyncSingleInstance), tham so --tray / --mini
  MiniWindow                    Che do Mini (dung chung JobListItem voi MainWindow)
  TrayIconService.cs            Icon khay (WinForms NotifyIcon) + menu + thong bao bong bong
  AppSettings.cs                Cai dat chung (app-settings.json canh state.db) + StartupManager (HKCU\...\Run)
  AppInfo.cs, AboutWindow       Phien ban + ban quyen (doc tu metadata assembly) + hop Gioi thieu
  SystemMonitor.cs              Chi so may CPU / Memory / Disk / Network (kieu Task Manager) cho thanh trang thai
tests/FolderSync.Core.Harness/  Program.cs (P0) + P1Tests, P2Tests, JobRunnerTests, SafetyTests, OptionsTests, PerfTests, BandwidthTests
packaging/                      build-release.ps1, FolderSync.iss
Directory.Build.props           Product, Company (VTC Digital), Copyright, Version - ap cho moi project
docs/                           Huong-dan-cai-dat-va-chay.md (nguoi dung), NHAT-KY-LAP-TRINH.md (dev)
```

## Luồng 1 lần đồng bộ (`SyncSession.Run`)

1. `EnsureRootsForFirstRun()` — chỉ khi Job bật "Tạo folder nếu thiếu" **và chưa có state**.
2. `Analyze()`: Scan Trái + Phải (**chỉ metadata**, gắn hash từ cache) → `LoadState` → `HashPlanner.ForComparison` (hash file cùng size khác mtime) → `ThreeWayAnalyzer` → `HashPlanner.ForRenames` + `RenameDetector` (nếu bật)
   → `ConflictResolver` → `SkipUnreadable` → nếu gốc không tồn tại mà có lệnh xóa/đổi tên thì `BlockReason`.
3. Báo `Planned` (kèm `BytesTotal`) → `SyncExecutor.Execute(actions, baseline, progress)`.
4. Không bị hủy: quét lại 2 phía → `ComputeSyncedState` (chỉ file 2 phía giống nhau) + `KeepUnreadableState` → `SaveState`.
5. `SaveRun` ghi nhật ký `sync_runs`.

`AnalyzeOnly()` = bước 2, **tuyệt đối không ghi/tạo/xóa gì** trên đĩa.

## JobRunner — mỗi Job chạy độc lập

- UI giữ `Dictionary<jobId, JobRunner>`; `ReloadJobs()` giữ runner của Job không đổi, thay runner khi Job bị sửa, dispose khi Job bị xóa.
- Mọi lần chạy (thủ công, liên tục, định kỳ, khi mở app) đi qua `JobRunner.RunAsync(trigger, progress)`; kết thúc báo qua sự kiện `Finished` → `MainWindow.OnRunFinished` (một chỗ xử lý chung).
- Khóa chạy là `SemaphoreSlim` **theo Id Job, static** (không theo instance) → Job khác nhau chạy song song; cùng Job không bao giờ chạy chồng, kể cả khi runner bị thay lúc đang chạy.
- Liên tục: watcher **cả 2 folder ở mọi chế độ** (Backup cũng phải chép lại khi file ở đích bị xóa/sửa) → debounce `AutoFileChangeDelaySec` → chạy. Khi bật thì chạy bắt kịp 1 lần. Thay đổi trong lúc chạy → chạy lại sau. Watcher lỗi / folder mất → gỡ, thử gắn lại 15 giây/lần + ngay sau mỗi lần chạy.
- **Bỏ qua file do chính lần chạy ghi** bằng ảnh chụp (có/không, size, mtime) chụp lúc `Completed` — không dùng cửa sổ thời gian (người dùng sửa ngay file vừa chép vẫn được đồng bộ).
- Sửa Job (đổi cấu hình/chế độ/bật-tắt liên tục) = **tạo runner mới** thay runner cũ (`InheritHistory` giữ lần chạy cuối). Runner cũ vẫn bắn sự kiện (vd `Idle` lúc Dispose) **sau** khi danh sách đã vẽ lại → mọi handler phải kiểm `IsCurrent(runner)`, nếu không sẽ ghi đè dòng Job bằng cấu hình cũ (từng làm Job đã tắt liên tục vẫn hiện nhãn LIEN TUC).
- **Runner cũ vẫn đang chạy khi bị thay** (sửa Tùy chọn / bật-tắt Liên tục lúc Job đang chép): `JobRunner.Active` (static, theo Id) giữ runner đang giữ khóa; `State`/`IsBusy`/`IsPaused`/`Cancel()`/`Pause()`/`Resume()` của runner MỚI đều chuyển tới lần chạy đó. Trước đây runner mới báo rảnh → UI ẩn nút Hủy + thanh tiến trình, Hủy không có tác dụng. Lần chạy tự động gặp khóa bận mà Job không tự đang chạy (đang Phân tích hoặc runner cũ giữ khóa) → thử lại sau 3 giây (`RetryWhenBusy`), không bỏ sót thay đổi file.
- **Tạm dừng** (`PauseGate`): chỉ dừng ở điểm an toàn — trước mỗi hành động và giữa 2 khối ghi `.tmp-sync` (cùng chỗ với throttle); file đích không bao giờ ghi dở. Hủy khi đang tạm dừng thoát ngay + xóa file tạm. Chỉ tạm dừng được pha CHÉP (pha quét/phân tích thì dùng Hủy). Kết thúc lần chạy luôn `Resume()` để lần sau không bị kẹt.
- Sự kiện của runner bắn từ **thread nền** → UI luôn `Dispatcher.BeginInvoke`. **Tiến trình (`AutoProgress`) KHÔNG dùng `Progress<T>`**: nó post theo mức ưu tiên của ngữ cảnh lúc TẠO (đo được: mở app = `Send`, menu chuột phải/đổi chế độ = `Render`) → báo cáo có thể bị xử lý SAU `Finished`/continuation → thanh "Đang quét" hiện lại và kẹt. Dùng `UiProgress` + `BeginInvoke(Normal)` (cùng mức với `StateChanged`/`Finished`), và bỏ qua báo cáo quét/hash khi Job không còn bận. Tiến trình vào `JobView` riêng từng Job; chỉ vẽ ra màn hình khi Job đó đang được chọn.

## Bất biến an toàn — KHÔNG được phá

Mỗi điều dưới đây từng là lỗi thật (làm mất dữ liệu) và đều có test trong harness:

1. **Không đoán "đã xóa" từ dữ liệu thiếu.** File/thư mục quét lỗi (`FailedPaths`) → mọi hành động chạm vào nó thành `Skip`, và giữ nguyên entry state cũ. Gốc không tồn tại (`RootMissing`) + có lệnh xóa/đổi tên → hủy cả lần chạy, **kể cả khi `Force`**.
2. **Không tự tạo folder gốc khi Job đã có state.** Tạo folder rỗng thay cho ổ/USB bị rút = biến "mất kết nối" thành "người dùng xóa hết". Analyze không bao giờ tạo folder.
3. **Chốt xóa hàng loạt** (`MaxDeletePercent`, `MaxDeleteAbsolute`) kiểm tra TRƯỚC khi đụng file nào. Chưa có state thì mốc % = tổng số file 2 phía.
4. **Copy nguyên tử**: kiểm chỗ trống ổ đích trước (`FreeSpaceProbe`, chừa 1 MB) → ghi `*.tmp-sync` → kiểm size + hash → nguồn không đổi trong lúc chép (size + mtime) → rename đè. Không bao giờ ghi thẳng lên file đích. **Lỗi ở bất kỳ bước nào phải xóa file tạm** — scanner bỏ qua `*.tmp-sync` nên file tạm sót lại chiếm chỗ vĩnh viễn (từng để lại 5 file trên USB đã đầy của người dùng).
5. **Không mất bản cũ**: xóa/ghi đè (chế độ `MoveToHistory`, mặc định) đưa bản cũ vào `.foldersync_history/<yyyyMMdd-HHmmss-fff>/`. Thư mục này luôn bị loại khỏi quét và không bao giờ được sync.
6. **State không nói dối**: state mới tính từ kết quả QUÉT LẠI sau khi chạy, chỉ gồm file 2 phía đã giống nhau (size + hash, hoặc size + mtime trong dung sai nếu tắt hash). Lần chạy bị hủy không lưu state.
7. **Xung đột Xóa/Sửa luôn giữ bản đã sửa**, với mọi chính sách. Engine mặc định `ConflictPolicy.Manual`.
8. **Job cũ giữ hành vi cũ**: thêm tùy chọn mới có mặc định khác thì phải bổ sung `JobOptions.Legacy()` + migration, không để Job có sẵn đổi hành vi âm thầm (ví dụ tự xóa history).
9. **Đổi folder Trái/Phải của Job = xóa state cũ** (`SqliteStateStore.UpsertJob` tự làm, trả `true`). State là ảnh chụp của cặp folder cũ; giữ lại sẽ lan truyền xóa sai.
10. **Một Job không bao giờ chạy 2 phiên chồng nhau** (khóa theo Id trong `JobRunner`; `RunAsync` trả `null` khi đang bận). Các Job khác nhau thì chạy song song.
11. **Chế độ liên tục không tự kích hoạt bằng file chính nó ghi**, nhưng cũng không được bỏ sót thay đổi thật xảy ra ngay sau / trong lúc chạy (nhận diện bằng ảnh chụp file, chạy lại khi bẩn).
12. **Chỉ một phiên bản app chạy** (mutex `FolderSyncSingleInstance` trong `App.xaml.cs`, trùng `AppMutex` của bộ cài): 2 tiến trình cùng mở 1 `state.db` = 2 runner cho cùng 1 Job, chạy chồng nhau.

## Quy ước code

- Thụt lề 4 dấu cách, không tab. Line ending đang lẫn lộn (đa số file CRLF, vài file LF; chưa có `.gitattributes`) — giữ nguyên kiểu của file đang sửa, đừng chuẩn hóa hàng loạt nếu không được yêu cầu.
- **Comment trong code và chuỗi hiển thị trên UI: tiếng Việt KHÔNG dấu** (ví dụ `"Dong bo hoan tat."`). Tài liệu `.md`: tiếng Việt có dấu.
- Comment giải thích **vì sao** (ràng buộc an toàn, lỗi từng gặp), không mô tả lại code.
- Core không được tham chiếu WPF. Tham số mới cho engine: thêm tham số tùy chọn có mặc định an toàn vào `SyncSession` / `SyncOptions`, map từ `JobOptions` trong `MainWindow.CreateSessionFor`.
- UI cập nhật tiến trình qua `IProgress<SyncProgress>` (`Progress<T>` tự về UI thread). Executor giới hạn báo byte ~10 lần/giây.
- Glyph icon dùng font `Segoe MDL2 Assets`, viết dạng escape `""` (không dán ký tự thô).

## Cách viết test

- Harness tự viết: mỗi file test có `Check(name, ok, detail)` và trả `(pass, fail)`; đăng ký trong `Program.cs`.
- Test **đi qua đường thật** (`FolderScanner`, `SyncSession.Run` trên thư mục tạm), không chỉ gọi hàm lẻ — lỗi Include chặn thư mục con từng lọt vì test chỉ gọi `SyncFilter` trực tiếp.
- Sửa lỗi: viết repro/test trước, xác nhận **FAIL trên code cũ** (`git stash push -- src`), rồi mới sửa cho PASS.
- Test cần `IProgress` đồng bộ thì dùng lớp tự viết (xem `CollectProgress` trong `SafetyTests`), không dùng `Progress<T>`.
- Ca có lệnh xóa: nhớ chốt 50% — thêm file đệm để 1 lệnh xóa không bị hủy nhầm.

## Bẫy đã biết

- Tắt `CompareChecksums`: file bị sửa mà giữ nguyên size + mtime sẽ không được phát hiện (giới hạn bản chất, UI đã cảnh báo). Nhận diện đổi tên cũng tắt theo.
- So sánh đường dẫn không phân biệt hoa/thường (Windows). Đổi tên chỉ khác hoa/thường chỉ đồng bộ **phần tên file**, không đồng bộ hoa/thường của thư mục (tránh lặp Move vô hạn).
- **Cây so sánh là `ListBox` danh sách PHẲNG có ảo hóa** (`CompareList`, `VirtualizingPanel.IsVirtualizing` + `Recycling`), không phải `TreeView`: TreeView tạo phần tử cho MỌI file → 40.000 file treo > 10 phút, 4 GB RAM, crash. Thu/mở thư mục = chèn/gỡ dòng con trong `BulkObservableCollection` (`OnNodeExpandedChanged`). Không bọc `ListBox` trong `StackPanel`/`ScrollViewer` (mất ảo hóa). Độ rộng cột cố định phải **khớp giữa header và `DataTemplate` của `CompareNode`** trong `MainWindow.xaml`. Mặc định chip **"Thay doi"**.
- **Sự kiện bắn trong lúc nạp XAML**: control có giá trị khởi tạo trong XAML (`IsSelected="True"`, `IsChecked="True"`…) gọi handler ngay trong `InitializeComponent()`, khi các control khai báo phía dưới còn null → crash. Mọi handler `*Changed`/`Checked` phải có chốt (`if (IsLoaded)` hoặc kiểm null) — từng làm app thoát mỗi lần bấm Thêm Job.
- App crash không hiện lỗi: xem stack trace trong Windows Event Log → Application → nguồn `.NET Runtime`.
- `SqliteStateStore` dùng 1 connection cho mọi Job → mọi method public bọc `lock (_sync)`. Thêm method mới cũng phải bọc.
- **Không hash mọi file** (`HashPlanner`): khác size → khác; cùng size + mtime bằng nhau → coi là giống (KHÔNG đọc nội dung); cùng size khác mtime → hash 2 phía. "mtime bằng nhau" = **khớp tuyệt đối nếu cả 2 ổ NTFS/ReFS**, trong dung sai 2 giây nếu có ổ FAT/exFAT/không xác định (`SyncSession.HashTolerance`) — dùng 2 giây cho mọi ổ từng bỏ sót 30.000 file khác nội dung tạo cách nhau < 2 giây. Giới hạn: file bị sửa mà giữ nguyên size + mtime không bị phát hiện → tùy chọn Job `FullContentCompare` (tab Nâng cao) hash toàn bộ mỗi lần.
- Cache hash (`ScanCache`) **lưu DB** bảng `hash_cache` (theo Job + phía, ghi chênh lệch), nạp lần đầu `JobRunner` tạo session; hash tính lúc chép (`SyncResult.KnownHashes`) cũng vào cache → state luôn có hash. Đổi folder / xóa Job → xóa cache. Phía nào gốc mất (USB rút) thì **giữ** cache cũ.
- **Giới hạn tốc độ chép** (`JobOptions.MaxCopySpeedMBps`, 0 = không giới hạn → `SyncOptions.MaxBytesPerSecond`): `BandwidthThrottle` tạo 1 lần cho mỗi `SyncExecutor.Execute` (tính chung cả lần chạy), chỉ chèn thời gian chờ trong vòng ghi `.tmp-sync` (`Consume` sau mỗi khối); chờ bằng `ct.WaitHandle.WaitOne` để Hủy dừng ngay; "chạy bù" tối đa 1 giây sau khi đứng yên. KHÔNG giới hạn bước kiểm hash file tạm và hash lúc phân tích. Đổi giới hạn khi đang chạy chỉ áp từ lần chạy sau.
- Đo hiệu năng: đặt biến môi trường `FOLDERSYNC_DIAG=<file log>` → `Diag` ghi thời gian từng bước Phân tích (quét/hash, tạo bảng, layout). Crash không bắt được → `crash.log` cạnh `state.db`. **Đừng đo bằng UI Automation duyệt cây** (`FindFirst(Descendants)` trên danh sách lớn) — chính nó tạo automation peer cho mọi dòng, gây nhiễu/treo.
- `SqliteStateStore.Dispose` lấy cùng khóa và sau đó mọi lệnh ném `ObjectDisposedException`: đóng ngang 1 transaction của Job chạy nền từng làm sập tiến trình.
- Harness tạo dữ liệu thật trong `%TEMP%`: từng làm **đầy ổ C:** (SQLite báo `database or disk is full`) trước khi có bước tự dọn. Test mới phải dùng `NewTempDir` (tiền tố `foldersync-`) để được dọn.
- Thông báo lỗi I/O hiển thị cho người dùng đi qua `SyncExecutor.FriendlyError` (hết chỗ, file bị khóa, USB bị rút, thiếu quyền) — thêm loại lỗi mới thì bổ sung ở đó.
- 2 Job trỏ vào **cùng folder** có thể chạy song song và giẫm lên nhau — chưa chặn.
- Icon `.ico` là nhị phân → repo chỉ lưu `Assets/foldersync.ico.b64`; `build-release.ps1` sinh lại `.ico`. Cửa sổ dùng PNG nhúng (`IconHelper`).
- **Chế độ chạy**: `ShutdownMode=OnExplicitShutdown`; bấm X = ẩn xuống khay (`AppSettings.CloseToTray`), thoát thật chỉ qua `MainWindow.ExitApp()`. Khởi tạo Job ở `InitOnce()` gọi từ `Start()` — **không chỉ dựa vào `Window_Loaded`** vì khởi động `--tray` thì cửa sổ không bao giờ hiện, Loaded không bắn, Job liên tục sẽ không chạy.
- **Một phiên bản duy nhất** là bất biến an toàn: 2 tiến trình cùng mở 1 `state.db` = 2 runner cho cùng 1 Job → chạy chồng. Tên mutex `FolderSyncSingleInstance` dùng chung với `AppMutex` trong `packaging/FolderSync.iss`.
- **Bản quyền / phiên bản**: © 2026 VTC Digital, phát triển 10/2026 bởi Canona. Nguồn duy nhất cho code là `Directory.Build.props` (`AppInfo` đọc lại từ assembly — không ghi cứng chuỗi trong UI). Đổi phiên bản phải sửa **cả** `Version` ở đó **và** `MyAppVersion` trong `packaging/FolderSync.iss`.
- **Chạy cùng Windows**: giá trị `FolderSync` trong `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` = `"<exe>" --tray`. App (`StartupManager`) và bộ cài (`[Registry]` task `autostart`) ghi **cùng tên + cùng tham số**; gỡ cài đặt luôn xóa giá trị này. Đổi tên/tham số phải sửa cả 2 nơi.
- **Chỉ số máy trên thanh trạng thái** (`SystemMonitor`): chỉ dùng Win32 (`GetSystemTimes`, `GlobalMemoryStatusEx`, PDH) + `NetworkInterface`, không thêm gói NuGet. Disk = 100 − `% Idle Time` của ổ vật lý BẬN NHẤT (bỏ `_Total` vì là trung bình); phải dùng `PdhAddEnglishCounter` — tên counter bị dịch theo ngôn ngữ Windows. Network = (nhận + gửi) / tốc độ liên kết của card bận nhất. Đo 1 giây/lần trên thread nền (~50 ms/lần), **không đo khi cửa sổ ẩn/thu nhỏ**.
- WinForms bật chỉ để dùng `NotifyIcon`; csproj có `<Using Remove="System.Windows.Forms" />` + `System.Drawing` để không trùng tên với WPF. Trong code dùng alias (`Forms.`, `Drawing.`).
- UI Automation: `MenuItem` phải gọi `InvokePattern` (không phải `TogglePattern` — Toggle chỉ đổi dấu tích, không bắn `Click`).
- App chế độ portable: có file `portable.flag` cạnh exe → `state.db` nằm cạnh exe; không có → `%AppData%\FolderSync\state.db`. Đừng để quên `portable.flag` trong `bin\Debug` sau khi thử nghiệm.

## Quy trình Git

- Remote: https://github.com/canona/dong-bo-2-folder, nhánh chính `main`. Nhiều người cùng push — luôn `git fetch` / `git pull --ff-only` trước khi làm.
- Làm trên nhánh riêng (`fix/...`, `feat/...`), chạy harness + build App trước khi commit; cập nhật số test trong `README.md` và `docs/Huong-dan-cai-dat-va-chay.md`.
- Commit message tiếng Việt không dấu, dòng đầu tóm tắt, các dòng sau liệt kê thay đổi.
- Chỉ commit / push / merge khi người dùng yêu cầu.
- Mỗi đợt làm việc: thêm 1 mục vào [docs/NHAT-KY-LAP-TRINH.md](docs/NHAT-KY-LAP-TRINH.md).

## Việc còn lại (ưu tiên giảm dần)

1. **Giai đoạn 2 — còn lại**: Scripts: quote `%LF%`/`%RF%`, ghi stdout/stderr vào log; nút **Hủy** (CancellationToken); chặn 2 Job dùng chung folder chạy cùng lúc; GitHub Actions `windows-latest` chạy harness; `.gitattributes`. (Đã xong: `JobRunner` trong Core, watcher bỏ qua file tự ghi / `.tmp-sync`, xử lý `Error`, gắn lại khi cắm lại ổ.)
2. **Hiệu năng**: (đã xong: hash theo nhu cầu, cache hash lưu DB, danh sách ảo hóa, quét 1 lần liệt kê/thư mục, RenameDetector O(n)) — còn: BLAKE3, USN Journal cho folder hàng triệu file.
3. **Dùng hằng ngày**: chạy nền (tray → Windows Service), chạy thử 1–2 tuần với dữ liệu + USB thật.
4. Cân nhắc giới hạn ngưỡng chốt xóa tối đa (ví dụ 90%) khi không bật Force — chờ người dùng quyết.
