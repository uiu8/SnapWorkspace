using System.Runtime.InteropServices;

namespace SnapWorkspace.Route3;

/// <summary>
/// Resolves non-packaged AppsFolder identities back to their executable target.
/// Shell can launch these identities, but process-level AUMID lookup is not a
/// reliable way to identify the window they later create.
/// </summary>
public static class ShellAppResolver
{
    public static ApplicationLaunchSpec Enrich(ApplicationLaunchSpec launch)
    {
        var result = launch.Clone();
        if (!string.IsNullOrWhiteSpace(result.ExecutablePath) ||
            string.IsNullOrWhiteSpace(result.AppUserModelId))
        {
            return result;
        }

        var resolved = ResolveExecutablePath(result.AppUserModelId);
        if (string.IsNullOrWhiteSpace(resolved)) return result;
        result.ExecutablePath = resolved;
        if (string.IsNullOrWhiteSpace(result.WorkingDirectory))
        {
            result.WorkingDirectory = Path.GetDirectoryName(resolved) ?? string.Empty;
        }
        return result;
    }

    private static string? ResolveExecutablePath(string appUserModelId)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return ResolveOnStaThread(appUserModelId);
        }

        string? result = null;
        Exception? failure = null;
        using var completed = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try { result = ResolveOnStaThread(appUserModelId); }
            catch (Exception exception) { failure = exception; }
            finally { completed.Set(); }
        })
        {
            IsBackground = true,
            Name = "SnapWorkspace AppsFolder resolver"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        _ = completed.Wait(TimeSpan.FromSeconds(3));
        return failure is null ? result : null;
    }

    private static string? ResolveOnStaThread(string appUserModelId)
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return null;
        object? shell = null;
        object? folder = null;
        object? items = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            folder = shellType.InvokeMember(
                "NameSpace",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                ["shell:AppsFolder"]);
            if (folder is null) return null;
            items = folder.GetType().InvokeMember(
                "Items",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                folder,
                null);
            if (items is null) return null;
            var count = Convert.ToInt32(items.GetType().InvokeMember(
                "Count",
                System.Reflection.BindingFlags.GetProperty,
                null,
                items,
                null));
            for (var index = 0; index < count; index++)
            {
                object? item = null;
                try
                {
                    item = items.GetType().InvokeMember(
                        "Item",
                        System.Reflection.BindingFlags.InvokeMethod,
                        null,
                        items,
                        [index]);
                    if (item is null) continue;
                    var aumid = ExtendedProperty(item, "System.AppUserModel.ID");
                    if (!string.Equals(aumid, appUserModelId, StringComparison.OrdinalIgnoreCase)) continue;

                    foreach (var candidate in new[]
                             {
                                 ExtendedProperty(item, "System.Link.TargetParsingPath"),
                                 ExtendedProperty(item, "System.Link.TargetPath"),
                                 item.GetType().InvokeMember(
                                     "Path",
                                     System.Reflection.BindingFlags.GetProperty,
                                     null,
                                     item,
                                     null) as string
                             })
                    {
                        if (string.IsNullOrWhiteSpace(candidate) ||
                            !string.Equals(Path.GetExtension(candidate), ".exe", StringComparison.OrdinalIgnoreCase) ||
                            !File.Exists(candidate))
                        {
                            continue;
                        }
                        return Path.GetFullPath(candidate);
                    }
                    return null;
                }
                catch
                {
                    // Continue with the next AppsFolder entry.
                }
                finally
                {
                    Release(item);
                }
            }
            return null;
        }
        finally
        {
            Release(items);
            Release(folder);
            Release(shell);
        }
    }

    private static string? ExtendedProperty(object item, string name)
    {
        try
        {
            return item.GetType().InvokeMember(
                "ExtendedProperty",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                item,
                [name]) as string;
        }
        catch
        {
            return null;
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}
