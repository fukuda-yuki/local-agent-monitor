using System.Reflection;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class CommittedReadLeaseRawAccessTests
{
    [Theory]
    [InlineData(typeof(RetentionReadLease<>))]
    [InlineData(typeof(RetentionBatchReadLease<>))]
    public void CommittedReadLease_ExposesNoDurableValueGetter(Type leaseType)
    {
        Assert.Null(leaseType.GetProperty(
            "Value",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
    }

    [Theory]
    [InlineData(
        "CopilotAgentObservability.Persistence.Sqlite/SourceCompatibility/SourceCompatibilityReconciler.cs",
        "using var retainedRecordReference = lease.AcquireValueReference();",
        "retainedRecordReference.Value")]
    [InlineData(
        "CopilotAgentObservability.Persistence.Sqlite/SkillProjection/SkillProjectionWorker.cs",
        "using var recordsReference = retentionLease.AcquireValueReference();",
        "recordsReference.Value")]
    [InlineData(
        "CopilotAgentObservability.Persistence.Sqlite/Repositories/LocalRepositoryRawAvailabilityReader.cs",
        "using (var reference = read.Lease.AcquireValueReference())",
        "reference.Value.PayloadJson")]
    [InlineData(
        "CopilotAgentObservability.Persistence.Sqlite/Repositories/LocalRepositoryReconciliationWorker.cs",
        "using var rawReference = raw.Lease.AcquireValueReference();",
        "rawReference.Value")]
    [InlineData(
        "CopilotAgentObservability.Persistence.Sqlite/Repositories/SqliteLocalRepositoryReconciliationStore.cs",
        "using var rawReference = input.Result.Lease.AcquireValueReference();",
        "rawReference.Value.PayloadJson")]
    public void RawConsumer_HoldsValueReferenceThroughItsLastRawAccess(
        string relativePath,
        string acquisition,
        string referencedAccess)
    {
        var source = File.ReadAllText(SourcePath(relativePath));

        var acquisitionIndex = source.IndexOf(acquisition, StringComparison.Ordinal);
        var accessIndex = source.LastIndexOf(referencedAccess, StringComparison.Ordinal);

        Assert.True(acquisitionIndex >= 0, $"Missing scoped acquisition in {relativePath}.");
        Assert.True(accessIndex > acquisitionIndex, $"Raw access is not reference-backed in {relativePath}.");
    }

    private static string SourcePath(string relativePath)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Path.Combine(root, "src", relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
