using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.Doctor;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.ConfigCli.FirstTrace.GitHubCopilot;

internal sealed class GitHubCopilotFirstTraceAdapter : IFirstTraceSourceAdapter
{
    private const string SetupAdapter = "github-copilot";
    private const string DoctorAdapter = "github-copilot-doctor";
    private readonly Func<SetupOptions, SetupCommandResult> setupDispatch;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly TimeProvider timeProvider;
    private readonly GitHubCopilotDoctorStaticFactCollector? staticFactCollector;
    private readonly string interaction;
    private readonly string target;

    public GitHubCopilotFirstTraceAdapter(
        string sourceId,
        Func<SetupOptions, SetupCommandResult> setupDispatch,
        Func<DateTimeOffset> utcNow,
        ISetupPlatform? platform = null)
    {
        ArgumentNullException.ThrowIfNull(setupDispatch);
        ArgumentNullException.ThrowIfNull(utcNow);
        (target, interaction) = sourceId switch
        {
            "github-copilot-vscode" => ("vscode", "vscode-chat"),
            "github-copilot-cli" => ("cli", "cli"),
            "github-copilot-app-sdk" => ("app-sdk", "app-sdk"),
            _ => throw new ArgumentException("Unsupported GitHub Copilot first-trace source.", nameof(sourceId)),
        };
        AdapterId = sourceId;
        SourceSurface = sourceId;
        this.setupDispatch = setupDispatch;
        this.utcNow = utcNow;
        timeProvider = new DelegateTimeProvider(utcNow);
        staticFactCollector = platform is null ? null : new GitHubCopilotDoctorStaticFactCollector(platform);
    }

    public string AdapterId { get; }

    public string SourceSurface { get; }

    public string ExpectedSourceAdapter => DoctorAdapter;

    public bool TryNormalizeEndpoint(string? endpoint, out string normalizedEndpoint)
    {
        var parsed = SetupOptions.Parse(
        [
            "setup", "plan", "--adapter", SetupAdapter, "--target", target,
            "--endpoint", endpoint ?? "http://127.0.0.1:4320",
        ]);
        if (parsed.Options?.Endpoint is not { } value)
        {
            normalizedEndpoint = string.Empty;
            return false;
        }

        normalizedEndpoint = value;
        return true;
    }

    public bool IsValidInteraction(string? value) => value is null
        || string.Equals(value, interaction, StringComparison.Ordinal);

    public DoctorFactSnapshot CollectFacts(
        string databasePath,
        string normalizedEndpoint,
        DoctorVerification? verification)
    {
        if (verification is not null)
        {
            GitHubCopilotDoctorCandidateObserver.Observe(
                databasePath,
                timeProvider,
                verification,
                target);
        }

        var setup = setupDispatch(new SetupOptions(
            SetupCommand.Status,
            SetupAdapter,
            Target: null,
            Endpoint: null,
            IncludeContentCapture: false,
            ChangeSetId: null));
        if (!setup.Success)
        {
            return UnknownFacts(verification);
        }

        try
        {
            var mapped = GitHubCopilotDoctorFactMapper.FromSetup(
                setup,
                target,
                utcNow().ToUniversalTime()) with
            {
                VerificationId = verification?.VerificationId,
            };
            return staticFactCollector?.Collect(target, normalizedEndpoint, setup, mapped) ?? mapped;
        }
        catch (ArgumentException)
        {
            return UnknownFacts(verification);
        }
    }

    public IReadOnlyList<FirstTraceGuidance> GetGuidance(
        string? interaction,
        bool includeSetupPlan) =>
    [
        new(
            "common",
            target == "app-sdk"
                ? "Use the caller-managed sample, run one bounded interaction, and flush telemetry before the process exits."
                : "After setup changes, start a new source process, run one bounded interaction, and close it afterwards.",
            includeSetupPlan
                ? $"setup plan --adapter {SetupAdapter} --target {target}"
                : null),
        new(
            this.interaction,
            target switch
            {
                "vscode" => "Open Copilot Chat in a new VS Code window, send one short test prompt, and close the window.",
                "cli" => "Open a new terminal, run GitHub Copilot CLI for one short test prompt, and exit.",
                _ => "Run one bounded caller-managed App/SDK interaction and flush telemetry before exit.",
            },
            target == "cli" ? "copilot" : null),
    ];

    public FirstTraceEvidenceSelection SelectEvidence(
        IReadOnlyList<DoctorEvidenceCandidate> candidates,
        DateTimeOffset now)
    {
        var eligible = candidates.Any(candidate =>
            candidate.EvidenceClass == DoctorEvidenceClass.RealSource
            && string.Equals(candidate.SourceSurface, SourceSurface, StringComparison.Ordinal)
            && string.Equals(candidate.SourceAdapter, DoctorAdapter, StringComparison.Ordinal)
            && candidate.ExpiresAt > now);
        return eligible
            ? FirstTraceEvidenceSelection.Explicit
            : FirstTraceEvidenceSelection.NoEligibleCandidates;
    }

    public bool CanBeginVerification(DoctorResult evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        if (evaluation.Code == DoctorResultCode.PartialFactSnapshot ||
            evaluation.Evaluation is null ||
            evaluation.Evaluation.MissingFactFamilies.Any(family => family is not (
                "source_version_and_schema_diagnostics" or
                "last_ingest" or
                "raw_persistence" or
                "projection" or
                "exact_session_binding" or
                "completeness_and_content")))
        {
            return false;
        }

        var blockers = evaluation.Evaluation?.States.Where(state =>
            state.Severity == DoctorSeverity.Error ||
            state.StateCode == DoctorStateCode.AgentRestartRequired).ToArray() ?? [];
        return blockers.All(state => state.StateCode == DoctorStateCode.AgentRestartRequired);
    }

    public DoctorFactSnapshot CollectPreWindowFacts(
        string databasePath,
        string normalizedEndpoint,
        DoctorFactSnapshot collectedFacts) =>
        collectedFacts with { VerificationId = null };

    public DoctorFactSnapshot CollectSelectedFacts(
        string databasePath,
        string normalizedEndpoint,
        DoctorVerification verification,
        IReadOnlyList<string> evidenceRefs,
        DoctorFactSnapshot collectedFacts)
    {
        if (!TryResolveRawRecordId(databasePath, verification, evidenceRefs, out var rawRecordId))
        {
            return collectedFacts;
        }

        var runtime = GitHubCopilotDoctorEvidenceAdapter.Observe(
            databasePath,
            timeProvider,
            new GitHubCopilotDoctorEvidenceSelection(
                verification.VerificationId,
                target,
                rawRecordId,
                NativeSession: null));
        if (runtime.ObservationResult.Code != DoctorResultCode.VerificationActive ||
            evidenceRefs.Any(reference => !runtime.EvidenceRefs.Contains(reference, StringComparer.Ordinal)))
        {
            return collectedFacts;
        }

        return collectedFacts with
        {
            ObservedAt = runtime.Snapshot.ObservedAt,
            SourceVersionAndSchemaDiagnostics = runtime.Snapshot.SourceVersionAndSchemaDiagnostics,
            LastIngest = runtime.Snapshot.LastIngest,
            RawPersistence = runtime.Snapshot.RawPersistence,
            Projection = runtime.Snapshot.Projection,
            ExactSessionBinding = runtime.Snapshot.ExactSessionBinding,
            CompletenessAndContent = runtime.Snapshot.CompletenessAndContent,
            RestartOrNewProcess = new(RestartRequirement.NotRequired),
        };
    }

    private static bool TryResolveRawRecordId(
        string databasePath,
        DoctorVerification verification,
        IReadOnlyList<string> evidenceRefs,
        out long rawRecordId)
    {
        rawRecordId = 0;
        if (evidenceRefs.Count == 0)
        {
            return false;
        }

        try
        {
            var navigation = new SqliteFirstTraceNavigationStore(databasePath)
                .List(verification.VerificationId, evidenceRefs);
            ISourceCompatibilityStore compatibility = new SqliteSourceCompatibilityStore(databasePath);
            var resolved = new List<long>(evidenceRefs.Count);
            foreach (var evidenceRef in evidenceRefs)
            {
                var diagnosticTargets = navigation.Where(target =>
                    string.Equals(target.EvidenceRef, evidenceRef, StringComparison.Ordinal) &&
                    target.TargetKind == FirstTraceNavigationTargetKind.SourceDiagnostic).ToArray();
                if (diagnosticTargets.Length != 1 ||
                    compatibility.GetByObservationId(diagnosticTargets[0].TargetId)?.RawRecordId is not { } exactRawRecordId)
                {
                    return false;
                }
                resolved.Add(exactRawRecordId);
            }
            if (resolved.Distinct().Count() != 1)
            {
                return false;
            }
            rawRecordId = resolved[0];
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return false;
        }
    }

    private DoctorFactSnapshot UnknownFacts(DoctorVerification? verification) => new(
        DoctorSchemaVersions.FactsV1,
        SourceSurface,
        DoctorAdapter,
        utcNow().ToUniversalTime(),
        verification?.VerificationId,
        [],
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null);

    private sealed class DelegateTimeProvider(Func<DateTimeOffset> getUtcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => getUtcNow().ToUniversalTime();
    }
}
