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
    public const string ReviewProjectFilesChanged = "review.project_files_changed"; // project/solution/build shape in the diff — graph may shift
    // Kept for backwards compatibility with v0.11.0 responses. New emitters use one id per
    // cause below so clients can distinguish a caller-selected symbol cap from a byte budget.
    public const string ReviewSymbolsTruncated = "review.symbols_truncated";
    public const string ReviewSymbolCountCap = "review.symbol_count_cap";
    public const string ReviewByteBudget = "review.byte_budget";
    public const string ReviewChangedFilesCap = "review.changed_files_cap";
    public const string ReviewDeletedFilesCap = "review.deleted_files_cap";
    public const string ReviewFormerTypesCap = "review.former_types_cap";
    public const string ReviewFormerSymbolsCap = "review.former_symbols_cap";
    public const string ReviewFormerSymbolDangling = "review.former_symbol_dangling";
    public const string ReviewReferenceCandidatesCap = "review.reference_candidates_cap";
    public const string ReviewReferenceDeclarationBudget = "review.reference_declaration_budget";
    public const string ReviewUnmappedHunks = "review.unmapped_hunks";
    public const string ReviewBaseBlobUnavailable = "review.base_blob_unavailable";
    public const string ReviewBaseBlobBudget = "review.base_blob_budget";
    public const string ReviewNamespaceAnalysisBudget = "review.namespace_analysis_budget";
    public const string ReviewProjectShapeBudget = "review.project_shape_budget";
    public const string ReviewProjectGlobBudget = "review.project_glob_budget";
    public const string ReviewProjectShapeIncomplete = "review.project_shape_incomplete";
    public const string ReviewSurvivingDeclarationsCap = "review.surviving_declarations_cap";
    public const string ReviewSubmoduleWorktreesExcluded = "review.submodule_worktrees_excluded"; // child dirt needs child-root review
    public const string ReviewUntrackedRepositoriesExcluded = "review.untracked_repositories_excluded"; // nested repos need child-root review
    public const string ReviewUntrackedLinksExcluded = "review.untracked_links_excluded"; // Git path crosses a symlink/junction

    // retrofits (additive noteId beside the existing prose note)
    public const string ReferencesZeroLoadingGap = "references.zero_loading_gap";       // exact 0 but base-list namers exist
    public const string ImpactTransitiveSingleCount = "impact.transitive_single_count"; // by-design single transitive number
    public const string HierarchyHeuristicFallback = "type_hierarchy.heuristic_fallback"; // derived list degraded to base-list
    public const string SearchDidYouMean = "search_text.did_you_mean";           // a probed variant/spelling suggestion exists
    public const string SearchElsewhereMatches = "search_text.elsewhere_matches"; // 0 here, precise lines exist outside filters
    public const string SearchAbsentEverywhere = "search_text.absent_everywhere"; // provably no file holds all tokens
}
