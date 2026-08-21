using System.Runtime.CompilerServices;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp.Daemon;

/// <summary>
/// Round-robin admission across MCP sessions. One client may consume idle capacity, but once peers
/// are queued each released slot rotates to another client; disconnect cancels only that client's
/// queued requests. Core semantic/index limits remain the hard resource ceilings.
/// </summary>
internal sealed class DaemonRequestAdmission
{
    private readonly object _gate = new();
    private readonly ConditionalWeakTable<McpServer, ClientState> _clients = new();
    private readonly Dictionary<string, Queue<Waiter>> _queues = new(StringComparer.Ordinal);
    private readonly List<string> _rotation = [];
    private readonly int _maxConcurrent;
    private readonly Action? _beforeWaiterEnqueueUnderGate;
    private readonly Action? _beforeBeginDrainGate;
    private int _rotationIndex;
    private int _active;
    private bool _draining;

    internal DaemonRequestAdmission(
        int? maxConcurrent = null,
        Action? beforeWaiterEnqueueUnderGate = null,
        Action? beforeBeginDrainGate = null)
    {
        _maxConcurrent = maxConcurrent ?? Math.Clamp(Environment.ProcessorCount, 4, 16);
        if (_maxConcurrent <= 0) throw new ArgumentOutOfRangeException(nameof(maxConcurrent));
        _beforeWaiterEnqueueUnderGate = beforeWaiterEnqueueUnderGate;
        _beforeBeginDrainGate = beforeBeginDrainGate;
    }

    internal int ActiveCount
    {
        get { lock (_gate) return _active; }
    }

    internal void Register(McpServer server, string clientId)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException(null, nameof(clientId));
        lock (_gate)
        {
            _clients.Remove(server);
            _clients.Add(server, new ClientState(clientId));
        }
    }

    internal void Unregister(McpServer server)
    {
        ClientState? state;
        lock (_gate)
        {
            if (!_clients.TryGetValue(server, out state)) return;
            _clients.Remove(server);
        }
        state.Lifetime.Cancel();
        // EnterAsync may already have captured this state but not yet linked its token. Do not
        // dispose the source while that race is possible; it owns no wait handle and becomes
        // collectible with the disconnected ClientState.
    }

    internal async ValueTask<IDisposable> EnterAsync(
        McpServer server,
        CancellationToken cancellationToken)
    {
        Waiter waiter;
        CancellationTokenSource linked;
        CancellationTokenRegistration registration;
        lock (_gate)
        {
            if (_draining)
                throw new OperationCanceledException(new CancellationToken(canceled: true));
            if (!_clients.TryGetValue(server, out ClientState? client))
                return NoopLease.Instance;

            waiter = new Waiter(client.ClientId);
            linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, client.Lifetime.Token);
            registration = linked.Token.Register(() =>
            {
                lock (_gate)
                {
                    waiter.Cancelled = true;
                    waiter.Completion.TrySetCanceled(linked.Token);
                    DispatchLocked();
                }
            });

            // The drain check, client capture, and waiter visibility are one transaction. A drain
            // either observes this waiter (or its active lease), or wins the gate and rejects it.
            _beforeWaiterEnqueueUnderGate?.Invoke();
            if (!_queues.TryGetValue(client.ClientId, out Queue<Waiter>? queue))
            {
                queue = new Queue<Waiter>();
                _queues.Add(client.ClientId, queue);
                _rotation.Add(client.ClientId);
            }
            queue.Enqueue(waiter);
            DispatchLocked();
        }

        using (linked)
        using (registration)
        {
            await waiter.Completion.Task.ConfigureAwait(false);
            return new AdmissionLease(this);
        }
    }

    internal void BeginDrain()
    {
        _beforeBeginDrainGate?.Invoke();
        lock (_gate)
        {
            if (_draining) return;
            _draining = true;
            DispatchLocked();
        }
    }

    private void DispatchLocked()
    {
        if (_draining)
        {
            var cancelled = new CancellationToken(canceled: true);
            foreach (Queue<Waiter> queue in _queues.Values)
            {
                foreach (Waiter waiter in queue)
                {
                    waiter.Cancelled = true;
                    waiter.Completion.TrySetCanceled(cancelled);
                }
            }
            _queues.Clear();
            _rotation.Clear();
            _rotationIndex = 0;
            return;
        }

        while (_active < _maxConcurrent && _rotation.Count > 0)
        {
            bool granted = false;
            int candidates = _rotation.Count;
            for (int examined = 0; examined < candidates && _rotation.Count > 0; examined++)
            {
                if (_rotationIndex >= _rotation.Count) _rotationIndex = 0;
                string clientId = _rotation[_rotationIndex];
                Queue<Waiter> queue = _queues[clientId];
                while (queue.Count > 0 && queue.Peek().Cancelled)
                    queue.Dequeue();
                if (queue.Count == 0)
                {
                    _queues.Remove(clientId);
                    _rotation.RemoveAt(_rotationIndex);
                    if (_rotationIndex >= _rotation.Count) _rotationIndex = 0;
                    candidates--;
                    examined--;
                    continue;
                }

                Waiter waiter = queue.Dequeue();
                // Deliberately retain Count as a one-past-the-end cursor. If a new client is
                // appended before the next release, it receives the next slot instead of the
                // sole prior client wrapping to index zero and monopolizing the scheduler.
                _rotationIndex++;
                _active++;
                waiter.Completion.TrySetResult();
                granted = true;
                break;
            }
            if (!granted) break;
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_active <= 0) throw new InvalidOperationException("Daemon admission underflow.");
            _active--;
            DispatchLocked();
        }
    }

    private sealed class ClientState(string clientId)
    {
        internal string ClientId { get; } = clientId;
        internal CancellationTokenSource Lifetime { get; } = new();
    }

    private sealed class Waiter(string clientId)
    {
        internal string ClientId { get; } = clientId;
        internal TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Cancelled { get; set; }
    }

    private sealed class AdmissionLease(DaemonRequestAdmission owner) : IDisposable
    {
        private DaemonRequestAdmission? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }

    private sealed class NoopLease : IDisposable
    {
        internal static readonly NoopLease Instance = new();
        public void Dispose() { }
    }
}

internal sealed class DaemonFairMcpServerTool : DelegatingMcpServerTool
{
    private readonly DaemonRequestAdmission _admission;

    internal DaemonFairMcpServerTool(McpServerTool inner, DaemonRequestAdmission admission)
        : base(inner) => _admission = admission;

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        using IDisposable lease = await _admission.EnterAsync(request.Server, cancellationToken)
            .ConfigureAwait(false);
        return await base.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
