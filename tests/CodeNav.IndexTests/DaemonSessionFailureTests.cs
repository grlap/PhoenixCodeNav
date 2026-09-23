using System.Reflection;
using System.Collections.Concurrent;
using CodeNav.Core.Indexing;
using CodeNav.Mcp;
using CodeNav.Mcp.Daemon;
using Microsoft.Extensions.Logging;

namespace CodeNav.Tests;

public sealed class DaemonSessionFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpectedRunningSessionDisconnectOrCancellationStaysQuiet(bool cancel)
    {
        string root = Directory.CreateTempSubdirectory("pcn-session").FullName;
        try
        {
            var endpoint = DaemonEndpoint.Create(root, null);
            var admission = new DaemonRequestAdmission();
            using var host = McpApplication.BuildHost(root, null, stdio: false, admission);
            using var manager = new IndexManager(root);
            using var stop = new CancellationTokenSource();
            var daemon = new DaemonServer(endpoint, null, false, false);
            if (cancel) daemon.SessionRegisteredForTest = _ => stop.Cancel();
            await using var stream = await RequestStreamAsync(endpoint);
            stream.FailTransportRead = !cancel;
            var session = (Task)Invoke(daemon, "HandleConnectionAsync", stream, manager, admission,
                host.Services, stop.Token)!;
            var logger = new RecordingLogger(false);
            await ((Task)Invoke(daemon, "TrackSession", 7L, session, logger)!).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(session.IsCompletedSuccessfully);
            Assert.Empty(logger.Messages);
            Assert.Equal(0, (int)Field(daemon, "_activeClients")!);
            Assert.True(stream.Disposed);
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public async Task UnregisterFailureStillReleasesClientAndDisposesStream()
    {
        string root = Directory.CreateTempSubdirectory("pcn-session").FullName;
        try
        {
            var endpoint = DaemonEndpoint.Create(root, null);
            var admission = new DaemonRequestAdmission();
            using var host = McpApplication.BuildHost(root, null, stdio: false, admission);
            using var manager = new IndexManager(root);
            var daemon = new DaemonServer(endpoint, null, false, false);
            using var callback = new CallbackRegistration();
            daemon.SessionRegisteredForTest = server =>
            {
                object clients = Field(admission, "_clients")!;
                object?[] args = [server, null];
                Assert.True((bool)clients.GetType().GetMethod("TryGetValue")!.Invoke(clients, args)!);
                var lifetime = (CancellationTokenSource)args[1]!.GetType()
                    .GetProperty("Lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(args[1])!;
                callback.Registration = lifetime.Token.Register(() => throw new IOException("unregister failed"));
            };
            await using var stream = await RequestStreamAsync(endpoint);
            var task = (Task)Invoke(daemon, "HandleConnectionAsync", stream, manager, admission,
                host.Services, CancellationToken.None)!;
            var exception = await Record.ExceptionAsync(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsType<AggregateException>(exception);
            Assert.Contains("unregister failed", exception.Message);
            Assert.True(stream.Disposed);
            Assert.Equal(0, (int)Field(daemon, "_activeClients")!);
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionObserverReportsUnexpectedFaultsAndSurvivesBrokenLogger(bool brokenLogger)
    {
        string root = Directory.CreateTempSubdirectory("pcn-session").FullName;
        try
        {
            var daemon = new DaemonServer(DaemonEndpoint.Create(root, null), null, false, true);
            var logger = new RecordingLogger(brokenLogger);
            Task failed = Task.FromException(new IOException("setup fault, not a disconnect"));
            await (Task)Invoke(daemon, "TrackSession", 17L, failed, logger)!;
            Assert.Contains("17", Assert.Single(logger.Messages));
            Assert.IsType<AggregateException>(Assert.Single(logger.Exceptions));
            var sessions = (ConcurrentDictionary<long, Task>)Field(daemon, "_sessions")!;
            Assert.Empty(sessions);
            await (Task)Invoke(daemon, "TrackSession", 18L, Task.CompletedTask, logger)!;
            await (Task)Invoke(daemon, "TrackSession", 19L,
                Task.FromCanceled(new CancellationToken(true)), logger)!;
            Assert.Single(logger.Messages);
            Assert.Empty(sessions);
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public async Task PostHandshakeSetupFailureReleasesClientAndPermitsIdleRetirement()
    {
        string root = Directory.CreateTempSubdirectory("pcn-session").FullName;
        try
        {
            var endpoint = DaemonEndpoint.Create(root, null);
            var daemon = new DaemonServer(endpoint, null, false, false,
                idleLinger: TimeSpan.FromMilliseconds(1));
            using var manager = new IndexManager(root);
            await using var stream = await RequestStreamAsync(endpoint);
            var session = (Task)Invoke(daemon, "HandleConnectionAsync", stream, manager,
                new DaemonRequestAdmission(), new BrokenServices(), CancellationToken.None)!;
            await Assert.ThrowsAsync<IOException>(() => session.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(stream.Response.Length > 0, "setup injection must follow a successful handshake");
            Assert.True(stream.Disposed);
            Assert.Equal(0, (int)Field(daemon, "_activeClients")!);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var idle = (Task)Invoke(daemon, "MonitorIdleAsync", stop.Token)!;
            await idle.WaitAsync(stop.Token);
            Assert.True(((CancellationTokenSource)Field(daemon, "_retire")!).IsCancellationRequested);
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    private sealed class BrokenServices : IServiceProvider
    {
        public object? GetService(Type type) => throw new IOException("post-handshake setup failed");
    }

    private sealed class CallbackRegistration : IDisposable
    {
        internal CancellationTokenRegistration Registration;
        public void Dispose() => Registration.Dispose();
    }

    private sealed class RecordingLogger(bool broken) : ILogger
    {
        internal List<string> Messages { get; } = [];
        internal List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
            if (broken) throw new IOException("logger failed");
        }
    }

    private static async Task<DuplexStream> RequestStreamAsync(DaemonEndpoint endpoint)
    {
        using var input = new MemoryStream();
        await DaemonProtocol.WriteRequestAsync(input, DaemonPreambleMode.Connect,
            DaemonProtocol.CreateRequest(endpoint, "session-failure-test"), CancellationToken.None);
        return new DuplexStream(input.ToArray());
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Field(object value, string name) => value.GetType().GetField(name, Private)!.GetValue(value);
    private static object? Invoke(object value, string name, params object[] args) => value.GetType().GetMethod(name, Private)!.Invoke(value, args);

    private sealed class DuplexStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _input = new(bytes);
        internal MemoryStream Response { get; } = new();
        internal bool Disposed { get; private set; }
        internal bool FailTransportRead { get; set; }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (FailTransportRead && _input.Position == _input.Length)
                throw new IOException("peer disconnected after handshake");
            return _input.ReadAsync(buffer, cancellationToken);
        }
        public override void Write(byte[] buffer, int offset, int count) => Response.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => Response.WriteAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            if (disposing) _input.Dispose();
            base.Dispose(disposing);
        }
    }
}
