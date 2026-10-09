using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Hexa.Models;

namespace Hexa.Services;

public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private static readonly IReadOnlyDictionary<int, string> ActionById =
        HotkeyConfig.Actions.Select((item, index) => (Id: index + 1, item.Action))
            .ToDictionary(item => item.Id, item => item.Action);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HashSet<int> _registeredIds = new();
    private readonly Dictionary<string, string> _registrationErrors = new(StringComparer.OrdinalIgnoreCase);
    private HotkeyConfig _bindings = new();
    private IntPtr _hwnd;
    private HwndSource? _source;
    private bool _enabled;
    private bool _disposed;

    public bool IsEnabled => _enabled;
    public IReadOnlyDictionary<string, string> RegistrationErrors => _registrationErrors;

    public event Action? OnInsert;
    public event Action? OnEnd;
    public event Action? OnHome;
    public event Action? OnUp;
    public event Action? OnDelete;
    public event Action? OnIntensityUp;
    public event Action? OnIntensityDown;

    public void Attach(Window window, bool enabled, HotkeyConfig bindings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_source != null) return;

        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        if (_hwnd == IntPtr.Zero || _source == null)
        {
            AppLogger.Error("无法把全局热键附加到主窗口句柄。");
            return;
        }

        _source.AddHook(WndProc);
        _bindings = bindings;
        SetEnabled(enabled);
    }

    public bool SetBindings(HotkeyConfig bindings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _bindings = bindings;
        if (!_enabled || _hwnd == IntPtr.Zero) return true;
        RegisterConfiguredBindings();
        return _registrationErrors.Count == 0;
    }

    public void SetEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _enabled = enabled;
        if (_hwnd == IntPtr.Zero) return;

        if (!enabled)
        {
            UnregisterAll();
            _registrationErrors.Clear();
            AppLogger.Info("全局热键已关闭。");
            return;
        }
        RegisterConfiguredBindings();
    }

    public static string FormatBinding(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join('+', parts);
    }

    public static bool IsBindableKey(Key key) => key is not (
        Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin);

    private void RegisterConfiguredBindings()
    {
        UnregisterAll();
        _registrationErrors.Clear();
        int id = 0;
        foreach ((string action, string label) in HotkeyConfig.Actions)
        {
            id++;
            string binding = _bindings.Get(action);
            if (string.IsNullOrWhiteSpace(binding)) continue;
            if (!TryParseBinding(binding, out uint modifiers, out uint virtualKey))
            {
                _registrationErrors[action] = "组合键格式无效";
                continue;
            }

            if (RegisterHotKey(_hwnd, id, modifiers | MOD_NOREPEAT, virtualKey))
            {
                _registeredIds.Add(id);
                continue;
            }

            int error = Marshal.GetLastWin32Error();
            _registrationErrors[action] = error == 1409 ? "组合键已被其他程序占用" : $"注册失败 ({error})";
            AppLogger.Warn($"全局热键注册失败: {label} = {binding} (Win32 {error})");
        }
        AppLogger.Info($"全局热键已注册: {_registeredIds.Count}/{HotkeyConfig.Actions.Count}");
    }

    private static bool TryParseBinding(string binding, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        string[] parts = binding.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win": case "windows": modifiers |= MOD_WIN; break;
                default: return false;
            }
        }

        if (!Enum.TryParse(parts[^1], ignoreCase: true, out Key key) || !IsBindableKey(key)) return false;
        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY || !_enabled) return IntPtr.Zero;
        if (!ActionById.TryGetValue(wParam.ToInt32(), out string? action)) return IntPtr.Zero;
        switch (action)
        {
            case HotkeyConfig.TogglePlayback: OnInsert?.Invoke(); break;
            case HotkeyConfig.EmergencyStop: OnEnd?.Invoke(); break;
            case HotkeyConfig.NextStroke: OnHome?.Invoke(); break;
            case HotkeyConfig.PreviousStroke: OnUp?.Invoke(); break;
            case HotkeyConfig.Burst: OnDelete?.Invoke(); break;
            case HotkeyConfig.IntensityUp: OnIntensityUp?.Invoke(); break;
            case HotkeyConfig.IntensityDown: OnIntensityDown?.Invoke(); break;
            default: return IntPtr.Zero;
        }
        handled = true;
        return IntPtr.Zero;
    }

    private void UnregisterAll()
    {
        foreach (int id in _registeredIds.ToArray())
        {
            UnregisterHotKey(_hwnd, id);
            _registeredIds.Remove(id);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _enabled = false;
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source = null;
        _hwnd = IntPtr.Zero;
        _disposed = true;
    }
}
