using CodeNav.WorkspaceGen;

string? outDir = null;
int projects = 2000;
int seed = 1337;
double density = 1.0;
bool clean = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out": outDir = args[++i]; break;
        case "--projects": projects = int.Parse(args[++i]); break;
        case "--seed": seed = int.Parse(args[++i]); break;
        case "--density": density = double.Parse(args[++i]); break;
        case "--clean": clean = true; break;
        case "--help" or "-h":
            Console.WriteLine("Usage: workspacegen --out <dir> [--projects 2000] [--seed 1337] [--density 1.0] [--clean]");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

if (outDir is null)
{
    Console.Error.WriteLine("Missing required --out <dir>.");
    return 2;
}

if (clean && Directory.Exists(outDir))
{
    Console.WriteLine($"Cleaning {outDir} ...");
    Directory.Delete(outDir, recursive: true);
}

Console.WriteLine($"Generating synthetic workspace: {projects} projects, seed {seed}, density {density}");
var stats = WorkspaceGenerator.Generate(outDir, projects, seed, density);

Console.WriteLine();
Console.WriteLine($"  projects   : {stats.Projects} ({stats.LegacyProjects} legacy, {stats.SdkProjects} SDK-style, {stats.TestProjects} test)");
Console.WriteLine($"  solutions  : {stats.Solutions} (+1 .slnf), orphan projects: {stats.Orphans}");
Console.WriteLine($"  files      : {stats.Files:N0}");
Console.WriteLine($"  lines      : {stats.Lines:N0}");
Console.WriteLine($"  size       : {stats.Bytes / (1024.0 * 1024.0):F1} MB");
Console.WriteLine($"  plan/emit  : {stats.PlanTime.TotalSeconds:F1}s / {stats.EmitTime.TotalSeconds:F1}s");
Console.WriteLine();
Console.WriteLine($"Workspace root: {Path.GetFullPath(outDir)}");
return 0;
