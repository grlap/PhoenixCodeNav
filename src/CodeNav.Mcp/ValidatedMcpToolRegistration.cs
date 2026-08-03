using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp;

/// <summary>
/// Registers the attributed navigation tools through one validation boundary. The MCP SDK keeps
/// non-nullable parameters in each tool's required JSON-schema list, but a missing or mistyped
/// value otherwise fails during SDK binding and is reduced to an opaque host error. This wrapper
/// preserves those schemas and returns a field-addressable tool error before binding begins.
/// </summary>
internal static class ValidatedMcpToolRegistration
{
    internal static IReadOnlyList<McpServerTool> CreateNavigationTools()
    {
        JsonSerializerOptions serializerOptions = McpJsonUtilities.DefaultOptions;
        var createOptions = new McpServerToolCreateOptions
        {
            SerializerOptions = serializerOptions,
        };

        return typeof(NavigationTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Static |
                        BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .OrderBy(method => method.MetadataToken)
            .Select(method => Create(method, createOptions, serializerOptions))
            .ToArray();
    }

    private static McpServerTool Create(
        MethodInfo method,
        McpServerToolCreateOptions createOptions,
        JsonSerializerOptions serializerOptions)
    {
        McpServerTool inner = method.IsStatic
            ? McpServerTool.Create(method, target: null, createOptions)
            : McpServerTool.Create(
                method,
                request => ActivatorUtilities.CreateInstance<NavigationTools>(request.Services!),
                createOptions);
        return new ValidatingMcpServerTool(inner, method, serializerOptions);
    }
}

internal sealed class ValidatingMcpServerTool : DelegatingMcpServerTool
{
    private readonly ParameterBinding[] _parameters;
    private readonly string[] _requiredFields;
    private readonly JsonSerializerOptions _serializerOptions;

    internal ValidatingMcpServerTool(
        McpServerTool innerTool,
        MethodInfo method,
        JsonSerializerOptions serializerOptions)
        : base(innerTool)
    {
        _serializerOptions = serializerOptions;
        HashSet<string> schemaFields = ReadSchemaFields(innerTool.ProtocolTool.InputSchema);
        _parameters = method.GetParameters()
            .Where(parameter => parameter.Name is not null)
            .Select(parameter => new ParameterBinding(
                WireName(parameter, serializerOptions),
                parameter.ParameterType,
                AllowsNull(parameter),
                ExpectedType(parameter.ParameterType)))
            .Where(parameter => schemaFields.Contains(parameter.Name))
            .ToArray();
        _requiredFields = ReadRequiredFields(innerTool.ProtocolTool.InputSchema);
    }

    public override ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentValidationIssue? issue = Validate(request.Params?.Arguments);
        return issue is null
            ? base.InvokeAsync(request, cancellationToken)
            : ValueTask.FromResult(BadRequest(issue));
    }

    internal ArgumentValidationIssue? Validate(
        IDictionary<string, JsonElement>? arguments)
    {
        foreach (string field in _requiredFields)
        {
            if (arguments is null || !arguments.ContainsKey(field))
            {
                ParameterBinding? parameter = _parameters.FirstOrDefault(
                    candidate => candidate.Name == field);
                return new ArgumentValidationIssue(
                    field,
                    "missing_required_field",
                    parameter?.Expected,
                    $"Required field '{field}' is missing.");
            }
        }

        if (arguments is null) return null;

        foreach (ParameterBinding parameter in _parameters)
        {
            if (!arguments.TryGetValue(parameter.Name, out JsonElement argument))
                continue;

            if (argument.ValueKind == JsonValueKind.Null && !parameter.AllowsNull)
            {
                return InvalidType(parameter);
            }

            try
            {
                _ = argument.Deserialize(parameter.Type, _serializerOptions);
            }
            catch (JsonException)
            {
                return InvalidType(parameter);
            }
            catch (NotSupportedException)
            {
                return InvalidType(parameter);
            }
        }

        return null;
    }

    private CallToolResult BadRequest(ArgumentValidationIssue issue)
    {
        var payload = new
        {
            error = "bad_request",
            tool = ProtocolTool.Name,
            field = issue.Field,
            reason = issue.Reason,
            expected = issue.Expected,
            detail = issue.Detail,
            retryable = true,
        };
        JsonElement structured = JsonSerializer.SerializeToElement(
            payload,
            _serializerOptions);
        return new CallToolResult
        {
            IsError = true,
            StructuredContent = structured,
            Content =
            [
                new TextContentBlock
                {
                    Text = structured.GetRawText(),
                },
            ],
        };
    }

    private static ArgumentValidationIssue InvalidType(ParameterBinding parameter) =>
        new(
            parameter.Name,
            "invalid_field_type",
            parameter.Expected,
            $"Field '{parameter.Name}' must be {Article(parameter.Expected)} {parameter.Expected}.");

    private static string WireName(
        ParameterInfo parameter,
        JsonSerializerOptions serializerOptions) =>
        serializerOptions.PropertyNamingPolicy?.ConvertName(parameter.Name!) ?? parameter.Name!;

    private static bool AllowsNull(ParameterInfo parameter)
    {
        if (Nullable.GetUnderlyingType(parameter.ParameterType) is not null)
            return true;
        if (parameter.ParameterType.IsValueType)
            return false;
        return new NullabilityInfoContext().Create(parameter).ReadState !=
               NullabilityState.NotNull;
    }

    private static string ExpectedType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type == typeof(char)) return "string";
        if (type == typeof(bool)) return "boolean";
        if (type == typeof(byte) || type == typeof(sbyte) ||
            type == typeof(short) || type == typeof(ushort) ||
            type == typeof(int) || type == typeof(uint) ||
            type == typeof(long) || type == typeof(ulong)) return "integer";
        if (type == typeof(float) || type == typeof(double) ||
            type == typeof(decimal)) return "number";
        if (type.IsArray ||
            (type != typeof(string) && typeof(System.Collections.IEnumerable)
                .IsAssignableFrom(type))) return "array";
        return "object";
    }

    private static string Article(string expected) =>
        expected is "integer" or "array" or "object" ? "an" : "a";

    private static string[] ReadRequiredFields(JsonElement inputSchema) =>
        inputSchema.TryGetProperty("required", out JsonElement required) &&
        required.ValueKind == JsonValueKind.Array
            ? required.EnumerateArray()
                .Select(field => field.GetString())
                .Where(field => !string.IsNullOrWhiteSpace(field))
                .Select(field => field!)
                .ToArray()
            : [];

    private static HashSet<string> ReadSchemaFields(JsonElement inputSchema) =>
        inputSchema.TryGetProperty("properties", out JsonElement properties) &&
        properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private sealed record ParameterBinding(
        string Name,
        Type Type,
        bool AllowsNull,
        string Expected);
}

internal sealed record ArgumentValidationIssue(
    string Field,
    string Reason,
    string? Expected,
    string Detail);
