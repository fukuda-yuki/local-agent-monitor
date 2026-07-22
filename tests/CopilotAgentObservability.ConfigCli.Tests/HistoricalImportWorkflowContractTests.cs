using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class HistoricalImportWorkflowContractTests
{
    private static readonly string ContractRoot = System.IO.Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "docs", "specifications", "contracts", "historical-import-workflow", "v1");

    private static readonly string[] ExpectedSchemas =
    [
        "confirmation-request.schema.json",
        "confirmation.schema.json",
        "error.schema.json",
        "import-history.schema.json",
        "import-request.schema.json",
        "import-result.schema.json",
        "import-status.schema.json",
        "observation-detail.schema.json",
        "observation-list.schema.json",
        "preview.schema.json",
        "source-selection.schema.json"
    ];

    private static readonly string[] ExpectedFixtures =
    [
        "confirmation-eligible.synthetic.json",
        "confirmation-request.synthetic.json",
        "error-source-changed.synthetic.json",
        "import-history.synthetic.json",
        "import-request.synthetic.json",
        "import-result-replayed.synthetic.json",
        "import-result.synthetic.json",
        "import-status-succeeded.synthetic.json",
        "observation-detail.synthetic.json",
        "observation-list.synthetic.json",
        "preview-eligible.synthetic.json",
        "preview-zero-candidate.json",
        "source-selection.json"
    ];

    [Fact]
    public void Repository_workflow_contract_artifacts_validate_against_strict_v1_schemas()
    {
        Assert.True(Directory.Exists(ContractRoot), $"Missing contract directory: {ContractRoot}");
        Assert.Equal(ExpectedSchemas, Directory.GetFiles(ContractRoot, "*.schema.json").Select(System.IO.Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(ExpectedFixtures, Directory.GetFiles(Path("fixtures"), "*.json").Select(System.IO.Path.GetFileName).Order(StringComparer.Ordinal));

        Validate("source-selection.schema.json", "source-selection.json");
        Validate("preview.schema.json", "preview-zero-candidate.json", "preview-eligible.synthetic.json");
        Validate("confirmation-request.schema.json", "confirmation-request.synthetic.json");
        Validate("confirmation.schema.json", "confirmation-eligible.synthetic.json");
        Validate("error.schema.json", "error-source-changed.synthetic.json");
        Validate("import-request.schema.json", "import-request.synthetic.json");
        Validate("import-status.schema.json", "import-status-succeeded.synthetic.json");
        Validate("import-result.schema.json", "import-result.synthetic.json", "import-result-replayed.synthetic.json");
        Validate("import-history.schema.json", "import-history.synthetic.json");
        Validate("observation-list.schema.json", "observation-list.synthetic.json");
        Validate("observation-detail.schema.json", "observation-detail.synthetic.json");
    }

    [Fact]
    public void Preview_represents_current_unavailable_and_future_available_counts_without_enabling_zero_candidate_commit()
    {
        using var unavailable = ReadFixture("preview-zero-candidate.json");
        var unavailableRoot = unavailable.RootElement;
        Assert.Equal("unsupported", Value(unavailableRoot, "adapter_state"));
        AssertUnavailableCount(unavailableRoot, "total");
        AssertAvailableCount(unavailableRoot, "eligible", 0);
        AssertUnavailableCount(unavailableRoot, "unsupported");
        AssertUnavailableCount(unavailableRoot, "malformed");
        AssertUnavailableCount(unavailableRoot, "new_sessions");
        AssertUnavailableCount(unavailableRoot, "new_events");
        Assert.Equal("none", Value(unavailableRoot, "completeness_ceiling"));
        Assert.Empty(unavailableRoot.GetProperty("completeness_reasons").EnumerateArray());
        Assert.False(unavailableRoot.GetProperty("commit_allowed").GetBoolean());
        Assert.False(unavailableRoot.TryGetProperty("confirmation_id", out _));
        Assert.False(unavailableRoot.TryGetProperty("confirmation_token", out _));
        Assert.Equal("historical_import_no_eligible_candidates", Value(unavailableRoot, "rejection_code"));
        Assert.Equal("not_read", Value(unavailableRoot, "content_risk"));

        using var available = ReadFixture("preview-eligible.synthetic.json");
        var availableRoot = available.RootElement;
        Assert.Equal("available", Value(availableRoot, "adapter_state"));
        Assert.Equal("production", Value(availableRoot, "evidence_status"));
        AssertAvailableCount(availableRoot, "eligible", 3);
        AssertAvailableCount(availableRoot, "new_observations", 1);
        AssertUnavailableCount(availableRoot, "new_sessions");
        AssertUnavailableCount(availableRoot, "new_events");
        Assert.True(availableRoot.GetProperty("commit_allowed").GetBoolean());
        Assert.Null(availableRoot.GetProperty("rejection_code").GetString());
    }

    [Fact]
    public void Preview_is_digest_and_snapshot_bound_and_discloses_only_sanitized_risk_capability_merge_retention_and_exclusion_metadata()
    {
        using var preview = ReadFixture("preview-eligible.synthetic.json");
        var root = preview.RootElement;

        Assert.Matches("^sha256:[0-9a-f]{64}$", Value(root, "preview_digest"));
        Assert.Matches("^hsv_[1-9][0-9]*$", Value(root, "snapshot_version"));
        Assert.Equal("partial", Value(root, "completeness_ceiling"));
        Assert.Equal(["historical_summary_only"], Strings(root.GetProperty("completeness_reasons")));
        Assert.Equal(
            ["content", "event_identity", "lifecycle", "session_identity", "timing", "trace_identity"],
            Strings(root.GetProperty("missing_capabilities")));
        Assert.Empty(root.GetProperty("merge_candidates").EnumerateArray());
        Assert.Equal("not_applicable", Value(root.GetProperty("retention_impact"), "disposition"));
        Assert.Empty(root.GetProperty("exclusions").EnumerateArray());

        AssertRepositorySafe(preview);
    }

    [Fact]
    public void Confirmation_is_issued_only_for_an_eligible_digest_bound_preview()
    {
        using var request = ReadFixture("confirmation-request.synthetic.json");
        Assert.Equal("confirm", Value(request.RootElement, "decision"));
        Assert.False(request.RootElement.TryGetProperty("confirmation_id", out _));

        using var confirmation = ReadFixture("confirmation-eligible.synthetic.json");
        var root = confirmation.RootElement;
        Assert.Equal("eligible", Value(root, "eligibility"));
        Assert.Equal("confirm", Value(root, "decision"));

        using var schema = Read("confirmation.schema.json");
        var ineligible = JsonNode.Parse(root.GetRawText())!.AsObject();
        ineligible["eligibility"] = "ineligible";
        using var invalid = JsonDocument.Parse(ineligible.ToJsonString());
        Assert.Contains("$.eligibility must equal \"eligible\".", HistoricalContractSchemaValidator.Validate(schema, invalid));
    }

    [Fact]
    public void Import_request_and_status_pin_idempotency_and_the_explicit_operation_lifecycle()
    {
        using var request = ReadFixture("import-request.synthetic.json");
        Assert.Matches("^hik_[0-9a-f]{32}$", Value(request.RootElement, "idempotency_key"));

        using var status = ReadFixture("import-status-succeeded.synthetic.json");
        var root = status.RootElement;
        Assert.Equal("succeeded", Value(root, "state"));
        Assert.True(root.GetProperty("result_available").GetBoolean());
        Assert.False(root.TryGetProperty("idempotency_key", out _));
        Assert.Equal(
            ["queued", "running", "succeeded"],
            Strings(root.GetProperty("lifecycle")));
        Assert.Equal(3, root.GetProperty("operation_version").GetInt32());
        Assert.Null(root.GetProperty("failure_code").GetString());
    }

    [Fact]
    public void Import_status_schema_pins_unsuccessful_outcomes_failure_codes_and_zero_progress()
    {
        using var schema = Read("import-status.schema.json");
        using var fixture = ReadFixture("import-status-succeeded.synthetic.json");

        var failed = JsonNode.Parse(fixture.RootElement.GetRawText())!.AsObject();
        SetUnsuccessfulStatus(
            failed,
            state: "failed",
            outcome: "rolled_back",
            failureCode: "historical_import_transaction_failed");
        using (var validFailed = JsonDocument.Parse(failed.ToJsonString()))
        {
            Assert.Empty(HistoricalContractSchemaValidator.Validate(schema, validFailed));
        }

        failed["failure_code"] = "historical_import_store_unavailable";
        using (var invalidFailed = JsonDocument.Parse(failed.ToJsonString()))
        {
            Assert.Contains(
                "$ does not match exactly one schema branch.",
                HistoricalContractSchemaValidator.Validate(schema, invalidFailed));
        }

        var rejected = JsonNode.Parse(fixture.RootElement.GetRawText())!.AsObject();
        SetUnsuccessfulStatus(
            rejected,
            state: "rejected",
            outcome: "not_started",
            failureCode: "historical_import_source_changed");
        using (var validRejected = JsonDocument.Parse(rejected.ToJsonString()))
        {
            Assert.Empty(HistoricalContractSchemaValidator.Validate(schema, validRejected));
        }

        rejected["failure_code"] = "historical_import_transaction_failed";
        rejected["counts"]!["processed"] = 1;
        using var invalidRejected = JsonDocument.Parse(rejected.ToJsonString());
        Assert.Contains(
            "$ does not match exactly one schema branch.",
            HistoricalContractSchemaValidator.Validate(schema, invalidRejected));
    }

    [Fact]
    public void Result_keeps_new_historical_observations_distinct_and_unbound_without_synthesized_authority()
    {
        using var result = ReadFixture("import-result.synthetic.json");
        var root = result.RootElement;
        var observation = Assert.Single(root.GetProperty("observations").EnumerateArray());

        Assert.Equal("distinct_unbound", Value(observation, "identity_resolution"));
        Assert.Equal("none", Value(observation, "binding_basis"));
        Assert.Equal("partial", Value(observation, "completeness"));
        Assert.Equal(["historical_summary_only"], Strings(observation.GetProperty("completeness_reasons")));
        Assert.Equal("not_captured", Value(observation, "content_state"));

        var forbiddenAuthority = new HashSet<string>(StringComparer.Ordinal)
        {
            "candidate_key", "source_record_key",
            "session_id", "event_id", "trace_id", "span_id", "parent_span_id", "parent_event_id",
            "occurred_at", "started_at", "ended_at", "source_timestamp"
        };
        Assert.DoesNotContain(EnumeratePropertyNames(observation), forbiddenAuthority.Contains);
    }

    [Fact]
    public void Result_pins_exact_noop_dedup_sanitized_conflict_preservation_and_metadata_only_retention()
    {
        using var result = ReadFixture("import-result.synthetic.json");
        var root = result.RootElement;

        AssertAvailableCount(root, "new_observations", 1);
        AssertAvailableCount(root, "duplicates", 1);
        AssertAvailableCount(root, "conflicts", 1);
        AssertUnavailableCount(root, "new_sessions");
        AssertUnavailableCount(root, "new_events");

        var duplicate = Assert.Single(root.GetProperty("duplicates").EnumerateArray());
        Assert.Equal("exact_duplicate_noop", Value(duplicate, "decision"));
        var conflict = Assert.Single(root.GetProperty("conflicts").EnumerateArray());
        Assert.Equal("preserve_existing", Value(conflict, "decision"));
        Assert.Equal("source_record_conflict", Value(conflict, "code"));
        Assert.Matches("^sha256:[0-9a-f]{64}$", Value(conflict, "existing_fingerprint"));
        Assert.Matches("^sha256:[0-9a-f]{64}$", Value(conflict, "incoming_fingerprint"));
        Assert.False(conflict.TryGetProperty("existing_value", out _));
        Assert.False(conflict.TryGetProperty("incoming_value", out _));

        var retention = root.GetProperty("retention");
        Assert.Equal("not_applicable", Value(retention, "disposition"));
        Assert.Equal("not_applicable", Value(retention, "pin_state"));
        Assert.Equal("not_applicable", Value(retention, "deletion_state"));
        Assert.Equal(0, retention.GetProperty("created_item_count").GetInt32());

        using var replay = ReadFixture("import-result-replayed.synthetic.json");
        Assert.Equal("replayed", Value(replay.RootElement, "idempotency_outcome"));
        Assert.Equal(Value(root, "operation_id"), Value(replay.RootElement, "operation_id"));
        Assert.Equal(root.GetProperty("counts").GetRawText(), replay.RootElement.GetProperty("counts").GetRawText());
    }

    [Fact]
    public void History_is_summary_only_and_all_fixtures_exclude_paths_and_raw_content()
    {
        using var history = ReadFixture("import-history.synthetic.json");
        var item = Assert.Single(history.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("historical", Value(item, "source_kind"));
        Assert.Equal("partial", Value(item, "completeness"));
        Assert.Equal("not_captured", Value(item, "content_state"));
        AssertRepositorySafe(history);

        foreach (var fixture in ExpectedFixtures.Where(name => name != "source-selection.json"))
        {
            using var document = ReadFixture(fixture);
            AssertRepositorySafe(document);
        }

        using var privateRequest = ReadFixture("source-selection.json");
        Assert.Equal("X:\\synthetic-historical-import\\selected-root", Value(privateRequest.RootElement, "exact_reference"));
        Assert.True(privateRequest.RootElement.GetProperty("consent_granted").GetBoolean());
        Assert.DoesNotContain("Users", privateRequest.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", privateRequest.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw_content", EnumeratePropertyNames(privateRequest.RootElement));
        foreach (var fixture in ExpectedFixtures.Where(name => name != "source-selection.json"))
        {
            using var publicEnvelope = ReadFixture(fixture);
            var names = EnumeratePropertyNames(publicEnvelope.RootElement).ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain("exact_reference", names);
            Assert.DoesNotContain("session_id", names);
        }
    }

    [Fact]
    public void Observation_reads_expose_historical_filter_and_completeness_without_trace_authority()
    {
        using var list = ReadFixture("observation-list.synthetic.json");
        Assert.Equal("historical", Value(list.RootElement, "source_filter"));
        var item = Assert.Single(list.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("historical", Value(item, "source_kind"));
        Assert.Equal(
            ["content", "event_identity", "lifecycle", "session_identity", "timing", "trace_identity"],
            Strings(item.GetProperty("missing_capabilities")));
        Assert.False(item.GetProperty("trace_controls_enabled").GetBoolean());

        using var detail = ReadFixture("observation-detail.synthetic.json");
        var root = detail.RootElement;
        Assert.Equal("distinct_unbound", Value(root, "identity_resolution"));
        Assert.Equal("none", Value(root, "binding_basis"));
        Assert.Equal("partial", Value(root, "completeness"));
        Assert.Equal(["historical_summary_only"], Strings(root.GetProperty("completeness_reasons")));
        Assert.False(root.GetProperty("trace_controls_enabled").GetBoolean());
        AssertRepositorySafe(detail);
    }

    [Fact]
    public void Workflow_schemas_reject_cross_arm_authority_available_synthetic_counts_and_out_of_bound_state()
    {
        using var selectionSchema = Read("source-selection.schema.json");
        using var selectionFixture = ReadFixture("source-selection.json");
        var mixedSelection = JsonNode.Parse(selectionFixture.RootElement.GetRawText())!.AsObject();
        mixedSelection["source_surface"] = "claude-code";
        using var invalidSelection = JsonDocument.Parse(mixedSelection.ToJsonString());
        Assert.Contains("$ does not match exactly one schema branch.", HistoricalContractSchemaValidator.Validate(selectionSchema, invalidSelection));

        using var previewSchema = Read("preview.schema.json");
        using var previewFixture = ReadFixture("preview-eligible.synthetic.json");
        var invalidPreview = JsonNode.Parse(previewFixture.RootElement.GetRawText())!.AsObject();
        invalidPreview["expires_after_seconds"] = 301;
        invalidPreview["source_format"]!["version"] = "1";
        invalidPreview["source_time_range"] = JsonNode.Parse("""{ "availability": "available", "start": "2026-01-01T00:00:00Z", "end": "2026-01-01T00:00:01Z" }""");
        invalidPreview["counts"]!["eligible"]!["value"] = 0;
        invalidPreview["counts"]!["new_sessions"] = JsonNode.Parse("""{ "availability": "available", "value": 1 }""");
        using var invalidPreviewDocument = JsonDocument.Parse(invalidPreview.ToJsonString());
        var previewErrors = HistoricalContractSchemaValidator.Validate(previewSchema, invalidPreviewDocument);
        Assert.Contains("$.expires_after_seconds is above the maximum.", previewErrors);
        Assert.Contains("$.source_format.version does not match the required pattern.", previewErrors);
        Assert.Contains("$.source_time_range.availability must equal \"unavailable\".", previewErrors);
        Assert.Contains("$.counts.eligible.value is below the minimum.", previewErrors);
        Assert.Contains("$.counts.new_sessions.availability must equal \"unavailable\".", previewErrors);

        using var resultSchema = Read("import-result.schema.json");
        using var resultFixture = ReadFixture("import-result.synthetic.json");
        var invalidResult = JsonNode.Parse(resultFixture.RootElement.GetRawText())!.AsObject();
        invalidResult["counts"]!["total"]!["value"] = 1001;
        invalidResult["counts"]!["new_events"] = JsonNode.Parse("""{ "availability": "available", "value": 1 }""");
        using var invalidResultDocument = JsonDocument.Parse(invalidResult.ToJsonString());
        var resultErrors = HistoricalContractSchemaValidator.Validate(resultSchema, invalidResultDocument);
        Assert.Contains("$.counts.total.value is above the maximum.", resultErrors);
        Assert.Contains("$.counts.new_events.availability must equal \"unavailable\".", resultErrors);

        using var statusSchema = Read("import-status.schema.json");
        using var statusFixture = ReadFixture("import-status-succeeded.synthetic.json");
        var invalidStatus = JsonNode.Parse(statusFixture.RootElement.GetRawText())!.AsObject();
        invalidStatus["lifecycle"] = JsonNode.Parse("""["queued", "succeeded"]""");
        invalidStatus["counts"]!["processed"] = 1001;
        using var invalidStatusDocument = JsonDocument.Parse(invalidStatus.ToJsonString());
        var statusErrors = HistoricalContractSchemaValidator.Validate(statusSchema, invalidStatusDocument);
        Assert.Contains("$ does not match exactly one schema branch.", statusErrors);
        Assert.Contains("$.counts.processed is above the maximum.", statusErrors);
    }

    [Fact]
    public void Observation_list_schema_requires_per_row_missing_capabilities()
    {
        using var schema = Read("observation-list.schema.json");
        using var fixture = ReadFixture("observation-list.synthetic.json");
        var list = JsonNode.Parse(fixture.RootElement.GetRawText())!.AsObject();
        list["items"]![0]!.AsObject().Remove("missing_capabilities");
        using var invalid = JsonDocument.Parse(list.ToJsonString());

        Assert.Contains("$.items[0].missing_capabilities is required.", HistoricalContractSchemaValidator.Validate(schema, invalid));
    }

    [Theory]
    [InlineData("succeeded", "committed", "partial")]
    [InlineData("failed", "rolled_back", "none")]
    [InlineData("rejected", "not_started", "none")]
    public void Import_history_schema_pins_each_terminal_projection(
        string state,
        string outcome,
        string completeness)
    {
        using var schema = Read("import-history.schema.json");
        using var fixture = ReadFixture("import-history.synthetic.json");
        var history = JsonNode.Parse(fixture.RootElement.GetRawText())!.AsObject();
        var item = history["items"]![0]!.AsObject();
        item["state"] = state;
        item["outcome"] = outcome;
        item["new_observation_count"] = state == "succeeded" ? 1 : 0;
        item["duplicate_count"] = state == "succeeded" ? 1 : 0;
        item["conflict_count"] = state == "succeeded" ? 1 : 0;
        item["completeness"] = completeness;
        item["completeness_reasons"] = completeness == "partial"
            ? JsonNode.Parse("""["historical_summary_only"]""")
            : new JsonArray();

        using (var valid = JsonDocument.Parse(history.ToJsonString()))
            Assert.Empty(HistoricalContractSchemaValidator.Validate(schema, valid));

        item["outcome"] = outcome == "committed" ? "rolled_back" : "committed";
        using (var invalidOutcome = JsonDocument.Parse(history.ToJsonString()))
            Assert.Contains("$.items[0] does not match exactly one schema branch.",
                HistoricalContractSchemaValidator.Validate(schema, invalidOutcome));

        item["outcome"] = outcome;
        item["completeness"] = completeness == "partial" ? "none" : "partial";
        item["completeness_reasons"] = completeness == "partial"
            ? new JsonArray()
            : JsonNode.Parse("""["historical_summary_only"]""");
        using (var invalidCompleteness = JsonDocument.Parse(history.ToJsonString()))
            Assert.Contains("$.items[0] does not match exactly one schema branch.",
                HistoricalContractSchemaValidator.Validate(schema, invalidCompleteness));

        item["completeness"] = completeness;
        item["completeness_reasons"] = completeness == "partial"
            ? JsonNode.Parse("""["historical_summary_only"]""")
            : new JsonArray();
        item["source_badge"] = "unsupported";
        using var invalidBadge = JsonDocument.Parse(history.ToJsonString());
        Assert.Contains("$.items[0].source_badge must equal \"historical\".",
            HistoricalContractSchemaValidator.Validate(schema, invalidBadge));
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
    public void Workflow_preview_semver_grammar_rejects_noncanonical_edges(string version)
    {
        using var schema = Read("preview.schema.json");
        using var fixture = ReadFixture("preview-eligible.synthetic.json");
        var preview = JsonNode.Parse(fixture.RootElement.GetRawText())!.AsObject();
        preview["source_application_version"] = version;
        preview["source_format"]!["version"] = version;
        using var invalid = JsonDocument.Parse(preview.ToJsonString());

        var errors = HistoricalContractSchemaValidator.Validate(schema, invalid);
        Assert.Contains("$.source_application_version does not match the required pattern.", errors);
        Assert.Contains("$.source_format.version does not match the required pattern.", errors);
    }

    [Theory]
    [InlineData("0.0.0")]
    [InlineData("1.2.3-alpha")]
    [InlineData("1.2.3-alpha.1+build.001")]
    [InlineData("1.2.3-0")]
    public void Workflow_preview_semver_grammar_accepts_canonical_edges(string version)
    {
        using var schema = Read("preview.schema.json");
        using var fixture = ReadFixture("preview-eligible.synthetic.json");
        var preview = JsonNode.Parse(fixture.RootElement.GetRawText())!.AsObject();
        preview["source_application_version"] = version;
        preview["source_format"]!["version"] = version;
        using var valid = JsonDocument.Parse(preview.ToJsonString());

        Assert.Empty(HistoricalContractSchemaValidator.Validate(schema, valid));
    }

    [Fact]
    public void Source_selection_application_version_remains_an_exact_metadata_token()
    {
        using var schema = Read("source-selection.schema.json");
        using var fixture = ReadFixture("source-selection.json");
        var selection = JsonNode.Parse(fixture.RootElement.GetRawText())!.AsObject();
        selection["source_application_version"] = "01.2.3-detector_metadata";
        using var valid = JsonDocument.Parse(selection.ToJsonString());

        Assert.Empty(HistoricalContractSchemaValidator.Validate(schema, valid));
    }

    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\source")]
    [InlineData(@"\\.\C:\source")]
    [InlineData("//server/share")]
    [InlineData("C:/source")]
    [InlineData(@"C:\source/child")]
    [InlineData(@"/native/path\foreign-separator")]
    public void Source_selection_schema_rejects_non_native_windows_reference_forms(string exactReference)
    {
        using var schema = Read("source-selection.schema.json");
        using var fixture = ReadFixture("source-selection.json");
        var selection = JsonNode.Parse(fixture.RootElement.GetRawText())!.AsObject();
        selection["exact_reference"] = exactReference;
        using var invalid = JsonDocument.Parse(selection.ToJsonString());

        Assert.Contains("$.exact_reference does not match the required pattern.",
            HistoricalContractSchemaValidator.Validate(schema, invalid));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    public void Workflow_grammar_families_reject_terminal_line_breaks(string lineBreak)
    {
        using var selectionSchema = Read("source-selection.schema.json");
        using var selectionFixture = ReadFixture("source-selection.json");

        var pathSelection = JsonNode.Parse(selectionFixture.RootElement.GetRawText())!.AsObject();
        pathSelection["exact_reference"] = pathSelection["exact_reference"]!.GetValue<string>() + lineBreak;
        using var invalidPath = JsonDocument.Parse(pathSelection.ToJsonString());
        Assert.Contains("$.exact_reference does not match the required pattern.", HistoricalContractSchemaValidator.Validate(selectionSchema, invalidPath));

        var sessionSelection = JsonNode.Parse(selectionFixture.RootElement.GetRawText())!.AsObject();
        sessionSelection["session_id"] = sessionSelection["session_id"]!.GetValue<string>() + lineBreak;
        using var invalidSession = JsonDocument.Parse(sessionSelection.ToJsonString());
        Assert.Contains("$.session_id does not match the required pattern.", HistoricalContractSchemaValidator.Validate(selectionSchema, invalidSession));

        var versionSelection = JsonNode.Parse(selectionFixture.RootElement.GetRawText())!.AsObject();
        versionSelection["source_application_version"] = versionSelection["source_application_version"]!.GetValue<string>() + lineBreak;
        using var invalidMetadataVersion = JsonDocument.Parse(versionSelection.ToJsonString());
        Assert.Contains("$.source_application_version does not match the required pattern.", HistoricalContractSchemaValidator.Validate(selectionSchema, invalidMetadataVersion));

        using var previewSchema = Read("preview.schema.json");
        using var previewFixture = ReadFixture("preview-eligible.synthetic.json");

        var tokenPreview = JsonNode.Parse(previewFixture.RootElement.GetRawText())!.AsObject();
        tokenPreview["profile_id"] = tokenPreview["profile_id"]!.GetValue<string>() + lineBreak;
        using var invalidToken = JsonDocument.Parse(tokenPreview.ToJsonString());
        Assert.Contains("$.profile_id does not match the required pattern.", HistoricalContractSchemaValidator.Validate(previewSchema, invalidToken));

        var snapshotPreview = JsonNode.Parse(previewFixture.RootElement.GetRawText())!.AsObject();
        snapshotPreview["snapshot_version"] = snapshotPreview["snapshot_version"]!.GetValue<string>() + lineBreak;
        using var invalidSnapshot = JsonDocument.Parse(snapshotPreview.ToJsonString());
        Assert.Contains("$.snapshot_version does not match the required pattern.", HistoricalContractSchemaValidator.Validate(previewSchema, invalidSnapshot));

        var idPreview = JsonNode.Parse(previewFixture.RootElement.GetRawText())!.AsObject();
        idPreview["preview_id"] = idPreview["preview_id"]!.GetValue<string>() + lineBreak;
        using var invalidId = JsonDocument.Parse(idPreview.ToJsonString());
        Assert.Contains("$.preview_id is too long.", HistoricalContractSchemaValidator.Validate(previewSchema, invalidId));

        var digestPreview = JsonNode.Parse(previewFixture.RootElement.GetRawText())!.AsObject();
        digestPreview["preview_digest"] = digestPreview["preview_digest"]!.GetValue<string>() + lineBreak;
        using var invalidDigest = JsonDocument.Parse(digestPreview.ToJsonString());
        Assert.Contains("$.preview_digest is too long.", HistoricalContractSchemaValidator.Validate(previewSchema, invalidDigest));

        var semverPreview = JsonNode.Parse(previewFixture.RootElement.GetRawText())!.AsObject();
        semverPreview["source_format"]!["version"] = semverPreview["source_format"]!["version"]!.GetValue<string>() + lineBreak;
        using var invalidSemver = JsonDocument.Parse(semverPreview.ToJsonString());
        Assert.Contains("$.source_format.version does not match the required pattern.", HistoricalContractSchemaValidator.Validate(previewSchema, invalidSemver));
    }

    [Fact]
    public void Workflow_schemas_reject_unknown_path_or_raw_content_properties()
    {
        using var schema = Read("preview.schema.json");
        using var fixture = ReadFixture("preview-zero-candidate.json");
        var preview = JsonNode.Parse(fixture.RootElement.GetRawText())!.AsObject();
        preview["source_path"] = "C:\\Users\\private\\history.jsonl";
        preview["raw_content"] = "secret";
        using var invalid = JsonDocument.Parse(preview.ToJsonString());

        var errors = HistoricalContractSchemaValidator.Validate(schema, invalid);
        Assert.Contains("$.source_path is not allowed.", errors);
        Assert.Contains("$.raw_content is not allowed.", errors);
    }

    private static void Validate(string schemaFile, params string[] fixtures)
    {
        using var schema = Read(schemaFile);
        foreach (var fixtureFile in fixtures)
        {
            using var fixture = ReadFixture(fixtureFile);
            Assert.Empty(HistoricalContractSchemaValidator.Validate(schema, fixture));
        }
    }

    private static void AssertRepositorySafe(JsonDocument document)
    {
        var forbiddenNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "path", "source_path", "absolute_path", "raw", "raw_content", "prompt", "response", "tool_arguments", "tool_results",
            "candidate_key", "source_record_key"
        };
        Assert.DoesNotContain(EnumeratePropertyNames(document.RootElement), forbiddenNames.Contains);

        foreach (var value in EnumerateStrings(document.RootElement))
        {
            Assert.DoesNotMatch("^[A-Za-z]:[\\\\/]", value);
            Assert.DoesNotMatch("^/(?:Users|home|var|tmp)/", value);
        }
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

    private static IEnumerable<string> EnumerateStrings(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            yield return value.GetString()!;
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            foreach (var nested in EnumerateStrings(property.Value)) yield return nested;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            foreach (var nested in EnumerateStrings(item)) yield return nested;
        }
    }

    private static string[] Strings(JsonElement value) =>
        value.EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static void AssertAvailableCount(JsonElement envelope, string name, int expected)
    {
        var count = envelope.GetProperty("counts").GetProperty(name);
        Assert.Equal("available", Value(count, "availability"));
        Assert.Equal(expected, count.GetProperty("value").GetInt32());
    }

    private static void AssertUnavailableCount(JsonElement envelope, string name)
    {
        var count = envelope.GetProperty("counts").GetProperty(name);
        Assert.Equal("unavailable", Value(count, "availability"));
        Assert.Equal(JsonValueKind.Null, count.GetProperty("value").ValueKind);
    }

    private static void SetUnsuccessfulStatus(
        JsonObject status,
        string state,
        string outcome,
        string failureCode)
    {
        status["state"] = state;
        status["lifecycle"] = JsonNode.Parse($"[\"queued\",\"running\",\"{state}\"]");
        status["transaction_outcome"] = outcome;
        status["result_available"] = false;
        status["failure_code"] = failureCode;
        status["counts"]!["processed"] = 0;
        status["counts"]!["new_observations"] = 0;
        status["counts"]!["duplicates"] = 0;
        status["counts"]!["conflicts"] = 0;
        status["counts"]!["record_rejections"] = 0;
    }

    private static string Value(JsonElement value, string property) =>
        value.GetProperty(property).GetString()!;

    private static JsonDocument Read(string file) =>
        JsonDocument.Parse(File.ReadAllText(Path(file)));

    private static JsonDocument ReadFixture(string file) =>
        JsonDocument.Parse(File.ReadAllText(Path("fixtures", file)));

    private static string Path(params string[] segments) =>
        segments.Aggregate(ContractRoot, System.IO.Path.Combine);
}
