using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.SanitizedExport;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SanitizedExportServiceTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_SameSnapshotAndSelection_ProducesIdenticalStrictArchive()
    {
        var service = new SanitizedExportService();
        var request = Request([RepositoryRecord("session-a")]);

        var first = service.Create(request);
        var second = service.Create(request);

        Assert.True(first.Success, first.ErrorCode);
        Assert.Equal(first.ArchiveBytes, second.ArchiveBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(first.ArchiveBytes!)).ToLowerInvariant(), first.ArchiveSha256);
        Assert.True(service.Inspect(first.ArchiveBytes!).Success);
        using var archive = new ZipArchive(new MemoryStream(first.ArchiveBytes!), ZipArchiveMode.Read);
        Assert.Equal(["manifest.json", "repository-metadata/session-a.json"], archive.Entries.Select(entry => entry.FullName));
        Assert.All(archive.Entries, entry => Assert.Equal(entry.Length, entry.CompressedLength));
    }

    [Fact]
    public void Create_PreservesExactIssue80CanonicalBytes()
    {
        var alert = AlertBytes();
        var result = new SanitizedExportService().Create(Request([AlertRecord(alert)]));

        Assert.True(result.Success, result.ErrorCode);
        using var archive = new ZipArchive(new MemoryStream(result.ArchiveBytes!), ZipArchiveMode.Read);
        Assert.Equal(alert, Read(archive.GetEntry("alert-receipts/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.json")!));
    }

    [Fact]
    public void Create_ManifestUsesIssue58LabelsStateAndRepeatableProofProfiles()
    {
        var request = Request([RepositoryRecord("session-a")]) with
        {
            Snapshot = Request([RepositoryRecord("session-a")]).Snapshot with
            {
                ProcessingVersions = new Dictionary<string, string> { ["normalization"] = "normalization.v3" },
            },
        };

        var result = new SanitizedExportService().Create(request);

        Assert.True(result.Success, result.ErrorCode);
        using var manifest = JsonDocument.Parse(result.ManifestBytes!);
        var root = manifest.RootElement;
        Assert.Equal("sample-repository", Assert.Single(root.GetProperty("source_labels").EnumerateArray()).GetProperty("repository_name").GetString());
        Assert.Equal(1, root.GetProperty("record_counts").GetProperty("repository_metadata_projection").GetInt32());
        Assert.Equal("sanitized-evidence-producers.v1", root.GetProperty("repository_safe_validation").GetProperty("producer_profile").GetString());
        Assert.Equal("repository-safe-scanner.v1", root.GetProperty("repository_safe_validation").GetProperty("scanner_profile").GetString());
        Assert.False(root.GetProperty("repository_safe_validation").TryGetProperty("markers", out _));
    }

    [Fact]
    public void Preview_ResolvesRequiredDependencyByExactIdentityAndReportsMissing()
    {
        var selected = RepositoryRecord("selected") with
        {
            Dependencies =
            [
                new("repository_metadata_projection", "dependency", SanitizedExportDependencyDisposition.Required),
                new("repository_metadata_projection", "missing", SanitizedExportDependencyDisposition.Required),
            ],
        };
        var request = Request([selected, RepositoryRecord("dependency")]) with { Selection = new(SessionIds: ["selected"]) };

        var preview = new SanitizedExportService().Preview(request);

        Assert.True(preview.Success, preview.ErrorCode);
        Assert.Equal(["dependency", "selected"], preview.Entries.Select(entry => entry.RecordId).Order(StringComparer.Ordinal));
        Assert.Equal("missing", Assert.Single(preview.UnresolvedDependencies).State);
    }

    [Theory]
    [InlineData("{\"prompt\":\"synthetic body\"}", "forbidden_field")]
    [InlineData("{\"value\":\"Authorization: Bearer synthetic-token\"}", "credential_pattern")]
    [InlineData("{\"value\":\"C:\\\\Users\\\\Example\\\\private.txt\"}", "local_path")]
    [InlineData("{\"contact\":\"person@example.invalid\"}", "pii_pattern")]
    public void Create_RejectsGenericScannerViolationsBeforeProducerAuthorization(string json, string expected)
    {
        var invalid = RepositoryRecord("session-a") with { CanonicalBytes = Encoding.UTF8.GetBytes(json) };

        var result = new SanitizedExportService().Create(Request([invalid]));

        Assert.Equal(expected, result.ErrorCode);
        Assert.Null(result.ArchiveBytes);
    }

    [Fact]
    public void Create_RejectsEveryIssue91MarkerTransformationCaseInsensitively()
    {
        var path = Path.Combine(FindRepositoryRoot(), "scripts", "validation", "issue-91", "fixtures", "secret-corpus.v1.json");
        using var corpus = JsonDocument.Parse(File.ReadAllBytes(path));
        var service = new SanitizedExportService();
        foreach (var item in corpus.RootElement.GetProperty("cases").EnumerateArray())
        {
            var marker = item.GetProperty("marker").GetString()!;
            foreach (var value in MarkerTransformations(marker.ToUpperInvariant()))
            {
                var record = RepositoryRecord("session-a") with { RepositoryName = value };
                record = record with { CanonicalBytes = RepositoryBytes(record) };
                Assert.False(service.Create(Request([record]) with { ForbiddenMarkers = [marker] }).Success);
            }
        }
    }

    [Fact]
    public void CreateAndPublish_FailureLeavesNoPartialOrReturnedBytes()
    {
        using var temp = new MonitorTempDirectory();
        var output = Path.Combine(temp.Path, "bundle.zip");
        File.WriteAllText(output, "existing");

        var result = new SanitizedExportService().CreateAndPublish(Request([RepositoryRecord("session-a")]), output);

        Assert.False(result.Success);
        Assert.Equal("output_exists", result.ErrorCode);
        Assert.Null(result.ArchiveBytes);
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.partial"));
    }

    [Fact]
    public void Inspect_RejectsCompressionNoncanonicalManifestAndExternalAttributes()
    {
        var service = new SanitizedExportService();
        var created = service.Create(Request([RepositoryRecord("session-a")]));
        Assert.True(created.Success, created.ErrorCode);

        Assert.Equal("compression_not_allowed", service.Inspect(Repack(created.ArchiveBytes!, CompressionLevel.SmallestSize)).ErrorCode);
        Assert.Equal("archive_attributes_invalid", service.Inspect(Repack(created.ArchiveBytes!, CompressionLevel.NoCompression, externalAttributes: 32)).ErrorCode);
        Assert.Equal("manifest_not_canonical", service.Inspect(Repack(created.ArchiveBytes!, CompressionLevel.NoCompression, manifestPrefix: " ")).ErrorCode);
    }

    [Fact]
    public void CanonicalSchemaAndIssue91HandoffFreezeClosedContract()
    {
        var root = FindRepositoryRoot();
        var contractRoot = Path.Combine(root, "docs", "specifications", "contracts", "sanitized-evidence", "v1");
        using var schema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(contractRoot, "manifest.schema.json")));
        using var handoff = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(contractRoot, "issue-91-validation-handoff.json")));
        Assert.Equal("sanitized-evidence-producers.v1", schema.RootElement.GetProperty("properties").GetProperty("repository_safe_validation").GetProperty("properties").GetProperty("producer_profile").GetProperty("const").GetString());
        Assert.Equal("blocked_owner_providers", handoff.RootElement.GetProperty("production_surface_state").GetString());
    }

    [Fact]
    public void GoldenBundleFixture_IsByteEquivalentAndInspectable()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "tests", "CopilotAgentObservability.LocalMonitor.Tests", "TestData", "SanitizedExport");
        var control = SanitizedExportJson.DeserializeControlRequest(File.ReadAllBytes(Path.Combine(directory, "sanitized-evidence.request.v1.json")));
        var snapshot = Request([RepositoryRecord("session-a")]).Snapshot with
        {
            SnapshotId = "snapshot-golden-85",
            ProcessingVersions = new Dictionary<string, string>
            {
                ["normalization"] = "normalization.v3",
                ["evaluation"] = "alert.evaluation.v1",
            },
        };
        var expected = File.ReadAllBytes(Path.Combine(directory, "sanitized-evidence.v1.zip"));
        var service = new SanitizedExportService();
        var created = new SanitizedExportAuthorizedService(new Provider(snapshot)).Create(control);

        Assert.True(created.Success, created.ErrorCode);
        Assert.Equal(expected, created.ArchiveBytes);
        Assert.True(service.Inspect(expected).Success);
    }

    private sealed class Provider(SanitizedExportSourceSnapshot snapshot) : ISanitizedExportSnapshotProvider
    {
        public SanitizedExportSnapshotCapture Capture() => new(true, null, snapshot);
    }

    private static SanitizedExportRequest Request(IReadOnlyList<SanitizedExportRecord> records)
    {
        var findings = records.Any(record => record.RecordType == "instruction_finding_handoff");
        var alerts = records.Any(record => record.RecordType == "alert_receipt");
        return new(CreatedAt, new("snapshot-85", "local-monitor-test", [new("github-copilot-cli", "1.0.73")], records,
            new(findings ? "available" : "missing", alerts ? "available" : "missing", "unavailable", "unavailable", "unavailable")), new(), []);
    }

    private static SanitizedExportRecord RepositoryRecord(string id)
    {
        var record = new SanitizedExportRecord($"repository-metadata/{id}.json", "repository_metadata_projection", id, id, "trace-a", "github-copilot-cli",
            "sample-repository", "sample-workspace", "sample-snapshot", CreatedAt, [], [], "partial", "not_captured", "retained_by_policy");
        return record with { CanonicalBytes = RepositoryBytes(record) };
    }

    private static byte[] RepositoryBytes(SanitizedExportRecord record) => Encoding.UTF8.GetBytes(
        $"{{\"schema_version\":\"repository-metadata-projection.v1\",\"record_id\":{JsonSerializer.Serialize(record.RecordId)},\"session_id\":{JsonSerializer.Serialize(record.SessionId)},\"trace_id\":{JsonSerializer.Serialize(record.TraceId)},\"source_surface\":{JsonSerializer.Serialize(record.SourceSurface)},\"repository_name\":{JsonSerializer.Serialize(record.RepositoryName)},\"workspace_label\":{JsonSerializer.Serialize(record.WorkspaceLabel)},\"repo_snapshot\":{JsonSerializer.Serialize(record.RepoSnapshot)},\"observed_at\":\"2026-07-22T00:00:00.0000000Z\",\"completeness\":{JsonSerializer.Serialize(record.Completeness)},\"content_state\":{JsonSerializer.Serialize(record.ContentState)},\"retention_state\":{JsonSerializer.Serialize(record.RetentionState)}}}");

    private static SanitizedExportRecord AlertRecord(byte[] bytes) => new(
        "alert-receipts/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.json", "alert_receipt",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "session-1", "trace-1", "github-copilot", null, null, null,
        new DateTimeOffset(2026, 7, 21, 1, 2, 4, TimeSpan.Zero), bytes, []);

    private static byte[] AlertBytes() => Trim(File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "tests", "CopilotAgentObservability.Alerts.Tests", "TestData", "alert-receipt-v1.golden.json")));
    private static byte[] Trim(byte[] bytes) => bytes is [.., (byte)'\n'] ? bytes[..^1] : bytes;
    private static byte[] Read(ZipArchiveEntry entry) { using var stream = entry.Open(); using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray(); }

    private static byte[] Repack(byte[] sourceBytes, CompressionLevel compression, int externalAttributes = 0, string manifestPrefix = "")
    {
        using var source = new ZipArchive(new MemoryStream(sourceBytes), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var sourceEntry in source.Entries)
            {
                var entry = target.CreateEntry(sourceEntry.FullName, compression); entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero); entry.ExternalAttributes = externalAttributes;
                var bytes = Read(sourceEntry); if (sourceEntry.FullName == "manifest.json") bytes = Encoding.UTF8.GetBytes(manifestPrefix + Encoding.UTF8.GetString(bytes));
                using var stream = entry.Open(); stream.Write(bytes);
            }
        return output.ToArray();
    }

    private static IEnumerable<string> MarkerTransformations(string marker)
    {
        yield return marker; yield return JsonSerializer.Serialize(marker)[1..^1]; yield return System.Net.WebUtility.HtmlEncode(marker);
        yield return Uri.EscapeDataString(marker); yield return Convert.ToBase64String(Encoding.UTF8.GetBytes(marker));
        yield return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(marker))).ToLowerInvariant()[..12];
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
}
