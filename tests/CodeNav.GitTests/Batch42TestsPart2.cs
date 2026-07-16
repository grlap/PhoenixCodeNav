using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using static CodeNav.Tests.Batch42Support;

namespace CodeNav.Tests;

/// <summary>
/// Owns: slice 2 of 3 of the Batch 42 (v0.11.0) review_pack suite — a contiguous, duration-
/// balanced block of tests moved VERBATIM (xUnit parallelizes across classes but runs one class
/// serially; the original single class was the suite's ~98s critical path).
/// Deliberately does not own: the shared fixture/helpers (Batch42Support.cs) or sibling slices.
/// Split out of: Batch42Tests.cs (PhoenixCodeNav-6zdy).
/// </summary>
public class Batch42TestsPart2
{
    [Fact]
    public void ExactCSharpMoveAcrossUnrelatedProjectsRetainsFormerDeletionCoverage()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-cross-project-move").FullName);
        try
        {
            WriteReviewRepo(root);
            string unrelated = Path.Combine(root, "Unrelated");
            Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated, "Unrelated.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            const string oldPath = "Lib/CrossProjectMove.cs";
            const string newPath = "Unrelated/CrossProjectMove.cs";
            File.WriteAllText(Path.Combine(root, oldPath),
                "namespace CrossProjectMoveNs { public class CrossProjectMove42 { public void Keep() { } } }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m cross-project-move-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.Move(Path.Combine(root, oldPath), Path.Combine(root, newPath));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "CrossProjectMove42");
            }, 20_000), "index did not reflect the cross-project exact move");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("changedFiles", out _), "review_pack with changedFiles");
            JsonElement move = Assert.Single(pack.GetProperty("movedFiles")
                .GetProperty("items").EnumerateArray(), item =>
                item.GetProperty("from").GetString() == oldPath);
            Assert.Equal(newPath, move.GetProperty("to").GetString());
            Assert.Equal("exact_blob", move.GetProperty("match").GetString());

            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles").EnumerateArray(),
                file => file.GetProperty("path").GetString() == oldPath);
            JsonElement formerType = Assert.Single(
                deleted.GetProperty("formerTypes").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "CrossProjectMove42");
            Assert.Equal("ambiguous_survivor",
                formerType.GetProperty("danglingStatus").GetString());
            Assert.True(!formerType.TryGetProperty("danglingCandidates", out JsonElement dangling) ||
                        dangling.ValueKind == JsonValueKind.Null,
                formerType.GetRawText());
            Assert.Contains(formerType.GetProperty("survivingDeclarationPaths").EnumerateArray(),
                path => path.GetString() == newPath);

            JsonElement coverage = pack.GetProperty("deletedFilesCoverage");
            Assert.Equal(1, coverage.GetProperty("total").GetInt32());
            Assert.Equal(1, coverage.GetProperty("returned").GetInt32());
            Assert.False(coverage.TryGetProperty("truncated", out _));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ExactMoveLosingOneOfMultipleProjectDomainsRetainsDeletionEvidence()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-partial-project-move").FullName);
        try
        {
            WriteReviewRepo(root);
            string shared = Path.Combine(root, "Shared");
            string projectA = Path.Combine(root, "ProjectA");
            string projectB = Path.Combine(root, "ProjectB");
            Directory.CreateDirectory(shared);
            Directory.CreateDirectory(projectA);
            Directory.CreateDirectory(projectB);
            const string oldPath = "Shared/SharedDomain.cs";
            const string newPath = "ProjectA/SharedDomain.cs";
            File.WriteAllText(Path.Combine(root, oldPath),
                "namespace SharedDomainNs { public class SharedDomain42 { public void Keep() { } } }\n");
            File.WriteAllText(Path.Combine(projectA, "ProjectA.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"../Shared/SharedDomain.cs\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(projectB, "ProjectB.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"../Shared/SharedDomain.cs\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(projectB, "UseSharedDomain.cs"),
                "namespace ProjectB { public class UseSharedDomain { public SharedDomainNs.SharedDomain42? Value; } }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m shared-project-domain-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            using (var baselineQueries = m.OpenQueries())
            {
                HashSet<string> baselineOwners = baselineQueries.ProjectsContaining(oldPath)
                    .Select(project => project.Name)
                    .ToHashSet(StringComparer.Ordinal);
                Assert.Contains("ProjectA", baselineOwners);
                Assert.Contains("ProjectB", baselineOwners);
            }

            File.Move(Path.Combine(root, oldPath), Path.Combine(root, newPath));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "SharedDomain42");
            }, 20_000), "index did not reflect the partial-project-domain exact move");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("movedFiles", out _), "review_pack with movedFiles");
            JsonElement move = Assert.Single(pack.GetProperty("movedFiles")
                .GetProperty("items").EnumerateArray(), item =>
                item.GetProperty("from").GetString() == oldPath);
            Assert.Equal(newPath, move.GetProperty("to").GetString());
            Assert.Equal("exact_blob", move.GetProperty("match").GetString());

            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles").EnumerateArray(),
                file => file.GetProperty("path").GetString() == oldPath);
            JsonElement formerType = Assert.Single(
                deleted.GetProperty("formerTypes").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "SharedDomain42");
            Assert.True(formerType.GetProperty("referenceCandidates").GetInt32() > 0,
                formerType.GetRawText());
            Assert.Contains(formerType.GetProperty("samplePaths").EnumerateArray(), path =>
                path.GetString() == "ProjectB/UseSharedDomain.cs");
            if (formerType.TryGetProperty("danglingCandidates", out JsonElement dangling) &&
                dangling.ValueKind == JsonValueKind.Number)
            {
                Assert.NotEqual(0, dangling.GetInt32());
            }

            JsonElement coverage = pack.GetProperty("deletedFilesCoverage");
            Assert.Equal(1, coverage.GetProperty("total").GetInt32());
            Assert.Equal(1, coverage.GetProperty("returned").GetInt32());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void RelocatedSurvivingTypeDoesNotProduceDeletedDanglingWarning()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-modified-move").FullName);
        try
        {
            WriteReviewRepo(root);
            const string oldPath = "Lib/OldRelocated.cs";
            const string newPath = "Lib/NewRelocated.cs";
            File.WriteAllText(Path.Combine(root, oldPath),
                """
                namespace Lib
                {
                    public class Relocated42
                    {
                        public int Value => 1;
                    }
                }
                """);
            File.WriteAllText(Path.Combine(root, "Consumer", "UseRelocated.cs"),
                "namespace Consumer { public class UseRelocated { public Lib.Relocated42? Value; } }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m modified-move-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.Move(Path.Combine(root, oldPath), Path.Combine(root, newPath));
            File.WriteAllText(Path.Combine(root, newPath),
                File.ReadAllText(Path.Combine(root, newPath))
                    .Replace("Value => 1", "Value => 2", StringComparison.Ordinal));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "Relocated42");
            }, 20_000), "index did not reflect the modified C# relocation");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("deletedFiles", out _), "review_pack with deletedFiles");
            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles").EnumerateArray(),
                file => file.GetProperty("path").GetString() == oldPath);
            JsonElement formerType = Assert.Single(
                deleted.GetProperty("formerTypes").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "Relocated42");
            Assert.True(formerType.TryGetProperty("danglingCandidates", out JsonElement dangling),
                formerType.GetRawText());
            Assert.Equal(0, dangling.GetInt32());
            Assert.Equal("project_candidate_survivor",
                formerType.GetProperty("danglingStatus").GetString());
            Assert.DoesNotContain(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.deleted_dangling");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewPackMarksReferenceCandidateCountsAsLowerBoundsAtTheScanCap()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-ref-cap").FullName);
        try
        {
            string dir = Path.Combine(root, "App");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(dir, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(dir, "Target.cs"),
                "public class ReferenceCapTarget42 { }\n");
            for (int i = 0; i < 205; i++)
            {
                File.WriteAllText(Path.Combine(dir, $"Use{i:D3}.cs"),
                    $"public class Use{i:D3} {{ ReferenceCapTarget42? Value; }}\n");
            }
            Git(root, "init -q -b main");
            Git(root, "config user.email test@example.com");
            Git(root, "config user.name CodeNavTest");
            Git(root, "config commit.gpgsign false");
            Git(root, "add -A");
            Git(root, "commit -q -m initial");
            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(paths: "App/Target.cs", maxBytes: 24576),
                j => j.TryGetProperty("symbols", out _), "review_pack with symbols");
            var digest = Assert.Single(pack.GetProperty("symbols").EnumerateArray());
            Assert.True(digest.GetProperty("referenceCandidatesLowerBound").GetBoolean());
            Assert.True(digest.TryGetProperty("referenceCandidatesCoverage", out var coverage),
                digest.GetRawText());
            Assert.Equal(200, coverage.GetProperty("scanned").GetInt32());
            Assert.Equal(201, coverage.GetProperty("atLeast").GetInt32());
            Assert.Equal(200, coverage.GetProperty("limit").GetInt32());
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.reference_candidates_cap");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ModifiedSameProjectRelocationReportsRemovedMemberWithoutCallingTypeDangling()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-relocated-member").FullName);
        try
        {
            WriteReviewRepo(root);
            const string oldPath = "Lib/OldRelocatedMember.cs";
            const string newPath = "Lib/NewRelocatedMember.cs";
            File.WriteAllText(Path.Combine(root, oldPath),
                "namespace Lib { public class RelocatedMember42 { public void Gone() { } public int Value => 1; } }\n");
            File.WriteAllText(Path.Combine(root, "Consumer", "UseRelocatedMember.cs"),
                "namespace Consumer { public class UseRelocatedMember { public void Run(Lib.RelocatedMember42 value) { value.Gone(); } } }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m relocated-member-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.Delete(Path.Combine(root, oldPath));
            File.WriteAllText(Path.Combine(root, newPath),
                "namespace Lib { public class RelocatedMember42 { public int Value => 2; } }\n");
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "RelocatedMember42") &&
                       q.Outline(newPath).All(symbol => symbol.Name != "Gone");
            }, 20_000), "index did not reflect the member-removing relocation");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("deletedFiles", out _), "review_pack with deletedFiles");
            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles").EnumerateArray(),
                file => file.GetProperty("path").GetString() == oldPath);
            JsonElement survivingType = Assert.Single(
                deleted.GetProperty("formerTypes").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "RelocatedMember42");
            Assert.True(survivingType.TryGetProperty("danglingCandidates",
                out JsonElement typeDangling), survivingType.GetRawText());
            Assert.Equal(0, typeDangling.GetInt32());
            Assert.Equal("project_candidate_survivor",
                survivingType.GetProperty("danglingStatus").GetString());

            JsonElement formerFile = pack.GetProperty("formerSymbols").EnumerateArray()
                .Single(file => file.GetProperty("path").GetString() == oldPath);
            JsonElement gone = formerFile.GetProperty("formerSymbols").EnumerateArray()
                .Single(symbol => symbol.GetProperty("name").GetString() == "Gone");
            Assert.True(gone.GetProperty("danglingCandidates").GetInt32() > 0,
                gone.GetRawText());
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.former_symbol_dangling");
            Assert.DoesNotContain(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.deleted_dangling");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void IdenticalFqnInUnrelatedProjectRemainsAdvisoryInsteadOfExactZero()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-unrelated-survivor").FullName);
        try
        {
            WriteReviewRepo(root);
            string unrelated = Path.Combine(root, "Unrelated");
            Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated, "Unrelated.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(unrelated, "Shadow.cs"),
                "namespace Lib { public class OldThing { public void Legacy() { } } }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m unrelated-survivor-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            const string oldPath = "Lib/Old.cs";
            File.Delete(Path.Combine(root, oldPath));
            m.RequestRefresh(new[] { oldPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline("Unrelated/Shadow.cs").Any(symbol => symbol.Name == "OldThing");
            }, 20_000), "index did not reflect the unrelated surviving declaration");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("deletedFiles", out _), "review_pack with deletedFiles");
            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles").EnumerateArray(),
                file => file.GetProperty("path").GetString() == oldPath);
            JsonElement formerType = Assert.Single(
                deleted.GetProperty("formerTypes").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "OldThing");
            Assert.Equal("ambiguous_survivor",
                formerType.GetProperty("danglingStatus").GetString());
            Assert.True(formerType.GetProperty("referenceCandidates").GetInt32() > 0,
                formerType.GetRawText());
            Assert.True(!formerType.TryGetProperty("danglingCandidates", out JsonElement dangling) ||
                        dangling.ValueKind == JsonValueKind.Null,
                formerType.GetRawText());
            Assert.Contains(formerType.GetProperty("survivingDeclarationPaths").EnumerateArray(),
                path => path.GetString() == "Unrelated/Shadow.cs");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void UnrelatedSameNameDeclarationWithoutCallsIsNotADanglingReference()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-unrelated-no-call").FullName);
        try
        {
            WriteReviewRepo(root);
            string unrelated = Path.Combine(root, "Unrelated");
            Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated, "Unrelated.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            const string oldPath = "Lib/NoCallOld.cs";
            const string shadowPath = "Unrelated/NoCallShadow.cs";
            const string source =
                "namespace NoCallNs { public class NoCallShadow42 { } }\n";
            File.WriteAllText(Path.Combine(root, oldPath), source);
            File.WriteAllText(Path.Combine(root, shadowPath), source);
            Git(root, "add -A");
            Git(root, "commit -q -m unrelated-no-call-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.Delete(Path.Combine(root, oldPath));
            m.RequestRefresh(new[] { oldPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(shadowPath).Any(symbol => symbol.Name == "NoCallShadow42");
            }, 20_000), "index did not reflect the unrelated no-call survivor");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("deletedFiles", out _), "review_pack with deletedFiles");
            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles").EnumerateArray(),
                file => file.GetProperty("path").GetString() == oldPath);
            JsonElement formerType = Assert.Single(
                deleted.GetProperty("formerTypes").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "NoCallShadow42");
            Assert.Equal("ambiguous_survivor",
                formerType.GetProperty("danglingStatus").GetString());
            Assert.Equal(0, formerType.GetProperty("referenceCandidates").GetInt32());
            Assert.Empty(formerType.GetProperty("samplePaths").EnumerateArray());
            Assert.True(!formerType.TryGetProperty("danglingCandidates", out JsonElement dangling) ||
                        dangling.ValueKind == JsonValueKind.Null,
                formerType.GetRawText());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void GeneratedAndBaseListChangedSameProjectSurvivorsAreProjectDomainEvidence()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-project-domain-survivors").FullName);
        try
        {
            WriteReviewRepo(root);
            const string generatedOld = "Lib/GeneratedOld.cs";
            const string generatedCurrent = "Lib/GeneratedDomain.g.cs";
            const string baseOld = "Lib/BaseOld.cs";
            const string baseCurrent = "Lib/BaseCurrent.cs";
            File.WriteAllText(Path.Combine(root, generatedOld),
                "namespace Lib { public class GeneratedDomain42 { public int Value => 1; } }\n");
            File.WriteAllText(Path.Combine(root, baseOld),
                "namespace Lib { public class BaseChangedDomain42 : System.IDisposable { public void Dispose() { } } }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m project-domain-survivor-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.Delete(Path.Combine(root, generatedOld));
            File.Delete(Path.Combine(root, baseOld));
            File.WriteAllText(Path.Combine(root, generatedCurrent),
                "namespace Lib { public class GeneratedDomain42 { public int Value => 2; } }\n");
            File.WriteAllText(Path.Combine(root, baseCurrent),
                "namespace Lib { public class BaseChangedDomain42 : object { } }\n");
            m.RequestRefresh(new[] { generatedOld, generatedCurrent, baseOld, baseCurrent });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(generatedOld) is null &&
                       q.ContentByPath(baseOld) is null &&
                       q.Outline(generatedCurrent).Any(symbol =>
                           symbol.Name == "GeneratedDomain42" && symbol.FileIsGenerated) &&
                       q.Outline(baseCurrent).Any(symbol => symbol.Name == "BaseChangedDomain42");
            }, 20_000), "index did not reflect the same-project survivors");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("deletedFiles", out _), "review_pack with deletedFiles");
            var deletedFiles = pack.GetProperty("deletedFiles").EnumerateArray().ToList();
            foreach ((string oldPath, string typeName, string currentPath) in new[]
                     {
                         (generatedOld, "GeneratedDomain42", generatedCurrent),
                         (baseOld, "BaseChangedDomain42", baseCurrent),
                     })
            {
                JsonElement deleted = deletedFiles.Single(file =>
                    file.GetProperty("path").GetString() == oldPath);
                JsonElement formerType = deleted.GetProperty("formerTypes").EnumerateArray()
                    .Single(symbol => symbol.GetProperty("name").GetString() == typeName);
                Assert.True(formerType.TryGetProperty("danglingCandidates",
                    out JsonElement dangling), formerType.GetRawText());
                Assert.Equal(0, dangling.GetInt32());
                Assert.Equal("project_candidate_survivor",
                    formerType.GetProperty("danglingStatus").GetString());
                Assert.Contains(
                    formerType.GetProperty("survivingDeclarationPaths").EnumerateArray(),
                    path => path.GetString() == currentPath);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void PureOldSideDeletionReportsFileLevelGapAndNamespaceNameCoverage()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-old-side-deletions").FullName);
        try
        {
            WriteReviewRepo(root);
            const string mixedPath = "Lib/MixedOldSide.cs";
            const string namespacePath = "Lib/DeletedNamespaceName.cs";
            File.WriteAllText(Path.Combine(root, mixedPath),
                "global using System.Text;\n" +
                "public class RemovedOldSide42 { }\n" +
                "public class KeptOldSide42 { }\n");
            File.WriteAllText(Path.Combine(root, namespacePath),
                "namespace DeletedNamespace42;\n" +
                "public class KeptNamespaceBody42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m old-side-deletion-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.WriteAllText(Path.Combine(root, mixedPath),
                "public class KeptOldSide42 { }\n");
            File.WriteAllText(Path.Combine(root, namespacePath),
                "public class KeptNamespaceBody42 { }\n");
            m.RequestRefresh(new[] { mixedPath, namespacePath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.Outline(mixedPath).Any(symbol => symbol.Name == "KeptOldSide42") &&
                       q.Outline(mixedPath).All(symbol => symbol.Name != "RemovedOldSide42") &&
                       q.Outline(namespacePath).Any(symbol =>
                           symbol.Name == "KeptNamespaceBody42" && symbol.Ns is null);
            }, 20_000), "index did not reflect the pure old-side deletions");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("unmappedChanges", out _), "review_pack with unmappedChanges");
            var unmapped = pack.GetProperty("unmappedChanges").GetProperty("items")
                .EnumerateArray().ToList();
            JsonElement mixed = Assert.Single(unmapped, item =>
                item.GetProperty("path").GetString() == mixedPath);
            Assert.Equal("old", mixed.GetProperty("side").GetString());
            Assert.Equal("file_level_deleted", mixed.GetProperty("reason").GetString());
            Assert.Equal(1, mixed.GetProperty("old").GetProperty("start").GetInt32());
            Assert.Equal(2, mixed.GetProperty("old").GetProperty("count").GetInt32());
            Assert.False(mixed.TryGetProperty("new", out _));

            JsonElement namespaceOnly = Assert.Single(unmapped, item =>
                item.GetProperty("path").GetString() == namespacePath);
            Assert.Equal("old", namespaceOnly.GetProperty("side").GetString());
            Assert.Equal("namespace", namespaceOnly.GetProperty("reason").GetString());
            Assert.Equal(1,
                namespaceOnly.GetProperty("old").GetProperty("count").GetInt32());

            JsonElement formerFile = pack.GetProperty("formerSymbols").EnumerateArray()
                .Single(file => file.GetProperty("path").GetString() == mixedPath);
            Assert.Contains(formerFile.GetProperty("formerSymbols").EnumerateArray(), symbol =>
                symbol.GetProperty("name").GetString() == "RemovedOldSide42");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void MultilineNamespaceFinalNameLineWithMixedContentIsFileLevel()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-multiline-namespace").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/MultilineNamespace.cs";
            string path = Path.Combine(root, relativePath);
            File.WriteAllText(path,
                "namespace Multiline42\n" +
                "    .Inner { /* final-name-marker */ }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m multiline-namespace-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.WriteAllText(path, File.ReadAllText(path).Replace(
                "final-name-marker", "final-name-edited", StringComparison.Ordinal));
            m.RequestRefresh(new[] { relativePath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(relativePath)?.Contains("final-name-edited",
                           StringComparison.Ordinal) == true;
            }, 20_000), "index did not reflect the multiline namespace comment edit");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("unmappedChanges", out _), "review_pack with unmappedChanges");
            JsonElement item = Assert.Single(pack.GetProperty("unmappedChanges")
                .GetProperty("items").EnumerateArray(), candidate =>
                candidate.GetProperty("path").GetString() == relativePath);
            Assert.Equal("file_level", item.GetProperty("reason").GetString());
            Assert.Equal("both", item.GetProperty("side").GetString());
            Assert.Contains(item.GetProperty("additionalReasons").EnumerateArray(), reason =>
                reason.GetString() == "file_level_old");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void RemovingMemberFromGenericSiblingDoesNotMatchNonGenericContainerMember()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-generic-container-identity").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/GenericContainerIdentity.cs";
            string path = Path.Combine(root, relativePath);
            File.WriteAllText(path,
                "namespace GenericContainerIdentity42;\n" +
                "\n" +
                "public class C\n" +
                "{\n" +
                "    public void M() { }\n" +
                "}\n" +
                "\n" +
                "public class C<T>\n" +
                "{\n" +
                "    public void M() { }\n" +
                "    public void Keep() { }\n" +
                "}\n");
            Git(root, "add -A");
            Git(root, "commit -q -m generic-container-identity-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.WriteAllText(path,
                "namespace GenericContainerIdentity42;\n" +
                "\n" +
                "public class C\n" +
                "{\n" +
                "    public void M() { }\n" +
                "}\n" +
                "\n" +
                "public class C<T>\n" +
                "{\n" +
                "    public void Keep() { }\n" +
                "}\n");
            m.RequestRefresh(new[] { relativePath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                List<SymbolHit> outline = q.Outline(relativePath);
                return outline.Count(symbol => symbol.Name == "M") == 1 &&
                       outline.Any(symbol => symbol.Name == "Keep");
            }, 20_000), "index did not reflect the generic-container member deletion");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("formerSymbols", out _), "review_pack with formerSymbols");
            JsonElement formerFile = pack.GetProperty("formerSymbols").EnumerateArray()
                .Single(file => file.GetProperty("path").GetString() == relativePath);
            JsonElement removed = Assert.Single(
                formerFile.GetProperty("formerSymbols").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "M");
            Assert.Equal("C", removed.GetProperty("container").GetString());
            Assert.Equal("void M()", removed.GetProperty("signature").GetString());
            Assert.Equal(10, removed.GetProperty("startLine").GetInt32());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void RemovingOneExplicitInterfaceImplementationKeepsSignaturesDistinct()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-explicit-interface-identity").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/ExplicitInterfaceIdentity.cs";
            string path = Path.Combine(root, relativePath);
            File.WriteAllText(path,
                "namespace ExplicitInterfaceIdentity42;\n" +
                "\n" +
                "public interface IFoo { void M(); }\n" +
                "public interface IBar { void M(); }\n" +
                "\n" +
                "public class Implementation : IFoo, IBar\n" +
                "{\n" +
                "    void IFoo.M() { }\n" +
                "    void IBar.M() { }\n" +
                "}\n");
            Git(root, "add -A");
            Git(root, "commit -q -m explicit-interface-identity-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.WriteAllText(path,
                "namespace ExplicitInterfaceIdentity42;\n" +
                "\n" +
                "public interface IFoo { void M(); }\n" +
                "public interface IBar { void M(); }\n" +
                "\n" +
                "public class Implementation : IFoo, IBar\n" +
                "{\n" +
                "    void IBar.M() { }\n" +
                "}\n");
            m.RequestRefresh(new[] { relativePath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                List<SymbolHit> outline = q.Outline(relativePath);
                return outline.Any(symbol => symbol.Signature == "void IBar.M()") &&
                       outline.All(symbol => symbol.Signature != "void IFoo.M()");
            }, 20_000), "index did not reflect the explicit-interface member deletion");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("formerSymbols", out _), "review_pack with formerSymbols");
            JsonElement formerFile = pack.GetProperty("formerSymbols").EnumerateArray()
                .Single(file => file.GetProperty("path").GetString() == relativePath);
            JsonElement removed = Assert.Single(
                formerFile.GetProperty("formerSymbols").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "M");
            Assert.Equal("void IFoo.M()", removed.GetProperty("signature").GetString());
            Assert.DoesNotContain(formerFile.GetProperty("formerSymbols").EnumerateArray(),
                symbol => symbol.GetProperty("signature").GetString() == "void IBar.M()");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void NamespaceDeclarationTokenIsNotADeletedTypeDanglingReference()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-namespace-declaration-reference").FullName);
        try
        {
            WriteReviewRepo(root);
            const string oldPath = "Lib/NamespaceTokenVictim.cs";
            const string namespacePath = "Lib/NamespaceTokenOnly.cs";
            File.WriteAllText(Path.Combine(root, oldPath),
                "namespace Lib { public class NamespaceTokenVictim42 { } }\n");
            File.WriteAllText(Path.Combine(root, namespacePath),
                "namespace NamespaceTokenVictim42 { public class Marker42 { } }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m namespace-declaration-reference-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.Delete(Path.Combine(root, oldPath));
            m.RequestRefresh(new[] { oldPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(namespacePath).Any(symbol => symbol.Name == "Marker42");
            }, 20_000), "index did not reflect the namespace-token victim deletion");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("deletedFiles", out _), "review_pack with deletedFiles");
            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles").EnumerateArray(),
                file => file.GetProperty("path").GetString() == oldPath);
            JsonElement formerType = Assert.Single(
                deleted.GetProperty("formerTypes").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "NamespaceTokenVictim42");
            Assert.Equal(0, formerType.GetProperty("referenceCandidates").GetInt32());
            Assert.Equal(0, formerType.GetProperty("danglingCandidates").GetInt32());
            Assert.Empty(formerType.GetProperty("samplePaths").EnumerateArray());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ExactMoveWithinDefaultOwnerRetainsEvidenceForLostLinkedProjectDomains()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-default-and-linked-owners").FullName);
        try
        {
            WriteReviewRepo(root);
            string shared = Path.Combine(root, "Shared");
            string projectA = Path.Combine(root, "A");
            string projectB = Path.Combine(root, "B");
            Directory.CreateDirectory(shared);
            Directory.CreateDirectory(projectA);
            Directory.CreateDirectory(projectB);
            const string oldPath = "Shared/Type.cs";
            const string newPath = "Shared/MovedType.cs";
            const string typeName = "LinkedOwnerDomain42";
            File.WriteAllText(Path.Combine(shared, "Shared.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, oldPath),
                $"namespace LinkedOwnerDomainNs {{ public class {typeName} {{ }} }}\n");
            foreach ((string directory, string projectName) in new[]
                     {
                         (projectA, "A"),
                         (projectB, "B"),
                     })
            {
                File.WriteAllText(Path.Combine(directory, $"{projectName}.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>" +
                    "<ItemGroup><Compile Include=\"../Shared/Type.cs\" Link=\"Linked/Type.cs\" /></ItemGroup></Project>");
                File.WriteAllText(Path.Combine(directory, $"Use{projectName}.cs"),
                    $"namespace {projectName} {{ public class Use{projectName} {{ public LinkedOwnerDomainNs.{typeName}? Value; }} }}\n");
            }
            Git(root, "add -A");
            Git(root, "commit -q -m default-and-linked-owner-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            using (var baselineQueries = m.OpenQueries())
            {
                HashSet<string> owners = baselineQueries.ProjectsContaining(oldPath)
                    .Select(project => project.Name)
                    .ToHashSet(StringComparer.Ordinal);
                Assert.Contains("Shared", owners);
                Assert.Contains("A", owners);
                Assert.Contains("B", owners);
            }

            File.Move(Path.Combine(root, oldPath), Path.Combine(root, newPath));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == typeName);
            }, 20_000), "index did not reflect the multi-owner exact move");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("changedFiles", out _), "review_pack with changedFiles");
            JsonElement move = Assert.Single(pack.GetProperty("movedFiles")
                .GetProperty("items").EnumerateArray(), item =>
                item.GetProperty("from").GetString() == oldPath);
            Assert.Equal(newPath, move.GetProperty("to").GetString());
            Assert.Equal("exact_blob", move.GetProperty("match").GetString());

            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles").EnumerateArray(),
                file => file.GetProperty("path").GetString() == oldPath);
            JsonElement formerType = Assert.Single(
                deleted.GetProperty("formerTypes").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == typeName);
            Assert.True(formerType.GetProperty("referenceCandidates").GetInt32() > 0,
                formerType.GetRawText());
            Assert.Contains(formerType.GetProperty("samplePaths").EnumerateArray(), path =>
                path.GetString() is "A/UseA.cs" or "B/UseB.cs");
            Assert.Contains(formerType.GetProperty("survivingDeclarationPaths").EnumerateArray(),
                path => path.GetString() == newPath);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void TupleElementNamesAreOmittedFromDeclarationKeysButTypesAndNestingRemain()
    {
        static string Key(string parameter)
        {
            ParsedCsFile parsed = SyntaxIndexer.Parse("TupleDeclarationKey.cs",
                $"public class TupleIdentity42 {{ public void M({parameter}) {{ }} }}");
            SymbolRow method = Assert.Single(parsed.Symbols,
                symbol => symbol.Kind == "method" && symbol.Name == "M");
            return Assert.IsType<string>(method.DeclarationKey);
        }

        string flat = Key("(int left, string right) value");
        Assert.Equal(flat, Key("(int x, string y) renamed"));
        Assert.Equal(
            Key("System.Collections.Generic.List<(int left, string right)> value"),
            Key("System.Collections.Generic.List<(int x, string y)> renamed"));

        string nested = Key("(int code, (string text, bool valid) metadata) value");
        Assert.Equal(nested,
            Key("(int number, (string label, bool ok) details) renamed"));
        Assert.NotEqual(flat, Key("(long left, string right) value"));
        Assert.NotEqual(flat, nested);
        Assert.NotEqual(flat, Key("ref (int left, string right) value"));
    }

    [Fact]
    public void TupleElementRenamePreservesReviewPackDeclarationIdentity()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-tuple-declaration-key").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/TupleDeclarationKey.cs";
            string path = Path.Combine(root, relativePath);
            File.WriteAllText(path,
                "namespace DeclarationKey42;\n" +
                "public class TupleIdentity42\n" +
                "{\n" +
                "    public void Transform((int code, (string text, bool valid) metadata) value)\n" +
                "    {\n" +
                "        _ = value.code;\n" +
                "    }\n" +
                "}\n");
            Git(root, "add -A");
            Git(root, "commit -q -m tuple-declaration-key-fixture");

            using var manager = StartManager(root);
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);

            File.WriteAllText(path,
                "namespace DeclarationKey42;\n" +
                "public class TupleIdentity42\n" +
                "{\n" +
                "    public void Transform((int number, (string label, bool ok) details) renamed)\n" +
                "    {\n" +
                "        _ = renamed.number;\n" +
                "    }\n" +
                "}\n");
            manager.RequestRefresh(new[] { relativePath });
            Assert.True(WaitUntil(() =>
            {
                using var queries = manager.OpenQueries();
                return queries.Outline(relativePath).Any(symbol =>
                    symbol.Name == "Transform" &&
                    symbol.Signature.Contains("number", StringComparison.Ordinal) &&
                    symbol.Signature.Contains("label", StringComparison.Ordinal));
            }, 20_000), "index did not reflect the tuple element-name changes");

            JsonElement pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("symbols", out _), "review_pack with symbols");
            Assert.Contains(pack.GetProperty("symbols").EnumerateArray(), item =>
                item.GetProperty("symbol").GetProperty("name").GetString() == "Transform");
            if (pack.TryGetProperty("formerSymbols", out JsonElement formerFiles))
            {
                Assert.DoesNotContain(formerFiles.EnumerateArray(), file =>
                    file.GetProperty("path").GetString() == relativePath);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void MethodParameterRenameAndReturnTypeChangePreserveDeclarationIdentity()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-method-declaration-key").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/MethodDeclarationKey.cs";
            string path = Path.Combine(root, relativePath);
            File.WriteAllText(path,
                "namespace DeclarationKey42;\n" +
                "public class MethodIdentity42\n" +
                "{\n" +
                "    public int Transform(int value)\n" +
                "    {\n" +
                "        return value;\n" +
                "    }\n" +
                "}\n");
            Git(root, "add -A");
            Git(root, "commit -q -m method-declaration-key-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.WriteAllText(path,
                "namespace DeclarationKey42;\n" +
                "public class MethodIdentity42\n" +
                "{\n" +
                "    public long Transform(int renamed)\n" +
                "    {\n" +
                "        return renamed;\n" +
                "    }\n" +
                "}\n");
            m.RequestRefresh(new[] { relativePath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.Outline(relativePath).Any(symbol =>
                    symbol.Name == "Transform" &&
                    symbol.Signature == "long Transform(int renamed)");
            }, 20_000), "index did not reflect the method display-signature change");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("symbols", out _), "review_pack with symbols");
            Assert.Contains(pack.GetProperty("symbols").EnumerateArray(), item =>
                item.GetProperty("symbol").GetProperty("name").GetString() == "Transform");
            if (pack.TryGetProperty("formerSymbols", out JsonElement formerFiles))
            {
                Assert.DoesNotContain(formerFiles.EnumerateArray(), file =>
                    file.GetProperty("path").GetString() == relativePath);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseListChangeIsStableButOverloadParameterTypeReplacementIsFormer()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-type-and-overload-declaration-key").FullName);
        try
        {
            WriteReviewRepo(root);
            const string baseListPath = "Lib/BaseListDeclarationKey.cs";
            const string overloadPath = "Lib/OverloadDeclarationKey.cs";
            string baseListFullPath = Path.Combine(root, baseListPath);
            string overloadFullPath = Path.Combine(root, overloadPath);
            File.WriteAllText(baseListFullPath,
                "namespace DeclarationKey42;\n" +
                "public class BaseListIdentity42 : System.IDisposable\n" +
                "{\n" +
                "    public void Dispose() { }\n" +
                "}\n");
            File.WriteAllText(overloadFullPath,
                "namespace DeclarationKey42;\n" +
                "public class OverloadIdentity42\n" +
                "{\n" +
                "    public void M(int value) { }\n" +
                "    public void M(string value) { }\n" +
                "}\n");
            Git(root, "add -A");
            Git(root, "commit -q -m type-and-overload-declaration-key-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.WriteAllText(baseListFullPath,
                "namespace DeclarationKey42;\n" +
                "public class BaseListIdentity42 : object\n" +
                "{\n" +
                "    public void Dispose() { }\n" +
                "}\n");
            File.WriteAllText(overloadFullPath,
                "namespace DeclarationKey42;\n" +
                "public class OverloadIdentity42\n" +
                "{\n" +
                "    public void M(long value) { }\n" +
                "    public void M(string value) { }\n" +
                "}\n");
            m.RequestRefresh(new[] { baseListPath, overloadPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                List<SymbolHit> overloads = q.Outline(overloadPath)
                    .Where(symbol => symbol.Name == "M").ToList();
                return q.Outline(baseListPath).Any(symbol =>
                           symbol.Name == "BaseListIdentity42" &&
                           symbol.Signature.Contains(": object", StringComparison.Ordinal)) &&
                       overloads.Any(symbol => symbol.Signature == "void M(long value)") &&
                       overloads.Any(symbol => symbol.Signature == "void M(string value)") &&
                       overloads.All(symbol => symbol.Signature != "void M(int value)");
            }, 20_000), "index did not reflect the declaration-key counterexamples");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("symbols", out _), "review_pack with symbols");
            Assert.Contains(pack.GetProperty("symbols").EnumerateArray(), item =>
                item.GetProperty("symbol").GetProperty("name").GetString() ==
                "BaseListIdentity42");
            JsonElement formerFiles = pack.GetProperty("formerSymbols");
            Assert.DoesNotContain(formerFiles.EnumerateArray(), file =>
                file.GetProperty("path").GetString() == baseListPath);
            JsonElement overloadFormerFile = Assert.Single(formerFiles.EnumerateArray(), file =>
                file.GetProperty("path").GetString() == overloadPath);
            JsonElement replacedOverload = Assert.Single(
                overloadFormerFile.GetProperty("formerSymbols").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "M");
            Assert.Equal("void M(int value)",
                replacedOverload.GetProperty("signature").GetString());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReferenceDeclarationExclusionPerFileBudgetReportsExactCoverage()
    {
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-declaration-budget").FullName);
        try
        {
            string dir = Path.Combine(root, "App");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(dir, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            const int perFileLimit = 512 * 1024;
            const string prefix = "public class DeclarationBudgetProbe42 { }\n/*";
            string content = prefix +
                             new string('x', perFileLimit + 1 - prefix.Length - 2) +
                             "*/";
            Assert.Equal(perFileLimit + 1, content.Length);
            File.WriteAllText(Path.Combine(dir, "Oversized.cs"), content);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var q = new IndexQueries(dbPath);
            IndexQueries.ReferenceCandidateResult result = q.ReferenceCandidates(
                "DeclarationBudgetProbe42", maxCandidateFiles: 10, samplesPerProject: 1,
                excludeDeclarations: true);

            Assert.False(result.CandidateFilesTruncated);
            Assert.True(result.DeclarationExclusionBudgetHit);
            Assert.True(result.CandidateFilesScanned < result.CandidateFilesAtLeast);
            Assert.Equal(0, result.CandidateFilesScanned);
            Assert.Equal(1, result.CandidateFilesAtLeast);
            Assert.Equal(10, result.CandidateFileLimit);
            Assert.Equal(0, result.DeclarationFilesParsed);
            Assert.Equal(128, result.DeclarationFileParseLimit);
            Assert.Equal(0, result.DeclarationCharsParsed);
            Assert.Equal(4 * 1024 * 1024, result.DeclarationCharLimit);
            Assert.Equal(perFileLimit, result.DeclarationPerFileCharLimit);
            Assert.Equal(0, result.TotalHits);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewDeclarationBudgetDoesNotMasqueradeAsTheCandidateFileCap()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-declaration-note-cause").FullName);
        try
        {
            string dir = Path.Combine(root, "App");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(dir, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            const string path = "App/Former.cs";
            File.WriteAllText(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)),
                "public class DeclarationNoteCause42 { }\n");

            const int perFileLimit = 512 * 1024;
            const string prefix = "public class DeclarationNoteCause42 { }\n/*";
            string oversized = prefix +
                               new string('x', perFileLimit + 1 - prefix.Length - 2) +
                               "*/";
            Assert.Equal(perFileLimit + 1, oversized.Length);
            File.WriteAllText(Path.Combine(dir, "OversizedCandidate.cs"), oversized);
            Git(root, "init -q -b main");
            Git(root, "config user.email test@example.com");
            Git(root, "config user.name CodeNavTest");
            Git(root, "config commit.gpgsign false");
            Git(root, "add -A");
            Git(root, "commit -q -m declaration-note-cause");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.WriteAllText(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)),
                "public class RenamedDeclarationNoteCause42 { }\n");
            RefreshAndWait(m, path, "RenamedDeclarationNoteCause42");

            JsonElement pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("notes", out _), "review_pack with notes");
            List<JsonElement> notes = pack.GetProperty("notes").EnumerateArray().ToList();
            Assert.Contains(notes, note => note.GetProperty("id").GetString() ==
                                           "review.reference_declaration_budget");
            Assert.DoesNotContain(notes, note => note.GetProperty("id").GetString() ==
                                                 "review.reference_candidates_cap");
            JsonElement formerFile = Assert.Single(pack.GetProperty("formerSymbols")
                .EnumerateArray(), file => file.GetProperty("path").GetString() == path);
            JsonElement former = Assert.Single(formerFile.GetProperty("formerSymbols")
                .EnumerateArray(), symbol => symbol.GetProperty("name").GetString() ==
                                             "DeclarationNoteCause42");
            Assert.True(former.GetProperty("referenceCandidatesLowerBound").GetBoolean());
            Assert.True(former.GetProperty("referenceCandidatesCoverage")
                .GetProperty("declarationExclusionBudgetHit").GetBoolean());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewSurvivorPathCheckPreservesLiteralBackslashesOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-survivor-backslash").FullName);
        try
        {
            string directory = Path.Combine(root, "Survivors");
            Directory.CreateDirectory(directory);
            const string gitPath = "Survivors/Literal\\Twin.cs";
            File.WriteAllText(Path.Combine(directory, "Literal\\Twin.cs"),
                "public class LiteralBackslashSurvivor42 { }\n");

            var literal = new SymbolHit(1, "class", "LiteralBackslashSurvivor42", null,
                null, "class LiteralBackslashSurvivor42", "public", 1, 1, false, null,
                gitPath, false, null);
            var excluded = literal with { Id = 2, FilePath = "Deleted.cs" };
            var missing = literal with { Id = 3, FilePath = "Survivors/Missing.cs" };
            List<SymbolHit> survivors = NavigationTools.FilterExistingReviewDeclarations(root,
                "Deleted.cs", [literal, excluded, missing]);

            Assert.Equal(literal, Assert.Single(survivors));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReferenceDeclarationOffsetCacheInvalidatesWhenIndexedContentChanges()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-declaration-offset-cache").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/DeclarationOffsetCache.cs";
            string path = Path.Combine(root, relativePath);
            File.WriteAllText(path,
                "namespace DeclarationOffsetCache42;\n" +
                "public class CacheProbe42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m declaration-offset-cache-fixture");

            using var m = StartManager(root);
            using IndexQueries q = m.OpenQueries();

            IndexQueries.ReferenceCandidateResult primed = q.ReferenceCandidates(
                "CacheProbe42", 20, 3, excludeDeclarations: true);
            Assert.Equal(0, primed.TotalHits);

            // Shift the declaration identifier to a different absolute offset and add one real
            // use. Reusing q is decisive: stale declaration offsets would count both the moved
            // declaration and the constructor call.
            File.WriteAllText(path,
                "namespace DeclarationOffsetCache42;\n" +
                "\n" +
                "public class PaddingBeforeCacheProbe42\n" +
                "{\n" +
                "    public int Value => 42;\n" +
                "}\n" +
                "\n" +
                "public class CacheProbe42 { }\n" +
                "\n" +
                "public class CacheProbeConsumer42\n" +
                "{\n" +
                "    public object Make() => new CacheProbe42();\n" +
                "}\n");
            m.RequestRefresh(new[] { relativePath });
            Assert.True(WaitUntil(() =>
                q.ContentByPath(relativePath)?.Contains("new CacheProbe42()",
                    StringComparison.Ordinal) == true, 20_000),
                "the reused query connection did not observe the refreshed indexed content");

            IndexQueries.ReferenceCandidateResult refreshed = q.ReferenceCandidates(
                "CacheProbe42", 20, 3, excludeDeclarations: true);
            Assert.Equal(1, refreshed.TotalHits);
            Assert.Equal(1, refreshed.ProdHits);
            Assert.Equal(0, refreshed.TestHits);
            List<TextHit> samples = refreshed.Groups.SelectMany(group => group.Samples).ToList();
            Assert.Contains(samples, hit =>
                hit.FilePath == relativePath &&
                hit.LineText.Contains("new CacheProbe42()", StringComparison.Ordinal));
            Assert.DoesNotContain(samples, hit =>
                hit.LineText.Contains("class CacheProbe42", StringComparison.Ordinal));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DeclarationKeyCanonicalizesGenericTypeAndExplicitInterfaceTrivia()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-declaration-key-trivia").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/DeclarationKeyTrivia.cs";
            string path = Path.Combine(root, relativePath);
            File.WriteAllText(path,
                "using System.Collections.Generic;\n" +
                "namespace DeclarationKeyTrivia42;\n" +
                "public interface IFoo<T>\n" +
                "{\n" +
                "    void M(Dictionary<string, int> value);\n" +
                "}\n" +
                "public class Implementation : IFoo<int>\n" +
                "{\n" +
                "    void IFoo < int > . M(Dictionary < string, int > value) { }\n" +
                "}\n");
            Git(root, "add -A");
            Git(root, "commit -q -m declaration-key-trivia-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            // The spelling stays syntax-equivalent and only trivia around generic punctuation
            // changes. Alias-versus-qualified equivalence is deliberately not asserted: that
            // would require semantic binding and is outside the persisted syntax-key contract.
            File.WriteAllText(path,
                "using System.Collections.Generic;\n" +
                "namespace DeclarationKeyTrivia42;\n" +
                "public interface IFoo<T>\n" +
                "{\n" +
                "    void M(Dictionary<string, int> value);\n" +
                "}\n" +
                "public class Implementation : IFoo<int>\n" +
                "{\n" +
                "    void IFoo<int>.M(Dictionary<string,int> value) { }\n" +
                "}\n");
            m.RequestRefresh(new[] { relativePath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.Outline(relativePath).Any(symbol =>
                    symbol.Name == "M" &&
                    symbol.Signature.Contains("IFoo<int>.M", StringComparison.Ordinal));
            }, 20_000), "index did not reflect the canonical generic trivia");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("symbols", out _), "review_pack with symbols");
            Assert.Contains(pack.GetProperty("symbols").EnumerateArray(), item =>
                item.GetProperty("symbol").GetProperty("signature").GetString()?
                    .Contains("IFoo<int>.M", StringComparison.Ordinal) == true);
            if (pack.TryGetProperty("formerSymbols", out JsonElement formerFiles))
            {
                Assert.DoesNotContain(formerFiles.EnumerateArray(), file =>
                    file.GetProperty("path").GetString() == relativePath);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void GenericParameterRenamesAreStableWhileArityAndConcreteTypesRemainDistinct()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-generic-parameter-identity").FullName);
        try
        {
            WriteReviewRepo(root);
            const string renamePath = "Lib/GenericParameterRename.cs";
            const string distinctPath = "Lib/GenericIdentityChanges.cs";
            string renameFullPath = Path.Combine(root, renamePath);
            string distinctFullPath = Path.Combine(root, distinctPath);
            File.WriteAllText(renameFullPath,
                "namespace GenericParameterIdentity42;\n" +
                "public static class N\n" +
                "{\n" +
                "    public sealed class T { }\n" +
                "    public sealed class U { }\n" +
                "}\n" +
                "public class C<T>\n" +
                "{\n" +
                "    public T FromContainer(T x) => x;\n" +
                "    public void Qualified(N.T concrete, T generic) { }\n" +
                "}\n" +
                "public class MethodContainer\n" +
                "{\n" +
                "    public T M<T>(T x) => x;\n" +
                "}\n");
            File.WriteAllText(distinctFullPath,
                "namespace GenericParameterIdentity42;\n" +
                "public class GenuineChanges\n" +
                "{\n" +
                "    public void ByType(int x) { }\n" +
                "    public void ByQualifiedType(N.T x) { }\n" +
                "    public T ByArity<T>(T x) => x;\n" +
                "}\n");
            Git(root, "add -A");
            Git(root, "commit -q -m generic-parameter-identity-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.WriteAllText(renameFullPath,
                "namespace GenericParameterIdentity42;\n" +
                "public static class N\n" +
                "{\n" +
                "    public sealed class T { }\n" +
                "    public sealed class U { }\n" +
                "}\n" +
                "public class C<U>\n" +
                "{\n" +
                "    public U FromContainer(U x) => x;\n" +
                "    public void Qualified(N.T concrete, U generic) { }\n" +
                "}\n" +
                "public class MethodContainer\n" +
                "{\n" +
                "    public U M<U>(U x) => x;\n" +
                "}\n");
            File.WriteAllText(distinctFullPath,
                "namespace GenericParameterIdentity42;\n" +
                "public class GenuineChanges\n" +
                "{\n" +
                "    public void ByType(long x) { }\n" +
                "    public void ByQualifiedType(N.U x) { }\n" +
                "    public T ByArity<T, U>(T x) => x;\n" +
                "}\n");
            m.RequestRefresh(new[] { renamePath, distinctPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                List<SymbolHit> renamed = q.Outline(renamePath);
                List<SymbolHit> distinct = q.Outline(distinctPath);
                return renamed.Any(symbol => symbol.Signature == "class C<U>") &&
                       renamed.Any(symbol =>
                           symbol.Signature == "U FromContainer(U x)") &&
                       renamed.Any(symbol =>
                           symbol.Signature == "void Qualified(N.T concrete, U generic)") &&
                       renamed.Any(symbol => symbol.Signature == "U M<U>(U x)") &&
                       distinct.Any(symbol => symbol.Signature == "void ByType(long x)") &&
                       distinct.Any(symbol =>
                           symbol.Signature == "void ByQualifiedType(N.U x)") &&
                       distinct.Any(symbol => symbol.Signature == "T ByArity<T, U>(T x)");
            }, 20_000), "index did not reflect the generic identity changes");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("symbols", out _), "review_pack with symbols");
            Assert.Contains(pack.GetProperty("symbols").EnumerateArray(), item =>
                item.GetProperty("symbol").GetProperty("name").GetString() == "FromContainer");
            Assert.Contains(pack.GetProperty("symbols").EnumerateArray(), item =>
                item.GetProperty("symbol").GetProperty("name").GetString() == "M");

            JsonElement formerFiles = pack.GetProperty("formerSymbols");
            Assert.DoesNotContain(formerFiles.EnumerateArray(), file =>
                file.GetProperty("path").GetString() == renamePath);
            JsonElement distinctFormerFile = Assert.Single(formerFiles.EnumerateArray(), file =>
                file.GetProperty("path").GetString() == distinctPath);
            List<JsonElement> former = distinctFormerFile.GetProperty("formerSymbols")
                .EnumerateArray().ToList();
            Assert.Contains(former, symbol =>
                symbol.GetProperty("signature").GetString() == "void ByType(int x)");
            Assert.Contains(former, symbol =>
                symbol.GetProperty("signature").GetString() ==
                "void ByQualifiedType(N.T x)");
            Assert.Contains(former, symbol =>
                symbol.GetProperty("signature").GetString() == "T ByArity<T>(T x)");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void UnindexedWholeFileUsesAnUnknownRangeInsteadOfInventingIntMaxLines()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-unindexed-range").FullName);
        try
        {
            WriteReviewRepo(root);
            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            // This test RACES the watcher: the file must still be unindexed when the pack
            // runs, and once a debounced refresh indexes it the classification flips to
            // file_level forever - so each retry attempt recreates a FRESH file (new name)
            // and the pack is scoped to that one path, restarting the race it usually wins
            // (write-to-pack is milliseconds; the debounce is hundreds).
            int freshAttempt = 0;
            string freshName = "";
            var pack = SemanticRetry.ParseWithRetry(
                () =>
                {
                    freshAttempt++;
                    freshName = $"Fresh{freshAttempt}.cs";
                    File.WriteAllText(Path.Combine(root, freshName),
                        $"namespace Fresh42; public class FreshType42_{freshAttempt} {{ }}\n");
                    return tools.ReviewPack(paths: freshName, maxBytes: 24576);
                },
                j => j.TryGetProperty("unmappedChanges", out var u)
                     && u.GetProperty("items").EnumerateArray().Any(i =>
                         i.GetProperty("reason").GetString() == "whole_file_unindexed"),
                "review_pack with a whole_file_unindexed note");
            JsonElement item = Assert.Single(pack.GetProperty("unmappedChanges")
                .GetProperty("items").EnumerateArray());
            Assert.Equal("whole_file_unindexed", item.GetProperty("reason").GetString());
            Assert.Equal(1, item.GetProperty("start").GetInt32());
            Assert.False(item.TryGetProperty("end", out _));
            Assert.DoesNotContain("2147483646", pack.GetRawText(), StringComparison.Ordinal);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void NamespaceClassificationStopsBeforeLoadingAnOversizedIndexedFile()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-namespace-budget").FullName);
        try
        {
            WriteReviewRepo(root);
            const string path = "Lib/HugeNamespaceBudget.cs";
            string fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            string padding = new string('x', (512 * 1024) + 64);
            File.WriteAllText(fullPath,
                "global using System;\nnamespace NamespaceBudget42;\n" +
                "public class HugeNamespaceBudget42 { }\n/*" + padding + "*/\n");
            Git(root, "add -A");
            Git(root, "commit -q -m namespace-budget-baseline");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.WriteAllText(fullPath,
                "global using System.Text;\nnamespace NamespaceBudget42;\n" +
                "public class HugeNamespaceBudget42 { }\n/*" + padding + "*/\n");
            m.RequestRefresh(new[] { path });
            // n7ly: the old gate keyed on invariant "HugeNamespaceBudget42" — wait for the edit.
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return (q.ContentByPath(path) ?? "").Contains("global using System.Text;");
            }, 60_000), "index did not reflect the global-using edit");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("namespaceAnalysisCoverage", out _), "review_pack with namespaceAnalysisCoverage");
            JsonElement coverage = pack.GetProperty("namespaceAnalysisCoverage");
            Assert.Equal(1, coverage.GetProperty("requested").GetInt32());
            Assert.Equal(0, coverage.GetProperty("parsed").GetInt32());
            Assert.Equal(512 * 1024,
                coverage.GetProperty("perFileCharLimit").GetInt32());
            Assert.True(coverage.GetProperty("budgetHit").GetBoolean());
            Assert.Contains(pack.GetProperty("unmappedChanges").GetProperty("items")
                .EnumerateArray(), item => item.GetProperty("reason").GetString() == "file_level");
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.namespace_analysis_budget");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ProjectOwnershipFallbackIncludesEveryUnremovedDefaultAncestor()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-owner-ancestors").FullName);
        try
        {
            WriteReviewRepo(root);
            string outer = Path.Combine(root, "Outer");
            string inner = Path.Combine(outer, "Inner");
            Directory.CreateDirectory(inner);
            File.WriteAllText(Path.Combine(outer, "Outer.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>" +
                "<ItemGroup><Compile Remove=\"Inner/Moved.cs\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(inner, "Inner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            const string oldPath = "Outer/Inner/Type.cs";
            const string newPath = "Outer/Inner/Moved.cs";
            File.WriteAllText(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                "namespace OwnerAncestor42; public class SharedByAncestors42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m owner-ancestor-baseline");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.Move(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, newPath.Replace('/', Path.DirectorySeparatorChar)));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "SharedByAncestors42");
            }, 20_000), "index did not reflect the default-ancestor move");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("changedFiles", out _), "review_pack with changedFiles");
            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == oldPath);
            JsonElement coverage = pack.GetProperty("projectOwnershipFallbackCoverage");
            Assert.True(coverage.GetProperty("parsed").GetInt32() >= 2);
            Assert.False(coverage.TryGetProperty("budgetHit", out _));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ProjectOwnershipFallbackKeepsExplicitReincludeAfterMatchingRemove()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-owner-reinclude").FullName);
        try
        {
            WriteReviewRepo(root);
            string project = Path.Combine(root, "Owner");
            string shared = Path.Combine(root, "Shared");
            Directory.CreateDirectory(project);
            Directory.CreateDirectory(shared);
            File.WriteAllText(Path.Combine(project, "Owner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Remove=\"../Shared/*.cs\" />" +
                "<Compile Include=\"../Shared/*.cs\" /></ItemGroup></Project>");
            const string oldPath = "Shared/Type.cs";
            const string newPath = "Shared/Moved.cs";
            File.WriteAllText(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                "namespace OwnerReinclude42; public class ExplicitReinclude42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m owner-reinclude-baseline");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.Move(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, newPath.Replace('/', Path.DirectorySeparatorChar)));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.ProjectsContaining(newPath).Any(projectRow =>
                           projectRow.Name == "Owner");
            }, 20_000), "index did not retain the explicit reinclude owner");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("changedFiles", out _), "review_pack with changedFiles");
            Assert.Equal(0, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            Assert.False(pack.TryGetProperty("deletedFiles", out _));
            Assert.Contains(pack.GetProperty("movedFiles").GetProperty("items")
                .EnumerateArray(), item => item.GetProperty("from").GetString() == oldPath &&
                                          item.GetProperty("to").GetString() == newPath);
        }
        finally { Cleanup(root); }
    }
}
