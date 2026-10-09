using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hexa.Services;

/// <summary>
/// 读取当前前台窗口所属进程名（不含 .exe）。供前后台检测（ForegroundWatcher /
/// 动作键联动等）复用，避免重复声明 P/Invoke。
/// </summary>
public static class ForegroundProcess
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>当前前台进程名（不含 .exe）；无法读取时返回空字符串。</summary>
    public static string GetName()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "";
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "";
            using var process = Process.GetProcessById((int)pid);
            return System.IO.Path.GetFileNameWithoutExtension(process.ProcessName) ?? "";
        }
        catch
        {
            return "";
        }
    }
}
