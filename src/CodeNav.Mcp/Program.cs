using System.Diagnostics;
using System.Runtime.InteropServices;
using CodeNav.Mcp;
using CodeNav.Mcp.Daemon;

// x5ls.1: the telemetry producer's hello.mcpVersion — Core cannot reference Mcp's BuildInfo.
CodeNav.Core.Telemetry.TelemetryProducer.ProductVersion = BuildInfo.Version;

McpCommandLine command;
try
{
    command = McpCommandLine.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    McpCommandLine.WriteUsage();
    return 2;
}

if (command.Help)
{
    McpCommandLine.WriteUsage();
    return 0;
}

string workspaceRoot = command.WorkspaceRoot ??
    Environment.GetEnvironmentVariable("CODENAV_WORKSPACE_ROOT") ??
    Directory.GetCurrentDirectory();
if (!Directory.Exists(workspaceRoot))
{
    if (command.Mode == McpLaunchMode.SharedProxy)
    {
        return await UnavailableMcpShim.RunAsync(new DaemonUnavailableFailure(
            "daemon_workspace_unavailable",
            "Phoenix workspace root does not exist or is not a directory.",
            "Fix --workspace-root or CODENAV_WORKSPACE_ROOT, then reconnect.",
            Retryable: false));
    }
    Console.Error.WriteLine($"Workspace root not found: {workspaceRoot}");
    return 2;
}

workspaceRoot = Path.GetFullPath(workspaceRoot);
string? indexDb;
try
{
    indexDb = string.IsNullOrWhiteSpace(command.IndexDb)
        ? null
        : Path.GetFullPath(command.IndexDb);
}
catch (Exception ex) when (ex is ArgumentException or IOException or
                           NotSupportedException or UnauthorizedAccessException)
{
    if (command.Mode == McpLaunchMode.SharedProxy)
    {
        return await UnavailableMcpShim.RunAsync(new DaemonUnavailableFailure(
            "daemon_index_destination_invalid",
            "Phoenix index destination could not be normalized.",
            "Fix --index-db and reconnect.",
            Retryable: false));
    }
    Console.Error.WriteLine("Index destination is invalid.");
    return 2;
}
using var shutdown = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};
Console.CancelKeyPress += cancelHandler;
EventHandler processExit = (_, _) => shutdown.Cancel();
AppDomain.CurrentDomain.ProcessExit += processExit;
PosixSignalRegistration? terminate = null;
if (!OperatingSystem.IsWindows())
{
    terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        shutdown.Cancel();
    });
}

try
{
    if (command.Mode == McpLaunchMode.DaemonRetireAuthorized)
    {
        try
        {
            DaemonEndpoint endpoint = DaemonEndpoint.Create(workspaceRoot, indexDb);
            using var retirement = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            retirement.CancelAfter(TimeSpan.FromMinutes(2));
            await DaemonRetirement.RetireForHarnessAsync(endpoint, retirement.Token);
            return 0;
        }
        catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                "Phoenix daemon did not complete authority-checked retirement before the deadline.");
            return 3;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or
                                   NotSupportedException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"Phoenix daemon authority-checked retirement failed ({ex.GetType().Name}).");
            return 3;
        }
    }

    if (command.Mode == McpLaunchMode.Daemon)
    {
        DaemonProcessIsolation.DetachStandardStreams(preserveStandardOutput: true);
        var reporter = new DaemonStartupReporter(Console.OpenStandardOutput());
        try
        {
            DaemonEndpoint endpoint = DaemonEndpoint.Create(workspaceRoot, indexDb);
            var daemon = new DaemonServer(
                endpoint,
                indexDb,
                command.Rebuild,
                command.KeepAlive,
                command.DaemonIdle,
                startupReporter: reporter);
            return await daemon.RunAsync(shutdown.Token);
        }
        catch (Exception ex) when (!shutdown.IsCancellationRequested)
        {
            await reporter.ReportAsync(DaemonStartupReport.Refused(
                Environment.ProcessId,
                DaemonStartupFailures.Unexpected(ex)), CancellationToken.None);
            return 3;
        }
    }

    if (command.Mode == McpLaunchMode.DaemonBootstrap)
    {
        try
        {
            DaemonEndpoint endpoint = DaemonEndpoint.Create(workspaceRoot, indexDb);
            return await DaemonBootstrap.RunAsync(
                endpoint,
                indexDb,
                command.Rebuild,
                command.KeepAlive,
                command.DaemonIdle,
                shutdown.Token);
        }
        catch (Exception ex) when (!shutdown.IsCancellationRequested)
        {
            await DaemonStartupChannel.WriteAsync(
                Console.OpenStandardOutput(),
                DaemonStartupReport.Refused(
                    0, DaemonStartupFailures.Unexpected(ex)),
                CancellationToken.None);
            return 3;
        }
    }

    if (command.Mode == McpLaunchMode.SharedProxy)
    {
        DaemonEndpoint endpoint;
        try
        {
            endpoint = DaemonEndpoint.Create(workspaceRoot, indexDb);
        }
        catch (DaemonRuntimeDirectoryUnavailableException ex)
        {
            return await UnavailableMcpShim.RunAsync(new DaemonUnavailableFailure(
                "daemon_runtime_directory_unavailable",
                $"Phoenix could not select a runtime directory for its local transport ({ex.GetType().Name}).",
                "Verify the owner-only /tmp runtime authority or shorten the configured runtime paths, then reconnect.",
                Retryable: false), shutdown.Token);
        }
        catch (Exception ex)
        {
            return await UnavailableMcpShim.RunAsync(new DaemonUnavailableFailure(
                "daemon_workspace_identity_unavailable",
                $"Phoenix could not prove the physical workspace identity ({ex.GetType().Name}).",
                "Verify workspace ownership and path safety, then reconnect.",
                Retryable: false), shutdown.Token);
        }
        DaemonRuntimeDiagnostics.WriteDiscoveryWarning(endpoint, Console.Error);
        string clientName = Environment.GetEnvironmentVariable("CODENAV_MCP_CLIENT_NAME") ??
                            "stdio-client";
        var proxy = new DaemonProxy(
            endpoint,
            indexDb,
            command.Rebuild,
            command.KeepAlive,
            clientName,
            command.DaemonIdle);
        return await proxy.RunAsync(shutdown.Token);
    }

    return await McpApplication.RunStandaloneAsync(
        workspaceRoot, indexDb, command.Rebuild, shutdown.Token);
}
finally
{
    terminate?.Dispose();
    Console.CancelKeyPress -= cancelHandler;
    AppDomain.CurrentDomain.ProcessExit -= processExit;
}

internal static class DaemonRuntimeDiagnostics
{
    internal const string DiscoveryFallbackWarning =
        "Phoenix warning: stable /tmp daemon discovery authority is unavailable; using an environment-specific runtime fallback, so clients with different XDG_RUNTIME_DIR or TMPDIR values may not converge.";

    internal static void WriteDiscoveryWarning(DaemonEndpoint endpoint, TextWriter error)
    {
        if (!endpoint.IsStableRuntime) error.WriteLine(DiscoveryFallbackWarning);
    }
}

internal enum McpLaunchMode
{
    Standalone,
    SharedProxy,
    DaemonBootstrap,
    Daemon,
    DaemonRetireAuthorized,
}

internal sealed record McpCommandLine(
    McpLaunchMode Mode,
    string? WorkspaceRoot,
    string? IndexDb,
    bool Rebuild,
    bool KeepAlive,
    TimeSpan? DaemonIdle,
    bool Help)
{
    internal static McpCommandLine Parse(string[] arguments)
    {
        bool shared = false;
        bool daemonBootstrap = false;
        bool daemon = false;
        bool daemonRetireAuthorized = false;
        bool standalone = false;
        bool rebuild = false;
        bool keepAlive = false;
        bool help = false;
        string? workspaceRoot = null;
        string? indexDb = null;
        TimeSpan? idle = null;

        for (int i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--workspace-root" or "-w":
                    workspaceRoot = RequiredValue(arguments, ref i);
                    break;
                case "--index-db":
                    indexDb = RequiredValue(arguments, ref i);
                    break;
                case "--rebuild": rebuild = true; break;
                case "--shared-daemon": shared = true; break;
                case "--standalone": standalone = true; break;
                case "--daemon-bootstrap": daemonBootstrap = true; break;
                case "--daemon": daemon = true; break;
                // Internal integration-harness control path. It uses the authenticated daemon
                // handshake and is intentionally omitted from public usage/help output.
                case "--daemon-retire-authorized": daemonRetireAuthorized = true; break;
                case "--keepalive": keepAlive = true; break;
                // v0.12.59 compatibility alias. Shared mode is now unconditional and never
                // automatically falls back to a standalone process.
                case "--daemon-fallback-standalone": break;
                case "--daemon-idle-ms":
                    string raw = RequiredValue(arguments, ref i);
                    if (!long.TryParse(raw, out long milliseconds) ||
                        milliseconds is < 100 or > 86_400_000)
                        throw new ArgumentException("--daemon-idle-ms must be between 100 and 86400000.");
                    idle = TimeSpan.FromMilliseconds(milliseconds);
                    break;
                case "--help" or "-h": help = true; break;
                default: throw new ArgumentException($"Unknown argument: {arguments[i]}");
            }
        }

        int modes = (shared ? 1 : 0) + (daemonBootstrap ? 1 : 0) +
                    (daemon ? 1 : 0) + (daemonRetireAuthorized ? 1 : 0) +
                    (standalone ? 1 : 0);
        if (modes > 1)
            throw new ArgumentException("Choose only one Phoenix launch mode.");
        McpLaunchMode mode = daemonRetireAuthorized
            ? McpLaunchMode.DaemonRetireAuthorized
            : daemonBootstrap
            ? McpLaunchMode.DaemonBootstrap
            : daemon
            ? McpLaunchMode.Daemon
            : standalone
                ? McpLaunchMode.Standalone
                : McpLaunchMode.SharedProxy;
        return new McpCommandLine(
            mode, workspaceRoot, indexDb, rebuild, keepAlive, idle, help);
    }

    internal static void WriteUsage() => Console.Error.WriteLine(
        "Usage: PhoenixCodeNav.Mcp --workspace-root <dir> [--index-db <path>] [--rebuild] " +
        "[--standalone] [--keepalive]");

    private static string RequiredValue(string[] arguments, ref int index)
    {
        if (++index >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index]))
            throw new ArgumentException($"Missing value for {arguments[index - 1]}.");
        return arguments[index];
    }

}
