using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodeNav.Portal;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit.Sdk;

namespace CodeNav.Tests;

/// <summary>
/// Pins the portal's live MVP integration boundary: workspace files are opened through anchored
/// regular-file handles, semantic JSONL is normalized incrementally, and every bounded-data gap
/// stays visible instead of producing complete-looking dashboard state.
/// </summary>
public class PortalDataSourceTests
{
    [Fact]
    public void AnchoredIndexPresenceAndSemanticJsonlBecomeALiveReadOnlyView()
    {
        string root = Directory.CreateTempSubdirectory("codenav-portal-live").FullName;
        try
        {
            string telemetryDirectory = Path.Combine(root, ".codenav", "telemetry");
            Directory.CreateDirectory(telemetryDirectory);
            string database = Path.Combine(root, ".codenav", "index.db");
            CreateIndex(database);
            File.SetAttributes(database, File.GetAttributes(database) | FileAttributes.ReadOnly);

            string telemetry = CurrentProcessTelemetryPath(telemetryDirectory);
            using var telemetryStream = new FileStream(
                telemetry,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            using var telemetryWriter = new StreamWriter(telemetryStream);
            telemetryWriter.Write(
                ServerInfo()
                + BuildProgress("build-portal", "running", "indexing_files", 9, 12)
                + SemanticOperation("portal-first", "references", 41));
            telemetryWriter.Flush();

            var source = new PortalDataSource([root]);
            source.RefreshForTest();

            using (JsonDocument bootstrap = Serialize(source.Bootstrap()))
            {
                JsonElement live = bootstrap.RootElement;
                Assert.Equal("live", live.GetProperty("dataSource").GetString());
                Assert.True(live.GetProperty("dataComplete").GetBoolean());
                Assert.Equal(1, live.GetProperty("summary").GetProperty("workspaceCount").GetInt32());
                JsonElement workspace = live.GetProperty("workspaces")[0];
                Assert.Equal("queryable", workspace.GetProperty("state").GetString());
                Assert.Equal(1, workspace.GetProperty("recentOperationCount").GetInt32());
                Assert.False(
                    workspace.GetProperty("recentOperationCountIsLowerBound").GetBoolean());
                JsonElement index = live.GetProperty("indexes")[0];
                Assert.Equal("queryable", index.GetProperty("state").GetString());
                Assert.Equal("unknown", index.GetProperty("freshness").GetString());
                Assert.Equal(JsonValueKind.Null, index.GetProperty("schemaVersion").ValueKind);
                Assert.True(index.GetProperty("databaseSizeBytes").GetInt64() > 0);
                Assert.Equal(
                    JsonValueKind.Null,
                    index.GetProperty("counts").GetProperty("files").ValueKind);
                Assert.Equal(
                    JsonValueKind.Null,
                    index.GetProperty("counts").GetProperty("projects").ValueKind);
                Assert.Equal(
                    JsonValueKind.Null,
                    index.GetProperty("counts").GetProperty("symbols").ValueKind);
                JsonElement build = index.GetProperty("currentBuild");
                Assert.Equal("build-portal", build.GetProperty("buildId").GetString());
                Assert.Equal("indexing_files", build.GetProperty("phase").GetString());
                Assert.Equal(9, build.GetProperty("filesProcessed").GetInt64());
                Assert.Equal(321, build.GetProperty("symbolsWritten").GetInt64());
                JsonElement instance = live.GetProperty("instances")[0];
                Assert.Equal("0.12.26", instance.GetProperty("version").GetString());
                Assert.Equal("18", instance.GetProperty("schemaVersion").GetString());
                Assert.Contains(
                    instance.GetProperty("featureIds").EnumerateArray(),
                    value => value.GetString() == "operations-portal-live-build-status");
            }

            using (JsonDocument operations = Serialize(source.Operations()))
            {
                JsonElement page = operations.RootElement;
                Assert.Equal(1, page.GetProperty("returned").GetInt32());
                JsonElement item = page.GetProperty("items")[0];
                Assert.Equal("portal-first", item.GetProperty("correlationId").GetString());
                Assert.Equal("references", item.GetProperty("tool").GetString());
                Assert.Equal(41, item.GetProperty("durationMs").GetInt64());
                Assert.Equal("completed", item.GetProperty("outcome").GetString());
                Assert.Equal("exact", item.GetProperty("confidence").GetString());
                Assert.False(item.GetProperty("partial").GetBoolean());
                Assert.Equal("unknown", item.GetProperty("coldState").GetString());
                Assert.True(item.GetProperty("timings").TryGetProperty("topologyMs", out _));
                Assert.False(item.GetProperty("timings").TryGetProperty("topoMs", out _));
                Assert.Equal(4, item.GetProperty("counts").GetProperty("loaded").GetInt64());
            }

            telemetryWriter.Write(
                SemanticOperation("portal-second", "implementations", 73)
                + BuildProgress("build-portal", "completed", "finalizing", 12, 12)
                + "{\"e\":\"telemetry_dropped\",\"count\":3}\n"
                + "{malformed-json}\n"
                + "{\"e\":\"telemetry_truncated\",\"capBytes\":16777216}\n");
            telemetryWriter.Flush();
            source.RefreshForTest();

            using (JsonDocument bootstrap = Serialize(source.Bootstrap()))
            {
                JsonElement live = bootstrap.RootElement;
                Assert.False(live.GetProperty("dataComplete").GetBoolean());
                JsonElement evidence = live.GetProperty("telemetry");
                Assert.Equal(3, evidence.GetProperty("droppedRecords").GetInt64());
                Assert.Equal(1, evidence.GetProperty("truncatedFiles").GetInt32());
                Assert.Equal(1, evidence.GetProperty("invalidRecords").GetInt32());
                JsonElement workspace = live.GetProperty("workspaces")[0];
                Assert.Equal("queryable", workspace.GetProperty("state").GetString());
                Assert.Equal(2, workspace.GetProperty("recentOperationCount").GetInt32());
                Assert.True(
                    workspace.GetProperty("recentOperationCountIsLowerBound").GetBoolean());
                Assert.Equal(
                    JsonValueKind.Null,
                    live.GetProperty("indexes")[0].GetProperty("currentBuild").ValueKind);
            }
            using (JsonDocument operations = Serialize(source.Operations()))
                Assert.Equal(2, operations.RootElement.GetProperty("returned").GetInt32());

            Assert.Equal("sentinel-unchanged", File.ReadAllText(database));
        }
        finally
        {
            string database = Path.Combine(root, ".codenav", "index.db");
            if (File.Exists(database))
                File.SetAttributes(database, FileAttributes.Normal);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void IndexWithoutTelemetryRemainsAnExplicitLowerBound()
    {
        string root = Directory.CreateTempSubdirectory("codenav-portal-index-only").FullName;
        try
        {
            CreateIndex(Path.Combine(root, ".codenav", "index.db"));
            var source = new PortalDataSource([root]);
            source.RefreshForTest();

            using (JsonDocument bootstrap = Serialize(source.Bootstrap()))
            {
                JsonElement live = bootstrap.RootElement;
                Assert.Equal("live", live.GetProperty("dataSource").GetString());
                Assert.False(live.GetProperty("dataComplete").GetBoolean());
                Assert.Equal(
                    "unknown",
                    live.GetProperty("indexes")[0].GetProperty("state").GetString());
                Assert.Equal(
                    0,
                    live.GetProperty("workspaces")[0]
                        .GetProperty("recentOperationCount")
                        .GetInt32());
                Assert.Equal(
                    0,
                    live.GetProperty("telemetry").GetProperty("sourceFiles").GetInt32());
            }
            using JsonDocument operations = Serialize(source.Operations());
            Assert.Equal(0, operations.RootElement.GetProperty("total").GetInt32());
            Assert.True(operations.RootElement.GetProperty("totalIsLowerBound").GetBoolean());
            Assert.False(operations.RootElement.GetProperty("dataComplete").GetBoolean());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void FailedOperationDoesNotPromoteAnObservedIndexToQueryable()
    {
        string root = Directory.CreateTempSubdirectory("codenav-portal-failed-query").FullName;
        try
        {
            string telemetryDirectory = Path.Combine(root, ".codenav", "telemetry");
            Directory.CreateDirectory(telemetryDirectory);
            CreateIndex(Path.Combine(root, ".codenav", "index.db"));
            File.WriteAllText(
                CurrentProcessTelemetryPath(telemetryDirectory),
                ServerInfo()
                + SemanticOperation(
                    "portal-failed",
                    "references",
                    41,
                    result: "unresolved",
                    reason: "symbol_not_found"));

            var source = new PortalDataSource([root]);
            source.RefreshForTest();

            using JsonDocument bootstrap = Serialize(source.Bootstrap());
            JsonElement live = bootstrap.RootElement;
            Assert.Equal(
                "unknown",
                live.GetProperty("indexes")[0].GetProperty("state").GetString());
            Assert.Equal(
                1,
                live.GetProperty("workspaces")[0]
                    .GetProperty("recentOperationCount")
                    .GetInt32());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void IndexGenerationChangeInvalidatesRetainedQueryEvidence()
    {
        string root = Directory.CreateTempSubdirectory("codenav-portal-index-generation").FullName;
        try
        {
            string telemetryDirectory = Path.Combine(root, ".codenav", "telemetry");
            Directory.CreateDirectory(telemetryDirectory);
            string database = Path.Combine(root, ".codenav", "index.db");
            CreateIndex(database);
            string telemetry = CurrentProcessTelemetryPath(telemetryDirectory);
            File.WriteAllText(
                telemetry,
                ServerInfo()
                + SemanticOperation("before-replacement", "references", 41));

            var source = new PortalDataSource([root]);
            source.RefreshForTest();
            using (JsonDocument before = Serialize(source.Bootstrap()))
            {
                Assert.Equal(
                    "queryable",
                    before.RootElement.GetProperty("indexes")[0]
                        .GetProperty("state")
                        .GetString());
            }

            string replacement = Path.Combine(root, ".codenav", "replacement.db");
            File.WriteAllText(replacement, "replacement-generation");
            File.Move(replacement, database, overwrite: true);
            source.RefreshForTest(forceIndexProbe: true);
            using (JsonDocument replaced = Serialize(source.Bootstrap()))
            {
                JsonElement live = replaced.RootElement;
                Assert.Equal(
                    "unknown",
                    live.GetProperty("indexes")[0].GetProperty("state").GetString());
                Assert.Equal(
                    1,
                    live.GetProperty("workspaces")[0]
                        .GetProperty("recentOperationCount")
                        .GetInt32());
            }

            File.AppendAllText(
                telemetry,
                SemanticOperation("after-replacement", "implementations", 73));
            source.RefreshForTest();
            using JsonDocument after = Serialize(source.Bootstrap());
            Assert.Equal(
                "queryable",
                after.RootElement.GetProperty("indexes")[0]
                    .GetProperty("state")
                    .GetString());
            Assert.Equal(
                2,
                after.RootElement.GetProperty("workspaces")[0]
                    .GetProperty("recentOperationCount")
                    .GetInt32());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void CompletedOperationFromAStaleInstanceDoesNotPromoteTheIndex()
    {
        string root = Directory.CreateTempSubdirectory("codenav-portal-stale-query").FullName;
        try
        {
            string telemetryDirectory = Path.Combine(root, ".codenav", "telemetry");
            Directory.CreateDirectory(telemetryDirectory);
            CreateIndex(Path.Combine(root, ".codenav", "index.db"));
            File.WriteAllText(
                Path.Combine(
                    telemetryDirectory,
                    $"phoenix-{Environment.ProcessId}-20260724080000-1.jsonl"),
                ServerInfo()
                + SemanticOperation("stale-completed", "references", 41));

            var source = new PortalDataSource([root]);
            source.RefreshForTest();

            using JsonDocument bootstrap = Serialize(source.Bootstrap());
            JsonElement live = bootstrap.RootElement;
            Assert.Equal(
                "stale",
                live.GetProperty("instances")[0]
                    .GetProperty("connectionState")
                    .GetString());
            Assert.Equal(
                "unknown",
                live.GetProperty("indexes")[0].GetProperty("state").GetString());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void ReparsePointedWorkspaceDataIsNeverRead()
    {
        string root = Directory.CreateTempSubdirectory("codenav-portal-link-root").FullName;
        string outside = Directory.CreateTempSubdirectory("codenav-portal-link-out").FullName;
        try
        {
            string outsideCodeNav = Path.Combine(outside, ".codenav");
            string outsideTelemetry = Path.Combine(outsideCodeNav, "telemetry");
            Directory.CreateDirectory(outsideTelemetry);
            CreateIndex(Path.Combine(outsideCodeNav, "index.db"));
            File.WriteAllText(
                Path.Combine(
                    outsideTelemetry,
                    $"phoenix-{Environment.ProcessId}-20260724080000-1.jsonl"),
                SemanticOperation("outside-secret", "references", 10));

            if (!TryCreateDirectoryLink(Path.Combine(root, ".codenav"), outsideCodeNav))
                throw SkipException.ForSkip("The host cannot create a directory link or junction.");

            var source = new PortalDataSource([root]);
            source.RefreshForTest();

            using JsonDocument bootstrap = Serialize(source.Bootstrap());
            Assert.Equal("live", bootstrap.RootElement.GetProperty("dataSource").GetString());
            Assert.False(bootstrap.RootElement.GetProperty("dataComplete").GetBoolean());
            using JsonDocument operations = Serialize(source.Operations());
            Assert.DoesNotContain(
                operations.RootElement.GetProperty("items").EnumerateArray(),
                item => item.TryGetProperty("correlationId", out JsonElement correlation)
                    && correlation.GetString() == "outside-secret");
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
            TestWorkspaceCleanup.DeleteWorkspace(outside);
        }
    }

    [Fact]
    public void SemanticShapesFiltersAndCursorsRemainContractHonest()
    {
        string root = Directory.CreateTempSubdirectory("codenav-portal-query").FullName;
        try
        {
            string telemetryDirectory = Path.Combine(root, ".codenav", "telemetry");
            Directory.CreateDirectory(telemetryDirectory);
            string telemetry = Path.Combine(
                telemetryDirectory,
                $"phoenix-{Environment.ProcessId}-20260724080000-1.jsonl");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            File.WriteAllText(
                telemetry,
                SemanticOperation("exact", "references", 10, timestamp: now.AddSeconds(-1))
                + SemanticOperation(
                    "partial",
                    "callers",
                    20,
                    result: "partial",
                    reason: "semantic_timeout",
                    timestamp: now.AddSeconds(-2))
                + SemanticOperation(
                    "degraded",
                    "implementations",
                    30,
                    result: "degraded",
                    reason: "cluster_cold_load",
                    cold: true,
                    timestamp: now.AddSeconds(-3))
                + SemanticOperation(
                    "unresolved",
                    "definition",
                    40,
                    result: "unresolved",
                    reason: "not_found",
                    timestamp: now.AddSeconds(-4)));

            var source = new PortalDataSource([root]);
            source.RefreshForTest();

            using (JsonDocument operations = Serialize(source.Operations()))
            {
                Dictionary<string, JsonElement> items = operations.RootElement
                    .GetProperty("items")
                    .EnumerateArray()
                    .ToDictionary(
                        item => item.GetProperty("correlationId").GetString()!,
                        item => item.Clone(),
                        StringComparer.Ordinal);
                Assert.Equal("completed", items["partial"].GetProperty("outcome").GetString());
                Assert.Equal("unknown", items["partial"].GetProperty("confidence").GetString());
                Assert.True(items["partial"].GetProperty("partial").GetBoolean());
                Assert.Equal("unknown", items["partial"].GetProperty("coldState").GetString());
                Assert.Equal("degraded", items["degraded"].GetProperty("outcome").GetString());
                Assert.Equal("unknown", items["degraded"].GetProperty("confidence").GetString());
                Assert.True(items["degraded"].GetProperty("partial").GetBoolean());
                Assert.Equal("cold", items["degraded"].GetProperty("coldState").GetString());
                Assert.Equal("failed", items["unresolved"].GetProperty("outcome").GetString());
                Assert.Equal("unknown", items["unresolved"].GetProperty("confidence").GetString());
            }

            IQueryCollection firstQuery = Query(
                ("outcome", "completed"),
                ("limit", "1"));
            Assert.True(PortalOperationQuery.TryParse(
                firstQuery,
                out PortalOperationQuery first,
                out string? firstError),
                firstError);
            using JsonDocument firstPage = Serialize(source.Operations(first));
            Assert.Equal(2, firstPage.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(1, firstPage.RootElement.GetProperty("returned").GetInt32());
            string cursor = firstPage.RootElement.GetProperty("nextCursor").GetString()!;

            Assert.True(PortalOperationQuery.TryParse(
                Query(("outcome", "completed"), ("limit", "1"), ("cursor", cursor)),
                out PortalOperationQuery second,
                out string? secondError),
                secondError);
            using JsonDocument secondPage = Serialize(source.Operations(second));
            Assert.Equal(1, secondPage.RootElement.GetProperty("returned").GetInt32());
            Assert.NotEqual(
                firstPage.RootElement.GetProperty("items")[0].GetProperty("operationId").GetString(),
                secondPage.RootElement.GetProperty("items")[0].GetProperty("operationId").GetString());

            Assert.True(PortalOperationQuery.TryParse(
                Query(("outcome", "failed"), ("limit", "1"), ("cursor", cursor)),
                out PortalOperationQuery mismatchedFilter,
                out string? mismatchError),
                mismatchError);
            Assert.Throws<PortalCursorExpiredException>(
                () => source.Operations(mismatchedFilter));

            var nextSession = new PortalDataSource([root]);
            nextSession.RefreshForTest();
            Assert.True(PortalOperationQuery.TryParse(
                Query(("outcome", "completed"), ("limit", "1"), ("cursor", cursor)),
                out PortalOperationQuery foreignSession,
                out string? foreignError),
                foreignError);
            Assert.Throws<PortalCursorExpiredException>(
                () => nextSession.Operations(foreignSession));

            Assert.False(PortalOperationQuery.TryParse(
                Query(("limit", "501")),
                out _,
                out _));
            Assert.False(PortalOperationQuery.TryParse(
                Query(("cursor", "not-a-cursor")),
                out _,
                out _));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void ReadFailuresStayPartialUntilTheFileIsReadableAgain()
    {
        string root = Directory.CreateTempSubdirectory("codenav-portal-read-gap").FullName;
        try
        {
            string telemetryDirectory = Path.Combine(root, ".codenav", "telemetry");
            Directory.CreateDirectory(telemetryDirectory);
            string telemetry = Path.Combine(
                telemetryDirectory,
                $"phoenix-{Environment.ProcessId}-20260724080000-1.jsonl");
            File.WriteAllText(telemetry, SemanticOperation("first", "references", 10));
            var source = new PortalDataSource([root]);
            source.RefreshForTest();

            if (OperatingSystem.IsWindows())
            {
                using var locked = new FileStream(
                    telemetry,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
                locked.Position = locked.Length;
                byte[] appended = Encoding.UTF8.GetBytes(
                    SemanticOperation("second", "references", 11));
                locked.Write(appended);
                locked.Flush();
                source.RefreshForTest();
                AssertReadError(source, expected: 1);
            }
            else
            {
                File.AppendAllText(telemetry, SemanticOperation("second", "references", 11));
                UnixFileMode mode = File.GetUnixFileMode(telemetry);
                File.SetUnixFileMode(telemetry, UnixFileMode.None);
                try
                {
                    source.RefreshForTest();
                    AssertReadError(source, expected: 1);
                }
                finally
                {
                    File.SetUnixFileMode(telemetry, mode);
                }
            }

            source.RefreshForTest();
            AssertReadError(source, expected: 0);
            using JsonDocument operations = Serialize(source.Operations());
            Assert.Contains(
                operations.RootElement.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("correlationId").GetString() == "second");
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void RemovingTelemetrySourcesPurgesRecordsButKeepsLossEvidenceSticky()
    {
        string root = Directory.CreateTempSubdirectory("codenav-portal-remove").FullName;
        try
        {
            string telemetryDirectory = Path.Combine(root, ".codenav", "telemetry");
            Directory.CreateDirectory(telemetryDirectory);
            string removedTelemetry = Path.Combine(
                telemetryDirectory,
                $"phoenix-{Environment.ProcessId}-20260724080000-1.jsonl");
            string retainedTelemetry = Path.Combine(
                telemetryDirectory,
                $"phoenix-{Environment.ProcessId}-20260724080001-1.jsonl");
            File.WriteAllText(
                removedTelemetry,
                SemanticOperation("removed", "references", 10));
            File.WriteAllText(
                retainedTelemetry,
                SemanticOperation("retained", "implementations", 11));
            var source = new PortalDataSource([root]);
            source.RefreshForTest();
            using (JsonDocument complete = Serialize(source.Bootstrap()))
                Assert.True(complete.RootElement.GetProperty("dataComplete").GetBoolean());

            File.Delete(removedTelemetry);
            source.RefreshForTest();

            using (JsonDocument bootstrap = Serialize(source.Bootstrap()))
            {
                Assert.Equal("live", bootstrap.RootElement.GetProperty("dataSource").GetString());
                Assert.False(bootstrap.RootElement.GetProperty("dataComplete").GetBoolean());
                Assert.Equal(
                    0,
                    bootstrap.RootElement.GetProperty("telemetry")
                        .GetProperty("truncatedFiles").GetInt32());
                Assert.Equal(
                    1,
                    bootstrap.RootElement.GetProperty("telemetry")
                        .GetProperty("sourceFiles").GetInt32());
                Assert.True(
                    bootstrap.RootElement.GetProperty("telemetry")
                        .GetProperty("retentionEvictions").GetInt64() > 0);
                Assert.Equal(
                    JsonValueKind.String,
                    bootstrap.RootElement.GetProperty("telemetry")
                        .GetProperty("retainedFromUtc").ValueKind);
            }
            using (JsonDocument operations = Serialize(source.Operations()))
            {
                string[] correlations = operations.RootElement.GetProperty("items")
                    .EnumerateArray()
                    .Select(item => item.GetProperty("correlationId").GetString()!)
                    .ToArray();
                Assert.DoesNotContain("removed", correlations);
                Assert.Contains("retained", correlations);
                Assert.True(operations.RootElement.GetProperty("totalIsLowerBound").GetBoolean());
            }

            File.Delete(retainedTelemetry);
            Directory.Delete(telemetryDirectory);
            source.RefreshForTest();

            using JsonDocument soleSourceGone = Serialize(source.Bootstrap());
            Assert.Equal("live", soleSourceGone.RootElement.GetProperty("dataSource").GetString());
            Assert.False(soleSourceGone.RootElement.GetProperty("dataComplete").GetBoolean());
            Assert.Equal(
                0,
                soleSourceGone.RootElement.GetProperty("telemetry")
                    .GetProperty("sourceFiles").GetInt32());
            Assert.True(
                soleSourceGone.RootElement.GetProperty("telemetry")
                    .GetProperty("retentionEvictions").GetInt64() > 0);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void RetentionAndStringBudgetsAreVisibleAndBounded()
    {
        string root = Directory.CreateTempSubdirectory("codenav-portal-bounds").FullName;
        try
        {
            string telemetryDirectory = Path.Combine(root, ".codenav", "telemetry");
            Directory.CreateDirectory(telemetryDirectory);
            string telemetry = Path.Combine(
                telemetryDirectory,
                $"phoenix-{Environment.ProcessId}-20260724080000-1.jsonl");
            var text = new StringBuilder();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            for (int i = 0; i < 600; i++)
            {
                text.Append(SemanticOperation(
                    $"bounded-{i}",
                    "references",
                    10,
                    timestamp: now.AddMilliseconds(-i)));
            }
            File.WriteAllText(telemetry, text.ToString());

            var source = new PortalDataSource([root]);
            source.RefreshForTest();

            using (JsonDocument bootstrap = Serialize(source.Bootstrap()))
            {
                JsonElement live = bootstrap.RootElement;
                Assert.False(live.GetProperty("dataComplete").GetBoolean());
                Assert.True(
                    live.GetProperty("telemetry").GetProperty("retentionEvictions").GetInt64() > 0);
            }
            using (JsonDocument operations = Serialize(source.Operations()))
            {
                JsonElement page = operations.RootElement;
                Assert.Equal(100, page.GetProperty("returned").GetInt32());
                Assert.Equal(512, page.GetProperty("total").GetInt32());
                Assert.True(page.GetProperty("totalIsLowerBound").GetBoolean());
                Assert.NotNull(page.GetProperty("nextCursor").GetString());
                int responseLimit = page.GetProperty("responseByteLimit").GetInt32();
                Assert.True(
                    Encoding.UTF8.GetByteCount(page.GetRawText()) <= responseLimit,
                    "The actual serialized response must remain within its advertised byte limit.");
            }
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void OversizedStringsAndOverflowingTimestampsAreRejectedPerRecord()
    {
        string root = Directory.CreateTempSubdirectory("codenav-portal-invalid-bounds").FullName;
        try
        {
            string telemetryDirectory = Path.Combine(root, ".codenav", "telemetry");
            Directory.CreateDirectory(telemetryDirectory);
            string telemetry = Path.Combine(
                telemetryDirectory,
                $"phoenix-{Environment.ProcessId}-20260724080000-1.jsonl");
            string oversizedTool = new('界', 300);
            string underflowSemantic = JsonSerializer.Serialize(new
            {
                e = "semanticOp",
                ts = DateTimeOffset.MinValue,
                corr = "underflow-semantic",
                tool = "references",
                result = "exact",
                clusterLoadMs = 1
            }) + "\n";
            string underflowBuild = JsonSerializer.Serialize(new
            {
                e = "buildProgress",
                ts = DateTimeOffset.MinValue,
                buildId = "underflow-build",
                state = "running",
                phase = "scanning",
                elapsedMs = 1
            }) + "\n";
            string negativeNested = JsonSerializer.Serialize(new
            {
                e = "semanticOp",
                ts = DateTimeOffset.UtcNow,
                corr = "negative-nested",
                tool = "references",
                result = "exact",
                clusterLoadMs = 1,
                ownerLoad = new { gateWaitMs = -1 }
            }) + "\n";
            string overflowingNested = JsonSerializer.Serialize(new
            {
                e = "semanticOp",
                ts = DateTimeOffset.UtcNow,
                corr = "overflowing-nested",
                tool = "references",
                result = "exact",
                clusterLoadMs = 1,
                ownerLoad = new { loaded = long.MaxValue },
                scanLoad = new { loaded = 1 }
            }) + "\n";
            File.WriteAllText(
                telemetry,
                SemanticOperation("valid-before", "references", 10)
                + SemanticOperation("oversized", oversizedTool, 10)
                + underflowSemantic
                + underflowBuild
                + negativeNested
                + overflowingNested
                + SemanticOperation("valid-after", "implementations", 11));

            var source = new PortalDataSource([root]);
            source.RefreshForTest();

            using (JsonDocument bootstrap = Serialize(source.Bootstrap()))
            {
                JsonElement live = bootstrap.RootElement;
                Assert.False(live.GetProperty("dataComplete").GetBoolean());
                Assert.Equal(
                    5,
                    live.GetProperty("telemetry").GetProperty("invalidRecords").GetInt32());
            }
            using JsonDocument operations = Serialize(source.Operations());
            string[] correlations = operations.RootElement.GetProperty("items")
                .EnumerateArray()
                .Select(item => item.GetProperty("correlationId").GetString()!)
                .ToArray();
            Assert.Equal(2, correlations.Length);
            Assert.Contains("valid-before", correlations);
            Assert.Contains("valid-after", correlations);
            Assert.DoesNotContain("oversized", correlations);
            Assert.DoesNotContain("underflow-semantic", correlations);
            Assert.DoesNotContain("negative-nested", correlations);
            Assert.DoesNotContain("overflowing-nested", correlations);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void BoundedInitialTailIsDisclosedAsPartial()
    {
        string root = Directory.CreateTempSubdirectory("codenav-portal-tail").FullName;
        try
        {
            string telemetryDirectory = Path.Combine(root, ".codenav", "telemetry");
            Directory.CreateDirectory(telemetryDirectory);
            string telemetry = Path.Combine(
                telemetryDirectory,
                $"phoenix-{Environment.ProcessId}-20260724080000-1.jsonl");
            using (var stream = new FileStream(telemetry, FileMode.CreateNew, FileAccess.Write))
                stream.SetLength(4L * 1024 * 1024 + 1024);

            var source = new PortalDataSource([root]);
            source.RefreshForTest();

            using JsonDocument bootstrap = Serialize(source.Bootstrap());
            JsonElement live = bootstrap.RootElement;
            Assert.Equal("live", live.GetProperty("dataSource").GetString());
            Assert.False(live.GetProperty("dataComplete").GetBoolean());
            Assert.Equal(
                1,
                live.GetProperty("telemetry").GetProperty("tailLimitedFiles").GetInt32());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void AggregateRefreshBudgetDefersExcessInputAndDisclosesTheBacklog()
    {
        string root = Directory.CreateTempSubdirectory("codenav-portal-refresh-budget").FullName;
        try
        {
            string telemetryDirectory = Path.Combine(root, ".codenav", "telemetry");
            Directory.CreateDirectory(telemetryDirectory);
            for (int i = 1; i <= 3; i++)
            {
                string telemetry = Path.Combine(
                    telemetryDirectory,
                    $"phoenix-{Environment.ProcessId}-20260724080000-{i}.jsonl");
                using var stream = new FileStream(
                    telemetry,
                    FileMode.CreateNew,
                    FileAccess.Write);
                stream.SetLength(4L * 1024 * 1024);
            }

            var source = new PortalDataSource([root]);
            source.RefreshForTest();

            using (JsonDocument first = Serialize(source.Bootstrap()))
            {
                JsonElement live = first.RootElement;
                Assert.False(live.GetProperty("dataComplete").GetBoolean());
                Assert.True(
                    live.GetProperty("telemetry").GetProperty("ingestionBacklogs").GetInt32() > 0);
            }

            source.RefreshForTest();
            using JsonDocument second = Serialize(source.Bootstrap());
            Assert.Equal(
                0,
                second.RootElement.GetProperty("telemetry")
                    .GetProperty("ingestionBacklogs").GetInt32());
            Assert.False(second.RootElement.GetProperty("dataComplete").GetBoolean());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void UnsafeTelemetryAndIndexLeafLinksAreVisibleButNeverRead()
    {
        string root = Directory.CreateTempSubdirectory("codenav-portal-leaf-root").FullName;
        string outside = Directory.CreateTempSubdirectory("codenav-portal-leaf-out").FullName;
        try
        {
            string telemetryDirectory = Path.Combine(root, ".codenav", "telemetry");
            Directory.CreateDirectory(telemetryDirectory);
            string outsideTelemetry = Path.Combine(outside, "secret.jsonl");
            File.WriteAllText(
                outsideTelemetry,
                SemanticOperation("outside-leaf-secret", "references", 10));
            string telemetryLink = Path.Combine(
                telemetryDirectory,
                $"phoenix-{Environment.ProcessId}-20260724080000-1.jsonl");
            string outsideIndex = Path.Combine(outside, "index.db");
            File.WriteAllText(outsideIndex, "outside-index-secret");
            Directory.CreateDirectory(Path.Combine(root, ".codenav"));
            try
            {
                File.CreateSymbolicLink(telemetryLink, outsideTelemetry);
                File.CreateSymbolicLink(
                    Path.Combine(root, ".codenav", "index.db"),
                    outsideIndex);
            }
            catch
            {
                throw SkipException.ForSkip("The host cannot create file symbolic links.");
            }

            var source = new PortalDataSource([root]);
            source.RefreshForTest();

            using (JsonDocument bootstrap = Serialize(source.Bootstrap()))
            {
                JsonElement live = bootstrap.RootElement;
                Assert.Equal("live", live.GetProperty("dataSource").GetString());
                Assert.False(live.GetProperty("dataComplete").GetBoolean());
                Assert.True(
                    live.GetProperty("telemetry").GetProperty("sourceReadErrors").GetInt32() > 0);
            }
            using JsonDocument operations = Serialize(source.Operations());
            Assert.DoesNotContain(
                operations.RootElement.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("correlationId").GetString() == "outside-leaf-secret");
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
            TestWorkspaceCleanup.DeleteWorkspace(outside);
        }
    }

    [Fact]
    public void UnixSpecialTelemetryFileFailsClosedWithoutBlocking()
    {
        if (OperatingSystem.IsWindows())
            throw SkipException.ForSkip("FIFO coverage applies to Unix hosts.");

        string root = Directory.CreateTempSubdirectory("codenav-portal-fifo").FullName;
        try
        {
            string telemetryDirectory = Path.Combine(root, ".codenav", "telemetry");
            Directory.CreateDirectory(telemetryDirectory);
            string fifo = Path.Combine(
                telemetryDirectory,
                $"phoenix-{Environment.ProcessId}-20260724080000-1.jsonl");
            Assert.Equal(0, UnixMkFifo(fifo, 0x180));

            var source = new PortalDataSource([root]);
            var stopwatch = Stopwatch.StartNew();
            source.RefreshForTest();
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
            using JsonDocument bootstrap = Serialize(source.Bootstrap());
            Assert.Equal("live", bootstrap.RootElement.GetProperty("dataSource").GetString());
            Assert.False(bootstrap.RootElement.GetProperty("dataComplete").GetBoolean());
            Assert.Equal(
                1,
                bootstrap.RootElement.GetProperty("telemetry")
                    .GetProperty("sourceReadErrors").GetInt32());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void WorkspaceAndDirectoryDiscoveryCapsAreVisible()
    {
        var roots = new List<string>();
        try
        {
            for (int i = 0; i < 9; i++)
            {
                string root = Directory.CreateTempSubdirectory("codenav-portal-root-cap").FullName;
                roots.Add(root);
                Directory.CreateDirectory(Path.Combine(root, ".codenav", "telemetry"));
            }

            var source = new PortalDataSource(roots);
            Assert.Equal(8, source.WorkspaceCount);
            source.RefreshForTest();

            using JsonDocument bootstrap = Serialize(source.Bootstrap());
            JsonElement live = bootstrap.RootElement;
            Assert.Equal("live", live.GetProperty("dataSource").GetString());
            Assert.False(live.GetProperty("dataComplete").GetBoolean());
            Assert.Equal(8, live.GetProperty("summary").GetProperty("workspaceCount").GetInt32());
            Assert.Equal(
                1,
                live.GetProperty("telemetry")
                    .GetProperty("omittedConfiguredWorkspaces").GetInt32());

            string telemetryDirectory = Path.Combine(
                roots[0],
                ".codenav",
                "telemetry");
            for (int i = 0; i < 40; i++)
            {
                File.WriteAllText(
                    Path.Combine(
                        telemetryDirectory,
                        $"phoenix-{Environment.ProcessId}-20260724080000-{i + 1}.jsonl"),
                    string.Empty);
            }
            source.RefreshForTest();

            using (JsonDocument bounded = Serialize(source.Bootstrap()))
            {
                JsonElement telemetry = bounded.RootElement.GetProperty("telemetry");
                Assert.Equal(8, telemetry.GetProperty("omittedSourceFiles").GetInt32());
                Assert.False(
                    telemetry.GetProperty("omittedSourceFilesIsLowerBound").GetBoolean());
            }

            for (int i = 0; i < 257; i++)
                File.WriteAllText(Path.Combine(telemetryDirectory, $"noise-{i:D3}.tmp"), string.Empty);
            source.RefreshForTest();

            using JsonDocument rescanned = Serialize(source.Bootstrap());
            Assert.True(
                rescanned.RootElement.GetProperty("telemetry")
                    .GetProperty("sourceDiscoveryTruncations").GetInt32() > 0);
            Assert.True(
                rescanned.RootElement.GetProperty("telemetry")
                    .GetProperty("omittedSourceFilesIsLowerBound").GetBoolean());
            Assert.False(rescanned.RootElement.GetProperty("dataComplete").GetBoolean());
        }
        finally
        {
            foreach (string root in roots)
                TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void EqualLengthAtomicReplacementPurgesOldProvenanceAndReadsTheNewFile()
    {
        string root = Directory.CreateTempSubdirectory("codenav-portal-replace").FullName;
        try
        {
            string telemetryDirectory = Path.Combine(root, ".codenav", "telemetry");
            Directory.CreateDirectory(telemetryDirectory);
            string telemetry = Path.Combine(
                telemetryDirectory,
                $"phoenix-{Environment.ProcessId}-20260724080000-1.jsonl");
            string first = SemanticOperation(
                "replace-old-with-padding-xxxxxxxxxxxxxxxx",
                "references",
                10);
            File.WriteAllText(telemetry, first);
            var source = new PortalDataSource([root]);
            source.RefreshForTest();

            string next = SemanticOperation("replace-new", "references", 11).TrimEnd('\n');
            string replacement = next.PadRight(first.Length - 1) + "\n";
            Assert.Equal(first.Length, replacement.Length);
            string pending = Path.Combine(telemetryDirectory, "replacement.tmp");
            File.WriteAllText(pending, replacement);
            File.Move(pending, telemetry, overwrite: true);

            source.RefreshForTest();

            using JsonDocument operations = Serialize(source.Operations());
            string[] correlations = operations.RootElement.GetProperty("items")
                .EnumerateArray()
                .Select(item => item.GetProperty("correlationId").GetString()!)
                .ToArray();
            Assert.DoesNotContain("replace-old-with-padding-xxxxxxxxxxxxxxxx", correlations);
            Assert.Contains("replace-new", correlations);
            Assert.True(operations.RootElement.GetProperty("totalIsLowerBound").GetBoolean());
            using JsonDocument bootstrap = Serialize(source.Bootstrap());
            Assert.False(bootstrap.RootElement.GetProperty("dataComplete").GetBoolean());
            Assert.True(
                bootstrap.RootElement.GetProperty("telemetry")
                    .GetProperty("retentionEvictions").GetInt64() > 0);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void TimeRetentionEvictionIsDisclosedAsALowerBound()
    {
        string root = Directory.CreateTempSubdirectory("codenav-portal-time-retention").FullName;
        try
        {
            string telemetryDirectory = Path.Combine(root, ".codenav", "telemetry");
            Directory.CreateDirectory(telemetryDirectory);
            File.WriteAllText(
                Path.Combine(
                    telemetryDirectory,
                    $"phoenix-{Environment.ProcessId}-20260724080000-1.jsonl"),
                SemanticOperation(
                    "expired",
                    "references",
                    10,
                    timestamp: DateTimeOffset.UtcNow.AddHours(-2)));

            var source = new PortalDataSource([root]);
            source.RefreshForTest();

            using (JsonDocument bootstrap = Serialize(source.Bootstrap()))
            {
                JsonElement live = bootstrap.RootElement;
                Assert.False(live.GetProperty("dataComplete").GetBoolean());
                Assert.True(
                    live.GetProperty("telemetry").GetProperty("retentionEvictions").GetInt64() > 0);
                Assert.Equal(
                    JsonValueKind.String,
                    live.GetProperty("telemetry").GetProperty("retainedFromUtc").ValueKind);
            }
            using JsonDocument operations = Serialize(source.Operations());
            Assert.Equal(0, operations.RootElement.GetProperty("total").GetInt32());
            Assert.True(operations.RootElement.GetProperty("totalIsLowerBound").GetBoolean());
            Assert.False(operations.RootElement.GetProperty("dataComplete").GetBoolean());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void CursorExpiresWhenItsRetainedGenerationIsEvicted()
    {
        string root = Directory.CreateTempSubdirectory("codenav-portal-cursor-expiry").FullName;
        try
        {
            string telemetryDirectory = Path.Combine(root, ".codenav", "telemetry");
            Directory.CreateDirectory(telemetryDirectory);
            string telemetry = Path.Combine(
                telemetryDirectory,
                $"phoenix-{Environment.ProcessId}-20260724080000-1.jsonl");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            File.WriteAllText(
                telemetry,
                SemanticOperation("cursor-a", "references", 10, timestamp: now)
                + SemanticOperation("cursor-b", "references", 10, timestamp: now.AddMilliseconds(-1)));
            var source = new PortalDataSource([root]);
            source.RefreshForTest();
            Assert.True(PortalOperationQuery.TryParse(
                Query(("limit", "1")),
                out PortalOperationQuery first,
                out string? firstError),
                firstError);
            using JsonDocument page = Serialize(source.Operations(first));
            string cursor = page.RootElement.GetProperty("nextCursor").GetString()!;

            var appended = new StringBuilder();
            for (int i = 0; i < 600; i++)
            {
                appended.Append(SemanticOperation(
                    $"new-{i}",
                    "references",
                    10,
                    timestamp: now.AddMilliseconds(i + 1)));
            }
            File.AppendAllText(telemetry, appended.ToString());
            source.RefreshForTest();

            Assert.True(PortalOperationQuery.TryParse(
                Query(("limit", "1"), ("cursor", cursor)),
                out PortalOperationQuery expired,
                out string? expiredError),
                expiredError);
            Assert.Throws<PortalCursorExpiredException>(() => source.Operations(expired));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void EmptyTelemetryDirectoryAndInvalidSemanticShapesStayPartial()
    {
        string root = Directory.CreateTempSubdirectory("codenav-portal-empty").FullName;
        try
        {
            string telemetryDirectory = Path.Combine(root, ".codenav", "telemetry");
            Directory.CreateDirectory(telemetryDirectory);
            var source = new PortalDataSource([root]);
            source.RefreshForTest();
            using (JsonDocument empty = Serialize(source.Bootstrap()))
            {
                Assert.Equal("live", empty.RootElement.GetProperty("dataSource").GetString());
                Assert.False(empty.RootElement.GetProperty("dataComplete").GetBoolean());
            }

            File.WriteAllText(
                Path.Combine(
                    telemetryDirectory,
                    $"phoenix-{Environment.ProcessId}-20260724080000-1.jsonl"),
                "{\"e\":\"semanticOp\",\"corr\":\"missing-ts\",\"tool\":\"references\",\"result\":\"exact\"}\n"
                + JsonSerializer.Serialize(new
                {
                    e = "semanticOp",
                    ts = DateTimeOffset.UtcNow,
                    corr = "unknown-measurements",
                    tool = "references",
                    result = "exact"
                }) + "\n");
            source.RefreshForTest();

            using (JsonDocument bootstrap = Serialize(source.Bootstrap()))
            {
                Assert.False(bootstrap.RootElement.GetProperty("dataComplete").GetBoolean());
                Assert.Equal(
                    1,
                    bootstrap.RootElement.GetProperty("telemetry")
                        .GetProperty("invalidRecords").GetInt32());
            }
            using JsonDocument operations = Serialize(source.Operations());
            JsonElement operation = operations.RootElement
                .GetProperty("items")
                .EnumerateArray()
                .Single();
            Assert.Equal(JsonValueKind.Null, operation.GetProperty("durationMs").ValueKind);
            Assert.Equal(
                JsonValueKind.Null,
                operation.GetProperty("timings").GetProperty("topologyMs").ValueKind);
            Assert.Equal(
                JsonValueKind.Null,
                operation.GetProperty("counts").GetProperty("loaded").ValueKind);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void DirectoryReplacementAfterAnchoringCannotRedirectEnumeration()
    {
        if (OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Unix symlink race coverage; Windows junction coverage is separate.");

        string root = Directory.CreateTempSubdirectory("codenav-portal-swap-root").FullName;
        string outside = Directory.CreateTempSubdirectory("codenav-portal-swap-out").FullName;
        try
        {
            string telemetryDirectory = Path.Combine(root, ".codenav", "telemetry");
            string heldDirectory = Path.Combine(root, ".codenav", "telemetry-held");
            Directory.CreateDirectory(telemetryDirectory);
            File.WriteAllText(
                Path.Combine(
                    telemetryDirectory,
                    $"phoenix-{Environment.ProcessId}-20260724080000-1.jsonl"),
                SemanticOperation("inside", "references", 10));
            File.WriteAllText(
                Path.Combine(
                    outside,
                    $"phoenix-{Environment.ProcessId}-20260724080001-1.jsonl"),
                SemanticOperation("outside-secret", "references", 10));

            var source = new PortalDataSource([root]);
            source.BeforeTelemetryEnumerationForTest = () =>
            {
                Directory.Move(telemetryDirectory, heldDirectory);
                Directory.CreateSymbolicLink(telemetryDirectory, outside);
                source.BeforeTelemetryEnumerationForTest = null;
            };
            source.RefreshForTest();

            using JsonDocument operations = Serialize(source.Operations());
            Assert.DoesNotContain(
                operations.RootElement.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("correlationId").GetString() == "outside-secret");
            using JsonDocument bootstrap = Serialize(source.Bootstrap());
            Assert.False(bootstrap.RootElement.GetProperty("dataComplete").GetBoolean());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
            TestWorkspaceCleanup.DeleteWorkspace(outside);
        }
    }

    private static JsonDocument Serialize(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(
            value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static string CurrentProcessTelemetryPath(string telemetryDirectory)
    {
        using Process process = Process.GetCurrentProcess();
        string started = process.StartTime
            .ToUniversalTime()
            .ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(
            telemetryDirectory,
            $"phoenix-{Environment.ProcessId}-{started}-1.jsonl");
    }

    private static string SemanticOperation(
        string correlationId,
        string tool,
        long durationMs,
        string result = "exact",
        string? reason = null,
        bool? cold = null,
        DateTimeOffset? timestamp = null) =>
        JsonSerializer.Serialize(new
        {
            e = "semanticOp",
            ts = (timestamp ?? DateTimeOffset.UtcNow).ToString(
                "yyyy-MM-ddTHH:mm:ss.fffZ",
                CultureInfo.InvariantCulture),
            corr = correlationId,
            tool,
            accessMode = "writer",
            result,
            reason,
            cold,
            clusterLoadMs = durationMs,
            queryMs = 0,
            ownerLoad = new
            {
                gateWaitMs = 1,
                fingerprintMs = 2,
                topoMs = 3,
                projectLoadMs = durationMs - 6,
                requested = 4,
                loaded = 4,
                reloaded = 0,
                failed = 0
            }
        }) + "\n";

    private static string ServerInfo() =>
        JsonSerializer.Serialize(new
        {
            e = "serverInfo",
            ts = DateTimeOffset.UtcNow,
            version = "0.12.26",
            buildStamp = "0.12.26+test",
            schemaVersion = "18",
            featureIds = new[]
            {
                "operations-portal-jsonl-readonly",
                "operations-portal-live-build-status"
            },
            featureCount = 2,
            platform = "test-platform",
            accessMode = "writer",
            processId = Environment.ProcessId
        }) + "\n";

    private static string BuildProgress(
        string buildId,
        string state,
        string phase,
        long filesDone,
        long filesTotal) =>
        JsonSerializer.Serialize(new
        {
            e = "buildProgress",
            ts = DateTimeOffset.UtcNow,
            buildId,
            state,
            reason = "startup_missing",
            accessMode = "writer",
            phase,
            phaseElapsedMs = 250,
            elapsedMs = 1_000,
            filesDone,
            filesTotal,
            filesSkipped = 1,
            projectsFailed = 0,
            symbolsWritten = 321,
            bytesRead = 4_096,
            filesPerSecond = 9.5,
            estimatedRemainingMs = 300
        }) + "\n";

    private static IQueryCollection Query(params (string Name, string Value)[] values) =>
        new QueryCollection(values.ToDictionary(
            value => value.Name,
            value => new StringValues(value.Value),
            StringComparer.OrdinalIgnoreCase));

    private static void AssertReadError(PortalDataSource source, int expected)
    {
        using JsonDocument bootstrap = Serialize(source.Bootstrap());
        JsonElement root = bootstrap.RootElement;
        Assert.Equal(
            expected,
            root.GetProperty("telemetry").GetProperty("sourceReadErrors").GetInt32());
        Assert.Equal(expected == 0, root.GetProperty("dataComplete").GetBoolean());
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return new DirectoryInfo(link).LinkTarget is not null;
        }
        catch when (OperatingSystem.IsWindows())
        {
            try
            {
                using Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                    Arguments = $"/d /s /c \"mklink /J \\\"{link}\\\" \\\"{target}\\\"\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                })!;
                if (!process.WaitForExit(5000))
                {
                    process.Kill(entireProcessTree: true);
                    return false;
                }
                return process.ExitCode == 0
                    && new DirectoryInfo(link).LinkTarget is not null;
            }
            catch
            {
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int UnixMkFifo(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint mode);

    private static void CreateIndex(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "sentinel-unchanged");
    }
}
