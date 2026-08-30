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
            ["archive_inspected", "write", "flush", "staged_inspected", "reference_released", "seal", "move_observed"],
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

    [Fact]
    public async Task DirectCliNonSealDeletionFailureRetainsCleanupUntilTheExactStagedPathIsRemoved()
    {
        using var directory = new TempDirectory();
        var stagedPath = Path.Combine(directory.Path, "raw-local-replay.zip.owned.partial");
        var deleteAttempts = 0;
        using var firstDelete = new ManualResetEventSlim();
        using var dispatcherEntered = new ManualResetEventSlim();
        using var secondDelete = new ManualResetEventSlim();
        using var timeProvider = new ObservingSystemTimeProvider();
        var archiveService = ArchiveService(
            stagedPath,
            deleteFile: path =>
            {
                if (Interlocked.Increment(ref deleteAttempts) == 1)
                {
                    firstDelete.Set();
                    throw new IOException("synthetic delete contention");
                }
                dispatcherEntered.Set();
                secondDelete.Set();
                File.Delete(path);
            },
            inspectArchive: bytes => new RawReplayArchiveService().Inspect(bytes),
            checkpoint: checkpoint =>
            {
                if (checkpoint == RawReplayFileStagingCheckpoint.BeforeFlush)
                    throw new IOException("synthetic non-seal failure");
            },
            timeProvider: timeProvider);

        var result = await new RawReplayAuthorizedService(
            new Provider(ValidSnapshot(), []),
            archiveService).CreateAndPublishAsync(
                ConfirmedControl(ValidSnapshot()),
                Path.Combine(directory.Path, "raw-local-replay.zip"));

        Assert.False(result.Success);
        Assert.Equal("publish_failed", result.ErrorCode);
        Assert.True(
            SpinWait.SpinUntil(() => !File.Exists(stagedPath), TimeSpan.FromSeconds(5)),
            RawReplayCheckpoints(
                "non-seal", firstDelete, timeProvider, dispatcherEntered, secondDelete,
                deleteAttempts, stagedPath));
        Assert.True(deleteAttempts >= 2);
    }

    [Theory]
    [InlineData((int)RawReplaySnapshotTerminalResult.Lost)]
    [InlineData((int)RawReplaySnapshotTerminalResult.Busy)]
    public async Task DirectCliSealLossDeletionFailureRetainsCleanupUntilTheExactStagedPathIsRemoved(int terminalResultValue)
    {
        using var directory = new TempDirectory();
        var stagedPath = Path.Combine(directory.Path, "raw-local-replay.zip.owned.partial");
        var deleteAttempts = 0;
        using var firstDelete = new ManualResetEventSlim();
        using var dispatcherEntered = new ManualResetEventSlim();
        using var secondDelete = new ManualResetEventSlim();
        using var timeProvider = new ObservingSystemTimeProvider();
        var archiveService = ArchiveService(
            stagedPath,
            deleteFile: path =>
            {
                if (Interlocked.Increment(ref deleteAttempts) == 1)
                {
                    firstDelete.Set();
                    throw new UnauthorizedAccessException("synthetic delete contention");
                }
                dispatcherEntered.Set();
                secondDelete.Set();
                File.Delete(path);
            },
            timeProvider: timeProvider);
        var provider = new TerminalProvider(ValidSnapshot(), (RawReplaySnapshotTerminalResult)terminalResultValue);

        var result = await new RawReplayAuthorizedService(provider, archiveService).CreateAndPublishAsync(
            ConfirmedControl(ValidSnapshot()),
            Path.Combine(directory.Path, "raw-local-replay.zip"));

        Assert.False(result.Success);
        Assert.Equal(
            (RawReplaySnapshotTerminalResult)terminalResultValue == RawReplaySnapshotTerminalResult.Busy
                ? "snapshot_store_busy"
                : "snapshot_read_denied",
            result.ErrorCode);
        Assert.True(
            SpinWait.SpinUntil(() => !File.Exists(stagedPath), TimeSpan.FromSeconds(5)),
            RawReplayCheckpoints(
                ((RawReplaySnapshotTerminalResult)terminalResultValue).ToString(),
                firstDelete,
                timeProvider,
                dispatcherEntered,
                secondDelete,
                deleteAttempts,
                stagedPath));
        Assert.True(deleteAttempts >= 2);
    }

    [Fact]
    public void StagedFileDoesNotReachIrreversibleDisposalBeforeDeletionIsConfirmed()
    {
        using var directory = new TempDirectory();
        var stagedPath = Path.Combine(directory.Path, "raw-local-replay.zip.owned.partial");
        File.WriteAllBytes(stagedPath, [1, 2, 3]);
        var deleteAttempts = 0;
        var staged = new RawReplayStagedFile(
            stagedPath,
            Path.Combine(directory.Path, "raw-local-replay.zip"),
            path =>
            {
                if (Interlocked.Increment(ref deleteAttempts) == 1) throw new IOException("synthetic delete contention");
                File.Delete(path);
            },
            new InertTimeProvider());

        staged.Dispose();
        Assert.True(File.Exists(stagedPath));

        staged.Dispose();

        Assert.False(File.Exists(stagedPath));
        Assert.Equal(2, deleteAttempts);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("loss")]
    [InlineData("busy")]
    [InlineData("non_seal")]
    [InlineData("exception")]
    public async Task DirectCliZeroesEveryOwnedRawBufferOnEveryPostGrantExit(string scenario)
    {
        using var directory = new TempDirectory();
        var ownedBuffers = new List<byte[]>();
        var inspectCalls = 0;
        var realInspector = new RawReplayArchiveService();
        var output = Path.Combine(directory.Path, "raw-local-replay.zip");
        if (scenario == "non_seal") File.WriteAllBytes(output, [9]);
        var archiveService = ArchiveService(
            output + ".owned.partial",
            inspectArchive: bytes =>
            {
                if (scenario == "exception" && Interlocked.Increment(ref inspectCalls) == 2)
                    throw new IOException("synthetic staged inspection failure");
                return realInspector.Inspect(bytes);
            },
            rawBufferObserver: bytes => ownedBuffers.Add(bytes));
        var terminal = scenario switch
        {
            "loss" => RawReplaySnapshotTerminalResult.Lost,
            "busy" => RawReplaySnapshotTerminalResult.Busy,
            _ => RawReplaySnapshotTerminalResult.Sealed,
        };

        _ = await new RawReplayAuthorizedService(
            new TerminalProvider(ValidSnapshot(), terminal),
            archiveService).CreateAndPublishAsync(ConfirmedControl(ValidSnapshot()), output);

        Assert.NotEmpty(ownedBuffers);
        Assert.All(ownedBuffers, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
    }

    private static RawReplayArchiveService ArchiveService(
        string stagedPath,
        Action<string>? deleteFile = null,
        Func<byte[], RawReplayInspection>? inspectArchive = null,
        Action<RawReplayFileStagingCheckpoint>? checkpoint = null,
        Action<byte[]>? rawBufferObserver = null,
        TimeProvider? timeProvider = null) =>
        new(
            _ => stagedPath,
            inspectArchive,
            checkpoint,
            deleteFile,
            timeProvider ?? TimeProvider.System,
            rawBufferObserver);

    private static string RawReplayCheckpoints(
        string terminalResult,
        ManualResetEventSlim firstDelete,
        ObservingSystemTimeProvider timeProvider,
        ManualResetEventSlim dispatcherEntered,
        ManualResetEventSlim secondDelete,
        int deleteAttempts,
        string stagedPath) =>
        $"terminal-result={terminalResult} harness-ready=true first-delete={firstDelete.IsSet} " +
        $"owns-pending={firstDelete.IsSet && File.Exists(stagedPath)} timer-armed={timeProvider.Armed.IsSet} " +
        $"timer-fired={timeProvider.Fired.IsSet} dispatcher-entered={dispatcherEntered.IsSet} " +
        $"second-delete={secondDelete.IsSet} delete-attempts={deleteAttempts} " +
        $"final-exact-path-exists={File.Exists(stagedPath)}";

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
        private bool referenceReleased;

        public ValueTask<RawReplaySnapshotCapture> CaptureAsync(
            RawReplaySelection selection,
            bool includeSessionContent,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RawReplaySnapshotCapture(true, null, new RawReplaySnapshotLease(
                AcquireReference,
                static () => ValueTask.CompletedTask,
                operation =>
                {
                    Assert.True(referenceReleased);
                    terminalOperations.Add(operation);
                    return operation == RawReplaySnapshotTerminalOperation.CompleteWithoutRaw
                        ? RawReplaySnapshotTerminalResult.CompletedWithoutRaw
                        : RawReplaySnapshotTerminalResult.Sealed;
                })));

        private RawReplaySnapshotUseReference AcquireReference()
        {
            referenceReleased = false;
            return new(() => snapshot, () => referenceReleased = true);
        }
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
                () => new RawReplaySnapshotUseReference(() => snapshot, () => events.Add("reference_released")),
                static () => ValueTask.CompletedTask,
                operation =>
                {
                    Assert.Equal(RawReplaySnapshotTerminalOperation.SealFilePublication, operation);
                    events.Add("seal");
                    return RawReplaySnapshotTerminalResult.Sealed;
                })));
    }

    private sealed class TerminalProvider(
        RawReplaySnapshot snapshot,
        RawReplaySnapshotTerminalResult terminalResult) : IRawReplaySnapshotProvider
    {
        public ValueTask<RawReplaySnapshotCapture> CaptureAsync(
            RawReplaySelection selection,
            bool includeSessionContent,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RawReplaySnapshotCapture(true, null, new RawReplaySnapshotLease(
                () => new RawReplaySnapshotUseReference(() => snapshot, static () => { }),
                static () => ValueTask.CompletedTask,
                operation => operation == RawReplaySnapshotTerminalOperation.CompleteWithoutRaw
                    ? RawReplaySnapshotTerminalResult.CompletedWithoutRaw
                    : terminalResult)));
    }

    private sealed class InertTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) => new InertTimer();

        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ObservingSystemTimeProvider : TimeProvider, IDisposable
    {
        internal ManualResetEventSlim Armed { get; } = new();
        internal ManualResetEventSlim Fired { get; } = new();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) => new ObservingTimer(this, callback, state, dueTime, period);

        public void Dispose()
        {
            Armed.Dispose();
            Fired.Dispose();
        }

        private sealed class ObservingTimer : ITimer
        {
            private readonly ObservingSystemTimeProvider owner;
            private readonly ITimer timer;

            internal ObservingTimer(
                ObservingSystemTimeProvider owner,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                this.owner = owner;
                timer = TimeProvider.System.CreateTimer(
                    _ =>
                    {
                        owner.Fired.Set();
                        callback(state);
                    },
                    null,
                    dueTime,
                    period);
                if (dueTime != Timeout.InfiniteTimeSpan) owner.Armed.Set();
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime != Timeout.InfiniteTimeSpan) owner.Armed.Set();
                return timer.Change(dueTime, period);
            }

            public void Dispose() => timer.Dispose();
            public ValueTask DisposeAsync() => timer.DisposeAsync();
        }
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
