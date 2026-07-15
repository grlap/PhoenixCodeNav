using System.Diagnostics;

namespace CodeNav.Tests;

public sealed class RoslynHarnessLifecycleTests
{
    [Fact]
    public async Task TeardownBoundsStderrAndKillsDescendantProcessTree()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = FindRepositoryRoot();
        string script = Path.Combine(root, "scripts", "test-roslyn-mcp.ps1");
        var start = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("-SelfTestProcessLifecycle");

        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        string output = (await stdout) + Environment.NewLine + (await stderr);
        Assert.True(process.ExitCode == 0,
            $"Roslyn harness lifecycle self-test exited {process.ExitCode}:{Environment.NewLine}{output}");
        Assert.Contains("Process lifecycle self-test passed", output, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "PhoenixCodeNav.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException(
            "Could not locate PhoenixCodeNav.sln from the test output directory.");
    }
}
