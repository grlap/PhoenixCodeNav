using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace CodeNav.Tests;

public sealed class MacOsTestPolicyTests
{
    [Fact]
    public async Task TestProjectsImportRootBuildPolicyAndRetainPlatformRunSettings()
    {
        string root = FindRepositoryRoot();
        string project = Path.Combine(root, "tests", "CodeNav.Tests", "CodeNav.Tests.csproj");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("-nologo");
        start.ArgumentList.Add(
            "-getProperty:InvariantGlobalization,LangVersion,RunSettingsFilePath");

        using Process process = Process.Start(start)!;
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* exited between timeout and cleanup */ }
            await process.WaitForExitAsync();
            string timedOutStdout = await stdoutTask;
            string timedOutStderr = await stderrTask;
            Assert.Fail(
                $"MSBuild property evaluation timed out after 45 seconds." +
                $"{Environment.NewLine}stdout:{Environment.NewLine}{timedOutStdout}" +
                $"{Environment.NewLine}stderr:{Environment.NewLine}{timedOutStderr}");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        Assert.True(process.ExitCode == 0,
            $"MSBuild property evaluation exited {process.ExitCode}:" +
            $"{Environment.NewLine}stdout:{Environment.NewLine}{stdout}" +
            $"{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");

        using JsonDocument document = JsonDocument.Parse(stdout);
        JsonElement properties = document.RootElement.GetProperty("Properties");
        Assert.Equal("true", properties.GetProperty("InvariantGlobalization").GetString());
        Assert.Equal("latest", properties.GetProperty("LangVersion").GetString());
        string runSettings = properties.GetProperty("RunSettingsFilePath").GetString()!;
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(Path.Combine(root, "tests", "macos.runsettings"), runSettings);
        }
        else
        {
            Assert.Equal("", runSettings);
        }
    }

    [Fact]
    public void ProcessHeavyIsolationReportsHeldLeaseWithoutWaiting()
    {
        string mutexName = $"PhoenixCodeNav.ProcessHeavyTests.Contract.{Guid.NewGuid():N}";
        using var held = new Mutex(initiallyOwned: false, mutexName);
        Assert.True(held.WaitOne(TimeSpan.Zero));
        try
        {
            TimeoutException timeout = Assert.Throws<TimeoutException>(() =>
                ProcessHeavyTestIsolation.AcquireForTest(mutexName, TimeSpan.Zero));
            Assert.Contains("process-heavy test isolation lease", timeout.Message,
                StringComparison.Ordinal);
            Assert.Contains(mutexName, timeout.Message, StringComparison.Ordinal);
        }
        finally
        {
            held.ReleaseMutex();
        }
    }

    [Fact]
    public void MacOsPolicyUsesPhysicalTempPathAndKeepsExternalHarnessEnabled()
    {
        string root = FindRepositoryRoot();
        string propsPath = Path.Combine(root, "tests", "Directory.Build.props");
        string settingsPath = Path.Combine(root, "tests", "macos.runsettings");

        string props = File.ReadAllText(propsPath);
        Assert.Contains("$([MSBuild]::IsOSPlatform('OSX'))", props,
            StringComparison.Ordinal);
        Assert.Contains("macos.runsettings", props, StringComparison.Ordinal);

        XDocument settings = XDocument.Load(settingsPath);
        XElement runConfiguration = Assert.Single(
            settings.Descendants("RunConfiguration"));
        Assert.Equal("/private/tmp",
            Assert.Single(runConfiguration.Descendants("TMPDIR")).Value);

        string filter = Assert.Single(
            runConfiguration.Descendants("TestCaseFilter")).Value;
        Assert.Contains("FullyQualifiedName!~CodeNav.Tests.Batch42", filter,
            StringComparison.Ordinal);
        Assert.Contains("FullyQualifiedName!~CodeNav.Tests.Batch43", filter,
            StringComparison.Ordinal);
        Assert.Contains("FullyQualifiedName!~CodeNav.Tests.Batch44", filter,
            StringComparison.Ordinal);
        Assert.DoesNotContain("RoslynHarnessLifecycleTests", filter,
            StringComparison.Ordinal);
        Assert.DoesNotContain("FSharpSingleFilePublishTests", filter,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PhoenixCodeNav.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
