using System.Net;
using System.Text;
using Hexa.Models;

namespace Hexa.Services;

/// <summary>
/// 脚本库的「网页版」：浏览器打开 <c>http://localhost:8582/</c> 就能看到库里的脚本，点一下设备就播。
/// 手机、平板、VR 头显里的浏览器都能用（同一台机器上访问 localhost 即可）。
///
/// 安全边界（刻意的）：
///   · 只绑回环地址，不暴露到局域网；
///   · 只提供三件事 —— 列出 / 播放 / 停止，**没有任何"写任意轴"的入口**；
///   · 播放走的是和其它页面同一条 <see cref="FunscriptPlayerService"/>，所以急停、限位、舒适档、
///     让位关系照常生效，不绕开任何一道闸；
///   · 待播文件必须落在脚本库目录内（挡住构造路径读别的文件）。
/// </summary>
public sealed class LibraryWebService : IDisposable
{
    public const int Port = 8582;

    private readonly FunscriptPlayerService _player;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>累计点了多少次播放（界面上用来确认"网页真的连上来了"）。</summary>
    public long Plays;

    public string LastError = "";
    public bool IsRunning => _listener?.IsListening == true;
    public string Url => $"http://localhost:{Port}/";

    public LibraryWebService(FunscriptPlayerService player) => _player = player;

    public void Start()
    {
        if (IsRunning) return;
        Stop();

        var cts = new CancellationTokenSource();
        HttpListener? listener = TryListen(Port);
        if (listener is null)
        {
            cts.Dispose();
            if (LastError.Length == 0) LastError = "端口被占用";
            AppLogger.Warn("脚本库网页启动失败：" + LastError);
            return;
        }

        _listener = listener;
        _cts = cts;
        LastError = "";
        AppLogger.Info($"脚本库网页已监听 {Url}");
        _ = Task.Run(() => LoopAsync(listener, cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
        try { _cts?.Dispose(); } catch { }
        _cts = null;
    }

    public void Dispose() => Stop();

    private HttpListener? TryListen(int port)
    {
        var listener = new HttpListener();
        foreach (string prefix in new[] { $"http://localhost:{port}/", $"http://127.0.0.1:{port}/" })
        {
            try
            {
                listener.Prefixes.Clear();
                listener.Prefixes.Add(prefix);
                listener.Start();
                return listener;
            }
            catch (Exception ex) { LastError = ex.Message; }
        }
        try { listener.Close(); } catch { }
        return null;
    }

    private async Task LoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }   // Stop() 会让它抛异常，直接退出
            _ = Task.Run(() => HandleAsync(ctx), token);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            // 带副作用的端点必须校验来源：否则任何网页写一个 <img src="http://localhost:8582/stop">
            // 就能打断正在播的脚本（浏览器发起这类请求不受同源策略约束）。
            bool sideEffect = path.Equals("/play", StringComparison.OrdinalIgnoreCase)
                           || path.Equals("/stop", StringComparison.OrdinalIgnoreCase);
            if (sideEffect && !IsAllowedBrowserOrigin(ctx.Request.Headers["Origin"]))
            {
                ctx.Response.StatusCode = 403;
                ctx.Response.Close();
                return;
            }
            if (path.Equals("/play", StringComparison.OrdinalIgnoreCase)) { HandlePlay(ctx); return; }
            if (path.Equals("/stop", StringComparison.OrdinalIgnoreCase)) { HandleStop(ctx); return; }
            await WriteHtmlAsync(ctx, BuildPage(ctx.Request.QueryString)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            try { ctx.Response.Abort(); } catch { }
        }
    }

    /// <summary>只接受本机页面的请求（原生客户端不带 Origin 头时按本机处理）。</summary>
    private static bool IsAllowedBrowserOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)) return false;
        string host = uri.Host.Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.Ordinal)
            || host.Equals("::1", StringComparison.Ordinal);
    }

    private void HandlePlay(HttpListenerContext ctx)
    {
        string requested = ctx.Request.QueryString["f"] ?? "";
        string library = Path.GetFullPath(ScriptLibrary.Folder);
        string full;
        try { full = Path.GetFullPath(requested); }
        catch { full = ""; }

        // 只允许播库目录里的文件。前缀匹配要带上目录分隔符，否则 ...\Scripts-evil\x.funscript
        // 这种兄弟目录会被当成「在库里」而放行（审计发现）。
        string libraryPrefix = library.EndsWith(Path.DirectorySeparatorChar)
            ? library : library + Path.DirectorySeparatorChar;
        if (full.Length == 0
            || !full.StartsWith(libraryPrefix, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(full))
        {
            Redirect(ctx, "/?msg=" + Uri.EscapeDataString("这个脚本不在库里"));
            return;
        }

        try
        {
            _player.Load(full);
            _player.Play();
            Interlocked.Increment(ref Plays);
            Redirect(ctx, "/?msg=" + Uri.EscapeDataString("已开始播放：" + Path.GetFileNameWithoutExtension(full)));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("网页端点播失败：" + ex.Message);
            Redirect(ctx, "/?msg=" + Uri.EscapeDataString("播不了：" + ex.Message));
        }
    }

    private void HandleStop(HttpListenerContext ctx)
    {
        try { _player.Stop(); } catch { }
        Redirect(ctx, "/?msg=" + Uri.EscapeDataString("已停止"));
    }

    private static void Redirect(HttpListenerContext ctx, string location)
    {
        ctx.Response.StatusCode = 302;
        ctx.Response.RedirectLocation = location;
        ctx.Response.Close();
    }

    private static async Task WriteHtmlAsync(HttpListenerContext ctx, string html)
    {
        byte[] body = Encoding.UTF8.GetBytes(html);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = body.Length;
        await ctx.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private string BuildPage(System.Collections.Specialized.NameValueCollection query)
    {
        var entries = ScriptLibrary.List();
        string message = query["msg"] ?? "";
        bool listening = _player.IsPlaying;

        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>Hexa 脚本库</title><style>");
        sb.Append("*{box-sizing:border-box}body{margin:0;padding:16px;background:#14131a;color:#e8e6ef;");
        sb.Append("font-family:'Segoe UI','Microsoft YaHei',sans-serif}h1{font-size:20px;margin:0 0 4px}");
        sb.Append(".sub{color:#8f8ba3;font-size:13px;margin:0 0 14px}");
        sb.Append(".msg{background:#1e2a22;border:1px solid #2f6b45;color:#8fe0ae;padding:10px 12px;");
        sb.Append("border-radius:8px;margin:0 0 14px;font-size:14px}");
        sb.Append(".item{display:block;padding:14px 16px;margin:0 0 8px;background:#1c1b24;border-radius:10px;");
        sb.Append("color:#e8e6ef;text-decoration:none;border:1px solid #2a2833}.item:active{background:#26242f}");
        sb.Append(".name{font-size:16px;display:block}.meta{color:#8f8ba3;font-size:12px;margin-top:4px;display:block}");
        sb.Append(".bad{color:#e08f8f}.stop{display:block;text-align:center;padding:16px;margin-top:16px;");
        sb.Append("background:#3a1c1c;border:1px solid #6b2f2f;color:#ffb3b3;border-radius:10px;");
        sb.Append("text-decoration:none;font-size:16px}.empty{color:#8f8ba3;padding:24px 0;text-align:center;line-height:1.7;word-break:break-word}");
        sb.Append("code{color:#b9b4cc;word-break:break-all}");
        sb.Append("</style></head><body>");
        sb.Append("<h1>📚 Hexa 脚本库</h1>");
        sb.Append($"<p class=\"sub\">共 {entries.Count} 个脚本 · 点一下就在设备上播 · {PackageHint(listening)}</p>");
        if (message.Length > 0) sb.Append($"<div class=\"msg\">{WebUtility.HtmlEncode(message)}</div>");

        if (entries.Count == 0)
        {
            sb.Append("<div class=\"empty\">库里还没有脚本。<br>把 .funscript 放进这个目录：<br><code>");
            sb.Append(WebUtility.HtmlEncode(ScriptLibrary.Folder));
            sb.Append("</code><br>或者在主界面的脚本库页用「🔍 去社区搜」找一个。</div>");
        }
        else
        {
            foreach (ScriptEntry entry in entries)
            {
                string meta = entry.Valid
                    ? $"{FormatDuration(entry.DurationMs)} · {entry.TrackCount} 轨"
                    : "<span class=\"bad\">打不开：" + WebUtility.HtmlEncode(entry.Error ?? "解析失败") + "</span>";
                sb.Append($"<a class=\"item\" href=\"/play?f={Uri.EscapeDataString(entry.FilePath)}\">");
                sb.Append($"<span class=\"name\">{WebUtility.HtmlEncode(entry.Name)}</span>");
                sb.Append($"<span class=\"meta\">{meta}</span></a>");
            }
        }

        sb.Append("<a class=\"stop\" href=\"/stop\">⏹ 全部停止</a>");
        sb.Append("<p class=\"sub\" style=\"margin-top:18px\">这个页面只在本机（localhost）可用，只提供播放/停止。</p>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private string PackageHint(bool playing) => playing ? "当前：正在播放" : "当前：没有在播";

    private static string FormatDuration(long ms)
    {
        if (ms <= 0) return "时长未知";
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
