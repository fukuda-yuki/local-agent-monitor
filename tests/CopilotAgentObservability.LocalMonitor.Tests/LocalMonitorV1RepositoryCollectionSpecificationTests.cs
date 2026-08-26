using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1RepositoryCollectionSpecificationTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string FixtureRoot = Path.Combine(
        AppContext.BaseDirectory, "TestData", "LocalMonitorV1RepositoryCollection");

    [Fact]
    public void CanonicalSpecificationAndSchemaOwnTheClosedSuccessAndCursorWire()
    {
        var specification = File.ReadAllText(Path.Combine(
            RepositoryRoot, "docs", "specifications", "interfaces", "local-monitor-v1-repository-collection.md"));
        using var schema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            FixtureRoot, "repository-collection.response.schema.json")));

        Assert.Contains("local-monitor-repositories.response.v1", specification, StringComparison.Ordinal);
        Assert.Contains("GET /api/local-monitor/v1/repositories", specification, StringComparison.Ordinal);
        Assert.Contains("local-monitor-repository-filter\\0v1\\0", specification, StringComparison.Ordinal);
        Assert.Contains("local-monitor-repository-cursor\\0v1\\0", specification, StringComparison.Ordinal);
        Assert.Contains("exactly 101 bytes", specification, StringComparison.Ordinal);
        Assert.Contains("exactly 135 ASCII characters", specification, StringComparison.Ordinal);
        Assert.Contains("exactly 8,388,608 UTF-8 entity bytes", specification, StringComparison.Ordinal);
        Assert.Contains("8,388,608 bytes is accepted", specification, StringComparison.Ordinal);
        Assert.Contains("8,388,609 bytes", specification, StringComparison.Ordinal);
        Assert.Contains("fully buffers and measures the complete entity", specification, StringComparison.Ordinal);
        Assert.Contains("before publishing status, headers, or body bytes", specification, StringComparison.Ordinal);
        Assert.Contains("publishes no partial success body", specification, StringComparison.Ordinal);
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.RootElement.GetProperty("$schema").GetString());
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        AssertSchemaObject(schema.RootElement, "schema_version", "workspace_revision", "repositories",
            "all_session_count", "unassigned_active_session_count", "archived_repository_count", "next_cursor");
        var definitions = schema.RootElement.GetProperty("$defs");
        var repository = definitions.GetProperty("repository");
        Assert.False(repository.GetProperty("additionalProperties").GetBoolean());
        AssertSchemaObject(repository, "repository_id", "display_name", "archive_state", "archive_revision",
            "active_session_count", "last_observed_at", "assignment_conflict_count", "repository_revision");
        var repositories = schema.RootElement.GetProperty("properties").GetProperty("repositories");
        Assert.Equal(0, repositories.GetProperty("minItems").GetInt32());
        Assert.Equal(200, repositories.GetProperty("maxItems").GetInt32());
        var nextCursor = schema.RootElement.GetProperty("properties").GetProperty("next_cursor");
        Assert.Equal(135, nextCursor.GetProperty("minLength").GetInt32());
        Assert.Equal(135, nextCursor.GetProperty("maxLength").GetInt32());
        Assert.Equal("^[A-Za-z0-9_-]{135}$", nextCursor.GetProperty("pattern").GetString());
        Assert.Equal(0, definitions.GetProperty("count").GetProperty("minimum").GetInt32());
        Assert.Equal("^[0-9a-f]{64}$", definitions.GetProperty("revision").GetProperty("pattern").GetString());
        Assert.Equal("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
            definitions.GetProperty("uuidv7").GetProperty("pattern").GetString());
        Assert.Equal("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{7}\\+00:00$",
            definitions.GetProperty("canonicalUtc").GetProperty("pattern").GetString());
        Assert.Equal("integer", definitions.GetProperty("count").GetProperty("type").GetString());
        Assert.Equal("string", definitions.GetProperty("revision").GetProperty("type").GetString());
        Assert.Equal("string", definitions.GetProperty("uuidv7").GetProperty("type").GetString());
        Assert.Equal("string", definitions.GetProperty("canonicalUtc").GetProperty("type").GetString());
        var repositoryProperties = repository.GetProperty("properties");
        Assert.Equal("#/$defs/uuidv7", repositoryProperties.GetProperty("repository_id").GetProperty("$ref").GetString());
        Assert.Equal(1, repositoryProperties.GetProperty("display_name").GetProperty("minLength").GetInt32());
        Assert.Equal(200, repositoryProperties.GetProperty("display_name").GetProperty("maxLength").GetInt32());
        Assert.Equal(new[] { "active", "archived" }, repositoryProperties.GetProperty("archive_state")
            .GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("#/$defs/count", repositoryProperties.GetProperty("archive_revision").GetProperty("$ref").GetString());
        Assert.Equal("#/$defs/count", repositoryProperties.GetProperty("active_session_count").GetProperty("$ref").GetString());
        Assert.Equal("#/$defs/count", repositoryProperties.GetProperty("assignment_conflict_count").GetProperty("$ref").GetString());
        Assert.Equal("#/$defs/revision", repositoryProperties.GetProperty("repository_revision").GetProperty("$ref").GetString());
        var observedAtVariants = repositoryProperties.GetProperty("last_observed_at").GetProperty("oneOf").EnumerateArray().ToArray();
        Assert.Equal("#/$defs/canonicalUtc", observedAtVariants[0].GetProperty("$ref").GetString());
        Assert.Equal("null", observedAtVariants[1].GetProperty("type").GetString());
    }

    [Fact]
    public void RouteTransportOwnsTheClosedRepositoryGetTransport()
    {
        var transport = File.ReadAllText(Path.Combine(
            RepositoryRoot, "docs", "specifications", "interfaces", "local-monitor-v1-route-transport.md"));

        Assert.Contains("## 7A. Repository collection GET transport", transport, StringComparison.Ordinal);
        Assert.Contains("archive_scope, after, limit", transport, StringComparison.Ordinal);
        Assert.Contains("`archive_scope` | Optional singleton", transport, StringComparison.Ordinal);
        Assert.Contains("`after` | Optional singleton", transport, StringComparison.Ordinal);
        Assert.Contains("`limit` | Optional singleton", transport, StringComparison.Ordinal);
        Assert.Contains("Host guard;", transport, StringComparison.Ordinal);
        Assert.Contains("exact route and method dispatch, including the shared-path Repository catalog POST contract;", transport, StringComparison.Ordinal);
        Assert.Contains("integrated `Allow: GET, HEAD, POST`", transport, StringComparison.Ordinal);
        Assert.Contains("raw-default only", transport, StringComparison.Ordinal);
        Assert.Contains("local-monitor-v1-repository-collection.md", transport, StringComparison.Ordinal);
    }

    [Fact]
    public void GoldenResponsesHaveExactBytesOrderAndPaginationStates()
    {
        var fixtures = new[] { "empty.json", "final-page.json", "more-page.json" }
            .Select(name => (Name: name, Bytes: File.ReadAllBytes(Path.Combine(FixtureRoot, name))))
            .ToArray();

        Assert.All(fixtures, fixture =>
        {
            Assert.False(fixture.Bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            Assert.NotEqual((byte)'\n', fixture.Bytes[^1]);
            Assert.NotEqual((byte)'\r', fixture.Bytes[^1]);
            _ = new UTF8Encoding(false, true).GetString(fixture.Bytes);
        });

        var expectedEmpty = "{\"schema_version\":\"local-monitor-repositories.response.v1\",\"workspace_revision\":\"" +
            new string('0', 64) + "\",\"repositories\":[],\"all_session_count\":0,\"unassigned_active_session_count\":0,\"archived_repository_count\":0,\"next_cursor\":null}";
        Assert.Equal(Encoding.UTF8.GetBytes(expectedEmpty), fixtures.Single(fixture => fixture.Name == "empty.json").Bytes);

        foreach (var fixture in fixtures)
        {
            using var document = JsonDocument.Parse(fixture.Bytes);
            AssertProperties(document.RootElement, "schema_version", "workspace_revision", "repositories",
                "all_session_count", "unassigned_active_session_count", "archived_repository_count", "next_cursor");
            foreach (var repository in document.RootElement.GetProperty("repositories").EnumerateArray())
            {
                AssertProperties(repository, "repository_id", "display_name", "archive_state", "archive_revision",
                    "active_session_count", "last_observed_at", "assignment_conflict_count", "repository_revision");
            }
        }

        using var finalPage = JsonDocument.Parse(fixtures.Single(fixture => fixture.Name == "final-page.json").Bytes);
        using var morePage = JsonDocument.Parse(fixtures.Single(fixture => fixture.Name == "more-page.json").Bytes);
        Assert.Equal(JsonValueKind.Null, finalPage.RootElement.GetProperty("next_cursor").ValueKind);
        var cursor = morePage.RootElement.GetProperty("next_cursor").GetString()!;
        Assert.Equal(CreateCursor(), cursor);
        Assert.Equal(135, cursor.Length);
        Assert.DoesNotContain("018f0000-0000-7000-8000-000000000101", cursor, StringComparison.Ordinal);
    }

    [Fact]
    public void Draft202012SchemaAcceptsEveryGoldenResponse()
    {
        var schemaPath = Path.Combine(FixtureRoot, "repository-collection.response.schema.json");
        Assert.All(new[] { "empty.json", "final-page.json", "more-page.json" }, fixture =>
            Assert.True(ValidateWithPowerShellJsonSchema(Path.Combine(FixtureRoot, fixture), schemaPath), fixture));
    }

    private static string CreateCursor()
    {
        var key = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var filterPrefix = Encoding.ASCII.GetBytes("local-monitor-repository-filter\0v1\0archive_scope\0include_archived\0limit\0");
        var filterFrame = new byte[filterPrefix.Length + sizeof(ushort)];
        filterPrefix.CopyTo(filterFrame, 0);
        BinaryPrimitives.WriteUInt16BigEndian(filterFrame.AsSpan(filterPrefix.Length), 1);

        var bytes = new byte[101];
        bytes[0] = 0x01;
        HMACSHA256.HashData(key, filterFrame).CopyTo(bytes, 1);
        Encoding.ASCII.GetBytes("018f0000-0000-7000-8000-000000000101").CopyTo(bytes, 33);
        var tagPrefix = Encoding.ASCII.GetBytes("local-monitor-repository-cursor\0v1\0");
        var tagFrame = new byte[tagPrefix.Length + 69];
        tagPrefix.CopyTo(tagFrame, 0);
        bytes.AsSpan(0, 69).CopyTo(tagFrame.AsSpan(tagPrefix.Length));
        HMACSHA256.HashData(key, tagFrame).CopyTo(bytes, 69);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static void AssertProperties(JsonElement element, params string[] expected) =>
        Assert.Equal(expected, element.EnumerateObject().Select(property => property.Name));

    private static void AssertSchemaObject(JsonElement schema, params string[] expected)
    {
        Assert.Equal(expected, schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(expected, schema.GetProperty("properties").EnumerateObject().Select(property => property.Name));
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
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CopilotAgentObservability.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
