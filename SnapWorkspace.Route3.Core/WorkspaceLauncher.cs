using System.ComponentModel;
using System.Diagnostics;

namespace SnapWorkspace.Route3;

public enum ApplicationStartStatus
{
    AlreadyAvailable,
    Started,
    Failed,
    NotLaunchable
}

public sealed record ApplicationStartOutcome(
    string ApplicationId,
    string DisplayName,
    WorkspaceApplicationKind Kind,
    ApplicationStartStatus Status,
    string Message,
    int? MatchScore = null,
    string? MatchReason = null);

public sealed record WorkspacePreparationResult(
    IReadOnlyList<SnapAssignment> Assignments,
    IReadOnlyList<CompatibilityWindowAssignment> CompatibilityAssignments,
    IReadOnlyList<WorkspaceApplication> MissingWindows,
    IReadOnlyList<ApplicationStartOutcome> Outcomes)
{
    public int FailedBackgroundApplications => Outcomes.Count(outcome =>
        outcome.Kind == WorkspaceApplicationKind.Background &&
        outcome.Status is ApplicationStartStatus.Failed or ApplicationStartStatus.NotLaunchable);
}

public static class WorkspaceLauncher
{
    public static void ReleaseBackgroundGuard(WorkspaceDefinition workspace) =>
        BackgroundWindowGuard.Disarm(workspace.Applications
            .Where(application => application.Kind == WorkspaceApplicationKind.Background)
            .Select(application => application.Id));

    public static async Task<WorkspacePreparationResult> PrepareAsync(
        WorkspaceDefinition workspace,
        TimeSpan? windowTimeout = null,
        CancellationToken cancellationToken = default)
    {
        WorkspaceService.Validate(workspace);
        var timeout = windowTimeout ?? TimeSpan.FromSeconds(15);
        var outcomes = new List<ApplicationStartOutcome>();
        var assignments = new List<SnapAssignment>();
        var compatibilityAssignments = new List<CompatibilityWindowAssignment>();
        var pending = new List<PendingWindow>();
        var matchedApplicationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedHandles = new HashSet<nint>();

        var backgroundApplications = workspace.Applications.Where(application =>
            application.Kind == WorkspaceApplicationKind.Background).ToList();
        foreach (var application in backgroundApplications)
        {
            // AppsFolder can expose traditional desktop applications as AUMID-only
            // entries. Resolve their real executable before arming the guard so a
            // visible window cannot escape process identity matching.
            application.Launch = ShellAppResolver.Enrich(application.Launch);
        }
        var backgroundPlans = backgroundApplications.ToDictionary(
            application => application.Id,
            application => CreateBackgroundLaunchPlan(application.Launch),
            StringComparer.OrdinalIgnoreCase);
        _ = BackgroundWindowGuard.Arm(backgroundApplications
            .Where(application => backgroundPlans[application.Id].UseWindowGuard)
            .ToList());
        foreach (var application in backgroundApplications)
        {
            if (string.IsNullOrWhiteSpace(application.Launch.ExecutablePath) &&
                application.WindowIdentity is null)
            {
                outcomes.Add(new ApplicationStartOutcome(
                    application.Id,
                    application.DisplayName,
                    application.Kind,
                    ApplicationStartStatus.NotLaunchable,
                    "严格后台启动需要可解析的 exe 或已捕捉窗口身份；当前 AUMID 无法解析，因此未启动桌面阶段。"));
                continue;
            }

            var launchPlan = backgroundPlans[application.Id];
            if (launchPlan.Prepare is not null)
            {
                _ = TryStart(launchPlan.Prepare, suppressWindow: true);
                await Task.Delay(250, cancellationToken);
            }
            var runningProcessIds = application.Launch.AlwaysStartNewInstance
                ? []
                : FindRunningProcessIds(launchPlan.Launch);
            if (runningProcessIds.Count > 0 && !launchPlan.AlwaysIssueLaunchRequest)
            {
                if (launchPlan.UseWindowGuard)
                {
                    BackgroundWindowGuard.TrackProcesses(application.Id, runningProcessIds);
                }
                outcomes.Add(new ApplicationStartOutcome(
                    application.Id,
                    application.DisplayName,
                    application.Kind,
                    ApplicationStartStatus.AlreadyAvailable,
                    launchPlan.UseWindowGuard
                        ? $"已检测到 {runningProcessIds.Count} 个后台进程；本次恢复事务内将持续拦截并隐藏可见窗口。"
                        : $"已检测到 {runningProcessIds.Count} 个浏览器进程；不修改现有用户窗口，后续仍可正常打开前台窗口。"));
                continue;
            }

            var launch = TryStart(launchPlan.Launch, suppressWindow: true);
            if (launch.Success)
            {
                if (launchPlan.UseWindowGuard)
                {
                    BackgroundWindowGuard.TrackProcesses(application.Id, runningProcessIds);
                    BackgroundWindowGuard.TrackProcess(application.Id, launch.ProcessId);
                }
            }
            outcomes.Add(new ApplicationStartOutcome(
                application.Id,
                application.DisplayName,
                application.Kind,
                launch.Success ? ApplicationStartStatus.Started : ApplicationStartStatus.Failed,
                launch.Success
                    ? launch.Message +
                      (launchPlan.ProtocolName is null ? string.Empty : $" 已应用 {launchPlan.ProtocolName}。") +
                      (launchPlan.UseWindowGuard
                          ? " 已启用本次恢复事务的后台窗口拦截。"
                          : " 未创建或隐藏浏览器主窗口，后续前台启动不受影响。")
                    : launch.Message));
        }

        if (backgroundApplications.Count > 0)
        {
            await Task.Delay(600, cancellationToken);
            foreach (var application in backgroundApplications)
            {
                if (!backgroundPlans[application.Id].UseWindowGuard) continue;
                var visibleWindowCount = BackgroundWindowGuard.EnforceAndCountVisible(application.Id);
                if (visibleWindowCount == 0) continue;
                var outcomeIndex = outcomes.FindIndex(outcome =>
                    string.Equals(outcome.ApplicationId, application.Id, StringComparison.OrdinalIgnoreCase));
                if (outcomeIndex >= 0)
                {
                    outcomes[outcomeIndex] = outcomes[outcomeIndex] with
                    {
                        Status = ApplicationStartStatus.Failed,
                        Message = $"后台启动失败：仍有 {visibleWindowCount} 个可见窗口，已停止把该应用报告为后台成功。" +
                                  "应用可能以更高权限运行，或需要应用专用的后台启动协议。"
                    };
                }
            }
        }

        // Background is a prerequisite stage, not an optional side effect. If it
        // cannot guarantee a windowless state, do not launch or submit desktop
        // windows into Snap while an unexpected foreground window is competing.
        if (outcomes.Any(outcome =>
                outcome.Kind == WorkspaceApplicationKind.Background &&
                outcome.Status is ApplicationStartStatus.Failed or ApplicationStartStatus.NotLaunchable))
        {
            return new WorkspacePreparationResult([], [], [], outcomes);
        }

        var initialWindows = WindowCatalog.EnumerateCandidates();
        var initialHandles = initialWindows.Select(window => window.Hwnd).ToHashSet();
        foreach (var application in workspace.Applications.Where(application => application.IsWindow))
        {
            var existing = application.Launch.AlwaysStartNewInstance
                ? null
                : FindBestWindow(application, initialWindows, usedHandles, _ => true);
            if (existing is not null)
            {
                var usedCompatibilityFallback = AddWindowAssignment(
                    workspace.Layout,
                    workspace.CapturedWorkArea,
                    application,
                    existing.Window.Hwnd,
                    assignments,
                    compatibilityAssignments);
                matchedApplicationIds.Add(application.Id);
                usedHandles.Add(existing.Window.Hwnd);
                outcomes.Add(new ApplicationStartOutcome(
                    application.Id,
                    application.DisplayName,
                    application.Kind,
                    ApplicationStartStatus.AlreadyAvailable,
                    $"已匹配现有窗口：{existing.Window.Title}" +
                    (usedCompatibilityFallback ? "；窗口样式不支持原生 Snap，已按对应 Zone 使用兼容定位。" : string.Empty),
                    existing.Evaluation.Score,
                    existing.Evaluation.Reason));
                continue;
            }

            if (!application.Launch.CanLaunch)
            {
                pending.Add(new PendingWindow(application, initialHandles, false));
                outcomes.Add(new ApplicationStartOutcome(
                    application.Id,
                    application.DisplayName,
                    application.Kind,
                    ApplicationStartStatus.NotLaunchable,
                    "没有可执行文件或 AUMID，无法启动缺失窗口。"));
                continue;
            }

            var launch = TryStart(application.Launch, suppressWindow: false);
            if (!launch.Success)
            {
                pending.Add(new PendingWindow(application, initialHandles, false));
                outcomes.Add(new ApplicationStartOutcome(
                    application.Id,
                    application.DisplayName,
                    application.Kind,
                    ApplicationStartStatus.Failed,
                    launch.Message));
                continue;
            }

            pending.Add(new PendingWindow(
                application,
                initialHandles,
                application.Launch.AlwaysStartNewInstance));
            outcomes.Add(new ApplicationStartOutcome(
                application.Id,
                application.DisplayName,
                application.Kind,
                ApplicationStartStatus.Started,
                launch.Message));
        }

        var launchablePending = pending
            .Where(item => item.Application.Launch.CanLaunch &&
                           outcomes.Any(outcome =>
                               outcome.ApplicationId == item.Application.Id &&
                               outcome.Status == ApplicationStartStatus.Started))
            .ToList();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (launchablePending.Count > 0 && DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(250, cancellationToken);
            var candidates = WindowCatalog.EnumerateCandidates();
            for (var index = launchablePending.Count - 1; index >= 0; index--)
            {
                var item = launchablePending[index];
                var match = FindBestWindow(
                    item.Application,
                    candidates,
                    usedHandles,
                    window => !item.RequireNewHandle || !item.InitialHandles.Contains(window.Hwnd));
                if (match is null)
                {
                    continue;
                }

                var usedCompatibilityFallback = AddWindowAssignment(
                    workspace.Layout,
                    workspace.CapturedWorkArea,
                    item.Application,
                    match.Window.Hwnd,
                    assignments,
                    compatibilityAssignments);
                matchedApplicationIds.Add(item.Application.Id);
                usedHandles.Add(match.Window.Hwnd);
                launchablePending.RemoveAt(index);

                var outcomeIndex = outcomes.FindIndex(outcome => outcome.ApplicationId == item.Application.Id);
                if (outcomeIndex >= 0)
                {
                    outcomes[outcomeIndex] = outcomes[outcomeIndex] with
                    {
                        Message = $"已启动并匹配窗口：{match.Window.Title}" +
                                  (usedCompatibilityFallback ? "；窗口样式不支持原生 Snap，已按对应 Zone 使用兼容定位。" : string.Empty),
                        MatchScore = match.Evaluation.Score,
                        MatchReason = match.Evaluation.Reason
                    };
                }
            }
        }

        var missing = workspace.Applications
            .Where(application =>
                application.IsWindow &&
                !matchedApplicationIds.Contains(application.Id))
            .ToList();
        foreach (var application in missing)
        {
            var outcomeIndex = outcomes.FindIndex(outcome => outcome.ApplicationId == application.Id);
            if (outcomeIndex >= 0 && outcomes[outcomeIndex].Status == ApplicationStartStatus.Started)
            {
                outcomes[outcomeIndex] = outcomes[outcomeIndex] with
                {
                    Status = ApplicationStartStatus.Failed,
                    Message = $"已发出启动请求，但 {timeout.TotalSeconds:0.#} 秒内没有出现可匹配窗口。"
                };
            }
        }

        return new WorkspacePreparationResult(assignments, compatibilityAssignments, missing, outcomes);
    }

    private static bool AddWindowAssignment(
        SnapLayoutDefinition layout,
        PhysicalRect capturedWorkArea,
        WorkspaceApplication application,
        nint hwnd,
        ICollection<SnapAssignment> snapAssignments,
        ICollection<CompatibilityWindowAssignment> compatibilityAssignments)
    {
        if (application.IsNativeSnapWindow && NativeInterop.IsLikelySnapEligibleWindow(hwnd))
        {
            snapAssignments.Add(new SnapAssignment(hwnd, application.ZoneId!));
            return false;
        }

        if (application.IsNativeSnapWindow)
        {
            var zone = layout.GetZone(application.ZoneId!);
            compatibilityAssignments.Add(new CompatibilityWindowAssignment(
                hwnd,
                application.Id,
                application.DisplayName,
                new NormalizedRect
                {
                    X = zone.Rect.X,
                    Y = zone.Rect.Y,
                    Width = zone.Rect.Width,
                    Height = zone.Rect.Height
                },
                application.ZoneId));
            return true;
        }

        var repairedBounds = WorkspaceService.RepairCompatibilityBounds(
            application.CompatibilityBounds!,
            hwnd,
            capturedWorkArea);
        application.CompatibilityBounds = repairedBounds;
        compatibilityAssignments.Add(new CompatibilityWindowAssignment(
            hwnd,
            application.Id,
            application.DisplayName,
            repairedBounds));
        return false;
    }

    private static MatchedWindow? FindBestWindow(
        WorkspaceApplication application,
        IReadOnlyList<WindowSnapshot> candidates,
        IReadOnlySet<nint> usedHandles,
        Func<WindowSnapshot, bool> predicate) => candidates
        .Where(window => !usedHandles.Contains(window.Hwnd) && predicate(window))
        .Select(window => new
        {
            Window = window,
            Evaluation = WorkspaceService.EvaluateMatch(application, window)
        })
        .Where(item => item.Evaluation.IsMatch)
        .OrderByDescending(item => item.Evaluation.Score)
        .ThenBy(item => item.Window.ProcessId)
        .Select(item => new MatchedWindow(item.Window, item.Evaluation))
        .FirstOrDefault();

    private static (bool Success, string Message, int? ProcessId) TryStart(
        ApplicationLaunchSpec launch,
        bool suppressWindow)
    {
        try
        {
            ProcessStartInfo startInfo;
            if (!string.IsNullOrWhiteSpace(launch.LaunchUri))
            {
                if (launch.RunAsAdministrator)
                {
                    return (false, "URI 应用不支持通过当前启动器请求管理员权限。", null);
                }

                startInfo = new ProcessStartInfo
                {
                    FileName = launch.LaunchUri,
                    UseShellExecute = true,
                    WindowStyle = suppressWindow ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
                };
            }
            else if (!string.IsNullOrWhiteSpace(launch.ExecutablePath))
            {
                var executableDirectory = Path.GetDirectoryName(launch.ExecutablePath) ?? string.Empty;
                var requestedWorkingDirectory = launch.WorkingDirectory;
                startInfo = new ProcessStartInfo
                {
                    FileName = launch.ExecutablePath,
                    Arguments = launch.Arguments,
                    WorkingDirectory = !string.IsNullOrWhiteSpace(requestedWorkingDirectory) &&
                                       Directory.Exists(requestedWorkingDirectory)
                        ? requestedWorkingDirectory
                        : executableDirectory,
                    UseShellExecute = launch.RunAsAdministrator,
                    Verb = launch.RunAsAdministrator ? "runas" : string.Empty,
                    WindowStyle = suppressWindow ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                    CreateNoWindow = suppressWindow && !launch.RunAsAdministrator
                };
            }
            else
            {
                if (launch.RunAsAdministrator)
                {
                    return (false, "AUMID 应用不支持通过当前启动器请求管理员权限。", null);
                }

                startInfo = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"shell:AppsFolder\\{launch.AppUserModelId}",
                    UseShellExecute = true,
                    WindowStyle = suppressWindow ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
                };
            }

            var process = Process.Start(startInfo);
            var directProcessId = string.IsNullOrWhiteSpace(launch.ExecutablePath) ||
                                  !string.IsNullOrWhiteSpace(launch.LaunchUri)
                ? null
                : process?.Id;
            return (
                true,
                launch.AlwaysStartNewInstance ? "已请求启动新实例。" : "已发出启动请求。",
                directProcessId);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return (false, "用户取消了管理员权限请求。", null);
        }
        catch (Exception exception)
        {
            return (false, $"启动失败：{exception.Message}", null);
        }
    }

    private static IReadOnlyList<int> FindRunningProcessIds(ApplicationLaunchSpec launch)
    {
        var processIds = new List<int>();
        var currentSessionId = Process.GetCurrentProcess().SessionId;
        var expectedProcessName = string.IsNullOrWhiteSpace(launch.ExecutablePath)
            ? null
            : Path.GetFileNameWithoutExtension(launch.ExecutablePath);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != currentSessionId) continue;
                    var processPath = NativeInterop.TryGetProcessImagePath((uint)process.Id) ?? process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(launch.ExecutablePath) &&
                        string.Equals(
                            processPath,
                            launch.ExecutablePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        processIds.Add(process.Id);
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(expectedProcessName) &&
                        string.Equals(process.ProcessName, expectedProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        processIds.Add(process.Id);
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(launch.AppUserModelId) &&
                        string.Equals(
                            NativeInterop.TryGetApplicationUserModelId((uint)process.Id),
                            launch.AppUserModelId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        processIds.Add(process.Id);
                    }
                }
                catch
                {
                    // 跨完整性级别进程可能拒绝查询，继续检查其他进程。
                }
            }
        }

        return processIds;
    }

    private static BackgroundLaunchPlan CreateBackgroundLaunchPlan(ApplicationLaunchSpec launch)
    {
        var executableName = Path.GetFileName(launch.ExecutablePath);
        if (IsChromiumBrowser(executableName))
        {
            var chromiumLaunch = launch.Clone();
            if (!ContainsCommandLineSwitch(chromiumLaunch.Arguments, "--no-startup-window"))
            {
                chromiumLaunch.Arguments = string.IsNullOrWhiteSpace(chromiumLaunch.Arguments)
                    ? "--no-startup-window"
                    : $"--no-startup-window {chromiumLaunch.Arguments}";
            }

            return new BackgroundLaunchPlan(
                chromiumLaunch,
                null,
                "Chromium 原生无窗口协议 (--no-startup-window)",
                false,
                false);
        }

        if (!string.Equals(executableName, "Everything.exe", StringComparison.OrdinalIgnoreCase))
        {
            return new BackgroundLaunchPlan(launch, null, null, false, true);
        }

        var prepare = launch.Clone();
        prepare.Arguments = "-close";
        prepare.AlwaysStartNewInstance = true;
        var background = launch.Clone();
        if (!ContainsCommandLineSwitch(background.Arguments, "-startup"))
        {
            background.Arguments = string.IsNullOrWhiteSpace(background.Arguments)
                ? "-startup"
                : $"-startup {background.Arguments}";
        }
        return new BackgroundLaunchPlan(
            background,
            prepare,
            "Everything 原生后台协议 (-close → -startup)",
            true,
            true);
    }

    private static bool ContainsCommandLineSwitch(string arguments, string expectedSwitch) =>
        arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(argument => string.Equals(argument.Trim('"'), expectedSwitch, StringComparison.OrdinalIgnoreCase));

    private static bool IsChromiumBrowser(string? executableName) => executableName is not null &&
        (executableName.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase) ||
         executableName.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase) ||
         executableName.Equals("brave.exe", StringComparison.OrdinalIgnoreCase) ||
         executableName.Equals("vivaldi.exe", StringComparison.OrdinalIgnoreCase) ||
         executableName.Equals("opera.exe", StringComparison.OrdinalIgnoreCase));

    private sealed record PendingWindow(
        WorkspaceApplication Application,
        IReadOnlySet<nint> InitialHandles,
        bool RequireNewHandle);

    private sealed record MatchedWindow(
        WindowSnapshot Window,
        WindowMatchEvaluation Evaluation);

    private sealed record BackgroundLaunchPlan(
        ApplicationLaunchSpec Launch,
        ApplicationLaunchSpec? Prepare,
        string? ProtocolName,
        bool AlwaysIssueLaunchRequest,
        bool UseWindowGuard);

}
