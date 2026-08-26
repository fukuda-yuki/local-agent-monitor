using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvocationV2IngestTransactionV1Tests
{
    private const string AvailablePayload = """{"name":"review","path":".github/skills/review.md","content":"body","source":"project","trigger":"user-invoked"}""";
    private const string FaultPayload = """{"path":".github/skills/review.md","content":"body","source":"project","trigger":"user-invoked"}""";
    private static readonly DateTimeOffset WriteAt = new(2026, 8, 22, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public void Execute_FreshAvailableRequest_CommitsCompleteGraphWithExactFingerprintAndOneClockInstant()
    {
        using var database = new TestDatabase();
        var facts = Derive(AvailablePayload);
        var authority = new RegistryAuthority();
        var clock = new CountingTimeProvider(WriteAt);

        var result = Execute(database, facts, authority, clock);

        Assert.Equal(SkillInvocationV2IngestOutcomeV1.Committed, result.Outcome);
        Assert.True(result.TerminalSealAttempted);
        var committedIdentity = Assert.IsType<SkillInvocationV2CommittedIdentityV1>(result.CommittedIdentity!);
        Assert.Equal(Guid.Parse(Scalar(database, "SELECT session_id FROM skill_invocation_snapshots;")!.ToString()!), committedIdentity.SessionId);
        Assert.Equal(Guid.Parse(Scalar(database, "SELECT snapshot_id FROM skill_invocation_snapshots;")!.ToString()!), committedIdentity.SnapshotId);
        Assert.Equal(1, clock.CallCount);
        Assert.Equal(facts.RequestFingerprintSha256, Scalar(database, "SELECT request_fingerprint_sha256 FROM skill_invocation_snapshot_receipts;"));
        AssertRows(database, 1, "skill_invocation_snapshot_receipts", "skill_invocation_snapshots", "session_events",
            "session_event_content", "skill_projection_sdk_claims");
        var writeAtText = FormatTimestamp(WriteAt);
        var expiresAtText = FormatTimestamp(WriteAt.AddDays(90));
        Assert.Equal(writeAtText, Scalar(database, "SELECT created_at FROM sessions;"));
        Assert.Equal(writeAtText, Scalar(database, "SELECT updated_at FROM sessions;"));
        Assert.Equal(writeAtText, Scalar(database, "SELECT captured_at FROM session_event_content;"));
        Assert.Equal(expiresAtText, Scalar(database, "SELECT expires_at FROM session_event_content;"));
        Assert.Equal(writeAtText, Scalar(database, "SELECT captured_at FROM retention_items;"));
        Assert.Equal(expiresAtText, Scalar(database, "SELECT expires_at FROM retention_items;"));
        Assert.Equal(writeAtText, Scalar(database, "SELECT captured_at FROM skill_invocation_snapshots;"));
        Assert.Equal(writeAtText, Scalar(database, "SELECT created_at FROM skill_invocation_snapshots;"));
        Assert.Equal(writeAtText, Scalar(database, "SELECT created_at FROM skill_invocation_snapshot_receipts;"));
        Assert.Equal(writeAtText, Scalar(database, "SELECT created_at FROM skill_projection_sdk_claims;"));
        Assert.True(authority.AllLeasesDisposed);
    }

    [Fact]
    public void Execute_FreshAvailableRequestPublishesWorkspaceFactInOwningCommit()
    {
        using var database = new TestDatabase();
        var authority = new RegistryAuthority();
        using (var connection = database.Open())
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, WriteAt, authority);
        var participant = new LocalWorkspaceProjectionTransactionParticipant(authority);
        var gate = new LocalWorkspacePublicationGate();

        var result = SkillInvocationV2IngestTransactionV1.Execute(
            database.Path,
            Derive(AvailablePayload),
            authority,
            new CountingTimeProvider(WriteAt),
            () => true,
            () => true,
            CancellationToken.None,
            gate,
            participant);

        Assert.Equal(SkillInvocationV2IngestOutcomeV1.Committed, result.Outcome);
        Assert.Equal("review", Scalar(database,
            "SELECT normalized_text FROM local_workspace_session_search_facts WHERE kind='skill';"));
        Assert.Equal(FormatTimestamp(WriteAt.AddDays(90)), Scalar(database,
            "SELECT expires_at FROM local_workspace_session_search_facts WHERE kind='skill';"));
    }

    [Fact]
    public void Execute_1075Request_PersistsExactFrozenFiveFieldIdentity()
    {
        using var database = new TestDatabase();
        var facts = Derive(AvailablePayload, "1.0.75");

        var result = Execute(database, facts, new RegistryAuthority(), new CountingTimeProvider(WriteAt));

        Assert.Equal(SkillInvocationV2IngestOutcomeV1.Committed, result.Outcome);
        Assert.Equal("1.0.75", Scalar(database, "SELECT source_application_version FROM session_events;"));
        Assert.Equal("copilot-sdk-dotnet-1.0.4+cao-skill-v2.1", Scalar(database, "SELECT adapter_version FROM session_events;"));
        Assert.Equal("github-copilot-sdk.skill-invoked.normalize.v2", Scalar(database, "SELECT normalization_version FROM session_events;"));
        Assert.Equal("github-copilot-sdk.skill-invoked.v1", facts.ProducerTuple.PayloadSchema);
        Assert.Equal("8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c", Scalar(database, "SELECT schema_fingerprint FROM session_events;"));
    }

    [Fact]
    public void Execute_TwoFreshInvocationsExposeOneResolvedSessionAndDistinctSnapshots()
    {
        using var database = new TestDatabase();
        var authority = new RegistryAuthority();
        var first = Execute(database, Derive(AvailablePayload), authority, new CountingTimeProvider(WriteAt));
        var secondRequest = ValidRequest(AvailablePayload);
        var secondJson = Encoding.UTF8.GetString(secondRequest)
            .Replace("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "cccccccc-cccc-4ccc-8ccc-cccccccccccc", StringComparison.Ordinal)
            .Replace("run-1", "run-2", StringComparison.Ordinal);
        var secondFacts = SkillInvocationV2IngestRequestFactsV1.Derive(
            SkillInvocationV2Parser.Parse(Encoding.UTF8.GetBytes(secondJson), new RuntimeCapability()));
        var second = Execute(database, secondFacts, authority, new CountingTimeProvider(WriteAt.AddSeconds(1)));

        var firstIdentity = Assert.IsType<SkillInvocationV2CommittedIdentityV1>(first.CommittedIdentity!);
        var secondIdentity = Assert.IsType<SkillInvocationV2CommittedIdentityV1>(second.CommittedIdentity!);
        Assert.Equal(firstIdentity.SessionId, secondIdentity.SessionId);
        Assert.NotEqual(firstIdentity.SnapshotId, secondIdentity.SnapshotId);
    }

    [Fact]
    public void Execute_IdenticalReplay_SucceedsWithoutRegistryAdmissionOrNewRows()
    {
        using var database = new TestDatabase();
        var facts = Derive(AvailablePayload);
        Execute(database, facts, new RegistryAuthority(), new CountingTimeProvider(WriteAt));
        var before = DumpRows(database);
        var replayAuthority = new RegistryAuthority();

        var result = Execute(database, facts, replayAuthority, new CountingTimeProvider(WriteAt.AddHours(1)));

        Assert.Equal(new SkillInvocationV2IngestResultV1(SkillInvocationV2IngestOutcomeV1.ReplaySucceeded, true), result);
        Assert.Equal(0, replayAuthority.CaptureCount);
        Assert.Equal(0, replayAuthority.AcquireCount);
        Assert.Equal(before, DumpRows(database));
    }

    [Fact]
    public void Execute_ReplayWithDifferentPayload_ReturnsIdempotencyConflict()
    {
        using var database = new TestDatabase();
        Execute(database, Derive(AvailablePayload), new RegistryAuthority(), new CountingTimeProvider(WriteAt));

        var result = Execute(database, Derive(AvailablePayload.Replace("body", "changed", StringComparison.Ordinal)),
            new RegistryAuthority(), new CountingTimeProvider(WriteAt));

        Assert.Equal(new SkillInvocationV2IngestResultV1(SkillInvocationV2IngestOutcomeV1.IdempotencyConflict, false), result);
    }

    [Fact]
    public void Execute_EqualReplaySealLoses_ReturnsUnavailableAfterSealAttempt()
    {
        using var database = new TestDatabase();
        var facts = Derive(AvailablePayload);
        Execute(database, facts, new RegistryAuthority(), new CountingTimeProvider(WriteAt));
        var sealInvocationCount = 0;

        var result = Execute(database, facts, new RegistryAuthority(), new CountingTimeProvider(WriteAt), sealReplay: () =>
        {
            sealInvocationCount++;
            return false;
        });

        Assert.Equal(new SkillInvocationV2IngestResultV1(SkillInvocationV2IngestOutcomeV1.Unavailable, true), result);
        Assert.Equal(1, sealInvocationCount);
    }

    [Fact]
    public void Execute_NullRegistryCapture_ReturnsUnavailableWithoutWrites()
    {
        using var database = new TestDatabase();

        var result = Execute(database, Derive(AvailablePayload), new RegistryAuthority { ReturnNullCapture = true },
            new CountingTimeProvider(WriteAt));

        AssertUnavailableWithoutWrites(database, result);
    }

    [Fact]
    public void Execute_Order11LeaseFailure_DoesNotRecaptureAndWritesNothing()
    {
        using var database = new TestDatabase();
        var authority = new RegistryAuthority { AlwaysFailAcquisition = true };

        var result = Execute(database, Derive(AvailablePayload), authority, new CountingTimeProvider(WriteAt));

        AssertUnavailableWithoutWrites(database, result);
        Assert.Equal(1, authority.CaptureCount);
        Assert.Equal(1, authority.AcquireCount);
    }

    [Fact]
    public void Execute_RejectedProducerTuple_ReturnsUnavailableWithoutWritesAndDisposesEveryLease()
    {
        using var database = new TestDatabase();
        var authority = new RegistryAuthority { AcceptTuple = false };

        var result = Execute(database, Derive(AvailablePayload), authority, new CountingTimeProvider(WriteAt));

        AssertUnavailableWithoutWrites(database, result);
        Assert.True(authority.LeaseCount > 0);
        Assert.True(authority.AllLeasesDisposed);
    }

    [Fact]
    public void Execute_DifferentReceiptWithStorageContentionPresentAtStart_ReturnsIdempotencyConflict()
    {
        using var database = new TestDatabase();
        Execute(database, Derive(AvailablePayload), new RegistryAuthority(), new CountingTimeProvider(WriteAt));

        var result = ExecuteWithStorageContentionAtStart(
            database,
            Derive(AvailablePayload.Replace("body", "changed", StringComparison.Ordinal)),
            new RegistryAuthority(),
            new CountingTimeProvider(WriteAt));

        Assert.Equal(new SkillInvocationV2IngestResultV1(SkillInvocationV2IngestOutcomeV1.IdempotencyConflict, false), result);
    }

    [Fact]
    public void Execute_MissingReceiptAndRejectedTupleWithStorageContentionPresentAtStart_ReturnsUnavailable()
    {
        using var database = new TestDatabase();

        var result = ExecuteWithStorageContentionAtStart(
            database,
            Derive(AvailablePayload),
            new RegistryAuthority { AcceptTuple = false },
            new CountingTimeProvider(WriteAt));

        AssertUnavailableWithoutWrites(database, result);
    }

    [Fact]
    public void Execute_MissingReceiptAndAcceptedTupleWithStorageContentionPresentAtStart_ReturnsPersistenceBusy()
    {
        using var database = new TestDatabase();

        var result = ExecuteWithStorageContentionAtStart(
            database,
            Derive(AvailablePayload),
            new RegistryAuthority(),
            new CountingTimeProvider(WriteAt));

        Assert.Equal(new SkillInvocationV2IngestResultV1(SkillInvocationV2IngestOutcomeV1.PersistenceBusy, false), result);
        AssertNoWrites(database);
    }

    [Fact]
    public void Execute_RejectedProducerTupleDuringStorageContention_PrefersUnavailableOverPersistenceBusy()
    {
        using var database = new TestDatabase();

        var result = ExecuteDuringStorageContention(
            database,
            Derive(AvailablePayload),
            new RegistryAuthority { AcceptTuple = false },
            new CountingTimeProvider(WriteAt));

        AssertUnavailableWithoutWrites(database, result);
    }

    [Fact]
    public void Execute_NullRegistryCaptureDuringStorageContention_PrefersUnavailableOverPersistenceBusy()
    {
        using var database = new TestDatabase();

        var result = ExecuteDuringStorageContention(
            database,
            Derive(AvailablePayload),
            new RegistryAuthority { ReturnNullCapture = true },
            new CountingTimeProvider(WriteAt));

        AssertUnavailableWithoutWrites(database, result);
    }

    [Fact]
    public void Execute_AcceptedRegistryDuringStorageContention_ReturnsPersistenceBusyWithoutWrites()
    {
        using var database = new TestDatabase();

        var result = ExecuteDuringStorageContention(
            database,
            Derive(AvailablePayload),
            new RegistryAuthority(),
            new CountingTimeProvider(WriteAt));

        Assert.Equal(new SkillInvocationV2IngestResultV1(SkillInvocationV2IngestOutcomeV1.PersistenceBusy, false), result);
        AssertNoWrites(database);
    }

    [Fact]
    public void Execute_IdenticalReceiptCommittedAfterPublicProbe_ReplaysWithoutChangingCompetingRows()
    {
        using var database = new TestDatabase();
        var facts = Derive(AvailablePayload);
        var captureCount = 0;
        var competingRows = string.Empty;
        var authority = new RegistryAuthority
        {
            OnCapture = () =>
            {
                captureCount++;
                var competingResult = Execute(database, facts, new RegistryAuthority(), new CountingTimeProvider(WriteAt));
                Assert.Equal(SkillInvocationV2IngestOutcomeV1.Committed, competingResult.Outcome);
                competingRows = DumpRows(database);
            },
        };

        var result = Execute(database, facts, authority, new CountingTimeProvider(WriteAt.AddHours(1)));

        Assert.Equal(new SkillInvocationV2IngestResultV1(SkillInvocationV2IngestOutcomeV1.ReplaySucceeded, true), result);
        Assert.Equal(1, captureCount);
        Assert.Equal(competingRows, DumpRows(database));
    }

    [Fact]
    public void Execute_DifferentReceiptCommittedAfterPublicProbe_ConflictsWithoutChangingCompetingRows()
    {
        using var database = new TestDatabase();
        var outerFacts = Derive(AvailablePayload);
        var competingFacts = Derive(AvailablePayload.Replace("body", "changed", StringComparison.Ordinal));
        var captureCount = 0;
        var competingRows = string.Empty;
        var authority = new RegistryAuthority
        {
            OnCapture = () =>
            {
                captureCount++;
                var competingResult = Execute(database, competingFacts, new RegistryAuthority(), new CountingTimeProvider(WriteAt));
                Assert.Equal(SkillInvocationV2IngestOutcomeV1.Committed, competingResult.Outcome);
                competingRows = DumpRows(database);
            },
        };

        var result = Execute(database, outerFacts, authority, new CountingTimeProvider(WriteAt.AddHours(1)));

        Assert.Equal(new SkillInvocationV2IngestResultV1(SkillInvocationV2IngestOutcomeV1.IdempotencyConflict, false), result);
        Assert.Equal(1, captureCount);
        Assert.Equal(competingRows, DumpRows(database));
    }

    [Fact]
    public void Execute_PreExistingEventWithoutReceipt_ReturnsIdempotencyConflictWithoutSnapshotReceiptOrClaim()
    {
        using var database = new TestDatabase();
        var facts = Derive(AvailablePayload);
        InsertNonSkillEvent(database, facts);

        var result = Execute(database, facts, new RegistryAuthority(), new CountingTimeProvider(WriteAt));

        Assert.Equal(new SkillInvocationV2IngestResultV1(SkillInvocationV2IngestOutcomeV1.IdempotencyConflict, false), result);
        AssertRows(database, 0, "skill_invocation_snapshots", "skill_invocation_snapshot_receipts",
            "skill_projection_sdk_claims");
    }

    [Fact]
    public void Execute_Order14FirstLeaseFailure_RecapturesOnceThenCommits()
    {
        using var database = new TestDatabase();
        var authority = new RegistryAuthority(true, false, true);

        var result = Execute(database, Derive(AvailablePayload), authority, new CountingTimeProvider(WriteAt));

        Assert.Equal(SkillInvocationV2IngestOutcomeV1.Committed, result.Outcome);
        Assert.Equal(2, authority.CaptureCount);
        Assert.True(authority.AllLeasesDisposed);
    }

    [Fact]
    public void Execute_Order14BothLeaseAttemptsFail_ReturnsUnavailableWithoutWrites()
    {
        using var database = new TestDatabase();
        var authority = new RegistryAuthority(true, false, false);

        var result = Execute(database, Derive(AvailablePayload), authority, new CountingTimeProvider(WriteAt));

        AssertUnavailableWithoutWrites(database, result);
        Assert.Equal(2, authority.CaptureCount);
        Assert.True(authority.AllLeasesDisposed);
    }

    [Fact]
    public void Execute_Order14GenerationVerificationFailure_DoesNotRecaptureAndWritesNothing()
    {
        using var database = new TestDatabase();
        var authority = new RegistryAuthority { VerifyIdentity = false };

        var result = Execute(database, Derive(AvailablePayload), authority, new CountingTimeProvider(WriteAt));

        AssertUnavailableWithoutWrites(database, result);
        Assert.Equal(1, authority.CaptureCount);
        Assert.True(authority.AllLeasesDisposed);
    }

    [Fact]
    public void Execute_CommitSealLoses_RollsBackEveryWrite()
    {
        using var database = new TestDatabase();

        var result = Execute(database, Derive(AvailablePayload), new RegistryAuthority(),
            new CountingTimeProvider(WriteAt), sealCommit: () => false);

        Assert.Equal(new SkillInvocationV2IngestResultV1(SkillInvocationV2IngestOutcomeV1.Unavailable, true), result);
        AssertNoWrites(database);
    }

    [Fact]
    public void Execute_PreUnixEpochClock_ReturnsUnavailableWithoutWrites()
    {
        using var database = new TestDatabase();

        var result = Execute(
            database,
            Derive(AvailablePayload),
            new RegistryAuthority(),
            new CountingTimeProvider(DateTimeOffset.UnixEpoch.AddTicks(-1)));

        AssertUnavailableWithoutWrites(database, result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Execute_SealDelegateThrows_ReturnsUnavailableAfterAttemptWithoutWrites(bool replay)
    {
        using var database = new TestDatabase();
        var facts = Derive(AvailablePayload);
        if (replay)
            Execute(database, facts, new RegistryAuthority(), new CountingTimeProvider(WriteAt));
        var before = DumpRows(database);

        var result = Execute(
            database,
            facts,
            new RegistryAuthority(),
            new CountingTimeProvider(WriteAt),
            sealReplay: replay ? ThrowBusy : null,
            sealCommit: replay ? null : ThrowBusy);

        Assert.Equal(new SkillInvocationV2IngestResultV1(SkillInvocationV2IngestOutcomeV1.Unavailable, true), result);
        Assert.Equal(before, DumpRows(database));
    }

    [Theory]
    [InlineData(RegistryThrowPoint.Capture)]
    [InlineData(RegistryThrowPoint.Acquire)]
    [InlineData(RegistryThrowPoint.AcceptTuple)]
    [InlineData(RegistryThrowPoint.VerifyIdentity)]
    public void Execute_RegistryAuthorityMethodThrows_ReturnsUnavailableWithoutWrites(RegistryThrowPoint throwPoint)
    {
        using var database = new TestDatabase();
        var authority = new RegistryAuthority { ThrowPoint = throwPoint };

        var result = Execute(database, Derive(AvailablePayload), authority, new CountingTimeProvider(WriteAt));

        AssertUnavailableWithoutWrites(database, result);
    }

    [Fact]
    public void Execute_ClassifiedFault_CommitsAvailableEventAndSnapshotWithoutClaim()
    {
        using var database = new TestDatabase();
        var facts = Derive(FaultPayload);

        var result = Execute(database, facts, new RegistryAuthority(), new CountingTimeProvider(WriteAt));

        Assert.Equal(SkillInvocationV2IngestOutcomeV1.Committed, result.Outcome);
        Assert.Equal("available", Scalar(database, "SELECT content_state FROM session_events;"));
        Assert.Equal(facts.StateToken, Scalar(database, "SELECT state FROM skill_invocation_snapshots;"));
        Assert.Equal(facts.ReasonToken, Scalar(database, "SELECT reason FROM skill_invocation_snapshots;"));
        Assert.Null(Scalar(database, "SELECT claim_id FROM skill_invocation_snapshots;"));
        Assert.Equal(0L, Count(database, "skill_projection_sdk_claims"));
    }

    [Fact]
    public void Execute_CancelledBeforeProbe_ReturnsUnavailableWithoutWritesOrAdmission()
    {
        using var database = new TestDatabase();
        var authority = new RegistryAuthority();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = SkillInvocationV2IngestTransactionV1.Execute(database.Path, Derive(AvailablePayload), authority,
            new CountingTimeProvider(WriteAt), () => true, () => true, cancellation.Token);

        AssertUnavailableWithoutWrites(database, result);
        Assert.Equal(0, authority.CaptureCount);
    }

    private static SkillInvocationV2IngestResultV1 Execute(
        TestDatabase database,
        SkillInvocationV2IngestRequestFactsV1 facts,
        RegistryAuthority authority,
        TimeProvider clock,
        Func<bool>? sealReplay = null,
        Func<bool>? sealCommit = null) =>
        SkillInvocationV2IngestTransactionV1.Execute(
            database.Path, facts, authority, clock, sealReplay ?? (() => true), sealCommit ?? (() => true), CancellationToken.None);

    private static SkillInvocationV2IngestResultV1 ExecuteDuringStorageContention(
        TestDatabase database,
        SkillInvocationV2IngestRequestFactsV1 facts,
        RegistryAuthority authority,
        TimeProvider clock)
    {
        SqliteConnection? competingConnection = null;
        SqliteTransaction? competingTransaction = null;
        authority.OnCapture = () =>
        {
            competingConnection = database.Open();
            competingTransaction = competingConnection.BeginTransaction(deferred: false);
        };

        try
        {
            return Execute(database, facts, authority, clock);
        }
        finally
        {
            competingTransaction?.Dispose();
            competingConnection?.Dispose();
        }
    }

    private static SkillInvocationV2IngestResultV1 ExecuteWithStorageContentionAtStart(
        TestDatabase database,
        SkillInvocationV2IngestRequestFactsV1 facts,
        RegistryAuthority authority,
        TimeProvider clock)
    {
        using var competingConnection = database.Open();
        using var competingTransaction = competingConnection.BeginTransaction(deferred: false);
        return Execute(database, facts, authority, clock);
    }

    private static bool ThrowBusy() => throw new SqliteException("delegate failure", 5);

    private static SkillInvocationV2IngestRequestFactsV1 Derive(string payload) =>
        SkillInvocationV2IngestRequestFactsV1.Derive(
            SkillInvocationV2Parser.Parse(ValidRequest(payload), new RuntimeCapability()));

    private static SkillInvocationV2IngestRequestFactsV1 Derive(string payload, string version) =>
        SkillInvocationV2IngestRequestFactsV1.Derive(SkillInvocationV2Parser.Parse(
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ValidRequest(payload)).Replace("1.0.65", version, StringComparison.Ordinal)),
            new RuntimeCapability(SkillInvocationV2TestIdentity.Create(version))));

    private static byte[] ValidRequest(string payload)
    {
        var request = "{\"schema_version\":2,\"source_adapter\":\"copilot-sdk-stream\",\"source_surface\":\"copilot-sdk\",\"native_session_id\":\"native-session\",\"source_application_version\":\"1.0.65\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v2\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c\",\"events\":[{\"source_event_id\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\",\"source_parent_event_id\":\"bbbbbbbb-bbbb-4bbb-9bbb-bbbbbbbbbbbb\",\"type\":\"skill.invoked\",\"occurred_at\":\"2026-08-09T00:00:00.0000000+00:00\",\"run_native_id\":\"run-1\",\"source_ephemeral\":true,\"trace_id\":null,\"span_id\":null,\"payload\":" + payload + "}]}";
        return Encoding.UTF8.GetBytes(request);
    }

    private static void AssertUnavailableWithoutWrites(TestDatabase database, SkillInvocationV2IngestResultV1 result)
    {
        Assert.Equal(new SkillInvocationV2IngestResultV1(SkillInvocationV2IngestOutcomeV1.Unavailable, false), result);
        AssertNoWrites(database);
    }

    private static void AssertNoWrites(TestDatabase database) => Assert.Equal(string.Empty, DumpRows(database));

    private static void AssertRows(TestDatabase database, long expected, params string[] tables)
    {
        foreach (var table in tables)
            Assert.Equal(expected, Count(database, table));
    }

    private static long Count(TestDatabase database, string table) =>
        Convert.ToInt64(Scalar(database, $"SELECT COUNT(*) FROM {table};"), CultureInfo.InvariantCulture);

    private static object? Scalar(TestDatabase database, string sql)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private static string DumpRows(TestDatabase database)
    {
        var tables = new[] { "sessions", "session_native_ids", "session_runs", "session_events", "session_event_content",
            "retention_items", "retention_tombstones", "skill_projection_sdk_claims", "skill_invocation_snapshots",
            "skill_invocation_snapshot_receipts" };
        var builder = new StringBuilder();
        using var connection = database.Open();
        foreach (var table in tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY rowid;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                builder.Append(table);
                for (var index = 0; index < reader.FieldCount; index++)
                    builder.Append('|').Append(reader.IsDBNull(index) ? "<null>" : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture));
                builder.AppendLine();
            }
        }
        return builder.ToString();
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'", CultureInfo.InvariantCulture);

    private static void InsertNonSkillEvent(TestDatabase database, SkillInvocationV2IngestRequestFactsV1 facts)
    {
        var sessionId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        var at = FormatTimestamp(WriteAt);

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        ExecuteSql(connection, transaction,
            """
            INSERT INTO sessions(
                session_id,status,completeness,repository,workspace,started_at,ended_at,
                last_seen_at,raw_retention_state,created_at,updated_at)
            VALUES($session_id,'active','partial',NULL,NULL,NULL,NULL,$at,'expiring',$at,$at);
            """,
            ("$session_id", sessionId.ToString("D")), ("$at", at));

        ExecuteSql(connection, transaction,
            """
            INSERT INTO session_native_ids(session_id,source_surface,native_session_id,binding_kind,observed_at)
            VALUES($session_id,'copilot-sdk',$native,'native',$at);
            """,
            ("$session_id", sessionId.ToString("D")), ("$native", facts.NativeSessionId), ("$at", at));

        ExecuteSql(connection, transaction,
            """
            INSERT INTO session_events(
                event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,
                source_adapter,source_event_id,type,occurred_at,content_state,
                source_application_version,adapter_version,schema_fingerprint,normalization_version,
                match_kind,terminal_outcome,terminal_policy_version)
            VALUES(
                $event_id,$session_id,NULL,'copilot-sdk',NULL,NULL,NULL,
                $source_adapter,$source_event_id,'user_prompt',$at,'not_captured',
                '1.0.65','adapter-version-1',$fingerprint,'normalization-1',
                NULL,NULL,NULL);
            """,
            ("$event_id", eventId.ToString("D")),
            ("$session_id", sessionId.ToString("D")),
            ("$source_adapter", SkillInvocationV2Parser.SourceAdapter),
            ("$source_event_id", facts.Identity.SourceEventId),
            ("$at", at),
            ("$fingerprint", new string('a', 64)));

        transaction.Commit();
    }

    private static void ExecuteSql(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private sealed class RuntimeCapability(
        CopilotAgentObservability.LocalMonitor.SkillRuntime.CertifiedSkillProducerIdentityV1? identity = null)
        : ISkillInvocationV2RuntimeCapability
    {
        public CopilotAgentObservability.LocalMonitor.SkillRuntime.CertifiedSkillProducerIdentityV1 CertifiedIdentity { get; } =
            identity ?? SkillInvocationV2TestIdentity.V1065;
    }

    private sealed class CountingTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        internal int CallCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            CallCount++;
            return instant;
        }
    }

    private sealed class RegistryAuthority(params bool[] acquisitionResults) : ISkillRegistryGenerationAuthority
    {
        private readonly Queue<bool> acquisitionResults = new(acquisitionResults);
        private readonly List<Lease> leases = [];

        internal bool ReturnNullCapture { get; init; }
        internal bool AlwaysFailAcquisition { get; init; }
        internal bool VerifyIdentity { get; init; } = true;
        internal bool AcceptTuple { get; init; } = true;
        internal Action? OnCapture { get; set; }
        internal RegistryThrowPoint? ThrowPoint { get; init; }
        internal int CaptureCount { get; private set; }
        internal int AcquireCount { get; private set; }
        internal int LeaseCount => leases.Count;
        internal bool AllLeasesDisposed => leases.All(lease => lease.IsDisposed);

        public ISkillRegistryGenerationCapture? CaptureGeneration()
        {
            ThrowIfConfigured(RegistryThrowPoint.Capture);
            CaptureCount++;
            OnCapture?.Invoke();
            return ReturnNullCapture ? null : new Capture(CaptureCount);
        }

        public bool TryAcquireGenerationReadLease(
            ISkillRegistryGenerationCapture capture,
            [NotNullWhen(true)] out ISkillRegistryGenerationLease? lease)
        {
            ThrowIfConfigured(RegistryThrowPoint.Acquire);
            AcquireCount++;
            var succeeds = !AlwaysFailAcquisition && (acquisitionResults.Count == 0 || acquisitionResults.Dequeue());
            if (!succeeds)
            {
                lease = null;
                return false;
            }

            var created = new Lease();
            leases.Add(created);
            lease = created;
            return true;
        }

        public bool VerifyGenerationIdentity(ISkillRegistryGenerationCapture capture, ISkillRegistryGenerationLease lease)
        {
            ThrowIfConfigured(RegistryThrowPoint.VerifyIdentity);
            return VerifyIdentity;
        }

        public bool IsProducerTupleAccepted(ISkillRegistryGenerationLease lease, SkillRegistryProducerTuple tuple)
        {
            ThrowIfConfigured(RegistryThrowPoint.AcceptTuple);
            return AcceptTuple;
        }

        private void ThrowIfConfigured(RegistryThrowPoint point)
        {
            if (ThrowPoint == point)
                throw new SqliteException("registry failure", 5);
        }

        private sealed record Capture(int Identity) : ISkillRegistryGenerationCapture;

        private sealed class Lease : ISkillRegistryGenerationLease
        {
            internal bool IsDisposed { get; private set; }
            public void Dispose() => IsDisposed = true;
        }
    }

    public enum RegistryThrowPoint
    {
        Capture,
        Acquire,
        AcceptTuple,
        VerifyIdentity,
    }

    private sealed class TestDatabase : IDisposable
    {
        internal TestDatabase()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"skill-invocation-v2-transaction-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Path = System.IO.Path.Combine(Root, "monitor.db");
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
            new SqliteSourceCompatibilityStore(Path).CreateSchema();
            new SqliteSessionStore(Path).CreateSchema();
            using var componentConnection = Open();
            using var componentTransaction = componentConnection.BeginTransaction();
            SkillProjectionSchemaV1.Ensure(componentConnection, componentTransaction);
            LocalRepositoryCatalogSchemaV1.Ensure(componentConnection, componentTransaction);
            LocalArchiveSchemaV1.Ensure(componentConnection, componentTransaction);
            SkillInvocationSnapshotSchemaV1.Ensure(componentConnection, componentTransaction);
            componentTransaction.Commit();
        }

        internal string Root { get; }
        internal string Path { get; }

        internal SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
            return connection;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }
}
