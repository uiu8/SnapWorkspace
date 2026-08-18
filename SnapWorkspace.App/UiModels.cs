using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using SnapWorkspace.App.Services;
using SnapWorkspace.Route3;

namespace SnapWorkspace.App;

public sealed class WorkspaceCardViewModel
{
    public required WorkspaceDefinition Workspace { get; init; }
    public string Name => Workspace.Name;
    public string LayoutName => Workspace.Layout.DisplayName;
    public string Summary =>
        $"{Workspace.NativeSnapWindowCount} 个原生 · {Workspace.CompatibilityWindowCount} 个兼容 · " +
        $"{Workspace.BackgroundApplicationCount} 个后台 · {Workspace.EmptyZoneIds.Count} 个空槽位";
    public IReadOnlyList<string> AssignedZoneIds => Workspace.Applications
        .Where(application => application.IsNativeSnapWindow)
        .Select(application => application.ZoneId!)
        .ToList();
}

public sealed record WorkspaceSelectionOption(string? Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record ManagedSessionWindow(
    nint Hwnd,
    string ApplicationId,
    string DisplayName,
    bool StartedByRestore);

public sealed record ActiveWorkspaceSession(
    WorkspaceDefinition Workspace,
    DateTimeOffset StartedAt,
    IReadOnlyList<ManagedSessionWindow> Windows,
    int StartedBackgroundApplicationCount)
{
    public int CloseableWindowCount => Windows.Count(window => window.StartedByRestore);
}

public sealed class WindowChoice
{
    public required string DisplayName { get; init; }
    public string Details { get; init; } = string.Empty;
    public WindowSnapshot? Snapshot { get; init; }
    public WindowIdentity? Identity { get; init; }
    public WindowMatchSpec Match { get; init; } = new();
    public ApplicationLaunchSpec Launch { get; init; } = new();
    public string? ApplicationId { get; init; }
    public bool IsEmpty => Identity is null;
    public bool IsUnavailable { get; init; }

    public static WindowChoice Empty { get; } = new()
    {
        DisplayName = "留空，显示桌面",
        Details = "恢复时不会向这个 Zone 提交窗口"
    };

    public static WindowChoice FromSnapshot(WindowSnapshot snapshot) => new()
    {
        DisplayName = snapshot.Title,
        Details = Path.GetFileName(snapshot.ProcessPath) ?? snapshot.AppUserModelId ?? snapshot.ClassName,
        Snapshot = snapshot,
        Identity = WorkspaceService.IdentityFrom(snapshot),
        Launch = WorkspaceService.LaunchFrom(snapshot)
    };

    public static WindowChoice Unavailable(WorkspaceApplication application) => new()
    {
        DisplayName = application.DisplayName,
        Details = "当前未运行；会按保存的启动信息尝试启动",
        Identity = application.WindowIdentity,
        Match = application.Match.Clone(),
        Launch = application.Launch.Clone(),
        ApplicationId = application.Id,
        IsUnavailable = true
    };

    public static WindowChoice FromInstalledApplication(InstalledApplicationEntry application) => new()
    {
        DisplayName = application.DisplayName,
        Details = $"未运行 · {application.SourceLabel} · 恢复时启动",
        Identity = new WindowIdentity
        {
            ProcessPath = application.ExecutablePath,
            AppUserModelId = application.AppUserModelId,
            ClassName = string.Empty,
            ExactTitle = string.Empty
        },
        Match = new WindowMatchSpec
        {
            ProcessMode = WindowProcessMatchMode.ExactPath,
            TitleMode = WindowTitleMatchMode.Ignore
        },
        Launch = application.ToLaunchSpec(),
        IsUnavailable = true
    };
}

public sealed record ProcessMatchOption(WindowProcessMatchMode Mode, string DisplayName)
{
    public static IReadOnlyList<ProcessMatchOption> All { get; } =
    [
        new(WindowProcessMatchMode.Automatic, "自动判断"),
        new(WindowProcessMatchMode.ExactPath, "完整路径 / AUMID"),
        new(WindowProcessMatchMode.FileName, "仅文件名"),
        new(WindowProcessMatchMode.Ignore, "忽略进程")
    ];
}

public sealed record TitleMatchOption(WindowTitleMatchMode Mode, string DisplayName)
{
    public static IReadOnlyList<TitleMatchOption> All { get; } =
    [
        new(WindowTitleMatchMode.Automatic, "自动判断"),
        new(WindowTitleMatchMode.Exact, "标题完全相同"),
        new(WindowTitleMatchMode.Contains, "标题包含文字"),
        new(WindowTitleMatchMode.Wildcard, "通配符（* / ?）"),
        new(WindowTitleMatchMode.RegularExpression, "正则表达式"),
        new(WindowTitleMatchMode.Ignore, "忽略标题")
    ];
}

public sealed class ZoneAssignmentRow : INotifyPropertyChanged
{
    private WindowChoice _selectedChoice = WindowChoice.Empty;
    private WindowMatchSpec _match = new();

    public required SnapZone Zone { get; init; }
    public required ObservableCollection<WindowChoice> Choices { get; init; }
    public WindowChoice SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (ReferenceEquals(_selectedChoice, value)) return;
            _selectedChoice = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAssigned));
        }
    }

    public ApplicationLaunchSpec Launch { get; set; } = new();
    public WindowMatchSpec Match
    {
        get => _match;
        set
        {
            _match = value ?? new WindowMatchSpec();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProcessMode));
            OnPropertyChanged(nameof(TitleMode));
            OnPropertyChanged(nameof(TitlePattern));
            OnPropertyChanged(nameof(RequireSameWindowClass));
        }
    }
    public string? ApplicationId { get; set; }
    public IReadOnlyList<ProcessMatchOption> ProcessMatchOptions => ProcessMatchOption.All;
    public IReadOnlyList<TitleMatchOption> TitleMatchOptions => TitleMatchOption.All;
    public WindowProcessMatchMode ProcessMode { get => Match.ProcessMode; set => Match.ProcessMode = value; }
    public WindowTitleMatchMode TitleMode { get => Match.TitleMode; set => Match.TitleMode = value; }
    public string TitlePattern { get => Match.TitlePattern; set => Match.TitlePattern = value ?? string.Empty; }
    public bool RequireSameWindowClass { get => Match.RequireSameWindowClass; set => Match.RequireSameWindowClass = value; }
    public string ZoneTitle => Zone.Id;
    public string ZoneDetails => $"x {Zone.Rect.X:0.##} · y {Zone.Rect.Y:0.##} · {Zone.Rect.Width:P0} × {Zone.Rect.Height:P0}";
    public bool IsAssigned => !SelectedChoice.IsEmpty;
    public string Arguments { get => Launch.Arguments; set => Launch.Arguments = value ?? string.Empty; }
    public string WorkingDirectory { get => Launch.WorkingDirectory; set => Launch.WorkingDirectory = value ?? string.Empty; }
    public bool RunAsAdministrator { get => Launch.RunAsAdministrator; set => Launch.RunAsAdministrator = value; }
    public bool AlwaysStartNewInstance { get => Launch.AlwaysStartNewInstance; set => Launch.AlwaysStartNewInstance = value; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class BackgroundApplicationRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "后台应用";
    public string ExecutablePath { get; set; } = string.Empty;
    public string AppUserModelId { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public bool RunAsAdministrator { get; set; }
    public bool AlwaysStartNewInstance { get; set; }
    public WindowIdentity? Identity { get; set; }
    public WindowMatchSpec Match { get; set; } = new();

    public WorkspaceApplication ToApplication() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Kind = WorkspaceApplicationKind.Background,
        BackgroundWindowMode = BackgroundWindowMode.Hide,
        WindowIdentity = Identity,
        Match = Match.Clone(),
        Launch = new ApplicationLaunchSpec
        {
            ExecutablePath = string.IsNullOrWhiteSpace(ExecutablePath) ? null : ExecutablePath.Trim(),
            AppUserModelId = string.IsNullOrWhiteSpace(AppUserModelId) ? null : AppUserModelId.Trim(),
            Arguments = Arguments ?? string.Empty,
            WorkingDirectory = WorkingDirectory ?? string.Empty,
            RunAsAdministrator = RunAsAdministrator,
            AlwaysStartNewInstance = AlwaysStartNewInstance
        }
    };

    public static BackgroundApplicationRow FromApplication(WorkspaceApplication application) => new()
    {
        Id = application.Id,
        DisplayName = application.DisplayName,
        ExecutablePath = application.Launch.ExecutablePath ?? string.Empty,
        AppUserModelId = application.Launch.AppUserModelId ?? string.Empty,
        Arguments = application.Launch.Arguments,
        WorkingDirectory = application.Launch.WorkingDirectory,
        RunAsAdministrator = application.Launch.RunAsAdministrator,
        AlwaysStartNewInstance = application.Launch.AlwaysStartNewInstance,
        Identity = application.WindowIdentity,
        Match = application.Match.Clone()
    };
}

public sealed class CompatibilityApplicationRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "兼容窗口";
    public required WindowIdentity Identity { get; init; }
    public WindowMatchSpec Match { get; set; } = new();
    public ApplicationLaunchSpec Launch { get; set; } = new();
    public required NormalizedRect Bounds { get; init; }
    public string ExecutablePath => Launch.ExecutablePath ?? Launch.AppUserModelId ?? "无启动目标";
    public string PositionDetails =>
        $"x {Bounds.X:P0} · y {Bounds.Y:P0} · {Bounds.Width:P0} × {Bounds.Height:P0}";
    public string Arguments { get => Launch.Arguments; set => Launch.Arguments = value ?? string.Empty; }
    public string WorkingDirectory { get => Launch.WorkingDirectory; set => Launch.WorkingDirectory = value ?? string.Empty; }
    public bool RunAsAdministrator { get => Launch.RunAsAdministrator; set => Launch.RunAsAdministrator = value; }
    public bool AlwaysStartNewInstance { get => Launch.AlwaysStartNewInstance; set => Launch.AlwaysStartNewInstance = value; }

    public WorkspaceApplication ToApplication() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Kind = WorkspaceApplicationKind.Window,
        WindowPlacement = WorkspaceWindowPlacementMode.CompatibilityPositioned,
        CompatibilityBounds = new NormalizedRect
        {
            X = Bounds.X,
            Y = Bounds.Y,
            Width = Bounds.Width,
            Height = Bounds.Height
        },
        WindowIdentity = Identity,
        Match = Match.Clone(),
        Launch = Launch.Clone()
    };

    public static CompatibilityApplicationRow FromApplication(WorkspaceApplication application) => new()
    {
        Id = application.Id,
        DisplayName = application.DisplayName,
        Identity = application.WindowIdentity!,
        Match = application.Match.Clone(),
        Launch = application.Launch.Clone(),
        Bounds = new NormalizedRect
        {
            X = application.CompatibilityBounds!.X,
            Y = application.CompatibilityBounds.Y,
            Width = application.CompatibilityBounds.Width,
            Height = application.CompatibilityBounds.Height
        }
    };
}

public enum CaptureApplicationRole
{
    NativeSnapWindow,
    CompatibilityWindow,
    Background,
    Ignore
}

public enum CaptureApplicationSurface
{
    Taskbar,
    Tray,
    Background,
    Manual
}

public sealed record CaptureRoleOption(
    CaptureApplicationRole Role,
    string DisplayName,
    string Description)
{
    public static IReadOnlyList<CaptureRoleOption> All { get; } =
    [
        new(CaptureApplicationRole.NativeSnapWindow, "原生吸附", "可见，并提交给 Windows Snap Layout"),
        new(CaptureApplicationRole.CompatibilityWindow, "兼容定位", "可见，不提交 Snap；恢复时定位一次"),
        new(CaptureApplicationRole.Background, "后台启动", "不占桌面布局；恢复启动阶段强制无可见窗口"),
        new(CaptureApplicationRole.Ignore, "忽略", "不保存到工作区")
    ];
}

public sealed class CaptureApplicationRow : INotifyPropertyChanged
{
    private CaptureRoleOption _selectedRole = CaptureRoleOption.All[3];

    public WindowSnapshot? Window { get; init; }
    public RunningApplicationSnapshot? BackgroundProcess { get; init; }
    public required CaptureApplicationSurface Surface { get; init; }
    public required string AutoReason { get; init; }
    public string? Publisher { get; init; }
    public CaptureApplicationRole SuggestedRole { get; init; } = CaptureApplicationRole.Ignore;
    public IReadOnlyList<CaptureRoleOption> RoleOptions => Window is null
        ? CaptureRoleOption.All.Where(option =>
            option.Role is CaptureApplicationRole.Background or CaptureApplicationRole.Ignore).ToList()
        : CaptureRoleOption.All;
    public CaptureRoleOption SelectedRole
    {
        get => _selectedRole;
        set
        {
            if (_selectedRole == value) return;
            _selectedRole = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedRole)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedRoleDescription)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReviewBadge)));
        }
    }

    public string DisplayName => Window?.Title ?? BackgroundProcess?.DisplayName ?? "应用";
    public string Details => Window is not null
        ? $"{Path.GetFileName(Window.ProcessPath) ?? Window.AppUserModelId ?? Window.ClassName} · {Window.VisibleBounds.Width}×{Window.VisibleBounds.Height}"
        : $"{Path.GetFileName(BackgroundProcess?.ExecutablePath) ?? BackgroundProcess?.AppUserModelId ?? "后台进程"} · 当前没有可吸附窗口";
    public string Glyph => Window is null ? "\uE968" : "\uE7F4";
    public string SurfaceLabel => Surface switch
    {
        CaptureApplicationSurface.Taskbar => "任务栏",
        CaptureApplicationSurface.Tray => "托盘/驻留",
        CaptureApplicationSurface.Background => "纯后台",
        _ => "手动选取"
    };
    public string? ExecutablePath => Window?.ProcessPath ?? BackgroundProcess?.ExecutablePath;
    public string? AppUserModelId => Window?.AppUserModelId ?? BackgroundProcess?.AppUserModelId;
    public string? PackageFamilyName
    {
        get
        {
            var separator = AppUserModelId?.IndexOf('!') ?? -1;
            return separator > 0 ? AppUserModelId![..separator] : null;
        }
    }
    public string LaunchTargetKey => ExecutablePath ?? AppUserModelId ?? DisplayName;
    public string SelectedRoleDescription => SelectedRole.Description;
    public string ReviewBadge => SelectedRole.Role == SuggestedRole ? "自动建议" : "已调整";
    public string ConfidenceLabel => Surface switch
    {
        CaptureApplicationSurface.Taskbar => "高可信",
        CaptureApplicationSurface.Tray => "Shell 托盘来源",
        CaptureApplicationSurface.Background => "后台候选",
        _ => "用户确认"
    };

    public WorkspaceApplication ToCompatibilityApplication(PhysicalRect workArea)
    {
        if (Window is null)
        {
            throw new InvalidOperationException("没有可用于兼容定位的窗口。");
        }

        return WorkspaceService.CreateCompatibilityApplication(Window, workArea);
    }

    public WorkspaceApplication ToBackgroundApplication() => new()
    {
        DisplayName = DisplayName,
        Kind = WorkspaceApplicationKind.Background,
        BackgroundWindowMode = BackgroundWindowMode.Hide,
        WindowIdentity = Window is null ? null : WorkspaceService.IdentityFrom(Window),
        Match = new WindowMatchSpec(),
        Launch = Window is not null
            ? WorkspaceService.LaunchFrom(Window)
            : BackgroundProcess!.ToLaunchSpec()
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record CaptureReviewIssue(string Glyph, string Message, bool IsBlocking = false);
