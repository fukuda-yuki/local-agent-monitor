using CopilotAgentObservability.ConfigCli.FirstTrace;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode.AgentSdk;
using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.ConfigCli.FirstTrace.ClaudeCode;

internal sealed class ClaudeCodeFirstTraceAdapter : IFirstTraceSourceAdapter
{
    private const string AdapterToken = "claude-code";
    private const string SourceSurfaceToken = "claude-code";
    private const string ExpectedSourceAdapterToken = "claude-code-otel";
    private const string CommonInteraction = "common";
    private const string InteractiveCliInteraction = "interactive-cli";
    private const string PrintInteraction = "print";
    private const string AgentSdkInteraction = "agent-sdk";

    private readonly ClaudeDoctorFactCollector collector;
    private readonly ISetupClock clock;

    public ClaudeCodeFirstTraceAdapter(
        ISetupPlatform platform,
        ISetupHttpProbe? httpProbe = null,
        ISetupClock? clock = null,
        string? invocationDirectory = null,
        string? managedFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(platform);
        this.clock = clock ?? platform.Clock;
        collector = new ClaudeDoctorFactCollector(
            platform,
            httpProbe,
            this.clock,
            invocationDirectory,
            managedFilePath);
    }

    public string AdapterId => AdapterToken;

    public string SourceSurface => SourceSurfaceToken;

    public string ExpectedSourceAdapter => ExpectedSourceAdapterToken;

    public bool TryNormalizeEndpoint(string? endpoint, out string normalizedEndpoint)
    {
        var result = SetupOptions.Parse(
        [
            "setup", "plan", "--adapter", AdapterToken, "--target", "cli",
            "--endpoint", endpoint ?? "http://127.0.0.1:4320",
        ]);
        if (result.Options is null)
        {
            normalizedEndpoint = string.Empty;
            return false;
        }

        normalizedEndpoint = result.Options.Endpoint!;
        return true;
    }

    public bool IsValidInteraction(string? interaction) => interaction is
        null or InteractiveCliInteraction or PrintInteraction or AgentSdkInteraction;

    public DoctorFactSnapshot CollectFacts(
        string databasePath,
        string normalizedEndpoint,
        DoctorVerification? verification)
    {
        var inputs = collector.Collect(databasePath, normalizedEndpoint, verification);
        return ClaudeDoctorFactMapper.Map(
            inputs,
            clock.UtcNow.ToUniversalTime(),
            verification?.VerificationId,
            SourceSurfaceToken,
            ExpectedSourceAdapterToken);
    }

    public IReadOnlyList<FirstTraceGuidance> GetGuidance(
        string? interaction,
        bool includeSetupPlan)
    {
        var selected = interaction is null
            ? new[] { InteractiveCliInteraction, PrintInteraction, AgentSdkInteraction }
            : [interaction];
        var guidance = new List<FirstTraceGuidance>
        {
            new(
                CommonInteraction,
                "A changed setup apply requires a new Claude process. " +
                "Environment-derived settings require a new shell. Run one bounded test interaction and close Claude afterwards.",
                includeSetupPlan ? "setup plan --adapter claude-code --target cli" : null),
        };

        foreach (var item in selected)
        {
            guidance.Add(item switch
            {
                InteractiveCliInteraction => new FirstTraceGuidance(
                    InteractiveCliInteraction,
                    "Open a new terminal, run Claude in the target project, send one short test prompt, and exit.",
                    "claude"),
                PrintInteraction => new FirstTraceGuidance(
                    PrintInteraction,
                    "Open a new terminal and run one bounded print interaction.",
                    "claude -p \"Reply with exactly: OK\""),
                AgentSdkInteraction => new FirstTraceGuidance(
                    AgentSdkInteraction,
                    CreateAgentSdkGuidanceText(),
                    null),
                _ => throw new ArgumentException("Unknown bounded interaction.", nameof(interaction)),
            });
        }

        return guidance;
    }

    public FirstTraceEvidenceSelection SelectEvidence(
        IReadOnlyList<DoctorEvidenceCandidate> candidates,
        DateTimeOffset now)
    {
        var current = candidates
            .Where(candidate =>
                candidate.EvidenceClass == DoctorEvidenceClass.RealSource
                && string.Equals(candidate.SourceSurface, SourceSurfaceToken, StringComparison.Ordinal)
                && string.Equals(candidate.SourceAdapter, ExpectedSourceAdapterToken, StringComparison.Ordinal)
                && candidate.ExpiresAt > now)
            .ToArray();
        if (current.Length == 0)
        {
            return FirstTraceEvidenceSelection.NoEligibleCandidates;
        }

        var bindings = current
            .Where(candidate => candidate.EvidenceKind == DoctorEvidenceKind.ExactSessionBinding)
            .Select(candidate => TryParseBinding(candidate.EvidenceRef, out var binding)
                ? binding
                : null)
            .Where(binding => binding is not null)
            .Select(binding => binding!)
            .ToArray();

        var claims = bindings
            .GroupBy(binding => binding.TraceId, StringComparer.Ordinal)
            .Any(group => group.Select(binding => binding.SessionId).Distinct(StringComparer.Ordinal).Count() > 1);
        if (claims)
        {
            return FirstTraceEvidenceSelection.Explicit;
        }

        var groups = new List<HashSet<string>>();
        var sessionGroups = bindings
            .GroupBy(binding => binding.SessionId, StringComparer.Ordinal)
            .ToArray();
        var boundTraces = bindings.Select(binding => binding.TraceId).ToHashSet(StringComparer.Ordinal);
        foreach (var sessionGroup in sessionGroups)
        {
            var sessionId = sessionGroup.Key;
            var traces = sessionGroup.Select(binding => binding.TraceId).ToHashSet(StringComparer.Ordinal);
            var refs = current
                .Where(candidate =>
                    (candidate.EvidenceKind == DoctorEvidenceKind.ExactSessionBinding
                        && TryParseBinding(candidate.EvidenceRef, out var binding)
                        && string.Equals(binding.SessionId, sessionId, StringComparison.Ordinal))
                    || IsPipelineCandidateForTrace(candidate, traces)
                    || (candidate.EvidenceKind == DoctorEvidenceKind.CompletenessContent
                        && TryParseCompleteness(candidate.EvidenceRef, out var completenessSession)
                        && string.Equals(completenessSession, sessionId, StringComparison.Ordinal)))
                .Select(candidate => candidate.EvidenceRef)
                .ToHashSet(StringComparer.Ordinal);
            if (refs.Count > 0)
            {
                groups.Add(refs);
            }
        }

        var unboundTraceGroups = current
            .Where(candidate => IsPipelineCandidate(candidate, out var traceId)
                && !boundTraces.Contains(traceId))
            .GroupBy(candidate => GetTraceId(candidate.EvidenceRef)!, StringComparer.Ordinal);
        foreach (var traceGroup in unboundTraceGroups)
        {
            groups.Add(traceGroup.Select(candidate => candidate.EvidenceRef).ToHashSet(StringComparer.Ordinal));
        }

        if (groups.Count != 1)
        {
            return FirstTraceEvidenceSelection.Explicit;
        }

        return FirstTraceEvidenceSelection.Auto(
            groups[0].OrderBy(reference => reference, StringComparer.Ordinal).ToArray());
    }

    private static string CreateAgentSdkGuidanceText()
    {
        var python = ClaudeAgentSdkGuidanceVariant.CreateGuidance(
            ClaudeAgentSdkGuidanceVariant.PythonLabel,
            "caller_managed_sample",
            "python",
            includeContentCapture: false).Sample;
        var typescript = ClaudeAgentSdkGuidanceVariant.CreateGuidance(
            ClaudeAgentSdkGuidanceVariant.TypeScriptLabel,
            "caller_managed_sample",
            "typescript",
            includeContentCapture: false).Sample;
        return "Caller-managed only. Merge, do not replace, the process environment and " +
            "flush telemetry before a short-lived process exits.\n\nPython:\n" + python +
            "\n\nTypeScript:\n" + typescript +
            "\nfirst-trace never edits caller-owned configuration.";
    }

    private static bool IsPipelineCandidateForTrace(
        DoctorEvidenceCandidate candidate,
        IReadOnlySet<string> traces) =>
        IsPipelineCandidate(candidate, out var traceId) && traces.Contains(traceId);

    private static bool IsPipelineCandidate(DoctorEvidenceCandidate candidate, out string traceId)
    {
        traceId = string.Empty;
        if (candidate.EvidenceKind is not (DoctorEvidenceKind.Ingest
            or DoctorEvidenceKind.RawPersistence
            or DoctorEvidenceKind.Projection))
        {
            return false;
        }

        return TryParseTrace(candidate.EvidenceRef, candidate.EvidenceKind, out traceId);
    }

    private static string? GetTraceId(string evidenceRef) =>
        TryParseTrace(evidenceRef, out var traceId) ? traceId : null;

    private static bool TryParseTrace(string value, out string traceId) =>
        TryParseTrace(value, evidenceKind: null, out traceId);

    private static bool TryParseTrace(
        string value,
        DoctorEvidenceKind? evidenceKind,
        out string traceId)
    {
        traceId = string.Empty;
        var prefix = evidenceKind switch
        {
            DoctorEvidenceKind.Ingest => "claude-otel-ingest-",
            DoctorEvidenceKind.RawPersistence => "claude-otel-raw-",
            DoctorEvidenceKind.Projection => "claude-otel-projection-",
            _ => string.Empty,
        };
        if (prefix.Length == 0)
        {
            prefix = value switch
            {
                _ when value.StartsWith("claude-otel-ingest-", StringComparison.Ordinal) => "claude-otel-ingest-",
                _ when value.StartsWith("claude-otel-raw-", StringComparison.Ordinal) => "claude-otel-raw-",
                _ when value.StartsWith("claude-otel-projection-", StringComparison.Ordinal) => "claude-otel-projection-",
                _ => string.Empty,
            };
        }

        if (prefix.Length == 0 || !value.StartsWith(prefix, StringComparison.Ordinal)
            || value.Length != prefix.Length + 32 + 1 + 16
            || value[prefix.Length + 32] != '-')
        {
            return false;
        }

        traceId = value.Substring(prefix.Length, 32);
        return IsLowerHex(traceId, 32) && IsLowerHex(value[(prefix.Length + 33)..], 16);
    }

    private static bool TryParseBinding(string value, out Binding binding)
    {
        const string prefix = "claude-otel-binding-";
        binding = null!;
        if (!value.StartsWith(prefix, StringComparison.Ordinal)
            || value.Length != prefix.Length + 32 + 1 + 36
            || value[prefix.Length + 32] != '-')
        {
            return false;
        }

        var traceId = value.Substring(prefix.Length, 32);
        var sessionValue = value[(prefix.Length + 33)..];
        if (!IsLowerHex(traceId, 32) || !Guid.TryParseExact(sessionValue, "D", out var sessionId))
        {
            return false;
        }

        binding = new Binding(traceId, sessionId.ToString("D"));
        return true;
    }

    private static bool TryParseCompleteness(string value, out string sessionId)
    {
        const string prefix = "claude-otel-completeness-";
        sessionId = string.Empty;
        if (!value.StartsWith(prefix, StringComparison.Ordinal)
            || value.Length != prefix.Length + 36
            || !Guid.TryParseExact(value[prefix.Length..], "D", out var parsed))
        {
            return false;
        }

        sessionId = parsed.ToString("D");
        return true;
    }

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record Binding(string TraceId, string SessionId);
}
