using CodeNav.Core.Indexing;

namespace CodeNav.Mcp.Daemon;

internal sealed class DaemonRetirementRefusedException : IOException
{
    internal DaemonHandshakeResponse Response { get; }

    internal DaemonRetirementRefusedException(DaemonHandshakeResponse response)
        : base($"Phoenix daemon refused graceful retirement ({response.Cause}).")
    {
        Response = response;
    }
}

internal sealed class DaemonWriterLeaseUnverifiableException : IOException
{
    internal DaemonWriterLeaseUnverifiableException()
        : base("Phoenix could not verify whether the retiring daemon released writer ownership.")
    {
    }
}

/// <summary>
/// Retires a daemon through the authority-bound transport handshake. This deliberately never
/// opens or terminates a process by PID: the descriptor is used only to observe that the
/// authenticated daemon generation has relinquished discovery and writer authority.
/// </summary>
internal static class DaemonRetirement
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RelinquishmentPollDelay = TimeSpan.FromMilliseconds(100);

    internal static Task RetireOlderAsync(
        DaemonEndpoint endpoint,
        string clientName,
        CancellationToken cancellationToken) =>
        RetireAsync(endpoint, clientName, forceNewerIdentity: false, cancellationToken);

    internal static async Task RetireForHarnessAsync(
        DaemonEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            await RetireAsync(
                endpoint,
                "integration-harness-retire",
                forceNewerIdentity: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (DaemonEndpointUnavailableException)
        {
            // An idle daemon may disappear between the harness's last MCP response and cleanup.
            // No endpoint means there is no process authority to retire, which is already clean.
        }
    }

    private static async Task RetireAsync(
        DaemonEndpoint endpoint,
        string clientName,
        bool forceNewerIdentity,
        CancellationToken cancellationToken)
    {
        DaemonDescriptorRecord? observed = DaemonDescriptor.TryRead(endpoint);
        await using Stream stream = await DaemonTransport.ConnectAsync(
            endpoint, ConnectTimeout, cancellationToken).ConfigureAwait(false);
        DaemonHandshakeRequest request = DaemonProtocol.CreateRequest(endpoint, clientName);
        if (forceNewerIdentity)
        {
            request = request with
            {
                ToolVersion = "2147483647.0.0.0",
                SchemaVersion = int.MaxValue.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            };
        }

        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(DaemonProtocol.HandshakeTimeout);
        await DaemonProtocol.WriteRequestAsync(
            stream,
            DaemonPreambleMode.RetireAndReplace,
            request,
            handshake.Token).ConfigureAwait(false);
        DaemonHandshakeResponse? response = await DaemonProtocol.ReadResponseAsync(
            stream, handshake.Token).ConfigureAwait(false);
        ValidateAuthority(endpoint, request, response);
        if (response is not { Accepted: true, Retiring: true })
            throw new DaemonRetirementRefusedException(response!);

        observed ??= DaemonDescriptor.TryRead(endpoint);
        await WaitForRelinquishmentAsync(
            endpoint, response.DaemonPid, observed, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task WaitForRelinquishmentAsync(
        DaemonEndpoint endpoint,
        int daemonPid,
        DaemonDescriptorRecord? observed,
        CancellationToken cancellationToken,
        Func<DaemonEndpoint, DaemonDescriptorRecord?>? readDescriptor = null,
        Func<DaemonEndpoint, CancellationToken, ValueTask<int>>? probeDaemonPid = null,
        Func<DaemonEndpoint, IndexLeaseAcquireResult>? probeWriterLease = null,
        TimeSpan? pollDelay = null)
    {
        readDescriptor ??= DaemonDescriptor.TryRead;
        probeDaemonPid ??= ProbeDaemonPidAsync;
        probeWriterLease ??= ProbeWriterLease;
        TimeSpan delay = pollDelay ?? RelinquishmentPollDelay;
        DaemonDescriptorRecord? expected =
            MatchesRetiringGeneration(endpoint, observed, daemonPid) ? observed : null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DaemonDescriptorRecord? current = readDescriptor(endpoint);
            if (expected is not null && SameGeneration(expected, current))
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (MatchesRetiringGeneration(endpoint, current, daemonPid))
            {
                expected = current;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // A different published generation proves discovery turnover. When descriptor
            // evidence disappears instead, verify the live endpoint rather than treating an
            // unreadable hint as proof that the authenticated daemon finished draining.
            if (expected is not null && current is not null) return;
            try
            {
                int currentPid = await probeDaemonPid(endpoint, cancellationToken)
                    .ConfigureAwait(false);
                if (currentPid != daemonPid) return;
            }
            catch (DaemonEndpointUnavailableException)
            {
                IndexLeaseAcquireResult leaseResult = probeWriterLease(endpoint);
                if (leaseResult == IndexLeaseAcquireResult.Acquired)
                    return;
                if (leaseResult == IndexLeaseAcquireResult.Failed)
                    throw new DaemonWriterLeaseUnverifiableException();
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IndexLeaseAcquireResult ProbeWriterLease(DaemonEndpoint endpoint)
    {
        // Writer ownership is workspace-identity keyed, so dbPath does not affect this probe.
        return IndexOwnershipLease.ProbeOwnerDetailed(
            endpoint.WorkspaceRoot,
            IndexBuilder.DefaultDbPath(endpoint.WorkspaceRoot));
    }

    private static async ValueTask<int> ProbeDaemonPidAsync(
        DaemonEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await DaemonTransport.ConnectAsync(
            endpoint, ConnectTimeout, cancellationToken).ConfigureAwait(false);
        DaemonHandshakeRequest request = DaemonProtocol.CreateRequest(
            endpoint, "retirement-observer") with
        {
            // Force a refusal after authority validation so the probe never opens an MCP
            // session or retires a successor generation.
            DatabaseKey = $"{endpoint.DatabaseKey}:retirement-observer",
        };
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(DaemonProtocol.HandshakeTimeout);
        await DaemonProtocol.WriteRequestAsync(
            stream, DaemonPreambleMode.Connect, request, handshake.Token).ConfigureAwait(false);
        DaemonHandshakeResponse? response = await DaemonProtocol.ReadResponseAsync(
            stream, handshake.Token).ConfigureAwait(false);
        ValidateAuthority(endpoint, request, response);
        return response!.DaemonPid;
    }

    private static void ValidateAuthority(
        DaemonEndpoint endpoint,
        DaemonHandshakeRequest request,
        DaemonHandshakeResponse? response)
    {
        if (response is null || response.DaemonPid <= 0 ||
            !string.Equals(response.Nonce, request.Nonce, StringComparison.Ordinal) ||
            !string.Equals(response.WorkspaceIdentity, endpoint.WorkspaceIdentity,
                StringComparison.Ordinal) ||
            response.Cause != "daemon_index_destination_mismatch" &&
            !string.Equals(response.DatabaseKey, endpoint.DatabaseKey, StringComparison.Ordinal))
            throw new DaemonAuthorityException(
                "Phoenix daemon retirement response did not prove the requested authority.");
    }

    private static bool MatchesRetiringGeneration(
        DaemonEndpoint endpoint,
        DaemonDescriptorRecord? descriptor,
        int daemonPid) =>
        descriptor is not null &&
        descriptor.Pid == daemonPid &&
        string.Equals(descriptor.EndpointKey, endpoint.EndpointKey, StringComparison.Ordinal) &&
        string.Equals(descriptor.WorkspaceIdentity, endpoint.WorkspaceIdentity,
            StringComparison.Ordinal) &&
        string.Equals(descriptor.DatabaseKey, endpoint.DatabaseKey, StringComparison.Ordinal);

    private static bool SameGeneration(
        DaemonDescriptorRecord expected,
        DaemonDescriptorRecord? current) =>
        current is not null &&
        current.Pid == expected.Pid &&
        string.Equals(current.EndpointKey, expected.EndpointKey, StringComparison.Ordinal) &&
        string.Equals(current.WorkspaceIdentity, expected.WorkspaceIdentity,
            StringComparison.Ordinal) &&
        string.Equals(current.DatabaseKey, expected.DatabaseKey, StringComparison.Ordinal) &&
        string.Equals(current.StartedAtUtc, expected.StartedAtUtc, StringComparison.Ordinal);
}
