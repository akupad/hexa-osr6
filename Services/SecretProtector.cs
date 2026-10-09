using System.Security.Cryptography;
using System.Text;

namespace Hexa.Services;

/// <summary>
/// 本机密钥保护（DPAPI，零新依赖）：把 API Key 这类敏感字符串加密后再落盘 settings.json。
/// 用 System.Security.Cryptography.ProtectedData（DataProtectionScope.CurrentUser）——
/// 这个程序集就在本机 WindowsDesktop 共享框架里（Microsoft.WindowsDesktop.App），不需要任何新 NuGet 包。
///
/// 诚实边界（别当成万能的）：
///   · 挡得住：settings.json 被同步盘/备份/截图/随手打开时，Key 不是明文；别的 Windows 账户也解不开。
///   · 挡不住：同一个账户下运行的其它进程（它们能以你的身份调用同一套 DPAPI 解密）；
///     也挡不住已经拿到你账户的恶意软件。DPAPI 不是"加密保险箱"，只是"别让 Key 裸奔在配置文件里"。
/// </summary>
public static class SecretProtector
{
    /// <summary>密文前缀（带版本号：将来换算法时能认出老格式，不会把老密文当明文用）。</summary>
    public const string Prefix = "dpapi:v1:";

    /// <summary>
    /// 附加熵：让密文只对本程序有意义（纵深防御，不是密钥 —— 它就写在这份源码里）。
    /// 换掉这个值会导致老密文解不开，所以一旦发布就不要改。
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Hexa.AiApiKey.v1");

    /// <summary>是不是"我们加密过的"格式（不是的话一律当明文处理）。</summary>
    public static bool LooksProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// 加密成 Base64（带前缀）。空串原样返回；已经是密文的原样返回（不会重复加密）。
    /// 加密不可用时**返回原文**：宁可这一段没加密，也不能让 AI 功能因为加密失败而不可用。
    /// </summary>
    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        if (LooksProtected(plain)) return plain;

        try
        {
            byte[] cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(cipher);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[Secret] DPAPI 加密不可用，Key 仍按明文保存（不影响使用）：{ex.Message}");
            return plain;
        }
    }

    /// <summary>
    /// 解密。**不是我们格式的字符串（老版本留下的明文、手工填的 Key）原样返回**，绝不抛异常；
    /// 是我们格式但解不开（换了 Windows 账户 / 配置文件被拷到别的机器 / 被改坏）时返回空串 +
    /// 记一条日志：界面上会显示"还没配 Key"，让用户重填一次；
    /// 绝不把密文当 Key 发出去（那只会换来一个看不懂的 401）。
    /// </summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!LooksProtected(stored)) return stored;

        try
        {
            byte[] cipher = Convert.FromBase64String(stored[Prefix.Length..]);
            byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[Secret] 保存的 Key 解不开（换了 Windows 账户，或配置文件被拷到了别的机器）："
                + $"请到「设置 → AI 助手」重新填一次。原因：{ex.Message}");
            return "";
        }
    }
}
