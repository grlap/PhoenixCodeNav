using System.Text.Json;
using CodeNav.Core.Diagnostics;
using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

public class Batch66PortalLiveStatusTelemetryTests
{
    [Fact]
    public void FullBuildEmitsOneServerIdentityAndOrderedJsonlProgress()
    {
        string root = Directory.CreateTempSubdirectory("codenav-66-live-build").FullName;
        try
        {
            WriteProject(root);
            using var manager = new IndexManager(root);
            manager.Start();
            manager.EmitServerInfo(new TelemetryServerInfo(
                "9.8.7",
                "9.8.7+abc123",
                IndexBuilder.SchemaVersion,
                ["feature-a", "feature-b"]));
            manager.EmitServerInfo(new TelemetryServerInfo(
                "ignored",
                "ignored",
                "ignored",
                ["ignored"]));

            Assert.True(
                WaitUntil(() => manager.IsQueryable, 30_000),
                "index never became queryable");
            JsonElement[] records = manager.Telemetry.Snapshot()
                .Select(Parse)
                .ToArray();

            JsonElement server = records.Single(Is("serverInfo"));
            Assert.Equal("9.8.7", server.GetProperty("version").GetString());
            Assert.Equal("9.8.7+abc123", server.GetProperty("buildStamp").GetString());
            Assert.Equal(IndexBuilder.SchemaVersion, server.GetProperty("schemaVersion").GetString());
            Assert.Equal(2, server.GetProperty("featureCount").GetInt32());
            Assert.Equal(2, server.GetProperty("featureIds").GetArrayLength());
            Assert.False(string.IsNullOrWhiteSpace(server.GetProperty("platform").GetString()));

            JsonElement[] progress = records.Where(Is("buildProgress")).ToArray();
            Assert.NotEmpty(progress);
            string buildId = progress[0].GetProperty("buildId").GetString()!;
            Assert.Equal("started", progress[0].GetProperty("state").GetString());
            Assert.Equal(
                ["scanning", "parsing_projects", "indexing_files", "finalizing"],
                progress
                    .Select(item => item.GetProperty("phase").GetString()!)
                    .Distinct()
                    .ToArray());
            JsonElement terminal = progress[^1];
            Assert.Equal(buildId, terminal.GetProperty("buildId").GetString());
            Assert.Equal("completed", terminal.GetProperty("state").GetString());
            Assert.True(terminal.GetProperty("filesDone").GetInt64() >= 2);
            Assert.True(terminal.GetProperty("symbolsWritten").GetInt64() >= 2);
            Assert.True(terminal.GetProperty("bytesRead").GetInt64() > 0);
            Assert.True(
                progress.Zip(progress.Skip(1))
                    .All(pair =>
                        pair.First.GetProperty("filesDone").GetInt64()
                        <= pair.Second.GetProperty("filesDone").GetInt64()));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void CancelledBuildEndsWithOneCancelledTerminalRecord()
    {
        string root = Directory.CreateTempSubdirectory("codenav-66-cancel").FullName;
        try
        {
            WriteProject(root);
            using var manager = new IndexManager(root)
            {
                FullRebuildAfterTelemetryStartedForTest =
                    () => throw new OperationCanceledException("test cancellation")
            };
            manager.Start();

            Assert.True(
                WaitUntil(
                    () => manager.Telemetry.Snapshot()
                        .Any(line => line.Contains(
                            "\"state\":\"cancelled\"",
                            StringComparison.Ordinal)),
                    15_000),
                "cancelled terminal telemetry never arrived");
            JsonElement[] progress = manager.Telemetry.Snapshot()
                .Select(Parse)
                .Where(Is("buildProgress"))
                .ToArray();
            Assert.Equal("started", progress[0].GetProperty("state").GetString());
            Assert.Equal("cancelled", progress[^1].GetProperty("state").GetString());
            _ = progress.Single(
                item => item.GetProperty("state").GetString() == "cancelled");
            Assert.DoesNotContain(progress, item =>
                item.GetProperty("state").GetString() == "failed");
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    private static Func<JsonElement, bool> Is(string eventName) =>
        item => item.TryGetProperty("e", out JsonElement value)
            && value.GetString() == eventName;

    private static JsonElement Parse(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    private static void WriteProject(string root)
    {
        File.WriteAllText(
            Path.Combine(root, "P.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(root, "A.cs"),
            "namespace P { public sealed class A { public void M() { } } }");
        File.WriteAllText(
            Path.Combine(root, "B.cs"),
            "namespace P { public sealed class B : A { } }");
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
                return true;
            Thread.Sleep(25);
        }
        return condition();
    }
}
