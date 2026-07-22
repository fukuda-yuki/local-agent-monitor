using System.Globalization;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class HistoricalImportApplicationTests
{
    [Fact]
    public void IncludeContentIsRejectedBeforeTheSourceGatewayIsInvoked()
    {
        using var database = new HistoricalImportTestDatabase();
        var gateway = new HistoricalImportTestGateway(UnsupportedProbe());
        var application = CreateApplication(database, gateway, HistoricalAdmissionRegistry.Empty);

        var exception = Assert.Throws<HistoricalImportException>(() => application.CreatePreview(
            PreviewRequest() with { RequestedCapture = "include_content" }));

        Assert.Equal(HistoricalImportErrorCodes.RequestInvalid, exception.Code);
        Assert.Equal(0, gateway.ProbeCount);
        AssertNoWorkflowRows(database);
    }

    [Fact]
    public void ZeroCandidatePreview_FailsClosedAndDoesNotPersistPrivateLocatorOrConfirmation()
    {
        using var database = new HistoricalImportTestDatabase();
        var gateway = new HistoricalImportTestGateway(UnsupportedProbe());
        var application = CreateApplication(database, gateway, HistoricalAdmissionRegistry.Empty);

        var preview = application.CreatePreview(PreviewRequest());

        Assert.False(preview.CommitAllowed);
        Assert.Equal(HistoricalImportErrorCodes.NoEligibleCandidates, preview.RejectionCode);
        Assert.Equal(HistoricalImportCount.Available(0), preview.Counts.Eligible);
        Assert.Equal(HistoricalImportCount.Unavailable, preview.Counts.Total);
        var exception = Assert.Throws<HistoricalImportException>(() => application.IssueConfirmation(ConfirmationRequest(preview)));
        Assert.Equal(HistoricalImportErrorCodes.NoEligibleCandidates, exception.Code);

        using var verification = database.Open();
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_confirmation_bindings;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_previews WHERE private_selection_json IS NOT NULL;"));
    }

    [Fact]
    public void ExactProfileCommit_WritesOnlyHistoricalObservationsAndNoRetentionInventory()
    {
        using var database = new HistoricalImportTestDatabase();
        using (var setup = database.Open())
        {
            Execute(setup, "CREATE TABLE sessions(id TEXT PRIMARY KEY);");
            Execute(setup, "CREATE TABLE session_runs(id TEXT PRIMARY KEY);");
            Execute(setup, "CREATE TABLE session_events(id TEXT PRIMARY KEY);");
            Execute(setup, "CREATE TABLE retention_items(id TEXT PRIMARY KEY);");
        }
        var fixture = AdmittedFixture(Candidate("11", ("model_tokens.model", "\"gpt-synthetic\""), ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry);

        var result = CommitOnce(application, requestId: "req_positive", idempotencyKey: "idem_positive");

        Assert.Equal("committed", result.TransactionOutcome);
        Assert.Equal(HistoricalImportCount.Available(1), result.Counts.NewObservations);
        Assert.Equal(HistoricalImportCount.Unavailable, result.Counts.NewSessions);
        Assert.Equal(HistoricalImportCount.Unavailable, result.Counts.NewEvents);
        Assert.Equal("not_applicable", result.Retention.Disposition);
        Assert.Equal(0, result.Retention.CreatedItemCount);
        Assert.Single(result.Observations);
        Assert.Equal("partial", result.Observations[0].Completeness);
        Assert.Equal("not_captured", result.Observations[0].ContentState);
        var status = application.ReadStatus(result.OperationId);
        Assert.Equal("succeeded", status.State);
        Assert.Equal(["queued", "running", "succeeded"], status.Lifecycle);
        Assert.Equal("committed", status.TransactionOutcome);
        Assert.True(status.ResultAvailable);
        Assert.Null(status.FailureCode);
        var history = Assert.Single(application.ListHistory().Items);
        Assert.Equal(result.OperationId, history.OperationId);
        Assert.Equal("succeeded", history.State);
        Assert.Equal("committed", history.Outcome);

        using var verification = database.Open();
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_observations;"));
        Assert.Equal(2L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_observation_fields;"));
        Assert.Equal(2L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_observation_provenance;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM sessions;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM session_runs;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM session_events;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM retention_items;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_previews WHERE private_selection_json IS NOT NULL;"));
    }

    [Fact]
    public void ExactIdempotentReplay_ReturnsOriginalOperationWithoutAnotherWrite()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(Candidate("21", ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry);
        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));
        var request = CommitRequest(preview, confirmation, "req_replay", "idem_replay");

        var first = application.Commit(request);
        var replay = application.Commit(request);

        Assert.Equal(first.OperationId, replay.OperationId);
        Assert.Equal("first_application", first.IdempotencyOutcome);
        Assert.Equal("replayed", replay.IdempotencyOutcome);
        using var verification = database.Open();
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_operations;"));
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_observations;"));
    }

    [Fact]
    public void SucceededStatusResultHistoryAndReplaySurviveApplicationRestart()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(Candidate("211", ("model_tokens.input_tokens", "42")));
        var firstApplication = CreateApplication(database, fixture.Gateway, fixture.Registry);
        var preview = firstApplication.CreatePreview(PreviewRequest());
        var confirmation = firstApplication.IssueConfirmation(ConfirmationRequest(preview));
        var request = CommitRequest(preview, confirmation, "req_restart_result", "idem_restart_result");
        var first = firstApplication.Commit(request);
        firstApplication.Dispose();

        using var restarted = CreateApplication(database, fixture.Gateway, fixture.Registry);
        var status = restarted.ReadStatus(first.OperationId);
        var result = restarted.ReadResult(first.OperationId);
        var replay = restarted.Commit(request);
        var history = Assert.Single(restarted.ListHistory().Items);

        Assert.Equal("succeeded", status.State);
        Assert.Equal("committed", status.TransactionOutcome);
        Assert.Equal(HistoricalImportJson.SerializeString(first), HistoricalImportJson.SerializeString(result));
        Assert.Equal(first.OperationId, replay.OperationId);
        Assert.Equal("replayed", replay.IdempotencyOutcome);
        Assert.Equal(first.OperationId, history.OperationId);
        using var verification = database.Open();
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_operations;"));
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_observations;"));
    }

    [Fact]
    public void WrongConfirmationDoesNotClearALivePreview()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(Candidate("22", ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry);
        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));
        var wrongConfirmation = confirmation with { ConfirmationId = Token("hic_", "wrong_confirmation") };

        var exception = Assert.Throws<HistoricalImportException>(() => application.Commit(
            CommitRequest(preview, wrongConfirmation, "req_wrong_confirmation", "idem_wrong_confirmation")));

        Assert.Equal(HistoricalImportErrorCodes.ConfirmationInvalid, exception.Code);
        Assert.Null(exception.OperationId);
        using (var beforeValidCommit = database.Open())
            Assert.Equal(0L, Scalar(beforeValidCommit, "SELECT COUNT(*) FROM historical_import_operations;"));
        Assert.Equal(1L, EphemeralPreviewCount(database));
        var result = application.Commit(CommitRequest(preview, confirmation, "req_right_confirmation", "idem_right_confirmation"));
        Assert.Equal("committed", result.TransactionOutcome);
    }

    [Fact]
    public void IdempotencyConflictDoesNotClearAnotherLivePreview()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(Candidate("23", ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry);
        CommitOnce(application, "req_idempotency_original", "shared_idempotency");
        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));

        var exception = Assert.Throws<HistoricalImportException>(() => application.Commit(
            CommitRequest(preview, confirmation, "req_idempotency_conflict", "shared_idempotency")));

        Assert.Equal(HistoricalImportErrorCodes.IdempotencyConflict, exception.Code);
        Assert.Null(exception.OperationId);
        using (var afterConflict = database.Open())
            Assert.Equal(1L, Scalar(afterConflict, "SELECT COUNT(*) FROM historical_import_operations;"));
        Assert.Equal(1L, EphemeralPreviewCount(database));
        var result = application.Commit(CommitRequest(
            preview,
            confirmation,
            "req_idempotency_recovery",
            "new_idempotency"));
        Assert.Equal(HistoricalImportCount.Available(1), result.Counts.Duplicates);
    }

    [Fact]
    public void RequestIdCanBeReusedWithADifferentIdempotencyKey()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(Candidate("24", ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry);
        CommitOnce(application, "shared_request", "idem_request_first");

        var repeated = CommitOnce(application, "shared_request", "idem_request_second");

        Assert.Equal(HistoricalImportCount.Available(1), repeated.Counts.Duplicates);
        using var verification = database.Open();
        Assert.Equal(2L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_operations;"));
    }

    [Fact]
    public void ExactDuplicate_IsANoopAndKeepsTheExistingObservation()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(Candidate("31", ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry);
        var first = CommitOnce(application, "req_duplicate_first", "idem_duplicate_first");

        var second = CommitOnce(application, "req_duplicate_second", "idem_duplicate_second");

        Assert.Equal(HistoricalImportCount.Available(0), second.Counts.NewObservations);
        Assert.Equal(HistoricalImportCount.Available(1), second.Counts.Duplicates);
        Assert.Equal("exact_duplicate_noop", Assert.Single(second.Duplicates).Decision);
        Assert.Equal(Assert.Single(first.Observations).ObservationId, second.Duplicates[0].ObservationId);
        using var verification = database.Open();
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_observations;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_conflicts;"));
    }

    [Fact]
    public void ConflictingRepeat_PreservesExistingFieldAndRecordsOnlySanitizedConflictMetadata()
    {
        using var database = new HistoricalImportTestDatabase();
        var initial = AdmittedFixture(Candidate("41", ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(database, initial.Gateway, initial.Registry);
        var first = CommitOnce(application, "req_conflict_first", "idem_conflict_first");
        initial.Gateway.ProbeResult = Probe(
            Batch(Candidate("41", ("model_tokens.input_tokens", "99"))),
            evidence: initial.Gateway.ProbeResult.AdmissionEvidence);

        var conflictResult = CommitOnce(application, "req_conflict_second", "idem_conflict_second");

        Assert.Equal(HistoricalImportCount.Available(0), conflictResult.Counts.NewObservations);
        Assert.Equal(HistoricalImportCount.Available(1), conflictResult.Counts.Conflicts);
        var conflict = Assert.Single(conflictResult.Conflicts);
        Assert.Equal(Assert.Single(first.Observations).ObservationId, conflict.ObservationId);
        Assert.Equal(["model_tokens.input_tokens"], conflict.ConflictFields);
        Assert.Equal("preserve_existing", conflict.Decision);
        using var verification = database.Open();
        Assert.Equal("42", StringScalar(verification, "SELECT canonical_value_json FROM historical_import_observation_fields;"));
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_conflicts;"));
        Assert.DoesNotContain("99", StringScalar(verification, "SELECT field_names_json FROM historical_import_conflicts;"), StringComparison.Ordinal);
    }

    [Fact]
    public void FixtureOnlyMarker_RejectsTheCompleteBatch()
    {
        using var database = new HistoricalImportTestDatabase();
        var batch = Batch(Candidate("51", ("model_tokens.input_tokens", "42"))) with { FixtureOnlyNotSourceSupportEvidence = true };
        var fixture = AdmittedFixture(batch);
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry);

        var exception = Assert.Throws<HistoricalImportException>(() => application.CreatePreview(PreviewRequest()));

        Assert.Equal(HistoricalImportErrorCodes.FixtureNotSourceSupportEvidence, exception.Code);
        AssertNoWorkflowRows(database);
    }

    [Fact]
    public void ProvenanceMismatch_RejectsTheCompleteBatch()
    {
        using var database = new HistoricalImportTestDatabase();
        var valid = Candidate("61", ("model_tokens.model", "\"gpt-synthetic\""), ("model_tokens.input_tokens", "42"));
        var malformed = valid with { FieldProvenance = valid.FieldProvenance.Take(1).ToArray() };
        var fixture = AdmittedFixture(malformed);
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry);

        var exception = Assert.Throws<HistoricalImportException>(() => application.CreatePreview(PreviewRequest()));

        Assert.Equal(HistoricalImportErrorCodes.CandidateInvalid, exception.Code);
        AssertNoWorkflowRows(database);
    }

    [Fact]
    public void InternalJsonRejectsQuotedNumericFields()
    {
        var json = HistoricalImportJson.SerializeString(UnsupportedProbe().AdapterResult)
            .Replace("\"candidate_count\":0", "\"candidate_count\":\"0\"", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => HistoricalImportJson.Deserialize<HistoricalAdapterResult>(json));
    }

    [Fact]
    public void PersistedResultWithQuotedNumericFieldFailsClosed()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(Candidate("62", ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry);
        var result = CommitOnce(application, "req_quoted_result", "idem_quoted_result");
        using (var mutation = database.Open())
        {
            Execute(mutation,
                "UPDATE historical_import_operations SET result_json=json_set(result_json,'$.counts.total.value','1') WHERE operation_id='" + result.OperationId + "';");
        }

        var exception = Assert.Throws<HistoricalImportException>(() => application.ReadResult(result.OperationId));

        Assert.Equal(HistoricalImportErrorCodes.StoreUnavailable, exception.Code);
    }

    [Fact]
    public void ChangedSourceSnapshot_RejectsCommitAsSourceChanged()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(Candidate("71", ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry);
        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));
        fixture.Gateway.ProbeResult = fixture.Gateway.ProbeResult with
        {
            SnapshotVersion = "hsv_2",
            SnapshotDigest = Fingerprint('2'),
        };

        var exception = Assert.Throws<HistoricalImportException>(() =>
            application.Commit(CommitRequest(preview, confirmation, "req_changed", "idem_changed")));

        Assert.Equal(HistoricalImportErrorCodes.SourceChanged, exception.Code);
        Assert.NotNull(exception.OperationId);
        var status = application.ReadStatus(exception.OperationId!);
        Assert.Equal("rejected", status.State);
        Assert.Equal(["queued", "running", "rejected"], status.Lifecycle);
        Assert.Equal("not_started", status.TransactionOutcome);
        Assert.Equal(HistoricalImportErrorCodes.SourceChanged, status.FailureCode);
        Assert.False(status.ResultAvailable);
        Assert.Equal(new(1, 0, 0, 0, 0, 0), status.Counts);
        var resultException = Assert.Throws<HistoricalImportException>(() => application.ReadResult(status.OperationId));
        Assert.Equal(HistoricalImportErrorCodes.ResultNotAvailable, resultException.Code);
        Assert.Equal(status.OperationId, resultException.OperationId);
        var replay = Assert.Throws<HistoricalImportException>(() =>
            application.Commit(CommitRequest(preview, confirmation, "req_changed", "idem_changed")));
        Assert.Equal(HistoricalImportErrorCodes.SourceChanged, replay.Code);
        Assert.Equal(status.OperationId, replay.OperationId);
        var history = Assert.Single(application.ListHistory().Items);
        Assert.Equal("rejected", history.State);
        Assert.Equal("not_started", history.Outcome);
        Assert.Equal(0, history.NewObservationCount);
        Assert.Equal(0, history.DuplicateCount);
        Assert.Equal(0, history.ConflictCount);
        Assert.Equal("none", history.Completeness);
        Assert.Empty(history.CompletenessReasons);
        AssertNoDomainRows(database);
        using var verification = database.Open();
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_operations;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_previews WHERE private_selection_json IS NOT NULL;"));
    }

    [Fact]
    public void ChangedProbePayloadAtSameSnapshot_RejectsCommitAsPreviewStaleAndPurgesPrivateLocator()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(Candidate("72", ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry);
        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));
        fixture.Gateway.ProbeResult = Probe(
            Batch(Candidate("72", ("model_tokens.input_tokens", "99"))),
            evidence: fixture.Gateway.ProbeResult.AdmissionEvidence) with
        {
            SnapshotVersion = preview.SnapshotVersion,
            SnapshotDigest = fixture.Gateway.ProbeResult.SnapshotDigest,
        };

        var exception = Assert.Throws<HistoricalImportException>(() =>
            application.Commit(CommitRequest(preview, confirmation, "req_stale", "idem_stale")));

        Assert.Equal(HistoricalImportErrorCodes.PreviewStale, exception.Code);
        Assert.NotNull(exception.OperationId);
        var status = application.ReadStatus(exception.OperationId!);
        Assert.Equal("rejected", status.State);
        Assert.Equal("not_started", status.TransactionOutcome);
        Assert.Equal(HistoricalImportErrorCodes.PreviewStale, status.FailureCode);
        AssertNoDomainRows(database);
        using var verification = database.Open();
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_operations;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_previews WHERE private_selection_json IS NOT NULL;"));
    }

    [Fact]
    public void ExpiredPreview_PurgesPrivateLocatorAndCannotBeConfirmed()
    {
        using var database = new HistoricalImportTestDatabase();
        var clock = new MutableHistoricalImportTimeProvider(new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
        var fixture = AdmittedFixture(Candidate("81", ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry, clock);
        var preview = application.CreatePreview(PreviewRequest());
        using (var before = database.Open())
            Assert.Equal(1L, Scalar(before, "SELECT COUNT(*) FROM historical_import_previews WHERE private_selection_json IS NOT NULL;"));
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        var exception = Assert.Throws<HistoricalImportException>(() => application.IssueConfirmation(ConfirmationRequest(preview)));

        Assert.Equal(HistoricalImportErrorCodes.PreviewExpired, exception.Code);
        using var verification = database.Open();
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_previews WHERE private_selection_json IS NOT NULL;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_confirmation_bindings;"));
    }

    [Fact]
    public void ActiveExpiryTimerPurgesPrivateSelectionProbeAndBatchWithoutARead()
    {
        using var database = new HistoricalImportTestDatabase();
        var clock = new MutableHistoricalImportTimeProvider(new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
        var fixture = AdmittedFixture(Candidate("811", ("model_tokens.input_tokens", "42")));
        using var application = CreateApplication(database, fixture.Gateway, fixture.Registry, clock);
        application.CreatePreview(PreviewRequest());
        Assert.Equal(1L, EphemeralPreviewCount(database));

        clock.Advance(TimeSpan.FromMinutes(5));

        AssertNoEphemeralPreviewState(database);
    }

    [Fact]
    public void PrivateSourceReferenceIsConfinedToPrivateSelectionAndNeverShapesPublicDigest()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(Candidate("812", ("model_tokens.input_tokens", "42")));
        using var application = CreateApplication(database, fixture.Gateway, fixture.Registry);
        var request = PreviewRequest();

        var preview = application.CreatePreview(request);

        using var verification = database.Open();
        using var command = verification.CreateCommand();
        command.CommandText =
            "SELECT private_selection_json,probe_json,candidate_batch_json,preview_json,preview_digest FROM historical_import_previews WHERE preview_id=$id;";
        command.Parameters.AddWithValue("$id", preview.PreviewId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var privateSelection = HistoricalImportJson.Deserialize<HistoricalSourceSelection>(reader.GetString(0));
        Assert.Equal(request.ExactReference, privateSelection.ExactReference);
        var forbiddenFragment = "synthetic-historical-import";
        Assert.DoesNotContain(forbiddenFragment, reader.GetString(1), StringComparison.Ordinal);
        Assert.DoesNotContain(forbiddenFragment, reader.GetString(2), StringComparison.Ordinal);
        Assert.DoesNotContain(forbiddenFragment, reader.GetString(3), StringComparison.Ordinal);
        Assert.DoesNotContain(forbiddenFragment, reader.GetString(4), StringComparison.Ordinal);
        Assert.DoesNotContain(forbiddenFragment, HistoricalImportJson.SerializeString(preview), StringComparison.Ordinal);
    }

    [Fact]
    public void StartupSweepPurgesExpiredPrivateStateBeforeAnyWorkflowRead()
    {
        using var database = new HistoricalImportTestDatabase();
        var clock = new MutableHistoricalImportTimeProvider(new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
        var fixture = AdmittedFixture(Candidate("82", ("model_tokens.input_tokens", "42")));
        var firstApplication = CreateApplication(database, fixture.Gateway, fixture.Registry, clock);
        firstApplication.CreatePreview(PreviewRequest());
        firstApplication.Dispose();
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        using var restartedApplication = CreateApplication(database, fixture.Gateway, fixture.Registry, clock);

        AssertNoEphemeralPreviewState(database);
    }

    [Fact]
    public void RestartSchedulesCleanupForPersistedUnexpiredPrivatePreview()
    {
        using var database = new HistoricalImportTestDatabase();
        var clock = new MutableHistoricalImportTimeProvider(new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
        var fixture = AdmittedFixture(Candidate("813", ("model_tokens.input_tokens", "42")));
        using (var firstApplication = CreateApplication(database, fixture.Gateway, fixture.Registry, clock))
            firstApplication.CreatePreview(PreviewRequest());
        Assert.Equal(1L, EphemeralPreviewCount(database));

        using var restarted = CreateApplication(database, fixture.Gateway, fixture.Registry, clock);
        clock.Advance(TimeSpan.FromMinutes(5));

        AssertNoEphemeralPreviewState(database);
    }

    [Theory]
    [InlineData("private_selection_json")]
    [InlineData("probe_json")]
    [InlineData("candidate_batch_json")]
    public void RestartSchedulesCleanupWhenOnlyOnePrivatePreviewColumnRemains(string retainedColumn)
    {
        using var database = new HistoricalImportTestDatabase();
        var clock = new MutableHistoricalImportTimeProvider(new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
        var fixture = AdmittedFixture(Candidate("814", ("model_tokens.input_tokens", "42")));
        HistoricalImportPreview preview;
        using (var firstApplication = CreateApplication(database, fixture.Gateway, fixture.Registry, clock))
            preview = firstApplication.CreatePreview(PreviewRequest());
        var clearedColumns = new[] { "private_selection_json", "probe_json", "candidate_batch_json" }
            .Where(column => column != retainedColumn)
            .Select(column => $"{column}=NULL");
        using (var mutation = database.Open())
            Execute(mutation, $"UPDATE historical_import_previews SET {string.Join(',', clearedColumns)} WHERE preview_id='{preview.PreviewId}';");

        using var restarted = CreateApplication(database, fixture.Gateway, fixture.Registry, clock);
        clock.Advance(TimeSpan.FromMinutes(5));

        AssertNoEphemeralPreviewState(database);
    }

    [Fact]
    public void AcceptedCommit_PersistsQueryableQueuedAndRunningStatesBeforeSuccess()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(Candidate("821", ("model_tokens.input_tokens", "42")));
        var checkpoint = new RecordingHistoricalImportLifecycleCheckpoint(database.Path);
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry, checkpoint: checkpoint);

        var result = CommitOnce(application, "req_lifecycle", "idem_lifecycle");

        Assert.Collection(
            checkpoint.Statuses,
            queued =>
            {
                Assert.Equal(result.OperationId, queued.OperationId);
                Assert.Equal(1, queued.OperationVersion);
                Assert.Equal("queued", queued.State);
                Assert.Equal(["queued"], queued.Lifecycle);
                Assert.Equal("pending", queued.TransactionOutcome);
                Assert.False(queued.ResultAvailable);
            },
            running =>
            {
                Assert.Equal(result.OperationId, running.OperationId);
                Assert.Equal(2, running.OperationVersion);
                Assert.Equal("running", running.State);
                Assert.Equal(["queued", "running"], running.Lifecycle);
                Assert.Equal("pending", running.TransactionOutcome);
                Assert.False(running.ResultAvailable);
            });
        Assert.Equal("succeeded", application.ReadStatus(result.OperationId).State);
    }

    [Fact]
    public void StartupRecovery_TerminalizesAbandonedRunningOperationWithoutDomainClaims()
    {
        using var database = new HistoricalImportTestDatabase();
        var clock = new MutableHistoricalImportTimeProvider(new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
        var fixture = AdmittedFixture(Candidate("822", ("model_tokens.input_tokens", "42")));
        using var firstApplication = CreateApplication(database, fixture.Gateway, fixture.Registry, clock);
        var preview = firstApplication.CreatePreview(PreviewRequest());
        var confirmation = firstApplication.IssueConfirmation(ConfirmationRequest(preview));
        var request = CommitRequest(preview, confirmation, "req_abandoned", "idem_abandoned");
        var store = new SqliteHistoricalImportStore(database.Path);
        var queued = store.QueueOperation(QueueCommand(
            store,
            request,
            preview,
            fixture.Gateway.ProbeResult.CandidateBatch!.Candidates.Count,
            clock));
        Assert.Null(queued.ReplayResult);
        store.MarkOperationRunning(queued.OperationId);
        firstApplication.Dispose();
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        using var restarted = CreateApplication(database, fixture.Gateway, fixture.Registry, clock);

        var status = restarted.ReadStatus(queued.OperationId);
        Assert.Equal("failed", status.State);
        Assert.Equal(["queued", "running", "failed"], status.Lifecycle);
        Assert.Equal("rolled_back", status.TransactionOutcome);
        Assert.Equal(HistoricalImportErrorCodes.TransactionFailed, status.FailureCode);
        Assert.Equal(new(1, 0, 0, 0, 0, 0), status.Counts);
        Assert.False(status.ResultAvailable);
        AssertNoDomainRows(database);
        AssertNoEphemeralPreviewState(database);
        var replay = Assert.Throws<HistoricalImportException>(() => restarted.Commit(request));
        Assert.Equal(HistoricalImportErrorCodes.TransactionFailed, replay.Code);
        Assert.Equal(queued.OperationId, replay.OperationId);
        using var verification = database.Open();
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_operations;"));
    }

    [Fact]
    public void StartupRecoveryDoesNotFailAnUnexpiredRunningOperationOwnedByAnotherProcess()
    {
        using var database = new HistoricalImportTestDatabase();
        var clock = new MutableHistoricalImportTimeProvider(new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
        var fixture = AdmittedFixture(Candidate("824", ("model_tokens.input_tokens", "42")));
        using var firstApplication = CreateApplication(database, fixture.Gateway, fixture.Registry, clock);
        var preview = firstApplication.CreatePreview(PreviewRequest());
        var confirmation = firstApplication.IssueConfirmation(ConfirmationRequest(preview));
        var request = CommitRequest(preview, confirmation, "req_live_owner", "idem_live_owner");
        var store = new SqliteHistoricalImportStore(database.Path);
        var queued = store.QueueOperation(QueueCommand(store, request, preview, 1, clock));
        store.MarkOperationRunning(queued.OperationId);

        using var secondApplication = CreateApplication(database, fixture.Gateway, fixture.Registry, clock);

        var status = secondApplication.ReadStatus(queued.OperationId);
        Assert.Equal("running", status.State);
        Assert.Equal("pending", status.TransactionOutcome);
        Assert.Equal(1L, EphemeralPreviewCount(database));
    }

    [Fact]
    public void ExactRetryOfPendingOperation_ReturnsFixedStatusErrorWithOperationId()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(Candidate("823", ("model_tokens.input_tokens", "42")));
        using var application = CreateApplication(database, fixture.Gateway, fixture.Registry);
        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));
        var request = CommitRequest(preview, confirmation, "req_pending", "idem_pending");
        var store = new SqliteHistoricalImportStore(database.Path);
        var queued = store.QueueOperation(QueueCommand(
            store,
            request,
            preview,
            fixture.Gateway.ProbeResult.CandidateBatch!.Candidates.Count,
            TimeProvider.System));

        var exception = Assert.Throws<HistoricalImportException>(() => application.Commit(request));

        Assert.Equal(HistoricalImportErrorCodes.ResultNotAvailable, exception.Code);
        Assert.Equal(queued.OperationId, exception.OperationId);
        Assert.Equal("queued", application.ReadStatus(queued.OperationId).State);
        var resultException = Assert.Throws<HistoricalImportException>(() => application.ReadResult(queued.OperationId));
        Assert.Equal(HistoricalImportErrorCodes.ResultNotAvailable, resultException.Code);
        Assert.Equal(queued.OperationId, resultException.OperationId);
        using var verification = database.Open();
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_operations;"));
        Assert.Equal(0L, Scalar(verification,
            "SELECT COUNT(*) FROM historical_import_confirmation_bindings WHERE consumed_operation_id IS NOT NULL;"));
        Assert.Equal(1L, EphemeralPreviewCount(database));
    }

    [Fact]
    public async Task ConcurrentExactRetryObservesPendingOperationThenReplaysTheCommittedResult()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(Candidate("825", ("model_tokens.input_tokens", "42")));
        using var checkpoint = new BlockingAfterRunningHistoricalImportCheckpoint();
        using var firstApplication = CreateApplication(
            database,
            fixture.Gateway,
            fixture.Registry,
            checkpoint: checkpoint);
        using var secondApplication = CreateApplication(database, fixture.Gateway, fixture.Registry);
        var preview = firstApplication.CreatePreview(PreviewRequest());
        var confirmation = firstApplication.IssueConfirmation(ConfirmationRequest(preview));
        var request = CommitRequest(preview, confirmation, "req_concurrent_idem", "idem_concurrent_idem");
        var firstCommit = Task.Run(() => firstApplication.Commit(request));
        Assert.True(checkpoint.WaitUntilRunning(TimeSpan.FromSeconds(5)));

        HistoricalImportException pending;
        try
        {
            pending = Assert.Throws<HistoricalImportException>(() => secondApplication.Commit(request));
        }
        finally
        {
            checkpoint.Release();
        }
        var committed = await firstCommit.WaitAsync(TimeSpan.FromSeconds(5));
        var replayed = secondApplication.Commit(request);

        Assert.Equal(HistoricalImportErrorCodes.ResultNotAvailable, pending.Code);
        Assert.Equal(committed.OperationId, pending.OperationId);
        Assert.Equal(committed.OperationId, replayed.OperationId);
        Assert.Equal("replayed", replayed.IdempotencyOutcome);
        using var verification = database.Open();
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_operations;"));
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_observations;"));
    }

    [Theory]
    [InlineData(HistoricalImportErrorCodes.StoreBusy)]
    [InlineData(HistoricalImportErrorCodes.StoreUnavailable)]
    public void PreDomainStoreFailureAfterAcceptanceIsRejectedWithoutRollbackClaims(string failureCode)
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(Candidate("826", ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(
            database,
            fixture.Gateway,
            fixture.Registry,
            checkpoint: new RejectingAfterRunningHistoricalImportCheckpoint(failureCode));
        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));

        var exception = Assert.Throws<HistoricalImportException>(() => application.Commit(
            CommitRequest(preview, confirmation, "req_predomain_store", "idem_predomain_store")));

        Assert.Equal(failureCode, exception.Code);
        Assert.NotNull(exception.OperationId);
        var status = application.ReadStatus(exception.OperationId!);
        Assert.Equal("rejected", status.State);
        Assert.Equal("not_started", status.TransactionOutcome);
        Assert.Equal(failureCode, status.FailureCode);
        AssertNoDomainRows(database);
        AssertNoEphemeralPreviewState(database);
    }

    [Fact]
    public void FailureBeforeTheDomainTransactionBeginsIsRejectedWithoutRollbackClaims()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(Candidate("827", ("model_tokens.input_tokens", "42")));
        var checkpoint = new InterleavingHistoricalImportCheckpoint
        {
            BeforeTransaction = () => throw new HistoricalImportException(HistoricalImportErrorCodes.StoreBusy),
        };
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry, checkpoint: checkpoint);
        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));

        var exception = Assert.Throws<HistoricalImportException>(() => application.Commit(
            CommitRequest(preview, confirmation, "req_prebegin_store", "idem_prebegin_store")));

        Assert.Equal(HistoricalImportErrorCodes.StoreBusy, exception.Code);
        Assert.NotNull(exception.OperationId);
        var status = application.ReadStatus(exception.OperationId!);
        Assert.Equal("rejected", status.State);
        Assert.Equal("not_started", status.TransactionOutcome);
        Assert.Equal(HistoricalImportErrorCodes.StoreBusy, status.FailureCode);
        AssertNoDomainRows(database);
        AssertNoEphemeralPreviewState(database);
    }

    [Fact]
    public void StoreFailureAfterTheDomainTransactionBeginsIsFailedWithRollbackClaims()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(Candidate("833", ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(
            database,
            fixture.Gateway,
            fixture.Registry,
            checkpoint: new BusyAfterCandidateHistoricalImportCheckpoint());
        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));

        var exception = Assert.Throws<HistoricalImportException>(() => application.Commit(
            CommitRequest(preview, confirmation, "req_postbegin_store", "idem_postbegin_store")));

        Assert.Equal(HistoricalImportErrorCodes.TransactionFailed, exception.Code);
        Assert.NotNull(exception.OperationId);
        var status = application.ReadStatus(exception.OperationId!);
        Assert.Equal("failed", status.State);
        Assert.Equal("rolled_back", status.TransactionOutcome);
        Assert.Equal(HistoricalImportErrorCodes.TransactionFailed, status.FailureCode);
        AssertNoDomainRows(database);
        AssertNoEphemeralPreviewState(database);
    }

    [Fact]
    public void BusyRunningAndTerminalWritesRecoverTheQueuedOperationAtPreviewExpiry()
    {
        using var database = new HistoricalImportTestDatabase();
        var clock = new MutableHistoricalImportTimeProvider(new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
        var fixture = AdmittedFixture(Candidate("828", ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(
            database,
            fixture.Gateway,
            fixture.Registry,
            clock,
            new BusyRunningAndTerminalHistoricalImportCheckpoint());
        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));

        var exception = Assert.Throws<HistoricalImportException>(() => application.Commit(
            CommitRequest(preview, confirmation, "req_busy_terminal", "idem_busy_terminal")));

        Assert.Equal(HistoricalImportErrorCodes.TransactionFailed, exception.Code);
        Assert.NotNull(exception.OperationId);
        Assert.Equal("queued", application.ReadStatus(exception.OperationId!).State);
        Assert.Equal(1L, EphemeralPreviewCount(database));

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        var recovered = application.ReadStatus(exception.OperationId!);
        Assert.Equal("failed", recovered.State);
        Assert.Equal("rolled_back", recovered.TransactionOutcome);
        Assert.Equal(HistoricalImportErrorCodes.TransactionFailed, recovered.FailureCode);
        AssertNoEphemeralPreviewState(database);
    }

    [Fact]
    public void QueueOperationRejectsAConsumedConfirmationWithoutCreatingAnOperation()
    {
        using var database = new HistoricalImportTestDatabase();
        var clock = new MutableHistoricalImportTimeProvider(new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
        var fixture = AdmittedFixture(Candidate("829", ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry, clock);
        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));
        var request = CommitRequest(preview, confirmation, "req_consumed_queue", "idem_consumed_queue");
        var consumedOperationId = Token("hop_", "consumed_queue");
        using (var mutation = database.Open())
            Execute(mutation, $"UPDATE historical_import_confirmation_bindings SET consumed_operation_id='{consumedOperationId}' WHERE confirmation_id='{confirmation.ConfirmationId}';");
        var store = new SqliteHistoricalImportStore(database.Path);

        var exception = Assert.Throws<HistoricalImportException>(() => store.QueueOperation(QueueCommand(
            store,
            request,
            preview,
            fixture.Gateway.ProbeResult.CandidateBatch!.Candidates.Count,
            clock)));

        Assert.Equal(HistoricalImportErrorCodes.ConfirmationConsumed, exception.Code);
        Assert.Equal(consumedOperationId, exception.OperationId);
        using var verification = database.Open();
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_operations;"));
    }

    [Fact]
    public void QueueOperationRejectsAnExpiredPreviewWithoutCreatingAnOperation()
    {
        using var database = new HistoricalImportTestDatabase();
        var clock = new MutableHistoricalImportTimeProvider(new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
        var fixture = AdmittedFixture(Candidate("832", ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry, clock);
        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));
        var request = CommitRequest(preview, confirmation, "req_expired_preview_queue", "idem_expired_preview_queue");
        var store = new SqliteHistoricalImportStore(database.Path);
        var command = QueueCommand(
            store,
            request,
            preview,
            fixture.Gateway.ProbeResult.CandidateBatch!.Candidates.Count,
            clock);
        application.Dispose();
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        var exception = Assert.Throws<HistoricalImportException>(() => store.QueueOperation(command));

        Assert.Equal(HistoricalImportErrorCodes.PreviewExpired, exception.Code);
        using var verification = database.Open();
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_operations;"));
    }

    [Fact]
    public void QueueOperationRejectsAnExpiredConfirmationWithoutCreatingAnOperation()
    {
        using var database = new HistoricalImportTestDatabase();
        var clock = new MutableHistoricalImportTimeProvider(new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
        var fixture = AdmittedFixture(Candidate("830", ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry, clock);
        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));
        var request = CommitRequest(preview, confirmation, "req_expired_confirmation_queue", "idem_expired_confirmation_queue");
        var confirmationExpiry = clock.GetUtcNow() + TimeSpan.FromMinutes(1);
        using (var mutation = database.Open())
            Execute(mutation, $"UPDATE historical_import_confirmation_bindings SET expires_at='{confirmationExpiry:O}' WHERE confirmation_id='{confirmation.ConfirmationId}';");
        clock.Advance(TimeSpan.FromMinutes(2));
        var store = new SqliteHistoricalImportStore(database.Path);

        var exception = Assert.Throws<HistoricalImportException>(() => store.QueueOperation(QueueCommand(
            store,
            request,
            preview,
            fixture.Gateway.ProbeResult.CandidateBatch!.Candidates.Count,
            clock)));

        Assert.Equal(HistoricalImportErrorCodes.ConfirmationExpired, exception.Code);
        using var verification = database.Open();
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_operations;"));
    }

    [Fact]
    public void QueueOperationRejectsPurgedPrivatePreviewStateWithoutCreatingAnOperation()
    {
        using var database = new HistoricalImportTestDatabase();
        var clock = new MutableHistoricalImportTimeProvider(new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
        var fixture = AdmittedFixture(Candidate("831", ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry, clock);
        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));
        var request = CommitRequest(preview, confirmation, "req_purged_queue", "idem_purged_queue");
        var store = new SqliteHistoricalImportStore(database.Path);
        var command = QueueCommand(
            store,
            request,
            preview,
            fixture.Gateway.ProbeResult.CandidateBatch!.Candidates.Count,
            clock);
        store.ClearEphemeralPreviewState(preview.PreviewId);

        var exception = Assert.Throws<HistoricalImportException>(() => store.QueueOperation(command));

        Assert.Equal(HistoricalImportErrorCodes.PreviewStale, exception.Code);
        using var verification = database.Open();
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_operations;"));
    }

    [Theory]
    [InlineData("private_selection_json")]
    [InlineData("probe_json")]
    [InlineData("candidate_batch_json")]
    public void QueueOperationRejectsIncompletePrivatePreviewStateWithoutCreatingAnOperation(string missingColumn)
    {
        using var database = new HistoricalImportTestDatabase();
        var clock = new MutableHistoricalImportTimeProvider(new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
        var fixture = AdmittedFixture(Candidate("834", ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry, clock);
        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));
        var request = CommitRequest(preview, confirmation, "req_incomplete_queue", "idem_incomplete_queue");
        var store = new SqliteHistoricalImportStore(database.Path);
        var command = QueueCommand(
            store,
            request,
            preview,
            fixture.Gateway.ProbeResult.CandidateBatch!.Candidates.Count,
            clock);
        using (var mutation = database.Open())
            Execute(mutation, $"UPDATE historical_import_previews SET {missingColumn}=NULL WHERE preview_id='{preview.PreviewId}';");

        var exception = Assert.Throws<HistoricalImportException>(() => store.QueueOperation(command));

        Assert.Equal(HistoricalImportErrorCodes.PreviewStale, exception.Code);
        using var verification = database.Open();
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_operations;"));
    }

    [Fact]
    public void ExpiryReachedImmediatelyBeforeCommitTransactionRejectsWithoutWrites()
    {
        using var database = new HistoricalImportTestDatabase();
        var clock = new MutableHistoricalImportTimeProvider(new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
        var fixture = AdmittedFixture(Candidate("83", ("model_tokens.input_tokens", "42")));
        var checkpoint = new InterleavingHistoricalImportCheckpoint
        {
            BeforeTransaction = () => clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1)),
        };
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry, clock, checkpoint);
        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));

        var exception = Assert.Throws<HistoricalImportException>(() => application.Commit(
            CommitRequest(preview, confirmation, "req_expiry_race", "idem_expiry_race")));

        Assert.Equal(HistoricalImportErrorCodes.PreviewExpired, exception.Code);
        Assert.NotNull(exception.OperationId);
        var status = application.ReadStatus(exception.OperationId!);
        Assert.Equal("rejected", status.State);
        Assert.Equal("not_started", status.TransactionOutcome);
        Assert.Equal(HistoricalImportErrorCodes.PreviewExpired, status.FailureCode);
        AssertNoDomainRows(database);
        AssertNoEphemeralPreviewState(database);
    }

    [Fact]
    public void CheckpointFailure_RollsBackEveryTransactionalWrite()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(
            Candidate("91", ("model_tokens.input_tokens", "42")),
            Candidate("92", ("model_tokens.output_tokens", "17")));
        var checkpoint = new ThrowingHistoricalImportCheckpoint();
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry, checkpoint: checkpoint);
        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));

        var exception = Assert.Throws<HistoricalImportException>(() =>
            application.Commit(CommitRequest(preview, confirmation, "req_rollback", "idem_rollback")));

        Assert.Equal(HistoricalImportErrorCodes.TransactionFailed, exception.Code);
        Assert.NotNull(exception.OperationId);
        var status = application.ReadStatus(exception.OperationId!);
        Assert.Equal("failed", status.State);
        Assert.Equal(["queued", "running", "failed"], status.Lifecycle);
        Assert.Equal("rolled_back", status.TransactionOutcome);
        Assert.Equal(HistoricalImportErrorCodes.TransactionFailed, status.FailureCode);
        Assert.Equal(new(2, 0, 0, 0, 0, 0), status.Counts);
        Assert.False(status.ResultAvailable);
        using var verification = database.Open();
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_operations;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_observations;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_observation_fields;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_conflicts;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_confirmation_bindings WHERE consumed_operation_id IS NOT NULL;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_previews WHERE private_selection_json IS NOT NULL;"));
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("confirmation")]
    [InlineData("preview_purge")]
    public void LateStageCheckpointFailureRollsBackAllTransactionalWrites(string stage)
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(Candidate("921", ("model_tokens.input_tokens", "42")));
        var checkpoint = new StagedThrowingHistoricalImportCheckpoint(stage);
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry, checkpoint: checkpoint);
        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));

        var exception = Assert.Throws<HistoricalImportException>(() => application.Commit(
            CommitRequest(preview, confirmation, $"req_rollback_{stage}", $"idem_rollback_{stage}")));

        Assert.Equal(HistoricalImportErrorCodes.TransactionFailed, exception.Code);
        Assert.NotNull(exception.OperationId);
        var status = application.ReadStatus(exception.OperationId!);
        Assert.Equal("failed", status.State);
        Assert.Equal("rolled_back", status.TransactionOutcome);
        Assert.Equal(HistoricalImportErrorCodes.TransactionFailed, status.FailureCode);
        AssertNoDomainRows(database);
        using (var verification = database.Open())
        {
            Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_operations;"));
            Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_observation_fields;"));
            Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_observation_provenance;"));
            Assert.Equal(0L, Scalar(verification,
                "SELECT COUNT(*) FROM historical_import_confirmation_bindings WHERE consumed_operation_id IS NOT NULL;"));
        }
        AssertNoEphemeralPreviewState(database);
    }

    [Fact]
    public void ConcurrentDatabaseChangeBeforeTransaction_RejectsTheConfirmedDecisionAsStale()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(Candidate("93", ("model_tokens.input_tokens", "42")));
        var checkpoint = new InterleavingHistoricalImportCheckpoint();
        var firstApplication = CreateApplication(database, fixture.Gateway, fixture.Registry, checkpoint: checkpoint);
        var competingApplication = CreateApplication(database, fixture.Gateway, fixture.Registry);
        var firstPreview = firstApplication.CreatePreview(PreviewRequest());
        var firstConfirmation = firstApplication.IssueConfirmation(ConfirmationRequest(firstPreview));
        var competingPreview = competingApplication.CreatePreview(PreviewRequest());
        var competingConfirmation = competingApplication.IssueConfirmation(ConfirmationRequest(competingPreview));
        checkpoint.BeforeTransaction = () => competingApplication.Commit(
            CommitRequest(competingPreview, competingConfirmation, "req_competing", "idem_competing"));

        var exception = Assert.Throws<HistoricalImportException>(() => firstApplication.Commit(
            CommitRequest(firstPreview, firstConfirmation, "req_interleaved", "idem_interleaved")));

        Assert.Equal(HistoricalImportErrorCodes.PreviewStale, exception.Code);
        Assert.NotNull(exception.OperationId);
        var rejected = firstApplication.ReadStatus(exception.OperationId!);
        Assert.Equal("rejected", rejected.State);
        Assert.Equal("not_started", rejected.TransactionOutcome);
        using var verification = database.Open();
        Assert.Equal(2L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_operations;"));
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_observations;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_confirmation_bindings WHERE confirmation_id='" + firstConfirmation.ConfirmationId + "' AND consumed_operation_id IS NOT NULL;"));
    }

    [Theory]
    [InlineData("native_id", true)]
    [InlineData("explicit_link", true)]
    [InlineData("exact_trace_id", false)]
    public void ExactBinding_UsesASeparateTrustedSeamAndNeverEntersTheCandidateBatch(
        string bindingBasis,
        bool traceIdentityIsMissing)
    {
        using var database = new HistoricalImportTestDatabase();
        var candidate = Candidate("94", ("model_tokens.input_tokens", "42"));
        var fixture = AdmittedFixture(candidate);
        fixture.Gateway.ProbeResult = fixture.Gateway.ProbeResult with
        {
            CandidateBindings = [new(candidate.CandidateKey, bindingBasis, "synthetic-target")],
        };
        var application = CreateApplication(
            database,
            fixture.Gateway,
            fixture.Registry,
            exactBindingTargetValidator: target => target == "synthetic-target");

        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));
        var result = application.Commit(CommitRequest(preview, confirmation, "req_binding", "idem_binding"));

        Assert.Equal(bindingBasis, Assert.Single(preview.MergeCandidates).BindingBasis);
        var observation = Assert.Single(result.Observations);
        Assert.Equal("attached_exact", observation.IdentityResolution);
        Assert.Equal(bindingBasis, observation.BindingBasis);
        Assert.DoesNotContain("session_identity", observation.MissingCapabilities);
        Assert.Equal(traceIdentityIsMissing, observation.MissingCapabilities.Contains("trace_identity", StringComparer.Ordinal));
        var listItem = Assert.Single(application.ListObservations().Items);
        Assert.Equal(observation.MissingCapabilities, listItem.MissingCapabilities);
        using var batchJson = JsonDocument.Parse(HistoricalImportJson.Serialize(fixture.Gateway.ProbeResult.CandidateBatch!));
        var serializedCandidate = Assert.Single(batchJson.RootElement.GetProperty("candidates").EnumerateArray());
        Assert.Equal(
            ["candidate_key", "source_record_key", "values", "completeness", "completeness_reasons", "field_provenance"],
            serializedCandidate.EnumerateObject().Select(property => property.Name));
        Assert.False(serializedCandidate.TryGetProperty("exact_binding", out _));
        Assert.Equal(42L, serializedCandidate.GetProperty("values").GetProperty("model_tokens").GetProperty("input_tokens").GetInt64());
    }

    [Fact]
    public void DuplicateCannotAttachPreviouslyUnboundObservation()
    {
        using var database = new HistoricalImportTestDatabase();
        var candidate = Candidate("941", ("model_tokens.input_tokens", "42"));
        var fixture = AdmittedFixture(candidate);
        var application = CreateApplication(
            database,
            fixture.Gateway,
            fixture.Registry,
            exactBindingTargetValidator: target => target == "later-target");
        var first = CommitOnce(application, "req_unbound_first", "idem_unbound_first");
        fixture.Gateway.ProbeResult = fixture.Gateway.ProbeResult with
        {
            CandidateBindings = [new(candidate.CandidateKey, "exact_trace_id", "later-target")],
        };

        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));
        var duplicate = application.Commit(CommitRequest(preview, confirmation, "req_unbound_second", "idem_unbound_second"));

        Assert.Empty(preview.MergeCandidates);
        Assert.Single(duplicate.Duplicates);
        var detail = application.GetObservation(Assert.Single(first.Observations).ObservationId);
        Assert.Equal("distinct_unbound", detail.IdentityResolution);
        Assert.Equal("none", detail.BindingBasis);
        using var verification = database.Open();
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_observations WHERE binding_target_token IS NOT NULL;"));
    }

    [Fact]
    public void DuplicateCannotReplacePreviouslyBoundTarget()
    {
        using var database = new HistoricalImportTestDatabase();
        var candidate = Candidate("942", ("model_tokens.input_tokens", "42"));
        var fixture = AdmittedFixture(candidate);
        fixture.Gateway.ProbeResult = fixture.Gateway.ProbeResult with
        {
            CandidateBindings = [new(candidate.CandidateKey, "native_id", "first-target")],
        };
        var application = CreateApplication(
            database,
            fixture.Gateway,
            fixture.Registry,
            exactBindingTargetValidator: target => target is "first-target" or "second-target");
        CommitOnce(application, "req_bound_first", "idem_bound_first");
        fixture.Gateway.ProbeResult = fixture.Gateway.ProbeResult with
        {
            CandidateBindings = [new(candidate.CandidateKey, "exact_trace_id", "second-target")],
        };

        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));
        var duplicate = application.Commit(CommitRequest(preview, confirmation, "req_bound_second", "idem_bound_second"));

        Assert.Empty(preview.MergeCandidates);
        Assert.Single(duplicate.Duplicates);
        using var verification = database.Open();
        Assert.Equal("first-target", StringScalar(verification, "SELECT binding_target_token FROM historical_import_observations;"));
        Assert.Equal("native_id", StringScalar(verification, "SELECT binding_basis FROM historical_import_observations;"));
    }

    [Fact]
    public void GoldenTestEvidence_MustMatchTheExactAdmissionTuple()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(Candidate("95", ("model_tokens.input_tokens", "42")));
        fixture.Gateway.ProbeResult = fixture.Gateway.ProbeResult with
        {
            AdmissionEvidence = fixture.Gateway.ProbeResult.AdmissionEvidence! with
            {
                GoldenTestId = "different-golden-test-v1",
            },
        };
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry);

        var exception = Assert.Throws<HistoricalImportException>(() => application.CreatePreview(PreviewRequest()));

        Assert.Equal(HistoricalImportErrorCodes.ProfileNotAdmitted, exception.Code);
        AssertNoWorkflowRows(database);
    }

    [Fact]
    public void GoldenTestRevisionDoesNotChangePersistedCandidateIdentity()
    {
        using var database = new HistoricalImportTestDatabase();
        var batch = Batch(Candidate("951", ("model_tokens.input_tokens", "42")));
        var firstProfile = Profile(batch, "golden-synthetic-contract-v1");
        var gateway = new HistoricalImportTestGateway(Probe(batch, evidence: Evidence(firstProfile)));
        var firstApplication = CreateApplication(database, gateway, new HistoricalAdmissionRegistry([firstProfile]));
        var first = CommitOnce(firstApplication, "req_golden_first", "idem_golden_first");
        var revisedProfile = Profile(batch, "golden-synthetic-contract-v2");
        gateway.ProbeResult = Probe(batch, evidence: Evidence(revisedProfile));
        var revisedApplication = CreateApplication(database, gateway, new HistoricalAdmissionRegistry([revisedProfile]));

        var repeated = CommitOnce(revisedApplication, "req_golden_second", "idem_golden_second");

        Assert.Equal(HistoricalImportCount.Available(0), repeated.Counts.NewObservations);
        Assert.Equal(HistoricalImportCount.Available(1), repeated.Counts.Duplicates);
        Assert.Equal(Assert.Single(first.Observations).ObservationId, Assert.Single(repeated.Duplicates).ObservationId);
    }

    [Fact]
    public void ProvenanceOnlyChange_RecordsTheAffectedFieldInsteadOfAnEmptyConflict()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(Candidate("96", ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry);
        CommitOnce(application, "req_provenance_first", "idem_provenance_first");
        var changed = Candidate("96", ("model_tokens.input_tokens", "42"));
        changed = changed with
        {
            FieldProvenance = changed.FieldProvenance
                .Select(value => value with { CaptureContentState = "available" })
                .ToArray(),
        };
        fixture.Gateway.ProbeResult = Probe(
            Batch(changed),
            evidence: fixture.Gateway.ProbeResult.AdmissionEvidence);

        var result = CommitOnce(application, "req_provenance_second", "idem_provenance_second");

        Assert.Equal(["model_tokens.input_tokens"], Assert.Single(result.Conflicts).ConflictFields);
    }

    [Fact]
    public void ReadPreview_ReportsRemainingExpiryWithoutChangingTheBoundDigest()
    {
        using var database = new HistoricalImportTestDatabase();
        var clock = new MutableHistoricalImportTimeProvider(new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
        var fixture = AdmittedFixture(Candidate("97", ("model_tokens.input_tokens", "42")));
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry, clock);
        var created = application.CreatePreview(PreviewRequest());
        clock.Advance(TimeSpan.FromSeconds(61));

        var reread = application.ReadPreview(created.PreviewId);
        var confirmation = application.IssueConfirmation(ConfirmationRequest(reread));

        Assert.Equal(239, reread.ExpiresAfterSeconds);
        Assert.Equal(created.PreviewDigest, reread.PreviewDigest);
        Assert.Equal(reread.PreviewDigest, confirmation.PreviewDigest);
        Assert.Equal(239, confirmation.ExpiresAfterSeconds);
    }

    [Fact]
    public void ObservationCursor_PagesWithoutOverlapAndRejectsUnknownCursor()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = AdmittedFixture(
            Candidate("a1", ("model_tokens.input_tokens", "42")),
            Candidate("a2", ("model_tokens.output_tokens", "17")));
        var application = CreateApplication(database, fixture.Gateway, fixture.Registry);
        CommitOnce(application, "req_cursor", "idem_cursor");

        var first = application.ListObservations(limit: 1);
        var second = application.ListObservations(limit: 1, cursor: first.NextCursor);

        Assert.Single(first.Items);
        Assert.NotNull(first.NextCursor);
        Assert.Single(second.Items);
        Assert.Null(second.NextCursor);
        Assert.NotEqual(first.Items[0].ObservationId, second.Items[0].ObservationId);
        var exception = Assert.Throws<HistoricalImportException>(() => application.ListObservations(limit: 1, cursor: "hoc_00000000000000000000000000000000"));
        Assert.Equal(HistoricalImportErrorCodes.RequestInvalid, exception.Code);
    }

    [Fact]
    public void ObservationCursorRejectsUnknownCursorWhenTheStoreIsEmpty()
    {
        using var database = new HistoricalImportTestDatabase();
        var application = CreateApplication(
            database,
            new HistoricalImportTestGateway(UnsupportedProbe()),
            HistoricalAdmissionRegistry.Empty);

        var exception = Assert.Throws<HistoricalImportException>(() =>
            application.ListObservations(limit: 1, cursor: "hoc_00000000000000000000000000000000"));

        Assert.Equal(HistoricalImportErrorCodes.RequestInvalid, exception.Code);
    }

    private static HistoricalImportApplicationService CreateApplication(
        HistoricalImportTestDatabase database,
        HistoricalImportTestGateway gateway,
        IHistoricalAdmissionRegistry registry,
        TimeProvider? clock = null,
        IHistoricalImportCommitCheckpoint? checkpoint = null,
        Func<string, bool>? exactBindingTargetValidator = null)
    {
        var store = new SqliteHistoricalImportStore(database.Path, checkpoint);
        store.CreateSchema();
        return database.Track(new HistoricalImportApplicationService(
            store,
            gateway,
            registry,
            clock,
            exactBindingTargetValidator));
    }

    private static HistoricalImportResult CommitOnce(
        IHistoricalImportApplication application,
        string requestId,
        string idempotencyKey)
    {
        var preview = application.CreatePreview(PreviewRequest());
        var confirmation = application.IssueConfirmation(ConfirmationRequest(preview));
        return application.Commit(CommitRequest(preview, confirmation, requestId, idempotencyKey));
    }

    private static HistoricalImportPreviewRequest PreviewRequest() => new(
        HistoricalImportContractVersions.Workflow,
        HistoricalImportContractVersions.SourceSelection,
        "github-copilot-cli",
        "selected_root",
        OperatingSystem.IsWindows()
            ? "X:\\synthetic-historical-import\\selected-root"
            : "/synthetic-historical-import/selected-root",
        "9.9.9-synthetic",
        "metadata_only",
        ConsentGranted: true,
        SessionId: "synthetic-session");

    private static HistoricalImportConfirmationRequest ConfirmationRequest(HistoricalImportPreview preview) => new(
        HistoricalImportContractVersions.Workflow,
        HistoricalImportContractVersions.ConfirmationRequest,
        preview.PreviewId,
        preview.PreviewDigest,
        preview.SnapshotVersion,
        "confirm");

    private static HistoricalImportCommitRequest CommitRequest(
        HistoricalImportPreview preview,
        HistoricalImportConfirmation confirmation,
        string requestId,
        string idempotencyKey) => new(
            HistoricalImportContractVersions.Workflow,
            HistoricalImportContractVersions.ImportRequest,
            Token("hir_", requestId),
            Token("hik_", idempotencyKey),
            confirmation.ConfirmationId,
            preview.PreviewId,
            preview.PreviewDigest,
            preview.SnapshotVersion);

    private static HistoricalQueueCommand QueueCommand(
        SqliteHistoricalImportStore store,
        HistoricalImportCommitRequest request,
        HistoricalImportPreview preview,
        int totalCandidateCount,
        TimeProvider timeProvider)
    {
        var storedPreview = store.ReadStoredPreview(preview.PreviewId)
            ?? throw new InvalidOperationException("The test preview was not persisted.");
        var storedConfirmation = store.ReadConfirmation(request.ConfirmationId)
            ?? throw new InvalidOperationException("The test confirmation was not persisted.");
        return new(
            request,
            preview,
            totalCandidateCount,
            IdempotencyHash(request.IdempotencyKey),
            HistoricalImportIdentifiers.DigestBytes(HistoricalImportJson.Serialize(request)),
            storedPreview.ExpiresAt,
            storedConfirmation.ExpiresAt,
            timeProvider);
    }

    private static HistoricalImportTestFixture AdmittedFixture(params HistoricalCandidate[] candidates) =>
        AdmittedFixture(Batch(candidates));

    private static HistoricalImportTestFixture AdmittedFixture(HistoricalCandidateBatch batch)
    {
        var profile = Profile(batch, "golden-synthetic-contract-v1");
        return new(new(Probe(batch, evidence: Evidence(profile))), new([profile]));
    }

    private static HistoricalAdmissionProfile Profile(HistoricalCandidateBatch batch, string goldenTestId) =>
        new(
            batch.ProfileId,
            batch.AdapterId,
            batch.SourceSurface,
            batch.SourceApplicationVersion,
            batch.SourceFormatName,
            batch.SourceFormatVersion,
            batch.SourceFixtureSha256,
            batch.SourceSchemaFingerprint,
            goldenTestId,
            batch.NormalizationVersion,
            ["model_tokens.model", "model_tokens.input_tokens", "model_tokens.output_tokens"]);

    private static HistoricalCandidateBatch Batch(params HistoricalCandidate[] candidates) => new(
        "historical-candidate-batch/v1",
        FixtureOnlyNotSourceSupportEvidence: false,
        "github-copilot-cli-session-state",
        "github-copilot-cli-history-v1",
        "github-copilot-cli",
        "tier_b",
        "9.9.9-synthetic",
        "synthetic-contract-format",
        "1.0.0",
        Sha('a'),
        Fingerprint('b'),
        "historical-summary-v1",
        "partial",
        candidates);

    private static HistoricalCandidate Candidate(string suffix, params (string Field, string Json)[] values)
    {
        var sourceRecordKey = $"hr_{suffix.PadLeft(32, '0')}";
        var provenance = values.Select(value => new HistoricalFieldProvenance(
            value.Field,
            "github-copilot-cli-history-v1",
            "github-copilot-cli",
            "9.9.9-synthetic",
            "synthetic-contract-format",
            "1.0.0",
            Sha('a'),
            Fingerprint('b'),
            sourceRecordKey,
            "not_captured",
            "historical-summary-v1")).ToArray();
        string? model = null;
        long? inputTokens = null;
        long? outputTokens = null;
        long? totalTokens = null;
        long? cacheTokens = null;
        long? reasoningTokens = null;
        bool? retry = null;
        long? attempt = null;
        bool? errorPresent = null;
        JsonElement errorCode = default;
        foreach (var value in values)
        {
            switch (value.Field)
            {
                case "model_tokens.model": model = JsonSerializer.Deserialize<string>(value.Json); break;
                case "model_tokens.input_tokens": inputTokens = JsonSerializer.Deserialize<long>(value.Json); break;
                case "model_tokens.output_tokens": outputTokens = JsonSerializer.Deserialize<long>(value.Json); break;
                case "model_tokens.total_tokens": totalTokens = JsonSerializer.Deserialize<long>(value.Json); break;
                case "model_tokens.cache_tokens": cacheTokens = JsonSerializer.Deserialize<long>(value.Json); break;
                case "model_tokens.reasoning_tokens": reasoningTokens = JsonSerializer.Deserialize<long>(value.Json); break;
                case "retry_attempt.retry": retry = JsonSerializer.Deserialize<bool>(value.Json); break;
                case "retry_attempt.attempt": attempt = JsonSerializer.Deserialize<long>(value.Json); break;
                case "errors.present": errorPresent = JsonSerializer.Deserialize<bool>(value.Json); break;
                case "errors.code": errorCode = JsonDocument.Parse(value.Json).RootElement.Clone(); break;
                default: throw new InvalidOperationException(value.Field);
            }
        }
        var modelTokens = values.Any(value => value.Field.StartsWith("model_tokens.", StringComparison.Ordinal))
            ? new HistoricalModelTokenValues(model, inputTokens, outputTokens, totalTokens, cacheTokens, reasoningTokens)
            : null;
        var retryAttempt = values.Any(value => value.Field.StartsWith("retry_attempt.", StringComparison.Ordinal))
            ? new HistoricalRetryAttemptValues(retry, attempt)
            : null;
        var errors = values.Any(value => value.Field.StartsWith("errors.", StringComparison.Ordinal))
            ? new HistoricalErrorValues(errorPresent, errorCode)
            : null;
        return new(
            $"hc_{suffix.PadLeft(32, '0')}",
            sourceRecordKey,
            new(modelTokens, retryAttempt, errors),
            "partial",
            ["historical_summary_only"],
            provenance);
    }

    private static HistoricalSourceProbe Probe(
        HistoricalCandidateBatch batch,
        IReadOnlyList<HistoricalCandidateBinding>? bindings = null,
        HistoricalAdmissionEvidence? evidence = null) => new(
        new(
            "historical-adapter-result/v1",
            batch.AdapterId,
            batch.ProfileId,
            batch.SourceSurface,
            batch.SourceTier,
            "detected",
            "provided",
            batch.SourceApplicationVersion,
            SupportAuthorized: true,
            "synthetic-contract-format-v1",
            batch.Candidates.Count,
            "source_read_metadata_only",
            RepositorySafe: true,
            []),
        batch,
        evidence,
        bindings ?? [],
        "hsv_1",
        Fingerprint('1'));

    private static HistoricalSourceProbe UnsupportedProbe() => new(
        new(
            "historical-adapter-result/v1",
            "github-copilot-cli-history-v1",
            "github-copilot-cli-session-state",
            "github-copilot-cli",
            "tier_b",
            "detected",
            "provided",
            "9.9.9-synthetic",
            SupportAuthorized: false,
            "none",
            CandidateCount: 0,
            "not_read",
            RepositorySafe: true,
            ["historical_source_format_unsupported"]),
        CandidateBatch: null,
        AdmissionEvidence: null,
        CandidateBindings: [],
        "hsv_1",
        Fingerprint('0'));

    private static HistoricalAdmissionEvidence Evidence(HistoricalAdmissionProfile profile) => new(
        profile.ProfileId,
        profile.AdapterId,
        profile.SourceApplicationVersion,
        profile.SourceFormatName,
        profile.SourceFormatVersion,
        profile.SourceFixtureSha256,
        profile.SourceSchemaFingerprint,
        profile.GoldenTestId);

    private static string Sha(char value) => new(value, 64);

    private static string Fingerprint(char value) => $"sha256:{Sha(value)}";

    private static string Token(string prefix, string seed)
    {
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
        return prefix + digest[..32];
    }

    private static string IdempotencyHash(string idempotencyKey) =>
        HistoricalImportIdentifiers.Digest(Frame("idempotency", idempotencyKey));

    private static string Frame(params string[] values)
    {
        var output = new System.Text.StringBuilder();
        foreach (var value in values)
            output.Append(System.Text.Encoding.UTF8.GetByteCount(value)).Append(':').Append(value);
        return output.ToString();
    }

    private static void AssertNoWorkflowRows(HistoricalImportTestDatabase database)
    {
        using var verification = database.Open();
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_previews;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_confirmation_bindings;"));
        AssertNoCommittedRows(verification);
    }

    private static void AssertNoCommittedRows(SqliteConnection verification)
    {
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_operations;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_observations;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_conflicts;"));
    }

    private static void AssertNoDomainRows(HistoricalImportTestDatabase database)
    {
        using var verification = database.Open();
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_observations;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_observation_fields;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_observation_provenance;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_conflicts;"));
        Assert.Equal(0L, Scalar(verification,
            "SELECT COUNT(*) FROM historical_import_confirmation_bindings WHERE consumed_operation_id IS NOT NULL;"));
    }

    private static void AssertNoEphemeralPreviewState(HistoricalImportTestDatabase database)
    {
        using var verification = database.Open();
        Assert.Equal(0L, Scalar(verification,
            "SELECT COUNT(*) FROM historical_import_previews WHERE private_selection_json IS NOT NULL OR probe_json IS NOT NULL OR candidate_batch_json IS NOT NULL;"));
    }

    private static long EphemeralPreviewCount(HistoricalImportTestDatabase database)
    {
        using var verification = database.Open();
        return Scalar(verification,
            "SELECT COUNT(*) FROM historical_import_previews WHERE private_selection_json IS NOT NULL AND probe_json IS NOT NULL AND candidate_batch_json IS NOT NULL;");
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string StringScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)!;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

internal sealed record HistoricalImportTestFixture(
    HistoricalImportTestGateway Gateway,
    HistoricalAdmissionRegistry Registry);

internal sealed class HistoricalImportTestGateway(HistoricalSourceProbe probe) : IHistoricalSourceGateway
{
    public HistoricalSourceProbe ProbeResult { get; set; } = probe;

    public int ProbeCount { get; private set; }

    public HistoricalSourceProbe Probe(HistoricalSourceSelection selection)
    {
        ProbeCount++;
        return ProbeResult;
    }
}

internal sealed class MutableHistoricalImportTimeProvider(DateTimeOffset initialNow) : TimeProvider
{
    private readonly object gate = new();
    private readonly List<MutableTimer> timers = [];
    private DateTimeOffset now = initialNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate) return now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new MutableTimer(this, callback, state);
        lock (gate)
        {
            timers.Add(timer);
            timer.ChangeUnderLock(dueTime, period);
        }
        return timer;
    }

    public void Advance(TimeSpan duration)
    {
        MutableTimer[] due;
        lock (gate)
        {
            now += duration;
            due = timers.Where(timer => timer.TakeIfDueUnderLock(now)).ToArray();
        }
        foreach (var timer in due) timer.Invoke();
    }

    private sealed class MutableTimer(
        MutableHistoricalImportTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        private DateTimeOffset? dueAt;
        private TimeSpan period = Timeout.InfiniteTimeSpan;
        private bool disposed;

        public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
        {
            lock (owner.gate)
            {
                if (disposed) return false;
                ChangeUnderLock(dueTime, newPeriod);
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner.gate)
            {
                if (disposed) return;
                disposed = true;
                dueAt = null;
                owner.timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        internal void ChangeUnderLock(TimeSpan dueTime, TimeSpan newPeriod)
        {
            period = newPeriod;
            dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
        }

        internal bool TakeIfDueUnderLock(DateTimeOffset current)
        {
            if (disposed || dueAt is null || dueAt > current) return false;
            dueAt = period == Timeout.InfiniteTimeSpan ? null : current + period;
            return true;
        }

        internal void Invoke() => callback(state);
    }
}

internal sealed class ThrowingHistoricalImportCheckpoint : IHistoricalImportCommitCheckpoint
{
    public void AfterCandidate(int candidateOrdinal) => throw new InvalidOperationException("synthetic checkpoint failure");
}

internal sealed class StagedThrowingHistoricalImportCheckpoint(string stage) : IHistoricalImportCommitCheckpoint
{
    public void AfterCandidate(int candidateOrdinal)
    {
    }

    public void AfterOperationPersisted() => ThrowAt("operation");

    public void AfterConfirmationConsumed() => ThrowAt("confirmation");

    public void AfterPreviewPurged() => ThrowAt("preview_purge");

    private void ThrowAt(string currentStage)
    {
        if (stage == currentStage) throw new InvalidOperationException("synthetic checkpoint failure");
    }
}

internal sealed class InterleavingHistoricalImportCheckpoint : IHistoricalImportCommitCheckpoint
{
    public Action? BeforeTransaction { get; set; }

    public void BeforeCommitTransaction()
    {
        var action = BeforeTransaction;
        BeforeTransaction = null;
        action?.Invoke();
    }

    public void AfterCandidate(int candidateOrdinal)
    {
    }
}

internal sealed class RecordingHistoricalImportLifecycleCheckpoint(string databasePath) : IHistoricalImportCommitCheckpoint
{
    private readonly List<HistoricalImportStatus> statuses = [];

    public IReadOnlyList<HistoricalImportStatus> Statuses => statuses;

    public void AfterQueued(string operationId) => statuses.Add(Read(operationId));

    public void AfterRunning(string operationId) => statuses.Add(Read(operationId));

    public void AfterCandidate(int candidateOrdinal)
    {
    }

    private HistoricalImportStatus Read(string operationId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status_json FROM historical_import_operations WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId);
        return HistoricalImportJson.Deserialize<HistoricalImportStatus>((string)command.ExecuteScalar()!);
    }
}

internal sealed class BlockingAfterRunningHistoricalImportCheckpoint : IHistoricalImportCommitCheckpoint, IDisposable
{
    private readonly ManualResetEventSlim running = new(initialState: false);
    private readonly ManualResetEventSlim release = new(initialState: false);

    public void AfterRunning(string operationId)
    {
        running.Set();
        release.Wait(TimeSpan.FromSeconds(10));
    }

    public void AfterCandidate(int candidateOrdinal)
    {
    }

    public bool WaitUntilRunning(TimeSpan timeout) => running.Wait(timeout);

    public void Release() => release.Set();

    public void Dispose()
    {
        release.Set();
        running.Dispose();
        release.Dispose();
    }
}

internal sealed class RejectingAfterRunningHistoricalImportCheckpoint(string code) : IHistoricalImportCommitCheckpoint
{
    public void AfterRunning(string operationId) => throw new HistoricalImportException(code);

    public void AfterCandidate(int candidateOrdinal)
    {
    }
}

internal sealed class BusyRunningAndTerminalHistoricalImportCheckpoint :
    IHistoricalImportCommitCheckpoint,
    IHistoricalImportLifecycleCheckpoint
{
    public void BeforeMarkOperationRunning() =>
        throw new HistoricalImportException(HistoricalImportErrorCodes.StoreBusy);

    public void BeforeTerminalOperation() =>
        throw new HistoricalImportException(HistoricalImportErrorCodes.StoreBusy);

    public void AfterCandidate(int candidateOrdinal)
    {
    }
}

internal sealed class BusyAfterCandidateHistoricalImportCheckpoint : IHistoricalImportCommitCheckpoint
{
    public void AfterCandidate(int candidateOrdinal) =>
        throw new HistoricalImportException(HistoricalImportErrorCodes.StoreBusy);
}
