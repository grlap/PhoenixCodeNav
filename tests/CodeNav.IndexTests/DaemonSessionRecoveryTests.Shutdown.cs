using CodeNav.Mcp.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace CodeNav.Tests;

public sealed partial class DaemonSessionRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownObservesCancelledReconnectWithoutReplacingExitStatus(bool wrappedCancellation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var caller = new CancellationTokenSource();
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var transport = new StreamServerTransport(Stream.Null, Stream.Null, "recovery", NullLoggerFactory.Instance);
        await using var server = McpServer.Create(transport, new McpServerOptions(), NullLoggerFactory.Instance, services);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        await using var recovery = new DaemonSessionRecovery(Retryable, async token =>
        {
            Interlocked.Increment(ref attempts);
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException ex) when (token.IsCancellationRequested)
            {
                cancelled.TrySetResult();
                if (wrappedCancellation)
                    throw new DaemonEndpointUnavailableException("Controlled cancelled connect", ex);
                throw;
            }
            throw new InvalidOperationException("The reconnect must wait for shutdown.");
        }, CancellationToken.None);

        Task call = recovery.InvokeAsync(Request(server, "echo"), caller.Token).AsTask();
        await entered.Task.WaitAsync(timeout.Token);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(timeout.Token));
        Assert.False(cancelled.Task.IsCompleted); // Cancelling a waiter does not cancel the shared attempt.
        Assert.False(recovery.HasEstablishedConnection);
        int exit = recovery.HasEstablishedConnection ? 0 : 4;
        var error = await Record.ExceptionAsync(() => recovery.DisposeAsync().AsTask().WaitAsync(timeout.Token));
        Assert.True(cancelled.Task.IsCompletedSuccessfully);
        Assert.Equal(1, attempts);
        Assert.False(recovery.HasEstablishedConnection);
        Assert.Null(error);
        Assert.Equal(4, exit); // Local disposal contract; the actual shim is covered separately.
    }
}
