using System.Diagnostics;

namespace Hexa.Services;

public enum GameEngineKind
{
    Unknown,
    RpgMakerMv,
    RpgMakerMz,
    RpgMakerVxAce,
    RenPy,
    UnityMono,
    UnityIl2Cpp,
    Unreal,
    Godot,
    GameMaker,
}

public sealed record GameEngineInfo(GameEngineKind Kind, string DisplayName, string ExecutablePath)
{
    public static readonly GameEngineInfo Unknown = new(GameEngineKind.Unknown, "未识别", "");
}

public static class GameEngineDetector
{
    public static GameEngineInfo DetectProcess(string? processName)
    {
        string normalized = GameTelemetryProtocol.NormalizeProcessName(processName);
        if (normalized.Length == 0) return GameEngineInfo.Unknown;
        try
        {
            foreach (Process process in Process.GetProcessesByName(normalized))
            {
                using (process)
                {
                    string? path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path)) return DetectExecutable(path);
                }
            }
        }
        catch { }
        return GameEngineInfo.Unknown;
    }

    public static GameEngineInfo DetectExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return GameEngineInfo.Unknown;
        string fullPath;
        try { fullPath = Path.GetFullPath(executablePath); }
        catch { return GameEngineInfo.Unknown; }
        string root = Path.GetDirectoryName(fullPath) ?? "";
        string stem = Path.GetFileNameWithoutExtension(fullPath);

        if (HasFile(root, "www", "js", "rmmz_core.js") || HasFile(root, "js", "rmmz_core.js"))
            return new(GameEngineKind.RpgMakerMz, "RPG Maker MZ", fullPath);
        if (HasFile(root, "www", "js", "rpg_core.js") || HasFile(root, "js", "rpg_core.js"))
            return new(GameEngineKind.RpgMakerMv, "RPG Maker MV", fullPath);
        if (Directory.EnumerateFiles(root, "RGSS3*.dll", System.IO.SearchOption.TopDirectoryOnly).Any()
            || HasFile(root, "Game.rgss3a"))
            return new(GameEngineKind.RpgMakerVxAce, "RPG Maker VX Ace", fullPath);

        if (HasFile(root, "UnityPlayer.dll") || Directory.Exists(Path.Combine(root, stem + "_Data")))
        {
            bool il2Cpp = HasFile(root, "GameAssembly.dll");
            return new(il2Cpp ? GameEngineKind.UnityIl2Cpp : GameEngineKind.UnityMono,
                il2Cpp ? "Unity IL2CPP" : "Unity Mono", fullPath);
        }

        if (stem.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase)
            || fullPath.Contains($"{Path.DirectorySeparatorChar}Binaries{Path.DirectorySeparatorChar}Win64{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return new(GameEngineKind.Unreal, "Unreal Engine", fullPath);

        if (Directory.Exists(Path.Combine(root, "renpy"))
            || Directory.Exists(Path.Combine(root, "lib", "windows-x86_64"))
            || Directory.Exists(Path.Combine(root, "game")) && Directory.EnumerateFiles(Path.Combine(root, "game"), "*.rpyc", System.IO.SearchOption.TopDirectoryOnly).Any())
            return new(GameEngineKind.RenPy, "Ren'Py", fullPath);

        if (HasFile(root, stem + ".pck") || Directory.EnumerateFiles(root, "*.pck", System.IO.SearchOption.TopDirectoryOnly).Any())
            return new(GameEngineKind.Godot, "Godot", fullPath);
        if (HasFile(root, "data.win"))
            return new(GameEngineKind.GameMaker, "GameMaker", fullPath);

        return new(GameEngineKind.Unknown, "未识别", fullPath);
    }

    private static bool HasFile(string root, params string[] parts) =>
        File.Exists(parts.Aggregate(root, Path.Combine));
}
