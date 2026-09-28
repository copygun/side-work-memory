using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Side.Win.Capture;

/// <summary>Raw notifications from the hook thread. Implementations must only enqueue work (hooks have
/// a hard timeout: slow low-level hook callbacks are silently removed by Windows).</summary>
internal interface IHookSink
{
    void Foreground(IntPtr hwnd);
    void Focus(IntPtr hwnd);
    void ValueChanged(IntPtr hwnd);
    void NameChanged(IntPtr hwnd);
    void SelectionChanged(IntPtr hwnd);
    void KeyDown(uint vk, Chord.Modifiers modifiers, bool injected);
    void Mouse(int message, Native.POINT point);
    void Session(bool active, string reason);
}

/// <summary>
/// One STA thread with a message loop that owns every Windows hook: WinEvent hooks (foreground,
/// focus, value, name, selection), listen-only WH_KEYBOARD_LL / WH_MOUSE_LL, and a message-only
/// window for WTS lock/unlock and suspend/resume. Low-level hooks never block or modify input.
/// </summary>
internal sealed class HookThread : IDisposable
{
    private readonly IHookSink _sink;
    private readonly ConcurrentQueue<Action> _calls = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private uint _threadId;
    private readonly List<IntPtr> _winEventHooks = [];
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private IntPtr _window;
    // Delegates must stay referenced while hooks are installed.
    private readonly Native.WinEventProc _winEventProc;
    private readonly Native.HookProc _keyboardProc;
    private readonly Native.HookProc _mouseProc;
    private readonly Native.WndProc _wndProc;
    private volatile bool _observing;
    private volatile bool _inputRunning;

    public bool InputRunning => _inputRunning;
    public bool WinEventsInstalled { get; private set; }

    public HookThread(IHookSink sink)
    {
        _sink = sink;
        _winEventProc = OnWinEvent;
        _keyboardProc = OnKeyboard;
        _mouseProc = OnMouse;
        _wndProc = OnWindowMessage;
        _thread = new Thread(Run) { IsBackground = true, Name = "Side hook thread" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void Invoke(Action action)
    {
        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId)
        {
            action();
            return;
        }
        using var done = new ManualResetEventSlim();
        _calls.Enqueue(() => { try { action(); } finally { done.Set(); } });
        Native.PostThreadMessage(_threadId, Native.WM_APP, IntPtr.Zero, IntPtr.Zero);
        done.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>WinEvents + session notifications (always on while capture is unpaused).</summary>
    public void StartObserving() => Invoke(() =>
    {
        _observing = true;
        if (_winEventHooks.Count > 0) return;
        const uint flags = Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS;
        foreach (var eventType in new[]
                 {
                     Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_OBJECT_FOCUS, Native.EVENT_OBJECT_NAMECHANGE,
                     Native.EVENT_OBJECT_VALUECHANGE, Native.EVENT_OBJECT_TEXTSELECTIONCHANGED,
                 })
        {
            var hook = Native.SetWinEventHook(eventType, eventType, IntPtr.Zero, _winEventProc, 0, 0, flags);
            if (hook != IntPtr.Zero) _winEventHooks.Add(hook);
        }
        WinEventsInstalled = _winEventHooks.Count == 5;
    });

    public void StopObserving() => Invoke(() =>
    {
        _observing = false;
        foreach (var hook in _winEventHooks) Native.UnhookWinEvent(hook);
        _winEventHooks.Clear();
        WinEventsInstalled = false;
        StopInputCore();
    });

    public bool StartInput()
    {
        Invoke(() =>
        {
            if (_keyboardHook != IntPtr.Zero) return;
            var module = Native.GetModuleHandle(null);
            _keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyboardProc, module, 0);
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
            _inputRunning = _keyboardHook != IntPtr.Zero && _mouseHook != IntPtr.Zero;
            if (!_inputRunning) StopInputCore();
        });
        return _inputRunning;
    }

    public void StopInput() => Invoke(StopInputCore);

    private void StopInputCore()
    {
        if (_keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = IntPtr.Zero;
        _mouseHook = IntPtr.Zero;
        _inputRunning = false;
    }

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        // Force creation of the thread message queue before signalling readiness.
        Native.PostThreadMessage(_threadId, 0, IntPtr.Zero, IntPtr.Zero);
        CreateMessageWindow();
        _ready.Set();
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.hwnd == IntPtr.Zero && msg.message == Native.WM_APP)
            {
                while (_calls.TryDequeue(out var call)) call();
                continue;
            }
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        foreach (var hook in _winEventHooks) Native.UnhookWinEvent(hook);
        StopInputCore();
        if (_window != IntPtr.Zero)
        {
            Native.WTSUnRegisterSessionNotification(_window);
            Native.DestroyWindow(_window);
        }
    }

    private void CreateMessageWindow()
    {
        var className = "SideCaptureSession" + Environment.ProcessId;
        var cls = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = className,
        };
        Native.RegisterClassEx(ref cls);
        _window = Native.CreateWindowEx(0, className, "Side capture", 0, 0, 0, 0, 0, Native.HWND_MESSAGE, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
        if (_window != IntPtr.Zero) Native.WTSRegisterSessionNotification(_window, Native.NOTIFY_FOR_THIS_SESSION);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_observing)
        {
            if (msg == Native.WM_WTSSESSION_CHANGE)
            {
                var code = wParam.ToInt32();
                if (code == Native.WTS_SESSION_LOCK) _sink.Session(false, "screen-locked");
                else if (code == Native.WTS_SESSION_UNLOCK) _sink.Session(true, "screen-unlocked");
            }
            else if (msg == Native.WM_POWERBROADCAST)
            {
                var code = wParam.ToInt32();
                if (code == Native.PBT_APMSUSPEND) _sink.Session(false, "sleep");
                else if (code is Native.PBT_APMRESUMESUSPEND or Native.PBT_APMRESUMEAUTOMATIC) _sink.Session(true, "wake");
            }
        }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!_observing) return;
        switch (eventType)
        {
            case Native.EVENT_SYSTEM_FOREGROUND:
                _sink.Foreground(hwnd);
                break;
            case Native.EVENT_OBJECT_FOCUS:
                _sink.Focus(hwnd);
                break;
            case Native.EVENT_OBJECT_NAMECHANGE:
                // Name changes fire system-wide; only the foreground window title matters.
                if (idObject == Native.OBJID_WINDOW && idChild == Native.CHILDID_SELF && hwnd == Native.GetForegroundWindow())
                    _sink.NameChanged(hwnd);
                break;
            case Native.EVENT_OBJECT_VALUECHANGE:
                _sink.ValueChanged(hwnd);
                break;
            case Native.EVENT_OBJECT_TEXTSELECTIONCHANGED:
                _sink.SelectionChanged(hwnd);
                break;
        }
    }

    private IntPtr OnKeyboard(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _inputRunning)
        {
            var message = wParam.ToInt32();
            if (message is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN)
            {
                var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                var modifiers = Chord.Modifiers.None;
                if (Down(0x11)) modifiers |= Chord.Modifiers.Control;
                if (Down(0x12)) modifiers |= Chord.Modifiers.Alt;
                if (Down(0x10)) modifiers |= Chord.Modifiers.Shift;
                if (Down(0x5B) || Down(0x5C)) modifiers |= Chord.Modifiers.Win;
                _sink.KeyDown(data.vkCode, modifiers, (data.flags & Native.LLKHF_INJECTED) != 0);
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        static bool Down(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _inputRunning)
        {
            var message = wParam.ToInt32();
            if (message is Native.WM_LBUTTONDOWN or Native.WM_LBUTTONUP or Native.WM_RBUTTONDOWN or Native.WM_MOUSEMOVE)
            {
                var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                _sink.Mouse(message, data.pt);
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
