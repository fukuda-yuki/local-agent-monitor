using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionChildTriggerExtensionRegistryTests
{
    private const string UpdateTriggerName = "skill_invocation_snapshot_session_event_update_rejected";
    private const string DeleteTriggerName = "skill_invocation_snapshot_session_event_delete_rejected";
    private const string CaseAliasedUpdateTriggerName = "SKILL_INVOCATION_SNAPSHOT_SESSION_EVENT_UPDATE_REJECTED";
    private const string AdditionalNamespaceTriggerName = "skill_invocation_snapshot_session_event_insert_rejected";
    private const string AdditionalNamespaceTriggerSql =
        "CREATE TRIGGER skill_invocation_snapshot_session_event_insert_rejected\n"
        + "BEFORE INSERT ON session_events\n"
        + "BEGIN SELECT RAISE(ABORT,'skill_invocation_snapshot_extra_trigger'); END;";
    private const string ChildStampSql =
        "INSERT INTO schema_version(component,version) VALUES('skill_invocation_snapshot',1);";

    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string DdlArtifactPath = Path.Combine(
        RepositoryRoot, "docs", "specifications", "contracts", "skill-invocation-snapshot", "v1",
        "skill-invocation-snapshot.schema.v1.sql");
    private static readonly string RegistryArtifactPath = Path.Combine(
        RepositoryRoot, "docs", "specifications", "contracts", "session", "v14",
        "session-child-trigger-extensions-r0001.json");
    private static readonly string InstalledSqlGoldenPath = Path.Combine(
        RepositoryRoot, "tests", "CopilotAgentObservability.LocalMonitor.Tests", "TestData",
        "SkillInvocationSnapshot", "session-v14-child-trigger-installed-sql.golden.json");

    private static readonly byte[] DdlArtifactBytes = File.ReadAllBytes(DdlArtifactPath);
    private static readonly byte[] RegistryArtifactBytes = File.ReadAllBytes(RegistryArtifactPath);
    private static readonly string DdlArtifactText = Encoding.UTF8.GetString(DdlArtifactBytes);
    private static readonly IReadOnlyDictionary<string, GoldenTriggerEntry> GoldenEntries = ParseGoldenEntries();

    // GROUP A -- registry data integrity

    [Fact]
    public void RegistryTriggerSql_MatchesContractJsonByteForByte()
    {
        var contractTriggers = ParseContractTriggerSql();
        var registryTriggers = SessionChildTriggerExtensionRegistry.Entries[0].Triggers;

        Assert.Equal(contractTriggers.Count, registryTriggers.Count);
        foreach (var registryTrigger in registryTriggers)
        {
            var contractSql = contractTriggers[registryTrigger.Name];
            Assert.Equal(contractSql, registryTrigger.Sql);
            Assert.Equal(Encoding.UTF8.GetBytes(contractSql), Encoding.UTF8.GetBytes(registryTrigger.Sql));
        }
    }

    [Fact]
    public void ArtifactBytes_HashToPinnedShaValues()
    {
        Assert.Equal(
            "0b5f7782a9686791c2ce9bcff8638dccf1de44833303c0932f05e2ae57259c64",
            Sha256Hex(RegistryArtifactBytes));
        Assert.Equal(
            "502f787c28b13363826aeccde96979ed22dc89c8ee137593922b106528935d7c",
            Sha256Hex(DdlArtifactBytes));
    }

    [Theory]
    [InlineData(UpdateTriggerName)]
    [InlineData(DeleteTriggerName)]
    public void RegistryTriggerSql_HasNoCarriageReturnOrBomAndEndsWithBareEnd(string triggerName)
    {
        var sql = RegistryTrigger(triggerName).Sql;
        var bytes = Encoding.UTF8.GetBytes(sql);

        Assert.DoesNotContain((byte)0x0D, bytes);
        Assert.DoesNotContain('\uFEFF', sql);
        Assert.False(sql.EndsWith(';'));
        Assert.EndsWith("END", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Entries_HasExactlyOneComponentWithTwoSessionEventsTriggers()
    {
        var entries = SessionChildTriggerExtensionRegistry.Entries;

        Assert.Single(entries);
        var entry = entries[0];
        Assert.Equal("skill_invocation_snapshot", entry.Component);
        Assert.Equal(1, entry.Version);
        Assert.Equal("session", entry.ParentComponent);
        Assert.Equal(14, entry.ParentVersion);
        Assert.Equal(2, entry.Triggers.Count);
        Assert.All(entry.Triggers, trigger => Assert.Equal("session_events", trigger.TargetTable));
    }

    // GROUP B -- executable DDL versus installed SQL (TDD slice 3, first half)

    [Fact]
    public void InstalledChildDdl_SessionEventsTriggerSql_MatchesRegistryOrdinal()
    {
        using var temp = new MonitorTempDirectory();
        CreateSessionSchema(temp);
        using var connection = Open(temp.DatabasePath);
        InstallChildDdl(connection);

        foreach (var trigger in SessionChildTriggerExtensionRegistry.Entries[0].Triggers)
            Assert.Equal(trigger.Sql, InstalledTriggerSql(connection, trigger.Name));
    }

    [Fact]
    public void InstalledChildDdlWithStamp_PassesCanonicalizerEqualityAndRawOrdinalEquality()
    {
        using var temp = new MonitorTempDirectory();
        CreateSessionSchema(temp);
        using var connection = Open(temp.DatabasePath);
        InstallChildDdl(connection);
        InstallChildStamp(connection);

        Assert.True(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
        foreach (var trigger in SessionChildTriggerExtensionRegistry.Entries[0].Triggers)
            Assert.Equal(trigger.Sql, InstalledTriggerSql(connection, trigger.Name));
    }

    [Theory]
    [InlineData(UpdateTriggerName, 276, 275, 59, 68)]
    [InlineData(DeleteTriggerName, 276, 275, 59, 68)]
    public void DdlSourceStatement_DiffersFromInstalledSqlOnlyByTerminalSemicolon(
        string triggerName, int ddlSourceBytes, int installedBytes, int ddlTerminalByte, int installedTerminalByte)
    {
        var ddlStatement = ExtractDdlTriggerStatement(triggerName);
        var installedStatement = RegistryTrigger(triggerName).Sql;
        var golden = GoldenEntries[triggerName];

        Assert.Equal(installedStatement + ";", ddlStatement);
        Assert.Equal(ddlSourceBytes, Encoding.UTF8.GetByteCount(ddlStatement));
        Assert.Equal(installedBytes, Encoding.UTF8.GetByteCount(installedStatement));
        Assert.Equal(golden.DdlSourceUtf8Bytes, ddlSourceBytes);
        Assert.Equal(golden.SqliteSchemaSqlUtf8Bytes, installedBytes);
        Assert.Equal(golden.DdlSourceSha256, Sha256Hex(Encoding.UTF8.GetBytes(ddlStatement)));
        Assert.Equal(golden.SqliteSchemaSqlSha256, Sha256Hex(Encoding.UTF8.GetBytes(installedStatement)));
        Assert.Equal((byte)ddlTerminalByte, Encoding.UTF8.GetBytes(ddlStatement)[^1]);
        Assert.Equal((byte)installedTerminalByte, Encoding.UTF8.GetBytes(installedStatement)[^1]);
        Assert.Equal(golden.DdlTerminalByte, ddlTerminalByte);
        Assert.Equal(golden.InstalledTerminalByte, installedTerminalByte);
    }

    // GROUP C -- parent validation matrix (TDD slice 3, second half)

    [Fact]
    public void StampAndBothTriggersInstalled_AcceptsSessionSchema()
    {
        using var temp = new MonitorTempDirectory();
        CreateSessionSchema(temp);
        using var connection = Open(temp.DatabasePath);
        InstallChildDdl(connection);
        InstallChildStamp(connection);

        Assert.True(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
    }

    [Fact]
    public void TriggersInstalledWithoutStamp_RejectsSessionSchema()
    {
        using var temp = new MonitorTempDirectory();
        CreateSessionSchema(temp);
        using var connection = Open(temp.DatabasePath);
        InstallChildDdl(connection);

        Assert.False(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
    }

    [Fact]
    public void StampPresentWithNeitherTriggerInstalled_RejectsSessionSchema()
    {
        using var temp = new MonitorTempDirectory();
        CreateSessionSchema(temp);
        using var connection = Open(temp.DatabasePath);
        InstallChildStamp(connection);

        Assert.False(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
    }

    [Theory]
    [InlineData(UpdateTriggerName)]
    [InlineData(DeleteTriggerName)]
    public void StampPresentWithOnlyOneTriggerInstalled_RejectsSessionSchema(string installedTriggerName)
    {
        using var temp = new MonitorTempDirectory();
        CreateSessionSchema(temp);
        using var connection = Open(temp.DatabasePath);
        InstallTrigger(connection, installedTriggerName);
        InstallChildStamp(connection);

        Assert.False(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
    }

    [Theory]
    [InlineData("appended-terminal-semicolon")]
    [InlineData("changed-internal-semicolon")]
    [InlineData("changed-when-clause")]
    [InlineData("changed-raise-message")]
    public void StampPresentWithAlteredTriggerSql_RejectsSessionSchema(string mutation)
    {
        using var temp = new MonitorTempDirectory();
        CreateSessionSchema(temp);
        using var connection = Open(temp.DatabasePath);
        InstallChildDdl(connection);
        InstallChildStamp(connection);

        MutateInstalledTriggerSql(connection, UpdateTriggerName, sql => mutation switch
        {
            "appended-terminal-semicolon" => sql + ";",
            "changed-internal-semicolon" => sql.Replace("); END", ");; END", StringComparison.Ordinal),
            "changed-when-clause" => sql.Replace("OLD.event_id", "NEW.event_id", StringComparison.Ordinal),
            "changed-raise-message" => sql.Replace(
                "skill_invocation_snapshot_event_immutable",
                "skill_invocation_snapshot_event_mutable",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        });

        Assert.False(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
    }

    [Fact]
    public void StampPresentWithTriggerOnWrongTargetTable_RejectsSessionSchema()
    {
        using var temp = new MonitorTempDirectory();
        CreateSessionSchema(temp);
        using var connection = Open(temp.DatabasePath);
        InstallTriggerOnTable(connection, UpdateTriggerName, "sessions");
        InstallTrigger(connection, DeleteTriggerName);
        InstallChildStamp(connection);

        Assert.False(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
    }

    [Fact]
    public void StampPresentWithCaseAliasedTriggerName_RejectsSessionSchema()
    {
        using var temp = new MonitorTempDirectory();
        CreateSessionSchema(temp);
        using var connection = Open(temp.DatabasePath);
        InstallTrigger(connection, DeleteTriggerName);
        InstallCaseAliasedUpdateTrigger(connection);
        InstallChildStamp(connection);

        Assert.False(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
    }

    [Fact]
    public void StampPresentWithAdditionalRegisteredNamespaceTrigger_RejectsSessionSchema()
    {
        using var temp = new MonitorTempDirectory();
        CreateSessionSchema(temp);
        using var connection = Open(temp.DatabasePath);
        InstallChildDdl(connection);
        Execute(connection, AdditionalNamespaceTriggerSql);
        InstallChildStamp(connection);

        Assert.Equal(
            AdditionalNamespaceTriggerName,
            Scalar<string>(
                connection,
                "SELECT name FROM sqlite_schema WHERE type='trigger' AND name='"
                + AdditionalNamespaceTriggerName + "';"));
        Assert.False(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
    }

    [Fact]
    public void StampComponentCaseWrong_RejectsSessionSchema()
    {
        using var temp = new MonitorTempDirectory();
        CreateSessionSchema(temp);
        using var connection = Open(temp.DatabasePath);
        InstallChildDdl(connection);
        Execute(connection, "INSERT INTO schema_version(component,version) VALUES('Skill_Invocation_Snapshot',1);");

        Assert.False(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
    }

    // schema_version.version is declared INTEGER, so affinity rewrites a text '1' stamp into
    // integer 1 on store. Holding the spec's literal text stamp requires widening that column,
    // which itself changes the Session profile — so IsCurrentSchemaValid cannot isolate the
    // typeof guard. The two tests below separate the confound from the guard.
    [Fact]
    public void WidenedSchemaVersionColumnAlone_AlreadyRejectsSessionSchema()
    {
        using var temp = new MonitorTempDirectory();
        CreateSessionSchema(temp);
        using var connection = Open(temp.DatabasePath);
        WidenSchemaVersionColumnToText(connection);

        Assert.False(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
    }

    [Fact]
    public void StampVersionStoredAsText_ResolvesIncompatible()
    {
        using var temp = new MonitorTempDirectory();
        CreateSessionSchema(temp);
        using var connection = Open(temp.DatabasePath);
        InstallChildDdl(connection);
        WidenSchemaVersionColumnToText(connection);
        Execute(connection, "INSERT INTO schema_version(component,version) VALUES('skill_invocation_snapshot','1');");

        Assert.Equal(
            "text",
            Scalar<string>(
                connection,
                "SELECT typeof(version) FROM schema_version WHERE component='skill_invocation_snapshot';"));
        Assert.Equal(
            SessionChildTriggerExtensionRegistry.StampKind.Incompatible,
            SessionChildTriggerExtensionRegistry.ResolveStamp(connection, null, "session", 14).Kind);
        Assert.False(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
    }

    [Fact]
    public void ExactStamp_ResolvesActiveWithTheRegisteredEntry()
    {
        using var temp = new MonitorTempDirectory();
        CreateSessionSchema(temp);
        using var connection = Open(temp.DatabasePath);
        InstallChildDdl(connection);
        InstallChildStamp(connection);

        var resolution = SessionChildTriggerExtensionRegistry.ResolveStamp(connection, null, "session", 14);

        Assert.Equal(SessionChildTriggerExtensionRegistry.StampKind.Active, resolution.Kind);
        Assert.Equal("skill_invocation_snapshot", Assert.Single(resolution.ActiveEntries).Component);
    }

    [Fact]
    public void AbsentStamp_ResolvesInactive()
    {
        using var temp = new MonitorTempDirectory();
        CreateSessionSchema(temp);
        using var connection = Open(temp.DatabasePath);

        Assert.Equal(
            SessionChildTriggerExtensionRegistry.StampKind.Inactive,
            SessionChildTriggerExtensionRegistry.ResolveStamp(connection, null, "session", 14).Kind);
    }

    [Fact]
    public void ExactStampUnderAnEarlierParentVersion_ResolvesIncompatible()
    {
        using var temp = new MonitorTempDirectory();
        CreateSessionSchema(temp);
        using var connection = Open(temp.DatabasePath);
        InstallChildDdl(connection);
        InstallChildStamp(connection);

        Assert.Equal(
            SessionChildTriggerExtensionRegistry.StampKind.Incompatible,
            SessionChildTriggerExtensionRegistry.ResolveStamp(connection, null, "session", 13).Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void StampVersionOutsideRegisteredValue_RejectsSessionSchema(int version)
    {
        using var temp = new MonitorTempDirectory();
        CreateSessionSchema(temp);
        using var connection = Open(temp.DatabasePath);
        InstallChildDdl(connection);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO schema_version(component,version) VALUES('skill_invocation_snapshot',$version);";
            command.Parameters.AddWithValue("$version", version);
            command.ExecuteNonQuery();
        }

        Assert.False(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
    }

    [Fact]
    public void DuplicateStampRowsForSameComponent_RejectsSessionSchema()
    {
        using var temp = new MonitorTempDirectory();
        CreateSessionSchema(temp);
        using (var connection = Open(temp.DatabasePath))
        {
            InstallChildDdl(connection);
            InstallChildStamp(connection);
            RemoveSchemaVersionPrimaryKeyConstraint(connection);
        }
        SqliteConnection.ClearAllPools();
        using var reopened = Open(temp.DatabasePath);
        Execute(reopened, ChildStampSql);

        Assert.Equal(
            2L,
            Scalar<long>(reopened, "SELECT COUNT(*) FROM schema_version WHERE component='skill_invocation_snapshot';"));
        Assert.False(SqliteSessionStore.IsCurrentSchemaValid(reopened, null));
    }

    [Fact]
    public void StampedDatabase_PhysicallyAddsChildTriggersWithoutChangingParentAcceptance()
    {
        using var plainTemp = new MonitorTempDirectory();
        CreateSessionSchema(plainTemp);
        using var plainConnection = Open(plainTemp.DatabasePath);

        using var stampedTemp = new MonitorTempDirectory();
        CreateSessionSchema(stampedTemp);
        using var stampedConnection = Open(stampedTemp.DatabasePath);
        InstallChildDdl(stampedConnection);
        InstallChildStamp(stampedConnection);

        var plainTriggerNames = SessionEventsTriggerNames(plainConnection);
        var stampedTriggerNames = SessionEventsTriggerNames(stampedConnection);
        Assert.DoesNotContain(UpdateTriggerName, plainTriggerNames);
        Assert.DoesNotContain(DeleteTriggerName, plainTriggerNames);
        Assert.Contains(UpdateTriggerName, stampedTriggerNames);
        Assert.Contains(DeleteTriggerName, stampedTriggerNames);
        Assert.Equal(plainTriggerNames.Count + 2, stampedTriggerNames.Count);

        // The parent fingerprint (SessionSchemaV11Validator.Fingerprint) is private with no public
        // seam. The observable proxy for "the stamped database's parent fingerprint is unchanged"
        // is that both the plain database and the stamped database -- which physically carries the
        // two extra session_events triggers asserted above -- are accepted by the same
        // IsCurrentSchemaValid call path.
        Assert.True(SqliteSessionStore.IsCurrentSchemaValid(plainConnection, null));
        Assert.True(SqliteSessionStore.IsCurrentSchemaValid(stampedConnection, null));
    }

    [Fact]
    public void PartialChildWithOnlySessionEventsTriggersInstalled_AcceptsParentSessionSchema()
    {
        using var temp = new MonitorTempDirectory();
        CreateSessionSchema(temp);
        using var connection = Open(temp.DatabasePath);
        InstallTrigger(connection, UpdateTriggerName);
        InstallTrigger(connection, DeleteTriggerName);
        InstallChildStamp(connection);

        Assert.Equal(
            0L,
            Scalar<long>(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name IN "
                + "('skill_invocation_snapshots','skill_invocation_snapshot_receipts');"));

        // Spec: docs/specifications/interfaces/skill-invocation-snapshot.md line 326, "Strongest
        // counterexample" -- the parent Session validator proves only the exact stamp and the two
        // session_events triggers; it does not require the child tables or the other six triggers,
        // so parent validation may correctly accept a database whose skill_invocation_snapshot
        // component is otherwise incomplete. Only the later mandatory skill_invocation_snapshot:1
        // validator rejects that partial component.
        Assert.True(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
    }

    private static void CreateSessionSchema(MonitorTempDirectory temp) =>
        new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider).CreateSchema();

    private static void InstallChildDdl(SqliteConnection connection) => Execute(connection, DdlArtifactText);

    private static void InstallChildStamp(SqliteConnection connection) => Execute(connection, ChildStampSql);

    private static void InstallTrigger(SqliteConnection connection, string triggerName) =>
        Execute(connection, RegistryTrigger(triggerName).Sql + ";");

    private static void InstallTriggerOnTable(SqliteConnection connection, string triggerName, string targetTable)
    {
        var trigger = RegistryTrigger(triggerName);
        var retargeted = trigger.Sql.Replace(
            $"ON {trigger.TargetTable}", $"ON {targetTable}", StringComparison.Ordinal);
        Execute(connection, retargeted + ";");
    }

    private static void InstallCaseAliasedUpdateTrigger(SqliteConnection connection)
    {
        var aliased = RegistryTrigger(UpdateTriggerName).Sql.Replace(
            UpdateTriggerName, CaseAliasedUpdateTriggerName, StringComparison.Ordinal);
        Execute(connection, aliased + ";");
    }

    private static void MutateInstalledTriggerSql(
        SqliteConnection connection, string triggerName, Func<string, string> mutate)
    {
        var current = InstalledTriggerSql(connection, triggerName);
        Execute(connection, "PRAGMA writable_schema=ON;");
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE sqlite_schema SET sql=$sql WHERE type='trigger' AND name=$name;";
            command.Parameters.AddWithValue("$sql", mutate(current));
            command.Parameters.AddWithValue("$name", triggerName);
            command.ExecuteNonQuery();
        }
        Execute(connection, "PRAGMA writable_schema=OFF;");
    }

    private static void WidenSchemaVersionColumnToText(SqliteConnection connection)
    {
        Execute(connection, "PRAGMA writable_schema=ON;");
        Execute(
            connection,
            "UPDATE sqlite_schema SET sql='CREATE TABLE schema_version (component TEXT PRIMARY KEY, version TEXT NOT NULL)' WHERE type='table' AND name='schema_version';");
        Execute(connection, "PRAGMA writable_schema=RESET;");
    }

    private static void RemoveSchemaVersionPrimaryKeyConstraint(SqliteConnection connection)
    {
        Execute(connection, "PRAGMA writable_schema=ON;");
        Execute(
            connection,
            "UPDATE sqlite_schema SET sql='CREATE TABLE schema_version (component TEXT NOT NULL, version INTEGER NOT NULL)' WHERE type='table' AND name='schema_version';"
            + "DELETE FROM sqlite_schema WHERE type='index' AND tbl_name='schema_version';");
        Execute(connection, "PRAGMA writable_schema=RESET;");
    }

    private static string InstalledTriggerSql(SqliteConnection connection, string triggerName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='trigger' AND name=$name;";
        command.Parameters.AddWithValue("$name", triggerName);
        return (string)command.ExecuteScalar()!;
    }

    private static IReadOnlyList<string> SessionEventsTriggerNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type='trigger' AND tbl_name='session_events' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private static string ExtractDdlTriggerStatement(string triggerName)
    {
        var start = DdlArtifactText.IndexOf($"CREATE TRIGGER {triggerName}", StringComparison.Ordinal);
        var end = DdlArtifactText.IndexOf("END;", start, StringComparison.Ordinal) + "END;".Length;
        return DdlArtifactText[start..end];
    }

    private static SessionChildTriggerExtensionRegistry.ChildTrigger RegistryTrigger(string triggerName) =>
        SessionChildTriggerExtensionRegistry.Entries[0].Triggers.Single(trigger => trigger.Name == triggerName);

    private static IReadOnlyDictionary<string, string> ParseContractTriggerSql()
    {
        using var document = JsonDocument.Parse(RegistryArtifactBytes);
        var entry = document.RootElement.GetProperty("entries").GetProperty("skill_invocation_snapshot:1");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var trigger in entry.GetProperty("triggers").EnumerateArray())
            result.Add(trigger.GetProperty("name").GetString()!, trigger.GetProperty("sql").GetString()!);
        return result;
    }

    private static IReadOnlyDictionary<string, GoldenTriggerEntry> ParseGoldenEntries()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(InstalledSqlGoldenPath));
        var result = new Dictionary<string, GoldenTriggerEntry>(StringComparer.Ordinal);
        foreach (var entry in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            result.Add(
                entry.GetProperty("name").GetString()!,
                new(
                    entry.GetProperty("ddl_source_utf8_bytes").GetInt32(),
                    entry.GetProperty("ddl_source_sha256").GetString()!,
                    entry.GetProperty("sqlite_schema_sql_utf8_bytes").GetInt32(),
                    entry.GetProperty("sqlite_schema_sql_sha256").GetString()!,
                    entry.GetProperty("ddl_terminal_byte").GetInt32(),
                    entry.GetProperty("installed_terminal_byte").GetInt32()));
        }
        return result;
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }

    private sealed record GoldenTriggerEntry(
        int DdlSourceUtf8Bytes,
        string DdlSourceSha256,
        int SqliteSchemaSqlUtf8Bytes,
        string SqliteSchemaSqlSha256,
        int DdlTerminalByte,
        int InstalledTerminalByte);
}
