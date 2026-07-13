namespace CopilotAgentObservability.ConfigCli.Setup.Contracts;

public static class SetupCodes
{
    public const string ContractVersion = "setup.v1";

    public const string PlanReady = "plan_ready";
    public const string NoChanges = "no_changes";
    public const string ApplySucceeded = "apply_succeeded";
    public const string RollbackSucceeded = "rollback_succeeded";
    public const string StatusReady = "status_ready";
    public const string InterruptedApplyRecovered = "interrupted_apply_recovered";
    public const string InterruptedRollbackRecovered = "interrupted_rollback_recovered";
    public const string InvalidArguments = "invalid_arguments";
    public const string UnsupportedAdapter = "unsupported_adapter";
    public const string UnsupportedTarget = "unsupported_target";
    public const string TargetNotInstalled = "target_not_installed";
    public const string UnsupportedVersion = "unsupported_version";
    public const string ManagedPolicyConflict = "managed_policy_conflict";
    public const string EnvironmentOverrideConflict = "environment_override_conflict";
    public const string MalformedSettings = "malformed_settings";
    public const string PermissionDenied = "permission_denied";
    public const string UnsafePath = "unsafe_path";
    public const string StalePlan = "stale_plan";
    public const string RollbackStale = "rollback_stale";
    public const string RollbackNotAvailable = "rollback_not_available";
    public const string PortOwnedByForeignProcess = "port_owned_by_foreign_process";
    public const string PartialApply = "partial_apply";
    public const string PartialRollback = "partial_rollback";
    public const string SetupBusy = "setup_busy";
    public const string RecoveryRequired = "recovery_required";
    public const string InterruptedRecoveryFailed = "interrupted_recovery_failed";
    public const string LedgerCorrupt = "ledger_corrupt";
    public const string LedgerVersionUnsupported = "ledger_version_unsupported";
    public const string InternalError = "internal_error";

    public const string ContentCaptureSensitive = "content_capture_sensitive";
    public const string ManagedPolicyUnverified = "managed_policy_unverified";
    public const string MonitorNotRunning = "monitor_not_running";
    public const string SharedUserEnvironmentAffectsOtherProcesses = "shared_user_environment_affects_other_processes";
    public const string VscodeNonDefaultProfilesNotModified = "vscode_non_default_profiles_not_modified";
    public const string CliTraceProtocolOverrideNotModified = "cli_trace_protocol_override_not_modified";

    public const string InstallVsCode = "install_vscode";
    public const string InstallGitHubCopilotChatExtension = "install_github_copilot_chat_extension";
    public const string UpgradeVsCode = "upgrade_vscode";
    public const string InstallCopilotCli = "install_copilot_cli";
    public const string UpgradeCopilotCli = "upgrade_copilot_cli";
    public const string RunVsCodePolicyDiagnostics = "run_vscode_policy_diagnostics";
    public const string RestartVsCode = "restart_vscode";
    public const string RestartTerminalSession = "restart_terminal_session";
    public const string StartLocalMonitor = "start_local_monitor";
    public const string ReviewContentCaptureWarning = "review_content_capture_warning";
    public const string ReviewCliTraceProtocolOverride = "review_cli_trace_protocol_override";
    public const string RunFirstTraceDoctor = "run_first_trace_doctor";
    public const string RerunRequestedSetupCommand = "rerun_requested_setup_command";

    private static readonly IReadOnlyList<string> ResultCodeValues = Array.AsReadOnly(
    [
        PlanReady,
        NoChanges,
        ApplySucceeded,
        RollbackSucceeded,
        StatusReady,
        InterruptedApplyRecovered,
        InterruptedRollbackRecovered,
        InvalidArguments,
        UnsupportedAdapter,
        UnsupportedTarget,
        TargetNotInstalled,
        UnsupportedVersion,
        ManagedPolicyConflict,
        EnvironmentOverrideConflict,
        MalformedSettings,
        PermissionDenied,
        UnsafePath,
        StalePlan,
        RollbackStale,
        RollbackNotAvailable,
        PortOwnedByForeignProcess,
        PartialApply,
        PartialRollback,
        SetupBusy,
        RecoveryRequired,
        InterruptedRecoveryFailed,
        LedgerCorrupt,
        LedgerVersionUnsupported,
        InternalError,
    ]);

    public static IReadOnlyList<string> ResultCodes => ResultCodeValues;
}
