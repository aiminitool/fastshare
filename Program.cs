using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace FileServer;

internal class AppConfig
{
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "matkhau_cua_ban";
    public string SafeDir { get; set; } = "C:/backup/files";
    public int Port { get; set; } = 96;
    public bool AutoOpenFirewall { get; set; } = true;
    public bool ShowNetworkInfo { get; set; } = true;
}

internal static class Program
{
    private static AppConfig _cfg = new();
    private static List<string> _lanIPs = new();
    private static string? _publicIP;

    private static async Task Main()
    {
        try
        {
            await RunServer();
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("==== LOI KHONG MONG MUON ====");
            Console.WriteLine(ex.ToString());
            PauseIfInteractive();
        }
    }

    private static void PauseIfInteractive()
    {
        if (!Console.IsInputRedirected)
        {
            Console.WriteLine();
            Console.WriteLine("Nhan phim bat ky de dong cua so...");
            Console.ReadKey();
        }
    }

    private static async Task RunServer()
    {
        LoadConfig();
        ThreadPool.SetMinThreads(64, 64);

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{_cfg.Port}/");

        if (_cfg.AutoOpenFirewall)
        {
            TryOpenFirewall(_cfg.Port);
        }

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.WriteLine("Khong the mo cong " + _cfg.Port + ": " + ex.Message);
            Console.WriteLine("Nguyen nhan thuong gap:");
            Console.WriteLine("  1. Cong dang bi chiem boi chuong trinh khac, hoac FileServer da chay san.");
            Console.WriteLine("  2. Tren Windows, cong < 1024 can quyen Administrator, hoac dang ky truoc:");
            Console.WriteLine("     netsh http add urlacl url=http://+:" + _cfg.Port + "/ user=Everyone");
            Console.WriteLine("  3. Tren Linux, cong < 1024 can chay bang sudo hoac cap quyen (setcap).");
            PauseIfInteractive();
            return;
        }

        // Do IP LAN/Public 1 lan luc khoi dong, cache lai de trang web dung khong can goi lai
        _lanIPs = GetLanIPv4Addresses();
        _publicIP = await GetPublicIPAsync();

        Console.WriteLine("File server dang chay tai cong " + _cfg.Port);
        Console.WriteLine("Thu muc chia se: " + _cfg.SafeDir);

        if (_cfg.ShowNetworkInfo)
        {
            PrintAccessLinksToConsole(_cfg.Port);
        }

        Console.WriteLine("Trang quan tri (mo bang trinh duyet, dang nhap bang Username/Password):");
        string adminHost = _lanIPs.Count > 0 ? _lanIPs[0] : "localhost";
        Console.WriteLine($"  http://{adminHost}:{_cfg.Port}/");
        Console.WriteLine("Nhan Ctrl+C de dung.");

        while (true)
        {
            var context = await listener.GetContextAsync();
            _ = Task.Run(() => HandleRequestSafe(context));
        }
    }

    // ==================== CAU HINH ====================

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    private static void LoadConfig()
    {
        if (File.Exists(ConfigPath))
        {
            string json = File.ReadAllText(ConfigPath);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (loaded != null) _cfg = loaded;
        }
        else
        {
            Console.WriteLine("Khong tim thay appsettings.json, dung cau hinh mac dinh.");
        }
    }

    private static void SaveConfig()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(_cfg, options);
        File.WriteAllText(ConfigPath, json);
    }

    // ==================== FIREWALL ====================

    private static void TryOpenFirewall(int port)
    {
        try
        {
            if (OperatingSystem.IsWindows()) OpenFirewallWindows(port);
            else if (OperatingSystem.IsLinux()) OpenFirewallLinux(port);
            else Console.WriteLine("He dieu hanh nay chua ho tro tu dong mo firewall.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Khong the tu dong mo firewall: " + ex.Message);
        }
    }

    private static void OpenFirewallWindows(int port)
    {
        string ruleName = $"FileServer_{port}";
        RunProcess("netsh", $"advfirewall firewall delete rule name=\"{ruleName}\"", ignoreErrors: true);
        int code = RunProcess("netsh",
            $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port}",
            ignoreErrors: true);

        if (code == 0)
            Console.WriteLine($"Da tu dong mo cong {port} tren Windows Firewall.");
        else
        {
            Console.WriteLine($"Khong the tu mo Windows Firewall (can chay voi quyen Administrator).");
            Console.WriteLine($"  netsh advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port}");
        }
    }

    private static void OpenFirewallLinux(int port)
    {
        int checkCode = RunProcess("which", "ufw", ignoreErrors: true, silent: true);
        if (checkCode != 0)
        {
            Console.WriteLine($"Khong tim thay ufw, bo qua tu dong mo firewall. Neu can: sudo ufw allow {port}/tcp");
            return;
        }

        int code = RunProcess("ufw", $"allow {port}/tcp", ignoreErrors: true);
        if (code == 0)
            Console.WriteLine($"Da tu dong mo cong {port} tren ufw.");
        else
            Console.WriteLine($"Khong the tu mo ufw (co the can sudo). Lenh thu cong: sudo ufw allow {port}/tcp");
    }

    private static int RunProcess(string fileName, string arguments, bool ignoreErrors = false, bool silent = false)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            process!.WaitForExit();
            return process.ExitCode;
        }
        catch
        {
            if (ignoreErrors) return -1;
            throw;
        }
    }

    private static void RestartApplication()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (exePath != null)
            {
                Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
            }
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    // ==================== THONG TIN MANG ====================

    private static void PrintAccessLinksToConsole(int port)
    {
        Console.WriteLine();
        Console.WriteLine("==== DIA CHI TRUY CAP (TAI FILE) ====");
        if (_lanIPs.Count > 0)
        {
            Console.WriteLine("Trong mang LAN:");
            foreach (var ip in _lanIPs) Console.WriteLine($"  http://{ip}:{port}/");
        }
        else
        {
            Console.WriteLine("Khong tim thay dia chi IP LAN nao.");
        }

        if (_publicIP != null)
        {
            Console.WriteLine("Tu Internet ben ngoai (can mo/forward port tren router neu can):");
            Console.WriteLine($"  http://{_publicIP}:{port}/");
        }
        else
        {
            Console.WriteLine("Khong lay duoc Public IP (co the khong co Internet hoac bi chan).");
        }
        Console.WriteLine("========================================");
        Console.WriteLine();
    }

    private static List<string> GetLanIPv4Addresses()
    {
        var result = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    result.Add(addr.Address.ToString());
            }
        }
        return result.Distinct().ToList();
    }

    private static async Task<string?> GetPublicIPAsync()
    {
        string[] services = { "https://api.ipify.org", "https://icanhazip.com", "https://ifconfig.me/ip" };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };

        foreach (var url in services)
        {
            try
            {
                string result = (await http.GetStringAsync(url)).Trim();
                if (!string.IsNullOrEmpty(result)) return result;
            }
            catch { /* thu dich vu tiep theo */ }
        }
        return null;
    }

    // ==================== DINH TUYEN REQUEST ====================

    private static async Task HandleRequestSafe(HttpListenerContext context)
    {
        try
        {
            await HandleRequest(context);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Loi xu ly request: " + ex.Message);
            try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
        }
    }

    private static async Task HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        if (!IsAuthorized(request))
        {
            response.StatusCode = 401;
            response.Headers.Add("WWW-Authenticate", "Basic realm=\"File Manager\"");
            byte[] body = Encoding.UTF8.GetBytes("Ban can dang nhap de truy cap.");
            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body);
            response.Close();
            return;
        }

        string path = request.Url?.AbsolutePath ?? "/";

        if (request.HttpMethod == "POST" && path == "/settings")
        {
            await HandleSaveSettings(request, response);
            return;
        }

        if (request.HttpMethod == "POST" && path == "/restart")
        {
            await HandleRestart(response);
            return;
        }

        if (path == "/refresh-public-ip")
        {
            _publicIP = await GetPublicIPAsync();
            response.StatusCode = 302;
            response.RedirectLocation = "/?tab=links";
            response.Close();
            return;
        }

        var query = ParseFormEncoded(request.Url?.Query);

        if (query.TryGetValue("download", out var downloadName) && !string.IsNullOrEmpty(downloadName))
        {
            await ServeFileWithRange(request, response, downloadName);
            return;
        }

        query.TryGetValue("tab", out var initialTab);
        ServeDashboard(response, string.IsNullOrEmpty(initialTab) ? "files" : initialTab);
    }

    private static bool IsAuthorized(HttpListenerRequest request)
    {
        string? header = request.Headers["Authorization"];
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            string encoded = header.Substring("Basic ".Length).Trim();
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            int sep = decoded.IndexOf(':');
            if (sep < 0) return false;

            string user = decoded[..sep];
            string pass = decoded[(sep + 1)..];
            return user == _cfg.Username && pass == _cfg.Password;
        }
        catch { return false; }
    }

    private static Dictionary<string, string> ParseFormEncoded(string? text)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(text)) return result;

        text = text.TrimStart('?');
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            string key = WebUtility.UrlDecode(parts[0]);
            string value = parts.Length > 1 ? WebUtility.UrlDecode(parts[1]) : "";
            result[key] = value;
        }
        return result;
    }

    private static async Task<Dictionary<string, string>> ReadFormBodyAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        string body = await reader.ReadToEndAsync();
        return ParseFormEncoded(body);
    }

    // ==================== TRANG QUAN TRI (DASHBOARD) ====================

    private const string SharedStyle = @"
        body{font-family:Arial,sans-serif;max-width:800px;margin:40px auto;padding:0 16px;color:#222;}
        h1{font-size:1.4em;margin-bottom:4px;}
        .subtitle{color:#888;margin-bottom:20px;font-size:0.9em;}
        .tabs{display:flex;gap:4px;border-bottom:2px solid #eee;margin-bottom:20px;}
        .tab-btn{padding:10px 18px;border:none;background:none;cursor:pointer;font-size:1em;color:#666;border-bottom:3px solid transparent;}
        .tab-btn.active{color:#0066cc;border-bottom:3px solid #0066cc;font-weight:bold;}
        .tab-content{display:none;}
        .tab-content.active{display:block;}
        ul{list-style:none;padding:0;}
        li{padding:8px 0;border-bottom:1px solid #eee;}
        a{text-decoration:none;color:#0066cc;}
        a:hover{text-decoration:underline;}
        .folder{color:#c9950a;}.file{color:#333;}
        .size{color:#888;font-size:0.9em;margin-left:6px;}
        .tag{display:inline-block;width:52px;font-size:0.8em;color:#999;}
        .link-row{display:flex;align-items:center;gap:8px;background:#f7f7f8;padding:10px 14px;border-radius:6px;margin-bottom:8px;}
        .link-row code{flex:1;font-size:0.95em;word-break:break-all;}
        .copy-btn{padding:6px 12px;border:1px solid #ccc;background:#fff;border-radius:4px;cursor:pointer;font-size:0.85em;}
        .copy-btn:hover{background:#eee;}
        .section-title{font-weight:bold;margin:18px 0 8px;}
        form.settings label{display:block;margin-top:14px;font-size:0.9em;color:#444;}
        form.settings input[type=text],form.settings input[type=password],form.settings input[type=number]{
            width:100%;padding:8px;margin-top:4px;box-sizing:border-box;border:1px solid #ccc;border-radius:4px;}
        form.settings .checkbox-row{margin-top:14px;display:flex;align-items:center;gap:8px;}
        form.settings button{margin-top:22px;padding:10px 20px;background:#0066cc;color:#fff;border:none;border-radius:4px;cursor:pointer;}
        form.settings button:hover{background:#0052a3;}
        .hint{color:#888;font-size:0.85em;margin-top:4px;}
        .banner{background:#eaf7ea;border:1px solid #b7e0b7;padding:12px 16px;border-radius:6px;margin-bottom:16px;}
        .btn-secondary{display:inline-block;margin-top:10px;padding:8px 16px;background:#fff;border:1px solid #ccc;border-radius:4px;color:#333;cursor:pointer;}
    ";

    private static void ServeDashboard(HttpListenerResponse response, string initialTab)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang='vi'><head><meta charset='UTF-8'>");
        sb.Append("<meta name='viewport' content='width=device-width, initial-scale=1'>");
        sb.Append("<title>FileServer - Quan tri</title><style>").Append(SharedStyle).Append("</style></head><body>");

        sb.Append("<h1>FileServer</h1>");
        sb.Append("<div class='subtitle'>Thu muc chia se: ").Append(WebUtility.HtmlEncode(_cfg.SafeDir)).Append("</div>");

        sb.Append("<div class='tabs'>");
        sb.Append("<button class='tab-btn' data-tab='files' onclick=\"showTab('files')\">Danh sach file</button>");
        sb.Append("<button class='tab-btn' data-tab='links' onclick=\"showTab('links')\">Duong dan truy cap</button>");
        sb.Append("<button class='tab-btn' data-tab='settings' onclick=\"showTab('settings')\">Cai dat</button>");
        sb.Append("</div>");

        // ---- Tab: Files ----
        sb.Append("<div id='tab-files' class='tab-content'>");
        AppendFileListing(sb);
        sb.Append("</div>");

        // ---- Tab: Links ----
        sb.Append("<div id='tab-links' class='tab-content'>");
        AppendAccessLinks(sb);
        sb.Append("</div>");

        // ---- Tab: Settings ----
        sb.Append("<div id='tab-settings' class='tab-content'>");
        AppendSettingsForm(sb);
        sb.Append("</div>");

        sb.Append(@"
        <script>
        function showTab(name) {
            document.querySelectorAll('.tab-content').forEach(el => el.classList.remove('active'));
            document.querySelectorAll('.tab-btn').forEach(el => el.classList.remove('active'));
            document.getElementById('tab-' + name).classList.add('active');
            document.querySelector(""[data-tab='"" + name + ""']"").classList.add('active');
        }
        function copyLink(text, btn) {
            navigator.clipboard.writeText(text).then(() => {
                const old = btn.innerText;
                btn.innerText = 'Da copy!';
                setTimeout(() => btn.innerText = old, 1500);
            });
        }
        showTab('" + initialTab + @"');
        </script>");

        sb.Append("</body></html>");

        byte[] buffer = Encoding.UTF8.GetBytes(sb.ToString());
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.Close();
    }

    private static void AppendFileListing(StringBuilder sb)
    {
        if (!Directory.Exists(_cfg.SafeDir))
        {
            sb.Append("<p style='color:red;'>Khong tim thay thu muc: ")
              .Append(WebUtility.HtmlEncode(_cfg.SafeDir)).Append("</p>");
            return;
        }

        sb.Append("<ul>");
        foreach (var dir in Directory.GetDirectories(_cfg.SafeDir))
        {
            string name = Path.GetFileName(dir);
            sb.Append("<li class='folder'><span class='tag'>[DIR]</span>")
              .Append(WebUtility.HtmlEncode(name)).Append("</li>");
        }
        foreach (var file in Directory.GetFiles(_cfg.SafeDir))
        {
            string name = Path.GetFileName(file);
            long bytes = new FileInfo(file).Length;
            double mb = Math.Round(bytes / 1024.0 / 1024.0, 1);
            string sizeLabel = mb >= 1024 ? Math.Round(mb / 1024.0, 2) + " GB" : mb + " MB";

            sb.Append("<li class='file'><span class='tag'>[FILE]</span><a href='?download=")
              .Append(Uri.EscapeDataString(name)).Append("'>")
              .Append(WebUtility.HtmlEncode(name)).Append("</a>")
              .Append("<span class='size'>(").Append(sizeLabel).Append(")</span></li>");
        }
        sb.Append("</ul>");
    }

    private static void AppendAccessLinks(StringBuilder sb)
    {
        sb.Append("<div class='section-title'>Trong mang LAN (dung khi may tai cung mang/wifi)</div>");
        if (_lanIPs.Count == 0)
        {
            sb.Append("<p style='color:#888;'>Khong tim thay dia chi IP LAN nao.</p>");
        }
        else
        {
            foreach (var ip in _lanIPs)
            {
                string url = $"http://{ip}:{_cfg.Port}/";
                sb.Append("<div class='link-row'><code>").Append(url).Append("</code>")
                  .Append("<button class='copy-btn' onclick=\"copyLink('").Append(url)
                  .Append("', this)\">Copy</button></div>");
            }
        }

        sb.Append("<div class='section-title'>Tu Internet ben ngoai (can mo/forward port tren router neu can)</div>");
        if (_publicIP != null)
        {
            string url = $"http://{_publicIP}:{_cfg.Port}/";
            sb.Append("<div class='link-row'><code>").Append(url).Append("</code>")
              .Append("<button class='copy-btn' onclick=\"copyLink('").Append(url)
              .Append("', this)\">Copy</button></div>");
        }
        else
        {
            sb.Append("<p style='color:#888;'>Khong lay duoc Public IP.</p>");
        }
        sb.Append("<a class='btn-secondary' href='/refresh-public-ip'>Kiem tra lai Public IP</a>");
    }

    private static void AppendSettingsForm(StringBuilder sb)
    {
        sb.Append("<form class='settings' method='POST' action='/settings'>");

        sb.Append("<label>Ten dang nhap (Username)</label>");
        sb.Append("<input type='text' name='username' value='").Append(WebUtility.HtmlEncode(_cfg.Username)).Append("' required>");

        sb.Append("<label>Mat khau (Password)</label>");
        sb.Append("<input type='password' name='password' placeholder='De trong neu khong doi mat khau'>");
        sb.Append("<div class='hint'>De trong o nay se giu nguyen mat khau hien tai.</div>");

        sb.Append("<label>Thu muc chia se (SafeDir)</label>");
        sb.Append("<input type='text' name='safedir' value='").Append(WebUtility.HtmlEncode(_cfg.SafeDir)).Append("' required>");

        sb.Append("<label>Cong (Port)</label>");
        sb.Append("<input type='number' name='port' value='").Append(_cfg.Port).Append("' min='1' max='65535' required>");

        sb.Append("<div class='checkbox-row'><input type='checkbox' id='autofw' name='autoopenfirewall' ")
          .Append(_cfg.AutoOpenFirewall ? "checked" : "").Append("><label for='autofw' style='margin:0;'>Tu dong mo cong tren firewall khi khoi dong</label></div>");

        sb.Append("<div class='checkbox-row'><input type='checkbox' id='shownet' name='shownetworkinfo' ")
          .Append(_cfg.ShowNetworkInfo ? "checked" : "").Append("><label for='shownet' style='margin:0;'>Hien thi dia chi IP tren console khi khoi dong</label></div>");

        sb.Append("<button type='submit'>Luu cai dat</button>");
        sb.Append("</form>");
        sb.Append("<div class='hint' style='margin-top:16px;'>Sau khi luu, can khoi dong lai chuong trinh de ap dung thay doi (nhat la Port/Username/Password).</div>");
    }

    // ==================== XU LY LUU CAI DAT / KHOI DONG LAI ====================

    private static async Task HandleSaveSettings(HttpListenerRequest request, HttpListenerResponse response)
    {
        var form = await ReadFormBodyAsync(request);

        if (form.TryGetValue("username", out var username) && !string.IsNullOrWhiteSpace(username))
            _cfg.Username = username;

        if (form.TryGetValue("password", out var password) && !string.IsNullOrEmpty(password))
            _cfg.Password = password;

        if (form.TryGetValue("safedir", out var safedir) && !string.IsNullOrWhiteSpace(safedir))
            _cfg.SafeDir = safedir;

        if (form.TryGetValue("port", out var portStr) && int.TryParse(portStr, out var port) && port > 0 && port <= 65535)
            _cfg.Port = port;

        _cfg.AutoOpenFirewall = form.ContainsKey("autoopenfirewall");
        _cfg.ShowNetworkInfo = form.ContainsKey("shownetworkinfo");

        SaveConfig();

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang='vi'><head><meta charset='UTF-8'>");
        sb.Append("<title>Da luu cai dat</title><style>").Append(SharedStyle).Append("</style></head><body>");
        sb.Append("<h1>FileServer</h1>");
        sb.Append("<div class='banner'>Da luu cai dat thanh cong. Can khoi dong lai chuong trinh de ap dung day du thay doi.</div>");
        sb.Append("<a class='btn-secondary' href='/'>Quay lai trang chinh</a>");
        sb.Append("&nbsp;&nbsp;");
        sb.Append("<form method='POST' action='/restart' style='display:inline;'>");
        sb.Append("<button type='submit' class='btn-secondary' style='background:#0066cc;color:#fff;border:none;'>Khoi dong lai ngay</button>");
        sb.Append("</form>");
        sb.Append("</body></html>");

        byte[] buffer = Encoding.UTF8.GetBytes(sb.ToString());
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.Close();
    }

    private static async Task HandleRestart(HttpListenerResponse response)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang='vi'><head><meta charset='UTF-8'>");
        sb.Append("<title>Dang khoi dong lai</title><style>").Append(SharedStyle).Append("</style></head><body>");
        sb.Append("<h1>FileServer</h1>");
        sb.Append("<div class='banner'>Dang khoi dong lai server, vui long doi vai giay roi tai lai trang nay...</div>");
        sb.Append("</body></html>");

        byte[] buffer = Encoding.UTF8.GetBytes(sb.ToString());
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer);
        response.Close();

        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            RestartApplication();
        });
    }

    // ==== TAI FILE HO TRO RANGE / RESUME ====
    private static async Task ServeFileWithRange(HttpListenerRequest request, HttpListenerResponse response, string requestedName)
    {
        string safeName = Path.GetFileName(requestedName);
        string filePath = Path.Combine(_cfg.SafeDir, safeName);

        if (!File.Exists(filePath))
        {
            response.StatusCode = 404;
            byte[] msg = Encoding.UTF8.GetBytes("File khong ton tai.");
            response.ContentLength64 = msg.Length;
            await response.OutputStream.WriteAsync(msg);
            response.Close();
            return;
        }

        long fileSize = new FileInfo(filePath).Length;
        long start = 0;
        long end = fileSize - 1;

        string? rangeHeader = request.Headers["Range"];
        bool isPartial = false;

        if (!string.IsNullOrEmpty(rangeHeader))
        {
            var match = Regex.Match(rangeHeader, @"bytes=(\d*)-(\d*)");
            if (match.Success)
            {
                if (match.Groups[1].Value != "") start = long.Parse(match.Groups[1].Value);
                if (match.Groups[2].Value != "") end = long.Parse(match.Groups[2].Value);
                isPartial = true;
            }
        }

        long length = end - start + 1;

        response.ContentType = "application/octet-stream";
        response.AddHeader("Content-Disposition", "attachment; filename=\"" + safeName + "\"");
        response.AddHeader("Accept-Ranges", "bytes");
        response.ContentLength64 = length;

        if (isPartial)
        {
            response.StatusCode = 206;
            response.AddHeader("Content-Range", $"bytes {start}-{end}/{fileSize}");
        }
        else
        {
            response.StatusCode = 200;
        }

        const int bufferSize = 4 * 1024 * 1024;
        byte[] buffer = new byte[bufferSize];

        try
        {
            using var fs = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            fs.Seek(start, SeekOrigin.Begin);

            long remaining = length;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(bufferSize, remaining);
                int read = await fs.ReadAsync(buffer.AsMemory(0, toRead));
                if (read <= 0) break;

                await response.OutputStream.WriteAsync(buffer.AsMemory(0, read));
                remaining -= read;
            }
        }
        catch (HttpListenerException) { }
        catch (IOException) { }
        finally
        {
            response.Close();
        }
    }
}
