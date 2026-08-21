using System.Diagnostics;
using System.Text.Json;

namespace CodeNav.Mcp.Daemon;

internal sealed record DaemonStartupStatusRecord(
    int OwnerPid,
    long OwnerStartedUtcTicks,
    string ToolVersion,
    string SchemaVersion,
    string WorkspaceIdentity,
    string DatabaseKey,
    string EndpointKey,
    bool Rebuild,
    DaemonUnavailableFailure Failure);

/// <summary>
/// Advisory startup refusal shared with concurrent proxies. Only a live owning proxy and an exact
/// endpoint/build/attempt match can make the record decisive; malformed or stale files are ignored.
/// </summary>
internal static class DaemonStartupStatus
{
    private static readonly JsonSerializerOptions Options =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static void Publish(
        DaemonEndpoint endpoint,
        bool rebuild,
        DaemonUnavailableFailure failure)
    {
        DaemonTransport.EnsureRuntimeDirectory(endpoint);
        VerifyRuntimeDirectory(endpoint);
        RejectUnsafeExistingPath(endpoint.StartupStatusPath);

        using Process current = Process.GetCurrentProcess();
        var record = new DaemonStartupStatusRecord(
            Environment.ProcessId,
            current.StartTime.ToUniversalTime().Ticks,
            BuildInfo.Version,
            BuildInfo.IndexSchema,
            endpoint.WorkspaceIdentity,
            endpoint.DatabaseKey,
            endpoint.EndpointKey,
            rebuild,
            failure);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(record, Options);
        if (payload.Length is < 2 or > DaemonProtocol.MaxPayloadBytes)
            throw new IOException("Phoenix daemon startup status exceeds its byte limit.");

        string temporary = Path.Combine(endpoint.RuntimeDirectory,
            $".startup-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            RejectUnsafeExistingPath(temporary);
            File.Move(temporary, endpoint.StartupStatusPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    internal static DaemonUnavailableFailure? TryReadLiveFailure(
        DaemonEndpoint endpoint,
        bool rebuild)
    {
        try
        {
            VerifyRuntimeDirectory(endpoint);
            var info = new FileInfo(endpoint.StartupStatusPath);
            if (!info.Exists || info.LinkTarget is not null ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                info.Length is <= 0 or > DaemonProtocol.MaxPayloadBytes)
                return null;
            using var stream = new FileStream(endpoint.StartupStatusPath, FileMode.Open,
                FileAccess.Read, FileShare.Read | FileShare.Delete);
            DaemonStartupStatusRecord? record =
                JsonSerializer.Deserialize<DaemonStartupStatusRecord>(stream, Options);
            if (record is null || record.OwnerPid <= 0 ||
                record.OwnerStartedUtcTicks <= 0 || record.Failure is null ||
                record.Failure.Cause.Length == 0 ||
                !string.Equals(record.ToolVersion, BuildInfo.Version, StringComparison.Ordinal) ||
                !string.Equals(record.SchemaVersion, BuildInfo.IndexSchema,
                    StringComparison.Ordinal) ||
                !string.Equals(record.WorkspaceIdentity, endpoint.WorkspaceIdentity,
                    StringComparison.Ordinal) ||
                !string.Equals(record.DatabaseKey, endpoint.DatabaseKey,
                    StringComparison.Ordinal) ||
                !string.Equals(record.EndpointKey, endpoint.EndpointKey,
                    StringComparison.Ordinal) || record.Rebuild != rebuild ||
                !IsOwnerAlive(record.OwnerPid, record.OwnerStartedUtcTicks))
                return null;
            return record.Failure;
        }
        catch
        {
            return null;
        }
    }

    internal static void Delete(DaemonEndpoint endpoint)
    {
        try
        {
            VerifyRuntimeDirectory(endpoint);
            var info = new FileInfo(endpoint.StartupStatusPath);
            if (!info.Exists || info.LinkTarget is not null ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return;
            File.Delete(endpoint.StartupStatusPath);
        }
        catch
        {
        }
    }

    private static bool IsOwnerAlive(int processId, long startedUtcTicks)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited &&
                process.StartTime.ToUniversalTime().Ticks == startedUtcTicks;
        }
        catch
        {
            return false;
        }
    }

    private static void VerifyRuntimeDirectory(DaemonEndpoint endpoint)
    {
        var info = new DirectoryInfo(endpoint.RuntimeDirectory);
        if (!info.Exists || info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Phoenix daemon runtime directory is unsafe.");
        if (!OperatingSystem.IsWindows())
            DaemonUnixFileAuthority.VerifyOwnerOnlyDirectory(endpoint.RuntimeDirectory);
    }

    private static void RejectUnsafeExistingPath(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists && (info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new IOException("Phoenix daemon startup status path is an unsafe link.");
    }
}
