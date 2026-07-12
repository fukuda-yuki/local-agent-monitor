using System.Security.Cryptography;
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

    private enum Requirement { Required, Optional, Conditional }
    private enum J { String, Object, Array, Number, Boolean, Any }
    private sealed record MappingRow(string FieldId, string ProducerPath, string Selector, string ProducerType, Requirement Requirement, string ContentGate, J JsonType);
    private sealed record FixtureField(string Name, J Type);
    private sealed record FixtureContract(string Path, string EventName, bool Accepted, FixtureField[] Fields, string[] MappingFieldIds);
}
