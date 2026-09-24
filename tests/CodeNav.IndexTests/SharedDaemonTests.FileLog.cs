using System.Diagnostics;
using CodeNav.Mcp;
using CodeNav.Mcp.Daemon;

namespace CodeNav.Tests;

public sealed partial class SharedDaemonTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetachedDaemonPersistsHostLifecycleOrSurvivesUnwritableLog(bool unwritable)
    {
        string root = Directory.CreateTempSubdirectory("pcn-daemon-log").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        bool succeeded = false;
        Process? daemon = null;
        try
        {
            if (unwritable)
            {
                Directory.CreateDirectory(Path.Combine(root, ".codenav"));
                File.WriteAllText(Path.Combine(root, ".codenav", "logs"), "blocked");
            }
            daemon = LaunchDaemonForTest(FindMcpExecutable(), root, false, idleMilliseconds: 350);
            await daemon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(0, daemon.ExitCode);
            if (unwritable)
                Assert.Equal("blocked", File.ReadAllText(Path.Combine(root, ".codenav", "logs")));
            else
            {
                string log = Assert.Single(Directory.GetFiles(Path.Combine(root, ".codenav", "logs"), "phoenix-*.log"));
                string[] lines = File.ReadAllLines(log);
                Assert.Contains("daemon_start build=" + BuildInfo.Stamp, lines[0]);
                Assert.Contains("pid=" + daemon.Id, lines[0]);
                Assert.Contains("Phoenix shared daemon ready", string.Join('\n', lines));
                Assert.Contains("daemon_stop state=", lines[^1]);
                Assert.Contains("reason=retired", lines[^1]);
            }
            succeeded = true;
        }
        finally
        {
            if (daemon is not null)
            {
                if (!daemon.HasExited)
                {
                    try { await RetireDaemonForTestAsync(endpoint); } catch { }
                    try { await daemon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch { daemon.Kill(entireProcessTree: false); }
                }
                daemon.Dispose();
            }
            await CleanupEndpointForTestAsync(endpoint);
            ExternalProcessWorkspaceCleanup.DeleteAfterSuccess(succeeded, root);
        }
    }
}
