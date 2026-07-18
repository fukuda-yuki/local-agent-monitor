using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.Persistence.Sqlite.Ingestion;

internal sealed class SqliteIngestionCommitStore : IIngestionCommitStore
{
    private readonly string databasePath;
    private readonly RawTelemetryStoreConnectionOptions connectionOptions;
    private readonly Action<IngestionCommitWritePhase>? writeFailureInjector;

    public SqliteIngestionCommitStore(
        string databasePath,
        RawTelemetryStoreConnectionOptions? connectionOptions = null)
        : this(databasePath, connectionOptions, writeFailureInjector: null)
    {
    }

    internal SqliteIngestionCommitStore(
        string databasePath,
        RawTelemetryStoreConnectionOptions? connectionOptions,
        Action<IngestionCommitWritePhase>? writeFailureInjector)
    {
        this.databasePath = databasePath;
        this.connectionOptions = connectionOptions ?? RawTelemetryStoreConnectionOptions.Default;
        this.writeFailureInjector = writeFailureInjector;
    }

    public CommittedIngestionIds Commit(ValidatedIngestionBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            if (FindExisting(connection, transaction, batch.IngestBatchId) is { } existing)
            {
                transaction.Commit();
                return existing;
            }

            var catalog = new RetentionCatalogStore(databasePath);
            try
            {
                catalog.InitializeForWrite(connection, transaction);
            }
            catch (RetentionMigrationBlockedException)
            {
                throw new IngestionCommitFailedException();
            }

            var ownerToken = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var rawRecordId = RawTelemetryRecordSql.Insert(connection, transaction, batch.RawRecord, ownerToken);
            writeFailureInjector?.Invoke(IngestionCommitWritePhase.AfterRawRecordInsert);
            catalog.RegisterRawRecord(connection, transaction, rawRecordId, batch.RawRecord.ReceivedAt, batch.RawRecord.SchemaVersion, ownerToken);
            writeFailureInjector?.Invoke(IngestionCommitWritePhase.AfterCatalogRegistration);
            var observationId = SqliteSourceCompatibilityStore.InsertBatch(
                connection,
                transaction,
                rawRecordId,
                batch.Observation);
            InsertProjectionDisposition(connection, transaction, rawRecordId, batch.RawRecord.ReceivedAt);
            writeFailureInjector?.Invoke(IngestionCommitWritePhase.BeforeCommit);
            transaction.Commit();
            return new CommittedIngestionIds(rawRecordId, observationId);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new IngestionCommitBusyException();
        }
    }

    private static void InsertProjectionDisposition(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRecordId,
        DateTimeOffset observedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO monitor_projection_dispositions (raw_record_id, state, revision, updated_at)
            VALUES ($raw_record_id, 'not_started', 1, $updated_at);
            """;
        command.Parameters.AddWithValue("$raw_record_id", rawRecordId);
        command.Parameters.AddWithValue("$updated_at", observedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static CommittedIngestionIds? FindExisting(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ingestBatchId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT raw_record_id, id
            FROM source_schema_observations
            WHERE ingest_batch_id = $ingest_batch_id;
            """;
        command.Parameters.AddWithValue("$ingest_batch_id", ingestBatchId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        if (reader.IsDBNull(0))
        {
            throw new InvalidOperationException("An ingest batch ID is already owned by an adapter-failure observation.");
        }
        return new CommittedIngestionIds(reader.GetInt64(0), reader.GetInt64(1));
    }

    private SqliteConnection OpenConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        };
        if (connectionOptions.BusyTimeoutMilliseconds is { } configuredTimeout)
        {
            connectionString.DefaultTimeout = Math.Max(1, checked((configuredTimeout + 999) / 1_000));
        }

        var connection = new SqliteConnection(connectionString.ToString());
        connection.Open();
        if (connectionOptions.BusyTimeoutMilliseconds is { } timeout)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA busy_timeout = {timeout.ToString(CultureInfo.InvariantCulture)};";
            command.ExecuteNonQuery();
        }
        return connection;
    }
}

internal enum IngestionCommitWritePhase
{
    AfterRawRecordInsert,
    AfterCatalogRegistration,
    BeforeCommit,
}
