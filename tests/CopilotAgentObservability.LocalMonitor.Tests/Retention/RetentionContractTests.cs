using System.Reflection;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;

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
                "DeleteIoFailed", "UnexpectedSourceMissing", "RetryExhausted", "MaintenanceBusy",
                "AdapterCoverageMismatch", "ItemLimitExceeded"
            },
            EnumNames("RetentionErrorCode"));
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
