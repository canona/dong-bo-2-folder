# AGENTS.md — Tóm tắt dự án FolderSync cho AI agent

File này dành cho **mọi AI agent** (Claude, Codex, Cursor, Copilot, Gemini…) và lập trình viên mới. Đọc xong file này là đủ để bắt đầu làm việc.
- Quy tắc làm việc chi tiết, lệnh, bẫy kỹ thuật: [CLAUDE.md](CLAUDE.md)
- Lịch sử từng đợt phát triển, lỗi đã sửa và lý do: [docs/NHAT-KY-LAP-TRINH.md](docs/NHAT-KY-LAP-TRINH.md)
- Hướng dẫn cho người dùng cuối: [docs/Huong-dan-cai-dat-va-chay.md](docs/Huong-dan-cai-dat-va-chay.md)

---

## 1. Dự án là gì

**FolderSync** — phần mềm desktop Windows đồng bộ dữ liệu giữa **2 folder** (cùng máy hoặc ổ USB), giao diện và cách làm việc mô phỏng **GoodSync**. Dùng nội bộ/cá nhân: không tài khoản, không server.

| Hạng mục | Giá trị |
|---|---|
| Nền tảng | Windows 10 64-bit trở lên |
| Ngôn ngữ / framework | C# — .NET 8 (`net8.0` cho Core, `net8.0-windows` + WPF cho App) |
| Lưu trữ | SQLite (`Microsoft.Data.Sqlite` 8.0.10), 1 file `state.db` |
| Đóng gói | Bản portable (zip) + Inno Setup 6 (`packaging/`) |
| Test | Harness tự viết (console app, **không phải xUnit**) |
| Repo | https://github.com/canona/dong-bo-2-folder — nhánh chính `main` |
| Bản quyền | © 2026 **VTC Digital** — phát triển tháng 10/2026 bởi **Canona**. Khai báo 1 nơi: `Directory.Build.props` (hiện trong Properties của .exe, hộp Giới thiệu, thanh trạng thái, bộ cài) |

### Ba chế độ đồng bộ (mỗi Job chọn 1)

| Chế độ | Hành vi |
|---|---|
| **Backup 1 chiều** | Trái → Phải. Thêm/sửa ở nguồn thì chép sang đích. **Không xóa ở đích** (trừ khi bật "Lan truyền xóa"). File ở đích bị xóa/sửa thì chép lại từ nguồn. |
| **Mirror 1 chiều** | Phải luôn **giống hệt** Trái: file chỉ có ở Phải bị xóa, file ở Phải bị sửa bị ghi đè lại. |
| **Đồng bộ 2 chiều** | Thay đổi ở bên nào cũng chép sang bên kia (kể cả xóa); xung đột (cùng sửa 2 bên) xử lý theo chính sách: NewerWins / LeftWins / RightWins / KeepBoth / Manual. |

### Tính năng chính hiện có
- **Phân tích trước** (xem danh sách hành động, không động vào file) → **Đồng bộ**. Folder lớn: 40.000 file phân tích ~1 giây (chỉ hash file cùng size khác giờ sửa, cache hash lưu DB, danh sách ảo hóa); có tiến trình "Đang quét / Đang so nội dung" và nút **Hủy**.
- So sánh **3 chiều** (state lần trước – Trái – Phải), nhận diện **đổi tên/di chuyển**, đổi tên chỉ khác hoa/thường.
- **Đồng bộ liên tục**: mỗi Job tự chạy khi có file thay đổi (FileSystemWatcher), chạy **định kỳ**, chạy khi mở app. **Các Job chạy độc lập, song song**.
- **Tiến trình**: % theo dung lượng, tốc độ tức thời + trung bình, thời gian còn lại, % từng file.
- **Thanh dung lượng ổ** trên mỗi thẻ folder; cảnh báo đỏ khi ổ đích không đủ chỗ.
- **Cảnh báo lỗi bằng màu**: dòng lỗi đỏ, chip "Loi (n)", panel tiến trình đỏ, Job lỗi có ⚠, log lỗi đỏ.
- **Lịch sử / khôi phục**: file bị xóa/ghi đè được giữ trong `.foldersync_history/`, khôi phục **nhiều file** cùng lúc, có trạng thái "đã khôi phục / chưa".
- **Giới hạn tốc độ chép (MB/s) cho từng Job** (thanh kéo ở tab Nâng cao, 0 = không giới hạn; panel tiến trình hiện "giới hạn x MB/s").
- **Job Options 7 tab** (kiểu GoodSync): Chung, Bộ lọc (glob Include/Exclude), Tự động, Scripts (cmd trước/sau), Nâng cao, Phía Trái/Phải.
- **3 chế độ chạy**: Đầy đủ / **Mini** (cửa sổ nhỏ góc màn hình) / **Khay hệ thống** (icon + menu + thông báo; bấm X = ẩn xuống khay). **Chạy cùng Windows** (khởi động ẩn ở khay). Chỉ 1 phiên bản app chạy cùng lúc.
- **Bộ cài đặt** `FolderSync-Setup-x.y.z.exe` (Inno Setup, kèm .NET runtime + SQLite, cài không cần admin, ô chọn chạy cùng Windows) + bản portable zip.
- Thao tác nhanh: bấm thẻ folder để đổi folder; bấm vùng giữa để đổi chế độ; chuột phải Job (Phân tích / Đồng bộ / Bật-tắt liên tục / Tùy chọn); nhấp đúp Job mở Tùy chọn.

---

## 2. Bắt đầu nhanh

```powershell
# Test engine - Windows hien tai: TONG: 304 PASS, 2 FAIL (H1b, H1c = loi P0 chua sua, FAIL co chu y); Linux them 1 FAIL watcher da biet (tu don thu muc tam khi chay xong)
dotnet run --project tests\FolderSync.Core.Harness

# Build + chay app
dotnet build src\FolderSync.App\FolderSync.App.csproj
dotnet run   --project src\FolderSync.App\FolderSync.App.csproj
```

- Máy chỉ có .NET SDK 8 **không mở được `FolderSync.slnx`** (cần SDK ≥ 9.0.200) → build từng `.csproj`.
- App đang chạy thì build App lỗi khóa file → đóng app trước (`taskkill /IM FolderSync.App.exe /F`). **Lưu ý**: người dùng có thể đang dùng app với Job thật đang bật liên tục — báo trước khi đóng/mở.
- Dữ liệu app: `%AppData%\FolderSync\state.db`; có file `portable.flag` cạnh exe thì dùng `state.db` cạnh exe (dùng cách này để thử nghiệm **không đụng Job thật** của người dùng, xong phải xóa `portable.flag` + `state.db`).

---

## 3. Kiến trúc

```
src/FolderSync.Core/   (KHONG tham chieu WPF - toan bo logic, test duoc)
  Models/SyncModels.cs         SyncMode, ConflictPolicy, FileEntry(path,size,mtime,hash), SyncAction(Kind)
  Models/JobOptions.cs         Tuy chon Job -> JSON trong jobs.settings_json; Legacy() cho Job cu
  Engine/FolderScanner.cs      Quet de quy (1 lan liet ke/thu muc, chi metadata), FailedPaths, RootMissing, tien trinh, huy
  Engine/BandwidthThrottle.cs  Gioi han toc do chep (token bucket, huy duoc)
  Engine/HashPlanner.cs        Chon file can hash (cung size khac mtime / ung vien doi ten)
  Engine/SyncFilter.cs         Loc mac dinh + glob; IsIncluded (file) / IsDirectoryIncluded (thu muc)
  Engine/ThreeWayAnalyzer.cs   So sanh 3 chieu -> danh sach SyncAction (CHI phan tich)
  Engine/RenameDetector.cs     Ghep Delete + Copy cung noi dung thanh Move
  Engine/ConflictResolver.cs   Bien Conflict thanh hanh dong theo chinh sach
  Engine/SyncExecutor.cs       Thuc thi: kiem cho trong, copy nguyen tu, history, chot xoa, bao tien trinh
  Engine/SyncSession.cs        1 lan chay: AnalyzeOnly() / Run(progress); ScanCache
  Engine/JobRunner.cs          1 Job DOC LAP: khoa rieng, watcher lien tuc, dinh ky, scripts, don history
  Engine/HistoryService.cs     Liet ke / trang thai / khoi phuc nhieu file / don history
  Storage/SqliteStateStore.cs  jobs, file_state, sync_runs; migration; lock da luong
src/FolderSync.App/    WPF
  MainWindow                   Danh sach Job (JobListItem) + JobView rieng tung Job, the folder, cay so sanh, tien trinh, log
  JobOptionsWindow             Hop Tuy chon Job 7 tab (them / sua Job)
  HistoryWindow                Lich su + khoi phuc nhieu file
  MiniWindow                   Che do Mini
  TrayIconService / AppSettings  Icon khay, cai dat chung, chay cung Windows (HKCU Run, tham so --tray)
  App.xaml.cs                  1 phien ban (mutex), tham so --tray / --mini
  AppInfo / AboutWindow        Phien ban + ban quyen doc tu metadata assembly; hop Gioi thieu
tests/FolderSync.Core.Harness/ Program.cs (P0) + P1Tests, P2Tests, JobRunnerTests, SafetyTests, OptionsTests
Directory.Build.props          Product / Company / Copyright / Version chung cho moi project
packaging/                     build-release.ps1 (publish self-contained + zip portable + goi ISCC), FolderSync.iss
```

### Luồng 1 lần đồng bộ

```
JobRunner.RunAsync(trigger, progress)          (thu cong / lien tuc / dinh ky / khi mo app - chung 1 duong)
 └─ SyncSession.Run
     1. EnsureRootsForFirstRun   (chi khi bat "Tao folder neu thieu" VA Job chua co state)
     2. Analyze: Scan Trai + Phai (cache hash) -> LoadState -> ThreeWayAnalyzer -> RenameDetector
                 -> ConflictResolver -> SkipUnreadable -> neu goc mat + co lenh xoa/doi ten: HUY
     3. Bao Planned (kem tong byte) -> SyncExecutor.Execute (chot % xoa truoc; tung file: kiem cho trong,
        ghi .tmp-sync, kiem hash, rename; xoa/ghi de -> .foldersync_history)
     4. Quet lai -> ComputeSyncedState (chi file 2 phia giong nhau) -> SaveState; SaveRun
 └─ Finished -> MainWindow.OnRunFinished (cap nhat JobView, cay, tien trinh, danh sach Job, log)
```

### Mô hình đa Job
- UI giữ `Dictionary<jobId, JobRunner>`. Sửa Job (cấu hình/chế độ/bật-tắt liên tục) = **thay runner mới** (`InheritHistory` giữ "lần chạy cuối"); xóa Job = Dispose runner.
- Khóa chạy là `SemaphoreSlim` **static theo Id Job** → Job khác nhau chạy song song, cùng Job không bao giờ chạy chồng.
- Mỗi Job có `JobView` riêng (bảng + tiến trình) → chuyển Job không mất kết quả; Job chạy nền vẫn cập nhật dữ liệu của nó.
- Sự kiện runner bắn từ **thread nền** → UI dùng `Dispatcher.BeginInvoke`; handler phải kiểm `IsCurrent(runner)` (bỏ qua runner đã bị thay).

---

## 4. Nguyên tắc an toàn — KHÔNG được phá

Mỗi điều từng là **lỗi mất dữ liệu thật** và đều có test trong harness. Chi tiết: [CLAUDE.md](CLAUDE.md).

1. **Không đoán "đã xóa" từ dữ liệu thiếu**: file/thư mục quét lỗi → hành động thành Skip, giữ state cũ; thư mục gốc không tồn tại + có lệnh xóa → hủy cả lần chạy (kể cả Force).
2. **Không tự tạo folder gốc khi Job đã có state** (USB bị rút ≠ người dùng xóa hết). Analyze không bao giờ ghi gì.
3. **Chốt xóa hàng loạt** kiểm trước khi đụng file nào (mặc định > 50% hoặc > 1000 file → hủy).
4. **Copy nguyên tử**: kiểm chỗ trống → `.tmp-sync` → kiểm hash → rename. **Lỗi ở bất kỳ bước nào phải xóa file tạm.**
5. **Không mất bản cũ**: xóa/ghi đè → `.foldersync_history/<thời gian>/`; thư mục này không bao giờ được sync.
6. **State không nói dối**: tính từ kết quả quét lại, chỉ gồm file 2 phía đã giống nhau.
7. **Xung đột Xóa/Sửa luôn giữ bản đã sửa.**
8. **Job cũ giữ hành vi cũ** khi thêm tùy chọn mới (`JobOptions.Legacy()` + migration).
9. **Đổi folder Trái/Phải = xóa state cũ** (`UpsertJob` tự làm).
10. **Một Job không chạy 2 phiên chồng nhau**; các Job khác nhau chạy song song.
11. **Chế độ liên tục không tự kích hoạt bằng file chính nó ghi** (nhận diện bằng ảnh chụp size + mtime), nhưng không được bỏ sót thay đổi thật.
12. **Chỉ một phiên bản app** (mutex `FolderSyncSingleInstance`, trùng `AppMutex` của bộ cài) — 2 tiến trình = 2 runner cho cùng 1 Job.

---

## 5. Quy ước bắt buộc

- **Comment trong code và chữ trên UI: tiếng Việt KHÔNG dấu** (vd `"Dong bo hoan tat."`). Tài liệu `.md`: tiếng Việt **có dấu**.
- Thụt lề 4 dấu cách. Line ending đang lẫn lộn CRLF/LF → giữ kiểu của file đang sửa.
- Comment giải thích **vì sao** (ràng buộc, lỗi từng gặp), không mô tả lại code.
- Tham số mới cho engine: tham số tùy chọn có mặc định an toàn trong `SyncSession`/`SyncOptions`, map từ `JobOptions` trong `JobRunner.CreateSession`.
- Handler WPF `*Changed`/`Checked` phải có chốt (`IsLoaded` hoặc kiểm null) — giá trị khởi tạo trong XAML làm handler chạy trong `InitializeComponent()` (từng làm app crash khi bấm Thêm Job).
- `SqliteStateStore`: mọi method public bọc `lock (_sync)`.
- Thông báo lỗi I/O cho người dùng đi qua `SyncExecutor.FriendlyError`.

### Quy trình sửa lỗi / thêm tính năng
1. `git fetch` + `git pull --ff-only` (nhiều người cùng push lên `main`).
2. Làm trên nhánh riêng (`fix/...`, `feat/...`).
3. Lỗi: viết **repro/test trước**, xác nhận test **FAIL trên code cũ**, rồi mới sửa cho PASS. Test phải đi qua đường thật (`SyncSession.Run`, `FolderScanner`, `JobRunner` với thư mục tạm qua `NewTempDir`).
4. Chạy harness + build App; với UI: chạy thử bằng DB portable riêng, chụp **riêng cửa sổ app** (PrintWindow), không chụp toàn màn hình.
5. Cập nhật số test trong `CLAUDE.md`, `README.md`, `docs/Huong-dan-cai-dat-va-chay.md`; thêm 1 mục vào `docs/NHAT-KY-LAP-TRINH.md`.
6. **Chỉ commit / push / merge khi người dùng yêu cầu.** Commit message tiếng Việt không dấu.

---

## 6. Trạng thái hiện tại (15/10/2026)

- `main` trên GitHub (22/10/2026): đã gộp bộ test khó PR #3 / #5 / #10 / #11 / #9 + sửa race Hủy; phiên bản **0.3.1** (Release `v0.3.1-internal`). Harness Windows **304 PASS + 2 FAIL** (H1b/H1c — lỗi P0 H1 chưa sửa: mất/hỏng `file_state` hoặc `settings_json` thì Mirror có thể xóa file ở đích). App build 0 lỗi.
- Quy mô: Core ~2.300 dòng, App ~3.000 dòng (riêng `MainWindow.xaml.cs` ~1.200), harness ~1.300 dòng.

### Hạn chế đã biết
- Tắt so hash / cache hash: file bị sửa mà giữ nguyên size + mtime không bị phát hiện.
- 2 Job trỏ vào **cùng folder** có thể chạy song song và giẫm lên nhau (chưa chặn).
- Tự động chỉ chạy khi app đang mở (chưa có chạy nền / service).
- Khôi phục vào folder **đích** của Job 1 chiều sẽ bị lần đồng bộ sau ghi đè lại (UI có cảnh báo).
- Môi trường người dùng: ổ C: và USB H: gần đầy — đã gây lỗi thật trong quá trình phát triển.

### Việc nên làm tiếp (ưu tiên giảm dần)
1. Scripts: quote `%LF%`/`%RF%`, ghi stdout/stderr vào log; nút **Hủy** khi đang đồng bộ (CancellationToken); chặn 2 Job dùng chung folder chạy cùng lúc.
2. GitHub Actions `windows-latest` chạy harness; thêm `.gitattributes`.
3. Tách tiếp `MainWindow.xaml.cs` (ViewModel cho JobView / cây so sánh).
4. Hiệu năng folder lớn: lưu cache hash qua lần mở app, BLAKE3, USN Journal.
5. Chạy nền khi chưa đăng nhập (Windows Service) — hiện app chạy ở khay sau khi user đăng nhập.
