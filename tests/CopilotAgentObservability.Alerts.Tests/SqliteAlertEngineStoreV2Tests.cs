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

    [Fact]
    public void VersionedReceiptQuery_RejectsV2ReceiptWithV1ParentScalar()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.InitializeV2().Status);
        var evaluation = AlertEngineV2Tests.Evaluation();
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.Append(evaluation).Status);
        using (var connection = Open())
        {
            Execute(
                connection,
                "UPDATE alert_evaluations SET schema_version='alert.evaluation.v1' WHERE evaluation_id=$id;",
                ("$id", evaluation.EvaluationId));
        }

        var page = store.ListReceiptsVersioned(null, 10);

        Assert.Equal(AlertEngineQueryStatus.Unavailable, page.Status);
        Assert.Empty(page.Items);
    }

    [Fact]
    public void VersionedEvaluationQuery_RejectsV2ParentWithV1ReceiptScalar()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.InitializeV2().Status);
        var evaluation = AlertEngineV2Tests.Evaluation();
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.Append(evaluation).Status);
        using (var connection = Open())
        {
            Execute(
                connection,
                "UPDATE alert_receipts SET schema_version='alert.receipt.v1' WHERE evaluation_id=$id;",
                ("$id", evaluation.EvaluationId));
        }

        var page = store.ListEvaluationsVersioned(null, 10);

        Assert.Equal(AlertEngineQueryStatus.Unavailable, page.Status);
        Assert.Empty(page.Items);
    }

    [Fact]
    public void V1EvaluationQuery_RejectsV1ParentWithV2ReceiptScalar()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertStoreStatus.Success, store.Initialize().Status);
        var evaluation = V1Evaluation();
        Assert.Equal(AlertStoreStatus.Success, store.Append(evaluation).Status);
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.InitializeV2().Status);
        using (var connection = Open())
        {
            Execute(
                connection,
                "UPDATE alert_receipts SET schema_version='alert.receipt.v2' WHERE evaluation_id=$id;",
                ("$id", evaluation.EvaluationId));
        }

        var page = store.ListEvaluations(null, 10);

        Assert.Equal(AlertEngineQueryStatus.Unavailable, page.Status);
        Assert.Empty(page.Items);
    }

    [Fact]
    public void GetReceipt_V1ReceiptWithV2Parent_ReturnsUnavailable()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertStoreStatus.Success, store.Initialize().Status);
        var evaluation = V1Evaluation();
        Assert.Equal(AlertStoreStatus.Success, store.Append(evaluation).Status);
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.InitializeV2().Status);
        using (var connection = Open())
        {
            Execute(
                connection,
                "UPDATE alert_evaluations SET schema_version='alert.evaluation.v2' WHERE evaluation_id=$id;",
                ("$id", evaluation.EvaluationId));
        }

        var result = store.GetReceipt(evaluation.Receipts[0].AlertId);

        Assert.Equal(AlertStoreStatus.Unavailable, result.Status);
        Assert.Equal("alert_store_unavailable", result.Code);
        Assert.Null(result.CanonicalJson);
    }

    [Fact]
    public void GetReceiptV2_V2ReceiptWithV1Parent_ReturnsUnavailable()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.InitializeV2().Status);
        var evaluation = AlertEngineV2Tests.Evaluation();
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.Append(evaluation).Status);
        Assert.Equal(
            AlertEngineQueryStatus.Success,
            store.GetReceiptV2(evaluation.Receipts[0].AlertId).Status);
        using (var connection = Open())
        {
            Execute(
                connection,
                "UPDATE alert_evaluations SET schema_version='alert.evaluation.v1' WHERE evaluation_id=$id;",
                ("$id", evaluation.EvaluationId));
        }

        var result = store.GetReceiptV2(evaluation.Receipts[0].AlertId);

        Assert.Equal(AlertEngineQueryStatus.Unavailable, result.Status);
        Assert.Equal("alert_store_unavailable", result.Code);
        Assert.Empty(result.CanonicalBytes);
    }

    [Fact]
    public void V1ReceiptQuery_V1ReceiptWithV2Parent_ReturnsUnavailable()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertStoreStatus.Success, store.Initialize().Status);
        var evaluation = V1Evaluation();
        Assert.Equal(AlertStoreStatus.Success, store.Append(evaluation).Status);
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.InitializeV2().Status);
        using (var connection = Open())
        {
            Execute(
                connection,
                "UPDATE alert_evaluations SET schema_version='alert.evaluation.v2' WHERE evaluation_id=$id;",
                ("$id", evaluation.EvaluationId));
        }

        var page = store.ListReceipts(null, 10);

        Assert.Equal(AlertEngineQueryStatus.Unavailable, page.Status);
        Assert.Equal("alert_store_unavailable", page.Code);
        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void VersionedUnionConstructors_RequireProjectionToMatchContractVersion()
    {
        var evaluation = AlertEngineV2Tests.Evaluation();
        var evaluationBytes = AlertCanonicalJsonV2.SerializeEvaluation(evaluation);
        var receiptBytes = AlertCanonicalJsonV2.SerializeReceipt(evaluation.Receipts[0]);
        var evaluationProjection = AlertEvaluationConsumerV2.Validate(evaluationBytes);
        var receiptProjection = AlertCenterReceiptConsumerV2.Validate(receiptBytes);
        var inputs = AlertEngineV2Tests.Inputs();
        var suppressed = Assert.IsType<AlertEvaluationResultV2>(
            new AlertEvaluationEngine(new AlertRuleRegistryV2(), new ExistingResolverV2())
                .Evaluate(
                    new("session-estimated-cost-threshold", "1"),
                    inputs.Snapshot,
                    inputs.Configuration with { Rules = [] },
                    new(AlertEvidenceReadViewV2.Instance, []))
                .Evaluation);
        var suppressionBytes = AlertCanonicalJsonV2.SerializeSuppression(suppressed.Suppressions[0]);
        var suppressionProjection = AlertEvaluationConsumerV2.ValidateSuppression(suppressionBytes);

        Assert.Throws<ArgumentException>(() =>
            new AlertVersionedReceiptQueryItem(
                AlertContractKind.V1,
                receiptBytes,
                null,
                receiptProjection));
        Assert.Throws<ArgumentException>(() =>
            new AlertVersionedEvaluationQueryItem(
                AlertContractKind.V1,
                evaluationBytes,
                null,
                evaluationProjection));
        Assert.Throws<ArgumentException>(() =>
            new AlertVersionedSuppressionQueryItem(
                AlertContractKind.V1,
                0,
                suppressionBytes,
                null,
                suppressionProjection));
    }

    [Fact]
    public void VersionedReceiptQuery_ProjectsEligibilityDigestFromValidatedV2Parent()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.InitializeV2().Status);
        var evaluation = AlertEngineV2Tests.Evaluation();
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.Append(evaluation).Status);

        var page = store.ListReceiptsVersioned(null, 10);

        Assert.Equal(
            evaluation.EligibilityDigest,
            Assert.Single(page.Items).ReceiptV2!.EligibilityDigest);
    }

    [Fact]
    public void TransactionParticipant_ExternallyRolledBackTransactionIsInvalidBeforeAppend()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.InitializeV2().Status);
        var participant = Assert.IsAssignableFrom<ISqliteAlertEngineTransactionParticipantV2>(store);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var externalRollback = connection.CreateCommand())
        {
            externalRollback.Transaction = transaction;
            externalRollback.CommandText = "ROLLBACK;";
            externalRollback.ExecuteNonQuery();
        }

        var result = participant.AppendEvaluation(
            connection,
            transaction,
            AlertEngineV2Tests.Evaluation());

        Assert.Equal(AlertEngineTransactionAppendStatusV2.InvalidTransaction, result.Status);
        Assert.Null(result.EvaluationId);
        Assert.Empty(result.ReceiptIds);
        Assert.Empty(result.SuppressionIdentities);
    }

    [Fact]
    public void TransactionParticipant_IdenticalReplaySucceedsAndDifferentBytesConflict()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.InitializeV2().Status);
        var participant = Assert.IsAssignableFrom<ISqliteAlertEngineTransactionParticipantV2>(store);
        var evaluation = AlertEngineV2Tests.Evaluation();

        using (var connection = Open())
        using (var transaction = connection.BeginTransaction())
        {
            var first = participant.AppendEvaluation(connection, transaction, evaluation);
            var replay = participant.AppendEvaluation(connection, transaction, evaluation);
            Assert.Equal(AlertEngineTransactionAppendStatusV2.Success, first.Status);
            Assert.Equal(AlertEngineTransactionAppendStatusV2.Success, replay.Status);
            Assert.Equal(first.EvaluationId, replay.EvaluationId);
            Assert.Equal(first.ReceiptIds, replay.ReceiptIds);
            transaction.Commit();
        }

        var changedReceipt = evaluation.Receipts[0] with
        {
            SourceConfigurationHeadRevision = 2,
        };
        changedReceipt = Reidentify(changedReceipt);
        var conflicting = evaluation with
        {
            SourceConfigurationHeadRevision = 2,
            Receipts = [changedReceipt],
        };
        using var conflictConnection = Open();
        using var conflictTransaction = conflictConnection.BeginTransaction();

        var conflict = participant.AppendEvaluation(
            conflictConnection,
            conflictTransaction,
            conflicting);

        Assert.Equal(AlertEngineTransactionAppendStatusV2.Conflict, conflict.Status);
        Assert.Null(conflict.EvaluationId);
        Assert.Empty(conflict.ReceiptIds);
        Assert.Empty(conflict.SuppressionIdentities);
    }

    [Fact]
    public void InitializeV2_FutureVersionIsUnavailableWithoutMutation()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertStoreStatus.Success, store.Initialize().Status);
        using (var connection = Open())
        {
            Execute(
                connection,
                "UPDATE schema_version SET version=3 WHERE component='alert_engine';");
        }

        var result = store.InitializeV2();

        Assert.Equal(AlertEngineStoreStatusV2.Unavailable, result.Status);
        using var check = Open();
        Assert.Equal(
            3L,
            Scalar<long>(
                check,
                "SELECT version FROM schema_version WHERE component='alert_engine';"));
    }

    [Fact]
    public void InitializeV2_UnexpectedOwnedTriggerRollsBackWithoutMutation()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertStoreStatus.Success, store.Initialize().Status);
        using (var connection = Open())
        {
            Execute(
                connection,
                """
                CREATE TRIGGER unexpected_alert_trigger
                AFTER INSERT ON alert_evaluations
                BEGIN
                    SELECT 1;
                END;
                """);
        }

        var result = store.InitializeV2();

        Assert.Equal(AlertEngineStoreStatusV2.Unavailable, result.Status);
        using var check = Open();
        Assert.Equal(
            1L,
            Scalar<long>(
                check,
                "SELECT version FROM schema_version WHERE component='alert_engine';"));
        Assert.Equal(
            1L,
            Scalar<long>(
                check,
                "SELECT count(*) FROM sqlite_schema WHERE type='trigger' AND name='unexpected_alert_trigger';"));
    }

    [Fact]
    public void InitializeV2_RestartIsIdempotentAndPreservesMigratedBytes()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertStoreStatus.Success, store.Initialize().Status);
        var evaluation = V1Evaluation();
        Assert.Equal(AlertStoreStatus.Success, store.Append(evaluation).Status);
        var expected = store.GetEvaluation(evaluation.EvaluationId).CanonicalJson;

        var first = store.InitializeV2();
        var second = store.InitializeV2();

        Assert.Equal(AlertEngineStoreStatusV2.Success, first.Status);
        Assert.Equal(AlertEngineStoreStatusV2.Success, second.Status);
        Assert.Equal(expected, store.GetEvaluation(evaluation.EvaluationId).CanonicalJson);
    }

    [Fact]
    public void VersionedEvaluationQuery_PaginatesWithExactOwnerCursor()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.InitializeV2().Status);
        var first = AlertEngineV2Tests.Evaluation();
        var inputs = AlertEngineV2Tests.Inputs();
        var second = Assert.IsType<AlertEvaluationResultV2>(
            new AlertEvaluationEngine(new AlertRuleRegistryV2(), new ExistingResolverV2())
                .Evaluate(
                    new("session-estimated-cost-threshold", "1"),
                    inputs.Snapshot,
                    inputs.Configuration with { SourceConfigurationHeadRevision = 2 },
                    new(AlertEvidenceReadViewV2.Instance, []))
                .Evaluation);
        Assert.NotEqual(first.EvaluationId, second.EvaluationId);
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.Append(first).Status);
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.Append(second).Status);
        var ordered = new[] { first.EvaluationId, second.EvaluationId }
            .Order(StringComparer.Ordinal)
            .ToArray();

        var page1 = store.ListEvaluationsVersioned(null, 1);
        var page2 = store.ListEvaluationsVersioned(page1.NextCursor, 1);

        Assert.False(page1.Exhausted);
        Assert.Equal(ordered[0], page1.NextCursor);
        Assert.Equal(ordered[0], Assert.Single(page1.Items).EvaluationV2!.EvaluationId);
        Assert.True(page2.Exhausted);
        Assert.Null(page2.NextCursor);
        Assert.Equal(ordered[1], Assert.Single(page2.Items).EvaluationV2!.EvaluationId);
    }

    [Fact]
    public void VersionedEvaluationQuery_RejectsCanonicalRecordOverByteCap()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.InitializeV2().Status);
        var evaluation = AlertEngineV2Tests.Evaluation();
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.Append(evaluation).Status);
        var oversized = $$"""{"evaluation_id":"{{evaluation.EvaluationId}}","padding":"{{new string('x', AlertEngineQueryLimits.MaximumPageBytes)}}"}""";
        using (var connection = Open())
        {
            Execute(
                connection,
                "UPDATE alert_evaluations SET canonical_json=$json WHERE evaluation_id=$id;",
                ("$json", oversized),
                ("$id", evaluation.EvaluationId));
        }

        var page = store.ListEvaluationsVersioned(null, 1);

        Assert.Equal(AlertEngineQueryStatus.Unavailable, page.Status);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.CanonicalByteCount);
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

    private static void Execute(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        command.ExecuteNonQuery();
    }

    private static AlertReceiptV2 Reidentify(AlertReceiptV2 receipt)
    {
        var identityType = typeof(AlertEvaluationEngine).Assembly.GetType(
            "CopilotAgentObservability.Alerts.AlertReceiptIdentityV2",
            throwOnError: true)!;
        var create = identityType.GetMethod(
            "Create",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        return receipt with
        {
            AlertId = Assert.IsType<string>(create.Invoke(null, [receipt])),
        };
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
