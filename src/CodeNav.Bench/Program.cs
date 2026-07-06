using System.Diagnostics;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using Microsoft.Data.Sqlite;

string? workspace = null;
bool rebuild = false;
bool semantic = false;
int iters = 25;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--workspace": workspace = args[++i]; break;
        case "--rebuild": rebuild = true; break;
        case "--semantic": semantic = true; break;
        case "--iters": iters = int.Parse(args[++i]); break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

if (workspace is null)
{
    Console.Error.WriteLine("Usage: bench --workspace <dir> [--rebuild] [--semantic] [--iters 25]");
    return 2;
}

string dbPath = IndexBuilder.DefaultDbPath(workspace);

// ---------------------------------------------------------------- semantic scenario
if (semantic)
{
    return await RunSemanticScenario(workspace, dbPath);
}

static async Task<int> RunSemanticScenario(string workspace, string dbPath)
{
    Console.WriteLine("=== Semantic layer scenario (2k-scale cluster loading)");
    using var manager = new IndexManager(workspace, dbPath, msg => Console.WriteLine($"  [index] {msg}"));
    manager.Start();
    for (int i = 0; i < 300 && !manager.IsQueryable; i++) Thread.Sleep(100);
    if (!manager.IsQueryable)
    {
        Console.Error.WriteLine("Index not queryable — build it first (run without --semantic).");
        return 1;
    }

    using var sem = new SemanticService(manager);
    var swTotal = Stopwatch.StartNew();

    async Task<double> Time(string label, Func<Task<string>> op)
    {
        var sw = Stopwatch.StartNew();
        string outcome = await op();
        double ms = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  {label,-58} {ms,9:F0}ms  {outcome}");
        return ms;
    }

    // Target: a real Guard.NotNull usage inside an Application project (big-ish cluster).
    string usagePath;
    int usageLine, usageCol;
    string ifaceName, ifacePath;
    int ifaceLine;
    using (var q = manager.OpenQueries())
    {
        var hit = q.SearchText("Guard.NotNull", 20, new IndexQueries.TextFilter(PathGlob: "src/*/*/*Application/**")).First();
        string content = q.ContentByPath(hit.FilePath)!;
        string realLine = content.Split('\n')[hit.Line - 1];
        usagePath = hit.FilePath;
        usageLine = hit.Line;
        usageCol = realLine.IndexOf("NotNull", StringComparison.Ordinal) + 1;

        // A medium interface: declared in a Contracts project, has candidates.
        var iface = q.SearchSymbols("IInvoice", "prefix", new[] { "interface" }, 5).FirstOrDefault()
                    ?? q.SearchSymbols("I", "prefix", new[] { "interface" }, 20).First(s => s.Name != "IClock");
        ifaceName = iface.Name;
        ifacePath = iface.FilePath;
        ifaceLine = iface.StartLine;
        Console.WriteLine($"  targets: usage {usagePath}:{usageLine}, interface {ifaceName}");
    }

    await Time("definition COLD (cluster load incl. ref assemblies)", async () =>
    {
        var (d, r) = await sem.DefinitionAsync(usagePath, usageLine, usageCol, null, 120000);
        return d is not null ? $"→ {d.DocumentationCommentId}" : $"FAILED: {r}";
    });

    await Time("definition WARM (same position)", async () =>
    {
        var (d, r) = await sem.DefinitionAsync(usagePath, usageLine, usageCol, null, 120000);
        return d is not null ? "ok" : $"FAILED: {r}";
    });

    await Time($"references MEDIUM ({ifaceName}, maxProjects 24)", async () =>
    {
        var (res, r) = await sem.ReferencesAsync(ifacePath, ifaceLine, null, ifaceName, 24, 2, 120000);
        return res is not null
            ? $"→ {res.TotalLocations} refs, {res.Groups.Count} projects, skipped {res.SkippedCandidateProjects.Count}"
            : $"FAILED: {r}";
    });

    await Time("references HOT (Guard, maxProjects 24 of ~85 candidates)", async () =>
    {
        using var q = manager.OpenQueries();
        var guard = q.SearchSymbols("Guard", "exact", new[] { "class" }, 1).Single();
        var (res, r) = await sem.ReferencesAsync(guard.FilePath, guard.StartLine, null, "Guard", 24, 1, 120000);
        return res is not null
            ? $"→ {res.TotalLocations} refs, {res.Groups.Count} projects, skipped {res.SkippedCandidateProjects.Count} (partial by design)"
            : $"FAILED: {r}";
    });

    await Time($"implementations ({ifaceName})", async () =>
    {
        var (res, r) = await sem.ImplementationsAsync(ifacePath, ifaceLine, null, ifaceName, 24, 120000);
        return res is not null ? $"→ {res.Implementations.Count} implementations" : $"FAILED: {r}";
    });

    await Time("definition WARM after big loads (cache retained)", async () =>
    {
        var (d, r) = await sem.DefinitionAsync(usagePath, usageLine, usageCol, null, 120000);
        return d is not null ? "ok" : $"FAILED: {r}";
    });

    var proc = Process.GetCurrentProcess();
    proc.Refresh();
    Console.WriteLine();
    Console.WriteLine($"  total scenario time : {swTotal.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"  managed heap        : {GC.GetTotalMemory(forceFullCollection: true) / (1024.0 * 1024):F0} MB");
    Console.WriteLine($"  process working set : {proc.WorkingSet64 / (1024.0 * 1024):F0} MB");
    return 0;
}

// ---------------------------------------------------------------- build
if (rebuild || !File.Exists(dbPath))
{
    Console.WriteLine($"=== Cold index build: {workspace}");
    var result = IndexBuilder.Build(workspace, dbPath, msg => Console.WriteLine($"  {msg}"));
    Console.WriteLine();
    Console.WriteLine($"  projects          : {result.Projects} ({result.UnresolvedProjectRefs} unresolved refs)");
    Console.WriteLine($"  solutions         : {result.Solutions}");
    Console.WriteLine($"  cs files          : {result.CsFiles:N0} ({result.Lines:N0} lines)");
    Console.WriteLine($"  other files       : {result.OtherFiles:N0}");
    Console.WriteLine($"  symbols           : {result.Symbols:N0}");
    Console.WriteLine($"  scan/projects     : {result.ScanTime.TotalSeconds:F1}s / {result.ProjectTime.TotalSeconds:F1}s");
    Console.WriteLine($"  parse+persist     : {result.ParseTime.TotalSeconds:F1}s");
    Console.WriteLine($"  TOTAL             : {result.TotalTime.TotalSeconds:F1}s");
    Console.WriteLine($"  index size        : {result.DbBytes / (1024.0 * 1024.0):F0} MB");
    Console.WriteLine();
}

// ---------------------------------------------------------------- sample targets from the index
using var sampler = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
sampler.Open();

List<T> Sample<T>(string sql, Func<SqliteDataReader, T> map)
{
    using var cmd = sampler.CreateCommand();
    cmd.CommandText = sql;
    var list = new List<T>();
    using var r = cmd.ExecuteReader();
    while (r.Read()) list.Add(map(r));
    return list;
}

// Deterministic sampling: hash-ordered instead of RANDOM() so runs are comparable.
var classNames = Sample(
    "SELECT name FROM symbols WHERE kind='class' AND name NOT LIKE '%Attribute' GROUP BY name HAVING COUNT(*)=1 ORDER BY (MIN(id)*2654435761)%100000 LIMIT 12",
    r => r.GetString(0));
var methodNames = Sample(
    "SELECT name FROM symbols WHERE kind='method' GROUP BY name HAVING COUNT(*)<=3 ORDER BY (MIN(id)*2654435761)%100000 LIMIT 8",
    r => r.GetString(0));
var filePaths = Sample(
    "SELECT path FROM files WHERE lang='cs' ORDER BY (id*2654435761)%100000 LIMIT 10",
    r => r.GetString(0));
var largeFiles = Sample(
    "SELECT path, line_count FROM files WHERE lang='cs' ORDER BY line_count DESC LIMIT 3",
    r => (Path: r.GetString(0), Lines: r.GetInt32(1)));
var midMethodSpots = Sample(
    """
    SELECT f.path, (s.start_line + s.end_line) / 2 FROM symbols s
    JOIN files f ON f.id = s.file_id
    WHERE s.kind='method' AND s.end_line - s.start_line >= 3
    ORDER BY (s.id*2654435761)%100000 LIMIT 10
    """,
    r => (Path: r.GetString(0), Line: r.GetInt32(1)));
var projectNames = Sample(
    "SELECT name FROM projects WHERE is_test=0 ORDER BY (id*2654435761)%100000 LIMIT 8",
    r => r.GetString(0));

Console.WriteLine($"Sampled: {classNames.Count} classes, {methodNames.Count} methods, {filePaths.Count} files, {projectNames.Count} projects");
Console.WriteLine($"Largest file: {largeFiles[0].Path} ({largeFiles[0].Lines} lines)");
Console.WriteLine();

// ---------------------------------------------------------------- benchmark harness
var results = new List<(string Name, double P50, double P95, double Max, double Target)>();

void Bench(string name, double targetMs, Action action)
{
    for (int i = 0; i < 3; i++) action(); // warmup

    var samples = new double[iters];
    for (int i = 0; i < iters; i++)
    {
        var sw = Stopwatch.StartNew();
        action();
        samples[i] = sw.Elapsed.TotalMilliseconds;
    }
    Array.Sort(samples);
    double p50 = samples[iters / 2];
    double p95 = samples[Math.Min(iters - 1, (int)(iters * 0.95))];
    results.Add((name, p50, p95, samples[^1], targetMs));
    Console.WriteLine($"  {name,-46} p50 {p50,8:F1}ms  p95 {p95,8:F1}ms  max {samples[^1],8:F1}ms");
}

using var q = new IndexQueries(dbPath);
int cursor = 0;
T Next<T>(List<T> list) => list[cursor++ % list.Count];

Console.WriteLine($"=== Warm query benchmarks ({iters} iterations each)");

Bench("repo_overview", 100, () => q.Overview());

Bench("find_file (exact name)", 100, () => q.FindFiles(Path.GetFileName(Next(filePaths)), 20));
Bench("find_file (glob *Controller.cs)", 100, () => q.FindFiles("*Controller.cs", 50));
Bench("find_file (glob src/Billing/**/*.csproj)", 100, () => q.FindFiles("src/Billing/*.csproj", 50));

Bench("search_text (hot: Guard.NotNull)", 250, () => q.SearchText("Guard.NotNull", 50));
Bench("search_text (medium: AcmeException)", 250, () => q.SearchText("AcmeException", 50));
Bench("search_text (rare: sampled method)", 250, () => q.SearchText(Next(methodNames), 50));
Bench("search_text (config key)", 250, () => q.SearchText("ConnectionStringName", 50));

Bench("search_symbol (exact, sampled class)", 250, () => q.SearchSymbols(Next(classNames), "exact", null, 20));
Bench("search_symbol (hot name: Result)", 250, () => q.SearchSymbols("Result", "exact", null, 20));
Bench("search_symbol (prefix Invoice)", 250, () => q.SearchSymbols("Invoice", "prefix", new[] { "class", "interface" }, 25));
Bench("search_symbol (substring Calculator)", 250, () => q.SearchSymbols("Calculator", "substring", null, 25));

Bench("outline (largest file)", 100, () => q.Outline(largeFiles[0].Path));
Bench("outline (sampled files)", 100, () => q.Outline(Next(filePaths)));

Bench("symbol_at (sampled positions)", 250, () =>
{
    var (path, line) = Next(midMethodSpots);
    q.SymbolAt(path, line);
});

Bench("definition (sampled class, kinds=type)", 250,
    () => q.SearchSymbols(Next(classNames), "exact", new[] { "class", "interface", "struct", "enum" }, 10));

Bench("references (hot: Guard)", 1000, () => q.ReferenceCandidates("Guard", 500, 3));
Bench("references (medium: sampled class)", 1000, () => q.ReferenceCandidates(Next(classNames), 500, 3));
Bench("references (rare: sampled method)", 1000, () => q.ReferenceCandidates(Next(methodNames), 500, 3));

Bench("project_graph (hot: Common upstream d2)", 250, () => q.ProjectGraph("Acme.Platform.Common", 2, "upstream"));
Bench("project_graph (sampled downstream d2)", 250, () => q.ProjectGraph(Next(projectNames), 2, "downstream"));

Bench("projects_containing (sampled)", 100, () => q.ProjectsContaining(Next(filePaths)));

// ---------------------------------------------------------------- report
Console.WriteLine();
Console.WriteLine("=== Results vs brief targets");
Console.WriteLine($"  {"benchmark",-46} {"p95",10} {"target",10}   verdict");
int failures = 0;
foreach (var r in results)
{
    bool pass = r.P95 <= r.Target;
    if (!pass) failures++;
    Console.WriteLine($"  {r.Name,-46} {r.P95,9:F1}ms {r.Target,9:F0}ms   {(pass ? "PASS" : "FAIL")}");
}
Console.WriteLine();
Console.WriteLine(failures == 0
    ? "ALL TARGETS MET."
    : $"{failures} benchmark(s) exceeded target.");
return failures == 0 ? 0 : 1;
