using System.Text;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Alerts.Tests;

public sealed class SqliteAlertLifecycleStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "alert-lifecycle-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));
    private string DatabasePath => Path.Combine(directory, "monitor.sqlite");
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString();

    public SqliteAlertLifecycleStoreTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void Initialize_FreshDatabase_CreatesSeparateV1ComponentAndAppendOnlyTable()
    {
        var result = Store().Initialize();

        Assert.Equal(AlertLifecycleStoreStatus.Success, result.Status);
        using var connection = Open();
        Assert.Equal(1L, Scalar<long>(connection, "SELECT version FROM schema_version WHERE component='alert_lifecycle';"));
        Assert.Equal(["alert_lifecycle_events"], Strings(connection, "SELECT name FROM sqlite_schema WHERE type='table' AND name LIKE 'alert_lifecycle_%' ORDER BY name;"));
        Assert.Equal(2L, Scalar<long>(connection, "SELECT count(*) FROM sqlite_schema WHERE type='trigger' AND tbl_name='alert_lifecycle_events';"));
    }

    [Fact]
    public void Initialize_AcceptedEngineV1Upgrade_PreservesReceiptBytesAndAllComponentVersions()
    {
        SeedReceipts();
        var before = ReceiptJson(AlertA);

        var result = Store().Initialize();

        Assert.Equal(AlertLifecycleStoreStatus.Success, result.Status);
        Assert.Equal(before, ReceiptJson(AlertA));
        using var connection = Open();
        Assert.Equal(["alert_engine:1", "alert_lifecycle:1"], Strings(connection, "SELECT component || ':' || version FROM schema_version WHERE component LIKE 'alert_%' ORDER BY component;"));
    }

    [Fact]
    public void Get_ReceiptWithoutEvents_IsLazyOpenRevisionZeroWithoutWriting()
    {
        SeedReceipts();
        var store = Store();
        store.Initialize();

        var result = store.Get(AlertA);

        Assert.Equal(AlertLifecycleStoreStatus.Success, result.Status);
        Assert.Equal(AlertLifecycleState.Open, result.Lifecycle!.State);
        Assert.Equal(0, result.Lifecycle.Revision);
        using var connection = Open();
        Assert.Equal(0L, Scalar<long>(connection, "SELECT count(*) FROM alert_lifecycle_events;"));
    }

    [Fact]
    public void UnchangedReevaluation_ReusesReceiptIdentityAndCreatesNoLifecycleEvent()
    {
        SeedReceipts();
        var engine = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertStoreStatus.Success, engine.Append(Evaluation()).Status);
        var store = Store();
        store.Initialize();

        var current = store.Get(AlertA);

        Assert.Equal(AlertLifecycleState.Open, current.Lifecycle!.State);
        Assert.Equal(0, current.Lifecycle.Revision);
        Assert.Empty(store.History(AlertA).Events);
    }

    [Fact]
    public void Mutate_AppendsEventsDerivesCurrentAndNeverRewritesReceipt()
    {
        SeedReceipts();
        var store = Store();
        store.Initialize();
        var receipt = ReceiptJson(AlertA);

        var acknowledged = store.Mutate(Command(AlertA, AlertLifecycleAction.Acknowledge, 0, 'a'));
        time.Advance(TimeSpan.FromMinutes(1));
        var resolved = store.Mutate(Command(AlertA, AlertLifecycleAction.Resolve, 1, 'b'));

        Assert.Equal(AlertLifecycleState.Acknowledged, acknowledged.Lifecycle!.State);
        Assert.Equal(1, acknowledged.Lifecycle.Revision);
        Assert.Equal(AlertLifecycleState.Resolved, resolved.Lifecycle!.State);
        Assert.Equal(2, resolved.Lifecycle.Revision);
        Assert.Equal([2L, 1L], store.History(AlertA, 100).Events.Select(item => item.Revision));
        Assert.Equal(receipt, ReceiptJson(AlertA));
    }

    [Fact]
    public void Mutate_InvalidTransitionAndStaleRevisionAppendNothing()
    {
        SeedReceipts();
        var store = Store();
        store.Initialize();
        Assert.Equal(AlertLifecycleStoreStatus.Success, store.Mutate(Command(AlertA, AlertLifecycleAction.Dismiss, 0, 'a')).Status);

        var invalid = store.Mutate(Command(AlertA, AlertLifecycleAction.Resolve, 1, 'b'));
        var stale = store.Mutate(Command(AlertA, AlertLifecycleAction.Reopen, 0, 'c'));

        Assert.Equal("alert_invalid_transition", invalid.Code);
        Assert.Equal("alert_revision_conflict", stale.Code);
        Assert.Single(store.History(AlertA).Events);
    }

    [Fact]
    public void Mutate_ExactIdempotencyReplayReturnsPriorResultButMismatchConflicts()
    {
        SeedReceipts();
        var store = Store();
        store.Initialize();
        var command = Command(AlertA, AlertLifecycleAction.Acknowledge, 0, 'a');

        var first = store.Mutate(command);
        var replay = store.Mutate(command);
        var mismatch = store.Mutate(command with { ReasonCode = "different" });

        Assert.Equal(first.Event, replay.Event);
        Assert.True(replay.Replayed);
        Assert.Equal("alert_idempotency_conflict", mismatch.Code);
        Assert.Single(store.History(AlertA).Events);
    }

    [Fact]
    public async Task Mutate_ConcurrentExpectedRevision_AllowsOneWinner()
    {
        SeedReceipts();
        var store = Store();
        store.Initialize();
        using var gate = new ManualResetEventSlim();

        var tasks = new[] { 'a', 'b' }.Select(suffix => Task.Run(() =>
        {
            gate.Wait();
            return store.Mutate(Command(AlertA, AlertLifecycleAction.Acknowledge, 0, suffix));
        })).ToArray();
        gate.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results, result => result.Status == AlertLifecycleStoreStatus.Success);
        Assert.Single(results, result => result.Code == "alert_revision_conflict");
        Assert.Single(store.History(AlertA).Events);
    }

    [Fact]
    public void Supersede_UsesOnlyExplicitOldAndNewIdsAndDoesNotInheritDismissal()
    {
        SeedReceipts();
        var store = Store();
        store.Initialize();
        store.Mutate(Command(AlertA, AlertLifecycleAction.Dismiss, 0, 'a'));

        var superseded = store.Supersede(Command(AlertA, AlertLifecycleAction.Supersede, 1, 'b') with { OldAlertId = AlertA, NewAlertId = AlertB });

        Assert.Equal(AlertLifecycleState.Superseded, superseded.Lifecycle!.State);
        Assert.Equal(AlertLifecycleState.Open, store.Get(AlertB).Lifecycle!.State);
        Assert.Equal(0, store.Get(AlertB).Lifecycle!.Revision);
        Assert.Equal(AlertB, superseded.Event!.NewAlertId);
    }

    [Fact]
    public void SourceDeleted_ExplicitCallbackPreservesStateAndSanitizedReceipt()
    {
        SeedReceipts();
        var store = Store();
        store.Initialize();
        store.Mutate(Command(AlertA, AlertLifecycleAction.Acknowledge, 0, 'a'));
        var receipt = ReceiptJson(AlertA);

        var deleted = store.SourceDeleted(Command(AlertA, AlertLifecycleAction.SourceDeleted, 1, 'b'));

        Assert.Equal(AlertLifecycleState.Acknowledged, deleted.Lifecycle!.State);
        Assert.Equal(2, deleted.Lifecycle.Revision);
        Assert.Equal(receipt, ReceiptJson(AlertA));
    }

    [Fact]
    public void History_IsRevisionDescendingAndBounded()
    {
        SeedReceipts();
        var store = Store();
        store.Initialize();
        store.Mutate(Command(AlertA, AlertLifecycleAction.Dismiss, 0, 'a'));
        store.Mutate(Command(AlertA, AlertLifecycleAction.Reopen, 1, 'b'));
        store.Mutate(Command(AlertA, AlertLifecycleAction.Acknowledge, 2, 'c'));

        Assert.Equal([3L, 2L], store.History(AlertA, 2).Events.Select(item => item.Revision));
        Assert.Equal("alert_invalid_limit", store.History(AlertA, 101).Code);
    }

    [Fact]
    public void AppendOnlyTriggers_RejectUpdateAndDelete()
    {
        SeedReceipts();
        var store = Store();
        store.Initialize();
        store.Mutate(Command(AlertA, AlertLifecycleAction.Acknowledge, 0, 'a'));
        using var connection = Open();

        Assert.Throws<SqliteException>(() => Execute(connection, "UPDATE alert_lifecycle_events SET reason_code='changed';"));
        Assert.Throws<SqliteException>(() => Execute(connection, "DELETE FROM alert_lifecycle_events;"));
    }

    [Fact]
    public void Initialize_NewerOrBrokenLifecycleSchema_FailsClosedWithoutRepair()
    {
        using (var connection = Open()) Execute(connection, "CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL); INSERT INTO schema_version VALUES('alert_lifecycle',2); CREATE TABLE kept(value TEXT); INSERT INTO kept VALUES('same');");

        var result = Store().Initialize();

        Assert.Equal(AlertLifecycleStoreStatus.Unavailable, result.Status);
        Assert.Equal("alert_lifecycle_store_unavailable", result.Code);
        using var check = Open();
        Assert.Equal("same", Scalar<string>(check, "SELECT value FROM kept;"));
        Assert.Equal(2L, Scalar<long>(check, "SELECT version FROM schema_version WHERE component='alert_lifecycle';"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    private const string AlertA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AlertB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private SqliteAlertLifecycleStore Store() => new(ConnectionString, time);

    private AlertLifecycleMutation Command(string alertId, AlertLifecycleAction action, long revision, char suffix) =>
        new(alertId, action, revision, "user_reviewed", "reviewed locally", "aid1_" + new string(suffix, 43));

    private void SeedReceipts()
    {
        var engine = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertStoreStatus.Success, engine.Initialize().Status);
        Assert.Equal(AlertStoreStatus.Success, engine.Append(Evaluation()).Status);
    }

    private string ReceiptJson(string alertId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT canonical_json FROM alert_receipts WHERE alert_id=$id;";
        command.Parameters.AddWithValue("$id", alertId);
        return (string)command.ExecuteScalar()!;
    }

    private AlertEvaluationResult Evaluation()
    {
        var observed = time.GetUtcNow();
        var evidence = new AlertEvidenceReference(AlertEvidenceKind.Event, "evidence-1", "session-1", "trace-1", "span-1", null, "event-1", null, observed);
        AlertReceipt Receipt(string id, string rule, string version) => new(
            AlertContractVersions.Receipt, AlertContractVersions.SanitizedReceiptProfile, id, new string('e', 64), rule, version,
            AlertSeverity.Warning, AlertInitialState.Open, "github-copilot", "1.2.3", "session-1", "trace-1", [evidence],
            [new("count", "calls", 2)], [new("count.warning", "calls", 1)], "fixture-v1", new string('c', 64), ["tool-events"],
            AlertCompleteness.Partial, ["ingest_gap"], observed, observed, new string('d', 64), "Fixture summary");
        return new(AlertContractVersions.Evaluation, new string('e', 64), new string('d', 64), "fixture-v1", new string('c', 64),
            [Receipt(AlertA, "fixture-rule", "1"), Receipt(AlertB, "fixture-rule", "2")], [], []);
    }

    private SqliteConnection Open() { var connection = new SqliteConnection(ConnectionString); connection.Open(); return connection; }
    private static void Execute(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static T Scalar<T>(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture); }
    private static string[] Strings(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; using var reader = command.ExecuteReader(); var values = new List<string>(); while (reader.Read()) values.Add(reader.GetString(0)); return values.ToArray(); }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
}
