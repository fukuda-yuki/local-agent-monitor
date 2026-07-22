using CopilotAgentObservability.ConfigCli;
using CopilotAgentObservability.RawReplay;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class RawReplayCliTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PreviewExportAndResult_UseStrictRawProfileAndReleaseSnapshotLease()
    {
        using var fixture = new Fixture();
        var provider = new Provider(Snapshot());
        var request = Control();
        File.WriteAllBytes(fixture.Request, RawReplayJson.Serialize(request));
        using var previewOut = new StringWriter(); using var errors = new StringWriter();

        Assert.Equal(0, RawReplayCli.Run(["preview", "--database", fixture.Database, "--request", fixture.Request], previewOut, errors, provider));
        var preview = RawReplayJson.DeserializeExact<RawReplayPreview>(System.Text.Encoding.UTF8.GetBytes(previewOut.ToString().Trim()));
        Assert.True(preview.Success);
        File.WriteAllBytes(fixture.Request, RawReplayJson.Serialize(request with
        {
            PreviewDigest = preview.PreviewDigest,
            Consent = new(RawReplayContractVersions.BundleProfile, true, RawReplayConsent.RequiredPhrase),
        }));
        using var exportOut = new StringWriter();
        Assert.Equal(0, RawReplayCli.Run(["export", "--database", fixture.Database, "--request", fixture.Request, "--output", fixture.Bundle], exportOut, errors, provider));
        Assert.True(File.Exists(fixture.Bundle));
        using var resultOut = new StringWriter();
        Assert.Equal(0, RawReplayCli.Run(["result", "--bundle", fixture.Bundle], resultOut, errors));
        var inspection = RawReplayJson.DeserializeExact<RawReplayInspection>(System.Text.Encoding.UTF8.GetBytes(resultOut.ToString().Trim()));
        Assert.True(inspection.Success);
        Assert.Equal(2, provider.ReleaseCount);
    }

    [Fact]
    public void Preview_SanitizedOnlyFailsWithoutCapturingRawData()
    {
        using var fixture = new Fixture();
        var provider = new Provider(Snapshot());
        File.WriteAllBytes(fixture.Request, RawReplayJson.Serialize(Control() with { SanitizedOnly = true }));
        using var output = new StringWriter(); using var errors = new StringWriter();

        Assert.Equal(2, RawReplayCli.Run(["preview", "--database", fixture.Database, "--request", fixture.Request], output, errors, provider));
        Assert.Contains("sanitized_only_denied", errors.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, provider.CaptureCount);
    }

    [Fact]
    public void Preview_MissingDatabaseReturnsFixedErrorWithoutPathLeakage()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Request, RawReplayJson.Serialize(Control()));
        using var output = new StringWriter(); using var errors = new StringWriter();

        Assert.Equal(2, RawReplayCli.Run(["preview", "--database", fixture.Database, "--request", fixture.Request], output, errors));

        Assert.Equal("snapshot_store_unavailable", errors.ToString().Trim());
        Assert.DoesNotContain(fixture.Root, errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_RejectsDuplicateControlProperties()
    {
        using var fixture = new Fixture();
        var json = RawReplayJson.Text(Control());
        File.WriteAllText(fixture.Request, json.Replace(
            "\"schema_version\":",
            "\"schema_version\":\"raw-local-replay-export-control.v1\",\"schema_version\":",
            StringComparison.Ordinal));
        using var output = new StringWriter(); using var errors = new StringWriter();

        Assert.Equal(2, RawReplayCli.Run(["preview", "--database", fixture.Database, "--request", fixture.Request], output, errors, new Provider(Snapshot())));
        Assert.Equal("request_invalid", errors.ToString().Trim());
    }

    [Fact]
    public void Preview_RejectsSelectionWithAMissingClosedPropertyBeforeCapture()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Request, RawReplayJson.Text(Control()).Replace("\"sources\":null,", string.Empty, StringComparison.Ordinal));
        var provider = new Provider(Snapshot());
        using var output = new StringWriter(); using var errors = new StringWriter();

        Assert.Equal(2, RawReplayCli.Run(["preview", "--database", fixture.Database, "--request", fixture.Request], output, errors, provider));
        Assert.Equal("request_invalid", errors.ToString().Trim());
        Assert.Equal(0, provider.CaptureCount);
    }

    [Theory]
    [InlineData(false, null, "consent_required")]
    [InlineData(true, "not-a-digest", "preview_changed")]
    public void Export_RejectsInvalidCommitAuthorityBeforeCapturingRawData(bool includeConsent, string? digest, string expectedError)
    {
        using var fixture = new Fixture();
        var provider = new Provider(Snapshot());
        var control = Control() with
        {
            PreviewDigest = digest,
            Consent = includeConsent
                ? new(RawReplayContractVersions.BundleProfile, true, RawReplayConsent.RequiredPhrase)
                : null,
        };
        File.WriteAllBytes(fixture.Request, RawReplayJson.Serialize(control));
        using var output = new StringWriter(); using var errors = new StringWriter();

        Assert.Equal(2, RawReplayCli.Run(
            ["export", "--database", fixture.Database, "--request", fixture.Request, "--output", fixture.Bundle],
            output, errors, provider));
        Assert.Equal(expectedError, errors.ToString().Trim());
        Assert.Equal(0, provider.CaptureCount);
        Assert.False(File.Exists(fixture.Bundle));
    }

    [Fact]
    public void Export_RejectsUnsafeOutputNameBeforeCapturingRawData()
    {
        using var fixture = new Fixture();
        var provider = new Provider(Snapshot());
        File.WriteAllBytes(fixture.Request, RawReplayJson.Serialize(Control() with
        {
            PreviewDigest = new string('0', 64),
            Consent = new(RawReplayContractVersions.BundleProfile, true, RawReplayConsent.RequiredPhrase),
        }));
        var unsafeOutput = Path.Combine(fixture.Root, "session-one.zip");
        using var output = new StringWriter(); using var errors = new StringWriter();

        Assert.Equal(2, RawReplayCli.Run(
            ["export", "--database", fixture.Database, "--request", fixture.Request, "--output", unsafeOutput],
            output, errors, provider));
        Assert.Equal("output_name_invalid", errors.ToString().Trim());
        Assert.Equal(0, provider.CaptureCount);
        Assert.False(File.Exists(unsafeOutput));
    }

    [Fact]
    public void PreviewAndExport_MapProviderControlledErrorTextToAFixedCodeWithoutLeakage()
    {
        using var fixture = new Fixture();
        const string hostile = "private\"\nprovider-code";
        File.WriteAllBytes(fixture.Request, RawReplayJson.Serialize(Control()));
        using var output = new StringWriter();
        using var errors = new StringWriter();

        var exit = RawReplayCli.Run(
            ["preview", "--database", fixture.Database, "--request", fixture.Request],
            output,
            errors,
            new FailingProvider(hostile));

        Assert.Equal(2, exit);
        Assert.Equal("snapshot_store_unavailable", errors.ToString().Trim());
        var preview = RawReplayJson.DeserializeExact<RawReplayPreview>(System.Text.Encoding.UTF8.GetBytes(output.ToString().Trim()));
        Assert.Equal("snapshot_store_unavailable", preview.ErrorCode);
        Assert.DoesNotContain("private", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private", errors.ToString(), StringComparison.Ordinal);

        File.WriteAllBytes(fixture.Request, RawReplayJson.Serialize(Control() with
        {
            PreviewDigest = new string('0', 64),
            Consent = new(RawReplayContractVersions.BundleProfile, true, RawReplayConsent.RequiredPhrase),
        }));
        using var exportOutput = new StringWriter();
        using var exportErrors = new StringWriter();
        var exportExit = RawReplayCli.Run(
            ["export", "--database", fixture.Database, "--request", fixture.Request, "--output", fixture.Bundle],
            exportOutput,
            exportErrors,
            new FailingProvider(hostile));

        Assert.Equal(2, exportExit);
        Assert.Equal("snapshot_store_unavailable", exportErrors.ToString().Trim());
        using var exportJson = System.Text.Json.JsonDocument.Parse(exportOutput.ToString());
        Assert.Equal("snapshot_store_unavailable", exportJson.RootElement.GetProperty("error_code").GetString());
        Assert.DoesNotContain("private", exportOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private", exportErrors.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.Bundle));
    }

    [Fact]
    public void CliApplication_dispatches_the_real_raw_replay_result_command()
    {
        using var fixture = new Fixture();
        var service = new RawReplayArchiveService();
        var control = Control();
        var preview = service.Preview(Snapshot(), control);
        var created = service.Create(Snapshot(), control with
        {
            PreviewDigest = preview.PreviewDigest,
            Consent = new(RawReplayContractVersions.BundleProfile, true, RawReplayConsent.RequiredPhrase),
        });
        Assert.True(created.Success, created.ErrorCode);
        File.WriteAllBytes(fixture.Bundle, created.ArchiveBytes!);
        using var output = new StringWriter();
        using var errors = new StringWriter();

        var exit = CliApplication.Run(
            ["raw-replay", "result", "--bundle", fixture.Bundle],
            output,
            errors);

        Assert.Equal(0, exit);
        Assert.Empty(errors.ToString());
        var inspection = RawReplayJson.DeserializeExact<RawReplayInspection>(System.Text.Encoding.UTF8.GetBytes(output.ToString().Trim()));
        Assert.True(inspection.Success, inspection.ErrorCode);
    }

    private static RawReplayExportControl Control() => new(RawReplayContractVersions.ExportControl,
        RawReplayContractVersions.BundleProfile, Now, new(RawRecordIds: [1]), false, false, null, null);

    private static RawReplaySnapshot Snapshot() => new("snapshot", Now, "monitor-v1",
        [new(1, "raw-otlp", "trace-one", Now, null,
            "{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[{\"traceId\":\"trace-one\",\"spanId\":\"span\"}]}]}]}", 1,
            new("copilot-cli", "1", "otlp-json", "adapter-v1", "schema-v1", new string('a', 64), "supported", "available", "not_applied_raw_capture", RawReplayContractVersions.CredentialScanner))],
        [], ["session_content_not_requested"]);

    private sealed class Provider(RawReplaySnapshot snapshot) : IRawReplaySnapshotProvider
    {
        public int CaptureCount { get; private set; }
        public int ReleaseCount { get; private set; }
        public ValueTask<RawReplaySnapshotCapture> CaptureAsync(RawReplaySelection selection, bool includeSessionContent, CancellationToken cancellationToken)
        {
            CaptureCount++;
            return ValueTask.FromResult(new RawReplaySnapshotCapture(true, null,
                new RawReplaySnapshotLease(snapshot, () => { ReleaseCount++; return ValueTask.CompletedTask; })));
        }
    }

    private sealed class FailingProvider(string code) : IRawReplaySnapshotProvider
    {
        public ValueTask<RawReplaySnapshotCapture> CaptureAsync(
            RawReplaySelection selection,
            bool includeSessionContent,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RawReplaySnapshotCapture(false, code, null));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"raw-replay-cli-{Guid.NewGuid():N}"); Directory.CreateDirectory(Root);
        }
        public string Root { get; }
        public string Database => Path.Combine(Root, "monitor.db");
        public string Request => Path.Combine(Root, "request.json");
        public string Bundle => Path.Combine(Root, "raw-local-replay.zip");
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
