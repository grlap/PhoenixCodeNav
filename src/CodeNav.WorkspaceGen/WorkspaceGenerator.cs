using System.Diagnostics;

namespace CodeNav.WorkspaceGen;

public sealed record WorkspaceStats(
    int Projects,
    int LegacyProjects,
    int SdkProjects,
    int TestProjects,
    int Solutions,
    int Orphans,
    long Files,
    long Lines,
    long Bytes,
    TimeSpan PlanTime,
    TimeSpan EmitTime);

/// <summary>
/// Public façade over the planner + emitters so CodeNav.Bench and CodeNav.Tests can
/// generate workspaces in-process. The CLI in Program.cs is a thin wrapper over this.
/// </summary>
public static class WorkspaceGenerator
{
    public static WorkspaceStats Generate(string outDir, int targetProjects = 2000, int seed = 1337, double density = 1.0)
    {
        var sw = Stopwatch.StartNew();
        var planner = new WorkspacePlanner(targetProjects, seed, density);
        var spec = planner.Plan();
        var planTime = sw.Elapsed;

        sw.Restart();
        var emitter = new ProjectEmitter(spec, outDir);
        var (files, lines, bytes) = emitter.EmitAll();
        var emitTime = sw.Elapsed;

        return new WorkspaceStats(
            Projects: spec.Projects.Count,
            LegacyProjects: spec.Projects.Count(p => p.Style == ProjectStyle.Legacy),
            SdkProjects: spec.Projects.Count(p => p.Style == ProjectStyle.Sdk),
            TestProjects: spec.Projects.Count(p => p.IsTest),
            Solutions: spec.Solutions.Count,
            Orphans: planner.Orphans.Count,
            Files: files,
            Lines: lines,
            Bytes: bytes,
            PlanTime: planTime,
            EmitTime: emitTime);
    }
}
