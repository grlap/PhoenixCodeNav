using System.Security.Cryptography;
using System.Text;

namespace CodeNav.Tests;

/// <summary>
/// Cross-process lease for the two test classes whose real compiler/index workloads were proven
/// to starve one another when solution projects run concurrently. A dedicated owner thread keeps
/// the named Mutex's thread affinity correct across async xUnit continuations; no production
/// scheduler or global test-runner concurrency setting is changed.
/// </summary>
internal sealed class ProcessHeavyTestIsolation : IDisposable
{
    // Reuse the external harness's approved 130-second process-exit ceiling. The longest
    // individual lifecycle test already has a 90-second watchdog, so an overlapping runner gets
    // meaningful scheduling headroom without turning a stalled lease into an unbounded hang.
    private static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(130);
    private readonly ManualResetEventSlim _release = new(false);
    private readonly TaskCompletionSource _acquired = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _ownerThread;
    private readonly string _mutexName;
    private readonly TimeSpan _acquisitionTimeout;
    private int _disposed;

    private ProcessHeavyTestIsolation(string mutexName, TimeSpan acquisitionTimeout)
    {
        _mutexName = mutexName;
        _acquisitionTimeout = acquisitionTimeout;
        _ownerThread = new Thread(OwnMutex)
        {
            IsBackground = true,
            Name = "Phoenix process-heavy test isolation",
        };
        _ownerThread.Start();
        try
        {
            _acquired.Task.GetAwaiter().GetResult();
        }
        catch
        {
            _ownerThread.Join();
            _release.Dispose();
            throw;
        }
    }

    internal static ProcessHeavyTestIsolation Acquire()
    {
        string root = FindRepositoryRoot();
        string identity = OperatingSystem.IsWindows()
            ? root.ToUpperInvariant()
            : root;
        string suffix = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return new ProcessHeavyTestIsolation(
            $"PhoenixCodeNav.ProcessHeavyTests.{suffix}", AcquisitionTimeout);
    }

    internal static ProcessHeavyTestIsolation AcquireForTest(
        string mutexName, TimeSpan acquisitionTimeout) =>
        new(mutexName, acquisitionTimeout);

    private void OwnMutex()
    {
        bool ownsMutex = false;
        try
        {
            using var mutex = new Mutex(initiallyOwned: false, _mutexName);
            try
            {
                if (!mutex.WaitOne(_acquisitionTimeout))
                {
                    throw new TimeoutException(
                        $"Timed out waiting for process-heavy test isolation lease '{_mutexName}' " +
                        $"after {_acquisitionTimeout.TotalSeconds:0} seconds.");
                }
                ownsMutex = true;
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            _acquired.TrySetResult();
            _release.Wait();
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
                ownsMutex = false;
            }
        }
        catch (Exception ex)
        {
            _acquired.TrySetException(ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _release.Set();
        _ownerThread.Join();
        _release.Dispose();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PhoenixCodeNav.sln")))
                return Path.TrimEndingDirectorySeparator(current.FullName);
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
