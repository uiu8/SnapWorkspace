using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using Microsoft.Win32;
using SnapWorkspace.App.Services;
using SnapWorkspace.App.Controls;
using SnapWorkspace.Route3;

namespace SnapWorkspace.App;

public partial class MainWindow : Window
{
    private readonly ShellSnapBackend _backend = new();
    private readonly WorkspaceRepository _workspaceRepository = new();
    private readonly SettingsRepository _settingsRepository = new();
    private readonly ObservableCollection<WorkspaceCardViewModel> _workspaceCards = [];
    private readonly ObservableCollection<ZoneAssignmentRow> _zoneRows = [];
    private readonly ObservableCollection<BackgroundApplicationRow> _backgroundRows = [];
    private readonly ObservableCollection<CompatibilityApplicationRow> _compatibilityRows = [];
    private readonly ObservableCollection<CaptureApplicationRow> _captureRows = [];
    private readonly List<CaptureApplicationRow> _captureInventory = [];
    private readonly ObservableCollection<CaptureExclusionRule> _blockedRules = [];
    private readonly ObservableCollection<CaptureRoleRule> _captureRoleRules = [];
    private readonly ObservableCollection<CaptureReviewIssue> _captureReviewIssues = [];
    private readonly List<SnapLayoutDefinition> _layouts = [.. BuiltInLayouts.All];
    private readonly IDisposable _shellThreadContext;
    private readonly GlobalHotkeyService _globalHotkey = new();
    private readonly DiagnosticSupportService _diagnostics = new();

    private AppSettings _settings = new();
    private IReadOnlyList<WindowSnapshot> _windowSnapshots = [];
    private WorkspaceDefinition? _editingWorkspace;
    private SnapCapability? _capability;
    private bool _suppressLayoutSelection;
    private bool _suppressThemeSelection;
    private bool _manualCaptureMode;
    private Task<IReadOnlyList<InstalledApplicationEntry>>? _installedApplicationsTask;
    private TrayIconService? _trayIcon;
    private ActiveWorkspaceSession? _activeSession;
    private bool _suppressUtilitySettings;
    private bool _allowExit;
    private bool _isRestoreInProgress;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MainWindow()
    {
        _shellThreadContext = ShellSnapBackend.EnterShellThreadContext();
        InitializeComponent();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
            _globalHotkey.Dispose();
            _trayIcon?.Dispose();
            _shellThreadContext.Dispose();
        };
        WorkspaceCards.ItemsSource = _workspaceCards;
        ZoneRows.ItemsSource = _zoneRows;
        BackgroundRows.ItemsSource = _backgroundRows;
        CompatibilityRows.ItemsSource = _compatibilityRows;
        CaptureWindowRows.ItemsSource = _captureRows;
        BlockedApplicationsRows.ItemsSource = _blockedRules;
        CaptureRoleRulesRows.ItemsSource = _captureRoleRules;
        CaptureReviewIssues.ItemsSource = _captureReviewIssues;
        LayoutPicker.ItemsSource = _layouts;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true }) return;
        e.Handled = HandleShortcut(e.Key, Keyboard.Modifiers);
    }

    private bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (key == Key.F5 && modifiers == ModifierKeys.None)
        {
            RefreshWindows_Click(this, new RoutedEventArgs());
            return true;
        }
        if (key == Key.Escape && LibraryView.Visibility != Visibility.Visible)
        {
            ShowLibrary();
            return true;
        }
        if ((modifiers & ModifierKeys.Control) == 0) return false;
        if (key == Key.N && modifiers == ModifierKeys.Control)
        {
            NewWorkspace_Click(this, new RoutedEventArgs());
            return true;
        }
        if (key == Key.C && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            CaptureWorkspace_Click(this, new RoutedEventArgs());
            return true;
        }
        if (key == Key.OemComma && modifiers == ModifierKeys.Control)
        {
            Settings_Click(this, new RoutedEventArgs());
            return true;
        }
        if (key == Key.S && modifiers == ModifierKeys.Control &&
                 EditorView.Visibility == Visibility.Visible)
        {
            SaveWorkspace_Click(this, new RoutedEventArgs());
            return true;
        }
        return false;
    }

    private void SystemParameters_StaticPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.HighContrast)) return;
        Dispatcher.BeginInvoke(() => ApplyTheme(_settings.Theme));
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = _settingsRepository.Load();
        _diagnostics.Enabled = _settings.DiagnosticLoggingEnabled;
        _diagnostics.Record("application_loaded", new
        {
            startupMilliseconds = App.StartupClock.ElapsedMilliseconds,
            startFromLogin = Environment.GetCommandLineArgs().Any(argument =>
                string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase))
        });
        RefreshBlockedRules();
        RefreshCaptureRoleRules();
        InitializeThemePicker();
        DataPathText.Text = _workspaceRepository.RootPath;
        RefreshWorkspaceCards();
        InitializeUtilitySettings();
        RefreshWindowChoices(updateEditor: false);
        ShowLibrary();

        StatusText.Text = $"已识别 {_windowSnapshots.Count} 个可用窗口，正在检测路线三能力…";
        _capability = await Task.Run(_backend.Probe);
        _diagnostics.Record("route3_probe_completed", new
        {
            _capability.Supported,
            compatibilityLevel = _capability.CompatibilityLevel.ToString(),
            _capability.IsKnownShellBinary,
            _capability.EffectiveMaximumWindowsPerSubmission,
            shellVersion = _capability.ShellBinaryVersion,
            shellSha256 = _capability.ShellBinarySha256
        });
        UpdateCapabilityCard(_capability);
        StatusText.Text = _capability.Supported
            ? $"路线三可用{CompatibilityStatusSuffix(_capability)} · " +
              $"启动 {App.StartupClock.ElapsedMilliseconds} ms · {_windowSnapshots.Count} 个窗口可选"
            : $"路线三不可用：{_capability.Reason}";

        await RenderPreviewIfRequestedAsync();

        if (Environment.GetCommandLineArgs().Any(argument =>
                string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase)) &&
            _settings.EnableTrayIcon && _settings.StartMinimizedAtLogin)
        {
            HideToTray(showNotice: false);
        }
        _ready.TrySetResult();
    }

    internal async Task RestoreFromExternalAsync(WorkspaceDefinition workspace)
    {
        await _ready.Task;
        await RestoreWorkspaceAsync(workspace);
    }

    internal async Task ExecuteExternalCommandAsync(CommandPaletteAction action)
    {
        await _ready.Task;
        switch (action)
        {
            case CommandPaletteAction.ShowMainWindow:
                ShowAndActivate();
                break;
            case CommandPaletteAction.NewWorkspace:
                ShowAndActivate();
                NewWorkspace_Click(this, new RoutedEventArgs());
                break;
            case CommandPaletteAction.SmartCapture:
                ShowAndActivate();
                BeginCapture(manual: false);
                break;
            case CommandPaletteAction.ManualCapture:
                ShowAndActivate();
                BeginCapture(manual: true);
                break;
            case CommandPaletteAction.RefreshWindows:
                ShowAndActivate();
                RefreshWindowChoices(updateEditor: true);
                break;
            case CommandPaletteAction.OpenSettings:
                ShowAndActivate();
                Settings_Click(this, new RoutedEventArgs());
                break;
        }
    }

    private void RefreshWorkspaceCards()
    {
        _workspaceCards.Clear();
        foreach (var workspace in _workspaceRepository.LoadAll())
        {
            _workspaceCards.Add(new WorkspaceCardViewModel { Workspace = workspace });
        }

        EmptyState.Visibility = _workspaceCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (QuickWorkspace1Picker.ItemsSource is not null) RefreshQuickWorkspacePicker();
        _trayIcon?.Refresh();
    }

    private void RefreshWindowChoices(bool updateEditor)
    {
        _windowSnapshots = WindowCatalog.EnumerateCandidates()
            .Where(window => window.ProcessId != Environment.ProcessId)
            .OrderBy(window => Path.GetFileName(window.ProcessPath))
            .ThenBy(window => window.Title)
            .ToList();

        if (updateEditor && EditorView.Visibility == Visibility.Visible && LayoutPicker.SelectedItem is SnapLayoutDefinition layout)
        {
            var preserved = _zoneRows
                .Select(row => new { row.Zone.Id, Application = BuildWindowApplication(row) })
                .Where(item => item.Application is not null)
                .ToDictionary(
                    item => item.Id,
                    item => item.Application!,
                    StringComparer.OrdinalIgnoreCase);
            BuildZoneRows(layout, preserved);
        }

        StatusText.Text = $"已刷新：{_windowSnapshots.Count} 个窗口可用于工作区。";
    }

    private void ShowLibrary()
    {
        LibraryView.Visibility = Visibility.Visible;
        EditorView.Visibility = Visibility.Collapsed;
        CaptureView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        PageTitle.Text = "工作区";
        PageSubtitle.Text = "保存、编辑并恢复你的窗口组合";
    }

    private void ShowEditor(WorkspaceDefinition? workspace)
    {
        LibraryView.Visibility = Visibility.Collapsed;
        EditorView.Visibility = Visibility.Visible;
        CaptureView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        _editingWorkspace = workspace;
        PageTitle.Text = workspace is null ? "新建工作区" : "编辑工作区";
        PageSubtitle.Text = "原生槽位可以留空；兼容窗口和后台应用不会进入 SnapWindows";
        WorkspaceNameBox.Text = workspace?.Name ?? "我的工作区";

        var layout = workspace?.Layout ?? BuiltInLayouts.Get("halves");
        var builtInIds = BuiltInLayouts.All.Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _layouts.RemoveAll(item => !builtInIds.Contains(item.Id));
        if (_layouts.All(item => !string.Equals(item.Id, layout.Id, StringComparison.OrdinalIgnoreCase)))
        {
            _layouts.Add(layout);
        }
        LayoutPicker.Items.Refresh();

        _suppressLayoutSelection = true;
        LayoutPicker.SelectedItem = _layouts.First(item =>
            string.Equals(item.Id, layout.Id, StringComparison.OrdinalIgnoreCase));
        _suppressLayoutSelection = false;

        var applications = workspace?.Applications
            .Where(application => application.IsNativeSnapWindow)
            .ToDictionary(
            application => application.ZoneId!,
            application => application,
            StringComparer.OrdinalIgnoreCase);
        BuildZoneRows(layout, applications);
        _compatibilityRows.Clear();
        foreach (var application in workspace?.Applications.Where(application =>
                     application.IsCompatibilityWindow) ?? [])
        {
            _compatibilityRows.Add(CompatibilityApplicationRow.FromApplication(application));
        }
        _backgroundRows.Clear();
        foreach (var application in workspace?.Applications.Where(application =>
                     application.Kind == WorkspaceApplicationKind.Background) ?? [])
        {
            _backgroundRows.Add(BackgroundApplicationRow.FromApplication(application));
        }

        StatusText.Text = "原生窗口进入 Snap；兼容窗口只定位一次；后台应用不占桌面布局。";
    }

    private void BuildZoneRows(
        SnapLayoutDefinition layout,
        IReadOnlyDictionary<string, WorkspaceApplication>? selectedApplications = null)
    {
        _zoneRows.Clear();
        var usedHandles = new HashSet<nint>();
        foreach (var zone in layout.Zones)
        {
            var choices = new ObservableCollection<WindowChoice> { WindowChoice.Empty };
            foreach (var snapshot in _windowSnapshots)
            {
                choices.Add(WindowChoice.FromSnapshot(snapshot));
            }

            var selected = WindowChoice.Empty;
            WorkspaceApplication? application = null;
            if (selectedApplications is not null &&
                selectedApplications.TryGetValue(zone.Id, out application) &&
                application?.WindowIdentity is not null)
            {
                var best = choices
                    .Where(choice => choice.Snapshot is not null && !usedHandles.Contains(choice.Snapshot.Hwnd))
                    .Select(choice => new
                    {
                        Choice = choice,
                        Evaluation = WorkspaceService.EvaluateMatch(application, choice.Snapshot!)
                    })
                    .Where(item => item.Evaluation.IsMatch)
                    .OrderByDescending(item => item.Evaluation.Score)
                    .FirstOrDefault();
                if (best is not null)
                {
                    selected = best.Choice;
                    usedHandles.Add(best.Choice.Snapshot!.Hwnd);
                }
                else
                {
                    selected = WindowChoice.Unavailable(application);
                    choices.Add(selected);
                }
            }

            _zoneRows.Add(new ZoneAssignmentRow
            {
                Zone = zone,
                Choices = choices,
                SelectedChoice = selected,
                Launch = application?.Launch.Clone() ?? selected.Launch.Clone(),
                Match = application?.Match.Clone() ?? selected.Match.Clone(),
                ApplicationId = application?.Id
            });
        }

        EditorPreview.Layout = layout;
        UpdateEditorPreview();
    }

    private WorkspaceDefinition BuildWorkspaceFromEditor()
    {
        var layout = LayoutPicker.SelectedItem as SnapLayoutDefinition
            ?? throw new InvalidOperationException("请先选择布局。");
        var selectedHandles = _zoneRows
            .Where(row => row.SelectedChoice.Snapshot is not null)
            .Select(row => row.SelectedChoice.Snapshot!.Hwnd)
            .ToList();
        if (selectedHandles.Count != selectedHandles.Distinct().Count())
        {
            throw new InvalidDataException("同一个窗口不能同时分配到多个槽位。");
        }

        var applications = _zoneRows
            .Where(row => !row.SelectedChoice.IsEmpty)
            .Select(BuildWindowApplication)
            .Where(application => application is not null)
            .Cast<WorkspaceApplication>()
            .Concat(_compatibilityRows.Select(row => row.ToApplication()))
            .Concat(_backgroundRows.Select(row => row.ToApplication()))
            .ToList();
        return WorkspaceService.Create(
            WorkspaceNameBox.Text,
            layout,
            ShellSnapBackend.GetPrimaryWorkArea(),
            applications,
            _editingWorkspace?.Id,
            _editingWorkspace?.CreatedAt);
    }

    private static WorkspaceApplication? BuildWindowApplication(ZoneAssignmentRow row)
    {
        var choice = row.SelectedChoice;
        if (choice.IsEmpty || choice.Identity is null)
        {
            return null;
        }

        return new WorkspaceApplication
        {
            Id = row.ApplicationId ?? choice.ApplicationId ?? Guid.NewGuid().ToString("N"),
            DisplayName = choice.DisplayName,
            Kind = WorkspaceApplicationKind.Window,
            ZoneId = row.Zone.Id,
            WindowIdentity = choice.Identity,
            Match = row.Match.Clone(),
            Launch = row.Launch.Clone()
        };
    }

    private async Task RestoreWorkspaceAsync(WorkspaceDefinition workspace)
    {
        if (_isRestoreInProgress)
        {
            StatusText.Text = "已有工作区正在恢复，请等待当前操作完成。";
            return;
        }

        _isRestoreInProgress = true;
        _diagnostics.Record("workspace_restore_started", new
        {
            workspaceId = workspace.Id,
            nativeWindowCount = workspace.NativeSnapWindowCount,
            compatibilityWindowCount = workspace.CompatibilityWindowCount,
            backgroundApplicationCount = workspace.BackgroundApplicationCount
        });
        try
        {
            await RestoreWorkspaceCoreAsync(workspace);
        }
        catch (Exception exception)
        {
            _diagnostics.Record("workspace_restore_failed", new
            {
                workspaceId = workspace.Id,
                exceptionType = exception.GetType().FullName,
                exception.HResult
            }, "error");
            if (!IsVisible) ShowAndActivate();
            StatusText.Text = "工作区恢复失败；已记录匿名诊断事件。";
            MessageBox.Show(
                this,
                exception.Message,
                "工作区恢复失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isRestoreInProgress = false;
        }
    }

    private async Task RestoreWorkspaceCoreAsync(WorkspaceDefinition workspace)
    {
        StatusText.Text = $"正在检查“{workspace.Name}”…";
        var preflight = WorkspacePreflight.Analyze(
            workspace,
            WindowCatalog.EnumerateCandidates(),
            _capability?.Supported,
            _capability?.EffectiveMaximumWindowsPerSubmission ?? 4);
        if (!preflight.CanRestore)
        {
            _diagnostics.Record("workspace_preflight_blocked", new
            {
                workspaceId = workspace.Id,
                preflight.ErrorCount,
                preflight.WarningCount
            }, "warning");
            if (!IsVisible) ShowAndActivate();
            StatusText.Text = $"未恢复“{workspace.Name}”：预检发现 {preflight.ErrorCount} 个错误。";
            MessageBox.Show(
                this,
                FormatPreflightReport(preflight),
                "恢复前检查",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        StatusText.Text = $"正在准备“{workspace.Name}”中的应用…";
        var compatibilityBoundsBeforePreparation = workspace.Applications
            .Where(application => application.IsCompatibilityWindow && application.CompatibilityBounds is not null)
            .ToDictionary(
                application => application.Id,
                application => (
                    application.CompatibilityBounds!.X,
                    application.CompatibilityBounds.Y,
                    application.CompatibilityBounds.Width,
                    application.CompatibilityBounds.Height),
                StringComparer.OrdinalIgnoreCase);
        var prepared = await WorkspaceLauncher.PrepareAsync(workspace);
        try
        {
        StatusText.ToolTip = FormatPreparationReport(prepared);
        _diagnostics.Record("workspace_preparation_completed", new
        {
            workspaceId = workspace.Id,
            outcomes = prepared.Outcomes.Select(outcome => new
            {
                outcome.ApplicationId,
                kind = outcome.Kind.ToString(),
                status = outcome.Status.ToString(),
                hasMatchedWindow = outcome.MatchScore is not null
            }).ToArray(),
            missingApplicationIds = prepared.MissingWindows.Select(application => application.Id).ToArray()
        });
        if (prepared.FailedBackgroundApplications > 0)
        {
            _diagnostics.Record("background_stage_failed", new
            {
                workspaceId = workspace.Id,
                failedBackgroundApplicationCount = prepared.FailedBackgroundApplications,
                desktopStageSkipped = true
            }, "error");
            if (!IsVisible) ShowAndActivate();
            StatusText.Text =
                $"未恢复“{workspace.Name}”：{prepared.FailedBackgroundApplications} 个后台应用未能保持隐藏，已跳过桌面布局。";
            MessageBox.Show(
                this,
                FormatPreparationReport(prepared),
                "后台启动未完成",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (prepared.MissingWindows.Count > 0)
        {
            _diagnostics.Record("workspace_windows_missing", new
            {
                workspaceId = workspace.Id,
                missingWindowCount = prepared.MissingWindows.Count
            }, "warning");
            StatusText.Text =
                $"{prepared.MissingWindows.Count} 个应用未就绪，正在用其余窗口继续恢复…";
        }

        if (prepared.Assignments.Count == 0 && prepared.CompatibilityAssignments.Count == 0)
        {
            if (prepared.MissingWindows.Count > 0)
            {
                if (!IsVisible) ShowAndActivate();
                _diagnostics.Record("workspace_restore_failed_no_windows", new
                {
                    workspaceId = workspace.Id,
                    missingWindowCount = prepared.MissingWindows.Count
                }, "error");
                StatusText.Text = $"未恢复“{workspace.Name}”：没有任何桌面窗口准备成功。";
                MessageBox.Show(
                    this,
                    FormatPreparationReport(prepared),
                    "应用准备失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            SetActiveSession(workspace, prepared);
            _diagnostics.Record("workspace_restore_completed", new
            {
                workspaceId = workspace.Id,
                nativeWindowCount = 0,
                compatibilityWindowCount = 0,
                prepared.FailedBackgroundApplications
            });
            StatusText.Text = prepared.FailedBackgroundApplications == 0
                ? $"已启动“{workspace.Name}”的后台应用；该工作区没有桌面窗口。"
                : $"后台应用启动完成，但有 {prepared.FailedBackgroundApplications} 个失败。";
            return;
        }

        var workArea = ShellSnapBackend.GetPrimaryWorkArea();
        SnapOperationResult? snapResult = null;
        if (prepared.Assignments.Count > 0)
        {
            StatusText.Text = $"正在通过 Shell 一次性吸附 {prepared.Assignments.Count} 个窗口…";
            snapResult = _backend.Snap(workspace.Layout, workArea, prepared.Assignments);
            await Task.Delay(Math.Clamp(_settings.VerificationDelayMilliseconds, 250, 1500));
            snapResult = _backend.Verify(snapResult);
        }

        var compatibilityResults = prepared.CompatibilityAssignments.Count == 0
            ? []
            : CompatibilityWindowBackend.PlaceOnce(workArea, prepared.CompatibilityAssignments);
        CompatibilityLayerResult? layerResult = null;
        if (snapResult?.Success != false &&
            prepared.Assignments.Count > 0 &&
            prepared.CompatibilityAssignments.Count > 0)
        {
            var hasColdStartedWindow = prepared.Outcomes.Any(outcome =>
                outcome.Kind == WorkspaceApplicationKind.Window &&
                outcome.Status == ApplicationStartStatus.Started);
            layerResult = hasColdStartedWindow
                ? await CompatibilityWindowBackend.StabilizeColdStartOverlapAsync(
                    prepared.CompatibilityAssignments,
                    prepared.Assignments)
                : CompatibilityWindowBackend.RestoreOverlapLayer(
                    prepared.CompatibilityAssignments,
                    prepared.Assignments);
        }
        var failedCompatibility = compatibilityResults.Count(result => !result.Success);
        var compatibilityFallbackCount = prepared.CompatibilityAssignments.Count(assignment =>
            !string.IsNullOrWhiteSpace(assignment.SourceZoneId));
        var runtimeEmptyZoneCount = Math.Max(
            0,
            workspace.Layout.Zones.Count - prepared.Assignments.Count - compatibilityFallbackCount);
        var nativeSuccess = snapResult?.Success != false;
        if (nativeSuccess && failedCompatibility == 0)
        {
            var repairedCompatibilityIds = workspace.Applications
                .Where(application => application.IsCompatibilityWindow && application.CompatibilityBounds is not null)
                .Where(application => compatibilityBoundsBeforePreparation.TryGetValue(application.Id, out var before) &&
                                      (Math.Abs(before.X - application.CompatibilityBounds!.X) > 0.000001 ||
                                       Math.Abs(before.Y - application.CompatibilityBounds.Y) > 0.000001 ||
                                       Math.Abs(before.Width - application.CompatibilityBounds.Width) > 0.000001 ||
                                       Math.Abs(before.Height - application.CompatibilityBounds.Height) > 0.000001))
                .Select(application => application.Id)
                .ToList();
            if (repairedCompatibilityIds.Count > 0)
            {
                workspace.UpdatedAt = DateTimeOffset.UtcNow;
                _workspaceRepository.Save(workspace);
                RefreshWorkspaceCards();
                _diagnostics.Record("compatibility_bounds_repaired", new
                {
                    workspaceId = workspace.Id,
                    repairedApplicationIds = repairedCompatibilityIds
                });
            }
            SetActiveSession(workspace, prepared);
            _diagnostics.Record("workspace_restore_completed", new
            {
                workspaceId = workspace.Id,
                nativeWindowCount = prepared.Assignments.Count,
                compatibilityWindowCount = prepared.CompatibilityAssignments.Count,
                compatibilityFallbackWindowCount = compatibilityFallbackCount,
                overlappingCompatibilityWindowCount = layerResult?.OverlappingWindowCount ?? 0,
                raisedCompatibilityWindowCount = layerResult?.RaisedWindowCount ?? 0,
                missingWindowCount = prepared.MissingWindows.Count,
                failedBackgroundApplicationCount = prepared.FailedBackgroundApplications,
                preflight.WarningCount
            });
            StatusText.Text =
                $"已恢复“{workspace.Name}”：{prepared.Assignments.Count} 个原生吸附、" +
                $"{prepared.CompatibilityAssignments.Count} 个兼容定位" +
                (compatibilityFallbackCount == 0 ? "、" : $"（其中 {compatibilityFallbackCount} 个不支持 Snap，已自动回退）、") +
                $"{runtimeEmptyZoneCount} 个空槽位" +
                (prepared.MissingWindows.Count == 0 ? "。" : $"；{prepared.MissingWindows.Count} 个应用未启动。") +
                (preflight.WarningCount == 0 ? string.Empty : $" 预检有 {preflight.WarningCount} 条提醒。");
        }
        else if (!nativeSuccess)
        {
            _diagnostics.Record("native_snap_failed", new
            {
                workspaceId = workspace.Id,
                snapResult!.HResult,
                snapResult.FallbackRecommended
            }, "error");
            StatusText.Text = snapResult!.FallbackRecommended
                ? $"原生吸附未完成，建议路线二回退：{snapResult.Message}"
                : snapResult.Message;
        }
        else
        {
            _diagnostics.Record("compatibility_placement_failed", new
            {
                workspaceId = workspace.Id,
                failedCompatibilityWindowCount = failedCompatibility
            }, "warning");
            StatusText.Text = $"原生吸附完成，但有 {failedCompatibility} 个兼容窗口拒绝了保存位置。";
        }

        if (compatibilityResults.Count > 0)
        {
            StatusText.ToolTip = FormatPreparationReport(prepared) + Environment.NewLine + Environment.NewLine +
                                 string.Join(Environment.NewLine, compatibilityResults.Select(result =>
                                     $"[{(result.Success ? "兼容定位完成" : "兼容定位失败")}] {result.DisplayName}：{result.Message}"));
        }

        if (nativeSuccess && failedCompatibility == 0 && prepared.MissingWindows.Count > 0)
        {
            if (!IsVisible) ShowAndActivate();
            MessageBox.Show(
                this,
                FormatPreparationReport(prepared),
                "工作区已部分恢复",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        if (nativeSuccess && prepared.FailedBackgroundApplications > 0)
        {
            MessageBox.Show(
                this,
                FormatPreparationReport(prepared),
                "部分后台应用启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        }
        finally
        {
            // 后台语义只约束本次恢复。事务结束后立即交还托盘点击等用户操作，
            // 不能再用固定 20 秒租约继续隐藏用户主动打开的窗口。
            WorkspaceLauncher.ReleaseBackgroundGuard(workspace);
        }
    }

    private void SetActiveSession(WorkspaceDefinition workspace, WorkspacePreparationResult prepared)
    {
        var previouslyStartedHandles = _activeSession is not null &&
                                       string.Equals(_activeSession.Workspace.Id, workspace.Id, StringComparison.OrdinalIgnoreCase)
            ? _activeSession.Windows.Where(window => window.StartedByRestore).Select(window => window.Hwnd).ToHashSet()
            : [];
        var startedIds = prepared.Outcomes
            .Where(outcome => outcome.Status == ApplicationStartStatus.Started)
            .Select(outcome => outcome.ApplicationId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var managedWindows = new List<ManagedSessionWindow>();
        foreach (var assignment in prepared.Assignments)
        {
            var application = workspace.Applications.FirstOrDefault(candidate =>
                candidate.IsNativeSnapWindow &&
                string.Equals(candidate.ZoneId, assignment.ZoneId, StringComparison.OrdinalIgnoreCase));
            if (application is null) continue;
            managedWindows.Add(new ManagedSessionWindow(
                assignment.Hwnd,
                application.Id,
                application.DisplayName,
                startedIds.Contains(application.Id) || previouslyStartedHandles.Contains(assignment.Hwnd)));
        }
        foreach (var assignment in prepared.CompatibilityAssignments)
        {
            managedWindows.Add(new ManagedSessionWindow(
                assignment.Hwnd,
                assignment.ApplicationId,
                assignment.DisplayName,
                startedIds.Contains(assignment.ApplicationId) || previouslyStartedHandles.Contains(assignment.Hwnd)));
        }

        _activeSession = new ActiveWorkspaceSession(
            workspace,
            DateTimeOffset.Now,
            managedWindows,
            workspace.Applications.Count(application =>
                application.Kind == WorkspaceApplicationKind.Background && startedIds.Contains(application.Id)));
        UpdateActiveWorkspaceUi();
    }

    private void UpdateActiveWorkspaceUi()
    {
        if (_activeSession is null)
        {
            ActiveWorkspaceBanner.Visibility = Visibility.Collapsed;
        }
        else
        {
            ActiveWorkspaceBanner.Visibility = Visibility.Visible;
            ActiveWorkspaceTitle.Text = $"正在运行：{_activeSession.Workspace.Name}";
            ActiveWorkspaceDetails.Text =
                $"管理 {_activeSession.Windows.Count} 个可见窗口 · " +
                $"本次新启动 {_activeSession.CloseableWindowCount} 个 · " +
                $"后台启动 {_activeSession.StartedBackgroundApplicationCount} 个";
        }
        _trayIcon?.Refresh();
    }

    private async void RestoreActiveWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is not null) await RestoreWorkspaceAsync(_activeSession.Workspace);
    }

    private void EndActiveWorkspace_Click(object sender, RoutedEventArgs e) =>
        EndActiveWorkspace(promptForStartedWindows: true);

    private void EndActiveWorkspace(bool promptForStartedWindows)
    {
        if (_activeSession is null) return;
        var session = _activeSession;
        var closeWindows = false;
        if (promptForStartedWindows && session.CloseableWindowCount > 0)
        {
            var choice = MessageBox.Show(
                this,
                $"结束“{session.Workspace.Name}”的管理。\n\n" +
                $"是否同时正常关闭本次恢复新启动的 {session.CloseableWindowCount} 个可见窗口？\n" +
                "不会关闭恢复前已经存在的窗口，也不会强制终止后台进程；应用仍可提示保存未完成内容。",
                "结束工作区",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;
            closeWindows = choice == MessageBoxResult.Yes;
        }

        var requested = closeWindows
            ? WorkspaceWindowLifecycle.RequestClose(session.Windows
                .Where(window => window.StartedByRestore)
                .Select(window => window.Hwnd))
            : 0;
        _activeSession = null;
        UpdateActiveWorkspaceUi();
        StatusText.Text = closeWindows
            ? $"已结束工作区管理，并向 {requested} 个本次新启动窗口发送正常关闭请求。"
            : "已结束工作区管理；所有应用和窗口保持原状。";
    }

    private void UpdateEditorPreview()
    {
        EditorPreview.AssignedZoneIds = _zoneRows
            .Where(row => !row.SelectedChoice.IsEmpty)
            .Select(row => row.Zone.Id)
            .ToArray();
        EditorPreview.InvalidateVisual();
    }

    private void UpdateCapabilityCard(SnapCapability capability)
    {
        CapabilityDot.Fill = capability.Supported
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("WarningBrush");
        CapabilityTitle.Text = capability.Supported
            ? capability.CompatibilityLevel == ShellCompatibilityLevel.KnownBaseline
                ? "路线三已启用"
                : "路线三已启用 · 运行时兼容"
            : "兼容模式待接管";
        CapabilityDetails.Text = capability.Supported
            ? $"Shell {capability.ShellBinaryVersion} · " +
              $"{(capability.IsKnownShellBinary ? "已验证基线" : "哈希仅用于诊断")} · " +
              $"上限 {capability.EffectiveMaximumWindowsPerSubmission} 窗口"
            : capability.Reason;
    }

    private static string CompatibilityStatusSuffix(SnapCapability capability) =>
        capability.CompatibilityLevel == ShellCompatibilityLevel.RuntimeCompatible
            ? "（运行时兼容）"
            : string.Empty;

    private void Library_Click(object sender, RoutedEventArgs e) => ShowLibrary();

    private void NewWorkspace_Click(object sender, RoutedEventArgs e)
    {
        RefreshWindowChoices(updateEditor: false);
        ShowEditor(null);
    }

    private void ImportWorkspaces_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入工作区或备份包",
            Filter = "Snap Workspace 备份 (*.snapworkspace)|*.snapworkspace|工作区 JSON (*.json)|*.json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var count = _workspaceRepository.Import(dialog.FileName);
            RefreshWorkspaceCards();
            StatusText.Text = $"已导入 {count} 个工作区；同 Id 的现有工作区已保留。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"导入失败：{exception.Message}";
        }
    }

    private void ExportWorkspaces_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "备份全部工作区",
            Filter = "Snap Workspace 备份 (*.snapworkspace)|*.snapworkspace",
            FileName = $"SnapWorkspace-{DateTime.Now:yyyyMMdd-HHmm}.snapworkspace",
            AddExtension = true,
            DefaultExt = ".snapworkspace"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _workspaceRepository.ExportAll(dialog.FileName);
            StatusText.Text = $"已备份 {_workspaceCards.Count} 个工作区：{dialog.FileName}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"备份失败：{exception.Message}";
        }
    }

    private void CaptureWorkspace_Click(object sender, RoutedEventArgs e)
        => BeginCapture(manual: false);

    private void ManualCaptureWorkspace_Click(object sender, RoutedEventArgs e)
        => BeginCapture(manual: true);

    private void BeginCapture(bool manual)
    {
        _manualCaptureMode = manual;
        RefreshWindowChoices(updateEditor: false);
        PopulateCaptureInventory();

        LibraryView.Visibility = Visibility.Collapsed;
        EditorView.Visibility = Visibility.Collapsed;
        CaptureView.Visibility = Visibility.Visible;
        SettingsView.Visibility = Visibility.Collapsed;
        PageTitle.Text = manual ? "手动捕捉工作区" : "智能捕捉工作区";
        PageSubtitle.Text = manual
            ? "桌面窗口默认忽略；启用范围内的无窗口进程默认后台启动"
            : "自动分类当前应用，再由你确认原生、兼容或后台角色";
        CaptureModeText.Text = manual ? "手动选择" : "智能分类";
        UpdateCaptureSummary();
    }

    private void PopulateCaptureInventory()
    {
        _captureInventory.Clear();
        var workArea = ShellSnapBackend.GetPrimaryWorkArea();
        var rankedWindows = _windowSnapshots
            .Select(window => new
            {
                Window = window,
                IsLayoutCapable = IsLayoutCapable(window, workArea),
                Area = IntersectionArea(window.VisibleBounds, workArea)
            })
            .OrderByDescending(item => item.IsLayoutCapable)
            .ThenByDescending(item => item.Area)
            .ToList();
        var autoSnapHandles = rankedWindows
            .Where(item => item.IsLayoutCapable)
            .Take(4)
            .Select(item => item.Window.Hwnd)
            .ToHashSet();

        foreach (var item in rankedWindows)
        {
            var launchable = WorkspaceService.LaunchFrom(item.Window).CanLaunch;
            var role = _manualCaptureMode
                ? CaptureApplicationRole.Ignore
                : autoSnapHandles.Contains(item.Window.Hwnd)
                    ? CaptureApplicationRole.NativeSnapWindow
                    : launchable
                        ? CaptureApplicationRole.CompatibilityWindow
                        : CaptureApplicationRole.Ignore;
            _captureInventory.Add(new CaptureApplicationRow
            {
                Window = item.Window,
                Surface = CaptureApplicationSurface.Taskbar,
                Publisher = GetPublisher(item.Window.ProcessPath),
                SuggestedRole = role,
                AutoReason = role == CaptureApplicationRole.NativeSnapWindow
                    ? "窗口尺寸和位置适合原生布局，已加入 Snap 集合。"
                    : _manualCaptureMode
                        ? "手动模式不会自动保存此应用。"
                    : role == CaptureApplicationRole.Ignore
                        ? "无法读取启动路径或 AUMID，默认忽略。"
                    : item.IsLayoutCapable
                        ? "原生集合已达到 4 个；改为可见的兼容定位窗口。"
                        : "窗口不适合原生 Snap；保留为可见的兼容定位窗口。",
                SelectedRole = CaptureRoleOption.All.First(option => option.Role == role)
            });
        }

        var trayApplications = TrayIconCatalog.EnumerateRunningApplications();
        foreach (var process in trayApplications)
        {
            _captureInventory.Add(new CaptureApplicationRow
            {
                BackgroundProcess = process,
                Surface = CaptureApplicationSurface.Tray,
                Publisher = GetPublisher(process.ExecutablePath),
                SuggestedRole = CaptureApplicationRole.Background,
                AutoReason = "当前运行进程存在 Windows 通知区域图标。",
                SelectedRole = CaptureRoleOption.All.First(option =>
                    option.Role == CaptureApplicationRole.Background)
            });
        }

        foreach (var process in RunningApplicationCatalog.EnumerateBackgroundCandidates(_windowSnapshots, trayApplications))
        {
            _captureInventory.Add(new CaptureApplicationRow
            {
                BackgroundProcess = process,
                Surface = CaptureApplicationSurface.Background,
                Publisher = GetPublisher(process.ExecutablePath),
                SuggestedRole = CaptureApplicationRole.Background,
                AutoReason = "当前运行，但没有任务栏窗口或通知区域图标。",
                SelectedRole = CaptureRoleOption.All.First(option =>
                    option.Role == CaptureApplicationRole.Background)
            });
        }

        foreach (var row in _captureInventory)
        {
            ApplySavedCaptureRole(row);
        }

        _captureInventory.RemoveAll(IsCaptureBlocked);

        ApplyCaptureScope();
    }

    private bool IsCaptureBlocked(CaptureApplicationRow row) =>
        _settings.CaptureExclusions.Any(rule => CaptureExclusionMatches(rule, row));

    private static bool CaptureExclusionMatches(CaptureExclusionRule rule, CaptureApplicationRow row)
    {
        if (!string.IsNullOrWhiteSpace(rule.ExecutablePath) &&
            string.Equals(rule.ExecutablePath, row.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrWhiteSpace(rule.AppUserModelId) &&
            string.Equals(rule.AppUserModelId, row.AppUserModelId, StringComparison.OrdinalIgnoreCase)) return true;
        if (WildcardMatches(rule.ExecutablePattern, row.ExecutablePath)) return true;
        if (WildcardMatches(rule.PackageFamilyPattern, row.PackageFamilyName)) return true;
        if (WildcardMatches(rule.PublisherPattern, row.Publisher)) return true;
        if (WildcardMatches(rule.DisplayNamePattern, row.DisplayName)) return true;
        var hasStructuredMatcher = !string.IsNullOrWhiteSpace(rule.ExecutablePath) ||
                                   !string.IsNullOrWhiteSpace(rule.AppUserModelId) ||
                                   !string.IsNullOrWhiteSpace(rule.ExecutablePattern) ||
                                   !string.IsNullOrWhiteSpace(rule.PackageFamilyPattern) ||
                                   !string.IsNullOrWhiteSpace(rule.PublisherPattern) ||
                                   !string.IsNullOrWhiteSpace(rule.DisplayNamePattern);
        return !hasStructuredMatcher &&
               string.Equals(rule.DisplayName, row.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool WildcardMatches(string? pattern, string? value)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(value)) return false;
        var expression = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string? GetPublisher(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        try { return FileVersionInfo.GetVersionInfo(executablePath).CompanyName; }
        catch { return null; }
    }

    private void ApplySavedCaptureRole(CaptureApplicationRow row)
    {
        var rule = _settings.CaptureRoleRules
            .Where(rule => CaptureIdentityMatches(rule.ExecutablePath, rule.AppUserModelId, rule.DisplayName, row))
            .OrderByDescending(rule => rule.UpdatedAt)
            .FirstOrDefault();
        if (rule is null || row.RoleOptions.All(option => option.Role != rule.Role))
        {
            return;
        }

        row.SelectedRole = row.RoleOptions.First(option => option.Role == rule.Role);
    }

    private static bool CaptureIdentityMatches(
        string? executablePath,
        string? appUserModelId,
        string displayName,
        CaptureApplicationRow row)
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            return string.Equals(executablePath, row.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(appUserModelId))
        {
            return string.Equals(appUserModelId, row.AppUserModelId, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(displayName, row.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyCaptureScope()
    {
        _captureRows.Clear();
        var candidates = _captureInventory.Where(row => row.Surface switch
                     {
                      CaptureApplicationSurface.Taskbar => CaptureTaskbarToggle.IsChecked == true,
                      CaptureApplicationSurface.Tray => CaptureTrayToggle.IsChecked == true,
                      CaptureApplicationSurface.Background => CaptureBackgroundToggle.IsChecked == true,
                      CaptureApplicationSurface.Manual => true,
                      _ => false
                 }).ToList();
        var taskbarTargets = candidates
            .Where(row => row.Surface == CaptureApplicationSurface.Taskbar)
            .Select(row => row.LaunchTargetKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trayTargets = candidates
            .Where(row => row.Surface == CaptureApplicationSurface.Tray)
            .Select(row => row.LaunchTargetKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in candidates.Where(row =>
                     !(row.Surface == CaptureApplicationSurface.Tray && taskbarTargets.Contains(row.LaunchTargetKey)) &&
                     !(row.Surface == CaptureApplicationSurface.Background &&
                       (taskbarTargets.Contains(row.LaunchTargetKey) || trayTargets.Contains(row.LaunchTargetKey)))))
        {
            _captureRows.Add(row);
        }

        UpdateCaptureSummary();
    }

    private static bool IsLayoutCapable(WindowSnapshot window, PhysicalRect workArea) =>
        WindowCatalog.IsLikelySnapEligible(window) &&
        window.VisibleBounds.Width >= 280 &&
        window.VisibleBounds.Height >= 180 &&
        IntersectionArea(window.VisibleBounds, workArea) >= 280L * 180L;

    private static long IntersectionArea(PhysicalRect left, PhysicalRect right)
    {
        var width = Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.X, right.X));
        var height = Math.Max(0, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Y, right.Y));
        return (long)width * height;
    }

    private void AutoClassifyCapture_Click(object sender, RoutedEventArgs e)
    {
        _manualCaptureMode = false;
        PageTitle.Text = "智能捕捉工作区";
        PageSubtitle.Text = "自动分类当前应用，再由你确认原生、兼容或后台角色";
        CaptureModeText.Text = "智能分类";
        PopulateCaptureInventory();
        CaptureWindowRows.Items.Refresh();
    }

    private async void ManualPickCaptureWindow_Click(object sender, RoutedEventArgs e)
    {
        var ownerHwnd = new WindowInteropHelper(this).EnsureHandle();
        WindowPickerHintWindow? hint = null;
        try
        {
            Hide();
            hint = new WindowPickerHintWindow();
            hint.Show();
            await Dispatcher.Yield(DispatcherPriority.Render);
            var hintHwnd = new WindowInteropHelper(hint).EnsureHandle();
            using var picker = new WindowPickerService([ownerHwnd, hintHwnd]);
            var selectedHwnd = await picker.PickAsync();
            if (selectedHwnd is null)
            {
                StatusText.Text = "已取消手动点选窗口。";
                return;
            }

            var snapshot = WindowCatalog.CaptureWindow(selectedHwnd.Value);
            if (snapshot is null || snapshot.ProcessId == Environment.ProcessId)
            {
                StatusText.Text = "该区域不是可捕捉的应用顶层窗口，请重新点选。";
                return;
            }

            _captureInventory.RemoveAll(row => row.Window?.Hwnd == snapshot.Hwnd);
            _windowSnapshots = _windowSnapshots
                .Where(window => window.Hwnd != snapshot.Hwnd)
                .Append(snapshot)
                .ToList();
            var workArea = ShellSnapBackend.GetPrimaryWorkArea();
            var nativeCount = _captureInventory.Count(row =>
                row.SelectedRole.Role == CaptureApplicationRole.NativeSnapWindow);
            var suggestedRole = IsLayoutCapable(snapshot, workArea) && nativeCount < 4
                ? CaptureApplicationRole.NativeSnapWindow
                : CaptureApplicationRole.CompatibilityWindow;
            var row = new CaptureApplicationRow
            {
                Window = snapshot,
                Surface = CaptureApplicationSurface.Manual,
                Publisher = GetPublisher(snapshot.ProcessPath),
                SuggestedRole = suggestedRole,
                AutoReason = suggestedRole == CaptureApplicationRole.NativeSnapWindow
                    ? "你已手动确认此窗口；窗口样式支持原生 Snap。"
                    : "你已手动确认此窗口；将按兼容定位保存其正常还原区域。",
                SelectedRole = CaptureRoleOption.All.First(option => option.Role == suggestedRole)
            };
            ApplySavedCaptureRole(row);
            _captureInventory.Add(row);
            ApplyCaptureScope();
            CaptureWindowRows.Items.Refresh();
            StatusText.Text = $"已手动捕捉“{row.DisplayName}”并加入列表；请确认它的启动角色。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"手动捕捉失败：{exception.Message}";
        }
        finally
        {
            hint?.Close();
            ShowAndActivate();
        }
    }

    private void CaptureScope_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || CaptureView.Visibility != Visibility.Visible)
        {
            return;
        }

        ApplyCaptureScope();
    }

    private void IgnoreAllCapture_Click(object sender, RoutedEventArgs e)
    {
        var ignore = CaptureRoleOption.All.First(option => option.Role == CaptureApplicationRole.Ignore);
        foreach (var row in _captureInventory) row.SelectedRole = ignore;
        UpdateCaptureSummary();
        CaptureWindowRows.Items.Refresh();
    }

    private void BlockCaptureApplication_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CaptureApplicationRow row)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"以后捕捉时都隐藏“{row.DisplayName}”？\n\n可以在设置的“捕捉屏蔽列表”中恢复。",
            "屏蔽捕捉",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var rule = new CaptureExclusionRule
        {
            DisplayName = row.DisplayName,
            ExecutablePath = row.ExecutablePath,
            AppUserModelId = row.AppUserModelId
        };
        _settings.CaptureExclusions.Add(rule);
        _settingsRepository.Save(_settings);
        RefreshBlockedRules();
        _captureInventory.RemoveAll(IsCaptureBlocked);
        ApplyCaptureScope();
        StatusText.Text = $"已屏蔽“{row.DisplayName}”；以后捕捉时不会再显示。";
    }

    private void UnblockCaptureApplication_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CaptureExclusionRule rule)
        {
            return;
        }

        _settings.CaptureExclusions.RemoveAll(item => item.Id == rule.Id);
        _settingsRepository.Save(_settings);
        RefreshBlockedRules();
        StatusText.Text = $"已取消屏蔽“{rule.DisplayName}”；下次捕捉时重新识别。";
    }

    private void AddRecommendedCaptureExclusions_Click(object sender, RoutedEventArgs e)
    {
        var recommended = new[]
        {
            new CaptureExclusionRule
            {
                DisplayName = "Windows 安全通知图标",
                ExecutablePattern = @"*\SecurityHealthSystray.exe",
                IsRecommended = true
            },
            new CaptureExclusionRule
            {
                DisplayName = "Windows 安全界面宿主",
                ExecutablePattern = @"*\SecHealthUI.exe",
                IsRecommended = true
            },
            new CaptureExclusionRule
            {
                DisplayName = "Windows Shell 体验宿主",
                ExecutablePattern = @"*\ShellExperienceHost.exe",
                IsRecommended = true
            }
        };
        var added = 0;
        foreach (var rule in recommended)
        {
            if (_settings.CaptureExclusions.Any(existing =>
                    string.Equals(existing.ExecutablePattern, rule.ExecutablePattern, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            _settings.CaptureExclusions.Add(rule);
            added++;
        }
        _settingsRepository.Save(_settings);
        RefreshBlockedRules();
        StatusText.Text = added == 0
            ? "推荐系统屏蔽已经全部启用。"
            : $"已添加 {added} 条推荐系统屏蔽；可在列表中逐条取消。";
    }

    private void RefreshBlockedRules()
    {
        _blockedRules.Clear();
        foreach (var rule in _settings.CaptureExclusions.OrderBy(rule => rule.DisplayName))
        {
            _blockedRules.Add(rule);
        }
    }

    private void RefreshCaptureRoleRules()
    {
        _captureRoleRules.Clear();
        foreach (var rule in _settings.CaptureRoleRules.OrderBy(rule => rule.DisplayName))
        {
            _captureRoleRules.Add(rule);
        }
    }

    private void RememberCaptureRole_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CaptureApplicationRow row)
        {
            return;
        }

        _settings.CaptureRoleRules.RemoveAll(rule =>
            CaptureIdentityMatches(rule.ExecutablePath, rule.AppUserModelId, rule.DisplayName, row));
        _settings.CaptureRoleRules.Add(new CaptureRoleRule
        {
            DisplayName = row.DisplayName,
            ExecutablePath = row.ExecutablePath,
            AppUserModelId = row.AppUserModelId,
            Role = row.SelectedRole.Role,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        _settingsRepository.Save(_settings);
        RefreshCaptureRoleRules();
        StatusText.Text = $"已记住“{row.DisplayName}”的捕捉角色：{row.SelectedRole.DisplayName}。";
    }

    private void RemoveCaptureRoleRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CaptureRoleRule rule)
        {
            return;
        }

        _settings.CaptureRoleRules.RemoveAll(item => item.Id == rule.Id);
        _settingsRepository.Save(_settings);
        RefreshCaptureRoleRules();
        StatusText.Text = $"已删除“{rule.DisplayName}”的捕捉角色规则。";
    }

    private void CaptureRole_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(UpdateCaptureSummary, DispatcherPriority.Background);

    private void UpdateCaptureSummary()
    {
        var snapCount = _captureRows.Count(row => row.SelectedRole.Role == CaptureApplicationRole.NativeSnapWindow);
        var compatibilityCount = _captureRows.Count(row => row.SelectedRole.Role == CaptureApplicationRole.CompatibilityWindow);
        var backgroundRoleCount = _captureRows.Count(row => row.SelectedRole.Role == CaptureApplicationRole.Background);
        var ignoredCount = _captureRows.Count - snapCount - compatibilityCount - backgroundRoleCount;
        var taskbarCount = _captureRows.Count(row => row.Surface == CaptureApplicationSurface.Taskbar);
        var trayCount = _captureRows.Count(row => row.Surface == CaptureApplicationSurface.Tray);
        var backgroundCount = _captureRows.Count(row => row.Surface == CaptureApplicationSurface.Background);
        var manualCount = _captureRows.Count(row => row.Surface == CaptureApplicationSurface.Manual);
        CaptureSummaryText.Text =
            $"任务栏 {taskbarCount} · 托盘 {trayCount} · 纯后台 {backgroundCount} · 手动 {manualCount}　|　" +
            $"原生 {snapCount}/4 · 兼容 {compatibilityCount} · 后台 {backgroundRoleCount} · 忽略 {ignoredCount}";
        StatusText.Text = snapCount > 4
            ? $"原生吸附窗口有 {snapCount} 个，请将其中 {snapCount - 4} 个改为“兼容定位”或“忽略”。"
            : $"已识别 {_captureRows.Count} 个应用；四窗口限制只作用于原生吸附集合。";
        UpdateCaptureReview();
    }

    private void UpdateCaptureReview()
    {
        _captureReviewIssues.Clear();
        var selectedRows = _captureRows
            .Where(row => row.SelectedRole.Role != CaptureApplicationRole.Ignore)
            .ToList();
        var nativeRows = selectedRows
            .Where(row => row.SelectedRole.Role == CaptureApplicationRole.NativeSnapWindow && row.Window is not null)
            .ToList();
        LayoutRecognitionResult? recognition = null;
        if (nativeRows.Count > 0)
        {
            recognition = LayoutRecognizer.Recognize(
                ShellSnapBackend.GetPrimaryWorkArea(),
                nativeRows.Select(row => row.Window!).ToList(),
                BuiltInLayouts.All);
        }

        CaptureReviewPreview.Layout = recognition?.Layout ?? BuiltInLayouts.Get("halves");
        CaptureReviewPreview.AssignedZoneIds = recognition?.Assignments.Select(assignment => assignment.ZoneId).ToArray() ?? [];
        CaptureReviewLayoutText.Text = recognition is null
            ? "无原生 Snap 布局"
            : recognition.Layout.DisplayName;
        CaptureReviewConfidenceText.Text = recognition is null
            ? "只会启动兼容或后台应用"
            : $"识别置信度 {recognition.Confidence:P0} · {recognition.Assignments.Count} 个槽位";
        var adjustedCount = selectedRows.Count(row => row.SelectedRole.Role != row.SuggestedRole);
        CaptureReviewSourceText.Text = $"自动建议 {selectedRows.Count - adjustedCount} · 已调整 {adjustedCount}";

        if (selectedRows.Count == 0)
        {
            _captureReviewIssues.Add(new CaptureReviewIssue("\uEA39", "尚未选择任何要保存的应用。", true));
        }

        if (nativeRows.Count > 4)
        {
            _captureReviewIssues.Add(new CaptureReviewIssue("\uEA39", $"原生吸附有 {nativeRows.Count} 个，超过路线三上限 4 个。", true));
        }

        var unavailable = selectedRows.Where(row =>
            row.SelectedRole.Role != CaptureApplicationRole.Ignore &&
            !(row.Window is not null ? WorkspaceService.LaunchFrom(row.Window).CanLaunch : row.BackgroundProcess?.ToLaunchSpec().CanLaunch == true))
            .ToList();
        if (unavailable.Count > 0)
        {
            _captureReviewIssues.Add(new CaptureReviewIssue(
                "\uE7BA",
                $"{unavailable.Count} 个应用缺少可执行路径或 AUMID，恢复时无法启动。",
                true));
        }

        var launchGroups = selectedRows
            .Where(row => !string.IsNullOrWhiteSpace(row.ExecutablePath) || !string.IsNullOrWhiteSpace(row.AppUserModelId))
            .GroupBy(row => row.LaunchTargetKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToList();
        foreach (var group in launchGroups.Take(2))
        {
            var hasBackgroundMix = group.Any(row => row.SelectedRole.Role == CaptureApplicationRole.Background) &&
                                   group.Any(row => row.SelectedRole.Role != CaptureApplicationRole.Background);
            _captureReviewIssues.Add(new CaptureReviewIssue(
                hasBackgroundMix ? "\uEA39" : "\uE946",
                hasBackgroundMix
                    ? $"“{group.First().DisplayName}”同时被保存为桌面窗口和后台应用，请只保留一种。"
                    : $"同一程序包含 {group.Count()} 个窗口；生成后请检查每个窗口的标题匹配规则。",
                hasBackgroundMix));
        }

        if (recognition is { Confidence: < 0.7 })
        {
            _captureReviewIssues.Add(new CaptureReviewIssue(
                "\uE946",
                "当前窗口与内置布局接近度较低；将采用最接近的原生布局，请在编辑器确认槽位。"));
        }

        if (_captureReviewIssues.Count == 0)
        {
            _captureReviewIssues.Add(new CaptureReviewIssue("\uE73E", "未发现重复、不可启动或高风险匹配项。"));
        }

        GenerateCaptureButton.IsEnabled = _captureReviewIssues.All(issue => !issue.IsBlocking);
    }

    private void CaptureSelected_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = _captureRows
                .Where(row => row.SelectedRole.Role == CaptureApplicationRole.NativeSnapWindow)
                .Select(row => row.Window ?? throw new InvalidOperationException(
                    $"“{row.DisplayName}”当前没有窗口，不能设为吸附窗口。"))
                .ToList();
            if (selected.Count > 4)
            {
                throw new InvalidOperationException("原生吸附集合最多 4 个窗口；兼容定位和后台应用数量不限。\n");
            }

            var workArea = ShellSnapBackend.GetPrimaryWorkArea();
            var compatibilityApplications = _captureRows
                .Where(row => row.SelectedRole.Role == CaptureApplicationRole.CompatibilityWindow)
                .Select(row => row.ToCompatibilityApplication(workArea))
                .ToList();
            var backgroundApplications = _captureRows
                .Where(row => row.SelectedRole.Role == CaptureApplicationRole.Background)
                .Select(row => row.ToBackgroundApplication())
                .GroupBy(
                    application => application.Launch.ExecutablePath ??
                                   application.Launch.AppUserModelId ??
                                   application.DisplayName,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (selected.Count == 0 && compatibilityApplications.Count == 0 && backgroundApplications.Count == 0)
            {
                throw new InvalidOperationException("当前捕捉范围没有要保存的应用。请勾选至少一种范围或调整应用角色。");
            }

            LayoutRecognitionResult? recognition = null;
            var layout = BuiltInLayouts.Get("halves");
            var applications = new List<WorkspaceApplication>();
            if (selected.Count > 0)
            {
                recognition = LayoutRecognizer.Recognize(workArea, selected, BuiltInLayouts.All);
                layout = recognition.Layout;
                if (_layouts.All(item => !string.Equals(item.Id, layout.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    _layouts.Add(layout);
                    LayoutPicker.Items.Refresh();
                }

                applications.AddRange(recognition.Assignments.Select(assignment =>
                    WorkspaceService.CreateWindowApplication(assignment.Window, assignment.ZoneId)));
            }

            applications.AddRange(compatibilityApplications);
            applications.AddRange(backgroundApplications);
            var captured = WorkspaceService.Create(
                $"捕捉工作区 {DateTime.Now:MM-dd HH-mm}",
                layout,
                workArea,
                applications);
            ShowEditor(captured);
            _editingWorkspace = null;
            StatusText.Text = recognition is null
                ? $"已捕捉 {compatibilityApplications.Count} 个兼容窗口和 {backgroundApplications.Count} 个后台应用；该工作区暂不提交原生 Snap。"
                : $"{recognition.Message} 请确认应用角色和布局后保存。";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message.Trim();
        }
    }

    private void InitializeUtilitySettings()
    {
        _suppressUtilitySettings = true;
        EnableTrayToggle.IsChecked = _settings.EnableTrayIcon;
        CloseToTrayToggle.IsChecked = _settings.CloseToTray;
        GlobalHotkeyToggle.IsChecked = _settings.GlobalHotkeyEnabled;
        RefreshShortcutEditors();
        RunAtStartupToggle.IsChecked = StartupRegistrationService.IsEnabled();
        _settings.RunAtStartup = RunAtStartupToggle.IsChecked == true;
        StartMinimizedToggle.IsChecked = _settings.StartMinimizedAtLogin;
        DiagnosticLoggingToggle.IsChecked = _settings.DiagnosticLoggingEnabled;
        IncludeApplicationDetailsToggle.IsChecked = _settings.SupportBundleIncludeApplicationDetails;
        IncludeWorkspaceDefinitionsToggle.IsChecked = _settings.SupportBundleIncludeWorkspaceDefinitions;
        UpdateSupportBundlePrivacyText();
        CloseToTrayToggle.IsEnabled = _settings.EnableTrayIcon;
        StartMinimizedToggle.IsEnabled = _settings.EnableTrayIcon && _settings.RunAtStartup;
        RefreshQuickWorkspacePicker();
        _suppressUtilitySettings = false;

        if (Environment.GetCommandLineArgs().Any(argument =>
                string.Equals(argument, "--render-preview", StringComparison.OrdinalIgnoreCase)))
        {
            _allowExit = true;
            Application.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;
            return;
        }
        ApplyTraySetting();
        ApplyGlobalHotkey();
    }

    private void RefreshQuickWorkspacePicker()
    {
        var options = new List<WorkspaceSelectionOption> { new(null, "未分配") };
        options.AddRange(_workspaceCards
            .Select(card => new WorkspaceSelectionOption(card.Workspace.Id, card.Name))
            .ToList());
        _suppressUtilitySettings = true;
        foreach (var (picker, bindingId) in QuickWorkspacePickers())
        {
            var binding = Shortcut(bindingId);
            picker.ItemsSource = options;
            picker.SelectedItem = options.FirstOrDefault(option =>
                string.Equals(option.Id, binding.WorkspaceId, StringComparison.OrdinalIgnoreCase)) ?? options[0];
        }
        _suppressUtilitySettings = false;
    }

    private void UtilitySettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUtilitySettings) return;
        _settings.EnableTrayIcon = EnableTrayToggle.IsChecked == true;
        _settings.CloseToTray = CloseToTrayToggle.IsChecked == true;
        _settings.GlobalHotkeyEnabled = GlobalHotkeyToggle.IsChecked == true;
        _settings.StartMinimizedAtLogin = StartMinimizedToggle.IsChecked == true;
        CloseToTrayToggle.IsEnabled = _settings.EnableTrayIcon;
        StartMinimizedToggle.IsEnabled = _settings.EnableTrayIcon && _settings.RunAtStartup;
        _settingsRepository.Save(_settings);
        ApplyTraySetting();
        ApplyGlobalHotkey();
    }

    private void RefreshShortcutEditors()
    {
        foreach (var (toggle, box, bindingId) in ShortcutEditors())
        {
            var binding = Shortcut(bindingId);
            toggle.IsChecked = binding.Enabled;
            box.Text = binding.Gesture;
            box.IsEnabled = binding.Enabled;
        }
    }

    private GlobalShortcutBinding Shortcut(string id) =>
        _settings.GlobalShortcuts.First(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<(CheckBox Toggle, TextBox Box, string BindingId)> ShortcutEditors()
    {
        yield return (CommandPaletteHotkeyToggle, CommandPaletteHotkeyBox, GlobalShortcutBinding.CommandPaletteId);
        yield return (ShowWindowHotkeyToggle, ShowWindowHotkeyBox, GlobalShortcutBinding.ShowMainWindowId);
        yield return (SmartCaptureHotkeyToggle, SmartCaptureHotkeyBox, GlobalShortcutBinding.SmartCaptureId);
        yield return (NewWorkspaceHotkeyToggle, NewWorkspaceHotkeyBox, GlobalShortcutBinding.NewWorkspaceId);
        yield return (QuickWorkspace1Toggle, QuickWorkspace1HotkeyBox, GlobalShortcutBinding.QuickWorkspace1Id);
        yield return (QuickWorkspace2Toggle, QuickWorkspace2HotkeyBox, GlobalShortcutBinding.QuickWorkspace2Id);
        yield return (QuickWorkspace3Toggle, QuickWorkspace3HotkeyBox, GlobalShortcutBinding.QuickWorkspace3Id);
    }

    private IEnumerable<(ComboBox Picker, string BindingId)> QuickWorkspacePickers()
    {
        yield return (QuickWorkspace1Picker, GlobalShortcutBinding.QuickWorkspace1Id);
        yield return (QuickWorkspace2Picker, GlobalShortcutBinding.QuickWorkspace2Id);
        yield return (QuickWorkspace3Picker, GlobalShortcutBinding.QuickWorkspace3Id);
    }

    private void DiagnosticSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUtilitySettings) return;
        _settings.DiagnosticLoggingEnabled = DiagnosticLoggingToggle.IsChecked == true;
        _settings.SupportBundleIncludeApplicationDetails = IncludeApplicationDetailsToggle.IsChecked == true;
        _settings.SupportBundleIncludeWorkspaceDefinitions = IncludeWorkspaceDefinitionsToggle.IsChecked == true;
        _settingsRepository.Save(_settings);
        _diagnostics.Enabled = _settings.DiagnosticLoggingEnabled;
        _diagnostics.Record("diagnostic_settings_changed", new
        {
            _settings.DiagnosticLoggingEnabled,
            _settings.SupportBundleIncludeApplicationDetails,
            _settings.SupportBundleIncludeWorkspaceDefinitions
        });
        UpdateSupportBundlePrivacyText();
    }

    private void UpdateSupportBundlePrivacyText()
    {
        var sensitive = new List<string>();
        if (IncludeApplicationDetailsToggle.IsChecked == true)
            sensitive.Add("应用路径、启动参数与窗口标题");
        if (IncludeWorkspaceDefinitionsToggle.IsChecked == true)
            sensitive.Add("完整工作区 JSON");
        SupportBundlePrivacyText.Text = sensitive.Count == 0
            ? "默认支持包只含系统版本、功能状态、数量统计和最近 7 天的匿名事件日志。"
            : $"当前支持包还会包含：{string.Join("；", sensitive)}。请在发送前确认内容。";
    }

    private void ExportSupportBundle_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出 Snap Workspace 支持包",
            Filter = "Snap Workspace 支持包 (*.zip)|*.zip",
            FileName = $"SnapWorkspace-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            AddExtension = true,
            DefaultExt = ".zip"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var windows = _settings.SupportBundleIncludeApplicationDetails
                ? WindowCatalog.EnumerateCandidates()
                : [];
            var path = _diagnostics.Export(
                dialog.FileName,
                _settings,
                _capability,
                _workspaceRepository.LoadAll(),
                windows);
            StatusText.Text = $"支持包已导出：{path}";
            MessageBox.Show(
                this,
                "支持包已生成。默认内容不含应用路径和窗口标题；如果你勾选了敏感内容，请在发送前再次确认压缩包。",
                "导出完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"支持包导出失败：{exception.Message}";
            MessageBox.Show(this, exception.Message, "导出支持包失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearDiagnosticLogs_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                this,
                "清除本机保存的诊断事件？工作区和应用设置不会被删除。",
                "清除诊断事件",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _diagnostics.Clear();
        StatusText.Text = "已清除本机诊断事件。";
    }

    private void QuickWorkspacePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUtilitySettings ||
            sender is not ComboBox { Tag: string bindingId, SelectedItem: WorkspaceSelectionOption selected }) return;
        var binding = Shortcut(bindingId);
        binding.WorkspaceId = selected.Id;
        if (binding.Id == GlobalShortcutBinding.QuickWorkspace1Id)
        {
            _settings.QuickWorkspaceId = selected.Id;
        }
        _settingsRepository.Save(_settings);
        ApplyGlobalHotkey();
    }

    private void ShortcutBindingToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUtilitySettings || sender is not CheckBox { Tag: string bindingId } toggle) return;
        var binding = Shortcut(bindingId);
        binding.Enabled = toggle.IsChecked == true;
        var editor = ShortcutEditors().First(item => item.BindingId == bindingId).Box;
        editor.IsEnabled = binding.Enabled;
        if (binding.Enabled && !GlobalHotkeyService.TryParse(binding.Gesture, out _, out _))
        {
            HotkeyStatusText.Text = $"{binding.DisplayName} 尚未设置有效组合键。";
        }
        _settingsRepository.Save(_settings);
        ApplyGlobalHotkey();
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_suppressUtilitySettings || sender is not TextBox { Tag: string bindingId } box) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }
        if (key is Key.Delete or Key.Back && Keyboard.Modifiers == ModifierKeys.None)
        {
            var cleared = Shortcut(bindingId);
            cleared.Gesture = string.Empty;
            cleared.Enabled = false;
            box.Text = string.Empty;
            ShortcutEditors().First(item => item.BindingId == bindingId).Toggle.IsChecked = false;
            SaveAndApplyShortcuts($"已清空“{cleared.DisplayName}”快捷键。");
            e.Handled = true;
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            HotkeyStatusText.Text = "全局快捷键至少需要一个修饰键。";
            e.Handled = true;
            return;
        }
        var gesture = GlobalHotkeyService.FormatGesture(modifiers, key);
        if (!GlobalHotkeyService.TryParse(gesture, out _, out _))
        {
            HotkeyStatusText.Text = $"无法使用组合键 {gesture}。";
            e.Handled = true;
            return;
        }
        var binding = Shortcut(bindingId);
        binding.Gesture = GlobalHotkeyService.Normalize(gesture);
        binding.Enabled = true;
        box.Text = binding.Gesture;
        ShortcutEditors().First(item => item.BindingId == bindingId).Toggle.IsChecked = true;
        SaveAndApplyShortcuts($"已设置“{binding.DisplayName}”：{binding.Gesture}。");
        e.Handled = true;
    }

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_suppressUtilitySettings || sender is not TextBox { Tag: string bindingId } box) return;
        var binding = Shortcut(bindingId);
        var entered = box.Text.Trim();
        if (string.IsNullOrWhiteSpace(entered))
        {
            box.Text = binding.Gesture;
            return;
        }
        if (!GlobalHotkeyService.TryParse(entered, out _, out _))
        {
            box.Text = binding.Gesture;
            HotkeyStatusText.Text = $"“{entered}”不是有效的全局快捷键。";
            return;
        }
        binding.Gesture = GlobalHotkeyService.Normalize(entered);
        box.Text = binding.Gesture;
        SaveAndApplyShortcuts($"已设置“{binding.DisplayName}”：{binding.Gesture}。");
    }

    private void SaveAndApplyShortcuts(string status)
    {
        _settingsRepository.Save(_settings);
        ApplyGlobalHotkey();
        if (!string.IsNullOrWhiteSpace(status)) HotkeyStatusText.Text = status + " " + HotkeyStatusText.Text;
    }

    private void RunAtStartupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUtilitySettings) return;
        var enabled = RunAtStartupToggle.IsChecked == true;
        try
        {
            StartupRegistrationService.SetEnabled(enabled);
            _settings.RunAtStartup = enabled;
            StartMinimizedToggle.IsEnabled = enabled && _settings.EnableTrayIcon;
            _settingsRepository.Save(_settings);
            StatusText.Text = enabled ? "已启用当前用户开机启动。" : "已关闭开机启动。";
        }
        catch (Exception exception)
        {
            _suppressUtilitySettings = true;
            RunAtStartupToggle.IsChecked = !enabled;
            _suppressUtilitySettings = false;
            StatusText.Text = $"修改开机启动失败：{exception.Message}";
        }
    }

    private void ApplyTraySetting()
    {
        if (_settings.EnableTrayIcon)
        {
            _trayIcon ??= new TrayIconService(
                new WindowInteropHelper(this).Handle,
                () => _workspaceRepository.LoadAll(),
                RestoreWorkspaceAsync,
                ShowAndActivate,
                () => ((App)Application.Current).ShowCommandPalette(),
                () => _activeSession?.Workspace.Name,
                () => EndActiveWorkspace(promptForStartedWindows: false),
                RequestExit);
            _trayIcon.Refresh();
        }
        else
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
        }
    }

    private void ApplyGlobalHotkey()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var registrations = _settings.GlobalShortcuts.Select(binding =>
            new GlobalHotkeyRegistration(
                binding.Id,
                binding.Gesture,
                _settings.GlobalHotkeyEnabled && binding.Enabled &&
                (binding.Action != GlobalShortcutAction.RestoreWorkspace || !string.IsNullOrWhiteSpace(binding.WorkspaceId)),
                binding.Action switch
                {
                    GlobalShortcutAction.CommandPalette => () => ((App)Application.Current).ShowCommandPalette(),
                    GlobalShortcutAction.ShowMainWindow => ShowAndActivate,
                    GlobalShortcutAction.SmartCapture => () => _ = ExecuteExternalCommandAsync(CommandPaletteAction.SmartCapture),
                    GlobalShortcutAction.NewWorkspace => () => _ = ExecuteExternalCommandAsync(CommandPaletteAction.NewWorkspace),
                    GlobalShortcutAction.RestoreWorkspace => () => _ = RestoreQuickWorkspaceAsync(binding.WorkspaceId),
                    _ => () => ((App)Application.Current).ShowCommandPalette()
                })).ToList();
        var results = _globalHotkey.Apply(hwnd, registrations);
        if (!_settings.GlobalHotkeyEnabled)
        {
            HotkeyStatusText.Text = "全局快捷键未启用。";
            return;
        }

        var failures = results.Where(result => result.Enabled && !result.Registered).ToList();
        var registered = results.Count(result => result.Registered);
        var unassigned = _settings.GlobalShortcuts.Count(binding =>
            binding.Enabled && binding.Action == GlobalShortcutAction.RestoreWorkspace && string.IsNullOrWhiteSpace(binding.WorkspaceId));
        HotkeyStatusText.Text = failures.Count == 0
            ? $"已启用 {registered} 个全局快捷键" + (unassigned == 0 ? "。" : $"；{unassigned} 个工作区槽位尚未分配。")
            : $"已启用 {registered} 个；" + string.Join("；", failures.Select(failure =>
                $"{Shortcut(failure.Id).DisplayName} {failure.Gesture}：{failure.Message}"));
    }

    private async Task RestoreQuickWorkspaceAsync(string? workspaceId)
    {
        var workspaces = _workspaceRepository.LoadAll();
        var workspace = workspaces.FirstOrDefault(item =>
            string.Equals(item.Id, workspaceId, StringComparison.OrdinalIgnoreCase));
        if (workspace is null)
        {
            ShowAndActivate();
            StatusText.Text = "该快捷槽位尚未分配有效工作区。";
            return;
        }
        await RestoreWorkspaceAsync(workspace);
    }

    public void ShowAndActivate()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void HideToTray(bool showNotice)
    {
        if (!_settings.EnableTrayIcon) return;
        ShowInTaskbar = false;
        Hide();
        if (showNotice) _trayIcon?.ShowStillRunningNotice();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowExit) return;
        if (_settings.EnableTrayIcon && _settings.CloseToTray)
        {
            e.Cancel = true;
            HideToTray(showNotice: true);
            return;
        }

        _allowExit = true;
        Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
    }

    private void RequestExit()
    {
        _allowExit = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        Application.Current.Shutdown();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        LibraryView.Visibility = Visibility.Collapsed;
        EditorView.Visibility = Visibility.Collapsed;
        CaptureView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        RefreshBlockedRules();
        PageTitle.Text = "设置";
        PageSubtitle.Text = "外观、数据和运行状态";
        _suppressUtilitySettings = true;
        RefreshShortcutEditors();
        _suppressUtilitySettings = false;
        RefreshQuickWorkspacePicker();
        var workingSet = Process.GetCurrentProcess().WorkingSet64 / 1024d / 1024d;
        PerformanceText.Text =
            $"当前工作集：{workingSet:0.0} MB\n" +
            $"界面启动计时：{App.StartupClock.ElapsedMilliseconds} ms\n" +
            $"路线三：{(_capability?.Supported == true ? "可用" : "不可用或仍在检测")}";
    }

    private void RefreshWindows_Click(object sender, RoutedEventArgs e) =>
        RefreshWindowChoices(updateEditor: true);

    private void LayoutPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLayoutSelection || LayoutPicker.SelectedItem is not SnapLayoutDefinition layout)
        {
            return;
        }

        BuildZoneRows(layout);
    }

    private void OpenVisualLayoutEditor_Click(object sender, RoutedEventArgs e)
    {
        if (LayoutPicker.SelectedItem is not SnapLayoutDefinition layout) return;

        static string ChoiceKey(WindowChoice choice) => choice.Snapshot is not null
            ? $"hwnd:{choice.Snapshot.Hwnd}"
            : choice.Identity?.ProcessPath ?? choice.Identity?.AppUserModelId ??
              $"{choice.DisplayName}|{choice.Identity?.ExactTitle}";

        var availableByKey = new Dictionary<string, VisualLayoutAssignment>(StringComparer.OrdinalIgnoreCase);
        var currentAssignments = new Dictionary<string, VisualLayoutAssignment>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _zoneRows)
        {
            if (!row.SelectedChoice.IsEmpty)
            {
                var assignment = new VisualLayoutAssignment(
                    row.SelectedChoice,
                    row.Launch.Clone(),
                    row.Match.Clone(),
                    row.ApplicationId);
                currentAssignments[row.Zone.Id] = assignment;
                availableByKey[ChoiceKey(row.SelectedChoice)] = assignment;
            }

            foreach (var choice in row.Choices.Where(choice => !choice.IsEmpty))
            {
                var key = ChoiceKey(choice);
                if (!availableByKey.ContainsKey(key))
                {
                    availableByKey[key] = new VisualLayoutAssignment(
                        choice,
                        choice.Launch.Clone(),
                        choice.Match.Clone(),
                        choice.ApplicationId);
                }
            }
        }

        var editor = new VisualLayoutEditorWindow(layout, availableByKey.Values.ToList(), currentAssignments)
        {
            Owner = this
        };
        if (editor.ShowDialog() != true || editor.ResultLayout is null) return;

        var result = editor.ResultLayout;
        var canonical = _layouts.FirstOrDefault(item =>
            string.Equals(item.Id, result.Id, StringComparison.OrdinalIgnoreCase));
        if (canonical is null)
        {
            canonical = result;
            _layouts.Add(canonical);
        }
        LayoutPicker.Items.Refresh();
        _suppressLayoutSelection = true;
        LayoutPicker.SelectedItem = canonical;
        _suppressLayoutSelection = false;
        BuildZoneRows(canonical);

        foreach (var row in _zoneRows)
        {
            if (!editor.ResultAssignments.TryGetValue(row.Zone.Id, out var assignment)) continue;
            var key = ChoiceKey(assignment.Choice);
            var choice = row.Choices.FirstOrDefault(candidate =>
                !candidate.IsEmpty && string.Equals(ChoiceKey(candidate), key, StringComparison.OrdinalIgnoreCase));
            if (choice is null)
            {
                choice = assignment.Choice;
                row.Choices.Add(choice);
            }
            row.SelectedChoice = choice;
            row.Launch = assignment.Launch.Clone();
            row.Match = assignment.Match.Clone();
            row.ApplicationId = assignment.ApplicationId;
        }
        UpdateEditorPreview();
        StatusText.Text = $"已应用原生模型“{canonical.DisplayName}”：{canonical.Zones.Count} 个完整槽位，可继续留空或调整应用。";
    }

    private void ZoneChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ZoneAssignmentRow row)
        {
            var choice = row.SelectedChoice;
            if (choice.Snapshot is not null)
            {
                row.Launch = choice.Launch.Clone();
                row.Match = choice.Match.Clone();
                row.ApplicationId = null;
            }
            else if (choice.IsEmpty)
            {
                row.Launch = new ApplicationLaunchSpec();
                row.Match = new WindowMatchSpec();
                row.ApplicationId = null;
            }
        }

        Dispatcher.BeginInvoke(UpdateEditorPreview, DispatcherPriority.Background);
    }

    private void AddBackgroundApplication_Click(object sender, RoutedEventArgs e) =>
        _backgroundRows.Add(new BackgroundApplicationRow());

    private async void ChooseInstalledApplicationForZone_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ZoneAssignmentRow row) return;
        var application = await PickInstalledApplicationAsync();
        if (application is null) return;
        var choice = WindowChoice.FromInstalledApplication(application);
        row.Choices.Add(choice);
        row.SelectedChoice = choice;
        row.Launch = choice.Launch.Clone();
        row.Match = choice.Match.Clone();
        row.ApplicationId = null;
        ZoneRows.Items.Refresh();
        UpdateEditorPreview();
        StatusText.Text = $"已把“{application.DisplayName}”加入 {row.ZoneTitle}；恢复时会先启动并按进程匹配窗口。";
    }

    private async void ChooseInstalledBackgroundApplication_Click(object sender, RoutedEventArgs e)
    {
        var application = await PickInstalledApplicationAsync();
        if (application is null) return;
        var launch = application.ToLaunchSpec();
        _backgroundRows.Add(new BackgroundApplicationRow
        {
            DisplayName = application.DisplayName,
            ExecutablePath = launch.ExecutablePath ?? string.Empty,
            AppUserModelId = launch.AppUserModelId ?? string.Empty,
            Arguments = launch.Arguments,
            WorkingDirectory = launch.WorkingDirectory
        });
        StatusText.Text = $"已添加后台应用“{application.DisplayName}”；恢复启动阶段会强制保持无可见窗口。";
    }

    private async Task<InstalledApplicationEntry?> PickInstalledApplicationAsync()
    {
        try
        {
            StatusText.Text = "正在读取开始菜单、AppsFolder 和已安装程序…";
            _installedApplicationsTask ??= InstalledApplicationCatalog.EnumerateAsync();
            var applications = await _installedApplicationsTask;
            if (applications.Count == 0)
            {
                StatusText.Text = "没有找到可启动的已安装应用。";
                return null;
            }

            var picker = new InstalledApplicationPickerWindow(
                applications,
                _settings.FavoriteApplicationKeys,
                _settings.RecentApplicationKeys)
            {
                Owner = this
            };
            var accepted = picker.ShowDialog() == true;
            _settings.FavoriteApplicationKeys = picker.FavoriteKeys.ToList();
            if (accepted && picker.SelectedApplication is not null)
            {
                _settings.RecentApplicationKeys.RemoveAll(key =>
                    string.Equals(key, picker.SelectedApplication.IdentityKey, StringComparison.OrdinalIgnoreCase));
                _settings.RecentApplicationKeys.Insert(0, picker.SelectedApplication.IdentityKey);
                if (_settings.RecentApplicationKeys.Count > 20)
                {
                    _settings.RecentApplicationKeys.RemoveRange(20, _settings.RecentApplicationKeys.Count - 20);
                }
            }
            _settingsRepository.Save(_settings);
            StatusText.Text = accepted ? "已选择已安装应用。" : "已取消选择应用。";
            return accepted ? picker.SelectedApplication : null;
        }
        catch (Exception exception)
        {
            _installedApplicationsTask = null;
            StatusText.Text = $"读取已安装应用失败：{exception.Message}";
            return null;
        }
    }

    private void RemoveBackgroundApplication_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BackgroundApplicationRow row)
        {
            _backgroundRows.Remove(row);
        }
    }

    private void RemoveCompatibilityApplication_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CompatibilityApplicationRow row)
        {
            _compatibilityRows.Remove(row);
        }
    }

    private void BrowseBackgroundExecutable_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BackgroundApplicationRow row) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择后台应用",
            Filter = "应用程序 (*.exe)|*.exe|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        row.ExecutablePath = dialog.FileName;
        row.WorkingDirectory = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
        if (row.DisplayName == "后台应用") row.DisplayName = Path.GetFileNameWithoutExtension(dialog.FileName);
        BackgroundRows.Items.Refresh();
    }

    private void EditorScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            FindAncestor<ComboBox>(source) is { IsDropDownOpen: true })
        {
            return;
        }

        EditorScrollViewer.ScrollToVerticalOffset(
            Math.Clamp(
                EditorScrollViewer.VerticalOffset - e.Delta * 0.85,
                0,
                EditorScrollViewer.ScrollableHeight));
        e.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? child)
        where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
            {
                return match;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private void SaveWorkspace_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var workspace = BuildWorkspaceFromEditor();
            _workspaceRepository.Save(workspace);
            _editingWorkspace = workspace;
            RefreshWorkspaceCards();
            ShowLibrary();
            StatusText.Text = $"已保存“{workspace.Name}”。";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async void SaveAndRestore_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var workspace = BuildWorkspaceFromEditor();
            _workspaceRepository.Save(workspace);
            _editingWorkspace = workspace;
            RefreshWorkspaceCards();
            await RestoreWorkspaceAsync(workspace);
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async void RestoreCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is WorkspaceCardViewModel card)
        {
            await RestoreWorkspaceAsync(card.Workspace);
        }
    }

    private void CheckCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not WorkspaceCardViewModel card)
        {
            return;
        }

        var preflight = WorkspacePreflight.Analyze(
            card.Workspace,
            WindowCatalog.EnumerateCandidates(),
            _capability?.Supported,
            _capability?.EffectiveMaximumWindowsPerSubmission ?? 4);
        StatusText.Text = preflight.CanRestore
            ? $"“{card.Name}”检查完成：可以恢复，{preflight.WarningCount} 条提醒。"
            : $"“{card.Name}”检查未通过：{preflight.ErrorCount} 个错误。";
        MessageBox.Show(
            this,
            FormatPreflightReport(preflight),
            "恢复前检查",
            MessageBoxButton.OK,
            preflight.CanRestore ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private static string FormatPreflightReport(WorkspacePreflightResult result)
    {
        var heading = result.CanRestore
            ? $"可以恢复 · {result.WarningCount} 条提醒"
            : $"暂时不能恢复 · {result.ErrorCount} 个错误";
        var lines = result.Issues.Select(issue =>
        {
            var marker = issue.Severity switch
            {
                WorkspacePreflightSeverity.Error => "错误",
                WorkspacePreflightSeverity.Warning => "提醒",
                _ => "就绪"
            };
            return $"[{marker}] {issue.Message}";
        });
        return heading + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private static string FormatPreparationReport(WorkspacePreparationResult result)
    {
        if (result.Outcomes.Count == 0)
        {
            return "没有需要启动或匹配的应用。";
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            result.Outcomes.Select(outcome =>
            {
                var status = outcome.Status switch
                {
                    ApplicationStartStatus.AlreadyAvailable => "已复用",
                    ApplicationStartStatus.Started => "已启动",
                    ApplicationStartStatus.NotLaunchable => "无法启动",
                    _ => "失败"
                };
                var match = outcome.MatchScore is null
                    ? string.Empty
                    : $"{Environment.NewLine}匹配 {outcome.MatchScore} 分：{outcome.MatchReason}";
                return $"[{status}] {outcome.DisplayName}{Environment.NewLine}{outcome.Message}{match}";
            }));
    }

    private void EditCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is WorkspaceCardViewModel card)
        {
            RefreshWindowChoices(updateEditor: false);
            ShowEditor(card.Workspace);
        }
    }

    private void DeleteCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not WorkspaceCardViewModel card)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"删除工作区“{card.Name}”？应用窗口和布局文件以外的数据不会被删除。",
            "删除工作区",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _workspaceRepository.Delete(card.Workspace);
        RefreshWorkspaceCards();
        StatusText.Text = $"已删除“{card.Name}”。";
    }

    private void InitializeThemePicker()
    {
        ThemePicker.ItemsSource = new[] { "Dark", "Light", "System" };
        _suppressThemeSelection = true;
        ThemePicker.SelectedItem = _settings.Theme;
        _suppressThemeSelection = false;
        ApplyTheme(_settings.Theme);
    }

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressThemeSelection || ThemePicker.SelectedItem is not string theme)
        {
            return;
        }

        _settings.Theme = theme;
        ApplyTheme(theme);
        _settingsRepository.Save(_settings);
        StatusText.Text = $"主题已切换为 {theme}。";
    }

    internal static void ApplyTheme(string theme)
    {
        if (SystemParameters.HighContrast)
        {
            ApplyHighContrastTheme();
            return;
        }
        var isLight = theme switch
        {
            "Light" => true,
            "System" => IsSystemLightTheme(),
            _ => false
        };
        CurrentThemeMode(theme);
        SetBrush("AppBackgroundBrush", isLight ? "#F4F6FA" : "#0B0D12");
        SetBrush("SidebarBrush", isLight ? "#FFFFFF" : "#11141B");
        SetBrush("CardBrush", isLight ? "#FFFFFF" : "#171B24");
        SetBrush("CardHoverBrush", isLight ? "#F1F3F8" : "#1D2230");
        SetBrush("BorderBrushSoft", isLight ? "#DCE1EA" : "#2B3140");
        SetBrush("TextPrimaryBrush", isLight ? "#171A21" : "#F5F7FB");
        SetBrush("TextSecondaryBrush", isLight ? "#626B7B" : "#AAB2C2");
        SetBrush("InputBrush", isLight ? "#F8F9FC" : "#10131A");
        SetBrush("SecondarySurfaceBrush", isLight ? "#EEF1F6" : "#252B38");
    }

    private static void ApplyHighContrastTheme()
    {
        Application.Current.Resources["AppBackgroundBrush"] = SystemColors.WindowBrush;
        Application.Current.Resources["SidebarBrush"] = SystemColors.WindowBrush;
        Application.Current.Resources["CardBrush"] = SystemColors.WindowBrush;
        Application.Current.Resources["CardHoverBrush"] = SystemColors.ControlBrush;
        Application.Current.Resources["BorderBrushSoft"] = SystemColors.WindowTextBrush;
        Application.Current.Resources["TextPrimaryBrush"] = SystemColors.WindowTextBrush;
        Application.Current.Resources["TextSecondaryBrush"] = SystemColors.GrayTextBrush;
        Application.Current.Resources["AccentBrush"] = SystemColors.HighlightBrush;
        Application.Current.Resources["AccentHoverBrush"] = SystemColors.HighlightBrush;
        Application.Current.Resources["SuccessBrush"] = SystemColors.WindowTextBrush;
        Application.Current.Resources["WarningBrush"] = SystemColors.WindowTextBrush;
        Application.Current.Resources["InputBrush"] = SystemColors.WindowBrush;
        Application.Current.Resources["SecondarySurfaceBrush"] = SystemColors.ControlBrush;
    }

    private static void CurrentThemeMode(string theme)
    {
        Application.Current.ThemeMode = theme switch
        {
            "Light" => ThemeMode.Light,
            "System" => ThemeMode.System,
            _ => ThemeMode.Dark
        };
    }

    private static void SetBrush(string key, string color) =>
        Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    private static bool IsSystemLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_workspaceRepository.RootPath);
        Process.Start(new ProcessStartInfo("explorer.exe", _workspaceRepository.RootPath)
        {
            UseShellExecute = true
        });
    }

    private async Task RenderPreviewIfRequestedAsync()
    {
        var args = Environment.GetCommandLineArgs();
        var argumentIndex = Array.FindIndex(args, value =>
            string.Equals(value, "--render-preview", StringComparison.OrdinalIgnoreCase));
        if (argumentIndex < 0 || argumentIndex + 1 >= args.Length)
        {
            return;
        }

        var utilityTestIndex = Array.FindIndex(args, value =>
            string.Equals(value, "--utility-test-output", StringComparison.OrdinalIgnoreCase));
        if (utilityTestIndex >= 0 && utilityTestIndex + 1 < args.Length)
        {
            RunUtilityRegression(Path.GetFullPath(args[utilityTestIndex + 1]));
        }

        var previewLayoutIndex = Array.FindIndex(args, value =>
            string.Equals(value, "--preview-layout", StringComparison.OrdinalIgnoreCase));
        var previewLayoutId = previewLayoutIndex >= 0 && previewLayoutIndex + 1 < args.Length
            ? args[previewLayoutIndex + 1]
            : "quarters";
        var capturePreview = args.Any(value =>
            string.Equals(value, "--capture-preview", StringComparison.OrdinalIgnoreCase));
        var manualCapturePreview = args.Any(value =>
            string.Equals(value, "--manual-capture-preview", StringComparison.OrdinalIgnoreCase));
        var settingsPreview = args.Any(value =>
            string.Equals(value, "--settings-preview", StringComparison.OrdinalIgnoreCase));
        var diagnosticsPreview = args.Any(value =>
            string.Equals(value, "--diagnostics-preview", StringComparison.OrdinalIgnoreCase));
        var hotkeySettingsPreview = args.Any(value =>
            string.Equals(value, "--hotkey-settings-preview", StringComparison.OrdinalIgnoreCase));
        var activeSessionPreview = args.Any(value =>
            string.Equals(value, "--active-session-preview", StringComparison.OrdinalIgnoreCase));
        var visualLayoutPreview = args.Any(value =>
            string.Equals(value, "--visual-layout-preview", StringComparison.OrdinalIgnoreCase));
        var installedPickerPreview = args.Any(value =>
            string.Equals(value, "--installed-picker-preview", StringComparison.OrdinalIgnoreCase));
        var accessibilityTestIndex = Array.FindIndex(args, value =>
            string.Equals(value, "--accessibility-test-output", StringComparison.OrdinalIgnoreCase));
        if (manualCapturePreview)
        {
            ShowPreviewManualCapture();
        }
        else if (capturePreview)
        {
            ShowPreviewCapture();
        }
        else if (settingsPreview)
        {
            ShowPreviewSettings();
        }
        else if (activeSessionPreview)
        {
            ShowPreviewActiveSession();
        }
        else
        {
            ShowPreviewEditor(previewLayoutId);
        }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(350);
        if (settingsPreview && diagnosticsPreview)
        {
            SettingsScrollViewer.ScrollToVerticalOffset(620);
            UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);
        }
        else if (settingsPreview && hotkeySettingsPreview)
        {
            SettingsScrollViewer.ScrollToVerticalOffset(430);
            UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);
        }
        if (accessibilityTestIndex >= 0 && accessibilityTestIndex + 1 < args.Length)
        {
            var reportPath = Path.GetFullPath(args[accessibilityTestIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            var passed = RunAccessibilityRegression();
            File.WriteAllText(reportPath, passed
                ? "PASS: keyboard navigation, automation names, live regions and high-contrast resources"
                : "FAIL: accessibility regression");
            if (!passed) throw new InvalidOperationException("辅助功能回归测试失败。");
            ShowPreviewSettings();
            if (diagnosticsPreview) SettingsScrollViewer.ScrollToVerticalOffset(620);
            UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(120);
        }
        if (installedPickerPreview)
        {
            var applications = await InstalledApplicationCatalog.EnumerateAsync();
            var picker = new InstalledApplicationPickerWindow(applications, [], []) { Owner = this };
            picker.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(250);
            var passed = picker.RunRegressionScenario();
            var pickerTestIndex = Array.FindIndex(args, value =>
                string.Equals(value, "--installed-picker-test-output", StringComparison.OrdinalIgnoreCase));
            if (pickerTestIndex >= 0 && pickerTestIndex + 1 < args.Length)
            {
                var reportPath = Path.GetFullPath(args[pickerTestIndex + 1]);
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                File.WriteAllText(reportPath, passed
                    ? $"PASS: search, favorites and virtualization catalog count={applications.Count}"
                    : "FAIL: installed application picker regression");
            }
            if (!passed) throw new InvalidOperationException("已安装应用选择器回归测试失败。");
            await Dispatcher.Yield(DispatcherPriority.Render);
            var pickerOutputPath = Path.GetFullPath(args[argumentIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(pickerOutputPath)!);
            var pickerBitmap = new RenderTargetBitmap(
                Math.Max(1, (int)picker.ActualWidth),
                Math.Max(1, (int)picker.ActualHeight),
                96,
                96,
                PixelFormats.Pbgra32);
            pickerBitmap.Render(picker);
            var pickerEncoder = new PngBitmapEncoder();
            pickerEncoder.Frames.Add(BitmapFrame.Create(pickerBitmap));
            using (var pickerStream = File.Create(pickerOutputPath)) pickerEncoder.Save(pickerStream);
            picker.Close();
            Close();
            return;
        }
        if (visualLayoutPreview)
        {
            var available = _zoneRows
                .SelectMany(row => row.Choices)
                .Where(choice => !choice.IsEmpty)
                .DistinctBy(choice => choice.Snapshot?.Hwnd.ToString() ?? choice.DisplayName)
                .Take(8)
                .Select(choice => new VisualLayoutAssignment(
                    choice,
                    choice.Launch.Clone(),
                    choice.Match.Clone(),
                    choice.ApplicationId))
                .ToList();
            var initialAssignments = new Dictionary<string, VisualLayoutAssignment>(StringComparer.OrdinalIgnoreCase);
            if (available.Count > 0)
            {
                initialAssignments[BuiltInLayouts.Get("halves").Zones[0].Id] = available[0];
            }
            var visualEditor = new VisualLayoutEditorWindow(
                BuiltInLayouts.Get("halves"),
                available,
                initialAssignments)
            {
                Owner = this
            };
            visualEditor.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(200);
            var passed = visualEditor.RunRegressionScenario();
            var visualTestIndex = Array.FindIndex(args, value =>
                string.Equals(value, "--visual-layout-test-output", StringComparison.OrdinalIgnoreCase));
            if (visualTestIndex >= 0 && visualTestIndex + 1 < args.Length)
            {
                var reportPath = Path.GetFullPath(args[visualTestIndex + 1]);
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                File.WriteAllText(reportPath, passed
                    ? "PASS: split and merge snap to native models; empty slots preserve the complete layout"
                    : "FAIL: visual layout operation regression");
            }
            if (!passed) throw new InvalidOperationException("可视化布局编辑器回归测试失败。");
            await Dispatcher.Yield(DispatcherPriority.Render);
            var visualOutputPath = Path.GetFullPath(args[argumentIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(visualOutputPath)!);
            var visualBitmap = new RenderTargetBitmap(
                Math.Max(1, (int)visualEditor.ActualWidth),
                Math.Max(1, (int)visualEditor.ActualHeight),
                96,
                96,
                PixelFormats.Pbgra32);
            visualBitmap.Render(visualEditor);
            var visualEncoder = new PngBitmapEncoder();
            visualEncoder.Frames.Add(BitmapFrame.Create(visualBitmap));
            using (var visualStream = File.Create(visualOutputPath)) visualEncoder.Save(visualStream);
            visualEditor.Close();
            Close();
            return;
        }
        var scopeTestIndex = Array.FindIndex(args, value =>
            string.Equals(value, "--scope-test-output", StringComparison.OrdinalIgnoreCase));
        if (capturePreview && scopeTestIndex >= 0 && scopeTestIndex + 1 < args.Length)
        {
            var passed = true;
            for (var mask = 0; mask < 8; mask++)
            {
                CaptureTaskbarToggle.IsChecked = (mask & 1) != 0;
                CaptureTrayToggle.IsChecked = (mask & 2) != 0;
                CaptureBackgroundToggle.IsChecked = (mask & 4) != 0;
                ApplyCaptureScope();
                var expected = ((mask & 1) != 0 ? 4 : 0) +
                               ((mask & 2) != 0 ? 1 : 0) +
                               ((mask & 4) != 0 ? 1 : 0);
                passed &= _captureRows.Count == expected;
            }
            var testPath = Path.GetFullPath(args[scopeTestIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(testPath)!);
            File.WriteAllText(testPath, passed
                ? "PASS: all 8 taskbar/tray/background combinations"
                : $"FAIL: visible={_captureRows.Count}");
            if (!passed)
            {
                throw new InvalidOperationException("捕捉范围组合过滤测试失败。");
            }

            CaptureTaskbarToggle.IsChecked = true;
            CaptureTrayToggle.IsChecked = false;
            CaptureBackgroundToggle.IsChecked = false;
            ApplyCaptureScope();
            await Dispatcher.Yield(DispatcherPriority.Render);
        }
        var blockTestIndex = Array.FindIndex(args, value =>
            string.Equals(value, "--block-test-output", StringComparison.OrdinalIgnoreCase));
        if (capturePreview && blockTestIndex >= 0 && blockTestIndex + 1 < args.Length)
        {
            var target = _captureInventory.First();
            var rule = new CaptureExclusionRule
            {
                DisplayName = target.DisplayName,
                ExecutablePath = target.ExecutablePath,
                AppUserModelId = target.AppUserModelId
            };
            _settings.CaptureExclusions.Add(rule);
            _captureInventory.RemoveAll(IsCaptureBlocked);
            var filtered = _captureInventory.All(row =>
                !string.Equals(row.LaunchTargetKey, target.LaunchTargetKey, StringComparison.OrdinalIgnoreCase));
            var reportPath = Path.GetFullPath(args[blockTestIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            var settingsTestPath = reportPath + ".settings.json";
            var testRepository = new SettingsRepository(settingsTestPath);
            testRepository.Save(_settings);
            var persisted = testRepository.Load().CaptureExclusions.Any(item => item.Id == rule.Id);
            var passed = filtered && persisted;
            File.WriteAllText(reportPath, passed
                ? "PASS: exclusion filtered and persisted"
                : $"FAIL: filtered={filtered} persisted={persisted}");
            if (File.Exists(settingsTestPath)) File.Delete(settingsTestPath);
            _settings.CaptureExclusions.RemoveAll(item => item.Id == rule.Id);
            if (!passed) throw new InvalidOperationException("捕捉屏蔽测试失败。");
            ShowPreviewCapture();
            await Dispatcher.Yield(DispatcherPriority.Render);
        }
        var capturePatternTestIndex = Array.FindIndex(args, value =>
            string.Equals(value, "--capture-pattern-test-output", StringComparison.OrdinalIgnoreCase));
        if (capturePatternTestIndex >= 0 && capturePatternTestIndex + 1 < args.Length)
        {
            var testRule = new CaptureExclusionRule
            {
                DisplayName = "Windows 安全通知图标",
                ExecutablePattern = @"*\SecurityHealthSystray.exe",
                PackageFamilyPattern = "Microsoft.SecHealthUI_*",
                PublisherPattern = "*Microsoft*",
                IsRecommended = true
            };
            var reportPath = Path.GetFullPath(args[capturePatternTestIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            var settingsPath = reportPath + ".settings.json";
            var repository = new SettingsRepository(settingsPath);
            repository.Save(new AppSettings { CaptureExclusions = [testRule] });
            var loaded = repository.Load().CaptureExclusions.Single();
            var passed = WildcardMatches(testRule.ExecutablePattern, @"C:\Windows\System32\SecurityHealthSystray.exe") &&
                         WildcardMatches(testRule.PackageFamilyPattern, "Microsoft.SecHealthUI_8wekyb3d8bbwe") &&
                         WildcardMatches(testRule.PublisherPattern, "Microsoft Corporation") &&
                         loaded.IsRecommended && loaded.ExecutablePattern == testRule.ExecutablePattern;
            File.WriteAllText(reportPath, passed
                ? "PASS: executable wildcard, package family, publisher and persistence"
                : "FAIL: structured capture exclusion regression");
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
            if (!passed) throw new InvalidOperationException("结构化捕捉屏蔽规则回归测试失败。");
        }
        var captureRuleTestIndex = Array.FindIndex(args, value =>
            string.Equals(value, "--capture-rule-test-output", StringComparison.OrdinalIgnoreCase));
        if (capturePreview && captureRuleTestIndex >= 0 && captureRuleTestIndex + 1 < args.Length)
        {
            var target = _captureInventory.First(row => row.Window is not null);
            var originalRules = _settings.CaptureRoleRules.ToList();
            var rule = new CaptureRoleRule
            {
                DisplayName = target.DisplayName,
                ExecutablePath = target.ExecutablePath,
                AppUserModelId = target.AppUserModelId,
                Role = CaptureApplicationRole.CompatibilityWindow
            };
            _settings.CaptureRoleRules = [rule];
            target.SelectedRole = CaptureRoleOption.All.First(option => option.Role == CaptureApplicationRole.Ignore);
            ApplySavedCaptureRole(target);
            var reportPath = Path.GetFullPath(args[captureRuleTestIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            var settingsTestPath = reportPath + ".settings.json";
            var testRepository = new SettingsRepository(settingsTestPath);
            testRepository.Save(_settings);
            var persisted = testRepository.Load().CaptureRoleRules.SingleOrDefault();
            var passed = target.SelectedRole.Role == CaptureApplicationRole.CompatibilityWindow &&
                         persisted?.Role == CaptureApplicationRole.CompatibilityWindow &&
                         string.Equals(persisted.ExecutablePath, target.ExecutablePath, StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(reportPath, passed
                ? "PASS: capture role applied and persisted"
                : "FAIL: reusable capture role regression");
            if (File.Exists(settingsTestPath)) File.Delete(settingsTestPath);
            _settings.CaptureRoleRules = originalRules;
            if (!passed) throw new InvalidOperationException("捕捉角色规则回归测试失败。");
            ShowPreviewCapture();
            await Dispatcher.Yield(DispatcherPriority.Render);
        }
        var installedCatalogTestIndex = Array.FindIndex(args, value =>
            string.Equals(value, "--installed-catalog-test-output", StringComparison.OrdinalIgnoreCase));
        if (installedCatalogTestIndex >= 0 && installedCatalogTestIndex + 1 < args.Length)
        {
            var applications = await InstalledApplicationCatalog.EnumerateAsync();
            var distinctIdentities = applications
                .Select(application => application.IdentityKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var sample = applications.FirstOrDefault();
            var choice = sample is null ? null : WindowChoice.FromInstalledApplication(sample);
            var passed = applications.Count > 0 &&
                         applications.All(application => application.ToLaunchSpec().CanLaunch) &&
                         distinctIdentities == applications.Count &&
                         choice?.Launch.CanLaunch == true &&
                         choice.Snapshot is null;
            var reportPath = Path.GetFullPath(args[installedCatalogTestIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            var bySource = applications
                .GroupBy(application => application.SourceLabel)
                .Select(group => $"{group.Key}={group.Count()}");
            File.WriteAllText(
                reportPath,
                $"{(passed ? "PASS" : "FAIL")}: count={applications.Count}; " +
                string.Join(", ", bySource) +
                $"; sample={sample?.DisplayName ?? "none"}");
            if (!passed) throw new InvalidOperationException("已安装应用目录回归测试失败。");
        }
        if (!capturePreview && !settingsPreview && args.Any(value => string.Equals(value, "--background-detail", StringComparison.OrdinalIgnoreCase)))
        {
            EditorScrollViewer.ScrollToVerticalOffset(1250);
            await Dispatcher.Yield(DispatcherPriority.Render);
        }
        else if (!capturePreview && !settingsPreview && args.Any(value => string.Equals(value, "--editor-detail", StringComparison.OrdinalIgnoreCase)))
        {
            EditorScrollViewer.ScrollToVerticalOffset(470);
            await Dispatcher.Yield(DispatcherPriority.Render);
        }

        var wheelTestIndex = Array.FindIndex(args, value =>
            string.Equals(value, "--wheel-test-output", StringComparison.OrdinalIgnoreCase));
        if (wheelTestIndex >= 0 && wheelTestIndex + 1 < args.Length)
        {
            var comboBox = FindVisualChild<SmoothComboBox>(ZoneRows)
                ?? throw new InvalidOperationException("没有找到应用选择框。");
            if (comboBox.DataContext is ZoneAssignmentRow wheelRow)
            {
                for (var index = wheelRow.Choices.Count; index < 18; index++)
                {
                    wheelRow.Choices.Add(new WindowChoice
                    {
                        DisplayName = $"滚轮回归测试应用 {index:00}",
                        Details = $"test-{index:00}.exe",
                        Identity = new WindowIdentity
                        {
                            ClassName = "WheelRegressionWindow",
                            ExactTitle = $"Wheel regression {index:00}"
                        }
                    });
                }
            }
            comboBox.IsDropDownOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(150);
            var wheelResult = comboBox.RunSyntheticWheelTest(-120);
            var passed = wheelResult.Handled && wheelResult.After > wheelResult.Before;
            var testPath = Path.GetFullPath(args[wheelTestIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(testPath)!);
            File.WriteAllText(
                testPath,
                $"{(passed ? "PASS" : "FAIL")} handled={wheelResult.Handled} " +
                $"before={wheelResult.Before:0.##} after={wheelResult.After:0.##}");
            comboBox.IsDropDownOpen = false;
            if (!passed)
            {
                throw new InvalidOperationException("Popup 滚轮事件未被下拉列表消费。");
            }
        }

        var repositoryTestIndex = Array.FindIndex(args, value =>
            string.Equals(value, "--repository-test-output", StringComparison.OrdinalIgnoreCase));
        if (repositoryTestIndex >= 0 && repositoryTestIndex + 1 < args.Length)
        {
            RunRepositoryRoundTripTest(Path.GetFullPath(args[repositoryTestIndex + 1]));
        }

        var outputPath = Path.GetFullPath(args[argumentIndex + 1]);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)ActualWidth),
            Math.Max(1, (int)ActualHeight),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
        Close();
    }

    private static void RunRepositoryRoundTripTest(string reportPath)
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"snapworkspace-repository-{Guid.NewGuid():N}");
        var archivePath = testRoot + ".snapworkspace";
        try
        {
            var source = new WorkspaceRepository(Path.Combine(testRoot, "source"));
            var workspace = WorkspaceService.Create(
                "Archive test",
                BuiltInLayouts.Get("halves"),
                new PhysicalRect(0, 0, 1920, 1080),
                new[]
                {
                    new WorkspaceApplication
                    {
                        DisplayName = "Test host",
                        Kind = WorkspaceApplicationKind.Background,
                        Launch = new ApplicationLaunchSpec
                        {
                            ExecutablePath = Environment.ProcessPath
                        }
                    }
                });
            source.Save(workspace);
            workspace.Name = "Archive test updated";
            source.Save(workspace);
            source.ExportAll(archivePath);

            var target = new WorkspaceRepository(Path.Combine(testRoot, "target"));
            var imported = target.Import(archivePath);
            var importedAgain = target.Import(archivePath);
            var backupCount = Directory.Exists(source.BackupRootPath)
                ? Directory.EnumerateFiles(source.BackupRootPath, "*.json", SearchOption.AllDirectories).Count()
                : 0;
            var importedWorkspaces = target.LoadAll();
            var passed = imported == 1 &&
                         importedAgain == 1 &&
                         importedWorkspaces.Count == 2 &&
                         importedWorkspaces.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2 &&
                         backupCount == 1;
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, passed
                ? "PASS: automatic backup and archive round trip"
                : $"FAIL: imported={imported}+{importedAgain} target={importedWorkspaces.Count} backups={backupCount}");
            if (!passed)
            {
                throw new InvalidOperationException("工作区备份与导入回归测试失败。");
            }
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    private void RunUtilityRegression(string reportPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var gestures = new[] { "Ctrl+Alt+W", "Ctrl+Alt+1", "Ctrl+Shift+W", "Win+Alt+W" };
        var hotkeysPassed = gestures.All(gesture =>
            GlobalHotkeyService.TryParse(gesture, out var modifiers, out var key) && modifiers != 0 && key != 0) &&
            !GlobalHotkeyService.TryParse("W", out _, out _) &&
            GlobalHotkeyService.TryParse("Ctrl+Alt+1", out _, out var digitKey) && digitKey == 0x31 &&
            GlobalHotkeyService.Normalize("alt+ctrl+space") == "Ctrl+Alt+Space";
        using var registrationProbe = new GlobalHotkeyService();
        var registrationResults = registrationProbe.Apply(
            new WindowInteropHelper(this).Handle,
            [
                new GlobalHotkeyRegistration("probe-1", "Ctrl+Shift+F11", true, () => { }),
                new GlobalHotkeyRegistration("probe-2", "Ctrl+Shift+F12", true, () => { }),
                new GlobalHotkeyRegistration("probe-duplicate", "Ctrl+Shift+F11", true, () => { })
            ]);
        var registrationPassed = registrationResults.Count(result => result.Registered) == 2 &&
                                 registrationResults.Single(result => result.Id == "probe-duplicate").Message.Contains("重复");
        var startupCommandPassed = StartupRegistrationService.CurrentCommand.StartsWith('"') &&
                                   StartupRegistrationService.CurrentCommand.EndsWith(" --startup", StringComparison.Ordinal);
        var settingsPath = reportPath + ".settings.json";
        try
        {
            var repository = new SettingsRepository(settingsPath);
            var shortcuts = GlobalShortcutBinding.CreateDefaults();
            shortcuts.First(item => item.Id == GlobalShortcutBinding.QuickWorkspace1Id).WorkspaceId = "quick-one";
            shortcuts.First(item => item.Id == GlobalShortcutBinding.QuickWorkspace2Id).WorkspaceId = "quick-two";
            shortcuts.First(item => item.Id == GlobalShortcutBinding.QuickWorkspace3Id).WorkspaceId = "quick-three";
            repository.Save(new AppSettings
            {
                EnableTrayIcon = true,
                CloseToTray = true,
                GlobalHotkeyEnabled = true,
                GlobalShortcuts = shortcuts,
                GlobalHotkey = "Ctrl+Shift+W",
                QuickWorkspaceId = "quick-test",
                RunAtStartup = true,
                StartMinimizedAtLogin = true
            });
            var loaded = repository.Load();
            var settingsPassed = loaded.EnableTrayIcon && loaded.CloseToTray && loaded.GlobalHotkeyEnabled &&
                                 loaded.GlobalHotkey == "Ctrl+Shift+W" && loaded.QuickWorkspaceId == "quick-test" &&
                                 loaded.RunAtStartup && loaded.StartMinimizedAtLogin &&
                                 loaded.GlobalShortcuts.Count == 7 &&
                                 loaded.GlobalShortcuts.Where(item => item.Action == GlobalShortcutAction.RestoreWorkspace)
                                     .Select(item => item.WorkspaceId).SequenceEqual(new[] { "quick-one", "quick-two", "quick-three" });
            File.WriteAllText(reportPath, hotkeysPassed && registrationPassed && startupCommandPassed && settingsPassed
                ? "PASS: custom hotkey parsing/registration, duplicate detection, three quick-workspace slots and startup command persistence"
                : "FAIL: utility lifecycle regression");
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    private void ShowPreviewEditor(string layoutId)
    {
        ShowEditor(null);
        WorkspaceNameBox.Text = "专注写作";
        var layout = BuiltInLayouts.Get(layoutId);
        _suppressLayoutSelection = true;
        LayoutPicker.SelectedItem = _layouts.First(item => item.Id == layout.Id);
        _suppressLayoutSelection = false;
        BuildZoneRows(layout);

        var samples = new[]
        {
            new WindowChoice
            {
                DisplayName = "Visual Studio Code · 项目",
                Details = "Code.exe",
                Identity = new WindowIdentity { ClassName = "Chrome_WidgetWin_1", ExactTitle = "Visual Studio Code · 项目" }
            },
            new WindowChoice
            {
                DisplayName = "Microsoft Edge · 参考资料",
                Details = "msedge.exe",
                Identity = new WindowIdentity { ClassName = "Chrome_WidgetWin_1", ExactTitle = "Microsoft Edge · 参考资料" }
            }
        };
        for (var index = 0; index < samples.Length; index++)
        {
            _zoneRows[index].Choices.Add(samples[index]);
            _zoneRows[index].SelectedChoice = samples[index];
        }

        ZoneRows.Items.Refresh();
        _compatibilityRows.Add(new CompatibilityApplicationRow
        {
            DisplayName = "EasyPub · 兼容窗口",
            Identity = new WindowIdentity
            {
                ProcessPath = @"C:\Apps\EasyPub.exe",
                ClassName = "EasyPubWindow",
                ExactTitle = "EasyPub"
            },
            Launch = new ApplicationLaunchSpec { ExecutablePath = @"C:\Apps\EasyPub.exe" },
            Bounds = new NormalizedRect { X = 0.05, Y = 0.68, Width = 0.38, Height = 0.28 }
        });
        _backgroundRows.Add(new BackgroundApplicationRow
        {
            DisplayName = "同步服务",
            ExecutablePath = @"C:\Tools\sync-agent.exe",
            Arguments = "--silent",
            WorkingDirectory = @"C:\Tools"
        });
        UpdateEditorPreview();
        StatusText.Text = $"正在预览：{layout.DisplayName}。";
    }

    private void ShowPreviewCapture()
    {
        _captureRows.Clear();
        _captureInventory.Clear();
        var samples = new[]
        {
            new WindowSnapshot(new nint(1), 101, @"C:\Apps\Editor.exe", "EditorWindow", "代码编辑器 · Snap Workspace",
                new PhysicalRect(0, 0, 1280, 1080)),
            new WindowSnapshot(new nint(2), 102, @"C:\Apps\Browser.exe", "BrowserWindow", "参考资料 · Microsoft Edge",
                new PhysicalRect(1280, 0, 640, 540)),
            new WindowSnapshot(new nint(3), 103, @"C:\Apps\Terminal.exe", "TerminalWindow", "终端 · 构建输出",
                new PhysicalRect(1280, 540, 640, 540))
        };
        foreach (var snapshot in samples)
        {
            _captureInventory.Add(new CaptureApplicationRow
            {
                Window = snapshot,
                Surface = CaptureApplicationSurface.Taskbar,
                SuggestedRole = CaptureApplicationRole.NativeSnapWindow,
                AutoReason = "窗口尺寸和位置适合参与布局，已自动放入吸附集合。",
                SelectedRole = CaptureRoleOption.All.First(option =>
                    option.Role == CaptureApplicationRole.NativeSnapWindow)
            });
        }

        _captureInventory.Add(new CaptureApplicationRow
        {
            Window = new WindowSnapshot(new nint(4), 104, @"C:\Apps\Reader.exe", "ReaderWindow", "电子书工具",
                new PhysicalRect(10, 10, 219, 39)),
            Surface = CaptureApplicationSurface.Taskbar,
            SuggestedRole = CaptureApplicationRole.CompatibilityWindow,
            AutoReason = "窗口不适合原生 Snap，按兼容定位处理。",
            SelectedRole = CaptureRoleOption.All.First(option =>
                    option.Role == CaptureApplicationRole.CompatibilityWindow)
        });
        _captureInventory.Add(new CaptureApplicationRow
        {
            BackgroundProcess = new RunningApplicationSnapshot(
                105, "同步服务", @"C:\Tools\sync-agent.exe", null, RunningApplicationSurface.Tray),
            Surface = CaptureApplicationSurface.Tray,
            SuggestedRole = CaptureApplicationRole.Background,
            AutoReason = "当前进程存在通知区域图标，按后台启动处理。",
            SelectedRole = CaptureRoleOption.All.First(option =>
                    option.Role == CaptureApplicationRole.Background)
        });
        _captureInventory.Add(new CaptureApplicationRow
        {
            BackgroundProcess = new RunningApplicationSnapshot(
                106, "索引服务", @"C:\Tools\index-agent.exe", null, RunningApplicationSurface.Background),
            Surface = CaptureApplicationSurface.Background,
            SuggestedRole = CaptureApplicationRole.Background,
            AutoReason = "没有任务栏窗口或驻留窗口；归入纯后台进程。",
            SelectedRole = CaptureRoleOption.All.First(option =>
                    option.Role == CaptureApplicationRole.Background)
        });

        LibraryView.Visibility = Visibility.Collapsed;
        EditorView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        CaptureView.Visibility = Visibility.Visible;
        PageTitle.Text = "智能捕捉工作区";
        PageSubtitle.Text = "自动分类当前应用，再由你确认原生、兼容或后台角色";
        CaptureModeText.Text = "智能分类";
        ApplyCaptureScope();
    }

    private void ShowPreviewManualCapture()
    {
        ShowPreviewCapture();
        _manualCaptureMode = true;
        var ignore = CaptureRoleOption.All.First(option => option.Role == CaptureApplicationRole.Ignore);
        var background = CaptureRoleOption.All.First(option => option.Role == CaptureApplicationRole.Background);
        foreach (var row in _captureInventory)
        {
            row.SelectedRole = row.Window is null ? background : ignore;
        }
        PageTitle.Text = "手动捕捉工作区";
        PageSubtitle.Text = "桌面窗口默认忽略；启用范围内的无窗口进程默认后台启动";
        CaptureModeText.Text = "手动选择";
        ApplyCaptureScope();
        CaptureWindowRows.Items.Refresh();
    }

    private void ShowPreviewSettings()
    {
        LibraryView.Visibility = Visibility.Collapsed;
        EditorView.Visibility = Visibility.Collapsed;
        CaptureView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        PageTitle.Text = "设置";
        PageSubtitle.Text = "外观、捕捉规则和运行状态";
        PerformanceText.Text = "当前工作集：82.4 MB\n界面启动计时：842 ms\n路线三：可用";
        _blockedRules.Clear();
        _blockedRules.Add(new CaptureExclusionRule
        {
            DisplayName = "Windows Security notification icon",
            ExecutablePath = @"C:\Windows\System32\SecurityHealthSystray.exe"
        });
        _captureRoleRules.Clear();
        _captureRoleRules.Add(new CaptureRoleRule
        {
            DisplayName = "电子书工具",
            ExecutablePath = @"C:\Apps\Reader.exe",
            Role = CaptureApplicationRole.CompatibilityWindow
        });
        _captureRoleRules.Add(new CaptureRoleRule
        {
            DisplayName = "同步服务",
            ExecutablePath = @"C:\Tools\sync-agent.exe",
            Role = CaptureApplicationRole.Background
        });
    }

    private bool RunAccessibilityRegression()
    {
        var openedEditor = HandleShortcut(Key.N, ModifierKeys.Control) && EditorView.Visibility == Visibility.Visible;
        var openedSettings = HandleShortcut(Key.OemComma, ModifierKeys.Control) && SettingsView.Visibility == Visibility.Visible;
        var returnedToLibrary = HandleShortcut(Key.Escape, ModifierKeys.None) && LibraryView.Visibility == Visibility.Visible;
        var automationNames = new FrameworkElement[]
        {
            ThemePicker,
            QuickWorkspace1Picker,
            CommandPaletteHotkeyBox,
            DiagnosticLoggingToggle,
            IncludeApplicationDetailsToggle,
            IncludeWorkspaceDefinitionsToggle
        }.All(element => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(element)) ||
                         element is ContentControl { Content: string content } && !string.IsNullOrWhiteSpace(content));
        var liveRegions = AutomationProperties.GetLiveSetting(PageTitle) == AutomationLiveSetting.Polite &&
                          AutomationProperties.GetLiveSetting(StatusText) == AutomationLiveSetting.Polite &&
                          AutomationProperties.GetLiveSetting(SupportBundlePrivacyText) == AutomationLiveSetting.Polite;
        ApplyHighContrastTheme();
        var highContrastResources = ReferenceEquals(Application.Current.Resources["AppBackgroundBrush"], SystemColors.WindowBrush) &&
                                    ReferenceEquals(Application.Current.Resources["AccentBrush"], SystemColors.HighlightBrush) &&
                                    ReferenceEquals(Application.Current.Resources["TextPrimaryBrush"], SystemColors.WindowTextBrush);
        ApplyTheme(_settings.Theme);
        return openedEditor && openedSettings && returnedToLibrary && automationNames && liveRegions && highContrastResources;
    }

    private void ShowPreviewActiveSession()
    {
        var workspace = _workspaceCards.FirstOrDefault()?.Workspace ?? WorkspaceService.Create(
            "专注写作",
            BuiltInLayouts.Get("main-left-stack"),
            ShellSnapBackend.GetPrimaryWorkArea(),
            Array.Empty<WorkspaceApplication>());
        _activeSession = new ActiveWorkspaceSession(
            workspace,
            DateTimeOffset.Now,
            [
                new ManagedSessionWindow((nint)101, "editor", "编辑器", true),
                new ManagedSessionWindow((nint)102, "browser", "参考资料", false),
                new ManagedSessionWindow((nint)103, "terminal", "终端", true)
            ],
            1);
        ShowLibrary();
        UpdateActiveWorkspaceUi();
        StatusText.Text = "工作区运行中；结束时只会关闭本次恢复新启动的窗口。";
    }
}
