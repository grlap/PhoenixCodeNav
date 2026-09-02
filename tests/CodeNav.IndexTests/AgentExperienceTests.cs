using System.Text.Json;
using System.ComponentModel;
using System.Reflection;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

public sealed class AgentExperienceTests : IClassFixture<AgentExperienceFixture>
{
    private readonly AgentExperienceFixture _fixture;

    public AgentExperienceTests(AgentExperienceFixture fixture) => _fixture = fixture;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ListInputsAndConfiguredQueryScopeAreAgentFriendly()
    {
        var tools = new NavigationTools(_fixture.Manager, _fixture.Semantic, "first_party");

        JsonElement csv = Parse(tools.SearchSymbol("IWorker", kinds: "interface", match: "exact"));
        JsonElement json = Parse(tools.SearchSymbol("IWorker", kinds: "[\"interface\"]", match: "exact"));
        Assert.Equal(
            csv.GetProperty("symbols").EnumerateArray().Select(item => item.GetProperty("symbolId").GetString()),
            json.GetProperty("symbols").EnumerateArray().Select(item => item.GetProperty("symbolId").GetString()));

        JsonElement source = Parse(tools.SourceContext(
            "Core/Agent.cs", "[\"1-2\",\"8\"]", contextLines: 0));
        Assert.Equal(2, source.GetProperty("spans").GetArrayLength());
        JsonElement malformed = Parse(tools.SearchSymbol(
            "IWorker", kinds: "[\"interface\",1]", match: "exact"));
        Assert.Equal("bad_request", malformed.GetProperty("error").GetString());
        Assert.Equal("bad_request", Parse(tools.SearchSymbol(
            "IWorker", kinds: ",", match: "exact")).GetProperty("error").GetString());
        Assert.Equal("bad_request", Parse(tools.SearchSymbol(
            "IWorker", kinds: "[]", match: "exact")).GetProperty("error").GetString());
        Assert.Equal("bad_request", Parse(tools.SearchSymbol(
            "IWorker", kinds: " ", match: "exact")).GetProperty("error").GetString());
        JsonElement emptyKinds = Parse(tools.SearchSymbol(
            "IWorker", kinds: "", match: "exact"));
        Assert.Single(emptyKinds.GetProperty("symbols").EnumerateArray());
        Assert.Equal("bad_request", Parse(tools.SourceContext(
            "Core/Agent.cs", " ")).GetProperty("error").GetString());
        Assert.Equal("bad_request", Parse(tools.SourceContext(
            "Core/Agent.cs", range: " ")).GetProperty("error").GetString());

        JsonElement explicitFirstParty = Parse(_fixture.Tools.SearchSymbol(
            "VendorOnly", match: "exact", includeGenerated: true,
            queryScope: "first_party"));
        Assert.Empty(explicitFirstParty.GetProperty("symbols").EnumerateArray());
        Assert.Equal("first_party",
            explicitFirstParty.GetProperty("queryScope").GetProperty("applied").GetString());
        JsonElement undocumentedScopeAlias = Parse(tools.SearchSymbol(
            "IWorker", match: "exact", queryScope: "first-party"));
        Assert.Equal("bad_request", undocumentedScopeAlias.GetProperty("error").GetString());
        foreach (JsonElement response in new[]
                 {
                     Parse(tools.SearchSymbol("IWorker", match: "exact", queryScope: "")),
                     Parse(tools.SearchText("IWorker", queryScope: "")),
                     Parse(tools.FindFile("Agent.cs", queryScope: "")),
                 })
        {
            Assert.False(response.TryGetProperty("error", out _), response.ToString());
            Assert.True(response.GetProperty("queryScope")
                .GetProperty("defaultApplied").GetBoolean());
        }
        foreach (JsonElement response in new[]
                 {
                     Parse(tools.SearchSymbol("IWorker", match: "exact", queryScope: " ")),
                     Parse(tools.SearchText("IWorker", queryScope: " ")),
                     Parse(tools.FindFile("Agent.cs", queryScope: " ")),
                 })
        {
            Assert.Equal("bad_request", response.GetProperty("error").GetString());
            Assert.Equal("queryScope", response.GetProperty("field").GetString());
            Assert.Equal(new[] { "default", "all", "first_party" },
                response.GetProperty("validValues").EnumerateArray()
                    .Select(value => value.GetString()).ToArray());
        }
        foreach (string methodName in new[]
                 {
                     nameof(NavigationTools.SearchSymbol),
                     nameof(NavigationTools.SearchText),
                 })
        {
            string[] parameters = typeof(NavigationTools).GetMethod(methodName)!
                .GetParameters().Select(parameter => parameter.Name!).ToArray();
            Assert.Contains("queryScope", parameters);
            Assert.DoesNotContain("firstPartyOnly", parameters);
        }

        JsonElement defaultMiss = Parse(tools.SearchSymbol(
            "VendorOnly", match: "exact", includeGenerated: true));
        Assert.Empty(defaultMiss.GetProperty("symbols").EnumerateArray());
        Assert.Equal("first_party",
            defaultMiss.GetProperty("queryScope").GetProperty("applied").GetString());
        Assert.True(defaultMiss.GetProperty("queryScope").GetProperty("defaultApplied").GetBoolean());
        Assert.Equal("filtered_out",
            defaultMiss.GetProperty("zeroResult").GetProperty("reason").GetString());

        JsonElement all = Parse(tools.SearchSymbol(
            "VendorOnly", match: "exact", includeGenerated: true, queryScope: "all"));
        Assert.Single(all.GetProperty("symbols").EnumerateArray());
        Assert.Equal("all", all.GetProperty("queryScope").GetProperty("applied").GetString());
        Assert.False(all.GetProperty("queryScope").GetProperty("defaultApplied").GetBoolean());

        JsonElement textDefault = Parse(tools.SearchText("VendorOnly"));
        Assert.Equal(0, textDefault.GetProperty("preciseCount").GetInt32());
        Assert.Equal("first_party",
            textDefault.GetProperty("queryScope").GetProperty("applied").GetString());
        JsonElement textAll = Parse(tools.SearchText("VendorOnly", queryScope: "all"));
        Assert.True(textAll.GetProperty("preciseCount").GetInt32() > 0);
        JsonElement fileDefault = Parse(tools.FindFile("Vendor.cs"));
        Assert.Empty(fileDefault.GetProperty("files").EnumerateArray());
        JsonElement fileAll = Parse(tools.FindFile("Vendor.cs", queryScope: "all"));
        Assert.Single(fileAll.GetProperty("files").EnumerateArray());

        JsonElement capabilities = Parse(tools.ServerCapabilities());
        Assert.Equal("first_party",
            capabilities.GetProperty("queryDefaults").GetProperty("scope").GetString());
        Assert.Equal("ids", capabilities.GetProperty("featureSummaryMode").GetString());
        Assert.False(capabilities.TryGetProperty("featuresCompacted", out _));
        Assert.False(capabilities.TryGetProperty("featureSummariesReturned", out _));
        Assert.Contains("agent-request-patch-recovery",
            NavigationTools.CapabilityFeatureIds(_fixture.Manager.Health()));

        JsonElement oneCharacterMiss = Parse(tools.SearchSymbol("Z", match: "exact"));
        Assert.Equal("symbol_not_found",
            oneCharacterMiss.GetProperty("zeroResult").GetProperty("reason").GetString());
        Assert.False(oneCharacterMiss.GetProperty("zeroResult")
            .TryGetProperty("suggestionCoverage", out _));

        JsonElement unsupportedDocument = Parse(tools.SearchSymbol(
            "Anything", match: "exact", pathGlob: "README.md", lang: "csharp"));
        Assert.Equal("unsupported_language",
            unsupportedDocument.GetProperty("error").GetString());

        JsonElement fsharpScopedCsharp = Parse(tools.SearchSymbol(
            "FsOnly", match: "exact", pathGlob: "CollisionFs/**", lang: "csharp"));
        Assert.False(fsharpScopedCsharp.TryGetProperty("error", out _));
        Assert.Empty(fsharpScopedCsharp.GetProperty("symbols").EnumerateArray());
        Assert.Contains(fsharpScopedCsharp.GetProperty("zeroResult")
                .GetProperty("effectiveScope").GetProperty("availableLanguages")
                .EnumerateArray(),
            item => item.GetString() == "fs");

        var boundedTools = new NavigationTools(_fixture.Manager, _fixture.Semantic)
        {
            TestOnlySearchSymbolResponseMaxBytes = 4096,
        };
        string boundedRaw = boundedTools.SearchSymbol(
            "ExMissing", kinds: "class", match: "exact", includeGenerated: true,
            pathGlob: "Core/**", excludePath: "Core/Never/**", @namespace: "Agent",
            limit: 40, queryScope: "all");
        Assert.True(Json.Utf8Bytes(boundedRaw) <= 4096, boundedRaw);
        JsonElement bounded = Parse(boundedRaw);
        JsonElement boundedZero = bounded.GetProperty("zeroResult");
        Assert.Equal("symbol_not_found", boundedZero.GetProperty("reason").GetString());
        Assert.True(boundedZero.GetProperty("suggestionCoverage")
            .GetProperty("budgetTruncated").GetBoolean());
        Assert.InRange(boundedZero.GetProperty("suggestions").GetArrayLength(), 1, 39);
        JsonElement retryArguments = boundedZero.GetProperty("retry").GetProperty("arguments");
        Assert.Equal("all", retryArguments.GetProperty("queryScope").GetString());
        Assert.Equal("class", retryArguments.GetProperty("kinds").GetString());
        Assert.True(retryArguments.GetProperty("includeGenerated").GetBoolean());
        Assert.Equal("Core/**", retryArguments.GetProperty("pathGlob").GetString());
        Assert.Equal("Core/Never/**", retryArguments.GetProperty("excludePath").GetString());
        Assert.Equal("Agent", retryArguments.GetProperty("namespace").GetString());
    }

    [Fact]
    public void ProjectSelectorsPreservePhysicalAmbiguityAndDirectionAliases()
    {
        var tools = _fixture.Tools;

        JsonElement ambiguous = Parse(tools.ProjectGraph("Shared.Identity"));
        Assert.Equal("project_ambiguous", ambiguous.GetProperty("error").GetString());
        Assert.Equal(2, ambiguous.GetProperty("totalMatches").GetInt32());
        Assert.Equal(2, ambiguous.GetProperty("matches").GetArrayLength());

        JsonElement projectNameWins = Parse(tools.ProjectGraph("Project.File"));
        Assert.Equal("Stem/Project.File.csproj",
            projectNameWins.GetProperty("root").GetProperty("path").GetString());
        JsonElement shadowed = projectNameWins.GetProperty("projectSelectorResolution");
        Assert.Equal(1, shadowed.GetProperty("shadowedMatchCount").GetInt32());
        Assert.Equal("assemblyName", shadowed.GetProperty("shadowedMatches")[0]
            .GetProperty("matchedBy").GetString());

        JsonElement explicitCs = Parse(tools.ProjectGraph("Foo.csproj"));
        Assert.Equal("CollisionCs/Foo.csproj",
            explicitCs.GetProperty("root").GetProperty("path").GetString());
        JsonElement explicitFs = Parse(tools.ProjectGraph("Foo.fsproj"));
        Assert.Equal("CollisionFs/Foo.fsproj",
            explicitFs.GetProperty("root").GetProperty("path").GetString());
        JsonElement stemCollision = Parse(tools.ProjectGraph("Foo"));
        Assert.Equal("project_ambiguous", stemCollision.GetProperty("error").GetString());
        Assert.Equal(2, stemCollision.GetProperty("totalMatches").GetInt32());

        JsonElement exact = Parse(tools.ProjectGraph("DuplicateA/Alpha.csproj"));
        Assert.Equal("DuplicateA/Alpha.csproj",
            exact.GetProperty("root").GetProperty("path").GetString());

        JsonElement missing = Parse(tools.ProjectGraph("Agent"));
        Assert.Equal("project_not_found", missing.GetProperty("error").GetString());
        Assert.True(missing.GetProperty("suggestionCoverage").GetProperty("probeLimit").GetInt32() > 0);
        Assert.NotEmpty(missing.GetProperty("suggestions").EnumerateArray());
        Assert.False(missing.TryGetProperty("retry", out _));
        JsonElement graphRecovery = missing.GetProperty("suggestions")[0]
            .GetProperty("recovery");
        Assert.Equal("project_graph", graphRecovery.GetProperty("tool").GetString());
        Assert.True(graphRecovery.GetProperty("replayOriginalRequest").GetBoolean());
        Assert.Equal("project", graphRecovery.GetProperty("remove")[0].GetString());

        JsonElement missingFrom = Parse(tools.DependencyPath(
            "Agent", "Core/Core.csproj", maxPaths: 7));
        Assert.Equal("project_not_found", missingFrom.GetProperty("error").GetString());
        JsonElement fromRecovery = missingFrom.GetProperty("suggestions")[0]
            .GetProperty("recovery");
        Assert.Equal("dependency_path", fromRecovery.GetProperty("tool").GetString());
        Assert.Equal("fromProject", fromRecovery.GetProperty("remove")[0].GetString());
        Assert.True(fromRecovery.GetProperty("arguments").TryGetProperty(
            "fromProject", out _));

        JsonElement missingTo = Parse(tools.DependencyPath(
            "Consumer/Consumer.csproj", "Agent", maxPaths: 7));
        JsonElement toRecovery = missingTo.GetProperty("suggestions")[0]
            .GetProperty("recovery");
        Assert.Equal("dependency_path", toRecovery.GetProperty("tool").GetString());
        Assert.Equal("toProject", toRecovery.GetProperty("remove")[0].GetString());
        Assert.True(toRecovery.GetProperty("arguments").TryGetProperty(
            "toProject", out _));

        var budgetedSelectorTools = new NavigationTools(_fixture.Manager, _fixture.Semantic)
        {
            TestOnlyProjectSelectorResponseMaxBytes = 4096,
            TestOnlyProjectSelectorResolutionTransform = resolution =>
            {
                if (resolution.Matches.Count != 1) return resolution;
                ProjectRow selected = resolution.Matches[0].Project;
                return resolution with
                {
                    ShadowedMatches = Enumerable.Range(0, 200)
                        .Select(index => new ProjectSelectorMatch(selected with
                        {
                            Id = -1 - index,
                            Name = $"Shadowed.{index:D3}",
                            Path = $"Shadowed/{index:D3}/{new string('x', 1000)}.csproj",
                        }, "assemblyName"))
                        .ToList(),
                };
            },
        };
        string budgetedSuccessRaw = budgetedSelectorTools.ProjectGraph("Project.File");
        Assert.True(Json.Utf8Bytes(budgetedSuccessRaw) <= 4096, budgetedSuccessRaw);
        JsonElement budgetedSuccess = Parse(budgetedSuccessRaw);
        Assert.Equal("Stem/Project.File.csproj",
            budgetedSuccess.GetProperty("root").GetProperty("path").GetString());
        JsonElement budgetedResolution = budgetedSuccess.GetProperty("projectSelectorResolution");
        Assert.Equal(200, budgetedResolution.GetProperty("shadowedMatchCount").GetInt32());
        Assert.InRange(budgetedResolution.GetProperty("shadowedMatchesReturned").GetInt32(), 0, 199);
        Assert.True(budgetedResolution.GetProperty("shadowedMatchesTruncated").GetBoolean());

        string oversizedSelector = new('界', 5000);
        string oversizedSelectorRaw = budgetedSelectorTools.ProjectGraph(oversizedSelector);
        Assert.True(Json.Utf8Bytes(oversizedSelectorRaw) <= 4096, oversizedSelectorRaw);
        JsonElement oversizedSelectorResponse = Parse(oversizedSelectorRaw);
        Assert.Equal("project_not_found", oversizedSelectorResponse.GetProperty("error").GetString());
        Assert.True(oversizedSelectorResponse.GetProperty("selectorTruncated").GetBoolean());
        Assert.Equal(Json.Utf8Bytes(oversizedSelector),
            oversizedSelectorResponse.GetProperty("selectorBytes").GetInt32());

        JsonElement downstream = Parse(tools.ProjectGraph(
            "Consumer/Consumer.csproj", depth: 1, direction: "downstream"));
        JsonElement dependencies = Parse(tools.ProjectGraph(
            "Consumer/Consumer.csproj", depth: 1, direction: "dependencies"));
        Assert.Equal("downstream", dependencies.GetProperty("direction").GetString());
        Assert.Equal(Edges(downstream), Edges(dependencies));

        JsonElement upstream = Parse(tools.ProjectGraph(
            "Core/Core.csproj", depth: 1, direction: "upstream"));
        JsonElement dependents = Parse(tools.ProjectGraph(
            "Core/Core.csproj", depth: 1, direction: "dependents"));
        Assert.Equal("upstream", dependents.GetProperty("direction").GetString());
        Assert.Equal(Edges(upstream), Edges(dependents));
        JsonElement nullDirection = Parse(tools.ProjectGraph(
            "Core/Core.csproj", depth: 1, direction: null!));
        Assert.Equal("both", nullDirection.GetProperty("direction").GetString());

        JsonElement dependencyPath = Parse(tools.DependencyPath(
            "Consumer/Consumer.csproj", "Core/Core.csproj"));
        Assert.True(dependencyPath.GetProperty("found").GetBoolean());
        Assert.Equal("Consumer/Consumer.csproj",
            dependencyPath.GetProperty("fromProjectSelector").GetString());
        string budgetedDependencyRaw = budgetedSelectorTools.DependencyPath(
            "Consumer/Consumer.csproj", "Core/Core.csproj");
        Assert.True(Json.Utf8Bytes(budgetedDependencyRaw) <= 4096,
            budgetedDependencyRaw);
        JsonElement budgetedDependency = Parse(budgetedDependencyRaw);
        Assert.True(budgetedDependency.GetProperty("found").GetBoolean());
        Assert.Equal(200, budgetedDependency.GetProperty("fromProjectSelectorResolution")
            .GetProperty("shadowedMatchCount").GetInt32());
        Assert.Equal(200, budgetedDependency.GetProperty("toProjectSelectorResolution")
            .GetProperty("shadowedMatchCount").GetInt32());

        static string[] Edges(JsonElement response) => response.GetProperty("edges")
            .EnumerateArray()
            .Select(edge => $"{edge.GetProperty("from").GetString()}->{edge.GetProperty("to").GetString()}")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public void ProjectPathSelectorsUseHostCaseSemanticsEndToEnd()
    {
        string database = IndexBuilder.DefaultDbPath(_fixture.Root);
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText =
                """
                INSERT INTO projects(path, dir, name, style, lang, guid, tfms, is_test,
                                     load_status, compile_globs)
                VALUES ('Case/Widget.csproj', 'Case', 'Case.Widget', 'sdk', 'cs', NULL,
                        'net9.0', 0, 'loaded', 0),
                       ('case/widget.csproj', 'case', 'case.widget', 'sdk', 'cs', NULL,
                        'net9.0', 0, 'loaded', 0)
                """;
            insert.ExecuteNonQuery();
        }

        try
        {
            using var queries = new IndexQueries(database);
            ProjectSelectorResolution unixUpper = queries.ProjectsBySelectorForHost(
                "Case/Widget.csproj", caseInsensitivePaths: false);
            ProjectSelectorResolution unixLower = queries.ProjectsBySelectorForHost(
                "case/widget.csproj", caseInsensitivePaths: false);
            Assert.Equal(["Case/Widget.csproj"],
                unixUpper.Matches.Select(match => match.Project.Path));
            Assert.Equal(["case/widget.csproj"],
                unixLower.Matches.Select(match => match.Project.Path));

            ProjectSelectorResolution windows = queries.ProjectsBySelectorForHost(
                "Case/Widget.csproj", caseInsensitivePaths: true);
            Assert.Equal(2, windows.Matches.Count);
        }
        finally
        {
            using SqliteCommand delete = connection.CreateCommand();
            delete.CommandText =
                "DELETE FROM projects WHERE path IN ('Case/Widget.csproj', 'case/widget.csproj')";
            delete.ExecuteNonQuery();
        }
    }

    [Fact]
    public void DocumentationCommentIdsResolveStableDeclarationsAndKinds()
    {
        if (!_fixture.Semantic.FrameworkRefsAvailable) return;
        var tools = _fixture.Tools;
        string[] ids =
        [
            "T:Agent.IWorker",
            "M:Agent.IWorker.Work(System.String)",
            "P:Agent.IWorker.Name",
            "E:Agent.IWorker.Changed",
            "F:Agent.Worker.Field",
            "M:Agent.Worker.#ctor",
            "M:Agent.Worker.op_Addition(Agent.Worker,Agent.Worker)",
        ];
        foreach (string id in ids)
        {
            JsonElement definition = Parse(tools.Definition(
                mode: "semantic", documentationCommentId: id));
            Assert.False(definition.TryGetProperty("error", out _), $"{id}: {definition}");
            Assert.Equal(id, definition.GetProperty("documentationCommentId").GetString());
            Assert.Equal(id,
                definition.GetProperty("symbol").GetProperty("documentationCommentId").GetString());
        }
        JsonElement canonicalEcho = Parse(tools.Definition(
            mode: "semantic", documentationCommentId: "  T:Agent.IWorker  "));
        Assert.Equal("T:Agent.IWorker",
            canonicalEcho.GetProperty("documentationCommentId").GetString());

        JsonElement references = Parse(tools.References(
            mode: "semantic",
            usageKinds: "[\"call\"]",
            documentationCommentId: "M:Agent.IWorker.Work(System.String)"));
        Assert.False(references.TryGetProperty("error", out _), references.ToString());
        Assert.Equal("M:Agent.IWorker.Work(System.String)",
            references.GetProperty("documentationCommentId").GetString());
        Assert.Equal("M:Agent.IWorker.Work(System.String)",
            references.GetProperty("symbol").GetProperty("documentationCommentId").GetString());

        JsonElement implementations = Parse(tools.Implementations(
            documentationCommentId: "T:Agent.IWorker"));
        Assert.False(implementations.TryGetProperty("error", out _), implementations.ToString());
        Assert.Equal("T:Agent.IWorker",
            implementations.GetProperty("documentationCommentId").GetString());
        Assert.Contains(implementations.GetProperty("implementations").EnumerateArray(), item =>
            item.GetProperty("symbol").GetProperty("display").GetString()!.Contains("Worker", StringComparison.Ordinal));

        JsonElement unsupported = Parse(tools.Implementations(
            documentationCommentId: "M:Agent.Worker.op_Addition(Agent.Worker,Agent.Worker)"));
        Assert.Equal("unsupported_symbol_kind", unsupported.GetProperty("error").GetString());
        JsonElement malformed = Parse(tools.Definition(documentationCommentId: "M:"));
        Assert.Equal("bad_request", malformed.GetProperty("error").GetString());
        JsonElement[] whitespaceIds =
        [
            Parse(tools.Definition(name: "Worker", documentationCommentId: " ")),
            Parse(tools.References(name: "Work", documentationCommentId: " ")),
            Parse(tools.Implementations(name: "IWorker", documentationCommentId: " ")),
        ];
        Assert.All(whitespaceIds, response =>
        {
            Assert.Equal("bad_request", response.GetProperty("error").GetString());
            Assert.Equal("documentationCommentId", response.GetProperty("field").GetString());
        });
        Assert.False(Parse(tools.Definition(name: "Worker", documentationCommentId: ""))
            .TryGetProperty("error", out _));
        Assert.False(Parse(tools.References(name: "Work", documentationCommentId: ""))
            .TryGetProperty("error", out _));
        Assert.False(Parse(tools.Implementations(name: "IWorker", documentationCommentId: ""))
            .TryGetProperty("error", out _));
        JsonElement unsupportedPrefix = Parse(tools.Definition(documentationCommentId: "N:Agent"));
        Assert.Equal("unsupported_documentation_id_kind",
            unsupportedPrefix.GetProperty("error").GetString());
        JsonElement ambiguous = Parse(tools.Definition(
            documentationCommentId: "T:Shared.Duplicate"));
        Assert.Equal("symbol_ambiguous", ambiguous.GetProperty("error").GetString());
        Assert.True(ambiguous.GetProperty("candidateCount").GetInt32() >= 2);
        JsonElement ambiguousCandidate = ambiguous.GetProperty("candidates")[0];
        Assert.False(string.IsNullOrWhiteSpace(
            ambiguousCandidate.GetProperty("project").GetString()));
        Assert.True(ambiguousCandidate.GetProperty("position").GetProperty("column").GetInt32() > 0);
        Assert.Equal("semantic", ambiguousCandidate.GetProperty("recovery")
            .GetProperty("arguments").GetProperty("mode").GetString());
        Assert.False(ambiguous.TryGetProperty("noteId", out _));

        const string secondSameLineOverload = "M:Agent.Worker.Same(System.String)";
        JsonElement sameLine = Parse(tools.Definition(
            mode: "semantic", documentationCommentId: secondSameLineOverload));
        Assert.False(sameLine.TryGetProperty("error", out _), sameLine.ToString());
        Assert.Equal(secondSameLineOverload,
            sameLine.GetProperty("symbol").GetProperty("documentationCommentId").GetString());
        Assert.Equal(secondSameLineOverload,
            sameLine.GetProperty("documentationCommentId").GetString());

        JsonElement invalidDefinitionMode = Parse(tools.Definition(
            mode: "mystery", documentationCommentId: "T:Agent.IWorker"));
        Assert.Equal("bad_request", invalidDefinitionMode.GetProperty("error").GetString());
        Assert.Equal("mode", invalidDefinitionMode.GetProperty("field").GetString());
        JsonElement indexedReferences = Parse(tools.References(
            mode: "indexed", documentationCommentId: "T:Agent.IWorker"));
        Assert.Equal("semantic_required", indexedReferences.GetProperty("error").GetString());

        JsonElement noSeed = Parse(tools.Definition(
            documentationCommentId: "T:Missing.Nowhere"));
        Assert.Equal("symbol_not_found", noSeed.GetProperty("error").GetString());
        Assert.Equal("no_indexed_seed_declaration", noSeed.GetProperty("reason").GetString());
        Assert.Equal("indexed", noSeed.GetProperty("meta").GetProperty("confidence").GetString());
        JsonElement noOwner = Parse(tools.Definition(
            documentationCommentId: "T:Orphan.Loose"));
        Assert.Equal("symbol_not_found", noOwner.GetProperty("error").GetString());
        Assert.Equal("no_csharp_compile_owner", noOwner.GetProperty("reason").GetString());
        Assert.Equal("indexed", noOwner.GetProperty("meta").GetProperty("confidence").GetString());
        JsonElement compilerMiss = Parse(tools.Definition(
            documentationCommentId: "M:Agent.IWorker.Missing"));
        Assert.Equal("symbol_not_found", compilerMiss.GetProperty("error").GetString());
        Assert.Equal("documentation_id_not_found", compilerMiss.GetProperty("reason").GetString());
        Assert.Equal("exact", compilerMiss.GetProperty("meta").GetProperty("confidence").GetString());

        var incompleteTools = new NavigationTools(_fixture.Manager, _fixture.Semantic)
        {
            TestOnlyDocumentationIdResolutionTransform = result => result with
            {
                Coverage = result.Coverage with
                {
                    CompilerScanned = false,
                    SkippedProjects = new[] { "Skipped/Unavailable.csproj" },
                },
            },
        };
        JsonElement incomplete = Parse(incompleteTools.Definition(
            documentationCommentId: "T:Agent.IWorker"));
        Assert.Equal("documentation_id_coverage_incomplete",
            incomplete.GetProperty("error").GetString());
        Assert.True(incomplete.GetProperty("partial").GetBoolean());
        Assert.Equal(1, incomplete.GetProperty("candidateCount").GetInt32());
        Assert.Single(incomplete.GetProperty("candidates").EnumerateArray());
        Assert.False(incomplete.TryGetProperty("symbol", out _));
        Assert.Contains(incomplete.GetProperty("documentationIdCoverage")
                .GetProperty("skippedProjects").EnumerateArray(),
            item => item.GetString() == "Skipped/Unavailable.csproj");
        Assert.False(incomplete.TryGetProperty("retryRecommended", out _));

        var incompleteAmbiguityTools = new NavigationTools(_fixture.Manager, _fixture.Semantic)
        {
            TestOnlyDocumentationIdResolutionTransform = result =>
            {
                if (result.Matches is not { Count: 1 }) return result;
                DocumentationIdResolution first = result.Matches[0];
                DocumentationIdResolution second = first with
                {
                    ProjectName = "Incomplete.Second",
                    NavigationPath = "Linked/Second.cs",
                    NavigationLine = 7,
                    NavigationColumn = 9,
                    Declaration = first.Declaration with { Assembly = "Incomplete.Second" },
                };
                return result with
                {
                    Matches = [first, second],
                    Coverage = result.Coverage with
                    {
                        CompilerScanned = false,
                        SkippedProjects = ["Skipped/Third.csproj"],
                    },
                };
            },
        };
        JsonElement incompleteAmbiguity = Parse(incompleteAmbiguityTools.Definition(
            documentationCommentId: "T:Agent.IWorker"));
        Assert.Equal("symbol_ambiguous",
            incompleteAmbiguity.GetProperty("error").GetString());
        Assert.True(incompleteAmbiguity.GetProperty("partial").GetBoolean());
        Assert.Equal("documentation_id_coverage_incomplete",
            incompleteAmbiguity.GetProperty("partialReason").GetString());
        Assert.True(incompleteAmbiguity.GetProperty("candidateCountIsLowerBound").GetBoolean());
        Assert.Equal(2, incompleteAmbiguity.GetProperty("candidateCount").GetInt32());
        AssertReplayPatch(incompleteAmbiguity, "definition", "includeBody");

        JsonElement referenceAmbiguity = Parse(incompleteAmbiguityTools.References(
            documentationCommentId: "M:Agent.IWorker.Work(System.String)",
            usageKinds: "call", includeTests: false, publicConsumersOnly: true));
        AssertReplayPatch(referenceAmbiguity, "references", "usageKinds");
        JsonElement implementationAmbiguity = Parse(incompleteAmbiguityTools.Implementations(
            documentationCommentId: "T:Agent.IWorker", maxProjects: 1));
        AssertReplayPatch(implementationAmbiguity, "implementations", "maxProjects");

        var sharedPositionTools = new NavigationTools(_fixture.Manager, _fixture.Semantic)
        {
            TestOnlyDocumentationIdResolutionTransform = result =>
            {
                if (result.Matches is not { Count: 1 }) return result;
                DocumentationIdResolution first = result.Matches[0];
                DocumentationIdResolution second = first with
                {
                    ProjectName = "Linked.Second",
                    Declaration = first.Declaration with { Assembly = "Linked.Second" },
                };
                return result with { Matches = new List<DocumentationIdResolution> { first, second } };
            },
        };
        JsonElement sharedPosition = Parse(sharedPositionTools.Definition(
            documentationCommentId: "T:Agent.IWorker"));
        Assert.Equal("symbol_ambiguous", sharedPosition.GetProperty("error").GetString());
        Assert.Equal(NoteIds.DocumentationIdPositionShared,
            sharedPosition.GetProperty("noteId").GetString());
        Assert.Equal(2, sharedPosition.GetProperty("candidates").GetArrayLength());
        Assert.All(sharedPosition.GetProperty("candidates").EnumerateArray(), candidate =>
            Assert.False(candidate.TryGetProperty("recovery", out _)));
        JsonElement sharedImplementation = Parse(sharedPositionTools.Implementations(
            documentationCommentId: "T:Agent.IWorker"));
        Assert.False(sharedImplementation.GetProperty("candidates")[0]
            .TryGetProperty("recovery", out _));

        var budgetedCoverageTools = new NavigationTools(_fixture.Manager, _fixture.Semantic)
        {
            TestOnlyDocumentationIdResolutionTransform = result => result with
            {
                Coverage = result.Coverage with
                {
                    CompilerScanned = false,
                    SkippedProjects = Enumerable.Range(0, 200)
                        .Select(index => $"Skipped/{index:D3}/{new string('x', 1000)}.csproj")
                        .ToArray(),
                },
            },
        };
        string budgetedCoverageRaw = budgetedCoverageTools.Definition(
            documentationCommentId: "T:Agent.IWorker");
        Assert.True(Json.Utf8Bytes(budgetedCoverageRaw) <= Json.HardBudgetBytes,
            budgetedCoverageRaw);
        JsonElement budgetedCoverage = Parse(budgetedCoverageRaw);
        Assert.Single(budgetedCoverage.GetProperty("candidates").EnumerateArray());
        JsonElement coverage = budgetedCoverage.GetProperty("documentationIdCoverage");
        Assert.Equal(200, coverage.GetProperty("skippedProjectCount").GetInt32());
        Assert.InRange(coverage.GetProperty("skippedProjectsReturned").GetInt32(), 0, 199);
        Assert.True(coverage.GetProperty("skippedProjectsTruncated").GetBoolean());

        var oversizedCandidateTools = new NavigationTools(_fixture.Manager, _fixture.Semantic)
        {
            TestOnlyDocumentationIdResolutionTransform = result =>
            {
                if (result.Matches is not { Count: 1 }) return result;
                string huge = new('界', Json.HardBudgetBytes);
                DocumentationIdResolution first = result.Matches[0] with
                {
                    ProjectName = huge,
                    NavigationPath = huge,
                    Declaration = result.Matches[0].Declaration with { Assembly = huge },
                };
                DocumentationIdResolution second = first with
                {
                    ProjectName = huge + "2",
                    NavigationLine = first.NavigationLine + 1,
                };
                return result with { Matches = [first, second] };
            },
        };
        JsonElement oversizedCandidate = Parse(oversizedCandidateTools.Definition(
            documentationCommentId: "T:Agent.IWorker"));
        Assert.Equal("symbol_ambiguous", oversizedCandidate.GetProperty("error").GetString());
        Assert.Single(oversizedCandidate.GetProperty("candidates").EnumerateArray());
        Assert.True(oversizedCandidate.GetProperty("candidates")[0]
            .TryGetProperty("recovery", out _));
        Assert.True(oversizedCandidate.GetProperty("responseBudget")
            .GetProperty("exceeded").GetBoolean());

        var seedTimeoutTools = new NavigationTools(_fixture.Manager, _fixture.Semantic)
        {
            TestOnlyDocumentationIdSeedTimeout = true,
        };
        JsonElement seedTimeout = Parse(seedTimeoutTools.Definition(
            documentationCommentId: "T:Agent.IWorker"));
        Assert.Equal("semantic_unavailable", seedTimeout.GetProperty("error").GetString());
        Assert.Equal("documentation_id_seed_timeout",
            seedTimeout.GetProperty("partialReason").GetString());
        Assert.True(seedTimeout.GetProperty("retryRecommended").GetBoolean());
        JsonElement maxSeedTimeout = Parse(seedTimeoutTools.Definition(
            timeoutMs: int.MaxValue, documentationCommentId: "T:Agent.IWorker"));
        JsonElement maxTiming = maxSeedTimeout.GetProperty("timing");
        Assert.Equal(NavigationTools.DefinitionDeadlineMaxMs,
            maxTiming.GetProperty("effectiveDeadlineMs").GetInt32());
        Assert.Equal(NavigationTools.DefinitionDeadlineMaxMs,
            maxTiming.GetProperty("maxDeadlineMs").GetInt32());
        Assert.DoesNotContain("larger", maxSeedTimeout.GetProperty("retryHint").GetString()!,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(maxSeedTimeout.TryGetProperty("documentationIdCoverage", out _));
        JsonElement seedRetry = maxSeedTimeout.GetProperty("retry");
        Assert.Equal("search_symbol", seedRetry.GetProperty("tool").GetString());
        Assert.Equal("IWorker",
            seedRetry.GetProperty("arguments").GetProperty("query").GetString());

        using (IndexQueries queries = _fixture.Manager.OpenQueries())
        {
            DocumentationIdSeedResult seedFiles = queries.DocumentationIdSeedPaths(
                "RepeatedSeed", CancellationToken.None);
            Assert.Single(seedFiles.Paths);
            Assert.Equal(seedFiles.Paths.Count, seedFiles.SeedFiles);
        }
        using (var connection = new SqliteConnection(IndexQueries.ReadConnectionString(
                   _fixture.Manager.DbPath, pinReadSnapshot: false, pooling: false)))
        {
            connection.Open();
            using SqliteCommand plan = connection.CreateCommand();
            plan.CommandText = "EXPLAIN QUERY PLAN " + IndexQueries.DocumentationIdSeedPathsSql;
            plan.Parameters.AddWithValue("$name", "RepeatedSeed");
            var details = new List<string>();
            using SqliteDataReader reader = plan.ExecuteReader();
            while (reader.Read()) details.Add(reader.GetString(3));
            Assert.Contains(details, detail =>
                detail.Contains("idx_symbols_name", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(details, detail =>
                detail.Contains("SCAN s", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("SCAN symbols", StringComparison.OrdinalIgnoreCase));
        }

        JsonElement operatorNamedType = Parse(tools.Implementations(
            documentationCommentId: "T:Agent.op_Registry"));
        Assert.False(operatorNamedType.TryGetProperty("error", out _), operatorNamedType.ToString());
        Assert.Empty(operatorNamedType.GetProperty("implementations").EnumerateArray());

        string hugeMissingId = "T:" + new string('界', Json.HardBudgetBytes);
        string hugeMissingRaw = tools.Definition(documentationCommentId: hugeMissingId);
        JsonElement hugeMissing = Parse(hugeMissingRaw);
        Assert.True(Json.Utf8Bytes(hugeMissingRaw) <= Json.HardBudgetBytes);
        Assert.True(hugeMissing.GetProperty("documentationCommentIdTruncated").GetBoolean());
        Assert.True(hugeMissing.GetProperty("documentationCommentIdBytes").GetInt32() >
                    Json.HardBudgetBytes);

        foreach (string methodName in new[]
                 {
                     nameof(NavigationTools.Definition),
                     nameof(NavigationTools.References),
                     nameof(NavigationTools.Implementations),
                 })
        {
            string description = typeof(NavigationTools).GetMethod(methodName)!
                .GetCustomAttribute<DescriptionAttribute>()!.Description;
            Assert.Contains("documentationCommentId is semantic-only", description,
                StringComparison.Ordinal);
            Assert.Contains("never", description, StringComparison.Ordinal);
            string parameterDescription = typeof(NavigationTools).GetMethod(methodName)!
                .GetParameters().Single(parameter =>
                    parameter.Name == "documentationCommentId")
                .GetCustomAttribute<DescriptionAttribute>()!.Description;
            Assert.Contains("compiler-canonical", parameterDescription,
                StringComparison.Ordinal);
            Assert.Contains("Empty means omitted", parameterDescription,
                StringComparison.Ordinal);
            Assert.Contains("whitespace-only is bad_request", parameterDescription,
                StringComparison.Ordinal);
        }

        static void AssertReplayPatch(JsonElement response, string operation,
            string preservedArgument)
        {
            JsonElement recovery = response.GetProperty("candidates")[0]
                .GetProperty("recovery");
            Assert.Equal(operation, recovery.GetProperty("tool").GetString());
            Assert.True(recovery.GetProperty("replayOriginalRequest").GetBoolean());
            string[] removed = recovery.GetProperty("remove").EnumerateArray()
                .Select(item => item.GetString()!).ToArray();
            Assert.Contains("documentationCommentId", removed);
            Assert.Contains("name", removed);
            Assert.Contains("path", removed);
            Assert.DoesNotContain(preservedArgument, removed);
            JsonElement arguments = recovery.GetProperty("arguments");
            Assert.True(arguments.TryGetProperty("path", out _));
            Assert.True(arguments.TryGetProperty("line", out _));
            Assert.True(arguments.TryGetProperty("column", out _));
        }
    }

    [Fact]
    public void DocumentationCommentIdsNeverFallBackToNamesAfterSemanticFailure()
    {
        if (!_fixture.Semantic.FrameworkRefsAvailable) return;
        var tools = new NavigationTools(_fixture.Manager, _fixture.Semantic)
        {
            TestOnlySemanticFailureReason = "semantic_timeout",
        };

        JsonElement definition = Parse(tools.Definition(
            documentationCommentId: "M:Agent.IWorker.Work(System.String)"));
        Assert.Equal("semantic_unavailable", definition.GetProperty("error").GetString());
        Assert.False(definition.TryGetProperty("declarations", out _));

        JsonElement references = Parse(tools.References(
            documentationCommentId: "M:Agent.IWorker.Work(System.String)"));
        Assert.Equal("semantic_unavailable", references.GetProperty("error").GetString());
        Assert.False(references.TryGetProperty("groups", out _));

        JsonElement implementations = Parse(tools.Implementations(
            documentationCommentId: "T:Agent.IWorker"));
        Assert.Equal("semantic_unavailable", implementations.GetProperty("error").GetString());
        Assert.False(implementations.TryGetProperty("implementations", out _));

        foreach (JsonElement response in new[] { definition, references, implementations })
        {
            JsonElement timing = response.GetProperty("timing");
            Assert.InRange(timing.GetProperty("documentationIdResolutionMs").GetInt64(),
                0, timing.GetProperty("elapsedMs").GetInt64());
        }
    }

    [Fact]
    public void DocumentationCommentIdSeedDiscoveryUsesThePinnedSnapshotQueries()
    {
        if (!_fixture.Semantic.FrameworkRefsAvailable) return;
        _fixture.Manager.WriterQueryAfterRegistrationForTest = () =>
            throw new InvalidOperationException(
                "documentation-id seed discovery used an ordinary unpinned query");
        var tools = new NavigationTools(_fixture.Manager, _fixture.Semantic)
        {
            TestOnlyDocumentationIdResolutionTransform = result =>
            {
                _fixture.Manager.WriterQueryAfterRegistrationForTest = null;
                return result;
            },
        };

        try
        {
            JsonElement response = Parse(tools.Definition(
                documentationCommentId: "T:Agent.IWorker"));
            Assert.False(response.TryGetProperty("error", out _), response.ToString());
            Assert.Equal("T:Agent.IWorker",
                response.GetProperty("documentationCommentId").GetString());
        }
        finally
        {
            _fixture.Manager.WriterQueryAfterRegistrationForTest = null;
        }
    }

    [Fact]
    public async Task DocumentationCommentIdPreCompilerDiscoveryHonorsCancellation()
    {
        if (!_fixture.Semantic.FrameworkRefsAvailable) return;
        using var cancellation = new CancellationTokenSource();
        int ownerQueries = 0;
        _fixture.Manager.ReviewSnapshotAfterQueryForTest = sql =>
        {
            if (!sql.Contains("FROM compile_items ci", StringComparison.Ordinal) ||
                !sql.Contains("WHERE f.path IN", StringComparison.Ordinal)) return;
            ownerQueries++;
            cancellation.Cancel();
        };

        try
        {
            DocumentationIdResolutionResult resolution = await _fixture.Semantic
                .ResolveDocumentationCommentIdAsync(
                    "HighCardinalitySeed", "T:Agent.HighCardinalitySeed", 60_000,
                    cancellation.Token);
            Assert.Equal("documentation_id_seed_timeout", resolution.FailReason);
            Assert.Null(resolution.Matches);
            Assert.True(resolution.Coverage.SeedFiles > 200,
                $"expected a multi-chunk seed set, got {resolution.Coverage.SeedFiles}");
            Assert.Equal(1, ownerQueries);
        }
        finally
        {
            _fixture.Manager.ReviewSnapshotAfterQueryForTest = null;
        }

        using var closureCancellation = new CancellationTokenSource();
        int closureQueries = 0;
        _fixture.Manager.ReviewSnapshotAfterQueryForTest = sql =>
        {
            if (!sql.Contains("SELECT pf.name, pt.name FROM project_refs",
                    StringComparison.Ordinal)) return;
            closureQueries++;
            closureCancellation.Cancel();
        };
        try
        {
            DocumentationIdResolutionResult resolution = await _fixture.Semantic
                .ResolveDocumentationCommentIdAsync(
                    "IWorker", "T:Agent.IWorker", 60_000,
                    closureCancellation.Token);
            Assert.Equal("documentation_id_seed_timeout", resolution.FailReason);
            Assert.Null(resolution.Matches);
            Assert.Equal(1, closureQueries);
        }
        finally
        {
            _fixture.Manager.ReviewSnapshotAfterQueryForTest = null;
        }
    }

    [Fact]
    public async Task DocumentationCommentIdUsesTheEstablishedNameKeyedPairedCompilation()
    {
        if (!_fixture.Semantic.FrameworkRefsAvailable) return;
        using (IndexQueries queries = _fixture.Manager.OpenQueries())
        {
            ProjectRow[] physicalOwners = queries.ProjectsContaining("Paired/Pair.cs")
                .Where(project => project.Language == "cs")
                .ToArray();
            Assert.Equal(2, physicalOwners.Length);
            Assert.Equal(new[] { "legacy", "sdk" },
                physicalOwners.Select(project => project.Style)
                    .OrderBy(style => style, StringComparer.Ordinal).ToArray());
            Assert.Single(physicalOwners.Select(project => project.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase));
        }

        DocumentationIdResolutionResult resolution = await _fixture.Semantic
            .ResolveDocumentationCommentIdAsync(
                "PairedIdentity", "T:Agent.PairedIdentity", 60_000);
        DocumentationIdResolution match = Assert.Single(resolution.Matches!);
        Assert.Null(resolution.FailReason);
        Assert.Equal("T:Agent.PairedIdentity", match.CanonicalDocumentationCommentId);
        Assert.Equal("Agent.Paired", match.Declaration.Assembly);
        Assert.Equal(1, resolution.Coverage.SeedProjects);
        Assert.True(resolution.Coverage.CompilerScanned);
        Assert.Equal(0, resolution.Coverage.NameKeyedOwnerCollisionGroups);

        JsonElement unrelatedSameName = Parse(_fixture.Tools.Definition(
            documentationCommentId: "T:DuplicateA.Alpha"));
        Assert.False(unrelatedSameName.TryGetProperty("error", out _),
            unrelatedSameName.ToString());
        JsonElement collisionCoverage = unrelatedSameName
            .GetProperty("documentationIdCoverage");
        Assert.Equal(1,
            collisionCoverage.GetProperty("nameKeyedOwnerCollisionGroups").GetInt32());
        Assert.Equal("documentation_id_name_keyed_owner_collision",
            collisionCoverage.GetProperty("noteId").GetString());
    }

    [Fact]
    public void DocumentationCommentIdNavigationRejectsEveryIndexIdentityChange()
    {
        if (!_fixture.Semantic.FrameworkRefsAvailable) return;
        var epochTools = new NavigationTools(_fixture.Manager, _fixture.Semantic)
        {
            TestOnlyDocumentationIdResolutionTransform = result =>
                result.SnapshotIdentity is null
                    ? result
                    : result with
                    {
                        SnapshotIdentity = result.SnapshotIdentity with
                        {
                            RefreshEpoch = result.SnapshotIdentity.RefreshEpoch + 2,
                        },
                    },
        };

        var databaseTools = new NavigationTools(_fixture.Manager, _fixture.Semantic)
        {
            TestOnlyDocumentationIdResolutionTransform = result =>
                result.SnapshotIdentity is null
                    ? result
                    : result with
                    {
                        SnapshotIdentity = result.SnapshotIdentity with
                        {
                            DatabaseIdentity = result.SnapshotIdentity.DatabaseIdentity +
                                               "-replacement",
                        },
                    },
        };

        AssertSnapshotChanged(epochTools);
        AssertSnapshotChanged(databaseTools);

        static void AssertSnapshotChanged(NavigationTools tools)
        {
            (JsonElement Response, string ForbiddenResult)[] responses =
            [
                (Parse(tools.Definition(documentationCommentId: "T:Agent.IWorker")),
                    "declarations"),
                (Parse(tools.References(documentationCommentId:
                        "M:Agent.IWorker.Work(System.String)")), "groups"),
                (Parse(tools.Implementations(documentationCommentId: "T:Agent.IWorker")),
                    "implementations"),
            ];
            Assert.All(responses, item =>
            {
                Assert.Equal("semantic_unavailable",
                    item.Response.GetProperty("error").GetString());
                Assert.Equal("index_snapshot_changed",
                    item.Response.GetProperty("partialReason").GetString());
                Assert.True(item.Response.GetProperty("retryRecommended").GetBoolean());
                Assert.Contains("refresh", item.Response.GetProperty("retryHint").GetString()!,
                    StringComparison.OrdinalIgnoreCase);
                Assert.False(item.Response.TryGetProperty(item.ForbiddenResult, out _));
            });
        }
    }

    [Fact]
    public void OversizedImplementationIdentityUsesTheMeasuredException()
    {
        if (!_fixture.Semantic.FrameworkRefsAvailable) return;
        var tools = new NavigationTools(_fixture.Manager, _fixture.Semantic)
        {
            TestOnlyImplementationsResultTransform = result => result with
            {
                Symbol = result.Symbol with
                {
                    SymbolDisplay = new string('界', Json.HardBudgetBytes),
                    DocumentationCommentId = "T:" + new string('界', Json.HardBudgetBytes),
                },
            },
        };

        JsonElement response = Parse(tools.Implementations("IWorker"));
        Assert.False(response.TryGetProperty("error", out _), response.ToString());
        JsonElement budget = response.GetProperty("responseBudget");
        Assert.True(budget.GetProperty("exceeded").GetBoolean());
        Assert.Equal("indivisible_semantic_identity", budget.GetProperty("reason").GetString());
    }

    [Fact]
    public void EverySemanticNavigationFamilyUsesTheSameBoundedRetryContract()
    {
        var transientTools = new NavigationTools(_fixture.Manager, _fixture.Semantic)
        {
            TestOnlySemanticFailureReason = "semantic_timeout",
        };
        foreach ((string operation, JsonElement response) in Responses(transientTools))
        {
            Assert.True(response.GetProperty("retryRecommended").GetBoolean(), response.ToString());
            Assert.Contains(operation, response.GetProperty("retryHint").GetString()!,
                StringComparison.Ordinal);
        }

        var permanentTools = new NavigationTools(_fixture.Manager, _fixture.Semantic)
        {
            TestOnlySemanticFailureReason = "symbol_not_resolved",
        };
        foreach (JsonElement response in Responses(permanentTools).Values)
        {
            Assert.False(response.TryGetProperty("retryRecommended", out _), response.ToString());
            Assert.False(response.TryGetProperty("retryHint", out _), response.ToString());
        }

        static Dictionary<string, JsonElement> Responses(NavigationTools tools) => new()
        {
            ["definition"] = Parse(tools.Definition("Worker")),
            ["references"] = Parse(tools.References("Work")),
            ["implementations"] = Parse(tools.Implementations("IWorker")),
            ["callers"] = Parse(tools.Callers("Work")),
            ["callees"] = Parse(tools.Callees("Work")),
            ["type_hierarchy"] = Parse(tools.TypeHierarchy("Worker")),
        };
    }

    [Fact]
    public void ReviewPackNamesABoundedAffectedPathSetWhenCoverageIsClipped()
    {
        string paths = JsonSerializer.Serialize(Enumerable.Range(0, 40)
            .Select(index => $"Core/Extra{index}.cs"));
        JsonElement response = Parse(_fixture.Tools.ReviewPack(
            paths: paths, maxBytes: 65536, maxSymbols: 1));
        JsonElement affected = response.GetProperty("affectedPaths");
        Assert.Equal(40, affected.GetProperty("total").GetInt32());
        Assert.Equal(NavigationTools.ReviewPathSampleLimit,
            affected.GetProperty("returned").GetInt32());
        Assert.Equal(NavigationTools.ReviewPathSampleLimit,
            affected.GetProperty("disclosureLimit").GetInt32());
        Assert.Equal(NavigationTools.ReviewPathSampleBytes,
            affected.GetProperty("disclosureBytes").GetInt32());
        Assert.True(affected.GetProperty("truncated").GetBoolean());
        Assert.Contains(affected.GetProperty("reasonIds").EnumerateArray(), item =>
            item.GetString() == "review.symbol_count_cap");
        Assert.Equal("split_review_by_explicit_paths",
            affected.GetProperty("recovery").GetProperty("action").GetString());

        JsonElement combined = Parse(_fixture.Tools.ReviewPack(
            paths: paths, maxBytes: 4096, maxSymbols: 20));
        string[] combinedReasons = combined.GetProperty("affectedPaths")
            .GetProperty("reasonIds").EnumerateArray()
            .Select(item => item.GetString()!).ToArray();
        Assert.Contains("review.symbol_count_cap", combinedReasons);
        Assert.Contains("review.byte_budget", combinedReasons);

        Assert.Empty(NavigationTools.BoundedReviewPathSample(["a"], maxJsonBytes: 4));
        Assert.Equal(["a"],
            NavigationTools.BoundedReviewPathSample(["a"], maxJsonBytes: 5));
    }
}

public sealed class AgentExperienceFixture : IDisposable
{
    public string Root { get; }
    public IndexManager Manager { get; }
    public SemanticService Semantic { get; }
    public NavigationTools Tools { get; }

    public AgentExperienceFixture()
    {
        Root = Directory.CreateTempSubdirectory("codenav-agent-experience").FullName;
        WriteWorkspace(Root);
        string database = IndexBuilder.DefaultDbPath(Root);
        IndexBuilder.Build(Root, database);
        Manager = new IndexManager(Root, database);
        Manager.Start();
        IndexManagerTestSupport.WaitUntilReady(Manager, TimeSpan.FromSeconds(30),
            "agent-experience fixture did not become queryable");
        Semantic = new SemanticService(Manager);
        Tools = new NavigationTools(Manager, Semantic);
    }

    public void Dispose()
    {
        Semantic.Dispose();
        Manager.Dispose();
        TestWorkspaceCleanup.ClearIndexPools(Root);
        TestWorkspaceCleanup.DeleteWorkspace(Root);
    }

    private static void WriteWorkspace(string root)
    {
        static void Project(string root, string directory, string projectFile,
            string project, params (string Path, string Source)[] sources)
        {
            string projectDirectory = Path.Combine(root, directory);
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, projectFile), project);
            foreach ((string path, string source) in sources)
            {
                string full = Path.Combine(projectDirectory, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, source);
            }
        }

        Project(root, "Core", "Core.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework><AssemblyName>Agent.Core</AssemblyName></PropertyGroup>
            </Project>
            """,
            ("Agent.cs",
                """
                namespace Agent;
                public interface IWorker
                {
                    string Name { get; }
                    event System.EventHandler Changed;
                    void Work(string value);
                }
                public class Worker : IWorker
                {
                    public int Field;
                    public Worker() { }
                    public string Name => "worker";
                    public event System.EventHandler? Changed;
                    public void Work(string value) { Changed?.Invoke(this, System.EventArgs.Empty); }
                    public void Same(int value) { } public void Same(string value) { }
                    public static Worker operator +(Worker left, Worker right) => left;
                }
                public class DerivedWorker : Worker { }
                public class op_Registry { }
                public class Missing { }
                public partial class RepeatedSeed { }
                public partial class RepeatedSeed { }
                """),
            ("SharedDuplicate.cs", "namespace Shared; public class Duplicate { }") ,
            ("external/Vendor.cs", "namespace Agent; public class VendorOnly { }"));
        for (int index = 0; index < 40; index++)
        {
            File.WriteAllText(Path.Combine(root, "Core", $"Extra{index}.cs"),
                $"namespace Agent; public class Extra{index} {{ }}");
        }
        for (int index = 0; index < 205; index++)
        {
            File.WriteAllText(Path.Combine(root, "Core", $"HighCardinalitySeed{index}.cs"),
                "namespace Agent; public partial class HighCardinalitySeed { }");
        }

        Project(root, "Paired", "Project.csproj",
            """
            <Project ToolsVersion="15.0">
              <PropertyGroup>
                <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
                <AssemblyName>Agent.Paired</AssemblyName>
              </PropertyGroup>
              <ItemGroup><Compile Include="Pair.cs" /></ItemGroup>
            </Project>
            """,
            ("Pair.cs", "namespace Agent; public class PairedIdentity { }"));
        Project(root, "Paired", "Project.Net.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <AssemblyName>Agent.Paired</AssemblyName>
              </PropertyGroup>
            </Project>
            """,
            ("Pair.cs", "namespace Agent; public class PairedIdentity { }"));

        Project(root, "Consumer", "Consumer.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Core/Core.csproj" /></ItemGroup>
            </Project>
            """,
            ("Use.cs",
                "namespace Consumer; public class Use { public void Run(Agent.IWorker worker) => worker.Work(\"go\"); }"),
            ("SharedDuplicate.cs", "namespace Shared; public class Duplicate { }"));

        const string shared =
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework><AssemblyName>Shared.Identity</AssemblyName></PropertyGroup>
            </Project>
            """;
        Project(root, "DuplicateA", "Alpha.csproj", shared,
            ("Alpha.cs", "namespace DuplicateA; public class Alpha { }"));
        Project(root, "DuplicateB", "Beta.csproj", shared,
            ("Beta.cs", "namespace DuplicateB; public class Beta { }"));
        Project(root, "Stem", "Project.File.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework><AssemblyName>Stem.Winner</AssemblyName></PropertyGroup>
            </Project>
            """,
            ("Stem.cs", "namespace Stem; public class Winner { }"));
        Project(root, "Metadata", "Other.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework><AssemblyName>Project.File</AssemblyName></PropertyGroup>
            </Project>
            """,
            ("Metadata.cs", "namespace Metadata; public class Other { }"));

        Project(root, "CollisionCs", "Foo.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework><AssemblyName>Collision.Cs</AssemblyName></PropertyGroup>
            </Project>
            """,
            ("Foo.cs", "namespace Collision; public class CsOnly { }"));
        Project(root, "CollisionFs", "Foo.fsproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework><AssemblyName>Collision.Fs</AssemblyName></PropertyGroup>
            </Project>
            """,
            ("Foo.fs", "namespace Collision\ntype FsOnly() = class end"));

        File.WriteAllText(Path.Combine(root, "Orphan.cs"),
            "namespace Orphan; public class Loose { }");
        File.WriteAllText(Path.Combine(root, "README.md"), "# Agent fixture");
    }
}
