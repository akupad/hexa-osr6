using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hexa.Services;

/// <summary>
/// 运行时词表翻译：把界面上出现的中文原文整串换成英文（词表见 <see cref="LocalizationTable"/>）。
///
/// 为什么不做成 XAML 资源：26 个页面里上千条字面量，逐条改写等于把界面回归风险乘以页数，
/// 而且中文原文会从"唯一真源"变成一堆资源键。这里在运行时替换，词表缺一条最多是那一句没翻。
///
/// 两条不变式：
/// ① <see cref="T"/> 查不到就原样返回（绝不半截替换）；
/// ② <see cref="Apply"/> 只认整串精确匹配，且幂等 —— 英文界面下重复调用不会二次翻译，
///    切回中文时按"当初记下的原文"复原。
/// </summary>
public static class LocalizationService
{
    public const string Chinese = "zh";
    public const string English = "en";

    private static string _uiLanguage = Chinese;

    /// <summary>当前界面语言：<see cref="Chinese"/>（默认）或 <see cref="English"/>；写别的值一律当中文。</summary>
    public static string UiLanguage
    {
        get => _uiLanguage;
        set => _uiLanguage = value == English ? English : Chinese;
    }

    /// <summary>查表翻译：查不到原样返回中文（绝不半截替换）。</summary>
    public static string T(string zh)
    {
        if (zh is null) return "";
        if (LocalizationTable.Map.TryGetValue(zh, out string? english) && english is not null) return english;
        return zh;
    }

    /// <summary>
    /// 把 <paramref name="root"/> 这一棵树里看得见的中文换成当前语言。
    /// 处理 <c>TextBlock.Text</c>、<c>ContentControl.Content</c>（Button/CheckBox/TabItem/Label/列头…）、
    /// <c>HeaderedContentControl.Header</c> 与字符串 <c>ToolTip</c>。
    /// </summary>
    public static void Apply(FrameworkElement root)
    {
        if (root is null) return;
        Visit(root, new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance));
    }

    private static void Visit(DependencyObject node, HashSet<DependencyObject> seen)
    {
        // 逻辑树与可视树在校验里会重叠（Frame 的内容、ItemsControl 的子项…），用 seen 去重，
        // 否则同一棵树会被走好几遍 —— 结果虽然一样，代价随层数放大。
        if (!seen.Add(node)) return;
        TranslateOwn(node);

        // 逻辑树：XAML 里声明的子元素。页面刚构造、还没排版时也能走到 —— 「切页后立刻生效」靠的就是它。
        if (node is FrameworkElement or FrameworkContentElement)
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject dependency) Visit(dependency, seen);

        // 可视树：模板/生成出来的元素（列表列头、ComboBoxItem…）。没排过版时子节点数为 0，不会出错。
        if (node is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++) Visit(VisualTreeHelper.GetChild(node, i), seen);
        }
    }

    private static void TranslateOwn(DependencyObject node)
    {
        if (node is TextBlock text) ApplyString(text, TextOriginals, text.Text, value => text.Text = value);
        if (node is not FrameworkElement element) return;

        if (element.ToolTip is string tip) ApplyString(element, ToolTipOriginals, tip, value => element.ToolTip = value);
        if (element is ContentControl content && content.Content is string raw)
            ApplyString(content, ContentOriginals, raw, value => content.Content = value);
        if (element is HeaderedContentControl headered && headered.Header is string header)
            ApplyString(headered, HeaderOriginals, header, value => headered.Header = value);
    }

    /// <summary>一个控件上"我们翻译过的那串字符串"的记账（原文 + 我们写进去的英文）。</summary>
    private sealed class Applied
    {
        public string Original = "";
        public string English = "";
    }

    // Text / Content / Header / ToolTip 是同一个控件上的四个独立字符串，各自记一份，互不干扰。
    private static readonly ConditionalWeakTable<DependencyObject, Applied> TextOriginals = new();
    private static readonly ConditionalWeakTable<DependencyObject, Applied> ContentOriginals = new();
    private static readonly ConditionalWeakTable<DependencyObject, Applied> HeaderOriginals = new();
    private static readonly ConditionalWeakTable<DependencyObject, Applied> ToolTipOriginals = new();

    private static void ApplyString(
        DependencyObject owner,
        ConditionalWeakTable<DependencyObject, Applied> originals,
        string current,
        Action<string> write)
    {
        if (UiLanguage == English)
        {
            // 空的、纯数字/型号（L0、9999）没有可翻译的内容：词表的键全是中文，
            // 所以「不含汉字」就是最准的跳过判据。
            if (current.Length == 0 || !HasHan(current)) return;
            // 幂等：这一格已经是我们写进去的英文，再翻一次会查不到（英文不是键），本来也无害，
            // 但先短路掉省一次查表。
            if (originals.TryGetValue(owner, out Applied? applied) && applied.English == current) return;
            if (!LocalizationTable.Map.TryGetValue(current, out string? english)) return;
            originals.AddOrUpdate(owner, new Applied { Original = current, English = english });
            write(english);
            return;
        }

        // 切回中文：只有"当前内容仍然是我们写进去的那句英文"才复原，
        // 否则可能把别处（定时器/事件）刚设进去的新内容盖成旧内容。
        if (!originals.TryGetValue(owner, out Applied? record)) return;
        originals.Remove(owner);
        if (record.English == current) write(record.Original);
    }

    private static bool HasHan(string value)
    {
        foreach (char c in value)
            if (c is >= '\u4e00' and <= '\u9fff') return true;
        return false;
    }
}
