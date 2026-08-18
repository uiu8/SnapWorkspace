namespace SnapWorkspace.Route3;

public interface IWindowIdProvider
{
    string Name { get; }
    bool TryGetWindowId(nint hwnd, out ulong windowId, out string? error);
}

/// <summary>
/// 当前 Windows 10.0.29639.1000 行为探针验证过的兼容实现。
/// 生产版应替换为 windows.ui.interop.h 的 GetWindowIdFromWindow。
/// </summary>
public sealed class CurrentBuildHwndWindowIdProvider : IWindowIdProvider
{
    public string Name => "HWND bit-cast (validated only on 10.0.29639.1000)";

    public bool TryGetWindowId(nint hwnd, out ulong windowId, out string? error)
    {
        if (!NativeInterop.IsWindow(hwnd))
        {
            windowId = 0;
            error = $"0x{hwnd.ToInt64():X} 不是有效窗口。";
            return false;
        }

        windowId = unchecked((ulong)hwnd.ToInt64());
        error = null;
        return true;
    }
}
