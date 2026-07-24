using System.Text;
using System.Security.Cryptography;
using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.Alerts.Tests;

public sealed class GoldenAlertReceiptTests
{
    [Fact]
    public void ReceiptV1_CanonicalBytes_MatchGoldenFixture()
    {
        var observed = new DateTimeOffset(2026, 7, 21, 1, 2, 3, TimeSpan.Zero);
        var evidence = new AlertEvidenceReference(AlertEvidenceKind.Event, "evidence-1", "session-1", "trace-1", "span-1", null, "event-1", null, observed);
        var receipt = new AlertReceipt(
            AlertContractVersions.Receipt, AlertContractVersions.SanitizedReceiptProfile, new string('a', 64), new string('e', 64),
            "fixture-rule", "1", AlertSeverity.Warning, AlertInitialState.Open, "github-copilot", "1.2.3", "session-1", "trace-1",
            [evidence], [new("count", "calls", 2)], [new("count.warning", "calls", 1)], "fixture-v1", new string('c', 64), ["tool-events"],
            AlertCompleteness.Partial, ["ingest_gap"], observed, observed.AddSeconds(1), new string('b', 64), "Fixture summary");
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "alert-receipt-v1.golden.json"), Encoding.UTF8).TrimEnd('\r', '\n');

        Assert.Equal(Encoding.UTF8.GetBytes(expected), AlertCanonicalJson.SerializeReceipt(receipt));
    }

    [Fact]
    public void ReceiptV1_CanonicalSerializerPreservesFrozenCompletenessReasonBehavior()
    {
        var receipt = Receipt() with { Completeness = AlertCompleteness.Full };

        var json = Encoding.UTF8.GetString(AlertCanonicalJson.SerializeReceipt(receipt));

        Assert.Contains("\"completeness\":\"full\"", json, StringComparison.Ordinal);
        Assert.Contains("\"completeness_reasons\":[\"ingest_gap\"]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ReceiptV2_CanonicalBytesHashesAndDerivedIdsMatchGoldenFixture()
    {
        var evaluation = AlertEngineV2Tests.Evaluation();
        var inputs = AlertEngineV2Tests.Inputs();
        var receiptBytes = AlertCanonicalJsonV2.SerializeReceipt(evaluation.Receipts[0]);
        var expected = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "TestData", "alert-receipt-v2.golden.json"),
            Encoding.UTF8).TrimEnd('\r', '\n');

        Assert.Equal(Encoding.UTF8.GetBytes(expected), receiptBytes);
        Assert.Equal(
            "6246121e02248fe9a550fe915dcb2b5c437e6823dfc45653f1133084a088373c",
            Sha256(AlertCanonicalJsonV2.SerializeSnapshot(inputs.Snapshot)));
        Assert.Equal(
            "6cc7eaf0e299930667dad2e91bb40e05115dd3d5edaf63cea6c052bc2c943b32",
            Sha256(AlertCanonicalJsonV2.SerializeConfiguration(inputs.Configuration)));
        Assert.Equal(
            "1628716584f4617e1cb8ef3c6e6f04f34dccf02356c77f3705d918f599bb8abc",
            Sha256(AlertCanonicalJsonV2.SerializeEvaluation(evaluation)));
        Assert.Equal(
            "e03251172ec0eab57426c007a95b99b60fec29517517dd67e35ca9d8559db88b",
            Sha256(receiptBytes));
        Assert.Equal(
            "5ebcfab264e97348f3c912e02a5b718bd6fe9f40db288990078a01b81bd27dd9",
            evaluation.EvaluationId);
        Assert.Equal(
            "282004010e1183a87ecae7d3cd83783084a4930ddf1e31042918c84cd0402959",
            evaluation.Receipts[0].AlertId);
        Assert.Equal(
            "7d6328e09fa20cdb9a4e2239eacd6d48f5f4081f4bf93ff711409a647b8e6fd9",
            evaluation.InputHash);
        Assert.Equal(
            "ec9a88dff00099ee72615aae5889a6f435bfc5fe441f763b0c07e4e06a13b256",
            evaluation.ConfigurationHash);
    }

    private static AlertReceipt Receipt()
    {
        var observed = new DateTimeOffset(2026, 7, 21, 1, 2, 3, TimeSpan.Zero);
        var evidence = new AlertEvidenceReference(AlertEvidenceKind.Event, "evidence-1", "session-1", "trace-1", "span-1", null, "event-1", null, observed);
        return new AlertReceipt(
            AlertContractVersions.Receipt, AlertContractVersions.SanitizedReceiptProfile, new string('a', 64), new string('e', 64),
            "fixture-rule", "1", AlertSeverity.Warning, AlertInitialState.Open, "github-copilot", "1.2.3", "session-1", "trace-1",
            [evidence], [new("count", "calls", 2)], [new("count.warning", "calls", 1)], "fixture-v1", new string('c', 64), ["tool-events"],
            AlertCompleteness.Partial, ["ingest_gap"], observed, observed.AddSeconds(1), new string('b', 64), "Fixture summary");
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
