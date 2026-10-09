using System.IO;
using Hexa.Services;

namespace Hexa.Models;

/// <summary>脚本库中的一条脚本记录。</summary>
public sealed record ScriptEntry(string FilePath, string Name, long DurationMs, int TrackCount, bool Valid, string? Error = null);

/// <summary>批量导入结果中的单个文件（导入后重新解析一次，供 UI 提示时长/轨道/失败原因）。</summary>
public sealed record ImportedScript(string Name, long DurationMs, int TrackCount, string? Error, bool IsAxisFile);

/// <summary>批量导入结果。</summary>
public sealed record ImportSummary(IReadOnlyList<ImportedScript> Scripts)
{
    public int FileCount => Scripts.Count;
    public int AxisFileCount => Scripts.Count(script => script.IsAxisFile);
    public int InvalidCount => Scripts.Count(script => script.Error != null);
}

/// <summary>
/// 脚本库管理 — 在 %LocalAppData%\Hexa\Scripts 下管理 funscript/波形脚本：
/// 列出、导入、重命名、删除（删除仅删除库内副本，不碰源文件）。
/// </summary>
public static class ScriptLibrary
{
    /// <summary>用户自定义目录（由设置页写入；空 = 用默认目录）。</summary>
    public static string? CustomFolder { get; set; }

    private static readonly string DefaultScriptFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hexa", "Scripts");

    public static string Folder => string.IsNullOrWhiteSpace(CustomFolder) ? DefaultScriptFolder : CustomFolder!;

    public static void EnsureFolder()
    {
        try { Directory.CreateDirectory(Folder); } catch { }
    }

    /// <summary>列出库内全部 .funscript，附带时长/轨道数；解析失败的标记为无效。</summary>
    public static List<ScriptEntry> List()
    {
        EnsureFolder();
        var result = new List<ScriptEntry>();
        foreach (string file in Directory.EnumerateFiles(Folder, "*.funscript", System.IO.SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            try
            {
                var set = FunscriptTrackLoader.LoadCompanionSet(file);
                result.Add(new ScriptEntry(file, name, set.DurationMs, set.Tracks.Count, Valid: true));
            }
            catch (Exception ex)
            {
                // 不静默吞掉原因：列表行用它做 ToolTip，导入提示也会直接列出来。
                result.Add(new ScriptEntry(file, name + "（无效）", 0, 0, Valid: false, Error: DescribeParseError(ex)));
            }
        }
        return result.OrderBy(entry => entry.Name).ToList();
    }

    /// <summary>导入时在库内的目标路径（与 Import 内部一致，供调用方先做同名冲突检查）。</summary>
    public static string ImportPathFor(string sourcePath) =>
        Path.Combine(Folder, Path.GetFileName(sourcePath));

    /// <summary>重命名时在库内的目标路径（与 Rename 内部一致，供调用方先做同名冲突检查）。</summary>
    public static string RenamePathFor(string newName)
    {
        string clean = (newName ?? "").Trim();
        // 去掉可能误输入的扩展名，统一补 .funscript
        string stem = Path.GetFileNameWithoutExtension(clean);
        return Path.Combine(Folder, stem + ".funscript");
    }

    /// <summary>
    /// 把一个脚本文件复制进库（不删除源文件）。返回库内路径。
    /// overwrite=false（默认）且库内已有同名脚本时抛 InvalidOperationException，绝不静默覆盖。
    /// </summary>
    public static string Import(string sourcePath, bool overwrite = false)
    {
        EnsureFolder();
        string dest = ImportPathFor(sourcePath);
        // 目标就是源文件本身（用户直接选了库内脚本）：无需复制，避免 File.Copy 因同路径抛 IOException。
        if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
            return dest;
        if (!overwrite && File.Exists(dest))
            throw new InvalidOperationException("同名脚本已存在");
        File.Copy(sourcePath, dest, overwrite: true);
        return dest;
    }

    /// <summary>
    /// 把一个选中路径展开成「它自己 + 同名伴生轴文件」。
    /// 微调波形「导出 ALL」按轴拆成 name.L0..R2.funscript，只导入其中一个会让其它轴在播放时
    /// 被 FunscriptPlayerService.Sample 静默填成中位 50，所以多选导入必须把伴生轴一起带上。
    /// 轴后缀识别复用 FunscriptTrackLoader.AxisFromFileName，不在这里重复维护后缀表。
    /// </summary>
    public static List<string> ExpandCompanionFiles(IEnumerable<string> selectedPaths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in selectedPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string full = Path.GetFullPath(path);
            Add(full);   // 选中文件本身一定要导入（即使后缀不认识）

            // 选中的可能是 name.funscript，也可能是 name.L0.funscript：两种都要能找回基准名
            string withoutExtension = Path.ChangeExtension(full, null);
            bool hasAxisSuffix = FunscriptTrackLoader.AxisFromFileName(full) != null;
            string basePath = hasAxisSuffix ? Path.ChangeExtension(withoutExtension, null) : withoutExtension;
            string? directory = Path.GetDirectoryName(basePath);
            string stem = Path.GetFileName(basePath);
            if (directory == null || stem.Length == 0) continue;

            // 只认 FunscriptTrackLoader 认识的后缀（stroke/surge/… 与 L0…R2），不误抓无关同名文件
            foreach (string candidate in Directory.EnumerateFiles(directory, stem + ".*.funscript"))
            {
                if (FunscriptTrackLoader.AxisFromFileName(candidate) == null) continue;
                Add(candidate);
            }
        }
        return result;

        void Add(string candidate)
        {
            if (!File.Exists(candidate)) return;
            string fullCandidate = Path.GetFullPath(candidate);
            if (seen.Add(fullCandidate)) result.Add(fullCandidate);
        }
    }

    /// <summary>
    /// 批量导入前的同名冲突检查：返回会被覆盖的库内目标路径。
    /// 源文件本身就在库内（目标 = 源）不算冲突，否则「导入」一个库内脚本会莫名其妙问是否覆盖自己。
    /// </summary>
    public static List<string> ConflictingTargets(IReadOnlyList<string> sourcePaths)
    {
        var targets = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in sourcePaths)
        {
            string dest = ImportPathFor(source);
            if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
                continue;
            if (!File.Exists(dest)) continue;
            if (seen.Add(Path.GetFullPath(dest))) targets.Add(dest);
        }
        return targets;
    }

    /// <summary>
    /// 批量导入（含伴生轴文件），返回逐个文件的解析结果供 UI 提示。
    /// overwrite=false（默认）且库内已有同名脚本时抛 InvalidOperationException，绝不静默覆盖；
    /// 冲突是「先整体检查再复制」，避免复制了一半才报错。
    /// </summary>
    public static ImportSummary ImportMany(IReadOnlyList<string> sourcePaths, bool overwrite = false)
    {
        EnsureFolder();
        var conflicts = ConflictingTargets(sourcePaths);
        if (conflicts.Count > 0 && !overwrite)
            throw new InvalidOperationException(
                "同名脚本已存在：" + string.Join("、", conflicts.Select(dest => Path.GetFileNameWithoutExtension(dest))));

        var imported = new List<ImportedScript>();
        foreach (string source in sourcePaths)
        {
            string dest = Import(source, overwrite);
            imported.Add(DescribeImported(dest));
        }
        return new ImportSummary(imported);
    }

    /// <summary>导入后重新解析一次库内文件，得到「时长 / 轨道数 / 解析错误」。</summary>
    private static ImportedScript DescribeImported(string filePath)
    {
        string name = Path.GetFileNameWithoutExtension(filePath);
        bool axisFile = FunscriptTrackLoader.AxisFromFileName(filePath) != null;
        try
        {
            var set = FunscriptTrackLoader.LoadCompanionSet(filePath);
            return new ImportedScript(name, set.DurationMs, set.Tracks.Count, null, axisFile);
        }
        catch (Exception ex)
        {
            return new ImportedScript(name, 0, 0, DescribeParseError(ex), axisFile);
        }
    }

    /// <summary>
    /// 解析错误的中文说法统一由 <see cref="WaveScriptCodec.DescribeParseError"/> 提供。
    /// 以前这里抄了一份一模一样的 switch，两处一旦改一处就会给出不同的提示。
    /// </summary>
    private static string DescribeParseError(Exception ex) => WaveScriptCodec.DescribeParseError(ex);

    /// <summary>
    /// 重命名库内脚本（保留 .funscript 后缀）。
    /// overwrite=false（默认）且库内已有同名脚本时抛 InvalidOperationException，绝不静默覆盖。
    /// </summary>
    public static string Rename(string filePath, string newName, bool overwrite = false)
    {
        string clean = (newName ?? "").Trim();
        if (clean.Length == 0) throw new InvalidOperationException("名称不能为空。");
        string dest = RenamePathFor(clean);
        // 名称未变：直接返回（避免 File.Move 同路径异常），调用方也无需确认覆盖。
        if (string.Equals(dest, filePath, StringComparison.OrdinalIgnoreCase))
            return dest;
        if (!overwrite && File.Exists(dest))
            throw new InvalidOperationException("同名脚本已存在");
        File.Move(filePath, dest, overwrite: true);
        return dest;
    }

    /// <summary>删除库内脚本（仅删除库内副本）。</summary>
    public static void Delete(string filePath) => File.Delete(filePath);
}
