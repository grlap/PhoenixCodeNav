using System.ComponentModel;
using System.Diagnostics;
using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp;

/// <summary>
/// Owns: review_pack (91u) — the review-system centerpiece: ONE budget-bounded call that maps
/// a diff to touched symbols to per-symbol impact digests. The alternative was ~30 round trips
/// per review (git diff -> symbol_at per hunk -> impact/related_tests/references per symbol),
/// re-orchestrated by every consumer skill. Pipeline: changed set (git diff -U0 against a
/// validated base, UNION untracked dirt — or an explicit path list) -> hunk ranges -> symbol
/// SPAN intersection in the index (one SQL per file, innermost-symbol policy via ParentId) ->
/// digests reusing the shipped pieces (handles, dependent split incl. viaHintPathOnly,
/// transitive count, publicApi, related_tests with signal, indexed reference candidates,
/// deterministic risks). Deleted files get DELETION honesty: the post-change index cannot know
/// removed symbols, so the base blob (read-only `git show`) is re-parsed IN MEMORY and each
/// former top-level type reports its dangling reference candidates.
/// Honesty contract: everything here is INDEXED confidence and says so (notes carry stable ids
/// — a0b); no semantic resolution inside (unbounded latency) — every digest ships its handle so
/// the caller escalates chosen symbols via references(symbolId, mode:'semantic').
/// Does not own: git mechanics (GitInfo), span queries (IndexQueries), or worktree lifecycle.
/// Split out of: nothing — new for 91u.
/// </summary>
public sealed partial class NavigationTools
{
    internal Func<GlobMatchBudget>? ReviewProjectGlobBudgetFactoryForTest;
    private const int ReviewMaxFiles = 200;        // changed .cs files mapped to symbols
    private const int ReviewMaxUnsupportedLanguageFiles = 200;
    private const int ReviewMaxDeletedFiles = 20;  // deleted files re-parsed from the base blob
    private const int ReviewMaxTypesPerDeleted = 5;
    private const int ReviewMaxFormerSymbols = 100;
    private const int ReviewMaxBaseBlobBytes = 512 * 1024;
    private const int ReviewMaxBaseBlobChars = 4 * 1024 * 1024;
    private const int ReviewMaxBaseBlobAttempts = 32;
    private const int ReviewMaxBaseBlobMilliseconds = 5_000;
    private const int ReviewMaxNamespaceFiles = 32;
    private const int ReviewMaxNamespaceChars = 2 * 1024 * 1024;
    private const int ReviewMaxNamespacePerFileChars = 512 * 1024;
    private const int ReviewMaxNamespaceMilliseconds = 2_000;
    private const int ReviewMaxProjectShapeFiles = 256;
    private const int ReviewMaxProjectShapeBytes = 8 * 1024 * 1024;
    private const int ReviewMaxProjectShapePerFileBytes = 256 * 1024;
    private const int ReviewMaxProjectShapeMilliseconds = 2_000;
    private const int ReviewMaxProjectGlobSegments = 256;
    private const long ReviewMaxProjectGlobOperations = 1_000_000;

    internal static bool BaseBlobTimeBudgetHitAfterAttempt(string? content,
        long elapsedMilliseconds) => content is null &&
        ReviewMaxBaseBlobMilliseconds - elapsedMilliseconds < 4;

    private sealed record ReviewNote(string Id, string Text);
    private sealed record ReviewDiffRange(int Start, int Count);
    private readonly record struct ReviewUnmappedKey(string Path, ReviewDiffRange? Old,
        ReviewDiffRange? New);
    private sealed record ReviewUnmappedHunk(string Path, int Start, int? End, string Reason,
        string Side, ReviewDiffRange? Old, ReviewDiffRange? New,
        List<string>? AdditionalReasons = null);
    private sealed record ReviewReferenceCoverage(int Scanned, int AtLeast, int Limit,
        bool? DeclarationExclusionBudgetHit = null, int? DeclarationFilesParsed = null,
        int? DeclarationFileParseLimit = null, int? DeclarationCharsParsed = null,
        int? DeclarationCharLimit = null, int? DeclarationPerFileCharLimit = null,
        int? DeclarationBytesParsed = null, int? DeclarationByteLimit = null,
        int? DeclarationPerFileByteLimit = null);
    private sealed record ReviewFormerSymbol(string Name, string Kind, string? Namespace,
        string? Container,
        string Signature, int? DanglingCandidates, List<string> SamplePaths,
        bool ReferenceCandidatesLowerBound, ReviewReferenceCoverage ReferenceCandidatesCoverage,
        int StartLine = 0, int EndLine = 0)
    {
        public int? ReferenceCandidates { get; init; }
        public string? DanglingStatus { get; init; }
        public List<string>? SurvivingDeclarationPaths { get; init; }
        public int? SurvivingDeclarationsAtLeast { get; init; }
        public bool? SurvivingDeclarationsTruncated { get; init; }
    }
    private sealed record ReviewFormerFile(string Path, List<ReviewFormerSymbol> FormerSymbols,
        int FormerSymbolsTotal, bool FormerSymbolsTruncated);
    private sealed record ReviewDeletedFile(string Path, List<ReviewFormerSymbol>? FormerTypes,
        int? FormerTypesTotal, bool? FormerTypesTruncated, string? RecoveryStatus = null);
    private sealed record ReviewMovedFile(string From, string To, string Match);
    private sealed record ReviewUnsupportedLanguageFile(
        string Path, string Language, string Change);
    internal sealed record ReviewProjectShapeSnapshot(List<ProjectRow> Projects,
        Dictionary<long, ParsedProject> Parsed, int RequestedAtLeast, int Attempted, int BytesRead,
        long LoadElapsedMilliseconds, bool ShapeBudgetHit, bool EvaluationIncomplete,
        GlobMatchBudget MatchBudget)
    {
        private int _globMatchAttempted;

        public long ElapsedMilliseconds => Math.Max(LoadElapsedMilliseconds,
            MatchBudget.ElapsedMilliseconds);
        public bool GlobMatchAttempted => Volatile.Read(ref _globMatchAttempted) != 0;
        public bool GlobBudgetHit => GlobMatchAttempted && MatchBudget.IsExhausted;
        public bool BudgetHit => ShapeBudgetHit || GlobBudgetHit;
        public bool Complete => !BudgetHit && !EvaluationIncomplete;

        internal void MarkGlobMatchAttempted() =>
            Volatile.Write(ref _globMatchAttempted, 1);
    }

    internal sealed class ReviewProjectOwnershipResolver(
        Func<ReviewProjectShapeSnapshot> snapshotFactory)
    {
        private readonly Dictionary<string, HashSet<long>> _ownersByPath =
            new(StringComparer.Ordinal);

        public HashSet<long> OwnerIds(string path)
        {
            ReviewProjectShapeSnapshot snapshot = snapshotFactory();
            if (!snapshot.Complete)
            {
                _ownersByPath.Clear();
                return [];
            }
            if (_ownersByPath.TryGetValue(path, out HashSet<long>? cached)) return cached;

            HashSet<long> owners = LikelyOwningProjectIds(path, snapshot.Projects,
                snapshot.Parsed, matchBudget: snapshot.MatchBudget,
                onMatchAttempt: snapshot.MarkGlobMatchAttempted);
            if (!snapshot.Complete)
            {
                // A later path can exhaust the shared sticky budget. Invalidate every earlier
                // owner proof so a cached result cannot suppress former-symbol evidence.
                _ownersByPath.Clear();
                return [];
            }
            _ownersByPath[path] = owners;
            return owners;
        }
    }

    [McpServerTool(Name = "review_pack")]
    [Description("ONE budget-bounded review digest: diff -> touched symbols -> per-symbol impact. Default reviews the working tree against the index's recorded commit (falling back to HEAD when no indexed commit exists), so diff evidence and indexed symbols share one baseline; pass baseRef (sha or branch name, e.g. the merge-base) to choose another base, or pass explicit paths. Digests are INDEX-backed (confidence indexed) and each carries a symbolId handle. Former symbols in surviving files, exact file moves, deleted types, symbol-less C# changes, and file-level unsupported-language source changes are reported explicitly; every bounded section exposes coverage.")]
    public string ReviewPack(
        [Description("Base to diff against: a commit sha or a ref name (strict charset; typically the merge-base). Default: the index's recorded commit, so diff evidence and indexed symbols share one baseline.")] string? baseRef = null,
        [Description("Comma-separated workspace-relative paths to review INSTEAD of a git diff (whole-file granularity; no git needed).")] string? paths = null,
        [Description("Byte budget (default 16384, max 65536).")] int maxBytes = 16384,
        [Description("Max touched symbols digested (default 40, max 100).")] int maxSymbols = 40)
    {
        maxBytes = Math.Clamp(maxBytes, 2048, Json.HardBudgetBytes);
        if (!_manager.IsQueryable)
            return BoundedReviewNotReady(_manager.Health(), maxBytes);
        maxSymbols = Math.Clamp(maxSymbols, 1, 100);
        IndexReadSnapshot? readSnapshot = _manager.TryOpenReviewSnapshot();
        if (readSnapshot is null)
        {
            IndexHealth health = _manager.Health();
            return BoundedReviewError("index_refresh_in_progress",
                "The index changed while review_pack was pinning a read snapshot; retry after the current refresh completes.",
                maxBytes, Meta.From(health, "indexed", "text"));
        }
        using IndexReadSnapshot pinnedSnapshot = readSnapshot;
        IndexQueries q = pinnedSnapshot.Queries;
        IndexHealth pinnedIndexHealth = pinnedSnapshot.Health;
        string root = _manager.WorkspaceRoot;
        ReviewProjectShapeSnapshot? cachedProjectShapes = null;
        ReviewProjectShapeSnapshot ProjectShapes()
        {
            if (cachedProjectShapes is null)
            {
                List<ProjectRow> candidates = q.AllProjects(ReviewMaxProjectShapeFiles + 1);
                bool projectCountLimited = candidates.Count > ReviewMaxProjectShapeFiles;
                if (projectCountLimited) candidates.RemoveAt(candidates.Count - 1);
                cachedProjectShapes = LoadProjectShapesBounded(root, candidates,
                    projectCountLimited,
                    matchBudgetOverride: ReviewProjectGlobBudgetFactoryForTest?.Invoke());
            }
            return cachedProjectShapes;
        }
        // The stored compile graph intentionally over-approximates some item operations and uses
        // a nearest-default-root approximation. The resolver replaces it with a bounded raw model
        // and invalidates all cached proofs if the shared sticky match budget is ever exhausted.
        var ownershipResolver = new ReviewProjectOwnershipResolver(ProjectShapes);
        HashSet<long> OwnerIds(string path) => ownershipResolver.OwnerIds(path);
        var outlinesByPath = new Dictionary<string, List<SymbolHit>>(StringComparer.Ordinal);
        List<SymbolHit> Outline(string path)
        {
            if (!outlinesByPath.TryGetValue(path, out List<SymbolHit>? outline))
            {
                outline = q.Outline(path);
                outlinesByPath[path] = outline;
            }
            return outline;
        }
        var notes = new List<ReviewNote>();
        GitInfo.SubmoduleWorktreeCoverage? excludedSubmoduleWorktrees = null;
        GitInfo.UntrackedRepositoryCoverage? excludedUntrackedRepositories = null;
        GitInfo.UntrackedLinkCoverage? excludedUntrackedLinks = null;
        var changedSubmoduleLinks = new List<string>();

        // ---- 1. The changed set: (file -> ranges) + deleted + untracked ----
        // Git path identity is byte/case-sensitive even when the host filesystem is not.
        // Keep it case-sensitive through review aggregation so case-distinct paths cannot
        // overwrite each other's ranges in repositories that support both names.
        var changed = NewReviewPathMap();
        var changedHunks = new Dictionary<string, List<GitInfo.DiffHunk>>(StringComparer.Ordinal);
        var deleted = new List<string>();
        var movedFiles = new List<ReviewMovedFile>();
        var provisionallyPreservedMoveSources = new List<string>();
        string? resolvedBase = null;
        var baseContentsByPath = new Dictionary<string, string?>(StringComparer.Ordinal);
        int baseBlobRequests = 0;
        int baseBlobAttempts = 0;
        int baseBlobRecovered = 0;
        int baseBlobCharsRetained = 0;
        long baseBlobElapsedMilliseconds = 0;
        bool baseBlobBudgetHit = false;
        string? BaseContent(string path)
        {
            if (!baseContentsByPath.TryGetValue(path, out string? content))
            {
                baseBlobRequests++;
                int remainingChars = ReviewMaxBaseBlobChars - baseBlobCharsRetained;
                if (resolvedBase is null || remainingChars <= 0 ||
                    baseBlobAttempts >= ReviewMaxBaseBlobAttempts ||
                    ReviewMaxBaseBlobMilliseconds - baseBlobElapsedMilliseconds < 4)
                {
                    content = null;
                    baseBlobBudgetHit |= remainingChars <= 0 ||
                                         baseBlobAttempts >= ReviewMaxBaseBlobAttempts ||
                                         ReviewMaxBaseBlobMilliseconds -
                                         baseBlobElapsedMilliseconds < 4;
                }
                else
                {
                    baseBlobAttempts++;
                    var attemptTimer = Stopwatch.StartNew();
                    int remainingTimeMs = ReviewMaxBaseBlobMilliseconds -
                                          (int)baseBlobElapsedMilliseconds;
                    content = GitInfo.ShowFile(root, resolvedBase, path,
                        ReviewMaxBaseBlobBytes, remainingTimeMs);
                    attemptTimer.Stop();
                    baseBlobElapsedMilliseconds += attemptTimer.ElapsedMilliseconds;
                    if (BaseBlobTimeBudgetHitAfterAttempt(content,
                            baseBlobElapsedMilliseconds))
                    {
                        baseBlobBudgetHit = true;
                    }
                    if (content is not null && content.Length > remainingChars)
                    {
                        content = null;
                        baseBlobBudgetHit = true;
                    }
                }
                if (content is not null)
                {
                    baseBlobRecovered++;
                    baseBlobCharsRetained += content.Length;
                }
                baseContentsByPath[path] = content;
            }
            return content;
        }
        var baseParsedByPath = new Dictionary<string, ParsedCsFile?>(StringComparer.Ordinal);
        ParsedCsFile? BaseParsed(string path)
        {
            if (!baseParsedByPath.TryGetValue(path, out ParsedCsFile? parsed))
            {
                string? content = BaseContent(path);
                parsed = content is null ? null : SyntaxIndexer.Parse(path, content);
                baseParsedByPath[path] = parsed;
            }
            return parsed;
        }
        int untrackedCount = 0;
        var untrackedPaths = new HashSet<string>(StringComparer.Ordinal);
        bool projectShapeChanged = false;
        if (paths is not null)
        {
            foreach (var p in SplitCsv(paths) ?? new List<string>())
            {
                changed[NormalizePath(p)] = WholeFile();
            }
        }
        else
        {
            var (head, headStatus) = _manager.CurrentHeadCommitEx();
            if (head is null)
            {
                return BoundedReviewError("git_unavailable",
                    $"review_pack needs git for diff mode (HEAD: {headStatus}) — pass explicit 'paths' instead.",
                    maxBytes, Meta.From(pinnedIndexHealth, "indexed", "text"));
            }
            string defaultBase = pinnedIndexHealth.IndexedCommit ?? head;
            resolvedBase = GitInfo.ResolveRef(root, baseRef ?? defaultBase);
            if (resolvedBase is null)
            {
                // Never reflect the caller's raw ref: besides being unnecessary, a very large
                // invalid value used to bypass both review_pack's maxBytes contract and the
                // server-wide hard envelope on this early-return path.
                return baseRef is not null
                    ? BoundedReviewError("bad_request",
                        "baseRef did not resolve — pass a commit sha or a simple ref name (letters/digits and / - _ .).",
                        maxBytes, Meta.From(pinnedIndexHealth, "indexed", "text"))
                    : BoundedReviewError("git_index_baseline_unavailable",
                        "The index's recorded Git baseline no longer resolves. Run refresh_index to realign the index, or pass an explicit baseRef.",
                        maxBytes, Meta.From(pinnedIndexHealth, "indexed", "text"));
            }
            var reviewDiff = GitInfo.ReviewDiff(root, resolvedBase);
            excludedSubmoduleWorktrees = reviewDiff.ExcludedSubmoduleWorktrees;
            excludedUntrackedRepositories = reviewDiff.ExcludedUntrackedRepositories;
            excludedUntrackedLinks = reviewDiff.ExcludedUntrackedLinks;
            changedSubmoduleLinks = reviewDiff.ChangedSubmoduleLinks;
            var diff = reviewDiff.Diff;
            if (!string.Equals(diff.Status, "ok", StringComparison.Ordinal) || diff.Files is null)
            {
                var (error, detail) = ReviewGitFailure(diff.Status);
                return BoundedReviewError(error, detail, maxBytes,
                    Meta.From(pinnedIndexHealth, "indexed", "text"));
            }

            var hunks = diff.Files;
            var dirty = reviewDiff.Dirty;
            var untracked = reviewDiff.UntrackedFiles;
            if (dirty is null || untracked is null)
            {
                return BoundedReviewError("git_status_failed",
                    "Git could not safely enumerate working-tree changes; no partial result was returned - pass explicit 'paths' instead.",
                    maxBytes, Meta.From(pinnedIndexHealth, "indexed", "text"));
            }
            projectShapeChanged = hunks.Any(file => IsProjectShapePath(file.Path)) ||
                                  untracked.Any(IsProjectShapePath);
            foreach (var f in hunks)
            {
                if (f.Deleted)
                {
                    if (f.Hunks.Count > 0) changedHunks[f.Path] = f.Hunks;
                    if (f.MovedToPath is not null)
                    {
                        movedFiles.Add(new ReviewMovedFile(f.Path, f.MovedToPath, "exact_blob"));
                        if (resolvedBase is null || !MovePreservesReviewableCSharp(root,
                                f.Path, f.MovedToPath, q, BaseContent, OwnerIds, Outline,
                                projectShapeChanged))
                        {
                            deleted.Add(f.Path);
                        }
                        else
                        {
                            // Owner preservation is provisional until the one shared cumulative
                            // project/glob proof has survived every later move/path.
                            provisionallyPreservedMoveSources.Add(f.Path);
                        }
                    }
                    else
                        deleted.Add(f.Path);
                }
                else
                {
                    changed[f.Path] = f.Ranges.Count > 0 ? f.Ranges : WholeFile();
                    if (f.Hunks.Count > 0) changedHunks[f.Path] = f.Hunks;
                }
            }
            // Untracked files never appear in a diff against a commit — union the dirt,
            // whole-file (they are entirely new, and files deleted-on-disk stay with `deleted`).
            foreach (var p in untracked)
            {
                if (!changed.ContainsKey(p) && !deleted.Contains(p, StringComparer.Ordinal)
                    && CodeNav.Core.WorkspacePaths.TryResolveGitPathInside(root, p,
                        out string fullPath)
                    && File.Exists(fullPath) &&
                    !CodeNav.Core.WorkspacePaths.EscapesViaReparsePoint(root, fullPath))
                {
                    changed[p] = WholeFile();
                    untrackedPaths.Add(p);
                    untrackedCount++;
                }
            }
            if (excludedSubmoduleWorktrees is not null)
            {
                notes.Add(new ReviewNote(NoteIds.ReviewSubmoduleWorktreesExcluded,
                    $"{excludedSubmoduleWorktrees.Count} submodule worktree(s) were intentionally excluded from dirty-file inspection because entering child repositories can execute child-local Git filters. Review each submodule from its own root; superproject gitlink pointer changes are still detected and reported separately."));
            }
            if (excludedUntrackedRepositories is not null)
            {
                notes.Add(new ReviewNote(NoteIds.ReviewUntrackedRepositoriesExcluded,
                    $"{excludedUntrackedRepositories.Count} untracked nested {(excludedUntrackedRepositories.Count == 1 ? "repository was" : "repositories were")} excluded. Parent Git reported {(excludedUntrackedRepositories.Count == 1 ? "it" : "them")} atomically; review each from its own root. Phoenix did not run child-local Git filters or helpers."));
            }
            if (excludedUntrackedLinks is not null)
            {
                notes.Add(new ReviewNote(NoteIds.ReviewUntrackedLinksExcluded,
                    $"{excludedUntrackedLinks.Count} untracked path(s) crossing a symbolic-link or junction boundary were excluded; review the linked target from its own workspace root."));
            }
        }

        // ---- 2. Partition: .cs -> symbols; project/solution/config files listed as-is ----
        var allCsFiles = changed.Keys
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        int csFilesTotal = allCsFiles.Count;
        var csFiles = allCsFiles.Take(ReviewMaxFiles).ToList();
        bool changedFileCapHit = csFiles.Count < csFilesTotal;
        if (changedFileCapHit)
        {
            notes.Add(new ReviewNote(NoteIds.ReviewChangedFilesCap,
                $"Only {csFiles.Count} of {csFilesTotal} changed C# files were mapped to symbols; narrow with 'paths' to inspect the remainder."));
        }
        var allUnsupportedLanguageFiles = changed.Keys
            .Where(IsFSharpSourcePath)
            .Select(path => new ReviewUnsupportedLanguageFile(path, "fs",
                untrackedPaths.Contains(path) ? "untracked" : paths is not null ? "explicit" : "changed"))
            .Concat(deleted.Where(IsFSharpSourcePath)
                .Select(path => new ReviewUnsupportedLanguageFile(path, "fs", "deleted")))
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Change, StringComparer.Ordinal)
            .ToList();
        int unsupportedLanguageFilesTotal = allUnsupportedLanguageFiles.Count;
        var unsupportedLanguageFiles = allUnsupportedLanguageFiles
            .Take(ReviewMaxUnsupportedLanguageFiles).ToList();
        if (unsupportedLanguageFilesTotal > 0)
        {
            notes.Add(new ReviewNote(NoteIds.ReviewUnsupportedLanguageFiles,
                $"{unsupportedLanguageFilesTotal} F# source change(s) are reported at file level because tier-a indexing has no F# syntax or compiler model; inspect their raw diffs directly."));
        }
        var projectFiles = changed.Keys.Concat(deleted)
            .Where(IsProjectShapePath)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        projectShapeChanged |= projectFiles.Count > 0;
        if (projectFiles.Count > 0)
        {
            notes.Add(new ReviewNote(NoteIds.ReviewProjectFilesChanged,
                $"{projectFiles.Count} project/build file(s) changed — dependency edges, compile sets, or test classification may shift; check project_graph on the affected projects."));
        }

        // ---- 3. Hunks -> symbols (span intersection, innermost policy) ----
        var touched = new List<SymbolHit>();
        var unmappedHunks = new List<ReviewUnmappedHunk>();
        var unmappedIndexes = new Dictionary<ReviewUnmappedKey, int>();
        void AddUnmapped(string path, int start, int? end, string reason, string side,
            GitInfo.DiffHunk? hunk = null)
        {
            ReviewDiffRange? oldRange = hunk is { OldCount: > 0 }
                ? new ReviewDiffRange(hunk.OldStart, hunk.OldCount)
                : null;
            ReviewDiffRange? newRange = hunk is { NewCount: > 0 }
                ? new ReviewDiffRange(hunk.NewStart, hunk.NewCount)
                : null;
            var key = new ReviewUnmappedKey(path, oldRange, newRange);
            if (hunk is null || !unmappedIndexes.TryGetValue(key, out int existingIndex))
            {
                unmappedHunks.Add(new ReviewUnmappedHunk(path, start, end, reason, side,
                    oldRange, newRange));
                if (hunk is not null) unmappedIndexes[key] = unmappedHunks.Count - 1;
                return;
            }

            ReviewUnmappedHunk existing = unmappedHunks[existingIndex];
            var additional = existing.AdditionalReasons?.ToList() ?? [];
            if (!string.Equals(existing.Reason, reason, StringComparison.Ordinal) &&
                !additional.Contains(reason, StringComparer.Ordinal))
            {
                additional.Add(reason);
                additional.Sort(StringComparer.Ordinal);
            }
            unmappedHunks[existingIndex] = existing with
            {
                Side = string.Equals(existing.Side, side, StringComparison.Ordinal)
                    ? existing.Side
                    : "both",
                Old = existing.Old ?? oldRange,
                New = existing.New ?? newRange,
                AdditionalReasons = additional.Count == 0 ? null : additional,
            };
        }
        var seenIds = new HashSet<long>();
        int namespaceAnalysisRequested = 0;
        int namespaceAnalysisParsed = 0;
        int namespaceAnalysisChars = 0;
        long namespaceAnalysisElapsedMilliseconds = 0;
        bool namespaceAnalysisBudgetHit = false;
        foreach (var file in csFiles)
        {
            FileHit? indexedFile = q.FileByPath(file);
            if (indexedFile is null)
            {
                if (changedHunks.TryGetValue(file, out List<GitInfo.DiffHunk>? unindexedHunks))
                {
                    foreach (GitInfo.DiffHunk hunk in unindexedHunks.Where(hunk =>
                                 hunk.NewCount > 0))
                    {
                        AddUnmapped(file, hunk.NewStart,
                            checked(hunk.NewStart + hunk.NewCount - 1), "file_level", "new",
                            hunk);
                    }
                }
                else
                {
                    // A true whole-file untracked/explicit path has no trustworthy indexed line
                    // count. Null End is the explicit unknown-range marker; never invent an
                    // int.MaxValue line number merely to drive the span query.
                    AddUnmapped(file, 1, null, "whole_file_unindexed", "new");
                }
                continue;
            }
            int indexedLineCount = Math.Max(1, indexedFile.LineCount);
            List<(int Start, int End, GitInfo.DiffHunk? Hunk)> preciseChanges;
            if (changedHunks.TryGetValue(file, out List<GitInfo.DiffHunk>? fileHunks))
            {
                preciseChanges = fileHunks.Where(hunk => hunk.NewCount > 0)
                    .Select(hunk => (hunk.NewStart,
                        checked(hunk.NewStart + hunk.NewCount - 1),
                        (GitInfo.DiffHunk?)hunk))
                    .ToList();
            }
            else
            {
                preciseChanges = changed[file]
                    .Select(range => (range.Start, Math.Min(range.End, indexedLineCount),
                        (GitInfo.DiffHunk?)null))
                    .ToList();
            }
            List<(int Start, int End)> preciseRanges = preciseChanges
                .Select(change => (change.Start, change.End)).ToList();
            // Review F2: SymbolsIntersecting caps at 64 ranges — beyond that the tail hunks
            // were SILENTLY dropped (no flag, no note). A 65+-hunk file is effectively a
            // rewrite: the documented whole-file fallback applies, and the fully-covered-type
            // rule below then digests it honestly at TYPE level.
            List<(int Start, int End)> queryRanges = preciseRanges.Count > 64
                ? WholeFile()
                : preciseRanges;
            var hits = q.SymbolsIntersecting(file, queryRanges);
            // Granularity policy, two-sided (test-driven — the one-sided innermost rule
            // surfaced 'method Run' for a brand-new file and swallowed the class):
            //  * a type FULLY covered by a changed range (new file, whole-type addition) is
            //    the reviewable unit — it SWALLOWS its members (one digest, not N);
            //  * a PARTIALLY touched type defers to its touched members (editing a method
            //    body is a method review, not a class review); a type-only touch
            //    (attribute/base-list line) has no touched child and survives itself.
            List<SymbolHit> reviewable = SelectReviewableHits(hits, queryRanges);
            foreach (var h in reviewable)
            {
                if (seenIds.Add(h.Id)) touched.Add(h);
            }

            var uncoveredChanges = new List<((int Start, int End,
                GitInfo.DiffHunk? Hunk) Change, List<(int Start, int End)> Uncovered)>();
            foreach (var change in preciseChanges)
            {
                var range = (change.Start, change.End);
                List<(int Start, int End)> uncovered = UncoveredLineRanges(range,
                    reviewable.Select(hit => (hit.StartLine, hit.EndLine)));
                if (uncovered.Count > 0) uncoveredChanges.Add((change, uncovered));
            }
            if (uncoveredChanges.Count == 0) continue;

            namespaceAnalysisRequested++;
            List<(int Start, int End)> namespaceNameRanges = [];
            int remainingNamespaceChars = ReviewMaxNamespaceChars - namespaceAnalysisChars;
            if (namespaceAnalysisParsed < ReviewMaxNamespaceFiles &&
                remainingNamespaceChars > 0 &&
                namespaceAnalysisElapsedMilliseconds < ReviewMaxNamespaceMilliseconds)
            {
                int maxChars = Math.Min(ReviewMaxNamespacePerFileChars,
                    remainingNamespaceChars);
                if (indexedFile.Size > maxChars)
                {
                    namespaceAnalysisBudgetHit = true;
                }
                else
                {
                    var timer = Stopwatch.StartNew();
                    string? content = q.ContentByPathBounded(file, maxChars);
                    if (content is not null)
                    {
                        namespaceNameRanges = SyntaxIndexer.NamespaceNameLineRanges(content);
                        namespaceAnalysisParsed++;
                        namespaceAnalysisChars += content.Length;
                    }
                    else
                    {
                        namespaceAnalysisBudgetHit = true;
                    }
                    timer.Stop();
                    namespaceAnalysisElapsedMilliseconds += timer.ElapsedMilliseconds;
                    if (namespaceAnalysisElapsedMilliseconds >= ReviewMaxNamespaceMilliseconds)
                        namespaceAnalysisBudgetHit = true;
                }
            }
            else
            {
                namespaceAnalysisBudgetHit = true;
            }

            foreach (var (change, uncovered) in uncoveredChanges)
            {
                bool namespaceOnly = uncovered.All(gap =>
                    UncoveredLineRanges(gap, namespaceNameRanges).Count == 0);
                AddUnmapped(file, uncovered[0].Start, uncovered[^1].End,
                    namespaceOnly ? "namespace" : "file_level", "new", change.Hunk);
            }
        }
        bool referenceCandidatesCapHit = false;
        bool referenceDeclarationBudgetHit = false;
        bool baseBlobUnavailable = false;
        int formerSymbolsTotal = 0;
        bool formerSymbolDangling = false;
        var formerByPath = new Dictionary<string, List<ReviewFormerSymbol>>(StringComparer.Ordinal);
        var formerTotalsByPath = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenFormer = new HashSet<string>(StringComparer.Ordinal);
        ReviewFormerSymbol BuildFormerEvidence(SymbolRow oldSymbol)
        {
            var refs = q.ReferenceCandidates(oldSymbol.Name, 100, 1,
                excludeDeclarations: true);
            referenceCandidatesCapHit |= refs.CandidateFilesTruncated;
            referenceDeclarationBudgetHit |= refs.DeclarationExclusionBudgetHit;
            return new ReviewFormerSymbol(oldSymbol.Name, oldSymbol.Kind,
                oldSymbol.Namespace, oldSymbol.Container, oldSymbol.Signature, refs.TotalHits,
                refs.Groups.SelectMany(group => group.Samples)
                    .Select(sample => sample.FilePath)
                    .Distinct(StringComparer.Ordinal).Take(2).ToList(),
                refs.CandidateFilesTruncated || refs.DeclarationExclusionBudgetHit,
                ReferenceCoverage(refs), oldSymbol.StartLine,
                oldSymbol.EndLine)
            {
                ReferenceCandidates = refs.TotalHits,
            };
        }

        void RecordFormer(string file, SymbolRow oldSymbol)
        {
            string formerKey = file + "\0" + oldSymbol.OrdinalInFile.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            if (!seenFormer.Add(formerKey)) return;
            formerSymbolsTotal++;
            formerTotalsByPath[file] = formerTotalsByPath.GetValueOrDefault(file) + 1;
            if (formerSymbolsTotal > ReviewMaxFormerSymbols) return;
            ReviewFormerSymbol evidence = BuildFormerEvidence(oldSymbol);
            if (!formerByPath.TryGetValue(file, out List<ReviewFormerSymbol>? fileFormer))
            {
                fileFormer = [];
                formerByPath[file] = fileFormer;
            }
            fileFormer.Add(evidence);
            formerSymbolDangling |= evidence.DanglingCandidates is > 0;
        }

        void PreflightLaterDeletedOwnerProof()
        {
            if (projectShapeChanged || provisionallyPreservedMoveSources.Count == 0) return;

            // Exact-move suppression is provisional because ordinary deleted files can inspect
            // additional owner paths later. Run that bounded owner work before finalizing the
            // move-only contract. Every owner lookup used by the expansion below is now cached,
            // so a complete preflight cannot become incomplete merely because another deletion
            // exists; only actual proof incompleteness reintroduces a moved source.
            foreach (string path in deleted.OrderBy(candidate => candidate,
                         StringComparer.Ordinal).Take(ReviewMaxDeletedFiles))
            {
                if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                ParsedCsFile? parsed = BaseParsed(path);
                if (parsed is null) continue;

                HashSet<long> likelyOldOwners = OwnerIds(path);
                if (cachedProjectShapes is { Complete: false }) return;
                if (likelyOldOwners.Count == 0) continue;

                foreach (SymbolRow formerType in parsed.Symbols.Where(symbol =>
                             string.IsNullOrEmpty(symbol.Container) &&
                             symbol.Kind is "class" or "interface" or "struct" or "record" or
                                 "record_struct" or "enum" or "delegate")
                         .Take(ReviewMaxTypesPerDeleted))
                {
                    List<SymbolHit> survivingDeclarations = FilterExistingReviewDeclarations(root,
                            path, q.SymbolsByDeclarationIdentity(formerType.Kind,
                                formerType.Name, formerType.Namespace, formerType.Container,
                                formerType.Arity, 17))
                        .Take(16).ToList();
                    foreach (string survivingPath in survivingDeclarations
                                 .Select(hit => hit.FilePath)
                                 .Distinct(StringComparer.Ordinal))
                    {
                        _ = OwnerIds(survivingPath);
                        if (cachedProjectShapes is { Complete: false }) return;
                    }
                }
            }
        }

        PreflightLaterDeletedOwnerProof();
        if (cachedProjectShapes is { Complete: false })
        {
            foreach (string source in provisionallyPreservedMoveSources)
            {
                if (!deleted.Contains(source, StringComparer.Ordinal)) deleted.Add(source);
            }
        }

        var expandedDeletedPaths = deleted.OrderBy(path => path, StringComparer.Ordinal)
            .Take(ReviewMaxDeletedFiles).ToHashSet(StringComparer.Ordinal);

        if (resolvedBase is not null)
        {
            foreach (var (file, fileHunks) in changedHunks.OrderBy(pair => pair.Key,
                         StringComparer.Ordinal))
            {
                bool deletedFile = deleted.Contains(file, StringComparer.Ordinal);
                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                    (deletedFile && !expandedDeletedPaths.Contains(file)) ||
                    (!csFiles.Contains(file, StringComparer.Ordinal) && !deletedFile))
                {
                    continue;
                }
                List<GitInfo.DiffHunk> oldHunks = fileHunks
                    .Where(hunk => hunk.OldCount > 0)
                    .ToList();
                if (oldHunks.Count == 0) continue;
                string? oldContent = BaseContent(file);
                if (oldContent is null)
                {
                    baseBlobUnavailable = true;
                    foreach (GitInfo.DiffHunk hunk in oldHunks)
                    {
                        int oldEnd = checked(hunk.OldStart + hunk.OldCount - 1);
                        AddUnmapped(file, hunk.OldStart, oldEnd,
                            "base_blob_unavailable", "old", hunk);
                    }
                    continue;
                }
                ParsedCsFile parsedOld = BaseParsed(file)!;
                List<SymbolHit> currentOutline = Outline(file);
                var currentById = currentOutline.ToDictionary(symbol => symbol.Id);
                var currentByIdentity = currentOutline
                    .GroupBy(symbol => SyntaxIdentity(symbol, currentById),
                        StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(),
                        StringComparer.Ordinal);
                List<(int Start, int End)> oldNamespaceNameRanges =
                    SyntaxIndexer.NamespaceNameLineRanges(oldContent);

                foreach (GitInfo.DiffHunk hunk in oldHunks)
                {
                    int oldEnd = checked(hunk.OldStart + hunk.OldCount - 1);
                    var oldRanges = new List<(int Start, int End)>
                    {
                        (hunk.OldStart, oldEnd),
                    };
                    List<SymbolRow> oldSymbols = SelectReviewableSyntaxSymbols(
                        parsedOld.Symbols, oldRanges);
                    List<(int Start, int End)> oldUncovered = UncoveredLineRanges(
                        oldRanges[0], oldSymbols.Select(symbol =>
                            (symbol.StartLine, symbol.EndLine)));
                    if (oldUncovered.Count > 0)
                    {
                        bool namespaceOnly = oldUncovered.All(gap =>
                            UncoveredLineRanges(gap, oldNamespaceNameRanges).Count == 0);
                        AddUnmapped(file, oldUncovered[0].Start, oldUncovered[^1].End,
                            namespaceOnly ? "namespace" : hunk.NewCount == 0
                                ? "file_level_deleted"
                            : "file_level_old", "old", hunk);
                    }
                    if (deletedFile) continue;
                    foreach (SymbolRow oldSymbol in oldSymbols)
                    {
                        string identity = SyntaxIdentity(oldSymbol, parsedOld.Symbols);
                        if (currentByIdentity.TryGetValue(identity, out SymbolHit? surviving))
                        {
                            if (seenIds.Add(surviving.Id)) touched.Add(surviving);
                            continue;
                        }
                        RecordFormer(file, oldSymbol);
                    }
                }
            }
        }
        int touchedSymbolsTotal = touched.Count;
        bool symbolCapHit = touchedSymbolsTotal > maxSymbols;
        if (symbolCapHit)
        {
            notes.Add(new ReviewNote(NoteIds.ReviewSymbolCountCap,
                $"Only {maxSymbols} of {touchedSymbolsTotal} touched symbols were digested because maxSymbols was reached."));
        }
        if (unmappedHunks.Count > 0)
        {
            notes.Add(new ReviewNote(NoteIds.ReviewUnmappedHunks,
                $"{unmappedHunks.Count} changed C# evidence record(s) did not map completely to a reviewable indexed symbol; inspect unmappedChanges side plus old/new coordinates."));
        }
        touched = touched.Take(maxSymbols).ToList();

        // ---- 4. Digests (owner-level facts cached — many symbols share an owner) ----
        var orphaned = q.OrphanedPaths(touched.Select(t => t.FilePath).Distinct(StringComparer.Ordinal).ToList());
        var ownerFacts = new Dictionary<string, (int Direct, int HintOnly, int Transitive)>(StringComparer.OrdinalIgnoreCase);
        var digests = touched.Select(h =>
        {
            string? owner = q.ProjectsContaining(h.FilePath).FirstOrDefault(p => !p.IsTest)?.Name;
            (int Direct, int HintOnly, int Transitive) facts = (0, 0, 0);
            if (owner is not null && !ownerFacts.TryGetValue(owner, out facts))
            {
                var groups = q.ProjectGraph(owner, 1, "upstream")
                    .GroupBy(e => e.FromProject, StringComparer.OrdinalIgnoreCase).ToList();
                facts = (groups.Count,
                         groups.Count(g => g.All(e => e.Kind == "assembly")),
                         q.DependentClosure(owner).Count);
                ownerFacts[owner] = facts;
            }
            var tests = q.RelatedTests(h.Name, owner, 3);
            var referenceCandidates = q.ReferenceCandidates(h.Name, 200, 0);
            referenceCandidatesCapHit |= referenceCandidates.CandidateFilesTruncated;
            bool isPublic = h.Accessibility == "public";
            bool isOrphaned = orphaned.Contains(h.FilePath);

            var risks = new List<string>();
            if (isPublic && facts.Transitive > 0) risks.Add($"public symbol; {facts.Transitive} projects transitively depend on {owner}");
            if (facts.HintOnly > 0) risks.Add($"{facts.HintOnly} of {facts.Direct} direct dependents reach {owner} only via <Reference>/HintPath — refactor tooling won't follow those edges");
            if (tests.Count == 0) risks.Add("no test signal found for this symbol");
            if (isOrphaned) risks.Add("declared in a file NO project compiles — verify this change is even built");

            return (object)new
            {
                symbol = SymbolJson(h),
                owningProject = owner,
                directDependentProjects = owner is null ? null : new
                {
                    total = facts.Direct,
                    viaHintPathOnly = facts.HintOnly > 0 ? facts.HintOnly : (int?)null,
                },
                transitiveDependentProjects = owner is null ? (int?)null : facts.Transitive,
                publicApi = isPublic ? true : (bool?)null,
                referenceCandidates = referenceCandidates.TotalHits,
                referenceCandidatesLowerBound = referenceCandidates.CandidateFilesTruncated
                    ? true
                    : (bool?)null,
                referenceCandidatesCoverage = ReferenceCoverage(referenceCandidates),
                relatedTests = tests.Count == 0 ? null : tests.Select(t => new
                {
                    project = t.TestProject, t.Reason, signal = t.Signal,
                }),
                risks = risks.Count > 0 ? risks : null,
            };
        }).ToList();
        // ---- 5. Deletion honesty: former top-level types + dangling reference candidates ----
        var deletedOut = new List<ReviewDeletedFile>();
        var ownerSuppressedMembers = new List<(string Path, SymbolRow Symbol)>();
        bool formerTypesCapHit = false;
        bool survivingDeclarationsCapHit = false;
        var orderedDeleted = deleted.OrderBy(path => path, StringComparer.Ordinal).ToList();
        bool deletedFileCapHit = orderedDeleted.Count > ReviewMaxDeletedFiles;
        if (deleted.Count > 0 && resolvedBase is not null)
        {
            foreach (var path in orderedDeleted.Take(ReviewMaxDeletedFiles))
            {
                if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    deletedOut.Add(new ReviewDeletedFile(path, null, null, null));
                    continue;
                }
                ParsedCsFile? parsed = BaseParsed(path);
                if (parsed is null)
                {
                    baseBlobUnavailable = true;
                    deletedOut.Add(new ReviewDeletedFile(path, null, null, null,
                        "unavailable"));
                    continue;
                }
                HashSet<long> likelyOldOwners = projectShapeChanged ? [] : OwnerIds(path);
                var allFormerTypes = parsed.Symbols
                    .Where(s => string.IsNullOrEmpty(s.Container) &&
                                s.Kind is "class" or "interface" or "struct" or "record" or "record_struct" or "enum" or "delegate")
                    .ToList();
                bool typesTruncated = allFormerTypes.Count > ReviewMaxTypesPerDeleted;
                formerTypesCapHit |= typesTruncated;
                var formerTypes = new List<ReviewFormerSymbol>();
                foreach (SymbolRow formerType in allFormerTypes.Take(ReviewMaxTypesPerDeleted))
                {
                    List<SymbolHit> declarationProbe = FilterExistingReviewDeclarations(root,
                        path, q.SymbolsByDeclarationIdentity(formerType.Kind, formerType.Name,
                            formerType.Namespace, formerType.Container, formerType.Arity, 17));
                    bool declarationsTruncated = declarationProbe.Count > 16;
                    survivingDeclarationsCapHit |= declarationsTruncated;
                    List<SymbolHit> survivingDeclarations = declarationProbe.Take(16).ToList();
                    List<string> survivingPaths = survivingDeclarations
                        .Select(hit => hit.FilePath).Distinct(StringComparer.Ordinal)
                        .OrderBy(candidate => candidate, StringComparer.Ordinal).ToList();
                    List<SymbolHit> provenDeclarations = [];
                    if (!projectShapeChanged && likelyOldOwners.Count > 0)
                    {
                        var survivingOwnerIds = survivingDeclarations
                            .SelectMany(hit => OwnerIds(hit.FilePath))
                            .ToHashSet();
                        if (cachedProjectShapes is { Complete: true } &&
                            likelyOldOwners.IsSubsetOf(survivingOwnerIds))
                            provenDeclarations = survivingDeclarations;
                    }
                    List<string> provenPaths = provenDeclarations.Select(hit => hit.FilePath)
                        .Distinct(StringComparer.Ordinal).ToList();
                    ReviewFormerSymbol candidateEvidence = BuildFormerEvidence(formerType);
                    int? dangling = survivingPaths.Count == 0
                        ? candidateEvidence.ReferenceCandidates
                        : provenPaths.Count > 0
                            ? 0
                            : null;
                    string? danglingStatus = survivingPaths.Count == 0
                        ? null
                        : provenPaths.Count > 0
                            ? "project_candidate_survivor"
                            : "ambiguous_survivor";
                    ReviewFormerSymbol typeEvidence = candidateEvidence with
                    {
                        DanglingCandidates = dangling,
                        DanglingStatus = danglingStatus,
                        SurvivingDeclarationPaths = survivingPaths.Count == 0
                            ? null
                            : survivingPaths,
                        SurvivingDeclarationsAtLeast = survivingPaths.Count == 0
                            ? null
                            : declarationsTruncated ? 17 : declarationProbe.Count,
                        SurvivingDeclarationsTruncated = declarationsTruncated ? true : null,
                    };
                    formerTypes.Add(typeEvidence);

                    // A modified relocation or deleted partial file can preserve the type while
                    // dropping members. Compare members only when the destination is proven to
                    // occupy the same likely project domain; ambiguous cross-project duplicates
                    // remain advisory and never suppress evidence.
                    if (provenPaths.Count == 0 || declarationsTruncated) continue;
                    var provenOutlines = provenPaths.ToDictionary(path => path, Outline,
                        StringComparer.Ordinal);
                    var provenIdentitySets = provenOutlines.ToDictionary(pair => pair.Key,
                        pair =>
                        {
                            var byId = pair.Value.ToDictionary(symbol => symbol.Id);
                            return pair.Value.Select(symbol => SyntaxIdentity(symbol, byId))
                                .ToHashSet(StringComparer.Ordinal);
                        }, StringComparer.Ordinal);
                    foreach (SymbolRow oldMember in parsed.Symbols.Where(symbol =>
                                 BelongsToTopLevelType(symbol, formerType, parsed.Symbols)))
                    {
                        string memberIdentity = SyntaxIdentity(oldMember, parsed.Symbols);
                        var preservingOwnerIds = provenIdentitySets
                            .Where(pair => pair.Value.Contains(memberIdentity))
                            .SelectMany(pair => OwnerIds(pair.Key))
                            .ToHashSet();
                        if (cachedProjectShapes is { Complete: true } &&
                            likelyOldOwners.Count > 0 &&
                            likelyOldOwners.IsSubsetOf(preservingOwnerIds))
                        {
                            ownerSuppressedMembers.Add((path, oldMember));
                            continue;
                        }
                        RecordFormer(path, oldMember);
                    }
                }
                deletedOut.Add(new ReviewDeletedFile(path,
                    formerTypes.Count > 0 ? formerTypes : null, allFormerTypes.Count,
                    typesTruncated));
                if (formerTypes.Any(t => t.DanglingCandidates > 0))
                {
                    notes.Add(new ReviewNote(NoteIds.ReviewDeletedDangling,
                        $"'{path}' was deleted but its former top-level type(s) are still named elsewhere (see deletedFiles[].formerTypes.danglingCandidates) — likely broken references."));
                }
            }
        }

        if (cachedProjectShapes is { Complete: false })
        {
            // Exhaustion is global to this ownership proof. Reintroduce members suppressed before
            // the later exhaustion, and downgrade every earlier "proven" type survivor to the
            // same conservative ambiguity used when no complete owner proof was available.
            foreach ((string path, SymbolRow symbol) in ownerSuppressedMembers)
                RecordFormer(path, symbol);
            for (int i = 0; i < deletedOut.Count; i++)
            {
                ReviewDeletedFile file = deletedOut[i];
                if (file.FormerTypes is null) continue;
                deletedOut[i] = file with
                {
                    FormerTypes = file.FormerTypes.Select(type =>
                        string.Equals(type.DanglingStatus, "project_candidate_survivor",
                            StringComparison.Ordinal)
                            ? type with
                            {
                                DanglingCandidates = null,
                                DanglingStatus = "ambiguous_survivor",
                            }
                            : type).ToList(),
                };
            }
        }

        var formerFiles = formerByPath.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ReviewFormerFile(pair.Key, pair.Value,
                formerTotalsByPath.GetValueOrDefault(pair.Key),
                pair.Value.Count < formerTotalsByPath.GetValueOrDefault(pair.Key)))
            .ToList();
        if (formerSymbolsTotal > ReviewMaxFormerSymbols)
        {
            notes.Add(new ReviewNote(NoteIds.ReviewFormerSymbolsCap,
                $"Only {ReviewMaxFormerSymbols} of {formerSymbolsTotal} former symbols were expanded; narrow with 'paths' to inspect the remainder."));
        }
        if (formerSymbolDangling)
        {
            notes.Add(new ReviewNote(NoteIds.ReviewFormerSymbolDangling,
                "At least one removed or renamed symbol is still named elsewhere; inspect formerSymbols[]."));
        }

        if (deletedFileCapHit)
        {
            notes.Add(new ReviewNote(NoteIds.ReviewDeletedFilesCap,
                $"Only {deletedOut.Count} of {orderedDeleted.Count} deleted files were expanded; narrow with 'paths' to inspect the remainder."));
        }
        if (formerTypesCapHit)
        {
            notes.Add(new ReviewNote(NoteIds.ReviewFormerTypesCap,
                $"At least one deleted file contained more than {ReviewMaxTypesPerDeleted} former top-level types; each record reports its total and truncation state."));
        }
        if (survivingDeclarationsCapHit)
        {
            notes.Add(new ReviewNote(NoteIds.ReviewSurvivingDeclarationsCap,
                "At least one former type matched more than 16 current declarations; survivor samples are bounded and member-comparison evidence is omitted for that ambiguous type."));
        }
        if (referenceCandidatesCapHit)
        {
            notes.Add(new ReviewNote(NoteIds.ReviewReferenceCandidatesCap,
                "At least one reference-candidate scan reached its file cap; its count is a lower bound and is marked referenceCandidatesLowerBound."));
        }
        if (referenceDeclarationBudgetHit)
        {
            notes.Add(new ReviewNote(NoteIds.ReviewReferenceDeclarationBudget,
                "Reference declaration-exclusion reached its per-ReviewPack budget (per-file size, cumulative characters/bytes, or parse count); affected counts are lower bounds and their coverage reports the cause."));
        }
        if (baseBlobUnavailable)
        {
            notes.Add(new ReviewNote(NoteIds.ReviewBaseBlobUnavailable,
                "At least one base blob could not be recovered within the bounded local Git read; former-symbol evidence is explicitly marked unavailable instead of being omitted silently."));
        }
        if (baseBlobBudgetHit)
        {
            notes.Add(new ReviewNote(NoteIds.ReviewBaseBlobBudget,
                $"Base-blob recovery reached a cumulative bound ({ReviewMaxBaseBlobChars} retained characters, {ReviewMaxBaseBlobAttempts} attempts, or {ReviewMaxBaseBlobMilliseconds} ms); remaining former-symbol evidence is marked unavailable."));
        }
        if (namespaceAnalysisBudgetHit)
        {
            notes.Add(new ReviewNote(NoteIds.ReviewNamespaceAnalysisBudget,
                "Namespace-only classification reached its bounded content/parse budget; affected uncovered ranges remain conservatively labeled file_level."));
        }
        if (cachedProjectShapes is { ShapeBudgetHit: true })
        {
            notes.Add(new ReviewNote(NoteIds.ReviewProjectShapeBudget,
                "Deleted-path project ownership fallback reached its bounded no-follow XML project count, byte, or time budget; preservation proof remains conservative and may retain extra former evidence."));
        }
        if (cachedProjectShapes is { GlobBudgetHit: true })
        {
            notes.Add(new ReviewNote(NoteIds.ReviewProjectGlobBudget,
                "Deleted-path project ownership fallback reached its cumulative glob segment, operation, or time budget; preservation proof remains conservative and may retain extra former evidence."));
        }
        if (cachedProjectShapes is { EvaluationIncomplete: true })
        {
            notes.Add(new ReviewNote(NoteIds.ReviewProjectShapeIncomplete,
                "Deleted-path project ownership depends on MSBuild evaluation the raw bounded project model cannot prove; preservation suppression is disabled and extra former evidence may remain."));
        }

        notes.Add(new ReviewNote(NoteIds.ReviewIndexedOnly,
            "Digests are index-backed (confidence indexed): reference counts are whole-identifier candidates and dependents come from the stored graph. Escalate chosen symbols with references(symbolId, mode:'semantic') / impact(symbolId)."));

        int sampleListCount = (changedSubmoduleLinks.Count > 0 ? 1 : 0) +
                               (excludedSubmoduleWorktrees is not null ? 1 : 0) +
                               (excludedUntrackedRepositories is not null ? 1 : 0) +
                               (excludedUntrackedLinks is not null ? 1 : 0);
        int sampleBytesPerList = sampleListCount == 0
            ? 0
            : Math.Min(512, Math.Max(0,
                (maxBytes - 2048) / (4 * sampleListCount)));
        List<string> changedSubmoduleSample = BoundedReviewPathSample(
            changedSubmoduleLinks, sampleBytesPerList);
        List<string> excludedSubmoduleSample = excludedSubmoduleWorktrees is null
            ? []
            : BoundedReviewPathSample(
                excludedSubmoduleWorktrees.SamplePaths, sampleBytesPerList);
        List<string> excludedUntrackedRepositorySample = excludedUntrackedRepositories is null
            ? []
            : BoundedReviewPathSample(
                 excludedUntrackedRepositories.SamplePaths, sampleBytesPerList);
        List<string> excludedUntrackedLinkSample = excludedUntrackedLinks is null
            ? []
            : BoundedReviewPathSample(excludedUntrackedLinks.SamplePaths,
                sampleBytesPerList);
        var meta = Meta.From(pinnedIndexHealth, "indexed", "text");
        var symbolItems = new List<object>(digests);
        var unsupportedLanguageItems = new List<ReviewUnsupportedLanguageFile>(
            unsupportedLanguageFiles);
        var projectItems = new List<string>(projectFiles);
        var deletedItems = new List<ReviewDeletedFile>(deletedOut);
        var unmappedItems = new List<ReviewUnmappedHunk>(unmappedHunks);
        var formerItems = new List<ReviewFormerFile>(formerFiles);
        var movedItems = new List<ReviewMovedFile>(movedFiles);
        var responseNotes = new List<ReviewNote>(notes);
        bool byteBudgetTrimmed = false;
        bool compactNoteText = false;

        string BuildResponse()
        {
            List<ReviewNote> emittedNotes = compactNoteText
                ? responseNotes.Select(note => note with
                    {
                        Text = "Detail trimmed for maxBytes; use the structured coverage fields.",
                    }).ToList()
                : responseNotes.ToList();
            if (symbolItems.Count < touchedSymbolsTotal)
            {
                emittedNotes.Add(new ReviewNote(NoteIds.ReviewSymbolsTruncated,
                    "Legacy umbrella: symbols were truncated; inspect the exact maxSymbols or maxBytes note id."));
            }
            return Json.Serialize(new
            {
                baseRef = resolvedBase,
                changedFiles = new
                {
                    total = changed.Count + deleted.Count,
                    cs = csFilesTotal,
                    fs = changed.Keys.Count(IsFSharpSourcePath),
                    projectFiles = projectFiles.Count,
                    deleted = deleted.Count,
                    untracked = untrackedCount > 0 ? untrackedCount : (int?)null,
                    submoduleLinks = changedSubmoduleLinks.Count > 0
                        ? changedSubmoduleLinks.Count
                        : (int?)null,
                },
                changedCsFilesCoverage = new
                {
                    total = csFilesTotal,
                    returned = csFiles.Count,
                    truncated = changedFileCapHit ? true : (bool?)null,
                },
                unsupportedLanguageFiles = unsupportedLanguageItems.Count > 0
                    ? unsupportedLanguageItems
                    : null,
                unsupportedLanguageFilesCoverage = unsupportedLanguageFilesTotal == 0
                    ? null
                    : new
                    {
                        total = unsupportedLanguageFilesTotal,
                        returned = unsupportedLanguageItems.Count,
                        truncated = unsupportedLanguageItems.Count < unsupportedLanguageFilesTotal
                            ? true
                            : (bool?)null,
                    },
                baseBlobRecoveryCoverage = baseBlobRequests == 0 ? null : new
                {
                    attempted = baseBlobAttempts,
                    requested = baseBlobRequests,
                    recovered = baseBlobRecovered,
                    unavailable = baseBlobRequests - baseBlobRecovered,
                    retainedChars = baseBlobCharsRetained,
                    charLimit = ReviewMaxBaseBlobChars,
                    perFileByteLimit = ReviewMaxBaseBlobBytes,
                    attemptLimit = ReviewMaxBaseBlobAttempts,
                    elapsedLimitMs = ReviewMaxBaseBlobMilliseconds,
                    elapsedMs = baseBlobElapsedMilliseconds,
                    budgetHit = baseBlobBudgetHit ? true : (bool?)null,
                },
                namespaceAnalysisCoverage = namespaceAnalysisRequested == 0 ? null : new
                {
                    requested = namespaceAnalysisRequested,
                    parsed = namespaceAnalysisParsed,
                    retainedChars = namespaceAnalysisChars,
                    fileLimit = ReviewMaxNamespaceFiles,
                    charLimit = ReviewMaxNamespaceChars,
                    perFileCharLimit = ReviewMaxNamespacePerFileChars,
                    elapsedLimitMs = ReviewMaxNamespaceMilliseconds,
                    elapsedMs = namespaceAnalysisElapsedMilliseconds,
                    budgetHit = namespaceAnalysisBudgetHit ? true : (bool?)null,
                },
                projectOwnershipFallbackCoverage = cachedProjectShapes is null ? null : new
                {
                    requestedAtLeast = cachedProjectShapes.RequestedAtLeast,
                    attempted = cachedProjectShapes.Attempted,
                    parsed = cachedProjectShapes.Parsed.Count,
                    retainedBytes = cachedProjectShapes.BytesRead,
                    fileLimit = ReviewMaxProjectShapeFiles,
                    byteLimit = ReviewMaxProjectShapeBytes,
                    perFileByteLimit = ReviewMaxProjectShapePerFileBytes,
                    elapsedLimitMs = (long)cachedProjectShapes.MatchBudget.TimeLimit.TotalMilliseconds,
                    elapsedMs = cachedProjectShapes.ElapsedMilliseconds,
                    globSegmentLimit = cachedProjectShapes.MatchBudget.SegmentLimit,
                    globOperationLimit = cachedProjectShapes.MatchBudget.OperationLimit,
                    globOperations = cachedProjectShapes.MatchBudget.Operations,
                    shapeBudgetHit = cachedProjectShapes.ShapeBudgetHit ? true : (bool?)null,
                    globBudgetHit = cachedProjectShapes.GlobBudgetHit ? true : (bool?)null,
                    budgetHit = cachedProjectShapes.BudgetHit ? true : (bool?)null,
                    evaluationIncomplete = cachedProjectShapes.EvaluationIncomplete
                        ? true
                        : (bool?)null,
                    complete = cachedProjectShapes.Complete,
                },
                changedProjectFiles = projectItems.Count > 0 ? projectItems : null,
                changedProjectFilesCoverage = projectFiles.Count == 0 ? null : new
                {
                    total = projectFiles.Count,
                    returned = projectItems.Count,
                    truncated = projectItems.Count < projectFiles.Count ? true : (bool?)null,
                },
                changedSubmoduleLinks = changedSubmoduleLinks.Count == 0 ? null : new
                {
                    count = changedSubmoduleLinks.Count,
                    samplePaths = changedSubmoduleSample.Count > 0
                        ? changedSubmoduleSample
                        : null,
                    samplesTruncated = changedSubmoduleSample.Count < changedSubmoduleLinks.Count
                        ? true
                        : (bool?)null,
                },
                coverage = excludedSubmoduleWorktrees is null &&
                           excludedUntrackedRepositories is null &&
                           excludedUntrackedLinks is null
                    ? null
                    : new
                    {
                        submoduleWorktrees = excludedSubmoduleWorktrees is null
                            ? null
                            : new
                            {
                                status = "excluded",
                                count = excludedSubmoduleWorktrees.Count,
                                samplePaths = excludedSubmoduleSample.Count > 0
                                    ? excludedSubmoduleSample
                                    : null,
                                samplesTruncated = excludedSubmoduleWorktrees.SamplesTruncated ||
                                                   excludedSubmoduleSample.Count <
                                                   excludedSubmoduleWorktrees.Count
                                    ? true
                                    : (bool?)null,
                            },
                        untrackedRepositories = excludedUntrackedRepositories is null
                            ? null
                            : new
                            {
                                status = "excluded",
                                count = excludedUntrackedRepositories.Count,
                                samplePaths = excludedUntrackedRepositorySample.Count > 0
                                    ? excludedUntrackedRepositorySample
                                    : null,
                                samplesTruncated = excludedUntrackedRepositories.SamplesTruncated ||
                                                   excludedUntrackedRepositorySample.Count <
                                                   excludedUntrackedRepositories.Count
                                    ? true
                                     : (bool?)null,
                             },
                        untrackedLinks = excludedUntrackedLinks is null
                            ? null
                            : new
                            {
                                status = "excluded",
                                count = excludedUntrackedLinks.Count,
                                samplePaths = excludedUntrackedLinkSample.Count > 0
                                    ? excludedUntrackedLinkSample
                                    : null,
                                samplesTruncated = excludedUntrackedLinks.SamplesTruncated ||
                                                   excludedUntrackedLinkSample.Count <
                                                   excludedUntrackedLinks.Count
                                    ? true
                                    : (bool?)null,
                            },
                    },
                symbols = symbolItems,
                symbolsTruncated = symbolItems.Count < touchedSymbolsTotal ? true : (bool?)null,
                symbolsCoverage = new
                {
                    total = touchedSymbolsTotal,
                    returned = symbolItems.Count,
                    truncated = symbolItems.Count < touchedSymbolsTotal ? true : (bool?)null,
                },
                deletedFiles = deletedItems.Count > 0 ? deletedItems : null,
                deletedFilesCoverage = orderedDeleted.Count == 0 ? null : new
                {
                    total = orderedDeleted.Count,
                    returned = deletedItems.Count,
                    truncated = deletedItems.Count < orderedDeleted.Count ? true : (bool?)null,
                },
                formerSymbols = formerItems.Count > 0 ? formerItems : null,
                formerSymbolsCoverage = formerSymbolsTotal == 0 ? null : new
                {
                    total = formerSymbolsTotal,
                    returned = formerItems.Sum(file => file.FormerSymbols.Count),
                    truncated = formerItems.Sum(file => file.FormerSymbols.Count) <
                                formerSymbolsTotal
                        ? true
                        : (bool?)null,
                },
                movedFiles = movedFiles.Count == 0 ? null : new
                {
                    total = movedFiles.Count,
                    returned = movedItems.Count,
                    truncated = movedItems.Count < movedFiles.Count ? true : (bool?)null,
                    items = movedItems,
                },
                unmappedChanges = unmappedHunks.Count == 0 ? null : new
                {
                    total = unmappedHunks.Count,
                    returned = unmappedItems.Count,
                    truncated = unmappedItems.Count < unmappedHunks.Count ? true : (bool?)null,
                    items = unmappedItems,
                },
                notes = emittedNotes,
                meta,
            });
        }

        string json = BuildResponse();
        while (Json.Utf8Bytes(json) > maxBytes)
        {
            if (!byteBudgetTrimmed)
            {
                byteBudgetTrimmed = true;
                responseNotes.Add(new ReviewNote(NoteIds.ReviewByteBudget,
                    "Optional review evidence was trimmed to satisfy maxBytes; affected sections report total, returned, and truncated."));
            }

            bool trimmed = TrimReviewList(changedSubmoduleSample) ||
                           TrimReviewList(excludedSubmoduleSample) ||
                           TrimReviewList(excludedUntrackedRepositorySample) ||
                           TrimReviewList(excludedUntrackedLinkSample) ||
                           TrimReviewList(unsupportedLanguageItems) ||
                           TrimReviewList(projectItems) ||
                           TrimReviewList(symbolItems) ||
                           TrimReviewList(deletedItems) ||
                           TrimReviewList(movedItems) ||
                           TrimReviewList(formerItems) ||
                           TrimReviewList(unmappedItems);
            if (!trimmed && !compactNoteText)
            {
                compactNoteText = true;
                trimmed = true;
            }
            if (!trimmed)
            {
                return BoundedReviewError("response_budget_exhausted",
                    "The fixed review_pack metadata exceeded maxBytes; retry with a larger budget.",
                    maxBytes, meta);
            }
            json = BuildResponse();
        }
        return json;
    }

    private static List<SymbolHit> SelectReviewableHits(
        IReadOnlyList<SymbolHit> hits, IReadOnlyList<(int Start, int End)> ranges)
    {
        var selectedIds = new HashSet<long>();
        foreach ((int Start, int End) range in ranges)
        {
            List<SymbolHit> rangeHits = hits.Where(hit =>
                    hit.StartLine <= range.End && hit.EndLine >= range.Start)
                .ToList();
            var fullyCoveredTypes = rangeHits
                .Where(hit => (hit.Kind is "class" or "interface" or "struct" or "record" or
                                   "record_struct" or "enum") &&
                              range.Start <= hit.StartLine && hit.EndLine <= range.End)
                .Select(hit => hit.Id)
                .ToHashSet();
            var parentIds = rangeHits.Where(hit => hit.ParentId is not null)
                .Select(hit => hit.ParentId!.Value)
                .ToHashSet();
            foreach (SymbolHit hit in rangeHits.Where(hit =>
                         hit.Kind != "namespace" &&
                         !(hit.ParentId is { } parentId &&
                           fullyCoveredTypes.Contains(parentId)) &&
                         (fullyCoveredTypes.Contains(hit.Id) || !parentIds.Contains(hit.Id))))
            {
                selectedIds.Add(hit.Id);
            }
        }
        return hits.Where(hit => selectedIds.Contains(hit.Id)).ToList();
    }

    private static List<SymbolRow> SelectReviewableSyntaxSymbols(
        IReadOnlyList<SymbolRow> symbols, IReadOnlyList<(int Start, int End)> ranges)
    {
        List<SymbolRow> hits = symbols.Where(symbol => ranges.Any(range =>
                symbol.StartLine <= range.End && symbol.EndLine >= range.Start))
            .ToList();
        var selectedOrdinals = new HashSet<int>();
        foreach ((int Start, int End) range in ranges)
        {
            List<SymbolRow> rangeHits = hits.Where(symbol =>
                    symbol.StartLine <= range.End && symbol.EndLine >= range.Start)
                .ToList();
            var fullyCoveredTypes = rangeHits
                .Where(symbol => (symbol.Kind is "class" or "interface" or "struct" or
                                      "record" or "record_struct" or "enum") &&
                                 range.Start <= symbol.StartLine && symbol.EndLine <= range.End)
                .Select(symbol => symbol.OrdinalInFile)
                .ToHashSet();
            var parentOrdinals = rangeHits.Where(symbol => symbol.ParentOrdinal >= 0)
                .Select(symbol => symbol.ParentOrdinal)
                .ToHashSet();
            foreach (SymbolRow symbol in rangeHits.Where(symbol =>
                         symbol.Kind != "namespace" &&
                         !(symbol.ParentOrdinal >= 0 &&
                           fullyCoveredTypes.Contains(symbol.ParentOrdinal)) &&
                         (fullyCoveredTypes.Contains(symbol.OrdinalInFile) ||
                          !parentOrdinals.Contains(symbol.OrdinalInFile))))
            {
                selectedOrdinals.Add(symbol.OrdinalInFile);
            }
        }
        return hits.Where(symbol => selectedOrdinals.Contains(symbol.OrdinalInFile)).ToList();
    }

    private static string SyntaxIdentity(SymbolRow symbol,
        IReadOnlyList<SymbolRow> symbols) => string.Join('\u001f',
        SyntaxAncestorIdentity(symbol, symbols), symbol.Kind, symbol.Name,
        symbol.Namespace ?? "", symbol.Arity.ToString(
            System.Globalization.CultureInfo.InvariantCulture),
        symbol.DeclarationKey ?? symbol.Signature);

    private static string SyntaxIdentity(SymbolHit symbol,
        IReadOnlyDictionary<long, SymbolHit> symbolsById) => string.Join('\u001f',
        SyntaxAncestorIdentity(symbol, symbolsById), symbol.Kind, symbol.Name,
        symbol.Ns ?? "", symbol.Arity.ToString(
            System.Globalization.CultureInfo.InvariantCulture),
        symbol.DeclarationKey ?? symbol.Signature);

    private static string SyntaxAncestorIdentity(SymbolRow symbol,
        IReadOnlyList<SymbolRow> symbols)
    {
        var ancestors = new Stack<string>();
        int parent = symbol.ParentOrdinal;
        var seen = new HashSet<int>();
        while (parent >= 0 && parent < symbols.Count && seen.Add(parent))
        {
            SymbolRow ancestor = symbols[parent];
            ancestors.Push(string.Join(':', ancestor.Kind, ancestor.Name,
                ancestor.Arity.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            parent = ancestor.ParentOrdinal;
        }
        return string.Join('/', ancestors);
    }

    private static string SyntaxAncestorIdentity(SymbolHit symbol,
        IReadOnlyDictionary<long, SymbolHit> symbolsById)
    {
        var ancestors = new Stack<string>();
        long? parent = symbol.ParentId;
        var seen = new HashSet<long>();
        while (parent is { } id && seen.Add(id) && symbolsById.TryGetValue(id,
                   out SymbolHit? ancestor))
        {
            ancestors.Push(string.Join(':', ancestor.Kind, ancestor.Name,
                ancestor.Arity.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            parent = ancestor.ParentId;
        }
        return string.Join('/', ancestors);
    }

    private static ReviewReferenceCoverage ReferenceCoverage(
        IndexQueries.ReferenceCandidateResult result) => new(
        result.CandidateFilesScanned, result.CandidateFilesAtLeast,
        result.CandidateFileLimit,
        result.DeclarationExclusionBudgetHit ? true : null,
        result.DeclarationExclusionApplied ? result.DeclarationFilesParsed : null,
        result.DeclarationExclusionApplied ? result.DeclarationFileParseLimit : null,
        result.DeclarationExclusionApplied ? result.DeclarationCharsParsed : null,
        result.DeclarationExclusionApplied ? result.DeclarationCharLimit : null,
        result.DeclarationExclusionApplied ? result.DeclarationPerFileCharLimit : null,
        result.DeclarationExclusionApplied ? result.DeclarationBytesParsed : null,
        result.DeclarationExclusionApplied ? result.DeclarationByteLimit : null,
        result.DeclarationExclusionApplied ? result.DeclarationPerFileByteLimit : null);

    private static bool BelongsToTopLevelType(SymbolRow symbol, SymbolRow topLevelType,
        IReadOnlyList<SymbolRow> symbols)
    {
        int parent = symbol.ParentOrdinal;
        var seen = new HashSet<int>();
        while (parent >= 0 && parent < symbols.Count && seen.Add(parent))
        {
            if (parent == topLevelType.OrdinalInFile) return true;
            parent = symbols[parent].ParentOrdinal;
        }
        return false;
    }

    internal static ReviewProjectShapeSnapshot LoadProjectShapesBounded(string root,
        IReadOnlyList<ProjectRow> projects, bool projectCountLimited = false,
        GlobMatchBudget? matchBudgetOverride = null)
    {
        var parsed = new Dictionary<long, ParsedProject>();
        GlobMatchBudget matchBudget = matchBudgetOverride ?? new GlobMatchBudget(
            ReviewMaxProjectGlobSegments, ReviewMaxProjectGlobOperations,
            TimeSpan.FromMilliseconds(ReviewMaxProjectShapeMilliseconds));
        if (projectCountLimited)
        {
            // A partial project universe cannot prove owner preservation. Avoid spending the
            // remaining XML budget on a snapshot that must be discarded as non-proof anyway.
            return new ReviewProjectShapeSnapshot(projects.ToList(), parsed,
                projects.Count + 1, 0, 0, 0, true, false, matchBudget);
        }
        int attempted = 0;
        int bytesRead = 0;
        bool budgetHit = false;
        bool evaluationIncomplete = false;
        var timer = Stopwatch.StartNew();
        foreach (ProjectRow project in projects)
        {
            int remainingBytes = ReviewMaxProjectShapeBytes - bytesRead;
            if (attempted >= ReviewMaxProjectShapeFiles || remainingBytes <= 0 ||
                timer.ElapsedMilliseconds >= ReviewMaxProjectShapeMilliseconds)
            {
                budgetHit = true;
                break;
            }
            if (!string.Equals(project.LoadStatus, "parsed", StringComparison.Ordinal))
            {
                evaluationIncomplete = true;
                continue;
            }
            if (!CodeNav.Core.WorkspacePaths.TryResolveGitPathInside(root, project.Path,
                    out string fullPath))
            {
                evaluationIncomplete = true;
                continue;
            }

            attempted++;
            byte[]? snapshot = GitInfo.ReadBoundedRegularFile(fullPath,
                Math.Min(ReviewMaxProjectShapePerFileBytes, remainingBytes), root);
            if (snapshot is null)
            {
                budgetHit = true;
                continue;
            }
            bytesRead += snapshot.Length;
            ParsedProject shape = ProjectFileParser.ParseCompileShape(project.Path, snapshot);
            if (!string.Equals(shape.LoadStatus, "parsed", StringComparison.Ordinal))
            {
                evaluationIncomplete = true;
                continue;
            }
            if (!shape.CompileOwnershipComplete ||
                HasImplicitBuildCustomization(root, fullPath))
            {
                evaluationIncomplete = true;
                continue;
            }
            parsed[project.Id] = shape;
        }
        timer.Stop();
        if (timer.ElapsedMilliseconds >= ReviewMaxProjectShapeMilliseconds)
            budgetHit = true;
        return new ReviewProjectShapeSnapshot(projects.ToList(), parsed,
            projects.Count + (projectCountLimited ? 1 : 0), attempted, bytesRead,
            timer.ElapsedMilliseconds, budgetHit, evaluationIncomplete, matchBudget);
    }

    private static bool HasImplicitBuildCustomization(string root, string projectFullPath)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string? directory = Path.GetDirectoryName(projectFullPath);
        if (directory is null ||
            !(string.Equals(directory, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
              directory.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
                  StringComparison.OrdinalIgnoreCase)))
            return true;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Directory.Build.props")) ||
                File.Exists(Path.Combine(directory, "Directory.Build.targets")) ||
                File.Exists(Path.Combine(directory, "Directory.Packages.props")) ||
                File.Exists(Path.Combine(directory, "Directory.Build.rsp")) ||
                File.Exists(Path.Combine(directory, "MSBuild.rsp")))
            {
                return true;
            }
            directory = Path.GetDirectoryName(directory);
        }
        if (File.Exists(projectFullPath + ".user")) return true;
        return false;
    }

    internal static HashSet<long> LikelyOwningProjectIds(string filePath,
        IReadOnlyList<ProjectRow> projects, IReadOnlyDictionary<long, ParsedProject> parsedProjects,
        bool? ignoreCaseOverride = null, GlobMatchBudget? matchBudget = null,
        Action? onMatchAttempt = null, Action? afterDefaultSdkEvaluationForTest = null)
    {
        string normalizedFile = CodeNav.Core.WorkspacePaths.Normalize(filePath);
        string sourceLanguage = ReviewSourceLanguage(normalizedFile);
        bool ignoreCase = ignoreCaseOverride ?? OperatingSystem.IsWindows();
        matchBudget ??= new GlobMatchBudget(ReviewMaxProjectGlobSegments,
            ReviewMaxProjectGlobOperations,
            TimeSpan.FromMilliseconds(ReviewMaxProjectShapeMilliseconds));
        onMatchAttempt?.Invoke();
        if (!OwnershipBudgetCheckpoint(matchBudget)) return [];
        StringComparison pathComparison = ignoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var owners = new HashSet<long>();
        foreach (ProjectRow project in projects)
        {
            if (!OwnershipBudgetCheckpoint(matchBudget)) return [];
            // Compile items are language-specific. A raw Include in an fsproj cannot prove
            // ownership of C# source (or vice versa), even when the path/glob itself matches.
            if (!project.Language.Equals(sourceLanguage, StringComparison.Ordinal)) continue;
            if (!parsedProjects.TryGetValue(project.Id, out ParsedProject? parsed)) continue;
            string projectPath = CodeNav.Core.WorkspacePaths.Normalize(project.Path);
            int slash = projectPath.LastIndexOf('/');
            string directory = slash < 0 ? "" : projectPath[..slash];
            bool owns = false;
            if (parsed.DefaultCompileItems)
            {
                if (!OwnershipBudgetCheckpoint(matchBudget)) return [];
                owns = IsDefaultSdkCompileItem(normalizedFile, directory, pathComparison);
                afterDefaultSdkEvaluationForTest?.Invoke();
                if (!OwnershipBudgetCheckpoint(matchBudget)) return [];
            }

            // MSBuild item operations are ordered. SDK default items exist first, a Remove only
            // deletes an item currently present, and a later Include may add it back. An Include
            // whose Exclude matches is a no-op; it must not remove an item supplied earlier.
            foreach (CompileMembershipOperation operation in
                     parsed.CompileOperations ?? Enumerable.Empty<CompileMembershipOperation>())
            {
                GlobMatchOutcome operationMatch = MsBuildGlob.Match(normalizedFile,
                    operation.Pattern, ignoreCase, matchBudget);
                if (operationMatch == GlobMatchOutcome.BudgetExhausted) return [];
                if (operationMatch != GlobMatchOutcome.Matched) continue;
                if (operation.Include)
                {
                    bool excluded = false;
                    foreach (string exclude in operation.Excludes ?? [])
                    {
                        GlobMatchOutcome excludeMatch = MsBuildGlob.Match(normalizedFile,
                            exclude, ignoreCase, matchBudget);
                        if (excludeMatch == GlobMatchOutcome.BudgetExhausted) return [];
                        if (excludeMatch == GlobMatchOutcome.Matched)
                        {
                            excluded = true;
                            break;
                        }
                    }
                    if (!excluded)
                        owns = true;
                }
                else
                {
                    owns = false;
                }
            }
            if (owns) owners.Add(project.Id);
        }
        return owners;
    }

    private static string ReviewSourceLanguage(string normalizedPath)
        => IsFSharpSourcePath(normalizedPath) ? "fs" : "cs";

    private static bool IsFSharpSourcePath(string normalizedPath)
    {
        string extension = Path.GetExtension(normalizedPath);
        return extension.Equals(".fs", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".fsi", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".fsx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool OwnershipBudgetCheckpoint(GlobMatchBudget budget) =>
        MsBuildGlob.Match("", "", ignoreCase: false, budget) !=
        GlobMatchOutcome.BudgetExhausted;

    private static bool IsDefaultSdkCompileItem(string normalizedFile, string projectDirectory,
        StringComparison pathComparison)
    {
        string relative;
        if (projectDirectory.Length == 0)
        {
            relative = normalizedFile;
        }
        else
        {
            string prefix = projectDirectory + "/";
            if (!normalizedFile.StartsWith(prefix, pathComparison))
                return false;
            relative = normalizedFile[prefix.Length..];
        }
        if (relative.Length == 0) return false;
        string[] segments = relative.Split('/');
        if (segments.Length > 1 && segments[..^1].Any(segment => segment.StartsWith('.')))
            return false;
        // These are the standard Microsoft.NET.Sdk defaults. Any project override of their base
        // properties already marks the raw shape incomplete above.
        return !segments[0].Equals("bin", pathComparison) &&
               !segments[0].Equals("obj", pathComparison);
    }

    internal static bool IsProjectShapePath(string path)
    {
        string normalized = CodeNav.Core.WorkspacePaths.Normalize(path);
        string fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
        return normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".csproj.user", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".fsproj.user", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".shproj", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".proj", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".projitems", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("Directory.Build.rsp", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("MSBuild.rsp", StringComparison.OrdinalIgnoreCase);
    }

    private static List<(int Start, int End)> UncoveredLineRanges(
        (int Start, int End) target, IEnumerable<(int Start, int End)> coveredRanges)
    {
        var covered = coveredRanges
            .Where(range => range.Start <= target.End && range.End >= target.Start)
            .Select(range => (Start: Math.Max(target.Start, range.Start),
                End: Math.Min(target.End, range.End)))
            .OrderBy(range => range.Start).ThenBy(range => range.End)
            .ToList();
        var gaps = new List<(int Start, int End)>();
        int cursor = target.Start;
        foreach (var range in covered)
        {
            if (range.Start > cursor) gaps.Add((cursor, range.Start - 1));
            if (range.End >= cursor)
            {
                cursor = range.End == int.MaxValue ? int.MaxValue : range.End + 1;
            }
            if (cursor > target.End) break;
        }
        if (cursor <= target.End) gaps.Add((cursor, target.End));
        return gaps;
    }

    private static bool MovePreservesReviewableCSharp(string root,
        string fromPath, string toPath, IndexQueries queries,
        Func<string, string?> baseContent,
        Func<string, HashSet<long>> ownerIds,
        Func<string, List<SymbolHit>> outline,
        bool projectShapeChanged)
    {
        if (projectShapeChanged ||
            !fromPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
            !toPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
            !CodeNav.Core.WorkspacePaths.TryResolveGitPathInside(root, toPath,
                out string fullPath) ||
            !File.Exists(fullPath))
        {
            return false;
        }
        if (CodeNav.Core.WorkspacePaths.EscapesViaReparsePoint(root, fullPath)) return false;

        string? oldContent = baseContent(fromPath);
        if (oldContent is null) return false;
        if (queries.ContentByPath(toPath) is null) return false;
        HashSet<long> oldOwners = ownerIds(fromPath);
        HashSet<long> newOwners = ownerIds(toPath);
        if (oldOwners.Count == 0 || !oldOwners.IsSubsetOf(newOwners))
        {
            return false;
        }
        List<SymbolRow> oldSymbols = SyntaxIndexer.Parse(fromPath, oldContent).Symbols;
        List<string> oldIdentities = oldSymbols
            .Where(symbol => symbol.Kind != "namespace")
            .Select(symbol => SyntaxIdentity(symbol, oldSymbols))
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToList();
        List<SymbolHit> currentSymbols = outline(toPath);
        var currentById = currentSymbols.ToDictionary(symbol => symbol.Id);
        List<string> currentIdentities = currentSymbols
            .Where(symbol => symbol.Kind != "namespace")
            .Select(symbol => SyntaxIdentity(symbol, currentById))
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToList();
        return oldIdentities.SequenceEqual(currentIdentities, StringComparer.Ordinal);
    }

    private static bool TrimReviewList<T>(List<T> items)
    {
        if (items.Count == 0) return false;
        int keep = items.Count / 2;
        items.RemoveRange(keep, items.Count - keep);
        return true;
    }

    internal static Dictionary<string, List<(int Start, int End)>> NewReviewPathMap() =>
        new(StringComparer.Ordinal);

    internal static bool ReviewGitPathExists(string workspaceRoot, string gitPath) =>
        CodeNav.Core.WorkspacePaths.TryResolveGitPathInside(workspaceRoot, gitPath,
            out string fullPath) && File.Exists(fullPath);

    internal static List<SymbolHit> FilterExistingReviewDeclarations(string workspaceRoot,
        string excludedGitPath, IEnumerable<SymbolHit> candidates) => candidates
        .Where(hit => !string.Equals(hit.FilePath, excludedGitPath,
                          StringComparison.Ordinal) &&
                      ReviewGitPathExists(workspaceRoot, hit.FilePath))
        .ToList();

    internal static (string Error, string Detail) ReviewGitFailure(string status) =>
        status switch
        {
            "ok" => ("git_diff_malformed",
                "Git diff parsing completed without a file manifest; no partial result was returned - pass explicit 'paths' instead."),
            "invalid_commit" => ("bad_request",
                "The resolved base is no longer a valid commit; choose an existing commit or ref and retry."),
            "config_failed" => ("git_config_failed",
                "Git configuration could not be inspected safely, so review_pack refused to run a diff that might invoke repository filters - pass explicit 'paths' instead."),
            "filter_unsafe" => ("git_filter_unsafe",
                "Tracked paths use configured Git clean/process filters. review_pack will not execute those helpers or claim a helper-free diff is complete - pass explicit 'paths' instead."),
            "process_failed" => ("git_diff_failed",
                "The bounded git diff process failed or timed out - pass explicit 'paths' instead."),
            "malformed" => ("git_diff_malformed",
                "Git produced output outside review_pack's deterministic diff format; no partial result was returned - pass explicit 'paths' instead."),
            "unmerged" => ("git_unmerged",
                "Git reports unmerged paths; resolve the conflicts, or pass explicit 'paths' to review selected files."),
            "layered_changes" => ("git_layered_changes",
                "At least one path has different staged and worktree payloads. A single final-worktree diff cannot cover both byte layers, so review_pack refused to bless bytes different from a possible commit; align or stage the intended payload, then retry."),
            "snapshot_changed" => ("git_worktree_changed",
                "Repeated raw patch and typed status captures did not match because the Git index or working tree changed during review_pack; no partial result was returned - retry against a stable workspace."),
            "status_failed" => ("git_status_failed",
                "Git status could not be captured safely within the bounded subprocess contract; no partial result was returned - retry or pass explicit 'paths' instead."),
            _ => ("git_diff_failed",
                $"Git diff failed with an unexpected status ('{status}'); no partial result was returned - pass explicit 'paths' instead."),
        };

    internal static string BoundedReviewNotReady(IndexHealth health, int maxBytes)
    {
        int cap = Math.Clamp(maxBytes, 2048, Json.HardBudgetBytes);
        string error = health.State == "building" ? "index_building" : "index_unavailable";
        string hint = health.State == "building"
            ? "The workspace index is still building (first run). Retry shortly; use shell tools meanwhile."
            : "Index unavailable. Falling back to shell search is appropriate.";
        string json = Json.Serialize(new
        {
            error,
            state = health.State,
            detail = health.Error,
            progress = ProgressJson(health),
            hint,
        });
        if (Json.Utf8Bytes(json) <= cap) return json;

        string state = Json.Utf8Prefix(health.State, CapabilityIdentityTextBytes,
            out bool stateTruncated);
        json = Json.Serialize(new
        {
            error,
            state,
            stateTruncated = stateTruncated ? true : (bool?)null,
            stateBytes = stateTruncated ? Json.Utf8Bytes(health.State) : (int?)null,
            detail = "Index status detail and progress were omitted to satisfy maxBytes; poll server_capabilities for the bounded health envelope.",
            meta = new
            {
                confidence = "indexed",
                navigationLayer = "text",
                build = BuildInfo.Stamp,
                indexSchema = BuildInfo.IndexSchema,
            },
        });
        if (Json.Utf8Bytes(json) <= cap) return json;

        return BoundedReviewError(error,
            "Index status detail and progress were omitted to satisfy maxBytes.", cap,
            Meta.From(health, "indexed", "text"));
    }

    internal static string BoundedReviewError(string error, string detail, int maxBytes,
        Meta meta)
    {
        int cap = Math.Clamp(maxBytes, 2048, Json.HardBudgetBytes);
        string json = Json.Serialize(new { error, detail, meta });
        if (Json.Utf8Bytes(json) <= cap) return json;

        // error is always an internal stable id. Keep the fallback fixed-size so this helper
        // remains a hard guarantee even if a future detail accidentally includes caller input.
        json = Json.Serialize(new
        {
            error,
            detail = "Request failed; detail was omitted to satisfy maxBytes.",
            meta,
        });
        if (Json.Utf8Bytes(json) <= cap) return json;

        // The normal envelope is expected to fit the 2 KiB minimum. Retain its deployment and
        // confidence identity even if future optional Meta prose grows beyond that minimum.
        return Json.Serialize(new
        {
            error,
            detail = "Request failed; detail was omitted to satisfy maxBytes.",
            meta = new
            {
                confidence = "indexed",
                navigationLayer = "text",
                build = BuildInfo.Stamp,
                indexSchema = BuildInfo.IndexSchema,
            },
        });
    }

    internal static List<string> BoundedReviewPathSample(IEnumerable<string> paths,
        int maxJsonBytes = 512)
    {
        const int maxPaths = 8;
        var sample = new List<string>();
        int bytes = 0;
        foreach (string path in paths.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (sample.Count >= maxPaths) break;
            int pathBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(path).Length + 1;
            if (pathBytes > Math.Max(0, maxJsonBytes) - bytes) continue;
            sample.Add(path);
            bytes += pathBytes;
        }
        return sample;
    }

    private static List<(int Start, int End)> WholeFile() => new() { (1, int.MaxValue - 1) };
}
