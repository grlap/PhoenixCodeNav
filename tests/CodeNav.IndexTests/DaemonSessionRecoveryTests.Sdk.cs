using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using CodeNav.Mcp.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit.Abstractions;

namespace CodeNav.Tests;

public sealed partial class DaemonSessionRecoveryTests
{
    private readonly ITestOutputHelper _output;

    public DaemonSessionRecoveryTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("idle-eof", false)]
    [InlineData("idle-dispose", false)]
    [InlineData("active-eof", false)]
    [InlineData("idle-eof", true)]
    [InlineData("idle-dispose", true)]
    [InlineData("active-eof", true)]
    [InlineData("unix-idle-eof", false)]
    [InlineData("unix-idle-dispose", false)]
    [InlineData("unix-active-eof", false)]
    public async Task DisconnectedSdkRequestAndNotificationExposeTransportLoss(string mode, bool cancelledCaller)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var peer = await SdkPeer.CreateAsync(mode.StartsWith("unix-", StringComparison.Ordinal), lifetime.Token);
        Stream peerStream = peer.Server;
        Stream clientStream = peer.Client;
        mode = mode.Replace("unix-", "", StringComparison.Ordinal);
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var transport = new StreamServerTransport(peerStream, peerStream, "sdk-loss", NullLoggerFactory.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = McpServer.Create(transport, new McpServerOptions
        {
            ServerInfo = new() { Name = "real-sdk-loss", Version = "1" },
            Handlers = new()
            {
                CallToolHandler = async (_, token) =>
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                    return new CallToolResult();
                },
            },
        }, NullLoggerFactory.Instance, services);
        Task serving = server.RunAsync(lifetime.Token);
        try
        {
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(
                clientStream, clientStream, NullLoggerFactory.Instance), cancellationToken: lifetime.Token);
            Assert.False(client.Completion.IsCompleted);
            Task<JsonRpcResponse>? active = null;
            if (mode == "active-eof")
            {
                active = client.SendRequestAsync(ToolRequest("active"), lifetime.Token);
                await entered.Task.WaitAsync(lifetime.Token);
            }
            if (mode == "idle-dispose") await server.DisposeAsync();
            await peerStream.DisposeAsync(); // Close the actual pipe endpoint, not an injected SDK exception.

            var completion = await client.Completion.WaitAsync(lifetime.Token);
            _output.WriteLine($"{mode}: Completion exception = {completion.Exception?.GetType().FullName ?? "none"}");
            if (active is not null)
            {
                var activeError = await Record.ExceptionAsync(() => active);
                _output.WriteLine($"active request: {activeError?.GetType().FullName ?? "none"}");
                Assert.IsAssignableFrom<IOException>(activeError);
            }
            var notificationError = await Record.ExceptionAsync(() => client.SendMessageAsync(new JsonRpcNotification
            {
                Method = "notifications/cancelled",
                Params = JsonSerializer.SerializeToNode(new CancelledNotificationParams { RequestId = new RequestId("active") }),
            }, lifetime.Token));
            var requestError = await Record.ExceptionAsync(() => client.SendRequestAsync(ToolRequest("after-close"), lifetime.Token));
            _output.WriteLine($"after completion: request = {requestError?.GetType().FullName ?? "none"}; notification = {notificationError?.GetType().FullName ?? "none"}");
            Assert.IsAssignableFrom<IOException>(requestError);
            Assert.IsAssignableFrom<IOException>(notificationError);

            int reconnects = 0;
            await using var recovery = new DaemonSessionRecovery(Retryable, _ =>
            {
                reconnects++;
                throw new DaemonProxyFailureException(Retryable);
            }, lifetime.Token);
            SetEstablishedClient(recovery, client, clientStream);
            if (cancelledCaller)
            {
                using var caller = new CancellationTokenSource();
                caller.Cancel();
                var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    recovery.InvokeAsync(Request(server, "mutate"), caller.Token).AsTask());
                Assert.Equal(caller.Token, cancelled.CancellationToken);
            }
            else
            {
                var lost = await recovery.InvokeAsync(Request(server, "mutate"), lifetime.Token);
                Assert.Equal("daemon_request_outcome_unknown", Cause(lost));
                Assert.False(lost.StructuredContent!.Value.GetProperty("retryable").GetBoolean());
            }
            Assert.False(recovery.HasEstablishedConnection);
            Assert.Equal(0, reconnects); // The uncertain request is never replayed.
            var next = await recovery.InvokeAsync(Request(server), lifetime.Token);
            Assert.Equal("daemon_handshake_timeout", Cause(next));
            Assert.Equal(1, reconnects); // A later independent call can attempt recovery.
        }
        finally
        {
            await lifetime.CancelAsync();
            await server.DisposeAsync();
            try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (IOException) when (lifetime.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { } // Owned endpoint deliberately closed above.
        }

        static JsonRpcRequest ToolRequest(string id) => new()
        {
            Id = new RequestId(id),
            Method = "tools/call",
            Params = JsonSerializer.SerializeToNode(new CallToolRequestParams { Name = "cancel" }),
        };
    }

    private sealed class SdkPeer(Stream client, Stream server, string? directory = null) : IAsyncDisposable
    {
        internal Stream Client { get; } = client;
        internal Stream Server { get; } = server;

        internal static async Task<SdkPeer> CreateAsync(bool unixSocket, CancellationToken token)
        {
            if (!unixSocket)
            {
                string name = "phoenix-sdk-loss-" + Guid.NewGuid().ToString("N");
                var pipeServer = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                var pipeClient = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await Task.WhenAll(pipeServer.WaitForConnectionAsync(token), pipeClient.ConnectAsync(token));
                    return new SdkPeer(pipeClient, pipeServer);
                }
                catch { pipeClient.Dispose(); pipeServer.Dispose(); throw; }
            }
            string root = Directory.CreateTempSubdirectory("psdk-").FullName;
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                var endpoint = new UnixDomainSocketEndPoint(Path.Combine(root, "s"));
                listener.Bind(endpoint);
                listener.Listen(1);
                Task<Socket> accepting = listener.AcceptAsync(token).AsTask();
                await socket.ConnectAsync(endpoint, token);
                Socket accepted = await accepting;
                return new SdkPeer(new NetworkStream(socket, ownsSocket: true), new NetworkStream(accepted, ownsSocket: true), root);
            }
            catch
            {
                socket.Dispose();
                File.Delete(Path.Combine(root, "s"));
                Directory.Delete(root);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await Client.DisposeAsync(); }
            finally
            {
                try { await Server.DisposeAsync(); }
                finally
                {
                    if (directory is not null)
                    {
                        File.Delete(Path.Combine(directory, "s"));
                        Directory.Delete(directory);
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrelatedInvalidOperationDoesNotInvalidateEstablishedConnection(bool forwardingCancellation)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var caller = new CancellationTokenSource();
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var transport = new StreamServerTransport(Stream.Null, Stream.Null, "policy", NullLoggerFactory.Instance);
        await using var server = McpServer.Create(transport, new McpServerOptions(), NullLoggerFactory.Instance, services);
        var failure = new InvalidOperationException("An unrelated operation failed");
        await using var client = new CancellationClient
        {
            RequestFailure = forwardingCancellation ? null : failure,
            ForwardCancellation = _ => Task.FromException(failure),
        };
        using var stream = new MemoryStream();
        await using var recovery = new DaemonSessionRecovery(Retryable,
            _ => throw new InvalidOperationException("Must not reconnect"), lifetime.Token);
        SetEstablishedClient(recovery, client, stream);
        var call = recovery.InvokeAsync(Request(server, forwardingCancellation ? "cancel" : "echo"), caller.Token).AsTask();
        if (forwardingCancellation)
        {
            await client.Entered.Task.WaitAsync(lifetime.Token);
            caller.Cancel();
        }
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => call));
        Assert.True(recovery.HasEstablishedConnection);
        Assert.True(stream.CanRead);
        Assert.Equal(0, client.Disposals);
    }
}
