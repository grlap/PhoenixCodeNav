using System.Text.Json;
using CodeNav.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeNav.Tests;

public sealed class PhoenixCliArgumentTests
{
    [Fact]
    public void CommandParserKeepsTheAgentGrammarExactAndMachineActionable()
    {
        McpCommandLine parsed = McpCommandLine.Parse([
            "search_symbol",
            "--workspace-root=C:\\repo",
            "--index-db=C:\\repo\\.codenav\\index.db",
            "--query=--leading-dashes",
            "--pretty",
        ]);

        Assert.Equal(McpLaunchMode.Cli, parsed.Mode);
        Assert.Equal("C:\\repo", parsed.WorkspaceRoot);
        Assert.Equal("C:\\repo\\.codenav\\index.db", parsed.IndexDb);
        Assert.Null(parsed.DaemonIdle);
        Assert.True(parsed.Cli!.Pretty);
        Assert.Equal(["--query=--leading-dashes"], parsed.Cli.ToolArguments);

        PhoenixCliUsageException argumentSources = Assert.Throws<PhoenixCliUsageException>(() =>
            McpCommandLine.Parse([
                "search_symbol", "--json", "{}", "--args-file", "request.json",
            ]));
        Assert.Equal("conflicting_argument_sources", argumentSources.Issue.Reason);

        PhoenixCliUsageException mixed = Assert.Throws<PhoenixCliUsageException>(() =>
            McpCommandLine.Parse([
                "search_symbol", "--json", "{}", "--query", "IndexManager",
            ]));
        Assert.Equal("mixed_argument_sources", mixed.Issue.Reason);

        PhoenixCliUsageException lifecycle = Assert.Throws<PhoenixCliUsageException>(() =>
            McpCommandLine.Parse([
                "search_symbol", "--daemon-idle-ms", "100", "--query", "IndexManager",
            ]));
        Assert.Equal("daemonIdleMs", lifecycle.Issue.Field);
        Assert.Equal("unexpected_field", lifecycle.Issue.Reason);

        PhoenixCliUsageException helpAlias = Assert.Throws<PhoenixCliUsageException>(() =>
            McpCommandLine.Parse(["search_symbol", "--help"]));
        Assert.Equal("help", helpAlias.Issue.Field);
        Assert.Equal("help <tool>", helpAlias.Issue.Expected);

        McpCommandLine scopedDiscovery = McpCommandLine.Parse([
            "tools", "--workspace-root", "C:\\repo", "--index-db=C:\\repo\\index.db",
        ]);
        Assert.Equal(PhoenixCliAction.Tools, scopedDiscovery.Cli!.Action);
        Assert.Equal("C:\\repo", scopedDiscovery.WorkspaceRoot);
        Assert.Equal("C:\\repo\\index.db", scopedDiscovery.IndexDb);

        PhoenixCliUsageException missingWorkspace = Assert.Throws<PhoenixCliUsageException>(() =>
            McpCommandLine.Parse([
                "search_symbol", "--workspace-root", "--query", "IndexManager",
            ]));
        Assert.Equal("workspaceRoot", missingWorkspace.Issue.Field);
        Assert.Equal("missing_required_field", missingWorkspace.Issue.Reason);

        PhoenixCliUsageException duplicateWorkspace = Assert.Throws<PhoenixCliUsageException>(() =>
            McpCommandLine.Parse([
                "search_symbol", "--workspace-root", "C:\\first",
                "-w=C:\\second", "--query", "IndexManager",
            ]));
        Assert.Equal("workspaceRoot", duplicateWorkspace.Issue.Field);
        Assert.Equal("duplicate_field", duplicateWorkspace.Issue.Reason);

        PhoenixCliUsageException duplicatePretty = Assert.Throws<PhoenixCliUsageException>(() =>
            McpCommandLine.Parse(["tools", "--pretty", "--pretty"]));
        Assert.Equal("pretty", duplicatePretty.Issue.Field);
        Assert.Equal("duplicate_field", duplicatePretty.Issue.Reason);
    }

    [Fact]
    public void ScalarArgumentsFollowTheLiveSchemaWithoutPermissiveFallbacks()
    {
        JsonElement schema = ToolSchema("search_symbol");

        PhoenixCli.ArgumentBuildResult valid = PhoenixCli.ParseScalarArguments([
            "--query=--leading-dashes", "--includeGenerated", "--limit", "3",
        ], schema);
        Assert.Null(valid.Issue);
        Assert.Equal("--leading-dashes", valid.Arguments!["query"].GetString());
        Assert.True(valid.Arguments["includeGenerated"].GetBoolean());
        Assert.Equal(3, valid.Arguments["limit"].GetInt32());

        AssertIssue(
            PhoenixCli.ParseScalarArguments(["IndexManager"], schema),
            "arguments", "unexpected_value");
        AssertIssue(
            PhoenixCli.ParseScalarArguments(["--query"], schema),
            "query", "missing_field_value");
        AssertIssue(
            PhoenixCli.ParseScalarArguments([
                "--query", "First", "--query", "Second",
            ], schema),
            "query", "duplicate_field");
        AssertIssue(
            PhoenixCli.ParseScalarArguments(["--Query", "IndexManager"], schema),
            "Query", "unknown_field");
        AssertIssue(
            PhoenixCli.ParseScalarArguments(["--limit", "many"], schema),
            "limit", "invalid_field_type");
    }

    [Fact]
    public void CompleteJsonArgumentsRejectMalformedAmbiguousOrUnknownInput()
    {
        JsonElement schema = ToolSchema("search_symbol");

        PhoenixCli.ArgumentBuildResult valid = PhoenixCli.ParseJsonArguments(
            "{\"query\":\"IndexManager\",\"limit\":2}", schema);
        Assert.Null(valid.Issue);
        Assert.Equal("IndexManager", valid.Arguments!["query"].GetString());

        AssertIssue(
            PhoenixCli.ParseJsonArguments("{", schema),
            "arguments", "invalid_json");
        AssertIssue(
            PhoenixCli.ParseJsonArguments("[]", schema),
            "arguments", "invalid_field_type");
        AssertIssue(
            PhoenixCli.ParseJsonArguments("{\"Query\":\"IndexManager\"}", schema),
            "Query", "unknown_field");
        AssertIssue(
            PhoenixCli.ParseJsonArguments(
                "{\"query\":\"First\",\"query\":\"Second\"}", schema),
            "query", "duplicate_field");
    }

    [Theory]
    [InlineData("unexpected_tool_result_shape")]
    [InlineData("tool_result_not_json")]
    public void InvalidDaemonToolResultsAreNeverReportedAsDaemonUnavailable(string reason)
    {
        var command = new PhoenixCliCommand(
            PhoenixCliAction.Invoke,
            "search_symbol",
            [],
            null,
            null,
            Pretty: false);
        JsonElement payload = PhoenixCli.CreateInvalidToolResultPayload(
            command,
            new PhoenixCliResultException(reason, "The daemon returned an invalid tool result."));

        Assert.Equal("phoenix_tool_result_invalid", payload.GetProperty("error").GetString());
        Assert.Equal(reason, payload.GetProperty("reason").GetString());
        Assert.False(payload.GetProperty("retryable").GetBoolean());
        Assert.Equal(BuildInfo.Stamp,
            payload.GetProperty("meta").GetProperty("build").GetString());
        Assert.False(payload.GetProperty("meta").TryGetProperty("indexMode", out _));
    }

    [Fact]
    public void DaemonToolResultExtractionClassifiesEachInvalidShape()
    {
        var noJsonBlock = new CallToolResult { Content = [] };
        PhoenixCliResultException shape = Assert.Throws<PhoenixCliResultException>(() =>
            PhoenixCli.ExtractPayload(noJsonBlock));
        Assert.Equal("unexpected_tool_result_shape", shape.Reason);

        var invalidJson = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "not-json" }],
        };
        PhoenixCliResultException json = Assert.Throws<PhoenixCliResultException>(() =>
            PhoenixCli.ExtractPayload(invalidJson));
        Assert.Equal("tool_result_not_json", json.Reason);

        var scalarStructured = new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(42),
            Content = [],
        };
        PhoenixCliResultException scalar = Assert.Throws<PhoenixCliResultException>(() =>
            PhoenixCli.ExtractPayload(scalarStructured));
        Assert.Equal("unexpected_tool_result_shape", scalar.Reason);

        var arrayText = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "[]" }],
        };
        PhoenixCliResultException array = Assert.Throws<PhoenixCliResultException>(() =>
            PhoenixCli.ExtractPayload(arrayText));
        Assert.Equal("unexpected_tool_result_shape", array.Reason);

        JsonElement structuredObject = JsonSerializer.SerializeToElement(
            new { error = "bad_request" }, Json.Options);
        var mirrored = new CallToolResult
        {
            StructuredContent = structuredObject,
            Content = [new TextContentBlock { Text = structuredObject.GetRawText() }],
        };
        Assert.Equal("bad_request", PhoenixCli.ExtractPayload(mirrored)
            .GetProperty("error").GetString());
    }

    [Fact]
    public void CliGeneratedErrorsReuseThePhoenixEncoderAndExistingHardBudget()
    {
        var unknownTool = new ArgumentValidationIssue(
            "tool",
            "unknown_tool",
            "an exact name returned by 'tools'",
            "Phoenix MCP does not advertise the requested tool name.");

        JsonElement reproduced = PhoenixCli.BadRequest(new string('\u00e9', 20_000), unknownTool);
        string reproducedJson = reproduced.GetRawText();
        Assert.True(Json.Utf8Bytes(reproducedJson) <= Json.HardBudgetBytes);
        Assert.Contains(new string('\u00e9', 32), reproducedJson, StringComparison.Ordinal);
        Assert.False(reproduced.TryGetProperty("truncated", out _));

        JsonElement oversized = PhoenixCli.BadRequest(new string('\u00e9', 40_000), unknownTool);
        Assert.True(Json.Utf8Bytes(oversized.GetRawText()) <= Json.HardBudgetBytes);
        Assert.True(oversized.GetProperty("truncated").GetBoolean());
        Assert.Equal("tool", oversized.GetProperty("truncatedField").GetString());
        Assert.DoesNotContain("\\u00e9", oversized.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var unknownField = new ArgumentValidationIssue(
            new string('\u00e9', 40_000),
            "unknown_field",
            "a property from the live MCP input schema",
            "The live MCP input schema does not define the requested field.");
        JsonElement oversizedField = PhoenixCli.BadRequest("search_symbol", unknownField);
        Assert.True(Json.Utf8Bytes(oversizedField.GetRawText()) <= Json.HardBudgetBytes);
        Assert.True(oversizedField.GetProperty("truncated").GetBoolean());
        Assert.Equal("field", oversizedField.GetProperty("truncatedField").GetString());
    }

    [Fact]
    public void PrettyOutputChangesWhitespaceWithoutChangingThePhoenixEncoding()
    {
        JsonElement payload = JsonSerializer.SerializeToElement(
            new { name = "Caf\u00e9 <agent> & Phoenix" }, Json.Options);

        string compact = PhoenixCli.SerializeOutput(payload, pretty: false);
        string pretty = PhoenixCli.SerializeOutput(payload, pretty: true);

        Assert.Contains("Caf\u00e9 <agent> & Phoenix", compact, StringComparison.Ordinal);
        Assert.Contains("Caf\u00e9 <agent> & Phoenix", pretty, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', compact);
        Assert.Contains('\n', pretty);
        using JsonDocument compactDocument = JsonDocument.Parse(compact);
        using JsonDocument prettyDocument = JsonDocument.Parse(pretty);
        Assert.True(JsonElement.DeepEquals(
            compactDocument.RootElement,
            prettyDocument.RootElement));
    }

    [Fact]
    public void ReservedCliNamesCannotShadowLiveMcpParameters()
    {
        IReadOnlyList<McpServerTool> tools = PhoenixCli.RegisteredTools;
        int inspectedPropertyCount = 0;

        foreach (McpServerTool tool in tools)
        {
            if (!tool.ProtocolTool.InputSchema.TryGetProperty(
                    "properties", out JsonElement properties))
                continue;

            Assert.True(properties.ValueKind == JsonValueKind.Object,
                $"{tool.ProtocolTool.Name} input schema 'properties' must be an object.");
            JsonProperty[] schemaProperties = properties.EnumerateObject().ToArray();
            inspectedPropertyCount += schemaProperties.Length;
            string[] collisions = schemaProperties
                .Select(property => property.Name)
                .Where(PhoenixCliCommand.ReservedToolParameterNames.Contains)
                .ToArray();
            Assert.True(collisions.Length == 0,
                $"{tool.ProtocolTool.Name} collides with CLI-reserved parameter(s): " +
                string.Join(", ", collisions));
        }

        Assert.True(inspectedPropertyCount > 0,
            "The reserved CLI-name contract test did not inspect any MCP input-schema properties.");
    }

    [Fact]
    public void WindowsArgumentSourcePredicateRejectsEveryNonRegularAttribute()
    {
        Assert.False(PhoenixCli.IsRegularWindowsArgumentSource(
            FileAttributes.Normal | FileAttributes.ReparsePoint));
        Assert.False(PhoenixCli.IsRegularWindowsArgumentSource(
            FileAttributes.Normal | FileAttributes.Directory));
        Assert.False(PhoenixCli.IsRegularWindowsArgumentSource(
            FileAttributes.Normal | FileAttributes.Device));
        Assert.True(PhoenixCli.IsRegularWindowsArgumentSource(
            FileAttributes.Normal | FileAttributes.Archive | FileAttributes.ReadOnly));
    }

    [Fact]
    public void CompleteDiscoveryDocumentsStayWithinTheEstablishedResponseBudget()
    {
        IReadOnlyList<McpServerTool> tools = PhoenixCli.RegisteredTools;

        Assert.True(Json.Utf8Bytes(PhoenixCli.CreateToolsPayload(tools).GetRawText()) <=
            Json.HardBudgetBytes);
        foreach (McpServerTool tool in tools)
        {
            Assert.True(
                Json.Utf8Bytes(PhoenixCli.CreateToolHelpPayload(tool).GetRawText()) <=
                    Json.HardBudgetBytes,
                $"help {tool.ProtocolTool.Name} exceeds the Phoenix response budget");
        }
    }

    [Fact]
    public async Task TeardownAndBrokenOutputCannotReplaceOrRepeatADecisiveResult()
    {
        var teardown = new ThrowingAsyncDisposable();
        await PhoenixCli.DisposeQuietlyAsync(teardown);
        Assert.Equal(1, teardown.DisposeCalls);

        var output = new ThrowingWriteStream();
        JsonElement payload = JsonSerializer.SerializeToElement(
            new { error = "bad_request" }, Json.Options);
        int exitCode = await PhoenixCli.WriteJsonAsync(
            payload,
            pretty: false,
            exitCode: 2,
            outputStream: output);

        Assert.Equal(2, exitCode);
        Assert.Equal(1, output.WriteCalls);
    }

    [Fact]
    public void InternalCliFailuresRemainStructuredAndNonRetryable()
    {
        var command = new PhoenixCliCommand(
            PhoenixCliAction.Invoke,
            "search_symbol",
            [],
            null,
            null,
            Pretty: false);

        JsonElement payload = PhoenixCli.CreateInternalErrorPayload(
            command,
            new InvalidOperationException("Caf\u00e9 internal failure"));

        Assert.Equal("phoenix_cli_internal_error", payload.GetProperty("error").GetString());
        Assert.Equal("InvalidOperationException", payload.GetProperty("reason").GetString());
        Assert.Equal("Caf\u00e9 internal failure", payload.GetProperty("detail").GetString());
        Assert.False(payload.GetProperty("retryable").GetBoolean());
        Assert.True(Json.Utf8Bytes(payload.GetRawText()) <= Json.HardBudgetBytes);

        var oversizedTool = command with { ToolName = new string('\u00e9', 40_000) };
        JsonElement boundedTool = PhoenixCli.CreateInternalErrorPayload(
            oversizedTool,
            new InvalidOperationException("internal failure"));
        Assert.True(Json.Utf8Bytes(boundedTool.GetRawText()) <= Json.HardBudgetBytes);
        Assert.True(boundedTool.GetProperty("truncated").GetBoolean());
        Assert.Equal("tool", boundedTool.GetProperty("truncatedField").GetString());

        JsonElement boundedDetail = PhoenixCli.CreateInternalErrorPayload(
            command,
            new InvalidOperationException(new string('\u00e9', 40_000)));
        Assert.True(Json.Utf8Bytes(boundedDetail.GetRawText()) <= Json.HardBudgetBytes);
        Assert.True(boundedDetail.GetProperty("truncated").GetBoolean());
        Assert.Equal("detail", boundedDetail.GetProperty("truncatedField").GetString());
    }

    [Fact]
    public void ProtocolToolRejectionsAreNotReportedAsDaemonUnavailability()
    {
        var protocol = new McpProtocolException(
            new string('\u00e9', 40_000),
            McpErrorCode.InternalError);
        PhoenixCliCallFailure failure = Assert.IsType<PhoenixCliCallFailure>(
            PhoenixCli.ClassifyCallFailure(protocol));
        Assert.Equal(McpErrorCode.InternalError.ToString(), failure.Reason);
        Assert.True(failure.Retryable);

        var command = new PhoenixCliCommand(
            PhoenixCliAction.Invoke,
            "search_symbol",
            [],
            null,
            null,
            Pretty: false);
        JsonElement payload = PhoenixCli.CreateCallFailurePayload(command, failure);

        Assert.Equal("daemon_request_rejected", payload.GetProperty("error").GetString());
        Assert.Equal("search_symbol", payload.GetProperty("tool").GetString());
        Assert.Equal(McpErrorCode.InternalError.ToString(),
            payload.GetProperty("reason").GetString());
        Assert.True(payload.GetProperty("retryable").GetBoolean());
        Assert.False(payload.TryGetProperty("indexMode", out _));
        Assert.True(Json.Utf8Bytes(payload.GetRawText()) <= Json.HardBudgetBytes);
        Assert.True(payload.GetProperty("truncated").GetBoolean());
        Assert.Equal("detail", payload.GetProperty("truncatedField").GetString());

        Assert.Null(PhoenixCli.ClassifyCallFailure(new McpException("stream failed")));
    }

    [Fact]
    public void UnavailablePayloadUsesTheSharedPhoenixJsonEncoder()
    {
        JsonElement payload = CodeNav.Mcp.Daemon.UnavailableMcpShim.CreatePayload(
            new CodeNav.Mcp.Daemon.DaemonUnavailableFailure(
                "daemon_cli_transport_failed",
                "Caf\u00e9 daemon unavailable",
                "Retry once.",
                Retryable: true),
            "search_symbol");

        Assert.Contains("Caf\u00e9", payload.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void SdkAndStreamFailuresRemainTransportUnavailable()
    {
        Exception[] transportFailures =
        [
            new IOException("stream closed"),
            new McpException("initialize failed"),
            new JsonException("invalid transport frame"),
            new TimeoutException("SDK initialization timeout"),
            new OperationCanceledException("SDK internal cancellation"),
        ];

        foreach (Exception failure in transportFailures)
        {
            var classified = Assert.IsType<CodeNav.Mcp.Daemon.DaemonUnavailableFailure>(
                PhoenixCli.ClassifyTransportFailure(failure));
            Assert.Equal("daemon_cli_transport_failed", classified.Cause);
            Assert.True(classified.Retryable);
        }

        Assert.Null(PhoenixCli.ClassifyTransportFailure(
            new InvalidOperationException("CLI bug")));
    }

    private static JsonElement ToolSchema(string name)
    {
        IReadOnlyList<McpServerTool> tools = PhoenixCli.RegisteredTools;
        return Assert.Single(tools, tool => tool.ProtocolTool.Name == name)
            .ProtocolTool.InputSchema;
    }

    private static void AssertIssue(
        PhoenixCli.ArgumentBuildResult result,
        string field,
        string reason)
    {
        Assert.Null(result.Arguments);
        Assert.NotNull(result.Issue);
        Assert.Equal(field, result.Issue.Field);
        Assert.Equal(reason, result.Issue.Reason);
    }

    private sealed class ThrowingAsyncDisposable : IAsyncDisposable
    {
        internal int DisposeCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            throw new IOException("teardown failed");
        }
    }

    private sealed class ThrowingWriteStream : Stream
    {
        internal int WriteCalls { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            throw new IOException("pipe closed");
        }
    }
}
