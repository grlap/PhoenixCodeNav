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
    string StartupStatusPath,
    string DescriptorPath,
    bool IsStableRuntime)
{
    private const string ProductDirectory = "phoenix-codenav";

    private sealed record UnixRuntimeInputs(
        string? StableParent,
        string? XdgParent,
        string? TemporaryParent,
        uint UserId);

    internal static DaemonEndpoint Create(string workspaceRoot, string? indexDb) =>
        Create(workspaceRoot, indexDb, unixRuntimeInputs: null);

    internal static DaemonEndpoint CreateForUnixRuntimeTest(
        string workspaceRoot,
        string? indexDb,
        string? stableParent,
        string? xdgParent,
        string? temporaryParent,
        uint userId)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return Create(
            workspaceRoot,
            indexDb,
            new UnixRuntimeInputs(
                stableParent, xdgParent, temporaryParent, userId));
    }

    private static DaemonEndpoint Create(
        string workspaceRoot,
        string? indexDb,
        UnixRuntimeInputs? unixRuntimeInputs)
    {
        string lexicalRoot = WorkspacePaths.NormalizeFullForComparison(workspaceRoot);
        string physicalIdentity = WorkspacePhysicalIdentity.Get(lexicalRoot);
        string userIdentity = CurrentUserIdentity();
        string endpointKey = Hash($"{userIdentity}\0{physicalIdentity}")[..32].ToLowerInvariant();
        string database = Path.GetFullPath(indexDb ?? IndexBuilder.DefaultDbPath(lexicalRoot));
        string databaseKey = Hash(WorkspacePaths.NormalizeFullForComparison(database));

        string runtimeDirectory;
        string? socketPath;
        bool stableRuntime;
        string pipeName = $"PhoenixCodeNav.{endpointKey}";
        if (OperatingSystem.IsWindows())
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
                throw new IOException("current-user local application data directory is unavailable");
            runtimeDirectory = Path.Combine(local, "PhoenixCodeNav", "runtime");
            socketPath = null;
            stableRuntime = true;
        }
        else
        {
            UnixRuntimeInputs inputs = unixRuntimeInputs ?? new UnixRuntimeInputs(
                "/tmp",
                Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
                Path.GetTempPath(),
                GetEffectiveUserId());
            (runtimeDirectory, stableRuntime) = SelectUnixRuntimeDirectoryWithKind(
                inputs.StableParent,
                inputs.XdgParent,
                inputs.TemporaryParent,
                inputs.UserId);
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
            Path.Combine(runtimeDirectory, endpointKey + ".startup.json"),
            Path.Combine(runtimeDirectory, endpointKey + ".daemon.json"),
            stableRuntime);
    }

    internal static string SelectUnixRuntimeDirectory(
        string? stableParent,
        string? xdgParent,
        string? temporaryParent,
        uint userId) =>
        SelectUnixRuntimeDirectoryWithKind(
            stableParent, xdgParent, temporaryParent, userId).RuntimeDirectory;

    private static (string RuntimeDirectory, bool IsStable)
        SelectUnixRuntimeDirectoryWithKind(
            string? stableParent,
            string? xdgParent,
            string? temporaryParent,
            uint userId)
    {
        // Discovery must not depend on the launching host's environment. MCP clients commonly
        // inherit different XDG_RUNTIME_DIR/TMPDIR values even though they share one OS user and
        // one writer lease. Prefer the short, owner-verified per-user location so every session
        // derives the same endpoint; environment-specific locations are availability fallbacks.
        if (TryUnixRuntimeCandidate(
                stableParent, $"{ProductDirectory}-{userId}", out string runtime))
            return (runtime, true);
        if (TryUnixRuntimeCandidate(xdgParent, ProductDirectory, out runtime))
            return (runtime, false);
        if (TryUnixRuntimeCandidate(
                temporaryParent, $"{ProductDirectory}-{userId}", out runtime))
            return (runtime, false);
        throw new DaemonRuntimeDirectoryUnavailableException(
            "Phoenix daemon runtime path cannot host a Unix-domain socket.");
    }

    /// <summary>
    /// Returns only already-existing, owner-authorized addresses used by v0.12.60 and earlier.
    /// Probing them preserves frozen-preamble retirement across the stable-address migration without
    /// creating environment-specific directories that could themselves become new discovery roots.
    /// </summary>
    internal static IReadOnlyList<DaemonEndpoint> LegacyUnixCandidates(
        DaemonEndpoint primary)
    {
        if (OperatingSystem.IsWindows()) return [];
        return LegacyUnixCandidates(
            primary,
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
            Path.GetTempPath(),
            GetEffectiveUserId());
    }

    internal static IReadOnlyList<DaemonEndpoint> LegacyUnixCandidates(
        DaemonEndpoint primary,
        string? xdgParent,
        string? temporaryParent,
        uint userId)
    {
        if (OperatingSystem.IsWindows()) return [];
        var candidates = new List<DaemonEndpoint>(2);
        AddExistingLegacyCandidate(xdgParent, ProductDirectory);
        AddExistingLegacyCandidate(temporaryParent, $"{ProductDirectory}-{userId}");
        return candidates;

        void AddExistingLegacyCandidate(string? parent, string directoryName)
        {
            if (!TryExistingUnixRuntimeCandidate(parent, directoryName, out string runtime) ||
                string.Equals(runtime, primary.RuntimeDirectory, StringComparison.Ordinal) ||
                candidates.Any(candidate => string.Equals(
                    candidate.RuntimeDirectory, runtime, StringComparison.Ordinal)))
                return;
            candidates.Add(AtUnixRuntime(primary, runtime, stableRuntime: false));
        }
    }

    private static DaemonEndpoint AtUnixRuntime(
        DaemonEndpoint endpoint,
        string runtimeDirectory,
        bool stableRuntime) =>
        endpoint with
        {
            SocketPath = Path.Combine(runtimeDirectory, endpoint.EndpointKey + ".sock"),
            RuntimeDirectory = runtimeDirectory,
            StartupLockPath = Path.Combine(
                runtimeDirectory, endpoint.EndpointKey + ".startup.lock"),
            StartupStatusPath = Path.Combine(
                runtimeDirectory, endpoint.EndpointKey + ".startup.json"),
            DescriptorPath = Path.Combine(
                runtimeDirectory, endpoint.EndpointKey + ".daemon.json"),
            IsStableRuntime = stableRuntime,
        };

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

    private static bool TryExistingUnixRuntimeCandidate(
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
            DaemonUnixFileAuthority.VerifyOwnerOnlyDirectory(candidate);
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
