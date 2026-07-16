using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.Doctor;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class GitHubCopilotDoctorEvidenceAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 1, 2, 3, TimeSpan.Zero);

    [Theory]
    [InlineData("vscode", "github-copilot-vscode", "vscode-copilot-chat", "vscode", "copilot-compatible-hook")]
    [InlineData("cli", "github-copilot-cli", "copilot-cli", "copilot-cli", "copilot-compatible-hook")]
    [InlineData("app-sdk", "github-copilot-app-sdk", "copilot-app-sdk", "copilot-sdk", "copilot-sdk-stream")]
    public void Observe_UsesOnlyExplicitExactRawSelection_ForEachSupportedPartition(
        string target,
        string sourceSurface,
        string clientKind,
        string nativeSurface,
        string eventAdapter)
    {
        using var database = TemporaryDatabase.Create();
        var verification = Start(database.Path, sourceSurface);
        var rawRecordId = CommitRaw(database.Path, clientKind, "0123456789abcdef0123456789abcdef");
        CompleteProjection(database.Path, rawRecordId);
        WriteSession(database.Path, "exact-native", "0123456789abcdef0123456789abcdef", nativeSurface, eventAdapter);

        var observed = GitHubCopilotDoctorEvidenceAdapter.Observe(
            database.Path,
            new AdjustableTimeProvider(Now),
            new(verification.VerificationId, target, rawRecordId, new(nativeSurface, "exact-native")));

        Assert.Equal(
            [
                DoctorEvidenceKind.Ingest,
                DoctorEvidenceKind.RawPersistence,
                DoctorEvidenceKind.Projection,
                DoctorEvidenceKind.ExactSessionBinding,
                DoctorEvidenceKind.CompletenessContent,
            ],
            observed.ObservedKinds);
        Assert.All(observed.EvidenceRefs, AssertOpaqueReference);
        Assert.Equal(5, observed.EvidenceRefs.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(DoctorResultCode.VerificationActive, observed.ObservationResult.Code);
        Assert.Empty(observed.Snapshot.Observations);
        Assert.Equal(verification.VerificationId, observed.Snapshot.VerificationId);
        Assert.Equal(sourceSurface, observed.Snapshot.SourceSurface);
        Assert.Equal("github-copilot-doctor", observed.Snapshot.ExpectedSourceAdapter);
        Assert.Equal(LastIngestOutcome.Accepted, observed.Snapshot.LastIngest!.Outcome);
        Assert.Equal(RawPersistenceOutcome.Persisted, observed.Snapshot.RawPersistence!.Outcome);
        Assert.Equal(ProjectionOutcome.Completed, observed.Snapshot.Projection!.Outcome);
        Assert.Equal(ExactSessionBindingOutcome.ExactBound, observed.Snapshot.ExactSessionBinding!.Outcome);
        Assert.Equal(DoctorCompleteness.Full, observed.Snapshot.CompletenessAndContent!.Completeness);

        var completed = CreateDoctor(database.Path, new AdjustableTimeProvider(Now))
            .Complete(
                verification.VerificationId,
                verification.Revision,
                WithReadyStaticFacts(observed.Snapshot),
                observed.EvidenceRefs);
        Assert.Equal(DoctorStateCode.FirstTraceReady, completed.Evaluation!.PrimaryState!.StateCode);
        Assert.Equal(DoctorNextAction.OpenVerifiedTraceOrSession, completed.Evaluation.PrimaryState.NextAction);
        AssertCanonicalCandidates(database.Path, verification, observed.EvidenceRefs, expectedCount: 5);
    }

    [Theory]
    [InlineData("not_started", ProjectionOutcome.NotStarted, DoctorStateCode.RawPersistedProjectionPending)]
    [InlineData("pending", ProjectionOutcome.Pending, DoctorStateCode.RawPersistedProjectionPending)]
    [InlineData("failed", ProjectionOutcome.Failed, DoctorStateCode.ProjectionFailed)]
    [InlineData("completed", ProjectionOutcome.Completed, DoctorStateCode.FirstTraceReady)]
    public void Observe_MapsExactProjectionDispositionIndependently(
        string disposition,
        ProjectionOutcome expectedOutcome,
        DoctorStateCode expectedState)
    {
        using var database = TemporaryDatabase.Create();
        var verification = Start(database.Path, "github-copilot-vscode");
        var rawRecordId = CommitRaw(database.Path, "vscode-copilot-chat", "0123456789abcdef0123456789abcdef");
        SetDisposition(database.Path, rawRecordId, disposition);
        WriteSession(
            database.Path,
            "exact-vscode-session",
            "0123456789abcdef0123456789abcdef",
            "vscode",
            "copilot-compatible-hook");

        var observed = GitHubCopilotDoctorEvidenceAdapter.Observe(
            database.Path,
            new AdjustableTimeProvider(Now),
            new(
                verification.VerificationId,
                "vscode",
                rawRecordId,
                new("vscode", "exact-vscode-session")));

        Assert.Equal(
            [
                DoctorEvidenceKind.Ingest,
                DoctorEvidenceKind.RawPersistence,
                DoctorEvidenceKind.Projection,
                DoctorEvidenceKind.ExactSessionBinding,
                DoctorEvidenceKind.CompletenessContent,
            ],
            observed.ObservedKinds);
        Assert.Equal(expectedOutcome, observed.Snapshot.Projection!.Outcome);
        Assert.Equal(5, observed.EvidenceRefs.Distinct(StringComparer.Ordinal).Count());
        AssertCanonicalCandidates(database.Path, verification, observed.EvidenceRefs, expectedCount: 5);

        var projectionRef = Assert.Single(
            ReadCandidates(database.Path, verification.VerificationId),
            row => row.EvidenceKind == "projection").EvidenceRef;
        var completed = CreateDoctor(database.Path, new AdjustableTimeProvider(Now))
            .Complete(
                verification.VerificationId,
                verification.Revision,
                WithReadyStaticFacts(observed.Snapshot),
                observed.EvidenceRefs);
        Assert.Equal(expectedState, completed.Evaluation!.PrimaryState!.StateCode);
        Assert.Contains(projectionRef, completed.Evaluation.PrimaryState.EvidenceRefs);
    }

    [Theory]
    [InlineData("vscode", "copilot-cli")]
    [InlineData("cli", "vscode-copilot-chat")]
    [InlineData("app-sdk", "vscode-copilot-chat")]
    [InlineData("vscode", "synthetic-probe")]
    [InlineData("vscode", "setup-success")]
    public void Observe_RejectsSpoofedOrNonRuntimeClientKind(string target, string clientKind)
    {
        using var database = TemporaryDatabase.Create();
        var sourceSurface = target switch
        {
            "cli" => "github-copilot-cli",
            "app-sdk" => "github-copilot-app-sdk",
            _ => "github-copilot-vscode",
        };
        var verification = Start(database.Path, sourceSurface);
        var rawRecordId = CommitRaw(database.Path, clientKind);

        var observed = Observe(database.Path, verification.VerificationId, target, rawRecordId);

        Assert.Empty(observed.ObservedKinds);
        Assert.Empty(observed.EvidenceRefs);
        AssertUnattributedUnknown(observed);
    }

    [Fact]
    public void Observe_RejectsMismatchedVerificationSourceAndMissingExactRows()
    {
        using var database = TemporaryDatabase.Create();
        var cliVerification = Start(database.Path, "github-copilot-cli");
        var rawRecordId = CommitRaw(database.Path, "vscode-copilot-chat");

        var sourceMismatch = Observe(database.Path, cliVerification.VerificationId, "vscode", rawRecordId);
        var clientKindMismatch = Observe(database.Path, cliVerification.VerificationId, "cli", rawRecordId);
        var missingRaw = Observe(database.Path, cliVerification.VerificationId, "cli", rawRecordId + 1);

        Assert.Empty(sourceMismatch.ObservedKinds);
        Assert.Empty(clientKindMismatch.ObservedKinds);
        Assert.Empty(missingRaw.ObservedKinds);
        AssertUnattributedUnknown(sourceMismatch);
        AssertUnattributedUnknown(clientKindMismatch);
        AssertUnattributedUnknown(missingRaw);
    }

    [Fact]
    public void Observe_MissingCompatibilityRejectsSourceAttribution()
    {
        using var database = TemporaryDatabase.Create();
        var verification = Start(database.Path, "github-copilot-vscode");
        var rawRecordId = CommitRaw(database.Path, "vscode-copilot-chat");
        Execute(database.Path, "DELETE FROM source_schema_observations WHERE raw_record_id = $raw_record_id;", rawRecordId);

        var observed = Observe(database.Path, verification.VerificationId, "vscode", rawRecordId);

        Assert.Empty(observed.ObservedKinds);
        Assert.Empty(observed.EvidenceRefs);
        AssertUnattributedUnknown(observed);
    }

    [Fact]
    public void Observe_MissingDispositionKeepsExactIngestAndPersistenceButProjectionUnknown()
    {
        using var database = TemporaryDatabase.Create();
        var verification = Start(database.Path, "github-copilot-vscode");
        var rawRecordId = CommitRaw(database.Path, "vscode-copilot-chat");
        Execute(database.Path, "DELETE FROM monitor_projection_dispositions WHERE raw_record_id = $raw_record_id;", rawRecordId);

        var observed = Observe(database.Path, verification.VerificationId, "vscode", rawRecordId);

        Assert.Equal([DoctorEvidenceKind.Ingest, DoctorEvidenceKind.RawPersistence], observed.ObservedKinds);
        Assert.Equal(ProjectionOutcome.Unknown, observed.Snapshot.Projection!.Outcome);
        Assert.Equal(2, observed.EvidenceRefs.Count);
        AssertCanonicalCandidates(database.Path, verification, observed.EvidenceRefs, expectedCount: 2);
    }

    [Fact]
    public void Observe_RejectsMismatchedCommittedSourceAdapter()
    {
        using var database = TemporaryDatabase.Create();
        var verification = Start(database.Path, "github-copilot-vscode");
        var rawRecordId = CommitRaw(database.Path, "vscode-copilot-chat");
        Execute(
            database.Path,
            "UPDATE source_schema_observations SET source_adapter = 'claude-code-otel' WHERE raw_record_id = $raw_record_id;",
            rawRecordId);

        var observed = Observe(database.Path, verification.VerificationId, "vscode", rawRecordId);

        Assert.Empty(observed.ObservedKinds);
        Assert.Empty(observed.EvidenceRefs);
        AssertUnattributedUnknown(observed);
    }

    [Fact]
    public void Observe_RejectsExpiredAndCancelledVerification()
    {
        using var database = TemporaryDatabase.Create();
        var clock = new AdjustableTimeProvider(Now);
        var service = CreateDoctor(database.Path, clock);
        var expired = service.Start("github-copilot-vscode", "github-copilot-doctor", Now.AddMinutes(1)).Verification!;
        var cancelled = service.Start("github-copilot-vscode", "github-copilot-doctor", Now.AddMinutes(5)).Verification!;
        service.Cancel(cancelled.VerificationId, cancelled.Revision);
        var rawRecordId = CommitRaw(database.Path, "vscode-copilot-chat");
        clock.UtcNow = Now.AddMinutes(2);

        var expiredResult = Observe(database.Path, expired.VerificationId, "vscode", rawRecordId, clock);
        var cancelledResult = Observe(database.Path, cancelled.VerificationId, "vscode", rawRecordId, clock);
        Assert.Empty(expiredResult.ObservedKinds);
        Assert.Empty(cancelledResult.ObservedKinds);
        AssertUnattributedUnknown(expiredResult);
        AssertUnattributedUnknown(cancelledResult);
    }

    [Fact]
    public void Observe_ExactNativeSelectionAddsBindingAndCompleteness_WithoutLeakingIdentity()
    {
        using var database = TemporaryDatabase.Create();
        var verification = Start(database.Path, "github-copilot-app-sdk");
        var rawRecordId = CommitRaw(database.Path, "copilot-app-sdk", traceId: "0123456789abcdef0123456789abcdef");
        CompleteProjection(database.Path, rawRecordId);
        WriteSession(database.Path, "native-session-secret", "0123456789abcdef0123456789abcdef");

        var selection = new GitHubCopilotDoctorEvidenceSelection(
            verification.VerificationId,
            "app-sdk",
            rawRecordId,
            new GitHubCopilotNativeSessionSelection("copilot-sdk", "native-session-secret"));
        var observed = GitHubCopilotDoctorEvidenceAdapter.Observe(database.Path, new AdjustableTimeProvider(Now), selection);

        Assert.Contains(DoctorEvidenceKind.ExactSessionBinding, observed.ObservedKinds);
        Assert.Contains(DoctorEvidenceKind.CompletenessContent, observed.ObservedKinds);
        Assert.All(observed.EvidenceRefs, reference =>
        {
            AssertOpaqueReference(reference);
            Assert.DoesNotContain("native-session-secret", reference, StringComparison.Ordinal);
            Assert.DoesNotContain("0123456789abcdef", reference, StringComparison.Ordinal);
            Assert.DoesNotContain(database.Path, reference, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Observe_WrongNativeIdAndTraceIdAloneRemainHonestlyUnbound()
    {
        using var database = TemporaryDatabase.Create();
        var verification = Start(database.Path, "github-copilot-app-sdk");
        var rawRecordId = CommitRaw(database.Path, "copilot-app-sdk", traceId: "0123456789abcdef0123456789abcdef");
        WriteSession(database.Path, "exact-native", "0123456789abcdef0123456789abcdef");

        var wrong = GitHubCopilotDoctorEvidenceAdapter.Observe(
            database.Path,
            new AdjustableTimeProvider(Now),
            new(verification.VerificationId, "app-sdk", rawRecordId, new("copilot-sdk", "wrong-native")));
        var traceOnly = Observe(database.Path, verification.VerificationId, "app-sdk", rawRecordId);

        Assert.DoesNotContain(DoctorEvidenceKind.ExactSessionBinding, wrong.ObservedKinds);
        Assert.DoesNotContain(DoctorEvidenceKind.ExactSessionBinding, traceOnly.ObservedKinds);
        Assert.True(wrong.SessionUnbound);
        Assert.True(traceOnly.SessionUnbound);
    }

    [Fact]
    public void Observe_MixedRawAndSessionSurfaceDoesNotBind()
    {
        using var database = TemporaryDatabase.Create();
        var verification = Start(database.Path, "github-copilot-vscode");
        var rawRecordId = CommitRaw(database.Path, "vscode-copilot-chat", traceId: "0123456789abcdef0123456789abcdef");
        WriteSession(database.Path, "native-session", "0123456789abcdef0123456789abcdef");

        var observed = GitHubCopilotDoctorEvidenceAdapter.Observe(
            database.Path,
            new AdjustableTimeProvider(Now),
            new(verification.VerificationId, "vscode", rawRecordId, new("copilot-sdk", "native-session")));

        Assert.DoesNotContain(DoctorEvidenceKind.ExactSessionBinding, observed.ObservedKinds);
        Assert.DoesNotContain(DoctorEvidenceKind.CompletenessContent, observed.ObservedKinds);
        Assert.True(observed.SessionUnbound);
    }

    [Fact]
    public void Observe_AppSdkWithoutExactNativeSessionProducesNoCandidates()
    {
        using var database = TemporaryDatabase.Create();
        var verification = Start(database.Path, "github-copilot-app-sdk");
        var rawRecordId = CommitRaw(database.Path, "copilot-app-sdk", "0123456789abcdef0123456789abcdef");
        CompleteProjection(database.Path, rawRecordId);

        var observed = Observe(database.Path, verification.VerificationId, "app-sdk", rawRecordId);

        Assert.Empty(observed.ObservedKinds);
        Assert.Empty(observed.EvidenceRefs);
        Assert.Empty(ReadCandidates(database.Path, verification.VerificationId));
    }

    [Fact]
    public void Observe_AppSdkRejectsWrongActualSessionAdapter()
    {
        using var database = TemporaryDatabase.Create();
        var verification = Start(database.Path, "github-copilot-app-sdk");
        var rawRecordId = CommitRaw(database.Path, "copilot-app-sdk", "0123456789abcdef0123456789abcdef");
        WriteSession(
            database.Path,
            "exact-native",
            "0123456789abcdef0123456789abcdef",
            "copilot-sdk",
            "claude-code-otel");

        var observed = GitHubCopilotDoctorEvidenceAdapter.Observe(
            database.Path,
            new AdjustableTimeProvider(Now),
            new(verification.VerificationId, "app-sdk", rawRecordId, new("copilot-sdk", "exact-native")));

        Assert.Empty(observed.ObservedKinds);
        Assert.Empty(observed.EvidenceRefs);
        Assert.Empty(ReadCandidates(database.Path, verification.VerificationId));
    }

    [Theory]
    [InlineData("vscode", "github-copilot-vscode", "vscode-copilot-chat", "vscode")]
    [InlineData("cli", "github-copilot-cli", "copilot-cli", "copilot-cli")]
    public void Observe_VsCodeAndCliWrongActualSessionAdapterDoNotCreateBindingCandidates(
        string target,
        string sourceSurface,
        string clientKind,
        string nativeSurface)
    {
        using var database = TemporaryDatabase.Create();
        var verification = Start(database.Path, sourceSurface);
        var rawRecordId = CommitRaw(database.Path, clientKind, "0123456789abcdef0123456789abcdef");
        CompleteProjection(database.Path, rawRecordId);
        WriteSession(
            database.Path,
            "exact-native",
            "0123456789abcdef0123456789abcdef",
            nativeSurface,
            "claude-code-otel");

        var observed = GitHubCopilotDoctorEvidenceAdapter.Observe(
            database.Path,
            new AdjustableTimeProvider(Now),
            new(verification.VerificationId, target, rawRecordId, new(nativeSurface, "exact-native")));

        Assert.Equal(
            [DoctorEvidenceKind.Ingest, DoctorEvidenceKind.RawPersistence, DoctorEvidenceKind.Projection],
            observed.ObservedKinds);
        Assert.DoesNotContain(DoctorEvidenceKind.ExactSessionBinding, observed.ObservedKinds);
        Assert.DoesNotContain(DoctorEvidenceKind.CompletenessContent, observed.ObservedKinds);
        Assert.DoesNotContain(ReadCandidates(database.Path, verification.VerificationId),
            row => row.EvidenceKind is "exact_session_binding" or "completeness_content");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(301)]
    public void Observe_RejectsRawOutsideExactVerificationWindow(int receivedAtSecondsFromStart)
    {
        using var database = TemporaryDatabase.Create();
        var verification = Start(database.Path, "github-copilot-vscode");
        var rawRecordId = CommitRaw(
            database.Path,
            "vscode-copilot-chat",
            receivedAt: Now.AddSeconds(receivedAtSecondsFromStart));

        var observed = Observe(database.Path, verification.VerificationId, "vscode", rawRecordId);

        Assert.Empty(observed.ObservedKinds);
        Assert.Empty(ReadCandidates(database.Path, verification.VerificationId));
    }

    [Fact]
    public void Observe_RepeatedExplicitSelectionIsDeterministicAndDoesNotGuessAnotherRecord()
    {
        using var database = TemporaryDatabase.Create();
        var verification = Start(database.Path, "github-copilot-vscode");
        var selected = CommitRaw(database.Path, "vscode-copilot-chat", traceId: "explicitly-selected");
        _ = CommitRaw(database.Path, "copilot-cli", traceId: "newer-wrong-source-decoy", receivedAt: Now.AddSeconds(1));

        var first = Observe(database.Path, verification.VerificationId, "vscode", selected);
        var second = Observe(database.Path, verification.VerificationId, "vscode", selected);

        Assert.Equal(first.ObservedKinds, second.ObservedKinds);
        Assert.Equal(first.EvidenceRefs, second.EvidenceRefs);
        Assert.Equal(DoctorResultCode.VerificationActive, first.ObservationResult.Code);
        Assert.Equal(first.ObservationResult.Code, second.ObservationResult.Code);
        Assert.All(first.EvidenceRefs, AssertOpaqueReference);
        Assert.Equal(first.EvidenceRefs.Count, ReadCandidates(database.Path, verification.VerificationId).Count);
    }

    [Fact]
    public void Observe_TwoExactSessionsOnOneRawShareRawRefsButKeepBindingRefsDistinctAndCompletable()
    {
        using var database = TemporaryDatabase.Create();
        const string traceId = "0123456789abcdef0123456789abcdef";
        var rawRecordId = CommitRaw(database.Path, "vscode-copilot-chat", traceId);
        CompleteProjection(database.Path, rawRecordId);
        WriteSession(database.Path, "native-one", traceId, "vscode", "copilot-compatible-hook", Now.AddSeconds(3), "event-one");
        WriteSession(database.Path, "native-two", traceId, "vscode", "copilot-compatible-hook", Now.AddSeconds(4), "event-two");
        var firstVerification = Start(database.Path, "github-copilot-vscode");
        var secondVerification = Start(database.Path, "github-copilot-vscode");

        AssertPairCompletes(database.Path, firstVerification, rawRecordId, completeSecondSelection: false);
        AssertPairCompletes(database.Path, secondVerification, rawRecordId, completeSecondSelection: true);
    }

    [Fact]
    public void Observe_ProjectionDispositionAfterVerificationExpiryKeepsOnlyIngestAndRawWithExactTimes()
    {
        using var database = TemporaryDatabase.Create();
        var verification = Start(database.Path, "github-copilot-vscode");
        var rawRecordId = CommitRaw(database.Path, "vscode-copilot-chat");
        SetDispositionUpdatedAt(database.Path, rawRecordId, "completed", Now.AddMinutes(6));

        var observed = Observe(database.Path, verification.VerificationId, "vscode", rawRecordId);

        Assert.Equal([DoctorEvidenceKind.Ingest, DoctorEvidenceKind.RawPersistence], observed.ObservedKinds);
        Assert.Equal(ProjectionOutcome.Unknown, observed.Snapshot.Projection!.Outcome);
        var rows = ReadCandidates(database.Path, verification.VerificationId);
        Assert.All(rows, row => Assert.Equal(Now, row.ObservedAt));
        Assert.DoesNotContain(rows, row => row.EvidenceKind == "projection");
    }

    [Theory]
    [InlineData("vscode", "github-copilot-vscode", "vscode-copilot-chat", "vscode", "copilot-compatible-hook", -1, 3)]
    [InlineData("cli", "github-copilot-cli", "copilot-cli", "copilot-cli", "copilot-compatible-hook", 301, 3)]
    [InlineData("app-sdk", "github-copilot-app-sdk", "copilot-app-sdk", "copilot-sdk", "copilot-sdk-stream", -1, 0)]
    [InlineData("app-sdk", "github-copilot-app-sdk", "copilot-app-sdk", "copilot-sdk", "copilot-sdk-stream", 301, 0)]
    public void Observe_SessionEvidenceOutsideVerificationWindowCannotCreateBindingCandidates(
        string target,
        string sourceSurface,
        string clientKind,
        string nativeSurface,
        string eventAdapter,
        int sessionSecondsFromStart,
        int expectedRawGateCount)
    {
        using var database = TemporaryDatabase.Create();
        const string traceId = "0123456789abcdef0123456789abcdef";
        var verification = Start(database.Path, sourceSurface);
        var rawRecordId = CommitRaw(database.Path, clientKind, traceId);
        CompleteProjection(database.Path, rawRecordId);
        WriteSession(
            database.Path,
            "exact-native",
            traceId,
            nativeSurface,
            eventAdapter,
            Now.AddSeconds(sessionSecondsFromStart));

        var observed = GitHubCopilotDoctorEvidenceAdapter.Observe(
            database.Path,
            new AdjustableTimeProvider(Now),
            new(verification.VerificationId, target, rawRecordId, new(nativeSurface, "exact-native")));

        Assert.Equal(expectedRawGateCount, observed.ObservedKinds.Count);
        Assert.DoesNotContain(DoctorEvidenceKind.ExactSessionBinding, observed.ObservedKinds);
        Assert.DoesNotContain(DoctorEvidenceKind.CompletenessContent, observed.ObservedKinds);
    }

    [Fact]
    public void Observe_MatchingTraceOnDifferentRunThanCorrectAdapterEventDoesNotBind()
    {
        using var database = TemporaryDatabase.Create();
        const string traceId = "0123456789abcdef0123456789abcdef";
        var verification = Start(database.Path, "github-copilot-vscode");
        var rawRecordId = CommitRaw(database.Path, "vscode-copilot-chat", traceId);
        CompleteProjection(database.Path, rawRecordId);
        WriteSplitRunSession(database.Path, "exact-native", traceId);

        var observed = GitHubCopilotDoctorEvidenceAdapter.Observe(
            database.Path,
            new AdjustableTimeProvider(Now),
            new(verification.VerificationId, "vscode", rawRecordId, new("vscode", "exact-native")));

        Assert.Equal(
            [DoctorEvidenceKind.Ingest, DoctorEvidenceKind.RawPersistence, DoctorEvidenceKind.Projection],
            observed.ObservedKinds);
        Assert.DoesNotContain(DoctorEvidenceKind.ExactSessionBinding, observed.ObservedKinds);
        Assert.DoesNotContain(DoctorEvidenceKind.CompletenessContent, observed.ObservedKinds);
    }

    [Fact]
    public void Observe_CandidateObservedAtUsesEachExactEvidenceTimestamp()
    {
        using var database = TemporaryDatabase.Create();
        const string traceId = "0123456789abcdef0123456789abcdef";
        var verification = Start(database.Path, "github-copilot-vscode");
        var rawRecordId = CommitRaw(database.Path, "vscode-copilot-chat", traceId);
        SetDispositionUpdatedAt(database.Path, rawRecordId, "completed", Now.AddSeconds(2));
        WriteSession(
            database.Path,
            "exact-native",
            traceId,
            "vscode",
            "copilot-compatible-hook",
            Now.AddSeconds(3));

        _ = GitHubCopilotDoctorEvidenceAdapter.Observe(
            database.Path,
            new AdjustableTimeProvider(Now),
            new(verification.VerificationId, "vscode", rawRecordId, new("vscode", "exact-native")));

        var rows = ReadCandidates(database.Path, verification.VerificationId).ToDictionary(row => row.EvidenceKind);
        Assert.Equal(Now, rows["ingest"].ObservedAt);
        Assert.Equal(Now, rows["raw_persistence"].ObservedAt);
        Assert.Equal(Now.AddSeconds(2), rows["projection"].ObservedAt);
        Assert.Equal(Now.AddSeconds(3), rows["exact_session_binding"].ObservedAt);
        Assert.Equal(Now.AddSeconds(3), rows["completeness_content"].ObservedAt);
    }

    [Fact]
    public void Observe_RawExactlyAtVerificationExpiryIsExcludedWithoutPartialCandidates()
    {
        using var database = TemporaryDatabase.Create();
        var verification = Start(database.Path, "github-copilot-vscode");
        var rawRecordId = CommitRaw(
            database.Path,
            "vscode-copilot-chat",
            receivedAt: verification.ExpiresAt);

        var observed = Observe(database.Path, verification.VerificationId, "vscode", rawRecordId);

        Assert.Empty(observed.ObservedKinds);
        Assert.Empty(observed.EvidenceRefs);
        Assert.Empty(ReadCandidates(database.Path, verification.VerificationId));
        AssertUnattributedUnknown(observed);
    }

    [Fact]
    public void Observe_DispositionExactlyAtVerificationExpiryIsExcludedWithoutProjectionLeakage()
    {
        using var database = TemporaryDatabase.Create();
        var verification = Start(database.Path, "github-copilot-vscode");
        var rawRecordId = CommitRaw(database.Path, "vscode-copilot-chat");
        SetDispositionUpdatedAt(database.Path, rawRecordId, "completed", verification.ExpiresAt);

        var observed = Observe(database.Path, verification.VerificationId, "vscode", rawRecordId);

        Assert.Equal([DoctorEvidenceKind.Ingest, DoctorEvidenceKind.RawPersistence], observed.ObservedKinds);
        Assert.Equal(ProjectionOutcome.Unknown, observed.Snapshot.Projection!.Outcome);
        Assert.Equal(2, ReadCandidates(database.Path, verification.VerificationId).Count);
        Assert.DoesNotContain(
            ReadCandidates(database.Path, verification.VerificationId),
            row => row.EvidenceKind == "projection");
    }

    [Theory]
    [InlineData("vscode", "github-copilot-vscode", "vscode-copilot-chat", "vscode", "copilot-compatible-hook", 3)]
    [InlineData("cli", "github-copilot-cli", "copilot-cli", "copilot-cli", "copilot-compatible-hook", 3)]
    [InlineData("app-sdk", "github-copilot-app-sdk", "copilot-app-sdk", "copilot-sdk", "copilot-sdk-stream", 0)]
    public void Observe_SessionExactlyAtVerificationExpiryCannotCreateBindingOrContent(
        string target,
        string sourceSurface,
        string clientKind,
        string nativeSurface,
        string eventAdapter,
        int expectedPersistedCount)
    {
        using var database = TemporaryDatabase.Create();
        const string traceId = "0123456789abcdef0123456789abcdef";
        var verification = Start(database.Path, sourceSurface);
        var rawRecordId = CommitRaw(database.Path, clientKind, traceId);
        CompleteProjection(database.Path, rawRecordId);
        WriteSession(
            database.Path,
            "expiry-native",
            traceId,
            nativeSurface,
            eventAdapter,
            verification.ExpiresAt);

        var observed = GitHubCopilotDoctorEvidenceAdapter.Observe(
            database.Path,
            new AdjustableTimeProvider(Now),
            new(verification.VerificationId, target, rawRecordId, new(nativeSurface, "expiry-native")));

        Assert.DoesNotContain(DoctorEvidenceKind.ExactSessionBinding, observed.ObservedKinds);
        Assert.DoesNotContain(DoctorEvidenceKind.CompletenessContent, observed.ObservedKinds);
        Assert.Equal(expectedPersistedCount, ReadCandidates(database.Path, verification.VerificationId).Count);
        Assert.True(observed.SessionUnbound);
        if (target == "app-sdk")
        {
            Assert.Empty(observed.ObservedKinds);
        }
        else
        {
            Assert.Equal(
                [DoctorEvidenceKind.Ingest, DoctorEvidenceKind.RawPersistence, DoctorEvidenceKind.Projection],
                observed.ObservedKinds);
        }
    }

    private static GitHubCopilotDoctorEvidenceResult Observe(
        string databasePath,
        string verificationId,
        string target,
        long rawRecordId,
        TimeProvider? timeProvider = null) =>
        GitHubCopilotDoctorEvidenceAdapter.Observe(
            databasePath,
            timeProvider ?? new AdjustableTimeProvider(Now),
            new GitHubCopilotDoctorEvidenceSelection(verificationId, target, rawRecordId, NativeSession: null));

    private static DoctorVerification Start(string databasePath, string sourceSurface)
    {
        var result = CreateDoctor(databasePath, new AdjustableTimeProvider(Now))
            .Start(sourceSurface, "github-copilot-doctor", Now.AddMinutes(5));
        return Assert.IsType<DoctorVerification>(result.Verification);
    }

    private static SqliteDoctorApplicationService CreateDoctor(string databasePath, TimeProvider timeProvider) =>
        SqliteDoctorApplicationService.Create(new SqliteDoctorVerificationStore(databasePath, timeProvider));

    private static long CommitRaw(
        string databasePath,
        string clientKind,
        string? traceId = null,
        DateTimeOffset? receivedAt = null)
    {
        var observedAt = receivedAt ?? Now;
        new SqliteSourceCompatibilityStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter).CreateSchema();
        var payload = "{\"resourceSpans\":[{\"resource\":{\"attributes\":[{\"key\":\"client.kind\",\"value\":{\"stringValue\":\""
            + clientKind
            + "\"}}]},\"scopeSpans\":[{\"spans\":[{\"traceId\":\""
            + (traceId ?? "trace")
            + "\",\"spanId\":\"span\"}]}]}]}";
        var inventory = OtlpJsonStructuralWalker.Build(payload, observedAt);
        var observation = SourceObservationBatchDraft.Create(
            Guid.CreateVersion7().ToString("D"),
            RawTelemetrySources.RawOtlp,
            sourceApplicationVersion: null,
            RawTelemetrySources.RawOtlp,
            "1",
            inventory,
            SourceCompatibilityEvaluator.Assess(
                RawTelemetrySources.RawOtlp,
                sourceApplicationVersion: null,
                inventory,
                observedRecognizedCount: 1,
                VerifiedSourceFingerprintRegistry.Create([], [], [])),
            SourceCaptureContentState.Available,
            observedAt);
        var raw = new RawTelemetryRecord(
            Id: null,
            RawTelemetrySources.RawOtlp,
            traceId,
            observedAt,
            $$"""{"client.kind":"{{clientKind}}"}""",
            payload);
        return new SqliteIngestionCommitStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter)
            .Commit(ValidatedIngestionBatch.Create(raw, observation)).RawRecordId;
    }

    private static void CompleteProjection(string databasePath, long rawRecordId) =>
        SetDisposition(databasePath, rawRecordId, "completed");

    private static void SetDisposition(string databasePath, long rawRecordId, string state)
    {
        var store = new RawTelemetryStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        var current = Assert.IsType<ProjectionDisposition>(store.GetProjectionDisposition(rawRecordId));
        if (state == "not_started") return;
        Assert.True(store.TryBeginProjection(rawRecordId, current.Revision, Now.AddSeconds(1)));
        current = Assert.IsType<ProjectionDisposition>(store.GetProjectionDisposition(rawRecordId));
        if (state == "pending") return;
        if (state == "failed")
        {
            Assert.True(store.RecordProjectionFailure(rawRecordId, current.Revision, Now.AddSeconds(2)));
            return;
        }
        Assert.True(store.ApplyProjection(
            rawRecordId,
            RawTelemetrySources.RawOtlp,
            Now,
            new MonitorRecordProjection(null, null, 1, []),
            Now.AddSeconds(2),
            current.Revision));
    }

    private static void SetDispositionUpdatedAt(
        string databasePath,
        long rawRecordId,
        string state,
        DateTimeOffset updatedAt)
    {
        Execute(
            databasePath,
            $"UPDATE monitor_projection_dispositions SET state = '{state}', revision = revision + 1, updated_at = '{updatedAt:yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'}' WHERE raw_record_id = $raw_record_id;",
            rawRecordId);
    }

    private static void WriteSession(
        string databasePath,
        string nativeId,
        string traceId,
        string nativeSurface = "copilot-sdk",
        string eventAdapter = "copilot-sdk-stream",
        DateTimeOffset? evidenceAt = null,
        string sourceEventId = "event-1")
    {
        var observedAt = evidenceAt ?? Now;
        var store = new SqliteSessionStore(databasePath, new AdjustableTimeProvider(Now));
        store.CreateSchema();
        var session = ObservedSession.Create(
            ObservedSessionStatus.Completed,
            SessionCompleteness.Full,
            repository: "must-not-be-used",
            workspace: "must-not-be-used",
            observedAt,
            observedAt,
            observedAt,
            SessionRawRetentionState.Expiring);
        var surface = SessionWire.ParseSourceSurface(nativeSurface);
        var native = new SessionNativeId(session.SessionId, surface, nativeId, SessionBindingKind.Native, observedAt);
        var run = ObservedSessionRun.Create(session.SessionId, ObservedSessionStatus.Completed) with
        {
            SourceSurface = surface,
            TraceId = traceId,
            StartedAt = observedAt,
            EndedAt = observedAt,
        };
        var @event = ObservedSessionEvent.Create(
            session.SessionId,
            run.RunId,
            eventAdapter,
            sourceEventId,
            "assistant.completed",
            observedAt,
            SessionContentState.Available) with { SourceSurface = surface, TraceId = traceId };
        store.Write(new SessionWriteBatch(new SessionDetail(session, [native], [run], [@event]), []));
    }

    private static void WriteSplitRunSession(string databasePath, string nativeId, string traceId)
    {
        var store = new SqliteSessionStore(databasePath, new AdjustableTimeProvider(Now));
        store.CreateSchema();
        var session = ObservedSession.Create(
            ObservedSessionStatus.Completed,
            SessionCompleteness.Full,
            null,
            null,
            Now,
            Now.AddSeconds(2),
            Now.AddSeconds(2),
            SessionRawRetentionState.Expiring);
        var native = new SessionNativeId(
            session.SessionId,
            SessionSourceSurface.VisualStudioCode,
            nativeId,
            SessionBindingKind.Native,
            Now);
        var traceRun = ObservedSessionRun.Create(session.SessionId, ObservedSessionStatus.Completed) with
        {
            SourceSurface = SessionSourceSurface.VisualStudioCode,
            TraceId = traceId,
        };
        var differentRun = ObservedSessionRun.Create(session.SessionId, ObservedSessionStatus.Completed) with
        {
            SourceSurface = SessionSourceSurface.VisualStudioCode,
            TraceId = "fedcba9876543210fedcba9876543210",
        };
        var @event = ObservedSessionEvent.Create(
            session.SessionId,
            differentRun.RunId,
            "copilot-compatible-hook",
            "split-run-event",
            "assistant.completed",
            Now.AddSeconds(2),
            SessionContentState.Available) with
        {
            SourceSurface = SessionSourceSurface.VisualStudioCode,
            TraceId = traceId,
        };
        store.Write(new SessionWriteBatch(
            new SessionDetail(session, [native], [traceRun, differentRun], [@event]),
            []));
    }

    private static void AssertPairCompletes(
        string databasePath,
        DoctorVerification verification,
        long rawRecordId,
        bool completeSecondSelection)
    {
        var first = GitHubCopilotDoctorEvidenceAdapter.Observe(
            databasePath,
            new AdjustableTimeProvider(Now),
            new(verification.VerificationId, "vscode", rawRecordId, new("vscode", "native-one")));
        var second = GitHubCopilotDoctorEvidenceAdapter.Observe(
            databasePath,
            new AdjustableTimeProvider(Now),
            new(verification.VerificationId, "vscode", rawRecordId, new("vscode", "native-two")));

        Assert.Equal(first.EvidenceRefs.Take(3), second.EvidenceRefs.Take(3));
        Assert.NotEqual(first.EvidenceRefs[3], second.EvidenceRefs[3]);
        Assert.NotEqual(first.EvidenceRefs[4], second.EvidenceRefs[4]);
        Assert.Equal(7, ReadCandidates(databasePath, verification.VerificationId).Count);
        var selected = completeSecondSelection ? second : first;
        var completed = CreateDoctor(databasePath, new AdjustableTimeProvider(Now)).Complete(
            verification.VerificationId,
            verification.Revision,
            WithReadyStaticFacts(selected.Snapshot),
            selected.EvidenceRefs);
        Assert.Equal(DoctorStateCode.FirstTraceReady, completed.Evaluation!.PrimaryState!.StateCode);
    }

    private static void AssertOpaqueReference(string reference)
    {
        Assert.Matches("^[a-z0-9_-]{1,128}$", reference);
    }

    private static void AssertUnattributedUnknown(GitHubCopilotDoctorEvidenceResult result)
    {
        Assert.False(result.SessionUnbound);
        Assert.Equal(ExactSessionBindingRequirement.Unknown, result.Snapshot.ExactSessionBinding!.Requirement);
        Assert.Equal(ExactSessionBindingOutcome.Unknown, result.Snapshot.ExactSessionBinding.Outcome);
        Assert.Equal(DoctorCompleteness.Unknown, result.Snapshot.CompletenessAndContent!.Completeness);
        Assert.Equal(ContentCaptureStatus.Unknown, result.Snapshot.CompletenessAndContent.ContentCapture);
        Assert.Equal(RawAccessStatus.Unknown, result.Snapshot.CompletenessAndContent.RawAccess);
    }

    private static DoctorFactSnapshot WithReadyStaticFacts(DoctorFactSnapshot snapshot) => snapshot with
    {
        InstallAndSourceVersion = new(MonitorInstallStatus.Installed, SourceVersionStatus.Supported, SourceFeatureStatus.Available),
        ProcessReceiverAndPort = new(MonitorProcessStatus.Running, ReceiverBindStatus.Bound, PortOwnerStatus.Monitor),
        SourceEffectiveConfiguration = new(EndpointAlignmentStatus.Match),
        EndpointReachability = new(ReachabilityStatus.Reachable),
        ProtocolAndSignalCompatibility = new(ProtocolStatus.HttpProtobuf, TraceSignalStatus.Enabled),
        SourceVersionAndSchemaDiagnostics = new(SourceCompatibilityStatus.Supported, SchemaStatus.Matching),
        RestartOrNewProcess = new(RestartRequirement.NotRequired),
    };

    private static void AssertCanonicalCandidates(
        string databasePath,
        DoctorVerification verification,
        IReadOnlyList<string> expectedRefs,
        int expectedCount)
    {
        var candidates = ReadCandidates(databasePath, verification.VerificationId);
        Assert.Equal(expectedCount, candidates.Count);
        Assert.Equal(expectedRefs.Order(StringComparer.Ordinal), candidates.Select(row => row.EvidenceRef).Order(StringComparer.Ordinal));
        Assert.All(candidates, row =>
        {
            Assert.Equal("github-copilot-doctor", row.SourceAdapter);
            Assert.Equal(verification.ExpectedSourceSurface, row.SourceSurface);
            Assert.Equal("real_source", row.EvidenceClass);
            Assert.InRange(row.ObservedAt, verification.StartedAt, verification.ExpiresAt);
            Assert.Equal(verification.ExpiresAt, row.ExpiresAt);
            AssertOpaqueReference(row.EvidenceRef);
            var candidateId = Guid.ParseExact(row.CandidateId, "D");
            Assert.Equal(7, candidateId.Version);
            Assert.Equal(row.ObservedAt, UuidV7Timestamp(candidateId));
        });
    }

    private static DateTimeOffset UuidV7Timestamp(Guid value)
    {
        var bytes = value.ToByteArray(bigEndian: true);
        var unixMilliseconds =
            ((long)bytes[0] << 40) |
            ((long)bytes[1] << 32) |
            ((long)bytes[2] << 24) |
            ((long)bytes[3] << 16) |
            ((long)bytes[4] << 8) |
            bytes[5];
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
    }

    private static IReadOnlyList<CandidateRow> ReadCandidates(string databasePath, string verificationId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT candidate_id, source_surface, source_adapter, evidence_class, evidence_kind, evidence_ref, observed_at, expires_at
            FROM doctor_verification_evidence
            WHERE verification_id = $verification_id
            ORDER BY evidence_kind;
            """;
        command.Parameters.AddWithValue("$verification_id", verificationId);
        using var reader = command.ExecuteReader();
        var rows = new List<CandidateRow>();
        while (reader.Read())
        {
            rows.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        return rows;
    }

    private static void Execute(string databasePath, string sql, long rawRecordId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$raw_record_id", rawRecordId);
        command.ExecuteNonQuery();
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed record CandidateRow(
        string CandidateId,
        string SourceSurface,
        string SourceAdapter,
        string EvidenceClass,
        string EvidenceKind,
        string EvidenceRef,
        DateTimeOffset ObservedAt,
        DateTimeOffset ExpiresAt);

    private sealed class TemporaryDatabase : IDisposable
    {
        private TemporaryDatabase(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDatabase Create() =>
            new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"issue103-{Guid.NewGuid():N}.db"));

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
