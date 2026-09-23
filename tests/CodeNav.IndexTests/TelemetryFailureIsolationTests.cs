using System.Reflection;
using System.Threading.Channels;
using CodeNav.Core.Diagnostics;
using CodeNav.Core.Indexing;
using CodeNav.Mcp;

namespace CodeNav.Tests;

public sealed class TelemetryFailureIsolationTests
{
    [Fact]
    public void CallbackDiagnosticCapabilityAndConfidenceAreExplicit()
    {
        Assert.Equal("indexed", NavigationTools.FSharpSemanticConfidence(IndexManager.RefreshCallbackFailedCause));
        var health = new IndexHealth("stale", "1", null, null, 0,
            IndexManager.RefreshCallbackFailedCause, 0, "workspace", "index.db",
            RefreshIncompleteReason: IndexManager.RefreshCallbackFailedCause);
        Assert.Equal("indexed", Meta.From(health, "exact", "semantic").Confidence);
        using var document = System.Text.Json.JsonDocument.Parse(
            NavigationTools.ServerCapabilitiesUncompactedForTest(health));
        var feature = Assert.Single(document.RootElement.GetProperty("features").EnumerateArray(),
            entry => entry.GetProperty("id").GetString() == "refresh-callback-failure");
        Assert.Contains("refreshCallbackFailed", feature.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task SerializationFailureWithBrokenDiagnosticsDoesNotEscapeEmit()
    {
        string root = Directory.CreateTempSubdirectory("pcn-log").FullName;
        int diagnostics = 0;
        try
        {
            using var log = new TelemetryLog(root, _ =>
            {
                Interlocked.Increment(ref diagnostics);
                throw new IOException("diagnostics unavailable");
            });
            Assert.Null(Record.Exception(() => log.Emit(new BrokenRecord())));
            Assert.Equal(1, diagnostics);
            Assert.Empty(log.Snapshot());
            log.Emit(new { e = "after_serialization_failure" });
            await log.ShutdownAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(Drainer(log).IsCompletedSuccessfully);
            Assert.Contains("after_serialization_failure", Assert.Single(log.Snapshot()));
            Assert.Contains("after_serialization_failure", File.ReadAllText(log.FilePath));
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileFailureKeepsDrainingEvenWhenDiagnosticsThrow(bool brokenLogger)
    {
        string root = Directory.CreateTempSubdirectory("pcn-log").FullName;
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int diagnostics = 0;
        try
        {
            File.WriteAllText(Path.Combine(root, ".codenav"), "not a directory");
            using var log = new TelemetryLog(root, _ =>
            {
                Interlocked.Increment(ref diagnostics);
                reported.TrySetResult();
                if (brokenLogger) throw new IOException("diagnostics unavailable");
            });
            log.Emit(new { e = "before_file_failure" });
            await reported.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (int i = 0; i < 300; i++) log.Emit(new { e = "after_file_failure", i });
            await log.ShutdownAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(Drainer(log).IsCompletedSuccessfully, "diagnostic failure must not kill the consumer");
            var pending = (Channel<string>)typeof(TelemetryLog)
                .GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(log)!;
            Assert.Equal(0, pending.Reader.Count);
            Assert.False(pending.Writer.TryWrite("after_shutdown"));
            Assert.Equal(1, diagnostics); // File I/O failure is reported once; the ring stays useful.
            Assert.Equal(256, log.Snapshot().Count);
            Assert.Contains("\"i\":299", log.Snapshot()[^1]);
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    private static Task Drainer(TelemetryLog log) => (Task)typeof(TelemetryLog)
        .GetField("_drainer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(log)!;

    private sealed class BrokenRecord
    {
        public string Value => throw new InvalidOperationException("serialization failed");
    }
}
