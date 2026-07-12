using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodeNav.Core.Indexing;

internal enum IndexReviewCoordinationAcquireResult
{
    Acquired,
    Contended,
    Failed,
}

/// <summary>
/// Scalable cross-process reader/rebuild coordination for one physical Windows index. A brief
/// named turnstile prevents reader barging once a rebuild is waiting. Readers retain only a shared
/// handle to one anchored regular sidecar, so their population is limited by the OS rather than a
/// Phoenix slot table. A rebuild retains the turnstile while opening that sidecar exclusively and
/// through database replacement. Kernel handles and an abandoned mutex recover on process death.
/// </summary>
internal sealed class IndexReviewCoordinationLease : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);

    private SafeFileHandle? _readerHandle;
    private readonly Mutex? _turnstile;
    private readonly IndexDirectoryAuthority? _authority;
    private readonly TimeSpan _timeout;
    private readonly Action? _waiting;
    private readonly CancellationToken _cancellationToken;
    private readonly ManualResetEventSlim? _ready;
    private readonly ManualResetEventSlim? _release;
    private readonly ManualResetEventSlim? _exited;
    private readonly Thread? _ownerThread;
    private IndexReviewCoordinationAcquireResult _result =
        IndexReviewCoordinationAcquireResult.Failed;
    private int _disposed;

    private IndexReviewCoordinationLease(SafeFileHandle? readerHandle)
    {
        _readerHandle = readerHandle;
    }

    private IndexReviewCoordinationLease(IndexDirectoryAuthority authority, Mutex turnstile,
        TimeSpan timeout, Action? waiting, CancellationToken cancellationToken)
    {
        _authority = authority;
        _turnstile = turnstile;
        _timeout = timeout;
        _waiting = waiting;
        _cancellationToken = cancellationToken;
        _ready = new ManualResetEventSlim(false);
        _release = new ManualResetEventSlim(false);
        _exited = new ManualResetEventSlim(false);
        _ownerThread = new Thread(OwnExclusiveCoordination)
        {
            IsBackground = true,
            Name = "PhoenixCodeNav index rebuild gate",
        };
    }

    internal static IndexReviewCoordinationAcquireResult TryAcquireReader(
        IndexDirectoryAuthority authority, TimeSpan timeout,
        out IndexReviewCoordinationLease? lease,
        CancellationToken cancellationToken = default)
    {
        lease = null;
        if (!OperatingSystem.IsWindows())
        {
            lease = new IndexReviewCoordinationLease(readerHandle: null);
            return IndexReviewCoordinationAcquireResult.Acquired;
        }
        if (!TryCreateTurnstile(authority, out Mutex? turnstile))
            return IndexReviewCoordinationAcquireResult.Failed;

        bool held = false;
        try
        {
            held = Wait(turnstile!, timeout, cancellationToken);
            if (!held) return IndexReviewCoordinationAcquireResult.Contended;
            cancellationToken.ThrowIfCancellationRequested();
            IndexReviewCoordinationAcquireResult result =
                authority.TryOpenReviewCoordinationHandle(exclusive: false,
                    out SafeFileHandle? handle);
            if (result == IndexReviewCoordinationAcquireResult.Acquired)
                lease = new IndexReviewCoordinationLease(handle);
            return result;
        }
        finally
        {
            if (held)
            {
                try { turnstile!.ReleaseMutex(); } catch { }
            }
            turnstile!.Dispose();
        }
    }

    internal static IndexReviewCoordinationAcquireResult TryAcquireExclusive(
        IndexDirectoryAuthority authority, TimeSpan timeout, Action? waiting,
        out IndexReviewCoordinationLease? lease,
        CancellationToken cancellationToken = default)
    {
        lease = null;
        if (!OperatingSystem.IsWindows())
        {
            lease = new IndexReviewCoordinationLease(readerHandle: null);
            return IndexReviewCoordinationAcquireResult.Acquired;
        }
        if (!TryCreateTurnstile(authority, out Mutex? turnstile))
            return IndexReviewCoordinationAcquireResult.Failed;

        var candidate = new IndexReviewCoordinationLease(authority, turnstile!, timeout,
            waiting, cancellationToken);
        try
        {
            candidate._ownerThread!.Start();
            TimeSpan readyTimeout = timeout + ExitTimeout;
            if (!candidate._ready!.Wait(readyTimeout, cancellationToken))
            {
                candidate.Dispose();
                return IndexReviewCoordinationAcquireResult.Failed;
            }
        }
        catch
        {
            candidate.Dispose();
            throw;
        }

        if (candidate._result != IndexReviewCoordinationAcquireResult.Acquired)
        {
            IndexReviewCoordinationAcquireResult result = candidate._result;
            candidate.Dispose();
            return result;
        }

        lease = candidate;
        return IndexReviewCoordinationAcquireResult.Acquired;
    }

    private void OwnExclusiveCoordination()
    {
        bool held = false;
        bool published = false;
        bool waitingPublished = false;
        long started = Stopwatch.GetTimestamp();
        try
        {
            held = Wait(_turnstile!, Remaining(started), _cancellationToken);
            if (!held)
            {
                Publish(IndexReviewCoordinationAcquireResult.Contended);
                return;
            }

            while (true)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                IndexReviewCoordinationAcquireResult result =
                    _authority!.TryOpenReviewCoordinationHandle(exclusive: true,
                        out SafeFileHandle? handle);
                if (result == IndexReviewCoordinationAcquireResult.Acquired)
                {
                    _readerHandle = handle;
                    break;
                }
                if (result == IndexReviewCoordinationAcquireResult.Failed)
                {
                    Publish(result);
                    return;
                }
                if (!waitingPublished)
                {
                    waitingPublished = true;
                    try { _waiting?.Invoke(); } catch { }
                }
                TimeSpan remaining = Remaining(started);
                if (remaining <= TimeSpan.Zero)
                {
                    Publish(IndexReviewCoordinationAcquireResult.Contended);
                    return;
                }
                TimeSpan delay = remaining < RetryInterval ? remaining : RetryInterval;
                if (_cancellationToken.WaitHandle.WaitOne(delay))
                    _cancellationToken.ThrowIfCancellationRequested();
            }

            Publish(IndexReviewCoordinationAcquireResult.Acquired);
            _release!.Wait();
        }
        catch (OperationCanceledException)
        {
            if (!published) Publish(IndexReviewCoordinationAcquireResult.Contended);
        }
        catch
        {
            if (!published) Publish(IndexReviewCoordinationAcquireResult.Failed);
        }
        finally
        {
            Interlocked.Exchange(ref _readerHandle, null)?.Dispose();
            if (held)
            {
                try { _turnstile!.ReleaseMutex(); } catch { }
            }
            try { _turnstile!.Dispose(); } catch { }
            try { _exited!.Set(); } catch { }
        }

        void Publish(IndexReviewCoordinationAcquireResult result)
        {
            if (published) return;
            published = true;
            _result = result;
            _ready!.Set();
        }
    }

    private TimeSpan Remaining(long started)
    {
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        return elapsed >= _timeout ? TimeSpan.Zero : _timeout - elapsed;
    }

    private static bool TryCreateTurnstile(IndexDirectoryAuthority authority,
        out Mutex? turnstile)
    {
        turnstile = null;
        try
        {
            if (!authority.TryGetReviewCoordinationKey(out string? key)) return false;
            string hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(key!)));
            turnstile = new Mutex(initiallyOwned: false,
                $"Global\\PhoenixCodeNav.Index.Readers.{hash}.Turnstile");
            return true;
        }
        catch
        {
            turnstile?.Dispose();
            turnstile = null;
            return false;
        }
    }

    private static bool Wait(Mutex mutex, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!cancellationToken.CanBeCanceled) return mutex.WaitOne(timeout);
            int signaled = WaitHandle.WaitAny(
                [mutex, cancellationToken.WaitHandle], timeout);
            if (signaled == 1) cancellationToken.ThrowIfCancellationRequested();
            return signaled == 0;
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ownerThread is null)
        {
            Interlocked.Exchange(ref _readerHandle, null)?.Dispose();
            return;
        }

        try { _release!.Set(); } catch { }
        try { _exited!.Wait(ExitTimeout); } catch { }
        try { _ready!.Dispose(); } catch { }
        try { _release!.Dispose(); } catch { }
        try { _exited!.Dispose(); } catch { }
    }
}
