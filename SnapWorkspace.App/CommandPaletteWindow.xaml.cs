using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapWorkspace.App.Services;
using SnapWorkspace.Route3;

namespace SnapWorkspace.App;

public enum CommandPaletteAction
{
    ShowMainWindow,
    NewWorkspace,
    SmartCapture,
    ManualCapture,
    RefreshWindows,
    OpenSettings
}

public partial class CommandPaletteWindow : Window
{
    private readonly IReadOnlyList<CommandPaletteEntry> _allEntries;
    private readonly ObservableCollection<CommandPaletteEntry> _visibleEntries = [];
    private readonly Func<CommandPaletteAction, Task> _executeCommand;
    private readonly Func<WorkspaceDefinition, Task> _restoreWorkspace;
    private bool _executing;
    private bool _closing;

    internal CommandPaletteWindow(
        IReadOnlyList<WorkspaceDefinition> workspaces,
        AppSettings settings,
        Func<CommandPaletteAction, Task> executeCommand,
        Func<WorkspaceDefinition, Task> restoreWorkspace)
    {
        InitializeComponent();
        _executeCommand = executeCommand;
        _restoreWorkspace = restoreWorkspace;
        _allEntries = BuildEntries(workspaces, settings);
        ResultsList.ItemsSource = _visibleEntries;
        ApplyFilter(string.Empty);
    }

    private static IReadOnlyList<CommandPaletteEntry> BuildEntries(
        IReadOnlyList<WorkspaceDefinition> workspaces,
        AppSettings settings)
    {
        var entries = new List<CommandPaletteEntry>
        {
            Command("打开主窗口", "显示工作区库", "\uE8A7", CommandPaletteAction.ShowMainWindow, "主页 窗口"),
            Command("智能捕捉", "识别当前任务栏、托盘和后台应用", "\uE722", CommandPaletteAction.SmartCapture, "捕捉 识别 capture"),
            Command("手动捕捉", "自行选择需要保存的应用角色", "\uE7C8", CommandPaletteAction.ManualCapture, "选择 捕捉 manual"),
            Command("新建工作区", "从原生布局模型开始创建", "\uE710", CommandPaletteAction.NewWorkspace, "创建 new"),
            Command("刷新窗口", "重新扫描当前可用窗口", "\uE72C", CommandPaletteAction.RefreshWindows, "扫描 refresh"),
            Command("设置", "快捷键、托盘、诊断和外观", "\uE713", CommandPaletteAction.OpenSettings, "选项 settings")
        };

        var quickIds = new[]
        {
            GlobalShortcutBinding.QuickWorkspace1Id,
            GlobalShortcutBinding.QuickWorkspace2Id,
            GlobalShortcutBinding.QuickWorkspace3Id
        };
        var pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < quickIds.Length; index++)
        {
            var binding = settings.GlobalShortcuts.FirstOrDefault(item => item.Id == quickIds[index]);
            var workspace = workspaces.FirstOrDefault(item =>
                string.Equals(item.Id, binding?.WorkspaceId, StringComparison.OrdinalIgnoreCase));
            if (workspace is null || !pinned.Add(workspace.Id)) continue;
            entries.Insert(index, Workspace(workspace, true, $"固定工作区 {index + 1}", binding?.Gesture ?? string.Empty));
        }

        foreach (var workspace in workspaces.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (pinned.Contains(workspace.Id)) continue;
            entries.Add(Workspace(workspace, false, workspace.Layout.DisplayName, string.Empty));
        }
        return entries;
    }

    private static CommandPaletteEntry Command(
        string title,
        string subtitle,
        string icon,
        CommandPaletteAction action,
        string keywords) => new(title, subtitle, icon, string.Empty, false, action, null, $"{title} {subtitle} {keywords}");

    private static CommandPaletteEntry Workspace(
        WorkspaceDefinition workspace,
        bool pinned,
        string prefix,
        string gesture) => new(
        workspace.Name,
        $"{prefix} · {workspace.NativeSnapWindowCount} 原生 · {workspace.CompatibilityWindowCount} 兼容 · {workspace.BackgroundApplicationCount} 后台",
        "\uE8B7",
        gesture,
        pinned,
        null,
        workspace,
        $"{workspace.Name} {workspace.Layout.DisplayName} 工作区 workspace");

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        var args = Environment.GetCommandLineArgs();
        var previewIndex = Array.FindIndex(args, value =>
            string.Equals(value, "--command-palette-preview", StringComparison.OrdinalIgnoreCase));
        if (previewIndex < 0 || previewIndex + 1 >= args.Length) return;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, checked((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX))),
            Math.Max(1, checked((int)Math.Ceiling(ActualHeight * dpi.DpiScaleY))),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.GetFullPath(args[previewIndex + 1]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = File.Create(path)) encoder.Save(stream);
        Application.Current.Shutdown();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_executing || _closing || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closing && IsVisible) Close();
        });
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ApplyFilter(SearchBox.Text);

    private void ApplyFilter(string query)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filtered = _allEntries.Where(entry => tokens.All(token =>
            entry.SearchText.Contains(token, StringComparison.CurrentCultureIgnoreCase))).ToList();
        _visibleEntries.Clear();
        foreach (var entry in filtered) _visibleEntries.Add(entry);
        ResultsList.SelectedIndex = _visibleEntries.Count > 0 ? 0 : -1;
        ResultCountText.Text = _visibleEntries.Count == 0 ? "没有匹配结果" : $"{_visibleEntries.Count} 个可用项目";
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            MoveSelection(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            MoveSelection(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = ExecuteSelectedAsync();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private void MoveSelection(int delta)
    {
        if (_visibleEntries.Count == 0) return;
        var next = Math.Clamp(ResultsList.SelectedIndex + delta, 0, _visibleEntries.Count - 1);
        ResultsList.SelectedIndex = next;
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => _ = ExecuteSelectedAsync();

    private async Task ExecuteSelectedAsync()
    {
        if (_executing || ResultsList.SelectedItem is not CommandPaletteEntry entry) return;
        _executing = true;
        Hide();
        try
        {
            if (entry.Workspace is not null) await _restoreWorkspace(entry.Workspace);
            else if (entry.Action is not null) await _executeCommand(entry.Action.Value);
            Close();
        }
        catch (Exception exception)
        {
            Show();
            _executing = false;
            MessageBox.Show(this, exception.Message, "命令执行失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private sealed record CommandPaletteEntry(
        string Title,
        string Subtitle,
        string Icon,
        string Gesture,
        bool Pinned,
        CommandPaletteAction? Action,
        WorkspaceDefinition? Workspace,
        string SearchText)
    {
        public Visibility PinnedVisibility => Pinned ? Visibility.Visible : Visibility.Collapsed;
    }
}
