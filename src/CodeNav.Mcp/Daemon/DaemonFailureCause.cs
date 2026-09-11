namespace CodeNav.Mcp.Daemon;

/// <summary>Local failure declarations require a recovery decision independent of retry advice.</summary>
internal sealed class DaemonFailureCause
{
    private static readonly Dictionary<string, DaemonFailureCause> ById = new(StringComparer.Ordinal);

    internal string Id { get; }
    private bool RecoveryAvailable { get; }

    private DaemonFailureCause(string id, bool recoveryAvailable)
    {
        Id = id;
        RecoveryAvailable = recoveryAvailable;
        ById.Add(id, this);
    }

    internal static readonly DaemonFailureCause HandshakeTimeout = new("daemon_handshake_timeout", true);
    internal static readonly DaemonFailureCause StartupTimeout = new("daemon_startup_timeout", true);
    internal static readonly DaemonFailureCause LaunchFailed = new("daemon_launch_failed", true);
    internal static readonly DaemonFailureCause DiedBeforeReport = new("daemon_died_before_report", true);
    internal static readonly DaemonFailureCause StartupReportTimeout = new("daemon_startup_report_timeout", true);
    internal static readonly DaemonFailureCause StartupReportInvalid = new("daemon_startup_report_invalid", true);
    internal static readonly DaemonFailureCause WriterLeaseUnverifiable = new("daemon_writer_lease_unverifiable", true);
    internal static readonly DaemonFailureCause TakeoverTimeout = new("daemon_takeover_timeout", true);
    internal static readonly DaemonFailureCause OlderThanClient = new("daemon_older_than_client", true);
    internal static readonly DaemonFailureCause WriterUnavailable = new("daemon_writer_unavailable", true);
    internal static readonly DaemonFailureCause WriterAuthorityUnavailable = new("daemon_writer_authority_unavailable", true);
    internal static readonly DaemonFailureCause IndexValidationFailed = new("daemon_index_validation_failed", true);
    internal static readonly DaemonFailureCause IndexStartupFailed = new("daemon_index_startup_failed", true);
    internal static readonly DaemonFailureCause StartupException = new("daemon_startup_exception", true);
    internal static readonly DaemonFailureCause ConnectionUnavailable = new("daemon_connection_unavailable", true);
    internal static readonly DaemonFailureCause ProxyFailed = new("daemon_proxy_failed", false);
    internal static readonly DaemonFailureCause EndpointAuthorityFailed = new("daemon_endpoint_authority_failed", false);
    internal static readonly DaemonFailureCause ResponseAuthorityFailed = new("daemon_response_authority_failed", false);
    internal static readonly DaemonFailureCause ResponseVersionMismatch = new("daemon_response_version_mismatch", false);
    internal static readonly DaemonFailureCause RuntimeDirectoryUnavailable = new("daemon_runtime_directory_unavailable", false);
    internal static readonly DaemonFailureCause WorkspaceUnavailable = new("daemon_workspace_unavailable", false);
    internal static readonly DaemonFailureCause IndexDestinationInvalid = new("daemon_index_destination_invalid", false);
    internal static readonly DaemonFailureCause WorkspaceIdentityUnavailable = new("daemon_workspace_identity_unavailable", false);
    internal static readonly DaemonFailureCause CliTransportFailed = new("daemon_cli_transport_failed", false);
    internal static readonly DaemonFailureCause RequestOutcomeUnknown = new("daemon_request_outcome_unknown", false);
    internal static readonly DaemonFailureCause IndexRebuildRequired = new("daemon_index_rebuild_required", false);
    internal static readonly DaemonFailureCause IndexDestinationForeign = new("daemon_index_destination_foreign", false);
    internal static readonly DaemonFailureCause IndexDestinationUnsafe = new("daemon_index_destination_unsafe", false);
    internal static readonly DaemonFailureCause StandaloneWriterUnavailable = new("standalone_writer_unavailable", false);
    internal static readonly DaemonFailureCause PreambleIncompatible = new("daemon_preamble_incompatible", false);
    internal static readonly DaemonFailureCause PreambleInvalid = new("daemon_preamble_invalid", false);
    internal static readonly DaemonFailureCause NonceInvalid = new("daemon_nonce_invalid", false);
    internal static readonly DaemonFailureCause ClientInvalid = new("daemon_client_invalid", false);
    internal static readonly DaemonFailureCause UserMismatch = new("daemon_user_mismatch", false);
    internal static readonly DaemonFailureCause WorkspaceMismatch = new("daemon_workspace_mismatch", false);
    internal static readonly DaemonFailureCause IndexDestinationMismatch = new("daemon_index_destination_mismatch", false);
    internal static readonly DaemonFailureCause RetireNotNewer = new("daemon_retire_not_newer", false);
    internal static readonly DaemonFailureCause NewerThanClient = new("daemon_newer_than_client", false);

    // Wire peers may know causes this build does not. Unknown wire input remains terminal.
    internal static bool CanRecover(string cause) =>
        cause is not null && ById.TryGetValue(cause, out var known) && known.RecoveryAvailable;
}
