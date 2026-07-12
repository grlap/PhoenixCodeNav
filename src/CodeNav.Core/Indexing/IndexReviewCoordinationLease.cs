using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CodeNav.Core.Indexing;

internal enum IndexReviewCoordinationAcquireResult
{
    Acquired,
    Contended,
    Failed,
}

/// <summary>
/// Cross-process reader/rebuild coordination for one physical index database on Windows.
/// Review snapshots briefly pass a named turnstile and retain one of a fixed set of reader
/// slots. A destructive rebuild retains the turnstile while draining every slot, preventing new
/// snapshots from entering until replacement finishes. Dedicated owner threads preserve named
/// Mutex thread affinity and abandoned mutexes make process crashes self-healing.
/// </summary>
internal sealed class IndexReviewCoordinationLease : IDisposable
{
    private const int ReaderSlotCount = 32;
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);

    private readonly Mutex _turnstile;
    private readonly Mutex[] _readerSlots;
    private readonly bool _exclusive;
    private readonly TimeSpan _timeout;
    private readonly Action? _waiting;
    private readonly ManualResetEventSlim _release = new(false);
    private readonly ManualResetEventSlim _exited = new(false);
    private readonly Thread _ownerThread;
    private int _disposed;

    private IndexReviewCoordinationLease(Mutex turnstile, Mutex[] readerSlots,
        bool exclusive, TimeSpan timeout, Action? waiting,
        ManualResetEventSlim ready,
        Action<IndexReviewCoordinationAcquireResult> publishResult)
    {
        _turnstile = turnstile;
        _readerSlots = readerSlots;
        _exclusive = exclusive;
        _timeout = timeout;
        _waiting = waiting;
        _ownerThread = new Thread(() => OwnCoordination(ready, publishResult))
        {
            IsBackground = true,
            Name = exclusive
                ? "PhoenixCodeNav index rebuild gate"
                : "PhoenixCodeNav index review reader",
        };
    }

    internal static IndexReviewCoordinationAcquireResult TryAcquireReader(
        IndexLeaseIdentity identity, TimeSpan timeout,
        out IndexReviewCoordinationLease? lease) =>
        TryAcquire(identity, exclusive: false, timeout, waiting: null, out lease);

    internal static IndexReviewCoordinationAcquireResult TryAcquireExclusive(
        IndexLeaseIdentity identity, TimeSpan timeout, Action? waiting,
        out IndexReviewCoordinationLease? lease) =>
        TryAcquire(identity, exclusive: true, timeout, waiting, out lease);

    private static IndexReviewCoordinationAcquireResult TryAcquire(
        IndexLeaseIdentity identity, bool exclusive, TimeSpan timeout, Action? waiting,
        out IndexReviewCoordinationLease? lease)
    {
        lease = null;
        if (!OperatingSystem.IsWindows() || identity.DatabaseIdentity is not { Length: > 0 })
            return IndexReviewCoordinationAcquireResult.Failed;

        Mutex? turnstile = null;
        var slots = new Mutex[ReaderSlotCount];
        try
        {
            string key = Hash(identity.DatabaseIdentity);
            turnstile = new Mutex(initiallyOwned: false,
                $"Global\\PhoenixCodeNav.Index.Review.{key}.Turnstile");
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i] = new Mutex(initiallyOwned: false,
                    $"Global\\PhoenixCodeNav.Index.Review.{key}.Reader.{i:D2}");
            }
        }
        catch
        {
            turnstile?.Dispose();
            foreach (Mutex? slot in slots) slot?.Dispose();
            return IndexReviewCoordinationAcquireResult.Failed;
        }

        using var ready = new ManualResetEventSlim(false);
        IndexReviewCoordinationAcquireResult result =
            IndexReviewCoordinationAcquireResult.Failed;
        var candidate = new IndexReviewCoordinationLease(turnstile, slots, exclusive,
            timeout, waiting, ready, value => result = value);
        bool ownerThreadStarted = false;
        try
        {
            candidate._ownerThread.Start();
            ownerThreadStarted = true;
            TimeSpan coordinationWait = timeout + ExitTimeout;
            if (!ready.Wait(coordinationWait))
            {
                candidate._release.Set();
                candidate.CleanupFailedAcquisition();
                return IndexReviewCoordinationAcquireResult.Failed;
            }
        }
        catch
        {
            if (ownerThreadStarted)
            {
                candidate._release.Set();
                candidate.CleanupFailedAcquisition();
            }
            else
            {
                candidate.CleanupUnstartedAcquisition();
            }
            return IndexReviewCoordinationAcquireResult.Failed;
        }

        if (result != IndexReviewCoordinationAcquireResult.Acquired)
        {
            candidate.CleanupFailedAcquisition();
            return result;
        }

        lease = candidate;
        return IndexReviewCoordinationAcquireResult.Acquired;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _release.Set(); }
        catch { return; }
        try
        {
            if (_exited.Wait(ExitTimeout))
            {
                _release.Dispose();
                _exited.Dispose();
            }
        }
        catch
        {
            // The owner thread releases every kernel mutex in its finally block. Disposal remains
            // bounded even if the host is shutting down while kernel coordination is delayed.
        }
    }

    private void CleanupFailedAcquisition()
    {
        try
        {
            if (_exited.Wait(ExitTimeout))
            {
                Interlocked.Exchange(ref _disposed, 1);
                _release.Dispose();
                _exited.Dispose();
            }
        }
        catch { }
    }

    private void CleanupUnstartedAcquisition()
    {
        Interlocked.Exchange(ref _disposed, 1);
        try { _turnstile.Dispose(); } catch { }
        foreach (Mutex slot in _readerSlots)
        {
            try { slot.Dispose(); } catch { }
        }
        try { _release.Dispose(); } catch { }
        try { _exited.Dispose(); } catch { }
    }

    private void OwnCoordination(ManualResetEventSlim ready,
        Action<IndexReviewCoordinationAcquireResult> publishResult)
    {
        bool turnstileHeld = false;
        int readerSlot = -1;
        int exclusiveSlotsHeld = 0;
        bool published = false;
        bool waitingPublished = false;
        long started = Stopwatch.GetTimestamp();
        try
        {
            turnstileHeld = Wait(_turnstile, Remaining(started));
            if (!turnstileHeld)
            {
                Publish(IndexReviewCoordinationAcquireResult.Contended);
                return;
            }

            if (_exclusive)
            {
                for (; exclusiveSlotsHeld < _readerSlots.Length; exclusiveSlotsHeld++)
                {
                    if (TryWait(_readerSlots[exclusiveSlotsHeld])) continue;
                    PublishWaiting();
                    if (!Wait(_readerSlots[exclusiveSlotsHeld], Remaining(started)))
                    {
                        Publish(IndexReviewCoordinationAcquireResult.Contended);
                        return;
                    }
                }
            }
            else
            {
                int first = (int)((uint)Environment.CurrentManagedThreadId %
                                  (uint)_readerSlots.Length);
                for (int offset = 0; offset < _readerSlots.Length; offset++)
                {
                    int candidate = (first + offset) % _readerSlots.Length;
                    if (!TryWait(_readerSlots[candidate])) continue;
                    readerSlot = candidate;
                    break;
                }
                if (readerSlot < 0)
                {
                    Publish(IndexReviewCoordinationAcquireResult.Contended);
                    return;
                }

                _turnstile.ReleaseMutex();
                turnstileHeld = false;
            }

            Publish(IndexReviewCoordinationAcquireResult.Acquired);
            _release.Wait();
        }
        catch
        {
            if (!published) Publish(IndexReviewCoordinationAcquireResult.Failed);
        }
        finally
        {
            if (readerSlot >= 0)
            {
                try { _readerSlots[readerSlot].ReleaseMutex(); } catch { }
            }
            for (int i = exclusiveSlotsHeld - 1; i >= 0; i--)
            {
                try { _readerSlots[i].ReleaseMutex(); } catch { }
            }
            if (turnstileHeld)
            {
                try { _turnstile.ReleaseMutex(); } catch { }
            }
            try { _turnstile.Dispose(); } catch { }
            foreach (Mutex slot in _readerSlots)
            {
                try { slot.Dispose(); } catch { }
            }
            try { _exited.Set(); } catch { }
        }

        void Publish(IndexReviewCoordinationAcquireResult value)
        {
            if (published) return;
            published = true;
            try { publishResult(value); } finally { ready.Set(); }
        }

        void PublishWaiting()
        {
            if (waitingPublished) return;
            waitingPublished = true;
            try { _waiting?.Invoke(); } catch { }
        }
    }

    private TimeSpan Remaining(long started)
    {
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        return elapsed >= _timeout ? TimeSpan.Zero : _timeout - elapsed;
    }

    private static bool TryWait(Mutex mutex)
    {
        try { return mutex.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }

    private static bool Wait(Mutex mutex, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) return TryWait(mutex);
        try { return mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
