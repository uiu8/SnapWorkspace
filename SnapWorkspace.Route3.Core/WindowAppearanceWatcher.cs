using System.Runtime.InteropServices;

namespace SnapWorkspace.Route3;

/// <summary>
/// Receives the same accessibility window lifecycle events used by PowerToys
/// Workspaces. A dedicated message-loop thread keeps delivery reliable even
/// when workspace restoration is started from a tray or worker thread.
/// </summary>
internal sealed class WindowAppearanceWatcher
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectShow = 0x8002;
    private const uint WineventOutOfContext = 0x0000;
    private const int ObjidWindow = 0;
    private const int ChildidSelf = 0;

    private readonly object _sync = new();
    private readonly Action<nint> _windowAppeared;
    private readonly WinEventProc _callback;
    private ManualResetEventSlim? _ready;
    private Thread? _thread;
    private bool _running;

    public WindowAppearanceWatcher(Action<nint> windowAppeared)
    {
        _windowAppeared = windowAppeared;
        _callback = OnWinEvent;
    }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _running;
            }
        }
    }

    public bool EnsureStarted()
    {
        ManualResetEventSlim ready;
        lock (_sync)
        {
            if (_running) return true;
            if (_thread is null || !_thread.IsAlive)
            {
                _ready?.Dispose();
                _ready = new ManualResetEventSlim(false);
                _thread = new Thread(RunMessageLoop)
                {
                    IsBackground = true,
                    Name = "SnapWorkspace.BackgroundWindowEvents"
                };
                _thread.Start();
            }
            ready = _ready!;
        }

        return ready.Wait(TimeSpan.FromSeconds(2)) && IsRunning;
    }

    private void RunMessageLoop()
    {
        var createHook = SetWinEventHook(
            EventObjectCreate,
            EventObjectCreate,
            nint.Zero,
            _callback,
            0,
            0,
            WineventOutOfContext);
        var showHook = SetWinEventHook(
            EventObjectShow,
            EventObjectShow,
            nint.Zero,
            _callback,
            0,
            0,
            WineventOutOfContext);

        lock (_sync)
        {
            _running = createHook != nint.Zero || showHook != nint.Zero;
            _ready?.Set();
        }

        if (createHook == nint.Zero && showHook == nint.Zero)
        {
            return;
        }

        try
        {
            while (GetMessageW(out _, nint.Zero, 0, 0) > 0)
            {
                // SetWinEventHook dispatches out-of-context callbacks on this thread.
            }
        }
        finally
        {
            if (createHook != nint.Zero) _ = UnhookWinEvent(createHook);
            if (showHook != nint.Zero) _ = UnhookWinEvent(showHook);
            lock (_sync)
            {
                _running = false;
            }
        }
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        _ = hook;
        _ = eventThread;
        _ = eventTime;
        if (hwnd == nint.Zero ||
            objectId != ObjidWindow ||
            childId != ChildidSelf ||
            eventType is not (EventObjectCreate or EventObjectShow))
        {
            return;
        }

        try
        {
            _windowAppeared(hwnd);
        }
        catch
        {
            // Native event callbacks must never escape into User32.
        }
    }

    private delegate void WinEventProc(
        nint hook,
        uint eventType,
        nint hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal nint Hwnd;
        internal uint Message;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal NativePoint Point;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint eventHookModule,
        WinEventProc eventProc,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint eventHook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(
        out NativeMessage message,
        nint hwnd,
        uint messageFilterMin,
        uint messageFilterMax);
}
