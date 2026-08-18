using System.Runtime.InteropServices;
using System.Text;

namespace SnapWorkspace.Route3;

internal static class NativeInterop
{
    internal const int S_OK = 0;
    internal const int S_FALSE = 1;
    internal const int RpcEChangedMode = unchecked((int)0x80010106);
    private const uint RoInitSinglethreaded = 0;
    private const uint SpiGetWorkArea = 0x0030;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExTopmost = 0x00000008L;
    private const long WsChild = 0x40000000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;
    private const int SwRestore = 9;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint WmClose = 0x0010;
    private const uint Th32csSnapprocess = 0x00000002;
    private static readonly nint InvalidHandleValue = new(-1);
    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNotTopmost = new(-2);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;

        internal readonly PhysicalRect ToPhysicalRect() =>
            new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        internal uint Length;
        internal uint Flags;
        internal uint ShowCommand;
        internal NativePoint MinPosition;
        internal NativePoint MaxPosition;
        internal NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinRtRect
    {
        internal float X;
        internal float Y;
        internal float Width;
        internal float Height;

        internal static WinRtRect FromPhysical(PhysicalRect rect) =>
            new() { X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowId
    {
        internal ulong Value;
    }

    [DllImport("combase.dll")]
    internal static extern int RoInitialize(uint initType);

    [DllImport("combase.dll")]
    private static extern void RoUninitialize();

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    internal static extern int WindowsCreateString(
        string sourceString,
        uint length,
        out nint hstring);

    [DllImport("combase.dll")]
    internal static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll")]
    internal static extern int RoGetActivationFactory(
        nint activatableClassId,
        ref Guid iid,
        out nint factory);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        out NativeRect value,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    private delegate bool EnumWindowsCallback(nint hwnd, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        nint process,
        uint flags,
        StringBuilder executableName,
        ref uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(
        nint process,
        ref uint applicationUserModelIdLength,
        StringBuilder? applicationUserModelId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint hwnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        nint hwnd,
        int attribute,
        out NativeRect value,
        int valueSize);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(
        nint hwnd,
        int attribute,
        out int value,
        int valueSize);

    internal static PhysicalRect GetPrimaryWorkArea()
    {
        using var dpi = new DpiAwarenessScope();
        if (!SystemParametersInfo(SpiGetWorkArea, 0, out var rect, 0))
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "读取主显示器工作区失败。");
        }

        return rect.ToPhysicalRect();
    }

    internal static bool RequestClose(nint hwnd) =>
        IsWindow(hwnd) && PostMessageW(hwnd, WmClose, nint.Zero, nint.Zero);

    internal static PhysicalRect GetVisibleWindowBounds(nint hwnd)
    {
        var hr = DwmGetWindowAttribute(
            hwnd,
            DwmwaExtendedFrameBounds,
            out var rect,
            Marshal.SizeOf<NativeRect>());
        if (hr >= 0)
        {
            return rect.ToPhysicalRect();
        }

        if (!GetWindowRect(hwnd, out rect))
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "读取窗口矩形失败。");
        }

        return rect.ToPhysicalRect();
    }

    internal static PhysicalRect GetPreferredCaptureBounds(
        nint hwnd,
        PhysicalRect visibleBounds,
        PhysicalRect workArea)
    {
        if (IsUsableCaptureBounds(visibleBounds, workArea))
        {
            return visibleBounds;
        }

        var placement = new WindowPlacement
        {
            Length = checked((uint)Marshal.SizeOf<WindowPlacement>())
        };
        if (GetWindowPlacement(hwnd, ref placement))
        {
            var normalBounds = placement.NormalPosition.ToPhysicalRect();
            if (IsUsableCaptureBounds(normalBounds, workArea))
            {
                return normalBounds;
            }
        }

        return visibleBounds;
    }

    internal static bool IsUsableCaptureBounds(PhysicalRect bounds, PhysicalRect workArea)
    {
        if (bounds.Width < 160 || bounds.Height < 100)
        {
            return false;
        }

        var intersectionWidth = Math.Max(
            0,
            Math.Min(bounds.Right, workArea.Right) - Math.Max(bounds.X, workArea.X));
        var intersectionHeight = Math.Max(
            0,
            Math.Min(bounds.Bottom, workArea.Bottom) - Math.Max(bounds.Y, workArea.Y));
        return intersectionWidth >= 80 && intersectionHeight >= 60;
    }

    internal static PhysicalRect PlaceWindowByVisibleBounds(nint hwnd, PhysicalRect target)
    {
        if (!IsWindow(hwnd))
        {
            throw new InvalidOperationException("兼容定位目标窗口已经关闭。");
        }

        _ = ShowWindow(hwnd, SwRestore);
        var currentVisible = GetVisibleWindowBounds(hwnd);
        if (!GetWindowRect(hwnd, out var outerRect))
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "读取兼容窗口外框失败。");
        }

        var leftInset = currentVisible.X - outerRect.Left;
        var topInset = currentVisible.Y - outerRect.Top;
        var rightInset = outerRect.Right - currentVisible.Right;
        var bottomInset = outerRect.Bottom - currentVisible.Bottom;
        var outerX = target.X - leftInset;
        var outerY = target.Y - topInset;
        var outerWidth = target.Width + leftInset + rightInset;
        var outerHeight = target.Height + topInset + bottomInset;
        if (!SetWindowPos(
                hwnd,
                nint.Zero,
                outerX,
                outerY,
                outerWidth,
                outerHeight,
                SwpNoZOrder | SwpNoActivate))
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "兼容窗口定位失败。");
        }

        var actual = GetVisibleWindowBounds(hwnd);
        for (var correction = 0; correction < 2; correction++)
        {
            var deltaX = target.X - actual.X;
            var deltaY = target.Y - actual.Y;
            var deltaWidth = target.Width - actual.Width;
            var deltaHeight = target.Height - actual.Height;
            if (deltaX == 0 && deltaY == 0 && deltaWidth == 0 && deltaHeight == 0)
            {
                break;
            }

            if (!GetWindowRect(hwnd, out outerRect))
            {
                break;
            }

            if (!SetWindowPos(
                    hwnd,
                    nint.Zero,
                    outerRect.Left + deltaX,
                    outerRect.Top + deltaY,
                    outerRect.Right - outerRect.Left + deltaWidth,
                    outerRect.Bottom - outerRect.Top + deltaHeight,
                    SwpNoZOrder | SwpNoActivate))
            {
                break;
            }

            actual = GetVisibleWindowBounds(hwnd);
        }

        return actual;
    }

    internal static bool RaiseWindowWithoutActivation(nint hwnd) =>
        IsWindow(hwnd) && SetWindowPos(
            hwnd,
            nint.Zero,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow);

    internal static bool IsTopmostWindow(nint hwnd) =>
        IsWindow(hwnd) && (GetWindowLongPtrW(hwnd, GwlExStyle).ToInt64() & WsExTopmost) != 0;

    internal static bool SetTemporaryTopmost(nint hwnd, bool enabled) =>
        IsWindow(hwnd) && SetWindowPos(
            hwnd,
            enabled ? HwndTopmost : HwndNotTopmost,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow);

    internal static bool IsLikelySnapEligibleWindow(nint hwnd)
    {
        if (!IsWindow(hwnd)) return false;
        var style = GetWindowLongPtrW(hwnd, -16).ToInt64();
        return (style & WsChild) == 0 &&
               ((style & WsThickFrame) != 0 || (style & WsMaximizeBox) != 0);
    }

    internal static void SetWindowPresentation(nint hwnd, BackgroundWindowMode mode)
    {
        if (!IsWindow(hwnd)) return;
        var command = mode switch
        {
            BackgroundWindowMode.Minimize => 6,
            BackgroundWindowMode.Hide => 0,
            _ => -1
        };
        if (command < 0) return;

        // Cross-process GUI threads can be busy while their first window is created.
        // Queue the presentation request first, then apply it synchronously as a
        // best-effort fallback. The event guard will retry if the app shows again.
        _ = ShowWindowAsync(hwnd, command);
        if (IsWindowVisible(hwnd)) _ = ShowWindow(hwnd, command);
    }

    internal static IReadOnlySet<uint> ExpandProcessTree(IEnumerable<uint> rootProcessIds)
    {
        var roots = rootProcessIds.Where(processId => processId != 0).ToHashSet();
        if (roots.Count == 0) return roots;

        var snapshot = CreateToolhelp32Snapshot(Th32csSnapprocess, 0);
        if (snapshot == nint.Zero || snapshot == InvalidHandleValue) return roots;
        try
        {
            var children = new Dictionary<uint, List<uint>>();
            var entry = new ProcessEntry32 { Size = checked((uint)Marshal.SizeOf<ProcessEntry32>()) };
            if (Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    if (!children.TryGetValue(entry.ParentProcessId, out var list))
                    {
                        list = [];
                        children[entry.ParentProcessId] = list;
                    }
                    list.Add(entry.ProcessId);
                    entry.Size = checked((uint)Marshal.SizeOf<ProcessEntry32>());
                }
                while (Process32NextW(snapshot, ref entry));
            }

            var queue = new Queue<uint>(roots);
            while (queue.TryDequeue(out var parent))
            {
                if (!children.TryGetValue(parent, out var descendants)) continue;
                foreach (var child in descendants)
                {
                    if (roots.Add(child)) queue.Enqueue(child);
                }
            }
            return roots;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    internal static IReadOnlyList<nint> EnumerateTopLevelWindows()
    {
        var windows = new List<nint>();
        EnumWindows((hwnd, _) =>
        {
            windows.Add(hwnd);
            return true;
        }, nint.Zero);
        return windows;
    }

    internal static bool IsCandidateTopLevelWindow(nint hwnd)
    {
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd))
        {
            return false;
        }

        var exStyle = GetWindowLongPtrW(hwnd, GwlExStyle).ToInt64();
        if ((exStyle & WsExToolWindow) != 0)
        {
            return false;
        }

        var cloakedHr = DwmGetWindowAttributeInt(
            hwnd,
            DwmwaCloaked,
            out var cloaked,
            sizeof(int));
        return cloakedHr < 0 || cloaked == 0;
    }

    internal static bool IsVisibleTopLevelWindow(nint hwnd) =>
        IsWindow(hwnd) && IsWindowVisible(hwnd);

    internal static string GetWindowTitle(nint hwnd)
    {
        var length = GetWindowTextLengthW(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        GetWindowTextW(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    internal static string GetWindowClassName(nint hwnd)
    {
        var builder = new StringBuilder(512);
        return GetClassNameW(hwnd, builder, builder.Capacity) > 0
            ? builder.ToString()
            : string.Empty;
    }

    internal static uint GetWindowProcessId(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var processId);
        return processId;
    }

    internal static string? TryGetApplicationUserModelId(uint processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == nint.Zero)
        {
            return null;
        }

        try
        {
            uint length = 0;
            var firstResult = GetApplicationUserModelId(process, ref length, null);
            if (firstResult != ErrorInsufficientBuffer || length is 0 or > 32768)
            {
                return null;
            }

            var builder = new StringBuilder(checked((int)length));
            return GetApplicationUserModelId(process, ref length, builder) == 0
                ? builder.ToString()
                : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    internal static string? TryGetProcessImagePath(uint processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == nint.Zero) return null;
        try
        {
            var builder = new StringBuilder(32768);
            uint length = (uint)builder.Capacity;
            return QueryFullProcessImageNameW(process, 0, builder, ref length)
                ? builder.ToString()
                : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    internal static nint VtableSlot(nint instance, int slot)
    {
        var vtable = Marshal.ReadIntPtr(instance);
        return Marshal.ReadIntPtr(vtable, slot * nint.Size);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        internal uint Size;
        internal uint UsageCount;
        internal uint ProcessId;
        internal nuint DefaultHeapId;
        internal uint ModuleId;
        internal uint ThreadCount;
        internal uint ParentProcessId;
        internal int BasePriority;
        internal uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string ExecutableFile;
    }

    internal sealed class DpiAwarenessScope : IDisposable
    {
        private static readonly nint PerMonitorAwareV2 = new(-4);
        private readonly nint _previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);

        public void Dispose()
        {
            if (_previous != nint.Zero)
            {
                SetThreadDpiAwarenessContext(_previous);
            }
        }
    }

    internal sealed class RoApartmentScope : IDisposable
    {
        private readonly bool _mustUninitialize;
        internal int InitializeHResult { get; }

        internal RoApartmentScope()
        {
            InitializeHResult = RoInitialize(RoInitSinglethreaded);
            _mustUninitialize = InitializeHResult is S_OK or S_FALSE;
            if (InitializeHResult < 0 && InitializeHResult != RpcEChangedMode)
            {
                Marshal.ThrowExceptionForHR(InitializeHResult);
            }
        }

        public void Dispose()
        {
            if (_mustUninitialize)
            {
                RoUninitialize();
            }
        }
    }

    internal sealed class HString : IDisposable
    {
        internal nint Value { get; private set; }

        internal HString(string value)
        {
            var hr = WindowsCreateString(value, (uint)value.Length, out var hstring);
            Marshal.ThrowExceptionForHR(hr);
            Value = hstring;
        }

        public void Dispose()
        {
            if (Value != nint.Zero)
            {
                WindowsDeleteString(Value);
                Value = nint.Zero;
            }
        }
    }

    internal sealed class ComPtr : IDisposable
    {
        internal nint Value { get; private set; }
        internal bool IsNull => Value == nint.Zero;

        internal ComPtr(nint value)
        {
            Value = value;
        }

        public void Dispose()
        {
            if (Value != nint.Zero)
            {
                Marshal.Release(Value);
                Value = nint.Zero;
            }
        }
    }
}
