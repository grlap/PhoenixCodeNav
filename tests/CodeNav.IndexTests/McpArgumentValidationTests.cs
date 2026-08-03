using System.Reflection;
using System.Text.Json;
using CodeNav.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeNav.Tests;

[Collection("Operations Portal MCP process isolation")]
public sealed class McpArgumentValidationTests
{
    [Fact]
    public void EveryAttributedNavigationToolIsRegisteredThroughTheValidationBoundary()
    {
        MethodInfo[] attributed = typeof(NavigationTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Static |
                        BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToArray();
        IReadOnlyList<McpServerTool> registered =
            ValidatedMcpToolRegistration.CreateNavigationTools();

        Assert.Equal(attributed.Length, registered.Count);
        Assert.Equal(
            attributed.Select(method => method.GetCustomAttribute<McpServerToolAttribute>()!.Name)
                .Order(StringComparer.Ordinal),
            registered.Select(tool => tool.ProtocolTool.Name).Order(StringComparer.Ordinal));
        Assert.All(registered, tool => Assert.IsType<ValidatingMcpServerTool>(tool));

        McpServerTool findFile = Assert.Single(
            registered,
            tool => tool.ProtocolTool.Name == "find_file");
        Assert.Contains(
            findFile.ProtocolTool.InputSchema.GetProperty("required").EnumerateArray(),
            field => field.GetString() == "nameOrGlob");

        var portal = Assert.IsType<ValidatingMcpServerTool>(Assert.Single(
            registered,
            tool => tool.ProtocolTool.Name == "open_operations_portal"));
        Assert.Null(portal.Validate(new Dictionary<string, JsonElement>
        {
            ["cancellationToken"] = JsonSerializer.SerializeToElement<object?>(null),
        }));
    }

    [Fact]
    public async Task RealStdioServerNamesEveryMissingRequiredFieldAndRepresentativeInvalidType()
    {
        string root = Directory.CreateTempSubdirectory(
            "Phoenix MCP argument validation ").FullName;
        try
        {
            string executable = FindMcpExecutable();
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "Phoenix structured argument validation",
                Command = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                Arguments = ["--workspace-root", root],
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            await using McpClient client = await McpClient.CreateAsync(
                transport,
                cancellationToken: timeout.Token);

            IList<McpClientTool> tools = await client.ListToolsAsync(
                cancellationToken: timeout.Token);
            Assert.Equal(27, tools.Count);

            int requiredFieldsChecked = 0;
            int invalidFieldsChecked = 0;
            foreach (McpClientTool tool in tools)
            {
                JsonElement schema = tool.JsonSchema;
                if (!schema.TryGetProperty("required", out JsonElement required) ||
                    required.ValueKind != JsonValueKind.Array)
                    continue;

                JsonElement properties = schema.GetProperty("properties");
                string[] requiredFields = required.EnumerateArray()
                    .Select(field => field.GetString())
                    .Where(field => !string.IsNullOrWhiteSpace(field))
                    .Select(field => field!)
                    .ToArray();
                foreach (string omittedField in requiredFields)
                {
                    var arguments = requiredFields
                        .Where(field => field != omittedField)
                        .ToDictionary(
                            field => field,
                            field => ValidValue(properties.GetProperty(field)));

                    CallToolResult result = await client.CallToolAsync(
                        tool.Name,
                        arguments,
                        cancellationToken: timeout.Token);
                    JsonElement error = ParseError(result);

                    Assert.Equal("bad_request", error.GetProperty("error").GetString());
                    Assert.Equal(tool.Name, error.GetProperty("tool").GetString());
                    Assert.Equal(omittedField, error.GetProperty("field").GetString());
                    Assert.Equal(
                        "missing_required_field",
                        error.GetProperty("reason").GetString());
                    Assert.True(error.GetProperty("retryable").GetBoolean());
                    requiredFieldsChecked++;

                    var invalidArguments = requiredFields.ToDictionary(
                        field => field,
                        field => ValidValue(properties.GetProperty(field)));
                    invalidArguments[omittedField] =
                        InvalidValue(properties.GetProperty(omittedField));
                    CallToolResult invalidResult = await client.CallToolAsync(
                        tool.Name,
                        invalidArguments,
                        cancellationToken: timeout.Token);
                    JsonElement invalidError = ParseError(invalidResult);
                    Assert.Equal("bad_request",
                        invalidError.GetProperty("error").GetString());
                    Assert.Equal(tool.Name,
                        invalidError.GetProperty("tool").GetString());
                    Assert.Equal(omittedField,
                        invalidError.GetProperty("field").GetString());
                    Assert.Equal("invalid_field_type",
                        invalidError.GetProperty("reason").GetString());
                    invalidFieldsChecked++;
                }
            }
            Assert.True(requiredFieldsChecked > 0);
            Assert.Equal(requiredFieldsChecked, invalidFieldsChecked);

            CallToolResult invalidType = await client.CallToolAsync(
                "find_file",
                new Dictionary<string, object?> { ["nameOrGlob"] = 42 },
                cancellationToken: timeout.Token);
            JsonElement invalid = ParseError(invalidType);
            Assert.Equal("bad_request", invalid.GetProperty("error").GetString());
            Assert.Equal("find_file", invalid.GetProperty("tool").GetString());
            Assert.Equal("nameOrGlob", invalid.GetProperty("field").GetString());
            Assert.Equal("invalid_field_type", invalid.GetProperty("reason").GetString());
            Assert.Equal("string", invalid.GetProperty("expected").GetString());

            CallToolResult valid = await client.CallToolAsync(
                "server_capabilities",
                new Dictionary<string, object?>(),
                cancellationToken: timeout.Token);
            Assert.False(valid.IsError is true);
            JsonElement capabilities = ParseContent(valid);
            Assert.Equal("phoenixCodeNav", capabilities.GetProperty("server").GetString());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    private static object? ValidValue(JsonElement schema)
    {
        return SchemaType(schema) switch
        {
            "string" => "x",
            "integer" => 1,
            "number" => 1.0,
            "boolean" => true,
            "array" => Array.Empty<object>(),
            "object" => new Dictionary<string, object?>(),
            _ => null,
        };
    }

    private static object InvalidValue(JsonElement schema) =>
        SchemaType(schema) switch
        {
            "string" => 42,
            "integer" or "number" or "boolean" or "array" or "object" => "invalid",
            _ => "invalid",
        };

    private static string? SchemaType(JsonElement schema)
    {
        JsonElement type = schema.GetProperty("type");
        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray()
                .Select(value => value.GetString())
                .FirstOrDefault(value => value != "null")
            : type.GetString();
    }

    private static JsonElement ParseError(CallToolResult result)
    {
        Assert.True(result.IsError is true);
        return ParseContent(result);
    }

    private static JsonElement ParseContent(CallToolResult result)
    {
        TextContentBlock text = Assert.IsType<TextContentBlock>(
            Assert.Single(result.Content));
        return JsonDocument.Parse(text.Text).RootElement.Clone();
    }

    private static string FindMcpExecutable()
    {
        string repository = FindRepositoryRoot();
        string configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent?.Name ?? "Debug";
        string executable = Path.Combine(
            repository,
            "src",
            "CodeNav.Mcp",
            "bin",
            configuration,
            "net10.0",
            OperatingSystem.IsWindows()
                ? "PhoenixCodeNav.Mcp.exe"
                : "PhoenixCodeNav.Mcp");
        Assert.True(File.Exists(executable), $"MCP apphost missing: {executable}");
        return executable;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "PhoenixCodeNav.sln")))
            directory = directory.Parent;
        return directory?.FullName ??
               throw new InvalidOperationException("Could not locate PhoenixCodeNav.sln.");
    }
}
