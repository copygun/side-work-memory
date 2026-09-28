using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Side.Win.Core;

/// <summary>
/// Windows has no TCC-style prompts for the capabilities Side uses: UI Automation and low-level
/// input hooks need no grant, and Windows.Graphics.Capture only needs OS support (plus the
/// optional "screen capture" privacy toggle on newer builds). Status is therefore derived from
/// capability probes rather than user grants.
/// </summary>
public sealed class PermissionCoordinator(ICaptureLayer capture, IUiLayer? ui)
{
    public event Action<PermissionStatus>? Changed;

    public PermissionStatus Preflight()
    {
        var automation = new Dictionary<string, bool>();
        foreach (var browser in KnownBrowsers) automation[browser] = true;
        return new PermissionStatus(true, true, capture.ScreenOcrAvailable, automation);
    }

    public PermissionStatus Request(IReadOnlySet<PermissionKind> kinds)
    {
        var status = Preflight();
        if (kinds.Contains(PermissionKind.ScreenRecording) && !status.ScreenRecording)
            ui?.ShowPermissionHelp(PermissionKind.ScreenRecording);
        Changed?.Invoke(status);
        return status;
    }

    public static readonly string[] KnownBrowsers =
        ["chrome.exe", "msedge.exe", "brave.exe", "whale.exe", "aside.exe", "arc.exe"];
}

/// <summary>Sends health on change and at least every 30 s (port of Health.swift).</summary>
public sealed class HelperHealthMonitor(PermissionCoordinator permissions, Func<HelperHealth> sample, Action<byte[]> send) : IDisposable
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private string? _lastBody;
    private DateTimeOffset? _lastSentAt;

    public void Start()
    {
        lock (_gate)
        {
            if (_timer is not null) return;
            _timer = new System.Threading.Timer(_ => Refresh(), null, TimeSpan.Zero, HeartbeatInterval);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Refresh()
    {
        try { Publish(permissions.Preflight()); }
        catch (Exception) { /* daemon not running */ }
    }

    private void Publish(PermissionStatus status)
    {
        var health = sample();
        health.AccessibilityTrusted = status.Accessibility;
        health.InputMonitoringTrusted = status.InputMonitoring;
        health.ScreenRecordingTrusted = status.ScreenRecording;
        health.InputTapRunning = health.InputTapRunning && status.InputMonitoring;
        health.InputCaptureAvailable = health.InputCaptureAvailable && health.InputTapRunning;
        health.ScreenOcrAvailable = health.ScreenOcrAvailable && status.ScreenRecording;
        health.EventTapHealthy = health.EventTapHealthy && health.InputTapRunning;
        health.PermissionSheetVisible = false;
        var body = JsonSerializer.Serialize(health, CaptureProtocol.Json);
        var now = DateTimeOffset.Now;
        lock (_gate)
        {
            var heartbeatDue = _lastSentAt is null || now - _lastSentAt >= HeartbeatInterval;
            if (body == _lastBody && !heartbeatDue) return;
            send(CaptureProtocol.EncodeHealth(health));
            _lastBody = body;
            _lastSentAt = now;
        }
    }

    public void Dispose() => Stop();
}

/// <summary>Adapts the capture layer + denial policy to the router (LiveCommandRouterServices).</summary>
public sealed class LiveRouterServices(
    ICaptureLayer capture, PermissionCoordinator permissions, Func<HelperHealth> sampleHealth, Action openSettings)
    : IRouterServices
{
    private volatile IReadOnlySet<string> _denied = new HashSet<string>();
    private static readonly HashSet<string> HardBlocked = LoadHardBlocked();

    public bool ScreenOcrEnabled { get; private set; }
    public ForegroundInfo? Foreground => capture.Foreground;
    public bool SecureInputEnabled => capture.SecureInputEnabled;
    public bool ScreenRecordingTrusted => permissions.Preflight().ScreenRecording;

    public HelperHealth HealthSnapshot()
    {
        var health = sampleHealth();
        var status = permissions.Preflight();
        health.AccessibilityTrusted = status.Accessibility;
        health.InputMonitoringTrusted = status.InputMonitoring;
        health.ScreenRecordingTrusted = status.ScreenRecording;
        health.SecureInput = capture.SecureInputEnabled;
        capture.WriteHealth(health);
        return health;
    }

    public PermissionStatus PermissionStatus() => permissions.Preflight();
    public PermissionStatus RequestPermissions(IReadOnlySet<PermissionKind> kinds) => permissions.Request(kinds);
    public IReadOnlyList<RouterApplication> Applications() => capture.Applications(IsDenied);
    public IReadOnlyList<RouterIcon> Icons(IReadOnlyList<string> bundleIds) => capture.Icons(bundleIds);

    public static bool IsHardDenied(string bundleId) => HardBlocked.Contains(bundleId.ToLowerInvariant());

    public bool IsDenied(string bundleId) =>
        _denied.Contains(bundleId) || IsHardDenied(bundleId) ||
        string.Equals(bundleId, OwnExecutableName, StringComparison.OrdinalIgnoreCase);

    public static string OwnExecutableName { get; } =
        Path.GetFileName(Environment.ProcessPath ?? "side.exe").ToLowerInvariant();

    public bool MayObserve(string bundleId) => capture.MayObserve(bundleId);
    public bool IsBrowser(string bundleId) => capture.IsBrowser(bundleId);
    public string? CaptureUia(string bundleId, int? windowId) => capture.CaptureUia(bundleId, windowId);
    public BrowserCaptureResult CaptureBrowser(string bundleId, Func<BrowserCaptureContent> readContent) =>
        capture.CaptureBrowser(bundleId, readContent);
    public BrowserCaptureResult BrowserUrl(string bundleId) => capture.BrowserUrl(bundleId);
    public string? RawBrowserUrl(string bundleId) => capture.RawBrowserUrl(bundleId);
    public Task<string?> CaptureOcrAsync(int windowId) => capture.CaptureOcrAsync(windowId);

    public void Configure(ObserverConfiguration configuration)
    {
        _denied = new HashSet<string>(configuration.DeniedBundleIds, StringComparer.OrdinalIgnoreCase);
        ScreenOcrEnabled = configuration.ScreenOcr;
        capture.Configure(configuration, IsDenied);
    }

    public void OpenSettings() => openSettings();

    private static HashSet<string> LoadHardBlocked()
    {
        using var stream = typeof(LiveRouterServices).Assembly.GetManifestResourceStream("hard-blocked-bundle-ids.json");
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (stream is null) return set;
        foreach (var item in JsonSerializer.Deserialize<string[]>(stream) ?? []) set.Add(item);
        return set;
    }
}

/// <summary>Composition of supervisor, router, capture and health (port of SideRuntime.swift).</summary>
public sealed class SideRuntime : IDisposable
{
    private readonly ICaptureLayer _capture;
    private readonly IUiLayer _ui;
    private readonly HelperHealthMonitor _health;
    private readonly CommandRouter _router;
    private volatile bool _paused = true;
    private bool _started;
    private bool _healthStarted;

    public DaemonSupervisor Supervisor { get; }
    public PermissionCoordinator Permissions { get; }
    public LiveRouterServices Services { get; }

    public SideRuntime(DaemonSupervisor supervisor, ICaptureLayer capture, IUiLayer ui)
    {
        Supervisor = supervisor;
        _capture = capture;
        _ui = ui;
        Permissions = new PermissionCoordinator(capture, ui);
        _capture.Initialize(observation =>
        {
            try { supervisor.SendEvent(observation); } catch (Exception) { /* daemon not running */ }
        });
        _health = new HelperHealthMonitor(Permissions, SampleHealth, frame =>
        {
            try { supervisor.SendFrame(frame); } catch (Exception) { }
        });
        Permissions.Changed += _ => _health.Refresh();
        Services = new LiveRouterServices(capture, Permissions, SampleHealth, ui.OpenSettings);
        _router = new CommandRouter(Services);
        supervisor.OtherCommand = RouteAsync;
        supervisor.StateChanged += SupervisorChanged;
        supervisor.WebSessionChanged += ui.OnWebSessionChanged;
    }

    private HelperHealth SampleHealth()
    {
        var running = Supervisor.State.IsRunning;
        var foreground = _capture.Foreground;
        var health = new HelperHealth
        {
            NativeCaptureAvailable = running,
            InputTapRunning = _capture.InputHookRunning,
            InputCaptureAvailable = running && _capture.InputHookRunning,
            EventTapHealthy = _capture.InputHookRunning,
            ScreenOcrAvailable = _capture.ScreenOcrAvailable,
            ScreenOcrLanguages = [.. _capture.ScreenOcrLanguages],
            SecureInput = _capture.SecureInputEnabled,
            SystemSessionActive = foreground is not null,
            Idle = _capture.IsIdle,
            Pid = Environment.ProcessId,
            ObserverPid = foreground?.Pid ?? 0,
            ObserverRegistrationFailures = _capture.ObserverRegistrationFailures,
            State = running ? (_paused ? HelperState.Paused : HelperState.Running) : HelperState.Stopped,
        };
        _capture.WriteHealth(health);
        return health;
    }

    public void Start()
    {
        if (_started) return;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        Supervisor.Start();
        _started = true;
        if (Supervisor.State.IsRunning && !_healthStarted)
        {
            _health.Start();
            _healthStarted = true;
        }
    }

    public async Task QuitAsync()
    {
        if (!_started) return;
        _started = false;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _health.Stop();
        _healthStarted = false;
        PauseCapture();
        await Supervisor.StopForQuitAsync().ConfigureAwait(false);
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)
            Supervisor.RetryKeyStore();
    }

    private async Task<byte[]?> RouteAsync(byte[] line)
    {
        var reply = await _router.ReplyAsync(line).ConfigureAwait(false);
        if (reply is null) return null;
        try
        {
            if (JsonNode.Parse(line) is JsonObject command && command["name"]?.GetValue<string>() == "observer.configure" &&
                command["args"]?["paused"] is JsonValue pausedNode && pausedNode.TryGetValue<bool>(out var paused) &&
                JsonNode.Parse(reply.AsSpan(0, reply.Length - 1)) is JsonObject result && result["ok"]?.GetValue<bool>() == true)
            {
                _paused = paused;
                _health.Refresh();
            }
        }
        catch (Exception) { /* malformed frames were already rejected by the router */ }
        return reply;
    }

    private void SupervisorChanged(SupervisorState state)
    {
        _ui.OnSupervisorStateChanged(state);
        if (!_started) return;
        if (state.IsRunning)
        {
            if (!_healthStarted)
            {
                _health.Start();
                _healthStarted = true;
            }
            else
            {
                _health.Refresh();
            }
        }
        else
        {
            _health.Stop();
            _healthStarted = false;
            PauseCapture();
        }
    }

    private void PauseCapture()
    {
        _paused = true;
        _capture.PauseAll();
    }

    public void Dispose()
    {
        _health.Dispose();
        _capture.Dispose();
    }
}
