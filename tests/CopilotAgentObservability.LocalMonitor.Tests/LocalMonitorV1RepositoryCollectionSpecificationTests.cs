using System.Buffers.Binary;
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
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.RootElement.GetProperty("$schema").GetString());
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.False(schema.RootElement.GetProperty("$defs").GetProperty("repository").GetProperty("additionalProperties").GetBoolean());
        var repositories = schema.RootElement.GetProperty("properties").GetProperty("repositories");
        Assert.Equal(0, repositories.GetProperty("minItems").GetInt32());
        Assert.Equal(200, repositories.GetProperty("maxItems").GetInt32());
        var nextCursor = schema.RootElement.GetProperty("properties").GetProperty("next_cursor");
        Assert.Equal(135, nextCursor.GetProperty("minLength").GetInt32());
        Assert.Equal(135, nextCursor.GetProperty("maxLength").GetInt32());
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
