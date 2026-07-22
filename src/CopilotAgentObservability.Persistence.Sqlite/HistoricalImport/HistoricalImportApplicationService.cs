using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;

public sealed class HistoricalImportApplicationService : IHistoricalImportApplication, IDisposable
{
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(5);
    private const RegexOptions SyntaxOptions = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly Regex PreviewId = new(@"^hip_[0-9a-f]{32}\z", SyntaxOptions);
    private static readonly Regex ConfirmationId = new(@"^hic_[0-9a-f]{32}\z", SyntaxOptions);
    private static readonly Regex OperationId = new(@"^hop_[0-9a-f]{32}\z", SyntaxOptions);
    private static readonly Regex ObservationId = new(@"^hob_[0-9a-f]{32}\z", SyntaxOptions);
    private static readonly Regex RequestId = new(@"^hir_[0-9a-f]{32}\z", SyntaxOptions);
    private static readonly Regex IdempotencyKey = new(@"^hik_[0-9a-f]{32}\z", SyntaxOptions);
    private static readonly Regex SnapshotVersion = new(@"^hsv_[1-9][0-9]*\z", SyntaxOptions);
    private static readonly Regex Digest = new(@"^sha256:[0-9a-f]{64}\z", SyntaxOptions);
    private static readonly Regex Cursor = new(@"^hoc_[0-9a-f]{32}\z", SyntaxOptions);
    private static readonly Regex CandidateKey = new(@"^hc_[0-9a-f]{32}\z", SyntaxOptions);
    private static readonly Regex SourceRecordKey = new(@"^hr_[0-9a-f]{32}\z", SyntaxOptions);
    private static readonly Regex Sha256 = new(@"^[0-9a-f]{64}\z", SyntaxOptions);
    private static readonly Regex Token = new(@"^[a-z0-9]+(?:[._-][a-z0-9]+)*\z", SyntaxOptions);
    private static readonly Regex Session = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,255}\z", SyntaxOptions);
    private static readonly IReadOnlySet<string> AdapterDiagnostics = new HashSet<string>(StringComparer.Ordinal)
    {
        "historical_source_format_unsupported",
        "historical_source_malformed",
        "historical_source_reference_required",
    };

    internal static readonly string[] FieldOrder =
    [
        "model_tokens.model",
        "model_tokens.input_tokens",
        "model_tokens.output_tokens",
        "model_tokens.total_tokens",
        "model_tokens.cache_tokens",
        "model_tokens.reasoning_tokens",
        "retry_attempt.retry",
        "retry_attempt.attempt",
        "errors.present",
        "errors.code",
    ];

    internal static readonly IReadOnlyList<string> MissingCapabilities =
    [
        "content",
        "event_identity",
        "lifecycle",
        "session_identity",
        "timing",
        "trace_identity",
    ];

    private readonly SqliteHistoricalImportStore store;
    private readonly IHistoricalSourceGateway gateway;
    private readonly IHistoricalAdmissionRegistry registry;
    private readonly TimeProvider timeProvider;
    private readonly Func<string, bool> exactBindingTargetValidator;
    private readonly ConcurrentDictionary<string, ITimer> previewCleanupTimers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> activePreviewAttempts = new(StringComparer.Ordinal);
    private int disposed;

    public HistoricalImportApplicationService(
        SqliteHistoricalImportStore store,
        IHistoricalSourceGateway gateway,
        IHistoricalAdmissionRegistry registry,
        TimeProvider? timeProvider = null,
        Func<string, bool>? exactBindingTargetValidator = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.exactBindingTargetValidator = exactBindingTargetValidator ?? (_ => false);
        var now = this.timeProvider.GetUtcNow();
        AccessStore(() => store.RecoverAbandonedOperations(now));
        AccessStore(() => store.ClearExpiredEphemeralPreviewState(now));
        foreach (var preview in AccessStore(() => store.ListLiveEphemeralPreviews(now)))
            SchedulePreviewCleanup(preview.PreviewId, preview.ExpiresAt);
    }

    public HistoricalImportPreview CreatePreview(HistoricalImportPreviewRequest request)
    {
        ValidatePreviewRequest(request);
        var selection = request.ToSelection();
        var probe = gateway.Probe(selection);
        ValidateProbe(selection, probe);

        var now = timeProvider.GetUtcNow();
        var previewId = HistoricalImportIdentifiers.New("hip_");
        var selectionId = HistoricalImportIdentifiers.New("hss_");
        HistoricalImportPreview preview;
        HistoricalAdmissionProfile? profile = null;
        string decisionSignature;

        if (!probe.AdapterResult.SupportAuthorized)
        {
            preview = BuildUnavailablePreview(previewId, selectionId, request, probe);
            decisionSignature = "unavailable";
        }
        else
        {
            var batch = probe.CandidateBatch
                ?? throw new HistoricalImportException(HistoricalImportErrorCodes.CandidateInvalid);
            if (batch.FixtureOnlyNotSourceSupportEvidence)
                throw new HistoricalImportException(HistoricalImportErrorCodes.FixtureNotSourceSupportEvidence);
            profile = ResolveAdmission(probe.AdapterResult, batch, probe.AdmissionEvidence);
            ValidateCandidateBatch(probe.AdapterResult, batch, probe.CandidateBindings, profile);
            var decisions = ClassifyCandidates(profile, batch);
            decisionSignature = decisions.Signature;
            preview = BuildEligiblePreview(previewId, selectionId, request, probe, batch, decisions);
        }

        preview = preview with
        {
            PreviewDigest = ComputePreviewDigest(preview, probe, probe.CandidateBatch, profile, decisionSignature),
        };
        var expiresAt = now + PreviewLifetime;
        AccessStore(() => store.SavePreview(new(preview, selection, probe, probe.CandidateBatch, expiresAt, now)));
        if (preview.CommitAllowed) SchedulePreviewCleanup(preview.PreviewId, expiresAt);
        return preview;
    }

    public HistoricalImportPreview ReadPreview(string previewId)
    {
        RequireMatch(previewId, PreviewId);
        var stored = ReadLivePreview(previewId);
        var remaining = Math.Clamp((int)Math.Ceiling((stored.ExpiresAt - timeProvider.GetUtcNow()).TotalSeconds), 0, 300);
        return stored.Preview with { ExpiresAfterSeconds = remaining };
    }

    public HistoricalImportConfirmation IssueConfirmation(HistoricalImportConfirmationRequest request)
    {
        if (request is null
            || request.ContractVersion != HistoricalImportContractVersions.Workflow
            || request.SchemaVersion != HistoricalImportContractVersions.ConfirmationRequest
            || !PreviewId.IsMatch(request.PreviewId ?? string.Empty)
            || !Digest.IsMatch(request.PreviewDigest ?? string.Empty)
            || !SnapshotVersion.IsMatch(request.SnapshotVersion ?? string.Empty)
            || request.Decision != "confirm")
            throw Invalid();

        var stored = ReadLivePreview(request.PreviewId!);
        EnsurePreviewBinding(stored.Preview, request.PreviewDigest!, request.SnapshotVersion!);
        if (!stored.Preview.CommitAllowed)
            throw new HistoricalImportException(HistoricalImportErrorCodes.NoEligibleCandidates);
        if (stored.Selection is null)
            throw new HistoricalImportException(HistoricalImportErrorCodes.PreviewStale);

        var now = timeProvider.GetUtcNow();
        var seconds = Math.Clamp((int)Math.Ceiling((stored.ExpiresAt - now).TotalSeconds), 1, 300);
        var confirmation = new HistoricalImportConfirmation(
            HistoricalImportContractVersions.Workflow,
            HistoricalImportContractVersions.Confirmation,
            HistoricalImportIdentifiers.New("hic_"),
            stored.Preview.PreviewId,
            stored.Preview.PreviewDigest,
            stored.Preview.SnapshotVersion,
            "eligible",
            "confirm",
            seconds);
        AccessStore(() => store.SaveConfirmation(confirmation, stored.ExpiresAt, now));
        return confirmation;
    }

    public HistoricalImportResult Commit(HistoricalImportCommitRequest request)
    {
        ValidateCommitRequest(request!);
        var validRequest = request!;
        var keyHash = HistoricalImportIdentifiers.Digest(Framed("idempotency", validRequest.IdempotencyKey));
        var requestDigest = HistoricalImportIdentifiers.DigestBytes(HistoricalImportJson.Serialize(validRequest));
        var replay = AccessStore(() => store.ResolveIdempotentOperation(keyHash, requestDigest));
        if (replay is not null) return replay;

        var stored = ReadLivePreview(validRequest.PreviewId);
        EnsurePreviewBinding(stored.Preview, validRequest.PreviewDigest, validRequest.SnapshotVersion);
        if (!stored.Preview.CommitAllowed)
            throw new HistoricalImportException(HistoricalImportErrorCodes.NoEligibleCandidates);
        var confirmation = AccessStore(() => store.ReadConfirmation(validRequest.ConfirmationId))
            ?? throw new HistoricalImportException(HistoricalImportErrorCodes.ConfirmationInvalid);
        if (confirmation.ExpiresAt <= timeProvider.GetUtcNow())
            throw new HistoricalImportException(HistoricalImportErrorCodes.ConfirmationExpired);
        if (confirmation.ConsumedOperationId is not null)
            throw new HistoricalImportException(HistoricalImportErrorCodes.ConfirmationConsumed, confirmation.ConsumedOperationId);
        EnsureConfirmationBinding(confirmation.Confirmation, validRequest);
        if (stored.Selection is null || stored.Probe is null || stored.Batch is null)
            throw new HistoricalImportException(HistoricalImportErrorCodes.PreviewStale);

        EnterPreviewAttempt(stored.Preview.PreviewId);
        try
        {
            var queued = AccessStore(() => store.QueueOperation(new(
                validRequest,
                stored.Preview,
                stored.Batch.Candidates.Count,
                keyHash,
                requestDigest,
                stored.ExpiresAt,
                confirmation.ExpiresAt,
                timeProvider)));
            if (queued.ReplayResult is not null) return queued.ReplayResult;
            var operationId = queued.OperationId;
            try
            {
                AccessStore(() => store.MarkOperationRunning(operationId));
                var reprobe = gateway.Probe(stored.Selection);
                ValidateProbe(stored.Selection, reprobe);
                if (reprobe.SnapshotVersion != stored.Probe.SnapshotVersion
                    || reprobe.SnapshotDigest != stored.Probe.SnapshotDigest)
                    throw new HistoricalImportException(HistoricalImportErrorCodes.SourceChanged);
                if (!ProbePayloadEqual(stored.Probe, reprobe))
                    throw new HistoricalImportException(HistoricalImportErrorCodes.PreviewStale);
                var profile = ResolveAdmission(reprobe.AdapterResult, stored.Batch, reprobe.AdmissionEvidence);
                ValidateCandidateBatch(reprobe.AdapterResult, stored.Batch, reprobe.CandidateBindings, profile);

                var decisions = ClassifyCandidates(profile, stored.Batch);
                var currentDigest = ComputePreviewDigest(
                    stored.Preview with { PreviewDigest = EmptyDigest },
                    stored.Probe,
                    stored.Batch,
                    profile,
                    decisions.Signature);
                if (currentDigest != stored.Preview.PreviewDigest)
                    throw new HistoricalImportException(HistoricalImportErrorCodes.PreviewStale);

                var outcome = AccessStore(() => store.Commit(new(
                        operationId,
                        validRequest,
                        stored.Preview,
                        stored.Batch,
                        reprobe.CandidateBindings,
                        profile,
                        decisions.Signature,
                        keyHash,
                        requestDigest,
                        stored.ExpiresAt,
                        confirmation.ExpiresAt,
                        timeProvider)));
                CancelPreviewCleanup(stored.Preview.PreviewId);
                return outcome.Result;
            }
            catch (HistoricalImportDomainTransactionException)
            {
                if (PersistFailedOperation(operationId))
                    PurgeEphemeralPreviewState(stored.Preview.PreviewId, terminalStatePersisted: true);
                throw new HistoricalImportException(HistoricalImportErrorCodes.TransactionFailed, operationId);
            }
            catch (HistoricalImportException exception)
            {
                var rejectionCode = IsAcceptedPreDomainRejection(exception.Code)
                    ? exception.Code
                    : HistoricalImportErrorCodes.StoreUnavailable;
                PersistRejectedOperation(operationId, rejectionCode);
                PurgeEphemeralPreviewState(stored.Preview.PreviewId, terminalStatePersisted: true);
                throw new HistoricalImportException(rejectionCode, operationId);
            }
            catch
            {
                PersistRejectedOperation(operationId, HistoricalImportErrorCodes.StoreUnavailable);
                PurgeEphemeralPreviewState(stored.Preview.PreviewId, terminalStatePersisted: true);
                throw new HistoricalImportException(
                    HistoricalImportErrorCodes.StoreUnavailable,
                    operationId);
            }
        }
        finally
        {
            ExitPreviewAttempt(stored.Preview.PreviewId);
        }
    }

    public HistoricalImportStatus ReadStatus(string operationId)
    {
        RequireMatch(operationId, OperationId);
        return AccessStore(() => store.ReadStatusOrNull(operationId))
            ?? throw new HistoricalImportException(HistoricalImportErrorCodes.OperationNotFound);
    }

    public HistoricalImportResult ReadResult(string operationId)
    {
        RequireMatch(operationId, OperationId);
        var result = AccessStore(() => store.ReadResultOrNull(operationId));
        if (result is not null) return result;
        if (AccessStore(() => store.ReadStatusOrNull(operationId)) is not null)
            throw new HistoricalImportException(HistoricalImportErrorCodes.ResultNotAvailable, operationId);
        throw new HistoricalImportException(HistoricalImportErrorCodes.OperationNotFound);
    }

    public HistoricalImportHistory ListHistory(int limit = 100)
    {
        ValidateLimit(limit);
        return AccessStore(() => store.ListHistoryRows(limit));
    }

    public HistoricalObservationList ListObservations(int limit = 100, string? cursor = null)
    {
        ValidateLimit(limit);
        if (cursor is not null && !Cursor.IsMatch(cursor)) throw Invalid();
        return AccessStore(() => store.ListObservationRows(limit, cursor));
    }

    public HistoricalObservationDetail GetObservation(string observationId)
    {
        RequireMatch(observationId, ObservationId);
        return AccessStore(() => store.ReadObservationDetail(observationId))
            ?? throw new HistoricalImportException(HistoricalImportErrorCodes.ObservationNotFound);
    }

    internal static string CandidateIdentity(
        HistoricalAdmissionProfile profile,
        HistoricalCandidateBatch batch,
        HistoricalCandidate candidate) => HistoricalImportIdentifiers.Digest(Framed(
            "candidate-identity-v1",
            profile.ProfileId,
            profile.AdapterId,
            profile.SourceApplicationVersion,
            profile.SourceFormatName,
            profile.SourceFormatVersion,
            profile.SourceFixtureSha256,
            profile.SourceSchemaFingerprint,
            batch.NormalizationVersion,
            candidate.SourceRecordKey));

    internal static string CandidateFingerprint(HistoricalCandidateBatch batch, HistoricalCandidate candidate) =>
        HistoricalImportIdentifiers.Digest(Framed(
            "candidate-fingerprint-v1",
            batch.ProfileId,
            batch.AdapterId,
            batch.SourceFixtureSha256,
            batch.SourceSchemaFingerprint,
            batch.NormalizationVersion,
            HistoricalImportJson.SerializeString(candidate.Values),
            candidate.Completeness,
            HistoricalImportJson.SerializeString(candidate.CompletenessReasons),
            HistoricalImportJson.SerializeString(candidate.FieldProvenance)));

    internal static HistoricalImportObservationResult ObservationResult(
        string observationId,
        HistoricalCandidateBinding? binding) => new(
        observationId,
        binding is null ? "distinct_unbound" : "attached_exact",
        binding?.Basis ?? "none",
        "partial",
        ["historical_summary_only"],
        MissingCapabilitiesFor(binding?.Basis),
        "not_captured");

    internal static IReadOnlyList<string> MissingCapabilitiesFor(string? bindingBasis) => bindingBasis switch
    {
        "native_id" or "explicit_link" => MissingCapabilities.Where(value => value != "session_identity").ToArray(),
        "exact_trace_id" => MissingCapabilities.Where(value => value is not ("session_identity" or "trace_identity")).ToArray(),
        _ => MissingCapabilities,
    };

    private const string EmptyDigest = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    private HistoricalStoredPreview ReadLivePreview(string previewId)
    {
        var stored = AccessStore(() => store.ReadStoredPreview(previewId))
            ?? throw new HistoricalImportException(HistoricalImportErrorCodes.PreviewNotFound);
        if (stored.ExpiresAt <= timeProvider.GetUtcNow())
        {
            PurgeEphemeralPreviewState(previewId);
            throw new HistoricalImportException(HistoricalImportErrorCodes.PreviewExpired);
        }
        return stored;
    }

    private void PurgeEphemeralPreviewState(string previewId, bool terminalStatePersisted = false)
    {
        var recoveryComplete = terminalStatePersisted;
        if (!recoveryComplete && !activePreviewAttempts.ContainsKey(previewId))
        {
            try
            {
                store.RecoverAbandonedOperation(previewId, timeProvider.GetUtcNow());
                recoveryComplete = true;
            }
            catch
            {
                // Retry recovery with the same cleanup timer after a transient store failure.
            }
        }

        try
        {
            store.ClearEphemeralPreviewState(previewId);
        }
        catch
        {
            ReschedulePreviewCleanup(previewId);
            // The fixed workflow failure must not be replaced by cleanup details.
            return;
        }

        if (recoveryComplete) CancelPreviewCleanup(previewId);
        else ReschedulePreviewCleanup(previewId);
    }

    private void PersistRejectedOperation(string operationId, string failureCode)
    {
        try
        {
            AccessStore(() => store.RejectOperation(operationId, failureCode, timeProvider.GetUtcNow()));
        }
        catch
        {
            throw new HistoricalImportException(HistoricalImportErrorCodes.TransactionFailed, operationId);
        }
    }

    private bool PersistFailedOperation(string operationId)
    {
        try
        {
            AccessStore(() => store.FailOperation(operationId, timeProvider.GetUtcNow()));
            return true;
        }
        catch
        {
            // The fixed transaction failure remains the only safe public detail.
            return false;
        }
    }

    private static bool IsDeterministicRejection(string code) => code is
        HistoricalImportErrorCodes.RequestInvalid or
        HistoricalImportErrorCodes.PreviewNotFound or
        HistoricalImportErrorCodes.PreviewExpired or
        HistoricalImportErrorCodes.PreviewStale or
        HistoricalImportErrorCodes.SourceChanged or
        HistoricalImportErrorCodes.NoEligibleCandidates or
        HistoricalImportErrorCodes.ConfirmationInvalid or
        HistoricalImportErrorCodes.ConfirmationExpired or
        HistoricalImportErrorCodes.ConfirmationConsumed or
        HistoricalImportErrorCodes.ProfileNotAdmitted or
        HistoricalImportErrorCodes.CandidateInvalid or
        HistoricalImportErrorCodes.FixtureNotSourceSupportEvidence;

    private static bool IsAcceptedPreDomainRejection(string code) =>
        IsDeterministicRejection(code)
        || code is HistoricalImportErrorCodes.StoreBusy or HistoricalImportErrorCodes.StoreUnavailable;

    private void SchedulePreviewCleanup(string previewId, DateTimeOffset expiresAt)
    {
        var dueTime = expiresAt - timeProvider.GetUtcNow();
        if (dueTime < TimeSpan.Zero) dueTime = TimeSpan.Zero;
        var timer = timeProvider.CreateTimer(
            static state =>
            {
                var (application, id) = ((HistoricalImportApplicationService, string))state!;
                application.OnPreviewCleanupTimer(id);
            },
            (this, previewId),
            dueTime,
            Timeout.InfiniteTimeSpan);
        if (!previewCleanupTimers.TryAdd(previewId, timer)) timer.Dispose();
    }

    private void OnPreviewCleanupTimer(string previewId)
        => PurgeEphemeralPreviewState(previewId);

    private void EnterPreviewAttempt(string previewId) =>
        activePreviewAttempts.AddOrUpdate(previewId, 1, static (_, count) => checked(count + 1));

    private void ExitPreviewAttempt(string previewId)
    {
        var remaining = activePreviewAttempts.AddOrUpdate(
            previewId,
            0,
            static (_, count) => Math.Max(0, count - 1));
        if (remaining == 0)
            ((ICollection<KeyValuePair<string, int>>)activePreviewAttempts)
                .Remove(new(previewId, 0));
    }

    private void CancelPreviewCleanup(string previewId)
    {
        if (previewCleanupTimers.TryRemove(previewId, out var timer)) timer.Dispose();
    }

    private void ReschedulePreviewCleanup(string previewId)
    {
        if (previewCleanupTimers.TryGetValue(previewId, out var timer))
            timer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var previewId in previewCleanupTimers.Keys) CancelPreviewCleanup(previewId);
    }

    private static T AccessStore<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (HistoricalImportException)
        {
            throw;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new HistoricalImportException(HistoricalImportErrorCodes.StoreBusy);
        }
        catch (SqliteException)
        {
            throw new HistoricalImportException(HistoricalImportErrorCodes.StoreUnavailable);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            throw new HistoricalImportException(HistoricalImportErrorCodes.StoreUnavailable);
        }
    }

    private static void AccessStore(Action operation) => AccessStore(() =>
    {
        operation();
        return true;
    });

    private HistoricalCandidateDecisions ClassifyCandidates(HistoricalAdmissionProfile profile, HistoricalCandidateBatch batch) =>
        AccessStore(() => store.ClassifyCandidates(profile, batch));

    private static HistoricalImportPreview BuildUnavailablePreview(
        string previewId,
        string selectionId,
        HistoricalImportPreviewRequest request,
        HistoricalSourceProbe probe)
    {
        var adapter = probe.AdapterResult;
        var state = adapter.Diagnostics.Contains("historical_source_malformed", StringComparer.Ordinal)
            ? "malformed"
            : adapter.Diagnostics.Contains("historical_source_format_unsupported", StringComparer.Ordinal)
                ? "unsupported"
                : "unavailable";
        return new(
            HistoricalImportContractVersions.Workflow,
            HistoricalImportContractVersions.Preview,
            previewId,
            EmptyDigest,
            probe.SnapshotVersion,
            300,
            selectionId,
            "historical",
            request.SourceSurface,
            "unsupported",
            "tier_b",
            adapter.ProfileId,
            adapter.AdapterId,
            state,
            adapter.Diagnostics,
            "production",
            request.SourceApplicationVersion,
            new(null, null),
            "metadata_only",
            new("unavailable", null, null),
            new(
                HistoricalImportCount.Unavailable,
                HistoricalImportCount.Available(0),
                HistoricalImportCount.Unavailable,
                HistoricalImportCount.Unavailable,
                HistoricalImportCount.Unavailable,
                HistoricalImportCount.Unavailable,
                HistoricalImportCount.Unavailable,
                HistoricalImportCount.Unavailable,
                HistoricalImportCount.Unavailable,
                HistoricalImportCount.Unavailable,
                HistoricalImportCount.Unavailable),
            "not_read",
            "none",
            [],
            [],
            [],
            new("not_applicable", 0, null, false),
            adapter.Diagnostics.Select(diagnostic => new HistoricalImportExclusion(diagnostic, 1)).ToArray(),
            false,
            HistoricalImportErrorCodes.NoEligibleCandidates);
    }

    private static HistoricalImportPreview BuildEligiblePreview(
        string previewId,
        string selectionId,
        HistoricalImportPreviewRequest request,
        HistoricalSourceProbe probe,
        HistoricalCandidateBatch batch,
        HistoricalCandidateDecisions decisions)
    {
        var count = batch.Candidates.Count;
        var merges = probe.CandidateBindings
            .Where(binding => decisions.NewCandidateKeys.Contains(binding.CandidateKey))
            .Select(binding => new HistoricalImportMergeCandidate(HistoricalImportIdentifiers.New("hmc_"), binding.Basis))
            .ToArray();
        return new(
            HistoricalImportContractVersions.Workflow,
            HistoricalImportContractVersions.Preview,
            previewId,
            EmptyDigest,
            probe.SnapshotVersion,
            300,
            selectionId,
            "historical",
            request.SourceSurface,
            "historical",
            "tier_b",
            batch.ProfileId,
            batch.AdapterId,
            "available",
            [],
            "production",
            batch.SourceApplicationVersion,
            new(batch.SourceFormatName, batch.SourceFormatVersion),
            "metadata_only",
            new("unavailable", null, null),
            new(
                HistoricalImportCount.Available(count),
                HistoricalImportCount.Available(count),
                HistoricalImportCount.Available(0),
                HistoricalImportCount.Available(0),
                HistoricalImportCount.Available(decisions.DuplicateCount),
                HistoricalImportCount.Available(decisions.ConflictCount),
                HistoricalImportCount.Available(decisions.NewCount),
                HistoricalImportCount.Unavailable,
                HistoricalImportCount.Unavailable,
                HistoricalImportCount.Available(merges.Length),
                HistoricalImportCount.Available(0)),
            "source_read_metadata_only",
            "partial",
            ["historical_summary_only"],
            MissingCapabilitiesForBatch(batch, probe.CandidateBindings),
            merges,
            new("not_applicable", 0, null, false),
            [],
            true,
            null);
    }

    private static string ComputePreviewDigest(
        HistoricalImportPreview preview,
        HistoricalSourceProbe probe,
        HistoricalCandidateBatch? batch,
        HistoricalAdmissionProfile? profile,
        string decisionSignature) => HistoricalImportIdentifiers.Digest(Framed(
            "copilot-agent-observability/historical-import-preview/v1",
            preview.ContractVersion,
            preview.SchemaVersion,
            preview.SourceSelectionId,
            HistoricalImportIdentifiers.DigestBytes(HistoricalImportJson.Serialize(probe.AdapterResult)),
            batch is null
                ? "candidate-batch:absent"
                : HistoricalImportIdentifiers.DigestBytes(HistoricalImportJson.Serialize(batch)),
            probe.SnapshotVersion,
            probe.SnapshotDigest,
            probe.AdmissionEvidence is null
                ? "admission-evidence:absent"
                : HistoricalImportIdentifiers.DigestBytes(HistoricalImportJson.Serialize(probe.AdmissionEvidence)),
            profile is null
                ? "admission-profile:absent"
                : HistoricalImportIdentifiers.DigestBytes(HistoricalImportJson.Serialize(profile)),
            batch?.FixtureOnlyNotSourceSupportEvidence.ToString() ?? "fixture-marker:absent",
            HistoricalImportIdentifiers.DigestBytes(HistoricalImportJson.Serialize(probe.CandidateBindings)),
            decisionSignature,
            HistoricalImportJson.SerializeString(preview.RetentionImpact),
            HistoricalImportJson.SerializeString(preview with
            {
                PreviewDigest = EmptyDigest,
                ExpiresAfterSeconds = 0,
            })));

    private static void ValidatePreviewRequest(HistoricalImportPreviewRequest request)
    {
        if (request is null
            || request.ContractVersion != HistoricalImportContractVersions.Workflow
            || request.SchemaVersion != HistoricalImportContractVersions.SourceSelection
            || request.RequestedCapture != "metadata_only"
            || !request.ConsentGranted
            || !HistoricalSourcePathPolicy.IsCanonicalNativeAbsolute(request.ExactReference)
            || !HistoricalSourceMetadataTokenPolicy.IsValid(request.SourceApplicationVersion))
            throw Invalid();

        if (request.SourceSurface == "github-copilot-cli")
        {
            if (request.ReferenceKind != "selected_root"
                || string.IsNullOrEmpty(request.SessionId)
                || !Session.IsMatch(request.SessionId)) throw Invalid();
        }
        else if (request.SourceSurface == "claude-code")
        {
            if (request.ReferenceKind is not ("official_hook" or "explicit_user_selection")
                || request.SessionId is not null) throw Invalid();
        }
        else throw Invalid();
    }

    private static void ValidateProbe(HistoricalSourceSelection selection, HistoricalSourceProbe probe)
    {
        if (probe is null
            || probe.AdapterResult is null
            || probe.CandidateBindings is null
            || !SnapshotVersion.IsMatch(probe.SnapshotVersion ?? string.Empty)
            || !Digest.IsMatch(probe.SnapshotDigest ?? string.Empty))
            throw new HistoricalImportException(HistoricalImportErrorCodes.CandidateInvalid);
        var adapter = probe.AdapterResult;
        var currentUnsupported = selection.SourceSurface switch
        {
            "github-copilot-cli" => (Adapter: "github-copilot-cli-history-v1", Profile: "github-copilot-cli-session-state"),
            "claude-code" => (Adapter: "claude-code-history-v1", Profile: "claude-code-transcript"),
            _ => throw Invalid(),
        };
        if (adapter.ContractVersion != "historical-adapter-result/v1"
            || !IsToken(adapter.AdapterId)
            || !IsToken(adapter.ProfileId)
            || adapter.SourceSurface != selection.SourceSurface
            || adapter.SourceTier != "tier_b"
            || adapter.DetectionState is not ("detected" or "not_evaluated")
            || adapter.SourceReferenceState is not ("provided" or "missing")
            || adapter.SourceApplicationVersion is not null
                && !HistoricalImportSemanticVersionPolicy.IsValid(adapter.SourceApplicationVersion)
            || !adapter.RepositorySafe
            || adapter.CandidateCount < 0
            || adapter.Diagnostics is null
            || adapter.Diagnostics.Count != adapter.Diagnostics.Distinct(StringComparer.Ordinal).Count()
            || adapter.Diagnostics.Any(value => !AdapterDiagnostics.Contains(value)))
            throw new HistoricalImportException(HistoricalImportErrorCodes.CandidateInvalid);

        if (!adapter.SupportAuthorized)
        {
            if (adapter.AdapterId != currentUnsupported.Adapter
                || adapter.ProfileId != currentUnsupported.Profile
                || adapter.CandidateCount != 0
                || probe.CandidateBatch is not null
                || probe.AdmissionEvidence is not null
                || probe.CandidateBindings.Count != 0
                || adapter.SourceFormatProfile != "none"
                || adapter.ContentRisk != "not_read"
                || adapter.Diagnostics.Count != 1
                || !IsValidUnsupportedDiagnostic(selection, adapter))
                throw new HistoricalImportException(HistoricalImportErrorCodes.CandidateInvalid);
        }
        else if (adapter.CandidateCount <= 0
            || probe.CandidateBatch is null
            || adapter.DetectionState != "detected"
            || adapter.SourceReferenceState != "provided"
            || string.IsNullOrEmpty(adapter.SourceApplicationVersion)
            || adapter.SourceApplicationVersion != selection.SourceApplicationVersion
            || !HistoricalImportSemanticVersionPolicy.IsValid(adapter.SourceApplicationVersion)
            || !IsToken(adapter.SourceFormatProfile)
            || adapter.ContentRisk != "source_read_metadata_only"
            || adapter.Diagnostics.Count != 0)
        {
            throw new HistoricalImportException(HistoricalImportErrorCodes.CandidateInvalid);
        }
    }

    private static bool IsValidUnsupportedDiagnostic(
        HistoricalSourceSelection selection,
        HistoricalAdapterResult adapter) => adapter.Diagnostics[0] switch
    {
        "historical_source_reference_required" =>
            adapter.DetectionState == "not_evaluated"
            && adapter.SourceReferenceState == "missing"
            && adapter.SourceApplicationVersion is null,
        "historical_source_format_unsupported" =>
            adapter.DetectionState == "detected"
            && adapter.SourceReferenceState == "provided"
            && (adapter.SourceApplicationVersion is null
                || adapter.SourceApplicationVersion == selection.SourceApplicationVersion),
        "historical_source_malformed" =>
            adapter.SourceReferenceState == "provided"
            && (adapter.DetectionState == "not_evaluated"
                ? adapter.SourceApplicationVersion is null
                : adapter.SourceApplicationVersion is null
                    || adapter.SourceApplicationVersion == selection.SourceApplicationVersion),
        _ => false,
    };

    private void ValidateCandidateBatch(
        HistoricalAdapterResult adapter,
        HistoricalCandidateBatch batch,
        IReadOnlyList<HistoricalCandidateBinding> bindings,
        HistoricalAdmissionProfile profile)
    {
        if (batch.ContractVersion != "historical-candidate-batch/v1"
            || batch.FixtureOnlyNotSourceSupportEvidence
            || batch.Candidates is null
            || batch.Candidates.Count == 0
            || batch.Candidates.Count > 1000
            || batch.Candidates.Count != adapter.CandidateCount
            || adapter.AdapterId != batch.AdapterId
            || adapter.ProfileId != batch.ProfileId
            || adapter.SourceSurface != batch.SourceSurface
            || adapter.SourceApplicationVersion != batch.SourceApplicationVersion
            || batch.ProfileId != profile.ProfileId
            || batch.AdapterId != profile.AdapterId
            || batch.SourceSurface != profile.SourceSurface
            || batch.SourceTier != "tier_b"
            || batch.SourceApplicationVersion != profile.SourceApplicationVersion
            || batch.SourceFormatName != profile.SourceFormatName
            || batch.SourceFormatVersion != profile.SourceFormatVersion
            || batch.SourceFixtureSha256 != profile.SourceFixtureSha256
            || batch.SourceSchemaFingerprint != profile.SourceSchemaFingerprint
            || batch.NormalizationVersion != profile.NormalizationVersion
            || batch.CompletenessCeiling != "partial"
            || !IsToken(profile.ProfileId)
            || !IsToken(profile.AdapterId)
            || !IsToken(profile.SourceFormatName)
            || !IsToken(profile.GoldenTestId)
            || !IsToken(profile.NormalizationVersion)
            || !HistoricalImportSemanticVersionPolicy.IsValid(profile.SourceApplicationVersion)
            || !HistoricalImportSemanticVersionPolicy.IsValid(profile.SourceFormatVersion)
            || !Sha256.IsMatch(profile.SourceFixtureSha256 ?? string.Empty)
            || !Digest.IsMatch(profile.SourceSchemaFingerprint ?? string.Empty)
            || profile.ActiveFieldAllowlist is null
            || profile.ActiveFieldAllowlist.Count is < 1 or > 10
            || profile.ActiveFieldAllowlist.Distinct(StringComparer.Ordinal).Count() != profile.ActiveFieldAllowlist.Count
            || !IsPolicyOrdered(profile.ActiveFieldAllowlist)
            || !IsToken(batch.ProfileId)
            || !IsToken(batch.AdapterId)
            || !IsToken(batch.SourceFormatName)
            || !IsToken(batch.NormalizationVersion)
            || !HistoricalImportSemanticVersionPolicy.IsValid(batch.SourceApplicationVersion)
            || !HistoricalImportSemanticVersionPolicy.IsValid(batch.SourceFormatVersion)
            || !Sha256.IsMatch(batch.SourceFixtureSha256 ?? string.Empty)
            || !Digest.IsMatch(batch.SourceSchemaFingerprint ?? string.Empty))
            throw new HistoricalImportException(HistoricalImportErrorCodes.CandidateInvalid);

        var candidateKeys = new HashSet<string>(StringComparer.Ordinal);
        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in batch.Candidates)
        {
            if (candidate is null
                || !CandidateKey.IsMatch(candidate.CandidateKey ?? string.Empty)
                || !SourceRecordKey.IsMatch(candidate.SourceRecordKey ?? string.Empty)
                || !candidateKeys.Add(candidate.CandidateKey!)
                || !sourceKeys.Add(candidate.SourceRecordKey!)
                || candidate.Values is null
                || HasEmptyValueFamily(candidate.Values)
                || candidate.Completeness != "partial"
                || candidate.CompletenessReasons is null
                || !candidate.CompletenessReasons.SequenceEqual(["historical_summary_only"], StringComparer.Ordinal)
                || candidate.FieldProvenance is null)
                throw new HistoricalImportException(HistoricalImportErrorCodes.CandidateInvalid);
            var fields = FlattenCandidate(candidate);
            if (fields.Count is < 1 or > 10 || fields.Count != candidate.FieldProvenance.Count)
                throw new HistoricalImportException(HistoricalImportErrorCodes.CandidateInvalid);
            var priorOrdinal = -1;
            for (var index = 0; index < fields.Count; index++)
            {
                var field = fields[index];
                var provenance = candidate.FieldProvenance[index];
                if (field is null || provenance is null)
                    throw new HistoricalImportException(HistoricalImportErrorCodes.CandidateInvalid);
                var ordinal = Array.IndexOf(FieldOrder, field.Field);
                if (ordinal <= priorOrdinal
                    || !profile.ActiveFieldAllowlist.Contains(field.Field, StringComparer.Ordinal)
                    || provenance.Field != field.Field
                    || provenance.AdapterId != batch.AdapterId
                    || provenance.SourceSurface != batch.SourceSurface
                    || provenance.SourceApplicationVersion != batch.SourceApplicationVersion
                    || provenance.SourceFormatName != batch.SourceFormatName
                    || provenance.SourceFormatVersion != batch.SourceFormatVersion
                    || provenance.SourceFixtureSha256 != batch.SourceFixtureSha256
                    || provenance.SourceSchemaFingerprint != batch.SourceSchemaFingerprint
                    || provenance.SourceRecordKey != candidate.SourceRecordKey
                    || provenance.CaptureContentState is not ("available" or "not_captured" or "redacted" or "unsupported")
                    || provenance.NormalizationVersion != batch.NormalizationVersion
                    || !IsValidFieldJson(field.Field, field.CanonicalJson))
                    throw new HistoricalImportException(HistoricalImportErrorCodes.CandidateInvalid);
                priorOrdinal = ordinal;
            }
        }

        var candidateOrdinals = batch.Candidates
            .Select((candidate, index) => (candidate.CandidateKey, index))
            .ToDictionary(value => value.CandidateKey, value => value.index, StringComparer.Ordinal);
        var priorBindingOrdinal = -1;
        var boundCandidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            if (binding is null
                || !candidateOrdinals.TryGetValue(binding.CandidateKey ?? string.Empty, out var ordinal)
                || ordinal <= priorBindingOrdinal
                || !boundCandidates.Add(binding.CandidateKey!))
                throw new HistoricalImportException(HistoricalImportErrorCodes.CandidateInvalid);
            ValidateBinding(binding);
            priorBindingOrdinal = ordinal;
        }
    }

    private static bool HasEmptyValueFamily(HistoricalCandidateValues values) =>
        values.ModelTokens is { } modelTokens
            && modelTokens.Model is null
            && modelTokens.InputTokens is null
            && modelTokens.OutputTokens is null
            && modelTokens.TotalTokens is null
            && modelTokens.CacheTokens is null
            && modelTokens.ReasoningTokens is null
        || values.RetryAttempt is { } retryAttempt
            && retryAttempt.Retry is null
            && retryAttempt.Attempt is null
        || values.Errors is { } errors
            && errors.Present is null
            && errors.Code.ValueKind == JsonValueKind.Undefined;

    internal static IReadOnlyList<HistoricalFieldValue> FlattenCandidate(HistoricalCandidate candidate)
    {
        var fields = new List<HistoricalFieldValue>(10);
        var modelTokens = candidate.Values.ModelTokens;
        if (modelTokens is not null)
        {
            if (modelTokens.Model is not null)
                fields.Add(new("model_tokens.model", JsonSerializer.Serialize(modelTokens.Model)));
            if (modelTokens.InputTokens is not null)
                fields.Add(new("model_tokens.input_tokens", JsonSerializer.Serialize(modelTokens.InputTokens.Value)));
            if (modelTokens.OutputTokens is not null)
                fields.Add(new("model_tokens.output_tokens", JsonSerializer.Serialize(modelTokens.OutputTokens.Value)));
            if (modelTokens.TotalTokens is not null)
                fields.Add(new("model_tokens.total_tokens", JsonSerializer.Serialize(modelTokens.TotalTokens.Value)));
            if (modelTokens.CacheTokens is not null)
                fields.Add(new("model_tokens.cache_tokens", JsonSerializer.Serialize(modelTokens.CacheTokens.Value)));
            if (modelTokens.ReasoningTokens is not null)
                fields.Add(new("model_tokens.reasoning_tokens", JsonSerializer.Serialize(modelTokens.ReasoningTokens.Value)));
        }

        var retryAttempt = candidate.Values.RetryAttempt;
        if (retryAttempt is not null)
        {
            if (retryAttempt.Retry is not null)
                fields.Add(new("retry_attempt.retry", JsonSerializer.Serialize(retryAttempt.Retry.Value)));
            if (retryAttempt.Attempt is not null)
                fields.Add(new("retry_attempt.attempt", JsonSerializer.Serialize(retryAttempt.Attempt.Value)));
        }

        var errors = candidate.Values.Errors;
        if (errors is not null)
        {
            if (errors.Present is not null)
                fields.Add(new("errors.present", JsonSerializer.Serialize(errors.Present.Value)));
            if (errors.Code.ValueKind != JsonValueKind.Undefined)
                fields.Add(new("errors.code", errors.Code.GetRawText()));
        }

        return fields;
    }

    private static bool IsValidFieldJson(string field, string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 4096) return false;
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 4 });
            var element = document.RootElement;
            if (element.GetRawText() != value || JsonSerializer.Serialize(element) != value) return false;
            return field switch
            {
                "model_tokens.model" => element.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(element.GetString()),
                "model_tokens.input_tokens" or
                "model_tokens.output_tokens" or
                "model_tokens.total_tokens" or
                "model_tokens.cache_tokens" or
                "model_tokens.reasoning_tokens" or
                "retry_attempt.attempt" => element.ValueKind == JsonValueKind.Number
                    && element.TryGetInt64(out var number)
                    && number >= 0,
                "retry_attempt.retry" or "errors.present" => element.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "errors.code" => element.ValueKind == JsonValueKind.Null
                    || element.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(element.GetString()),
                _ => false,
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void ValidateBinding(HistoricalCandidateBinding binding)
    {
        if (binding.Basis is not ("native_id" or "explicit_link" or "exact_trace_id")
            || string.IsNullOrEmpty(binding.TargetToken)
            || binding.TargetToken.Length > 128
            || !Token.IsMatch(binding.TargetToken))
            throw new HistoricalImportException(HistoricalImportErrorCodes.CandidateInvalid);
        try
        {
            if (exactBindingTargetValidator(binding.TargetToken)) return;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
        }
        throw new HistoricalImportException(HistoricalImportErrorCodes.CandidateInvalid);
    }

    private static bool IsPolicyOrdered(IReadOnlyList<string> fields)
    {
        var prior = -1;
        foreach (var field in fields)
        {
            var current = Array.IndexOf(FieldOrder, field);
            if (current <= prior) return false;
            prior = current;
        }
        return true;
    }

    private static bool IsToken(string? value) => value is { Length: > 0 and <= 128 } && Token.IsMatch(value);

    private HistoricalAdmissionProfile ResolveAdmission(
        HistoricalAdapterResult adapter,
        HistoricalCandidateBatch batch,
        HistoricalAdmissionEvidence? evidence)
    {
        try
        {
            if (evidence is not null
                && registry.TryResolve(adapter, batch, evidence, out var profile)
                && profile is not null)
                return profile;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
        }
        throw new HistoricalImportException(HistoricalImportErrorCodes.ProfileNotAdmitted);
    }

    private static IReadOnlyList<string> MissingCapabilitiesForBatch(
        HistoricalCandidateBatch batch,
        IReadOnlyList<HistoricalCandidateBinding> bindings)
    {
        var bindingsByCandidate = bindings.ToDictionary(binding => binding.CandidateKey, StringComparer.Ordinal);
        var missing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in batch.Candidates)
        {
            bindingsByCandidate.TryGetValue(candidate.CandidateKey, out var binding);
            missing.UnionWith(MissingCapabilitiesFor(binding?.Basis));
        }
        return MissingCapabilities.Where(missing.Contains).ToArray();
    }

    private static bool ProbePayloadEqual(HistoricalSourceProbe left, HistoricalSourceProbe right) =>
        HistoricalImportJson.SerializeString(left.AdapterResult) == HistoricalImportJson.SerializeString(right.AdapterResult)
        && HistoricalImportJson.SerializeString(left.CandidateBatch) == HistoricalImportJson.SerializeString(right.CandidateBatch)
        && HistoricalImportJson.SerializeString(left.AdmissionEvidence) == HistoricalImportJson.SerializeString(right.AdmissionEvidence)
        && HistoricalImportJson.SerializeString(left.CandidateBindings) == HistoricalImportJson.SerializeString(right.CandidateBindings);

    private static void EnsurePreviewBinding(HistoricalImportPreview preview, string digest, string snapshot)
    {
        if (preview.PreviewDigest != digest || preview.SnapshotVersion != snapshot)
            throw new HistoricalImportException(HistoricalImportErrorCodes.PreviewStale);
    }

    private static void EnsureConfirmationBinding(HistoricalImportConfirmation confirmation, HistoricalImportCommitRequest request)
    {
        if (confirmation.PreviewId != request.PreviewId
            || confirmation.PreviewDigest != request.PreviewDigest
            || confirmation.SnapshotVersion != request.SnapshotVersion
            || confirmation.Decision != "confirm"
            || confirmation.Eligibility != "eligible")
            throw new HistoricalImportException(HistoricalImportErrorCodes.ConfirmationInvalid);
    }

    private static void ValidateCommitRequest(HistoricalImportCommitRequest request)
    {
        if (request is null
            || request.ContractVersion != HistoricalImportContractVersions.Workflow
            || request.SchemaVersion != HistoricalImportContractVersions.ImportRequest
            || !RequestId.IsMatch(request.RequestId ?? string.Empty)
            || !IdempotencyKey.IsMatch(request.IdempotencyKey ?? string.Empty)
            || !ConfirmationId.IsMatch(request.ConfirmationId ?? string.Empty)
            || !PreviewId.IsMatch(request.PreviewId ?? string.Empty)
            || !Digest.IsMatch(request.PreviewDigest ?? string.Empty)
            || !SnapshotVersion.IsMatch(request.SnapshotVersion ?? string.Empty))
            throw Invalid();
    }

    private static void RequireMatch(string value, Regex regex)
    {
        if (value is null || !regex.IsMatch(value)) throw Invalid();
    }

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > 100) throw Invalid();
    }

    private static HistoricalImportException Invalid() => new(HistoricalImportErrorCodes.RequestInvalid);

    private static string Framed(params string[] values)
    {
        var output = new StringBuilder();
        foreach (var value in values)
        {
            output.Append(Encoding.UTF8.GetByteCount(value)).Append(':').Append(value);
        }
        return output.ToString();
    }
}
