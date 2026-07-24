using System.Text;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Alerts.Tests;

public sealed class SqliteAlertEngineStoreV2Tests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "alert-store-v2-tests",
        Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(directory, "monitor.sqlite");
    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        Pooling = false,
    }.ToString();

    public SqliteAlertEngineStoreV2Tests() => Directory.CreateDirectory(directory);

    [Fact]
    public void InitializeV2_ValidV1Database_PreservesCanonicalRowsAndV1Queries()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertStoreStatus.Success, store.Initialize().Status);
        var evaluation = V1Evaluation();
        Assert.Equal(AlertStoreStatus.Success, store.Append(evaluation).Status);
        var evaluationBytes = store.GetEvaluation(evaluation.EvaluationId).CanonicalJson;
        var receiptBytes = store.GetReceipt(evaluation.Receipts[0].AlertId).CanonicalJson;

        var result = store.InitializeV2();

        Assert.Equal(AlertEngineStoreStatusV2.Success, result.Status);
        using var connection = Open();
        Assert.Equal(2L, Scalar<long>(
            connection,
            "SELECT version FROM schema_version WHERE component='alert_engine';"));
        Assert.Equal(evaluationBytes, store.GetEvaluation(evaluation.EvaluationId).CanonicalJson);
        Assert.Equal(receiptBytes, store.GetReceipt(evaluation.Receipts[0].AlertId).CanonicalJson);
        Assert.Equal(evaluation.Receipts[0].AlertId, Assert.Single(store.ListReceipts(null, 10).Items).Receipt.AlertId);
    }

    [Fact]
    public void TransactionParticipant_UsesCallerTransactionAndVersionedQueryReturnsTypedV2()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.InitializeV2().Status);
        var evaluation = AlertEngineV2Tests.Evaluation();
        var participant = Assert.IsAssignableFrom<ISqliteAlertEngineTransactionParticipantV2>(store);

        using (var connection = Open())
        using (var transaction = connection.BeginTransaction())
        {
            var rolledBack = participant.AppendEvaluation(connection, transaction, evaluation);
            Assert.Equal(AlertEngineTransactionAppendStatusV2.Success, rolledBack.Status);
            transaction.Rollback();
        }
        Assert.Equal(
            AlertEngineQueryStatus.NotFound,
            store.GetEvaluationV2(evaluation.EvaluationId).Status);

        using (var connection = Open())
        using (var transaction = connection.BeginTransaction())
        {
            var committed = participant.AppendEvaluation(connection, transaction, evaluation);
            Assert.Equal(AlertEngineTransactionAppendStatusV2.Success, committed.Status);
            Assert.Equal(evaluation.EvaluationId, committed.EvaluationId);
            transaction.Commit();
        }

        var page = store.ListEvaluationsVersioned(null, 10);
        Assert.Equal(AlertEngineQueryStatus.Success, page.Status);
        Assert.True(page.Exhausted);
        var item = Assert.Single(page.Items);
        Assert.Equal(AlertContractKind.V2, item.ContractVersion);
        Assert.Equal(evaluation.EvaluationId, item.EvaluationV2!.EvaluationId);
        Assert.Null(item.EvaluationV1);
        Assert.Equal(
            Encoding.UTF8.GetString(AlertCanonicalJsonV2.SerializeEvaluation(evaluation)),
            Encoding.UTF8.GetString(item.CanonicalBytes.ToArray()));
    }

    [Fact]
    public void InitializeV2_LifecycleForeignKeyAndRowsRemainBoundToLiveReceiptTable()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertStoreStatus.Success, store.Initialize().Status);
        var evaluation = V1Evaluation();
        Assert.Equal(AlertStoreStatus.Success, store.Append(evaluation).Status);
        var lifecycle = new SqliteAlertLifecycleStore(
            ConnectionString,
            new FixedTimeProvider(new(2026, 7, 24, 2, 0, 0, TimeSpan.Zero)));
        Assert.Equal(AlertLifecycleStoreStatus.Success, lifecycle.Initialize().Status);
        Assert.Equal(
            AlertLifecycleStoreStatus.Success,
            lifecycle.Mutate(new(
                evaluation.Receipts[0].AlertId,
                AlertLifecycleAction.Acknowledge,
                0,
                "user_reviewed",
                "reviewed locally",
                "aid1_" + new string('a', 43))).Status);
        using var before = Open();
        var lifecycleSql = Scalar<string>(
            before,
            "SELECT sql FROM sqlite_schema WHERE type='table' AND name='alert_lifecycle_events';");

        var result = store.InitializeV2();

        Assert.Equal(AlertEngineStoreStatusV2.Success, result.Status);
        using var after = Open();
        Assert.Equal(
            lifecycleSql,
            Scalar<string>(
                after,
                "SELECT sql FROM sqlite_schema WHERE type='table' AND name='alert_lifecycle_events';"));
        Assert.Contains(
            "REFERENCES alert_receipts(alert_id)",
            lifecycleSql,
            StringComparison.Ordinal);
        Assert.Equal(
            0L,
            Scalar<long>(after, "SELECT count(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(
            1L,
            Scalar<long>(after, "SELECT count(*) FROM alert_lifecycle_events;"));
    }

    [Fact]
    public void InitializeV2_NonCanonicalV1Row_RollsBackWithoutChangingVersionOrBytes()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertStoreStatus.Success, store.Initialize().Status);
        var evaluation = V1Evaluation();
        Assert.Equal(AlertStoreStatus.Success, store.Append(evaluation).Status);
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE alert_evaluations SET canonical_json=canonical_json || ' ' WHERE evaluation_id=$id;";
            command.Parameters.AddWithValue("$id", evaluation.EvaluationId);
            command.ExecuteNonQuery();
        }
        var nonCanonical = store.GetEvaluation(evaluation.EvaluationId).CanonicalJson;

        var result = store.InitializeV2();

        Assert.Equal(AlertEngineStoreStatusV2.Unavailable, result.Status);
        using var check = Open();
        Assert.Equal(
            1L,
            Scalar<long>(
                check,
                "SELECT version FROM schema_version WHERE component='alert_engine';"));
        Assert.Equal(
            nonCanonical,
            Scalar<string>(
                check,
                "SELECT canonical_json FROM alert_evaluations WHERE evaluation_id='" +
                evaluation.EvaluationId +
                "';"));
    }

    [Fact]
    public void EvaluateAndAppend_V2_UsesExistingApplicationAndStore()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        var inputs = AlertEngineV2Tests.Inputs();
        var application = new AlertEvaluationApplication(
            new AlertRuleRegistryV2(),
            inputs.Configuration,
            new ExistingResolverV2(),
            store);

        var result = application.EvaluateAndAppend(
            new("session-estimated-cost-threshold", "1"),
            inputs.Snapshot);

        Assert.Equal(AlertEvaluationApplicationStatusV2.Success, result.Status);
        Assert.NotNull(result.Outcome);
        Assert.Single(result.Outcome.ReceiptIds);
        Assert.Equal(
            result.Outcome.EvaluationId,
            Assert.Single(store.ListEvaluationsVersioned(null, 10).Items)
                .EvaluationV2!.EvaluationId);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(
            command.ExecuteScalar()!,
            typeof(T),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static AlertEvaluationResult V1Evaluation()
    {
        var observed = new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);
        var evidence = new AlertEvidenceReference(
            AlertEvidenceKind.Session,
            "session-evidence",
            "session-1",
            null,
            null,
            null,
            null,
            null,
            observed);
        var snapshot = new AlertNormalizedSnapshot(
            AlertContractVersions.Snapshot,
            "github-copilot",
            "1",
            "session-1",
            null,
            AlertCompleteness.Full,
            [],
            observed,
            observed,
            [new("tool-events", AlertCapabilityAvailability.Available)],
            [
                new(
                    "signal-1",
                    AlertSignalKind.SessionEvent,
                    0,
                    observed,
                    null,
                    AlertSignalStatus.Success,
                    [],
                    [],
                    evidence),
            ]);
        var descriptor = new AlertRuleDescriptor(
            "migration-fixture",
            "1",
            "Migration fixture",
            "Produces one strict v1 receipt for migration.",
            ["tool-events"],
            AlertRuleScope.Session,
            [],
            "session",
            [],
            ["missing_required_capability", "rule_disabled", "source_not_applicable"],
            ["github-copilot"]);
        var rule = new FixedRule(
            descriptor,
            new(
                [new(AlertSeverity.Warning, [new("count", "calls", 1)], [evidence], observed, observed)],
                []));
        return new AlertEvaluationEngine(
            new AlertRuleRegistry([rule]),
            new ExistingResolver()).Evaluate(
                snapshot,
                new(AlertContractVersions.Configuration, "migration-v1", []));
    }

    private sealed class FixedRule(
        AlertRuleDescriptor descriptor,
        AlertRuleOutcome outcome) : IAlertRule
    {
        public AlertRuleDescriptor Descriptor { get; } = descriptor;
        public AlertRuleOutcome Evaluate(AlertRuleContext context) => outcome;
    }

    private sealed class ExistingResolver : IAlertEvidenceResolver
    {
        public bool Exists(AlertEvidenceReference reference) => true;
    }

    private sealed class ExistingResolverV2 : IAlertEvidenceResolverV2
    {
        public AlertEvidenceResolutionStatusV2 Resolve(
            AlertEvidenceReferenceV2 reference,
            AlertEvidenceResolutionScopeV2 scope) =>
            AlertEvidenceResolutionStatusV2.Resolved;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
