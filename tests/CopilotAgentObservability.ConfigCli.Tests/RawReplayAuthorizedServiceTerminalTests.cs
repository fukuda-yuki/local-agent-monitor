using CopilotAgentObservability.RawReplay;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class RawReplayAuthorizedServiceTerminalTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DirectCliWritesFlushesAndInspectsTheStagedFileBeforeTheFileSeal()
    {
        using var directory = new TempDirectory();
        var events = new List<string>();
        var realInspector = new RawReplayArchiveService();
        var inspectCount = 0;
        var archiveService = new RawReplayArchiveService(
            output => output + ".owned.partial",
            bytes =>
            {
                events.Add(++inspectCount == 1 ? "archive_inspected" : "staged_inspected");
                return realInspector.Inspect(bytes);
            },
            checkpoint => events.Add(checkpoint == RawReplayFileStagingCheckpoint.BeforeWrite
                ? "write"
                : "flush"));
        var provider = new OrderedProvider(ValidSnapshot(), events);
        var output = Path.Combine(directory.Path, "raw-local-replay.zip");

        var result = await new RawReplayAuthorizedService(provider, archiveService)
            .CreateAndPublishAsync(ConfirmedControl(ValidSnapshot()), output);

        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal(
            ["archive_inspected", "write", "flush", "staged_inspected", "seal", "move_observed"],
            [.. events, File.Exists(output) ? "move_observed" : "move_missing"]);
    }

    [Theory]
    [InlineData("archive", "selection_empty")]
    [InlineData("credential", "credential_material_detected")]
    [InlineData("bounds", "entry_too_large")]
    [InlineData("inspection", "publish_validation_failed")]
    [InlineData("output_exists", "output_exists")]
    [InlineData("parent", "publish_failed")]
    [InlineData("temp", "publish_failed")]
    [InlineData("create", "publish_failed")]
    [InlineData("write", "publish_failed")]
    [InlineData("flush", "publish_failed")]
    [InlineData("staged_validation", "publish_validation_failed")]
    public async Task EveryDirectCliNonSealBranchCompletesWithoutRawBeforeItsFixedFailure(
        string scenario,
        string expectedError)
    {
        using var directory = new TempDirectory();
        var snapshot = ScenarioSnapshot(scenario);
        var terminalOperations = new List<RawReplaySnapshotTerminalOperation>();
        var provider = new Provider(snapshot, terminalOperations);
        var output = Path.Combine(
            scenario == "parent" ? Path.Combine(directory.Path, "missing") : directory.Path,
            "raw-local-replay.zip");
        var collision = output + ".owned.partial";
        if (scenario == "output_exists") File.WriteAllBytes(output, [9]);
        if (scenario == "create") File.WriteAllBytes(collision, [8]);
        var inspectCalls = 0;
        var realInspector = new RawReplayArchiveService();
        var archiveService = new RawReplayArchiveService(
            scenario == "temp"
                ? _ => Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.partial")
                : _ => collision,
            bytes =>
            {
                inspectCalls++;
                if (scenario == "inspection" && inspectCalls == 1
                    || scenario == "staged_validation" && inspectCalls == 2)
                    return new(false, "publish_validation_failed", null);
                return realInspector.Inspect(bytes);
            },
            checkpoint =>
            {
                if (scenario == "write" && checkpoint == RawReplayFileStagingCheckpoint.BeforeWrite
                    || scenario == "flush" && checkpoint == RawReplayFileStagingCheckpoint.BeforeFlush)
                    throw new IOException("synthetic staging failure");
            });
        var service = new RawReplayAuthorizedService(provider, archiveService);
        var control = ConfirmedControl(ValidSnapshot());

        var result = await service.CreateAndPublishAsync(control, output);

        Assert.False(result.Success);
        Assert.Equal(expectedError, result.ErrorCode);
        Assert.Null(result.ArchiveBytes);
        Assert.Null(result.ManifestBytes);
        Assert.Null(result.ArchiveSha256);
        Assert.Equal([RawReplaySnapshotTerminalOperation.CompleteWithoutRaw], terminalOperations);
        if (scenario == "create")
            Assert.Equal(new byte[] { 8 }, File.ReadAllBytes(collision));
        else
            Assert.False(File.Exists(collision));
        if (scenario != "output_exists") Assert.False(File.Exists(output));
    }

    private static RawReplaySnapshot ScenarioSnapshot(string scenario) => scenario switch
    {
        "archive" => ValidSnapshot() with { Records = [] },
        "credential" => ValidSnapshot() with
        {
            Records = [ValidSnapshot().Records[0] with { PayloadJson = "ghp_abcdefghijklmnopqrstuvwxyz" }],
        },
        "bounds" => ValidSnapshot() with
        {
            Records = [ValidSnapshot().Records[0] with
            {
                PayloadJson = $"{{\"resourceSpans\":[],\"padding\":\"{new string('x', RawReplayLimits.MaximumRawRecordBytes)}\"}}",
            }],
        },
        _ => ValidSnapshot(),
    };

    private static RawReplayExportControl ConfirmedControl(RawReplaySnapshot digestSnapshot)
    {
        var request = new RawReplayExportControl(
            RawReplayContractVersions.ExportControl,
            RawReplayContractVersions.BundleProfile,
            Now,
            new(RawRecordIds: [1]),
            false,
            false,
            null,
            null);
        var preview = new RawReplayArchiveService().Preview(digestSnapshot, request);
        return request with
        {
            PreviewDigest = preview.PreviewDigest,
            Consent = new(RawReplayContractVersions.BundleProfile, true, RawReplayConsent.RequiredPhrase),
        };
    }

    private static RawReplaySnapshot ValidSnapshot() => new(
        "snapshot",
        Now,
        "monitor-v1",
        [new RawReplayRecord(
            1,
            "raw-otlp",
            "trace-one",
            Now,
            null,
            "{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[{\"traceId\":\"trace-one\",\"spanId\":\"span\"}]}]}]}",
            1,
            new("copilot-cli", "1", "otlp-json", "adapter-v1", "schema-v1", new string('a', 64),
                "supported", "available", "not_applied_raw_capture", RawReplayContractVersions.CredentialScanner))],
        [],
        ["session_content_not_requested"]);

    private sealed class Provider(
        RawReplaySnapshot snapshot,
        List<RawReplaySnapshotTerminalOperation> terminalOperations) : IRawReplaySnapshotProvider
    {
        public ValueTask<RawReplaySnapshotCapture> CaptureAsync(
            RawReplaySelection selection,
            bool includeSessionContent,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RawReplaySnapshotCapture(true, null, new RawReplaySnapshotLease(
                snapshot,
                static () => ValueTask.CompletedTask,
                operation =>
                {
                    terminalOperations.Add(operation);
                    return operation == RawReplaySnapshotTerminalOperation.CompleteWithoutRaw
                        ? RawReplaySnapshotTerminalResult.CompletedWithoutRaw
                        : RawReplaySnapshotTerminalResult.Sealed;
                })));
    }

    private sealed class OrderedProvider(
        RawReplaySnapshot snapshot,
        List<string> events) : IRawReplaySnapshotProvider
    {
        public ValueTask<RawReplaySnapshotCapture> CaptureAsync(
            RawReplaySelection selection,
            bool includeSessionContent,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RawReplaySnapshotCapture(true, null, new RawReplaySnapshotLease(
                snapshot,
                static () => ValueTask.CompletedTask,
                operation =>
                {
                    Assert.Equal(RawReplaySnapshotTerminalOperation.SealFilePublication, operation);
                    events.Add("seal");
                    return RawReplaySnapshotTerminalResult.Sealed;
                })));
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"raw-replay-authority-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
