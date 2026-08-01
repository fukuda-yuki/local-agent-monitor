using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Telemetry;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class LocalRepositoryRawAvailability
{
    internal const string Available = "available";
    internal const string Expired = "expired";
    internal const string NotRetained = "not_retained";
    internal const string Unknown = "unknown";

    internal static IReadOnlyList<string> CurrentlyReachable { get; } = [Available, Expired, Unknown];
    internal static bool IsDefined(string value) => value is Available or Expired or NotRetained or Unknown;
}

internal enum LocalRepositoryRawAvailabilityStatus { Success, Busy, PayloadDigestMismatch, Corrupt }

internal sealed class LocalRepositoryRawAvailabilityResult : IAsyncDisposable
{
    private LocalRepositoryRawAvailabilityResult(LocalRepositoryRawAvailabilityStatus status, string? availability, RetentionReadLease<RawTelemetryRecord>? lease) =>
        (Status, Availability, Lease) = (status, availability, lease);

    internal LocalRepositoryRawAvailabilityStatus Status { get; }
    internal string? Availability { get; }
    internal RetentionReadLease<RawTelemetryRecord>? Lease { get; }

    internal static LocalRepositoryRawAvailabilityResult Available(RetentionReadLease<RawTelemetryRecord> lease) =>
        new(LocalRepositoryRawAvailabilityStatus.Success, LocalRepositoryRawAvailability.Available, lease ?? throw new ArgumentNullException(nameof(lease)));
    internal static LocalRepositoryRawAvailabilityResult Expired() => new(LocalRepositoryRawAvailabilityStatus.Success, LocalRepositoryRawAvailability.Expired, null);
    internal static LocalRepositoryRawAvailabilityResult Unknown() => new(LocalRepositoryRawAvailabilityStatus.Success, LocalRepositoryRawAvailability.Unknown, null);
    internal static LocalRepositoryRawAvailabilityResult Busy() => new(LocalRepositoryRawAvailabilityStatus.Busy, null, null);
    internal static LocalRepositoryRawAvailabilityResult PayloadDigestMismatch() => new(LocalRepositoryRawAvailabilityStatus.PayloadDigestMismatch, null, null);
    internal static LocalRepositoryRawAvailabilityResult Corrupt() => new(LocalRepositoryRawAvailabilityStatus.Corrupt, null, null);

    public ValueTask DisposeAsync() => Lease?.DisposeAsync() ?? ValueTask.CompletedTask;
}

internal sealed class LocalRepositoryStoreBinding
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private LocalRepositoryStoreBinding(string canonicalDatabasePath, string storeInstanceId) =>
        (CanonicalDatabasePath, StoreInstanceId) = (canonicalDatabasePath, storeInstanceId);

    internal string CanonicalDatabasePath { get; }
    internal string StoreInstanceId { get; }

    internal static LocalRepositoryStoreBinding Create(string databasePath, RetentionCatalogContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(context);
        var canonicalDatabasePath = Path.GetFullPath(databasePath);
        if (!PathComparer.Equals(canonicalDatabasePath, Path.GetFullPath(context.DatabasePath)))
            throw new ArgumentException("The retention catalog context belongs to a different database.", nameof(context));
        return new(canonicalDatabasePath, context.StoreInstanceId);
    }

    internal bool Matches(LocalRepositoryStoreBinding other) =>
        other is not null
        && PathComparer.Equals(CanonicalDatabasePath, other.CanonicalDatabasePath)
        && string.Equals(StoreInstanceId, other.StoreInstanceId, StringComparison.Ordinal);

    internal bool Matches(SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!PathComparer.Equals(CanonicalDatabasePath, Path.GetFullPath(connection.DataSource))) return false;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT store_instance_id FROM retention_store_instances WHERE id=1;";
        return command.ExecuteScalar() is string storeInstanceId
            && string.Equals(StoreInstanceId, storeInstanceId, StringComparison.Ordinal);
    }
}

internal sealed class LocalRepositoryRawAvailabilityReader
{
    private readonly RawTelemetryStore rawStore;
    private readonly RetentionCatalogContext retentionContext;

    internal LocalRepositoryRawAvailabilityReader(RawTelemetryStore rawStore, RetentionCatalogContext retentionContext)
    {
        this.rawStore = rawStore ?? throw new ArgumentNullException(nameof(rawStore));
        this.retentionContext = retentionContext ?? throw new ArgumentNullException(nameof(retentionContext));
        Binding = LocalRepositoryStoreBinding.Create(rawStore.DatabasePath, retentionContext);
    }

    internal LocalRepositoryStoreBinding Binding { get; }

    internal async ValueTask<LocalRepositoryRawAvailabilityResult> ReadAsync(
        long rawRecordId,
        string? expectedPayloadSha256,
        RetentionReadKind readKind,
        CancellationToken cancellationToken)
    {
        if (rawRecordId <= 0) throw new ArgumentOutOfRangeException(nameof(rawRecordId));
        if (expectedPayloadSha256 is not null && !IsSha256(expectedPayloadSha256))
            throw new ArgumentException("The expected digest is invalid.", nameof(expectedPayloadSha256));
        cancellationToken.ThrowIfCancellationRequested();
        var initialFact = RetentionCatalogStore.LocalRepositoryAvailabilityFact(retentionContext, rawRecordId);
        if (initialFact == LocalRepositoryRetentionFact.Busy) return LocalRepositoryRawAvailabilityResult.Busy();
        if (initialFact == LocalRepositoryRetentionFact.Corrupt) return LocalRepositoryRawAvailabilityResult.Corrupt();
        RetentionReadResult<RawTelemetryRecord> read;
        try
        {
            read = await rawStore.GetRawRecordByIdAsync(rawRecordId, readKind, cancellationToken).ConfigureAwait(false);
        }
        catch (RetentionCatalogUnavailableException)
        {
            return LocalRepositoryRawAvailabilityResult.Busy();
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            return LocalRepositoryRawAvailabilityResult.Corrupt();
        }
        if (read.Disposition == RetentionReadDisposition.Busy) return LocalRepositoryRawAvailabilityResult.Busy();
        if (read.Disposition != RetentionReadDisposition.Granted || read.Lease is null)
        {
            return RetentionCatalogStore.LocalRepositoryAvailabilityFact(retentionContext, rawRecordId) switch
            {
                LocalRepositoryRetentionFact.Expired => LocalRepositoryRawAvailabilityResult.Expired(),
                LocalRepositoryRetentionFact.Unknown => LocalRepositoryRawAvailabilityResult.Unknown(),
                LocalRepositoryRetentionFact.Busy => LocalRepositoryRawAvailabilityResult.Busy(),
                _ => LocalRepositoryRawAvailabilityResult.Corrupt(),
            };
        }
        var digest = SkillProjectionHashing.InputDigest(read.Lease.Value.PayloadJson);
        if (expectedPayloadSha256 is not null && !string.Equals(expectedPayloadSha256, digest, StringComparison.Ordinal))
        {
            await read.Lease.DisposeAsync().ConfigureAwait(false);
            return LocalRepositoryRawAvailabilityResult.PayloadDigestMismatch();
        }
        return LocalRepositoryRawAvailabilityResult.Available(read.Lease);
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
