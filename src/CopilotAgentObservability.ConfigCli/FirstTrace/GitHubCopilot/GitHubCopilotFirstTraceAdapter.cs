using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.ConfigCli.FirstTrace.GitHubCopilot;

internal sealed class GitHubCopilotFirstTraceAdapter : IFirstTraceSourceAdapter
{
    private const string SetupAdapter = "github-copilot";
    private const string DoctorAdapter = "github-copilot-doctor";
    private readonly Func<SetupOptions, SetupCommandResult> setupDispatch;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly string interaction;
    private readonly string target;

    public GitHubCopilotFirstTraceAdapter(
        string sourceId,
        Func<SetupOptions, SetupCommandResult> setupDispatch,
        Func<DateTimeOffset> utcNow)
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
            return GitHubCopilotDoctorFactMapper.FromSetup(
                setup,
                target,
                utcNow().ToUniversalTime()) with
            {
                VerificationId = verification?.VerificationId,
            };
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
}
