using System.Reflection;
using System.Text.Json;
using CodeNav.Mcp.Daemon;

namespace CodeNav.Tests;

public sealed partial class DaemonSessionRecoveryTests
{
    [Theory]
    [InlineData(typeof(DaemonUnavailableFailure))]
    [InlineData(typeof(DaemonHandshakeResponse))]
    public void LocalCauseConstructionHasNoBareStringEscape(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic;
        var constructors = type.GetConstructors(flags | BindingFlags.Instance).Where(item => !item.IsPrivate);
        Assert.All(constructors, item => Assert.Contains(item.GetParameters(), parameter =>
            parameter.ParameterType == typeof(DaemonFailureCause)));
        var factories = type.GetMethods(flags | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(item => !item.IsPrivate && item.ReturnType == type && item.Name != "<Clone>$");
        Assert.NotEmpty(constructors.Cast<MethodBase>().Concat(factories));
        Assert.All(factories, item =>
        {
            if (type == typeof(DaemonHandshakeResponse) && item.Name is "SessionAccepted" or "RetirementAccepted")
            {
                Assert.DoesNotContain(item.GetParameters(), parameter =>
                    parameter.ParameterType == typeof(DaemonFailureCause)
                    || string.Equals(parameter.Name, "cause", StringComparison.OrdinalIgnoreCase));
                return; // Fixed successful statuses, not failure emitters.
            }
            Assert.Contains(item.GetParameters(), parameter => parameter.ParameterType == typeof(DaemonFailureCause)
                || parameter.ParameterType == typeof(DaemonHandshakeResponse));
        });
        Assert.Null(type.GetProperty("Cause")!.SetMethod);
    }

    [Fact]
    public async Task HandshakeResponseKeepsFrozenJsonShapeAndUnknownWireCause()
    {
        const string json = """{"accepted":false,"cause":"daemon_future_unlisted_cause","detail":"detail","toolVersion":"1.2.3","schemaVersion":"42","workspaceIdentity":"workspace","databaseKey":"database","daemonPid":123,"nonce":"nonce","retiring":false}""";
        var response = JsonSerializer.Deserialize<DaemonHandshakeResponse>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        using var frame = new MemoryStream();
        await DaemonProtocol.WriteResponseAsync(frame, response, CancellationToken.None);
        Assert.Equal(json, System.Text.Encoding.UTF8.GetString(frame.ToArray().AsSpan(DaemonProtocol.HeaderBytes)));
        frame.Position = 0;
        Assert.Equal(response, await DaemonProtocol.ReadResponseAsync(frame, CancellationToken.None));
        var withoutRetiring = JsonSerializer.Deserialize<DaemonHandshakeResponse>(
            json.Replace(",\"retiring\":false", "", StringComparison.Ordinal), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(response, withoutRetiring);
        Assert.False(DaemonFailureCause.CanRecover(response.Cause));
        var failure = DaemonUnavailableFailure.FromRefusal(response, "repair", Retryable: true);
        Assert.Equal("daemon_future_unlisted_cause", failure.Cause);
        Assert.Equal("detail", failure.Detail);
        Assert.False(failure.CanRecoverInSession);
        Assert.True(failure.Retryable);
    }

    [Fact]
    public void EveryLocalFailureCauseRequiresAnExplicitRecoveryExpectation()
    {
        var expected = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["daemon_handshake_timeout"] = true,
            ["daemon_startup_timeout"] = true,
            ["daemon_launch_failed"] = true,
            ["daemon_died_before_report"] = true,
            ["daemon_startup_report_timeout"] = true,
            ["daemon_startup_report_invalid"] = true,
            ["daemon_writer_lease_unverifiable"] = true,
            ["daemon_takeover_timeout"] = true,
            ["daemon_older_than_client"] = true,
            ["daemon_writer_unavailable"] = true,
            ["daemon_writer_authority_unavailable"] = true,
            ["daemon_index_validation_failed"] = true,
            ["daemon_index_startup_failed"] = true,
            ["daemon_startup_exception"] = true,
            ["daemon_connection_unavailable"] = true,
            ["daemon_proxy_failed"] = false,
            ["daemon_endpoint_authority_failed"] = false,
            ["daemon_response_authority_failed"] = false,
            ["daemon_response_version_mismatch"] = false,
            ["daemon_runtime_directory_unavailable"] = false,
            ["daemon_workspace_unavailable"] = false,
            ["daemon_index_destination_invalid"] = false,
            ["daemon_workspace_identity_unavailable"] = false,
            ["daemon_cli_transport_failed"] = false,
            ["daemon_request_outcome_unknown"] = false,
            ["daemon_index_rebuild_required"] = false,
            ["daemon_index_destination_foreign"] = false,
            ["daemon_index_destination_unsafe"] = false,
            ["standalone_writer_unavailable"] = false,
            ["daemon_preamble_incompatible"] = false,
            ["daemon_preamble_invalid"] = false,
            ["daemon_nonce_invalid"] = false,
            ["daemon_client_invalid"] = false,
            ["daemon_user_mismatch"] = false,
            ["daemon_workspace_mismatch"] = false,
            ["daemon_index_destination_mismatch"] = false,
            ["daemon_retire_not_newer"] = false,
            ["daemon_newer_than_client"] = false,
        };
        // Inspect the registry populated by every declaration, not a visibility-filtered
        // field list that could miss a new public member with no test expectation.
        var registry = Assert.IsAssignableFrom<IReadOnlyDictionary<string, DaemonFailureCause>>(
            typeof(DaemonFailureCause).GetField("ById", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        var causes = registry.Values.ToArray();
        Assert.Equal(expected.Keys.Order(), causes.Select(cause => cause.Id).Order());
        foreach (var cause in causes)
        {
            foreach (bool advice in new[] { false, true })
            {
                var failure = new DaemonUnavailableFailure(cause, "detail", "recovery", advice);
                Assert.Equal(expected[cause.Id], failure.CanRecoverInSession);
                Assert.Equal(advice, failure.Retryable);
                var refused = DaemonHandshakeResponse.Refused(cause, "detail", "1", "42", "workspace", "database", 123, "nonce");
                Assert.False(refused.Accepted);
                Assert.False(refused.Retiring);
                Assert.Equal(failure, DaemonUnavailableFailure.FromRefusal(refused, "recovery", advice));
                Assert.Equal(expected[cause.Id], DaemonWireTestData.Failure(
                    cause.Id, failure.Detail, failure.Recovery, advice).CanRecoverInSession);
            }
        }
    }

    [Fact]
    public void SuccessfulHandshakeFactoriesHaveFixedStatuses()
    {
        var session = DaemonHandshakeResponse.SessionAccepted("detail", "1", "42", "workspace", "database", 123, "nonce");
        var retiring = DaemonHandshakeResponse.RetirementAccepted("detail", "1", "42", "workspace", "database", 123, "nonce");
        Assert.True(session.Accepted);
        Assert.Equal("ok", session.Cause);
        Assert.False(session.Retiring);
        Assert.True(retiring.Accepted);
        Assert.Equal("daemon_retiring", retiring.Cause);
        Assert.True(retiring.Retiring);
    }

    [Fact]
    public async Task UnknownWireFailureRemainsTerminalThroughStartupFrameAndCopies()
    {
        const string unknown = "daemon_future_unlisted_cause";
        var failure = DaemonWireTestData.Failure(unknown, "detail", "repair", Retryable: true);
        Assert.False(failure.CanRecoverInSession);
        using var frame = new MemoryStream();
        await DaemonStartupChannel.WriteAsync(frame, DaemonStartupReport.Refused(0, failure));
        using var payload = JsonDocument.Parse(frame.ToArray().AsMemory(sizeof(int)));
        Assert.Equal(new[] { "cause", "detail", "recovery", "retryable" },
            payload.RootElement.GetProperty("failure").EnumerateObject().Select(property => property.Name));
        frame.Position = 0;
        var restored = Assert.IsType<DaemonUnavailableFailure>((await DaemonStartupChannel.ReadAsync(frame)).Failure);
        Assert.Equal(failure, restored);
        Assert.False(restored.CanRecoverInSession);
        Assert.True(restored.Retryable);
        Assert.False((restored with { Retryable = false }).CanRecoverInSession);

        // Product construction is typed; synthetic/unknown ids belong only to the wire boundary.
        Assert.All(typeof(DaemonUnavailableFailure).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(constructor => !constructor.IsPrivate), constructor =>
            Assert.Equal(typeof(DaemonFailureCause), constructor.GetParameters()[0].ParameterType));
        Assert.All(typeof(DaemonFailureCause).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            constructor => Assert.True(constructor.IsPrivate));
        Assert.Null(typeof(DaemonUnavailableFailure).GetProperty(nameof(DaemonUnavailableFailure.Cause))!.SetMethod);
    }
}

// Synthetic peer payloads belong in tests, not in a production local-cause escape hatch.
internal static class DaemonWireTestData
{
    internal static DaemonUnavailableFailure Failure(string Cause, string Detail, string Recovery, bool Retryable) =>
        JsonSerializer.Deserialize<DaemonUnavailableFailure>(
            JsonSerializer.Serialize(new { Cause, Detail, Recovery, Retryable }, CodeNav.Mcp.Json.Options),
            CodeNav.Mcp.Json.Options)!;
}
