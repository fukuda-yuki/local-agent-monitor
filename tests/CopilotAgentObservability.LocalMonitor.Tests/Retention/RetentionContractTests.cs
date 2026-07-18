using System.Reflection;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionContractTests
{
    [Fact]
    public void CatalogDomains_MatchRetentionV1()
    {
        Assert.Equal(
            new[]
            {
                "Expiring", "RetainedByPolicy", "ExpiredPendingDeletion", "DeletionQueued",
                "Deleting", "Deleted", "DeletionFailed"
            },
            EnumNames("RetentionItemLifecycle"));
        Assert.Equal(
            new[]
            {
                "SessionEventContent", "RawRecord", "AnalysisRunRaw", "SensitiveBundle", "AnalysisSdkDirectory"
            },
            EnumNames("RetentionStoreKind"));
        Assert.Equal(
            new[] { "RawDefault90Days", "SensitiveBundle7Days" },
            EnumNames("RetentionPolicyKind"));
        Assert.Equal(
            new[]
            {
                "MigrationBlocked", "MissingTimestamp", "InvalidIdentity", "OwnershipMismatch",
                "CaptureIncomplete", "LeaseConflict", "LeaseLost", "DeleteBusy", "DeletePermissionDenied",
                "DeleteIoFailed", "UnexpectedSourceMissing", "MaintenanceBusy", "ItemLimitExceeded"
            },
            EnumNames("RetentionErrorCode"));
        Assert.Equal(new[] { "RetryExhausted", "AdapterCoverageMismatch" }, EnumNames("RetentionWorkerDiagnosticCode"));
        Assert.Equal(new[] { "RequiredCleanup", "RetainedByPolicy", "NotApplicable", "Blocked" }, EnumNames("RetentionInventoryCategory"));
    }

    [Fact]
    public void SessionV1Projection_CoversEveryCanonicalCondition()
    {
        var projection = RetentionType("RetentionSessionV1Projection");
        var project = projection.GetMethod("Project", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(project);

        Assert.Equal("not_captured", Project(project!, null, false));
        Assert.Equal("expiring", Project(project!, "Expiring", true));
        Assert.Equal("expiring", Project(project!, "RetainedByPolicy", true));
        foreach (var lifecycle in new[] { "ExpiredPendingDeletion", "DeletionQueued", "Deleting", "Deleted", "DeletionFailed" })
        {
            Assert.Equal("expired_pending_deletion", Project(project!, lifecycle, true));
        }
    }

    [Fact]
    public void RetentionV1Constants_PinFinitePoliciesSchedulingAndDisposition()
    {
        var constants = RetentionType("RetentionV1Constants");
        Assert.Equal(1, constants.GetProperty("CatalogSchemaVersion")!.GetValue(null));
        Assert.Equal(1, constants.GetProperty("AdapterCoverageVersion")!.GetValue(null));
        Assert.Equal("raw-default-90d", constants.GetProperty("RawDefaultPolicyId")!.GetValue(null));
        Assert.Equal(TimeSpan.FromDays(90), constants.GetProperty("RawDefaultTtl")!.GetValue(null));
        Assert.Equal("sensitive-bundle-7d", constants.GetProperty("SensitiveBundlePolicyId")!.GetValue(null));
        Assert.Equal(TimeSpan.FromDays(7), constants.GetProperty("SensitiveBundleTtl")!.GetValue(null));
        Assert.Equal(new[] { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2) }, (TimeSpan[])constants.GetProperty("RetryDelays")!.GetValue(null)!);
        Assert.Equal(100, constants.GetProperty("ExpiryScanItemLimit")!.GetValue(null));
        Assert.Equal(5, constants.GetProperty("MaximumDeleteAttempts")!.GetValue(null));
        Assert.Equal(256, constants.GetProperty("MaximumFileMembers")!.GetValue(null));
        Assert.Equal(128L * 1024 * 1024, constants.GetProperty("MaximumFileBytes")!.GetValue(null));
        Assert.Equal(100, constants.GetProperty("ClaimBatchLimit")!.GetValue(null));
        Assert.Equal(2, constants.GetProperty("MaximumActiveDeletionWorkers")!.GetValue(null));
        Assert.Equal(TimeSpan.FromSeconds(30), constants.GetProperty("ScanElapsedBudget")!.GetValue(null));
        Assert.Equal(TimeSpan.FromSeconds(15), constants.GetProperty("WorkerWakeInterval")!.GetValue(null));
        Assert.Equal(TimeSpan.FromMinutes(2), constants.GetProperty("LeaseDuration")!.GetValue(null));
        Assert.Equal(TimeSpan.FromMinutes(1), constants.GetProperty("LeaseRenewalDeadline")!.GetValue(null));
        Assert.Equal(TimeSpan.FromMinutes(2), constants.GetProperty("ActiveOperationQuiescenceBound")!.GetValue(null));
        Assert.Equal(TimeSpan.FromMinutes(2), constants.GetProperty("ShutdownDrainBound")!.GetValue(null));
        Assert.Equal(TimeSpan.FromMinutes(1), constants.GetProperty("WalMaintenanceRetryDelay")!.GetValue(null));
        Assert.Equal(100, constants.GetProperty("StatusItemSummaryLimit")!.GetValue(null));
    }

    [Fact]
    public void RetentionItemSummary_UsesClosedInventoryCategory()
    {
        Assert.Equal(RetentionType("RetentionInventoryCategory"), RetentionType("RetentionItemSummary").GetProperty("InventoryCategory")!.PropertyType);
    }

    [Fact]
    public void SessionV1Projection_CoversNormativeRouteConditions()
    {
        var conditionType = RetentionType("RetentionSessionV1Condition");
        var projection = RetentionType("RetentionSessionV1Projection");
        var project = projection.GetMethod("ProjectCondition")!;
        var expected = new Dictionary<string, string>
        {
            ["NeverCaptured"] = "not_captured", ["ReadableExpiring"] = "expiring", ["ReadableRetainedByPolicy"] = "expiring",
            ["DeniedLifecycle"] = "expired_pending_deletion", ["StaleMissingOrRepairBlocked"] = "expired_pending_deletion",
            ["SelectedReadableWithDeniedSibling"] = "expiring", ["SelectedDeniedWithReadableSibling"] = "expiring", ["CapturedWithoutReadableSibling"] = "expired_pending_deletion",
            ["UnknownSession"] = "not_captured", ["UnknownEvent"] = "not_captured", ["SanitizedOnly"] = "not_captured"
        };
        foreach (var pair in expected)
        {
            Assert.Equal(pair.Value, project.Invoke(null, [Enum.Parse(conditionType, pair.Key)])!);
        }
    }

    [Fact]
    public void SessionV1Table_DistinguishesUnknownAndSelectedSiblingOutcomes()
    {
        var denied = RetentionSessionV1Projection.Describe(RetentionSessionV1Condition.SelectedDeniedWithReadableSibling);
        Assert.True(denied.HasEventDto);
        Assert.True(denied.HasSessionDto);
        Assert.Equal("expired_pending_deletion", denied.EventContentState);
        Assert.Equal("expiring", denied.SessionRawRetentionState);
        Assert.Equal(410, denied.StatusCode);
        Assert.Equal(RetentionSessionRouteOutcome.Expired, denied.RouteOutcome);

        var unknown = RetentionSessionV1Projection.Describe(RetentionSessionV1Condition.UnknownSession);
        Assert.False(unknown.HasEventDto); Assert.False(unknown.HasSessionDto);
        Assert.Equal(RetentionSessionRouteOutcome.SessionNotFound, unknown.RouteOutcome);
        var sanitized = RetentionSessionV1Projection.Describe(RetentionSessionV1Condition.SanitizedOnly);
        Assert.Equal(RetentionSessionRouteOutcome.HostFallback, sanitized.RouteOutcome);
        Assert.Equal("{\"accepted\":false,\"error\":\"unsupported_endpoint\",\"message\":\"Only /v1/traces is supported.\"}"u8.ToArray(), sanitized.ErrorUtf8);

        var neverCaptured = new Dictionary<RetentionSessionV1Condition, string>
        {
            [RetentionSessionV1Condition.NeverCapturedNotCaptured] = "not_captured",
            [RetentionSessionV1Condition.NeverCapturedRedacted] = "redacted",
            [RetentionSessionV1Condition.NeverCapturedUnsupported] = "unsupported"
        };
        foreach (var pair in neverCaptured)
        {
            var row = RetentionSessionV1Projection.Describe(pair.Key);
            Assert.True(row.HasEventDto); Assert.True(row.HasSessionDto);
            Assert.Equal(pair.Value, row.EventContentState);
            Assert.Equal("not_captured", row.SessionRawRetentionState); Assert.Equal(404, row.StatusCode);
            Assert.Equal(RetentionSessionRouteOutcome.SessionNotFound, row.RouteOutcome);
        }
        var unknownEvent = RetentionSessionV1Projection.Describe(RetentionSessionV1Condition.UnknownEventWithExpiringSession);
        Assert.False(unknownEvent.HasEventDto); Assert.True(unknownEvent.HasSessionDto);
        Assert.Equal("expiring", unknownEvent.SessionRawRetentionState); Assert.Equal(404, unknownEvent.StatusCode);
    }

    private static string[] EnumNames(string name) =>
        Enum.GetNames(RetentionType(name));

    private static Type RetentionType(string name) =>
        typeof(SqliteSessionStore).Assembly.GetType($"CopilotAgentObservability.Persistence.Sqlite.Retention.{name}")
        ?? throw new Xunit.Sdk.XunitException($"Retention contract type '{name}' is missing.");

    private static string Project(MethodInfo project, string? lifecycle, bool wasCaptured)
    {
        var lifecycleType = RetentionType("RetentionItemLifecycle");
        var value = lifecycle is null ? null : Enum.Parse(lifecycleType, lifecycle);
        return (string)project.Invoke(null, [value, wasCaptured])!;
    }
}
