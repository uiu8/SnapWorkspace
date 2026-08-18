using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using SnapWorkspace.Route3;

namespace SnapWorkspace.App.Services;

internal sealed class TrayIconService : IDisposable
{
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NotifyIconVersion4 = 4;
    private const uint CallbackMessage = 0x8000 + 0x51;
    private const int WmLeftButtonDoubleClick = 0x0203;
    private const int WmRightButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const int NinSelect = 0x0400;
    private const int NinKeySelect = 0x0401;
    private static readonly nint IdiApplication = (nint)32512;

    private readonly nint _hwnd;
    private readonly HwndSource _source;
    private readonly Func<IReadOnlyList<WorkspaceDefinition>> _workspaceProvider;
    private readonly Func<WorkspaceDefinition, Task> _restore;
    private readonly Action _showWindow;
    private readonly Action _showCommandPalette;
    private readonly Action _endSession;
    private readonly Action _exit;
    private readonly Func<string?> _activeWorkspaceName;
    private NotifyIconData _data;
    private bool _added;
    private bool _ownsIcon;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIconW(nint instance, nint iconName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(
        string file,
        int iconIndex,
        out nint largeIcon,
        out nint smallIcon,
        uint iconCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIcon;
    }

    public TrayIconService(
        nint hwnd,
        Func<IReadOnlyList<WorkspaceDefinition>> workspaceProvider,
        Func<WorkspaceDefinition, Task> restore,
        Action showWindow,
        Action showCommandPalette,
        Func<string?> activeWorkspaceName,
        Action endSession,
        Action exit)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("主窗口句柄尚未创建。");
        _workspaceProvider = workspaceProvider;
        _restore = restore;
        _showWindow = showWindow;
        _showCommandPalette = showCommandPalette;
        _activeWorkspaceName = activeWorkspaceName;
        _endSession = endSession;
        _exit = exit;
        var icon = LoadIconW(nint.Zero, IdiApplication);
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath) &&
            ExtractIconExW(Environment.ProcessPath, 0, out var largeIcon, out var smallIcon, 1) > 0)
        {
            icon = smallIcon != nint.Zero ? smallIcon : largeIcon;
            if (largeIcon != nint.Zero && largeIcon != icon) _ = DestroyIcon(largeIcon);
            if (smallIcon != nint.Zero && smallIcon != icon) _ = DestroyIcon(smallIcon);
            _ownsIcon = icon != nint.Zero;
        }
        _data = new NotifyIconData
        {
            Size = checked((uint)Marshal.SizeOf<NotifyIconData>()),
            Window = hwnd,
            Id = 1,
            Flags = NifMessage | NifIcon | NifTip,
            CallbackMessage = CallbackMessage,
            Icon = icon,
            Tip = "Snap Workspace",
            Info = string.Empty,
            InfoTitle = string.Empty
        };
        _added = Shell_NotifyIconW(NimAdd, ref _data);
        if (!_added) throw new InvalidOperationException("Windows 通知区域图标创建失败。");
        _data.VersionOrTimeout = NotifyIconVersion4;
        _ = Shell_NotifyIconW(NimSetVersion, ref _data);
        _source.AddHook(WindowProc);
    }

    public void ShowStillRunningNotice()
    {
        if (!_added) return;
        _data.Flags = NifInfo;
        _data.InfoTitle = "Snap Workspace 仍在运行";
        _data.Info = "可从通知区域快速恢复工作区；使用托盘菜单中的“退出”可完全关闭。";
        _data.InfoFlags = 0x00000001;
        _ = Shell_NotifyIconW(NimModify, ref _data);
        _data.Flags = NifMessage | NifIcon | NifTip;
    }

    public void Refresh()
    {
        if (!_added) return;
        var active = _activeWorkspaceName();
        var tip = string.IsNullOrWhiteSpace(active) ? "Snap Workspace" : $"Snap Workspace · {active}";
        _data.Flags = NifTip;
        _data.Tip = tip[..Math.Min(127, tip.Length)];
        _ = Shell_NotifyIconW(NimModify, ref _data);
        _data.Flags = NifMessage | NifIcon | NifTip;
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != CallbackMessage) return nint.Zero;
        var notification = unchecked((int)((long)lParam & 0xFFFF));
        if (notification is WmLeftButtonDoubleClick or NinSelect or NinKeySelect)
        {
            handled = true;
            _showWindow();
        }
        else if (notification is WmRightButtonUp or WmContextMenu)
        {
            handled = true;
            ShowMenu();
        }
        return nint.Zero;
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        var activeName = _activeWorkspaceName();
        if (!string.IsNullOrWhiteSpace(activeName))
        {
            menu.Items.Add(new MenuItem { Header = $"正在运行：{activeName}", IsEnabled = false });
            var endItem = new MenuItem { Header = "结束管理（保留应用）" };
            endItem.Click += (_, _) => _endSession();
            menu.Items.Add(endItem);
            menu.Items.Add(new Separator());
        }

        var restoreMenu = new MenuItem { Header = "恢复工作区" };
        var workspaces = _workspaceProvider();
        foreach (var workspace in workspaces.Take(20))
        {
            var captured = workspace;
            var item = new MenuItem { Header = captured.Name };
            item.Click += async (_, _) => await _restore(captured);
            restoreMenu.Items.Add(item);
        }
        if (workspaces.Count == 0) restoreMenu.Items.Add(new MenuItem { Header = "暂无工作区", IsEnabled = false });
        menu.Items.Add(restoreMenu);
        menu.Items.Add(new Separator());
        var commands = new MenuItem { Header = "打开命令面板" };
        commands.Click += (_, _) => _showCommandPalette();
        menu.Items.Add(commands);
        var open = new MenuItem { Header = "打开 Snap Workspace" };
        open.Click += (_, _) => _showWindow();
        menu.Items.Add(open);
        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => _exit();
        menu.Items.Add(exit);
        _ = SetForegroundWindow(_hwnd);
        menu.IsOpen = true;
    }

    public void Dispose()
    {
        _source.RemoveHook(WindowProc);
        if (!_added) return;
        _ = Shell_NotifyIconW(NimDelete, ref _data);
        _added = false;
        if (_ownsIcon && _data.Icon != nint.Zero)
        {
            _ = DestroyIcon(_data.Icon);
            _data.Icon = nint.Zero;
            _ownsIcon = false;
        }
    }
}
