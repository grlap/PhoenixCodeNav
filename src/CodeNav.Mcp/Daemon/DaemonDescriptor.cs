using System.Text.Json;
using CodeNav.Core.Indexing;

namespace CodeNav.Mcp.Daemon;

internal sealed record DaemonDescriptorRecord(
    int Pid,
    string ToolVersion,
    string SchemaVersion,
    string WorkspaceIdentity,
    string DatabaseKey,
    string EndpointKey,
    string Transport,
    string Address,
    string StartedAtUtc,
    string ProcessMode = "unknown");

internal static class DaemonDescriptor
{
    private const int MaxDescriptorBytes = 4 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    internal static void Publish(DaemonEndpoint endpoint, bool daemonProcess = false)
    {
        VerifyWorkspace(endpoint);
        DaemonTransport.EnsureRuntimeDirectory(endpoint);
        string directory = Path.GetDirectoryName(endpoint.DescriptorPath)!;
        VerifyRuntimeDirectory(endpoint, directory);
        var existing = new FileInfo(endpoint.DescriptorPath);
        if (existing.Exists && (existing.LinkTarget is not null ||
            (existing.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new IOException("Phoenix daemon descriptor path is an unsafe link.");

        var descriptor = new DaemonDescriptorRecord(
            Environment.ProcessId,
            BuildInfo.Version,
            BuildInfo.IndexSchema,
            endpoint.WorkspaceIdentity,
            endpoint.DatabaseKey,
            endpoint.EndpointKey,
            OperatingSystem.IsWindows() ? "named-pipe" : "unix-domain-socket",
            OperatingSystem.IsWindows() ? endpoint.PipeName : endpoint.SocketPath!,
            DateTimeOffset.UtcNow.ToString("O"),
            daemonProcess ? "daemon" : "non-daemon");
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(descriptor, Options);
        if (payload.Length > MaxDescriptorBytes)
            throw new IOException("Phoenix daemon descriptor exceeds its byte limit.");

        string temporary = Path.Combine(directory,
            $".daemon-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (new FileInfo(temporary).LinkTarget is not null)
                throw new IOException("Phoenix daemon descriptor staging file is unsafe.");
            File.Move(temporary, endpoint.DescriptorPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    internal static DaemonDescriptorRecord? TryRead(DaemonEndpoint endpoint)
    {
        try
        {
            VerifyRuntimeDirectory(endpoint, Path.GetDirectoryName(endpoint.DescriptorPath)!);
            var info = new FileInfo(endpoint.DescriptorPath);
            if (!info.Exists || info.LinkTarget is not null ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                info.Length is <= 0 or > MaxDescriptorBytes)
                return null;
            using var stream = new FileStream(endpoint.DescriptorPath, FileMode.Open,
                FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<DaemonDescriptorRecord>(stream, Options);
        }
        catch
        {
            return null;
        }
    }

    internal static void DeleteOwn(DaemonEndpoint endpoint)
    {
        DaemonDescriptorRecord? descriptor = TryRead(endpoint);
        if (descriptor is null || descriptor.Pid != Environment.ProcessId ||
            !string.Equals(descriptor.EndpointKey, endpoint.EndpointKey, StringComparison.Ordinal) ||
            !string.Equals(descriptor.WorkspaceIdentity, endpoint.WorkspaceIdentity,
                StringComparison.Ordinal))
            return;
        try { File.Delete(endpoint.DescriptorPath); } catch { }
    }

    private static void VerifyWorkspace(DaemonEndpoint endpoint)
    {
        if (!WorkspacePhysicalIdentity.TryGet(endpoint.WorkspaceRoot, out string identity) ||
            !string.Equals(identity, endpoint.WorkspaceIdentity, StringComparison.Ordinal))
            throw new IOException("Phoenix daemon workspace identity changed before discovery publication.");
    }

    private static void VerifyRuntimeDirectory(DaemonEndpoint endpoint, string directory)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(Path.GetFullPath(directory),
                Path.GetFullPath(endpoint.RuntimeDirectory), comparison))
            throw new IOException("Phoenix daemon descriptor escaped its runtime directory.");

        var info = new DirectoryInfo(directory);
        if (!info.Exists || info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Phoenix daemon runtime directory is unsafe.");
        if (!OperatingSystem.IsWindows())
            DaemonUnixFileAuthority.VerifyOwnerOnlyDirectory(directory);
    }
}
