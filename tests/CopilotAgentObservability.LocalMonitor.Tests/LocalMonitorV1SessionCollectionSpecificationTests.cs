using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1SessionCollectionSpecificationTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string FixtureRoot = Path.Combine(
        AppContext.BaseDirectory, "TestData", "LocalMonitorV1SessionCollection");

    [Fact]
    public void CanonicalSpecificationAndSchemaOwnTheClosedSuccessWire()
    {
        var specificationPath = Path.Combine(
            RepositoryRoot, "docs", "specifications", "interfaces", "local-monitor-v1-session-collection.md");
        var schemaPath = Path.Combine(FixtureRoot, "session-collection.response.schema.json");

        var specification = File.ReadAllText(specificationPath);
        using var schema = JsonDocument.Parse(File.ReadAllBytes(schemaPath));

        Assert.Contains("local-monitor-sessions.response.v1", specification, StringComparison.Ordinal);
        Assert.Contains("POST /api/local-monitor/v1/sessions", specification, StringComparison.Ordinal);
        Assert.Contains("#133", specification, StringComparison.Ordinal);
        Assert.Contains("#134", specification, StringComparison.Ordinal);
        Assert.Contains("8,388,608 UTF-8 entity", specification, StringComparison.Ordinal);
        Assert.Contains("fully buffers and measures the complete entity", specification, StringComparison.Ordinal);
        Assert.Contains("409 workspace_too_large", specification, StringComparison.Ordinal);
        Assert.Contains("publishes no partial success body", specification, StringComparison.Ordinal);
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.RootElement.GetProperty("$schema").GetString());
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void GoldenResponsesHaveExactBytesOrderedClosedShapesAndDistinctPaginationStates()
    {
        var fixtures = new[] { "empty.json", "final-page.json", "more-page.json" }
            .Select(name => (Name: name, Bytes: File.ReadAllBytes(Path.Combine(FixtureRoot, name))))
            .ToArray();

        Assert.All(fixtures, fixture =>
        {
            Assert.False(fixture.Bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            Assert.NotEqual((byte)'\n', fixture.Bytes[^1]);
            Assert.NotEqual((byte)'\r', fixture.Bytes[^1]);
        });
        Assert.Equal(3, fixtures.Select(fixture => Convert.ToHexString(fixture.Bytes)).Distinct(StringComparer.Ordinal).Count());

        var expectedEmpty = "{\"schema_version\":\"local-monitor-sessions.response.v1\",\"workspace_revision\":\"" +
            new string('0', 64) + "\",\"items\":[],\"next_cursor\":null}";
        Assert.Equal(Encoding.UTF8.GetBytes(expectedEmpty), fixtures.Single(fixture => fixture.Name == "empty.json").Bytes);

        foreach (var fixture in fixtures)
        {
            using var document = JsonDocument.Parse(fixture.Bytes);
            AssertProperties(document.RootElement, "schema_version", "workspace_revision", "items", "next_cursor");
            Assert.Equal("local-monitor-sessions.response.v1", document.RootElement.GetProperty("schema_version").GetString());
            Assert.Matches("^[0-9a-f]{64}$", document.RootElement.GetProperty("workspace_revision").GetString()!);

            foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
            {
                AssertProperties(item, "session_id", "assignment", "archive", "label", "status", "completeness",
                    "source", "model", "summary", "tokens", "timing", "capture_notes", "workspace_revision");
                AssertProperties(item.GetProperty("assignment"), "state", "authority", "revision", "repository_id", "candidate_repository_ids");
                AssertProperties(item.GetProperty("archive"), "state", "revision", "effectively_eligible", "exclusion_reason");
                AssertProperties(item.GetProperty("label"), "state", "text");
                AssertProperties(item.GetProperty("source"), "state", "values");
                AssertProperties(item.GetProperty("model"), "state", "values");
                AssertProperties(item.GetProperty("summary"), "skill", "tool", "subagent", "error", "retry");
                Assert.All(item.GetProperty("summary").EnumerateObject(), fact => AssertProperties(fact.Value, "state", "count"));
                AssertProperties(item.GetProperty("tokens"), "authority", "state", "available_execution_count",
                    "total_execution_count", "input", "output", "total", "reasoning", "cache_read", "cache_creation",
                    "new_input", "cache_read_ratio_basis_points");
                Assert.All(item.GetProperty("tokens").EnumerateObject().Skip(4), component => AssertProperties(component.Value, "state", "value"));
                AssertProperties(item.GetProperty("timing"), "state", "started_at", "ended_at", "duration_ms");
            }
        }

        using var finalPage = JsonDocument.Parse(fixtures.Single(fixture => fixture.Name == "final-page.json").Bytes);
        using var morePage = JsonDocument.Parse(fixtures.Single(fixture => fixture.Name == "more-page.json").Bytes);
        Assert.Equal(JsonValueKind.Null, finalPage.RootElement.GetProperty("next_cursor").ValueKind);
        var nextCursor = morePage.RootElement.GetProperty("next_cursor").GetString()!;
        var cursorKey = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var cursorRequest = ParseCursorRequest();
        Assert.Equal(cursorRequest.EffectiveLimit, morePage.RootElement.GetProperty("items").GetArrayLength());
        var regenerated = LocalMonitorV1SessionCursorCodec.Encode(cursorKey, cursorRequest, new(
            LocalMonitorV1SessionSortGroup.ValidTime, 1_767_312_000_000, "018f0000-0000-7000-8000-000000000002"));
        Assert.Equal(regenerated, nextCursor);
        Assert.Equal(147, nextCursor.Length);
        Assert.True(LocalMonitorV1SessionCursorCodec.TryDecode(nextCursor, cursorKey, cursorRequest, out var cursorPosition));
        Assert.Equal(new LocalMonitorV1SessionCursorPosition(
            LocalMonitorV1SessionSortGroup.ValidTime,
            1_767_312_000_000,
            "018f0000-0000-7000-8000-000000000002"), cursorPosition);

        var allItems = finalPage.RootElement.GetProperty("items").EnumerateArray()
            .Concat(morePage.RootElement.GetProperty("items").EnumerateArray()).ToArray();
        Assert.Contains(allItems, item => item.GetProperty("archive").GetProperty("state").GetString() == "archived");
        Assert.Contains(allItems, item => item.GetProperty("assignment").GetProperty("state").GetString() == "unassigned");
        Assert.Contains(allItems, item => item.GetProperty("assignment").GetProperty("state").GetString() == "conflict");
        Assert.Contains(allItems, item => item.GetProperty("summary").GetProperty("tool").GetProperty("count").ValueKind == JsonValueKind.Null);
        Assert.Contains(allItems, item => item.GetProperty("summary").GetProperty("tool").GetProperty("count").ValueKind == JsonValueKind.Number &&
            item.GetProperty("summary").GetProperty("tool").GetProperty("count").GetInt64() == 0);
        Assert.Contains(allItems, item => item.GetProperty("tokens").GetProperty("state").GetString() == "inconsistent");
        Assert.Contains(allItems, item => item.GetProperty("tokens").GetProperty("cache_read").GetProperty("state").GetString() == "inconsistent");
    }

    [Fact]
    public void JsonSchemaAcceptsEveryGoldenResponse()
    {
        var schemaPath = Path.Combine(FixtureRoot, "session-collection.response.schema.json");
        Assert.All(new[] { "empty.json", "final-page.json", "more-page.json" }, fixture =>
            Assert.True(ValidateWithPowerShellJsonSchema(Path.Combine(FixtureRoot, fixture), schemaPath), fixture));
    }

    private static void AssertProperties(JsonElement value, params string[] expected) =>
        Assert.Equal(expected, value.EnumerateObject().Select(property => property.Name));

    private static LocalMonitorV1SessionSearchRequest ParseCursorRequest()
    {
        const string RequestJson = "{\"schema_version\":\"local-monitor-session-search.request.v1\",\"scope\":\"all\",\"repository_id\":null,\"archive_scope\":\"active_only\",\"from\":null,\"to\":null,\"source\":[],\"model\":[],\"status\":[],\"has_skill\":null,\"has_subagent\":null,\"has_error\":null,\"has_retry\":null,\"q\":null,\"cursor\":null,\"limit\":1}";
        Assert.Equal(
            "Success",
            LocalMonitorV1SessionSearchRequestParser.Parse(Encoding.UTF8.GetBytes(RequestJson), out var request).ToString());
        Assert.Equal(1, request!.EffectiveLimit);
        return request;
    }

    private static bool ValidateWithPowerShellJsonSchema(string instancePath, string schemaPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "pwsh",
            ArgumentList =
            {
                "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                "$instance = Get-Content -Raw -LiteralPath $args[0]; " +
                "if ($instance | Test-Json -SchemaFile $args[1]) { exit 0 } else { exit 1 }",
                instancePath, schemaPath,
            },
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
            if (File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
