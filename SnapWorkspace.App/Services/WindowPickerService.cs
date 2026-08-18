using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SnapWorkspace.App.Services;

internal sealed class WindowPickerService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const uint VkEscape = 0x1B;
    private const uint GaRoot = 2;

    private readonly IReadOnlySet<nint> _excludedWindows;
    private readonly TaskCompletionSource<nint?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HookProcedure _mouseProcedure;
    private readonly HookProcedure _keyboardProcedure;
    private nint _mouseHook;
    private nint _keyboardHook;
    private bool _disposed;

    internal WindowPickerService(IEnumerable<nint> excludedWindows)
    {
        _excludedWindows = excludedWindows.Where(hwnd => hwnd != nint.Zero).ToHashSet();
        _mouseProcedure = MouseHookCallback;
        _keyboardProcedure = KeyboardHookCallback;
    }

    internal Task<nint?> PickAsync()
    {
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = GetModuleHandleW(module?.ModuleName);
        _mouseHook = SetWindowsHookExW(WhMouseLl, _mouseProcedure, moduleHandle, 0);
        _keyboardHook = SetWindowsHookExW(WhKeyboardLl, _keyboardProcedure, moduleHandle, 0);
        if (_mouseHook == nint.Zero || _keyboardHook == nint.Zero)
        {
            Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动全局窗口点选器。");
        }

        return _completion.Task;
    }

    private nint MouseHookCallback(int code, nint message, nint data)
    {
        if (code >= 0 && message == WmRButtonDown)
        {
            Complete(null);
            return new nint(1);
        }

        if (code >= 0 && message == WmLButtonDown)
        {
            var hook = Marshal.PtrToStructure<LowLevelMouseHook>(data);
            var selected = GetAncestor(WindowFromPoint(hook.Point), GaRoot);
            if (selected != nint.Zero && !_excludedWindows.Contains(selected))
            {
                Complete(selected);
                return new nint(1);
            }
        }

        return CallNextHookEx(_mouseHook, code, message, data);
    }

    private nint KeyboardHookCallback(int code, nint message, nint data)
    {
        if (code >= 0 && (message == WmKeyDown || message == WmSysKeyDown))
        {
            var hook = Marshal.PtrToStructure<LowLevelKeyboardHook>(data);
            if (hook.VirtualKey == VkEscape)
            {
                Complete(null);
                return new nint(1);
            }
        }

        return CallNextHookEx(_keyboardHook, code, message, data);
    }

    private void Complete(nint? hwnd)
    {
        Dispose();
        _completion.TrySetResult(hwnd);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_mouseHook != nint.Zero)
        {
            _ = UnhookWindowsHookEx(_mouseHook);
            _mouseHook = nint.Zero;
        }
        if (_keyboardHook != nint.Zero)
        {
            _ = UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = nint.Zero;
        }
    }

    private delegate nint HookProcedure(int code, nint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        internal readonly int X;
        internal readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LowLevelMouseHook
    {
        internal readonly NativePoint Point;
        internal readonly uint MouseData;
        internal readonly uint Flags;
        internal readonly uint Time;
        internal readonly nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LowLevelKeyboardHook
    {
        internal readonly uint VirtualKey;
        internal readonly uint ScanCode;
        internal readonly uint Flags;
        internal readonly uint Time;
        internal readonly nuint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(
        int hookId,
        HookProcedure procedure,
        nint module,
        uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandleW(string? moduleName);
}
