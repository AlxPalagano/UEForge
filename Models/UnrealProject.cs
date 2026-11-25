using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UELauncher.Models;

public enum ProjectBuildStatus
{
    Blueprint,
    UpToDate,
    BuildNeeded,
    MissingEngine
}

public sealed class UnrealProject : INotifyPropertyChanged
{
    private bool _isFavorite;
    private bool _isEditorRunning;
    private int? _runningEditorPid;
    private ProjectBuildStatus _buildStatus;
    private EngineInstall? _effectiveEngine;
    private string? _engineOverrideRoot;
    private string _statusDetail = "";
    private BitmapImage? _thumbnail;
    private bool _thumbnailLoaded;

    public required string Name { get; init; }
    public required string UProjectPath { get; init; }
    public required string ProjectDir { get; init; }
    public required string EngineAssociation { get; init; }
    public string Description { get; init; } = "";
    public bool IsCpp { get; init; }
    public required string EditorTarget { get; init; }
    public List<string> Modules { get; init; } = new();
    public int EnabledPluginCount { get; init; }
    public DateTime LastModified { get; init; }
    public string? ThumbnailPath { get; init; }
    public string? SolutionPath { get; init; }
    public bool HasPreBuildTonemapper { get; init; }

    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (_isFavorite != value) { _isFavorite = value; OnPropertyChanged(); OnPropertyChanged(nameof(FavoriteGlyph)); } }
    }

    public string FavoriteGlyph => IsFavorite ? "\u2605" : "\u2606";

    public bool IsEditorRunning
    {
        get => _isEditorRunning;
        set
        {
            if (_isEditorRunning != value)
            {
                _isEditorRunning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusBadgeText));
                OnPropertyChanged(nameof(StatusBadgeColor));
                OnPropertyChanged(nameof(PrimaryActionLabel));
            }
        }
    }

    public int? RunningEditorPid
    {
        get => _runningEditorPid;
        set { if (_runningEditorPid != value) { _runningEditorPid = value; OnPropertyChanged(); } }
    }

    public ProjectBuildStatus BuildStatus
    {
        get => _buildStatus;
        set
        {
            if (_buildStatus != value)
            {
                _buildStatus = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusBadgeText));
                OnPropertyChanged(nameof(StatusBadgeColor));
                OnPropertyChanged(nameof(PrimaryActionLabel));
            }
        }
    }

    public EngineInstall? EffectiveEngine
    {
        get => _effectiveEngine;
        set
        {
            if (!ReferenceEquals(_effectiveEngine, value))
            {
                _effectiveEngine = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EngineBadgeText));
                OnPropertyChanged(nameof(EngineDisplayPath));
            }
        }
    }

    public string? EngineOverrideRoot
    {
        get => _engineOverrideRoot;
        set { if (_engineOverrideRoot != value) { _engineOverrideRoot = value; OnPropertyChanged(); OnPropertyChanged(nameof(EngineBadgeText)); } }
    }

    public string StatusDetail
    {
        get => _statusDetail;
        set { if (_statusDetail != value) { _statusDetail = value; OnPropertyChanged(); } }
    }

    public string TypeBadgeText => IsCpp ? "C++" : "BP";
    public string TypeBadgeColor => IsCpp ? "#8B5CF6" : "#0EA5E9";

    public string EngineBadgeText =>
        EffectiveEngine is not null
            ? (string.IsNullOrEmpty(EngineOverrideRoot) ? EffectiveEngine.DisplayName : $"{EffectiveEngine.DisplayName}*")
            : (string.IsNullOrWhiteSpace(EngineAssociation)
                ? "UE ?"
                : (EngineAssociation.StartsWith("{") ? "UE Custom" : $"UE {EngineAssociation}"));

    public string EngineDisplayPath =>
        EffectiveEngine?.RootPath ?? "Engine not found — select an override below";

    public string StatusBadgeText => IsEditorRunning
        ? "Running"
        : BuildStatus switch
        {
            ProjectBuildStatus.UpToDate => "Ready",
            ProjectBuildStatus.BuildNeeded => "Build Needed",
            ProjectBuildStatus.Blueprint => "Blueprint",
            ProjectBuildStatus.MissingEngine => "No Engine",
            _ => "Ready"
        };

    public string StatusBadgeColor => IsEditorRunning
        ? "#10B981"
        : BuildStatus switch
        {
            ProjectBuildStatus.UpToDate => "#22C55E",
            ProjectBuildStatus.BuildNeeded => "#F59E0B",
            ProjectBuildStatus.Blueprint => "#38BDF8",
            ProjectBuildStatus.MissingEngine => "#EF4444",
            _ => "#22C55E"
        };

    public string PrimaryActionLabel => IsEditorRunning
        ? "Focus / Launch"
        : (IsCpp && BuildStatus == ProjectBuildStatus.BuildNeeded ? "Build & Run" : "Launch");

    public string Monogram
    {
        get
        {
            var parts = Name.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[1][0])}";
            return Name.Length >= 2 ? Name[..2].ToUpperInvariant() : Name.ToUpperInvariant();
        }
    }

    public string FallbackGradientStart
    {
        get
        {
            string[] palette = { "#1E3A5F", "#2D1F4E", "#1B3B36", "#3D2618", "#2B2D42", "#1F364D", "#3A1F34" };
            return palette[Math.Abs(Name.GetHashCode()) % palette.Length];
        }
    }

    public string FallbackGradientEnd
    {
        get
        {
            string[] palette = { "#0F1E33", "#18102B", "#0E1F1D", "#21140C", "#161824", "#101C29", "#1E0F1B" };
            return palette[Math.Abs(Name.GetHashCode()) % palette.Length];
        }
    }

    public string LastModifiedDisplay => LastModified.ToString("yyyy-MM-dd  HH:mm");

    public bool HasThumbnail => Thumbnail is not null;

    public BitmapImage? Thumbnail
    {
        get
        {
            if (!_thumbnailLoaded)
            {
                _thumbnailLoaded = true;
                if (!string.IsNullOrEmpty(ThumbnailPath) && File.Exists(ThumbnailPath))
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.DecodePixelWidth = 420;
                        bmp.UriSource = new Uri(ThumbnailPath, UriKind.Absolute);
                        bmp.EndInit();
                        bmp.Freeze();
                        _thumbnail = bmp;
                    }
                    catch
                    {
                        _thumbnail = null;
                    }
                }
            }
            return _thumbnail;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? prop = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
