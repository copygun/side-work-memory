using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Side.Win.Core;

/// <summary>
/// Wire shape of HelperHealthSchema (src/contracts/protocol.ts). The daemon parses it with
/// z.strictObject, so every property must be present and no extra property may appear.
/// </summary>
public sealed class HelperHealth
{
    public string Platform => "win32";
    public int ProtocolVersion => CaptureProtocol.Version;
    public bool NativeCaptureAvailable { get; set; }
    public bool InputCaptureAvailable { get; set; }
    public bool ScreenOcrAvailable { get; set; }
    public List<string> ScreenOcrLanguages { get; set; } = [];
    public bool AccessibilityTrusted { get; set; }
    public bool InputMonitoringTrusted { get; set; }
    public bool ScreenRecordingTrusted { get; set; }
    public bool EventTapHealthy { get; set; }
    public bool InputTapRunning { get; set; }
    public int ObserverRegistrationFailures { get; set; }
    public bool SecureInput { get; set; }
    public bool PermissionSheetVisible { get; set; }
    public bool SystemSessionActive { get; set; }
    public bool Idle { get; set; }
    public int Pid { get; set; }
    public int ObserverPid { get; set; }
    public bool ResponsibleSelf => true;
    [JsonConverter(typeof(JsonStringEnumConverter<HelperState>))]
    public HelperState State { get; set; } = HelperState.Starting;
    [JsonConverter(typeof(JsonStringEnumConverter<AsideAdapterState>))]
    public AsideAdapterState AsideAdapter { get; set; } = AsideAdapterState.Off;
    public Dictionary<string, PerAppHealth> PerApp { get; set; } = [];
}

public sealed record PerAppHealth(bool ChromeOnly, int MaxTreeBytes);

[JsonConverter(typeof(JsonStringEnumConverter<HelperState>))]
public enum HelperState { [JsonStringEnumMemberName("starting")] Starting, [JsonStringEnumMemberName("running")] Running, [JsonStringEnumMemberName("paused")] Paused, [JsonStringEnumMemberName("stopped")] Stopped }

[JsonConverter(typeof(JsonStringEnumConverter<AsideAdapterState>))]
public enum AsideAdapterState { [JsonStringEnumMemberName("off")] Off, [JsonStringEnumMemberName("available")] Available, [JsonStringEnumMemberName("unavailable")] Unavailable, [JsonStringEnumMemberName("error")] Error }

public sealed class CaptureProtocolException(string message) : Exception(message);

/// <summary>stdio JSON-lines framing shared with the daemon (docs/planning/02-architecture.md §3).</summary>
public static class CaptureProtocol
{
    public const int Version = 1;
    public const int MaxRawBytes = 4_194_304;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static byte[] EncodeHello(byte[] key, string appVersion)
    {
        if (key.Length != 32) throw new CaptureProtocolException("invalid key length");
        return Line(new { type = "hello", protocolVersion = Version, key = Convert.ToBase64String(key), appVersion });
    }

    public static byte[] EncodeHealth(HelperHealth health) => Line(new { type = "health", health });

    public static byte[] EncodeEvent(object observation) => Line(new { type = "event", @event = observation });

    public static byte[] EncodeResultSuccess(string id, object? data) => Line(new ResultSuccess(id, data));

    public static byte[] EncodeResultFailure(string id, string error) => Line(new { type = "result", id, ok = false, error });

    public static byte[] EncodeProtocolError(string message) => Line(new { type = "protocol-error", message });

    private sealed record ResultSuccess(string Id, object? Data)
    {
        public string Type => "result";
        public bool Ok => true;
    }

    private static byte[] Line(object message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), Json);
        if (body.Length + 1 > MaxRawBytes) throw new CaptureProtocolException("frame too large");
        var line = new byte[body.Length + 1];
        body.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        return line;
    }

    /// <summary>A frame is one UTF-8 JSON object terminated by exactly one LF and no CR/LF inside.</summary>
    public static bool IsSingleLine(ReadOnlySpan<byte> line)
    {
        if (line.Length == 0 || line.Length > MaxRawBytes || line[^1] != (byte)'\n') return false;
        var body = line[..^1];
        if (body.IndexOf((byte)'\n') >= 0 || body.IndexOf((byte)'\r') >= 0) return false;
        try
        {
            _ = new UTF8Encoding(false, true).GetString(body);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
