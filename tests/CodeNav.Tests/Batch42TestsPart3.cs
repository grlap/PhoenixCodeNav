using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using static CodeNav.Tests.Batch42Support;

namespace CodeNav.Tests;

/// <summary>
/// Owns: slice 3 of 3 of the Batch 42 (v0.11.0) review_pack suite — a contiguous, duration-
/// balanced block of tests moved VERBATIM (xUnit parallelizes across classes but runs one class
/// serially; the original single class was the suite's ~98s critical path).
/// Deliberately does not own: the shared fixture/helpers (Batch42Support.cs) or sibling slices.
/// Split out of: Batch42Tests.cs (PhoenixCodeNav-6zdy).
/// </summary>
public class Batch42TestsPart3
{
    [Fact]
    public void ProjectOwnershipFallbackAppliesCompileOperationsInDocumentOrder()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-owner-operation-order").FullName);
        try
        {
            WriteReviewRepo(root);
            string owner = Path.Combine(root, "OrderedOwner");
            string shared = Path.Combine(root, "Shared");
            Directory.CreateDirectory(owner);
            Directory.CreateDirectory(shared);
            File.WriteAllText(Path.Combine(shared, "Shared.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(owner, "OrderedOwner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Include=\"../Shared/*.cs\" />" +
                "<Compile Remove=\"../Shared/Moved.cs\" /></ItemGroup></Project>");
            const string oldPath = "Shared/Type.cs";
            const string newPath = "Shared/Moved.cs";
            File.WriteAllText(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                "namespace OrderedOwner42; public class OrderedOwnerType42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m owner-operation-order-baseline");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.Move(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, newPath.Replace('/', Path.DirectorySeparatorChar)));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "OrderedOwnerType42");
            }, 20_000), "index did not reflect the ordered-membership move");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("changedFiles", out _), "review_pack with changedFiles");
            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == oldPath);
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData("Directory.Build.rsp")]
    [InlineData("MSBuild.rsp")]
    public void ResponseFileDeletionInvalidatesMoveOwnershipProof(string responseFile)
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-rsp-invalidation").FullName);
        try
        {
            WriteReviewRepo(root);
            File.WriteAllText(Path.Combine(root, responseFile),
                "-property:EnableDefaultCompileItems=true\n");
            Git(root, "add -A");
            Git(root, "commit -q -m response-file-baseline");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            const string oldPath = "Lib/Widget.cs";
            const string newPath = "Lib/MovedWidget.cs";
            File.Move(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, newPath.Replace('/', Path.DirectorySeparatorChar)));
            File.Delete(Path.Combine(root, responseFile));
            m.RequestRefresh(new[] { oldPath, newPath, responseFile });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "Widget");
            }, 20_000), "index did not reflect the response-file move");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("deletedFiles", out _), "review_pack with deletedFiles");
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == oldPath);
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData("src/App.csproj", true)]
    [InlineData("src/App.csproj.user", true)]
    [InlineData("shared/Shared.shproj", true)]
    [InlineData("build/Build.proj", true)]
    [InlineData("shared/Imports.projitems", true)]
    [InlineData("Phoenix.sln", true)]
    [InlineData("Phoenix.slnx", true)]
    [InlineData("Phoenix.slnf", true)]
    [InlineData("config/Directory.Build.props", true)]
    [InlineData("config/Directory.Build.targets", true)]
    [InlineData("config/Directory.Build.rsp", true)]
    [InlineData("config/MSBuild.rsp", true)]
    [InlineData("config/directory.build.RSP", true)]
    [InlineData("config/notes.rsp", false)]
    [InlineData("src/App.cs", false)]
    public void ProjectShapePathsRecognizeEveryBuildAndSolutionShape(string path,
        bool expected)
    {
        Assert.Equal(expected, NavigationTools.IsProjectShapePath(path));
    }

    [Fact]
    public void ReviewPackUsesProjectShapeClassifierForChangedProjectFiles()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-project-shapes").FullName);
        try
        {
            WriteReviewRepo(root);
            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            string[] paths =
            [
                "build/App.csproj.user",
                "build/Shared.shproj",
                "build/Build.proj",
                "build/Imports.projitems",
                "build/Directory.Build.rsp",
                "build/MSBuild.rsp",
                "Phoenix.slnx",
                "Phoenix.slnf",
                "Phoenix.sln",
                "build/Directory.Build.props",
                "build/Directory.Build.targets",
            ];

            JsonElement pack = SemanticRetry.ParseWithRetry( // n7ly sweep
                () => tools.ReviewPack(paths: string.Join(',', paths), maxBytes: 24576),
                j => j.TryGetProperty("changedProjectFiles", out _), "review_pack with changedProjectFiles");
            string[] actual = pack.GetProperty("changedProjectFiles").EnumerateArray()
                .Select(item => item.GetString()!).ToArray();
            Assert.Equal(paths.OrderBy(path => path, StringComparer.Ordinal), actual);
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.project_files_changed");
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData("BaseOutputPath")]
    [InlineData("BaseIntermediateOutputPath")]
    [InlineData("DefaultLanguageSourceExtension")]
    [InlineData("CustomBeforeMicrosoftCommonProps")]
    [InlineData("CustomAfterMicrosoftCommonTargets")]
    [InlineData("CustomBeforeMicrosoftCSharpTargets")]
    [InlineData("ImportByWildcardBeforeMicrosoftCommonProps")]
    [InlineData("ImportUserLocationsByWildcardAfterMicrosoftCommonTargets")]
    [InlineData("MSBuildExtensionsPath")]
    public void RawProjectShapeRejectsUnevaluatedSdkMembershipControls(string propertyName)
    {
        string xml = $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><{propertyName}>custom</{propertyName}></PropertyGroup></Project>";
        var shape = CodeNav.Core.Discovery.ProjectFileParser.ParseCompileShape("P/P.csproj",
            System.Text.Encoding.UTF8.GetBytes(xml));
        Assert.False(shape.CompileOwnershipComplete);
    }

    [Theory]
    [InlineData("Microsoft.NET.Sdk", true)]
    [InlineData("Microsoft.NET.Sdk/9.0.100", true)]
    [InlineData("Microsoft.NET.Sdk.Contoso", false)]
    [InlineData("Microsoft.NET.Sdk.Web", false)]
    public void RawProjectShapeAcceptsOnlyTheKnownStandardSdk(string sdk, bool expectedComplete)
    {
        string xml = $"<Project Sdk=\"{sdk}\" />";
        var shape = CodeNav.Core.Discovery.ProjectFileParser.ParseCompileShape("P/P.csproj",
            System.Text.Encoding.UTF8.GetBytes(xml));
        Assert.Equal(expectedComplete, shape.CompileOwnershipComplete);
    }

    [Fact]
    public void RawProjectShapeTreatsPackageBuildAssetsAsUnevaluated()
    {
        const string xml = "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
                           "<PackageReference Include=\"Build.Customizer\" Version=\"1.0.0\" />" +
                           "</ItemGroup></Project>";
        var shape = CodeNav.Core.Discovery.ProjectFileParser.ParseCompileShape("P/P.csproj",
            System.Text.Encoding.UTF8.GetBytes(xml));
        Assert.False(shape.CompileOwnershipComplete);
    }

    [Theory]
    [InlineData("true", true, true)]
    [InlineData("false", false, true)]
    [InlineData("0", false, false)]
    [InlineData("no", false, false)]
    [InlineData("", false, false)]
    public void RawProjectShapeRequiresABooleanDefaultCompileValue(string value,
        bool expectedDefaultItems, bool expectedComplete)
    {
        string xml = $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                     $"<EnableDefaultCompileItems>{value}</EnableDefaultCompileItems>" +
                     "</PropertyGroup></Project>";
        var shape = CodeNav.Core.Discovery.ProjectFileParser.ParseCompileShape("P/P.csproj",
            System.Text.Encoding.UTF8.GetBytes(xml));
        Assert.Equal(expectedDefaultItems, shape.DefaultCompileItems);
        Assert.Equal(expectedComplete, shape.CompileOwnershipComplete);
    }

    [Theory]
    [InlineData("<ItemGroup><Compile Include=\"../../Moved.cs\" /></ItemGroup>")]
    [InlineData("<ItemGroup><Compile Include=\"Old.cs\" Exclude=\"../../Moved.cs\" /></ItemGroup>")]
    [InlineData("<ItemGroup><Compile Remove=\"C:/outside/Moved.cs\" /></ItemGroup>")]
    [InlineData("<ProjectExtensions><Compile Include=\"Moved.cs\" /></ProjectExtensions>")]
    public void RawProjectShapeRejectsEscapingOrNonItemCompileSpecs(string projectXml)
    {
        string xml = "<Project Sdk=\"Microsoft.NET.Sdk\">" + projectXml + "</Project>";
        var shape = CodeNav.Core.Discovery.ProjectFileParser.ParseCompileShape("P/P.csproj",
            System.Text.Encoding.UTF8.GetBytes(xml));
        Assert.False(shape.CompileOwnershipComplete);
    }

    [Fact]
    public void MsBuildGlobCanUseCaseSensitiveProofSemantics()
    {
        Assert.True(CodeNav.Core.Discovery.MsBuildGlob.IsMatch(
            "Shared/Moved.cs", "shared/*.cs"));
        Assert.False(CodeNav.Core.Discovery.MsBuildGlob.IsMatch(
            "Shared/Moved.cs", "shared/*.cs", ignoreCase: false));
    }

    [Fact]
    public void ProjectOwnershipCanForceCaseSensitiveProofSemantics()
    {
        var project = new ProjectRow(1, "Owner/Owner.csproj", "Owner", "sdk", "net9.0",
            false, "parsed");
        var parsed = CodeNav.Core.Discovery.ProjectFileParser.ParseCompileShape(
            project.Path, System.Text.Encoding.UTF8.GetBytes(
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>" +
                "<ItemGroup><Compile Include=\"../owner/Moved.cs\" /></ItemGroup></Project>"));
        var shapes = new Dictionary<long, CodeNav.Core.Discovery.ParsedProject>
        {
            [project.Id] = parsed,
        };
        Assert.Empty(NavigationTools.LikelyOwningProjectIds("Owner/Moved.cs", [project], shapes,
            ignoreCaseOverride: false));
        Assert.Contains(project.Id, NavigationTools.LikelyOwningProjectIds("Owner/Moved.cs",
            [project], shapes, ignoreCaseOverride: true));
    }

    [Theory]
    [InlineData("<PropertyGroup><TargetFramework>net9.0</TargetFramework>" +
        "<EnableDefaultCompileItems>0</EnableDefaultCompileItems></PropertyGroup>" +
        "<ItemGroup><Compile Include=\"Old.cs\" /></ItemGroup>")]
    [InlineData("<PropertyGroup><TargetFramework>net9.0</TargetFramework>" +
        "<DefaultLanguageSourceExtension>.fs</DefaultLanguageSourceExtension></PropertyGroup>" +
        "<ItemGroup><Compile Include=\"Old.cs\" /></ItemGroup>")]
    [InlineData("<PropertyGroup><TargetFramework>net9.0</TargetFramework>" +
        "<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>" +
        "<ItemGroup><Compile Include=\"Old.cs\" /></ItemGroup>" +
        "<ProjectExtensions><Compile Include=\"Moved.cs\" /></ProjectExtensions>")]
    [InlineData("<PropertyGroup><TargetFramework>net9.0</TargetFramework>" +
        "<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>" +
        "<ItemGroup><Compile Include=\"Old.cs\" />" +
        "<Compile Include=\"../../Owner/Moved.cs\" /></ItemGroup>")]
    public void UnevaluatedProjectMembershipCannotPreserveAnExactMove(string ownerBody)
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-unevaluated-owner").FullName);
        try
        {
            WriteReviewRepo(root);
            string owner = Path.Combine(root, "Owner");
            string secondary = Path.Combine(root, "Secondary");
            Directory.CreateDirectory(owner);
            Directory.CreateDirectory(secondary);
            File.WriteAllText(Path.Combine(owner, "Owner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">" + ownerBody + "</Project>");
            File.WriteAllText(Path.Combine(secondary, "Secondary.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Include=\"../Owner/*.cs\" /></ItemGroup></Project>");
            const string oldPath = "Owner/Old.cs";
            const string newPath = "Owner/Moved.cs";
            File.WriteAllText(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                "namespace UnevaluatedOwner42; public class UnevaluatedOwnerType42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m unevaluated-owner-baseline");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.Move(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, newPath.Replace('/', Path.DirectorySeparatorChar)));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "UnevaluatedOwnerType42");
            }, 20_000), "index did not reflect the unevaluated-owner move");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("changedFiles", out _), "review_pack with changedFiles");
            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            Assert.True(pack.GetProperty("projectOwnershipFallbackCoverage")
                .GetProperty("evaluationIncomplete").GetBoolean());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void UnixProjectOwnershipProofUsesCaseSensitiveCompileSpecs()
    {
        if (OperatingSystem.IsWindows() || !GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-case-owner").FullName);
        try
        {
            WriteReviewRepo(root);
            Directory.CreateDirectory(Path.Combine(root, "Owner"));
            Directory.CreateDirectory(Path.Combine(root, "Secondary"));
            File.WriteAllText(Path.Combine(root, "Owner", "Owner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Include=\"Old.cs\" />" +
                "<Compile Include=\"../owner/Moved.cs\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Secondary", "Secondary.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Include=\"../Owner/*.cs\" /></ItemGroup></Project>");
            const string oldPath = "Owner/Old.cs";
            const string newPath = "Owner/Moved.cs";
            File.WriteAllText(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                "namespace CaseOwner42; public class CaseOwnerType42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m case-owner-baseline");
            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.Move(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, newPath.Replace('/', Path.DirectorySeparatorChar)));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null && q.ContentByPath(newPath) is not null;
            }, 20_000));
            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("changedFiles", out _), "review_pack with changedFiles");
            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData(".hidden/Moved.cs", "", true)]
    [InlineData(".hidden/Moved.cs",
        "<ItemGroup><Compile Include=\".hidden/Moved.cs\" /></ItemGroup>", false)]
    [InlineData("Moved.cs",
        "<ItemGroup><Compile Include=\"Moved.cs\" Exclude=\"Moved.cs\" /></ItemGroup>", false)]
    public void DefaultSdkMembershipHonorsExclusionsAndOrderedExplicitIncludes(
        string movedRelativePath, string projectItems, bool expectedDeletion)
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-sdk-default-membership").FullName);
        try
        {
            WriteReviewRepo(root);
            string projectDirectory = Path.Combine(root, "P");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "P.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework></PropertyGroup>" + projectItems +
                "</Project>");
            const string oldPath = "P/Old.cs";
            string newPath = "P/" + movedRelativePath;
            File.WriteAllText(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                "namespace SdkDefaults42; public class SdkDefaultType42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m sdk-default-membership-baseline");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            string destination = Path.Combine(root,
                newPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                destination);
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "SdkDefaultType42");
            }, 20_000), "index did not reflect the SDK-default membership move");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("changedFiles", out _), "review_pack with changedFiles");
            Assert.Equal(expectedDeletion ? 1 : 0,
                pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            if (expectedDeletion)
            {
                Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                    file.GetProperty("path").GetString() == oldPath);
            }
            else if (pack.TryGetProperty("deletedFiles", out JsonElement deletedFiles))
            {
                Assert.DoesNotContain(deletedFiles.EnumerateArray(), file =>
                    file.GetProperty("path").GetString() == oldPath);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ImportedProjectOwnershipCannotBecomeACompleteMovePreservationProof()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-imported-owner").FullName);
        try
        {
            WriteReviewRepo(root);
            string shared = Path.Combine(root, "Shared");
            string importedOwner = Path.Combine(root, "ImportedOwner");
            Directory.CreateDirectory(shared);
            Directory.CreateDirectory(importedOwner);
            File.WriteAllText(Path.Combine(shared, "Shared.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(shared, "Shared.projitems"),
                "<Project><ItemGroup><Compile Include=\"Type.cs\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(importedOwner, "ImportedOwner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><Import Project=\"../Shared/Shared.projitems\" /></Project>");
            const string oldPath = "Shared/Type.cs";
            const string newPath = "Shared/Moved.cs";
            File.WriteAllText(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                "namespace ImportedOwner42; public class ImportedOwnerType42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m imported-owner-baseline");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.Move(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, newPath.Replace('/', Path.DirectorySeparatorChar)));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "ImportedOwnerType42");
            }, 20_000), "index did not reflect the imported-owner move");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("changedFiles", out _), "review_pack with changedFiles");
            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == oldPath);
            Assert.True(pack.GetProperty("projectOwnershipFallbackCoverage")
                .GetProperty("evaluationIncomplete").GetBoolean());
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.project_shape_incomplete");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ExpressionBasedProjectOwnershipCannotBecomeACompleteMovePreservationProof()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-expression-owner").FullName);
        try
        {
            WriteReviewRepo(root);
            string shared = Path.Combine(root, "Shared");
            string expressionOwner = Path.Combine(root, "ExpressionOwner");
            Directory.CreateDirectory(shared);
            Directory.CreateDirectory(expressionOwner);
            File.WriteAllText(Path.Combine(shared, "Shared.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(expressionOwner, "ExpressionOwner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "<SharedRoot>../Shared</SharedRoot></PropertyGroup><ItemGroup>" +
                "<Compile Include=\"$(SharedRoot)/*.cs\" /></ItemGroup></Project>");
            const string oldPath = "Shared/Type.cs";
            const string newPath = "Shared/Moved.cs";
            File.WriteAllText(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                "namespace ExpressionOwner42; public class ExpressionOwnerType42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m expression-owner-baseline");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.Move(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, newPath.Replace('/', Path.DirectorySeparatorChar)));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "ExpressionOwnerType42");
            }, 20_000), "index did not reflect the expression-owner move");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("changedFiles", out _), "review_pack with changedFiles");
            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == oldPath);
            Assert.True(pack.GetProperty("projectOwnershipFallbackCoverage")
                .GetProperty("evaluationIncomplete").GetBoolean());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ConditionedProjectOwnershipCannotBecomeACompleteMovePreservationProof()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-conditioned-owner").FullName);
        try
        {
            WriteReviewRepo(root);
            string shared = Path.Combine(root, "Shared");
            string conditionalOwner = Path.Combine(root, "ConditionalOwner");
            Directory.CreateDirectory(shared);
            Directory.CreateDirectory(conditionalOwner);
            File.WriteAllText(Path.Combine(shared, "Shared.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(conditionalOwner, "ConditionalOwner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Include=\"../Shared/*.cs\" " +
                "Condition=\"Exists('../Shared/Type.cs')\" /></ItemGroup></Project>");
            const string oldPath = "Shared/Type.cs";
            const string newPath = "Shared/Moved.cs";
            File.WriteAllText(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                "namespace ConditionedOwner42; public class ConditionedOwnerType42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m conditioned-owner-baseline");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.Move(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, newPath.Replace('/', Path.DirectorySeparatorChar)));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "ConditionedOwnerType42");
            }, 20_000), "index did not reflect the conditioned-owner move");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("changedFiles", out _), "review_pack with changedFiles");
            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == oldPath);
            Assert.True(pack.GetProperty("projectOwnershipFallbackCoverage")
                .GetProperty("evaluationIncomplete").GetBoolean());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ProjectShapeFallbackRejectsAnOversizedProjectBeforeXmlParsing()
    {
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-project-shape-budget").FullName);
        try
        {
            File.WriteAllText(Path.Combine(root, "Huge.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><!--" +
                new string('x', (256 * 1024) + 1) + "--></Project>");
            var projects = new List<ProjectRow>
            {
                new(1, "Huge.csproj", "Huge", "sdk", "net9.0", false, "parsed"),
            };
            NavigationTools.ReviewProjectShapeSnapshot snapshot =
                NavigationTools.LoadProjectShapesBounded(root, projects);
            Assert.True(snapshot.BudgetHit);
            Assert.False(snapshot.EvaluationIncomplete);
            Assert.Equal(1, snapshot.Attempted);
            Assert.Empty(snapshot.Parsed);
            Assert.Equal(0, snapshot.BytesRead);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ProjectShapeFallbackSkipsXmlWorkWhenTheProjectUniverseIsAlreadyCapped()
    {
        var projects = new List<ProjectRow>
        {
            new(1, "Missing.csproj", "Missing", "sdk", "net9.0", false, "parsed"),
        };
        NavigationTools.ReviewProjectShapeSnapshot snapshot =
            NavigationTools.LoadProjectShapesBounded(Path.GetTempPath(), projects,
                projectCountLimited: true);
        Assert.True(snapshot.BudgetHit);
        Assert.False(snapshot.EvaluationIncomplete);
        Assert.Equal(2, snapshot.RequestedAtLeast);
        Assert.Equal(0, snapshot.Attempted);
        Assert.Empty(snapshot.Parsed);
    }

    [Fact]
    public void ProjectShapeFallbackTreatsImplicitDirectoryBuildFilesAsIncomplete()
    {
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-directory-build-shape").FullName);
        try
        {
            string projectDirectory = Path.Combine(root, "P");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "P.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "Directory.Build.props"),
                "<Project><ItemGroup><Compile Include=\"Shared/*.cs\" /></ItemGroup></Project>");
            var projects = new List<ProjectRow>
            {
                new(1, "P/P.csproj", "P", "sdk", "net9.0", false, "parsed"),
            };
            NavigationTools.ReviewProjectShapeSnapshot snapshot =
                NavigationTools.LoadProjectShapesBounded(root, projects);
            Assert.True(snapshot.EvaluationIncomplete);
            Assert.False(snapshot.BudgetHit);
            Assert.Equal(1, snapshot.Attempted);
            Assert.Empty(snapshot.Parsed);

            var customSdk = CodeNav.Core.Discovery.ProjectFileParser.ParseCompileShape(
                "P/Custom.csproj", System.Text.Encoding.UTF8.GetBytes(
                    "<Project Sdk=\"Contoso.Custom.Sdk\" />"));
            Assert.False(customSdk.CompileOwnershipComplete);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewPackPinsRowsAndMetadataToOneRefreshEpoch()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-review-epoch").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/ReviewEpoch.cs";
            string fullPath = Path.Combine(root,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(fullPath,
                "namespace ReviewEpoch42; public class BeforeRefreshEpoch42 { " +
                "public void BeforeRefreshMember42() { } }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m review-epoch-baseline");

            using var manager = StartManager(root);
            Assert.True(WaitUntil(() => manager.State == "ready", 20_000));
            IndexHealth before = manager.Health();
            var tools = new NavigationTools(manager, new SemanticService(manager));
            int refreshes = 0;
            manager.ReviewSnapshotAfterQueryForTest = sql =>
            {
                if (!sql.Contains("SELECT id, path, size, line_count, is_generated FROM files",
                        StringComparison.Ordinal) || Interlocked.Exchange(ref refreshes, 1) != 0)
                    return;

                File.WriteAllText(fullPath,
                    "namespace ReviewEpoch42; public class AfterRefreshEpoch42 { " +
                    "public void AfterRefreshMember42() { } }\n");
                long processedBefore = manager.Health().PendingProcessed;
                manager.RequestRefresh(new[] { relativePath });
                Assert.True(WaitUntil(() =>
                {
                    using var q = manager.OpenQueries();
                    return manager.State == "ready" &&
                           manager.Health().PendingProcessed > processedBefore &&
                           q.SearchSymbols("AfterRefreshEpoch42", "exact", null, 2).Count == 1;
                }, 20_000), "deterministic in-review refresh did not complete");
            };

            JsonElement pack;
            try
            {
                pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                    () => tools.ReviewPack(paths: relativePath, maxBytes: 24576),
                    j => j.TryGetProperty("symbols", out _), "review_pack with symbols");
            }
            finally
            {
                manager.ReviewSnapshotAfterQueryForTest = null;
            }

            Assert.Equal(1, Volatile.Read(ref refreshes));
            List<string?> names = pack.GetProperty("symbols").EnumerateArray()
                .Select(item => item.GetProperty("symbol").GetProperty("name").GetString())
                .ToList();
            Assert.Contains("BeforeRefreshEpoch42", names);
            Assert.DoesNotContain("AfterRefreshEpoch42", names);
            Assert.Equal(before.LastRefreshUtc,
                pack.GetProperty("meta").GetProperty("lastRefreshUtc").GetString());
            Assert.NotEqual(before.LastRefreshUtc, manager.Health().LastRefreshUtc);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void FullRebuildWaitsForPinnedReviewSnapshotToDrain()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-review-rebuild-gate").FullName);
        try
        {
            WriteReviewRepo(root);
            using var manager = StartManager(root);
            var tools = new NavigationTools(manager, new SemanticService(manager));
            using var waiting = new ManualResetEventSlim(false);
            using var boundary = new ManualResetEventSlim(false);
            using var completed = new ManualResetEventSlim(false);
            int activeAtBoundary = -1;
            manager.FullRebuildWaitingForReviewSnapshotsForTest = () => waiting.Set();
            manager.FullRebuildDestructiveBoundaryForTest = active =>
            {
                activeAtBoundary = active;
                boundary.Set();
            };
            manager.FullRebuildCompletedForTest = () => completed.Set();
            int rebuildRequests = 0;
            manager.ReviewSnapshotAfterQueryForTest = sql =>
            {
                if (!sql.Contains("SELECT id, path, size, line_count, is_generated FROM files",
                        StringComparison.Ordinal) ||
                    Interlocked.Exchange(ref rebuildRequests, 1) != 0)
                    return;
                manager.RequestFullRebuild();
                Assert.True(waiting.Wait(TimeSpan.FromSeconds(10)),
                    "full rebuild did not reach the active-review gate");
                Assert.False(completed.IsSet,
                    "full rebuild crossed its destructive boundary while a review snapshot was active");
                Assert.False(boundary.IsSet,
                    "full rebuild reached destructive work while a review snapshot was active");
            };

            JsonElement pack;
            try
            {
                pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                    () => tools.ReviewPack(paths: "Lib/Widget.cs", maxBytes: 24576),
                    j => j.TryGetProperty("symbols", out _), "review_pack with symbols");
            }
            finally
            {
                manager.ReviewSnapshotAfterQueryForTest = null;
            }
            Assert.Contains(pack.GetProperty("symbols").EnumerateArray(), item =>
                item.GetProperty("symbol").GetProperty("name").GetString() == "Widget");
            Assert.True(boundary.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, Volatile.Read(ref activeAtBoundary));
            Assert.True(completed.Wait(TimeSpan.FromSeconds(30)),
                "full rebuild did not resume after the review snapshot drained");
            // The rebuild epilogue deliberately queues a detect-all convergence sweep (edits made
            // during BuildOwned's pre-watcher interval); "refreshing" is a designed transient
            // right after the completed seam, so wait for the landing state instead of racing it.
            Assert.True(WaitUntil(() => manager.State == "ready", 20000),
                $"manager did not settle at 'ready' after the rebuild (state '{manager.State}')");
            manager.FullRebuildWaitingForReviewSnapshotsForTest = null;
            manager.FullRebuildDestructiveBoundaryForTest = null;
            manager.FullRebuildCompletedForTest = null;
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SeparateTypeHeaderAndMemberHunksRetainBothSidesReviewUnits()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-per-hunk-types").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/PerHunkType.cs";
            string fullPath = Path.Combine(root,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(fullPath,
                "namespace PerHunkMapping42;\n" +
                "public class OldPerHunkType42 : OldPerHunkBase42\n" +
                "{\n" +
                "    public void StableOne42() { }\n" +
                "\n\n\n\n" +
                "    public void OldPerHunkMember42() { int value = 1; }\n" +
                "}\n" +
                "public class OldPerHunkBase42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m per-hunk-baseline");

            using var manager = StartManager(root);
            var tools = new NavigationTools(manager, new SemanticService(manager));
            File.WriteAllText(fullPath,
                "namespace PerHunkMapping42;\n" +
                "public class NewPerHunkType42 : NewPerHunkBase42\n" +
                "{\n" +
                "    public void StableOne42() { }\n" +
                "\n\n\n\n" +
                "    public void NewPerHunkMember42() { int value = 2; }\n" +
                "}\n" +
                "public class OldPerHunkBase42 { }\n" +
                "public class NewPerHunkBase42 { }\n");
            RefreshAndWait(manager, relativePath, "NewPerHunkMember42");

            JsonElement pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("symbols", out _), "review_pack with symbols");
            List<string?> currentNames = pack.GetProperty("symbols").EnumerateArray()
                .Select(item => item.GetProperty("symbol").GetProperty("name").GetString())
                .ToList();
            Assert.Contains("NewPerHunkType42", currentNames);
            Assert.Contains("NewPerHunkMember42", currentNames);

            JsonElement formerFile = Assert.Single(pack.GetProperty("formerSymbols")
                .EnumerateArray(), file => file.GetProperty("path").GetString() == relativePath);
            List<string?> formerNames = formerFile.GetProperty("formerSymbols").EnumerateArray()
                .Select(symbol => symbol.GetProperty("name").GetString()).ToList();
            Assert.Contains("OldPerHunkType42", formerNames);
            Assert.Contains("OldPerHunkMember42", formerNames);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ProjectOwnershipGlobBudgetIsStickyAndFailsClosed()
    {
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-owner-glob-budget").FullName);
        try
        {
            File.WriteAllText(Path.Combine(root, "Owner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Include=\"**/*.cs\" />" +
                "</ItemGroup></Project>");
            var projects = new List<ProjectRow>
            {
                new(1, "Owner.csproj", "Owner", "sdk", "net9.0", false, "parsed"),
            };
            var tinyBudget = new CodeNav.Core.Discovery.GlobMatchBudget(32, 1,
                TimeSpan.FromSeconds(10));
            NavigationTools.ReviewProjectShapeSnapshot snapshot =
                NavigationTools.LoadProjectShapesBounded(root, projects,
                    matchBudgetOverride: tinyBudget);
            Assert.False(snapshot.BudgetHit);

            HashSet<long> owners = NavigationTools.LikelyOwningProjectIds("Nested/File.cs",
                snapshot.Projects, snapshot.Parsed, matchBudget: snapshot.MatchBudget,
                onMatchAttempt: snapshot.MarkGlobMatchAttempted);
            Assert.Empty(owners);
            Assert.True(snapshot.MatchBudget.IsExhausted);
            Assert.True(snapshot.BudgetHit);
            Assert.False(snapshot.Complete);
            Assert.Equal(1, snapshot.MatchBudget.OperationLimit);
            Assert.True(snapshot.MatchBudget.Operations <= snapshot.MatchBudget.OperationLimit);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ExpiredOwnershipBudgetCannotProveDefaultSdkOwnership()
    {
        CodeNav.Core.Discovery.ParsedProject parsed =
            CodeNav.Core.Discovery.ProjectFileParser.ParseCompileShape(
            "Default.csproj", System.Text.Encoding.UTF8.GetBytes(
                "<Project Sdk=\"Microsoft.NET.Sdk\" />"));
        Assert.True(parsed.DefaultCompileItems);
        Assert.Empty(parsed.CompileOperations ?? []);
        var projects = new List<ProjectRow>
        {
            new(1, "Default.csproj", "Default", "sdk", "net9.0", false, "parsed"),
        };
        var budget = new CodeNav.Core.Discovery.GlobMatchBudget(32, 1_000,
            TimeSpan.Zero);

        HashSet<long> owners = NavigationTools.LikelyOwningProjectIds("Default.cs",
            projects, new Dictionary<long, CodeNav.Core.Discovery.ParsedProject> { [1] = parsed },
            matchBudget: budget);

        Assert.Empty(owners);
        Assert.True(budget.IsExhausted);
    }

    [Fact]
    public void DefaultSdkOwnershipCannotEscapeAfterBudgetExpiresDuringFinalEvaluation()
    {
        CodeNav.Core.Discovery.ParsedProject parsed =
            CodeNav.Core.Discovery.ProjectFileParser.ParseCompileShape(
                "Default.csproj", System.Text.Encoding.UTF8.GetBytes(
                    "<Project Sdk=\"Microsoft.NET.Sdk\" />"));
        var projects = new List<ProjectRow>
        {
            new(1, "Default.csproj", "Default", "sdk", "net9.0", false, "parsed"),
        };
        var budget = new CodeNav.Core.Discovery.GlobMatchBudget(32, 32,
            Timeout.InfiniteTimeSpan);
        bool evaluationCompleted = false;

        HashSet<long> owners = NavigationTools.LikelyOwningProjectIds("Default.cs",
            projects, new Dictionary<long, CodeNav.Core.Discovery.ParsedProject> { [1] = parsed },
            matchBudget: budget,
            afterDefaultSdkEvaluationForTest: () =>
            {
                evaluationCompleted = true;
                Assert.Equal(CodeNav.Core.Discovery.GlobMatchOutcome.BudgetExhausted,
                    CodeNav.Core.Discovery.MsBuildGlob.Match(new string('x', 128), "*",
                        ignoreCase: false, budget));
            });

        Assert.True(evaluationCompleted);
        Assert.True(budget.IsExhausted);
        Assert.Empty(owners);
    }

    [Fact]
    public void ProjectOwnershipCacheIsInvalidatedAfterLaterGlobExhaustion()
    {
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-owner-cache-budget").FullName);
        try
        {
            File.WriteAllText(Path.Combine(root, "Owner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Include=\"**/*.cs\" />" +
                "</ItemGroup></Project>");
            var projects = new List<ProjectRow>
            {
                new(1, "Owner.csproj", "Owner", "sdk", "net9.0", false, "parsed"),
            };
            var budget = new CodeNav.Core.Discovery.GlobMatchBudget(32, 128,
                TimeSpan.FromSeconds(10));
            NavigationTools.ReviewProjectShapeSnapshot snapshot =
                NavigationTools.LoadProjectShapesBounded(root, projects,
                    matchBudgetOverride: budget);
            var resolver = new NavigationTools.ReviewProjectOwnershipResolver(() => snapshot);

            Assert.Contains(1, resolver.OwnerIds("A.cs"));
            Assert.Empty(resolver.OwnerIds(new string('a', 512) + ".cs"));
            Assert.True(snapshot.BudgetHit);
            Assert.Empty(resolver.OwnerIds("A.cs"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewPackDisclosesProjectOwnershipGlobExhaustion()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-owner-glob-coverage").FullName);
        try
        {
            WriteReviewRepo(root);
            string ownerDirectory = Path.Combine(root, "GlobOwner");
            Directory.CreateDirectory(ownerDirectory);
            File.WriteAllText(Path.Combine(ownerDirectory, "GlobOwner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework>" +
                "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Include=\"**/*.cs\" />" +
                "</ItemGroup></Project>");
            const string relativePath = "GlobOwner/Deleted.cs";
            File.WriteAllText(Path.Combine(ownerDirectory, "Deleted.cs"),
                "namespace GlobOwner42; public class DeletedByGlobBudget42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m owner-glob-coverage-baseline");

            using var manager = StartManager(root);
            var tools = new NavigationTools(manager, new SemanticService(manager));
            tools.ReviewProjectGlobBudgetFactoryForTest = () =>
                new CodeNav.Core.Discovery.GlobMatchBudget(32, 1,
                    TimeSpan.FromSeconds(10));
            File.Delete(Path.Combine(ownerDirectory, "Deleted.cs"));
            manager.RequestRefresh(new[] { relativePath });
            Assert.True(WaitUntil(() =>
            {
                using var q = manager.OpenQueries();
                return manager.State == "ready" && q.ContentByPath(relativePath) is null;
            }, 20_000), "index did not reflect glob-budget deletion");

            JsonElement pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("projectOwnershipFallbackCoverage", out _), "review_pack with projectOwnershipFallbackCoverage");
            JsonElement coverage = pack.GetProperty("projectOwnershipFallbackCoverage");
            Assert.True(coverage.GetProperty("budgetHit").GetBoolean());
            Assert.True(coverage.GetProperty("globBudgetHit").GetBoolean());
            Assert.False(coverage.TryGetProperty("shapeBudgetHit", out _));
            Assert.False(coverage.GetProperty("complete").GetBoolean());
            Assert.Equal(1, coverage.GetProperty("globOperationLimit").GetInt64());
            Assert.True(coverage.GetProperty("globOperations").GetInt64() <= 1);
            Assert.Equal(32, coverage.GetProperty("globSegmentLimit").GetInt32());
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.project_glob_budget");
            Assert.DoesNotContain(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.project_shape_budget");
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == relativePath);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewPackSeparatesShapeOnlyOwnershipBudgetCause()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-owner-shape-cause").FullName);
        try
        {
            WriteReviewRepo(root);
            string hugeDirectory = Path.Combine(root, "HugeShape");
            Directory.CreateDirectory(hugeDirectory);
            File.WriteAllText(Path.Combine(hugeDirectory, "HugeShape.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><!--" +
                new string('x', (256 * 1024) + 1) + "--></Project>");
            Git(root, "add -A");
            Git(root, "commit -q -m shape-cause-baseline");

            using var manager = StartManager(root);
            var tools = new NavigationTools(manager, new SemanticService(manager));
            File.Delete(Path.Combine(root, "Lib", "Old.cs"));
            manager.RequestRefresh(new[] { "Lib/Old.cs" });
            Assert.True(WaitUntil(() =>
            {
                using var q = manager.OpenQueries();
                return manager.State == "ready" && q.ContentByPath("Lib/Old.cs") is null;
            }, 20_000));

            JsonElement pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("projectOwnershipFallbackCoverage", out _), "review_pack with projectOwnershipFallbackCoverage");
            JsonElement coverage = pack.GetProperty("projectOwnershipFallbackCoverage");
            Assert.True(coverage.GetProperty("shapeBudgetHit").GetBoolean());
            Assert.False(coverage.TryGetProperty("globBudgetHit", out _));
            Assert.True(coverage.GetProperty("budgetHit").GetBoolean());
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.project_shape_budget");
            Assert.DoesNotContain(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.project_glob_budget");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ProjectShapeLoadingDoesNotConsumeOrReportGlobBudgetBeforeMatching()
    {
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-owner-cause-isolation").FullName);
        try
        {
            File.WriteAllText(Path.Combine(root, "Default.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            var projects = new List<ProjectRow>
            {
                new(1, "Default.csproj", "Default", "sdk", "net9.0", false, "parsed"),
            };
            var zeroTimeMatchBudget = new CodeNav.Core.Discovery.GlobMatchBudget(32, 1_000,
                TimeSpan.Zero);

            NavigationTools.ReviewProjectShapeSnapshot snapshot =
                NavigationTools.LoadProjectShapesBounded(root, projects,
                    matchBudgetOverride: zeroTimeMatchBudget);

            Assert.False(snapshot.ShapeBudgetHit);
            Assert.False(snapshot.GlobMatchAttempted);
            Assert.False(snapshot.GlobBudgetHit);
            Assert.False(snapshot.MatchBudget.IsExhausted);
            Assert.True(snapshot.Complete);

            var resolver = new NavigationTools.ReviewProjectOwnershipResolver(() => snapshot);
            Assert.Empty(resolver.OwnerIds("Default.cs"));

            Assert.False(snapshot.ShapeBudgetHit);
            Assert.True(snapshot.GlobMatchAttempted);
            Assert.True(snapshot.GlobBudgetHit);
            Assert.True(snapshot.MatchBudget.IsExhausted);
            Assert.False(snapshot.Complete);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ExactMoveWithUnrelatedDeletionAndCompleteOwnerProofStaysMoveOnly()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-move-unrelated-delete").FullName);
        try
        {
            WriteReviewRepo(root);
            const string moveSource = "Lib/Widget.cs";
            const string moveTarget = "Lib/MovedWidget.cs";
            const string unrelatedDeletion = "Lib/Old.cs";

            using var manager = StartManager(root);
            var tools = new NavigationTools(manager, new SemanticService(manager));
            tools.ReviewProjectGlobBudgetFactoryForTest = () =>
                new CodeNav.Core.Discovery.GlobMatchBudget(256, 1_000_000,
                    Timeout.InfiniteTimeSpan);
            File.Move(Path.Combine(root,
                    moveSource.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, moveTarget.Replace('/', Path.DirectorySeparatorChar)));
            File.Delete(Path.Combine(root,
                unrelatedDeletion.Replace('/', Path.DirectorySeparatorChar)));
            manager.RequestRefresh(new[] { moveSource, moveTarget, unrelatedDeletion });
            Assert.True(WaitUntil(() =>
            {
                using var q = manager.OpenQueries();
                return manager.State == "ready" && q.ContentByPath(moveSource) is null &&
                       q.Outline(moveTarget).Any(symbol => symbol.Name == "Widget") &&
                       q.ContentByPath(unrelatedDeletion) is null;
            }, 20_000), "index did not reflect the exact move plus unrelated deletion");

            JsonElement pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("changedFiles", out _), "review_pack with changedFiles");
            Assert.Contains(pack.GetProperty("movedFiles").GetProperty("items")
                .EnumerateArray(), move =>
                move.GetProperty("from").GetString() == moveSource &&
                move.GetProperty("to").GetString() == moveTarget);
            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles")
                .EnumerateArray());
            Assert.Equal(unrelatedDeletion, deleted.GetProperty("path").GetString());
            Assert.DoesNotContain(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == moveSource);
            JsonElement coverage = pack.GetProperty("projectOwnershipFallbackCoverage");
            Assert.True(coverage.GetProperty("complete").GetBoolean());
            Assert.False(coverage.TryGetProperty("budgetHit", out _));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void OrdinaryDeletionPreflightExhaustionReplaysEarlierExactMove()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-move-preflight-exhaustion").FullName);
        try
        {
            File.WriteAllText(Path.Combine(root, "Owner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            const string moveSource = "Move.cs";
            const string moveTarget = "Moved.cs";
            const string ordinaryDeletion = "Delete.cs";
            File.WriteAllText(Path.Combine(root, moveSource),
                "namespace PreflightBudget42; public class MovedByPreflight42 { }\n");
            File.WriteAllText(Path.Combine(root, ordinaryDeletion),
                "namespace PreflightBudget42; public class DeletedByPreflight42 { }\n");
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            Git(root, "init -q -b main");
            Git(root, "config user.email test@example.com");
            Git(root, "config user.name CodeNavTest");
            Git(root, "config commit.gpgsign false");
            Git(root, "add -A");
            Git(root, "commit -q -m preflight-budget-baseline");

            using var manager = StartManager(root);
            var tools = new NavigationTools(manager, new SemanticService(manager));
            // One default-SDK owner lookup costs 16 operations. The exact move consumes two
            // complete lookups (32); the ordinary deletion's preflight gets four operations into
            // its entry checkpoint, then exhausts before it can evaluate the project.
            tools.ReviewProjectGlobBudgetFactoryForTest = () =>
                new CodeNav.Core.Discovery.GlobMatchBudget(32, 36,
                    Timeout.InfiniteTimeSpan);
            File.Move(Path.Combine(root, moveSource), Path.Combine(root, moveTarget));
            File.Delete(Path.Combine(root, ordinaryDeletion));
            manager.RequestRefresh(new[] { moveSource, moveTarget, ordinaryDeletion });
            Assert.True(WaitUntil(() =>
            {
                using var q = manager.OpenQueries();
                return manager.State == "ready" && q.ContentByPath(moveSource) is null &&
                       q.Outline(moveTarget).Any(symbol =>
                           symbol.Name == "MovedByPreflight42") &&
                       q.ContentByPath(ordinaryDeletion) is null;
            }, 20_000), "index did not reflect the move plus ordinary deletion");

            JsonElement pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("changedFiles", out _), "review_pack with changedFiles");
            Assert.Contains(pack.GetProperty("movedFiles").GetProperty("items")
                .EnumerateArray(), move =>
                move.GetProperty("from").GetString() == moveSource &&
                move.GetProperty("to").GetString() == moveTarget);
            Assert.Equal(2, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == ordinaryDeletion);
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == moveSource);
            JsonElement coverage = pack.GetProperty("projectOwnershipFallbackCoverage");
            Assert.Equal(36, coverage.GetProperty("globOperationLimit").GetInt64());
            Assert.Equal(36, coverage.GetProperty("globOperations").GetInt64());
            Assert.True(coverage.GetProperty("globBudgetHit").GetBoolean());
            Assert.False(coverage.GetProperty("complete").GetBoolean());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void LaterMoveGlobExhaustionReplaysEarlierProvisionalMoveAsDeletion()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-move-budget-replay").FullName);
        try
        {
            WriteReviewRepo(root);
            string movesDirectory = Path.Combine(root, "Moves");
            Directory.CreateDirectory(movesDirectory);
            File.WriteAllText(Path.Combine(movesDirectory, "Moves.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework>" +
                "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Include=\"**/*.cs\" />" +
                "</ItemGroup></Project>");
            const string aSource = "Moves/A.cs";
            const string aTarget = "Moves/A2.cs";
            string longStem = "Z" + new string('z', 96);
            string zSource = $"Moves/{longStem}.cs";
            string zTarget = $"Moves/{longStem}2.cs";
            File.WriteAllText(Path.Combine(movesDirectory, "A.cs"),
                "namespace MoveBudget42; public class EarlyMove42 { " +
                "public void Preserved42() { } }\n");
            File.WriteAllText(Path.Combine(movesDirectory, longStem + ".cs"),
                "namespace MoveBudget42; public class LateMove42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m move-budget-baseline");

            using var manager = StartManager(root);
            var tools = new NavigationTools(manager, new SemanticService(manager));
            tools.ReviewProjectGlobBudgetFactoryForTest = () =>
                new CodeNav.Core.Discovery.GlobMatchBudget(64, 256,
                    TimeSpan.FromSeconds(10));
            File.Move(Path.Combine(movesDirectory, "A.cs"),
                Path.Combine(movesDirectory, "A2.cs"));
            File.Move(Path.Combine(movesDirectory, longStem + ".cs"),
                Path.Combine(movesDirectory, longStem + "2.cs"));
            manager.RequestRefresh(new[] { aSource, aTarget, zSource, zTarget });
            Assert.True(WaitUntil(() =>
            {
                using var q = manager.OpenQueries();
                return manager.State == "ready" && q.ContentByPath(aSource) is null &&
                       q.Outline(aTarget).Any(symbol => symbol.Name == "EarlyMove42") &&
                       q.ContentByPath(zSource) is null &&
                       q.Outline(zTarget).Any(symbol => symbol.Name == "LateMove42");
            }, 20_000), "index did not reflect both exact moves");

            JsonElement pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("movedFiles", out _), "review_pack with movedFiles");
            Assert.Contains(pack.GetProperty("movedFiles").GetProperty("items")
                .EnumerateArray(), move =>
                move.GetProperty("from").GetString() == aSource &&
                move.GetProperty("to").GetString() == aTarget);
            Assert.Contains(pack.GetProperty("movedFiles").GetProperty("items")
                .EnumerateArray(), move =>
                move.GetProperty("from").GetString() == zSource &&
                move.GetProperty("to").GetString() == zTarget);
            JsonElement replayed = Assert.Single(pack.GetProperty("deletedFiles")
                .EnumerateArray(), file => file.GetProperty("path").GetString() == aSource);
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == zSource);
            JsonElement formerType = Assert.Single(replayed.GetProperty("formerTypes")
                .EnumerateArray(), type => type.GetProperty("name").GetString() == "EarlyMove42");
            Assert.Equal("ambiguous_survivor",
                formerType.GetProperty("danglingStatus").GetString());
            Assert.False(formerType.TryGetProperty("danglingCandidates", out _));
            JsonElement coverage = pack.GetProperty("projectOwnershipFallbackCoverage");
            Assert.True(coverage.GetProperty("globBudgetHit").GetBoolean());
            Assert.False(coverage.GetProperty("complete").GetBoolean());
        }
        finally { Cleanup(root); }
    }
}
