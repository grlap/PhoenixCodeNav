using System.Xml.Linq;

namespace CodeNav.Tests;

public sealed class MacOsTestPolicyTests
{
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
