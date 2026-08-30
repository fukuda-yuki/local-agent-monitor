using System.Globalization;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

// Group 6: the frozen generic raw content route must deny every skill.invoked Event before any
// Retention lease, content column selection, or materialization, and it must do so inside the same
// transaction that inserts the lease and selects the content so no racing type change can slip a
// v2 payload across the boundary.
public sealed class GenericRouteContentDenialTests
{
    private const string DefaultAdapter = "copilot-sdk-stream";
    private const string DefaultSurface = "copilot-sdk";
    private const string DefaultPayloadSchema = "github-copilot-sdk.skill-invoked.v1";
    private static readonly DateTimeOffset WriteAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ASkillInvokedEventIsIndistinguishableFromMissing()
    {
        using var database = new TestDatabase();
        var write = CommitSkillWrite(database, "native-denied");

        var result = await database.CreateStore().ReadGenericRouteContentAsync(
            write.NewSessionId, write.EventId, CancellationToken.None);

        Assert.Equal(SessionGenericRouteContentDisposition.NotFound, result.Disposition);
        Assert.Null(result.Lease);
    }

    [Fact]
    public async Task AMissingEventIsTheSameNotFound()
    {
        using var database = new TestDatabase();

        var result = await database.CreateStore().ReadGenericRouteContentAsync(
            Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None);

        Assert.Equal(SessionGenericRouteContentDisposition.NotFound, result.Disposition);
        Assert.Null(result.Lease);
    }

    [Fact]
    public async Task ADeniedSkillEventNeverAcquiresALeaseOrSelectsContent()
    {
        using var database = new TestDatabase();
        var write = CommitSkillWrite(database, "native-no-lease");
        var leasesBefore = database.Count("retention_leases");

        var statements = new List<string>();
        var store = database.CreateStore(statements.Add);
        var result = await store.ReadGenericRouteContentAsync(
            write.NewSessionId, write.EventId, CancellationToken.None);

        Assert.Equal(SessionGenericRouteContentDisposition.NotFound, result.Disposition);
        Assert.Equal(leasesBefore, database.Count("retention_leases"));
        Assert.DoesNotContain(statements, statement =>
            statement.Contains("session_event_content", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(statements, statement =>
            statement.Contains("retention_leases", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ThePolicyCheckAndTheContentSelectionShareOneImmediateTransaction()
    {
        using var database = new TestDatabase();
        var identity = InsertNonSkillEvent(database.DatabasePath, "native-one-transaction");

        var statements = new List<string>();
        var store = database.CreateStore(statements.Add);
        var result = await store.ReadGenericRouteContentAsync(
            identity.SessionId, identity.EventId, CancellationToken.None);

        Assert.Equal(SessionGenericRouteContentDisposition.Granted, result.Disposition);
        await result.Lease!.DisposeAsync();

        var begins = statements.Count(statement =>
            statement.TrimStart().StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, begins);
        Assert.Contains(statements, statement =>
            statement.Contains("BEGIN IMMEDIATE", StringComparison.OrdinalIgnoreCase));

        // The type policy read and the content selection are both inside that one transaction.
        var policyIndex = statements.FindIndex(statement =>
            statement.Contains("SELECT type,content_state", StringComparison.Ordinal));
        var selectionIndex = statements.FindIndex(statement =>
            statement.Contains("FROM session_event_content", StringComparison.Ordinal));
        var commitIndex = statements.FindIndex(statement =>
            statement.TrimStart().StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase));

        Assert.True(policyIndex >= 0, "the type-only policy query did not run");
        Assert.True(selectionIndex > policyIndex, "the content selection did not follow the policy query");
        Assert.True(commitIndex > selectionIndex, "the transaction committed before the content selection");
    }

    [Fact]
    public async Task ANonSkillEventStillGrantsItsFrozenContent()
    {
        using var database = new TestDatabase();
        var identity = InsertNonSkillEvent(database.DatabasePath, "native-allowed");

        var result = await database.CreateStore().ReadGenericRouteContentAsync(
            identity.SessionId, identity.EventId, CancellationToken.None);

        Assert.Equal(SessionGenericRouteContentDisposition.Granted, result.Disposition);
        Assert.NotNull(result.Lease);

        await using (result.Lease!.ConfigureAwait(false))
        {
            using var reference = result.Lease.AcquireContentReference();
            Assert.Equal(identity.EventId, reference.Content.EventId);
            Assert.Equal(NonSkillContentJson, reference.Content.ContentJson);
        }
    }

    [Fact]
    public async Task AMalformedStorageIsTheSanitizedUnavailableResult()
    {
        using var database = new TestDatabase();
        var identity = InsertNonSkillEvent(database.DatabasePath, "native-malformed");
        database.Execute("ALTER TABLE session_events RENAME TO session_events_moved;");

        var result = await database.CreateStore().ReadGenericRouteContentAsync(
            identity.SessionId, identity.EventId, CancellationToken.None);

        Assert.Equal(SessionGenericRouteContentDisposition.Unavailable, result.Disposition);
        Assert.Null(result.Lease);
    }

    [Fact]
    public async Task ATypeChangeCommittedBeforeThePolicyReadDeniesTheRequest()
    {
        using var database = new TestDatabase();
        var identity = InsertNonSkillEvent(database.DatabasePath, "native-preexisting-change");
        database.Execute(
            $"UPDATE session_events SET type='skill.invoked' WHERE event_id='{identity.EventId:D}';");

        var result = await database.CreateStore().ReadGenericRouteContentAsync(
            identity.SessionId, identity.EventId, CancellationToken.None);

        Assert.Equal(SessionGenericRouteContentDisposition.NotFound, result.Disposition);
        Assert.Null(result.Lease);
    }

    // The two windows inside the held transaction where a racing type change could otherwise
    // matter: right after the policy decision, and while the Retention lease is being inserted.
    [Theory]
    [InlineData("SELECT type,content_state")]
    [InlineData("INSERT INTO retention_leases")]
    public async Task ATypeChangeAttemptedInsideTheTransactionCannotCommit(string observedStatement)
    {
        using var database = new TestDatabase();
        var identity = InsertNonSkillEvent(database.DatabasePath, "native-racing-change");

        var policyObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The observer holds the route's transaction open until a competing writer has had its
        // turn, so the window between the policy read and the content selection is the exact
        // window the mutation is attempted in.
        var store = database.CreateStore(statement =>
        {
            if (!statement.Contains(observedStatement, StringComparison.Ordinal) || policyObserved.Task.IsCompleted)
            {
                return;
            }

            policyObserved.TrySetResult();
            mutationAttempted.Task.Wait(TimeSpan.FromSeconds(30));
        });

        var readActor = BlockingTestActor.Start(() => store
            .ReadGenericRouteContentAsync(identity.SessionId, identity.EventId, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult());

        await readActor.Entered;
        await policyObserved.Task;

        SqliteException? mutationFailure = null;
        try
        {
            using var competing = database.OpenWithoutBusyTimeout();
            using var command = competing.CreateCommand();

            // Microsoft.Data.Sqlite retries SQLITE_BUSY up to CommandTimeout regardless of the
            // busy_timeout pragma, so the retry budget is what has to be bounded here.
            command.CommandTimeout = 1;
            command.CommandText =
                $"UPDATE session_events SET type='skill.invoked' WHERE event_id='{identity.EventId:D}';";
            command.ExecuteNonQuery();
        }
        catch (SqliteException exception)
        {
            mutationFailure = exception;
        }

        mutationAttempted.TrySetResult();
        var result = await readActor.Completion;

        // SQLITE_BUSY: the route already holds BEGIN IMMEDIATE, so no type change can commit
        // between its policy decision and its content selection.
        Assert.NotNull(mutationFailure);
        Assert.Equal(5, mutationFailure!.SqliteErrorCode);

        Assert.Equal(SessionGenericRouteContentDisposition.Granted, result.Disposition);
        await using (result.Lease!.ConfigureAwait(false))
        {
            using var reference = result.Lease.AcquireContentReference();
            Assert.Equal(NonSkillContentJson, reference.Content.ContentJson);
        }

        Assert.Equal(
            "user_prompt",
            database.ScalarText($"SELECT type FROM session_events WHERE event_id='{identity.EventId:D}';"));
    }

    [Fact]
    public async Task AMissingContentRowIsDeniedBeforeAnyLeaseIsInserted()
    {
        using var database = new TestDatabase();
        var identity = InsertNonSkillEvent(database.DatabasePath, "native-missing-content");
        var leasesBefore = database.Count("retention_leases");

        // The Event still reports readable content, but the content row is gone, so the Retention
        // source proof denies the admission before it inserts a lease.
        database.Execute($"DELETE FROM session_event_content WHERE event_id='{identity.EventId:D}';");

        var result = await database.CreateStore().ReadGenericRouteContentAsync(
            identity.SessionId, identity.EventId, CancellationToken.None);

        Assert.Equal(SessionGenericRouteContentDisposition.Denied, result.Disposition);
        Assert.Null(result.Lease);
        Assert.Equal(leasesBefore, database.Count("retention_leases"));
    }

    [Fact]
    public async Task ASelectorFailureAfterTheLeaseRollsTheUncommittedLeaseBack()
    {
        using var database = new TestDatabase();
        var identity = InsertNonSkillEvent(database.DatabasePath, "native-selector-failure");
        var leasesBefore = database.Count("retention_leases");

        var catalog = new RetentionCatalogStore(database.RetentionContext, new FixedTimeProvider(WriteAt));
        var request = new RetentionReadRequest(
            new(database.RetentionContext.StoreInstanceId, RetentionStoreKind.SessionEventContent, identity.EventId.ToString("D")),
            RetentionReadKind.Access,
            WriteAt,
            ExpectedRevision: null);

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        // The lease is inserted, then the selector refuses the value: the transaction-aware arm
        // must roll the whole thing back rather than leave a committed lease behind.
        var result = await catalog.ReadWithinCallerTransactionAsync<string>(
            connection,
            transaction,
            request,
            (_, _, _, _) => ValueTask.FromResult<string?>(null),
            CancellationToken.None);

        Assert.Equal(RetentionReadDisposition.SelectorUnavailable, result.Disposition);
        Assert.Null(result.Lease);
        Assert.Equal(leasesBefore, database.Count("retention_leases"));
    }

    // The terminal proof re-reads the clock, so the buffered non-Skill 200 is authorized by the
    // exact expiry tick rather than by the moment the lease was granted.
    [Theory]
    [InlineData(-1, (int)SessionContentTerminalResult.Sealed)]
    [InlineData(0, (int)SessionContentTerminalResult.Lost)]
    [InlineData(1, (int)SessionContentTerminalResult.Lost)]
    public async Task TheBufferedSealIsDecidedAtTheExactLeaseExpiryTick(int expiryTickOffset, int expected)
    {
        using var database = new TestDatabase();
        var identity = InsertNonSkillEvent(database.DatabasePath, $"native-seal-tick{expiryTickOffset}");
        var clock = new GenericRouteContentClock(WriteAt);

        var result = await database.CreateStore(clock).ReadGenericRouteContentAsync(
            identity.SessionId, identity.EventId, CancellationToken.None);

        Assert.Equal(SessionGenericRouteContentDisposition.Granted, result.Disposition);
        await using (result.Lease!.ConfigureAwait(false))
        {
            using (var reference = result.Lease.AcquireContentReference())
            {
                Assert.Equal(NonSkillContentJson, reference.Content.ContentJson);
            }

            clock.UtcNow = LeaseExpiryTick(expiryTickOffset);
            Assert.Equal((SessionContentTerminalResult)expected, result.Lease.TrySealRawResponse());
        }
    }

    // A due expiry notification is the only authority that retires a still-published grant before
    // the terminal proof runs, so its boundary is pinned by the one observable it alone changes:
    // whether the buffered content is still acquirable once the notification has run.
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    public async Task AnExpiryNotificationRetiresThePublishedGrantOnlyFromItsExactExpiryTick(
        int expiryTickOffset,
        bool remainsUsable)
    {
        using var database = new TestDatabase();
        var identity = InsertNonSkillEvent(database.DatabasePath, $"native-notify-tick{expiryTickOffset}");
        var clock = new GenericRouteContentClock(WriteAt);

        var result = await database.CreateStore(clock).ReadGenericRouteContentAsync(
            identity.SessionId, identity.EventId, CancellationToken.None);

        Assert.Equal(SessionGenericRouteContentDisposition.Granted, result.Disposition);
        await using (result.Lease!.ConfigureAwait(false))
        {
            clock.UtcNow = LeaseExpiryTick(expiryTickOffset);
            clock.FireLeaseExpiryNotification();

            if (!remainsUsable)
            {
                Assert.Throws<InvalidOperationException>(() => result.Lease.AcquireContentReference());
                Assert.Equal(SessionContentTerminalResult.Lost, result.Lease.TrySealRawResponse());
                return;
            }

            using (var reference = result.Lease.AcquireContentReference())
            {
                Assert.Equal(NonSkillContentJson, reference.Content.ContentJson);
            }

            Assert.Equal(SessionContentTerminalResult.Sealed, result.Lease.TrySealRawResponse());
        }
    }

    [Fact]
    public async Task AnExpiryNotificationDueWhileTheHandleIsHiddenNeverPublishesTheGrant()
    {
        using var database = new TestDatabase();
        var identity = InsertNonSkillEvent(database.DatabasePath, "native-hidden-handle");
        var clock = new GenericRouteContentClock(WriteAt);
        clock.FireOnceWhenTheLeaseExpiryNotificationArms();

        var result = await database.CreateStore(clock).ReadGenericRouteContentAsync(
            identity.SessionId, identity.EventId, CancellationToken.None);

        Assert.Equal(SessionGenericRouteContentDisposition.Denied, result.Disposition);
        Assert.Null(result.Lease);
    }

    // The item's own lease expiry can cross while the caller's transaction is still open, either
    // before the content selection or at the commit. Neither window may publish the grant: the
    // committed handle has to fail its publication proof instead of handing the caller content it
    // no longer holds a live lease for.
    [Theory]
    [InlineData("SELECT c.event_id,c.content_kind,c.content_json")]
    [InlineData("COMMIT")]
    public async Task ALeaseExpiryCrossedInsideTheTransactionNeverPublishesTheGrant(string injectedStatement)
    {
        using var database = new TestDatabase();
        var identity = InsertNonSkillEvent(database.DatabasePath, "native-expiry-in-transaction");
        var clock = new GenericRouteContentClock(WriteAt);
        var crossed = false;

        var store = database.CreateStore(clock, statement =>
        {
            if (crossed || !statement.Contains(injectedStatement, StringComparison.Ordinal)) return;
            crossed = true;
            clock.UtcNow = LeaseExpiryTick(0);
        });

        var result = await store.ReadGenericRouteContentAsync(
            identity.SessionId, identity.EventId, CancellationToken.None);

        Assert.True(crossed, "the injected expiry window was never entered");
        Assert.Equal(SessionGenericRouteContentDisposition.Denied, result.Disposition);
        Assert.Null(result.Lease);
    }

    // The value-publication proof is the last fence before the internal pre-publication buffer would
    // become caller-accessible. The clock crosses the lease expiry after the selector has already
    // produced the value and after the pre-consumption re-proof has passed, so only this fence can
    // still refuse it.
    [Fact]
    public async Task ALeaseExpiryCrossedAtValuePublicationNeverHandsOverTheBufferedContent()
    {
        using var database = new TestDatabase();
        var identity = InsertNonSkillEvent(database.DatabasePath, "native-expiry-value-publication");
        var clock = new GenericRouteContentClock(WriteAt);
        var catalog = new RetentionCatalogStore(
            database.RetentionContext,
            clock,
            new ExpiryCrossingBoundaryCheckpoint(
                clock, RetentionReadBoundaryCheckpoint.AfterValueSelector, LeaseExpiryTick(0)));

        var request = new RetentionReadRequest(
            new(database.RetentionContext.StoreInstanceId, RetentionStoreKind.SessionEventContent, identity.EventId.ToString("D")),
            RetentionReadKind.Access,
            WriteAt,
            ExpectedRevision: null);

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        var result = await catalog.ReadWithinCallerTransactionAsync(
            connection,
            transaction,
            request,
            (_, _, _, _) => ValueTask.FromResult<string?>(NonSkillContentJson),
            CancellationToken.None);

        Assert.Equal(RetentionReadDisposition.LeaseLost, result.Disposition);
        Assert.Null(result.Lease);
    }

    private static DateTimeOffset LeaseExpiryTick(int offset) =>
        WriteAt + RetentionV1Constants.LeaseDuration + TimeSpan.FromTicks(offset);

    private const string NonSkillContentJson = """{"message":"synthetic"}""";

    private static SessionSkillInvocationWrite CommitSkillWrite(TestDatabase database, string nativeSessionId)
    {
        var write = new SessionSkillInvocationWrite(
            SourceAdapter: DefaultAdapter,
            SourceSurface: DefaultSurface,
            SourceEventId: Guid.NewGuid().ToString("D"),
            SourceParentEventId: null,
            NativeSessionId: nativeSessionId,
            RunNativeId: null,
            SourceEphemeral: false,
            OccurredAt: WriteAt,
            SourceApplicationVersion: "1.0.65",
            AdapterVersion: "adapter-version-1",
            NormalizationVersion: "normalization-1",
            PayloadSchema: DefaultPayloadSchema,
            SchemaFingerprint: new string('a', 64),
            PayloadTokenUtf8: "{\"skill\":\"demo\"}"u8.ToArray(),
            State: "available",
            Reason: "none",
            Name: "demo-skill",
            Source: "project",
            Trigger: "user-invoked",
            BodySha256: new string('b', 64),
            BodyUtf8Bytes: 7L,
            DefinitionPathSha256: new string('c', 64),
            DefinitionPathUtf8Bytes: 12L,
            EventId: Guid.CreateVersion7(),
            SnapshotId: Guid.CreateVersion7(),
            ClaimId: Guid.CreateVersion7(),
            NewSessionId: Guid.CreateVersion7(),
            NewRunId: Guid.CreateVersion7(),
            WriteAt: WriteAt,
            ExpiresAt: WriteAt.AddDays(90));

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var outcome = SessionSkillInvocationParticipant.InsertOrVerify(connection, transaction, write);
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, outcome);
        transaction.Commit();
        return write;
    }

    // A plain v1 non-Skill Event with readable content and a live Retention item. It is built
    // directly rather than by mutating a Skill write, because the Session 14 child trigger makes a
    // session_events row immutable while a snapshot references it -- which is exactly why no
    // supported path can turn an existing non-Skill Event into a Skill one.
    internal static (Guid SessionId, Guid EventId) InsertNonSkillEvent(string databasePath, string nativeSessionId)
    {
        var sessionId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        var at = FormatTimestamp(WriteAt);
        var expiresAt = FormatTimestamp(WriteAt.AddDays(90));
        var sourceEventId = Guid.NewGuid().ToString("D");
        var ownerToken = new byte[32];
        Random.Shared.NextBytes(ownerToken);

        using var connection = Open(databasePath);
        using var transaction = connection.BeginTransaction();

        Execute(connection, transaction,
            """
            INSERT INTO sessions(
                session_id,status,completeness,repository,workspace,started_at,ended_at,
                last_seen_at,raw_retention_state,created_at,updated_at)
            VALUES($session_id,'active','partial',NULL,NULL,NULL,NULL,$at,'expiring',$at,$at);
            """,
            ("$session_id", sessionId.ToString("D")), ("$at", at));

        Execute(connection, transaction,
            """
            INSERT INTO session_native_ids(session_id,source_surface,native_session_id,binding_kind,observed_at)
            VALUES($session_id,'copilot-sdk',$native,'native',$at);
            """,
            ("$session_id", sessionId.ToString("D")), ("$native", nativeSessionId), ("$at", at));

        Execute(connection, transaction,
            """
            INSERT INTO session_events(
                event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,
                source_adapter,source_event_id,type,occurred_at,content_state,
                source_application_version,adapter_version,schema_fingerprint,normalization_version,
                match_kind,terminal_outcome,terminal_policy_version)
            VALUES(
                $event_id,$session_id,NULL,'copilot-sdk',NULL,NULL,NULL,
                $source_adapter,$source_event_id,'user_prompt',$at,'available',
                '1.0.65','adapter-version-1',$fingerprint,'normalization-1',
                NULL,NULL,NULL);
            """,
            ("$event_id", eventId.ToString("D")),
            ("$session_id", sessionId.ToString("D")),
            ("$source_adapter", DefaultAdapter),
            ("$source_event_id", sourceEventId),
            ("$at", at),
            ("$fingerprint", new string('a', 64)));

        Execute(connection, transaction,
            """
            INSERT INTO session_event_content(event_id,content_kind,content_json,captured_at,expires_at,retention_owner_token)
            VALUES($event_id,'user_prompt',$content_json,$at,$expires_at,$owner_token);
            """,
            ("$event_id", eventId.ToString("D")),
            ("$content_json", NonSkillContentJson),
            ("$at", at),
            ("$expires_at", expiresAt),
            ("$owner_token", ownerToken));

        new RetentionCatalogStore(databasePath, new FixedTimeProvider(WriteAt))
            .RegisterSessionEventContent(
                connection,
                transaction,
                eventId.ToString("D"),
                "user_prompt",
                WriteAt,
                WriteAt.AddDays(90),
                sessionId.ToString("D"),
                null,
                DefaultAdapter,
                sourceEventId,
                ownerToken);

        transaction.Commit();
        return (sessionId, eventId);
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'", CultureInfo.InvariantCulture);

    private sealed class TestDatabase : IDisposable
    {
        private readonly RetentionCatalogContext retentionContext;

        internal TestDatabase()
        {
            Root = Path.Combine(Path.GetTempPath(), $"generic-route-denial-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            DatabasePath = Path.Combine(Root, "monitor.db");

            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);
                transaction.Commit();
            }

            using (var retentionConnection = Open())
            using (var retentionTransaction = retentionConnection.BeginTransaction())
            {
                RetentionSchemaMigrator.Apply(retentionConnection, retentionTransaction);
                retentionTransaction.Commit();
            }

            new SqliteSourceCompatibilityStore(DatabasePath).CreateSchema();
            new SqliteSessionStore(DatabasePath).CreateSchema();

            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                SkillProjectionSchemaV1.Ensure(connection, transaction);
                LocalRepositoryCatalogSchemaV1.Ensure(connection, transaction);
                LocalArchiveSchemaV1.Ensure(connection, transaction);
                SkillInvocationSnapshotSchemaV1.Ensure(connection, transaction);
                transaction.Commit();
            }

            retentionContext = RetentionCatalogContext.InitializeNewOwnedDatabase(
                DatabasePath, new FixedTimeProvider(WriteAt));
        }

        internal string Root { get; }

        internal string DatabasePath { get; }

        internal RetentionCatalogContext RetentionContext => retentionContext;

        internal SqliteSessionStore CreateStore(Action<string>? statementObserver = null) =>
            statementObserver is null
                ? new SqliteSessionStore(DatabasePath, retentionContext, new FixedTimeProvider(WriteAt))
                : new SqliteSessionStore(
                    DatabasePath,
                    retentionContext,
                    new FixedTimeProvider(WriteAt),
                    _ => { },
                    statementObserver);

        internal SqliteSessionStore CreateStore(TimeProvider timeProvider) =>
            new(DatabasePath, retentionContext, timeProvider);

        internal SqliteSessionStore CreateStore(TimeProvider timeProvider, Action<string> statementObserver) =>
            new(DatabasePath, retentionContext, timeProvider, _ => { }, statementObserver);

        internal SqliteConnection Open() => GenericRouteContentDenialTests.Open(DatabasePath);

        internal SqliteConnection OpenWithoutBusyTimeout()
        {
            var connection = Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout=0;";
            pragma.ExecuteNonQuery();
            return connection;
        }

        internal void Execute(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        internal long Count(string table)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        internal string ScalarText(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ExpiryCrossingBoundaryCheckpoint(
        GenericRouteContentClock clock,
        RetentionReadBoundaryCheckpoint target,
        DateTimeOffset crossedAt) : IRetentionReadBoundaryCheckpoint
    {
        public void Reached(RetentionReadBoundaryCheckpoint checkpoint)
        {
            if (checkpoint == target) clock.UtcNow = crossedAt;
        }
    }
}

// Group 6's expiry matrix has to enter the lease-expiry and hidden-handle windows at an exact tick
// instead of hoping to observe them, so this clock never moves on its own and never fires a timer
// on its own. Only the access lease's own two-minute arming is tracked: it is the single timer the
// Retention expiry notification arms for this route, which keeps the hook off every unrelated timer
// a composed host also creates.
internal sealed class GenericRouteContentClock(DateTimeOffset start) : TimeProvider
{
    private readonly object gate = new();
    private DateTimeOffset now = start;
    private ControlledTimer? leaseExpiryNotification;
    private bool fireOnArm;
    private int timerArmCount;
    private int leaseExpiryArmCount;

    internal DateTimeOffset UtcNow
    {
        get { lock (gate) { return now; } }
        set { lock (gate) { now = value; } }
    }

    // A renewed operation grant replaces its expiry notification, so the arming counts are the
    // observable that separates "the original two-minute notification" from "a rescheduled one".
    internal int TimerArmCount
    {
        get { lock (gate) { return timerArmCount; } }
    }

    internal int LeaseExpiryArmCount
    {
        get { lock (gate) { return leaseExpiryArmCount; } }
    }

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ControlledTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    // Fires the notification synchronously on the arming thread, which is inside Activate before the
    // committed handle has left its hidden state.
    internal void FireOnceWhenTheLeaseExpiryNotificationArms()
    {
        lock (gate)
        {
            fireOnArm = true;
        }
    }

    internal void FireLeaseExpiryNotification()
    {
        ControlledTimer timer;
        lock (gate)
        {
            timer = leaseExpiryNotification
                ?? throw new InvalidOperationException("No lease expiry notification has been armed.");
        }

        timer.Fire();
    }

    private void Armed(ControlledTimer timer, TimeSpan dueTime)
    {
        lock (gate)
        {
            timerArmCount++;
        }

        if (dueTime != RetentionV1Constants.LeaseDuration) return;
        bool fire;
        lock (gate)
        {
            leaseExpiryArmCount++;
            leaseExpiryNotification = timer;
            fire = fireOnArm;
            fireOnArm = false;
        }

        if (fire) timer.Fire();
    }

    internal sealed class ControlledTimer(GenericRouteContentClock clock, TimerCallback callback, object? state) : ITimer
    {
        private int disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (Volatile.Read(ref disposed) != 0) return false;
            if (dueTime != Timeout.InfiniteTimeSpan) clock.Armed(this, dueTime);
            return true;
        }

        internal void Fire()
        {
            if (Volatile.Read(ref disposed) == 0) callback(state);
        }

        public void Dispose() => Interlocked.Exchange(ref disposed, 1);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
