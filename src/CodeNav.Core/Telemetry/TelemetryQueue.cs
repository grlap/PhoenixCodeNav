namespace CodeNav.Core.Telemetry;

/// <summary>One enqueued frame. Data is a FACTORY resolved at send time, for two reasons:
/// (1) identity fields (workspaceId/indexId) do not exist until the portal's welcome supplies
/// the session salt — and change on portal restart — so frames retained across (re)connects
/// must bind ids late; (2) serialization+bounds checking runs on the dedicated sender, keeping
/// the request-path cost of Emit to a closure allocation (the &lt;1 ms p95 budget).
/// Sequence is assigned at enqueue so evictions leave HONEST wire gaps, and the timestamp is
/// event time (the queue may hold frames across a portal outage).</summary>
internal sealed record PendingFrame(
    string Type, Func<TelemetryIds, object> DataFactory, long Sequence, string TimestampUtc,
    bool Lifecycle);

/// <summary>The per-portal-session opaque identities every frame factory may reference.</summary>
public sealed record TelemetryIds(string WorkspaceId, string IndexId);

/// <summary>
/// Owns: the bounded in-process publication queue of telemetry API v1 (x5ls.1). Contract
/// behavior: enqueue is non-blocking and never throws; when full, the OLDEST NON-LIFECYCLE
/// frame is evicted first (snapshots/build-completions outlive floods of progress ticks); if
/// every queued frame is lifecycle, the oldest lifecycle frame goes (never the incoming one —
/// newest state wins). Every eviction is counted for the next telemetry.dropped disclosure.
/// Does not own: serialization/bounds (TelemetryBounds, before enqueue) or IPC (the producer
/// drains this from its dedicated background sender).
/// </summary>
internal sealed class TelemetryQueue
{
    private readonly LinkedList<PendingFrame> _frames = new();
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly SemaphoreSlim _available = new(0);
    private long _sequence;
    private long _dropped;
    private long _droppedSinceSequence = -1;

    public TelemetryQueue(int capacity = 2048) => _capacity = capacity;

    public int Count { get { lock (_gate) return _frames.Count; } }

    /// <summary>Builds and enqueues one frame; evicts per the lifecycle rule when full. Never
    /// blocks. The SEQUENCE is assigned INSIDE the gate (review F3): assigning it before the
    /// lock let two concurrent emitters enqueue in inverted order — thread A takes seq 41, is
    /// preempted, thread B takes 42 and enqueues first, and the FIFO wire carries 42 then 41,
    /// violating the contract's strictly-increasing envelope rule and tripping portal
    /// replay/gap detection.</summary>
    public void Enqueue(string type, Func<TelemetryIds, object> dataFactory,
        string timestampUtc, bool lifecycle)
    {
        lock (_gate)
        {
            var frame = new PendingFrame(type, dataFactory, ++_sequence, timestampUtc, lifecycle);
            if (_frames.Count >= _capacity)
            {
                var victim = _frames.First;
                for (var n = _frames.First; n is not null; n = n.Next)
                {
                    if (!n.Value.Lifecycle) { victim = n; break; }
                }
                if (victim is not null)
                {
                    if (_droppedSinceSequence < 0) _droppedSinceSequence = victim.Value.Sequence;
                    _frames.Remove(victim);
                    _dropped++;
                }
            }
            _frames.AddLast(frame);
        }
        // Release AFTER the lock: a waiting sender must observe the frame it was woken for.
        try { _available.Release(); } catch (SemaphoreFullException) { /* saturated wake signal */ }
    }

    /// <summary>Waits for at least one frame, then drains up to <paramref name="max"/> in FIFO
    /// order. Returns an empty list only on cancellation. STALE PERMITS are absorbed here:
    /// an eviction removes a frame without consuming its semaphore permit, and a batch drain
    /// consumes many frames against one permit — both make permit count drift from frame
    /// count, so a wake against an empty queue simply waits again (callers treat an empty
    /// batch as cancellation, and a spurious empty would tear down a healthy session).</summary>
    public async Task<List<PendingFrame>> DequeueBatchAsync(int max, CancellationToken ct)
    {
        var batch = new List<PendingFrame>();
        while (true)
        {
            try { await _available.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return batch; }
            lock (_gate)
            {
                while (batch.Count < max && _frames.First is { } first)
                {
                    batch.Add(first.Value);
                    _frames.RemoveFirst();
                }
            }
            if (batch.Count > 0) return batch;
        }
    }

    /// <summary>Takes the pending drop disclosure (count + first affected sequence), resetting
    /// it. Returns null when nothing was dropped since the last call.</summary>
    public (long Records, long SinceSequence)? TakeDropReport()
    {
        lock (_gate)
        {
            if (_dropped == 0) return null;
            var report = (_dropped, _droppedSinceSequence);
            _totalDropped += _dropped;
            _dropped = 0;
            _droppedSinceSequence = -1;
            return report;
        }
    }

    /// <summary>Total drops since process start (snapshot gauge; does not reset).</summary>
    public long TotalDropped { get { lock (_gate) return _totalDropped + _dropped; } }
    private long _totalDropped;
}
