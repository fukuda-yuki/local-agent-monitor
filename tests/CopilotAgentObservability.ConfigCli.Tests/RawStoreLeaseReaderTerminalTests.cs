using CopilotAgentObservability.ConfigCli;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class RawStoreLeaseReaderTerminalTests
{
    [Fact]
    public void ReadAll_LeaseLostAfterReaderAccess_ReturnsNoRawDerivedValue()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"raw-reader-terminal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "monitor.db");
        try
        {
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(databasePath);
            var store = new RawTelemetryStore(databasePath, context);
            store.CreateSchema();
            store.Insert(new(null, "raw-otlp", "trace", DateTimeOffset.UtcNow, null, "{}"));

            var exception = Assert.Throws<InvalidDataException>(() => RawStoreLeaseReader.ReadAll(
                databasePath,
                records =>
                {
                    var value = Assert.Single(records).PayloadJson;
                    using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "DELETE FROM retention_leases WHERE lease_kind='operation';";
                    command.ExecuteNonQuery();
                    return value;
                }));

            Assert.Equal("raw_store_unavailable", exception.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
