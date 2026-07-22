using System.IO.Compression;
using System.Text;
using CopilotAgentObservability.RawReplay;
using CopilotAgentObservability.SanitizedExport;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class RawReplayArchiveServiceTests
{
    [Fact]
    public void Create_is_deterministic_and_round_trips_exact_raw_identity_and_versions()
    {
        var service = new RawReplayArchiveService();
        var snapshot = Snapshot(Record(7, Payload("trace-b")), Record(3, Payload("trace-a")));
        var request = Request();

        var preview = service.Preview(snapshot, request);
        var confirmed = request with { PreviewDigest = preview.PreviewDigest, Consent = Consent() };
        var first = service.Create(snapshot, confirmed);
        var second = service.Create(Snapshot(Record(3, Payload("trace-a")), Record(7, Payload("trace-b"))), confirmed);
        Assert.True(first.Success, first.ErrorCode);
        Assert.True(second.Success, second.ErrorCode);
        var inspected = service.Inspect(first.ArchiveBytes!);

        Assert.Equal(first.ArchiveSha256, second.ArchiveSha256);
        Assert.Equal(first.ArchiveBytes, second.ArchiveBytes);
        Assert.True(inspected.Success);
        Assert.Equal(RawReplayContractVersions.BundleProfile, inspected.BundleProfile);
        Assert.Equal([3L, 7L], inspected.Bundle!.Records.Select(record => record.RawRecordId));
        Assert.All(inspected.Bundle.Records, record => Assert.Equal("adapter-v1", record.Provenance.AdapterVersion));
        Assert.Equal(RawReplayContractVersions.Normalization, inspected.Bundle.Manifest.NormalizationVersion);
        Assert.Equal(first.Preview.ExpectedNormalizedSha256, inspected.Bundle.Manifest.ExpectedNormalizedSha256);
        Assert.False(new SanitizedExportBundleInspector().Inspect(first.ArchiveBytes!).Success);
    }

    [Fact]
    public void Create_round_trips_allowed_session_content_with_original_identity_and_timestamps()
    {
        var service = new RawReplayArchiveService();
        var content = new RawReplaySessionContent(
            "event-one", "session-one", "run-one", "trace-a", "copilot-cli", "source-event-one",
            new DateTimeOffset(2026, 7, 22, 11, 0, 0, TimeSpan.Zero), "available", "app-v1", "adapter-v1",
            "schema-v1", "normalization-v1", "exact-native", "assistant_response", "{\"text\":\"synthetic response\"}",
            new DateTimeOffset(2026, 7, 22, 11, 0, 1, TimeSpan.Zero), new DateTimeOffset(2026, 7, 23, 11, 0, 1, TimeSpan.Zero),
            "session_secret_filter_applied", RawReplayContractVersions.CredentialScanner);
        var snapshot = Snapshot(Record(1, Payload("trace-a"))) with { SessionContents = [content], KnownMissing = [] };
        var request = Request() with { Selection = new(SessionIds: ["session-one"], RawRecordIds: [1]), IncludeSessionContent = true };

        var created = Create(service, snapshot, request);
        var inspected = service.Inspect(created.ArchiveBytes!);

        Assert.True(inspected.Success, inspected.ErrorCode);
        var roundTripped = Assert.Single(inspected.Bundle!.SessionContents);
        Assert.Equal(content.EventId, roundTripped.EventId);
        Assert.Equal(content.OccurredAt, roundTripped.OccurredAt);
        Assert.Equal(content.CapturedAt, roundTripped.CapturedAt);
        Assert.Equal(content.AdapterVersion, roundTripped.AdapterVersion);
    }

    [Fact]
    public void Preview_and_create_require_raw_profile_warning_consent_and_exact_digest()
    {
        var service = new RawReplayArchiveService();
        var snapshot = Snapshot(Record(1, Payload("trace-a")));
        var request = Request();
        var preview = service.Preview(snapshot, request);

        Assert.True(preview.Success);
        Assert.Equal("raw", preview.DataClassification);
        Assert.Contains("Keep it local", preview.Warning, StringComparison.Ordinal);
        Assert.Equal("consent_required", service.Create(snapshot, request with { PreviewDigest = preview.PreviewDigest }).ErrorCode);
        Assert.Equal("preview_changed", service.Create(snapshot, request with { PreviewDigest = new string('0', 64), Consent = Consent() }).ErrorCode);
        Assert.Equal("sanitized_only_denied", service.Create(snapshot, request with { SanitizedOnly = true, PreviewDigest = preview.PreviewDigest, Consent = Consent() }).ErrorCode);
        Assert.Equal("profile_invalid", service.Create(snapshot, request with { Profile = "sanitized-evidence", PreviewDigest = preview.PreviewDigest, Consent = Consent() }).ErrorCode);
    }

    [Fact]
    public void Known_credential_fixture_is_rejected_without_echoing_the_value()
    {
        var service = new RawReplayArchiveService();
        const string fixture = "Bearer example-fixture-not-a-secret";
        var snapshot = Snapshot(Record(1, Payload("trace-a", fixture)));
        var request = Request();
        var preview = service.Preview(snapshot, request);

        Assert.False(preview.Success);
        Assert.Equal("credential_material_detected", preview.ErrorCode);
        Assert.DoesNotContain(fixture, RawReplayJson.Text(preview), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sk-abcdefghijklmnopqrstuv")]
    [InlineData("ghp_123456789012345678901234")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    public void Known_provider_credential_fixtures_are_rejected(string fixture)
    {
        var service = new RawReplayArchiveService();
        var preview = service.Preview(Snapshot(Record(1, Payload("trace-a", fixture))), Request());

        Assert.False(preview.Success);
        Assert.Equal("credential_material_detected", preview.ErrorCode);
        Assert.DoesNotContain(fixture, RawReplayJson.Text(preview), StringComparison.Ordinal);
    }

    [Fact]
    public void Json_credential_property_is_rejected_without_echoing_the_value()
    {
        var service = new RawReplayArchiveService();
        const string fixture = "abcdefgh12345678";
        var payload = $$"""{"api_key":"{{fixture}}"}""";

        var preview = service.Preview(Snapshot(Record(1, payload)), Request());

        Assert.False(preview.Success);
        Assert.Equal("credential_material_detected", preview.ErrorCode);
        Assert.DoesNotContain(fixture, RawReplayJson.Text(preview), StringComparison.Ordinal);
    }

    [Fact]
    public void Credential_material_in_identity_or_manifest_metadata_is_rejected_without_echoing_it()
    {
        var service = new RawReplayArchiveService();
        const string fixture = "ghp_123456789012345678901234";

        var identity = service.Preview(Snapshot(Record(1, Payload("trace-a")) with { TraceId = fixture }), Request());
        var manifestMetadata = service.Preview(Snapshot(Record(1, Payload("trace-a"))) with { KnownMissing = [fixture] }, Request());

        Assert.Equal("credential_material_detected", identity.ErrorCode);
        Assert.Equal("credential_material_detected", manifestMetadata.ErrorCode);
        Assert.DoesNotContain(fixture, RawReplayJson.Text(identity), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture, RawReplayJson.Text(manifestMetadata), StringComparison.Ordinal);
    }

    [Fact]
    public void Inspector_rejects_wrong_profile_checksum_traversal_and_oversized_archive()
    {
        var service = new RawReplayArchiveService();
        var created = Create(service, Snapshot(Record(1, Payload("trace-a"))));

        Assert.Equal("profile_invalid", service.Inspect(RewriteManifest(created, text => text.Replace("raw-local-replay", "sanitized-evidence", StringComparison.Ordinal))).ErrorCode);
        Assert.Equal("checksum_mismatch", service.Inspect(RewriteRecord(created, bytes =>
        {
            var changed = bytes.ToArray();
            changed[^2] = changed[^2] == (byte)'}' ? (byte)']' : (byte)'}';
            return changed;
        })).ErrorCode);
        Assert.Equal("compression_not_allowed", service.Inspect(RewriteCompressed(created)).ErrorCode);
        Assert.Equal("entry_path_invalid", service.Inspect(AddStoredEntry(created, "../escape.json", "{}\n"u8.ToArray())).ErrorCode);
        Assert.Equal("archive_too_large", service.Inspect(new byte[RawReplayLimits.MaximumArchiveBytes + 1]).ErrorCode);
    }

    [Fact]
    public void Inspector_rejects_manifest_metadata_that_disagrees_with_canonical_members()
    {
        var service = new RawReplayArchiveService();
        var created = Create(service, Snapshot(Record(1, Payload("trace-a"))));

        var changed = RewriteManifest(created, text => text.Replace(
            "github-copilot-cli|1.0|otlp-json|adapter-v1|schema-v1",
            "tampered-source|1.0|otlp-json|adapter-v1|schema-v1",
            StringComparison.Ordinal));

        Assert.Equal("manifest_metadata_mismatch", service.Inspect(changed).ErrorCode);
    }

    [Fact]
    public void Preview_rejects_non_utc_record_as_data_error()
    {
        var service = new RawReplayArchiveService();
        var record = Record(1, Payload("trace-a")) with
        {
            ReceivedAt = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.FromHours(9)),
        };

        var preview = service.Preview(Snapshot(record), Request());

        Assert.False(preview.Success);
        Assert.Equal("record_invalid", preview.ErrorCode);
    }

    [Fact]
    public void Preview_rejects_unknown_source_contract_state_and_manifest_path_metadata()
    {
        var service = new RawReplayArchiveService();
        var record = Record(1, Payload("trace-a"));
        var unknownState = record with { Provenance = record.Provenance with { CompatibilityState = "future_state" } };
        var pathMetadata = record with { Provenance = record.Provenance with { SourceSurface = @"C:\private\adapter" } };

        Assert.Equal("record_invalid", service.Preview(Snapshot(unknownState), Request()).ErrorCode);
        Assert.Equal("record_invalid", service.Preview(Snapshot(pathMetadata), Request()).ErrorCode);
    }

    [Fact]
    public void Source_id_conflict_fails_closed_but_identical_duplicates_are_idempotent()
    {
        var service = new RawReplayArchiveService();
        var identical = Snapshot(Record(4, Payload("trace-a")), Record(4, Payload("trace-a")));
        var conflicting = Snapshot(Record(4, Payload("trace-a")), Record(4, Payload("trace-b")));

        Assert.True(service.Preview(identical, Request()).Success);
        Assert.Equal(1, service.Preview(identical, Request()).RawRecordCount);
        Assert.Equal("source_id_conflict", service.Preview(conflicting, Request()).ErrorCode);
    }

    [Fact]
    public void Session_content_uses_source_adapter_and_source_event_id_as_its_identity()
    {
        var service = new RawReplayArchiveService();
        var first = Content("event-shared", "adapter-a", "source-event");
        var distinctSource = Content("event-shared", "adapter-b", "source-event");
        var conflictingSource = first with { EventId = "event-other" };
        var request = Request() with
        {
            Selection = new(SessionIds: ["session-one"], RawRecordIds: [1]),
            IncludeSessionContent = true,
        };

        var distinct = service.Preview(Snapshot(Record(1, Payload("trace-a"))) with
        {
            SessionContents = [first, distinctSource],
            KnownMissing = [],
        }, request);
        var conflict = service.Preview(Snapshot(Record(1, Payload("trace-a"))) with
        {
            SessionContents = [first, conflictingSource],
            KnownMissing = [],
        }, request);

        Assert.True(distinct.Success, distinct.ErrorCode);
        Assert.Equal(2, distinct.SessionContentCount);
        Assert.Equal("source_id_conflict", conflict.ErrorCode);
    }

    [Fact]
    public void Derived_hashes_are_stable_when_equivalent_multi_trace_containers_are_permuted()
    {
        var service = new RawReplayArchiveService();
        var forward = Record(1, MultiTracePayload("trace-a", "trace-b")) with { TraceId = "trace-a" };
        var reverse = Record(1, MultiTracePayload("trace-b", "trace-a")) with { TraceId = "trace-a" };

        var first = service.Preview(Snapshot(forward), Request());
        var second = service.Preview(Snapshot(reverse), Request());

        Assert.True(first.Success, first.ErrorCode);
        Assert.True(second.Success, second.ErrorCode);
        Assert.Equal(first.ExpectedNormalizedSha256, second.ExpectedNormalizedSha256);
        Assert.Equal(first.ExpectedProjectionSha256, second.ExpectedProjectionSha256);
        Assert.Equal(first.ExpectedDashboardSha256, second.ExpectedDashboardSha256);
    }

    [Fact]
    public void Projection_hash_uses_canonical_primary_trace_when_the_record_trace_is_missing()
    {
        var service = new RawReplayArchiveService();
        var forward = Record(1, MultiTracePayloadWithClientKinds(
            ("trace-b", "client-b"),
            ("trace-a", "client-a"))) with { TraceId = null };
        var reverse = Record(1, MultiTracePayloadWithClientKinds(
            ("trace-a", "client-a"),
            ("trace-b", "client-b"))) with { TraceId = null };

        var first = service.Preview(Snapshot(forward), Request());
        var second = service.Preview(Snapshot(reverse), Request());

        Assert.True(first.Success, first.ErrorCode);
        Assert.True(second.Success, second.ErrorCode);
        Assert.Equal(first.ExpectedNormalizedSha256, second.ExpectedNormalizedSha256);
        Assert.Equal(first.ExpectedProjectionSha256, second.ExpectedProjectionSha256);
        Assert.Equal(first.ExpectedDashboardSha256, second.ExpectedDashboardSha256);
    }

    [Fact]
    public void Inspector_collapses_byte_identical_duplicate_source_ids()
    {
        var service = new RawReplayArchiveService();
        var created = Create(service, Snapshot(Record(1, Payload("trace-a"))));

        var inspected = service.Inspect(DuplicateFirstRawRecord(created));

        Assert.True(inspected.Success, inspected.ErrorCode);
        Assert.Equal(1, inspected.RawRecordCount);
        Assert.Single(inspected.Bundle!.Records);
    }

    [Fact]
    public void Inspector_rejects_a_null_manifest_file_descriptor_without_throwing()
    {
        var service = new RawReplayArchiveService();
        var created = Create(service, Snapshot(Record(1, Payload("trace-a"))));
        var manifest = RawReplayJson.DeserializeExact<RawReplayManifest>(created.ManifestBytes!);

        var inspected = service.Inspect(Rewrite(
            created,
            "manifest.json",
            _ => RawReplayJson.SerializeCanonical(manifest with { Files = [null!] })));

        Assert.False(inspected.Success);
        Assert.Equal("inventory_mismatch", inspected.ErrorCode);
    }

    [Fact]
    public void CreateAndPublish_rejects_sensitive_output_names_without_writing_them()
    {
        var service = new RawReplayArchiveService();
        var snapshot = Snapshot(Record(1, Payload("trace-a")));
        var request = Request();
        var preview = service.Preview(snapshot, request);
        var directory = Path.Combine(Path.GetTempPath(), $"raw-replay-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.Combine(directory, "session-one.zip");
            var result = service.CreateAndPublish(snapshot, request with { PreviewDigest = preview.PreviewDigest, Consent = Consent() }, output);

            Assert.False(result.Success);
            Assert.Equal("output_name_invalid", result.ErrorCode);
            Assert.False(File.Exists(output));
            Assert.DoesNotContain("session-one", RawReplayJson.Text(result), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CreateAndPublish_preserves_a_preexisting_unowned_fixed_partial()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var output = Path.Combine(directory, "raw-local-replay.zip");
            var unownedPartial = output + ".partial";
            var marker = "unowned-writer"u8.ToArray();
            File.WriteAllBytes(unownedPartial, marker);
            var service = new RawReplayArchiveService();
            var snapshot = Snapshot(Record(1, Payload("trace-a")));
            var request = Request();
            var preview = service.Preview(snapshot, request);

            var result = service.CreateAndPublish(
                snapshot,
                request with { PreviewDigest = preview.PreviewDigest, Consent = Consent() },
                output);

            Assert.True(result.Success, result.ErrorCode);
            Assert.True(service.Inspect(File.ReadAllBytes(output)).Success);
            Assert.Equal(marker, File.ReadAllBytes(unownedPartial));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CreateAndPublish_does_not_delete_a_staging_file_it_did_not_create()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var output = Path.Combine(directory, "raw-local-replay.zip");
            var collision = Path.Combine(directory, "raw-local-replay.zip.forced.partial");
            var marker = "concurrent-writer"u8.ToArray();
            File.WriteAllBytes(collision, marker);
            var service = new RawReplayArchiveService(_ => collision);
            var snapshot = Snapshot(Record(1, Payload("trace-a")));
            var request = Request();
            var preview = service.Preview(snapshot, request);

            var result = service.CreateAndPublish(
                snapshot,
                request with { PreviewDigest = preview.PreviewDigest, Consent = Consent() },
                output);

            Assert.False(result.Success);
            Assert.Equal("publish_failed", result.ErrorCode);
            Assert.False(File.Exists(output));
            Assert.Equal(marker, File.ReadAllBytes(collision));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Concurrent_publishers_do_not_delete_each_others_staging_files()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var output = Path.Combine(directory, "raw-local-replay.zip");
            using var ready = new Barrier(2);
            var sequence = 0;
            var service = new RawReplayArchiveService(path =>
            {
                var owned = $"{path}.{Interlocked.Increment(ref sequence):D2}.partial";
                Assert.True(ready.SignalAndWait(TimeSpan.FromSeconds(5)), "Concurrent publisher did not reach the staging factory.");
                return owned;
            });
            var snapshot = Snapshot(Record(1, Payload("trace-a")));
            var request = Request();
            var preview = service.Preview(snapshot, request);
            var confirmed = request with { PreviewDigest = preview.PreviewDigest, Consent = Consent() };

            var results = await Task.WhenAll(
                Task.Run(() => service.CreateAndPublish(snapshot, confirmed, output)),
                Task.Run(() => service.CreateAndPublish(snapshot, confirmed, output)));

            Assert.Single(results, result => result.Success);
            var loser = Assert.Single(results, result => !result.Success);
            Assert.Equal("publish_failed", loser.ErrorCode);
            Assert.True(service.Inspect(File.ReadAllBytes(output)).Success);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.partial"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static RawReplayResult Create(RawReplayArchiveService service, RawReplaySnapshot snapshot)
        => Create(service, snapshot, Request());

    private static RawReplayResult Create(RawReplayArchiveService service, RawReplaySnapshot snapshot, RawReplayExportControl request)
    {
        var preview = service.Preview(snapshot, request);
        var result = service.Create(snapshot, request with { PreviewDigest = preview.PreviewDigest, Consent = Consent() });
        Assert.True(result.Success, result.ErrorCode);
        return result;
    }

    private static RawReplayExportControl Request() => new(
        RawReplayContractVersions.ExportControl,
        RawReplayContractVersions.BundleProfile,
        new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero),
        new(RawRecordIds: [3, 7]),
        IncludeSessionContent: false,
        SanitizedOnly: false,
        PreviewDigest: null,
        Consent: null);

    private static RawReplayConsent Consent() => new(
        RawReplayContractVersions.BundleProfile,
        WarningAcknowledged: true,
        ConfirmationPhrase: RawReplayConsent.RequiredPhrase);

    private static RawReplaySnapshot Snapshot(params RawReplayRecord[] records) => new(
        SnapshotId: "snapshot-v1",
        CapturedAt: new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero),
        LocalMonitorVersion: "monitor-v1",
        Records: records,
        SessionContents: [],
        KnownMissing: ["session_event_content_not_requested"]);

    private static RawReplayRecord Record(long id, string payload) => new(
        id,
        "raw-otlp",
        id == 3 ? "trace-a" : "trace-b",
        new DateTimeOffset(2026, 7, 22, 12, 0, checked((int)id), TimeSpan.Zero),
        "{}",
        payload,
        1,
        new("github-copilot-cli", "1.0", "otlp-json", "adapter-v1", "schema-v1", new string('a', 64), "supported", "available", "not_applied_raw_otlp", "raw-replay-credential-scan.v1"));

    private static RawReplaySessionContent Content(string eventId, string sourceAdapter, string sourceEventId) => new(
        eventId, "session-one", "run-one", "trace-a", sourceAdapter, sourceEventId,
        new DateTimeOffset(2026, 7, 22, 11, 0, 0, TimeSpan.Zero), "available", "app-v1", "adapter-v1",
        "schema-v1", "normalization-v1", "exact-native", "assistant_response", "{\"text\":\"synthetic\"}",
        new DateTimeOffset(2026, 7, 22, 11, 0, 1, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 23, 11, 0, 1, TimeSpan.Zero),
        "session_secret_filter_applied", RawReplayContractVersions.CredentialScanner);

    private static string Payload(string traceId, string? body = null)
    {
        var span = new Dictionary<string, object?>
        {
            ["traceId"] = traceId,
            ["spanId"] = "span-1",
            ["name"] = "chat",
            ["attributes"] = new[] { new { key = "gen_ai.usage.input_tokens", value = new { intValue = "2" } } },
        };
        if (body is not null) span["status"] = new { message = body };
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            resourceSpans = new[] { new { scopeSpans = new[] { new { spans = new[] { span } } } } },
        });
    }

    private static string MultiTracePayload(params string[] traceIds) => System.Text.Json.JsonSerializer.Serialize(new
    {
        resourceSpans = traceIds.Select(traceId => new
        {
            scopeSpans = new[]
            {
                new
                {
                    spans = new[]
                    {
                        new
                        {
                            traceId,
                            spanId = $"span-{traceId}",
                            name = "chat",
                            attributes = new[] { new { key = "gen_ai.usage.input_tokens", value = new { intValue = "2" } } },
                        },
                    },
                },
            },
        }).ToArray(),
    });

    private static string MultiTracePayloadWithClientKinds(params (string TraceId, string ClientKind)[] traces) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            resourceSpans = traces.Select(trace => new
            {
                resource = new
                {
                    attributes = new[]
                    {
                        new { key = "client.kind", value = new { stringValue = trace.ClientKind } },
                    },
                },
                scopeSpans = new[]
                {
                    new
                    {
                        spans = new[]
                        {
                            new
                            {
                                traceId = trace.TraceId,
                                spanId = $"span-{trace.TraceId}",
                                name = "chat",
                            },
                        },
                    },
                },
            }).ToArray(),
        });

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"raw-replay-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static byte[] RewriteManifest(RawReplayResult result, Func<string, string> transform) => Rewrite(result, "manifest.json", bytes => Encoding.UTF8.GetBytes(transform(Encoding.UTF8.GetString(bytes))));
    private static byte[] RewriteRecord(RawReplayResult result, Func<byte[], byte[]> transform) => Rewrite(result, "records/record-000001.json", transform);

    private static byte[] DuplicateFirstRawRecord(RawReplayResult result)
    {
        using var input = new ZipArchive(new MemoryStream(result.ArchiveBytes!), ZipArchiveMode.Read);
        var recordEntry = input.GetEntry("records/record-000001.json")!;
        using var recordOutput = new MemoryStream();
        using (var source = recordEntry.Open()) source.CopyTo(recordOutput);
        var recordBytes = recordOutput.ToArray();
        var manifest = RawReplayJson.DeserializeExact<RawReplayManifest>(result.ManifestBytes!);
        var duplicate = manifest.Files[0] with { Path = "records/record-000002.json" };
        var manifestBytes = RawReplayJson.SerializeCanonical(manifest with
        {
            RawRecordCount = manifest.RawRecordCount + 1,
            Files = [.. manifest.Files, duplicate],
        });

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteStored(archive, "manifest.json", manifestBytes);
            WriteStored(archive, "records/record-000001.json", recordBytes);
            WriteStored(archive, "records/record-000002.json", recordBytes);
        }
        return output.ToArray();
    }

    private static void WriteStored(ZipArchive archive, string path, byte[] bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        entry.ExternalAttributes = 0;
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static byte[] Rewrite(RawReplayResult result, string target, Func<byte[], byte[]> transform)
    {
        using var input = new ZipArchive(new MemoryStream(result.ArchiveBytes!), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in input.Entries)
            {
                var copy = archive.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                copy.LastWriteTime = entry.LastWriteTime;
                using var source = entry.Open(); using var buffer = new MemoryStream(); source.CopyTo(buffer);
                var bytes = entry.FullName == target ? transform(buffer.ToArray()) : buffer.ToArray();
                using var destination = copy.Open(); destination.Write(bytes);
            }
        }
        return output.ToArray();
    }

    private static byte[] AddStoredEntry(RawReplayResult result, string path, byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var input = new ZipArchive(new MemoryStream(result.ArchiveBytes!), ZipArchiveMode.Read);
            foreach (var entry in input.Entries)
            {
                var copy = archive.CreateEntry(entry.FullName, CompressionLevel.NoCompression); copy.LastWriteTime = entry.LastWriteTime;
                using var source = entry.Open(); using var destination = copy.Open(); source.CopyTo(destination);
            }
            var added = archive.CreateEntry(path, CompressionLevel.NoCompression); added.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var stream = added.Open(); stream.Write(bytes);
        }
        return output.ToArray();
    }

    private static byte[] RewriteCompressed(RawReplayResult result)
    {
        using var input = new ZipArchive(new MemoryStream(result.ArchiveBytes!), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in input.Entries)
            {
                var level = entry.FullName == "manifest.json" ? CompressionLevel.Optimal : CompressionLevel.NoCompression;
                var copy = archive.CreateEntry(entry.FullName, level);
                copy.LastWriteTime = entry.LastWriteTime;
                copy.ExternalAttributes = 0;
                using var source = entry.Open();
                using var destination = copy.Open();
                source.CopyTo(destination);
            }
        }
        return output.ToArray();
    }
}
