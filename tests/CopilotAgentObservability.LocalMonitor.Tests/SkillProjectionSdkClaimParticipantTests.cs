using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillProjectionSdkClaimParticipantTests
{
    private const string DefaultClaimId = "claim-1";
    private const string DefaultSessionId = "session-1";
    private const string DefaultEventId = "event-1";
    private const string DefaultSourceAdapter = "adapter-a";
    private const string DefaultSourceEventId = "source-event-1";
    private static readonly DateTimeOffset DefaultCreatedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void First_call_on_an_empty_table_inserts_and_returns_inserted()
    {
        using var database = new TestDatabase();
        var claim = DefaultClaim();
        Seed(database, claim);

        var outcome = Call(database, claim);

        Assert.Equal(SkillProjectionSdkClaimWriteOutcome.Inserted, outcome);
        AssertStoredMatches(database, claim, ExpectedCreatedAt(claim.CreatedAt));
        AssertRowCount(database, 1);
    }

    [Fact]
    public void Replaying_the_identical_write_returns_existing_identical_and_writes_nothing()
    {
        using var database = new TestDatabase();
        var claim = DefaultClaim();
        Seed(database, claim);
        Assert.Equal(SkillProjectionSdkClaimWriteOutcome.Inserted, Call(database, claim));

        var outcome = Call(database, claim);

        Assert.Equal(SkillProjectionSdkClaimWriteOutcome.ExistingIdentical, outcome);
        AssertRowCount(database, 1);
    }

    public static IEnumerable<object[]> IdentityCollisionScenarios()
    {
        yield return ["claim_id"];
        yield return ["session_event"];
        yield return ["source_key"];
    }

    [Theory]
    [MemberData(nameof(IdentityCollisionScenarios))]
    public void Each_identity_key_independently_detects_a_collision(string scenario)
    {
        using var database = new TestDatabase();
        var original = DefaultClaim();
        Seed(database, original);
        Assert.Equal(SkillProjectionSdkClaimWriteOutcome.Inserted, Call(database, original));
        var candidate = MutateForIdentityCollision(scenario, original);

        var exception = Assert.Throws<InvalidOperationException>(() => Call(database, candidate));

        Assert.Equal("skill_projection_sdk_claim_conflict", exception.Message);
        AssertRowCount(database, 1);
    }

    private static SkillProjectionSdkClaimWrite MutateForIdentityCollision(
        string scenario,
        SkillProjectionSdkClaimWrite original) => scenario switch
        {
            "claim_id" => original with
            {
                SessionId = "session-2",
                EventId = "event-2",
                SourceAdapter = "adapter-b",
                SourceEventId = "source-event-2",
                SkillName = "different-skill",
            },
            "session_event" => original with
            {
                ClaimId = "claim-2",
                SourceAdapter = "adapter-b",
                SourceEventId = "source-event-2",
                SkillName = "different-skill",
            },
            "source_key" => original with
            {
                ClaimId = "claim-2",
                SessionId = "session-2",
                EventId = "event-2",
                SkillName = "different-skill",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

    public static IEnumerable<object[]> SingleColumnDivergences()
    {
        yield return ["skill_name"];
        yield return ["skill_source_null_vs_nonnull"];
        yield return ["producer_trace_id_null_vs_nonnull"];
        yield return ["payload_sha256"];
        yield return ["created_at"];
    }

    [Theory]
    [MemberData(nameof(SingleColumnDivergences))]
    public void Single_differing_column_makes_an_otherwise_identical_replay_a_conflict(string column)
    {
        using var database = new TestDatabase();
        var original = DefaultClaim();
        Seed(database, original);
        Assert.Equal(SkillProjectionSdkClaimWriteOutcome.Inserted, Call(database, original));
        var candidate = MutateForColumnDivergence(column, original);

        var exception = Assert.Throws<InvalidOperationException>(() => Call(database, candidate));

        Assert.Equal("skill_projection_sdk_claim_conflict", exception.Message);
        AssertRowCount(database, 1);
    }

    private static SkillProjectionSdkClaimWrite MutateForColumnDivergence(
        string column,
        SkillProjectionSdkClaimWrite original) => column switch
        {
            "skill_name" => original with { SkillName = "different-skill" },
            "skill_source_null_vs_nonnull" => original with { SkillSource = null },
            "producer_trace_id_null_vs_nonnull" => original with { ProducerTraceId = null },
            "payload_sha256" => original with { PayloadSha256 = new string('f', 64) },
            "created_at" => original with { CreatedAt = original.CreatedAt.AddSeconds(1) },
            _ => throw new ArgumentOutOfRangeException(nameof(column)),
        };

    [Fact]
    public void Nullable_columns_round_trip_and_replay_is_identical()
    {
        using var database = new TestDatabase();
        var claim = DefaultClaim() with
        {
            ProducerTraceId = null,
            ProducerSpanId = null,
            SkillSource = null,
            InvocationTrigger = null,
        };
        Seed(database, claim);

        var first = Call(database, claim);
        var second = Call(database, claim);

        Assert.Equal(SkillProjectionSdkClaimWriteOutcome.Inserted, first);
        Assert.Equal(SkillProjectionSdkClaimWriteOutcome.ExistingIdentical, second);
        AssertStoredMatches(database, claim, ExpectedCreatedAt(claim.CreatedAt));
        AssertRowCount(database, 1);
    }

    [Fact]
    public void Rolling_back_the_callers_transaction_leaves_the_table_empty()
    {
        using var database = new TestDatabase();
        var claim = DefaultClaim();
        Seed(database, claim);

        using (var connection = database.Open())
        {
            using var transaction = connection.BeginTransaction();
            var outcome = SkillProjectionSdkClaimParticipant.InsertOrVerify(connection, transaction, claim);
            Assert.Equal(SkillProjectionSdkClaimWriteOutcome.Inserted, outcome);
            transaction.Rollback();
        }

        AssertRowCount(database, 0);
    }

    [Fact]
    public void No_clock_is_read_the_stored_value_is_the_supplied_instants_rendering()
    {
        using var database = new TestDatabase();
        var farPast = new DateTimeOffset(1901, 3, 4, 5, 6, 7, TimeSpan.Zero).AddTicks(1234567);
        var claim = DefaultClaim() with { CreatedAt = farPast };
        Seed(database, claim);

        var outcome = Call(database, claim);

        Assert.Equal(SkillProjectionSdkClaimWriteOutcome.Inserted, outcome);
        var expected = ExpectedCreatedAt(farPast);
        Assert.Equal(33, expected.Length);
        AssertStoredMatches(database, claim, expected);
    }

    [Fact]
    public void Append_only_triggers_still_reject_update_and_delete_after_insert()
    {
        using var database = new TestDatabase();
        var claim = DefaultClaim();
        Seed(database, claim);
        Assert.Equal(SkillProjectionSdkClaimWriteOutcome.Inserted, Call(database, claim));

        using var connection = database.Open();
        using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE skill_projection_sdk_claims SET skill_name='changed' WHERE claim_id=$claim_id;";
            update.Parameters.AddWithValue("$claim_id", claim.ClaimId);
            var exception = Assert.Throws<SqliteException>(() => update.ExecuteNonQuery());
            Assert.Contains("skill_projection_append_only", exception.Message, StringComparison.Ordinal);
        }
        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM skill_projection_sdk_claims WHERE claim_id=$claim_id;";
            delete.Parameters.AddWithValue("$claim_id", claim.ClaimId);
            var exception = Assert.Throws<SqliteException>(() => delete.ExecuteNonQuery());
            Assert.Contains("skill_projection_append_only", exception.Message, StringComparison.Ordinal);
        }
    }

    private static SkillProjectionSdkClaimWrite DefaultClaim() => new(
        ClaimId: DefaultClaimId,
        SessionId: DefaultSessionId,
        EventId: DefaultEventId,
        SourceEventId: DefaultSourceEventId,
        SourceAdapter: DefaultSourceAdapter,
        SourceSurface: "copilot-sdk",
        SourceApplicationVersion: "1.0.0",
        AdapterVersion: "adapter-version-1",
        NormalizationVersion: "normalization-1",
        PayloadSchema: "payload-schema-1",
        SchemaFingerprint: new string('a', 64),
        PayloadSha256: new string('b', 64),
        ProducerTraceId: new string('c', 32),
        ProducerSpanId: new string('d', 16),
        SkillName: "demo-skill",
        SkillSource: "marketplace",
        InvocationTrigger: "explicit",
        CreatedAt: DefaultCreatedAt);

    private static string ExpectedCreatedAt(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

    private static void Seed(TestDatabase database, SkillProjectionSdkClaimWrite claim)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        InsertSessionAndEvent(
            connection,
            transaction,
            claim.SessionId,
            claim.EventId,
            claim.SourceAdapter,
            claim.SourceEventId);
        transaction.Commit();
    }

    private static void InsertSessionAndEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string eventId,
        string sourceAdapter,
        string sourceEventId)
    {
        using (var session = connection.CreateCommand())
        {
            session.Transaction = transaction;
            session.CommandText =
                """
                INSERT INTO sessions(
                    session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at)
                VALUES($session_id,'active','unbound',$now,'not_captured',$now,$now)
                ON CONFLICT(session_id) DO NOTHING;
                """;
            session.Parameters.AddWithValue("$session_id", sessionId);
            session.Parameters.AddWithValue("$now", "2026-01-01T00:00:00.0000000+00:00");
            session.ExecuteNonQuery();
        }
        using (var sessionEvent = connection.CreateCommand())
        {
            sessionEvent.Transaction = transaction;
            sessionEvent.CommandText =
                """
                INSERT INTO session_events(
                    event_id,session_id,source_adapter,source_event_id,type,occurred_at,content_state)
                VALUES($event_id,$session_id,$source_adapter,$source_event_id,'skill_invocation',$now,'available');
                """;
            sessionEvent.Parameters.AddWithValue("$event_id", eventId);
            sessionEvent.Parameters.AddWithValue("$session_id", sessionId);
            sessionEvent.Parameters.AddWithValue("$source_adapter", sourceAdapter);
            sessionEvent.Parameters.AddWithValue("$source_event_id", sourceEventId);
            sessionEvent.Parameters.AddWithValue("$now", "2026-01-01T00:00:00.0000000+00:00");
            sessionEvent.ExecuteNonQuery();
        }
    }

    private static SkillProjectionSdkClaimWriteOutcome Call(TestDatabase database, SkillProjectionSdkClaimWrite claim)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var outcome = SkillProjectionSdkClaimParticipant.InsertOrVerify(connection, transaction, claim);
        transaction.Commit();
        return outcome;
    }

    private static void AssertRowCount(TestDatabase database, long expected)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM skill_projection_sdk_claims;";
        Assert.Equal(expected, Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    private static void AssertStoredMatches(
        TestDatabase database,
        SkillProjectionSdkClaimWrite claim,
        string expectedCreatedAt)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT session_id,event_id,source_event_id,source_adapter,source_surface,
                source_application_version,adapter_version,normalization_version,payload_schema,
                schema_fingerprint,payload_sha256,producer_trace_id,producer_span_id,skill_name,
                skill_source,invocation_trigger,created_at
            FROM skill_projection_sdk_claims WHERE claim_id=$claim_id;
            """;
        command.Parameters.AddWithValue("$claim_id", claim.ClaimId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(claim.SessionId, reader.GetString(0));
        Assert.Equal(claim.EventId, reader.GetString(1));
        Assert.Equal(claim.SourceEventId, reader.GetString(2));
        Assert.Equal(claim.SourceAdapter, reader.GetString(3));
        Assert.Equal(claim.SourceSurface, reader.GetString(4));
        Assert.Equal(claim.SourceApplicationVersion, reader.GetString(5));
        Assert.Equal(claim.AdapterVersion, reader.GetString(6));
        Assert.Equal(claim.NormalizationVersion, reader.GetString(7));
        Assert.Equal(claim.PayloadSchema, reader.GetString(8));
        Assert.Equal(claim.SchemaFingerprint, reader.GetString(9));
        Assert.Equal(claim.PayloadSha256, reader.GetString(10));
        Assert.Equal(claim.ProducerTraceId, reader.IsDBNull(11) ? null : reader.GetString(11));
        Assert.Equal(claim.ProducerSpanId, reader.IsDBNull(12) ? null : reader.GetString(12));
        Assert.Equal(claim.SkillName, reader.GetString(13));
        Assert.Equal(claim.SkillSource, reader.IsDBNull(14) ? null : reader.GetString(14));
        Assert.Equal(claim.InvocationTrigger, reader.IsDBNull(15) ? null : reader.GetString(15));
        Assert.Equal(expectedCreatedAt, reader.GetString(16));
        Assert.Equal(33, reader.GetString(16).Length);
        Assert.False(reader.Read());
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"skill-sdk-claim-{Guid.NewGuid():N}");

        internal TestDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "monitor.sqlite");
            InstallComponent();
        }

        internal string Path { get; }

        internal SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
            return connection;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }

        private void InstallComponent()
        {
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                RetentionSchemaMigrator.Apply(connection, transaction);
                transaction.Commit();
            }
            new SqliteSourceCompatibilityStore(Path).CreateSchema();
            new SqliteSessionStore(Path).CreateSchema();
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                SkillProjectionSchemaV1.Ensure(connection, transaction);
                transaction.Commit();
            }
        }
    }
}
