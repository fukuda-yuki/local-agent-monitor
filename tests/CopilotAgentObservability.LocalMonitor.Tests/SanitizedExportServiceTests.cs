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
    public void Create_SameSnapshotAndSelection_ProducesIdenticalArchiveBytes()
    {
        var service = new SanitizedExportService();
        var request = Request(Records(
            Record("sessions/session-a.json", "session_projection", "session-a", "{\"schema_version\":\"session-workspace.v1\",\"session_id\":\"session-a\"}"),
            Record("receipts/finding-a.json", "instruction_finding_handoff", "finding-a", "{\"schema_version\":\"instruction-finding-handoff.v1\",\"analysis_run_id\":7,\"findings\":[],\"candidates\":[]}")));

        var first = service.Create(request);
        var second = service.Create(request);

        Assert.True(first.Success, first.ErrorCode);
        Assert.Equal(first.ArchiveSha256, second.ArchiveSha256);
        Assert.Equal(first.ArchiveBytes, second.ArchiveBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(first.ArchiveBytes!)).ToLowerInvariant(), first.ArchiveSha256);
    }

    [Fact]
    public void Create_UsesStoreOnlyFixedTimestampOrdinalEntriesAndExactPayloadChecksums()
    {
        var service = new SanitizedExportService();
        var alertBytes = Encoding.UTF8.GetBytes("{\"schema_version\":\"alert.receipt.v1\",\"sanitized_export_profile\":\"sanitized-alert-receipt.v1\"}");
        var result = service.Create(Request(Records(
            Record("receipts/z-alert.json", "alert_receipt", "alert-z", alertBytes),
            Record("sessions/a-session.json", "session_projection", "session-a", "{\"schema_version\":\"session-workspace.v1\"}"))));

        Assert.True(result.Success, result.ErrorCode);
        using var archive = new ZipArchive(new MemoryStream(result.ArchiveBytes!), ZipArchiveMode.Read);
        Assert.Equal(["manifest.json", "receipts/z-alert.json", "sessions/a-session.json"], archive.Entries.Select(entry => entry.FullName));
        Assert.All(archive.Entries, entry => Assert.Equal(new DateTime(1980, 1, 1, 0, 0, 0), entry.LastWriteTime.DateTime));
        Assert.Equal(alertBytes, Read(archive.GetEntry("receipts/z-alert.json")!));

        using var manifest = JsonDocument.Parse(Read(archive.GetEntry("manifest.json")!));
        var files = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(2, files.Length);
        Assert.DoesNotContain(files, file => file.GetProperty("path").GetString() == "manifest.json");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(alertBytes)).ToLowerInvariant(), files[0].GetProperty("sha256").GetString());
        Assert.All(archive.Entries, entry => Assert.Equal(entry.Length, entry.CompressedLength));
    }

    [Fact]
    public void Create_ManifestUsesExactIssue58LabelsAndStateDistributions()
    {
        var request = Request(Records(Record(
            "sessions/session-a.json",
            "session_projection",
            "session-a",
            "{\"schema_version\":\"session-workspace.v1\"}"))) with
        {
            Snapshot = Request([]).Snapshot with
            {
                Records =
                [
                    Record("sessions/session-a.json", "session_projection", "session-a", "{\"schema_version\":\"session-workspace.v1\"}") with
                    {
                        Completeness = "partial",
                        ContentState = "not_captured",
                        RetentionState = "retained_by_policy",
                    },
                ],
                ProcessingVersions = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["normalization"] = "normalization.v3",
                    ["evaluation"] = "alert.evaluation.v1",
                },
            },
        };

        var result = new SanitizedExportService().Create(request);

        Assert.True(result.Success, result.ErrorCode);
        using var manifest = JsonDocument.Parse(result.ManifestBytes!);
        var root = manifest.RootElement;
        Assert.Equal("2026-07-22T00:00:00.0000000Z", root.GetProperty("date_range").GetProperty("start").GetString());
        var label = Assert.Single(root.GetProperty("source_labels").EnumerateArray());
        Assert.Equal("sample-repository", label.GetProperty("repository_name").GetString());
        Assert.Equal("sample-workspace", label.GetProperty("workspace_label").GetString());
        Assert.Equal("sample-snapshot", label.GetProperty("repo_snapshot").GetString());
        Assert.Equal(1, root.GetProperty("completeness_distribution").GetProperty("partial").GetInt32());
        Assert.Equal(1, root.GetProperty("content_state_distribution").GetProperty("not_captured").GetInt32());
        Assert.Equal(1, root.GetProperty("retention_state_distribution").GetProperty("retained_by_policy").GetInt32());
        Assert.Equal("normalization.v3", root.GetProperty("processing_versions").GetProperty("normalization").GetString());
        Assert.Equal("unavailable", root.GetProperty("capabilities").GetProperty("historical_instruction_analysis").GetString());
        Assert.Equal("unavailable", root.GetProperty("capabilities").GetProperty("historical_efficiency_analysis").GetString());
        Assert.Equal("unavailable", root.GetProperty("capabilities").GetProperty("alert_center").GetString());
    }

    [Fact]
    public void Create_PreservesExactIssue59AndIssue80CanonicalBytes()
    {
        var root = FindRepositoryRoot();
        var findingBytes = File.ReadAllBytes(Path.Combine(root, "tests", "CopilotAgentObservability.LocalMonitor.Tests", "TestData", "InstructionFindings", "instruction-finding-handoff.v1.json"));
        var alertBytes = File.ReadAllBytes(Path.Combine(root, "tests", "CopilotAgentObservability.Alerts.Tests", "TestData", "alert-receipt-v1.golden.json"));
        var result = new SanitizedExportService().Create(Request(Records(
            Record("receipts/instruction-finding-handoff.json", "instruction_finding_handoff", "finding-handoff", findingBytes),
            Record("receipts/alert-receipt.json", "alert_receipt", "alert-receipt", alertBytes))));

        Assert.True(result.Success, result.ErrorCode);
        using var archive = new ZipArchive(new MemoryStream(result.ArchiveBytes!), ZipArchiveMode.Read);
        Assert.Equal(findingBytes, Read(archive.GetEntry("receipts/instruction-finding-handoff.json")!));
        Assert.Equal(alertBytes, Read(archive.GetEntry("receipts/alert-receipt.json")!));
    }

    [Fact]
    public void Preview_ResolvesDependenciesByExactTypeAndIdAndReportsMissingWithoutInference()
    {
        var service = new SanitizedExportService();
        var session = Record(
            "sessions/session-a.json",
            "session_projection",
            "session-a",
            "{\"schema_version\":\"session-workspace.v1\"}",
            dependencies:
            [
                new("instruction_finding_handoff", "finding-a", SanitizedExportDependencyDisposition.Required),
                new("driver_receipt", "driver-absent", SanitizedExportDependencyDisposition.External),
                new("candidate_receipt", "candidate-absent", SanitizedExportDependencyDisposition.Required),
            ]);
        var finding = Record("receipts/finding-a.json", "instruction_finding_handoff", "finding-a", "{\"schema_version\":\"instruction-finding-handoff.v1\"}");
        var request = Request(Records(session, finding)) with
        {
            Selection = new(SessionIds: ["session-a"]),
        };

        var preview = service.Preview(request);

        Assert.True(preview.Success, preview.ErrorCode);
        Assert.Equal(["receipts/finding-a.json", "sessions/session-a.json"], preview.Entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).Select(entry => entry.Path));
        Assert.Collection(
            preview.UnresolvedDependencies,
            dependency =>
            {
                Assert.Equal("candidate_receipt", dependency.RecordType);
                Assert.Equal("candidate-absent", dependency.RecordId);
                Assert.Equal("missing", dependency.State);
            },
            dependency =>
            {
                Assert.Equal("driver_receipt", dependency.RecordType);
                Assert.Equal("driver-absent", dependency.RecordId);
                Assert.Equal("external", dependency.State);
            });
    }

    [Fact]
    public void Preview_RequiredMissingDependencyDominatesExternalReferences()
    {
        var dependency = new SanitizedExportDependency("candidate_receipt", "candidate-absent", SanitizedExportDependencyDisposition.External);
        var requiredDependency = dependency with { Disposition = SanitizedExportDependencyDisposition.Required };
        var request = Request(Records(
            Record("sessions/session-a.json", "session_projection", "session-a", "{}", dependencies: [requiredDependency]),
            Record("sessions/session-b.json", "session_projection", "session-b", "{}", dependencies: [dependency]))) with
        {
            Selection = new(SessionIds: ["session-a", "session-b"]),
        };

        var missing = Assert.Single(new SanitizedExportService().Preview(request).UnresolvedDependencies);

        Assert.Equal("missing", missing.State);
    }

    [Fact]
    public void Create_DuplicatePathsOutsideSelectedClosureDoNotAffectTheBundle()
    {
        var request = Request(Records(
            Record("sessions/session-a.json", "session_projection", "session-a", "{}"),
            Record("receipts/duplicate.json", "candidate_receipt", "candidate-b", "{}", sessionId: "session-b"),
            Record("receipts/duplicate.json", "driver_receipt", "driver-c", "{}", sessionId: "session-c"))) with
        {
            Selection = new(SessionIds: ["session-a"]),
        };

        var result = new SanitizedExportService().Create(request);

        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal(["sessions/session-a.json"], result.Preview.Entries.Select(entry => entry.Path));
    }

    [Theory]
    [InlineData("{\"prompt\":\"synthetic body\"}", "forbidden_field")]
    [InlineData("{\"tool_input\":\"synthetic body\"}", "forbidden_field")]
    [InlineData("{\"file_content\":\"synthetic body\"}", "forbidden_field")]
    [InlineData("{\"user_id\":\"synthetic identity\"}", "forbidden_field")]
    [InlineData("{\"value\":\"Authorization: Bearer synthetic-token\"}", "credential_pattern")]
    [InlineData("{\"email\":\"person@example.invalid\"}", "forbidden_field")]
    [InlineData("{\"value\":\"C:\\\\Users\\\\Example\\\\private.txt\"}", "local_path")]
    public void Create_RejectsForbiddenContentBeforeArchiveSuccess(string json, string expectedCode)
    {
        var service = new SanitizedExportService();

        var result = service.Create(Request(Records(Record("sessions/session-a.json", "session_projection", "session-a", json))));

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Null(result.ArchiveBytes);
        Assert.Null(result.ArchiveSha256);
    }

    [Theory]
    [InlineData("{\"value\":\"ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789\"}", "credential_pattern")]
    [InlineData("{\"value\":\"D:\\\\projects\\\\private.txt\"}", "local_path")]
    public void Create_RejectsKnownTokenAndAbsolutePathPatterns(string json, string expectedCode)
    {
        var result = new SanitizedExportService().Create(Request(Records(
            Record("sessions/session-a.json", "session_projection", "session-a", json))));

        Assert.Equal(expectedCode, result.ErrorCode);
    }

    [Theory]
    [InlineData("C:/Users/Example/private.txt")]
    [InlineData("D:/projects/private.txt")]
    [InlineData("\\\\server\\share\\private.txt")]
    [InlineData("\\\\?\\C:\\Users\\Example\\private.txt")]
    [InlineData("/mnt/c/Users/example/private.txt")]
    [InlineData("/home/example/private.txt")]
    [InlineData("file:///C:/Users/Example/private.txt")]
    public void Create_RejectsCanonicalWindowsUnixAndWslAbsolutePathVariants(string path)
    {
        var json = JsonSerializer.Serialize(new { value = path });

        var result = new SanitizedExportService().Create(Request(Records(
            Record("sessions/session-a.json", "session_projection", "session-a", json))));

        Assert.Equal("local_path", result.ErrorCode);
    }

    [Theory]
    [InlineData("Server=synthetic.invalid;Password=synthetic-password")]
    [InlineData("Authorization: Basic c3ludGhldGljOnZhbHVl")]
    [InlineData("x-api-key: synthetic-key-value")]
    [InlineData("client_secret=synthetic-secret-value")]
    [InlineData("AKIA1234567890ABCDEF")]
    [InlineData("-----BEGIN CERTIFICATE-----")]
    public void Create_RejectsBoundedCredentialAndCertificatePatterns(string value)
    {
        var json = JsonSerializer.Serialize(new { value });

        var result = new SanitizedExportService().Create(Request(Records(
            Record("sessions/session-a.json", "session_projection", "session-a", json))));

        Assert.Equal("credential_pattern", result.ErrorCode);
    }

    [Fact]
    public void Create_RejectsEveryIssue91CorpusMarkerAcrossDeclaredTransformations()
    {
        var corpusPath = Path.Combine(FindRepositoryRoot(), "scripts", "validation", "issue-91", "fixtures", "secret-corpus.v1.json");
        using var corpus = JsonDocument.Parse(File.ReadAllBytes(corpusPath));
        var service = new SanitizedExportService();

        foreach (var item in corpus.RootElement.GetProperty("cases").EnumerateArray())
        {
            var marker = item.GetProperty("marker").GetString()!;
            foreach (var transformedMarker in MarkerTransformations(marker))
            {
                var request = Request(Records(Record(
                    "sessions/session-a.json",
                    "session_projection",
                    "session-a",
                    JsonSerializer.Serialize(new { summary = transformedMarker })))) with
                {
                    ForbiddenMarkers = [marker],
                };

                Assert.False(service.Create(request).Success);
            }
        }
    }

    [Fact]
    public void Create_RejectsCompletedManifestThatFailsRepositorySafeInspectionWithoutReturningBytes()
    {
        var request = Request(Records(Record(
            "sessions/session-a.json", "session_projection", "session-a", "{}"))) with
        {
            Snapshot = Request(Records(Record(
                "sessions/session-a.json", "session_projection", "session-a", "{}"))).Snapshot with
            {
                ProcessingVersions = new Dictionary<string, string> { ["secret"] = "version-one" },
            },
        };

        var result = new SanitizedExportService().Create(request);

        Assert.False(result.Success);
        Assert.Equal("forbidden_field", result.ErrorCode);
        Assert.Null(result.ManifestBytes);
        Assert.Null(result.ArchiveBytes);
        Assert.Null(result.ArchiveSha256);
    }

    [Fact]
    public void CreateAndPublish_InvalidOutputPathReturnsFixedFailureWithoutBytesOrPartialArtifact()
    {
        var request = Request(Records(Record(
            "sessions/session-a.json", "session_projection", "session-a", "{}")));

        var result = new SanitizedExportService().CreateAndPublish(request, "invalid\0bundle.zip");

        Assert.False(result.Success);
        Assert.Equal("publish_failed", result.ErrorCode);
        Assert.Null(result.ManifestBytes);
        Assert.Null(result.ArchiveBytes);
        Assert.Null(result.ArchiveSha256);
    }

    [Fact]
    public void Create_RejectsInvalidUtf8InNonJsonPayload()
    {
        var bytes = new byte[] { 0x66, 0x6f, 0x80, 0x6f };

        var result = new SanitizedExportService().Create(Request(Records(
            Record("measurements/data.csv", "measurement_dataset", "measurement-a", bytes))));

        Assert.Equal("invalid_canonical_content", result.ErrorCode);
    }

    [Fact]
    public void Create_RejectsCallerSuppliedSourceBodyMarkerAndUnexpectedEntryPath()
    {
        var service = new SanitizedExportService();
        var markerRequest = Request(Records(Record("sessions/session-a.json", "session_projection", "session-a", "{\"summary\":\"source-fragment-85\"}"))) with
        {
            ForbiddenMarkers = ["source-fragment-85"],
        };

        Assert.Equal("forbidden_marker", service.Create(markerRequest).ErrorCode);
        Assert.Equal(
            "unexpected_entry",
            service.Create(Request(Records(Record("raw/payload.json", "session_projection", "session-a", "{}")))).ErrorCode);
    }

    [Theory]
    [InlineData("c291cmNlLWZyYWdtZW50LTg1")]
    [InlineData("source-fragment-85")]
    [InlineData("a8507c7e2cbf")]
    public void Create_RejectsSupportedMarkerTransformations(string transformedMarker)
    {
        var request = Request(Records(Record("sessions/session-a.json", "session_projection", "session-a", $"{{\"summary\":\"{transformedMarker}\"}}"))) with
        {
            ForbiddenMarkers = ["source-fragment-85"],
        };

        Assert.Equal("forbidden_marker", new SanitizedExportService().Create(request).ErrorCode);
    }

    [Theory]
    [InlineData("{\"contact\":\"person@example.invalid\"}", "pii_pattern")]
    [InlineData("prompt,value\\nblocked,1", "forbidden_field")]
    public void Create_RejectsPiiValuesAndRawFieldNamesOutsideJsonProperties(string content, string expectedCode)
    {
        var path = content.StartsWith("prompt", StringComparison.Ordinal) ? "measurements/data.csv" : "sessions/session-a.json";
        var type = path.StartsWith("measurements", StringComparison.Ordinal) ? "measurement_dataset" : "session_projection";
        Assert.Equal(expectedCode, new SanitizedExportService().Create(Request(Records(Record(path, type, "record-a", content)))).ErrorCode);
    }

    [Fact]
    public void GoldenBundleFixture_IsByteEquivalentAndInspectable()
    {
        var root = FindRepositoryRoot();
        var fixtureDirectory = Path.Combine(root, "tests", "CopilotAgentObservability.LocalMonitor.Tests", "TestData", "SanitizedExport");
        var request = SanitizedExportJson.DeserializeRequest(File.ReadAllBytes(Path.Combine(fixtureDirectory, "sanitized-evidence.request.v1.json")));
        var expectedArchive = File.ReadAllBytes(Path.Combine(fixtureDirectory, "sanitized-evidence.v1.zip"));
        var service = new SanitizedExportService();

        var created = service.Create(request);
        var inspected = service.Inspect(expectedArchive);

        Assert.True(created.Success, created.ErrorCode);
        Assert.True(inspected.Success, inspected.ErrorCode);
        Assert.Equal(expectedArchive, created.ArchiveBytes);
        Assert.Equal(created.ArchiveSha256, inspected.ArchiveSha256);
    }

    [Fact]
    public void CanonicalSchemaAndIssue91HandoffFreezeConsumerContractWithoutEditingSharedRegistry()
    {
        var root = FindRepositoryRoot();
        var contractRoot = Path.Combine(root, "docs", "specifications", "contracts", "sanitized-evidence", "v1");
        using var schema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(contractRoot, "manifest.schema.json")));
        using var handoff = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(contractRoot, "issue-91-validation-handoff.json")));
        using var registry = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "docs", "specifications", "contracts", "validation-matrix", "v1", "future-surface-registry.json")));

        Assert.Equal("sanitized-evidence-manifest.v1", schema.RootElement.GetProperty("properties").GetProperty("schema_version").GetProperty("const").GetString());
        Assert.Equal(255, schema.RootElement.GetProperty("properties").GetProperty("files").GetProperty("maxItems").GetInt32());
        Assert.Equal("not_available", handoff.RootElement.GetProperty("future_registry_state_at_kickoff").GetString());
        Assert.Equal("active", handoff.RootElement.GetProperty("production_surface_state").GetString());
        Assert.False(handoff.RootElement.TryGetProperty("requested_registry_state", out _));
        Assert.Equal(
            "not_available",
            Assert.Single(registry.RootElement.GetProperty("entries").EnumerateArray(), entry => entry.GetProperty("owner_issue").GetInt32() == 85)
                .GetProperty("state").GetString());
    }

    [Fact]
    public void Preview_RejectsMoreThan256ArchiveEntriesAndMoreThan128MiBUncompressed()
    {
        var service = new SanitizedExportService();
        var tooMany = Enumerable.Range(0, 256)
            .Select(index => Record($"sessions/{index:D3}.json", "session_projection", $"session-{index:D3}", "{}"))
            .ToArray();
        var oversized = new byte[SanitizedExportLimits.MaximumUncompressedBytes];

        Assert.Equal("entry_limit_exceeded", service.Preview(Request(Records(tooMany))).ErrorCode);
        Assert.Equal(
            "uncompressed_size_limit_exceeded",
            service.Preview(Request(Records(Record("sessions/large.json", "session_projection", "session-large", oversized)))).ErrorCode);
    }

    [Fact]
    public void Preview_RejectsUnknownCapabilityState()
    {
        var request = Request(Records(Record("sessions/session-a.json", "session_projection", "session-a", "{}"))) with
        {
            Snapshot = Request([]).Snapshot with
            {
                Records = [Record("sessions/session-a.json", "session_projection", "session-a", "{}")],
                Capabilities = new("safe", "missing", "unavailable", "unavailable", "unavailable"),
            },
        };

        Assert.Equal("invalid_capability_state", new SanitizedExportService().Preview(request).ErrorCode);
    }

    [Theory]
    [InlineData("person@example.invalid", "sample-workspace", "pii_pattern")]
    [InlineData("sample-repository", "C:\\Users\\Example\\private", "local_path")]
    [InlineData("source-fragment-85", "sample-workspace", "forbidden_marker")]
    public void Preview_ScansManifestMetadataBeforeSuccess(string repositoryName, string workspaceLabel, string expectedCode)
    {
        var record = Record("sessions/session-a.json", "session_projection", "session-a", "{}") with
        {
            RepositoryName = repositoryName,
            WorkspaceLabel = workspaceLabel,
        };
        var request = Request(Records(record)) with { ForbiddenMarkers = ["source-fragment-85"] };

        Assert.Equal(expectedCode, new SanitizedExportService().Preview(request).ErrorCode);
    }

    [Fact]
    public void AtomicPublisher_ScannerFailureLeavesNoFinalOrPartialArtifact()
    {
        var service = new SanitizedExportService();
        using var temp = new MonitorTempDirectory();
        var outputPath = Path.Combine(temp.Path, "bundle.zip");
        var request = Request(Records(Record("sessions/session-a.json", "session_projection", "session-a", "{\"prompt\":\"blocked\"}")));

        var result = service.CreateAndPublish(request, outputPath);

        Assert.False(result.Success);
        Assert.False(File.Exists(outputPath));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.partial", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Inspect_RejectsCompressedAndNonOrdinalPayloadEntries()
    {
        var service = new SanitizedExportService();
        var created = service.Create(Request(Records(
            Record("receipts/alert.json", "alert_receipt", "alert-a", "{\"schema_version\":\"alert.receipt.v1\",\"sanitized_export_profile\":\"sanitized-alert-receipt.v1\"}"),
            Record("sessions/session.json", "session_projection", "session-a", "{\"schema_version\":\"session-workspace.v1\"}"))));
        Assert.True(created.Success, created.ErrorCode);

        var compressed = Repack(created, CompressionLevel.SmallestSize, reversePayloads: false);
        var nonOrdinal = Repack(created, CompressionLevel.NoCompression, reversePayloads: true);

        Assert.Equal("compression_not_allowed", service.Inspect(compressed).ErrorCode);
        Assert.Equal("entry_order_invalid", service.Inspect(nonOrdinal).ErrorCode);
    }

    [Fact]
    public void Inspect_RejectsChangedFrozenManifestAndExternalAttributes()
    {
        var service = new SanitizedExportService();
        var created = service.Create(Request(Records(
            Record("sessions/session.json", "session_projection", "session-a", "{}"))));
        Assert.True(created.Success, created.ErrorCode);

        var changedManifest = RepackWithManifestReplacement(created, "\"checksum\":\"sha256.v1\"", "\"checksum\":\"sha256.v2\"");
        var externalAttributes = Repack(created, CompressionLevel.NoCompression, reversePayloads: false, externalAttributes: 32);

        Assert.Equal("schema_unsupported", service.Inspect(changedManifest).ErrorCode);
        Assert.Equal("archive_attributes_invalid", service.Inspect(externalAttributes).ErrorCode);
    }

    [Fact]
    public void Inspect_RejectsNoncanonicalManifestWhitespaceAndDeflateMethodEvenWhenStoredSizesMatch()
    {
        var service = new SanitizedExportService();
        var created = service.Create(Request(Records(
            Record("sessions/session.json", "session_projection", "session-a", "{}"))));
        Assert.True(created.Success, created.ErrorCode);

        var noncanonicalManifest = RepackWithManifestReplacement(created, "{", "{ ");
        var deflateMethodWithStoredBytes = ChangeZipCompressionMethod(created.ArchiveBytes!, method: 8);

        Assert.Equal("manifest_not_canonical", service.Inspect(noncanonicalManifest).ErrorCode);
        Assert.Equal("compression_not_allowed", service.Inspect(deflateMethodWithStoredBytes).ErrorCode);
    }

    private static SanitizedExportRequest Request(IReadOnlyList<SanitizedExportRecord> records) => new(
        CreatedAt,
        new SanitizedExportSourceSnapshot(
            "snapshot-85",
            "local-monitor-test",
            [new("github-copilot-cli", "1.0.73")],
            records,
            new SanitizedExportCapabilityStates(
                InstructionFindings: "available",
                AlertReceipts: "available",
                HistoricalInstructionAnalysis: "unavailable",
                HistoricalEfficiencyAnalysis: "unavailable",
                AlertCenter: "unavailable")),
        new SanitizedExportSelection(),
        []);

    private static IReadOnlyList<SanitizedExportRecord> Records(params SanitizedExportRecord[] records) => records;

    private static SanitizedExportRecord Record(
        string path,
        string type,
        string id,
        string json,
        string? sessionId = "session-a",
        IReadOnlyList<SanitizedExportDependency>? dependencies = null) =>
        Record(path, type, id, Encoding.UTF8.GetBytes(json), sessionId, dependencies);

    private static SanitizedExportRecord Record(
        string path,
        string type,
        string id,
        byte[] bytes,
        string? sessionId = "session-a",
        IReadOnlyList<SanitizedExportDependency>? dependencies = null) => new(
            path,
            type,
            id,
            sessionId,
            "trace-a",
            "github-copilot-cli",
            "sample-repository",
            "sample-workspace",
            "sample-snapshot",
            CreatedAt,
            bytes,
            dependencies ?? []);

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static byte[] Repack(SanitizedExportResult result, CompressionLevel compression, bool reversePayloads, int externalAttributes = 0)
    {
        using var source = new ZipArchive(new MemoryStream(result.ArchiveBytes!), ZipArchiveMode.Read);
        var entries = source.Entries.Skip(1).ToArray();
        if (reversePayloads) Array.Reverse(entries);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries.Take(1).Concat(entries))
            {
                var copy = archive.CreateEntry(entry.FullName, compression);
                copy.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                copy.ExternalAttributes = externalAttributes;
                using var stream = copy.Open();
                stream.Write(Read(entry));
            }
        }
        return output.ToArray();
    }

    private static byte[] RepackWithManifestReplacement(SanitizedExportResult result, string oldValue, string newValue)
    {
        using var source = new ZipArchive(new MemoryStream(result.ArchiveBytes!), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                var bytes = Read(entry);
                if (entry.FullName == "manifest.json")
                    bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(oldValue, newValue, StringComparison.Ordinal));
                var copy = archive.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                copy.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                copy.ExternalAttributes = 0;
                using var stream = copy.Open();
                stream.Write(bytes);
            }
        }
        return output.ToArray();
    }

    private static byte[] ChangeZipCompressionMethod(byte[] source, byte method)
    {
        var result = source.ToArray();
        for (var index = 0; index <= result.Length - 12; index++)
        {
            if (result[index] == 0x50 && result[index + 1] == 0x4b && result[index + 2] == 0x03 && result[index + 3] == 0x04)
                result[index + 8] = method;
            if (result[index] == 0x50 && result[index + 1] == 0x4b && result[index + 2] == 0x01 && result[index + 3] == 0x02)
                result[index + 10] = method;
        }
        return result;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }

    private static IEnumerable<string> MarkerTransformations(string marker)
    {
        yield return marker;
        yield return JsonSerializer.Serialize(marker)[1..^1];
        yield return System.Net.WebUtility.HtmlEncode(marker);
        yield return Uri.EscapeDataString(marker);
        yield return Convert.ToBase64String(Encoding.UTF8.GetBytes(marker));
        yield return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(marker))).ToLowerInvariant()[..12];
    }
}
