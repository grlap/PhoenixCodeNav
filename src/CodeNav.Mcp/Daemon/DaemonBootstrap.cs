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
                        DaemonStartupFailures.DiedBeforeReport(exitCode, bootstrap: false));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    report = DaemonStartupReport.Refused(
                        daemon.Id,
                        DaemonStartupFailures.ReportTimeout());
                }
                catch (IOException)
                {
                    report = DaemonStartupReport.Refused(
                        daemon.Id,
                        DaemonStartupFailures.InvalidReport());
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
                DaemonStartupFailures.LaunchFailed(ex));
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
