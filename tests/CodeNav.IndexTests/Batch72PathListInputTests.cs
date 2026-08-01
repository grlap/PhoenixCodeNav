using System.Text;
using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

public class Batch72PathListInputTests
{
    [Fact]
    public void RefreshIndexAcceptsExactJsonArrayPathsAndRejectsMalformedOrOversizedLists()
    {
        string root = Directory.CreateTempSubdirectory("codenav-51dh-refresh-paths").FullName;
        try
        {
            string projectDirectory = Path.Combine(root, "P");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "P.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(projectDirectory, "Comma,Path.cs"),
                "namespace P; public class CommaPath72 { }");
            File.WriteAllText(Path.Combine(projectDirectory, "Other.cs"),
                "namespace P; public class Other72 { }");

            string databasePath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, databasePath);
            using var manager = new IndexManager(root, databasePath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, TimeSpan.FromSeconds(30)),
                "index did not become queryable");
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);

            JsonElement jsonArray = Parse(tools.RefreshIndex(paths:
                JsonSerializer.Serialize(new[] { "P/Comma,Path.cs" })));
            Assert.True(jsonArray.GetProperty("queued").GetBoolean());
            Assert.Equal("1 paths", jsonArray.GetProperty("scope").GetString());

            JsonElement csv = Parse(tools.RefreshIndex(paths: "P/Other.cs,P/Other.cs"));
            Assert.True(csv.GetProperty("queued").GetBoolean());
            Assert.Equal("2 paths", csv.GetProperty("scope").GetString());

            JsonElement malformed = Parse(tools.RefreshIndex(
                paths: "[\"P/Comma,Path.cs\",]"));
            Assert.Equal("bad_request", malformed.GetProperty("error").GetString());

            var invalidExactPaths = new List<string>
            {
                JsonSerializer.Serialize(new[] { "/P/Other.cs" }),
                "/P/Other.cs",
                JsonSerializer.Serialize(new[] { @"C:\P\Other.cs" }),
                "C:/P/Other.cs",
                JsonSerializer.Serialize(new[] { @"\\server\share\Other.cs" }),
                "//server/share/Other.cs",
                JsonSerializer.Serialize(new[] { "P/../Other.cs" }),
                "../P/Other.cs",
                JsonSerializer.Serialize(new[] { "P/Bad\u0001.cs" }),
                "P/Bad\u0001.cs",
                "   ",
            };
            if (OperatingSystem.IsWindows())
            {
                invalidExactPaths.Add(JsonSerializer.Serialize(
                    new[] { "P/File.cs:secret.cs" }));
                invalidExactPaths.Add("P/File.cs:secret.cs");
            }
            foreach (string invalidPaths in invalidExactPaths)
            {
                JsonElement refreshError = Parse(tools.RefreshIndex(paths: invalidPaths));
                Assert.Equal("bad_request", refreshError.GetProperty("error").GetString());

                JsonElement reviewError = Parse(tools.ReviewPack(paths: invalidPaths));
                Assert.Equal("bad_request", reviewError.GetProperty("error").GetString());
            }

            string oversized = JsonSerializer.Serialize(Enumerable.Range(0, 257)
                .Select(index => $"P/F{index:D3}.cs"));
            JsonElement tooMany = Parse(tools.RefreshIndex(paths: oversized));
            Assert.Equal("bad_request", tooMany.GetProperty("error").GetString());
            Assert.Contains("at most 256", tooMany.GetProperty("detail").GetString(),
                StringComparison.Ordinal);

            string oversizedCharacters = "P/" +
                new string('x', NavigationTools.PathListInputByteLimit);
            JsonElement refreshTooLarge = Parse(tools.RefreshIndex(paths: oversizedCharacters));
            Assert.Equal("bad_request", refreshTooLarge.GetProperty("error").GetString());
            Assert.Contains("at most 65536", refreshTooLarge.GetProperty("detail").GetString(),
                StringComparison.Ordinal);
            JsonElement reviewTooLarge = Parse(tools.ReviewPack(paths: oversizedCharacters));
            Assert.Equal("bad_request", reviewTooLarge.GetProperty("error").GetString());

            int multibyteCharacters = (NavigationTools.PathListInputByteLimit - 2) / 3;
            int asciiRemainder = NavigationTools.PathListInputByteLimit - 2 -
                (multibyteCharacters * 3);
            string atUtf8Limit = "P/" + new string('界', multibyteCharacters) +
                new string('a', asciiRemainder);
            Assert.Equal(NavigationTools.PathListInputByteLimit,
                Encoding.UTF8.GetByteCount(atUtf8Limit));
            Assert.True(NavigationTools.TryParsePathList(atUtf8Limit, 256,
                out List<string> atLimitPaths, out string? atLimitDetail), atLimitDetail);
            Assert.Equal(atUtf8Limit, Assert.Single(atLimitPaths));
            Assert.False(NavigationTools.TryParsePathList(atUtf8Limit + "a", 256,
                out _, out string? overUtf8Detail));
            Assert.Contains("UTF-8 bytes", overUtf8Detail, StringComparison.Ordinal);

            Assert.False(NavigationTools.IsExactWorkspaceRelativePath(
                "P/File.cs:secret.cs", windowsPathRules: true));
            Assert.True(NavigationTools.IsExactWorkspaceRelativePath(
                "P/File.cs:secret.cs", windowsPathRules: false));

            if (!OperatingSystem.IsWindows())
            {
                Assert.True(NavigationTools.TryParsePathList(@"P/Literal\Backslash.cs", 256,
                    out List<string> literalBackslash, out string? literalDetail), literalDetail);
                Assert.Equal(@"P/Literal\Backslash.cs", Assert.Single(literalBackslash));
            }
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }
}
