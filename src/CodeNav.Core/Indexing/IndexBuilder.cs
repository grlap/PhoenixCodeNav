using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using CodeNav.Core.Discovery;

namespace CodeNav.Core.Indexing;

public sealed record BuildResult(
    int Projects,
    int Solutions,
    int CsFiles,
    int FsFiles,
    int OtherFiles,
    long Symbols,
    long Lines,
    int UnresolvedProjectRefs,
    TimeSpan ScanTime,
    TimeSpan ProjectTime,
    TimeSpan ParseTime,
    TimeSpan TotalTime,
    long DbBytes);

internal sealed record FSharpPipelineTestHooks(
    long BatchMemoryBudgetBytes,
    Action<long, int>? ReadBatchPrepared = null,
    Action<int, int>? BeforePersist = null);

internal sealed record BuildCaptureTestHooks(
    Func<string, string, int, GitInfo.WorkspaceFileReadResult> Reader,
    Action<string>? FirstCaptureFailureRetained = null);

/// <summary>
/// Owns: full index construction — scan, project/solution parsing, parallel syntax
/// parsing, and single-writer persistence. v1 is full-rebuild; delta refresh arrives
/// with the file watcher. Does not own: query shapes (IndexQueries) or MCP surface.
/// </summary>
public static class IndexBuilder
{
    internal const int MaxStructuralFileBytes = 16 * 1024 * 1024;
    internal const int MaxOptionalSolutionSnapshotBytes = 128 * 1024 * 1024;
    internal const int SourceWriteBatchSize = 2000;
    internal const long FSharpBatchMemoryBudgetBytes = 32L * 1024 * 1024;
    internal const int FSharpReadBatchMaxItems = 64;
    public static string DefaultDbPath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".codenav", "index.db");

    /// <summary>Rebuild-trigger version: bump whenever the schema OR the indexer's stored output
    /// changes, so a deployed binary rebuilds a stale on-disk index instead of trusting old rows.
    /// v2: ref/out/in/params modifiers in signatures; interface members default to public.
    /// v3: compile-graph fidelity (3tz) — compile_items honor &lt;Compile Include&gt; globs and
    /// &lt;Compile Remove&gt;; projects gained compile_globs.
    /// v4: symbols.modifiers (bt7) — static/sealed/abstract/virtual/override/new/readonly/const.
    /// v5: assembly-ref edge recovery (lhg) — project_refs now include &lt;Reference&gt;-to-
    /// in-workspace-project edges (multi-staged binary refs); same tables, new edge content.
    /// v6: assembly-name COLLISIONS resolve to a name-level edge instead of no-edge (field
    /// 0.7.2 regression: paired declarers lost every consumer edge) — same tables, new edge
    /// content again; without this bump a deployed v5 index keeps the severed graph until an
    /// unrelated csproj change, because the delta path hash-skips untouched project files.
    /// v7: isTest classification — BINARY-referenced test frameworks (nunit.framework/xunit/
    /// MSTest via &lt;Reference&gt;+HintPath) now count, plus compiled-test-attribute leaf
    /// promotion; name matching stays dotted-suffix-only (the no-dot loosening was REJECTED —
    /// TestRoute counterexample). Field: HubServiceTests carried [TestFixture] types yet
    /// filtered as production.
    /// v8: review fixes — name-level self-edge guard (a pair member referencing its own
    /// assembly name minted X-&gt;X, inflating dependents), NAME-uniform is_test across
    /// same-AssemblyName pair rows (half-promotion made the answer scan-order-dependent),
    /// xunit.assert marker.
    /// v9: symbols.accessors (hu7) — per-accessor accessibility ("get=public;set=private")
    /// when an accessor differs from the member's own.
    /// v10: project_refs.kind (bxw) — edge provenance 'project' vs 'assembly' (HintPath /
    /// bare-&lt;Reference&gt; recovered), so graph consumers can flag multi-staged-build couplings
    /// that a ProjectReference-aware rename/refactor will NOT carry. New column + new stored
    /// edge content; a deployed v9 index must rebuild to answer kind at all.
    /// v11: explicit-interface qualifiers are persisted in member signatures so IFoo.M and
    /// IBar.M remain distinct declaration identities for review evidence.
    /// v12: filesystem-produced paths convert only the platform directory separator, preserving
    /// a literal backslash in Unix file.path identity across full and incremental indexing.
    /// v13: tuple element labels no longer participate in persisted declaration identity; callable
    /// tuple element types and nesting remain identity-bearing.
    /// v14: checked operators and explicit-interface operator implementations have distinct
    /// persisted names/declaration keys.
    /// v15: F# source files are persisted as lang='fs'; .fsproj inputs participate in project,
    /// reference, and compile-ownership graphs; projects persist their source language.
    /// v16: arbitrary workspace .props/.targets files are persisted as config inputs so cold builds
    /// match delta refresh classification and pinned F# project evaluation can resolve local props.
    /// v17: incomplete-source freshness metadata and fail-closed bounded capture prevent lossy
    /// builds or refreshes from publishing complete-looking source evidence.
    /// v18: normalized syntax-derived type_base_edges replace repeated leading-wildcard signature
    /// scans and preserve base entries beyond the 400-character display-signature limit.</summary>
    public const string SchemaVersion = "18";

    public static BuildResult Build(string workspaceRoot, string? dbPath = null, Action<string>? progress = null,
        BuildProgress? liveProgress = null) =>
        BuildCore(workspaceRoot, dbPath, progress, liveProgress,
            TimeSpan.FromSeconds(30), waitingForReviewReaders: null, SourceWriteBatchSize,
            fSharpPipelineTestHooks: null, buildCaptureTestHooks: null);

    internal static BuildResult BuildWithReviewWaitForTest(string workspaceRoot,
        string? dbPath, TimeSpan reviewWaitTimeout, Action? waitingForReviewReaders = null) =>
        BuildCore(workspaceRoot, dbPath, progress: null, liveProgress: null,
            reviewWaitTimeout, waitingForReviewReaders, SourceWriteBatchSize,
            fSharpPipelineTestHooks: null, buildCaptureTestHooks: null);

    internal static BuildResult BuildWithSourceBatchSizeForTest(string workspaceRoot,
        int sourceWriteBatchSize, Action<string>? progress = null,
        FSharpPipelineTestHooks? fSharpPipelineTestHooks = null,
        BuildCaptureTestHooks? buildCaptureTestHooks = null) =>
        BuildCore(workspaceRoot, dbPath: null, progress, liveProgress: null,
            TimeSpan.FromSeconds(30), waitingForReviewReaders: null,
            Math.Max(1, sourceWriteBatchSize), fSharpPipelineTestHooks,
            buildCaptureTestHooks);

    private static BuildResult BuildCore(string workspaceRoot, string? dbPath,
        Action<string>? progress, BuildProgress? liveProgress, TimeSpan reviewWaitTimeout,
        Action? waitingForReviewReaders, int sourceWriteBatchSize,
        FSharpPipelineTestHooks? fSharpPipelineTestHooks,
        BuildCaptureTestHooks? buildCaptureTestHooks)
    {
        string root = Path.GetFullPath(workspaceRoot);
        string database = Path.GetFullPath(dbPath ?? DefaultDbPath(root));
        if (!IndexDirectoryAuthority.TryOpen(database, createDirectory: true,
                out IndexDirectoryAuthority? authority) ||
            !authority!.TryGetLeaseIdentity(out IndexLeaseIdentity? leaseIdentity))
        {
            authority?.Dispose();
            throw new IOException("index destination could not be opened without following links");
        }
        using (authority)
        {
            if (!IndexOwnershipLease.TryAcquire(root, database, leaseIdentity,
                    out IndexOwnershipLease? ownershipLease))
                throw new IOException("another Phoenix process owns this index");
            using (ownershipLease!)
            {
                if (!authority.TryGetLeaseIdentity(out IndexLeaseIdentity? afterLease) ||
                    afterLease != leaseIdentity)
                    throw new IOException("index destination changed during ownership acquisition");
                IndexLeaseIdentity ownedIdentity = afterLease!;

                if (!authority.TryAnchorReviewCoordinationFile(create: true))
                    throw new IOException(
                        "index reader coordination could not be initialized safely");

                IndexReviewCoordinationLease? rebuildLease = null;
                if (OperatingSystem.IsWindows())
                {
                    IndexReviewCoordinationAcquireResult coordination =
                        IndexReviewCoordinationLease.TryAcquireExclusive(authority,
                            reviewWaitTimeout, waitingForReviewReaders, out rebuildLease);
                    if (coordination != IndexReviewCoordinationAcquireResult.Acquired)
                        throw new IOException(
                            "index rebuild deferred while another process holds a review snapshot");
                    if (!authority.TryGetLeaseIdentity(out IndexLeaseIdentity? afterReaders) ||
                        afterReaders != ownedIdentity)
                    {
                        rebuildLease!.Dispose();
                        throw new IOException(
                            "index destination changed during review-reader coordination");
                    }
                }

                using (rebuildLease)
                    return BuildOwned(root, authority.DatabasePath, progress, liveProgress,
                        sourceWriteBatchSize, fSharpPipelineTestHooks,
                        buildCaptureTestHooks);
            }
        }
    }

    internal static BuildResult BuildOwned(string workspaceRoot, string dbPath,
        Action<string>? progress = null, BuildProgress? liveProgress = null,
        int sourceWriteBatchSize = SourceWriteBatchSize,
        FSharpPipelineTestHooks? fSharpPipelineTestHooks = null,
        BuildCaptureTestHooks? buildCaptureTestHooks = null)
    {
        var total = Stopwatch.StartNew();
        progress?.Invoke($"Scanning {workspaceRoot} ...");

        var sw = Stopwatch.StartNew();
        var scan = WorkspaceScanner.Scan(workspaceRoot);
        var scanTime = sw.Elapsed;
        progress?.Invoke($"Scanned: {scan.CsFiles.Count} C# source, {scan.FsFiles.Count} F# source, " +
                         $"{scan.ProjectFiles.Count} project files, {scan.SolutionFiles.Count} solutions");
        // The scan fixes filesTotal — the point where "% done" becomes derivable (bead two).
        liveProgress?.SetFilesTotal(scan.CsFiles.Count + scan.FsFiles.Count);
        liveProgress?.SetPhase("parsing_projects");

        GitInfo.WorkspaceFileReadResult ReadBuildInputResult(string relPath, int maxBytes) =>
            buildCaptureTestHooks?.Reader(workspaceRoot, relPath, maxBytes) ??
            GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, relPath, maxBytes);

        byte[]? ReadBuildInput(string relPath, int maxBytes)
        {
            GitInfo.WorkspaceFileReadResult read = ReadBuildInputResult(relPath, maxBytes);
            if (read.Bytes is { } bytes) return bytes;
            if (read.Disposition is GitInfo.WorkspaceFileReadDisposition.Missing or
                GitInfo.WorkspaceFileReadDisposition.DefinitelyNonRegular)
            {
                liveProgress?.AddFileSkipped();
                progress?.Invoke($"Skipped missing or non-regular build input: {relPath}");
                return null;
            }
            ThrowRequiredCaptureFailure(relPath, read);
            throw new RefreshInputUnavailableException(relPath);
        }

        // Capture and parse every authoritative project independently through a bounded no-follow
        // snapshot. Only compact parse facts + hashes survive this pass, so thousands of custom
        // projects do not accumulate XML bytes in memory. Solutions are optional metadata under a
        // separate cumulative budget and never select or suppress projects.
        sw.Restart();
        (ParsedProject[] parsedProjects, List<ParsedSolution> parsedSolutions,
            Dictionary<string, string> requiredStructuralHashes,
            Dictionary<string, string> optionalSolutionHashes) =
            CaptureAndParseStructuralInputs(scan, progress, ReadBuildInput);
        // efa: failed csproj parses were invisible — their compile sets and graph edges are
        // guesses at best, and a watcher of the build deserves the count, not a clean-looking bar.
        liveProgress?.SetProjectsFailed(parsedProjects.Count(p => p.LoadStatus.StartsWith("failed", StringComparison.Ordinal)));
        var projectTime = sw.Elapsed;

        using var store = new IndexStore(dbPath, createNew: true);
        var fileIds = new Dictionary<string, long>(WorkspacePaths.FileSystemPathComparer);
        var projectFilesByPath = scan.ProjectFiles.ToDictionary(file => file.RelPath,
            WorkspacePaths.FileSystemPathComparer);
        var configFilesByPath = scan.ConfigFiles.ToDictionary(file => file.RelPath,
            WorkspacePaths.FileSystemPathComparer);
        var solutionFilesByPath = scan.SolutionFiles.ToDictionary(file => file.RelPath,
            WorkspacePaths.FileSystemPathComparer);
        var associatedPackagesPaths = parsedProjects.Select(project =>
                PackagesConfigPath(project.RelPath))
            .Where(configFilesByPath.ContainsKey)
            .ToHashSet(WorkspacePaths.FileSystemPathComparer);
        int otherCount = 0;

        bool PersistVerifiedStructuralFile(Microsoft.Data.Sqlite.SqliteTransaction tx,
            ScannedFile file, string lang, string expectedHash, bool required)
        {
            GitInfo.WorkspaceFileReadResult read =
                ReadBuildInputResult(file.RelPath, MaxStructuralFileBytes);
            byte[]? bytes = read.Bytes;
            if (bytes is null || StructuralFingerprint(bytes) != expectedHash)
            {
                if (required)
                {
                    ThrowRequiredCaptureFailure(file.RelPath, read);
                    throw new RefreshInputUnavailableException(file.RelPath);
                }
                progress?.Invoke(
                    $"Skipped changed or unavailable optional solution metadata: {file.RelPath}");
                return false;
            }

            string content = DecodeUtf8(bytes);
            long id = store.InsertFile(tx, file.RelPath, bytes.LongLength, file.MtimeTicks,
                XxHash64.HashToUInt64(bytes), lang, CountNewlines(content) + 1,
                isGenerated: false, hasTestAttrs: false);
            store.InsertContent(tx, id, content);
            fileIds[file.RelPath] = id;
            otherCount++;
            return true;
        }

        // ---- verify + atomically write authoritative project facts and file bytes ----
        // Keep this before the potentially long C# parse. Each raw file is re-read, SHA-256 bound
        // to the bytes that produced its parse facts, persisted, and then discarded. The graph and
        // its structural file rows therefore commit as one coherent snapshot without retaining an
        // aggregate XML buffer. A post-build detect-all sweep closes the later watcher gap.
        var projectIds = new Dictionary<string, long>(WorkspacePaths.FileSystemPathComparer);
        int unresolvedRefs = 0;
        var persistedStructuralPaths = new HashSet<string>(
            WorkspacePaths.FileSystemPathComparer);
        var verifiedSolutions = new List<ParsedSolution>(parsedSolutions.Count);
        using (var tx = store.BeginTransaction())
        {
            foreach (var p in parsedProjects)
            {
                ScannedFile projectFile = projectFilesByPath[p.RelPath];
                PersistVerifiedStructuralFile(tx, projectFile,
                    p.Language == "fs" ? "fsproj" : "csproj",
                    requiredStructuralHashes[p.RelPath], required: true);
                persistedStructuralPaths.Add(p.RelPath);

                string packagesPath = PackagesConfigPath(p.RelPath);
                if (requiredStructuralHashes.TryGetValue(packagesPath,
                        out string? packagesHash) &&
                    persistedStructuralPaths.Add(packagesPath))
                {
                    PersistVerifiedStructuralFile(tx, configFilesByPath[packagesPath], "config",
                        packagesHash, required: true);
                }
                projectIds[p.RelPath] = store.InsertProject(tx, p);
            }
            foreach (var p in parsedProjects)
            {
                long fromId = projectIds[p.RelPath];
                foreach (var r in p.ProjectRefRelPaths)
                {
                    if (projectIds.TryGetValue(r, out long toId)) store.InsertProjectRef(tx, fromId, toId);
                    else unresolvedRefs++;
                }
                foreach (var (pkg, version) in p.PackageRefs)
                {
                    store.InsertPackageRef(tx, fromId, pkg, version);
                }
            }
            foreach (var s in parsedSolutions)
            {
                if (!optionalSolutionHashes.TryGetValue(s.RelPath, out string? solutionHash) ||
                    !PersistVerifiedStructuralFile(tx, solutionFilesByPath[s.RelPath], "sln",
                        solutionHash, required: false))
                    continue;
                verifiedSolutions.Add(s);
                long slnId = store.InsertSolution(tx, s.RelPath, s.Name);
                foreach (var pr in s.ProjectRelPaths)
                {
                    if (projectIds.TryGetValue(pr, out long pid)) store.InsertSolutionProject(tx, slnId, pid);
                }
            }
            // Multi-staged builds reference ASSEMBLIES from a common output folder, not projects
            // (lhg) — recover those as graph edges or the dependency graph is blind to them.
            var (recovered, nameCollisions) = AssemblyRefEdges.Write(store, tx, parsedProjects, projectIds);
            if (recovered + nameCollisions > 0)
            {
                progress?.Invoke($"Assembly-ref edges: {recovered} recovered ({nameCollisions} assembly-name collisions resolved to their first project row)");
            }
            tx.Commit();
        }
        parsedSolutions = verifiedSolutions;

        // ---- parse + persist .cs files (parallel parse, single writer) ----
        sw.Restart();
        liveProgress?.SetPhase("indexing_files");
        progress?.Invoke($"Parsing {scan.CsFiles.Count} C# files on {Environment.ProcessorCount} cores ...");

        var channel = Channel.CreateBounded<(
            ScannedFile File, ParsedCsFile Parsed, ulong Hash, int ByteCount)>(
            new BoundedChannelOptions(1024) { SingleReader = true });
        Exception? csharpCaptureFailure = null;

        var producer = Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(scan.CsFiles, (scanned, loop) =>
                {
                    if (Volatile.Read(ref csharpCaptureFailure) is not null)
                    {
                        loop.Stop();
                        return;
                    }
                    try
                    {
                        byte[]? bytes = ReadBuildInput(scanned.RelPath,
                            DeltaRefresher.MaxIndexedFileBytes);
                        if (bytes is null) return;
                        ulong hash = XxHash64.HashToUInt64(bytes);
                        string content = DecodeUtf8(bytes);
                        var parsed = SyntaxIndexer.Parse(scanned.RelPath, content);
                        channel.Writer.WriteAsync((scanned, parsed, hash, bytes.Length)).AsTask()
                            .GetAwaiter().GetResult();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        liveProgress?.AddFileSkipped();
                        Exception normalized = ex is IOException
                            ? ex
                            : new RefreshInputUnavailableException(scanned.RelPath);
                        if (Interlocked.CompareExchange(ref csharpCaptureFailure,
                                normalized, null) is null)
                        {
                            buildCaptureTestHooks?.FirstCaptureFailureRetained?.Invoke(
                                scanned.RelPath);
                            loop.Stop();
                        }
                    }
                });
            }
            finally
            {
                channel.Writer.Complete();
            }
        });

        long symbolCount = 0, lineCount = 0;
        long commitTicks = 0; // lf4p: the writer's commit share, reported with the store's split
        int csCount = 0;
        {
            var reader = channel.Reader;
            var tx = store.BeginTransaction();
            int inTx = 0;
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var item))
                {
                    long fileId = store.InsertFile(tx, item.File.RelPath, item.ByteCount,
                        item.File.MtimeTicks,
                        item.Hash, "cs", item.Parsed.LineCount, item.Parsed.LooksGenerated, item.Parsed.HasTestAttributes);
                    store.InsertContent(tx, fileId, item.Parsed.Content);
                    store.InsertSymbols(tx, fileId, item.Parsed.Symbols);
                    fileIds[item.File.RelPath] = fileId;
                    symbolCount += item.Parsed.Symbols.Count;
                    lineCount += item.Parsed.LineCount;
                    csCount++;

                    liveProgress?.AddFileIndexed();
                    if (++inTx >= sourceWriteBatchSize) // lf4p A/B: 400 → commits cost 2.9s/roslyn; WAL commit amortizes with width
                    {
                        long tc0 = System.Diagnostics.Stopwatch.GetTimestamp(); // lf4p
                        tx.Commit();
                        commitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tc0;
                        tx.Dispose();
                        tx = store.BeginTransaction();
                        inTx = 0;
                        if (csCount % 8000 == 0)
                        {
                            // The human-facing estimate for anyone watching the log (bead two):
                            // running count / fixed total / derived percent / elapsed. The API
                            // surface carries the raw numbers only — no percent field there.
                            progress?.Invoke(
                                $"  indexed {csCount}/{scan.CsFiles.Count} files " +
                                $"({(scan.CsFiles.Count > 0 ? csCount * 100 / scan.CsFiles.Count : 100)}%, " +
                                $"{total.Elapsed.TotalSeconds:F0}s elapsed)");
                        }
                    }
                }
            }
            long tcF = System.Diagnostics.Stopwatch.GetTimestamp(); // lf4p
            tx.Commit();
            commitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tcF;
            tx.Dispose();
        }
        producer.GetAwaiter().GetResult();
        if (csharpCaptureFailure is not null)
            ExceptionDispatchInfo.Capture(csharpCaptureFailure).Throw();

        // ---- persist F# source text (no compiler/syntax service in tier-a) ----
        // Parallel reads are grouped by a conservative byte budget covering both the raw byte[]
        // and decoded UTF-16 text. Every group
        // joins before its single-writer phase begins, so a SQLite failure cannot leave channel
        // producers blocked without a consumer. A source larger than the aggregate budget forms
        // a one-item group: it is still accepted under the per-file cap, but never overlaps
        // another retained F# document.
        progress?.Invoke($"Indexing {scan.FsFiles.Count} F# files on {Environment.ProcessorCount} cores ...");
        long fsBatchMemoryBudget = Math.Max(1,
            fSharpPipelineTestHooks?.BatchMemoryBudgetBytes ?? FSharpBatchMemoryBudgetBytes);
        int fsReadersInFlight = 0;
        int fsCount = 0;
        int fsWriteBatches = 0;
        {
            var tx = store.BeginTransaction();
            int inTx = 0;
            try
            {
                foreach (List<ScannedFile> readBatch in FSharpReadBatches(
                             scan.FsFiles, fsBatchMemoryBudget))
                {
                    var prepared = new PreparedFSharpSource?[readBatch.Count];
                    Exception? captureFailure = null;
                    Parallel.For(0, readBatch.Count, (i, loop) =>
                    {
                        if (Volatile.Read(ref captureFailure) is not null)
                        {
                            loop.Stop();
                            return;
                        }
                        Interlocked.Increment(ref fsReadersInFlight);
                        try
                        {
                            ScannedFile scanned = readBatch[i];
                            try
                            {
                                byte[]? bytes = ReadBuildInput(scanned.RelPath,
                                    DeltaRefresher.MaxIndexedFileBytes);
                                if (bytes is null) return;
                                // The byte budget was calculated from the no-follow scan snapshot.
                                // If the file changed size meanwhile, omit this incoherent row; the
                                // post-build freshness sweep will attribute the current bytes.
                                if (bytes.LongLength != scanned.Size)
                                {
                                    throw new RefreshInputUnavailableException(scanned.RelPath);
                                }

                                string content = DecodeUtf8(bytes);
                                prepared[i] = new PreparedFSharpSource(scanned, content,
                                    XxHash64.HashToUInt64(bytes), bytes.Length,
                                    CountNewlines(content) + 1,
                                    FileClassifier.LooksGenerated(scanned.RelPath, content));
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                liveProgress?.AddFileSkipped();
                                Exception normalized = ex is IOException
                                    ? ex
                                    : new RefreshInputUnavailableException(scanned.RelPath);
                                if (Interlocked.CompareExchange(ref captureFailure,
                                        normalized, null) is null)
                                {
                                    buildCaptureTestHooks?.FirstCaptureFailureRetained?.Invoke(
                                        scanned.RelPath);
                                    loop.Stop();
                                }
                            }
                        }
                        finally
                        {
                            Interlocked.Decrement(ref fsReadersInFlight);
                        }
                    });
                    if (captureFailure is not null)
                        ExceptionDispatchInfo.Capture(captureFailure).Throw();

                    long batchMemoryBytes = prepared.Where(item => item is not null)
                        .Sum(item => ActualFSharpBatchMemoryBytes(
                            item!.ByteCount, item.Content.Length));
                    int preparedCount = prepared.Count(item => item is not null);
                    fSharpPipelineTestHooks?.ReadBatchPrepared?.Invoke(
                        batchMemoryBytes, preparedCount);

                    for (int i = 0; i < prepared.Length; i++)
                    {
                        PreparedFSharpSource? item = prepared[i];
                        if (item is null) continue;
                        fSharpPipelineTestHooks?.BeforePersist?.Invoke(
                            fsCount, Volatile.Read(ref fsReadersInFlight));
                        long id = store.InsertFile(tx, item.File.RelPath, item.ByteCount,
                            item.File.MtimeTicks, item.Hash, "fs", item.LineCount,
                            item.IsGenerated, hasTestAttrs: false);
                        store.InsertContent(tx, id, item.Content);
                        fileIds[item.File.RelPath] = id;
                        fsCount++;
                        lineCount += item.LineCount;
                        liveProgress?.AddFileIndexed();
                        prepared[i] = null; // release large strings as soon as the writer is done
                        if (++inTx >= sourceWriteBatchSize)
                        {
                            long tc0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            tx.Commit();
                            commitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tc0;
                            fsWriteBatches++;
                            tx.Dispose();
                            tx = store.BeginTransaction();
                            inTx = 0;
                        }
                    }
                }
                if (inTx > 0)
                {
                    long tc0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    tx.Commit();
                    commitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tc0;
                    fsWriteBatches++;
                }
            }
            finally
            {
                tx.Dispose();
            }
        }
        progress?.Invoke(
            $"  indexed {fsCount}/{scan.FsFiles.Count} F# files in {fsWriteBatches} writer batches");
        liveProgress?.SetPhase("finalizing");

        // ---- remaining config files: find_file + config_lookup fodder ----
        // Project/package rows and verified optional solutions were already persisted in the
        // coherent structural transaction above. Unsafe/budget-skipped solutions stay absent.
        using (var tx = store.BeginTransaction())
        {
            foreach (var (bucket, lang) in new[]
                     {
                         (scan.ConfigFiles, "config"),
                     })
            {
                foreach (var f in bucket)
                {
                    if (persistedStructuralPaths.Contains(f.RelPath)) continue;
                    // A packages.config associated with a parsed project was either persisted in
                    // the structural transaction or deliberately classified non-regular/absent.
                    // Leave the latter absent so the post-build sweep can observe a later regular
                    // replacement and rebuild the graph. Orphan packages.config files retain their
                    // baseline config_lookup behavior here.
                    if (associatedPackagesPaths.Contains(f.RelPath)) continue;
                    byte[]? bytes = ReadBuildInput(f.RelPath,
                        DeltaRefresher.MaxIndexedFileBytes);
                    if (bytes is null) continue;
                    string content = DecodeUtf8(bytes);
                    long id = store.InsertFile(tx, f.RelPath, bytes.LongLength, f.MtimeTicks,
                        XxHash64.HashToUInt64(bytes), lang,
                        CountNewlines(content) + 1, isGenerated: false, hasTestAttrs: false);
                    store.InsertContent(tx, id, content);
                    fileIds[f.RelPath] = id;
                    otherCount++;
                }
            }
            tx.Commit();
        }

        // ---- compile items: explicit for legacy, longest-dir-prefix for SDK ----
        using (var tx = store.BeginTransaction())
        {
            var sourceFileIds = new Dictionary<string, long>(WorkspacePaths.FileSystemPathComparer);
            foreach (var f in scan.CsFiles.Concat(scan.FsFiles))
            {
                if (fileIds.TryGetValue(f.RelPath, out long fid)) sourceFileIds[f.RelPath] = fid;
            }
            CompileItemResolver.Write(store, tx, parsedProjects, projectIds, sourceFileIds);
            // isTest R3 (custom-resolve-proof): compiled test attributes + graph-leaf promotion —
            // must run after BOTH compile attribution and ref insertion (leaf check).
            int promoted = store.PromoteTestProjectsByCompiledAttributes(tx);
            if (promoted > 0) progress?.Invoke($"Test classification: {promoted} project rows promoted (compiled test attributes + same-name uniformity)");
            tx.Commit();
        }

        store.SetMeta("index_version", Guid.NewGuid().ToString("N"));
        store.SetMeta("indexed_at_utc", DateTime.UtcNow.ToString("O"));
        store.SetMeta("workspace_root", Path.GetFullPath(workspaceRoot));
        store.SetMeta("unresolved_project_refs", unresolvedRefs.ToString());
        // A new database is compatible only after its manager attaches the watcher and commits
        // the post-build detect-all pass. Write the follower-visible marker before schema_version,
        // which is the final compatibility barrier observed by another process.
        IndexManager.PersistRefreshSweepPending(store);
        store.SetMeta("schema_version", SchemaVersion);
        progress?.Invoke("Optimizing index ...");
        store.Optimize();

        // lf4p: the per-statement split of the single writer's time — the measurement that
        // decides whether FTS tokenization (second-writer-split candidate) or b-tree work
        // dominates. Log-only surface: no API change, Bench and server logs both capture it.
        var wt = store.WriterTimingsMs;
        double commitMs = commitTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        double writerTotal = wt.FileRows + wt.ContentRows + wt.FtsRows + wt.SymbolRows
                             + wt.BaseEdgeRows
                             + wt.FtsOptimize + wt.Analyze + wt.Checkpoint + commitMs;
        progress?.Invoke(
            $"Writer split (lf4p): fts {wt.FtsRows / 1000:F1}s, content {wt.ContentRows / 1000:F1}s, " +
            $"symbols {wt.SymbolRows / 1000:F1}s, base-edges {wt.BaseEdgeRows / 1000:F1}s, " +
            $"files {wt.FileRows / 1000:F1}s, " +
            $"commits {commitMs / 1000:F1}s, fts-optimize {wt.FtsOptimize / 1000:F1}s, " +
            $"analyze {wt.Analyze / 1000:F1}s, checkpoint {wt.Checkpoint / 1000:F1}s " +
            $"— writer statements total {writerTotal / 1000:F1}s");

        var parseTime = sw.Elapsed;
        long dbBytes = new FileInfo(dbPath).Length;

        return new BuildResult(
            parsedProjects.Length, parsedSolutions.Count, csCount, fsCount, otherCount,
            symbolCount, lineCount, unresolvedRefs,
            scanTime, projectTime, parseTime, total.Elapsed, dbBytes);
    }

    private const long FSharpPreparedItemOverheadBytes = 256;

    private sealed record PreparedFSharpSource(
        ScannedFile File,
        string Content,
        ulong Hash,
        int ByteCount,
        int LineCount,
        bool IsGenerated);

    private static IEnumerable<List<ScannedFile>> FSharpReadBatches(
        IReadOnlyList<ScannedFile> files, long batchMemoryBudgetBytes)
    {
        var batch = new List<ScannedFile>(Math.Min(FSharpReadBatchMaxItems, files.Count));
        long retainedCharge = 0;
        foreach (ScannedFile file in files)
        {
            long estimate = EstimatedFSharpBatchMemoryBytes(file.Size);
            long charge = Math.Min(estimate, batchMemoryBudgetBytes);
            if (batch.Count > 0 &&
                (batch.Count >= FSharpReadBatchMaxItems ||
                 charge > batchMemoryBudgetBytes - retainedCharge))
            {
                yield return batch;
                batch = new List<ScannedFile>(Math.Min(FSharpReadBatchMaxItems, files.Count));
                retainedCharge = 0;
            }

            batch.Add(file);
            retainedCharge += charge;
            if (estimate > batchMemoryBudgetBytes || batch.Count >= FSharpReadBatchMaxItems)
            {
                yield return batch;
                batch = new List<ScannedFile>(Math.Min(FSharpReadBatchMaxItems, files.Count));
                retainedCharge = 0;
            }
        }

        if (batch.Count > 0) yield return batch;
    }

    private static long EstimatedFSharpBatchMemoryBytes(long rawByteCount)
    {
        long bounded = Math.Max(0, rawByteCount);
        // The raw byte[] and decoded UTF-16 string can coexist until a read iteration
        // completes, so charge both representations rather than only queued text.
        return bounded > (long.MaxValue - FSharpPreparedItemOverheadBytes) / 3
            ? long.MaxValue
            : bounded * 3 + FSharpPreparedItemOverheadBytes;
    }

    private static long ActualFSharpBatchMemoryBytes(int byteCount, int characterCount) =>
        Math.Max(0L, byteCount) +
        (long)Math.Max(0, characterCount) * sizeof(char) +
        FSharpPreparedItemOverheadBytes;

    private static string DecodeUtf8(byte[] bytes)
    {
        string s = Encoding.UTF8.GetString(bytes);
        return s.Length > 0 && s[0] == (char)0xFEFF ? s[1..] : s;
    }

    private static (ParsedProject[] Projects, List<ParsedSolution> Solutions,
        Dictionary<string, string> RequiredHashes,
        Dictionary<string, string> OptionalSolutionHashes) CaptureAndParseStructuralInputs(
        ScanResult scan, Action<string>? progress,
        Func<string, int, byte[]?> readBuildInput)
    {
        var requiredHashes = new Dictionary<string, string>(
            WorkspacePaths.FileSystemPathComparer);
        List<ScannedFile> projects = scan.ProjectFiles
            .OrderBy(file => file.RelPath, StringComparer.Ordinal).ToList();
        var configByPath = scan.ConfigFiles.ToDictionary(file => file.RelPath,
            WorkspacePaths.FileSystemPathComparer);
        var parsedProjects = new List<ParsedProject>(projects.Count);
        foreach (ScannedFile file in projects)
        {
            byte[]? projectBytes = readBuildInput(file.RelPath, MaxStructuralFileBytes);
            if (projectBytes is null) continue;
            requiredHashes[file.RelPath] = StructuralFingerprint(projectBytes);
            string packagesPath = PackagesConfigPath(file.RelPath);
            byte[]? packagesBytes = null;
            if (configByPath.ContainsKey(packagesPath))
            {
                packagesBytes = readBuildInput(packagesPath, MaxStructuralFileBytes);
                if (packagesBytes is not null)
                    requiredHashes[packagesPath] = StructuralFingerprint(packagesBytes);
            }
            parsedProjects.Add(ProjectFileParser.ParseSnapshot(file.RelPath, projectBytes,
                packagesBytes));
        }

        var parsedSolutions = new List<ParsedSolution>();
        var optionalSolutionHashes = new Dictionary<string, string>(
            WorkspacePaths.FileSystemPathComparer);
        long optionalBytes = 0;
        foreach (ScannedFile file in scan.SolutionFiles.OrderBy(file => file.RelPath,
                     StringComparer.Ordinal))
        {
            byte[]? bytes = GitInfo.ReadBoundedWorkspaceFile(scan.Root,
                file.RelPath, MaxStructuralFileBytes);
            if (bytes is null || optionalBytes + bytes.LongLength >
                MaxOptionalSolutionSnapshotBytes)
            {
                progress?.Invoke($"Skipped unsafe or oversized optional solution metadata: {file.RelPath}");
                continue;
            }
            optionalSolutionHashes[file.RelPath] = StructuralFingerprint(bytes);
            parsedSolutions.Add(SolutionParser.ParseSnapshot(file.RelPath, bytes));
            optionalBytes += bytes.LongLength;
        }
        return (parsedProjects.ToArray(), parsedSolutions, requiredHashes,
            optionalSolutionHashes);
    }

    private static string StructuralFingerprint(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static void ThrowRequiredCaptureFailure(string relPath,
        GitInfo.WorkspaceFileReadResult read)
    {
        if (read.Disposition == GitInfo.WorkspaceFileReadDisposition.Oversized)
            throw new RefreshInputOversizedException(relPath);
        throw new RefreshInputUnavailableException(relPath);
    }

    private static string PackagesConfigPath(string projectPath)
    {
        int slash = projectPath.LastIndexOf('/');
        return slash < 0 ? "packages.config" : projectPath[..(slash + 1)] + "packages.config";
    }

    private static int CountNewlines(string s)
    {
        int n = 0;
        foreach (char c in s)
        {
            if (c == '\n') n++;
        }
        return n;
    }
}
