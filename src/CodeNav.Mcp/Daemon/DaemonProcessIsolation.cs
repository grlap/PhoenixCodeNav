using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CodeNav.Mcp.Daemon;

internal static class DaemonProcessIsolation
{
    private const int StandardInputHandle = -10;
    private const int StandardOutputHandle = -11;
    private const int StandardErrorHandle = -12;
    private const uint HandleFlagInherit = 0x00000001;

    internal static Process Launch(
        DaemonEndpoint endpoint,
        string? indexDb,
        bool rebuild,
        bool keepAlive,
        TimeSpan? idleLingerForTest = null)
        => LaunchProcess(
            endpoint,
            indexDb,
            rebuild,
            keepAlive,
            idleLingerForTest,
            "--daemon-bootstrap");

    /// <summary>
    /// Starts the long-lived daemon from the short-lived bootstrap. Once this process returns,
    /// the daemon is no longer a descendant of an individual MCP stdio proxy, so a host that
    /// forcibly tears down one proxy process tree cannot tear down every other client's daemon.
    /// </summary>
    internal static Process LaunchDaemonChild(
        DaemonEndpoint endpoint,
        string? indexDb,
        bool rebuild,
        bool keepAlive,
        TimeSpan? idleLingerForTest = null)
        => LaunchProcess(
            endpoint,
            indexDb,
            rebuild,
            keepAlive,
            idleLingerForTest,
            "--daemon");

    /// <summary>
    /// On Windows, a launcher must keep its own stdout/stderr open while ensuring that its child
    /// cannot inherit those caller-owned pipes as unrelated extra handles. The child receives
    /// dedicated redirected startup streams from <see cref="LaunchProcess"/>.
    /// </summary>
    private static void PreventParentStreamInheritance()
    {
        // On Unix, redirected stdout/stderr replace the child's descriptors 1 and 2 before exec,
        // so the caller-owned descriptors do not require a separate inheritance mutation.
        if (!OperatingSystem.IsWindows()) return;

        foreach (int kind in new[]
                 {
                     StandardOutputHandle, StandardErrorHandle,
                 })
        {
            IntPtr handle = GetStdHandle(kind);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) continue;
            if (!SetHandleInformation(handle, HandleFlagInherit, 0))
                throw new IOException(
                    "Phoenix bootstrap could not isolate an inherited standard stream.");
        }
    }

    private static Process LaunchProcess(
        DaemonEndpoint endpoint,
        string? indexDb,
        bool rebuild,
        bool keepAlive,
        TimeSpan? idleLingerForTest,
        string mode)
    {
        PreventParentStreamInheritance();
        (string executable, string? managedEntry) = CurrentLaunchAuthority();
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = endpoint.WorkspaceRoot,
            // Private daemon-startup IPC. This stream is consumed only by the parent Phoenix
            // process and is detached immediately after the one startup report.
            RedirectStandardOutput = true,
            // Never let a long-lived daemon retain a one-shot CLI/MCP caller's captured stderr
            // handle. The child detaches its own standard streams, but Console's managed writer
            // may otherwise keep the inherited OS pipe alive long enough to prevent caller EOF.
            RedirectStandardError = true,
        };
        if (managedEntry is not null) start.ArgumentList.Add(managedEntry);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add("--workspace-root");
        start.ArgumentList.Add(endpoint.WorkspaceRoot);
        if (!string.IsNullOrWhiteSpace(indexDb))
        {
            start.ArgumentList.Add("--index-db");
            start.ArgumentList.Add(indexDb);
        }
        if (rebuild) start.ArgumentList.Add("--rebuild");
        if (keepAlive) start.ArgumentList.Add("--keepalive");
        if (idleLingerForTest is { } linger)
        {
            start.ArgumentList.Add("--daemon-idle-ms");
            start.ArgumentList.Add(((long)linger.TotalMilliseconds).ToString());
        }

        Process process = Process.Start(start) ??
            throw new IOException("Phoenix daemon process could not be started.");
        _ = DrainStandardErrorAsync(process);
        return process;
    }

    private static async Task DrainStandardErrorAsync(Process process)
    {
        try
        {
            await process.StandardError.BaseStream
                .CopyToAsync(Console.OpenStandardError())
                .ConfigureAwait(false);
        }
        catch
        {
            // Diagnostics are best effort. Startup authority and cleanup remain on stdout and the
            // process handle, so a closed caller stderr must never replace the real startup result.
        }
    }

    internal static void DetachStandardStreams(bool preserveStandardOutput = false)
    {
        if (OperatingSystem.IsWindows()) DetachWindows(preserveStandardOutput);
        else DetachUnix(preserveStandardOutput);
        Console.SetIn(TextReader.Null);
        if (!preserveStandardOutput) Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
    }

    internal static void DetachStandardOutput()
    {
        if (OperatingSystem.IsWindows()) ReplaceWindowsHandle(StandardOutputHandle);
        else ReplaceUnixDescriptor(1);
        Console.SetOut(TextWriter.Null);
    }

    internal static async Task TerminateFailedStartupAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or
                                   NotSupportedException or Win32Exception)
        {
            // Best effort: the process may already have exited or the platform may have
            // rejected tree termination. The bounded exit observation below still runs.
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(DaemonProtocol.HandshakeTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or
                                   TimeoutException or OperationCanceledException)
        {
            // Cleanup must stay bounded and must not replace the original startup failure.
        }
    }

    private static (string Executable, string? ManagedEntry) CurrentLaunchAuthority()
    {
        string? process = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(process) || !File.Exists(process))
            throw new IOException("Phoenix executable launch authority is unavailable.");
        process = Path.GetFullPath(process);

        string? entry = Assembly.GetEntryAssembly()?.Location;
        bool dotnetHost = string.Equals(
            Path.GetFileNameWithoutExtension(process), "dotnet",
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
        if (!dotnetHost) return (process, null);
        if (string.IsNullOrWhiteSpace(entry) || !File.Exists(entry))
            throw new IOException("Phoenix managed entry assembly is unavailable.");
        return (process, Path.GetFullPath(entry));
    }

    private static void DetachUnix(bool preserveStandardOutput)
    {
        int pid = getpid();
        if (getsid(0) != pid && setsid() < 0)
            throw new IOException("Phoenix daemon could not create an independent Unix session.");

        int nullFd = open("/dev/null", 2);
        if (nullFd < 0) throw new IOException("Phoenix daemon could not detach standard streams.");
        try
        {
            if (dup2(nullFd, 0) < 0 ||
                !preserveStandardOutput && dup2(nullFd, 1) < 0 ||
                dup2(nullFd, 2) < 0)
                throw new IOException("Phoenix daemon could not detach standard streams.");
        }
        finally
        {
            if (nullFd > 2) close(nullFd);
        }
    }

    private static void ReplaceUnixDescriptor(int descriptor)
    {
        int nullFd = open("/dev/null", 2);
        if (nullFd < 0)
            throw new IOException("Phoenix daemon could not detach a standard stream.");
        try
        {
            if (dup2(nullFd, descriptor) < 0)
                throw new IOException("Phoenix daemon could not detach a standard stream.");
        }
        finally
        {
            if (nullFd > 2) close(nullFd);
        }
    }

    private static void DetachWindows(bool preserveStandardOutput)
    {
        ReplaceWindowsHandle(StandardInputHandle);
        if (!preserveStandardOutput) ReplaceWindowsHandle(StandardOutputHandle);
        ReplaceWindowsHandle(StandardErrorHandle);
    }

    private static void ReplaceWindowsHandle(int kind)
    {
        IntPtr nullHandle = CreateFileW(
            "NUL", 0x80000000 | 0x40000000,
            0x00000001 | 0x00000002, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (nullHandle == IntPtr.Zero || nullHandle == new IntPtr(-1))
            throw new IOException("Phoenix daemon could not detach a standard stream.");
        IntPtr previous = GetStdHandle(kind);
        if (!SetStdHandle(kind, nullHandle))
        {
            CloseHandle(nullHandle);
            throw new IOException("Phoenix daemon could not replace a standard stream.");
        }
        if (previous != IntPtr.Zero && previous != new IntPtr(-1) && previous != nullHandle)
            CloseHandle(previous);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    [DllImport("libc", SetLastError = true)]
    private static extern int getsid(int processId);

    [DllImport("libc")]
    private static extern int getpid();

    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldFileDescriptor, int newFileDescriptor);

    [DllImport("libc")]
    private static extern int close(int fileDescriptor);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(
        IntPtr handle,
        uint mask,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int standardHandle, IntPtr handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
