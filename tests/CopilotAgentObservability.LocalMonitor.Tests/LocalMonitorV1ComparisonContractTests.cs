using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1ComparisonContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ContractRoot = Path.Combine(RepositoryRoot, "docs", "specifications", "contracts", "local-monitor-v1");
    private static readonly string FixtureRoot = Path.Combine(AppContext.BaseDirectory, "TestData", "LocalMonitorV1Comparison");

    private static readonly string[] Tokens =
    [
        "local-monitor-comparison-preview.request.v1",
        "local-monitor-comparison-preview.response.v1",
        "local-monitor-comparison-create.request.v1",
        "local-monitor-comparison-create.response.v1",
        "local-monitor-comparison-read.response.v1",
        "local-monitor-comparison-rows.response.v1",
        "local-monitor-comparison-evidence.response.v1",
    ];

    private static readonly (int Ordinal, string Key, string Label)[] Sections =
    [
        (1, "target", "対象"), (2, "tokens", "トークン"),
        (3, "input_token_breakdown", "入力トークンの内訳"), (4, "time_and_execution", "時間・実行量"),
        (5, "skills", "スキル"), (6, "tools", "ツール"), (7, "subagents", "サブエージェント"),
        (8, "errors_and_retries", "エラー・再試行"), (9, "conditions", "比較条件"),
    ];

    [Fact]
    public void OwnerSpecificationFreezesExactlyFiveOperationsAndSevenSchemas()
    {
        var specification = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "specifications", "interfaces", "local-monitor-v1-comparison.md"));
        var normalized = Regex.Replace(specification, @"\s+", " ");
        string[] routes =
        [
            "POST /api/local-monitor/v1/repositories/{repositoryId}/comparisons/preview",
            "POST /api/local-monitor/v1/repositories/{repositoryId}/comparisons",
            "GET  /api/local-monitor/v1/repositories/{repositoryId}/comparisons/{comparisonId}",
            "GET  /api/local-monitor/v1/repositories/{repositoryId}/comparisons/{comparisonId}/rows",
            "GET  /api/local-monitor/v1/repositories/{repositoryId}/comparisons/{comparisonId}/evidence",
        ];

        Assert.All(routes, route => Assert.Contains(route, specification, StringComparison.Ordinal));
        Assert.All(Tokens, token => Assert.Contains(token, specification, StringComparison.Ordinal));
        Assert.Equal(5, Regex.Matches(specification, @"(?m)^(POST |GET  )/api/local-monitor/v1/repositories/\{repositoryId\}/comparisons(?:/preview|/\{comparisonId\}(?:/rows|/evidence)?)?$", RegexOptions.CultureInvariant).Count);
        Assert.Equal(7, Regex.Matches(specification, @"(?m)^local-monitor-comparison-(?:preview|create|read|rows|evidence)\.(?:request|response)\.v1$", RegexOptions.CultureInvariant).Count);
        Assert.Contains("16,384", specification, StringComparison.Ordinal);
        Assert.Contains("8,388,608", specification, StringComparison.Ordinal);
        Assert.Contains("comparison_preview_stale", specification, StringComparison.Ordinal);
        Assert.Contains("HEAD", specification, StringComparison.Ordinal);
        Assert.Contains("total requested occurrences `a + b <= 200` is parser-owned", normalized, StringComparison.Ordinal);
        Assert.Contains("later parser tests MUST enforce", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void SevenSchemasAreClosedAndAcceptTheirSyntheticGoldenDocuments()
    {
        Assert.All(Tokens, token =>
        {
            var stem = token[..^3];
            var schemaPath = Path.Combine(ContractRoot, $"{stem}.schema.json");
            var fixturePath = Path.Combine(FixtureRoot, $"{stem}.json");
            using var schema = JsonDocument.Parse(File.ReadAllBytes(schemaPath));
            Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.RootElement.GetProperty("$schema").GetString());
            Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
            AssertEveryObjectSchemaIsClosed(schema.RootElement, "$", token);
            Assert.True(ValidateWithPowerShellJsonSchema(fixturePath, schemaPath), token);
        });
    }

    [Fact]
    public void SchemasFreezeCoreFactStatesLabelsSectionsAndStoredValueCollections()
    {
        using var readSchema = ReadSchema("local-monitor-comparison-read.response");
        using var rowsSchema = ReadSchema("local-monitor-comparison-rows.response");
        Assert.Equal(["key", "value"], readSchema.RootElement.GetProperty("$defs").GetProperty("value").GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(["key", "value"], rowsSchema.RootElement.GetProperty("$defs").GetProperty("value").GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(128, readSchema.RootElement.GetProperty("$defs").GetProperty("value").GetProperty("properties").GetProperty("key").GetProperty("maxLength").GetInt32());
        Assert.Equal(16_384, readSchema.RootElement.GetProperty("$defs").GetProperty("value").GetProperty("properties").GetProperty("value").GetProperty("maxLength").GetInt32());
        Assert.Equal("^[a-z0-9_.:/-]{1,128}$", readSchema.RootElement.GetProperty("$defs").GetProperty("value").GetProperty("properties").GetProperty("key").GetProperty("pattern").GetString());
        Assert.Equal(1, readSchema.RootElement.GetProperty("$defs").GetProperty("value").GetProperty("properties").GetProperty("value").GetProperty("minLength").GetInt32());
        Assert.Equal(4096, readSchema.RootElement.GetProperty("$defs").GetProperty("result").GetProperty("properties").GetProperty("values").GetProperty("maxItems").GetInt32());
        Assert.Equal(4096, rowsSchema.RootElement.GetProperty("$defs").GetProperty("item").GetProperty("properties").GetProperty("values").GetProperty("maxItems").GetInt32());

        using var read = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureRoot, "local-monitor-comparison-read.response.json")));
        Assert.Equal(["基準", "比較対象"], read.RootElement.GetProperty("cohorts").EnumerateObject().Select(item => item.Value.GetProperty("label").GetString()));
        Assert.Equal(Sections, read.RootElement.GetProperty("sections").EnumerateArray().Select(section => (
            section.GetProperty("ordinal").GetInt32(), section.GetProperty("key").GetString()!, section.GetProperty("label").GetString()!)));
        Assert.All(read.RootElement.GetProperty("results").EnumerateArray(), result => Assert.All(result.GetProperty("values").EnumerateArray(), value => Assert.Equal(["key", "value"], value.EnumerateObject().Select(p => p.Name))));

        using var rows = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureRoot, "local-monitor-comparison-rows.response.json")));
        Assert.All(rows.RootElement.GetProperty("items").EnumerateArray(), item => Assert.All(item.GetProperty("values").EnumerateArray(), value => Assert.Equal(["key", "value"], value.EnumerateObject().Select(p => p.Name))));
    }

    [Fact]
    public void PreviewLabelsAndClosedEnumsAreExact()
    {
        using var preview = ReadSchema("local-monitor-comparison-preview.response");
        var summaries = preview.RootElement.GetProperty("$defs");
        Assert.Equal("基準", summaries.GetProperty("summaryA").GetProperty("properties").GetProperty("label").GetProperty("const").GetString());
        Assert.Equal("比較対象", summaries.GetProperty("summaryB").GetProperty("properties").GetProperty("label").GetProperty("const").GetString());
        Assert.NotEqual("Baseline", summaries.GetProperty("summaryA").GetProperty("properties").GetProperty("label").GetProperty("const").GetString());
        Assert.NotEqual("Candidate", summaries.GetProperty("summaryB").GetProperty("properties").GetProperty("label").GetProperty("const").GetString());
        Assert.Equal(["session_not_found", "repository_mismatch", "duplicate", "cohort_overlap", "session_archived", "repository_archived", "projection_unavailable", "unsupported_selection", "workspace_too_large"], summaries.GetProperty("excluded").GetProperty("properties").GetProperty("reason").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
        using var evidence = ReadSchema("local-monitor-comparison-evidence.response");
        Assert.Equal([null, "value", "available_count", "median", "minimum", "maximum", "total", "absolute_difference", "relative_difference_percent", "condition", "count", "duration_ms", "input_tokens", "output_tokens", "total_tokens", "cache_read", "cache_creation", "new_input", "error_count", "retry_count"], evidence.RootElement.GetProperty("properties").GetProperty("field_key").GetProperty("enum").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Null ? null : x.GetString()));
    }

    [Fact]
    public void EverySchemaUuidGrammarRejectsCanonicalNearMisses()
    {
        string[] invalid = ["018F0000-0000-7000-8000-000000000001", "018f0000-0000-4000-8000-000000000001", "018f0000-0000-7000-7000-000000000001", "018f00000000-7000-8000-000000000001"];
        foreach (var token in Tokens)
        {
            using var schema = ReadSchema(token[..^3]);
            if (!schema.RootElement.GetProperty("$defs").TryGetProperty("uuidv7", out var uuid)) continue;
            var pattern = uuid.GetProperty("pattern").GetString()!;
            Assert.All(invalid, value => Assert.DoesNotMatch(pattern, value));
        }
    }

    [Fact]
    public void SchemaAcceptsOverAggregateShapeOnlyForTask2ParserRejection()
    {
        var idsA = Enumerable.Repeat("018f0000-0000-7000-8000-000000000001", 101);
        var idsB = Enumerable.Repeat("018f0000-0000-7000-8000-000000000002", 100);
        var json = JsonSerializer.Serialize(new { schema_version = "local-monitor-comparison-preview.request.v1", cohorts = new { a = idsA, b = idsB }, include_archived = false });
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        try { File.WriteAllText(path, json, new UTF8Encoding(false)); Assert.True(ValidateWithPowerShellJsonSchema(path, Path.Combine(ContractRoot, "local-monitor-comparison-preview.request.schema.json"))); }
        finally { File.Delete(path); }
        var specification = Regex.Replace(File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "specifications", "interfaces", "local-monitor-v1-comparison.md")), @"\s+", " ");
        Assert.Contains("⚠️ Task 2 obligation: `LocalMonitorV1ComparisonPreviewRequestParserTests.RejectsAggregateOccurrenceCountAbove200` MUST reject the schema-valid 101+100 request", specification, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemasRejectNoncanonicalIdentityAndChangedFrozenLabels()
    {
        using var create = ReadSchema("local-monitor-comparison-create.response");
        var locationPattern = create.RootElement.GetProperty("properties").GetProperty("location").GetProperty("pattern").GetString()!;
        Assert.DoesNotMatch(locationPattern, "/repositories/not-a-uuid/comparisons/not-a-uuid");
        string[] badIds = ["018F0000-0000-7000-8000-000000000001", "018f0000-0000-4000-8000-000000000001", "018f0000-0000-7000-7000-000000000001", "018f00000000-7000-8000-000000000001"];
        Assert.All(badIds, id => Assert.DoesNotMatch(locationPattern, $"/repositories/{id}/comparisons/{id}"));
        using var read = ReadSchema("local-monitor-comparison-read.response");
        Assert.Equal("基準", read.RootElement.GetProperty("$defs").GetProperty("cohortA").GetProperty("properties").GetProperty("label").GetProperty("const").GetString());
        Assert.Equal("target", read.RootElement.GetProperty("$defs").GetProperty("section1").GetProperty("allOf")[1].GetProperty("properties").GetProperty("key").GetProperty("const").GetString());
        using var evidence = ReadSchema("local-monitor-comparison-evidence.response");
        Assert.Equal("#/$defs/uuidv7", evidence.RootElement.GetProperty("$defs").GetProperty("item").GetProperty("properties").GetProperty("execution_id").GetProperty("oneOf")[0].GetProperty("$ref").GetString());
        var sessionLocationPattern = evidence.RootElement.GetProperty("$defs").GetProperty("item").GetProperty("properties").GetProperty("session_location").GetProperty("pattern").GetString()!;
        Assert.All(badIds, id => Assert.DoesNotMatch(sessionLocationPattern, $"/sessions/{id}?execution={id}"));
        for (var index = 0; index < Sections.Length; index++)
        {
            var definition = read.RootElement.GetProperty("$defs").GetProperty($"section{index + 1}").GetProperty("allOf")[1].GetProperty("properties");
            Assert.Equal(Sections[index].Ordinal, definition.GetProperty("ordinal").GetProperty("const").GetInt32());
            Assert.Equal(Sections[index].Key, definition.GetProperty("key").GetProperty("const").GetString());
            Assert.Equal(Sections[index].Label, definition.GetProperty("label").GetProperty("const").GetString());
        }
    }

    [Fact]
    public void SchemasFreezeExpressibleCollectionAndPagingBounds()
    {
        using var preview = ReadSchema("local-monitor-comparison-preview.request");
        var cohort = preview.RootElement.GetProperty("$defs").GetProperty("cohort");
        Assert.Equal(1, cohort.GetProperty("minItems").GetInt32());
        Assert.Equal(199, cohort.GetProperty("maxItems").GetInt32());
        using var rows = ReadSchema("local-monitor-comparison-rows.response");
        Assert.Equal(100, rows.RootElement.GetProperty("properties").GetProperty("items").GetProperty("maxItems").GetInt32());
        Assert.Equal(1, rows.RootElement.GetProperty("properties").GetProperty("next_cursor").GetProperty("minLength").GetInt32());
        Assert.Equal(512, rows.RootElement.GetProperty("properties").GetProperty("next_cursor").GetProperty("maxLength").GetInt32());
        Assert.Equal(4096, rows.RootElement.GetProperty("$defs").GetProperty("item").GetProperty("properties").GetProperty("values").GetProperty("maxItems").GetInt32());
        using var evidence = ReadSchema("local-monitor-comparison-evidence.response");
        Assert.Equal(200, evidence.RootElement.GetProperty("properties").GetProperty("items").GetProperty("maxItems").GetInt32());
        Assert.Equal(1, evidence.RootElement.GetProperty("properties").GetProperty("next_cursor").GetProperty("minLength").GetInt32());
        Assert.Equal(512, evidence.RootElement.GetProperty("properties").GetProperty("next_cursor").GetProperty("maxLength").GetInt32());
        using var previewResponse = ReadSchema("local-monitor-comparison-preview.response");
        Assert.Equal(2, previewResponse.RootElement.GetProperty("properties").GetProperty("requested").GetProperty("minItems").GetInt32());
        Assert.Equal(200, previewResponse.RootElement.GetProperty("properties").GetProperty("requested").GetProperty("maxItems").GetInt32());
        Assert.Equal(200, previewResponse.RootElement.GetProperty("properties").GetProperty("included").GetProperty("maxItems").GetInt32());
        Assert.Equal(200, previewResponse.RootElement.GetProperty("properties").GetProperty("excluded").GetProperty("maxItems").GetInt32());
        using var read = ReadSchema("local-monitor-comparison-read.response");
        Assert.Equal(9, read.RootElement.GetProperty("properties").GetProperty("sections").GetProperty("minItems").GetInt32());
        Assert.Equal(9, read.RootElement.GetProperty("properties").GetProperty("sections").GetProperty("maxItems").GetInt32());
        Assert.Equal(4096, read.RootElement.GetProperty("properties").GetProperty("results").GetProperty("maxItems").GetInt32());
        Assert.Equal(4096, read.RootElement.GetProperty("$defs").GetProperty("result").GetProperty("properties").GetProperty("values").GetProperty("maxItems").GetInt32());

        (string Token, int Count, string Sha256)[] completeInventories =
        [
            ("local-monitor-comparison-create.request", 4, "10315d8be24ad5dd873d5e231a155dc6208d2990c07708d8de4cf257c71d1388"),
            ("local-monitor-comparison-create.response", 4, "f4e1825ddb9cece844c6a0725eff773ff8a7cdefe317245d462eb8915d01f631"),
            ("local-monitor-comparison-evidence.response", 10, "5e731008b08de85443e9cb7e160e7d1349a7be4d436cfdf5887f0e00ad391372"),
            ("local-monitor-comparison-preview.request", 3, "6b15153afc39f33ca276423a23bef7710479e227a603cad0379c4625a6a39967"),
            ("local-monitor-comparison-preview.response", 30, "57b4e2a132cca9fecb58c5219b5b7794c85d4395a064a5c94afb15b48489b3dc"),
            ("local-monitor-comparison-read.response", 25, "bca8f7f018b6c23d1e6e68e12bbd2557fcc27d3124e9b3e3616f04ad973f23ee"),
            ("local-monitor-comparison-rows.response", 16, "0c7749b84abf4705832c454308a8d60604aeaaf9626e4ce4fdaf0381a27cab4b"),
        ];
        foreach (var expected in completeInventories)
        {
            using var schema = ReadSchema(expected.Token);
            var inventory = CollectExpressibleBounds(schema.RootElement, "#").Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(expected.Count, inventory.Length);
            Assert.Equal(expected.Sha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', inventory)))).ToLowerInvariant());
        }
    }

    [Fact]
    public void OwnerContractFreezesQueriesSecurityAndExactErrorTable()
    {
        var specification = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "specifications", "interfaces", "local-monitor-v1-comparison.md"));
        var normalized = Regex.Replace(specification, @"\s+", " ");
        Assert.Contains("`family`, `q`, `after`, `limit`", specification, StringComparison.Ordinal);
        Assert.Contains("1..100, default 50", specification, StringComparison.Ordinal);
        Assert.Contains("`result_ordinal`, `field_key`, `after`, `limit`", specification, StringComparison.Ordinal);
        Assert.Contains("1..200, default 100", specification, StringComparison.Ordinal);
        Assert.Contains("same origin", specification, StringComparison.Ordinal);
        Assert.Contains("CSRF", specification, StringComparison.Ordinal);
        Assert.Contains("no CORS", specification, StringComparison.Ordinal);
        Assert.Contains("HEAD has the GET-equivalent status, headers, and content length with zero body", normalized, StringComparison.Ordinal);
        Assert.Contains("Exact security/transport rule: POST request entity is strict UTF-8, at most 16,384 bytes, exact `application/json; charset=utf-8`, same-origin, and requires the existing CSRF header.", normalized, StringComparison.Ordinal);
        Assert.Contains("Exact publication rule: every response is fully buffered strict UTF-8 JSON, at most 8,388,608 bytes, `Cache-Control: no-store`, exact `Content-Length`, and no CORS.", normalized, StringComparison.Ordinal);
        Assert.Contains("Exact no-echo rule: errors and logs MUST NOT echo any request value, Repository ID, Session ID, comparison ID, cursor, search value, field key, or locator.", normalized, StringComparison.Ordinal);
        Assert.Contains("JSON Schema `maxLength` counts characters and is not byte-bound proof", normalized, StringComparison.Ordinal);

        (int Status, string Code)[] errors =
        [
            (400, "invalid_host"), (400, "invalid_request"), (400, "invalid_cursor"), (403, "csrf_rejected"),
            (405, "method_not_allowed"), (409, "comparison_selection_invalid"), (409, "comparison_preview_stale"),
            (409, "workspace_too_large"), (404, "comparison_not_found"), (410, "comparison_expired"), (503, "persistence_busy"),
        ];
        Assert.All(errors, error => Assert.Contains($"{error.Status} `{error.Code}` `{{\"error\":\"{error.Code}\"}}`", specification, StringComparison.Ordinal));
    }

    [Fact]
    public void ReadSchemaRejectsEveryMutatedSectionTuple()
    {
        for (var index = 0; index < Sections.Length; index++)
        {
            foreach (var property in new[] { "ordinal", "key", "label" })
            {
                var node = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureRoot, "local-monitor-comparison-read.response.json")))!;
                node["sections"]![index]![property] = property == "ordinal" ? JsonValue.Create(99) : JsonValue.Create("mutated");
                AssertSchemaRejects(node.ToJsonString(), "local-monitor-comparison-read.response");
            }
        }
    }

    [Fact]
    public void GoldenDocumentsAreCanonicalOrderedUtf8WithoutRawFields()
    {
        AssertGolden("local-monitor-comparison-preview.request", "schema_version", "cohorts", "include_archived");
        AssertGolden("local-monitor-comparison-create.request", "schema_version", "cohorts", "include_archived", "selection_sha256", "preview_revision");
        AssertGolden("local-monitor-comparison-preview.response", "schema_version", "valid", "selection_sha256", "preview_revision", "cohorts", "requested", "included", "excluded");
        AssertGolden("local-monitor-comparison-create.response", "schema_version", "comparison_id", "location", "receipt_sha256", "created_at", "expires_at");
        AssertGolden("local-monitor-comparison-read.response", "schema_version", "comparison_id", "repository_id", "receipt_sha256", "created_at", "expires_at", "cohorts", "sections", "results");
        AssertGolden("local-monitor-comparison-rows.response", "schema_version", "comparison_id", "family", "items", "next_cursor");
        AssertGolden("local-monitor-comparison-evidence.response", "schema_version", "comparison_id", "result_ordinal", "field_key", "items", "next_cursor");
        foreach (var requestName in new[] { "local-monitor-comparison-preview.request.json", "local-monitor-comparison-create.request.json" })
        {
            using var request = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureRoot, requestName)));
            AssertProperties(request.RootElement.GetProperty("cohorts"), "a", "b");
        }
        using var preview = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureRoot, "local-monitor-comparison-preview.response.json")));
        AssertProperties(preview.RootElement.GetProperty("cohorts"), "a", "b");
        Assert.All(preview.RootElement.GetProperty("cohorts").EnumerateObject(), cohort => AssertProperties(cohort.Value, "label", "requested_count", "included_count", "excluded_count"));
        Assert.All(preview.RootElement.GetProperty("requested").EnumerateArray(), item => AssertProperties(item, "cohort", "request_ordinal", "session_id"));
        Assert.All(preview.RootElement.GetProperty("included").EnumerateArray(), item => { AssertProperties(item, "cohort", "session_id", "metadata"); AssertProperties(item.GetProperty("metadata"), "archive_state", "source", "model", "projection_version", "completeness", "metric_coverage", "session_revision", "projection_revision"); });
        Assert.NotEmpty(preview.RootElement.GetProperty("excluded").EnumerateArray());
        Assert.All(preview.RootElement.GetProperty("excluded").EnumerateArray(), item => AssertProperties(item, "cohort", "request_ordinal", "session_id", "reason", "metadata"));
        using var read = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureRoot, "local-monitor-comparison-read.response.json")));
        AssertProperties(read.RootElement.GetProperty("cohorts"), "a", "b");
        Assert.All(read.RootElement.GetProperty("cohorts").EnumerateObject(), cohort => AssertProperties(cohort.Value, "label", "session_ids", "included_count"));
        Assert.All(read.RootElement.GetProperty("sections").EnumerateArray(), item => AssertProperties(item, "ordinal", "key", "label"));
        Assert.All(read.RootElement.GetProperty("results").EnumerateArray(), item => AssertProperties(item, "result_ordinal", "section_key", "row_kind", "row_key", "values"));
        Assert.All(read.RootElement.GetProperty("results")[0].GetProperty("values").EnumerateArray(), item => AssertProperties(item, "key", "value"));
        using var rows = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureRoot, "local-monitor-comparison-rows.response.json")));
        Assert.All(rows.RootElement.GetProperty("items").EnumerateArray(), item => { AssertProperties(item, "result_ordinal", "row_key", "display_name", "values"); Assert.All(item.GetProperty("values").EnumerateArray(), value => AssertProperties(value, "key", "value")); });
        using var evidence = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureRoot, "local-monitor-comparison-evidence.response.json")));
        Assert.All(evidence.RootElement.GetProperty("items").EnumerateArray(), item => AssertProperties(item, "evidence_ordinal", "cohort", "session_id", "state", "unavailable_reason", "consumed_value", "consumed_revision", "execution_id", "node_id", "session_location"));
    }

    private static void AssertGolden(string stem, params string[] properties)
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixtureRoot, $"{stem}.json"));
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.NotEqual((byte)'\n', bytes[^1]);
        Assert.NotEqual((byte)'\r', bytes[^1]);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotMatch("(?i)raw|prompt|response_body|path|locator|tool_arguments|tool_results|skill_body", text);
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(properties, document.RootElement.EnumerateObject().Select(property => property.Name));
    }

    private static void AssertProperties(JsonElement value, params string[] expected) => Assert.Equal(expected, value.EnumerateObject().Select(property => property.Name));

    private static void AssertSchemaRejects(string json, string stem)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        try { File.WriteAllText(path, json, new UTF8Encoding(false)); Assert.False(ValidateWithPowerShellJsonSchema(path, Path.Combine(ContractRoot, $"{stem}.schema.json"))); }
        finally { File.Delete(path); }
    }

    private static bool ValidateWithPowerShellJsonSchema(string instancePath, string schemaPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "$instance = Get-Content -Raw -LiteralPath $env:COMPARISON_CONTRACT_INSTANCE; if ($instance | Test-Json -SchemaFile $env:COMPARISON_CONTRACT_SCHEMA) { exit 0 } else { exit 1 }" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["COMPARISON_CONTRACT_INSTANCE"] = instancePath;
        startInfo.Environment["COMPARISON_CONTRACT_SCHEMA"] = schemaPath;
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static JsonDocument ReadSchema(string stem) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ContractRoot, $"{stem}.schema.json")));

    private static IEnumerable<string> CollectExpressibleBounds(JsonElement element, string path)
    {
        string[] keywords = ["minItems", "maxItems", "minLength", "maxLength", "minimum", "maximum", "pattern"];
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (keywords.Contains(property.Name, StringComparer.Ordinal)) yield return $"{path}/{property.Name}={property.Value}";
                foreach (var nested in CollectExpressibleBounds(property.Value, $"{path}/{property.Name}")) yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in CollectExpressibleBounds(item, $"{path}/{index}")) yield return nested;
                index++;
            }
        }
    }

    private static void AssertEveryObjectSchemaIsClosed(JsonElement element, string path, string token)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object")
            {
                Assert.True(element.TryGetProperty("additionalProperties", out var closed) && closed.ValueKind == JsonValueKind.False, $"{token}: {path}");
            }
            foreach (var property in element.EnumerateObject()) AssertEveryObjectSchemaIsClosed(property.Value, $"{path}/{property.Name}", token);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray()) AssertEveryObjectSchemaIsClosed(item, $"{path}/{index++}", token);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
