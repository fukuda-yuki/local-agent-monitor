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
    public void Adapter_result_schema_accepts_a_future_fixture_bound_supported_result()
    {
        using var schema = Read("historical-adapter-result.schema.json");
        var result = JsonNode.Parse(File.ReadAllText(ContractPath("fixtures", "github-copilot-cli", "detected-unsupported.json")))!.AsObject();
        result["support_authorized"] = true;
        result["source_format_profile"] = "fixture-bound-v1";
        result["candidate_count"] = 1;
        result["content_risk"] = "source_read_metadata_only";
        result["diagnostics"] = new JsonArray();

        using var supported = JsonDocument.Parse(result.ToJsonString());
        Assert.Empty(HistoricalContractSchemaValidator.Validate(schema, supported));
    }

    [Fact]
    public void Adapter_result_schema_enforces_authorized_and_unauthorized_branches()
    {
        using var schema = Read("historical-adapter-result.schema.json");

        AssertRejected(Unsupported(result => result["source_format_profile"] = "fixture-bound-v1"));
        AssertRejected(Unsupported(result => result["candidate_count"] = 1));
        AssertRejected(Unsupported(result => result["content_risk"] = "source_read_metadata_only"));
        AssertRejected(Unsupported(result => result["diagnostics"] = new JsonArray()));
        AssertRejected(Unsupported(result => result["detection_state"] = "not_evaluated"));
        AssertRejected(Unsupported(result => result["diagnostics"] = new JsonArray("historical_source_reference_required")));

        AssertRejected(Supported(result => result["source_format_profile"] = "none"));
        AssertRejected(Supported(result => result["candidate_count"] = 0));
        AssertRejected(Supported(result => result["detection_state"] = "not_evaluated"));
        AssertRejected(Supported(result => result["source_reference_state"] = "missing"));
        AssertRejected(Supported(result => result["source_application_version"] = null));
        AssertRejected(Supported(result => result["content_risk"] = "not_read"));
        AssertRejected(Supported(result => result["diagnostics"] = new JsonArray("historical_source_format_unsupported")));

        JsonObject Unsupported(Action<JsonObject> mutate)
        {
            var result = AdapterFixture();
            mutate(result);
            return result;
        }

        JsonObject Supported(Action<JsonObject> mutate)
        {
            var result = AdapterFixture();
            result["support_authorized"] = true;
            result["source_format_profile"] = "fixture-bound-v1";
            result["candidate_count"] = 1;
            result["content_risk"] = "source_read_metadata_only";
            result["diagnostics"] = new JsonArray();
            mutate(result);
            return result;
        }

        JsonObject AdapterFixture() => JsonNode.Parse(File.ReadAllText(
            ContractPath("fixtures", "github-copilot-cli", "detected-unsupported.json")))!.AsObject();

        void AssertRejected(JsonObject result)
        {
            using var document = JsonDocument.Parse(result.ToJsonString());
            Assert.Contains("$ does not match exactly one schema branch.",
                HistoricalContractSchemaValidator.Validate(schema, document));
        }
    }

    [Fact]
    public void Producer_contract_bounds_match_importer_admission_limits()
    {
        using var adapterSchema = Read("historical-adapter-result.schema.json");
        Assert.Equal(1000, adapterSchema.RootElement.GetProperty("properties").GetProperty("candidate_count").GetProperty("maximum").GetInt32());
        Assert.Equal(128, adapterSchema.RootElement.GetProperty("$defs").GetProperty("token").GetProperty("maxLength").GetInt32());
        var tokenPattern = adapterSchema.RootElement.GetProperty("$defs").GetProperty("token").GetProperty("pattern").GetString();
        var semverPattern = adapterSchema.RootElement.GetProperty("properties").GetProperty("source_application_version").GetProperty("pattern").GetString();

        using var batchSchema = Read("historical-candidate-batch.schema.json");
        Assert.Equal(1000, batchSchema.RootElement.GetProperty("properties").GetProperty("candidates").GetProperty("maxItems").GetInt32());
        Assert.Equal(128, batchSchema.RootElement.GetProperty("$defs").GetProperty("token").GetProperty("maxLength").GetInt32());
        Assert.Equal(tokenPattern, batchSchema.RootElement.GetProperty("$defs").GetProperty("token").GetProperty("pattern").GetString());
        Assert.Equal(semverPattern, batchSchema.RootElement.GetProperty("$defs").GetProperty("version").GetProperty("pattern").GetString());

        using var profileSchema = Read("historical-source-profile.schema.json");
        Assert.Equal(tokenPattern, profileSchema.RootElement.GetProperty("$defs").GetProperty("token").GetProperty("pattern").GetString());
        Assert.Equal(semverPattern, profileSchema.RootElement.GetProperty("$defs").GetProperty("version").GetProperty("pattern").GetString());

        using var previewSchema = Read("historical-import-preview.schema.json");
        Assert.Equal(tokenPattern, previewSchema.RootElement.GetProperty("$defs").GetProperty("token").GetProperty("pattern").GetString());

        using var mergeSchema = Read("historical-merge-cases.schema.json");
        Assert.Equal(tokenPattern, mergeSchema.RootElement.GetProperty("$defs").GetProperty("case").GetProperty("properties").GetProperty("case_id").GetProperty("pattern").GetString());
    }

    [Theory]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-.alpha")]
    [InlineData("1.2.3-alpha..1")]
    [InlineData("1.2.3-alpha.")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3+.build")]
    [InlineData("1.2.3+build..1")]
    [InlineData("1.2.3+build.")]
    public void Producer_semver_grammar_rejects_noncanonical_edges(string version)
    {
        using var schema = Read("historical-adapter-result.schema.json");
        var result = JsonNode.Parse(File.ReadAllText(ContractPath("fixtures", "github-copilot-cli", "detected-unsupported.json")))!.AsObject();
        result["source_application_version"] = version;
        using var invalid = JsonDocument.Parse(result.ToJsonString());

        Assert.Contains(
            "$.source_application_version does not match the required pattern.",
            HistoricalContractSchemaValidator.Validate(schema, invalid));
    }

    [Theory]
    [InlineData("0.0.0")]
    [InlineData("1.2.3-alpha")]
    [InlineData("1.2.3-alpha.1+build.001")]
    [InlineData("1.2.3-0")]
    public void Producer_semver_grammar_accepts_canonical_edges(string version)
    {
        using var schema = Read("historical-adapter-result.schema.json");
        var result = JsonNode.Parse(File.ReadAllText(ContractPath("fixtures", "github-copilot-cli", "detected-unsupported.json")))!.AsObject();
        result["source_application_version"] = version;
        using var valid = JsonDocument.Parse(result.ToJsonString());

        Assert.Empty(HistoricalContractSchemaValidator.Validate(schema, valid));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    public void Producer_grammar_families_reject_terminal_line_breaks(string lineBreak)
    {
        using var adapterSchema = Read("historical-adapter-result.schema.json");
        var adapter = JsonNode.Parse(File.ReadAllText(ContractPath("fixtures", "github-copilot-cli", "detected-unsupported.json")))!.AsObject();
        adapter["profile_id"] = adapter["profile_id"]!.GetValue<string>() + lineBreak;
        using var invalidToken = JsonDocument.Parse(adapter.ToJsonString());
        Assert.Contains("$.profile_id does not match the required pattern.", HistoricalContractSchemaValidator.Validate(adapterSchema, invalidToken));

        var versionedAdapter = JsonNode.Parse(File.ReadAllText(ContractPath("fixtures", "github-copilot-cli", "detected-unsupported.json")))!.AsObject();
        versionedAdapter["source_application_version"] = "1.0.71" + lineBreak;
        using var invalidVersion = JsonDocument.Parse(versionedAdapter.ToJsonString());
        Assert.Contains("$.source_application_version does not match the required pattern.", HistoricalContractSchemaValidator.Validate(adapterSchema, invalidVersion));

        using var batchSchema = Read("historical-candidate-batch.schema.json");
        var batchWithKey = JsonNode.Parse(File.ReadAllText(ContractPath("fixtures", "handoff", "synthetic-candidate-batch.json")))!.AsObject();
        var candidate = batchWithKey["candidates"]![0]!.AsObject();
        candidate["candidate_key"] = candidate["candidate_key"]!.GetValue<string>() + lineBreak;
        using var invalidKey = JsonDocument.Parse(batchWithKey.ToJsonString());
        Assert.Contains("$.candidates[0].candidate_key is too long.", HistoricalContractSchemaValidator.Validate(batchSchema, invalidKey));

        var batchWithHash = JsonNode.Parse(File.ReadAllText(ContractPath("fixtures", "handoff", "synthetic-candidate-batch.json")))!.AsObject();
        batchWithHash["source_fixture_sha256"] = batchWithHash["source_fixture_sha256"]!.GetValue<string>() + lineBreak;
        using var invalidHash = JsonDocument.Parse(batchWithHash.ToJsonString());
        Assert.Contains("$.source_fixture_sha256 is too long.", HistoricalContractSchemaValidator.Validate(batchSchema, invalidHash));
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
        AssertCandidateFieldParity(candidate);

        var forbidden = new HashSet<string>(StringComparer.Ordinal)
        {
            "trace_id", "span_id", "parent_span_id", "parent_event_id", "duration_ms", "ttft_ms",
            "started_at", "ended_at", "agent_id", "repository", "workspace", "source_path"
        };
        Assert.DoesNotContain(EnumeratePropertyNames(root), forbidden.Contains);
    }

    [Fact]
    public void Candidate_schema_accepts_production_marker_and_sparse_allowlisted_values()
    {
        using var schema = Read("historical-candidate-batch.schema.json");
        var batch = JsonNode.Parse(File.ReadAllText(ContractPath("fixtures", "handoff", "synthetic-candidate-batch.json")))!.AsObject();
        batch["fixture_only_not_source_support_evidence"] = false;

        var candidate = batch["candidates"]!.AsArray().Single()!.AsObject();
        var provenance = candidate["field_provenance"]!.AsArray()
            .Single(item => item!["field"]!.GetValue<string>() == "errors.present")!
            .DeepClone();
        candidate["values"] = JsonNode.Parse("""{ "errors": { "present": false } }""");
        candidate["field_provenance"] = new JsonArray(provenance);

        using var productionBatch = JsonDocument.Parse(batch.ToJsonString());
        Assert.Empty(HistoricalContractSchemaValidator.Validate(schema, productionBatch));
        AssertCandidateFieldParity(productionBatch.RootElement.GetProperty("candidates")[0]);
        Assert.False(productionBatch.RootElement.GetProperty("fixture_only_not_source_support_evidence").GetBoolean());
        Assert.False(productionBatch.RootElement.GetProperty("candidates")[0].GetProperty("values").TryGetProperty("model_tokens", out _));
        Assert.False(productionBatch.RootElement.GetProperty("candidates")[0].GetProperty("values").TryGetProperty("retry_attempt", out _));
    }

    [Theory]
    [InlineData("candidate_key")]
    [InlineData("source_record_key")]
    public void Candidate_keys_reject_paths_and_require_closed_repository_safe_opaque_tokens(string propertyName)
    {
        using var schema = Read("historical-candidate-batch.schema.json");
        var batch = JsonNode.Parse(File.ReadAllText(ContractPath("fixtures", "handoff", "synthetic-candidate-batch.json")))!.AsObject();
        var candidate = batch["candidates"]!.AsArray().Single()!.AsObject();
        candidate[propertyName] = "C:\\Users\\private\\session.jsonl";

        using var invalidBatch = JsonDocument.Parse(batch.ToJsonString());
        Assert.Contains(
            $"$.candidates[0].{propertyName} does not match the required pattern.",
            HistoricalContractSchemaValidator.Validate(schema, invalidBatch));
    }

    [Fact]
    public void Candidate_schema_rejects_an_empty_values_object()
    {
        using var schema = Read("historical-candidate-batch.schema.json");
        var batch = JsonNode.Parse(File.ReadAllText(ContractPath("fixtures", "handoff", "synthetic-candidate-batch.json")))!.AsObject();
        var candidate = batch["candidates"]!.AsArray().Single()!.AsObject();
        candidate["values"] = new JsonObject();

        using var invalidBatch = JsonDocument.Parse(batch.ToJsonString());
        Assert.Contains(
            "$.candidates[0].values has too few properties.",
            HistoricalContractSchemaValidator.Validate(schema, invalidBatch));
    }

    [Fact]
    public void Candidate_field_parity_rejects_missing_or_extra_provenance()
    {
        using var fixture = Read("fixtures", "handoff", "synthetic-candidate-batch.json");
        var candidate = fixture.RootElement.GetProperty("candidates")[0];
        AssertCandidateFieldParity(candidate);

        var batch = JsonNode.Parse(fixture.RootElement.GetRawText())!.AsObject();
        batch["candidates"]![0]!["field_provenance"]!.AsArray().RemoveAt(0);
        using var mismatched = JsonDocument.Parse(batch.ToJsonString());
        Assert.Equal(
            ["Candidate populated fields and field provenance must have exact one-to-one ordered correspondence."],
            CandidateFieldParityErrors(mismatched.RootElement.GetProperty("candidates")[0]));

        var extraBatch = JsonNode.Parse(fixture.RootElement.GetRawText())!.AsObject();
        var provenance = extraBatch["candidates"]![0]!["field_provenance"]!.AsArray();
        provenance.Add(provenance[^1]!.DeepClone());
        using var extra = JsonDocument.Parse(extraBatch.ToJsonString());
        Assert.Equal(
            ["Candidate populated fields and field provenance must have exact one-to-one ordered correspondence."],
            CandidateFieldParityErrors(extra.RootElement.GetProperty("candidates")[0]));
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
            "keep_separate_no_cross_store_comparison",
            "partial_historical_summary_only",
            "metadata_only_no_retention_item",
            "read_denied_and_deletion_queued",
            "expiring_raw_default_90d",
            "no_automatic_pin"
        ],
        cases.RootElement.GetProperty("cases").EnumerateArray().Select(item => Value(item.GetProperty("decision"))));
    }

    [Fact]
    public void Import_preview_schema_accepts_future_eligible_candidates_without_a_rejection_code()
    {
        using var schema = Read("historical-import-preview.schema.json");
        var preview = JsonNode.Parse(File.ReadAllText(ContractPath("fixtures", "handoff", "zero-candidate-preview.json")))!.AsObject();
        preview["adapter_diagnostics"] = new JsonArray();
        preview["eligible_candidate_count"] = 1;
        preview["commit_allowed"] = true;
        preview["rejection_code"] = null;
        preview["content_risk"] = "source_read_metadata_only";

        using var eligible = JsonDocument.Parse(preview.ToJsonString());
        Assert.Empty(HistoricalContractSchemaValidator.Validate(schema, eligible));
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
    public void Contract_schema_validator_enforces_composition_bounds_and_duplicate_members()
    {
        using var schema = JsonDocument.Parse("""
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["id", "kind", "count", "enabled"],
              "properties": {
                "id": { "type": "string", "pattern": "^[a-z]+$" },
                "kind": { "type": "string" },
                "count": { "type": "integer", "minimum": 0, "maximum": 1 },
                "enabled": { "type": "boolean" }
              },
              "oneOf": [
                { "properties": { "kind": { "const": "a" } } },
                { "properties": { "kind": { "const": "b" } } }
              ],
              "allOf": [
                {
                  "if": { "properties": { "enabled": { "const": true } } },
                  "then": { "properties": { "count": { "maximum": 0 } } }
                }
              ]
            }
            """);
        using var invalid = JsonDocument.Parse("""{ "id": "BAD", "kind": "c", "count": 1, "enabled": true, "extra": true }""");

        var errors = HistoricalContractSchemaValidator.Validate(schema, invalid);

        Assert.Contains("$.id does not match the required pattern.", errors);
        Assert.Contains("$ does not match exactly one schema branch.", errors);
        Assert.Contains("$.count is above the maximum.", errors);
        Assert.Contains("$.extra is not allowed.", errors);

        using var duplicate = JsonDocument.Parse("""{ "id": "first", "id": "second", "kind": "a", "count": 0, "enabled": false }""");
        Assert.Contains("$.id is duplicated.", HistoricalContractSchemaValidator.Validate(schema, duplicate));

        using var semanticUniqueSchema = JsonDocument.Parse("""{ "type": "array", "uniqueItems": true, "items": { "type": "object" } }""");
        using var reorderedDuplicate = JsonDocument.Parse("""[{ "a": 1, "b": 2 }, { "b": 2, "a": 1 }]""");
        Assert.Contains("$ items must be unique.", HistoricalContractSchemaValidator.Validate(semanticUniqueSchema, reorderedDuplicate));

        using var unsupportedVocabulary = JsonDocument.Parse("""{ "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "string", "format": "email" }""");
        using var stringValue = JsonDocument.Parse("\"value\"");
        Assert.Contains("$schema.format is not a supported schema keyword.", HistoricalContractSchemaValidator.Validate(unsupportedVocabulary, stringValue));

        using var unsupportedDialect = JsonDocument.Parse("""{ "$schema": "http://json-schema.org/draft-07/schema#", "type": "string" }""");
        Assert.Contains("$schema.$schema must identify JSON Schema 2020-12.", HistoricalContractSchemaValidator.Validate(unsupportedDialect, stringValue));
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

    private static void AssertCandidateFieldParity(JsonElement candidate)
    {
        Assert.Empty(CandidateFieldParityErrors(candidate));
    }

    private static IReadOnlyList<string> CandidateFieldParityErrors(JsonElement candidate)
    {
        var populatedFields = candidate.GetProperty("values").EnumerateObject()
            .SelectMany(family => family.Value.EnumerateObject().Select(field => $"{family.Name}.{field.Name}"))
            .ToArray();
        var provenanceFields = candidate.GetProperty("field_provenance").EnumerateArray()
            .Select(provenance => Value(provenance.GetProperty("field")))
            .ToArray();
        var expectedProvenance = PolicyFieldCeiling
            .Where(field => populatedFields.Contains(field, StringComparer.Ordinal))
            .ToArray();

        return populatedFields.Distinct(StringComparer.Ordinal).Count() == populatedFields.Length
            && populatedFields.Length == provenanceFields.Length
            && provenanceFields.SequenceEqual(expectedProvenance, StringComparer.Ordinal)
            ? []
            : ["Candidate populated fields and field provenance must have exact one-to-one ordered correspondence."];
    }

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
    private const string SupportedDialect = "https://json-schema.org/draft/2020-12/schema";

    private static readonly IReadOnlySet<string> SupportedKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "$schema", "$id", "$ref", "$defs", "title", "description",
        "type", "const", "enum", "properties", "required", "additionalProperties",
        "minProperties", "maxProperties", "minLength", "maxLength", "pattern",
        "minimum", "maximum", "items", "minItems", "maxItems", "uniqueItems",
        "allOf", "oneOf", "if", "then", "else",
    };

    public static IReadOnlyList<string> Validate(JsonDocument schema, JsonDocument value)
    {
        var errors = new List<string>();
        ValidateSupportedSchema(schema.RootElement, "$schema", errors);
        if (errors.Count != 0) return errors.Distinct(StringComparer.Ordinal).ToArray();
        ValidateNode(schema.RootElement, schema.RootElement, value.RootElement, "$", errors);
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void ValidateSupportedSchema(JsonElement schema, string path, List<string> errors)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path} must be an object schema.");
            return;
        }

        var keywords = schema.EnumerateObject().ToArray();
        foreach (var duplicate in keywords.GroupBy(keyword => keyword.Name, StringComparer.Ordinal).Where(group => group.Count() > 1))
            errors.Add($"{path}.{duplicate.Key} is duplicated.");
        foreach (var keyword in keywords)
        {
            if (!SupportedKeywords.Contains(keyword.Name))
            {
                errors.Add($"{path}.{keyword.Name} is not a supported schema keyword.");
                continue;
            }

            switch (keyword.Name)
            {
                case "$schema" when keyword.Value.ValueKind != JsonValueKind.String
                    || keyword.Value.GetString() != SupportedDialect:
                    errors.Add($"{path}.$schema must identify JSON Schema 2020-12.");
                    break;
                case "$ref" when keyword.Value.ValueKind != JsonValueKind.String
                    || !keyword.Value.GetString()!.StartsWith("#/$defs/", StringComparison.Ordinal):
                    errors.Add($"{path}.$ref must be a local $defs reference.");
                    break;
                case "properties" or "$defs":
                    ValidateSchemaMap(keyword.Value, $"{path}.{keyword.Name}", errors);
                    break;
                case "items" or "if" or "then" or "else":
                    ValidateSupportedSchema(keyword.Value, $"{path}.{keyword.Name}", errors);
                    break;
                case "additionalProperties" when keyword.Value.ValueKind == JsonValueKind.Object:
                    errors.Add($"{path}.additionalProperties object schemas are not supported.");
                    break;
                case "additionalProperties" when keyword.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False):
                    errors.Add($"{path}.additionalProperties must be boolean.");
                    break;
                case "allOf" or "oneOf":
                    if (keyword.Value.ValueKind != JsonValueKind.Array)
                    {
                        errors.Add($"{path}.{keyword.Name} must be an array of schemas.");
                        break;
                    }
                    var index = 0;
                    foreach (var branch in keyword.Value.EnumerateArray())
                        ValidateSupportedSchema(branch, $"{path}.{keyword.Name}[{index++}]", errors);
                    break;
            }
        }
    }

    private static void ValidateSchemaMap(JsonElement map, string path, List<string> errors)
    {
        if (map.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path} must be an object.");
            return;
        }

        var entries = map.EnumerateObject().ToArray();
        foreach (var duplicate in entries.GroupBy(entry => entry.Name, StringComparer.Ordinal).Where(group => group.Count() > 1))
            errors.Add($"{path}.{duplicate.Key} is duplicated.");
        foreach (var entry in entries)
            ValidateSupportedSchema(entry.Value, $"{path}.{entry.Name}", errors);
    }

    private static void ValidateNode(JsonElement root, JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (schema.TryGetProperty("$ref", out var reference))
            ValidateNode(root, Resolve(root, reference.GetString()!), value, path, errors);

        if (!MatchesDeclaredType(schema, value, path, errors)) return;

        if (schema.TryGetProperty("const", out var constant) && !JsonEquals(value, constant))
            errors.Add($"{path} must equal {constant.GetRawText()}.");
        if (schema.TryGetProperty("enum", out var allowed)
            && !allowed.EnumerateArray().Any(candidate => JsonEquals(value, candidate)))
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

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (schema.TryGetProperty("minimum", out var minimum)
                && (!value.TryGetDecimal(out var minimumNumber) || minimumNumber < minimum.GetDecimal()))
                errors.Add($"{path} is below the minimum.");
            if (schema.TryGetProperty("maximum", out var maximum)
                && (!value.TryGetDecimal(out var maximumNumber) || maximumNumber > maximum.GetDecimal()))
                errors.Add($"{path} is above the maximum.");
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var members = value.EnumerateObject().ToArray();
            foreach (var duplicate in members.GroupBy(property => property.Name, StringComparer.Ordinal).Where(group => group.Count() > 1))
                errors.Add($"{path}.{duplicate.Key} is duplicated.");

            if (schema.TryGetProperty("minProperties", out var minProperties)
                && members.Length < minProperties.GetInt32())
                errors.Add($"{path} has too few properties.");
            if (schema.TryGetProperty("maxProperties", out var maxProperties)
                && members.Length > maxProperties.GetInt32())
                errors.Add($"{path} has too many properties.");

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
                && items.Distinct(JsonElementComparer.Instance).Count() != items.Length)
                errors.Add($"{path} items must be unique.");
            if (schema.TryGetProperty("items", out var itemSchema) && itemSchema.ValueKind == JsonValueKind.Object)
            for (var index = 0; index < items.Length; index++)
                ValidateNode(root, itemSchema, items[index], $"{path}[{index}]", errors);
        }

        if (schema.TryGetProperty("allOf", out var allOf))
        foreach (var branch in allOf.EnumerateArray())
            ValidateNode(root, branch, value, path, errors);

        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            var matches = 0;
            foreach (var branch in oneOf.EnumerateArray())
            {
                var branchErrors = new List<string>();
                ValidateNode(root, branch, value, path, branchErrors);
                if (branchErrors.Count == 0) matches++;
            }
            if (matches != 1) errors.Add($"{path} does not match exactly one schema branch.");
        }

        if (schema.TryGetProperty("if", out var condition))
        {
            var conditionErrors = new List<string>();
            ValidateNode(root, condition, value, path, conditionErrors);
            var branchName = conditionErrors.Count == 0 ? "then" : "else";
            if (schema.TryGetProperty(branchName, out var conditionalBranch))
                ValidateNode(root, conditionalBranch, value, path, errors);
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

    private static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        return left.ValueKind switch
        {
            JsonValueKind.Object => ObjectsEqual(left, right),
            JsonValueKind.Array => left.EnumerateArray().SequenceEqual(right.EnumerateArray(), JsonElementComparer.Instance),
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.TryGetDecimal(out var leftNumber)
                && right.TryGetDecimal(out var rightNumber)
                && leftNumber == rightNumber,
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            JsonValueKind.Null => true,
            _ => left.GetRawText() == right.GetRawText(),
        };
    }

    private static bool ObjectsEqual(JsonElement left, JsonElement right)
    {
        var leftProperties = left.EnumerateObject().ToArray();
        var rightProperties = right.EnumerateObject().ToArray();
        return leftProperties.Length == rightProperties.Length
            && leftProperties.All(property => right.TryGetProperty(property.Name, out var other)
                && JsonEquals(property.Value, other));
    }

    private sealed class JsonElementComparer : IEqualityComparer<JsonElement>
    {
        public static JsonElementComparer Instance { get; } = new();

        public bool Equals(JsonElement left, JsonElement right) => JsonEquals(left, right);

        public int GetHashCode(JsonElement value) => JsonHash(value);
    }

    private static int JsonHash(JsonElement value)
    {
        var hash = new HashCode();
        hash.Add(value.ValueKind);
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    hash.Add(property.Name, StringComparer.Ordinal);
                    hash.Add(JsonHash(property.Value));
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) hash.Add(JsonHash(item));
                break;
            case JsonValueKind.String:
                hash.Add(value.GetString(), StringComparer.Ordinal);
                break;
            case JsonValueKind.Number:
                if (value.TryGetDecimal(out var number)) hash.Add(number);
                else hash.Add(value.GetRawText(), StringComparer.Ordinal);
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                hash.Add(value.GetBoolean());
                break;
        }
        return hash.ToHashCode();
    }

    private static string Value(JsonElement element) => element.GetString()!;
}
