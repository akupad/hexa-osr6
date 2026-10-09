using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexa.Models;
using Hexa.Services;
using System.Collections.ObjectModel;

namespace Hexa.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SerialService _serial;
    private readonly MotionEngine _engine;
    private readonly AppSettings _cfg;
    private bool _initializing = true;
    // 退出模拟时是否抑制「自动重连真机」：设置页「断开连接」要的是断开，不能再连回去
    private bool _suppressSimulationReconnect;

    [ObservableProperty] private string _port = "";
    [ObservableProperty] private string _fallbackPort = "";
    [ObservableProperty] private string _connectionStatus = "未连接";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isArmed;
    [ObservableProperty] private string _safetyStatus = "输出锁定";
    [ObservableProperty] private bool _autoConnect = true;
    [ObservableProperty] private bool _preferWired = true;
    [ObservableProperty] private bool _simulationMode;
    [ObservableProperty] private ObservableCollection<SerialPortOption> _availablePorts = [];

    [ObservableProperty] private int _l0Min, _l1Min, _l2Min, _r0Min, _r1Min, _r2Min;
    [ObservableProperty] private int _l0Max = 9999, _l1Max = 9999, _l2Max = 9999,
                                      _r0Max = 9999, _r1Max = 9999, _r2Max = 9999;
    // 限位写盘失败：设置页据此给出可见提示，不能让用户以为已经保存好了
    [ObservableProperty] private bool _limitSaveFailed;

    [ObservableProperty] private bool _audioReactiveEnabled;
    [ObservableProperty] private double _audioSensitivity = 1.0;
    [ObservableProperty] private bool _audioLowPassEnabled;
    [ObservableProperty] private bool _ruleEngineEnabled;
    [ObservableProperty] private bool _webApiEnabled;

    public SettingsViewModel(SerialService serial, MotionEngine engine, AppSettings cfg)
    {
        _serial = serial;
        _engine = engine;
        _cfg = cfg;

        Port = cfg.Port;
        FallbackPort = cfg.FallbackPort;
        AutoConnect = cfg.AutoConnect;
        PreferWired = cfg.PreferWired;
        SimulationMode = cfg.SimulationMode;
        AudioReactiveEnabled = cfg.AudioReactiveEnabled;
        AudioSensitivity = cfg.AudioSensitivity;
        AudioLowPassEnabled = cfg.AudioLowPassEnabled;
        RuleEngineEnabled = cfg.RuleEngineEnabled;
        WebApiEnabled = cfg.WebApiEnabled;

        LoadAxisLimits();
        RefreshPorts();
        SyncConnectionState();

        _serial.ConnectionChanged += _ => App.Dispatch(SyncConnectionState);
        _engine.StateChanged += () => App.Dispatch(SyncConnectionState);
        _initializing = false;
    }

    private int GetMin(string axis) => _cfg.AxisMin.TryGetValue(axis, out int value) ? value : 0;
    private int GetMax(string axis) => _cfg.AxisMax.TryGetValue(axis, out int value) ? value : 9999;

    private void LoadAxisLimits()
    {
        L0Min = GetMin("L0"); L1Min = GetMin("L1"); L2Min = GetMin("L2");
        R0Min = GetMin("R0"); R1Min = GetMin("R1"); R2Min = GetMin("R2");
        L0Max = GetMax("L0"); L1Max = GetMax("L1"); L2Max = GetMax("L2");
        R0Max = GetMax("R0"); R1Max = GetMax("R1"); R2Max = GetMax("R2");
    }

    private void SyncConnectionState()
    {
        IsConnected = _serial.IsOpen;
        IsArmed = _engine.CanRun;
        SafetyStatus = _engine.SafetyStatus;
        ConnectionStatus = _serial.IsOpen
            ? $"已连接 {_serial.PortName} · {_engine.SafetyStatus}"
            : $"未连接{(string.IsNullOrWhiteSpace(_serial.LastError) ? "" : $" · {_serial.LastError}")}";
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        AvailablePorts.Clear();
        foreach (var option in SerialService.GetAvailablePortOptions()) AvailablePorts.Add(option);
        if (AvailablePorts.Count == 0) return;

        SerialPortOption? current = AvailablePorts.FirstOrDefault(option =>
            string.Equals(option.PortName, Port, StringComparison.OrdinalIgnoreCase));
        SerialPortOption? wired = AvailablePorts.FirstOrDefault(option => !option.IsBluetooth);

        if (PreferWired && wired != null && (current == null || current.IsBluetooth))
        {
            if (current?.IsBluetooth == true && string.IsNullOrWhiteSpace(FallbackPort))
                FallbackPort = current.PortName;
            Port = wired.PortName;
        }
        else if (current == null)
        {
            Port = (wired ?? AvailablePorts[0]).PortName;
        }

        bool fallbackValid = AvailablePorts.Any(option =>
            string.Equals(option.PortName, FallbackPort, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(option.PortName, Port, StringComparison.OrdinalIgnoreCase));
        if (!fallbackValid)
        {
            FallbackPort = AvailablePorts
                .FirstOrDefault(option => option.IsBluetooth
                    && !string.Equals(option.PortName, Port, StringComparison.OrdinalIgnoreCase))
                ?.PortName ?? "";
        }

        PersistConnectionPreferences();
    }

    [RelayCommand]
    private async Task Connect()
    {
        if (SimulationMode)
        {
            _serial.SetSimulationMode(true);
            SyncConnectionState();
            return;
        }

        string primary = Port.Trim();
        if (primary.Length == 0)
        {
            ConnectionStatus = "未连接 · 请先选择主端口";
            return;
        }

        PersistConnectionPreferences();
        IsConnected = false;
        ConnectionStatus = "正在自动连接主通道 / 备用通道…";
        await _serial.ConnectAsync(primary, FallbackPort);
        SyncConnectionState();
    }

    /// <summary>
    /// 退出「模拟设备」。设置页按用户要求移除了模拟开关，但 settings.json 里
    /// SimulationMode=true 时界面上必须留一个出口，这就是那个出口。
    /// 复用 OnSimulationModeChanged 的既有逻辑：落盘 + SetSimulationMode(false) 让 Serial 真正退出模拟；
    /// 勾了「启动时自动连接」时顺手去连真机（reconnectIfAutoConnect=false 则不连，用于「断开连接」）。
    /// 返回 false = settings.json 写盘失败（内存值已改，本次运行已生效）。
    /// </summary>
    public bool ExitSimulation(bool reconnectIfAutoConnect = true)
    {
        if (SimulationMode)
        {
            _suppressSimulationReconnect = !reconnectIfAutoConnect;
            try { SimulationMode = false; }   // 触发 OnSimulationModeChanged：SetSimulationMode(false) + Save + 按需重连
            finally { _suppressSimulationReconnect = false; }
        }
        _cfg.SimulationMode = false;
        _serial.SetSimulationMode(false);     // 兜底（幂等）：属性原本就是 false 时也确保 Serial 退出模拟
        SyncConnectionState();
        return _cfg.Save();
    }

    [RelayCommand]
    private void EmergencyStop() => _engine.EmergencyStop();

    private void PersistConnectionPreferences()
    {
        _cfg.Port = Port.Trim().ToUpperInvariant();
        _cfg.FallbackPort = FallbackPort.Trim().ToUpperInvariant();
        _cfg.AutoConnect = AutoConnect;
        _cfg.PreferWired = PreferWired;
        _cfg.Save();
    }

    [RelayCommand]
    private void SaveAxisLimits()
    {
        (L0Min, L0Max) = NormalizeLimits(L0Min, L0Max);
        (L1Min, L1Max) = NormalizeLimits(L1Min, L1Max);
        (L2Min, L2Max) = NormalizeLimits(L2Min, L2Max);
        (R0Min, R0Max) = NormalizeLimits(R0Min, R0Max);
        (R1Min, R1Max) = NormalizeLimits(R1Min, R1Max);
        (R2Min, R2Max) = NormalizeLimits(R2Min, R2Max);

        _cfg.AxisMin["L0"] = L0Min; _cfg.AxisMin["L1"] = L1Min; _cfg.AxisMin["L2"] = L2Min;
        _cfg.AxisMin["R0"] = R0Min; _cfg.AxisMin["R1"] = R1Min; _cfg.AxisMin["R2"] = R2Min;
        _cfg.AxisMax["L0"] = L0Max; _cfg.AxisMax["L1"] = L1Max; _cfg.AxisMax["L2"] = L2Max;
        _cfg.AxisMax["R0"] = R0Max; _cfg.AxisMax["R1"] = R1Max; _cfg.AxisMax["R2"] = R2Max;
        // 写盘结果不能丢（AppSettings.Save() 返回 false = 磁盘写不进去）：
        // 设置页读到 LimitSaveFailed 会把提示行变红并弹窗，不再静默当成已保存。
        LimitSaveFailed = !_cfg.Save();
        if (LimitSaveFailed) AppLogger.Warn("轴限位保存失败：settings.json 写盘出错，重启后可能丢失。");
    }

    [RelayCommand]
    private void ResetAxisLimits()
    {
        L0Min = L1Min = L2Min = R0Min = R1Min = R2Min = 0;
        L0Max = L1Max = L2Max = R0Max = R1Max = R2Max = 9999;
        SaveAxisLimits();
    }

    private static (int Min, int Max) NormalizeLimits(int first, int second)
    {
        int min = Math.Clamp(Math.Min(first, second), 0, 9999);
        int max = Math.Clamp(Math.Max(first, second), 0, 9999);
        return (min, max);
    }

    partial void OnAutoConnectChanged(bool value)
    {
        if (_initializing) return;
        _cfg.AutoConnect = value;
        _cfg.Save();
        if (value && !SimulationMode) _serial.EnableAutoDiscovery(PreferWired);
        else _serial.DisableAutoDiscovery();
    }

    partial void OnPreferWiredChanged(bool value)
    {
        if (_initializing) return;
        _cfg.PreferWired = value;
        if (AutoConnect && !SimulationMode) _serial.EnableAutoDiscovery(value);
        RefreshPorts();
    }

    partial void OnSimulationModeChanged(bool value)
    {
        if (_initializing) return;
        _cfg.SimulationMode = value;
        _cfg.Save();
        _serial.SetSimulationMode(value);
        if (!value && !_suppressSimulationReconnect && AutoConnect && !string.IsNullOrWhiteSpace(Port))
            _ = _serial.ConnectAsync(Port, FallbackPort);
        SyncConnectionState();
    }

    partial void OnAudioReactiveEnabledChanged(bool value)
    {
        if (_initializing) return;
        _cfg.AudioReactiveEnabled = value;
        _cfg.Save();
        App.AudioReactive.Refresh();
    }

    partial void OnAudioSensitivityChanged(double value)
    {
        if (_initializing) return;
        _cfg.AudioSensitivity = Math.Clamp(value, 0, 2);
        _cfg.Save();
    }

    partial void OnAudioLowPassEnabledChanged(bool value)
    {
        if (_initializing) return;
        _cfg.AudioLowPassEnabled = value;
        _cfg.Save();
    }

    partial void OnRuleEngineEnabledChanged(bool value)
    {
        if (_initializing) return;
        _cfg.RuleEngineEnabled = value;
        _cfg.Save();
        App.RuleEngine.Refresh();
    }

    partial void OnWebApiEnabledChanged(bool value)
    {
        if (_initializing) return;
        _cfg.WebApiEnabled = value;
        _cfg.Save();
        // 待开发：本地控制接口已停用，不随设置启停（见 WebApiService.FeatureEnabled）
        if (!WebApiService.FeatureEnabled) return;
        if (value) App.WebApi.Start(); else App.WebApi.Stop();
    }
}
