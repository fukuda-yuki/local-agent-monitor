using System.Security.Cryptography;
using CopilotAgentObservability.LocalMonitor.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class SensitiveBundleRetentionAdapterTests
{
    [Fact]
    public async Task BundleDelete_UsesExactOwnershipAndMarkerLast()
    {
        using var fixture = await Fixture.CreateAsync();
        var sibling = Path.Combine(fixture.Root, "unrelated");
        Directory.CreateDirectory(sibling);
        var unlinked = new List<int>();
        var adapter = new SensitiveBundleRetentionAdapter(fixture.Catalog, fixture.Time, checkpoint =>
        {
            if (checkpoint.Phase == SensitiveBundleRetentionCheckpointPhase.AfterUnlink) unlinked.Add(checkpoint.Ordinal);
        });

        var result = await adapter.DeleteAsync(fixture.Context);

        Assert.Same(RetentionAdapterResult.Deleted, result);
        Assert.False(Directory.Exists(fixture.Final));
        Assert.True(Directory.Exists(fixture.Root));
        Assert.True(Directory.Exists(sibling));
        Assert.Equal(new[] { 2, 1, 0, 3 }, unlinked);
        Assert.Equal(4L, fixture.Scalar("SELECT durable_cursor FROM retention_delete_journal WHERE item_id=$id"));
    }

    [Theory]
    [InlineData("raw.json")]
    [InlineData("manifest.json")]
    [InlineData(".retention-owner.v1")]
    public async Task BundleDelete_RejectsReplacedOwnedMember(string member)
    {
        using var fixture = await Fixture.CreateAsync();
        File.WriteAllText(Path.Combine(fixture.Final, member), "replacement");

        var result = await new SensitiveBundleRetentionAdapter(fixture.Catalog, fixture.Time).DeleteAsync(fixture.Context);

        Assert.Equal(RetentionErrorCode.OwnershipMismatch, result.ErrorCode);
        Assert.True(File.Exists(Path.Combine(fixture.Final, member)));
    }

    [Fact]
    public async Task BundleDelete_RejectsPriorStepReappearanceAndUnexpectedNestedMember()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.Equal(RetentionMutationDisposition.Applied, await fixture.Catalog.TryAdvanceDeleteCursorAsync(new(fixture.Context.ItemId, fixture.Context.ExpectedRevision, fixture.Context.LeaseOwner, fixture.Context.LeaseGeneration), 0, 1, fixture.Time.GetUtcNow(), CancellationToken.None));
        Directory.CreateDirectory(Path.Combine(fixture.Final, "nested"));
        File.WriteAllText(Path.Combine(fixture.Final, "nested", "unexpected"), "keep");

        var result = await new SensitiveBundleRetentionAdapter(fixture.Catalog, fixture.Time).DeleteAsync(fixture.Context with { IntentCursor = 1 });

        Assert.Equal(RetentionErrorCode.OwnershipMismatch, result.ErrorCode);
        Assert.True(File.Exists(Path.Combine(fixture.Final, "nested", "unexpected")));
    }

    [Fact]
    public async Task BundleDelete_RejectsReparseMemberAndFinalChild()
    {
        using var memberFixture = await Fixture.CreateAsync();
        var memberTarget = Path.Combine(memberFixture.Root, "member-target");
        File.WriteAllText(memberTarget, "x");
        File.Delete(Path.Combine(memberFixture.Final, "raw.json"));
        File.CreateSymbolicLink(Path.Combine(memberFixture.Final, "raw.json"), memberTarget);
        Assert.Equal(RetentionErrorCode.OwnershipMismatch, (await new SensitiveBundleRetentionAdapter(memberFixture.Catalog, memberFixture.Time).DeleteAsync(memberFixture.Context)).ErrorCode);

        using var childFixture = await Fixture.CreateAsync();
        var childTarget = Path.Combine(childFixture.Root, "child-target");
        Directory.CreateDirectory(childTarget);
        Directory.Delete(childFixture.Final, recursive: true);
        Directory.CreateSymbolicLink(childFixture.Final, childTarget);
        Assert.Equal(RetentionErrorCode.OwnershipMismatch, (await new SensitiveBundleRetentionAdapter(childFixture.Catalog, childFixture.Time).DeleteAsync(childFixture.Context)).ErrorCode);
    }

    [Fact]
    public async Task BundleDelete_RejectsUnexpectedMembersWithoutRemovingThem()
    {
        using var fixture = await Fixture.CreateAsync();
        var unexpected = Path.Combine(fixture.Final, "unexpected.json");
        File.WriteAllText(unexpected, "keep");

        var result = await new SensitiveBundleRetentionAdapter(fixture.Catalog, fixture.Time).DeleteAsync(fixture.Context);

        Assert.Equal(RetentionAdapterDisposition.TerminalFailure, result.Disposition);
        Assert.Equal(RetentionErrorCode.OwnershipMismatch, result.ErrorCode);
        Assert.True(File.Exists(unexpected));
        Assert.True(File.Exists(Path.Combine(fixture.Final, "raw.json")));
        Assert.True(File.Exists(Path.Combine(fixture.Final, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(fixture.Final, RetentionFileCaptureContracts.OwnerMarkerName)));
    }

    [Fact]
    public async Task BundleDelete_MissingCurrentAfterUnlinkBeforeCursor_RecoversForwardOnly()
    {
        using var fixture = await Fixture.CreateAsync();
        var crashed = new SensitiveBundleRetentionAdapter(fixture.Catalog, fixture.Time, checkpoint =>
        {
            if (checkpoint.Ordinal == 2 && checkpoint.Phase == SensitiveBundleRetentionCheckpointPhase.AfterUnlink) throw new IOException();
        });

        var first = await crashed.DeleteAsync(fixture.Context);
        var recovered = await new SensitiveBundleRetentionAdapter(fixture.Catalog, fixture.Time).DeleteAsync(fixture.Context);

        Assert.Equal(RetentionErrorCode.DeleteIoFailed, first.ErrorCode);
        Assert.Same(RetentionAdapterResult.Deleted, recovered);
        Assert.False(Directory.Exists(fixture.Final));
        Assert.Equal(4L, fixture.Scalar("SELECT durable_cursor FROM retention_delete_journal WHERE item_id=$id"));
    }

    [Fact]
    public async Task BundleDelete_CrashAfterCursorAdvance_ResumesAtNextMember()
    {
        using var fixture = await Fixture.CreateAsync();
        var crashed = new SensitiveBundleRetentionAdapter(fixture.Catalog, fixture.Time, checkpoint =>
        {
            if (checkpoint.Ordinal == 2 && checkpoint.Phase == SensitiveBundleRetentionCheckpointPhase.AfterCursorAdvanced) throw new IOException();
        });

        var first = await crashed.DeleteAsync(fixture.Context);
        var recovered = await new SensitiveBundleRetentionAdapter(fixture.Catalog, fixture.Time).DeleteAsync(fixture.Context with { IntentCursor = 1 });

        Assert.Equal(RetentionErrorCode.DeleteIoFailed, first.ErrorCode);
        Assert.Same(RetentionAdapterResult.Deleted, recovered);
        Assert.False(Directory.Exists(fixture.Final));
    }

    [Fact]
    public async Task BundleDelete_StaleFenceAndCancellationDoNotDelete()
    {
        using var fixture = await Fixture.CreateAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var adapter = new SensitiveBundleRetentionAdapter(fixture.Catalog, fixture.Time);

        Assert.Same(RetentionAdapterResult.LeaseLost, await adapter.DeleteAsync(fixture.Context with { ExpectedRevision = fixture.Context.ExpectedRevision + 1 }));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await adapter.DeleteAsync(fixture.Context with { CancellationToken = cancelled.Token }));
        Assert.True(Directory.Exists(fixture.Final));
        Assert.True(File.Exists(Path.Combine(fixture.Final, "raw.json")));
    }

    [Fact]
    public async Task BundleDelete_CatalogBusyIsTransientBusyWithoutMutation()
    {
        using var fixture = await Fixture.CreateAsync();
        using var connection = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False");
        connection.Open();
        using var begin = connection.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE;";
        begin.ExecuteNonQuery();

        var result = await new SensitiveBundleRetentionAdapter(fixture.Catalog, fixture.Time).DeleteAsync(fixture.Context);

        Assert.Equal(RetentionAdapterDisposition.TransientFailure, result.Disposition);
        Assert.Equal(RetentionErrorCode.DeleteBusy, result.ErrorCode);
        Assert.True(File.Exists(Path.Combine(fixture.Final, "raw.json")));
        using var rollback = connection.CreateCommand();
        rollback.CommandText = "ROLLBACK;";
        rollback.ExecuteNonQuery();
    }

    [Fact]
    public async Task BundleDelete_FinalChildAlreadyAbsentIsForwardProgressOnlyAtFinalCursor()
    {
        using var fixture = await Fixture.CreateAsync();
        for (var cursor = 0; cursor < 3; cursor++)
            Assert.Equal(RetentionMutationDisposition.Applied, await fixture.Catalog.TryAdvanceDeleteCursorAsync(new(fixture.Context.ItemId, fixture.Context.ExpectedRevision, fixture.Context.LeaseOwner, fixture.Context.LeaseGeneration), cursor, cursor + 1, fixture.Time.GetUtcNow(), CancellationToken.None));
        Directory.Delete(fixture.Final, recursive: true);

        var result = await new SensitiveBundleRetentionAdapter(fixture.Catalog, fixture.Time).DeleteAsync(fixture.Context with { IntentCursor = 3 });

        Assert.Same(RetentionAdapterResult.Deleted, result);
        Assert.Equal(4L, fixture.Scalar("SELECT durable_cursor FROM retention_delete_journal WHERE item_id=$id"));
    }

    [Fact]
    public async Task BundleDelete_FinalChildAbsentBeforeMemberCursorIsOwnershipMismatch()
    {
        using var fixture = await Fixture.CreateAsync();
        Directory.Delete(fixture.Final, recursive: true);

        var result = await new SensitiveBundleRetentionAdapter(fixture.Catalog, fixture.Time).DeleteAsync(fixture.Context);

        Assert.Equal(RetentionErrorCode.OwnershipMismatch, result.ErrorCode);
    }

    [Fact]
    public async Task BundleDelete_FinalCursorIsIdempotentOnlyWhenChildAbsent()
    {
        using var fixture = await Fixture.CreateAsync();
        for (var cursor = 0; cursor < 4; cursor++)
            Assert.Equal(RetentionMutationDisposition.Applied, await fixture.Catalog.TryAdvanceDeleteCursorAsync(new(fixture.Context.ItemId, fixture.Context.ExpectedRevision, fixture.Context.LeaseOwner, fixture.Context.LeaseGeneration), cursor, cursor + 1, fixture.Time.GetUtcNow(), CancellationToken.None));
        Directory.Delete(fixture.Final, recursive: true);
        var adapter = new SensitiveBundleRetentionAdapter(fixture.Catalog, fixture.Time);

        Assert.Same(RetentionAdapterResult.Deleted, await adapter.DeleteAsync(fixture.Context with { IntentCursor = 4 }));
        Directory.CreateDirectory(fixture.Final);
        Assert.Equal(RetentionErrorCode.OwnershipMismatch, (await adapter.DeleteAsync(fixture.Context with { IntentCursor = 4 })).ErrorCode);
    }

    [Fact]
    public async Task BundleDelete_DoesNotExposeSensitiveValuesInCheckpointOrResult()
    {
        using var fixture = await Fixture.CreateAsync();
        var checkpoint = new SensitiveBundleRetentionCheckpoint(2, SensitiveBundleRetentionCheckpointPhase.AfterUnlink);
        var result = RetentionAdapterResult.TransientFailure(RetentionErrorCode.DeleteIoFailed);

        Assert.DoesNotContain(fixture.Root, checkpoint.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Final, checkpoint.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Context.SourceIdentity.OwnershipReceipt, result.ToString(), StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, RetentionCatalogStore catalog, MutableTimeProvider time, RetentionDeleteContext context, string itemId, string final)
            => (Root, Catalog, Time, Context, ItemId, Final) = (root, catalog, time, context, itemId, final);

        internal string Root { get; }
        internal string DatabasePath => Path.Combine(Root, "catalog.sqlite");
        internal RetentionCatalogStore Catalog { get; }
        internal MutableTimeProvider Time { get; }
        internal RetentionDeleteContext Context { get; }
        internal string ItemId { get; }
        internal string Final { get; }

        internal long Scalar(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", ItemId);
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"sensitive-bundle-adapter-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var now = new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);
            var time = new MutableTimeProvider(now);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(Path.Combine(root, "catalog.sqlite"), time);
            var catalog = new RetentionCatalogStore(context, time);
            var reservation = catalog.ReserveSensitiveBundle(root);
            var marker = RetentionFileCaptureOwnershipMarker.Create(reservation.StoreInstanceId, reservation.CaptureId, reservation.ReservedAtText, reservation.ReservedAtTicks, reservation.OwnerToken);
            var manifest = "{}"u8.ToArray();
            var plan = new RetentionFileCapturePlanInput(SHA256.HashData(marker), SHA256.HashData(manifest),
            [
                new(0, RetentionFileCaptureContracts.OwnerMarkerName, RetentionFileCaptureMemberKind.OwnerMarker, null, null, 2),
                new(1, "manifest.json", RetentionFileCaptureMemberKind.File, manifest.Length, SHA256.HashData(manifest), 1),
                new(2, "raw.json", RetentionFileCaptureMemberKind.File, 1, SHA256.HashData("x"u8), 0)
            ]);
            Assert.Equal(RetentionCaptureMutationDisposition.Applied, catalog.PlanSensitiveBundle(reservation, plan));
            for (var cursor = 0; cursor < 3; cursor++) Assert.Equal(RetentionCaptureMutationDisposition.Applied, catalog.AdvanceSensitiveBundleCursor(reservation, cursor));
            Assert.Equal(RetentionCaptureMutationDisposition.Applied, catalog.TransitionSensitiveBundle(reservation, RetentionCapturePhase.Staging, RetentionCapturePhase.PublishedPendingCatalog, 3));
            Directory.CreateDirectory(reservation.FinalLocator);
            File.WriteAllBytes(Path.Combine(reservation.FinalLocator, RetentionFileCaptureContracts.OwnerMarkerName), marker);
            File.WriteAllBytes(Path.Combine(reservation.FinalLocator, "manifest.json"), manifest);
            File.WriteAllText(Path.Combine(reservation.FinalLocator, "raw.json"), "x");
            Assert.Equal(RetentionCaptureMutationDisposition.Applied, catalog.CompleteSensitiveBundle(reservation, SHA256.HashData(marker), SHA256.HashData(manifest)));
            Execute(root, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");
            var itemId = Text(root, "SELECT item_id FROM retention_items WHERE source_item_id=$source", ("$source", reservation.CaptureId));
            Execute(root, "UPDATE retention_items SET state='deletion_queued',revision=1,read_denied_at=$now,queued_at=$now WHERE item_id=$id", ("$now", now.ToString("O")), ("$id", itemId));
            var claim = (await catalog.TryClaimDeletionAsync(new(itemId, 1, RetentionWorkKind.Queued), "bundle-adapter", now, CancellationToken.None)).Claim!;
            var intent = await catalog.EnsureDeleteIntentAsync(claim.Fence, 0, now, CancellationToken.None);
            Assert.Equal(RetentionIntentDisposition.Committed, intent.Disposition);
            return new(root, catalog, time, new(claim.Fence.ItemId, claim.StoreInstanceId, claim.StoreKind, claim.Fence.ExpectedRevision, claim.Fence.LeaseOwner, claim.Fence.LeaseGeneration, claim.SourceIdentity, claim.PrivateLocator, intent.IntentCursor, CancellationToken.None), itemId, reservation.FinalLocator);
        }

        public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        private static void Execute(string root, string sql, params (string Name, object Value)[] values) { using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "catalog.sqlite")};Pooling=False"); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value); command.ExecuteNonQuery(); }
        private static string Text(string root, string sql, params (string Name, object Value)[] values) { using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "catalog.sqlite")};Pooling=False"); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value); return (string)command.ExecuteScalar()!; }
    }
}
