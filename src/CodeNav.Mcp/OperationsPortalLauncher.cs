using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodeNav.Mcp;

internal interface IOperationsPortalLauncher
{
    Task<OperationsPortalLaunchResult> LaunchAsync(
        string workspaceRoot,
        CancellationToken cancellationToken);
}

internal sealed record OperationsPortalLaunchResult(
    bool Success,
    string? Status = null,
    string? Url = null,
    int? Pid = null,
    int? WorkspaceCount = null,
    string? Error = null,
    string? Detail = null,
    bool Retryable = false)
{
    internal static OperationsPortalLaunchResult Failed(
        string error,
        string detail,
        bool retryable) =>
        new(false, Error: error, Detail: detail, Retryable: retryable);
}

/// <summary>
/// Launches the separately packaged portal and consumes its private one-line handshake. Neither
/// child stdout nor stderr is inherited by the MCP stdio transport.
/// </summary>
internal sealed class OperationsPortalLauncher : IOperationsPortalLauncher
{
    internal static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(30);
    internal const int MaxHandshakeBytes = 16 * 1024;
    internal const int MaxErrorBytes = 2 * 1024;
    private readonly string _executablePath;
    private readonly TimeSpan _startupTimeout;

    internal OperationsPortalLauncher()
        : this(DefaultExecutablePath(), DefaultStartupTimeout)
    {
    }

    internal OperationsPortalLauncher(string executablePath, TimeSpan startupTimeout)
    {
        _executablePath = Path.GetFullPath(executablePath);
        _startupTimeout = startupTimeout > TimeSpan.Zero
            ? startupTimeout
            : throw new ArgumentOutOfRangeException(nameof(startupTimeout));
    }

    public async Task<OperationsPortalLaunchResult> LaunchAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_executablePath))
            {
                return OperationsPortalLaunchResult.Failed(
                    "portal_companion_missing",
                    "The packaged Phoenix Operations Portal companion was not found. Reinstall or republish PhoenixCodeNav with its portal directory.",
                    retryable: false);
            }
            if ((File.GetAttributes(_executablePath) & FileAttributes.ReparsePoint) != 0)
            {
                return OperationsPortalLaunchResult.Failed(
                    "portal_companion_invalid",
                    "The packaged Phoenix Operations Portal companion is a reparse point and was not launched.",
                    retryable: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationsPortalLaunchResult.Failed(
                "portal_companion_invalid",
                "The packaged Phoenix Operations Portal companion could not be validated.",
                retryable: false);
        }

        ProcessStartInfo start = CreateStartInfo(
            _executablePath,
            Path.GetFullPath(workspaceRoot));

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return OperationsPortalLaunchResult.Failed(
                "portal_start_failed",
                "The Phoenix Operations Portal companion could not be started.",
                retryable: true);
        }
        if (process is null)
        {
            return OperationsPortalLaunchResult.Failed(
                "portal_start_failed",
                "The Phoenix Operations Portal companion process could not be created.",
                retryable: true);
        }

        process.StandardInput.Close();

        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(_startupTimeout);
            Task<string> errorOutput = DrainBoundedAsync(
                process.StandardError.BaseStream,
                MaxErrorBytes,
                timeout.Token);
            string? line;
            try
            {
                line = await ReadBoundedLineAsync(
                    process.StandardOutput.BaseStream,
                    MaxHandshakeBytes,
                    timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await TerminateAttemptAsync(process).ConfigureAwait(false);
                await FinishErrorDrainAsync(process, errorOutput).ConfigureAwait(false);
                return OperationsPortalLaunchResult.Failed(
                    "portal_start_timeout",
                    $"The Phoenix Operations Portal did not become ready within {(int)_startupTimeout.TotalSeconds} seconds.",
                    retryable: true);
            }
            catch (OperationCanceledException)
            {
                await TerminateAttemptAsync(process).ConfigureAwait(false);
                await FinishErrorDrainAsync(process, errorOutput).ConfigureAwait(false);
                throw;
            }
            catch (InvalidDataException)
            {
                await TerminateAttemptAsync(process).ConfigureAwait(false);
                await FinishErrorDrainAsync(process, errorOutput).ConfigureAwait(false);
                return OperationsPortalLaunchResult.Failed(
                    "portal_protocol_error",
                    "The Phoenix Operations Portal returned an invalid startup handshake.",
                    retryable: true);
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                string detail = await EarlyExitDetailAsync(process).ConfigureAwait(false);
                await TerminateAttemptAsync(process).ConfigureAwait(false);
                await FinishErrorDrainAsync(process, errorOutput).ConfigureAwait(false);
                return OperationsPortalLaunchResult.Failed(
                    "portal_start_failed",
                    detail,
                    retryable: true);
            }

            OperationsPortalLaunchResult result = ParseHandshake(line, process.Id);
            if (!result.Success)
            {
                await TerminateAttemptAsync(process).ConfigureAwait(false);
                await FinishErrorDrainAsync(process, errorOutput).ConfigureAwait(false);
                return result;
            }

            timeout.Cancel();
            await FinishErrorDrainAsync(process, errorOutput).ConfigureAwait(false);
            return result;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executablePath,
        string workspaceRoot)
    {
        var start = new ProcessStartInfo(executablePath)
        {
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--launcher");
        start.ArgumentList.Add("--workspace-root");
        start.ArgumentList.Add(workspaceRoot);
        return start;
    }

    private static string DefaultExecutablePath()
    {
        string name = OperatingSystem.IsWindows()
            ? "PhoenixCodeNav.Portal.exe"
            : "PhoenixCodeNav.Portal";
        return Path.Combine(AppContext.BaseDirectory, "portal", name);
    }

    internal static async Task<string?> ReadBoundedLineAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var result = new MemoryStream(Math.Min(maxBytes, 512));
        var buffer = new byte[256];
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return result.Length == 0
                    ? null
                    : Encoding.UTF8.GetString(
                        result.GetBuffer(),
                        0,
                        checked((int)result.Length)).TrimEnd('\r');
            }

            int newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            int retained = newline >= 0 ? newline : read;
            if (result.Length + retained > maxBytes)
                throw new InvalidDataException("The portal startup handshake exceeded its bounded protocol size.");
            result.Write(buffer, 0, retained);
            if (newline >= 0)
            {
                return Encoding.UTF8.GetString(
                    result.GetBuffer(),
                    0,
                    checked((int)result.Length)).TrimEnd('\r');
            }
        }
    }

    internal static OperationsPortalLaunchResult ParseHandshake(
        string line,
        int launchedPid)
    {
        try
        {
            PortalProcessHandshake? handshake =
                JsonSerializer.Deserialize<PortalProcessHandshake>(line, Json.Options);
            ValidateHandshake(handshake, launchedPid);
            return new OperationsPortalLaunchResult(
                true,
                handshake!.Status,
                handshake.Url,
                handshake.Pid,
                handshake.WorkspaceCount);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or UriFormatException)
        {
            return OperationsPortalLaunchResult.Failed(
                "portal_protocol_error",
                "The Phoenix Operations Portal returned an invalid startup handshake.",
                retryable: true);
        }
    }

    private static void ValidateHandshake(PortalProcessHandshake? handshake, int launchedPid)
    {
        if (handshake is null
            || handshake.ProtocolVersion != 1
            || handshake.Status is not ("started" or "reused")
            || handshake.Pid <= 0
            || handshake.WorkspaceCount <= 0
            || !IsLaunchSessionId(handshake.LaunchSessionId)
            || !handshake.ReadOnly
            || (handshake.Status == "started" && handshake.Pid != launchedPid)
            || !Uri.TryCreate(handshake.Url, UriKind.Absolute, out Uri? url)
            || !string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !url.IsLoopback
            || !url.Fragment.StartsWith("#token=", StringComparison.Ordinal)
            || url.Fragment.Length <= "#token=".Length)
        {
            throw new InvalidOperationException("Required portal session fields were missing or invalid.");
        }
    }

    private static bool IsLaunchSessionId(string? value)
    {
        if (value is null || value.Length != 43)
            return false;

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character)
                && character is not ('-' or '_'))
            {
                return false;
            }
        }
        return true;
    }

    private static async Task<string> EarlyExitDetailAsync(
        Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            if (process.HasExited)
            {
                return $"The Phoenix Operations Portal exited before publishing its session (exit code {process.ExitCode}).";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return "The Phoenix Operations Portal did not publish a startup handshake.";
    }

    internal static async Task<string> DrainBoundedAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var retained = new MemoryStream(Math.Min(maxBytes, 512));
        var buffer = new byte[256];
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;
                int remaining = maxBytes - checked((int)retained.Length);
                if (remaining > 0)
                    retained.Write(buffer, 0, Math.Min(remaining, read));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        return Encoding.UTF8.GetString(
            retained.GetBuffer(),
            0,
            checked((int)retained.Length));
    }

    private static async Task FinishErrorDrainAsync(
        Process process,
        Task<string> errorOutput)
    {
        Task completed = await Task.WhenAny(
            errorOutput,
            Task.Delay(TimeSpan.FromMilliseconds(250))).ConfigureAwait(false);
        if (completed != errorOutput)
        {
            try { process.StandardError.BaseStream.Dispose(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            completed = await Task.WhenAny(
                errorOutput,
                Task.Delay(TimeSpan.FromMilliseconds(250))).ConfigureAwait(false);
        }

        if (completed == errorOutput)
        {
            try { _ = await errorOutput.ConfigureAwait(false); }
            catch { }
            return;
        }

        _ = errorOutput.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return;
    }

    private static async Task TerminateAttemptAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }

        try
        {
            if (!process.HasExited)
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
        }
    }

    private sealed record PortalProcessHandshake(
        int ProtocolVersion,
        string Status,
        string Url,
        int Pid,
        int WorkspaceCount,
        string LaunchSessionId,
        bool ReadOnly);
}
