using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace SnapWorkspace.App.Services;

internal sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int FirstHotkeyId = 0x534E;
    private const uint ModNoRepeat = 0x4000;
    private nint _hwnd;
    private HwndSource? _source;
    private readonly Dictionary<int, Action> _callbacks = [];
    private readonly List<int> _registeredIds = [];

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

    public IReadOnlyList<GlobalHotkeyRegistrationResult> Apply(
        nint hwnd,
        IReadOnlyList<GlobalHotkeyRegistration> registrations)
    {
        DisposeRegistration();
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WindowProc);
        var results = new List<GlobalHotkeyRegistrationResult>(registrations.Count);
        var combinations = new HashSet<(uint Modifiers, uint VirtualKey)>();
        var nextId = FirstHotkeyId;
        foreach (var registration in registrations)
        {
            if (!registration.Enabled)
            {
                results.Add(new(registration.Id, registration.Gesture, false, false, "未启用"));
                continue;
            }
            if (!TryParse(registration.Gesture, out var modifiers, out var virtualKey))
            {
                results.Add(new(registration.Id, registration.Gesture, true, false, "格式无效"));
                continue;
            }
            if (!combinations.Add((modifiers, virtualKey)))
            {
                results.Add(new(registration.Id, registration.Gesture, true, false, "与本软件的其他快捷键重复"));
                continue;
            }
            var id = nextId++;
            if (!RegisterHotKey(hwnd, id, modifiers | ModNoRepeat, virtualKey))
            {
                results.Add(new(registration.Id, registration.Gesture, true, false, "已被其他程序占用"));
                continue;
            }
            _registeredIds.Add(id);
            _callbacks[id] = registration.Callback;
            results.Add(new(registration.Id, Normalize(registration.Gesture), true, true, "已启用"));
        }
        return results;
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && _callbacks.TryGetValue(unchecked((int)wParam), out var callback))
        {
            handled = true;
            callback();
        }
        return nint.Zero;
    }

    internal static bool TryParse(string gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        foreach (var part in parts[..^1])
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) modifiers |= 0x0002;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= 0x0001;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= 0x0004;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase)) modifiers |= 0x0008;
            else return false;
        }

        var keyText = parts[^1];
        Key key;
        if (keyText.Length == 1 && char.IsDigit(keyText[0]))
        {
            key = Key.D0 + (keyText[0] - '0');
        }
        else if (!Enum.TryParse(keyText, true, out key) || key == Key.None)
        {
            return false;
        }
        virtualKey = checked((uint)KeyInterop.VirtualKeyFromKey(key));
        return virtualKey != 0 && modifiers != 0;
    }

    internal static string FormatGesture(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(5);
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(key is >= Key.D0 and <= Key.D9 ? ((int)(key - Key.D0)).ToString() : key.ToString());
        return string.Join('+', parts);
    }

    internal static string Normalize(string gesture)
    {
        if (!TryParse(gesture, out var modifiers, out var virtualKey)) return gesture.Trim();
        var key = KeyInterop.KeyFromVirtualKey(checked((int)virtualKey));
        var keys = ModifierKeys.None;
        if ((modifiers & 0x0002) != 0) keys |= ModifierKeys.Control;
        if ((modifiers & 0x0001) != 0) keys |= ModifierKeys.Alt;
        if ((modifiers & 0x0004) != 0) keys |= ModifierKeys.Shift;
        if ((modifiers & 0x0008) != 0) keys |= ModifierKeys.Windows;
        return FormatGesture(keys, key);
    }

    private void DisposeRegistration()
    {
        if (_hwnd != nint.Zero)
        {
            foreach (var id in _registeredIds) _ = UnregisterHotKey(_hwnd, id);
        }
        if (_source is not null) _source.RemoveHook(WindowProc);
        _registeredIds.Clear();
        _callbacks.Clear();
        _source = null;
        _hwnd = nint.Zero;
    }

    public void Dispose() => DisposeRegistration();
}

internal sealed record GlobalHotkeyRegistration(
    string Id,
    string Gesture,
    bool Enabled,
    Action Callback);

internal sealed record GlobalHotkeyRegistrationResult(
    string Id,
    string Gesture,
    bool Enabled,
    bool Registered,
    string Message);
