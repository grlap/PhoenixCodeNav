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
    private readonly TextWriter? _error;
    private readonly object _errorSync = new();
    private DaemonUnavailableFailure _failure;
    private Task<McpClient?>? _connecting;
    private McpClient? _client;
    private Stream? _stream;
    private bool _disposed;
    private long _nextRequestId;

    // A completed initialize with no subsequently observed transport failure. Read under
    // the same lock as connection publication/loss; deliberately not an ever-recovered flag.
    internal bool HasEstablishedConnection
    {
        get { lock (_sync) return _client is not null; }
    }

    internal DaemonSessionRecovery(DaemonUnavailableFailure failure,
        Func<CancellationToken, Task<Stream>> connect, CancellationToken cancellationToken, TextWriter? error = null)
    {
        _failure = failure;
        _connect = connect;
        _error = error;
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
            if (client is null && _failure.CanRecoverInSession)
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
            lock (_sync) return UnavailableMcpServerTool.FailureResult(_failure, parameters.Name,
                sessionRecoveryAvailable: _failure.CanRecoverInSession);
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
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                // An expected lifetime cancellation is shutdown, not evidence of a broken
                // transport. A real failure still counts even if shutdown also began.
                if (ex is not OperationCanceledException || !_lifetime.IsCancellationRequested)
                {
                    await InvalidateConnectionAsync(client).ConfigureAwait(false);
                }
            }
            throw;
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            bool sessionRecoveryAvailable = await InvalidateConnectionAsync(client).ConfigureAwait(false);
            return UnavailableMcpServerTool.FailureResult(new(
                DaemonFailureCause.RequestOutcomeUnknown,
                "The daemon exchange failed after dispatch; this request may have executed and was not replayed.",
                "Inspect the operation's outcome before repeating a mutating call. Later calls may reconnect.",
                Retryable: false), parameters.Name, sessionRecoveryAvailable: sessionRecoveryAvailable);
        }
    }

    private async ValueTask<bool> InvalidateConnectionAsync(McpClient client)
    {
        Stream? stream = null;
        bool sessionRecoveryAvailable;
        lock (_sync)
        {
            if (ReferenceEquals(client, _client))
            {
                _client = null;
                stream = _stream;
                _stream = null;
                _failure = ConnectionFailure();
            }
            // A concurrent call may already have replaced the failed connection or cause.
            sessionRecoveryAvailable = _failure.CanRecoverInSession;
        }
        if (stream is not null)
            await DisposeConnectionAsync(client, stream).ConfigureAwait(false);
        return sessionRecoveryAvailable;
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

    private async ValueTask DisposeConnectionAsync(McpClient? client, Stream? stream)
    {
        // Cleanup must not replace an initialize refusal, uncertain-dispatch result,
        // caller cancellation, or the already-computed orderly shutdown status.
        try
        {
            try { if (client is not null) await client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { ReportCleanupFailure("client", ex); }
        }
        finally
        {
            try { if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { ReportCleanupFailure("stream", ex); }
        }
    }

    private void ReportCleanupFailure(string operation, Exception exception)
    {
        if (IsTransportFailure(exception)) return;
        // Host logging is already disposed during final recovery cleanup. Keep this on
        // stderr, serialize concurrent writers, and never expose messages or paths.
        try
        {
            lock (_errorSync)
                (_error ?? Console.Error).WriteLine(
                    operation == "connect"
                        ? $"Phoenix warning: daemon recovery connect attempt failed ({exception.GetType().Name}); observed at shutdown."
                        : $"Phoenix warning: daemon recovery {operation} cleanup failed ({exception.GetType().Name}).");
        }
        catch { } // A diagnostic sink failure is never a new operational result.
    }

    private static DaemonUnavailableFailure ConnectionFailure() => new(
        DaemonFailureCause.ConnectionUnavailable, "The existing shared daemon is not responding to this connection attempt.",
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
            {
                // Lifetime cancellation may surface as a wrapped transport failure.
                // Observing the owned attempt is cleanup, not a new shutdown outcome.
                try { await connecting.ConfigureAwait(false); }
                catch (Exception ex) { ReportCleanupFailure("connect", ex); }
            }
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
