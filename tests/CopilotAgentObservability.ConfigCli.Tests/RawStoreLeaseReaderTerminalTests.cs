using CopilotAgentObservability.ConfigCli;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;
using System.Reflection;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class RawStoreLeaseReaderTerminalTests
{
    [Fact]
    public void RawStoreLeaseReader_ExposesOnlyFixedOwnerMappers()
    {
        var methods = typeof(RawStoreLeaseReader).GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

        Assert.NotEmpty(methods);
        Assert.All(methods, method => Assert.False(method.IsGenericMethod));
        Assert.All(methods, method => Assert.DoesNotContain(
            method.GetParameters(),
            parameter => parameter.ParameterType.IsGenericType
                && parameter.ParameterType.GetGenericTypeDefinition() == typeof(Func<,>)
                && parameter.ParameterType.GenericTypeArguments[0]
                    == typeof(IReadOnlyList<RawTelemetryRecord>)));
    }

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
            store.Insert(new(null, "raw-otlp", "trace", DateTimeOffset.UtcNow, null, "{\"resourceSpans\":[]}"));

            var exception = Assert.Throws<InvalidDataException>(() => RawStoreLeaseReader.ReadDashboardOperations(
                databasePath,
                () =>
                {
                    using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "DELETE FROM retention_leases WHERE lease_kind='operation';";
                    command.ExecuteNonQuery();
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
