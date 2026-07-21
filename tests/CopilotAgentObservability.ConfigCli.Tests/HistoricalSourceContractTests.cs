using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class HistoricalSourceContractTests
{
    private static readonly string ContractRoot = System.IO.Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "docs", "specifications", "contracts", "historical-import", "v1");

    private static readonly string[] PolicyFieldCeiling =
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
        "errors.code"
    ];

    [Fact]
    public void Repository_contract_artifacts_validate_against_their_v1_schemas()
    {
        AssertExactFiles("profiles",
        [
            "claude-code-transcript.json",
            "github-copilot-cli-session-state.json"
        ]);
        AssertExactFiles(Path.Combine("fixtures", "claude-code"),
        [
            "detected-unsupported.json",
            "malformed-source.json",
            "missing-source-reference.json"
        ]);
        AssertExactFiles(Path.Combine("fixtures", "github-copilot-cli"),
        [
            "detected-unsupported.json",
            "malformed-source.json"
        ]);
        AssertExactFiles(Path.Combine("fixtures", "handoff"),
        [
            "merge-cases.json",
            "synthetic-candidate-batch.json",
            "zero-candidate-preview.json"
        ]);

        Validate("historical-source-profile.schema.json", Directory.GetFiles(ContractPath("profiles"), "*.json"));
        Validate("historical-adapter-result.schema.json", Directory.GetFiles(ContractPath("fixtures"), "*.json", SearchOption.AllDirectories)
            .Where(path => path.EndsWith("detected-unsupported.json", StringComparison.Ordinal)
                || path.EndsWith("malformed-source.json", StringComparison.Ordinal)
                || path.EndsWith("missing-source-reference.json", StringComparison.Ordinal)));
        Validate("historical-candidate-batch.schema.json", [ContractPath("fixtures", "handoff", "synthetic-candidate-batch.json")]);
        Validate("historical-import-preview.schema.json", [ContractPath("fixtures", "handoff", "zero-candidate-preview.json")]);
        Validate("historical-merge-cases.schema.json", [ContractPath("fixtures", "handoff", "merge-cases.json")]);
    }

    [Fact]
    public void Tier_b_profiles_have_no_supported_format_until_fixture_fingerprint_evidence_exists()
    {
        var copilot = Read("profiles", "github-copilot-cli-session-state.json");
        var claude = Read("profiles", "claude-code-transcript.json");

        AssertProfile(
            copilot.RootElement,
            "github-copilot-cli-session-state",
            "github-copilot-cli-history-v1",
            "github-copilot-cli",
            "1.0.71",
            "session-state/{sessionId}/events.jsonl",
            "explicit_opt_in_documented_root");
        AssertProfile(
            claude.RootElement,
            "claude-code-transcript",
            "claude-code-history-v1",
            "claude-code",
            "2.1.215",
            "hook.transcript_path",
            "official_hook_reference_or_explicit_file");

        Assert.NotEqual(
            copilot.RootElement.GetProperty("adapter_id").GetString(),
            claude.RootElement.GetProperty("adapter_id").GetString());
    }

    [Theory]
    [InlineData("github-copilot-cli", "detected-unsupported.json", "historical_source_format_unsupported")]
    [InlineData("github-copilot-cli", "malformed-source.json", "historical_source_malformed")]
    [InlineData("claude-code", "detected-unsupported.json", "historical_source_format_unsupported")]
    [InlineData("claude-code", "missing-source-reference.json", "historical_source_reference_required")]
    [InlineData("claude-code", "malformed-source.json", "historical_source_malformed")]
    public void Unsupported_and_malformed_adapter_results_fail_closed_with_zero_candidates(
        string source,
        string fileName,
        string expectedDiagnostic)
    {
        using var fixture = Read("fixtures", source, fileName);
        var root = fixture.RootElement;

        Assert.Equal(0, root.GetProperty("candidate_count").GetInt32());
        Assert.False(root.GetProperty("support_authorized").GetBoolean());
        Assert.Equal("none", root.GetProperty("source_format_profile").GetString());
        Assert.Contains(expectedDiagnostic, root.GetProperty("diagnostics").EnumerateArray().Select(Value));
        Assert.True(root.GetProperty("repository_safe").GetBoolean());
    }

    [Fact]
    public void Candidate_handoff_is_synthetic_only_partial_and_contains_no_trace_authority()
    {
        using var fixture = Read("fixtures", "handoff", "synthetic-candidate-batch.json");
        var root = fixture.RootElement;

        Assert.True(root.GetProperty("fixture_only_not_source_support_evidence").GetBoolean());
        Assert.Equal("tier_b", root.GetProperty("source_tier").GetString());
        Assert.Equal("partial", root.GetProperty("completeness_ceiling").GetString());

        var candidate = Assert.Single(root.GetProperty("candidates").EnumerateArray());
        Assert.Equal(["historical_summary_only"], candidate.GetProperty("completeness_reasons").EnumerateArray().Select(Value));
        Assert.Equal(PolicyFieldCeiling, candidate.GetProperty("field_provenance").EnumerateArray().Select(field => Value(field.GetProperty("field"))));
        Assert.All(candidate.GetProperty("field_provenance").EnumerateArray(), provenance =>
            Assert.Equal(root.GetProperty("adapter_id").GetString(), provenance.GetProperty("adapter_id").GetString()));

        var forbidden = new HashSet<string>(StringComparer.Ordinal)
        {
            "trace_id", "span_id", "parent_span_id", "parent_event_id", "duration_ms", "ttft_ms",
            "started_at", "ended_at", "agent_id", "repository", "workspace", "source_path"
        };
        Assert.DoesNotContain(EnumeratePropertyNames(root), forbidden.Contains);
    }

    [Fact]
    public void Import_handoff_rejects_zero_candidates_and_pins_merge_and_retention_decisions()
    {
        using var preview = Read("fixtures", "handoff", "zero-candidate-preview.json");
        var previewRoot = preview.RootElement;
        Assert.Equal(0, previewRoot.GetProperty("eligible_candidate_count").GetInt32());
        Assert.False(previewRoot.GetProperty("commit_allowed").GetBoolean());
        Assert.Equal("historical_import_no_eligible_candidates", previewRoot.GetProperty("rejection_code").GetString());
        Assert.Equal("not_read", previewRoot.GetProperty("content_risk").GetString());

        using var cases = Read("fixtures", "handoff", "merge-cases.json");
        Assert.Equal(
        [
            "duplicate_noop",
            "source_record_conflict_preserve_existing",
            "attach_observation_preserve_strong",
            "keep_distinct_unbound",
            "missing_does_not_overwrite",
            "record_conflict_no_overwrite",
            "partial_historical_summary_only",
            "metadata_only_no_retention_item",
            "read_denied_and_deletion_queued",
            "expiring_raw_default_90d",
            "no_automatic_pin"
        ],
        cases.RootElement.GetProperty("cases").EnumerateArray().Select(item => Value(item.GetProperty("decision"))));
    }

    [Fact]
    public void Contract_spec_references_the_machine_contract_and_all_three_child_handoffs()
    {
        var specPath = System.IO.Path.Combine(ContractRoot, "..", "..", "..", "interfaces", "historical-source-import.md");
        var text = File.ReadAllText(specPath);

        Assert.Contains("historical-import/v1", text, StringComparison.Ordinal);
        Assert.Contains("#77 handoff", text, StringComparison.Ordinal);
        Assert.Contains("#78 handoff", text, StringComparison.Ordinal);
        Assert.Contains("#79 handoff", text, StringComparison.Ordinal);
        Assert.Contains("supported application-version set is empty", text, StringComparison.Ordinal);
        Assert.Contains("raw-default-90d", text, StringComparison.Ordinal);
        Assert.Contains("retained_by_policy", text, StringComparison.Ordinal);
        Assert.Contains("historical_import_no_eligible_candidates", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_schema_validator_rejects_unknown_properties_wrong_types_and_invalid_patterns()
    {
        using var schema = JsonDocument.Parse("""
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["id", "count", "enabled"],
              "properties": {
                "id": { "type": "string", "pattern": "^[a-z]+$" },
                "count": { "type": "integer", "minimum": 0 },
                "enabled": { "type": "boolean" }
              }
            }
            """);
        using var invalid = JsonDocument.Parse("""{ "id": "BAD", "count": "1", "enabled": true, "extra": true }""");

        var errors = HistoricalContractSchemaValidator.Validate(schema, invalid);

        Assert.Contains("$.id does not match the required pattern.", errors);
        Assert.Contains("$.count must be integer.", errors);
        Assert.Contains("$.extra is not allowed.", errors);
    }

    private static void AssertProfile(
        JsonElement profile,
        string profileId,
        string adapterId,
        string sourceSurface,
        string observedVersion,
        string documentedContainer,
        string sourceReferencePolicy)
    {
        Assert.Equal(profileId, profile.GetProperty("profile_id").GetString());
        Assert.Equal(adapterId, profile.GetProperty("adapter_id").GetString());
        Assert.Equal(sourceSurface, profile.GetProperty("source_surface").GetString());
        Assert.Equal("tier_b", profile.GetProperty("source_tier").GetString());
        Assert.Equal("unsupported_no_fixture", profile.GetProperty("support_state").GetString());
        Assert.Empty(profile.GetProperty("supported_application_versions").EnumerateArray());
        Assert.Empty(profile.GetProperty("supported_source_formats").EnumerateArray());
        Assert.Equal([observedVersion], profile.GetProperty("observed_detector_versions").EnumerateArray().Select(Value));
        Assert.Equal(documentedContainer, profile.GetProperty("documented_container").GetString());
        Assert.Equal(sourceReferencePolicy, profile.GetProperty("source_reference_policy").GetString());
        Assert.Empty(profile.GetProperty("active_field_allowlist").EnumerateArray());
        Assert.Equal(PolicyFieldCeiling, profile.GetProperty("policy_field_ceiling").EnumerateArray().Select(Value));
        Assert.Equal("partial", profile.GetProperty("completeness_ceiling").GetString());
        Assert.Equal(["historical_summary_only"], profile.GetProperty("required_completeness_reasons").EnumerateArray().Select(Value));

        var gate = profile.GetProperty("activation_gate");
        Assert.False(gate.GetProperty("product_decision_required").GetBoolean());
        Assert.Equal("source_specific_compatibility_revision", gate.GetProperty("promotion_scope").GetString());
        Assert.Equal(
        [
            "source_application_version",
            "source_format_name",
            "source_format_version",
            "source_fixture_sha256",
            "source_schema_fingerprint",
            "adapter_golden_tests"
        ],
        gate.GetProperty("required_evidence").EnumerateArray().Select(Value));
    }

    private static void AssertExactFiles(string relativeDirectory, string[] expected)
    {
        var directory = ContractPath(relativeDirectory);
        Assert.True(Directory.Exists(directory), $"Missing contract directory: {directory}");
        Assert.Equal(expected, Directory.GetFiles(directory, "*.json").Select(System.IO.Path.GetFileName).Order(StringComparer.Ordinal));
    }

    private static void Validate(string schemaFile, IEnumerable<string> fixturePaths)
    {
        using var schema = Read(schemaFile);
        foreach (var fixturePath in fixturePaths.Order(StringComparer.Ordinal))
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(fixturePath));
            Assert.Empty(HistoricalContractSchemaValidator.Validate(schema, fixture));
        }
    }

    private static JsonDocument Read(params string[] segments) =>
        JsonDocument.Parse(File.ReadAllText(ContractPath(segments)));

    private static string ContractPath(params string[] segments) =>
        segments.Aggregate(ContractRoot, System.IO.Path.Combine);

    private static string Value(JsonElement element) => element.GetString()!;

    private static IEnumerable<string> EnumeratePropertyNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                yield return property.Name;
                foreach (var nested in EnumeratePropertyNames(property.Value)) yield return nested;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            foreach (var nested in EnumeratePropertyNames(item)) yield return nested;
        }
    }
}

internal static class HistoricalContractSchemaValidator
{
    public static IReadOnlyList<string> Validate(JsonDocument schema, JsonDocument value)
    {
        var errors = new List<string>();
        ValidateNode(schema.RootElement, schema.RootElement, value.RootElement, "$", errors);
        return errors;
    }

    private static void ValidateNode(JsonElement root, JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            ValidateNode(root, Resolve(root, reference.GetString()!), value, path, errors);
            return;
        }

        if (!MatchesDeclaredType(schema, value, path, errors)) return;

        if (schema.TryGetProperty("const", out var constant) && value.GetRawText() != constant.GetRawText())
            errors.Add($"{path} must equal {constant.GetRawText()}.");
        if (schema.TryGetProperty("enum", out var allowed)
            && !allowed.EnumerateArray().Any(candidate => candidate.GetRawText() == value.GetRawText()))
            errors.Add($"{path} is not an allowed value.");

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (schema.TryGetProperty("minLength", out var minLength) && text.Length < minLength.GetInt32())
                errors.Add($"{path} is too short.");
            if (schema.TryGetProperty("maxLength", out var maxLength) && text.Length > maxLength.GetInt32())
                errors.Add($"{path} is too long.");
            if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant))
                errors.Add($"{path} does not match the required pattern.");
        }

        if (value.ValueKind == JsonValueKind.Number && schema.TryGetProperty("minimum", out var minimum)
            && value.GetDecimal() < minimum.GetDecimal())
            errors.Add($"{path} is below the minimum.");

        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = schema.TryGetProperty("properties", out var declared) ? declared : default;
            if (schema.TryGetProperty("required", out var required))
            foreach (var name in required.EnumerateArray().Select(Value))
            if (!value.TryGetProperty(name, out _)) errors.Add($"{path}.{name} is required.");

            foreach (var property in value.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var propertySchema))
                    ValidateNode(root, propertySchema, property.Value, $"{path}.{property.Name}", errors);
                else if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False)
                    errors.Add($"{path}.{property.Name} is not allowed.");
            }
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().ToArray();
            if (schema.TryGetProperty("minItems", out var minItems) && items.Length < minItems.GetInt32())
                errors.Add($"{path} has too few items.");
            if (schema.TryGetProperty("maxItems", out var maxItems) && items.Length > maxItems.GetInt32())
                errors.Add($"{path} has too many items.");
            if (schema.TryGetProperty("uniqueItems", out var unique) && unique.GetBoolean()
                && items.Select(item => item.GetRawText()).Distinct(StringComparer.Ordinal).Count() != items.Length)
                errors.Add($"{path} items must be unique.");
            if (schema.TryGetProperty("items", out var itemSchema) && itemSchema.ValueKind == JsonValueKind.Object)
            for (var index = 0; index < items.Length; index++)
                ValidateNode(root, itemSchema, items[index], $"{path}[{index}]", errors);
        }
    }

    private static bool MatchesDeclaredType(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (!schema.TryGetProperty("type", out var type)) return true;
        var declared = type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(Value)
            : [Value(type)];
        if (declared.Any(candidate => Matches(candidate, value))) return true;
        errors.Add($"{path} must be {string.Join(" or ", declared)}.");
        return false;
    }

    private static bool Matches(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false
    };

    private static JsonElement Resolve(JsonElement root, string reference)
    {
        if (!reference.StartsWith("#/$defs/", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported schema reference: {reference}");
        return root.GetProperty("$defs").GetProperty(reference[8..]);
    }

    private static string Value(JsonElement element) => element.GetString()!;
}
