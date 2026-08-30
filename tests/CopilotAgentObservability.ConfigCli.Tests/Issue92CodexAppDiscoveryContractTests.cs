using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class Issue92CodexAppDiscoveryContractTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly string ContractRoot = Path.Combine(
        RepositoryRoot,
        "docs", "specifications", "contracts", "source-capabilities", "v1", "codex-app");

    [Fact]
    public void DiscoveryInventoryPinsNoGoAndSeparatesStandaloneAppServerFromDesktopApp()
    {
        using var inventory = ReadJson("discovery-inventory.json");
        var root = inventory.RootElement;

        Assert.Equal(
            [
                "schema_version", "source_surface", "decision", "evaluated_on", "kickoff_sha",
                "validated_versions", "surface_boundary", "version_detection",
                "configuration_contract", "evidence", "content_boundary", "correlation",
                "blocked_profiles", "production_integration_gate"
            ],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal("codex-app-discovery.v1", String(root, "schema_version"));
        Assert.Equal("codex-app", String(root, "source_surface"));
        Assert.Equal("no_go", String(root, "decision"));
        Assert.Equal("2026-07-24", String(root, "evaluated_on"));
        Assert.Equal("07dc219c4f5c5ef56e7810a23c6466a52e90aa97", String(root, "kickoff_sha"));

        var versions = root.GetProperty("validated_versions");
        Assert.Equal("26.715.10079.0", String(versions, "desktop_package"));
        Assert.Equal("0.145.0", String(versions, "cli"));
        Assert.Equal("codex-app-server", String(versions, "app_server_scope"));

        var detection = root.GetProperty("version_detection");
        Assert.Equal("codex_version_command", String(detection, "producer"));
        Assert.True(detection.GetProperty("package_install_location_resolved_for_execution_retry").GetBoolean());
        Assert.False(detection.GetProperty("absolute_install_path_value_retained").GetBoolean());
        Assert.False(detection.GetProperty("private_app_state_read").GetBoolean());

        var boundary = root.GetProperty("surface_boundary");
        Assert.Equal("Codex App", String(boundary, "inventory_target"));
        Assert.Equal("standalone_app_server", String(boundary, "observed_producer"));
        Assert.True(boundary.GetProperty("desktop_package_process_parent_relationship_observed").GetBoolean());
        Assert.False(boundary.GetProperty("desktop_owned_execution_observed").GetBoolean());
        Assert.False(boundary.GetProperty("desktop_to_producer_ownership_observed").GetBoolean());
        Assert.True(boundary.GetProperty("standalone_app_server_is_not_desktop_support").GetBoolean());

        var configuration = root.GetProperty("configuration_contract");
        Assert.Equal("per_command_overrides_only", String(configuration, "mutation"));
        Assert.Equal("disposable_loopback", String(configuration, "receiver"));
        Assert.Equal("disabled", String(configuration, "content_capture"));
        Assert.True(configuration.GetProperty("global_configuration_loaded_by_process").GetBoolean());
        Assert.False(configuration.GetProperty("global_configuration_values_inspected").GetBoolean());
        Assert.False(configuration.GetProperty("global_configuration_written").GetBoolean());
        Assert.False(configuration.GetProperty("unoverridden_global_influence_excluded").GetBoolean());
        Assert.Equal(
            ["otel.log_user_prompt", "otel.environment", "otel.exporter", "otel.trace_exporter", "otel.metrics_exporter"],
            configuration.GetProperty("overridden_key_names").EnumerateArray().Select(Value));
        Assert.Equal(
            "config_layer_observation_only_excluded_from_signal_and_capability_evidence",
            String(configuration, "strict_config_probe"));

        var evidence = root.GetProperty("evidence");
        Assert.Contains(
            "https://developers.openai.com/codex/config-advanced/#observability-and-telemetry",
            evidence.GetProperty("official_documentation").EnumerateArray().Select(Value));
        Assert.Contains(
            "https://github.com/openai/codex/blob/rust-v0.145.0/codex-rs/core/src/tools/registry.rs",
            evidence.GetProperty("official_documentation").EnumerateArray().Select(Value));
        Assert.Contains(
            "https://github.com/openai/codex/blob/rust-v0.145.0/codex-rs/otel/src/events/session_telemetry.rs",
            evidence.GetProperty("official_documentation").EnumerateArray().Select(Value));

        var desktopRetry = evidence.GetProperty("desktop_bundled_producer_retry");
        Assert.True(desktopRetry.GetProperty("package_metadata_readable").GetBoolean());
        Assert.True(desktopRetry.GetProperty("binary_present").GetBoolean());
        Assert.Equal("blocked_windowsapps_access_control", String(desktopRetry, "direct_execution"));
        Assert.False(desktopRetry.GetProperty("standalone_substitution_allowed").GetBoolean());

        var processDiagnostic = evidence.GetProperty("desktop_process_diagnostic");
        Assert.Equal(
            "non_authoritative_package_process_tree_observation",
            String(processDiagnostic, "classification"));
        Assert.True(processDiagnostic.GetProperty("package_root_codex_process_observed").GetBoolean());
        Assert.True(processDiagnostic.GetProperty("package_root_parent_process_relationship_observed").GetBoolean());
        Assert.False(processDiagnostic.GetProperty("app_server_identity_observed").GetBoolean());
        Assert.False(processDiagnostic.GetProperty("desktop_otel_execution_observed").GetBoolean());
        Assert.False(processDiagnostic.GetProperty("pid_values_retained").GetBoolean());
        Assert.False(processDiagnostic.GetProperty("path_values_retained").GetBoolean());
        Assert.False(processDiagnostic.GetProperty("hash_values_retained").GetBoolean());
        Assert.False(processDiagnostic.GetProperty("command_line_read").GetBoolean());
        Assert.False(processDiagnostic.GetProperty("merge_authority").GetBoolean());
        Assert.Equal(
            "scripts/validation/codex-app-discovery/observe-desktop-process-tree.ps1",
            String(processDiagnostic, "replay_template"));

        var attestation = evidence.GetProperty("live_probe_attestation");
        Assert.Equal("non_replayable_attestation", String(attestation, "classification"));
        Assert.False(attestation.GetProperty("verbatim_command_retained").GetBoolean());
        Assert.False(attestation.GetProperty("exact_ephemeral_harness_retained").GetBoolean());
        Assert.False(attestation.GetProperty("raw_probe_output_retained").GetBoolean());
        Assert.Contains("codex", String(attestation, "repository_safe_command_template"), StringComparison.Ordinal);
        Assert.Contains("app-server", String(attestation, "repository_safe_command_template"), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(String(attestation, "execution_environment")));
        Assert.False(string.IsNullOrWhiteSpace(String(attestation, "limitation")));

        var content = root.GetProperty("content_boundary");
        Assert.Equal("not_authorized", String(content, "content_enabled_capture"));
        Assert.False(content.GetProperty("raw_payloads_committed").GetBoolean());
        Assert.False(content.GetProperty("identifier_values_committed").GetBoolean());
        Assert.False(content.GetProperty("resource_attribute_values_committed").GetBoolean());
        Assert.True(content.GetProperty("official_log_events_can_contain_content_when_prompt_logging_is_disabled").GetBoolean());
    }

    [Fact]
    public void SanitizedFixturesRetainOnlyStructuralEvidence()
    {
        using var initialize = ReadJson(Path.Combine("fixtures", "content-disabled-initialize.sanitized.json"));
        using var threadStart = ReadJson(Path.Combine("fixtures", "content-disabled-thread-start.sanitized.json"));

        AssertFixtureEnvelope(
            initialize.RootElement,
            "content-disabled-initialize",
            expectedSpanCount: 6,
            expectedThreadProfileExecuted: false);
        AssertFixtureEnvelope(
            threadStart.RootElement,
            "content-disabled-thread-start",
            expectedSpanCount: 7,
            expectedThreadProfileExecuted: true);
        Assert.Equal(
            ["otel.log_user_prompt", "otel.environment", "otel.exporter", "otel.trace_exporter", "otel.metrics_exporter"],
            initialize.RootElement.GetProperty("routing").GetProperty("overridden_key_names").EnumerateArray().Select(Value));
        Assert.Equal(
            [
                "otel.log_user_prompt", "otel.environment", "otel.exporter",
                "otel.trace_exporter", "otel.metrics_exporter", "history.persistence", "sqlite_home"
            ],
            threadStart.RootElement.GetProperty("routing").GetProperty("overridden_key_names").EnumerateArray().Select(Value));

        var initializeSignal = initialize.RootElement.GetProperty("signal_observation");
        Assert.Equal(5, initializeSignal.GetProperty("distinct_trace_count").GetInt32());
        Assert.Equal(["auth", "initialize"],
            initializeSignal.GetProperty("span_names").EnumerateArray().Select(Value));
        Assert.Equal(4, initializeSignal.GetProperty("hierarchy").GetProperty("root_span_count").GetInt32());
        Assert.Equal(2, initializeSignal.GetProperty("hierarchy").GetProperty("unresolved_parent_reference_count").GetInt32());
        Assert.Equal(32, initializeSignal.GetProperty("trace_id_shape").GetProperty("hex_length").GetInt32());
        Assert.Equal(16, initializeSignal.GetProperty("span_id_shape").GetProperty("hex_length").GetInt32());

        Assert.Equal(
            [
                "department", "env", "experiment.id", "service.name", "service.version",
                "team.id", "telemetry.sdk.language", "telemetry.sdk.name", "telemetry.sdk.version"
            ],
            initializeSignal.GetProperty("resource_attribute_keys").EnumerateArray().Select(Value));
        Assert.Equal(
            [
                "app_server.api_version", "app_server.client_name", "app_server.client_version",
                "app_server.connection_id", "busy_ns", "code.file.path", "code.line.number",
                "code.module.name", "idle_ns", "rpc.method", "rpc.request_id", "rpc.system",
                "rpc.transport", "target", "thread.id", "thread.name"
            ],
            initializeSignal.GetProperty("span_attribute_keys").EnumerateArray().Select(Value));

        var threadStartSignal = threadStart.RootElement.GetProperty("signal_observation");
        Assert.True(threadStartSignal.GetProperty("thread_start_span_observed").GetBoolean());
        Assert.Equal(
            [
                "app_server.api_version", "app_server.client_name", "app_server.client_version",
                "app_server.connection_id", "rpc.method", "rpc.request_id", "rpc.system",
                "rpc.transport"
            ],
            threadStartSignal.GetProperty("thread_start_attribute_keys").EnumerateArray().Select(Value));

        var binding = threadStart.RootElement.GetProperty("protocol_binding");
        Assert.True(binding.GetProperty("thread_profile_executed").GetBoolean());
        Assert.False(binding.GetProperty("turn_profile_executed").GetBoolean());
        Assert.True(binding.GetProperty("native_thread_id_returned").GetBoolean());
        Assert.Equal(JsonValueKind.Null, binding.GetProperty("native_turn_id_returned").ValueKind);
        Assert.False(binding.GetProperty("otel_native_thread_id_observed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, binding.GetProperty("otel_native_turn_id_observed").ValueKind);
        Assert.True(binding.GetProperty("generic_thread_id_observed").GetBoolean());
        Assert.True(binding.GetProperty("generic_thread_id_rejected_as_native").GetBoolean());
    }

    [Fact]
    public void CorrelationTablePreservesExactUnboundAndUnverifiedRelationships()
    {
        using var inventory = ReadJson("discovery-inventory.json");
        var rows = inventory.RootElement.GetProperty("correlation").EnumerateArray()
            .ToDictionary(row => String(row, "mapping"), StringComparer.Ordinal);

        Assert.Equal(
            [
                "desktop_package_to_package_root_process_tree",
                "app_package_to_app_server_process",
                "app_server_process_to_app_session",
                "protocol_thread_to_otel_trace",
                "protocol_turn_to_otel_span",
                "otel_trace_to_span",
                "otel_span_to_parent",
                "concurrent_windows_or_threads",
                "restart_resume_continuity"
            ],
            rows.Keys);
        Assert.Equal("non_authoritative_diagnostic", String(rows["desktop_package_to_package_root_process_tree"], "status"));
        Assert.Equal("unverified", String(rows["app_package_to_app_server_process"], "status"));
        Assert.Equal("unverified", String(rows["app_server_process_to_app_session"], "status"));
        Assert.Equal("unbound", String(rows["protocol_thread_to_otel_trace"], "status"));
        Assert.Equal("unverified", String(rows["protocol_turn_to_otel_span"], "status"));
        Assert.Equal("exact", String(rows["otel_trace_to_span"], "status"));
        Assert.Equal("source_declared_partial", String(rows["otel_span_to_parent"], "status"));
        Assert.Equal("unverified", String(rows["concurrent_windows_or_threads"], "status"));
        Assert.Equal("unverified", String(rows["restart_resume_continuity"], "status"));

        var serialized = inventory.RootElement.GetProperty("correlation").GetRawText();
        foreach (var forbidden in new[]
        {
            "repository_match", "workspace_match", "cwd_match", "timestamp_match",
            "process_match", "arrival_order", "generic_thread_id_as_native"
        })
        {
            Assert.Contains(forbidden, serialized, StringComparison.Ordinal);
        }

        Assert.All(rows.Values, row =>
            Assert.Equal(
                [
                    "mapping", "status", "evidence", "allowed_use", "forbidden_fallbacks"
                ],
                row.EnumerateObject().Select(property => property.Name)));
    }

    [Fact]
    public void Issue93RemainsBlockedWithoutProductionIntegration()
    {
        using var inventory = ReadJson("discovery-inventory.json");
        var gate = inventory.RootElement.GetProperty("production_integration_gate");
        Assert.Equal("blocked", String(gate, "state"));
        Assert.Equal("no_production_integration", String(gate, "implementation_scope"));
        Assert.Equal("no_go", String(gate, "capability_level"));
        Assert.False(gate.GetProperty("session_support_authorized").GetBoolean());
        Assert.Equal(JsonValueKind.Null, gate.GetProperty("maximum_session_completeness").ValueKind);
        Assert.Empty(gate.GetProperty("permitted_before_prerequisites").EnumerateArray());
        Assert.Contains("desktop_session_support_claim", gate.GetProperty("forbidden").EnumerateArray().Select(Value));
        Assert.Contains("full_completeness", gate.GetProperty("forbidden").EnumerateArray().Select(Value));
        Assert.Contains("heuristic_correlation", gate.GetProperty("forbidden").EnumerateArray().Select(Value));
        Assert.Contains("content_capture", gate.GetProperty("forbidden").EnumerateArray().Select(Value));
        Assert.Contains("generic_standalone_app_server_support_claim", gate.GetProperty("forbidden").EnumerateArray().Select(Value));
        Assert.Contains("production_adapter", gate.GetProperty("blocked_until_retry").EnumerateArray().Select(Value));
        Assert.Contains("setup_integration", gate.GetProperty("blocked_until_retry").EnumerateArray().Select(Value));
        Assert.Contains("doctor_integration", gate.GetProperty("blocked_until_retry").EnumerateArray().Select(Value));
        Assert.Contains("ui_integration", gate.GetProperty("blocked_until_retry").EnumerateArray().Select(Value));
        Assert.Contains("release_validation", gate.GetProperty("blocked_until_retry").EnumerateArray().Select(Value));
        Assert.Contains(
            "separately approved discovery retry",
            String(gate, "promotion_condition"),
            StringComparison.Ordinal);

    }

    [Fact]
    public void BlockedProfilesDeclareClassificationSeverityAndExactRetryConditions()
    {
        using var inventory = ReadJson("discovery-inventory.json");
        var profiles = inventory.RootElement.GetProperty("blocked_profiles").EnumerateArray()
            .ToDictionary(profile => String(profile, "profile_id"), StringComparer.Ordinal);

        Assert.Equal(
            [
                "desktop_bundled_producer_execution",
                "desktop_ownership_and_session",
                "native_thread_to_otel_binding",
                "native_turn_to_otel_binding",
                "concurrent_windows_or_threads",
                "restart_resume_continuity",
                "complete_signal_inventory",
                "semantic_field_inventory",
                "existing_generated_log_exporter_profiles",
                "content_enabled_delta"
            ],
            profiles.Keys);
        Assert.Equal("blocked_external", String(profiles["desktop_bundled_producer_execution"], "classification"));
        Assert.Equal("blocked_external", String(profiles["complete_signal_inventory"], "classification"));
        Assert.Equal("blocked_external", String(profiles["existing_generated_log_exporter_profiles"], "classification"));
        Assert.Equal("blocked_external", String(profiles["content_enabled_delta"], "classification"));
        Assert.All(
            profiles.Where(profile => profile.Key is not "desktop_bundled_producer_execution"
                and not "complete_signal_inventory"
                and not "existing_generated_log_exporter_profiles"
                and not "content_enabled_delta"),
            profile => Assert.Equal("unverified", String(profile.Value, "classification")));
        Assert.Equal("high", String(profiles["desktop_ownership_and_session"], "severity"));
        Assert.Equal("high", String(profiles["native_thread_to_otel_binding"], "severity"));
        Assert.Equal("high", String(profiles["native_turn_to_otel_binding"], "severity"));
        Assert.Contains(
            "log export disabled",
            String(profiles["semantic_field_inventory"], "retry_condition"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "before " + "Issue #93",
            String(profiles["existing_generated_log_exporter_profiles"], "retry_condition"),
            StringComparison.Ordinal);

        Assert.All(profiles.Values, profile =>
        {
            Assert.Equal(
                [
                    "profile_id", "classification", "severity", "blocker",
                    "retry_condition", "unverified_capability"
                ],
                profile.EnumerateObject().Select(property => property.Name));
            Assert.Contains(String(profile, "classification"), new[] { "blocked_external", "unverified" });
            Assert.Contains(String(profile, "severity"), new[] { "high", "medium" });
            Assert.False(string.IsNullOrWhiteSpace(String(profile, "blocker")));
            Assert.False(string.IsNullOrWhiteSpace(String(profile, "retry_condition")));
            Assert.False(string.IsNullOrWhiteSpace(String(profile, "unverified_capability")));
        });
    }

    [Fact]
    public void CanonicalDocumentsPinDecisionSecurityAndNoIntegrationBoundaries()
    {
        var requirements = ReadRepositoryText("docs", "requirements.md");
        var specification = ReadRepositoryText("docs", "spec.md");
        var decisions = ReadRepositoryText("docs", "decisions.md");
        var telemetry = ReadRepositoryText("docs", "specifications", "layers", "telemetry-ingestion.md");
        var security = ReadRepositoryText("docs", "specifications", "security-data-boundaries.md");

        Assert.Contains("Issue #92", requirements, StringComparison.Ordinal);
        Assert.Contains("NO-GO", requirements, StringComparison.Ordinal);
        Assert.Contains("NO-GO", specification, StringComparison.Ordinal);
        Assert.Contains("D072", decisions, StringComparison.Ordinal);
        Assert.Contains("standalone app-server", telemetry, StringComparison.Ordinal);
        Assert.Contains("does not prove", telemetry, StringComparison.Ordinal);
        Assert.Contains("Codex App Desktop", telemetry, StringComparison.Ordinal);
        Assert.Contains("Planned / blocked candidate", telemetry, StringComparison.Ordinal);
        Assert.Contains("現行の対応済み source ではない", telemetry, StringComparison.Ordinal);
        Assert.Contains("content-enabled capture", security, StringComparison.Ordinal);
        Assert.Contains("Setup, Doctor, or UI", security, StringComparison.Ordinal);
        Assert.Contains("現行の対応済み任意機能ではない", requirements, StringComparison.Ordinal);

        var architecture = ReadRepositoryText("docs", "architecture.md");
        Assert.Contains("active collection flow には含まれない", architecture, StringComparison.Ordinal);

        var rootReadme = ReadRepositoryText("README.md");
        Assert.Contains("Codex App Desktop", rootReadme, StringComparison.Ordinal);
        Assert.Contains("NO-GO", rootReadme, StringComparison.Ordinal);
        Assert.Contains("legacy sample", rootReadme, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DesktopProcessDiagnosticScriptEmitsOnlyFixedSanitizedFacts()
    {
        var scriptPath = Path.Combine(
            RepositoryRoot,
            "scripts", "validation", "codex-app-discovery", "observe-desktop-process-tree.ps1");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("ConvertTo-Json -Compress", script, StringComparison.Ordinal);
        Assert.Contains("pid_values_emitted = $false", script, StringComparison.Ordinal);
        Assert.Contains("path_values_emitted = $false", script, StringComparison.Ordinal);
        Assert.Contains("hash_values_emitted = $false", script, StringComparison.Ordinal);
        Assert.Contains("command_line_read = $false", script, StringComparison.Ordinal);
        Assert.Contains("private_state_read = $false", script, StringComparison.Ordinal);
        Assert.Contains("$result.observation = \"unavailable\"", script, StringComparison.Ordinal);
        Assert.Contains(
            "[IO.Path]::GetFullPath($_.ExecutablePath).StartsWith(",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$parentPath.StartsWith($pathPrefix",
            script,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            Regex.Matches(
                script,
                Regex.Escape("-Property ProcessId, ParentProcessId, ExecutablePath")).Count);
        Assert.DoesNotContain(".CommandLine", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Host", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Write-Output", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Get-FileHash", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("throw", script, StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var output = (await standardOutput).Trim();
        var error = (await standardError).Trim();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", error);
        using var result = JsonDocument.Parse(output);
        var root = result.RootElement;
        Assert.Equal(
            [
                "schema_version", "classification", "observation",
                "package_root_codex_process_observed",
                "package_root_parent_process_relationship_observed",
                "app_server_identity_observed", "desktop_otel_execution_observed",
                "diagnostic_authority", "merge_authority", "pid_values_emitted",
                "path_values_emitted", "hash_values_emitted", "command_line_read",
                "private_state_read"
            ],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal("issue-92-desktop-process-diagnostic.v1", String(root, "schema_version"));
        Assert.Equal(
            "non_authoritative_package_process_tree_observation",
            String(root, "classification"));
        Assert.Contains(String(root, "observation"), new[] { "observed", "not_observed", "unavailable" });
        Assert.False(root.GetProperty("app_server_identity_observed").GetBoolean());
        Assert.False(root.GetProperty("desktop_otel_execution_observed").GetBoolean());
        Assert.False(root.GetProperty("diagnostic_authority").GetBoolean());
        Assert.False(root.GetProperty("merge_authority").GetBoolean());
        Assert.False(root.GetProperty("pid_values_emitted").GetBoolean());
        Assert.False(root.GetProperty("path_values_emitted").GetBoolean());
        Assert.False(root.GetProperty("hash_values_emitted").GetBoolean());
        Assert.False(root.GetProperty("command_line_read").GetBoolean());
        Assert.False(root.GetProperty("private_state_read").GetBoolean());
        Assert.DoesNotContain(@":\", output, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/", output, StringComparison.Ordinal);
    }

    private static void AssertFixtureEnvelope(
        JsonElement fixture,
        string expectedFixtureId,
        int expectedSpanCount,
        bool expectedThreadProfileExecuted)
    {
        Assert.Equal(
            [
                "schema_version", "fixture_id", "source_surface", "observed_producer",
                "producer_version", "producer_version_source", "capture_profile", "driver", "routing", "requests",
                "signal_observation", "protocol_binding", "content_safety"
            ],
            fixture.EnumerateObject().Select(property => property.Name));
        Assert.Equal("codex-app-otel-sanitized-fixture.v1", String(fixture, "schema_version"));
        Assert.Equal(expectedFixtureId, String(fixture, "fixture_id"));
        Assert.Equal("codex-app", String(fixture, "source_surface"));
        Assert.Equal("standalone_app_server", String(fixture, "observed_producer"));
        Assert.Equal("0.145.0", String(fixture, "producer_version"));
        Assert.Equal("cli_version_command", String(fixture, "producer_version_source"));
        Assert.Equal("content_disabled", String(fixture, "capture_profile"));
        Assert.Equal("public_app_server_protocol", String(fixture, "driver"));

        var routing = fixture.GetProperty("routing");
        Assert.Equal("per_command_overrides_only", String(routing, "configuration"));
        Assert.Equal("disposable_loopback", String(routing, "receiver"));
        Assert.True(routing.GetProperty("global_configuration_loaded_by_process").GetBoolean());
        Assert.False(routing.GetProperty("global_configuration_values_inspected").GetBoolean());
        Assert.False(routing.GetProperty("global_configuration_written").GetBoolean());
        Assert.False(routing.GetProperty("unoverridden_global_influence_excluded").GetBoolean());

        var requests = fixture.GetProperty("requests").EnumerateArray().ToDictionary(
            request => String(request, "path"), StringComparer.Ordinal);
        Assert.Equal(
            ["path", "capture_profile_executed", "observation", "content_type", "count"],
            requests["/v1/traces"].EnumerateObject().Select(property => property.Name));
        Assert.True(requests["/v1/traces"].GetProperty("capture_profile_executed").GetBoolean());
        Assert.Equal("observed", String(requests["/v1/traces"], "observation"));
        Assert.Equal(1, requests["/v1/traces"].GetProperty("count").GetInt32());
        Assert.Equal("application/json", String(requests["/v1/traces"], "content_type"));
        Assert.Equal(
            ["path", "capture_profile_executed", "observation", "content_type", "count"],
            requests["/v1/logs"].EnumerateObject().Select(property => property.Name));
        Assert.False(requests["/v1/logs"].GetProperty("capture_profile_executed").GetBoolean());
        Assert.Equal("not_observed", String(requests["/v1/logs"], "observation"));
        Assert.Equal(JsonValueKind.Null, requests["/v1/logs"].GetProperty("content_type").ValueKind);
        Assert.Equal(JsonValueKind.Null, requests["/v1/logs"].GetProperty("count").ValueKind);

        var binding = fixture.GetProperty("protocol_binding");
        Assert.Equal(
            expectedThreadProfileExecuted,
            binding.GetProperty("thread_profile_executed").GetBoolean());
        Assert.False(binding.GetProperty("turn_profile_executed").GetBoolean());
        if (expectedThreadProfileExecuted)
        {
            Assert.True(binding.GetProperty("native_thread_id_returned").GetBoolean());
            Assert.False(binding.GetProperty("otel_native_thread_id_observed").GetBoolean());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, binding.GetProperty("native_thread_id_returned").ValueKind);
            Assert.Equal(JsonValueKind.Null, binding.GetProperty("otel_native_thread_id_observed").ValueKind);
        }
        Assert.Equal(JsonValueKind.Null, binding.GetProperty("native_turn_id_returned").ValueKind);
        Assert.Equal(JsonValueKind.Null, binding.GetProperty("otel_native_turn_id_observed").ValueKind);
        Assert.Equal(expectedSpanCount,
            fixture.GetProperty("signal_observation").GetProperty("span_count").GetInt32());

        var safety = fixture.GetProperty("content_safety");
        Assert.False(safety.GetProperty("raw_payload_retained").GetBoolean());
        Assert.False(safety.GetProperty("content_values_retained").GetBoolean());
        Assert.False(safety.GetProperty("identifier_values_retained").GetBoolean());
        Assert.False(safety.GetProperty("resource_attribute_values_retained").GetBoolean());
        Assert.False(safety.GetProperty("machine_paths_retained").GetBoolean());

        var text = fixture.GetRawText();
        Assert.DoesNotMatch(new Regex(@"(?i)[a-f0-9]{32}|[a-f0-9]{16}"), text);
        Assert.DoesNotMatch(
            new Regex(@"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\b"),
            text);
        Assert.DoesNotMatch(new Regex(@"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b"), text);
        Assert.DoesNotMatch(
            new Regex(@"(?i)(authorization|api[_-]?key|access[_-]?token)\s*[:=]"),
            text);
        Assert.DoesNotContain(@":\\", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/", text, StringComparison.Ordinal);
        Assert.DoesNotContain("native_thread_id_value", text, StringComparison.Ordinal);
        Assert.DoesNotContain("native_turn_id_value", text, StringComparison.Ordinal);
    }

    private static JsonDocument ReadJson(string relativePath)
    {
        var path = Path.Combine(ContractRoot, relativePath);
        Assert.True(File.Exists(path), $"Missing Issue #92 contract artifact: {path}");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string ReadRepositoryText(params string[] pathParts)
    {
        var path = Path.Combine([RepositoryRoot, .. pathParts]);
        Assert.True(File.Exists(path), $"Missing repository document: {path}");
        return File.ReadAllText(path);
    }

    private static string String(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString()!;

    private static string Value(JsonElement element) => element.GetString()!;
}
