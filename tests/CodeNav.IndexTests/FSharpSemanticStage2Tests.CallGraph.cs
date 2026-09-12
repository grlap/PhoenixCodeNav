using System.Text.Json;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

public partial class FSharpSemanticStage2Tests
{
    [Fact]
    public void CallersAndCalleesReturnCompilerBoundFSharpCallSites()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-call-graph-basic").FullName;
        try
        {
            WriteProject(root, "Calls/Calls.fsproj", SdkProject("net10.0", "Calls.fs"));
            WriteProject(root, "Calls/Calls.fs", """
                namespace Calls
                module Flow =
                    let helper value = value + 1
                    let caller value = helper value
                """);

            using var fixture = Fixture.Create(root);
            var (callers, _) = FSharpSemanticTelemetryAssert.Observe(fixture.Manager.Telemetry, () => fixture.Tools.Callers(
                path: "Calls/Calls.fs", line: 3, column: 9,
                timeoutMs: 60_000), "callers", "partial");
            string callersRaw = callers.GetRawText();
            Assert.False(callers.TryGetProperty("error", out _), callersRaw);
            Assert.Equal(1, callers.GetProperty("totalCallers").GetInt32());
            Assert.Equal(1, callers.GetProperty("totalCallSites").GetInt32());
            JsonElement caller = Assert.Single(callers.GetProperty("callers")
                .EnumerateArray());
            Assert.Equal("caller", caller.GetProperty("caller")
                .GetProperty("name").GetString());
            JsonElement callerSite = Assert.Single(caller.GetProperty("callSites")
                .EnumerateArray());
            Assert.Equal("directApplication", callerSite.GetProperty("callKind")
                .GetString());
            Assert.Equal("Calls/Calls.fs", callerSite.GetProperty("path").GetString());
            Assert.True(callers.GetProperty("coverage")
                .GetProperty("workspaceComplete").GetBoolean());
            Assert.Equal("exact", callers.GetProperty("meta")
                .GetProperty("confidence").GetString());
            Assert.Equal("semantic", callers.GetProperty("meta")
                .GetProperty("navigationLayer").GetString());

            var (callees, calleesRecord) = FSharpSemanticTelemetryAssert.Observe(fixture.Manager.Telemetry, () => fixture.Tools.Callees(
                path: "Calls/Calls.fs", line: 4, column: 9,
                timeoutMs: 60_000), "callees", "partial");
            string calleesRaw = callees.GetRawText();
            Assert.False(calleesRecord.GetProperty("semanticColdStart").TryGetProperty("fileParseAndCheckMs", out _));
            Assert.False(callees.TryGetProperty("error", out _), calleesRaw);
            Assert.Equal(1, callees.GetProperty("totalCallees").GetInt32());
            Assert.Equal(1, callees.GetProperty("totalCallSites").GetInt32());
            JsonElement callee = Assert.Single(callees.GetProperty("callees")
                .EnumerateArray());
            Assert.Equal("helper", callee.GetProperty("callee")
                .GetProperty("name").GetString());
            Assert.Equal([4], callee.GetProperty("callLines")
                .EnumerateArray().Select(value => value.GetInt32()).ToArray());
            Assert.Equal("directApplication", Assert.Single(callee
                .GetProperty("callSites").EnumerateArray())
                .GetProperty("callKind").GetString());
            Assert.Equal("selected_fsharp_body_with_project_reference_closure_targets",
                callees.GetProperty("coverage").GetProperty("scope").GetString());
            Assert.True(callees.GetProperty("coverage").GetProperty("complete")
                .GetBoolean());
            Assert.Equal("exact", callees.GetProperty("meta")
                .GetProperty("confidence").GetString());
            Assert.True(Json.Utf8Bytes(callersRaw) <= Json.HardBudgetBytes);
            Assert.True(Json.Utf8Bytes(calleesRaw) <= Json.HardBudgetBytes);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FSharpCallGraphClassifiesPartialPipelineAndFirstClassEvidence()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-call-graph-kinds").FullName;
        try
        {
            WriteProject(root, "Kinds/Kinds.fsproj", SdkProject("net10.0", "Kinds.fs"));
            WriteProject(root, "Kinds/Kinds.fs", """
                namespace Kinds
                module Flow =
                    let add left right = left + right
                    let target value = value + 1
                    let direct value = target value
                    let partial = add 1
                    let pipeline value = value |> target
                    let firstClass = target
                    let passed values = List.map target values
                    let invoke value =
                        let plusOne = add 1
                        let piped = value |> target
                        plusOne piped
                """);

            using var fixture = Fixture.Create(root);
            JsonElement targetCallers = Parse(CallSemantic(() => fixture.Tools.Callers(
                path: "Kinds/Kinds.fs", line: 4, column: 9,
                timeoutMs: 60_000)));
            Assert.False(targetCallers.TryGetProperty("error", out _),
                targetCallers.GetRawText());
            string[] targetKinds = targetCallers.GetProperty("callers")
                .EnumerateArray()
                .SelectMany(caller => caller.GetProperty("callSites").EnumerateArray())
                .Select(site => site.GetProperty("callKind").GetString()!)
                .Distinct().Order().ToArray();
            Assert.Contains("directApplication", targetKinds);
            Assert.Contains("pipelineApplication", targetKinds);
            Assert.Contains("firstClassReference", targetKinds);

            JsonElement addCallers = Parse(CallSemantic(() => fixture.Tools.Callers(
                path: "Kinds/Kinds.fs", line: 3, column: 9,
                timeoutMs: 60_000)));
            Assert.Contains("partialApplication", addCallers.GetProperty("callers")
                .EnumerateArray()
                .SelectMany(caller => caller.GetProperty("callSites").EnumerateArray())
                .Select(site => site.GetProperty("callKind").GetString()));

            JsonElement callees = Parse(CallSemantic(() => fixture.Tools.Callees(
                path: "Kinds/Kinds.fs", line: 10, column: 9,
                timeoutMs: 60_000)));
            Assert.False(callees.TryGetProperty("error", out _), callees.GetRawText());
            var calleeSites = callees.GetProperty("callees").EnumerateArray()
                .SelectMany(callee => callee.GetProperty("callSites").EnumerateArray())
                .ToList();
            Assert.Contains(calleeSites, site =>
                site.GetProperty("callKind").GetString() == "pipelineApplication");
            Assert.Contains(calleeSites, site =>
                site.GetProperty("callKind").GetString() == "indirectApplication");
            Assert.DoesNotContain(calleeSites, site =>
                site.GetProperty("callKind").GetString() == "firstClassReference");
            Assert.All(calleeSites, site => Assert.Contains(
                site.GetProperty("callKind").GetString(), new[]
                {
                    "directApplication", "partialApplication", "pipelineApplication",
                    "computationExpression", "construction", "unionCaseConstruction",
                    "activePattern", "operator", "indirectApplication",
                }));

            JsonElement localCallees = Parse(CallSemantic(() => fixture.Tools.Callees(
                path: "Kinds/Kinds.fs", line: 11, column: 13,
                timeoutMs: 60_000)));
            Assert.False(localCallees.TryGetProperty("error", out _),
                localCallees.GetRawText());
            Assert.Equal("plusOne", localCallees.GetProperty("symbol")
                .GetProperty("name").GetString());
            Assert.Contains(localCallees.GetProperty("callees").EnumerateArray()
                    .SelectMany(callee => callee.GetProperty("callSites").EnumerateArray()),
                site => site.GetProperty("callKind").GetString() == "partialApplication");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FSharpCallersIncludeLocalAndParameterApplications()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-callers-indirect-applications").FullName;
        try
        {
            WriteProject(root, "Indirect/Indirect.fsproj",
                SdkProject("net10.0", "Indirect.fs"));
            WriteProject(root, "Indirect/Indirect.fs", """
                module Indirect
                let outer x =
                    let local y = y + 1
                    local x
                let invoke (f: int -> int) x = f x
                """);

            using var fixture = Fixture.Create(root);
            JsonElement local = Parse(CallSemantic(() => fixture.Tools.Callers(
                path: "Indirect/Indirect.fs", line: 3, column: 9,
                timeoutMs: 60_000)));
            Assert.False(local.TryGetProperty("error", out _), local.GetRawText());
            Assert.Equal(1, local.GetProperty("totalCallers").GetInt32());
            JsonElement localCaller = Assert.Single(local.GetProperty("callers")
                .EnumerateArray());
            Assert.Equal("outer", localCaller.GetProperty("caller")
                .GetProperty("name").GetString());
            Assert.Equal("indirectApplication", Assert.Single(localCaller
                .GetProperty("callSites").EnumerateArray())
                .GetProperty("callKind").GetString());

            JsonElement parameter = Parse(CallSemantic(() => fixture.Tools.Callers(
                path: "Indirect/Indirect.fs", line: 5, column: 13,
                timeoutMs: 60_000)));
            Assert.False(parameter.TryGetProperty("error", out _),
                parameter.GetRawText());
            Assert.Equal(1, parameter.GetProperty("totalCallers").GetInt32());
            JsonElement parameterCaller = Assert.Single(parameter.GetProperty("callers")
                .EnumerateArray());
            Assert.Equal("invoke", parameterCaller.GetProperty("caller")
                .GetProperty("name").GetString());
            Assert.Equal("indirectApplication", Assert.Single(parameterCaller
                .GetProperty("callSites").EnumerateArray())
                .GetProperty("callKind").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FSharpCallGraphRecognizesFunctionValuedPipelineOperands()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-call-graph-pipeline-operands").FullName;
        try
        {
            WriteProject(root, "Pipelines/Pipelines.fsproj",
                SdkProject("net10.0", "Pipelines.fs"));
            WriteProject(root, "Pipelines/Pipelines.fs", """
                module Pipelines
                let forward (f: int -> int) value = value |> f
                let reverse (f: int -> int) value = f <| value
                let tupled (f: int -> int -> int) left right = (left, right) ||> f
                let saved (f: int -> int) = f
                """);

            using var fixture = Fixture.Create(root);

            void AssertCaller(int line, int column, string callerName,
                string callKind)
            {
                string raw = CallSemantic(() => fixture.Tools.Callers(
                    path: "Pipelines/Pipelines.fs", line: line, column: column,
                    timeoutMs: 60_000));
                JsonElement response = Parse(raw);
                Assert.False(response.TryGetProperty("error", out _), raw);
                Assert.Equal(1, response.GetProperty("totalCallers").GetInt32());
                Assert.Equal(1, response.GetProperty("totalCallSites").GetInt32());
                JsonElement caller = Assert.Single(response.GetProperty("callers")
                    .EnumerateArray());
                Assert.Equal(callerName, caller.GetProperty("caller")
                    .GetProperty("name").GetString());
                Assert.Equal(callKind, Assert.Single(caller.GetProperty("callSites")
                    .EnumerateArray()).GetProperty("callKind").GetString());
            }

            void AssertCallee(int line, string callKind)
            {
                string raw = CallSemantic(() => fixture.Tools.Callees(
                    path: "Pipelines/Pipelines.fs", line: line, column: 5,
                    timeoutMs: 60_000));
                JsonElement response = Parse(raw);
                Assert.False(response.TryGetProperty("error", out _), raw);
                Assert.Equal(1, response.GetProperty("totalCallees").GetInt32());
                Assert.Equal(1, response.GetProperty("totalCallSites").GetInt32());
                JsonElement callee = Assert.Single(response.GetProperty("callees")
                    .EnumerateArray());
                Assert.Equal("f", callee.GetProperty("callee")
                    .GetProperty("name").GetString());
                Assert.Equal(callKind, Assert.Single(callee.GetProperty("callSites")
                    .EnumerateArray()).GetProperty("callKind").GetString());
            }

            AssertCaller(line: 2, column: 14, callerName: "forward",
                callKind: "pipelineApplication");
            AssertCaller(line: 3, column: 14, callerName: "reverse",
                callKind: "pipelineApplication");
            AssertCaller(line: 4, column: 13, callerName: "tupled",
                callKind: "pipelineApplication");
            AssertCaller(line: 5, column: 12, callerName: "saved",
                callKind: "firstClassReference");

            AssertCallee(line: 2, callKind: "pipelineApplication");
            AssertCallee(line: 3, callKind: "pipelineApplication");
            AssertCallee(line: 4, callKind: "pipelineApplication");

            string savedRaw = CallSemantic(() => fixture.Tools.Callees(
                path: "Pipelines/Pipelines.fs", line: 5, column: 5,
                timeoutMs: 60_000));
            JsonElement saved = Parse(savedRaw);
            Assert.False(saved.TryGetProperty("error", out _), savedRaw);
            Assert.Equal(0, saved.GetProperty("totalCallees").GetInt32());
            Assert.Equal(0, saved.GetProperty("totalCallSites").GetInt32());
            Assert.Empty(saved.GetProperty("callees").EnumerateArray());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FSharpCalleesPreserveExternalOverloadIdentities()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-callees-external-overloads").FullName;
        try
        {
            WriteProject(root, "External/External.fsproj",
                SdkProject("net10.0", "External.fs"));
            WriteProject(root, "External/External.fs", """
                module External
                let combine (a: string) (b: string) (c: string) =
                    let pair = System.String.Concat(a, b)
                    let triple = System.String.Concat(a, b, c)
                    pair.Length + triple.Length
                """);

            using var fixture = Fixture.Create(root);
            JsonElement response = Parse(CallSemantic(() => fixture.Tools.Callees(
                path: "External/External.fs", line: 2, column: 5,
                timeoutMs: 60_000)));
            Assert.False(response.TryGetProperty("error", out _),
                response.GetRawText());
            JsonElement[] overloads = response.GetProperty("callees").EnumerateArray()
                .Where(callee => callee.GetProperty("callee").GetProperty("name")
                    .GetString() == "Concat")
                .ToArray();
            Assert.Equal(2, overloads.Length);
            Assert.All(overloads, overload => Assert.Single(overload
                .GetProperty("callSites").EnumerateArray()));
            Assert.Equal([3, 4], overloads
                .SelectMany(overload => overload.GetProperty("callLines")
                    .EnumerateArray())
                .Select(value => value.GetInt32())
                .Order()
                .ToArray());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FSharpCalleesSelectInnermostLocalObjectOverrideAndConstructorBodies()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-callee-body-ownership").FullName;
        try
        {
            WriteProject(root, "Nested/Nested.fsproj", SdkProject("net10.0", "Nested.fs"));
            WriteProject(root, "Nested/Nested.fs", """
                namespace Nested
                module Calls =
                    let target value = value + 1
                    type Runner() =
                        do target 0 |> ignore
                        member _.Outer value =
                            let rec local item =
                                if item <= 0 then target item else local (item - 1)
                            local value
                        member _.Lambda value =
                            List.map (fun item -> target item) [value]
                        member _.Factory() =
                            { new System.IDisposable with
                                member _.Dispose() = target 2 |> ignore }
                """);

            using var fixture = Fixture.Create(root);

            JsonElement callers = Parse(CallSemantic(() => fixture.Tools.Callers(
                path: "Nested/Nested.fs", line: 3, column: 9,
                timeoutMs: 60_000)));
            Assert.False(callers.TryGetProperty("error", out _), callers.GetRawText());
            Assert.Contains(callers.GetProperty("callers").EnumerateArray(), caller =>
                caller.GetProperty("caller").GetProperty("name").GetString() == "local");
            Assert.Contains(callers.GetProperty("callers").EnumerateArray(), caller =>
                caller.GetProperty("caller").GetProperty("kind").GetString() ==
                "objectExpressionOverride");
            Assert.Contains(callers.GetProperty("callers").EnumerateArray(), caller =>
                caller.GetProperty("caller").GetProperty("kind").GetString() ==
                "constructor");
            Assert.Contains(callers.GetProperty("callers").EnumerateArray(), caller =>
                caller.GetProperty("caller").GetProperty("name").GetString() == "Lambda");

            JsonElement local = Parse(CallSemantic(() => fixture.Tools.Callees(
                path: "Nested/Nested.fs", line: 7, column: 21,
                timeoutMs: 60_000)));
            Assert.False(local.TryGetProperty("error", out _), local.GetRawText());
            Assert.Equal("local", local.GetProperty("symbol").GetProperty("name")
                .GetString());
            Assert.Contains(local.GetProperty("callees").EnumerateArray(), callee =>
                callee.GetProperty("callee").GetProperty("name").GetString() == "target");
            Assert.Contains(local.GetProperty("callees").EnumerateArray(), callee =>
                callee.GetProperty("callee").GetProperty("name").GetString() == "local");

            JsonElement outer = Parse(CallSemantic(() => fixture.Tools.Callees(
                path: "Nested/Nested.fs", line: 6, column: 18,
                timeoutMs: 60_000)));
            Assert.False(outer.TryGetProperty("error", out _), outer.GetRawText());
            Assert.Equal("Outer", outer.GetProperty("symbol").GetProperty("name")
                .GetString());
            Assert.Equal("local", Assert.Single(outer.GetProperty("callees")
                .EnumerateArray()).GetProperty("callee").GetProperty("name").GetString());

            JsonElement lambda = Parse(CallSemantic(() => fixture.Tools.Callees(
                path: "Nested/Nested.fs", line: 11, column: 36,
                timeoutMs: 60_000)));
            Assert.False(lambda.TryGetProperty("error", out _), lambda.GetRawText());
            Assert.Equal("Lambda", lambda.GetProperty("symbol").GetProperty("name")
                .GetString());
            Assert.Contains(lambda.GetProperty("callees").EnumerateArray(), callee =>
                callee.GetProperty("callee").GetProperty("name").GetString() == "target");

            JsonElement objectOverride = Parse(CallSemantic(() => fixture.Tools.Callees(
                path: "Nested/Nested.fs", line: 14, column: 42,
                timeoutMs: 60_000)));
            Assert.False(objectOverride.TryGetProperty("error", out _),
                objectOverride.GetRawText());
            Assert.Equal("objectExpressionOverride", objectOverride.GetProperty("symbol")
                .GetProperty("kind").GetString());
            Assert.Contains(objectOverride.GetProperty("callees").EnumerateArray(), callee =>
                callee.GetProperty("callee").GetProperty("name").GetString() == "target");

            JsonElement constructor = Parse(CallSemantic(() => fixture.Tools.Callees(
                path: "Nested/Nested.fs", line: 5, column: 12,
                timeoutMs: 60_000)));
            Assert.False(constructor.TryGetProperty("error", out _), constructor.GetRawText());
            Assert.Equal("constructor", constructor.GetProperty("symbol")
                .GetProperty("kind").GetString());
            Assert.Contains(constructor.GetProperty("callees").EnumerateArray(), callee =>
                callee.GetProperty("callee").GetProperty("name").GetString() == "target");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FSharpCallGraphTraversalCancellationReturnsResolvedPartialEvidence()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-call-graph-cancellation").FullName;
        try
        {
            WriteProject(root, "Cancel/Cancel.fsproj", SdkProject("net10.0", "Cancel.fs"));
            WriteProject(root, "Cancel/Cancel.fs", """
                module Cancel
                let target value = value + 1
                let caller value = target value
                """);

            using var fixture = Fixture.Create(root);
            fixture.Semantic.BeforeFSharpImplementationTraversalForTest = _ =>
                throw new OperationCanceledException();

            JsonElement callers = Parse(CallSemantic(() => fixture.Tools.Callers(
                path: "Cancel/Cancel.fs", line: 2, column: 5,
                timeoutMs: 60_000)));
            Assert.False(callers.TryGetProperty("error", out _), callers.GetRawText());
            Assert.Equal("target", callers.GetProperty("symbol").GetProperty("name")
                .GetString());
            Assert.Equal(0, callers.GetProperty("totalCallSites").GetInt32());
            Assert.True(callers.GetProperty("totalIsLowerBound").GetBoolean());
            Assert.Contains("fsharp_workspace_deadline",
                callers.GetProperty("partialReason").GetString());
            Assert.False(callers.GetProperty("coverage")
                .GetProperty("workspaceComplete").GetBoolean());

            JsonElement callees = Parse(CallSemantic(() => fixture.Tools.Callees(
                path: "Cancel/Cancel.fs", line: 3, column: 9,
                timeoutMs: 60_000)));
            Assert.False(callees.TryGetProperty("error", out _), callees.GetRawText());
            Assert.Equal("caller", callees.GetProperty("symbol").GetProperty("name")
                .GetString());
            Assert.Equal(0, callees.GetProperty("totalCallSites").GetInt32());
            Assert.True(callees.GetProperty("totalIsLowerBound").GetBoolean());
            Assert.Contains("fsharp_workspace_deadline",
                callees.GetProperty("partialReason").GetString());
            Assert.False(callees.GetProperty("coverage").GetProperty("complete")
                .GetBoolean());
            Assert.True(callees.GetProperty("retryRecommended").GetBoolean());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FSharpCallersKeepOverrideIdentityAndDiscloseDynamicDispatchSlot()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-caller-dispatch-slot").FullName;
        try
        {
            WriteProject(root, "Dispatch/Dispatch.fsproj",
                SdkProject("net10.0", "Dispatch.fs"));
            WriteProject(root, "Dispatch/Dispatch.fs", """
                namespace Dispatch
                type IFoo =
                    abstract Run: int -> int
                type Foo() =
                    interface IFoo with
                        member _.Run value = value + 1
                module Calls =
                    let invoke (service: IFoo) value = service.Run value
                """);

            using var fixture = Fixture.Create(root);
            JsonElement concrete = Parse(CallSemantic(() => fixture.Tools.Callers(
                path: "Dispatch/Dispatch.fs", line: 6, column: 18,
                timeoutMs: 60_000)));
            Assert.False(concrete.TryGetProperty("error", out _), concrete.GetRawText());
            Assert.True(concrete.GetProperty("totalCallSites").GetInt32() == 0,
                concrete.GetRawText());
            Assert.Contains("IFoo.Run", Assert.Single(concrete.GetProperty("dispatchSlots")
                .EnumerateArray()).GetString());
            Assert.Equal(
                "dynamic-dispatch call sites resolve to the abstract slot; run callers at that declaration",
                concrete.GetProperty("dispatchDetail").GetString());

            JsonElement contract = Parse(CallSemantic(() => fixture.Tools.Callers(
                path: "Dispatch/Dispatch.fs", line: 3, column: 14,
                timeoutMs: 60_000)));
            Assert.False(contract.TryGetProperty("error", out _), contract.GetRawText());
            Assert.Equal(1, contract.GetProperty("totalCallSites").GetInt32());
            Assert.Equal("invoke", Assert.Single(contract.GetProperty("callers")
                .EnumerateArray()).GetProperty("caller").GetProperty("name").GetString());
            Assert.False(contract.TryGetProperty("dispatchDetail", out _));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FSharpCalleesClassifyConstructionUnionActiveOperatorAndComputationEvidence()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-callee-taxonomy").FullName;
        try
        {
            WriteProject(root, "Taxonomy/Taxonomy.fsproj",
                SdkProject("net10.0", "Taxonomy.fs"));
            WriteProject(root, "Taxonomy/Taxonomy.fs", """
                namespace Taxonomy
                module Calls =
                    type Box(value: int) = member _.Value = value
                    type Choice = Value of int
                    let (|Even|Odd|) value = if value % 2 = 0 then Even else Odd
                    let inline (++) left right = left + right
                    type MaybeBuilder() =
                        member _.Bind(value: int option, binder: int -> int option) =
                            Option.bind binder value
                        member _.Return(value: int) = Some value
                    let maybe = MaybeBuilder()
                    let target value = value + 1
                    let classify input =
                        let boxed = Box(input)
                        let choice = Value input
                        let sum = input ++ 1
                        let parity = match input with Even -> 1 | Odd -> 0
                        let computed = maybe {
                            let! item = Some input
                            return target item
                        }
                        boxed.Value + sum + parity + Option.defaultValue 0 computed +
                        match choice with Value item -> item
                """);

            using var fixture = Fixture.Create(root);
            JsonElement response = Parse(CallSemantic(() => fixture.Tools.Callees(
                path: "Taxonomy/Taxonomy.fs", line: 13, column: 9,
                timeoutMs: 60_000)));
            Assert.False(response.TryGetProperty("error", out _), response.GetRawText());
            string[] callKinds = response.GetProperty("callees").EnumerateArray()
                .SelectMany(callee => callee.GetProperty("callSites").EnumerateArray())
                .Select(site => site.GetProperty("callKind").GetString()!)
                .Distinct().Order().ToArray();
            Assert.True(callKinds.Contains("construction"), response.GetRawText());
            Assert.Contains("unionCaseConstruction", callKinds);
            Assert.Contains("activePattern", callKinds);
            Assert.Contains("operator", callKinds);
            Assert.Contains("computationExpression", callKinds);
            Assert.All(callKinds, callKind => Assert.Contains(callKind, new[]
            {
                "directApplication", "partialApplication", "pipelineApplication",
                "computationExpression", "construction", "unionCaseConstruction",
                "activePattern", "operator", "indirectApplication",
            }));

            JsonElement constructorCallers = Parse(CallSemantic(() => fixture.Tools.Callers(
                path: "Taxonomy/Taxonomy.fs", line: 14, column: 21,
                timeoutMs: 60_000)));
            Assert.False(constructorCallers.TryGetProperty("error", out _),
                constructorCallers.GetRawText());
            Assert.Equal("constructor", constructorCallers.GetProperty("symbol")
                .GetProperty("kind").GetString());
            var constructorSites = constructorCallers.GetProperty("callers")
                .EnumerateArray().SelectMany(caller => caller.GetProperty("callSites")
                    .EnumerateArray()).ToList();
            Assert.True(constructorSites.Count == 1, constructorCallers.GetRawText());
            JsonElement constructorSite = constructorSites[0];
            Assert.Equal("construction", constructorSite.GetProperty("callKind")
                .GetString());

            foreach ((int line, int column, string expectedKind) in new[]
                     {
                         (15, 22, "unionCaseConstruction"),
                         (16, 25, "operator"),
                         (17, 39, "activePattern"),
                     })
            {
                JsonElement callers = Parse(CallSemantic(() => fixture.Tools.Callers(
                    path: "Taxonomy/Taxonomy.fs", line: line, column: column,
                    timeoutMs: 60_000)));
                Assert.False(callers.TryGetProperty("error", out _), callers.GetRawText());
                Assert.Contains(expectedKind, callers.GetProperty("callers")
                    .EnumerateArray().SelectMany(caller => caller.GetProperty("callSites")
                        .EnumerateArray()).Select(site => site.GetProperty("callKind")
                        .GetString()));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FSharpCallersDisambiguateOverloadsAndPropertyAccessors()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-caller-member-identity").FullName;
        try
        {
            WriteProject(root, "Members/Members.fsproj",
                SdkProject("net10.0", "Members.fs"));
            WriteProject(root, "Members/Members.fs", """
                namespace Members
                module Calls =
                    type Counter() =
                        let mutable current = 0
                        member _.Value
                            with get() = current
                            and set value = current <- value
                    type Ops =
                        static member Work(value: int) = value + 1
                        static member Work(value: string) = value.Length
                    let consume () =
                        let counter = Counter()
                        counter.Value <- Ops.Work(1)
                        counter.Value + Ops.Work("x")
                """);

            using var fixture = Fixture.Create(root);
            JsonElement integerOverload = Parse(CallSemantic(() => fixture.Tools.Callers(
                path: "Members/Members.fs", line: 9, column: 23,
                timeoutMs: 60_000)));
            Assert.False(integerOverload.TryGetProperty("error", out _),
                integerOverload.GetRawText());
            JsonElement integerSite = Assert.Single(integerOverload.GetProperty("callers")
                .EnumerateArray().SelectMany(caller => caller.GetProperty("callSites")
                    .EnumerateArray()));
            Assert.Equal(13, integerSite.GetProperty("line").GetInt32());

            JsonElement setter = Parse(CallSemantic(() => fixture.Tools.Callers(
                path: "Members/Members.fs", line: 13, column: 17,
                timeoutMs: 60_000)));
            Assert.False(setter.TryGetProperty("error", out _), setter.GetRawText());
            int[] setterLines = setter.GetProperty("callers").EnumerateArray()
                .SelectMany(caller => caller.GetProperty("callSites").EnumerateArray())
                .Select(site => site.GetProperty("line").GetInt32()).ToArray();
            Assert.Equal([13], setterLines);

            JsonElement getter = Parse(CallSemantic(() => fixture.Tools.Callers(
                path: "Members/Members.fs", line: 14, column: 17,
                timeoutMs: 60_000)));
            Assert.False(getter.TryGetProperty("error", out _), getter.GetRawText());
            int[] getterLines = getter.GetProperty("callers").EnumerateArray()
                .SelectMany(caller => caller.GetProperty("callSites").EnumerateArray())
                .Select(site => site.GetProperty("line").GetInt32()).ToArray();
            Assert.Equal([14], getterLines);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FSharpCallGraphDisclosesQuotationAndTraitCallBoundaries()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-call-graph-boundaries").FullName;
        try
        {
            WriteProject(root, "Boundaries/Boundaries.fsproj",
                SdkProject("net10.0", "Boundaries.fs"));
            WriteProject(root, "Boundaries/Boundaries.fs", """
                module Boundaries
                let target value = value + 1
                let quoteBody () =
                    let quoted = <@ target 1 @>
                    target 2, quoted
                let inline traitBody (value: ^T) =
                    ((^T) : (static member get_Zero: unit -> ^T) ()) |> ignore
                    value
                """);

            using var fixture = Fixture.Create(root);
            JsonElement callers = Parse(CallSemantic(() => fixture.Tools.Callers(
                path: "Boundaries/Boundaries.fs", line: 2, column: 5,
                timeoutMs: 60_000)));
            Assert.False(callers.TryGetProperty("error", out _), callers.GetRawText());
            Assert.Equal(1, callers.GetProperty("totalCallSites").GetInt32());
            Assert.True(callers.GetProperty("quotationBodiesExcluded").GetBoolean());
            Assert.Contains("fsharp_workspace_quotation_bodies_excluded",
                callers.GetProperty("partialReason").GetString());

            JsonElement quotation = Parse(CallSemantic(() => fixture.Tools.Callees(
                path: "Boundaries/Boundaries.fs", line: 3, column: 9,
                timeoutMs: 60_000)));
            Assert.False(quotation.TryGetProperty("error", out _), quotation.GetRawText());
            Assert.True(quotation.GetProperty("coverage")
                .GetProperty("quotationBodiesExcluded").GetBoolean());
            JsonElement target = Assert.Single(quotation.GetProperty("callees")
                .EnumerateArray(), callee => callee.GetProperty("callee")
                    .GetProperty("name").GetString() == "target");
            Assert.Equal([5], target.GetProperty("callLines").EnumerateArray()
                .Select(value => value.GetInt32()).ToArray());

            JsonElement trait = Parse(CallSemantic(() => fixture.Tools.Callees(
                path: "Boundaries/Boundaries.fs", line: 6, column: 16,
                timeoutMs: 60_000)));
            Assert.False(trait.TryGetProperty("error", out _), trait.GetRawText());
            Assert.True(trait.GetProperty("coverage").GetProperty("traitCallsUnresolved")
                .GetBoolean());
            Assert.Contains("fsharp_workspace_trait_calls_unresolved",
                trait.GetProperty("partialReason").GetString());
            Assert.Equal("exact", trait.GetProperty("meta").GetProperty("confidence")
                .GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FSharpCallGraphBudgetsCallerAndCalleeItemsWithoutChangingTotals()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-call-graph-budget").FullName;
        try
        {
            WriteProject(root, "Budget/Budget.fsproj", SdkProject("net10.0", "Budget.fs"));
            var source = new System.Text.StringBuilder("module Budget\nlet target value = value + 1\n");
            for (int index = 0; index < 60; index++)
                source.AppendLine($"let caller{index:D2} value = target value");
            source.AppendLine("let aggregate value =");
            source.AppendLine("    [");
            for (int index = 0; index < 60; index++)
                source.AppendLine($"        caller{index:D2} value");
            source.AppendLine("    ]");
            WriteProject(root, "Budget/Budget.fs", source.ToString());

            using var fixture = Fixture.Create(root);
            fixture.Tools.TestOnlyReferencesResponseMaxBytes = 5_000;

            string callersRaw = CallSemantic(() => fixture.Tools.Callers(
                path: "Budget/Budget.fs", line: 2, column: 5,
                timeoutMs: 60_000));
            JsonElement callers = Parse(callersRaw);
            Assert.False(callers.TryGetProperty("error", out _), callersRaw);
            Assert.Equal(60, callers.GetProperty("totalCallers").GetInt32());
            Assert.Equal(60, callers.GetProperty("totalCallSites").GetInt32());
            Assert.True(callers.GetProperty("totalCallersReturned").GetInt32() < 60);
            Assert.True(callers.GetProperty("truncated").GetBoolean());
            Assert.Equal(NoteIds.FSharpCallerItemsByteBudget,
                callers.GetProperty("truncationNoteId").GetString());

            string calleesRaw = CallSemantic(() => fixture.Tools.Callees(
                path: "Budget/Budget.fs", line: 63, column: 5,
                timeoutMs: 60_000));
            JsonElement callees = Parse(calleesRaw);
            Assert.False(callees.TryGetProperty("error", out _), calleesRaw);
            Assert.True(callees.GetProperty("totalCallees").GetInt32() > 50);
            Assert.True(callees.GetProperty("totalCalleesReturned").GetInt32() <
                        callees.GetProperty("totalCallees").GetInt32());
            Assert.True(callees.GetProperty("truncated").GetBoolean());
            Assert.Equal(NoteIds.FSharpCalleeItemsByteBudget,
                callees.GetProperty("truncationNoteId").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FSharpCallersBudgetDiagnosticsWithoutChangingTheAnswer()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-callers-diagnostic-budget").FullName;
        try
        {
            WriteProject(root, "Diagnostics/Diagnostics.fsproj",
                SdkProject("net10.0", "Diagnostics.fs"));
            var source = new System.Text.StringBuilder("""
                module Diagnostics
                let target value = value + 1
                let caller value = target value
                """);
            source.AppendLine();
            for (int index = 0; index < 12; index++)
            {
                string missingName = $"missing_{index:D2}_" + new string('x', 240);
                source.AppendLine($"let broken{index:D2} = {missingName}");
            }
            WriteProject(root, "Diagnostics/Diagnostics.fs", source.ToString());

            using var fixture = Fixture.Create(root);
            string baselineRaw = CallSemantic(() => fixture.Tools.Callers(
                path: "Diagnostics/Diagnostics.fs", line: 2, column: 5,
                timeoutMs: 60_000));
            JsonElement baseline = Parse(baselineRaw);
            Assert.False(baseline.TryGetProperty("error", out _), baselineRaw);
            Assert.Equal(1, baseline.GetProperty("totalCallers").GetInt32());
            Assert.True(baseline.GetProperty("diagnosticCount").GetInt32() > 1,
                baselineRaw);

            const int responseCap = 5_000;
            fixture.Tools.TestOnlyReferencesResponseMaxBytes = responseCap;
            string boundedRaw = CallSemantic(() => fixture.Tools.Callers(
                path: "Diagnostics/Diagnostics.fs", line: 2, column: 5,
                timeoutMs: 60_000));
            JsonElement bounded = Parse(boundedRaw);
            Assert.False(bounded.TryGetProperty("error", out _), boundedRaw);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(boundedRaw) <= responseCap,
                boundedRaw);
            Assert.Equal(baseline.GetProperty("totalCallers").GetInt32(),
                bounded.GetProperty("totalCallers").GetInt32());
            Assert.Equal(baseline.GetProperty("diagnosticCount").GetInt32(),
                bounded.GetProperty("diagnosticCount").GetInt32());
            Assert.NotEmpty(bounded.GetProperty("diagnostics").EnumerateArray());
            Assert.True(bounded.GetProperty("diagnostics").GetArrayLength() <
                        bounded.GetProperty("diagnosticCount").GetInt32(), boundedRaw);
            Assert.True(bounded.GetProperty("diagnosticsTruncated").GetBoolean());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FSharpCallGraphDedupesTfmsAndDisclosesTestAndGeneratedFilters()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-call-graph-filters").FullName;
        try
        {
            WriteProject(root, "Contracts/Contracts.fsproj",
                SdkProject("net8.0;net10.0", "Contracts.fs"));
            WriteProject(root, "Contracts/Contracts.fs",
                "module Contracts\nlet target value = value + 1\n");
            WriteProject(root, "Consumer.Tests/Consumer.Tests.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Normal.fs" />
                    <Compile Include="Generated.g.fs" />
                    <ProjectReference Include="../Contracts/Contracts.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Consumer.Tests/Normal.fs",
                "module Consumer.Normal\nlet normalUse value = Contracts.target value\n");
            WriteProject(root, "Consumer.Tests/Generated.g.fs",
                "module Consumer.Generated\nlet generatedUse value = Contracts.target value\n");

            using var fixture = Fixture.Create(root);
            JsonElement generatedExcluded = Parse(CallSemantic(() => fixture.Tools.Callers(
                path: "Contracts/Contracts.fs", line: 2, column: 5,
                projectPath: "Contracts/Contracts.fsproj", targetFramework: "net10.0",
                includeTests: true, includeGenerated: false, timeoutMs: 60_000)));
            Assert.False(generatedExcluded.TryGetProperty("error", out _),
                generatedExcluded.GetRawText());
            Assert.Equal(1, generatedExcluded.GetProperty("totalCallSites").GetInt32());
            JsonElement normal = Assert.Single(generatedExcluded.GetProperty("callers")
                .EnumerateArray());
            Assert.Equal("normalUse", normal.GetProperty("caller").GetProperty("name")
                .GetString());
            Assert.Equal(["net10.0", "net8.0"], Assert.Single(normal
                .GetProperty("callSites").EnumerateArray())
                .GetProperty("targetFrameworksScanned").EnumerateArray()
                .Select(value => value.GetString()!).ToArray());

            JsonElement generatedIncluded = Parse(CallSemantic(() => fixture.Tools.Callers(
                path: "Contracts/Contracts.fs", line: 2, column: 5,
                projectPath: "Contracts/Contracts.fsproj", targetFramework: "net10.0",
                includeTests: true, includeGenerated: true, timeoutMs: 60_000)));
            Assert.Equal(2, generatedIncluded.GetProperty("totalCallSites").GetInt32());

            JsonElement testsExcluded = Parse(CallSemantic(() => fixture.Tools.Callers(
                path: "Contracts/Contracts.fs", line: 2, column: 5,
                projectPath: "Contracts/Contracts.fsproj", targetFramework: "net10.0",
                includeTests: false, includeGenerated: true, timeoutMs: 60_000)));
            Assert.Equal(0, testsExcluded.GetProperty("totalCallSites").GetInt32());
            JsonElement testGroup = Assert.Single(testsExcluded.GetProperty("groups")
                .EnumerateArray(), group => group.GetProperty("project").GetString() ==
                    "Consumer.Tests/Consumer.Tests.fsproj");
            Assert.Equal("filtered", testGroup.GetProperty("status").GetString());
            Assert.Equal("test_project", testGroup.GetProperty("reason").GetString());
            Assert.True(testsExcluded.GetProperty("coverage")
                .GetProperty("workspaceComplete").GetBoolean());
            Assert.Contains("Test projects were excluded before counting",
                testsExcluded.GetProperty("summary").GetString());

            JsonElement bodyFiltered = Parse(CallSemantic(() => fixture.Tools.Callees(
                path: "Consumer.Tests/Normal.fs", line: 2, column: 5,
                projectPath: "Consumer.Tests/Consumer.Tests.fsproj",
                targetFramework: "net10.0",
                includeTests: false, includeGenerated: true, timeoutMs: 60_000)));
            Assert.False(bodyFiltered.TryGetProperty("error", out _),
                bodyFiltered.GetRawText());
            Assert.Equal(0, bodyFiltered.GetProperty("totalCallSites").GetInt32());
            Assert.Equal("filtered", Assert.Single(bodyFiltered.GetProperty("groups")
                .EnumerateArray()).GetProperty("status").GetString());
            Assert.True(bodyFiltered.GetProperty("coverage").GetProperty("complete")
                .GetBoolean());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FSharpCallersDiscardChangedDependentBinaryEvidenceAndKeepHealthyGroups()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-callers-dependent-binary").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj", SdkProject("net10.0", "Library.fs"));
            WriteProject(root, "Library/Library.fs",
                "module Library\nlet target value = value + 1\n");
            WriteProject(root, "A_Binary/A_Binary.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="A_Binary.fs" />
                    <ProjectReference Include="../Library/Library.fsproj" />
                    <Reference Include="CodeNav.Core">
                      <HintPath>../Lib/CodeNav.Core.dll</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "A_Binary/A_Binary.fs",
                "module A_Binary\nlet binaryUse value = Library.target value\n");
            WriteProject(root, "Z_Healthy/Z_Healthy.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Z_Healthy.fs" />
                    <ProjectReference Include="../Library/Library.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Z_Healthy/Z_Healthy.fs",
                "module Z_Healthy\nlet healthyUse value = Library.target value\n");
            string binary = Path.Combine(root, "Lib", "CodeNav.Core.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            File.Copy(typeof(SemanticService).Assembly.Location, binary);
            DateTime originalWriteTime = File.GetLastWriteTimeUtc(binary);

            using var fixture = Fixture.Create(root);
            int completedChecks = 0;
            fixture.Semantic.FSharpSemanticCheckCompletedForTest = _ =>
            {
                completedChecks++;
                if (completedChecks != 2) return;
                using var stream = new FileStream(binary, FileMode.Open,
                    FileAccess.ReadWrite, FileShare.Read);
                stream.Position = 32;
                int original = stream.ReadByte();
                Assert.True(original >= 0);
                stream.Position = 32;
                stream.WriteByte((byte)(original ^ 1));
                File.SetLastWriteTimeUtc(binary, originalWriteTime);
            };

            string raw = CallSemantic(() => fixture.Tools.Callers(
                path: "Library/Library.fs", line: 2, column: 5,
                timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(1, response.GetProperty("totalCallSites").GetInt32());
            Assert.Equal("healthyUse", Assert.Single(response.GetProperty("callers")
                .EnumerateArray()).GetProperty("caller").GetProperty("name").GetString());
            Assert.True(response.GetProperty("totalIsLowerBound").GetBoolean());
            JsonElement binaryGroup = Assert.Single(response.GetProperty("groups")
                .EnumerateArray(), group => group.GetProperty("project").GetString() ==
                    "A_Binary/A_Binary.fsproj");
            Assert.Equal("failed", binaryGroup.GetProperty("status").GetString());
            Assert.Equal("fsharp_semantic_reference_changed",
                binaryGroup.GetProperty("reason").GetString());
            Assert.Equal(0, binaryGroup.GetProperty("callSites").GetInt32());
            JsonElement healthyGroup = Assert.Single(response.GetProperty("groups")
                .EnumerateArray(), group => group.GetProperty("project").GetString() ==
                    "Z_Healthy/Z_Healthy.fsproj");
            Assert.Equal("scanned", healthyGroup.GetProperty("status").GetString());
            Assert.Equal(1, healthyGroup.GetProperty("callSites").GetInt32());
            Assert.Equal(1, response.GetProperty("coverage")
                .GetProperty("dependentsFailed").GetInt32());
            Assert.Contains("fsharp_workspace_dependent_failed",
                response.GetProperty("partialReason").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FSharpCallersDiscloseIncompleteEvaluatedDependentDiscovery()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-callers-discovery-partial").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj", SdkProject("net10.0", "Library.fs"));
            WriteProject(root, "Library/Library.fs", """
                module Library
                let target value = value + 1
                let localUse = target 1
                """);
            WriteProject(root, "Broken/Broken.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Broken.fs" />
                    <ProjectReference Include="../Library/Library.fsproj" PrivateAssets="all" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Broken/Broken.fs", "module Broken\nlet marker = 1\n");

            using var fixture = Fixture.Create(root);
            JsonElement response = Parse(CallSemantic(() => fixture.Tools.Callers(
                path: "Library/Library.fs", line: 2, column: 5,
                timeoutMs: 60_000)));
            Assert.False(response.TryGetProperty("error", out _), response.GetRawText());
            Assert.Equal(1, response.GetProperty("totalCallSites").GetInt32());
            Assert.True(response.GetProperty("totalIsLowerBound").GetBoolean());
            JsonElement coverage = response.GetProperty("coverage");
            Assert.False(coverage.TryGetProperty("dependentsTotal", out _));
            Assert.False(coverage.GetProperty("workspaceComplete").GetBoolean());
            Assert.True(coverage.GetProperty("potentialConsumersUnevaluated").GetInt32() > 0);
            Assert.Contains(coverage.GetProperty("details").EnumerateArray(), detail =>
                detail.GetProperty("kind").GetString() == "discoveryFailed" &&
                detail.GetProperty("project").GetString() == "Broken/Broken.fsproj");
            Assert.Contains("fsharp_workspace_dependent_discovery_incomplete",
                response.GetProperty("partialReason").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FSharpCallersDiscoverEvaluatedDependentsAndCountDistinctDeclaringProject()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-callers-evaluated-edge").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj",
                SdkProject("net10.0", "Library.fs"));
            WriteProject(root, "Library/Library.fs", """
                module Library
                let target value = value + 1
                let local = target 1
                """);
            WriteProject(root, "Consumer/Consumer.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <DependencyDir>../Library</DependencyDir>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Consumer.fs" />
                    <ProjectReference Include="$(DependencyDir)/Library.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Consumer/Consumer.fs",
                "module Consumer\nlet externalUse = Library.target 2\n");

            using var fixture = Fixture.Create(root);
            JsonElement declaration = Parse(CallSemantic(() => fixture.Tools.Callers(
                path: "Library/Library.fs", line: 2, column: 5,
                timeoutMs: 60_000)));
            Assert.False(declaration.TryGetProperty("error", out _),
                declaration.GetRawText());
            Assert.Equal(2, declaration.GetProperty("totalCallers").GetInt32());
            Assert.Equal(2, declaration.GetProperty("totalCallSites").GetInt32());
            Assert.False(declaration.TryGetProperty("totalIsLowerBound", out _));
            JsonElement coverage = declaration.GetProperty("coverage");
            Assert.Equal(1, coverage.GetProperty("dependentsTotal").GetInt32());
            Assert.Equal(1, coverage.GetProperty("dependentsScanned").GetInt32());
            Assert.Equal(2, coverage.GetProperty("potentialConsumers").GetInt32());
            Assert.Equal(2, coverage.GetProperty("potentialConsumersEvaluated").GetInt32());
            Assert.True(coverage.GetProperty("workspaceComplete").GetBoolean());
            Assert.Contains(declaration.GetProperty("callers").EnumerateArray(), caller =>
                caller.GetProperty("caller").GetProperty("name").GetString() == "local");
            Assert.Contains(declaration.GetProperty("callers").EnumerateArray(), caller =>
                caller.GetProperty("caller").GetProperty("name").GetString() ==
                "externalUse");

            JsonElement consumer = Parse(CallSemantic(() => fixture.Tools.Callers(
                path: "Consumer/Consumer.fs", line: 2, column: 27,
                timeoutMs: 60_000)));
            Assert.False(consumer.TryGetProperty("error", out _), consumer.GetRawText());
            Assert.Equal(declaration.GetProperty("totalCallers").GetInt32(),
                consumer.GetProperty("totalCallers").GetInt32());
            Assert.Equal(declaration.GetProperty("totalCallSites").GetInt32(),
                consumer.GetProperty("totalCallSites").GetInt32());
            Assert.Equal("Library/Library.fsproj", consumer.GetProperty("coverage")
                .GetProperty("declaringProject").GetString());
            JsonElement declaringGroup = Assert.Single(consumer.GetProperty("groups")
                .EnumerateArray(), group => group.GetProperty("project").GetString() ==
                                           "Library/Library.fsproj");
            Assert.Equal("scanned", declaringGroup.GetProperty("status").GetString());

            JsonElement consumerCallees = Parse(CallSemantic(() => fixture.Tools.Callees(
                path: "Consumer/Consumer.fs", line: 2, column: 5,
                timeoutMs: 60_000)));
            Assert.False(consumerCallees.TryGetProperty("error", out _),
                consumerCallees.GetRawText());
            JsonElement libraryTarget = Assert.Single(consumerCallees.GetProperty("callees")
                .EnumerateArray(), callee => callee.GetProperty("callee")
                    .GetProperty("name").GetString() == "target");
            Assert.Equal(1, libraryTarget.GetProperty("callee")
                .GetProperty("declarationsFromProjectReferenceClosureCount").GetInt32());
            Assert.Equal("selected_fsharp_body_with_project_reference_closure_targets",
                consumerCallees.GetProperty("coverage").GetProperty("scope").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }
}
