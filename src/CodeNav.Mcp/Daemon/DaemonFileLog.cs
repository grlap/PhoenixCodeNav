using System.Diagnostics;
using System.Globalization;
using System.Text;
using CodeNav.Core.Indexing;
using Microsoft.Extensions.Logging;

namespace CodeNav.Mcp.Daemon;

/// <summary>Process-owned daemon diagnostics, independent of detached standard streams and host disposal.</summary>
internal sealed class DaemonFileLog : IDisposable
{
    private readonly object _gate = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private StreamWriter? _writer;
    private bool _disposed;
    private int _failureReported;
    private long _dropped;
    internal string? FilePath { get; private set; }
    internal long Dropped => Interlocked.Read(ref _dropped);

    internal static DaemonFileLog Start(string workspaceRoot, string? indexDb)
    {
        var log = new DaemonFileLog();
        try
        {
            string directory = workspaceRoot;
            foreach (string part in new[] { ".codenav", "logs" })
            {
                directory = Path.Combine(directory, part);
                if (Path.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Daemon log directory is a link.");
                Directory.CreateDirectory(directory);
            }
            log.FilePath = Path.Combine(directory,
                $"phoenix-{Environment.ProcessId}-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}.log");
            log._writer = new StreamWriter(new FileStream(log.FilePath, FileMode.CreateNew,
                FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            log.Write(LogLevel.Information, "PhoenixCodeNav.Daemon", () =>
                $"daemon_start build={BuildInfo.Stamp} pid={Environment.ProcessId} workspace={workspaceRoot} indexDb={indexDb ?? IndexBuilder.DefaultDbPath(workspaceRoot)} mode=daemon");
            if (log._writer is not null)
            {
                Stderr($"Phoenix daemon log: {log.FilePath}");
                log.Prune(directory, DateTime.UtcNow);
            }
        }
        catch (Exception ex) { log.Disable(ex); }
        AppDomain.CurrentDomain.UnhandledException += log.OnUnhandledException;
        TaskScheduler.UnobservedTaskException += log.OnUnobservedTaskException;
        return log;
    }

    // Host lifetime must not close the process sink before Program logs caught failures.
    internal ILoggerProvider CreateProvider() => new Provider(this);

    internal static bool Accepts(string category, LogLevel level) =>
        level != LogLevel.None && level >=
        (category.StartsWith("CodeNav.", StringComparison.Ordinal) ||
         category.StartsWith("PhoenixCodeNav.", StringComparison.Ordinal)
            ? LogLevel.Information : LogLevel.Warning);

    internal void Failure(string context, Exception exception) =>
        Write(LogLevel.Error, "PhoenixCodeNav.Daemon", () => context, exception);

    internal void ReportRunFailure(Exception exception, bool cancellationRequested)
    {
        if (cancellationRequested && IsCancellationOnly(exception)) return;
        Failure("daemon_run_failed", exception);
    }

    private static bool IsCancellationOnly(Exception exception) => exception switch
    {
        OperationCanceledException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 &&
            aggregate.InnerExceptions.All(IsCancellationOnly),
        _ => false,
    };

    internal void Shutdown(string state, string reason) =>
        Write(LogLevel.Information, "PhoenixCodeNav.Daemon", () =>
            $"daemon_stop state={state} reason={reason} uptimeMs={_uptime.ElapsedMilliseconds} dropped={Dropped}");

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args) =>
        Write(LogLevel.Critical, "PhoenixCodeNav.Daemon", () =>
            $"unhandled_exception terminating={args.IsTerminating} {args.ExceptionObject}", durable: true);

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        try { Failure("unobserved_task_exception", args.Exception); }
        finally { args.SetObserved(); }
    }

    internal void Write(LogLevel level, string category, Func<string> message,
        Exception? exception = null, bool durable = false)
    {
        if (!Accepts(category, level)) return;
        string record;
        try
        {
            // User formatters and Exception.ToString are outside the writer lock and guarded.
            record = $"{DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)} [{level}] {category}: {message()}";
            if (exception is not null) record += Environment.NewLine + exception;
        }
        catch
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        lock (_gate)
        {
            if (_writer is null || _disposed)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            try
            {
                _writer.WriteLine(record);
                _writer.Flush();
                if (durable && _writer.BaseStream is FileStream file) file.Flush(flushToDisk: true);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _dropped);
                Disable(ex);
            }
        }
    }

    private void Disable(Exception exception)
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        if (Interlocked.Exchange(ref _failureReported, 1) == 0)
            Stderr($"Phoenix daemon file logging disabled ({exception.GetType().Name}); continuing without a file log.");
    }

    private static void Stderr(string message)
    {
        try { Console.Error.WriteLine(message); } catch { }
    }

    private void Prune(string directory, DateTime nowUtc)
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, "phoenix-*.log"))
            {
                try
                {
                    if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                        continue;
                    if (nowUtc - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(14)) File.Delete(path);
                }
                catch (Exception ex) { Failure("daemon_log_prune_failed", ex); }
            }
        }
        catch (Exception ex) { Failure("daemon_log_prune_failed", ex); }
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _writer?.Dispose(); }
            catch (Exception ex) { Disable(ex); }
            _writer = null;
        }
    }

    private sealed class Provider(DaemonFileLog owner) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(owner, categoryName);
        public void Dispose() { } // Program, not the host, owns the log and exception hooks.
    }

    private sealed class Logger(DaemonFileLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => Accepts(category, logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Write(logLevel, category, () => formatter(state, exception), exception);
    }
}
