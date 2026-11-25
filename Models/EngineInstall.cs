using System.IO;

namespace UELauncher.Models;

public sealed class EngineInstall
{
    public required string VersionKey { get; init; }
    public required string DisplayName { get; init; }
    public required string RootPath { get; init; }
    public bool IsCustom { get; init; }

    public string BuildBatPath => Path.Combine(RootPath, "Engine", "Build", "BatchFiles", "Build.bat");
    public string RunUatBatPath => Path.Combine(RootPath, "Engine", "Build", "BatchFiles", "RunUAT.bat");
    public string UbtExePath => Path.Combine(RootPath, "Engine", "Binaries", "DotNET", "UnrealBuildTool", "UnrealBuildTool.exe");
    public string EditorExePath => Path.Combine(RootPath, "Engine", "Binaries", "Win64", "UnrealEditor.exe");

    public bool IsValid => File.Exists(BuildBatPath) && File.Exists(EditorExePath);

    public override string ToString() => $"{DisplayName} ({RootPath})";
}
