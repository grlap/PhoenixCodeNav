using CodeNav.Core.Discovery;
using Microsoft.Data.Sqlite;

namespace CodeNav.Core.Indexing;

internal sealed record PersistedVariant(EvaluatedProjectVariant Variant, long Id, long ContextId);

internal sealed class VariantWriteState
{
    public Dictionary<string, List<PersistedVariant>> ByProject { get; } =
        new(WorkspacePaths.FileSystemPathComparer);
    public Dictionary<long, (bool Complete, List<string> Reasons)> GraphCoverage { get; } = new();
}

internal static class VariantIndexWriter
{
    public static VariantWriteState WriteProjectFacts(IndexStore store, SqliteTransaction tx,
        IReadOnlyDictionary<string, ProjectVariantEvaluation> evaluations,
        IReadOnlyDictionary<string, long> projectIds,
        IReadOnlyDictionary<string, long> fileIds)
    {
        var state = new VariantWriteState();
        foreach (var pair in projectIds.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var persisted = new List<PersistedVariant>();
            if (evaluations.TryGetValue(pair.Key, out ProjectVariantEvaluation? evaluation))
            {
                List<KeyValuePair<string, long>> implicitInputs = fileIds.Where(candidate =>
                    IsImplicitBuildInput(pair.Key, candidate.Key)).ToList();
                foreach (EvaluatedProjectVariant variant in evaluation.Variants)
                {
                    long variantId = store.UpsertProjectVariant(tx, pair.Value, variant.DimensionKey,
                        variant.StableVariantKey, variant.TargetFramework, variant.Configuration,
                        variant.Platform, variant.AssemblyName, variant.TargetName, variant.TargetExt,
                        variant.Coverage.Complete);
                    long contextId = store.UpsertParseContext(tx, variant.ParseContext.ContextKey,
                        variant.ParseContext.LanguageVersion, variant.ParseContext.PreprocessorSymbols,
                        variant.ParseContext.Complete);
                    store.SetVariantParseContext(tx, variantId, contextId);
                    foreach (EvaluatedOutput output in variant.Outputs)
                        store.InsertVariantOutput(tx, variantId, output.OutputPath, output.TargetPath,
                            output.Condition, output.Evidence);
                    if (fileIds.TryGetValue(pair.Key, out long projectFileId))
                        store.InsertVariantStructuralInput(tx, variantId, projectFileId, "project", "evaluated");
                    string packagesPath = PackagesConfigPath(pair.Key);
                    if (fileIds.TryGetValue(packagesPath, out long packagesFileId))
                        store.InsertVariantStructuralInput(tx, variantId, packagesFileId, "packages", "evaluated");
                    foreach (string structuralPath in variant.StructuralInputPaths)
                        if (fileIds.TryGetValue(structuralPath, out long structuralFileId))
                            store.InsertVariantStructuralInput(tx, variantId, structuralFileId,
                                "project_import", "conservative");
                    foreach (var structural in implicitInputs)
                        store.InsertVariantStructuralInput(tx, variantId, structural.Value,
                            "project_customization", "conservative");
                    persisted.Add(new PersistedVariant(variant, variantId, contextId));
                }
            }
            state.ByProject[pair.Key] = persisted;
        }

        foreach (var pair in state.ByProject.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (PersistedVariant source in pair.Value)
            {
                bool graphComplete = source.Variant.Coverage.ReferenceGraphComplete;
                var reasons = new HashSet<string>(source.Variant.Coverage.Reasons, StringComparer.Ordinal);
                foreach (EvaluatedItemFact fact in source.Variant.ProjectReferenceFacts)
                {
                    long factId = store.InsertProjectReferenceFact(tx, source.Id, fact.Include,
                        fact.Metadata, fact.Condition, fact.EvaluationStatus);
                    long resolutionId = store.InsertReferenceResolution(tx, "project", factId,
                        source.Id, "unresolved", fact.EvaluationStatus == "evaluated");
                    List<PersistedVariant> candidates = state.ByProject.TryGetValue(fact.Include,
                        out List<PersistedVariant>? targets)
                        ? CompatibleVariants(source.Variant, targets) : [];
                    var ids = InsertSourceCandidates(store, tx, resolutionId, candidates,
                        "projectReference", fact.Metadata);
                    if (ids.Count == 1)
                        store.SelectReferenceResolutionCandidate(tx, resolutionId, ids[0].CandidateId,
                            source.Id, ids[0].TargetId, "projectReference");
                    else
                    {
                        graphComplete = false;
                        reasons.Add(ids.Count == 0 ? "unresolved_project_reference" : "ambiguous_project_variant");
                    }
                }

                foreach (EvaluatedItemFact fact in source.Variant.AssemblyReferenceFacts)
                {
                    long factId = store.InsertAssemblyReferenceFact(tx, source.Id, fact.Include,
                        fact.Metadata, fact.Condition, fact.EvaluationStatus);
                    long resolutionId = store.InsertReferenceResolution(tx, "assembly", factId,
                        source.Id, "unresolved", fact.EvaluationStatus == "evaluated");
                    var physical = state.ByProject.Where(project => project.Value.Any(variant =>
                        string.Equals(AssemblySimpleName(variant.Variant.AssemblyName),
                            AssemblySimpleName(fact.Include),
                            StringComparison.OrdinalIgnoreCase)) &&
                        !string.Equals(project.Key, pair.Key,
                            WorkspacePaths.FileSystemPathComparison)).ToList();
                    if (fact.Metadata is { Length: > 0 } gatedHint &&
                        WorkspaceScanner.IsExcludedPath(gatedHint)) physical = [];
                    List<PersistedVariant> candidates = physical.Count == 1
                        ? CompatibleVariants(source.Variant, physical[0].Value) : [];
                    if (fact.Metadata is { Length: > 0 } hint)
                    {
                        List<PersistedVariant> exactOutputs = (physical.Count == 1
                                ? physical[0].Value : [])
                            .Where(target => target.Variant.Outputs.Any(output =>
                                string.Equals(output.TargetPath, hint,
                                    WorkspacePaths.FileSystemPathComparison))).ToList();
                        if (exactOutputs.Count > 0) candidates = exactOutputs;
                    }
                    var ids = InsertSourceCandidates(store, tx, resolutionId, candidates,
                        "hintPathReference", fact.Metadata);
                    if (ids.Count == 1)
                        store.SelectReferenceResolutionCandidate(tx, resolutionId, ids[0].CandidateId,
                            source.Id, ids[0].TargetId, "hintPathReference");
                    else
                    {
                        if (ids.Count != 0 || physical.Count > 1)
                        {
                            graphComplete = false;
                            reasons.Add("ambiguous_assembly_reference");
                        }
                    }
                }

                foreach (EvaluatedItemFact fact in source.Variant.PackageReferenceFacts)
                {
                    store.InsertVariantPackageReference(tx, source.Id, fact.Include,
                        fact.Metadata ?? "", fact.Condition, fact.EvaluationStatus);
                }
                store.SetVariantFactCoverage(tx, source.Id,
                    source.Variant.Coverage.ParseContextComplete,
                    source.Variant.Coverage.CompileOwnershipComplete, graphComplete, reasons);
                state.GraphCoverage[source.Id] = (graphComplete,
                    reasons.OrderBy(reason => reason, StringComparer.Ordinal).ToList());
            }
        }
        return state;
    }

    public static void WriteCompileAndBaseFacts(IndexStore store, SqliteTransaction tx,
        VariantWriteState state, IReadOnlyDictionary<string, long> projectIds)
    {
        var indexed = new HashSet<(long FileId, long ContextId)>();
        foreach (var pair in state.ByProject.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!projectIds.TryGetValue(pair.Key, out long projectId)) continue;
            foreach (PersistedVariant persisted in pair.Value)
            {
                bool conditioned = persisted.Variant.CompileFacts.Any(fact =>
                    !string.IsNullOrWhiteSpace(fact.Condition));
                bool ownershipComplete = persisted.Variant.Coverage.CompileOwnershipComplete &&
                                         !conditioned;
                store.CopyProjectCompileItemsToVariant(tx, projectId, persisted.Id,
                    ownershipComplete ? "evaluated" : "conservative");
                (bool graphComplete, List<string> graphReasons) = state.GraphCoverage.TryGetValue(
                    persisted.Id, out var graph) ? graph : (false, ["missing_reference_resolution"]);
                if (!ownershipComplete) graphReasons.Add("variant_compile_incomplete");
                store.SetVariantFactCoverage(tx, persisted.Id,
                    persisted.Variant.Coverage.ParseContextComplete, ownershipComplete,
                    graphComplete, graphReasons);

                foreach (long fileId in store.GetVariantCompileItemIdsForWrite(tx, persisted.Id))
                {
                    if (!indexed.Add((fileId, persisted.ContextId))) continue;
                    string? content = store.GetContentForWrite(fileId);
                    if (content is null) continue;
                    var context = new BaseTypeParseContext(persisted.Variant.ParseContext.LanguageVersion,
                        persisted.Variant.ParseContext.PreprocessorSymbols);
                    foreach (BaseTypeFact fact in BaseTypeIndexer.Parse(content, context))
                        store.InsertBaseTypeFact(tx, persisted.ContextId, fileId,
                            fact.DeclarationOccurrence, fact.Ordinal, fact.RawTypeText, fact.LookupName,
                            fact.SyntacticArity, fact.QualifierText, fact.ResolutionKind, fact.ScopeEvidence);
                }
            }
        }

    }

    private static List<(long CandidateId, long TargetId)> InsertSourceCandidates(IndexStore store,
        SqliteTransaction tx, long resolutionId, IReadOnlyList<PersistedVariant> candidates,
        string provenance, string? hintPath)
    {
        var ids = new List<(long, long)>();
        int rank = 0;
        foreach (PersistedVariant target in candidates)
        {
            string? matched = target.Variant.Outputs.FirstOrDefault(output => hintPath is not null &&
                string.Equals(output.TargetPath, hintPath,
                    WorkspacePaths.FileSystemPathComparison))?.TargetPath;
            long id = store.InsertReferenceResolutionCandidate(tx, resolutionId, target.Id, null,
                provenance, "compatible", matched, rank++);
            ids.Add((id, target.Id));
        }
        return ids;
    }

    private static List<PersistedVariant> CompatibleVariants(EvaluatedProjectVariant source,
        IReadOnlyList<PersistedVariant> targets)
    {
        List<PersistedVariant> exact = targets.Where(target =>
                string.Equals(target.Variant.TargetFramework, source.TargetFramework,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(target.Variant.Configuration, source.Configuration,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(target.Variant.Platform, source.Platform,
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(target => target.Variant.DimensionKey, StringComparer.Ordinal).ToList();
        return exact.Count > 0 ? exact : targets.Count == 1 ? [targets[0]] : [];
    }

    private static string PackagesConfigPath(string projectPath)
    {
        int slash = projectPath.LastIndexOf('/');
        return slash < 0 ? "packages.config" : projectPath[..(slash + 1)] + "packages.config";
    }

    private static string AssemblySimpleName(string name) =>
        name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static bool IsImplicitBuildInput(string projectPath, string candidatePath)
    {
        string name = Path.GetFileName(candidatePath);
        if (name is not ("Directory.Build.props" or "Directory.Build.targets" or
            "Directory.Packages.props")) return false;
        string projectDirectory = WorkspacePaths.ToGitPath(Path.GetDirectoryName(projectPath) ?? "");
        string candidateDirectory = WorkspacePaths.ToGitPath(Path.GetDirectoryName(candidatePath) ?? "");
        return candidateDirectory.Length == 0 || projectDirectory.Equals(candidateDirectory,
            WorkspacePaths.FileSystemPathComparison) || projectDirectory.StartsWith(
            candidateDirectory + "/", WorkspacePaths.FileSystemPathComparison);
    }
}
