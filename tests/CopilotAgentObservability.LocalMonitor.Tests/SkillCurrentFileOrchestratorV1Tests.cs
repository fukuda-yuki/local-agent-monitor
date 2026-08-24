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

    // The orchestrator reads the grant's facts once for the scan and once to serialize the raw
    // response, so the second access is the request's entry into response serialization.
    private const int SerializationFactsAccessOrdinal = 2;

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
            Assert.False(fixture.NativeReader.WasCalled);
            Assert.Equal(0, fixture.AuthorizationGate.IssuedLeaseCount);
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
        fixture.RuntimeAdmission.InvalidateCurrentTestGeneration();

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
            MatchingFact(DefinitionPath),
            MatchingFact(DefinitionPath, "a second distinct row for the same target")
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
        fixture.NativeReader.BeforeRead = () => fixture.RuntimeAdmission.InvalidateCurrentTestGeneration();

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
        fixture.NativeReader.BeforeRead = () => fixture.RuntimeAdmission.InvalidateCurrentTestGeneration();

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
        fixture.NativeReader.BeforeRead = () => fixture.RuntimeAdmission.InvalidateCurrentTestGeneration();
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
            fixture.RuntimeAdmission.InvalidateCurrentTestGeneration();
            fixture.CallerAbort.Cancel();
        };

        var result = await fixture.ExecuteAsync();

        Assert.Equal(SkillCurrentFileDispositionV1.AbortWithoutResponse, result.Disposition);
        Assert.Empty(result.BodyUtf8);
    }

    // #154 is one opaque capability the request holds across its whole SDK and native lifetime, so
    // every arm that acquired it must hand it back exactly once, whatever the request's own result.
    [Theory]
    [InlineData("raw_success")]
    [InlineData("post_runtime_safe_error")]
    [InlineData("post_runtime_retention_loss")]
    [InlineData("runtime_generation_missing")]
    [InlineData("normal_shutdown_closed")]
    public async Task AnAcceptedCurrentAuthorizationIsHeldAcrossTheRequestAndReleasedExactlyOnce(string arm)
    {
        using var fixture = new Fixture(handleSource);
        switch (arm)
        {
            case "post_runtime_safe_error":
                fixture.NativeReader.Result =
                    CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Missing);
                break;
            case "post_runtime_retention_loss":
                fixture.NativeReader.Result =
                    CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Missing);
                fixture.Grant.CompleteWithoutRawResult = SkillInvocationSnapshotContentTerminalResult.Lost;
                break;
            case "runtime_generation_missing":
                fixture.RuntimeAdmission.InvalidateCurrentTestGeneration();
                break;
            case "normal_shutdown_closed":
                fixture.RuntimeAdmission.CloseForShutdown();
                break;
        }

        var result = await fixture.ExecuteAsync();

        var expected = arm switch
        {
            "raw_success" => (SkillCurrentFileDispositionV1.Respond, 200),
            "post_runtime_safe_error" => (SkillCurrentFileDispositionV1.Respond, 404),
            "runtime_generation_missing" => (SkillCurrentFileDispositionV1.Respond, 503),
            _ => (SkillCurrentFileDispositionV1.AbortWithoutResponse, 0),
        };
        Assert.Equal(expected, (result.Disposition, result.StatusCode));
        Assert.Equal(1, fixture.AuthorizationGate.IssuedLeaseCount);
        Assert.Equal(0, fixture.AuthorizationGate.ReleasedLeaseCount);
        Assert.Equal(1, fixture.Grant.DisposeCalls);
        Assert.Equal(0, fixture.RootGeneration.OutstandingLeaseCount);
        result.ResponseCapabilities?.Dispose();
        result.ResponseCapabilities?.Dispose();
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
        Assert.Equal(1, fixture.AuthorizationGate.ReleasedLeaseCount);
        Assert.Equal(1, fixture.AuthorizationGate.MaximumReleaseCallsOnOneLease);
    }

    // The registry generation the acceptance was taken from can be superseded while the request is
    // still running. The capability is what stops that from reaching an in-flight request: the
    // publication cannot swap the pointer until the request releases it, so the request finishes on
    // exactly the generation it was accepted under.
    [Fact]
    public async Task ARegistryPublicationBegunAfterAcceptanceCannotTakeEffectUntilTheCapabilityIsReleased()
    {
        using var fixture = new Fixture(handleSource);
        var provider = new SkillInvocationV2RegistryProviderV1();
        fixture.AuthorizationGate.LeaseFactory = () =>
        {
            var capture = provider.CaptureGeneration();
            Assert.NotNull(capture);
            Assert.True(provider.TryAcquireGenerationReadLease(capture!, out var lease));
            return lease!;
        };

        var publicationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? publication = null;
        var publicationCompletedWhileHeld = true;
        var leasesHeldAtTerminal = -1;

        fixture.NativeReader.Result = SuccessRead("# current review\n");
        fixture.NativeReader.BeforeRead = () =>
        {
            publication = Task.Run(() =>
            {
                publicationEntered.SetResult();
                provider.PublishGeneration(SkillInvocationV2ArtifactRegistry.Load());
            });
            publicationEntered.Task.GetAwaiter().GetResult();
        };
        fixture.Grant.OnTerminalAttempt = () =>
        {
            leasesHeldAtTerminal = provider.OutstandingLeaseCount;
            publicationCompletedWhileHeld = publication!.IsCompleted;
        };

        var result = await fixture.ExecuteAsync();

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(1, leasesHeldAtTerminal);
        Assert.False(publicationCompletedWhileHeld);

        Assert.False(publication!.IsCompleted);
        result.ResponseCapabilities!.Dispose();
        await publication!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, provider.OutstandingLeaseCount);
    }

    // Retention loss becomes authoritative only where a store-backed terminal operation proves it,
    // so a loss injected during discovery does not stop the scan or the read: the request runs to
    // its candidate and is refused at the completion it was always going to need.
    [Fact]
    public async Task RetentionGrantLossDuringDiscoveryIsProvedOnlyAtTheTerminalCompletionAndAborts()
    {
        using var fixture = new Fixture(handleSource);
        var injected = false;
        fixture.NativeReader.Result = CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Missing);
        fixture.DiscoveryGateway.DuringDiscovery = () =>
        {
            injected = true;
            fixture.Grant.Lose();
        };

        var result = await fixture.ExecuteAsync();

        Assert.True(injected);
        Assert.True(fixture.NativeReader.WasCalled);
        AssertRetentionLossAbort(fixture, result);
    }

    [Fact]
    public async Task RetentionGrantLossAfterDiscoveryBeforeResultEnumerationIsProvedOnlyAtTheTerminalCompletionAndAborts()
    {
        using var fixture = new Fixture(handleSource);
        var injected = false;
        fixture.NativeReader.Result = CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Missing);
        fixture.DiscoveryGateway.AfterDiscoveryBeforeResultEnumeration = () =>
        {
            injected = true;
            fixture.Grant.Lose();
        };

        var result = await fixture.ExecuteAsync();

        Assert.True(injected);
        Assert.True(fixture.NativeReader.WasCalled);
        AssertRetentionLossAbort(fixture, result);
    }

    // The native stages are entered through the production walker's own hooks, so the fence each
    // test names is the fence the walker actually reached rather than one the fixture asserts.
    [WindowsFact]
    public async Task RetentionGrantLossDuringTheNativeReadIsProvedOnlyAtTheTerminalCompletionAndAborts() =>
        await AssertRetentionLossAtNativeFenceAbortsAsync(NativeReadFence.BeforeBoundedRead);

    [WindowsFact]
    public async Task RetentionGrantLossAtThePostReadReproofIsProvedOnlyAtTheTerminalCompletionAndAborts() =>
        await AssertRetentionLossAtNativeFenceAbortsAsync(NativeReadFence.AfterReadBeforeReproof);

    // Serialization has the complete candidate buffered but has authorized nothing, so a loss here
    // still lets the runtime seal be attempted first; only the Retention raw seal can refuse it,
    // and the won seal is then abandoned rather than sent.
    [Fact]
    public async Task RetentionGrantLossDuringResponseSerializationAbandonsTheWonRuntimeSealWithNoOutput()
    {
        using var fixture = new Fixture(handleSource);
        var injected = false;
        fixture.NativeReader.Result = SuccessRead("# current review\n");
        fixture.Grant.OnFactsAccess = ordinal =>
        {
            if (ordinal != SerializationFactsAccessOrdinal) return;
            injected = true;
            fixture.Grant.Lose();
        };

        var result = await fixture.ExecuteAsync();

        Assert.True(injected);
        Assert.Equal(SerializationFactsAccessOrdinal, fixture.Grant.FactsAccessCount);
        Assert.Equal(SkillCurrentFileDispositionV1.AbortWithoutResponse, result.Disposition);
        Assert.Equal(0, result.StatusCode);
        Assert.Empty(result.BodyUtf8);
        Assert.Equal(["seal_raw"], fixture.Grant.CallOrder);
        Assert.Equal(0, fixture.Grant.CompleteWithoutRawCalls);
        Assert.Equal(SkillRuntimeTerminalSealV1.Response, fixture.LastCapability!.WonSealKind);
        Assert.False(fixture.LastCapability.TryAbandonWonSeal());
        AssertRequestScopedResourcesReleasedOnce(fixture, result);
    }

    // The two terminal orders diverge only here: a safe error proves Retention first, so a loss
    // that lands immediately before the seal never reaches TrySealResponse, while raw success has
    // already won the runtime seal and can only abandon it.
    [Theory]
    [InlineData("safe_error")]
    [InlineData("raw_success")]
    public async Task RetentionGrantLossImmediatelyBeforeTheResponseTerminalSealAbortsWithoutASubstitute(string arm)
    {
        using var fixture = new Fixture(handleSource);
        fixture.NativeReader.Result = arm == "safe_error"
            ? CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Oversized)
            : SuccessRead("# current review\n");

        var injected = false;
        fixture.Grant.OnTerminalAttempt = () =>
        {
            if (injected) return;
            injected = true;
            fixture.Grant.Lose();
        };

        var result = await fixture.ExecuteAsync();

        Assert.True(injected);
        Assert.True(fixture.NativeReader.WasCalled);
        Assert.Equal(SkillCurrentFileDispositionV1.AbortWithoutResponse, result.Disposition);
        Assert.Equal(0, result.StatusCode);
        Assert.Empty(result.BodyUtf8);

        if (arm == "safe_error")
        {
            Assert.Equal(["complete_without_raw"], fixture.Grant.CallOrder);
            Assert.Null(fixture.LastCapability!.WonSealKind);
        }
        else
        {
            Assert.Equal(["seal_raw"], fixture.Grant.CallOrder);
            Assert.Equal(SerializationFactsAccessOrdinal, fixture.Grant.FactsAccessCount);
            Assert.Equal(SkillRuntimeTerminalSealV1.Response, fixture.LastCapability!.WonSealKind);
            Assert.False(fixture.LastCapability.TryAbandonWonSeal());
        }

        AssertRequestScopedResourcesReleasedOnce(fixture, result);
    }

    private enum NativeReadFence
    {
        BeforeBoundedRead,
        AfterReadBeforeReproof
    }

    private static async Task AssertRetentionLossAtNativeFenceAbortsAsync(NativeReadFence fence)
    {
        // Invalid UTF-8 keeps the candidate on the buffered safe-error arm, where the terminal
        // order is Retention first, while both walker hooks still run exactly as they do for a
        // readable file.
        using var root = new RealSkillRoot([0xC3, 0x28, 0xA0]);
        var openedHandles = new List<nint>();
        var closedHandles = new List<nint>();
        var reachedFence = false;
        Fixture? pending = null;
        void Inject()
        {
            reachedFence = true;
            pending!.Grant.Lose();
        }

        var hooks = new CurrentSkillFileReaderHooksV1
        {
            AfterFinalMetadataCaptured = _ =>
            {
                if (fence == NativeReadFence.BeforeBoundedRead) Inject();
            },
            AfterReadCompleted = _ =>
            {
                if (fence == NativeReadFence.AfterReadBeforeReproof) Inject();
            },
            HandleOpened = handle => openedHandles.Add(handle),
            HandleClosed = handle => closedHandles.Add(handle),
        };

        using var fixture = new Fixture(root, hooks);
        pending = fixture;

        var result = await fixture.ExecuteAsync();

        Assert.True(reachedFence);
        Assert.Equal(2, openedHandles.Count);
        Assert.Equal(openedHandles.AsEnumerable().Reverse(), closedHandles);
        AssertRetentionLossAbort(fixture, result);
    }

    // Every Retention-loss stage owes the same closing invariants: the safe error was refused
    // before any runtime seal was attempted, nothing was started on the wire, and no candidate byte
    // survived the abort.
    private static void AssertRetentionLossAbort(Fixture fixture, SkillCurrentFileResultV1 result)
    {
        Assert.Equal(SkillCurrentFileDispositionV1.AbortWithoutResponse, result.Disposition);
        Assert.Equal(0, result.StatusCode);
        Assert.Empty(result.BodyUtf8);
        Assert.Equal(1, fixture.Grant.CompleteWithoutRawCalls);
        Assert.Equal(0, fixture.Grant.SealRawCalls);
        Assert.Null(fixture.LastCapability!.WonSealKind);
        AssertRequestScopedResourcesReleasedOnce(fixture, result);
    }

    private static void AssertRequestScopedResourcesReleasedOnce(Fixture fixture, SkillCurrentFileResultV1 result)
    {
        Assert.Equal(1, fixture.Grant.DisposeCalls);
        Assert.Equal(1, fixture.AuthorizationGate.IssuedLeaseCount);
        Assert.Equal(0, fixture.AuthorizationGate.ReleasedLeaseCount);
        Assert.Equal(0, fixture.RootGeneration.OutstandingLeaseCount);
        Assert.Equal(1, fixture.Generation.OutstandingCapabilityCount);
        result.ResponseCapabilities!.Dispose();
        result.ResponseCapabilities.Dispose();
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
        Assert.Equal(1, fixture.AuthorizationGate.ReleasedLeaseCount);
        Assert.Equal(1, fixture.AuthorizationGate.MaximumReleaseCallsOnOneLease);
    }

    [Fact]
    public async Task ExecuteAsync_PostRuntimeResults_KeepTheRuntimeCapabilityOutstanding()
    {
        var configurations = new Action<Fixture>[]
        {
            _ => { },
            fixture => fixture.NativeReader.Result =
                CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Missing),
        };

        foreach (var configure in configurations)
        {
            var fixture = new Fixture(handleSource);
            configure(fixture);

            var result = await fixture.ExecuteAsync();

            Assert.Equal(1, fixture.Generation.OutstandingCapabilityCount);
            Assert.Equal(0, fixture.AuthorizationGate.ReleasedLeaseCount);
            result.ResponseCapabilities?.Dispose();
            Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
            Assert.Equal(1, fixture.AuthorizationGate.ReleasedLeaseCount);
        }
    }

    [Fact]
    public async Task ResponseCapabilities_DisposesRuntimeBeforeCurrentAuthorization()
    {
        using var fixture = new Fixture(handleSource);
        var runtimeCapabilitiesAtAuthorizationRelease = -1;
        fixture.AuthorizationGate.OnLeaseRelease = () =>
            runtimeCapabilitiesAtAuthorizationRelease = fixture.Generation.OutstandingCapabilityCount;

        var result = await fixture.ExecuteAsync();

        result.ResponseCapabilities!.Dispose();

        Assert.Equal(0, runtimeCapabilitiesAtAuthorizationRelease);
    }

    [Fact]
    public async Task ExecuteAsync_NativeReadCancellation_ReleasesTheRuntimeCapabilityAndRethrows()
    {
        var fixture = new Fixture(handleSource);
        fixture.NativeReader.BeforeRead = () =>
        {
            fixture.CallerAbort.Cancel();
            fixture.CallerAbort.Token.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ExecuteAsync());

        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
        Assert.Equal(1, fixture.AuthorizationGate.ReleasedLeaseCount);
    }

    [Fact]
    public async Task ExecuteAsync_DiscoveryFailure_ReleasesTheRuntimeCapabilityAndRethrows()
    {
        var fixture = new Fixture(handleSource);
        fixture.DiscoveryGateway.ExceptionToThrow = new InvalidOperationException("discovery failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync());

        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
        Assert.Equal(1, fixture.AuthorizationGate.ReleasedLeaseCount);
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

    private static CopilotDiscoveredSkillFactV1 MatchingFact(string definitionPath, string? description = null) =>
        new(SkillName, SkillSource, definitionPath, null, description, null, true, true);

    private sealed class Fixture : IDisposable
    {
        internal Fixture(TempHandleSource handleSource)
            : this(
                SkillDiscoveryRootPreflightV1.Run(
                    [],
                    [RootPath],
                    new CertifiedDiscoveryPlatformV1(SkillProducerPathKeyPlatform.Windows, new StubOpener(handleSource))),
                DefinitionPath)
        {
            NativeReader.Result = SuccessRead("# current review\n");
            Reader = NativeReader;
        }

        // The staged native-read matrix needs the production walker's own hooks to prove which
        // fence a request had reached, so this arm binds a real retained root and the real reader
        // instead of the stub pair the ordering tests use.
        internal Fixture(RealSkillRoot root, CurrentSkillFileReaderHooksV1 hooks)
            : this(
                SkillDiscoveryRootPreflightV1.Run(
                    [],
                    [root.RootPath],
                    new CertifiedDiscoveryPlatformV1(
                        SkillProducerPathKeyPlatform.Windows, new WindowsDiscoveryRootOpenerV1())),
                root.DefinitionPath) =>
            Reader = new WindowsCurrentSkillFileReaderV1(() => ReadAt, hooks);

        private Fixture(SkillDiscoveryRootPreflightResultV1 preflight, string definitionPath)
        {
            var shutdownGate = new SkillHostShutdownGateV1();
            Preflight = preflight;
            Assert.Equal(SkillDiscoveryRootPreflightOutcomeV1.Certified, Preflight.Outcome);
            RootGeneration = new SkillDiscoveryRootGenerationV1(Preflight, shutdownGate);

            RuntimeAdmission = new CopilotRuntimeAdmissionV1(shutdownGate);
            Generation = RuntimeAdmission.PublishReadyTestCandidate(new StubRuntimeClient(), out _);

            Grant = new FakeGrant(definitionPath);
            Historical = new FakeHistoricalGate(Grant);
            AuthorizationGate = new FakeAuthorizationGate();
            DiscoveryGateway = new FakeDiscoveryGateway
            {
                Outcome = new CopilotSkillDiscoveryOutcome.Discovered([MatchingFact(definitionPath)])
            };
            NativeReader = new FakeNativeReader();
            Reader = NativeReader;
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

        internal ICurrentSkillNativeFileReaderV1 Reader { get; }

        internal CancellationTokenSource CallerAbort { get; } = new();

        internal CopilotRuntimeOperationCapabilityV1? LastCapability { get; private set; }

        internal async Task<SkillCurrentFileResultV1> ExecuteAsync()
        {
            var orchestrator = new SkillCurrentFileOrchestratorV1(
                Historical, AuthorizationGate, RuntimeAdmission, DiscoveryGateway, Reader);

            Assert.True(RootGeneration.TryAcquireLease(out var lease));
            using (lease)
            {
                var result = await orchestrator.ExecuteAsync(SessionId, SnapshotId, lease!, CallerAbort.Token);
                LastCapability = DiscoveryGateway.ObservedCapability;
                return result;
            }
        }

        public void Dispose()
        {
            CallerAbort.Dispose();
            Preflight.Dispose();
        }
    }

    // A real retained discovery root whose only candidate is <root>\review\SKILL.md.
    private sealed class RealSkillRoot : IDisposable
    {
        internal RealSkillRoot(byte[] skillFileBytes)
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"cao-orchestrator-root-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(RootPath, "review"));
            DefinitionPath = Path.Combine(RootPath, "review", "SKILL.md");
            File.WriteAllBytes(DefinitionPath, skillFileBytes);
        }

        internal string RootPath { get; }

        internal string DefinitionPath { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class FakeGrant : ISkillCurrentFileRetentionGrantV1
    {
        private readonly List<string> callOrder = [];
        private readonly SkillInvocationSnapshotContentFacts facts;

        internal FakeGrant(string definitionPath) =>
            facts = new(
                SnapshotId,
                HistoricalBody,
                definitionPath,
                "0000000000000000000000000000000000000000000000000000000000000000",
                "1111111111111111111111111111111111111111111111111111111111111111",
                Encoding.UTF8.GetByteCount(HistoricalBody),
                Encoding.UTF8.GetByteCount(definitionPath),
                ReadAt);

        internal SkillInvocationSnapshotContentTerminalResult SealRawResult { get; set; } =
            SkillInvocationSnapshotContentTerminalResult.Sealed;

        internal SkillInvocationSnapshotContentTerminalResult CompleteWithoutRawResult { get; set; } =
            SkillInvocationSnapshotContentTerminalResult.CompletedWithoutRaw;

        internal int SealRawCalls { get; private set; }

        internal int CompleteWithoutRawCalls { get; private set; }

        internal int DisposeCalls { get; private set; }

        // The candidate is serialized from these facts, so the access ordinal is the fixture's
        // handle on "the request is inside response serialization with the candidate buffered".
        internal int FactsAccessCount { get; private set; }

        internal Action<int>? OnFactsAccess { get; set; }

        internal Action? OnTerminalAttempt { get; set; }

        internal IReadOnlyList<string> CallOrder => callOrder;

        // The store-backed grant reports loss only through a terminal operation, so a lost grant
        // is modelled the one way the orchestrator can observe it.
        internal void Lose()
        {
            SealRawResult = SkillInvocationSnapshotContentTerminalResult.Lost;
            CompleteWithoutRawResult = SkillInvocationSnapshotContentTerminalResult.Lost;
        }

        public SkillInvocationSnapshotContentFacts Facts
        {
            get
            {
                FactsAccessCount++;
                OnFactsAccess?.Invoke(FactsAccessCount);
                return facts;
            }
        }

        public SkillInvocationSnapshotContentTerminalResult TrySealRawResponse()
        {
            OnTerminalAttempt?.Invoke();
            SealRawCalls++;
            callOrder.Add("seal_raw");
            return SealRawResult;
        }

        public SkillInvocationSnapshotContentTerminalResult TryCompleteWithoutRaw()
        {
            OnTerminalAttempt?.Invoke();
            CompleteWithoutRawCalls++;
            callOrder.Add("complete_without_raw");
            return CompleteWithoutRawResult;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
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
        private readonly List<CountingGenerationLease> issued = [];

        internal SkillRegistryCurrentAuthorizationOutcome Outcome { get; set; } =
            SkillRegistryCurrentAuthorizationOutcome.Acquired;

        internal string? SkillSource { get; set; } = SkillCurrentFileOrchestratorV1Tests.SkillSource;

        internal bool WasCalled { get; private set; }

        // #154 stays a single opaque capability: the fixture only chooses which lease object the
        // acquired authorization carries, never re-decides currentness.
        internal Func<ISkillRegistryGenerationLease>? LeaseFactory { get; set; }

        internal Action? OnLeaseRelease { get; set; }

        internal int IssuedLeaseCount => issued.Count;

        internal int ReleasedLeaseCount => issued.Count(lease => lease.ReleaseCalls > 0);

        internal int MaximumReleaseCallsOnOneLease =>
            issued.Count == 0 ? 0 : issued.Max(lease => lease.ReleaseCalls);

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
                    new SkillProjectionCurrentSdkClaimAuthorization(SkillName, SkillSource, AcquireLease()))
            };
        }

        private ISkillRegistryGenerationLease AcquireLease()
        {
            var lease = new CountingGenerationLease(LeaseFactory?.Invoke(), OnLeaseRelease);
            issued.Add(lease);
            return lease;
        }
    }

    private sealed class CountingGenerationLease(
        ISkillRegistryGenerationLease? inner,
        Action? onRelease = null) : ISkillRegistryGenerationLease
    {
        internal int ReleaseCalls { get; private set; }

        public void Dispose()
        {
            ReleaseCalls++;
            onRelease?.Invoke();
            inner?.Dispose();
        }
    }

    private sealed class FakeDiscoveryGateway : ISkillDiscoveryGatewayV1
    {
        internal CopilotSkillDiscoveryOutcome Outcome { get; set; } = new CopilotSkillDiscoveryOutcome.Unavailable();

        internal Exception? ExceptionToThrow { get; set; }

        internal bool WasCalled { get; private set; }

        internal CopilotRuntimeOperationCapabilityV1? ObservedCapability { get; private set; }

        internal Action? DuringDiscovery { get; set; }

        internal Action? AfterDiscoveryBeforeResultEnumeration { get; set; }

        public Task<CopilotSkillDiscoveryOutcome> DiscoverAsync(
            CopilotRuntimeOperationCapabilityV1 capability,
            DiscoveryRootSetV1 roots,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            ObservedCapability = capability;
            DuringDiscovery?.Invoke();
            if (ExceptionToThrow is not null)
            {
                return Task.FromException<CopilotSkillDiscoveryOutcome>(ExceptionToThrow);
            }

            var outcome = Outcome;
            AfterDiscoveryBeforeResultEnumeration?.Invoke();
            return Task.FromResult(outcome);
        }
    }

    private sealed class FakeNativeReader : ICurrentSkillNativeFileReaderV1
    {
        internal CurrentSkillNativeReadResultV1 Result { get; set; } =
            CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Missing);

        internal Action? BeforeRead { get; set; }

        internal Action? AfterRead { get; set; }

        internal bool WasCalled { get; private set; }

        public CurrentSkillNativeReadResultV1 Read(CurrentSkillReadTargetV1 target, CancellationToken cancellationToken)
        {
            WasCalled = true;
            BeforeRead?.Invoke();
            var result = Result;
            AfterRead?.Invoke();
            return result;
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
