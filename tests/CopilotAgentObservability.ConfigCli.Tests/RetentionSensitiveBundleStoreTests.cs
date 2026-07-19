using CopilotAgentObservability.ConfigCli;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class RetentionSensitiveBundleStoreTests
{
    [Fact]
    public void LegacyBundle_AdoptsTheOriginalWriterFormatWithItsOriginalExpiry()
    {
        using var fixture = Fixture.Create();
        var parent = Path.Combine(fixture.Root, "legacy-parent");
        var bundle = Path.Combine(parent, "legacy-bundle");
        Directory.CreateDirectory(Path.Combine(bundle, "evidence"));
        File.WriteAllText(Path.Combine(bundle, "evidence", "diagcand-0001.json"), "{\"raw\":\"synthetic\"}");
        var created = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        File.WriteAllText(Path.Combine(bundle, "manifest.json"), $$"""
            {"schema_version":1,"bundle_id":"legacy-bundle","created_at_utc":"{{created:O}}","expires_at_utc":"{{created.AddDays(7):O}}","generated_by_command":"generate-diagnosis-candidates","source_inputs":[],"content_included":true,"delete_target_paths":["{{bundle.Replace("\\", "\\\\")}}"],"evidence_index":[{"evidence_ref":"bundle:legacy-bundle:diagcand-0001","diagnosis_candidate_id":"diagcand-0001","evidence_file":"evidence/diagcand-0001.json","content_kinds":["prompt"],"fragment_count":1}]}
            """);

        new RetentionSensitiveBundleStore(fixture.Reopen()).AdoptLegacyBundles(bundle);

        var adoptedPath = fixture.Scalar<string>("SELECT private_locator FROM retention_items WHERE store_kind='sensitive_bundle'", "unused");
        Assert.True(File.Exists(Path.Combine(adoptedPath, RetentionFileCaptureContracts.OwnerMarkerName)));
        Assert.True(Directory.Exists(bundle));
        Assert.DoesNotContain(bundle, File.ReadAllText(Path.Combine(adoptedPath, "manifest.json")), StringComparison.Ordinal);
        Assert.Equal(created.ToString("O"), fixture.Scalar<string>("SELECT captured_at FROM retention_items WHERE store_kind='sensitive_bundle'", "unused"));
        Assert.Equal(created.AddDays(7).ToString("O"), fixture.Scalar<string>("SELECT expires_at FROM retention_items WHERE store_kind='sensitive_bundle'", "unused"));
        new RetentionSensitiveBundleStore(fixture.Reopen()).AdoptLegacyBundles(bundle);
        Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='sensitive_bundle'", "unused"));
    }

    [Theory]
    [InlineData("legacy_before_root_to_staging_move")]
    [InlineData("legacy_after_root_to_staging_move")]
    [InlineData("legacy_before_parent_recreate")]
    [InlineData("legacy_after_parent_recreate")]
    [InlineData("legacy_before_staging_to_final_move")]
    [InlineData("legacy_after_staging_to_final_move")]
    [InlineData("legacy_manifest_temp_flushed")]
    [InlineData("legacy_manifest_atomically_replaced")]
    [InlineData("legacy_after_manifest_replace")]
    [InlineData("legacy_after_marker_write")]
    [InlineData("legacy_after_catalog_completion")]
    public void LegacyBundle_RecoversEveryDurableFilesystemCheckpoint(string checkpoint)
    {
        using var fixture = Fixture.Create();
        var root = Path.Combine(fixture.Root, checkpoint);
        CreateLegacyBundle(root);
        var crashing = new RetentionSensitiveBundleStore(fixture.Reopen(), phase => { if (phase == checkpoint) throw new RetentionLegacyMigrationCrashException(); });

        Assert.Throws<RetentionLegacyMigrationCrashException>(() => crashing.AdoptLegacyBundles(root));
        var recovery = new RetentionSensitiveBundleStore(fixture.Reopen());
        recovery.Recover();
        recovery.Recover();

        Assert.True(Directory.Exists(root));
        Assert.Single(Directory.EnumerateDirectories(root));
        Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='sensitive_bundle'", "unused"));
        Assert.Equal("catalog_completed", fixture.Scalar<string>("SELECT subphase FROM retention_legacy_bundle_journal", "unused"));
    }

    [Fact]
    public void Capture_DoesNotCreateAChildWhenLegacyProofIsBlocked()
    {
        using var fixture = Fixture.Create();
        var root = Path.Combine(fixture.Root, "unprovable-legacy");
        CreateLegacyBundle(root);
        File.WriteAllText(Path.Combine(root, "unexpected.txt"), "preserve");

        Assert.Throws<InvalidOperationException>(() => new RetentionSensitiveBundleStore(fixture.Catalog).Capture(Candidates(), Sources(), root));
        Assert.Equal(new[] { "evidence" }, Directory.EnumerateDirectories(root).Select(Path.GetFileName));
        Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_legacy_bundle_blockers", "unused"));
    }

    [Fact]
    public void LegacyBundle_UnexpectedPersistedReplacementTempIsPreservedAndBlocked()
    {
        using var fixture = Fixture.Create();
        var root = Path.Combine(fixture.Root, "unexpected-temp");
        CreateLegacyBundle(root);
        var crashing = new RetentionSensitiveBundleStore(fixture.Reopen(), phase => { if (phase == "legacy_after_staging_to_final_move") throw new RetentionLegacyMigrationCrashException(); });
        Assert.Throws<RetentionLegacyMigrationCrashException>(() => crashing.AdoptLegacyBundles(root));
        var final = Assert.Single(Directory.EnumerateDirectories(root));
        var captureId = Path.GetFileName(final)!;
        var temporary = Path.Combine(final, $".manifest.retention.{captureId}.tmp");
        File.WriteAllText(temporary, "replacement-by-other-writer");

        new RetentionSensitiveBundleStore(fixture.Reopen()).Recover();

        Assert.True(File.Exists(temporary));
        Assert.Equal("retention_ownership_mismatch", fixture.Scalar<string>("SELECT error_code FROM retention_file_capture_reservations WHERE capture_id=$id", captureId));
    }

    [Fact]
    public void LegacyBundle_ReparseReplacementTempIsPreservedAndBlocked()
    {
        using var fixture = Fixture.Create();
        var root = Path.Combine(fixture.Root, "reparse-temp");
        CreateLegacyBundle(root);
        var crashing = new RetentionSensitiveBundleStore(fixture.Reopen(), phase => { if (phase == "legacy_after_staging_to_final_move") throw new RetentionLegacyMigrationCrashException(); });
        Assert.Throws<RetentionLegacyMigrationCrashException>(() => crashing.AdoptLegacyBundles(root));
        var final = Assert.Single(Directory.EnumerateDirectories(root));
        var captureId = Path.GetFileName(final)!;
        var target = Path.Combine(fixture.Root, "unrelated-temp-target"); File.WriteAllText(target, "keep");
        var temporary = Path.Combine(final, $".manifest.retention.{captureId}.tmp");
        File.CreateSymbolicLink(temporary, target);

        new RetentionSensitiveBundleStore(fixture.Reopen()).Recover();

        Assert.True(File.Exists(target));
        Assert.True(File.Exists(temporary));
        Assert.Equal("retention_ownership_mismatch", fixture.Scalar<string>("SELECT error_code FROM retention_file_capture_reservations WHERE capture_id=$id", captureId));
    }

    [Fact]
    public void LegacyBundle_ManifestOverOneMiBIsBlockedBeforeRead()
    {
        using var fixture = Fixture.Create();
        var root = Path.Combine(fixture.Root, "large-manifest");
        CreateLegacyBundle(root);
        using (var stream = new FileStream(Path.Combine(root, "manifest.json"), FileMode.Open, FileAccess.Write, FileShare.None)) stream.SetLength(1024 * 1024 + 1);

        Assert.Equal(RetentionSensitiveBundleStore.LegacyAdoptionOutcome.Blocked, new RetentionSensitiveBundleStore(fixture.Reopen()).AdoptLegacyBundles(root));
        Assert.True(File.Exists(Path.Combine(root, "manifest.json")));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_file_capture_reservations", "unused"));
    }

    [Fact]
    public void LegacyBundle_RejectsMoreThan256TotalMembersBeforeReservation()
    {
        using var fixture = Fixture.Create();
        var root = Path.Combine(fixture.Root, "too-many-members");
        CreateLegacyBundle(root, 254);

        Assert.Equal(RetentionSensitiveBundleStore.LegacyAdoptionOutcome.Blocked, new RetentionSensitiveBundleStore(fixture.Reopen()).AdoptLegacyBundles(root));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_file_capture_reservations", "unused"));
        Assert.True(File.Exists(Path.Combine(root, "evidence", "e253.json")));
    }

    [Fact]
    public void LegacyBundle_EvidenceReplacementAfterPreflightBlocksWithoutFilesystemMove()
    {
        using var fixture = Fixture.Create();
        var root = Path.Combine(fixture.Root, "evidence-toctou");
        CreateLegacyBundle(root);
        var evidence = Path.Combine(root, "evidence", "diagcand-0001.json");
        var replacement = "replacement-with-a-different-length";
        var store = new RetentionSensitiveBundleStore(fixture.Reopen(), phase => { if (phase == "legacy_evidence_preflight_complete") File.WriteAllText(evidence, replacement); });

        Assert.Equal(RetentionSensitiveBundleStore.LegacyAdoptionOutcome.Blocked, store.AdoptLegacyBundles(root));

        Assert.Equal(replacement, File.ReadAllText(evidence));
        Assert.True(File.Exists(Path.Combine(root, "manifest.json")));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_legacy_bundle_journal", "unused"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items", "unused"));
        Assert.DoesNotContain(Directory.EnumerateDirectories(fixture.Root), path => Path.GetFileName(path)!.Contains("legacy-staging", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LegacyBundle_ConcurrentClaimLoserLeavesNoBlockerOrReservation()
    {
        using var fixture = Fixture.Create();
        var root = Path.Combine(fixture.Root, "concurrent-legacy");
        CreateLegacyBundle(root);
        using var claimed = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var winner = new RetentionSensitiveBundleStore(fixture.Reopen(), phase =>
        {
            if (phase != "legacy_atomic_journal_claimed") return;
            claimed.Set();
            release.Wait(timeout.Token);
        });
        var winnerTask = Task.Run(() => winner.AdoptLegacyBundles(root), timeout.Token);
        Assert.True(claimed.Wait(TimeSpan.FromSeconds(10)));

        var loser = new RetentionSensitiveBundleStore(fixture.Reopen()).AdoptLegacyBundles(root);

        Assert.Equal(RetentionSensitiveBundleStore.LegacyAdoptionOutcome.Blocked, loser);
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_legacy_bundle_blockers", "unused"));
        Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_file_capture_reservations", "unused"));
        Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_legacy_bundle_journal", "unused"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_file_capture_members WHERE capture_id <> (SELECT capture_id FROM retention_legacy_bundle_journal)", "unused"));
        Assert.True(Directory.Exists(root));

        release.Set();
        Assert.Equal(RetentionSensitiveBundleStore.LegacyAdoptionOutcome.AdoptedOrRecovered, await winnerTask.WaitAsync(timeout.Token));
        new RetentionSensitiveBundleStore(fixture.Reopen()).Recover();
        Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='sensitive_bundle'", "unused"));
        Assert.Equal(RetentionSensitiveBundleStore.LegacyAdoptionOutcome.NoLegacy, new RetentionSensitiveBundleStore(fixture.Reopen()).AdoptLegacyBundles(root));
    }

    private static void CreateLegacyBundle(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "evidence"));
        File.WriteAllText(Path.Combine(root, "evidence", "diagcand-0001.json"), "{\"raw\":\"synthetic\"}");
        var created = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        File.WriteAllText(Path.Combine(root, "manifest.json"), $$"""{"schema_version":1,"bundle_id":"{{Path.GetFileName(root)}}","created_at_utc":"{{created:O}}","expires_at_utc":"{{created.AddDays(7):O}}","generated_by_command":"generate-diagnosis-candidates","source_inputs":[],"content_included":true,"delete_target_paths":["{{root.Replace("\\", "\\\\")}}"],"evidence_index":[{"evidence_ref":"bundle:x:diagcand-0001","diagnosis_candidate_id":"diagcand-0001","evidence_file":"evidence/diagcand-0001.json","content_kinds":["prompt"],"fragment_count":1}]}""");
    }

    private static void CreateLegacyBundle(string root, int evidenceCount)
    {
        Directory.CreateDirectory(Path.Combine(root, "evidence"));
        for (var index = 0; index < evidenceCount; index++) File.WriteAllText(Path.Combine(root, "evidence", $"e{index:000}.json"), "{}");
        var created = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var entries = string.Join(',', Enumerable.Range(0, evidenceCount).Select(index => $$"""{"evidence_file":"evidence/e{{index:000}}.json"}"""));
        File.WriteAllText(Path.Combine(root, "manifest.json"), $"{{\"schema_version\":1,\"bundle_id\":\"{Path.GetFileName(root)}\",\"created_at_utc\":\"{created:O}\",\"expires_at_utc\":\"{created.AddDays(7):O}\",\"generated_by_command\":\"generate-diagnosis-candidates\",\"content_included\":true,\"delete_target_paths\":[\"{root.Replace("\\", "\\\\")}\"],\"evidence_index\":[{entries}]}}");
    }

    [Fact]
    public void BundleCapture_CreatesOnlyTheGeneratedChildAndCompletesCatalog()
    {
        using var fixture = Fixture.Create();
        var parent = Path.Combine(fixture.Root, "bundle-parent");

        var result = new RetentionSensitiveBundleStore(fixture.Catalog).Capture(
            [new SensitiveBundlePlanCandidate("candidate-1", null, [new RawEvidenceFragment("prompt", "private-locator", "private-path", "raw-value")])],
            [new SensitiveBundleSourceInput("private-source", new string('a', 64), "raw-otlp")],
            parent);

        Assert.True(Directory.Exists(result.FinalPath));
        Assert.Equal(Path.Combine(parent, result.BundleId), result.FinalPath);
        Assert.Single(Directory.EnumerateDirectories(parent));
        Assert.NotNull(fixture.Catalog.Find(new(fixture.Catalog.StoreInstanceId, RetentionStoreKind.SensitiveBundle, result.BundleId)));
        Assert.Equal(typeof(RetentionSensitiveBundleCaptureResult).FullName, result.ToString());
    }

    [Fact]
    public void BundleCapture_RecoversEachJournalPhase()
    {
        using var fixture = Fixture.Create();
        foreach (var crashPhase in new[] { "member_0_verified_before_cursor", "published_intent_before_move", "moved_before_completion" })
        {
            var parent = Path.Combine(fixture.Root, crashPhase);
            var store = new RetentionSensitiveBundleStore(fixture.Reopen(), phase =>
            {
                if (phase == crashPhase) throw new InvalidOperationException("crash");
            });

            Assert.Throws<InvalidOperationException>(() => store.Capture(Candidates(), Sources(), parent));
            Assert.Single(Directory.EnumerateDirectories(parent));
            var captureId = store.LastCaptureId!;

            new RetentionSensitiveBundleStore(fixture.Reopen()).Recover();

            if (crashPhase == "member_0_verified_before_cursor")
            {
                Assert.Empty(Directory.EnumerateDirectories(parent));
                Assert.Equal("staging", fixture.Scalar<string>("SELECT phase FROM retention_file_capture_reservations WHERE capture_id=$id", captureId));
                Assert.Equal("retention_capture_incomplete", fixture.Scalar<string>("SELECT error_code FROM retention_file_capture_reservations WHERE capture_id=$id", captureId));
                Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE source_item_id=$id", captureId));
            }
            else
            {
                Assert.Single(Directory.EnumerateDirectories(parent));
                Assert.Equal("complete", fixture.Scalar<string>("SELECT phase FROM retention_file_capture_reservations WHERE capture_id=$id", captureId));
                Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE source_item_id=$id", captureId));
            }
        }
        Assert.Empty(fixture.Reopen().FindIncompleteSensitiveBundles(256));
    }

    [Fact]
    public void BundleCapture_PartialStagingNeverPublishesOrCompletes()
    {
        using var fixture = Fixture.Create();
        var parent = Path.Combine(fixture.Root, "bundle-parent");
        var store = new RetentionSensitiveBundleStore(fixture.Catalog, phase =>
        {
            if (phase == "member_0_verified_before_cursor") throw new InvalidOperationException("crash");
        });

        Assert.Throws<InvalidOperationException>(() => store.Capture(Candidates(), Sources(), parent));
        new RetentionSensitiveBundleStore(fixture.Reopen()).Recover();

        Assert.Empty(Directory.EnumerateDirectories(parent));
        Assert.Null(fixture.Reopen().Find(new(fixture.Catalog.StoreInstanceId, RetentionStoreKind.SensitiveBundle, "00000000000000000000000000000000")));
    }

    [Fact]
    public void BundleCapture_UsesExactDefaultAndCustomParentsAndPreservesCallerRaw()
    {
        using var fixture = Fixture.Create();
        var raw = Path.Combine(fixture.Root, "caller-raw.json");
        var bytes = "caller raw bytes"u8.ToArray();
        File.WriteAllBytes(raw, bytes);
        var custom = Path.Combine(fixture.Root, "custom-parent");
        var customResult = new RetentionSensitiveBundleStore(fixture.Catalog).Capture(Candidates(), [new SensitiveBundleSourceInput(raw, new string('a', 64), "raw-otlp")], custom);
        var defaultResult = new RetentionSensitiveBundleStore(fixture.Reopen()).Capture(Candidates(), Sources());

        Assert.Equal(Path.Combine(custom, customResult.BundleId), customResult.FinalPath);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "copilot-agent-observability", "sensitive-bundles", defaultResult.BundleId), defaultResult.FinalPath);
        Assert.Equal(bytes, File.ReadAllBytes(raw));
        Directory.Delete(defaultResult.FinalPath, recursive: true);
    }

    [Theory]
    [InlineData("before_staging_directory_create")]
    [InlineData("before_directory_member_1_create")]
    public void BundleCapture_CollisionIsNotAdopted(string phase)
    {
        using var fixture = Fixture.Create();
        var parent = Path.Combine(fixture.Root, "collision-parent");
        RetentionSensitiveBundleStore? store = null;
        store = new RetentionSensitiveBundleStore(fixture.Catalog, checkpoint =>
        {
            if (checkpoint != phase) return;
            var snapshot = fixture.Reopen().LoadIncompleteSensitiveBundle(store!.LastCaptureId!)!;
            var collision = phase == "before_staging_directory_create" ? snapshot.StagingLocator : Path.Combine(snapshot.StagingLocator, "evidence");
            Directory.CreateDirectory(collision);
            File.WriteAllText(Path.Combine(collision, "collision.txt"), "preserve");
        });

        Assert.Throws<InvalidOperationException>(() => store.Capture(Candidates(), Sources(), parent));
        var row = fixture.Reopen().LoadIncompleteSensitiveBundle(store.LastCaptureId!)!;
        Assert.True(Directory.Exists(phase == "before_staging_directory_create" ? row.StagingLocator : Path.Combine(row.StagingLocator, "evidence")));
        var collision = phase == "before_staging_directory_create" ? row.StagingLocator : Path.Combine(row.StagingLocator, "evidence");
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(collision, "collision.txt")));
        Assert.Equal("retention_ownership_mismatch", fixture.Scalar<string>("SELECT error_code FROM retention_file_capture_reservations WHERE capture_id=$id", store.LastCaptureId!));
    }

    [Theory]
    [InlineData(".retention-owner.v1")]
    [InlineData("manifest.json")]
    [InlineData("unexpected.txt")]
    public void Recovery_PreservesReplacedOrUnexpectedStagingAndRecordsOwnershipMismatch(string relative)
    {
        using var fixture = Fixture.Create();
        var parent = Path.Combine(fixture.Root, "replacement-parent");
        var store = new RetentionSensitiveBundleStore(fixture.Catalog, phase => { if (phase == "published_intent_before_move") throw new InvalidOperationException("crash"); });
        Assert.Throws<InvalidOperationException>(() => store.Capture(Candidates(), Sources(), parent));
        var snapshot = Assert.Single(fixture.Reopen().FindIncompleteSensitiveBundles(256));
        File.WriteAllText(Path.Combine(snapshot.StagingLocator, relative), "replacement");

        new RetentionSensitiveBundleStore(fixture.Reopen()).Recover();

        Assert.True(File.Exists(Path.Combine(snapshot.StagingLocator, relative)));
        Assert.Equal("retention_ownership_mismatch", fixture.Scalar<string>("SELECT error_code FROM retention_file_capture_reservations WHERE capture_id=$id", snapshot.CaptureId));
    }

    [Fact]
    public void Recovery_IsIdempotentAndRetainsUnrelatedSibling()
    {
        using var fixture = Fixture.Create();
        var parent = Path.Combine(fixture.Root, "parent");
        Directory.CreateDirectory(parent);
        var sibling = Path.Combine(parent, "unrelated"); Directory.CreateDirectory(sibling); File.WriteAllText(Path.Combine(sibling, "keep.txt"), "keep");
        var store = new RetentionSensitiveBundleStore(fixture.Catalog, phase => { if (phase == "member_0_verified_before_cursor") throw new InvalidOperationException("crash"); });
        Assert.Throws<InvalidOperationException>(() => store.Capture(Candidates(), Sources(), parent));

        var recovery = new RetentionSensitiveBundleStore(fixture.Reopen()); recovery.Recover(); recovery.Recover();

        Assert.True(File.Exists(Path.Combine(sibling, "keep.txt")));
    }

    [Fact]
    public void BundleCapture_ExactLimitRecordsFixedBlockerWithoutLeakingRaw()
    {
        using var fixture = Fixture.Create();
        var raw = "PRIVATE_RAW_VALUE";
        var candidates = Enumerable.Range(0, 254).Select(index => new SensitiveBundlePlanCandidate($"candidate-{index:000}", null, [new RawEvidenceFragment("prompt", "private", "private", raw)])).ToArray();
        var store = new RetentionSensitiveBundleStore(fixture.Catalog);

        var error = Assert.Throws<ArgumentException>(() => store.Capture(candidates, Sources(), Path.Combine(fixture.Root, "limit")));

        Assert.Equal("Sensitive bundle capture failed.", error.Message);
        Assert.DoesNotContain(raw, error.ToString(), StringComparison.Ordinal);
        Assert.Equal("retention_item_limit_exceeded", fixture.Scalar<string>("SELECT error_code FROM retention_file_capture_reservations WHERE capture_id=$id", store.LastCaptureId!));
    }

    [Fact]
    public void Recovery_PartialCleanupDeletesOwnerMarkerLast()
    {
        using var fixture = Fixture.Create();
        var parent = Path.Combine(fixture.Root, "cleanup");
        var crashing = new RetentionSensitiveBundleStore(fixture.Catalog, phase => { if (phase == "member_0_verified_before_cursor") throw new InvalidOperationException("crash"); });
        Assert.Throws<InvalidOperationException>(() => crashing.Capture(Candidates(), Sources(), parent));
        var deleted = new List<string>();

        new RetentionSensitiveBundleStore(fixture.Reopen(), phase => { if (phase.StartsWith("cleanup_before_delete_", StringComparison.Ordinal)) deleted.Add(phase); }).Recover();

        Assert.Equal(["cleanup_before_delete_.retention-owner.v1"], deleted);
    }

    [Fact]
    public void BundleCapture_ReparseStagingMemberIsPreservedAndNeverFollowed()
    {
        using var fixture = Fixture.Create();
        var parent = Path.Combine(fixture.Root, "reparse-parent");
        var target = Path.Combine(fixture.Root, "unrelated-target");
        Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        RetentionSensitiveBundleStore? store = null;
        store = new RetentionSensitiveBundleStore(fixture.Catalog, phase =>
        {
            if (phase != "before_directory_member_1_create") return;
            var snapshot = fixture.Reopen().LoadIncompleteSensitiveBundle(store!.LastCaptureId!)!;
            Directory.CreateSymbolicLink(Path.Combine(snapshot.StagingLocator, "evidence"), target);
        });

        Assert.Throws<InvalidOperationException>(() => store.Capture(Candidates(), Sources(), parent));
        new RetentionSensitiveBundleStore(fixture.Reopen()).Recover();

        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
        Assert.Equal("retention_ownership_mismatch", fixture.Scalar<string>("SELECT error_code FROM retention_file_capture_reservations WHERE capture_id=$id", store.LastCaptureId!));
    }

    private static IReadOnlyList<SensitiveBundlePlanCandidate> Candidates() =>
        [new("candidate-1", null, [new RawEvidenceFragment("prompt", "private-locator", "private-path", "raw-value")])];
    private static IReadOnlyList<SensitiveBundleSourceInput> Sources() =>
        [new("private-source", new string('a', 64), "raw-otlp")];

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, string database, RetentionCatalogStore catalog) => (Root, Database, Catalog) = (root, database, catalog);
        internal string Root { get; }
        internal string Database { get; }
        internal RetentionCatalogStore Catalog { get; }
        internal RetentionCatalogStore Reopen() => new(RetentionCatalogContext.AdoptExistingCatalogV1(Database));
        internal T Scalar<T>(string sql, string captureId)
        {
            using var connection = new SqliteConnection($"Data Source={Database};Pooling=False"); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = sql; command.Parameters.AddWithValue("$id", captureId);
            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
        }

        internal static Fixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"retention-sensitive-bundle-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var database = Path.Combine(root, "catalog.sqlite");
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(database);
            return new(root, database, new RetentionCatalogStore(context));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
