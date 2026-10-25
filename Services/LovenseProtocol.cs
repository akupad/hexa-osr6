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
    /// 我们报给游戏的那台玩具。默认 gravity：Lovense 家的抽插机（抽 + 振动），
    /// 和我们这台「抽 + 转」的机器最接近——主菜都是抽，而不是振动。
    /// 名字只影响游戏对能力的认知（它连上问一次），实际怎么动由我们这边的映射决定。
    /// </summary>
    public const string ToyId = "osr6hexa";
    public const string DefaultToyName = "gravity";
    private const string ToyNickName = "OSR6";

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

    /// <summary>游玩页那根深度滑杆的默认值：拉满。</summary>
    public const int DefaultDepthPercent = 100;

    public const int MinDepthPercent = 10;

    /// <summary>滑杆给的是深度上限：强度算出来的行程再乘用户设的百分比。</summary>
    public static double ScaleAmplitude(double amplitude, int depthPercent)
    {
        if (amplitude <= 0) return 0;
        double depth = Math.Clamp(depthPercent, 0, 100) / 100.0;
        return amplitude * depth;
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
    /// Thrusting / Fingering / Suction / Depth / All / Stop（前六个 0–20，Pump 只有 0–3）；
    /// 除 Rotate 外都落到 OSR6 的直线行程上。Thrust 是 Thrusting 的简写，也认。
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
            int max = name.Equals("Pump", StringComparison.OrdinalIgnoreCase) ? 3 : 20;
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
    public static string ToysJson(string toyName = DefaultToyName) =>
        "{" + string.Create(CultureInfo.InvariantCulture,
            $"\"{ToyId}\":{{\"id\":\"{ToyId}\",\"status\":\"1\",\"version\":\"\",\"name\":\"{Sanitize(toyName)}\",\"battery\":100,\"nickName\":\"{ToyNickName}\"}}")
        + "}";

    /// <summary>玩具名会原样进 JSON，只留字母数字下划线，避免用户手输把报文写坏。</summary>
    public static string Sanitize(string? toyName)
    {
        if (string.IsNullOrWhiteSpace(toyName)) return DefaultToyName;
        Span<char> buffer = stackalloc char[24];
        int n = 0;
        foreach (char c in toyName.Trim())
        {
            if (n >= buffer.Length) break;
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-') buffer[n++] = char.ToLowerInvariant(c);
        }
        return n == 0 ? DefaultToyName : new string(buffer[..n]);
    }

    public static string OkBody(bool withToys, string toyName = DefaultToyName)
    {
        string toys = withToys ? ",\"data\":{\"toys\":" + JsonSerializer.Serialize(ToysJson(toyName)) + ",\"platform\":\"windows\",\"appType\":\"remote\"}" : "";
        return "{\"code\":200" + toys + ",\"message\":\"\",\"result\":true,\"type\":\"OK\"}";
    }

    public static string ErrorBody(int code, string message) =>
        "{\"code\":" + code.ToString(CultureInfo.InvariantCulture)
        + ",\"message\":" + JsonSerializer.Serialize(message)
        + ",\"result\":false,\"type\":\"error\"}";

    /// <summary>Seconds 是这条指令要求的时长（Lovense 的 timeSec，缺省 20 秒）：它一次只发一条，靠这个字段说「跑多久」。</summary>
    public sealed record Outcome(
        bool Ok, string Body, string Action, int Strength, double Amplitude, double Hz, double Seconds = 0,
        double RotateAmplitude = 0, double RotateHz = 0);

    /// <summary>把一条 /command 请求翻成「要做什么动作 + 回什么」。纯函数，端口与设备都不在这儿。</summary>
    public static Outcome Handle(string method, string path, string? body, string toyName = DefaultToyName)
    {
        // 先剥掉 ?query 再判 verb：老式接口是 /Vibrate?v=10，不剥就整条当成一个陌生的 verb。
        int question = path.IndexOf('?');
        string verb = (question >= 0 ? path[..question] : path).Trim('/').ToLowerInvariant();
        string command = "";
        string actionText = "";
        string presetName = "";
        string patternStrength = "";
        double seconds = 0;
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
            return new Outcome(true, OkBody(true, toyName), "", 0, 0, 0);

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

        // 游戏每隔两秒发一次 {"type":"ping"} 探活：这种没有 command 的报文回 200 就行，
        // 回 400 会被它当成"玩具坏了"，之后就再也不发动作了。
        if (command.Length == 0 && body is not null && body.Contains("\"type\"", StringComparison.Ordinal))
            return new Outcome(true, OkBody(false), "", 0, 0, 0);

        if (!command.Equals("Function", StringComparison.OrdinalIgnoreCase))
            return new Outcome(false, ErrorBody(400, "Invalid Command"), "", 0, 0, 0);

        // 一条消息里可以同时带抽插和转动（"Vibrate:16,Rotate:8"）：两路都留下来，
        // OSR6 本来就有直线和扭转两组轴，能同时动。
        IReadOnlyList<(string Name, int Strength)> actions = ParseActions(actionText);
        int strokeStrength = 0;
        int rotateStrength = 0;
        var labels = new List<string>(2);

        // 我们对外报的玩具是 gravity（抽 + 振动）：分得清能力的游戏会一条消息里同时发
        // Thrusting（抽）和 Vibrate（振）。振动马达我们没有，但转轴有——这种时候把振动
        // 接到转轴上，两路就都动起来了。只有振动、没有抽的消息仍按"主菜"算到直线上，
        // 否则那些只会发 Vibrate 的老游戏就只剩下转了。
        bool hasThrust = false;
        foreach ((string name, int value) in actions)
            if (name is "Thrust" or "Thrusting" or "Fingering" or "Suction" or "Depth" or "Pump") { hasThrust = true; break; }

        foreach ((string name, int value) in actions)
        {
            if (name == "Stop") return new Outcome(true, OkBody(false), "Stop", 0, 0, 0);
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
        if (strokeStrength <= 0 && rotateStrength <= 0) return new Outcome(true, OkBody(false), "Stop", 0, 0, 0);
        return new Outcome(
            true, OkBody(false), string.Join('+', labels), Math.Max(strokeStrength, rotateStrength),
            StrokeAmplitude(strokeStrength), StrokeHz(strokeStrength), seconds,
            RotateAmplitude(rotateStrength), RotateHz(rotateStrength));
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
}
