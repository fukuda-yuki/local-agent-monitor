using System.Security.Cryptography;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class SensitiveBundleDeletionCatalogTests
{
    [Fact]
    public async Task MissingFinalChild_BeforeFirstIntentRecordsZeroAttemptTerminalFailure()
    {
        using var fixture = await Fixture.CreateAsync(createFinalChild: false);

        var result = await fixture.Catalog.EnsureDeleteIntentAsync(fixture.Claim.Fence, 0, fixture.Now, CancellationToken.None);

        Assert.Equal(RetentionIntentDisposition.TerminalFailureRecorded, result.Disposition);
        Assert.Equal(0, result.AttemptNumber);
        Assert.Equal("retention_unexpected_source_missing", fixture.Scalar<string>("SELECT error_code FROM retention_items WHERE item_id=$id"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT attempt_count FROM retention_items WHERE item_id=$id"));
    }

    [Fact]
    public async Task ExactCompletedBundle_AfterIntentLoadsImmutableOrderedDeletionPlan()
    {
        using var fixture = await Fixture.CreateAsync(createFinalChild: true);
        Assert.Equal(RetentionIntentDisposition.Committed, (await fixture.Catalog.EnsureDeleteIntentAsync(fixture.Claim.Fence, 0, fixture.Now, CancellationToken.None)).Disposition);
        var context = new RetentionDeleteContext(fixture.Claim.Fence.ItemId, fixture.Claim.StoreInstanceId, fixture.Claim.StoreKind, fixture.Claim.Fence.ExpectedRevision, fixture.Claim.Fence.LeaseOwner, fixture.Claim.Fence.LeaseGeneration, fixture.Claim.SourceIdentity, fixture.Claim.PrivateLocator, 0, CancellationToken.None);

        var result = fixture.Catalog.LoadSensitiveBundleDeletionPlan(context, fixture.Now);

        Assert.Equal(RetentionSensitiveBundleDeletionPlanDisposition.Ready, result.Disposition);
        var plan = Assert.IsType<RetentionSensitiveBundleDeletionPlan>(result.Plan);
        Assert.Equal(new[] { "raw.json", "manifest.json", RetentionFileCaptureContracts.OwnerMarkerName }, plan.Members.Select(member => member.RelativePath));
        Assert.Equal(0, plan.Cursor);
        Assert.DoesNotContain(fixture.Root, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root, plan.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("final-file", "retention_ownership_mismatch")]
    [InlineData("marker-missing", "retention_unexpected_source_missing")]
    [InlineData("marker-replaced", "retention_ownership_mismatch")]
    [InlineData("manifest-missing", "retention_unexpected_source_missing")]
    [InlineData("manifest-replaced", "retention_ownership_mismatch")]
    [InlineData("receipt-replaced", "retention_ownership_mismatch")]
    [InlineData("private-locator-replaced", "retention_ownership_mismatch")]
    public async Task PreIntentEvidenceFailure_IsTerminalWithoutAttempt(string mutation, string expectedError)
    {
        using var fixture = await Fixture.CreateAsync(createFinalChild: true);
        fixture.Mutate(mutation);

        var result = await fixture.Catalog.EnsureDeleteIntentAsync(fixture.Claim.Fence, 0, fixture.Now, CancellationToken.None);

        Assert.Equal(RetentionIntentDisposition.TerminalFailureRecorded, result.Disposition);
        Assert.Equal(expectedError, fixture.Scalar<string>("SELECT error_code FROM retention_items WHERE item_id=$id"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT attempt_count FROM retention_items WHERE item_id=$id"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_delete_journal WHERE item_id=$id"));
    }

    [Fact]
    public async Task ForgedPrivateLocator_ContextCannotLoadPlan()
    {
        using var fixture = await Fixture.CreateAsync(createFinalChild: true);
        Assert.Equal(RetentionIntentDisposition.Committed, (await fixture.Catalog.EnsureDeleteIntentAsync(fixture.Claim.Fence, 0, fixture.Now, CancellationToken.None)).Disposition);
        var context = fixture.Context() with { PrivateLocator = new RetentionPrivateLocatorHandle("forged") };

        Assert.Equal(RetentionSensitiveBundleDeletionPlanDisposition.LeaseLost, fixture.Catalog.LoadSensitiveBundleDeletionPlan(context, fixture.Now).Disposition);
    }

    [Theory]
    [InlineData("store")]
    [InlineData("source")]
    [InlineData("revision")]
    [InlineData("owner")]
    [InlineData("generation")]
    [InlineData("cursor")]
    public async Task ForgedDeletionFence_ContextCannotLoadPlan(string field)
    {
        using var fixture = await Fixture.CreateAsync(createFinalChild: true);
        Assert.Equal(RetentionIntentDisposition.Committed, (await fixture.Catalog.EnsureDeleteIntentAsync(fixture.Claim.Fence, 0, fixture.Now, CancellationToken.None)).Disposition);
        var context = field switch
        {
            "store" => fixture.Context() with { StoreInstanceId = "00000000000000000000000000000000" },
            "source" => fixture.Context() with { SourceIdentity = new RetentionSourceIdentity("00000000000000000000000000000000", fixture.Claim.SourceIdentity.OwnershipReceipt) },
            "revision" => fixture.Context() with { ExpectedRevision = fixture.Claim.Fence.ExpectedRevision + 1 },
            "owner" => fixture.Context() with { LeaseOwner = "forged" },
            "generation" => fixture.Context() with { LeaseGeneration = fixture.Claim.Fence.LeaseGeneration + 1 },
            "cursor" => fixture.Context() with { IntentCursor = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        Assert.Equal(RetentionSensitiveBundleDeletionPlanDisposition.LeaseLost, fixture.Catalog.LoadSensitiveBundleDeletionPlan(context, fixture.Now).Disposition);
    }

    [Fact]
    public async Task ReparseFinalChild_BeforeFirstIntentIsOwnershipMismatch()
    {
        using var fixture = await Fixture.CreateAsync(createFinalChild: true);
        var final = fixture.Scalar<string>("SELECT private_locator FROM retention_items WHERE item_id=$id");
        var target = Path.Combine(fixture.Root, "other");
        Directory.CreateDirectory(target);
        Directory.Delete(final, true);
        Directory.CreateSymbolicLink(final, target);

        var result = await fixture.Catalog.EnsureDeleteIntentAsync(fixture.Claim.Fence, 0, fixture.Now, CancellationToken.None);

        Assert.Equal(RetentionIntentDisposition.TerminalFailureRecorded, result.Disposition);
        Assert.Equal("retention_ownership_mismatch", fixture.Scalar<string>("SELECT error_code FROM retention_items WHERE item_id=$id"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT attempt_count FROM retention_items WHERE item_id=$id"));
    }

    [Fact]
    public async Task LargeMismatchedRawMember_IsNotReadBeforeIntentOrPlanLoad()
    {
        using var fixture = await Fixture.CreateAsync(createFinalChild: true);
        var final = fixture.Scalar<string>("SELECT private_locator FROM retention_items WHERE item_id=$id");
        File.WriteAllBytes(Path.Combine(final, "raw.json"), new byte[2 * 1024 * 1024]);

        Assert.Equal(RetentionIntentDisposition.Committed, (await fixture.Catalog.EnsureDeleteIntentAsync(fixture.Claim.Fence, 0, fixture.Now, CancellationToken.None)).Disposition);
        Assert.Equal(RetentionSensitiveBundleDeletionPlanDisposition.Ready, fixture.Catalog.LoadSensitiveBundleDeletionPlan(fixture.Context(), fixture.Now).Disposition);
    }

    [Fact]
    public async Task MalformedReservationPlan_BeforeFirstIntentIsTerminalOwnershipMismatch()
    {
        using var fixture = await Fixture.CreateAsync(createFinalChild: true);
        fixture.Execute("UPDATE retention_file_capture_members SET deletion_order=99 WHERE capture_id=(SELECT source_item_id FROM retention_items WHERE item_id=$id) AND relative_path='manifest.json'", ("$id", fixture.ItemId));

        var result = await fixture.Catalog.EnsureDeleteIntentAsync(fixture.Claim.Fence, 0, fixture.Now, CancellationToken.None);

        Assert.Equal(RetentionIntentDisposition.TerminalFailureRecorded, result.Disposition);
        Assert.Equal("retention_ownership_mismatch", fixture.Scalar<string>("SELECT error_code FROM retention_items WHERE item_id=$id"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT attempt_count FROM retention_items WHERE item_id=$id"));
    }

    [Fact]
    public async Task ReopenedCatalog_LoadsTheSameCompletedPlanAndExpiredLeaseIsLost()
    {
        using var fixture = await Fixture.CreateAsync(createFinalChild: true);
        Assert.Equal(RetentionIntentDisposition.Committed, (await fixture.Catalog.EnsureDeleteIntentAsync(fixture.Claim.Fence, 0, fixture.Now, CancellationToken.None)).Disposition);
        var reopened = new RetentionCatalogStore(RetentionCatalogContext.AdoptExistingCatalogV1(fixture.DatabasePath), new MutableTimeProvider(fixture.Now));

        Assert.Equal(RetentionSensitiveBundleDeletionPlanDisposition.Ready, reopened.LoadSensitiveBundleDeletionPlan(fixture.Context(), fixture.Now).Disposition);
        Assert.Equal(RetentionSensitiveBundleDeletionPlanDisposition.LeaseLost, reopened.LoadSensitiveBundleDeletionPlan(fixture.Context(), fixture.Now + RetentionV1Constants.LeaseDuration).Disposition);
    }

    [Theory]
    [InlineData("manifest.json", 2)]
    [InlineData(".retention-owner.v1", 3)]
    public async Task RestartAfterCommittedIntent_UsesDbPlanWhenPreviouslyDeletedMemberIsAbsent(string member, int cursor)
    {
        using var fixture = await Fixture.CreateAsync(createFinalChild: true);
        Assert.Equal(RetentionIntentDisposition.Committed, (await fixture.Catalog.EnsureDeleteIntentAsync(fixture.Claim.Fence, 0, fixture.Now, CancellationToken.None)).Disposition);
        await fixture.AdvanceCursorAsync(cursor);
        File.Delete(Path.Combine(fixture.FinalLocator, member));
        var reopened = new RetentionCatalogStore(RetentionCatalogContext.AdoptExistingCatalogV1(fixture.DatabasePath), new MutableTimeProvider(fixture.Now));

        var result = reopened.LoadSensitiveBundleDeletionPlan(fixture.Context(cursor), fixture.Now);

        Assert.Equal(RetentionSensitiveBundleDeletionPlanDisposition.Ready, result.Disposition);
        Assert.Equal(cursor, Assert.IsType<RetentionSensitiveBundleDeletionPlan>(result.Plan).Cursor);
    }

    [Fact]
    public async Task RestartAtFinalChildCursor_UsesDbPlanWhenFinalChildIsAbsent()
    {
        using var fixture = await Fixture.CreateAsync(createFinalChild: true);
        Assert.Equal(RetentionIntentDisposition.Committed, (await fixture.Catalog.EnsureDeleteIntentAsync(fixture.Claim.Fence, 0, fixture.Now, CancellationToken.None)).Disposition);
        await fixture.AdvanceCursorAsync(5);
        Directory.Delete(fixture.FinalLocator, true);
        var reopened = new RetentionCatalogStore(RetentionCatalogContext.AdoptExistingCatalogV1(fixture.DatabasePath), new MutableTimeProvider(fixture.Now));

        Assert.Equal(RetentionSensitiveBundleDeletionPlanDisposition.Ready, reopened.LoadSensitiveBundleDeletionPlan(fixture.Context(5), fixture.Now).Disposition);
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, string databasePath, DateTimeOffset now, RetentionCatalogStore catalog, RetentionDeletionClaim claim, string itemId)
            => (Root, DatabasePath, Now, Catalog, Claim, ItemId) = (root, databasePath, now, catalog, claim, itemId);

        internal string Root { get; }
        internal string DatabasePath { get; }
        internal DateTimeOffset Now { get; }
        internal RetentionCatalogStore Catalog { get; }
        internal RetentionDeletionClaim Claim { get; }
        internal string ItemId { get; }
        internal string FinalLocator => Scalar<string>("SELECT private_locator FROM retention_items WHERE item_id=$id");

        internal RetentionDeleteContext Context(int cursor = 0) => new(Claim.Fence.ItemId, Claim.StoreInstanceId, Claim.StoreKind, Claim.Fence.ExpectedRevision, Claim.Fence.LeaseOwner, Claim.Fence.LeaseGeneration, Claim.SourceIdentity, Claim.PrivateLocator, cursor, CancellationToken.None);
        internal async Task AdvanceCursorAsync(int target)
        {
            for (var cursor = 0; cursor < target; cursor++) Assert.Equal(RetentionMutationDisposition.Applied, await Catalog.TryAdvanceDeleteCursorAsync(Claim.Fence, cursor, cursor + 1, Now, CancellationToken.None));
        }

        internal static async Task<Fixture> CreateAsync(bool createFinalChild)
        {
            var root = Path.Combine(Path.GetTempPath(), $"sensitive-bundle-delete-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "catalog.sqlite");
            var now = new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);
            var time = new MutableTimeProvider(now);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(databasePath, time);
            var catalog = new RetentionCatalogStore(context, time);
            var reservation = catalog.ReserveSensitiveBundle(root);
            var marker = RetentionFileCaptureOwnershipMarker.Create(reservation.StoreInstanceId, reservation.CaptureId, reservation.ReservedAtText, reservation.ReservedAtTicks, reservation.OwnerToken);
            var manifest = "{}"u8.ToArray();
            var markerDigest = SHA256.HashData(marker);
            var manifestDigest = SHA256.HashData(manifest);
            var plan = new RetentionFileCapturePlanInput(markerDigest, manifestDigest,
            [
                new(0, RetentionFileCaptureContracts.OwnerMarkerName, RetentionFileCaptureMemberKind.OwnerMarker, null, null, 2),
                new(1, "manifest.json", RetentionFileCaptureMemberKind.File, manifest.Length, manifestDigest, 1),
                new(2, "raw.json", RetentionFileCaptureMemberKind.File, 1, SHA256.HashData("x"u8), 0)
            ]);
            Assert.Equal(RetentionCaptureMutationDisposition.Applied, catalog.PlanSensitiveBundle(reservation, plan));
            for (var cursor = 0; cursor != 3; cursor++) Assert.Equal(RetentionCaptureMutationDisposition.Applied, catalog.AdvanceSensitiveBundleCursor(reservation, cursor));
            Assert.Equal(RetentionCaptureMutationDisposition.Applied, catalog.TransitionSensitiveBundle(reservation, RetentionCapturePhase.Staging, RetentionCapturePhase.PublishedPendingCatalog, 3));
            if (createFinalChild)
            {
                Directory.CreateDirectory(reservation.FinalLocator);
                File.WriteAllBytes(Path.Combine(reservation.FinalLocator, RetentionFileCaptureContracts.OwnerMarkerName), marker);
                File.WriteAllBytes(Path.Combine(reservation.FinalLocator, "manifest.json"), manifest);
                File.WriteAllText(Path.Combine(reservation.FinalLocator, "raw.json"), "x");
            }
            Assert.Equal(RetentionCaptureMutationDisposition.Applied, catalog.CompleteSensitiveBundle(reservation, markerDigest, manifestDigest));
            Execute(databasePath, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");
            var itemId = Scalar<string>(databasePath, "SELECT item_id FROM retention_items WHERE source_item_id=$source", ("$source", reservation.CaptureId));
            Execute(databasePath, "UPDATE retention_items SET state='deletion_queued',revision=1,read_denied_at=$now,queued_at=$now WHERE item_id=$id", ("$now", now.ToString("O")), ("$id", itemId));
            var claim = (await catalog.TryClaimDeletionAsync(Assert.Single((await catalog.PrepareCleanupBatchAsync(now, 100, 100, TimeSpan.FromSeconds(30), CancellationToken.None)).Work), "worker", now, CancellationToken.None)).Claim!;
            return new(root, databasePath, now, catalog, claim, itemId);
        }

        internal T Scalar<T>(string sql) => Scalar<T>(DatabasePath, sql, ("$id", ItemId));
        internal void Execute(string sql, params (string Name, object Value)[] parameters) => Execute(DatabasePath, sql, parameters);
        internal void Mutate(string mutation)
        {
            var final = Scalar<string>(DatabasePath, "SELECT private_locator FROM retention_items WHERE item_id=$id", ("$id", ItemId));
            switch (mutation)
            {
                case "final-file": Directory.Delete(final, true); File.WriteAllText(final, "replacement"); break;
                case "marker-missing": File.Delete(Path.Combine(final, RetentionFileCaptureContracts.OwnerMarkerName)); break;
                case "marker-replaced": File.WriteAllText(Path.Combine(final, RetentionFileCaptureContracts.OwnerMarkerName), "replacement"); break;
                case "manifest-missing": File.Delete(Path.Combine(final, "manifest.json")); break;
                case "manifest-replaced": File.WriteAllText(Path.Combine(final, "manifest.json"), "replacement"); break;
                case "receipt-replaced": Execute(DatabasePath, "UPDATE retention_items SET ownership_receipt=zeroblob(32) WHERE item_id=$id", ("$id", ItemId)); break;
                case "private-locator-replaced": Execute(DatabasePath, "UPDATE retention_items SET private_locator='replaced' WHERE item_id=$id", ("$id", ItemId)); break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        }
        private static void Execute(string path, string sql, params (string Name, object Value)[] parameters) { using var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; foreach (var p in parameters) q.Parameters.AddWithValue(p.Name, p.Value); q.ExecuteNonQuery(); }
        private static T Scalar<T>(string path, string sql, params (string Name, object Value)[] parameters) { using var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; foreach (var p in parameters) q.Parameters.AddWithValue(p.Name, p.Value); return (T)q.ExecuteScalar()!; }
        public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
