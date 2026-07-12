using System.Diagnostics;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using CodeNav.Core.Discovery;

namespace CodeNav.Core.Indexing;

public sealed record BuildResult(
    int Projects,
    int Solutions,
    int CsFiles,
    int OtherFiles,
    long Symbols,
    long Lines,
    int UnresolvedProjectRefs,
    TimeSpan ScanTime,
    TimeSpan ProjectTime,
    TimeSpan ParseTime,
    TimeSpan TotalTime,
    long DbBytes);

/// <summary>
/// Owns: full index construction — scan, project/solution parsing, parallel syntax
/// parsing, and single-writer persistence. v1 is full-rebuild; delta refresh arrives
/// with the file watcher. Does not own: query shapes (IndexQueries) or MCP surface.
/// </summary>
public static class IndexBuilder
{
    internal const int MaxStructuralFileBytes = 16 * 1024 * 1024;
    internal const int MaxOptionalSolutionSnapshotBytes = 128 * 1024 * 1024;
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
    /// persisted names/declaration keys.</summary>
    public const string SchemaVersion = "14";

    public static BuildResult Build(string workspaceRoot, string? dbPath = null, Action<string>? progress = null,
        BuildProgress? liveProgress = null) =>
        BuildCore(workspaceRoot, dbPath, progress, liveProgress,
            TimeSpan.FromSeconds(30), waitingForReviewReaders: null);

    internal static BuildResult BuildWithReviewWaitForTest(string workspaceRoot,
        string? dbPath, TimeSpan reviewWaitTimeout, Action? waitingForReviewReaders = null) =>
        BuildCore(workspaceRoot, dbPath, progress: null, liveProgress: null,
            reviewWaitTimeout, waitingForReviewReaders);

    private static BuildResult BuildCore(string workspaceRoot, string? dbPath,
        Action<string>? progress, BuildProgress? liveProgress, TimeSpan reviewWaitTimeout,
        Action? waitingForReviewReaders)
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

                IndexReviewCoordinationLease? rebuildLease = null;
                if (OperatingSystem.IsWindows() &&
                    ownedIdentity.DatabaseIdentity is { Length: > 0 })
                {
                    IndexReviewCoordinationAcquireResult coordination =
                        IndexReviewCoordinationLease.TryAcquireExclusive(ownedIdentity,
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
                    return BuildOwned(root, authority.DatabasePath, progress, liveProgress);
            }
        }
    }

    internal static BuildResult BuildOwned(string workspaceRoot, string dbPath,
        Action<string>? progress = null, BuildProgress? liveProgress = null)
    {
        var total = Stopwatch.StartNew();
        progress?.Invoke($"Scanning {workspaceRoot} ...");

        var sw = Stopwatch.StartNew();
        var scan = WorkspaceScanner.Scan(workspaceRoot);
        var scanTime = sw.Elapsed;
        progress?.Invoke($"Scanned: {scan.CsFiles.Count} .cs, {scan.ProjectFiles.Count} .csproj, {scan.SolutionFiles.Count} solutions");
        // The scan fixes filesTotal — the point where "% done" becomes derivable (bead two).
        liveProgress?.SetFilesTotal(scan.CsFiles.Count);
        liveProgress?.SetPhase("parsing_projects");

        // Capture and parse every authoritative project independently through a bounded no-follow
        // snapshot. Only compact parse facts + hashes survive this pass, so thousands of custom
        // projects do not accumulate XML bytes in memory. Solutions are optional metadata under a
        // separate cumulative budget and never select or suppress projects.
        sw.Restart();
        (ParsedProject[] parsedProjects, List<ParsedSolution> parsedSolutions,
            Dictionary<string, string> requiredStructuralHashes,
            Dictionary<string, string> optionalSolutionHashes) =
            CaptureAndParseStructuralInputs(workspaceRoot, scan, progress);
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
            byte[]? bytes = GitInfo.ReadBoundedWorkspaceFile(workspaceRoot, file.RelPath,
                MaxStructuralFileBytes);
            if (bytes is null || StructuralFingerprint(bytes) != expectedHash)
            {
                if (required)
                    throw new IOException(
                        $"Authoritative project input changed during build: {file.RelPath}");
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
                PersistVerifiedStructuralFile(tx, projectFile, "csproj",
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

        var producer = Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(scan.CsFiles, scanned =>
                {
                    try
                    {
                        byte[]? bytes = GitInfo.ReadBoundedWorkspaceFile(workspaceRoot,
                            scanned.RelPath, DeltaRefresher.MaxIndexedFileBytes);
                        if (bytes is null)
                        {
                            liveProgress?.AddFileSkipped();
                            return;
                        }
                        ulong hash = XxHash64.HashToUInt64(bytes);
                        string content = DecodeUtf8(bytes);
                        var parsed = SyntaxIndexer.Parse(scanned.RelPath, content);
                        channel.Writer.WriteAsync((scanned, parsed, hash, bytes.Length)).AsTask()
                            .GetAwaiter().GetResult();
                    }
                    // efa: a skipped file is ABSENT from the index until a delta refresh retries
                    // it — count it so filesIndexed + filesSkipped accounts for filesTotal and a
                    // stalled-looking bar is distinguishable from a lossy one.
                    catch (IOException) { liveProgress?.AddFileSkipped(); /* transiently unreadable; delta refresh will retry */ }
                    catch (UnauthorizedAccessException) { liveProgress?.AddFileSkipped(); }
                });
            }
            finally
            {
                channel.Writer.Complete();
            }
        });

        long symbolCount = 0, lineCount = 0;
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
                    if (++inTx >= 400)
                    {
                        tx.Commit();
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
            tx.Commit();
            tx.Dispose();
        }
        producer.GetAwaiter().GetResult();
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
                    try
                    {
                        byte[]? bytes = GitInfo.ReadBoundedWorkspaceFile(workspaceRoot,
                            f.RelPath, DeltaRefresher.MaxIndexedFileBytes);
                        if (bytes is null) continue;
                        string content = DecodeUtf8(bytes);
                        long id = store.InsertFile(tx, f.RelPath, bytes.LongLength, f.MtimeTicks,
                            XxHash64.HashToUInt64(bytes), lang,
                            CountNewlines(content) + 1, isGenerated: false, hasTestAttrs: false);
                        store.InsertContent(tx, id, content);
                        fileIds[f.RelPath] = id;
                        otherCount++;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            tx.Commit();
        }

        // ---- compile items: explicit for legacy, longest-dir-prefix for SDK ----
        using (var tx = store.BeginTransaction())
        {
            var csFileIds = new Dictionary<string, long>(WorkspacePaths.FileSystemPathComparer);
            foreach (var f in scan.CsFiles)
            {
                if (fileIds.TryGetValue(f.RelPath, out long fid)) csFileIds[f.RelPath] = fid;
            }
            CompileItemResolver.Write(store, tx, parsedProjects, projectIds, csFileIds);
            // isTest R3 (custom-resolve-proof): compiled test attributes + graph-leaf promotion —
            // must run after BOTH compile attribution and ref insertion (leaf check).
            int promoted = store.PromoteTestProjectsByCompiledAttributes(tx);
            if (promoted > 0) progress?.Invoke($"Test classification: {promoted} project rows promoted (compiled test attributes + same-name uniformity)");
            tx.Commit();
        }

        store.SetMeta("schema_version", SchemaVersion);
        store.SetMeta("index_version", Guid.NewGuid().ToString("N"));
        store.SetMeta("indexed_at_utc", DateTime.UtcNow.ToString("O"));
        store.SetMeta("workspace_root", Path.GetFullPath(workspaceRoot));
        store.SetMeta("unresolved_project_refs", unresolvedRefs.ToString());
        progress?.Invoke("Optimizing index ...");
        store.Optimize();

        var parseTime = sw.Elapsed;
        long dbBytes = new FileInfo(dbPath).Length;

        return new BuildResult(
            parsedProjects.Length, parsedSolutions.Count, csCount, otherCount,
            symbolCount, lineCount, unresolvedRefs,
            scanTime, projectTime, parseTime, total.Elapsed, dbBytes);
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        string s = Encoding.UTF8.GetString(bytes);
        return s.Length > 0 && s[0] == (char)0xFEFF ? s[1..] : s;
    }

    private static (ParsedProject[] Projects, List<ParsedSolution> Solutions,
        Dictionary<string, string> RequiredHashes,
        Dictionary<string, string> OptionalSolutionHashes) CaptureAndParseStructuralInputs(
        string workspaceRoot, ScanResult scan, Action<string>? progress)
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
            GitInfo.WorkspaceFileReadResult projectRead =
                GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, file.RelPath,
                    MaxStructuralFileBytes);
            byte[]? projectBytes = projectRead.Bytes;
            if (projectBytes is null &&
                projectRead.Disposition == GitInfo.WorkspaceFileReadDisposition.DefinitelyNonRegular)
            {
                progress?.Invoke($"Skipped non-regular project candidate: {file.RelPath}");
                continue;
            }
            if (projectBytes is null)
                throw new IOException($"Project input could not be captured safely: {file.RelPath}");
            requiredHashes[file.RelPath] = StructuralFingerprint(projectBytes);
            string packagesPath = PackagesConfigPath(file.RelPath);
            byte[]? packagesBytes = null;
            if (configByPath.ContainsKey(packagesPath))
            {
                GitInfo.WorkspaceFileReadResult packagesRead =
                    GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, packagesPath,
                        MaxStructuralFileBytes);
                packagesBytes = packagesRead.Bytes;
                if (packagesBytes is null &&
                    packagesRead.Disposition ==
                    GitInfo.WorkspaceFileReadDisposition.DefinitelyNonRegular)
                {
                    progress?.Invoke($"Skipped non-regular packages.config candidate: {packagesPath}");
                }
                else if (packagesBytes is null)
                {
                    throw new IOException($"packages.config could not be captured safely: {packagesPath}");
                }
                else
                {
                    requiredHashes[packagesPath] = StructuralFingerprint(packagesBytes);
                }
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
            byte[]? bytes = GitInfo.ReadBoundedWorkspaceFile(workspaceRoot, file.RelPath,
                MaxStructuralFileBytes);
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
