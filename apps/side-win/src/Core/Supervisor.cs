using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Side.Win.Core;

public abstract record SupervisorState
{
    public sealed record Stopped : SupervisorState;
    public sealed record Starting : SupervisorState;
    public sealed record Running : SupervisorState;
    public sealed record WaitingToRestart(TimeSpan Delay) : SupervisorState;
    /// <summary>The credential store could not produce the master key (Windows analogue of a locked Keychain).</summary>
    public sealed record KeyStoreLocked : SupervisorState;
    public sealed record CaptureNotRunning : SupervisorState;

    public bool IsRunning => this is Running;
    public string? TrayMessage => this is CaptureNotRunning or KeyStoreLocked ? "Capture is not running" : null;
}

/// <summary>1s, 2s, 4s … max 60s backoff; more than 5 abnormal exits within a minute stops restarting.</summary>
public sealed class RestartPolicy
{
    private readonly List<DateTimeOffset> _recentExits = [];
    private int _attempt;
    private bool _stopped;

    public TimeSpan? RecordAbnormalExit(DateTimeOffset at)
    {
        if (_stopped) return null;
        _recentExits.RemoveAll(exit => exit <= at.AddSeconds(-60));
        _recentExits.Add(at);
        if (_recentExits.Count > 5)
        {
            _stopped = true;
            return null;
        }
        _attempt += 1;
        return TimeSpan.FromSeconds(Math.Min(1 << Math.Min(_attempt - 1, 6), 60));
    }
}

/// <summary>
/// Spawns resources\side.exe daemon, hands it the master key in the hello frame, relays
/// commands to the router and restarts it with backoff (port of Supervisor.swift).
/// </summary>
public sealed class DaemonSupervisor(string daemonPath, string appVersion, ISideKeyStore keyStore)
{
    private static readonly HashSet<string> ProviderCommands =
        ["keychain.set", "keychain.get", "keychain.status", "keychain.authorize"];

    private readonly object _gate = new();
    private Process? _process;
    private Stream? _stdin;
    private RestartPolicy _policy = new();
    private CancellationTokenSource? _restart;
    private int _launchGeneration;
    private bool _launchPending;
    private SupervisorState _state = new SupervisorState.Stopped();

    public SupervisorState State => _state;
    public WebSession? WebSession { get; private set; }
    public int? DaemonPid => _process?.Id;
    public event Action<SupervisorState>? StateChanged;
    public event Action<WebSession?>? WebSessionChanged;
    /// <summary>Router for every command that is not handled here. Returns one reply frame or null.</summary>
    public Func<byte[], Task<byte[]?>>? OtherCommand { get; set; }

    private void SetState(SupervisorState state)
    {
        _state = state;
        StateChanged?.Invoke(state);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_process is not null || _launchPending) return;
            _restart?.Cancel();
            _restart = null;
            _policy = new RestartPolicy();
        }
        Launch();
    }

    public void RetryKeyStore()
    {
        if (_state is SupervisorState.KeyStoreLocked) Launch();
    }

    public void Stop()
    {
        Process? child;
        lock (_gate)
        {
            _launchGeneration += 1;
            _launchPending = false;
            _restart?.Cancel();
            _restart = null;
            child = _process;
            _process = null;
            _stdin = null;
        }
        ClearWebSession();
        if (child is not null)
        {
            child.EnableRaisingEvents = false;
            // Closing stdin makes the daemon exit cleanly (it treats EOF as shutdown).
            try { child.StandardInput.Close(); } catch (Exception) { /* already gone */ }
            try { if (!child.WaitForExit(5000)) child.Kill(entireProcessTree: true); } catch (Exception) { /* exited */ }
        }
        SetState(new SupervisorState.Stopped());
    }

    public async Task StopForQuitAsync()
    {
        var child = _process;
        Stop();
        if (child is null) return;
        try { await child.WaitForExitAsync(new CancellationTokenSource(5000).Token); }
        catch (OperationCanceledException) { try { child.Kill(true); } catch (Exception) { } }
    }

    public void SendEvent(object observation) => SendFrame(CaptureProtocol.EncodeEvent(observation));

    public void SendHealth(HelperHealth health) => SendFrame(CaptureProtocol.EncodeHealth(health));

    public void SendFrame(byte[] line)
    {
        if (!IsAppFrame(line)) throw new InvalidOperationException("invalid frame");
        var child = _process ?? throw new InvalidOperationException("daemon not running");
        Write(line, child);
    }

    private ProcessStartInfo MakeStartInfo()
    {
        var info = new ProcessStartInfo(daemonPath, "daemon")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            // stderr is discarded like the macOS helper (it may carry diagnostics only).
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(daemonPath)!,
        };
        // Pass the user's environment through (PATH is needed to find claude/codex CLIs), but never
        // any SIDE_* override other than the data directory.
        foreach (var name in info.Environment.Keys.ToList())
            if (name.StartsWith("SIDE_", StringComparison.OrdinalIgnoreCase) && name != "SIDE_DATA_DIR")
                info.Environment.Remove(name);
        return info;
    }

    private void Launch()
    {
        int generation;
        lock (_gate)
        {
            _launchGeneration += 1;
            generation = _launchGeneration;
            _launchPending = true;
        }
        ClearWebSession();
        SetState(new SupervisorState.Starting());
        _ = Task.Run(() =>
        {
            byte[]? hello;
            try { hello = CaptureProtocol.EncodeHello(keyStore.MasterKey(), appVersion); }
            catch (Exception) { hello = null; }
            FinishLaunch(hello, generation);
        });
    }

    private void FinishLaunch(byte[]? hello, int generation)
    {
        lock (_gate)
        {
            if (_launchGeneration != generation) return;
            _launchPending = false;
        }
        if (hello is null)
        {
            SetState(new SupervisorState.KeyStoreLocked());
            return;
        }
        var child = new Process { StartInfo = MakeStartInfo(), EnableRaisingEvents = true };
        child.Exited += (_, _) => DidExit(child);
        try
        {
            child.Start();
            lock (_gate)
            {
                _process = child;
                _stdin = child.StandardInput.BaseStream;
            }
            child.ErrorDataReceived += (_, _) => { };
            child.BeginErrorReadLine();
            _ = Task.Run(() => ReadLoopAsync(child));
            Write(hello, child);
            SetState(new SupervisorState.Running());
        }
        catch (Exception)
        {
            try { if (!child.HasExited) child.Kill(true); } catch (Exception) { }
            lock (_gate) { if (_process == child) { _process = null; _stdin = null; } }
            ScheduleRestart();
        }
        finally
        {
            Array.Clear(hello);
        }
    }

    private async Task ReadLoopAsync(Process child)
    {
        var stream = child.StandardOutput.BaseStream;
        var buffer = new byte[64 * 1024];
        var frame = new MemoryStream();
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read <= 0) return;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        var command = frame.ToArray();
                        frame.SetLength(0);
                        _ = Task.Run(() => HandleCommandAsync(command, child));
                    }
                    else
                    {
                        if (frame.Length >= CaptureProtocol.MaxRawBytes - 1)
                        {
                            TryKill(child);
                            return;
                        }
                        frame.WriteByte(buffer[i]);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Pipe closed; Exited handles the restart.
        }
    }

    private async Task HandleCommandAsync(byte[] line, Process child)
    {
        if (_process != child) return;
        JsonObject? message;
        try { message = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { message = null; }
        if (message?["type"]?.GetValue<string>() == "protocol-error")
        {
            TryKill(child);
            return;
        }
        var reply = ReplyForCommand(message);
        if (reply is not null)
        {
            TryWrite(reply, child);
            return;
        }
        if (message?["type"]?.GetValue<string>() != "command" || message["id"] is not JsonValue idValue ||
            !idValue.TryGetValue<string>(out var id)) return;
        byte[]? routed = null;
        try { routed = OtherCommand is null ? null : await OtherCommand(line).ConfigureAwait(false); }
        catch (Exception) { routed = null; }
        if (_process != child) return;
        var final = routed is not null && IsCorrelatedResult(routed, id)
            ? routed
            : CaptureProtocol.EncodeResultFailure(id, routed is null ? "unavailable" : "invalid-result");
        TryWrite(final, child);
    }

    /// <summary>Handles web.session, keychain.* here; returns null for commands owned by the router.</summary>
    public byte[]? ReplyForCommand(JsonObject? message)
    {
        if (message is null) return CaptureProtocol.EncodeProtocolError("invalid command");
        var type = message["type"]?.GetValue<string>();
        if (type == "protocol-error") return null;
        if (type != "command" || message["id"] is not JsonValue idNode || !idNode.TryGetValue<string>(out var id) ||
            message["name"] is not JsonValue nameNode || !nameNode.TryGetValue<string>(out var name))
            return CaptureProtocol.EncodeProtocolError("invalid command");

        if (name == "web.session")
        {
            if (message["args"] is not JsonObject args || args.Count != 2 ||
                args["port"] is not JsonValue portNode || !portNode.TryGetValue<int>(out var port) ||
                args["token"] is not JsonValue tokenNode || !tokenNode.TryGetValue<string>(out var token) ||
                WebSession.TryCreate(port, token) is not { } session)
                return CaptureProtocol.EncodeResultFailure(id, "invalid-arguments");
            WebSession = session;
            WebSessionChanged?.Invoke(session);
            return CaptureProtocol.EncodeResultSuccess(id, null);
        }
        if (name == "keychain.rotate")
        {
            if (message.Count != 3) return CaptureProtocol.EncodeResultFailure(id, "invalid-arguments");
            try
            {
                var replacement = keyStore.RotateMasterKey();
                if (replacement.Length != 32) throw new KeyStoreException("invalid master key");
                return CaptureProtocol.EncodeResultSuccess(id, new { key = Convert.ToBase64String(replacement) });
            }
            catch (Exception)
            {
                return CaptureProtocol.EncodeResultFailure(id, "keychain-unavailable");
            }
        }
        if (!ProviderCommands.Contains(name)) return null;
        if (message.Count != 4 || message["args"] is not JsonObject providerArgs)
            return CaptureProtocol.EncodeResultFailure(id, "invalid-arguments");
        var expected = name == "keychain.set" ? 2 : 1;
        if (providerArgs.Count != expected || providerArgs["ref"] is not JsonValue refNode ||
            !refNode.TryGetValue<string>(out var reference) || reference.Length == 0)
            return CaptureProtocol.EncodeResultFailure(id, "invalid-arguments");
        try
        {
            switch (name)
            {
                case "keychain.set":
                    if (providerArgs["secret"] is not JsonValue secretNode ||
                        !secretNode.TryGetValue<string>(out var secret) || secret.Length == 0)
                        return CaptureProtocol.EncodeResultFailure(id, "invalid-arguments");
                    keyStore.SetProviderKey(reference, secret);
                    return CaptureProtocol.EncodeResultSuccess(id, new { @ref = reference });
                case "keychain.status":
                    var (stored, accessible) = keyStore.ProviderKeyStatus(reference);
                    return CaptureProtocol.EncodeResultSuccess(id, new { stored, accessible });
                case "keychain.authorize":
                    return CaptureProtocol.EncodeResultSuccess(id, new { authorized = keyStore.AuthorizeProviderKey(reference) });
                default:
                    return CaptureProtocol.EncodeResultSuccess(id, keyStore.ProviderKey(reference));
            }
        }
        catch (Exception)
        {
            return CaptureProtocol.EncodeResultFailure(id, "keychain-unavailable");
        }
    }

    private void TryWrite(byte[] line, Process child)
    {
        try { Write(line, child); } catch (Exception) { /* daemon gone */ }
    }

    private void Write(byte[] line, Process child)
    {
        Stream? stdin;
        lock (_gate)
        {
            if (_process != child) throw new InvalidOperationException("daemon not running");
            stdin = _stdin;
        }
        if (stdin is null || child.HasExited) throw new InvalidOperationException("daemon not running");
        try
        {
            lock (stdin)
            {
                stdin.Write(line);
                stdin.Flush();
            }
        }
        catch (Exception)
        {
            TryKill(child);
            throw;
        }
    }

    private static void TryKill(Process child)
    {
        try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch (Exception) { }
    }

    private static bool IsAppFrame(byte[] line)
    {
        if (!CaptureProtocol.IsSingleLine(line)) return false;
        try
        {
            if (JsonNode.Parse(line.AsSpan(0, line.Length - 1)) is not JsonObject message || message.Count != 2) return false;
            return message["type"]?.GetValue<string>() switch
            {
                "event" => message["event"] is JsonObject,
                "health" => message["health"] is JsonObject,
                _ => false,
            };
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsCorrelatedResult(byte[] line, string id)
    {
        if (!CaptureProtocol.IsSingleLine(line)) return false;
        try
        {
            if (JsonNode.Parse(line.AsSpan(0, line.Length - 1)) is not JsonObject message) return false;
            if (message["type"]?.GetValue<string>() != "result" || message["id"]?.GetValue<string>() != id) return false;
            if (message["ok"] is not JsonValue okNode || !okNode.TryGetValue<bool>(out var ok)) return false;
            return ok
                ? message.Count == 4 && message.ContainsKey("data")
                : message.Count == 4 && message["error"] is JsonValue error && error.TryGetValue<string>(out _);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void DidExit(Process child)
    {
        int exitCode;
        lock (_gate)
        {
            if (_process != child) return;
            _process = null;
            _stdin = null;
        }
        ClearWebSession();
        try { exitCode = child.ExitCode; } catch (Exception) { exitCode = -1; }
        if (exitCode == 0)
        {
            SetState(new SupervisorState.Stopped());
            return;
        }
        ScheduleRestart();
    }

    private void ScheduleRestart()
    {
        var delay = _policy.RecordAbnormalExit(DateTimeOffset.Now);
        if (delay is null)
        {
            SetState(new SupervisorState.CaptureNotRunning());
            return;
        }
        SetState(new SupervisorState.WaitingToRestart(delay.Value));
        var cancel = new CancellationTokenSource();
        lock (_gate) { _restart = cancel; }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay.Value, cancel.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            lock (_gate) { if (_restart == cancel) _restart = null; else return; }
            Launch();
        });
    }

    private void ClearWebSession()
    {
        if (WebSession is null) return;
        WebSession = null;
        WebSessionChanged?.Invoke(null);
    }
}
