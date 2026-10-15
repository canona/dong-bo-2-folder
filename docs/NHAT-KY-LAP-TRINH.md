# Nhật ký lập trình — FolderSync

Ghi theo thứ tự thời gian: mỗi đợt làm gì, vì sao, lỗi nào đã gặp, kiểm chứng ra sao.
Quy ước làm việc và bất biến an toàn: xem [CLAUDE.md](../CLAUDE.md). Mỗi đợt mới thêm 1 mục ở **cuối** file.

Tác giả commit trên GitHub: `canona` và `CaoNN` (Claude Code làm việc cùng người dùng ở máy cá nhân).

---

## Đợt 0 — MVP P0–P2 (14/10/2026, `canona`)

Commit: `b7a432d` Initial commit → `7f46e46` MVP → `36e2755` hướng dẫn cài đặt.

- **P0**: khung solution + `ThreeWayAnalyzer` (bảng quyết định 3 chiều cho 3 chế độ) + `SyncFilter` mặc định. Dung sai thời gian 2 giây (FAT32/USB).
- **P1**: `FolderScanner` (không theo symlink/junction, lỗi 1 file không sập cả lần quét), `SqliteStateStore` (`jobs`, `file_state`, `sync_runs`), `RenameDetector` (chỉ ghép khi khớp 1-1 và có hash), `SyncExecutor` (copy nguyên tử `.tmp-sync`, history, chốt xóa 50% / 1000 file), `SyncSession`.
- **P2**: `ConflictResolver` (NewerWins hòa giờ → KeepBoth, LeftWins, RightWins, KeepBoth; Xóa/Sửa luôn giữ bản sửa), `HistoryService` khôi phục 1-click, CRUD Job, WPF UI, đóng gói.
- Lỗi đã sửa trong đợt: khôi phục trong cùng 1 giây có thể ghi đè chính bản đang khôi phục → chụp file tạm trước + stamp history có mili giây.
- Harness: **48/48 PASS**.

## Đợt 1 — Phân tích + chốt an toàn khi quét lỗi (14/10/2026, Claude Code)

Commit: `284360b`.

Phân tích code MVP, viết repro trong thư mục tạm, phát hiện:

| Lỗi | Hậu quả (đã tái hiện) | Sửa |
|---|---|---|
| `Run()` bỏ qua `ScanErrors`; chốt % bị bỏ qua khi chưa có state | Mirror lần đầu, folder Trái không tồn tại → **xóa sạch 20/20 file bên Phải** | `ScanResult.FailedPaths` + `RootMissing`; gốc mất + có lệnh xóa/đổi tên → hủy (kể cả Force); hành động chạm đường dẫn lỗi → Skip + giữ state cũ; chưa có state thì mốc % = tổng file 2 phía |
| Thư mục con không đọc được bị coi là "đã xóa" | Lan truyền xóa sang phía kia | Như trên (`SkipUnreadable`, `KeepUnreadableState`) |
| Đổi tên chỉ khác hoa/thường | Không đồng bộ (dictionary `OrdinalIgnoreCase`) | `ThreeWayAnalyzer.DecideCaseRename` (chỉ phần tên file) |
| `SyncFilter.IsIncluded("")` | Ném `ArgumentOutOfRangeException` | Trả `false` |
| Mirror: `RenameDetector` dùng `state[A]` dù file đích đã bị sửa | Move mang nội dung sai | Kiểm nội dung phía đích khớp state trước khi ghép |

Thêm tính năng theo yêu cầu: **tiến trình đồng bộ** — `SyncSession.Run(IProgress<SyncProgress>)` báo Planned/Started/Completed/Failed kèm đường dẫn đích thật + thời gian; UI có thanh tiến trình, cột Trạng thái / Đích / Thời gian.

Kiểm chứng: thêm `SafetyTests.cs`; 13/16 ca mới **FAIL trên code cũ**, PASS trên code mới. Harness **66/66 PASS**.
Ghi chú môi trường: máy chỉ có .NET SDK 8.0.423 → không mở được `.slnx`, build từng `.csproj`.

## Đợt 2 — Giao diện GoodSync + Job Options (14/10/2026, `canona`)

Commit: `cda7760` … `d1675cf` (đẩy thành 4 phần qua GitHub connector; icon lưu dạng `.b64` vì connector chỉ đẩy được file text).

- UI phong cách GoodSync: thanh công cụ navy, thẻ 2 folder (định dạng ổ, dung lượng, "Cần chép"), chip lọc, cây so sánh theo thư mục.
- `JobOptionsWindow` 7 tab (thay `JobEditWindow` đã xóa): Chung, Bộ lọc (glob Include/Exclude, ẩn/hệ thống), Tự động (khi mở app / FileSystemWatcher có trễ / định kỳ), Scripts (cmd trước/sau, biến `%JOBNAME%`…), Nâng cao, Phía Trái/Phải (dung sai).
- Engine: `JobOptions` (JSON trong `jobs.settings_json` + migration), Backup lan truyền xóa (chỉ file có trong state), `HistoryService.Cleanup`, copy creation time/attributes.
- Harness: **86/86 PASS** (thêm `OptionsTests.cs`). UI mới chỉ được build trên Linux, chưa chạy thật trên Windows.

## Đợt 3 — Giai đoạn 1: sửa lỗi an toàn sau Job Options (14/10/2026, Claude Code)

Commit: `47798a7` (nhánh `fix/giai-doan-1`, merge fast-forward vào `main`).

Phân tích đợt 2 bằng repro, xác nhận:

| Lỗi | Hậu quả (đã tái hiện) | Sửa |
|---|---|---|
| A. UI `PrepareJob` tạo folder **trước** khi chạy (cả trong Analyze) | Ổ Trái mất → app tạo folder rỗng → qua mặt chốt RootMissing; ngưỡng 100% → **Phải còn 0/10 file** | Bỏ `PrepareJob`; `SyncSession(createMissingRoots)` chỉ tạo ở lần chạy đầu (chưa có state), 1 chiều chỉ tạo đích, Analyze không tạo |
| K. Include chặn thư mục con | Include `*.txt` bỏ sót mọi file trong thư mục con (scanner kiểm thư mục bằng `sub/_probe`) | `SyncFilter.IsDirectoryIncluded`: thư mục chỉ áp mặc định + Exclude |
| J1. Tắt hash: 2 file giống hệt cùng tạo mới → Conflict | NewerWins hòa giờ sinh conflict copy thừa | `SameContent` = `!Changed` (size + mtime trong dung sai khi thiếu hash) |
| J2. Tắt hash: `ComputeSyncedState` chỉ so size | File khác nội dung bị ghi vào state là "đã đồng bộ" | So thêm mtime khi thiếu hash. Giới hạn còn lại (cùng size + mtime, khác nội dung) không sửa được nếu không hash → cảnh báo trên UI |
| Job cũ nhận mặc định mới | `CleanupHistory=true` → **xóa vĩnh viễn history > 30 ngày** của Job cũ; tự bật copy thuộc tính | `JobOptions.Legacy()` + migration ghi Legacy cho Job có sẵn; `FromJson("{}")` = Legacy |

Kiểm chứng: thêm 10 ca vào `OptionsTests.cs` (chạy qua Scanner/Session thật). Harness **96/96 PASS**.

Rủi ro đã ghi nhận nhưng **chưa sửa** (đưa vào Giai đoạn 2): watcher tự kích hoạt lại sau khi chính app ghi file, không bỏ qua `.tmp-sync`, không xử lý `Error`; Scripts không quote đường dẫn, không ghi output; `MainWindow.xaml.cs` ôm toàn bộ logic chạy Job, không test được.

## Đợt 4 — Cây so sánh thẳng cột, tiến trình Job/file, tốc độ (14/10/2026, Claude Code)

Trạng thái lúc ghi: **chưa commit** (chờ người dùng test thực tế rồi mới commit + push).

Yêu cầu từ người dùng (kèm ảnh chụp GoodSync):

1. **Cột cây so sánh lệch** theo độ dài tên file.
   - Nguyên nhân: template mặc định của `TreeViewItem` đặt header trong cột `Auto` và thụt lề cả dòng → cột `*` co theo nội dung, cấp càng sâu càng lệch.
   - Sửa: `ControlTemplate` riêng cho `TreeViewItem` (dòng trải hết chiều ngang), thụt lề chỉ áp cho cột tên qua `CompareNode.Level/Indent`, nút ▸/▾ tự vẽ, icon folder/file (Segoe MDL2), nền cột L tím nhạt / R xanh lá nhạt, thanh cuộn dọc luôn hiện + header chừa đúng bề rộng thanh cuộn. Đã chụp màn hình xác nhận thẳng cột.
2. **Tiến trình cả Job + từng file + tốc độ.**
   - Core: `SyncExecutor.AtomicCopy` chép theo khối 1 MB, báo `SyncProgressStage.Bytes` (≤ 10 lần/giây) và `Verifying`; `Planned` kèm `BytesTotal` (`SyncSession.CopyBytes`).
   - Tối ưu: hash nguồn **trong lúc chép** (`IncrementalHash`) → đọc nguồn 1 lần thay vì 2. Bù lại khả năng phát hiện nguồn bị sửa giữa chừng bằng so size + mtime trước/sau khi chép.
   - UI: panel phía trên cây — `Dang dong bo X% • a / b file • đã chép / tổng`, `Con lai hh:mm:ss • Toc do (tức thời, cửa sổ 3 giây) (TB …)`, dòng file đang chép; cột Trạng thái có thanh % từng file.
   - Thử thật: 1.76 GB / 4 file trong 9 giây, trung bình 184 MB/s.
3. **Lỗi phát hiện khi thử**: 2 lượt Sync chạy chồng nhau (lượt bấm thứ 2 xếp hàng trong lúc hộp xác nhận mở) → chép lại, đếm `5 / 3 file`. Sửa: kiểm `_busy` ở đầu `Analyze_Click`/`Sync_Click` **và** sau hộp xác nhận.
4. **Cây không cập nhật sau đồng bộ** (lỗi có từ đợt 2): R Size/R Date vẫn "(khong co)". Sửa: mỗi file xong thì `RefreshRowFromDisk` đọc lại `FileInfo` 2 phía (`ActionRow.ApplySnapshot`), chiều chuyển thành `=` xám; cuối lượt dựng lại cây (tổng folder) và tính lại "Cần chép". **Chưa kiểm chứng trên giao diện** — người dùng sẽ test.

5. **Bấm Thêm/Sửa Job là app thoát** (lỗi có từ đợt 2 — UI đợt 2 chỉ build trên Linux, chưa từng chạy trên Windows).
   - Stack trace lấy từ Windows Event Log (`.NET Runtime`): `NullReferenceException` tại `JobOptionsWindow.TabList_SelectionChanged`, ngay trong `InitializeComponent()`.
   - Nguyên nhân: `ListBoxItem` đầu có `IsSelected="True"` → `SelectionChanged` bắn trong lúc nạp XAML, khi các `Panel*` khai báo phía dưới còn null.
   - Sửa: `if (PanelRight is null) return;` (không dùng `IsLoaded` để constructor vẫn chọn tab được). Kiểm chứng bằng UI Automation: mở hộp Job Options, chuyển qua đủ 7 tab, app không thoát; đóng không lưu, danh sách Job giữ nguyên.

6. **Cột Lý do vẫn ghi lý do lúc Phân tích sau khi đã chép** ("Thieu o dich"). Sửa: khi xong đổi thành kết quả (`Da chep sang Phai (Thieu o dich)`, `Da xoa o Phai (ban cu trong history)`, `LOI: ...`) — `MainWindow.DoneText`.
7. **Bấm vào thẻ folder để đổi folder của Job** (yêu cầu người dùng). Phát hiện kèm lỗi tiềm ẩn có từ trước: nút **Sửa** cho đổi folder mà **giữ state cũ** → state của cặp folder cũ áp lên folder mới, file "có trong state nhưng không có ở folder mới" bị hiểu là đã xóa. Repro: Job 2 chiều 10 file, đổi Trái sang folder có 6/10 file → **4 file bên Phải bị xóa**.
   - Sửa tại Core: `SqliteStateStore.UpsertJob` trả `bool`, tự xóa `file_state` trong cùng transaction khi folder Trái/Phải đổi (so đường dẫn không phân biệt hoa/thường, bỏ `\` cuối). Mọi đường đổi folder (thẻ, nút Sửa) đều được bảo vệ.
   - UI: thẻ folder có con trỏ tay, viền cyan khi rê chuột, icon bút chì, tooltip; bấm → `OpenFolderDialog` → chặn trùng folder kia → hộp xác nhận nêu rõ state cũ sẽ bị xóa → lưu, chọn lại Job, gắn lại watcher.
   - Thêm 3 ca vào `SafetyTests.cs` (giữ state khi chỉ đổi hoa/thường; xóa state khi đổi folder; đổi folder không xóa nhầm file).

Kiểm chứng: thêm 3 ca tiến trình byte + 3 ca đổi folder vào `SafetyTests.cs`. Harness **102/102 PASS**, build App 0 lỗi.

Ghi chú quy trình: khi tự động hóa UI để chụp màn hình, chỉ chụp riêng cửa sổ app bằng `PrintWindow` (không chụp toàn màn hình — dễ dính cửa sổ riêng tư của người dùng). Dữ liệu demo 1.8 GB ở `D:\ThucHanhAI\demo-dong-bo\Lon` (ngoài repo).

## Đợt 5 — Job chạy liên tục, độc lập; giữ kết quả khi chuyển Job (15/10/2026, Claude Code)

Trạng thái lúc ghi: **chưa commit** (nhánh `feat/job-lien-tuc`, gộp cả thay đổi đợt 4 chưa commit).

Yêu cầu: (1) bấm qua lại các Job thì bảng phân tích bị trắng; (2) cấu hình Job đã tạo để **chạy liên tục** — có thay đổi ở nguồn thì chép sang đích, Mirror thì thay đổi ở folder nào cũng xử lý; (3) **các Job hoạt động độc lập**.

Hiện trạng trước khi sửa: cả app dùng chung 1 cờ `_busy` + 1 `_autoGate` → 1 Job chạy thì mọi Job khác bị chặn; watcher cũ không bỏ qua file do chính app ghi (mỗi lần chạy tự kích hoạt thêm 1 lần), không xử lý `Error`, không gắn lại khi cắm lại ổ; mọi lần chạy hash lại toàn bộ dữ liệu.

- **Core `JobRunner`** (mới): khóa chạy `SemaphoreSlim` theo Id Job (static — runner bị thay khi sửa Job vẫn không chạy chồng); watcher + debounce + định kỳ; chạy bắt kịp khi bật; thay đổi trong lúc chạy → chạy lại; Scripts + dọn history chuyển từ `MainWindow` sang.
- **Bỏ qua file tự ghi**: phiên bản đầu dùng "đường dẫn + 3 giây ân hạn" → test bắt lỗi: người dùng sửa file đích ngay sau khi app chép → **thay đổi bị bỏ sót**. Đổi sang ảnh chụp (có/không, size, mtime) lúc `Completed`; chỉ thư mục cha dùng ân hạn 3 giây.
- **Mirror với folder đích chưa tồn tại**: watcher không gắn được lúc khởi động → phải chờ chu kỳ thử lại 15 giây (thấy khi chạy thử). Sửa: gắn watcher ngay sau lần chạy tạo folder.
- **`SqliteStateStore` an toàn đa luồng** (`lock` mọi method public) — nhiều Job chạy song song dùng chung 1 connection.
- **Cache hash** (`ScanCache` + `FolderScanner.Scan(hashCache)`): file cùng size + mtime dùng lại hash → chế độ liên tục không phải đọc lại toàn bộ dữ liệu mỗi lần có 1 file đổi.
- **UI**: `JobView` riêng từng Job (bảng + tiến trình) → chuyển Job không mất kết quả, Job chạy nền vẫn cập nhật; danh sách Job 2 dòng (tên + nhãn LIEN TUC / x PHUT + trạng thái "Dang theo doi • 11:44 copy 1…"); chuột phải: Phân tích / Đồng bộ ngay / Bật-tắt đồng bộ liên tục / Tùy chọn; nhấp đúp hoặc nút "Tuy chon" mở cấu hình; tab Tự động giải thích hành vi từng chế độ. Lần chạy tự động không có thay đổi không ghi log, không hiện thanh tiến trình.
- **Sự cố môi trường**: harness không bao giờ dọn `%TEMP%` → 995 thư mục, góp phần làm **ổ C: hết sạch dung lượng** (SQLite `database or disk is full`). Đã xóa thư mục tạm của harness/repro; harness giờ tự dọn khi chạy xong; giảm file test lớn 200 MB → 64 MB. Ổ C: của máy vẫn rất chật (~0,7 GB trống) — người dùng cần tự dọn.
- Kiểm chứng: thêm `JobRunnerTests.cs` (13 ca, watcher thật: Backup/Mirror liên tục, không tự kích hoạt, 2 Job song song, cùng Job không chạy chồng, cache hash, store 8 thread). Harness **115/115 PASS**. Chạy thử app thật (DB portable riêng, đã gỡ): 2 Job liên tục chạy song song, chuyển Job giữ kết quả, Mirror trả file đích bị sửa về giống nguồn.

## Đợt 6 — Dung lượng ổ trực quan + cảnh báo lỗi bằng màu (15/10/2026, Claude Code)

Trạng thái lúc ghi: **chưa commit** (nhánh `feat/job-lien-tuc`).

Yêu cầu (kèm ảnh: Job Backup sang USB H: FAT32 14,3 GB chỉ còn 248 KB, 5 file `.rar` lỗi "There is not enough space on the disk"): (1) hình ảnh trực quan dung lượng ổ còn lại; (2) đồng bộ lỗi có cảnh báo màu.

- **Lỗi thật phát hiện từ ảnh**: kiểm tra (chỉ đọc) `H:\Alarms` thấy **5 file `*.tmp-sync` sót lại** (1 file 59 MB) từ lần chép lỗi — `AtomicCopy` không xóa file tạm khi lỗi giữa chừng, scanner lại bỏ qua `*.tmp-sync` → chiếm chỗ mãi. Sửa: mọi lỗi từ lúc ghi file tạm tới lúc rename sang đích đều xóa file tạm (lần đầu sửa còn sót bước rename/PreserveExisting — phát hiện khi dựng demo, đã đưa vào cùng khối).
- **Kiểm chỗ trống trước khi chép** (`SyncExecutor.FreeSpaceProbe`, chừa 1 MB): không đủ thì báo lỗi ngay "Khong du cho trong o dich: can X, chi con Y", không ghi gì. `FriendlyError` dịch lỗi hay gặp sang tiếng Việt. `InternalsVisibleTo` cho harness để giả lập hết chỗ.
- **UI dung lượng**: mỗi thẻ folder có thanh 3 đoạn (đã dùng: xanh → cam > 80% → đỏ > 95% | cần chép của Job: cam, đỏ nếu không đủ | còn trống), dòng "Da dung x% • Trong / Tong", dòng "Can chep … (con lai sau khi chep …)" xanh hoặc "⚠ Khong du cho … thieu …" đỏ. Bấm Đồng bộ khi bảng phân tích cho thấy thiếu chỗ → hộp cảnh báo Yes/No trước.
- **UI lỗi**: dòng lỗi nền đỏ nhạt + chữ đỏ đậm; thư mục chứa lỗi ghi "n loi"; chip "Loi (n)" đỏ đậm; panel tiến trình viền/nền/thanh đỏ, "⚠ Hoan tat voi n LOI • x / y file thanh cong"; danh sách Job "⚠ …" đỏ (`JobRunner.LastErrorCount`); log lỗi đỏ, dòng tổng kết có lỗi màu cam.
- Kiểm chứng: thêm 3 ca vào `SafetyTests.cs` (hết chỗ không ghi gì; lỗi giữa lúc ghi; lỗi ở bước rename) → **119/119 PASS**. Chạy thử app với DB portable riêng (không mở danh sách Job thật vì cả 2 Job thật đang bật liên tục — sẽ ghi lên USB): Job lỗi thật (đích có thư mục trùng tên file) hiển thị đỏ đúng; Job nguồn 1,76 GB → đường dẫn chưa tồn tại trên H: chỉ Phân tích → thanh H: đỏ + cảnh báo thiếu 1,76 GB, không ghi gì lên H:.
- Tồn đọng: 5 file `.tmp-sync` cũ trên `H:\Alarms` — người dùng đã tự xóa (cùng toàn bộ file trong folder) khi thử ở đợt 7.

## Đợt 7 — Backup liên tục không chép lại file bị xóa ở đích (15/10/2026, Claude Code)

Trạng thái lúc ghi: **chưa commit** (nhánh `feat/job-lien-tuc`).

- Báo lỗi: Job SynFolder2 (Backup, bật liên tục) — người dùng xóa file ở folder đích `H:\Alarms` nhưng không được chép lại.
- Nguyên nhân: `JobRunner.WatchedRoots` chỉ theo dõi folder nguồn với chế độ Backup (giả định sai: "đích thay đổi thì Backup không cần làm gì"). Engine vốn xử lý đúng (file có ở nguồn, thiếu ở đích → chép), chỉ là không có sự kiện kích hoạt.
- Sửa: theo dõi **cả 2 folder ở mọi chế độ**; file do app tự ghi đã được bỏ qua bằng ảnh chụp nên không sinh vòng lặp. Cập nhật mô tả tab Tự động.
- Kiểm chứng: thêm 2 ca vào `JobRunnerTests` (xóa file ở đích → tự chép lại; file chỉ có ở đích → giữ nguyên). Ca mới **FAIL trên hành vi cũ** (120/121), PASS sau khi sửa → **121/121 PASS**. Chạy thật trên USB của người dùng: các file bị xóa ở `H:\Alarms` được chép lại, không sót file tạm; `525348 - Copy - Copy.txt` (chỉ có ở đích, nguồn đã xóa) không được tạo lại — đúng ngữ nghĩa Backup.

## Đợt 8 — Nhãn LIEN TUC sai + đổi nhanh chế độ (15/10/2026, Claude Code)

Trạng thái lúc ghi: **chưa commit** (nhánh `feat/job-lien-tuc`).

- **Lỗi**: SynFolder2 đã tắt đồng bộ liên tục nhưng vẫn hiện nhãn LIEN TUC (dòng trạng thái cũng mất "Dang theo doi" nhưng giữ kết quả cũ). Nguyên nhân: sửa Job → `ReloadJobs` thay runner mới và Dispose runner cũ; runner cũ bắn `StateChanged(Idle)` qua `Dispatcher.BeginInvoke`, tới **sau** khi danh sách vẽ lại → `Update(runnerCu)` ghi đè dòng Job bằng Options cũ. Sửa: `MainWindow.IsCurrent(runner)` — bỏ qua sự kiện của runner đã bị thay; `JobRunner.InheritHistory` để runner mới giữ "lần chạy cuối"; lần chạy dở của runner cũ vẫn hiện kết quả nhưng dòng Job cập nhật theo runner hiện tại. Kiểm chứng bằng UI Automation (DB demo riêng): mở Tùy chọn → bỏ "Dong bo lien tuc" → Lưu → nhãn biến mất, dòng giữ "12:53 copy 1…".
- **Tính năng**: bấm vùng giữa 2 thẻ folder (mũi tên + tên chế độ, có viền khi rê chuột) → menu chọn Backup 1 chiều / Mirror 1 chiều (đích giống hệt nguồn) / Đồng bộ 2 chiều. Hộp xác nhận nêu hậu quả riêng từng chế độ (Mirror xóa file chỉ có ở đích → history; 2 chiều chép ngược file chỉ có ở đích về nguồn) và cảnh báo Job liên tục sẽ chạy NGAY theo chế độ mới. Đổi chế độ xóa bảng phân tích cũ (tính theo chế độ cũ). **Chưa tự động kiểm chứng được** (vùng này là `Border`, UI Automation không bấm được nếu không điều khiển chuột thật) — chờ người dùng thử.
- Nhỏ: cột giữa header rộng 86 → 112 (tên chế độ không xuống dòng); bỏ log "chua theo doi duoc… se thu lai" khi Job có "Tao folder neu thieu" (folder sẽ được tạo ở lần chạy đầu).
- Harness **121/121 PASS** (thay đổi chủ yếu ở UI).

## Đợt 9 — Khôi phục nhiều file + trạng thái đã khôi phục (15/10/2026, Claude Code)

Trạng thái lúc ghi: **chưa commit** (nhánh `feat/job-lien-tuc`).

- Báo lỗi: cửa sổ Lịch sử chọn nhiều file nhưng chỉ khôi phục 1 (code dùng `SelectedItem`); không biết file nào đã/chưa khôi phục; file đã khôi phục vẫn cho khôi phục lại.
- Core `HistoryService`:
  - `GetStatus` so bản history với **file hiện tại ở vị trí gốc** (khác size → khác; cùng size → so hash): `OriginalMissing` / `CurrentDiffers` / `SameAsCurrent`. Suy ra từ nội dung thật nên đúng cả khi khôi phục ở lần mở app trước hoặc chép tay — không cần lưu nhật ký khôi phục riêng.
  - `RestoreMany`: bỏ qua bản đã giống file hiện tại; chọn nhiều phiên bản của **cùng 1 file** → chỉ khôi phục bản mới nhất (khôi phục cả hai sẽ ghi đè nhau); 1 file lỗi không dừng cả đợt.
  - `Restore` cũng từng bỏ sót file `.tmp-sync` khi bước sao lưu/đổi tên lỗi (cùng lỗi với Executor đợt 6) → bọc try/catch xóa file tạm.
- UI `HistoryWindow` (giờ nhận `JobRecord` + `JobOptions`): chọn nhiều dòng (Ctrl/Shift) + nút "Chon tat ca file chua khoi phuc"; cột Trạng thái có màu (cam: file gốc đã bị xóa, xanh dương: file hiện tại khác, xanh lá ✓: đã khôi phục); dòng đã khôi phục mờ + không chọn được; trạng thái tính nền (hash) sau khi hiện danh sách; nút "Khoi phuc n file da chon"; hộp kết quả (đã khôi phục / bỏ qua vì sao / lỗi từng file); tùy chọn ẩn file đã khôi phục. Cảnh báo vàng khi xem folder **đích** của Job 1 chiều: file khôi phục ở đó sẽ bị đồng bộ ghi đè lại (Mirror còn xóa nếu nguồn không có).
- Kiểm chứng: thêm 5 ca vào `SafetyTests` (trạng thái, khôi phục nhiều file + bỏ qua bản cũ hơn, sau khôi phục = "giống hiện tại", khôi phục lại bị bỏ qua, không sót file tạm) → **126/126 PASS**. Chạy thử app bằng UI Automation (DB demo riêng): chọn 4 bản → "Da khoi phuc 3 file, bo qua 1 (ban moi hon cua cung file)"; 3 dòng chuyển "✓ Da khoi phuc" và bị khóa; bản hiện tại bị thay được thêm vào history (khôi phục lại được).

## Đợt 10 — Full / Mini / Tray Icon, chạy cùng Windows, bộ cài đặt (15/10/2026, Claude Code)

Trạng thái lúc ghi: **chưa commit** (nhánh `feat/job-lien-tuc`).

Quyết định của người dùng (hỏi trước khi làm): cài Inno Setup vào ổ D: chế độ user; bấm X = ẩn xuống khay.

- **Môi trường**: ổ C: chỉ còn ~0,2 GB → publish self-contained (runtime ~200 MB) được chuyển cache NuGet/HTTP/TEMP sang D: (`-PackagesDir`, biến `NUGET_HTTP_CACHE_PATH`…). Inno Setup 6.7.3 tải từ GitHub release chính thức, **kiểm SHA256 khớp manifest winget + chữ ký số hợp lệ** trước khi cài im lặng `/CURRENTUSER` vào `D:\Tools\InnoSetup6`.
- **App**: `App.OnStartup` — 1 phiên bản (mutex + event báo phiên bản đầu hiện cửa sổ), `--tray` / `--mini`, `ShutdownMode=OnExplicitShutdown`; `TrayIconService` (NotifyIcon: menu Full/Mini/Đồng bộ tất cả/Tạm dừng tự động/Chạy cùng Windows/Thoát, tooltip trạng thái, bong bóng khi Job lỗi/hủy lúc cửa sổ ẩn); `MiniWindow` (góc dưới phải, ghim trên cùng, danh sách Job + tiến trình + nút Đồng bộ); `AppSettings` (app-settings.json) + `StartupManager` (HKCU Run, `--tray`); nút **Mini** + **Cai dat ▾** trên thanh công cụ; thoát khi có Job đang chạy thì hỏi.
- **Lỗi thiết kế tránh được**: khởi động `--tray` cửa sổ không hiện → `Window_Loaded` không bắn → nếu nạp Job ở đó thì Job liên tục không chạy khi khởi động cùng Windows. Đã chuyển sang `InitOnce()` gọi từ `Start()`.
- **Đóng gói**: `FolderSync.iss` — PrivilegesRequired=lowest (không cần admin), AppMutex, task `autostart` ghi HKCU Run cùng tên/tham số với app, gỡ cài đặt luôn xóa giá trị Run, shortcut Mini; `build-release.ps1` — publish self-contained single-file nén, zip portable có thử lại (Defender khóa file vừa chép), tự gọi ISCC (sửa bug PowerShell: 1 kết quả `Where-Object` là chuỗi → `$candidates[0]` = "D"). `dist/` thêm vào `.gitignore`.
- Kiểm chứng (DB portable riêng, không đụng Job thật): mở thường → Full; nút Mini → chỉ Mini; X ở Mini/Full → ẩn, tiến trình vẫn chạy; mở lần 2 → vẫn 1 tiến trình, hiện Full; `--tray` → không cửa sổ nào nhưng Job liên tục vẫn đồng bộ file mới; menu Cài đặt bật/tắt chạy cùng Windows ghi/xóa đúng registry (đã hoàn tác). Bộ cài: cài im lặng vào thư mục tạm → exe self-contained chạy được, task autostart ghi Run đúng → gỡ cài đặt xóa file + Run + khóa Uninstall sạch. Harness 126/126 PASS.
- Chưa kiểm chứng tự động: menu chuột phải của icon khay (NotifyIcon không truy cập được bằng UI Automation) và bong bóng thông báo — chờ người dùng thử.

## Đợt 11 — Điều tra: "Phân tích" treo với folder lớn, nhiều file (15/10/2026, Claude Code)

Trạng thái: **đã sửa** (người dùng duyệt "sửa tất cả theo đề xuất"), nhánh `perf/phan-tich-lon`, chưa commit.

- Đo trên bộ dữ liệu tự tạo `D:\ThucHanhAI\demo-dong-bo\Bench` (40.020 file, 2,1 GB; đích có sẵn 75%):
  - Quét có hash SHA-256 toàn bộ nội dung: nguồn 19,7 s + đích 26,8 s (không hash: 3,1 s; có cache: 3,2 s). So sánh 3 chiều: 0,03 s. RenameDetector 3.000×3.000: 0,26 s (O(xóa×chép)).
  - Trong app (đo bằng biến `FOLDERSYNC_DIAG`, không dùng UI Automation trên cây vì chính nó tạo thêm automation peer gây nhiễu): quét + so sánh 56 s → BuildRows + RebuildTree trên UI thread 4,5 s → **WPF layout/render cây 40.020 dòng > 10 phút, app Not Responding, RAM 4,2 GB**, hệ thống cạn bộ nhớ ảo (PowerShell báo 0x800705AF), lần đo đầu app crash (WER APPCRASH 0xe0434352 + RADAR_PRE_LEAK_64).
- Nguyên nhân: (1) **cây so sánh không ảo hóa** + hiển thị cả file không đổi → tạo toàn bộ phần tử giao diện cho mọi file; (2) **hash toàn bộ nội dung 2 phía** ở lần phân tích đầu (cache chỉ trong bộ nhớ), không có tiến trình khi quét → trông như treo; (3) BuildRows (2 `FileInfo`/dòng) + RebuildTree chạy trên UI thread; (4) RenameDetector bậc hai (tiềm ẩn).
- Thêm vĩnh viễn: `Diag` (đo từng bước khi đặt `FOLDERSYNC_DIAG=<file log>`), `crash.log` cạnh `state.db` cho mọi exception không bắt được (crash lần này không để lại stack trace trong Event Log).

- **Sửa (A)** cây so sánh → `ListBox` danh sách phẳng ảo hóa (`BulkObservableCollection`, thu/mở = chèn/gỡ dòng con), mặc định chip "Thay doi". **(B)** `HashPlanner`: chỉ hash file cùng size khác mtime + ứng viên đổi tên; cache hash lưu bảng `hash_cache` (ghi chênh lệch, xóa khi đổi folder/xóa Job, giữ khi USB rút); hash tính lúc chép vào cache/state; tiến trình `Scanning`/`Hashing`; `CancellationToken` xuyên suốt + nút **Huy** (hủy lúc chép → xóa file tạm, không lưu state); tùy chọn `FullContentCompare`. **(C)** `SessionOutcome`/`Planned` mang kết quả quét → UI không đọc lại file. **(D)** `RenameDetector` chỉ mục (size, hash) O(n), ghép theo nội dung THẬT của file sắp xóa ở đích (đúng cả khi state không có hash). Thêm: `FolderScanner` dùng `EnumerateFileSystemInfos` (1 lần liệt kê/thư mục, không `FileInfo` từng file).
- **Lỗi lộ ra khi sửa**: (1) dung sai 2 giây cho mọi ổ → 30.000 file cùng size khác nội dung tạo cách nhau < 2 giây bị coi là giống → chỉ dùng dung sai khi có ổ FAT/exFAT, NTFS khớp tuyệt đối; (2) race `SqliteStateStore.Dispose` khi Job chạy nền đang ghi → sập tiến trình (NullReferenceException trong SqliteTransaction) → Dispose lấy cùng khóa + `ObjectDisposedException`.
- **Kết quả đo lại** (40.020 file): Job thực tế phân tích + hiển thị **1,2 s** (trước: không xong sau 10 phút, 4,2 GB, crash), RAM 210 MB, chip "Tat ca" 40.020 dòng 0,5 s; mở lại app 0,5 s. Trường hợp xấu nhất (mọi file cùng size khác nội dung, phải đọc 4 GB): lần đầu ~2 phút có tiến trình %, UI không treo lần nào, Hủy dừng sau 0,09 s; lần sau 0,7 s nhờ cache DB.
- Test: thêm `PerfTests.cs` (12 ca); chỉnh 3 ca cũ cho hành vi mới (file khóa không cần đọc nội dung thì liệt kê bình thường; sự kiện `Scanning` đứng trước `Planned`). Harness **138/138 PASS**.

## Đợt 12 — Giới hạn tốc độ chép (băng thông) cho từng Job (15/10/2026, Claude Code)

Trạng thái: **chưa commit** (nhánh `perf/phan-tich-lon`). Làm theo quy trình 5 bước người dùng yêu cầu (phân tích → test FAIL → Core → UI → harness).

- **Thiết kế**: `JobOptions.MaxCopySpeedMBps` (0 = không giới hạn; JSON cũ + `Legacy()` = 0 → Job có sẵn giữ hành vi cũ) → `JobRunner.CreateSession` → `SyncOptions.MaxBytesPerSecond` → `SyncExecutor` tạo **1 `BandwidthThrottle` cho cả lần chạy** (tính chung mọi file, không reset theo file).
- **An toàn**: throttle chỉ chèn thời gian chờ trong vòng ghi file tạm `.tmp-sync` (sau mỗi khối); trình tự tmp → kiểm size/hash → rename và chốt xóa hàng loạt giữ nguyên. Chờ bằng `ct.WaitHandle.WaitOne` → Hủy trong lúc chờ đi đúng nhánh `OperationCanceledException` hiện có → file tạm bị xóa. Bị giới hạn thì khối đọc/ghi ~1/10 giây dữ liệu (64 KB–1 MB) cho tiến trình mượt; "chạy bù" tối đa 1 giây sau khi đứng yên (vd đang kiểm hash) để không vượt giới hạn xa. Không giới hạn bước kiểm hash file tạm / hash lúc phân tích (đọc, không phải truyền).
- **UI**: tab Nâng cao — thanh kéo 0–200 MB/s + ô số (tới 10.000) + dòng gợi ý ("1 GB mất khoảng…"); handler có chốt null (sự kiện bắn trong `InitializeComponent`) + cờ chống vòng lặp slider ↔ ô số; Lưu đọc lại ô số. Panel tiến trình thêm "• giới hạn x MB/s".
- **Kiểm chứng**: `BandwidthTests.cs` 7 ca, viết trước — harness **không biên dịch** trên code cũ (thiếu `SyncOptions.MaxBytesPerSecond`, `JobOptions.MaxCopySpeedMBps`); sau Core → 7/7 PASS (2 MB/s file 4 MB ≥ 1,8 s; không giới hạn < 1,5 s; 4 file chung giới hạn; Hủy khi đang chờ < 1,5 s không sót tmp; JSON cũ/Legacy = 0; lưu/đọc 25 MB/s; JobRunner áp giới hạn). Chạy thử app (DB portable riêng): kéo slider 5 MB/s, lưu, đồng bộ file 40 MB → 8 s, TB 4,97 MB/s, panel hiện "giới hạn 5 MB/s", không sót tmp. Harness **145/145 PASS**, App build 0 lỗi / 0 cảnh báo.

---

## Mẫu cho đợt tiếp theo

```markdown
## Đợt N — <tiêu đề> (<dd/mm/yyyy>, <tác giả>)

Commit: `<hash>` (hoặc: chưa commit — lý do).

- Yêu cầu / bối cảnh:
- Lỗi phát hiện (hậu quả đã tái hiện) → cách sửa:
- Quyết định thiết kế + lý do:
- Kiểm chứng: harness <n>/<n> PASS; test mới FAIL trên code cũ? chạy thử UI?
- Còn tồn đọng:
```
