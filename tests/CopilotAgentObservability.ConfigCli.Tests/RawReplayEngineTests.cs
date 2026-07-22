using CopilotAgentObservability.RawReplay;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class RawReplayEngineTests
{
    [Fact]
    public void Replay_is_byte_deterministic_preserves_ids_and_invokes_no_external_model()
    {
        var archive = Archive();
        var engine = new RawReplayEngine();

        var first = engine.Replay("replay-test-01", archive);
        var second = engine.Replay("replay-test-01", archive);

        Assert.True(first.Success);
        Assert.Equal(first.ResultBytes, second.ResultBytes);
        Assert.Equal(first.NormalizedBytes, second.NormalizedBytes);
        Assert.Equal(first.ProjectionBytes, second.ProjectionBytes);
        Assert.Equal(first.DashboardBytes, second.DashboardBytes);
        Assert.Equal(0, first.Result!.ExternalModelInvocations);
        Assert.Equal([11L], first.StagedRecords.Select(record => record.RawRecordId));
        Assert.Equal("raw-measurement-normalization.v1", first.Result.NormalizationVersion);
    }

    [Fact]
    public void Replay_rejects_version_mismatch_and_same_namespace_different_request()
    {
        var archive = Archive();
        var engine = new RawReplayEngine();
        var completed = engine.Replay("replay-test-02", archive);

        Assert.Equal("normalization_version_mismatch", engine.Replay("replay-test-03", archive, RawReplayContractVersions.Normalization + "-new").ErrorCode);
        Assert.Equal("replay_id_conflict", engine.Replay("replay-test-02", Archive("trace-other"), existing: completed.Result).ErrorCode);
        Assert.True(engine.Replay("replay-test-02", archive, existing: completed.Result).IdempotentReplay);
    }

    private static byte[] Archive(string traceId = "trace-a")
    {
        var service = new RawReplayArchiveService();
        var record = new RawReplayRecord(
            11, "raw-otlp", traceId, new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero), "{}",
            Payload(traceId), 1,
            new("github-copilot-cli", "1.0", "otlp-json", "adapter-v1", "schema-v1", new string('b', 64), "supported", "available", "not_applied_raw_otlp", "raw-replay-credential-scan.v1"));
        var snapshot = new RawReplaySnapshot("snapshot", new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero), "monitor-v1", [record], [], ["session_event_content_not_requested"]);
        var request = new RawReplayExportControl(RawReplayContractVersions.ExportControl, RawReplayContractVersions.BundleProfile, new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero), new(RawRecordIds: [11]), false, false, null, null);
        var preview = service.Preview(snapshot, request);
        var result = service.Create(snapshot, request with { PreviewDigest = preview.PreviewDigest, Consent = new(RawReplayContractVersions.BundleProfile, true, RawReplayConsent.RequiredPhrase) });
        Assert.True(result.Success, result.ErrorCode);
        return result.ArchiveBytes!;
    }

    private static string Payload(string traceId) => System.Text.Json.JsonSerializer.Serialize(new
    {
        resourceSpans = new[]
        {
            new { scopeSpans = new[] { new { spans = new[] { new { traceId, spanId = "span-a", name = "chat" } } } } },
        },
    });
}
