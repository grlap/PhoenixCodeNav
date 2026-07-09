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
/// Name collisions (two workspace projects producing the same assembly name — the monolith's
/// net-old/net-new pairing idiom) resolve to the FIRST row: the whole downstream is name-keyed
/// (graph queries and closures return names; the semantic workspace loads and merges BY name),
/// so an edge to any row is the same name-level fact — the original no-edge policy silently
/// severed every consumer of a PAIRED declarer (field 0.7.2: cold references returned exact 0).
/// A HintPath into a never-indexed dir (packages/bin/obj/...) marks the dll EXTERNAL: no edge
/// even on a name match (review: the NuGet-vs-vendored-fork false edge). Matching is by the
/// Include simple name only — no HintPath-basename fallback (review-reproduced false-edge source).
/// Does not own: parsing (ProjectFileParser.AssemblyRefs) or how the semantic layer exploits the
/// edges (SemanticWorkspace substitutes source for binary when the target project is loaded).
/// Split out of: the ProjectReference-relpath resolution loops in IndexBuilder / DeltaRefresher.
/// </summary>
internal static class AssemblyRefEdges
{
    /// <summary>Inserts <c>project_refs</c> edges for assembly refs that name an in-workspace
    /// project. Returns (Recovered, NameCollisions) counts for build logs. Idempotent with the
    /// relpath-resolved ProjectReference edges (INSERT OR IGNORE on the same PK).</summary>
    public static (int Recovered, int NameCollisions) Write(
        IndexStore store, SqliteTransaction tx,
        IReadOnlyList<ParsedProject> parsedProjects,
        IReadOnlyDictionary<string, long> projectIdsByRelPath)
    {
        // Assembly-name -> project id. ParsedProject.Name already prefers <AssemblyName> over the
        // csproj file name, which is the name a <Reference>'s Include simple name actually refers
        // to. FAILED-parse projects carry only a file-derived name guess — they never claim a slot.
        //
        // NAME COLLISIONS resolve to the FIRST row, not to no-edge (field 0.7.2 regression): the
        // monolith's net-old/net-new idiom pairs csprojs under ONE assembly name — including the
        // flagship declarer itself — and the old poison-to-null silently severed EVERY consumer's
        // edge to it (cold references loaded 1/1 and returned an "exact" zero). Picking a row is
        // safe because the entire downstream is NAME-keyed: ProjectGraph/closures return names,
        // and the semantic workspace loads BY NAME — merging a pair's compile sets into one adhoc
        // project regardless of which row an edge points at. An edge to any row is the same
        // name-level fact; no-edge is the only wrong answer.
        var byName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        int nameCollisions = 0;
        foreach (var p in parsedProjects)
        {
            if (p.LoadStatus.StartsWith("failed", StringComparison.Ordinal)) continue;
            if (!projectIdsByRelPath.TryGetValue(p.RelPath, out long id)) continue;
            if (!byName.TryAdd(p.Name, id)) nameCollisions++;
        }

        int recovered = 0;
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
                if (!byName.TryGetValue(assembly, out long toId)) continue; // not an in-workspace assembly
                if (toId == fromId) continue;                                // self-reference guard
                store.InsertProjectRef(tx, fromId, toId);
                recovered++;
            }
        }
        return (recovered, nameCollisions);
    }
}
