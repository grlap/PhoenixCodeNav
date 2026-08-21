using System.Text;

namespace CodeNav.Core.Indexing;

internal enum IndexDestinationClaimAcquireResult
{
    Acquired,
    Contended,
    Failed,
}

internal enum IndexDestinationClaimState
{
    Missing,
    Ready,
    Rebuilding,
    Foreign,
    Invalid,
}

/// <summary>
/// Binds one database destination to the physical workspace that owns its single writer mutex.
/// The claim is not a reader registry or a second named lock: followers only read its fixed-size
/// state before and after opening SQLite. The writer changes Ready -> Rebuilding before destructive
/// replacement, which prevents new follower opens from barging while already-open bounded handles
/// drain. The writer keeps the file open without delete sharing; process death releases that handle,
/// and a successor holding the same workspace mutex may replace its same-identity stale claim.
/// </summary>
internal sealed class IndexDestinationClaim : IDisposable
{
    internal const string Suffix = ".phoenix-owner";
    private const int MaxClaimBytes = 256;

    private readonly string _path;
    private readonly string _workspaceIdentity;
    private FileStream? _stream;
    private int _disposed;

    private IndexDestinationClaim(string path, string workspaceIdentity, FileStream stream)
    {
        _path = path;
        _workspaceIdentity = workspaceIdentity;
        _stream = stream;
    }

    internal static IndexDestinationClaimAcquireResult TryAcquire(
        string workspaceRoot, string databasePath, out IndexDestinationClaim? claim)
    {
        claim = null;
        string workspaceIdentity;
        string path;
        try
        {
            workspaceIdentity = IndexOwnershipLease.GetWorkspaceIdentity(workspaceRoot);
            path = databasePath + Suffix;
        }
        catch
        {
            return IndexDestinationClaimAcquireResult.Failed;
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.Read, bufferSize: 256,
                    FileOptions.WriteThrough);
                try
                {
                    byte[] payload = Encoding.UTF8.GetBytes("B\n" + workspaceIdentity + "\n");
                    if (payload.Length > MaxClaimBytes)
                        throw new IOException("index destination claim identity is too large");
                    stream.Write(payload);
                    stream.Flush(flushToDisk: true);
                    claim = new IndexDestinationClaim(path, workspaceIdentity, stream);
                    return IndexDestinationClaimAcquireResult.Acquired;
                }
                catch
                {
                    stream.Dispose();
                    try { File.Delete(path); } catch { }
                    throw;
                }
            }
            catch (IOException)
            {
                IndexDestinationClaimState state =
                    ReadStateForIdentity(path, workspaceIdentity);
                if (state == IndexDestinationClaimState.Foreign)
                    return IndexDestinationClaimAcquireResult.Contended;
                if (state is not (IndexDestinationClaimState.Ready or
                    IndexDestinationClaimState.Rebuilding))
                    return IndexDestinationClaimAcquireResult.Failed;

                // Holding the workspace mutex proves that a same-workspace claimant cannot still
                // be alive. Its open handle also denies deletion on Windows, so a mistaken live
                // cleanup fails closed rather than stealing destination ownership.
                try { File.Delete(path); }
                catch { return IndexDestinationClaimAcquireResult.Failed; }
            }
            catch
            {
                return IndexDestinationClaimAcquireResult.Failed;
            }
        }

        return IndexDestinationClaimAcquireResult.Failed;
    }

    internal static IndexDestinationClaimState ReadState(
        string workspaceRoot, string databasePath)
    {
        try
        {
            return ReadStateForIdentity(databasePath + Suffix,
                IndexOwnershipLease.GetWorkspaceIdentity(workspaceRoot));
        }
        catch
        {
            return IndexDestinationClaimState.Invalid;
        }
    }

    internal void SetReady() => SetState('R');

    internal void SetRebuilding() => SetState('B');

    internal bool IsActiveFor(string workspaceIdentity, string databasePath)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string expectedPath = databasePath + Suffix;
        bool pathMatches = string.Equals(_path, expectedPath, comparison) ||
                           (!OperatingSystem.IsWindows() &&
                            string.Equals(Path.GetFileName(_path),
                                Path.GetFileName(expectedPath),
                                StringComparison.Ordinal));
        return Volatile.Read(ref _disposed) == 0 &&
               _stream is not null &&
               string.Equals(_workspaceIdentity, workspaceIdentity,
                   StringComparison.Ordinal) &&
               pathMatches;
    }

    private void SetState(char state)
    {
        FileStream stream = _stream ??
            throw new ObjectDisposedException(nameof(IndexDestinationClaim));
        stream.Position = 0;
        stream.WriteByte((byte)state);
        stream.Flush(flushToDisk: true);
    }

    private static IndexDestinationClaimState ReadStateForIdentity(
        string path, string expectedIdentity)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return IndexDestinationClaimState.Invalid;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 256,
                FileOptions.SequentialScan);
            if (stream.Length is < 4 or > MaxClaimBytes)
                return IndexDestinationClaimState.Invalid;
            byte[] bytes = new byte[(int)stream.Length];
            int read = 0;
            while (read < bytes.Length)
            {
                int count = stream.Read(bytes, read, bytes.Length - read);
                if (count == 0) return IndexDestinationClaimState.Invalid;
                read += count;
            }

            string value = Encoding.UTF8.GetString(bytes);
            if (value.Length < 4 || value[1] != '\n' || value[^1] != '\n')
                return IndexDestinationClaimState.Invalid;
            string identity = value[2..^1];
            if (!string.Equals(identity, expectedIdentity, StringComparison.Ordinal))
                return IndexDestinationClaimState.Foreign;
            return value[0] switch
            {
                'R' => IndexDestinationClaimState.Ready,
                'B' => IndexDestinationClaimState.Rebuilding,
                _ => IndexDestinationClaimState.Invalid,
            };
        }
        catch (FileNotFoundException)
        {
            return IndexDestinationClaimState.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return IndexDestinationClaimState.Missing;
        }
        catch
        {
            return IndexDestinationClaimState.Invalid;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        FileStream? stream = Interlocked.Exchange(ref _stream, null);
        try { stream?.Dispose(); }
        finally
        {
            IndexDestinationClaimState state =
                ReadStateForIdentity(_path, _workspaceIdentity);
            if (state is IndexDestinationClaimState.Ready or
                IndexDestinationClaimState.Rebuilding)
            {
                try { File.Delete(_path); } catch { }
            }
        }
    }
}
