using System.Diagnostics;
using System.Threading;
using System.Windows;
using SnapWorkspace.App.Services;
using SnapWorkspace.Route3;

namespace SnapWorkspace.App;

public partial class App : Application
{
    internal static readonly Stopwatch StartupClock = Stopwatch.StartNew();

    private const string InstanceMutexName = "Local\\SnapWorkspace.Instance.v1";
    private const string ActivationEventName = "Local\\SnapWorkspace.Activate.v1";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationWait;
    private StartupTrayHost? _startupTrayHost;
    private CommandPaletteWindow? _commandPalette;
    private AppSettings _startupSettings = new();
    private readonly WorkspaceRepository _workspaceRepository = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        var commandPalettePreview = e.Args.Any(argument =>
            string.Equals(argument, "--command-palette-preview", StringComparison.OrdinalIgnoreCase));
        if (commandPalettePreview || e.Args.Any(argument =>
                string.Equals(argument, "--render-preview", StringComparison.OrdinalIgnoreCase)))
        {
            base.OnStartup(e);
            if (commandPalettePreview) ShowCommandPalette();
            else ShowMainWindow();
            return;
        }

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    using var activation = EventWaitHandle.OpenExisting(ActivationEventName);
                    activation.Set();
                    break;
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    Thread.Sleep(50);
                }
            }
            Shutdown();
            return;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) => Dispatcher.BeginInvoke(() =>
            {
                ShowMainWindow();
            }),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
        base.OnStartup(e);
        _startupSettings = new SettingsRepository().Load();
        var isLoginStartup = e.Args.Any(argument =>
            string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase));
        if (isLoginStartup && _startupSettings.EnableTrayIcon && _startupSettings.StartMinimizedAtLogin)
        {
            try
            {
                _startupTrayHost = new StartupTrayHost(
                    _startupSettings,
                    _workspaceRepository,
                    RestoreFromStartupHostAsync,
                    ShowMainWindow,
                    ShowCommandPalette,
                    ExecutePaletteCommandAsync,
                    Shutdown);
            }
            catch
            {
                ShowMainWindow();
                MessageBox.Show(
                    MainWindow,
                    "通知区域宿主未能启动，Snap Workspace 已回退到主窗口。",
                    "启动回退",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        else
        {
            ShowMainWindow();
        }
    }

    private MainWindow EnsureMainWindow(bool show)
    {
        if (MainWindow is MainWindow existing)
        {
            if (show) existing.ShowAndActivate();
            return existing;
        }

        _startupTrayHost?.Dispose();
        _startupTrayHost = null;
        var window = new MainWindow();
        MainWindow = window;
        if (show)
        {
            window.Show();
            window.Activate();
        }
        else
        {
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowState = WindowState.Minimized;
            window.Show();
            window.Hide();
        }
        return window;
    }

    private void ShowMainWindow() => EnsureMainWindow(show: true).ShowAndActivate();

    internal void ShowCommandPalette()
    {
        if (_commandPalette is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        var settings = new SettingsRepository().Load();
        SnapWorkspace.App.MainWindow.ApplyTheme(settings.Theme);
        _commandPalette = new CommandPaletteWindow(
            _workspaceRepository.LoadAll(),
            settings,
            ExecutePaletteCommandAsync,
            RestoreFromStartupHostAsync);
        _commandPalette.Closed += (_, _) => _commandPalette = null;
        _commandPalette.Show();
        _commandPalette.Activate();
    }

    internal async Task ExecutePaletteCommandAsync(CommandPaletteAction action)
    {
        var window = EnsureMainWindow(show: action == CommandPaletteAction.ShowMainWindow);
        await window.ExecuteExternalCommandAsync(action);
    }

    private async Task RestoreFromStartupHostAsync(WorkspaceDefinition workspace)
    {
        var window = EnsureMainWindow(show: false);
        await window.RestoreFromExternalAsync(workspace);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _startupTrayHost?.Dispose();
        _startupTrayHost = null;
        _commandPalette?.Close();
        _commandPalette = null;
        _activationWait?.Unregister(null);
        _activationEvent?.Dispose();
        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch { }
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }
}
