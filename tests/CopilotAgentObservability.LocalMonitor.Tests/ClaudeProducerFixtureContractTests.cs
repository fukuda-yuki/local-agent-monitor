using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class ClaudeProducerFixtureContractTests
{
    private const string ExpectedAdapter = "claude-code-hook";
    private const string ExpectedSourceDocument = "https://code.claude.com/docs/en/hooks";
    private const string ExpectedProducerContractLabel = "current-unversioned-claude-code-hooks-documentation";
    private const string ExpectedMappingPath = "docs/specifications/contracts/source-capabilities/v1/claude-code/hook-mapping.json";
    private static readonly string[] ExpectedAllowlist =
    [
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "PostToolUse",
        "PostToolUseFailure", "SubagentStart", "SubagentStop", "Stop", "StopFailure", "SessionEnd",
    ];

    private static readonly MappingRow[] ExpectedMappingRows =
    [
        Required("hook.session_id", "$.session_id", "every configured Hook event", "string", "configured Hook event fires", J.String),
        Required("hook.event_name", "$.hook_event_name", "every configured Hook event", "string", "configured Hook event fires", J.String),
        Conditional("hook.prompt_id", "$.prompt_id", "any Hook event where the producer emits prompt_id", "string", "absent before first user input", J.String),
        Required("hook.transcript_path", "$.transcript_path", "every configured Hook event", "string path", "configured Hook event fires", J.String),
        Required("hook.cwd", "$.cwd", "every configured Hook event", "string path", "configured Hook event fires", J.String),
        Conditional("hook.permission_mode", "$.permission_mode", "any Hook event where the producer emits permission_mode", "string enum", "event-dependent", J.String),
        Conditional("hook.effort_level", "$.effort.level", "any Hook event where the producer emits effort.level", "string enum", "tool-use context and supporting model", J.String),
        Conditional("hook.conditional_agent_id", "$.agent_id", "hook_event_name is not SubagentStart or SubagentStop and producer emits agent_id", "string", "event fires inside a subagent", J.String),
        Conditional("hook.conditional_agent_type", "$.agent_type", "hook_event_name is not SubagentStart or SubagentStop and producer emits agent_type", "string", "event fires inside a subagent or in a top-level --agent session", J.String),
        Required("hook.session_start.source", "$.source", "hook_event_name == \"SessionStart\"", "string enum", "SessionStart configured", J.String),
        Optional("hook.session_start.model", "$.model", "hook_event_name == \"SessionStart\"", "string", "producer emits the optional model", J.String),
        Optional("hook.session_start.session_title", "$.session_title", "hook_event_name == \"SessionStart\"", "string", "current session title is already set (for example via --name or /rename)", J.String),
        Required("hook.user_prompt_submit.prompt", "$.prompt", "hook_event_name == \"UserPromptSubmit\"", "string", "UserPromptSubmit configured", J.String),
        Required("hook.pre_tool_use.tool_name", "$.tool_name", "hook_event_name == \"PreToolUse\"", "string", "PreToolUse configured", J.String),
        Required("hook.pre_tool_use.tool_input", "$.tool_input", "hook_event_name == \"PreToolUse\"", "JSON object", "PreToolUse configured", J.Object),
        Required("hook.pre_tool_use.tool_use_id", "$.tool_use_id", "hook_event_name == \"PreToolUse\"", "string", "PreToolUse configured", J.String),
        Required("hook.permission_request.tool_name", "$.tool_name", "hook_event_name == \"PermissionRequest\"", "string", "PermissionRequest configured", J.String),
        Required("hook.permission_request.tool_input", "$.tool_input", "hook_event_name == \"PermissionRequest\"", "JSON object", "PermissionRequest configured", J.Object),
        Optional("hook.permission_request.permission_suggestions", "$.permission_suggestions", "hook_event_name == \"PermissionRequest\"", "array", "producer emits suggestions", J.Array),
        Required("hook.post_tool_use.tool_name", "$.tool_name", "hook_event_name == \"PostToolUse\"", "string", "PostToolUse configured", J.String),
        Required("hook.post_tool_use.tool_input", "$.tool_input", "hook_event_name == \"PostToolUse\"", "JSON object", "PostToolUse configured", J.Object),
        Required("hook.post_tool_use.tool_use_id", "$.tool_use_id", "hook_event_name == \"PostToolUse\"", "string", "PostToolUse configured after successful execution", J.String),
        Required("hook.post_tool_use.tool_response", "$.tool_response", "hook_event_name == \"PostToolUse\"", "producer-defined JSON value", "PostToolUse configured", J.Any),
        Optional("hook.post_tool_use.duration_ms", "$.duration_ms", "hook_event_name == \"PostToolUse\"", "number", "producer version emits duration_ms", J.Number),
        Required("hook.post_tool_failure.tool_name", "$.tool_name", "hook_event_name == \"PostToolUseFailure\"", "string", "PostToolUseFailure configured", J.String),
        Required("hook.post_tool_failure.tool_input", "$.tool_input", "hook_event_name == \"PostToolUseFailure\"", "JSON object", "PostToolUseFailure configured", J.Object),
        Required("hook.post_tool_failure.tool_use_id", "$.tool_use_id", "hook_event_name == \"PostToolUseFailure\"", "string", "PostToolUseFailure configured", J.String),
        Required("hook.post_tool_failure.error", "$.error", "hook_event_name == \"PostToolUseFailure\"", "string", "PostToolUseFailure configured", J.String),
        Optional("hook.post_tool_failure.is_interrupt", "$.is_interrupt", "hook_event_name == \"PostToolUseFailure\"", "boolean", "producer emits is_interrupt", J.Boolean),
        Optional("hook.post_tool_failure.duration_ms", "$.duration_ms", "hook_event_name == \"PostToolUseFailure\"", "number", "producer emits duration_ms", J.Number),
        Required("hook.subagent_start.agent_id", "$.agent_id", "hook_event_name == \"SubagentStart\"", "string", "SubagentStart configured", J.String),
        Required("hook.subagent_start.agent_type", "$.agent_type", "hook_event_name == \"SubagentStart\"", "string", "SubagentStart configured", J.String),
        Required("hook.subagent_stop.agent_id", "$.agent_id", "hook_event_name == \"SubagentStop\"", "string", "SubagentStop configured", J.String),
        Required("hook.subagent_stop.stop_hook_active", "$.stop_hook_active", "hook_event_name == \"SubagentStop\"", "boolean", "SubagentStop configured", J.Boolean),
        Required("hook.subagent_stop.agent_type", "$.agent_type", "hook_event_name == \"SubagentStop\"", "string", "SubagentStop configured", J.String),
        Required("hook.subagent_stop.agent_transcript_path", "$.agent_transcript_path", "hook_event_name == \"SubagentStop\"", "string path", "SubagentStop configured", J.String),
        Required("hook.subagent_stop.last_assistant_message", "$.last_assistant_message", "hook_event_name == \"SubagentStop\"", "string", "SubagentStop configured", J.String),
        Conditional("hook.subagent_stop.background_tasks", "$.background_tasks", "hook_event_name == \"SubagentStop\"", "array", "Claude Code v2.1.145 or later AND task registry is reachable", J.Array),
        Conditional("hook.subagent_stop.session_crons", "$.session_crons", "hook_event_name == \"SubagentStop\"", "array", "Claude Code v2.1.145 or later AND task registry is reachable", J.Array),
        Required("hook.stop.stop_hook_active", "$.stop_hook_active", "hook_event_name == \"Stop\"", "boolean", "Stop configured", J.Boolean),
        Required("hook.stop.last_assistant_message", "$.last_assistant_message", "hook_event_name == \"Stop\"", "string", "Stop configured", J.String),
        Conditional("hook.stop.background_tasks", "$.background_tasks", "hook_event_name == \"Stop\"", "array", "Claude Code v2.1.145 or later AND task registry is reachable", J.Array),
        Conditional("hook.stop.session_crons", "$.session_crons", "hook_event_name == \"Stop\"", "array", "Claude Code v2.1.145 or later AND task registry is reachable", J.Array),
        Required("hook.stop_failure.error", "$.error", "hook_event_name == \"StopFailure\"", "string enum", "StopFailure configured", J.String),
        Optional("hook.stop_failure.error_details", "$.error_details", "hook_event_name == \"StopFailure\"", "string", "producer emits error_details", J.String),
        Optional("hook.stop_failure.last_assistant_message", "$.last_assistant_message", "hook_event_name == \"StopFailure\"", "string", "producer emits last_assistant_message", J.String),
        Required("hook.session_end.reason", "$.reason", "hook_event_name == \"SessionEnd\"", "string enum", "SessionEnd configured", J.String),
    ];

    private static readonly FixtureContract[] FixtureContracts =
    [
        Accepted("hooks/session-start.json", "SessionStart",
            [S("session_id"), S("transcript_path"), S("cwd"), S("hook_event_name"), S("source"), S("model"), S("agent_type"), S("session_title")],
            ["hook.session_id", "hook.event_name", "hook.transcript_path", "hook.cwd", "hook.conditional_agent_type", "hook.session_start.source", "hook.session_start.model", "hook.session_start.session_title"]),
        Accepted("hooks/user-prompt-submit.json", "UserPromptSubmit",
            [S("session_id"), S("prompt_id"), S("transcript_path"), S("cwd"), S("permission_mode"), S("hook_event_name"), S("prompt")],
            ["hook.session_id", "hook.event_name", "hook.prompt_id", "hook.transcript_path", "hook.cwd", "hook.permission_mode", "hook.user_prompt_submit.prompt"]),
        Accepted("hooks/pre-tool-use.json", "PreToolUse",
            [S("session_id"), S("prompt_id"), S("transcript_path"), S("cwd"), S("permission_mode"), O("effort"), S("agent_id"), S("agent_type"), S("hook_event_name"), S("tool_name"), O("tool_input"), S("tool_use_id")],
            ["hook.session_id", "hook.event_name", "hook.prompt_id", "hook.transcript_path", "hook.cwd", "hook.permission_mode", "hook.effort_level", "hook.conditional_agent_id", "hook.conditional_agent_type", "hook.pre_tool_use.tool_name", "hook.pre_tool_use.tool_input", "hook.pre_tool_use.tool_use_id"]),
        Accepted("hooks/permission-request.json", "PermissionRequest",
            [S("session_id"), S("prompt_id"), S("transcript_path"), S("cwd"), S("permission_mode"), O("effort"), S("hook_event_name"), S("tool_name"), O("tool_input"), A("permission_suggestions")],
            ["hook.session_id", "hook.event_name", "hook.prompt_id", "hook.transcript_path", "hook.cwd", "hook.permission_mode", "hook.effort_level", "hook.permission_request.tool_name", "hook.permission_request.tool_input", "hook.permission_request.permission_suggestions"]),
        Accepted("hooks/post-tool-use.json", "PostToolUse",
            [S("session_id"), S("prompt_id"), S("transcript_path"), S("cwd"), S("permission_mode"), O("effort"), S("hook_event_name"), S("tool_name"), O("tool_input"), O("tool_response"), S("tool_use_id"), N("duration_ms")],
            ["hook.session_id", "hook.event_name", "hook.prompt_id", "hook.transcript_path", "hook.cwd", "hook.permission_mode", "hook.effort_level", "hook.post_tool_use.tool_name", "hook.post_tool_use.tool_input", "hook.post_tool_use.tool_use_id", "hook.post_tool_use.tool_response", "hook.post_tool_use.duration_ms"]),
        Accepted("hooks/post-tool-use-failure.json", "PostToolUseFailure",
            [S("session_id"), S("prompt_id"), S("transcript_path"), S("cwd"), S("permission_mode"), O("effort"), S("hook_event_name"), S("tool_name"), O("tool_input"), S("tool_use_id"), S("error"), B("is_interrupt"), N("duration_ms")],
            ["hook.session_id", "hook.event_name", "hook.prompt_id", "hook.transcript_path", "hook.cwd", "hook.permission_mode", "hook.effort_level", "hook.post_tool_failure.tool_name", "hook.post_tool_failure.tool_input", "hook.post_tool_failure.tool_use_id", "hook.post_tool_failure.error", "hook.post_tool_failure.is_interrupt", "hook.post_tool_failure.duration_ms"]),
        Accepted("hooks/subagent-start.json", "SubagentStart",
            [S("session_id"), S("prompt_id"), S("transcript_path"), S("cwd"), S("hook_event_name"), S("agent_id"), S("agent_type")],
            ["hook.session_id", "hook.event_name", "hook.prompt_id", "hook.transcript_path", "hook.cwd", "hook.subagent_start.agent_id", "hook.subagent_start.agent_type"]),
        Accepted("hooks/subagent-stop.json", "SubagentStop",
            [S("session_id"), S("prompt_id"), S("transcript_path"), S("cwd"), S("permission_mode"), O("effort"), S("hook_event_name"), B("stop_hook_active"), S("agent_id"), S("agent_type"), S("agent_transcript_path"), S("last_assistant_message"), A("background_tasks"), A("session_crons")],
            ["hook.session_id", "hook.event_name", "hook.prompt_id", "hook.transcript_path", "hook.cwd", "hook.permission_mode", "hook.effort_level", "hook.subagent_stop.agent_id", "hook.subagent_stop.stop_hook_active", "hook.subagent_stop.agent_type", "hook.subagent_stop.agent_transcript_path", "hook.subagent_stop.last_assistant_message", "hook.subagent_stop.background_tasks", "hook.subagent_stop.session_crons"]),
        Accepted("hooks/stop.json", "Stop",
            [S("session_id"), S("prompt_id"), S("transcript_path"), S("cwd"), S("permission_mode"), O("effort"), S("hook_event_name"), B("stop_hook_active"), S("last_assistant_message"), A("background_tasks"), A("session_crons")],
            ["hook.session_id", "hook.event_name", "hook.prompt_id", "hook.transcript_path", "hook.cwd", "hook.permission_mode", "hook.effort_level", "hook.stop.stop_hook_active", "hook.stop.last_assistant_message", "hook.stop.background_tasks", "hook.stop.session_crons"]),
        Accepted("hooks/stop-failure.json", "StopFailure",
            [S("session_id"), S("prompt_id"), S("transcript_path"), S("cwd"), S("hook_event_name"), S("error"), S("error_details"), S("last_assistant_message")],
            ["hook.session_id", "hook.event_name", "hook.prompt_id", "hook.transcript_path", "hook.cwd", "hook.stop_failure.error", "hook.stop_failure.error_details", "hook.stop_failure.last_assistant_message"]),
        Accepted("hooks/session-end.json", "SessionEnd",
            [S("session_id"), S("transcript_path"), S("cwd"), S("hook_event_name"), S("reason")],
            ["hook.session_id", "hook.event_name", "hook.transcript_path", "hook.cwd", "hook.session_end.reason"]),
        Rejected("hooks/unsupported-event.json", "Notification",
            [S("session_id"), S("prompt_id"), S("transcript_path"), S("cwd"), S("permission_mode"), S("hook_event_name"), S("message"), S("title"), S("notification_type")]),
    ];

    private static readonly OtelFixtureContract[] OtelFixtureContracts =
    [
        new("otel/content-disabled.json", "json", "documented-current-unversioned", "disabled_redacted_prompt", "schema_drift_detected"),
        new("otel/content-enabled.json", "json", "documented-current-unversioned", "enabled_synthetic_markers", "schema_drift_detected"),
        new("otel/unsupported-version.json", "json", "synthetic-unverified-version", "disabled_redacted_prompt", "schema_drift_detected"),
        new("otel/schema-drift.json", "json", "documented-current-unversioned", "disabled_redacted_prompt", "schema_drift_detected"),
        new("otel/content-disabled.bin", "protobuf", "documented-current-unversioned", "disabled_redacted_prompt", "schema_drift_detected"),
        new("otel/content-enabled.bin", "protobuf", "documented-current-unversioned", "enabled_synthetic_markers", "schema_drift_detected"),
    ];

    [Fact]
    public void Canonical_mapping_matches_independent_rows_allowlist_and_fixture_coverage()
    {
        var mapping = ReadObject(CanonicalMappingPath);
        var actualRows = mapping["fields"]!.AsArray();

        Assert.Equal(ExpectedAllowlist, mapping["event_type_allowlist"]!["values"]!.AsArray().Select(Value<string>));
        Assert.Equal("ordinal_case_sensitive", mapping["event_type_allowlist"]!["comparison"]!.GetValue<string>());
        Assert.Equal(ExpectedMappingRows.Length, actualRows.Count);

        for (var index = 0; index < ExpectedMappingRows.Length; index++)
        {
            var expected = ExpectedMappingRows[index];
            var actual = actualRows[index]!.AsObject();
            Assert.Equal(expected.FieldId, actual["field_id"]!.GetValue<string>());
            Assert.Equal(expected.ProducerPath, actual["producer_path"]!.GetValue<string>());
            Assert.Equal(expected.Selector, actual["selector"]!.GetValue<string>());
            Assert.Equal(expected.ProducerType, actual["producer_type"]!.GetValue<string>());
            Assert.Equal(expected.Requirement == Requirement.Required, actual["required"]!.GetValue<bool>());
            Assert.Equal(expected.ContentGate, actual["content_gate"]!.GetValue<string>());
        }

        var coveredRows = FixtureContracts
            .Where(contract => contract.Accepted)
            .SelectMany(contract => contract.MappingFieldIds)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(fieldId => Array.FindIndex(ExpectedMappingRows, row => row.FieldId == fieldId));
        Assert.Equal(ExpectedMappingRows.Select(row => row.FieldId), coveredRows);
    }

    [Fact]
    public void Independent_matrix_pins_required_optional_and_conditional_gates()
    {
        Assert.Equal(
            ["hook.session_start.model", "hook.session_start.session_title", "hook.permission_request.permission_suggestions", "hook.post_tool_use.duration_ms", "hook.post_tool_failure.is_interrupt", "hook.post_tool_failure.duration_ms", "hook.stop_failure.error_details", "hook.stop_failure.last_assistant_message"],
            ExpectedMappingRows.Where(row => row.Requirement == Requirement.Optional).Select(row => row.FieldId));
        Assert.Equal(
            ["hook.prompt_id", "hook.permission_mode", "hook.effort_level", "hook.conditional_agent_id", "hook.conditional_agent_type", "hook.subagent_stop.background_tasks", "hook.subagent_stop.session_crons", "hook.stop.background_tasks", "hook.stop.session_crons"],
            ExpectedMappingRows.Where(row => row.Requirement == Requirement.Conditional).Select(row => row.FieldId));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void Boolean_producer_type_accepts_both_json_boolean_values(string json)
    {
        AssertJsonType(JsonNode.Parse(json)!, J.Boolean);
    }

    [Theory]
    [MemberData(nameof(FixturePaths))]
    public void Fixture_matches_independent_event_matrix_and_every_referenced_mapping_row(string fixturePath)
    {
        var contract = Assert.Single(FixtureContracts, candidate => candidate.Path == fixturePath);
        var fixture = ReadObject(FixturePathOnDisk(fixturePath));

        Assert.Equal(contract.EventName, fixture["hook_event_name"]!.GetValue<string>());
        Assert.Equal(contract.Fields.Select(field => field.Name).Order(), fixture.Select(property => property.Key).Order());
        foreach (var field in contract.Fields)
        {
            AssertJsonType(fixture[field.Name]!, field.Type);
        }

        if (contract.Accepted)
        {
            var mappedTopLevelFields = contract.MappingFieldIds
                .Select(fieldId => Assert.Single(ExpectedMappingRows, row => row.FieldId == fieldId))
                .Select(row => row.ProducerPath[2..].Split('.')[0])
                .Distinct(StringComparer.Ordinal)
                .Order();
            Assert.Equal(contract.Fields.Select(field => field.Name).Order(), mappedTopLevelFields);

            foreach (var fieldId in contract.MappingFieldIds)
            {
                var row = Assert.Single(ExpectedMappingRows, candidate => candidate.FieldId == fieldId);
                AssertJsonType(Resolve(fixture, row.ProducerPath), row.JsonType);
            }
        }

        AssertNestedProducerShapes(fixture, contract.EventName);
        Assert.DoesNotContain(fixture, property => DerivedIdentityFields.Contains(property.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void Manifest_pins_unversioned_documentation_and_surface_specific_evidence_without_claiming_observation()
    {
        var manifest = ReadObject(ManifestPath);
        var producerContract = manifest["producer_contract"]!.AsObject();
        Assert.Equal(ExpectedProducerContractLabel, producerContract["label"]!.GetValue<string>());
        Assert.Null(producerContract["version"]);
        Assert.Equal(ExpectedSourceDocument, producerContract["source_document"]!.GetValue<string>());
        Assert.Equal(ExpectedMappingPath, producerContract["canonical_mapping"]!.GetValue<string>());
        Assert.Equal("documented_not_live_observed", producerContract["evidence_status"]!.GetValue<string>());

        var installed = manifest["installed_executable_evidence"]!.AsObject();
        Assert.Equal("2.1.207", installed["version"]!.GetValue<string>());
        Assert.Equal("inventory_label_only_not_hook_producer_version", installed["meaning"]!.GetValue<string>());
        Assert.Null(manifest["verified_hook_producer_version"]);

        var surfaces = manifest["surface_evidence"]!.AsObject();
        AssertSurface(surfaces, "interactive", "unverified", "docs/specifications/contracts/source-capabilities/v1/inventories/claude-code-interactive.json");
        AssertSurface(surfaces, "print", "executed_no_hook_capture", "docs/specifications/contracts/source-capabilities/v1/inventories/claude-code-print.json");
        AssertSurface(surfaces, "agent-sdk", "unverified", "docs/specifications/contracts/source-capabilities/v1/inventories/claude-agent-sdk.json");

        Assert.Equal(ExpectedAllowlist, manifest["ordered_event_allowlist"]!.AsArray().Select(Value<string>));
        var fixtures = manifest["fixtures"]!.AsArray();
        Assert.Equal(FixtureContracts.Select(contract => contract.Path), fixtures.Select(fixture => FixturePath(fixture!)));
        Assert.DoesNotContain(fixtures, fixture => FixturePath(fixture!) == "hooks/stop-error.json");

        var errorCapability = manifest["error_envelope_capability"]!.AsObject();
        Assert.Equal("StopFailure", errorCapability["documented_event_name"]!.GetValue<string>());
        Assert.Equal("hooks/stop-failure.json", errorCapability["fixture"]!.GetValue<string>());
        Assert.Equal("unavailable_not_documented", errorCapability["stop_error_alias"]!.GetValue<string>());
    }

    [Fact]
    public void Manifest_hashes_and_expected_adapter_outcomes_are_reproducible()
    {
        var fixtures = ReadObject(ManifestPath)["fixtures"]!.AsArray();
        foreach (var contract in FixtureContracts)
        {
            var entry = Assert.Single(fixtures, candidate => FixturePath(candidate!) == contract.Path)!.AsObject();
            var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(FixturePathOnDisk(contract.Path)))).ToLowerInvariant();
            Assert.Equal(contract.EventName, entry["hook_event_name"]!.GetValue<string>());
            Assert.Equal(contract.Accepted, entry["expected_acceptance"]!.GetValue<bool>());
            Assert.Equal(contract.Accepted ? null : "event_name_not_allowlisted", entry["expected_rejection_reason"]?.GetValue<string>());
            Assert.Equal(ExpectedAdapter, entry["expected_source_adapter"]!.GetValue<string>());
            Assert.Equal("synthetic_markers_only", entry["content_gate"]!.GetValue<string>());
            Assert.Equal(actualHash, entry["sha256"]!.GetValue<string>());
        }
    }

    [Theory]
    [MemberData(nameof(FixturePaths))]
    public void Fixture_contains_only_bounded_synthetic_values_and_no_sensitive_or_derived_material(string fixturePath)
    {
        var json = File.ReadAllText(FixturePathOnDisk(fixturePath));
        var fixture = JsonNode.Parse(json)!;
        Assert.InRange(json.Length, 2, 4096);
        Assert.DoesNotContain("@", json, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bearer", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", json, StringComparison.Ordinal);
        Assert.DoesNotContain("canonical_hash", json, StringComparison.Ordinal);
        Assert.DoesNotContain("source_event_id", json, StringComparison.Ordinal);
        foreach (var value in DescendantValues(fixture))
        {
            if (value.TryGetValue<string>(out var text))
            {
                Assert.InRange(text.Length, 1, 160);
            }
        }

        AssertSyntheticMarker(fixture["transcript_path"]);
        AssertSyntheticMarker(fixture["cwd"]);
        AssertSyntheticMarker(fixture["agent_transcript_path"]);
        AssertSyntheticMarker(fixture["prompt"]);
        AssertSyntheticMarker(fixture["last_assistant_message"]);
        AssertSyntheticMarker(fixture["error_details"]);
    }

    [Fact]
    public void Otel_manifest_pins_documented_provenance_without_claiming_live_capture()
    {
        var manifest = ReadObject(ManifestPath);
        var contract = manifest["otel_producer_contract"]!.AsObject();
        Assert.Equal("current-unversioned-claude-code-monitoring-documentation", contract["label"]!.GetValue<string>());
        Assert.Null(contract["version"]);
        Assert.Equal("https://code.claude.com/docs/en/monitoring-usage", contract["source_document"]!.GetValue<string>());
        Assert.Equal("docs/specifications/contracts/source-capabilities/v1/claude-code/otel-mapping.json", contract["canonical_mapping"]!.GetValue<string>());
        Assert.Equal(OtlpTraceSchema.Release, contract["otlp_descriptor_release"]!.GetValue<string>());
        Assert.Equal(OtlpTraceSchema.Commit, contract["otlp_descriptor_commit"]!.GetValue<string>());
        Assert.Equal("documented_not_live_observed", contract["evidence_status"]!.GetValue<string>());
        Assert.Null(manifest["verified_otel_producer_version"]);
        Assert.False(contract["live_capture_observed"]!.GetValue<bool>());
        Assert.Equal(
            "documentation-derived synthetic conformance envelopes, not captured producer payloads; matching protobuf files are descriptor-generated from the JSON semantics and byte-reproduced by ClaudeProducerFixtureContractTests",
            contract["conformance_basis"]!.GetValue<string>());

        var fixtures = manifest["otel_fixtures"]!.AsArray();
        Assert.Equal(OtelFixtureContracts.Select(item => item.Path), fixtures.Select(fixture => FixturePath(fixture!)));
    }

    [Fact]
    public void Otel_manifest_hashes_adapter_outcomes_and_unverified_version_policy_are_reproducible()
    {
        var fixtures = ReadObject(ManifestPath)["otel_fixtures"]!.AsArray();
        foreach (var contract in OtelFixtureContracts)
        {
            var entry = Assert.Single(fixtures, candidate => FixturePath(candidate!) == contract.Path)!.AsObject();
            var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(FixturePathOnDisk(contract.Path)))).ToLowerInvariant();
            Assert.Equal(contract.Encoding, entry["encoding"]!.GetValue<string>());
            Assert.Equal(contract.SourceVersionLabel, entry["source_version_label"]!.GetValue<string>());
            Assert.Equal(contract.ContentGate, entry["content_gate"]!.GetValue<string>());
            Assert.Equal(contract.ExpectedEmptyRegistryState, entry["expected_empty_registry_state"]!.GetValue<string>());
            Assert.Equal("claude-code-otel", entry["expected_source_adapter"]!.GetValue<string>());
            Assert.Equal("documentation_derived_conformance_fixture", entry["provenance"]!.GetValue<string>());
            Assert.Equal(actualHash, entry["sha256"]!.GetValue<string>());
        }

        var unverified = Assert.Single(fixtures, candidate => FixturePath(candidate!) == "otel/unsupported-version.json")!.AsObject();
        Assert.Equal("same_schema_unverified_evidence", unverified["evidence_label"]!.GetValue<string>());
        Assert.Equal("version_not_receive_allowlist", unverified["version_policy"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("otel/content-disabled.json")]
    [InlineData("otel/content-enabled.json")]
    [InlineData("otel/unsupported-version.json")]
    [InlineData("otel/schema-drift.json")]
    public void Otel_json_fixture_is_a_complete_export_trace_service_request(string fixturePath)
    {
        var request = ReadObject(FixturePathOnDisk(fixturePath));
        var resourceSpans = Assert.Single(request["resourceSpans"]!.AsArray())!.AsObject();
        Assert.NotNull(resourceSpans["resource"]!["attributes"]);
        var scopeSpans = Assert.Single(resourceSpans["scopeSpans"]!.AsArray())!.AsObject();
        Assert.Equal("com.anthropic.claude_code", scopeSpans["scope"]!["name"]!.GetValue<string>());
        Assert.NotEmpty(scopeSpans["scope"]!["version"]!.GetValue<string>());
        var spans = scopeSpans["spans"]!.AsArray();
        Assert.Equal(6, spans.Count);
        Assert.All(spans, span =>
        {
            var item = span!.AsObject();
            Assert.Matches("^[0-9a-f]{32}$", item["traceId"]!.GetValue<string>());
            Assert.Matches("^[0-9a-f]{16}$", item["spanId"]!.GetValue<string>());
            Assert.True(ulong.TryParse(item["startTimeUnixNano"]!.GetValue<string>(), out _));
            Assert.True(ulong.TryParse(item["endTimeUnixNano"]!.GetValue<string>(), out _));
        });
    }

    [Fact]
    public void Content_disabled_and_enabled_share_source_identity_and_recognized_semantics_but_gate_raw_content()
    {
        var disabled = ReadObject(FixturePathOnDisk("otel/content-disabled.json"));
        var enabled = ReadObject(FixturePathOnDisk("otel/content-enabled.json"));
        var disabledSpans = Spans(disabled);
        var enabledSpans = Spans(enabled);
        Assert.Equal(disabledSpans.Select(SourceIdentity), enabledSpans.Select(SourceIdentity));

        var disabledPrompt = Assert.Single(Attributes(disabledSpans[0]), pair => pair.Key == "user_prompt");
        Assert.Equal("<REDACTED>", StringValue(disabledPrompt.Value));
        var enabledPrompt = Assert.Single(Attributes(enabledSpans[0]), pair => pair.Key == "user_prompt");
        Assert.Equal("SYNTHETIC_USER_PROMPT", StringValue(enabledPrompt.Value));

        var gatedKeys = new[] { "file_path", "subagent_type", "response.model_output", "hook_definitions" };
        foreach (var key in gatedKeys)
        {
            Assert.DoesNotContain(disabledSpans.SelectMany(Attributes), pair => pair.Key == key);
            Assert.Contains(enabledSpans.SelectMany(Attributes), pair => pair.Key == key && StringValue(pair.Value).StartsWith("SYNTHETIC_", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(disabledSpans.SelectMany(Events), item => item["name"]!.GetValue<string>() == "tool.output");
        var output = Assert.Single(enabledSpans.SelectMany(Events), item => item["name"]!.GetValue<string>() == "tool.output");
        Assert.StartsWith("SYNTHETIC_", StringValue(Assert.Single(Attributes(output), pair => pair.Key == "content").Value), StringComparison.Ordinal);

        AssertRecognizedSemanticsEqual(disabledSpans, enabledSpans, [.. gatedKeys, "user_prompt"]);
    }

    [Theory]
    [InlineData("otel/content-disabled.json", false)]
    [InlineData("otel/content-enabled.json", true)]
    public void Otel_fixture_matches_independent_official_per_span_matrix(string fixturePath, bool contentEnabled)
    {
        var spans = Spans(ReadObject(FixturePathOnDisk(fixturePath)));
        var expected = new[]
        {
            OfficialSpan("claude_code.interaction", "1111111111111111", null, "1000000000", "1600000000",
                CommonAttributes("claude_code.interaction", ("user_prompt", contentEnabled ? "SYNTHETIC_USER_PROMPT" : "<REDACTED>"))),
            OfficialSpan("claude_code.llm_request", "2222222222222222", "1111111111111111", "1050000000", "1300000000",
                CommonAttributes("claude_code.llm_request", ("gen_ai.request.model", "SYNTHETIC_MODEL"), ("ttft_ms", 12.5),
                    ("input_tokens", 12L), ("output_tokens", 7L), ("cache_read_tokens", 3L), ("cache_creation_tokens", 2L),
                    ("attempt", 2L), ("agent_id", "SYNTHETIC_AGENT_001"), ("parent_agent_id", "SYNTHETIC_AGENT_PARENT"),
                    contentEnabled ? ("response.model_output", "SYNTHETIC_MODEL_OUTPUT") : default),
                expectedEvents: [OfficialEvent("gen_ai.request.attempt", "1060000000", new Dictionary<string, object> { ["attempt"] = 2L })]),
            OfficialSpan("claude_code.tool", "3333333333333333", "1111111111111111", "1310000000", "1500000000",
                CommonAttributes("claude_code.tool", ("tool_name", "SYNTHETIC_TOOL"), ("tool_use_id", "SYNTHETIC_TOOL_USE_001"),
                    contentEnabled ? ("file_path", "SYNTHETIC_RELATIVE_PATH") : default,
                    contentEnabled ? ("subagent_type", "SYNTHETIC_SUBAGENT_TYPE") : default),
                expectedEvents: contentEnabled ? [OfficialEvent("tool.output", "1490000000", new Dictionary<string, object> { ["content"] = "SYNTHETIC_TOOL_OUTPUT" })] : []),
            OfficialSpan("claude_code.tool.blocked_on_user", "4444444444444444", "3333333333333333", "1320000000", "1360000000",
                CommonAttributes("claude_code.tool.blocked_on_user", ("duration_ms", 40.0), ("decision", "accept"))),
            OfficialSpan("claude_code.tool.execution", "5555555555555555", "3333333333333333", "1370000000", "1490000000",
                CommonAttributes("claude_code.tool.execution", ("success", true))),
            OfficialSpan("claude_code.hook", "6666666666666666", "1111111111111111", "1510000000", "1550000000",
                CommonAttributes("claude_code.hook", contentEnabled ? ("hook_definitions", "SYNTHETIC_HOOK_DEFINITIONS") : default)),
        };

        Assert.Equal(expected.Length, spans.Count);
        for (var index = 0; index < expected.Length; index++) AssertOfficialSpan(expected[index], spans[index]!.AsObject());
        Assert.DoesNotContain(spans.SelectMany(Attributes), pair => pair.Key == "reasoning_tokens");
    }

    [Fact]
    public void Unverified_version_and_drift_fixtures_use_actual_empty_registry_compatibility_policy()
    {
        const string ExpectedBaselineFingerprint = "1086ea6fa32981e7430d1fc314824c3a10a2c24840876ffc376a5bdf37001154";
        const string ExpectedDriftFingerprint = "d4693fd54ceb9989084e2da663e2e59dd9d7f27ac45a658c490182b1f199ea49";
        var baseline = OtlpJsonStructuralWalker.Build(File.ReadAllText(FixturePathOnDisk("otel/content-disabled.json")));
        var unverified = OtlpJsonStructuralWalker.Build(File.ReadAllText(FixturePathOnDisk("otel/unsupported-version.json")));
        var drift = OtlpJsonStructuralWalker.Build(File.ReadAllText(FixturePathOnDisk("otel/schema-drift.json")));
        Assert.Equal(ExpectedBaselineFingerprint, baseline.SchemaFingerprint);
        Assert.Equal(ExpectedBaselineFingerprint, unverified.SchemaFingerprint);
        Assert.Equal(ExpectedDriftFingerprint, drift.SchemaFingerprint);
        Assert.NotEqual(baseline.SchemaFingerprint, drift.SchemaFingerprint);

        var emptyRegistry = VerifiedSourceFingerprintRegistry.Create([], [], []);
        foreach (var (inventory, version) in new[] { (baseline, "documented-current-unversioned"), (unverified, "synthetic-unverified-version"), (drift, "documented-current-unversioned") })
        {
            var decision = SourceCompatibilityEvaluator.Assess("claude-code", version, inventory, 0, emptyRegistry);
            Assert.Equal(SourceCompatibilityState.SchemaDriftDetected, decision.State);
            Assert.NotEqual(SourceCompatibilityState.UnsupportedSourceVersion, decision.State);
        }
    }

    [Theory]
    [InlineData("content-disabled")]
    [InlineData("content-enabled")]
    public void Json_and_protobuf_fixtures_are_semantically_equivalent_and_binary_is_reproducible(string fixtureName)
    {
        var json = ReadObject(FixturePathOnDisk($"otel/{fixtureName}.json"));
        var expectedBinary = EncodeEnvelope(json, SourceStructuralEnvelope.Request);
        var committedBinary = File.ReadAllBytes(FixturePathOnDisk($"otel/{fixtureName}.bin"));
        Assert.Equal(expectedBinary, committedBinary);

        var decoded = JsonNode.Parse(OtlpProtobufTraceConverter.ConvertTraceRequestToRawOtlpJson(committedBinary))!.AsObject();
        Assert.True(JsonNode.DeepEquals(json, decoded), $"Decoded protobuf differs from {fixtureName}.json");
    }

    [Fact]
    public void Version_and_schema_variants_change_only_the_cited_version_or_unknown_structure()
    {
        var baseline = ReadObject(FixturePathOnDisk("otel/content-disabled.json"));
        var unverified = ReadObject(FixturePathOnDisk("otel/unsupported-version.json"));
        var drift = ReadObject(FixturePathOnDisk("otel/schema-drift.json"));

        Assert.Equal("documented-current-unversioned", ResourceString(baseline, "service.version"));
        Assert.Equal("synthetic-unverified-version", ResourceString(unverified, "service.version"));
        SetResourceString(unverified, "service.version", "documented-current-unversioned");
        Assert.True(JsonNode.DeepEquals(baseline, unverified));

        var driftAttributes = Spans(drift)[0]!["attributes"]!.AsArray();
        var driftAttribute = Assert.Single(driftAttributes, item => item!["key"]!.GetValue<string>() == "synthetic.schema_drift_marker");
        driftAttributes.Remove(driftAttribute);
        Assert.True(JsonNode.DeepEquals(baseline, drift));
    }

    [Theory]
    [MemberData(nameof(OtelFixturePaths))]
    public void Otel_fixture_contains_only_bounded_synthetic_content_and_no_sensitive_material(string fixturePath)
    {
        var bytes = File.ReadAllBytes(FixturePathOnDisk(fixturePath));
        Assert.InRange(bytes.Length, 1, 32 * 1024);
        var searchable = fixturePath.EndsWith(".json", StringComparison.Ordinal) ? Encoding.UTF8.GetString(bytes) : OtlpProtobufTraceConverter.ConvertTraceRequestToRawOtlpJson(bytes);
        Assert.DoesNotContain("@", searchable, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", searchable, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", searchable, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bearer", searchable, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\", searchable, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/", searchable, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", searchable, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant-", searchable, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string> FixturePaths
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var contract in FixtureContracts)
            {
                data.Add(contract.Path);
            }
            return data;
        }
    }

    public static TheoryData<string> OtelFixturePaths
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var contract in OtelFixtureContracts) data.Add(contract.Path);
            return data;
        }
    }

    private static readonly string[] DerivedIdentityFields = ["canonical_hash", "source_event_id", "occurred_at", "parent_event_id", "trace_id", "explicit_link"];
    private static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "TestData", "Claude");
    private static string ManifestPath => Path.Combine(FixtureRoot, "fixture-manifest.json");
    private static string CanonicalMappingPath => Path.Combine(FixtureRoot, "canonical", "hook-mapping.json");

    private static MappingRow Required(string id, string path, string selector, string type, string gate, J jsonType) => new(id, path, selector, type, Requirement.Required, gate, jsonType);
    private static MappingRow Optional(string id, string path, string selector, string type, string gate, J jsonType) => new(id, path, selector, type, Requirement.Optional, gate, jsonType);
    private static MappingRow Conditional(string id, string path, string selector, string type, string gate, J jsonType) => new(id, path, selector, type, Requirement.Conditional, gate, jsonType);
    private static FixtureContract Accepted(string path, string eventName, FixtureField[] fields, string[] mappingIds) => new(path, eventName, true, fields, mappingIds);
    private static FixtureContract Rejected(string path, string eventName, FixtureField[] fields) => new(path, eventName, false, fields, []);
    private static FixtureField S(string name) => new(name, J.String);
    private static FixtureField O(string name) => new(name, J.Object);
    private static FixtureField A(string name) => new(name, J.Array);
    private static FixtureField B(string name) => new(name, J.Boolean);
    private static FixtureField N(string name) => new(name, J.Number);

    private static void AssertSurface(JsonObject surfaces, string name, string state, string inventory)
    {
        Assert.Equal(state, surfaces[name]!["state"]!.GetValue<string>());
        Assert.Equal(inventory, surfaces[name]!["inventory"]!.GetValue<string>());
        Assert.False(surfaces[name]!["hook_capture_observed"]!.GetValue<bool>());
    }

    private static void AssertNestedProducerShapes(JsonObject fixture, string eventName)
    {
        if (fixture["effort"] is JsonObject effort)
        {
            Assert.Equal(["level"], effort.Select(property => property.Key));
            AssertJsonType(effort["level"]!, J.String);
        }
        if (fixture["tool_input"] is JsonObject toolInput)
        {
            Assert.Equal(["command", "description"], toolInput.Select(property => property.Key));
            Assert.All(toolInput, property => AssertJsonType(property.Value!, J.String));
        }
        if (eventName == "PostToolUse")
        {
            var response = fixture["tool_response"]!.AsObject();
            Assert.Equal(["stdout", "stderr", "interrupted", "isImage"], response.Select(property => property.Key));
            AssertJsonType(response["stdout"]!, J.String);
            AssertJsonType(response["stderr"]!, J.String);
            AssertJsonType(response["interrupted"]!, J.Boolean);
            AssertJsonType(response["isImage"]!, J.Boolean);
        }
        if (eventName == "PermissionRequest")
        {
            var suggestion = Assert.Single(fixture["permission_suggestions"]!.AsArray())!.AsObject();
            Assert.Equal(["type", "rules", "behavior", "destination"], suggestion.Select(property => property.Key));
            var rule = Assert.Single(suggestion["rules"]!.AsArray())!.AsObject();
            Assert.Equal(["toolName", "ruleContent"], rule.Select(property => property.Key));
        }
    }

    private static void AssertJsonType(JsonNode node, J type)
    {
        var kind = node.GetValueKind();
        if (type == J.Any) return;
        if (type == J.Boolean)
        {
            Assert.True(kind is JsonValueKind.True or JsonValueKind.False, $"Expected boolean but found {kind}.");
            return;
        }
        var expected = type switch
        {
            J.String => JsonValueKind.String,
            J.Object => JsonValueKind.Object,
            J.Array => JsonValueKind.Array,
            J.Number => JsonValueKind.Number,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
        Assert.Equal(expected, kind);
    }

    private static JsonNode Resolve(JsonObject fixture, string producerPath)
    {
        JsonNode? current = fixture;
        foreach (var segment in producerPath[2..].Split('.')) current = current![segment];
        return Assert.IsAssignableFrom<JsonNode>(current);
    }

    private static JsonObject ReadObject(string path)
    {
        Assert.True(File.Exists(path), $"Missing Claude producer contract artifact: {path}");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static string FixturePathOnDisk(string path) => Path.Combine(FixtureRoot, path.Replace('/', Path.DirectorySeparatorChar));
    private static string FixturePath(JsonNode fixture) => fixture["path"]!.GetValue<string>();
    private static T Value<T>(JsonNode? node) => node!.GetValue<T>();

    private static IEnumerable<JsonValue> DescendantValues(JsonNode node)
    {
        if (node is JsonValue value) yield return value;
        var children = node switch
        {
            JsonObject jsonObject => jsonObject.Select(property => property.Value).OfType<JsonNode>(),
            JsonArray jsonArray => jsonArray.OfType<JsonNode>(),
            _ => [],
        };
        foreach (var child in children)
        foreach (var descendant in DescendantValues(child))
            yield return descendant;
    }

    private static void AssertSyntheticMarker(JsonNode? node)
    {
        if (node is not null) Assert.StartsWith("SYNTHETIC_", node.GetValue<string>(), StringComparison.Ordinal);
    }

    private static JsonArray Spans(JsonObject request) => request["resourceSpans"]![0]!["scopeSpans"]![0]!["spans"]!.AsArray();
    private static string SourceIdentity(JsonNode? span) => $"{span!["traceId"]}:{span["spanId"]}:{span["parentSpanId"]}:{span["name"]}:{span["startTimeUnixNano"]}:{span["endTimeUnixNano"]}";
    private static IEnumerable<KeyValuePair<string, JsonObject>> Attributes(JsonNode? envelope) =>
        envelope!["attributes"]?.AsArray().Select(item => item!.AsObject()).Select(item => KeyValuePair.Create(item["key"]!.GetValue<string>(), item["value"]!.AsObject())) ?? [];
    private static IEnumerable<JsonObject> Events(JsonNode? span) => span!["events"]?.AsArray().Select(item => item!.AsObject()) ?? [];
    private static string StringValue(JsonObject anyValue) => anyValue["stringValue"]!.GetValue<string>();

    private static void AssertRecognizedSemanticsEqual(JsonArray disabled, JsonArray enabled, IReadOnlyCollection<string> gatedKeys)
    {
        for (var index = 0; index < disabled.Count; index++)
        {
            var left = disabled[index]!.DeepClone().AsObject();
            var right = enabled[index]!.DeepClone().AsObject();
            left["attributes"] = new JsonArray(left["attributes"]!.AsArray()
                .Where(item => !gatedKeys.Contains(item!["key"]!.GetValue<string>(), StringComparer.Ordinal))
                .Select(item => item!.DeepClone()).ToArray());
            right["attributes"] = new JsonArray(right["attributes"]!.AsArray()
                .Where(item => !gatedKeys.Contains(item!["key"]!.GetValue<string>(), StringComparer.Ordinal))
                .Select(item => item!.DeepClone()).ToArray());
            var retainedEvents = new JsonArray((right["events"]?.AsArray() ?? [])
                .Where(item => item!["name"]!.GetValue<string>() != "tool.output")
                .Select(item => item!.DeepClone()).ToArray());
            if (retainedEvents.Count == 0 && left["events"] is null) right.Remove("events");
            else right["events"] = retainedEvents;
            Assert.True(JsonNode.DeepEquals(left, right), $"Recognized semantics differ for span {index}.");
        }
    }

    private static string ResourceString(JsonObject request, string key) =>
        StringValue(Assert.Single(Attributes(request["resourceSpans"]![0]!["resource"]), pair => pair.Key == key).Value);

    private static void SetResourceString(JsonObject request, string key, string value) =>
        Assert.Single(Attributes(request["resourceSpans"]![0]!["resource"]), pair => pair.Key == key).Value["stringValue"] = value;

    private static ExpectedOfficialSpan OfficialSpan(
        string name,
        string spanId,
        string? parentSpanId,
        string startTimeUnixNano,
        string endTimeUnixNano,
        IReadOnlyDictionary<string, object> attributes,
        IReadOnlyList<ExpectedOfficialEvent>? expectedEvents = null) =>
        new(name, spanId, parentSpanId, startTimeUnixNano, endTimeUnixNano, attributes, expectedEvents ?? []);

    private static ExpectedOfficialEvent OfficialEvent(
        string name,
        string timeUnixNano,
        IReadOnlyDictionary<string, object> attributes) => new(name, timeUnixNano, attributes);

    private static IReadOnlyDictionary<string, object> CommonAttributes(string spanName, params (string? Key, object? Value)[] extras)
    {
        var attributes = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["span.type"] = spanName,
            ["session.id"] = "SYNTHETIC_SESSION_001",
        };
        foreach (var (key, value) in extras)
        {
            if (key is not null) attributes.Add(key, value!);
        }
        return attributes;
    }

    private static void AssertOfficialSpan(ExpectedOfficialSpan expected, JsonObject actual)
    {
        var expectedFields = new List<string> { "traceId", "spanId" };
        if (expected.ParentSpanId is not null) expectedFields.Add("parentSpanId");
        expectedFields.AddRange(["name", "kind", "startTimeUnixNano", "endTimeUnixNano", "attributes"]);
        if (expected.Events.Count != 0) expectedFields.Add("events");
        expectedFields.Add("status");
        Assert.Equal(expectedFields.Order(), actual.Select(property => property.Key).Order());
        Assert.Equal("11111111111111111111111111111111", actual["traceId"]!.GetValue<string>());
        Assert.Equal(expected.SpanId, actual["spanId"]!.GetValue<string>());
        Assert.Equal(expected.ParentSpanId, actual["parentSpanId"]?.GetValue<string>());
        Assert.Equal(expected.Name, actual["name"]!.GetValue<string>());
        Assert.Equal(1, actual["kind"]!.GetValue<int>());
        Assert.Equal(expected.StartTimeUnixNano, actual["startTimeUnixNano"]!.GetValue<string>());
        Assert.Equal(expected.EndTimeUnixNano, actual["endTimeUnixNano"]!.GetValue<string>());
        Assert.Equal(["code"], actual["status"]!.AsObject().Select(property => property.Key));
        Assert.Equal(0, actual["status"]!["code"]!.GetValue<int>());

        var actualAttributes = Attributes(actual).ToDictionary(pair => pair.Key, pair => AnyValue(pair.Value), StringComparer.Ordinal);
        Assert.Equal(expected.Attributes.Keys.Order(), actualAttributes.Keys.Order());
        foreach (var pair in expected.Attributes) Assert.Equal(pair.Value, actualAttributes[pair.Key]);

        var actualEvents = Events(actual).ToArray();
        Assert.Equal(expected.Events.Count, actualEvents.Length);
        for (var index = 0; index < expected.Events.Count; index++)
        {
            var expectedEvent = expected.Events[index];
            Assert.Equal(["timeUnixNano", "name", "attributes"], actualEvents[index].Select(property => property.Key));
            Assert.Equal(expectedEvent.TimeUnixNano, actualEvents[index]["timeUnixNano"]!.GetValue<string>());
            Assert.Equal(expectedEvent.Name, actualEvents[index]["name"]!.GetValue<string>());
            var eventAttributes = Attributes(actualEvents[index]).ToDictionary(pair => pair.Key, pair => AnyValue(pair.Value), StringComparer.Ordinal);
            Assert.Equal(expectedEvent.Attributes.Keys.Order(), eventAttributes.Keys.Order());
            foreach (var pair in expectedEvent.Attributes) Assert.Equal(pair.Value, eventAttributes[pair.Key]);
        }
    }

    private static object AnyValue(JsonObject value)
    {
        Assert.Single(value);
        if (value["stringValue"] is JsonNode stringValue) return stringValue.GetValue<string>();
        if (value["intValue"] is JsonNode intValue) return long.Parse(intValue.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        if (value["doubleValue"] is JsonNode doubleValue) return doubleValue.GetValue<double>();
        if (value["boolValue"] is JsonNode boolValue) return boolValue.GetValue<bool>();
        throw new Xunit.Sdk.XunitException($"Unsupported independent-matrix AnyValue: {value.ToJsonString()}");
    }

    private static byte[] EncodeEnvelope(JsonObject value, SourceStructuralEnvelope envelope)
    {
        using var stream = new MemoryStream();
        foreach (var property in value)
        {
            Assert.True(OtlpTraceSchema.TryGetField(envelope, property.Key, out var field), $"No OTLP descriptor field for {envelope}.{property.Key}");
            var nodes = field.JsonRepresentation == OtlpJsonRepresentation.Array ? property.Value!.AsArray().OfType<JsonNode>() : [property.Value!];
            foreach (var node in nodes) stream.Write(EncodeField(field, node));
        }
        return stream.ToArray();
    }

    private static byte[] EncodeField(OtlpTraceField field, JsonNode node)
    {
        var raw = field.Disposition == OtlpTraceFieldDisposition.ChildEnvelope
            ? EncodeEnvelope(node.AsObject(), field.ChildEnvelope!.Value)
            : EncodeScalar(field, node);
        return field.ProtobufWireType switch
        {
            OtlpProtobufWireType.LengthDelimited => LengthDelimited(field.ProtobufTag, raw),
            OtlpProtobufWireType.Varint => VarintField(field.ProtobufTag, ParseUInt64(field, node)),
            OtlpProtobufWireType.Fixed64 => Fixed64Field(field.ProtobufTag, ParseFixed64(field, node)),
            OtlpProtobufWireType.Fixed32 => Fixed32Field(field.ProtobufTag, node.GetValue<uint>()),
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private static byte[] EncodeScalar(OtlpTraceField field, JsonNode node) => field.SemanticType switch
    {
        SourceStructuralType.String => Encoding.UTF8.GetBytes(node.GetValue<string>()),
        SourceStructuralType.Bytes when field.FieldCode == "any_value.bytes" => Convert.FromBase64String(node.GetValue<string>()),
        SourceStructuralType.Bytes => Convert.FromHexString(node.GetValue<string>()),
        _ => [],
    };

    private static ulong ParseUInt64(OtlpTraceField field, JsonNode node) => field.SemanticType switch
    {
        SourceStructuralType.Bool => node.GetValue<bool>() ? 1UL : 0UL,
        SourceStructuralType.Int when field.FieldCode == "any_value.int" => unchecked((ulong)long.Parse(node.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)),
        _ => node.GetValue<ulong>(),
    };

    private static ulong ParseFixed64(OtlpTraceField field, JsonNode node) => field.SemanticType switch
    {
        SourceStructuralType.Double => unchecked((ulong)BitConverter.DoubleToInt64Bits(node.GetValue<double>())),
        _ => ulong.Parse(node.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
    };

    private static byte[] LengthDelimited(int tag, byte[] value) => Message(Varint(((ulong)tag << 3) | 2), Varint((ulong)value.Length), value);
    private static byte[] VarintField(int tag, ulong value) => Message(Varint((ulong)tag << 3), Varint(value));
    private static byte[] Fixed64Field(int tag, ulong value)
    {
        var bytes = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return Message(Varint(((ulong)tag << 3) | 1), bytes);
    }
    private static byte[] Fixed32Field(int tag, uint value)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return Message(Varint(((ulong)tag << 3) | 5), bytes);
    }
    private static byte[] Message(params byte[][] values) => values.SelectMany(value => value).ToArray();
    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var next = (byte)(value & 0x7f);
            value >>= 7;
            bytes.Add(value == 0 ? next : (byte)(next | 0x80));
        } while (value != 0);
        return bytes.ToArray();
    }

    private enum Requirement { Required, Optional, Conditional }
    private enum J { String, Object, Array, Number, Boolean, Any }
    private sealed record MappingRow(string FieldId, string ProducerPath, string Selector, string ProducerType, Requirement Requirement, string ContentGate, J JsonType);
    private sealed record FixtureField(string Name, J Type);
    private sealed record FixtureContract(string Path, string EventName, bool Accepted, FixtureField[] Fields, string[] MappingFieldIds);
    private sealed record OtelFixtureContract(string Path, string Encoding, string SourceVersionLabel, string ContentGate, string ExpectedEmptyRegistryState);
    private sealed record ExpectedOfficialSpan(
        string Name,
        string SpanId,
        string? ParentSpanId,
        string StartTimeUnixNano,
        string EndTimeUnixNano,
        IReadOnlyDictionary<string, object> Attributes,
        IReadOnlyList<ExpectedOfficialEvent> Events);
    private sealed record ExpectedOfficialEvent(string Name, string TimeUnixNano, IReadOnlyDictionary<string, object> Attributes);
}
