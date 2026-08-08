using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using CodeNav.Core;
using CodeNav.Core.Indexing;

namespace CodeNav.Mcp.Daemon;

internal sealed class DaemonRuntimeDirectoryUnavailableException : IOException
{
    internal DaemonRuntimeDirectoryUnavailableException(string message) : base(message) { }
}

/// <summary>Derives one stable, version-independent local endpoint from user and physical worktree.</summary>
internal sealed record DaemonEndpoint(
    string WorkspaceRoot,
    string WorkspaceIdentity,
    string UserIdentity,
    string DatabaseKey,
    string EndpointKey,
    string PipeName,
    string? SocketPath,
    string RuntimeDirectory,
    string StartupLockPath,
    string DescriptorPath)
{
    private const string ProductDirectory = "phoenix-codenav";

    internal static DaemonEndpoint Create(string workspaceRoot, string? indexDb)
    {
        string lexicalRoot = WorkspacePaths.NormalizeFullForComparison(workspaceRoot);
        string physicalIdentity = WorkspacePhysicalIdentity.Get(lexicalRoot);
        string userIdentity = CurrentUserIdentity();
        string endpointKey = Hash($"{userIdentity}\0{physicalIdentity}")[..32].ToLowerInvariant();
        string database = Path.GetFullPath(indexDb ?? IndexBuilder.DefaultDbPath(lexicalRoot));
        string databaseKey = Hash(WorkspacePaths.NormalizeFullForComparison(database));

        string runtimeDirectory;
        string? socketPath;
        string pipeName = $"PhoenixCodeNav.{endpointKey}";
        if (OperatingSystem.IsWindows())
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
                throw new IOException("current-user local application data directory is unavailable");
            runtimeDirectory = Path.Combine(local, "PhoenixCodeNav", "runtime");
            socketPath = null;
        }
        else
        {
            runtimeDirectory = SelectUnixRuntimeDirectory();
            socketPath = Path.Combine(runtimeDirectory, endpointKey + ".sock");
        }

        return new DaemonEndpoint(
            lexicalRoot,
            physicalIdentity,
            userIdentity,
            databaseKey,
            endpointKey,
            pipeName,
            socketPath,
            runtimeDirectory,
            Path.Combine(runtimeDirectory, endpointKey + ".startup.lock"),
            Path.Combine(runtimeDirectory, endpointKey + ".daemon.json"));
    }

    private static string SelectUnixRuntimeDirectory()
    {
        uint userId = GetEffectiveUserId();
        return SelectUnixRuntimeDirectory(
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
            Path.GetTempPath(),
            "/tmp",
            userId);
    }

    internal static string SelectUnixRuntimeDirectory(
        string? xdgParent,
        string? temporaryParent,
        string? shortFallbackParent,
        uint userId)
    {
        if (TryUnixRuntimeCandidate(xdgParent, ProductDirectory, out string runtime))
            return runtime;
        if (TryUnixRuntimeCandidate(
                temporaryParent, $"{ProductDirectory}-{userId}", out runtime))
            return runtime;
        if (TryUnixRuntimeCandidate(
                shortFallbackParent, $"{ProductDirectory}-{userId}", out runtime))
            return runtime;
        throw new DaemonRuntimeDirectoryUnavailableException(
            "Phoenix daemon runtime path cannot host a Unix-domain socket.");
    }

    private static bool TryUnixRuntimeCandidate(
        string? parent,
        string directoryName,
        out string runtimeDirectory)
    {
        runtimeDirectory = "";
        if (string.IsNullOrWhiteSpace(parent) || !Path.IsPathFullyQualified(parent))
            return false;

        try
        {
            string resolvedParent = DaemonUnixFileAuthority.ResolveExistingDirectory(
                Path.GetFullPath(parent));
            string candidate = Path.Combine(resolvedParent, directoryName);
            if (!CanHostUnixSocket(candidate)) return false;
            DaemonUnixFileAuthority.EnsureOwnerOnlyDirectory(candidate);
            runtimeDirectory = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or
                                   NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool CanHostUnixSocket(string runtimeDirectory)
    {
        try
        {
            _ = new UnixDomainSocketEndPoint(
                Path.Combine(runtimeDirectory, new string('0', 32) + ".sock"));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string CurrentUserIdentity()
    {
        if (OperatingSystem.IsWindows())
        {
            string? sid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrWhiteSpace(sid))
                throw new IOException("current Windows user SID is unavailable");
            return "sid:" + sid;
        }

        return "uid:" + GetEffectiveUserId();
    }

    private static uint GetEffectiveUserId()
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        return geteuid();
    }

    internal static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [DllImport("libc")]
    private static extern uint geteuid();
}
