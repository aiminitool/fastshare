# FileServer — chương trình chia sẻ file nhẹ, có mật khẩu, hỗ trợ resume

Chương trình console C# (.NET 8), không phụ thuộc framework nặng (không dùng
ASP.NET Core), chỉ dùng `HttpListener` có sẵn trong .NET. Publish ra
**self-contained** (không cần cài .NET runtime trên máy chạy) dưới dạng
**1 thư mục gồm file `.exe`/binary chạy được kèm vài file `.dll`**.

> Trước đây bản này gộp thành 1 file `.exe` duy nhất ("tự giải nén" khi
> chạy), nhưng kiểu đóng gói đó hay bị Windows Defender/antivirus nghi ngờ
> nhầm là mã độc. Bản hiện tại đóng gói thành thư mục nhiều file bình
> thường, ít bị chặn nhầm hơn hẳn — chỉ cần nhớ **copy cả thư mục**, không
> tách riêng file `.exe` ra dùng một mình.

**Tính năng:**
- Đăng nhập bằng mật khẩu (HTTP Basic Auth, tương thích IDM).
- Liệt kê và tải file, hỗ trợ Range/resume/đa luồng (nhanh với IDM).
- **Giao diện quản trị ngay trên trình duyệt** (cùng địa chỉ, cùng mật khẩu
  đăng nhập), gồm 3 tab:
  - *Danh sách file*: xem và tải file như bình thường.
  - *Đường dẫn truy cập*: liệt kê sẵn mọi IP LAN + IP Public kèm cổng,
    có nút **Copy** để lấy link ngay, không cần tự tra IP thủ công.
  - *Cài đặt*: sửa Username, Password, thư mục chia sẻ, cổng, và các tùy
    chọn khác ngay trên web — không cần mở tay file `appsettings.json`.
    Có nút **Khởi động lại ngay** để áp dụng thay đổi mà không cần vào
    lại máy chủ.
- **Tự động mở cổng trên firewall** khi khởi động (Windows Firewall /
  ufw trên Ubuntu) — cần chạy với quyền Administrator/root.
- **Không tự đóng cửa sổ khi gặp lỗi**: nếu khởi động thất bại (ví dụ cổng
  bị chiếm), chương trình in rõ nguyên nhân và chờ bạn nhấn phím trước khi
  thoát.
- Kèm sẵn `start.bat` (trên Windows) để chạy tiện hơn, giữ cửa sổ luôn mở
  lại sau khi chương trình dừng, dù thành công hay lỗi.

## 1. Cài công cụ build (chỉ làm 1 lần, trên máy dùng để build)

Tải và cài **.NET 8 SDK** (miễn phí):
https://dotnet.microsoft.com/download/dotnet/8.0

Kiểm tra đã cài xong:
```
dotnet --version
```

## 2. Cấu hình trước khi build

Mở file `appsettings.json`, sửa lại:
```json
{
  "Username": "admin",
  "Password": "matkhau_cua_ban",
  "SafeDir": "C:/backup/files",
  "Port": 96,
  "AutoOpenFirewall": true,
  "ShowNetworkInfo": true
}
```
- `SafeDir`: trên Windows dùng kiểu `C:/backup/files`, trên Ubuntu dùng kiểu
  `/backup/files`.
- `AutoOpenFirewall`: `true` để chương trình tự động mở cổng trên firewall
  mỗi khi khởi động (cần chạy với quyền Administrator trên Windows, hoặc
  root/sudo trên Ubuntu để lệnh này thành công — nếu không đủ quyền,
  chương trình vẫn chạy bình thường, chỉ in ra lệnh để bạn tự chạy thủ công).
  Đặt `false` nếu bạn muốn tự quản lý firewall.
- `ShowNetworkInfo`: `true` để khi khởi động, chương trình tự liệt kê các
  link truy cập (IP LAN và IP Public kèm cổng) ngay trên màn hình console.
- File `appsettings.json` phải luôn nằm **cùng thư mục** với file chạy
  (exe/binary) sau khi publish — không nhúng được vào trong file exe.

## 3. Build ra 1 file chạy được (publish)

Chạy các lệnh này trong thư mục chứa `FileServer.csproj` (dùng Command
Prompt / PowerShell / Terminal):

### Build cho Windows (chạy trên máy Windows đích)
```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish-win
```
Kết quả: `publish-win/FileServer.exe` (kèm `appsettings.json` cùng thư mục).

### Build cho Ubuntu / Linux (chạy trên máy Linux đích)
```
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o publish-linux
```
Kết quả: `publish-linux/FileServer` (kèm `appsettings.json` cùng thư mục).

> Bạn có thể build cả 2 bản trên cùng 1 máy dev (Windows hoặc Linux đều
> build chéo được), rồi copy đúng bản sang đúng máy đích.

## 4. Chạy chương trình

### Trên Windows
Copy cả thư mục `publish-win` (gồm `FileServer.exe` + `appsettings.json`)
sang máy đích, chạy:
```
FileServer.exe
```
- Nếu cổng < 1024 (ví dụ 96) và bị lỗi quyền, chạy Command Prompt/PowerShell
  với quyền **Administrator**, hoặc đăng ký quyền trước bằng:
  ```
  netsh http add urlacl url=http://+:96/ user=Everyone
  ```
- Mở Firewall cho cổng đó (nếu cần truy cập từ máy khác):
  ```
  netsh advfirewall firewall add rule name="FileServer" dir=in action=allow protocol=TCP localport=96
  ```

**Chạy nền như 1 dịch vụ (Windows Service)** — khuyên dùng để tự khởi động
cùng máy, không cần đăng nhập:
- Dùng công cụ miễn phí **NSSM** (https://nssm.cc/):
  ```
  nssm install FileServer "C:\duong_dan\publish-win\FileServer.exe"
  nssm start FileServer
  ```

### Trên Ubuntu
Copy cả thư mục `publish-linux` (gồm `FileServer` + `appsettings.json`)
sang máy đích, chạy:
```bash
chmod +x FileServer
sudo ./FileServer
```
(`sudo` cần thiết nếu cổng < 1024, ví dụ cổng 96. Nếu dùng cổng ≥ 1024
thì không cần `sudo`.)

Mở firewall nếu dùng `ufw`:
```bash
sudo ufw allow 96/tcp
```

**Chạy nền như 1 service (systemd)** — khuyên dùng cho server thật:
1. Copy thư mục `publish-linux` vào ví dụ `/opt/fileserver/`.
2. Tạo file `/etc/systemd/system/fileserver.service`:
   ```ini
   [Unit]
   Description=FileServer - chia se file co mat khau
   After=network.target

   [Service]
   Type=simple
   WorkingDirectory=/opt/fileserver
   ExecStart=/opt/fileserver/FileServer
   Restart=on-failure
   User=root

   [Install]
   WantedBy=multi-user.target
   ```
3. Kích hoạt:
   ```bash
   sudo systemctl daemon-reload
   sudo systemctl enable fileserver
   sudo systemctl start fileserver
   sudo systemctl status fileserver
   ```

## 5. Dùng với IDM (giống hệt bản PHP trước đó)

- Truy cập `http://<ip-server>:96/` — trình duyệt sẽ hiện popup đăng nhập
  Basic Auth, nhập đúng Username/Password trong `appsettings.json`.
- Trong IDM, dán link tải, tick **Use authorization**, điền đúng
  Login/Password → tải được, hỗ trợ resume và Range giống bản PHP.

## 6. Build tự động bằng GitHub Actions (không cần cài .NET SDK trên máy)

Project này đã có sẵn file `.github/workflows/build.yml`. Chỉ cần đẩy code
lên GitHub là nó tự build cả 2 bản Windows và Linux, bạn chỉ việc tải file
kết quả về.

**Các bước:**
1. Tạo 1 repository mới trên GitHub (có thể để **Private** nếu không muốn
   ai khác thấy, vẫn dùng Actions bình thường, miễn phí).
2. Đẩy toàn bộ thư mục `FileServer` (đã giải nén từ file zip) lên repo đó:
   ```bash
   cd FileServer
   git init
   git add .
   git commit -m "Init FileServer"
   git branch -M main
   git remote add origin https://github.com/<ten-tai-khoan>/<ten-repo>.git
   git push -u origin main
   ```
   > **Quan trọng:** file `appsettings.json` đang chứa mật khẩu dạng
   > plain-text. Nếu repo là **Public**, KHÔNG nên đẩy mật khẩu thật lên —
   > hãy để giá trị mẫu trong repo, rồi sau khi tải bản build về, tự sửa
   > `appsettings.json` thật ở máy chạy server (không commit lại mật khẩu
   > thật). Muốn chắc ăn thì cứ để repo ở chế độ **Private**.
3. Vào tab **Actions** trên trang GitHub của repo → sẽ thấy workflow
   "Build FileServer" tự chạy sau khi push (mất khoảng 1-2 phút).
4. Khi chạy xong (dấu tick xanh), bấm vào lần chạy đó → kéo xuống mục
   **Artifacts** → tải về 2 file zip: `FileServer-windows` và
   `FileServer-linux`.
5. Giải nén ra, sửa `appsettings.json` cho đúng (mật khẩu, thư mục, cổng),
   rồi chạy theo hướng dẫn ở mục 4 (Windows) hoặc mục "chạy trên Ubuntu"
   ở trên — không cần cài .NET SDK trên máy chạy server hay máy bạn dùng
   hàng ngày.

Nếu muốn build lại thủ công bất cứ lúc nào (không cần push code mới): vào
tab **Actions** → chọn workflow "Build FileServer" → bấm **Run workflow**.

## Giao diện quản trị trên trình duyệt

Truy cập `http://<ip-server>:<port>/` bằng trình duyệt, đăng nhập bằng
đúng Username/Password đã cấu hình. Trang sẽ hiện 3 tab:

- **Danh sách file** — xem và tải file, giống như trước.
- **Đường dẫn truy cập** — copy nhanh link LAN/Public kèm sẵn cổng, để
  gửi cho người khác hoặc dán vào IDM.
- **Cài đặt** — sửa trực tiếp Username, Password, thư mục chia sẻ, cổng,
  và các tùy chọn khác. Sau khi bấm **Luu cai dat**, bấm tiếp
  **Khoi dong lai ngay** để chương trình tự khởi động lại và áp dụng thay
  đổi (không cần vào tận máy chủ để tắt/bật lại thủ công).

> Lưu ý: nếu đổi Port trong tab Cài đặt, sau khi khởi động lại bạn cần
> truy cập lại bằng **cổng mới**. Nếu đổi Username/Password, lần truy cập
> tiếp theo trình duyệt sẽ hỏi đăng nhập lại với thông tin mới.

## Lưu ý bảo mật

- Không nên để `appsettings.json` (chứa mật khẩu dạng plain-text) public ra
  ngoài — chỉ để trên server, không commit lên nơi công khai.
- Nên chạy sau NAT/firewall, chỉ mở đúng cổng cần thiết.
- Nếu cần bảo mật cao hơn, cân nhắc thêm HTTPS (cần thêm cấu hình
  certificate, phức tạp hơn — báo lại nếu bạn cần bản có HTTPS).
