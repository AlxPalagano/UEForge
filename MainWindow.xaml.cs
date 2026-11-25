using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using UELauncher.Helpers;
using UELauncher.Models;
using UELauncher.Services;
using UELauncher.Views;

namespace UELauncher;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly BuildService _buildService = new();
    private readonly DispatcherTimer _editorPollTimer;
    private readonly DispatcherTimer _elapsedTimer;
    private readonly Stopwatch _buildStopwatch = new();

    private List<EngineInstall> _engines = new();
    private List<UnrealProject> _allProjects = new();
    private UnrealProject? _selectedProject;
    private bool _suppressEngineCombo;
    private bool _isConsoleExpanded;

    private static readonly SolidColorBrush BrushInfo = new(Color.FromRgb(203, 213, 225));
    private static readonly SolidColorBrush BrushProgress = new(Color.FromRgb(56, 189, 248));
    private static readonly SolidColorBrush BrushWarning = new(Color.FromRgb(251, 191, 36));
    private static readonly SolidColorBrush BrushError = new(Color.FromRgb(248, 113, 113));
    private static readonly SolidColorBrush BrushSuccess = new(Color.FromRgb(74, 222, 128));

    public MainWindow()
    {
        InitializeComponent();
        NativeWindowTheme.ApplyDarkTitleBar(this);

        _settings = AppSettings.Load();
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);

        // Select persisted sort mode
        foreach (ComboBoxItem item in SortCombo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), _settings.SortMode, StringComparison.OrdinalIgnoreCase))
            {
                SortCombo.SelectedItem = item;
                break;
            }
        }
        if (SortCombo.SelectedIndex < 0) SortCombo.SelectedIndex = 0;

        _buildService.LogReceived += OnBuildLogReceived;
        _buildService.ProgressChanged += OnBuildProgressChanged;

        _editorPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _editorPollTimer.Tick += async (_, _) => await PollRunningEditorsAsync();

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _elapsedTimer.Tick += (_, _) =>
        {
            if (_buildStopwatch.IsRunning)
                ElapsedTimerText.Text = _buildStopwatch.Elapsed.ToString(@"mm\:ss");
        };

        Loaded += async (_, _) =>
        {
            await RescanAllAsync();
            _editorPollTimer.Start();
        };

        Closing += (_, _) =>
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.Save();
            if (_buildService.IsBusy) _buildService.CancelActiveTask();
        };
    }

    private async Task RescanAllAsync()
    {
        HeaderSummaryText.Text = "Scanning engines & projects...";

        var (engines, projects) = await Task.Run(() =>
        {
            var eng = EngineDiscoveryService.DiscoverEngines(_settings.CustomEnginePaths);
            var projs = ProjectDiscoveryService.DiscoverProjects(_settings, eng);
            return (eng, projs);
        });

        _engines = engines;
        _allProjects = projects;

        await ProjectDiscoveryService.RefreshRunningEditorsAsync(_allProjects);

        HeaderSummaryText.Text = $"{_allProjects.Count} projects  \u2022  {_engines.Count} UE installs detected";
        ApplyFilterAndSort();

        // Restore selection
        UnrealProject? toSelect = null;
        if (_selectedProject is not null)
            toSelect = _allProjects.FirstOrDefault(p => p.UProjectPath.Equals(_selectedProject.UProjectPath, StringComparison.OrdinalIgnoreCase));
        if (toSelect is null && !string.IsNullOrEmpty(_settings.LastSelectedProjectPath))
            toSelect = _allProjects.FirstOrDefault(p => p.UProjectPath.Equals(_settings.LastSelectedProjectPath, StringComparison.OrdinalIgnoreCase));
        toSelect ??= (ProjectsItemsControl.ItemsSource as IEnumerable<UnrealProject>)?.FirstOrDefault();

        SelectProject(toSelect);
    }

    private async Task PollRunningEditorsAsync()
    {
        if (_allProjects.Count == 0) return;
        await ProjectDiscoveryService.RefreshRunningEditorsAsync(_allProjects);
        if (_selectedProject is not null)
        {
            ProjectDiscoveryService.RefreshBuildStatus(_selectedProject);
            UpdateInspectorUI();
        }
    }

    private void ApplyFilterAndSort()
    {
        if (ProjectsItemsControl is null) return;

        var query = SearchBox?.Text?.Trim() ?? "";
        IEnumerable<UnrealProject> q = _allProjects;

        if (!string.IsNullOrEmpty(query))
        {
            q = q.Where(p =>
                p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.EngineBadgeText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.ProjectDir.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (FilterFav?.IsChecked == true)
            q = q.Where(p => p.IsFavorite);
        else if (FilterCpp?.IsChecked == true)
            q = q.Where(p => p.IsCpp);
        else if (FilterBp?.IsChecked == true)
            q = q.Where(p => !p.IsCpp);
        else if (FilterRunning?.IsChecked == true)
            q = q.Where(p => p.IsEditorRunning);

        var sortTag = (SortCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Recent";
        var ordered = sortTag switch
        {
            "Name" => q.OrderByDescending(p => p.IsFavorite).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            "Engine" => q.OrderByDescending(p => p.IsFavorite).ThenByDescending(p => p.EngineAssociation).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            _ => q.OrderByDescending(p => p.IsFavorite).ThenByDescending(p => p.IsEditorRunning).ThenByDescending(p => p.LastModified)
        };

        var list = ordered.ToList();
        ProjectsItemsControl.ItemsSource = list;
        EmptyStatePanel.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCardSelectionVisuals();
    }

    private void SelectProject(UnrealProject? project)
    {
        _selectedProject = project;
        if (project is not null)
        {
            _settings.LastSelectedProjectPath = project.UProjectPath;
            _settings.Save();
            ProjectDiscoveryService.RefreshBuildStatus(project);
        }
        UpdateInspectorUI();
        UpdateCardSelectionVisuals();
    }

    private void UpdateCardSelectionVisuals()
    {
        ProjectsItemsControl.UpdateLayout();
        for (int i = 0; i < ProjectsItemsControl.Items.Count; i++)
        {
            if (ProjectsItemsControl.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter cp)
            {
                if (VisualTreeHelper.GetChildrenCount(cp) > 0 && VisualTreeHelper.GetChild(cp, 0) is Border bd)
                {
                    bool selected = ReferenceEquals(ProjectsItemsControl.Items[i], _selectedProject);
                    bd.BorderBrush = selected
                        ? new SolidColorBrush(Color.FromRgb(56, 189, 248))
                        : new SolidColorBrush(Color.FromRgb(38, 48, 66));
                    bd.BorderThickness = selected ? new Thickness(2) : new Thickness(1.2);
                }
            }
        }
    }

    private void UpdateInspectorUI()
    {
        if (_selectedProject is null)
        {
            InspectorPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var p = _selectedProject;
        InspectorPanel.Visibility = Visibility.Visible;

        InspectorName.Text = p.Name;
        InspectorPath.Text = p.UProjectPath;
        InspectorMonogram.Text = p.Monogram;
        InspectorThumb.Source = p.Thumbnail;
        InspectorFallbackBg.Background = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(p.FallbackGradientStart),
            (Color)ColorConverter.ConvertFromString(p.FallbackGradientEnd),
            45);

        InspectorStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(p.StatusBadgeColor));
        InspectorStatusTitle.Text = p.IsEditorRunning
            ? $"Unreal Editor Running (PID {p.RunningEditorPid})"
            : p.StatusBadgeText;
        InspectorStatusDetail.Text = p.IsEditorRunning
            ? "Close Editor before running a C++ rebuild to avoid locked DLLs."
            : p.StatusDetail;
        CloseEditorBtn.Visibility = p.IsEditorRunning ? Visibility.Visible : Visibility.Collapsed;

        // Primary CTA text
        if (p.BuildStatus == ProjectBuildStatus.MissingEngine)
        {
            PrimaryRunTitle.Text = "Select Engine Below";
            PrimaryRunSub.Text = $"Engine '{p.EngineAssociation}' not detected";
        }
        else if (p.IsCpp && p.BuildStatus == ProjectBuildStatus.BuildNeeded)
        {
            PrimaryRunTitle.Text = "\u25B6  Build & Run";
            PrimaryRunSub.Text = $"Compile {p.EditorTarget}, then open {p.EffectiveEngine?.DisplayName}";
        }
        else
        {
            PrimaryRunTitle.Text = "\u25B6  Launch Editor";
            PrimaryRunSub.Text = p.IsCpp
                ? $"Binaries up to date \u2014 instant launch ({p.EffectiveEngine?.DisplayName})"
                : $"Blueprint project \u2014 instant launch ({p.EffectiveEngine?.DisplayName})";
        }

        bool busy = _buildService.IsBusy;
        bool hasEngine = p.EffectiveEngine is not null;
        PrimaryRunBtn.IsEnabled = !busy && hasEngine;
        BtnLaunchOnly.IsEnabled = !busy && hasEngine;
        BtnBuildEditor.IsEnabled = !busy && hasEngine && p.IsCpp;
        BtnCleanRebuild.IsEnabled = !busy && hasEngine && p.IsCpp;
        BtnRegenSln.IsEnabled = !busy && hasEngine && p.IsCpp;
        BtnOpenIde.IsEnabled = !busy && p.IsCpp;
        BtnPackage.IsEnabled = !busy && hasEngine;

        // Populate engine override dropdown
        _suppressEngineCombo = true;
        EngineOverrideCombo.Items.Clear();
        var autoMatch = EngineDiscoveryService.MatchEngine(p.EngineAssociation, null, _engines);
        var autoLabel = autoMatch is not null
            ? $"Auto ({autoMatch.DisplayName} \u2014 from .uproject)"
            : $"Auto (UE {p.EngineAssociation} \u2014 not found)";
        EngineOverrideCombo.Items.Add(new ComboBoxItem { Content = autoLabel, Tag = "" });

        int selectedIdx = 0;
        for (int i = 0; i < _engines.Count; i++)
        {
            var eng = _engines[i];
            EngineOverrideCombo.Items.Add(new ComboBoxItem { Content = $"{eng.DisplayName}  ({eng.RootPath})", Tag = eng.RootPath });
            if (!string.IsNullOrEmpty(p.EngineOverrideRoot) &&
                p.EngineOverrideRoot.Equals(eng.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                selectedIdx = i + 1;
            }
        }
        EngineOverrideCombo.SelectedIndex = selectedIdx;
        _suppressEngineCombo = false;

        // Metadata
        MetaType.Text = p.IsCpp ? "C++ & Blueprints" : "Blueprint Only";
        MetaTarget.Text = p.IsCpp ? p.EditorTarget : "N/A";
        MetaModules.Text = p.Modules.Count > 0 ? string.Join(", ", p.Modules) : (p.IsCpp ? "(Plugins / Source)" : "None");
        MetaPlugins.Text = $"{p.EnabledPluginCount} enabled";
        MetaModified.Text = p.LastModifiedDisplay;
    }

    // =========================================================================
    // Build & Launch Workflows
    // =========================================================================

    private async Task<bool> ConfirmIfEditorRunningForBuildAsync(UnrealProject project)
    {
        await ProjectDiscoveryService.RefreshRunningEditorsAsync(new[] { project });
        if (!project.IsEditorRunning) return true;

        var result = System.Windows.MessageBox.Show(
            $"Unreal Editor is currently running for '{project.Name}' (PID {project.RunningEditorPid}).\n\n" +
            "Close that Editor cleanly before building?\n\n" +
            "  \u2022 Yes = Close Editor and build\n" +
            "  \u2022 No = Build anyway (may fail if DLLs are locked)\n" +
            "  \u2022 Cancel = Abort",
            "Unreal Editor Running",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes && project.RunningEditorPid is int pid)
        {
            await CloseEditorProcessAsync(pid, project.Name);
            await ProjectDiscoveryService.RefreshRunningEditorsAsync(new[] { project });
        }
        return true;
    }

    private async Task CloseEditorProcessAsync(int pid, string projectName)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            AppendConsoleLine(new BuildLogEntry($"Closing Unreal Editor for {projectName} (PID {pid})...", LogSeverity.Warning));
            proc.CloseMainWindow();
            if (!await Task.Run(() => proc.WaitForExit(6000)))
            {
                proc.Kill(entireProcessTree: true);
                await Task.Run(() => proc.WaitForExit(3000));
            }
            AppendConsoleLine(new BuildLogEntry("Unreal Editor closed.", LogSeverity.Info));
        }
        catch (Exception ex)
        {
            AppendConsoleLine(new BuildLogEntry($"Could not close Editor PID {pid}: {ex.Message}", LogSeverity.Warning));
        }
    }

    private async Task RunManagedBuildAsync(Func<Task> action)
    {
        if (_buildService.IsBusy) return;

        SetConsoleExpanded(true);
        CancelBuildBtn.Visibility = Visibility.Visible;
        BuildProgressBar.Visibility = Visibility.Visible;
        BuildProgressBar.IsIndeterminate = true;
        _buildStopwatch.Restart();
        _elapsedTimer.Start();
        UpdateInspectorUI();

        try
        {
            await action();
        }
        finally
        {
            _buildStopwatch.Stop();
            _elapsedTimer.Stop();
            ElapsedTimerText.Text = _buildStopwatch.Elapsed.ToString(@"mm\:ss");
            CancelBuildBtn.Visibility = Visibility.Collapsed;
            BuildProgressBar.IsIndeterminate = false;
            if (_selectedProject is not null)
                ProjectDiscoveryService.RefreshBuildStatus(_selectedProject);
            UpdateInspectorUI();
        }
    }

    private async Task SmartBuildAndRunProjectAsync(UnrealProject project)
    {
        if (project.EffectiveEngine is null)
        {
            System.Windows.MessageBox.Show(
                $"No Unreal Engine installation matched '{project.EngineAssociation}'.\nPick an override in the Inspector or add the engine under Folders & Engines.",
                "Engine Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ProjectDiscoveryService.RefreshBuildStatus(project);
        if (!project.IsCpp || project.BuildStatus == ProjectBuildStatus.UpToDate)
        {
            _buildService.LaunchEditor(project);
            ConsoleStatusText.Text = $"Launched {project.Name}";
            return;
        }

        if (!await ConfirmIfEditorRunningForBuildAsync(project)) return;

        await RunManagedBuildAsync(async () =>
        {
            bool ok = await _buildService.BuildEditorAsync(project, regenerateFirst: false);
            if (ok)
            {
                _buildService.LaunchEditor(project);
            }
        });
    }

    // =========================================================================
    // UI Event Handlers
    // =========================================================================

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool hasText = !string.IsNullOrEmpty(SearchBox.Text);
        SearchPlaceholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        ClearSearchBtn.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilterAndSort();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) => SearchBox.Clear();

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilterAndSort();

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _settings.SortMode = tag;
            _settings.Save();
        }
        ApplyFilterAndSort();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RescanAllAsync();

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog(_settings, _engines) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            await RescanAllAsync();
        }
    }

    private async void ProjectCard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is UnrealProject proj)
        {
            SelectProject(proj);
            if (e.ClickCount == 2)
            {
                await SmartBuildAndRunProjectAsync(proj);
            }
        }
    }

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.Tag is UnrealProject proj)
        {
            proj.IsFavorite = !proj.IsFavorite;
            if (proj.IsFavorite) _settings.Favorites.Add(proj.UProjectPath);
            else _settings.Favorites.Remove(proj.UProjectPath);
            _settings.Save();
            ApplyFilterAndSort();
        }
    }

    private async void CardQuickPlay_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.Tag is UnrealProject proj)
        {
            SelectProject(proj);
            await SmartBuildAndRunProjectAsync(proj);
        }
    }

    private async void SmartBuildAndRun_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is not null)
            await SmartBuildAndRunProjectAsync(_selectedProject);
    }

    private void LaunchOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is not null)
        {
            _buildService.LaunchEditor(_selectedProject);
            ConsoleStatusText.Text = $"Launched {_selectedProject.Name}";
        }
    }

    private async void BuildEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null) return;
        if (!await ConfirmIfEditorRunningForBuildAsync(_selectedProject)) return;
        await RunManagedBuildAsync(() => _buildService.BuildEditorAsync(_selectedProject, regenerateFirst: false));
    }

    private async void CleanRebuild_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null) return;
        var confirm = System.Windows.MessageBox.Show(
            $"Clean Rebuild will delete Binaries, Intermediate, and .sln files for '{_selectedProject.Name}' (and its plugins), then regenerate and compile from scratch.\n\nProceed?",
            "Confirm Clean Rebuild", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        if (!await ConfirmIfEditorRunningForBuildAsync(_selectedProject)) return;
        await RunManagedBuildAsync(() => _buildService.CleanRebuildAsync(_selectedProject));
    }

    private async void RegenSln_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null) return;
        await RunManagedBuildAsync(() => _buildService.RegenerateProjectFilesAsync(_selectedProject));
    }

    private async void OpenIde_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null) return;
        var sln = Path.Combine(_selectedProject.ProjectDir, $"{_selectedProject.Name}.sln");
        var slnx = Path.Combine(_selectedProject.ProjectDir, $"{_selectedProject.Name}.slnx");
        var target = File.Exists(sln) ? sln : (File.Exists(slnx) ? slnx : null);

        if (target is null)
        {
            bool ok = false;
            await RunManagedBuildAsync(async () => ok = await _buildService.RegenerateProjectFilesAsync(_selectedProject));
            if (!ok) return;
            target = File.Exists(sln) ? sln : (File.Exists(slnx) ? slnx : null);
        }

        if (target is not null)
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
    }

    private async void PackageWin64_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null) return;
        var dlg = new OpenFolderDialog
        {
            Title = $"Select output folder for packaged '{_selectedProject.Name}' (Win64 Development)"
        };
        if (dlg.ShowDialog(this) != true) return;

        await RunManagedBuildAsync(() => _buildService.PackageWin64Async(_selectedProject, dlg.FolderName, "Development"));
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is not null && Directory.Exists(_selectedProject.ProjectDir))
        {
            Process.Start(new ProcessStartInfo { FileName = _selectedProject.ProjectDir, UseShellExecute = true });
        }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null) return;
        var logsDir = Path.Combine(_selectedProject.ProjectDir, "Saved", "Logs");
        var mainLog = Path.Combine(logsDir, $"{_selectedProject.Name}.log");

        if (File.Exists(mainLog))
        {
            Process.Start(new ProcessStartInfo { FileName = mainLog, UseShellExecute = true });
        }
        else if (Directory.Exists(logsDir))
        {
            var latest = Directory.EnumerateFiles(logsDir, "*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            Process.Start(new ProcessStartInfo { FileName = latest ?? logsDir, UseShellExecute = true });
        }
        else
        {
            System.Windows.MessageBox.Show("No Saved/Logs folder found for this project yet.", "Logs", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void CloseRunningEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject?.RunningEditorPid is int pid)
        {
            await CloseEditorProcessAsync(pid, _selectedProject.Name);
            await PollRunningEditorsAsync();
        }
    }

    private void EngineOverrideCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEngineCombo || _selectedProject is null) return;
        if (EngineOverrideCombo.SelectedItem is ComboBoxItem item)
        {
            var root = item.Tag?.ToString() ?? "";
            if (string.IsNullOrEmpty(root))
            {
                _settings.EngineOverrides.Remove(_selectedProject.UProjectPath);
                _selectedProject.EngineOverrideRoot = null;
            }
            else
            {
                _settings.EngineOverrides[_selectedProject.UProjectPath] = root;
                _selectedProject.EngineOverrideRoot = root;
            }
            _settings.Save();
            _selectedProject.EffectiveEngine = EngineDiscoveryService.MatchEngine(
                _selectedProject.EngineAssociation, _selectedProject.EngineOverrideRoot, _engines);
            ProjectDiscoveryService.RefreshBuildStatus(_selectedProject);
            UpdateInspectorUI();
        }
    }

    private void InspectorPath_Click(object sender, MouseButtonEventArgs e)
    {
        if (_selectedProject is not null)
        {
            System.Windows.Clipboard.SetText(_selectedProject.UProjectPath);
            ConsoleStatusText.Text = "Copied .uproject path to clipboard";
        }
    }

    // =========================================================================
    // Console Helpers
    // =========================================================================

    private void SetConsoleExpanded(bool expanded)
    {
        _isConsoleExpanded = expanded;
        ConsoleBox.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ToggleConsoleBtn.Content = expanded ? "\u25BC  Build Console" : "\u25B2  Build Console";
    }

    private void ToggleConsole_Click(object sender, RoutedEventArgs e) => SetConsoleExpanded(!_isConsoleExpanded);

    private void CancelBuild_Click(object sender, RoutedEventArgs e) => _buildService.CancelActiveTask();

    private void ClearLog_Click(object sender, RoutedEventArgs e) => ConsoleBox.Document.Blocks.Clear();

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var text = new TextRange(ConsoleBox.Document.ContentStart, ConsoleBox.Document.ContentEnd).Text;
        if (!string.IsNullOrEmpty(text))
        {
            System.Windows.Clipboard.SetText(text);
            ConsoleStatusText.Text = "Copied console log to clipboard";
        }
    }

    private void OnBuildLogReceived(BuildLogEntry entry)
    {
        Dispatcher.BeginInvoke(() => AppendConsoleLine(entry));
    }

    private void OnBuildProgressChanged(double? percent, string statusText)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ConsoleStatusText.Text = statusText;
            if (percent.HasValue)
            {
                BuildProgressBar.IsIndeterminate = false;
                BuildProgressBar.Value = percent.Value;
            }
            else
            {
                BuildProgressBar.IsIndeterminate = true;
            }
        });
    }

    private void AppendConsoleLine(BuildLogEntry entry)
    {
        var brush = entry.Severity switch
        {
            LogSeverity.Error => BrushError,
            LogSeverity.Warning => BrushWarning,
            LogSeverity.Progress => BrushProgress,
            LogSeverity.Success => BrushSuccess,
            _ => BrushInfo
        };

        var p = new Paragraph(new Run(entry.Text) { Foreground = brush })
        {
            Margin = new Thickness(0, 0, 0, 2),
            LineHeight = 17
        };
        ConsoleBox.Document.Blocks.Add(p);

        // Trim very old lines so memory stays tiny during huge clean rebuilds
        if (ConsoleBox.Document.Blocks.Count > 2500)
        {
            ConsoleBox.Document.Blocks.Remove(ConsoleBox.Document.Blocks.FirstBlock);
        }

        ConsoleBox.ScrollToEnd();
    }
}
