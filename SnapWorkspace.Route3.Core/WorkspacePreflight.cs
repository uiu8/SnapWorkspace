namespace SnapWorkspace.Route3;

public enum WorkspacePreflightSeverity
{
    Information,
    Warning,
    Error
}

public sealed record WorkspacePreflightIssue(
    WorkspacePreflightSeverity Severity,
    string Code,
    string Message,
    string? ApplicationId = null);

public sealed record WorkspacePreflightResult(
    IReadOnlyList<WorkspacePreflightIssue> Issues)
{
    public bool CanRestore => Issues.All(issue => issue.Severity != WorkspacePreflightSeverity.Error);
    public int ErrorCount => Issues.Count(issue => issue.Severity == WorkspacePreflightSeverity.Error);
    public int WarningCount => Issues.Count(issue => issue.Severity == WorkspacePreflightSeverity.Warning);
}

public static class WorkspacePreflight
{
    public static WorkspacePreflightResult Analyze(
        WorkspaceDefinition workspace,
        IReadOnlyList<WindowSnapshot>? currentWindows = null,
        bool? shellSupported = null,
        int maximumSnapWindows = 4)
    {
        var issues = new List<WorkspacePreflightIssue>();
        try
        {
            WorkspaceService.Validate(workspace);
        }
        catch (Exception exception)
        {
            issues.Add(new WorkspacePreflightIssue(
                WorkspacePreflightSeverity.Error,
                "workspace-invalid",
                exception.Message));
            return new WorkspacePreflightResult(issues);
        }

        var windowApplications = workspace.Applications.Where(application => application.IsWindow).ToList();
        var nativeSnapApplications = windowApplications.Where(application => application.IsNativeSnapWindow).ToList();
        if (nativeSnapApplications.Count > 0 && NativeSnapLayoutCatalog.FindExact(workspace.Layout) is null)
        {
            issues.Add(new WorkspacePreflightIssue(
                WorkspacePreflightSeverity.Error,
                "native-layout-unsupported",
                "此工作区的几何区域不是完整、已验证的 Windows Snap Layout 模型。" +
                "直接提交只能保证窗口位置，不能保证仍属于 Snap Group；请在可视化编辑器中转换为原生模型，" +
                "或把相关窗口改为兼容定位。"));
        }
        if (nativeSnapApplications.Count > maximumSnapWindows)
        {
            issues.Add(new WorkspacePreflightIssue(
                WorkspacePreflightSeverity.Error,
                "snap-window-limit",
                $"当前路线三一次最多提交 {maximumSnapWindows} 个窗口，但工作区包含 {nativeSnapApplications.Count} 个原生吸附窗口。"));
        }

        if (shellSupported == false && nativeSnapApplications.Count > 0)
        {
            issues.Add(new WorkspacePreflightIssue(
                WorkspacePreflightSeverity.Error,
                "shell-unavailable",
                "当前 Windows Shell 版本未通过路线三能力检查。"));
        }
        else if (shellSupported is null && nativeSnapApplications.Count > 0)
        {
            issues.Add(new WorkspacePreflightIssue(
                WorkspacePreflightSeverity.Warning,
                "shell-not-probed",
                "Shell 能力仍在检测；恢复时会再次确认。"));
        }

        currentWindows ??= WindowCatalog.EnumerateCandidates();
        foreach (var application in windowApplications)
        {
            var matches = currentWindows
                .Select(window => new
                {
                    Window = window,
                    Evaluation = WorkspaceService.EvaluateMatch(application, window)
                })
                .Where(item => item.Evaluation.IsMatch)
                .OrderByDescending(item => item.Evaluation.Score)
                .ToList();

            if (application.Launch.AlwaysStartNewInstance)
            {
                AddLaunchTargetIssue(issues, application, required: true);
                continue;
            }

            if (matches.Count == 0)
            {
                if (AddLaunchTargetIssue(issues, application, required: true))
                {
                    issues.Add(new WorkspacePreflightIssue(
                        WorkspacePreflightSeverity.Information,
                        "window-will-launch",
                        $"“{application.DisplayName}”当前没有匹配窗口，将尝试启动。",
                        application.Id));
                }
                continue;
            }

            if (matches.Count > 1 && matches[0].Evaluation.Score == matches[1].Evaluation.Score)
            {
                issues.Add(new WorkspacePreflightIssue(
                    WorkspacePreflightSeverity.Warning,
                    "window-match-ambiguous",
                    $"“{application.DisplayName}”有 {matches.Count} 个同分候选窗口，建议添加标题包含、通配符或正则规则。",
                    application.Id));
            }
            else
            {
                issues.Add(new WorkspacePreflightIssue(
                    WorkspacePreflightSeverity.Information,
                    "window-ready",
                    $"“{application.DisplayName}”已找到窗口：{matches[0].Window.Title}。",
                    application.Id));
            }
        }

        foreach (var application in workspace.Applications.Where(application =>
                     application.Kind == WorkspaceApplicationKind.Background))
        {
            AddLaunchTargetIssue(issues, application, required: false);
        }

        if (issues.Count == 0)
        {
            issues.Add(new WorkspacePreflightIssue(
                WorkspacePreflightSeverity.Information,
                "ready",
                "工作区可以恢复。"));
        }

        return new WorkspacePreflightResult(issues);
    }

    private static bool AddLaunchTargetIssue(
        ICollection<WorkspacePreflightIssue> issues,
        WorkspaceApplication application,
        bool required)
    {
        if (!application.Launch.CanLaunch)
        {
            if (required)
            {
                issues.Add(new WorkspacePreflightIssue(
                    WorkspacePreflightSeverity.Error,
                    "launch-target-missing",
                    $"“{application.DisplayName}”当前不可复用，并且没有可执行文件、AUMID 或启动 URI。",
                    application.Id));
            }
            return false;
        }

        if (!string.IsNullOrWhiteSpace(application.Launch.ExecutablePath) &&
            !File.Exists(application.Launch.ExecutablePath) &&
            string.IsNullOrWhiteSpace(application.Launch.AppUserModelId))
        {
            issues.Add(new WorkspacePreflightIssue(
                required ? WorkspacePreflightSeverity.Error : WorkspacePreflightSeverity.Warning,
                "executable-not-found",
                $"“{application.DisplayName}”的可执行文件不存在：{application.Launch.ExecutablePath}",
                application.Id));
            return false;
        }

        if (!string.IsNullOrWhiteSpace(application.Launch.WorkingDirectory) &&
            !Directory.Exists(application.Launch.WorkingDirectory))
        {
            issues.Add(new WorkspacePreflightIssue(
                WorkspacePreflightSeverity.Warning,
                "working-directory-not-found",
                $"“{application.DisplayName}”的工作目录不存在，将由系统决定启动目录。",
                application.Id));
        }

        return true;
    }
}
