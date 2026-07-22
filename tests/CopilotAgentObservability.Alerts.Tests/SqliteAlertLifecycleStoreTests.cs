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
    public void Initialize_AfterFreshParentInitialization_CreatesSeparateV1ComponentAndAppendOnlyTable()
    {
        Assert.Equal(AlertStoreStatus.Success, new SqliteAlertEngineStore(ConnectionString).Initialize().Status);

        var result = Store().Initialize();

        Assert.Equal(AlertLifecycleStoreStatus.Success, result.Status);
        using var connection = Open();
        Assert.Equal(1L, Scalar<long>(connection, "SELECT version FROM schema_version WHERE component='alert_lifecycle';"));
        Assert.Equal(["alert_lifecycle_events"], Strings(connection, "SELECT name FROM sqlite_schema WHERE type='table' AND name LIKE 'alert_lifecycle_%' ORDER BY name;"));
        Assert.Equal(2L, Scalar<long>(connection, "SELECT count(*) FROM sqlite_schema WHERE type='trigger' AND tbl_name='alert_lifecycle_events';"));
        AssertActorVocabulary(connection);
    }

    [Fact]
    public void Initialize_CounterfeitReceiptTableWithoutAcceptedParent_FailsClosedWithoutLifecycleObjects()
    {
        using (var connection = Open())
        {
            Execute(connection, "CREATE TABLE alert_receipts(alert_id TEXT PRIMARY KEY,canonical_json TEXT NOT NULL);");
        }

        var result = Store().Initialize();

        Assert.Equal(AlertLifecycleStoreStatus.Unavailable, result.Status);
        Assert.Equal("alert_lifecycle_store_unavailable", result.Code);
        using var check = Open();
        Assert.Equal(0L, Scalar<long>(check, "SELECT count(*) FROM sqlite_schema WHERE name LIKE 'alert_lifecycle_%';"));
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
        AssertActorVocabulary(connection);
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
    public void Mutate_UnexpectedConstraintFailureReturnsUnavailableWithoutAppend()
    {
        SeedReceipts();
        var store = Store();
        store.Initialize();
        using (var connection = Open())
        {
            Execute(connection, "CREATE TRIGGER synthetic_integrity_failure BEFORE INSERT ON alert_lifecycle_events BEGIN SELECT RAISE(ABORT,'synthetic integrity failure'); END;");
        }

        var result = store.Mutate(Command(AlertA, AlertLifecycleAction.Acknowledge, 0, 'a'));

        Assert.Equal(AlertLifecycleStoreStatus.Unavailable, result.Status);
        Assert.Equal("alert_lifecycle_store_unavailable", result.Code);
        Assert.Empty(store.History(AlertA).Events);
    }

    [Fact]
    public void Mutate_RejectsByteDistinctMalformedUtf16CommentsBeforeIdempotencyHashing()
    {
        SeedReceipts();
        var store = Store();
        store.Initialize();
        var first = Command(AlertA, AlertLifecycleAction.Acknowledge, 0, 'a') with { Comment = "high \ud800 surrogate" };
        var second = first with { Comment = "low \udc00 surrogate" };

        var firstResult = store.Mutate(first);
        var secondResult = store.Mutate(second);

        Assert.Equal("alert_comment_not_sanitized", firstResult.Code);
        Assert.Equal("alert_comment_not_sanitized", secondResult.Code);
        Assert.Empty(store.History(AlertA).Events);
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

        var superseded = store.Supersede(Command(AlertA, AlertLifecycleAction.Supersede, 1, 'b') with
        {
            Actor = "local_system",
            ReasonCode = "rule_version_changed",
            Comment = null,
            OldAlertId = AlertA,
            NewAlertId = AlertB,
        });

        Assert.Equal(AlertLifecycleStoreStatus.Success, superseded.Status);
        Assert.Equal(AlertLifecycleState.Superseded, superseded.Lifecycle!.State);
        Assert.Equal("local_system", superseded.Event!.Actor);
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

        var deleted = store.SourceDeleted(Command(AlertA, AlertLifecycleAction.SourceDeleted, 1, 'b') with
        {
            Actor = "local_system",
            ReasonCode = "source_deleted",
            Comment = null,
        });

        Assert.Equal(AlertLifecycleStoreStatus.Success, deleted.Status);
        Assert.Equal(AlertLifecycleState.Acknowledged, deleted.Lifecycle!.State);
        Assert.Equal(2, deleted.Lifecycle.Revision);
        Assert.Equal("local_system", deleted.Event!.Actor);
        Assert.Equal(receipt, ReceiptJson(AlertA));
    }

    [Fact]
    public void Resolve_ReevaluationEvidenceDisappearedAppendsSystemEventWithoutChangingReceipt()
    {
        SeedReceipts();
        var store = Store();
        store.Initialize();
        var receipt = ReceiptJson(AlertA);

        var resolved = store.ResolveFromReevaluation(Command(AlertA, AlertLifecycleAction.Resolve, 0, 'a') with
        {
            Actor = "local_system",
            ReasonCode = "evidence_missing",
            Comment = null,
        });

        Assert.Equal(AlertLifecycleStoreStatus.Success, resolved.Status);
        Assert.Equal(AlertLifecycleState.Resolved, resolved.Lifecycle!.State);
        Assert.Equal("local_system", resolved.Event!.Actor);
        Assert.Equal("evidence_missing", resolved.Event.ReasonCode);
        Assert.Equal(receipt, ReceiptJson(AlertA));
    }

    [Fact]
    public void TrustedProducerSeams_RejectUserActorSystemCommentAndNonExactAlertId()
    {
        SeedReceipts();
        var store = Store();
        store.Initialize();

        var userActor = store.ResolveFromReevaluation(Command(AlertA, AlertLifecycleAction.Resolve, 0, 'a') with { Comment = null });
        var systemComment = store.ResolveFromReevaluation(Command(AlertA, AlertLifecycleAction.Resolve, 0, 'b') with { Actor = "local_system" });
        var nonExactAlert = store.ResolveFromReevaluation(Command(AlertA.ToUpperInvariant(), AlertLifecycleAction.Resolve, 0, 'c') with { Actor = "local_system", Comment = null });
        var unknownActor = store.Mutate(Command(AlertA, AlertLifecycleAction.Acknowledge, 0, 'd') with { Actor = "remote_user" });

        Assert.Equal("alert_invalid_actor", userActor.Code);
        Assert.Equal("alert_invalid_reevaluation", systemComment.Code);
        Assert.Equal("alert_invalid_request", nonExactAlert.Code);
        Assert.Equal("alert_invalid_request", unknownActor.Code);
        Assert.Empty(store.History(AlertA).Events);
    }

    [Fact]
    public void LifecycleEvent_ReferencesImmutableReceiptVersionMetadataWithoutDuplicatingIt()
    {
        SeedReceipts();
        var store = Store();
        store.Initialize();
        Assert.Equal(AlertLifecycleStoreStatus.Success, store.Mutate(Command(AlertA, AlertLifecycleAction.Acknowledge, 0, 'a')).Status);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT json_extract(receipt.canonical_json,'$.evaluation_id'),
                   json_extract(receipt.canonical_json,'$.rule_version'),
                   json_extract(receipt.canonical_json,'$.configuration_version')
            FROM alert_lifecycle_events AS lifecycle
            JOIN alert_receipts AS receipt ON receipt.alert_id=lifecycle.alert_id
            WHERE lifecycle.alert_id=$alert;
            """;
        command.Parameters.AddWithValue("$alert", AlertA);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(new string('e', 64), reader.GetString(0));
        Assert.Equal("1", reader.GetString(1));
        Assert.Equal("fixture-v1", reader.GetString(2));
        Assert.Equal(
            ["event_id", "alert_id", "revision", "expected_revision", "action", "previous_state", "state", "occurred_at", "actor", "reason_code", "comment", "idempotency_key", "request_hash", "old_alert_id", "new_alert_id", "result_code"],
            Strings(connection, "SELECT name FROM pragma_table_info('alert_lifecycle_events') ORDER BY cid;"));
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
    public void Initialize_ExtraLifecycleInsertTriggerFailsClosed()
    {
        SeedReceipts();
        var store = Store();
        Assert.Equal(AlertLifecycleStoreStatus.Success, store.Initialize().Status);
        using (var connection = Open())
        {
            Execute(connection,
                "CREATE TRIGGER counterfeit_receipt_rewrite AFTER INSERT ON alert_lifecycle_events BEGIN UPDATE alert_receipts SET canonical_json=canonical_json WHERE alert_id=NEW.alert_id; END;");
        }

        var result = store.Initialize();

        Assert.Equal(AlertLifecycleStoreStatus.Unavailable, result.Status);
        Assert.Equal("alert_lifecycle_store_unavailable", result.Code);
    }

    [Fact]
    public void Operations_DefinitionMismatchedParentFailClosedWithoutAppend()
    {
        SeedReceipts();
        var store = Store();
        Assert.Equal(AlertLifecycleStoreStatus.Success, store.Initialize().Status);
        using (var connection = Open()) Execute(connection, "DROP TABLE alert_suppressions;");

        var read = store.Get(AlertA);
        var history = store.History(AlertA);
        var mutation = store.Mutate(Command(AlertA, AlertLifecycleAction.Acknowledge, 0, 'a'));

        Assert.Equal((AlertLifecycleStoreStatus.Unavailable, "alert_lifecycle_store_unavailable"), (read.Status, read.Code));
        Assert.Equal((AlertLifecycleStoreStatus.Unavailable, "alert_lifecycle_store_unavailable"), (history.Status, history.Code));
        Assert.Empty(history.Events);
        Assert.Equal((AlertLifecycleStoreStatus.Unavailable, "alert_lifecycle_store_unavailable"), (mutation.Status, mutation.Code));
        using var check = Open();
        Assert.Equal(0L, Scalar<long>(check, "SELECT count(*) FROM alert_lifecycle_events;"));
    }

    [Fact]
    public void Get_NonCanonicalPersistedUtcReturnsUnavailableWithoutLifecycleData()
    {
        SeedReceipts();
        var store = Store();
        Assert.Equal(AlertLifecycleStoreStatus.Success, store.Initialize().Status);
        InsertEvent("2026-07-22T00:00:00Z", 1L, 0L);

        var result = store.Get(AlertA);

        Assert.Equal(AlertLifecycleStoreStatus.Unavailable, result.Status);
        Assert.Equal("alert_lifecycle_store_unavailable", result.Code);
        Assert.Null(result.Lifecycle);
    }

    [Fact]
    public void History_NonIntegerPersistedRevisionReturnsUnavailableWithoutPartialEvents()
    {
        SeedReceipts();
        var store = Store();
        Assert.Equal(AlertLifecycleStoreStatus.Success, store.Initialize().Status);
        InsertEvent("2026-07-22T00:00:00.0000000+00:00", 1.5d, 0.5d, ignoreChecks: true);

        var result = store.History(AlertA);

        Assert.Equal(AlertLifecycleStoreStatus.Unavailable, result.Status);
        Assert.Equal("alert_lifecycle_store_unavailable", result.Code);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void Append_MaximumPersistedRevisionReturnsUnavailableWithoutOverflowOrInsert()
    {
        SeedReceipts();
        var store = Store();
        Assert.Equal(AlertLifecycleStoreStatus.Success, store.Initialize().Status);
        InsertEvent("2026-07-22T00:00:00.0000000+00:00", long.MaxValue, long.MaxValue - 1);

        var result = store.Mutate(Command(AlertA, AlertLifecycleAction.Resolve, long.MaxValue, 'b'));

        Assert.Equal(AlertLifecycleStoreStatus.Unavailable, result.Status);
        Assert.Equal("alert_lifecycle_store_unavailable", result.Code);
        using var check = Open();
        Assert.Equal(1L, Scalar<long>(check, "SELECT count(*) FROM alert_lifecycle_events;"));
    }

    [Fact]
    public void Initialize_OverflowingLifecycleVersionReturnsUnavailableWithoutThrowing()
    {
        SeedReceipts();
        var store = Store();
        Assert.Equal(AlertLifecycleStoreStatus.Success, store.Initialize().Status);
        using (var connection = Open()) Execute(connection, "UPDATE schema_version SET version=1e100 WHERE component='alert_lifecycle';");

        var result = store.Initialize();

        Assert.Equal(AlertLifecycleStoreStatus.Unavailable, result.Status);
        Assert.Equal("alert_lifecycle_store_unavailable", result.Code);
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

    [Fact]
    public void Initialize_DefinitionMismatchedLifecycleV1_FailsClosedWithoutRepair()
    {
        using (var connection = Open())
        {
            Execute(connection, "CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL); INSERT INTO schema_version VALUES('alert_lifecycle',1); CREATE TABLE alert_lifecycle_events(event_id TEXT PRIMARY KEY); INSERT INTO alert_lifecycle_events VALUES('kept');");
        }

        var result = Store().Initialize();

        Assert.Equal(AlertLifecycleStoreStatus.Unavailable, result.Status);
        Assert.Equal("alert_lifecycle_store_unavailable", result.Code);
        using var check = Open();
        Assert.Equal("kept", Scalar<string>(check, "SELECT event_id FROM alert_lifecycle_events;"));
        Assert.Equal(1L, Scalar<long>(check, "SELECT version FROM schema_version WHERE component='alert_lifecycle';"));
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

    private void InsertEvent(string occurredAt, object revision, object expectedRevision, bool ignoreChecks = false)
    {
        using var connection = Open();
        if (ignoreChecks) Execute(connection, "PRAGMA ignore_check_constraints=ON;");
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO alert_lifecycle_events(event_id,alert_id,revision,expected_revision,action,previous_state,state,occurred_at,actor,reason_code,comment,idempotency_key,request_hash,old_alert_id,new_alert_id,result_code) VALUES($event,$alert,$revision,$expected,'acknowledge','open','acknowledged',$occurred,'local_user','user_reviewed',NULL,$key,$hash,NULL,NULL,'alert_lifecycle_updated');";
        command.Parameters.AddWithValue("$event", new string('f', 64));
        command.Parameters.AddWithValue("$alert", AlertA);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$expected", expectedRevision);
        command.Parameters.AddWithValue("$occurred", occurredAt);
        command.Parameters.AddWithValue("$key", "aid1_" + new string('z', 43));
        command.Parameters.AddWithValue("$hash", new string('1', 64));
        command.ExecuteNonQuery();
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
    private static void AssertActorVocabulary(SqliteConnection connection) => Assert.Contains("actor IN ('local_user','local_system')", Scalar<string>(connection, "SELECT sql FROM sqlite_schema WHERE type='table' AND name='alert_lifecycle_events';"), StringComparison.Ordinal);

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
}
