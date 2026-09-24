using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using CodeNav.Core.Indexing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp.Daemon;

internal sealed class DaemonServer
{
    internal static readonly TimeSpan SessionDrainTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly DaemonEndpoint _endpoint;
    private readonly string? _indexDb;
    private readonly bool _rebuild;
    private readonly bool _keepAlive;
    private readonly TimeSpan _idleLinger;
    private readonly Action<IndexManager>? _configureIndexForTest;
    private readonly Action? _beforeConnectionHandshakeForTest;
    private readonly DaemonStartupReporter? _startupReporter;
    private readonly DaemonFileLog? _fileLog;
    internal string ShutdownIndexState { get; private set; } = "not_started";
    internal string ShutdownReason { get; private set; } = "failed";
    private readonly ConcurrentDictionary<long, Task> _sessions = new();
    private readonly CancellationTokenSource _retire = new();
    private long _nextSessionId;
    private long _lastClientTicks = DateTime.UtcNow.Ticks;
    private int _activeClients;

    internal Action<McpServer>? SessionRegisteredForTest { get; set; }

    internal DaemonServer(
        DaemonEndpoint endpoint,
        string? indexDb,
        bool rebuild,
        bool keepAlive,
        TimeSpan? idleLinger = null,
        Action<IndexManager>? configureIndexForTest = null,
        Action? beforeConnectionHandshakeForTest = null,
        DaemonStartupReporter? startupReporter = null,
        DaemonFileLog? fileLog = null)
    {
        _endpoint = endpoint;
        _indexDb = indexDb;
        _rebuild = rebuild;
        _keepAlive = keepAlive;
        _idleLinger = idleLinger ?? TimeSpan.FromMinutes(15);
        _configureIndexForTest = configureIndexForTest;
        _beforeConnectionHandshakeForTest = beforeConnectionHandshakeForTest;
        _startupReporter = startupReporter;
        _fileLog = fileLog;
        if (_idleLinger <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleLinger));
    }

    internal async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        PhoenixRuntimeMode.Set(PhoenixProcessMode.Daemon);
        var admission = new DaemonRequestAdmission();
        IHost host = McpApplication.BuildHost(
            _endpoint.WorkspaceRoot, _indexDb, stdio: false, admission, _fileLog);
        bool hostStarted = false;
        IndexManager? manager = null;
        try
        {
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            hostStarted = true;
            _configureIndexForTest?.Invoke(
                host.Services.GetRequiredService<IndexManager>());
            manager = McpApplication.StartIndex(host, _rebuild);
            ILogger logger = host.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("PhoenixCodeNav.Daemon");
            if (!manager.IsWriter)
            {
                ShutdownReason = "writer_unavailable";
                logger.LogError("Shared daemon requires writer ownership; access mode is {Mode}.",
                    manager.Health().AccessMode);
                if (_startupReporter is not null)
                {
                    await _startupReporter.ReportAsync(DaemonStartupReport.Refused(
                        Environment.ProcessId,
                        DaemonStartupFailures.FromIndexManager(manager)),
                        CancellationToken.None).ConfigureAwait(false);
                }
                return 3;
            }

            using var acceptLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _retire.Token);
            using var sessionLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            // Capture while the source is alive; a queued handler may start after shutdown drain.
            CancellationToken sessionToken = sessionLifetime.Token;
            IDaemonTransportListener? listener = null;
            try
            {
                listener = DaemonTransport.Listen(_endpoint);
                DaemonDescriptor.Publish(_endpoint, daemonProcess: true);
                if (_startupReporter is not null)
                {
                    await _startupReporter.ReportAsync(
                        DaemonStartupReport.ReadyReport(Environment.ProcessId),
                        CancellationToken.None).ConfigureAwait(false);
                }
                logger.LogInformation(
                    "Phoenix shared daemon ready for workspace identity {Identity}.",
                    _endpoint.EndpointKey);

                Task idleMonitor = MonitorIdleAsync(acceptLifetime.Token);
                try
                {
                    while (!acceptLifetime.IsCancellationRequested)
                    {
                        Stream stream;
                        try
                        {
                            stream = await AcceptWithRetryAsync(
                                listener, logger, acceptLifetime.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (acceptLifetime.IsCancellationRequested)
                        {
                            break;
                        }

                        long id = Interlocked.Increment(ref _nextSessionId);
                        // An async handler can execute synchronously until its first incomplete
                        // await. Keep that work off the accept loop so one busy MCP session cannot
                        // starve later clients before their frozen-preamble handshake.
                        Task session = Task.Run(
                            () => HandleConnectionAsync(
                                stream,
                                manager,
                                admission,
                                host.Services,
                                sessionToken),
                            CancellationToken.None);
                        _ = TrackSession(id, session, logger);
                    }
                }
                finally
                {
                    acceptLifetime.Cancel();
                    try { await idleMonitor.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
            }
            finally
            {
                if (listener is not null)
                    await listener.DisposeAsync().ConfigureAwait(false);
                acceptLifetime.Cancel();
                await DrainSessionsAsync(logger, sessionLifetime, admission).ConfigureAwait(false);
            }
            ShutdownReason = cancellationToken.IsCancellationRequested ? "cancelled" : "retired";
            return 0;
        }
        finally
        {
            if (hostStarted)
            {
                try { await host.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }
            ShutdownIndexState = manager?.State ?? "not_started";
            host.Dispose();
            DaemonDescriptor.DeleteOwn(_endpoint);
        }
    }

    private Task TrackSession(long id, Task session, ILogger logger)
    {
        _sessions[id] = session;
        return session.ContinueWith(completed =>
        {
            try
            {
                if (completed.Exception is { } failure)
                {
                    // Normal disconnect/cancellation is handled at the transport boundary.
                    // Setup, registration and disposal faults must not disappear as disconnects.
                    try { logger.LogWarning(failure, "Phoenix daemon session {SessionId} failed unexpectedly.", id); }
                    catch { /* Observing a session must not depend on the diagnostic provider. */ }
                }
            }
            finally { _sessions.TryRemove(id, out _); }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal static async ValueTask<Stream> AcceptWithRetryAsync(
        IDaemonTransportListener listener,
        ILogger logger,
        CancellationToken cancellationToken,
        TimeSpan? retryDelay = null)
    {
        TimeSpan delay = retryDelay ?? AcceptRetryDelay;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                logger.LogWarning(ex,
                    "Phoenix daemon transport accept failed transiently; retrying.");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleConnectionAsync(
        Stream stream,
        IndexManager manager,
        DaemonRequestAdmission admission,
        IServiceProvider services,
        CancellationToken daemonCancellation)
    {
        await using (stream.ConfigureAwait(false))
        {
            _beforeConnectionHandshakeForTest?.Invoke();
            DaemonHandshakeRequest? request = null;
            DaemonHandshakeResponse response;
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(daemonCancellation))
            {
                handshake.CancelAfter(DaemonProtocol.HandshakeTimeout);
                try
                {
                    (byte version, DaemonPreambleMode mode, request) =
                        await DaemonProtocol.ReadRequestAsync(stream, handshake.Token)
                            .ConfigureAwait(false);
                    response = DaemonProtocol.Evaluate(_endpoint, version, mode, request);
                    await DaemonProtocol.WriteResponseAsync(stream, response, handshake.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or JsonException or
                                           OperationCanceledException)
                {
                    return;
                }
            }

            if (!response.Accepted) return;
            if (response.Retiring)
            {
                _retire.Cancel();
                return;
            }
            if (request is null) return;
            if (request.Rebuild && !manager.RequestFullRebuild())
                services.GetRequiredService<ILoggerFactory>().CreateLogger("PhoenixCodeNav.Daemon")
                    .LogWarning("Client rebuild request refused: {Cause}.", manager.Health().Error);

            Interlocked.Increment(ref _activeClients);
            Volatile.Write(ref _lastClientTicks, DateTime.UtcNow.Ticks);
            try
            {
                ILoggerFactory loggerFactory = services.GetRequiredService<ILoggerFactory>();
                McpServerOptions options = services
                    .GetRequiredService<IOptions<McpServerOptions>>().Value;
                await using var transport = new StreamServerTransport(
                    stream, stream, "phoenix-codenav-daemon", loggerFactory);
                await using McpServer server = McpServer.Create(
                    transport, options, loggerFactory, services);
                admission.Register(server, $"{request.ClientPid}:{request.Nonce}");
                try
                {
                    SessionRegisteredForTest?.Invoke(server);
                    try { await server.RunAsync(daemonCancellation).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (daemonCancellation.IsCancellationRequested) { }
                    catch (Exception ex) when (ex is IOException or SocketException)
                    {
                        // Only errors from the running transport are expected peer disconnects.
                    }
                }
                finally { admission.Unregister(server); }
            }
            finally
            {
                // Includes failed setup, unregister and asynchronous resource disposal.
                Interlocked.Decrement(ref _activeClients);
                Volatile.Write(ref _lastClientTicks, DateTime.UtcNow.Ticks);
            }
        }
    }

    private async Task MonitorIdleAsync(CancellationToken cancellationToken)
    {
        if (_keepAlive) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Volatile.Read(ref _activeClients) != 0) continue;
            var idle = TimeSpan.FromTicks(DateTime.UtcNow.Ticks -
                Volatile.Read(ref _lastClientTicks));
            if (idle < _idleLinger) continue;
            _retire.Cancel();
            return;
        }
    }

    private async Task DrainSessionsAsync(
        ILogger logger,
        CancellationTokenSource sessionLifetime,
        DaemonRequestAdmission admission)
    {
        admission.BeginDrain();
        DateTime deadline = DateTime.UtcNow + SessionDrainTimeout;
        while (admission.ActiveCount != 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50).ConfigureAwait(false);
        if (admission.ActiveCount != 0)
        {
            logger.LogWarning(
                "Phoenix daemon request drain timed out with {Count} admitted requests.",
                admission.ActiveCount);
        }

        sessionLifetime.Cancel();
        Task[] sessions = _sessions.Values.ToArray();
        if (sessions.Length == 0) return;
        Task closed = Task.WhenAll(sessions);
        await Task.WhenAny(closed, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        if (closed.IsCompleted)
            try { await closed.ConfigureAwait(false); } catch { }
    }
}
