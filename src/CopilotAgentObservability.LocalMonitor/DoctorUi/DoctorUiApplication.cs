using CopilotAgentObservability.ConfigCli.FirstTrace;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.Doctor;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor;

internal sealed class DoctorUiApplication : IDoctorUiApplication
{
    private readonly string databasePath;
    private readonly string endpoint;
    private readonly IReadOnlyList<IFirstTraceSourceAdapter> adapters;
    private readonly ISetupPlatform platform;
    private readonly FirstTraceOrchestrator orchestrator;
    private readonly SqliteFirstTraceNavigationStore navigationStore;

    public DoctorUiApplication(string databasePath, string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        this.databasePath = databasePath;
        this.endpoint = endpoint;
        platform = new SystemSetupPlatform();
        adapters = FirstTraceSourceRegistry.CreateAdapters(platform);
        orchestrator = new FirstTraceOrchestrator(adapters);
        navigationStore = new SqliteFirstTraceNavigationStore(databasePath);
    }

    public IReadOnlyDictionary<string, DoctorUiDetectionState> DetectSources()
    {
        return FirstTraceSourceRegistry.DetectSources(platform).ToDictionary(
            pair => pair.Key,
            pair => pair.Value switch
            {
                FirstTraceSourceDetectionState.Detected => DoctorUiDetectionState.Detected,
                FirstTraceSourceDetectionState.NotDetected => DoctorUiDetectionState.NotDetected,
                FirstTraceSourceDetectionState.Unavailable => DoctorUiDetectionState.Unavailable,
                _ => throw new ArgumentOutOfRangeException(nameof(pair)),
            },
            StringComparer.Ordinal);
    }

    public DoctorUiApplicationResult Begin(string sourceId, string? interaction, DateTimeOffset? expiresAt) =>
        Execute(new(
            "begin",
            databasePath,
            sourceId,
            VerificationId: null,
            ExpectedRevision: null,
            endpoint,
            interaction,
            expiresAt,
            EvidenceRefs: []));

    public DoctorUiApplicationResult Status(string verificationId) =>
        Execute(new(
            "status",
            databasePath,
            Adapter: null,
            verificationId,
            ExpectedRevision: null,
            endpoint,
            Interaction: null,
            ExpiresAt: null,
            EvidenceRefs: []));

    public DoctorUiApplicationResult Complete(
        string verificationId,
        int expectedRevision,
        IReadOnlyList<string> acceptedEvidenceRefs) =>
        Execute(new(
            "complete",
            databasePath,
            Adapter: null,
            verificationId,
            expectedRevision,
            endpoint,
            Interaction: null,
            ExpiresAt: null,
            acceptedEvidenceRefs));

    public DoctorUiApplicationResult Cancel(string verificationId, int expectedRevision) =>
        Execute(new(
            "cancel",
            databasePath,
            Adapter: null,
            verificationId,
            expectedRevision,
            Endpoint: null,
            Interaction: null,
            ExpiresAt: null,
            EvidenceRefs: []));

    private DoctorUiApplicationResult Execute(FirstTraceRequest request)
    {
        var envelope = orchestrator.Execute(request);
        var references = EvidenceReferences(envelope.Doctor)
            .Distinct(StringComparer.Ordinal)
            .Take(DoctorValidation.MaximumEvidenceCandidates)
            .ToArray();
        var identities = envelope.VerificationId is null
            ? []
            : navigationStore.List(envelope.VerificationId, references)
                .Take(64)
                .Select(ToIdentity)
                .ToArray();
        return new(StatusCode(envelope), FirstTraceJson.Serialize(envelope), identities);
    }

    private static IEnumerable<string> EvidenceReferences(DoctorResult? result) =>
        result?.Evaluation?.States.SelectMany(state => state.EvidenceRefs) ?? [];

    private static DoctorUiNavigationIdentity ToIdentity(FirstTraceNavigationTarget target) => new(
        target.EvidenceRef,
        target.TargetKind switch
        {
            FirstTraceNavigationTargetKind.Trace => DoctorUiNavigationTargetKind.Trace,
            FirstTraceNavigationTargetKind.Session => DoctorUiNavigationTargetKind.Session,
            FirstTraceNavigationTargetKind.SourceDiagnostic => DoctorUiNavigationTargetKind.SourceDiagnostic,
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        },
        target.TargetId);

    private static int StatusCode(FirstTraceEnvelope envelope)
    {
        if (envelope.Success)
        {
            return envelope.Code == FirstTraceCodes.VerificationStarted ? StatusCodes.Status201Created : StatusCodes.Status200OK;
        }
        if (envelope.Code == FirstTraceCodes.InvalidArguments)
        {
            return StatusCodes.Status400BadRequest;
        }
        if (envelope.Code is FirstTraceCodes.Blocked
            or FirstTraceCodes.ActiveVerificationExists
            or FirstTraceCodes.NotReady
            or FirstTraceCodes.ExplicitEvidenceSelectionRequired)
        {
            return StatusCodes.Status409Conflict;
        }
        return envelope.Doctor?.Code switch
        {
            DoctorResultCode.InvalidArguments
                or DoctorResultCode.InvalidInput
                or DoctorResultCode.UnsupportedSchemaVersion => StatusCodes.Status400BadRequest,
            DoctorResultCode.VerificationNotFound => StatusCodes.Status404NotFound,
            DoctorResultCode.VerificationStale
                or DoctorResultCode.ExpectedSourceMismatch
                or DoctorResultCode.EvidenceNotFound
                or DoctorResultCode.EvaluationCompleted
                or DoctorResultCode.PartialFactSnapshot => StatusCodes.Status409Conflict,
            DoctorResultCode.VerificationExpired
                or DoctorResultCode.VerificationAlreadyCancelled
                or DoctorResultCode.VerificationAlreadyCompleted
                or DoctorResultCode.EvidenceExpired => StatusCodes.Status410Gone,
            DoctorResultCode.DoctorStoreBusy
                or DoctorResultCode.DoctorStoreUnavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };
    }
}
