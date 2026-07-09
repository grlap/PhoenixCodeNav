using System.Diagnostics;
using System.IO.Hashing;
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
    /// when an accessor differs from the member's own.</summary>
    public const string SchemaVersion = "9";

    public static BuildResult Build(string workspaceRoot, string? dbPath = null, Action<string>? progress = null,
        BuildProgress? liveProgress = null)
    {
        var total = Stopwatch.StartNew();
        dbPath ??= DefaultDbPath(workspaceRoot);
        progress?.Invoke($"Scanning {workspaceRoot} ...");

        var sw = Stopwatch.StartNew();
        var scan = WorkspaceScanner.Scan(workspaceRoot);
        var scanTime = sw.Elapsed;
        progress?.Invoke($"Scanned: {scan.CsFiles.Count} .cs, {scan.ProjectFiles.Count} .csproj, {scan.SolutionFiles.Count} solutions");
        // The scan fixes filesTotal — the point where "% done" becomes derivable (bead two).
        liveProgress?.SetFilesTotal(scan.CsFiles.Count);
        liveProgress?.SetPhase("parsing_projects");

        // ---- project + solution parsing (parallel, cheap XML) ----
        sw.Restart();
        var parsedProjects = new ParsedProject[scan.ProjectFiles.Count];
        Parallel.For(0, scan.ProjectFiles.Count, i =>
        {
            parsedProjects[i] = ProjectFileParser.Parse(workspaceRoot, scan.ProjectFiles[i].RelPath);
        });
        var parsedSolutions = scan.SolutionFiles
            .Select(s => SolutionParser.Parse(workspaceRoot, s.RelPath))
            .ToList();
        var projectTime = sw.Elapsed;

        using var store = new IndexStore(dbPath, createNew: true);

        // ---- write projects, refs, packages, solutions ----
        var projectIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        int unresolvedRefs = 0;
        using (var tx = store.BeginTransaction())
        {
            foreach (var p in parsedProjects)
            {
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

        // ---- parse + persist .cs files (parallel parse, single writer) ----
        sw.Restart();
        liveProgress?.SetPhase("indexing_files");
        progress?.Invoke($"Parsing {scan.CsFiles.Count} C# files on {Environment.ProcessorCount} cores ...");

        var channel = Channel.CreateBounded<(ScannedFile File, ParsedCsFile Parsed, ulong Hash)>(
            new BoundedChannelOptions(1024) { SingleReader = true });

        var producer = Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(scan.CsFiles, scanned =>
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(Path.Combine(workspaceRoot, scanned.RelPath));
                        ulong hash = XxHash64.HashToUInt64(bytes);
                        string content = DecodeUtf8(bytes);
                        var parsed = SyntaxIndexer.Parse(scanned.RelPath, content);
                        channel.Writer.WriteAsync((scanned, parsed, hash)).AsTask().GetAwaiter().GetResult();
                    }
                    catch (IOException) { /* transiently unreadable — skip; delta refresh will retry */ }
                    catch (UnauthorizedAccessException) { }
                });
            }
            finally
            {
                channel.Writer.Complete();
            }
        });

        var fileIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
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
                    long fileId = store.InsertFile(tx, item.File.RelPath, item.File.Size, item.File.MtimeTicks,
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

        // ---- other files (csproj/sln/config): find_file + config_lookup fodder ----
        int otherCount = 0;
        using (var tx = store.BeginTransaction())
        {
            foreach (var (bucket, lang) in new[]
                     {
                         (scan.ProjectFiles, "csproj"),
                         (scan.SolutionFiles, "sln"),
                         (scan.ConfigFiles, "config"),
                     })
            {
                foreach (var f in bucket)
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(Path.Combine(workspaceRoot, f.RelPath));
                        string content = DecodeUtf8(bytes);
                        long id = store.InsertFile(tx, f.RelPath, f.Size, f.MtimeTicks,
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
            var csFileIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
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
