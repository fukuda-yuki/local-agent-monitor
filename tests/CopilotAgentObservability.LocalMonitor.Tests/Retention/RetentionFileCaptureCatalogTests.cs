using System.Security.Cryptography;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionFileCaptureCatalogTests
{
    [Fact]
    public void ReserveSensitiveBundle_RequiresAdoptedCatalogAndPersistsAnImmutablePlan()
    {
        using var fixture = Fixture.Create();
        var catalog = new RetentionCatalogStore(RetentionCatalogContext.AdoptExistingCatalogV1(fixture.Path), fixture.Time);

        var reservation = catalog.ReserveSensitiveBundle("C:/private-parent");
        Assert.Equal(RetentionCaptureMutationDisposition.Applied, catalog.PlanSensitiveBundle(reservation, Fixture.Plan()));

        Assert.Matches("^[0-9a-f]{32}$", reservation.CaptureId);
        Assert.Equal(RetentionCapturePhase.Reserved, reservation.Phase);
        Assert.Equal(3L, fixture.Scalar<long>("SELECT planned_member_count FROM retention_file_capture_reservations WHERE capture_id=$id", reservation.CaptureId));
        Assert.Equal(3L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_file_capture_members WHERE capture_id=$id", reservation.CaptureId));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='sensitive_bundle'", reservation.CaptureId));
        Assert.Equal(typeof(RetentionFileCaptureReservation).FullName, reservation.ToString());
    }

    [Fact]
    public void TransitionSensitiveBundle_UsesPhaseCompareAndSwapAndIsIdempotentAfterCompletion()
    {
        using var fixture = Fixture.Create();
        var catalog = fixture.Catalog;
        var reservation = catalog.ReserveSensitiveBundle("C:/private-parent");

        Assert.Equal(RetentionCaptureMutationDisposition.StaleNoOp, catalog.TransitionSensitiveBundle(reservation, RetentionCapturePhase.Staging, RetentionCapturePhase.PublishedPendingCatalog, 0));
        Assert.Equal(RetentionCaptureMutationDisposition.Applied, catalog.PlanSensitiveBundle(reservation, Fixture.Plan()));
        Assert.Equal(RetentionCaptureMutationDisposition.Applied, catalog.AdvanceSensitiveBundleCursor(reservation, 0));
        Assert.Equal(RetentionCaptureMutationDisposition.Applied, catalog.AdvanceSensitiveBundleCursor(reservation, 1));
        Assert.Equal(RetentionCaptureMutationDisposition.Applied, catalog.AdvanceSensitiveBundleCursor(reservation, 2));
        Assert.Equal(RetentionCaptureMutationDisposition.Applied, catalog.TransitionSensitiveBundle(reservation, RetentionCapturePhase.Staging, RetentionCapturePhase.PublishedPendingCatalog, 3));
        Assert.Equal(RetentionCaptureMutationDisposition.Applied, catalog.CompleteSensitiveBundle(reservation, Fixture.Marker, Fixture.Manifest));
        Assert.Equal(RetentionCaptureMutationDisposition.NoOpAlreadyFinalized, catalog.CompleteSensitiveBundle(reservation, Fixture.Marker, Fixture.Manifest));
        Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE source_item_id=$id", reservation.CaptureId));
        Assert.Equal("2026-07-26T01:02:03.0000000+00:00", fixture.Scalar<string>("SELECT expires_at FROM retention_items WHERE source_item_id=$id", reservation.CaptureId));
        Assert.Equal(reservation.FinalLocator, fixture.Scalar<string>("SELECT private_locator FROM retention_items WHERE source_item_id=$id", reservation.CaptureId));
        Assert.Equal(32L, fixture.Scalar<long>("SELECT length(ownership_receipt) FROM retention_items WHERE source_item_id=$id", reservation.CaptureId));
        Assert.Equal("complete", fixture.Scalar<string>("SELECT phase FROM retention_file_capture_reservations WHERE capture_id=$id", reservation.CaptureId));
        Assert.Equal("complete", fixture.Scalar<string>("SELECT phase FROM retention_capture_journal WHERE item_id=(SELECT item_id FROM retention_items WHERE source_item_id=$id)", reservation.CaptureId));
    }

    [Fact]
    public void CompleteSensitiveBundle_RejectsDigestMismatchWithoutCreatingAReadableItem()
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveSensitiveBundle("C:/private-parent");
        fixture.Catalog.PlanSensitiveBundle(reservation, Fixture.Plan());
        AdvanceAll(fixture.Catalog, reservation);
        fixture.Catalog.TransitionSensitiveBundle(reservation, RetentionCapturePhase.Staging, RetentionCapturePhase.PublishedPendingCatalog, 3);

        Assert.Equal(RetentionCaptureMutationDisposition.Conflict, fixture.Catalog.CompleteSensitiveBundle(reservation, SHA256.HashData("different"u8), Fixture.Manifest));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE source_item_id=$id", reservation.CaptureId));
        Assert.Equal("published_pending_catalog", fixture.Scalar<string>("SELECT phase FROM retention_file_capture_reservations WHERE capture_id=$id", reservation.CaptureId));
    }

    [Fact]
    public void RecoverIncompleteSensitiveBundles_IsBoundedAndSurvivesTwoReopensWithoutLeakingPrivateLocators()
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveSensitiveBundle("C:/private-parent");
        fixture.Catalog.PlanSensitiveBundle(reservation, Fixture.Plan());

        var reopened = new RetentionCatalogStore(RetentionCatalogContext.AdoptExistingCatalogV1(fixture.Path), fixture.Time);
        var snapshot = Assert.Single(reopened.FindIncompleteSensitiveBundles(1));
        var reopenedAgain = new RetentionCatalogStore(RetentionCatalogContext.AdoptExistingCatalogV1(fixture.Path), fixture.Time);
        var second = Assert.Single(reopenedAgain.FindIncompleteSensitiveBundles(1));

        Assert.Equal(reservation.CaptureId, snapshot.CaptureId);
        Assert.Equal(snapshot.CaptureId, second.CaptureId);
        Assert.Equal(3, snapshot.PlannedMemberCount);
        Assert.Equal(RetentionCaptureMutationDisposition.Applied, reopenedAgain.AdvanceSensitiveBundleCursor(second, 0));
        Assert.DoesNotContain("private-parent", snapshot.ToString(), StringComparison.Ordinal);
        Assert.False(
            snapshot.ToString().Contains("private-token", StringComparison.Ordinal),
            "Private capture material reached the catalog snapshot.");
        Assert.Throws<ArgumentOutOfRangeException>(() => reopened.FindIncompleteSensitiveBundles(0));
    }

    [Fact]
    public void PlanSensitiveBundle_RejectsOversizedPlanWithoutMutatingReservedJournal()
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveSensitiveBundle("C:/private-parent");
        var members = Enumerable.Range(0, 257)
            .Select(index => new RetentionFileCaptureMember(index, $"evidence/{index}", RetentionFileCaptureMemberKind.File, 1, SHA256.HashData([(byte)index]), index))
            .ToArray();

        Assert.Equal(RetentionCaptureMutationDisposition.Conflict, fixture.Catalog.PlanSensitiveBundle(reservation, new(Fixture.Marker, Fixture.Manifest, members)));
        Assert.Equal("reserved", fixture.Scalar<string>("SELECT phase FROM retention_file_capture_reservations WHERE capture_id=$id", reservation.CaptureId));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_file_capture_members WHERE capture_id=$id", reservation.CaptureId));

        var bytesReservation = fixture.Catalog.ReserveSensitiveBundle("C:/private-parent");
        var tooLarge = new RetentionFileCaptureMember(0, "evidence/raw", RetentionFileCaptureMemberKind.File, 128L * 1024 * 1024 + 1, SHA256.HashData("raw"u8), 0);
        Assert.Equal(RetentionCaptureMutationDisposition.Conflict, fixture.Catalog.PlanSensitiveBundle(bytesReservation, new(Fixture.Marker, Fixture.Manifest, [tooLarge])));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT planned_total_bytes FROM retention_file_capture_reservations WHERE capture_id=$id", bytesReservation.CaptureId));

        var invalid = fixture.Catalog.ReserveSensitiveBundle("C:/private-parent");
        var wrongMarker = Fixture.Plan().Members.Select(member => member.Kind == RetentionFileCaptureMemberKind.OwnerMarker ? member with { RelativePath = "owner" } : member).ToArray();
        Assert.Equal(RetentionCaptureMutationDisposition.Conflict, fixture.Catalog.PlanSensitiveBundle(invalid, new(Fixture.Marker, Fixture.Manifest, wrongMarker)));
        var wrongManifest = Fixture.Plan().Members.Select(member => member.RelativePath == "manifest.json" ? member with { Sha256 = SHA256.HashData("other"u8) } : member).ToArray();
        Assert.Equal(RetentionCaptureMutationDisposition.Conflict, fixture.Catalog.PlanSensitiveBundle(invalid, new(Fixture.Marker, Fixture.Manifest, wrongManifest)));
        var wrongOrder = Fixture.Plan().Members.Select(member => member.RelativePath == "manifest.json" ? member with { DeletionOrder = 99 } : member).ToArray();
        Assert.Equal(RetentionCaptureMutationDisposition.Conflict, fixture.Catalog.PlanSensitiveBundle(invalid, new(Fixture.Marker, Fixture.Manifest, wrongOrder)));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_file_capture_members WHERE capture_id=$id", invalid.CaptureId));
    }

    [Fact]
    public void PlanSensitiveBundle_AcceptsExact128MiBAndRejectsOneByteOver()
    {
        using var fixture = Fixture.Create();
        var exact = fixture.Catalog.ReserveSensitiveBundle("C:/private-parent");
        var exactMembers = Fixture.Plan().Members.Append(new RetentionFileCaptureMember(3, "evidence/raw", RetentionFileCaptureMemberKind.File, 128L * 1024 * 1024 - 12, SHA256.HashData("raw"u8), 3)).ToArray();
        Assert.Equal(RetentionCaptureMutationDisposition.Applied, fixture.Catalog.PlanSensitiveBundle(exact, new(Fixture.Marker, Fixture.Manifest, exactMembers)));

        var over = fixture.Catalog.ReserveSensitiveBundle("C:/private-parent");
        var overMembers = Fixture.Plan().Members.Append(new RetentionFileCaptureMember(3, "evidence/raw", RetentionFileCaptureMemberKind.File, 128L * 1024 * 1024 - 11, SHA256.HashData("raw"u8), 3)).ToArray();
        Assert.Equal(RetentionCaptureMutationDisposition.Conflict, fixture.Catalog.PlanSensitiveBundle(over, new(Fixture.Marker, Fixture.Manifest, overMembers)));
    }

    [Fact]
    public void AbandonAndBlocker_AreExactAndDoNotCreateLifecycleItems()
    {
        using var fixture = Fixture.Create();
        var abandoned = fixture.Catalog.ReserveSensitiveBundle("C:/private-parent");
        Assert.Equal(RetentionCaptureMutationDisposition.Applied, fixture.Catalog.AbandonReservedSensitiveBundle(abandoned));
        Assert.Equal(RetentionCaptureMutationDisposition.StaleNoOp, fixture.Catalog.RecordSensitiveBundleBlocker(abandoned, RetentionErrorCode.CaptureIncomplete));

        var blocked = fixture.Catalog.ReserveSensitiveBundle("C:/private-parent");
        Assert.Equal(RetentionCaptureMutationDisposition.Applied, fixture.Catalog.RecordSensitiveBundleBlocker(blocked, RetentionErrorCode.CaptureIncomplete));
        Assert.Equal("retention_capture_incomplete", fixture.Scalar<string>("SELECT error_code FROM retention_file_capture_reservations WHERE capture_id=$id", blocked.CaptureId));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE source_item_id=$id", blocked.CaptureId));
    }

    [Fact]
    public void CursorAndPlan_AreCompareAndSwapBoundedAndImmutable()
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveSensitiveBundle("C:/private-parent");
        Assert.Equal(RetentionCaptureMutationDisposition.Applied, fixture.Catalog.PlanSensitiveBundle(reservation, Fixture.Plan()));
        Assert.Equal(RetentionCaptureMutationDisposition.StaleNoOp, fixture.Catalog.TransitionSensitiveBundle(reservation, RetentionCapturePhase.Staging, RetentionCapturePhase.PublishedPendingCatalog, 3));
        Assert.Equal(RetentionCaptureMutationDisposition.StaleNoOp, fixture.Catalog.AdvanceSensitiveBundleCursor(reservation, 1));
        AdvanceAll(fixture.Catalog, reservation);
        Assert.Equal(RetentionCaptureMutationDisposition.StaleNoOp, fixture.Catalog.AdvanceSensitiveBundleCursor(reservation, 3));
        Assert.Equal(RetentionCaptureMutationDisposition.StaleNoOp, fixture.Catalog.PlanSensitiveBundle(reservation, Fixture.Plan()));
        Assert.Equal(RetentionCaptureMutationDisposition.Applied, fixture.Catalog.TransitionSensitiveBundle(reservation, RetentionCapturePhase.Staging, RetentionCapturePhase.PublishedPendingCatalog, 3));
    }

    [Fact]
    public void PlanSensitiveBundle_CheckpointFailureRollsBackMembersAndPhase()
    {
        using var fixture = Fixture.Create(phase => { if (phase == "plan_members_inserted") throw new InvalidOperationException("checkpoint"); });
        var reservation = fixture.Catalog.ReserveSensitiveBundle("C:/private-parent");

        Assert.Throws<InvalidOperationException>(() => fixture.Catalog.PlanSensitiveBundle(reservation, Fixture.Plan()));
        Assert.Equal("reserved", fixture.Scalar<string>("SELECT phase FROM retention_file_capture_reservations WHERE capture_id=$id", reservation.CaptureId));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_file_capture_members WHERE capture_id=$id", reservation.CaptureId));
    }

    [Theory]
    [InlineData("legacy_plan_members_inserted")]
    [InlineData("legacy_plan_reservation_staged")]
    [InlineData("legacy_plan_journal_inserted")]
    public void PlanLegacySensitiveBundle_CheckpointFailureRollsBackEveryDurablePart(string checkpoint)
    {
        using var fixture = Fixture.Create(phase => { if (phase == checkpoint) throw new InvalidOperationException("checkpoint"); });
        var reservation = fixture.Catalog.ReserveSensitiveBundle("C:/legacy-root", legacyV1: true);

        Assert.Throws<InvalidOperationException>(() => fixture.Catalog.PlanLegacySensitiveBundle(reservation, Fixture.Plan(), SHA256.HashData("legacy"u8)));

        Assert.Equal("reserved", fixture.Scalar<string>("SELECT phase FROM retention_file_capture_reservations WHERE capture_id=$id", reservation.CaptureId));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_file_capture_members WHERE capture_id=$id", reservation.CaptureId));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_legacy_bundle_journal WHERE capture_id=$id", reservation.CaptureId));
    }

    [Fact]
    public async Task PlanLegacySensitiveBundle_ConcurrentSameRootClaimHasOneWinnerAndRollbackLoser()
    {
        using var fixture = Fixture.Create();
        var first = fixture.Catalog.ReserveSensitiveBundle("C:/same-root", legacyV1: true);
        var second = fixture.Catalog.ReserveSensitiveBundle("C:/same-root", legacyV1: true);
        var digest = SHA256.HashData("legacy"u8);
        using var barrier = new Barrier(2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var firstTask = Task.Run(() => { barrier.SignalAndWait(timeout.Token); return fixture.Catalog.PlanLegacySensitiveBundle(first, Fixture.Plan(), digest); }, timeout.Token);
        var secondTask = Task.Run(() => { barrier.SignalAndWait(timeout.Token); return fixture.Catalog.PlanLegacySensitiveBundle(second, Fixture.Plan(), digest); }, timeout.Token);

        var results = await Task.WhenAll(firstTask, secondTask).WaitAsync(timeout.Token);

        Assert.Equal(1, results.Count(result => result == RetentionCaptureMutationDisposition.Applied));
        Assert.Equal(1, results.Count(result => result == RetentionCaptureMutationDisposition.StaleNoOp));
        Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_legacy_bundle_journal", first.CaptureId));
        var loser = results[0] == RetentionCaptureMutationDisposition.StaleNoOp ? first : second;
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_file_capture_members WHERE capture_id=$id", loser.CaptureId));
        Assert.Equal("reserved", fixture.Scalar<string>("SELECT phase FROM retention_file_capture_reservations WHERE capture_id=$id", loser.CaptureId));
    }

    [Fact]
    public void CompleteSensitiveBundle_CheckpointFailureRollsBackItemJournalAndPhase()
    {
        using var fixture = Fixture.Create(phase => { if (phase == "completion_item_and_journal_inserted") throw new InvalidOperationException("checkpoint"); });
        var reservation = fixture.Catalog.ReserveSensitiveBundle("C:/private-parent");
        Assert.Equal(RetentionCaptureMutationDisposition.Applied, fixture.Catalog.PlanSensitiveBundle(reservation, Fixture.Plan()));
        AdvanceAll(fixture.Catalog, reservation);
        Assert.Equal(RetentionCaptureMutationDisposition.Applied, fixture.Catalog.TransitionSensitiveBundle(reservation, RetentionCapturePhase.Staging, RetentionCapturePhase.PublishedPendingCatalog, 3));

        Assert.Throws<InvalidOperationException>(() => fixture.Catalog.CompleteSensitiveBundle(reservation, Fixture.Marker, Fixture.Manifest));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE source_item_id=$id", reservation.CaptureId));
        Assert.Equal("published_pending_catalog", fixture.Scalar<string>("SELECT phase FROM retention_file_capture_reservations WHERE capture_id=$id", reservation.CaptureId));
    }

    public static IEnumerable<object[]> TamperedReservationSql()
    {
        yield return ["UPDATE retention_file_capture_reservations SET source_item_id='11111111111111111111111111111111' WHERE capture_id=$id"];
        yield return ["UPDATE retention_file_capture_reservations SET policy_id='raw-default-90d' WHERE capture_id=$id"];
        yield return ["UPDATE retention_file_capture_reservations SET reserved_at_utc_ticks=1 WHERE capture_id=$id"];
        yield return ["UPDATE retention_file_capture_reservations SET final_locator='C:/other' WHERE capture_id=$id"];
        yield return ["UPDATE retention_file_capture_members SET relative_path='wrong-marker' WHERE capture_id=$id AND member_kind='owner_marker'"];
        yield return ["UPDATE retention_file_capture_members SET sha256=zeroblob(32) WHERE capture_id=$id AND relative_path='manifest.json'"];
        yield return ["UPDATE retention_file_capture_members SET deletion_order=99 WHERE capture_id=$id AND relative_path='manifest.json'"];
        yield return ["UPDATE retention_file_capture_reservations SET durable_cursor=99 WHERE capture_id=$id"];
    }

    [Theory]
    [MemberData(nameof(TamperedReservationSql))]
    public void LoadIncompleteSensitiveBundle_RejectsSchemaValidAuthorityTampering(string sql)
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveSensitiveBundle("C:/private-parent");
        Assert.Equal(RetentionCaptureMutationDisposition.Applied, fixture.Catalog.PlanSensitiveBundle(reservation, Fixture.Plan()));
        fixture.Execute(sql, reservation.CaptureId);

        Assert.Throws<RetentionMigrationBlockedException>(() => fixture.Catalog.LoadIncompleteSensitiveBundle(reservation.CaptureId));
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly byte[] Marker = SHA256.HashData("marker"u8);
        internal static readonly byte[] Manifest = SHA256.HashData("manifest"u8);
        private Fixture(string path, MutableTimeProvider time, RetentionCatalogStore catalog) => (Path, Time, Catalog) = (path, time, catalog);
        internal string Path { get; }
        internal MutableTimeProvider Time { get; }
        internal RetentionCatalogStore Catalog { get; }

        internal static Fixture Create(Action<string>? checkpoint = null)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retention-file-capture-catalog-{Guid.NewGuid():N}.sqlite");
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 19, 1, 2, 3, TimeSpan.Zero));
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            return new(path, time, checkpoint is null ? new RetentionCatalogStore(context, time) : new RetentionCatalogStore(context, time, checkpoint));
        }

        internal static RetentionFileCapturePlanInput Plan() => new(
            Marker,
            Manifest,
            [
                new(0, ".retention-owner.v1", RetentionFileCaptureMemberKind.OwnerMarker, null, null, 2),
                new(1, "manifest.json", RetentionFileCaptureMemberKind.File, 12, SHA256.HashData("manifest"u8), 1),
                new(2, "evidence", RetentionFileCaptureMemberKind.Directory, null, null, 0)
            ]);

        internal T Scalar<T>(string sql, string captureId)
        {
            using var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", captureId);
            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
        }

        internal void Execute(string sql, string captureId)
        {
            using var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", captureId);
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var candidate in new[] { Path, Path + "-wal", Path + "-shm" }) if (File.Exists(candidate)) File.Delete(candidate);
        }
    }

    private static void AdvanceAll(RetentionCatalogStore catalog, RetentionFileCaptureReservation reservation)
    {
        Assert.Equal(RetentionCaptureMutationDisposition.Applied, catalog.AdvanceSensitiveBundleCursor(reservation, 0));
        Assert.Equal(RetentionCaptureMutationDisposition.Applied, catalog.AdvanceSensitiveBundleCursor(reservation, 1));
        Assert.Equal(RetentionCaptureMutationDisposition.Applied, catalog.AdvanceSensitiveBundleCursor(reservation, 2));
    }
}
