using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SnapWorkspace.Route3;

public enum WorkspaceApplicationKind
{
    Window,
    Background
}

public enum WorkspaceWindowPlacementMode
{
    NativeSnap,
    CompatibilityPositioned
}

public enum BackgroundWindowMode
{
    ApplicationManaged,
    Minimize,
    Hide
}

public enum WindowProcessMatchMode
{
    Automatic,
    ExactPath,
    FileName,
    Ignore
}

public enum WindowTitleMatchMode
{
    Automatic,
    Exact,
    Contains,
    Wildcard,
    RegularExpression,
    Ignore
}

public sealed class WindowMatchSpec
{
    public WindowProcessMatchMode ProcessMode { get; set; } = WindowProcessMatchMode.Automatic;
    public WindowTitleMatchMode TitleMode { get; set; } = WindowTitleMatchMode.Automatic;
    public string TitlePattern { get; set; } = string.Empty;
    public bool RequireSameWindowClass { get; set; }

    public WindowMatchSpec Clone() => new()
    {
        ProcessMode = ProcessMode,
        TitleMode = TitleMode,
        TitlePattern = TitlePattern,
        RequireSameWindowClass = RequireSameWindowClass
    };
}

public sealed class WindowIdentity
{
    public string? ProcessPath { get; set; }
    public string? AppUserModelId { get; set; }
    public required string ClassName { get; set; }
    public required string ExactTitle { get; set; }
}

public sealed class ApplicationLaunchSpec
{
    public string? ExecutablePath { get; set; }
    public string? AppUserModelId { get; set; }
    public string? LaunchUri { get; set; }
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public bool RunAsAdministrator { get; set; }
    public bool AlwaysStartNewInstance { get; set; }

    [JsonIgnore]
    public bool CanLaunch =>
        !string.IsNullOrWhiteSpace(ExecutablePath) ||
        !string.IsNullOrWhiteSpace(AppUserModelId) ||
        !string.IsNullOrWhiteSpace(LaunchUri);

    public ApplicationLaunchSpec Clone() => new()
    {
        ExecutablePath = ExecutablePath,
        AppUserModelId = AppUserModelId,
        LaunchUri = LaunchUri,
        Arguments = Arguments,
        WorkingDirectory = WorkingDirectory,
        RunAsAdministrator = RunAsAdministrator,
        AlwaysStartNewInstance = AlwaysStartNewInstance
    };
}

public sealed class WorkspaceApplication
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "应用";
    public WorkspaceApplicationKind Kind { get; set; }
    public WorkspaceWindowPlacementMode WindowPlacement { get; set; } = WorkspaceWindowPlacementMode.NativeSnap;
    public BackgroundWindowMode BackgroundWindowMode { get; set; } = BackgroundWindowMode.Hide;
    public string? ZoneId { get; set; }
    public NormalizedRect? CompatibilityBounds { get; set; }
    public WindowIdentity? WindowIdentity { get; set; }
    public WindowMatchSpec Match { get; set; } = new();
    public ApplicationLaunchSpec Launch { get; set; } = new();

    [JsonIgnore]
    public bool IsWindow => Kind == WorkspaceApplicationKind.Window;

    [JsonIgnore]
    public bool IsNativeSnapWindow => IsWindow && WindowPlacement == WorkspaceWindowPlacementMode.NativeSnap;

    [JsonIgnore]
    public bool IsCompatibilityWindow => IsWindow && WindowPlacement == WorkspaceWindowPlacementMode.CompatibilityPositioned;
}

// schema v2 兼容输入；schema v3 以后保存时不再写出该结构。
public sealed class WorkspaceWindowSlot
{
    public required string ZoneId { get; set; }
    public WindowIdentity? Identity { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Identity is null;
}

public sealed class WorkspaceDefinition
{
    public int SchemaVersion { get; set; } = 5;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string Name { get; set; }
    public required SnapLayoutDefinition Layout { get; set; }
    public required PhysicalRect CapturedWorkArea { get; set; }
    public List<WorkspaceApplication> Applications { get; set; } = [];

    [JsonPropertyName("windows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<WorkspaceWindowSlot>? LegacyWindows { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public int AssignedWindowCount => Applications.Count(application => application.IsWindow);

    [JsonIgnore]
    public int NativeSnapWindowCount => Applications.Count(application => application.IsNativeSnapWindow);

    [JsonIgnore]
    public int CompatibilityWindowCount => Applications.Count(application => application.IsCompatibilityWindow);

    [JsonIgnore]
    public int BackgroundApplicationCount =>
        Applications.Count(application => application.Kind == WorkspaceApplicationKind.Background);

    [JsonIgnore]
    public IReadOnlyList<string> EmptyZoneIds => Layout.Zones
        .Select(zone => zone.Id)
        .Where(zoneId => !Applications.Any(application =>
            application.IsNativeSnapWindow &&
            string.Equals(application.ZoneId, zoneId, StringComparison.OrdinalIgnoreCase)))
        .ToList();
}

public sealed record WorkspaceCaptureResult(
    WorkspaceDefinition Workspace,
    IReadOnlyList<string> EmptyZones);

public sealed record WorkspaceMatchResult(
    IReadOnlyList<SnapAssignment> Assignments,
    IReadOnlyList<CompatibilityWindowAssignment> CompatibilityAssignments,
    IReadOnlyList<WorkspaceApplication> MissingWindows);

public sealed record CompatibilityWindowAssignment(
    nint Hwnd,
    string ApplicationId,
    string DisplayName,
    NormalizedRect Bounds,
    string? SourceZoneId = null);

public sealed record WindowMatchEvaluation(
    bool IsMatch,
    int Score,
    string Reason);

public static class WorkspaceService
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static WorkspaceCaptureResult CaptureCurrent(
        string name,
        SnapLayoutDefinition layout,
        PhysicalRect workArea,
        IReadOnlyList<WindowSnapshot> candidates,
        double minimumZoneCoverage = 0.65)
    {
        ValidateLayout(layout);

        var used = new HashSet<nint>();
        var applications = new List<WorkspaceApplication>();
        var emptyZones = new List<string>();
        foreach (var zone in layout.Zones)
        {
            var zoneRect = zone.Rect.ToPhysical(workArea);
            var best = candidates
                .Where(window => !used.Contains(window.Hwnd))
                .Select(window => new
                {
                    Window = window,
                    Coverage = Coverage(zoneRect, window.VisibleBounds)
                })
                .OrderByDescending(item => item.Coverage)
                .FirstOrDefault();

            if (best is null || best.Coverage < minimumZoneCoverage)
            {
                emptyZones.Add(zone.Id);
                continue;
            }

            used.Add(best.Window.Hwnd);
            applications.Add(CreateWindowApplication(best.Window, zone.Id));
        }

        return new WorkspaceCaptureResult(
            Create(name, layout, workArea, applications),
            emptyZones);
    }

    public static WorkspaceDefinition Create(
        string name,
        SnapLayoutDefinition layout,
        PhysicalRect workArea,
        IEnumerable<WorkspaceApplication> applications,
        string? id = null,
        DateTimeOffset? createdAt = null)
    {
        ValidateLayout(layout);
        var applicationList = applications.ToList();
        NormalizeApplications(applicationList);
        ValidateApplications(layout, applicationList);

        return new WorkspaceDefinition
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
            Name = string.IsNullOrWhiteSpace(name) ? "未命名工作区" : name.Trim(),
            Layout = layout,
            CapturedWorkArea = workArea,
            Applications = applicationList,
            LegacyWindows = null,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    // 保留给现有 CLI/旧调用方的兼容重载。
    public static WorkspaceDefinition Create(
        string name,
        SnapLayoutDefinition layout,
        PhysicalRect workArea,
        IEnumerable<WorkspaceWindowSlot> slots,
        string? id = null,
        DateTimeOffset? createdAt = null) =>
        Create(
            name,
            layout,
            workArea,
            slots.Where(slot => !slot.IsEmpty).Select(slot => new WorkspaceApplication
            {
                DisplayName = slot.Identity!.ExactTitle,
                Kind = WorkspaceApplicationKind.Window,
                ZoneId = slot.ZoneId,
                WindowIdentity = slot.Identity,
                Launch = LaunchFromIdentity(slot.Identity)
            }),
            id,
            createdAt);

    public static WorkspaceApplication CreateWindowApplication(
        WindowSnapshot snapshot,
        string zoneId) => new()
    {
        DisplayName = snapshot.Title,
        Kind = WorkspaceApplicationKind.Window,
        ZoneId = zoneId,
        WindowIdentity = IdentityFrom(snapshot),
        Match = new WindowMatchSpec(),
        Launch = LaunchFrom(snapshot)
    };

    public static WorkspaceApplication CreateCompatibilityApplication(
        WindowSnapshot snapshot,
        PhysicalRect workArea)
    {
        var captureBounds = NativeInterop.GetPreferredCaptureBounds(
            snapshot.Hwnd,
            snapshot.VisibleBounds,
            workArea);
        if (!NativeInterop.IsUsableCaptureBounds(captureBounds, workArea))
        {
            throw new InvalidOperationException(
                $"“{snapshot.Title}”当前只提供 {snapshot.VisibleBounds.Width}×{snapshot.VisibleBounds.Height} 的异常窗口区域。" +
                "请先把窗口恢复到正常大小，再用“手动点选窗口”重新捕捉。");
        }

        return new WorkspaceApplication
        {
            DisplayName = snapshot.Title,
            Kind = WorkspaceApplicationKind.Window,
            WindowPlacement = WorkspaceWindowPlacementMode.CompatibilityPositioned,
            ZoneId = null,
            CompatibilityBounds = NormalizeToWorkArea(captureBounds, workArea),
            WindowIdentity = IdentityFrom(snapshot),
            Match = new WindowMatchSpec(),
            Launch = LaunchFrom(snapshot)
        };
    }

    public static WorkspaceMatchResult MatchExistingWindows(
        WorkspaceDefinition workspace,
        IReadOnlyList<WindowSnapshot> candidates)
    {
        ValidateWorkspace(workspace);
        var available = candidates.ToList();
        var assignments = new List<SnapAssignment>();
        var compatibilityAssignments = new List<CompatibilityWindowAssignment>();
        var missing = new List<WorkspaceApplication>();

        foreach (var application in workspace.Applications.Where(application => application.IsWindow))
        {
            var best = available
                .Select(window => new
                {
                    Window = window,
                    Evaluation = EvaluateMatch(application, window)
                })
                .Where(item => item.Evaluation.IsMatch)
                .OrderByDescending(item => item.Evaluation.Score)
                .ThenBy(item => item.Window.ProcessId)
                .FirstOrDefault();
            if (best is null)
            {
                missing.Add(application);
                continue;
            }

            if (application.IsNativeSnapWindow)
            {
                assignments.Add(new SnapAssignment(best.Window.Hwnd, application.ZoneId!));
            }
            else
            {
                compatibilityAssignments.Add(new CompatibilityWindowAssignment(
                    best.Window.Hwnd,
                    application.Id,
                    application.DisplayName,
                    application.CompatibilityBounds!));
            }
            available.Remove(best.Window);
        }

        return new WorkspaceMatchResult(assignments, compatibilityAssignments, missing);
    }

    public static WorkspaceDefinition Load(string path)
    {
        var workspace = JsonSerializer.Deserialize<WorkspaceDefinition>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("工作区 JSON 为空。");
        NormalizeWorkspace(workspace);
        ValidateWorkspace(workspace);
        return workspace;
    }

    public static void Save(string path, WorkspaceDefinition workspace)
    {
        NormalizeWorkspace(workspace);
        ValidateWorkspace(workspace);
        workspace.UpdatedAt = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(workspace, JsonOptions));
    }

    public static WindowIdentity IdentityFrom(WindowSnapshot window) => new()
    {
        ProcessPath = window.ProcessPath,
        AppUserModelId = window.AppUserModelId,
        ClassName = window.ClassName,
        ExactTitle = window.Title
    };

    public static ApplicationLaunchSpec LaunchFrom(WindowSnapshot window) =>
        IsWindowsSettingsWindow(window.ProcessPath, window.ClassName, window.Title)
            ? CreateWindowsSettingsLaunch()
            : new ApplicationLaunchSpec
            {
                ExecutablePath = window.ProcessPath,
                AppUserModelId = window.AppUserModelId,
                WorkingDirectory = string.IsNullOrWhiteSpace(window.ProcessPath)
                    ? string.Empty
                    : Path.GetDirectoryName(window.ProcessPath) ?? string.Empty
            };

    public static int GetMatchScore(WindowIdentity identity, WindowSnapshot window) =>
        MatchScore(identity, window);

    public static NormalizedRect NormalizeToWorkArea(PhysicalRect bounds, PhysicalRect workArea)
    {
        var width = Math.Clamp(bounds.Width, 1, workArea.Width);
        var height = Math.Clamp(bounds.Height, 1, workArea.Height);
        var left = Math.Clamp(bounds.X, workArea.X, workArea.Right - width);
        var top = Math.Clamp(bounds.Y, workArea.Y, workArea.Bottom - height);
        return new NormalizedRect
        {
            X = (double)(left - workArea.X) / workArea.Width,
            Y = (double)(top - workArea.Y) / workArea.Height,
            Width = (double)width / workArea.Width,
            Height = (double)height / workArea.Height
        };
    }

    internal static NormalizedRect RepairCompatibilityBounds(
        NormalizedRect storedBounds,
        nint hwnd,
        PhysicalRect capturedWorkArea)
    {
        var storedPhysical = storedBounds.ToPhysical(capturedWorkArea);
        if (NativeInterop.IsUsableCaptureBounds(storedPhysical, capturedWorkArea))
        {
            return storedBounds;
        }

        var visibleBounds = NativeInterop.GetVisibleWindowBounds(hwnd);
        var liveBounds = NativeInterop.GetPreferredCaptureBounds(hwnd, visibleBounds, capturedWorkArea);
        if (NativeInterop.IsUsableCaptureBounds(liveBounds, capturedWorkArea))
        {
            return NormalizeToWorkArea(liveBounds, capturedWorkArea);
        }

        // 旧版本可能已经把越界窗口压缩成 1×1。若应用也不给出有效的
        // WINDOWPLACEMENT，至少使用一个可见、可再次编辑的安全区域。
        return new NormalizedRect { X = 0.15, Y = 0.12, Width = 0.7, Height = 0.72 };
    }

    public static WindowMatchEvaluation EvaluateMatch(
        WorkspaceApplication application,
        WindowSnapshot window)
    {
        if (application.WindowIdentity is null)
        {
            return new WindowMatchEvaluation(false, 0, "缺少窗口身份信息。");
        }

        return EvaluateMatch(application.WindowIdentity, application.Match, window);
    }

    public static WindowMatchEvaluation EvaluateMatch(
        WindowIdentity identity,
        WindowMatchSpec? match,
        WindowSnapshot window)
    {
        match ??= new WindowMatchSpec();
        var score = 0;
        var reasons = new List<string>();

        var sameAumid = !string.IsNullOrWhiteSpace(identity.AppUserModelId) &&
                        string.Equals(identity.AppUserModelId, window.AppUserModelId, StringComparison.OrdinalIgnoreCase);
        var samePath = !string.IsNullOrWhiteSpace(identity.ProcessPath) &&
                       string.Equals(identity.ProcessPath, window.ProcessPath, StringComparison.OrdinalIgnoreCase);
        var sameFileName = !string.IsNullOrWhiteSpace(identity.ProcessPath) &&
                           !string.IsNullOrWhiteSpace(window.ProcessPath) &&
                           string.Equals(
                               Path.GetFileName(identity.ProcessPath),
                               Path.GetFileName(window.ProcessPath),
                               StringComparison.OrdinalIgnoreCase);

        switch (match.ProcessMode)
        {
            case WindowProcessMatchMode.ExactPath when !samePath && !sameAumid:
                return new WindowMatchEvaluation(false, 0, "可执行文件路径或 AUMID 不一致。");
            case WindowProcessMatchMode.FileName when !sameFileName && !sameAumid:
                return new WindowMatchEvaluation(false, 0, "可执行文件名或 AUMID 不一致。");
            case WindowProcessMatchMode.ExactPath:
                score += sameAumid ? 10 : 8;
                reasons.Add(sameAumid ? "AUMID 相同" : "路径相同");
                break;
            case WindowProcessMatchMode.FileName:
                score += sameAumid ? 10 : 6;
                reasons.Add(sameAumid ? "AUMID 相同" : "文件名相同");
                break;
            case WindowProcessMatchMode.Automatic:
                if (sameAumid)
                {
                    score += 8;
                    reasons.Add("AUMID 相同");
                }
                if (samePath)
                {
                    score += 6;
                    reasons.Add("路径相同");
                }
                break;
        }

        var sameClass = string.Equals(identity.ClassName, window.ClassName, StringComparison.Ordinal);
        if (match.RequireSameWindowClass && !sameClass)
        {
            return new WindowMatchEvaluation(false, score, "窗口类名不一致。");
        }
        if (sameClass)
        {
            score += 4;
            reasons.Add("类名相同");
        }

        var pattern = string.IsNullOrWhiteSpace(match.TitlePattern)
            ? identity.ExactTitle
            : match.TitlePattern;
        try
        {
            switch (match.TitleMode)
            {
                case WindowTitleMatchMode.Exact when !string.Equals(pattern, window.Title, StringComparison.OrdinalIgnoreCase):
                    return new WindowMatchEvaluation(false, score, "窗口标题不完全相同。");
                case WindowTitleMatchMode.Exact:
                    score += 8;
                    reasons.Add("标题完全匹配");
                    break;
                case WindowTitleMatchMode.Contains when string.IsNullOrWhiteSpace(pattern) ||
                                                        !window.Title.Contains(pattern, StringComparison.OrdinalIgnoreCase):
                    return new WindowMatchEvaluation(false, score, "窗口标题不包含指定文字。");
                case WindowTitleMatchMode.Contains:
                    score += 7;
                    reasons.Add("标题包含指定文字");
                    break;
                case WindowTitleMatchMode.Wildcard when string.IsNullOrWhiteSpace(pattern) ||
                                                        !Regex.IsMatch(window.Title, WildcardToRegex(pattern), RegexOptions.IgnoreCase):
                    return new WindowMatchEvaluation(false, score, "窗口标题不符合通配符。");
                case WindowTitleMatchMode.Wildcard:
                    score += 7;
                    reasons.Add("标题符合通配符");
                    break;
                case WindowTitleMatchMode.RegularExpression when string.IsNullOrWhiteSpace(pattern) ||
                                                                 !Regex.IsMatch(window.Title, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)):
                    return new WindowMatchEvaluation(false, score, "窗口标题不符合正则表达式。");
                case WindowTitleMatchMode.RegularExpression:
                    score += 7;
                    reasons.Add("标题符合正则表达式");
                    break;
                case WindowTitleMatchMode.Automatic:
                    if (string.Equals(identity.ExactTitle, window.Title, StringComparison.Ordinal))
                    {
                        score += 4;
                        reasons.Add("标题相同");
                    }
                    else if (!string.IsNullOrWhiteSpace(identity.ExactTitle) &&
                             (window.Title.Contains(identity.ExactTitle, StringComparison.OrdinalIgnoreCase) ||
                              identity.ExactTitle.Contains(window.Title, StringComparison.OrdinalIgnoreCase)))
                    {
                        score += 2;
                        reasons.Add("标题近似");
                    }
                    break;
            }
        }
        catch (ArgumentException exception)
        {
            return new WindowMatchEvaluation(false, score, $"标题规则无效：{exception.Message}");
        }
        catch (RegexMatchTimeoutException)
        {
            return new WindowMatchEvaluation(false, score, "标题正则表达式执行超时。");
        }

        var usesOnlyAutomaticRules =
            match.ProcessMode == WindowProcessMatchMode.Automatic &&
            match.TitleMode == WindowTitleMatchMode.Automatic &&
            !match.RequireSameWindowClass;
        var minimumScore = usesOnlyAutomaticRules ? 7 : 4;
        return new WindowMatchEvaluation(
            score >= minimumScore,
            score,
            reasons.Count == 0 ? "没有足够的身份字段匹配。" : string.Join("、", reasons));
    }

    public static void Validate(WorkspaceDefinition workspace) => ValidateWorkspace(workspace);

    private static void NormalizeWorkspace(WorkspaceDefinition workspace)
    {
        var sourceSchemaVersion = workspace.SchemaVersion;
        workspace.Id = string.IsNullOrWhiteSpace(workspace.Id)
            ? Guid.NewGuid().ToString("N")
            : workspace.Id;
        workspace.Applications ??= [];
        if (workspace.Applications.Count == 0 && workspace.LegacyWindows is not null)
        {
            foreach (var slot in workspace.LegacyWindows.Where(slot => !slot.IsEmpty))
            {
                workspace.Applications.Add(new WorkspaceApplication
                {
                    DisplayName = slot.Identity!.ExactTitle,
                    Kind = WorkspaceApplicationKind.Window,
                    ZoneId = slot.ZoneId,
                    WindowIdentity = slot.Identity,
                    Launch = LaunchFromIdentity(slot.Identity)
                });
            }
        }

        NormalizeApplications(workspace.Applications);
        if (sourceSchemaVersion <= 4 &&
            (workspace.Layout.Id.StartsWith("captured-", StringComparison.OrdinalIgnoreCase) ||
             workspace.Layout.DisplayName.StartsWith("自定义捕捉", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var application in workspace.Applications.Where(application => application.IsNativeSnapWindow))
            {
                var zone = workspace.Layout.Zones.FirstOrDefault(item =>
                    string.Equals(item.Id, application.ZoneId, StringComparison.OrdinalIgnoreCase));
                if (zone is null) continue;
                application.WindowPlacement = WorkspaceWindowPlacementMode.CompatibilityPositioned;
                application.CompatibilityBounds = new NormalizedRect
                {
                    X = zone.Rect.X,
                    Y = zone.Rect.Y,
                    Width = zone.Rect.Width,
                    Height = zone.Rect.Height
                };
                application.ZoneId = null;
            }

            workspace.Layout = BuiltInLayouts.Get("halves");
        }
        workspace.LegacyWindows = null;
        workspace.SchemaVersion = 5;
    }

    private static void NormalizeApplications(List<WorkspaceApplication> applications)
    {
        foreach (var application in applications)
        {
            application.Id = string.IsNullOrWhiteSpace(application.Id)
                ? Guid.NewGuid().ToString("N")
                : application.Id;
            application.Launch ??= new ApplicationLaunchSpec();
            application.Match ??= new WindowMatchSpec();
            if (application.WindowIdentity is not null &&
                IsWindowsSettingsWindow(
                    application.WindowIdentity.ProcessPath,
                    application.WindowIdentity.ClassName,
                    application.WindowIdentity.ExactTitle))
            {
                // ApplicationFrameHost.exe is only the UWP surface host. Launching it
                // directly creates no Settings window, so migrate captured definitions
                // to the canonical Settings URI while retaining the original identity
                // for matching the resulting ApplicationFrameWindow.
                application.Launch = CreateWindowsSettingsLaunch();
            }
            if (application.Kind == WorkspaceApplicationKind.Background)
            {
                // Schema v5 早期版本允许应用自行决定是否显示窗口。这与“后台启动”的
                // 产品语义冲突；读取旧工作区时统一迁移为启动阶段强制隐藏。
                application.BackgroundWindowMode = BackgroundWindowMode.Hide;
            }
            if (string.IsNullOrWhiteSpace(application.Launch.ExecutablePath))
            {
                application.Launch.ExecutablePath = application.WindowIdentity?.ProcessPath;
            }

            if (string.IsNullOrWhiteSpace(application.Launch.AppUserModelId))
            {
                application.Launch.AppUserModelId = application.WindowIdentity?.AppUserModelId;
            }

            application.DisplayName = string.IsNullOrWhiteSpace(application.DisplayName)
                ? application.WindowIdentity?.ExactTitle ??
                  Path.GetFileNameWithoutExtension(application.Launch.ExecutablePath) ??
                  "应用"
                : application.DisplayName.Trim();
        }
    }

    private static ApplicationLaunchSpec LaunchFromIdentity(WindowIdentity identity) =>
        IsWindowsSettingsWindow(identity.ProcessPath, identity.ClassName, identity.ExactTitle)
            ? CreateWindowsSettingsLaunch()
            : new ApplicationLaunchSpec
            {
                ExecutablePath = identity.ProcessPath,
                AppUserModelId = identity.AppUserModelId,
                WorkingDirectory = string.IsNullOrWhiteSpace(identity.ProcessPath)
                    ? string.Empty
                    : Path.GetDirectoryName(identity.ProcessPath) ?? string.Empty
            };

    private static bool IsWindowsSettingsWindow(string? processPath, string className, string title) =>
        string.Equals(Path.GetFileName(processPath), "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(className, "ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(title, "设置", StringComparison.CurrentCultureIgnoreCase) ||
         string.Equals(title, "Settings", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(title, "Windows 设置", StringComparison.CurrentCultureIgnoreCase));

    private static ApplicationLaunchSpec CreateWindowsSettingsLaunch()
    {
        return new ApplicationLaunchSpec
        {
            LaunchUri = "ms-settings:"
        };
    }

    private static void ValidateWorkspace(WorkspaceDefinition workspace)
    {
        ValidateLayout(workspace.Layout);
        ValidateApplications(workspace.Layout, workspace.Applications);
    }

    private static void ValidateLayout(SnapLayoutDefinition layout)
    {
        var errors = layout.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }
    }

    private static void ValidateApplications(
        SnapLayoutDefinition layout,
        IReadOnlyList<WorkspaceApplication> applications)
    {
        if (applications.Count == 0)
        {
            throw new InvalidDataException("工作区至少需要一个窗口应用或后台应用。");
        }

        var duplicateApplication = applications
            .GroupBy(application => application.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateApplication is not null)
        {
            throw new InvalidDataException($"应用 Id 重复：{duplicateApplication.Key}。");
        }

        var zoneIds = layout.Zones.Select(zone => zone.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var windowApplications = applications.Where(application => application.IsWindow).ToList();
        var nativeSnapApplications = windowApplications.Where(application => application.IsNativeSnapWindow).ToList();
        var duplicateZone = nativeSnapApplications
            .GroupBy(application => application.ZoneId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateZone is not null)
        {
            throw new InvalidDataException($"同一个 Zone 只能分配一个窗口应用：{duplicateZone.Key}。");
        }

        foreach (var application in windowApplications)
        {
            if (application.IsNativeSnapWindow &&
                (string.IsNullOrWhiteSpace(application.ZoneId) || !zoneIds.Contains(application.ZoneId)))
            {
                throw new InvalidDataException($"窗口应用“{application.DisplayName}”的 Zone 无效。");
            }

            if (application.IsCompatibilityWindow)
            {
                if (!string.IsNullOrWhiteSpace(application.ZoneId))
                {
                    throw new InvalidDataException($"兼容定位窗口“{application.DisplayName}”不能绑定 Snap Zone。");
                }

                ValidateNormalizedBounds(application);
            }

            if (application.WindowIdentity is null)
            {
                throw new InvalidDataException($"窗口应用“{application.DisplayName}”缺少窗口身份信息。");
            }

            ValidateMatchSpec(application);
        }

        foreach (var application in applications.Where(application =>
                     application.Kind == WorkspaceApplicationKind.Background))
        {
            if (!string.IsNullOrWhiteSpace(application.ZoneId))
            {
                throw new InvalidDataException($"后台应用“{application.DisplayName}”不能绑定 Zone。");
            }

            if (!application.Launch.CanLaunch)
            {
                throw new InvalidDataException($"后台应用“{application.DisplayName}”缺少可执行文件或 AUMID。");
            }
        }
    }

    private static void ValidateNormalizedBounds(WorkspaceApplication application)
    {
        var bounds = application.CompatibilityBounds
            ?? throw new InvalidDataException($"兼容定位窗口“{application.DisplayName}”缺少保存位置。");
        const double epsilon = 0.000001;
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
            bounds.Width <= epsilon || bounds.Height <= epsilon ||
            bounds.X < -epsilon || bounds.Y < -epsilon ||
            bounds.X + bounds.Width > 1 + epsilon ||
            bounds.Y + bounds.Height > 1 + epsilon)
        {
            throw new InvalidDataException($"兼容定位窗口“{application.DisplayName}”的保存位置无效。");
        }
    }

    private static void ValidateMatchSpec(WorkspaceApplication application)
    {
        var match = application.Match ?? new WindowMatchSpec();
        var identity = application.WindowIdentity!;
        if (match.ProcessMode == WindowProcessMatchMode.Ignore &&
            match.TitleMode == WindowTitleMatchMode.Ignore &&
            !match.RequireSameWindowClass)
        {
            throw new InvalidDataException(
                $"窗口应用“{application.DisplayName}”不能同时忽略进程、标题和窗口类。");
        }

        if (match.ProcessMode is WindowProcessMatchMode.ExactPath or WindowProcessMatchMode.FileName &&
            string.IsNullOrWhiteSpace(identity.ProcessPath) &&
            string.IsNullOrWhiteSpace(identity.AppUserModelId))
        {
            throw new InvalidDataException(
                $"窗口应用“{application.DisplayName}”启用了进程匹配，但没有可用路径或 AUMID。");
        }

        if (match.TitleMode is WindowTitleMatchMode.Exact or
                               WindowTitleMatchMode.Contains or
                               WindowTitleMatchMode.Wildcard or
                               WindowTitleMatchMode.RegularExpression &&
            string.IsNullOrWhiteSpace(match.TitlePattern) &&
            string.IsNullOrWhiteSpace(identity.ExactTitle))
        {
            throw new InvalidDataException(
                $"窗口应用“{application.DisplayName}”启用了标题规则，但匹配文字为空。");
        }

        if (match.TitleMode == WindowTitleMatchMode.RegularExpression)
        {
            var pattern = string.IsNullOrWhiteSpace(match.TitlePattern)
                ? identity.ExactTitle
                : match.TitlePattern;
            try
            {
                _ = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    $"窗口应用“{application.DisplayName}”的标题正则表达式无效：{exception.Message}");
            }
        }
    }

    private static int MatchScore(WindowIdentity identity, WindowSnapshot window)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(identity.AppUserModelId) &&
            string.Equals(identity.AppUserModelId, window.AppUserModelId, StringComparison.OrdinalIgnoreCase))
        {
            score += 8;
        }

        if (!string.IsNullOrWhiteSpace(identity.ProcessPath) &&
            string.Equals(identity.ProcessPath, window.ProcessPath, StringComparison.OrdinalIgnoreCase))
        {
            score += 6;
        }

        if (string.Equals(identity.ClassName, window.ClassName, StringComparison.Ordinal))
        {
            score += 4;
        }

        if (string.Equals(identity.ExactTitle, window.Title, StringComparison.Ordinal))
        {
            score += 4;
        }
        else if (window.Title.Contains(identity.ExactTitle, StringComparison.OrdinalIgnoreCase) ||
                 identity.ExactTitle.Contains(window.Title, StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        return score;
    }

    private static string WildcardToRegex(string pattern) =>
        "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";

    private static double Coverage(PhysicalRect zone, PhysicalRect window)
    {
        var left = Math.Max(zone.X, window.X);
        var top = Math.Max(zone.Y, window.Y);
        var right = Math.Min(zone.Right, window.Right);
        var bottom = Math.Min(zone.Bottom, window.Bottom);
        if (right <= left || bottom <= top)
        {
            return 0;
        }

        var intersection = (long)(right - left) * (bottom - top);
        var zoneArea = (long)zone.Width * zone.Height;
        return zoneArea == 0 ? 0 : (double)intersection / zoneArea;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
