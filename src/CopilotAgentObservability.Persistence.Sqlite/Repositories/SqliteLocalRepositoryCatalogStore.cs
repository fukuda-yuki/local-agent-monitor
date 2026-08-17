using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum LocalRepositoryAdmissionCheckpoint
{
    BeforePayloadParsing,
    AfterPreparationBeforeHandoff,
    BeforeTransaction,
    BeforePublication,
    AfterRepositories,
    AfterLocators,
    AfterLocatorHeads,
    AfterRepositoryHistory,
    AfterObservations,
    AfterContexts,
    AfterAssignments,
    BeforeQueueCompletion,
    BeforePublicationClaim,
}

internal interface ILocalRepositoryAdmissionCheckpoint
{
    void Reached(LocalRepositoryAdmissionCheckpoint checkpoint);
}

internal enum LocalRepositoryLocatorReadCheckpoint { BeforeAvailabilityRead, AfterAvailabilityLeaseAcquired }

internal interface ILocalRepositoryLocatorReadCheckpoint
{
    void Reached(LocalRepositoryLocatorReadCheckpoint checkpoint);
}

internal sealed class LocalRepositoryAdmissionRetryableException(string message) : Exception(message);

internal sealed partial class SqliteLocalRepositoryCatalogStore : ILocalRepositoryRawRecordProcessor
{
    private readonly string databasePath;
    private readonly LocalRepositoryStoreBinding binding;
    private readonly SqliteLocalRepositoryReconciliationStore queue;
    private readonly LocalRepositoryAssignmentResolver assignmentResolver;
    private readonly TimeProvider timeProvider;
    private readonly Func<DateTimeOffset, string> uuidV7Factory;
    private readonly ILocalRepositoryAdmissionCheckpoint? checkpoint;
    private readonly ILocalRepositoryLocatorReadCheckpoint? locatorReadCheckpoint;

    internal SqliteLocalRepositoryCatalogStore(
        string databasePath,
        SqliteLocalRepositoryReconciliationStore queue,
        LocalRepositoryAssignmentResolver assignmentResolver,
        TimeProvider? timeProvider = null,
        Func<DateTimeOffset, string>? uuidV7Factory = null,
        ILocalRepositoryAdmissionCheckpoint? checkpoint = null,
        ILocalRepositoryLocatorReadCheckpoint? locatorReadCheckpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.assignmentResolver = assignmentResolver ?? throw new ArgumentNullException(nameof(assignmentResolver));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        var retentionContext = RetentionCatalogContext.AdoptExistingCatalogV1(databasePath);
        binding = LocalRepositoryStoreBinding.Create(
            databasePath,
            retentionContext);
        this.databasePath = binding.CanonicalDatabasePath;
        var rawStore = new RawTelemetryStore(this.databasePath, retentionContext, this.timeProvider);
        if (!this.queue.IsBoundTo(new LocalRepositoryRawAvailabilityReader(rawStore, retentionContext)))
            throw new InvalidOperationException("local_repository_store_binding_mismatch");
        this.uuidV7Factory = uuidV7Factory
            ?? (static at => Guid.CreateVersion7(at).ToString("D", CultureInfo.InvariantCulture));
        this.checkpoint = checkpoint;
        this.locatorReadCheckpoint = locatorReadCheckpoint;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=1;";
        command.ExecuteNonQuery();
        return connection;
    }

    private string NextId(DateTimeOffset at)
    {
        var id = uuidV7Factory(at);
        if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(id))
            throw new LocalRepositoryAdmissionRetryableException("local_repository_catalog_generated_id_invalid");
        return id;
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
