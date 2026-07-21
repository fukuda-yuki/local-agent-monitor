using CopilotAgentObservability.Doctor;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.ConfigCli.FirstTrace;

internal sealed class FirstTraceOrchestrator
{
    private readonly IReadOnlyDictionary<string, IFirstTraceSourceAdapter> adapters;
    private readonly TimeProvider timeProvider;
    private readonly Func<string, SqliteDoctorApplicationService> applicationFactory;

    public FirstTraceOrchestrator(
        IEnumerable<IFirstTraceSourceAdapter> adapters,
        TimeProvider? timeProvider = null,
        Func<string, SqliteDoctorApplicationService>? applicationFactory = null)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        var registered = new Dictionary<string, IFirstTraceSourceAdapter>(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            if (!registered.TryAdd(adapter.AdapterId, adapter))
            {
                throw new ArgumentException("Duplicate first-trace adapter ID.", nameof(adapters));
            }
        }

        this.adapters = registered;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.applicationFactory = applicationFactory ?? (databasePath =>
            SqliteDoctorApplicationService.Create(
                new SqliteDoctorVerificationStore(databasePath, this.timeProvider)));
    }

    public FirstTraceEnvelope Execute(FirstTraceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return request.Command switch
            {
                "begin" => Begin(request),
                "status" => Status(request),
                "complete" => Complete(request),
                "cancel" => Cancel(request),
                _ => Invalid(request.Command),
            };
        }
        catch (ArgumentException)
        {
            return Invalid(request.Command);
        }
        catch
        {
            return DoctorFailure(
                request.Command,
                adapter: null,
                sourceSurface: null,
                verificationId: request.VerificationId,
                new DoctorResult(
                    DoctorSchemaVersions.ResultV1,
                    Success: false,
                    DoctorResultCode.InternalError,
                    Evaluation: null,
                    Verification: null));
        }
    }

    private FirstTraceEnvelope Begin(FirstTraceRequest request)
    {
        if (request.Adapter is null || !adapters.TryGetValue(request.Adapter, out var adapter)
            || !TryPrepare(adapter, request, requireInteraction: true, out var endpoint))
        {
            return Invalid(request.Command);
        }

        var snapshot = adapter.CollectFacts(request.DatabasePath, endpoint, verification: null) with
        {
            Observations = [],
        };
        var evaluation = applicationFactory(request.DatabasePath).Evaluate(snapshot);
        if (!evaluation.Success)
        {
            return DoctorFailure(
                request.Command,
                adapter,
                evaluation.Evaluation?.SourceSurface ?? adapter.SourceSurface,
                verificationId: null,
                evaluation);
        }

        var canBegin = adapter.CanBeginVerification(evaluation);
        var guidance = adapter.GetGuidance(
            request.Interaction,
            includeSetupPlan: !canBegin);
        if (!canBegin)
        {
            return new FirstTraceEnvelope(
                request.Command,
                Success: false,
                FirstTraceCodes.Blocked,
                adapter.AdapterId,
                adapter.SourceSurface,
                VerificationId: null,
                evaluation,
                EvaluationPreview: null,
                guidance,
                Candidates: [],
                Truncated: false);
        }

        var start = applicationFactory(request.DatabasePath).StartExclusive(
            adapter.SourceSurface,
            adapter.ExpectedSourceAdapter,
            request.ExpiresAt);
        var doctor = StoreResult(start);
        if (start.Code == DoctorResultCode.VerificationActive)
        {
            return new FirstTraceEnvelope(
                request.Command,
                Success: false,
                FirstTraceCodes.ActiveVerificationExists,
                adapter.AdapterId,
                adapter.SourceSurface,
                start.Verification?.VerificationId,
                doctor,
                EvaluationPreview: null,
                guidance,
                Candidates: [],
                Truncated: false);
        }

        if (start.Code != DoctorResultCode.VerificationStarted)
        {
            return DoctorFailure(
                request.Command,
                adapter,
                adapter.SourceSurface,
                start.Verification?.VerificationId,
                doctor);
        }

        return new FirstTraceEnvelope(
            request.Command,
            Success: true,
            FirstTraceCodes.VerificationStarted,
            adapter.AdapterId,
            adapter.SourceSurface,
            start.Verification?.VerificationId,
            doctor,
            EvaluationPreview: null,
            guidance,
            Candidates: [],
            Truncated: false);
    }

    private FirstTraceEnvelope Status(FirstTraceRequest request)
    {
        if (!TryValidateEndpoint(request, out _))
        {
            return Invalid(request.Command);
        }

        var application = applicationFactory(request.DatabasePath);
        var doctor = application.Status(request.VerificationId!);
        if (!doctor.Success || doctor.Verification is not { } verification)
        {
            return DoctorFailure(
                request.Command,
                ResolveAdapter(doctor.Verification),
                doctor.Verification?.ExpectedSourceSurface,
                request.VerificationId,
                doctor);
        }

        var adapter = ResolveAdapter(verification);
        if (adapter is null || !TryPrepare(adapter, request, requireInteraction: false, out var endpoint))
        {
            return Invalid(request.Command);
        }

        var snapshot = adapter.CollectFacts(request.DatabasePath, endpoint, verification) with
        {
            Observations = [],
        };
        var candidatesOutcome = application.ListCandidates(verification.VerificationId);
        if (candidatesOutcome.Code != DoctorResultCode.VerificationActive)
        {
            return DoctorFailure(
                request.Command,
                adapter,
                verification.ExpectedSourceSurface,
                verification.VerificationId,
                doctor with { Success = false, Code = candidatesOutcome.Code });
        }

        var candidates = candidatesOutcome.ResolvedCandidates;
        var preview = application.Evaluate(snapshot with
        {
            Observations = candidates.Select(ToObservation).ToArray(),
        });
        return new FirstTraceEnvelope(
            request.Command,
            Success: true,
            FirstTraceCodes.StatusReported,
            adapter.AdapterId,
            adapter.SourceSurface,
            verification.VerificationId,
            doctor,
            preview,
            Guidance: [],
            candidates,
            Truncated: false);
    }

    private FirstTraceEnvelope Complete(FirstTraceRequest request)
    {
        var application = applicationFactory(request.DatabasePath);
        var status = application.Status(request.VerificationId!);
        if (!status.Success || status.Verification is not { } verification)
        {
            return DoctorFailure(
                request.Command,
                ResolveAdapter(status.Verification),
                status.Verification?.ExpectedSourceSurface,
                request.VerificationId,
                status);
        }

        var adapter = ResolveAdapter(verification);
        if (adapter is null || !TryPrepare(adapter, request, requireInteraction: false, out var endpoint))
        {
            return DoctorFailure(
                request.Command,
                adapter: null,
                verification.ExpectedSourceSurface,
                verification.VerificationId,
                status with { Success = false, Code = DoctorResultCode.ExpectedSourceMismatch });
        }

        var snapshot = adapter.CollectFacts(request.DatabasePath, endpoint, verification) with
        {
            Observations = [],
        };
        var candidatesOutcome = application.ListCandidates(verification.VerificationId);
        if (candidatesOutcome.Code != DoctorResultCode.VerificationActive)
        {
            return DoctorFailure(
                request.Command,
                adapter,
                verification.ExpectedSourceSurface,
                verification.VerificationId,
                status with { Success = false, Code = candidatesOutcome.Code });
        }

        var candidates = candidatesOutcome.ResolvedCandidates;
        var evidenceRefs = request.EvidenceRefs;
        if (evidenceRefs.Count == 0)
        {
            var selection = adapter.SelectEvidence(candidates, UtcNow());
            if (!selection.HasEligibleCandidates)
            {
                var freshSnapshot = snapshot;
                var evaluation = application.Evaluate(freshSnapshot);
                if (evaluation.Code == DoctorResultCode.PartialFactSnapshot)
                {
                    freshSnapshot = adapter.CollectPreWindowFacts(
                        request.DatabasePath,
                        endpoint,
                        snapshot) with
                    {
                        Observations = [],
                    };
                    evaluation = application.Evaluate(freshSnapshot);
                }

                if (!evaluation.Success)
                {
                    return DoctorFailure(
                        request.Command,
                        adapter,
                        adapter.SourceSurface,
                        verification.VerificationId,
                        evaluation);
                }

                return new FirstTraceEnvelope(
                    request.Command,
                    Success: false,
                    FirstTraceCodes.NotReady,
                    adapter.AdapterId,
                    adapter.SourceSurface,
                    verification.VerificationId,
                    evaluation,
                    EvaluationPreview: null,
                    Guidance: [],
                    candidates,
                    Truncated: false);
            }

            if (selection.RequiresExplicitSelection)
            {
                return new FirstTraceEnvelope(
                    request.Command,
                    Success: false,
                    FirstTraceCodes.ExplicitEvidenceSelectionRequired,
                    adapter.AdapterId,
                    adapter.SourceSurface,
                    verification.VerificationId,
                    Doctor: null,
                    EvaluationPreview: null,
                    Guidance: [],
                    candidates,
                    Truncated: false);
            }

            evidenceRefs = selection.EvidenceRefs;
        }

        var selectedSnapshot = adapter.CollectSelectedFacts(
            request.DatabasePath,
            endpoint,
            verification,
            evidenceRefs,
            snapshot) with
        {
            Observations = [],
        };
        var doctor = application.Complete(
            verification.VerificationId,
            request.ExpectedRevision!.Value,
            selectedSnapshot,
            evidenceRefs);
        return CompletionEnvelope(request, adapter, verification, doctor, candidates);
    }

    private FirstTraceEnvelope Cancel(FirstTraceRequest request)
    {
        var application = applicationFactory(request.DatabasePath);
        var doctor = application.Cancel(
            request.VerificationId!,
            request.ExpectedRevision!.Value);
        var adapter = ResolveAdapter(doctor.Verification);
        var sourceSurface = doctor.Verification?.ExpectedSourceSurface ?? adapter?.SourceSurface;
        if (doctor.Code != DoctorResultCode.VerificationCancelled)
        {
            return DoctorFailure(
                request.Command,
                adapter,
                sourceSurface,
                request.VerificationId,
                doctor);
        }

        return new FirstTraceEnvelope(
            request.Command,
            Success: true,
            FirstTraceCodes.Cancelled,
            adapter?.AdapterId,
            sourceSurface,
            doctor.Verification?.VerificationId ?? request.VerificationId,
            doctor,
            EvaluationPreview: null,
            Guidance: [],
            Candidates: [],
            Truncated: false);
    }

    private FirstTraceEnvelope CompletionEnvelope(
        FirstTraceRequest request,
        IFirstTraceSourceAdapter adapter,
        DoctorVerification verification,
        DoctorResult doctor,
        IReadOnlyList<DoctorEvidenceCandidate> candidates)
    {
        if (doctor.Code == DoctorResultCode.VerificationCompleted)
        {
            return new FirstTraceEnvelope(
                request.Command,
                Success: true,
                FirstTraceCodes.Completed,
                adapter.AdapterId,
                adapter.SourceSurface,
                verification.VerificationId,
                doctor,
                EvaluationPreview: null,
                Guidance: [],
                candidates,
                Truncated: false);
        }

        if (doctor.Code == DoctorResultCode.EvaluationCompleted)
        {
            return new FirstTraceEnvelope(
                request.Command,
                Success: false,
                FirstTraceCodes.NotReady,
                adapter.AdapterId,
                adapter.SourceSurface,
                verification.VerificationId,
                doctor,
                EvaluationPreview: null,
                Guidance: [],
                candidates,
                Truncated: false);
        }

        return DoctorFailure(
            request.Command,
            adapter,
            adapter.SourceSurface,
            verification.VerificationId,
            doctor);
    }

    private bool TryPrepare(
        IFirstTraceSourceAdapter adapter,
        FirstTraceRequest request,
        bool requireInteraction,
        out string endpoint)
    {
        endpoint = string.Empty;
        if (!adapter.TryNormalizeEndpoint(request.Endpoint, out endpoint))
        {
            return false;
        }

        return !requireInteraction || adapter.IsValidInteraction(request.Interaction);
    }

    private bool TryValidateEndpoint(FirstTraceRequest request, out string normalizedEndpoint)
    {
        normalizedEndpoint = string.Empty;
        if (request.Endpoint is null)
        {
            return true;
        }

        foreach (var adapter in adapters.Values)
        {
            if (adapter.TryNormalizeEndpoint(request.Endpoint, out normalizedEndpoint))
            {
                return true;
            }
        }

        return false;
    }

    private IFirstTraceSourceAdapter? ResolveAdapter(DoctorVerification? verification) =>
        verification is null
            ? null
            : adapters.Values.FirstOrDefault(adapter =>
                string.Equals(adapter.SourceSurface, verification.ExpectedSourceSurface, StringComparison.Ordinal)
                && string.Equals(adapter.ExpectedSourceAdapter, verification.ExpectedSourceAdapter, StringComparison.Ordinal));

    private FirstTraceEnvelope DoctorFailure(
        string command,
        IFirstTraceSourceAdapter? adapter,
        string? sourceSurface,
        string? verificationId,
        DoctorResult doctor) =>
        new(
            command,
            Success: false,
            FirstTraceCodes.DoctorFailed,
            adapter?.AdapterId,
            sourceSurface,
            verificationId,
            doctor,
            EvaluationPreview: null,
            Guidance: [],
            Candidates: [],
            Truncated: false);

    private FirstTraceEnvelope Invalid(string command) =>
        new(
            command,
            Success: false,
            FirstTraceCodes.InvalidArguments,
            Adapter: null,
            SourceSurface: null,
            VerificationId: null,
            Doctor: null,
            EvaluationPreview: null,
            Guidance: [],
            Candidates: [],
            Truncated: false);

    private DateTimeOffset UtcNow() => timeProvider.GetUtcNow().ToUniversalTime();

    private static DoctorResult StoreResult(DoctorStoreOutcome outcome) =>
        new(
            DoctorSchemaVersions.ResultV1,
            Success: outcome.Code is DoctorResultCode.VerificationStarted or DoctorResultCode.VerificationActive,
            outcome.Code,
            Evaluation: null,
            outcome.Verification);

    private static DoctorObservation ToObservation(DoctorEvidenceCandidate candidate) => new(
        candidate.SourceSurface,
        candidate.SourceAdapter,
        candidate.EvidenceClass,
        candidate.EvidenceKind,
        candidate.EvidenceRef,
        candidate.ObservedAt);
}
