using System.Text.Json;

namespace CopilotAgentObservability.ConfigCli.Setup.Capabilities;

internal static class SourceCapabilityManifestLoader
{
    private const string VsCodeSurface = "github-copilot-vscode";
    private const string CliSurface = "github-copilot-cli";
    private const string VsCodeResourceName = "CopilotAgentObservability.ConfigCli.Setup.Capabilities.Manifests.github-copilot-vscode.json";
    private const string CliResourceName = "CopilotAgentObservability.ConfigCli.Setup.Capabilities.Manifests.github-copilot-cli.json";
    private static readonly string[] ManifestProperties =
    [
        "contract_version", "source_surface", "source_adapter", "support_status", "stability",
        "source_version_detector", "signals", "native_session_identity", "trace_span_identity",
        "timing_ttft", "model_tokens", "retry_attempt", "tool_calls", "permission", "errors",
        "agent_ownership", "prompt_response", "file_diff", "content_capture_gate", "provenance", "completeness",
    ];
    private static readonly string[] ProvenanceKeys =
    [
        "source_adapter", "source_version_or_schema_fingerprint", "source_event_or_trace_span_id",
        "capture_content_state", "normalization_version",
    ];
    private static readonly string[] CompletenessStatuses = ["unbound", "partial", "rich", "full"];
    private static readonly string[] CompletenessReasonCodes =
    [
        "missing_native_session_id", "missing_trace_context", "trace_signal_disabled",
        "content_capture_disabled", "unsupported_source_version", "ingest_gap", "hook_only",
        "historical_summary_only", "unknown_span_kind", "schema_drift_detected", "planned_source_not_enabled",
    ];

    public static SourceCapabilityManifest? LoadForTarget(GitHubCopilotSetupTarget target) => target switch
    {
        GitHubCopilotSetupTarget.VsCode => LoadForSurface(VsCodeSurface),
        GitHubCopilotSetupTarget.Cli => LoadForSurface(CliSurface),
        GitHubCopilotSetupTarget.AppSdk => null,
        _ => throw new InvalidDataException("Unsupported GitHub Copilot setup target."),
    };

    public static SourceCapabilityManifest LoadForSurface(string sourceSurface)
    {
        ArgumentNullException.ThrowIfNull(sourceSurface);

        return sourceSurface switch
        {
            VsCodeSurface => LoadEmbedded(VsCodeSurface, VsCodeResourceName),
            CliSurface => LoadEmbedded(CliSurface, CliResourceName),
            _ => throw new InvalidDataException("Unknown source capability manifest."),
        };
    }

    public static bool MatchesCanonical(SourceCapabilityManifest canonicalManifest, JsonElement candidate)
    {
        ArgumentNullException.ThrowIfNull(canonicalManifest);
        return SemanticallyEqual(canonicalManifest.CanonicalJson, candidate);
    }

    public static bool MatchesCanonical(JsonElement candidate)
    {
        if (candidate.ValueKind != JsonValueKind.Object ||
            !candidate.TryGetProperty("source_surface", out var sourceSurfaceElement) ||
            sourceSurfaceElement.ValueKind != JsonValueKind.String ||
            sourceSurfaceElement.GetString() is not { } sourceSurface)
        {
            return false;
        }

        try
        {
            return MatchesCanonical(LoadForSurface(sourceSurface), candidate);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public static bool IsValidLedgerManifest(JsonElement candidate, string expectedSurface)
    {
        if (expectedSurface is not VsCodeSurface and not CliSurface ||
            !HasExactProperties(candidate, ManifestProperties) ||
            !HasString(candidate, "contract_version", "v1") ||
            !HasString(candidate, "source_surface", expectedSurface) ||
            !HasString(candidate, "source_adapter", "otel-http+copilot-compatible-hook") ||
            !HasClosedString(candidate, "support_status", "active", "planned", "unsupported") ||
            !HasClosedString(candidate, "stability", "stable", "preview", "beta", "internal-unstable") ||
            !IsCapability(candidate.GetProperty("source_version_detector")) ||
            !IsCapability(candidate.GetProperty("native_session_identity")) ||
            !IsCapability(candidate.GetProperty("errors")) ||
            !IsCapability(candidate.GetProperty("content_capture_gate")))
        {
            return false;
        }

        return IsCapabilityGroup(candidate.GetProperty("signals"), "trace", "log", "metric", "hook", "sdk_event", "saved_raw") &&
            IsCapabilityGroup(candidate.GetProperty("trace_span_identity"), "trace_id", "span_id", "parentage") &&
            IsCapabilityGroup(candidate.GetProperty("timing_ttft"), "timing", "ttft") &&
            IsCapabilityGroup(candidate.GetProperty("model_tokens"), "model", "input_tokens", "output_tokens", "total_tokens", "cache_tokens", "reasoning_tokens") &&
            IsCapabilityGroup(candidate.GetProperty("retry_attempt"), "retry", "attempt") &&
            IsCapabilityGroup(candidate.GetProperty("tool_calls"), "identity", "input", "output") &&
            IsCapabilityGroup(candidate.GetProperty("permission"), "wait", "decision") &&
            IsCapabilityGroup(candidate.GetProperty("agent_ownership"), "main_agent", "sub_agent") &&
            IsCapabilityGroup(candidate.GetProperty("prompt_response"), "prompt", "response") &&
            IsCapabilityGroup(candidate.GetProperty("file_diff"), "file", "diff") &&
            HasFixedStringArrayObject(candidate.GetProperty("provenance"), "required_keys", ProvenanceKeys) &&
            HasCompleteness(candidate.GetProperty("completeness"));
    }

    private static SourceCapabilityManifest LoadEmbedded(string expectedSurface, string resourceName)
    {
        using var stream = typeof(SourceCapabilityManifestLoader).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new InvalidDataException("Embedded source capability manifest is unavailable.");
        }

        try
        {
            using var document = JsonDocument.Parse(stream);
            var canonicalJson = document.RootElement.Clone();

            if (canonicalJson.ValueKind != JsonValueKind.Object ||
                !canonicalJson.TryGetProperty("source_surface", out var sourceSurface) ||
                sourceSurface.ValueKind != JsonValueKind.String ||
                !string.Equals(sourceSurface.GetString(), expectedSurface, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Embedded source capability manifest is invalid.");
            }

            return new SourceCapabilityManifest(expectedSurface, canonicalJson);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Embedded source capability manifest is invalid.");
        }
    }

    private static bool SemanticallyEqual(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            return false;
        }

        return expected.ValueKind switch
        {
            JsonValueKind.Object => SemanticallyEqualObjects(expected, actual),
            JsonValueKind.Array => SemanticallyEqualArrays(expected, actual),
            JsonValueKind.String => expected.GetString() == actual.GetString(),
            JsonValueKind.True or JsonValueKind.False => expected.GetBoolean() == actual.GetBoolean(),
            JsonValueKind.Null => true,
            _ => expected.GetRawText() == actual.GetRawText(),
        };
    }

    private static bool SemanticallyEqualObjects(JsonElement expected, JsonElement actual)
    {
        var expectedProperties = expected.EnumerateObject().ToArray();
        var actualProperties = actual.EnumerateObject().ToArray();

        return expectedProperties.Length == actualProperties.Length &&
            expectedProperties.All(property => actual.TryGetProperty(property.Name, out var actualValue) && SemanticallyEqual(property.Value, actualValue));
    }

    private static bool SemanticallyEqualArrays(JsonElement expected, JsonElement actual)
    {
        return expected.GetArrayLength() == actual.GetArrayLength() &&
            expected.EnumerateArray().Zip(actual.EnumerateArray()).All(pair => SemanticallyEqual(pair.First, pair.Second));
    }

    private static bool IsCapabilityGroup(JsonElement element, params string[] names) =>
        HasExactProperties(element, names) && names.All(name => IsCapability(element.GetProperty(name)));

    private static bool IsCapability(JsonElement element) =>
        HasExactProperties(element, "availability") &&
        HasClosedString(element, "availability", "available", "unavailable", "unknown");

    private static bool HasCompleteness(JsonElement element) =>
        HasExactProperties(element, "statuses", "reason_codes") &&
        HasFixedStringArray(element.GetProperty("statuses"), CompletenessStatuses) &&
        HasFixedStringArray(element.GetProperty("reason_codes"), CompletenessReasonCodes);

    private static bool HasFixedStringArrayObject(JsonElement element, string propertyName, IReadOnlyList<string> expected) =>
        HasExactProperties(element, propertyName) && HasFixedStringArray(element.GetProperty(propertyName), expected);

    private static bool HasFixedStringArray(JsonElement element, IReadOnlyList<string> expected) =>
        element.ValueKind == JsonValueKind.Array &&
        element.GetArrayLength() == expected.Count &&
        element.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
            .SequenceEqual(expected, StringComparer.Ordinal);

    private static bool HasExactProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var expected = new HashSet<string>(names, StringComparer.Ordinal);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        return expected.Count == names.Length &&
            element.EnumerateObject().All(property => expected.Contains(property.Name) && actual.Add(property.Name)) &&
            actual.SetEquals(expected);
    }

    private static bool HasString(JsonElement element, string propertyName, string expected)
    {
        var property = element.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.String && property.GetString() == expected;
    }

    private static bool HasClosedString(JsonElement element, string propertyName, params string[] allowed)
    {
        var property = element.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.String && allowed.Contains(property.GetString(), StringComparer.Ordinal);
    }
}
