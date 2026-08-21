using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace CodeNav.Mcp.Daemon;

internal static class DaemonWindowsPipeAuthority
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;

    internal static void VerifyServerOwnedByCurrentUser(NamedPipeClientStream pipe)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) ||
            serverPid == 0)
            throw new IOException("Phoenix daemon pipe server identity is unavailable.");

        using SafeProcessHandle process = OpenProcess(
            ProcessQueryLimitedInformation, false, serverPid);
        if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out SafeAccessTokenHandle token))
            throw new IOException("Phoenix daemon pipe server owner could not be inspected.");
        using (token)
        {
            _ = GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out uint bytes);
            if (bytes == 0)
                throw new IOException("Phoenix daemon pipe server token is unavailable.");
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytes));
            try
            {
                if (!GetTokenInformation(token, TokenUser, buffer, bytes, out _))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                IntPtr sidPointer = Marshal.ReadIntPtr(buffer);
                var serverSid = new SecurityIdentifier(sidPointer);
                SecurityIdentifier? currentSid = WindowsIdentity.GetCurrent().User;
                if (currentSid is null || !currentSid.Equals(serverSid))
                    throw new IOException("Phoenix daemon pipe server belongs to another user.");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle process,
        uint desiredAccess,
        out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int tokenInformationClass,
        IntPtr tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);
}
