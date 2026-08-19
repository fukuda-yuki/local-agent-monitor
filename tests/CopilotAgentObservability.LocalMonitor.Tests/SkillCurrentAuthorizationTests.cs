using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillCurrentAuthorizationTests
{
    private static readonly DateTimeOffset DefaultWriteAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ValidationAt = DefaultWriteAt.AddHours(1);
    private static readonly byte[] PayloadToken = "{\"skill\":\"demo\"}"u8.ToArray();
    private const string PayloadSchema = "github-copilot-sdk.skill-invoked.v1";
    private static readonly string SchemaFingerprint = new('a', 64);

    private static readonly SkillRegistryProducerTuple AcceptedTuple = new(
        "1.0.65",
        "adapter-version-1",
        "normalization-1",
        PayloadSchema,
        SchemaFingerprint);

    [Fact]
    public void StableGeneration_AcquiresCapabilityWithSanitizedSkillFacts()
    {
        using var database = new TestDatabase();
        var write = NewWrite("native-stable");
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));
        var authority = new ScriptedRegistryGenerationAuthority(leaseGrants: [true]);
        var service = new SkillProjectionReadService(database.Path, authority);

        var result = service.TryAcquireCurrentSdkClaimAuthorization(write.NewSessionId, write.SnapshotId, Time());

        Assert.Equal(SkillRegistryCurrentAuthorizationOutcome.Acquired, result.Outcome);
        var authorization = result.Authorization;
        Assert.NotNull(authorization);
        Assert.Equal(write.Name, authorization!.SkillName);
        Assert.Equal(write.Source, authorization.SkillSource);
        Assert.Equal(1, authority.LeaseAttemptCount);
        authorization.Dispose();
        Assert.Equal(0, authority.OutstandingLeaseCount);
    }

    [Fact]
    public void OnePreLeaseChurn_RecapturesOnce_ThenAcquires()
    {
        using var database = new TestDatabase();
        var write = NewWrite("native-recapture");
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));
        var authority = new ScriptedRegistryGenerationAuthority(leaseGrants: [false, true]);
        var service = new SkillProjectionReadService(database.Path, authority);

        var result = service.TryAcquireCurrentSdkClaimAuthorization(write.NewSessionId, write.SnapshotId, Time());

        Assert.Equal(SkillRegistryCurrentAuthorizationOutcome.Acquired, result.Outcome);
        Assert.NotNull(result.Authorization);
        Assert.Equal(2, authority.CaptureCallCount);
        Assert.Equal(2, authority.LeaseAttemptCount);
        result.Authorization!.Dispose();
        Assert.Equal(0, authority.OutstandingLeaseCount);
    }

    [Fact]
    public void SecondPreLeaseChurn_ReturnsSanitizedUnavailable_WithNoLeaseHeld()
    {
        using var database = new TestDatabase();
        var write = NewWrite("native-double-churn");
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));
        var authority = new ScriptedRegistryGenerationAuthority(leaseGrants: [false, false]);
        var service = new SkillProjectionReadService(database.Path, authority);

        var result = service.TryAcquireCurrentSdkClaimAuthorization(write.NewSessionId, write.SnapshotId, Time());

        Assert.Equal(SkillRegistryCurrentAuthorizationOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Authorization);
        Assert.Equal(2, authority.LeaseAttemptCount);
        Assert.Equal(0, authority.OutstandingLeaseCount);
    }

    [Fact]
    public void RevokedOrAbsentTuple_ReturnsNotCurrent_WithNoLeaseHeld()
    {
        using var database = new TestDatabase();
        var write = NewWrite("native-revoked");
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));
        var authority = new ScriptedRegistryGenerationAuthority(leaseGrants: [true])
        {
            TupleAccepted = _ => false
        };
        var service = new SkillProjectionReadService(database.Path, authority);

        var result = service.TryAcquireCurrentSdkClaimAuthorization(write.NewSessionId, write.SnapshotId, Time());

        Assert.Equal(SkillRegistryCurrentAuthorizationOutcome.NotCurrent, result.Outcome);
        Assert.Null(result.Authorization);
        Assert.Equal(0, authority.OutstandingLeaseCount);
    }

    [Fact]
    public void CaptureGenerationUnavailable_ReturnsUnavailable()
    {
        using var database = new TestDatabase();
        var write = NewWrite("native-capture-null");
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));
        var authority = new ScriptedRegistryGenerationAuthority(leaseGrants: [], captureAvailable: [false]);
        var service = new SkillProjectionReadService(database.Path, authority);

        var result = service.TryAcquireCurrentSdkClaimAuthorization(write.NewSessionId, write.SnapshotId, Time());

        Assert.Equal(SkillRegistryCurrentAuthorizationOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Authorization);
        Assert.Equal(0, authority.LeaseAttemptCount);
    }

    [Fact]
    public void GenerationIdentityVerificationFailure_ReturnsUnavailable()
    {
        using var database = new TestDatabase();
        var write = NewWrite("native-verify-fail");
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));
        var authority = new ScriptedRegistryGenerationAuthority(leaseGrants: [true])
        {
            VerifyIdentityResult = false
        };
        var service = new SkillProjectionReadService(database.Path, authority);

        var result = service.TryAcquireCurrentSdkClaimAuthorization(write.NewSessionId, write.SnapshotId, Time());

        Assert.Equal(SkillRegistryCurrentAuthorizationOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Authorization);
        Assert.Equal(0, authority.OutstandingLeaseCount);
    }

    [Fact]
    public void NoRegistryAuthority_ReturnsUnavailable()
    {
        using var database = new TestDatabase();
        var write = NewWrite("native-no-authority");
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));
        var service = new SkillProjectionReadService(database.Path);

        var result = service.TryAcquireCurrentSdkClaimAuthorization(write.NewSessionId, write.SnapshotId, Time());

        Assert.Equal(SkillRegistryCurrentAuthorizationOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Authorization);
    }

    [Fact]
    public void UnknownSnapshot_ReturnsUnavailable()
    {
        using var database = new TestDatabase();
        var write = NewWrite("native-unknown");
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));
        var authority = new ScriptedRegistryGenerationAuthority(leaseGrants: [true]);
        var service = new SkillProjectionReadService(database.Path, authority);

        var result = service.TryAcquireCurrentSdkClaimAuthorization(write.NewSessionId, Guid.CreateVersion7(), Time());

        Assert.Equal(SkillRegistryCurrentAuthorizationOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Authorization);
        Assert.Equal(0, authority.OutstandingLeaseCount);
        Assert.Equal(0, authority.LeaseAttemptCount);
    }

    [Fact]
    public void FaultedSnapshot_ReturnsUnavailable()
    {
        using var database = new TestDatabase();
        var write = NewWrite("native-faulted", state: "malformed", reason: "duplicate_property");
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));
        var authority = new ScriptedRegistryGenerationAuthority(leaseGrants: [true]);
        var service = new SkillProjectionReadService(database.Path, authority);

        var result = service.TryAcquireCurrentSdkClaimAuthorization(write.NewSessionId, write.SnapshotId, Time());

        Assert.Equal(SkillRegistryCurrentAuthorizationOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Authorization);
        Assert.Equal(0, authority.LeaseAttemptCount);
    }

    [Fact]
    public void AcquiredAuthorization_HoldsGenerationLeaseUntilDisposed()
    {
        using var database = new TestDatabase();
        var write = NewWrite("native-lease-hold");
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));
        var authority = new ScriptedRegistryGenerationAuthority(leaseGrants: [true]);
        var service = new SkillProjectionReadService(database.Path, authority);

        var result = service.TryAcquireCurrentSdkClaimAuthorization(write.NewSessionId, write.SnapshotId, Time());
        Assert.Equal(SkillRegistryCurrentAuthorizationOutcome.Acquired, result.Outcome);
        Assert.Equal(1, authority.OutstandingLeaseCount);

        result.Authorization!.Dispose();
        Assert.Equal(0, authority.OutstandingLeaseCount);

        // Dispose is idempotent and does not double-release.
        result.Authorization.Dispose();
        Assert.Equal(0, authority.OutstandingLeaseCount);
    }

    [Fact]
    public async Task RealProvider_PublicationBlocksUntilLeaseReleased_ThenOldCaptureChurns()
    {
        var provider = new SkillInvocationV2RegistryProviderV1();
        var capture = provider.CaptureGeneration();
        Assert.NotNull(capture);
        Assert.True(provider.TryAcquireGenerationReadLease(capture!, out var lease));
        Assert.NotNull(lease);
        Assert.Equal(1, provider.OutstandingLeaseCount);

        var publishTask = Task.Run(() => provider.PublishGeneration(SkillInvocationV2ArtifactRegistry.Load()));
        var firstToFinish = await Task.WhenAny(publishTask, Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.NotSame(publishTask, firstToFinish);
        Assert.False(publishTask.IsCompleted);
        Assert.Equal(1, provider.OutstandingLeaseCount);

        lease!.Dispose();
        await publishTask;
        Assert.Equal(0, provider.OutstandingLeaseCount);

        // After publication the old capture no longer matches the current pointer.
        Assert.False(provider.TryAcquireGenerationReadLease(capture!, out var staleLease));
        Assert.Null(staleLease);

        var freshCapture = provider.CaptureGeneration();
        Assert.NotNull(freshCapture);
        Assert.True(provider.TryAcquireGenerationReadLease(freshCapture!, out var freshLease));
        Assert.True(provider.VerifyGenerationIdentity(freshCapture!, freshLease!));
        freshLease!.Dispose();
    }

    private static FixedTimeProvider Time() => new(ValidationAt);

    private static SessionSkillInvocationWrite NewWrite(
        string nativeSessionId,
        string state = "available",
        string reason = "none")
    {
        var isAvailable = state == "available";
        return new SessionSkillInvocationWrite(
            SourceAdapter: "copilot-sdk-stream",
            SourceSurface: "copilot-sdk",
            SourceEventId: Guid.NewGuid().ToString("D"),
            SourceParentEventId: null,
            NativeSessionId: nativeSessionId,
            RunNativeId: null,
            SourceEphemeral: false,
            OccurredAt: DefaultWriteAt,
            SourceApplicationVersion: "1.0.65",
            AdapterVersion: "adapter-version-1",
            NormalizationVersion: "normalization-1",
            PayloadSchema: PayloadSchema,
            SchemaFingerprint: SchemaFingerprint,
            PayloadTokenUtf8: PayloadToken,
            State: state,
            Reason: reason,
            Name: isAvailable ? "demo-skill" : null,
            Source: isAvailable ? "project" : null,
            Trigger: isAvailable ? "user-invoked" : null,
            BodySha256: isAvailable ? new string('b', 64) : null,
            BodyUtf8Bytes: isAvailable ? 7L : null,
            DefinitionPathSha256: isAvailable ? new string('c', 64) : null,
            DefinitionPathUtf8Bytes: isAvailable ? 12L : null,
            EventId: Guid.CreateVersion7(),
            SnapshotId: Guid.CreateVersion7(),
            ClaimId: isAvailable ? Guid.CreateVersion7() : null,
            NewSessionId: Guid.CreateVersion7(),
            NewRunId: Guid.CreateVersion7(),
            WriteAt: DefaultWriteAt,
            ExpiresAt: DefaultWriteAt.AddDays(90));
    }

    private static SessionSkillInvocationWriteOutcome Commit(TestDatabase database, SessionSkillInvocationWrite write)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var outcome = SessionSkillInvocationParticipant.InsertOrVerify(connection, transaction, write);
        transaction.Commit();
        return outcome;
    }

    private sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private sealed class ScriptedRegistryGenerationAuthority : ISkillRegistryGenerationAuthority
    {
        private readonly object syncRoot = new();
        private readonly Queue<bool> leaseGrants;
        private readonly Queue<bool> captureAvailable;
        private readonly HashSet<ScriptedLease> outstanding = new();

        internal ScriptedRegistryGenerationAuthority(
            IEnumerable<bool> leaseGrants,
            IEnumerable<bool>? captureAvailable = null)
        {
            this.leaseGrants = new Queue<bool>(leaseGrants);
            this.captureAvailable = new Queue<bool>(captureAvailable ?? Enumerable.Repeat(true, 64));
        }

        internal Func<SkillRegistryProducerTuple, bool> TupleAccepted { get; set; } = _ => true;

        internal bool VerifyIdentityResult { get; set; } = true;

        internal int CaptureCallCount { get; private set; }

        internal int LeaseAttemptCount { get; private set; }

        internal int OutstandingLeaseCount
        {
            get
            {
                lock (syncRoot)
                {
                    return outstanding.Count;
                }
            }
        }

        public ISkillRegistryGenerationCapture? CaptureGeneration()
        {
            lock (syncRoot)
            {
                CaptureCallCount++;
                var available = captureAvailable.Count > 0 ? captureAvailable.Dequeue() : true;
                return available ? new ScriptedCapture() : null;
            }
        }

        public bool TryAcquireGenerationReadLease(
            ISkillRegistryGenerationCapture capture,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ISkillRegistryGenerationLease? lease)
        {
            lock (syncRoot)
            {
                LeaseAttemptCount++;
                lease = null;
                if (capture is not ScriptedCapture typedCapture)
                    return false;
                var grant = leaseGrants.Count > 0 ? leaseGrants.Dequeue() : true;
                if (!grant)
                    return false;
                var scriptedLease = new ScriptedLease(this, typedCapture);
                outstanding.Add(scriptedLease);
                lease = scriptedLease;
                return true;
            }
        }

        public bool VerifyGenerationIdentity(ISkillRegistryGenerationCapture capture, ISkillRegistryGenerationLease lease)
        {
            lock (syncRoot)
            {
                if (capture is not ScriptedCapture typedCapture || lease is not ScriptedLease typedLease)
                    return false;
                return VerifyIdentityResult && ReferenceEquals(typedCapture, typedLease.Capture);
            }
        }

        public bool IsProducerTupleAccepted(ISkillRegistryGenerationLease lease, SkillRegistryProducerTuple tuple)
        {
            lock (syncRoot)
            {
                return lease is ScriptedLease && TupleAccepted(tuple);
            }
        }

        private void Release(ScriptedLease lease)
        {
            lock (syncRoot)
            {
                outstanding.Remove(lease);
            }
        }

        private sealed class ScriptedCapture : ISkillRegistryGenerationCapture
        {
        }

        private sealed class ScriptedLease : ISkillRegistryGenerationLease
        {
            private readonly ScriptedRegistryGenerationAuthority authority;
            private int released;

            internal ScriptedLease(ScriptedRegistryGenerationAuthority authority, ScriptedCapture capture)
            {
                this.authority = authority;
                Capture = capture;
            }

            internal ScriptedCapture Capture { get; }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref released, 1) == 0)
                {
                    authority.Release(this);
                }
            }
        }
    }

    private sealed class TestDatabase : IDisposable
    {
        internal TestDatabase()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"skill-current-authorization-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Path = System.IO.Path.Combine(Root, "monitor.db");
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);
                transaction.Commit();
            }
            InstallComponent();
        }

        internal string Root { get; }

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

        private void InstallComponent()
        {
            using (var retentionConnection = Open())
            using (var retentionTransaction = retentionConnection.BeginTransaction())
            {
                RetentionSchemaMigrator.Apply(retentionConnection, retentionTransaction);
                retentionTransaction.Commit();
            }
            new SqliteSourceCompatibilityStore(Path).CreateSchema();
            new SqliteSessionStore(Path).CreateSchema();
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            SkillProjectionSchemaV1.Ensure(connection, transaction);
            LocalRepositoryCatalogSchemaV1.Ensure(connection, transaction);
            LocalArchiveSchemaV1.Ensure(connection, transaction);
            SkillInvocationSnapshotSchemaV1.Ensure(connection, transaction);
            transaction.Commit();
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
}
