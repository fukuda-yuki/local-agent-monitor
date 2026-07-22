using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.Alerts.Tests;

public sealed class AlertReceiptConsumerV1Tests
{
    private const string ExpectedHistoricalSerializerGoldenSha256 = "21103e968f2a4eaf90caae9a8a5915c279a44941de63e54316eddb1882d33dd6";
    private const string ExpectedEngineGoldenSha256 = "d9401f5829b3c0d4c2cdcf457f539ae9f2ef9f8396db602e14c3b1341c170cca";

    [Fact]
    public void Validate_AcceptsExactEngineProducedBytesAndReturnsBoundedProjection()
    {
        var receipt = EngineReceipt();
        var bytes = AlertCanonicalJson.SerializeReceipt(receipt);

        var result = AlertReceiptConsumerV1.Validate(bytes);

        var actualSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.True(string.Equals(ExpectedEngineGoldenSha256, actualSha256, StringComparison.Ordinal), $"Actual engine golden SHA-256: {actualSha256}");
        Assert.Equal(receipt.AlertId, result.AlertId);
        Assert.Equal(receipt.SessionId, result.SessionId);
        Assert.Equal(receipt.TraceId, result.TraceId);
        Assert.Equal(receipt.SourceSurface, result.SourceSurface);
        Assert.Equal(receipt.LastObservedAt, result.LastObservedAt);
    }

    [Fact]
    public void Validate_RejectsHistoricalSerializerOnlyGoldenButPreservesItsExactHash()
    {
        var bytes = HistoricalSerializerGoldenBytes();

        var exception = Assert.Throws<AlertReceiptConsumerException>(() => AlertReceiptConsumerV1.Validate(bytes));

        Assert.Equal(ExpectedHistoricalSerializerGoldenSha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.Equal("invalid_alert_receipt", exception.Code);
        Assert.Equal("Alert receipt is invalid.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void PublicSurface_ExposesOnlyValidateBoundedProjectionAndFixedFailure()
    {
        var validate = Assert.Single(typeof(AlertReceiptConsumerV1).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));
        Assert.Equal("Validate", validate.Name);
        Assert.Equal(typeof(ReadOnlySpan<byte>), Assert.Single(validate.GetParameters()).ParameterType);
        Assert.Equal(typeof(AlertReceiptConsumerEnvelopeV1), validate.ReturnType);

        var envelope = typeof(AlertReceiptConsumerEnvelopeV1);
        Assert.True(envelope.IsPublic);
        Assert.True(envelope.IsSealed);
        Assert.Empty(envelope.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(
            ["AlertId", "LastObservedAt", "SessionId", "SourceSurface", "TraceId"],
            envelope.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.All(envelope.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), property =>
        {
            Assert.True(property.CanRead);
            Assert.False(property.CanWrite);
        });
        Assert.Empty(envelope.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));

        var failure = typeof(AlertReceiptConsumerException);
        Assert.True(failure.IsPublic);
        Assert.True(failure.IsSealed);
        Assert.Empty(failure.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(["Code"], failure.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(property => property.Name));
    }

    [Theory]
    [MemberData(nameof(InvalidReceipts))]
    public void Validate_RejectsSchemaCanonicalAndSemanticViolationsWithOneNoLeakFailure(string _, byte[] bytes)
    {
        var exception = Assert.Throws<AlertReceiptConsumerException>(() => AlertReceiptConsumerV1.Validate(bytes));

        Assert.Equal("invalid_alert_receipt", exception.Code);
        Assert.Equal("Alert receipt is invalid.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Validate_RejectsInputAboveConsumerCeilingBeforeParsing()
    {
        var bytes = new byte[8_388_609];
        bytes.AsSpan().Fill((byte)'x');

        var exception = Assert.Throws<AlertReceiptConsumerException>(() => AlertReceiptConsumerV1.Validate(bytes));

        Assert.Equal("invalid_alert_receipt", exception.Code);
        Assert.Equal("Alert receipt is invalid.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Validate_RejectsJsonDeeperThanExactReceiptShapeWithoutParserDetails()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"schema_version\":{\"nested\":{\"too\":{\"deep\":true}}}}");

        var exception = Assert.Throws<AlertReceiptConsumerException>(() => AlertReceiptConsumerV1.Validate(bytes));

        Assert.Equal("invalid_alert_receipt", exception.Code);
        Assert.Equal("Alert receipt is invalid.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    public static IEnumerable<object[]> InvalidReceipts()
    {
        var json = Encoding.UTF8.GetString(AlertCanonicalJson.SerializeReceipt(EngineReceipt()));
        var evidence = RawArrayItem(json, "evidence");
        var observed = RawArrayItem(json, "observed_values");
        var threshold = RawArrayItem(json, "effective_thresholds");

        yield return Case("empty", []);
        yield return Case("malformed", Encoding.UTF8.GetBytes("{\"sensitive-marker\":"));
        yield return Case("invalid-utf8", [0xff]);
        yield return Case("trailing-json", Encoding.UTF8.GetBytes(json + "{}"));
        yield return JsonCase("unknown-root", json[..^1] + ",\"unexpected\":true}");
        yield return JsonCase("duplicate-root", json.Replace("{\"schema_version\":\"alert.receipt.v1\",", "{\"schema_version\":\"alert.receipt.v1\",\"schema_version\":\"alert.receipt.v1\",", StringComparison.Ordinal));
        yield return JsonCase("unknown-evidence", json.Replace("\"kind\":\"event\",", "\"kind\":\"event\",\"unexpected\":true,", StringComparison.Ordinal));
        yield return JsonCase("duplicate-evidence", json.Replace("\"kind\":\"event\",", "\"kind\":\"event\",\"kind\":\"event\",", StringComparison.Ordinal));
        yield return JsonCase("noncanonical-whitespace", " " + json);
        yield return JsonCase("trailing-newline", json + "\n");
        yield return JsonCase("noncanonical-root-order", json.Replace("{\"schema_version\":\"alert.receipt.v1\",\"sanitized_export_profile\":\"sanitized-alert-receipt.v1\"", "{\"sanitized_export_profile\":\"sanitized-alert-receipt.v1\",\"schema_version\":\"alert.receipt.v1\"", StringComparison.Ordinal));
        yield return JsonCase("noncanonical-decimal", json.Replace("\"value\":2", "\"value\":2.0", StringComparison.Ordinal));
        yield return JsonCase("noncanonical-timestamp", json.Replace("2026-07-21T01:02:04.0000000Z", "2026-07-21T01:02:04.0000000+00:00", StringComparison.Ordinal));
        yield return JsonCase("noncanonical-escape", json.Replace("Fixture summary", "Fixture\\u0020summary", StringComparison.Ordinal));
        yield return JsonCase("incomplete-surrogate", json.Replace("Fixture summary", "\\uD800", StringComparison.Ordinal));
        yield return JsonCase("receipt-version", json.Replace("alert.receipt.v1", "alert.receipt.v2", StringComparison.Ordinal));
        yield return JsonCase("profile-version", json.Replace("sanitized-alert-receipt.v1", "sanitized-alert-receipt.v2", StringComparison.Ordinal));
        yield return JsonCase("severity", json.Replace("\"severity\":\"warning\"", "\"severity\":\"severe\"", StringComparison.Ordinal));
        yield return JsonCase("initial-state", json.Replace("\"initial_state\":\"open\"", "\"initial_state\":\"closed\"", StringComparison.Ordinal));
        yield return JsonCase("completeness-enum", json.Replace("\"completeness\":\"partial\"", "\"completeness\":\"complete\"", StringComparison.Ordinal));
        yield return JsonCase("evidence-kind", json.Replace("\"kind\":\"event\"", "\"kind\":\"raw\"", StringComparison.Ordinal));
        yield return JsonCase("short-alert-id", ReplacePropertyString(json, "alert_id", PropertyString(json, "alert_id")[..63]));
        yield return JsonCase("uppercase-hash", ReplacePropertyString(json, "configuration_hash", PropertyString(json, "configuration_hash").ToUpperInvariant()));
        yield return JsonCase("invalid-rule-token", json.Replace("\"rule_id\":\"fixture-rule\"", "\"rule_id\":\"Fixture Rule\"", StringComparison.Ordinal));
        yield return JsonCase("invalid-session-id", json.Replace("\"session_id\":\"session-1\"", "\"session_id\":\"private/path\"", StringComparison.Ordinal));
        yield return JsonCase("summary-empty", json.Replace("\"summary\":\"Fixture summary\"", "\"summary\":\"\"", StringComparison.Ordinal));
        yield return JsonCase("summary-oversize", json.Replace("Fixture summary", new string('s', 161), StringComparison.Ordinal));
        yield return JsonCase("empty-evidence", ReplaceArray(json, "evidence", "[]"));
        yield return JsonCase("empty-observed", ReplaceArray(json, "observed_values", "[]"));
        yield return JsonCase("evidence-session-scope", json.Replace("\"evidence_id\":\"evidence-1\",\"session_id\":\"session-1\"", "\"evidence_id\":\"evidence-1\",\"session_id\":\"session-2\"", StringComparison.Ordinal));
        yield return JsonCase("evidence-trace-scope", json.Replace("\"evidence_id\":\"evidence-1\",\"session_id\":\"session-1\",\"trace_id\":\"trace-1\"", "\"evidence_id\":\"evidence-1\",\"session_id\":\"session-1\",\"trace_id\":\"trace-2\"", StringComparison.Ordinal));
        yield return JsonCase("evidence-required-id", json.Replace("\"event_id\":\"event-1\"", "\"event_id\":null", StringComparison.Ordinal));
        yield return JsonCase("receipt-time-order", json.Replace("\"last_observed_at\":\"2026-07-21T01:02:04.0000000Z\"", "\"last_observed_at\":\"2026-07-21T01:02:02.0000000Z\"", StringComparison.Ordinal));
        yield return JsonCase("duplicate-evidence-identity", ReplaceArray(json, "evidence", $"[{evidence},{evidence}]"));
        yield return JsonCase("duplicate-observed-identity", ReplaceArray(json, "observed_values", $"[{observed},{observed}]"));
        yield return JsonCase("duplicate-threshold-identity", ReplaceArray(json, "effective_thresholds", $"[{threshold},{threshold}]"));
        yield return JsonCase("duplicate-capability", json.Replace("[\"tool-events\"]", "[\"tool-events\",\"tool-events\"]", StringComparison.Ordinal));
        yield return JsonCase("capability-order", json.Replace("[\"tool-events\"]", "[\"z-capability\",\"a-capability\"]", StringComparison.Ordinal));
        yield return JsonCase("observed-order", ReplaceArray(json, "observed_values", $"[{{\"name\":\"z-count\",\"unit\":\"calls\",\"value\":1}},{observed}]"));
        yield return JsonCase("completeness-reason-order", json.Replace("[\"ingest_gap\"]", "[\"ingest_gap\",\"missing_trace_context\"]", StringComparison.Ordinal));
        yield return JsonCase("unknown-completeness-reason", json.Replace("[\"ingest_gap\"]", "[\"unknown_reason\"]", StringComparison.Ordinal));
        yield return JsonCase("full-with-reason", json.Replace("\"completeness\":\"partial\"", "\"completeness\":\"full\"", StringComparison.Ordinal));
        yield return JsonCase("rich-with-partial-ceiling", json.Replace("\"completeness\":\"partial\",\"completeness_reasons\":[\"ingest_gap\"]", "\"completeness\":\"rich\",\"completeness_reasons\":[\"historical_summary_only\"]", StringComparison.Ordinal));
        yield return JsonCase("partial-with-unbound-ceiling", json.Replace("[\"ingest_gap\"]", "[\"planned_source_not_enabled\"]", StringComparison.Ordinal));
    }

    private static object[] Case(string name, byte[] bytes) => [name, bytes];
    private static object[] JsonCase(string name, string json) => Case(name, Encoding.UTF8.GetBytes(json));

    private static byte[] HistoricalSerializerGoldenBytes() => Encoding.UTF8.GetBytes(HistoricalSerializerGoldenJson());

    private static string HistoricalSerializerGoldenJson() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "TestData", "alert-receipt-v1.golden.json"),
        Encoding.UTF8).TrimEnd('\r', '\n');

    private static string RawArrayItem(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(property).EnumerateArray().First().GetRawText();
    }

    private static string ReplaceArray(string json, string property, string replacement)
    {
        using var document = JsonDocument.Parse(json);
        var raw = document.RootElement.GetProperty(property).GetRawText();
        return json.Replace($"\"{property}\":{raw}", $"\"{property}\":{replacement}", StringComparison.Ordinal);
    }

    private static string PropertyString(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(property).GetString()!;
    }

    private static string ReplacePropertyString(string json, string property, string replacement)
    {
        var current = PropertyString(json, property);
        return json.Replace($"\"{property}\":\"{current}\"", $"\"{property}\":\"{replacement}\"", StringComparison.Ordinal);
    }

    private static AlertReceipt EngineReceipt()
    {
        var observed = new DateTimeOffset(2026, 7, 21, 1, 2, 3, TimeSpan.Zero);
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
        var engine = new AlertEvaluationEngine(new AlertRuleRegistry([new FixedRule(descriptor, match)]), new ExistingEvidenceResolver());
        return Assert.Single(engine.Evaluate(
            snapshot,
            new AlertEngineConfiguration(AlertContractVersions.Configuration, "fixture-v1", [])).Receipts);
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
