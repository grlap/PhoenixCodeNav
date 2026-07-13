using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using CodeNav.Core.Discovery;

namespace CodeNav.Core.Indexing;

public sealed record RefreshResult(
    int ChangedFiles, int AddedFiles, int DeletedFiles, bool ProjectsRefreshed, TimeSpan Elapsed,
    string? RefreshedAtUtc = null);

/// <summary>
/// Owns: incremental index updates — targeted (watcher batches) or detect-all
/// (mtime/size/hash sweep). Single-writer: callers must serialize invocations.
/// Does not own: watching (WorkspaceWatcher) or lifecycle/status (IndexManager).
/// </summary>
public static class DeltaRefresher
{
    internal const int MaxIndexedFileBytes = 256 * 1024 * 1024;

    public static RefreshResult Refresh(
        IndexStore store, string workspaceRoot, IReadOnlyCollection<string>? changedRelPaths,
        Action<string>? log = null, bool removeUnavailableKnown = false,
        string? recordCommit = null, string? recordBranch = null)
    {
        var sw = Stopwatch.StartNew();
        var stored = store.AllFilesByPath();

        List<string> candidates;
        bool detectAll = changedRelPaths is null;
        ScanResult? scan = null;

        if (detectAll)
        {
            scan = WorkspaceScanner.Scan(workspaceRoot);
            var seen = new HashSet<string>(WorkspacePaths.FileSystemPathComparer);
            candidates = new List<string>();
            foreach (var f in scan.CsFiles.Concat(scan.ProjectFiles).Concat(scan.SolutionFiles).Concat(scan.ConfigFiles))
            {
                seen.Add(f.RelPath);
                if (!stored.TryGetValue(f.RelPath, out _))
                {
                    candidates.Add(f.RelPath); // added
                }
                else
                {
                    candidates.Add(f.RelPath); // may be changed — hash check below decides
                }
            }
            foreach (var path in stored.Keys.Where(p => !seen.Contains(p)))
            {
                candidates.Add(path); // deleted
            }
        }
        else
        {
            candidates = changedRelPaths!.Distinct(WorkspacePaths.FileSystemPathComparer).ToList();
        }

        int added = 0, changed = 0, deleted = 0;
        bool projectDataDirty = false;
        Dictionary<string, long>? globRoots = null; // lazy: only built when a .cs file is ADDED
        bool? hasLegacy = null;                     // lazy: gates the incremental attribution

        string? refreshedAtUtc = null;
        using (var tx = store.BeginTransaction())
        {
            foreach (var rel in candidates)
            {
                // Every candidate is in the canonical Git/index path domain: scanner and watcher
                // producers convert only the platform separator, Git already emits '/', and the
                // MCP boundary normalizes caller paths. Resolve with Git semantics so a literal
                // backslash in a Unix filename cannot become a directory separator.
                if (!WorkspacePaths.TryResolveGitPathInside(workspaceRoot, rel,
                        out string full)) continue;
                // Excluded-dir parity with the scanner (review): git reconcile and refresh_index feed
                // RAW paths here (the watcher filters, they don't) — without this, a committed csproj
                // under packages/ or bin/ becomes a file row and, now that RefreshProjectData sources
                // its project list from the files table, a phantom project the disk walk never minted.
                if (!detectAll && WorkspaceScanner.IsExcludedPath(rel)) continue;
                bool known = stored.TryGetValue(rel, out var old);
                string lang = LangOf(rel);
                if (lang == "other") continue;
                bool authoritativeProjectInput = lang == "csproj" || IsPackagesConfig(rel) ||
                    Path.GetExtension(rel).Equals(".props", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(rel).Equals(".targets", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(rel).Equals(".projitems", StringComparison.OrdinalIgnoreCase);
                bool projectShapePath = authoritativeProjectInput || lang == "sln";

                GitInfo.WorkspaceFileReadResult read =
                    GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, rel,
                        MaxIndexedFileBytes);
                byte[]? bytes = read.Bytes;
                if (bytes is null)
                {
                    if (read.Disposition == GitInfo.WorkspaceFileReadDisposition.Missing)
                    {
                        if (known)
                        {
                            store.DeleteFileCascade(tx, old!.Id,
                                store.GetContentForWrite(old.Id));
                            deleted++;
                            if (projectShapePath) projectDataDirty = true;
                        }
                        continue;
                    }
                    if (authoritativeProjectInput &&
                        read.Disposition == GitInfo.WorkspaceFileReadDisposition.Unavailable)
                    {
                        // Regular project/package inputs are authoritative. Publishing a graph
                        // that silently retained their old facts—or deleted them in a strict
                        // worktree sweep—would be a false-complete snapshot. The surrounding
                        // transaction rolls every preceding file mutation back.
                        throw new IOException(
                            $"Authoritative project input could not be captured safely: {rel}");
                    }
                    if (known && (removeUnavailableKnown ||
                        read.Disposition == GitInfo.WorkspaceFileReadDisposition.DefinitelyNonRegular))
                    {
                        // A pinned full sweep has proved the leaf non-regular, or the strict
                        // worktree reconcile could not obtain its complete bounded bytes. Remove
                        // any seeded row; retaining it would publish source evidence for bytes the
                        // target index did not inspect.
                        store.DeleteFileCascade(tx, old!.Id, store.GetContentForWrite(old.Id));
                        deleted++;
                        if (projectShapePath) projectDataDirty = true;
                    }
                    log?.Invoke($"Skipped non-regular, linked, unreadable, or oversized file: {rel}");
                    continue;
                }
                long mtimeTicks;
                try { mtimeTicks = File.GetLastWriteTimeUtc(full).Ticks; }
                catch { mtimeTicks = 0; }

                ulong hash = XxHash64.HashToUInt64(bytes);
                if (known && unchecked((long)hash) == old!.Hash)
                {
                    continue; // touched but identical
                }

                string content = DecodeUtf8(bytes);
                if (lang == "cs")
                {
                    var parsed = SyntaxIndexer.Parse(rel, content);
                    if (known)
                    {
                        string oldContent = store.GetContentForWrite(old!.Id) ?? "";
                        store.UpdateFileRow(tx, old.Id, bytes.LongLength, mtimeTicks, hash,
                            parsed.LineCount, parsed.LooksGenerated, parsed.HasTestAttributes);
                        store.ReplaceContent(tx, old.Id, oldContent, content);
                        store.DeleteSymbolsForFile(tx, old.Id);
                        store.InsertSymbols(tx, old.Id, parsed.Symbols);
                        RefreshBaseFactsForFile(store, tx, old.Id, content);
                        changed++;
                    }
                    else
                    {
                        long id = store.InsertFile(tx, rel, bytes.LongLength, mtimeTicks, hash,
                            "cs", parsed.LineCount, parsed.LooksGenerated, parsed.HasTestAttributes);
                        store.InsertContent(tx, id, content);
                        store.InsertSymbols(tx, id, parsed.Symbols);
                        added++;
                        // Attribute the new file incrementally instead of rebuilding the whole project
                        // graph per added .cs (zki: full disk walk + reparse of EVERY csproj). SAFE ONLY
                        // when every project's ownership is dir-prefix-derivable: a LEGACY explicit
                        // <Compile> list can claim a re-added file WITHOUT its csproj changing (git
                        // stash pop, branch switch — review-reproduced permanent ownership loss), and
                        // Include/Remove globs (3tz) can claim or exclude the new file in ways only the
                        // full rebuild re-evaluates. With any such shape present we fall back to the
                        // full rebuild, which itself no longer walks the disk.
                        hasLegacy ??= store.HasNonTrivialCompileShapes();
                        if (hasLegacy.Value)
                        {
                            projectDataDirty = true;
                        }
                        else
                        {
                            // Longest glob-root dir prefix wins, mirroring CompileItemResolver's walk
                            // exactly (including its root-dir quirk, so both attributions always agree).
                            globRoots ??= BuildGlobRoots(store);
                            string fdir = rel;
                            while (true)
                            {
                                int slash = fdir.LastIndexOf('/');
                                if (slash < 0) break;
                                fdir = fdir[..slash];
                                if (globRoots.TryGetValue(fdir, out long pid))
                                {
                                    store.InsertCompileItem(tx, pid, id);
                                    store.InsertVariantCompileItemsForProject(tx, pid, id, "evaluated");
                                    RefreshBaseFactsForFile(store, tx, id, content);
                                    break;
                                }
                            }
                        }
                    }
                }
                else
                {
                    int lines = content.Count(c => c == '\n') + 1;
                    if (known)
                    {
                        string oldContent = store.GetContentForWrite(old!.Id) ?? "";
                        store.UpdateFileRow(tx, old.Id, bytes.LongLength, mtimeTicks, hash, lines, false, false);
                        store.ReplaceContent(tx, old.Id, oldContent, content);
                        changed++;
                    }
                    else
                    {
                        long id = store.InsertFile(tx, rel, bytes.LongLength, mtimeTicks, hash, lang, lines, false, false);
                        store.InsertContent(tx, id, content);
                        added++;
                    }
                    if (projectShapePath) projectDataDirty = true;
                }
            }
            if (projectDataDirty)
            {
                log?.Invoke("Project files changed — rebuilding project graph ...");
                RefreshProjectDataCore(store, workspaceRoot, tx, log);
            }
            if (added + changed + deleted > 0)
            {
                refreshedAtUtc = DateTime.UtcNow.ToString("O");
                store.SetMeta(tx, "last_refresh_utc", refreshedAtUtc);
            }
            // Commit identity belongs to the same SQLite transaction as the rows it describes.
            // A follower can pin between writer transactions, so writing this afterward would let
            // it observe new rows with the previous commit metadata.
            if (recordCommit is not null)
            {
                store.SetMeta(tx, "indexed_commit", recordCommit);
                if (recordBranch is not null)
                    store.SetMeta(tx, "indexed_branch", recordBranch);
            }
            tx.Commit();
        }

        return new RefreshResult(changed, added, deleted, projectDataDirty, sw.Elapsed,
            refreshedAtUtc);
    }

    /// <summary>Same longest-dir-prefix map CompileItemResolver builds: non-legacy (SDK or failed-
    /// parse) project dirs, keyed for the added-file walk. Built at most once per refresh.</summary>
    private static Dictionary<string, long> BuildGlobRoots(IndexStore store)
    {
        var roots = new Dictionary<string, long>(WorkspacePaths.FileSystemPathComparer);
        foreach (var (id, relPath) in store.GlobRootProjects())
        {
            string dir = WorkspacePaths.ToGitPath(Path.GetDirectoryName(relPath) ?? "");
            roots[dir] = id;
        }
        return roots;
    }

    /// <summary>Re-parses all authoritative project files and rebuilds project tables + compile
    /// items atomically. Every project snapshot is bounded, no-follow, and hash-bound to the file
    /// row in the same transaction; one unavailable project aborts instead of publishing a partial
    /// graph. Solution membership remains optional metadata only.</summary>
    public static void RefreshProjectData(IndexStore store, string workspaceRoot, ScanResult? scan = null)
    {
        _ = scan;
        using var tx = store.BeginTransaction();
        RefreshProjectDataCore(store, workspaceRoot, tx, log: null);
        tx.Commit();
    }

    private static void RefreshProjectDataCore(IndexStore store, string workspaceRoot,
        Microsoft.Data.Sqlite.SqliteTransaction tx, Action<string>? log)
    {
        List<(long Id, string Path, string Lang)> rows = store.FileIdPathLang(tx);
        Dictionary<string, IndexStore.StoredFile> stored = store.AllFilesByPath(tx);
        List<string> projectPaths = rows.Where(row => row.Lang == "csproj")
            .Select(row => row.Path).OrderBy(path => path, StringComparer.Ordinal).ToList();
        List<string> solutionPaths = rows.Where(row => row.Lang == "sln")
            .Select(row => row.Path).OrderBy(path => path, StringComparer.Ordinal).ToList();
        var parsedProjects = new List<ParsedProject>(projectPaths.Count);
        var variantEvaluations = new Dictionary<string, ProjectVariantEvaluation>(
            WorkspacePaths.FileSystemPathComparer);
        foreach (string path in projectPaths)
        {
            byte[]? projectBytes = GitInfo.ReadBoundedWorkspaceFile(workspaceRoot, path,
                IndexBuilder.MaxStructuralFileBytes);
            if (projectBytes is null ||
                !stored.TryGetValue(path, out IndexStore.StoredFile? projectRow) ||
                unchecked((long)XxHash64.HashToUInt64(projectBytes)) != projectRow.Hash)
                throw new IOException($"Project snapshot changed or could not be captured safely: {path}");
            string packagesPath = PackagesConfigPath(path);
            byte[]? packagesBytes = null;
            if (stored.TryGetValue(packagesPath, out IndexStore.StoredFile? packagesRow))
            {
                packagesBytes = GitInfo.ReadBoundedWorkspaceFile(workspaceRoot, packagesPath,
                    IndexBuilder.MaxStructuralFileBytes);
                if (packagesBytes is null ||
                    unchecked((long)XxHash64.HashToUInt64(packagesBytes)) != packagesRow.Hash)
                    throw new IOException($"packages.config changed or could not be captured safely: {packagesPath}");
            }
            parsedProjects.Add(ProjectFileParser.ParseSnapshot(path, projectBytes,
                packagesBytes));
            variantEvaluations[path] = ProjectVariantEvaluator.Evaluate(path, projectBytes,
                packagesBytes);
        }

        var parsedSolutions = new List<ParsedSolution>();
        long optionalSolutionBytes = 0;
        foreach (string path in solutionPaths)
        {
            if (!stored.TryGetValue(path, out IndexStore.StoredFile? row)) continue;
            byte[]? bytes = GitInfo.ReadBoundedWorkspaceFile(workspaceRoot, path,
                IndexBuilder.MaxStructuralFileBytes);
            if (bytes is null || unchecked((long)XxHash64.HashToUInt64(bytes)) != row.Hash ||
                optionalSolutionBytes + bytes.LongLength >
                IndexBuilder.MaxOptionalSolutionSnapshotBytes)
            {
                log?.Invoke($"Skipped unavailable optional solution metadata: {path}");
                continue;
            }
            parsedSolutions.Add(SolutionParser.ParseSnapshot(path, bytes));
            optionalSolutionBytes += bytes.LongLength;
        }

        var csFileIds = new Dictionary<string, long>(WorkspacePaths.FileSystemPathComparer);
        foreach (var (id, path, lang) in rows)
        {
            if (lang == "cs") csFileIds[path] = id;
        }

        store.ClearProjectData(tx);

        var projectIds = new Dictionary<string, long>(WorkspacePaths.FileSystemPathComparer);
        foreach (var p in parsedProjects)
        {
            projectIds[p.RelPath] = store.InsertProject(tx, p);
        }
        Dictionary<string, long> fileIds = stored.ToDictionary(pair => pair.Key,
            pair => pair.Value.Id, WorkspacePaths.FileSystemPathComparer);
        VariantWriteState variantState = VariantIndexWriter.WriteProjectFacts(store, tx,
            variantEvaluations, projectIds, fileIds);
        foreach (var p in parsedProjects)
        {
            long fromId = projectIds[p.RelPath];
            foreach (var r in p.ProjectRefRelPaths)
            {
                if (projectIds.TryGetValue(r, out long toId)) store.InsertProjectRef(tx, fromId, toId);
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
        // Assembly-ref edge recovery must mirror the full build (lhg) — a csproj touch rebuilds
        // the whole graph here, and losing the recovered edges would silently re-break
        // cross-project implementations/references until the next full rebuild.
        AssemblyRefEdges.Write(store, tx, parsedProjects, projectIds);
        CompileItemResolver.Write(store, tx, parsedProjects, projectIds, csFileIds);
        VariantIndexWriter.WriteCompileAndBaseFacts(store, tx, variantState, projectIds);
        // isTest R3 parity with the full build (a .cs file GAINING [TestFixture] converges on the
        // next graph rebuild — csproj-touch or full — an accepted staleness, same as ownership).
        store.PromoteTestProjectsByCompiledAttributes(tx);
    }

    private static void RefreshBaseFactsForFile(IndexStore store,
        Microsoft.Data.Sqlite.SqliteTransaction tx, long fileId, string content)
    {
        store.DeleteBaseTypeFactsForFile(tx, fileId);
        foreach (var context in store.ParseContextsForFileForWrite(tx, fileId))
        {
            var parseContext = new BaseTypeParseContext(context.LanguageVersion, context.Symbols);
            foreach (BaseTypeFact fact in BaseTypeIndexer.Parse(content, parseContext))
                store.InsertBaseTypeFact(tx, context.Id, fileId, fact.DeclarationOccurrence,
                    fact.Ordinal, fact.RawTypeText, fact.LookupName, fact.SyntacticArity,
                    fact.QualifierText, fact.ResolutionKind, fact.ScopeEvidence);
        }
    }

    private static string LangOf(string relPath)
    {
        string ext = Path.GetExtension(relPath).ToLowerInvariant();
        string name = Path.GetFileName(relPath);
        return ext switch
        {
            ".cs" => "cs",
            ".csproj" => "csproj",
            ".sln" or ".slnx" or ".slnf" => "sln",
            ".config" or ".props" or ".targets" or ".projitems" => "config",
            ".json" when name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("global.json", StringComparison.OrdinalIgnoreCase) => "config",
            _ => name.Equals("packages.config", StringComparison.OrdinalIgnoreCase) ? "config" : "other",
        };
    }

    private static bool IsPackagesConfig(string relPath) =>
        Path.GetFileName(relPath).Equals("packages.config",
            StringComparison.OrdinalIgnoreCase);

    private static string DecodeUtf8(byte[] bytes)
    {
        string s = Encoding.UTF8.GetString(bytes);
        return s.Length > 0 && s[0] == (char)0xFEFF ? s[1..] : s;
    }

    private static string PackagesConfigPath(string projectPath)
    {
        int slash = projectPath.LastIndexOf('/');
        return slash < 0 ? "packages.config" : projectPath[..(slash + 1)] + "packages.config";
    }
}
