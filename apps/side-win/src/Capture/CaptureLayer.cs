using System.Collections.Concurrent;
using Side.Win.Core;

namespace Side.Win.Capture;

/// <summary>
/// Windows capture layer: InputTap + AXObserverHub + WorkspaceObserver + BrowserCapture + OCR.
/// Threading: one STA hook thread (message loop, hooks only enqueue) and one MTA capture worker that
/// runs every observer/UIA step in order. Router-facing members (CaptureUia, BrowserUrl, OCR, ...)
/// run on the caller's thread-pool (MTA) thread; the shared UIA client is free-threaded.
/// </summary>
public sealed class CaptureLayer : ICaptureLayer, IHookSink
{
    public static readonly TimeSpan IdleThreshold = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan SecureCacheTtl = TimeSpan.FromMilliseconds(300);

    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _worker;
    private readonly BrowserCapture _browser = new();
    private CaptureStream? _stream;
    private FocusObserver? _hub;
    private HookThread? _hooks;
    private System.Threading.Timer? _tick;
    private readonly Debouncer _valueRead;
    private readonly Debouncer _selectionRead;

    // Workspace (session) state — worker thread only.
    private bool _observing;
    private bool _sessionActive;
    // Pointer state — hook thread only.
    private Native.POINT? _dragStart;
    private bool _dragReported;
    // Foreground pid used by hook callbacks to drop background noise cheaply.
    private volatile int _foregroundPid;

    private readonly object _secureGate = new();
    private (bool Value, DateTimeOffset At) _secure;

    public CaptureLayer()
    {
        _worker = new Thread(WorkLoop) { IsBackground = true, Name = "Side capture worker" };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
        _valueRead = new Debouncer(TimeSpan.FromMilliseconds(150), () => Post(() => _hub?.ValueMayHaveChanged()));
        _selectionRead = new Debouncer(TimeSpan.FromMilliseconds(400), () => Post(() => _hub?.SelectionChanged()));
    }

    public void Initialize(Action<object> emit)
    {
        _stream = new CaptureStream(emit, () => SecureInputEnabled, _browser);
        _hub = new FocusObserver(_stream);
        _hooks = new HookThread(this);
        _tick = new System.Threading.Timer(_ => Post(() => _hub?.Tick()), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        // Warm the UIA client on an MTA thread.
        Post(() => _ = Uia.Client);
    }

    // ------------------------------------------------------------------ worker plumbing

    private void WorkLoop()
    {
        foreach (var action in _work.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception) { /* one bad element must not kill capture */ }
        }
    }

    private void Post(Action action)
    {
        if (!_work.IsAddingCompleted) _work.Add(action);
    }

    private void Invoke(Action action)
    {
        if (Environment.CurrentManagedThreadId == _worker.ManagedThreadId)
        {
            action();
            return;
        }
        using var done = new ManualResetEventSlim();
        Post(() => { try { action(); } finally { done.Set(); } });
        done.Wait(TimeSpan.FromSeconds(10));
    }

    // ------------------------------------------------------------------ ICaptureLayer: state

    public ForegroundInfo? Foreground => ForegroundReader.Current()?.Info;

    public bool SecureInputEnabled
    {
        get
        {
            lock (_secureGate)
            {
                if (DateTimeOffset.Now - _secure.At < SecureCacheTtl) return _secure.Value;
            }
            var automation = Uia.Client;
            var focused = automation is null ? null : Uia.Try(automation.GetFocusedElement);
            var value = focused is not null && Uia.IsPassword(focused);
            lock (_secureGate) _secure = (value, DateTimeOffset.Now);
            return value;
        }
    }

    public bool IsIdle
    {
        get
        {
            var info = new Native.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.LASTINPUTINFO>() };
            if (!Native.GetLastInputInfo(ref info)) return false;
            var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
            return idleMs > IdleThreshold.TotalMilliseconds;
        }
    }

    public bool InputHookRunning => _hooks?.InputRunning ?? false;

    public int ObserverRegistrationFailures =>
        (_observing && _hooks is { WinEventsInstalled: false } ? 1 : 0) + (_hub?.RegistrationFailures ?? 0);

    public bool ScreenOcrAvailable => WindowOcr.Available;
    public IReadOnlyList<string> ScreenOcrLanguages => WindowOcr.Languages;

    public bool MayObserve(string bundleId) => _stream?.MayObserve(bundleId) ?? false;
    public bool IsBrowser(string bundleId) => BrowserCapture.IsBrowser(bundleId);

    // ------------------------------------------------------------------ ICaptureLayer: capture

    public string? CaptureUia(string bundleId, int? windowId)
    {
        var before = ForegroundReader.Current();
        if (before is null || before.Info.BundleId != bundleId || (windowId is not null && before.Info.WindowId != windowId))
            return null;
        var automation = Uia.Client;
        var window = automation is null ? null : Uia.Try(() => automation.ElementFromHandle(before.Hwnd));
        if (window is null) return null;
        var root = window;
        if (BrowserCapture.IsBrowser(bundleId))
        {
            // Web content only: skip tab strip/toolbar noise when the Document is exposed.
            root = UiaSnapshot.FindFirst(window, e => Uia.ControlType(e) == Uia.ControlTypeDocument, skipDocuments: false) ?? window;
        }
        var text = UiaSnapshot.Extract(root);
        var after = ForegroundReader.Current();
        if (after is null || after.Hwnd != before.Hwnd || after.Info.Pid != before.Info.Pid) return null;
        return text;
    }

    public BrowserCaptureResult CaptureBrowser(string bundleId, Func<BrowserCaptureContent> readContent) =>
        _browser.Capture(bundleId, readContent);

    public BrowserCaptureResult BrowserUrl(string bundleId) => _browser.Url(bundleId);

    public string? RawBrowserUrl(string bundleId) => _browser.RawUrl(bundleId);

    public Task<string?> CaptureOcrAsync(int windowId, CancellationToken cancellationToken = default) =>
        WindowOcr.CaptureTextAsync(new IntPtr(windowId), cancellationToken);

    public void Configure(ObserverConfiguration configuration, Func<string, bool> isDenied) => Invoke(() =>
    {
        var denied = new HashSet<string>(configuration.DeniedBundleIds, StringComparer.OrdinalIgnoreCase);
        _stream?.Configure(id => denied.Contains(id) || isDenied(id), configuration.CaptureTypedText, configuration.Paused);
        _hub?.Reconfigure();
        if (configuration.Paused)
        {
            _hooks?.StopInput();
            StopWorkspace();
        }
        else
        {
            StartWorkspace();
            _hooks?.StartInput();
        }
    });

    public void PauseAll() => Invoke(() =>
    {
        _stream?.Pause();
        _hooks?.StopInput();
        StopWorkspace();
    });

    public IReadOnlyList<RouterApplication> Applications(Func<string, bool> isDenied) => AppCatalog.Applications(isDenied);
    public IReadOnlyList<RouterIcon> Icons(IReadOnlyList<string> bundleIds) => AppCatalog.Icons(bundleIds);

    public void WriteHealth(HelperHealth health) => _browser.WriteHealth(health);

    // ------------------------------------------------------------------ workspace (session + activation)

    private void StartWorkspace()
    {
        if (_observing) return;
        _observing = true;
        _hooks?.StartObserving();
        RecordSessionStarted("startup");
        ActivateForeground();
    }

    private void StopWorkspace()
    {
        if (!_observing) return;
        _hooks?.StopObserving();
        _hub?.Stop();
        _sessionActive = false;
        _observing = false;
    }

    private void RecordSessionStarted(string reason)
    {
        if (_sessionActive) return;
        _sessionActive = true;
        _stream?.EmitSession(CaptureEvent.SessionStarted, reason);
    }

    private void RecordSessionEnded(string reason)
    {
        if (!_sessionActive) return;
        _sessionActive = false;
        _stream?.EmitSession(CaptureEvent.SessionEnded, reason);
    }

    private void ActivateForeground()
    {
        if (_hub is null || _stream is null) return;
        var current = ForegroundReader.Current();
        if (current is null) return;
        _foregroundPid = current.Info.Pid;
        if (_stream.ActiveBundleId == current.Info.BundleId)
        {
            _hub.WindowFocused(current.Hwnd);
            return;
        }
        _hub.Activate(current.Info.BundleId, current.Info.AppName, current.Info.Pid, current.Hwnd);
    }

    // ------------------------------------------------------------------ IHookSink (hook thread)

    void IHookSink.Foreground(IntPtr hwnd) => Post(() => { if (_observing) ActivateForeground(); });

    void IHookSink.Focus(IntPtr hwnd)
    {
        if (!FromForeground(hwnd)) return;
        lock (_secureGate) _secure = default;
        Post(() => { if (_observing) _hub?.FocusChanged(); });
    }

    void IHookSink.ValueChanged(IntPtr hwnd)
    {
        if (FromForeground(hwnd)) _valueRead.Trigger();
    }

    void IHookSink.NameChanged(IntPtr hwnd) => Post(() => { if (_observing) _hub?.TitleChanged(hwnd); });

    void IHookSink.SelectionChanged(IntPtr hwnd)
    {
        if (FromForeground(hwnd)) _selectionRead.Trigger();
    }

    private bool FromForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return true;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)_foregroundPid;
    }

    void IHookSink.KeyDown(uint vk, Chord.Modifiers modifiers, bool injected) => Post(() => HandleKey(vk, modifiers));

    private void HandleKey(uint vk, Chord.Modifiers modifiers)
    {
        if (_stream is null || _hub is null || !_observing) return;
        var bundleId = _stream.ActiveBundleId;
        if (bundleId is null || !_stream.MayObserve(bundleId) || _stream.IsSecureInputEnabled) return;
        if (vk == 0x0D && _hub.IsTextFieldFocused && modifiers == Chord.Modifiers.None)
        {
            _hub.ValueMayHaveChanged();
            _stream.Emit(CaptureEvent.KeyboardSubmit, bundleId, label: _hub.FocusedTextFieldLabel);
            _hub.FlushTypedText();
            return;
        }
        if (Chord.Notation(vk, modifiers) is { } chord)
        {
            _stream.Emit(CaptureEvent.KeyboardShortcut, bundleId, chord: chord);
            return;
        }
        // Fallback for editors that never raise EVENT_OBJECT_VALUECHANGE: re-read after typing pauses.
        if (_hub.IsTextFieldFocused && _stream.CaptureTypedText) _valueRead.Trigger();
    }

    void IHookSink.Mouse(int message, Native.POINT point)
    {
        switch (message)
        {
            case Native.WM_LBUTTONDOWN:
                _dragStart = point;
                _dragReported = false;
                Post(() => EmitPointer(CaptureEvent.MouseClick, point));
                break;
            case Native.WM_RBUTTONDOWN:
                Post(() => EmitPointer(CaptureEvent.MouseContextMenu, point));
                break;
            case Native.WM_MOUSEMOVE:
                if (_dragStart is { } start && !_dragReported &&
                    (Math.Abs(point.X - start.X) > 4 || Math.Abs(point.Y - start.Y) > 4))
                {
                    _dragReported = true;
                    Post(() => EmitPointer(CaptureEvent.MouseDrag, start));
                }
                break;
            case Native.WM_LBUTTONUP:
                _dragStart = null;
                _dragReported = false;
                break;
        }
    }

    private void EmitPointer(string kind, Native.POINT point)
    {
        if (_stream is null || !_observing) return;
        var foreground = ForegroundReader.Current();
        if (foreground is null || !_stream.MayObserve(foreground.Info.BundleId)) return;
        string? role = null;
        string? label = null;
        var automation = Uia.Client;
        var element = automation is null ? null : Uia.Try(() => automation.ElementFromPoint(point));
        if (element is not null && Uia.ProcessId(element) == foreground.Info.Pid)
        {
            var secure = Uia.IsPassword(element);
            role = secure ? FieldMetadata.SecureRole : RoleName(Uia.ControlType(element));
            label = Uia.Name(element);
            if (string.IsNullOrWhiteSpace(label)) label = Uia.Try(element.get_CurrentHelpText);
            if (string.IsNullOrWhiteSpace(label)) label = null;
            if (FieldLabels.BlockedRule(new FieldMetadata(Role: role, Title: label)) is not null) label = null;
            if (label is not null) label = TextLimits.PrefixUtf8(label, 200);
        }
        _stream.Emit(kind, foreground.Info.BundleId, role: role, label: label);
    }

    void IHookSink.Session(bool active, string reason) => Post(() =>
    {
        if (!_observing) return;
        if (active)
        {
            RecordSessionStarted(reason);
            ActivateForeground();
        }
        else
        {
            _hub?.Stop();
            RecordSessionEnded(reason);
        }
    });

    private static readonly Dictionary<int, string> RoleNames = new()
    {
        [50000] = "Button", [50001] = "Calendar", [50002] = "CheckBox", [50003] = "ComboBox", [50004] = "Edit",
        [50005] = "Hyperlink", [50006] = "Image", [50007] = "ListItem", [50008] = "List", [50009] = "Menu",
        [50010] = "MenuBar", [50011] = "MenuItem", [50012] = "ProgressBar", [50013] = "RadioButton",
        [50014] = "ScrollBar", [50015] = "Slider", [50016] = "Spinner", [50017] = "StatusBar", [50018] = "Tab",
        [50019] = "TabItem", [50020] = "Text", [50021] = "ToolBar", [50022] = "ToolTip", [50023] = "Tree",
        [50024] = "TreeItem", [50025] = "Custom", [50026] = "Group", [50027] = "Thumb", [50028] = "DataGrid",
        [50029] = "DataItem", [50030] = "Document", [50031] = "SplitButton", [50032] = "Window", [50033] = "Pane",
        [50034] = "Header", [50035] = "HeaderItem", [50036] = "Table", [50037] = "TitleBar", [50038] = "Separator",
    };

    private static string? RoleName(int controlType) => RoleNames.GetValueOrDefault(controlType);

    public void Dispose()
    {
        try { PauseAll(); } catch (Exception) { }
        _tick?.Dispose();
        _valueRead.Dispose();
        _selectionRead.Dispose();
        _hooks?.Dispose();
        _work.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>Trailing-edge debounce; the action only posts work to the capture worker.</summary>
    private sealed class Debouncer(TimeSpan delay, Action action) : IDisposable
    {
        private readonly System.Threading.Timer _timer = new(_ => action(), null, Timeout.Infinite, Timeout.Infinite);
        public void Trigger() => _timer.Change(delay, Timeout.InfiniteTimeSpan);
        public void Dispose() => _timer.Dispose();
    }
}
