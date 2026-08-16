using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Telemetry;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositoryRawAvailabilityTests
{
    [Fact]
    public void ReservedNotRetained_IsValidatedButNeverProducedByCurrentAvailabilityFacts()
    {
        Assert.True(LocalRepositoryRawAvailability.IsDefined(LocalRepositoryRawAvailability.NotRetained));
        Assert.DoesNotContain(LocalRepositoryRawAvailability.NotRetained, LocalRepositoryRawAvailability.CurrentlyReachable);
        Assert.Empty(typeof(LocalRepositoryRawAvailabilityResult).GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
    }

    [Fact]
    public async Task Reader_SeparatesAvailableDigestMatchFromSameIdDigestCorruption()
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        const string payload = "{\"resourceSpans\":[]}";
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, payload));
        Func<RawTelemetryRecord>? retainedAccess = null;
        var observedPayload = string.Empty;
        var reader = new LocalRepositoryRawAvailabilityReader(
            rawStore,
            temp.RetentionContext,
            access =>
            {
                retainedAccess = access;
                observedPayload = access().PayloadJson;
            });
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

        await using (var available = await reader.ReadAsync(rawId, digest, RetentionReadKind.Access, CancellationToken.None))
        {
            Assert.Equal(LocalRepositoryRawAvailabilityStatus.Success, available.Status);
            Assert.Equal(LocalRepositoryRawAvailability.Available, available.Availability);
            Assert.NotNull(available.Lease);
        }
        Assert.Equal(payload, observedPayload);
        Assert.Throws<ObjectDisposedException>(() => retainedAccess!());
        await using var mismatch = await reader.ReadAsync(rawId, new string('a', 64), RetentionReadKind.Access, CancellationToken.None);
        Assert.Equal(LocalRepositoryRawAvailabilityStatus.PayloadDigestMismatch, mismatch.Status);
        Assert.Null(mismatch.Availability);
        Assert.Null(mismatch.Lease);
    }

    [Fact]
    public async Task Reader_MapsAcceptedDeletionToExpiredAndMissingExactFactToUnknown()
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{}"));
        using (var connection = Open(temp.DatabasePath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at)
                SELECT item_id,'2026-08-01T00:00:00.0000000+00:00','2026-08-01T00:00:00.0000000+00:00'
                FROM retention_items WHERE store_kind='raw_record' AND source_item_id=$id;
                UPDATE retention_items SET state='deleted',read_denied_at='2026-08-01T00:00:00.0000000+00:00',deleted_at='2026-08-01T00:00:00.0000000+00:00' WHERE store_kind='raw_record' AND source_item_id=$id;
                """;
            command.Parameters.AddWithValue("$id", rawId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(2, command.ExecuteNonQuery());
        }
        var reader = new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext);
        using (var verify = Open(temp.DatabasePath))
        using (var command = verify.CreateCommand())
        {
            command.CommandText = "SELECT state FROM retention_items WHERE store_kind='raw_record' AND source_item_id=$id;";
            command.Parameters.AddWithValue("$id", rawId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal("deleted", Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }
        Assert.Equal(LocalRepositoryRetentionFact.Expired, RetentionCatalogStore.LocalRepositoryAvailabilityFact(temp.RetentionContext, rawId));

        await using var expired = await reader.ReadAsync(rawId, null, RetentionReadKind.Access, CancellationToken.None);
        await using var unknown = await reader.ReadAsync(rawId + 1, null, RetentionReadKind.Access, CancellationToken.None);

        Assert.Equal(LocalRepositoryRawAvailabilityStatus.Success, expired.Status);
        Assert.Equal(LocalRepositoryRawAvailability.Expired, expired.Availability);
        Assert.Equal(LocalRepositoryRawAvailabilityStatus.Success, unknown.Status);
        Assert.Equal(LocalRepositoryRawAvailability.Unknown, unknown.Availability);
    }

    [Fact]
    public void AvailabilityFact_OnlyAcceptsEnumeratedPostExpiryDeletionFailures()
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{}"));
        using var connection = Open(temp.DatabasePath);
        using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE retention_items
            SET state='deletion_failed',read_denied_at='1970-04-02T00:00:00.0000000+00:00',error_code=$error
            WHERE store_kind='raw_record' AND source_item_id=$id;
            """;
        update.Parameters.AddWithValue("$id", rawId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("$error", "retention_delete_io_failed");
        Assert.Equal(1, update.ExecuteNonQuery());
        Assert.Equal(LocalRepositoryRetentionFact.Expired, RetentionCatalogStore.LocalRepositoryAvailabilityFact(temp.RetentionContext, rawId));

        update.Parameters["$error"].Value = DBNull.Value;
        Assert.Equal(1, update.ExecuteNonQuery());
        Assert.Equal(LocalRepositoryRetentionFact.Corrupt, RetentionCatalogStore.LocalRepositoryAvailabilityFact(temp.RetentionContext, rawId));

        update.Parameters["$error"].Value = "retention_future_error";
        Assert.Equal(1, update.ExecuteNonQuery());
        Assert.Equal(LocalRepositoryRetentionFact.Corrupt, RetentionCatalogStore.LocalRepositoryAvailabilityFact(temp.RetentionContext, rawId));
    }

    [Theory]
    [InlineData("expired_pending_deletion", null, false)]
    [InlineData("deletion_queued", null, false)]
    [InlineData("deleting", null, false)]
    [InlineData("deletion_failed", "retention_delete_busy", false)]
    [InlineData("deletion_failed", "retention_delete_permission_denied", false)]
    [InlineData("deletion_failed", "retention_delete_io_failed", false)]
    [InlineData("deleted", null, true)]
    public void AvailabilityFact_AcceptsDeleteNowLifecycleBeforeNaturalExpiry(string state, string? error, bool tombstone)
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{}"));
        using var connection = Open(temp.DatabasePath);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE retention_items SET state=$state,expires_at='1970-04-01T00:00:00.0000000+00:00',read_denied_at='1970-01-02T00:00:00.0000000+00:00',deleted_at=$deleted,error_code=$error WHERE store_kind='raw_record' AND source_item_id=$id;";
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$deleted", tombstone ? "1970-01-03T00:00:00.0000000+00:00" : DBNull.Value);
            command.Parameters.AddWithValue("$error", error ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$id", rawId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        if (tombstone)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) SELECT item_id,'1970-01-03T00:00:00.0000000+00:00','1970-01-03T00:00:00.0000000+00:00' FROM retention_items WHERE store_kind='raw_record' AND source_item_id=$id;";
            command.Parameters.AddWithValue("$id", rawId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        Assert.Equal(LocalRepositoryRetentionFact.Expired, RetentionCatalogStore.LocalRepositoryAvailabilityFact(temp.RetentionContext, rawId));
    }

    [Fact]
    public void AvailabilityFact_RejectsDeletionCompletedBeforeReadDenial()
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{}"));
        using var connection = Open(temp.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE retention_items
            SET state='deleted',read_denied_at='1970-01-03T00:00:00.0000000+00:00',deleted_at='1970-01-02T00:00:00.0000000+00:00'
            WHERE store_kind='raw_record' AND source_item_id=$id;
            INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at)
            SELECT item_id,'1970-01-02T00:00:00.0000000+00:00','1970-01-02T00:00:00.0000000+00:00'
            FROM retention_items WHERE store_kind='raw_record' AND source_item_id=$id;
            """;
        command.Parameters.AddWithValue("$id", rawId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(2, command.ExecuteNonQuery());

        Assert.Equal(LocalRepositoryRetentionFact.Corrupt, RetentionCatalogStore.LocalRepositoryAvailabilityFact(temp.RetentionContext, rawId));
    }

    [Theory]
    [InlineData("deleted", false, null, "Corrupt")]
    [InlineData("expired_pending_deletion", false, null, "Expired")]
    [InlineData("deletion_queued", false, null, "Expired")]
    [InlineData("deleting", false, null, "Expired")]
    [InlineData("deletion_failed", false, "retention_delete_busy", "Expired")]
    [InlineData("deletion_failed", false, "retention_delete_permission_denied", "Expired")]
    [InlineData("deletion_failed", false, "retention_delete_io_failed", "Expired")]
    [InlineData("deletion_failed", false, "retention_unexpected_source_missing", "Unknown")]
    [InlineData("deletion_failed", false, "retention_invalid_identity", "Unknown")]
    [InlineData("deletion_failed", false, "retention_ownership_mismatch", "Unknown")]
    public void AvailabilityFact_RequiresTheExactLifecycleUnion(string state, bool tombstone, string? error, string expected)
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{}"));
        using var connection = Open(temp.DatabasePath);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE retention_items SET state=$state,read_denied_at='2099-01-01T00:00:00.0000000+00:00',error_code=$error WHERE store_kind='raw_record' AND source_item_id=$id;";
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$error", error ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$id", rawId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        if (tombstone)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) SELECT item_id,'1970-01-01T00:00:00.0000000+00:00','1970-01-01T00:00:00.0000000+00:00' FROM retention_items WHERE store_kind='raw_record' AND source_item_id=$id;";
            command.Parameters.AddWithValue("$id", rawId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
        Assert.Equal(Enum.Parse<LocalRepositoryRetentionFact>(expected), RetentionCatalogStore.LocalRepositoryAvailabilityFact(temp.RetentionContext, rawId));
    }

    [Theory]
    [InlineData("retention_unexpected_source_missing")]
    [InlineData("retention_invalid_identity")]
    [InlineData("retention_ownership_mismatch")]
    public void AvailabilityFact_PrematureOrIdentityDeletionFailuresRemainUnknownBeforeExpiry(string error)
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{}"));
        using var connection = Open(temp.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE retention_items
            SET state='deletion_failed',read_denied_at='1970-01-01T00:00:00.0000000+00:00',error_code=$error
            WHERE store_kind='raw_record' AND source_item_id=$id;
            """;
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$id", rawId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1, command.ExecuteNonQuery());

        Assert.Equal(LocalRepositoryRetentionFact.Unknown, RetentionCatalogStore.LocalRepositoryAvailabilityFact(temp.RetentionContext, rawId));
    }

    [Theory]
    [InlineData("expires_at='not-a-timestamp'")]
    [InlineData("state=42")]
    [InlineData("state='deletion_failed',error_code=42,read_denied_at='2099-01-01T00:00:00.0000000+00:00'")]
    [InlineData("state='deletion_failed',error_code='retention_future_error',read_denied_at='2099-01-01T00:00:00.0000000+00:00'")]
    [InlineData("state='expired_pending_deletion',read_denied_at=42")]
    [InlineData("state='deleted',deleted_at=42")]
    public void AvailabilityFact_MalformedTypesTimestampsAndUnknownErrorsAreTypedCorrupt(string mutation)
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{}"));
        using var connection = Open(temp.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA ignore_check_constraints=ON;
            UPDATE retention_items SET {mutation} WHERE store_kind='raw_record' AND source_item_id=$id;
            """;
        command.Parameters.AddWithValue("$id", rawId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1, command.ExecuteNonQuery());

        Assert.Equal(LocalRepositoryRetentionFact.Corrupt, RetentionCatalogStore.LocalRepositoryAvailabilityFact(temp.RetentionContext, rawId));
    }

    [Theory]
    [InlineData("captured_at='not-a-timestamp'")]
    [InlineData("state=42")]
    [InlineData("read_denied_at=42")]
    public async Task Reader_MalformedRetentionMaterializationIsTypedCorrupt(string mutation)
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{}"));
        using (var connection = Open(temp.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                PRAGMA ignore_check_constraints=ON;
                UPDATE retention_items SET {mutation} WHERE store_kind='raw_record' AND source_item_id=$id;
                """;
            command.Parameters.AddWithValue("$id", rawId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var reader = new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext);

        await using var result = await reader.ReadAsync(rawId, null, RetentionReadKind.Operation, CancellationToken.None);

        Assert.Equal(LocalRepositoryRawAvailabilityStatus.Corrupt, result.Status);
        Assert.Null(result.Availability);
        Assert.Null(result.Lease);
        using var verify = Open(temp.DatabasePath);
        using var leases = verify.CreateCommand();
        leases.CommandText = "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';";
        Assert.Equal(0L, Convert.ToInt64(leases.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
