using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AudioBookPlayer.Network;

/// <summary>共享出去的一章（一个音频文件 + 可能有的字幕）。</summary>
public sealed class ShareChapter
{
    public string Title { get; init; } = "";
    public long Size { get; init; }
    public bool HasSubtitle { get; init; }
    public string AudioName { get; init; } = "";
    public string SubtitleName { get; init; } = "";
}

/// <summary>共享出去的一本书。</summary>
public sealed class ShareBook
{
    public string Title { get; init; } = "";
    public string Folder { get; init; } = "";
    public bool HasCover { get; init; }
    public string CoverName { get; init; } = "";
    public List<ShareChapter> Chapters { get; init; } = new();
    public long TotalSize => Chapters.Sum(c => c.Size);
}

/// <summary>
/// 局域网共享服务：让手机在同一 Wi-Fi（或电脑连手机热点）下直接把书拉过去。
///
/// 为什么用裸 <see cref="TcpListener"/> 而不是 HttpListener：
///   HttpListener 绑定 http://+:8765/ 需要管理员权限或 netsh urlacl；
///   这里直接监听 0.0.0.0，普通用户权限就能跑，也不改系统配置。
/// 只实现手机端用到的那几个 GET，协议细节保持最小。
/// </summary>
public sealed class LanShareServer : IDisposable
{
    public const int DefaultPort = 8765;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly object _gate = new();

    public int Port { get; private set; }
    public bool IsRunning
    {
        get { lock (_gate) { return _listener != null; } }
    }

    /// <summary>每次请求时现取书库，避免共享期间扫描结果过期。</summary>
    public Func<IReadOnlyList<ShareBookEntry>>? LibraryProvider { get; set; }

    public LanShareServer(int port = DefaultPort)
    {
        Port = port;
    }

    public sealed class ShareBookEntry
    {
        public ShareBook Info { get; init; } = new();
        public List<string> AudioPaths { get; init; } = new();
        public List<string> SubtitlePaths { get; init; } = new();
        public string? CoverPath { get; init; }
    }

    /// <summary>本机所有可用于访问的 URL（有线/无线 IPv4 + 环回）。</summary>
    public IReadOnlyList<string> Urls
    {
        get
        {
            var list = new List<string>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    list.Add($"http://{addr.Address}:{Port}");
                }
            }
            list.Add($"http://127.0.0.1:{Port}");
            return list;
        }
    }

    /// <summary>启动监听；端口被占（比如另开了一个服务）就往后找一个空闲端口。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_listener != null) return;

            Exception? last = null;
            for (var offset = 0; offset < 10; offset++)
            {
                var port = Port + offset;
                try
                {
                    var listener = new TcpListener(IPAddress.Any, port);
                    listener.Start();
                    _listener = listener;
                    Port = port;
                    _cts = new CancellationTokenSource();
                    _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            throw new InvalidOperationException(
                $"端口 {Port}~{Port + 9} 都被占用，换一个端口或先关掉占用的程序", last);
        }
    }

    public void Stop()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            listener = _listener;
            cts = _cts;
            _listener = null;
            _cts = null;
        }

        try { cts?.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        cts?.Dispose();
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception) { continue; }

            _ = Task.Run(() => HandleClientAsync(client), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();

                var request = await ReadRequestAsync(stream).ConfigureAwait(false);
                if (request == null) return;

                var (method, target, headers) = request.Value;
                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteTextAsync(stream, 405, "text/plain; charset=utf-8", "method not allowed").ConfigureAwait(false);
                    return;
                }

                var path = target;
                var query = "";
                var q = target.IndexOf('?');
                if (q >= 0)
                {
                    path = target[..q];
                    query = target[(q + 1)..];
                }

                if (path == "/" || path == "/index.html")
                {
                    await WriteTextAsync(stream, 200, "text/html; charset=utf-8", BuildIndexHtml()).ConfigureAwait(false);
                    return;
                }

                if (path == "/api/library")
                {
                    var json = JsonSerializer.Serialize(BuildLibraryDto(), JsonOpts);
                    await WriteTextAsync(stream, 200, "application/json; charset=utf-8", json).ConfigureAwait(false);
                    return;
                }

                if (path == "/api/file")
                {
                    await ServeFileAsync(stream, query, headers, isHead: method == "HEAD").ConfigureAwait(false);
                    return;
                }

                if (path == "/api/cover")
                {
                    await ServeCoverAsync(stream, query).ConfigureAwait(false);
                    return;
                }

                await WriteTextAsync(stream, 404, "text/plain; charset=utf-8", "not found").ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 手机中途断开之类，直接忽略，不要让服务挂掉
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private object BuildLibraryDto()
    {
        var entries = LibraryProvider?.Invoke() ?? Array.Empty<ShareBookEntry>();
        return new
        {
            name = "KATARU",
            version = 1,
            books = entries.Select((e, i) => new
            {
                index = i,
                title = e.Info.Title,
                folder = e.Info.Folder,
                hasCover = e.Info.HasCover,
                totalSize = e.Info.TotalSize,
                chapters = e.Info.Chapters.Select((c, ci) => new
                {
                    index = ci,
                    title = c.Title,
                    size = c.Size,
                    hasSubtitle = c.HasSubtitle,
                    audioName = c.AudioName,
                    subtitleName = c.SubtitleName
                })
            })
        };
    }

    private async Task ServeFileAsync(NetworkStream stream, string query, Dictionary<string, string> headers, bool isHead)
    {
        var bookIndex = ParseInt(QueryValue(query, "book"), -1);
        var chapterIndex = ParseInt(QueryValue(query, "chapter"), -1);
        var kind = QueryValue(query, "kind");
        if (string.IsNullOrEmpty(kind)) kind = "audio";

        var entries = LibraryProvider?.Invoke() ?? Array.Empty<ShareBookEntry>();
        if (bookIndex < 0 || bookIndex >= entries.Count)
        {
            await WriteTextAsync(stream, 404, "text/plain; charset=utf-8", "no such book").ConfigureAwait(false);
            return;
        }

        var entry = entries[bookIndex];
        if (chapterIndex < 0 || chapterIndex >= entry.AudioPaths.Count)
        {
            await WriteTextAsync(stream, 404, "text/plain; charset=utf-8", "no such chapter").ConfigureAwait(false);
            return;
        }

        var filePath = kind == "subtitle"
            ? (chapterIndex < entry.SubtitlePaths.Count ? entry.SubtitlePaths[chapterIndex] : "")
            : entry.AudioPaths[chapterIndex];

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            await WriteTextAsync(stream, 404, "text/plain; charset=utf-8", "file missing").ConfigureAwait(false);
            return;
        }

        await ServePathAsync(stream, filePath, headers, isHead).ConfigureAwait(false);
    }

    private async Task ServeCoverAsync(NetworkStream stream, string query)
    {
        var bookIndex = ParseInt(QueryValue(query, "book"), -1);
        var entries = LibraryProvider?.Invoke() ?? Array.Empty<ShareBookEntry>();
        if (bookIndex < 0 || bookIndex >= entries.Count)
        {
            await WriteTextAsync(stream, 404, "text/plain; charset=utf-8", "no such book").ConfigureAwait(false);
            return;
        }

        var cover = entries[bookIndex].CoverPath;
        if (string.IsNullOrEmpty(cover) || !File.Exists(cover))
        {
            await WriteTextAsync(stream, 404, "text/plain; charset=utf-8", "no cover").ConfigureAwait(false);
            return;
        }

        await ServePathAsync(stream, cover, new Dictionary<string, string>(), isHead: false).ConfigureAwait(false);
    }

    /// <summary>带 Range 支持的文件下发，手机端可以做断点续传/校验长度。</summary>
    private static async Task ServePathAsync(NetworkStream stream, string filePath, Dictionary<string, string> headers, bool isHead)
    {
        var info = new FileInfo(filePath);
        var total = info.Length;
        var contentType = GuessContentType(filePath);

        long start = 0;
        long length = total;
        var partial = false;

        if (headers.TryGetValue("range", out var rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = rangeHeader[6..];
            var dash = spec.IndexOf('-');
            if (dash >= 0)
            {
                var startText = spec[..dash].Trim();
                var endText = spec[(dash + 1)..].Trim();
                if (startText.Length == 0 && endText.Length > 0)
                {
                    // bytes=-N 取最后 N 字节
                    if (long.TryParse(endText, out var tail) && tail > 0)
                    {
                        start = Math.Max(0, total - tail);
                        length = total - start;
                        partial = true;
                    }
                }
                else if (long.TryParse(startText, out var s))
                {
                    start = Math.Clamp(s, 0, Math.Max(0, total - 1));
                    long e = total - 1;
                    if (endText.Length > 0 && long.TryParse(endText, out var parsedEnd))
                    {
                        e = Math.Clamp(parsedEnd, start, total - 1);
                    }
                    length = e - start + 1;
                    partial = true;
                }
            }
        }

        var sb = new StringBuilder();
        sb.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(length).Append("\r\n");
        sb.Append("Accept-Ranges: bytes\r\n");
        sb.Append("Connection: close\r\n");
        if (partial)
        {
            sb.Append("Content-Range: bytes ").Append(start).Append('-').Append(start + length - 1)
              .Append('/').Append(total).Append("\r\n");
        }
        sb.Append("\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes).ConfigureAwait(false);

        if (isHead) return;

        using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        file.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            var want = (int)Math.Min(buffer.Length, remaining);
            var read = await file.ReadAsync(buffer.AsMemory(0, want)).ConfigureAwait(false);
            if (read <= 0) break;
            await stream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static string GuessContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".srt" => "text/plain; charset=utf-8",
            ".m4b" => "audio/mp4",
            ".m4a" => "audio/mp4",
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            ".opus" => "audio/ogg",
            ".ogg" => "audio/ogg",
            ".wav" => "audio/wav",
            _ => "application/octet-stream"
        };
    }

    private static async Task<(string Method, string Target, Dictionary<string, string> Headers)?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total)).ConfigureAwait(false);
            if (read <= 0) return null;
            total += read;
            // 头部读完（\r\n\r\n）就停
            if (total >= 4 && buffer[total - 1] == (byte)'\n')
            {
                var text = Encoding.ASCII.GetString(buffer, 0, total);
                if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) break;
            }
        }

        var head = Encoding.ASCII.GetString(buffer, 0, total);
        var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;

        var parts = lines[0].Split(' ');
        if (parts.Length < 3) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        return (parts[0], parts[1], headers);
    }

    private static async Task WriteTextAsync(NetworkStream stream, int status, string contentType, string body)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var reason = status switch
        {
            200 => "OK",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "Error"
        };

        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
        await stream.WriteAsync(payload).ConfigureAwait(false);
    }

    private static string QueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query)) return "";
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (!pair[..eq].Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return "";
    }

    private static int ParseInt(string text, int fallback)
        => int.TryParse(text, out var value) ? value : fallback;

    private string BuildIndexHtml()
    {
        var entries = LibraryProvider?.Invoke() ?? Array.Empty<ShareBookEntry>();
        var sb = new StringBuilder();
        sb.Append("<!doctype html><meta charset=\"utf-8\"><title>KATARU 局域网共享</title>");
        sb.Append("<style>body{font-family:system-ui;margin:24px;background:#121011;color:#f1ebe1}");
        sb.Append("h1{font-size:20px}li{margin:6px 0}a{color:#c9a227}</style>");
        sb.Append("<h1>KATARU 局域网共享</h1>");
        sb.Append("<p>手机连同一个 Wi-Fi（或电脑连手机热点），在 App 里填这个地址即可。</p>");
        sb.Append("<p>可用地址：<br>");
        foreach (var url in Urls) sb.Append("<b>").Append(url).Append("</b><br>");
        sb.Append("</p><h2>书库（").Append(entries.Count).Append(" 本）</h2><ul>");
        foreach (var entry in entries)
        {
            sb.Append("<li>").Append(WebUtility.HtmlEncode(entry.Info.Title))
              .Append(" — ").Append(entry.Info.Chapters.Count).Append(" 章 / ")
              .Append(FormatSize(entry.Info.TotalSize)).Append("</li>");
        }
        sb.Append("</ul>");
        return sb.ToString();
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
