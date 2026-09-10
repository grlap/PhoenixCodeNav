using CodeNav.Core.Discovery;
using Microsoft.Data.Sqlite;

namespace CodeNav.Core.Indexing;

/// <summary>Harvests concrete Exists requests with the same bounded evaluator and indexed
/// authority used by F# queries. Only the writer observes the filesystem; query-time evaluation
/// consumes these pinned facts. Dependencies belong to the project, not the imported document.</summary>
internal static class MsBuildExistsCapture
{
    internal static bool Refresh(IndexStore store, SqliteTransaction tx, string root,
        Semantic.ProjectModelMode fsharpProjectModel,
        IReadOnlyCollection<string>? changedPaths = null,
        string? publishedWorkspaceRoot = null,
        Action<string>? log = null)
    {
        using var queries = new IndexQueries(tx);
        bool wasReady = queries.EvaluatedFSharpInputsReady();
        if (fsharpProjectModel == Semantic.ProjectModelMode.Simple)
        {
            // Discard rather than revive stale negative facts after a later model switch.
            store.ClearMsBuildExistsInputs(tx);
            store.SetMeta(tx, "fsharp_evaluated_inputs", "disabled");
            if (wasReady)
                log?.Invoke("Simple F# model: discarded evaluated Exists facts. Switching back requires full rediscovery.");
            return wasReady;
        }
        if (!wasReady)
            log?.Invoke("Preparing evaluated F# inputs: full Exists rediscovery in the index writer transaction.");
        bool changed = !wasReady;
        var owners = new HashSet<long>();
        var observed = new Dictionary<string, bool?>(StringComparer.Ordinal);
        bool? Observe(string path)
        {
            if (!WorkspaceScanner.IsIndexedFilePath(path)) return null;
            if (observed.TryGetValue(path, out bool? existing)) return existing;
            // Zero is the existing metadata-only classification request, not a new limit.
            var result = GitInfo.ReadBoundedWorkspaceFileResult(root, path, 0);
            bool? presence = result.Disposition switch
            {
                GitInfo.WorkspaceFileReadDisposition.Success or GitInfo.WorkspaceFileReadDisposition.Oversized => true,
                GitInfo.WorkspaceFileReadDisposition.Missing => false,
                _ => null
            };
            observed[path] = presence;
            return presence;
        }

        // Native aliases may not match watcher spelling. Reobserve only retained probe targets,
        // never enumerate the filesystem. Changed facts can make previously skipped branches live.
        foreach (string path in store.MsBuildExistsPaths(tx))
        {
            if (!store.UpdateMsBuildExistsPresence(tx, path, Observe(path))) continue;
            changed = true;
            owners.UnionWith(store.MsBuildExistsOwners(tx, path));
        }

        // Import/property changes may affect several projects, including newly introduced
        // Directory.Build authority. Ordinary source/config-content edits need no project scan;
        // only projects with changed existence facts are reevaluated on that path.
        bool rediscoverAll = !wasReady || changedPaths is null || changedPaths.Any(IsProjectInput);
        // Cold build supplies the publication identity before meta exists. Delta reads that
        // same identity in its writer transaction; root remains the separate anchored I/O path.
        string? logicalRoot = publishedWorkspaceRoot ?? queries.ReadMetadata().WorkspaceRoot;
        foreach ((long id, string path, string tfms, string xml) in
                 store.MsBuildExistsProjects(tx, rediscoverAll ? null : owners))
        {
            var probes = new HashSet<string>(StringComparer.Ordinal);
            bool? Capture(string probe)
            {
                if (!WorkspaceScanner.IsIndexedFilePath(probe)) return null;
                probes.Add(probe);
                return Observe(probe);
            }
            string? Import(string requested)
            {
                FileHit? file = queries.FileByPathForHost(requested);
                return file is { Language: "config" } && file.Size <= ProjectFileParser.MaxFSharpSemanticImportBytes
                    ? queries.ContentByPathBounded(file.Path, ProjectFileParser.MaxFSharpSemanticImportBytes)
                    : null;
            }
            var directoryBuild = queries.ApplicableDirectoryBuildAuthority(path);
            var directoryPackages = queries.ApplicableDirectoryPackagesAuthority(path);
            foreach (string tfm in tfms.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _ = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot(path, xml, tfms, tfm,
                    Import, requested => queries.FileByPathForHost(requested) is { Language: "config" } file
                        ? file.Size : null,
                    directoryPackages.Path, directoryBuild.PropsPath, directoryBuild.TargetsPath,
                    hasAmbiguousDirectoryBuildAuthority: directoryBuild.HasAmbiguity,
                    hasAmbiguousDirectoryPackagesAuthority: directoryPackages.PathAmbiguous,
                    existsResolver: Capture, workspaceRoot: logicalRoot,
                    diagnosticOrigin: publishedWorkspaceRoot is null ? "index.refresh" : "index.build");
            }
            changed |= store.ReplaceMsBuildExistsPaths(tx, id, probes);
        }
        // Replacing an owner's dependency set creates nullable rows. Every exact target gets
        // the same single observation across owners/TFMs and both phases of this transaction.
        foreach ((string path, bool? presence) in observed)
            changed |= store.UpdateMsBuildExistsPresence(tx, path, presence);
        store.SetMeta(tx, "fsharp_evaluated_inputs", "ready");
        return changed;
    }

    private static bool IsProjectInput(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".fsproj" or ".props" or ".targets";
}
