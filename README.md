# FolderSync — Phần mềm desktop đồng bộ dữ liệu giữa 2 folder

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
- Việc còn lại để dùng thật hằng ngày (P3): chạy thử trên máy Windows thật với 2 folder thật + 1 USB thật trong 1-2 tuần; real-time watcher + hẹn lịch; tối ưu hash BLAKE3 và quét USN Journal cho folder lớn.
