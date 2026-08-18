using System.Diagnostics;

namespace SnapWorkspace.Route3;

public enum RunningApplicationSurface
{
    Tray,
    Background
}

public sealed record RunningApplicationSnapshot(
    uint ProcessId,
    string DisplayName,
    string? ExecutablePath,
    string? AppUserModelId,
    RunningApplicationSurface Surface = RunningApplicationSurface.Background)
{
    public ApplicationLaunchSpec ToLaunchSpec() => new()
    {
        ExecutablePath = ExecutablePath,
        AppUserModelId = AppUserModelId,
        WorkingDirectory = string.IsNullOrWhiteSpace(ExecutablePath)
            ? string.Empty
            : Path.GetDirectoryName(ExecutablePath) ?? string.Empty
    };
}

public static class RunningApplicationCatalog
{
    public static IReadOnlyList<RunningApplicationSnapshot> EnumerateBackgroundCandidates(
        IReadOnlyList<WindowSnapshot> visibleWindows,
        IReadOnlyList<RunningApplicationSnapshot>? trayApplications = null)
    {
        var currentSession = Process.GetCurrentProcess().SessionId;
        var trayLaunchTargets = (trayApplications ?? TrayIconCatalog.EnumerateRunningApplications())
            .Select(item => item.ExecutablePath ?? item.AppUserModelId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visiblePaths = visibleWindows
            .Select(window => window.ProcessPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visibleAumids = visibleWindows
            .Select(window => window.AppUserModelId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var windowsDirectory = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidates = new List<RunningApplicationSnapshot>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                try
                {
                    if (process.SessionId != currentSession)
                    {
                        continue;
                    }

                    var executablePath = process.MainModule?.FileName;
                    var aumid = NativeInterop.TryGetApplicationUserModelId((uint)process.Id);
                    if (string.IsNullOrWhiteSpace(executablePath) && string.IsNullOrWhiteSpace(aumid))
                    {
                        continue;
                    }

                    if ((!string.IsNullOrWhiteSpace(executablePath) && visiblePaths.Contains(executablePath)) ||
                        (!string.IsNullOrWhiteSpace(aumid) && visibleAumids.Contains(aumid)) ||
                        (!string.IsNullOrWhiteSpace(executablePath) && trayLaunchTargets.Contains(executablePath)) ||
                        (!string.IsNullOrWhiteSpace(aumid) && trayLaunchTargets.Contains(aumid)))
                    {
                        continue;
                    }

                    // 系统会话组件不是用户想恢复的工作区应用。带窗口的 Explorer 等已在 visibleWindows 中保留。
                    if (!string.IsNullOrWhiteSpace(executablePath) &&
                        Path.GetFullPath(executablePath).StartsWith(windowsDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var description = process.MainModule?.FileVersionInfo.FileDescription;
                    var displayName = string.IsNullOrWhiteSpace(description)
                        ? Path.GetFileNameWithoutExtension(executablePath) ?? process.ProcessName
                        : description.Trim();
                    candidates.Add(new RunningApplicationSnapshot(
                        (uint)process.Id,
                        displayName,
                        executablePath,
                        aumid,
                        RunningApplicationSurface.Background));
                }
                catch
                {
                    // 已退出或跨完整性级别的进程无法可靠恢复，不加入自动捕捉清单。
                }
            }
        }

        return candidates
            .GroupBy(
                candidate => candidate.ExecutablePath ?? candidate.AppUserModelId!,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(candidate => candidate.ProcessId)
                .First())
            .OrderBy(candidate => candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
