using System.Diagnostics;
using ReeLib;
using ReeLib.Common;

namespace ContentPatcher;

public static class ExeUtils
{
    /// <summary>
    /// Calculates a hash string based on an exe's metadata file version and detected PAK files.
    /// </summary>
    /// <returns>A string in the format `v[file_version]-[PAK_path_hash_hex]`, ex: "v1.0.3.0-A6</returns>
    public static string GetGameVersionHash(Workspace env, string? exePath = null)
    {
        string hash = string.Empty;
        var gamePath = env.Config.GamePath;
        exePath ??= FindGameExecutable(gamePath, env.Config.Game.name);
        if (exePath != null) {
            exePath = exePath.Replace("\\", "/");
            var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
            if (versionInfo.FileVersion != null) {
                hash = "v" + versionInfo.FileVersion;
            }
        }
        hash ??= "v.ukn";
        if (exePath == null) return hash;

        var paks = PakUtils.ScanPakFiles(Path.GetDirectoryName(exePath)!);
        if (paks.Count == 0) return hash;

        uint pakHash = 17;
        foreach (var pak in paks) {
            var pakRelativepath = Path.GetRelativePath(gamePath, pak).Replace("\\", "/");
            pakHash = unchecked(pakHash * 31 + MurMur3HashUtils.GetHash(pakRelativepath));
        }
        hash += "-" + pakHash.ToString("X8");
        return hash;
    }

    public static string? FindGameExecutable(string gamePath, string mostLikelyExeName)
    {
        var exes = Directory.GetFiles(gamePath, "*.exe");
        var exe = exes.Length == 1 ? exes[0] : exes.FirstOrDefault(e => Path.GetFileNameWithoutExtension(e) == mostLikelyExeName) ?? exes.FirstOrDefault(e =>
            e != System.Reflection.Assembly.GetEntryAssembly()?.Location
            && !Path.GetFileName(e).Contains("CrashReport", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(e).Contains("ContentPatcher", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(e).Contains("ContentEditor", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(e).Contains("Installer", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(exe)) {
            return exe;
        }

        return null;
    }
}