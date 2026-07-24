using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeNav.Portal;

/// <summary>
/// Owns the portal's read-only live view. It tails Phoenix's existing bounded JSONL telemetry
/// files through anchored handles and observes the index file's presence without opening SQLite.
/// The portal never loads Phoenix implementation assemblies and never writes to the workspace.
/// </summary>
public sealed partial class PortalDataSource : BackgroundService
{
    internal const string PortalVersion = "0.3.0-preview";
    private const int MaxWorkspaceCount = 8;
    private const int MaxTelemetryFilesPerWorkspace = 32;
    private const int MaxTelemetryDirectoryEntries = 256;
    private const int MaxInitialBytesPerFile = 4 * 1024 * 1024;
    private const int MaxReadBytesPerWorkspaceRefresh = 8 * 1024 * 1024;
    private const int MaxLineBytes = 256 * 1024;
    private const int MaxStringBytes = 256;
    private const int MaxOperations = 512;
    private const int MaxEvents = 256;
    private const int MaxRetainedOperationBytes = 2 * 1024 * 1024;
    private const int MaxRetainedEventBytes = 512 * 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan OperationRetention = TimeSpan.FromHours(1);
    private static readonly TimeSpan InstanceRetention = TimeSpan.FromMinutes(15);

    private readonly WorkspaceSource[] _sources;
    private readonly int _omittedConfiguredWorkspaces;
    private readonly ILogger<PortalDataSource>? _logger;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private DateTimeOffset _nextFailureLogUtc;
    private PortalView _view = PortalView.Fixture;
    internal Action? BeforeTelemetryEnumerationForTest { get; set; }

    public PortalDataSource()
        : this(DiscoverWorkspaceRoots(), logger: null)
    {
    }

    public PortalDataSource(ILogger<PortalDataSource> logger)
        : this(DiscoverWorkspaceRoots(), logger)
    {
    }

    internal PortalDataSource(
        IEnumerable<string> workspaceRoots,
        ILogger<PortalDataSource>? logger = null)
    {
        _logger = logger;
        WorkspaceSelection selection = SelectWorkspaceRoots(workspaceRoots);
        _omittedConfiguredWorkspaces = selection.OmittedCount;
        _sources = selection.Roots
            .Select(root => new WorkspaceSource(root))
            .ToArray();
    }

    public int WorkspaceCount => _sources.Length;

    public object Bootstrap() => Volatile.Read(ref _view).Bootstrap;

    public object Operations() => OperationsCore(query: null);

    internal object Operations(PortalOperationQuery query) => OperationsCore(query);

    private object OperationsCore(PortalOperationQuery? query)
    {
        PortalView view = Volatile.Read(ref _view);
        return query is null || view.LiveOperations is null
            ? view.Operations
            : BuildOperationPage(
                view.LiveOperations,
                view.DataComplete,
                view.TotalIsLowerBound,
                query,
                view.CursorGeneration);
    }

    public object Events() => EventsCore(query: null);

    internal object Events(PortalEventQuery query) => EventsCore(query);

    private object EventsCore(PortalEventQuery? query)
    {
        PortalView view = Volatile.Read(ref _view);
        return query is null || view.LiveEvents is null
            ? view.Events
            : BuildEventPage(
                view.LiveEvents,
                view.DataComplete,
                view.TotalIsLowerBound,
                query,
                view.CursorGeneration);
    }

    internal void RefreshForTest() => Refresh();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Refresh();
            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void Refresh()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            foreach (WorkspaceSource source in _sources)
                source.Refresh(now, BeforeTelemetryEnumerationForTest);

            WorkspaceSnapshot[] snapshots = _sources
                .Select(source => source.Snapshot(now))
                .Where(snapshot => snapshot.HasLiveData)
                .ToArray();
            Volatile.Write(ref _view, snapshots.Length == 0
                && _omittedConfiguredWorkspaces == 0
                ? PortalView.Fixture
                : BuildLiveView(snapshots, now));
        }
        catch (Exception ex)
        {
            // Retain the last complete immutable view. A transient source replacement must never
            // make the website itself unavailable.
            if (now >= _nextFailureLogUtc)
            {
                _nextFailureLogUtc = now.AddMinutes(1);
                _logger?.LogWarning(
                    ex,
                    "Portal refresh failed; retaining the previous bounded view.");
            }
        }
    }

    private PortalView BuildLiveView(
        IReadOnlyList<WorkspaceSnapshot> snapshots,
        DateTimeOffset now)
    {
        PortalOperation[] allOperations = snapshots
            .SelectMany(snapshot => snapshot.Operations)
            .OrderByDescending(operation => operation.StartedAtUtc)
            .ThenBy(operation => operation.OperationId, StringComparer.Ordinal)
            .ToArray();
        PortalEvent[] allEvents = snapshots
            .SelectMany(snapshot => snapshot.Events)
            .OrderByDescending(item => item.TimestampUtc)
            .ThenBy(item => item.EventId, StringComparer.Ordinal)
            .ToArray();
        PortalOperation[] operations = allOperations.Take(MaxOperations).ToArray();
        PortalEvent[] events = allEvents.Take(MaxEvents).ToArray();
        PortalInstance[] instances = snapshots
            .SelectMany(snapshot => snapshot.Instances)
            .OrderByDescending(instance => instance.LastSeenUtc)
            .ToArray();
        bool globallyTrimmed = allOperations.Length > operations.Length
            || allEvents.Length > events.Length;
        bool dataComplete = snapshots.All(snapshot => snapshot.DataComplete)
            && !globallyTrimmed
            && _omittedConfiguredWorkspaces == 0;
        long droppedRecords = snapshots.Sum(snapshot => snapshot.DroppedRecords);
        long retentionEvictions = snapshots.Sum(snapshot => snapshot.RetentionEvictions)
            + allOperations.Length - operations.Length
            + allEvents.Length - events.Length;
        int truncatedFiles = snapshots.Sum(snapshot => snapshot.TruncatedFiles);
        int tailLimitedFiles = snapshots.Sum(snapshot => snapshot.TailLimitedFiles);
        int invalidRecords = snapshots.Sum(snapshot => snapshot.InvalidRecords);
        int omittedSourceFiles = snapshots.Sum(snapshot => snapshot.OmittedSourceFiles);
        int sourceDiscoveryTruncations =
            snapshots.Sum(snapshot => snapshot.SourceDiscoveryTruncations);
        bool omittedSourceFilesIsLowerBound = sourceDiscoveryTruncations > 0;
        int sourceReadErrors = snapshots.Sum(snapshot => snapshot.SourceReadErrors);
        int ingestionBacklogs = snapshots.Sum(snapshot => snapshot.IngestionBacklogs);
        int sourceFiles = snapshots.Sum(snapshot => snapshot.SourceFiles);
        DateTimeOffset? retainedFromUtc = snapshots
            .Where(snapshot => snapshot.RetainedFromUtc is not null)
            .Select(snapshot => snapshot.RetainedFromUtc)
            .Max();
        if (allOperations.Length > operations.Length && operations.Length > 0)
        {
            DateTimeOffset boundary = operations[^1].StartedAtUtc;
            retainedFromUtc = retainedFromUtc is null || boundary > retainedFromUtc.Value
                ? boundary
                : retainedFromUtc;
        }
        string cursorGeneration = StableId(
            string.Join(
                "|",
                snapshots
                    .OrderBy(snapshot => snapshot.WorkspaceId, StringComparer.Ordinal)
                    .Select(snapshot =>
                        $"{snapshot.WorkspaceId}:{snapshot.RetentionGeneration}"))
            + $"|omitted:{_omittedConfiguredWorkspaces}"
            + (allOperations.Length > operations.Length && operations.Length > 0
                ? $"|operation:{operations[^1].OperationId}"
                : string.Empty)
            + (allEvents.Length > events.Length && events.Length > 0
                ? $"|event:{events[^1].EventId}"
                : string.Empty));
        long[] lags = snapshots
            .Where(snapshot => snapshot.LatestTelemetryUtc is not null)
            .Select(snapshot => Math.Max(
                0,
                (long)(now - snapshot.LatestTelemetryUtc!.Value).TotalMilliseconds))
            .ToArray();
        long? lagMs = lags.Length == 0 ? null : lags.Max();

        object[] workspaces = snapshots.Select(snapshot => new
        {
            workspaceId = snapshot.WorkspaceId,
            name = TrimUtf8(snapshot.Name, MaxStringBytes),
            shortName = ShortName(snapshot.Name),
            state = snapshot.Index.State,
            stateLabel = snapshot.Index.StateLabel,
            indexId = snapshot.Index.IndexId,
            instanceIds = snapshot.Instances.Select(instance => instance.InstanceId).ToArray(),
            description = snapshot.Index.Description
        }).Cast<object>().ToArray();

        object[] indexes = snapshots.Select(snapshot => new
        {
            indexId = snapshot.Index.IndexId,
            workspaceId = snapshot.WorkspaceId,
            state = snapshot.Index.State,
            stateLabel = snapshot.Index.StateLabel,
            freshness = snapshot.Index.Freshness,
            epoch = (long?)null,
            databaseSizeBytes = snapshot.Index.DatabaseSizeBytes,
            indexVersion = snapshot.Index.IndexVersion,
            schemaVersion = snapshot.Index.SchemaVersion,
            indexedCommit = snapshot.Index.IndexedCommit,
            indexedBranch = snapshot.Index.IndexedBranch,
            indexedAtUtc = snapshot.Index.IndexedAtUtc,
            currentBuild = BuildView(snapshot.CurrentBuild),
            refresh = new
            {
                state = snapshot.Index.RefreshState,
                queueDepth = (int?)null,
                changesProcessed = (int?)null,
                lastRefreshDurationMs = (long?)null,
                lastRefreshUtc = snapshot.Index.LastRefreshUtc,
                incompleteReason = snapshot.Index.RefreshIncompleteReason
            },
            counts = new
            {
                files = snapshot.Index.FileCount,
                projects = snapshot.Index.ProjectCount,
                symbols = snapshot.Index.SymbolCount
            }
        }).Cast<object>().ToArray();

        object bootstrap = new
        {
            apiVersion = 1,
            dataSource = "live",
            portal = new
            {
                sessionId = _sessionId,
                version = PortalVersion,
                startedAtUtc = _startedAtUtc,
                nowUtc = now
            },
            summary = new
            {
                workspaceCount = snapshots.Count,
                indexCount = snapshots.Count(snapshot => snapshot.Index.Exists),
                connectedInstanceCount =
                    instances.Count(instance => instance.ConnectionState == "connected"),
                staleInstanceCount =
                    instances.Count(instance => instance.ConnectionState != "connected"),
                activeOperationCount = 0,
                warningCount = events.Count(item => item.Severity is "warning" or "error")
            },
            telemetry = new
            {
                source = "workspace_jsonl",
                sourceFiles,
                lagMs,
                droppedRecords,
                truncatedFiles,
                tailLimitedFiles,
                invalidRecords,
                omittedSourceFiles,
                omittedSourceFilesIsLowerBound,
                sourceDiscoveryTruncations,
                omittedConfiguredWorkspaces = _omittedConfiguredWorkspaces,
                configuredWorkspaceListTruncated = _omittedConfiguredWorkspaces > 0,
                sourceReadErrors,
                ingestionBacklogs,
                retentionEvictions,
                retainedFromUtc,
                responseByteLimit = MaxPageResponseBytes,
                retention = "up_to_7_days_on_disk; 1_hour_in_portal"
            },
            workspaces,
            indexes,
            instances,
            activeOperations = Array.Empty<object>(),
            cursor = now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            dataComplete
        };

        bool totalIsLowerBound = !dataComplete;
        object operationPage = BuildOperationPage(
            operations,
            dataComplete,
            totalIsLowerBound,
            PortalOperationQuery.Default,
            cursorGeneration);
        object eventPage = BuildEventPage(
            events,
            dataComplete,
            totalIsLowerBound,
            PortalEventQuery.Default,
            cursorGeneration);
        return new PortalView(
            bootstrap,
            operationPage,
            eventPage,
            operations,
            events,
            dataComplete,
            totalIsLowerBound,
            cursorGeneration);
    }

    private static object? BuildView(PortalBuild? build)
    {
        if (build is null)
            return null;
        double? progress = build.FilesTotal is > 0
            ? Math.Clamp(build.FilesProcessed / (double)build.FilesTotal.Value, 0, 1)
            : null;
        return new
        {
            build.BuildId,
            build.InstanceId,
            build.State,
            build.Phase,
            phaseLabel = build.Phase.Replace('_', ' '),
            startedAtUtc = SubtractMilliseconds(build.TimestampUtc, build.ElapsedMs),
            build.TimestampUtc,
            build.ElapsedMs,
            build.PhaseElapsedMs,
            build.FilesProcessed,
            build.FilesTotal,
            progress,
            throughputPerSecond = build.FilesPerSecond,
            etaSeconds = build.EstimatedRemainingMs is null
                ? (double?)null
                : build.EstimatedRemainingMs.Value / 1000d,
            build.FilesSkipped,
            build.ProjectsFailed,
            build.SymbolsWritten,
            build.BytesRead,
            live = build.IsProcessAlive
        };
    }

    private static IEnumerable<string> DiscoverWorkspaceRoots()
    {
        string? configured = Environment.GetEnvironmentVariable("PHOENIX_PORTAL_WORKSPACES");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return EnumerateConfigured(configured);
        }

        DirectoryInfo? current = new(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".codenav"))
                || Directory.Exists(Path.Combine(current.FullName, ".git"))
                || File.Exists(Path.Combine(current.FullName, ".git")))
            {
                return [current.FullName];
            }
            current = current.Parent;
        }

        return [Directory.GetCurrentDirectory()];

        static IEnumerable<string> EnumerateConfigured(string value)
        {
            int start = 0;
            while (start <= value.Length)
            {
                int separator = value.IndexOf(Path.PathSeparator, start);
                int end = separator < 0 ? value.Length : separator;
                string candidate = value[start..end].Trim();
                if (candidate.Length > 0)
                    yield return candidate;
                if (separator < 0)
                    yield break;
                start = separator + 1;
            }
        }
    }

    private static WorkspaceSelection SelectWorkspaceRoots(IEnumerable<string> workspaceRoots)
    {
        var roots = new List<string>(MaxWorkspaceCount);
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        int omitted = 0;
        int inspected = 0;
        using IEnumerator<string> iterator = workspaceRoots.GetEnumerator();
        while (inspected <= MaxWorkspaceCount && iterator.MoveNext())
        {
            inspected++;
            string root;
            try
            {
                root = Path.GetFullPath(iterator.Current);
            }
            catch
            {
                omitted++;
                continue;
            }

            if (!seen.Add(root))
                continue;
            if (!Directory.Exists(root))
            {
                omitted++;
                continue;
            }
            if (roots.Count < MaxWorkspaceCount)
                roots.Add(root);
            else
                omitted++;
        }

        // Stop reading caller input after the first overflow element. The exact number of
        // additional configured roots is intentionally a lower bound; constructor work stays
        // bounded even for a hostile or accidentally infinite enumerable.
        if (inspected > MaxWorkspaceCount)
            omitted = Math.Max(1, omitted);

        return new WorkspaceSelection(roots.ToArray(), omitted);
    }

    private static string ShortName(string name)
    {
        string letters = new(name.Where(char.IsLetterOrDigit).Take(3).ToArray());
        return letters.Length == 0 ? "WS" : letters.ToUpperInvariant();
    }

    private sealed record PortalView(
        object Bootstrap,
        object Operations,
        object Events,
        PortalOperation[]? LiveOperations,
        PortalEvent[]? LiveEvents,
        bool DataComplete,
        bool TotalIsLowerBound,
        string CursorGeneration)
    {
        internal static PortalView Fixture { get; } = new(
            PortalFixtures.Bootstrap(),
            PortalFixtures.Operations(),
            PortalFixtures.Events(),
            null,
            null,
            true,
            false,
            "fixture");
    }

    private sealed record WorkspaceSelection(string[] Roots, int OmittedCount);

    private sealed class WorkspaceSource
    {
        private readonly string _root;
        private readonly string _workspaceId;
        private readonly string _indexId;
        private readonly Dictionary<string, FileCursor> _files =
            new(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        private readonly Dictionary<string, PortalOperation> _operations =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, PortalEvent> _events =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, MutableInstance> _instances =
            new(StringComparer.Ordinal);
        private IndexSnapshot _index;
        private IndexFileStamp? _lastIndexStamp;
        private DateTimeOffset _nextIndexProbeUtc;
        private bool _telemetryDirectoryPresent;
        private int _omittedSourceFiles;
        private bool _sourceDiscoveryTruncated;
        private bool _telemetryReadError;
        private bool _ingestionBacklog;
        private long _retentionGeneration;
        private long _sourceLossEvictions;
        private DateTimeOffset? _sourceLossBoundaryUtc;
        private PortalBuild? _currentBuild;

        internal WorkspaceSource(string root)
        {
            _root = root;
            string identity = StableId(root);
            _workspaceId = $"ws-{identity}";
            _indexId = $"idx-{identity}";
            _index = IndexSnapshot.Missing(_indexId);
        }

        internal void Refresh(DateTimeOffset now, Action? beforeTelemetryEnumeration)
        {
            RefreshTelemetry(now, beforeTelemetryEnumeration);
            RefreshIndex(now);
            Prune(now);
        }

        internal WorkspaceSnapshot Snapshot(DateTimeOffset now)
        {
            FileCursor[] cursors = _files.Values.ToArray();
            DateTimeOffset? latest = cursors
                .Where(cursor => cursor.LastWriteUtc is not null)
                .Select(cursor => cursor.LastWriteUtc)
                .Max();
            PortalInstance[] instances = _instances.Values
                .Where(instance => instance.IsProcessAlive()
                    || now - instance.LastSeenUtc <= InstanceRetention)
                .Select(instance => instance.ToSnapshot())
                .ToArray();
            DateTimeOffset? retainedFromUtc = cursors
                .Where(cursor => cursor.RetainedFromUtc is not null)
                .Select(cursor => cursor.RetainedFromUtc)
                .Max();
            if (_sourceLossBoundaryUtc is { } sourceLossBoundary
                && (retainedFromUtc is null || sourceLossBoundary > retainedFromUtc.Value))
            {
                retainedFromUtc = sourceLossBoundary;
            }
            return new WorkspaceSnapshot(
                _workspaceId,
                new DirectoryInfo(_root).Name,
                _index,
                _index.Exists
                    || cursors.Length > 0
                    || _telemetryDirectoryPresent
                    || _omittedSourceFiles > 0
                    || _sourceDiscoveryTruncated
                    || _telemetryReadError
                    || _ingestionBacklog
                    || _sourceLossEvictions > 0,
                instances,
                _operations.Values.ToArray(),
                _events.Values.ToArray(),
                _telemetryDirectoryPresent
                    && cursors.Length > 0
                    && cursors.All(cursor => !cursor.Truncated
                    && !cursor.TailLimited
                    && !cursor.ReadError
                    && cursor.DroppedRecords == 0
                    && cursor.InvalidLines == 0
                    && cursor.RetentionEvictions == 0)
                    && _omittedSourceFiles == 0
                    && !_sourceDiscoveryTruncated
                    && !_telemetryReadError
                    && !_ingestionBacklog
                    && _sourceLossEvictions == 0
                    && _index.ObservationComplete,
                cursors.Sum(cursor => cursor.DroppedRecords),
                cursors.Sum(cursor => cursor.RetentionEvictions) + _sourceLossEvictions,
                cursors.Count(cursor => cursor.Truncated),
                cursors.Count(cursor => cursor.TailLimited),
                cursors.Sum(cursor => cursor.InvalidLines),
                _omittedSourceFiles,
                _sourceDiscoveryTruncated ? 1 : 0,
                cursors.Count(cursor => cursor.ReadError)
                    + (_telemetryReadError ? 1 : 0),
                _ingestionBacklog ? 1 : 0,
                cursors.Length,
                latest,
                _retentionGeneration,
                retainedFromUtc,
                _currentBuild is { } build
                    ? build with
                    {
                        IsProcessAlive = _instances.TryGetValue(
                            build.InstanceId,
                            out MutableInstance? owner)
                            && owner.IsProcessAlive()
                    }
                    : null);
        }

        private void RefreshIndex(DateTimeOffset now)
        {
            if (now < _nextIndexProbeUtc)
                return;
            _nextIndexProbeUtc = now.AddSeconds(2);

            IndexFileStamp stamp = IndexFileStamp.Read(_root);
            if (_lastIndexStamp == stamp && _index.ObservationComplete)
                return;
            _lastIndexStamp = stamp;
            _index = ReadIndexSnapshot(_root, _indexId);
        }

        private void RefreshTelemetry(
            DateTimeOffset now,
            Action? beforeTelemetryEnumeration)
        {
            PortalPathGuard.EntryState directoryState =
                PortalPathGuard.OpenDirectory(
                    _root,
                    Path.Combine(".codenav", "telemetry"),
                    out PortalRegularFile? directory);
            if (directoryState == PortalPathGuard.EntryState.Missing)
            {
                _telemetryDirectoryPresent = false;
                PurgeAllTelemetrySources(now);
                return;
            }
            if (directoryState != PortalPathGuard.EntryState.Safe || directory is null)
            {
                _telemetryDirectoryPresent = true;
                _telemetryReadError = true;
                return;
            }

            using (directory)
            {
                _telemetryDirectoryPresent = true;
                string[] files;
                _telemetryReadError = false;
                _ingestionBacklog = false;
                beforeTelemetryEnumeration?.Invoke();
                if (!PortalPathGuard.TryEnumerateDirectoryNames(
                        directory,
                        MaxTelemetryDirectoryEntries + 1,
                        out string[] scanned))
                {
                    _telemetryReadError = true;
                    return;
                }
                string[] candidates = scanned
                    .Take(MaxTelemetryDirectoryEntries)
                    .Where(name =>
                        name.StartsWith("phoenix-", StringComparison.Ordinal)
                        && name.EndsWith(".jsonl", StringComparison.Ordinal))
                    .OrderByDescending(name => name, StringComparer.Ordinal)
                    .ToArray();
                _sourceDiscoveryTruncated =
                    scanned.Length > MaxTelemetryDirectoryEntries;
                _omittedSourceFiles = Math.Max(
                    0,
                    candidates.Length - MaxTelemetryFilesPerWorkspace);
                files = candidates.Take(MaxTelemetryFilesPerWorkspace).ToArray();

                var retained = new HashSet<string>(
                    files.Select(file =>
                        Path.Combine(_root, ".codenav", "telemetry", file)),
                    _files.Comparer);
                foreach (string stale in _files.Keys.Where(path => !retained.Contains(path)).ToArray())
                {
                    FileCursor cursor = _files[stale];
                    foreach (string key in _operations.Values
                                 .Where(operation => operation.InstanceId == cursor.InstanceId)
                                 .Select(operation => operation.OperationId)
                                 .ToArray())
                    {
                        _operations.Remove(key);
                    }
                    foreach (string key in _events.Values
                                 .Where(item => item.InstanceId == cursor.InstanceId)
                                 .Select(item => item.EventId)
                                 .ToArray())
                    {
                        _events.Remove(key);
                    }
                    _instances.Remove(cursor.InstanceId);
                    _files.Remove(stale);
                    RecordSourceLoss(now);
                }
                int remainingBytes = MaxReadBytesPerWorkspaceRefresh;
                foreach (string file in files.OrderBy(file => file, StringComparer.Ordinal))
                {
                    string fullPath = Path.Combine(_root, ".codenav", "telemetry", file);
                    if (!_files.TryGetValue(fullPath, out FileCursor? cursor))
                    {
                        cursor = new FileCursor(
                            _root,
                            Path.Combine(".codenav", "telemetry", file));
                        _files.Add(fullPath, cursor);
                    }

                    FileReadOutcome outcome = cursor.ReadNewLines(
                        remainingBytes,
                        () =>
                        {
                            PurgeInstance(cursor.InstanceId);
                            RecordSourceLoss(now);
                        },
                        (line, ordinal) => ConsumeLine(cursor, line, ordinal, now));
                    remainingBytes = Math.Max(0, remainingBytes - outcome.BytesRead);
                    _ingestionBacklog |= outcome.BacklogRemaining;
                }
            }
        }

        private void PurgeAllTelemetrySources(DateTimeOffset now)
        {
            if (_files.Count > 0 || _operations.Count > 0 || _events.Count > 0)
                RecordSourceLoss(now);
            _files.Clear();
            _operations.Clear();
            _events.Clear();
            _instances.Clear();
            _currentBuild = null;
            _telemetryDirectoryPresent = false;
            _omittedSourceFiles = 0;
            _sourceDiscoveryTruncated = false;
            _telemetryReadError = false;
            _ingestionBacklog = false;
        }

        private void RecordSourceLoss(DateTimeOffset now)
        {
            _sourceLossEvictions++;
            if (_sourceLossBoundaryUtc is null || now > _sourceLossBoundaryUtc.Value)
                _sourceLossBoundaryUtc = now;
            _retentionGeneration++;
        }

        private void PurgeInstance(string instanceId)
        {
            foreach (string key in _operations.Values
                         .Where(operation => operation.InstanceId == instanceId)
                         .Select(operation => operation.OperationId)
                         .ToArray())
            {
                _operations.Remove(key);
            }
            foreach (string key in _events.Values
                         .Where(item => item.InstanceId == instanceId)
                         .Select(item => item.EventId)
                         .ToArray())
            {
                _events.Remove(key);
            }
            _instances.Remove(instanceId);
            if (_currentBuild?.InstanceId == instanceId)
                _currentBuild = null;
        }

        private void ConsumeLine(
            FileCursor cursor,
            string line,
            long ordinal,
            DateTimeOffset now)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                cursor.InvalidLines++;
                return;
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (!StringsFitBudget(root, MaxStringBytes))
                {
                    cursor.InvalidLines++;
                    return;
                }
                string? recordType = String(root, "e");
                if (recordType == "semanticOp")
                {
                    PortalOperation? operation = NormalizeOperation(
                        root,
                        cursor.InstanceId,
                        _workspaceId);
                    if (operation is null)
                    {
                        cursor.InvalidLines++;
                        return;
                    }
                    AddOperation(operation);
                    MutableInstance instance = GetInstance(cursor);
                    instance.LastSeenUtc =
                        operation.StartedAtUtc.AddMilliseconds(operation.DurationMs ?? 0);
                    instance.Role = String(root, "accessMode") ?? instance.Role;
                    if (Bool(root, "cold") == true)
                        instance.SemanticState = "warming";
                    return;
                }

                if (recordType == "serverInfo")
                {
                    MutableInstance instance = GetInstance(cursor);
                    if (!instance.ApplyServerInfo(root, now))
                        cursor.InvalidLines++;
                    return;
                }

                if (recordType == "buildProgress")
                {
                    PortalBuild? build = NormalizeBuild(root, cursor.InstanceId, now);
                    if (build is null)
                    {
                        cursor.InvalidLines++;
                        return;
                    }
                    MutableInstance instance = GetInstance(cursor);
                    instance.LastSeenUtc = build.TimestampUtc;
                    instance.Role = String(root, "accessMode") ?? instance.Role;
                    build = build with { IsProcessAlive = instance.IsProcessAlive() };
                    if (build.State is "started" or "running")
                    {
                        _currentBuild = build;
                    }
                    else
                    {
                        if (_currentBuild?.BuildId == build.BuildId)
                            _currentBuild = null;
                        AddEvent(BuildTerminalEvent(build, _workspaceId, _indexId));
                    }
                    return;
                }

                if (recordType == "telemetry_dropped")
                {
                    long count = Long(root, "count") ?? 0;
                    cursor.DroppedRecords += Math.Max(0, count);
                    AddEvent(new PortalEvent(
                        $"event-{cursor.InstanceId}-{ordinal}",
                        _workspaceId,
                        _indexId,
                        cursor.InstanceId,
                        cursor.LastWriteUtc ?? now,
                        "warning",
                        "telemetry",
                        "telemetry.dropped",
                        $"{count} telemetry records were dropped before persistence.",
                        null));
                    return;
                }

                if (recordType == "telemetry_truncated")
                {
                    cursor.Truncated = true;
                    AddEvent(new PortalEvent(
                        $"event-{cursor.InstanceId}-{ordinal}",
                        _workspaceId,
                        _indexId,
                        cursor.InstanceId,
                        cursor.LastWriteUtc ?? now,
                        "warning",
                        "telemetry",
                        "telemetry.truncated",
                        "The telemetry file reached its bounded cap; later records are unavailable.",
                        null));
                }
            }
        }

        private MutableInstance GetInstance(FileCursor cursor)
        {
            if (_instances.TryGetValue(cursor.InstanceId, out MutableInstance? instance))
                return instance;
            instance = new MutableInstance(
                cursor.InstanceId,
                _workspaceId,
                _indexId,
                cursor.ProcessId,
                cursor.ProcessStartedUtc,
                cursor.LastWriteUtc ?? DateTimeOffset.UtcNow);
            _instances.Add(cursor.InstanceId, instance);
            return instance;
        }

        private void AddEvent(PortalEvent item)
        {
            _events[item.EventId] = item;
            int retainedBytes = _events.Values.Sum(EstimateBytes);
            if (_events.Count <= MaxEvents && retainedBytes <= MaxRetainedEventBytes)
                return;
            foreach (PortalEvent value in _events.Values
                         .OrderByDescending(value => value.TimestampUtc)
                         .ThenBy(value => value.EventId, StringComparer.Ordinal)
                         .Reverse()
                         .ToArray())
            {
                if (_events.Count <= MaxEvents && retainedBytes <= MaxRetainedEventBytes)
                    break;
                if (_events.Remove(value.EventId))
                {
                    retainedBytes -= EstimateBytes(value);
                    RecordRetentionEviction(
                        value.InstanceId,
                        value.TimestampUtc,
                        operation: false);
                }
            }
        }

        private void AddOperation(PortalOperation operation)
        {
            _operations[operation.OperationId] = operation;
            int retainedBytes = _operations.Values.Sum(EstimateBytes);
            if (_operations.Count <= MaxOperations
                && retainedBytes <= MaxRetainedOperationBytes)
            {
                return;
            }

            foreach (PortalOperation value in _operations.Values
                         .OrderByDescending(value => value.StartedAtUtc)
                         .ThenBy(value => value.OperationId, StringComparer.Ordinal)
                         .Reverse()
                         .ToArray())
            {
                if (_operations.Count <= MaxOperations
                    && retainedBytes <= MaxRetainedOperationBytes)
                {
                    break;
                }
                if (_operations.Remove(value.OperationId))
                {
                    retainedBytes -= EstimateBytes(value);
                    RecordRetentionEviction(
                        value.InstanceId,
                        value.StartedAtUtc,
                        operation: true);
                }
            }
        }

        private void RecordRetentionEviction(
            string instanceId,
            DateTimeOffset timestampUtc,
            bool operation)
        {
            FileCursor? cursor = _files.Values.FirstOrDefault(
                candidate => candidate.InstanceId == instanceId);
            if (cursor is not null)
            {
                cursor.RetentionEvictions++;
                if (operation
                    && (cursor.RetainedFromUtc is null
                        || timestampUtc > cursor.RetainedFromUtc.Value))
                {
                    cursor.RetainedFromUtc = timestampUtc;
                }
            }
            _retentionGeneration++;
        }

        private void Prune(DateTimeOffset now)
        {
            foreach (PortalOperation operation in _operations.Values
                         .Where(operation => now - operation.StartedAtUtc > OperationRetention)
                         .ToArray())
            {
                if (_operations.Remove(operation.OperationId))
                {
                    RecordRetentionEviction(
                        operation.InstanceId,
                        now - OperationRetention,
                        operation: true);
                }
            }

            var retainedInstances = new HashSet<string>(
                _files.Values.Select(file => file.InstanceId),
                StringComparer.Ordinal);
            foreach (string key in _instances
                         .Where(pair => !retainedInstances.Contains(pair.Key)
                             && now - pair.Value.LastSeenUtc > InstanceRetention)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _instances.Remove(key);
            }
        }
    }

    private sealed class FileCursor
    {
        private readonly string _workspaceRoot;
        private readonly List<byte> _pending = new();
        private bool _initialized;
        private bool _discardPrefix;
        private long _offset;
        private PortalFileIdentity? _identity;

        internal FileCursor(string workspaceRoot, string relativePath)
        {
            _workspaceRoot = workspaceRoot;
            RelativePath = relativePath;
            (ProcessId, ProcessStartedUtc, InstanceId) = ParseIdentity(relativePath);
        }

        internal string RelativePath { get; }
        internal int ProcessId { get; }
        internal DateTimeOffset? ProcessStartedUtc { get; }
        internal string InstanceId { get; }
        internal DateTimeOffset? LastWriteUtc { get; private set; }
        internal long DroppedRecords { get; set; }
        internal bool Truncated { get; set; }
        internal bool TailLimited { get; private set; }
        internal int InvalidLines { get; set; }
        internal bool ReadError { get; private set; }
        internal long RetentionEvictions { get; set; }
        internal DateTimeOffset? RetainedFromUtc { get; set; }

        internal FileReadOutcome ReadNewLines(
            int byteBudget,
            Action reset,
            Action<string, long> consume)
        {
            PortalPathGuard.EntryState state =
                PortalPathGuard.OpenRegularFile(
                    _workspaceRoot,
                    RelativePath,
                    out PortalRegularFile? opened);
            if (state != PortalPathGuard.EntryState.Safe || opened is null)
            {
                ReadError = true;
                return new FileReadOutcome(0, false, false);
            }

            using (opened)
            {
                PortalFileMetadata metadata = opened.Metadata;
                bool replaced = _initialized
                    && (_identity != metadata.Identity
                        || metadata.Length < _offset
                        || (metadata.Length == _offset
                            && LastWriteUtc is not null
                            && metadata.LastWriteUtc != LastWriteUtc.Value));
                if (!_initialized || replaced)
                {
                    if (replaced)
                        reset();
                    _pending.Clear();
                    _offset = Math.Max(0, metadata.Length - MaxInitialBytesPerFile);
                    _discardPrefix = _offset > 0;
                    TailLimited = _offset > 0;
                    _initialized = true;
                    _identity = metadata.Identity;
                    DroppedRecords = 0;
                    Truncated = false;
                    InvalidLines = 0;
                    RetentionEvictions = 0;
                    RetainedFromUtc = null;
                }
                if (metadata.Length == _offset)
                {
                    LastWriteUtc = metadata.LastWriteUtc;
                    ReadError = false;
                    return new FileReadOutcome(0, true, false);
                }
                if (byteBudget <= 0)
                    return new FileReadOutcome(0, true, true);

                long lineOrdinal = Math.Max(0, _offset - _pending.Count);
                int bytesRead = 0;
                try
                {
                    FileStream stream = opened.Stream;
                    stream.Position = _offset;
                    byte[] buffer = new byte[64 * 1024];
                    int read;
                    while (bytesRead < byteBudget
                        && _offset < metadata.Length
                        && (read = stream.Read(
                            buffer,
                            0,
                            (int)Math.Min(
                                Math.Min(buffer.Length, byteBudget - bytesRead),
                                metadata.Length - _offset))) > 0)
                    {
                        for (int i = 0; i < read; i++)
                        {
                            byte value = buffer[i];
                            if (_discardPrefix)
                            {
                                if (value == (byte)'\n')
                                {
                                    _discardPrefix = false;
                                    lineOrdinal = _offset + i + 1;
                                }
                                continue;
                            }

                            if (value == (byte)'\n')
                            {
                                if (_pending.Count > 0 && _pending[^1] == (byte)'\r')
                                    _pending.RemoveAt(_pending.Count - 1);
                                if (_pending.Count > 0)
                                    consume(
                                        Encoding.UTF8.GetString(_pending.ToArray()),
                                        lineOrdinal);
                                _pending.Clear();
                                lineOrdinal = _offset + i + 1;
                                continue;
                            }

                            if (_pending.Count < MaxLineBytes)
                                _pending.Add(value);
                            else
                            {
                                _pending.Clear();
                                InvalidLines++;
                                _discardPrefix = true;
                            }
                        }
                        _offset += read;
                        bytesRead += read;
                        lineOrdinal = Math.Max(lineOrdinal, _offset - _pending.Count);
                    }
                    LastWriteUtc = metadata.LastWriteUtc;
                    ReadError = false;
                    return new FileReadOutcome(
                        bytesRead,
                        true,
                        metadata.Length > _offset);
                }
                catch (IOException)
                {
                    ReadError = true;
                }
                catch (UnauthorizedAccessException)
                {
                    ReadError = true;
                }
                return new FileReadOutcome(
                    bytesRead,
                    false,
                    metadata.Length > _offset);
            }
        }

        private static (int ProcessId, DateTimeOffset? StartedUtc, string InstanceId)
            ParseIdentity(string path)
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(path);
            string[] parts = stem.Split('-', StringSplitOptions.RemoveEmptyEntries);
            int pid = parts.Length >= 4
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : 0;
            DateTimeOffset? started = parts.Length >= 4
                && DateTimeOffset.TryParseExact(
                    parts[2],
                    "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset parsedStart)
                ? parsedStart
                : null;
            return (pid, started, $"instance-{StableId(stem)}");
        }
    }

    private readonly record struct FileReadOutcome(
        int BytesRead,
        bool Success,
        bool BacklogRemaining);

    private sealed class MutableInstance
    {
        internal MutableInstance(
            string instanceId,
            string workspaceId,
            string indexId,
            int processId,
            DateTimeOffset? processStartedUtc,
            DateTimeOffset lastSeenUtc)
        {
            InstanceId = instanceId;
            WorkspaceId = workspaceId;
            IndexId = indexId;
            ProcessId = processId;
            ProcessStartedUtc = processStartedUtc;
            LastSeenUtc = lastSeenUtc;
        }

        internal string InstanceId { get; }
        internal string WorkspaceId { get; }
        internal string IndexId { get; }
        internal int ProcessId { get; }
        internal DateTimeOffset? ProcessStartedUtc { get; }
        internal DateTimeOffset LastSeenUtc { get; set; }
        internal string Role { get; set; } = "unknown";
        internal string SemanticState { get; set; } = "unknown";
        internal string Version { get; private set; } = "unknown";
        internal string? BuildStamp { get; private set; }
        internal string? SchemaVersion { get; private set; }
        internal string Platform { get; private set; } = "unknown";
        internal string[] FeatureIds { get; private set; } = [];
        internal int FeatureCount { get; private set; }
        internal bool FeatureIdsTruncated { get; private set; }

        internal bool ApplyServerInfo(JsonElement root, DateTimeOffset now)
        {
            string? version = String(root, "version");
            string? timestamp = String(root, "ts");
            if (version is null
                || !DateTimeOffset.TryParse(
                    timestamp,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset seen))
            {
                return false;
            }

            int featureCount = (int)Math.Clamp(
                Long(root, "featureCount") ?? 0,
                0,
                100_000);
            string[] providedFeatureIds =
                root.TryGetProperty("featureIds", out JsonElement values)
                && values.ValueKind == JsonValueKind.Array
                    ? values.EnumerateArray()
                        .Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => value.GetString())
                        .OfType<string>()
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .ToArray()
                    : [];
            if (providedFeatureIds.Any(value => Encoding.UTF8.GetByteCount(value) > 96))
                return false;
            string[] featureIds = providedFeatureIds
                .Distinct(StringComparer.Ordinal)
                .Take(128)
                .ToArray();
            Version = version;
            BuildStamp = String(root, "buildStamp");
            SchemaVersion = String(root, "schemaVersion");
            Platform = String(root, "platform") ?? "unknown";
            Role = String(root, "accessMode") ?? Role;
            FeatureCount = featureCount;
            FeatureIds = featureIds;
            FeatureIdsTruncated = FeatureCount > FeatureIds.Length;
            LastSeenUtc = seen > now.AddMinutes(5) ? now : seen;
            return true;
        }

        internal bool IsProcessAlive()
        {
            if (ProcessId <= 0)
                return false;
            try
            {
                using Process process = Process.GetProcessById(ProcessId);
                if (process.HasExited)
                    return false;
                if (ProcessStartedUtc is null)
                    return true;
                DateTimeOffset actualStart = process.StartTime.ToUniversalTime();
                return Math.Abs((actualStart - ProcessStartedUtc.Value).TotalMinutes) < 1;
            }
            catch
            {
                return false;
            }
        }

        internal PortalInstance ToSnapshot()
        {
            bool alive = IsProcessAlive();
            return new PortalInstance(
                InstanceId,
                WorkspaceId,
                IndexId,
                ProcessId > 0 ? $"Phoenix · PID {ProcessId}" : "Phoenix process",
                Role,
                alive ? "connected" : "stale",
                SemanticState,
                Version,
                BuildStamp,
                SchemaVersion,
                Platform,
                FeatureIds,
                FeatureCount,
                FeatureIdsTruncated,
                ProcessId,
                ProcessStartedUtc,
                LastSeenUtc);
        }
    }

    private static PortalOperation? NormalizeOperation(
        JsonElement root,
        string instanceId,
        string workspaceId)
    {
        string? correlationId = String(root, "corr");
        string? tool = String(root, "tool");
        string? result = String(root, "result");
        if (correlationId is null || tool is null || result is null)
            return null;

        if (!DateTimeOffset.TryParse(
                String(root, "ts"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset emittedAtUtc))
        {
            return null;
        }
        JsonElement? owner = Object(root, "ownerLoad");
        JsonElement? scan = Object(root, "scanLoad");
        if (!TryOptionalNonNegativeNumber(root, "clusterLoadMs", out double? cluster)
            || !TryOptionalNonNegativeNumber(root, "queryMs", out double? query))
        {
            return null;
        }
        if (!TrySumOptionalNonNegativeNumber(
                owner,
                scan,
                "gateWaitMs",
                out double? gateWait)
            || !TrySumOptionalNonNegativeNumber(
                owner,
                scan,
                "fingerprintMs",
                out double? fingerprint)
            || !TrySumOptionalNonNegativeNumber(
                owner,
                scan,
                "topoMs",
                out double? topology)
            || !TrySumOptionalNonNegativeNumber(
                owner,
                scan,
                "projectLoadMs",
                out double? projectLoad)
            || !TrySumOptionalNonNegativeLong(
                owner,
                scan,
                "requested",
                out long? requested)
            || !TrySumOptionalNonNegativeLong(
                owner,
                scan,
                "loaded",
                out long? loaded)
            || !TrySumOptionalNonNegativeLong(
                owner,
                scan,
                "reloaded",
                out long? reloaded)
            || !TrySumOptionalNonNegativeLong(
                owner,
                scan,
                "failed",
                out long? failed))
        {
            return null;
        }
        double totalDuration = (cluster ?? 0) + (query ?? 0);
        if (!double.IsFinite(totalDuration)
            || totalDuration > long.MaxValue)
        {
            return null;
        }
        long? duration = cluster is null && query is null
            ? null
            : (long)Math.Round(totalDuration);
        if (!TrySubtractMilliseconds(
                emittedAtUtc,
                duration ?? 0,
                out DateTimeOffset startedAtUtc))
        {
            return null;
        }
        string? reason = String(root, "reason");
        (string outcome, string confidence, bool partial) = result switch
        {
            "exact" => ("completed", "exact", false),
            "partial" => ("completed", "unknown", true),
            "degraded" => ("degraded", "unknown", true),
            "unresolved" => ("failed", "unknown", false),
            "error" => ("failed", "unknown", false),
            _ => ("failed", "unknown", true)
        };
        string summary = result switch
        {
            "exact" => "Compiler-exact semantic result",
            "partial" => $"Partial semantic result · {FormatReason(reason)}",
            "degraded" => $"Degraded · {FormatReason(reason)}",
            "unresolved" => $"Unresolved · {FormatReason(reason)}",
            _ => $"Error · {FormatReason(reason)}"
        };

        return new PortalOperation(
            $"{instanceId}-{StableId(correlationId)}",
            correlationId,
            workspaceId,
            instanceId,
            tool,
            "semantic",
            startedAtUtc,
            duration,
            "complete",
            outcome,
            confidence,
            partial,
            Bool(root, "cold") == true ? "cold" : "unknown",
            reason,
            summary,
            new OperationTimings(
                gateWait,
                fingerprint,
                topology,
                projectLoad),
            new OperationCounts(
                requested,
                loaded,
                reloaded,
                failed));
    }

    private static PortalBuild? NormalizeBuild(
        JsonElement root,
        string instanceId,
        DateTimeOffset now)
    {
        string? buildId = String(root, "buildId");
        string? state = String(root, "state");
        string? phase = String(root, "phase");
        if (buildId is null
            || state is not ("started" or "running" or "completed" or "failed" or "cancelled")
            || phase is null
            || !DateTimeOffset.TryParse(
                String(root, "ts"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset timestampUtc))
        {
            return null;
        }

        if (timestampUtc > now.AddMinutes(5))
            timestampUtc = now;
        if (!TryOptionalNonNegativeLong(root, "phaseElapsedMs", out long? phaseElapsed)
            || !TryOptionalNonNegativeLong(root, "elapsedMs", out long? elapsed)
            || !TryOptionalNonNegativeLong(root, "filesDone", out long? filesDone)
            || !TryOptionalNonNegativeLong(root, "filesTotal", out long? filesTotal)
            || !TryOptionalNonNegativeLong(root, "filesSkipped", out long? filesSkipped)
            || !TryOptionalNonNegativeLong(root, "projectsFailed", out long? projectsFailed)
            || !TryOptionalNonNegativeLong(root, "symbolsWritten", out long? symbolsWritten)
            || !TryOptionalNonNegativeLong(root, "bytesRead", out long? bytesRead)
            || !TryOptionalNonNegativeLong(
                root,
                "estimatedRemainingMs",
                out long? estimatedRemaining)
            || !TryOptionalNonNegativeNumber(
                root,
                "filesPerSecond",
                out double? filesPerSecond)
            || !TrySubtractMilliseconds(timestampUtc, elapsed ?? 0, out _))
        {
            return null;
        }
        return new PortalBuild(
            buildId,
            instanceId,
            state,
            String(root, "reason"),
            phase,
            timestampUtc,
            phaseElapsed ?? 0,
            elapsed ?? 0,
            filesDone ?? 0,
            filesTotal,
            filesSkipped ?? 0,
            projectsFailed ?? 0,
            symbolsWritten ?? 0,
            bytesRead ?? 0,
            filesPerSecond,
            estimatedRemaining,
            IsProcessAlive: false);
    }

    private static PortalEvent BuildTerminalEvent(
        PortalBuild build,
        string workspaceId,
        string indexId)
    {
        string severity = build.State == "failed" ? "error" : "info";
        return new PortalEvent(
            $"event-{build.InstanceId}-{StableId(build.BuildId + build.State)}",
            workspaceId,
            indexId,
            build.InstanceId,
            build.TimestampUtc,
            severity,
            "index",
            $"index.build_{build.State}",
            $"Full index build {build.State} after {build.ElapsedMs} ms.",
            build.BuildId);
    }

    private static IndexSnapshot ReadIndexSnapshot(string root, string indexId)
    {
        PortalPathGuard.EntryState state =
            PortalPathGuard.OpenRegularFile(
                root,
                Path.Combine(".codenav", "index.db"),
                out PortalRegularFile? database);
        if (state == PortalPathGuard.EntryState.Missing)
            return IndexSnapshot.Missing(indexId);
        if (state != PortalPathGuard.EntryState.Safe || database is null)
            return IndexSnapshot.Unavailable(indexId, 0);
        using (database)
        {
            return IndexSnapshot.Observed(indexId, database.Metadata.Length);
        }
    }

    private static string StableId(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }

    private static string FormatReason(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "unknown reason"
            : value.Replace('_', ' ');

    private static DateTimeOffset SubtractMilliseconds(
        DateTimeOffset timestamp,
        long milliseconds)
    {
        if (!TrySubtractMilliseconds(timestamp, milliseconds, out DateTimeOffset result))
            throw new InvalidOperationException("Validated telemetry duration exceeded timestamp range.");
        return result;
    }

    private static bool TrySubtractMilliseconds(
        DateTimeOffset timestamp,
        long milliseconds,
        out DateTimeOffset result)
    {
        result = default;
        if (milliseconds < 0
            || milliseconds > timestamp.UtcTicks / TimeSpan.TicksPerMillisecond)
        {
            return false;
        }
        result = timestamp.AddTicks(-(milliseconds * TimeSpan.TicksPerMillisecond));
        return true;
    }

    private static bool StringsFitBudget(JsonElement value, int maxBytes)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return Encoding.UTF8.GetByteCount(value.GetString() ?? string.Empty) <= maxBytes;
            case JsonValueKind.Array:
                return value.EnumerateArray().All(item => StringsFitBudget(item, maxBytes));
            case JsonValueKind.Object:
                return value.EnumerateObject()
                    .All(property => StringsFitBudget(property.Value, maxBytes));
            default:
                return true;
        }
    }

    private static string TrimUtf8(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            return value;

        var builder = new StringBuilder(Math.Min(value.Length, maxBytes));
        int bytes = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes)
                break;
            builder.Append(rune);
            bytes += rune.Utf8SequenceLength;
        }
        return builder.ToString();
    }

    private static int EstimateBytes(PortalOperation operation) =>
        256
        + Utf8Bytes(operation.OperationId)
        + Utf8Bytes(operation.CorrelationId)
        + Utf8Bytes(operation.WorkspaceId)
        + Utf8Bytes(operation.InstanceId)
        + Utf8Bytes(operation.Tool)
        + Utf8Bytes(operation.Category)
        + Utf8Bytes(operation.State)
        + Utf8Bytes(operation.Outcome)
        + Utf8Bytes(operation.Confidence)
        + Utf8Bytes(operation.ColdState)
        + Utf8Bytes(operation.Reason)
        + Utf8Bytes(operation.Summary);

    private static int EstimateBytes(PortalEvent item) =>
        160
        + Utf8Bytes(item.EventId)
        + Utf8Bytes(item.WorkspaceId)
        + Utf8Bytes(item.IndexId)
        + Utf8Bytes(item.InstanceId)
        + Utf8Bytes(item.Severity)
        + Utf8Bytes(item.Component)
        + Utf8Bytes(item.Code)
        + Utf8Bytes(item.Message)
        + Utf8Bytes(item.CorrelationId);

    private static int Utf8Bytes(string? value) =>
        value is null
            ? 0
            : 2 + (6 * Encoding.UTF8.GetByteCount(value));

    private static JsonElement? Object(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static bool TryOptionalNonNegativeNumber(
        JsonElement root,
        string name,
        out double? result)
    {
        result = null;
        if (!root.TryGetProperty(name, out JsonElement value))
            return true;
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out double number)
            || !double.IsFinite(number)
            || number < 0)
        {
            return false;
        }
        result = number;
        return true;
    }

    private static long? Long(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long number)
            ? number
            : null;

    private static bool TryOptionalNonNegativeLong(
        JsonElement root,
        string name,
        out long? result)
    {
        result = null;
        if (!root.TryGetProperty(name, out JsonElement value))
            return true;
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out long number)
            || number < 0)
        {
            return false;
        }
        result = number;
        return true;
    }

    private static bool TrySumOptionalNonNegativeNumber(
        JsonElement? first,
        JsonElement? second,
        string name,
        out double? result)
    {
        result = null;
        if (!TryOptionalNestedNonNegativeNumber(first, name, out double? firstValue)
            || !TryOptionalNestedNonNegativeNumber(second, name, out double? secondValue))
        {
            return false;
        }
        if (firstValue is null && secondValue is null)
            return true;
        double sum = (firstValue ?? 0) + (secondValue ?? 0);
        if (!double.IsFinite(sum))
            return false;
        result = sum;
        return true;
    }

    private static bool TryOptionalNestedNonNegativeNumber(
        JsonElement? container,
        string name,
        out double? result)
    {
        result = null;
        return container is null
            || TryOptionalNonNegativeNumber(container.Value, name, out result);
    }

    private static bool TrySumOptionalNonNegativeLong(
        JsonElement? first,
        JsonElement? second,
        string name,
        out long? result)
    {
        result = null;
        if (!TryOptionalNestedNonNegativeLong(first, name, out long? firstValue)
            || !TryOptionalNestedNonNegativeLong(second, name, out long? secondValue))
        {
            return false;
        }
        if (firstValue is null && secondValue is null)
            return true;
        long left = firstValue ?? 0;
        long right = secondValue ?? 0;
        if (left > long.MaxValue - right)
            return false;
        result = left + right;
        return true;
    }

    private static bool TryOptionalNestedNonNegativeLong(
        JsonElement? container,
        string name,
        out long? result)
    {
        result = null;
        return container is null
            || TryOptionalNonNegativeLong(container.Value, name, out result);
    }

    private readonly record struct IndexFileStamp(
        PortalPathGuard.EntryState State,
        PortalFileIdentity Identity,
        long DatabaseLength,
        DateTimeOffset DatabaseWriteUtc)
    {
        internal static IndexFileStamp Read(string root)
        {
            PortalPathGuard.EntryState state =
                PortalPathGuard.OpenRegularFile(
                    root,
                    Path.Combine(".codenav", "index.db"),
                    out PortalRegularFile? database);
            if (state != PortalPathGuard.EntryState.Safe || database is null)
                return new IndexFileStamp(state, default, 0, DateTimeOffset.MinValue);
            using (database)
            {
                PortalFileMetadata metadata = database.Metadata;
                return new IndexFileStamp(
                    state,
                    metadata.Identity,
                    metadata.Length,
                    metadata.LastWriteUtc);
            }
        }
    }

    private sealed record WorkspaceSnapshot(
        string WorkspaceId,
        string Name,
        IndexSnapshot Index,
        bool HasLiveData,
        PortalInstance[] Instances,
        PortalOperation[] Operations,
        PortalEvent[] Events,
        bool DataComplete,
        long DroppedRecords,
        long RetentionEvictions,
        int TruncatedFiles,
        int TailLimitedFiles,
        int InvalidRecords,
        int OmittedSourceFiles,
        int SourceDiscoveryTruncations,
        int SourceReadErrors,
        int IngestionBacklogs,
        int SourceFiles,
        DateTimeOffset? LatestTelemetryUtc,
        long RetentionGeneration,
        DateTimeOffset? RetainedFromUtc,
        PortalBuild? CurrentBuild);

    private sealed record IndexSnapshot(
        string IndexId,
        bool Exists,
        bool ObservationComplete,
        string State,
        string StateLabel,
        string Freshness,
        long? Epoch,
        long DatabaseSizeBytes,
        string? IndexVersion,
        string? SchemaVersion,
        string? IndexedCommit,
        string? IndexedBranch,
        DateTimeOffset? IndexedAtUtc,
        DateTimeOffset? LastRefreshUtc,
        string? RefreshIncompleteReason,
        string RefreshState,
        long? FileCount,
        long? ProjectCount,
        long? SymbolCount,
        string Description)
    {
        internal static IndexSnapshot Missing(string indexId) => new(
            indexId,
            false,
            true,
            "not_indexed",
            "Index unavailable",
            "unknown",
            null,
            0,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "unavailable",
            null,
            null,
            null,
            "Telemetry present · index metadata unavailable");

        internal static IndexSnapshot Observed(string indexId, long bytes) => new(
            indexId,
            true,
            true,
            "unknown",
            "Index present",
            "unknown",
            null,
            bytes,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "unknown",
            null,
            null,
            null,
            "Index file present · metadata intentionally not opened by the portal");

        internal static IndexSnapshot Unavailable(string indexId, long bytes) => new(
            indexId,
            true,
            false,
            "unknown",
            "Index metadata busy",
            "unknown",
            null,
            bytes,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "unknown",
            null,
            null,
            null,
            "Index path is present but not safe to observe");
    }

    private sealed record PortalOperation(
        string OperationId,
        string CorrelationId,
        string WorkspaceId,
        string InstanceId,
        string Tool,
        string Category,
        DateTimeOffset StartedAtUtc,
        long? DurationMs,
        string State,
        string Outcome,
        string Confidence,
        bool Partial,
        string ColdState,
        string? Reason,
        string Summary,
        OperationTimings Timings,
        OperationCounts Counts);

    private sealed record OperationTimings(
        double? GateWaitMs,
        double? FingerprintMs,
        double? TopologyMs,
        double? ProjectLoadMs);

    private sealed record OperationCounts(
        long? Requested,
        long? Loaded,
        long? Reloaded,
        long? Failed);

    private sealed record PortalEvent(
        string EventId,
        string WorkspaceId,
        string IndexId,
        string InstanceId,
        DateTimeOffset TimestampUtc,
        string Severity,
        string Component,
        string Code,
        string Message,
        string? CorrelationId);

    private sealed record PortalBuild(
        string BuildId,
        string InstanceId,
        string State,
        string? Reason,
        string Phase,
        DateTimeOffset TimestampUtc,
        long PhaseElapsedMs,
        long ElapsedMs,
        long FilesProcessed,
        long? FilesTotal,
        long FilesSkipped,
        long ProjectsFailed,
        long SymbolsWritten,
        long BytesRead,
        double? FilesPerSecond,
        long? EstimatedRemainingMs,
        bool IsProcessAlive);

    private sealed record PortalInstance(
        string InstanceId,
        string WorkspaceId,
        string IndexId,
        string DisplayName,
        string Role,
        string ConnectionState,
        string SemanticState,
        string Version,
        string? BuildStamp,
        string? SchemaVersion,
        string Platform,
        string[] FeatureIds,
        int FeatureCount,
        bool FeatureIdsTruncated,
        int ProcessId,
        DateTimeOffset? ProcessStartedUtc,
        DateTimeOffset LastSeenUtc);
}
