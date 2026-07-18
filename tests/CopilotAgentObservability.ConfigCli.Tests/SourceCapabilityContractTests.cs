using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SourceCapabilityContractTests
{
    private static readonly SessionCompletenessEvidence FullCompletenessEvidence = new(
        HasNativeId: true,
        HasLifecycleStart: true,
        HasUserInstruction: true,
        HasSdkHookOrOtelEvidence: true,
        HasTerminalEvidence: true,
        HasExactLinkedOtelEnrichment: true,
        HasAllSurfaceRequiredEvidence: true,
        HasUnsupportedVersion: false,
        HasIngestGap: false);

    public static TheoryData<string, SessionCompletenessEvidence> RuntimeEquivalentReasons => new()
    {
        { "missing_native_session_id", FullCompletenessEvidence with { HasNativeId = false } },
        { "missing_trace_context", FullCompletenessEvidence with { HasExactLinkedOtelEnrichment = false } },
        { "trace_signal_disabled", FullCompletenessEvidence with { HasExactLinkedOtelEnrichment = false } },
        { "content_capture_disabled", FullCompletenessEvidence with { HasAllSurfaceRequiredEvidence = false } },
        { "unsupported_source_version", FullCompletenessEvidence with { HasUnsupportedVersion = true } },
        { "ingest_gap", FullCompletenessEvidence with { HasIngestGap = true } },
        { "hook_only", FullCompletenessEvidence with { HasExactLinkedOtelEnrichment = false } },
        { "unknown_span_kind", FullCompletenessEvidence with { HasExactLinkedOtelEnrichment = false } }
    };

    private static readonly string SchemaPath = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "docs", "specifications", "contracts", "source-capabilities", "v1",
        "source-capability-manifest.schema.json");

    private static readonly string ManifestsDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "docs", "specifications", "contracts", "source-capabilities", "v1",
        "manifests");

    private static readonly string ClaudeContractDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "docs", "specifications", "contracts", "source-capabilities", "v1",
        "claude-code");

    [Fact]
    public void Repository_manifests_declare_the_initial_distinct_producer_surfaces()
    {
        Assert.True(Directory.Exists(ManifestsDirectory), $"Missing source capability manifests: {ManifestsDirectory}");

        var manifestPaths = Directory.GetFiles(ManifestsDirectory, "*.json")
            .OrderBy(Path.GetFileName)
            .ToArray();

        Assert.Equal(
            [
                "claude-code.json",
                "codex-app.json",
                "codex-cli.json",
                "github-copilot-cli.json",
                "github-copilot-vscode.json"
            ],
            manifestPaths.Select(path => Path.GetFileName(path)!).ToArray());

        using var schema = JsonDocument.Parse(File.ReadAllText(SchemaPath));
        var manifests = manifestPaths.ToDictionary(
            path => Path.GetFileNameWithoutExtension(path)!,
            path => JsonDocument.Parse(File.ReadAllText(path)),
            StringComparer.Ordinal);

        try
        {
            foreach (var manifest in manifests.Values)
            {
                Assert.Empty(SourceCapabilityManifestValidator.Validate(schema, manifest));
            }

            Assert.Equal(5, manifests.Values.Select(manifest => manifest.RootElement.GetProperty("source_surface").GetString()).Distinct().Count());

            AssertManifestHeader(manifests["github-copilot-vscode"], "github-copilot-vscode", "otel-http+copilot-compatible-hook", "active", "stable");
            AssertManifestHeader(manifests["github-copilot-cli"], "github-copilot-cli", "otel-http+copilot-compatible-hook", "active", "stable");
            AssertManifestHeader(manifests["claude-code"], "claude-code", "claude-code-otel+claude-code-hook", "planned", "preview");
            AssertManifestHeader(manifests["codex-app"], "codex-app", "not-implemented", "planned", "preview");
            AssertManifestHeader(manifests["codex-cli"], "codex-cli", "not-implemented", "planned", "preview");

            AssertAvailabilityMatrix(manifests["github-copilot-vscode"], CopilotAvailabilityMatrix());
            AssertAvailabilityMatrix(manifests["github-copilot-cli"], CopilotAvailabilityMatrix());
            var claudeAvailability = UnknownAvailabilityMatrix();
            claudeAvailability["source_version_detector"] = "available";
            AssertAvailabilityMatrix(manifests["claude-code"], claudeAvailability);
            AssertAvailabilityMatrix(manifests["codex-app"], UnknownAvailabilityMatrix());
            AssertAvailabilityMatrix(manifests["codex-cli"], UnknownAvailabilityMatrix());

            Assert.NotEqual(
                manifests["codex-app"].RootElement.GetProperty("source_surface").GetString(),
                manifests["codex-cli"].RootElement.GetProperty("source_surface").GetString());
        }
        finally
        {
            foreach (var manifest in manifests.Values)
            {
                manifest.Dispose();
            }
        }
    }

    [Fact]
    public void Claude_mapping_rows_are_closed_single_leaves_and_derived_inputs_are_total()
    {
        string[] expectedRequiredFields =
        [
            "field_id", "producer_path", "selector", "producer_type", "required", "content_gate",
            "evidence_source", "evidence_status", "semantic_class", "disposition", "authority",
            "normalized_target", "raw_storage_target", "transform", "absence_behavior"
        ];
        string[] expectedOptionalFields = ["resolver_group"];
        string[] expectedDispositions = ["normalized", "raw_retained", "corroboration_only", "documented_unmapped"];
        Assert.Equal(ExpectedOtelFields.Concat(ExpectedHookFields).Select(field => field.FieldId).Order(StringComparer.Ordinal),
            ExpectedFieldDetails.Keys.Order(StringComparer.Ordinal));

        foreach (var (fileName, expectedFields, expectedDerived) in new[]
        {
            ("otel-mapping.json", ExpectedOtelFields, ExpectedOtelDerivedMappings),
            ("hook-mapping.json", ExpectedHookFields, ExpectedHookDerivedMappings)
        })
        {
            using var mapping = ReadClaudeMapping(fileName);
            var root = mapping.RootElement;
            var rowSchema = root.GetProperty("row_schema");
            Assert.Equal(expectedRequiredFields, rowSchema.GetProperty("required_fields").EnumerateArray().Select(Value).ToArray());
            Assert.Equal(expectedOptionalFields, rowSchema.GetProperty("optional_fields").EnumerateArray().Select(Value).ToArray());
            Assert.Equal(expectedDispositions, rowSchema.GetProperty("dispositions").EnumerateArray().Select(Value).ToArray());

            var fields = root.GetProperty("fields").EnumerateArray().ToArray();
            Assert.Equal(expectedFields.Length, fields.Length);
            for (var index = 0; index < fields.Length; index++)
            {
                var field = fields[index];
                var expectedProperties = expectedFields[index].ResolverGroup is null
                    ? expectedRequiredFields
                    : expectedRequiredFields.Concat(expectedOptionalFields).ToArray();
                Assert.Equal(expectedProperties.Order(StringComparer.Ordinal),
                    field.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
                AssertFieldEquals(expectedFields[index], field);
            }

            var derivedMappings = root.GetProperty("derived_mappings").EnumerateArray().ToArray();
            Assert.Equal(expectedDerived.Length, derivedMappings.Length);
            for (var index = 0; index < derivedMappings.Length; index++)
            {
                AssertDerivedMappingEquals(expectedDerived[index], derivedMappings[index]);
            }
        }
    }

    [Fact]
    public void Claude_mapping_metadata_property_closures_and_multisets_are_exact()
    {
        using var otel = ReadClaudeMapping("otel-mapping.json");
        using var hook = ReadClaudeMapping("hook-mapping.json");
        Assert.Equal(
            ["contract_version", "source_surface", "source_adapter", "registry_label", "mapping_status", "manifest_rule", "transport", "row_schema", "evidence_sources", "fields", "derived_mappings", "surface_validation"],
            otel.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["contract_version", "source_surface", "source_adapter", "registry_label", "mapping_status", "manifest_rule", "producer_transport", "hook_selection", "event_type_allowlist", "strict_session_envelope", "row_schema", "evidence_sources", "fields", "derived_mappings", "documented_unmapped_envelope_fields", "surface_validation"],
            hook.RootElement.EnumerateObject().Select(property => property.Name));

        AssertMappingHeader(otel.RootElement, "claude-code-otel", "print_mode_live_observed",
            "Official documentation supplies candidate field semantics only. Manifest availability changes only from approved observed producer evidence.");
        AssertMappingHeader(hook.RootElement, "claude-code-hook", "documentation_only_not_live_observed",
            "Official Hook documentation supplies candidate field semantics only. Manifest availability changes only from approved observed producer evidence.");
        AssertJsonExact(otel.RootElement.GetProperty("transport"), """{"implementation_path":"otel-http","signal":"trace","receiver_path":"/v1/traces","protocols":["http/json","http/protobuf"],"envelope":"opentelemetry.proto.collector.trace.v1.ExportTraceServiceRequest"}""");
        AssertJsonExact(hook.RootElement.GetProperty("producer_transport"), """{"implementation_path":"copilot-compatible-hook","command_hook":"one JSON object on stdin","http_hook":"the same JSON object as the POST body"}""");
        AssertJsonExact(hook.RootElement.GetProperty("hook_selection"), """{"criterion":"Select an event-specific row only when the producer object's top-level $.hook_event_name is exactly equal, by ordinal case-sensitive comparison, to that row's selector event name.","fallback":"No normalization, aliasing, field-shape inference, or fallback event type is allowed."}""");
        AssertJsonExact(hook.RootElement.GetProperty("event_type_allowlist"), """{"comparison":"ordinal_case_sensitive","values":["SessionStart","UserPromptSubmit","PreToolUse","PermissionRequest","PostToolUse","PostToolUseFailure","SubagentStart","SubagentStop","Stop","StopFailure","SessionEnd"],"evidence_source":"official-hooks","evidence_status":"documented_not_live_observed","absence_behavior":"Reject conversion for any event name outside this exact ordered set; never infer an event from payload shape."}""");
        AssertJsonExact(hook.RootElement.GetProperty("strict_session_envelope"), """{"receiver_path":"/api/session-ingest/v1/events","version_header":"X-CAO-Session-Event-Version: 1","schema_version":1,"source_adapter":"claude-code-hook","source_surface":"claude-code","batch_size":1,"parent_event_id":null,"trace_id":null,"explicit_link":null,"implementation_gap":"SessionIngestEnvelope.SourceSurface exists, but current SessionIngestValidation and SessionSourceSurface do not yet accept claude-code; Task10B owns that seam."}""");

        AssertJsonExact(otel.RootElement.GetProperty("evidence_sources"), """[{"id":"official-monitoring","kind":"official_documentation","reference":"https://code.claude.com/docs/en/monitoring-usage","evidence_status":"documented_not_live_observed"},{"id":"inventory-interactive","kind":"approved_inventory","reference":"../inventories/claude-code-interactive.json","evidence_status":"blocked_no_producer_structure_observed"},{"id":"inventory-print","kind":"approved_inventory","reference":"../inventories/claude-code-print.json","evidence_status":"live_observed_print_structure"},{"id":"live-print-observation","kind":"live_observation","reference":"../../../../../sprints/issue-99-claude-live-validation.md","evidence_status":"live_observed"},{"id":"live-prompt-capture-observation","kind":"live_observation","reference":"../../../../../sprints/issue-106-claude-live-validation-followup.md","evidence_status":"live_observed"},{"id":"inventory-agent-sdk","kind":"approved_inventory","reference":"../inventories/claude-agent-sdk.json","evidence_status":"blocked_no_producer_structure_observed"},{"id":"no-producer-evidence","kind":"absence_record","reference":"../inventories/claude-code-print.json","evidence_status":"not_documented_or_live_observed"}]""");
        AssertJsonExact(hook.RootElement.GetProperty("evidence_sources"), """[{"id":"official-hooks","kind":"official_documentation","reference":"https://code.claude.com/docs/en/hooks","evidence_status":"documented_not_live_observed"},{"id":"canonical-session-envelope","kind":"canonical_specification","reference":"../../../../interfaces/canvas-session-workspace.md","evidence_status":"approved_contract"},{"id":"canonical-claude-contract","kind":"canonical_specification","reference":"../../../../interfaces/source-schema-drift-claude-code.md","evidence_status":"approved_contract"},{"id":"inventory-interactive","kind":"approved_inventory","reference":"../inventories/claude-code-interactive.json","evidence_status":"blocked_no_hook_structure_observed"},{"id":"inventory-print","kind":"approved_inventory","reference":"../inventories/claude-code-print.json","evidence_status":"completed_no_hook_structure_observed"},{"id":"inventory-agent-sdk","kind":"approved_inventory","reference":"../inventories/claude-agent-sdk.json","evidence_status":"blocked_no_hook_structure_observed"}]""");
        AssertJsonExact(otel.RootElement.GetProperty("surface_validation"), """[{"surface_mode":"interactive","state":"unverified","evidence_source":"inventory-interactive"},{"surface_mode":"print","state":"executed_structural_telemetry_observed","evidence_source":"inventory-print"},{"surface_mode":"agent-sdk","state":"unverified","evidence_source":"inventory-agent-sdk"}]""");
        AssertJsonExact(hook.RootElement.GetProperty("surface_validation"), """[{"surface_mode":"interactive","state":"unverified","evidence_source":"inventory-interactive"},{"surface_mode":"print","state":"executed_without_hook_capture","evidence_source":"inventory-print"},{"surface_mode":"agent-sdk","state":"unverified","evidence_source":"inventory-agent-sdk"}]""");
        AssertJsonExact(hook.RootElement.GetProperty("documented_unmapped_envelope_fields"), """[{"field":"CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEvent.ParentEventId","reason":"No generic Claude Hook parent event ID is documented or observed.","absence_behavior":"Set null; never infer from arrival order, agent ID, or timing."},{"field":"CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEvent.TraceId","reason":"No complete Claude Hook trace context is documented or observed; a trace ID alone is insufficient.","absence_behavior":"Set null and leave trace-context binding unavailable."},{"field":"CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEnvelope.ExplicitLink","reason":"SessionStart source=resume does not identify the target native session ID.","absence_behavior":"Set null; never infer target from paths, history, or time."}]""");

        AssertFieldMultisets(ExpectedOtelFields, otel.RootElement.GetProperty("fields"));
        AssertFieldMultisets(ExpectedHookFields, hook.RootElement.GetProperty("fields"));
    }

    [Fact]
    public void Claude_otel_authority_targets_and_status_resolution_are_exact()
    {
        using var mapping = ReadClaudeMapping("otel-mapping.json");
        var root = mapping.RootElement;
        var fields = IndexFields(root);

        AssertField(fields, "otel.trace_id", "normalized", "otel_identity", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.TraceId", null);
        AssertField(fields, "otel.span_id", "normalized", "otel_identity", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.SpanId", null);
        AssertField(fields, "otel.parent_span_id", "normalized", "otel_hierarchy", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.ParentSpanId", null);
        AssertField(fields, "otel.start_time", "normalized", "otel_timing", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.StartTime", null);
        AssertField(fields, "otel.end_time", "normalized", "otel_timing", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.EndTime", null);
        AssertField(fields, "otel.agent_id", "documented_unmapped", "none", null, "CopilotAgentObservability.Telemetry.RawTelemetryRecord.PayloadJson");
        AssertField(fields, "otel.parent_agent_id", "documented_unmapped", "none", null, "CopilotAgentObservability.Telemetry.RawTelemetryRecord.PayloadJson");
        Assert.Contains("is not MonitorSpanProjection.AgentName", Value(fields["otel.agent_id"].GetProperty("transform")), StringComparison.Ordinal);

        var rawOnly = new[] { "otel.status_message", "otel.tool_error_category", "otel.tool_error_detail", "otel.user_prompt", "otel.tool_output_event", "otel.file_path", "otel.response_model_output", "otel.hook_definitions" };
        Assert.All(rawOnly, id =>
        {
            Assert.Null(NullableString(fields[id], "normalized_target"));
            Assert.Equal("CopilotAgentObservability.Telemetry.RawTelemetryRecord.PayloadJson", NullableString(fields[id], "raw_storage_target"));
        });

        var statusElement = Assert.Single(root.GetProperty("derived_mappings").EnumerateArray(), item => Value(item.GetProperty("mapping_id")) == "otel.status_resolution");
        var status = ClaudeStatusContract.Parse(statusElement);
        Assert.Equal("CopilotAgentObservability.Telemetry.MonitorSpanProjection.Status", status.Target);
        Assert.Equal("none", status.CorroborationPersistence);
        Assert.Null(status.RawStorageTarget);
        Assert.Null(status.DiagnosticTarget);
        Assert.Null(status.ReasonCodeTarget);
        Assert.False(statusElement.TryGetProperty("diagnostic_target", out _));
        Assert.False(statusElement.TryGetProperty("reason_code_target", out _));
        Assert.Equal("Apply the five ordered cases; evaluate tool_success agreement/conflict transiently for contract validation only. Never persist the evaluation, invent a target, or emit a reason code.", status.Transform);

        Assert.Equal(new("error", "agree_or_missing_not_persisted"), status.Resolve("ERROR", false));
        Assert.Equal(new("error", "agree_or_missing_not_persisted"), status.Resolve("ERROR", null));
        Assert.Equal(new("error", "conflict_not_persisted_status_wins"), status.Resolve("ERROR", true));
        foreach (var code in new[] { "OK", "UNSET" })
        {
            Assert.Equal(new("ok", "agree_or_missing_not_persisted"), status.Resolve(code, true));
            Assert.Equal(new("ok", "agree_or_missing_not_persisted"), status.Resolve(code, null));
            Assert.Equal(new("ok", "conflict_not_persisted_status_wins"), status.Resolve(code, false));
        }
        foreach (var code in new string?[] { null, "INVALID" })
        foreach (var success in new bool?[] { null, false, true })
        {
            Assert.Equal(new(null, "no_fallback_not_persisted"), status.Resolve(code, success));
        }
        Assert.Contains("never a fallback", Value(fields["otel.status_code"].GetProperty("absence_behavior")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Claude_hook_contract_rejects_missing_or_wrong_typed_required_fields_and_allows_optional_omissions()
    {
        using var mapping = ReadClaudeMapping("hook-mapping.json");
        var validator = ClaudeHookContractValidator.Create(mapping.RootElement);
        Assert.Equal(ExpectedHookEvents, validator.AllowedEvents);
        Assert.Equal(ExpectedHookEvents, ExpectedHookApplicability.Select(item => item.EventName));
        Assert.Equal(70, ExpectedHookApplicability.Sum(item => item.RequiredFieldIds.Length));
        Assert.Equal(69, ExpectedHookApplicability.Sum(item => item.RequiredFieldIds.Count(id => Field(id).ProducerType != "producer-defined JSON value")));
        Assert.Equal(63, ExpectedHookApplicability.Sum(item => item.OptionalFieldIds.Length));

        foreach (var applicability in ExpectedHookApplicability)
        {
            var eventName = applicability.EventName;
            var envelope = CreateHookEnvelope(eventName);
            Assert.Empty(validator.Validate(envelope));

            var applicable = applicability.RequiredFieldIds.Select(Field).ToArray();
            var optional = applicability.OptionalFieldIds.Select(Field).ToArray();
            Assert.NotEmpty(applicable);
            Assert.All(optional, field => Assert.False(PathExists(envelope, field.ProducerPath),
                $"Generated {eventName} envelope must prove optional omission for {field.FieldId}."));
            foreach (var field in applicable)
            {
                var missing = Clone(envelope);
                RemovePath(missing, field.ProducerPath);
                Assert.Contains($"{field.ProducerPath} is required.", validator.Validate(missing));

                if (field.ProducerType != "producer-defined JSON value")
                {
                    var wrongType = Clone(envelope);
                    SetPath(wrongType, field.ProducerPath, WrongTypeValue(field.ProducerType));
                    Assert.Contains($"{field.ProducerPath} must be {field.ProducerType}.", validator.Validate(wrongType));
                }
            }

            foreach (var field in optional)
            {
                var present = Clone(envelope);
                SetPath(present, field.ProducerPath, ValidValue(field.ProducerType));
                Assert.Empty(validator.Validate(present));

                var wrongType = Clone(envelope);
                SetPath(wrongType, field.ProducerPath, WrongTypeValue(field.ProducerType));
                Assert.Contains($"{field.ProducerPath} must be {field.ProducerType}.", validator.Validate(wrongType));
            }
        }

        var stopFailure = CreateHookEnvelope("StopFailure");
        Assert.DoesNotContain(ExpectedHookFields.Where(field => !field.Required).Select(field => field.ProducerPath),
            path => PathExists(stopFailure, path));
        Assert.Empty(validator.Validate(stopFailure));
        Assert.Contains("$.hook_event_name is not an exact allowed event name.", validator.Validate(CreateHookEnvelope("stopfailure")));

        using var unknownSelector = JsonDocument.Parse(File.ReadAllText(Path.Combine(ClaudeContractDirectory, "hook-mapping.json"))
            .Replace("every configured Hook event", "unknown selector", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => ClaudeHookContractValidator.Create(unknownSelector.RootElement));
        using var unknownType = JsonDocument.Parse(File.ReadAllText(Path.Combine(ClaudeContractDirectory, "hook-mapping.json"))
            .Replace("\"producer_type\": \"string\"", "\"producer_type\": \"mystery\"", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => ClaudeHookContractValidator.Create(unknownType.RootElement));
    }

    [Fact]
    public void Claude_hook_identity_timing_status_and_adapter_vocabulary_preserve_authority_boundaries()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(ManifestsDirectory, "claude-code.json")));
        using var otel = ReadClaudeMapping("otel-mapping.json");
        using var hook = ReadClaudeMapping("hook-mapping.json");
        var fields = IndexFields(hook.RootElement);

        Assert.Equal("claude-code-otel+claude-code-hook", Value(manifest.RootElement.GetProperty("source_adapter")));
        Assert.Equal("claude-code-otel", Value(otel.RootElement.GetProperty("source_adapter")));
        Assert.Equal("claude-code-hook", Value(hook.RootElement.GetProperty("source_adapter")));
        Assert.Equal("claude-code-hook", Value(hook.RootElement.GetProperty("strict_session_envelope").GetProperty("source_adapter")));
        Assert.DoesNotContain("claude-code-otel+claude-code-hook", hook.RootElement.GetProperty("strict_session_envelope").GetRawText(), StringComparison.Ordinal);

        foreach (var id in new[] { "hook.conditional_agent_id", "hook.subagent_start.agent_id", "hook.subagent_stop.agent_id" })
        {
            AssertField(fields, id, "normalized", "hook_native_run_identity", "CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEvent.RunNativeId", null);
            Assert.Contains("Copy exactly", Value(fields[id].GetProperty("transform")), StringComparison.Ordinal);
            Assert.DoesNotContain("AgentName", fields[id].GetRawText(), StringComparison.Ordinal);
        }

        foreach (var id in new[] { "hook.post_tool_use.duration_ms", "hook.post_tool_failure.duration_ms", "hook.post_tool_failure.error", "hook.stop_failure.error", "hook.session_end.reason" })
        {
            Assert.Null(NullableString(fields[id], "normalized_target"));
            Assert.DoesNotContain("MonitorSpanProjection.Status", fields[id].GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("MonitorSpanProjection.DurationMs", fields[id].GetRawText(), StringComparison.Ordinal);
        }

        var captureTime = Assert.Single(hook.RootElement.GetProperty("derived_mappings").EnumerateArray(), item => Value(item.GetProperty("mapping_id")) == "hook.capture_time");
        Assert.Equal("transport_metadata_only", Value(captureTime.GetProperty("authority")));
        Assert.Contains("never treat it as producer timing, duration, ordering, ownership, or binding evidence", Value(captureTime.GetProperty("transform")), StringComparison.Ordinal);
    }

    [Fact]
    public void Claude_documentation_only_evidence_remains_unknown_and_exact_binding_is_closed()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(ManifestsDirectory, "claude-code.json")));
        using var otel = ReadClaudeMapping("otel-mapping.json");
        using var hook = ReadClaudeMapping("hook-mapping.json");
        var expectedSurfaceStates = new[] { "unverified", "executed_structural_telemetry_observed", "unverified" };

        Assert.Equal(expectedSurfaceStates, otel.RootElement.GetProperty("surface_validation").EnumerateArray().Select(item => Value(item.GetProperty("state"))).ToArray());
        Assert.Equal(["unverified", "executed_without_hook_capture", "unverified"], hook.RootElement.GetProperty("surface_validation").EnumerateArray().Select(item => Value(item.GetProperty("state"))).ToArray());
        Assert.All(IndexFields(otel.RootElement).Values, field => Assert.Contains(
            Value(field.GetProperty("evidence_status")),
            new[] { "documented_not_live_observed", "not_documented_or_live_observed", "live_observed" }));
        Assert.Equal("live_observed", Value(IndexFields(otel.RootElement)["otel.span_name"].GetProperty("evidence_status")));
        Assert.Equal("not_documented_or_live_observed", Value(IndexFields(otel.RootElement)["otel.reasoning_tokens"].GetProperty("evidence_status")));
        Assert.All(IndexFields(hook.RootElement).Values, field => Assert.Equal("documented_not_live_observed", Value(field.GetProperty("evidence_status"))));

        var availability = UnknownAvailabilityMatrix();
        availability["source_version_detector"] = "available";
        AssertAvailabilityMatrix(manifest, availability);

        var binding = ReadRepositoryDocument("docs/specifications/contracts/source-capabilities/v1/claude-code/exact-binding.md");
        var bindingRows = ParseBindingRows(binding);
        Assert.Equal(ExpectedBindingRows, bindingRows);
        var evaluator = new ClaudeBindingContractEvaluator(bindingRows);

        foreach (var hasHook in new[] { false, true })
        foreach (var hasOtel in new[] { false, true })
        foreach (var matchCount in new[] { 0, 1 })
        {
            var actual = evaluator.Resolve(new(
                HookNativeId: hasHook ? "run-01" : null,
                OtelNativeId: hasOtel ? "run-01" : null,
                NativeSessionMatchCount: matchCount));
            Assert.Equal(hasHook && hasOtel && matchCount == 1 ? SessionBindingKind.Native : null, actual);
        }
        Assert.Null(evaluator.Resolve(new(HookNativeId: "run-01", OtelNativeId: "RUN-01", NativeSessionMatchCount: 1)));
        Assert.Null(evaluator.Resolve(new(HookNativeId: "run-01", OtelNativeId: "run-01 ", NativeSessionMatchCount: 1)));
        Assert.Null(evaluator.Resolve(new(HookNativeId: "é", OtelNativeId: "e\u0301", NativeSessionMatchCount: 1)));
        Assert.Null(evaluator.Resolve(new(HookNativeId: "run-01", OtelNativeId: "run-01", NativeSessionMatchCount: 2)));
        Assert.Null(evaluator.Resolve(new(HookNativeId: "", OtelNativeId: "", NativeSessionMatchCount: 1)));
        Assert.Null(evaluator.Resolve(new(HookNativeId: " ", OtelNativeId: " ", NativeSessionMatchCount: 1)));
        Assert.Null(evaluator.Resolve(new(HookNativeId: "\uD800", OtelNativeId: "\uD801", NativeSessionMatchCount: 1)));

        Assert.Equal(SessionBindingKind.ExplicitResume, evaluator.Resolve(new(ExplicitTargetNativeId: "target", ExplicitLinkKind: "resume")));
        Assert.Equal(SessionBindingKind.ExplicitHandoff, evaluator.Resolve(new(ExplicitTargetNativeId: "target", ExplicitLinkKind: "handoff")));
        Assert.Null(evaluator.Resolve(new(ExplicitTargetNativeId: "target", ExplicitLinkKind: "link")));
        Assert.Null(evaluator.Resolve(new(ExplicitTargetNativeId: "target", ExplicitLinkKind: "Resume")));
        Assert.Null(evaluator.Resolve(new(ExplicitTargetNativeId: "target", ExplicitLinkKind: "resume ")));
        Assert.Null(evaluator.Resolve(new(ExplicitTargetNativeId: null, ExplicitLinkKind: "resume")));
        Assert.Null(evaluator.Resolve(new(ExplicitTargetNativeId: "", ExplicitLinkKind: "resume")));
        Assert.Null(evaluator.Resolve(new(ExplicitTargetNativeId: " ", ExplicitLinkKind: "handoff")));

        var context = Encoding.UTF8.GetBytes("00-0123456789abcdef0123456789abcdef-0123456789abcdef-01");
        Assert.Equal(SessionBindingKind.TraceContext, evaluator.Resolve(new(
            HookTraceContext: context, OtelTraceContext: context.ToArray(), HookTraceContextProvenance: true,
            OtelTraceContextProvenance: true, HasCompleteHookTraceContext: true, HasCompleteOtelTraceContext: true)));
        Assert.Null(evaluator.Resolve(new(
            HookTraceContext: context, OtelTraceContext: context.ToArray(), HookTraceContextProvenance: true,
            OtelTraceContextProvenance: true, HasCompleteHookTraceContext: false, HasCompleteOtelTraceContext: true)));
        Assert.Null(evaluator.Resolve(new(
            HookTraceContext: context, OtelTraceContext: context.ToArray(), HookTraceContextProvenance: true,
            OtelTraceContextProvenance: true, HasCompleteHookTraceContext: true, HasCompleteOtelTraceContext: false)));
        Assert.Null(evaluator.Resolve(new(
            HookTraceContext: context, OtelTraceContext: context.ToArray(), HookTraceContextProvenance: false,
            OtelTraceContextProvenance: true, HasCompleteHookTraceContext: true, HasCompleteOtelTraceContext: true)));
        Assert.Null(evaluator.Resolve(new(
            HookTraceContext: context, OtelTraceContext: context.ToArray(), HookTraceContextProvenance: true,
            OtelTraceContextProvenance: false, HasCompleteHookTraceContext: true, HasCompleteOtelTraceContext: true)));
        var differentContext = context.ToArray();
        differentContext[^1] ^= 1;
        Assert.Null(evaluator.Resolve(new(
            HookTraceContext: context, OtelTraceContext: differentContext, HookTraceContextProvenance: true,
            OtelTraceContextProvenance: true, HasCompleteHookTraceContext: true, HasCompleteOtelTraceContext: true)));
        Assert.Null(evaluator.Resolve(new(
            HookTraceContext: [], OtelTraceContext: [], HookTraceContextProvenance: true,
            OtelTraceContextProvenance: true, HasCompleteHookTraceContext: true, HasCompleteOtelTraceContext: true)));
        Assert.Null(evaluator.Resolve(new(TraceIdOnly: true)));

        var native = new ClaudeBindingCandidate(HookNativeId: "run-01", OtelNativeId: "run-01", NativeSessionMatchCount: 1);
        var explicitResume = new ClaudeBindingCandidate(ExplicitTargetNativeId: "target", ExplicitLinkKind: "resume");
        var trace = new ClaudeBindingCandidate(HookTraceContext: context, OtelTraceContext: context.ToArray(),
            HookTraceContextProvenance: true, OtelTraceContextProvenance: true,
            HasCompleteHookTraceContext: true, HasCompleteOtelTraceContext: true);
        Assert.Null(evaluator.Resolve(native with { ExplicitTargetNativeId = explicitResume.ExplicitTargetNativeId, ExplicitLinkKind = explicitResume.ExplicitLinkKind }));
        Assert.Null(evaluator.Resolve(native with { HookTraceContext = trace.HookTraceContext, OtelTraceContext = trace.OtelTraceContext,
            HookTraceContextProvenance = true, OtelTraceContextProvenance = true, HasCompleteHookTraceContext = true, HasCompleteOtelTraceContext = true }));
        Assert.Null(evaluator.Resolve(explicitResume with { HookTraceContext = trace.HookTraceContext, OtelTraceContext = trace.OtelTraceContext,
            HookTraceContextProvenance = true, OtelTraceContextProvenance = true, HasCompleteHookTraceContext = true, HasCompleteOtelTraceContext = true }));
        Assert.Null(evaluator.Resolve(native with { ExplicitTargetNativeId = "target", ExplicitLinkKind = "handoff",
            HookTraceContext = trace.HookTraceContext, OtelTraceContext = trace.OtelTraceContext,
            HookTraceContextProvenance = true, OtelTraceContextProvenance = true, HasCompleteHookTraceContext = true, HasCompleteOtelTraceContext = true }));

        foreach (var candidate in new[]
        {
            new ClaudeBindingCandidate(PathMatches: true),
            new ClaudeBindingCandidate(TimeMatches: true),
            new ClaudeBindingCandidate(AgentIdMatches: true),
            new ClaudeBindingCandidate(PromptMatches: true)
        })
        {
            Assert.Null(evaluator.Resolve(candidate));
        }
        foreach (var forbidden in new[] { "repository, workspace, `cwd`", "timestamp equality, proximity", "a trace ID without the complete byte-equivalent trace context", "`agent_id`, `parent_agent_id`", "shared prompt text" })
        {
            Assert.Contains(forbidden, binding, StringComparison.Ordinal);
        }
        Assert.Contains("no trimming, case folding", binding, StringComparison.Ordinal);

        var driftContract = ReadRepositoryDocument("docs/specifications/interfaces/source-schema-drift-claude-code.md");
        Assert.Contains("An unverified version with a known fingerprint is supported", driftContract, StringComparison.Ordinal);
        Assert.Contains("new fingerprint", driftContract, StringComparison.Ordinal);
        Assert.Contains("They are not a receive allowlist", driftContract, StringComparison.Ordinal);
        Assert.DoesNotContain("schema_fingerprint", otel.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("schema_fingerprint", hook.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void Schema_defines_the_exact_versioned_source_capability_contract()
    {
        Assert.True(File.Exists(SchemaPath), $"Missing source capability schema: {SchemaPath}");

        using var schema = JsonDocument.Parse(File.ReadAllText(SchemaPath));
        var root = schema.RootElement;

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
        Assert.Equal("https://copilot-agent-observability.dev/contracts/source-capabilities/v1", root.GetProperty("$id").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            [
                "contract_version", "source_surface", "source_adapter", "support_status", "stability",
                "source_version_detector", "signals", "native_session_identity", "trace_span_identity",
                "timing_ttft", "model_tokens", "retry_attempt", "tool_calls", "permission", "errors",
                "agent_ownership", "prompt_response", "file_diff", "content_capture_gate", "provenance",
                "completeness"
            ],
            root.GetProperty("required").EnumerateArray().Select(value => value.GetString()!).ToArray());

        Assert.Equal(
            ["github-copilot-vscode", "github-copilot-cli", "claude-code", "codex-app", "codex-cli"],
            Resolve(root, "#/$defs/sourceSurface").GetProperty("enum").EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.Equal(
            ["active", "planned", "unsupported"],
            Resolve(root, "#/$defs/supportStatus").GetProperty("enum").EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.Equal(
            ["stable", "preview", "beta", "internal-unstable"],
            Resolve(root, "#/$defs/stability").GetProperty("enum").EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.Equal(
            ["available", "unavailable", "unknown"],
            Resolve(root, "#/$defs/capability").GetProperty("properties").GetProperty("availability").GetProperty("enum").EnumerateArray().Select(value => value.GetString()!).ToArray());

        Assert.Equal(
            [
                "source_adapter", "source_version_or_schema_fingerprint", "source_event_or_trace_span_id",
                "capture_content_state", "normalization_version"
            ],
            Resolve(root, "#/$defs/provenance").GetProperty("properties").GetProperty("required_keys").GetProperty("prefixItems").EnumerateArray().Select(value => value.GetProperty("const").GetString()!).ToArray());
        Assert.Equal(
            ["unbound", "partial", "rich", "full"],
            Resolve(root, "#/$defs/completeness").GetProperty("properties").GetProperty("statuses").GetProperty("prefixItems").EnumerateArray().Select(value => value.GetProperty("const").GetString()!).ToArray());
        Assert.Equal(
            [
                "missing_native_session_id", "missing_trace_context", "trace_signal_disabled",
                "content_capture_disabled", "unsupported_source_version", "ingest_gap", "hook_only",
                "historical_summary_only", "unknown_span_kind", "schema_drift_detected",
                "planned_source_not_enabled"
            ],
            Resolve(root, "#/$defs/completeness").GetProperty("properties").GetProperty("reason_codes").GetProperty("prefixItems").EnumerateArray().Select(value => value.GetProperty("const").GetString()!).ToArray());
    }

    [Fact]
    public void Schema_validator_accepts_each_distinct_surface_and_rejects_unknown_properties()
    {
        Assert.True(File.Exists(SchemaPath), $"Missing source capability schema: {SchemaPath}");

        using var schema = JsonDocument.Parse(File.ReadAllText(SchemaPath));
        foreach (var surface in new[] { "github-copilot-vscode", "github-copilot-cli", "claude-code", "codex-app", "codex-cli" })
        {
            using var manifest = JsonDocument.Parse(CreateFixture(surface));
            Assert.Empty(SourceCapabilityManifestValidator.Validate(schema, manifest));
        }

        using var invalidManifest = JsonDocument.Parse(CreateFixture("github-copilot-vscode", "unexpected"));
        Assert.True(invalidManifest.RootElement.TryGetProperty("unexpected", out _));
        Assert.Contains(
            SourceCapabilityManifestValidator.Validate(schema, invalidManifest),
            error => error.Contains("unexpected", StringComparison.Ordinal));
    }

    [Fact]
    public void Schema_validator_rejects_malformed_string_fields_and_nested_unknown_properties()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(SchemaPath));

        using var emptyAdapter = JsonDocument.Parse(CreateFixture("github-copilot-vscode").Replace(
            "\"source_adapter\": \"fixture-adapter\"",
            "\"source_adapter\": \"\"",
            StringComparison.Ordinal));
        Assert.Contains(
            SourceCapabilityManifestValidator.Validate(schema, emptyAdapter),
            error => error == "$.source_adapter must be a non-empty string.");

        using var nonStringAdapter = JsonDocument.Parse(CreateFixture("github-copilot-vscode").Replace(
            "\"source_adapter\": \"fixture-adapter\"",
            "\"source_adapter\": false",
            StringComparison.Ordinal));
        Assert.Contains(
            SourceCapabilityManifestValidator.Validate(schema, nonStringAdapter),
            error => error == "$.source_adapter must be a string.");

        using var unexpectedSignalsProperty = JsonDocument.Parse(CreateFixture("github-copilot-vscode").Replace(
            "\"signals\": {",
            "\"signals\": { \"unexpected\": true,",
            StringComparison.Ordinal));
        Assert.Contains(
            SourceCapabilityManifestValidator.Validate(schema, unexpectedSignalsProperty),
            error => error == "$.signals.unexpected is not allowed.");

        using var unexpectedCapabilityProperty = JsonDocument.Parse(CreateFixture("github-copilot-vscode").Replace(
            "\"source_version_detector\": {\"availability\":\"unknown\"}",
            "\"source_version_detector\": {\"availability\":\"unknown\",\"unexpected\":true}",
            StringComparison.Ordinal));
        Assert.Contains(
            SourceCapabilityManifestValidator.Validate(schema, unexpectedCapabilityProperty),
            error => error == "$.source_version_detector.unexpected is not allowed.");
    }

    [Fact]
    public void Canonical_documents_define_the_v1_semantic_contract_and_adapter_handoff()
    {
        var canonicalDocuments = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["docs/requirements.md"] = ["Source capability semantic contract v1", "repository / workspace / timestamp"],
            ["docs/spec.md"] = ["Source capability semantic contract v1", "source-capability-manifest.schema.json"],
            ["docs/specifications/layers/telemetry-ingestion.md"] = ["Source capability semantic contract v1"],
            ["docs/specifications/layers/raw-store-normalization.md"] = ["Source capability semantic contract v1"],
            ["docs/specifications/interfaces/canvas-session-workspace.md"] = ["Completeness input facts and decision order", "historical_summary_only"],
            ["docs/specifications/security-data-boundaries.md"] = ["Source capability semantic contract v1", "manifest grants no content authority"],
            ["docs/decisions.md"] = ["D056: Source capability semantic contract v1", "adapter handoff checklist"],
            ["docs/task.md"] = ["Source capability contract (Issue #61)", "完了"]
        };

        foreach (var (relativePath, requiredIdentifiers) in canonicalDocuments)
        {
            var document = ReadRepositoryDocument(relativePath);
            foreach (var identifier in requiredIdentifiers)
            {
                Assert.Contains(identifier, document, StringComparison.OrdinalIgnoreCase);
            }
        }

        using var schema = JsonDocument.Parse(File.ReadAllText(SchemaPath));
        var completeness = Resolve(schema.RootElement, "#/$defs/completeness").GetProperty("properties");
        var expectedStatuses = completeness.GetProperty("statuses").GetProperty("prefixItems")
            .EnumerateArray().Select(item => item.GetProperty("const").GetString()!).ToArray();
        var expectedReasons = completeness.GetProperty("reason_codes").GetProperty("prefixItems")
            .EnumerateArray().Select(item => item.GetProperty("const").GetString()!).ToArray();

        var workspaceCompletenessRaw = GetMarkdownSectionRaw(
            ReadRepositoryDocument("docs/specifications/interfaces/canvas-session-workspace.md"),
            "### Completeness input facts and decision order",
            "\n## ");
        var normalizationCompletenessRaw = GetMarkdownSectionRaw(
            ReadRepositoryDocument("docs/specifications/layers/raw-store-normalization.md"),
            "### Deterministic completeness decision",
            "\n## ");
        var workspaceCompleteness = NormalizeMarkdown(workspaceCompletenessRaw);
        var normalizationCompleteness = NormalizeMarkdown(normalizationCompletenessRaw);

        AssertMarkdownCompletenessMatchesSchema(workspaceCompleteness, expectedStatuses, expectedReasons);
        AssertMarkdownCompletenessMatchesSchema(normalizationCompleteness, expectedStatuses, expectedReasons);
        var workspaceReasonCaps = ParseCompletenessReasonCaps(workspaceCompletenessRaw);
        var normalizationReasonCaps = ParseCompletenessReasonCaps(normalizationCompletenessRaw);
        AssertCompletenessDecisionIsTotal(expectedStatuses, expectedReasons, workspaceReasonCaps);
        AssertCompletenessDecisionIsTotal(expectedStatuses, expectedReasons, normalizationReasonCaps);
        Assert.Equal(workspaceReasonCaps, normalizationReasonCaps);

        var telemetryContract = GetMarkdownSection(
            ReadRepositoryDocument("docs/specifications/layers/telemetry-ingestion.md"),
            "## Source capability semantic contract v1",
            "\n## Session Event Ingestion And Enrichment");
        var normalizationContract = GetMarkdownSection(
            ReadRepositoryDocument("docs/specifications/layers/raw-store-normalization.md"),
            "### Source capability semantic contract v1",
            "\n### Deterministic completeness decision");
        var decision = GetMarkdownSection(
            ReadRepositoryDocument("docs/decisions.md"),
            "## D056: Source capability semantic contract v1",
            null);

        AssertActualAdapterProvenance(telemetryContract);
        AssertActualAdapterProvenance(normalizationContract);
        AssertActualAdapterProvenance(decision);
        AssertAuthorityAllowlist(telemetryContract);
        AssertNoHeuristicMergeOrSyntheticSpan(workspaceCompleteness, telemetryContract, normalizationContract);
        AssertNoContentAuthority(GetMarkdownSection(
            ReadRepositoryDocument("docs/specifications/security-data-boundaries.md"),
            "### Source capability semantic contract v1",
            "\n`POST /api/session-ingest/v1/events`"));
        AssertAdapterHandoffChecklist(telemetryContract, decision);

        var issue61Row = Assert.Single(
            ReadRepositoryDocument("docs/task.md")
                .Split('\n')
                .Select(line => line.TrimEnd('\r')),
            line => line.Contains("Source capability contract (Issue #61)", StringComparison.Ordinal));
        const string completedIssue61RoadmapRow = "| Source capability contract (Issue #61) | **完了** | JSON Schema 2020-12 と 5 surface の manifest による committed structural/capability contract、canonical semantic contract の authority / provenance / deterministic completeness / safety、cross-reference tests、adapter handoff を確定した。Issue #51 exact identity と Issue #49 ownership を保持し、receiver / adapter / DB / migration / HTTP / proxy / UI DTO は変更しない。仕様・品質・最終統合レビュー済み（live validation は scope 外）。focused SourceCapabilityContractTests 13/13、build 0 warnings/0 errors、Playwright install exit 0、full solution tests 1,250（ConfigCli 314 + LocalMonitor 936）が成功。 |";
        Assert.Equal(completedIssue61RoadmapRow, issue61Row);
        Assert.DoesNotContain("final validation pending", issue61Row, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("最終 validation 待ち", issue61Row, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RuntimeEquivalentReasons))]
    public void Markdown_reason_caps_match_existing_session_completeness_for_runtime_equivalent_facts(
        string reason,
        SessionCompletenessEvidence evidence)
    {
        var workspaceReasonCaps = ParseCompletenessReasonCaps(GetMarkdownSectionRaw(
            ReadRepositoryDocument("docs/specifications/interfaces/canvas-session-workspace.md"),
            "### Completeness input facts and decision order",
            "\n## "));
        var normalizationReasonCaps = ParseCompletenessReasonCaps(GetMarkdownSectionRaw(
            ReadRepositoryDocument("docs/specifications/layers/raw-store-normalization.md"),
            "### Deterministic completeness decision",
            "\n## "));

        Assert.Equal(workspaceReasonCaps, normalizationReasonCaps);
        Assert.Equal(
            SessionWire.ToWire(SessionCompletenessCalculator.Calculate(evidence)),
            workspaceReasonCaps[reason]);
    }

    private static string ReadRepositoryDocument(string relativePath)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Missing canonical Issue #61 document: {relativePath}");
        return File.ReadAllText(path).ReplaceLineEndings("\n");
    }

    private static string GetMarkdownSection(string document, string heading, string? endHeading)
    {
        return NormalizeMarkdown(GetMarkdownSectionRaw(document, heading, endHeading));
    }

    private static string GetMarkdownSectionRaw(string document, string heading, string? endHeading)
    {
        var start = document.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing Markdown section: {heading}");

        var end = endHeading is null
            ? document.Length
            : document.IndexOf(endHeading, start + heading.Length, StringComparison.Ordinal);
        Assert.True(endHeading is null || end >= 0, $"Missing section terminator after {heading}: {endHeading}");
        return document[start..(endHeading is null ? document.Length : end)];
    }

    private static string NormalizeMarkdown(string markdown) => Regex.Replace(markdown, "\\s+", " ");

    private static void AssertMarkdownCompletenessMatchesSchema(string section, IReadOnlyList<string> expectedStatuses, IReadOnlyList<string> expectedReasons)
    {
        var listedReasons = Regex.Matches(section, "\\d+\\. `([^`]+)`")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        Assert.Equal(expectedReasons, listedReasons);

        var position = 0;
        foreach (var status in expectedStatuses)
        {
            position = section.IndexOf($"`{status}`", position, StringComparison.Ordinal);
            Assert.True(position >= 0, $"Missing ordered completeness status: {status}");
            position++;
        }
    }

    private static Dictionary<string, string> ParseCompletenessReasonCaps(string section)
    {
        const string heading = "| Reason code | Maximum status |";
        var tableStart = section.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(tableStart >= 0, "Missing completeness reason-to-maximum-status table.");

        var caps = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
                     section[tableStart..],
                     "(?m)^\\| `([^`]+)` \\| `(unbound|partial|rich|full)` \\|[^\\r\\n]*\\|\\r?$"))
        {
            Assert.True(caps.TryAdd(match.Groups[1].Value, match.Groups[2].Value),
                $"Completeness reason '{match.Groups[1].Value}' has more than one maximum status.");
        }

        return caps;
    }

    private static void AssertCompletenessDecisionIsTotal(
        IReadOnlyList<string> statuses,
        IReadOnlyList<string> expectedReasons,
        IReadOnlyDictionary<string, string> reasonCaps)
    {
        Assert.Equal(["unbound", "partial", "rich", "full"], statuses);
        Assert.Equal(expectedReasons.OrderBy(reason => reason, StringComparer.Ordinal), reasonCaps.Keys.OrderBy(reason => reason, StringComparer.Ordinal));

        var expectedCaps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["missing_native_session_id"] = "unbound",
            ["missing_trace_context"] = "rich",
            ["trace_signal_disabled"] = "rich",
            ["content_capture_disabled"] = "rich",
            ["unsupported_source_version"] = "rich",
            ["ingest_gap"] = "rich",
            ["hook_only"] = "rich",
            ["historical_summary_only"] = "partial",
            ["unknown_span_kind"] = "rich",
            ["schema_drift_detected"] = "partial",
            ["planned_source_not_enabled"] = "unbound"
        };
        Assert.Equal(expectedCaps, reasonCaps);

        Assert.Equal("unbound", CalculateBaseStatus(hasNativeSessionId: false, hasRequiredLifecycleAndInput: true, hasRequiredContentAndTerminal: true));
        Assert.Equal("partial", CalculateBaseStatus(hasNativeSessionId: true, hasRequiredLifecycleAndInput: false, hasRequiredContentAndTerminal: true));
        Assert.Equal("rich", CalculateBaseStatus(hasNativeSessionId: true, hasRequiredLifecycleAndInput: true, hasRequiredContentAndTerminal: false));
        Assert.Equal("full", CalculateBaseStatus(hasNativeSessionId: true, hasRequiredLifecycleAndInput: true, hasRequiredContentAndTerminal: true));

        foreach (var reason in expectedReasons)
        {
            var evaluation = CalculateCompleteness("full", [reason], expectedReasons, reasonCaps);
            Assert.Equal(expectedCaps[reason], evaluation.Status);
            Assert.Equal([reason], evaluation.Reasons);
        }

        Assert.Equal("unbound", CalculateCompleteness("unbound", ["content_capture_disabled"], expectedReasons, reasonCaps).Status);
        Assert.Equal("partial", CalculateCompleteness("partial", ["content_capture_disabled"], expectedReasons, reasonCaps).Status);
        Assert.Equal("partial", CalculateCompleteness("full", ["historical_summary_only"], expectedReasons, reasonCaps).Status);
        Assert.Equal("rich", reasonCaps["unsupported_source_version"]);
        Assert.Equal("partial", reasonCaps["schema_drift_detected"]);
        Assert.NotEqual(reasonCaps["unsupported_source_version"], reasonCaps["schema_drift_detected"]);
        Assert.Equal("rich", reasonCaps["ingest_gap"]);
        Assert.Equal("partial", CalculateBaseStatus(hasNativeSessionId: true, hasRequiredLifecycleAndInput: false, hasRequiredContentAndTerminal: true));
        Assert.NotEqual(reasonCaps["ingest_gap"], CalculateBaseStatus(hasNativeSessionId: true, hasRequiredLifecycleAndInput: false, hasRequiredContentAndTerminal: true));
        AssertFutureOnlyReasonsAreExcludedFromRuntimeEquivalence(expectedReasons);
        Assert.Equal(
            ["missing_native_session_id", "content_capture_disabled", "hook_only"],
            CalculateCompleteness("full", ["hook_only", "content_capture_disabled", "hook_only", "missing_native_session_id"], expectedReasons, reasonCaps).Reasons);
        Assert.Throws<InvalidOperationException>(() => CalculateCompleteness("full", ["unknown_reason"], expectedReasons, reasonCaps));
    }

    private static void AssertFutureOnlyReasonsAreExcludedFromRuntimeEquivalence(IReadOnlyList<string> expectedReasons)
    {
        var futureOnlyReasons = new[]
        {
            "historical_summary_only",
            "schema_drift_detected",
            "planned_source_not_enabled"
        };
        var runtimeEquivalentReasons = RuntimeEquivalentReasons.Select(row => row[0]);

        Assert.Equal(futureOnlyReasons, expectedReasons.Where(futureOnlyReasons.Contains));
        Assert.DoesNotContain(runtimeEquivalentReasons, futureOnlyReasons.Contains);
    }

    private static string CalculateBaseStatus(
        bool hasNativeSessionId,
        bool hasRequiredLifecycleAndInput,
        bool hasRequiredContentAndTerminal)
    {
        if (!hasNativeSessionId)
        {
            return "unbound";
        }

        if (!hasRequiredLifecycleAndInput)
        {
            return "partial";
        }

        return hasRequiredContentAndTerminal ? "full" : "rich";
    }

    private static (string Status, string[] Reasons) CalculateCompleteness(
        string baseStatus,
        IReadOnlyCollection<string> presentReasons,
        IReadOnlyList<string> canonicalReasonOrder,
        IReadOnlyDictionary<string, string> reasonCaps)
    {
        var unknownReason = presentReasons.FirstOrDefault(reason => !reasonCaps.ContainsKey(reason));
        if (unknownReason is not null)
        {
            throw new InvalidOperationException($"Unknown completeness reason '{unknownReason}' is schema drift.");
        }

        var rank = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["unbound"] = 0,
            ["partial"] = 1,
            ["rich"] = 2,
            ["full"] = 3
        };
        var status = presentReasons.Aggregate(baseStatus, (current, reason) =>
            rank[reasonCaps[reason]] < rank[current] ? reasonCaps[reason] : current);
        var reasons = canonicalReasonOrder.Where(presentReasons.Contains).ToArray();
        return (status, reasons);
    }

    private static void AssertAuthorityAllowlist(string section)
    {
        Assert.Contains("| model/token, retry, error summary |", section, StringComparison.Ordinal);
        Assert.Contains("historical summary allowlist-only: `model_tokens.*`, `retry_attempt.*`, and `errors`", section, StringComparison.Ordinal);
        Assert.Contains("never identity, hierarchy, timing, lifecycle, or explicit event identity", section, StringComparison.Ordinal);
    }

    private static void AssertNoHeuristicMergeOrSyntheticSpan(params string[] sections)
    {
        Assert.Contains(sections, section => section.Contains("no heuristic merge", StringComparison.OrdinalIgnoreCase)
            && section.Contains("synthetic span", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sections, section => section.Contains("exact identity", StringComparison.OrdinalIgnoreCase)
            && section.Contains("Issue #49", StringComparison.Ordinal));
    }

    private static void AssertNoContentAuthority(string section)
    {
        Assert.Contains("manifest grants no content authority", section, StringComparison.Ordinal);
        Assert.Contains("does not grant any caller read, transport, storage, or display authority", section, StringComparison.Ordinal);
        Assert.Contains("must not contain raw prompt/response, tool input/output, file or diff content, paths, credentials, tokens, PII", section, StringComparison.Ordinal);
    }

    private static void AssertAdapterHandoffChecklist(string telemetryContract, string decision)
    {
        Assert.Contains("matching manifest version", telemetryContract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("declare only observed capabilities", telemetryContract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("per-field actual-adapter provenance", telemetryContract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authority table without overwrite/inference", telemetryContract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fixed completeness statuses/reasons in the canonical order", telemetryContract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("raw/sanitized boundaries", telemetryContract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new contract major", telemetryContract, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("matching schema/manifest version", decision, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("observed rather than invented capability", decision, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("actual-adapter field provenance", decision, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authority/absence precedence", decision, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fixed status/reason output deterministically", decision, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("raw/sanitized boundaries", decision, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new major", decision, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertActualAdapterProvenance(string section)
    {
        Assert.Contains("actual contributing adapter", section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("such as", section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`otel-http`", section, StringComparison.Ordinal);
        Assert.Contains("`copilot-compatible-hook`", section, StringComparison.Ordinal);
        Assert.Contains("`copilot-sdk-stream`", section, StringComparison.Ordinal);
        Assert.Contains("`otel-http+copilot-compatible-hook`", section, StringComparison.Ordinal);
        Assert.Contains("per-field provenance", section, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("(?i)(?:must never record the composite |composite )`otel-http\\+copilot-compatible-hook` manifest label (?:as |must never be |denotes registered paths only and is never )per-field provenance", section);
        Assert.DoesNotContain("actual contributing adapter (`otel-http` or `copilot-compatible-hook`)", section, StringComparison.Ordinal);
    }

    private static JsonElement Resolve(JsonElement root, string reference)
    {
        var current = root;
        foreach (var segment in reference[2..].Split('/'))
        {
            current = current.GetProperty(segment);
        }

        return current;
    }

    private static void AssertManifestHeader(JsonDocument manifest, string surface, string adapter, string supportStatus, string stability)
    {
        var root = manifest.RootElement;
        Assert.Equal("v1", root.GetProperty("contract_version").GetString());
        Assert.Equal(surface, root.GetProperty("source_surface").GetString());
        Assert.Equal(adapter, root.GetProperty("source_adapter").GetString());
        Assert.Equal(supportStatus, root.GetProperty("support_status").GetString());
        Assert.Equal(stability, root.GetProperty("stability").GetString());
    }

    private static void AssertCapability(JsonDocument manifest, string path, string expectedAvailability)
    {
        var value = manifest.RootElement;
        foreach (var segment in path.Split('.'))
        {
            value = value.GetProperty(segment);
        }

        Assert.Equal(expectedAvailability, value.GetProperty("availability").GetString());
    }

    private static void AssertAvailabilityMatrix(JsonDocument manifest, IReadOnlyDictionary<string, string> expectedAvailability)
    {
        Assert.Equal(34, expectedAvailability.Count);
        foreach (var (path, availability) in expectedAvailability)
        {
            AssertCapability(manifest, path, availability);
        }
    }

    private static Dictionary<string, string> CopilotAvailabilityMatrix()
    {
        var availability = UnknownAvailabilityMatrix();
        availability["source_version_detector"] = "unavailable";
        availability["signals.trace"] = "available";
        availability["signals.hook"] = "available";
        availability["signals.sdk_event"] = "unavailable";
        availability["native_session_identity"] = "available";
        availability["trace_span_identity.trace_id"] = "available";
        availability["trace_span_identity.span_id"] = "available";
        availability["trace_span_identity.parentage"] = "available";
        availability["timing_ttft.timing"] = "available";
        availability["content_capture_gate"] = "available";
        return availability;
    }

    private static Dictionary<string, string> UnknownAvailabilityMatrix()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source_version_detector"] = "unknown",
            ["signals.trace"] = "unknown",
            ["signals.log"] = "unknown",
            ["signals.metric"] = "unknown",
            ["signals.hook"] = "unknown",
            ["signals.sdk_event"] = "unknown",
            ["signals.saved_raw"] = "unknown",
            ["native_session_identity"] = "unknown",
            ["trace_span_identity.trace_id"] = "unknown",
            ["trace_span_identity.span_id"] = "unknown",
            ["trace_span_identity.parentage"] = "unknown",
            ["timing_ttft.timing"] = "unknown",
            ["timing_ttft.ttft"] = "unknown",
            ["model_tokens.model"] = "unknown",
            ["model_tokens.input_tokens"] = "unknown",
            ["model_tokens.output_tokens"] = "unknown",
            ["model_tokens.total_tokens"] = "unknown",
            ["model_tokens.cache_tokens"] = "unknown",
            ["model_tokens.reasoning_tokens"] = "unknown",
            ["retry_attempt.retry"] = "unknown",
            ["retry_attempt.attempt"] = "unknown",
            ["tool_calls.identity"] = "unknown",
            ["tool_calls.input"] = "unknown",
            ["tool_calls.output"] = "unknown",
            ["permission.wait"] = "unknown",
            ["permission.decision"] = "unknown",
            ["errors"] = "unknown",
            ["agent_ownership.main_agent"] = "unknown",
            ["agent_ownership.sub_agent"] = "unknown",
            ["prompt_response.prompt"] = "unknown",
            ["prompt_response.response"] = "unknown",
            ["file_diff.file"] = "unknown",
            ["file_diff.diff"] = "unknown",
            ["content_capture_gate"] = "unknown"
        };
    }

    private static JsonDocument ReadClaudeMapping(string fileName)
    {
        var path = Path.Combine(ClaudeContractDirectory, fileName);
        Assert.True(File.Exists(path), $"Missing Claude mapping artifact: {path}");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static Dictionary<string, JsonElement> IndexFields(JsonElement mapping) =>
        mapping.GetProperty("fields").EnumerateArray().ToDictionary(
            field => Value(field.GetProperty("field_id")),
            field => field,
            StringComparer.Ordinal);

    private static void AssertField(
        IReadOnlyDictionary<string, JsonElement> fields,
        string fieldId,
        string disposition,
        string authority,
        string? normalizedTarget,
        string? rawStorageTarget)
    {
        var field = fields[fieldId];
        Assert.Equal(disposition, Value(field.GetProperty("disposition")));
        Assert.Equal(authority, Value(field.GetProperty("authority")));
        Assert.Equal(normalizedTarget, NullableString(field, "normalized_target"));
        Assert.Equal(rawStorageTarget, NullableString(field, "raw_storage_target"));
    }

    private static string Value(JsonElement value) => value.GetString()!;

    private static string? NullableString(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    private static readonly ExpectedField[] ExpectedOtelFields =
    [
        new("otel.trace_id", "resourceSpans[].scopeSpans[].spans[].traceId", "OTLP trace-id bytes or descriptor-approved JSON string", true, "every OTLP span", "none", "normalized", "otel_identity", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.TraceId", null),
        new("otel.span_id", "resourceSpans[].scopeSpans[].spans[].spanId", "OTLP span-id bytes or descriptor-approved JSON string", true, "every OTLP span", "none", "normalized", "otel_identity", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.SpanId", null),
        new("otel.parent_span_id", "resourceSpans[].scopeSpans[].spans[].parentSpanId", "OTLP span-id bytes or descriptor-approved JSON string", false, "every OTLP span", "none", "normalized", "otel_hierarchy", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.ParentSpanId", null),
        new("otel.start_time", "resourceSpans[].scopeSpans[].spans[].startTimeUnixNano", "OTLP fixed64 or decimal-string nanoseconds", true, "every OTLP span", "none", "normalized", "otel_timing", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.StartTime", null),
        new("otel.end_time", "resourceSpans[].scopeSpans[].spans[].endTimeUnixNano", "OTLP fixed64 or decimal-string nanoseconds", true, "every OTLP span", "none", "normalized", "otel_timing", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.EndTime", null),
        new("otel.status_code", "resourceSpans[].scopeSpans[].spans[].status.code", "OTLP StatusCode enum", false, "every OTLP span status object", "none", "normalized", "sole_otel_status_authority", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.Status", null, "otel_status"),
        new("otel.status_message", "resourceSpans[].scopeSpans[].spans[].status.message", "string", false, "every OTLP span status object", "none", "documented_unmapped", "none", null, RawPayloadTarget),
        new("otel.span_name", "resourceSpans[].scopeSpans[].spans[].name", "string", true, "every OTLP span", "none", "normalized", "otel_observed_value", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.Operation", RawPayloadTarget, "claude_span_classification"),
        new("otel.span_type", "resourceSpans[].scopeSpans[].spans[].attributes[\"span.type\"]", "string", false, "every OTLP span carrying the attribute", "documented detailed trace emission", "documented_unmapped", "none", null, RawPayloadTarget),
        new("otel.session_id", "resourceSpans[].scopeSpans[].spans[].attributes[\"session.id\"]", "string", false, "every OTLP span carrying the attribute", "documented session-ID telemetry setting", "corroboration_only", "identical_native_id_candidate_only", null, RawPayloadTarget, "exact_native_session_binding"),
        new("otel.request_model", "resourceSpans[].scopeSpans[].spans[claude_code.llm_request].attributes[\"gen_ai.request.model\"]", "string", false, "span.name == \"claude_code.llm_request\"", "documented trace span is emitted", "normalized", "otel_observed_value", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.RequestModel", null),
        new("otel.ttft_ms", "resourceSpans[].scopeSpans[].spans[claude_code.llm_request].attributes[\"ttft_ms\"]", "number", false, "span.name == \"claude_code.llm_request\"", "documented trace span is emitted", "documented_unmapped", "otel_timing", null, RawPayloadTarget),
        new("otel.input_tokens", "resourceSpans[].scopeSpans[].spans[claude_code.llm_request].attributes[\"input_tokens\"]", "integer", false, "span.name == \"claude_code.llm_request\"", "documented trace span is emitted", "normalized", "otel_observed_value", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.InputTokens", null),
        new("otel.output_tokens", "resourceSpans[].scopeSpans[].spans[claude_code.llm_request].attributes[\"output_tokens\"]", "integer", false, "span.name == \"claude_code.llm_request\"", "documented trace span is emitted", "normalized", "otel_observed_value", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.OutputTokens", null),
        new("otel.cache_read_tokens", "resourceSpans[].scopeSpans[].spans[claude_code.llm_request].attributes[\"cache_read_tokens\"]", "integer", false, "span.name == \"claude_code.llm_request\"", "documented trace span is emitted", "normalized", "otel_observed_value", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.CacheReadTokens", null),
        new("otel.cache_creation_tokens", "resourceSpans[].scopeSpans[].spans[claude_code.llm_request].attributes[\"cache_creation_tokens\"]", "integer", false, "span.name == \"claude_code.llm_request\"", "documented trace span is emitted", "normalized", "otel_observed_value", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.CacheCreationTokens", null),
        new("otel.attempt", "resourceSpans[].scopeSpans[].spans[claude_code.llm_request].attributes[\"attempt\"]", "integer", false, "span.name == \"claude_code.llm_request\"", "documented trace span is emitted", "documented_unmapped", "none", null, RawPayloadTarget),
        new("otel.retry_event_attempt", "resourceSpans[].scopeSpans[].spans[claude_code.llm_request].events[gen_ai.request.attempt].attributes[\"attempt\"]", "integer", false, "span.name == \"claude_code.llm_request\" and event.name == \"gen_ai.request.attempt\"", "documented retry event is emitted", "documented_unmapped", "none", null, RawPayloadTarget),
        new("otel.tool_name", "resourceSpans[].scopeSpans[].spans[claude_code.tool].attributes[\"tool_name\"]", "string", false, "span.name == \"claude_code.tool\"", "documented trace span is emitted", "normalized", "otel_observed_value", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.ToolName", null),
        new("otel.tool_use_id", "resourceSpans[].scopeSpans[].spans[claude_code.tool].attributes[\"tool_use_id\"]", "string", false, "span.name == \"claude_code.tool\"", "documented trace span is emitted", "corroboration_only", "post_binding_tool_correlation_only", null, RawPayloadTarget),
        new("otel.permission_wait_ms", "resourceSpans[].scopeSpans[].spans[claude_code.tool.blocked_on_user].attributes[\"duration_ms\"]", "number", false, "span.name == \"claude_code.tool.blocked_on_user\"", "documented trace span is emitted", "documented_unmapped", "otel_timing", null, RawPayloadTarget),
        new("otel.permission_decision", "resourceSpans[].scopeSpans[].spans[claude_code.tool.blocked_on_user].attributes[\"decision\"]", "string", false, "span.name == \"claude_code.tool.blocked_on_user\"", "documented trace span is emitted", "documented_unmapped", "otel_observed_value", null, RawPayloadTarget),
        new("otel.tool_success", "resourceSpans[].scopeSpans[].spans[claude_code.tool.execution].attributes[\"success\"]", "boolean", false, "span.name == \"claude_code.tool.execution\"", "documented trace span is emitted", "corroboration_only", "none", null, RawPayloadTarget, "otel_status"),
        new("otel.tool_error_category", "resourceSpans[].scopeSpans[].spans[claude_code.tool.execution].attributes[\"error\"]", "documented error-category string", false, "span.name == \"claude_code.tool.execution\" and OTEL_LOG_TOOL_DETAILS != 1", "tool execution failed and detailed tool logging is disabled", "raw_retained", "none", null, RawPayloadTarget),
        new("otel.tool_error_detail", "resourceSpans[].scopeSpans[].spans[claude_code.tool.execution].attributes[\"error\"]", "full error-message string", false, "span.name == \"claude_code.tool.execution\" and OTEL_LOG_TOOL_DETAILS == 1", "OTEL_LOG_TOOL_DETAILS=1", "raw_retained", "none", null, RawPayloadTarget),
        new("otel.user_prompt", "resourceSpans[].scopeSpans[].spans[claude_code.interaction].attributes[\"user_prompt\"]", "string", false, "span.name == \"claude_code.interaction\"", "OTEL_LOG_USER_PROMPTS=1 emits content; Claude Code 2.1.214 gate-disabled observation emits exact <REDACTED> plus user_prompt_length.", "raw_retained", "none", null, RawPayloadTarget),
        new("otel.tool_output_event", "resourceSpans[].scopeSpans[].spans[claude_code.tool].events[tool.output]", "OTLP span event object", false, "span.name == \"claude_code.tool\" and event.name == \"tool.output\"", "OTEL_LOG_TOOL_CONTENT=1", "raw_retained", "none", null, RawPayloadTarget),
        new("otel.file_path", "resourceSpans[].scopeSpans[].spans[claude_code.tool].attributes[\"file_path\"]", "string path", false, "span.name == \"claude_code.tool\"", "OTEL_LOG_TOOL_DETAILS=1", "raw_retained", "none", null, RawPayloadTarget),
        new("otel.agent_id", "resourceSpans[].scopeSpans[].spans[claude_code.llm_request].attributes[\"agent_id\"]", "string", false, "span.name == \"claude_code.llm_request\"", "documented subagent request emits the field", "documented_unmapped", "none", null, RawPayloadTarget),
        new("otel.parent_agent_id", "resourceSpans[].scopeSpans[].spans[claude_code.llm_request].attributes[\"parent_agent_id\"]", "string", false, "span.name == \"claude_code.llm_request\"", "documented subagent request emits the field", "documented_unmapped", "none", null, RawPayloadTarget),
        new("otel.subagent_type", "resourceSpans[].scopeSpans[].spans[claude_code.tool].attributes[\"subagent_type\"]", "string", false, "span.name == \"claude_code.tool\"", "OTEL_LOG_TOOL_DETAILS=1", "documented_unmapped", "none", null, RawPayloadTarget),
        new("otel.response_model_output", "resourceSpans[].scopeSpans[].spans[claude_code.llm_request].attributes[\"response.model_output\"]", "string or producer-defined raw value", false, "span.name == \"claude_code.llm_request\"", "detailed beta tracing", "raw_retained", "none", null, RawPayloadTarget),
        new("otel.hook_definitions", "resourceSpans[].scopeSpans[].spans[claude_code.hook].attributes[\"hook_definitions\"]", "JSON string", false, "span.name == \"claude_code.hook\"", "detailed beta tracing and OTEL_LOG_TOOL_DETAILS=1", "raw_retained", "none", null, RawPayloadTarget),
        new("otel.reasoning_tokens", "resourceSpans[].scopeSpans[].spans[claude_code.llm_request].attributes[\"reasoning_tokens\"]", "integer", false, "span.name == \"claude_code.llm_request\"", "not established", "documented_unmapped", "none", null, RawPayloadTarget)
    ];

    private static readonly ExpectedField[] ExpectedHookFields =
    [
        new("hook.session_id", "$.session_id", "string", true, "every configured Hook event", "configured Hook event fires", "normalized", "hook_native_session_identity", "CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEnvelope.NativeSessionId", null, "exact_native_session_binding"),
        new("hook.event_name", "$.hook_event_name", "string", true, "every configured Hook event", "configured Hook event fires", "normalized", "hook_lifecycle", "CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEvent.Type", null),
        new("hook.prompt_id", "$.prompt_id", "string", false, "any Hook event where the producer emits prompt_id", "absent before first user input", "corroboration_only", "post_binding_correlation_only", null, HookRawTarget),
        new("hook.transcript_path", "$.transcript_path", "string path", true, "every configured Hook event", "configured Hook event fires", "raw_retained", "none", null, HookRawTarget),
        new("hook.cwd", "$.cwd", "string path", true, "every configured Hook event", "configured Hook event fires", "raw_retained", "none", null, HookRawTarget),
        new("hook.permission_mode", "$.permission_mode", "string enum", false, "any Hook event where the producer emits permission_mode", "event-dependent", "raw_retained", "hook_context_only", null, HookRawTarget),
        new("hook.effort_level", "$.effort.level", "string enum", false, "any Hook event where the producer emits effort.level", "tool-use context and supporting model", "raw_retained", "hook_context_only", null, HookRawTarget),
        new("hook.conditional_agent_id", "$.agent_id", "string", false, "hook_event_name is not SubagentStart or SubagentStop and producer emits agent_id", "event fires inside a subagent", "normalized", "hook_native_run_identity", "CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEvent.RunNativeId", null),
        new("hook.conditional_agent_type", "$.agent_type", "string", false, "hook_event_name is not SubagentStart or SubagentStop and producer emits agent_type", "event fires inside a subagent or in a top-level --agent session", "raw_retained", "none", null, HookRawTarget),
        new("hook.session_start.source", "$.source", "string enum", true, "hook_event_name == \"SessionStart\"", "SessionStart configured", "corroboration_only", "hook_lifecycle_context", null, HookRawTarget),
        new("hook.session_start.model", "$.model", "string", false, "hook_event_name == \"SessionStart\"", "producer emits the optional model", "raw_retained", "hook_context_only", null, HookRawTarget),
        new("hook.session_start.session_title", "$.session_title", "string", false, "hook_event_name == \"SessionStart\"", "current session title is already set (for example via --name or /rename)", "raw_retained", "none", null, HookRawTarget),
        new("hook.user_prompt_submit.prompt", "$.prompt", "string", true, "hook_event_name == \"UserPromptSubmit\"", "UserPromptSubmit configured", "raw_retained", "hook_user_instruction_content", null, HookRawTarget),
        new("hook.pre_tool_use.tool_name", "$.tool_name", "string", true, "hook_event_name == \"PreToolUse\"", "PreToolUse configured", "raw_retained", "hook_context_only", null, HookRawTarget),
        new("hook.pre_tool_use.tool_input", "$.tool_input", "JSON object", true, "hook_event_name == \"PreToolUse\"", "PreToolUse configured", "raw_retained", "none", null, HookRawTarget),
        new("hook.pre_tool_use.tool_use_id", "$.tool_use_id", "string", true, "hook_event_name == \"PreToolUse\"", "PreToolUse configured", "corroboration_only", "post_binding_tool_correlation_only", null, HookRawTarget),
        new("hook.permission_request.tool_name", "$.tool_name", "string", true, "hook_event_name == \"PermissionRequest\"", "PermissionRequest configured", "raw_retained", "hook_permission_context", null, HookRawTarget),
        new("hook.permission_request.tool_input", "$.tool_input", "JSON object", true, "hook_event_name == \"PermissionRequest\"", "PermissionRequest configured", "raw_retained", "none", null, HookRawTarget),
        new("hook.permission_request.permission_suggestions", "$.permission_suggestions", "array", false, "hook_event_name == \"PermissionRequest\"", "producer emits suggestions", "raw_retained", "hook_permission_context", null, HookRawTarget),
        new("hook.post_tool_use.tool_name", "$.tool_name", "string", true, "hook_event_name == \"PostToolUse\"", "PostToolUse configured", "raw_retained", "hook_context_only", null, HookRawTarget),
        new("hook.post_tool_use.tool_input", "$.tool_input", "JSON object", true, "hook_event_name == \"PostToolUse\"", "PostToolUse configured", "raw_retained", "none", null, HookRawTarget),
        new("hook.post_tool_use.tool_use_id", "$.tool_use_id", "string", true, "hook_event_name == \"PostToolUse\"", "PostToolUse configured after successful execution", "corroboration_only", "post_binding_tool_correlation_only", null, HookRawTarget),
        new("hook.post_tool_use.tool_response", "$.tool_response", "producer-defined JSON value", true, "hook_event_name == \"PostToolUse\"", "PostToolUse configured", "raw_retained", "none", null, HookRawTarget),
        new("hook.post_tool_use.duration_ms", "$.duration_ms", "number", false, "hook_event_name == \"PostToolUse\"", "producer version emits duration_ms", "corroboration_only", "hook_context_only", null, HookRawTarget),
        new("hook.post_tool_failure.tool_name", "$.tool_name", "string", true, "hook_event_name == \"PostToolUseFailure\"", "PostToolUseFailure configured", "raw_retained", "hook_context_only", null, HookRawTarget),
        new("hook.post_tool_failure.tool_input", "$.tool_input", "JSON object", true, "hook_event_name == \"PostToolUseFailure\"", "PostToolUseFailure configured", "raw_retained", "none", null, HookRawTarget),
        new("hook.post_tool_failure.tool_use_id", "$.tool_use_id", "string", true, "hook_event_name == \"PostToolUseFailure\"", "PostToolUseFailure configured", "corroboration_only", "post_binding_tool_correlation_only", null, HookRawTarget),
        new("hook.post_tool_failure.error", "$.error", "string", true, "hook_event_name == \"PostToolUseFailure\"", "PostToolUseFailure configured", "raw_retained", "hook_error_evidence_only", null, HookRawTarget),
        new("hook.post_tool_failure.is_interrupt", "$.is_interrupt", "boolean", false, "hook_event_name == \"PostToolUseFailure\"", "producer emits is_interrupt", "raw_retained", "hook_context_only", null, HookRawTarget),
        new("hook.post_tool_failure.duration_ms", "$.duration_ms", "number", false, "hook_event_name == \"PostToolUseFailure\"", "producer emits duration_ms", "corroboration_only", "hook_context_only", null, HookRawTarget),
        new("hook.subagent_start.agent_id", "$.agent_id", "string", true, "hook_event_name == \"SubagentStart\"", "SubagentStart configured", "normalized", "hook_native_run_identity", "CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEvent.RunNativeId", null),
        new("hook.subagent_start.agent_type", "$.agent_type", "string", true, "hook_event_name == \"SubagentStart\"", "SubagentStart configured", "raw_retained", "none", null, HookRawTarget),
        new("hook.subagent_stop.agent_id", "$.agent_id", "string", true, "hook_event_name == \"SubagentStop\"", "SubagentStop configured", "normalized", "hook_native_run_identity", "CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEvent.RunNativeId", null),
        new("hook.subagent_stop.stop_hook_active", "$.stop_hook_active", "boolean", true, "hook_event_name == \"SubagentStop\"", "SubagentStop configured", "raw_retained", "hook_lifecycle_context", null, HookRawTarget),
        new("hook.subagent_stop.agent_type", "$.agent_type", "string", true, "hook_event_name == \"SubagentStop\"", "SubagentStop configured", "raw_retained", "none", null, HookRawTarget),
        new("hook.subagent_stop.agent_transcript_path", "$.agent_transcript_path", "string path", true, "hook_event_name == \"SubagentStop\"", "SubagentStop configured", "raw_retained", "none", null, HookRawTarget),
        new("hook.subagent_stop.last_assistant_message", "$.last_assistant_message", "string", true, "hook_event_name == \"SubagentStop\"", "SubagentStop configured", "raw_retained", "none", null, HookRawTarget),
        new("hook.subagent_stop.background_tasks", "$.background_tasks", "array", false, "hook_event_name == \"SubagentStop\"", "Claude Code v2.1.145 or later AND task registry is reachable", "raw_retained", "none", null, HookRawTarget),
        new("hook.subagent_stop.session_crons", "$.session_crons", "array", false, "hook_event_name == \"SubagentStop\"", "Claude Code v2.1.145 or later AND task registry is reachable", "raw_retained", "none", null, HookRawTarget),
        new("hook.stop.stop_hook_active", "$.stop_hook_active", "boolean", true, "hook_event_name == \"Stop\"", "Stop configured", "raw_retained", "hook_lifecycle_context", null, HookRawTarget),
        new("hook.stop.last_assistant_message", "$.last_assistant_message", "string", true, "hook_event_name == \"Stop\"", "Stop configured", "raw_retained", "none", null, HookRawTarget),
        new("hook.stop.background_tasks", "$.background_tasks", "array", false, "hook_event_name == \"Stop\"", "Claude Code v2.1.145 or later AND task registry is reachable", "raw_retained", "none", null, HookRawTarget),
        new("hook.stop.session_crons", "$.session_crons", "array", false, "hook_event_name == \"Stop\"", "Claude Code v2.1.145 or later AND task registry is reachable", "raw_retained", "none", null, HookRawTarget),
        new("hook.stop_failure.error", "$.error", "string enum", true, "hook_event_name == \"StopFailure\"", "StopFailure configured", "corroboration_only", "hook_lifecycle_only", null, HookRawTarget),
        new("hook.stop_failure.error_details", "$.error_details", "string", false, "hook_event_name == \"StopFailure\"", "producer emits error_details", "raw_retained", "none", null, HookRawTarget),
        new("hook.stop_failure.last_assistant_message", "$.last_assistant_message", "string", false, "hook_event_name == \"StopFailure\"", "producer emits last_assistant_message", "raw_retained", "none", null, HookRawTarget),
        new("hook.session_end.reason", "$.reason", "string enum", true, "hook_event_name == \"SessionEnd\"", "SessionEnd configured", "corroboration_only", "hook_lifecycle_only", null, HookRawTarget)
    ];

    private const string RawPayloadTarget = "CopilotAgentObservability.Telemetry.RawTelemetryRecord.PayloadJson";
    private const string HookRawTarget = "CopilotAgentObservability.Telemetry.Sessions.SessionEventContent.ContentJson";

    private static readonly IReadOnlyDictionary<string, ExpectedFieldDetail> ExpectedFieldDetails = ParseFieldDetails(
        """
        otel.trace_id|official-monitoring|documented_not_live_observed|trace_identity|Use the existing OTLP decoder representation without guessing or synthesizing an ID.|Keep TraceId null and do not bind a Session.
        otel.span_id|official-monitoring|documented_not_live_observed|span_identity|Use the existing OTLP decoder representation without guessing or synthesizing an ID.|Keep SpanId null and reject authoritative promotion.
        otel.parent_span_id|official-monitoring|documented_not_live_observed|span_parentage|Use the exact decoded parent ID.|Keep ParentSpanId null; ownership remains unresolved.
        otel.start_time|official-monitoring|documented_not_live_observed|span_timing|Format through the existing OTLP timestamp conversion.|Keep StartTime null; never substitute Hook capture time.
        otel.end_time|official-monitoring|documented_not_live_observed|span_timing|Format through the existing OTLP timestamp conversion.|Keep EndTime null; never substitute Hook capture time.
        otel.status_code|official-monitoring|documented_not_live_observed|span_status|Resolve only through derived mapping otel.status_resolution.|Keep Status null; tool success is never a fallback.
        otel.status_message|official-monitoring|documented_not_live_observed|raw_error_detail|No field-level extraction; retain only inside the existing raw OTLP payload.|Do not invent a raw-error DTO or copy the message into sanitized metadata.
        otel.span_name|live-print-observation|live_observed|producer_span_name|Resolve only through derived mappings otel.operation_classification and otel.category_classification; the raw span name itself stays inside the raw payload.|Keep Operation null and Category unknown for absent or unrecognized names.
        otel.span_type|official-monitoring|documented_not_live_observed|producer_span_type|No field-level extraction.|Do not invent source_span_type.
        otel.session_id|official-monitoring|documented_not_live_observed|native_session_candidate|Compare byte-for-byte only after a Claude Hook native session ID exists; never persist as a new native ID by itself.|Leave OTel evidence unbound.
        otel.request_model|official-monitoring|documented_not_live_observed|request_model|Copy the emitted model string.|Keep RequestModel null; do not use configured/requested defaults.
        otel.ttft_ms|official-monitoring|documented_not_live_observed|ttft|No normalized target exists.|Never derive TTFT from span duration.
        otel.input_tokens|official-monitoring|documented_not_live_observed|input_tokens|Copy the bounded integer.|Keep InputTokens null; do not zero-fill.
        otel.output_tokens|official-monitoring|documented_not_live_observed|output_tokens|Copy the bounded integer.|Keep OutputTokens null; do not zero-fill.
        otel.cache_read_tokens|official-monitoring|documented_not_live_observed|cache_read_tokens|Copy the bounded integer.|Keep CacheReadTokens null; do not zero-fill.
        otel.cache_creation_tokens|official-monitoring|documented_not_live_observed|cache_creation_tokens|Copy the bounded integer.|Keep CacheCreationTokens null; do not zero-fill.
        otel.attempt|official-monitoring|documented_not_live_observed|attempt_count|No normalized target exists.|Do not infer attempt 1.
        otel.retry_event_attempt|official-monitoring|documented_not_live_observed|retry_attempt|No retry collection target exists.|Do not synthesize per-attempt events.
        otel.tool_name|official-monitoring|documented_not_live_observed|tool_name|Apply the existing bounded free-form-name sanitizer.|Keep ToolName null; do not infer it from timing or Hook content.
        otel.tool_use_id|official-monitoring|documented_not_live_observed|tool_correlation_id|Compare only after exact Session binding; never establish Session binding.|Keep tool correlation absent.
        otel.permission_wait_ms|official-monitoring|documented_not_live_observed|permission_wait|No permission-wait target exists.|Never derive permission wait by subtracting tool duration.
        otel.permission_decision|official-monitoring|documented_not_live_observed|permission_decision|No permission-decision target exists.|Do not infer acceptance from later tool execution.
        otel.tool_success|official-monitoring|documented_not_live_observed|tool_status_corroboration|Use only in derived mapping otel.status_resolution to record agreement/conflict; never fill Status.|No fallback status is produced.
        otel.tool_error_category|official-monitoring|documented_not_live_observed|raw_error_value|Retain only inside the existing raw OTLP payload; no observable producer discriminator currently authorizes ErrorType normalization.|Keep ErrorType null.
        otel.tool_error_detail|official-monitoring|documented_not_live_observed|raw_error_detail|Retain only inside the existing raw OTLP payload.|Never copy full detail into ErrorType or sanitized outputs.
        otel.user_prompt|live-prompt-capture-observation|live_observed|raw_prompt|Retain only inside the existing raw OTLP payload; inspect in place for capture evidence only when value.stringValue is non-empty and not ordinal-equal to <REDACTED>.|Exact <REDACTED>, empty or absent user_prompt/value.stringValue, non-string values, and user_prompt_length without a qualifying user_prompt are not_captured; never copy prompt content into sanitized outputs.
        otel.tool_output_event|official-monitoring|documented_not_live_observed|raw_tool_content|Retain only inside the existing raw OTLP payload.|Do not reconstruct missing input/output.
        otel.file_path|official-monitoring|documented_not_live_observed|sensitive_path|Retain only inside the existing raw OTLP payload.|Never expose or use the path for identity, ownership, or binding.
        otel.agent_id|official-monitoring|documented_not_live_observed|producer_agent_identifier|No normalization; this is not MonitorSpanProjection.AgentName and is never ownership or binding evidence.|Keep all agent-identifier projections null; ownership uses ParentSpanId only.
        otel.parent_agent_id|official-monitoring|documented_not_live_observed|producer_parent_agent_identifier|No normalization; parentage comes only from ParentSpanId.|Keep ownership unresolved when ParentSpanId is absent.
        otel.subagent_type|official-monitoring|documented_not_live_observed|producer_agent_label|No normalization; MonitorSpanProjection.ToolType has different semantics.|Do not infer subagent type from tool name.
        otel.response_model_output|official-monitoring|documented_not_live_observed|raw_response|Retain only inside the existing raw OTLP payload.|Never copy response content into sanitized DTOs.
        otel.hook_definitions|official-monitoring|documented_not_live_observed|raw_configuration|Retain only inside the existing raw OTLP payload.|Never expose configuration detail through sanitized outputs.
        otel.reasoning_tokens|no-producer-evidence|not_documented_or_live_observed|reasoning_tokens|No producer claim or mapping is established.|Keep ReasoningTokens null.
        hook.session_id|official-hooks|documented_not_live_observed|native_session_identity|Copy exactly; source surface validation is the Task10B seam.|Reject conversion; never infer from paths, prompt IDs, or timing.
        hook.event_name|official-hooks|documented_not_live_observed|hook_lifecycle_type|Require exact ordinal membership in the documented event allowlist.|Reject conversion; never infer event type from field shape.
        hook.prompt_id|official-hooks|documented_not_live_observed|event_correlation_label|Carry in SessionIngestEvent.Payload, then secret-filter the complete payload before content storage.|Keep absent; prompt ID never binds a Session or trace.
        hook.transcript_path|official-hooks|documented_not_live_observed|sensitive_path|Carry in SessionIngestEvent.Payload, then secret-filter before content storage.|Reject conversion; never use the path for identity, repository attribution, or sanitized output.
        hook.cwd|official-hooks|documented_not_live_observed|sensitive_path|Carry in SessionIngestEvent.Payload, then secret-filter before content storage.|Reject conversion; never infer workspace, repository, identity, or binding.
        hook.permission_mode|official-hooks|documented_not_live_observed|permission_context_label|Carry in SessionIngestEvent.Payload, then secret-filter before content storage.|Do not substitute a configured/default permission mode.
        hook.effort_level|official-hooks|documented_not_live_observed|effort_label|Carry in SessionIngestEvent.Payload, then secret-filter before content storage.|Do not copy requested effort as observed effort.
        hook.conditional_agent_id|official-hooks|documented_not_live_observed|native_run_identity|Copy exactly for the enclosing Hook event; never treat as Session or OTel parentage.|Keep RunNativeId null.
        hook.conditional_agent_type|official-hooks|documented_not_live_observed|agent_label|Carry in SessionIngestEvent.Payload, then secret-filter before content storage.|Never use an agent label for ownership or binding.
        hook.session_start.source|official-hooks|documented_not_live_observed|lifecycle_context|Carry in Payload; source=resume corroborates lifecycle only and lacks the target native session ID required for ExplicitLink.|Reject invalid SessionStart; never infer an explicit link.
        hook.session_start.model|official-hooks|documented_not_live_observed|model_label|Carry in SessionIngestEvent.Payload, then secret-filter before content storage.|Do not substitute configured/requested model.
        hook.session_start.session_title|official-hooks|documented_not_live_observed|raw_session_label|Carry in SessionIngestEvent.Payload, then secret-filter before content storage.|Keep absent; never synthesize a title or expose it through sanitized projections.
        hook.user_prompt_submit.prompt|official-hooks|documented_not_live_observed|raw_prompt|Carry in SessionIngestEvent.Payload, then secret-filter before content storage.|Reject invalid event; never expose prompt in sanitized metadata.
        hook.pre_tool_use.tool_name|official-hooks|documented_not_live_observed|tool_label|Carry in Payload, then secret-filter before content storage.|Reject invalid event; never use tool name for binding.
        hook.pre_tool_use.tool_input|official-hooks|documented_not_live_observed|raw_tool_input|Carry in Payload, then secret-filter before content storage.|Reject invalid event; never expose input through sanitized metadata.
        hook.pre_tool_use.tool_use_id|official-hooks|documented_not_live_observed|tool_correlation_id|Carry in Payload; compare with OTel only after exact Session binding.|Reject invalid event; never join by tool name or time.
        hook.permission_request.tool_name|official-hooks|documented_not_live_observed|tool_label|Carry in Payload, then secret-filter before content storage.|Reject invalid event; never infer a decision.
        hook.permission_request.tool_input|official-hooks|documented_not_live_observed|raw_tool_input|Carry in Payload, then secret-filter before content storage.|Reject invalid event; never expose input through sanitized metadata.
        hook.permission_request.permission_suggestions|official-hooks|documented_not_live_observed|permission_context|Carry in Payload, then secret-filter before content storage.|Keep absent; never infer allow or deny.
        hook.post_tool_use.tool_name|official-hooks|documented_not_live_observed|tool_label|Carry in Payload, then secret-filter before content storage.|Reject invalid event.
        hook.post_tool_use.tool_input|official-hooks|documented_not_live_observed|raw_tool_input|Carry in Payload, then secret-filter before content storage.|Reject invalid event.
        hook.post_tool_use.tool_use_id|official-hooks|documented_not_live_observed|tool_correlation_id|Carry in Payload; compare only after exact Session binding.|Reject invalid event; never bind by this ID.
        hook.post_tool_use.tool_response|official-hooks|documented_not_live_observed|raw_tool_output|Carry in Payload, then secret-filter before content storage.|Reject invalid event; never reconstruct from OTel status.
        hook.post_tool_use.duration_ms|official-hooks|documented_not_live_observed|hook_duration_context|Carry in Payload for Hook evidence only.|Keep absent; never create or overwrite OTel timing.
        hook.post_tool_failure.tool_name|official-hooks|documented_not_live_observed|tool_label|Carry in Payload, then secret-filter before content storage.|Reject invalid event.
        hook.post_tool_failure.tool_input|official-hooks|documented_not_live_observed|raw_tool_input|Carry in Payload, then secret-filter before content storage.|Reject invalid event.
        hook.post_tool_failure.tool_use_id|official-hooks|documented_not_live_observed|tool_correlation_id|Carry in Payload; compare only after exact Session binding.|Reject invalid event; never bind by this ID.
        hook.post_tool_failure.error|official-hooks|documented_not_live_observed|raw_error_detail|Carry in Payload, then secret-filter before content storage.|Reject invalid event; never overwrite OTel Status or ErrorType.
        hook.post_tool_failure.is_interrupt|official-hooks|documented_not_live_observed|failure_context|Carry in Payload, then secret-filter before content storage.|Keep absent; never infer false.
        hook.post_tool_failure.duration_ms|official-hooks|documented_not_live_observed|hook_duration_context|Carry in Payload for Hook evidence only.|Keep absent; never create or overwrite OTel timing.
        hook.subagent_start.agent_id|official-hooks|documented_not_live_observed|native_run_identity|Copy exactly; never treat as Session identity or OTel parentage.|Reject invalid event.
        hook.subagent_start.agent_type|official-hooks|documented_not_live_observed|agent_label|Carry in Payload, then secret-filter before content storage.|Reject invalid event; never infer from tool name or use for ownership.
        hook.subagent_stop.agent_id|official-hooks|documented_not_live_observed|native_run_identity|Copy exactly; never treat as Session identity or OTel parentage.|Reject invalid event.
        hook.subagent_stop.stop_hook_active|official-hooks|documented_not_live_observed|lifecycle_context|Carry in Payload, then secret-filter before content storage.|Reject invalid event; never infer false.
        hook.subagent_stop.agent_type|official-hooks|documented_not_live_observed|agent_label|Carry in Payload, then secret-filter before content storage.|Reject invalid event; never use for ownership or binding.
        hook.subagent_stop.agent_transcript_path|official-hooks|documented_not_live_observed|sensitive_path|Carry in Payload, then secret-filter before content storage.|Reject invalid event; never use path for identity, binding, or fallback content.
        hook.subagent_stop.last_assistant_message|official-hooks|documented_not_live_observed|raw_response|Carry in Payload, then secret-filter before content storage.|Reject invalid event; never read transcript path as fallback or expose through sanitized reads.
        hook.subagent_stop.background_tasks|official-hooks|documented_not_live_observed|raw_task_registry|Carry the producer array of objects in SessionIngestEvent.Payload, then secret-filter before content storage.|Keep absent; never infer task state or expose it through sanitized projections.
        hook.subagent_stop.session_crons|official-hooks|documented_not_live_observed|raw_cron_registry|Carry the producer array of objects in SessionIngestEvent.Payload, then secret-filter before content storage.|Keep absent; never infer scheduled work or expose it through sanitized projections.
        hook.stop.stop_hook_active|official-hooks|documented_not_live_observed|lifecycle_context|Carry in Payload, then secret-filter before content storage.|Reject invalid event; never infer terminal success.
        hook.stop.last_assistant_message|official-hooks|documented_not_live_observed|raw_response|Carry in Payload, then secret-filter before content storage.|Reject invalid event; never parse transcript path as fallback.
        hook.stop.background_tasks|official-hooks|documented_not_live_observed|raw_task_registry|Carry the producer array of objects in SessionIngestEvent.Payload, then secret-filter before content storage.|Keep absent; never infer task state or expose it through sanitized projections.
        hook.stop.session_crons|official-hooks|documented_not_live_observed|raw_cron_registry|Carry the producer array of objects in SessionIngestEvent.Payload, then secret-filter before content storage.|Keep absent; never infer scheduled work or expose it through sanitized projections.
        hook.stop_failure.error|official-hooks|documented_not_live_observed|hook_terminal_error_category|Carry in Payload as Hook terminal evidence only.|Reject invalid event; never overwrite OTel Status or classify from free-form text.
        hook.stop_failure.error_details|official-hooks|documented_not_live_observed|raw_error_detail|Carry in Payload, then secret-filter before content storage.|Keep absent; never expose through sanitized reads.
        hook.stop_failure.last_assistant_message|official-hooks|documented_not_live_observed|raw_error_detail|Carry in Payload, then secret-filter before content storage.|Keep absent; never expose through sanitized reads.
        hook.session_end.reason|official-hooks|documented_not_live_observed|hook_lifecycle_reason|Carry in Payload as Hook lifecycle evidence only.|Reject invalid event; never infer reason from process exit or overwrite OTel Status.
        """);

    private static readonly ExpectedDerivedMapping[] ExpectedOtelDerivedMappings =
    [
        new("otel.raw_payload", "raw_transport", null, null, RawPayloadTarget, "Persist the accepted complete OTLP request through the existing raw store; the listed field IDs define the audited producer leaves, not a reconstructed payload.", ExpectedOtelFields.Select(field => field.FieldId).ToArray()),
        new("otel.duration", "otel_timing", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.DurationMs", null, null, "When both timestamps exist and end is not before start, compute (end-start)/1,000,000; otherwise null.", ["otel.start_time", "otel.end_time"]),
        new("otel.operation_classification", "otel_observed_value", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.Operation", null, null, "Exact recognized-name transform per the canonical classification table: claude_code.llm_request => chat; claude_code.tool => execute_tool; claude_code.interaction, claude_code.tool.blocked_on_user, claude_code.tool.execution, claude_code.hook => null. Unrecognized names never classify by substring, attribute inference, or hierarchy position.", ["otel.span_name"]),
        new("otel.category_classification", "otel_observed_value", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.Category", null, null, "When the resolved status is error, Category is error. Otherwise per the canonical classification table: claude_code.llm_request => llm_call; claude_code.tool => tool_call; claude_code.hook => hook; claude_code.interaction, claude_code.tool.blocked_on_user, claude_code.tool.execution => unknown. agent_invocation is never derived from these names or tool_name values.", ["otel.span_name", "otel.status_code"]),
        new("otel.status_resolution", "otel.status_code_only", "CopilotAgentObservability.Telemetry.MonitorSpanProjection.Status", null, null, "Apply the five ordered cases; evaluate tool_success agreement/conflict transiently for contract validation only. Never persist the evaluation, invent a target, or emit a reason code.", ["otel.status_code", "otel.tool_success"])
    ];

    private static readonly ExpectedDerivedMapping[] ExpectedHookDerivedMappings =
    [
        new("hook.canonical_source_event_id", "hook_explicit_event_identity", "CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEvent.SourceEventId", null, null, "Hash the accepted complete producer object with the existing canonical property ordering; field IDs define the audited leaves and do not reconstruct omitted producer members.", ExpectedHookFields.Select(field => field.FieldId).ToArray()),
        new("hook.payload_and_secret_filtered_content", "raw_transport_then_secret_filtered_storage", null, "CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEvent.Payload", HookRawTarget, "Carry the accepted complete producer object in SessionIngestEvent.Payload; secret-filter the complete payload before persisting SessionEventContent.ContentJson. Payload is transport, not a normalized field target.", ExpectedHookFields.Select(field => field.FieldId).ToArray()),
        new("hook.capture_time", "transport_metadata_only", "CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEvent.OccurredAtValue", null, null, "Generate hook-forward capture time with an explicit offset after an event object is accepted; never treat it as producer timing, duration, ordering, ownership, or binding evidence.", ["hook.event_name"])
    ];

    private static readonly string[] ExpectedHookEvents =
    [
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "PostToolUse",
        "PostToolUseFailure", "SubagentStart", "SubagentStop", "Stop", "StopFailure", "SessionEnd"
    ];

    private static readonly HookApplicability[] ExpectedHookApplicability =
    [
        new("SessionStart", ["hook.session_id", "hook.event_name", "hook.transcript_path", "hook.cwd", "hook.session_start.source"], ["hook.prompt_id", "hook.permission_mode", "hook.effort_level", "hook.conditional_agent_id", "hook.conditional_agent_type", "hook.session_start.model", "hook.session_start.session_title"]),
        new("UserPromptSubmit", ["hook.session_id", "hook.event_name", "hook.transcript_path", "hook.cwd", "hook.user_prompt_submit.prompt"], ["hook.prompt_id", "hook.permission_mode", "hook.effort_level", "hook.conditional_agent_id", "hook.conditional_agent_type"]),
        new("PreToolUse", ["hook.session_id", "hook.event_name", "hook.transcript_path", "hook.cwd", "hook.pre_tool_use.tool_name", "hook.pre_tool_use.tool_input", "hook.pre_tool_use.tool_use_id"], ["hook.prompt_id", "hook.permission_mode", "hook.effort_level", "hook.conditional_agent_id", "hook.conditional_agent_type"]),
        new("PermissionRequest", ["hook.session_id", "hook.event_name", "hook.transcript_path", "hook.cwd", "hook.permission_request.tool_name", "hook.permission_request.tool_input"], ["hook.prompt_id", "hook.permission_mode", "hook.effort_level", "hook.conditional_agent_id", "hook.conditional_agent_type", "hook.permission_request.permission_suggestions"]),
        new("PostToolUse", ["hook.session_id", "hook.event_name", "hook.transcript_path", "hook.cwd", "hook.post_tool_use.tool_name", "hook.post_tool_use.tool_input", "hook.post_tool_use.tool_use_id", "hook.post_tool_use.tool_response"], ["hook.prompt_id", "hook.permission_mode", "hook.effort_level", "hook.conditional_agent_id", "hook.conditional_agent_type", "hook.post_tool_use.duration_ms"]),
        new("PostToolUseFailure", ["hook.session_id", "hook.event_name", "hook.transcript_path", "hook.cwd", "hook.post_tool_failure.tool_name", "hook.post_tool_failure.tool_input", "hook.post_tool_failure.tool_use_id", "hook.post_tool_failure.error"], ["hook.prompt_id", "hook.permission_mode", "hook.effort_level", "hook.conditional_agent_id", "hook.conditional_agent_type", "hook.post_tool_failure.is_interrupt", "hook.post_tool_failure.duration_ms"]),
        new("SubagentStart", ["hook.session_id", "hook.event_name", "hook.transcript_path", "hook.cwd", "hook.subagent_start.agent_id", "hook.subagent_start.agent_type"], ["hook.prompt_id", "hook.permission_mode", "hook.effort_level"]),
        new("SubagentStop", ["hook.session_id", "hook.event_name", "hook.transcript_path", "hook.cwd", "hook.subagent_stop.agent_id", "hook.subagent_stop.stop_hook_active", "hook.subagent_stop.agent_type", "hook.subagent_stop.agent_transcript_path", "hook.subagent_stop.last_assistant_message"], ["hook.prompt_id", "hook.permission_mode", "hook.effort_level", "hook.subagent_stop.background_tasks", "hook.subagent_stop.session_crons"]),
        new("Stop", ["hook.session_id", "hook.event_name", "hook.transcript_path", "hook.cwd", "hook.stop.stop_hook_active", "hook.stop.last_assistant_message"], ["hook.prompt_id", "hook.permission_mode", "hook.effort_level", "hook.conditional_agent_id", "hook.conditional_agent_type", "hook.stop.background_tasks", "hook.stop.session_crons"]),
        new("StopFailure", ["hook.session_id", "hook.event_name", "hook.transcript_path", "hook.cwd", "hook.stop_failure.error"], ["hook.prompt_id", "hook.permission_mode", "hook.effort_level", "hook.conditional_agent_id", "hook.conditional_agent_type", "hook.stop_failure.error_details", "hook.stop_failure.last_assistant_message"]),
        new("SessionEnd", ["hook.session_id", "hook.event_name", "hook.transcript_path", "hook.cwd", "hook.session_end.reason"], ["hook.prompt_id", "hook.permission_mode", "hook.effort_level", "hook.conditional_agent_id", "hook.conditional_agent_type"])
    ];

    private static readonly BindingRow[] ExpectedBindingRows =
    [
        new("Identical native session ID", "Mapping fields `hook.session_id` and `otel.session_id` are both present and their UTF-8 bytes are identical. The Hook value resolves to exactly one Session.", "Bind the OTel trace evidence to that Session with `CopilotAgentObservability.Telemetry.Sessions.SessionBindingKind.Native`; lower-authority context cannot participate."),
        new("Explicit resume/handoff", "`CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEnvelope.ExplicitLink` contains an exact target `CopilotAgentObservability.LocalMonitor.Sessions.SessionExplicitLink.NativeSessionId`, and `CopilotAgentObservability.LocalMonitor.Sessions.SessionExplicitLink.Kind` is `resume` or `handoff`.", "Merge only the explicitly named Sessions with `CopilotAgentObservability.Telemetry.Sessions.SessionBindingKind.ExplicitResume` or `CopilotAgentObservability.Telemetry.Sessions.SessionBindingKind.ExplicitHandoff`."),
        new("Byte-equivalent trace context", "A producer event and an OTel record carry the complete same trace context byte-for-byte, with provenance for both values.", "**Deferred in Session v1:** the current envelope carries only `trace_id`, which is insufficient. No `TraceContext` binding is emitted until a provenance-bearing complete trace-context DTO is added and separately reviewed.")
    ];

    private static void AssertFieldEquals(ExpectedField expected, JsonElement actual)
    {
        Assert.Equal(expected.FieldId, Value(actual.GetProperty("field_id")));
        Assert.Equal(expected.ProducerPath, Value(actual.GetProperty("producer_path")));
        Assert.Equal(expected.ProducerType, Value(actual.GetProperty("producer_type")));
        Assert.Equal(expected.Required, actual.GetProperty("required").GetBoolean());
        Assert.Equal(expected.Selector, Value(actual.GetProperty("selector")));
        Assert.Equal(expected.ContentGate, Value(actual.GetProperty("content_gate")));
        Assert.Equal(expected.EvidenceSource, Value(actual.GetProperty("evidence_source")));
        Assert.Equal(expected.EvidenceStatus, Value(actual.GetProperty("evidence_status")));
        Assert.Equal(expected.SemanticClass, Value(actual.GetProperty("semantic_class")));
        Assert.Equal(expected.Disposition, Value(actual.GetProperty("disposition")));
        Assert.Equal(expected.Authority, Value(actual.GetProperty("authority")));
        Assert.Equal(expected.NormalizedTarget, NullableString(actual, "normalized_target"));
        Assert.Equal(expected.RawStorageTarget, NullableString(actual, "raw_storage_target"));
        Assert.Equal(expected.Transform, Value(actual.GetProperty("transform")));
        Assert.Equal(expected.AbsenceBehavior, Value(actual.GetProperty("absence_behavior")));
        if (expected.ResolverGroup is null)
        {
            Assert.False(actual.TryGetProperty("resolver_group", out _));
        }
        else
        {
            Assert.True(actual.TryGetProperty("resolver_group", out var resolver));
            Assert.Equal(JsonValueKind.String, resolver.ValueKind);
            Assert.Equal(expected.ResolverGroup, resolver.GetString());
        }
    }

    private static void AssertDerivedMappingEquals(ExpectedDerivedMapping expected, JsonElement actual)
    {
        string[] expectedProperties = expected.MappingId switch
        {
            "otel.status_resolution" => ["mapping_id", "input_field_ids", "authority", "normalized_target", "raw_storage_target", "transform", "corroboration_persistence", "cases"],
            _ when expected.MappingId.StartsWith("hook.", StringComparison.Ordinal) => ["mapping_id", "input_field_ids", "authority", "normalized_target", "transport_target", "raw_storage_target", "transform"],
            _ => ["mapping_id", "input_field_ids", "authority", "normalized_target", "raw_storage_target", "transform"]
        };
        Assert.Equal(expectedProperties, actual.EnumerateObject().Select(property => property.Name));
        Assert.Equal(expected.MappingId, Value(actual.GetProperty("mapping_id")));
        Assert.Equal(expected.Authority, Value(actual.GetProperty("authority")));
        Assert.Equal(expected.NormalizedTarget, NullableString(actual, "normalized_target"));
        Assert.Equal(expected.TransportTarget, OptionalNullableString(actual, "transport_target"));
        Assert.Equal(expected.RawStorageTarget, NullableString(actual, "raw_storage_target"));
        Assert.Equal(expected.Transform, Value(actual.GetProperty("transform")));
        Assert.Equal(expected.InputFieldIds, actual.GetProperty("input_field_ids").EnumerateArray().Select(Value).ToArray());
    }

    private static void AssertMappingHeader(JsonElement root, string adapter, string mappingStatus, string manifestRule)
    {
        Assert.Equal("v1", Value(root.GetProperty("contract_version")));
        Assert.Equal("claude-code", Value(root.GetProperty("source_surface")));
        Assert.Equal(adapter, Value(root.GetProperty("source_adapter")));
        Assert.Equal("claude-code-otel+claude-code-hook", Value(root.GetProperty("registry_label")));
        Assert.Equal(mappingStatus, Value(root.GetProperty("mapping_status")));
        Assert.Equal(manifestRule, Value(root.GetProperty("manifest_rule")));
    }

    private static void AssertJsonExact(JsonElement actual, string expectedJson)
    {
        using var expected = JsonDocument.Parse(expectedJson);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual),
            $"Expected JSON {expected.RootElement.GetRawText()} but found {actual.GetRawText()}.");
    }

    private static void AssertFieldMultisets(ExpectedField[] expected, JsonElement actualFields)
    {
        var actual = actualFields.EnumerateArray().ToArray();
        AssertMultiset(expected.Select(field => field.ProducerType), actual.Select(field => Value(field.GetProperty("producer_type"))));
        AssertMultiset(expected.Select(field => field.Required.ToString()), actual.Select(field => field.GetProperty("required").GetBoolean().ToString()));
        AssertMultiset(expected.Select(field => field.Selector), actual.Select(field => Value(field.GetProperty("selector"))));
        AssertMultiset(expected.Select(field => field.ContentGate), actual.Select(field => Value(field.GetProperty("content_gate"))));
        AssertMultiset(expected.Select(field => field.EvidenceSource), actual.Select(field => Value(field.GetProperty("evidence_source"))));
        AssertMultiset(expected.Select(field => field.EvidenceStatus), actual.Select(field => Value(field.GetProperty("evidence_status"))));
        AssertMultiset(expected.Select(field => field.SemanticClass), actual.Select(field => Value(field.GetProperty("semantic_class"))));
        AssertMultiset(expected.Select(field => field.Disposition), actual.Select(field => Value(field.GetProperty("disposition"))));
        AssertMultiset(expected.Select(field => field.Authority), actual.Select(field => Value(field.GetProperty("authority"))));
        AssertMultiset(expected.Select(field => field.NormalizedTarget ?? "<null>"), actual.Select(field => NullableString(field, "normalized_target") ?? "<null>"));
        AssertMultiset(expected.Select(field => field.RawStorageTarget ?? "<null>"), actual.Select(field => NullableString(field, "raw_storage_target") ?? "<null>"));
        AssertMultiset(expected.Select(field => field.ResolverGroup is null ? "<absent>" : field.ResolverGroup),
            actual.Select(field => !field.TryGetProperty("resolver_group", out var resolver)
                ? "<absent>"
                : resolver.ValueKind == JsonValueKind.Null ? "<null>" : resolver.GetString()!));
    }

    private static void AssertMultiset(IEnumerable<string> expected, IEnumerable<string> actual) => Assert.Equal(
        expected.GroupBy(value => value, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (group.Key, group.Count())),
        actual.GroupBy(value => value, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (group.Key, group.Count())));

    private static string? OptionalNullableString(JsonElement element, string propertyName) =>
        !element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null ? null : value.GetString();

    private static JsonObject CreateHookEnvelope(string eventName)
    {
        var envelope = new JsonObject
        {
            ["session_id"] = "session-1", ["hook_event_name"] = eventName,
            ["transcript_path"] = "C:/synthetic/transcript.jsonl", ["cwd"] = "C:/synthetic"
        };
        switch (eventName)
        {
            case "SessionStart": envelope["source"] = "startup"; break;
            case "UserPromptSubmit": envelope["prompt"] = "synthetic prompt"; break;
            case "PreToolUse": SetToolFields(envelope, includeResponse: false, includeError: false); break;
            case "PermissionRequest": envelope["tool_name"] = "Read"; envelope["tool_input"] = new JsonObject(); break;
            case "PostToolUse": SetToolFields(envelope, includeResponse: true, includeError: false); break;
            case "PostToolUseFailure": SetToolFields(envelope, includeResponse: false, includeError: true); break;
            case "SubagentStart": envelope["agent_id"] = "agent-1"; envelope["agent_type"] = "Explore"; break;
            case "SubagentStop":
                envelope["agent_id"] = "agent-1"; envelope["stop_hook_active"] = true; envelope["agent_type"] = "Explore";
                envelope["agent_transcript_path"] = "C:/synthetic/agent.jsonl"; envelope["last_assistant_message"] = "synthetic"; break;
            case "Stop": envelope["stop_hook_active"] = true; envelope["last_assistant_message"] = "synthetic"; break;
            case "StopFailure": envelope["error"] = "timeout"; break;
            case "SessionEnd": envelope["reason"] = "exit"; break;
        }
        return envelope;
    }

    private static void SetToolFields(JsonObject envelope, bool includeResponse, bool includeError)
    {
        envelope["tool_name"] = "Read"; envelope["tool_input"] = new JsonObject(); envelope["tool_use_id"] = "tool-1";
        if (includeResponse) envelope["tool_response"] = new JsonObject { ["ok"] = true };
        if (includeError) envelope["error"] = "synthetic failure";
    }

    private static JsonObject Clone(JsonObject value) => JsonNode.Parse(value.ToJsonString())!.AsObject();

    private static ExpectedField Field(string fieldId) => Assert.Single(ExpectedHookFields, field => field.FieldId == fieldId);

    private static JsonNode ValidValue(string producerType) => producerType switch
    {
        "string" or "string enum" or "string path" => JsonValue.Create("synthetic")!,
        "boolean" => JsonValue.Create(true)!,
        "number" => JsonValue.Create(7.5)!,
        "integer" => JsonValue.Create(7)!,
        "JSON object" => new JsonObject(),
        "array" => new JsonArray(new JsonObject { ["synthetic"] = true }),
        "producer-defined JSON value" => new JsonObject { ["synthetic"] = true },
        _ => throw new InvalidDataException($"Unknown expected producer type: {producerType}"),
    };

    private static JsonNode WrongTypeValue(string producerType) => producerType switch
    {
        "boolean" => JsonValue.Create("not-boolean")!,
        "number" or "integer" or "JSON object" or "array" => JsonValue.Create("wrong-type")!,
        _ => JsonValue.Create(17)!,
    };

    private static void RemovePath(JsonObject root, string path)
    {
        var segments = path[2..].Split('.');
        var parent = root;
        for (var index = 0; index < segments.Length - 1; index++) parent = parent[segments[index]]!.AsObject();
        parent.Remove(segments[^1]);
    }

    private static void SetPath(JsonObject root, string path, JsonNode value)
    {
        var segments = path[2..].Split('.');
        var parent = root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            parent[segments[index]] ??= new JsonObject();
            parent = parent[segments[index]]!.AsObject();
        }
        parent[segments[^1]] = value;
    }

    private static bool PathExists(JsonObject root, string path)
    {
        JsonNode? current = root;
        foreach (var segment in path[2..].Split('.')) current = current?[segment];
        return current is not null;
    }

    private static BindingRow[] ParseBindingRows(string markdown)
    {
        var match = Regex.Match(markdown,
            "^\\| Binding kind \\| Required evidence \\| Result \\|\\r?\\n\\| --- \\| --- \\| --- \\|\\r?\\n(?<rows>(?:\\| .+ \\| .+ \\| .+ \\|\\r?\\n)+)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        Assert.True(match.Success, "Missing exact-binding table.");
        return match.Groups["rows"].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split(" | ", StringSplitOptions.None))
            .Select(parts => new BindingRow(parts[0][2..], parts[1], parts[2][..^2])).ToArray();
    }

    private static IReadOnlyDictionary<string, ExpectedFieldDetail> ParseFieldDetails(string data) =>
        data.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|'))
            .Select(parts =>
            {
                if (parts.Length != 6) throw new InvalidDataException($"Expected six field-detail columns, got {parts.Length}.");
                return new ExpectedFieldDetail(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5]);
            })
            .ToDictionary(item => item.FieldId, StringComparer.Ordinal);

    private sealed record ExpectedField(string FieldId, string ProducerPath, string ProducerType, bool Required,
        string Selector, string ContentGate, string Disposition, string Authority, string? NormalizedTarget,
        string? RawStorageTarget, string? ResolverGroup = null)
    {
        private ExpectedFieldDetail Detail => ExpectedFieldDetails[FieldId];
        public string EvidenceSource => Detail.EvidenceSource;
        public string EvidenceStatus => Detail.EvidenceStatus;
        public string SemanticClass => Detail.SemanticClass;
        public string Transform => Detail.Transform;
        public string AbsenceBehavior => Detail.AbsenceBehavior;
    }
    private sealed record ExpectedFieldDetail(string FieldId, string EvidenceSource, string EvidenceStatus,
        string SemanticClass, string Transform, string AbsenceBehavior);
    private sealed record ExpectedDerivedMapping(string MappingId, string Authority, string? NormalizedTarget,
        string? TransportTarget, string? RawStorageTarget, string Transform, string[] InputFieldIds);
    private sealed record HookApplicability(string EventName, string[] RequiredFieldIds, string[] OptionalFieldIds);
    internal sealed record BindingRow(string Kind, string Evidence, string Result);

    private static string CreateFixture(string surface, string? unknownProperty = null)
    {
        var capabilities = "{\"availability\":\"unknown\"}";
        var json = $$"""
            {
              "contract_version": "v1",
              "source_surface": "{{surface}}",
              "source_adapter": "fixture-adapter",
              "support_status": "planned",
              "stability": "preview",
              "source_version_detector": {{capabilities}},
              "signals": { "trace": {{capabilities}}, "log": {{capabilities}}, "metric": {{capabilities}}, "hook": {{capabilities}}, "sdk_event": {{capabilities}}, "saved_raw": {{capabilities}} },
              "native_session_identity": {{capabilities}},
              "trace_span_identity": { "trace_id": {{capabilities}}, "span_id": {{capabilities}}, "parentage": {{capabilities}} },
              "timing_ttft": { "timing": {{capabilities}}, "ttft": {{capabilities}} },
              "model_tokens": { "model": {{capabilities}}, "input_tokens": {{capabilities}}, "output_tokens": {{capabilities}}, "total_tokens": {{capabilities}}, "cache_tokens": {{capabilities}}, "reasoning_tokens": {{capabilities}} },
              "retry_attempt": { "retry": {{capabilities}}, "attempt": {{capabilities}} },
              "tool_calls": { "identity": {{capabilities}}, "input": {{capabilities}}, "output": {{capabilities}} },
              "permission": { "wait": {{capabilities}}, "decision": {{capabilities}} },
              "errors": {{capabilities}},
              "agent_ownership": { "main_agent": {{capabilities}}, "sub_agent": {{capabilities}} },
              "prompt_response": { "prompt": {{capabilities}}, "response": {{capabilities}} },
              "file_diff": { "file": {{capabilities}}, "diff": {{capabilities}} },
              "content_capture_gate": {{capabilities}},
              "provenance": { "required_keys": ["source_adapter", "source_version_or_schema_fingerprint", "source_event_or_trace_span_id", "capture_content_state", "normalization_version"] },
              "completeness": { "statuses": ["unbound", "partial", "rich", "full"], "reason_codes": ["missing_native_session_id", "missing_trace_context", "trace_signal_disabled", "content_capture_disabled", "unsupported_source_version", "ingest_gap", "hook_only", "historical_summary_only", "unknown_span_kind", "schema_drift_detected", "planned_source_not_enabled"] }{{(unknownProperty is null ? string.Empty : $", \"{unknownProperty}\": true")}}
            }
            """;

        return json;
    }
}

internal sealed class ClaudeHookContractValidator
{
    private static readonly string[] KnownTypes =
    ["string", "string enum", "string path", "boolean", "number", "integer", "JSON object", "array", "producer-defined JSON value"];
    private readonly string[] allowedEvents;
    private readonly HookField[] fields;

    private ClaudeHookContractValidator(string[] allowedEvents, HookField[] fields)
    {
        this.allowedEvents = allowedEvents;
        this.fields = fields;
    }

    public IReadOnlyList<string> AllowedEvents => allowedEvents;

    public static ClaudeHookContractValidator Create(JsonElement mapping)
    {
        var allowedEvents = mapping.GetProperty("event_type_allowlist").GetProperty("values")
            .EnumerateArray().Select(item => item.GetString()!).ToArray();
        var fields = mapping.GetProperty("fields").EnumerateArray().Select(field =>
        {
            var selector = field.GetProperty("selector").GetString()!;
            var producerType = field.GetProperty("producer_type").GetString()!;
            if (!IsKnownSelector(selector, allowedEvents)) throw new InvalidDataException($"Unknown Hook selector: {selector}");
            if (!KnownTypes.Contains(producerType, StringComparer.Ordinal)) throw new InvalidDataException($"Unknown Hook producer type: {producerType}");
            return new HookField(field.GetProperty("producer_path").GetString()!, selector, producerType, field.GetProperty("required").GetBoolean());
        }).ToArray();
        return new ClaudeHookContractValidator(allowedEvents, fields);
    }

    public IReadOnlyList<string> Validate(JsonObject json) => Validate(json.ToJsonString());

    private IReadOnlyList<string> Validate(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var errors = new List<string>();
        if (!root.TryGetProperty("hook_event_name", out var eventNameValue))
        {
            errors.Add("$.hook_event_name is required.");
            return errors;
        }
        if (eventNameValue.ValueKind != JsonValueKind.String)
        {
            errors.Add("$.hook_event_name must be string.");
            return errors;
        }
        var eventName = eventNameValue.GetString()!;
        if (!allowedEvents.Contains(eventName, StringComparer.Ordinal))
        {
            errors.Add("$.hook_event_name is not an exact allowed event name.");
            return errors;
        }

        foreach (var field in fields.Where(field => Applies(field.Selector, eventName)))
        {
            var present = TryResolve(root, field.Path, out var value);
            if (!present)
            {
                if (field.Required)
                {
                    errors.Add($"{field.Path} is required.");
                }
                continue;
            }

            if (!MatchesType(value, field.ProducerType))
            {
                errors.Add($"{field.Path} must be {field.ProducerType}.");
            }
        }

        return errors;
    }

    private static bool Applies(string selector, string eventName)
    {
        if (selector == "every configured Hook event")
        {
            return true;
        }

        var exact = Regex.Match(selector, "^hook_event_name == \\\"(?<event>[^\\\"]+)\\\"$", RegexOptions.CultureInvariant);
        if (exact.Success)
        {
            return string.Equals(exact.Groups["event"].Value, eventName, StringComparison.Ordinal);
        }

        return selector.StartsWith("any Hook event", StringComparison.Ordinal)
            || selector.StartsWith("hook_event_name is not SubagentStart", StringComparison.Ordinal)
                && eventName is not ("SubagentStart" or "SubagentStop");
    }

    private static bool IsKnownSelector(string selector, IReadOnlyList<string> allowedEvents) =>
        selector == "every configured Hook event"
        || selector.StartsWith("any Hook event where the producer emits ", StringComparison.Ordinal)
        || selector is "hook_event_name is not SubagentStart or SubagentStop and producer emits agent_id"
            or "hook_event_name is not SubagentStart or SubagentStop and producer emits agent_type"
        || allowedEvents.Any(eventName => selector == $"hook_event_name == \"{eventName}\"");

    private static bool TryResolve(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        foreach (var segment in path[2..].Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                value = default;
                return false;
            }
        }
        return true;
    }

    private static bool MatchesType(JsonElement value, string producerType) => producerType switch
    {
        "string" or "string enum" or "string path" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "JSON object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "producer-defined JSON value" => value.ValueKind != JsonValueKind.Undefined,
        _ => false,
    };

    private sealed record HookField(string Path, string Selector, string ProducerType, bool Required);
}

internal sealed class ClaudeStatusContract
{
    private readonly IReadOnlyDictionary<int, StatusCase> cases;

    private ClaudeStatusContract(
        string target,
        string corroborationPersistence,
        string? rawStorageTarget,
        string? diagnosticTarget,
        string? reasonCodeTarget,
        string transform,
        StatusCase[] cases)
    {
        Target = target;
        CorroborationPersistence = corroborationPersistence;
        RawStorageTarget = rawStorageTarget;
        DiagnosticTarget = diagnosticTarget;
        ReasonCodeTarget = reasonCodeTarget;
        Transform = transform;
        this.cases = cases.ToDictionary(item => item.Number);
    }

    public string Target { get; }
    public string CorroborationPersistence { get; }
    public string? RawStorageTarget { get; }
    public string? DiagnosticTarget { get; }
    public string? ReasonCodeTarget { get; }
    public string Transform { get; }

    public static ClaudeStatusContract Parse(JsonElement mapping)
    {
        string? Optional(string name) => !mapping.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null : value.GetString();
        var expected = new[]
        {
            new StatusCase(1, "status.code == ERROR and success is false or absent", "error", "agree_or_missing_not_persisted"),
            new StatusCase(2, "status.code == ERROR and success is true", "error", "conflict_not_persisted_status_wins"),
            new StatusCase(3, "status.code is OK or UNSET and success is true or absent", "ok", "agree_or_missing_not_persisted"),
            new StatusCase(4, "status.code is OK or UNSET and success is false", "ok", "conflict_not_persisted_status_wins"),
            new StatusCase(5, "status.code is absent or invalid regardless of success", null, "no_fallback_not_persisted")
        };
        var actual = mapping.GetProperty("cases").EnumerateArray().Select(item =>
        {
            if (!item.EnumerateObject().Select(property => property.Name)
                .SequenceEqual(["case", "when", "status", "evaluation"], StringComparer.Ordinal))
                throw new InvalidDataException("Unknown Claude status case property shape.");
            return new StatusCase(item.GetProperty("case").GetInt32(), item.GetProperty("when").GetString()!,
                OptionalFrom(item, "status"), item.GetProperty("evaluation").GetString()!);
        }).ToArray();
        if (!expected.SequenceEqual(actual)) throw new InvalidDataException("Unknown Claude status truth table.");
        return new ClaudeStatusContract(
            mapping.GetProperty("normalized_target").GetString()!, mapping.GetProperty("corroboration_persistence").GetString()!,
            Optional("raw_storage_target"), Optional("diagnostic_target"), Optional("reason_code_target"),
            mapping.GetProperty("transform").GetString()!, actual);
    }

    public StatusOutcome Resolve(string? statusCode, bool? success)
    {
        var number = statusCode switch
        {
            "ERROR" when success is true => 2,
            "ERROR" => 1,
            "OK" or "UNSET" when success is false => 4,
            "OK" or "UNSET" => 3,
            _ => 5
        };
        var selected = cases[number];
        return new(selected.Status, selected.Evaluation);
    }

    private static string? OptionalFrom(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    private sealed record StatusCase(int Number, string When, string? Status, string Evaluation);
}

internal sealed record StatusOutcome(string? Status, string Evaluation);

internal sealed class ClaudeBindingContractEvaluator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HashSet<string> bindingKinds;

    public ClaudeBindingContractEvaluator(IEnumerable<SourceCapabilityContractTests.BindingRow> rows) =>
        bindingKinds = rows.Select(row => row.Kind).ToHashSet(StringComparer.Ordinal);

    public SessionBindingKind? Resolve(ClaudeBindingCandidate candidate)
    {
        var native = bindingKinds.Contains("Identical native session ID")
            && !string.IsNullOrWhiteSpace(candidate.HookNativeId) && !string.IsNullOrWhiteSpace(candidate.OtelNativeId)
            && candidate.NativeSessionMatchCount == 1 && ExactUtf8(candidate.HookNativeId, candidate.OtelNativeId);
        var explicitLink = bindingKinds.Contains("Explicit resume/handoff")
            && !string.IsNullOrWhiteSpace(candidate.ExplicitTargetNativeId)
            && candidate.ExplicitLinkKind is "resume" or "handoff";
        var trace = bindingKinds.Contains("Byte-equivalent trace context")
            && candidate.HasCompleteHookTraceContext && candidate.HasCompleteOtelTraceContext
            && candidate.HookTraceContextProvenance && candidate.OtelTraceContextProvenance
            && candidate.HookTraceContext is not null && candidate.OtelTraceContext is not null
            && candidate.HookTraceContext.Length > 0
            && candidate.HookTraceContext.AsSpan().SequenceEqual(candidate.OtelTraceContext);

        if ((native ? 1 : 0) + (explicitLink ? 1 : 0) + (trace ? 1 : 0) != 1) return null;
        if (native) return SessionBindingKind.Native;
        if (trace) return SessionBindingKind.TraceContext;
        return candidate.ExplicitLinkKind == "resume" ? SessionBindingKind.ExplicitResume : SessionBindingKind.ExplicitHandoff;
    }

    private static bool ExactUtf8(string left, string right)
    {
        try
        {
            return StrictUtf8.GetBytes(left).AsSpan().SequenceEqual(StrictUtf8.GetBytes(right));
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}

internal sealed record ClaudeBindingCandidate(
    string? HookNativeId = null,
    string? OtelNativeId = null,
    int NativeSessionMatchCount = 0,
    string? ExplicitTargetNativeId = null,
    string? ExplicitLinkKind = null,
    byte[]? HookTraceContext = null,
    byte[]? OtelTraceContext = null,
    bool HookTraceContextProvenance = false,
    bool OtelTraceContextProvenance = false,
    bool HasCompleteHookTraceContext = false,
    bool HasCompleteOtelTraceContext = false,
    bool TraceIdOnly = false,
    bool PathMatches = false,
    bool TimeMatches = false,
    bool AgentIdMatches = false,
    bool PromptMatches = false);

internal static class SourceCapabilityManifestValidator
{
    public static IReadOnlyList<string> Validate(JsonDocument schema, JsonDocument manifest)
    {
        var errors = new List<string>();
        ValidateNode(schema.RootElement, schema.RootElement, manifest.RootElement, "$", errors);
        return errors;
    }

    private static void ValidateNode(JsonElement rootSchema, JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            ValidateNode(rootSchema, Resolve(rootSchema, reference.GetString()!), value, path, errors);
            return;
        }

        if (schema.TryGetProperty("const", out var constant) && value.GetRawText() != constant.GetRawText())
        {
            errors.Add($"{path} must equal {constant.GetRawText()}.");
        }

        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(candidate => candidate.GetRawText() == value.GetRawText()))
        {
            errors.Add($"{path} is not an allowed value.");
        }

        if (schema.TryGetProperty("type", out var type) && type.GetString() == "string")
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                errors.Add($"{path} must be a string.");
                return;
            }

            if (schema.TryGetProperty("minLength", out var minLength) && value.GetString()!.Length < minLength.GetInt32())
            {
                errors.Add($"{path} must be a non-empty string.");
            }
        }

        if (type.ValueKind != JsonValueKind.Undefined && type.GetString() == "object")
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path} must be an object.");
                return;
            }

            var properties = schema.TryGetProperty("properties", out var declaredProperties) ? declaredProperties : default;
            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var property in required.EnumerateArray())
                {
                    if (!value.TryGetProperty(property.GetString()!, out _))
                    {
                        errors.Add($"{path}.{property.GetString()} is required.");
                    }
                }
            }

            foreach (var property in value.EnumerateObject())
            {
                if (properties.ValueKind != JsonValueKind.Undefined && properties.TryGetProperty(property.Name, out var propertySchema))
                {
                    ValidateNode(rootSchema, propertySchema, property.Value, $"{path}.{property.Name}", errors);
                }
                else if (schema.TryGetProperty("additionalProperties", out var additionalProperties) && additionalProperties.ValueKind == JsonValueKind.False)
                {
                    errors.Add($"{path}.{property.Name} is not allowed.");
                }
            }
        }

        if (type.ValueKind != JsonValueKind.Undefined && type.GetString() == "array")
        {
            if (value.ValueKind != JsonValueKind.Array)
            {
                errors.Add($"{path} must be an array.");
                return;
            }

            var values = value.EnumerateArray().ToArray();
            if (schema.TryGetProperty("minItems", out var minItems) && values.Length < minItems.GetInt32())
            {
                errors.Add($"{path} has too few items.");
            }

            if (schema.TryGetProperty("maxItems", out var maxItems) && values.Length > maxItems.GetInt32())
            {
                errors.Add($"{path} has too many items.");
            }

            if (schema.TryGetProperty("prefixItems", out var prefixItems))
            {
                var prefixes = prefixItems.EnumerateArray().ToArray();
                for (var index = 0; index < Math.Min(values.Length, prefixes.Length); index++)
                {
                    ValidateNode(rootSchema, prefixes[index], values[index], $"{path}[{index}]", errors);
                }

                if (schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.False && values.Length > prefixes.Length)
                {
                    errors.Add($"{path} has additional items.");
                }
            }
        }
    }

    private static JsonElement Resolve(JsonElement root, string reference)
    {
        if (!reference.StartsWith("#/$defs/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported schema reference: {reference}");
        }

        return root.GetProperty("$defs").GetProperty(reference[8..]);
    }
}
