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
}
