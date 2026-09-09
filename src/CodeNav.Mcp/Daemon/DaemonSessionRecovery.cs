using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp.Daemon;

/// <summary>On-demand recovery behind an already initialized downstream MCP server.</summary>
internal sealed class DaemonSessionRecovery : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Func<CancellationToken, Task<Stream>> _connect;
    private readonly CancellationTokenSource _lifetime;
    private DaemonUnavailableFailure _failure;
    private Task<McpClient?>? _connecting;
    private McpClient? _client;
    private Stream? _stream;
    private bool _disposed;
    private long _nextRequestId;

    internal DaemonSessionRecovery(DaemonUnavailableFailure failure,
        Func<CancellationToken, Task<Stream>> connect, CancellationToken cancellationToken)
    {
        _failure = failure;
        _connect = connect;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    internal async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
    {
        CallToolRequestParams parameters = request.Params ?? throw new InvalidOperationException("Missing tool parameters.");
        McpClient? client;
        Task<McpClient?>? connecting = null;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _client;
            if (client is null && _failure.Retryable)
            {
                // Share the in-flight attempt, not a failed result cached for the session.
                if (_connecting is null || _connecting.IsCompleted)
                    _connecting = ConnectAsync(request.Server);
                connecting = _connecting;
            }
        }
        if (connecting is not null)
            client = await connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            lock (_sync) return UnavailableMcpServerTool.FailureResult(_failure, parameters.Name, sessionRecoveryAvailable: true);
        }

        // String ids keep our correlation separate from the SDK's numeric initialization ids.
        var upstreamRequest = new JsonRpcRequest
        {
            Id = new RequestId("recovery-" + Interlocked.Increment(ref _nextRequestId)),
            Method = "tools/call",
            Params = JsonSerializer.SerializeToNode(parameters, McpJsonUtilities.DefaultOptions),
        };
        try
        {
            var response = await client.SendRequestAsync(upstreamRequest, cancellationToken).ConfigureAwait(false);
            return response.Result.Deserialize<CallToolResult>(McpJsonUtilities.DefaultOptions)
                ?? throw new McpException("The daemon returned no tool result.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Explicitly await forwarding: disposing the SDK's cancellation registration while
            // its WaitAsync unwinds can otherwise suppress the fire-and-forget notification.
            try
            {
                await client.SendMessageAsync(new JsonRpcNotification
                {
                    Method = "notifications/cancelled",
                    Params = JsonSerializer.SerializeToNode(new CancelledNotificationParams
                    { RequestId = upstreamRequest.Id }, McpJsonUtilities.DefaultOptions),
                }, _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransportFailure(ex)) { }
            throw;
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            Stream? stream = null;
            lock (_sync)
            {
                if (ReferenceEquals(client, _client))
                {
                    _client = null;
                    stream = _stream;
                    _stream = null;
                    _failure = ConnectionFailure();
                }
            }
            if (stream is not null)
            {
                await DisposeConnectionAsync(client, stream).ConfigureAwait(false);
            }
            return UnavailableMcpServerTool.FailureResult(new(
                "daemon_request_outcome_unknown",
                "The daemon exchange failed after dispatch; this request may have executed and was not replayed.",
                "Inspect the operation's outcome before repeating a mutating call. Later calls may reconnect.",
                Retryable: false), parameters.Name, sessionRecoveryAvailable: true);
        }
    }

    private async Task<McpClient?> ConnectAsync(McpServer downstream)
    {
        Stream? stream = null;
        McpClient? client = null;
        try
        {
            stream = await _connect(_lifetime.Token).ConfigureAwait(false);
            client = await McpClient.CreateAsync(new StreamClientTransport(stream, stream, NullLoggerFactory.Instance),
                new McpClientOptions { ClientInfo = downstream.ClientInfo },
                cancellationToken: _lifetime.Token).ConfigureAwait(false);
            // No roots, sampling or elicitation capability is advertised without a handler.
            client.RegisterNotificationHandler("notifications/progress", async (notification, token) =>
                await downstream.SendMessageAsync(notification, token).ConfigureAwait(false));
            client.RegisterNotificationHandler("notifications/message", async (notification, token) =>
                await downstream.SendMessageAsync(notification, token).ConfigureAwait(false));
            lock (_sync)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                _client = client;
                _stream = stream;
                stream = null;
                McpClient connected = client;
                client = null;
                return connected;
            }
        }
        catch (DaemonProxyFailureException ex)
        {
            lock (_sync) _failure = ex.Failure;
            return null;
        }
        // An initialize rejection leaves no usable client; a tools/call rejection above
        // instead belongs to the caller and must preserve the established connection.
        catch (Exception ex) when (!_lifetime.IsCancellationRequested &&
            (IsTransportFailure(ex) || ex is McpProtocolException))
        {
            lock (_sync) _failure = ConnectionFailure();
            return null;
        }
        finally
        {
            await DisposeConnectionAsync(client, stream).ConfigureAwait(false);
        }
    }

    // A JSON-RPC rejection is a received response, not a lost transport. Preserve it and
    // the connection. Caller cancellation is handled separately before this classifier.
    private static bool IsTransportFailure(Exception ex) => ex is not McpProtocolException &&
        ex is IOException or ObjectDisposedException or McpException or JsonException or
            TimeoutException or OperationCanceledException;

    private static async ValueTask DisposeConnectionAsync(McpClient? client, Stream? stream)
    {
        try
        {
            try { if (client is not null) await client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (IsTransportFailure(ex)) { }
        }
        finally
        {
            try { if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (IsTransportFailure(ex)) { }
        }
    }

    private static DaemonUnavailableFailure ConnectionFailure() => new(
        "daemon_connection_unavailable", "The existing shared daemon is not responding to this connection attempt.",
        "Retry a tool call in this MCP session after the daemon is ready. Recovery does not start or replace a daemon.",
        Retryable: true);

    public async ValueTask DisposeAsync()
    {
        Task<McpClient?>? connecting;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            connecting = _connecting;
        }
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            if (connecting is not null)
                try { await connecting.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        finally
        {
            McpClient? client;
            Stream? stream;
            lock (_sync)
            {
                client = _client;
                stream = _stream;
                _client = null;
                _stream = null;
            }
            try { await DisposeConnectionAsync(client, stream).ConfigureAwait(false); }
            finally { _lifetime.Dispose(); }
        }
    }
}
