using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using UELauncher.Models;

namespace UELauncher.Services;

public static class EngineDiscoveryService
{
    private static readonly Regex VersionRegex = new(@"UE[_]?(\d+\.\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<EngineInstall> DiscoverEngines(IEnumerable<string>? customPaths = null)
    {
        var found = new Dictionary<string, EngineInstall>(StringComparer.OrdinalIgnoreCase);

        void TryRegister(string? rootDir, string? explicitKey = null, bool isCustom = false)
        {
            if (string.IsNullOrWhiteSpace(rootDir)) return;
            var full = Path.GetFullPath(rootDir.Trim().TrimEnd('\\', '/'));
            if (found.ContainsKey(full)) return;

            var candidate = new EngineInstall
            {
                RootPath = full,
                VersionKey = explicitKey ?? InferVersionKey(full),
                DisplayName = BuildDisplayName(explicitKey ?? InferVersionKey(full)),
                IsCustom = isCustom
            };
            if (candidate.IsValid)
            {
                found[full] = candidate;
            }
        }

        // 1. User-configured custom engine installs
        if (customPaths is not null)
        {
            foreach (var p in customPaths)
            {
                TryRegister(p, isCustom: true);
            }
        }

        // 2. Windows Registry (Epic Games standard + source builds)
        ReadRegistryEngines(Registry.LocalMachine, @"SOFTWARE\EpicGames\Unreal Engine", TryRegister);
        ReadRegistryEngines(Registry.CurrentUser, @"SOFTWARE\EpicGames\Unreal Engine", TryRegister);
        ReadCustomBuildsRegistry(Registry.CurrentUser, @"SOFTWARE\Epic Games\Unreal Engine\Builds", TryRegister);

        // 3. Epic Games Launcher LauncherInstalled.dat
        try
        {
            var datPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
            if (File.Exists(datPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(datPath));
                if (doc.RootElement.TryGetProperty("InstallationList", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in list.EnumerateArray())
                    {
                        var appName = item.TryGetProperty("AppName", out var an) ? an.GetString() : null;
                        var loc = item.TryGetProperty("InstallLocation", out var il) ? il.GetString() : null;
                        if (!string.IsNullOrEmpty(appName) && appName.StartsWith("UE_", StringComparison.OrdinalIgnoreCase))
                        {
                            TryRegister(loc, appName[3..]);
                        }
                    }
                }
            }
        }
        catch { }

        // 4. Scan common Epic Games folders on drives C..G (catches UE_5.8, UE5.5, etc.)
        foreach (var drive in new[] { "C", "D", "E", "F", "G" })
        {
            string[] bases =
            {
                $@"{drive}:\Program Files\Epic Games",
                $@"{drive}:\Epic Games",
                $@"{drive}:\"
            };
            foreach (var b in bases)
            {
                if (!Directory.Exists(b)) continue;
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(b, "UE*"))
                    {
                        TryRegister(dir);
                    }
                }
                catch { }
            }
        }

        return found.Values
            .OrderByDescending(e => ParseSortableVersion(e.VersionKey))
            .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static EngineInstall? MatchEngine(string engineAssociation, string? overrideRoot, List<EngineInstall> installs)
    {
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            var ov = installs.FirstOrDefault(e => e.RootPath.Equals(overrideRoot, StringComparison.OrdinalIgnoreCase));
            if (ov is not null) return ov;
        }

        if (string.IsNullOrWhiteSpace(engineAssociation))
            return installs.FirstOrDefault();

        var assoc = engineAssociation.Trim();
        var exact = installs.FirstOrDefault(e => e.VersionKey.Equals(assoc, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        // Try matching major.minor prefix (e.g. "5.8.4" -> "5.8")
        var parts = assoc.Split('.');
        if (parts.Length >= 2)
        {
            var majorMinor = $"{parts[0]}.{parts[1]}";
            var match = installs.FirstOrDefault(e => e.VersionKey.Equals(majorMinor, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return null;
    }

    private static void ReadRegistryEngines(RegistryKey hive, string subKeyPath, Action<string?, string?, bool> register)
    {
        try
        {
            using var root = hive.OpenSubKey(subKeyPath);
            if (root is null) return;
            foreach (var subName in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(subName);
                var dir = sub?.GetValue("InstalledDirectory") as string;
                register(dir, subName, false);
            }
        }
        catch { }
    }

    private static void ReadCustomBuildsRegistry(RegistryKey hive, string subKeyPath, Action<string?, string?, bool> register)
    {
        try
        {
            using var root = hive.OpenSubKey(subKeyPath);
            if (root is null) return;
            foreach (var valName in root.GetValueNames())
            {
                var dir = root.GetValue(valName) as string;
                register(dir, valName, true);
            }
        }
        catch { }
    }

    private static string InferVersionKey(string rootDir)
    {
        var folderName = Path.GetFileName(rootDir);
        var m = VersionRegex.Match(folderName);
        if (m.Success) return m.Groups[1].Value;

        // Try reading Engine/Build/Build.version
        try
        {
            var bv = Path.Combine(rootDir, "Engine", "Build", "Build.version");
            if (File.Exists(bv))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(bv));
                if (doc.RootElement.TryGetProperty("MajorVersion", out var maj) &&
                    doc.RootElement.TryGetProperty("MinorVersion", out var min))
                {
                    return $"{maj.GetInt32()}.{min.GetInt32()}";
                }
            }
        }
        catch { }

        return folderName;
    }

    private static string BuildDisplayName(string versionKey)
    {
        if (Version.TryParse(versionKey, out _))
            return $"UE {versionKey}";
        if (versionKey.StartsWith("UE", StringComparison.OrdinalIgnoreCase))
            return versionKey;
        return versionKey.Length > 12 ? $"Custom ({versionKey[..8]})" : $"UE {versionKey}";
    }

    private static Version ParseSortableVersion(string key)
    {
        if (Version.TryParse(key, out var v)) return v;
        return new Version(0, 0);
    }
}
