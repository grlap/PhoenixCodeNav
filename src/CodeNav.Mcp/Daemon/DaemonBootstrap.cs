using System.Diagnostics;

namespace CodeNav.Mcp.Daemon;

internal static class DaemonBootstrap
{
    internal static async Task<int> RunAsync(
        DaemonEndpoint endpoint,
        string? indexDb,
        bool rebuild,
        bool keepAlive,
        TimeSpan? idleLingerForTest,
        CancellationToken cancellationToken)
    {
        DaemonStartupReport report;
        try
        {
            using Process daemon = DaemonProcessIsolation.LaunchDaemonChild(
                endpoint, indexDb, rebuild, keepAlive, idleLingerForTest);
            bool ready = false;
            try
            {
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                startup.CancelAfter(DaemonProxy.StartupTimeout);
                try
                {
                    report = await DaemonStartupChannel.ReadAsync(
                        daemon.StandardOutput.BaseStream, startup.Token).ConfigureAwait(false);
                    ready = report.Ready;
                }
                catch (EndOfStreamException)
                {
                    int? exitCode = await TryObserveExitCodeAsync(daemon, cancellationToken)
                        .ConfigureAwait(false);
                    report = DaemonStartupReport.Refused(
                        daemon.Id,
                        new DaemonUnavailableFailure(
                            "daemon_died_before_report",
                            exitCode is { } code
                                ? $"Phoenix daemon exited with code {code} before reporting startup state."
                                : "Phoenix daemon closed its startup channel before reporting startup state.",
                            "Retry the MCP connection; if this repeats, inspect the Phoenix server log for the startup failure.",
                            Retryable: true));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    report = DaemonStartupReport.Refused(
                        daemon.Id,
                        new DaemonUnavailableFailure(
                            "daemon_startup_report_timeout",
                            "Phoenix daemon did not report ready or refused before the startup deadline.",
                            "Retry the MCP connection; if this repeats, inspect the Phoenix server log for a blocked startup.",
                            Retryable: true));
                }
                catch (IOException)
                {
                    report = DaemonStartupReport.Refused(
                        daemon.Id,
                        new DaemonUnavailableFailure(
                            "daemon_startup_report_invalid",
                            "Phoenix daemon returned an invalid private startup report.",
                            "Restart active Phoenix sessions for this workspace, then reconnect.",
                            Retryable: true));
                }
            }
            finally
            {
                if (!ready)
                    await DaemonProcessIsolation.TerminateFailedStartupAsync(daemon)
                        .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or
                                   UnauthorizedAccessException)
        {
            report = DaemonStartupReport.Refused(
                0,
                new DaemonUnavailableFailure(
                    "daemon_launch_failed",
                    $"Phoenix daemon process could not be launched ({ex.GetType().Name}).",
                    "Verify the deployed Phoenix executable and retry the MCP connection.",
                    Retryable: true));
        }

        await DaemonStartupChannel.WriteAsync(
            Console.OpenStandardOutput(), report, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int?> TryObserveExitCodeAsync(
        Process daemon,
        CancellationToken cancellationToken)
    {
        try
        {
            await daemon.WaitForExitAsync(cancellationToken)
                .WaitAsync(DaemonProtocol.HandshakeTimeout, cancellationToken)
                .ConfigureAwait(false);
            return daemon.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or
                                   OperationCanceledException)
        {
            return null;
        }
    }
}
