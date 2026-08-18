using System.Diagnostics;

namespace SnapWorkspace.Route3;

public sealed record WindowSnapshot(
    nint Hwnd,
    uint ProcessId,
    string? ProcessPath,
    string ClassName,
    string Title,
    PhysicalRect VisibleBounds,
    string? AppUserModelId = null);

public static class WindowCatalog
{
    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow"
    };

    public static IReadOnlyList<WindowSnapshot> EnumerateCandidates()
    {
        var snapshots = new List<WindowSnapshot>();
        foreach (var hwnd in NativeInterop.EnumerateTopLevelWindows())
        {
            if (!NativeInterop.IsCandidateTopLevelWindow(hwnd))
            {
                continue;
            }

            var title = NativeInterop.GetWindowTitle(hwnd);
            var className = NativeInterop.GetWindowClassName(hwnd);
            if (string.IsNullOrWhiteSpace(title) ||
                string.IsNullOrWhiteSpace(className) ||
                ExcludedClasses.Contains(className))
            {
                continue;
            }

            var processId = NativeInterop.GetWindowProcessId(hwnd);
            string? processPath = null;
            try
            {
                using var process = Process.GetProcessById(checked((int)processId));
                processPath = process.MainModule?.FileName;
            }
            catch
            {
                // 高完整性进程可能禁止读取路径；类名和标题仍可用于低置信度匹配。
            }

            try
            {
                snapshots.Add(new WindowSnapshot(
                    hwnd,
                    processId,
                    processPath,
                    className,
                    title,
                    NativeInterop.GetVisibleWindowBounds(hwnd),
                    NativeInterop.TryGetApplicationUserModelId(processId)));
            }
            catch
            {
                // 窗口可能在枚举过程中被关闭。
            }
        }

        return snapshots;
    }

    public static WindowSnapshot? CaptureWindow(nint hwnd, bool allowUntitled = true)
    {
        if (!NativeInterop.IsCandidateTopLevelWindow(hwnd))
        {
            return null;
        }

        var className = NativeInterop.GetWindowClassName(hwnd);
        if (string.IsNullOrWhiteSpace(className) || ExcludedClasses.Contains(className))
        {
            return null;
        }

        var processId = NativeInterop.GetWindowProcessId(hwnd);
        string? processPath = null;
        string? processDisplayName = null;
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            processPath = process.MainModule?.FileName;
            processDisplayName = process.MainModule?.FileVersionInfo.FileDescription;
            if (string.IsNullOrWhiteSpace(processDisplayName))
            {
                processDisplayName = process.ProcessName;
            }
        }
        catch
        {
            // 高完整性窗口仍可通过窗口类和 AUMID 进行低置信度捕捉。
        }

        var title = NativeInterop.GetWindowTitle(hwnd);
        if (string.IsNullOrWhiteSpace(title))
        {
            if (!allowUntitled)
            {
                return null;
            }

            title = !string.IsNullOrWhiteSpace(processDisplayName)
                ? processDisplayName
                : Path.GetFileNameWithoutExtension(processPath) ?? className;
        }

        try
        {
            var workArea = NativeInterop.GetPrimaryWorkArea();
            var visibleBounds = NativeInterop.GetVisibleWindowBounds(hwnd);
            return new WindowSnapshot(
                hwnd,
                processId,
                processPath,
                className,
                title,
                NativeInterop.GetPreferredCaptureBounds(hwnd, visibleBounds, workArea),
                NativeInterop.TryGetApplicationUserModelId(processId));
        }
        catch
        {
            return null;
        }
    }

    public static bool IsLikelySnapEligible(WindowSnapshot window) =>
        NativeInterop.IsLikelySnapEligibleWindow(window.Hwnd);
}
