using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

public sealed class RefreshTimerFailureTests
{
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
            manager.GitHeadSnapshotForTest = () => mode == "git-fault"
                ? throw new IOException("private snapshot failure")
                : new GitInfo.HeadSnapshot(new string('a', 40), "main", "attached");
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
