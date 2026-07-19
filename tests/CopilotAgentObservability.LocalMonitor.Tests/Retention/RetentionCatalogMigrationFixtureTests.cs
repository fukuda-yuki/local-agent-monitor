using System.Security.Cryptography;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionCatalogMigrationFixtureTests
{
    private const string RetentionGenerationCommand = "dotnet run --project scripts/test/GenerateRetentionSchemaFixtures/GenerateRetentionSchemaFixtures.csproj -- --output tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/retention";

    [Fact]
    public void Committed_fixture_manifests_enumerate_every_reviewed_source_and_the_retention_catalog_v1_fixture()
    {
        var schemaRoot = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations");
        var session = ReadManifest(Path.Combine(schemaRoot, "session", "manifest.json"));
        var monitor = ReadManifest(Path.Combine(schemaRoot, "monitor", "manifest.json"));
        var retention = ReadManifest(Path.Combine(schemaRoot, "retention", "manifest.json"));

        Assert.Equal(13, session.Fixtures.Count);
        Assert.Equal(5, monitor.Fixtures.Count);
        var fixture = Assert.Single(retention.Fixtures);
        Assert.Equal("retention", retention.Component);
        Assert.Equal(RetentionGenerationCommand, retention.GenerationCommand);
        Assert.Equal("retention-catalog-v1.sqlite", fixture.File);
        Assert.True(fixture.ByteLength > 0);
        Assert.Equal("ok", fixture.IntegrityCheck);
        Assert.Equal("00000000000000000000000000000089", fixture.Sentinels.StoreInstanceId);
        Assert.Equal(1, fixture.Sentinels.CatalogSchemaVersion);
        Assert.Equal(0, fixture.Sentinels.ItemCount);
        using (var fixtureConnection = Open(Path.Combine(schemaRoot, "retention", fixture.File)))
            Assert.Equal(fixture.IntegrityCheck, Scalar<string>(fixtureConnection, "PRAGMA integrity_check;"));

        var identities = session.Fixtures.Select(entry => $"session/{entry.File}")
            .Concat(monitor.Fixtures.Select(entry => $"monitor/{entry.File}"))
            .Append($"retention/{fixture.File}")
            .ToArray();
        Assert.Equal(19, identities.Length);
        Assert.Equal(19, identities.Distinct(StringComparer.Ordinal).Count());

        foreach (var identity in identities)
        {
            var path = Path.Combine(schemaRoot, identity.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Missing committed retention migration fixture: {identity}");
            var manifestEntry = identity.StartsWith("session/", StringComparison.Ordinal)
                ? session.Fixtures.Single(entry => identity.EndsWith(entry.File, StringComparison.Ordinal))
                : identity.StartsWith("monitor/", StringComparison.Ordinal)
                    ? monitor.Fixtures.Single(entry => identity.EndsWith(entry.File, StringComparison.Ordinal))
                    : fixture;
            Assert.Equal(manifestEntry.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
            if (ReferenceEquals(manifestEntry, fixture)) Assert.Equal(manifestEntry.ByteLength, new FileInfo(path).Length);
        }
    }

    [Fact]
    public void Every_reviewed_fixture_migrates_through_two_fresh_catalog_stores_without_changing_authoritative_source_or_catalog_identity()
    {
        var schemaRoot = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations");
        foreach (var source in ReviewedFixtures(schemaRoot))
        {
            var copy = Path.Combine(Path.GetTempPath(), $"retention-fixture-{Guid.NewGuid():N}.sqlite");
            File.Copy(source.Path, copy);
            try
            {
                var sourceHash = Hash(copy);
                var expectedCatalog = ReadExpectedCatalogRows(copy);
                var sourceRows = ReadAuthoritativeSourceRows(copy);

                var firstSnapshot = InitializeThroughProductionMonitorStore(copy, sourceRows, expectedCatalog);
                Assert.Equal(sourceHash, Hash(source.Path));

                SqliteConnection.ClearAllPools();

                var secondSnapshot = InitializeThroughProductionMonitorStore(copy, sourceRows, expectedCatalog);
                Assert.Equal(firstSnapshot.StoreInstanceId, secondSnapshot.StoreInstanceId);
                Assert.Equal(firstSnapshot.Items, secondSnapshot.Items);
                Assert.Equal(sourceHash, Hash(source.Path));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                DeleteDatabaseFiles(copy);
            }
        }
    }

    [Fact]
    public void Invalid_legacy_authority_blocks_entire_retention_migration_without_fallback_or_partial_catalog()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "monitor", "monitor-v5.sqlite");
        var copy = Path.Combine(Path.GetTempPath(), $"retention-fixture-invalid-{Guid.NewGuid():N}.sqlite");
        File.Copy(source, copy);
        try
        {
            Execute(copy, "UPDATE raw_records SET received_at='invalid' WHERE id=(SELECT MIN(id) FROM raw_records);");
            var preMigrationHash = Hash(copy);

            var error = Assert.Throws<RetentionMigrationBlockedException>(() => new RetentionCatalogStore(copy).CreateSchema());

            Assert.Equal("retention_migration_blocked", error.Message);
            Assert.Equal(preMigrationHash, Hash(copy));
            Assert.False(TableExists(copy, "retention_items"));
            Assert.Equal("invalid", Scalar<string>(copy, "SELECT received_at FROM raw_records WHERE id=(SELECT MIN(id) FROM raw_records);"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(copy);
        }
    }

    [Fact]
    public void Newer_catalog_schema_refuses_without_mutation()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "retention", "retention-catalog-v1.sqlite");
        var copy = Path.Combine(Path.GetTempPath(), $"retention-fixture-newer-{Guid.NewGuid():N}.sqlite");
        File.Copy(source, copy);
        try
        {
            Execute(copy, "ALTER TABLE retention_component_versions RENAME TO retention_component_versions_v1; CREATE TABLE retention_component_versions(component TEXT PRIMARY KEY, version INTEGER NOT NULL); INSERT INTO retention_component_versions SELECT component,2 FROM retention_component_versions_v1; DROP TABLE retention_component_versions_v1;");
            var preMigrationHash = Hash(copy);

            var error = Assert.Throws<RetentionMigrationBlockedException>(() => new RetentionCatalogStore(copy).CreateSchema());

            Assert.Equal("retention_migration_blocked", error.Message);
            Assert.Equal(preMigrationHash, Hash(copy));
            Assert.Equal(2L, Scalar<long>(copy, "SELECT version FROM retention_component_versions WHERE component='retention';"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(copy);
        }
    }

    [Fact]
    public async Task Changed_store_instance_adoption_cannot_grant_prior_owned_source_without_mutation()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "monitor", "monitor-v5.sqlite");
        var copy = Path.Combine(Path.GetTempPath(), $"retention-fixture-adoption-{Guid.NewGuid():N}.sqlite");
        File.Copy(source, copy);
        try
        {
            var now = DateTimeOffset.Parse(Scalar<string>(copy, "SELECT received_at FROM raw_records ORDER BY id LIMIT 1;"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
            var originalContext = RetentionCatalogContext.InitializeNewOwnedDatabase(copy, new MutableTimeProvider(now));
            var rawId = Scalar<long>(copy, "SELECT id FROM raw_records ORDER BY id LIMIT 1;");
            Execute(copy, "PRAGMA foreign_keys=OFF; UPDATE retention_store_instances SET store_instance_id='0000000000000000000000000000008a' WHERE id=1;");
            var preAdoptionHash = Hash(copy);

            var adoptedContext = RetentionCatalogContext.AdoptExistingCatalogV1(copy);
            Assert.NotEqual(originalContext.StoreInstanceId, adoptedContext.StoreInstanceId);
            var adoptedStore = new RawTelemetryStore(copy, adoptedContext, new MutableTimeProvider(now));
            var result = await adoptedStore.GetRawRecordByIdAsync(rawId, RetentionReadKind.Access, CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.NotFound, result.Disposition);
            Assert.Null(result.Lease);
            Assert.Equal(preAdoptionHash, Hash(copy));
            Assert.Equal("0000000000000000000000000000008a", Scalar<string>(copy, "SELECT store_instance_id FROM retention_store_instances WHERE id=1;"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(copy);
        }
    }

    private static FixtureManifest ReadManifest(string path) =>
        JsonSerializer.Deserialize<FixtureManifest>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException($"Fixture manifest could not be read: {path}");

    private static IEnumerable<FixtureSource> ReviewedFixtures(string schemaRoot)
    {
        foreach (var component in new[] { "session", "monitor", "retention" })
        {
            var directory = Path.Combine(schemaRoot, component);
            foreach (var fixture in ReadManifest(Path.Combine(directory, "manifest.json")).Fixtures)
                yield return new FixtureSource($"{component}/{fixture.File}", Path.Combine(directory, fixture.File));
        }
    }

    private static CatalogSnapshot InitializeThroughProductionMonitorStore(string path, IReadOnlyList<string> sourceRows, IReadOnlyList<ExpectedCatalogRow> expectedCatalog)
    {
        var context = File.Exists(path) && TableExists(path, "retention_component_versions")
            ? RetentionCatalogContext.AdoptExistingCatalogV1(path)
            : RetentionCatalogContext.InitializeNewOwnedDatabase(path);
        var store = new RawTelemetryStore(path, context);
        store.CreateMonitorSchema();
        return AssertMigrated(path, sourceRows, expectedCatalog);
    }

    private static CatalogSnapshot AssertMigrated(string path, IReadOnlyList<string> sourceRows, IReadOnlyList<ExpectedCatalogRow> expectedCatalog)
    {
        using var connection = Open(path);
        Assert.Equal("ok", Scalar<string>(connection, "PRAGMA integrity_check;"));
        Assert.Equal(sourceRows, ReadAuthoritativeSourceRows(connection));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT version FROM retention_component_versions WHERE component='retention';"));

        var storeInstanceId = Scalar<string>(connection, "SELECT store_instance_id FROM retention_store_instances WHERE id=1;");
        var items = ReadRows(connection, "SELECT item_id || '|' || store_instance_id || '|' || store_kind || '|' || source_item_id || '|' || captured_at || '|' || expires_at || '|' || policy_id || '|' || revision FROM retention_items ORDER BY store_kind, source_item_id;");
        var expected = expectedCatalog.Select(row => $"{row.StoreKind}|{row.SourceItemId}|{row.CapturedAt}|{row.ExpiresAt}|raw-default-90d|1").ToArray();
        var actual = ReadRows(connection, "SELECT store_kind || '|' || source_item_id || '|' || captured_at || '|' || expires_at || '|' || policy_id || '|' || revision FROM retention_items ORDER BY store_kind, source_item_id;");
        Assert.Equal(expected, actual);
        Assert.Equal(items.Count, items.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM retention_items WHERE store_instance_id <> $store;", ("$store", storeInstanceId)));
        return new CatalogSnapshot(storeInstanceId, items);
    }

    private static IReadOnlyList<string> ReadAuthoritativeSourceRows(string path)
    {
        using var connection = Open(path);
        return ReadAuthoritativeSourceRows(connection);
    }

    private static IReadOnlyList<ExpectedCatalogRow> ReadExpectedCatalogRows(string path)
    {
        using var connection = Open(path);
        var rows = new List<ExpectedCatalogRow>();
        if (TableExists(connection, "raw_records"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,received_at FROM raw_records ORDER BY id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var capturedAt = DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
                rows.Add(new("raw_record", reader.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture), capturedAt.ToString("O"), capturedAt.AddDays(90).ToString("O")));
            }
        }
        if (TableExists(connection, "session_event_content"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT event_id,captured_at,expires_at FROM session_event_content ORDER BY event_id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var capturedAt = DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime().ToString("O");
                var expiresAt = DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime().ToString("O");
                rows.Add(new("session_event_content", reader.GetString(0), capturedAt, expiresAt));
            }
        }
        return rows.OrderBy(row => row.StoreKind, StringComparer.Ordinal).ThenBy(row => row.SourceItemId, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> ReadAuthoritativeSourceRows(SqliteConnection connection)
    {
        var rows = new List<string>();
        if (TableExists(connection, "raw_records"))
            rows.AddRange(ReadRows(connection, "SELECT 'raw|' || id || '|' || received_at || '|' || schema_version FROM raw_records ORDER BY id;"));
        if (TableExists(connection, "session_event_content"))
            rows.AddRange(ReadRows(connection, "SELECT 'session|' || event_id || '|' || captured_at || '|' || expires_at FROM session_event_content ORDER BY event_id;"));
        if (TableExists(connection, "monitor_analysis_runs"))
            rows.AddRange(ReadRows(connection, "SELECT 'analysis|' || id || '|' || requested_at FROM monitor_analysis_runs WHERE result_markdown IS NOT NULL OR error_message IS NOT NULL ORDER BY id;"));
        return rows;
    }

    private static IReadOnlyList<string> ReadRows(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static SqliteConnection Open(string path) { var connection = new SqliteConnection($"Data Source={path};Pooling=False"); connection.Open(); return connection; }
    private static bool TableExists(string path, string table) { using var connection = Open(path); return TableExists(connection, table); }
    private static bool TableExists(SqliteConnection connection, string table) { using var command = connection.CreateCommand(); command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name);"; command.Parameters.AddWithValue("$name", table); return Convert.ToInt64(command.ExecuteScalar()) == 1; }
    private static T Scalar<T>(string path, string sql) { using var connection = Open(path); return Scalar<T>(connection, sql); }
    private static T Scalar<T>(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters) { using var command = connection.CreateCommand(); command.CommandText = sql; foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value); return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T)); }
    private static void Execute(string path, string sql) { using var connection = Open(path); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static void DeleteDatabaseFiles(string path) { foreach (var candidate in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(candidate)) File.Delete(candidate); }

    private sealed record FixtureManifest(string Component, string GenerationCommand, IReadOnlyList<FixtureEntry> Fixtures);
    private sealed record FixtureEntry(string File, string Sha256, long ByteLength, string IntegrityCheck, FixtureSentinels Sentinels);
    private sealed record FixtureSentinels(string StoreInstanceId, int CatalogSchemaVersion, int ItemCount);
    private sealed record FixtureSource(string Identity, string Path);
    private sealed record CatalogSnapshot(string StoreInstanceId, IReadOnlyList<string> Items);
    private sealed record ExpectedCatalogRow(string StoreKind, string SourceItemId, string CapturedAt, string ExpiresAt);
}
