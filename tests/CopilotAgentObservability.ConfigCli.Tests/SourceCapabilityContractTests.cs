using System.Text.Json;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SourceCapabilityContractTests
{
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
            AssertManifestHeader(manifests["claude-code"], "claude-code", "not-implemented", "planned", "preview");
            AssertManifestHeader(manifests["codex-app"], "codex-app", "not-implemented", "planned", "preview");
            AssertManifestHeader(manifests["codex-cli"], "codex-cli", "not-implemented", "planned", "preview");

            AssertAvailabilityMatrix(manifests["github-copilot-vscode"], CopilotAvailabilityMatrix());
            AssertAvailabilityMatrix(manifests["github-copilot-cli"], CopilotAvailabilityMatrix());
            AssertAvailabilityMatrix(manifests["claude-code"], UnknownAvailabilityMatrix());
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
