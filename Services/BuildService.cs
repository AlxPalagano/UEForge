using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using UELauncher.Models;

namespace UELauncher.Services;

public enum LogSeverity
{
    Info,
    Progress,
    Warning,
    Error,
    Success
}

public sealed record BuildLogEntry(string Text, LogSeverity Severity);

public sealed class BuildService
{
    private static readonly Regex UbtStepRegex = new(@"^\s*\[(\d+)/(\d+)\]", RegexOptions.Compiled);
    private Process? _activeProcess;
    private CancellationTokenSource? _cts;

    public bool IsBusy => _activeProcess is not null;

    public event Action<BuildLogEntry>? LogReceived;
    public event Action<double?, string>? ProgressChanged;

    public void CancelActiveTask()
    {
        try
        {
            _cts?.Cancel();
            if (_activeProcess is { HasExited: false } proc)
            {
                proc.Kill(entireProcessTree: true);
                Emit("Build cancelled by user.", LogSeverity.Warning);
            }
        }
        catch { }
    }

    public void LaunchEditor(UnrealProject project)
    {
        if (project.EffectiveEngine is null)
        {
            Emit($"Cannot launch {project.Name}: no matching Unreal Engine installation.", LogSeverity.Error);
            return;
        }

        var exe = project.EffectiveEngine.EditorExePath;
        if (!File.Exists(exe))
        {
            Emit($"UnrealEditor.exe not found at {exe}", LogSeverity.Error);
            return;
        }

        Emit($"Launching {project.Name} with {project.EffectiveEngine.DisplayName}...", LogSeverity.Success);
        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"\"{project.UProjectPath}\"",
            WorkingDirectory = project.ProjectDir,
            UseShellExecute = true
        });
    }

    public async Task<bool> RunPreBuildHookIfPresentAsync(UnrealProject project)
    {
        if (!project.HasPreBuildTonemapper || project.EffectiveEngine is null)
            return true;

        var script = Path.Combine(project.ProjectDir, "Setup-Tonemapper.ps1");
        if (!File.Exists(script)) return true;

        Emit("Running project pre-build hook: Setup-Tonemapper.ps1...", LogSeverity.Progress);
        var code = await RunProcessAsync(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -EngineRoot \"{project.EffectiveEngine.RootPath}\"",
            project.ProjectDir);

        if (code != 0)
            Emit("Setup-Tonemapper.ps1 reported a warning (continuing build).", LogSeverity.Warning);
        return true;
    }

    public async Task<bool> RegenerateProjectFilesAsync(UnrealProject project)
    {
        if (project.EffectiveEngine is null)
        {
            Emit("No engine associated with this project.", LogSeverity.Error);
            return false;
        }

        var ubt = project.EffectiveEngine.UbtExePath;
        if (!File.Exists(ubt))
        {
            Emit($"UnrealBuildTool.exe not found at {ubt}", LogSeverity.Error);
            return false;
        }

        ProgressChanged?.Invoke(null, $"Generating project files for {project.Name}...");
        Emit($"=== Regenerating VS Project Files ({project.Name}) ===", LogSeverity.Progress);
        var code = await RunProcessAsync(
            ubt,
            $"-projectfiles -project=\"{project.UProjectPath}\" -game -progress",
            project.ProjectDir);

        if (code == 0)
        {
            Emit("Project files regenerated successfully.", LogSeverity.Success);
            ProgressChanged?.Invoke(100, "Project files up to date.");
            return true;
        }

        Emit($"Project file generation failed (exit code {code}).", LogSeverity.Error);
        ProgressChanged?.Invoke(0, "Failed.");
        return false;
    }

    public async Task<bool> BuildEditorAsync(UnrealProject project, bool regenerateFirst = false)
    {
        if (project.EffectiveEngine is null)
        {
            Emit("No engine associated with this project.", LogSeverity.Error);
            return false;
        }

        await RunPreBuildHookIfPresentAsync(project);

        if (regenerateFirst || (project.IsCpp && string.IsNullOrEmpty(project.SolutionPath)))
        {
            if (!await RegenerateProjectFilesAsync(project))
                return false;
        }

        ProgressChanged?.Invoke(null, $"Building {project.EditorTarget} ({project.EffectiveEngine.DisplayName})...");
        Emit($"=== Building {project.EditorTarget} (Win64 Development) ===", LogSeverity.Progress);
        Emit($"Engine: {project.EffectiveEngine.RootPath}", LogSeverity.Info);

        var code = await RunProcessAsync(
            project.EffectiveEngine.BuildBatPath,
            $"{project.EditorTarget} Win64 Development -Project=\"{project.UProjectPath}\" -WaitMutex",
            project.ProjectDir);

        ProjectDiscoveryService.RefreshBuildStatus(project);

        if (code == 0)
        {
            Emit($"Build succeeded for {project.Name}!", LogSeverity.Success);
            ProgressChanged?.Invoke(100, "Build succeeded.");
            return true;
        }

        Emit($"Build failed for {project.Name} (exit code {code}).", LogSeverity.Error);
        ProgressChanged?.Invoke(0, "Build failed.");
        return false;
    }

    public async Task<bool> CleanRebuildAsync(UnrealProject project)
    {
        if (project.EffectiveEngine is null)
        {
            Emit("No engine associated with this project.", LogSeverity.Error);
            return false;
        }

        ProgressChanged?.Invoke(null, $"Cleaning {project.Name}...");
        Emit($"=== Clean Rebuild ({project.Name}) ===", LogSeverity.Progress);

        await Task.Run(() =>
        {
            void SafeDeleteDir(string path)
            {
                if (!Directory.Exists(path)) return;
                try
                {
                    Directory.Delete(path, true);
                    Emit($"Deleted {path}", LogSeverity.Info);
                }
                catch (Exception ex)
                {
                    Emit($"Could not delete {path}: {ex.Message}", LogSeverity.Warning);
                }
            }

            SafeDeleteDir(Path.Combine(project.ProjectDir, "Binaries"));
            SafeDeleteDir(Path.Combine(project.ProjectDir, "Intermediate"));
            SafeDeleteDir(Path.Combine(project.ProjectDir, ".vs"));

            var pluginsDir = Path.Combine(project.ProjectDir, "Plugins");
            if (Directory.Exists(pluginsDir))
            {
                foreach (var pDir in Directory.EnumerateDirectories(pluginsDir))
                {
                    SafeDeleteDir(Path.Combine(pDir, "Binaries"));
                    SafeDeleteDir(Path.Combine(pDir, "Intermediate"));
                }
            }

            foreach (var sln in Directory.EnumerateFiles(project.ProjectDir, "*.sln*", SearchOption.TopDirectoryOnly))
            {
                try { File.Delete(sln); } catch { }
            }
        });

        if (!await RegenerateProjectFilesAsync(project))
            return false;

        return await BuildEditorAsync(project, regenerateFirst: false);
    }

    public async Task<bool> PackageWin64Async(UnrealProject project, string archiveDir, string config = "Development")
    {
        if (project.EffectiveEngine is null)
        {
            Emit("No engine associated with this project.", LogSeverity.Error);
            return false;
        }

        var uat = project.EffectiveEngine.RunUatBatPath;
        if (!File.Exists(uat))
        {
            Emit($"RunUAT.bat not found at {uat}", LogSeverity.Error);
            return false;
        }

        ProgressChanged?.Invoke(null, $"Packaging {project.Name} (Win64 {config})...");
        Emit($"=== Packaging {project.Name} -> {archiveDir} ===", LogSeverity.Progress);

        var args = $"BuildCookRun -project=\"{project.UProjectPath}\" -noP4 -platform=Win64 " +
                   $"-clientconfig={config} -serverconfig={config} -cook -allmaps -build -stage -pak -archive " +
                   $"-archivedirectory=\"{archiveDir}\" -utf8output";

        var code = await RunProcessAsync(uat, args, project.ProjectDir);
        if (code == 0)
        {
            Emit($"Packaging succeeded! Output: {archiveDir}", LogSeverity.Success);
            ProgressChanged?.Invoke(100, "Package succeeded.");
            return true;
        }

        Emit($"Packaging failed (exit code {code}).", LogSeverity.Error);
        ProgressChanged?.Invoke(0, "Packaging failed.");
        return false;
    }

    private async Task<int> RunProcessAsync(string fileName, string arguments, string workingDir)
    {
        _cts = new CancellationTokenSource();
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _activeProcess = proc;

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) HandleOutputLine(e.Data, isErrorStream: false);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) HandleOutputLine(e.Data, isErrorStream: true);
        };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(_cts.Token);
            return proc.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        catch (Exception ex)
        {
            Emit($"Process error: {ex.Message}", LogSeverity.Error);
            return -1;
        }
        finally
        {
            _activeProcess = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void HandleOutputLine(string line, bool isErrorStream)
    {
        var m = UbtStepRegex.Match(line);
        if (m.Success &&
            int.TryParse(m.Groups[1].Value, out int step) &&
            int.TryParse(m.Groups[2].Value, out int total) &&
            total > 0)
        {
            double pct = Math.Clamp((double)step / total * 100.0, 0, 100);
            ProgressChanged?.Invoke(pct, $"Compiling [{step}/{total}]...");
            Emit(line, LogSeverity.Progress);
            return;
        }

        var lower = line.ToLowerInvariant();
        if (isErrorStream || lower.Contains("error c") || lower.Contains("error lnk") ||
            lower.Contains(": error:") || lower.Contains("fatal error") || lower.Contains("result: failed"))
        {
            Emit(line, LogSeverity.Error);
        }
        else if (lower.Contains("warning:") || lower.Contains("warning c") || lower.Contains("deprecation:"))
        {
            Emit(line, LogSeverity.Warning);
        }
        else if (lower.Contains("result: succeeded") || lower.Contains("build succeeded"))
        {
            Emit(line, LogSeverity.Success);
        }
        else
        {
            Emit(line, LogSeverity.Info);
        }
    }

    private void Emit(string text, LogSeverity severity) =>
        LogReceived?.Invoke(new BuildLogEntry(text, severity));
}
