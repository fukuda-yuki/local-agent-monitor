namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionNoLeakTests
{
    [Fact]
    public void Claim_ToString_IsSafeTypeName()
    {
        var claim=new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionDeletionClaim
        {
            Fence=new("opaque-item",7,"owner",3),StoreInstanceId="source-id",StoreKind=CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionStoreKind.RawRecord,
            SourceIdentity=new("raw-source","receipt-material"),PrivateLocator=new("private-path"),IntentCursor=0,HasCurrentIntent=false,LeaseExpiresAt=DateTimeOffset.UnixEpoch
        };
        Assert.Equal("RetentionDeletionClaim",claim.ToString());
        Assert.DoesNotContain("source",claim.ToString(),StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("receipt",claim.ToString(),StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path",claim.ToString(),StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaintenanceAndAdapterCarrierToStrings_DoNotExposePrivateTokens()
    {
        var identity = new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionSourceIdentity("source-id-unsafe", "receipt-unsafe");
        var locator = new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionPrivateLocatorHandle("C:\\private\\database.db-wal");
        var context = new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionDeleteContext("opaque-item", "database-name", CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionStoreKind.RawRecord, 1, "owner", 1, identity, locator, 0, CancellationToken.None);
        var forbidden = new[] { "source-id-unsafe", "receipt-unsafe", "database-name", "C:\\private\\database.db-wal", "pragma", "exception" };

        foreach (var value in new object[] { identity, locator, context, CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionAdapterResult.TransientFailure(CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionErrorCode.DeleteBusy) })
            foreach (var marker in forbidden)
                Assert.DoesNotContain(marker, value.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkerCarrierToStrings_AreTypeNamesOnly()
    {
        var work = new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionWorkReference("item-source", 1, CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionWorkKind.Queued);
        var fence = new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionDeleteFence("item-source", 1, "owner-receipt", 1);
        Assert.Equal("RetentionWorkReference", work.ToString());
        Assert.Equal("RetentionDeleteFence", fence.ToString());
    }

    [Fact]
    public void RetentionCarriers_DeclareOwnToStringGuards()
    {
        var types = new[]
        {
            typeof(CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionWorkReference),
            typeof(CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionDeleteFence),
            typeof(CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionDeletionClaim),
            typeof(CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionDeleteContext),
            typeof(CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionSourceIdentity),
            typeof(CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionPrivateLocatorHandle)
        };
        Assert.All(types, type => Assert.Equal(type, type.GetMethod(nameof(ToString), Type.EmptyTypes)!.DeclaringType));
    }

    [Fact]
    public void SqliteDeletionBridgeCarriers_DoNotExposeBoundTokenOrCheckpointData()
    {
        var token = Enumerable.Repeat((byte)0xab, 32).ToArray();
        var grant = new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionSqliteDeletionGrant(
            new("source-id", CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionStoreKind.RawRecord, "7"), token);
        var forbidden = Convert.ToHexString(token).ToLowerInvariant();

        Assert.False(
            (grant.ToString() ?? string.Empty).Contains(forbidden, StringComparison.OrdinalIgnoreCase),
            "Ownership material reached a deletion carrier.");
        Assert.DoesNotContain("source-id", grant.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(
            CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionAdapterResult
                .TransientFailure(CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionErrorCode.DeleteIoFailed)
                .ToString()
                .Contains(forbidden, StringComparison.OrdinalIgnoreCase),
            "Ownership material reached an adapter result.");
    }
}
