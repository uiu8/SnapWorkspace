using System.Diagnostics;
using System.Collections.Concurrent;

namespace SnapWorkspace.Route3;

internal sealed record BackgroundGuardStartResult(
    int ApplicationCount,
    int ImmediatelyHiddenWindowCount,
    TimeSpan ActiveFor);

/// <summary>
/// Enforces the product meaning of a background workspace application during startup:
/// no visible top-level window may remain on the desktop. The guard is deliberately
/// bounded so a user can open the application normally after workspace restoration.
/// </summary>
internal static class BackgroundWindowGuard
{
    private static readonly object Sync = new();
    private static readonly List<Registration> Registrations = [];
    private static readonly Timer ScanTimer = new(ScanTimerTick, null, Timeout.Infinite, Timeout.Infinite);
    private static readonly WindowAppearanceWatcher AppearanceWatcher = new(HideAppearedWindow);
    private static int _scanInProgress;

    internal static bool IsEventDrivenMonitoringActive => AppearanceWatcher.IsRunning;

    public static BackgroundGuardStartResult Arm(
        IReadOnlyList<WorkspaceApplication> applications,
        TimeSpan? activeFor = null)
    {
        var backgrounds = applications
            .Where(application => application.Kind == WorkspaceApplicationKind.Background)
            .ToList();
        var duration = activeFor ?? TimeSpan.FromSeconds(20);
        if (backgrounds.Count == 0)
        {
            return new BackgroundGuardStartResult(0, 0, duration);
        }

        _ = AppearanceWatcher.EnsureStarted();

        var expiresAt = DateTimeOffset.UtcNow + duration;
        List<Registration> armed;
        lock (Sync)
        {
            Registrations.RemoveAll(registration =>
                backgrounds.Any(application =>
                    string.Equals(application.Id, registration.Application.Id, StringComparison.OrdinalIgnoreCase)));
            armed = backgrounds.Select(application => new Registration(application, expiresAt)).ToList();
            Registrations.AddRange(armed);
            ScanTimer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(50));
        }

        var hidden = HideMatchingWindows(armed);
        return new BackgroundGuardStartResult(backgrounds.Count, hidden, duration);
    }

    public static void TrackProcess(string applicationId, int? processId)
    {
        if (processId is null or <= 0)
        {
            return;
        }

        TrackProcesses(applicationId, [processId.Value]);
    }

    public static void TrackProcesses(string applicationId, IEnumerable<int> processIds)
    {
        var validIds = processIds.Where(processId => processId > 0).Select(processId => checked((uint)processId)).ToList();
        lock (Sync)
        {
            foreach (var registration in Registrations.Where(registration =>
                         string.Equals(registration.Application.Id, applicationId, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var processId in validIds)
                {
                    registration.ProcessIds.TryAdd(processId, 0);
                }
            }
        }

        // A GUI process can create its first window immediately after Process.Start returns.
        // Scan synchronously once in addition to the event-loop timer.
        ScanTimerTick(null);
    }

    public static int EnforceAndCountVisible(string applicationId)
    {
        List<Registration> registrations;
        lock (Sync)
        {
            registrations = Registrations.Where(registration =>
                string.Equals(registration.Application.Id, applicationId, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (registrations.Count == 0) return 0;

        _ = HideMatchingWindows(registrations);
        var visible = 0;
        var processPaths = new Dictionary<uint, string?>();
        var processAumids = new Dictionary<uint, string?>();
        var processNames = new Dictionary<uint, string?>();
        foreach (var hwnd in NativeInterop.EnumerateTopLevelWindows())
        {
            try
            {
                if (!NativeInterop.IsVisibleTopLevelWindow(hwnd)) continue;
                var processId = NativeInterop.GetWindowProcessId(hwnd);
                if (processId != 0 && registrations.Any(registration =>
                        Matches(registration, hwnd, processId, processPaths, processAumids, processNames)))
                {
                    visible++;
                }
            }
            catch
            {
                // A disappearing window is not a remaining visible window.
            }
        }
        return visible;
    }

    public static void Disarm(IEnumerable<string> applicationIds)
    {
        var ids = applicationIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0) return;
        lock (Sync)
        {
            Registrations.RemoveAll(registration => ids.Contains(registration.Application.Id));
            if (Registrations.Count == 0)
            {
                ScanTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }

    private static void ScanTimerTick(object? _)
    {
        if (Interlocked.Exchange(ref _scanInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            List<Registration> active;
            lock (Sync)
            {
                var now = DateTimeOffset.UtcNow;
                Registrations.RemoveAll(registration => registration.ExpiresAt <= now);
                if (Registrations.Count == 0)
                {
                    ScanTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    return;
                }

                active = Registrations.ToList();
            }

            _ = HideMatchingWindows(active);
        }
        finally
        {
            Volatile.Write(ref _scanInProgress, 0);
        }
    }

    private static int HideMatchingWindows(IReadOnlyList<Registration> registrations)
    {
        ExpandTrackedProcessTrees(registrations);
        var hidden = 0;
        var processPaths = new Dictionary<uint, string?>();
        var processAumids = new Dictionary<uint, string?>();
        var processNames = new Dictionary<uint, string?>();
        foreach (var hwnd in NativeInterop.EnumerateTopLevelWindows())
        {
            try
            {
                if (HideMatchingWindow(hwnd, registrations, processPaths, processAumids, processNames)) hidden++;
            }
            catch
            {
                // The window may close during enumeration or belong to a higher-integrity process.
                // The bounded guard will retry while the startup interval is active.
            }
        }

        return hidden;
    }

    private static void HideAppearedWindow(nint hwnd)
    {
        List<Registration> active;
        lock (Sync)
        {
            var now = DateTimeOffset.UtcNow;
            Registrations.RemoveAll(registration => registration.ExpiresAt <= now);
            active = Registrations.ToList();
        }
        if (active.Count == 0) return;

        ExpandTrackedProcessTrees(active);
        _ = HideMatchingWindow(
            hwnd,
            active,
            new Dictionary<uint, string?>(),
            new Dictionary<uint, string?>(),
            new Dictionary<uint, string?>());
    }

    private static bool HideMatchingWindow(
        nint hwnd,
        IReadOnlyList<Registration> registrations,
        IDictionary<uint, string?> processPaths,
        IDictionary<uint, string?> processAumids,
        IDictionary<uint, string?> processNames)
    {
        if (!NativeInterop.IsVisibleTopLevelWindow(hwnd)) return false;
        var processId = NativeInterop.GetWindowProcessId(hwnd);
        if (processId == 0 || !registrations.Any(registration =>
                Matches(registration, hwnd, processId, processPaths, processAumids, processNames)))
        {
            return false;
        }

        NativeInterop.SetWindowPresentation(hwnd, BackgroundWindowMode.Hide);
        return !NativeInterop.IsVisibleTopLevelWindow(hwnd);
    }

    private static void ExpandTrackedProcessTrees(IEnumerable<Registration> registrations)
    {
        foreach (var registration in registrations)
        {
            foreach (var processId in NativeInterop.ExpandProcessTree(registration.ProcessIds.Keys))
            {
                registration.ProcessIds.TryAdd(processId, 0);
            }
        }
    }

    private static bool Matches(
        Registration registration,
        nint hwnd,
        uint processId,
        IDictionary<uint, string?> processPaths,
        IDictionary<uint, string?> processAumids,
        IDictionary<uint, string?> processNames)
    {
        if (registration.ProcessIds.ContainsKey(processId))
        {
            return true;
        }

        var application = registration.Application;
        var path = GetProcessPath(processId, processPaths);
        if (!string.IsNullOrWhiteSpace(application.Launch.ExecutablePath) &&
            string.Equals(application.Launch.ExecutablePath, path, StringComparison.OrdinalIgnoreCase))
        {
            registration.ProcessIds.TryAdd(processId, 0);
            return true;
        }

        var expectedProcessName = string.IsNullOrWhiteSpace(application.Launch.ExecutablePath)
            ? null
            : Path.GetFileNameWithoutExtension(application.Launch.ExecutablePath);
        var processName = GetProcessName(processId, processNames);
        if (!string.IsNullOrWhiteSpace(expectedProcessName) &&
            string.Equals(expectedProcessName, processName, StringComparison.OrdinalIgnoreCase))
        {
            registration.ProcessIds.TryAdd(processId, 0);
            return true;
        }

        var aumid = GetProcessAumid(processId, processAumids);
        if (!string.IsNullOrWhiteSpace(application.Launch.AppUserModelId) &&
            string.Equals(application.Launch.AppUserModelId, aumid, StringComparison.OrdinalIgnoreCase))
        {
            registration.ProcessIds.TryAdd(processId, 0);
            return true;
        }

        if (application.WindowIdentity is null)
        {
            return false;
        }

        var snapshot = new WindowSnapshot(
            hwnd,
            processId,
            path,
            NativeInterop.GetWindowClassName(hwnd),
            NativeInterop.GetWindowTitle(hwnd),
            new PhysicalRect(0, 0, 1, 1),
            aumid);
        return WorkspaceService.EvaluateMatch(application, snapshot).IsMatch;
    }

    private static string? GetProcessPath(uint processId, IDictionary<uint, string?> cache)
    {
        if (cache.TryGetValue(processId, out var cached))
        {
            return cached;
        }

        try
        {
            cached = NativeInterop.TryGetProcessImagePath(processId);
            if (string.IsNullOrWhiteSpace(cached))
            {
                using var process = Process.GetProcessById(checked((int)processId));
                cached = process.MainModule?.FileName;
            }
        }
        catch
        {
            cached = null;
        }

        cache[processId] = cached;
        return cached;
    }

    private static string? GetProcessName(uint processId, IDictionary<uint, string?> cache)
    {
        if (cache.TryGetValue(processId, out var cached)) return cached;
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            cached = process.ProcessName;
        }
        catch
        {
            cached = null;
        }
        cache[processId] = cached;
        return cached;
    }

    private static string? GetProcessAumid(uint processId, IDictionary<uint, string?> cache)
    {
        if (!cache.TryGetValue(processId, out var cached))
        {
            cached = NativeInterop.TryGetApplicationUserModelId(processId);
            cache[processId] = cached;
        }

        return cached;
    }

    private sealed class Registration(WorkspaceApplication application, DateTimeOffset expiresAt)
    {
        public WorkspaceApplication Application { get; } = application;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public ConcurrentDictionary<uint, byte> ProcessIds { get; } = new();
    }
}
