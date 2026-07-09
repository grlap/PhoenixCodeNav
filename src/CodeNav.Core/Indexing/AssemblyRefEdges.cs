using CodeNav.Core.Discovery;
using Microsoft.Data.Sqlite;

namespace CodeNav.Core.Indexing;

/// <summary>
/// Owns: recovering project-graph edges from legacy ASSEMBLY references — &lt;Reference Include&gt;
/// items whose simple name (or HintPath dll basename) is the assembly name of an IN-WORKSPACE
/// project. Multi-staged builds (field, lhg: "phase one, we build bunch stuff to common folder,
/// then we reference assembly NOT a project") leave no &lt;ProjectReference&gt;, so without this
/// the dependency graph — and everything built on it: dependents-closure candidate discovery,
/// semantic cluster topo-order and reference wiring, project_graph — cannot see that the
/// referencing project consumes the referenced one. That blindness is exactly why
/// implementations/references on a cross-project interface returned zero while the syntactic
/// base-list index could see all 8 implementers.
/// Ambiguous names (two workspace projects producing the same assembly name) create NO edge —
/// a wrong edge would substitute the wrong SOURCE into semantic compilations, worse than a hole.
/// A HintPath into a never-indexed dir (packages/bin/obj/...) marks the dll EXTERNAL: no edge
/// even on a name match (review: the NuGet-vs-vendored-fork false edge). Matching is by the
/// Include simple name only — no HintPath-basename fallback (review-reproduced false-edge source).
/// Does not own: parsing (ProjectFileParser.AssemblyRefs) or how the semantic layer exploits the
/// edges (SemanticWorkspace substitutes source for binary when the target project is loaded).
/// Split out of: the ProjectReference-relpath resolution loops in IndexBuilder / DeltaRefresher.
/// </summary>
internal static class AssemblyRefEdges
{
    /// <summary>Inserts <c>project_refs</c> edges for assembly refs that uniquely name an
    /// in-workspace project. Returns (Recovered, Ambiguous) counts for build logs. Idempotent
    /// with the relpath-resolved ProjectReference edges (INSERT OR IGNORE on the same PK).</summary>
    public static (int Recovered, int Ambiguous) Write(
        IndexStore store, SqliteTransaction tx,
        IReadOnlyList<ParsedProject> parsedProjects,
        IReadOnlyDictionary<string, long> projectIdsByRelPath)
    {
        // Assembly-name -> project id, collisions poisoned to null. ParsedProject.Name already
        // prefers <AssemblyName> over the csproj file name, which is the name a <Reference>'s
        // Include simple name actually refers to. FAILED-parse projects carry only a file-derived
        // name guess — they never claim (or poison) a slot (review: a failed csproj file-named
        // like a real assembly either stole its slot or falsely ambiguated it).
        var byName = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parsedProjects)
        {
            if (p.LoadStatus.StartsWith("failed", StringComparison.Ordinal)) continue;
            if (!projectIdsByRelPath.TryGetValue(p.RelPath, out long id)) continue;
            byName[p.Name] = byName.ContainsKey(p.Name) ? null : id;
        }

        int recovered = 0, ambiguous = 0;
        foreach (var p in parsedProjects)
        {
            if (!projectIdsByRelPath.TryGetValue(p.RelPath, out long fromId)) continue;
            foreach (var (assembly, hint) in p.AssemblyRefs)
            {
                // A HintPath into a NEVER-INDEXED dir (packages/, bin/, obj/, ...) is strong
                // evidence the dll is genuinely EXTERNAL (a NuGet or build-output binary) even
                // when its simple name collides with a workspace project — e.g. a vendored
                // Newtonsoft.Json fork project vs the real packages/ dll (review, MEDIUM: the
                // false edge also made the dll-substitution SEVER the consumer's real binding).
                // Multi-stage common output folders are regular indexed dirs, so the field
                // shape passes this gate. No-hint references keep name-only matching (the bare
                // <Reference Include="ET.SomeLib"/> output-dir-probing legacy idiom).
                if (hint is not null && Discovery.WorkspaceScanner.IsExcludedPath(hint)) continue;
                // Simple name only ("ET.Api.Generated, Version=..." was split by the parser).
                // No basename fallback: mapping an aliased Include via its dll file name was
                // speculative and review-reproduced as a false-edge source (Include="VendorLib"
                // whose HintPath file name happens to match a workspace project).
                if (!byName.TryGetValue(assembly, out long? toId)) continue; // not an in-workspace assembly
                if (toId is null) { ambiguous++; continue; }                 // name collision — no edge
                if (toId.Value == fromId) continue;                          // self-reference guard
                store.InsertProjectRef(tx, fromId, toId.Value);
                recovered++;
            }
        }
        return (recovered, ambiguous);
    }
}
