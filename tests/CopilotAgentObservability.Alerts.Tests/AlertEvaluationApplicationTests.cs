using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Alerts.Tests;

public sealed class AlertEvaluationApplicationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "alert-application-tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "monitor.sqlite");
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString();

    public AlertEvaluationApplicationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void EvaluateAndAppend_FreshAndIdenticalEvaluation_ReturnsSamePersistedIdentity()
    {
        var fixture = Fixture.Create();
        var store = new SqliteAlertEngineStore(ConnectionString);
        var application = new AlertEvaluationApplication(fixture.Registry, fixture.Configuration, new Resolver(true), store);

        var first = application.EvaluateAndAppend(fixture.Snapshot);
        var repeated = application.EvaluateAndAppend(fixture.Snapshot);

        Assert.Equal(AlertEvaluationApplicationStatus.Success, first.Status);
        Assert.Null(first.Code);
        Assert.NotNull(first.Identity);
        Assert.Equal(first.Identity, repeated.Identity);
        Assert.Equal(AlertEvaluationApplicationStatus.Success, repeated.Status);
        Assert.Equal(AlertStoreStatus.Success, store.GetEvaluation(first.Identity!.EvaluationId).Status);
        Assert.Equal(1, first.Identity.ReceiptCount);
        Assert.Equal(0, first.Identity.SuppressionCount);
        Assert.Equal(0, first.Identity.RejectedMatchCount);
        Assert.NotNull(first.Outcome);
        Assert.Equal(first.Identity.EvaluationId, first.Outcome.EvaluationId);
        Assert.Equal(
            [Assert.Single(new AlertEvaluationEngine(fixture.Registry, new Resolver(true)).Evaluate(fixture.Snapshot, fixture.Configuration).Receipts).AlertId],
            first.Outcome.ReceiptIds);
        Assert.Empty(first.Outcome.Suppressions);
        Assert.Empty(first.Outcome.RejectedMatches);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)first.Outcome.ReceiptIds).Add("private-value"));
    }

    [Fact]
    public void EvaluateAndAppend_ConflictingStoredEvaluation_ReturnsConflictWithoutIdentity()
    {
        var fixture = Fixture.Create();
        var store = new SqliteAlertEngineStore(ConnectionString);
        Assert.Equal(AlertStoreStatus.Success, store.Initialize().Status);
        var evaluation = new AlertEvaluationEngine(fixture.Registry, new Resolver(true)).Evaluate(fixture.Snapshot, fixture.Configuration);
        Assert.Equal(AlertStoreStatus.Success, store.Append(evaluation with { ConfigurationHash = new string('f', 64) }).Status);
        var application = new AlertEvaluationApplication(fixture.Registry, fixture.Configuration, new Resolver(true), store);

        var result = application.EvaluateAndAppend(fixture.Snapshot);

        Assert.Equal(AlertEvaluationApplicationStatus.AppendConflict, result.Status);
        Assert.Equal("alert_store_conflict", result.Code);
        Assert.Null(result.Identity);
    }

    [Theory]
    [InlineData(AlertStoreStatus.Busy, "alert_store_busy", AlertEvaluationApplicationStatus.AppendBusy)]
    [InlineData(AlertStoreStatus.Unavailable, "alert_store_unavailable", AlertEvaluationApplicationStatus.AppendUnavailable)]
    public void EvaluateAndAppend_AppendFailure_NeverReturnsSuccess(
        AlertStoreStatus storeStatus,
        string storeCode,
        AlertEvaluationApplicationStatus expectedStatus)
    {
        var fixture = Fixture.Create();
        var store = new RecordingStore(append: new(storeStatus, storeCode));
        var application = new AlertEvaluationApplication(fixture.Registry, fixture.Configuration, new Resolver(true), store);

        var result = application.EvaluateAndAppend(fixture.Snapshot);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(storeCode, result.Code);
        Assert.Null(result.Identity);
        Assert.NotNull(store.AppendedEvaluation);
    }

    [Theory]
    [InlineData(AlertStoreStatus.Busy, "alert_store_busy", AlertEvaluationApplicationStatus.InitializationBusy)]
    [InlineData(AlertStoreStatus.Unavailable, "alert_store_unavailable", AlertEvaluationApplicationStatus.InitializationUnavailable)]
    public void EvaluateAndAppend_InitializationFailure_DoesNotEvaluateOrAppend(
        AlertStoreStatus storeStatus,
        string storeCode,
        AlertEvaluationApplicationStatus expectedStatus)
    {
        var fixture = Fixture.Create();
        var store = new RecordingStore(initialize: new(storeStatus, storeCode));
        var application = new AlertEvaluationApplication(fixture.Registry, fixture.Configuration, new Resolver(true), store);

        var result = application.EvaluateAndAppend(fixture.Snapshot);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(storeCode, result.Code);
        Assert.Null(result.Identity);
        Assert.Null(store.AppendedEvaluation);
    }

    [Fact]
    public void EvaluateAndAppend_InvalidSnapshot_ReturnsBoundedContractRejection()
    {
        var fixture = Fixture.Create();
        var store = new RecordingStore();
        var application = new AlertEvaluationApplication(fixture.Registry, fixture.Configuration, new Resolver(true), store);

        var result = application.EvaluateAndAppend(fixture.Snapshot with { SchemaVersion = "alert.snapshot.v2" });

        Assert.Equal(AlertEvaluationApplicationStatus.ContractRejected, result.Status);
        Assert.Equal("invalid_snapshot", result.Code);
        Assert.Null(result.Identity);
        Assert.Null(store.AppendedEvaluation);
    }

    [Fact]
    public void EvaluateAndAppend_InvalidFrozenConfiguration_ReturnsBoundedContractRejection()
    {
        var fixture = Fixture.Create();
        var store = new RecordingStore();
        var application = new AlertEvaluationApplication(
            fixture.Registry,
            fixture.Configuration with { SchemaVersion = "alert.config.v2" },
            new Resolver(true),
            store);

        var result = application.EvaluateAndAppend(fixture.Snapshot);

        Assert.Equal(AlertEvaluationApplicationStatus.ContractRejected, result.Status);
        Assert.Equal("invalid_configuration", result.Code);
        Assert.Null(result.Outcome);
        Assert.Null(store.AppendedEvaluation);
    }

    [Fact]
    public void EvaluateAndAppend_MissingCapability_AppendsSuppressedEvaluation()
    {
        var fixture = Fixture.Create();
        var store = new RecordingStore();
        var application = new AlertEvaluationApplication(fixture.Registry, fixture.Configuration, new Resolver(true), store);
        var snapshot = fixture.Snapshot with
        {
            Capabilities = [new("tool-events", AlertCapabilityAvailability.Unknown)],
        };

        var result = application.EvaluateAndAppend(snapshot);

        Assert.Equal(AlertEvaluationApplicationStatus.Success, result.Status);
        Assert.Equal(0, result.Identity!.ReceiptCount);
        Assert.Equal(1, result.Identity.SuppressionCount);
        Assert.Equal("missing_required_capability", Assert.Single(store.AppendedEvaluation!.Suppressions).Code);
        var suppression = Assert.Single(result.Outcome!.Suppressions);
        Assert.Equal("fixture-rule", suppression.RuleId);
        Assert.Equal("missing_required_capability", suppression.Code);
        Assert.Equal(["tool-events"], suppression.MissingCapabilities);
    }

    [Fact]
    public void EvaluateAndAppend_UnresolvedEvidence_AppendsRejectedEvaluation()
    {
        var fixture = Fixture.Create();
        var store = new RecordingStore();
        var application = new AlertEvaluationApplication(fixture.Registry, fixture.Configuration, new Resolver(false), store);

        var result = application.EvaluateAndAppend(fixture.Snapshot);

        Assert.Equal(AlertEvaluationApplicationStatus.Success, result.Status);
        Assert.Equal(0, result.Identity!.ReceiptCount);
        Assert.Equal(1, result.Identity.RejectedMatchCount);
        Assert.Equal("unresolved_evidence", Assert.Single(store.AppendedEvaluation!.RejectedMatches).Code);
        var rejected = Assert.Single(result.Outcome!.RejectedMatches);
        Assert.Equal("fixture-rule", rejected.RuleId);
        Assert.Equal("1", rejected.RuleVersion);
        Assert.Equal("unresolved_evidence", rejected.Code);
    }

    [Fact]
    public void EvaluateAndAppend_ConfigurationIsFrozenAtConstruction()
    {
        var fixture = Fixture.Create();
        var rules = new List<AlertRuleConfiguration>();
        var configuration = new AlertEngineConfiguration(AlertContractVersions.Configuration, "fixture-v1", rules);
        var store = new RecordingStore();
        var application = new AlertEvaluationApplication(fixture.Registry, configuration, new Resolver(true), store);
        rules.Add(new("fixture-rule", "1", false, new Dictionary<string, decimal>(), null));

        var result = application.EvaluateAndAppend(fixture.Snapshot);

        Assert.Equal(AlertEvaluationApplicationStatus.Success, result.Status);
        Assert.Equal(1, result.Identity!.ReceiptCount);
        Assert.Empty(store.AppendedEvaluation!.Suppressions);
    }

    [Fact]
    public void EvaluateAndAppend_MalformedStoreSuccess_FailsClosedWithoutSuppliedCode()
    {
        var fixture = Fixture.Create();
        var store = new RecordingStore(append: new(AlertStoreStatus.Success, "private-database-text"));
        var application = new AlertEvaluationApplication(fixture.Registry, fixture.Configuration, new Resolver(true), store);

        var result = application.EvaluateAndAppend(fixture.Snapshot);

        Assert.Equal(AlertEvaluationApplicationStatus.AppendUnavailable, result.Status);
        Assert.Equal("alert_store_unavailable", result.Code);
        Assert.Null(result.Identity);
        Assert.DoesNotContain("private", result.Code, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class Resolver(bool exists) : IAlertEvidenceResolver
    {
        public bool Exists(AlertEvidenceReference reference) => exists;
    }

    private sealed class FixedRule(AlertRuleDescriptor descriptor, AlertRuleMatch match) : IAlertRule
    {
        public AlertRuleDescriptor Descriptor { get; } = descriptor;
        public AlertRuleOutcome Evaluate(AlertRuleContext context) => new([match], []);
    }

    private sealed class RecordingStore(
        AlertStoreResult? initialize = null,
        AlertStoreResult? append = null) : IAlertEngineStore
    {
        private readonly AlertStoreResult _initialize = initialize ?? new(AlertStoreStatus.Success);
        private readonly AlertStoreResult _append = append ?? new(AlertStoreStatus.Success);

        public AlertEvaluationResult? AppendedEvaluation { get; private set; }

        public AlertStoreResult Initialize() => _initialize;

        public AlertStoreResult Append(AlertEvaluationResult evaluation)
        {
            AppendedEvaluation = evaluation;
            return _append;
        }

        public AlertStoreReadResult GetEvaluation(string evaluationId) => throw new NotSupportedException();
        public AlertStoreReadResult GetReceipt(string alertId) => throw new NotSupportedException();
        public AlertStoreListResult ListSuppressions(string evaluationId) => throw new NotSupportedException();
    }

    private sealed record Fixture(
        AlertRuleRegistry Registry,
        AlertEngineConfiguration Configuration,
        AlertNormalizedSnapshot Snapshot)
    {
        public static Fixture Create()
        {
            var observed = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
            var evidence = new AlertEvidenceReference(
                AlertEvidenceKind.ToolCall,
                "evidence-1",
                "session-1",
                "trace-1",
                "span-1",
                null,
                "event-1",
                "tool-call-1",
                observed);
            var snapshot = new AlertNormalizedSnapshot(
                AlertContractVersions.Snapshot,
                "github-copilot",
                "1.2.3",
                "session-1",
                "trace-1",
                AlertCompleteness.Partial,
                ["ingest_gap"],
                observed,
                observed.AddSeconds(1),
                [new("tool-events", AlertCapabilityAvailability.Available)],
                [new("signal-1", AlertSignalKind.ToolCall, 1, observed, null, AlertSignalStatus.Error, [], [], evidence)]);
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
                [new("count", "calls", 1)],
                [evidence],
                observed,
                observed.AddSeconds(1));
            return new(
                new AlertRuleRegistry([new FixedRule(descriptor, match)]),
                new AlertEngineConfiguration(AlertContractVersions.Configuration, "fixture-v1", []),
                snapshot);
        }
    }
}
