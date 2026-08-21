using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeNav.Portal;

/// <summary>
/// Coordinates one launcher-owned portal per normalized workspace path without coupling the portal to
/// an MCP process lifetime. The exclusive file handle is released automatically if the owner
/// exits; the descriptor is accepted only while its loopback health endpoint proves the same
/// private portal session identity.
/// </summary>
internal sealed class PortalLaunchCoordinator : IAsyncDisposable
{
    internal const int ProtocolVersion = 1;
    private const int MaxDescriptorBytes = 16 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly HttpClient HealthClient = CreateHealthClient();

    private readonly FileStream? _ownerLock;
    private readonly string _descriptorPath;
    private bool _descriptorPublished;

    private PortalLaunchCoordinator(FileStream ownerLock, string descriptorPath)
    {
        _ownerLock = ownerLock;
        _descriptorPath = descriptorPath;
        IsOwner = true;
    }

    private PortalLaunchCoordinator(string descriptorPath, PortalLaunchHandshake reused)
    {
        _descriptorPath = descriptorPath;
        ReusedHandshake = reused;
    }

    internal bool IsOwner { get; }
    internal PortalLaunchHandshake? ReusedHandshake { get; }

    internal static async Task<PortalLaunchCoordinator> AcquireAsync(
        string workspaceRoot,
        CancellationToken cancellationToken,
        string? runtimeBaseDirectory = null)
    {
        string key = WorkspaceCoordinationKey(workspaceRoot);
        string runtimeDirectory = PreparePrivateRuntimeDirectory(
            runtimeBaseDirectory ?? GetUserRuntimeBaseDirectory());

        string lockPath = Path.Combine(runtimeDirectory, $"{key}.lock");
        string descriptorPath = Path.Combine(runtimeDirectory, $"{key}.json");
        FileStream? ownerLock = TryAcquireOwnerLock(lockPath);
        if (ownerLock is not null)
        {
            TryDelete(descriptorPath);
            return new PortalLaunchCoordinator(ownerLock, descriptorPath);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PortalLaunchHandshake? descriptor = await TryReadLiveDescriptorAsync(
                descriptorPath,
                cancellationToken).ConfigureAwait(false);
            if (descriptor is not null)
            {
                return new PortalLaunchCoordinator(
                    descriptorPath,
                    descriptor with { Status = "reused" });
            }

            ownerLock = TryAcquireOwnerLock(lockPath);
            if (ownerLock is not null)
            {
                TryDelete(descriptorPath);
                return new PortalLaunchCoordinator(ownerLock, descriptorPath);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(75), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal async Task<PortalLaunchHandshake> PublishStartedAsync(
        string url,
        int workspaceCount,
        string launchSessionId,
        CancellationToken cancellationToken)
    {
        if (!IsOwner)
            throw new InvalidOperationException("Only the portal owner can publish a session.");

        var handshake = new PortalLaunchHandshake(
            ProtocolVersion,
            "started",
            url,
            Environment.ProcessId,
            workspaceCount,
            launchSessionId,
            ReadOnly: true);
        ValidateHandshake(handshake);

        string temporaryPath = $"{_descriptorPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(handshake, SerializerOptions),
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);
            SetPrivateFileMode(temporaryPath);
            File.Move(temporaryPath, _descriptorPath, overwrite: true);
            _descriptorPublished = true;
            return handshake;
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    internal static string Serialize(PortalLaunchHandshake handshake) =>
        JsonSerializer.Serialize(handshake, SerializerOptions);

    internal static string WorkspaceCoordinationKey(string workspaceRoot)
    {
        string canonicalRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(workspaceRoot));
        string identity;
        if (PortalPathGuard.TryGetDirectoryIdentity(
                canonicalRoot,
                out PortalFileIdentity physicalIdentity))
        {
            identity = $"physical:{physicalIdentity.Authority:X16}:{physicalIdentity.File:X16}";
        }
        else
        {
            identity = OperatingSystem.IsWindows()
                ? $"path:{canonicalRoot.ToUpperInvariant()}"
                : $"path:{canonicalRoot}";
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
    }

    public ValueTask DisposeAsync()
    {
        if (_descriptorPublished)
            TryDelete(_descriptorPath);
        _ownerLock?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static FileStream? TryAcquireOwnerLock(string lockPath)
    {
        if (File.Exists(lockPath)
            && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The portal runtime lock is a reparse point.");
        }

        FileStream stream;
        try
        {
            stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException)
        {
            return null;
        }

        try
        {
            SetPrivateFileMode(lockPath);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static async Task<PortalLaunchHandshake?> TryReadLiveDescriptorAsync(
        string descriptorPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(descriptorPath);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxDescriptorBytes)
                return null;
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                return null;

            string json = await File.ReadAllTextAsync(descriptorPath, cancellationToken)
                .ConfigureAwait(false);
            PortalLaunchHandshake? handshake =
                JsonSerializer.Deserialize<PortalLaunchHandshake>(json, SerializerOptions);
            if (handshake is null)
                return null;
            ValidateHandshake(handshake);

            Uri url = new(handshake.Url, UriKind.Absolute);
            string healthUrl = new Uri(url.GetLeftPart(UriPartial.Authority) + "/healthz").AbsoluteUri;
            using var healthTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            healthTimeout.CancelAfter(TimeSpan.FromSeconds(1));
            using HttpResponseMessage response = await HealthClient.GetAsync(
                healthUrl,
                HttpCompletionOption.ResponseHeadersRead,
                healthTimeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using Stream body = await response.Content.ReadAsStreamAsync(
                healthTimeout.Token).ConfigureAwait(false);
            PortalHealthStatus? health = await JsonSerializer.DeserializeAsync<PortalHealthStatus>(
                body,
                SerializerOptions,
                healthTimeout.Token).ConfigureAwait(false);
            return HealthMatchesDescriptor(health, handshake) ? handshake : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static void ValidateHandshake(PortalLaunchHandshake handshake)
    {
        if (handshake.ProtocolVersion != ProtocolVersion
            || handshake.Status is not ("started" or "reused")
            || handshake.Pid <= 0
            || handshake.WorkspaceCount <= 0
            || !IsLaunchSessionId(handshake.LaunchSessionId)
            || !handshake.ReadOnly
            || !Uri.TryCreate(handshake.Url, UriKind.Absolute, out Uri? url)
            || !string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !url.IsLoopback
            || !url.Fragment.StartsWith("#token=", StringComparison.Ordinal)
            || url.Fragment.Length <= "#token=".Length)
        {
            throw new InvalidOperationException("The portal session descriptor is invalid.");
        }
    }

    private static bool HealthMatchesDescriptor(
        PortalHealthStatus? health,
        PortalLaunchHandshake handshake) =>
        health is not null
        && string.Equals(health.Status, "ok", StringComparison.Ordinal)
        && health.ProtocolVersion == ProtocolVersion
        && health.ApiVersion == 1
        && health.Pid == handshake.Pid
        && health.ReadOnly
        && string.Equals(
            health.LaunchSessionId,
            handshake.LaunchSessionId,
            StringComparison.Ordinal);

    internal static bool IsLaunchSessionId(string? value)
    {
        if (value is null || value.Length != 43)
            return false;

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character)
                && character is not ('-' or '_'))
            {
                return false;
            }
        }
        return true;
    }

    private static string GetUserRuntimeBaseDirectory()
    {
        string userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            throw new InvalidOperationException(
                "The current user's profile directory is unavailable.");
        }

        return Path.GetFullPath(userProfile);
    }

    private static string PreparePrivateRuntimeDirectory(string baseDirectory)
    {
        string trustedBase = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(baseDirectory));
        ValidateSecureDirectoryChain(trustedBase);

        string applicationDirectory = CreatePrivateOwnedDirectory(
            trustedBase,
            ".phoenixcodenav");
        string runtimeDirectory = CreatePrivateOwnedDirectory(
            applicationDirectory,
            "runtime");
        return CreatePrivateOwnedDirectory(runtimeDirectory, "portal");
    }

    private static void ValidateSecureDirectoryChain(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(
                $"The portal runtime base directory does not exist: {path}");

        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            throw new IOException("The portal runtime base directory has no filesystem root.");

        string current = root;
        ValidateSecureDirectory(current, allowStickySharedDirectory: true);
        string relative = Path.GetRelativePath(root, path);
        if (relative == ".")
            return;

        foreach (string segment in relative.Split(Path.DirectorySeparatorChar))
        {
            if (string.IsNullOrEmpty(segment) || segment == ".")
                continue;
            current = Path.Combine(current, segment);
            ValidateSecureDirectory(current, allowStickySharedDirectory: true);
        }
    }

    private static string CreatePrivateOwnedDirectory(string parent, string name)
    {
        string path = Path.Combine(parent, name);
        if (File.Exists(path) && !Directory.Exists(path))
            throw new IOException($"The portal runtime path is not a directory: {path}");
        if (Directory.Exists(path)
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The portal runtime directory is a reparse point: {path}");
        }

        Directory.CreateDirectory(path);
        ValidateSecureDirectory(path, allowStickySharedDirectory: false);
        if (OperatingSystem.IsWindows())
            return path;

        UnixFileMode mode = UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute;
        File.SetUnixFileMode(path, mode);
        if (File.GetUnixFileMode(path) != mode)
        {
            throw new UnauthorizedAccessException(
                $"The portal runtime directory is not owner-private: {path}");
        }
        return path;
    }

    private static void ValidateSecureDirectory(
        string path,
        bool allowStickySharedDirectory)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0)
            throw new IOException($"The portal runtime ancestor is not a directory: {path}");
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"The portal runtime ancestor is a reparse point: {path}");
        if (OperatingSystem.IsWindows())
            return;

        UnixFileMode mode = File.GetUnixFileMode(path);
        UnixFileMode writableByOthers = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        if ((mode & writableByOthers) == 0)
            return;
        if (allowStickySharedDirectory && (mode & UnixFileMode.StickyBit) != 0)
            return;

        throw new UnauthorizedAccessException(
            $"The portal runtime ancestor is writable by other users: {path}");
    }

    private static void SetPrivateFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static HttpClient CreateHealthClient()
    {
        var handler = new HttpClientHandler { UseProxy = false };
        return new HttpClient(handler, disposeHandler: true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed record PortalLaunchHandshake(
    int ProtocolVersion,
    string Status,
    string Url,
    int Pid,
    int WorkspaceCount,
    string LaunchSessionId,
    bool ReadOnly);

internal sealed record PortalHealthStatus(
    string Status,
    string PortalVersion,
    int ApiVersion,
    int ProtocolVersion,
    int Pid,
    string LaunchSessionId,
    bool ReadOnly);
