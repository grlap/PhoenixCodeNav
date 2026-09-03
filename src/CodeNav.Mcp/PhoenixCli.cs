using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeNav.Mcp.Daemon;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp;

internal enum PhoenixCliAction
{
    Tools,
    Help,
    Schema,
    Invoke,
}

internal sealed record PhoenixCliCommand(
    PhoenixCliAction Action,
    string? ToolName,
    IReadOnlyList<string> ToolArguments,
    string? JsonArguments,
    string? ArgumentsFile,
    bool Pretty)
{
    internal static readonly IReadOnlySet<string> ReservedToolParameterNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "workspaceRoot",
            "indexDb",
            "json",
            "argsFile",
            "pretty",
            "rebuild",
            "keepalive",
            "daemonIdleMs",
            "help",
            "w",
            "h",
        };

    internal static McpCommandLine Parse(string[] arguments)
    {
        string verb = arguments[0];
        PhoenixCliAction action = verb switch
        {
            "tools" => PhoenixCliAction.Tools,
            "help" => PhoenixCliAction.Help,
            "schema" => PhoenixCliAction.Schema,
            _ => PhoenixCliAction.Invoke,
        };
        int index = 1;
        string? toolName = action == PhoenixCliAction.Invoke ? verb : null;
        if (action is PhoenixCliAction.Help or PhoenixCliAction.Schema)
        {
            if (index >= arguments.Length || arguments[index].StartsWith("-", StringComparison.Ordinal))
                throw Usage(verb, "tool", "missing_required_field",
                    "tool name", "This discovery command requires an exact MCP tool name.");
            toolName = arguments[index++];
        }

        string? workspaceRoot = null;
        string? indexDb = null;
        bool pretty = false;
        string? json = null;
        string? argumentsFile = null;
        var toolArguments = new List<string>();
        var seenGlobalOptions = new HashSet<string>(StringComparer.Ordinal);

        while (index < arguments.Length)
        {
            string argument = arguments[index++];
            if (TryTakeRequiredValue(
                    argument, "--workspace-root", "workspaceRoot",
                    arguments, ref index, verb, out string? optionValue) ||
                TryTakeRequiredValue(
                    argument, "-w", "workspaceRoot",
                    arguments, ref index, verb, out optionValue))
            {
                RequireUnique(seenGlobalOptions, "workspaceRoot", verb);
                workspaceRoot = optionValue;
                continue;
            }
            if (TryTakeRequiredValue(
                    argument, "--index-db", "indexDb",
                    arguments, ref index, verb, out optionValue))
            {
                RequireUnique(seenGlobalOptions, "indexDb", verb);
                indexDb = optionValue;
                continue;
            }
            if (TryTakeRequiredValue(
                    argument, "--json", "arguments",
                    arguments, ref index, verb, out optionValue))
            {
                RequireUnique(seenGlobalOptions, "json", verb);
                json = optionValue;
                continue;
            }
            if (TryTakeRequiredValue(
                    argument, "--args-file", "arguments",
                    arguments, ref index, verb, out optionValue))
            {
                RequireUnique(seenGlobalOptions, "argsFile", verb);
                argumentsFile = optionValue;
                continue;
            }

            switch (argument)
            {
                case "--rebuild":
                    throw Usage(verb, "rebuild", "unexpected_field",
                        "refresh_index with force='full'",
                        "The one-shot CLI never rebuilds as a side effect; call the refresh_index tool explicitly when a full rebuild is intended.");
                case "--keepalive":
                    throw Usage(verb, "keepalive", "unexpected_field",
                        "MCP host lifecycle configuration",
                        "The one-shot CLI cannot change shared-daemon lifetime; remove --keepalive.");
                case "--pretty":
                    RequireUnique(seenGlobalOptions, "pretty", verb);
                    pretty = true;
                    break;
                case "--daemon-idle-ms":
                    throw Usage(verb, "daemonIdleMs", "unexpected_field",
                        "MCP host lifecycle configuration",
                        "The one-shot CLI cannot change shared-daemon lifetime; remove --daemon-idle-ms.");
                case "--help" or "-h":
                    throw Usage(verb, "help", "unexpected_field",
                        "help <tool>",
                        "CLI tool help uses the exact 'help <tool>' command.");
                default:
                    toolArguments.Add(argument);
                    break;
            }
        }

        if (json is not null && argumentsFile is not null)
            throw Usage(verb, "arguments", "conflicting_argument_sources",
                "one of --json or --args-file",
                "--json and --args-file cannot be used together.");
        if ((json is not null || argumentsFile is not null) && toolArguments.Count != 0)
            throw Usage(verb, "arguments", "mixed_argument_sources",
                "one complete argument source",
                "Direct --<parameter> values cannot be combined with --json or --args-file.");
        if (action != PhoenixCliAction.Invoke &&
            (json is not null || argumentsFile is not null || toolArguments.Count != 0))
            throw Usage(verb, "arguments", "unexpected_field",
                "only global CLI options",
                "Phoenix discovery commands do not accept tool arguments.");

        var cli = new PhoenixCliCommand(
            action, toolName, toolArguments, json, argumentsFile, pretty);
        return new McpCommandLine(
            McpLaunchMode.Cli,
            workspaceRoot,
            indexDb,
            Rebuild: false,
            KeepAlive: false,
            DaemonIdle: null,
            Help: false,
            Cli: cli);
    }

    private static bool TryTakeRequiredValue(
        string argument,
        string option,
        string field,
        string[] arguments,
        ref int index,
        string verb,
        out string? value)
    {
        if (string.Equals(argument, option, StringComparison.Ordinal))
        {
            value = RequiredValue(arguments, ref index, option, field, verb);
            return true;
        }

        string prefix = option + "=";
        if (!argument.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = null;
            return false;
        }

        value = argument[prefix.Length..];
        if (string.IsNullOrWhiteSpace(value))
            throw Usage(verb, field, "missing_required_field",
                "non-empty string", "A CLI global option is missing its value.");
        return true;
    }

    private static string RequiredValue(
        string[] arguments,
        ref int index,
        string option,
        string field,
        string verb)
    {
        if (index >= arguments.Length ||
            string.IsNullOrWhiteSpace(arguments[index]) ||
            arguments[index].StartsWith("--", StringComparison.Ordinal))
            throw Usage(verb, field, "missing_required_field",
                "non-empty string", "A CLI global option is missing its value.");
        return arguments[index++];
    }

    private static void RequireUnique(
        HashSet<string> seen,
        string field,
        string verb)
    {
        if (!seen.Add(field))
            throw Usage(verb, field, "duplicate_field", "one value",
                "A CLI global option was supplied more than once.");
    }

    private static PhoenixCliUsageException Usage(
        string tool,
        string field,
        string reason,
        string expected,
        string detail) => new(tool, new ArgumentValidationIssue(
            field, reason, expected, detail));
}

internal sealed class PhoenixCliUsageException : ArgumentException
{
    internal PhoenixCliUsageException(string toolName, ArgumentValidationIssue issue)
        : base(issue.Detail)
    {
        ToolName = toolName;
        Issue = issue;
    }

    internal string ToolName { get; }
    internal ArgumentValidationIssue Issue { get; }
}

internal static class PhoenixCli
{
    private static readonly JsonSerializerOptions CompactJson = Json.Options;
    private static readonly JsonSerializerOptions PrettyJson = new(Json.Options)
    {
        WriteIndented = true,
    };
    private static readonly Lazy<IReadOnlyList<McpServerTool>> ToolRegistrations =
        new(() => ValidatedMcpToolRegistration.CreateNavigationTools());

    internal static IReadOnlyList<McpServerTool> RegisteredTools =>
        ToolRegistrations.Value;

    internal static async Task<int> RunAsync(
        PhoenixCliCommand command,
        DaemonProxy? proxy,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<McpServerTool> tools = RegisteredTools;

            return command.Action switch
            {
                PhoenixCliAction.Tools => await WriteJsonAsync(
                    CreateToolsPayload(tools),
                    command.Pretty),
                PhoenixCliAction.Help => await WriteToolHelpAsync(
                    command, tools, schemaOnly: false),
                PhoenixCliAction.Schema => await WriteToolHelpAsync(
                    command, tools, schemaOnly: true),
                PhoenixCliAction.Invoke => await ValidateAndInvokeAsync(
                    command,
                    proxy ?? throw new InvalidOperationException(
                        "A Phoenix CLI invocation requires a daemon proxy."),
                    tools,
                    cancellationToken),
                _ => throw new InvalidOperationException("Unknown Phoenix CLI action."),
            };
        }
        catch (DaemonProxyFailureException ex)
        {
            return await WriteUnavailableAsync(command, ex.Failure).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (PhoenixCliResultException ex)
        {
            return await WriteJsonAsync(
                CreateInvalidToolResultPayload(command, ex),
                command.Pretty,
                exitCode: 1).ConfigureAwait(false);
        }
        catch (Exception ex) when (ClassifyTransportFailure(ex) is not null)
        {
            return await WriteUnavailableAsync(
                command,
                ClassifyTransportFailure(ex)!).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return await WriteJsonAsync(
                CreateInternalErrorPayload(command, ex),
                command.Pretty,
                exitCode: 1).ConfigureAwait(false);
        }
    }

    internal static Task<int> WriteUnavailableAsync(
        PhoenixCliCommand command,
        DaemonUnavailableFailure failure)
    {
        string toolName = command.ToolName ?? command.Action.ToString().ToLowerInvariant();
        JsonElement payload = UnavailableMcpShim.CreatePayload(failure, toolName);
        return WriteJsonAsync(payload, command.Pretty, exitCode: 3);
    }

    internal static Task<int> WriteBadRequestAsync(
        string toolName,
        ArgumentValidationIssue issue,
        bool pretty = false) => WriteJsonAsync(
            BadRequest(toolName, issue), pretty, exitCode: 2);

    private static async Task<int> WriteToolHelpAsync(
        PhoenixCliCommand command,
        IReadOnlyList<McpServerTool> tools,
        bool schemaOnly)
    {
        McpServerTool? tool = FindTool(tools, command.ToolName!);
        if (tool is null)
        {
            return await WriteBadRequestAsync(
                command.ToolName!,
                UnknownTool(command.ToolName!),
                command.Pretty).ConfigureAwait(false);
        }

        JsonElement payload = schemaOnly
            ? tool.ProtocolTool.InputSchema
            : CreateToolHelpPayload(tool);
        return await WriteJsonAsync(payload, command.Pretty).ConfigureAwait(false);
    }

    private static async Task<int> ValidateAndInvokeAsync(
        PhoenixCliCommand command,
        DaemonProxy proxy,
        IReadOnlyList<McpServerTool> tools,
        CancellationToken cancellationToken)
    {
        McpServerTool? tool = FindTool(tools, command.ToolName!);
        if (tool is null)
        {
            return await WriteBadRequestAsync(
                command.ToolName!,
                UnknownTool(command.ToolName!),
                command.Pretty).ConfigureAwait(false);
        }

        ArgumentBuildResult built = await BuildArgumentsAsync(
            command, tool.ProtocolTool.InputSchema, cancellationToken).ConfigureAwait(false);
        if (built.Issue is not null)
        {
            return await WriteBadRequestAsync(
                tool.ProtocolTool.Name, built.Issue, command.Pretty).ConfigureAwait(false);
        }

        var validationArguments = built.Arguments!.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        ArgumentValidationIssue? validation =
            ((ValidatingMcpServerTool)tool).Validate(validationArguments);
        if (validation is not null)
        {
            return await WriteBadRequestAsync(
                tool.ProtocolTool.Name, validation, command.Pretty).ConfigureAwait(false);
        }

        PhoenixCliInvocationResult invocation = await InvokeOverDaemonAsync(
            command,
            proxy,
            tool,
            validationArguments,
            cancellationToken).ConfigureAwait(false);
        return await WriteJsonAsync(
            invocation.Payload,
            command.Pretty,
            invocation.ExitCode).ConfigureAwait(false);
    }

    internal static async Task<PhoenixCliInvocationResult> InvokeOverDaemonAsync(
        PhoenixCliCommand command,
        DaemonProxy proxy,
        McpServerTool tool,
        IReadOnlyDictionary<string, JsonElement> validationArguments,
        CancellationToken cancellationToken)
    {
        Stream? daemon = null;
        try
        {
            daemon = await proxy.ConnectOrStartAsync(cancellationToken).ConfigureAwait(false);
            return await InvokeOverStreamAsync(
                command,
                daemon,
                tool,
                validationArguments,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DisposeQuietlyAsync(daemon).ConfigureAwait(false);
        }
    }

    private static async Task<PhoenixCliInvocationResult> InvokeOverStreamAsync(
        PhoenixCliCommand command,
        Stream daemon,
        McpServerTool tool,
        IReadOnlyDictionary<string, JsonElement> validationArguments,
        CancellationToken cancellationToken)
    {
        var transport = new StreamClientTransport(
            daemon, daemon, NullLoggerFactory.Instance);
        McpClient? client = null;
        try
        {
            client = await McpClient.CreateAsync(
                transport, cancellationToken: cancellationToken).ConfigureAwait(false);
            var callArguments = validationArguments.ToDictionary(
                pair => pair.Key,
                pair => (object?)pair.Value,
                StringComparer.Ordinal);

            CallToolResult result;
            try
            {
                result = await client.CallToolAsync(
                    tool.ProtocolTool.Name,
                    callArguments,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (McpProtocolException ex)
            {
                PhoenixCliCallFailure failure = ClassifyCallFailure(ex)!;
                return new PhoenixCliInvocationResult(
                    CreateCallFailurePayload(command, failure),
                    ExitCode: 1);
            }
            JsonElement payload = ExtractPayload(result);
            return new PhoenixCliInvocationResult(payload, ExitCode(result, payload));
        }
        finally
        {
            await DisposeQuietlyAsync(client).ConfigureAwait(false);
        }
    }

    internal static async ValueTask DisposeQuietlyAsync(IAsyncDisposable? resource)
    {
        if (resource is null)
            return;
        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // A decisive CLI outcome must not be replaced by teardown failure.
        }
    }

    private static async Task<ArgumentBuildResult> BuildArgumentsAsync(
        PhoenixCliCommand command,
        JsonElement inputSchema,
        CancellationToken cancellationToken)
    {
        if (command.JsonArguments is not null || command.ArgumentsFile is not null)
        {
            try
            {
                if (command.JsonArguments is not null)
                {
                    return ParseJsonArguments(command.JsonArguments, inputSchema);
                }

                if (command.ArgumentsFile == "-")
                {
                    await using Stream input = Console.OpenStandardInput();
                    return await ParseJsonArgumentsAsync(
                        input, inputSchema, cancellationToken).ConfigureAwait(false);
                }

                string argumentsPath = command.ArgumentsFile!;
                if (!OperatingSystem.IsWindows())
                {
                    if (!DaemonUnixFileAuthority.TryLStat(
                            argumentsPath, out DaemonUnixFileInfo sourceInfo))
                        throw new IOException("Phoenix CLI could not inspect the argument source.");
                    if (!sourceInfo.IsRegular)
                    {
                        return ArgumentBuildResult.Failed(new ArgumentValidationIssue(
                            "arguments",
                            "argument_source_not_regular",
                            "regular JSON file or '-' for stdin",
                            "--args-file accepts only a regular file or '-' for EOF-framed stdin."));
                    }
                }
                else
                {
                    FileAttributes sourceAttributes = File.GetAttributes(argumentsPath);
                    if (!IsRegularWindowsArgumentSource(sourceAttributes))
                    {
                        return ArgumentBuildResult.Failed(new ArgumentValidationIssue(
                            "arguments",
                            "argument_source_not_regular",
                            "regular JSON file or '-' for stdin",
                            "--args-file accepts only a regular file or '-' for EOF-framed stdin."));
                    }
                }

                await using var inputFile = new FileStream(
                    argumentsPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (!inputFile.CanSeek)
                {
                    return ArgumentBuildResult.Failed(new ArgumentValidationIssue(
                        "arguments",
                        "argument_source_not_regular",
                        "regular JSON file or '-' for stdin",
                        "--args-file accepts only a regular file or '-' for EOF-framed stdin."));
                }
                return await ParseJsonArgumentsAsync(
                    inputFile, inputSchema, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or NotSupportedException)
            {
                return ArgumentBuildResult.Failed(new ArgumentValidationIssue(
                    "arguments",
                    "argument_source_unavailable",
                    "readable JSON object",
                    $"Phoenix CLI could not read the argument source ({ex.GetType().Name})."));
            }
        }

        return ParseScalarArguments(command.ToolArguments, inputSchema);
    }

    internal static ArgumentBuildResult ParseJsonArguments(
        string json,
        JsonElement inputSchema)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ArgumentBuildResult.Failed(new ArgumentValidationIssue(
                    "arguments", "invalid_field_type", "object",
                    "The complete CLI argument payload must be a JSON object."));
            }

            return BuildJsonArguments(document.RootElement, inputSchema);
        }
        catch (JsonException)
        {
            return ArgumentBuildResult.Failed(new ArgumentValidationIssue(
                "arguments", "invalid_json", "JSON object",
                "The complete CLI argument payload is not valid JSON."));
        }
    }

    internal static bool IsRegularWindowsArgumentSource(FileAttributes attributes) =>
        (attributes & (FileAttributes.Directory |
                       FileAttributes.Device |
                       FileAttributes.ReparsePoint)) == 0;

    private static async Task<ArgumentBuildResult> ParseJsonArgumentsAsync(
        Stream json,
        JsonElement inputSchema,
        CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(
                json, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ArgumentBuildResult.Failed(new ArgumentValidationIssue(
                    "arguments", "invalid_field_type", "object",
                    "The complete CLI argument payload must be a JSON object."));
            }
            return BuildJsonArguments(document.RootElement, inputSchema);
        }
        catch (JsonException)
        {
            return ArgumentBuildResult.Failed(new ArgumentValidationIssue(
                "arguments", "invalid_json", "JSON object",
                "The complete CLI argument payload is not valid JSON."));
        }
    }

    private static ArgumentBuildResult BuildJsonArguments(
        JsonElement root,
        JsonElement inputSchema)
    {
        Dictionary<string, JsonElement> properties = SchemaProperties(inputSchema);
        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!properties.ContainsKey(property.Name))
            {
                return ArgumentBuildResult.Failed(new ArgumentValidationIssue(
                    property.Name, "unknown_field", "a property from the live MCP input schema",
                    "The live MCP input schema does not define the requested field."));
            }
            if (!arguments.TryAdd(property.Name, property.Value.Clone()))
            {
                return ArgumentBuildResult.Failed(new ArgumentValidationIssue(
                    property.Name, "duplicate_field", "one value",
                    "A tool field was supplied more than once."));
            }
        }
        return ArgumentBuildResult.Succeeded(arguments);
    }

    internal static ArgumentBuildResult ParseScalarArguments(
        IReadOnlyList<string> tokens,
        JsonElement inputSchema)
    {
        Dictionary<string, JsonElement> properties = SchemaProperties(inputSchema);
        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (int index = 0; index < tokens.Count; index++)
        {
            string token = tokens[index];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
            {
                return ArgumentBuildResult.Failed(new ArgumentValidationIssue(
                    "arguments", "unexpected_value", "--<wireParameter> <value>",
                    "The CLI received a positional value where a named tool field was required."));
            }

            int equals = token.IndexOf('=');
            string name = equals < 0 ? token[2..] : token[2..equals];
            string? raw = equals < 0 ? null : token[(equals + 1)..];
            if (!properties.TryGetValue(name, out JsonElement propertySchema))
            {
                return ArgumentBuildResult.Failed(new ArgumentValidationIssue(
                    name, "unknown_field", "a property from the live MCP input schema",
                    "The live MCP input schema does not define the requested field."));
            }
            if (arguments.ContainsKey(name))
            {
                return ArgumentBuildResult.Failed(new ArgumentValidationIssue(
                    name, "duplicate_field", "one value",
                    "A tool field was supplied more than once."));
            }

            HashSet<string> types = SchemaTypes(propertySchema);
            bool boolean = types.SetEquals(["boolean"]);
            if (raw is null && boolean &&
                (index + 1 >= tokens.Count || tokens[index + 1].StartsWith("--", StringComparison.Ordinal)))
            {
                raw = "true";
            }
            else if (raw is null)
            {
                if (++index >= tokens.Count || tokens[index].StartsWith("--", StringComparison.Ordinal))
                {
                    return ArgumentBuildResult.Failed(new ArgumentValidationIssue(
                        name, "missing_field_value", Expected(types),
                        "The requested tool field requires a value."));
                }
                raw = tokens[index];
            }

            if (!TryScalar(raw, types, out JsonElement value))
            {
                return ArgumentBuildResult.Failed(new ArgumentValidationIssue(
                    name, "invalid_field_type", Expected(types),
                    "The requested tool field could not be parsed as the schema type; use --json or --args-file for non-scalar values."));
            }
            arguments[name] = value;
        }
        return ArgumentBuildResult.Succeeded(arguments);
    }

    private static bool TryScalar(
        string raw,
        HashSet<string> types,
        out JsonElement value)
    {
        string[] scalarTypes = types.Where(type => type != "null").ToArray();
        if (scalarTypes.Length != 1)
        {
            value = default;
            return false;
        }

        switch (scalarTypes[0])
        {
            case "string":
                value = JsonSerializer.SerializeToElement(raw, CompactJson);
                return true;
            case "boolean" when bool.TryParse(raw, out bool boolean):
                value = JsonSerializer.SerializeToElement(boolean, CompactJson);
                return true;
            case "integer" when long.TryParse(
                raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer):
                value = JsonSerializer.SerializeToElement(integer, CompactJson);
                return true;
            case "number" when decimal.TryParse(
                raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number):
                value = JsonSerializer.SerializeToElement(number, CompactJson);
                return true;
            default:
                value = default;
                return false;
        }
    }

    private static Dictionary<string, JsonElement> SchemaProperties(JsonElement schema) =>
        schema.TryGetProperty("properties", out JsonElement properties) &&
        properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value,
                StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    private static HashSet<string> SchemaTypes(JsonElement schema)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        AddTypes(schema, types);
        return types;

        static void AddTypes(JsonElement candidate, HashSet<string> target)
        {
            if (candidate.TryGetProperty("type", out JsonElement type))
            {
                if (type.ValueKind == JsonValueKind.String && type.GetString() is { } single)
                    target.Add(single);
                else if (type.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in type.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { } value)
                            target.Add(value);
                }
            }
            foreach (string union in new[] { "anyOf", "oneOf" })
            {
                if (!candidate.TryGetProperty(union, out JsonElement alternatives) ||
                    alternatives.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (JsonElement alternative in alternatives.EnumerateArray())
                    AddTypes(alternative, target);
            }
        }
    }

    private static string Expected(HashSet<string> types)
    {
        string[] meaningful = types.Where(type => type != "null").Order().ToArray();
        return meaningful.Length == 0 ? "schema-compatible value" : string.Join(" or ", meaningful);
    }

    internal static bool IsKnownTool(string? name) =>
        !string.IsNullOrEmpty(name) &&
        FindTool(RegisteredTools, name) is not null;

    internal static JsonElement CreateToolsPayload(IEnumerable<McpServerTool> tools) =>
        JsonSerializer.SerializeToElement(new
        {
            tools = tools.Select(tool => new
            {
                name = tool.ProtocolTool.Name,
                description = tool.ProtocolTool.Description,
            }),
            meta = DiscoveryMeta(),
        }, CompactJson);

    internal static JsonElement CreateToolHelpPayload(McpServerTool tool) =>
        JsonSerializer.SerializeToElement(new
        {
            name = tool.ProtocolTool.Name,
            description = tool.ProtocolTool.Description,
            inputSchema = tool.ProtocolTool.InputSchema,
            meta = DiscoveryMeta(),
        }, CompactJson);

    private static McpServerTool? FindTool(IEnumerable<McpServerTool> tools, string name) =>
        tools.FirstOrDefault(tool => string.Equals(
            tool.ProtocolTool.Name, name, StringComparison.Ordinal));

    internal static ArgumentValidationIssue UnknownTool(string name) => new(
        "tool", "unknown_tool", "an exact name returned by 'tools'",
        "Phoenix MCP does not advertise the requested tool name.");

    internal static JsonElement ExtractPayload(CallToolResult result)
    {
        if (result.StructuredContent is { } structured)
            return RequireObjectPayload(structured);
        if (result.Content.Count != 1 || result.Content[0] is not TextContentBlock textBlock)
            throw new PhoenixCliResultException(
                "unexpected_tool_result_shape",
                "Phoenix tool result did not contain exactly one JSON text block.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(textBlock.Text);
            return RequireObjectPayload(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new PhoenixCliResultException(
                "tool_result_not_json",
                "Phoenix tool result text was not valid JSON.", ex);
        }
    }

    private static JsonElement RequireObjectPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new PhoenixCliResultException(
                "unexpected_tool_result_shape",
                "Phoenix tool result payload was not a JSON object.");
        return payload.Clone();
    }

    private static int ExitCode(CallToolResult result, JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("error", out JsonElement error) &&
            error.ValueKind == JsonValueKind.String)
        {
            return string.Equals(error.GetString(), "bad_request", StringComparison.Ordinal)
                ? 2
                : 1;
        }
        return result.IsError is true ? 1 : 0;
    }

    internal static JsonElement BadRequest(
        string toolName,
        ArgumentValidationIssue issue)
    {
        bool boundField = string.Equals(
            issue.Reason, "unknown_field", StringComparison.Ordinal);
        string reflected = boundField ? issue.Field : toolName;
        string json = Json.WithStringBudget(
            reflected,
            Json.HardBudgetBytes,
            (bounded, truncated) => new
            {
                error = "bad_request",
                tool = boundField ? toolName : bounded,
                field = boundField ? bounded : issue.Field,
                reason = issue.Reason,
                expected = issue.Expected,
                detail = issue.Detail,
                retryable = true,
                truncated = truncated ? true : (bool?)null,
                truncatedField = truncated ? (boundField ? "field" : "tool") : null,
                meta = DiscoveryMeta(),
            });
        return ParseObject(json);
    }

    internal static JsonElement CreateInvalidToolResultPayload(
        PhoenixCliCommand command,
        PhoenixCliResultException failure)
    {
        string toolName = command.ToolName ?? command.Action.ToString().ToLowerInvariant();
        string json = Json.WithStringBudget(
            toolName,
            Json.HardBudgetBytes,
            (bounded, truncated) => new
            {
                error = "phoenix_tool_result_invalid",
                tool = bounded,
                reason = failure.Reason,
                detail = failure.Message,
                retryable = false,
                truncated = truncated ? true : (bool?)null,
                truncatedField = truncated ? "tool" : null,
                meta = DiscoveryMeta(),
            });
        return ParseObject(json);
    }

    internal static JsonElement CreateInternalErrorPayload(
        PhoenixCliCommand command,
        Exception failure)
    {
        string toolName = command.ToolName ?? command.Action.ToString().ToLowerInvariant();
        string detail = failure.Message;
        object BuildEnvelope(
            string boundedTool,
            bool toolTruncated,
            string boundedDetail,
            bool detailTruncated) => new
        {
            error = "phoenix_cli_internal_error",
            tool = boundedTool,
            reason = failure.GetType().Name,
            detail = boundedDetail,
            retryable = false,
            truncated = toolTruncated || detailTruncated ? true : (bool?)null,
            truncatedField = toolTruncated
                ? "tool"
                : detailTruncated ? "detail" : null,
            meta = DiscoveryMeta(),
        };

        string toolBudgetJson = Json.WithStringBudget(
            toolName,
            Json.HardBudgetBytes,
            (boundedTool, toolTruncated) => BuildEnvelope(
                boundedTool,
                toolTruncated,
                "",
                detail.Length > 0));
        using JsonDocument toolBudget = JsonDocument.Parse(toolBudgetJson);
        string boundedTool = toolBudget.RootElement.GetProperty("tool").GetString()!;
        bool toolTruncated = toolBudget.RootElement.TryGetProperty(
            "truncatedField", out JsonElement truncatedField) &&
            string.Equals(truncatedField.GetString(), "tool", StringComparison.Ordinal);

        string json = Json.WithStringBudget(
            detail,
            Json.HardBudgetBytes,
            (boundedDetail, detailTruncated) => BuildEnvelope(
                boundedTool,
                toolTruncated,
                boundedDetail,
                detailTruncated));
        return ParseObject(json);
    }

    internal static PhoenixCliCallFailure? ClassifyCallFailure(Exception failure) =>
        failure is McpProtocolException protocol
            ? new PhoenixCliCallFailure(
                protocol.ErrorCode.ToString(),
                protocol.Message,
                "Re-run once: a retiring daemon can reject an in-flight request; " +
                "a repeated rejection with the same reason is a daemon defect.",
                Retryable: true)
            : null;

    internal static JsonElement CreateCallFailurePayload(
        PhoenixCliCommand command,
        PhoenixCliCallFailure failure)
    {
        string toolName = command.ToolName ?? command.Action.ToString().ToLowerInvariant();
        string json = Json.WithStringBudget(
            failure.Detail,
            Json.HardBudgetBytes,
            (bounded, truncated) => new
            {
                error = "daemon_request_rejected",
                tool = toolName,
                reason = failure.Reason,
                detail = bounded,
                recovery = failure.Recovery,
                retryable = failure.Retryable,
                truncated = truncated ? true : (bool?)null,
                truncatedField = truncated ? "detail" : null,
                meta = DiscoveryMeta(),
            });
        return ParseObject(json);
    }

    internal static DaemonUnavailableFailure? ClassifyTransportFailure(Exception failure) =>
        failure is IOException or McpException or JsonException or
            TimeoutException or OperationCanceledException
            ? new DaemonUnavailableFailure(
                "daemon_cli_transport_failed",
                $"Phoenix CLI could not complete its MCP exchange ({failure.GetType().Name}).",
                "Retry the CLI call and inspect Phoenix daemon discovery state if the failure repeats.",
                Retryable: true)
            : null;

    private static JsonElement ParseObject(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static object DiscoveryMeta() => new
    {
        build = BuildInfo.Stamp,
        indexSchema = BuildInfo.IndexSchema,
    };

    internal static async Task<int> WriteJsonAsync(
        JsonElement payload,
        bool pretty,
        int exitCode = 0,
        Stream? outputStream = null)
    {
        string json = SerializeOutput(payload, pretty);
        byte[] output = Encoding.UTF8.GetBytes(json + Environment.NewLine);
        try
        {
            Stream destination = outputStream ?? Console.OpenStandardOutput();
            await destination.WriteAsync(output).ConfigureAwait(false);
            await destination.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            try
            {
                Console.Error.WriteLine(
                    $"Phoenix CLI could not write its result ({ex.GetType().Name}).");
            }
            catch
            {
                // The caller closed its output channels; the exit code is the only signal left.
            }
        }
        return exitCode;
    }

    internal static string SerializeOutput(JsonElement payload, bool pretty) => pretty
        ? JsonSerializer.Serialize(payload, PrettyJson)
        : payload.GetRawText();

    internal sealed record ArgumentBuildResult(
        IReadOnlyDictionary<string, JsonElement>? Arguments,
        ArgumentValidationIssue? Issue)
    {
        internal static ArgumentBuildResult Succeeded(
            IReadOnlyDictionary<string, JsonElement> arguments) => new(arguments, null);

        internal static ArgumentBuildResult Failed(
            ArgumentValidationIssue issue) => new(null, issue);
    }

    internal sealed record PhoenixCliInvocationResult(JsonElement Payload, int ExitCode);
}

internal sealed class PhoenixCliResultException : Exception
{
    internal PhoenixCliResultException(
        string reason,
        string message,
        Exception? inner = null)
        : base(message, inner)
    {
        Reason = reason;
    }

    internal string Reason { get; }
}

internal sealed record PhoenixCliCallFailure(
    string Reason,
    string Detail,
    string Recovery,
    bool Retryable);
