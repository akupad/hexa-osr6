using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hexa.Models;
using Hexa.ViewModels;

namespace Hexa.Services;

/// <summary>
/// 无头 Web API — HttpListener on http://localhost:8580/
/// 零外部依赖，仅用 System.Net.HttpListener + System.Text.Json。
/// 启动时自动开始监听，退出时调用 Stop()/Dispose() 关闭。
///
/// ⚠ 待开发（WIP）：当前<b>停用</b>，见 <see cref="FeatureEnabled"/>。
/// </summary>
public sealed class WebApiService : IDisposable
{
    /// <summary>
    /// ⚠ 待开发（WIP）：本地控制接口是否对外开放。当前为 <c>false</c> —— 服务不会监听，
    /// 设置页也只显示「待开发」提示，<see cref="AppSettings.WebApiEnabled"/> 会被强制关掉。
    ///
    /// 为什么还没开放：这个接口能直接控制设备（写轴、急停、归中、切模式），
    /// 但还缺三样东西 —— ①调用方鉴权（目前只靠「仅监听 127.0.0.1」这一层）；
    /// ②访问审计与限流；③与急停/规则引擎状态的完整联动验证。
    /// 做完这三样再改成 true，并恢复设置页的开关（Views/SettingsPage.xaml）。
    /// </summary>
    public static readonly bool FeatureEnabled = false;

    private readonly MotionEngine     _engine;
    private readonly AppSettings      _cfg;
    private readonly StrokesViewModel _strokesVm;
    private readonly FunscriptPlayerService? _funscript;

    private HttpListener?            _listener;
    private CancellationTokenSource? _cts;
    private Task?                    _loopTask;

    public const int Port = 8580;
    public bool IsRunning => _listener?.IsListening == true;

    /// <summary>轴顺序（唯一真源：<see cref="Osr6DeviceProfile.InstalledAxes"/>）。</summary>
    private static readonly string[] AxisNames = Osr6DeviceProfile.InstalledAxes;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public WebApiService(
        MotionEngine engine,
        AppSettings cfg,
        StrokesViewModel strokesVm,
        FunscriptPlayerService? funscript = null)
    {
        _engine    = engine;
        _cfg       = cfg;
        _strokesVm = strokesVm;
        _funscript = funscript;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public void Start()
    {
        if (IsRunning) return;
        Stop();
        _cts      = new CancellationTokenSource();
        _listener = new HttpListener();
        // 只绑定回环地址，避免暴露到局域网
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");

        try
        {
            _listener.Start();
            AppLogger.Info($"[WebApi] 已启动，监听 http://127.0.0.1:{Port}/");
            _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            AppLogger.Error("[WebApi] 启动监听器失败", ex);
        }
    }

    public void Stop()
    {
        if (_listener == null && _cts == null) return;
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* 监听器已关闭时忽略 */ }
        try { _loopTask?.Wait(2000); } catch { }
        _listener?.Close();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _loopTask = null;
        AppLogger.Info("[WebApi] 已停止。");
    }

    public void Dispose() => Stop();

    // ── Request loop ──────────────────────────────────────────────────────

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener!.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex)
            {
                AppLogger.Error("[WebApi] Accept 错误", ex);
                break;
            }

            // 每个请求独立处理，不阻塞接收循环
            _ = Task.Run(() => HandleAsync(ctx), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req  = ctx.Request;
        var resp = ctx.Response;

        // 不再返回 CORS *，浏览器跨域调用默认被阻断
        if (req.HttpMethod == "OPTIONS")
        {
            resp.StatusCode = 204;
            resp.Close();
            return;
        }

        var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "/";

        // 控制面板页面（无敏感数据，允许匿名访问；token 由页面内输入后用于 API 调用）
        if (req.HttpMethod == "GET" && path == "/")
        {
            await WriteHtmlAsync(resp, BuildControlPanelHtml());
            return;
        }

        // 简单鉴权：只允许本机回环 + token 校验
        if (!IsAuthorized(req))
        {
            AppLogger.Warn($"[WebApi] 拒绝未授权请求: {req.RemoteEndPoint} {req.HttpMethod} {req.Url?.AbsolutePath}");
            resp.StatusCode = 403;
            await WriteJsonAsync(resp, new { error = "forbidden" });
            return;
        }

        try
        {
            AppLogger.Info($"[WebApi] {req.HttpMethod} {path}");

            if (req.HttpMethod == "GET" && path == "/status")
                await WriteJsonAsync(resp, BuildStatus());

            else if (req.HttpMethod == "POST" && path.StartsWith("/preset/"))
                await HandlePresetAsync(path["/preset/".Length..], resp);

            else if (req.HttpMethod == "POST" && path == "/intensity")
                await HandleIntensityAsync(req, resp);

            else if (req.HttpMethod == "POST" && path.StartsWith("/mode/"))
                await HandleModeAsync(path["/mode/".Length..], resp);

            else if (req.HttpMethod == "POST" && path == "/axis")
                await HandleAxisAsync(req, resp);

            else if (req.HttpMethod == "POST" && path == "/safety/arm")
                await HandleArmAsync(resp);

            else if (req.HttpMethod == "POST" && path == "/safety/disarm")
            {
                App.Dispatch(_engine.Disarm);
                await WriteJsonAsync(resp, new { ok = true, safety = _engine.SafetyStatus });
            }

            else if (req.HttpMethod == "POST" && path == "/safety/estop")
            {
                App.Dispatch(_engine.EmergencyStop);
                await WriteJsonAsync(resp, new { ok = true, safety = _engine.SafetyStatus });
            }

            else if (req.HttpMethod == "POST" && path == "/safety/home")
            {
                App.Dispatch(_engine.Home);
                await WriteJsonAsync(resp, new { ok = true, safety = _engine.SafetyStatus });
            }

            else if (req.HttpMethod == "GET" && path == "/time")
                await WriteJsonAsync(resp, BuildFunscriptState());

            else if (req.HttpMethod == "POST" && path == "/seek")
                await HandleSeekAsync(req, resp);

            else if (req.HttpMethod == "GET" && path == "/funscript/status")
                await WriteJsonAsync(resp, BuildFunscriptState());

            else if (req.HttpMethod == "POST" && path == "/funscript/load")
                await HandleFunscriptLoadAsync(req, resp);

            else if (req.HttpMethod == "POST" && path == "/funscript/play")
                await HandleFunscriptControlAsync(resp, "play");

            else if (req.HttpMethod == "POST" && path == "/funscript/pause")
                await HandleFunscriptControlAsync(resp, "pause");

            else if (req.HttpMethod == "POST" && path == "/funscript/stop")
                await HandleFunscriptControlAsync(resp, "stop");

            else
            {
                resp.StatusCode = 404;
                await WriteJsonAsync(resp, new { error = "endpoint not found" });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[WebApi] 处理请求失败", ex);
            resp.StatusCode = 500;
            try { await WriteJsonAsync(resp, new { error = ex.Message }); } catch { }
        }
    }

    /// <summary>校验请求来源为本机回环地址，且携带正确 token。</summary>
    private bool IsAuthorized(HttpListenerRequest req)
    {
        string host = req.Url?.Host ?? "";
        string addr = req.UserHostAddress ?? "";
        bool loopback = host is "127.0.0.1" or "localhost" or "::1"
                     || addr is "127.0.0.1" or "::1";
        if (!loopback) return false;

        var token = req.Headers["X-Hexa-Token"];
        return token == _cfg.ApiToken;
    }

    // ── Handlers ──────────────────────────────────────────────────────────

    /// <summary>GET /status — 返回当前状态快照</summary>
    private object BuildStatus()
    {
        var amp = _engine.GetAxisAmp();
        var axes = new Dictionary<string, double>();
        for (int i = 0; i < 6; i++)
            axes[AxisNames[i]] = Math.Round(amp[i], 2);

        string modeStr = _engine.ActiveMode switch
        {
            MotionMode.Auto   => "auto",
            MotionMode.Stroke => "stroke",
            MotionMode.Custom => "custom",
            MotionMode.Telemetry => "telemetry",
            _                 => "stop",
        };

        return new
        {
            mode      = modeStr,
            running   = _engine.IsRunning,
            connected = App.Serial.IsOpen,
            port      = App.Serial.PortName,
            armed     = _engine.IsArmed,
            emergencyStopped = _engine.EmergencyStopped,
            safety    = _engine.SafetyStatus,
            speed     = Math.Round(_engine.Speed, 3),
            intensity = Math.Round(_engine.IntensityScale, 3),
            preset    = _engine.CurrentStroke?.Id,
            axes,
        };
    }

    /// <summary>POST /preset/{id} — 切换预设（支持 Stroke 预设 ID 和快速预设 ID）</summary>
    private async Task HandlePresetAsync(string id, HttpListenerResponse resp)
    {
        // 先查 TempestStroke 预设
        var stroke = StrokesViewModel.AllPresets.FirstOrDefault(p => p.Id == id);
        if (stroke != null)
        {
            if (!_engine.CanRun)
            {
                resp.StatusCode = 409;
                await WriteJsonAsync(resp, new { error = "device output is locked", safety = _engine.SafetyStatus });
                return;
            }
            var s = stroke;
            App.Dispatch(() =>
            {
                _strokesVm.SelectedStroke = s;
                _cfg.LastStroke = s.Id;
                _engine.StartStroke(s);
            });
            await WriteJsonAsync(resp, new { ok = true, preset = id, type = "stroke" });
            return;
        }

        // 再查 Auto 快速预设
        var quickIds = new[] { "gentle", "daily", "crazy", "tease", "wave", "heartbeat", "escalate", "edging", "ambush" };
        if (quickIds.Contains(id))
        {
            if (!_engine.CanRun)
            {
                resp.StatusCode = 409;
                await WriteJsonAsync(resp, new { error = "device output is locked", safety = _engine.SafetyStatus });
                return;
            }
            App.Dispatch(() =>
            {
                _engine.ApplyQuickPreset(id);
                if (!_engine.AutoRunning) _engine.StartAuto();
            });
            await WriteJsonAsync(resp, new { ok = true, preset = id, type = "quick" });
            return;
        }

        resp.StatusCode = 404;
        await WriteJsonAsync(resp, new { error = $"preset '{id}' not found" });
    }

    /// <summary>POST /intensity?delta=0.1 或 ?value=1.2 — 相对/绝对调整强度（夹在 [0.1, 2.0]）</summary>
    private async Task HandleIntensityAsync(HttpListenerRequest req, HttpListenerResponse resp)
    {
        double delta = 0.1;
        if (double.TryParse(
                req.QueryString["delta"],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var d))
            delta = d;

        bool hasAbsolute = double.TryParse(
            req.QueryString["value"],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double absolute);

        // 与其它控制端点一致：回 UI 线程执行，避免与手动强度调整在后台线程竞争 read-modify-write。
        App.Dispatch(() =>
        {
            if (hasAbsolute) _engine.IntensityScale = absolute;   // setter 内部夹紧 0.1–2.0
            else _engine.AdjustIntensity(delta);
            _cfg.Save();
        });
        await WriteJsonAsync(resp, new { ok = true, intensity = Math.Round(_engine.IntensityScale, 3) });
    }

    /// <summary>POST /mode/{mode} — 切换模式：auto / stroke / custom / stop</summary>
    private async Task HandleModeAsync(string mode, HttpListenerResponse resp)
    {
        bool started = true;
        switch (mode)
        {
            case "auto":
                App.Dispatch(() => { if (!_engine.AutoRunning) started = _engine.StartAuto(); });
                break;
            case "stroke":
                App.Dispatch(() =>
                {
                    if (!_engine.StrokeRunning && _strokesVm.SelectedStroke != null)
                        started = _engine.StartStroke(_strokesVm.SelectedStroke);
                    else started = false;
                });
                break;
            case "custom":
                App.Dispatch(() => { if (!_engine.CustomRunning) started = _engine.StartCustom(); });
                break;
            case "stop":
                App.Dispatch(() => _engine.StopAll());
                break;
            default:
                resp.StatusCode = 400;
                await WriteJsonAsync(resp,
                    new { error = $"未知模式 '{mode}'，有效值：auto / stroke / custom / stop" });
                return;
        }
        if (!started)
        {
            resp.StatusCode = 409;
            await WriteJsonAsync(resp, new { error = "device output is locked or mode is unavailable", safety = _engine.SafetyStatus });
            return;
        }
        await WriteJsonAsync(resp, new { ok = true, mode });
    }

    /// <summary>
    /// POST /axis — 设置各轴振幅（JSON body）
    /// Body 示例：{"L0": 35.0, "R0": 20.0}（仅覆盖指定轴，其余保持不变）
    /// 值域：0–100
    /// </summary>
    private async Task HandleAxisAsync(HttpListenerRequest req, HttpListenerResponse resp)
    {
        if (req.ContentLength64 > 64 * 1024)
        {
            resp.StatusCode = 413;
            await WriteJsonAsync(resp, new { error = "request body too large" });
            return;
        }

        string body;
        using (var reader = new System.IO.StreamReader(req.InputStream, req.ContentEncoding))
            body = await reader.ReadToEndAsync();

        JsonObject? obj;
        try   { obj = JsonNode.Parse(body)?.AsObject(); }
        catch
        {
            resp.StatusCode = 400;
            await WriteJsonAsync(resp, new { error = "无效的 JSON 请求体" });
            return;
        }

        if (obj == null)
        {
            resp.StatusCode = 400;
            await WriteJsonAsync(resp, new { error = "请求体为空" });
            return;
        }

        // 以当前振幅为基准，只覆盖请求中指定的轴
        var amp  = _engine.GetAxisAmp();
        var vals = new double[6];
        for (int i = 0; i < 6; i++) vals[i] = amp[i];

        foreach (var (key, node) in obj)
        {
            int idx = Array.IndexOf(AxisNames, key.ToUpper());
            if (idx >= 0 && node != null &&
                double.TryParse(
                    node.ToString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var v))
                vals[idx] = Math.Clamp(v, 0, 100);
        }

        App.Dispatch(() =>
        {
            for (int i = 0; i < 6; i++) _engine.SetAxisAmp(i, vals[i]);
        });

        var result = new Dictionary<string, double>();
        for (int i = 0; i < 6; i++) result[AxisNames[i]] = Math.Round(vals[i], 2);
        await WriteJsonAsync(resp, new { ok = true, axes = result });
    }

    private async Task HandleArmAsync(HttpListenerResponse resp)
    {
        string? error = null;
        bool armed = false;
        App.Dispatch(() => armed = _engine.TryArm(out error));
        if (!armed)
        {
            resp.StatusCode = 409;
            await WriteJsonAsync(resp, new { error = error ?? "unable to arm" });
            return;
        }
        await WriteJsonAsync(resp, new { ok = true, safety = _engine.SafetyStatus });
    }

    // ── Funscript / time sync endpoints（需求6）────────────────────────────

    /// <summary>/time 与 /funscript/status 共用同一份播放器状态（字段含义完全一致）。</summary>
    private object BuildFunscriptState()
    {
        var p = _funscript;
        return new
        {
            ok = p != null,
            loaded = p?.HasTrack ?? false,
            playing = p?.IsPlaying ?? false,
            position = p?.PositionMs ?? 0,
            duration = p?.DurationMs ?? 0,
            file = p?.LoadedFile,
        };
    }

    private async Task HandleSeekAsync(HttpListenerRequest req, HttpListenerResponse resp)
    {
        if (_funscript == null)
        {
            resp.StatusCode = 409;
            await WriteJsonAsync(resp, new { error = "funscript player unavailable" });
            return;
        }

        long ms = 0;
        if (long.TryParse(req.QueryString["ms"], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            ms = parsed;
        else if (long.TryParse(req.QueryString["position"], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out parsed))
            ms = parsed;

        App.Dispatch(() => _funscript.Seek(ms));
        await WriteJsonAsync(resp, new { ok = true, position = _funscript.PositionMs, duration = _funscript.DurationMs });
    }

    private async Task HandleFunscriptLoadAsync(HttpListenerRequest req, HttpListenerResponse resp)
    {
        if (_funscript == null)
        {
            resp.StatusCode = 409;
            await WriteJsonAsync(resp, new { error = "funscript player unavailable" });
            return;
        }
        string? path = req.QueryString["path"];   // URL 编码后的文件路径
        if (string.IsNullOrWhiteSpace(path))
        {
            resp.StatusCode = 400;
            await WriteJsonAsync(resp, new { error = "missing 'path' query parameter" });
            return;
        }
        string decoded = Uri.UnescapeDataString(path);
        if (!System.IO.File.Exists(decoded))
        {
            resp.StatusCode = 404;
            await WriteJsonAsync(resp, new { error = $"file not found: {decoded}" });
            return;
        }
        try
        {
            App.Dispatch(() => _funscript.Load(decoded));
            await WriteJsonAsync(resp, new { ok = true, duration = _funscript.DurationMs, file = _funscript.LoadedFile });
        }
        catch (Exception ex)
        {
            resp.StatusCode = 400;
            await WriteJsonAsync(resp, new { error = ex.Message });
        }
    }

    /// <summary>POST /funscript/play|pause|stop — 手机面板远程控制 funscript 播放状态。</summary>
    private async Task HandleFunscriptControlAsync(HttpListenerResponse resp, string action)
    {
        if (_funscript == null)
        {
            resp.StatusCode = 409;
            await WriteJsonAsync(resp, new { error = "funscript player unavailable" });
            return;
        }

        App.Dispatch(() =>
        {
            switch (action)
            {
                case "play": _funscript.Play(); break;
                case "pause": _funscript.Pause(); break;
                case "stop": _funscript.Stop(); break;
            }
        });

        await WriteJsonAsync(resp, new
        {
            ok = true,
            playing = _funscript.IsPlaying,
            position = _funscript.PositionMs,
            duration = _funscript.DurationMs,
        });
    }

    /// <summary>极简深色手机控制面板（GET /）。只在回环访问，token 由页面输入并存入 localStorage。</summary>
    private static string BuildControlPanelHtml() => """
<!DOCTYPE html>
<html lang="zh">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Hexa 遥控</title>
<style>
  :root{--bg:#0f1114;--card:#171a1d;--border:#30363b;--text:#f0f2f4;--muted:#a8b0b8;--ok:#2ccb7f;--danger:#e55353;--warn:#f5b842;}
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI",system-ui,sans-serif;padding:14px;}
  h1{font-size:18px;margin:0 0 4px;} .sub{color:var(--muted);font-size:11px;margin:0 0 16px;}
  .card{background:var(--card);border:1px solid var(--border);border-radius:10px;padding:12px;margin:0 0 12px;}
  .row{display:flex;gap:8px;flex-wrap:wrap;align-items:center;margin:6px 0;}
  button{background:#22272b;color:var(--text);border:1px solid var(--border);border-radius:8px;padding:8px 12px;font-size:13px;cursor:pointer;}
  button.primary{background:var(--ok);color:#04120b;border-color:var(--ok);}
  button.danger{background:var(--danger);color:#fff;border-color:var(--danger);}
  button.warn{background:var(--warn);color:#201400;border-color:var(--warn);}
  input,select{background:#1c2024;color:var(--text);border:1px solid var(--border);border-radius:8px;padding:8px;font-size:13px;flex:1;min-width:0;}
  label{color:var(--muted);font-size:11px;}
  .stat{font-size:13px;} .stat b{color:var(--ok);}
  .bar{height:10px;background:#0f0f14;border-radius:5px;overflow:hidden;margin:4px 0;}
  .bar>div{height:100%;background:var(--ok);border-radius:5px;width:0;transition:width .15s;}
  .set{font-size:11px;color:var(--muted);}
</style>
</head>
<body>
<h1>Hexa 遥控</h1>
<p class="sub">本机回环控制面板 · 仅 127.0.0.1 可访问</p>

<div class="card">
  <label>访问令牌（存于本机浏览器 localStorage）</label>
  <div class="row">
    <input id="token" placeholder="粘贴 X-Hexa-Token">
    <button onclick="saveToken()">保存</button>
  </div>
</div>

<div class="card">
  <div class="stat" id="status">…</div>
  <div class="row">
    <button class="danger" onclick="api('/safety/estop',{method:'POST'}).then(refresh)">急停</button>
    <button onclick="api('/safety/home',{method:'POST'}).then(refresh)">归中</button>
    <button class="warn" onclick="api('/intensity?delta=-0.1',{method:'POST'}).then(refresh)">强度−</button>
    <button class="warn" onclick="api('/intensity?delta=0.1',{method:'POST'}).then(refresh)">强度+</button>
  </div>
</div>

<div class="card">
  <label>强度 <b id="intv">100%</b></label>
  <input type="range" id="intensity" min="10" max="200" step="5" value="100" oninput="setIntensity(this.value)">
</div>

<div class="card">
  <div class="row">
    <button class="primary" onclick="api('/mode/auto',{method:'POST'}).then(refresh)">自动</button>
    <button onclick="api('/mode/stop',{method:'POST'}).then(refresh)">停止</button>
    <select id="preset" onchange="applyPreset()"></select>
  </div>
  <div class="stat" id="mode">模式：—</div>
</div>

<div class="card">
  <label>六轴实时 · 拖动调整振幅</label>
  <div id="axes"></div>
</div>


<script>
let token=localStorage.getItem('helixToken')||'';
document.getElementById('token').value=token;
document.querySelectorAll('#axes').forEach(()=>{});
const AXES=['L0','L1','L2','R0','R1','R2'];
const PRESETS=[['gentle','轻柔'],['daily','日常'],['crazy','疯狂'],['tease','挑逗'],['wave','波浪'],['heartbeat','心跳'],['escalate','阶梯递增'],['edging','欲擒故纵'],['ambush','偷袭一下']];
function saveToken(){token=document.getElementById('token').value.trim();localStorage.setItem('helixToken',token);}
async function api(path,opts={}){
  const headers=Object.assign({'X-Hexa-Token':token},opts.headers||{});
  const r=await fetch(path,Object.assign({},opts,{headers}));
  const d=await r.json().catch(()=>({}));
  if(!r.ok) throw new Error(d.error||('HTTP '+r.status));
  return d;
}
const axisEls={};
function bars(axes){
  const el=document.getElementById('axes');
  if(!Object.keys(axisEls).length){
    for(const a of AXES){
      const w=document.createElement('div');w.className='set';
      const b=document.createElement('div');b.className='bar';
      const f=document.createElement('div');
      const s=document.createElement('input');s.type='range';s.min='0';s.max='100';
      s.oninput=()=>{w.textContent=a+' · '+s.value+'%';setAxis(a,s.value);};
      b.appendChild(f);
      el.appendChild(w);el.appendChild(b);el.appendChild(s);
      axisEls[a]={w:w,f:f,s:s};
    }
  }
  for(const a of AXES){
    const v=Math.round(axes?.[a]??0);
    const e=axisEls[a];
    e.f.style.width=Math.min(100,v)+'%';
    if(document.activeElement!==e.s){ e.s.value=v; e.w.textContent=a+' · '+v+'%'; }
  }
}
function setAxis(a,v){api('/axis',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({[a]:Number(v)})}).catch(()=>{});}
function setIntensity(v){document.getElementById('intv').textContent=v+'%';api('/intensity?value='+(v/100),{method:'POST'}).catch(()=>{});}
async function refresh(){
  try{
    const s=await api('/status');
    document.getElementById('status').innerHTML='连接：'+(s.connected?'✔ '+s.port:'—')+' · 安全：'+s.safety;
    document.getElementById('mode').textContent='模式：'+s.mode+' · '+(s.running?'运行中':'待机');
    const iv=document.getElementById('intensity');
    if(iv && document.activeElement!==iv){ const pv=Math.round(s.intensity*100); iv.value=pv; document.getElementById('intv').textContent=pv+'%'; }
    bars(s.axes);
    const p=document.getElementById('preset');
    if(!p.options.length){for(const [id,label] of PRESETS){const o=document.createElement('option');o.value=id;o.textContent=label;p.appendChild(o);}}
  }catch(e){
    document.getElementById('status').innerHTML='<span style="color:var(--danger)">未授权或未连接：'+(e.message||e)+'</span>';
  }
}
function applyPreset(){const id=document.getElementById('preset').value;if(id) api('/preset/'+id,{method:'POST'}).then(refresh).catch(alert);}
refresh();setInterval(refresh,1000);
</script>
</body>
</html>
""";

    // ── Helpers ───────────────────────────────────────────────────────────

    private static async Task WriteHtmlAsync(HttpListenerResponse resp, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        resp.ContentType     = "text/html; charset=utf-8";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes);
        resp.Close();
    }

    private static async Task WriteJsonAsync(HttpListenerResponse resp, object data)
    {
        var json  = JsonSerializer.Serialize(data, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentType     = "application/json; charset=utf-8";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes);
        resp.Close();
    }
}
