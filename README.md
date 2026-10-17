# FolderSync — Phần mềm desktop đồng bộ dữ liệu giữa 2 folder

> **Bản quyền © 2026 VTC Digital.** Phát triển tháng 10/2026 bởi Canona. All rights reserved.

> Kế hoạch đầy đủ: `~/workspace/your_files/Ke-hoach-phat-trien-phan-mem-dong-bo-2-folder.md`

## Phạm vi đã khóa (14/10/2026)

| Hạng mục | Chốt |
|---|---|
| Kịch bản | A — 2 folder cùng 1 máy / ổ USB |
| Kiểu sync | Cả 3 chế độ, chọn theo Job: Backup 1 chiều, Mirror 1 chiều, Sync 2 chiều |
| Hệ điều hành | Chỉ Windows 10 64-bit trở lên |
| Mục đích | Nội bộ / cá nhân — không bản quyền bán, không tài khoản, không server |
| Stack | C# .NET 8 LTS, WPF, SQLite, Inno Setup + bản portable |

## Cấu trúc solution (khung P0)

```
FolderSync.slnx        (dinh dang solution moi cua .NET SDK 10; VS 2022 17.10+ mo duoc)
src/
  FolderSync.Core/     Engine doc lap UI: Scanner, so sanh 3 chieu, nhan dien doi ten,
                       Executor copy nguyen tu, SyncSession, SQLite state store
  FolderSync.App/      WPF UI skeleton (Windows) - noi day vao SyncSession o P2
tests/
  FolderSync.Core.Harness/  Harness kiem chung (chay: dotnet run --project tests/FolderSync.Core.Harness)
docs/
```

## Trạng thái

- P0 ✅ (14/10/2026): khung solution + lõi `ThreeWayAnalyzer` (bảng quyết định 3 chiều cho cả 3 chế độ) + `SyncFilter`.
  - Build: Core và WPF App đều **0 errors / 0 warnings** (SDK .NET 10.0.401 tại `~/workspace/tools/dotnet`, target net8.0 / net8.0-windows, WPF build kiểm chứng nhờ EnableWindowsTargeting).
  - Harness: **20/20 PASS** (16 ca bảng quyết định 2 chiều/Mirror/Backup + dung sai FAT32 + 4 ca filter). Chạy harness trên VM này cần `DOTNET_ROLL_FORWARD=LatestMajor` vì VM chỉ có runtime .NET 10; trên máy Windows có .NET 8 runtime thì chạy thẳng.
  - Git: đã init + commit đầu tiên.
- P1 ✅ (14/10/2026): đủ 4 hạng mục —
  - **FolderScanner:** quét đệ quy, áp SyncFilter, không đi theo symlink/junction, lỗi truy cập từng file không làm sập lần quét, tùy chọn tính hash SHA-256.
  - **SqliteStateStore** (Microsoft.Data.Sqlite): bảng `jobs`, `file_state` (snapshot thay thế toàn bộ trong 1 transaction), `sync_runs`.
  - **RenameDetector:** ghép cặp Delete+Copy có cùng size+hash thành Move (chỉ khi khớp 1-1 duy nhất, thiếu hash thì không kết luận).
  - **SyncExecutor:** copy nguyên tử (file tạm .tmp-sync → kiểm size+hash → rename đè, giữ mtime nguồn), xóa/ghi đè mặc định dời vào `.foldersync_history/<timestamp>/`, chốt an toàn hủy toàn bộ khi số lệnh xóa vượt 50% số file trong state hoặc 1000 file (trừ khi Force), dọn thư mục rỗng.
  - **SyncSession:** điều phối Analyze (chỉ xem trước, không đụng file) và Run (Execute → quét lại → tính state mới từ thực tế hội tụ → lưu state + sync_runs; Aborted thì không lưu state).
  - Kiểm chứng: build toàn solution **0 errors / 0 warnings**, harness **35/35 PASS** (20 ca P0 + 15 ca P1 end-to-end trên thư mục thật: mirror/backup/2 chiều/xung đột/đổi tên/chốt an toàn/file 1MB/tên tiếng Việt).
  - Lưu ý kỹ thuật: hash hiện dùng SHA-256 của BCL (đúng vai trò kiểm chứng toàn vẹn); chuyển BLAKE3 để tăng tốc quét là việc tối ưu ở P3. Thư mục history hiện nằm trong chính folder được sync (bị loại khỏi quét nhờ filter).
- P2 ✅ (14/10/2026) — **MVP hoàn chỉnh**:
  - **ConflictResolver:** chính sách tự động cho Job 2 chiều — NewerWins (hoà giờ thì rút về KeepBoth), LeftWins, RightWins, KeepBoth (bản Trái làm chuẩn + bản Phải thành `file (conflict <thời gian>).ext` ở cả 2 phía, hội tụ sạch ở lần chạy sau); xung đột Xóa/Sửa với mọi chính sách đều giữ bản đã sửa. Engine mặc định Manual (chỉ tự quyết khi Job khai báo chính sách), UI đặt mặc định NewerWins cho Job mới. Executor hỗ trợ copy tới đường dẫn đích khác nguồn (TargetPath) để sinh conflict copy.
  - **HistoryService:** liệt kê `.foldersync_history` và khôi phục 1-click; khôi phục ghi đè thì bản hiện tại cũng được sao vào history trước. Đã sửa 1 lỗi thật do harness bắt được: khôi phục trong cùng 1 giây có thể tự ghi đè lên chính bản đang khôi phục — đã sửa bằng thứ tự chụp file tạm trước + stamp history kèm mili giây.
  - **Quản lý Job trong SQLite:** ListJobs/DeleteJob (xóa Job xóa kèm state + nhật ký, không đụng file dữ liệu).
  - **WPF UI đầy đủ:** danh sách Job + Thêm/Sửa/Xoá (hộp thoại chọn folder, chọn chế độ và chính sách xung đột), nút Phân tích (bảng hành động xem trước, không đụng file), nút Đồng bộ có xác nhận, log theo thời gian, cửa sổ Lịch sử/Khôi phục theo từng phía. State: bản cài đặt ở `%AppData%\FolderSync\state.db`, bản portable (có `portable.flag` cạnh .exe) ở ngay thư mục app.
  - **Đóng gói** trong `packaging/`: `build-release.ps1` (publish self-contained single-file win-x64 → bản portable + zip + đầu vào installer) và `FolderSync.iss` (Inno Setup 6). Script chạy trên máy Windows của người dùng — chưa kiểm chứng thực tế trên Windows trong môi trường này.
  - Kiểm chứng: build toàn solution **0 errors / 0 warnings**, harness **48/48 PASS** (20 P0 + 15 P1 + 13 P2).
- Bản vá an toàn + tiến trình (14/10/2026):
  - **Quét lỗi không còn bị coi là "đã xóa":** `FolderScanner` trả `FailedPaths` / `RootMissing`. `SyncSession` hủy toàn bộ khi thư mục gốc không tồn tại mà có lệnh xóa/đổi tên (kể cả `Force`); mọi hành động chạm đường dẫn không đọc được (file bị khóa, thiếu quyền) chuyển thành Skip và giữ nguyên state cũ của chúng. Trước đó Mirror lần đầu với Trái không tồn tại sẽ xóa sạch Phải.
  - **Chốt % lần chạy đầu:** chưa có state thì lấy tổng số file 2 phía làm mốc (trước đây chốt % bị bỏ qua).
  - **Đổi tên chỉ khác hoa/thường** được đồng bộ (chỉ phần tên file, không đổi hoa/thường thư mục).
  - `RenameDetector` chỉ ghép Move khi file phía đích còn đúng nội dung trong state; `SyncFilter.IsIncluded("")` không còn ném lỗi.
  - **Tiến trình đồng bộ:** `SyncSession.Run(IProgress<SyncProgress>)` báo Planned/Started/Completed/Failed kèm đường dẫn đích thật và thời gian; UI có thanh tiến trình, cột Trạng thái / Đích / Thời gian.
  - Kiểm chứng: harness **66/66 PASS** (thêm `SafetyTests.cs`: 13/16 ca an toàn FAIL trên code cũ, PASS trên code mới).
  - Lưu ý môi trường: máy chỉ có .NET SDK 8 không mở được `FolderSync.slnx` (cần SDK ≥ 9.0.200) — build/chạy từng `.csproj`.
- Giao diện phong cách GoodSync + Job Options (14/10/2026):
  - **Nhận diện thương hiệu:** icon app từ SVG (nền navy #0F172A→#1E3A8A, mũi tên vòng cyan #06B6D4, folder A xanh dương / folder B xanh lá + dấu tích) — file nguồn `src/FolderSync.App/Assets/foldersync.svg`; bảng màu navy/cyan/xanh lá áp toàn UI. Lưu ý kỹ thuật: file `.ico` là nhị phân nên repo chỉ lưu bản mã hoá `Assets/foldersync.ico.b64` — cửa sổ app luôn có icon nhờ bản PNG nhúng trong code (`IconHelper`), còn icon cho file .exe được `packaging/build-release.ps1` tự sinh từ `.b64` trước khi publish (chạy `dotnet run` để dev thì không cần .ico).
  - **Màn hình chính:** thanh công cụ navy (Job mới / Phân tích cyan / Đồng bộ xanh lá / Lịch sử), panel Job bên trái, thẻ 2 folder kiểu GoodSync (đường dẫn + định dạng ổ + dung lượng trống/tổng, folder Phải có thêm "Cần chép"), chip lọc Tất cả / Thay đổi / Xung đột / Xóa / Lỗi, và **cây so sánh** theo thư mục với cột L Size / L Date / mũi tên chiều có màu / R Size / R Date / Trạng thái / Lý do (folder hiển thị tổng kích thước + số file cần xử lý).
  - **Job Options 7 tab** (mô hình theo hộp thoại Options của GoodSync): **Chung** (loại Job, lan truyền xóa cho Backup, tạo folder nếu thiếu, tự dọn history theo ngày giữ lại), **Bộ lọc** (mẫu Include/Exclude dạng glob + loại file ẩn/hệ thống — engine `SyncFilter` đã hỗ trợ, Exclude thắng Include, không mở lại được các mục app tự loại), **Tự động** (chính sách xung đột, chạy khi mở app / khi file thay đổi qua FileSystemWatcher có trễ / định kỳ theo phút, ngưỡng chốt an toàn riêng từng Job), **Scripts** (lệnh cmd chạy trước phân tích & sau đồng bộ, hỗ trợ biến %JOBNAME% %RESULT% %CHANGED% %ERRORS% %CONFLICTS% %LF% %RF%), **Nâng cao** (copy creation time / attributes, nhận diện đổi tên, so hash), **Phía Trái / Phía Phải** (dung sai thời gian riêng từng phía + thông tin hệ thống file của ổ). Tùy chọn mở rộng lưu JSON ở cột `jobs.settings_json` (có migration cho DB cũ).
  - **Engine bổ sung:** `JobOptions`, Backup lan truyền xóa (chỉ với file đã có trong state), `HistoryService.Cleanup` theo ngày, Executor copy creation time/attributes theo tùy chọn, `SyncSession` nhận filter/dung sai/cờ hash/cờ đổi tên từ Job.
  - Kiểm chứng: build toàn solution **0 errors / 0 warnings**, harness **86/86 PASS** (thêm `OptionsTests.cs` 20 ca). Giao diện WPF mới vẫn chỉ build-kiểm chứng trên Linux — cần chạy thử trên Windows thật.
- Giai đoạn 1 — sửa lỗi an toàn sau bản Job Options (14/10/2026):
  - **"Tạo folder nếu thiếu" chuyển vào Core** (`SyncSession(createMissingRoots)`): chỉ tạo ở lần chạy đầu (Job chưa có state), 1 chiều chỉ tạo phía đích, Analyze không bao giờ tạo. Trước đó UI tạo folder trước khi chạy → ổ Trái bị rút thì app tạo folder rỗng, qua mặt chốt RootMissing và (với ngưỡng 100%) xóa sạch Phải.
  - **Include không còn chặn thư mục con:** `SyncFilter.IsDirectoryIncluded` chỉ áp thư mục mặc định + Exclude; Include chỉ áp cho file (trước đó Include `*.txt` bỏ sót mọi file trong thư mục con).
  - **Tắt so hash:** 2 file giống hệt (cùng size + mtime trong dung sai) không còn thành xung đột giả; `ComputeSyncedState` so cả mtime khi thiếu hash. Giới hạn còn lại (không sửa được nếu không hash): file bị sửa mà giữ nguyên size và mtime — UI đã cảnh báo ở tab Nâng cao.
  - **Job cũ giữ hành vi cũ:** migration ghi `JobOptions.Legacy()` (không tự dọn history, không copy creation time/thuộc tính) cho Job có sẵn; `FromJson("{}")` cũng trả Legacy.
  - Kiểm chứng: harness **96/96 PASS** (thêm 10 ca trong `OptionsTests.cs`, chạy qua Scanner/Session thật).
  - Bộ test khó giai đoạn 3 (E4/F1/H1/D2): harness Linux **182 PASS / 3 FAIL** (1 ca watcher Linux đã biết + 2 repro P0 của H1 chờ chủ repo quyết); sửa `FullContentCompare` không dùng cache hash.
- Việc còn lại để dùng thật hằng ngày (P3): chạy thử trên máy Windows thật với 2 folder thật + 1 USB thật trong 1-2 tuần; tối ưu hash BLAKE3 và quét USN Journal cho folder lớn; chạy nền kiểu Windows Service cho phần Tự động (hiện chạy khi app đang mở).
