using System.Text.Json;
using System.Text.Json.Nodes;

namespace Side.Win.Core;

/// <summary>Services the router needs; LiveRouterServices adapts ICaptureLayer + policy state.</summary>
public interface IRouterServices
{
    ForegroundInfo? Foreground { get; }
    bool SecureInputEnabled { get; }
    bool ScreenOcrEnabled { get; }
    bool ScreenRecordingTrusted { get; }
    HelperHealth HealthSnapshot();
    PermissionStatus PermissionStatus();
    PermissionStatus RequestPermissions(IReadOnlySet<PermissionKind> kinds);
    IReadOnlyList<RouterApplication> Applications();
    IReadOnlyList<RouterIcon> Icons(IReadOnlyList<string> bundleIds);
    bool IsDenied(string bundleId);
    bool MayObserve(string bundleId);
    bool IsBrowser(string bundleId);
    string? CaptureUia(string bundleId, int? windowId);
    BrowserCaptureResult CaptureBrowser(string bundleId, Func<BrowserCaptureContent> readContent);
    BrowserCaptureResult BrowserUrl(string bundleId);
    string? RawBrowserUrl(string bundleId);
    Task<string?> CaptureOcrAsync(int windowId);
    void Configure(ObserverConfiguration configuration);
    void OpenSettings();
}

/// <summary>Observation returned by capture.request / ocr.window (RouterObservation in Swift).</summary>
public sealed record RouterObservation(
    long OccurredAt, string Kind, string BundleId, string? Url, int? WindowId, string Content, string Shape)
{
    public string Source => "mac_ax";
}

/// <summary>Port of CommandRouter.swift. Every public path returns exactly one correlated frame or null.</summary>
public sealed class CommandRouter(IRouterServices services, Func<long>? nowMillis = null)
{
    private static readonly string[] Triggers = ["interaction", "activation", "navigation", "sweep", "discovery"];
    /// <summary>Below this many UTF-8 bytes of accessibility text, OCR is attempted (same as macOS).</summary>
    public const int OcrFallbackBytes = 2_048;

    private readonly Func<long> _now = nowMillis ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static readonly Dictionary<string, PermissionKind> PermissionNames = new()
    {
        ["accessibility"] = PermissionKind.Accessibility,
        ["inputMonitoring"] = PermissionKind.InputMonitoring,
        ["screenRecording"] = PermissionKind.ScreenRecording,
        ["automation"] = PermissionKind.Automation,
    };

    public async Task<byte[]?> ReplyAsync(byte[] line)
    {
        JsonObject? message;
        try { message = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
        if (message?["type"]?.GetValue<string>() != "command") return null;
        if (message["id"] is not JsonValue idNode || !idNode.TryGetValue<string>(out var id)) return null;
        if (message["name"] is not JsonValue nameNode || !nameNode.TryGetValue<string>(out var name)) return null;
        var args = message["args"] as JsonObject;

        switch (name)
        {
            case "health":
                return Success(id, services.HealthSnapshot());
            case "permissions":
                return Success(id, services.PermissionStatus());
            case "requestPermissions":
            {
                if (args?["kinds"] is not JsonArray kindsNode) return Failure(id, "invalid-arguments");
                var kinds = new HashSet<PermissionKind>();
                foreach (var kind in kindsNode)
                {
                    if (kind is not JsonValue value || !value.TryGetValue<string>(out var text) ||
                        !PermissionNames.TryGetValue(text, out var parsed)) return Failure(id, "invalid-arguments");
                    kinds.Add(parsed);
                }
                return Success(id, services.RequestPermissions(kinds));
            }
            case "applications.list":
                return Success(id, services.Applications());
            case "applications.icons":
            {
                if (StringArray(args?["bundleIds"]) is not { } ids) return Failure(id, "invalid-arguments");
                return Success(id, services.Icons(ids));
            }
            case "capture.request":
            {
                var targetKey = String(args?["targetKey"]);
                var shape = String(args?["shape"]);
                var trigger = String(args?["trigger"]);
                if (targetKey is null || RouterTarget.Parse(targetKey) is not { } target || shape != "ax" ||
                    trigger is null || !Triggers.Contains(trigger)) return Failure(id, "invalid-arguments");
                var (observation, failure) = await CaptureAsync(target).ConfigureAwait(false);
                return observation is not null ? Success(id, observation) : Failure(id, failure!);
            }
            case "ocr.window":
            {
                if (args?["windowId"] is not JsonValue windowNode || !windowNode.TryGetValue<int>(out var windowId))
                    return Failure(id, "invalid-arguments");
                var (observation, failure) = await CaptureOcrAsync(windowId).ConfigureAwait(false);
                return observation is not null ? Success(id, observation) : Failure(id, failure!);
            }
            case "browser.url":
            {
                var bundleId = String(args?["bundleId"]);
                if (string.IsNullOrEmpty(bundleId)) return Failure(id, "invalid-arguments");
                var (url, failure) = BrowserUrl(bundleId);
                return url is not null ? Success(id, url) : Failure(id, failure!);
            }
            case "observer.configure":
            {
                var denied = StringArray(args?["deniedBundleIds"]);
                if (denied is null || Bool(args?["captureTypedText"]) is not { } typed ||
                    Bool(args?["screenOcr"]) is not { } ocr || Bool(args?["paused"]) is not { } paused)
                    return Failure(id, "invalid-arguments");
                services.Configure(new ObserverConfiguration(denied, typed, ocr, paused));
                return Success(id, null);
            }
            case "settings.open":
                services.OpenSettings();
                return Success(id, null);
            default:
                return null;
        }
    }

    private sealed record RouterTarget(string BundleId, int? WindowId, string? Url)
    {
        public static RouterTarget? Parse(string key)
        {
            var parts = key.Split('|', 3);
            if (parts.Length != 3 || parts[0].Length == 0) return null;
            int? windowId = null;
            if (parts[1] != "-")
            {
                if (!int.TryParse(parts[1], out var parsed) || parsed <= 0) return null;
                windowId = parsed;
            }
            return new RouterTarget(parts[0], windowId, parts[2] == "-" ? null : parts[2]);
        }
    }

    private async Task<(RouterObservation?, string?)> CaptureAsync(RouterTarget target)
    {
        if (services.IsDenied(target.BundleId)) return (null, "denied");
        if (services.SecureInputEnabled) return (null, "secure-input");
        var before = services.Foreground;
        if (!services.MayObserve(target.BundleId) || before is null || before.BundleId != target.BundleId ||
            (target.WindowId is not null && before.WindowId != target.WindowId)) return (null, "unavailable");

        var isBrowser = services.IsBrowser(target.BundleId);
        string? text;
        string? url = null;
        bool ocrFallbackNeeded;
        if (isBrowser)
        {
            var result = services.CaptureBrowser(target.BundleId, () =>
            {
                var extracted = services.CaptureUia(target.BundleId, target.WindowId);
                return new BrowserCaptureContent(extracted, Utf8Length(extracted));
            });
            switch (result)
            {
                case BrowserCaptureResult.Captured captured:
                    if (target.Url is not null && target.Url != captured.Url) return (null, "unavailable");
                    text = captured.Content.Text;
                    url = captured.Url;
                    ocrFallbackNeeded = captured.NeedsOcr;
                    break;
                case BrowserCaptureResult.Suppressed:
                    return (null, "incognito");
                default:
                    return (null, "unavailable");
            }
        }
        else
        {
            if (target.Url is not null) return (null, "unavailable");
            text = services.CaptureUia(target.BundleId, target.WindowId);
            ocrFallbackNeeded = Utf8Length(text) < OcrFallbackBytes;
        }
        if (services.Foreground != before) return (null, "unavailable");

        var kind = "content.snapshot";
        var shape = "ax";
        var content = text ?? "";
        if (ocrFallbackNeeded && services.ScreenOcrEnabled && services.ScreenRecordingTrusted && before.WindowId is { } windowId)
        {
            var rawBefore = isBrowser ? services.RawBrowserUrl(target.BundleId) : null;
            if (isBrowser && rawBefore is null) return (null, "unavailable");
            try
            {
                var recognized = await services.CaptureOcrAsync(windowId).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(recognized))
                {
                    content = recognized;
                    kind = "screen.ocr";
                    shape = "text";
                }
            }
            catch (Exception)
            {
                return (null, "unavailable");
            }
            if (isBrowser)
            {
                var (afterUrl, failure) = BrowserUrl(target.BundleId);
                if (failure is not null) return (null, failure);
                if (afterUrl != url) return (null, "unavailable");
                if (services.RawBrowserUrl(target.BundleId) != rawBefore) return (null, "unavailable");
            }
        }
        if (services.Foreground != before || services.IsDenied(target.BundleId) ||
            !services.MayObserve(target.BundleId) || services.SecureInputEnabled) return (null, "unavailable");
        if (content.Length == 0) return (null, text is null ? "unavailable" : "empty-content");
        return (new RouterObservation(_now(), kind, target.BundleId, url, target.WindowId, content, shape), null);
    }

    private async Task<(RouterObservation?, string?)> CaptureOcrAsync(int windowId)
    {
        var before = services.Foreground;
        if (before is null || before.WindowId != windowId || services.IsDenied(before.BundleId) ||
            services.SecureInputEnabled || !services.MayObserve(before.BundleId) ||
            !services.ScreenOcrEnabled || !services.ScreenRecordingTrusted) return (null, "unavailable");
        var isBrowser = services.IsBrowser(before.BundleId);
        string? url = null;
        string? rawBefore = null;
        if (isBrowser)
        {
            var (resolved, failure) = BrowserUrl(before.BundleId);
            if (failure is not null) return (null, failure);
            url = resolved;
            rawBefore = services.RawBrowserUrl(before.BundleId);
            if (rawBefore is null) return (null, "unavailable");
        }
        try
        {
            var text = await services.CaptureOcrAsync(windowId).ConfigureAwait(false);
            if (string.IsNullOrEmpty(text) || services.Foreground != before || services.SecureInputEnabled ||
                !services.MayObserve(before.BundleId)) return (null, "unavailable");
            if (isBrowser)
            {
                var (afterUrl, failure) = BrowserUrl(before.BundleId);
                if (failure is not null) return (null, failure);
                if (afterUrl != url || services.RawBrowserUrl(before.BundleId) != rawBefore) return (null, "unavailable");
            }
            return (new RouterObservation(_now(), "screen.ocr", before.BundleId, url, windowId, text, "text"), null);
        }
        catch (Exception)
        {
            return (null, "unavailable");
        }
    }

    private (string?, string?) BrowserUrl(string bundleId)
    {
        if (services.IsDenied(bundleId)) return (null, "denied");
        if (!services.MayObserve(bundleId) || services.Foreground?.BundleId != bundleId) return (null, "unavailable");
        return services.BrowserUrl(bundleId) switch
        {
            BrowserCaptureResult.Captured captured => (captured.Url, null),
            BrowserCaptureResult.Suppressed => (null, "incognito"),
            _ => (null, "unavailable"),
        };
    }

    private static int Utf8Length(string? text) => text is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(text);

    private static string? String(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool? Bool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static List<string>? StringArray(JsonNode? node)
    {
        if (node is not JsonArray array) return null;
        var list = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (String(item) is not { } text) return null;
            list.Add(text);
        }
        return list;
    }

    private static byte[]? Success(string id, object? data)
    {
        try { return CaptureProtocol.EncodeResultSuccess(id, data); }
        catch (Exception) { return Failure(id, "unavailable"); }
    }

    private static byte[]? Failure(string id, string code)
    {
        try { return CaptureProtocol.EncodeResultFailure(id, code); }
        catch (Exception) { return null; }
    }
}
