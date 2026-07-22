using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.SanitizedExport;
using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SanitizedExportAuthorizationTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("session_projection")]
    [InlineData("measurement_dataset")]
    [InlineData("historical_instruction_analysis")]
    [InlineData("dashboard_dataset")]
    public void PreviewAndCreate_RejectUnknownAndFutureRecordTypes(string recordType)
    {
        var request = Request([Record("records/arbitrary.json", recordType, "arbitrary", Encoding.UTF8.GetBytes("{}"))]);
        var service = new SanitizedExportService();

        Assert.Equal("unsupported_record_type", service.Preview(request).ErrorCode);
        Assert.Equal("unsupported_record_type", service.Create(request).ErrorCode);
    }

    [Theory]
    [InlineData("alert.receipt.v2", "sanitized-alert-receipt.v1")]
    [InlineData("alert.receipt.v1", "sanitized-alert-receipt.v2")]
    public void PreviewAndCreate_RejectUnknownAlertVersionOrProfile(string version, string profile)
    {
        var bytes = AlertBytes();
        using var document = JsonDocument.Parse(bytes);
        var changed = Encoding.UTF8.GetBytes(bytes.AsSpan().SequenceEqual(bytes)
            ? Encoding.UTF8.GetString(bytes)
                .Replace("alert.receipt.v1", version, StringComparison.Ordinal)
                .Replace("sanitized-alert-receipt.v1", profile, StringComparison.Ordinal)
            : throw new InvalidOperationException());
        var request = Request([AlertRecord(AlertBytes()) with { CanonicalBytes = changed }]);
        var service = new SanitizedExportService();

        Assert.Equal("producer_contract_invalid", service.Preview(request).ErrorCode);
        Assert.Equal("producer_contract_invalid", service.Create(request).ErrorCode);
    }

    [Fact]
    public void PreviewAndCreate_RejectAlertEnvelopeContradiction()
    {
        var request = Request([AlertRecord(AlertBytes()) with { SessionId = "contradictory-session" }]);
        var service = new SanitizedExportService();

        Assert.Equal("producer_envelope_mismatch", service.Preview(request).ErrorCode);
        Assert.Equal("producer_envelope_mismatch", service.Create(request).ErrorCode);
    }

    [Theory]
    [InlineData("sample-repository", "sample\\u002drepository")]
    [InlineData("sample-workspace", "\\u0073ample-workspace")]
    public void Inspect_RejectsSemanticallyEquivalentNoncanonicalRepositoryEscapes(string canonical, string alternate)
    {
        var record = RepositoryRecord("first");
        record = record with { CanonicalBytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(record.CanonicalBytes).Replace(canonical, alternate, StringComparison.Ordinal)) };

        Assert.Equal("producer_contract_invalid", new SanitizedExportService().Preview(Request([record])).ErrorCode);
    }

    [Fact]
    public void Preview_UsesSharedInstructionValidatorAndRejectsTampering()
    {
        var bytes = InstructionBytes();
        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes)
            .Replace("instruction-finding-a54798d971cf0ee972a831fd", "instruction-finding-000000000000000000000000", StringComparison.Ordinal));
        var request = Request([InstructionRecord(changed)]);

        Assert.Equal("producer_contract_invalid", new SanitizedExportService().Preview(request).ErrorCode);
    }

    [Fact]
    public void AuthorizedCreate_CapturesOwnerSnapshotExactlyOnceAndUsesOwnerBytes()
    {
        var ownerRecord = RepositoryRecord("owner");
        var provider = new CountingProvider(Request([ownerRecord]).Snapshot);
        var control = new SanitizedExportControlRequest("sanitized-export-control.v1", ObservedAt, new(SessionIds: ["owner"]));

        var created = new SanitizedExportAuthorizedService(provider).Create(control);

        Assert.True(created.Success, created.ErrorCode);
        Assert.Equal(1, provider.CaptureCount);
        using var archive = new ZipArchive(new MemoryStream(created.ArchiveBytes!), ZipArchiveMode.Read);
        using var stream = archive.GetEntry(ownerRecord.EntryPath)!.Open();
        using var output = new MemoryStream();
        stream.CopyTo(output);
        Assert.Equal(ownerRecord.CanonicalBytes, output.ToArray());
    }

    [Fact]
    public void DeserializeControlRequest_RequiresCompleteSelectionShape()
    {
        var incomplete = Encoding.UTF8.GetBytes("""
            {"schema_version":"sanitized-export-control.v1","created_at":"2026-07-22T00:00:00.0000000Z","selection":{}}
            """);
        var complete = new SanitizedExportControlRequest(
            SanitizedExportContractVersions.ControlRequest,
            ObservedAt,
            new());

        Assert.Throws<JsonException>(() => SanitizedExportJson.DeserializeControlRequest(incomplete));
        Assert.Equal(complete, SanitizedExportJson.DeserializeControlRequest(SanitizedExportJson.SerializeControlRequest(complete)));
    }

    [Fact]
    public void ControlRequestJson_WritesAndAcceptsOnlyCanonicalUtcTimestampLexemes()
    {
        var control = new SanitizedExportControlRequest(
            SanitizedExportContractVersions.ControlRequest,
            ObservedAt,
            new(StartInclusive: ObservedAt, EndExclusive: ObservedAt.AddHours(1)));

        var bytes = SanitizedExportJson.SerializeControlRequest(control);

        var json = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"created_at\":\"2026-07-22T00:00:00.0000000Z\"", json, StringComparison.Ordinal);
        Assert.Contains("\"start_inclusive\":\"2026-07-22T00:00:00.0000000Z\"", json, StringComparison.Ordinal);
        Assert.Contains("\"end_exclusive\":\"2026-07-22T01:00:00.0000000Z\"", json, StringComparison.Ordinal);
        Assert.Equal(control, SanitizedExportJson.DeserializeControlRequest(bytes));
    }

    [Theory]
    [InlineData("created_at", "2026-07-22T00:00:00+00:00")]
    [InlineData("created_at", "2026-07-22T00:00:00Z")]
    [InlineData("created_at", "2026-07-22T00:00:00.123Z")]
    [InlineData("created_at", "2026-07-22T00:00:00.12345678Z")]
    [InlineData("start_inclusive", "2026-07-22T00:00:00+00:00")]
    [InlineData("end_exclusive", "2026-07-22T01:00:00.123Z")]
    public void InvalidControlTimestampLexemes_FailBeforeSnapshotCapture(string field, string value)
    {
        var provider = new CountingProvider(Request([RepositoryRecord("owner")]).Snapshot);
        var service = new SanitizedExportAuthorizedService(provider);
        var bytes = field switch
        {
            "start_inclusive" => ControlJson("2026-07-22T00:00:00.0000000Z", value, null),
            "end_exclusive" => ControlJson("2026-07-22T00:00:00.0000000Z", null, value),
            _ => ControlJson(value, null, null),
        };

        var exception = Xunit.Record.Exception(() => service.Preview(SanitizedExportJson.DeserializeControlRequest(bytes)));

        Assert.IsType<JsonException>(exception);
        Assert.Equal(0, provider.CaptureCount);
    }

    [Fact]
    public void DeserializeControlRequest_EnforcesOneMiBBeforeMaterialization()
    {
        var valid = ControlJson("2026-07-22T00:00:00.0000000Z", null, null);
        var maximum = new byte[SanitizedExportLimits.MaximumControlRequestBytes];
        valid.CopyTo(maximum, 0);
        maximum.AsSpan(valid.Length).Fill((byte)' ');
        var oversized = new byte[maximum.Length + 1];
        maximum.CopyTo(oversized, 0);
        oversized[^1] = (byte)' ';
        var provider = new CountingProvider(Request([RepositoryRecord("owner")]).Snapshot);
        var service = new SanitizedExportAuthorizedService(provider);

        Assert.Equal(SanitizedExportContractVersions.ControlRequest, SanitizedExportJson.DeserializeControlRequest(maximum).SchemaVersion);
        var exception = Xunit.Record.Exception(() => service.Preview(SanitizedExportJson.DeserializeControlRequest(oversized)));

        Assert.IsType<JsonException>(exception);
        Assert.Equal(0, provider.CaptureCount);
    }

    [Fact]
    public void AuthorizedPreview_RejectsNonUtcControlAndSelectionBeforeCapture()
    {
        var provider = new CountingProvider(Request([RepositoryRecord("owner")]).Snapshot);
        var service = new SanitizedExportAuthorizedService(provider);

        var invalidControl = service.Preview(new(
            SanitizedExportContractVersions.ControlRequest,
            ObservedAt.ToOffset(TimeSpan.FromHours(9)),
            new()));
        var invalidSelection = service.Preview(new(
            SanitizedExportContractVersions.ControlRequest,
            ObservedAt,
            new(StartInclusive: ObservedAt.ToOffset(TimeSpan.FromHours(9)))));

        Assert.Equal("request_invalid", invalidControl.ErrorCode);
        Assert.Equal("invalid_selection", invalidSelection.ErrorCode);
        Assert.Equal(0, provider.CaptureCount);
    }

    [Fact]
    public void Preview_ExternalDependencyRemainsExternalWhenMatchingRecordExists()
    {
        var first = RepositoryRecord("first") with
        {
            Dependencies = [new("repository_metadata_projection", "second", SanitizedExportDependencyDisposition.External)],
        };
        var request = Request([first, RepositoryRecord("second")]) with
        {
            Selection = new(SessionIds: ["first"]),
        };

        var preview = new SanitizedExportService().Preview(request);

        Assert.True(preview.Success, preview.ErrorCode);
        Assert.Equal(["first"], preview.Entries.Select(entry => entry.RecordId));
        var unresolved = Assert.Single(preview.UnresolvedDependencies);
        Assert.Equal("second", unresolved.RecordId);
        Assert.Equal("external", unresolved.State);
    }

    [Fact]
    public void Preview_RejectsDuplicatePathEvenWhenDuplicateIsExcluded()
    {
        var first = RepositoryRecord("first");
        var second = RepositoryRecord("second") with { EntryPath = first.EntryPath };
        var request = Request([first, second]) with { Selection = new(SessionIds: ["first"]) };

        Assert.Equal("duplicate_entry", new SanitizedExportService().Preview(request).ErrorCode);
    }

    [Theory]
    [InlineData("/opt/private/file.txt")]
    [InlineData("/custom-root/private/file.txt")]
    [InlineData("//server/share/private.txt")]
    [InlineData("//?/C:/private/file.txt")]
    public void Preview_RejectsAdditionalAbsolutePathForms(string path)
    {
        var record = RepositoryRecord("first") with { RepositoryName = path };
        record = record with { CanonicalBytes = RepositoryBytes(record) };

        Assert.Equal("local_path", new SanitizedExportService().Preview(Request([record])).ErrorCode);
    }

    [Fact]
    public void Inspect_RejectsZipPreambleAndEndComment()
    {
        var created = new SanitizedExportService().Create(Request([RepositoryRecord("first")]));
        Assert.True(created.Success, created.ErrorCode);
        var preamble = new byte[created.ArchiveBytes!.Length + 1];
        created.ArchiveBytes.CopyTo(preamble, 1);
        var comment = created.ArchiveBytes.Concat(new byte[] { 0x01 }).ToArray();
        comment[^3] = 0x01;
        comment[^2] = 0x00;

        Assert.Equal("archive_invalid", new SanitizedExportService().Inspect(preamble).ErrorCode);
        Assert.Equal("archive_invalid", new SanitizedExportService().Inspect(comment).ErrorCode);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void Inspect_RejectsLocalOrCentralExtraFieldsAndEntryComments(bool localExtra, bool entryComment)
    {
        var service = new SanitizedExportService();
        var created = service.Create(Request([RepositoryRecord("first")]));
        Assert.True(created.Success, created.ErrorCode);

        var changed = localExtra
            ? AddLastLocalExtraField(created.ArchiveBytes!)
            : AddLastCentralField(created.ArchiveBytes!, entryComment);

        Assert.Equal("archive_invalid", service.Inspect(changed).ErrorCode);
    }

    [Fact]
    public void Inspect_RejectsManifestRecordCountContradiction()
    {
        var service = new SanitizedExportService();
        var created = service.Create(Request([RepositoryRecord("first")]));
        Assert.True(created.Success, created.ErrorCode);
        var changed = RepackManifest(created.ArchiveBytes!, "\"repository_metadata_projection\":1", "\"repository_metadata_projection\":2");

        Assert.Equal("file_inventory_invalid", service.Inspect(changed).ErrorCode);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(34)]
    [InlineData(36)]
    public void Inspect_RejectsNoncanonicalCentralHeaderFields(int fieldOffset)
    {
        var service = new SanitizedExportService();
        var created = service.Create(Request([RepositoryRecord("first")]));
        Assert.True(created.Success, created.ErrorCode);
        var changed = created.ArchiveBytes!.ToArray();
        changed[FindLast(changed, 0x02014b50) + fieldOffset] ^= 0x01;

        Assert.Equal("archive_invalid", service.Inspect(changed).ErrorCode);
    }

    [Fact]
    public void Inspect_RejectsUnnecessaryUtf8FlagForAsciiEntryName()
    {
        var service = new SanitizedExportService();
        var created = service.Create(Request([RepositoryRecord("first")]));
        Assert.True(created.Success, created.ErrorCode);
        var changed = created.ArchiveBytes!.ToArray();
        var central = FindLast(changed, 0x02014b50);
        var local = (int)BinaryPrimitives.ReadUInt32LittleEndian(changed.AsSpan(central + 42, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(changed.AsSpan(central + 8, 2), 0x0800);
        BinaryPrimitives.WriteUInt16LittleEndian(changed.AsSpan(local + 6, 2), 0x0800);

        Assert.Equal("archive_invalid", service.Inspect(changed).ErrorCode);
    }

    private static SanitizedExportRequest Request(IReadOnlyList<SanitizedExportRecord> records)
    {
        var hasFinding = records.Any(record => record.RecordType == "instruction_finding_handoff");
        var hasAlert = records.Any(record => record.RecordType == "alert_receipt");
        return new(
            ObservedAt,
            new(
                "snapshot-85",
                "local-monitor-test",
                [new("github-copilot-cli", "1.0.73")],
                records,
                new(hasFinding ? "available" : "missing", hasAlert ? "available" : "missing", "unavailable", "unavailable", "unavailable")),
            new());
    }

    private static SanitizedExportRecord RepositoryRecord(string id)
    {
        var record = Record($"repository-metadata/{id}.json", "repository_metadata_projection", id, []);
        return record with { CanonicalBytes = RepositoryBytes(record) };
    }

    private static byte[] RepositoryBytes(SanitizedExportRecord record) => Encoding.UTF8.GetBytes(
        $"{{\"schema_version\":\"repository-metadata-projection.v1\",\"record_id\":{JsonSerializer.Serialize(record.RecordId)},\"session_id\":{JsonSerializer.Serialize(record.SessionId)},\"trace_id\":{JsonSerializer.Serialize(record.TraceId)},\"source_surface\":{JsonSerializer.Serialize(record.SourceSurface)},\"repository_name\":{JsonSerializer.Serialize(record.RepositoryName)},\"workspace_label\":{JsonSerializer.Serialize(record.WorkspaceLabel)},\"repo_snapshot\":{JsonSerializer.Serialize(record.RepoSnapshot)},\"observed_at\":\"2026-07-22T00:00:00.0000000Z\",\"completeness\":{JsonSerializer.Serialize(record.Completeness)},\"content_state\":{JsonSerializer.Serialize(record.ContentState)},\"retention_state\":{JsonSerializer.Serialize(record.RetentionState)}}}");

    private static SanitizedExportRecord InstructionRecord(byte[] bytes) => new(
        "instruction-findings/123.json", "instruction_finding_handoff", "123", null, null, null, null, null, null,
        ObservedAt, bytes, []);

    private static SanitizedExportRecord AlertRecord(byte[] bytes)
    {
        var envelope = AlertReceiptConsumerV1.Validate(bytes);
        return new($"alert-receipts/{envelope.AlertId}.json", "alert_receipt", envelope.AlertId, envelope.SessionId,
            envelope.TraceId, envelope.SourceSurface, null, null, null, envelope.LastObservedAt, bytes, []);
    }

    private static SanitizedExportRecord Record(string path, string type, string id, byte[] bytes) => new(
        path, type, id, id, "trace-a", "github-copilot-cli", "sample-repository", "sample-workspace", "sample-snapshot",
        ObservedAt, bytes, [], "partial", "not_captured", "retained_by_policy");

    private static byte[] InstructionBytes() => TrimFinalNewline(File.ReadAllBytes(Path.Combine(
        FindRepositoryRoot(), "tests", "CopilotAgentObservability.LocalMonitor.Tests", "TestData", "InstructionFindings", "instruction-finding-handoff.v1.json")));

    private static byte[] AlertBytes() => SanitizedExportAlertFixture.Bytes();

    private static byte[] TrimFinalNewline(byte[] bytes) => bytes is [.., (byte)'\n'] ? bytes[..^1] : bytes;

    private static byte[] RepackManifest(byte[] sourceBytes, string oldValue, string newValue)
    {
        using var source = new ZipArchive(new MemoryStream(sourceBytes), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var sourceEntry in source.Entries)
            {
                var entry = target.CreateEntry(sourceEntry.FullName, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var input = sourceEntry.Open();
                using var buffer = new MemoryStream();
                input.CopyTo(buffer);
                var bytes = sourceEntry.FullName == "manifest.json"
                    ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(buffer.ToArray()).Replace(oldValue, newValue, StringComparison.Ordinal))
                    : buffer.ToArray();
                using var outputEntry = entry.Open();
                outputEntry.Write(bytes);
            }
        }
        return output.ToArray();
    }

    private static byte[] AddLastLocalExtraField(byte[] source)
    {
        var local = FindLast(source, 0x04034b50);
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(local + 26, 2));
        var insert = local + 30 + nameLength;
        var result = InsertByte(source, insert);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(local + 28, 2), 1);
        var end = FindLast(result, 0x06054b50);
        var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(end + 16, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(end + 16, 4), centralOffset + 1);
        return result;
    }

    private static byte[] AddLastCentralField(byte[] source, bool comment)
    {
        var central = FindLast(source, 0x02014b50);
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(central + 28, 2));
        var insert = central + 46 + nameLength;
        var result = InsertByte(source, insert);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(central + (comment ? 32 : 30), 2), 1);
        var end = FindLast(result, 0x06054b50);
        var centralSize = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(end + 12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(end + 12, 4), centralSize + 1);
        return result;
    }

    private static byte[] InsertByte(byte[] source, int offset)
    {
        var result = new byte[source.Length + 1];
        source.AsSpan(0, offset).CopyTo(result);
        source.AsSpan(offset).CopyTo(result.AsSpan(offset + 1));
        return result;
    }

    private static int FindLast(byte[] bytes, uint signature)
    {
        for (var index = bytes.Length - 4; index >= 0; index--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4)) == signature) return index;
        throw new InvalidDataException();
    }

    private static byte[] ControlJson(string createdAt, string? startInclusive, string? endExclusive) => Encoding.UTF8.GetBytes(
        $"{{\"schema_version\":\"sanitized-export-control.v1\",\"created_at\":{JsonSerializer.Serialize(createdAt)},\"selection\":{{\"session_ids\":null,\"trace_ids\":null,\"source_surfaces\":null,\"repository_names\":null,\"workspace_labels\":null,\"start_inclusive\":{JsonSerializer.Serialize(startInclusive)},\"end_exclusive\":{JsonSerializer.Serialize(endExclusive)},\"receipt_types\":null}}}}");

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }

    private sealed class CountingProvider(SanitizedExportSourceSnapshot snapshot) : ISanitizedExportSnapshotProvider
    {
        internal int CaptureCount { get; private set; }

        public SanitizedExportSnapshotCapture Capture(SanitizedExportSelection selection)
        {
            CaptureCount++;
            return new(true, null, snapshot);
        }
    }
}
