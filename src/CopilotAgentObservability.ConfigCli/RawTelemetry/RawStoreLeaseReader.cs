using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.ConfigCli;

internal static class RawStoreLeaseReader
{
    public static IReadOnlyList<MeasurementRow> ReadMeasurements(string databasePath) =>
        ReadMeasurementsAsync(databasePath).GetAwaiter().GetResult();

    public static IReadOnlyList<DashboardRawOperation> ReadDashboardOperations(
        string databasePath,
        Action? afterMappingForTesting = null) =>
        ReadDashboardOperationsAsync(databasePath, afterMappingForTesting).GetAwaiter().GetResult();

    public static RawEvidenceIndex ReadEvidence(string databasePath) =>
        ReadEvidenceAsync(databasePath).GetAwaiter().GetResult();

    private static async Task<IReadOnlyList<MeasurementRow>> ReadMeasurementsAsync(string databasePath)
    {
        var result = await ReadLeaseAsync(databasePath).ConfigureAwait(false);
        if (result.Disposition == RetentionReadDisposition.Empty && result.EmptyValue is { } emptyValue)
            return RawMeasurementNormalizer.Normalize(emptyValue);
        var lease = RequireLease(result);
        await using (lease.ConfigureAwait(false))
        {
            RequireConsumable(result);
            IReadOnlyList<MeasurementRow> value;
            using (var reference = lease.AcquireValueReference())
                value = RawMeasurementNormalizer.Normalize(reference.Value);
            return SealOrThrow(lease, value);
        }
    }

    private static async Task<IReadOnlyList<DashboardRawOperation>> ReadDashboardOperationsAsync(
        string databasePath,
        Action? afterMappingForTesting)
    {
        var result = await ReadLeaseAsync(databasePath).ConfigureAwait(false);
        if (result.Disposition == RetentionReadDisposition.Empty && result.EmptyValue is { } emptyValue)
            return DashboardRawOperationReader.MapRawStoreRecords(emptyValue);
        var lease = RequireLease(result);
        await using (lease.ConfigureAwait(false))
        {
            RequireConsumable(result);
            IReadOnlyList<DashboardRawOperation> value;
            using (var reference = lease.AcquireValueReference())
                value = DashboardRawOperationReader.MapRawStoreRecords(reference.Value);
            afterMappingForTesting?.Invoke();
            return SealOrThrow(lease, value);
        }
    }

    private static async Task<RawEvidenceIndex> ReadEvidenceAsync(string databasePath)
    {
        var result = await ReadLeaseAsync(databasePath).ConfigureAwait(false);
        if (result.Disposition == RetentionReadDisposition.Empty && result.EmptyValue is { } emptyValue)
            return RawEvidenceReader.MapRawStoreRecords(databasePath, emptyValue);
        var lease = RequireLease(result);
        await using (lease.ConfigureAwait(false))
        {
            RequireConsumable(result);
            RawEvidenceIndex value;
            using (var reference = lease.AcquireValueReference())
                value = RawEvidenceReader.MapRawStoreRecords(databasePath, reference.Value);
            return SealOrThrow(lease, value);
        }
    }

    private static async Task<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ReadLeaseAsync(
        string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var context = RetentionCatalogContext.AdoptExistingCatalogV1(databasePath);
        var store = new RawTelemetryStore(databasePath, context);
        return await store.ListRecordsAsync(RetentionReadKind.Operation, CancellationToken.None).ConfigureAwait(false);
    }

    private static RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>> RequireLease(
        RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>> result)
    {
        if (result.Lease is null)
            throw new InvalidDataException("raw_store_unavailable");
        return result.Lease;
    }

    private static void RequireConsumable(
        RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>> result)
    {
        if (result.Disposition is null) return;
        _ = result.CompletePostGrantFailure();
        throw new InvalidDataException("raw_store_unavailable");
    }

    private static T SealOrThrow<T>(RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>> lease, T value) =>
        lease.TrySealRawResponse() == RetentionRawTerminalResult.Sealed
            ? value
            : throw new InvalidDataException("raw_store_unavailable");
}
