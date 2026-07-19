using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed class RetentionCatalogContext
{
    private RetentionCatalogContext(string databasePath, string storeInstanceId)
    {
        DatabasePath = databasePath;
        StoreInstanceId = storeInstanceId;
    }

    internal string DatabasePath { get; }
    internal string StoreInstanceId { get; }
    internal int ComponentVersion => RetentionV1Constants.CatalogSchemaVersion;
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    public static RetentionCatalogContext InitializeNewOwnedDatabase(string databasePath, TimeProvider? timeProvider = null)
    {
        var store = new RetentionCatalogStore(databasePath, timeProvider);
        store.CreateSchema();
        return AdoptExistingCatalogV1(databasePath);
    }

    public static RetentionCatalogContext AdoptExistingCatalogV1(string databasePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath)) throw new RetentionCatalogUnavailableException();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT (SELECT version FROM retention_component_versions WHERE component='retention'), (SELECT store_instance_id FROM retention_store_instances WHERE id=1);";
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0) || reader.GetInt64(0) != RetentionV1Constants.CatalogSchemaVersion || reader.IsDBNull(1) || !IsStoreId(reader.GetString(1))) throw new RetentionCatalogUnavailableException();
            return new RetentionCatalogContext(Path.GetFullPath(databasePath), reader.GetString(1));
        }
        catch (RetentionCatalogUnavailableException) { throw; }
        catch (SqliteException) { throw new RetentionCatalogUnavailableException(); }
        catch (IOException) { throw new RetentionCatalogUnavailableException(); }
    }

    private static bool IsStoreId(string value) => value.Length == 32 && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
