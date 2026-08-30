using CopilotAgentObservability.LocalMonitor.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Trait("ValidationLane", "Nightly")]
public sealed class SessionTerminalOutcomePolicyTests
{
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-08-09T00:00:00Z");

    public static TheoryData<string, string, string?> SdkCases => new()
    {
        { "session.task_complete", "{}", "clean" },
        { "session.task_complete", "{\"shutdownType\":\"error\"}", "clean" },
        { "session.shutdown", "{\"shutdownType\":\"routine\"}", "clean" },
        { "session.shutdown", "{\"shutdownType\":\"error\"}", "failed" },
        { "session.shutdown", "{}", "neutral" },
        { "session.shutdown", "{\"shutdownType\":\"routine\",\"shutdownType\":\"error\"}", "neutral" },
        { "session.shutdown", "{\"shutdownType\":7}", "neutral" },
        { "session.shutdown", "{\"shutdownType\":\"Routine\"}", "neutral" },
        { "session.Shutdown", "{\"shutdownType\":\"error\"}", null },
        { "session.taskComplete", "{}", null },
    };

    public static TheoryData<string, string, string, string?> CompatibleHookCases
    {
        get
        {
            var cases = new TheoryData<string, string, string, string?>();
            foreach (var surface in new[] { "copilot-cli", "vscode", "hook-unknown" })
            {
                cases.Add(surface, "SessionEnd", "{\"reason\":\"complete\"}", "clean");
                cases.Add(surface, "SessionEnd", "{\"reason\":\"user_exit\"}", "clean");
                cases.Add(surface, "SessionEnd", "{\"reason\":\"error\"}", "failed");
                cases.Add(surface, "SessionEnd", "{\"reason\":\"timeout\"}", "failed");
                cases.Add(surface, "SessionEnd", "{\"reason\":\"abort\"}", "neutral");
                cases.Add(surface, "SessionEnd", "{}", "neutral");
                cases.Add(surface, "SessionEnd", "{\"reason\":\"complete\",\"reason\":\"error\"}", "neutral");
                cases.Add(surface, "SessionEnd", "{\"reason\":false}", "neutral");
                cases.Add(surface, "SessionEnd", "{\"reason\":\"Complete\"}", "neutral");
                cases.Add(surface, "sessionend", "{\"reason\":\"complete\"}", null);
            }
            return cases;
        }
    }

    public static TheoryData<string, string?> ClaudeCases => new()
    {
        { "clear", "clean" },
        { "resume", "clean" },
        { "logout", "clean" },
        { "prompt_input_exit", "clean" },
        { "bypass_permissions_disabled", "neutral" },
        { "other", "neutral" },
        { "future_admitted_reason", "neutral" },
    };

    public static TheoryData<string, string, string, string, string?> OtherTupleCases => new()
    {
        { "copilot-sdk-stream", "copilot-cli", "session.task_complete", "{}", null },
        { "copilot-compatible-hook", "copilot-sdk", "SessionEnd", "{\"reason\":\"error\"}", null },
        { "claude-code-hook", "hook-unknown", "SessionEnd", "{\"reason\":\"clear\"}", null },
        { "Copilot-sdk-stream", "copilot-sdk", "session.task_complete", "{}", null },
        { "copilot-compatible-hook", "copilot-cli", "SESSIONEND", "{\"reason\":\"error\"}", null },
        { "claude-code-hook", "claude-code", "Sessionend", "{\"reason\":\"clear\"}", null },
        { "claude-code-otel", "claude-code", "otel.span", "{\"status\":\"error\"}", null },
        { "copilot-compatible-hook", "copilot-cli", "Stop", "{\"reason\":\"error\"}", null },
        { "copilot-compatible-hook", "copilot-cli", "PostToolUseFailure", "{\"error\":\"failed\"}", null },
        { "copilot-sdk-stream", "copilot-sdk", "subagent.failed", "{\"status\":\"error\"}", null },
    };

    [Theory]
    [MemberData(nameof(SdkCases))]
    public void Normalizer_ClassifiesExactSdkPolicyBeforePersistence(
        string type,
        string payload,
        string? expectedOutcome) =>
        AssertNormalizerOutcome("copilot-sdk-stream", "copilot-sdk", type, payload, expectedOutcome);

    [Theory]
    [MemberData(nameof(CompatibleHookCases))]
    public void Normalizer_ClassifiesExactCompatibleHookPolicy(
        string surface,
        string type,
        string payload,
        string? expectedOutcome) =>
        AssertNormalizerOutcome("copilot-compatible-hook", surface, type, payload, expectedOutcome);

    [Theory]
    [MemberData(nameof(ClaudeCases))]
    public void Normalizer_ClassifiesAdmittedClaudeReason(string reason, string? expectedOutcome) =>
        AssertNormalizerOutcome("claude-code-hook", "claude-code", "SessionEnd", $$"""{"reason":"{{reason}}"}""", expectedOutcome);

    [Theory]
    [MemberData(nameof(OtherTupleCases))]
    public void Normalizer_LeavesEveryOtherTupleWithoutTerminalFact(
        string adapter,
        string surface,
        string type,
        string payload,
        string? expectedOutcome) =>
        AssertNormalizerOutcome(adapter, surface, type, payload, expectedOutcome);

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"reason\":7}")]
    [InlineData("{\"reason\":\"clear\",\"reason\":\"other\"}")]
    [InlineData("[]")]
    public void Normalizer_RejectsInvalidClaudeLiveDiscriminatorWithoutWriting(string payload)
    {
        using var temp = new MonitorTempDirectory();
        var store = CreateStore(temp);
        var envelope = Envelope("claude-code-hook", "claude-code", "SessionEnd", payload);

        Assert.Throws<InvalidOperationException>(() => new SessionEventNormalizer(store, temp.TimeProvider).NormalizeAndWrite(envelope));

        using var connection = Open(temp.DatabasePath);
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_events;"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM sessions;"));
    }

    [Fact]
    public void Normalizer_ClassifiesRawDiscriminatorBeforeSecretFilteringAndContentRemoval()
    {
        using var temp = new MonitorTempDirectory();
        var store = CreateStore(temp);
        var envelope = Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            "SessionEnd",
            "{\"reason\":\"error\",\"api_key\":\"synthetic-secret\"}");

        new SessionEventNormalizer(store, temp.TimeProvider).NormalizeAndWrite(envelope);

        using var connection = Open(temp.DatabasePath);
        Assert.Equal("failed|1", Scalar<string>(connection, "SELECT terminal_outcome||'|'||terminal_policy_version FROM session_events;"));
        Assert.DoesNotContain("api_key", Scalar<string>(connection, "SELECT content_json FROM session_event_content;"), StringComparison.OrdinalIgnoreCase);
        Execute(connection, "DELETE FROM session_event_content;");
        Assert.Equal("failed|1", Scalar<string>(connection, "SELECT terminal_outcome||'|'||terminal_policy_version FROM session_events;"));
    }

    [Fact]
    public void Normalizer_ReplayRequiresTheSameImmutableTerminalFact()
    {
        using var temp = new MonitorTempDirectory();
        var store = CreateStore(temp);
        var normalizer = new SessionEventNormalizer(store, temp.TimeProvider);
        var clean = Envelope("copilot-compatible-hook", "vscode", "SessionEnd", "{\"reason\":\"complete\"}");
        normalizer.NormalizeAndWrite(clean);
        normalizer.NormalizeAndWrite(clean);

        var conflict = Envelope("copilot-compatible-hook", "vscode", "SessionEnd", "{\"reason\":\"error\"}");
        Assert.Throws<InvalidOperationException>(() => normalizer.NormalizeAndWrite(conflict));

        using var connection = Open(temp.DatabasePath);
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_events;"));
        Assert.Equal("clean|1", Scalar<string>(connection, "SELECT terminal_outcome||'|'||terminal_policy_version FROM session_events;"));
    }

    [Fact]
    public void Version13Migration_ClassifiesPinnedContentAfterItsOriginalExpiry()
    {
        using var temp = new MonitorTempDirectory();
        var store = CreateStore(temp);
        new SessionEventNormalizer(store, temp.TimeProvider).NormalizeAndWrite(Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            "SessionEnd",
            "{\"reason\":\"error\"}"));
        string contentBefore;
        string catalogBefore;
        using (var connection = Open(temp.DatabasePath))
        {
            Execute(connection, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");
            Execute(connection, "UPDATE retention_items SET state='retained_by_policy' WHERE store_kind='session_event_content';");
            contentBefore = Scalar<string>(connection, "SELECT event_id||'|'||content_kind||'|'||content_json||'|'||captured_at||'|'||expires_at||'|'||hex(retention_owner_token) FROM session_event_content;");
            catalogBefore = Scalar<string>(connection, "SELECT item_id||'|'||store_instance_id||'|'||store_kind||'|'||source_item_id||'|'||receipt_version||'|'||hex(ownership_receipt)||'|'||captured_at||'|'||expires_at||'|'||policy_id||'|'||policy_version||'|'||state||'|'||revision||'|'||adapter_coverage_version FROM retention_items WHERE store_kind='session_event_content';");
            SessionVersion13TestFixture.DowngradeSessionEvents(connection);
            Assert.True(SessionSchemaV11Validator.IsCurrentV13SchemaValidSelectOnly(connection, null));
            Assert.Equal("1|1|1|5|5", Scalar<string>(connection, "SELECT (SELECT COUNT(*) FROM retention_component_versions)||'|'||(SELECT COUNT(*) FROM retention_component_versions WHERE component='retention' AND typeof(version)='integer' AND version=1)||'|'||(SELECT COUNT(*) FROM retention_store_instances)||'|'||(SELECT COUNT(*) FROM retention_adapter_coverage)||'|'||(SELECT COUNT(*) FROM retention_adapter_coverage WHERE coverage_version=1 AND store_kind IN ('session_event_content','raw_record','analysis_run_raw','sensitive_bundle','analysis_sdk_directory'));"));
        }

        temp.TimeProvider = new MutableTimeProvider(DateTimeOffset.UnixEpoch.AddDays(91));
        new SqliteSessionStore(temp.DatabasePath, temp.TimeProvider).CreateSchema();

        using var migrated = Open(temp.DatabasePath);
        Assert.Equal(14L, Scalar<long>(migrated, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal("failed|1", Scalar<string>(migrated, "SELECT terminal_outcome||'|'||terminal_policy_version FROM session_events;"));
        Assert.Equal("retained_by_policy", Scalar<string>(migrated, "SELECT state FROM retention_items WHERE store_kind='session_event_content';"));
        Assert.Equal(1L, Scalar<long>(migrated, "SELECT COUNT(*) FROM retention_items i JOIN session_event_content c ON c.event_id=i.source_item_id WHERE i.expires_at=c.expires_at AND i.expires_at<'1970-04-02T00:00:00.0000000+00:00';"));
        Assert.Equal(contentBefore, Scalar<string>(migrated, "SELECT event_id||'|'||content_kind||'|'||content_json||'|'||captured_at||'|'||expires_at||'|'||hex(retention_owner_token) FROM session_event_content;"));
        Assert.Equal(catalogBefore, Scalar<string>(migrated, "SELECT item_id||'|'||store_instance_id||'|'||store_kind||'|'||source_item_id||'|'||receipt_version||'|'||hex(ownership_receipt)||'|'||captured_at||'|'||expires_at||'|'||policy_id||'|'||policy_version||'|'||state||'|'||revision||'|'||adapter_coverage_version FROM retention_items WHERE store_kind='session_event_content';"));
        Assert.Equal("session_events", Scalar<string>(migrated, "SELECT \"table\" FROM pragma_foreign_key_list('session_event_content') WHERE \"from\"='event_id';"));
        Assert.Equal("session_events", Scalar<string>(migrated, "SELECT \"table\" FROM pragma_foreign_key_list('session_events') WHERE \"from\"='parent_event_id';"));
        Assert.Equal(0L, Scalar<long>(migrated, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        Assert.True(SqliteSessionStore.IsCurrentSchemaValid(migrated, null));
        var schemaSql = Scalar<string>(migrated, "SELECT sql FROM sqlite_schema WHERE type='table' AND name='session_events';");
        var schemaFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(schemaSql))).ToLowerInvariant();
        Assert.Equal("081400ac41b59fcdc776b0443da9a3aec8121f1fd6b27b22277c54702706cc94", schemaFingerprint);
    }

    [Theory]
    [InlineData("copilot-compatible-hook", SessionSourceSurface.CopilotCli, "Stop", "not-a-timestamp")]
    [InlineData("copilot-compatible-hook", SessionSourceSurface.CopilotCli, "Stop", "2026-08-09T09:00:00.0000000+09:00")]
    [InlineData("claude-code-otel", SessionSourceSurface.ClaudeCode, "otel.span", "not-a-timestamp")]
    [InlineData("claude-code-otel", SessionSourceSurface.ClaudeCode, "otel.span", "2026-08-09T09:00:00.0000000+09:00")]
    public void Version13Migration_NonfactTuplePreservesRawOccurredAtBytes(
        string sourceAdapter,
        SessionSourceSurface sourceSurface,
        string type,
        string occurredAt)
    {
        using var temp = new MonitorTempDirectory();
        var store = CreateStore(temp);
        SeedStoredEvent(store, sourceAdapter, sourceSurface, type);
        using (var connection = Open(temp.DatabasePath))
        {
            SessionVersion13TestFixture.DowngradeSessionEvents(connection);
            SetOccurredAt(connection, occurredAt);
        }

        new SqliteSessionStore(temp.DatabasePath, temp.TimeProvider).CreateSchema();

        using var migrated = Open(temp.DatabasePath);
        Assert.Equal(14L, Scalar<long>(migrated, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal(occurredAt, Scalar<string>(migrated, "SELECT occurred_at FROM session_events;"));
        Assert.Equal("null|null", Scalar<string>(migrated, "SELECT typeof(terminal_outcome)||'|'||typeof(terminal_policy_version) FROM session_events;"));
        Assert.Equal("active|<null>", Scalar<string>(migrated, "SELECT status||'|'||IFNULL(ended_at,'<null>') FROM sessions;"));
    }

    [Theory]
    [InlineData("not-a-timestamp")]
    [InlineData("2026-08-09T09:00:00.0000000+09:00")]
    public void Version13Migration_RecognizedFactRejectsMalformedOrNoncanonicalOccurredAtAtomically(string occurredAt)
    {
        using var temp = new MonitorTempDirectory();
        var store = CreateStore(temp);
        SeedStoredEvent(store, "copilot-sdk-stream", SessionSourceSurface.CopilotSdk, "session.task_complete");
        using (var connection = Open(temp.DatabasePath))
        {
            SessionVersion13TestFixture.DowngradeSessionEvents(connection);
            SetOccurredAt(connection, occurredAt);
        }

        Assert.ThrowsAny<Exception>(() =>
            new SqliteSessionStore(temp.DatabasePath, temp.TimeProvider).CreateSchema());

        using var rejected = Open(temp.DatabasePath);
        Assert.Equal(13L, Scalar<long>(rejected, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal(occurredAt, Scalar<string>(rejected, "SELECT occurred_at FROM session_events;"));
        Assert.Equal(0L, Scalar<long>(rejected, "SELECT COUNT(*) FROM pragma_table_info('session_events') WHERE name IN ('terminal_outcome','terminal_policy_version');"));
    }

    [Theory]
    [InlineData("copilot-sdk-stream", SessionSourceSurface.CopilotSdk, "session.task_complete", "clean")]
    [InlineData("copilot-compatible-hook", SessionSourceSurface.CopilotCli, "Stop", null)]
    [InlineData("claude-code-otel", SessionSourceSurface.ClaudeCode, "otel.span", null)]
    public void Version13Migration_NoReadTupleDoesNotConsultPartialRetentionSchema(
        string sourceAdapter,
        SessionSourceSurface sourceSurface,
        string type,
        string? expectedOutcome)
    {
        using var temp = new MonitorTempDirectory();
        var store = CreateStore(temp);
        SeedStoredEvent(store, sourceAdapter, sourceSurface, type);
        using (var connection = Open(temp.DatabasePath))
        {
            SessionVersion13TestFixture.DowngradeSessionEvents(connection);
            Execute(connection, "DROP TABLE retention_component_versions;");
        }

        new SqliteSessionStore(temp.DatabasePath, temp.TimeProvider).CreateSchema();

        using var migrated = Open(temp.DatabasePath);
        Assert.Equal(14L, Scalar<long>(migrated, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal(expectedOutcome, ScalarOrNull<string>(migrated, "SELECT terminal_outcome FROM session_events;"));
    }

    [Fact]
    public void Version13Migration_DiscriminatorTupleRejectsPartialCurrentRetentionSchemaAtomically()
    {
        using var temp = new MonitorTempDirectory();
        SeedVersion13DiscriminatorFixture(temp);
        using (var connection = Open(temp.DatabasePath))
            Execute(connection, "DROP TABLE retention_mutation_previews;");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new SqliteSessionStore(temp.DatabasePath, temp.TimeProvider).CreateSchema());
        Assert.Equal("Invalid Retention authority during Session migration.", exception.Message);

        using var rejected = Open(temp.DatabasePath);
        Assert.Equal(13L, Scalar<long>(rejected, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal(0L, Scalar<long>(rejected, "SELECT COUNT(*) FROM pragma_table_info('session_events') WHERE name IN ('terminal_outcome','terminal_policy_version');"));
        Assert.Equal(1L, Scalar<long>(rejected, "SELECT COUNT(*) FROM session_event_content;"));
        Assert.Equal(1L, Scalar<long>(rejected, "SELECT COUNT(*) FROM retention_items WHERE store_kind='session_event_content';"));
    }

    [Fact]
    public void Version13Migration_DiscriminatorTupleRejectsMalformedCurrentRetentionSchemaAtomically()
    {
        using var temp = new MonitorTempDirectory();
        SeedVersion13DiscriminatorFixture(temp);
        using (var connection = Open(temp.DatabasePath))
        {
            Execute(connection, """
                ALTER TABLE retention_adapter_coverage RENAME TO retention_adapter_coverage_valid;
                CREATE TABLE retention_adapter_coverage(store_kind TEXT PRIMARY KEY, coverage_version INTEGER NOT NULL);
                INSERT INTO retention_adapter_coverage SELECT store_kind,coverage_version FROM retention_adapter_coverage_valid;
                DROP TABLE retention_adapter_coverage_valid;
                """);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new SqliteSessionStore(temp.DatabasePath, temp.TimeProvider).CreateSchema());
        Assert.Equal("Invalid Retention authority during Session migration.", exception.Message);

        using var rejected = Open(temp.DatabasePath);
        Assert.Equal(13L, Scalar<long>(rejected, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal(0L, Scalar<long>(rejected, "SELECT COUNT(*) FROM pragma_table_info('session_events') WHERE name IN ('terminal_outcome','terminal_policy_version');"));
    }

    [Fact]
    public void Version13Migration_DiscriminatorTupleRejectsSameNameIndexWithWrongTargetAtomically()
    {
        using var temp = new MonitorTempDirectory();
        SeedVersion13DiscriminatorFixture(temp);
        using (var connection = Open(temp.DatabasePath))
        {
            Execute(connection, """
                DROP TRIGGER IF EXISTS retention_raw_records_token_immutable;
                DROP TRIGGER IF EXISTS retention_monitor_analysis_runs_token_immutable;
                DROP INDEX IX_retention_items_expiry;
                CREATE INDEX IX_retention_items_expiry ON retention_items(item_id);
                """);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new SqliteSessionStore(temp.DatabasePath, temp.TimeProvider).CreateSchema());
        Assert.Equal("Invalid Retention authority during Session migration.", exception.Message);

        using var rejected = Open(temp.DatabasePath);
        Assert.Equal(13L, Scalar<long>(rejected, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal(0L, Scalar<long>(rejected, "SELECT COUNT(*) FROM pragma_table_info('session_events') WHERE name IN ('terminal_outcome','terminal_policy_version');"));
    }

    [Theory]
    [InlineData("DELETE FROM retention_adapter_coverage WHERE store_kind='analysis_sdk_directory';")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE retention_adapter_coverage SET coverage_version=CAST(x'01' AS BLOB) WHERE store_kind='raw_record'; PRAGMA ignore_check_constraints=OFF;")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE retention_adapter_coverage SET coverage_version=2 WHERE store_kind='raw_record'; PRAGMA ignore_check_constraints=OFF;")]
    [InlineData("UPDATE retention_store_instances SET store_instance_id='invalid';")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE retention_component_versions SET version=2; PRAGMA ignore_check_constraints=OFF;")]
    public void Version13Migration_AbsentDiscriminatorContentRejectsInvalidInstalledRetentionAuthorityAtomically(
        string authorityMutation)
    {
        using var temp = new MonitorTempDirectory();
        SeedVersion13DiscriminatorFixtureWithoutContent(temp);
        using (var connection = Open(temp.DatabasePath))
            Execute(connection, authorityMutation);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new SqliteSessionStore(temp.DatabasePath, temp.TimeProvider).CreateSchema());
        Assert.Equal("Invalid Retention authority during Session migration.", exception.Message);

        using var rejected = Open(temp.DatabasePath);
        Assert.Equal(13L, Scalar<long>(rejected, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal(0L, Scalar<long>(rejected, "SELECT COUNT(*) FROM pragma_table_info('session_events') WHERE name IN ('terminal_outcome','terminal_policy_version');"));
        Assert.Equal(0L, Scalar<long>(rejected, "SELECT COUNT(*) FROM session_event_content;"));
        Assert.Equal(0L, Scalar<long>(rejected, "SELECT COUNT(*) FROM retention_items WHERE store_kind='session_event_content';"));
    }

    [Fact]
    public void Version13Migration_AbsentDiscriminatorContentWithExactInstalledRetentionAuthorityMigratesNeutral()
    {
        using var temp = new MonitorTempDirectory();
        SeedVersion13DiscriminatorFixtureWithoutContent(temp);

        new SqliteSessionStore(temp.DatabasePath, temp.TimeProvider).CreateSchema();

        using var migrated = Open(temp.DatabasePath);
        Assert.Equal(14L, Scalar<long>(migrated, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal("neutral|1", Scalar<string>(migrated, "SELECT terminal_outcome||'|'||terminal_policy_version FROM session_events;"));
        Assert.Equal(0L, Scalar<long>(migrated, "SELECT COUNT(*) FROM session_event_content;"));
        Assert.Equal(0L, Scalar<long>(migrated, "SELECT COUNT(*) FROM retention_items WHERE store_kind='session_event_content';"));
    }

    [Theory]
    [InlineData("terminal_outcome=NULL,terminal_policy_version=CAST(x'01' AS BLOB)")]
    [InlineData("terminal_outcome='clean',terminal_policy_version=NULL")]
    [InlineData("terminal_outcome=CAST(x'636C65616E' AS BLOB),terminal_policy_version=1")]
    [InlineData("terminal_outcome='clean',terminal_policy_version='not-integer'")]
    [InlineData("terminal_outcome='clean',terminal_policy_version=1.5")]
    [InlineData("terminal_outcome='clean',terminal_policy_version=CAST(x'01' AS BLOB)")]
    [InlineData("terminal_outcome='future',terminal_policy_version=1")]
    [InlineData("terminal_outcome='clean',terminal_policy_version=2")]
    public void CurrentSchemaValidation_RejectsNonCanonicalTerminalFactStorageClasses(string assignment)
    {
        using var temp = new MonitorTempDirectory();
        var store = CreateStore(temp);
        new SessionEventNormalizer(store, temp.TimeProvider).NormalizeAndWrite(Envelope(
            "copilot-sdk-stream",
            "copilot-sdk",
            "session.task_complete",
            "{}"));
        using var connection = Open(temp.DatabasePath);
        Execute(connection, $"PRAGMA ignore_check_constraints=ON; UPDATE session_events SET {assignment}; PRAGMA ignore_check_constraints=OFF;");

        Assert.False(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
    }

    [Theory]
    [InlineData("2026-08-09T09:00:00.0000000+09:00")]
    [InlineData("2026-08-09T00:00:00Z")]
    [InlineData("not-a-timestamp")]
    public void CurrentSchemaValidation_RejectsNonCanonicalTerminalFactTimestampBytes(string occurredAt)
    {
        using var temp = new MonitorTempDirectory();
        var store = CreateStore(temp);
        new SessionEventNormalizer(store, temp.TimeProvider).NormalizeAndWrite(Envelope(
            "copilot-sdk-stream",
            "copilot-sdk",
            "session.task_complete",
            "{}"));
        using var connection = Open(temp.DatabasePath);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE session_events SET occurred_at=$occurred_at;";
            command.Parameters.AddWithValue("$occurred_at", occurredAt);
            command.ExecuteNonQuery();
        }

        Assert.False(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
    }

    [Fact]
    public void CurrentSchemaValidation_AcceptsCanonicalTerminalFactStorageAndTimestamp()
    {
        using var temp = new MonitorTempDirectory();
        var store = CreateStore(temp);
        new SessionEventNormalizer(store, temp.TimeProvider).NormalizeAndWrite(Envelope(
            "copilot-sdk-stream",
            "copilot-sdk",
            "session.task_complete",
            "{}"));
        using var connection = Open(temp.DatabasePath);

        Assert.True(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
        Assert.Equal(
            "text|integer|2026-08-09T00:00:00.0000000+00:00",
            Scalar<string>(connection, "SELECT typeof(terminal_outcome)||'|'||typeof(terminal_policy_version)||'|'||occurred_at FROM session_events;"));
    }

    private static void AssertNormalizerOutcome(
        string adapter,
        string surface,
        string type,
        string payload,
        string? expectedOutcome)
    {
        using var temp = new MonitorTempDirectory();
        var store = CreateStore(temp);

        new SessionEventNormalizer(store, temp.TimeProvider).NormalizeAndWrite(Envelope(adapter, surface, type, payload));

        using var connection = Open(temp.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT terminal_outcome,terminal_policy_version FROM session_events;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        if (expectedOutcome is null)
        {
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
        }
        else
        {
            Assert.Equal(expectedOutcome, reader.GetString(0));
            Assert.Equal(1L, reader.GetInt64(1));
        }
        Assert.False(reader.Read());
    }

    private static SqliteSessionStore CreateStore(MonitorTempDirectory temp)
    {
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        return store;
    }

    private static void SeedVersion13DiscriminatorFixture(MonitorTempDirectory temp)
    {
        var store = CreateStore(temp);
        new SessionEventNormalizer(store, temp.TimeProvider).NormalizeAndWrite(Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            "SessionEnd",
            "{\"reason\":\"error\"}"));
        using var connection = Open(temp.DatabasePath);
        Execute(connection, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");
        SessionVersion13TestFixture.DowngradeSessionEvents(connection);
        Assert.True(SessionSchemaV11Validator.IsCurrentV13SchemaValidSelectOnly(connection, null));
    }

    private static void SeedVersion13DiscriminatorFixtureWithoutContent(MonitorTempDirectory temp)
    {
        SeedVersion13DiscriminatorFixture(temp);
        using var connection = Open(temp.DatabasePath);
        Execute(connection, "DELETE FROM retention_items WHERE store_kind='session_event_content'; DELETE FROM session_event_content;");
    }

    private static void SeedStoredEvent(
        SqliteSessionStore store,
        string sourceAdapter,
        SessionSourceSurface sourceSurface,
        string type)
    {
        var sessionId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        var now = ObservedAt;
        var @event = new ObservedSessionEvent(
            eventId,
            sessionId,
            RunId: null,
            sourceSurface,
            ParentEventId: null,
            TraceId: sourceAdapter == "claude-code-otel" ? "migration-no-read-trace" : null,
            Status: null,
            sourceAdapter,
            SourceEventId: $"migration-no-read-{type}",
            type,
            now,
            sourceAdapter == "claude-code-otel" ? SessionContentState.NotCaptured : SessionContentState.Available);
        var session = new ObservedSession(
            sessionId,
            ObservedSessionStatus.Active,
            SessionCompleteness.Partial,
            Repository: null,
            Workspace: null,
            StartedAt: null,
            EndedAt: null,
            LastSeenAt: now,
            SessionRawRetentionState.NotCaptured,
            CreatedAt: now,
            UpdatedAt: now);
        var content = sourceAdapter == "claude-code-otel"
            ? Array.Empty<SessionEventContent>()
            : [new SessionEventContent(eventId, "application/json", "{}", now, now.AddDays(90))];
        store.Write(new(new(session, [], [], [@event]), content));
    }

    private static SessionIngestEnvelope Envelope(
        string adapter,
        string surface,
        string type,
        string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return new(
            1,
            adapter,
            surface,
            "terminal-policy-session",
            [new("terminal-policy-event", type, ObservedAt.ToString("O"), document.RootElement.Clone())],
            SourceApplicationVersion: adapter == "claude-code-hook" ? "2.1.207" : null,
            AdapterVersion: adapter == "claude-code-hook" ? "claude-hook-v1" : null,
            NormalizationVersion: adapter == "claude-code-hook" ? "session-normalization-v1" : null);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private static T? ScalarOrNull<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void SetOccurredAt(SqliteConnection connection, string occurredAt)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE session_events SET occurred_at=$occurred_at;";
        command.Parameters.AddWithValue("$occurred_at", occurredAt);
        command.ExecuteNonQuery();
    }

}
