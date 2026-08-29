using System.Diagnostics;
using System.Text;
using System.Text.Json;

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

    [Fact]
    public void OwnerSpecificationFreezesExactlyFiveOperationsAndSevenSchemas()
    {
        var specification = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "specifications", "interfaces", "local-monitor-v1-comparison.md"));
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
        Assert.Contains("16,384", specification, StringComparison.Ordinal);
        Assert.Contains("8,388,608", specification, StringComparison.Ordinal);
        Assert.Contains("comparison_preview_stale", specification, StringComparison.Ordinal);
        Assert.Contains("HEAD", specification, StringComparison.Ordinal);
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
            Assert.True(ValidateWithPowerShellJsonSchema(fixturePath, schemaPath), token);
        });
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
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "$instance = Get-Content -Raw -LiteralPath $args[0]; if ($instance | Test-Json -SchemaFile $args[1]) { exit 0 } else { exit 1 }", instancePath, schemaPath },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.NotNull(process);
        process.WaitForExit();
        return process.ExitCode == 0;
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
