# HƯỚNG DẪN LẤY CODE VỀ, BUILD VÀ CHẠY FOLDERSYNC TRÊN MÁY WINDOWS CÁ NHÂN

> Mã nguồn: https://github.com/canona/dong-bo-2-folder — nhánh `main`
> Yêu cầu: Windows 10 64-bit trở lên

---

## CÁCH 1 — CHẠY NHANH ĐỂ DÙNG THỬ (khuyên dùng lần đầu)

### Bước 1. Cài công cụ (chỉ làm 1 lần)

1. **Git cho Windows:** tải ở https://git-scm.com/download/win — cài mặc định (Next liên tục).
2. **.NET 8 SDK:** tải ở https://dotnet.microsoft.com/download/dotnet/8.0 — chọn bản **SDK x64** cho Windows, cài mặc định.
   > Phải là bản **8.0 SDK** (kèm sẵn Runtime 8). Cài SDK 9/10 cũng build được nhưng khi chạy có thể báo thiếu Runtime 8.

### Bước 2. Lấy code về

Mở **Command Prompt** hoặc **PowerShell**, chạy:

```bat
cd C:\
git clone https://github.com/canona/dong-bo-2-folder.git
cd dong-bo-2-folder
```

> Không muốn dùng Git: vào trang GitHub của repo → nút **Code** màu xanh → **Download ZIP** → giải nén. Sau này muốn cập nhật bản mới thì tải lại ZIP, hoặc dùng Git: đứng trong thư mục code và chạy `git pull`.

### Bước 3. Build và chạy app

```bat
dotnet run --project src\FolderSync.App\FolderSync.App.csproj
```

- Lần đầu chạy sẽ tải các gói phụ thuộc (NuGet) — cần có mạng, mất khoảng 1-3 phút. Các lần sau chạy ngay.
- Cửa sổ **FolderSync** hiện lên là thành công.

### Bước 4. Kiểm tra engine bằng bộ test tự động (không bắt buộc)

```bat
dotnet run --project tests\FolderSync.Core.Harness
```

Kết quả mong đợi ở dòng cuối: `TONG: 96 PASS, 0 FAIL`.

---

## CÁCH 2 — MỞ BẰNG VISUAL STUDIO (để xem/sửa code)

1. Cài **Visual Studio 2022** bản mới nhất, khi cài tick workload **".NET desktop development"**.
2. Mở file `FolderSync.slnx` trong thư mục code.
   - Nếu VS báo không nhận dạng được file `.slnx` (VS cũ): cập nhật VS lên bản mới nhất, hoặc mở trực tiếp file `src\FolderSync.App\FolderSync.App.csproj`.
3. Bấm **F5** để chạy (chọn project khởi động là FolderSync.App nếu được hỏi).

---

## CÁCH 3 — TẠO BẢN CHẠY ĐỘC LẬP (mang sang máy khác / cho vào USB, máy đích không cần cài .NET)

Đứng trong thư mục code, mở PowerShell và chạy:

```powershell
powershell -ExecutionPolicy Bypass -File .\packaging\build-release.ps1
```

Kết quả trong thư mục `dist\`:

| Đầu ra | Dùng để |
|---|---|
| `FolderSync-portable.zip` | Giải nén ra 1 thư mục (hoặc vào USB) — chạy ngay file `FolderSync.App.exe`, không cần cài đặt |
| `dist\installer\` | Đầu vào để làm bộ cài bằng Inno Setup (mở `packaging\FolderSync.iss` bằng Inno Setup 6 → Compile) |

Bản portable có sẵn file `portable.flag` — dữ liệu cấu hình (state.db) nằm ngay trong thư mục app nên copy cả thư mục là mang theo được toàn bộ Job đã tạo.

---

## DÙNG THỬ LẦN ĐẦU — LƯU Ý AN TOÀN

1. Bấm **Thêm** → đặt tên Job → chọn Folder Trái và Folder Phải → chọn chế độ → **Lưu**.
2. Bấm **Phân tích (Analyze)** để xem trước danh sách hành động — bước này **không động vào file nào**.
3. Kiểm tra danh sách xong mới bấm **Đồng bộ (Sync)**.
4. **Lần đầu hãy thử với 2 folder bản sao (copy thử nghiệm), chưa dùng ngay folder dữ liệu thật.** Đặc biệt chế độ **Mirror** sẽ xóa các file thừa ở Folder Phải cho giống hệt Folder Trái.
5. File bị xóa/ghi đè không mất hẳn: chúng nằm trong thư mục `.foldersync_history` bên trong folder bị tác động, khôi phục bằng nút **Lịch sử / Khôi phục** trong app.

## Lỗi thường gặp

| Hiện tượng | Cách xử lý |
|---|---|
| `dotnet` không được nhận diện | Chưa cài .NET SDK hoặc cài xong chưa mở lại cửa sổ lệnh — đóng/mở lại Command Prompt |
| Báo thiếu .NET Runtime 8.0 khi chạy | Đã cài SDK 9/10 mà chưa có Runtime 8 — cài thêm .NET 8 SDK (Cách 1, Bước 1) |
| PowerShell báo script bị chặn (execution policy) | Chạy đúng lệnh ở Cách 3 (đã có `-ExecutionPolicy Bypass`) |
| Lần build đầu lâu hoặc lỗi tải gói | Kiểm tra mạng — lần đầu cần tải NuGet; chạy lại lệnh là được |
| App mở lên nhưng danh sách Job trống | Bình thường với lần chạy đầu — bấm **Thêm** để tạo Job đầu tiên |
