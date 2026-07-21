using System.Collections.Concurrent;
using System.Collections.Immutable;
using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CodeNav.Core.Semantic;

/// <summary>An immutable Roslyn snapshot plus the resource ownership that keeps every source and
/// metadata input reachable from that snapshot alive for the complete compiler operation.</summary>
public sealed class SemanticSolutionLease : IDisposable
{
    private Action? _release;

    internal SemanticSolutionLease(Solution solution, ClusterCoverage coverage,
        IEnumerable<ProjectId> requestedProjectIds, Action release)
    {
        Solution = solution;
        Coverage = coverage;
        RequestedProjectIds = requestedProjectIds.ToImmutableArray();
        _release = release;
    }

    public Solution Solution { get; }
    public ClusterCoverage Coverage { get; }
    internal ImmutableArray<ProjectId> RequestedProjectIds { get; }

    public void Deconstruct(out Solution solution, out ClusterCoverage coverage)
    {
        solution = Solution;
        coverage = Coverage;
    }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

public sealed partial class SemanticWorkspace
{
    private const long PreparedProjectOverheadBytes = 64 * 1024;
    private const long PerDocumentOverheadBytes = 4 * 1024;
    private const long LargeSourceFileBytes = 4L * 1024 * 1024;

    private static readonly ColdStartRuntime SharedColdStartRuntime = new(
        Math.Min(8, Math.Max(1, Environment.ProcessorCount)));

    private ColdStartRuntime _coldStartRuntime = SharedColdStartRuntime;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ConcurrentDictionary<PreparationKey, SharedPreparation> _inFlightPreparations = new();
    private readonly object _planningOwnershipSync = new();
    private readonly Dictionary<string, int> _activeRequestedProjects =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _inFlightPreparationSync = new();
    private readonly Dictionary<string, ProjectId> _plannedProjectIds =
        new(StringComparer.OrdinalIgnoreCase);
    private long _workspaceGeneration;

    internal Func<string, CancellationToken, Task>? TestOnlyBeforeProjectCaptureAsync { get; set; }
    internal Func<string, string, CancellationToken, Task>? TestOnlyBeforeSourceCaptureAsync
    { get; set; }
    internal Action<string>? TestOnlyAfterProjectPrepared { get; set; }
    internal Func<string, CancellationToken, Task>? TestOnlyBeforeIndexedFallbackAsync { get; set; }
    internal Action<string>? TestOnlyAfterIndexedFallbackLock { get; set; }
    internal Func<CancellationToken, Task>? TestOnlyBeforeCommitAsync { get; set; }
    internal Action<string>? TestOnlyBeforeColdStartSql { get; set; }
    internal bool TestOnlyRejectPreparedCommit { get; set; }
    internal long RetainedSemanticInputBytes => _coldStartRuntime.Accounting.RetainedBytes;
    internal long SemanticInputHighWaterBytes => _coldStartRuntime.Accounting.HighWaterBytes;
    internal int TestOnlyPreparationWaiters =>
        _inFlightPreparations.Values.Sum(preparation => preparation.WaiterCount);
    internal int TestOnlyPlannedProjectIds
    {
        get
        {
            lock (_planningOwnershipSync) return _plannedProjectIds.Count;
        }
    }

    internal SemanticWorkspace(string workspaceRoot, string dbPath, int preparationConcurrency,
        Action<string>? log = null, bool poolIndexConnections = true)
        : this(workspaceRoot, dbPath, log, poolIndexConnections)
    {
        _coldStartRuntime = new ColdStartRuntime(Math.Max(1, preparationConcurrency));
    }

    private sealed record LoadedSnapshot(
        ProjectId Id,
        (long Count, long Sum) Fingerprint,
        string ModelIdentity,
        IReadOnlySet<string> DirectReferences,
        IReadOnlySet<string> PhysicalReferenceNames);

    private sealed record PlannedProject(
        string Name,
        ProjectRow Row,
        ProjectId ProjectId,
        string SnapshotIdentity,
        (long Count, long Sum) Fingerprint,
        IReadOnlyList<(string Path, long Size)> Files,
        bool HasDirectoryBuildAuthority,
        long ProjectFileSize,
        long PackagesFileSize);

    private readonly record struct PreparationKey(
        string Name, string ProjectPath, ProjectId ProjectId, long FingerprintCount,
        long FingerprintSum, string SnapshotIdentity);

    private sealed record PreparedProjectIdentity(
        string Name,
        string ProjectPath,
        ProjectId ProjectId,
        (long Count, long Sum) Fingerprint,
        string ModelIdentity);

    private static PreparedProjectIdentity PreparedIdentity(PlannedProject plan) => new(
        plan.Name, plan.Row.Path, plan.ProjectId, plan.Fingerprint, plan.SnapshotIdentity);

    private sealed class PreparedSource
    {
        public required string RelativePath { get; init; }
        public required string FullPath { get; init; }
        public SourceText? Text { get; set; }
    }

    private sealed record PreparedMetadataCandidate(
        string? AssemblyName, string FullPath, PortableExecutableReference Reference);

    private sealed class PreparedProject
    {
        private readonly object _fallbackSync = new();
        private bool _fallbacksResolved;

        public required PreparedProjectIdentity Identity { get; init; }
        public required ParsedProject Parsed { get; init; }
        public required PreparedSource[] Sources { get; init; }
        public required IReadOnlyList<PreparedMetadataCandidate> MetadataCandidates { get; init; }
        public required ProjectResources? Resources { get; init; }
        public required bool UnprovenFriendAssemblyAuthority { get; init; }
        public required long ParseTicks { get; init; }
        public required long ReadTicks { get; init; }
        public required long MetadataTicks { get; init; }
        public required long DescriptorRetainedBytes { get; init; }
        public required long QueueTicks { get; init; }
        public string? FailureCause { get; private set; }
        public IReadOnlyList<DocumentInfo> Documents { get; private set; } = [];

        public static PreparedProject Failed(PlannedProject plan, string? cause = null,
            long queueTicks = 0) => new()
            {
                Identity = PreparedIdentity(plan),
                Parsed = new ParsedProject(plan.Row.Path, plan.Name, "unknown", null, "", false,
                    [], [], null, [], "failed:prepare"),
                Sources = [],
                MetadataCandidates = [],
                Resources = null,
                UnprovenFriendAssemblyAuthority = false,
                ParseTicks = 0,
                ReadTicks = 0,
                MetadataTicks = 0,
                DescriptorRetainedBytes = 0,
                QueueTicks = queueTicks,
                FailureCause = cause,
            };

        public bool EnsureIndexedFallbacks(IndexQueries queries, CancellationToken cancellationToken,
            Action<string>? afterLock = null)
        {
            bool lockTaken = false;
            try
            {
                while (!Monitor.TryEnter(_fallbackSync, TimeSpan.FromMilliseconds(25)))
                    cancellationToken.ThrowIfCancellationRequested();
                lockTaken = true;
                afterLock?.Invoke(Identity.Name);
                cancellationToken.ThrowIfCancellationRequested();
                if (_fallbacksResolved) return FailureCause is null;
                if (FailureCause is not null || Resources is null)
                {
                    _fallbacksResolved = true;
                    return false;
                }

                foreach (PreparedSource source in Sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (source.Text is not null) continue;
                    string? indexed = queries.ContentByPathBounded(
                        source.RelativePath, DeltaRefresher.MaxIndexedFileBytes,
                        cancellationToken);
                    if (indexed is not null) source.Text = SourceText.From(indexed);
                }

                long retained = SaturatingAdd(PreparedProjectOverheadBytes,
                    DescriptorRetainedBytes);
                var documents = new List<DocumentInfo>(Sources.Length + 1);
                foreach (PreparedSource source in Sources)
                {
                    if (source.Text is null) continue;
                    retained = SaturatingAdd(retained,
                        SaturatingAdd((long)source.Text.Length * sizeof(char), PerDocumentOverheadBytes));
                    documents.Add(DocumentInfo.Create(
                        StableDocumentId(Identity.ProjectId, source.RelativePath),
                        name: Path.GetFileName(source.RelativePath),
                        loader: TextLoader.From(TextAndVersion.Create(
                            source.Text, VersionStamp.Create(), source.FullPath)),
                        filePath: source.FullPath));
                }

                if (Parsed.InternalsVisibleTo is { Count: > 0 } friendAssemblies)
                {
                    string generated = InternalsVisibleToSource(friendAssemblies);
                    SourceText text = SourceText.From(generated);
                    retained = SaturatingAdd(retained,
                        SaturatingAdd((long)text.Length * sizeof(char), PerDocumentOverheadBytes));
                    const string generatedName = "__PhoenixCodeNav.InternalsVisibleTo.g.cs";
                    documents.Add(DocumentInfo.Create(
                        StableDocumentId(Identity.ProjectId, $"generated:{generatedName}"),
                        name: generatedName,
                        loader: TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create()))));
                }

                Resources.ResizeProjectReservation(retained);
                Documents = documents;
                _fallbacksResolved = true;
                return true;
            }
            finally
            {
                if (lockTaken) Monitor.Exit(_fallbackSync);
            }
        }
    }

    private sealed class PreparedProjectHandle : IDisposable
    {
        private ProjectResources? _ownedResources;

        public PreparedProjectHandle(PreparedProject project)
        {
            Project = project;
            _ownedResources = project.Resources;
            _ownedResources?.AddRef();
        }

        public PreparedProject Project { get; }

        public ProjectResources? TransferResources() =>
            Interlocked.Exchange(ref _ownedResources, null);

        public void Dispose() => Interlocked.Exchange(ref _ownedResources, null)?.Release();
    }

    private sealed class SharedPreparation
    {
        private readonly SemanticWorkspace _owner;
        private readonly PreparationKey _key;
        private readonly CancellationTokenSource _workCts;
        private int _waiters;
        private int _hasWaiter;
        private int _retired;
        private int _ownerReleased;

        public SharedPreparation(SemanticWorkspace owner, PreparationKey key, PlannedProject plan)
        {
            _owner = owner;
            _key = key;
            _workCts = CancellationTokenSource.CreateLinkedTokenSource(owner._disposeCts.Token);
            Task = owner.PrepareProjectAsync(plan, _workCts.Token);
            _ = Task.ContinueWith(_ =>
                {
                    TryRetire();
                    ReleaseOwnerIfRetired();
                }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private Task<PreparedProject> Task { get; }
        public int WaiterCount => Volatile.Read(ref _waiters);

        public void RegisterWaiter()
        {
            Volatile.Write(ref _hasWaiter, 1);
            Interlocked.Increment(ref _waiters);
        }

        public async Task<PreparedProjectHandle> AcquireRegisteredAsync(
            CancellationToken cancellationToken)
        {
            bool acquired = false;
            try
            {
                PreparedProject project = await Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;
                return new PreparedProjectHandle(project);
            }
            finally
            {
                Interlocked.Decrement(ref _waiters);
                if (!acquired && cancellationToken.IsCancellationRequested)
                    AbandonIfUnobserved();
                else
                    TryRetire();
                ReleaseOwnerIfRetired();
            }
        }

        private void TryRetire()
        {
            lock (_owner._inFlightPreparationSync)
            {
                if (Volatile.Read(ref _hasWaiter) == 0 ||
                    Volatile.Read(ref _waiters) != 0 || !Task.IsCompleted ||
                    Interlocked.Exchange(ref _retired, 1) != 0)
                    return;
                _owner._inFlightPreparations.TryRemove(
                    new KeyValuePair<PreparationKey, SharedPreparation>(_key, this));
            }
        }

        private void AbandonIfUnobserved()
        {
            bool cancel = false;
            lock (_owner._inFlightPreparationSync)
            {
                if (Volatile.Read(ref _waiters) != 0 ||
                    Interlocked.Exchange(ref _retired, 1) != 0)
                    return;
                _owner._inFlightPreparations.TryRemove(
                    new KeyValuePair<PreparationKey, SharedPreparation>(_key, this));
                cancel = !Task.IsCompleted;
            }
            if (cancel)
            {
                try { _workCts.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }

        private void ReleaseOwnerIfRetired()
        {
            if (Volatile.Read(ref _retired) == 0 || !Task.IsCompleted ||
                Interlocked.Exchange(ref _ownerReleased, 1) != 0)
                return;
            if (Task.Status == TaskStatus.RanToCompletion)
                Task.Result.Resources?.Release(); // release the single-flight cache owner
            _workCts.Dispose();
        }
    }

    private sealed class ProjectResources
    {
        private InputReservation? _projectReservation;
        private List<MetadataReferenceLease>? _metadataLeases;
        private int _references = 1;

        public ProjectResources(InputReservation projectReservation,
            List<MetadataReferenceLease> metadataLeases)
        {
            _projectReservation = projectReservation;
            _metadataLeases = metadataLeases;
        }

        public void AddRef()
        {
            int current = Volatile.Read(ref _references);
            while (current > 0)
            {
                int observed = Interlocked.CompareExchange(
                    ref _references, current + 1, current);
                if (observed == current) return;
                current = observed;
            }
            throw new ObjectDisposedException(nameof(ProjectResources));
        }

        public void ResizeProjectReservation(long bytes)
        {
            InputReservation? reservation = Volatile.Read(ref _projectReservation);
            if (reservation is null) throw new ObjectDisposedException(nameof(ProjectResources));
            reservation.Resize(bytes);
        }

        public long ProjectReservationBytes =>
            Volatile.Read(ref _projectReservation)?.Bytes ?? 0;

        public void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0) return;
            Interlocked.Exchange(ref _projectReservation, null)?.Dispose();
            List<MetadataReferenceLease>? leases = Interlocked.Exchange(ref _metadataLeases, null);
            if (leases is not null)
            {
                foreach (MetadataReferenceLease lease in leases) lease.Dispose();
            }
        }
    }

    private sealed class InputReservation : IDisposable
    {
        private readonly InputAccounting _owner;
        private long _bytes;

        public InputReservation(InputAccounting owner, long bytes)
        {
            _owner = owner;
            _bytes = bytes;
        }

        public long Bytes => Interlocked.Read(ref _bytes);

        public void Resize(long bytes)
        {
            bytes = Math.Max(0, bytes);
            lock (_owner.Sync)
            {
                long current = _bytes;
                _bytes = bytes;
                if (bytes > current) _owner.AddLocked(bytes - current);
                else _owner.ReleaseLocked(current - bytes);
            }
        }

        public InputReservation Split(long bytes)
        {
            bytes = Math.Max(0, bytes);
            lock (_owner.Sync)
            {
                long current = _bytes;
                _bytes = Math.Max(0, current - bytes);
                if (bytes > current) _owner.AddLocked(bytes - current);
                return new InputReservation(_owner, bytes);
            }
        }

        public void Dispose()
        {
            lock (_owner.Sync)
            {
                long bytes = _bytes;
                _bytes = 0;
                _owner.ReleaseLocked(bytes);
            }
        }
    }

    private sealed class InputAccounting
    {
        private long _retained;
        private long _highWater;

        public object Sync { get; } = new();
        public long RetainedBytes { get { lock (Sync) return _retained; } }
        public long HighWaterBytes { get { lock (Sync) return _highWater; } }

        public InputReservation Reserve(long bytes)
        {
            bytes = Math.Max(1, bytes);
            lock (Sync)
            {
                AddLocked(bytes);
                return new InputReservation(this, bytes);
            }
        }

        public void AddLocked(long bytes)
        {
            _retained = SaturatingAdd(_retained, bytes);
            _highWater = Math.Max(_highWater, _retained);
        }

        public void ReleaseLocked(long bytes)
        {
            _retained -= bytes;
            if (_retained < 0)
                throw new InvalidOperationException("semantic input accounting underflow");
        }
    }

    private readonly record struct MetadataCacheKey(string Path, long MTimeTicks, long Size);

    private sealed class MetadataReferenceLease : IDisposable
    {
        private MetadataCacheEntry? _entry;

        public MetadataReferenceLease(MetadataCacheEntry entry, PortableExecutableReference reference)
        {
            _entry = entry;
            Reference = reference;
        }

        public PortableExecutableReference Reference { get; }
        public void Dispose() => Interlocked.Exchange(ref _entry, null)?.Release();
    }

    private sealed class MetadataCacheEntry
    {
        private readonly ColdStartRuntime _owner;
        private readonly MetadataCacheKey _key;
        private readonly object _sync = new();
        private PortableExecutableReference? _reference;
        private InputReservation? _reservation;
        private int _leases;
        private bool _retired;

        public MetadataCacheEntry(ColdStartRuntime owner, MetadataCacheKey key)
        {
            _owner = owner;
            _key = key;
        }

        public MetadataReferenceLease? TryAcquire(InputReservation projectReservation,
            out bool retry)
        {
            retry = false;
            lock (_sync)
            {
                if (_retired)
                {
                    retry = true;
                    return null;
                }
                if (_reference is null)
                {
                    InputReservation split = projectReservation.Split(_key.Size);
                    try
                    {
                        _reference = MetadataReference.CreateFromFile(_key.Path);
                        _reservation = split;
                    }
                    catch
                    {
                        split.Dispose();
                        _retired = true;
                        _owner.Metadata.TryRemove(
                            new KeyValuePair<MetadataCacheKey, MetadataCacheEntry>(_key, this));
                        return null;
                    }
                }
                _leases++;
                return new MetadataReferenceLease(this, _reference);
            }
        }

        public void Release()
        {
            InputReservation? release = null;
            lock (_sync)
            {
                if (--_leases > 0) return;
                _retired = true;
                _owner.Metadata.TryRemove(
                    new KeyValuePair<MetadataCacheKey, MetadataCacheEntry>(_key, this));
                release = _reservation;
                _reservation = null;
                _reference = null;
            }
            release?.Dispose();
        }
    }

    private sealed class ColdStartRuntime
    {
        public ColdStartRuntime(int concurrency)
        {
            Accounting = new InputAccounting();
            ProjectSlots = new SemaphoreSlim(concurrency, concurrency);
            SourceReadSlots = new SemaphoreSlim(concurrency, concurrency);
            DescriptorSlots = new SemaphoreSlim(Math.Max(32, concurrency * 16),
                Math.Max(32, concurrency * 16));
            Concurrency = concurrency;
        }

        public InputAccounting Accounting { get; }
        public int Concurrency { get; }
        public SemaphoreSlim ProjectSlots { get; }
        public SemaphoreSlim SourceReadSlots { get; }
        public SemaphoreSlim DescriptorSlots { get; }
        public SemaphoreSlim LargeFileSlot { get; } = new(1, 1);
        public ConcurrentDictionary<MetadataCacheKey, MetadataCacheEntry> Metadata { get; } = new();

        public MetadataReferenceLease? AcquireMetadata(
            string fullPath, InputReservation projectReservation)
        {
            try
            {
                string canonical = Path.GetFullPath(fullPath);
                if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
                var info = new FileInfo(canonical);
                if (!info.Exists || info.Length <= 0) return null;
                var key = new MetadataCacheKey(canonical, info.LastWriteTimeUtc.Ticks, info.Length);
                while (true)
                {
                    MetadataCacheEntry entry = Metadata.GetOrAdd(key,
                        static (cacheKey, runtime) => new MetadataCacheEntry(runtime, cacheKey), this);
                    MetadataReferenceLease? lease = entry.TryAcquire(projectReservation,
                        out bool retry);
                    if (!retry) return lease;
                }
            }
            catch
            {
                return null;
            }
        }
    }

    private async Task<SemanticSolutionLease> EnsureLoadedParallelAsync(
        IReadOnlyCollection<string> projectNames, CancellationToken cancellationToken,
        IReadOnlyCollection<string>? ensureReferenceTo, LoadStatsBox? statsBox)
    {
        var requested = new HashSet<string>(projectNames, StringComparer.OrdinalIgnoreCase);
        lock (_planningOwnershipSync)
        {
            foreach (string name in requested)
            {
                _activeRequestedProjects.TryGetValue(name, out int count);
                _activeRequestedProjects[name] = count + 1;
            }
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        long gateWaitTicks = 0;
        long fingerprintTicks = 0;
        long topoTicks = 0;
        long planTicks = 0;
        long prepareTicks = 0;
        long commitTicks = 0;
        long parseTicks = 0;
        long readTicks = 0;
        long metadataTicks = 0;
        long queueTicks = 0;
        long activePlanStarted = 0;
        long activePrepareStarted = 0;
        long activeCommitStarted = 0;
        int? loadedBefore = null;
        int reloaded = 0;
        int loadedResult = 0;
        int failedResult = 0;
        int preparedCount = 0;
        int committedCount = 0;
        int replanCount = 0;

        async Task WaitForGateAsync()
        {
            long wait = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gateWaitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - wait;
            }
        }

        void PublishStats()
        {
            if (statsBox is null) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long elapsed = now - started;
            long publishedPlanTicks = planTicks +
                (activePlanStarted == 0 ? 0 : now - activePlanStarted);
            long publishedPrepareTicks = prepareTicks +
                (activePrepareStarted == 0 ? 0 : now - activePrepareStarted);
            long publishedCommitTicks = commitTicks +
                (activeCommitStarted == 0 ? 0 : now - activeCommitStarted);
            double projectLoadMs = ToMs(publishedPrepareTicks + publishedCommitTicks);
            statsBox.Stats = new LoadStats(
                ToMs(gateWaitTicks), ToMs(fingerprintTicks), ToMs(topoTicks), projectLoadMs,
                loadedBefore, requested.Count, reloaded, loadedResult, failedResult,
                ToMs(parseTicks), ToMs(readTicks), ToMs(metadataTicks), ToMs(publishedCommitTicks),
                PlanMs: ToMs(publishedPlanTicks),
                PreparationMs: ToMs(publishedPrepareTicks), PreparedProjects: preparedCount,
                EffectiveProjectConcurrency: Math.Min(preparedCount,
                    _coldStartRuntime.Concurrency),
                AdmittedBytesHighWater: _coldStartRuntime.Accounting.HighWaterBytes,
                RetainedBytes: _coldStartRuntime.Accounting.RetainedBytes,
                ReplanCount: replanCount,
                TotalElapsedMs: ToMs(elapsed),
                PreparationQueueMs: ToMs(queueTicks),
                CommittedProjects: committedCount);
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long plannedGeneration;
                Dictionary<string, LoadedSnapshot> resident;
                Dictionary<string, ProjectId> plannedIds;
                await WaitForGateAsync().ConfigureAwait(false);
                try
                {
                    loadedBefore ??= _loaded.Count;
                    plannedGeneration = _workspaceGeneration;
                    Solution plannedSolution = _workspace.CurrentSolution;
                    Dictionary<ProjectId, string> namesById = _loaded.ToDictionary(
                        pair => pair.Value.Id, pair => pair.Key);
                    resident = _loaded.ToDictionary(
                        pair => pair.Key,
                        pair => new LoadedSnapshot(
                            pair.Value.Id,
                            pair.Value.Fingerprint,
                            pair.Value.ModelIdentity,
                            plannedSolution.GetProject(pair.Value.Id)?.AllProjectReferences
                                .Select(reference => namesById.GetValueOrDefault(reference.ProjectId))
                                .Where(name => name is not null)
                                .Cast<string>()
                                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                            pair.Value.PhysicalReferenceNames),
                        StringComparer.OrdinalIgnoreCase);
                    lock (_planningOwnershipSync)
                    {
                        foreach (string name in requested)
                        {
                            if (!resident.ContainsKey(name) && !_plannedProjectIds.ContainsKey(name))
                                _plannedProjectIds[name] = StableProjectId(name);
                        }
                        plannedIds = requested.ToDictionary(
                            name => name,
                            name => resident.TryGetValue(name, out LoadedSnapshot? loaded)
                                ? loaded.Id
                                : _plannedProjectIds[name],
                            StringComparer.OrdinalIgnoreCase);
                    }
                }
                finally
                {
                    _gate.Release();
                }

                long planStarted = activePlanStarted =
                    System.Diagnostics.Stopwatch.GetTimestamp();
                using var queries = new IndexQueries(_dbPath, pinReadSnapshot: true,
                    beforeQueryForTest: TestOnlyBeforeColdStartSql,
                    pooling: _poolIndexConnections, cancellationToken: cancellationToken);
                IndexMetadataSnapshot plannedMetadata = queries.ReadMetadata(cancellationToken);
                long phase = System.Diagnostics.Stopwatch.GetTimestamp();
                string[] residentRequested = requested.Where(resident.ContainsKey).ToArray();
                Dictionary<string, (long, long)> fingerprints = requested.Count > 0
                    ? queries.ProjectFingerprints(requested, cancellationToken)
                    : new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
                fingerprintTicks += System.Diagnostics.Stopwatch.GetTimestamp() - phase;

                var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string name in residentRequested)
                {
                    (long, long) current = fingerprints.TryGetValue(name, out var value)
                        ? value : (0L, 0L);
                    if (resident[name].Fingerprint != current) changed.Add(name);
                }

                long planningBytes = queries.SemanticPlanningDescriptorBytes(requested,
                    cancellationToken);
                using InputReservation planningLease =
                    _coldStartRuntime.Accounting.Reserve(planningBytes);

                phase = System.Diagnostics.Stopwatch.GetTimestamp();
                Dictionary<string, List<ProjectRow>> rowsByProject =
                    queries.ProjectsByNames(requested, cancellationToken);
                ProjectRow[] selectedCSharpRows = rowsByProject.Values
                    .Select(rows => rows.FirstOrDefault(row => row.Language == "cs"))
                    .Where(row => row is not null)
                    .Cast<ProjectRow>()
                    .ToArray();
                Dictionary<string, DirectoryBuildSemanticAuthority> plannedAuthorities =
                    queries.ProjectDirectoryBuildSemanticAuthorities(selectedCSharpRows,
                        cancellationToken);

                string ModelIdentity(string name) => string.Join('\u001f',
                    plannedMetadata.SchemaVersion,
                    plannedMetadata.IndexVersion,
                    plannedAuthorities.GetValueOrDefault(name)?.Identity);
                foreach (string name in residentRequested)
                {
                    if (!string.Equals(resident[name].ModelIdentity, ModelIdentity(name),
                            StringComparison.Ordinal))
                        changed.Add(name);
                }

                List<SemanticProjectEdge> requestedEdges =
                    queries.SemanticProjectEdges(requested, cancellationToken);
                Dictionary<string, List<SemanticProjectEdge>> edgesByProject = requestedEdges
                    .GroupBy(edge => edge.FromProject, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.ToList(),
                        StringComparer.OrdinalIgnoreCase);
                Dictionary<string, HashSet<string>> ensuredByProject =
                    ensureReferenceTo is { Count: > 0 }
                        ? queries.SemanticCSharpReachability(requested, ensureReferenceTo,
                            cancellationToken)
                        : new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                var missingEnsured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var retiredOperationReferences = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (string name in residentRequested)
                {
                    ensuredByProject.TryGetValue(name, out HashSet<string>? ensured);
                    if (ensured is not null &&
                        ensured.Any(target => !resident[name].DirectReferences.Contains(target)))
                        missingEnsured.Add(name);

                    var desiredReferences = new HashSet<string>(
                        resident[name].PhysicalReferenceNames,
                        StringComparer.OrdinalIgnoreCase);
                    if (ensured is not null) desiredReferences.UnionWith(ensured);
                    if (resident[name].DirectReferences.Any(reference =>
                            !desiredReferences.Contains(reference)))
                        retiredOperationReferences.Add(name);
                }
                var missingPhysical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string name in residentRequested)
                {
                    if (!edgesByProject.TryGetValue(name,
                            out List<SemanticProjectEdge>? projectEdges)) continue;
                    if (projectEdges.Any(edge =>
                            edge.ToLanguage.Equals("cs", StringComparison.OrdinalIgnoreCase) &&
                            requested.Contains(edge.ToProject) &&
                            !resident[name].DirectReferences.Contains(edge.ToProject)))
                        missingPhysical.Add(name);
                }
                List<string> toLoad = TopoOrder(requested
                    .Where(name => !resident.ContainsKey(name) || changed.Contains(name))
                    .ToList(), requestedEdges);
                var toLoadSet = new HashSet<string>(toLoad, StringComparer.OrdinalIgnoreCase);
                var rewireCandidates = new HashSet<string>(missingEnsured,
                    StringComparer.OrdinalIgnoreCase);
                rewireCandidates.UnionWith(retiredOperationReferences);
                rewireCandidates.UnionWith(missingPhysical);
                foreach ((string name, LoadedSnapshot loaded) in resident)
                {
                    if (loaded.PhysicalReferenceNames.Overlaps(toLoadSet))
                        rewireCandidates.Add(name);
                }
                foreach (string name in residentRequested)
                {
                    if (ensuredByProject.TryGetValue(name,
                            out HashSet<string>? ensuredReferences) &&
                        ensuredReferences.Overlaps(toLoadSet))
                        rewireCandidates.Add(name);
                }
                rewireCandidates.ExceptWith(toLoadSet);
                topoTicks += System.Diagnostics.Stopwatch.GetTimestamp() - phase;

                var skipped = new List<string>();
                var failed = new List<string>();
                var failureCauses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, List<(string Path, long Size)>> filesByProject =
                    queries.ProjectFilesWithSizes(toLoad, cancellationToken);
                var plans = new List<PlannedProject>();
                foreach (string name in toLoad)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    List<ProjectRow> rows = rowsByProject.GetValueOrDefault(name) ?? [];
                    ProjectRow? row = rows.FirstOrDefault(candidate => candidate.Language == "cs");
                    if (row is null && rows.Count > 0)
                    {
                        skipped.Add(name);
                        continue;
                    }
                    if (row is null)
                    {
                        failed.Add(name);
                        continue;
                    }
                    (long, long) fingerprint = fingerprints.TryGetValue(name, out var value)
                        ? value : (0L, 0L);
                    ProjectId id = plannedIds[name];
                    DirectoryBuildSemanticAuthority authority =
                        plannedAuthorities.GetValueOrDefault(name) ?? new(false, "");
                    plans.Add(new PlannedProject(
                        name, row, id, ModelIdentity(name), fingerprint,
                        filesByProject.GetValueOrDefault(name) ?? [],
                        authority.HasPotentialAuthority,
                        0,
                        0));
                }
                Dictionary<string, long> descriptorSizes = queries.FileSizesByExactPath(
                    plans.SelectMany(plan => new[]
                    {
                        plan.Row.Path,
                        PackagesConfigPath(plan.Row.Path),
                    }).ToArray(), cancellationToken);
                for (int i = 0; i < plans.Count; i++)
                {
                    PlannedProject plan = plans[i];
                    descriptorSizes.TryGetValue(plan.Row.Path, out long projectFileSize);
                    descriptorSizes.TryGetValue(PackagesConfigPath(plan.Row.Path),
                        out long packagesFileSize);
                    plans[i] = plan with
                    {
                        ProjectFileSize = projectFileSize,
                        PackagesFileSize = packagesFileSize,
                    };
                }
                IReadOnlyList<MetadataReference> frameworkReferences =
                    ReferenceAssemblyLocator.Net472References(out string? referenceDirectory);
                if (referenceDirectory is null)
                    _log("WARNING: no .NET Framework reference assemblies found — semantic fidelity degraded.");
                planTicks += System.Diagnostics.Stopwatch.GetTimestamp() - planStarted;
                activePlanStarted = 0;

                long prepareStarted = activePrepareStarted =
                    System.Diagnostics.Stopwatch.GetTimestamp();
                var prepared = new ConcurrentDictionary<string, PreparedProjectHandle>(
                    StringComparer.OrdinalIgnoreCase);
                try
                {
                    try
                    {
                        await Parallel.ForEachAsync(plans, new ParallelOptions
                        {
                            MaxDegreeOfParallelism = _coldStartRuntime.Concurrency,
                            CancellationToken = cancellationToken,
                        }, async (plan, ct) =>
                        {
                            PreparedProjectHandle handle = await AcquirePreparedProjectAsync(plan, ct)
                                .ConfigureAwait(false);
                            prepared[plan.Name] = handle;
                        }).ConfigureAwait(false);
                        foreach (PreparedProjectHandle handle in prepared.Values)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (TestOnlyBeforeIndexedFallbackAsync is { } beforeFallback)
                                await beforeFallback(handle.Project.Identity.Name, cancellationToken)
                                    .ConfigureAwait(false);
                            if (!handle.Project.EnsureIndexedFallbacks(queries,
                                    cancellationToken, TestOnlyAfterIndexedFallbackLock) ||
                                handle.Project.Documents.Count == 0)
                            {
                                failed.Add(handle.Project.Identity.Name);
                                if (handle.Project.FailureCause is { } cause)
                                {
                                    failureCauses[handle.Project.Identity.Name] = cause;
                                    _log($"Semantic preparation failed for {handle.Project.Identity.Name}: " +
                                        cause);
                                }
                            }
                        }
                    }
                    finally
                    {
                        prepareTicks += System.Diagnostics.Stopwatch.GetTimestamp() - prepareStarted;
                        activePrepareStarted = 0;
                        preparedCount += prepared.Count;
                        foreach (PreparedProjectHandle completed in prepared.Values)
                        {
                            parseTicks += completed.Project.ParseTicks;
                            readTicks += completed.Project.ReadTicks;
                            metadataTicks += completed.Project.MetadataTicks;
                            queueTicks += completed.Project.QueueTicks;
                        }
                    }

                    if (TestOnlyBeforeCommitAsync is { } beforeCommit)
                        await beforeCommit(cancellationToken).ConfigureAwait(false);

                    // Re-check every requested resident and all graph/model inputs immediately
                    // before commit. A refresh can invalidate a warm owner just as decisively as a
                    // project prepared in this call; nothing has been published yet.
                    using var verify = new IndexQueries(_dbPath, pinReadSnapshot: false,
                        beforeQueryForTest: TestOnlyBeforeColdStartSql,
                        pooling: _poolIndexConnections);
                    Dictionary<string, (long, long)> currentFingerprints = requested.Count > 0
                        ? verify.ProjectFingerprints(requested, cancellationToken)
                        : new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
                    IndexMetadataSnapshot currentMetadata = verify.ReadMetadata(cancellationToken);
                    Dictionary<string, List<ProjectRow>> currentRows =
                        verify.ProjectsByNames(requested, cancellationToken);
                    ProjectRow[] currentCSharpRows = currentRows.Values
                        .Select(rows => rows.FirstOrDefault(row => row.Language == "cs"))
                        .Where(row => row is not null)
                        .Cast<ProjectRow>()
                        .ToArray();
                    Dictionary<string, DirectoryBuildSemanticAuthority> currentAuthorities =
                        verify.ProjectDirectoryBuildSemanticAuthorities(currentCSharpRows,
                            cancellationToken);
                    string CurrentModelIdentity(string name) => string.Join('\u001f',
                        currentMetadata.SchemaVersion,
                        currentMetadata.IndexVersion,
                        currentAuthorities.GetValueOrDefault(name)?.Identity);
                    bool staleIndex = requested.Any(name =>
                        (currentFingerprints.TryGetValue(name, out var current)
                            ? current : (0L, 0L)) !=
                        (fingerprints.TryGetValue(name, out var planned)
                            ? planned : (0L, 0L)) ||
                        !string.Equals(CurrentModelIdentity(name), ModelIdentity(name),
                            StringComparison.Ordinal));
                    if (staleIndex)
                    {
                        replanCount++;
                        continue;
                    }

                    await WaitForGateAsync().ConfigureAwait(false);
                    try
                    {
                        if (_workspaceGeneration != plannedGeneration)
                        {
                            replanCount++;
                            continue;
                        }
                        long commitStarted = activeCommitStarted =
                            System.Diagnostics.Stopwatch.GetTimestamp();

                        Solution next = _workspace.CurrentSolution;
                        var stagedLoaded = new Dictionary<string, LoadedProject>(
                            StringComparer.OrdinalIgnoreCase);
                        bool stagedPublished = false;
                        try
                        {
                            var workingLoaded = new Dictionary<string, LoadedProject>(_loaded,
                                StringComparer.OrdinalIgnoreCase);
                            var removed = new Dictionary<string, LoadedProject>(
                                StringComparer.OrdinalIgnoreCase);
                            foreach (string name in toLoad)
                            {
                                if (!workingLoaded.Remove(name, out LoadedProject? old)) continue;
                                removed[name] = old;
                                next = next.RemoveProject(old.Id);
                                _log(changed.Contains(name)
                                    ? $"Semantic reload (files changed): {name}"
                                    : $"Semantic reload (operation reference wiring changed): {name}");
                            }

                            foreach (string name in toLoad)
                            {
                                if (!prepared.TryGetValue(name,
                                        out PreparedProjectHandle? handle) ||
                                    handle.Project.FailureCause is not null ||
                                    handle.Project.Resources is null ||
                                    handle.Project.Documents.Count == 0)
                                    continue;
                                PreparedProject project = handle.Project;
                                var projectReferences = new List<ProjectReference>();
                                var referenceIds = new HashSet<ProjectId>();
                                var wiredNames = new HashSet<string>(
                                    StringComparer.OrdinalIgnoreCase);
                                var physicalTargets = (edgesByProject.GetValueOrDefault(name) ?? [])
                                    .Where(edge => edge.ToLanguage.Equals("cs",
                                        StringComparison.OrdinalIgnoreCase))
                                    .Select(edge => edge.ToProject)
                                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                                bool TryWire(string target)
                                {
                                    if (target.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                        !workingLoaded.TryGetValue(target,
                                            out LoadedProject? dependency) ||
                                        !referenceIds.Add(dependency.Id) ||
                                        ReachesId(next, dependency.Id, project.Identity.ProjectId))
                                        return false;
                                    projectReferences.Add(new ProjectReference(dependency.Id));
                                    wiredNames.Add(target);
                                    return true;
                                }

                                foreach (string target in physicalTargets) TryWire(target);
                                if (ensuredByProject.TryGetValue(name,
                                        out HashSet<string>? ensuredReferences))
                                {
                                    foreach (string target in ensuredReferences) TryWire(target);
                                }

                                var metadataReferences = new List<MetadataReference>(
                                    frameworkReferences);
                                foreach (PreparedMetadataCandidate candidate in
                                         project.MetadataCandidates)
                                {
                                    if (candidate.AssemblyName is { } assembly &&
                                        (wiredNames.Contains(assembly) ||
                                         wiredNames.Contains(Path.GetFileNameWithoutExtension(
                                             candidate.FullPath))))
                                        continue;
                                    metadataReferences.Add(candidate.Reference);
                                }

                                var info = ProjectInfo.Create(
                                    project.Identity.ProjectId,
                                    VersionStamp.Create(),
                                    name: name,
                                    assemblyName: name,
                                    language: LanguageNames.CSharp,
                                    filePath: Path.Combine(_workspaceRoot,
                                        project.Identity.ProjectPath.Replace('/',
                                            Path.DirectorySeparatorChar)),
                                    compilationOptions: CompilationOptions,
                                    parseOptions: ParseOptions,
                                    documents: project.Documents,
                                    projectReferences: projectReferences,
                                    metadataReferences: metadataReferences);
                                next = next.AddProject(info);
                                var loadedProject = new LoadedProject
                                {
                                    Id = project.Identity.ProjectId,
                                    Fingerprint = project.Identity.Fingerprint,
                                    ModelIdentity = project.Identity.ModelIdentity,
                                    UnprovenFriendAssemblyAuthority =
                                        project.UnprovenFriendAssemblyAuthority,
                                    LastUse = 0,
                                    Resources = handle.TransferResources(),
                                    MetadataCandidates = project.MetadataCandidates,
                                    PhysicalReferenceNames = physicalTargets,
                                };
                                stagedLoaded[name] = loadedProject;
                                workingLoaded[name] = loadedProject;
                            }

                            int rewiredProjects = 0;
                            foreach (string name in rewireCandidates.OrderBy(value => value,
                                         StringComparer.OrdinalIgnoreCase))
                            {
                                if (!workingLoaded.TryGetValue(name,
                                        out LoadedProject? loadedProject) ||
                                    next.GetProject(loadedProject.Id) is not { } currentProject)
                                    continue;

                                var currentMetadataReferences =
                                    currentProject.MetadataReferences.ToList();
                                next = next.WithProjectReferences(loadedProject.Id,
                                    Array.Empty<ProjectReference>());
                                var projectReferences = new List<ProjectReference>();
                                var referenceIds = new HashSet<ProjectId>();
                                var wiredNames = new HashSet<string>(
                                    StringComparer.OrdinalIgnoreCase);

                                void TryWire(string target)
                                {
                                    if (target.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                        !workingLoaded.TryGetValue(target,
                                            out LoadedProject? dependency) ||
                                        !referenceIds.Add(dependency.Id) ||
                                        ReachesId(next, dependency.Id, loadedProject.Id))
                                        return;
                                    projectReferences.Add(new ProjectReference(dependency.Id));
                                    wiredNames.Add(target);
                                }

                                foreach (string target in loadedProject.PhysicalReferenceNames)
                                    TryWire(target);
                                if (ensuredByProject.TryGetValue(name,
                                        out HashSet<string>? ensuredReferences))
                                {
                                    foreach (string target in ensuredReferences) TryWire(target);
                                }

                                var metadataReferences = currentMetadataReferences;
                                foreach (PreparedMetadataCandidate candidate in
                                         loadedProject.MetadataCandidates)
                                {
                                    if (!metadataReferences.Contains(candidate.Reference))
                                        metadataReferences.Add(candidate.Reference);
                                }
                                var suppressed = loadedProject.MetadataCandidates
                                    .Where(candidate =>
                                        candidate.AssemblyName is { } assembly &&
                                        (wiredNames.Contains(assembly) ||
                                         wiredNames.Contains(Path.GetFileNameWithoutExtension(
                                             candidate.FullPath))))
                                    .Select(candidate => (MetadataReference)candidate.Reference)
                                    .ToHashSet();
                                metadataReferences.RemoveAll(suppressed.Contains);
                                next = next.WithProjectReferences(loadedProject.Id,
                                    projectReferences);
                                next = next.WithProjectMetadataReferences(loadedProject.Id,
                                    metadataReferences);
                                rewiredProjects++;
                            }

                            bool changedSolution = removed.Count > 0 || stagedLoaded.Count > 0 ||
                                                   rewiredProjects > 0;
                            if (changedSolution &&
                                (TestOnlyRejectPreparedCommit ||
                                 !_workspace.TryApplyChanges(next)))
                            {
                                throw new InvalidOperationException(
                                    "semantic workspace rejected the prepared solution");
                            }
                            if (changedSolution)
                            {
                                foreach ((string name, LoadedProject old) in removed)
                                {
                                    _loaded.Remove(name);
                                    old.Resources?.Release();
                                    reloaded++;
                                }
                                foreach ((string name, LoadedProject project) in stagedLoaded)
                                {
                                    _loaded[name] = project;
                                    lock (_planningOwnershipSync)
                                        _plannedProjectIds.Remove(name);
                                }
                                committedCount += stagedLoaded.Count;
                                _workspaceGeneration++;
                            }
                            stagedPublished = true;

                            foreach (string name in requested)
                            {
                                if (_loaded.TryGetValue(name, out LoadedProject? project))
                                    project.LastUse = ++_useCounter;
                            }
                            EvictBeyondCap(requested);

                            var skippedSet = new HashSet<string>(skipped,
                                StringComparer.OrdinalIgnoreCase);
                            foreach (SemanticProjectEdge edge in requestedEdges)
                            {
                                if (!edge.ToLanguage.Equals("cs",
                                        StringComparison.OrdinalIgnoreCase) &&
                                    !skippedSet.Contains(edge.ToProject))
                                    skippedSet.Add(edge.ToPath);
                            }
                            List<string> orderedSkipped = skippedSet
                                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
                            failed = failed.Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
                            var coverage = new ClusterCoverage(
                                LoadedProjects: requested.Count(name => _loaded.ContainsKey(name)),
                                RequestedProjects: requested.Count,
                                SkippedProjects: orderedSkipped,
                                FailedProjects: failed,
                                FrameworkRefsAvailable: referenceDirectory is not null,
                                SolutionProjects: _workspace.CurrentSolution.ProjectIds.Count,
                                UnprovenFriendAssemblyProjects: requested
                                    .Where(name => _loaded.TryGetValue(name,
                                                       out LoadedProject? project) &&
                                                   project.UnprovenFriendAssemblyAuthority)
                                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                                    .ToList(),
                                FailedProjectCauses: failureCauses.Count > 0
                                    ? failureCauses
                                    : null);
                            loadedResult = coverage.LoadedProjects;
                            failedResult = coverage.FailedProjects.Count;
                            SemanticSolutionLease lease = CreateSolutionLease(coverage, requested);
                            PublishStats();
                            return lease;
                        }
                        finally
                        {
                            commitTicks += System.Diagnostics.Stopwatch.GetTimestamp() -
                                           commitStarted;
                            activeCommitStarted = 0;
                            if (!stagedPublished)
                            {
                                foreach (LoadedProject staged in stagedLoaded.Values)
                                    staged.Resources?.Release();
                            }
                        }
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
                finally
                {
                    foreach (PreparedProjectHandle handle in prepared.Values) handle.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            PublishStats();
            throw;
        }
        catch
        {
            PublishStats();
            throw;
        }
        finally
        {
            lock (_planningOwnershipSync)
            {
                foreach (string name in requested)
                {
                    if (!_activeRequestedProjects.TryGetValue(name, out int count)) continue;
                    if (count <= 1)
                    {
                        _activeRequestedProjects.Remove(name);
                        _plannedProjectIds.Remove(name);
                    }
                    else _activeRequestedProjects[name] = count - 1;
                }
            }
        }
    }

    private SemanticSolutionLease CreateSolutionLease(ClusterCoverage coverage,
        IReadOnlyCollection<string> requested)
    {
        Solution solution = _workspace.CurrentSolution;
        ProjectId[] requestedProjectIds = requested
            .Where(name => _loaded.TryGetValue(name, out LoadedProject? loaded) &&
                           solution.GetProject(loaded.Id) is not null)
            .Select(name => _loaded[name].Id)
            .Distinct()
            .OrderBy(id => solution.GetProject(id)?.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ProjectResources[] resources = _loaded.Values
            .Select(project => project.Resources)
            .Where(resource => resource is not null)
            .Cast<ProjectResources>()
            .ToArray();
        foreach (ProjectResources resource in resources) resource.AddRef();
        return new SemanticSolutionLease(solution, coverage, requestedProjectIds, () =>
        {
            foreach (ProjectResources resource in resources) resource.Release();
        });
    }

    private async Task<PreparedProjectHandle> AcquirePreparedProjectAsync(
        PlannedProject plan, CancellationToken cancellationToken)
    {
        var key = new PreparationKey(plan.Name, plan.Row.Path, plan.ProjectId,
            plan.Fingerprint.Count, plan.Fingerprint.Sum, plan.SnapshotIdentity);
        while (true)
        {
            SharedPreparation shared;
            lock (_inFlightPreparationSync)
            {
                if (!_inFlightPreparations.TryGetValue(key, out shared!))
                {
                    shared = new SharedPreparation(this, key, plan);
                    _inFlightPreparations[key] = shared;
                }
                shared.RegisterWaiter();
            }
            try
            {
                return await shared.AcquireRegisteredAsync(cancellationToken).ConfigureAwait(false);
            }
            catch when (!cancellationToken.IsCancellationRequested &&
                        !_disposeCts.IsCancellationRequested)
            {
                // The registered waiter's finally retires the faulted entry. Loop to create a
                // clean single-flight rather than allowing one failure to poison the key.
            }
        }
    }

    private async Task<PreparedProject> PrepareProjectAsync(
        PlannedProject plan, CancellationToken cancellationToken)
    {
        long queueStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        await _coldStartRuntime.DescriptorSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _coldStartRuntime.ProjectSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            long projectQueueTicks = System.Diagnostics.Stopwatch.GetTimestamp() - queueStarted;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                long[] sourceBounds = plan.Files.Select(file =>
                    FileCaptureBound(file.Path, file.Size,
                        DeltaRefresher.MaxIndexedFileBytes)).ToArray();
                long projectBound = FileCaptureBound(plan.Row.Path, plan.ProjectFileSize,
                    IndexBuilder.MaxStructuralFileBytes);
                string packagesPath = PackagesConfigPath(plan.Row.Path);
                long packagesBound = FileCaptureBound(packagesPath, plan.PackagesFileSize,
                    IndexBuilder.MaxStructuralFileBytes);
                string projectFullPath = Path.Combine(_workspaceRoot,
                    plan.Row.Path.Replace('/', Path.DirectorySeparatorChar));
                string packagesFullPath = Path.Combine(_workspaceRoot,
                    packagesPath.Replace('/', Path.DirectorySeparatorChar));
                long descriptorAllowance = SaturatingAdd(PreparedProjectOverheadBytes,
                    SaturatingAdd(projectBound * 2, packagesBound * 2));
                InputReservation descriptorReservation =
                    _coldStartRuntime.Accounting.Reserve(descriptorAllowance);

                long parseStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                ParsedProject parsed;
                long projectByteCount;
                long packagesByteCount;
                try
                {
                    byte[] projectBytes = GitInfo.ReadBoundedWorkspaceFile(_workspaceRoot,
                        plan.Row.Path, (int)Math.Clamp(projectBound, 1,
                            IndexBuilder.MaxStructuralFileBytes)) ?? [];
                    if (projectBytes.Length == 0 &&
                        SourceExceedsBound(projectFullPath, projectBound))
                    {
                        throw new InvalidOperationException(
                            $"semantic project changed beyond its admitted bound: {plan.Row.Path}");
                    }
                    byte[]? packagesBytes = GitInfo.ReadBoundedWorkspaceFile(_workspaceRoot,
                        packagesPath, (int)Math.Clamp(packagesBound, 1,
                            IndexBuilder.MaxStructuralFileBytes));
                    if (packagesBytes is null &&
                        SourceExceedsBound(packagesFullPath, packagesBound))
                    {
                        throw new InvalidOperationException(
                            $"semantic packages.config changed beyond its admitted bound: {packagesPath}");
                    }
                    projectByteCount = projectBytes.LongLength;
                    packagesByteCount = packagesBytes?.LongLength ?? 0;
                    parsed = ProjectFileParser.ParseSnapshot(
                        plan.Row.Path, projectBytes, packagesBytes);
                }
                finally
                {
                    descriptorReservation.Dispose();
                }
                long parseTicks = System.Diagnostics.Stopwatch.GetTimestamp() - parseStarted;

                long descriptorRetainedBytes = SaturatingAdd(projectByteCount * 2,
                    packagesByteCount * 2);
                long estimate = EstimateProjectBytes(plan, parsed, projectByteCount,
                    packagesByteCount, sourceBounds);
                InputReservation reservation = _coldStartRuntime.Accounting.Reserve(estimate);

                var metadataLeases = new List<MetadataReferenceLease>();
                try
                {
                    if (TestOnlyBeforeProjectCaptureAsync is { } beforeCapture)
                        await beforeCapture(plan.Name, cancellationToken).ConfigureAwait(false);

                    long readStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    var sources = new PreparedSource[plan.Files.Count];

                    async ValueTask CaptureSourceAsync(int i, SemaphoreSlim lane,
                        CancellationToken captureToken)
                    {
                        await lane.WaitAsync(captureToken).ConfigureAwait(false);
                        try
                        {
                            captureToken.ThrowIfCancellationRequested();
                            string relativePath = plan.Files[i].Path;
                            string fullPath = Path.Combine(_workspaceRoot,
                                relativePath.Replace('/', Path.DirectorySeparatorChar));
                            if (TestOnlyBeforeSourceCaptureAsync is { } beforeSourceCapture)
                                await beforeSourceCapture(plan.Name, relativePath, captureToken)
                                    .ConfigureAwait(false);
                            int readLimit = (int)Math.Clamp(sourceBounds[i], 1,
                                DeltaRefresher.MaxIndexedFileBytes);
                            byte[]? bytes = GitInfo.ReadBoundedWorkspaceFile(_workspaceRoot,
                                relativePath, readLimit);
                            if (bytes is null && SourceExceedsBound(fullPath, sourceBounds[i]))
                                throw new InvalidOperationException(
                                    $"semantic source changed beyond its admitted bound: {relativePath}");
                            SourceText? text = null;
                            if (bytes is not null)
                            {
                                using var stream = new MemoryStream(bytes, writable: false);
                                text = SourceText.From(stream);
                            }
                            sources[i] = new PreparedSource
                            {
                                RelativePath = relativePath,
                                FullPath = fullPath,
                                Text = text,
                            };
                        }
                        finally
                        {
                            lane.Release();
                        }
                    }

                    int[] smallFiles = Enumerable.Range(0, plan.Files.Count)
                        .Where(i => sourceBounds[i] <= LargeSourceFileBytes).ToArray();
                    await Parallel.ForEachAsync(smallFiles, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = _coldStartRuntime.Concurrency,
                        CancellationToken = cancellationToken,
                    }, async (index, ct) =>
                        await CaptureSourceAsync(index, _coldStartRuntime.SourceReadSlots, ct)
                            .ConfigureAwait(false)).ConfigureAwait(false);

                    foreach (int index in Enumerable.Range(0, plan.Files.Count)
                                 .Where(i => sourceBounds[i] > LargeSourceFileBytes))
                    {
                        await CaptureSourceAsync(index, _coldStartRuntime.LargeFileSlot,
                            cancellationToken).ConfigureAwait(false);
                    }
                    long readTicks = System.Diagnostics.Stopwatch.GetTimestamp() - readStarted;

                    long metadataStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    var candidates = new List<PreparedMetadataCandidate>();
                    var leasesByPath = new Dictionary<string, MetadataReferenceLease>(
                        WorkspacePaths.FileSystemPathComparer);

                    void AddCandidate(string? assemblyName, string fullPath)
                    {
                        string key = Path.GetFullPath(fullPath);
                        if (!leasesByPath.TryGetValue(key, out MetadataReferenceLease? lease))
                        {
                            lease = _coldStartRuntime.AcquireMetadata(key, reservation);
                            if (lease is null) return; // unreadable/missing keeps old skip semantics
                            leasesByPath[key] = lease;
                            metadataLeases.Add(lease);
                        }
                        candidates.Add(new PreparedMetadataCandidate(
                            assemblyName, key, lease.Reference));
                    }

                    foreach ((string assembly, string? hint) in parsed.AssemblyRefs)
                    {
                        if (hint is null) continue;
                        string full = Path.Combine(_workspaceRoot,
                            hint.Replace('/', Path.DirectorySeparatorChar));
                        AddCandidate(assembly, full);
                    }
                    foreach ((string package, string version) in parsed.PackageRefs)
                    {
                        if (ReferenceAssemblyLocator.ResolvePackageDll(package, version) is { } dll)
                            AddCandidate(null, dll);
                    }
                    long metadataTicks = System.Diagnostics.Stopwatch.GetTimestamp() - metadataStarted;

                    bool unproven = parsed.InternalsVisibleTo is { Count: > 0 } &&
                        (!parsed.InternalsVisibleToAuthorityComplete ||
                         plan.HasDirectoryBuildAuthority);
                    if (unproven)
                        _log($"Semantic project-model boundary: imported friend-assembly authority is unproven for {plan.Name}.");

                    var resources = new ProjectResources(reservation, metadataLeases);
                    var preparedProject = new PreparedProject
                    {
                        Identity = PreparedIdentity(plan),
                        Parsed = parsed,
                        Sources = sources,
                        MetadataCandidates = candidates,
                        Resources = resources,
                        UnprovenFriendAssemblyAuthority = unproven,
                        ParseTicks = parseTicks,
                        ReadTicks = readTicks,
                        MetadataTicks = metadataTicks,
                        DescriptorRetainedBytes = descriptorRetainedBytes,
                        QueueTicks = projectQueueTicks,
                    };
                    TestOnlyAfterProjectPrepared?.Invoke(plan.Name);
                    return preparedProject;
                }
                catch
                {
                    foreach (MetadataReferenceLease lease in metadataLeases) lease.Dispose();
                    reservation.Dispose();
                    throw;
                }
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
                return PreparedProject.Failed(plan, queueTicks: projectQueueTicks);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"Semantic preparation failed for {plan.Name}: {ex.Message}");
                return PreparedProject.Failed(plan, queueTicks: projectQueueTicks);
            }
            finally
            {
                _coldStartRuntime.ProjectSlots.Release();
            }
        }
        finally
        {
            _coldStartRuntime.DescriptorSlots.Release();
        }
    }

    private long EstimateProjectBytes(PlannedProject plan, ParsedProject parsed,
        long projectBytes, long packagesBytes, IReadOnlyList<long> sourceBounds)
    {
        long estimate = SaturatingAdd(PreparedProjectOverheadBytes,
            SaturatingAdd(projectBytes * 2, packagesBytes * 2));
        foreach (long size in sourceBounds)
        {
            long bounded = Math.Clamp(size, 0, DeltaRefresher.MaxIndexedFileBytes);
            estimate = SaturatingAdd(estimate,
                SaturatingAdd(bounded * 3, PerDocumentOverheadBytes));
        }
        foreach ((_, string? hint) in parsed.AssemblyRefs)
        {
            if (hint is null) continue;
            estimate = SaturatingAdd(estimate, MetadataFileSize(Path.Combine(_workspaceRoot,
                hint.Replace('/', Path.DirectorySeparatorChar))));
        }
        foreach ((string package, string version) in parsed.PackageRefs)
        {
            if (ReferenceAssemblyLocator.ResolvePackageDll(package, version) is { } dll)
                estimate = SaturatingAdd(estimate, MetadataFileSize(dll));
        }
        if (parsed.InternalsVisibleTo is { Count: > 0 } friendAssemblies)
        {
            string generated = InternalsVisibleToSource(friendAssemblies);
            estimate = SaturatingAdd(estimate,
                SaturatingAdd((long)generated.Length * sizeof(char), PerDocumentOverheadBytes));
        }
        return Math.Max(PreparedProjectOverheadBytes, estimate);
    }

    private static string InternalsVisibleToSource(IEnumerable<string> friendAssemblies) =>
        string.Join('\n', friendAssemblies.Select(friend =>
            $"[assembly: global::System.Runtime.CompilerServices.InternalsVisibleToAttribute({SyntaxFactory.Literal(friend).Text})]")) + '\n';

    private long FileCaptureBound(string relativePath, long indexedSize, long maximumBytes)
    {
        long boundedIndexed = Math.Clamp(indexedSize, 0, maximumBytes);
        string fullPath = Path.Combine(_workspaceRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            var info = new FileInfo(fullPath);
            return info.Exists
                ? Math.Clamp(Math.Max(boundedIndexed, info.Length), 0,
                    maximumBytes)
                : boundedIndexed;
        }
        catch
        {
            return boundedIndexed;
        }
    }

    private static bool SourceExceedsBound(string fullPath, long admittedBound)
    {
        try
        {
            var info = new FileInfo(fullPath);
            return info.Exists && info.Length > admittedBound;
        }
        catch
        {
            return false;
        }
    }

    private static long MetadataFileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? Math.Max(0, info.Length) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right <= 0) return left;
        return left >= long.MaxValue - right ? long.MaxValue : left + right;
    }
}
