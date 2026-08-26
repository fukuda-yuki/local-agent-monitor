using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using Microsoft.Data.Sqlite;
using System.Reflection;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RuntimeBackupLocalWorkspaceProjectionTests
{
    private static readonly DateTimeOffset PublicationAt = DateTimeOffset.Parse("2026-08-26T00:00:00Z");

    [Fact]
    public void ConfiguredProductionServicePreservesCurrentSdkSnapshotAndWorkspaceFactThroughRestore()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        var archive = fixture.Path("configured-production.zip");
        var restored = fixture.Path("configured-production-restored.db");
        var created = fixture.Service.CreateAndPublish(fixture.DatabasePath, archive);
        var inspected = fixture.Service.Inspect(archive);
        var restore = fixture.Service.Restore(archive, restored, new RuntimeRestoreOptions());

        Assert.True(created.Success, created.ErrorCode);
        Assert.True(inspected.Success, inspected.ErrorCode);
        Assert.Equal(1, inspected.RowCounts!["skill_invocation_snapshots"]);
        Assert.Equal(1, inspected.RowCounts["local_workspace_session_search_facts"]);
        Assert.True(restore.Success, restore.ErrorCode);
        Assert.Equal(["demo-skill"], Strings(restored, "SELECT normalized_text FROM local_workspace_session_search_facts WHERE kind='skill';"));
        Assert.Equal(1L, Scalar(restored, "SELECT COUNT(*) FROM skill_invocation_snapshots;"));
    }

    [Fact]
    public void PublicationUsesOneCapturedInstantToExcludeFactAtExpiryEquality()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt);
        var archive = fixture.Path("expiry-equality.zip");

        var created = fixture.Service.CreateAndPublish(fixture.DatabasePath, archive);

        Assert.True(created.Success, created.ErrorCode);
        Assert.Equal(0L, Scalar(fixture.DatabasePath, "SELECT COUNT(*) FROM local_workspace_session_search_facts WHERE kind='skill';"));
        Assert.Equal(PublicationAt, fixture.Clock.FirstObservedInstant);
    }

    [Fact]
    public void PreRefreshBusyMapsToSnapshotStoreBusyWithoutEscaping()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        using var blocker = Open(fixture.DatabasePath);
        using var transaction = blocker.BeginTransaction(deferred: false);

        var result = fixture.Service.CreateAndPublish(fixture.DatabasePath, fixture.Path("busy.zip"));

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.SnapshotStoreBusy, result.ErrorCode);
    }

    [Fact]
    public void InvalidProjectionAtPreRefreshMapsToRestoreIncompatibleWithoutEscaping()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        Execute(fixture.DatabasePath, "ALTER TABLE local_workspace_projection_state ADD COLUMN injected TEXT;");

        var result = fixture.Service.CreateAndPublish(fixture.DatabasePath, fixture.Path("invalid-projection.zip"));

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
    }

    [Theory]
    [InlineData("UPDATE local_workspace_sessions SET label_text='tampered';")]
    [InlineData("UPDATE local_workspace_session_search_facts SET normalized_text='tampered' WHERE kind='label';")]
    [InlineData("UPDATE local_workspace_sessions SET revision_seed='tampered';")]
    [InlineData("UPDATE local_workspace_session_models SET model='tampered';")]
    [InlineData("UPDATE local_workspace_token_observations SET input_tokens=999;")]
    [InlineData("DELETE FROM local_workspace_session_activity WHERE kind='retry';")]
    [InlineData("UPDATE local_workspace_sessions SET capture_notes='unknown';")]
    [InlineData("UPDATE local_workspace_sessions SET capture_notes='raw_content_expired,raw_content_expired';")]
    [InlineData("UPDATE local_workspace_sessions SET capture_notes='raw_content_not_captured,projection_invalid';")]
    public void ValidationRejectsSemanticProjectionTampering(string mutation)
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000003','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,'gpt-5',NULL,NULL,10,3,13,'active');
            INSERT INTO session_events VALUES('0198f5b8-0c00-7000-8000-000000000002','0198f5b8-0c00-7000-8000-000000000001',NULL,NULL,NULL,NULL,NULL,'synthetic','prompt-1','user.message','2026-08-24T00:00:00.0000000+00:00','available',NULL,NULL,NULL,NULL,NULL,NULL,NULL);
            INSERT INTO session_event_content VALUES('0198f5b8-0c00-7000-8000-000000000002','application/json','{"value":"hello"}','2026-08-24T00:00:00.0000000+00:00','2099-01-01T00:00:00.0000000+00:00',randomblob(32));
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        LocalWorkspaceProjectionSchemaTests.Execute(connection, mutation);

        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Equal("local_workspace_projection_backup_invalid", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionBackupValidation.Validate(connection, transaction)).Message);
    }

    [Fact]
    public void ValidationAcceptsExactComponentAndRejectsTamperedOwnedSchema()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            LocalWorkspaceProjectionBackupValidation.Validate(connection, transaction);
            transaction.Rollback();
        }

        LocalWorkspaceProjectionSchemaTests.Execute(connection, "ALTER TABLE local_workspace_projection_state ADD COLUMN injected TEXT;");
        using var tampered = connection.BeginTransaction(deferred: true);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionBackupValidation.Validate(connection, tampered));
        Assert.Equal("local_workspace_projection_backup_invalid", exception.Message);
    }

    [Fact]
    public void CurrentBackupInventoryIncludesVersionAndAllSixRowCounts()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(8, LocalWorkspaceProjectionSchemaV1.TableNames.Length);
        Assert.All(LocalWorkspaceProjectionSchemaV1.TableNames, table =>
            Assert.Equal([table], LocalWorkspaceProjectionSchemaTests.Strings(connection, $"SELECT name FROM sqlite_schema WHERE type='table' AND name='{table}';")));
    }

    [Fact]
    public void MigrationOrderPlacesProjectionAfterBothSkillAuthorities()
    {
        var order = Assert.IsType<string[]>(typeof(SqliteRuntimeBackupService)
            .GetField("MigrationOrder", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));
        var projection = Array.IndexOf(order, "local_workspace_projection");
        Assert.True(projection > Array.IndexOf(order, "skill_projection"));
        Assert.True(projection > Array.IndexOf(order, "skill_invocation_snapshot"));
    }

    [Fact]
    public void ValidationRejectsRecordedLabelWithoutExactAuthorizedContent()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');");
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "UPDATE local_workspace_sessions SET label_state='recorded',label_text='x',label_source_identity='0198f5b8-0c00-7000-8000-000000000099',label_expires_at='2099-01-01T00:00:00.0000000+00:00'; INSERT INTO local_workspace_session_search_facts VALUES((SELECT session_id FROM local_workspace_sessions),'label','0198f5b8-0c00-7000-8000-000000000099','x','2099-01-01T00:00:00.0000000+00:00');");

        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Equal("local_workspace_projection_backup_invalid", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionBackupValidation.Validate(connection, transaction)).Message);
    }

    [Fact]
    public void ValidationRejectsFactExpiredAtBackupPublicationTime()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');");
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "INSERT INTO local_workspace_session_search_facts VALUES((SELECT session_id FROM sessions),'label','stale','stale','2026-08-26T00:00:00.0000000+00:00');");

        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Equal("local_workspace_projection_backup_invalid", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionBackupValidation.Validate(connection, transaction, DateTimeOffset.Parse("2026-08-26T00:00:00Z"))).Message);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string[] Strings(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private sealed class ConfiguredBackupFixture : IDisposable
    {
        private readonly LocalRepositoryCatalogFixture fixture = new();

        internal ConfiguredBackupFixture(DateTimeOffset publicationAt, DateTimeOffset expiresAt)
        {
            Clock = new RecordingTimeProvider(publicationAt);
            var gate = new LocalWorkspacePublicationGate();
            Authority = new SkillInvocationV2RegistryProviderV1(SkillInvocationV2ArtifactRegistry.Load(), gate);
            Service = new SqliteRuntimeBackupService(Clock, Authority, gate);
            var initialized = Service.Initialize(DatabasePath);
            Assert.True(initialized.Success, initialized.ErrorCode);
            var participant = new LocalWorkspaceProjectionTransactionParticipant(Authority);
            var facts = SkillInvocationV2IngestRequestFactsV1.Derive(
                SkillInvocationV2Parser.Parse(Encoding.UTF8.GetBytes(ValidRequest), new ProductionRuntimeCapability()));
            var ingested = SkillInvocationV2IngestTransactionV1.Execute(
                DatabasePath, facts, Authority, new RecordingTimeProvider(expiresAt.AddDays(-90)),
                () => true, () => true, CancellationToken.None, gate, participant);
            Assert.Equal(SkillInvocationV2IngestOutcomeV1.Committed, ingested.Outcome);
        }

        internal string DatabasePath => fixture.DatabasePath;
        internal RecordingTimeProvider Clock { get; }
        internal SkillInvocationV2RegistryProviderV1 Authority { get; }
        internal SqliteRuntimeBackupService Service { get; }
        internal string Path(string name) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(DatabasePath)!, name);
        public void Dispose() => fixture.Dispose();

        private const string ValidRequest = "{\"schema_version\":2,\"source_adapter\":\"copilot-sdk-stream\",\"source_surface\":\"copilot-sdk\",\"native_session_id\":\"backup-sdk-session\",\"source_application_version\":\"1.0.65\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v2\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c\",\"events\":[{\"source_event_id\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\",\"source_parent_event_id\":null,\"type\":\"skill.invoked\",\"occurred_at\":\"2026-08-09T00:00:00.0000000+00:00\",\"run_native_id\":null,\"source_ephemeral\":false,\"trace_id\":null,\"span_id\":null,\"payload\":{\"name\":\"demo-skill\",\"path\":\"skills/demo/SKILL.md\",\"content\":\"synthetic body\",\"source\":\"project\",\"trigger\":\"user-invoked\"}}]}";
    }

    private sealed class ProductionRuntimeCapability : ISkillInvocationV2RuntimeCapability
    {
        public CopilotAgentObservability.LocalMonitor.SkillRuntime.CertifiedSkillProducerIdentityV1 CertifiedIdentity { get; } =
            SkillInvocationV2TestIdentity.V1065;
    }

    private sealed class RecordingTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        internal DateTimeOffset? FirstObservedInstant { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            FirstObservedInstant ??= instant;
            return instant;
        }
    }
}
