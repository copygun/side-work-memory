namespace Side.Win.Core;

// Contracts between Core and the Capture/UI layers. Core never references concrete Capture or UI
// types; src/App/Composition.cs wires the implementations together.
//
// App identity on Windows: the "bundleId" wire field carries the lowercase executable file name
// (e.g. "chrome.exe"). Window identity: "windowId" carries the top-level HWND as a positive int.
// Observation "source" stays "mac_ax" on the wire: the daemon/ledger treat it as "native OS
// accessibility capture" and changing it would fork the ledger schema.

/// <summary>
/// Frontmost app + window. Equality is used to detect focus changes during a capture, so it compares
/// only the identity (app, pid, window) like the macOS helper; a title change (page load) is not a
/// focus change.
/// </summary>
public sealed record ForegroundInfo(string BundleId, string AppName, int Pid, int? WindowId, string WindowTitle)
{
    public bool Equals(ForegroundInfo? other) =>
        other is not null && BundleId == other.BundleId && Pid == other.Pid && WindowId == other.WindowId;

    public override int GetHashCode() => HashCode.Combine(BundleId, Pid, WindowId);
}

public sealed record RouterApplication(string BundleId, string Name, bool Denied);

public sealed record RouterIcon(string BundleId, string IconPngBase64);

public sealed record ObserverConfiguration(
    IReadOnlyList<string> DeniedBundleIds, bool CaptureTypedText, bool ScreenOcr, bool Paused);

/// <summary>PermissionStatus wire shape (HelperPermissionsSchema, strict).</summary>
public sealed record PermissionStatus(
    bool Accessibility, bool InputMonitoring, bool ScreenRecording, IReadOnlyDictionary<string, bool> Automation);

public enum PermissionKind { Accessibility, InputMonitoring, ScreenRecording, Automation }

public sealed record BrowserCaptureContent(string? Text, int AxTreeBytes);

public abstract record BrowserCaptureResult
{
    /// <summary>Normalized page URL plus the content read while that URL was verified.</summary>
    public sealed record Captured(string Url, BrowserCaptureContent Content, bool NeedsOcr) : BrowserCaptureResult;
    /// <summary>Private/incognito window: never capture.</summary>
    public sealed record Suppressed : BrowserCaptureResult;
    public sealed record Unavailable : BrowserCaptureResult;
}

/// <summary>
/// Everything the command router and health sampler need from the capture layer.
/// Implemented in src/Capture. All members must be safe to call from a worker thread.
/// </summary>
public interface ICaptureLayer : IDisposable
{
    /// <summary>Called once by the runtime. Observations (RawObservation objects serialised with
    /// camelCase) are pushed through <paramref name="emit"/>.</summary>
    void Initialize(Action<object> emit);

    ForegroundInfo? Foreground { get; }
    /// <summary>True while a password field has keyboard focus (Windows analogue of Secure Input).</summary>
    bool SecureInputEnabled { get; }
    bool IsIdle { get; }
    bool InputHookRunning { get; }
    int ObserverRegistrationFailures { get; }
    bool ScreenOcrAvailable { get; }
    IReadOnlyList<string> ScreenOcrLanguages { get; }

    bool MayObserve(string bundleId);
    bool IsBrowser(string bundleId);

    /// <summary>Accessibility (UI Automation) text snapshot of the given app/window, or null.</summary>
    string? CaptureUia(string bundleId, int? windowId);
    BrowserCaptureResult CaptureBrowser(string bundleId, Func<BrowserCaptureContent> readContent);
    BrowserCaptureResult BrowserUrl(string bundleId);
    /// <summary>Unnormalised address-bar value, used to detect navigation during OCR.</summary>
    string? RawBrowserUrl(string bundleId);
    Task<string?> CaptureOcrAsync(int windowId, CancellationToken cancellationToken = default);

    /// <summary>Apply observer.configure. Paused stops hooks/observers; unpaused starts them.</summary>
    void Configure(ObserverConfiguration configuration, Func<string, bool> isDenied);
    /// <summary>Stop all hooks and observers (daemon gone or quitting).</summary>
    void PauseAll();

    IReadOnlyList<RouterApplication> Applications(Func<string, bool> isDenied);
    IReadOnlyList<RouterIcon> Icons(IReadOnlyList<string> bundleIds);

    /// <summary>Fill browser/adapter specific health fields (perApp, asideAdapter).</summary>
    void WriteHealth(HelperHealth health);
}

/// <summary>What Core asks from the UI layer. Implemented in src/UI; calls may arrive on any thread.</summary>
public interface IUiLayer
{
    void OnSupervisorStateChanged(SupervisorState state);
    void OnWebSessionChanged(WebSession? session);
    void OpenSettings();
    /// <summary>Show the OS page where a permission-like setting lives (e.g. screen capture privacy).</summary>
    void ShowPermissionHelp(PermissionKind kind);
}

public sealed record WebSession(int Port, string Token)
{
    public static WebSession? TryCreate(int port, string token)
    {
        if (port is < 1 or > 65_535 || token.Length != 64) return null;
        foreach (var c in token) if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return null;
        return new WebSession(port, token);
    }

    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");
}
