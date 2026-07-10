using System.ComponentModel;
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
    private const int ReviewMaxFiles = 200;        // changed .cs files mapped to symbols
    private const int ReviewMaxDeletedFiles = 20;  // deleted files re-parsed from the base blob
    private const int ReviewMaxTypesPerDeleted = 5;

    [McpServerTool(Name = "review_pack")]
    [Description("ONE budget-bounded review digest: diff -> touched symbols -> per-symbol impact. Default reviews the working tree against HEAD (uncommitted changes); pass baseRef (sha or branch name, e.g. the merge-base) to review a whole branch; or pass explicit paths. Digests are INDEX-backed (confidence indexed) and each carries a symbolId handle — escalate chosen symbols via references(symbolId, mode:'semantic'). Deleted files report their FORMER top-level types' dangling reference candidates.")]
    public string ReviewPack(
        [Description("Base to diff against: a commit sha or a ref name (strict charset; typically the merge-base). Default: HEAD — reviews uncommitted changes only.")] string? baseRef = null,
        [Description("Comma-separated workspace-relative paths to review INSTEAD of a git diff (whole-file granularity; no git needed).")] string? paths = null,
        [Description("Byte budget (default 16384, max 24576).")] int maxBytes = 16384,
        [Description("Max touched symbols digested (default 40, max 100).")] int maxSymbols = 40)
    {
        if (NotReady() is { } notReady) return notReady;
        maxBytes = Math.Clamp(maxBytes, 2048, Json.HardBudgetBytes);
        maxSymbols = Math.Clamp(maxSymbols, 1, 100);
        using var q = _manager.OpenQueries();
        string root = _manager.WorkspaceRoot;
        var notes = new List<object>();

        // ---- 1. The changed set: (file -> ranges) + deleted + untracked ----
        var changed = new Dictionary<string, List<(int Start, int End)>>(StringComparer.OrdinalIgnoreCase);
        var deleted = new List<string>();
        string? resolvedBase = null;
        int untrackedCount = 0;
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
                return Json.Serialize(new
                {
                    error = "git_unavailable",
                    detail = $"review_pack needs git for diff mode (HEAD: {headStatus}) — pass explicit 'paths' instead.",
                    meta = Meta.From(_manager.Health(), "indexed", "text"),
                });
            }
            resolvedBase = baseRef is null ? head : GitInfo.ResolveRef(root, baseRef);
            if (resolvedBase is null)
            {
                return Json.Serialize(new
                {
                    error = "bad_request",
                    detail = $"baseRef '{baseRef}' did not resolve — pass a commit sha or a simple ref name (letters/digits and / - _ .).",
                });
            }
            var hunks = GitInfo.DiffHunks(root, resolvedBase);
            var dirty = GitInfo.DirtyFiles(root);
            if (hunks is null || dirty is null)
            {
                return Json.Serialize(new
                {
                    error = "git_diff_failed",
                    detail = "git could not produce the diff (shallow clone / unrelated base?) — pass explicit 'paths' instead.",
                    meta = Meta.From(_manager.Health(), "indexed", "text"),
                });
            }
            foreach (var f in hunks)
            {
                if (f.Deleted) deleted.Add(f.Path);
                else changed[f.Path] = f.Ranges.Count > 0 ? f.Ranges : WholeFile();
            }
            // Untracked files never appear in a diff against a commit — union the dirt,
            // whole-file (they are entirely new, and files deleted-on-disk stay with `deleted`).
            foreach (var p in dirty)
            {
                if (!changed.ContainsKey(p) && !deleted.Contains(p, StringComparer.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(root, p)))
                {
                    changed[p] = WholeFile();
                    untrackedCount++;
                }
            }
        }

        // ---- 2. Partition: .cs -> symbols; project/solution/config files listed as-is ----
        var csFiles = changed.Keys.Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Take(ReviewMaxFiles).ToList();
        var projectFiles = changed.Keys.Where(p =>
                p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (projectFiles.Count > 0)
        {
            notes.Add(new
            {
                id = NoteIds.ReviewProjectFilesChanged,
                text = $"{projectFiles.Count} project/build file(s) changed — dependency edges, compile sets, or test classification may shift; check project_graph on the affected projects.",
            });
        }

        // ---- 3. Hunks -> symbols (span intersection, innermost policy) ----
        var touched = new List<SymbolHit>();
        var seenIds = new HashSet<long>();
        foreach (var file in csFiles)
        {
            var ranges = changed[file];
            // Review F2: SymbolsIntersecting caps at 64 ranges — beyond that the tail hunks
            // were SILENTLY dropped (no flag, no note). A 65+-hunk file is effectively a
            // rewrite: the documented whole-file fallback applies, and the fully-covered-type
            // rule below then digests it honestly at TYPE level.
            if (ranges.Count > 64) ranges = WholeFile();
            var hits = q.SymbolsIntersecting(file, ranges);
            // Granularity policy, two-sided (test-driven — the one-sided innermost rule
            // surfaced 'method Run' for a brand-new file and swallowed the class):
            //  * a type FULLY covered by a changed range (new file, whole-type addition) is
            //    the reviewable unit — it SWALLOWS its members (one digest, not N);
            //  * a PARTIALLY touched type defers to its touched members (editing a method
            //    body is a method review, not a class review); a type-only touch
            //    (attribute/base-list line) has no touched child and survives itself.
            var fullyCoveredTypes = hits
                .Where(h => h.Kind is "class" or "interface" or "struct" or "record" or "record_struct" or "enum"
                            && ranges.Any(r => r.Start <= h.StartLine && h.EndLine <= r.End))
                .Select(h => h.Id)
                .ToHashSet();
            var parentIds = new HashSet<long>(hits.Where(h => h.ParentId is not null).Select(h => h.ParentId!.Value));
            foreach (var h in hits)
            {
                if (h.Kind == "namespace") continue;      // never a reviewable unit
                if (h.ParentId is { } pid && fullyCoveredTypes.Contains(pid)) continue; // its new type represents it
                if (!fullyCoveredTypes.Contains(h.Id) && parentIds.Contains(h.Id)) continue; // touched child represents it
                if (seenIds.Add(h.Id)) touched.Add(h);
            }
        }
        bool symbolCapHit = touched.Count > maxSymbols;
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
            int refCandidates = q.ReferenceCandidates(h.Name, 200, 0).TotalHits;
            bool isPublic = h.Accessibility == "public";
            bool isOrphaned = orphaned.Contains(h.FilePath);

            var risks = new List<string>();
            if (isPublic && facts.Transitive > 0) risks.Add($"public symbol; {facts.Transitive} projects transitively depend on {owner}");
            if (facts.HintOnly > 0) risks.Add($"{facts.HintOnly} of {facts.Direct} direct dependents reach {owner} only via <Reference>/HintPath — refactor tooling won't follow those edges");
            if (tests.Count == 0) risks.Add("no test signal found for this symbol");
            if (isOrphaned) risks.Add("declared in a file NO project compiles — verify this change is even built");

            return new
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
                referenceCandidates = refCandidates,
                relatedTests = tests.Count == 0 ? null : tests.Select(t => new
                {
                    project = t.TestProject, t.Reason, signal = t.Signal,
                }),
                risks = risks.Count > 0 ? risks : null,
            };
        }).ToList();

        // ---- 5. Deletion honesty: former top-level types + dangling reference candidates ----
        var deletedOut = new List<object>();
        if (deleted.Count > 0 && resolvedBase is not null)
        {
            foreach (var path in deleted.Take(ReviewMaxDeletedFiles))
            {
                if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) { deletedOut.Add(new { path }); continue; }
                string? content = GitInfo.ShowFile(root, resolvedBase, path);
                if (content is null) { deletedOut.Add(new { path }); continue; }
                var parsed = SyntaxIndexer.Parse(path, content);
                var formerTypes = parsed.Symbols
                    .Where(s => string.IsNullOrEmpty(s.Container) &&
                                s.Kind is "class" or "interface" or "struct" or "record" or "record_struct" or "enum" or "delegate")
                    .Take(ReviewMaxTypesPerDeleted)
                    .Select(s =>
                    {
                        var refs = q.ReferenceCandidates(s.Name, 100, 1);
                        return new
                        {
                            s.Name,
                            s.Kind,
                            danglingCandidates = refs.TotalHits,
                            samplePaths = refs.Groups.SelectMany(g => g.Samples).Select(x => x.FilePath)
                                .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList(),
                        };
                    })
                    .ToList();
                deletedOut.Add(new { path, formerTypes = formerTypes.Count > 0 ? formerTypes : null });
                if (formerTypes.Any(t => t.danglingCandidates > 0))
                {
                    notes.Add(new
                    {
                        id = NoteIds.ReviewDeletedDangling,
                        text = $"'{path}' was deleted but its former top-level type(s) are still named elsewhere (see deletedFiles[].formerTypes.danglingCandidates) — likely broken references.",
                    });
                }
            }
        }

        notes.Add(new
        {
            id = NoteIds.ReviewIndexedOnly,
            text = "Digests are index-backed (confidence indexed): reference counts are whole-identifier candidates and dependents come from the stored graph. Escalate chosen symbols with references(symbolId, mode:'semantic') / impact(symbolId).",
        });

        var meta = Meta.From(_manager.Health(), "indexed", "text");
        return Json.WithListBudget(digests, (items, truncated) =>
        {
            bool trimmed = truncated || symbolCapHit;
            return new
            {
                baseRef = resolvedBase, // null in explicit-paths mode
                changedFiles = new
                {
                    total = changed.Count + deleted.Count,
                    cs = csFiles.Count,
                    projectFiles = projectFiles.Count,
                    deleted = deleted.Count,
                    untracked = untrackedCount > 0 ? untrackedCount : (int?)null,
                },
                changedProjectFiles = projectFiles.Count > 0 ? projectFiles : null,
                symbols = items,
                symbolsTruncated = trimmed ? true : (bool?)null,
                deletedFiles = deletedOut.Count > 0 ? deletedOut : null,
                notes = trimmed
                    ? notes.Append(new
                      {
                          id = NoteIds.ReviewSymbolsTruncated,
                          text = "the touched-symbol list was trimmed (symbol cap or byte budget) — narrow with 'paths' or raise maxSymbols/maxBytes",
                      }).ToList()
                    : notes,
                meta,
            };
        }, maxBytes);
    }

    private static List<(int Start, int End)> WholeFile() => new() { (1, int.MaxValue - 1) };
}
