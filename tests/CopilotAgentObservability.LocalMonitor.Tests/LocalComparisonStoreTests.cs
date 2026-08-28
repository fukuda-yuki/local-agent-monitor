using System.Security.Cryptography;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalComparisonStoreTests
{
    [Fact]
    public void AcceptReadAndCleanup_PreserveFrozenIdentityAndDeterministicExpiry()
    {
        using var database = new ComparisonDatabase();
        database.Initialize();
        var clock = new FixedTimeProvider(CreatedAt);
        var store = new SqliteLocalComparisonStore(database.Path, clock);
        var snapshot = Snapshot();

        Assert.Equal(LocalComparisonAcceptStatus.Accepted, store.Accept(snapshot, default));
        Assert.Equal(LocalComparisonAcceptStatus.Identical, store.Accept(snapshot, default));

        var found = store.Read(RepositoryId, ComparisonId, default);
        Assert.Equal(LocalComparisonReadStatus.Found, found.Status);
        Assert.NotNull(found.Snapshot);
        Assert.Equal([SessionA, SessionB], found.Snapshot.Memberships.Select(item => item.SessionId));
        var inputTokens = found.Snapshot.Results.Single(item =>
            item.SectionOrdinal == 2 && item.RowKey == "input_tokens");
        Assert.Equal("10", inputTokens.Values.Single(item => item.Key == "a_median").Value);

        var wrongRepository = store.Read(OtherRepositoryId, ComparisonId, default);
        Assert.Equal(LocalComparisonReadStatus.NotFound, wrongRepository.Status);
        Assert.Null(wrongRepository.Snapshot);

        var changed = snapshot with
        {
            ScopeConditionSha256 = SHA256.HashData(Bytes("changed")),
        };
        Assert.Throws<InvalidOperationException>(() => store.Accept(changed, default));

        clock.Set(ExpiresAt);
        Assert.Equal(LocalComparisonReadStatus.Expired,
            store.Read(RepositoryId, ComparisonId, default).Status);
        Assert.Equal(
            new LocalComparisonCleanupResult(LocalComparisonCleanupStatus.Completed, 1),
            store.CleanupExpired(default));
        Assert.Equal(LocalComparisonReadStatus.Expired,
            store.Read(RepositoryId, ComparisonId, default).Status);

        using var connection = database.Open();
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM local_comparison_snapshots;"));
        Assert.Equal(1L, Scalar(connection,
            "SELECT COUNT(*) FROM local_comparison_expiry_tombstones WHERE comparison_id='" + ComparisonId + "' AND repository_id='" + RepositoryId + "' AND expired_at='" + Timestamp(ExpiresAt) + "';"));
        LocalComparisonSchemaV1.Validate(connection, transaction: null);
    }

    [Fact]
    public void ImmutableTables_RejectDirectUpdateAndDelete()
    {
        using var database = new ComparisonDatabase();
        database.Initialize();
        var store = new SqliteLocalComparisonStore(database.Path, new FixedTimeProvider(CreatedAt));
        Assert.Equal(LocalComparisonAcceptStatus.Accepted, store.Accept(Snapshot(), default));

        using var connection = database.Open();
        Assert.Throws<SqliteException>(() => Execute(connection,
            "UPDATE local_comparison_results SET row_key='other' WHERE result_ordinal=1;"));
        Assert.Throws<SqliteException>(() => Execute(connection,
            "DELETE FROM local_comparison_snapshots WHERE comparison_id='" + ComparisonId + "';"));
        Assert.Throws<SqliteException>(() => Execute(connection, $"""
            INSERT INTO local_comparison_cohort_memberships(
              comparison_id,cohort,ordinal,session_id,workspace_revision,fact_frame,fact_sha256)
            SELECT comparison_id,'a',199,'{SessionC}',workspace_revision,fact_frame,fact_sha256
            FROM local_comparison_cohort_memberships WHERE cohort='a' AND ordinal=0;
            """));
        Assert.Throws<SqliteException>(() => Execute(connection, """
            INSERT INTO local_comparison_results(
              comparison_id,result_ordinal,section_ordinal,row_kind,row_key,payload,payload_sha256)
            SELECT comparison_id,1000000,section_ordinal,row_kind,'appended-row',payload,payload_sha256
            FROM local_comparison_results WHERE result_ordinal=1;
            """));
        Assert.Throws<SqliteException>(() => Execute(connection, """
            INSERT INTO local_comparison_evidence(
              comparison_id,result_ordinal,evidence_ordinal,field_key,cohort,session_id,
              availability_state,source_kind,source_identity,trace_id,span_id,event_id,revision_sha256)
            SELECT comparison_id,result_ordinal,1000000,field_key,cohort,session_id,
                   availability_state,source_kind,source_identity,trace_id,span_id,event_id,revision_sha256
            FROM local_comparison_evidence WHERE result_ordinal=1 ORDER BY evidence_ordinal LIMIT 1;
            """));
    }

    [Fact]
    public void FrozenSnapshot_DoesNotRetainOrReResolveDeletedSourceSessions()
    {
        using var database = new ComparisonDatabase();
        database.Initialize();
        var store = new SqliteLocalComparisonStore(database.Path, new FixedTimeProvider(CreatedAt));
        Assert.Equal(LocalComparisonAcceptStatus.Accepted, store.Accept(Snapshot(), default));

        using (var connection = database.Open())
            Execute(connection, "DELETE FROM sessions WHERE session_id='" + SessionA + "';");

        var read = store.Read(RepositoryId, ComparisonId, default);
        Assert.Equal(LocalComparisonReadStatus.Found, read.Status);
        Assert.Contains(Assert.IsType<LocalComparisonFrozenSnapshot>(read.Snapshot).Memberships,
            item => item.SessionId == SessionA);
    }

    [Fact]
    public void ImmutableTables_RejectInsertOrReplaceWithRecursiveTriggersDisabled()
    {
        using var database = new ComparisonDatabase();
        database.Initialize();
        var clock = new FixedTimeProvider(CreatedAt);
        var store = new SqliteLocalComparisonStore(database.Path, clock);
        Assert.Equal(LocalComparisonAcceptStatus.Accepted, store.Accept(Snapshot(), default));

        using (var connection = database.Open())
        {
            Execute(connection, "PRAGMA recursive_triggers=OFF;");
            Assert.Throws<SqliteException>(() => Execute(connection, """
                INSERT OR REPLACE INTO local_comparison_snapshots
                SELECT comparison_id,'0198f5b8-0c00-7000-8000-000000000021',created_at,expires_at,
                       selection_frame,selection_sha256,scope_condition_sha256
                FROM local_comparison_snapshots;
                """));
            Assert.Throws<SqliteException>(() => Execute(connection, """
                INSERT OR REPLACE INTO local_comparison_cohort_memberships
                SELECT comparison_id,cohort,ordinal,session_id,
                       'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                       fact_frame,fact_sha256
                FROM local_comparison_cohort_memberships WHERE cohort='a';
                """));
            Assert.Throws<SqliteException>(() => Execute(connection, """
                INSERT OR REPLACE INTO local_comparison_results
                SELECT comparison_id,result_ordinal,section_ordinal,row_kind,row_key,payload,
                       'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
                FROM local_comparison_results WHERE result_ordinal=1;
                """));
            Assert.Throws<SqliteException>(() => Execute(connection, """
                INSERT OR REPLACE INTO local_comparison_evidence
                SELECT comparison_id,result_ordinal,evidence_ordinal,field_key,cohort,session_id,
                       'capture_gap',source_kind,source_identity,trace_id,span_id,event_id,revision_sha256
                FROM local_comparison_evidence WHERE result_ordinal=1 AND evidence_ordinal=0;
                """));
        }

        clock.Set(ExpiresAt);
        Assert.Equal(
            new LocalComparisonCleanupResult(LocalComparisonCleanupStatus.Completed, 1),
            store.CleanupExpired(default));
        using var tombstoneConnection = database.Open();
        Execute(tombstoneConnection, "PRAGMA recursive_triggers=OFF;");
        Assert.Throws<SqliteException>(() => Execute(tombstoneConnection, """
            INSERT OR REPLACE INTO local_comparison_expiry_tombstones
            SELECT comparison_id,'0198f5b8-0c00-7000-8000-000000000021',expired_at
            FROM local_comparison_expiry_tombstones;
            """));
    }

    [Fact]
    public void Accept_ReturnsTheClosedPersistenceBusyOutcomeForAWriteLeaseConflict()
    {
        using var database = new ComparisonDatabase();
        database.Initialize();
        using var blocker = database.Open();
        using var transaction = blocker.BeginTransaction(deferred: false);
        var store = new SqliteLocalComparisonStore(database.Path, new FixedTimeProvider(CreatedAt));

        Assert.Equal(
            LocalComparisonAcceptStatus.PersistenceBusy,
            store.Accept(Snapshot(), default));
    }

    [Fact]
    public void CleanupExpired_DistinguishesNoOpFromPersistenceBusy()
    {
        using var database = new ComparisonDatabase();
        database.Initialize();
        var clock = new FixedTimeProvider(CreatedAt);
        var store = new SqliteLocalComparisonStore(database.Path, clock);
        Assert.Equal(
            new LocalComparisonCleanupResult(LocalComparisonCleanupStatus.Completed, 0),
            store.CleanupExpired(default));

        using var blocker = database.Open();
        using var transaction = blocker.BeginTransaction(deferred: false);
        Assert.Equal(
            new LocalComparisonCleanupResult(LocalComparisonCleanupStatus.PersistenceBusy, 0),
            store.CleanupExpired(default));
    }

    [Fact]
    public void Accept_RejectsSelfHashedPartialResultAndOpaqueFactGraphs()
    {
        using var database = new ComparisonDatabase();
        database.Initialize();
        var store = new SqliteLocalComparisonStore(
            database.Path,
            new FixedTimeProvider(CreatedAt));
        var valid = Snapshot();

        var partialResults = valid.Results
            .Where(item => item.ResultOrdinal != valid.Results[^1].ResultOrdinal)
            .ToArray();
        var partialEvidence = valid.Evidence
            .Where(item => item.ResultOrdinal != valid.Results[^1].ResultOrdinal)
            .ToArray();
        var partial = ReReceipt(valid, valid.Memberships, partialResults, partialEvidence);
        Assert.Throws<InvalidOperationException>(() => store.Accept(partial, default));

        var arbitraryFrame = Bytes("self-hashed-arbitrary-fact");
        var memberships = valid.Memberships.ToArray();
        memberships[0] = memberships[0] with
        {
            FactFrame = arbitraryFrame,
            FactSha256 = Hash(arbitraryFrame),
        };
        var opaque = ReReceipt(valid, memberships, valid.Results, valid.Evidence);
        Assert.Throws<InvalidOperationException>(() => store.Accept(opaque, default));

        var mutatedResults = valid.Results.ToArray();
        var inputIndex = Array.FindIndex(mutatedResults, item =>
            item.SectionOrdinal == 2 && item.RowKey == "input_tokens");
        var input = mutatedResults[inputIndex];
        mutatedResults[inputIndex] = LocalComparisonStoredResult.Create(
            input.ComparisonId,
            input.ResultOrdinal,
            input.SectionOrdinal,
            input.RowKind,
            input.RowKey,
            input.Values.Select(item => item.Key == "a_median"
                ? new KeyValuePair<string, string>(item.Key, "999")
                : item).ToArray());
        var selfHashedWrongFormula = ReReceipt(
            valid, valid.Memberships, mutatedResults, valid.Evidence);
        Assert.Throws<InvalidOperationException>(() =>
            store.Accept(selfHashedWrongFormula, default));
    }

    [Fact]
    public void Accept_RejectsSelfReceiptedNonCanonicalFactOrdering()
    {
        using var database = new ComparisonDatabase();
        database.Initialize();
        var store = new SqliteLocalComparisonStore(
            database.Path,
            new FixedTimeProvider(CreatedAt));
        var referenceA = new LocalComparisonSourceReference(
            "workspace_session", SessionA, null, null, null, Revision);
        var runReference = new LocalComparisonSourceReference(
            "session_run", RunA, null, null, null, Revision);
        var referenceB = new LocalComparisonSourceReference(
            "workspace_session", SessionB, null, null, null, Revision);
        var a = Session(SessionA, referenceA, 10m);
        a = a with
        {
            Scalars = new Dictionary<string, LocalComparisonObservedScalar>(a.Scalars, StringComparer.Ordinal)
            {
                ["input_tokens"] = new(
                    new(LocalComparisonFactState.Recorded, 10m),
                    Array.AsReadOnly(new[]
                    {
                        new LocalComparisonFactEvidence(LocalComparisonFactState.Recorded, runReference),
                        new LocalComparisonFactEvidence(LocalComparisonFactState.Recorded, referenceA),
                    })),
            },
        };
        var prepared = new LocalComparisonApplicationService(
            store: null,
            new FixedTimeProvider(CreatedAt),
            _ => ComparisonId).Prepare(new(
                RepositoryId,
                new([a], 0),
                new([Session(SessionB, referenceB, 20m)], 0),
                ScopeConditionDigest()));
        var valid = Assert.IsType<LocalComparisonSnapshotWrite>(prepared.Snapshot);
        var decoded = LocalComparisonFactFrame.Decode(valid.Memberships[0].FactFrame);
        var input = decoded.Scalars["input_tokens"];
        var reversed = decoded with
        {
            Scalars = new Dictionary<string, LocalComparisonObservedScalar>(decoded.Scalars, StringComparer.Ordinal)
            {
                ["input_tokens"] = new(input.Observation,
                    Array.AsReadOnly(input.Evidence.Reverse().ToArray())),
            },
        };
        var frame = LocalComparisonFactFrame.Create(reversed);
        var memberships = valid.Memberships.ToArray();
        memberships[0] = memberships[0] with
        {
            FactFrame = frame,
            FactSha256 = Hash(frame),
        };
        var nonCanonical = ReReceipt(valid, memberships, valid.Results, valid.Evidence);

        Assert.Throws<InvalidOperationException>(() => store.Accept(nonCanonical, default));
    }

    private static LocalComparisonSnapshotWrite ReReceipt(
        LocalComparisonSnapshotWrite source,
        IReadOnlyList<LocalComparisonStoredMembership> memberships,
        IReadOnlyList<LocalComparisonStoredResult> results,
        IReadOnlyList<LocalComparisonStoredEvidence> evidence)
    {
        var nonReceipt = results.Where(static item => item.ResultOrdinal != 0).ToArray();
        var receipt = LocalComparisonReceiptFrame.CreateResult(
            source.ComparisonId,
            source.RepositoryId,
            source.CreatedAt,
            source.ExpiresAt,
            source.SelectionFrame,
            source.SelectionSha256,
            source.ScopeConditionSha256,
            memberships,
            nonReceipt,
            evidence);
        return source with
        {
            Memberships = Array.AsReadOnly(memberships.ToArray()),
            Results = Array.AsReadOnly(new[] { receipt }.Concat(nonReceipt).ToArray()),
            Evidence = Array.AsReadOnly(evidence.ToArray()),
        };
    }

    private static LocalComparisonSnapshotWrite Snapshot()
    {
        var referenceA = new LocalComparisonSourceReference(
            "workspace_session", SessionA, null, null, null, Revision);
        var referenceB = new LocalComparisonSourceReference(
            "workspace_session", SessionB, null, null, null, Revision);
        var prepared = new LocalComparisonApplicationService(
            store: null,
            new FixedTimeProvider(CreatedAt),
            _ => ComparisonId).Prepare(new(
                RepositoryId,
                new([Session(SessionA, referenceA, 10m)], 0),
                new([Session(SessionB, referenceB, 20m)], 0),
                ScopeConditionDigest()));
        Assert.Equal(LocalComparisonCreateStatus.Accepted, prepared.Status);
        return Assert.IsType<LocalComparisonSnapshotWrite>(prepared.Snapshot);
    }

    private static LocalComparisonSessionFact Session(
        string sessionId,
        LocalComparisonSourceReference reference,
        decimal inputTokens)
    {
        LocalComparisonObservedScalar Observed(decimal value) => new(
            new LocalComparisonScalarObservation(
                value == 0m
                    ? LocalComparisonFactState.ExplicitZero
                    : LocalComparisonFactState.Recorded,
                value),
            reference);
        var scalars = LocalComparisonRegistryV1.RequiredSessionScalarKeys.ToDictionary(
            static key => key,
            _ => Observed(0m),
            StringComparer.Ordinal);
        scalars["input_tokens"] = Observed(inputTokens);
        var families = LocalComparisonRegistryV1.NamedFamilies.Select(definition =>
            new LocalComparisonNamedFamilyFact(
                definition.Key,
                LocalComparisonFactState.ExplicitZero,
                Array.Empty<LocalComparisonNamedItem>(),
                reference)).ToArray();
        var conditions = LocalComparisonRegistryV1.ConditionKeys.ToDictionary(
            static key => key,
            key => new LocalComparisonConditionFact(
                LocalComparisonFactState.Recorded,
                Array.AsReadOnly(new[] { key + "-value" }),
                reference),
            StringComparer.Ordinal);
        return new(
            sessionId,
            RepositoryId,
            Revision,
            IsSelectable: true,
            IsArchived: false,
            scalars,
            Array.AsReadOnly(families),
            conditions,
            new(
                LocalComparisonFactState.Recorded,
                reference,
                LocalComparisonFactState.Recorded,
                CreatedAt,
                reference));
    }

    private static byte[] Bytes(string value) => System.Text.Encoding.UTF8.GetBytes(value);
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static byte[] ScopeConditionDigest() => SHA256.HashData(Bytes("scope-v5-revision"));
    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static readonly DateTimeOffset CreatedAt =
        DateTimeOffset.Parse("2026-08-28T00:00:00.0000000+00:00", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset ExpiresAt = CreatedAt.AddHours(24);
    private const string ComparisonId = "0198f5b8-0c00-7000-8000-000000000010";
    private const string RepositoryId = "0198f5b8-0c00-7000-8000-000000000020";
    private const string OtherRepositoryId = "0198f5b8-0c00-7000-8000-000000000021";
    private const string SessionA = "0198f5b8-0c00-7000-8000-000000000001";
    private const string SessionB = "0198f5b8-0c00-7000-8000-000000000002";
    private const string SessionC = "0198f5b8-0c00-7000-8000-000000000003";
    private const string RunA = "0198f5b8-0c00-7000-8000-000000000004";
    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private DateTimeOffset value = value;
        public override DateTimeOffset GetUtcNow() => value;
        internal void Set(DateTimeOffset next) => value = next;
    }

    private sealed class ComparisonDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"local-comparison-store-{Guid.NewGuid():N}");

        internal ComparisonDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "comparison.sqlite");
        }

        internal string Path { get; }

        internal void Initialize()
        {
            new SqliteSessionStore(Path).CreateSchema();
            using var connection = Open();
            LocalRepositoryCatalogSchemaV1.Ensure(connection);
            LocalArchiveSchemaV1.Ensure(connection);
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
            LocalComparisonSchemaV1.Ensure(connection);
            Execute(connection, $"""
                INSERT INTO local_repositories(repository_id,display_name,revision,created_at,updated_at)
                VALUES('{RepositoryId}','Repository',1,'{Timestamp(CreatedAt)}','{Timestamp(CreatedAt)}'),
                      ('{OtherRepositoryId}','Other',1,'{Timestamp(CreatedAt)}','{Timestamp(CreatedAt)}');
                INSERT INTO sessions(
                  session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at)
                VALUES('{SessionA}','active','unbound','{Timestamp(CreatedAt)}','not_captured','{Timestamp(CreatedAt)}','{Timestamp(CreatedAt)}'),
                      ('{SessionB}','active','unbound','{Timestamp(CreatedAt)}','not_captured','{Timestamp(CreatedAt)}','{Timestamp(CreatedAt)}');
                """);
        }

        internal SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON;";
            command.ExecuteNonQuery();
            return connection;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
