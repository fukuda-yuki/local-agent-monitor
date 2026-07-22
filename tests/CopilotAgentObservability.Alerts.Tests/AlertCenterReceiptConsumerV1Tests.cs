using System.Reflection;
using System.Text;
using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.Alerts.Tests;

public sealed class AlertCenterReceiptConsumerV1Tests
{
    [Fact]
    public void Validate_ExactEngineReceipt_ReturnsFullyTypedReadOnlyProjection()
    {
        var receipt = EngineReceipt();
        var bytes = AlertCanonicalJson.SerializeReceipt(receipt);

        var projection = AlertCenterReceiptConsumerV1.Validate(bytes);

        Assert.Equal(receipt.AlertId, projection.AlertId);
        Assert.Equal(receipt.EvaluationId, projection.EvaluationId);
        Assert.Equal(receipt.RuleId, projection.RuleId);
        Assert.Equal(receipt.RuleVersion, projection.RuleVersion);
        Assert.Equal(receipt.Severity, projection.Severity);
        Assert.Equal(receipt.InitialState, projection.InitialState);
        Assert.Equal(receipt.SourceSurface, projection.SourceSurface);
        Assert.Equal(receipt.SourceVersion, projection.SourceVersion);
        Assert.Equal(receipt.SessionId, projection.SessionId);
        Assert.Equal(receipt.TraceId, projection.TraceId);
        Assert.Equal(receipt.Evidence, projection.Evidence);
        Assert.Equal(receipt.ObservedValues, projection.ObservedValues);
        Assert.Equal(receipt.EffectiveThresholds, projection.EffectiveThresholds);
        Assert.Equal(receipt.ConfigurationVersion, projection.ConfigurationVersion);
        Assert.Equal(receipt.ConfigurationHash, projection.ConfigurationHash);
        Assert.Equal(receipt.RequiredCapabilities, projection.RequiredCapabilities);
        Assert.Equal(receipt.Completeness, projection.Completeness);
        Assert.Equal(receipt.CompletenessReasons, projection.CompletenessReasons);
        Assert.Equal(receipt.FirstObservedAt, projection.FirstObservedAt);
        Assert.Equal(receipt.LastObservedAt, projection.LastObservedAt);
        Assert.Equal(receipt.EvaluationInputHash, projection.EvaluationInputHash);
        Assert.Equal(receipt.Summary, projection.Summary);
        Assert.Throws<NotSupportedException>(() => ((IList<AlertEvidenceReference>)projection.Evidence).Add(receipt.Evidence[0]));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)projection.RequiredCapabilities).Add("private-value"));
    }

    [Fact]
    public void Validate_InvalidReceipt_UsesExistingFixedNoLeakFailure()
    {
        var bytes = AlertCanonicalJson.SerializeReceipt(EngineReceipt());
        var json = Encoding.UTF8.GetString(bytes);
        var hostile = Encoding.UTF8.GetBytes(json[..^1] + ",\"private_path\":\"C:\\\\Users\\\\secret\"}");

        var exception = Assert.Throws<AlertReceiptConsumerException>(() => AlertCenterReceiptConsumerV1.Validate(hostile));

        Assert.Equal("invalid_alert_receipt", exception.Code);
        Assert.Equal("Alert receipt is invalid.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("private", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublicSurface_IsSealedTypedAndLeavesFiveFieldConsumerUnchanged()
    {
        var existingValidate = Assert.Single(typeof(AlertReceiptConsumerV1).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));
        Assert.Equal("Validate", existingValidate.Name);
        Assert.Equal(typeof(AlertReceiptConsumerEnvelopeV1), existingValidate.ReturnType);

        var validate = Assert.Single(typeof(AlertCenterReceiptConsumerV1).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));
        Assert.Equal("Validate", validate.Name);
        Assert.Equal(typeof(ReadOnlySpan<byte>), Assert.Single(validate.GetParameters()).ParameterType);
        Assert.Equal(typeof(AlertCenterReceiptProjectionV1), validate.ReturnType);

        var projection = typeof(AlertCenterReceiptProjectionV1);
        Assert.True(projection.IsSealed);
        Assert.Empty(projection.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(
            [
                "AlertId", "Completeness", "CompletenessReasons", "ConfigurationHash", "ConfigurationVersion",
                "EffectiveThresholds", "EvaluationId", "EvaluationInputHash", "Evidence", "FirstObservedAt",
                "InitialState", "LastObservedAt", "ObservedValues", "RequiredCapabilities", "RuleId", "RuleVersion",
                "SessionId", "Severity", "SourceSurface", "SourceVersion", "Summary", "TraceId",
            ],
            projection.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.All(projection.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), property =>
        {
            Assert.True(property.CanRead);
            Assert.False(property.CanWrite);
        });
    }

    [Fact]
    public void SuppressionValidate_ExactCanonicalBytes_ReturnsTypedProjection()
    {
        var suppression = new AlertSuppression(
            new string('e', 64),
            "fixture-rule",
            "1",
            "missing_required_capability",
            ["tool-events", "tool-status"]);
        var bytes = AlertCanonicalJson.SerializeSuppression(suppression);

        var projection = AlertSuppressionConsumerV1.Validate(bytes);

        Assert.Equal(suppression.EvaluationId, projection.EvaluationId);
        Assert.Equal(suppression.RuleId, projection.RuleId);
        Assert.Equal(suppression.RuleVersion, projection.RuleVersion);
        Assert.Equal(suppression.Code, projection.Code);
        Assert.Equal(suppression.MissingCapabilities, projection.MissingCapabilities);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)projection.MissingCapabilities).Add("private-value"));
    }

    [Fact]
    public void SuppressionValidate_UnknownField_UsesFixedNoLeakFailure()
    {
        var suppression = new AlertSuppression(
            new string('e', 64),
            "fixture-rule",
            "1",
            "rule_disabled",
            []);
        var json = Encoding.UTF8.GetString(AlertCanonicalJson.SerializeSuppression(suppression));
        var bytes = Encoding.UTF8.GetBytes(json[..^1] + ",\"private_path\":\"C:\\\\Users\\\\secret\"}");

        var exception = Assert.Throws<AlertSuppressionConsumerException>(() => AlertSuppressionConsumerV1.Validate(bytes));

        Assert.Equal("invalid_alert_suppression", exception.Code);
        Assert.Equal("Alert suppression is invalid.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void EvaluationValidate_ExactCanonicalBytes_ReturnsTypedProjectionAndCounts()
    {
        var evaluation = EngineEvaluation();

        var projection = AlertEvaluationConsumerV1.Validate(AlertCanonicalJson.SerializeEvaluation(evaluation));

        Assert.Equal(evaluation.EvaluationId, projection.EvaluationId);
        Assert.Equal(evaluation.InputHash, projection.InputHash);
        Assert.Equal(evaluation.ConfigurationVersion, projection.ConfigurationVersion);
        Assert.Equal(evaluation.ConfigurationHash, projection.ConfigurationHash);
        Assert.Equal(evaluation.Receipts.Count, projection.ReceiptCount);
        Assert.Equal(evaluation.Suppressions.Count, projection.SuppressionCount);
    }

    [Fact]
    public void EvaluationValidate_UnknownField_UsesFixedNoLeakFailure()
    {
        var json = Encoding.UTF8.GetString(AlertCanonicalJson.SerializeEvaluation(EngineEvaluation()));
        var bytes = Encoding.UTF8.GetBytes(json[..^1] + ",\"private_path\":\"C:\\\\Users\\\\secret\"}");

        var exception = Assert.Throws<AlertEvaluationConsumerException>(() => AlertEvaluationConsumerV1.Validate(bytes));

        Assert.Equal("invalid_alert_evaluation", exception.Code);
        Assert.Equal("Alert evaluation is invalid.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    private static AlertReceipt EngineReceipt() => Assert.Single(EngineEvaluation().Receipts);

    private static AlertEvaluationResult EngineEvaluation()
    {
        var observed = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
        var evidence = new AlertEvidenceReference(
            AlertEvidenceKind.Event,
            "evidence-1",
            "session-1",
            "trace-1",
            "span-1",
            null,
            "event-1",
            null,
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
            [new("signal-1", AlertSignalKind.SessionEvent, 1, observed, null, AlertSignalStatus.Success, [], [], evidence)]);
        var descriptor = new AlertRuleDescriptor(
            "fixture-rule",
            "1",
            "Fixture summary",
            "Fixture description",
            ["tool-events"],
            AlertRuleScope.Session,
            [],
            "session",
            [new("count", "calls", AlertThresholdDirection.HigherIsWorse, 0, 10, 1, 2)],
            ["missing_required_capability", "rule_disabled", "source_not_applicable"],
            ["github-copilot"]);
        var match = new AlertRuleMatch(
            AlertSeverity.Warning,
            [new("count", "calls", 2)],
            [evidence],
            observed,
            observed.AddSeconds(1));
        return new AlertEvaluationEngine(
            new AlertRuleRegistry([new FixedRule(descriptor, match)]),
            new ExistingEvidenceResolver()).Evaluate(
                snapshot,
                new AlertEngineConfiguration(AlertContractVersions.Configuration, "fixture-v1", []));
    }

    private sealed class FixedRule(AlertRuleDescriptor descriptor, AlertRuleMatch match) : IAlertRule
    {
        public AlertRuleDescriptor Descriptor { get; } = descriptor;
        public AlertRuleOutcome Evaluate(AlertRuleContext context) => new([match], []);
    }

    private sealed class ExistingEvidenceResolver : IAlertEvidenceResolver
    {
        public bool Exists(AlertEvidenceReference reference) => true;
    }
}
