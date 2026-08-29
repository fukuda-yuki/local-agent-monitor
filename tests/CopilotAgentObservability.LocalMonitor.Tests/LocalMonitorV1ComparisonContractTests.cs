using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
        using var read = ReadSchema("local-monitor-comparison-read.response");
        Assert.Equal("基準", read.RootElement.GetProperty("$defs").GetProperty("cohortA").GetProperty("properties").GetProperty("label").GetProperty("const").GetString());
        Assert.Equal("target", read.RootElement.GetProperty("$defs").GetProperty("section1").GetProperty("properties").GetProperty("key").GetProperty("const").GetString());
        using var evidence = ReadSchema("local-monitor-comparison-evidence.response");
        Assert.Equal("#/$defs/uuidv7", evidence.RootElement.GetProperty("$defs").GetProperty("item").GetProperty("properties").GetProperty("execution_id").GetProperty("oneOf")[0].GetProperty("$ref").GetString());
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
        using var evidence = ReadSchema("local-monitor-comparison-evidence.response");
        Assert.Equal(200, evidence.RootElement.GetProperty("properties").GetProperty("items").GetProperty("maxItems").GetInt32());
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

        (int Status, string Code)[] errors =
        [
            (400, "invalid_host"), (400, "invalid_request"), (400, "invalid_cursor"), (403, "csrf_rejected"),
            (405, "method_not_allowed"), (409, "comparison_selection_invalid"), (409, "comparison_preview_stale"),
            (409, "workspace_too_large"), (404, "comparison_not_found"), (410, "comparison_expired"), (503, "persistence_busy"),
        ];
        Assert.All(errors, error => Assert.Contains($"{error.Status} `{error.Code}` `{{\"error\":\"{error.Code}\"}}`", specification, StringComparison.Ordinal));
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

    private static bool ValidateWithPowerShellJsonSchema(string instancePath, string schemaPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "pwsh",
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference = 'SilentlyContinue'; $instance = Get-Content -Raw -LiteralPath $args[0]; if (Test-Json -Json $instance -SchemaFile $args[1]) { exit 0 } else { exit 1 }", instancePath, schemaPath },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.NotNull(process);
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static JsonDocument ReadSchema(string stem) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ContractRoot, $"{stem}.schema.json")));

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
