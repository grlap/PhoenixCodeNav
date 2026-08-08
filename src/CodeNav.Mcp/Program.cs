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
    if (command.Mode == McpLaunchMode.Daemon)
    {
        DaemonProcessIsolation.DetachStandardStreams();
        DaemonEndpoint endpoint = DaemonEndpoint.Create(workspaceRoot, indexDb);
        var daemon = new DaemonServer(
            endpoint,
            indexDb,
            command.Rebuild,
            command.KeepAlive,
            command.DaemonIdle);
        return await daemon.RunAsync(shutdown.Token);
    }

    if (command.Mode == McpLaunchMode.DaemonBootstrap)
    {
        DaemonEndpoint endpoint = DaemonEndpoint.Create(workspaceRoot, indexDb);
        using Process daemon = DaemonProcessIsolation.LaunchDaemonChild(
            endpoint,
            indexDb,
            command.Rebuild,
            command.KeepAlive,
            command.DaemonIdle);
        return 0;
    }

    if (command.Mode == McpLaunchMode.SharedProxy)
    {
        DaemonEndpoint endpoint;
        try
        {
            endpoint = DaemonEndpoint.Create(workspaceRoot, indexDb);
        }
        catch (Exception ex)
        {
            return await UnavailableMcpShim.RunAsync(new DaemonUnavailableFailure(
                "daemon_workspace_identity_unavailable",
                $"Phoenix could not prove the physical workspace identity ({ex.GetType().Name}).",
                "Verify workspace ownership and path safety, then reconnect.",
                Retryable: false), shutdown.Token);
        }
        string clientName = Environment.GetEnvironmentVariable("CODENAV_MCP_CLIENT_NAME") ??
                            "stdio-client";
        var proxy = new DaemonProxy(
            endpoint,
            indexDb,
            command.Rebuild,
            command.KeepAlive,
            command.StandaloneFallback,
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

internal enum McpLaunchMode
{
    Standalone,
    SharedProxy,
    DaemonBootstrap,
    Daemon,
}

internal sealed record McpCommandLine(
    McpLaunchMode Mode,
    string? WorkspaceRoot,
    string? IndexDb,
    bool Rebuild,
    bool KeepAlive,
    bool StandaloneFallback,
    TimeSpan? DaemonIdle,
    bool Help)
{
    internal static McpCommandLine Parse(string[] arguments)
    {
        bool sharedEnvironment = ReadBooleanEnvironment("CODENAV_SHARED_DAEMON");
        bool shared = false;
        bool daemonBootstrap = false;
        bool daemon = false;
        bool standalone = false;
        bool rebuild = false;
        bool keepAlive = false;
        bool fallback = ReadBooleanEnvironment("CODENAV_DAEMON_STANDALONE_FALLBACK");
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
                case "--keepalive": keepAlive = true; break;
                case "--daemon-fallback-standalone": fallback = true; break;
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
                    (daemon ? 1 : 0) + (standalone ? 1 : 0);
        if (modes > 1)
            throw new ArgumentException("Choose only one of --shared-daemon, --standalone, or --daemon.");
        McpLaunchMode mode = daemonBootstrap
            ? McpLaunchMode.DaemonBootstrap
            : daemon
            ? McpLaunchMode.Daemon
            : shared
                ? McpLaunchMode.SharedProxy
                : standalone
                    ? McpLaunchMode.Standalone
                    : sharedEnvironment
                        ? McpLaunchMode.SharedProxy
                        : McpLaunchMode.Standalone;
        return new McpCommandLine(
            mode, workspaceRoot, indexDb, rebuild, keepAlive, fallback, idle, help);
    }

    internal static void WriteUsage() => Console.Error.WriteLine(
        "Usage: PhoenixCodeNav.Mcp --workspace-root <dir> [--index-db <path>] [--rebuild] " +
        "[--shared-daemon | --standalone] [--keepalive] [--daemon-fallback-standalone]");

    private static string RequiredValue(string[] arguments, ref int index)
    {
        if (++index >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index]))
            throw new ArgumentException($"Missing value for {arguments[index - 1]}.");
        return arguments[index];
    }

    private static bool ReadBooleanEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is string value &&
        (value == "1" || bool.TryParse(value, out bool parsed) && parsed);
}
