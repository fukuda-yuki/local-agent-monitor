using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Alerts.Tests;

public sealed class SqliteAlertEngineStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "alert-store-tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "monitor.sqlite");
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString();

    public SqliteAlertEngineStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Initialize_FreshDatabase_CreatesOnlyVersionedAlertComponent()
    {
        var result = new SqliteAlertEngineStore(ConnectionString).Initialize();

        Assert.Equal(AlertStoreStatus.Success, result.Status);
        using var connection = Open();
        Assert.Equal(1L, Scalar<long>(connection, "SELECT version FROM schema_version WHERE component='alert_engine';"));
        Assert.Equal(
            ["alert_evaluations", "alert_receipts", "alert_suppressions"],
            Strings(connection, "SELECT name FROM sqlite_schema WHERE type='table' AND name LIKE 'alert_%' ORDER BY name;"));
    }

    [Fact]
    public void Initialize_ExistingDatabase_PreservesOtherComponentsAndRows()
    {
        using (var connection = Open())
        {
            Execute(connection, "CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL); INSERT INTO schema_version VALUES('monitor',7); CREATE TABLE kept(value TEXT NOT NULL); INSERT INTO kept VALUES('unchanged');");
        }

        var result = new SqliteAlertEngineStore(ConnectionString).Initialize();

        Assert.Equal(AlertStoreStatus.Success, result.Status);
        using var check = Open();
        Assert.Equal(7L, Scalar<long>(check, "SELECT version FROM schema_version WHERE component='monitor';"));
        Assert.Equal("unchanged", Scalar<string>(check, "SELECT value FROM kept;"));
    }

    [Fact]
    public void Append_IdempotentlyStoresAndReadsExactCanonicalBytes()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertStoreStatus.Success, store.Initialize().Status);
        var evaluation = Evaluation();

        var first = store.Append(evaluation);
        var repeated = store.Append(evaluation);

        Assert.Equal(AlertStoreStatus.Success, first.Status);
        Assert.Equal(AlertStoreStatus.Success, repeated.Status);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(AlertCanonicalJson.SerializeEvaluation(evaluation)), store.GetEvaluation(evaluation.EvaluationId).CanonicalJson);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(AlertCanonicalJson.SerializeReceipt(evaluation.Receipts[0])), store.GetReceipt(evaluation.Receipts[0].AlertId).CanonicalJson);
        Assert.Equal([System.Text.Encoding.UTF8.GetString(AlertCanonicalJson.SerializeSuppression(evaluation.Suppressions[0]))], store.ListSuppressions(evaluation.EvaluationId).CanonicalJsonItems);
    }

    [Fact]
    public void Append_SameIdentityWithDifferentCanonicalBytes_ReturnsConflictWithoutMutation()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        store.Initialize();
        var evaluation = Evaluation();
        store.Append(evaluation);
        var conflicting = evaluation with { ConfigurationHash = new string('f', 64) };

        var result = store.Append(conflicting);

        Assert.Equal(AlertStoreStatus.Conflict, result.Status);
        Assert.Equal("alert_store_conflict", result.Code);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(AlertCanonicalJson.SerializeEvaluation(evaluation)), store.GetEvaluation(evaluation.EvaluationId).CanonicalJson);
    }

    [Fact]
    public void Initialize_NewerOrBrokenAlertSchema_FailsClosedWithoutRepair()
    {
        using (var connection = Open())
        {
            Execute(connection, "CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL); INSERT INTO schema_version VALUES('alert_engine',2);");
        }

        var result = new SqliteAlertEngineStore(ConnectionString).Initialize();

        Assert.Equal(AlertStoreStatus.Unavailable, result.Status);
        Assert.Equal("alert_store_unavailable", result.Code);
        using var check = Open();
        Assert.Empty(Strings(check, "SELECT name FROM sqlite_schema WHERE type='table' AND name LIKE 'alert_%';"));
        Assert.Equal(2L, Scalar<long>(check, "SELECT version FROM schema_version WHERE component='alert_engine';"));
    }

    [Fact]
    public void Initialize_PartialAlertSchema_RollsBackWithoutAddingTablesOrVersion()
    {
        using (var connection = Open())
        {
            Execute(connection, "CREATE TABLE alert_evaluations(wrong TEXT);");
        }

        var result = new SqliteAlertEngineStore(ConnectionString).Initialize();

        Assert.Equal(AlertStoreStatus.Unavailable, result.Status);
        using var check = Open();
        Assert.Equal(["alert_evaluations"], Strings(check, "SELECT name FROM sqlite_schema WHERE type='table' AND name LIKE 'alert_%' ORDER BY name;"));
        Assert.Equal(0L, Scalar<long>(check, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='schema_version';"));
    }

    [Fact]
    public void Append_ChildIdentityConflict_RollsBackEvaluationAtomically()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        store.Initialize();
        var first = Evaluation();
        Assert.Equal(AlertStoreStatus.Success, store.Append(first).Status);
        var secondId = new string('d', 64);
        var second = first with
        {
            EvaluationId = secondId,
            Receipts = [first.Receipts[0] with { EvaluationId = secondId }],
            Suppressions = [first.Suppressions[0] with { EvaluationId = secondId }],
        };

        var result = store.Append(second);

        Assert.Equal(AlertStoreStatus.Conflict, result.Status);
        Assert.Equal(AlertStoreStatus.NotFound, store.GetEvaluation(secondId).Status);
        Assert.Equal(AlertStoreStatus.Success, store.GetEvaluation(first.EvaluationId).Status);
    }

    [Fact]
    public void Reads_DistinguishNotFoundFromUnavailable()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        store.Initialize();

        var missing = store.GetReceipt(new string('f', 64));

        Assert.Equal(AlertStoreStatus.NotFound, missing.Status);
        Assert.Equal("alert_not_found", missing.Code);
        using (var connection = Open()) Execute(connection, "ALTER TABLE alert_receipts ADD COLUMN broken TEXT;");
        var unavailable = store.GetReceipt(new string('f', 64));
        Assert.Equal(AlertStoreStatus.Unavailable, unavailable.Status);
        Assert.Equal("alert_store_unavailable", unavailable.Code);
    }

    [Fact]
    public void Initialize_LockedDatabase_ReturnsBoundedBusyResult()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        store.Initialize();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        var result = store.Initialize();

        Assert.Equal(AlertStoreStatus.Busy, result.Status);
        Assert.Equal("alert_store_busy", result.Code);
    }

    [Fact]
    public void Initialize_UnrelatedVersionedAlertLifecycleTables_CoexistWithEngineComponent()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertStoreStatus.Success, store.Initialize().Status);
        using (var connection = Open())
        {
            Execute(connection, "INSERT INTO schema_version(component,version) VALUES('alert_lifecycle',1); CREATE TABLE alert_state_events(event_id TEXT PRIMARY KEY,alert_id TEXT NOT NULL,state TEXT NOT NULL);");
        }

        var result = store.Initialize();

        Assert.Equal(AlertStoreStatus.Success, result.Status);
        using var check = Open();
        Assert.Equal(1L, Scalar<long>(check, "SELECT version FROM schema_version WHERE component='alert_lifecycle';"));
        Assert.Equal(1L, Scalar<long>(check, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='alert_state_events';"));
    }

    [Fact]
    public void Append_DeclaredRuleLevelSuppression_PersistsCanonicalCode()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        store.Initialize();
        var evaluation = Evaluation();
        evaluation = evaluation with
        {
            Suppressions = [evaluation.Suppressions[0] with { Code = "minimum_sample_unmet", MissingCapabilities = [] }],
        };

        var result = store.Append(evaluation);

        Assert.Equal(AlertStoreStatus.Success, result.Status);
        Assert.Contains("minimum_sample_unmet", Assert.Single(store.ListSuppressions(evaluation.EvaluationId).CanonicalJsonItems), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string[] Strings(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static AlertEvaluationResult Evaluation()
    {
        var observed = new DateTimeOffset(2026, 7, 21, 1, 2, 3, TimeSpan.Zero);
        var evidence = new AlertEvidenceReference(AlertEvidenceKind.Event, "evidence-1", "session-1", "trace-1", "span-1", null, "event-1", null, observed);
        var receipt = new AlertReceipt(
            AlertContractVersions.Receipt, AlertContractVersions.SanitizedReceiptProfile, new string('a', 64), new string('e', 64),
            "fixture-rule", "1", AlertSeverity.Warning, AlertInitialState.Open, "github-copilot", "1.2.3", "session-1", "trace-1",
            [evidence], [new("count", "calls", 2)], [new("count.warning", "calls", 1)], "fixture-v1", new string('c', 64), ["tool-events"],
            AlertCompleteness.Partial, ["ingest_gap"], observed, observed.AddSeconds(1), new string('b', 64), "Fixture summary");
        var suppression = new AlertSuppression(new string('e', 64), "suppressed-rule", "1", "missing_required_capability", ["token-usage"]);
        return new(AlertContractVersions.Evaluation, new string('e', 64), new string('b', 64), "fixture-v1", new string('c', 64), [receipt], [suppression], []);
    }
}
