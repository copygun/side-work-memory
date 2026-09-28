using System.Text;

namespace Side.Win.Capture;

/// <summary>
/// Port of AXObserverHub. macOS AXObserver notifications are replaced by out-of-context WinEvents
/// (focus, value, name, text selection) delivered by <see cref="HookThread"/>; the element itself is
/// read through UI Automation. Every method runs on the single capture worker thread.
/// </summary>
internal sealed class FocusObserver(CaptureStream stream)
{
    private const int MaxValueUtf16 = 20_000;
    private const int MaxPendingUtf8 = 4_096;
    private const int MaxSelectionUtf8 = 600;
    private static readonly TimeSpan DraftIdle = TimeSpan.FromSeconds(300);
    private const string SentenceEnds = ".!?。！？\n";

    private string? _bundleId;
    private int _pid;
    private IntPtr _hwnd;
    private string? _windowTitle;
    private IUIAutomationElement? _focused;
    private FieldMetadata? _field;
    private string? _previousValue;
    private string _pendingText = "";
    private DateTimeOffset? _lastInputAt;
    private string? _lastSelection;

    public string? FocusedTextFieldLabel { get; private set; }
    public bool IsTextFieldFocused { get; private set; }
    public int RegistrationFailures { get; private set; }

    public void Activate(string bundleId, string appName, int pid, IntPtr hwnd)
    {
        Stop();
        _bundleId = bundleId;
        _pid = pid;
        _hwnd = hwnd;
        if (!stream.Activate(bundleId, appName)) return;
        _windowTitle = Native.WindowTitle(hwnd);
        stream.Emit(CaptureEvent.WindowChanged, bundleId, windowTitle: _windowTitle, reason: "activation");
        if (Uia.Client is null)
        {
            RegistrationFailures = 1;
            return;
        }
        RegistrationFailures = 0;
        RefreshFocusedElement();
    }

    /// <summary>Another top-level window of the already active app came to the front.</summary>
    public void WindowFocused(IntPtr hwnd)
    {
        if (_bundleId is null || !stream.MayObserve(_bundleId)) return;
        _hwnd = hwnd;
        _windowTitle = Native.WindowTitle(hwnd);
        stream.Emit(CaptureEvent.WindowChanged, _bundleId, windowTitle: _windowTitle, reason: "focus");
    }

    public void TitleChanged(IntPtr hwnd)
    {
        if (_bundleId is null || hwnd != _hwnd || !stream.MayObserve(_bundleId)) return;
        var title = Native.WindowTitle(hwnd);
        if (title == _windowTitle) return;
        _windowTitle = title;
        stream.Emit(CaptureEvent.WindowChanged, _bundleId, windowTitle: title, reason: "title");
    }

    public void Stop()
    {
        FlushTypedText();
        _bundleId = null;
        _pid = 0;
        _hwnd = IntPtr.Zero;
        _windowTitle = null;
        _focused = null;
        _field = null;
        _previousValue = null;
        _pendingText = "";
        _lastInputAt = null;
        _lastSelection = null;
        FocusedTextFieldLabel = null;
        IsTextFieldFocused = false;
    }

    public void Reconfigure()
    {
        if (_bundleId is not null && !stream.MayObserve(_bundleId)) Stop();
        else if (_bundleId is not null) RefreshFocusedElement();
    }

    public void FocusChanged()
    {
        if (_bundleId is null || !stream.MayObserve(_bundleId)) return;
        FlushTypedText();
        RefreshFocusedElement();
    }

    public void ValueMayHaveChanged()
    {
        if (_bundleId is null || _focused is null || _field is null) return;
        if (!ShouldReadValue(_bundleId, _field)) return;
        var element = _focused;
        var value = Uia.StringProperty(element, Uia.PropValueValue);
        if (value is not null) RecordValueChange(value, _bundleId);
    }

    public void SelectionChanged()
    {
        if (_bundleId is null || _focused is null || _field is null || stream.IsSecureInputEnabled) return;
        if (FieldLabels.BlockedRule(_field) is not null) return;
        var text = Uia.SelectedText(_focused, 4 * MaxSelectionUtf8);
        if (string.IsNullOrEmpty(text) || text == _lastSelection) return;
        _lastSelection = text;
        RecordSelection(text, _bundleId);
    }

    public void Tick()
    {
        if (_lastInputAt is { } last && DateTimeOffset.Now - last >= DraftIdle) FlushTypedText();
    }

    public void RecordSelection(string text, string bundleId)
    {
        if (!stream.MayObserve(bundleId) || stream.IsSecureInputEnabled || text.Length == 0) return;
        stream.Emit(CaptureEvent.SelectionChanged, bundleId, label: FocusedTextFieldLabel,
            text: TextLimits.PrefixUtf8(text, MaxSelectionUtf8));
    }

    /// <summary>Same append-only diff as macOS: only text appended to the previous value is buffered;
    /// completed sentences are emitted, the rest waits for submit/focus change/300 s idle.</summary>
    public void RecordValueChange(string value, string bundleId)
    {
        if (value.Length > MaxValueUtf16)
        {
            ResetDraft();
            _previousValue = null;
            return;
        }
        if (_previousValue is null || !value.StartsWith(_previousValue, StringComparison.Ordinal))
        {
            _previousValue = value;
            ResetDraft();
            return;
        }
        var appended = value[_previousValue.Length..];
        _previousValue = value;
        if (appended.Length == 0) return;
        _pendingText += appended;
        if (Encoding.UTF8.GetByteCount(_pendingText) > MaxPendingUtf8)
        {
            ResetDraft();
            return;
        }
        _lastInputAt = DateTimeOffset.Now;
        var buffered = _pendingText;
        var start = 0;
        var sentences = new List<string>();
        for (var i = 0; i < buffered.Length; i++)
        {
            if (!SentenceEnds.Contains(buffered[i])) continue;
            var sentence = buffered[start..(i + 1)].Trim();
            if (sentence.Length >= 3) sentences.Add(sentence);
            start = i + 1;
        }
        _pendingText = buffered[start..];
        if (_pendingText.Length == 0) _lastInputAt = null;
        foreach (var sentence in sentences)
            stream.Emit(CaptureEvent.KeyboardTextInput, bundleId, label: FocusedTextFieldLabel, text: sentence);
    }

    public void FlushTypedText()
    {
        if (_bundleId is null) return;
        var draft = _pendingText.Trim();
        _pendingText = "";
        _lastInputAt = null;
        if (draft.Length >= 3)
            stream.Emit(CaptureEvent.KeyboardTextInput, _bundleId, label: FocusedTextFieldLabel, text: draft);
    }

    private void ResetDraft()
    {
        _pendingText = "";
        _lastInputAt = null;
    }

    private bool ShouldReadValue(string bundleId, FieldMetadata field) =>
        stream.MayObserve(bundleId) && stream.CaptureTypedText && !stream.IsSecureInputEnabled &&
        IsTextFieldFocused && FieldLabels.BlockedRule(field) is null;

    private void RefreshFocusedElement()
    {
        _focused = null;
        _field = null;
        _previousValue = null;
        _lastSelection = null;
        FocusedTextFieldLabel = null;
        IsTextFieldFocused = false;
        ResetDraft();
        if (_bundleId is null) return;
        var automation = Uia.Client;
        if (automation is null) return;
        var element = Uia.Try(automation.GetFocusedElement);
        if (element is null) return;
        // Focus reported by UIA must belong to the active app (focus may already be elsewhere).
        if (_pid != 0 && Uia.ProcessId(element) != _pid && ForegroundReader.Current()?.Info.BundleId != _bundleId) return;
        var field = Uia.Metadata(element);
        if (FieldLabels.BlockedRule(field) is not null) return;
        _focused = element;
        _field = field;
        FocusedTextFieldLabel = field.Title ?? field.Description ?? field.Placeholder;
        IsTextFieldFocused = Uia.IsTextField(element);
        if (ShouldReadValue(_bundleId, field)) _previousValue = Uia.StringProperty(element, Uia.PropValueValue);
    }
}
