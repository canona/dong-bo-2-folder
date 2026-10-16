# Nguyên lý test khó của dự án FolderSync

Luật thường trực dành cho mọi phiên làm việc test khó (Claude Code qua GitHub App, Claude Code local, hoặc con người), ban hành bởi chủ repo theo đề xuất của giám khảo Muse. Bản gốc đã gửi qua comment trên Issue #4 (comment 6225921636, ngày 2026-10-17). Mọi phiên test khó phải đọc kỹ file này trước khi làm.

Trên hết: 12 nguyên tắc an toàn trong AGENTS.md là luật cao nhất của repo. Bộ test khó sinh ra để bảo vệ chúng — không bao giờ đề xuất nới lỏng chúng để qua bài.

## 10 nguyên lý

1. **Bằng chứng trên hết.** Mọi kết luận phải có bằng chứng kiểm chứng được: output thật, số test, commit, PR. Lời tự báo không có bằng chứng sẽ không được nghiệm thu.

2. **Test trước, sửa sau.** Test phải FAIL trên code hiện tại thì mới được sửa engine; giữ nguyên output FAIL đưa vào báo cáo. Không có FAIL trước = không sửa. Cấm sửa test để ép PASS.

3. **Oracle độc lập.** Kết quả được đối chiếu bằng các bất biến toàn cục (Oracle O1–O8 trong bộ test khó) và quét lại độc lập từ đĩa/state — không tin bộ máy tự chấm chính nó.

4. **Đường nền là cổng.** Baseline phải khớp trước khi làm (trên Linux: 156 PASS / 0 FAIL, hoặc 155 PASS / 1 FAIL với ca FAIL duy nhất là ca watcher Linux đã biết "RUN Lien tuc Backup: tao file moi o nguon -> tu xuat hien o dich" và phải nêu đúng tên ca đó). Lệch thì dừng và báo cáo.

5. **Xác định và lặp lại.** Mọi ngẫu nhiên phải có seed cố định. Harness phải chạy 3 lần liên tiếp cho kết quả giống hệt mới được tính; ca nào chớp tắt (flaky) phải nêu rõ, không ép xanh.

6. **Đi đường thật.** Test phải đi qua đường thực thi thật (SyncSession, JobRunner, FolderScanner) với thư mục tạm và tự dọn sau chạy. Không mock tắt bản chất vấn đề.

7. **Dừng đúng lúc.** Gặp phát hiện lớn, mơ hồ thiết kế, hoặc nghi mất dữ liệu mà hướng sửa chưa rõ: dừng lại, báo cáo rõ ràng và chờ quyết định của chủ repo. Không tự ý làm, không quyết thay.

8. **Không nới thước đo.** Không sửa bất biến, không lỏng tiêu chí PASS để qua bài. Trường hợp chưa có bằng chứng thì để mở và ghi rõ lý do (tiền lệ: phát hiện 3 ở PR #3 và bất biến số 6).

9. **Sửa nhỏ nhất đúng tầm.** Chỉ sửa đúng phạm vi được giao, không đổi hành vi sản phẩm ngoài phạm vi. Việc ngoài phạm vi thì ghi nhận vào báo cáo, không tự mở rộng.

10. **Trung thực về môi trường + vệ sinh quy trình.** Môi trường chạy test là Linux runner: điều gì không tái hiện hoặc không kiểm chứng được trên Linux (Windows, USB thật, giao diện) phải nói rõ là chưa kiểm chứng, không khẳng định thay. Làm trên nhánh riêng, mở PR vào main, không commit thẳng main, không tự merge. Báo cáo theo mẫu: baseline đầu phiên / kết quả từng test / mục PHÁT HIỆN / tổng kết số test và 3 lần chạy.
