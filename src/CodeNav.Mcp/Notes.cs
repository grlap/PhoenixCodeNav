namespace CodeNav.Mcp;

/// <summary>
/// Owns: the STABLE note-id catalog (a0b) — every recurring diagnostic note gets a
/// machine-matchable id so programmatic consumers (the review system, the field's spec rows)
/// match ids instead of prose. Field directive (v0.9.2 review): ids are the contract; prose
/// may be reworded freely, ids may not. Conventions: dotted lowercase, tool-or-domain prefix,
/// one id per CAUSE (never per wording). Ids ship ADDITIVELY beside the existing prose
/// fields — no consumer of the prose breaks. The cluster_cold_load prefix token (t2b)
/// pioneered the pattern; this catalog generalizes it.
/// Does not own: note WORDING (each emitter's) or when notes fire (each tool's honesty rules).
/// Split out of: nothing — new for a0b.
/// </summary>
internal static class NoteIds
{
    // review_pack
    public const string ReviewIndexedOnly = "review.indexed_only";               // digests are index-backed; escalate via handles
    public const string ReviewDeletedDangling = "review.deleted_dangling";       // deleted file's former symbols still referenced
    public const string ReviewProjectFilesChanged = "review.project_files_changed"; // csproj/sln in the diff — graph may shift
    public const string ReviewSymbolsTruncated = "review.symbols_truncated";     // symbol cap or byte budget trimmed the digests

    // retrofits (additive noteId beside the existing prose note)
    public const string ReferencesZeroLoadingGap = "references.zero_loading_gap";       // exact 0 but base-list namers exist
    public const string ImpactTransitiveSingleCount = "impact.transitive_single_count"; // by-design single transitive number
    public const string HierarchyHeuristicFallback = "type_hierarchy.heuristic_fallback"; // derived list degraded to base-list
    public const string SearchDidYouMean = "search_text.did_you_mean";           // a probed variant/spelling suggestion exists
    public const string SearchElsewhereMatches = "search_text.elsewhere_matches"; // 0 here, precise lines exist outside filters
    public const string SearchAbsentEverywhere = "search_text.absent_everywhere"; // provably no file holds all tokens
}
