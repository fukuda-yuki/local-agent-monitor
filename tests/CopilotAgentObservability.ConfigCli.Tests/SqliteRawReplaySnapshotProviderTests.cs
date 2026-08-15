using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.RawReplay;
using Microsoft.Data.Sqlite;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SqliteRawReplaySnapshotProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public async Task CaptureAsync_SelectsExactRawRowsAndHoldsOneCompositeOperationLease()
    {
        using var temp = new TempDirectory();
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(temp.DatabasePath, new FixedTimeProvider(Now));
        var store = new RawTelemetryStore(temp.DatabasePath, context, new FixedTimeProvider(Now));
        store.CreateMonitorSchema();
        var first = store.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "trace-a", Now.AddMinutes(-2), null, "{\"resourceSpans\":[]}"));
        var second = store.Insert(new RawTelemetryRecord(null, RawTelemetrySources.CollectorOutput, "trace-b", Now.AddMinutes(-1), "{\"service.name\":\"fixture\"}", "{\"resourceSpans\":[]}"));

        var provider = new SqliteRawReplaySnapshotProvider(temp.DatabasePath, context, new FixedTimeProvider(Now));
        var capture = await provider.CaptureAsync(new RawReplaySelection(
            RawRecordIds: [first, second], Sources: [RawTelemetrySources.CollectorOutput],
            StartInclusive: Now.AddMinutes(-2), EndExclusive: Now), false, CancellationToken.None);

        Assert.True(capture.Success, capture.ErrorCode);
        var lease = Assert.IsType<RawReplaySnapshotLease>(capture.Lease);
        var record = Assert.Single(lease.Snapshot.Records);
        Assert.Equal(second, record.RawRecordId);
        Assert.Equal("trace-b", record.TraceId);
        Assert.Equal(Now.AddMinutes(-1), record.ReceivedAt);
        Assert.Equal("{\"resourceSpans\":[]}", record.PayloadJson);
        Assert.Equal(1, Scalar<long>(temp.DatabasePath, "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));

        await lease.DisposeAsync();
        Assert.Equal(0, Scalar<long>(temp.DatabasePath, "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
    }

    [Fact]
    public async Task CaptureAsync_UsesTraceAndSessionAxesWithoutHeuristicMerging()
    {
        using var temp = new TempDirectory();
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(temp.DatabasePath, new FixedTimeProvider(Now));
        var store = new RawTelemetryStore(temp.DatabasePath, context, new FixedTimeProvider(Now));
        store.CreateMonitorSchema();
        var selected = store.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "trace-exact", Now, null, "{\"resourceSpans\":[]}"));
        var other = store.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "trace-other", Now, null, "{\"resourceSpans\":[]}"));
        new SqliteSessionStore(temp.DatabasePath, context, new FixedTimeProvider(Now)).CreateSchema();
        var sessionId = Guid.CreateVersion7();
        SeedTraceAndSession(temp.DatabasePath, selected, sessionId, "trace-exact");

        var provider = new SqliteRawReplaySnapshotProvider(temp.DatabasePath, context, new FixedTimeProvider(Now));
        var capture = await provider.CaptureAsync(new RawReplaySelection(
            SessionIds: [sessionId.ToString("D")], TraceIds: ["trace-missing", "trace-exact"]), false,
            CancellationToken.None);

        Assert.True(capture.Success, capture.ErrorCode);
        await using var lease = Assert.IsType<RawReplaySnapshotLease>(capture.Lease);
        Assert.Equal(selected, Assert.Single(lease.Snapshot.Records).RawRecordId);

        var disjoint = await provider.CaptureAsync(new RawReplaySelection(
            RawRecordIds: [other], TraceIds: ["trace-exact"]), false, CancellationToken.None);
        Assert.True(disjoint.Success, disjoint.ErrorCode);
        await using var disjointLease = Assert.IsType<RawReplaySnapshotLease>(disjoint.Lease);
        Assert.Empty(disjointLease.Snapshot.Records);
    }

    [Fact]
    public async Task CaptureAsync_DeniedMemberFailsTheWholeSelectionWithoutAResidualLease()
    {
        using var temp = new TempDirectory();
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(temp.DatabasePath, new FixedTimeProvider(Now));
        var store = new RawTelemetryStore(temp.DatabasePath, context, new FixedTimeProvider(Now));
        store.CreateMonitorSchema();
        var first = store.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "trace-a", Now, null, "{}"));
        var second = store.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "trace-b", Now, null, "{}"));
        Execute(temp.DatabasePath, "UPDATE retention_items SET read_denied_at=$now WHERE store_kind='raw_record' AND source_item_id=$id;",
            ("$now", (object)Now.ToString("O", CultureInfo.InvariantCulture)), ("$id", second.ToString(CultureInfo.InvariantCulture)));

        var result = await new SqliteRawReplaySnapshotProvider(temp.DatabasePath, context, new FixedTimeProvider(Now))
            .CaptureAsync(new RawReplaySelection(RawRecordIds: [first, second]), false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("snapshot_read_denied", result.ErrorCode);
        Assert.Null(result.Lease);
        Assert.Equal(0, Scalar<long>(temp.DatabasePath, "SELECT COUNT(*) FROM retention_leases;"));
    }

    [Fact]
    public async Task CaptureAsync_MixedThreeMemberBoundaryAfterAdmissionRetainsTheCompleteSnapshotWithoutCatalogMutation()
    {
        using var temp = new TempDirectory();
        var boundary = Now.AddSeconds(1);
        var fixture = SeedSelectedBatchFixture(temp, boundary);
        var authorityBefore = CaptureRetentionAuthorityState(temp.DatabasePath, fixture.SelectedItemIds);
        var selectedCatalogBefore = fixture.Items
            .Select(item => FullRowDump(temp.DatabasePath, "retention_items", "item_id", item.ItemId))
            .ToArray();
        var boundarySiblingCatalogBefore = FullRowDump(
            temp.DatabasePath,
            "retention_items",
            "item_id",
            fixture.BoundarySiblingItem.ItemId);
        var pinnedSourceBefore = FullRowDump(temp.DatabasePath, "raw_records", "id", fixture.PinnedId);
        var unexpiredSourceBefore = FullRowDump(temp.DatabasePath, "raw_records", "id", fixture.UnexpiredId);
        var boundarySourceBefore = FullRowDump(temp.DatabasePath, "raw_records", "id", fixture.BoundaryId);
        var boundarySiblingSourceBefore = FullRowDump(temp.DatabasePath, "raw_records", "id", fixture.BoundarySiblingId);
        AuditRawOperationLeaseAcquisitions(
            temp.DatabasePath,
            fixture.CanonicalIds,
            fixture.UnrelatedItem.ItemId,
            guardMutationMemberId: fixture.BoundaryId);
        var checkpoint = new AdvanceTimeAfterMaterializationCheckpoint(fixture.Clock, boundary);

        var result = await new SqliteRawReplaySnapshotProvider(
                temp.DatabasePath,
                fixture.Context,
                fixture.Clock,
                checkpoint)
            .CaptureAsync(new RawReplaySelection(RawRecordIds: fixture.RequestedIds), false, CancellationToken.None);

        Assert.True(result.Success, result.ErrorCode);
        Assert.Null(result.ErrorCode);
        var lease = Assert.IsType<RawReplaySnapshotLease>(result.Lease);
        Assert.Equal(fixture.CanonicalIds, lease.Snapshot.Records.Select(static record => record.RawRecordId));
        Assert.Equal(1, checkpoint.InvocationCount);
        var acquisitionAudit = ReadLeaseAcquisitionAudit(temp.DatabasePath);
        Assert.Equal(
            new[]
            {
                $"{fixture.PinnedId}|1",
                $"{fixture.UnexpiredId}|2",
                $"{fixture.BoundaryId}|3",
            },
            acquisitionAudit.Select(static row => $"{row.SourceItemId}|{row.ActiveCount}"));
        AssertSharedCompositeLeaseTuple(acquisitionAudit);
        Assert.Equal(4, Scalar<long>(temp.DatabasePath, "SELECT COUNT(*) FROM retention_leases;"));
        Assert.Equal(3, Scalar<long>(
            temp.DatabasePath,
            $"SELECT COUNT(*) FROM retention_leases WHERE item_id IN ('{fixture.PinnedItem.ItemId}','{fixture.UnexpiredItem.ItemId}','{fixture.BoundaryItem.ItemId}');"));

        Assert.Equal(selectedCatalogBefore, fixture.Items
            .Select(item => FullRowDump(temp.DatabasePath, "retention_items", "item_id", item.ItemId))
            .ToArray());
        Assert.Equal(
            boundarySiblingCatalogBefore,
            FullRowDump(temp.DatabasePath, "retention_items", "item_id", fixture.BoundarySiblingItem.ItemId));
        Assert.Equal(pinnedSourceBefore, FullRowDump(temp.DatabasePath, "raw_records", "id", fixture.PinnedId));
        Assert.Equal(unexpiredSourceBefore, FullRowDump(temp.DatabasePath, "raw_records", "id", fixture.UnexpiredId));
        Assert.Equal(boundarySourceBefore, FullRowDump(temp.DatabasePath, "raw_records", "id", fixture.BoundaryId));
        Assert.Equal(
            boundarySiblingSourceBefore,
            FullRowDump(temp.DatabasePath, "raw_records", "id", fixture.BoundarySiblingId));

        await lease.DisposeAsync();

        AssertUnrelatedLeaseSurvives(temp.DatabasePath, fixture, acquisitionAudit[0]);
        AssertRetentionAuthorityChangedOnly(
            temp.DatabasePath,
            fixture,
            authorityBefore,
            acquisitionAudit[0],
            normalizedItemId: null);
    }

    [Fact]
    public async Task CaptureAsync_SelectorNullAfterThreeMemberLeaseAcquisitionReleasesAllLeasesWithoutCatalogMutation()
    {
        using var temp = new TempDirectory();
        var fixture = SeedSelectedBatchFixture(temp, Now.AddMinutes(1));
        var authorityBefore = CaptureRetentionAuthorityState(temp.DatabasePath, fixture.SelectedItemIds);
        var catalogBefore = fixture.Items
            .Select(item => FullRowDump(temp.DatabasePath, "retention_items", "item_id", item.ItemId))
            .ToArray();
        var boundarySiblingCatalogBefore = FullRowDump(
            temp.DatabasePath,
            "retention_items",
            "item_id",
            fixture.BoundarySiblingItem.ItemId);
        var unexpiredSourceBefore = FullRowDump(temp.DatabasePath, "raw_records", "id", fixture.UnexpiredId);
        var boundarySourceBefore = FullRowDump(temp.DatabasePath, "raw_records", "id", fixture.BoundaryId);
        var boundarySiblingSourceBefore = FullRowDump(temp.DatabasePath, "raw_records", "id", fixture.BoundarySiblingId);
        AuditRawOperationLeaseAcquisitions(
            temp.DatabasePath,
            fixture.CanonicalIds,
            fixture.UnrelatedItem.ItemId,
            deleteWhenMemberId: fixture.BoundaryId,
            deletedMemberId: fixture.PinnedId);

        var result = await new SqliteRawReplaySnapshotProvider(temp.DatabasePath, fixture.Context, fixture.Clock)
            .CaptureAsync(new RawReplaySelection(RawRecordIds: fixture.RequestedIds), false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("snapshot_read_denied", result.ErrorCode);
        Assert.Null(result.Lease);
        var acquisitionAudit = ReadLeaseAcquisitionAudit(temp.DatabasePath);
        Assert.Equal(
            new[]
            {
                $"{fixture.PinnedId}|1",
                $"{fixture.UnexpiredId}|2",
                $"{fixture.BoundaryId}|3",
            },
            acquisitionAudit.Select(static row => $"{row.SourceItemId}|{row.ActiveCount}"));
        AssertSharedCompositeLeaseTuple(acquisitionAudit);
        AssertUnrelatedLeaseSurvives(temp.DatabasePath, fixture, acquisitionAudit[0]);
        Assert.Equal(0, Scalar<long>(temp.DatabasePath, $"SELECT COUNT(*) FROM raw_records WHERE id={fixture.PinnedId};"));
        Assert.Equal(catalogBefore, fixture.Items
            .Select(item => FullRowDump(temp.DatabasePath, "retention_items", "item_id", item.ItemId))
            .ToArray());
        Assert.Equal(
            boundarySiblingCatalogBefore,
            FullRowDump(temp.DatabasePath, "retention_items", "item_id", fixture.BoundarySiblingItem.ItemId));
        Assert.Equal(unexpiredSourceBefore, FullRowDump(temp.DatabasePath, "raw_records", "id", fixture.UnexpiredId));
        Assert.Equal(boundarySourceBefore, FullRowDump(temp.DatabasePath, "raw_records", "id", fixture.BoundaryId));
        Assert.Equal(
            boundarySiblingSourceBefore,
            FullRowDump(temp.DatabasePath, "raw_records", "id", fixture.BoundarySiblingId));
        AssertRetentionAuthorityChangedOnly(
            temp.DatabasePath,
            fixture,
            authorityBefore,
            acquisitionAudit[0],
            normalizedItemId: null);
    }

    [Fact]
    public async Task CaptureAsync_SecondMemberSourceDisappearsBeforeTokenCaptureReleasesCurrentAndPriorLeasesAndDeniesOnlyMissingMember()
    {
        using var temp = new TempDirectory();
        var fixture = SeedSelectedBatchFixture(temp, Now.AddMinutes(1));
        var authorityBefore = CaptureRetentionAuthorityState(temp.DatabasePath, fixture.SelectedItemIds);
        var pinnedCatalogBefore = FullRowDump(temp.DatabasePath, "retention_items", "item_id", fixture.PinnedItem.ItemId);
        var boundaryCatalogBefore = FullRowDump(temp.DatabasePath, "retention_items", "item_id", fixture.BoundaryItem.ItemId);
        var boundarySiblingCatalogBefore = FullRowDump(
            temp.DatabasePath,
            "retention_items",
            "item_id",
            fixture.BoundarySiblingItem.ItemId);
        var corruptInvariantBefore = RowDumpExcluding(
            temp.DatabasePath,
            "retention_items",
            "item_id",
            fixture.UnexpiredItem.ItemId,
            "state",
            "revision",
            "read_denied_at",
            "error_code");
        var pinnedSourceBefore = FullRowDump(temp.DatabasePath, "raw_records", "id", fixture.PinnedId);
        var boundarySourceBefore = FullRowDump(temp.DatabasePath, "raw_records", "id", fixture.BoundaryId);
        var boundarySiblingSourceBefore = FullRowDump(temp.DatabasePath, "raw_records", "id", fixture.BoundarySiblingId);
        AuditRawOperationLeaseAcquisitions(
            temp.DatabasePath,
            fixture.CanonicalIds,
            fixture.UnrelatedItem.ItemId,
            deleteWhenMemberId: fixture.UnexpiredId,
            deletedMemberId: fixture.UnexpiredId,
            guardMutationMemberId: fixture.UnexpiredId);

        var result = await new SqliteRawReplaySnapshotProvider(temp.DatabasePath, fixture.Context, fixture.Clock)
            .CaptureAsync(new RawReplaySelection(RawRecordIds: fixture.RequestedIds), false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("snapshot_read_denied", result.ErrorCode);
        Assert.Null(result.Lease);
        var acquisitionAudit = ReadLeaseAcquisitionAudit(temp.DatabasePath);
        Assert.Equal(
            new[]
            {
                $"{fixture.PinnedId}|1",
                $"{fixture.UnexpiredId}|2",
            },
            acquisitionAudit.Select(static row => $"{row.SourceItemId}|{row.ActiveCount}"));
        AssertSharedCompositeLeaseTuple(acquisitionAudit);
        AssertUnrelatedLeaseSurvives(temp.DatabasePath, fixture, acquisitionAudit[0]);
        Assert.Equal(0, Scalar<long>(temp.DatabasePath, $"SELECT COUNT(*) FROM raw_records WHERE id={fixture.UnexpiredId};"));

        var catalog = new RetentionCatalogStore(fixture.Context, new FixedTimeProvider(Now));
        var corrupt = Assert.IsType<RetentionCatalogItem>(catalog.Find(fixture.UnexpiredItem.OwnershipKey));
        Assert.Equal(RetentionItemLifecycle.DeletionFailed, corrupt.State);
        Assert.Equal(Now, corrupt.ReadDeniedAt);
        Assert.Equal(fixture.UnexpiredItem.Revision + 1, corrupt.Revision);
        Assert.Equal("retention_invalid_identity", TextScalar(
            temp.DatabasePath,
            $"SELECT error_code FROM retention_items WHERE item_id='{fixture.UnexpiredItem.ItemId}';"));
        Assert.Null(TextScalar(
            temp.DatabasePath,
            $"SELECT queued_at FROM retention_items WHERE item_id='{fixture.UnexpiredItem.ItemId}';"));

        Assert.Equal(pinnedCatalogBefore, FullRowDump(temp.DatabasePath, "retention_items", "item_id", fixture.PinnedItem.ItemId));
        Assert.Equal(boundaryCatalogBefore, FullRowDump(temp.DatabasePath, "retention_items", "item_id", fixture.BoundaryItem.ItemId));
        Assert.Equal(
            boundarySiblingCatalogBefore,
            FullRowDump(temp.DatabasePath, "retention_items", "item_id", fixture.BoundarySiblingItem.ItemId));
        Assert.Equal(corruptInvariantBefore, RowDumpExcluding(
            temp.DatabasePath,
            "retention_items",
            "item_id",
            fixture.UnexpiredItem.ItemId,
            "state",
            "revision",
            "read_denied_at",
            "error_code"));
        Assert.Equal(pinnedSourceBefore, FullRowDump(temp.DatabasePath, "raw_records", "id", fixture.PinnedId));
        Assert.Equal(boundarySourceBefore, FullRowDump(temp.DatabasePath, "raw_records", "id", fixture.BoundaryId));
        Assert.Equal(
            boundarySiblingSourceBefore,
            FullRowDump(temp.DatabasePath, "raw_records", "id", fixture.BoundarySiblingId));
        AssertRetentionAuthorityChangedOnly(
            temp.DatabasePath,
            fixture,
            authorityBefore,
            acquisitionAudit[0],
            fixture.UnexpiredItem.ItemId,
            "state",
            "revision",
            "read_denied_at",
            "error_code");
    }

    [Fact]
    public async Task CaptureAsync_MissingExplicitRawMemberFailsTheWholeSelection()
    {
        using var temp = new TempDirectory();
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(temp.DatabasePath, new FixedTimeProvider(Now));
        var store = new RawTelemetryStore(temp.DatabasePath, context, new FixedTimeProvider(Now));
        store.CreateMonitorSchema();
        var existing = store.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "trace-a", Now, null, "{}"));

        var result = await new SqliteRawReplaySnapshotProvider(temp.DatabasePath, context, new FixedTimeProvider(Now))
            .CaptureAsync(new RawReplaySelection(RawRecordIds: [existing, existing + 1000]), false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("snapshot_member_missing", result.ErrorCode);
        Assert.Null(result.Lease);
        Assert.Equal(0, Scalar<long>(temp.DatabasePath, "SELECT COUNT(*) FROM retention_leases;"));
    }

    [Fact]
    public async Task CaptureAsync_IncludesExactSessionContentInTheSameCompositeLease()
    {
        using var temp = new TempDirectory();
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(temp.DatabasePath, new FixedTimeProvider(Now));
        var rawStore = new RawTelemetryStore(temp.DatabasePath, context, new FixedTimeProvider(Now)); rawStore.CreateMonitorSchema();
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "trace-content", Now, null, "{}"));
        var sessionStore = new SqliteSessionStore(temp.DatabasePath, context, new FixedTimeProvider(Now)); sessionStore.CreateSchema();
        var sessionId = Guid.CreateVersion7(); var runId = Guid.CreateVersion7(); var eventId = Guid.CreateVersion7();
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full, null, null,
            Now, Now, Now, SessionRawRetentionState.Expiring, Now, Now);
        var run = new ObservedSessionRun(runId, sessionId, SessionSourceSurface.CopilotCli, "run-native", "trace-content", null,
            "fixture-model", ObservedSessionStatus.Completed, Now, Now, 1, 2, 3);
        var @event = new ObservedSessionEvent(eventId, sessionId, runId, SessionSourceSurface.CopilotCli, null, "trace-content", "ok",
            "copilot-compatible-hook", "source-event", "assistant.completed", Now, SessionContentState.Available,
            "app-v1", "adapter-v1", "schema-v1", "normalization-v1", SessionMatchKind.ExactNative);
        sessionStore.Write(new SessionWriteBatch(new SessionDetail(session, [], [run], [@event]),
            [new SessionEventContent(eventId, "assistant_response", "{\"text\":\"synthetic\"}", Now, Now.AddDays(1))]));
        using (var connection = Open(temp.DatabasePath))
            Execute(connection, null, "INSERT INTO monitor_spans(raw_record_id,span_ordinal,trace_id,span_id,projected_at) VALUES($raw,0,'trace-content','span',$now);",
                ("$raw", rawId), ("$now", Now.ToString("O", CultureInfo.InvariantCulture)));

        var capture = await new SqliteRawReplaySnapshotProvider(temp.DatabasePath, context, new FixedTimeProvider(Now))
            .CaptureAsync(new RawReplaySelection(SessionIds: [sessionId.ToString("D")]), true, CancellationToken.None);

        Assert.True(capture.Success, capture.ErrorCode);
        await using var lease = Assert.IsType<RawReplaySnapshotLease>(capture.Lease);
        Assert.Equal(rawId, Assert.Single(lease.Snapshot.Records).RawRecordId);
        var content = Assert.Single(lease.Snapshot.SessionContents);
        Assert.Equal(eventId.ToString("D"), content.EventId);
        Assert.Equal(Now, content.OccurredAt);
        Assert.Equal("adapter-v1", content.AdapterVersion);
        Assert.Equal("{\"text\":\"synthetic\"}", content.ContentJson);
        Assert.Equal(2, Scalar<long>(temp.DatabasePath, "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
        Assert.Equal(1, Scalar<long>(temp.DatabasePath, "SELECT COUNT(DISTINCT owner) FROM retention_leases WHERE lease_kind='operation';"));
    }

    [Theory]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.RawRecordWithObservations, "$retention_read_source_token")]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.RawRecordWithObservations, "$retention_read_item_id")]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.RawRecordWithObservations, "$retention_read_revision")]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.RawRecordWithObservations, "$retention_read_lease_kind")]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.RawRecordWithObservations, "$retention_read_lease_owner")]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.RawRecordWithObservations, "$retention_read_lease_generation")]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.RawRecordWithObservations, "$retention_read_lease_expires_at")]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.RawRecordWithoutObservations, "$retention_read_source_token")]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.RawRecordWithoutObservations, "$retention_read_item_id")]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.RawRecordWithoutObservations, "$retention_read_revision")]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.RawRecordWithoutObservations, "$retention_read_lease_kind")]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.RawRecordWithoutObservations, "$retention_read_lease_owner")]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.RawRecordWithoutObservations, "$retention_read_lease_generation")]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.RawRecordWithoutObservations, "$retention_read_lease_expires_at")]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.SessionContent, "$retention_read_source_token")]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.SessionContent, "$retention_read_item_id")]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.SessionContent, "$retention_read_revision")]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.SessionContent, "$retention_read_lease_kind")]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.SessionContent, "$retention_read_lease_owner")]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.SessionContent, "$retention_read_lease_generation")]
    [InlineData((int)SqliteRawReplaySnapshotProvider.SnapshotReadShape.SessionContent, "$retention_read_lease_expires_at")]
    public async Task SnapshotReadCommand_EachAdmissionCapabilityMismatchReturnsNoRows(
        int shapeValue,
        string parameterName)
    {
        using var temp = new TempDirectory();
        var shape = (SqliteRawReplaySnapshotProvider.SnapshotReadShape)shapeValue;
        var fixture = SeedSnapshotCommandFixture(temp, shape);
        var request = new RetentionReadRequest(
            new RetentionOwnershipKey(fixture.Context.StoreInstanceId, fixture.StoreKind, fixture.SourceId),
            RetentionReadKind.Operation,
            Now,
            ExpectedRevision: null);

        var result = await new RetentionCatalogStore(fixture.Context, new FixedTimeProvider(Now)).ReadAsync<int[]>(
            request,
            (connection, transaction, grant, _) =>
            {
                using (var baseline = SqliteRawReplaySnapshotProvider.CreateSnapshotReadCommand(
                    connection,
                    transaction,
                    shape,
                    fixture.SourceId,
                    grant))
                {
                    Assert.Equal(1, CountRows(baseline));
                }

                using var perturbed = SqliteRawReplaySnapshotProvider.CreateSnapshotReadCommand(
                    connection,
                    transaction,
                    shape,
                    fixture.SourceId,
                    grant);
                PerturbAdmissionParameter(perturbed, parameterName);
                return ValueTask.FromResult<int[]?>([CountRows(perturbed)]);
            },
            CancellationToken.None);

        Assert.Null(result.Disposition);
        await using var lease = Assert.IsType<RetentionReadLease<int[]>>(result.Lease);
        Assert.Equal(0, Assert.Single(lease.Value));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task CaptureAsync_AcceptsTheExactRawMemberLimitAndRejectsAnOversizedMember(int excessBytes, bool accepted)
    {
        using var temp = new TempDirectory();
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(temp.DatabasePath, new FixedTimeProvider(Now));
        var store = new RawTelemetryStore(temp.DatabasePath, context, new FixedTimeProvider(Now));
        store.CreateMonitorSchema();
        var rawId = store.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "trace-large", Now, null, "{}"));
        SetTextLength(temp.DatabasePath, "raw_records", "payload_json", "id", rawId, RawReplayLimits.MaximumRawRecordBytes + excessBytes);

        var selection = new RawReplaySelection(RawRecordIds: [rawId]);
        if (!accepted)
        {
            await AssertPreflightFailure(temp.DatabasePath, context, selection, includeSessionContent: false, "entry_too_large");
            return;
        }

        var capture = await new SqliteRawReplaySnapshotProvider(temp.DatabasePath, context, new FixedTimeProvider(Now))
            .CaptureAsync(selection, includeSessionContent: false, CancellationToken.None);
        Assert.True(capture.Success, capture.ErrorCode);
        await using var lease = Assert.IsType<RawReplaySnapshotLease>(capture.Lease);
        Assert.Equal(rawId, Assert.Single(lease.Snapshot.Records).RawRecordId);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task CaptureAsync_AcceptsTheExactSessionMemberLimitAndRejectsAnOversizedMember(int excessBytes, bool accepted)
    {
        using var temp = new TempDirectory();
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(temp.DatabasePath, new FixedTimeProvider(Now));
        var rawStore = new RawTelemetryStore(temp.DatabasePath, context, new FixedTimeProvider(Now));
        rawStore.CreateMonitorSchema();
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "trace-content", Now, null, "{}"));
        var sessionStore = new SqliteSessionStore(temp.DatabasePath, context, new FixedTimeProvider(Now));
        sessionStore.CreateSchema();
        var sessionId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        sessionStore.Write(new SessionWriteBatch(
            new SessionDetail(
                new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full, null, null,
                    Now, Now, Now, SessionRawRetentionState.Expiring, Now, Now),
                [],
                [new ObservedSessionRun(runId, sessionId, SessionSourceSurface.CopilotCli, "run-native", "trace-content", null,
                    "fixture-model", ObservedSessionStatus.Completed, Now, Now, 1, 2, 3)],
                [new ObservedSessionEvent(eventId, sessionId, runId, SessionSourceSurface.CopilotCli, null, "trace-content", "ok",
                    "copilot-compatible-hook", "source-event", "assistant.completed", Now, SessionContentState.Available,
                    "app-v1", "adapter-v1", "schema-v1", "normalization-v1", SessionMatchKind.ExactNative)]),
            [new SessionEventContent(eventId, "assistant_response", "{}", Now, Now.AddDays(1))]));
        Execute(temp.DatabasePath,
            "INSERT INTO monitor_spans(raw_record_id,span_ordinal,trace_id,span_id,projected_at) VALUES($raw,0,'trace-content','span',$now);",
            ("$raw", rawId), ("$now", Now.ToString("O", CultureInfo.InvariantCulture)));
        SetTextLength(temp.DatabasePath, "session_event_content", "content_json", "event_id", eventId.ToString("D"), RawReplayLimits.MaximumSessionContentBytes + excessBytes);

        var selection = new RawReplaySelection(SessionIds: [sessionId.ToString("D")]);
        if (!accepted)
        {
            await AssertPreflightFailure(temp.DatabasePath, context, selection, includeSessionContent: true, "entry_too_large");
            return;
        }

        var capture = await new SqliteRawReplaySnapshotProvider(temp.DatabasePath, context, new FixedTimeProvider(Now))
            .CaptureAsync(selection, includeSessionContent: true, CancellationToken.None);
        Assert.True(capture.Success, capture.ErrorCode);
        await using var lease = Assert.IsType<RawReplaySnapshotLease>(capture.Lease);
        Assert.Equal(eventId.ToString("D"), Assert.Single(lease.Snapshot.SessionContents).EventId);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task CaptureAsync_AcceptsTheExactAggregateLimitAndRejectsAnOversizedAggregate(int excessBytes, bool accepted)
    {
        using var temp = new TempDirectory();
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(temp.DatabasePath, new FixedTimeProvider(Now));
        var store = new RawTelemetryStore(temp.DatabasePath, context, new FixedTimeProvider(Now));
        store.CreateMonitorSchema();
        var sizes = new[]
        {
            RawReplayLimits.MaximumRawRecordBytes,
            RawReplayLimits.MaximumRawRecordBytes,
            RawReplayLimits.MaximumRawRecordBytes,
            RawReplayLimits.MaximumRawRecordBytes,
            RawReplayLimits.MaximumArchiveBytes - 4 * RawReplayLimits.MaximumRawRecordBytes + excessBytes,
        };
        var ids = new List<long>();
        for (var index = 0; index < sizes.Length; index++)
        {
            var id = store.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, $"trace-{index}", Now, null, "{}"));
            SetTextLength(temp.DatabasePath, "raw_records", "payload_json", "id", id, sizes[index]);
            ids.Add(id);
        }

        var selection = new RawReplaySelection(RawRecordIds: ids);
        if (!accepted)
        {
            await AssertPreflightFailure(temp.DatabasePath, context, selection, includeSessionContent: false, "archive_too_large");
            return;
        }

        var capture = await new SqliteRawReplaySnapshotProvider(temp.DatabasePath, context, new FixedTimeProvider(Now))
            .CaptureAsync(selection, includeSessionContent: false, CancellationToken.None);
        Assert.True(capture.Success, capture.ErrorCode);
        await using var lease = Assert.IsType<RawReplaySnapshotLease>(capture.Lease);
        Assert.Equal(ids, lease.Snapshot.Records.Select(static record => record.RawRecordId));
    }

    private static void SeedTraceAndSession(string path, long rawRecordId, Guid sessionId, string traceId)
    {
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "INSERT INTO monitor_spans(raw_record_id,span_ordinal,trace_id,span_id,projected_at) VALUES($raw,0,$trace,'span-1',$now);", ("$raw", rawRecordId), ("$trace", traceId), ("$now", Now.ToString("O", CultureInfo.InvariantCulture)));
        Execute(connection, transaction, "INSERT INTO sessions(session_id,status,completeness,started_at,last_seen_at,raw_retention_state,created_at,updated_at) VALUES($session,'completed','full',$now,$now,'not_captured',$now,$now);", ("$session", sessionId.ToString("D")), ("$now", Now.ToString("O", CultureInfo.InvariantCulture)));
        Execute(connection, transaction, "INSERT INTO session_runs(run_id,session_id,source_surface,trace_id,status) VALUES($run,$session,'copilot-cli',$trace,'completed');", ("$run", Guid.CreateVersion7().ToString("D")), ("$session", sessionId.ToString("D")), ("$trace", traceId));
        transaction.Commit();
    }

    private static SnapshotCommandFixture SeedSnapshotCommandFixture(
        TempDirectory temp,
        SqliteRawReplaySnapshotProvider.SnapshotReadShape shape)
    {
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(temp.DatabasePath, new FixedTimeProvider(Now));
        var rawStore = new RawTelemetryStore(temp.DatabasePath, context, new FixedTimeProvider(Now));
        rawStore.CreateMonitorSchema();
        if (shape is SqliteRawReplaySnapshotProvider.SnapshotReadShape.RawRecordWithObservations
            or SqliteRawReplaySnapshotProvider.SnapshotReadShape.RawRecordWithoutObservations)
        {
            var rawRecordId = rawStore.Insert(new RawTelemetryRecord(
                null,
                RawTelemetrySources.RawOtlp,
                "trace-selector-capability",
                Now,
                null,
                "{\"resourceSpans\":[]}"));
            if (shape == SqliteRawReplaySnapshotProvider.SnapshotReadShape.RawRecordWithoutObservations)
                Execute(temp.DatabasePath, "DROP TABLE source_schema_observations;");
            return new(context, RetentionStoreKind.RawRecord, rawRecordId.ToString(CultureInfo.InvariantCulture));
        }

        var sessionStore = new SqliteSessionStore(temp.DatabasePath, context, new FixedTimeProvider(Now));
        sessionStore.CreateSchema();
        var sessionId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        sessionStore.Write(new SessionWriteBatch(
            new SessionDetail(
                new ObservedSession(
                    sessionId,
                    ObservedSessionStatus.Completed,
                    SessionCompleteness.Full,
                    null,
                    null,
                    Now,
                    Now,
                    Now,
                    SessionRawRetentionState.Expiring,
                    Now,
                    Now),
                [],
                [new ObservedSessionRun(
                    runId,
                    sessionId,
                    SessionSourceSurface.CopilotCli,
                    "run-selector-capability",
                    "trace-selector-capability",
                    null,
                    "fixture-model",
                    ObservedSessionStatus.Completed,
                    Now,
                    Now,
                    1,
                    2,
                    3)],
                [new ObservedSessionEvent(
                    eventId,
                    sessionId,
                    runId,
                    SessionSourceSurface.CopilotCli,
                    null,
                    "trace-selector-capability",
                    "ok",
                    "copilot-compatible-hook",
                    "source-event-selector-capability",
                    "assistant.completed",
                    Now,
                    SessionContentState.Available,
                    "app-v1",
                    "adapter-v1",
                    "schema-v1",
                    "normalization-v1",
                    SessionMatchKind.ExactNative)]),
            [new SessionEventContent(
                eventId,
                "assistant_response",
                "{\"text\":\"synthetic\"}",
                Now,
                Now.AddDays(1))]));
        return new(context, RetentionStoreKind.SessionEventContent, eventId.ToString("D"));
    }

    private static int CountRows(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read()) count++;
        return count;
    }

    private static void PerturbAdmissionParameter(SqliteCommand command, string parameterName)
    {
        var parameter = command.Parameters[parameterName];
        parameter.Value = parameterName switch
        {
            "$retention_read_source_token" => PerturbedToken(Assert.IsType<byte[]>(parameter.Value)),
            "$retention_read_item_id" => Assert.IsType<string>(parameter.Value) + "-mismatch",
            "$retention_read_revision" => Assert.IsType<long>(parameter.Value) + 1,
            "$retention_read_lease_kind" => Assert.IsType<string>(parameter.Value) == "operation" ? "access" : "operation",
            "$retention_read_lease_owner" => Assert.IsType<string>(parameter.Value) + "-mismatch",
            "$retention_read_lease_generation" => Assert.IsType<long>(parameter.Value) + 1,
            "$retention_read_lease_expires_at" => DateTimeOffset.Parse(
                    Assert.IsType<string>(parameter.Value),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind)
                .AddTicks(1)
                .ToString("O", CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException(nameof(parameterName)),
        };
    }

    private static byte[] PerturbedToken(byte[] sourceToken)
    {
        var value = sourceToken.ToArray();
        value[0] ^= byte.MaxValue;
        return value;
    }

    private static SelectedBatchFixture SeedSelectedBatchFixture(TempDirectory temp, DateTimeOffset boundaryExpiresAt)
    {
        var pinnedCapturedAt = Now.AddDays(-91);
        var clock = new TestTimeProvider(pinnedCapturedAt);
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(temp.DatabasePath, clock);
        var store = new RawTelemetryStore(temp.DatabasePath, context, clock);
        store.CreateMonitorSchema();
        var pinned = store.Insert(new RawTelemetryRecord(
            null,
            RawTelemetrySources.RawOtlp,
            "trace-pinned-past-expiry",
            pinnedCapturedAt,
            null,
            "{\"member\":\"pinned\"}"));
        Execute(
            temp.DatabasePath,
            "UPDATE retention_items SET state='retained_by_policy',revision=revision+1 WHERE store_kind='raw_record' AND source_item_id=$id AND state='expiring' AND read_denied_at IS NULL AND expires_at>$now;",
            ("$id", pinned.ToString(CultureInfo.InvariantCulture)),
            ("$now", pinnedCapturedAt.ToString("O", CultureInfo.InvariantCulture)));
        clock.Set(Now);
        var unexpired = store.Insert(new RawTelemetryRecord(
            null,
            RawTelemetrySources.RawOtlp,
            "trace-unexpired-expiring",
            Now,
            null,
            "{\"member\":\"unexpired\"}"));
        var boundary = store.Insert(new RawTelemetryRecord(
            null,
            RawTelemetrySources.RawOtlp,
            "trace-boundary-expiring",
            boundaryExpiresAt.AddDays(-90),
            null,
            "{\"member\":\"boundary\"}"));
        var boundarySibling = store.Insert(new RawTelemetryRecord(
            null,
            RawTelemetrySources.RawOtlp,
            "trace-unselected-boundary-expiring",
            boundaryExpiresAt.AddDays(-90),
            null,
            "{\"member\":\"unselected-boundary\"}"));
        var unrelated = store.Insert(new RawTelemetryRecord(
            null,
            RawTelemetrySources.RawOtlp,
            "trace-unrelated-live-lease",
            Now,
            null,
            "{\"member\":\"unrelated\"}"));
        Assert.True(pinned < unexpired
            && unexpired < boundary
            && boundary < boundarySibling
            && boundarySibling < unrelated);

        var catalog = new RetentionCatalogStore(context, clock);
        var pinnedItem = Assert.IsType<RetentionCatalogItem>(catalog.Find(new(
            context.StoreInstanceId,
            RetentionStoreKind.RawRecord,
            pinned.ToString(CultureInfo.InvariantCulture))));
        var unexpiredItem = Assert.IsType<RetentionCatalogItem>(catalog.Find(new(
            context.StoreInstanceId,
            RetentionStoreKind.RawRecord,
            unexpired.ToString(CultureInfo.InvariantCulture))));
        var boundaryItem = Assert.IsType<RetentionCatalogItem>(catalog.Find(new(
            context.StoreInstanceId,
            RetentionStoreKind.RawRecord,
            boundary.ToString(CultureInfo.InvariantCulture))));
        var boundarySiblingItem = Assert.IsType<RetentionCatalogItem>(catalog.Find(new(
            context.StoreInstanceId,
            RetentionStoreKind.RawRecord,
            boundarySibling.ToString(CultureInfo.InvariantCulture))));
        var unrelatedItem = Assert.IsType<RetentionCatalogItem>(catalog.Find(new(
            context.StoreInstanceId,
            RetentionStoreKind.RawRecord,
            unrelated.ToString(CultureInfo.InvariantCulture))));
        Assert.Equal(RetentionItemLifecycle.RetainedByPolicy, pinnedItem.State);
        Assert.True(pinnedItem.ExpiresAt < Now);
        Assert.Equal(RetentionItemLifecycle.Expiring, unexpiredItem.State);
        Assert.True(unexpiredItem.ExpiresAt > boundaryExpiresAt);
        Assert.Equal(RetentionItemLifecycle.Expiring, boundaryItem.State);
        Assert.Equal(boundaryExpiresAt, boundaryItem.ExpiresAt);
        Assert.Equal(RetentionItemLifecycle.Expiring, boundarySiblingItem.State);
        Assert.Equal(boundaryExpiresAt, boundarySiblingItem.ExpiresAt);
        Assert.Equal(0, Scalar<long>(temp.DatabasePath, "SELECT COUNT(*) FROM retention_leases;"));
        return new(
            context,
            clock,
            pinned,
            unexpired,
            boundary,
            boundarySibling,
            unrelatedItem,
            pinnedItem,
            unexpiredItem,
            boundaryItem,
            boundarySiblingItem);
    }

    private static void AuditRawOperationLeaseAcquisitions(
        string path,
        IReadOnlyList<long> selectedMemberIds,
        string unrelatedSentinelItemId,
        long? deleteWhenMemberId = null,
        long? deletedMemberId = null,
        long? guardMutationMemberId = null)
    {
        Assert.Equal(deleteWhenMemberId.HasValue, deletedMemberId.HasValue);
        var selectedIds = string.Join(
            ',',
            selectedMemberIds.Select(id => $"'{id.ToString(CultureInfo.InvariantCulture)}'"));
        var deleteClause = deleteWhenMemberId is { } leaseMemberId && deletedMemberId is { } deletedId
            ? $"""
                DELETE FROM raw_records
                WHERE id={deletedId.ToString(CultureInfo.InvariantCulture)}
                  AND NEW.item_id=(
                    SELECT item_id FROM retention_items
                    WHERE store_kind='raw_record'
                      AND source_item_id='{leaseMemberId.ToString(CultureInfo.InvariantCulture)}');
                """
            : string.Empty;
        var guardClause = guardMutationMemberId is { } guardedMemberId
            ? $"""
                CREATE TRIGGER test_raw_replay_mutation_after_release_guard
                BEFORE UPDATE ON retention_items
                WHEN OLD.item_id=(
                    SELECT item_id FROM retention_items
                    WHERE store_kind='raw_record'
                      AND source_item_id='{guardedMemberId.ToString(CultureInfo.InvariantCulture)}')
                  AND EXISTS(
                    SELECT 1
                    FROM retention_leases AS lease
                    JOIN retention_items AS selected ON selected.item_id=lease.item_id
                    WHERE lease.lease_kind='operation'
                      AND selected.store_kind='raw_record'
                      AND selected.source_item_id IN ({selectedIds}))
                BEGIN
                    SELECT RAISE(ABORT, 'selected leases must release before catalog mutation');
                END;
                """
            : string.Empty;
        Execute(path, $"""
            CREATE TABLE test_raw_replay_lease_audit(
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                source_item_id TEXT NOT NULL,
                active_count INTEGER NOT NULL,
                lease_kind TEXT NOT NULL,
                owner TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                generation INTEGER NOT NULL);
            CREATE TRIGGER test_raw_replay_lease_audit_trigger
            AFTER INSERT ON retention_leases
            WHEN NEW.lease_kind='operation'
              AND EXISTS(
                SELECT 1 FROM retention_items
                WHERE item_id=NEW.item_id
                  AND store_kind='raw_record'
                  AND source_item_id IN ({selectedIds}))
            BEGIN
                INSERT INTO test_raw_replay_lease_audit(
                    source_item_id,active_count,lease_kind,owner,expires_at,generation)
                SELECT source_item_id,(
                    SELECT COUNT(*)
                    FROM retention_leases AS lease
                    JOIN retention_items AS selected ON selected.item_id=lease.item_id
                    WHERE lease.lease_kind='operation'
                      AND selected.store_kind='raw_record'
                      AND selected.source_item_id IN ({selectedIds}))
                    ,NEW.lease_kind,NEW.owner,NEW.expires_at,NEW.generation
                FROM retention_items
                WHERE item_id=NEW.item_id AND store_kind='raw_record';
                INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation)
                VALUES('{unrelatedSentinelItemId}',NEW.lease_kind,NEW.owner,NEW.expires_at,NEW.generation)
                ON CONFLICT(item_id,lease_kind) DO UPDATE SET
                    owner=excluded.owner,
                    expires_at=excluded.expires_at,
                    generation=excluded.generation;
                {deleteClause}
            END;
            {guardClause}
            """);
    }

    private static void AssertUnrelatedLeaseSurvives(
        string path,
        SelectedBatchFixture fixture,
        LeaseAcquisitionAudit acquisition)
    {
        Assert.Equal(1, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));
        Assert.Equal(
            QuotedRow(
                path,
                fixture.UnrelatedItem.ItemId,
                acquisition.LeaseKind,
                acquisition.Owner,
                acquisition.ExpiresAt,
                acquisition.Generation),
            FullRowDump(path, "retention_leases", "item_id", fixture.UnrelatedItem.ItemId));
    }

    private static IReadOnlyList<LeaseAcquisitionAudit> ReadLeaseAcquisitionAudit(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_item_id,active_count,lease_kind,owner,expires_at,generation FROM test_raw_replay_lease_audit ORDER BY sequence;";
        using var reader = command.ExecuteReader();
        var rows = new List<LeaseAcquisitionAudit>();
        while (reader.Read())
            rows.Add(new(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5)));
        return rows;
    }

    private static void AssertSharedCompositeLeaseTuple(IReadOnlyList<LeaseAcquisitionAudit> acquisitions)
    {
        Assert.NotEmpty(acquisitions);
        var expected = acquisitions[0];
        Assert.Equal("operation", expected.LeaseKind);
        Assert.Matches("^[0-9a-f]{32}$", expected.Owner);
        Assert.Equal(Now.AddMinutes(2).ToString("O", CultureInfo.InvariantCulture), expected.ExpiresAt);
        Assert.Equal(1, expected.Generation);
        Assert.All(acquisitions, acquisition =>
        {
            Assert.Equal(expected.LeaseKind, acquisition.LeaseKind);
            Assert.Equal(expected.Owner, acquisition.Owner);
            Assert.Equal(expected.ExpiresAt, acquisition.ExpiresAt);
            Assert.Equal(expected.Generation, acquisition.Generation);
        });
    }

    private static void AssertRetentionAuthorityChangedOnly(
        string path,
        SelectedBatchFixture fixture,
        RetentionAuthorityState before,
        LeaseAcquisitionAudit sentinelTuple,
        string? normalizedItemId,
        params string[] normalizedItemColumns)
    {
        var after = CaptureRetentionAuthorityState(path, fixture.SelectedItemIds);
        var quotedNormalizedItemId = normalizedItemId is null
            ? null
            : Assert.Single(QuotedValues(path, normalizedItemId));
        var expectedSentinel = QuotedValues(
            path,
            fixture.UnrelatedItem.ItemId,
            sentinelTuple.LeaseKind,
            sentinelTuple.Owner,
            sentinelTuple.ExpiresAt,
            sentinelTuple.Generation);
        Assert.Equal(
            NormalizeRetentionAuthoritySnapshot(
                before,
                quotedNormalizedItemId,
                normalizedItemColumns,
                expectedSentinel),
            NormalizeRetentionAuthoritySnapshot(
                after,
                quotedNormalizedItemId,
                normalizedItemColumns,
                additionalLeaseRow: null));
    }

    private static RetentionAuthorityState CaptureRetentionAuthorityState(
        string path,
        IReadOnlyList<string> selectedItemIds)
    {
        using var connection = Open(path);
        var tableNames = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name GLOB 'retention_*' ORDER BY name COLLATE BINARY;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) tableNames.Add(reader.GetString(0));
        }

        var tables = new List<RetentionAuthorityTable>();
        foreach (var tableName in tableNames)
        {
            var columns = new List<RetentionAuthorityColumn>();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)});";
                using var reader = pragma.ExecuteReader();
                while (reader.Read())
                    columns.Add(new(reader.GetString(1), reader.GetInt32(5)));
            }

            Assert.NotEmpty(columns);
            var primaryKey = columns
                .Where(static column => column.PrimaryKeyOrdinal > 0)
                .OrderBy(static column => column.PrimaryKeyOrdinal)
                .ToArray();
            var orderColumns = primaryKey.Length > 0 ? primaryKey : columns.ToArray();
            using var rowsCommand = connection.CreateCommand();
            rowsCommand.CommandText = $"SELECT {string.Join(',', columns.Select(column => $"quote({QuoteIdentifier(column.Name)})"))} FROM {QuoteIdentifier(tableName)}";
            if (string.Equals(tableName, "retention_leases", StringComparison.Ordinal))
            {
                var parameterNames = new string[selectedItemIds.Count];
                for (var index = 0; index < selectedItemIds.Count; index++)
                {
                    parameterNames[index] = $"$selected_item_{index}";
                    rowsCommand.Parameters.AddWithValue(parameterNames[index], selectedItemIds[index]);
                }

                rowsCommand.CommandText += $" WHERE NOT (lease_kind='operation' AND item_id IN ({string.Join(',', parameterNames)}))";
            }

            rowsCommand.CommandText += $" ORDER BY {string.Join(',', orderColumns.Select(column => $"{QuoteIdentifier(column.Name)} COLLATE BINARY"))};";
            using var rowsReader = rowsCommand.ExecuteReader();
            var rows = new List<IReadOnlyList<string>>();
            while (rowsReader.Read())
                rows.Add(Enumerable.Range(0, rowsReader.FieldCount).Select(rowsReader.GetString).ToArray());
            tables.Add(new(tableName, columns, rows));
        }

        return new(tables);
    }

    private static string NormalizeRetentionAuthoritySnapshot(
        RetentionAuthorityState state,
        string? quotedNormalizedItemId,
        IReadOnlyCollection<string> normalizedItemColumns,
        IReadOnlyList<string>? additionalLeaseRow)
    {
        var lines = new List<string>();
        foreach (var table in state.Tables)
        {
            lines.Add($"TABLE|{table.Name}|{string.Join(',', table.Columns.Select(column => $"{column.Name}:{column.PrimaryKeyOrdinal}"))}");
            var rows = table.Rows.Select(static row => row.ToArray()).ToList();
            if (additionalLeaseRow is not null
                && string.Equals(table.Name, "retention_leases", StringComparison.Ordinal))
            {
                Assert.Equal(table.Columns.Count, additionalLeaseRow.Count);
                rows.Add(additionalLeaseRow.ToArray());
            }

            if (quotedNormalizedItemId is not null
                && string.Equals(table.Name, "retention_items", StringComparison.Ordinal))
            {
                var itemIdIndex = table.Columns.FindIndex(static column => column.Name == "item_id");
                Assert.True(itemIdIndex >= 0);
                foreach (var row in rows.Where(row => row[itemIdIndex] == quotedNormalizedItemId))
                {
                    foreach (var columnName in normalizedItemColumns)
                    {
                        var columnIndex = table.Columns.FindIndex(column => column.Name == columnName);
                        Assert.True(columnIndex >= 0);
                        row[columnIndex] = $"<normalized:{columnName}>";
                    }
                }
            }

            var primaryKeyIndexes = table.Columns
                .Select((column, index) => (column.PrimaryKeyOrdinal, Index: index))
                .Where(static entry => entry.PrimaryKeyOrdinal > 0)
                .OrderBy(static entry => entry.PrimaryKeyOrdinal)
                .Select(static entry => entry.Index)
                .ToArray();
            if (primaryKeyIndexes.Length == 0)
                primaryKeyIndexes = Enumerable.Range(0, table.Columns.Count).ToArray();
            rows.Sort((left, right) => CompareRows(left, right, primaryKeyIndexes));
            lines.AddRange(rows.Select(static row => $"ROW|{string.Join('|', row)}"));
        }

        return string.Join('\n', lines);
    }

    private static int CompareRows(IReadOnlyList<string> left, IReadOnlyList<string> right, IReadOnlyList<int> ordinals)
    {
        foreach (var ordinal in ordinals)
        {
            var comparison = StringComparer.Ordinal.Compare(left[ordinal], right[ordinal]);
            if (comparison != 0) return comparison;
        }

        return 0;
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string QuotedRow(string path, params object[] values) => string.Join('|', QuotedValues(path, values));

    private static IReadOnlyList<string> QuotedValues(string path, params object[] values)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(',', values.Select((_, index) => $"quote($value_{index})"))};";
        for (var index = 0; index < values.Length; index++)
            command.Parameters.AddWithValue($"$value_{index}", values[index]);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return Enumerable.Range(0, reader.FieldCount).Select(reader.GetString).ToArray();
    }

    private static async Task AssertPreflightFailure(
        string databasePath,
        RetentionCatalogContext context,
        RawReplaySelection selection,
        bool includeSessionContent,
        string expectedError)
    {
        var before = CatalogState(databasePath);

        var result = await new SqliteRawReplaySnapshotProvider(databasePath, context, new FixedTimeProvider(Now))
            .CaptureAsync(selection, includeSessionContent, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(expectedError, result.ErrorCode);
        Assert.Null(result.Lease);
        Assert.Equal(0, Scalar<long>(databasePath, "SELECT COUNT(*) FROM retention_leases;"));
        Assert.Equal(before, CatalogState(databasePath));
    }

    private static IReadOnlyList<string> CatalogState(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_id,state,revision,COALESCE(read_denied_at,''),COALESCE(queued_at,''),COALESCE(error_code,'')
            FROM retention_items ORDER BY item_id COLLATE BINARY;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue)));
        return rows;
    }

    private static string FullRowDump(string path, string table, string keyColumn, object key) =>
        RowDumpExcluding(path, table, keyColumn, key);

    private static string RowDumpExcluding(
        string path,
        string table,
        string keyColumn,
        object key,
        params string[] excludedColumns)
    {
        using var connection = Open(path);
        var columns = new List<string>();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info({table});";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                var column = reader.GetString(1);
                if (!excludedColumns.Contains(column, StringComparer.Ordinal)) columns.Add(column);
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(',', columns.Select(column => $"quote({column})"))} FROM {table} WHERE {keyColumn}=$key;";
        command.Parameters.AddWithValue("$key", key);
        using var row = command.ExecuteReader();
        Assert.True(row.Read());
        return string.Join("|", Enumerable.Range(0, row.FieldCount).Select(row.GetString));
    }

    private static void SetTextLength(string path, string table, string column, string keyColumn, object key, int size)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {table} SET {column}=CAST(zeroblob($size) AS TEXT) WHERE {keyColumn}=$key;";
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$key", key);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static long Scalar<T>(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string? TextScalar(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static void Execute(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open(path);
        Execute(connection, null, sql, parameters);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class TestTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private DateTimeOffset current = value;

        public override DateTimeOffset GetUtcNow() => current;

        internal void Set(DateTimeOffset value) => current = value;
    }

    private sealed class AdvanceTimeAfterMaterializationCheckpoint(
        TestTimeProvider timeProvider,
        DateTimeOffset boundary) : IRawReplaySnapshotCheckpoint
    {
        internal int InvocationCount { get; private set; }

        public void Reached(RawReplaySnapshotCheckpoint checkpoint)
        {
            Assert.Equal(RawReplaySnapshotCheckpoint.AfterNonNullMaterialization, checkpoint);
            InvocationCount++;
            timeProvider.Set(boundary);
        }
    }

    private sealed record SelectedBatchFixture(
        RetentionCatalogContext Context,
        TestTimeProvider Clock,
        long PinnedId,
        long UnexpiredId,
        long BoundaryId,
        long BoundarySiblingId,
        RetentionCatalogItem UnrelatedItem,
        RetentionCatalogItem PinnedItem,
        RetentionCatalogItem UnexpiredItem,
        RetentionCatalogItem BoundaryItem,
        RetentionCatalogItem BoundarySiblingItem)
    {
        internal IReadOnlyList<long> CanonicalIds => [PinnedId, UnexpiredId, BoundaryId];
        internal IReadOnlyList<long> RequestedIds => [BoundaryId, UnexpiredId, PinnedId];
        internal IReadOnlyList<string> SelectedItemIds => [PinnedItem.ItemId, UnexpiredItem.ItemId, BoundaryItem.ItemId];
        internal IReadOnlyList<RetentionCatalogItem> Items => [PinnedItem, UnexpiredItem, BoundaryItem];
    }

    private sealed record LeaseAcquisitionAudit(
        string SourceItemId,
        long ActiveCount,
        string LeaseKind,
        string Owner,
        string ExpiresAt,
        long Generation);

    private sealed record RetentionAuthorityColumn(string Name, int PrimaryKeyOrdinal);

    private sealed record RetentionAuthorityTable(
        string Name,
        List<RetentionAuthorityColumn> Columns,
        List<IReadOnlyList<string>> Rows);

    private sealed record RetentionAuthorityState(List<RetentionAuthorityTable> Tables);

    private sealed record SnapshotCommandFixture(
        RetentionCatalogContext Context,
        RetentionStoreKind StoreKind,
        string SourceId);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"raw-replay-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string DatabasePath => System.IO.Path.Combine(Path, "raw-store.db");

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
