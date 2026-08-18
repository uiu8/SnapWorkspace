using System.Windows.Interop;
using SnapWorkspace.Route3;

namespace SnapWorkspace.App.Services;

public sealed class StartupTrayHost : IDisposable
{
    private static readonly nint HwndMessage = (nint)(-3);
    private readonly HwndSource _messageWindow;
    private readonly TrayIconService _trayIcon;
    private readonly GlobalHotkeyService _globalHotkey = new();
    private readonly WorkspaceRepository _repository;
    private readonly AppSettings _settings;
    private readonly Func<WorkspaceDefinition, Task> _restore;

    public StartupTrayHost(
        AppSettings settings,
        WorkspaceRepository repository,
        Func<WorkspaceDefinition, Task> restore,
        Action showWindow,
        Action showCommandPalette,
        Func<CommandPaletteAction, Task> executeCommand,
        Action exit)
    {
        _settings = settings;
        _repository = repository;
        _restore = restore;
        _messageWindow = new HwndSource(new HwndSourceParameters("SnapWorkspace.StartupTrayHost")
        {
            ParentWindow = HwndMessage,
            WindowStyle = 0,
            Width = 0,
            Height = 0
        });
        _trayIcon = new TrayIconService(
            _messageWindow.Handle,
            repository.LoadAll,
            restore,
            showWindow,
            showCommandPalette,
            () => null,
            () => { },
            exit);
        _ = _globalHotkey.Apply(
            _messageWindow.Handle,
            settings.GlobalShortcuts.Select(binding => new GlobalHotkeyRegistration(
                binding.Id,
                binding.Gesture,
                settings.GlobalHotkeyEnabled && binding.Enabled,
                binding.Action switch
                {
                    GlobalShortcutAction.CommandPalette => showCommandPalette,
                    GlobalShortcutAction.ShowMainWindow => showWindow,
                    GlobalShortcutAction.SmartCapture => () => _ = executeCommand(CommandPaletteAction.SmartCapture),
                    GlobalShortcutAction.NewWorkspace => () => _ = executeCommand(CommandPaletteAction.NewWorkspace),
                    GlobalShortcutAction.RestoreWorkspace => () => _ = RestoreWorkspaceAsync(binding.WorkspaceId),
                    _ => showCommandPalette
                })).ToList());
    }

    private async Task RestoreWorkspaceAsync(string? workspaceId)
    {
        var workspaces = _repository.LoadAll();
        var workspace = workspaces.FirstOrDefault(item =>
            string.Equals(item.Id, workspaceId, StringComparison.OrdinalIgnoreCase));
        if (workspace is not null) await _restore(workspace);
    }

    public void Dispose()
    {
        _globalHotkey.Dispose();
        _trayIcon.Dispose();
        _messageWindow.Dispose();
    }
}
