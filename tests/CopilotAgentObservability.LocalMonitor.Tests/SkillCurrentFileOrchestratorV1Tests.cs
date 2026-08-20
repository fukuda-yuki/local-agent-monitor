using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.LocalMonitor.SkillNative;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using GitHub.Copilot;
using Microsoft.Win32.SafeHandles;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillCurrentFileOrchestratorV1Tests : IDisposable
{
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid SnapshotId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly DateTimeOffset ReadAt = new(2026, 5, 4, 3, 2, 1, TimeSpan.Zero);

    private const string SkillName = "review";
    private const string SkillSource = "custom";
    private const string RootPath = @"C:\skills";
    private const string DefinitionPath = @"C:\skills\review\SKILL.md";
    private const string HistoricalBody = "# review\n";

    private readonly TempHandleSource handleSource = new();

    public void Dispose() => handleSource.Dispose();

    [Fact]
    public async Task PreGrantOutcomesAnswerDirectlyAndCallNoTerminalMethod()
    {
        var expected = new (SkillInvocationSnapshotContentOutcome Outcome, int Status, string Token)[]
        {
            (SkillInvocationSnapshotContentOutcome.NotFound, 404, "skill_snapshot_not_found"),
            (SkillInvocationSnapshotContentOutcome.Busy, 503, "persistence_busy"),
            (SkillInvocationSnapshotContentOutcome.Unavailable, 503, "local_monitor_ui_unavailable"),
            (SkillInvocationSnapshotContentOutcome.Expired, 410, "skill_snapshot_expired"),
            (SkillInvocationSnapshotContentOutcome.ContentUnavailable, 422, "skill_snapshot_content_unavailable"),
        };

        foreach (var (outcome, status, token) in expected)
        {
            var fixture = new Fixture(handleSource);
            fixture.Historical.Outcome = outcome;

            var result = await fixture.ExecuteAsync();

            Assert.Equal(SkillCurrentFileDispositionV1.Respond, result.Disposition);
            Assert.Equal(status, result.StatusCode);
            Assert.Equal(token, ErrorToken(result));
            Assert.Equal(0, fixture.Grant.CompleteWithoutRawCalls);
            Assert.Equal(0, fixture.Grant.SealRawCalls);
            Assert.False(fixture.AuthorizationGate.WasCalled);
        }
    }

    [Fact]
    public async Task AnAbortedHistoricalAdmissionSendsNoResponse()
    {
        var fixture = new Fixture(handleSource);
        fixture.Historical.Outcome = SkillInvocationSnapshotContentOutcome.Aborted;

        var result = await fixture.ExecuteAsync();

        Assert.Equal(SkillCurrentFileDispositionV1.AbortWithoutResponse, result.Disposition);
        Assert.Empty(result.BodyUtf8);
    }

    [Fact]
    public async Task CurrentAuthorizationFailuresAreAuthorizedByRetentionCompletionAlone()
    {
        var expected = new (SkillRegistryCurrentAuthorizationOutcome Outcome, int Status, string Token)[]
        {
            (SkillRegistryCurrentAuthorizationOutcome.NotCurrent, 409, "skill_projection_not_current"),
            (SkillRegistryCurrentAuthorizationOutcome.Busy, 503, "persistence_busy"),
            (SkillRegistryCurrentAuthorizationOutcome.Unavailable, 503, "local_monitor_ui_unavailable"),
        };

        foreach (var (outcome, status, token) in expected)
        {
            var fixture = new Fixture(handleSource);
            fixture.AuthorizationGate.Outcome = outcome;

            var result = await fixture.ExecuteAsync();

            Assert.Equal(status, result.StatusCode);
            Assert.Equal(token, ErrorToken(result));
            Assert.Equal(1, fixture.Grant.CompleteWithoutRawCalls);
            Assert.Equal(0, fixture.Grant.SealRawCalls);
            Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
            Assert.False(fixture.DiscoveryGateway.WasCalled);
        }
    }

    [Fact]
    public async Task ARetentionLossAtAPreRuntimeFailureAbortsWithoutResponse()
    {
        var fixture = new Fixture(handleSource);
        fixture.AuthorizationGate.Outcome = SkillRegistryCurrentAuthorizationOutcome.NotCurrent;
        fixture.Grant.CompleteWithoutRawResult = SkillInvocationSnapshotContentTerminalResult.Lost;

        var result = await fixture.ExecuteAsync();

        Assert.Equal(SkillCurrentFileDispositionV1.AbortWithoutResponse, result.Disposition);
        Assert.Empty(result.BodyUtf8);
    }

    [Fact]
    public async Task NormalShutdownClosureCleansUpAndAbortsInsteadOfSendingDiscoveryUnavailable()
    {
        var fixture = new Fixture(handleSource);
        fixture.RuntimeAdmission.CloseForShutdown();

        var result = await fixture.ExecuteAsync();

        Assert.Equal(SkillCurrentFileDispositionV1.AbortWithoutResponse, result.Disposition);
        Assert.Equal(1, fixture.Grant.CompleteWithoutRawCalls);
        Assert.Equal(0, fixture.Grant.SealRawCalls);
        Assert.False(fixture.DiscoveryGateway.WasCalled);
    }

    [Fact]
    public async Task AMissingRuntimeGenerationIsTheFixedDiscoveryUnavailableBeforeAnySdkWork()
    {
        var fixture = new Fixture(handleSource);
        fixture.RuntimeAdmission.InvalidateCurrentGeneration();

        var result = await fixture.ExecuteAsync();

        Assert.Equal(503, result.StatusCode);
        Assert.Equal("skill_current_file_discovery_unavailable", ErrorToken(result));
        Assert.Equal(1, fixture.Grant.CompleteWithoutRawCalls);
        Assert.False(fixture.DiscoveryGateway.WasCalled);
    }

    [Fact]
    public async Task AClaimWithNoSourceIsNotDiscoveredWithoutAnSdkCall()
    {
        var fixture = new Fixture(handleSource);
        fixture.AuthorizationGate.SkillSource = null;

        var result = await fixture.ExecuteAsync();

        Assert.Equal(409, result.StatusCode);
        Assert.Equal("skill_current_file_not_discovered", ErrorToken(result));
        Assert.False(fixture.DiscoveryGateway.WasCalled);
    }

    [Fact]
    public async Task AnUnavailableDiscoveryAggregateIsTheSanitizedDiscoveryUnavailable()
    {
        var fixture = new Fixture(handleSource);
        fixture.DiscoveryGateway.Outcome = new CopilotSkillDiscoveryOutcome.Unavailable();

        var result = await fixture.ExecuteAsync();

        Assert.Equal(503, result.StatusCode);
        Assert.Equal("skill_current_file_discovery_unavailable", ErrorToken(result));
        Assert.Equal(1, fixture.Grant.CompleteWithoutRawCalls);
    }

    [Fact]
    public async Task ScanOutcomesMapToTheirExactRouteResults()
    {
        var fixture = new Fixture(handleSource);
        fixture.DiscoveryGateway.Outcome = new CopilotSkillDiscoveryOutcome.Discovered([]);

        var notDiscovered = await fixture.ExecuteAsync();
        Assert.Equal(409, notDiscovered.StatusCode);
        Assert.Equal("skill_current_file_not_discovered", ErrorToken(notDiscovered));

        var unsafeFixture = new Fixture(handleSource);
        unsafeFixture.DiscoveryGateway.Outcome = new CopilotSkillDiscoveryOutcome.Discovered(
        [
            MatchingFact(),
            MatchingFact(description: "a second distinct row for the same target")
        ]);

        var unsafeResult = await unsafeFixture.ExecuteAsync();
        Assert.Equal(409, unsafeResult.StatusCode);
        Assert.Equal("skill_current_file_unsafe", ErrorToken(unsafeResult));
    }

    [Fact]
    public async Task NativeOutcomesMapToTheirExactRouteResults()
    {
        var expected = new (CurrentSkillNativeOutcomeV1 Outcome, int Status, string Token)[]
        {
            (CurrentSkillNativeOutcomeV1.Unsafe, 409, "skill_current_file_unsafe"),
            (CurrentSkillNativeOutcomeV1.Raced, 409, "skill_current_file_raced"),
            (CurrentSkillNativeOutcomeV1.Missing, 404, "skill_current_file_missing"),
            (CurrentSkillNativeOutcomeV1.OtherNativeFailure, 503, "local_monitor_ui_unavailable"),
            (CurrentSkillNativeOutcomeV1.Oversized, 422, "skill_current_file_oversized"),
            (CurrentSkillNativeOutcomeV1.Binary, 422, "skill_current_file_binary"),
        };

        foreach (var (outcome, status, token) in expected)
        {
            var fixture = new Fixture(handleSource);
            fixture.NativeReader.Result = CurrentSkillNativeReadResultV1.Failure(outcome);

            var result = await fixture.ExecuteAsync();

            Assert.Equal(status, result.StatusCode);
            Assert.Equal(token, ErrorToken(result));
            Assert.Equal(1, fixture.Grant.CompleteWithoutRawCalls);
            Assert.Equal(0, fixture.Grant.SealRawCalls);
        }
    }

    [Fact]
    public async Task APostRuntimeSafeErrorCompletesRetentionBeforeItSealsTheRuntime()
    {
        var fixture = new Fixture(handleSource);
        fixture.NativeReader.Result = CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Missing);

        var result = await fixture.ExecuteAsync();

        Assert.Equal(404, result.StatusCode);
        Assert.Equal(["complete_without_raw"], fixture.Grant.CallOrder);
        Assert.Equal(SkillRuntimeTerminalSealV1.Response, fixture.LastCapability!.WonSealKind);
    }

    [Fact]
    public async Task APostRuntimeSafeErrorSubstitutesDiscoveryUnavailableWhenTheRuntimeSealLoses()
    {
        var fixture = new Fixture(handleSource);
        fixture.NativeReader.Result = CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Oversized);
        fixture.NativeReader.BeforeRead = () => fixture.RuntimeAdmission.InvalidateCurrentGeneration();

        var result = await fixture.ExecuteAsync();

        Assert.Equal(503, result.StatusCode);
        Assert.Equal("skill_current_file_discovery_unavailable", ErrorToken(result));
        Assert.Equal(1, fixture.Grant.CompleteWithoutRawCalls);
    }

    [Fact]
    public async Task APostRuntimeRetentionLossAbortsAndNeverAttemptsTheRuntimeSeal()
    {
        var fixture = new Fixture(handleSource);
        fixture.NativeReader.Result = CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Binary);
        fixture.Grant.CompleteWithoutRawResult = SkillInvocationSnapshotContentTerminalResult.Busy;

        var result = await fixture.ExecuteAsync();

        Assert.Equal(SkillCurrentFileDispositionV1.AbortWithoutResponse, result.Disposition);
        Assert.Null(fixture.LastCapability!.WonSealKind);
    }

    [Fact]
    public async Task RawSuccessSealsTheRuntimeBeforeRetentionAndSendsTheDocument()
    {
        var fixture = new Fixture(handleSource);

        var result = await fixture.ExecuteAsync();

        Assert.Equal(SkillCurrentFileDispositionV1.Respond, result.Disposition);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(["seal_raw"], fixture.Grant.CallOrder);
        Assert.Equal(SkillRuntimeTerminalSealV1.Response, fixture.LastCapability!.WonSealKind);

        using var document = JsonDocument.Parse(result.BodyUtf8);
        var root = document.RootElement;
        Assert.Equal("local-skill-current-file-read.response.v1", root.GetProperty("schema_version").GetString());
        Assert.Equal(SnapshotId.ToString("D"), root.GetProperty("snapshot_id").GetString());
        Assert.Equal("current_file", root.GetProperty("content_kind").GetString());
        Assert.Equal("changed", root.GetProperty("comparison").GetString());
        Assert.Equal("# current review\n", root.GetProperty("body").GetString());
    }

    [Fact]
    public async Task AByteIdenticalCurrentFileComparesAsSame()
    {
        var fixture = new Fixture(handleSource);
        fixture.NativeReader.Result = SuccessRead(HistoricalBody);

        var result = await fixture.ExecuteAsync();

        using var document = JsonDocument.Parse(result.BodyUtf8);
        Assert.Equal("same", document.RootElement.GetProperty("comparison").GetString());
    }

    [Fact]
    public async Task RawSuccessDiscardsRawAndSubstitutes503WhenTheRuntimeSealLoses()
    {
        var fixture = new Fixture(handleSource);
        fixture.NativeReader.BeforeRead = () => fixture.RuntimeAdmission.InvalidateCurrentGeneration();

        var result = await fixture.ExecuteAsync();

        Assert.Equal(503, result.StatusCode);
        Assert.Equal("skill_current_file_discovery_unavailable", ErrorToken(result));
        Assert.Equal(["complete_without_raw"], fixture.Grant.CallOrder);
        Assert.Equal(0, fixture.Grant.SealRawCalls);
    }

    [Fact]
    public async Task RawSuccessAbortsWhenBothTheRuntimeSealAndRetentionCompletionLose()
    {
        var fixture = new Fixture(handleSource);
        fixture.NativeReader.BeforeRead = () => fixture.RuntimeAdmission.InvalidateCurrentGeneration();
        fixture.Grant.CompleteWithoutRawResult = SkillInvocationSnapshotContentTerminalResult.Lost;

        var result = await fixture.ExecuteAsync();

        Assert.Equal(SkillCurrentFileDispositionV1.AbortWithoutResponse, result.Disposition);
        Assert.Empty(result.BodyUtf8);
    }

    [Fact]
    public async Task AWonRuntimeSealIsAbandonedWithNoOutputWhenTheRetentionRawSealLoses()
    {
        var fixture = new Fixture(handleSource);
        fixture.Grant.SealRawResult = SkillInvocationSnapshotContentTerminalResult.Lost;

        var result = await fixture.ExecuteAsync();

        Assert.Equal(SkillCurrentFileDispositionV1.AbortWithoutResponse, result.Disposition);
        Assert.Empty(result.BodyUtf8);
        Assert.Equal(["seal_raw"], fixture.Grant.CallOrder);
        Assert.Equal(0, fixture.Grant.CompleteWithoutRawCalls);

        // The seal was genuinely won and then abandoned, so a second abandon finds the capability
        // already abandoned rather than merely sealed.
        Assert.False(fixture.LastCapability!.TryAbandonWonSeal());
    }

    [Fact]
    public async Task CallerAbortOutranksRetentionAndRuntimeAndSendsNoResponse()
    {
        var fixture = new Fixture(handleSource);
        fixture.NativeReader.Result = CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Missing);
        fixture.Grant.CompleteWithoutRawResult = SkillInvocationSnapshotContentTerminalResult.Lost;
        fixture.NativeReader.BeforeRead = () =>
        {
            fixture.RuntimeAdmission.InvalidateCurrentGeneration();
            fixture.CallerAbort.Cancel();
        };

        var result = await fixture.ExecuteAsync();

        Assert.Equal(SkillCurrentFileDispositionV1.AbortWithoutResponse, result.Disposition);
        Assert.Empty(result.BodyUtf8);
    }

    [Fact]
    public async Task EveryPathReleasesTheRuntimeCapability()
    {
        var configurations = new Action<Fixture>[]
        {
            _ => { },
            fixture => fixture.NativeReader.Result =
                CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Missing),
            fixture => fixture.Grant.SealRawResult = SkillInvocationSnapshotContentTerminalResult.Lost,
            fixture => fixture.Grant.CompleteWithoutRawResult = SkillInvocationSnapshotContentTerminalResult.Busy,
            fixture => fixture.DiscoveryGateway.Outcome = new CopilotSkillDiscoveryOutcome.Unavailable(),
        };

        foreach (var configure in configurations)
        {
            var fixture = new Fixture(handleSource);
            configure(fixture);

            await fixture.ExecuteAsync();

            Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
        }
    }

    private static string? ErrorToken(SkillCurrentFileResultV1 result)
    {
        using var document = JsonDocument.Parse(result.BodyUtf8);
        return document.RootElement.GetProperty("error").GetString();
    }

    private static CurrentSkillNativeReadResultV1 SuccessRead(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return CurrentSkillNativeReadResultV1.Success(
            bytes,
            System.Security.Cryptography.SHA256.HashData(bytes),
            ReadAt);
    }

    private static CopilotDiscoveredSkillFactV1 MatchingFact(string? description = null) =>
        new(SkillName, SkillSource, DefinitionPath, null, description, null, true, true);

    private sealed class Fixture
    {
        internal Fixture(TempHandleSource handleSource)
        {
            Preflight = SkillDiscoveryRootPreflightV1.Run(
                [],
                [RootPath],
                new CertifiedDiscoveryPlatformV1(SkillProducerPathKeyPlatform.Windows, new StubOpener(handleSource)));
            RootGeneration = new SkillDiscoveryRootGenerationV1(Preflight);

            RuntimeAdmission = new CopilotRuntimeAdmissionV1();
            Generation = RuntimeAdmission.PublishAdmittedGeneration(new StubRuntimeClient(), out _)!;

            Grant = new FakeGrant();
            Historical = new FakeHistoricalGate(Grant);
            AuthorizationGate = new FakeAuthorizationGate();
            DiscoveryGateway = new FakeDiscoveryGateway { Outcome = new CopilotSkillDiscoveryOutcome.Discovered([MatchingFact()]) };
            NativeReader = new FakeNativeReader { Result = SuccessRead("# current review\n") };
        }

        internal SkillDiscoveryRootPreflightResultV1 Preflight { get; }

        internal SkillDiscoveryRootGenerationV1 RootGeneration { get; }

        internal CopilotRuntimeAdmissionV1 RuntimeAdmission { get; }

        internal CopilotRuntimeGenerationV1 Generation { get; }

        internal FakeGrant Grant { get; }

        internal FakeHistoricalGate Historical { get; }

        internal FakeAuthorizationGate AuthorizationGate { get; }

        internal FakeDiscoveryGateway DiscoveryGateway { get; }

        internal FakeNativeReader NativeReader { get; }

        internal CancellationTokenSource CallerAbort { get; } = new();

        internal CopilotRuntimeOperationCapabilityV1? LastCapability { get; private set; }

        internal async Task<SkillCurrentFileResultV1> ExecuteAsync()
        {
            var orchestrator = new SkillCurrentFileOrchestratorV1(
                Historical, AuthorizationGate, RuntimeAdmission, DiscoveryGateway, NativeReader);

            Assert.True(RootGeneration.TryAcquireLease(out var lease));
            using (lease)
            {
                var result = await orchestrator.ExecuteAsync(SessionId, SnapshotId, lease!, CallerAbort.Token);
                LastCapability = DiscoveryGateway.ObservedCapability;
                return result;
            }
        }
    }

    private sealed class FakeGrant : ISkillCurrentFileRetentionGrantV1
    {
        private readonly List<string> callOrder = [];

        internal SkillInvocationSnapshotContentTerminalResult SealRawResult { get; set; } =
            SkillInvocationSnapshotContentTerminalResult.Sealed;

        internal SkillInvocationSnapshotContentTerminalResult CompleteWithoutRawResult { get; set; } =
            SkillInvocationSnapshotContentTerminalResult.CompletedWithoutRaw;

        internal int SealRawCalls { get; private set; }

        internal int CompleteWithoutRawCalls { get; private set; }

        internal IReadOnlyList<string> CallOrder => callOrder;

        public SkillInvocationSnapshotContentFacts Facts { get; } = new(
            SnapshotId,
            HistoricalBody,
            DefinitionPath,
            "0000000000000000000000000000000000000000000000000000000000000000",
            "1111111111111111111111111111111111111111111111111111111111111111",
            Encoding.UTF8.GetByteCount(HistoricalBody),
            Encoding.UTF8.GetByteCount(DefinitionPath),
            ReadAt);

        public SkillInvocationSnapshotContentTerminalResult TrySealRawResponse()
        {
            SealRawCalls++;
            callOrder.Add("seal_raw");
            return SealRawResult;
        }

        public SkillInvocationSnapshotContentTerminalResult TryCompleteWithoutRaw()
        {
            CompleteWithoutRawCalls++;
            callOrder.Add("complete_without_raw");
            return CompleteWithoutRawResult;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeHistoricalGate(FakeGrant grant) : ISkillCurrentFileHistoricalGateV1
    {
        internal SkillInvocationSnapshotContentOutcome Outcome { get; set; } =
            SkillInvocationSnapshotContentOutcome.Granted;

        public Task<SkillCurrentFileHistoricalAdmissionV1> AdmitAsync(
            Guid sessionId,
            Guid snapshotId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SkillCurrentFileHistoricalAdmissionV1(
                Outcome,
                Outcome == SkillInvocationSnapshotContentOutcome.Granted ? grant : null));
    }

    private sealed class FakeAuthorizationGate : ISkillCurrentAuthorizationGateV1
    {
        internal SkillRegistryCurrentAuthorizationOutcome Outcome { get; set; } =
            SkillRegistryCurrentAuthorizationOutcome.Acquired;

        internal string? SkillSource { get; set; } = SkillCurrentFileOrchestratorV1Tests.SkillSource;

        internal bool WasCalled { get; private set; }

        public SkillProjectionCurrentSdkClaimAuthorizationResult TryAcquire(Guid sessionId, Guid snapshotId)
        {
            WasCalled = true;
            return Outcome switch
            {
                SkillRegistryCurrentAuthorizationOutcome.NotCurrent =>
                    SkillProjectionCurrentSdkClaimAuthorizationResult.NotCurrent,
                SkillRegistryCurrentAuthorizationOutcome.Busy =>
                    SkillProjectionCurrentSdkClaimAuthorizationResult.Busy,
                SkillRegistryCurrentAuthorizationOutcome.Unavailable =>
                    SkillProjectionCurrentSdkClaimAuthorizationResult.Unavailable,
                _ => SkillProjectionCurrentSdkClaimAuthorizationResult.ForAcquired(
                    new SkillProjectionCurrentSdkClaimAuthorization(SkillName, SkillSource, new StubGenerationLease()))
            };
        }
    }

    private sealed class StubGenerationLease : ISkillRegistryGenerationLease
    {
        public void Dispose()
        {
        }
    }

    private sealed class FakeDiscoveryGateway : ISkillDiscoveryGatewayV1
    {
        internal CopilotSkillDiscoveryOutcome Outcome { get; set; } = new CopilotSkillDiscoveryOutcome.Unavailable();

        internal bool WasCalled { get; private set; }

        internal CopilotRuntimeOperationCapabilityV1? ObservedCapability { get; private set; }

        public Task<CopilotSkillDiscoveryOutcome> DiscoverAsync(
            CopilotRuntimeOperationCapabilityV1 capability,
            DiscoveryRootSetV1 roots,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            ObservedCapability = capability;
            return Task.FromResult(Outcome);
        }
    }

    private sealed class FakeNativeReader : ICurrentSkillNativeFileReaderV1
    {
        internal CurrentSkillNativeReadResultV1 Result { get; set; } =
            CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Missing);

        internal Action? BeforeRead { get; set; }

        public CurrentSkillNativeReadResultV1 Read(CurrentSkillReadTargetV1 target, CancellationToken cancellationToken)
        {
            BeforeRead?.Invoke();
            return Result;
        }
    }

    private sealed class StubRuntimeClient : ICopilotSkillRuntimeClient
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult<CopilotRuntimeStatusObservationV1?>(new("1.0.65", 3, "1.0.65"));

        public Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> DiscoverSkillsAsync(
            IReadOnlyList<string> projectPaths,
            IReadOnlyList<string> skillDirectories,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CopilotDiscoveredSkillFactV1>?>([]);

        public Task<CopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void RecordSessionStartCopilotVersion(string? copilotVersion)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubOpener(TempHandleSource handleSource) : IDiscoveryRootOpenerV1
    {
        public DiscoveryRootOpenResultV1 TryOpenRetainedRoot(string configuredRootPath, DiscoveryRootKindV1 kind)
        {
            Assert.True(SkillProducerPathKeyV1.TryParse(
                configuredRootPath, SkillProducerPathKeyPlatform.Windows, out var pathKey, out _));

            return DiscoveryRootOpenResultV1.Succeeded(new RetainedDiscoveryRootV1(
                kind,
                pathKey,
                DiscoveryRootNativeIdentityV1.CreateWindows(7, new byte[16]),
                handleSource.OpenHandle()));
        }

        public bool TryReproveRetainedRoot(RetainedDiscoveryRootV1 root) => !root.IsDisposed;
    }

    private sealed class TempHandleSource : IDisposable
    {
        private readonly string directoryPath =
            Path.Combine(Path.GetTempPath(), $"cao-orchestrator-{Guid.NewGuid():N}");

        private readonly string filePath;

        public TempHandleSource()
        {
            Directory.CreateDirectory(directoryPath);
            filePath = Path.Combine(directoryPath, "handle-source.bin");
            File.WriteAllBytes(filePath, [1, 2, 3]);
        }

        public SafeFileHandle OpenHandle() => File.OpenHandle(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        public void Dispose()
        {
            try
            {
                Directory.Delete(directoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
