using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Side.Win.UI;

public class DaemonRpcException(string message) : Exception(message);

/// <summary>The daemon is not reachable (not started yet, restarting, or stopped).</summary>
public sealed class DaemonUnavailableException() : DaemonRpcException("daemon unavailable");

/// <summary>The daemon answered with a JSON-RPC error; Message is the daemon's error message.</summary>
public sealed class DaemonRejectedException(string message) : DaemonRpcException(message);

/// <summary>
/// JSON-RPC 2.0 over HTTP on the daemon's AF_UNIX socket (%APPDATA%\Side\run\daemon.sock).
/// Replaces the macOS helper's `curl --unix-socket` (MenuBarRPC.swift / OnboardingRPC.swift).
/// Windows 10 1803+ supports AF_UNIX natively; Bun's server listens on the same path.
/// </summary>
public sealed class DaemonRpcClient(string? socketPath = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    /// <summary>10 s shared-slot wait + 60 s provider request + 10 s RPC overhead.</summary>
    public static readonly TimeSpan ProviderTestTimeout = TimeSpan.FromSeconds(80);

    private readonly string _socketPath = socketPath ?? UiPaths.SocketPath;

    private HttpClient CreateClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cancellationToken)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    /// <summary>Call <paramref name="method"/> and return its "result" node (may be null for JSON null).</summary>
    public async Task<JsonNode?> CallAsync(string method, object? parameters = null, TimeSpan? timeout = null)
    {
        var id = Guid.NewGuid().ToString();
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
            request["params"] = parameters as JsonNode ?? JsonSerializer.SerializeToNode(parameters);
        string body;
        try
        {
            using var client = CreateClient(timeout ?? DefaultTimeout);
            using var content = new StringContent(request.ToJsonString(), Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await client.PostAsync("http://localhost/rpc", content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new DaemonUnavailableException();
            body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (DaemonRpcException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new DaemonUnavailableException();
        }

        JsonObject? packet;
        try { packet = JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { throw new DaemonRpcException("invalid response"); }
        if (packet?["jsonrpc"]?.GetValue<string>() != "2.0" || packet["id"]?.GetValue<string>() != id)
            throw new DaemonRpcException("invalid response");
        if (packet["error"] is JsonObject error)
            throw new DaemonRejectedException(error["message"]?.GetValue<string>() ?? "rejected");
        if (!packet.ContainsKey("result")) throw new DaemonRpcException("invalid response");
        return packet["result"];
    }
}
