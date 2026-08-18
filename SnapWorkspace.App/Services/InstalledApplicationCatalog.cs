using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SnapWorkspace.Route3;

namespace SnapWorkspace.App.Services;

public enum InstalledApplicationSource
{
    StartMenu,
    AppsFolder,
    Registry
}

public sealed class InstalledApplicationEntry
{
    private ImageSource? _icon;
    private bool _iconLoaded;

    public required string DisplayName { get; init; }
    public string? ExecutablePath { get; init; }
    public string? AppUserModelId { get; init; }
    public string Arguments { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;
    public InstalledApplicationSource Source { get; init; }
    public string IdentityKey => ExecutablePath ?? AppUserModelId ?? DisplayName;
    public string SourceLabel => Source switch
    {
        InstalledApplicationSource.StartMenu => "开始菜单",
        InstalledApplicationSource.AppsFolder => "应用包 / AppsFolder",
        _ => "已安装程序"
    };
    public string Details => ExecutablePath ?? AppUserModelId ?? "无启动目标";
    public ImageSource? Icon
    {
        get
        {
            if (_iconLoaded) return _icon;
            _iconLoaded = true;
            _icon = ShellApplicationIcon.Load(ExecutablePath, AppUserModelId);
            return _icon;
        }
    }

    public ApplicationLaunchSpec ToLaunchSpec() => new()
    {
        ExecutablePath = ExecutablePath,
        AppUserModelId = AppUserModelId,
        Arguments = Arguments,
        WorkingDirectory = WorkingDirectory
    };
}

public static class InstalledApplicationCatalog
{
    public static Task<IReadOnlyList<InstalledApplicationEntry>> EnumerateAsync()
    {
        var completion = new TaskCompletionSource<IReadOnlyList<InstalledApplicationEntry>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(Enumerate()); }
            catch (Exception exception) { completion.SetException(exception); }
        })
        {
            IsBackground = true,
            Name = "SnapWorkspace installed application catalog"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public static IReadOnlyList<InstalledApplicationEntry> Enumerate()
    {
        var applications = new Dictionary<string, InstalledApplicationEntry>(StringComparer.OrdinalIgnoreCase);
        AddStartMenuShortcuts(applications);
        AddShellAppsFolder(applications);
        AddUninstallRegistryEntries(applications);
        return applications.Values
            .Where(application => application.ToLaunchSpec().CanLaunch)
            .OrderBy(application => application.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void AddStartMenuShortcuts(IDictionary<string, InstalledApplicationEntry> applications)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            foreach (var root in roots)
            {
                IEnumerable<string> shortcuts;
                try { shortcuts = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var shortcutPath in shortcuts)
                {
                    object? shortcut = null;
                    try
                    {
                        shortcut = shellType.InvokeMember(
                            "CreateShortcut",
                            System.Reflection.BindingFlags.InvokeMethod,
                            null,
                            shell,
                            [shortcutPath]);
                        var target = shortcut?.GetType().InvokeMember(
                            "TargetPath",
                            System.Reflection.BindingFlags.GetProperty,
                            null,
                            shortcut,
                            null) as string;
                        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target) ||
                            !string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase) ||
                            IsLikelyMaintenanceTarget(target, Path.GetFileNameWithoutExtension(shortcutPath)))
                        {
                            continue;
                        }

                        if (shortcut is null) continue;
                        var arguments = shortcut.GetType().InvokeMember(
                            "Arguments", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string ?? string.Empty;
                        var workingDirectory = shortcut.GetType().InvokeMember(
                            "WorkingDirectory", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string ?? string.Empty;
                        AddIfMissing(applications, new InstalledApplicationEntry
                        {
                            DisplayName = Path.GetFileNameWithoutExtension(shortcutPath),
                            ExecutablePath = Path.GetFullPath(target),
                            Arguments = arguments,
                            WorkingDirectory = workingDirectory,
                            Source = InstalledApplicationSource.StartMenu
                        });
                    }
                    catch
                    {
                        // Broken or protected shortcut.
                    }
                    finally
                    {
                        if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                    }
                }
            }
        }
        catch
        {
            // Start-menu COM resolution is optional; AppsFolder/registry remain available.
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void AddShellAppsFolder(IDictionary<string, InstalledApplicationEntry> applications)
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return;
        object? shell = null;
        object? folder = null;
        object? items = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            folder = shellType.InvokeMember(
                "NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, ["shell:AppsFolder"]);
            if (folder is null) return;
            items = folder.GetType().InvokeMember(
                "Items", System.Reflection.BindingFlags.InvokeMethod, null, folder, null);
            if (items is null) return;
            var count = Convert.ToInt32(items.GetType().InvokeMember(
                "Count", System.Reflection.BindingFlags.GetProperty, null, items, null));
            for (var index = 0; index < count; index++)
            {
                object? item = null;
                try
                {
                    item = items.GetType().InvokeMember(
                        "Item", System.Reflection.BindingFlags.InvokeMethod, null, items, [index]);
                    if (item is null) continue;
                    var name = item.GetType().InvokeMember(
                        "Name", System.Reflection.BindingFlags.GetProperty, null, item, null) as string;
                    var path = item.GetType().InvokeMember(
                        "Path", System.Reflection.BindingFlags.GetProperty, null, item, null) as string;
                    var aumid = TryGetExtendedProperty(item, "System.AppUserModel.ID");
                    var targetParsingPath = TryGetExtendedProperty(item, "System.Link.TargetParsingPath");
                    var executableCandidate = new[] { path, targetParsingPath }
                        .FirstOrDefault(candidate =>
                            !string.IsNullOrWhiteSpace(candidate) &&
                            File.Exists(candidate) &&
                            string.Equals(Path.GetExtension(candidate), ".exe", StringComparison.OrdinalIgnoreCase));
                    var executablePath = string.IsNullOrWhiteSpace(executableCandidate)
                        ? null
                        : Path.GetFullPath(executableCandidate);
                    if (string.IsNullOrWhiteSpace(executablePath) && string.IsNullOrWhiteSpace(aumid)) continue;
                    AddIfMissing(applications, new InstalledApplicationEntry
                    {
                        DisplayName = string.IsNullOrWhiteSpace(name)
                            ? Path.GetFileNameWithoutExtension(executablePath) ?? aumid!
                            : name.Trim(),
                        ExecutablePath = executablePath,
                        AppUserModelId = aumid,
                        WorkingDirectory = executablePath is null ? string.Empty : Path.GetDirectoryName(executablePath) ?? string.Empty,
                        Source = InstalledApplicationSource.AppsFolder
                    });
                }
                catch
                {
                    // Individual shell items can disappear while the folder is enumerated.
                }
                finally
                {
                    if (item is not null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
                }
            }
        }
        catch
        {
            // AppsFolder is best-effort; traditional entries are still collected.
        }
        finally
        {
            if (items is not null && Marshal.IsComObject(items)) Marshal.FinalReleaseComObject(items);
            if (folder is not null && Marshal.IsComObject(folder)) Marshal.FinalReleaseComObject(folder);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static string? TryGetExtendedProperty(object item, string propertyName)
    {
        try
        {
            return item.GetType().InvokeMember(
                "ExtendedProperty",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                item,
                [propertyName]) as string;
        }
        catch { return null; }
    }

    private static void AddUninstallRegistryEntries(IDictionary<string, InstalledApplicationEntry> applications)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(subKeyName);
                    var displayName = entry?.GetValue("DisplayName") as string;
                    var systemComponent = Convert.ToInt32(entry?.GetValue("SystemComponent") ?? 0) == 1;
                    var releaseType = entry?.GetValue("ReleaseType") as string;
                    var displayIcon = entry?.GetValue("DisplayIcon") as string;
                    var executablePath = ParseDisplayIcon(displayIcon);
                    if (string.IsNullOrWhiteSpace(displayName) || executablePath is null || systemComponent ||
                        !string.IsNullOrWhiteSpace(releaseType) || IsLikelyMaintenanceTarget(executablePath, displayName)) continue;
                    AddIfMissing(applications, new InstalledApplicationEntry
                    {
                        DisplayName = displayName.Trim(),
                        ExecutablePath = executablePath,
                        WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
                        Source = InstalledApplicationSource.Registry
                    });
                }
            }
            catch
            {
                // Some uninstall keys deny access or contain malformed paths.
            }
        }
    }

    private static string? ParseDisplayIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        var candidate = expanded.StartsWith('"')
            ? expanded.TrimStart('"').Split('"')[0]
            : expanded.Split(',')[0].Trim();
        try
        {
            return File.Exists(candidate) && string.Equals(Path.GetExtension(candidate), ".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(candidate)
                : null;
        }
        catch { return null; }
    }

    private static bool IsLikelyMaintenanceTarget(string executablePath, string displayName)
    {
        var fileName = Path.GetFileNameWithoutExtension(executablePath);
        return fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("setup", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("msiexec", StringComparison.OrdinalIgnoreCase) ||
               displayName.Contains("卸载", StringComparison.CurrentCultureIgnoreCase) ||
               displayName.Contains("uninstall", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddIfMissing(
        IDictionary<string, InstalledApplicationEntry> applications,
        InstalledApplicationEntry entry)
    {
        if (!applications.ContainsKey(entry.IdentityKey)) applications[entry.IdentityKey] = entry;
    }
}

internal static class ShellApplicationIcon
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;
    private const uint ShgfiPidl = 0x000000008;

    public static ImageSource? Load(string? executablePath, string? aumid)
    {
        var parsingName = !string.IsNullOrWhiteSpace(executablePath)
            ? executablePath
            : string.IsNullOrWhiteSpace(aumid) ? null : $"shell:AppsFolder\\{aumid}";
        if (parsingName is null) return null;
        nint pidl = nint.Zero;
        try
        {
            if (SHParseDisplayName(parsingName, nint.Zero, out pidl, 0, out _) != 0 || pidl == nint.Zero)
            {
                return null;
            }

            var info = new ShFileInfo();
            if (SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiSmallIcon | ShgfiPidl) == nint.Zero ||
                info.IconHandle == nint.Zero)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    info.IconHandle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(24, 24));
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(info.IconHandle);
            }
        }
        catch { return null; }
        finally
        {
            if (pidl != nint.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public nint IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHParseDisplayName(
        string name,
        nint bindingContext,
        out nint itemIdList,
        uint attributes,
        out uint attributesOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(
        nint path,
        uint fileAttributes,
        ref ShFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
