namespace CodeNav.Core.Indexing;

public sealed record ProjectVariantRow(
    long Id,
    long ProjectId,
    string ProjectPath,
    string ProjectName,
    string DimensionKey,
    string StableVariantKey,
    string TargetFramework,
    string Configuration,
    string Platform,
    string AssemblyName,
    string TargetName,
    string TargetExt,
    bool EvaluationComplete,
    bool IsTest);

public sealed record ParseContextRow(
    long Id,
    string ContextKey,
    string LanguageVersion,
    IReadOnlyList<string> PreprocessorSymbols,
    bool IsComplete);

public sealed record VariantFactCoverageRow(
    long VariantId,
    bool ParseContextComplete,
    bool CompileOwnershipComplete,
    bool ReferenceGraphComplete,
    IReadOnlyList<string> Reasons)
{
    public bool Complete => ParseContextComplete && CompileOwnershipComplete && ReferenceGraphComplete;
}

public sealed record VariantOutputRow(
    long Id,
    long VariantId,
    string OutputPath,
    string TargetPath,
    string? Condition,
    string Evidence);

public sealed record VariantReferenceEdgeRow(
    long ResolutionId,
    long FromVariantId,
    long TargetVariantId,
    string Provenance);

public sealed record VariantReferenceCandidateRow(
    long Id,
    long ResolutionId,
    long? TargetVariantId,
    string? BinaryPath,
    string Provenance,
    string Compatibility,
    string? MatchedOutputPath,
    int Rank);

public sealed record VariantAssemblyReferenceInput(
    string IncludeName,
    string? HintPath,
    string EvaluationStatus,
    bool HasSelectedSourceVariant);

public sealed record VariantPackageReferenceInput(
    string Package,
    string Version,
    string EvaluationStatus);

public sealed record BaseTypeIndexRow(
    long Id,
    long ParseContextId,
    long FileId,
    string DeclarationOccurrence,
    int Ordinal,
    string RawTypeText,
    string LookupName,
    int SyntacticArity,
    string? QualifierText,
    string ResolutionKind,
    string? ScopeEvidence);

public sealed record VariantCandidateUniverse(
    IReadOnlyList<ProjectVariantRow> MandatoryVariants,
    IReadOnlyList<ProjectVariantRow> OptionalVariants,
    IReadOnlySet<long> DirectSeedVariantIds,
    IReadOnlySet<long> GraphGapVariantIds,
    bool GraphComplete,
    bool CompileOwnershipComplete,
    bool ParseContextsComplete,
    IReadOnlyList<string> IncompleteReasons)
{
    public bool Complete => GraphComplete && CompileOwnershipComplete && ParseContextsComplete;
}
