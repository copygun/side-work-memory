using System.Text.Json.Serialization;
using Side.Win.Core;

namespace Side.Win.Capture;

/// <summary>RawObservation wire shape (daemon ObservationSchema). Null fields are omitted like Swift's encoder.</summary>
public sealed record CaptureEvent(
    string Kind,
    long OccurredAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BundleId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AppName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WindowTitle,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Url,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? WindowId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Role,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Chord,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason)
{
    public string Source => "mac_ax";

    public const string SessionStarted = "session.started";
    public const string SessionEnded = "session.ended";
    public const string WindowChanged = "window.changed";
    public const string MouseClick = "mouse.click";
    public const string MouseContextMenu = "mouse.context_menu";
    public const string MouseDrag = "mouse.drag";
    public const string KeyboardShortcut = "keyboard.shortcut";
    public const string KeyboardSubmit = "keyboard.submit";
    public const string KeyboardTextInput = "keyboard.text_input";
    public const string SelectionChanged = "selection.changed";
}

/// <summary>Gatekeeper for every emitted observation (port of CaptureStream in AXObserverHub.swift).</summary>
internal sealed class CaptureStream(
    Action<object> output, Func<bool> secureInputEnabled, BrowserCapture browser)
{
    private readonly object _gate = new();
    private Func<string, bool> _isExcluded = _ => true;
    private bool _captureTypedText = true;
    private bool _paused = true;
    private string? _activeBundleId;
    private string? _activeAppName;

    public bool IsSecureInputEnabled => secureInputEnabled();
    public bool CaptureTypedText { get { lock (_gate) return _captureTypedText; } }
    public string? ActiveBundleId { get { lock (_gate) return _activeBundleId; } }

    public void Configure(Func<string, bool> isExcluded, bool captureTypedText, bool paused)
    {
        lock (_gate)
        {
            _isExcluded = isExcluded;
            _captureTypedText = captureTypedText;
            _paused = paused;
        }
    }

    public void Pause() { lock (_gate) _paused = true; }

    public bool Activate(string bundleId, string appName)
    {
        lock (_gate)
        {
            _activeBundleId = bundleId;
            _activeAppName = appName;
        }
        return MayObserve(bundleId);
    }

    public bool IsExcluded(string bundleId)
    {
        Func<string, bool> excluded;
        lock (_gate) excluded = _isExcluded;
        return excluded(bundleId) || LiveRouterServices.IsHardDenied(bundleId) ||
               string.Equals(bundleId, LiveRouterServices.OwnExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    public bool MayObserve(string bundleId)
    {
        lock (_gate)
        {
            if (_paused || string.IsNullOrEmpty(bundleId) || _activeBundleId != bundleId) return false;
        }
        if (IsExcluded(bundleId)) return false;
        return browser.IsObservableForeground(bundleId);
    }

    public void Emit(string kind, string bundleId, string? windowTitle = null, string? role = null,
        string? label = null, string? chord = null, string? text = null, string? reason = null)
    {
        if (!MayObserve(bundleId)) return;
        if (kind is CaptureEvent.KeyboardShortcut or CaptureEvent.KeyboardSubmit or
            CaptureEvent.KeyboardTextInput or CaptureEvent.SelectionChanged && IsSecureInputEnabled) return;
        if (kind == CaptureEvent.KeyboardTextInput && !CaptureTypedText) return;
        string? url = null;
        if (BrowserCapture.IsBrowser(bundleId))
        {
            url = browser.CurrentNormalizedUrl(bundleId);
            if (url is null) return;
        }
        var foreground = ForegroundReader.Current();
        var windowId = foreground?.Info.BundleId == bundleId ? foreground.Info.WindowId : null;
        string? appName;
        lock (_gate) appName = _activeAppName;
        output(new CaptureEvent(kind, NowMillis(), bundleId, appName, windowTitle, url, windowId, role, label, chord, text, reason));
    }

    public void EmitSession(string kind, string reason)
    {
        lock (_gate) { if (_paused) return; }
        output(new CaptureEvent(kind, NowMillis(), null, null, null, null, null, null, null, null, null, reason));
    }

    public static long NowMillis() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
