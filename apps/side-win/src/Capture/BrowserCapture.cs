using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Side.Win.Core;

namespace Side.Win.Capture;

/// <summary>
/// Chromium browsers on Windows (port of Browsers.swift). AppleScript is replaced by UI Automation:
/// the URL is the value of the omnibox (first Edit outside the web Document) and private windows are
/// detected from the window title or the "Incognito"/"InPrivate" toolbar button. Any uncertainty is
/// reported as Unavailable so capture fails closed.
/// </summary>
internal sealed class BrowserCapture
{
    public static readonly HashSet<string> Browsers =
        new(["chrome.exe", "msedge.exe", "brave.exe", "whale.exe", "aside.exe", "arc.exe"], StringComparer.OrdinalIgnoreCase);

    private const int EmptyTreeBytes = 2_048;
    private static readonly Regex PrivateMarker = new(
        @"(incognito|inprivate|시크릿|private browsing|private window|\bprivate\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly TimeSpan PrivacyTtl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<IntPtr, (bool Private, DateTimeOffset At)> _privacy = new();
    private readonly ConcurrentDictionary<IntPtr, IUIAutomationElement> _omnibox = new();
    private readonly ConcurrentDictionary<string, PerAppHealth> _perApp = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsBrowser(string bundleId) => Browsers.Contains(bundleId);

    private sealed record Page(string RawUrl, string NormalizedUrl);

    private enum Failure { None, Suppressed, Unavailable }

    public void WriteHealth(HelperHealth health)
    {
        foreach (var (bundle, value) in _perApp) health.PerApp[bundle] = value;
    }

    /// <summary>Called before any event is emitted: private windows never enter the pipe.</summary>
    public bool IsObservableForeground(string bundleId)
    {
        if (!IsBrowser(bundleId)) return true;
        var foreground = ForegroundReader.Current();
        if (foreground is null || foreground.Info.BundleId != bundleId) return false;
        return IsPrivate(foreground.Hwnd) == false;
    }

    public string? CurrentNormalizedUrl(string bundleId) =>
        Resolve(bundleId, out var page) == Failure.None ? page!.NormalizedUrl : null;

    public string? RawUrl(string bundleId) => Resolve(bundleId, out var page) == Failure.None ? page!.RawUrl : null;

    public BrowserCaptureResult Capture(string bundleId, Func<BrowserCaptureContent> readContent) =>
        Capture(bundleId, readContent, recordHealth: true);

    private BrowserCaptureResult Capture(string bundleId, Func<BrowserCaptureContent> readContent, bool recordHealth)
    {
        switch (Resolve(bundleId, out var before))
        {
            case Failure.Suppressed: return new BrowserCaptureResult.Suppressed();
            case Failure.Unavailable: return new BrowserCaptureResult.Unavailable();
        }
        var content = readContent();
        switch (Resolve(bundleId, out var after))
        {
            case Failure.Suppressed: return new BrowserCaptureResult.Suppressed();
            case Failure.Unavailable: return new BrowserCaptureResult.Unavailable();
        }
        if (before!.RawUrl != after!.RawUrl) return new BrowserCaptureResult.Unavailable();
        var bytes = Math.Max(0, content.AxTreeBytes);
        var needsOcr = bytes < EmptyTreeBytes;
        if (recordHealth)
            _perApp.AddOrUpdate(bundleId,
            _ => new PerAppHealth(needsOcr, bytes),
            (_, previous) => new PerAppHealth(needsOcr, Math.Max(previous.MaxTreeBytes, bytes)));
        return new BrowserCaptureResult.Captured(before.NormalizedUrl, content, needsOcr);
    }

    /// <summary>URL only (browser.url): same before/after verification, no health side effects.</summary>
    public BrowserCaptureResult Url(string bundleId) =>
        Capture(bundleId, () => new BrowserCaptureContent(null, 0), recordHealth: false);

    private Failure Resolve(string bundleId, out Page? page)
    {
        page = null;
        if (!IsBrowser(bundleId)) return Failure.Unavailable;
        var foreground = ForegroundReader.Current();
        if (foreground is null || foreground.Info.BundleId != bundleId) return Failure.Unavailable;
        switch (IsPrivate(foreground.Hwnd))
        {
            case true: return Failure.Suppressed;
            case null: return Failure.Unavailable;
        }
        var raw = OmniboxValue(foreground.Hwnd)?.Trim();
        if (string.IsNullOrEmpty(raw) || NormalizeUrl(raw) is not { } normalized) return Failure.Unavailable;
        page = new Page(raw, normalized);
        return Failure.None;
    }

    /// <summary>true = private window, false = normal, null = unknown (fail closed).</summary>
    private bool? IsPrivate(IntPtr hwnd)
    {
        if (_privacy.TryGetValue(hwnd, out var cached) && DateTimeOffset.Now - cached.At < PrivacyTtl) return cached.Private;
        if (PrivateMarker.IsMatch(Native.WindowTitle(hwnd)))
        {
            _privacy[hwnd] = (true, DateTimeOffset.Now);
            return true;
        }
        var automation = Uia.Client;
        var window = automation is null ? null : Uia.Try(() => automation.ElementFromHandle(hwnd));
        if (window is null) return null;
        var marker = UiaSnapshot.FindFirst(window, element =>
        {
            var type = Uia.ControlType(element);
            if (type != Uia.ControlTypeButton && type != Uia.ControlTypeText) return false;
            var name = Uia.Name(element);
            return !string.IsNullOrEmpty(name) && name.Length <= 40 && PrivateMarker.IsMatch(name);
        }, skipDocuments: true);
        // The toolbar must actually be exposed; without an omnibox we cannot vouch for "normal".
        if (marker is null && FindOmnibox(window) is null) return null;
        var verdict = marker is not null;
        _privacy[hwnd] = (verdict, DateTimeOffset.Now);
        if (_privacy.Count > 256)
            foreach (var key in _privacy.Keys) if (!Native.IsWindow(key)) _privacy.TryRemove(key, out _);
        return verdict;
    }

    private static IUIAutomationElement? FindOmnibox(IUIAutomationElement window) =>
        UiaSnapshot.FindFirst(window, element => Uia.ControlType(element) == Uia.ControlTypeEdit, skipDocuments: true);

    private string? OmniboxValue(IntPtr hwnd)
    {
        if (_omnibox.TryGetValue(hwnd, out var cached))
        {
            var value = Read(cached);
            if (value is not null) return value;
            _omnibox.TryRemove(hwnd, out _);
        }
        var automation = Uia.Client;
        var window = automation is null ? null : Uia.Try(() => automation.ElementFromHandle(hwnd));
        if (window is null) return null;
        var omnibox = FindOmnibox(window);
        if (omnibox is null) return null;
        _omnibox[hwnd] = omnibox;
        if (_omnibox.Count > 64)
            foreach (var key in _omnibox.Keys) if (!Native.IsWindow(key)) _omnibox.TryRemove(key, out _);
        return Read(omnibox);

        static string? Read(IUIAutomationElement element)
        {
            // While the user edits the omnibox its value is typed text, not the page URL.
            if (Uia.HasFocus(element)) return null;
            return Uia.StringProperty(element, Uia.PropValueValue);
        }
    }

    /// <summary>
    /// http(s) only; lowercase scheme/host; drop credentials, query and fragment; empty path -> "/".
    /// Chromium elides "https://" in the omnibox, so a scheme-less value is read as https.
    /// </summary>
    public static string? NormalizeUrl(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0 || text.Contains(' ')) return null;
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            if (!text.Contains('.') && !text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)) return null;
            text = "https://" + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme != "http" && scheme != "https") return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port;
        var path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        return $"{scheme}://{uri.Host.ToLowerInvariant()}{port}{path}";
    }
}
