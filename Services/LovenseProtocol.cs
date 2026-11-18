using System.Globalization;
using System.Text.Json;

namespace Hexa.Services;

/// <summary>
/// Lovense Remote 的本地接口（游戏里那一栏「Lovense IP / SSL 端口」连的就是它）。
/// 这里只有解析与拼响应，不碰端口也不碰设备，方便单测。
/// 协议出处：lovense/Standard_solutions 的 Standard API（POST https://{ip}:{port}/command）。
/// </summary>
public static class LovenseProtocol
{
    /// <summary>
    /// 我们报给游戏的那台玩具。默认 Solace Pro：Lovense 家的抽插机，抽是主菜，
    /// 和我们这台「抽 + 转」的机器最接近。名字只影响游戏对能力的认知（它连上问一次），
    /// 实际怎么动由我们这边的映射决定；想要游戏发 Rotate 就开「额外报一个会旋转的玩具」。
    /// </summary>
    public const string ToyId = "osr6hexa";
    public const string RotateToyId = "osr6rotate";
    public const string DefaultToyName = "Solace Pro";
    private const string ToyNickName = "OSR6";

    /// <summary>toy 参数定向到哪一台：全部 / 主玩具（抽插）/ 转动玩具（nora → R0）。</summary>
    public enum ToyTarget
    {
        All,
        Main,
        Rotate
    }

    /// <summary>Lovense 的强度是 0–20 的整数，拉满就走整段行程（0–100）。</summary>
    public static double StrokeAmplitude(int strength)
    {
        if (strength <= 0) return 0;
        double s = Math.Clamp(strength, 0, 20) / 20.0;
        return 6 + 94 * s;
    }

    /// <summary>
    /// 强度同时决定速度。行程翻倍以后频率要收一点：同样的听感只需要一半的来回数，
    /// 而机器追得上才不会被限速器削掉幅度。
    /// </summary>
    public static double StrokeHz(int strength)
    {
        double s = Math.Clamp(strength, 0, 20) / 20.0;
        return 0.3 + 1.1 * s;
    }

    /// <summary>
    /// 游玩页行程条是「一根条子两个圆点」：左边那个点＝抬到最高的位置，右边那个点＝插到最深的位置。
    /// 数值小的一头就是抬得高的那头，所以左把手拿到的数（LowerValue）天然就是「最高」。
    /// 100 = 整段行程的尽头。
    /// </summary>
    public const int DefaultStrokeTopPercent = 20;

    public const int DefaultStrokeDeepPercent = 70;
    /// <summary>扭转的两个端点（% 行程）：左边＝转到一头就停，右边＝另一头。默认留 20% 余量不撞机械端。</summary>
    public const int DefaultRotateTopPercent = 20;
    public const int DefaultRotateDeepPercent = 80;

    /// <summary>最高点与最深点至少差这么多，否则电机就在原地抖。</summary>
    public const int MinStrokeSpanPercent = 10;

    /// <summary>
    /// 深端留量：强度拉满时也不真的顶到用户画的那个最深点，留 8% 行程。
    /// 顶到底那一下是「疼 / 爽」的分界，靠留余量而不是靠用户手动把最深处往外拖。
    /// </summary>
    public const double DefaultEndMarginPercent = 8;

    /// <summary>
    /// 把两个圆点收成一个合法区间：各自夹进 0–100，再保证两点差不小于 10。
    /// 两个圆点被拖成交叉的也直接换回来（左点永远是较小的数＝抬得更高的那头），绝不让区间变成负的。
    /// </summary>
    public static (double Top, double Deep) StrokeWindow(int topPercent, int deepPercent)
    {
        double top = Math.Clamp(topPercent, 0, 100);
        double deep = Math.Clamp(deepPercent, 0, 100);
        if (top > deep) (top, deep) = (deep, top);
        if (deep - top >= MinStrokeSpanPercent) return (top, deep);
        // 区间太窄：先把「最深」往后撑，撑不下再把「最高」往前压。
        deep = Math.Min(100, top + MinStrokeSpanPercent);
        top = Math.Max(0, deep - MinStrokeSpanPercent);
        return (top, deep);
    }

    /// <summary>
    /// 一次抽插落在 0–100 的哪个位置：只在「最高点…最深点」这段里摆，碰不到两端之外 —— 这就是「抬太高掉出来」的物理保证。
    /// amplitude 是强度换算出的行程占比（0–100），wave 是 -1..1 的正弦；强度小就在区间中间小幅动，拉满才走完整个区间。
    /// </summary>
    public static double StrokePosition(double topPercent, double deepPercent, double amplitude, double wave)
    {
        double mid = (topPercent + deepPercent) / 2.0;
        double half = (deepPercent - topPercent) / 2.0;
        double scale = double.IsFinite(amplitude) ? Math.Clamp(amplitude, 0, 100) / 100.0 : 0;
        double w = double.IsFinite(wave) ? Math.Clamp(wave, -1, 1) : 0;
        return mid + half * scale * w;
    }

    /// <summary>
    /// 条上的百分比 → 真正写给 L0 的轴值。
    ///
    /// 真机实测（用户确认）的对应关系：**L0 轴值越大 = 抬得越高，轴值越小 = 插得越深**；
    /// 而条上是「小 = 最高、大 = 最深」，两者正好相反，所以必须翻一次。不翻的话把「最高」
    /// 那个圆点拉到 20%，机器反而沉到底。翻完两端和整条曲线仍然单调，抽插形状
    /// （进快—到底停—退慢）不变，只把整段坐标换个方向。
    /// </summary>
    public static double StrokeAxisValue(double barPercent)
    {
        double p = double.IsFinite(barPercent) ? Math.Clamp(barPercent, 0, 100) : 0;
        return 100.0 - p;
    }

    /// <summary>
    /// 真实抽插的形状版：<paramref name="wave01"/> 是 0–1 的单向相位（0＝抬到最高点，1＝插到最深点），
    /// 由 <see cref="MotionVocabulary.ThrustWave"/> 给出（进得快、到底顿一下、退得慢）。
    /// 和正弦版的区别：正弦两端速度都是 0、中段最快，摸起来像活塞；这个形状冲击集中在前段。
    /// 深端再留 <paramref name="endMarginPercent"/> 的余量，满强度也不会真的落到最深点。
    /// </summary>
    public static double StrokeWavePosition(
        double topPercent,
        double deepPercent,
        double amplitude,
        double wave01,
        double endMarginPercent = DefaultEndMarginPercent)
    {
        double top = Math.Min(topPercent, deepPercent);
        double deep = Math.Max(topPercent, deepPercent);
        double scale = double.IsFinite(amplitude) ? Math.Clamp(amplitude, 0, 100) / 100.0 : 0;
        double margin = double.IsFinite(endMarginPercent) ? Math.Clamp(endMarginPercent, 0, 40) / 100.0 : 0;
        double w = double.IsFinite(wave01) ? Math.Clamp(wave01, 0, 1) : 0;
        return top + (deep - top) * scale * (1.0 - margin) * w;
    }

    /// <summary>
    /// 游戏基本是「角色抽一下发一条指令」，所以指令间隔就是这个动作的真实周期。
    /// 间隔离谱（断续、卡顿、玩家暂停）时退回强度算出来的频率。
    /// </summary>
    public static double TempoHz(double intervalSeconds, double fallbackHz)
    {
        if (!double.IsFinite(intervalSeconds) || intervalSeconds < 0.15 || intervalSeconds > 3.0)
            return fallbackHz;
        return Math.Clamp(1.0 / intervalSeconds, 0.4, 2.6);
    }

    /// <summary>
    /// 旋转也跟指令走：游戏一抽发一条，就让它在这条指令的间隔里刚好拧完一个来回——
    /// 间隔长就慢慢拧，间隔短就拧得快。间隔离谱（断流、暂停）时退回强度算出来的慢转。
    /// </summary>
    public static double TwistHz(double intervalSeconds, double fallbackHz)
    {
        if (!double.IsFinite(intervalSeconds) || intervalSeconds < 0.15 || intervalSeconds > 3.0)
            return fallbackHz;
        return Math.Clamp(1.0 / intervalSeconds, 0.35, 2.6);
    }

    /// <summary>
    /// 行程和节奏受同一个物理上限约束：峰速 = π × 行程% × 频率，超过舒适档允许值就会被限速器削平
    /// （波形被磨圆、滞后几十上百毫秒）。与其让它削，不如按当前深度把频率压到刚好放得下。
    /// </summary>
    public static double FitHz(double desiredHz, double amplitude, double speedLimitPerSecond)
    {
        if (amplitude <= 0.5 || !double.IsFinite(speedLimitPerSecond) || speedLimitPerSecond <= 0)
            return desiredHz;
        double cap = speedLimitPerSecond / (Math.PI * amplitude);
        return Math.Clamp(Math.Min(desiredHz, cap), 0.35, 2.6);
    }

    /// <summary>把任意相位收进 [0,1)。</summary>
    public static double Frac(double value)
    {
        if (!double.IsFinite(value)) return 0;
        double wrapped = value - Math.Floor(value);
        return wrapped >= 1 ? 0 : wrapped;
    }

    /// <summary>
    /// 相位离「这一抽的起点（0）」还差多少，结果收在 -0.5~0.5（走短的那边）。
    /// 每条指令到达时用它把动作对齐到指令上，而不是让波形自己一直转。
    /// </summary>
    public static double PhaseError(double phase) => Frac(phase + 0.5) - 0.5;

    /// <summary>timeSec 的语义：0 或缺省 = 无限直到 Stop；>0 夹进 0.2–3600 秒。</summary>
    public static double EffectiveSeconds(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 ? Math.Clamp(seconds, 0.2, 3600) : double.PositiveInfinity;

    /// <summary>loopRunningSec / loopPauseSec 的「动 N 秒停 M 秒」：run 或 pause ≤ 0 时不开循环。</summary>
    public static bool LoopPaused(double runSec, double pauseSec, double elapsedSec)
    {
        if (!double.IsFinite(runSec) || !double.IsFinite(pauseSec) || runSec <= 0 || pauseSec <= 0) return false;
        if (!double.IsFinite(elapsedSec) || elapsedSec <= 0) return false;
        double cycle = runSec + pauseSec;
        return elapsedSec % cycle >= runSec;
    }

    /// <summary>PatternV2 位置序列的线性插值：返回 0–100 的条位置（还没翻成轴值）。</summary>
    public static double PatternV2PositionAt(IReadOnlyList<(double Ts, double Pos)> points, double elapsedMs)
    {
        if (points is null || points.Count == 0) return 50;
        if (points.Count == 1) return points[0].Pos;
        if (!double.IsFinite(elapsedMs)) elapsedMs = 0;
        (double ts0, double pos0) = points[0];
        if (elapsedMs <= ts0) return pos0;
        for (int i = 1; i < points.Count; i++)
        {
            (double ts1, double pos1) = points[i];
            if (elapsedMs < ts1)
            {
                double span = ts1 - ts0;
                double frac = span > 0 ? (elapsedMs - ts0) / span : 1;
                return pos0 + (pos1 - pos0) * frac;
            }
            ts0 = ts1;
            pos0 = pos1;
        }
        return pos0;
    }

    /// <summary>转动通道（nora 的 Rotate）单独一根滑杆：扭的幅度比抽插小得多，25% 已经很明显。</summary>
    public static double RotateAmplitude(int strength)
    {
        if (strength <= 0) return 0;
        double s = Math.Clamp(strength, 0, 20) / 20.0;
        return 5 + 20 * s;
    }

    /// <summary>转动速度跟抽插分开：扭得太快只会变成抖，慢一点才看得出是在转。</summary>
    public static double RotateHz(int strength)
    {
        double s = Math.Clamp(strength, 0, 20) / 20.0;
        return 0.15 + 0.85 * s;
    }

    /// <summary>
    /// 把 "Vibrate:16,Rotate:8" 这类串整个拆开。官方动作名有 Vibrate / Rotate / Pump /
    /// Thrusting / Fingering / Suction / Depth / All / Stop（Vibrate/Rotate/Thrusting/Fingering/Suction/All
    /// 0–20，Pump 与 Depth 只有 0–3）；除 Rotate 外都落到 OSR6 的直线行程上。
    /// Thrust 是 Thrusting 的简写，也认。Stroke / Oscillate 在这里不拆，由 Handle 单独解析。
    /// All:n 是「所有功能一起到 n」（Pure Onyx 主用这个），我们把 n 同时给抽插和转动。
    /// </summary>
    public static IReadOnlyList<(string Name, int Strength)> ParseActions(string? action)
    {
        var found = new List<(string Name, int Strength)>(2);
        if (string.IsNullOrWhiteSpace(action)) return found;
        foreach (string part in action.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = part.IndexOf(':');
            string name = (colon < 0 ? part : part[..colon]).Trim();
            string value = colon < 0 ? "" : part[(colon + 1)..].Trim();
            if (name.Equals("Stop", StringComparison.OrdinalIgnoreCase)) return [("Stop", 0)];
            bool known = name.Equals("Vibrate", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Rotate", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Thrust", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Thrusting", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Fingering", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Suction", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Depth", StringComparison.OrdinalIgnoreCase)
                || name.Equals("All", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Pump", StringComparison.OrdinalIgnoreCase);
            if (!known) continue;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int strength)) strength = 20;
            int max = name.Equals("Pump", StringComparison.OrdinalIgnoreCase) || name.Equals("Depth", StringComparison.OrdinalIgnoreCase) ? 3 : 20;
            found.Add((name, Math.Clamp(strength, 0, max)));
        }
        return found;
    }

    /// <summary>只取第一条认得出来的动作（老调用点用）。</summary>
    public static (string Action, int Strength) ParseAction(string? action)
    {
        IReadOnlyList<(string Name, int Strength)> all = ParseActions(action);
        return all.Count == 0 ? ("", 0) : (all[0].Name, all[0].Strength);
    }

    /// <summary>GetToys 里的 toys 字段是一段「被转义的 JSON 字符串」，游戏会再解一次。</summary>
    public static string ToysJson(string toyName = DefaultToyName, bool includeRotateToy = false)
    {
        string primary = $"\"{ToyId}\":{{\"id\":\"{ToyId}\",\"status\":\"1\",\"version\":\"\",\"name\":\"{Sanitize(toyName)}\",\"battery\":100,\"nickName\":\"{ToyNickName}\",\"shortFunctionNames\":[\"Vibrate\",\"Rotate\"],\"fullFunctionNames\":[\"Vibrate\",\"Rotate\",\"Stroke\",\"Oscillate\"]}}";
        if (includeRotateToy)
            primary += ",\"" + RotateToyId + "\":{\"id\":\"" + RotateToyId + "\",\"status\":\"1\",\"version\":\"\",\"name\":\"nora\",\"battery\":100,\"nickName\":\"OSR6-R\",\"shortFunctionNames\":[\"Vibrate\",\"Rotate\"],\"fullFunctionNames\":[\"Vibrate\",\"Rotate\"]}";
        return "{" + primary + "}";
    }

    /// <summary>
    /// 玩具名会原样进 JSON。真实 Lovense 本地接口的 GetToys name 是小写、单词间留空格
    /// （Solace Pro 报 "solace pro"，Nora 报 "nora"，Max 报 "max"），游戏常按这个 name 判型号，
    /// 所以这里转小写、把连续空白压成一个空格，其余非字母数字字符一律去掉，避免用户手输把报文写坏。
    /// </summary>
    public static string Sanitize(string? toyName)
    {
        if (string.IsNullOrWhiteSpace(toyName)) return Sanitize(DefaultToyName);
        Span<char> buffer = stackalloc char[24];
        int n = 0;
        bool lastWasSpace = false;
        foreach (char c in toyName.Trim())
        {
            if (n >= buffer.Length) break;
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
            {
                buffer[n++] = char.ToLowerInvariant(c);
                lastWasSpace = false;
            }
            else if (char.IsWhiteSpace(c) && !lastWasSpace && n > 0)
            {
                buffer[n++] = ' ';
                lastWasSpace = true;
            }
        }
        while (n > 0 && buffer[n - 1] == ' ') n--;
        return n == 0 ? Sanitize(DefaultToyName) : new string(buffer[..n]);
    }

    public static string OkBody(bool withToys, string toyName = DefaultToyName, bool includeRotateToy = false)
    {
        string toys = withToys ? ",\"data\":{\"toys\":" + JsonSerializer.Serialize(ToysJson(toyName, includeRotateToy)) + ",\"platform\":\"windows\",\"appType\":\"remote\"}" : "";
        return "{\"code\":200" + toys + ",\"message\":\"\",\"result\":true,\"type\":\"OK\"}";
    }

    public static string ErrorBody(int code, string message) =>
        "{\"code\":" + code.ToString(CultureInfo.InvariantCulture)
        + ",\"message\":" + JsonSerializer.Serialize(message)
        + ",\"result\":false,\"type\":\"error\"}";

    /// <summary>
    /// Seconds 是这条指令要求的时长（Lovense 的 timeSec，0/缺省 = 无限直到 Stop）；一次只发一条，
    /// 靠这个字段说「跑多久」。L0Target 是 Position 命令的直接轴值；PatternPoints/PatternOffsetMs/PatternTimeMs
    /// 给 PatternV2；StrokeMin/StrokeMax 是 Function 的 Stroke:min-max；LoopRunningSec/LoopPauseSec 是「动 N 秒停 M 秒」。
    /// </summary>
    public sealed record Outcome(
        bool Ok, string Body, string Action, int Strength, double Amplitude, double Hz, double Seconds = 0,
        double RotateAmplitude = 0, double RotateHz = 0, double L0Target = double.NaN,
        double LoopRunningSec = 0, double LoopPauseSec = 0,
        IReadOnlyList<(double Ts, double Pos)>? PatternPoints = null,
        double PatternOffsetMs = 0, double PatternTimeMs = 0,
        double StrokeMin = -1, double StrokeMax = -1, bool StopPrevious = true,
        ToyTarget Target = ToyTarget.All);

    /// <summary>
    /// Standard API 的 toy 参数定向：空/缺省/同时含两台/未知 id = 全部（保持原行为）；
    /// 只含主玩具 = Main；只含转动玩具 = Rotate。toy 是单 id 字符串或 id 数组（7.71.0+），
    /// 这里也容错逗号分隔的字符串写法。
    /// </summary>
    public static ToyTarget ResolveToyTarget(string? toyRaw)
    {
        if (string.IsNullOrWhiteSpace(toyRaw)) return ToyTarget.All;
        bool main = false, rotate = false;
        foreach (string part in toyRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals(ToyId, StringComparison.OrdinalIgnoreCase)) main = true;
            else if (part.Equals(RotateToyId, StringComparison.OrdinalIgnoreCase)) rotate = true;
        }
        return (main, rotate) switch
        {
            (true, false) => ToyTarget.Main,
            (false, true) => ToyTarget.Rotate,
            _ => ToyTarget.All
        };
    }

    /// <summary>把一条 /command 请求翻成「要做什么动作 + 回什么」。纯函数，端口与设备都不在这儿。</summary>
    public static Outcome Handle(string method, string path, string? body, string toyName = DefaultToyName, bool includeRotateToy = false)
    {
        // 先剥掉 ?query 再判 verb：老式接口是 /Vibrate?v=10，不剥就整条当成一个陌生的 verb。
        int question = path.IndexOf('?');
        string verb = (question >= 0 ? path[..question] : path).Trim('/').ToLowerInvariant();
        string command = "";
        string actionText = "";
        string presetName = "";
        string patternStrength = "";
        string positionValue = "";
        string toyRaw = "";
        double seconds = 0;
        double loopRunningSec = 0;
        double loopPauseSec = 0;
        double strokeMin = -1;
        double strokeMax = -1;
        bool stopPrevious = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("command", out var cmd)) command = (cmd.GetString() ?? "").Trim();
                if (root.TryGetProperty("action", out var act)) actionText = (act.GetString() ?? "").Trim();
                if (root.TryGetProperty("name", out var nm)) presetName = (nm.GetString() ?? "").Trim();
                if (root.TryGetProperty("strength", out var st)) patternStrength = (st.GetString() ?? "").Trim();
                if (root.TryGetProperty("timeSec", out var sec) && sec.ValueKind == JsonValueKind.Number
                    && sec.TryGetDouble(out double parsedSeconds)) seconds = parsedSeconds;
                if (root.TryGetProperty("loopRunningSec", out var lrs) && lrs.ValueKind == JsonValueKind.Number
                    && lrs.TryGetDouble(out double parsedLrs)) loopRunningSec = parsedLrs;
                if (root.TryGetProperty("loopPauseSec", out var lps) && lps.ValueKind == JsonValueKind.Number
                    && lps.TryGetDouble(out double parsedLps)) loopPauseSec = parsedLps;
                if (root.TryGetProperty("value", out var val))
                    positionValue = val.ValueKind == JsonValueKind.String ? (val.GetString() ?? "") : val.GetRawText();
                if (root.TryGetProperty("stopPrevious", out var sp) && sp.ValueKind == JsonValueKind.Number
                    && sp.TryGetInt32(out int parsedSp)) stopPrevious = parsedSp != 0;
                if (root.TryGetProperty("toy", out var toy))
                {
                    if (toy.ValueKind == JsonValueKind.String)
                    {
                        toyRaw = (toy.GetString() ?? "").Trim();
                    }
                    else if (toy.ValueKind == JsonValueKind.Array)
                    {
                        var toyIds = new List<string>();
                        foreach (JsonElement item in toy.EnumerateArray())
                            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                                toyIds.Add(item.GetString()!.Trim());
                        toyRaw = string.Join(',', toyIds);
                    }
                }
            }
        }
        catch (JsonException)
        {
            return new Outcome(false, ErrorBody(400, "Invalid Command"), "", 0, 0, 0);
        }

        // 老式 GET 接口（Lovense Connect 时期）：/Vibrate?v=10 / /Stop
        if (verb is "vibrate" or "rotate")
        {
            int v = 20;
            int q = path.IndexOf('?');
            if (q >= 0)
                foreach (string pair in path[(q + 1)..].Split('&'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0 && pair[..eq].Equals("v", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(pair[(eq + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) v = parsed;
                }
            actionText = verb + ":" + v.ToString(CultureInfo.InvariantCulture);
            command = "Function";
        }
        else if (verb is "stop") { command = "Function"; actionText = "Stop"; }
        else if (verb is "gettoys") command = "GetToys";

        if (command.Equals("GetToys", StringComparison.OrdinalIgnoreCase))
            return new Outcome(true, OkBody(true, toyName, includeRotateToy), "", 0, 0, 0);

        if (command.Equals("Preset", StringComparison.OrdinalIgnoreCase))
            return PresetToOutcome(presetName, seconds);

        if (command.Equals("Pattern", StringComparison.OrdinalIgnoreCase))
        {
            // 模式串是「每隔 S 毫秒换一个强度」，这里取它最强的一格当持续强度：
            // 节拍感丢了，但至少不会把「图案在跑」变成彻底不动。
            int peak = 0;
            foreach (string piece in patternStrength.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(piece, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) peak = Math.Max(peak, v);
            if (peak <= 0) return new Outcome(false, ErrorBody(400, "Invalid Command"), "", 0, 0, 0);
            peak = Math.Clamp(peak, 0, 20);
            return new Outcome(true, OkBody(false), "Vibrate", peak, StrokeAmplitude(peak), StrokeHz(peak), seconds);
        }

        if (command.Equals("Position", StringComparison.OrdinalIgnoreCase))
        {
            // 实时位置：value 是 0–100 的条位置，直接翻成 L0 轴值（Position 38 → L0 62）。
            if (!double.TryParse(positionValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double pos))
                return new Outcome(false, ErrorBody(400, "Invalid Command"), "", 0, 0, 0);
            pos = Math.Clamp(pos, 0, 100);
            return new Outcome(true, OkBody(false), "Position", 0, 0, 0, L0Target: StrokeAxisValue(pos));
        }

        if (command.Equals("PatternV2", StringComparison.OrdinalIgnoreCase))
            return PatternV2ToOutcome(body, stopPrevious);

        // 游戏每隔两秒发一次 {"type":"ping"} 探活：这种没有 command 的报文回 200 就行，
        // 回 400 会被它当成"玩具坏了"，之后就再也不发动作了。
        if (command.Length == 0 && body is not null && body.Contains("\"type\"", StringComparison.Ordinal))
            return new Outcome(true, OkBody(false), "", 0, 0, 0);

        if (!command.Equals("Function", StringComparison.OrdinalIgnoreCase))
            return new Outcome(false, ErrorBody(400, "Invalid Command"), "", 0, 0, 0);

        // toy 参数定向：只有开了额外旋转玩具才认（不开时行为与旧版完全一致）。
        ToyTarget target = includeRotateToy ? ResolveToyTarget(toyRaw) : ToyTarget.All;

        // 一条消息里可以同时带抽插和转动（"Vibrate:16,Rotate:8"）：两路都留下来，
        // OSR6 本来就有直线和扭转两组轴，能同时动。
        IReadOnlyList<(string Name, int Strength)> actions = ParseActions(actionText);
        int strokeStrength = 0;
        int rotateStrength = 0;
        int oscillateStrength = 0;
        var labels = new List<string>(2);

        // 我们对外报的玩具是抽插机：分得清能力的游戏会一条消息里同时发
        // Thrusting（抽）和 Vibrate（振）。振动马达我们没有，但转轴有——这种时候把振动
        // 接到转轴上，两路就都动起来了。只有振动、没有抽的消息仍按"主菜"算到直线上，
        // 否则那些只会发 Vibrate 的老游戏就只剩下转了。
        bool hasThrust = false;
        foreach ((string name, int value) in actions)
            if (name is "Thrust" or "Thrusting" or "Fingering" or "Suction" or "Depth" or "Pump") { hasThrust = true; break; }

        // Stroke:min-max 必须与 Thrusting 联用且差 ≥ 20，否则忽略（官方规则）；Oscillate 0–20 映射到转轴。
        foreach (string part in actionText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0) continue;
            string name = part[..colon].Trim();
            string range = part[(colon + 1)..].Trim();
            if (name.Equals("Stroke", StringComparison.OrdinalIgnoreCase))
            {
                int dash = range.IndexOf('-');
                if (dash > 0
                    && double.TryParse(range[..dash], NumberStyles.Float, CultureInfo.InvariantCulture, out double smin)
                    && double.TryParse(range[(dash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double smax))
                {
                    smin = Math.Clamp(smin, 0, 100);
                    smax = Math.Clamp(smax, 0, 100);
                    if (hasThrust && Math.Abs(smax - smin) >= 20) { strokeMin = Math.Min(smin, smax); strokeMax = Math.Max(smin, smax); }
                }
            }
            else if (name.Equals("Oscillate", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(range, NumberStyles.Integer, CultureInfo.InvariantCulture, out int osc))
            {
                oscillateStrength = Math.Clamp(osc, 0, 20);
            }
        }

        foreach ((string name, int value) in actions)
        {
            if (name == "Stop") return new Outcome(true, OkBody(false), "Stop", 0, 0, 0, StopPrevious: stopPrevious, Target: target);
            if (target == ToyTarget.Rotate)
            {
                // 定向到 nora（osr6rotate）的命令：Vibrate 和 Rotate 都只当转动强度，绝不碰抽插。
                int scaledRotate = name is "Pump" or "Depth" ? (int)Math.Round(value * 20.0 / 3.0) : value;
                if (scaledRotate > rotateStrength) rotateStrength = scaledRotate;
                labels.Add(name == "Vibrate" ? "Vibrate>转" : name);
                continue;
            }
            if (name == "Rotate")
            {
                rotateStrength = Math.Max(rotateStrength, value);
                labels.Add("Rotate");
                continue;
            }
            if (name == "All")
            {
                if (value > strokeStrength) strokeStrength = value;
                rotateStrength = Math.Max(rotateStrength, value);
                labels.Add("All");
                continue;
            }
            if (name == "Vibrate" && hasThrust)
            {
                rotateStrength = Math.Max(rotateStrength, value);
                labels.Add("Vibrate>转");
                continue;
            }
            int scaled = name == "Pump" ? (int)Math.Round(value * 20.0 / 3.0) : value;
            if (scaled > strokeStrength) { strokeStrength = scaled; }
            if (!labels.Contains(name)) labels.Add(name);
        }
        if (oscillateStrength > 0)
        {
            rotateStrength = Math.Max(rotateStrength, oscillateStrength);
            labels.Add("Oscillate");
        }
        if (strokeStrength <= 0 && rotateStrength <= 0) return new Outcome(true, OkBody(false), "Stop", 0, 0, 0, StopPrevious: stopPrevious, Target: target);
        return new Outcome(
            true, OkBody(false), string.Join('+', labels), Math.Max(strokeStrength, rotateStrength),
            StrokeAmplitude(strokeStrength), StrokeHz(strokeStrength), seconds,
            RotateAmplitude(rotateStrength), RotateHz(rotateStrength),
            StrokeMin: strokeMin, StrokeMax: strokeMax,
            LoopRunningSec: loopRunningSec, LoopPauseSec: loopPauseSec, StopPrevious: stopPrevious, Target: target);
    }

    private static Outcome PresetToOutcome(string name, double seconds)
    {
        int strength = name.ToLowerInvariant() switch
        {
            "pulse" => 14,
            "wave" => 10,
            "fireworks" => 18,
            "earthquake" => 20,
            _ => 0
        };
        if (strength == 0) return new Outcome(false, ErrorBody(404, "Invalid Parameter"), "", 0, 0, 0);
        return new Outcome(true, OkBody(false), "Vibrate", strength, StrokeAmplitude(strength), StrokeHz(strength), seconds);
    }

    private static Outcome PatternV2ToOutcome(string? body, bool stopPrevious)
    {
        if (string.IsNullOrWhiteSpace(body)) return new Outcome(false, ErrorBody(400, "Invalid Command"), "", 0, 0, 0);
        try
        {
            using var doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl))
                return new Outcome(false, ErrorBody(400, "Invalid Command"), "", 0, 0, 0);
            string type = (typeEl.GetString() ?? "").Trim();
            if (type.Equals("Setup", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("actions", out var actionsEl) || actionsEl.ValueKind != JsonValueKind.Array)
                    return new Outcome(false, ErrorBody(404, "Invalid Parameter"), "", 0, 0, 0);
                var points = new List<(double Ts, double Pos)>();
                foreach (JsonElement item in actionsEl.EnumerateArray())
                {
                    if (!item.TryGetProperty("ts", out var tsEl) || !item.TryGetProperty("pos", out var posEl)) continue;
                    if (!tsEl.TryGetDouble(out double ts) || !posEl.TryGetDouble(out double pos)) continue;
                    points.Add((Math.Max(0, ts), Math.Clamp(pos, 0, 100)));
                }
                points.Sort(static (a, b) => a.Ts.CompareTo(b.Ts));
                return new Outcome(true, OkBody(false), "PatternV2Setup", 0, 0, 0, PatternPoints: points, StopPrevious: stopPrevious);
            }
            if (type.Equals("Play", StringComparison.OrdinalIgnoreCase))
            {
                double offsetTime = 0;
                double timeMs = 0;
                if (root.TryGetProperty("offsetTime", out var offEl) && offEl.TryGetDouble(out double off)) offsetTime = Math.Clamp(off, 0, 15000);
                if (root.TryGetProperty("timeMs", out var tEl) && tEl.TryGetDouble(out double t)) timeMs = t;
                // 官方：timeMs 必须 > 100，否则这条 Play 被忽略。
                if (timeMs <= 100) return new Outcome(false, ErrorBody(404, "Invalid Parameter"), "", 0, 0, 0);
                return new Outcome(true, OkBody(false), "PatternV2Play", 0, 0, 0, PatternOffsetMs: offsetTime, PatternTimeMs: timeMs, StopPrevious: stopPrevious);
            }
            if (type.Equals("Stop", StringComparison.OrdinalIgnoreCase))
                return new Outcome(true, OkBody(false), "Stop", 0, 0, 0, StopPrevious: stopPrevious);
            if (type.Equals("SyncTime", StringComparison.OrdinalIgnoreCase))
                return new Outcome(true, OkBody(false), "", 0, 0, 0, StopPrevious: stopPrevious);
            return new Outcome(false, ErrorBody(404, "Invalid Parameter"), "", 0, 0, 0);
        }
        catch (JsonException)
        {
            return new Outcome(false, ErrorBody(400, "Invalid Command"), "", 0, 0, 0);
        }
    }
}
