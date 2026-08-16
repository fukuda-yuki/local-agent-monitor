using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.ConfigCli;

internal static class RawStoreLeaseReader
{
    public static T ReadAll<T>(string databasePath, Func<IReadOnlyList<RawTelemetryRecord>, T> reader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(reader);

        return ReadAllAsync(databasePath, reader).GetAwaiter().GetResult();
    }

    private static async Task<T> ReadAllAsync<T>(string databasePath, Func<IReadOnlyList<RawTelemetryRecord>, T> reader)
    {
        var context = RetentionCatalogContext.AdoptExistingCatalogV1(databasePath);
        var store = new RawTelemetryStore(databasePath, context);
        var result = await store.ListRecordsAsync(RetentionReadKind.Operation, CancellationToken.None);
        if (result.Disposition == RetentionReadDisposition.Empty && result.EmptyValue is { } emptyValue)
            return reader(emptyValue);
        if (result.Lease is null)
            throw new InvalidDataException("raw_store_unavailable");

        await using var lease = result.Lease;
        T value;
        using (var reference = lease.AcquireValueReference())
        {
            value = reader(reference.Value);
        }
        return lease.TryCompleteWithoutRaw() == RetentionRawTerminalResult.CompletedWithoutRaw
            ? value
            : throw new InvalidDataException("raw_store_unavailable");
    }
}
