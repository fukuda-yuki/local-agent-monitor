using System.Text;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Alerts.Tests;

public sealed class SqliteAlertEngineQueryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "alert-query-tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "monitor.sqlite");
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString();

    public SqliteAlertEngineQueryStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ListReceipts_PagesInStableIdOrderWithExactBytesAndTypedProjection()
    {
        var store = InitializedStore();
        var evaluations = Enumerable.Range(1, 3).Select(ReceiptEvaluation).ToArray();
        foreach (var evaluation in evaluations) Assert.Equal(AlertStoreStatus.Success, store.Append(evaluation).Status);
        var expected = evaluations.SelectMany(item => item.Receipts).OrderBy(item => item.AlertId, StringComparer.Ordinal).ToArray();

        var first = store.ListReceipts(null, 2);
        var second = store.ListReceipts(first.NextCursor, 2);

        Assert.Equal(AlertEngineQueryStatus.Success, first.Status);
        Assert.Null(first.Code);
        Assert.Equal(expected[..2].Select(item => item.AlertId), first.Items.Select(item => item.Receipt.AlertId));
        Assert.Equal(expected[1].AlertId, first.NextCursor);
        Assert.Equal(expected[2].AlertId, Assert.Single(second.Items).Receipt.AlertId);
        Assert.Null(second.NextCursor);
        foreach (var item in first.Items.Concat(second.Items))
        {
            var receipt = expected.Single(value => value.AlertId == item.Receipt.AlertId);
            Assert.Equal(AlertCanonicalJson.SerializeReceipt(receipt), item.CanonicalBytes);
            Assert.Equal(receipt.RuleId, item.Receipt.RuleId);
            Assert.Equal(receipt.Severity, item.Receipt.Severity);
            Assert.Equal(receipt.Evidence, item.Receipt.Evidence);
            Assert.Equal(receipt.ObservedValues, item.Receipt.ObservedValues);
            Assert.Equal(receipt.EffectiveThresholds, item.Receipt.EffectiveThresholds);
            Assert.Equal(receipt.Completeness, item.Receipt.Completeness);
            Assert.Equal(receipt.Summary, item.Receipt.Summary);
            Assert.Equal(receipt.AlertId, AlertReceiptConsumerV1.Validate(item.CanonicalBytes.ToArray()).AlertId);
        }
    }

    [Fact]
    public void ListEvaluations_PagesTypedMetadataAndChildCountsInStableIdOrder()
    {
        var store = InitializedStore();
        var evaluations = new[] { ReceiptEvaluation(1), SuppressedEvaluation(2), ReceiptEvaluation(3) };
        foreach (var evaluation in evaluations) Assert.Equal(AlertStoreStatus.Success, store.Append(evaluation).Status);
        var expected = evaluations.OrderBy(item => item.EvaluationId, StringComparer.Ordinal).ToArray();

        var first = store.ListEvaluations(null, 2);
        var second = store.ListEvaluations(first.NextCursor, 2);

        Assert.Equal(AlertEngineQueryStatus.Success, first.Status);
        Assert.Equal(expected[..2].Select(item => item.EvaluationId), first.Items.Select(item => item.EvaluationId));
        Assert.Equal(expected[1].EvaluationId, first.NextCursor);
        Assert.Equal(expected[2].EvaluationId, Assert.Single(second.Items).EvaluationId);
        Assert.Null(second.NextCursor);
        foreach (var item in first.Items.Concat(second.Items))
        {
            var evaluation = expected.Single(value => value.EvaluationId == item.EvaluationId);
            Assert.Equal(evaluation.InputHash, item.InputHash);
            Assert.Equal(evaluation.ConfigurationVersion, item.ConfigurationVersion);
            Assert.Equal(evaluation.ConfigurationHash, item.ConfigurationHash);
            Assert.Equal(evaluation.Receipts.Count, item.ReceiptCount);
            Assert.Equal(evaluation.Suppressions.Count, item.SuppressionCount);
        }
    }

    [Fact]
    public void ListSuppressions_ReturnsExactBytesAndTypedProjectionOrBoundedNotFound()
    {
        var store = InitializedStore();
        var evaluation = SuppressedEvaluation(1);
        Assert.Equal(AlertStoreStatus.Success, store.Append(evaluation).Status);

        var page = store.ListSuppressions(evaluation.EvaluationId, null, 1);
        var missing = store.ListSuppressions(new string('f', 64), null, 1);

        var item = Assert.Single(page.Items);
        var expected = Assert.Single(evaluation.Suppressions);
        Assert.Equal(AlertEngineQueryStatus.Success, page.Status);
        Assert.Null(page.Code);
        Assert.Equal(0, item.SuppressionOrdinal);
        Assert.Equal(AlertCanonicalJson.SerializeSuppression(expected), item.CanonicalBytes);
        Assert.Equal(expected.EvaluationId, item.Suppression.EvaluationId);
        Assert.Equal(expected.RuleId, item.Suppression.RuleId);
        Assert.Equal(expected.Code, item.Suppression.Code);
        Assert.Equal(expected.MissingCapabilities, item.Suppression.MissingCapabilities);
        Assert.Null(page.NextCursor);
        Assert.Equal(AlertEngineQueryStatus.NotFound, missing.Status);
        Assert.Equal("alert_not_found", missing.Code);
        Assert.Empty(missing.Items);
        Assert.Null(missing.NextCursor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Queries_InvalidBoundsAndCursors_ReturnOnlyFixedInvalidResult(int limit)
    {
        var store = InitializedStore();

        var receipt = store.ListReceipts("C:\\Users\\private", limit);
        var evaluation = store.ListEvaluations("C:\\Users\\private", limit);
        var suppression = store.ListSuppressions("C:\\Users\\private", -1, limit);

        AssertInvalid(receipt.Status, receipt.Code, receipt.Items, receipt.NextCursor);
        AssertInvalid(evaluation.Status, evaluation.Code, evaluation.Items, evaluation.NextCursor);
        AssertInvalid(suppression.Status, suppression.Code, suppression.Items, suppression.NextCursor);
    }

    [Fact]
    public void Queries_NewerSchemaOrInvalidCanonicalChild_FailClosedWithoutPartialRowsOrLeaks()
    {
        var store = InitializedStore();
        var first = ReceiptEvaluation(1);
        var second = ReceiptEvaluation(2);
        store.Append(first);
        store.Append(second);
        var validJson = Encoding.UTF8.GetString(AlertCanonicalJson.SerializeReceipt(second.Receipts[0]));
        var hostileJson = validJson[..^1] + ",\"private_path\":\"C:\\\\Users\\\\secret\"}";
        using (var connection = Open())
        {
            Execute(connection, "UPDATE alert_receipts SET canonical_json=$json WHERE alert_id=$id;", ("$json", hostileJson), ("$id", second.Receipts[0].AlertId));
        }

        var invalidRow = store.ListReceipts(null, 100);

        AssertUnavailable(invalidRow.Status, invalidRow.Code, invalidRow.Items, invalidRow.NextCursor);
        Assert.DoesNotContain("private", invalidRow.ToString(), StringComparison.OrdinalIgnoreCase);
        using (var connection = Open()) Execute(connection, "UPDATE schema_version SET version=2 WHERE component='alert_engine';");
        var newerSchema = store.ListEvaluations(null, 100);
        AssertUnavailable(newerSchema.Status, newerSchema.Code, newerSchema.Items, newerSchema.NextCursor);
    }

    [Fact]
    public void ListReceipts_OversizedCanonicalRow_FailsClosedWithoutTruncation()
    {
        var store = InitializedStore();
        var evaluation = ReceiptEvaluation(1);
        store.Append(evaluation);
        var receipt = Assert.Single(evaluation.Receipts);
        var oversized = $"{{\"alert_id\":\"{receipt.AlertId}\",\"evaluation_id\":\"{evaluation.EvaluationId}\",\"padding\":\"{new string('x', AlertEngineQueryLimits.MaximumPageBytes)}\"}}";
        using (var connection = Open())
        {
            Execute(connection, "UPDATE alert_receipts SET canonical_json=$json WHERE alert_id=$id;", ("$json", oversized), ("$id", receipt.AlertId));
        }

        var result = store.ListReceipts(null, 1);

        AssertUnavailable(result.Status, result.Code, result.Items, result.NextCursor);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private SqliteAlertEngineStore InitializedStore()
    {
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertStoreStatus.Success, store.Initialize().Status);
        return store;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }

    private static void AssertInvalid<T>(AlertEngineQueryStatus status, string? code, IReadOnlyList<T> items, object? cursor)
    {
        Assert.Equal(AlertEngineQueryStatus.Invalid, status);
        Assert.Equal("invalid_alert_query", code);
        Assert.Empty(items);
        Assert.Null(cursor);
    }

    private static void AssertUnavailable<T>(AlertEngineQueryStatus status, string? code, IReadOnlyList<T> items, object? cursor)
    {
        Assert.Equal(AlertEngineQueryStatus.Unavailable, status);
        Assert.Equal("alert_store_unavailable", code);
        Assert.Empty(items);
        Assert.Null(cursor);
    }

    private static AlertEvaluationResult ReceiptEvaluation(int discriminator) => Evaluate(discriminator, available: true);

    private static AlertEvaluationResult SuppressedEvaluation(int discriminator) => Evaluate(discriminator, available: false);

    private static AlertEvaluationResult Evaluate(int discriminator, bool available)
    {
        var observed = new DateTimeOffset(2026, 7, 23, 1, 2, discriminator, TimeSpan.Zero);
        var sessionId = $"session-{discriminator}";
        var traceId = $"trace-{discriminator}";
        var evidence = new AlertEvidenceReference(
            AlertEvidenceKind.ToolCall,
            $"evidence-{discriminator}",
            sessionId,
            traceId,
            $"span-{discriminator}",
            null,
            $"event-{discriminator}",
            $"tool-call-{discriminator}",
            observed);
        var snapshot = new AlertNormalizedSnapshot(
            AlertContractVersions.Snapshot,
            "github-copilot",
            "1.2.3",
            sessionId,
            traceId,
            AlertCompleteness.Partial,
            ["ingest_gap"],
            observed,
            observed.AddSeconds(1),
            [new("tool-events", available ? AlertCapabilityAvailability.Available : AlertCapabilityAvailability.Unknown)],
            [new($"signal-{discriminator}", AlertSignalKind.ToolCall, 1, observed, null, AlertSignalStatus.Error, [], [], evidence)]);
        var descriptor = new AlertRuleDescriptor(
            "fixture-rule",
            "1",
            "Fixture summary",
            "Fixture description",
            ["tool-events"],
            AlertRuleScope.Trace,
            [],
            "trace",
            [],
            ["missing_required_capability", "rule_disabled", "source_not_applicable"],
            ["github-copilot"]);
        var match = new AlertRuleMatch(
            AlertSeverity.Warning,
            [new("count", "calls", discriminator)],
            [evidence],
            observed,
            observed.AddSeconds(1));
        return new AlertEvaluationEngine(
            new AlertRuleRegistry([new FixedRule(descriptor, match)]),
            new Resolver()).Evaluate(
                snapshot,
                new AlertEngineConfiguration(AlertContractVersions.Configuration, "fixture-v1", []));
    }

    private sealed class FixedRule(AlertRuleDescriptor descriptor, AlertRuleMatch match) : IAlertRule
    {
        public AlertRuleDescriptor Descriptor { get; } = descriptor;
        public AlertRuleOutcome Evaluate(AlertRuleContext context) => new([match], []);
    }

    private sealed class Resolver : IAlertEvidenceResolver
    {
        public bool Exists(AlertEvidenceReference reference) => true;
    }
}
