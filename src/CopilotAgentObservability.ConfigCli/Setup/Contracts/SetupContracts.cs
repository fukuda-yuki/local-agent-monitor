using System.Text.Json;

namespace CopilotAgentObservability.ConfigCli.Setup.Contracts;

public sealed record SetupCommandResult(
    SetupCommand Command,
    bool Success,
    string Code,
    string? ChangeSetId,
    string? RecoveredChangeSetId,
    SetupRecoveryOperation? RecoveryOperation,
    string? Adapter,
    IReadOnlyList<SetupTargetResult> Targets,
    IReadOnlyList<SetupChangeSetStatusResult> ChangeSets,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> NextActions,
    bool Truncated);

public sealed record SetupTargetResult(
    string RecordId,
    SetupTargetKind TargetKind,
    string TargetLabel,
    bool Detected,
    string? DetectedVersion,
    SetupOperation Operation,
    SetupEffectiveSource? EffectiveSource,
    SetupReferenceState? ReferenceState,
    SetupCurrentState? CurrentState,
    SetupRestartRequirement RestartRequirement,
    bool RollbackAvailable,
    string? Endpoint,
    JsonElement? ExpectedResult,
    SetupGuidance? Guidance,
    IReadOnlyList<SetupMemberChangeResult> Changes);

public sealed record SetupMemberChangeResult(
    string SettingKey,
    SetupOperation Operation,
    string PreviousState,
    string NewState,
    string Conflict,
    bool Managed);

public sealed record SetupChangeSetStatusResult(
    string ChangeSetId,
    string Adapter,
    string SelectedTarget,
    string CreatedAt,
    string UpdatedAt,
    SetupChangeSetState State,
    string? OutcomeCode,
    SetupCurrentState CurrentState,
    bool RollbackAvailable,
    IReadOnlyList<SetupTargetResult> Targets);

public sealed record SetupGuidance(
    string Kind,
    string Language,
    string Sample);

internal sealed record SetupStatusProjection(
    bool Detected,
    string? DetectedVersion,
    SetupOperation Operation,
    SetupEffectiveSource? EffectiveSource,
    string? Endpoint,
    JsonElement? ExpectedResult,
    SetupStatusGuidance? Guidance,
    IReadOnlyList<SetupMemberChangeResult> Changes);

internal sealed record SetupStatusGuidance(
    string Kind,
    string Language);

public enum SetupCommand
{
    Plan,
    Apply,
    Rollback,
    Status,
}

public enum SetupRecoveryOperation
{
    Apply,
    Rollback,
}

public enum SetupTargetKind
{
    Env,
    Json,
    Toml,
    StartupTask,
    File,
    Guidance,
}

public enum SetupOperation
{
    Add,
    Replace,
    Remove,
    Mixed,
    NoOp,
}

public enum SetupEffectiveSource
{
    ManagedPolicy,
    Environment,
    UserSetting,
    Default,
}

public enum SetupReferenceState
{
    Base,
    Desired,
    Previous,
    None,
}

public enum SetupCurrentState
{
    Current,
    Stale,
    Diverged,
    Unavailable,
    NotApplicable,
}

public enum SetupRestartRequirement
{
    None,
    RestartVsCode,
    RestartTerminalSession,
}

public enum SetupChangeSetState
{
    Planned,
    Applying,
    Applied,
    NoChanges,
    Compensating,
    Restored,
    RollingBack,
    Partial,
    RolledBack,
}
