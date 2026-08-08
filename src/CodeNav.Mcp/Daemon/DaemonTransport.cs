using System.IO.Pipes;
using System.Net.Sockets;

namespace CodeNav.Mcp.Daemon;

internal sealed class DaemonEndpointUnavailableException : IOException
{
    internal DaemonEndpointUnavailableException(string message, Exception? inner = null)
        : base(message, inner) { }
}

internal sealed class DaemonAuthorityException : IOException
{
    internal DaemonAuthorityException(string message, Exception? inner = null)
        : base(message, inner) { }
}

internal interface IDaemonTransportListener : IAsyncDisposable
{
    ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken);
}

internal static class DaemonTransport
{
    internal static IDaemonTransportListener Listen(DaemonEndpoint endpoint)
    {
        EnsureRuntimeDirectory(endpoint);
        return OperatingSystem.IsWindows()
            ? new WindowsDaemonTransportListener(endpoint.PipeName)
            : new UnixDaemonTransportListener(endpoint.SocketPath!);
    }

    internal static async ValueTask<Stream> ConnectAsync(
        DaemonEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(
                ".",
                endpoint.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
                try { DaemonWindowsPipeAuthority.VerifyServerOwnedByCurrentUser(pipe); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new DaemonAuthorityException(
                        "Phoenix daemon pipe server authority could not be verified.", ex);
                }
                return pipe;
            }
            catch (Exception ex) when (ex is not DaemonAuthorityException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw new DaemonEndpointUnavailableException(
                    "Phoenix daemon named pipe is unavailable.", ex);
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        try { DaemonUnixFileAuthority.EnsureOwnerOnlyDirectory(endpoint.RuntimeDirectory); }
        catch (Exception ex)
        {
            throw new DaemonAuthorityException(
                "Phoenix daemon runtime directory authority could not be verified.", ex);
        }
        if (!DaemonUnixFileAuthority.TryLStat(endpoint.SocketPath!, out _))
            throw new DaemonEndpointUnavailableException(
                "Phoenix daemon Unix socket is unavailable.");
        try { DaemonUnixFileAuthority.VerifyOwnedSocket(endpoint.SocketPath!); }
        catch (Exception ex)
        {
            throw new DaemonAuthorityException(
                "Phoenix daemon Unix socket authority could not be verified.", ex);
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.SocketPath!),
                timeoutSource.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception ex)
        {
            socket.Dispose();
            throw new DaemonEndpointUnavailableException(
                "Phoenix daemon Unix socket is unavailable.", ex);
        }
    }

    internal static void EnsureRuntimeDirectory(DaemonEndpoint endpoint)
    {
        if (!OperatingSystem.IsWindows())
        {
            DaemonUnixFileAuthority.EnsureOwnerOnlyDirectory(endpoint.RuntimeDirectory);
            return;
        }

        Directory.CreateDirectory(endpoint.RuntimeDirectory);
        if (new DirectoryInfo(endpoint.RuntimeDirectory).LinkTarget is not null)
            throw new IOException("Phoenix daemon runtime directory cannot be a link.");
    }
}

internal sealed class WindowsDaemonTransportListener : IDaemonTransportListener
{
    private const int MaxInstances = 64;
    private readonly string _pipeName;

    internal WindowsDaemonTransportListener(string pipeName) => _pipeName = pipeName;

    public async ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly,
            64 * 1024,
            64 * 1024);
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class UnixDaemonTransportListener : IDaemonTransportListener
{
    private readonly string _socketPath;
    private readonly Socket _listener;
    private int _disposed;

    internal UnixDaemonTransportListener(string socketPath)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _socketPath = socketPath;
        if (!DaemonUnixFileAuthority.TryRemoveOwnedSocket(socketPath))
            throw new IOException("Phoenix daemon endpoint is occupied by an unsafe filesystem object.");

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            DaemonUnixFileAuthority.VerifyOwnedSocket(socketPath);
            _listener.Listen(64);
        }
        catch
        {
            _listener.Dispose();
            DaemonUnixFileAuthority.TryRemoveOwnedSocket(socketPath);
            throw;
        }
    }

    public async ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        Socket accepted = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
        return new NetworkStream(accepted, ownsSocket: true);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;
        _listener.Dispose();
        DaemonUnixFileAuthority.TryRemoveOwnedSocket(_socketPath);
        return ValueTask.CompletedTask;
    }
}
