using System.Text;
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
}
