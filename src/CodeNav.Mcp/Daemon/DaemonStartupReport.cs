using System.Buffers.Binary;
using System.Text.Json;
using CodeNav.Core.Indexing;

namespace CodeNav.Mcp.Daemon;

internal sealed record DaemonStartupReport(
    bool Ready,
    int DaemonPid,
    DaemonUnavailableFailure? Failure)
{
    internal static DaemonStartupReport ReadyReport(int daemonPid) =>
        new(true, daemonPid, null);

    internal static DaemonStartupReport Refused(
        int daemonPid,
        DaemonUnavailableFailure failure) =>
        new(false, daemonPid, failure);
}

internal static class DaemonStartupFailures
{
    internal static DaemonUnavailableFailure FromIndexManager(IndexManager manager) =>
        FromIndexFailure(manager.StartupFailureCause);

    internal static DaemonUnavailableFailure FromIndexFailure(
        IndexStartupFailureCause cause) => cause switch
        {
            IndexStartupFailureCause.RebuildRequired => new DaemonUnavailableFailure(
                "daemon_index_rebuild_required",
                "Phoenix found an index database that moved with its workspace and requires explicit rebinding.",
                "Approve an explicit full index rebuild with the existing --rebuild action, then reconnect.",
                Retryable: false),
            IndexStartupFailureCause.DestinationForeign => new DaemonUnavailableFailure(
                "daemon_index_destination_foreign",
                "Phoenix index destination belongs to a different workspace.",
                "Choose the correct index destination for this workspace, then reconnect.",
                Retryable: false),
            IndexStartupFailureCause.DestinationUnsafe or
            IndexStartupFailureCause.DestinationChanged => new DaemonUnavailableFailure(
                "daemon_index_destination_unsafe",
                "Phoenix could not establish safe authority over the index destination.",
                "Verify index-path ownership and remove unsafe links or replacements, then reconnect.",
                Retryable: false),
            IndexStartupFailureCause.WriterLeaseContended => new DaemonUnavailableFailure(
                "daemon_writer_unavailable",
                "Another Phoenix process currently owns the workspace index writer lease.",
                "Allow the existing Phoenix process to finish or close naturally, then reconnect.",
                Retryable: true),
            IndexStartupFailureCause.WriterAuthorityUnavailable => new DaemonUnavailableFailure(
                "daemon_writer_authority_unavailable",
                "Phoenix could not verify or acquire safe index writer authority.",
                "Verify current-user index ownership and reconnect after the authority blocker clears.",
                Retryable: true),
            IndexStartupFailureCause.DestinationValidationFailed => new DaemonUnavailableFailure(
                "daemon_index_validation_failed",
                "Phoenix failed while validating the configured index destination.",
                "Inspect the Phoenix server log, resolve the validation failure, then reconnect.",
                Retryable: true),
            _ => new DaemonUnavailableFailure(
                "daemon_index_startup_failed",
                "Phoenix could not establish the shared index writer during daemon startup.",
                "Resolve the index startup condition reported in the Phoenix server log, then reconnect.",
                Retryable: true),
        };

    internal static DaemonUnavailableFailure Unexpected(Exception exception) =>
        new(
            "daemon_startup_exception",
            $"Phoenix daemon failed before publishing its endpoint ({exception.GetType().Name}).",
            "Retry the MCP connection; if this repeats, inspect the Phoenix server log for the startup failure.",
            Retryable: true);
}

/// <summary>
/// One private, bounded daemon-to-bootstrap-to-proxy startup frame. This is internal process
/// plumbing, not an MCP or command-line protocol.
/// </summary>
internal static class DaemonStartupChannel
{
    private static readonly JsonSerializerOptions Options =
        new(JsonSerializerDefaults.Web);

    internal static async ValueTask WriteAsync(
        Stream stream,
        DaemonStartupReport report,
        CancellationToken cancellationToken = default)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(report, Options);
        if (payload.Length is < 2 or > DaemonProtocol.MaxPayloadBytes)
            throw new IOException("Phoenix daemon startup report exceeds its byte limit.");

        byte[] length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<DaemonStartupReport> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        byte[] length = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, length, cancellationToken).ConfigureAwait(false);
        int payloadLength = BinaryPrimitives.ReadInt32BigEndian(length);
        if (payloadLength is < 2 or > DaemonProtocol.MaxPayloadBytes)
            throw new IOException("Phoenix daemon startup report length is invalid.");

        byte[] payload = new byte[payloadLength];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        DaemonStartupReport? report;
        try
        {
            report = JsonSerializer.Deserialize<DaemonStartupReport>(payload, Options);
        }
        catch (JsonException ex)
        {
            throw new IOException("Phoenix daemon startup report is malformed.", ex);
        }

        bool validReady = report is { Ready: true, DaemonPid: > 0, Failure: null };
        bool validRefusal = report is
        {
            Ready: false,
            DaemonPid: >= 0,
            Failure.Cause.Length: > 0,
            Failure.Detail.Length: > 0,
            Failure.Recovery.Length: > 0,
        };
        if (!validReady && !validRefusal)
            throw new IOException("Phoenix daemon startup report is incomplete.");
        return report!;
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = await stream.ReadAsync(destination[offset..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException(
                    "Phoenix daemon startup channel closed before a report was received.");
            offset += read;
        }
    }
}

internal sealed class DaemonStartupReporter
{
    private readonly Stream _stream;
    private int _reported;

    internal DaemonStartupReporter(Stream stream) => _stream = stream;

    internal async ValueTask ReportAsync(
        DaemonStartupReport report,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _reported, 1) != 0) return;
        try
        {
            await DaemonStartupChannel.WriteAsync(_stream, report, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The launching proxy may have disappeared. Startup reporting must never turn an
            // otherwise valid daemon into a second failure mode.
        }
        finally
        {
            try { DaemonProcessIsolation.DetachStandardOutput(); }
            catch
            {
                // The startup frame is already decisive. Detach failure must not kill a
                // healthy daemon or replace its typed refusal with a second failure mode.
            }
        }
    }
}
