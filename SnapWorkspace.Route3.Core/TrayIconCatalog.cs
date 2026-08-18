using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SnapWorkspace.Route3;

public static class TrayIconCatalog
{
    private const string NotifyIconSettingsPath = @"Control Panel\NotifyIconSettings";

    public static IReadOnlyList<RunningApplicationSnapshot> EnumerateRunningApplications()
    {
        var running = EnumerateRunningProcesses();
        using var root = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsPath);
        if (root is null)
        {
            return [];
        }

        var results = new List<RunningApplicationSnapshot>();
        foreach (var subKeyName in root.GetSubKeyNames())
        {
            using var item = root.OpenSubKey(subKeyName);
            if (item?.GetValue("ExecutablePath") is not string storedPath ||
                string.IsNullOrWhiteSpace(storedPath))
            {
                continue;
            }

            var resolvedPath = ResolveNotifyIconPath(storedPath);
            var process = FindRunningProcess(running, resolvedPath);
            if (process is null ||
                string.Equals(Path.GetFileName(process.ExecutablePath), "explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tooltip = item.GetValue("InitialTooltip") as string;
            results.Add(new RunningApplicationSnapshot(
                process.ProcessId,
                string.IsNullOrWhiteSpace(tooltip) ? process.DisplayName : tooltip.Trim(),
                process.ExecutablePath,
                process.AppUserModelId,
                RunningApplicationSurface.Tray));
        }

        return results
            .GroupBy(item => item.ExecutablePath ?? item.AppUserModelId!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<RunningApplicationSnapshot> EnumerateRunningProcesses()
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        var results = new List<RunningApplicationSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.SessionId != sessionId)
                    {
                        continue;
                    }

                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    var description = process.MainModule?.FileVersionInfo.FileDescription;
                    results.Add(new RunningApplicationSnapshot(
                        (uint)process.Id,
                        string.IsNullOrWhiteSpace(description)
                            ? Path.GetFileNameWithoutExtension(path)
                            : description.Trim(),
                        Path.GetFullPath(path),
                        NativeInterop.TryGetApplicationUserModelId((uint)process.Id),
                        RunningApplicationSurface.Tray));
                }
                catch
                {
                    // 跨完整性级别或刚退出的进程无法作为可靠启动目标。
                }
            }
        }

        return results;
    }

    private static RunningApplicationSnapshot? FindRunningProcess(
        IReadOnlyList<RunningApplicationSnapshot> running,
        string? resolvedPath)
    {
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return null;
        }

        var exact = running.FirstOrDefault(item =>
            string.Equals(item.ExecutablePath, resolvedPath, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        // 应用升级后通知区域历史记录可能仍含旧版本目录；文件名唯一时允许迁移到当前进程。
        var fileName = Path.GetFileName(resolvedPath);
        var byFileName = running.Where(item =>
            string.Equals(Path.GetFileName(item.ExecutablePath), fileName, StringComparison.OrdinalIgnoreCase)).ToList();
        return byFileName.Count == 1 ? byFileName[0] : null;
    }

    private static string? ResolveNotifyIconPath(string storedPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(storedPath.Trim());
        if (!expanded.StartsWith('{'))
        {
            try { return Path.GetFullPath(expanded); }
            catch { return null; }
        }

        var separator = expanded.IndexOf('}');
        if (separator < 0 || !Guid.TryParse(expanded[..(separator + 1)], out var folderId))
        {
            return null;
        }

        var result = SHGetKnownFolderPath(folderId, 0, nint.Zero, out var folderPointer);
        if (result != 0 || folderPointer == nint.Zero)
        {
            return null;
        }

        try
        {
            var folder = Marshal.PtrToStringUni(folderPointer);
            var relative = expanded[(separator + 1)..].TrimStart('\\', '/');
            return string.IsNullOrWhiteSpace(folder) ? null : Path.GetFullPath(Path.Combine(folder, relative));
        }
        finally
        {
            Marshal.FreeCoTaskMem(folderPointer);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid folderId,
        uint flags,
        nint token,
        out nint path);
}
