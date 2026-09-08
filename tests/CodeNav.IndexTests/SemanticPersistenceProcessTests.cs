using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

public sealed class SemanticPersistenceProcessTests : IDisposable
{
    private readonly ProcessHeavyTestIsolation _processHeavyTestIsolation =
        ProcessHeavyTestIsolation.Acquire();

    public void Dispose() => _processHeavyTestIsolation.Dispose();

    [Fact]
    public async Task PersistencePreservesCrossProjectReferencesImplementationsAndHierarchyAfterRestart()
    {
        string root = Directory.CreateTempSubdirectory("codenav-persistent-graph").FullName;
        try
        {
            string contracts = Path.Combine(root, "Contracts");
            string consumer = Path.Combine(root, "Consumer");
            Directory.CreateDirectory(contracts);
            Directory.CreateDirectory(consumer);
            File.WriteAllText(Path.Combine(contracts, "Contracts.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(consumer, "Consumer.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
                "<ItemGroup><ProjectReference Include=\"../Contracts/Contracts.csproj\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(contracts, "Contract.cs"), SemanticPersistenceCanary.GraphContractSource);
            File.WriteAllText(Path.Combine(contracts, "Base.cs"), SemanticPersistenceCanary.GraphBaseSource);
            File.WriteAllText(Path.Combine(consumer, "Derived.cs"), SemanticPersistenceCanary.GraphDerivedSource);
            File.WriteAllText(Path.Combine(consumer, "Use.cs"), SemanticPersistenceCanary.GraphUseSource);
            IndexBuilder.Build(root, IndexBuilder.DefaultDbPath(root));

            // Run disabled first so its no-cache assertion is decisive; subsequent enabled
            // processes reuse exactly the same source, project graph and identity root.
            var results = new List<SemanticPersistenceCanary.GraphResult>();
            foreach ((bool enabled, bool hit) in new[] { (false, false), (true, false), (true, true) })
            {
                results.Add(await RunCanaryAsync<SemanticPersistenceCanary.GraphResult>(
                    ["--roslyn-persistence-graph-canary", root, enabled.ToString(), hit.ToString()]));
            }
            Assert.Equal(results[0].ReferenceSites, results[1].ReferenceSites);
            Assert.Equal(results[0].ReferenceSites, results[2].ReferenceSites);
            Assert.Equal(results[0].Implementations, results[2].Implementations);
            Assert.Equal(results[0].DerivedTypes, results[2].DerivedTypes);
            Assert.Equal(results[0].Overrides, results[2].Overrides);
            Assert.Single(results.Select(result => result.SolutionId).Distinct());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task PersistenceWritesReusesAndInvalidatesInSeparateProcessesWithSemanticParity()
    {
        string enabledRoot = Directory.CreateTempSubdirectory("codenav-persistent").FullName;
        string disabledRoot = Directory.CreateTempSubdirectory("codenav-memory-only").FullName;
        try
        {
            const string conditionalUse = "#if INCLUDE_REFERENCE\nclass Use { N.ITarget value; }\n#endif";
            Prepare(enabledRoot, conditionalUse);
            Prepare(disabledRoot, conditionalUse);
            var children = new List<SemanticPersistenceCanary.Result>();
            foreach ((bool include, bool hit, int count) in new[]
                     { (false, false, 0), (false, true, 0), (true, false, 1), (true, true, 1) })
            {
                children.Add(await RunChildAsync(enabledRoot, true, include, hit, count));
            }
            SemanticPersistenceCanary.Result off = await RunChildAsync(disabledRoot, false, false, false, 0);
            SemanticPersistenceCanary.Result on = await RunChildAsync(disabledRoot, false, true, false, 1);
            Assert.Equal(children[0].References, off.References);
            Assert.Equal(children[2].References, on.References);

            Prepare(enabledRoot, "class Noise { }");
            Prepare(disabledRoot, "class Noise { }");
            children.Add(await RunChildAsync(enabledRoot, true, true, false, 0));
            children.Add(await RunChildAsync(enabledRoot, true, true, true, 0));
            SemanticPersistenceCanary.Result changed = await RunChildAsync(disabledRoot, false, true, false, 0);
            Assert.Equal(children[^1].References, changed.References);

            Assert.Single(children.Select(result => result.DatabaseFile).Distinct());
            Assert.Single(children.Select(result => result.SolutionId).Distinct());
            Assert.Single(children.Select(result => result.ProjectId).Distinct());
            Assert.Single(children.Select(result => result.DocumentId).Distinct());
            Assert.All(children.Concat([off, on, changed]), result => Assert.NotEqual(Environment.ProcessId, result.ProcessId));
        }
        finally
        {
            // Every child has exited before these directories (including Roslyn's owned cache)
            // are removed. Do not substitute workspace.Dispose for the process lifetime boundary.
            TestWorkspaceCleanup.DeleteWorkspace(enabledRoot);
            TestWorkspaceCleanup.DeleteWorkspace(disabledRoot);
        }
    }

    private static void Prepare(string root, string useText)
    {
        string project = Path.Combine(root, "P");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "P.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(project, "Target.cs"), "namespace N; public interface ITarget { }");
        File.WriteAllText(Path.Combine(project, "Use.cs"), useText);
        IndexBuilder.Build(root, IndexBuilder.DefaultDbPath(root));
    }

    private static Task<SemanticPersistenceCanary.Result> RunChildAsync(string root,
        bool enabled, bool includeReference, bool expectedHit, int expectedReferences)
        => RunCanaryAsync<SemanticPersistenceCanary.Result>(
            ["--roslyn-persistence-canary", root, enabled.ToString(), includeReference.ToString(),
                expectedHit.ToString(), expectedReferences.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

    private static async Task<T> RunCanaryAsync<T>(string[] arguments)
    {
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.GetFullPath(
            Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        Assert.True(File.Exists(host), $"The current runtime's dotnet host is missing: {host}");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add(typeof(SemanticPersistenceCanary).Assembly.Location);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        RedirectedProcessResult captured = await CaptureCanaryAsync(process,
            TimeSpan.FromSeconds(60), string.Join(" ", arguments));
        Assert.True(captured.ExitCode == 0,
            $"Persistence canary exited {captured.ExitCode}. {string.Join(" ", arguments)}\n{captured.Output}\n{captured.Error}");
        T? result = JsonSerializer.Deserialize<T>(captured.Output);
        Assert.NotNull(result);
        using JsonDocument payload = JsonDocument.Parse(captured.Output);
        Assert.Equal(process.Id, payload.RootElement.GetProperty("ProcessId").GetInt32());
        return result;
    }

    private static async Task<RedirectedProcessResult> CaptureCanaryAsync(
        Process process, TimeSpan timeout, string phase)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();
        Task<string> stdout = CaptureAsync(process.StandardOutput, output);
        Task<string> stderr = CaptureAsync(process.StandardError, error);
        try
        {
            return await TestProcessLifecycle.WaitForExitAndDrainAsync(
                process, stdout, stderr, timeout, $"Persistence canary: {phase}");
        }
        catch (TimeoutException exception)
        {
            // The shared lifecycle helper owns bounded tree termination and pipe cleanup.
            // Keep bytes captured before a pipe faults/closes, including a final partial line.
            throw new TimeoutException(
                $"{exception.Message}\nPartial stdout:\n{Snapshot(output)}\nPartial stderr:\n{Snapshot(error)}",
                exception);
        }
    }

    private static async Task<string> CaptureAsync(StreamReader reader, StringBuilder captured)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) != 0)
        {
            lock (captured) captured.Append(buffer, 0, read);
        }
        return Snapshot(captured);
    }

    private static string Snapshot(StringBuilder captured)
    {
        lock (captured) return captured.ToString();
    }
}
