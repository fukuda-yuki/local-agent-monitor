using System.Text.Json;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SourceCapabilityContractTests
{
    private static readonly string SchemaPath = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "docs", "specifications", "contracts", "source-capabilities", "v1",
        "source-capability-manifest.schema.json");

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

    private static JsonElement Resolve(JsonElement root, string reference)
    {
        var current = root;
        foreach (var segment in reference[2..].Split('/'))
        {
            current = current.GetProperty(segment);
        }

        return current;
    }

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
