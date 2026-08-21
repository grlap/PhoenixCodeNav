using System.Diagnostics;

namespace CodeNav.Mcp.Daemon;

internal sealed class DaemonProxyFailureException : IOException
{
    internal DaemonUnavailableFailure Failure { get; }

    internal DaemonProxyFailureException(
        DaemonUnavailableFailure failure,
        Exception? inner = null)
        : base(failure.Detail, inner)
    {
        Failure = failure;
    }
}

internal sealed class DaemonProxy
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TakeoverTimeout = TimeSpan.FromMinutes(2);

    private readonly DaemonEndpoint _endpoint;
    private readonly string? _indexDb;
    private readonly bool _rebuild;
    private readonly bool _keepAlive;
    private readonly TimeSpan? _idleLingerForTest;
    private readonly string _clientName;
    private readonly IReadOnlyList<DaemonEndpoint> _legacyEndpoints;

    internal DaemonProxy(
        DaemonEndpoint endpoint,
        string? indexDb,
        bool rebuild,
        bool keepAlive,
        string clientName,
        TimeSpan? idleLingerForTest = null)
    {
        _endpoint = endpoint;
        _indexDb = indexDb;
        _rebuild = rebuild;
        _keepAlive = keepAlive;
        _clientName = clientName;
        _idleLingerForTest = idleLingerForTest;
        _legacyEndpoints = DaemonEndpoint.LegacyUnixCandidates(endpoint);
    }

    internal async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        PhoenixRuntimeMode.Set(PhoenixProcessMode.Proxy);
        try
        {
            await using Stream daemon = await ConnectOrStartAsync(cancellationToken)
                .ConfigureAwait(false);
            await RelayAsync(daemon, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (DaemonProxyFailureException failure)
        {
            return await UnavailableMcpShim.RunAsync(failure.Failure, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            return await UnavailableMcpShim.RunAsync(
                new DaemonUnavailableFailure(
                    "daemon_proxy_failed",
                    $"Phoenix daemon proxy failed before MCP relay ({ex.GetType().Name}).",
                    "Retry the MCP connection and inspect Phoenix daemon discovery state.",
                    Retryable: true),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Stream> ConnectOrStartAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ConnectAcceptedAsync(_endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (DaemonEndpointUnavailableException)
        {
            Stream? legacy = await TryConnectLegacyAsync(cancellationToken).ConfigureAwait(false);
            if (legacy is not null) return legacy;
            return await StartAndConnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Stream?> TryConnectLegacyAsync(CancellationToken cancellationToken)
    {
        foreach (DaemonEndpoint legacy in _legacyEndpoints)
        {
            try
            {
                return await ConnectAcceptedAsync(legacy, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DaemonEndpointUnavailableException)
            {
            }
        }
        return null;
    }

    internal async Task<Stream> ConnectAcceptedAsync(
        DaemonEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        Stream stream;
        try
        {
            stream = await DaemonTransport.ConnectAsync(
                endpoint, ConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (DaemonAuthorityException ex)
        {
            throw Failure(
                "daemon_endpoint_authority_failed",
                "Phoenix daemon endpoint authority could not be verified.",
                "Inspect the current-user runtime directory and remove only the unsafe endpoint after verification.",
                retryable: false,
                ex);
        }

        try
        {
            DaemonHandshakeRequest request = DaemonProtocol.CreateRequest(
                endpoint, _clientName, _rebuild);
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshake.CancelAfter(DaemonProtocol.HandshakeTimeout);
            DaemonHandshakeResponse? response;
            try
            {
                await DaemonProtocol.WriteRequestAsync(
                    stream, DaemonPreambleMode.Connect, request, handshake.Token)
                    .ConfigureAwait(false);
                response = await DaemonProtocol.ReadResponseAsync(
                    stream, handshake.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when
                (!cancellationToken.IsCancellationRequested)
            {
                throw Failure(
                    "daemon_handshake_timeout",
                    "Phoenix connected to the shared daemon endpoint, but the authority handshake did not complete before the protocol deadline.",
                    "Retry the MCP connection; if this repeats, close and restart active Phoenix sessions for this workspace.",
                    retryable: true,
                    ex);
            }
            catch (IOException ex)
            {
                throw new DaemonEndpointUnavailableException(
                    "Phoenix daemon endpoint disappeared during its authority handshake.", ex);
            }
            ValidateResponse(request, response);
            if (response!.Accepted) return stream;

            await stream.DisposeAsync().ConfigureAwait(false);
            if (response.Cause == "daemon_older_than_client")
            {
                await RetireOlderDaemonAsync(endpoint, cancellationToken).ConfigureAwait(false);
                return await StartAndConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            throw Refusal(response);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Stream> StartAndConnectAsync(CancellationToken cancellationToken)
    {
        DaemonTransport.EnsureRuntimeDirectory(_endpoint);
        DateTime deadline = DateTime.UtcNow + StartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Stream connected = await ConnectAcceptedAsync(_endpoint, cancellationToken)
                    .ConfigureAwait(false);
                DaemonStartupStatus.Delete(_endpoint);
                return connected;
            }
            catch (DaemonEndpointUnavailableException)
            {
            }

            if (IsMatchingDaemonProcessAlive())
            {
                await Task.Delay(75, cancellationToken).ConfigureAwait(false);
                continue;
            }

            DaemonUnavailableFailure? cached =
                DaemonStartupStatus.TryReadLiveFailure(_endpoint, _rebuild);
            if (cached is not null) throw Failure(SharedCachedFailure(cached));

            await using FileStream? startupLock = TryAcquireStartupLock();
            if (startupLock is null)
            {
                await Task.Delay(75, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                Stream connected = await ConnectAcceptedAsync(_endpoint, cancellationToken)
                    .ConfigureAwait(false);
                DaemonStartupStatus.Delete(_endpoint);
                return connected;
            }
            catch (DaemonEndpointUnavailableException)
            {
            }

            cached = DaemonStartupStatus.TryReadLiveFailure(_endpoint, _rebuild);
            if (cached is not null) throw Failure(SharedCachedFailure(cached));
            if (IsMatchingDaemonProcessAlive())
            {
                await Task.Delay(75, cancellationToken).ConfigureAwait(false);
                continue;
            }

            DaemonStartupStatus.Delete(_endpoint);
            Process launched;
            try
            {
                launched = LaunchBootstrap();
            }
            catch (DaemonProxyFailureException failure)
            {
                TryPublishStartupFailure(failure.Failure);
                throw;
            }
            using (launched)
            {
                DaemonStartupReport report;
                try
                {
                    report = await ReadStartupReportAsync(
                        launched, deadline, cancellationToken).ConfigureAwait(false);
                }
                catch (DaemonProxyFailureException failure)
                {
                    TryPublishStartupFailure(failure.Failure);
                    throw;
                }
                if (!report.Ready)
                {
                    await DaemonProcessIsolation.TerminateFailedStartupAsync(launched)
                        .ConfigureAwait(false);
                    DaemonUnavailableFailure failure = report.Failure!;
                    TryPublishStartupFailure(failure);
                    throw Failure(failure);
                }
            }

            DaemonStartupStatus.Delete(_endpoint);
            Stream? legacy = await TryConnectLegacyAsync(cancellationToken)
                .ConfigureAwait(false);
            if (legacy is not null) return legacy;
        }

        throw Failure(
            "daemon_startup_timeout",
            "Phoenix shared daemon did not become ready within the startup deadline; an older daemon at an undiscoverable environment-specific address may still own the writer lease.",
            "Close or restart pre-v0.12.61 Phoenix sessions for this workspace, then reconnect; do not kill an unverified process.",
            retryable: true);
    }

    private Process LaunchBootstrap()
    {
        try
        {
            return DaemonProcessIsolation.Launch(
                _endpoint, _indexDb, _rebuild, _keepAlive, _idleLingerForTest);
        }
        catch (Exception ex)
        {
            throw Failure(
                "daemon_launch_failed",
                $"Phoenix daemon process could not be launched ({ex.GetType().Name}).",
                "Verify the deployed Phoenix executable and retry the MCP connection.",
                retryable: true,
                ex);
        }
    }

    internal static async Task<DaemonStartupReport> ReadStartupReportAsync(
        Process bootstrap,
        DateTime deadline,
        CancellationToken cancellationToken,
        Stream? reportStreamForTest = null)
    {
        try
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw Failure(
                    "daemon_startup_timeout",
                    "Phoenix daemon startup deadline elapsed before a ready or refused report was received.",
                    "Retry the MCP connection; if this repeats, inspect the Phoenix server log.",
                    retryable: true);

            using var startup = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            startup.CancelAfter(remaining);
            try
            {
                Stream reportStream = reportStreamForTest ??
                    bootstrap.StandardOutput.BaseStream;
                return await DaemonStartupChannel.ReadAsync(reportStream, startup.Token)
                    .ConfigureAwait(false);
            }
            catch (EndOfStreamException ex)
            {
                int? exitCode = null;
                try
                {
                    await bootstrap.WaitForExitAsync(cancellationToken)
                        .WaitAsync(DaemonProtocol.HandshakeTimeout, cancellationToken)
                        .ConfigureAwait(false);
                    exitCode = bootstrap.ExitCode;
                }
                catch (Exception waitError) when (waitError is InvalidOperationException or
                                                  TimeoutException or OperationCanceledException)
                {
                }
                throw Failure(
                    "daemon_died_before_report",
                    exitCode is { } code
                        ? $"Phoenix daemon bootstrap exited with code {code} before reporting startup state."
                        : "Phoenix daemon bootstrap closed its startup channel before reporting startup state.",
                    "Retry the MCP connection; if this repeats, inspect the Phoenix server log for the startup failure.",
                    retryable: true,
                    ex);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw Failure(
                    "daemon_startup_report_timeout",
                    "Phoenix daemon did not report ready or refused before the startup deadline.",
                    "Retry the MCP connection; if this repeats, inspect the Phoenix server log for a blocked startup.",
                    retryable: true,
                    ex);
            }
            catch (IOException ex)
            {
                throw Failure(
                    "daemon_startup_report_invalid",
                    "Phoenix daemon returned an invalid private startup report.",
                    "Restart active Phoenix sessions for this workspace, then reconnect.",
                    retryable: true,
                    ex);
            }
        }
        catch
        {
            await DaemonProcessIsolation.TerminateFailedStartupAsync(bootstrap)
                .ConfigureAwait(false);
            throw;
        }
    }

    private static DaemonUnavailableFailure SharedCachedFailure(
        DaemonUnavailableFailure failure) => failure with
        {
            Recovery = failure.Recovery +
                " If the condition has already cleared, close the Phoenix session that first reported this startup failure, then reconnect."
        };

    private void TryPublishStartupFailure(DaemonUnavailableFailure failure)
    {
        try { DaemonStartupStatus.Publish(_endpoint, _rebuild, failure); }
        catch { }
    }

    private bool IsMatchingDaemonProcessAlive()
    {
        DaemonDescriptorRecord? descriptor = DaemonDescriptor.TryRead(_endpoint);
        if (descriptor is null || descriptor.Pid <= 0 ||
            !string.Equals(descriptor.ProcessMode, "daemon", StringComparison.Ordinal) ||
            !string.Equals(descriptor.EndpointKey, _endpoint.EndpointKey,
                StringComparison.Ordinal) ||
            !string.Equals(descriptor.WorkspaceIdentity, _endpoint.WorkspaceIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(descriptor.DatabaseKey, _endpoint.DatabaseKey,
                StringComparison.Ordinal) ||
            !DateTimeOffset.TryParse(descriptor.StartedAtUtc, out DateTimeOffset published))
            return false;
        try
        {
            using Process process = Process.GetProcessById(descriptor.Pid);
            return !process.HasExited &&
                process.StartTime.ToUniversalTime() <= published.UtcDateTime;
        }
        catch
        {
            return false;
        }
    }

    private async Task RetireOlderDaemonAsync(
        DaemonEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        using var takeover = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        takeover.CancelAfter(TakeoverTimeout);
        try
        {
            await DaemonRetirement.RetireOlderAsync(
                endpoint, _clientName, takeover.Token).ConfigureAwait(false);
        }
        catch (DaemonRetirementRefusedException ex)
        {
            throw Refusal(ex.Response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure(
                "daemon_takeover_timeout",
                "Older Phoenix daemon did not relinquish discovery authority within the takeover deadline.",
                "Allow existing agent requests to finish, then reconnect; do not kill an unverified process.",
                retryable: true);
        }
    }

    private FileStream? TryAcquireStartupLock()
    {
        try
        {
            var stream = new FileStream(
                _endpoint.StartupLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.WriteThrough);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_endpoint.StartupLockPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return stream;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task RelayAsync(Stream daemon, CancellationToken cancellationToken)
    {
        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();
        using var relay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task upstream = input.CopyToAsync(daemon, 64 * 1024, relay.Token);
        Task downstream = daemon.CopyToAsync(output, 64 * 1024, relay.Token);
        Task completed = await Task.WhenAny(upstream, downstream).ConfigureAwait(false);
        if (completed == downstream)
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        relay.Cancel();
        try { await completed.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }

        Task pending = completed == upstream ? downstream : upstream;
        try
        {
            await pending.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException)
        {
            _ = pending.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    internal void ValidateResponse(
        DaemonHandshakeRequest request,
        DaemonHandshakeResponse? response,
        bool allowOlderAcceptedResponse = false)
    {
        bool malformedRefusalWithoutNonce = response is
        {
            Accepted: false,
            Cause: "daemon_preamble_invalid",
            Nonce.Length: 0,
        };
        if (response is null || response.DaemonPid <= 0 ||
            !malformedRefusalWithoutNonce &&
            !string.Equals(response.Nonce, request.Nonce, StringComparison.Ordinal) ||
            !string.Equals(response.WorkspaceIdentity, _endpoint.WorkspaceIdentity,
                StringComparison.Ordinal) ||
            response.Cause != "daemon_index_destination_mismatch" &&
            !string.Equals(response.DatabaseKey, _endpoint.DatabaseKey, StringComparison.Ordinal))
            throw Failure(
                "daemon_response_authority_failed",
                "Phoenix daemon handshake response did not prove the requested authority.",
                "Inspect the current-user endpoint and reconnect after resolving the identity failure.",
                retryable: false);
        if (response.Accepted && !allowOlderAcceptedResponse &&
            (!string.Equals(response.ToolVersion, BuildInfo.Version, StringComparison.Ordinal) ||
             !string.Equals(response.SchemaVersion, BuildInfo.IndexSchema, StringComparison.Ordinal)))
            throw Failure(
                "daemon_response_version_mismatch",
                "Phoenix daemon accepted an incompatible tool or schema version.",
                "Restart Phoenix processes and reconnect.",
                retryable: false);
    }

    private static DaemonProxyFailureException Refusal(DaemonHandshakeResponse response) =>
        Failure(
            response.Cause,
            response.Detail,
            response.Cause switch
            {
                "daemon_newer_than_client" => "Restart or update this agent so it launches the deployed Phoenix version.",
                "daemon_index_destination_mismatch" => "Use the daemon's configured index destination or stop it gracefully before changing --index-db.",
                _ => "Resolve the reported daemon authority or compatibility condition, then reconnect.",
            },
            retryable: response.Cause is "daemon_older_than_client" or
                "daemon_startup_timeout");

    private static DaemonProxyFailureException Failure(
        string cause,
        string detail,
        string recovery,
        bool retryable,
        Exception? inner = null) =>
        new(new DaemonUnavailableFailure(cause, detail, recovery, retryable), inner);

    private static DaemonProxyFailureException Failure(
        DaemonUnavailableFailure failure,
        Exception? inner = null) => new(failure, inner);
}
