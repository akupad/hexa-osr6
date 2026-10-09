namespace Hexa.Services;

public interface ICommandTransport
{
    bool IsOpen { get; }
    bool OutputEnabled { get; set; }
    string PortName { get; }

    event Action<bool>? ConnectionChanged;

    /// <summary>
    /// 给用户看的处置建议（可空）。用于「软件层自己解决不了、必须他动手」的情形——
    /// 目前只有一种：串口卡死（写 100% 失败、读正常，软件复位与重开端口都无效，只有物理拔插能解）。
    /// 界面直接显示这句话，别让他自己猜"为什么突然不动了"。
    /// </summary>
    string? UserAdvice { get; }

    void Send(string command);
    void StopMotion();
    void SendAxes(
        double[] values,
        Dictionary<string, int> axisMin,
        Dictionary<string, int> axisMax,
        int interpolationMs = 16,
        bool changedOnly = true);

    void SendFrame(
        IEnumerable<TCodeAxisTarget> targets,
        Dictionary<string, int> axisMin,
        Dictionary<string, int> axisMax,
        bool changedOnly = true);
}

public sealed record SerialPortOption(
    string PortName,
    bool IsBluetooth,
    string FriendlyName = "",
    string HardwareId = "",
    bool IsBluetoothInbound = false)
{
    public bool IsKnownWiredDevice => !IsBluetooth
        && (HardwareId.Contains(Osr6DeviceProfile.WiredUsbHardwareId, StringComparison.OrdinalIgnoreCase)
            || FriendlyName.Contains("CH340", StringComparison.OrdinalIgnoreCase)
            || FriendlyName.Contains("USB-SERIAL", StringComparison.OrdinalIgnoreCase));

    public bool IsKnownBluetoothDevice => IsBluetooth
        && (FriendlyName.Contains(Osr6DeviceProfile.BluetoothDeviceName, StringComparison.OrdinalIgnoreCase)
            || (Osr6DeviceProfile.BluetoothAddress.Length > 0
                && HardwareId.Replace('-', ':').Contains(Osr6DeviceProfile.BluetoothAddress, StringComparison.OrdinalIgnoreCase)));

    public string TransportLabel => IsBluetooth ? "蓝牙 SPP" : "USB / 有线";
    public string DisplayName => string.IsNullOrWhiteSpace(PortName)
        ? "不使用备用通道"
        : $"{PortName}  ·  {TransportLabel}{(string.IsNullOrWhiteSpace(FriendlyName) ? "" : $"  ·  {FriendlyName}")}";
}
