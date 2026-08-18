namespace SnapWorkspace.Route3;

public static class WorkspaceWindowLifecycle
{
    public static int RequestClose(IEnumerable<nint> windows)
    {
        var requested = 0;
        foreach (var hwnd in windows.Distinct())
        {
            if (!NativeInterop.IsWindow(hwnd)) continue;
            if (NativeInterop.RequestClose(hwnd)) requested++;
        }
        return requested;
    }
}
