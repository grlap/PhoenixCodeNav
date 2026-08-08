using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeNav.Core;
using CodeNav.Core.Indexing;

namespace CodeNav.Mcp.Daemon;

internal enum DaemonPreambleMode : byte
{
    Connect = 0,
    RetireAndReplace = 1,
    Response = 2,
}

internal sealed record DaemonHandshakeRequest(
    string ToolVersion,
    string SchemaVersion,
    string WorkspaceIdentity,
    string WorkspaceRoot,
    string UserIdentity,
    string DatabaseKey,
    int ClientPid,
    string ClientName,
    string Nonce,
    bool Rebuild = false);

internal sealed record DaemonHandshakeResponse(
    bool Accepted,
    string Cause,
    string Detail,
    string ToolVersion,
    string SchemaVersion,
    string WorkspaceIdentity,
    string DatabaseKey,
    int DaemonPid,
    string Nonce,
    bool Retiring = false);

/// <summary>
/// Frozen pre-MCP framing. Bytes 0..15 are permanent: magic, preamble version, mode, reserved,
/// and a big-endian payload length. Future ordinary preambles may change their JSON payload under
/// another version, but must retain the v1 retire request so every successor can retire v1 safely.
/// </summary>
internal static partial class DaemonProtocol
{
    internal const byte CurrentVersion = 1;
    internal const int HeaderBytes = 16;
    internal const int MaxPayloadBytes = 8 * 1024;
    internal static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private static ReadOnlySpan<byte> Magic => "PHXDMN1\0"u8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[0-9A-Fa-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex NoncePattern();

    internal static DaemonHandshakeRequest CreateRequest(
        DaemonEndpoint endpoint,
        string clientName,
        bool rebuild = false) =>
        new(
            BuildInfo.Version,
            BuildInfo.IndexSchema,
            endpoint.WorkspaceIdentity,
            endpoint.WorkspaceRoot,
            endpoint.UserIdentity,
            endpoint.DatabaseKey,
            Environment.ProcessId,
            BoundClientName(clientName),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
            rebuild);

    internal static async ValueTask WriteRequestAsync(
        Stream stream,
        DaemonPreambleMode mode,
        DaemonHandshakeRequest request,
        CancellationToken cancellationToken)
    {
        if (mode is not (DaemonPreambleMode.Connect or DaemonPreambleMode.RetireAndReplace))
            throw new ArgumentOutOfRangeException(nameof(mode));
        await WriteFrameAsync(stream, CurrentVersion, mode, request, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async ValueTask<(byte Version, DaemonPreambleMode Mode,
        DaemonHandshakeRequest? Request)> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        (byte version, DaemonPreambleMode mode, byte[] payload) =
            await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        if (mode is not (DaemonPreambleMode.Connect or DaemonPreambleMode.RetireAndReplace))
            return (version, mode, null);
        try
        {
            return (version, mode,
                JsonSerializer.Deserialize<DaemonHandshakeRequest>(payload, JsonOptions));
        }
        catch (JsonException)
        {
            return (version, mode, null);
        }
    }

    internal static ValueTask WriteResponseAsync(
        Stream stream,
        DaemonHandshakeResponse response,
        CancellationToken cancellationToken) =>
        WriteFrameAsync(stream, CurrentVersion, DaemonPreambleMode.Response,
            response, cancellationToken);

    internal static async ValueTask<DaemonHandshakeResponse?> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        (byte version, DaemonPreambleMode mode, byte[] payload) =
            await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        if (version != CurrentVersion || mode != DaemonPreambleMode.Response)
            return null;
        try
        {
            return JsonSerializer.Deserialize<DaemonHandshakeResponse>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static DaemonHandshakeResponse Evaluate(
        DaemonEndpoint endpoint,
        byte preambleVersion,
        DaemonPreambleMode mode,
        DaemonHandshakeRequest? request)
    {
        string nonce = request?.Nonce ?? "";
        DaemonHandshakeResponse Refuse(string cause, string detail) => new(
            false,
            cause,
            detail,
            BuildInfo.Version,
            BuildInfo.IndexSchema,
            endpoint.WorkspaceIdentity,
            endpoint.DatabaseKey,
            Environment.ProcessId,
            nonce);

        if (preambleVersion != CurrentVersion)
            return Refuse("daemon_preamble_incompatible",
                "Phoenix daemon handshake version is incompatible; update Phoenix and retry graceful replacement.");
        if (mode is not (DaemonPreambleMode.Connect or DaemonPreambleMode.RetireAndReplace) ||
            request is null)
            return Refuse("daemon_preamble_invalid", "Phoenix daemon handshake is malformed.");
        if (!NoncePattern().IsMatch(request.Nonce))
            return Refuse("daemon_nonce_invalid", "Phoenix daemon handshake nonce is invalid.");
        if (request.ClientPid <= 0 || string.IsNullOrWhiteSpace(request.ClientName) ||
            request.ClientName.Length > 128)
            return Refuse("daemon_client_invalid", "Phoenix daemon client identity is invalid.");
        if (!string.Equals(request.UserIdentity, endpoint.UserIdentity, StringComparison.Ordinal))
            return Refuse("daemon_user_mismatch", "Phoenix daemon belongs to another operating-system user.");
        if (!Path.IsPathFullyQualified(request.WorkspaceRoot) ||
            !WorkspacePhysicalIdentity.TryGet(request.WorkspaceRoot, out string liveIdentity) ||
            !string.Equals(request.WorkspaceIdentity, liveIdentity, StringComparison.Ordinal) ||
            !string.Equals(request.WorkspaceIdentity, endpoint.WorkspaceIdentity,
                StringComparison.Ordinal))
            return Refuse("daemon_workspace_mismatch", "Phoenix daemon belongs to another physical worktree.");
        if (!string.Equals(request.DatabaseKey, endpoint.DatabaseKey, StringComparison.Ordinal))
            return Refuse("daemon_index_destination_mismatch",
                "Phoenix daemon is bound to a different index destination for this worktree.");

        int versionOrder = CompareVersion(request.ToolVersion, BuildInfo.Version);
        int schemaOrder = CompareSchema(request.SchemaVersion, BuildInfo.IndexSchema);
        bool exact = versionOrder == 0 && schemaOrder == 0;
        if (mode == DaemonPreambleMode.RetireAndReplace)
        {
            if (versionOrder < 0 || (versionOrder == 0 && schemaOrder <= 0))
                return Refuse("daemon_retire_not_newer",
                    "Only a newer Phoenix client may retire this daemon.");
            return new DaemonHandshakeResponse(
                true,
                "daemon_retiring",
                "Older Phoenix daemon accepted graceful retirement.",
                BuildInfo.Version,
                BuildInfo.IndexSchema,
                endpoint.WorkspaceIdentity,
                endpoint.DatabaseKey,
                Environment.ProcessId,
                request.Nonce,
                Retiring: true);
        }

        if (!exact)
        {
            bool clientOlder = versionOrder < 0 || (versionOrder == 0 && schemaOrder < 0);
            return Refuse(
                clientOlder ? "daemon_newer_than_client" : "daemon_older_than_client",
                clientOlder
                    ? "Phoenix daemon is newer; restart or update this agent."
                    : "Phoenix daemon is older; graceful replacement is required.");
        }

        return new DaemonHandshakeResponse(
            true,
            "ok",
            "Phoenix daemon session accepted.",
            BuildInfo.Version,
            BuildInfo.IndexSchema,
            endpoint.WorkspaceIdentity,
            endpoint.DatabaseKey,
            Environment.ProcessId,
            request.Nonce);
    }

    private static async ValueTask WriteFrameAsync<T>(
        Stream stream,
        byte version,
        DaemonPreambleMode mode,
        T payload,
        CancellationToken cancellationToken)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        if (json.Length > MaxPayloadBytes)
            throw new IOException("Phoenix daemon handshake payload exceeds its byte limit.");

        byte[] header = new byte[HeaderBytes];
        Magic.CopyTo(header);
        header[8] = version;
        header[9] = (byte)mode;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12), json.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<(byte Version, DaemonPreambleMode Mode, byte[] Payload)>
        ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[HeaderBytes];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic) ||
            header[10] != 0 || header[11] != 0)
            throw new IOException("Phoenix daemon handshake prefix is invalid.");
        int payloadLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(12));
        if (payloadLength is < 2 or > MaxPayloadBytes)
            throw new IOException("Phoenix daemon handshake payload length is invalid.");
        byte[] payload = new byte[payloadLength];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return (header[8], (DaemonPreambleMode)header[9], payload);
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
                throw new EndOfStreamException("Phoenix daemon handshake ended early.");
            offset += read;
        }
    }

    private static int CompareVersion(string client, string daemon)
    {
        if (!Version.TryParse(client, out Version? clientVersion) ||
            !Version.TryParse(daemon, out Version? daemonVersion))
            return string.Compare(client, daemon, StringComparison.Ordinal);
        return clientVersion.CompareTo(daemonVersion);
    }

    private static int CompareSchema(string client, string daemon)
    {
        if (int.TryParse(client, out int clientSchema) &&
            int.TryParse(daemon, out int daemonSchema))
            return clientSchema.CompareTo(daemonSchema);
        return string.Compare(client, daemon, StringComparison.Ordinal);
    }

    private static string BoundClientName(string clientName)
    {
        string trimmed = string.IsNullOrWhiteSpace(clientName) ? "unknown" : clientName.Trim();
        return trimmed.Length <= 128 ? trimmed : trimmed[..128];
    }
}
