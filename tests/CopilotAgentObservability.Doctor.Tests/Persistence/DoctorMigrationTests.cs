using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Doctor.Tests.Persistence;

public sealed class DoctorMigrationTests
{
    private const string GenerationCommand = "dotnet run --project scripts/test/GenerateMonitorSchemaFixtures/GenerateMonitorSchemaFixtures.csproj -- --output tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/monitor";

    public static TheoryData<int, string> HistoricalSchemas => new()
    {
        { 1, "655e00243df9e07b9abb3392d6e4daf747064a77" },
        { 2, "f91e195b549fa2bbfc51b3245dd3fb19fcc8759c" },
        { 3, "9ca613a97fd0611ccff1d84b35261b7346112eab" },
        { 4, "65ec872eb541b2023f55c32d32edebb9cf83818b" },
    };

    public static TheoryData<int, string, string> HistoricalSchemasAndFailurePoints
    {
        get
        {
            var data = new TheoryData<int, string, string>();
            foreach (var pair in HistoricalSchemas)
            {
                data.Add((int)pair[0], (string)pair[1], "after-doctor-tables");
                data.Add((int)pair[0], (string)pair[1], "after-doctor-version");
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(HistoricalSchemas))]
    public void HistoricalMonitorFixture_MigratesThroughV5ThenDoctorV1AndRestartsIdempotently(int version, string sourceCommit)
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "monitor");
        var manifestPath = Path.Combine(fixtureDirectory, "manifest.json");
        var manifest = JsonSerializer.Deserialize<FixtureManifest>(File.ReadAllText(manifestPath), JsonOptions);
        Assert.NotNull(manifest);
        Assert.Equal("monitor", manifest.Component);
        Assert.Equal(GenerationCommand, manifest.GenerationCommand);
        Assert.Equal("git status --porcelain", manifest.GitStatusCommand);
        var fixture = Assert.Single(manifest.Fixtures, item => item.Version == version);
        Assert.Equal(sourceCommit, fixture.SourceCommit);
        Assert.Equal(string.Empty, fixture.GitStatusBefore);
        Assert.Equal(string.Empty, fixture.GitStatusAfter);
        Assert.Equal($"monitor-v{version}.sqlite", fixture.File);

        var fixturePath = Path.Combine(fixtureDirectory, fixture.File);
        var sourceBytes = File.ReadAllBytes(fixturePath);
        Assert.Equal(fixture.Sha256, Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant());

        using var database = new DoctorTestDatabase();
        File.Copy(fixturePath, database.Path, overwrite: true);
        AssertHistoricalSentinels(database, version, fixture.Sentinels);

        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var time = new DoctorTestTimeProvider(DoctorTestData.Now);
        var store = new SqliteDoctorVerificationStore(database.Path, time);
        Assert.Equal(DoctorResultCode.VerificationActive, store.CreateSchema().Code);
        var verification = Assert.IsType<DoctorVerification>(
            store.Start("github-copilot-vscode", "otel", TimeSpan.FromMinutes(5)).Verification);
        Assert.Equal(
            DoctorResultCode.VerificationActive,
            store.ObserveCandidate(DoctorTestData.Candidate(verification, $"fixture:monitor-v{version}")).Code);

        string firstRestartRows;
        using (var firstRestart = database.Open())
        {
            AssertMigratedState(firstRestart, fixture.Sentinels);
            firstRestartRows = SnapshotDoctorRows(firstRestart);
        }

        var reopenedStore = new SqliteDoctorVerificationStore(database.Path, time);
        Assert.Equal(DoctorResultCode.VerificationActive, reopenedStore.CreateSchema().Code);
        Assert.Equal(DoctorResultCode.VerificationActive, reopenedStore.Get(verification.VerificationId).Code);
        using (var secondRestart = database.Open())
        {
            AssertMigratedState(secondRestart, fixture.Sentinels);
            Assert.Equal(firstRestartRows, SnapshotDoctorRows(secondRestart));
        }

        Assert.Equal(sourceBytes, File.ReadAllBytes(fixturePath));
        Assert.Equal(fixture.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath))).ToLowerInvariant());
    }

    [Theory]
    [MemberData(nameof(HistoricalSchemasAndFailurePoints))]
    public void InjectedDoctorMigrationFailure_RestoresExactPostMonitorPreDoctorSchemaAndRows(
        int version,
        string sourceCommit,
        string failurePoint)
    {
        Assert.False(string.IsNullOrWhiteSpace(sourceCommit));
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "SchemaMigrations",
            "monitor",
            $"monitor-v{version}.sqlite");
        using var database = new DoctorTestDatabase();
        File.Copy(fixturePath, database.Path, overwrite: true);
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();

        string before;
        using (var connection = database.Open())
        {
            before = SnapshotDatabase(connection);
            Assert.Equal(5L, DoctorTestDatabase.Scalar(connection, "SELECT version FROM schema_version WHERE component='monitor';"));
            Assert.Null(DoctorTestDatabase.Scalar(connection, "SELECT version FROM schema_version WHERE component='doctor';"));
        }

        var store = new SqliteDoctorVerificationStore(
            database.Path,
            new DoctorTestTimeProvider(DoctorTestData.Now),
            checkpoint: point =>
            {
                if (point == failurePoint)
                {
                    throw new InvalidOperationException("injected Doctor migration failure");
                }
            });

        var result = store.CreateSchema();

        Assert.Equal(DoctorResultCode.DoctorStoreUnavailable, result.Code);
        using var reopened = database.Open();
        Assert.Equal(before, SnapshotDatabase(reopened));
        Assert.Equal(0L, DoctorTestDatabase.Scalar(reopened, "SELECT count(*) FROM sqlite_schema WHERE name LIKE 'doctor_%';"));
        Assert.Null(DoctorTestDatabase.Scalar(reopened, "SELECT version FROM schema_version WHERE component='doctor';"));
    }

    private static void AssertHistoricalSentinels(DoctorTestDatabase database, int version, FixtureSentinels sentinels)
    {
        using var connection = database.Open();
        Assert.Equal((long)version, DoctorTestDatabase.Scalar(connection, "SELECT version FROM schema_version WHERE component='monitor';"));
        Assert.Equal(sentinels.RawRecordId, DoctorTestDatabase.Scalar(connection, "SELECT id FROM raw_records WHERE id=$id;", ("$id", sentinels.RawRecordId)));
        Assert.Equal(sentinels.IngestionId, DoctorTestDatabase.Scalar(connection, "SELECT id FROM monitor_ingestions WHERE id=$id;", ("$id", sentinels.IngestionId)));
        Assert.Equal(sentinels.TraceId, DoctorTestDatabase.Scalar(connection, "SELECT trace_id FROM monitor_traces WHERE trace_id=$id;", ("$id", sentinels.TraceId)));
        if (sentinels.SpanId is not null)
        {
            Assert.Equal(sentinels.SpanId, DoctorTestDatabase.Scalar(connection, "SELECT span_id FROM monitor_spans WHERE span_id=$id;", ("$id", sentinels.SpanId)));
        }
    }

    private static void AssertMigratedState(SqliteConnection connection, FixtureSentinels sentinels)
    {
        Assert.Equal(
            ["doctor|1", "monitor|5"],
            DoctorTestDatabase.Rows(connection, "SELECT component,version FROM schema_version ORDER BY component;"));
        Assert.Equal(sentinels.RawRecordId, DoctorTestDatabase.Scalar(connection, "SELECT id FROM raw_records WHERE id=$id;", ("$id", sentinels.RawRecordId)));
        Assert.Equal(sentinels.IngestionId, DoctorTestDatabase.Scalar(connection, "SELECT id FROM monitor_ingestions WHERE id=$id;", ("$id", sentinels.IngestionId)));
        Assert.Equal(sentinels.TraceId, DoctorTestDatabase.Scalar(connection, "SELECT trace_id FROM monitor_traces WHERE trace_id=$id;", ("$id", sentinels.TraceId)));
        if (sentinels.SpanId is not null)
        {
            Assert.Equal(sentinels.SpanId, DoctorTestDatabase.Scalar(connection, "SELECT span_id FROM monitor_spans WHERE span_id=$id;", ("$id", sentinels.SpanId)));
        }
        Assert.Equal("ok", DoctorTestDatabase.Scalar(connection, "PRAGMA integrity_check;"));
        Assert.Equal(2L, DoctorTestDatabase.Scalar(connection, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN ('doctor_verifications','doctor_verification_evidence');"));
        Assert.Equal(5L, DoctorTestDatabase.Scalar(connection, "SELECT version FROM schema_version WHERE component='monitor';"));
        Assert.Equal(1L, DoctorTestDatabase.Scalar(connection, "SELECT version FROM schema_version WHERE component='doctor';"));
    }

    private static string SnapshotDoctorRows(SqliteConnection connection) => string.Join(
        '\n',
        DoctorTestDatabase.Rows(connection, "SELECT * FROM doctor_verifications ORDER BY verification_id;")
            .Concat(DoctorTestDatabase.Rows(connection, "SELECT * FROM doctor_verification_evidence ORDER BY candidate_id;")));

    private static string SnapshotDatabase(SqliteConnection connection)
    {
        var schema = DoctorTestDatabase.Rows(
            connection,
            "SELECT type,name,tbl_name,coalesce(sql,'<null>') FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name;");
        var tables = DoctorTestDatabase.Rows(
            connection,
            "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;");
        var rows = tables.SelectMany(table => new[] { $"TABLE:{table}" }.Concat(
            DoctorTestDatabase.Rows(connection, $"SELECT * FROM \"{table.Replace("\"", "\"\"")}\" ORDER BY rowid;")));
        return string.Join('\n', schema.Concat(rows));
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record FixtureManifest(
        string Component,
        string GenerationCommand,
        string GitStatusCommand,
        IReadOnlyList<FixtureEntry> Fixtures);

    private sealed record FixtureEntry(
        int Version,
        string File,
        string SourceCommit,
        string Sha256,
        string GitStatusBefore,
        string GitStatusAfter,
        FixtureSentinels Sentinels);

    private sealed record FixtureSentinels(
        long RawRecordId,
        long IngestionId,
        long TraceRowId,
        string TraceId,
        long? SpanRowId,
        string? SpanId);
}
