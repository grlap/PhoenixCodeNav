using System.Diagnostics;
using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

internal sealed record RedirectedProcessResult(int ExitCode, string Output, string Error);

internal static class TestProcessLifecycle
{
    internal static async Task<RedirectedProcessResult> WaitForExitAndDrainAsync(
        Process process,
        Task<string> output,
        Task<string> error,
        TimeSpan timeout,
        string displayName)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            // Process.WaitForExitAsync also waits for redirected streams to reach EOF. A
            // descendant can keep those pipe handles open after the direct process exits, so
            // observe process exit independently and spend the same deadline on pipe drainage.
            await WaitForExitSignalAsync(process, cts.Token);
            string[] captured = await Task.WhenAll(output, error).WaitAsync(cts.Token);
            return new RedirectedProcessResult(process.ExitCode, captured[0], captured[1]);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Exception? cleanupError = null;
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await WaitForExitSignalAsync(process, cleanupCts.Token);
            }
            catch (Exception ex)
            {
                cleanupError = ex;
            }

            // A descendant can retain inherited pipe handles after the direct process exits.
            // Closing our readers is the only bounded way to release those reads once the parent
            // can no longer be traversed by Process.Kill(entireProcessTree: true).
            // StreamReader.Close can wait behind its outstanding async read. Close the pipe
            // streams themselves so the reads fault/complete without making cleanup unbounded.
            try { process.StandardOutput.BaseStream.Dispose(); }
            catch (Exception ex) { cleanupError ??= ex; }
            try { process.StandardError.BaseStream.Dispose(); }
            catch (Exception ex) { cleanupError ??= ex; }
            string[] captured = await ObserveReadersAsync(output, error);
            throw new TimeoutException(
                $"{displayName} exceeded {timeout}.\n{captured[0]}\n{captured[1]}",
                cleanupError);
        }
    }

    internal static async Task<bool> StopProcessAsync(int? processId)
    {
        if (processId is not int pid)
            return true;

        try
        {
            using Process process = Process.GetProcessById(pid);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task WaitForExitSignalAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        if (process.HasExited) return;
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnExited(object? _, EventArgs __) => completion.TrySetResult();
        process.Exited += OnExited;
        try
        {
            process.EnableRaisingEvents = true;
            if (process.HasExited) return;
            await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            process.Exited -= OnExited;
        }
    }

    private static async Task<string[]> ObserveReadersAsync(params Task<string>[] readers)
    {
        Task<string[]> all = Task.WhenAll(readers);
        Task completed = await Task.WhenAny(all, Task.Delay(TimeSpan.FromMilliseconds(250)));
        if (completed == all)
        {
            try { return await all; }
            catch { }
        }

        _ = all.ContinueWith(static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return readers.Select(static reader => reader.Status == TaskStatus.RanToCompletion
                ? reader.Result
                : "<redirected output unavailable after deadline>")
            .ToArray();
    }
}

internal static class StrictWorkspaceCleanup
{
    internal static void AssertLeaseReleased(string workspaceRoot, string? database = null)
    {
        string db = database ?? IndexBuilder.DefaultDbPath(workspaceRoot);
        Assert.False(
            IndexOwnershipLease.IsHeld(workspaceRoot, db),
            $"workspace ownership lease is still held: {workspaceRoot}");
    }

    internal static void DeleteAfterSuccess(bool success, params string[] roots)
        => DeleteAfterSuccess(success, assertDefaultLease: true, roots);

    private static void DeleteAfterSuccess(
        bool success,
        bool assertDefaultLease,
        params string[] roots)
    {
        Exception? firstFailure = null;
        foreach (string root in roots)
        {
            if (!success)
            {
                TestWorkspaceCleanup.DeleteWorkspace(root);
                continue;
            }

            try
            {
                if (assertDefaultLease)
                    AssertLeaseReleased(root);
                TestWorkspaceCleanup.ClearIndexPools(root);
                TestWorkspaceCleanup.DeleteWorkspaceStrict(root);
                Assert.False(Directory.Exists(root), $"workspace still exists after cleanup: {root}");
            }
            catch (Exception ex)
            {
                TestWorkspaceCleanup.DeleteWorkspace(root);
                firstFailure ??= ex;
            }
        }

        if (firstFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstFailure).Throw();
    }
}

internal static class ExternalProcessWorkspaceCleanup
{
    internal static void DeleteAfterSuccess(bool success, params string[] roots)
        => DeleteAfterSuccess(success, assertDefaultLease: true, roots);

    internal static void DeleteAfterSuccessWithoutLease(bool success, params string[] roots)
        => DeleteAfterSuccess(success, assertDefaultLease: false, roots);

    private static void DeleteAfterSuccess(
        bool success,
        bool assertDefaultLease,
        params string[] roots)
    {
        Exception? firstFailure = null;
        foreach (string root in roots)
        {
            try
            {
                if (success && assertDefaultLease)
                    StrictWorkspaceCleanup.AssertLeaseReleased(root);
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
            }
            finally
            {
                TestWorkspaceCleanup.DeleteWorkspace(root);
            }

            if (success && Directory.Exists(root))
            {
                firstFailure ??= new IOException(
                    $"workspace still exists after tolerant cleanup: {root}");
            }
        }

        if (firstFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstFailure).Throw();
    }
}
