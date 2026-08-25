using System.Text.Json;
using System.Net.Http.Json;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class Issue158OwnedSessionLiveHarnessTests
{
    private const string ExpectedSyntheticSkillName = "issue158-synthetic";
    private const string ExpectedSyntheticCompletionToken = "ISSUE158_RETAINED_ROOT_OK";
    private const string ExpectedSyntheticSkillDocument = "---\nname: issue158-synthetic\ndescription: Return the exact token ISSUE158_RETAINED_ROOT_OK when invoked.\nuser-invocable: true\n---\n\nReturn exactly ISSUE158_RETAINED_ROOT_OK, then call task_complete as the final action with successful synthetic completion.\n";

    [WindowsOwnedSessionLiveFact]
    [Trait("Issue158Lane", "WindowsOwnedSession")]
    public async Task WindowsOwnedSession_TraversesTheProductionHost()
    {
        await Issue158WindowsOwnedSessionLane.RunAsync();
    }

    [Fact]
    public void DefaultDiscoveryIsSkipped() =>
        Assert.NotNull(new WindowsOwnedSessionLiveFactAttribute().Skip);

    [Fact]
    public void SyntheticSkillFixtureCarriesTheProvenInvocationAndTerminalContract()
    {
        Assert.Equal(ExpectedSyntheticSkillName, Issue158WindowsOwnedSessionLane.SyntheticSkillName);
        Assert.Equal(ExpectedSyntheticCompletionToken, Issue158WindowsOwnedSessionLane.SyntheticCompletionToken);
        Assert.Equal(ExpectedSyntheticSkillDocument, Issue158WindowsOwnedSessionLane.SyntheticSkillDocument);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ExactSyntheticSkillFixtureContractRejectsEveryReviewedMutation(int mutation)
    {
        var name = ExpectedSyntheticSkillName;
        var token = ExpectedSyntheticCompletionToken;
        var document = ExpectedSyntheticSkillDocument;
        switch (mutation)
        {
            case 0: name = "issue158-drift"; break;
            case 1: token = "ISSUE158_RETAINED_ROOT_DRIFT"; break;
            case 2: document = document.Replace("user-invocable: true\n---\n\n", "---\n\nuser-invocable: true\n", StringComparison.Ordinal); break;
            case 3: document = document.Replace("user-invocable: true\n", "user-invocable: true\nextra: synthetic\n", StringComparison.Ordinal); break;
            case 4: document = document.Replace("as the final action with successful synthetic completion.", "before successful synthetic completion.", StringComparison.Ordinal); break;
            default: throw new InvalidOperationException();
        }
        Assert.False(MatchesSyntheticSkillFixtureContract(name, token, document));
    }

    [Fact]
    public void CheckpointWireLiteralsAreClosedAndOrdered()
    {
        Assert.Equal(
            ["client_started", "identity_certified", "candidate_created", "probe_certified", "execution_inventory_certified", "driver_completed", "callbacks_frozen", "import_completed", "candidate_ready", "candidate_published"],
            Enum.GetValues<OwnedSessionExecutionCheckpointV1>().Select(Issue158WindowsBlocker.CheckpointWire));
    }

    [Fact]
    public void CheckpointObserverIsOptionalAndCannotChangeExecution()
    {
        OwnedSessionExecutionCheckpointObservationV1.Notify(null, OwnedSessionExecutionCheckpointV1.ClientStarted);
        OwnedSessionExecutionCheckpointObservationV1.Notify(_ => throw new InvalidOperationException(), OwnedSessionExecutionCheckpointV1.ClientStarted);
    }

    [Fact]
    public void PoisonWireMapsEveryClosedReason()
    {
        Assert.Equal(
            ["work_token_pre_canceled", "closed_relevant_event", "session_start_contract", "session_binding_contract", "invocation_identity", "invocation_description", "invocation_content", "invocation_native_reproof", "invocation_preparation", "invocation_buffer", "terminal_contract", "session_error", "model_call_failure", "abort", "callback_exception"],
            Enum.GetValues<OwnedSessionDiagnosticEventV1>().Skip(2).Select(value => Issue158WindowsBlocker.PoisonWire(value)));
    }

    [Fact]
    public void PhaseWireMapsBothClosedPhases() => Assert.Equal(
        ["command_pending", "send_pending"],
        Enum.GetValues<OwnedSessionDiagnosticEventV1>().Take(2).Select(value => Issue158WindowsBlocker.PhaseWire(value)));

    [Fact]
    public void PostFreezeWireMapsEveryClosedFailureAndSuccessToNone() => Assert.Equal(
        ["none", "candidate_not_admitted", "prepared_body_rejected", "candidate_lost_during_first_v2", "first_v2_forward_unavailable", "candidate_lost_during_later_v2", "later_v2_forward_unavailable", "start_envelope_rejected", "terminal_envelope_rejected", "start_validation_rejected", "start_queue_refused", "start_commit_busy", "start_commit_failed", "start_commit_timeout", "start_commit_canceled", "terminal_validation_rejected", "terminal_queue_refused", "terminal_commit_busy", "terminal_commit_failed", "terminal_commit_timeout", "terminal_commit_canceled", "unexpected_import_exception"],
        Enum.GetValues<OwnedSessionPostFreezeOutcomeV1>().Select(value => Issue158WindowsBlocker.PostFreezeWire(value)));

    [Fact]
    public void PostFreezeCaptureRetainsOnlyFirstValueUnderConcurrentReports()
    {
        var sync = new object();
        OwnedSessionPostFreezeOutcomeV1? captured = null;
        Issue158PostFreezeFailureCapture.Capture(sync, ref captured, OwnedSessionPostFreezeOutcomeV1.PreparedBodyRejected);
        Parallel.ForEach(Enum.GetValues<OwnedSessionPostFreezeOutcomeV1>().Skip(1),
            value => Issue158PostFreezeFailureCapture.Capture(sync, ref captured, value));
        Assert.Equal(OwnedSessionPostFreezeOutcomeV1.PreparedBodyRejected, captured);
    }

    [Fact]
    public void BlockerSerializationCarriesEveryClosedReason()
    {
        foreach (var reason in Enum.GetValues<OwnedSessionDiagnosticEventV1>().Skip(2))
        {
            using var document = JsonDocument.Parse(Issue158WindowsBlocker.Serialize(
                new string('a', 40), "canceled", null, null, reason));
            Assert.Equal("none", document.RootElement.GetProperty("driver_phase").GetString());
            Assert.Equal(Issue158WindowsBlocker.PoisonWire(reason), document.RootElement.GetProperty("poison_reason").GetString());
        }
    }

    [Fact]
    public void BlockerSerializationCarriesBothPhasesAndNone()
    {
        foreach (var phase in new OwnedSessionDiagnosticEventV1?[]
            { null, OwnedSessionDiagnosticEventV1.CommandPending, OwnedSessionDiagnosticEventV1.SendPending })
        {
            using var document = JsonDocument.Parse(Issue158WindowsBlocker.Serialize(
                new string('a', 40), "timed_out", OwnedSessionExecutionCheckpointV1.ExecutionInventoryCertified,
                phase, null));
            Assert.Equal(Issue158WindowsBlocker.PhaseWire(phase), document.RootElement.GetProperty("driver_phase").GetString());
            Assert.Equal("none", document.RootElement.GetProperty("poison_reason").GetString());
        }
    }

    [Fact]
    public void BlockerDocumentIsStrictAndSanitized()
    {
        var json = Issue158WindowsBlocker.Serialize("a".PadLeft(40, 'a'), "failed", OwnedSessionExecutionCheckpointV1.DriverCompleted);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(9, document.RootElement.EnumerateObject().Count());
        Assert.Equal("issue-158-windows-blocker.v6", document.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("failed", document.RootElement.GetProperty("terminal_status").GetString());
        Assert.Equal("driver_completed", document.RootElement.GetProperty("last_checkpoint").GetString());
        Assert.Equal("none", document.RootElement.GetProperty("driver_phase").GetString());
        Assert.Equal("none", document.RootElement.GetProperty("poison_reason").GetString());
        Assert.Equal("none", document.RootElement.GetProperty("post_freeze_failure").GetString());
        Assert.Equal("none", document.RootElement.GetProperty("post_success_failure").GetString());
        Assert.Equal("none", document.RootElement.GetProperty("execution_evidence_failure").GetString());
    }

    [Fact]
    public void PostSuccessWireMapsEveryClosedStage()
    {
        Assert.Equal(
            ["none", "publication_barrier", "execution_evidence", "committed_identity_sequence", "metadata_request", "historical_request", "current_file_request", "route_matrix", "metadata_document", "historical_document", "current_file_document", "persistence_counts", "aggregate_counts", "observed_result", "shutdown_drain", "result_serialization", "result_write", "unexpected"],
            Enum.GetValues<Issue158PostSuccessFailureV1>().Select(value => Issue158WindowsBlocker.PostSuccessWire(value)));
    }

    [Fact]
    public void PostSuccessCaptureRetainsOnlyFirstValueAcrossConcurrentAndShutdownReports()
    {
        var capture = new Issue158PostSuccessFailureCapture();
        capture.Capture(Issue158PostSuccessFailureV1.MetadataDocument);
        Parallel.ForEach(Enum.GetValues<Issue158PostSuccessFailureV1>().Skip(1), capture.Capture);
        capture.Capture(Issue158PostSuccessFailureV1.ShutdownDrain);
        Assert.Equal(Issue158PostSuccessFailureV1.MetadataDocument, capture.Value);
    }

    [Fact]
    public async Task UnguardedPostSuccessFailurePrecedesShutdownFailure()
    {
        var capture = new Issue158PostSuccessFailureCapture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Issue158PostSuccessExecution.RunWithShutdownAsync(
            () => throw new InvalidOperationException("body"),
            () => throw new InvalidOperationException("shutdown"), () => true, capture));
        Assert.Equal(Issue158PostSuccessFailureV1.Unexpected, capture.Value);
    }

    [Fact]
    public async Task StagedPostSuccessFailurePrecedesShutdownFailure()
    {
        var capture = new Issue158PostSuccessFailureCapture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Issue158PostSuccessExecution.RunWithShutdownAsync(
            () => { capture.Capture(Issue158PostSuccessFailureV1.MetadataDocument); throw new InvalidOperationException("body"); },
            () => throw new InvalidOperationException("shutdown"), () => true, capture));
        Assert.Equal(Issue158PostSuccessFailureV1.MetadataDocument, capture.Value);
    }

    [Fact]
    public async Task ShutdownFailureIsCapturedWhenBodySucceeds()
    {
        var capture = new Issue158PostSuccessFailureCapture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Issue158PostSuccessExecution.RunWithShutdownAsync(
            () => Task.CompletedTask,
            () => throw new InvalidOperationException("shutdown"), () => true, capture));
        Assert.Equal(Issue158PostSuccessFailureV1.ShutdownDrain, capture.Value);
    }

    [Fact]
    public async Task CurrentFileRequestPreparationFailurePrecedesShutdownFailure()
    {
        var capture = new Issue158PostSuccessFailureCapture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Issue158PostSuccessExecution.RunWithShutdownAsync(
            () => Issue158PostSuccessExecution.RunStageAsync(
                Issue158PostSuccessFailureV1.CurrentFileRequest, capture,
                () => throw new InvalidOperationException("request preparation")),
            () => throw new InvalidOperationException("shutdown"), () => true, capture));
        Assert.Equal(Issue158PostSuccessFailureV1.CurrentFileRequest, capture.Value);
    }

    [Fact]
    public async Task PublicationBarrierCompletesOnlyAfterPublishedAndThenEvidenceIsVisible()
    {
        var state = new Issue158PublicationEvidenceState(new object());
        var capture = new Issue158PostSuccessFailureCapture();
        var currentGeneration = false;
        var wait = state.WaitAndCertifyAsync(capture, CancellationToken.None);
        state.ObserveCheckpoint(OwnedSessionExecutionCheckpointV1.CandidateReady);
        Assert.False(wait.IsCompleted);
        Assert.False(currentGeneration);
        Assert.Equal(OwnedSessionExecutionCheckpointV1.CandidateReady, state.LastCheckpoint);
        Assert.Equal(Issue158ExecutionEvidenceFailureV1.None, state.EvidenceFailure);
        var expected = ValidEvidence();
        state.ObserveEvidence(expected);
        currentGeneration = true;
        state.ObserveCheckpoint(OwnedSessionExecutionCheckpointV1.CandidatePublished);
        Assert.Equal(expected, await wait);
        Assert.Equal(OwnedSessionExecutionCheckpointV1.CandidatePublished, state.LastCheckpoint);
        Assert.Equal(Issue158ExecutionEvidenceFailureV1.None, state.EvidenceFailure);
        Assert.True(currentGeneration);
        state.ObserveCheckpoint(OwnedSessionExecutionCheckpointV1.CandidatePublished);
    }

    [Fact]
    public async Task PublicationBarrierFailureCapturesItsClosedStage()
    {
        var state = new Issue158PublicationEvidenceState(new object());
        var capture = new Issue158PostSuccessFailureCapture();
        state.ObserveCheckpoint(OwnedSessionExecutionCheckpointV1.CandidateReady);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            state.WaitAndCertifyAsync(capture, new CancellationToken(true)));
        Assert.Equal(Issue158PostSuccessFailureV1.PublicationBarrier, capture.Value);
        using var blocker = JsonDocument.Parse(Issue158WindowsBlocker.Serialize(new string('a', 40), "succeeded",
            state.LastCheckpoint, null, null, null, capture.Value, state.EvidenceFailure));
        Assert.Equal("candidate_ready", blocker.RootElement.GetProperty("last_checkpoint").GetString());
    }

    [Fact]
    public async Task PublishedStateClassifiesMissingAndMultipleObserversThroughRealContinuation()
    {
        var missing = new Issue158PublicationEvidenceState(new object());
        var missingCapture = new Issue158PostSuccessFailureCapture();
        missing.ObserveCheckpoint(OwnedSessionExecutionCheckpointV1.CandidatePublished);
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.WaitAndCertifyAsync(missingCapture, new CancellationToken(true)));
        Assert.Equal(Issue158PostSuccessFailureV1.ExecutionEvidence, missingCapture.Value);
        Assert.Equal(Issue158ExecutionEvidenceFailureV1.ObserverMissing, missing.EvidenceFailure);

        var multiple = new Issue158PublicationEvidenceState(new object());
        multiple.ObserveEvidence(ValidEvidence());
        multiple.ObserveEvidence(ValidEvidence());
        multiple.ObserveCheckpoint(OwnedSessionExecutionCheckpointV1.CandidatePublished);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            multiple.WaitAndCertifyAsync(new Issue158PostSuccessFailureCapture(), CancellationToken.None));
        Assert.Equal(Issue158ExecutionEvidenceFailureV1.ObserverMultiple, multiple.EvidenceFailure);
    }

    [Fact]
    public void EvidenceClassifierUsesSemanticInventoriesAndClosedPrecedence()
    {
        Assert.Equal(Issue158ExecutionEvidenceFailureV1.ObserverMissing, Issue158ExecutionEvidenceClassifier.Classify([]));
        Assert.Equal(Issue158ExecutionEvidenceFailureV1.ObserverMultiple,
            Issue158ExecutionEvidenceClassifier.Classify([ValidEvidence(), ValidEvidence() with { ProtocolVersion = 4 }]));
        Assert.Equal(Issue158ExecutionEvidenceFailureV1.None,
            Issue158ExecutionEvidenceClassifier.Classify([ValidEvidence() with { ProbeInventoryCount = 2, ExecutionInventoryCount = 2 }]));
        Assert.Equal(Issue158ExecutionEvidenceFailureV1.None,
            Issue158ExecutionEvidenceClassifier.Classify([ValidEvidence() with { ProbeInventoryCount = 2, ExecutionInventoryCount = 1 }]));
        Assert.Equal(Issue158ExecutionEvidenceFailureV1.ProtocolVersion,
            Issue158ExecutionEvidenceClassifier.Classify([ValidEvidence() with { ProbeInventoryCount = 0, ProtocolVersion = 4 }]));
        Assert.Equal(Issue158ExecutionEvidenceFailureV1.ExecutionInventoryCount,
            Issue158ExecutionEvidenceClassifier.Classify([ValidEvidence() with { ProbeInventoryCount = 2, ExecutionInventoryCount = 3 }]));
        Assert.Equal(Issue158ExecutionEvidenceFailureV1.SourceApplicationVersion,
            Issue158ExecutionEvidenceClassifier.Classify([ValidEvidence() with { SourceApplicationVersion = "bad", ProtocolVersion = 4 }]));
    }

    [Fact]
    public void EvidenceFailureWireMapsEveryClosedValue()
    {
        Assert.Equal(
            ["none", "observer_missing", "observer_multiple", "source_application_version", "protocol_version", "client_start_count", "status_observation_count", "probe_session_count", "execution_session_count", "retained_root_count", "retained_skill_count", "probe_inventory_count", "execution_inventory_count", "prepared_invocation_none", "prepared_invocation_excess", "same_client", "exact_tool_union", "retained_only_inventory", "probe_native_reproof", "execution_native_reproof", "callback_native_reproof"],
            Enum.GetValues<Issue158ExecutionEvidenceFailureV1>().Select(value => Issue158WindowsBlocker.ExecutionEvidenceWire(value)));
    }

    [Fact]
    public void EvidenceClassifierNamesEveryScalarAndBooleanMismatch()
    {
        var valid = ValidEvidence();
        (OwnedSessionExecutionEvidenceV1 Value, Issue158ExecutionEvidenceFailureV1 Failure)[] cases =
        [
            (valid with { SourceApplicationVersion = "bad" }, Issue158ExecutionEvidenceFailureV1.SourceApplicationVersion),
            (valid with { ProtocolVersion = 4 }, Issue158ExecutionEvidenceFailureV1.ProtocolVersion),
            (valid with { ClientStartCount = 0 }, Issue158ExecutionEvidenceFailureV1.ClientStartCount),
            (valid with { StatusObservationCount = 0 }, Issue158ExecutionEvidenceFailureV1.StatusObservationCount),
            (valid with { ProbeSessionCount = 0 }, Issue158ExecutionEvidenceFailureV1.ProbeSessionCount),
            (valid with { ExecutionSessionCount = 0 }, Issue158ExecutionEvidenceFailureV1.ExecutionSessionCount),
            (valid with { RetainedRootCount = 0 }, Issue158ExecutionEvidenceFailureV1.RetainedRootCount),
            (valid with { RetainedSkillCount = 0 }, Issue158ExecutionEvidenceFailureV1.RetainedSkillCount),
            (valid with { ProbeInventoryCount = 0 }, Issue158ExecutionEvidenceFailureV1.ProbeInventoryCount),
            (valid with { ExecutionInventoryCount = 0 }, Issue158ExecutionEvidenceFailureV1.ExecutionInventoryCount),
            (valid with { PreparedInvocationCount = 0 }, Issue158ExecutionEvidenceFailureV1.PreparedInvocationNone),
            (valid with { PreparedInvocationCount = 65 }, Issue158ExecutionEvidenceFailureV1.PreparedInvocationExcess),
            (valid with { SameClient = false }, Issue158ExecutionEvidenceFailureV1.SameClient),
            (valid with { ExactToolUnion = false }, Issue158ExecutionEvidenceFailureV1.ExactToolUnion),
            (valid with { RetainedOnlyInventory = false }, Issue158ExecutionEvidenceFailureV1.RetainedOnlyInventory),
            (valid with { ProbeNativeReproof = false }, Issue158ExecutionEvidenceFailureV1.ProbeNativeReproof),
            (valid with { ExecutionNativeReproof = false }, Issue158ExecutionEvidenceFailureV1.ExecutionNativeReproof),
            (valid with { CallbackNativeReproof = false }, Issue158ExecutionEvidenceFailureV1.CallbackNativeReproof),
        ];
        foreach (var item in cases)
            Assert.Equal(item.Failure, Issue158ExecutionEvidenceClassifier.Classify([item.Value]));
    }

    [Theory]
    [InlineData(0, "prepared_invocation_none")]
    [InlineData(1, "none")]
    [InlineData(2, "none")]
    [InlineData(3, "none")]
    [InlineData(64, "none")]
    [InlineData(65, "prepared_invocation_excess")]
    public void EvidenceClassifierClosesPreparedInvocationCardinality(int count, string expected) =>
        Assert.Equal(expected, Issue158WindowsBlocker.ExecutionEvidenceWire(
            Issue158ExecutionEvidenceClassifier.Classify([ValidEvidence() with { PreparedInvocationCount = count }])));

    [Fact]
    public void EvidenceClassifierFailsClosedForImpossibleNegativePreparedInvocationCount()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Issue158ExecutionEvidenceClassifier.Classify([ValidEvidence() with { PreparedInvocationCount = -1 }]));
        Assert.Equal("execution_evidence", exception.Message);
    }

    [Fact]
    public void BlockerAcceptsOnlyClosedEvidenceDetailCrossPairAndPublicationCheckpoints()
    {
        foreach (var checkpoint in new[] { OwnedSessionExecutionCheckpointV1.CandidateReady, OwnedSessionExecutionCheckpointV1.CandidatePublished })
            Issue158WindowsBlocker.Serialize(new string('a', 40), "succeeded", checkpoint, null, null, null,
                Issue158PostSuccessFailureV1.PublicationBarrier);
        foreach (var detail in Enum.GetValues<Issue158ExecutionEvidenceFailureV1>().Skip(1))
        {
            using var document = JsonDocument.Parse(Issue158WindowsBlocker.Serialize(new string('a', 40), "succeeded",
                OwnedSessionExecutionCheckpointV1.CandidatePublished, null, null, null,
                Issue158PostSuccessFailureV1.ExecutionEvidence, detail));
            Assert.Equal(Issue158WindowsBlocker.ExecutionEvidenceWire(detail),
                document.RootElement.GetProperty("execution_evidence_failure").GetString());
        }
        Assert.Throws<InvalidOperationException>(() => Issue158WindowsBlocker.Serialize(new string('a', 40), "succeeded",
            OwnedSessionExecutionCheckpointV1.CandidateReady, null, null, null, Issue158PostSuccessFailureV1.ExecutionEvidence,
            Issue158ExecutionEvidenceFailureV1.ObserverMissing));
        Assert.Throws<InvalidOperationException>(() => Issue158WindowsBlocker.Serialize(new string('a', 40), "succeeded",
            OwnedSessionExecutionCheckpointV1.CandidatePublished, null, null, null, Issue158PostSuccessFailureV1.PublicationBarrier,
            Issue158ExecutionEvidenceFailureV1.ObserverMissing));
    }

    [Fact]
    public void SucceededBlockerRequiresOnePostSuccessStageAndClosedSuccessFields()
    {
        Assert.Throws<InvalidOperationException>(() => Issue158WindowsBlocker.Serialize(
            new string('a', 40), "succeeded", OwnedSessionExecutionCheckpointV1.CandidatePublished,
            null, null, null, null));
        using var document = JsonDocument.Parse(Issue158WindowsBlocker.Serialize(
            new string('a', 40), "succeeded", OwnedSessionExecutionCheckpointV1.CandidatePublished,
            null, null, null, Issue158PostSuccessFailureV1.PersistenceCounts));
        Assert.Equal("persistence_counts", document.RootElement.GetProperty("post_success_failure").GetString());
    }

    [Fact]
    public async Task BlockerWriteIsCreateOnce()
    {
        using var fixture = new OwnedGateFixture();
        var gate = fixture.Read();
        await Issue158WindowsBlocker.WriteAsync(gate, "failed", OwnedSessionExecutionCheckpointV1.DriverCompleted,
            null, null, null);
        await Assert.ThrowsAsync<IOException>(() => Issue158WindowsBlocker.WriteAsync(gate, "failed",
            OwnedSessionExecutionCheckpointV1.DriverCompleted, null, null, null));
    }

    [Theory]
    [InlineData("failed", 9)]
    [InlineData("canceled", 9)]
    [InlineData("timed_out", 9)]
    [InlineData("succeeded", 0)]
    public void BlockerRejectsInvalidTerminalPostSuccessCrossPairs(string status, int stage)
    {
        Assert.Throws<InvalidOperationException>(() => Issue158WindowsBlocker.Serialize(
            new string('a', 40), status, OwnedSessionExecutionCheckpointV1.CandidatePublished,
            null, null, null, (Issue158PostSuccessFailureV1)stage));
    }

    [Fact]
    public void SucceededBlockerRejectsEveryWrongClosedCrossField()
    {
        foreach (var checkpoint in new OwnedSessionExecutionCheckpointV1?[] { null }.Concat(
            Enum.GetValues<OwnedSessionExecutionCheckpointV1>().Where(value => value != OwnedSessionExecutionCheckpointV1.CandidatePublished).Select(value => (OwnedSessionExecutionCheckpointV1?)value)))
            Assert.Throws<InvalidOperationException>(() => Issue158WindowsBlocker.Serialize(new string('a', 40), "succeeded",
                checkpoint, null, null, null, Issue158PostSuccessFailureV1.Unexpected));
        foreach (var phase in new[] { OwnedSessionDiagnosticEventV1.CommandPending, OwnedSessionDiagnosticEventV1.SendPending })
            Assert.Throws<InvalidOperationException>(() => Issue158WindowsBlocker.Serialize(new string('a', 40), "succeeded",
                OwnedSessionExecutionCheckpointV1.CandidatePublished, phase, null, null, Issue158PostSuccessFailureV1.Unexpected));
        foreach (var reason in Enum.GetValues<OwnedSessionDiagnosticEventV1>().Skip(2))
            Assert.Throws<InvalidOperationException>(() => Issue158WindowsBlocker.Serialize(new string('a', 40), "succeeded",
                OwnedSessionExecutionCheckpointV1.CandidatePublished, null, reason, null, Issue158PostSuccessFailureV1.Unexpected));
        foreach (var postFreeze in Enum.GetValues<OwnedSessionPostFreezeOutcomeV1>().Skip(1))
            Assert.Throws<InvalidOperationException>(() => Issue158WindowsBlocker.Serialize(new string('a', 40), "succeeded",
                OwnedSessionExecutionCheckpointV1.CandidatePublished, null, null, postFreeze, Issue158PostSuccessFailureV1.Unexpected));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void CurrentPersistenceFactsVerifySingleAndDoubleInvocationTopologies(bool includeAgent, long total)
    {
        var path = Path.Combine(Path.GetTempPath(), $"issue158-facts-{Guid.NewGuid():N}.db");
        var session = Guid.CreateVersion7();
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE skill_invocation_snapshots(session_id TEXT,trigger TEXT);
                    CREATE TABLE skill_projection_sdk_claims(session_id TEXT);
                    CREATE TABLE session_events(session_id TEXT,type TEXT);
                    INSERT INTO skill_invocation_snapshots VALUES($session,'user-invoked');
                    INSERT INTO skill_projection_sdk_claims VALUES($session);
                    INSERT INTO session_events VALUES($session,'session.start'),($session,'session.task_complete');
                    """;
                command.Parameters.AddWithValue("$session", session.ToString("D"));
                command.ExecuteNonQuery();
                if (includeAgent)
                {
                    command.CommandText = "INSERT INTO skill_invocation_snapshots VALUES($session,'agent-invoked'); INSERT INTO skill_projection_sdk_claims VALUES($session);";
                    command.ExecuteNonQuery();
                }
            }
            var exact = Issue158PersistenceFactReader.Read(path, session);
            Assert.Equal(new Issue158PersistenceFacts(total, total, 1, 1, 1, includeAgent ? 1 : 0), exact);
            Assert.Equal(2, Issue158PersistenceFactVerifier.Verify(exact));
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM skill_projection_sdk_claims WHERE rowid=(SELECT MAX(rowid) FROM skill_projection_sdk_claims);";
                command.ExecuteNonQuery();
            }
            var mismatched = Issue158PersistenceFactReader.Read(path, session);
            Assert.Equal(total - 1, mismatched.SdkClaims);
            Assert.Throws<InvalidOperationException>(() => Issue158PersistenceFactVerifier.Verify(mismatched));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1, 1, 1, 1, 0)]
    [InlineData(2, 2, 1, 1, 2)]
    [InlineData(2, 2, 0, 2, 0)]
    [InlineData(2, 1, 1, 1, 1)]
    [InlineData(65, 65, 1, 64, 1)]
    public void PersistenceVerifierRejectsInvalidTriggerAndAggregateCombinations(
        long snapshots, long claims, long userInvoked, long agentInvoked, long taskComplete)
    {
        var facts = new Issue158PersistenceFacts(snapshots, claims, 1, taskComplete, userInvoked, agentInvoked);
        Assert.Throws<InvalidOperationException>(() => Issue158PersistenceFactVerifier.Verify(facts));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(63)]
    public void PersistenceVerifierAcceptsAuthoritativeAgentCardinality(long agentInvoked)
    {
        var total = 1 + agentInvoked;
        Assert.Equal(2, Issue158PersistenceFactVerifier.Verify(new(total, total, 1, 1, 1, agentInvoked)));
    }

    [Theory]
    [InlineData(1, 2, 1, 1, 1, 1)]
    [InlineData(2, 1, 1, 1, 1, 1)]
    [InlineData(2, 2, 0, 1, 1, 1)]
    [InlineData(2, 2, 2, 1, 1, 1)]
    [InlineData(2, 2, 1, 0, 1, 1)]
    [InlineData(2, 2, 1, 2, 1, 1)]
    [InlineData(2, 2, 1, 1, 0, 2)]
    [InlineData(2, 2, 1, 1, 2, 0)]
    [InlineData(3, 3, 1, 1, 1, 1)]
    [InlineData(65, 65, 1, 1, 1, 64)]
    public void PersistenceVerifierRequiresEveryIndependentDoubleTopologyFact(
        long snapshots, long claims, long start, long complete, long user, long agent) =>
        Assert.Throws<InvalidOperationException>(() => Issue158PersistenceFactVerifier.Verify(
            new Issue158PersistenceFacts(snapshots, claims, start, complete, user, agent)));

    [Fact]
    public void PersistenceReadFailureRemainsDistinctFromAggregateMismatch()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"issue158-missing-{Guid.NewGuid():N}.db");
        Assert.ThrowsAny<Exception>(() => Issue158PersistenceFactReader.Read(missing, Guid.CreateVersion7()));
        SqliteConnection.ClearAllPools();
        if (File.Exists(missing)) File.Delete(missing);
    }

    [Theory]
    [InlineData(3, "failed")]
    [InlineData(4, "canceled")]
    [InlineData(5, "timed_out")]
    public void BlockerTerminalStatusMapsEveryClosedTerminalDistinctly(int status, string expected) =>
        Assert.Equal(expected, Issue158WindowsBlocker.TerminalWire(new MonitorAnalysisRun(1, "trace", null, null, MonitorAnalysisFocus.Latency, (MonitorAnalysisStatus)status, "synthetic", null, null)));

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void BlockerTerminalStatusRejectsMissingOrNonterminal(int? status)
    {
        MonitorAnalysisRun? run = status is null ? null : new(1, "trace", null, null, MonitorAnalysisFocus.Latency, (MonitorAnalysisStatus)status.Value, "synthetic", null, null);
        Assert.Throws<InvalidOperationException>(() => Issue158WindowsBlocker.TerminalWire(run));
    }

    private static bool MatchesSyntheticSkillFixtureContract(string name, string token, string document) =>
        name == ExpectedSyntheticSkillName
        && token == ExpectedSyntheticCompletionToken
        && document == ExpectedSyntheticSkillDocument;

    [Fact]
    public async Task BodyGateRejectsBeforeFactoryConstruction()
    {
        var constructed = 0;
        var gate = Issue158WindowsLaneGate.Read(new Dictionary<string, string?>());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Issue158WindowsOwnedSessionLane.RunAfterGateAsync(() => gate, _ =>
            {
                constructed++;
                return Task.CompletedTask;
            }));
        Assert.Equal(0, constructed);
    }

    [Theory]
    [InlineData("COPILOT_CLI_PATH", "")]
    [InlineData("COPILOT_CLI_PATH", "synthetic")]
    [InlineData("copilot_cli_path", "")]
    [InlineData("CoPiLoT_Cli_PaTh", "synthetic")]
    [InlineData("CAO_ISSUE158_OPERATOR_AUTHORIZED", "wrong")]
    [InlineData("CAO_ISSUE158_CANDIDATE_SHA", "ABC")]
    [InlineData("CAO_ISSUE158_RUN_ID", "bad")]
    [InlineData("CAO_ISSUE158_RESULT_FILE", "other.json")]
    public void InvalidInputsCloseTheGate(string key, string value)
    {
        var values = ValidValues();
        values[key] = value;
        Assert.False(Issue158WindowsLaneGate.Read(values).IsAuthorized);
    }

    [Fact]
    public void ExactInputsOpenThePureGate() =>
        Assert.True(Issue158WindowsLaneGate.Read(ValidValues()).IsAuthorized);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PhysicalGateRejectsUnexpectedRootChild(bool runtime)
    {
        using var fixture = new OwnedGateFixture();
        File.WriteAllText(Path.Combine(runtime ? fixture.Runtime : fixture.Result, "unexpected"), "synthetic");
        Assert.False(fixture.Read().IsAuthorized);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PhysicalGateRejectsLeafJunction(bool runtime)
    {
        using var fixture = new OwnedGateFixture();
        fixture.ReplaceRootWithJunction(runtime);
        Assert.False(fixture.Read().IsAuthorized);
    }

    [Fact]
    public void PhysicalGateRejectsAncestorJunction()
    {
        using var fixture = new OwnedGateFixture();
        fixture.UseAncestorJunctionForRuntime();
        Assert.False(fixture.Read().IsAuthorized);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PhysicalGateRejectsLinkedOwner(bool hardLink)
    {
        using var fixture = new OwnedGateFixture();
        fixture.ReplaceRuntimeOwnerWithLink(hardLink);
        Assert.False(fixture.Read().IsAuthorized);
    }

    [Fact]
    public async Task BoundaryRereadRejectsRootReplacementBeforeConstruction()
    {
        using var fixture = new OwnedGateFixture();
        Assert.True(fixture.Read().IsAuthorized);
        File.WriteAllText(Path.Combine(fixture.Runtime, "replacement"), "synthetic");
        var constructed = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Issue158WindowsOwnedSessionLane.RunAfterGateAsync(fixture.Read, _ => { constructed++; return Task.CompletedTask; }));
        Assert.Equal(0, constructed);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(63, 64)]
    public void ResultSerializationIsStrictTruthfulAndSanitized(int agentInvoked, int total)
    {
        var json = Issue158WindowsResult.Serialize(ValidObservedResult(agentInvoked));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(9, document.RootElement.EnumerateObject().Count());
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("windows_owned_session", document.RootElement.GetProperty("lane").GetString());
        var counts = document.RootElement.GetProperty("counts");
        Assert.Equal(agentInvoked, counts.GetProperty("agent_invoked").GetInt64());
        Assert.Equal(total, counts.GetProperty("v2_imported").GetInt64());
        Assert.Equal(total, counts.GetProperty("snapshot_rows").GetInt64());
    }

    [Theory]
    [InlineData(0, "{\"schema_version\":\"issue-158-live-validation.v1\",\"candidate_sha\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"lane\":\"windows_owned_session\",\"outcome\":\"passed\",\"source_application_version\":\"1.0.75\",\"protocol_version\":3,\"counts\":{\"retained_roots\":1,\"retained_skills\":1,\"probe_sessions\":1,\"execution_sessions\":1,\"user_invoked\":1,\"agent_invoked\":0,\"task_complete\":1,\"v2_imported\":1,\"v1_imported\":2,\"snapshot_rows\":1},\"checks\":{\"operator_gate\":true,\"cli_override_absent\":true,\"retained_only_inventory\":true,\"exact_tool_union\":true,\"native_reproof\":true,\"current_generation\":true,\"metadata_route\":true,\"historical_route\":true,\"current_file_route\":true,\"shutdown_drain\":true,\"cleanup_complete\":true},\"exit_code\":0}")]
    [InlineData(1, "{\"schema_version\":\"issue-158-live-validation.v1\",\"candidate_sha\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"lane\":\"windows_owned_session\",\"outcome\":\"passed\",\"source_application_version\":\"1.0.75\",\"protocol_version\":3,\"counts\":{\"retained_roots\":1,\"retained_skills\":1,\"probe_sessions\":1,\"execution_sessions\":1,\"user_invoked\":1,\"agent_invoked\":1,\"task_complete\":1,\"v2_imported\":2,\"v1_imported\":2,\"snapshot_rows\":2},\"checks\":{\"operator_gate\":true,\"cli_override_absent\":true,\"retained_only_inventory\":true,\"exact_tool_union\":true,\"native_reproof\":true,\"current_generation\":true,\"metadata_route\":true,\"historical_route\":true,\"current_file_route\":true,\"shutdown_drain\":true,\"cleanup_complete\":true},\"exit_code\":0}")]
    public void ResultSerializationPreservesTheExactSuccessfulWireContract(int agentInvoked, string expected) =>
        Assert.Equal(expected, Issue158WindowsResult.Serialize(ValidObservedResult(agentInvoked)));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    public void ResultSerializationRejectsEachIndependentCertificationFailure(int mutation)
    {
        var result = ValidObservedResult();
        result = mutation switch
        {
            0 => result with { Evidence = result.Evidence with { PreparedInvocationCount = 1 } },
            1 => result with { V2Imported = 1 },
            2 => result with { SnapshotRows = 1 },
            3 => result with { UserInvoked = 0, Evidence = result.Evidence with { PreparedInvocationCount = 1 }, V2Imported = 1, SnapshotRows = 1 },
            4 => result with { AgentInvoked = 64, Evidence = result.Evidence with { PreparedInvocationCount = 65 }, V2Imported = 65, SnapshotRows = 65 },
            5 => result with { TaskComplete = 0 },
            6 => result with { V1Imported = 1 },
            7 => result with { OperatorGate = false },
            8 => result with { CliOverrideAbsent = false },
            9 => result with { Evidence = result.Evidence with { RetainedOnlyInventory = false } },
            10 => result with { Evidence = result.Evidence with { ExactToolUnion = false } },
            11 => result with { Evidence = result.Evidence with { ProbeNativeReproof = false } },
            12 => result with { MetadataRoute = false },
            13 => result with { HistoricalRoute = false },
            14 => result with { CurrentFileRoute = false },
            15 => result with { ShutdownDrain = false },
            16 => result with { Evidence = result.Evidence with { SameClient = false } },
            17 => result with { Evidence = result.Evidence with { ExecutionNativeReproof = false } },
            18 => result with { Evidence = result.Evidence with { CallbackNativeReproof = false } },
            19 => result with { Evidence = result.Evidence with { PreparedInvocationCount = 65 } },
            _ => throw new InvalidOperationException(),
        };
        Assert.Throws<InvalidOperationException>(() => Issue158WindowsResult.Serialize(result));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    [InlineData(3, 2)]
    [InlineData(64, 63)]
    [InlineData(64, 65)]
    [InlineData(65, 65)]
    public void CommittedIdentitiesMustMatchCertifiedPreparedCardinality(int prepared, int committedCount)
    {
        var session = Guid.CreateVersion7();
        var identities = Enumerable.Range(0, committedCount)
            .Select(_ => new SkillInvocationV2CommittedIdentityV1(session, Guid.CreateVersion7())).ToArray();
        Assert.Throws<InvalidOperationException>(() => Issue158CommittedIdentityVerifier.Verify(identities, prepared));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(64)]
    public void CommittedIdentitiesAcceptExactCertifiedPreparedCardinality(int count)
    {
        var session = Guid.CreateVersion7();
        var identities = Enumerable.Range(0, count)
            .Select(_ => new SkillInvocationV2CommittedIdentityV1(session, Guid.CreateVersion7())).ToArray();
        Assert.Equal(count, Issue158CommittedIdentityVerifier.Verify(identities, count).Length);
    }

    [Fact]
    public void CommittedIdentitiesRejectMixedSessionsAtCorrectCardinality()
    {
        var values = new[]
        {
            new SkillInvocationV2CommittedIdentityV1(Guid.CreateVersion7(), Guid.CreateVersion7()),
            new SkillInvocationV2CommittedIdentityV1(Guid.CreateVersion7(), Guid.CreateVersion7()),
        };
        Assert.Throws<InvalidOperationException>(() => Issue158CommittedIdentityVerifier.Verify(values, 2));
    }

    [Fact]
    public void CommittedIdentitiesRejectDuplicateSnapshotsAtCorrectCardinality()
    {
        var session = Guid.CreateVersion7();
        var snapshot = Guid.CreateVersion7();
        var values = new[]
        {
            new SkillInvocationV2CommittedIdentityV1(session, snapshot),
            new SkillInvocationV2CommittedIdentityV1(session, snapshot),
        };
        Assert.Throws<InvalidOperationException>(() => Issue158CommittedIdentityVerifier.Verify(values, 2));
    }

    [Fact]
    public void CommittedIdentitiesRejectHighCardinalityMixedSessionAfterValidPrefix()
    {
        var session = Guid.CreateVersion7();
        var values = Enumerable.Range(0, 64)
            .Select(_ => new SkillInvocationV2CommittedIdentityV1(session, Guid.CreateVersion7())).ToArray();
        values[^1] = values[^1] with { SessionId = Guid.CreateVersion7() };
        Assert.Throws<InvalidOperationException>(() => Issue158CommittedIdentityVerifier.Verify(values, 64));
    }

    [Fact]
    public void CommittedIdentitiesRejectHighCardinalityDuplicateSnapshotAfterValidPrefix()
    {
        var session = Guid.CreateVersion7();
        var values = Enumerable.Range(0, 64)
            .Select(_ => new SkillInvocationV2CommittedIdentityV1(session, Guid.CreateVersion7())).ToArray();
        values[^1] = values[^1] with { SnapshotId = values[^2].SnapshotId };
        Assert.Throws<InvalidOperationException>(() => Issue158CommittedIdentityVerifier.Verify(values, 64));
    }

    [Fact]
    public void CommittedIdentitiesRejectHighCardinalityReplayTruncation()
    {
        var session = Guid.CreateVersion7();
        var values = Enumerable.Range(0, 63)
            .Select(_ => new SkillInvocationV2CommittedIdentityV1(session, Guid.CreateVersion7())).ToArray();
        Assert.Throws<InvalidOperationException>(() => Issue158CommittedIdentityVerifier.Verify(values, 64));
    }

    [Fact]
    public void ResultSerializationRejectsAnUncertifiedSourceVersion()
    {
        var result = ValidObservedResult() with
        {
            Evidence = ValidEvidence() with { SourceApplicationVersion = "1.0.65" },
        };
        Assert.Throws<InvalidOperationException>(() => Issue158WindowsResult.Serialize(result));
    }

    [Fact]
    public void PreparedResultCarriesPendingCleanup()
    {
        var json = Issue158WindowsResult.Serialize(ValidObservedResult() with { CleanupComplete = false });
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("checks").GetProperty("cleanup_complete").GetBoolean());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PostCommitObserverIgnoresEveryNonCommitOutcome(int outcomeValue)
    {
        var calls = 0;
        SkillInvocationV2CommittedObservationV1.Notify(
            new SkillInvocationV2IngestResultV1((SkillInvocationV2IngestOutcomeV1)outcomeValue, false),
            _ => calls++);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void PostCommitObserverReportsEachTrueCommitExactlyOnce()
    {
        var session = Guid.CreateVersion7();
        var observed = new List<SkillInvocationV2CommittedIdentityV1>();
        foreach (var snapshot in new[] { Guid.CreateVersion7(), Guid.CreateVersion7() })
            SkillInvocationV2CommittedObservationV1.Notify(
                new SkillInvocationV2IngestResultV1(
                    SkillInvocationV2IngestOutcomeV1.Committed,
                    true,
                    new(session, snapshot)),
                observed.Add);
        Assert.Equal(2, observed.Count);
        Assert.Single(observed.Select(static item => item.SessionId).Distinct());
        Assert.Equal(2, observed.Select(static item => item.SnapshotId).Distinct().Count());
    }

    [Fact]
    public void PostCommitObserverRequiresCommittedIdentityAndAllowsNullObserver()
    {
        var calls = 0;
        SkillInvocationV2CommittedObservationV1.Notify(
            new SkillInvocationV2IngestResultV1(SkillInvocationV2IngestOutcomeV1.Committed, true),
            _ => calls++);
        SkillInvocationV2CommittedObservationV1.Notify(
            new SkillInvocationV2IngestResultV1(
                SkillInvocationV2IngestOutcomeV1.Committed,
                true,
                new(Guid.CreateVersion7(), Guid.CreateVersion7())),
            null);
        Assert.Equal(0, calls);
    }

    private static Dictionary<string, string?> ValidValues() => new(StringComparer.Ordinal)
    {
        ["CAO_ISSUE158_OPERATOR_AUTHORIZED"] = Issue158WindowsLaneGate.Authorization,
        ["CAO_ISSUE158_CANDIDATE_SHA"] = new string('a', 40),
        ["CAO_ISSUE158_RUN_ID"] = new string('b', 32),
        ["CAO_ISSUE158_RUNTIME_DIRECTORY"] = Path.Combine(Path.GetTempPath(), "runtime"),
        ["CAO_ISSUE158_RESULT_DIRECTORY"] = Path.Combine(Path.GetTempPath(), "result"),
        ["CAO_ISSUE158_RESULT_FILE"] = "result.json",
        ["CAO_ISSUE158_RUNTIME_MARKER"] = "synthetic-marker",
    };

    private static OwnedSessionExecutionEvidenceV1 ValidEvidence() => new(
        "1.0.75", 3, 1, 1, 1, 1, 1, 1, 1, 1, 2,
        true, true, true, true, true, true);

    private static Issue158WindowsObservedResult ValidObservedResult(int agentInvoked = 1)
    {
        var total = 1 + agentInvoked;
        return new(
        new string('a', 40), ValidEvidence() with { PreparedInvocationCount = total }, 1, agentInvoked, 1, total, 2, total,
        true, true, true, true, true, true, true);
    }

    private sealed class OwnedGateFixture : IDisposable
    {
        private readonly List<string> links = [];
        private readonly List<string> extras = [];
        internal string RunId { get; } = Guid.NewGuid().ToString("N");
        internal string Candidate { get; } = new string('a', 40);
        internal string Runtime { get; private set; }
        internal string Result { get; }
        internal Dictionary<string, string?> Values { get; }

        internal OwnedGateFixture()
        {
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            Runtime = Path.Combine(temp, $"cao-issue158-runtime-{RunId}");
            Result = Path.Combine(temp, $"cao-issue158-result-{RunId}");
            Directory.CreateDirectory(Runtime);
            Directory.CreateDirectory(Result);
            WriteOwners();
            Values = ValidValues();
            Values["CAO_ISSUE158_CANDIDATE_SHA"] = Candidate;
            Values["CAO_ISSUE158_RUN_ID"] = RunId;
            Values["CAO_ISSUE158_RUNTIME_DIRECTORY"] = Runtime;
            Values["CAO_ISSUE158_RESULT_DIRECTORY"] = Result;
        }

        internal Issue158WindowsLaneGate Read() => Issue158WindowsLaneGate.Read(Values, verifyOwnedRoots: true);

        internal void ReplaceRootWithJunction(bool runtime)
        {
            var path = runtime ? Runtime : Result;
            var target = path + "-target";
            Directory.Move(path, target);
            Directory.CreateSymbolicLink(path, target);
            links.Add(path);
            extras.Add(target);
        }

        internal void UseAncestorJunctionForRuntime()
        {
            var ancestor = Path.Combine(Path.GetTempPath(), $"cao-issue158-ancestor-{RunId}");
            var target = ancestor + "-target";
            Directory.CreateDirectory(target);
            var nested = Path.Combine(target, Path.GetFileName(Runtime));
            Directory.Move(Runtime, nested);
            Directory.CreateSymbolicLink(ancestor, target);
            Runtime = Path.Combine(ancestor, Path.GetFileName(nested));
            Values["CAO_ISSUE158_RUNTIME_DIRECTORY"] = Runtime;
            links.Add(ancestor);
            extras.Add(target);
        }

        internal void ReplaceRuntimeOwnerWithLink(bool hardLink)
        {
            var owner = Path.Combine(Runtime, ".cao-issue-158-owner.json");
            var target = Path.Combine(Path.GetTempPath(), $"cao-issue158-owner-target-{RunId}.json");
            File.Move(owner, target);
            if (hardLink)
            {
                if (!CreateHardLink(owner, target, IntPtr.Zero)) throw new InvalidOperationException("fixture");
            }
            else File.CreateSymbolicLink(owner, target);
            extras.Add(target);
        }

        private void WriteOwners()
        {
            static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
            foreach (var pair in new[] { (Runtime, "runtime"), (Result, "result") })
                File.WriteAllText(Path.Combine(pair.Item1, ".cao-issue-158-owner.json"), JsonSerializer.Serialize(new
                {
                    schema_version = "issue-158-validation-owner.v1", run_id = RunId, candidate_sha = Candidate, kind = pair.Item2,
                    runtime_path_sha256 = Hash(Runtime), result_path_sha256 = Hash(Result),
                }));
        }

        public void Dispose()
        {
            foreach (var link in links.OrderByDescending(static path => path.Length)) if (Directory.Exists(link)) Directory.Delete(link);
            foreach (var path in new[] { Runtime, Result }.Concat(extras).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(static path => path.Length))
            {
                if (File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
    }
}

public sealed class WindowsOwnedSessionLiveFactAttribute : FactAttribute
{
    public WindowsOwnedSessionLiveFactAttribute()
    {
        if (!Issue158WindowsLaneGate.ReadProcess().IsAuthorized)
        {
            Skip = "operator_gate_required";
        }
    }
}

internal sealed record Issue158WindowsLaneGate(
    bool IsAuthorized,
    string Code,
    string CandidateSha,
    string RunId,
    string RuntimeDirectory,
    string ResultDirectory,
    string ResultFile,
    string RuntimeMarker)
{
    internal const string Authorization = "issue-158-windows-owned-session-v1";

    internal static Issue158WindowsLaneGate ReadProcess()
    {
        var values = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value?.ToString(), StringComparer.Ordinal);
        return Read(values, verifyOwnedRoots: true);
    }

    internal static Issue158WindowsLaneGate Read(
        IReadOnlyDictionary<string, string?> values,
        bool verifyOwnedRoots = false)
    {
        static string Get(IReadOnlyDictionary<string, string?> source, string key) =>
            source.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;
        var candidate = Get(values, "CAO_ISSUE158_CANDIDATE_SHA");
        var runId = Get(values, "CAO_ISSUE158_RUN_ID");
        var runtime = Get(values, "CAO_ISSUE158_RUNTIME_DIRECTORY");
        var result = Get(values, "CAO_ISSUE158_RESULT_DIRECTORY");
        var file = Get(values, "CAO_ISSUE158_RESULT_FILE");
        var marker = Get(values, "CAO_ISSUE158_RUNTIME_MARKER");
        var valid = OperatingSystem.IsWindows()
            && Get(values, "CAO_ISSUE158_OPERATOR_AUTHORIZED") == Authorization
            && !values.Keys.Any(static key => string.Equals(key, "COPILOT_CLI_PATH", StringComparison.OrdinalIgnoreCase))
            && candidate.Length == 40 && candidate.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            && runId.Length == 32 && runId.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            && Path.IsPathFullyQualified(runtime) && Path.IsPathFullyQualified(result)
            && file == "result.json" && !string.IsNullOrWhiteSpace(marker)
            && (!verifyOwnedRoots || VerifyOwnedRoots(runtime, result, runId, candidate));
        return new(valid, valid ? "passed" : "operator_gate", candidate, runId, runtime, result, file, marker);
    }

    private static bool VerifyOwnedRoots(string runtime, string result, string runId, string candidate)
    {
        try
        {
            var temp = ResolveNoReparseDirectory(Path.GetTempPath());
            var runtimeLexical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtime));
            var resultLexical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(result));
            runtime = ResolveNoReparseDirectory(runtimeLexical);
            result = ResolveNoReparseDirectory(resultLexical);
            if (!string.Equals(runtimeLexical, runtime, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(resultLexical, result, StringComparison.OrdinalIgnoreCase)
                || string.Equals(runtime, result, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetDirectoryName(runtime), temp, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetDirectoryName(result), temp, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(runtime) != $"cao-issue158-runtime-{runId}"
                || Path.GetFileName(result) != $"cao-issue158-result-{runId}") return false;
            if (!HasOnlyOwner(runtime) || !HasOnlyOwner(result)) return false;
            return VerifyOwner(runtime, "runtime", runId, candidate, runtime, result)
                && VerifyOwner(result, "result", runId, candidate, runtime, result);
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveNoReparseDirectory(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full) ?? throw new InvalidOperationException("operator_gate");
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists || (rootInfo.Attributes & FileAttributes.ReparsePoint) != 0 || rootInfo.LinkTarget is not null)
            throw new InvalidOperationException("operator_gate");
        var current = root;
        foreach (var component in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]))
        {
            if (component.Length == 0) continue;
            current = Path.Combine(current, component);
            var item = new DirectoryInfo(current);
            if (!item.Exists || (item.Attributes & FileAttributes.ReparsePoint) != 0 || item.LinkTarget is not null)
                throw new InvalidOperationException("operator_gate");
            current = Path.TrimEndingDirectorySeparator(item.FullName);
        }
        return current;
    }

    private static bool HasOnlyOwner(string directory)
    {
        var entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
        return entries.Length == 1
            && string.Equals(Path.GetFileName(entries[0]), ".cao-issue-158-owner.json", StringComparison.Ordinal);
    }

    private static bool VerifyOwner(
        string directory, string kind, string runId, string candidate, string runtime, string result)
    {
        var ownerPath = Path.Combine(directory, ".cao-issue-158-owner.json");
        var attributes = File.GetAttributes(ownerPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
        var owner = new FileInfo(ownerPath);
        if (!owner.Exists || owner.LinkTarget is not null
            || !string.Equals(owner.FullName, Path.GetFullPath(ownerPath), StringComparison.OrdinalIgnoreCase)) return false;
        using (var handle = File.OpenHandle(ownerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (!GetFileInformationByHandle(handle, out var information) || information.NumberOfLinks != 1) return false;
        }
        using var document = JsonDocument.Parse(File.ReadAllBytes(ownerPath));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 6) return false;
        static string Hash(string value) => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return root.GetProperty("schema_version").GetString() == "issue-158-validation-owner.v1"
            && root.GetProperty("run_id").GetString() == runId
            && root.GetProperty("candidate_sha").GetString() == candidate
            && root.GetProperty("kind").GetString() == kind
            && root.GetProperty("runtime_path_sha256").GetString() == Hash(runtime)
            && root.GetProperty("result_path_sha256").GetString() == Hash(result);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }
}

internal static class Issue158WindowsOwnedSessionLane
{
    internal const string SyntheticSkillName = "issue158-synthetic";
    internal const string SyntheticCompletionToken = "ISSUE158_RETAINED_ROOT_OK";
    internal const string SyntheticSkillDocument = "---\nname: " + SyntheticSkillName
        + "\ndescription: Return the exact token " + SyntheticCompletionToken
        + " when invoked.\nuser-invocable: true\n---\n\nReturn exactly " + SyntheticCompletionToken
        + ", then call task_complete as the final action with successful synthetic completion.\n";

    internal static Task RunAfterGateAsync(Func<Issue158WindowsLaneGate> readCurrentGate, Func<Issue158WindowsLaneGate, Task> factory)
    {
        var gate = readCurrentGate();
        if (!gate.IsAuthorized)
        {
            throw new InvalidOperationException("operator_gate");
        }
        return factory(gate);
    }

    internal static Task RunAsync() => RunAfterGateAsync(Issue158WindowsLaneGate.ReadProcess, ExecuteAsync);

    private static async Task ExecuteAsync(Issue158WindowsLaneGate gate)
    {
        const string traceId = "issue158-synthetic-trace";
        var databasePath = Path.Combine(gate.RuntimeDirectory, "monitor.db");
        var analysisDirectory = Path.Combine(gate.RuntimeDirectory, "analysis");
        var skillRoot = Path.Combine(gate.RuntimeDirectory, "skills");
        var skillDirectory = Path.Combine(skillRoot, SyntheticSkillName);
        Directory.CreateDirectory(analysisDirectory);
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), SyntheticSkillDocument);

        var timeProvider = TimeProvider.System;
        var retention = RetentionCatalogContext.InitializeNewOwnedDatabase(databasePath, timeProvider);
        var rawStore = new RawTelemetryStore(databasePath, retention, timeProvider, RawTelemetryStoreConnectionOptions.MonitorWriter);
        rawStore.CreateMonitorSchema();
        var record = new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, traceId, timeProvider.GetUtcNow(), null, "{}");
        var rawRecordId = rawStore.Insert(record);
        rawStore.ApplyProjection(rawRecordId, record.Source, record.ReceivedAt,
            new MonitorRecordProjection(traceId, "synthetic", 0,
                [new MonitorTraceContribution(traceId, "synthetic", null, null, null, null, null, 0, 0, 0, null, null, null)]),
            timeProvider.GetUtcNow());
        var identities = new List<SkillInvocationV2CommittedIdentityV1>();
        var identityLock = new object();
        var publicationState = new Issue158PublicationEvidenceState(identityLock);
        OwnedSessionDiagnosticEventV1? driverPhase = null;
        OwnedSessionDiagnosticEventV1? poisonReason = null;
        OwnedSessionPostFreezeOutcomeV1? postFreezeFailure = null;
        var options = new MonitorOptions(databasePath, "http://127.0.0.1:0", false,
            MonitorOptions.DefaultMaxRequestBodyBytes, SkillDiscoveryDirectories: [skillRoot]);
        var app = MonitorHost.Build(options, new MonitorHostTestOptions
        {
            TimeProvider = timeProvider,
            UseUserSecrets = false,
            ConfigurationValues = new Dictionary<string, string?>
            {
                ["CopilotAnalysis:Enabled"] = "true",
                ["CopilotAnalysis:BaseDirectory"] = analysisDirectory,
            },
            OwnedSessionExecutionDriver = new ExactSkillCommandExecutionDriverV1(SyntheticSkillName),
            SkillInvocationV2CommittedObserver = identity => { lock (identityLock) identities.Add(identity); },
            OwnedSessionExecutionEvidenceObserver = publicationState.ObserveEvidence,
            OwnedSessionExecutionCheckpointObserver = publicationState.ObserveCheckpoint,
            OwnedSessionPostFreezeFailureObserver = outcome =>
                Issue158PostFreezeFailureCapture.Capture(identityLock, ref postFreezeFailure, outcome),
            OwnedSessionDiagnosticObserver = diagnostic =>
            {
                lock (identityLock)
                {
                    if (diagnostic is OwnedSessionDiagnosticEventV1.CommandPending or OwnedSessionDiagnosticEventV1.SendPending)
                        driverPhase = diagnostic;
                    else poisonReason ??= diagnostic;
                }
            },
        });
        var analysisStore = app.Services.GetRequiredService<IMonitorAnalysisStore>();
        await app.StartAsync();
        Issue158WindowsObservedResult? observed = null;
        var shutdownComplete = false;
        var analysisSucceeded = false;
        var analysisPersistedSucceeded = false;
        var postSuccess = new Issue158PostSuccessFailureCapture();
        try
        {
          await Issue158PostSuccessExecution.RunWithShutdownAsync(async () =>
          {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/traces/{traceId}/analysis")
            {
                Content = JsonContent.Create(new { focus = "latency" }),
            };
            request.Headers.TryAddWithoutValidation("x-monitor-csrf", "local-monitor");
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var start = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            var runId = start.RootElement.GetProperty("run_id").GetInt64();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            MonitorAnalysisRun? run;
            try
            {
                do
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
                    run = analysisStore.GetRun(runId);
                } while (run?.Status is MonitorAnalysisStatus.Queued or MonitorAnalysisStatus.Running);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                run = analysisStore.GetRun(runId);
                OwnedSessionExecutionCheckpointV1? snapshot;
                OwnedSessionDiagnosticEventV1? phaseSnapshot;
                OwnedSessionDiagnosticEventV1? reasonSnapshot;
                OwnedSessionPostFreezeOutcomeV1? postFreezeSnapshot;
                lock (identityLock) { snapshot = publicationState.LastCheckpoint; phaseSnapshot = driverPhase; reasonSnapshot = poisonReason; postFreezeSnapshot = postFreezeFailure; }
                await Issue158WindowsBlocker.WriteAsync(gate, Issue158WindowsBlocker.TerminalWire(run), snapshot, phaseSnapshot, reasonSnapshot, postFreezeSnapshot);
                throw;
            }
            if (run?.Status != MonitorAnalysisStatus.Succeeded)
            {
                OwnedSessionExecutionCheckpointV1? snapshot;
                OwnedSessionDiagnosticEventV1? phaseSnapshot;
                OwnedSessionDiagnosticEventV1? reasonSnapshot;
                OwnedSessionPostFreezeOutcomeV1? postFreezeSnapshot;
                lock (identityLock) { snapshot = publicationState.LastCheckpoint; phaseSnapshot = driverPhase; reasonSnapshot = poisonReason; postFreezeSnapshot = postFreezeFailure; }
                await Issue158WindowsBlocker.WriteAsync(gate, Issue158WindowsBlocker.TerminalWire(run), snapshot, phaseSnapshot, reasonSnapshot, postFreezeSnapshot);
                throw new InvalidOperationException("analysis_terminal");
            }
            analysisPersistedSucceeded = true;
            OwnedSessionExecutionEvidenceV1 certified;
            using (var publicationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                certified = await publicationState.WaitAndCertifyAsync(postSuccess, publicationTimeout.Token);
            analysisSucceeded = true;

            var committed = Stage(Issue158PostSuccessFailureV1.CommittedIdentitySequence, () =>
            {
                SkillInvocationV2CommittedIdentityV1[] values;
                lock (identityLock) values = [.. identities];
                return Issue158CommittedIdentityVerifier.Verify(values, certified.PreparedInvocationCount);
            });
            var identity = committed[0];
            var routeBase = $"/api/local-monitor/v1/sessions/{identity.SessionId:D}/skill-invocations/{identity.SnapshotId:D}";
            using var metadata = await StageAsync(Issue158PostSuccessFailureV1.MetadataRequest,
                () => client.GetAsync(routeBase, timeout.Token));
            using var historical = await StageAsync(Issue158PostSuccessFailureV1.HistoricalRequest,
                () => client.GetAsync(routeBase + "/content", timeout.Token));
            using var current = await Issue158PostSuccessExecution.RunStageAsync(
                Issue158PostSuccessFailureV1.CurrentFileRequest, postSuccess, async () =>
                {
                    using var currentRequest = new HttpRequestMessage(HttpMethod.Post, routeBase + "/current-file-read")
                    {
                        Content = JsonContent.Create(new { schema_version = "local-skill-current-file-read.request.v1" }),
                    };
                    currentRequest.Headers.TryAddWithoutValidation("x-monitor-csrf", "local-monitor");
                    return await client.SendAsync(currentRequest, timeout.Token);
                });
            Stage(Issue158PostSuccessFailureV1.RouteMatrix, () =>
            {
                if (!metadata.IsSuccessStatusCode || !historical.IsSuccessStatusCode || !current.IsSuccessStatusCode)
                    throw new InvalidOperationException("route_matrix");
                return true;
            });
            await StageTaskAsync(Issue158PostSuccessFailureV1.MetadataDocument, async () =>
            {
                try { await Issue158RouteDocuments.ValidateMetadataAsync(metadata.Content, identity, certified.SourceApplicationVersion, timeout.Token); }
                finally { metadata.Dispose(); }
            });
            await StageTaskAsync(Issue158PostSuccessFailureV1.HistoricalDocument, async () =>
            {
                try { await Issue158RouteDocuments.ValidateHistoricalAsync(historical.Content, identity.SnapshotId, SyntheticSkillDocument, timeout.Token); }
                finally { historical.Dispose(); }
            });
            await StageTaskAsync(Issue158PostSuccessFailureV1.CurrentFileDocument, async () =>
            {
                try { await Issue158RouteDocuments.ValidateCurrentAsync(current.Content, identity.SnapshotId, SyntheticSkillDocument, timeout.Token); }
                finally { current.Dispose(); }
            });
            var facts = Stage(Issue158PostSuccessFailureV1.PersistenceCounts,
                () => Issue158PersistenceFactReader.Read(databasePath, identity.SessionId));
            var v1Imported = Stage(Issue158PostSuccessFailureV1.AggregateCounts,
                () => Issue158PersistenceFactVerifier.Verify(facts));
            observed = Stage(Issue158PostSuccessFailureV1.ObservedResult, () =>
                new Issue158WindowsObservedResult(gate.CandidateSha, certified, facts.UserInvoked, facts.AgentInvoked,
                    facts.TaskComplete, committed.LongLength, v1Imported, facts.SnapshotRows,
                    gate.IsAuthorized, true, true, true, true, true, false));
          }, async () =>
          {
              await app.StopAsync();
              await app.DisposeAsync();
              shutdownComplete = true;
          }, () => analysisSucceeded, postSuccess);
          observed = Stage(Issue158PostSuccessFailureV1.ObservedResult,
              () => observed! with { ShutdownDrain = shutdownComplete });
          var resultPath = Path.Combine(gate.ResultDirectory, gate.ResultFile);
          var resultText = Stage(Issue158PostSuccessFailureV1.ResultSerialization,
              () => Issue158WindowsResult.Serialize(observed));
          await StageTaskAsync(Issue158PostSuccessFailureV1.ResultWrite,
              () => File.WriteAllTextAsync(resultPath, resultText, new UTF8Encoding(false)));
        }
        catch
        {
            if (analysisPersistedSucceeded)
            {
                if (postSuccess.Value == Issue158PostSuccessFailureV1.None)
                    postSuccess.Capture(Issue158PostSuccessFailureV1.Unexpected);
                OwnedSessionExecutionCheckpointV1? snapshot;
                snapshot = publicationState.LastCheckpoint;
                await Issue158WindowsBlocker.WriteAsync(gate, "succeeded", snapshot,
                    null, null, null, postSuccess.Value, publicationState.EvidenceFailure);
            }
            throw;
        }

        T Stage<T>(Issue158PostSuccessFailureV1 stage, Func<T> action)
        {
            try { return action(); }
            catch { postSuccess.Capture(stage); throw; }
        }

        async Task<T> StageAsync<T>(Issue158PostSuccessFailureV1 stage, Func<Task<T>> action)
        {
            try { return await action(); }
            catch { postSuccess.Capture(stage); throw; }
        }

        async Task StageTaskAsync(Issue158PostSuccessFailureV1 stage, Func<Task> action)
        {
            try { await action(); }
            catch { postSuccess.Capture(stage); throw; }
        }
    }
}

internal static class Issue158PostFreezeFailureCapture
{
    internal static void Capture(object sync, ref OwnedSessionPostFreezeOutcomeV1? captured,
        OwnedSessionPostFreezeOutcomeV1 outcome)
    {
        lock (sync) captured ??= outcome;
    }
}

internal enum Issue158PostSuccessFailureV1
{
    None,
    PublicationBarrier,
    ExecutionEvidence,
    CommittedIdentitySequence,
    MetadataRequest,
    HistoricalRequest,
    CurrentFileRequest,
    RouteMatrix,
    MetadataDocument,
    HistoricalDocument,
    CurrentFileDocument,
    PersistenceCounts,
    AggregateCounts,
    ObservedResult,
    ShutdownDrain,
    ResultSerialization,
    ResultWrite,
    Unexpected,
}

internal enum Issue158ExecutionEvidenceFailureV1
{
    None, ObserverMissing, ObserverMultiple, SourceApplicationVersion, ProtocolVersion, ClientStartCount,
    StatusObservationCount, ProbeSessionCount, ExecutionSessionCount, RetainedRootCount, RetainedSkillCount,
    ProbeInventoryCount, ExecutionInventoryCount, PreparedInvocationNone, PreparedInvocationExcess, SameClient, ExactToolUnion,
    RetainedOnlyInventory, ProbeNativeReproof, ExecutionNativeReproof, CallbackNativeReproof,
}

internal sealed class Issue158PublicationEvidenceState
{
    private readonly object sync;
    private readonly TaskCompletionSource<bool> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<OwnedSessionExecutionEvidenceV1> evidence = [];
    private OwnedSessionExecutionCheckpointV1? lastCheckpoint;
    private Issue158ExecutionEvidenceFailureV1 evidenceFailure;

    internal Issue158PublicationEvidenceState(object sync) => this.sync = sync;
    internal OwnedSessionExecutionCheckpointV1? LastCheckpoint { get { lock (sync) return lastCheckpoint; } }
    internal Issue158ExecutionEvidenceFailureV1 EvidenceFailure { get { lock (sync) return evidenceFailure; } }

    internal void ObserveEvidence(OwnedSessionExecutionEvidenceV1 item) { lock (sync) evidence.Add(item); }
    internal void Observe(OwnedSessionExecutionCheckpointV1 checkpoint)
    {
        lock (sync) lastCheckpoint = checkpoint;
        if (checkpoint == OwnedSessionExecutionCheckpointV1.CandidatePublished) source.TrySetResult(true);
    }

    internal void ObserveCheckpoint(OwnedSessionExecutionCheckpointV1 checkpoint) => Observe(checkpoint);

    internal async Task<OwnedSessionExecutionEvidenceV1> WaitAndCertifyAsync(
        Issue158PostSuccessFailureCapture capture, CancellationToken cancellationToken)
    {
        await Issue158PostSuccessExecution.RunStageAsync(Issue158PostSuccessFailureV1.PublicationBarrier, capture,
            async () => { await source.Task.WaitAsync(cancellationToken); return true; });
        return await Issue158PostSuccessExecution.RunStageAsync(Issue158PostSuccessFailureV1.ExecutionEvidence, capture, () =>
        {
            OwnedSessionExecutionEvidenceV1[] snapshot;
            lock (sync)
            {
                snapshot = [.. evidence];
                evidenceFailure = Issue158ExecutionEvidenceClassifier.Classify(snapshot);
            }
            if (evidenceFailure != Issue158ExecutionEvidenceFailureV1.None)
                throw new InvalidOperationException("execution_evidence");
            return Task.FromResult(snapshot[0]);
        });
    }
}

internal static class Issue158ExecutionEvidenceClassifier
{
    internal static Issue158ExecutionEvidenceFailureV1 Classify(IReadOnlyList<OwnedSessionExecutionEvidenceV1> values)
    {
        if (values.Count == 0) return Issue158ExecutionEvidenceFailureV1.ObserverMissing;
        if (values.Count != 1) return Issue158ExecutionEvidenceFailureV1.ObserverMultiple;
        var value = values[0];
        if (value.SourceApplicationVersion != "1.0.75") return Issue158ExecutionEvidenceFailureV1.SourceApplicationVersion;
        if (value.ProtocolVersion != 3) return Issue158ExecutionEvidenceFailureV1.ProtocolVersion;
        if (value.ClientStartCount != 1) return Issue158ExecutionEvidenceFailureV1.ClientStartCount;
        if (value.StatusObservationCount != 1) return Issue158ExecutionEvidenceFailureV1.StatusObservationCount;
        if (value.ProbeSessionCount != 1) return Issue158ExecutionEvidenceFailureV1.ProbeSessionCount;
        if (value.ExecutionSessionCount != 1) return Issue158ExecutionEvidenceFailureV1.ExecutionSessionCount;
        if (value.RetainedRootCount != 1) return Issue158ExecutionEvidenceFailureV1.RetainedRootCount;
        if (value.RetainedSkillCount != 1) return Issue158ExecutionEvidenceFailureV1.RetainedSkillCount;
        if (value.ProbeInventoryCount < value.RetainedSkillCount) return Issue158ExecutionEvidenceFailureV1.ProbeInventoryCount;
        if (value.ExecutionInventoryCount < value.RetainedSkillCount || value.ExecutionInventoryCount > value.ProbeInventoryCount) return Issue158ExecutionEvidenceFailureV1.ExecutionInventoryCount;
        if (value.PreparedInvocationCount < 0) throw new InvalidOperationException("execution_evidence");
        if (value.PreparedInvocationCount == 0) return Issue158ExecutionEvidenceFailureV1.PreparedInvocationNone;
        if (value.PreparedInvocationCount > 64) return Issue158ExecutionEvidenceFailureV1.PreparedInvocationExcess;
        if (!value.SameClient) return Issue158ExecutionEvidenceFailureV1.SameClient;
        if (!value.ExactToolUnion) return Issue158ExecutionEvidenceFailureV1.ExactToolUnion;
        if (!value.RetainedOnlyInventory) return Issue158ExecutionEvidenceFailureV1.RetainedOnlyInventory;
        if (!value.ProbeNativeReproof) return Issue158ExecutionEvidenceFailureV1.ProbeNativeReproof;
        if (!value.ExecutionNativeReproof) return Issue158ExecutionEvidenceFailureV1.ExecutionNativeReproof;
        if (!value.CallbackNativeReproof) return Issue158ExecutionEvidenceFailureV1.CallbackNativeReproof;
        return Issue158ExecutionEvidenceFailureV1.None;
    }
}

internal sealed class Issue158PostSuccessFailureCapture
{
    private readonly object sync = new();
    private Issue158PostSuccessFailureV1 value;

    internal Issue158PostSuccessFailureV1 Value { get { lock (sync) return value; } }

    internal void Capture(Issue158PostSuccessFailureV1 stage)
    {
        if (stage == Issue158PostSuccessFailureV1.None) throw new InvalidOperationException("post_success");
        lock (sync)
        {
            if (value == Issue158PostSuccessFailureV1.None) value = stage;
        }
    }
}

internal static class Issue158PostSuccessExecution
{
    internal static async Task<T> RunStageAsync<T>(Issue158PostSuccessFailureV1 stage,
        Issue158PostSuccessFailureCapture capture, Func<Task<T>> action)
    {
        try { return await action(); }
        catch { capture.Capture(stage); throw; }
    }

    internal static async Task RunStageAsync(Issue158PostSuccessFailureV1 stage,
        Issue158PostSuccessFailureCapture capture, Func<Task> action)
    {
        try { await action(); }
        catch { capture.Capture(stage); throw; }
    }

    internal static async Task RunWithShutdownAsync(Func<Task> body, Func<Task> shutdown, Func<bool> isPostSuccess,
        Issue158PostSuccessFailureCapture capture)
    {
        try
        {
            try { await body(); }
            catch
            {
                if (isPostSuccess()) capture.Capture(Issue158PostSuccessFailureV1.Unexpected);
                throw;
            }
        }
        finally
        {
            try { await shutdown(); }
            catch
            {
                if (isPostSuccess()) capture.Capture(Issue158PostSuccessFailureV1.ShutdownDrain);
                throw;
            }
        }
    }
}

internal sealed record Issue158PersistenceFacts(
    long SnapshotRows, long SdkClaims, long SessionStart, long TaskComplete, long UserInvoked, long AgentInvoked);

internal static class Issue158PersistenceFactReader
{
    internal static Issue158PersistenceFacts Read(string databasePath, Guid sessionId)
    {
        using var connection = RetentionCatalogConnectionPolicy.OpenOrdinary(databasePath, SqliteOpenMode.ReadOnly);
        var session = sessionId.ToString("D");
        var facts = new Issue158PersistenceFacts(
            Count(connection, "SELECT COUNT(*) FROM skill_invocation_snapshots WHERE session_id=$session", session),
            Count(connection, "SELECT COUNT(*) FROM skill_projection_sdk_claims WHERE session_id=$session", session),
            Count(connection, "SELECT COUNT(*) FROM session_events WHERE session_id=$session AND type='session.start'", session),
            Count(connection, "SELECT COUNT(*) FROM session_events WHERE session_id=$session AND type='session.task_complete'", session),
            Count(connection, "SELECT COUNT(*) FROM skill_invocation_snapshots WHERE session_id=$session AND trigger='user-invoked'", session),
            Count(connection, "SELECT COUNT(*) FROM skill_invocation_snapshots WHERE session_id=$session AND trigger='agent-invoked'", session));
        return facts;
    }

    private static long Count(SqliteConnection connection, string sql, string session)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$session", session);
        return (long)command.ExecuteScalar()!;
    }
}

internal static class Issue158PersistenceFactVerifier
{
    internal static long Verify(Issue158PersistenceFacts facts)
    {
        var v1Imported = checked(facts.SessionStart + facts.TaskComplete);
        var invocationCount = checked(facts.UserInvoked + facts.AgentInvoked);
        if (facts is not { SessionStart: 1, TaskComplete: 1, UserInvoked: 1, AgentInvoked: >= 0 and <= 63 }
            || facts.SnapshotRows != invocationCount || facts.SdkClaims != invocationCount || v1Imported != 2)
            throw new InvalidOperationException("aggregate_counts");
        return v1Imported;
    }
}

internal static class Issue158CommittedIdentityVerifier
{
    internal static SkillInvocationV2CommittedIdentityV1[] Verify(
        SkillInvocationV2CommittedIdentityV1[] values, int preparedInvocationCount)
    {
        if (preparedInvocationCount is < 1 or > 64 || values.Length != preparedInvocationCount
            || values.Select(static item => item.SessionId).Distinct().Count() != 1
            || values.Select(static item => item.SnapshotId).Distinct().Count() != values.Length)
            throw new InvalidOperationException("committed_identity_sequence");
        return values;
    }
}

internal static class Issue158WindowsBlocker
{
    internal const string FileName = "blocker.json";

    internal static string CheckpointWire(OwnedSessionExecutionCheckpointV1 checkpoint) => checkpoint switch
    {
        OwnedSessionExecutionCheckpointV1.ClientStarted => "client_started",
        OwnedSessionExecutionCheckpointV1.IdentityCertified => "identity_certified",
        OwnedSessionExecutionCheckpointV1.CandidateCreated => "candidate_created",
        OwnedSessionExecutionCheckpointV1.ProbeCertified => "probe_certified",
        OwnedSessionExecutionCheckpointV1.ExecutionInventoryCertified => "execution_inventory_certified",
        OwnedSessionExecutionCheckpointV1.DriverCompleted => "driver_completed",
        OwnedSessionExecutionCheckpointV1.CallbacksFrozen => "callbacks_frozen",
        OwnedSessionExecutionCheckpointV1.ImportCompleted => "import_completed",
        OwnedSessionExecutionCheckpointV1.CandidateReady => "candidate_ready",
        OwnedSessionExecutionCheckpointV1.CandidatePublished => "candidate_published",
        _ => throw new InvalidOperationException("checkpoint"),
    };

    internal static string TerminalWire(MonitorAnalysisRun? run) => run?.Status switch
    {
        MonitorAnalysisStatus.Failed => "failed",
        MonitorAnalysisStatus.Canceled => "canceled",
        MonitorAnalysisStatus.TimedOut => "timed_out",
        _ => throw new InvalidOperationException("analysis_terminal"),
    };

    internal static string Serialize(string candidateSha, string terminalStatus, OwnedSessionExecutionCheckpointV1? checkpoint)
        => Serialize(candidateSha, terminalStatus, checkpoint, null, null, null, null);

    internal static string Serialize(string candidateSha, string terminalStatus, OwnedSessionExecutionCheckpointV1? checkpoint,
        OwnedSessionDiagnosticEventV1? phase, OwnedSessionDiagnosticEventV1? reason,
        OwnedSessionPostFreezeOutcomeV1? postFreezeFailure = null,
        Issue158PostSuccessFailureV1? postSuccessFailure = null,
        Issue158ExecutionEvidenceFailureV1? executionEvidenceFailure = null)
    {
        if (candidateSha.Length != 40 || candidateSha.Any(static value => !char.IsAsciiHexDigitLower(value))
            || terminalStatus is not ("failed" or "canceled" or "timed_out" or "succeeded"))
            throw new InvalidOperationException("blocker");
        var postSuccess = PostSuccessWire(postSuccessFailure);
        var evidenceFailure = ExecutionEvidenceWire(executionEvidenceFailure);
        if (terminalStatus == "succeeded")
        {
            var checkpointValid = postSuccessFailure == Issue158PostSuccessFailureV1.PublicationBarrier
                ? checkpoint is OwnedSessionExecutionCheckpointV1.CandidateReady or OwnedSessionExecutionCheckpointV1.CandidatePublished
                : checkpoint == OwnedSessionExecutionCheckpointV1.CandidatePublished;
            if (postSuccess == "none" || !checkpointValid
                || phase is not null || reason is not null || postFreezeFailure is not null)
                throw new InvalidOperationException("blocker");
        }
        else if (postSuccess != "none") throw new InvalidOperationException("blocker");
        if ((terminalStatus == "succeeded" && postSuccessFailure == Issue158PostSuccessFailureV1.ExecutionEvidence)
            != (executionEvidenceFailure is not null and not Issue158ExecutionEvidenceFailureV1.None))
            throw new InvalidOperationException("blocker");
        return JsonSerializer.Serialize(new
        {
            schema_version = "issue-158-windows-blocker.v6",
            candidate_sha = candidateSha,
            terminal_status = terminalStatus,
            last_checkpoint = checkpoint is null ? "none" : CheckpointWire(checkpoint.Value),
            driver_phase = PhaseWire(phase),
            poison_reason = PoisonWire(reason),
            post_freeze_failure = PostFreezeWire(postFreezeFailure),
            post_success_failure = postSuccess,
            execution_evidence_failure = evidenceFailure,
        });
    }

    internal static string PhaseWire(OwnedSessionDiagnosticEventV1? phase) => phase switch
    {
        null => "none",
        OwnedSessionDiagnosticEventV1.CommandPending => "command_pending",
        OwnedSessionDiagnosticEventV1.SendPending => "send_pending",
        _ => throw new InvalidOperationException("phase"),
    };

    internal static string PoisonWire(OwnedSessionDiagnosticEventV1? reason) => reason switch
    {
        null => "none",
        OwnedSessionDiagnosticEventV1.WorkTokenPreCanceled => "work_token_pre_canceled",
        OwnedSessionDiagnosticEventV1.ClosedRelevantEvent => "closed_relevant_event",
        OwnedSessionDiagnosticEventV1.SessionStartContract => "session_start_contract",
        OwnedSessionDiagnosticEventV1.SessionBindingContract => "session_binding_contract",
        OwnedSessionDiagnosticEventV1.InvocationIdentity => "invocation_identity",
        OwnedSessionDiagnosticEventV1.InvocationDescription => "invocation_description",
        OwnedSessionDiagnosticEventV1.InvocationContent => "invocation_content",
        OwnedSessionDiagnosticEventV1.InvocationNativeReproof => "invocation_native_reproof",
        OwnedSessionDiagnosticEventV1.InvocationPreparation => "invocation_preparation",
        OwnedSessionDiagnosticEventV1.InvocationBuffer => "invocation_buffer",
        OwnedSessionDiagnosticEventV1.TerminalContract => "terminal_contract",
        OwnedSessionDiagnosticEventV1.SessionError => "session_error",
        OwnedSessionDiagnosticEventV1.ModelCallFailure => "model_call_failure",
        OwnedSessionDiagnosticEventV1.Abort => "abort",
        OwnedSessionDiagnosticEventV1.CallbackException => "callback_exception",
        _ => throw new InvalidOperationException("reason"),
    };

    internal static string PostFreezeWire(OwnedSessionPostFreezeOutcomeV1? outcome) => outcome is null
        ? "none"
        : OwnedSessionPostFreezeOutcomeObservationV1.Wire(outcome.Value);

    internal static string PostSuccessWire(Issue158PostSuccessFailureV1? stage) => stage switch
    {
        null or Issue158PostSuccessFailureV1.None => "none",
        Issue158PostSuccessFailureV1.PublicationBarrier => "publication_barrier",
        Issue158PostSuccessFailureV1.ExecutionEvidence => "execution_evidence",
        Issue158PostSuccessFailureV1.CommittedIdentitySequence => "committed_identity_sequence",
        Issue158PostSuccessFailureV1.MetadataRequest => "metadata_request",
        Issue158PostSuccessFailureV1.HistoricalRequest => "historical_request",
        Issue158PostSuccessFailureV1.CurrentFileRequest => "current_file_request",
        Issue158PostSuccessFailureV1.RouteMatrix => "route_matrix",
        Issue158PostSuccessFailureV1.MetadataDocument => "metadata_document",
        Issue158PostSuccessFailureV1.HistoricalDocument => "historical_document",
        Issue158PostSuccessFailureV1.CurrentFileDocument => "current_file_document",
        Issue158PostSuccessFailureV1.PersistenceCounts => "persistence_counts",
        Issue158PostSuccessFailureV1.AggregateCounts => "aggregate_counts",
        Issue158PostSuccessFailureV1.ObservedResult => "observed_result",
        Issue158PostSuccessFailureV1.ShutdownDrain => "shutdown_drain",
        Issue158PostSuccessFailureV1.ResultSerialization => "result_serialization",
        Issue158PostSuccessFailureV1.ResultWrite => "result_write",
        Issue158PostSuccessFailureV1.Unexpected => "unexpected",
        _ => throw new InvalidOperationException("post_success"),
    };

    internal static string ExecutionEvidenceWire(Issue158ExecutionEvidenceFailureV1? failure) => failure switch
    {
        null or Issue158ExecutionEvidenceFailureV1.None => "none",
        Issue158ExecutionEvidenceFailureV1.ObserverMissing => "observer_missing",
        Issue158ExecutionEvidenceFailureV1.ObserverMultiple => "observer_multiple",
        Issue158ExecutionEvidenceFailureV1.SourceApplicationVersion => "source_application_version",
        Issue158ExecutionEvidenceFailureV1.ProtocolVersion => "protocol_version",
        Issue158ExecutionEvidenceFailureV1.ClientStartCount => "client_start_count",
        Issue158ExecutionEvidenceFailureV1.StatusObservationCount => "status_observation_count",
        Issue158ExecutionEvidenceFailureV1.ProbeSessionCount => "probe_session_count",
        Issue158ExecutionEvidenceFailureV1.ExecutionSessionCount => "execution_session_count",
        Issue158ExecutionEvidenceFailureV1.RetainedRootCount => "retained_root_count",
        Issue158ExecutionEvidenceFailureV1.RetainedSkillCount => "retained_skill_count",
        Issue158ExecutionEvidenceFailureV1.ProbeInventoryCount => "probe_inventory_count",
        Issue158ExecutionEvidenceFailureV1.ExecutionInventoryCount => "execution_inventory_count",
        Issue158ExecutionEvidenceFailureV1.PreparedInvocationNone => "prepared_invocation_none",
        Issue158ExecutionEvidenceFailureV1.PreparedInvocationExcess => "prepared_invocation_excess",
        Issue158ExecutionEvidenceFailureV1.SameClient => "same_client",
        Issue158ExecutionEvidenceFailureV1.ExactToolUnion => "exact_tool_union",
        Issue158ExecutionEvidenceFailureV1.RetainedOnlyInventory => "retained_only_inventory",
        Issue158ExecutionEvidenceFailureV1.ProbeNativeReproof => "probe_native_reproof",
        Issue158ExecutionEvidenceFailureV1.ExecutionNativeReproof => "execution_native_reproof",
        Issue158ExecutionEvidenceFailureV1.CallbackNativeReproof => "callback_native_reproof",
        _ => throw new InvalidOperationException("execution_evidence"),
    };

    internal static async Task WriteAsync(Issue158WindowsLaneGate gate, string terminalStatus, OwnedSessionExecutionCheckpointV1? checkpoint,
        OwnedSessionDiagnosticEventV1? phase, OwnedSessionDiagnosticEventV1? reason,
        OwnedSessionPostFreezeOutcomeV1? postFreezeFailure,
        Issue158PostSuccessFailureV1? postSuccessFailure = null,
        Issue158ExecutionEvidenceFailureV1? executionEvidenceFailure = null)
    {
        var bytes = new UTF8Encoding(false, true).GetBytes(
            Serialize(gate.CandidateSha, terminalStatus, checkpoint, phase, reason, postFreezeFailure, postSuccessFailure, executionEvidenceFailure));
        if (bytes.Length > 4096) throw new InvalidOperationException("blocker");
        await using var stream = new FileStream(Path.Combine(gate.ResultDirectory, FileName), FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }
}

internal sealed record Issue158WindowsObservedResult(
    string CandidateSha, OwnedSessionExecutionEvidenceV1 Evidence,
    long UserInvoked, long AgentInvoked, long TaskComplete, long V2Imported, long V1Imported, long SnapshotRows,
    bool OperatorGate, bool CliOverrideAbsent, bool MetadataRoute, bool HistoricalRoute, bool CurrentFileRoute,
    bool ShutdownDrain, bool CleanupComplete);

internal static class Issue158RouteDocuments
{
    private const int MaxBytes = 1_048_576;

    internal static async Task ValidateMetadataAsync(HttpContent content, SkillInvocationV2CommittedIdentityV1 identity,
        string sourceVersion, CancellationToken cancellationToken)
    {
        using var document = await ReadAsync(content, cancellationToken);
        RequireExact(document.RootElement,
            "schema_version", "snapshot_id", "session_id", "claim_id", "event_id", "name", "source", "trigger",
            "invoked_at", "run_id", "trace_id", "span_id", "projection_validity", "snapshot_state", "snapshot_reason",
            "body_sha256", "body_utf8_bytes", "definition_path_sha256", "definition_path_utf8_bytes", "captured_at",
            "source_application_version", "adapter_version", "payload_schema");
        RequireString(document.RootElement, "schema_version", "local-skill-invocation-snapshot.metadata.v1");
        RequireString(document.RootElement, "session_id", identity.SessionId.ToString("D"));
        RequireString(document.RootElement, "snapshot_id", identity.SnapshotId.ToString("D"));
        RequireString(document.RootElement, "projection_validity", "current");
        RequireString(document.RootElement, "snapshot_state", "available");
        RequireString(document.RootElement, "source_application_version", sourceVersion);
    }

    internal static async Task ValidateHistoricalAsync(HttpContent content, Guid snapshotId, string expectedBody,
        CancellationToken cancellationToken)
    {
        using var document = await ReadAsync(content, cancellationToken);
        RequireExact(document.RootElement, "schema_version", "snapshot_id", "content_kind", "body", "definition_path",
            "body_sha256", "definition_path_sha256", "captured_at");
        RequireString(document.RootElement, "schema_version", "local-skill-invocation-snapshot.content.v1");
        RequireString(document.RootElement, "snapshot_id", snapshotId.ToString("D"));
        RequireString(document.RootElement, "content_kind", "historical_snapshot");
        RequireString(document.RootElement, "body", expectedBody);
        RequireHash(document.RootElement, "body_sha256");
        RequireHash(document.RootElement, "definition_path_sha256");
    }

    internal static async Task ValidateCurrentAsync(HttpContent content, Guid snapshotId, string expectedBody,
        CancellationToken cancellationToken)
    {
        using var document = await ReadAsync(content, cancellationToken);
        RequireExact(document.RootElement, "schema_version", "snapshot_id", "content_kind", "comparison",
            "historical_body_sha256", "current_body_sha256", "current_body_utf8_bytes", "body", "read_at");
        RequireString(document.RootElement, "schema_version", "local-skill-current-file-read.response.v1");
        RequireString(document.RootElement, "snapshot_id", snapshotId.ToString("D"));
        RequireString(document.RootElement, "content_kind", "current_file");
        RequireString(document.RootElement, "comparison", "same");
        RequireString(document.RootElement, "body", expectedBody);
        RequireHash(document.RootElement, "historical_body_sha256");
        RequireHash(document.RootElement, "current_body_sha256");
        if (!document.RootElement.GetProperty("current_body_utf8_bytes").TryGetInt64(out var bytes)
            || bytes != Encoding.UTF8.GetByteCount(expectedBody))
            throw new InvalidOperationException("route_document");
    }

    private static async Task<JsonDocument> ReadAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            await using var input = await content.ReadAsStreamAsync(cancellationToken);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (output.Length + read > MaxBytes) throw new InvalidOperationException("route_document");
                output.Write(buffer, 0, read);
            }
            return JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new InvalidOperationException("route_document"); }
    }

    private static void RequireExact(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("route_document");
        var actual = element.EnumerateObject().Select(static property => property.Name).ToArray();
        if (actual.Length != expected.Length || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length
            || !actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            throw new InvalidOperationException("route_document");
    }

    private static void RequireString(JsonElement element, string property, string expected)
    {
        if (element.GetProperty(property).ValueKind != JsonValueKind.String
            || !string.Equals(element.GetProperty(property).GetString(), expected, StringComparison.Ordinal))
            throw new InvalidOperationException("route_document");
    }

    private static void RequireHash(JsonElement element, string property)
    {
        var value = element.GetProperty(property).GetString();
        if (value is null || value.Length != 64 || value.Any(static c => !char.IsAsciiHexDigit(c) || char.IsAsciiLetterUpper(c)))
            throw new InvalidOperationException("route_document");
    }
}

internal static class Issue158WindowsResult
{
    internal static bool IsCertified(OwnedSessionExecutionEvidenceV1 evidence) =>
        Issue158ExecutionEvidenceClassifier.Classify([evidence]) == Issue158ExecutionEvidenceFailureV1.None;

    internal static string Serialize(Issue158WindowsObservedResult result)
    {
        var invocationCount = checked(result.UserInvoked + result.AgentInvoked);
        if (!IsCertified(result.Evidence) || result is not
            { UserInvoked: 1, AgentInvoked: >= 0 and <= 63, TaskComplete: 1, V1Imported: 2,
              OperatorGate: true, CliOverrideAbsent: true, MetadataRoute: true, HistoricalRoute: true,
              CurrentFileRoute: true, ShutdownDrain: true }
            || result.Evidence.PreparedInvocationCount != invocationCount
            || result.V2Imported != invocationCount || result.SnapshotRows != invocationCount)
            throw new InvalidOperationException("result_evidence");
        return JsonSerializer.Serialize(new
        {
        schema_version = "issue-158-live-validation.v1",
        candidate_sha = result.CandidateSha,
        lane = "windows_owned_session",
        outcome = "passed",
        source_application_version = result.Evidence.SourceApplicationVersion,
        protocol_version = result.Evidence.ProtocolVersion,
        counts = new { retained_roots = result.Evidence.RetainedRootCount, retained_skills = result.Evidence.RetainedSkillCount, probe_sessions = result.Evidence.ProbeSessionCount, execution_sessions = result.Evidence.ExecutionSessionCount, user_invoked = result.UserInvoked, agent_invoked = result.AgentInvoked, task_complete = result.TaskComplete, v2_imported = result.V2Imported, v1_imported = result.V1Imported, snapshot_rows = result.SnapshotRows },
        checks = new { operator_gate = result.OperatorGate, cli_override_absent = result.CliOverrideAbsent, retained_only_inventory = result.Evidence.RetainedOnlyInventory, exact_tool_union = result.Evidence.ExactToolUnion, native_reproof = result.Evidence.ProbeNativeReproof && result.Evidence.ExecutionNativeReproof && result.Evidence.CallbackNativeReproof, current_generation = result.CurrentFileRoute, metadata_route = result.MetadataRoute, historical_route = result.HistoricalRoute, current_file_route = result.CurrentFileRoute, shutdown_drain = result.ShutdownDrain, cleanup_complete = result.CleanupComplete },
        exit_code = 0,
        });
    }
}
