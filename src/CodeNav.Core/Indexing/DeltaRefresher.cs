using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using CodeNav.Core.Discovery;

namespace CodeNav.Core.Indexing;

public sealed record RefreshResult(
    int ChangedFiles, int AddedFiles, int DeletedFiles, bool ProjectsRefreshed, TimeSpan Elapsed,
    string? RefreshedAtUtc = null);

internal sealed class RefreshInputUnavailableException(string path) : IOException(
    $"Workspace input could not be captured safely: {path}")
{
    internal string Path { get; } = path;
}

internal sealed class RefreshInputOversizedException(string path) : IOException(
    $"Workspace input exceeds its bounded capture limit: {path}")
{
    internal string Path { get; } = path;
}

/// <summary>
/// Owns: incremental index updates — targeted (watcher batches) or detect-all
/// (mtime/size/hash sweep). Single-writer: callers must serialize invocations.
/// Does not own: watching (WorkspaceWatcher) or lifecycle/status (IndexManager).
/// </summary>
public static class DeltaRefresher
{
    internal const int MaxIndexedFileBytes = 256 * 1024 * 1024;
    internal static readonly TimeSpan[] RefreshInputRetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1),
    ];

    public static RefreshResult Refresh(
        IndexStore store, string workspaceRoot, IReadOnlyCollection<string>? changedRelPaths,
        Action<string>? log = null,
        string? recordCommit = null, string? recordBranch = null,
        bool recordBranchKnown = false) =>
        RefreshCore(store, workspaceRoot, changedRelPaths,
            GitInfo.ReadBoundedWorkspaceFileResult, log, recordCommit, recordBranch,
            recordBranchKnown);

    internal static RefreshResult RefreshWithReaderForTest(
        IndexStore store, string workspaceRoot, IReadOnlyCollection<string>? changedRelPaths,
        Func<string, string, int, GitInfo.WorkspaceFileReadResult> readWorkspaceFile,
        Action<string>? log = null,
        string? recordCommit = null, string? recordBranch = null,
        bool recordBranchKnown = false) =>
        RefreshCore(store, workspaceRoot, changedRelPaths, readWorkspaceFile, log,
            recordCommit, recordBranch, recordBranchKnown);

    private static RefreshResult RefreshCore(
        IndexStore store, string workspaceRoot, IReadOnlyCollection<string>? changedRelPaths,
        Func<string, string, int, GitInfo.WorkspaceFileReadResult> readWorkspaceFile,
        Action<string>? log, string? recordCommit, string? recordBranch,
        bool recordBranchKnown)
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
            foreach (var f in scan.CsFiles.Concat(scan.FsFiles)
                         .Concat(scan.MarkdownFiles).Concat(scan.SqlFiles).Concat(scan.ProjectFiles)
                         .Concat(scan.SolutionFiles).Concat(scan.ConfigFiles))
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
        var globRootsByLanguage = new Dictionary<string, Dictionary<string, long>>(
            StringComparer.Ordinal);
        var hasNonTrivialByLanguage = new Dictionary<string, bool>(StringComparer.Ordinal);

        // F# syntax parsing is intentionally prepared before the SQLite writer transaction.
        // Candidate reads are reused below so persistence publishes the exact bytes that were
        // parsed; a later filesystem mutation is attributed by the normal post-refresh sweep.
        var capturedFSharpCandidates = new Dictionary<string, GitInfo.WorkspaceFileReadResult>(
            WorkspacePaths.FileSystemPathComparer);
        foreach (string rel in candidates)
        {
            if (!detectAll && WorkspaceScanner.IsExcludedPath(rel)) continue;
            string language = LangOf(rel);
            if (language is not ("fs" or "fsproj")) continue;
            if (!WorkspacePaths.TryResolveGitPathInside(workspaceRoot, rel, out _)) continue;
            int captureLimit = language == "fsproj"
                ? IndexBuilder.MaxStructuralFileBytes
                : MaxIndexedFileBytes;
            capturedFSharpCandidates[rel] =
                readWorkspaceFile(workspaceRoot, rel, captureLimit);
        }
        PreparedFSharpReindex preparedFSharp = PrepareFSharpReindex(
            store, candidates, stored, capturedFSharpCandidates, log);

        string? refreshedAtUtc = null;
        using (var tx = store.BeginTransaction())
        {
            void AttributeAddedSource(string sourceLanguage, string relPath, long fileId)
            {
                if (!hasNonTrivialByLanguage.TryGetValue(sourceLanguage, out bool nonTrivial))
                {
                    nonTrivial = store.HasNonTrivialCompileShapes(sourceLanguage);
                    hasNonTrivialByLanguage[sourceLanguage] = nonTrivial;
                }
                if (nonTrivial)
                {
                    projectDataDirty = true;
                    return;
                }

                if (!CompileItemResolver.IsImplicitDefaultSource(relPath)) return;

                if (!globRootsByLanguage.TryGetValue(sourceLanguage, out var roots))
                {
                    roots = BuildGlobRoots(store, sourceLanguage);
                    globRootsByLanguage[sourceLanguage] = roots;
                }
                string fileDir = relPath;
                while (true)
                {
                    int slash = fileDir.LastIndexOf('/');
                    fileDir = slash < 0 ? "" : fileDir[..slash];
                    if (roots.TryGetValue(fileDir, out long projectId))
                    {
                        store.InsertCompileItem(tx, projectId, fileId);
                        break;
                    }
                    if (slash < 0) break;
                }
            }

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
                bool authoritativeProjectInput = lang is "csproj" or "fsproj" || IsPackagesConfig(rel);
                bool projectShapePath = authoritativeProjectInput || lang == "sln";

                int captureLimit = authoritativeProjectInput
                    ? IndexBuilder.MaxStructuralFileBytes
                    : MaxIndexedFileBytes;
                GitInfo.WorkspaceFileReadResult read =
                    capturedFSharpCandidates.TryGetValue(rel, out var captured)
                        ? captured
                        : readWorkspaceFile(workspaceRoot, rel, captureLimit);
                byte[]? bytes = read.Bytes;
                if (bytes is null)
                {
                    if (read.Disposition == GitInfo.WorkspaceFileReadDisposition.Missing)
                    {
                        if (known)
                        {
                            store.DeleteFileCascade(tx, old!.Id,
                                store.GetContentForWrite(old.Id, tx));
                            deleted++;
                            if (projectShapePath)
                            {
                                projectDataDirty = true;
                            }
                        }
                        continue;
                    }
                    if (read.Disposition == GitInfo.WorkspaceFileReadDisposition.Oversized)
                    {
                        // Oversize is persistent rather than transient, but it is still incomplete
                        // source evidence. Throw inside the transaction so preceding row mutations
                        // roll back before the manager publishes the persistent stale latch.
                        throw new RefreshInputOversizedException(rel);
                    }
                    if (read.Disposition == GitInfo.WorkspaceFileReadDisposition.Unavailable)
                    {
                        // A regular source that exists but cannot be captured completely is a
                        // transient refresh failure, not evidence that the old row is current.
                        // Throw inside the transaction so the manager can retry the complete
                        // request without publishing a partial batch.
                        throw new RefreshInputUnavailableException(rel);
                    }
                    if (known &&
                        read.Disposition == GitInfo.WorkspaceFileReadDisposition.DefinitelyNonRegular)
                    {
                        // A pinned read proved the leaf non-regular. Remove any seeded row;
                        // retaining it would publish source evidence for a refused file type.
                        store.DeleteFileCascade(tx, old!.Id,
                            store.GetContentForWrite(old.Id, tx));
                        deleted++;
                        if (projectShapePath)
                        {
                            projectDataDirty = true;
                        }
                    }
                    log?.Invoke($"Skipped definitely non-regular file: {rel}");
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
                        string oldContent = store.GetContentForWrite(old!.Id, tx) ?? "";
                        store.UpdateFileRow(tx, old.Id, bytes.LongLength, mtimeTicks, hash,
                            parsed.LineCount, parsed.LooksGenerated, parsed.HasTestAttributes);
                        store.ReplaceContent(tx, old.Id, oldContent, content);
                        store.DeleteSymbolsForFile(tx, old.Id);
                        store.InsertSymbols(tx, old.Id, parsed.Symbols);
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
                        AttributeAddedSource("cs", rel, id);
                    }
                }
                else
                {
                    int lines = content.Count(c => c == '\n') + 1;
                    bool isGenerated = lang == "fs" &&
                        FileClassifier.LooksGenerated(rel, content);
                    if (known)
                    {
                        string oldContent = store.GetContentForWrite(old!.Id, tx) ?? "";
                        store.UpdateFileRow(tx, old.Id, bytes.LongLength, mtimeTicks, hash, lines,
                            isGenerated, false);
                        store.ReplaceContent(tx, old.Id, oldContent, content);
                        changed++;
                    }
                    else
                    {
                        long id = store.InsertFile(tx, rel, bytes.LongLength, mtimeTicks, hash,
                            lang, lines, isGenerated, false);
                        store.InsertContent(tx, id, content);
                        added++;
                        if (lang == "fs")
                        {
                            AttributeAddedSource("fs", rel, id);
                        }
                    }
                    if (projectShapePath)
                    {
                        projectDataDirty = true;
                    }
                }
            }
            if (projectDataDirty)
            {
                log?.Invoke("Project files changed — rebuilding project graph ...");
                RefreshProjectDataCore(store, workspaceRoot, tx, readWorkspaceFile, log);
            }

            if (preparedFSharp.ParsedFiles.Count > 0 || preparedFSharp.CoverageOnly.Count > 0)
            {
                Dictionary<string, (long Id, string Lang)> indexedFiles =
                    store.FileIdPathLang(tx).ToDictionary(row => row.Path,
                        row => (row.Id, row.Lang), WorkspacePaths.FileSystemPathComparer);
                foreach ((string path, ParsedFSharpFile parsed) in preparedFSharp.ParsedFiles
                             .OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    if (!indexedFiles.TryGetValue(path, out var indexedFile) ||
                        indexedFile.Lang != "fs" ||
                        !IsFSharpDeclarationFile(path))
                    {
                        continue;
                    }
                    store.DeleteSymbolsForFile(tx, indexedFile.Id);
                    store.InsertSymbols(tx, indexedFile.Id, parsed.Symbols);
                    store.ReplaceFSharpParseCoverage(tx, indexedFile.Id, parsed);
                }
                foreach ((string path, FSharpIndexCoverage coverage) in preparedFSharp.CoverageOnly
                             .OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    if (!indexedFiles.TryGetValue(path, out var indexedFile) ||
                        indexedFile.Lang != "fs" || !IsFSharpDeclarationFile(path)) continue;
                    store.ReplaceFSharpParseCoverage(tx, indexedFile.Id, coverage);
                }
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
                if (recordBranchKnown || recordBranch is not null)
                {
                    if (recordBranch is not null)
                        store.SetMeta(tx, "indexed_branch", recordBranch);
                    else
                        store.DeleteMeta(tx, "indexed_branch");
                }
            }
            tx.Commit();
        }

        return new RefreshResult(changed, added, deleted, projectDataDirty, sw.Elapsed,
            refreshedAtUtc);
    }

    private sealed record FSharpProjectInput(
        ParsedProject Project,
        string Xml,
        FSharpParsingContextSelection ContextSelection);

    private sealed record PreparedFSharpReindex(
        Dictionary<string, ParsedFSharpFile> ParsedFiles,
        Dictionary<string, FSharpIndexCoverage> CoverageOnly)
    {
        internal static PreparedFSharpReindex Empty { get; } = new(
            new(WorkspacePaths.FileSystemPathComparer),
            new(WorkspacePaths.FileSystemPathComparer));
    }

    private static PreparedFSharpReindex PrepareFSharpReindex(
        IndexStore store,
        IReadOnlyCollection<string> candidates,
        IReadOnlyDictionary<string, IndexStore.StoredFile> stored,
        IReadOnlyDictionary<string, GitInfo.WorkspaceFileReadResult> capturedCandidates,
        Action<string>? log)
    {
        var changedProjects = new HashSet<string>(WorkspacePaths.FileSystemPathComparer);
        var changedSources = new HashSet<string>(WorkspacePaths.FileSystemPathComparer);
        foreach (string path in candidates)
        {
            string language = LangOf(path);
            if (language is not ("fs" or "fsproj")) continue;
            if (!capturedCandidates.TryGetValue(path, out var read)) continue;
            bool known = stored.TryGetValue(path, out IndexStore.StoredFile? old);
            bool changed = read.Bytes is { } bytes
                ? !known || unchecked((long)XxHash64.HashToUInt64(bytes)) != old!.Hash
                : known && read.Disposition is
                    GitInfo.WorkspaceFileReadDisposition.Missing or
                    GitInfo.WorkspaceFileReadDisposition.DefinitelyNonRegular;
            if (!changed) continue;
            if (language == "fsproj") changedProjects.Add(path);
            else if (IsFSharpDeclarationFile(path)) changedSources.Add(path);
        }
        if (changedProjects.Count == 0 && changedSources.Count == 0)
            return PreparedFSharpReindex.Empty;

        Dictionary<string, IndexStore.FSharpParsingProjectSnapshot> storedProjectSnapshots =
            store.FSharpParsingProjects().ToDictionary(project => project.Path,
                WorkspacePaths.FileSystemPathComparer);
        var projectPaths = storedProjectSnapshots.Keys
            .ToHashSet(WorkspacePaths.FileSystemPathComparer);
        foreach (string projectPath in changedProjects)
        {
            if (capturedCandidates[projectPath].Bytes is null)
                projectPaths.Remove(projectPath);
            else
                projectPaths.Add(projectPath);
        }

        var projectInputsByPath = new Dictionary<string, FSharpProjectInput>(
            WorkspacePaths.FileSystemPathComparer);
        FSharpProjectInput? ProjectInput(string projectPath)
        {
            if (projectInputsByPath.TryGetValue(projectPath, out var cached)) return cached;
            byte[]? projectBytes = changedProjects.Contains(projectPath)
                ? capturedCandidates[projectPath].Bytes
                : storedProjectSnapshots.TryGetValue(projectPath, out var snapshot)
                    ? Encoding.UTF8.GetBytes(snapshot.Xml)
                    : null;
            if (projectBytes is null) return null;
            string xml = DecodeUtf8(projectBytes);
            ParsedProject project = ProjectFileParser.ParseSnapshot(projectPath, projectBytes);
            var input = new FSharpProjectInput(project, xml,
                FSharpSyntaxIndexer.ParsingContextsForProject(
                    project.RelPath, project.TargetFrameworks, xml));
            projectInputsByPath[projectPath] = input;
            return input;
        }

        var sourcePaths = stored.Values.Where(file =>
                file.Lang == "fs" && IsFSharpDeclarationFile(file.Path))
            .Select(file => file.Path)
            .ToHashSet(WorkspacePaths.FileSystemPathComparer);
        foreach (string sourcePath in changedSources)
        {
            if (capturedCandidates[sourcePath].Bytes is null)
                sourcePaths.Remove(sourcePath);
            else
                sourcePaths.Add(sourcePath);
        }

        List<(string ProjectPath, string FilePath)> oldOwnership = store.FSharpOwnership();
        var affectedPaths = new HashSet<string>(changedSources,
            WorkspacePaths.FileSystemPathComparer);
        foreach ((string projectPath, string filePath) in oldOwnership)
        {
            if (changedProjects.Contains(projectPath)) affectedPaths.Add(filePath);
        }

        ParsedProject[] changedProjectModels = changedProjects
            .Select(ProjectInput).Where(input => input is not null)
            .Select(input => input!.Project).ToArray();
        Dictionary<string, List<ParsedProject>> changedProjectOwners =
            CompileItemResolver.ResolveOwners(changedProjectModels, sourcePaths);
        foreach ((string filePath, List<ParsedProject> owners) in changedProjectOwners)
        {
            if (owners.Count > 0) affectedPaths.Add(filePath);
        }
        var parsePaths = new HashSet<string>(affectedPaths,
            WorkspacePaths.FileSystemPathComparer);

        // A project whose options cannot be selected has unknowable linked-file ownership, so cold
        // build conservatively applies that authority gap to every F# declaration file. Adding,
        // deleting, or repairing such a project changes the global coverage set even when no known
        // compile edge names a particular file. Refresh every F# declaration's coverage only on
        // that rare transition; files whose parser-context set changes are reparsed, while the
        // others retain their symbol rows. Ordinary valid project edits remain scoped to their
        // old/new owned files.
        bool globalFailureAuthorityChanged = changedProjects.Any(projectPath =>
        {
            FSharpParsingContextSelection? oldSelection =
                storedProjectSnapshots.TryGetValue(projectPath, out var oldSnapshot)
                    ? FSharpSyntaxIndexer.ParsingContextsForProject(
                        oldSnapshot.Path, oldSnapshot.TargetFrameworks, oldSnapshot.Xml)
                    : null;
            FSharpParsingContextSelection? newSelection = ProjectInput(projectPath)?.ContextSelection;
            return !StringComparer.Ordinal.Equals(
                GlobalFailureFingerprint(oldSelection),
                GlobalFailureFingerprint(newSelection));
        });
        if (globalFailureAuthorityChanged) affectedPaths.UnionWith(sourcePaths);

        bool hasAddedSource = changedSources.Any(path => !stored.ContainsKey(path) &&
            sourcePaths.Contains(path));
        var neededProjectPaths = new HashSet<string>(changedProjects,
            WorkspacePaths.FileSystemPathComparer);
        foreach ((string projectPath, string filePath) in oldOwnership)
        {
            if (affectedPaths.Contains(filePath) && !changedProjects.Contains(projectPath))
                neededProjectPaths.Add(projectPath);
        }
        foreach (var snapshot in storedProjectSnapshots.Values)
        {
            if (snapshot.LoadStatus.StartsWith("failed:", StringComparison.Ordinal))
                neededProjectPaths.Add(snapshot.Path);
        }
        if (hasAddedSource) neededProjectPaths.UnionWith(projectPaths);
        foreach (string projectPath in neededProjectPaths)
            _ = ProjectInput(projectPath);

        Dictionary<string, List<ParsedProject>> newOwners;
        if (hasAddedSource)
        {
            newOwners = CompileItemResolver.ResolveOwners(
                projectInputsByPath.Values.Select(input => input.Project).ToArray(),
                sourcePaths);
        }
        else
        {
            newOwners = new Dictionary<string, List<ParsedProject>>(
                WorkspacePaths.FileSystemPathComparer);
            void AddOwner(string filePath, ParsedProject owner)
            {
                if (!newOwners.TryGetValue(filePath, out List<ParsedProject>? owners))
                    newOwners[filePath] = owners = [];
                if (!owners.Any(existing => WorkspacePaths.FileSystemPathComparer.Equals(
                        existing.RelPath, owner.RelPath)))
                    owners.Add(owner);
            }
            foreach ((string projectPath, string filePath) in oldOwnership)
            {
                if (!affectedPaths.Contains(filePath) || changedProjects.Contains(projectPath) ||
                    !projectInputsByPath.TryGetValue(projectPath, out var input)) continue;
                AddOwner(filePath, input.Project);
            }
            foreach ((string filePath, List<ParsedProject> owners) in changedProjectOwners)
            {
                foreach (ParsedProject owner in owners) AddOwner(filePath, owner);
            }
        }

        List<FSharpProjectInput> projectInputs = projectInputsByPath.Values.ToList();
        var inputsByProject = projectInputsByPath;
        Dictionary<string, IndexStore.StoredFSharpParseCoverage> storedCoverage =
            globalFailureAuthorityChanged
                ? store.FSharpParseCoverageByPath()
                : new(WorkspacePaths.FileSystemPathComparer);
        var parsedFiles = new Dictionary<string, ParsedFSharpFile>(
            WorkspacePaths.FileSystemPathComparer);
        var coverageOnly = new Dictionary<string, FSharpIndexCoverage>(
            WorkspacePaths.FileSystemPathComparer);
        bool progressStarted = affectedPaths.Count > 0;
        if (progressStarted)
            log?.Invoke($"Preparing F# declaration reindex for {affectedPaths.Count} file(s) ...");
        foreach (string path in affectedPaths.OrderBy(path => path, StringComparer.Ordinal))
        {
            if (!sourcePaths.Contains(path)) continue;
            var selections = new List<FSharpParsingContextSelection>();
            bool hasValidOwner = newOwners.TryGetValue(path, out List<ParsedProject>? owners) &&
                                 owners.Count > 0;
            if (hasValidOwner)
            {
                selections.AddRange(owners!.Select(owner =>
                    inputsByProject[owner.RelPath].ContextSelection));
            }
            selections.AddRange(projectInputs
                .Where(input => input.ContextSelection.FailedProjects > 0)
                .Select(input => input.ContextSelection));
            FSharpParsingContextSelection selection = selections.Count == 0
                ? FSharpParsingContextSelection.Unowned
                : FSharpSyntaxIndexer.CombineParsingContexts(selections.Distinct());

            // A newly added/removed globally failed project contributes no parser contexts. For a
            // file with an unchanged valid owner set, only its option-coverage counters change;
            // retain the existing symbols and FCS failure count instead of reparsing identical
            // contexts. Orphans and direct old/new-owner changes still require a real parse.
            bool updateCoverageOnly = globalFailureAuthorityChanged &&
                                      !parsePaths.Contains(path) && hasValidOwner;
            if (updateCoverageOnly)
            {
                int failedContexts = storedCoverage.TryGetValue(path, out var oldCoverage)
                    ? oldCoverage.FailedContexts
                    : 0;
                coverageOnly[path] = new FSharpIndexCoverage(
                    selection.Contexts.Count,
                    failedContexts,
                    selection.ProjectCount,
                    selection.FailedProjects,
                    selection.PartialProjects,
                    selection.PartialReasons);
                continue;
            }

            string? content = changedSources.Contains(path)
                ? capturedCandidates[path].Bytes is { } bytes ? DecodeUtf8(bytes) : null
                : stored.TryGetValue(path, out IndexStore.StoredFile? storedFile)
                    ? store.GetContentForWrite(storedFile.Id)
                    : null;
            if (content is null) continue;
            parsedFiles[path] = FSharpSyntaxIndexer.Parse(path, content, selection);
        }
        if (progressStarted)
            log?.Invoke($"Prepared F# declaration reindex: {parsedFiles.Count} parsed, " +
                        $"{coverageOnly.Count} coverage-only file(s).");
        return new PreparedFSharpReindex(parsedFiles, coverageOnly);

        static string GlobalFailureFingerprint(FSharpParsingContextSelection? selection)
        {
            if (selection is null || selection.FailedProjects == 0) return "";
            return string.Join('\u001f', selection.PartialReasons
                .Distinct(StringComparer.Ordinal)
                .OrderBy(reason => reason, StringComparer.Ordinal));
        }
    }

    /// <summary>Same longest-dir-prefix map CompileItemResolver builds: non-legacy (SDK or failed-
    /// parse) project dirs, keyed for the added-file walk. Built at most once per refresh.</summary>
    private static Dictionary<string, long> BuildGlobRoots(IndexStore store, string language)
    {
        var roots = new Dictionary<string, long>(WorkspacePaths.FileSystemPathComparer);
        foreach (var (id, relPath) in store.GlobRootProjects(language))
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
        RefreshProjectDataCore(store, workspaceRoot, tx,
            GitInfo.ReadBoundedWorkspaceFileResult, log: null);
        tx.Commit();
    }

    private static void RefreshProjectDataCore(IndexStore store, string workspaceRoot,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        Func<string, string, int, GitInfo.WorkspaceFileReadResult> readWorkspaceFile,
        Action<string>? log)
    {
        List<(long Id, string Path, string Lang)> rows = store.FileIdPathLang(tx);
        Dictionary<string, IndexStore.StoredFile> stored = store.AllFilesByPath(tx);
        List<string> projectPaths = rows.Where(row => row.Lang is "csproj" or "fsproj")
            .Select(row => row.Path).OrderBy(path => path, StringComparer.Ordinal).ToList();
        List<string> solutionPaths = rows.Where(row => row.Lang == "sln")
            .Select(row => row.Path).OrderBy(path => path, StringComparer.Ordinal).ToList();
        var parsedProjects = new List<ParsedProject>(projectPaths.Count);
        foreach (string path in projectPaths)
        {
            byte[] projectBytes = ReadRequiredStructuralSnapshot(workspaceRoot, path, stored,
                readWorkspaceFile);
            string packagesPath = PackagesConfigPath(path);
            byte[]? packagesBytes = null;
            if (stored.ContainsKey(packagesPath))
            {
                packagesBytes = ReadRequiredStructuralSnapshot(workspaceRoot, packagesPath,
                    stored, readWorkspaceFile);
            }
            parsedProjects.Add(ProjectFileParser.ParseSnapshot(path, projectBytes,
                packagesBytes));
        }

        var parsedSolutions = new List<ParsedSolution>();
        long optionalSolutionBytes = 0;
        foreach (string path in solutionPaths)
        {
            if (!stored.TryGetValue(path, out IndexStore.StoredFile? row)) continue;
            GitInfo.WorkspaceFileReadResult read = readWorkspaceFile(workspaceRoot, path,
                IndexBuilder.MaxStructuralFileBytes);
            byte[]? bytes = read.Bytes;
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

        var sourceFileIds = new Dictionary<string, long>(WorkspacePaths.FileSystemPathComparer);
        foreach (var (id, path, lang) in rows)
        {
            if (lang is "cs" or "fs") sourceFileIds[path] = id;
        }

        store.ClearProjectData(tx);

        var projectIds = new Dictionary<string, long>(WorkspacePaths.FileSystemPathComparer);
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
        CompileItemResolver.Write(store, tx, parsedProjects, projectIds, sourceFileIds);
        // isTest R3 parity with the full build (a .cs file GAINING [TestFixture] converges on the
        // next graph rebuild — csproj-touch or full — an accepted staleness, same as ownership).
        store.PromoteTestProjectsByCompiledAttributes(tx);
    }

    private static byte[] ReadRequiredStructuralSnapshot(
        string workspaceRoot,
        string path,
        IReadOnlyDictionary<string, IndexStore.StoredFile> stored,
        Func<string, string, int, GitInfo.WorkspaceFileReadResult> readWorkspaceFile)
    {
        GitInfo.WorkspaceFileReadResult read = readWorkspaceFile(workspaceRoot, path,
            IndexBuilder.MaxStructuralFileBytes);
        if (read.Disposition == GitInfo.WorkspaceFileReadDisposition.Oversized)
            throw new RefreshInputOversizedException(path);
        if (read.Bytes is not { } bytes ||
            !stored.TryGetValue(path, out IndexStore.StoredFile? row) ||
            unchecked((long)XxHash64.HashToUInt64(bytes)) != row.Hash)
            throw new RefreshInputUnavailableException(path);
        return bytes;
    }

    private static string LangOf(string relPath)
    {
        string ext = Path.GetExtension(relPath).ToLowerInvariant();
        string name = Path.GetFileName(relPath);
        return ext switch
        {
            ".cs" => "cs",
            ".fs" or ".fsi" or ".fsx" => "fs",
            ".md" => "md",
            ".sql" => "sql",
            ".csproj" => "csproj",
            ".fsproj" => "fsproj",
            ".sln" or ".slnx" or ".slnf" => "sln",
            ".config" or ".props" or ".targets" => "config",
            ".json" when name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("global.json", StringComparison.OrdinalIgnoreCase) => "config",
            _ => name.Equals("packages.config", StringComparison.OrdinalIgnoreCase) ? "config" : "other",
        };
    }

    private static bool IsFSharpDeclarationFile(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".fs", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".fsi", StringComparison.OrdinalIgnoreCase);
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
