using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

public sealed class RefreshTimerFailureTests
{
    [Fact]
    public void HeadReadFailureLatchesBeforeDiagnosticsOutsideObservationLock()
    {
        string root = Directory.CreateTempSubdirectory("pcn-callback-lock").FullName;
        try
        {
            IndexManager? observed = null;
            bool? lockHeld = null;
            long generationAtLog = 0;
            using var manager = new IndexManager(root, log: line =>
            {
                if (!line.StartsWith("Refresh callback git_head failed:", StringComparison.Ordinal)) return;
                lockHeld = Monitor.IsEntered(Get(observed!, "_gitHeadObservationGate")!);
                generationAtLog = (long)Get(observed!, "_refreshCallbackFailureGeneration")!;
            });
            observed = manager;
            manager.GitHeadRetryDelayForTest = Timeout.InfiniteTimeSpan;
            manager.GitHeadSnapshotForTest = () => throw new IOException("snapshot failed");
            manager.NotifyGitHeadChangedForTest();
            Assert.Equal(false, lockHeld);
            Assert.Equal(1, generationAtLog);
            Assert.Single(manager.Telemetry.Snapshot(), text => text.Contains("refreshCallbackFailed"));
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public void CallbackFailureLogsFullExceptionButTelemetryRemainsCodeOnly()
    {
        string root = Directory.CreateTempSubdirectory("pcn-callback-log").FullName;
        try
        {
            var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var broker = new TestTelemetryBroker();
            using var manager = new IndexManager(root, log: logs.Enqueue, telemetryPipeName: broker.PipeName);
            manager.GitHeadRetryDelayForTest = Timeout.InfiniteTimeSpan;
            manager.GitHeadSnapshotForTest = () => throw new IOException("private snapshot at " + root);
            manager.NotifyGitHeadChangedForTest();
            string message = Assert.Single(logs, line => line.StartsWith("Refresh callback git_head failed:", StringComparison.Ordinal));
            Assert.Contains("private snapshot at " + root, message);
            Assert.Contains(nameof(CallbackFailureLogsFullExceptionButTelemetryRemainsCodeOnly), message);
            Assert.Contains("System.IO.IOException", message);
            string record = Assert.Single(manager.Telemetry.Snapshot(), line => line.Contains("refreshCallbackFailed"));
            Assert.DoesNotContain("private snapshot", record);
            Assert.DoesNotContain(root, record);
            Assert.DoesNotContain(nameof(CallbackFailureLogsFullExceptionButTelemetryRemainsCodeOnly), record);
            // IPC is supported on Windows only; the log/JSONL assertions above run everywhere.
            if (OperatingSystem.IsWindows())
            {
                Assert.True(SpinWait.SpinUntil(() => broker.FramesOfType("index.refresh.snapshot").Count > 0,
                    TimeSpan.FromSeconds(10)), "Callback failure IPC snapshot was not delivered");
                var data = broker.FramesOfType("index.refresh.snapshot")[0].RootElement.GetProperty("data");
                Assert.Equal("failed", data.GetProperty("state").GetString());
                Assert.Equal("refresh_callback_failed", data.GetProperty("errorCode").GetString());
                Assert.Equal(0, data.GetProperty("batchProcessed").GetInt32());
                Assert.Equal(0, data.GetProperty("elapsedMs").GetInt64());
                Assert.DoesNotContain("private snapshot", data.GetRawText());
                Assert.DoesNotContain(nameof(CallbackFailureLogsFullExceptionButTelemetryRemainsCodeOnly), data.GetRawText());
                Assert.DoesNotContain("snapshot at", JsonSerializer.Serialize(manager.Health()));
            }
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExceptionFormattingAndSinkFailuresDoNotPreventCallbackFailureReporting(bool brokenFormatter)
    {
        string root = Directory.CreateTempSubdirectory("pcn-format-log").FullName;
        try
        {
            using var manager = new IndexManager(root, log: _ => throw new IOException("sink failed"));
            manager.GitHeadRetryDelayForTest = Timeout.InfiniteTimeSpan;
            manager.GitHeadSnapshotForTest = () => throw (brokenFormatter
                ? new BrokenFormattingException() : new IOException("snapshot failed"));
            manager.NotifyGitHeadChangedForTest();
            Assert.False(manager.RefreshWorkerFailed);
            string line = Assert.Single(manager.Telemetry.Snapshot(), text => text.Contains("refreshCallbackFailed"));
            Assert.Contains(brokenFormatter ? nameof(BrokenFormattingException) : "IOException", line);
            Assert.Equal(0, manager.QueuedRefreshCountForTest);
            manager.GitHeadSnapshotForTest = () => new GitInfo.HeadSnapshot(new string('a', 40), "main", "attached");
            manager.NotifyGitHeadChangedForTest();
            Assert.Equal(1, manager.QueuedRefreshCountForTest);
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    private sealed class BrokenFormattingException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("formatter failed");
    }

    [Theory]
    [InlineData("git", false)]
    [InlineData("git", true)]
    [InlineData("recovery", false)]
    [InlineData("recovery", true)]
    [InlineData("git-fault", true)]
    public async Task TimerFailureIsContainedInAnIsolatedProcess(string mode, bool brokenLogger)
    {
        string root = Directory.CreateTempSubdirectory("pcn-timer").FullName;
        try
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add(typeof(RefreshTimerFailureTests).Assembly.Location);
            foreach (string arg in new[] { "--refresh-timer-canary", root, mode, brokenLogger.ToString() })
                start.ArgumentList.Add(arg);
            start.Environment["PHOENIX_TELEMETRY_IPC"] = "0";
            using var process = Process.Start(start)!;
            var captured = await TestProcessLifecycle.WaitForExitAndDrainAsync(process,
                process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync(),
                TimeSpan.FromSeconds(30), "refresh timer containment");
            Assert.True(captured.ExitCode == 0, $"Child exit {captured.ExitCode}: {captured.Output}\n{captured.Error}");
            Assert.Contains("timer assertions passed", captured.Output);
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    internal static async Task<int> RunCanaryAsync(string root, string mode, bool brokenLogger)
    {
        try
        {
            using var diagnostic = new ManualResetEventSlim();
            using var manager = new IndexManager(root, log: _ =>
            {
                diagnostic.Set();
                if (brokenLogger) throw new IOException("private sink failure");
            });
            manager.GitHeadRetryDelayForTest = TimeSpan.FromMilliseconds(10);
            manager.GitHeadSnapshotForTest = () =>
            {
                // Execute the initial real timer callback, but never arm a second one
                // while the parent test waits to drain and inspect this invocation.
                manager.GitHeadRetryDelayForTest = Timeout.InfiniteTimeSpan;
                return mode == "git-fault"
                    ? throw new IOException("private snapshot failure")
                    : new GitInfo.HeadSnapshot(new string('a', 40), "main", "attached");
            };
            string timerField;
            if (mode == "recovery")
            {
                Set(manager, "_refreshIncompleteReason", IndexManager.RefreshInputUnavailableCause);
                manager.RefreshRecoverySweepDelayForTest = _ => TimeSpan.FromMilliseconds(10);
                Invoke(manager, "ScheduleRefreshRecoverySweep", "A.cs");
                timerField = "_refreshRecoverySweepRetry";
            }
            else
            {
                Invoke(manager, "ScheduleGitHeadRetry");
                timerField = "_gitHeadRetry";
            }
            Assert.True(diagnostic.Wait(TimeSpan.FromSeconds(10)), "callback must execute");
            // Drain the actual callback; no assertion can race its final queue publication.
            await ((Timer)Get(manager, timerField)!).DisposeAsync();
            Assert.False(manager.RefreshWorkerFailed);
            if (mode == "git-fault")
            {
                Assert.Equal(0, manager.QueuedRefreshCountForTest);
                string line = Assert.Single(manager.Telemetry.Snapshot(), text => text.Contains("refreshCallbackFailed"));
                using var document = JsonDocument.Parse(line);
                Assert.Equal("git_head", document.RootElement.GetProperty("callback").GetString());
                Assert.Equal("IOException", document.RootElement.GetProperty("exceptionType").GetString());
                Assert.DoesNotContain("private", line);
                Assert.DoesNotContain(root, line);
                manager.GitHeadSnapshotForTest = () => new GitInfo.HeadSnapshot(new string('b', 40), "main", "attached");
                manager.NotifyGitHeadChangedForTest();
                Assert.Equal(1, manager.QueuedRefreshCountForTest); // Subsequent signals still work.
            }
            else
            {
                Assert.Equal(1, manager.QueuedRefreshCountForTest); // A failed log must not swallow work.
                Assert.DoesNotContain(manager.Telemetry.Snapshot(), text => text.Contains("refreshCallbackFailed"));
            }
            if (mode == "recovery")
                Assert.Equal(IndexManager.RefreshInputUnavailableCause, manager.Health().RefreshIncompleteReason);
            await manager.ShutdownAsync();
            Assert.False(manager.RequestRefresh());
            Console.WriteLine("timer assertions passed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Get(object value, string field) => value.GetType().GetField(field, Private)!.GetValue(value);
    private static void Set(object value, string field, object data) => value.GetType().GetField(field, Private)!.SetValue(value, data);
    private static void Invoke(object value, string method, params object[] args) => value.GetType().GetMethod(method, Private)!.Invoke(value, args);
}
