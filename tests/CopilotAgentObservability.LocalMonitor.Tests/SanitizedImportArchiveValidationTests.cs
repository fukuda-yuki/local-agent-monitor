using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotAgentObservability.SanitizedExport;
using CopilotAgentObservability.SanitizedImport;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SanitizedImportArchiveValidationTests
{
    [Fact]
    public void BundleReader_SnapshotsCallerBytesBeforeInspectionAndMemberReads()
    {
        var archive = GoldenBundle();
        var expected = archive.ToArray();

        var result = SanitizedImportBundleReader.Read(archive, () => Array.Fill(archive, (byte)0));

        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expected)).ToLowerInvariant(),
            result.Bundle!.ArchiveSha256);
        Assert.All(archive, value => Assert.Equal((byte)0, value));
        Assert.Equal(expected, GoldenBundle());
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("repository-metadata/session-a.json")]
    public void Inspect_RejectsMatchingButIncorrectLocalAndCentralCrc32(string entryName)
    {
        using var temp = new MonitorTempDirectory();
        var database = Path.Combine(temp.Path, "must-not-exist.sqlite");
        var archive = GoldenBundle();
        var central = FindCentralEntry(archive, entryName);
        var local = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(central + 42, 4)));
        archive[central + 16] ^= 0x01;
        archive[local + 14] ^= 0x01;

        var result = new SanitizedExportBundleInspector().Inspect(archive);
        var preview = new CopilotAgentObservability.Persistence.Sqlite.SanitizedImport.SqliteSanitizedImportStore(database)
            .Preview(archive);

        Assert.False(result.Success);
        Assert.Equal("archive_invalid", result.ErrorCode);
        Assert.False(preview.Success);
        Assert.Equal("archive_invalid", preview.ErrorCode);
        Assert.False(File.Exists(database));
    }

    [Fact]
    public void Inspect_AcceptsCanonicalUnicodeEntryName()
    {
        var archive = UnicodeBundle();

        var result = new SanitizedExportBundleInspector().Inspect(archive);

        Assert.True(result.Success, result.ErrorCode);
    }

    [Theory]
    [InlineData(0xff, 0xbf, 0xbd)]
    [InlineData(0xed, 0xa0, 0x80)]
    [InlineData(0xe2, 0x82, 0x20)]
    public void Inspect_RejectsMalformedOrTruncatedUtf8FilenameBytes(
        int first,
        int second,
        int third)
    {
        using var temp = new MonitorTempDirectory();
        var database = Path.Combine(temp.Path, "must-not-exist.sqlite");
        var archive = UnicodeBundle();
        const string path = "repository-metadata/�.json";
        var central = FindCentralEntry(archive, path);
        var local = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(central + 42, 4)));
        ReplaceReplacementRune(archive, central + 46, path, [(byte)first, (byte)second, (byte)third]);
        ReplaceReplacementRune(archive, local + 30, path, [(byte)first, (byte)second, (byte)third]);

        var result = new SanitizedExportBundleInspector().Inspect(archive);
        var preview = new CopilotAgentObservability.Persistence.Sqlite.SanitizedImport.SqliteSanitizedImportStore(database)
            .Preview(archive);

        Assert.False(result.Success);
        Assert.Equal("archive_invalid", result.ErrorCode);
        Assert.False(preview.Success);
        Assert.Equal("archive_invalid", preview.ErrorCode);
        Assert.False(File.Exists(database));
    }

    [Fact]
    public void PreviewAndCommit_HostileRawArchiveMatrixFailBeforeDatabaseOpen()
    {
        using var temp = new MonitorTempDirectory();

        foreach (var item in HostileArchives())
        {
            var database = Path.Combine(temp.Path, $"{item.Name}.sqlite");
            var commitDatabase = Path.Combine(temp.Path, $"{item.Name}-commit.sqlite");
            var inspected = new SanitizedExportBundleInspector().Inspect(item.Archive);
            var preview = new CopilotAgentObservability.Persistence.Sqlite.SanitizedImport.SqliteSanitizedImportStore(database)
                .Preview(item.Archive);
            var commit = new CopilotAgentObservability.Persistence.Sqlite.SanitizedImport.SqliteSanitizedImportStore(commitDatabase)
                .Commit(item.Archive, new string('a', 64));

            Assert.False(inspected.Success);
            Assert.False(preview.Success);
            Assert.False(commit.Success);
            Assert.Equal(item.Error, inspected.ErrorCode);
            Assert.Equal(item.Error, preview.ErrorCode);
            Assert.Equal(item.Error, commit.ErrorCode);
            Assert.False(File.Exists(database));
            Assert.False(File.Exists(commitDatabase));
        }
    }

    [Fact]
    public void BundleReader_DuplicateManifestMapKeyReturnsFixedFailure()
    {
        var archive = DuplicateMapKey(
            GoldenBundle(),
            "\"record_counts\":{\"repository_metadata_projection\":1}",
            "\"record_counts\":{\"repository_metadata_projection\":1,\"repository_metadata_projection\":1}");

        var result = SanitizedImportBundleReader.Read(archive);

        Assert.False(result.Success);
        Assert.Equal("manifest_invalid", result.ErrorCode);
        Assert.Null(result.Bundle);
    }

    [Theory]
    [InlineData(
        "\"record_counts\":{\"repository_metadata_projection\":1}",
        "\"record_counts\":{\"repository_metadata_projection\":1,\"repository_metadata_projection\":1}")]
    [InlineData(
        "\"completeness_distribution\":{\"partial\":1}",
        "\"completeness_distribution\":{\"partial\":1,\"partial\":1}")]
    [InlineData(
        "\"content_state_distribution\":{\"not_captured\":1}",
        "\"content_state_distribution\":{\"not_captured\":1,\"not_captured\":1}")]
    [InlineData(
        "\"retention_state_distribution\":{\"retained_by_policy\":1}",
        "\"retention_state_distribution\":{\"retained_by_policy\":1,\"retained_by_policy\":1}")]
    [InlineData(
        "\"processing_versions\":{\"evaluation\":\"alert.evaluation.v1\",",
        "\"processing_versions\":{\"evaluation\":\"alert.evaluation.v1\",\"evaluation\":\"alert.evaluation.v1\",")]
    public void Inspect_RejectsDuplicateManifestMapKeys(string original, string replacement)
    {
        var archive = DuplicateMapKey(GoldenBundle(), original, replacement);

        var result = new SanitizedExportBundleInspector().Inspect(archive);

        Assert.False(result.Success);
        Assert.Equal("manifest_invalid", result.ErrorCode);
    }

    private static byte[] GoldenBundle() => File.ReadAllBytes(Path.Combine(
        FindRepositoryRoot(),
        "tests",
        "CopilotAgentObservability.LocalMonitor.Tests",
        "TestData",
        "SanitizedExport",
        "sanitized-evidence.v1.zip"));

    internal static IReadOnlyList<(string Name, byte[] Archive, string Error)> HostileArchives()
    {
        var golden = GoldenBundle();
        var recordPath = EntryNames(golden).Single(name => name != "manifest.json");
        var traversal = MutateEntryName(golden, recordPath, "../" + new string('x', recordPath.Length - 3));
        var absolute = MutateEntryName(golden, recordPath, "/" + new string('x', recordPath.Length - 1));
        var driveAbsolute = MutateEntryName(golden, recordPath, "C:/" + new string('x', recordPath.Length - 3));
        var descriptor = SetMatchingFlags(golden, recordPath, 0x0008);
        var multiDisk = golden.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(multiDisk.AsSpan(FindEnd(multiDisk) + 4, 2), 1);
        var centralDisk = golden.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(
            centralDisk.AsSpan(FindCentralEntry(centralDisk, recordPath) + 34, 2), 1);
        var oversizedName = golden.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(
            oversizedName.AsSpan(FindCentralEntry(oversizedName, recordPath) + 28, 2), ushort.MaxValue);
        var two = TwoRecordBundle();
        var twoNames = EntryNames(two).Where(name => name != "manifest.json").Order(StringComparer.Ordinal).ToArray();
        var duplicate = MutateEntryName(two, twoNames[1], twoNames[0]);
        return
        [
            ("traversal", traversal, "entry_order_invalid"),
            ("absolute", absolute, "entry_order_invalid"),
            ("drive-absolute", driveAbsolute, "entry_order_invalid"),
            ("duplicate", duplicate, "duplicate_entry"),
            ("data-descriptor", descriptor, "archive_invalid"),
            ("multi-disk", multiDisk, "archive_invalid"),
            ("central-disk", centralDisk, "archive_invalid"),
            ("oversized-name", oversizedName, "archive_invalid"),
            ("truncated", golden[..^1], "archive_invalid"),
            ("entry-limit", TooManyEntriesArchive(), "entry_limit_exceeded"),
            ("member-limit", OversizeMemberBundle(), "manifest_invalid"),
            ("scanner", CredentialBundle(), "credential_pattern"),
        ];
    }

    private static byte[] DuplicateMapKey(byte[] sourceBytes, string original, string replacement)
    {
        using var source = new ZipArchive(new MemoryStream(sourceBytes), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var sourceEntry in source.Entries)
            {
                var entry = target.CreateEntry(sourceEntry.FullName, CompressionLevel.NoCompression);
                entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                entry.ExternalAttributes = 0;
                using var sourceStream = sourceEntry.Open();
                using var buffer = new MemoryStream();
                sourceStream.CopyTo(buffer);
                var bytes = buffer.ToArray();
                if (sourceEntry.FullName == "manifest.json")
                {
                    var manifest = Encoding.UTF8.GetString(bytes);
                    Assert.Contains(original, manifest, StringComparison.Ordinal);
                    bytes = Encoding.UTF8.GetBytes(manifest.Replace(original, replacement, StringComparison.Ordinal));
                }
                using var entryStream = entry.Open();
                entryStream.Write(bytes);
            }
        }
        return output.ToArray();
    }

    private static byte[] UnicodeBundle()
    {
        var observedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
        const string recordId = "�";
        var bytes = RepositoryMetadataProjectionV1.Serialize(
            recordId, recordId, "trace-a", "github-copilot-cli", "repository-a", "workspace-a",
            "snapshot-a", observedAt, "partial", "not_captured", "retained_by_policy");
        var record = new SanitizedExportRecord(
            "repository-metadata/�.json", "repository_metadata_projection", recordId,
            recordId, "trace-a", "github-copilot-cli", "repository-a", "workspace-a", "snapshot-a",
            observedAt, bytes, [], "partial", "not_captured", "retained_by_policy");
        var snapshot = new SanitizedExportSourceSnapshot(
            "snapshot-unicode", "local-monitor-test", [new("github-copilot-cli", "1.0.73")], [record],
            new("missing", "missing", "unavailable", "unavailable", "unavailable"));
        var result = new SanitizedExportService().Create(new(observedAt, snapshot, new()));
        Assert.True(result.Success, result.ErrorCode);
        return result.ArchiveBytes!;
    }

    private static byte[] TwoRecordBundle()
    {
        var observedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
        var records = new[] { "record-a", "record-b" }.Select(recordId => new SanitizedExportRecord(
            $"repository-metadata/{recordId}.json", "repository_metadata_projection", recordId,
            recordId, $"trace-{recordId}", "github-copilot-cli", "repository-a", "workspace-a", "snapshot-a",
            observedAt, RepositoryMetadataProjectionV1.Serialize(
                recordId, recordId, $"trace-{recordId}", "github-copilot-cli", "repository-a", "workspace-a",
                "snapshot-a", observedAt, "partial", "not_captured", "retained_by_policy"),
            [], "partial", "not_captured", "retained_by_policy")).ToArray();
        var snapshot = new SanitizedExportSourceSnapshot(
            "snapshot-two", "local-monitor-test", [new("github-copilot-cli", "1.0.73")], records,
            new("missing", "missing", "unavailable", "unavailable", "unavailable"));
        var result = new SanitizedExportService().Create(new(observedAt, snapshot, new()));
        Assert.True(result.Success, result.ErrorCode);
        return result.ArchiveBytes!;
    }

    private static byte[] CredentialBundle()
    {
        return RepackGoldenRecord(bytes =>
        {
            var record = JsonNode.Parse(Encoding.UTF8.GetString(bytes))!.AsObject();
            record["repository_name"] = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
            return JsonSerializer.SerializeToUtf8Bytes(record);
        });
    }

    private static byte[] OversizeMemberBundle() => RepackGoldenRecord(
        _ => new byte[SanitizedExportLimits.MaximumRecordBytes + 1]);

    private static byte[] RepackGoldenRecord(Func<byte[], byte[]> mutate)
    {
        var sourceBytes = GoldenBundle();
        using var source = new ZipArchive(new MemoryStream(sourceBytes), ZipArchiveMode.Read);
        var members = source.Entries.ToDictionary(entry => entry.FullName, entry =>
        {
            using var stream = entry.Open();
            using var output = new MemoryStream();
            stream.CopyTo(output);
            return output.ToArray();
        }, StringComparer.Ordinal);
        var recordPath = members.Keys.Single(name => name != "manifest.json");
        members[recordPath] = mutate(members[recordPath]);
        var manifest = JsonNode.Parse(Encoding.UTF8.GetString(members["manifest.json"]))!.AsObject();
        var file = manifest["files"]!.AsArray().Single()!.AsObject();
        file["size"] = members[recordPath].LongLength;
        file["sha256"] = Convert.ToHexString(SHA256.HashData(members[recordPath])).ToLowerInvariant();
        members["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest);

        using var outputArchive = new MemoryStream();
        using (var target = new ZipArchive(outputArchive, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in new[] { "manifest.json", recordPath })
            {
                var entry = target.CreateEntry(path, CompressionLevel.NoCompression);
                entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                entry.ExternalAttributes = 0;
                using var stream = entry.Open();
                stream.Write(members[path]);
            }
        }
        return outputArchive.ToArray();
    }

    private static byte[] TooManyEntriesArchive()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index <= SanitizedExportLimits.MaximumArchiveEntries; index++)
            {
                var entry = archive.CreateEntry($"entry-{index:D3}.json", CompressionLevel.NoCompression);
                entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                entry.ExternalAttributes = 0;
            }
        }
        return output.ToArray();
    }

    private static string[] EntryNames(byte[] archive)
    {
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        return zip.Entries.Select(entry => entry.FullName).ToArray();
    }

    private static byte[] MutateEntryName(byte[] source, string currentName, string replacement)
    {
        var current = Encoding.UTF8.GetBytes(currentName);
        var changed = Encoding.UTF8.GetBytes(replacement);
        Assert.Equal(current.Length, changed.Length);
        var result = source.ToArray();
        var central = FindCentralEntry(result, currentName);
        var local = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(central + 42, 4)));
        changed.CopyTo(result, central + 46);
        changed.CopyTo(result, local + 30);
        return result;
    }

    private static byte[] SetMatchingFlags(byte[] source, string entryName, ushort flags)
    {
        var result = source.ToArray();
        var central = FindCentralEntry(result, entryName);
        var local = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(central + 42, 4)));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(central + 8, 2), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(local + 6, 2), flags);
        return result;
    }

    private static int FindCentralEntry(byte[] archive, string name)
    {
        for (var offset = 0; offset <= archive.Length - 46; offset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(offset, 4)) != 0x02014b50) continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(offset + 28, 2));
            if (offset + 46 + nameLength <= archive.Length
                && Encoding.UTF8.GetString(archive, offset + 46, nameLength) == name)
                return offset;
        }
        throw new InvalidDataException();
    }

    private static int FindEnd(byte[] archive)
    {
        for (var offset = archive.Length - 22; offset >= 0; offset--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(offset, 4)) == 0x06054b50) return offset;
        throw new InvalidDataException();
    }

    private static void ReplaceReplacementRune(byte[] archive, int nameOffset, string name, byte[] replacement)
    {
        var encodedName = Encoding.UTF8.GetBytes(name);
        var rune = Encoding.UTF8.GetBytes("�");
        var relative = encodedName.AsSpan().IndexOf(rune);
        Assert.True(relative >= 0);
        Assert.Equal(rune.Length, replacement.Length);
        replacement.CopyTo(archive, nameOffset + relative);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
}
