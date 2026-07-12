using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CopilotAgentObservability.ConfigCli.Setup.Contracts;

public static class SetupContractValidator
{
    public const string InvalidContractCode = "setup_contract_invalid";

    private const int MaximumTargets = 16;
    private const int MaximumChangesPerTarget = 32;
    private const int MaximumStatusEntries = 100;

    private static readonly Regex UuidV7 = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex SemanticVersion = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant);
    private static readonly Regex FixedIdentifier = new(
        "^[a-z][a-z0-9_-]{0,127}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex SettingKey = new(
        "^[A-Za-z][A-Za-z0-9._-]{0,255}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex Timestamp = new(
        "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,7})?Z$",
        RegexOptions.CultureInvariant);
    private static readonly Regex UnsafeMarker = new(
        "(?<![A-Za-z0-9])(secret|token|authorization|header|raw|exception|stacktrace|bearer)(?![A-Za-z0-9])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> SuccessCodes = new(StringComparer.Ordinal)
    {
        SetupCodes.PlanReady,
        SetupCodes.NoChanges,
        SetupCodes.ApplySucceeded,
        SetupCodes.RollbackSucceeded,
        SetupCodes.StatusReady,
        SetupCodes.InterruptedApplyRecovered,
        SetupCodes.InterruptedRollbackRecovered,
    };

    private static readonly HashSet<string> WarningCodes = new(StringComparer.Ordinal)
    {
        SetupCodes.ContentCaptureSensitive,
        SetupCodes.ManagedPolicyUnverified,
        SetupCodes.MonitorNotRunning,
        SetupCodes.SharedUserEnvironmentAffectsOtherProcesses,
    };

    private static readonly HashSet<string> NextActionCodes = new(StringComparer.Ordinal)
    {
        SetupCodes.InstallVsCode,
        SetupCodes.InstallGitHubCopilotChatExtension,
        SetupCodes.UpgradeVsCode,
        SetupCodes.InstallCopilotCli,
        SetupCodes.UpgradeCopilotCli,
        SetupCodes.RunVsCodePolicyDiagnostics,
        SetupCodes.RestartVsCode,
        SetupCodes.RestartTerminalSession,
        SetupCodes.StartLocalMonitor,
        SetupCodes.ReviewContentCaptureWarning,
        SetupCodes.RunFirstTraceDoctor,
        SetupCodes.RerunRequestedSetupCommand,
    };

    public static void Validate(SetupCommandResult result)
    {
        if (result is null ||
            result.Targets is null ||
            result.ChangeSets is null ||
            result.Warnings is null ||
            result.NextActions is null ||
            !Enum.IsDefined(result.Command) ||
            !IsResultCode(result.Code) ||
            result.Success != SuccessCodes.Contains(result.Code))
        {
            Reject();
        }

        ValidateOptionalIdentifier(result.Adapter);
        ValidateStringList(result.Warnings, WarningCodes);
        ValidateStringList(result.NextActions, NextActionCodes);
        ValidateSuccessCodeForCommand(result.Command, result.Code, result.Success);
        ValidateCorrelation(result);

        if (result.Targets.Count > MaximumTargets || result.ChangeSets.Count > MaximumStatusEntries)
        {
            Reject();
        }

        if (result.Command == SetupCommand.Status)
        {
            if (result.ChangeSetId is not null || result.Targets.Count != 0)
            {
                Reject();
            }

            foreach (var changeSet in result.ChangeSets)
            {
                ValidateStatusChangeSet(changeSet);
            }
        }
        else
        {
            if (result.ChangeSets.Count != 0 || result.Truncated)
            {
                Reject();
            }

            foreach (var target in result.Targets)
            {
                ValidateTarget(target, result.Command, isStatusTarget: false);
            }
        }
    }

    private static void ValidateCorrelation(SetupCommandResult result)
    {
        var hasRecoveredId = result.RecoveredChangeSetId is not null;
        var hasRecoveryOperation = result.RecoveryOperation is not null;
        if (hasRecoveredId != hasRecoveryOperation)
        {
            Reject();
        }

        if (result.ChangeSetId is not null)
        {
            ValidateUuidV7(result.ChangeSetId);
        }

        if (result.RecoveredChangeSetId is not null)
        {
            ValidateUuidV7(result.RecoveredChangeSetId);
        }

        if (result.RecoveryOperation is { } recoveryOperation && !Enum.IsDefined(recoveryOperation))
        {
            Reject();
        }

        var recoveryCode = result.Code is SetupCodes.InterruptedApplyRecovered or SetupCodes.InterruptedRollbackRecovered;
        var recoveryFailure = result.Code == SetupCodes.InterruptedRecoveryFailed;
        if (recoveryCode || recoveryFailure)
        {
            if (!hasRecoveredId || !hasRecoveryOperation)
            {
                Reject();
            }

            if (result.Code == SetupCodes.InterruptedApplyRecovered && result.RecoveryOperation != SetupRecoveryOperation.Apply ||
                result.Code == SetupCodes.InterruptedRollbackRecovered && result.RecoveryOperation != SetupRecoveryOperation.Rollback)
            {
                Reject();
            }
        }
        else if (hasRecoveredId)
        {
            Reject();
        }

        if (result.Command == SetupCommand.Status)
        {
            return;
        }

        if (recoveryCode && result.Command == SetupCommand.Plan || recoveryCode && result.Command == SetupCommand.Status)
        {
            if (result.ChangeSetId is not null)
            {
                Reject();
            }

            return;
        }

        if (result.Success && result.ChangeSetId is null)
        {
            Reject();
        }
    }

    private static void ValidateSuccessCodeForCommand(SetupCommand command, string code, bool success)
    {
        if (!success || code is SetupCodes.InterruptedApplyRecovered or SetupCodes.InterruptedRollbackRecovered)
        {
            return;
        }

        var valid = command switch
        {
            SetupCommand.Plan => code is SetupCodes.PlanReady or SetupCodes.NoChanges,
            SetupCommand.Apply => code is SetupCodes.ApplySucceeded or SetupCodes.NoChanges,
            SetupCommand.Rollback => code == SetupCodes.RollbackSucceeded,
            SetupCommand.Status => code == SetupCodes.StatusReady,
            _ => false,
        };

        if (!valid)
        {
            Reject();
        }
    }

    private static void ValidateStatusChangeSet(SetupChangeSetStatusResult? changeSet)
    {
        if (changeSet is null ||
            changeSet.Targets is null ||
            !Enum.IsDefined(changeSet.State) ||
            !Enum.IsDefined(changeSet.CurrentState))
        {
            Reject();
        }

        ValidateUuidV7(changeSet.ChangeSetId);
        ValidateFixedIdentifier(changeSet.Adapter);
        ValidateFixedIdentifier(changeSet.SelectedTarget);
        var createdAt = ValidateUtcTimestamp(changeSet.CreatedAt);
        var updatedAt = ValidateUtcTimestamp(changeSet.UpdatedAt);
        if (createdAt > updatedAt || changeSet.Targets.Count > MaximumTargets)
        {
            Reject();
        }

        if (changeSet.OutcomeCode is not null && !IsResultCode(changeSet.OutcomeCode))
        {
            Reject();
        }

        foreach (var target in changeSet.Targets)
        {
            ValidateTarget(target, SetupCommand.Status, isStatusTarget: true);
            ValidateStatusTargetLifecycle(changeSet.State, target!);
        }

        var writableTargets = changeSet.Targets.Where(target => target is not null && target.TargetKind != SetupTargetKind.Guidance).ToArray();
        var expectedCurrentState = AggregateCurrentState(writableTargets);
        var expectedRollbackAvailable = changeSet.State == SetupChangeSetState.Applied &&
            writableTargets.Length > 0 &&
            writableTargets.All(target => target.CurrentState == SetupCurrentState.Current && target.RollbackAvailable);

        if (changeSet.CurrentState != expectedCurrentState || changeSet.RollbackAvailable != expectedRollbackAvailable)
        {
            Reject();
        }
    }

    private static void ValidateTarget(SetupTargetResult? target, SetupCommand command, bool isStatusTarget)
    {
        if (target is null ||
            target.Changes is null ||
            !Enum.IsDefined(target.TargetKind) ||
            !Enum.IsDefined(target.Operation) ||
            !Enum.IsDefined(target.RestartRequirement) ||
            target.EffectiveSource is { } effectiveSource && !Enum.IsDefined(effectiveSource) ||
            target.ReferenceState is { } referenceState && !Enum.IsDefined(referenceState) ||
            target.CurrentState is { } currentState && !Enum.IsDefined(currentState))
        {
            Reject();
        }

        ValidateUuidV7(target.RecordId);
        ValidateFixedIdentifier(target.TargetLabel);
        if (target.DetectedVersion is not null && !SemanticVersion.IsMatch(target.DetectedVersion))
        {
            Reject();
        }

        if (!isStatusTarget && (target.ReferenceState is not null || target.CurrentState is not null))
        {
            Reject();
        }

        if (target.Endpoint is not null && !IsCredentialFreeLoopbackHttpEndpoint(target.Endpoint))
        {
            Reject();
        }

        ValidateExpectedResult(target.ExpectedResult);

        if (target.TargetKind == SetupTargetKind.Guidance)
        {
            ValidateGuidanceTarget(target, command, isStatusTarget);
            return;
        }

        if (target.Guidance is not null || target.Changes.Count is 0 or > MaximumChangesPerTarget)
        {
            Reject();
        }

        foreach (var change in target.Changes)
        {
            ValidateMemberChange(change);
        }

        if (target.Operation != AggregateOperation(target.Changes))
        {
            Reject();
        }
    }

    private static void ValidateGuidanceTarget(SetupTargetResult target, SetupCommand command, bool isStatusTarget)
    {
        if (target.Guidance is null ||
            target.Operation != SetupOperation.NoOp ||
            target.EffectiveSource is not null ||
            target.Endpoint is not null ||
            target.ExpectedResult is not null ||
            target.RestartRequirement != SetupRestartRequirement.None ||
            target.RollbackAvailable ||
            target.Changes.Count != 0)
        {
            Reject();
        }

        if (isStatusTarget && (target.ReferenceState != SetupReferenceState.None || target.CurrentState != SetupCurrentState.NotApplicable))
        {
            Reject();
        }

        ValidateFixedIdentifier(target.Guidance.Kind);
        ValidateFixedIdentifier(target.Guidance.Language);
        if (command == SetupCommand.Plan && !IsRepositorySafeText(target.Guidance.Sample))
        {
            Reject();
        }
    }

    private static void ValidateMemberChange(SetupMemberChangeResult? change)
    {
        if (change is null || !Enum.IsDefined(change.Operation))
        {
            Reject();
        }

        if (!IsSafeSettingKey(change.SettingKey) ||
            !IsSafeFixedValue(change.PreviousState) ||
            !IsSafeFixedValue(change.NewState) ||
            !IsSafeFixedValue(change.Conflict))
        {
            Reject();
        }
    }

    private static void ValidateStatusTargetLifecycle(SetupChangeSetState state, SetupTargetResult target)
    {
        if (target.TargetKind == SetupTargetKind.Guidance)
        {
            return;
        }

        if (target.CurrentState == SetupCurrentState.NotApplicable)
        {
            Reject();
        }

        if (target.CurrentState is SetupCurrentState.Diverged or SetupCurrentState.Unavailable)
        {
            RequireReferenceState(target, SetupReferenceState.None);
            if (target.RollbackAvailable)
            {
                Reject();
            }

            return;
        }

        switch (state)
        {
            case SetupChangeSetState.Planned:
                RequireReferenceState(target, SetupReferenceState.Base);
                break;
            case SetupChangeSetState.NoChanges:
            case SetupChangeSetState.Applied:
                RequireReferenceState(target, SetupReferenceState.Desired);
                break;
            case SetupChangeSetState.Restored:
            case SetupChangeSetState.RolledBack:
                RequireReferenceState(target, SetupReferenceState.Previous);
                break;
            case SetupChangeSetState.Partial:
                if (target.ReferenceState is not SetupReferenceState.Desired and not SetupReferenceState.Previous)
                {
                    Reject();
                }

                if (target.RollbackAvailable)
                {
                    Reject();
                }

                break;
            case SetupChangeSetState.Applying:
            case SetupChangeSetState.Compensating:
            case SetupChangeSetState.RollingBack:
                if (target.ReferenceState is not SetupReferenceState.Base and not SetupReferenceState.Desired and not SetupReferenceState.Previous)
                {
                    Reject();
                }

                break;
            default:
                Reject();
                break;
        }
    }

    private static SetupCurrentState AggregateCurrentState(IReadOnlyList<SetupTargetResult> writableTargets)
    {
        if (writableTargets.Count == 0)
        {
            return SetupCurrentState.NotApplicable;
        }

        if (writableTargets.Any(target => target.CurrentState == SetupCurrentState.Diverged))
        {
            return SetupCurrentState.Diverged;
        }

        if (writableTargets.Any(target => target.CurrentState == SetupCurrentState.Stale))
        {
            return SetupCurrentState.Stale;
        }

        if (writableTargets.Any(target => target.CurrentState == SetupCurrentState.Unavailable))
        {
            return SetupCurrentState.Unavailable;
        }

        return SetupCurrentState.Current;
    }

    private static SetupOperation AggregateOperation(IReadOnlyList<SetupMemberChangeResult> changes)
    {
        var nonNoOpOperations = changes
            .Select(change => change.Operation)
            .Where(operation => operation != SetupOperation.NoOp)
            .Distinct()
            .ToArray();

        return nonNoOpOperations.Length switch
        {
            0 => SetupOperation.NoOp,
            1 => nonNoOpOperations[0],
            _ => SetupOperation.Mixed,
        };
    }

    private static void ValidateExpectedResult(JsonElement? expectedResult)
    {
        if (expectedResult is not { } value || value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        ValidateJsonValue(value);
    }

    private static void ValidateJsonValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (!IsRepositorySafeText(property.Name))
                    {
                        Reject();
                    }

                    ValidateJsonValue(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    ValidateJsonValue(item);
                }

                break;
            case JsonValueKind.String:
                if (!IsRepositorySafeText(value.GetString()))
                {
                    Reject();
                }

                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                break;
            default:
                Reject();
                break;
        }
    }

    private static void ValidateStringList(IReadOnlyList<string> values, ISet<string> allowedValues)
    {
        foreach (var value in values)
        {
            if (value is null || !allowedValues.Contains(value))
            {
                Reject();
            }
        }
    }

    private static bool IsResultCode(string? value) => value is not null && SetupCodes.ResultCodes.Contains(value, StringComparer.Ordinal);

    private static void ValidateUuidV7(string? value)
    {
        if (value is null || !UuidV7.IsMatch(value))
        {
            Reject();
        }
    }

    private static DateTimeOffset ValidateUtcTimestamp(string? value)
    {
        var timestamp = default(DateTimeOffset);
        if (value is null || !Timestamp.IsMatch(value) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp) ||
            timestamp.Offset != TimeSpan.Zero)
        {
            Reject();
        }

        return timestamp;
    }

    private static void ValidateOptionalIdentifier(string? value)
    {
        if (value is not null)
        {
            ValidateFixedIdentifier(value);
        }
    }

    private static void ValidateFixedIdentifier(string? value)
    {
        if (!IsSafeFixedValue(value))
        {
            Reject();
        }
    }

    private static bool IsSafeSettingKey(string? value) => value is not null &&
        SettingKey.IsMatch(value) &&
        !ContainsUnsafeMarker(value);

    private static bool IsSafeFixedValue(string? value) => value is not null &&
        FixedIdentifier.IsMatch(value) &&
        !ContainsUnsafeMarker(value);

    private static bool IsRepositorySafeText(string? value) => value is not null &&
        value.Length > 0 &&
        !ContainsAbsolutePath(value) &&
        !ContainsUnsafeMarker(value);

    private static bool ContainsUnsafeMarker(string value) => UnsafeMarker.IsMatch(value) ||
        value.Contains("stack trace", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAbsolutePath(string value) =>
        value.StartsWith("/", StringComparison.Ordinal) ||
        value.StartsWith("\\", StringComparison.Ordinal) ||
        value.StartsWith("~/", StringComparison.Ordinal) ||
        value.StartsWith("~\\", StringComparison.Ordinal) ||
        Regex.IsMatch(value, "(?:^|[^A-Za-z0-9_])[A-Za-z]:[\\\\/]", RegexOptions.CultureInvariant);

    private static bool IsCredentialFreeLoopbackHttpEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttp ||
            endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 ||
            endpoint.Fragment.Length != 0 ||
            endpoint.Port is < 1 or > 65535)
        {
            return false;
        }

        return endpoint.DnsSafeHost.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            endpoint.DnsSafeHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            endpoint.DnsSafeHost.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireReferenceState(SetupTargetResult target, SetupReferenceState expected)
    {
        if (target.ReferenceState != expected)
        {
            Reject();
        }
    }

    [DoesNotReturn]
    private static void Reject() => throw new InvalidOperationException(InvalidContractCode);
}
