using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using CodeNav.Core.Indexing;
using CodeNav.Mcp;
using CodeNav.Mcp.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeNav.Tests;

[Collection("Shared daemon MCP process isolation")]
public sealed class DaemonFileLogTests
{
    [Fact]
    public void CapabilityAdvertisesPersistentLogWithoutConsumingManifestReserve()
    {
        var health = new IndexHealth("ready", "1", null, null, 0, null, 0, "workspace", "index.db");
        string raw = NavigationTools.ServerCapabilitiesUncompactedForTest(health);
        using var document = System.Text.Json.JsonDocument.Parse(raw);
        var feature = Assert.Single(document.RootElement.GetProperty("features").EnumerateArray(),
            item => item.GetProperty("id").GetString() == "daemon-file-log");
        Assert.Contains(".codenav/logs", feature.GetProperty("summary").GetString());
        Assert.Contains("14-day", feature.GetProperty("summary").GetString());
        Assert.True(Json.HardBudgetBytes - Json.Utf8Bytes(raw) >= 2 * 1024);
    }

    [Fact]
    public void HostProviderFlushesFiltersAndOutlivesHost()
    {
        WithWorkspace(root =>
        {
            using var log = DaemonFileLog.Start(root, null);
            using (var host = McpApplication.BuildHost(root, null, false, daemonLog: log))
            {
                var factory = host.Services.GetRequiredService<ILoggerFactory>();
                factory.CreateLogger("CodeNav.Core.Indexing.IndexManager").LogInformation("index-info");
                factory.CreateLogger("PhoenixCodeNav.Daemon").LogInformation("daemon-info");
                factory.CreateLogger("Microsoft.Hosting").LogInformation("framework-hidden");
                factory.CreateLogger("ModelContextProtocol.Server").LogInformation("sdk-hidden");
                factory.CreateLogger("ModelContextProtocol.Server").LogWarning("sdk-warning");
                factory.CreateLogger("CodeNav.Core.Indexing.IndexManager").LogDebug("debug-hidden");
                string text = ReadLog(log.FilePath!);
                Assert.Contains("index-info", text);
                Assert.Contains("daemon-info", text);
                Assert.Contains("sdk-warning", text);
                Assert.DoesNotContain("hidden", text);
            }
            log.Failure("caught-after-host-disposal", CapturedFailure());
            log.Shutdown("ready", "retired");
            string[] lines = ReadLog(log.FilePath!).TrimEnd().Split('\n');
            Assert.Contains("daemon_start build=" + BuildInfo.Stamp, lines[0]);
            Assert.Contains("workspace=" + root, lines[0]);
            Assert.Contains("indexDb=" + IndexBuilder.DefaultDbPath(root), lines[0]);
            Assert.Contains("pid=" + Environment.ProcessId, lines[0]);
            Assert.Contains("mode=daemon", lines[0]);
            Assert.Contains("caught-after-host-disposal", string.Join('\n', lines));
            Assert.Contains(nameof(CapturedFailure), string.Join('\n', lines));
            Assert.Contains("daemon_stop state=ready reason=retired uptimeMs=", lines[^1]);
            Assert.Equal(0, log.Dropped);
        });
    }

    [Fact]
    public async Task UnwritableLogDirectoryDoesNotPreventDaemonStartup()
    {
        string root = Directory.CreateTempSubdirectory("pcn-log").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".codenav"));
            File.WriteAllText(Path.Combine(root, ".codenav", "logs"), "not a directory");
            using var log = DaemonFileLog.Start(root, null);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var daemon = new DaemonServer(DaemonEndpoint.Create(root, null), null, false, false,
                idleLinger: TimeSpan.FromMilliseconds(50), fileLog: log);
            Assert.Equal(0, await daemon.RunAsync(stop.Token));
            Assert.Equal("retired", daemon.ShutdownReason);
            Assert.True(log.Dropped > 0);
            Assert.Equal("not a directory", File.ReadAllText(Path.Combine(root, ".codenav", "logs")));
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public void BrokenWriterAndFormatterDoNotEscapeAndDropsAreCounted()
    {
        WithWorkspace(root =>
        {
            using var log = DaemonFileLog.Start(root, null);
            log.Write(LogLevel.Error, "PhoenixCodeNav.Daemon", () => throw new IOException("formatter"));
            Assert.Equal(1, log.Dropped);
            log.Failure("still-working", CapturedFailure());
            Assert.Contains("still-working", ReadLog(log.FilePath!));
            var writer = (StreamWriter)typeof(DaemonFileLog)
                .GetField("_writer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(log)!;
            writer.BaseStream.Dispose();
            log.Failure("io-failed", CapturedFailure());
            log.Failure("disabled", CapturedFailure());
            Assert.Equal(3, log.Dropped);
            Assert.DoesNotContain("disabled", ReadLog(log.FilePath!));
        });
    }

    [Fact]
    public void PrunesOnlyOldOwnedLogFiles()
    {
        WithWorkspace(root =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(root, ".codenav", "logs")).FullName;
            foreach (string name in new[] { "phoenix-old.log", "phoenix-recent.log", "other.log", "phoenix-old.jsonl" })
            {
                string path = Path.Combine(directory, name);
                File.WriteAllText(path, "retained control");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(name == "phoenix-recent.log" ? -13 : -15));
            }
            Directory.CreateDirectory(Path.Combine(directory, "phoenix-dir.log"));
            using var log = DaemonFileLog.Start(root, null);
            Assert.False(File.Exists(Path.Combine(directory, "phoenix-old.log")));
            Assert.True(File.Exists(Path.Combine(directory, "phoenix-recent.log")));
            Assert.True(File.Exists(Path.Combine(directory, "other.log")));
            Assert.True(File.Exists(Path.Combine(directory, "phoenix-old.jsonl")));
            Assert.True(Directory.Exists(Path.Combine(directory, "phoenix-dir.log")));
            Assert.True(File.Exists(log.FilePath));
        });
    }

    [Fact]
    public void StdioHostDoesNotUseDaemonFileProvider()
    {
        WithWorkspace(root =>
        {
            using var log = DaemonFileLog.Start(root, null);
            using var host = McpApplication.BuildHost(root, null, true, daemonLog: log);
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CodeNav.Test").LogWarning("stdio-only");
            Assert.DoesNotContain("stdio-only", ReadLog(log.FilePath!));
        });
    }

    [Theory]
    [InlineData("unhandled")]
    [InlineData("unobserved")]
    public async Task ProcessHooksPreserveExceptionDetails(string mode)
    {
        string root = Directory.CreateTempSubdirectory("pcn-log-hook").FullName;
        try
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in new[] { typeof(DaemonFileLogTests).Assembly.Location, "--daemon-log-canary", root, mode })
                start.ArgumentList.Add(arg);
            using var child = Process.Start(start)!;
            var captured = await TestProcessLifecycle.WaitForExitAndDrainAsync(child,
                child.StandardOutput.ReadToEndAsync(), child.StandardError.ReadToEndAsync(),
                TimeSpan.FromSeconds(30), "daemon log exception hooks");
            if (mode == "unhandled") Assert.NotEqual(0, captured.ExitCode);
            else Assert.True(captured.ExitCode == 0, captured.Error);
            string path = Assert.Single(Directory.GetFiles(Path.Combine(root, ".codenav", "logs"), "phoenix-*.log"));
            string text = File.ReadAllText(path);
            Assert.Contains(mode == "unhandled" ? "unhandled_exception" : "unobserved_task_exception", text);
            Assert.Contains("System.IO.IOException", text);
            Assert.Contains("hook canary failure", text);
            Assert.Contains(nameof(ThrowCanaryFailure), text);
            Assert.Single(captured.Error.Split('\n'), line => line.StartsWith("Phoenix daemon log:"));
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    internal static int RunCanary(string root, string mode)
    {
        using var log = DaemonFileLog.Start(root, null);
        DaemonProcessIsolation.DetachStandardStreams();
        if (mode == "unhandled")
        {
            new Thread(ThrowCanaryFailure).Start();
            Thread.Sleep(Timeout.Infinite);
            return 1;
        }
        bool observed = false;
        EventHandler<UnobservedTaskExceptionEventArgs> after = (_, args) => observed = args.Observed;
        TaskScheduler.UnobservedTaskException += after;
        try
        {
            WeakReference reference = AbandonFaultedTask();
            var wait = Stopwatch.StartNew();
            while (!observed && wait.Elapsed < TimeSpan.FromSeconds(10))
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(10);
            }
            GC.KeepAlive(reference);
            return observed ? 0 : 2;
        }
        finally { TaskScheduler.UnobservedTaskException -= after; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AbandonFaultedTask()
    {
        Task task = Task.Run(ThrowCanaryFailure);
        Assert.True(SpinWait.SpinUntil(() => task.IsCompleted, TimeSpan.FromSeconds(5)));
        return new WeakReference(task);
    }

    private static void ThrowCanaryFailure() => throw new IOException("hook canary failure");
    private static string ReadLog(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return reader.ReadToEnd();
    }
    private static Exception CapturedFailure()
    {
        try { throw new IOException("full-detail failure"); }
        catch (Exception ex) { return ex; }
    }

    private static void WithWorkspace(Action<string> action)
    {
        string root = Directory.CreateTempSubdirectory("pcn-log").FullName;
        try { action(root); }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }
}
