using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexa.Models;
using Hexa.Services;

namespace Hexa.ViewModels;

public partial class PlaygroundViewModel : ObservableObject
{
    private readonly MotionEngine _engine;
    private readonly AppSettings _cfg;

    [ObservableProperty] private double _speed = 1.0;

    // ── Auto mode ──
    [ObservableProperty] private bool _autoRunning;
    [ObservableProperty] private string _autoPattern = "free_play";
    [ObservableProperty] private string _selectedPreset = "";
    [ObservableProperty] private string _comfortProfile = "immersive";

    public static (string Id, string Label)[] QuickPresets =>
    [
        ("gentle","轻柔"),("daily","日常"),("crazy","疯狂"),("tease","挑逗"),("wave","波浪"),
        ("heartbeat","心跳"),("escalate","阶梯递增"),("edging","欲擒故纵"),("ambush","偷袭一下")
    ];

    // ── 自动模式：唯一真源 ──
    /// <summary>
    /// 「波形」下拉的内容 = 自由巡游 + <see cref="AutoBehaviorSequencer.AllPatterns"/>
    /// （内置波形 + 动作页的自建动作）。同页「参与随机的动作」勾选列表用的是同一个源，
    /// 于是修掉了"同一页两套动作口径"：以前下拉有「递进爆发」却没有「深层脉冲 / 螺旋游走」，
    /// 勾选列表正好相反；现在两处选项与顺序一致，自建动作带「自建 · 」前缀一眼可分。
    /// </summary>
    public static IReadOnlyList<(string Id, string Label)> Patterns
    {
        get
        {
            var result = new List<(string Id, string Label)>(AutoBehaviorSequencer.AllPatterns.Count + 1)
            {
                ("free_play", "自由巡游 · 自动换动作"),
            };
            foreach (var pattern in AutoBehaviorSequencer.AllPatterns)
                result.Add((pattern.Id, pattern.IsCustom ? "自建 · " + pattern.Label : pattern.Label));
            return result;
        }
    }

    /// <summary>收藏的动作 id（动作页与游玩页共用一份）：游玩页的随机列表据此排序 / 标星。</summary>
    public IReadOnlyList<string> FavoriteStrokeIds => _cfg.FavoriteStrokes ?? [];

    public static IReadOnlyList<ComfortProfile> ComfortProfiles => Hexa.Models.ComfortProfile.All;

    public PlaygroundViewModel(MotionEngine engine, AppSettings cfg)
    {
        _engine = engine;
        _cfg = cfg;
        Speed = cfg.Speed;
        ComfortProfile = cfg.ComfortProfile;
        _engine.StateChanged += () => App.Dispatch(SyncFromEngine);
        SyncFromEngine();
    }

    partial void OnSpeedChanged(double value) { _engine.Speed = value; _cfg.Speed = value; }

    /// <summary>
    /// 记住"上次选的波形 / 动作"（<c>AppSettings.LastAutoPattern</c>）：
    /// 以前这个选择只活在内存里，重启就回到「自由巡游」。写盘由游玩页的节流器负责，这里只改内存。
    /// </summary>
    partial void OnAutoPatternChanged(string value) => _cfg.LastAutoPattern = value ?? "";

    [RelayCommand]
    private void ToggleAuto()
    {
        _engine.AutoPattern = AutoPattern;
        if (_engine.AutoRunning) _engine.StopAuto();
        else _engine.StartAuto();
        SyncFromEngine();
    }

    [RelayCommand]
    private void ApplyPreset(string id)
    {
        SelectedPreset = id;
        _engine.ApplyQuickPreset(id);
        Speed = _engine.Speed;
        AutoPattern = _engine.AutoPattern;
        // 记住"正在用的快速预设"：重启后高亮回到它（用户手动改波形/强度/速度时页面会清掉）。
        _cfg.LastQuickPreset = id;
        if (!_engine.AutoRunning) _engine.StartAuto();
        SyncFromEngine();
    }

    [RelayCommand]
    private void Stop()
    {
        _engine.EmergencyStop();
    }

    [RelayCommand]
    private void ApplyComfortProfile(string id)
    {
        _engine.ApplyComfortProfile(id);
        ComfortProfile = _engine.ActiveComfortProfile.Id;
    }

    private void SyncFromEngine()
    {
        AutoRunning = _engine.AutoRunning;
        Speed = _engine.Speed;
    }
}
