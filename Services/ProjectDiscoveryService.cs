using System.Diagnostics;
using System.IO;
using System.Text.Json;
using UELauncher.Models;

namespace UELauncher.Services;

public static class ProjectDiscoveryService
{
    private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "_Tools", ".git", ".vs", "Binaries", "Intermediate", "Saved", "DerivedDataCache", "Content"
    };

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cpp", ".c", ".h", ".hpp", ".inl", ".cs"
    };

    public static List<UnrealProject> DiscoverProjects(AppSettings settings, List<EngineInstall> engines)
    {
        var results = new List<UnrealProject>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in settings.ScanRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            // Check root itself and 1 level of subdirectories
            IEnumerable<string> dirsToScan;
            try
            {
                dirsToScan = Directory.EnumerateDirectories(root)
                    .Where(d => !SkipDirNames.Contains(Path.GetFileName(d)))
                    .Prepend(root);
            }
            catch
            {
                continue;
            }

            foreach (var dir in dirsToScan)
            {
                try
                {
                    foreach (var uproj in Directory.EnumerateFiles(dir, "*.uproject", SearchOption.TopDirectoryOnly))
                    {
                        var full = Path.GetFullPath(uproj);
                        if (!seenPaths.Add(full)) continue;
                        var proj = ParseProject(full, settings, engines);
                        if (proj is not null)
                        {
                            results.Add(proj);
                        }
                    }
                }
                catch { }
            }
        }

        return results;
    }

    public static UnrealProject? ParseProject(string uprojectPath, AppSettings settings, List<EngineInstall> engines)
    {
        try
        {
            var projectDir = Path.GetDirectoryName(uprojectPath)!;
            var name = Path.GetFileNameWithoutExtension(uprojectPath);
            var engineAssoc = "";
            var description = "";
            var modules = new List<string>();
            int enabledPlugins = 0;

            using (var doc = JsonDocument.Parse(File.ReadAllText(uprojectPath)))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("EngineAssociation", out var ea))
                    engineAssoc = ea.GetString() ?? "";
                if (root.TryGetProperty("Description", out var desc))
                    description = desc.GetString() ?? "";
                if (root.TryGetProperty("Modules", out var mods) && mods.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in mods.EnumerateArray())
                    {
                        if (m.TryGetProperty("Name", out var mn) && mn.GetString() is string mName)
                            modules.Add(mName);
                    }
                }
                if (root.TryGetProperty("Plugins", out var plugs) && plugs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in plugs.EnumerateArray())
                    {
                        if (p.TryGetProperty("Enabled", out var en) && en.ValueKind == JsonValueKind.True)
                            enabledPlugins++;
                    }
                }
            }

            // Detect Editor target from Source\*Editor.Target.cs (handles LyraEditor, etc.)
            string? detectedTarget = null;
            var sourceDir = Path.Combine(projectDir, "Source");
            if (Directory.Exists(sourceDir))
            {
                try
                {
                    var targetFile = Directory.EnumerateFiles(sourceDir, "*Editor.Target.cs", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault();
                    if (targetFile is not null)
                    {
                        var fn = Path.GetFileName(targetFile);
                        detectedTarget = fn[..^".Target.cs".Length];
                    }
                }
                catch { }
            }

            bool hasPluginCpp = false;
            var pluginsDir = Path.Combine(projectDir, "Plugins");
            if (Directory.Exists(pluginsDir))
            {
                try
                {
                    hasPluginCpp = Directory.EnumerateFiles(pluginsDir, "*.Build.cs", SearchOption.AllDirectories).Any();
                }
                catch { }
            }

            bool isCpp = modules.Count > 0 || detectedTarget is not null || hasPluginCpp;
            string editorTarget = detectedTarget ?? $"{name}Editor";

            settings.EngineOverrides.TryGetValue(uprojectPath, out var overrideRoot);
            var matchedEngine = EngineDiscoveryService.MatchEngine(engineAssoc, overrideRoot, engines);

            // Find best thumbnail
            string? thumbPath = FindThumbnail(projectDir, name);

            // Find solution file (.sln or .slnx)
            string? slnPath = null;
            var sln = Path.Combine(projectDir, $"{name}.sln");
            var slnx = Path.Combine(projectDir, $"{name}.slnx");
            if (File.Exists(sln)) slnPath = sln;
            else if (File.Exists(slnx)) slnPath = slnx;

            // Compute last modified time across .uproject, Config, Source, Content (fast top-level + Saved/Logs)
            var lastMod = File.GetLastWriteTime(uprojectPath);
            var logFile = Path.Combine(projectDir, "Saved", "Logs", $"{name}.log");
            if (File.Exists(logFile))
            {
                var lt = File.GetLastWriteTime(logFile);
                if (lt > lastMod) lastMod = lt;
            }

            var proj = new UnrealProject
            {
                Name = name,
                UProjectPath = uprojectPath,
                ProjectDir = projectDir,
                EngineAssociation = engineAssoc,
                Description = description,
                IsCpp = isCpp,
                EditorTarget = editorTarget,
                Modules = modules,
                EnabledPluginCount = enabledPlugins,
                LastModified = lastMod,
                ThumbnailPath = thumbPath,
                SolutionPath = slnPath,
                HasPreBuildTonemapper = File.Exists(Path.Combine(projectDir, "Setup-Tonemapper.ps1")),
                IsFavorite = settings.Favorites.Contains(uprojectPath),
                EffectiveEngine = matchedEngine,
                EngineOverrideRoot = overrideRoot
            };

            RefreshBuildStatus(proj);
            return proj;
        }
        catch
        {
            return null;
        }
    }

    public static void RefreshBuildStatus(UnrealProject project)
    {
        if (project.EffectiveEngine is null)
        {
            project.BuildStatus = ProjectBuildStatus.MissingEngine;
            project.StatusDetail = $"Engine '{project.EngineAssociation}' not installed on this PC.";
            return;
        }

        if (!project.IsCpp)
        {
            project.BuildStatus = ProjectBuildStatus.Blueprint;
            project.StatusDetail = "Blueprint-only project — ready for instant launch.";
            return;
        }

        // Check project Binaries/Win64/*.dll and *.target timestamps vs Source/ and Plugins/*/Source/
        var binDir = Path.Combine(project.ProjectDir, "Binaries", "Win64");
        DateTime newestBinary = DateTime.MinValue;
        bool hasBinary = false;

        if (Directory.Exists(binDir))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(binDir, "*.dll", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.EnumerateFiles(binDir, "*.target", SearchOption.TopDirectoryOnly)))
                {
                    hasBinary = true;
                    var wt = File.GetLastWriteTimeUtc(f);
                    if (wt > newestBinary) newestBinary = wt;
                }
            }
            catch { }
        }

        if (!hasBinary)
        {
            project.BuildStatus = ProjectBuildStatus.BuildNeeded;
            project.StatusDetail = "No compiled Editor binaries found yet.";
            return;
        }

        DateTime newestSource = File.GetLastWriteTimeUtc(project.UProjectPath);
        string? newestSourceName = null;

        void ScanCodeDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
                {
                    if (!CodeExtensions.Contains(Path.GetExtension(f))) continue;
                    var wt = File.GetLastWriteTimeUtc(f);
                    if (wt > newestSource)
                    {
                        newestSource = wt;
                        newestSourceName = Path.GetFileName(f);
                    }
                }
            }
            catch { }
        }

        ScanCodeDir(Path.Combine(project.ProjectDir, "Source"));
        var pluginsDir = Path.Combine(project.ProjectDir, "Plugins");
        if (Directory.Exists(pluginsDir))
        {
            try
            {
                foreach (var pDir in Directory.EnumerateDirectories(pluginsDir))
                {
                    ScanCodeDir(Path.Combine(pDir, "Source"));
                }
            }
            catch { }
        }

        // 2-second grace window for filesystem timestamp jitter
        if (newestSource > newestBinary.AddSeconds(2))
        {
            project.BuildStatus = ProjectBuildStatus.BuildNeeded;
            project.StatusDetail = newestSourceName is not null
                ? $"Source changed ({newestSourceName}) since last build."
                : ".uproject updated since last build.";
        }
        else
        {
            project.BuildStatus = ProjectBuildStatus.UpToDate;
            project.StatusDetail = $"Binaries up to date ({newestBinary.ToLocalTime():MMM dd HH:mm}).";
        }
    }

    /// <summary>
    /// Detects which projects currently have an active UnrealEditor.exe process open.
    /// Zero-cost when no UnrealEditor.exe is running.
    /// </summary>
    public static async Task RefreshRunningEditorsAsync(IEnumerable<UnrealProject> projects)
    {
        var projList = projects.ToList();
        var runningProcs = Process.GetProcessesByName("UnrealEditor");
        if (runningProcs.Length == 0)
        {
            foreach (var p in projList)
            {
                p.IsEditorRunning = false;
                p.RunningEditorPid = null;
            }
            return;
        }

        var cmdLines = new List<(int Pid, string CmdLine)>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_Process -Filter \\\"Name='UnrealEditor.exe'\\\" | ForEach-Object { \\\"$($_.ProcessId)|$($_.CommandLine)\\\" }\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                var output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var sep = line.IndexOf('|');
                    if (sep > 0 && int.TryParse(line[..sep].Trim(), out int pid))
                    {
                        cmdLines.Add((pid, line[(sep + 1)..].Trim()));
                    }
                }
            }
        }
        catch { }

        foreach (var proj in projList)
        {
            var match = cmdLines.FirstOrDefault(c =>
                c.CmdLine.Contains(proj.UProjectPath, StringComparison.OrdinalIgnoreCase) ||
                c.CmdLine.Contains($"{proj.Name}.uproject", StringComparison.OrdinalIgnoreCase));

            if (match.Pid > 0)
            {
                proj.IsEditorRunning = true;
                proj.RunningEditorPid = match.Pid;
            }
            else
            {
                proj.IsEditorRunning = false;
                proj.RunningEditorPid = null;
            }
        }
    }

    private static string? FindThumbnail(string projectDir, string projectName)
    {
        string[] candidates =
        {
            Path.Combine(projectDir, $"{projectName}.png"),
            Path.Combine(projectDir, "Saved", "AutoScreenshot.png"),
            Path.Combine(projectDir, $"{projectName}.jpg")
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        try
        {
            return Directory.EnumerateFiles(projectDir, "*.png", SearchOption.TopDirectoryOnly).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
