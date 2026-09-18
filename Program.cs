using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FileServer;

internal class AppConfig
{
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "matkhau_cua_ban";
    public string SafeDir { get; set; } = "C:/backup/files";
    public int Port { get; set; } = 96;
}

internal static class Program
{
    private static AppConfig _cfg = new();

    private static async Task Main()
    {
        LoadConfig();

        var listener = new HttpListener();
        // "+" nghĩa là lắng nghe trên mọi địa chỉ IP của máy
        listener.Prefixes.Add($"http://+:{_cfg.Port}/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.WriteLine("Khong the mo cong " + _cfg.Port + ": " + ex.Message);
            Console.WriteLine("Tren Linux, cong < 1024 can chay bang sudo hoac cap quyen (setcap).");
            Console.WriteLine("Tren Windows, co the can chay Command Prompt/PowerShell voi quyen Administrator");
            Console.WriteLine("hoac dang ky URL ACL bang: netsh http add urlacl url=http://+:" + _cfg.Port + "/ user=Everyone");
            return;
        }

        Console.WriteLine("File server dang chay tai cong " + _cfg.Port);
        Console.WriteLine("Thu muc chia se: " + _cfg.SafeDir);
        Console.WriteLine("Nhan Ctrl+C de dung.");

        while (true)
        {
            var context = await listener.GetContextAsync();
            _ = Task.Run(() => HandleRequestSafe(context));
        }
    }

    private static void LoadConfig()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(path))
        {
            string json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (loaded != null) _cfg = loaded;
        }
        else
        {
            Console.WriteLine("Khong tim thay appsettings.json, dung cau hinh mac dinh.");
        }
    }

    private static async Task HandleRequestSafe(HttpListenerContext context)
    {
        try
        {
            await HandleRequest(context);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Loi xu ly request: " + ex.Message);
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch { /* bo qua */ }
        }
    }

    private static async Task HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        // ==== KIEM TRA DANG NHAP (HTTP Basic Auth) ====
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

        var query = ParseQuery(request.Url?.Query);

        if (query.TryGetValue("download", out var downloadName) && !string.IsNullOrEmpty(downloadName))
        {
            await ServeFileWithRange(request, response, downloadName);
            return;
        }

        ServeFileListing(response);
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
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(query)) return result;

        query = query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            string key = WebUtility.UrlDecode(parts[0]);
            string value = parts.Length > 1 ? WebUtility.UrlDecode(parts[1]) : "";
            result[key] = value;
        }
        return result;
    }

    // ==== TRANG LIET KE FILE ====
    private static void ServeFileListing(HttpListenerResponse response)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang='vi'><head><meta charset='UTF-8'>");
        sb.Append("<title>Quan ly file</title><style>");
        sb.Append("body{font-family:Arial,sans-serif;max-width:700px;margin:60px auto;}");
        sb.Append("h2{margin-bottom:20px;}ul{list-style:none;padding:0;}");
        sb.Append("li{padding:8px 0;border-bottom:1px solid #eee;}");
        sb.Append("a{text-decoration:none;color:#0066cc;}a:hover{text-decoration:underline;}");
        sb.Append(".folder{color:#c9950a;}.file{color:#333;}");
        sb.Append(".size{color:#888;font-size:0.9em;margin-left:6px;}");
        sb.Append(".tag{display:inline-block;width:52px;font-size:0.8em;color:#999;}");
        sb.Append("</style></head><body><h2>Danh sach file</h2>");

        if (!Directory.Exists(_cfg.SafeDir))
        {
            sb.Append("<p style='color:red;'>Khong tim thay thu muc: ")
              .Append(WebUtility.HtmlEncode(_cfg.SafeDir)).Append("</p>");
        }
        else
        {
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
                string sizeLabel = mb >= 1024
                    ? Math.Round(mb / 1024.0, 2) + " GB"
                    : mb + " MB";

                sb.Append("<li class='file'><span class='tag'>[FILE]</span><a href='?download=")
                  .Append(Uri.EscapeDataString(name)).Append("'>")
                  .Append(WebUtility.HtmlEncode(name)).Append("</a>")
                  .Append("<span class='size'>(").Append(sizeLabel).Append(")</span></li>");
            }
            sb.Append("</ul>");
        }
        sb.Append("</body></html>");

        byte[] buffer = Encoding.UTF8.GetBytes(sb.ToString());
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.Close();
    }

    // ==== TAI FILE HO TRO RANGE / RESUME ====
    private static async Task ServeFileWithRange(HttpListenerRequest request, HttpListenerResponse response, string requestedName)
    {
        // Chi lay ten file, chong path traversal (../../)
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

        const int bufferSize = 1024 * 1024; // 1MB moi lan doc
        byte[] buffer = new byte[bufferSize];

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
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
        catch (HttpListenerException)
        {
            // Client huy ket noi giua chung, bo qua
        }
        catch (IOException)
        {
            // Client dong ket noi giua chung, bo qua
        }
        finally
        {
            response.Close();
        }
    }
}
