using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using CodeNav.Core;
using CodeNav.Core.Indexing;

namespace CodeNav.Mcp.Daemon;

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
        string? xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(xdg) && Path.IsPathFullyQualified(xdg))
            return Path.Combine(
                DaemonUnixFileAuthority.ResolveExistingDirectory(Path.GetFullPath(xdg)),
                ProductDirectory);

        return Path.Combine(
            DaemonUnixFileAuthority.ResolveExistingDirectory(Path.GetTempPath()),
            $"{ProductDirectory}-{GetEffectiveUserId()}");
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
