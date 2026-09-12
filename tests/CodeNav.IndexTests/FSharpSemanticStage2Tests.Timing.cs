using System.Text.Json;
using CodeNav.Core.Discovery;
using CodeNav.Core.Semantic;
using CodeNav.FSharp;

namespace CodeNav.Tests;

public partial class FSharpSemanticStage2Tests
{
    [Fact]
    public void FSharpTimingAggregatesEnteredSpansAndFreezesInterruptedWork()
    {
        var timing = new SemanticService.FSharpSemanticTimingBox();
        Assert.Null(timing.Snapshot());
        using (timing.Admission()) { }
        Assert.NotNull(timing.Snapshot()!.AdmissionWaitMs);
        Assert.Null(timing.Snapshot()!.SnapshotCaptureMs);
        using (timing.Capture()) { }
        Assert.NotNull(timing.Snapshot()!.SnapshotCaptureMs);
        ISemanticTiming fcs = timing;
        using (fcs.StartPhase(SemanticPhase.Setup)) { }
        using (fcs.StartPhase(SemanticPhase.ProjectParseAndCheck)) Thread.Sleep(20);
        var first = timing.Snapshot()!;
        using (fcs.StartPhase(SemanticPhase.ProjectParseAndCheck)) Thread.Sleep(20);
        Assert.True(timing.Snapshot()!.ProjectParseAndCheckMs > first.ProjectParseAndCheckMs);
        Assert.Null(first.FileParseAndCheckMs);
        using IDisposable interrupted = fcs.StartPhase(SemanticPhase.FileParseAndCheck);
        Thread.Sleep(20);
        var frozen = timing.Snapshot()!;
        Assert.True(frozen.FileParseAndCheckMs > 0);
        string emitted = JsonSerializer.Serialize(frozen);
        Thread.Sleep(20);
        interrupted.Dispose();
        Assert.True(timing.Snapshot()!.FileParseAndCheckMs > frozen.FileParseAndCheckMs);
        Assert.Equal(emitted, JsonSerializer.Serialize(frozen));
    }

    [Fact]
    public async Task FSharpTimingKeepsCancelledAdmissionSeparateFromTheAdmittedRequest()
    {
        string root = Directory.CreateTempSubdirectory("cn-fcs-admission").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProject("net10.0", "Core.fs"));
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 42\n");
            using var fixture = Fixture.Create(root, ProjectModelMode.Simple);
            using var admitted = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            fixture.Semantic.FSharpSemanticSnapshotCapturedForTest = () =>
            {
                admitted.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            };
            var firstTiming = new SemanticService.FSharpSemanticTimingBox();
            var secondTiming = new SemanticService.FSharpSemanticTimingBox();
            Task<FSharpSemanticResult> first = Task.Run(() =>
                fixture.Semantic.FSharpSymbolAtAsync("Core/Core.fs", 2, 5, null, null, 60_000, firstTiming));
            try
            {
                Assert.True(admitted.Wait(TimeSpan.FromSeconds(10)));
                var second = await fixture.Semantic.FSharpSymbolAtAsync("Core/Core.fs", 2, 5,
                    null, null, 500, secondTiming);
                Assert.Equal("fsharp_semantic_timeout", second.Error);
                var frozen = secondTiming.Snapshot()!;
                Assert.True(frozen.AdmissionWaitMs > 0);
                Assert.Null(frozen.SnapshotCaptureMs);
                Assert.Null(frozen.FcsSetupMs);
                Assert.Null(frozen.ProjectParseAndCheckMs);
                Assert.Null(frozen.FileParseAndCheckMs);
                Assert.NotNull(firstTiming.Snapshot()!.SnapshotCaptureMs);
            }
            finally
            {
                release.Set();
                await first.WaitAsync(TimeSpan.FromSeconds(15));
            }
            Assert.Null((await first).Error);
            Assert.NotNull(firstTiming.Snapshot()!.FileParseAndCheckMs);
            Assert.Null(secondTiming.Snapshot()!.FileParseAndCheckMs);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void FSharpTimingDisclosesCaptureRefusalAndUnresolvedFcsWork()
    {
        string root = Directory.CreateTempSubdirectory("cn-fcs-failure").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProject("net10.0", "Core.fs"));
            WriteProject(root, "Core/Core.fs", "module PrivateTelemetry\nlet value = 42\n\n");
            using var fixture = Fixture.Create(root, ProjectModelMode.Simple);
            var (refusal, _) = FSharpSemanticTelemetryAssert.Observe(fixture.Manager.Telemetry,
                () => fixture.Tools.SymbolAt("Core/Core.fs", 2, 5,
                    projectPath: "Missing.fsproj", targetFramework: "net10.0", timeoutMs: 60_000),
                "symbol_at", "error");
            Assert.Equal("fsharp_type_check_context_not_found", refusal.GetProperty("error").GetString());
            JsonElement timing = refusal.GetProperty("timing").GetProperty("semanticColdStart");
            Assert.True(timing.GetProperty("snapshotCaptureMs").GetInt64() >= 0);
            Assert.False(timing.TryGetProperty("fcsSetupMs", out _));
            Assert.False(timing.TryGetProperty("projectParseAndCheckMs", out _));
            Assert.False(timing.TryGetProperty("fileParseAndCheckMs", out _));
            var (unresolved, _) = FSharpSemanticTelemetryAssert.Observe(fixture.Manager.Telemetry,
                () => fixture.Tools.SymbolAt("Core/Core.fs", 3, 1, timeoutMs: 60_000),
                "symbol_at", "unresolved");
            Assert.False(unresolved.TryGetProperty("error", out _));
            Assert.False(unresolved.GetProperty("found").GetBoolean());
            Assert.True(unresolved.GetProperty("timing").GetProperty("semanticColdStart")
                .GetProperty("fileParseAndCheckMs").GetInt64() >= 0);

            foreach (var (tool, invoke) in new (string, Func<string>)[]
                     {
                         ("implementations", () => fixture.Tools.Implementations(
                             path: "Core/Core.fs", line: 3, column: 1, timeoutMs: 60_000)),
                         ("callers", () => fixture.Tools.Callers(
                             path: "Core/Core.fs", line: 3, column: 1, timeoutMs: 60_000)),
                     })
            {
                var (missing, missingRecord) = FSharpSemanticTelemetryAssert.Observe(
                    fixture.Manager.Telemetry, invoke, tool, "unresolved");
                Assert.Equal("fsharp_symbol_not_resolved", missing.GetProperty("error").GetString());
                Assert.Equal("fsharp_symbol_not_resolved", missingRecord.GetProperty("reason").GetString());
                if (tool == "implementations")
                    Assert.False(missing.GetProperty("found").GetBoolean());
                else
                    Assert.False(missing.TryGetProperty("found", out _));
            }

            foreach (var (tool, invoke) in new (string, Func<string>)[]
                     {
                         ("references", () => fixture.Tools.References(
                             path: "Core/Core.fs", line: 2, column: 5, mode: "semantic",
                             projectPath: "Missing.fsproj", targetFramework: "net10.0", timeoutMs: 60_000)),
                         ("implementations", () => fixture.Tools.Implementations(
                             path: "Core/Core.fs", line: 2, column: 5,
                             projectPath: "Missing.fsproj", targetFramework: "net10.0", timeoutMs: 60_000)),
                         ("callers", () => fixture.Tools.Callers(
                             path: "Core/Core.fs", line: 2, column: 5,
                             projectPath: "Missing.fsproj", targetFramework: "net10.0", timeoutMs: 60_000)),
                     })
            {
                var (failed, failedRecord) = FSharpSemanticTelemetryAssert.Observe(
                    fixture.Manager.Telemetry, invoke, tool, "error");
                Assert.Equal("fsharp_type_check_context_not_found", failed.GetProperty("error").GetString());
                Assert.Equal("fsharp_type_check_context_not_found", failedRecord.GetProperty("reason").GetString());
                JsonElement failedTiming = failed.GetProperty("timing").GetProperty("semanticColdStart");
                Assert.True(failedTiming.GetProperty("snapshotCaptureMs").GetInt64() >= 0);
                Assert.False(failedTiming.TryGetProperty("fcsSetupMs", out _));
                Assert.False(failedTiming.TryGetProperty("projectParseAndCheckMs", out _));
                Assert.False(failedTiming.TryGetProperty("fileParseAndCheckMs", out _));
            }
        }
        finally { Cleanup(root); }
    }
}
