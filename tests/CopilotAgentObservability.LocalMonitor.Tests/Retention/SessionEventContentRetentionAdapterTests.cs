using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class SessionEventContentRetentionAdapterTests
{
    [Fact]
    public async Task SessionAdapter_DeletesOnlyExactContentAtomically()
    {
        using var fixture = await Fixture.CreateAsync(refreshAfterQueue: true);
        Assert.Equal("instruction|json_pointer|/prompt|read_denied", fixture.Text("SELECT part||'|'||locator_kind||'|'||json_pointer||'|'||availability_state FROM local_workspace_node_content_refs WHERE source_item_id=$target;"));
        var result = await fixture.Adapter.DeleteAsync(fixture.Context);

        Assert.Same(RetentionAdapterResult.Deleted, result);
        Assert.Equal(0L, fixture.Count("SELECT COUNT(*) FROM session_event_content WHERE event_id=$target;"));
        Assert.Equal(1L, fixture.Count("SELECT COUNT(*) FROM session_event_content WHERE event_id=$sibling;"));
        Assert.Equal(fixture.SiblingContent, fixture.Text("SELECT content_json FROM session_event_content WHERE event_id=$sibling;"));
        Assert.Equal(fixture.SiblingCapturedAt, fixture.Text("SELECT captured_at FROM session_event_content WHERE event_id=$sibling;"));
        Assert.Equal(fixture.SiblingExpiresAt, fixture.Text("SELECT expires_at FROM session_event_content WHERE event_id=$sibling;"));
        Assert.Equal(fixture.SessionSnapshot, fixture.Text("SELECT session_id || '|' || status || '|' || completeness || '|' || last_seen_at || '|' || created_at || '|' || updated_at FROM sessions;"));
        Assert.Equal(fixture.RunSnapshot, fixture.Text("SELECT run_id || '|' || session_id || '|' || trace_id || '|' || status || '|' || started_at FROM session_runs;"));
        Assert.Equal(fixture.EventSnapshot, fixture.Text("SELECT group_concat(event_id || '|' || session_id || '|' || IFNULL(run_id,'') || '|' || source_adapter || '|' || source_event_id || '|' || occurred_at || '|' || content_state, ';') FROM session_events ORDER BY event_id;"));
        Assert.Equal(fixture.NativeIdSnapshot, fixture.Text("SELECT session_id || '|' || source_surface || '|' || native_session_id || '|' || binding_kind || '|' || observed_at FROM session_native_ids;"));
        Assert.Equal(fixture.ProjectionSnapshot, fixture.Text("SELECT projector_key || '|' || IFNULL(projection_cursor,'') || '|' || unsupported_event_version_count || '|' || updated_at FROM session_projection_state;"));
        Assert.Equal(1L, fixture.Count("SELECT durable_cursor FROM retention_delete_journal WHERE item_id=$item;"));
        Assert.Equal("deleted", fixture.Text("SELECT state FROM retention_items WHERE item_id=$item;"));
        Assert.Equal(1L, fixture.Count("SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$item;"));
        Assert.Equal("instruction|json_pointer|/prompt|deleted", fixture.Text("SELECT part||'|'||locator_kind||'|'||json_pointer||'|'||availability_state FROM local_workspace_node_content_refs WHERE source_item_id=$target;"));
        using (var connection = new SqliteConnection($"Data Source={fixture.Path};Pooling=False"))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup.LocalWorkspaceProjectionBackupValidation.Validate(
                connection, transaction,
                skillRegistryAuthority: FixedSkillRegistryGenerationAuthority.ForWriterVersion(
                    SkillInvocationV2ArtifactRegistry.CurrentWriterVersion));
        }
        Assert.Equal("1|1|1", fixture.Text("SELECT (retention_owner_token IS NULL)||'|'||(retention_ownership_receipt IS NULL)||'|'||(retention_store_instance_id IS NULL) FROM local_workspace_node_content_refs WHERE source_item_id=$target;"));

        using (var connection = new SqliteConnection($"Data Source={fixture.Path};Pooling=False"))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            new LocalWorkspaceProjectionTransactionParticipant(FixedSkillRegistryGenerationAuthority.ForWriterVersion(
                SkillInvocationV2ArtifactRegistry.CurrentWriterVersion)).RefreshSessions(
                    connection, transaction, [fixture.SessionId], DateTimeOffset.Parse("2026-07-20T00:00:00Z"));
            transaction.Commit();
        }
        Assert.Equal("instruction|json_pointer|/prompt|deleted", fixture.Text("SELECT part||'|'||locator_kind||'|'||json_pointer||'|'||availability_state FROM local_workspace_node_content_refs WHERE source_item_id=$target;"));
    }

    [Fact]
    public async Task SessionAdapter_RollbackAfterRealParticipantRefreshRestoresPriorContentReference()
    {
        using var fixture = await Fixture.CreateAsync(failAfterRefresh: true);
        var before = fixture.Text("SELECT part||'|'||locator_kind||'|'||json_pointer||'|'||availability_state||'|'||retention_revision||'|'||hex(retention_owner_token) FROM local_workspace_node_content_refs WHERE source_item_id=$target;");

        var result = await fixture.Adapter.DeleteAsync(fixture.Context);

        Assert.NotSame(RetentionAdapterResult.Deleted, result);
        Assert.Equal(1L, fixture.Count("SELECT COUNT(*) FROM session_event_content WHERE event_id=$target;"));
        Assert.Equal(before, fixture.Text("SELECT part||'|'||locator_kind||'|'||json_pointer||'|'||availability_state||'|'||retention_revision||'|'||hex(retention_owner_token) FROM local_workspace_node_content_refs WHERE source_item_id=$target;"));
        Assert.Equal(0L, fixture.Count("SELECT COUNT(*) FROM local_workspace_content_tombstones WHERE source_item_id=$target;"));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("oversized")]
    public async Task SessionAdapter_PreservesExactSemanticBindingFromTrustedPreTerminalStates(string availabilityState)
    {
        using var fixture = await Fixture.CreateAsync(refreshAfterQueue: true);
        fixture.Execute("UPDATE local_workspace_node_content_refs SET availability_state=$state WHERE source_item_id=$target;", ("$state", availabilityState));

        Assert.Same(RetentionAdapterResult.Deleted, await fixture.Adapter.DeleteAsync(fixture.Context));
        Assert.Equal("instruction|json_pointer|/prompt|6|deleted", fixture.Text("SELECT part||'|'||locator_kind||'|'||json_pointer||'|'||selected_utf8_bytes||'|'||availability_state FROM local_workspace_node_content_refs WHERE source_item_id=$target;"));
        Assert.Equal("instruction|json_pointer|/prompt|6", fixture.Text("SELECT part||'|'||locator_kind||'|'||json_pointer||'|'||selected_utf8_bytes FROM local_workspace_content_tombstones WHERE source_item_id=$target;"));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("not_captured")]
    public async Task SessionAdapter_DoesNotPromoteUntrustedPreTerminalRowsIntoExactTombstones(string availabilityState)
    {
        using var fixture = await Fixture.CreateAsync(refreshAfterQueue: true);
        fixture.Execute("UPDATE local_workspace_node_content_refs SET availability_state=$state WHERE source_item_id=$target;", ("$state", availabilityState));

        Assert.Same(RetentionAdapterResult.Deleted, await fixture.Adapter.DeleteAsync(fixture.Context));
        Assert.Equal(0L, fixture.Count("SELECT COUNT(*) FROM local_workspace_content_tombstones WHERE source_item_id=$target;"));
        Assert.Equal("not_captured", fixture.Text("SELECT availability_state FROM local_workspace_node_content_refs WHERE source_item_id=$target;"));

        fixture.RefreshProjection(DateTimeOffset.Parse("2026-07-20T00:00:00Z"));
        Assert.Equal("not_captured", fixture.Text("SELECT availability_state FROM local_workspace_node_content_refs WHERE source_item_id=$target;"));
        Assert.Equal(0L, fixture.Count("SELECT COUNT(*) FROM local_workspace_content_tombstones WHERE source_item_id=$target;"));
    }

    internal sealed class Fixture : IDisposable
    {
        private Fixture(string path, SessionEventContentRetentionAdapter adapter, RetentionDeleteContext context, RetentionCatalogStore catalog, MutableTimeProvider time, ILocalWorkspaceProjectionTransactionParticipant participant, string sessionId, string targetEventId, string siblingEventId, string siblingContent, string siblingCapturedAt, string siblingExpiresAt, string sessionSnapshot, string runSnapshot, string eventSnapshot, string nativeIdSnapshot, string projectionSnapshot)
            => (Path, Adapter, Context, Catalog, Time, Participant, SessionId, TargetEventId, SiblingEventId, SiblingContent, SiblingCapturedAt, SiblingExpiresAt, SessionSnapshot, RunSnapshot, EventSnapshot, NativeIdSnapshot, ProjectionSnapshot) = (path, adapter, context, catalog, time, participant, sessionId, targetEventId, siblingEventId, siblingContent, siblingCapturedAt, siblingExpiresAt, sessionSnapshot, runSnapshot, eventSnapshot, nativeIdSnapshot, projectionSnapshot);

        internal string Path { get; }
        internal SessionEventContentRetentionAdapter Adapter { get; }
        internal RetentionDeleteContext Context { get; }
        internal RetentionCatalogStore Catalog { get; }
        internal MutableTimeProvider Time { get; }
        internal ILocalWorkspaceProjectionTransactionParticipant Participant { get; }
        internal string SessionId { get; }
        internal string TargetEventId { get; }
        internal string SiblingEventId { get; }
        internal string SiblingContent { get; }
        internal string SiblingCapturedAt { get; }
        internal string SiblingExpiresAt { get; }
        internal string SessionSnapshot { get; }
        internal string RunSnapshot { get; }
        internal string EventSnapshot { get; }
        internal string NativeIdSnapshot { get; }
        internal string ProjectionSnapshot { get; }

        internal string RevisionStateRows() => Text("""
            SELECT
              (SELECT group_concat(item_id||'|'||state||'|'||revision||'|'||IFNULL(read_denied_at,'')||'|'||IFNULL(queued_at,'')||'|'||IFNULL(deletion_started_at,'')||'|'||attempt_count||'|'||IFNULL(error_code,''),';') FROM retention_items) || '#' ||
              (SELECT group_concat(node_id||'|'||part||'|'||availability_state||'|'||revision_input||'|'||IFNULL(retention_revision,-1)||'|'||IFNULL(hex(retention_owner_token),''),';') FROM local_workspace_node_content_refs) || '#' ||
              (SELECT COUNT(*) FROM session_event_content) || '#' ||
              (SELECT COUNT(*) FROM local_workspace_content_tombstones) || '#' ||
              (SELECT COUNT(*) FROM retention_tombstones);
            """);

        internal long Count(string sql) => Convert.ToInt64(Scalar(sql));
        internal string Text(string sql) => (string)Scalar(sql)!;
        internal void Execute(string sql, params (string Name, object Value)[] values) => Execute(Path, sql, [("$target", TargetEventId), .. values]);
        internal void DriftSourceOwnerToken() => Execute(
            "DROP TRIGGER retention_session_event_content_token_immutable; UPDATE session_event_content SET retention_owner_token=randomblob(32) WHERE event_id=$target;");
        internal void RefreshProjection(DateTimeOffset now)
        {
            using var connection = Open(Path);
            using var transaction = connection.BeginTransaction();
            new LocalWorkspaceProjectionTransactionParticipant(FixedSkillRegistryGenerationAuthority.ForWriterVersion(
                SkillInvocationV2ArtifactRegistry.CurrentWriterVersion)).RefreshSessions(connection, transaction, [SessionId], now);
            transaction.Commit();
        }

        internal static async Task<Fixture> CreateAsync(bool failAfterRefresh = false, bool refreshAfterQueue = false, bool prepareDeletion = true)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"session-retention-adapter-{Guid.NewGuid():N}.db");
            var now = new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);
            var time = new MutableTimeProvider(now);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            var sessionStore = new SqliteSessionStore(path, context, time);
            sessionStore.CreateSchema();
            var batch = CreateBatch(now);
            sessionStore.Write(batch);
            var catalog = new RetentionCatalogStore(context, time);
            var authority = FixedSkillRegistryGenerationAuthority.ForWriterVersion(
                SkillInvocationV2ArtifactRegistry.CurrentWriterVersion);
            using (var connection = Open(path))
            {
                using var transaction = connection.BeginTransaction();
                LocalRepositoryCatalogSchemaV1.Ensure(connection, transaction);
                LocalArchiveSchemaV1.Ensure(connection, transaction);
                SkillProjectionSchemaV1.Ensure(connection, transaction);
                SkillInvocationSnapshotSchemaV1.Ensure(connection, transaction);
                transaction.Commit();
                LocalWorkspaceProjectionSchemaV1.Ensure(connection, now, authority);
            }
            ILocalWorkspaceProjectionTransactionParticipant participant =
                new LocalWorkspaceProjectionTransactionParticipant(authority);
            if (failAfterRefresh) participant = new ThrowAfterRefreshParticipant(participant);
            var gate = new LocalWorkspacePublicationGate();
            Execute(path, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");
            var target = batch.Content[0].EventId.ToString("D").ToLowerInvariant();
            var sibling = batch.Content[1].EventId.ToString("D").ToLowerInvariant();
            var item = Text(path, "SELECT item_id FROM retention_items WHERE store_kind='session_event_content' AND source_item_id=$source;", ("$source", target));
            if (prepareDeletion)
            {
                Execute(path, "UPDATE retention_items SET state='deletion_queued',revision=1,read_denied_at=$now,queued_at=$now WHERE item_id=$item;", ("$item", item), ("$now", now.ToString("O")));
                if (refreshAfterQueue)
                {
                    using var connection = Open(path);
                    using var transaction = connection.BeginTransaction();
                    new LocalWorkspaceProjectionTransactionParticipant(authority).RefreshSessions(
                        connection, transaction, [batch.Detail.Session.SessionId.ToString("D").ToLowerInvariant()], now);
                    transaction.Commit();
                }
            }
            RetentionDeleteContext deleteContext = null!;
            if (prepareDeletion)
            {
                var claim = (await catalog.TryClaimDeletionAsync(new(item, 1, RetentionWorkKind.Queued), "session-adapter", now, CancellationToken.None)).Claim!;
                var intent = await catalog.EnsureDeleteIntentAsync(claim.Fence, 0, now, CancellationToken.None);
                Assert.Equal(RetentionIntentDisposition.Committed, intent.Disposition);
                deleteContext = new(claim.Fence.ItemId, claim.StoreInstanceId, claim.StoreKind, claim.Fence.ExpectedRevision, claim.Fence.LeaseOwner, claim.Fence.LeaseGeneration, claim.SourceIdentity, null, intent.IntentCursor, CancellationToken.None);
            }

            Execute(path, "INSERT INTO session_projection_state(projector_key,projection_cursor,unsupported_event_version_count,updated_at) VALUES('preserved',7,0,$now);", ("$now", now.ToString("O")));
            return new Fixture(path, new SessionEventContentRetentionAdapter(catalog, time, participant, gate), deleteContext, catalog, time, participant, batch.Detail.Session.SessionId.ToString("D").ToLowerInvariant(), target, sibling,
                Text(path, "SELECT content_json FROM session_event_content WHERE event_id=$sibling;", ("$sibling", sibling)),
                Text(path, "SELECT captured_at FROM session_event_content WHERE event_id=$sibling;", ("$sibling", sibling)),
                Text(path, "SELECT expires_at FROM session_event_content WHERE event_id=$sibling;", ("$sibling", sibling)),
                Text(path, "SELECT session_id || '|' || status || '|' || completeness || '|' || last_seen_at || '|' || created_at || '|' || updated_at FROM sessions;"),
                Text(path, "SELECT run_id || '|' || session_id || '|' || trace_id || '|' || status || '|' || started_at FROM session_runs;"),
                Text(path, "SELECT group_concat(event_id || '|' || session_id || '|' || IFNULL(run_id,'') || '|' || source_adapter || '|' || source_event_id || '|' || occurred_at || '|' || content_state, ';') FROM session_events ORDER BY event_id;"),
                Text(path, "SELECT session_id || '|' || source_surface || '|' || native_session_id || '|' || binding_kind || '|' || observed_at FROM session_native_ids;"),
                Text(path, "SELECT projector_key || '|' || IFNULL(projection_cursor,'') || '|' || unsupported_event_version_count || '|' || updated_at FROM session_projection_state;"));
        }

        private object? Scalar(string sql) => Scalar(Path, sql, ("$target", TargetEventId), ("$sibling", SiblingEventId), ("$item", Context.ItemId));
        public void Dispose() { SqliteConnection.ClearAllPools(); foreach (var file in new[] { Path, Path + "-wal", Path + "-shm" }) if (File.Exists(file)) File.Delete(file); }

        private static SessionWriteBatch CreateBatch(DateTimeOffset now)
        {
            var traceId = new string('a', 32);
            var session = new ObservedSession(Guid.CreateVersion7(), ObservedSessionStatus.Active, SessionCompleteness.Rich, "owner/repository", "workspace", now.AddMinutes(-2), null, now, SessionRawRetentionState.Expiring, now.AddMinutes(-2), now);
            var native = new SessionNativeId(session.SessionId, SessionSourceSurface.CopilotSdk, "native-session", SessionBindingKind.Native, now.AddMinutes(-2));
            var run = new ObservedSessionRun(Guid.CreateVersion7(), session.SessionId, SessionSourceSurface.CopilotSdk, "native-run", traceId, null, "gpt-5", ObservedSessionStatus.Active, now.AddMinutes(-1), null, 10, 20, 30);
            var target = new ObservedSessionEvent(Guid.CreateVersion7(), session.SessionId, run.RunId, SessionSourceSurface.CopilotSdk, null, traceId, "received", "claude-code-hook", "target", "UserPromptSubmit", now, SessionContentState.Available, SchemaFingerprint: new string('a', 64));
            var sibling = new ObservedSessionEvent(Guid.CreateVersion7(), session.SessionId, run.RunId, SessionSourceSurface.CopilotSdk, null, traceId, "received", "copilot-sdk-stream", "sibling", "assistant.message", now.AddSeconds(1), SessionContentState.Available);
            return new(new SessionDetail(session, [native], [run], [target, sibling]), [new SessionEventContent(target.EventId, "application/json", "{\"prompt\":\"target\"}", now, now.AddDays(90)), new SessionEventContent(sibling.EventId, "application/json", "{\"text\":\"sibling\"}", now.AddSeconds(1), now.AddDays(90).AddSeconds(1))]);
        }

        private static void Execute(string path, string sql, params (string Name, object Value)[] values) { using var connection = Open(path); using var command = connection.CreateCommand(); command.CommandText = sql; foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value); command.ExecuteNonQuery(); }
        private static string Text(string path, string sql, params (string Name, object Value)[] values) => (string)Scalar(path, sql, values)!;
        private static object? Scalar(string path, string sql, params (string Name, object Value)[] values) { using var connection = Open(path); using var command = connection.CreateCommand(); command.CommandText = sql; foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value); return command.ExecuteScalar(); }
        private static SqliteConnection Open(string path) { var connection = new SqliteConnection($"Data Source={path};Pooling=False"); connection.Open(); return connection; }

        private sealed class ThrowAfterRefreshParticipant(ILocalWorkspaceProjectionTransactionParticipant inner) : ILocalWorkspaceProjectionTransactionParticipant
        {
            public void RefreshSessions(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyCollection<string> sessionIds, DateTimeOffset now)
            {
                inner.RefreshSessions(connection, transaction, sessionIds, now);
                throw new InvalidOperationException("injected_after_workspace_refresh");
            }

            public void CompleteSessionEventContentDeletion(SqliteConnection connection, SqliteTransaction transaction, string sourceItemId, DateTimeOffset now)
            {
                inner.CompleteSessionEventContentDeletion(connection, transaction, sourceItemId, now);
                throw new InvalidOperationException("injected_after_workspace_refresh");
            }
        }
    }
}
