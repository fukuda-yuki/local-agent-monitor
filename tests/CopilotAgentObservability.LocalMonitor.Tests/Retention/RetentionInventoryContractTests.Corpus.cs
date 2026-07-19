using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed partial class RetentionInventoryContractTests
{
    private const string SchemaMigrationPrefix = "tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/";

    [Fact]
    public void CloseoutCorpusManifest_IsExact()
    {
        var schemaRoot = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations");
        var manifestPath = Path.Combine(schemaRoot, "retention", "retention-closeout-corpus-v1", "manifest.json");
        Assert.True(File.Exists(manifestPath), "The checked-in closeout corpus manifest is required.");

        var manifest = JsonSerializer.Deserialize<CloseoutCorpusManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(manifest);
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.NotNull(manifest.Entries);
        Assert.Equal(ExpectedEntries, manifest.Entries.Select(entry => new CorpusIdentity(entry.RelativePath, entry.FixtureKind, entry.FixtureVersion, entry.PreMigrationSentinel)).ToArray());
        Assert.Equal(ExpectedEntries.Length, manifest.Entries.Select(entry => entry.RelativePath).Distinct(StringComparer.Ordinal).Count());

        var expectedPaths = ExpectedEntries.Select(entry => entry.RelativePath).ToHashSet(StringComparer.Ordinal);
        var actualPaths = Directory.EnumerateFiles(schemaRoot, "*", SearchOption.AllDirectories)
            .Select(path => SchemaMigrationPrefix + Path.GetRelativePath(schemaRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => path is not SchemaMigrationPrefix + "session/manifest.json"
                and not SchemaMigrationPrefix + "monitor/manifest.json"
                and not SchemaMigrationPrefix + "retention/manifest.json"
                and not SchemaMigrationPrefix + "retention/retention-closeout-corpus-v1/manifest.json")
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expectedPaths.OrderBy(path => path, StringComparer.Ordinal), actualPaths.OrderBy(path => path, StringComparer.Ordinal));

        foreach (var entry in manifest.Entries)
        {
            Assert.Matches("^[0-9a-f]{64}$", entry.Sha256);
            var path = Path.Combine(schemaRoot, entry.RelativePath[SchemaMigrationPrefix.Length..].Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(entry.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
            AssertReconcilesWithAuthoritativeFixtureEvidence(schemaRoot, entry, path);
            var migrates = entry.FixtureKind is "session" or "monitor" or "retention-catalog";
            Assert.Equal(migrates ? "migrated" : "not-applicable", entry.MigrationResult);
            Assert.Equal(migrates ? "passed" : "not-applicable", entry.FirstFreshHostRestart);
            Assert.Equal(migrates ? "passed" : "not-applicable", entry.SecondFreshHostRestart);
        }
    }

    private static void AssertReconcilesWithAuthoritativeFixtureEvidence(string schemaRoot, CloseoutCorpusEntry entry, string path)
    {
        switch (entry.FixtureKind)
        {
            case "session":
                AssertSqliteEntry(schemaRoot, "session", entry, "sessionId");
                break;
            case "monitor":
                AssertSqliteEntry(schemaRoot, "monitor", entry, "traceId");
                break;
            case "retention-catalog":
                AssertRetentionCatalogEntry(schemaRoot, entry);
                break;
            case "sensitive-bundle":
                AssertJsonFixtureSentinel(entry, path, entry.RelativePath.EndsWith(".retention-owner.json", StringComparison.Ordinal) ? "owner" : entry.RelativePath.EndsWith("/manifest.json", StringComparison.Ordinal) ? "bundle_id" : "evidence_id");
                break;
            case "sdk-directory" when entry.RelativePath.EndsWith(".retention-owner.json", StringComparison.Ordinal):
                AssertJsonFixtureSentinel(entry, path, "owner");
                break;
            case "sdk-directory":
                Assert.Equal(entry.PreMigrationSentinel, Encoding.UTF8.GetString(File.ReadAllBytes(path)));
                break;
            default:
                throw new Xunit.Sdk.XunitException($"Unknown corpus fixture kind: {entry.FixtureKind}");
        }
    }

    private static void AssertSqliteEntry(string schemaRoot, string component, CloseoutCorpusEntry entry, string sentinelName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(schemaRoot, component, "manifest.json")));
        var fixture = document.RootElement.GetProperty("fixtures").EnumerateArray().Single(candidate => candidate.GetProperty("file").GetString() == Path.GetFileName(entry.RelativePath));
        Assert.Equal(entry.FixtureVersion, fixture.GetProperty("version").GetInt32());
        Assert.Equal(entry.Sha256, fixture.GetProperty("sha256").GetString());
        Assert.Equal(entry.PreMigrationSentinel, fixture.GetProperty("sentinels").GetProperty(sentinelName).GetString());
    }

    private static void AssertRetentionCatalogEntry(string schemaRoot, CloseoutCorpusEntry entry)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(schemaRoot, "retention", "manifest.json")));
        var fixture = document.RootElement.GetProperty("fixtures").EnumerateArray().Single(candidate => candidate.GetProperty("file").GetString() == Path.GetFileName(entry.RelativePath));
        Assert.Equal(entry.Sha256, fixture.GetProperty("sha256").GetString());
        Assert.Equal(entry.FixtureVersion, fixture.GetProperty("sentinels").GetProperty("catalogSchemaVersion").GetInt32());
        Assert.Equal(entry.PreMigrationSentinel, fixture.GetProperty("sentinels").GetProperty("storeInstanceId").GetString());
    }

    private static void AssertJsonFixtureSentinel(CloseoutCorpusEntry entry, string path, string propertyName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(entry.FixtureVersion, document.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(entry.PreMigrationSentinel, document.RootElement.GetProperty(propertyName).GetString());
    }

    private static readonly CorpusIdentity[] ExpectedEntries =
    [
        new(SchemaMigrationPrefix + "session/session-v1.sqlite", "session", 1, "00000001-0000-7000-8000-000000000001"),
        new(SchemaMigrationPrefix + "session/session-v2.sqlite", "session", 2, "00000001-0000-7000-8000-000000000002"),
        new(SchemaMigrationPrefix + "session/session-v3.sqlite", "session", 3, "00000001-0000-7000-8000-000000000003"),
        new(SchemaMigrationPrefix + "session/session-v4.sqlite", "session", 4, "00000001-0000-7000-8000-000000000004"),
        new(SchemaMigrationPrefix + "session/session-v5.sqlite", "session", 5, "00000001-0000-7000-8000-000000000005"),
        new(SchemaMigrationPrefix + "session/session-v6.sqlite", "session", 6, "00000001-0000-7000-8000-000000000006"),
        new(SchemaMigrationPrefix + "session/session-v7.sqlite", "session", 7, "00000001-0000-7000-8000-000000000007"),
        new(SchemaMigrationPrefix + "session/session-v8.sqlite", "session", 8, "00000001-0000-7000-8000-000000000008"),
        new(SchemaMigrationPrefix + "session/session-v9.sqlite", "session", 9, "00000001-0000-7000-8000-000000000009"),
        new(SchemaMigrationPrefix + "session/session-v10.sqlite", "session", 10, "00000001-0000-7000-8000-000000000010"),
        new(SchemaMigrationPrefix + "session/session-v10-from-v4.sqlite", "session", 10, "00000001-0000-7000-8000-000000000004"),
        new(SchemaMigrationPrefix + "session/session-v10-from-v5.sqlite", "session", 10, "00000001-0000-7000-8000-000000000005"),
        new(SchemaMigrationPrefix + "session/session-v10-from-v6.sqlite", "session", 10, "00000001-0000-7000-8000-000000000006"),
        new(SchemaMigrationPrefix + "monitor/monitor-v1.sqlite", "monitor", 1, "fixture-monitor-v1-trace"),
        new(SchemaMigrationPrefix + "monitor/monitor-v2.sqlite", "monitor", 2, "fixture-monitor-v2-trace"),
        new(SchemaMigrationPrefix + "monitor/monitor-v3.sqlite", "monitor", 3, "fixture-monitor-v3-trace"),
        new(SchemaMigrationPrefix + "monitor/monitor-v4.sqlite", "monitor", 4, "fixture-monitor-v4-trace"),
        new(SchemaMigrationPrefix + "monitor/monitor-v5.sqlite", "monitor", 5, "fixture-monitor-v5-trace"),
        new(SchemaMigrationPrefix + "retention/retention-catalog-v1.sqlite", "retention-catalog", 1, "00000000000000000000000000000089"),
        new(SchemaMigrationPrefix + "retention/sensitive-bundle-v1/.retention-owner.json", "sensitive-bundle", 1, "synthetic-sensitive-bundle-v1"),
        new(SchemaMigrationPrefix + "retention/sensitive-bundle-v1/manifest.json", "sensitive-bundle", 1, "synthetic-sensitive-bundle-v1"),
        new(SchemaMigrationPrefix + "retention/sensitive-bundle-v1/evidence/evidence-0001.json", "sensitive-bundle", 1, "synthetic-evidence-0001"),
        new(SchemaMigrationPrefix + "retention/sdk-directory-v1/.retention-owner.json", "sdk-directory", 1, "synthetic-sdk-directory-v1"),
        new(SchemaMigrationPrefix + "retention/sdk-directory-v1/member-0001.dat", "sdk-directory", 1, "synthetic-sdk-member-v1\n"),
    ];

    private sealed record CloseoutCorpusManifest(int SchemaVersion, List<CloseoutCorpusEntry> Entries);
    private sealed record CloseoutCorpusEntry(string RelativePath, string Sha256, string FixtureKind, int FixtureVersion, string PreMigrationSentinel, string MigrationResult, string FirstFreshHostRestart, string SecondFreshHostRestart);
    private sealed record CorpusIdentity(string RelativePath, string FixtureKind, int FixtureVersion, string PreMigrationSentinel);
}
