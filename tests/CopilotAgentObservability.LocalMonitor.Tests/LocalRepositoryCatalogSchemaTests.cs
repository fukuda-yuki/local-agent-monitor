using Microsoft.Data.Sqlite;
using SQLitePCL;
using System.Collections.Frozen;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Repositories;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Trait("ValidationLane", "Nightly")]
public sealed class LocalRepositoryCatalogSchemaTests
{
    private const string At = "2026-08-01T00:00:00.0000000+00:00";

    [Fact]
    public void Ensure_CreatesTheCurrentIndependentCatalogAndIsRepeatable()
    {
        using var database = new TestDatabase();
        new SqliteSessionStore(database.Path).CreateSchema();
        using var connection = Open(database.Path);

        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        LocalRepositoryCatalogSchemaV1.Ensure(connection);

        Assert.Equal(1L, ScalarLong(connection,
            "SELECT version FROM schema_version WHERE component='local_repository_catalog';"));
        Assert.Equal(12L, ScalarLong(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND (name='local_repositories' OR name LIKE 'local_repository_%' OR name LIKE 'session_repository_%');"));
        Assert.Equal(6L, ScalarLong(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name LIKE 'IX_%repository%';"));
        Assert.Equal(18L, ScalarLong(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND (name LIKE 'local_repository%' OR name LIKE 'session_repository%');"));
        Assert.Equal(0L, ScalarLong(connection,
            "SELECT COUNT(*) FROM pragma_table_info('session_repository_observation_contexts') WHERE name LIKE '%label%';"));
        Assert.Equal(0L, ScalarLong(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE sql LIKE '%label_only%';"));
        LocalRepositoryCatalogValidation.Validate(connection, transaction: null);
    }

    [Fact]
    public void FreshSchemaSeedsExactlyOneNullableProjectorStateAndRepeatEnsurePreservesIt()
    {
        using var database = new TestDatabase();
        new SqliteSessionStore(database.Path).CreateSchema();
        using var connection = Open(database.Path);

        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        LocalRepositoryCatalogSchemaV1.Ensure(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT projector_key,last_discovered_span_id,updated_at FROM local_repository_reconciliation_state;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("local-repository-catalog-v1", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.Equal("1970-01-01T00:00:00.0000000+00:00", reader.GetString(2));
        Assert.False(reader.Read());
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("newer")]
    [InlineData("case-colliding")]
    [InlineData("unknown")]
    public void Ensure_FailsClosedForAnyPreexistingReservedAuthority(string shape)
    {
        using var database = new TestDatabase();
        new SqliteSessionStore(database.Path).CreateSchema();
        using var connection = Open(database.Path);
        Execute(connection, shape switch
        {
            "partial" => "CREATE TABLE local_repositories(repository_id TEXT PRIMARY KEY);",
            "newer" => "INSERT INTO schema_version(component,version) VALUES('local_repository_catalog',2);",
            "case-colliding" => "CREATE TABLE Local_Repositories(repository_id TEXT PRIMARY KEY);",
            "unknown" => "CREATE TABLE local_repository_unknown(id INTEGER PRIMARY KEY);",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        });
        var before = ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%repository%';");

        Assert.Throws<InvalidOperationException>(() => LocalRepositoryCatalogSchemaV1.Ensure(connection));

        Assert.Equal(before, ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%repository%';"));
        Assert.Equal(shape == "newer" ? 1L : 0L, ScalarLong(connection,
            "SELECT COUNT(*) FROM schema_version WHERE component='local_repository_catalog';"));
    }

    [Fact]
    public void ImmutableLocator_RejectsUpdateDeleteAndReplaceWithRecursiveTriggersDisabled()
    {
        using var database = new TestDatabase();
        new SqliteSessionStore(database.Path).CreateSchema();
        using var connection = Open(database.Path);
        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        var repositoryId = Guid.CreateVersion7().ToString("D");
        var locatorId = Guid.CreateVersion7().ToString("D");
        Execute(connection, $"""
            INSERT INTO local_repositories VALUES('{repositoryId}','Synthetic',1,'{At}','{At}');
            INSERT INTO local_repository_locators VALUES(
                '{locatorId}','{repositoryId}','github_repository','github.com/example/repository',
                '{new string('a', 64)}','manual','example','repository','{At}');
            PRAGMA recursive_triggers=OFF;
            """);
        var before = ScalarText(connection, "SELECT canonical_locator FROM local_repository_locators;");

        foreach (var sql in new[]
        {
            "UPDATE local_repository_locators SET canonical_locator='github.com/other/repository';",
            "DELETE FROM local_repository_locators;",
            $"INSERT OR REPLACE INTO local_repository_locators VALUES('{locatorId}','{repositoryId}','github_repository','github.com/other/repository','{new string('b', 64)}','manual','other','repository','{At}');",
            $"INSERT OR REPLACE INTO local_repository_locators VALUES('{Guid.CreateVersion7():D}','{repositoryId}','github_repository','github.com/other/repository','{new string('a', 64)}','manual','other','repository','{At}');",
        })
        {
            var error = Assert.Throws<SqliteException>(() => Execute(connection, sql));
            Assert.Contains("local_repository_catalog_append_only", error.Message, StringComparison.Ordinal);
            Assert.Equal(before, ScalarText(connection, "SELECT canonical_locator FROM local_repository_locators;"));
        }
    }

    [Theory]
    [InlineData("018e4fd6-4b1e-7a5c-8a2e-20ff0d270000", true)]
    [InlineData("018e4fd6-4b1e-6a5c-8a2e-20ff0d270000", false)]
    [InlineData("018E4FD6-4B1E-7A5C-8A2E-20FF0D270000", false)]
    public void ScalarValidators_EnforceCanonicalUuidV7(string value, bool expected) =>
        Assert.Equal(expected, LocalRepositoryCatalogValidation.IsCanonicalUuidV7(value));

    [Theory]
    [InlineData("Repository", true)]
    [InlineData(" Repository", false)]
    [InlineData("Repository\t", false)]
    [InlineData("a\U0001F600", true)]
    [InlineData("e\u0301", false)]
    [InlineData("a\u202eb", false)]
    public void DisplayNameValidation_UsesNfcUnicodeScalarsAndSafetyPolicy(string value, bool expected) =>
        Assert.Equal(expected, LocalRepositoryCatalogValidation.IsDisplayName(value));

    [Theory]
    [InlineData("2026-08-01T00:00:00.0000000+00:00", true)]
    [InlineData("2026-08-01T00:00:00.000000+00:00", false)]
    [InlineData("2026-02-30T00:00:00.0000000+00:00", false)]
    public void TimestampValidation_RequiresExactUtcRoundTrip(string value, bool expected) =>
        Assert.Equal(expected, LocalRepositoryCatalogValidation.IsCanonicalTimestamp(value));

    [Fact]
    public void CatalogObjectInventory_HasEveryNamedTableIndexAndTrigger()
    {
        using var database = new TestDatabase();
        new SqliteSessionStore(database.Path).CreateSchema();
        using var connection = Open(database.Path);
        LocalRepositoryCatalogSchemaV1.Ensure(connection);

        foreach (var table in LocalRepositoryCatalogSchemaV1.TableNames)
            Assert.Equal(1L, ScalarLong(connection, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';"));
        foreach (var index in LocalRepositoryCatalogSchemaV1.IndexNames)
            Assert.Equal(1L, ScalarLong(connection, $"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='{index}';"));
        Assert.True(ScalarLong(connection, "SELECT COUNT(*) FROM pragma_index_list('session_repository_assignment_history') WHERE [unique]=1;") >= 1);
        Assert.True(ScalarLong(connection, "SELECT COUNT(*) FROM pragma_index_list('local_repository_history') WHERE [unique]=1;") >= 1);
        foreach (var trigger in LocalRepositoryCatalogSchemaV1.TriggerDefinitions)
            Assert.Equal(1L, ScalarLong(connection, $"SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='{trigger.Name}' AND sql LIKE '%local_repository_catalog_append_only%';"));
    }

    [Fact]
    public void OwnedSchemaDescriptorCompiler_IsConnectionFreeImmutableAndRejectsDuplicateKeys()
    {
        var definitions = new List<SqliteOwnedSchemaDefinition>
        {
            new("table", "owned", "owned", "CREATE TABLE IF NOT EXISTS owned(id INTEGER);"),
        };

        var compiled = SqliteOwnedSchemaAuthority.Compile(definitions);
        definitions[0] = new SqliteOwnedSchemaDefinition("table", "owned", "owned", "CREATE TABLE owned(changed TEXT);");

        Assert.IsAssignableFrom<FrozenDictionary<(string Type, string Name), SqliteOwnedSchemaObject>>(compiled);
        Assert.Equal("6:create5:table5:owned1:(2:id7:integer1:)",
            compiled[("table", "owned")].Sql);
        Assert.Throws<ArgumentException>(() => SqliteOwnedSchemaAuthority.Compile(
        [
            new SqliteOwnedSchemaDefinition("table", "duplicate", "duplicate", "CREATE TABLE duplicate(id INTEGER);"),
            new SqliteOwnedSchemaDefinition("table", "duplicate", "duplicate", "CREATE TABLE duplicate(other INTEGER);"),
        ]));
    }

    [Fact]
    public void OwnedSchemaDescriptor_NormalizesTerminalSemicolonsAndIfNotExistsLikeInstalledSqlite()
    {
        var expected = SqliteOwnedSchemaAuthority.Compile(
        [
            new SqliteOwnedSchemaDefinition("table", "owned", "owned", "CREATE TABLE IF NOT EXISTS owned(id INTEGER);"),
            new SqliteOwnedSchemaDefinition("index", "IX_owned_id", "owned", "CREATE INDEX IF NOT EXISTS IX_owned_id ON owned(id);"),
        ]);
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE owned(id INTEGER); CREATE INDEX IX_owned_id ON owned(id);");

        var actual = SqliteOwnedSchemaAuthority.Read(connection, null, static (name, _) =>
            name.Equals("owned", StringComparison.OrdinalIgnoreCase)
            || name.Equals("IX_owned_id", StringComparison.OrdinalIgnoreCase));

        Assert.True(SqliteOwnedSchemaAuthority.Equal(actual, expected));
    }

    [Fact]
    public void OwnedSchemaDescriptor_NormalizationKeepsQuotedLiteralsInjectiveWhileNormalizingCreatePrefixes()
    {
        var firstQuotedLiteral = SqliteOwnedSchemaAuthority.Compile(
        [
            new SqliteOwnedSchemaDefinition("table", "owned", "owned", "CREATE TABLE owned(value TEXT DEFAULT 'a2:if3:not6:existsb');"),
        ]);
        var secondQuotedLiteral = SqliteOwnedSchemaAuthority.Compile(
        [
            new SqliteOwnedSchemaDefinition("table", "owned", "owned", "CREATE TABLE owned(value TEXT DEFAULT '2:if3:not6:existsab');"),
        ]);

        Assert.Equal(firstQuotedLiteral[("table", "owned")].Sql.Length, secondQuotedLiteral[("table", "owned")].Sql.Length);
        Assert.NotEqual(firstQuotedLiteral[("table", "owned")].Sql, secondQuotedLiteral[("table", "owned")].Sql);
        foreach (var (type, name, table, withPrefix, normalized) in new[]
        {
            ("table", "owned", "owned", "CREATE TABLE IF NOT EXISTS owned(value TEXT DEFAULT 'unchanged');", "create /* comment */ table owned(value text default 'unchanged')"),
            ("index", "IX_owned_value", "owned", "CREATE INDEX IF NOT EXISTS IX_owned_value ON owned(value);", "create /* comment */ index IX_owned_value on owned(value)"),
            ("trigger", "owned_insert", "owned", "CREATE TRIGGER IF NOT EXISTS owned_insert BEFORE INSERT ON owned BEGIN SELECT 1; END;", "create /* comment */ trigger owned_insert before insert on owned begin select 1; end"),
        })
        {
            var withPrefixObject = SqliteOwnedSchemaAuthority.Compile(
            [
                new SqliteOwnedSchemaDefinition(type, name, table, withPrefix),
            ]);
            var normalizedObject = SqliteOwnedSchemaAuthority.Compile(
            [
                new SqliteOwnedSchemaDefinition(type, name, table, normalized),
            ]);

            Assert.Equal(withPrefixObject[(type, name)].Sql, normalizedObject[(type, name)].Sql);
        }
    }

    [Fact]
    public void BlankCatalogInstall_ExecutesEachOwnedCreateDefinitionExactlyOnce()
    {
        using var database = new TestDatabase();
        new SqliteSessionStore(database.Path).CreateSchema();
        using var connection = Open(database.Path);
        var statements = new List<string>();
        strdelegate_trace trace = (_, sql) => statements.Add(sql);
        raw.sqlite3_trace(connection.Handle, trace, null);
        try
        {
            LocalRepositoryCatalogSchemaV1.Ensure(connection);
        }
        finally
        {
            raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null);
        }

        Assert.Equal(12, statements.Count(static sql => sql.TrimStart().StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(6, statements.Count(static sql => sql.TrimStart().StartsWith("CREATE INDEX", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(18, statements.Count(static sql => sql.TrimStart().StartsWith("CREATE TRIGGER", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void CatalogOwnerDescriptor_EqualsTheFreshInstalledTwelveTableSixIndexAndEighteenTriggerSchema()
    {
        using var database = new TestDatabase();
        new SqliteSessionStore(database.Path).CreateSchema();
        using var connection = Open(database.Path);
        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        using var transaction = connection.BeginTransaction();

        Assert.Equal(12L, ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND (name='local_repositories' OR name LIKE 'local_repository_%' OR name LIKE 'session_repository_%');"));
        Assert.Equal(6L, ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name LIKE 'IX_%repository%';"));
        Assert.Equal(18L, ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND (name LIKE 'local_repository%' OR name LIKE 'session_repository%');"));
        Assert.True(LocalRepositoryCatalogSchemaV1.HasExactOwnedSchema(connection, transaction));
        LocalRepositoryCatalogSchemaV1.Validate(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1;";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void OperationKeyValidation_RequiresCanonicalUnpaddedBase64Url32Bytes()
    {
        Assert.True(LocalRepositoryCatalogValidation.IsOperationKey("lrc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
        Assert.False(LocalRepositoryCatalogValidation.IsOperationKey("lrc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="));
        Assert.False(LocalRepositoryCatalogValidation.IsOperationKey("lrc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA-"));
    }

    [Fact]
    public void ImmutableTables_RejectEveryUpdateDeleteAndReplacementConflictWithRecursiveTriggersDisabled()
    {
        using var database = new TestDatabase();
        using var connection = CreateCompleteCatalog(database.Path);
        Execute(connection, "PRAGMA recursive_triggers=OFF;");

        foreach (var attack in ImmutableAttacks)
        {
            var before = Snapshot(connection, attack.Table);
            var update = Assert.Throws<SqliteException>(() => Execute(connection, $"UPDATE {attack.Table} SET {attack.MutableColumn}={attack.MutableValue};"));
            Assert.Contains("local_repository_catalog_append_only", update.Message, StringComparison.Ordinal);
            Assert.Equal(before, Snapshot(connection, attack.Table));

            var delete = Assert.Throws<SqliteException>(() => Execute(connection, $"DELETE FROM {attack.Table};"));
            Assert.Contains("local_repository_catalog_append_only", delete.Message, StringComparison.Ordinal);
            Assert.Equal(before, Snapshot(connection, attack.Table));

            foreach (var replacement in attack.Replacements)
            {
                var replace = Assert.Throws<SqliteException>(() => Execute(connection, replacement));
                Assert.Contains("local_repository_catalog_append_only", replace.Message, StringComparison.Ordinal);
                Assert.Equal(before, Snapshot(connection, attack.Table));
            }
        }
    }

    [Theory]
    [InlineData("missing-index", "DROP INDEX IX_local_repository_locators_repository_created;")]
    [InlineData("missing-trigger", "DROP TRIGGER local_repository_locators_update_rejected;")]
    [InlineData("altered-trigger", "DROP TRIGGER local_repository_locators_update_rejected; CREATE TRIGGER local_repository_locators_update_rejected BEFORE UPDATE ON local_repository_locators BEGIN SELECT RAISE(ABORT,'altered'); END;")]
    [InlineData("altered-table", "ALTER TABLE local_repositories ADD COLUMN altered INTEGER;")]
    [InlineData("extra-table", "CREATE TABLE local_repository_extra(id INTEGER PRIMARY KEY);")]
    [InlineData("extra-session-repository-table", "CREATE TABLE session_repository_extra(id INTEGER PRIMARY KEY);")]
    [InlineData("extra-index", "CREATE INDEX IX_local_repository_extra ON local_repositories(display_name);")]
    [InlineData("extra-trigger", "CREATE TRIGGER local_repository_extra_trigger BEFORE INSERT ON local_repositories BEGIN SELECT 1; END;")]
    public void Validate_FailsClosedAndDoesNotMutateForAnyOwnedObjectInventoryDeviation(string _, string mutation)
    {
        using var database = new TestDatabase();
        new SqliteSessionStore(database.Path).CreateSchema();
        using var connection = Open(database.Path);
        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        Execute(connection, mutation);
        var before = ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%repository%';");

        Assert.False(LocalRepositoryCatalogSchemaV1.HasExactOwnedSchema(connection, transaction: null));
        Assert.Throws<InvalidOperationException>(() => LocalRepositoryCatalogSchemaV1.Ensure(connection));

        Assert.Equal(before, ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%repository%';"));
    }

    [Theory]
    [InlineData("IX_local_repository_extra", false)]
    [InlineData("IX_SESSION_REPOSITORY_EXTRA", false)]
    [InlineData("IX_unrelated_extra", true)]
    public void HasExactOwnedSchema_ReservesCatalogIndexPrefixesOutsideCatalogTables(string indexName, bool expected)
    {
        using var database = new TestDatabase();
        new SqliteSessionStore(database.Path).CreateSchema();
        using var connection = Open(database.Path);
        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        Execute(connection, "CREATE TABLE raw_records(id INTEGER PRIMARY KEY);");
        Execute(connection, $"CREATE INDEX {indexName} ON raw_records(id);");

        Assert.Equal(expected, LocalRepositoryCatalogSchemaV1.HasExactOwnedSchema(connection, transaction: null));
    }

    [Fact]
    public void Validator_RejectsSemanticIdentityAndCauseReferenceMismatches()
    {
        using var database = new TestDatabase();
        using var connection = CreateCompleteCatalog(database.Path);
        Execute(connection, "UPDATE local_repository_reconciliation_queue SET reconciliation_fingerprint='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';");

        Assert.Throws<InvalidOperationException>(() => LocalRepositoryCatalogValidation.Validate(connection, null));
    }

    [Fact]
    public void Validator_AcceptsACompletePhysicalContextAndCauseGraph()
    {
        using var database = new TestDatabase();
        using var connection = CreateCompleteCatalog(database.Path);

        LocalRepositoryCatalogValidation.Validate(connection, null);
    }

    [Fact]
    public void Validator_AcceptsOriginalDisplayCasingForDurableLocatorAndAdmittedPhysicalObservation()
    {
        using var database = new TestDatabase();
        using var connection = CreateCompleteCatalog(database.Path, "https://github.com/Example/Widget");

        Assert.Equal("Example|Widget", ScalarText(connection,
            "SELECT display_owner || '|' || display_repository FROM local_repository_locators;"));
        Assert.Equal("Example|Widget", ScalarText(connection,
            "SELECT display_owner || '|' || display_repository FROM session_repository_observations;"));
        LocalRepositoryCatalogValidation.Validate(connection, null);
    }

    [Fact]
    public void Validator_AcceptsLogicalDisplayRepositoryEndingInLowercaseGit()
    {
        using var database = new TestDatabase();
        using var connection = CreateCompleteCatalog(database.Path, "https://github.com/Example/Repository.git.git");

        Assert.Equal("github.com/example/repository.git|Example|Repository.git", ScalarText(connection,
            "SELECT canonical_locator || '|' || display_owner || '|' || display_repository FROM local_repository_locators;"));
        Assert.Equal("github.com/example/repository.git|Example|Repository.git", ScalarText(connection,
            "SELECT canonical_locator || '|' || display_owner || '|' || display_repository FROM session_repository_observations;"));
        LocalRepositoryCatalogValidation.Validate(connection, null);
    }

    [Theory]
    [InlineData("locator display owner does not match", "UPDATE local_repository_locators SET display_owner='Other';")]
    [InlineData("observation display repository does not match", "UPDATE session_repository_observations SET display_repository='Gadget';")]
    [InlineData("locator display repository suffix casing changes identity", "UPDATE local_repository_locators SET display_repository='Widget.GIT';")]
    [InlineData("locator display owner has invalid grammar", "UPDATE local_repository_locators SET display_owner='-Example';")]
    [InlineData("observation display repository has invalid grammar", "UPDATE session_repository_observations SET display_repository='Widget/Other';")]
    [InlineData("locator canonical locator does not match", "UPDATE local_repository_locators SET canonical_locator='github.com/example/gadget';")]
    [InlineData("observation fingerprint does not match", "UPDATE session_repository_observations SET locator_sha256='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';")]
    [InlineData("locator canonical owner casing is not lowercase", "UPDATE local_repository_locators SET canonical_locator='github.com/Example/widget';")]
    [InlineData("observation canonical repository casing is not lowercase", "UPDATE session_repository_observations SET canonical_locator='github.com/example/Widget';")]
    public void Validator_RejectsPersistedLocatorFieldsThatCannotBeReparsedExactly(string _, string mutation)
    {
        using var database = new TestDatabase();
        using var connection = CreateCompleteCatalog(database.Path, "https://github.com/Example/Widget");
        Execute(connection, "DROP TRIGGER local_repository_locators_update_rejected; DROP TRIGGER session_repository_observations_update_rejected;");
        Execute(connection, mutation);

        Assert.Throws<InvalidOperationException>(() => LocalRepositoryCatalogValidation.ValidateRows(connection, null));
    }

    [Theory]
    [InlineData("01900000_0000-7000-8000-000000000082")]
    [InlineData("01900000-0000-7000-c000-000000000082")]
    [InlineData("01900000-0000-7000-8000-00000000008A")]
    [InlineData("01900000-0000-7000-8000-0000000000-2")]
    public void SchemaAndManagedValidation_RejectNoncanonicalUuidValues(string value)
    {
        using var database = new TestDatabase();
        new SqliteSessionStore(database.Path).CreateSchema();
        using var connection = Open(database.Path);
        LocalRepositoryCatalogSchemaV1.Ensure(connection);

        Assert.False(LocalRepositoryCatalogValidation.IsCanonicalUuidV7(value));
        Assert.Throws<SqliteException>(() => Execute(connection,
            $"INSERT INTO local_repositories VALUES('{value}','Synthetic',1,'{At}','{At}');"));
    }

    [Theory]
    [InlineData("2026-08-01T00:00:00.000000+00:00")]
    [InlineData("2026-02-30T00:00:00.0000000+00:00")]
    [InlineData("2026-08-01T00:00:00.0000000Z")]
    public void SchemaAndManagedValidation_RejectNoncanonicalTimestampText(string value)
    {
        using var database = new TestDatabase();
        new SqliteSessionStore(database.Path).CreateSchema();
        using var connection = Open(database.Path);
        LocalRepositoryCatalogSchemaV1.Ensure(connection);

        Assert.False(LocalRepositoryCatalogValidation.IsCanonicalTimestamp(value));
        Assert.Throws<SqliteException>(() => Execute(connection,
            $"INSERT INTO local_repositories VALUES('01900000-0000-7000-8000-000000000082','Synthetic',1,'{value}','{At}');"));
    }

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void SchemaAndManagedValidation_RejectNoncanonicalDigestText(string value)
    {
        using var database = new TestDatabase();
        using var connection = CreateCompleteCatalog(database.Path);

        Assert.False(LocalRepositoryCatalogValidation.IsLowerSha256(value));
        Assert.Throws<SqliteException>(() => Execute(connection, $"""
            INSERT INTO local_repository_operation_receipts VALUES(
                'lrc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAE','{value}',200,
                'application/json; charset=utf-8','no-store',X'7B7D','{At}');
            """));
    }

    [Fact]
    public void SchemaAndManagedValidation_RejectNoncanonicalOperationKeyFinalBase64UrlCharacter()
    {
        const string operationKey = "lrc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB";
        using var database = new TestDatabase();
        using var connection = CreateCompleteCatalog(database.Path);

        Assert.False(LocalRepositoryCatalogValidation.IsOperationKey(operationKey));
        Assert.Throws<SqliteException>(() => Execute(connection, $"""
            INSERT INTO local_repository_operation_receipts VALUES('{operationKey}',
                '{new string('a', 64)}',200,'application/json; charset=utf-8','no-store',X'7B7D','{At}');
            """));
    }

    [Theory]
    [InlineData("INSERT INTO local_repositories VALUES('01900000-0000-7000-8000-000000000082','Synthetic',X'01','2026-08-01T00:00:00.0000000+00:00','2026-08-01T00:00:00.0000000+00:00');")]
    [InlineData("INSERT INTO local_repositories VALUES('01900000-0000-7000-8000-000000000082','Synthetic',1,X'323032362D30382D30315430303A30303A30302E303030303030302B30303A3030','2026-08-01T00:00:00.0000000+00:00');")]
    public void Schema_RejectsWrongManagedScalarStorageClasses(string sql)
    {
        using var database = new TestDatabase();
        new SqliteSessionStore(database.Path).CreateSchema();
        using var connection = Open(database.Path);
        LocalRepositoryCatalogSchemaV1.Ensure(connection);

        Assert.Throws<SqliteException>(() => Execute(connection, sql));
    }

    [Fact]
    public void Schema_RejectsCanonical36ByteUuidBlobStorage()
    {
        const string uuidBlob = "X'30313930303030302D303030302D373030302D383030302D303030303030303030303832'";
        using var database = new TestDatabase();
        new SqliteSessionStore(database.Path).CreateSchema();
        using var connection = Open(database.Path);
        LocalRepositoryCatalogSchemaV1.Ensure(connection);

        Assert.Equal(36L, ScalarLong(connection, $"SELECT length({uuidBlob});"));
        Assert.Equal("blob", ScalarText(connection, $"SELECT typeof({uuidBlob});"));
        Assert.Throws<SqliteException>(() => Execute(connection,
            $"INSERT INTO local_repositories VALUES({uuidBlob},'Synthetic',1,'{At}','{At}');"));
    }

    [Fact]
    public void SchemaAndManagedValidation_RejectBlobDigestStorage()
    {
        const string digestBlob = "X'61616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161'";
        using var database = new TestDatabase();
        using var connection = CreateCompleteCatalog(database.Path);

        Assert.Equal(64L, ScalarLong(connection, $"SELECT length({digestBlob});"));
        Assert.Equal("blob", ScalarText(connection, $"SELECT typeof({digestBlob});"));
        Assert.Throws<SqliteException>(() => Execute(connection, $"""
            INSERT INTO local_repository_operation_receipts VALUES(
                'lrc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAE',{digestBlob},200,
                'application/json; charset=utf-8','no-store',X'7B7D','{At}');
            """));
    }

    [Fact]
    public void ManagedValidation_RejectsCatalogSchemaVersionStoredOutsideTheIntegerClass()
    {
        using var database = new TestDatabase();
        new SqliteSessionStore(database.Path).CreateSchema();
        using var connection = Open(database.Path);
        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        Execute(connection, "UPDATE schema_version SET version=X'31' WHERE component='local_repository_catalog';");

        Assert.Throws<InvalidOperationException>(() => LocalRepositoryCatalogValidation.Validate(connection, null));
    }

    [Theory]
    [InlineData("invalid_locator", "admitted", true)]
    [InlineData("admitted", "invalid_type", false)]
    [InlineData("admitted", "shadowed", false)]
    public void ManagedValidation_RejectsImpossiblePhysicalClassificationAndContextStatePairs(string classification, string state, bool claimsRepository)
    {
        using var database = new TestDatabase();
        using var connection = CreateCompleteCatalog(database.Path);
        Execute(connection, "DROP TRIGGER session_repository_observations_update_rejected; DROP TRIGGER session_repository_observation_contexts_update_rejected; DROP TRIGGER local_repository_history_delete_rejected; DELETE FROM local_repository_history;");
        var physicalValues = classification == "admitted"
            ? "'github_repository','github.com/example/repository',(SELECT locator_sha256 FROM local_repository_locators),'example','repository'"
            : "NULL,NULL,NULL,NULL,NULL";
        var contextValues = claimsRepository
            ? "'01900000-0000-7000-8000-000000000001','01900000-0000-7000-8000-000000000010'"
            : "NULL,NULL";
        Execute(connection, $"""
            UPDATE session_repository_observations
            SET value_classification='{classification}',locator_kind={physicalValues.Split(',')[0]},canonical_locator={physicalValues.Split(',')[1]},locator_sha256={physicalValues.Split(',')[2]},display_owner={physicalValues.Split(',')[3]},display_repository={physicalValues.Split(',')[4]};
            UPDATE session_repository_observation_contexts
            SET admission_state='{state}',repository_id={contextValues.Split(',')[0]},locator_id={contextValues.Split(',')[1]};
            """);

        Assert.Throws<InvalidOperationException>(() => LocalRepositoryCatalogValidation.Validate(connection, null));
    }

    [Fact]
    public void ManagedValidation_AcceptsOnlyTheResourceScopedShadowedExceptionWithoutARepositoryClaim()
    {
        using var database = new TestDatabase();
        using var connection = CreateCompleteCatalog(database.Path);
        Execute(connection, "DROP TRIGGER session_repository_observations_update_rejected; DROP TRIGGER session_repository_observation_contexts_update_rejected; DROP TRIGGER local_repository_history_delete_rejected; DELETE FROM local_repository_history;");
        var sourceIdentity = LocalRepositoryIdentityHashing.SourceIdentity(
            LocalRepositorySourceIdentityInput.Resource(1, 0, 0, "vcs.repository.url.full"));
        var contextIdentity = LocalRepositoryIdentityHashing.ContextIdentity(new(
            sourceIdentity,
            "01900000-0000-7000-8000-000000000020",
            "01900000-0000-7000-8000-000000000021",
            "11111111111111111111111111111111",
            "2222222222222222"));
        Execute(connection, $"""
            UPDATE session_repository_observations
            SET source_identity_sha256='{sourceIdentity}',scope_span_ordinal=NULL,span_ordinal=NULL,scope_kind='resource';
            UPDATE session_repository_observation_contexts
            SET context_identity_sha256='{contextIdentity}',admission_state='shadowed',repository_id=NULL,locator_id=NULL;
            """);

        LocalRepositoryCatalogValidation.ValidateRows(connection, null);
    }

    [Theory]
    [InlineData("source_context_neither")]
    [InlineData("source_context_both")]
    [InlineData("source_context_operation_only")]
    [InlineData("user_operation_neither")]
    [InlineData("user_operation_both")]
    [InlineData("user_operation_context_only")]
    public void RepositoryHistorySchema_RejectsBothNeitherAndWrongCauseArm(string attack)
    {
        using var database = new TestDatabase();
        using var connection = CreateCompleteCatalog(database.Path);
        const string repositoryId = "01900000-0000-7000-8000-000000000090";
        const string locatorId = "01900000-0000-7000-8000-000000000091";
        Assert.True(GitHubRepositoryLocatorParser.TryParse("https://github.com/other/repository", out var locator));
        Execute(connection, $"""
            INSERT INTO local_repositories VALUES('{repositoryId}','Other',1,'{At}','{At}');
            INSERT INTO local_repository_locators VALUES('{locatorId}','{repositoryId}','github_repository','{locator!.CanonicalLocator}','{locator.LocatorSha256}','manual','{locator.DisplayOwner}','{locator.DisplayRepository}','{At}');
            """);
        var values = attack switch
        {
            "source_context_neither" => ("create_observed", 0, 1, $"'{locatorId}'", "source_context", "NULL", "NULL"),
            "source_context_both" => ("create_observed", 0, 1, $"'{locatorId}'", "source_context", "'lrc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'", "(SELECT context_identity_sha256 FROM session_repository_observation_contexts)"),
            "source_context_operation_only" => ("create_observed", 0, 1, $"'{locatorId}'", "source_context", "'lrc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'", "NULL"),
            "user_operation_neither" => ("rename", 1, 2, "NULL", "user_operation", "NULL", "NULL"),
            "user_operation_both" => ("rename", 1, 2, "NULL", "user_operation", "'lrc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'", "(SELECT context_identity_sha256 FROM session_repository_observation_contexts)"),
            "user_operation_context_only" => ("rename", 1, 2, "NULL", "user_operation", "NULL", "(SELECT context_identity_sha256 FROM session_repository_observation_contexts)"),
            _ => throw new ArgumentOutOfRangeException(nameof(attack)),
        };

        Assert.Throws<SqliteException>(() => Execute(connection, $"""
            INSERT INTO local_repository_history VALUES('01900000-0000-7000-8000-000000000092','{repositoryId}','{values.Item1}',{values.Item2},{values.Item3},{values.Item4},'{values.Item5}',{values.Item6},{values.Item7},'{At}');
            """));
    }

    [Theory]
    [InlineData("source_reconciliation_neither")]
    [InlineData("source_reconciliation_both")]
    [InlineData("source_reconciliation_operation_only")]
    [InlineData("user_operation_neither")]
    [InlineData("user_operation_both")]
    [InlineData("user_operation_reconciliation_only")]
    public void AssignmentHistorySchema_RejectsBothNeitherAndWrongCauseArm(string attack)
    {
        using var database = new TestDatabase();
        using var connection = CreateCompleteCatalog(database.Path);
        var values = attack switch
        {
            "source_reconciliation_neither" => ("automatic_reconcile", "source_reconciliation", "NULL", "NULL"),
            "source_reconciliation_both" => ("automatic_reconcile", "source_reconciliation", "'lrc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'", "(SELECT reconciliation_fingerprint FROM local_repository_reconciliation_queue)"),
            "source_reconciliation_operation_only" => ("automatic_reconcile", "source_reconciliation", "'lrc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'", "NULL"),
            "user_operation_neither" => ("assign", "user_operation", "NULL", "NULL"),
            "user_operation_both" => ("assign", "user_operation", "'lrc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'", "(SELECT reconciliation_fingerprint FROM local_repository_reconciliation_queue)"),
            "user_operation_reconciliation_only" => ("assign", "user_operation", "NULL", "(SELECT reconciliation_fingerprint FROM local_repository_reconciliation_queue)"),
            _ => throw new ArgumentOutOfRangeException(nameof(attack)),
        };

        Assert.Throws<SqliteException>(() => Execute(connection, $"""
            INSERT INTO session_repository_assignment_history VALUES('01900000-0000-7000-8000-000000000052','01900000-0000-7000-8000-000000000020','{values.Item1}',1,2,
                '{new string('a', 64)}','{new string('a', 64)}','unassigned','unassigned','none','none',NULL,NULL,'{values.Item2}',{values.Item3},{values.Item4},'{At}');
            """));
    }

    [Theory]
    [InlineData("source_context")]
    [InlineData("user_operation")]
    public void RepositoryHistorySchema_RejectsWrongActionWithAnOtherwiseValidExclusiveCauseArm(string causeKind)
    {
        using var database = new TestDatabase();
        using var connection = CreateCompleteCatalog(database.Path);
        const string repositoryId = "01900000-0000-7000-8000-000000000090";
        const string locatorId = "01900000-0000-7000-8000-000000000091";
        Assert.True(GitHubRepositoryLocatorParser.TryParse("https://github.com/other/repository", out var locator));
        Execute(connection, $"""
            INSERT INTO local_repositories VALUES('{repositoryId}','Other',1,'{At}','{At}');
            INSERT INTO local_repository_locators VALUES('{locatorId}','{repositoryId}','github_repository','{locator!.CanonicalLocator}','{locator.LocatorSha256}','manual','{locator.DisplayOwner}','{locator.DisplayRepository}','{At}');
            """);
        var values = causeKind == "source_context"
            ? ("rename", 1, 2, "NULL", "NULL", "(SELECT context_identity_sha256 FROM session_repository_observation_contexts)")
            : ("create_observed", 0, 1, $"'{locatorId}'", "'lrc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'", "NULL");

        Assert.Throws<SqliteException>(() => Execute(connection, $"""
            INSERT INTO local_repository_history VALUES('01900000-0000-7000-8000-000000000092','{repositoryId}','{values.Item1}',{values.Item2},{values.Item3},{values.Item4},'{causeKind}',{values.Item5},{values.Item6},'{At}');
            """));
    }

    [Theory]
    [InlineData("source_reconciliation")]
    [InlineData("user_operation")]
    public void AssignmentHistorySchema_RejectsWrongActionWithAnOtherwiseValidExclusiveCauseArm(string causeKind)
    {
        using var database = new TestDatabase();
        using var connection = CreateCompleteCatalog(database.Path);
        var values = causeKind == "source_reconciliation"
            ? ("assign", "NULL", "(SELECT reconciliation_fingerprint FROM local_repository_reconciliation_queue)")
            : ("automatic_reconcile", "'lrc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'", "NULL");

        Assert.Throws<SqliteException>(() => Execute(connection, $"""
            INSERT INTO session_repository_assignment_history VALUES('01900000-0000-7000-8000-000000000052','01900000-0000-7000-8000-000000000020','{values.Item1}',1,2,
                '{new string('a', 64)}','{new string('a', 64)}','unassigned','unassigned','none','none',NULL,NULL,'{causeKind}',{values.Item2},{values.Item3},'{At}');
            """));
    }

    [Fact]
    public void ManagedValidation_RejectsSyntacticallyValidCauseValuesThatDoNotReferenceExactTargets()
    {
        using var database = new TestDatabase();
        using var connection = CreateCompleteCatalog(database.Path);
        Execute(connection, $"""
            INSERT INTO local_repositories VALUES('01900000-0000-7000-8000-000000000093','Other',1,'{At}','{At}');
            INSERT INTO local_repository_history VALUES('01900000-0000-7000-8000-000000000094','01900000-0000-7000-8000-000000000093','create_observed',0,1,'01900000-0000-7000-8000-000000000010','source_context',NULL,'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','{At}');
            INSERT INTO session_repository_assignment_history VALUES('01900000-0000-7000-8000-000000000095','01900000-0000-7000-8000-000000000020','automatic_reconcile',1,2,'{new string('a', 64)}','{new string('a', 64)}','unassigned','unassigned','none','none',NULL,NULL,'source_reconciliation',NULL,'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','{At}');
            """);

        Assert.Throws<InvalidOperationException>(() => LocalRepositoryCatalogValidation.Validate(connection, null));
    }

    [Theory]
    [InlineData("repository_context_missing")]
    [InlineData("repository_receipt_missing")]
    [InlineData("assignment_queue_missing")]
    [InlineData("assignment_receipt_missing")]
    public void ManagedValidation_RejectsEveryAbsentCauseUnionTarget(string attack)
    {
        using var database = new TestDatabase();
        using var connection = CreateCompleteCatalog(database.Path);
        const string absentOperationKey = "lrc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAE";
        Assert.True(LocalRepositoryCatalogValidation.IsOperationKey(absentOperationKey));
        Assert.True(GitHubRepositoryLocatorParser.TryParse("https://github.com/other/repository", out var otherLocator));
        var sql = attack switch
        {
            "repository_context_missing" => $"""
                INSERT INTO local_repositories VALUES('01900000-0000-7000-8000-000000000096','Other',1,'{At}','{At}');
                INSERT INTO local_repository_locators VALUES('01900000-0000-7000-8000-000000000097','01900000-0000-7000-8000-000000000096','github_repository','{otherLocator!.CanonicalLocator}','{otherLocator.LocatorSha256}','manual','{otherLocator.DisplayOwner}','{otherLocator.DisplayRepository}','{At}');
                INSERT INTO local_repository_history VALUES('01900000-0000-7000-8000-000000000098','01900000-0000-7000-8000-000000000096','create_observed',0,1,'01900000-0000-7000-8000-000000000097','source_context',NULL,(SELECT context_identity_sha256 FROM session_repository_observation_contexts),'{At}');
                """,
            "repository_receipt_missing" => $"""
                INSERT INTO local_repository_history VALUES('01900000-0000-7000-8000-000000000098','01900000-0000-7000-8000-000000000001','rename',1,2,NULL,'user_operation','{absentOperationKey}',NULL,'{At}');
                """,
            "assignment_queue_missing" => $"""
                INSERT INTO session_repository_assignment_history VALUES('01900000-0000-7000-8000-000000000099','01900000-0000-7000-8000-000000000020','automatic_reconcile',1,2,'{new string('a', 64)}','{new string('a', 64)}','unassigned','unassigned','none','none',NULL,NULL,'source_reconciliation',NULL,'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','{At}');
                """,
            "assignment_receipt_missing" => $"""
                INSERT INTO session_repository_assignment_history VALUES('01900000-0000-7000-8000-000000000100','01900000-0000-7000-8000-000000000020','assign',1,2,'{new string('a', 64)}','{new string('a', 64)}','unassigned','unassigned','none','none',NULL,NULL,'user_operation','{absentOperationKey}',NULL,'{At}');
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(attack)),
        };
        Execute(connection, sql);

        Assert.Throws<InvalidOperationException>(() => LocalRepositoryCatalogValidation.Validate(connection, null));
    }

    [Fact]
    public void HistorySchema_RequiresIntegerStorageForNewRevision()
    {
        using var database = new TestDatabase();
        new SqliteSessionStore(database.Path).CreateSchema();
        using var connection = Open(database.Path);
        LocalRepositoryCatalogSchemaV1.Ensure(connection);

        Assert.Contains("new_revision INTEGER NOT NULL CHECK(typeof(new_revision)='integer'", ScalarText(connection,
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='local_repository_history';"), StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalObservationSchema_RejectsAnUnapprovedAttributeKey()
    {
        using var database = new TestDatabase();
        using var connection = CreateCompleteCatalog(database.Path);
        var sourceIdentity = LocalRepositoryIdentityHashing.SourceIdentity(
            LocalRepositorySourceIdentityInput.Span(2, 0, 0, 0, 0, "unapproved.repository.key"));

        Assert.Throws<SqliteException>(() => Execute(connection, $"""
            INSERT INTO session_repository_observations VALUES(
                '01900000-0000-7000-8000-000000000080','{sourceIdentity}',2,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',0,0,0,0,
                'span','unapproved.repository.key','invalid_type',NULL,NULL,NULL,NULL,NULL,'github-copilot-vscode','1.2.3','{At}');
            """));
    }

    [Fact]
    public void Validator_RejectsAParsedObservationWhoseCanonicalLocatorFieldsDoNotAgree()
    {
        using var database = new TestDatabase();
        using var connection = CreateCompleteCatalog(database.Path);
        var sourceIdentity = LocalRepositoryIdentityHashing.SourceIdentity(
            LocalRepositorySourceIdentityInput.Span(3, 0, 0, 0, 0, "vcs.repository.url.full"));
        Execute(connection, $"""
            INSERT INTO session_repository_observations VALUES(
                '01900000-0000-7000-8000-000000000081','{sourceIdentity}',3,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',0,0,0,0,
                'span','vcs.repository.url.full','admitted','github_repository','github.com/Example/Repository','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','Example','Repository','github-copilot-vscode','1.2.3','{At}');
            """);

        Assert.Throws<InvalidOperationException>(() => LocalRepositoryCatalogValidation.Validate(connection, null));
    }

    [Fact]
    public void Validator_RejectsSourceContextHistoryWhoseRepositoryLocatorAndContextDoNotAgree()
    {
        using var database = new TestDatabase();
        using var connection = CreateCompleteCatalog(database.Path);
        Execute(connection, $"""
            INSERT INTO local_repositories VALUES('01900000-0000-7000-8000-000000000090','Other',1,'{At}','{At}');
            INSERT INTO local_repository_history VALUES(
                '01900000-0000-7000-8000-000000000091','01900000-0000-7000-8000-000000000090','create_observed',0,1,
                '01900000-0000-7000-8000-000000000010','source_context',NULL,
                (SELECT context_identity_sha256 FROM session_repository_observation_contexts),'2026-08-01T00:00:00.0000000+00:00');
            """);

        Assert.Throws<InvalidOperationException>(() => LocalRepositoryCatalogValidation.Validate(connection, null));
    }

    [Theory]
    [InlineData("e\u0301", false)]
    [InlineData("name\u0085", false)]
    [InlineData("name\u001f", false)]
    [InlineData("\ufffd", true)]
    [InlineData("a\U0001f642", true)]
    public void DisplayNameValidation_RejectsUnsafeUnicodeWhileKeepingSupplementaryScalars(string value, bool expected) =>
        Assert.Equal(expected, LocalRepositoryCatalogValidation.IsDisplayName(value));

    [Fact]
    public void DisplayNameValidation_RejectsAnExplicitUnpairedSurrogate() =>
        Assert.False(LocalRepositoryCatalogValidation.IsDisplayName(new string((char)0xd800, 1)));

    private static readonly ImmutableAttack[] ImmutableAttacks =
    [
        new("local_repository_locators", "created_at", "'2026-08-02T00:00:00.0000000+00:00'",
            "INSERT OR REPLACE INTO local_repository_locators SELECT locator_id,repository_id,kind,canonical_locator,locator_sha256,source,display_owner,display_repository,'2026-08-02T00:00:00.0000000+00:00' FROM local_repository_locators;",
            "INSERT OR REPLACE INTO local_repository_locators SELECT '01900000-0000-7000-8000-000000000002',repository_id,kind,canonical_locator,locator_sha256,source,display_owner,display_repository,'2026-08-02T00:00:00.0000000+00:00' FROM local_repository_locators;",
            "INSERT OR REPLACE INTO local_repository_locators SELECT locator_id,repository_id,kind,canonical_locator,locator_sha256,source,display_owner,display_repository,'2026-08-02T00:00:00.0000000+00:00' FROM local_repository_locators;"),
        new("session_repository_observations", "observed_at", "'2026-08-02T00:00:00.0000000+00:00'",
            "INSERT OR REPLACE INTO session_repository_observations SELECT observation_id,source_identity_sha256,raw_record_id,raw_payload_sha256,resource_span_ordinal,scope_span_ordinal,span_ordinal,attribute_ordinal,scope_kind,attribute_key,value_classification,locator_kind,canonical_locator,locator_sha256,display_owner,display_repository,source_surface,source_application_version,'2026-08-02T00:00:00.0000000+00:00' FROM session_repository_observations;",
            "INSERT OR REPLACE INTO session_repository_observations SELECT '01900000-0000-7000-8000-000000000003',source_identity_sha256,raw_record_id,raw_payload_sha256,resource_span_ordinal,scope_span_ordinal,span_ordinal,attribute_ordinal,scope_kind,attribute_key,value_classification,locator_kind,canonical_locator,locator_sha256,display_owner,display_repository,source_surface,source_application_version,'2026-08-02T00:00:00.0000000+00:00' FROM session_repository_observations;"),
        new("session_repository_observation_contexts", "observed_at", "'2026-08-02T00:00:00.0000000+00:00'",
            "INSERT OR REPLACE INTO session_repository_observation_contexts SELECT context_id,observation_id,context_identity_sha256,session_event_id,session_id,trace_id,span_id,admission_state,repository_id,locator_id,'2026-08-02T00:00:00.0000000+00:00' FROM session_repository_observation_contexts;",
            "INSERT OR REPLACE INTO session_repository_observation_contexts SELECT '01900000-0000-7000-8000-000000000004',observation_id,context_identity_sha256,session_event_id,session_id,trace_id,span_id,admission_state,repository_id,locator_id,'2026-08-02T00:00:00.0000000+00:00' FROM session_repository_observation_contexts;",
            "INSERT OR REPLACE INTO session_repository_observation_contexts SELECT '01900000-0000-7000-8000-000000000014',observation_id,'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',session_event_id,session_id,trace_id,span_id,admission_state,repository_id,locator_id,'2026-08-02T00:00:00.0000000+00:00' FROM session_repository_observation_contexts;",
            "INSERT OR REPLACE INTO session_repository_observation_contexts SELECT context_id,observation_id,context_identity_sha256,session_event_id,session_id,trace_id,span_id,admission_state,repository_id,locator_id,'2026-08-02T00:00:00.0000000+00:00' FROM session_repository_observation_contexts;"),
        new("session_repository_assignment_history", "occurred_at", "'2026-08-02T00:00:00.0000000+00:00'",
            "INSERT OR REPLACE INTO session_repository_assignment_history SELECT history_id,session_id,action,previous_revision,new_revision,previous_assignment_state_sha256,new_assignment_state_sha256,previous_state,new_state,previous_authority,new_authority,previous_repository_id,new_repository_id,cause_kind,operation_key,reconciliation_fingerprint,'2026-08-02T00:00:00.0000000+00:00' FROM session_repository_assignment_history;",
            "INSERT OR REPLACE INTO session_repository_assignment_history SELECT '01900000-0000-7000-8000-000000000005',session_id,action,previous_revision,new_revision,previous_assignment_state_sha256,new_assignment_state_sha256,previous_state,new_state,previous_authority,new_authority,previous_repository_id,new_repository_id,cause_kind,operation_key,reconciliation_fingerprint,'2026-08-02T00:00:00.0000000+00:00' FROM session_repository_assignment_history;"),
        new("local_repository_history", "occurred_at", "'2026-08-02T00:00:00.0000000+00:00'",
            "INSERT OR REPLACE INTO local_repository_history SELECT history_id,repository_id,action,previous_revision,new_revision,locator_id,cause_kind,operation_key,context_identity_sha256,'2026-08-02T00:00:00.0000000+00:00' FROM local_repository_history;",
            "INSERT OR REPLACE INTO local_repository_history SELECT '01900000-0000-7000-8000-000000000006',repository_id,action,previous_revision,new_revision,locator_id,cause_kind,operation_key,context_identity_sha256,'2026-08-02T00:00:00.0000000+00:00' FROM local_repository_history;"),
        new("local_repository_operation_receipts", "created_at", "'2026-08-02T00:00:00.0000000+00:00'",
            "INSERT OR REPLACE INTO local_repository_operation_receipts SELECT operation_key,request_fingerprint,status_code,content_type,cache_control,response_entity,'2026-08-02T00:00:00.0000000+00:00' FROM local_repository_operation_receipts;"),
    ];

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys=ON;");
        return connection;
    }

    private static SqliteConnection CreateCompleteCatalog(string path, string locatorText = "https://github.com/example/repository")
    {
        new SqliteSessionStore(path).CreateSchema();
        SqliteConnection.ClearAllPools();
        var connection = Open(path);
        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        const string repositoryId = "01900000-0000-7000-8000-000000000001";
        const string locatorId = "01900000-0000-7000-8000-000000000010";
        const string sessionId = "01900000-0000-7000-8000-000000000020";
        const string eventId = "01900000-0000-7000-8000-000000000021";
        const string observationId = "01900000-0000-7000-8000-000000000030";
        const string contextId = "01900000-0000-7000-8000-000000000040";
        const string operationKey = "lrc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const string digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        Assert.True(GitHubRepositoryLocatorParser.TryParse(locatorText, out var locator));
        var sourceIdentity = LocalRepositoryIdentityHashing.SourceIdentity(
            LocalRepositorySourceIdentityInput.Span(1, 0, 0, 0, 0, "vcs.repository.url.full"));
        var contextIdentity = LocalRepositoryIdentityHashing.ContextIdentity(
            new(sourceIdentity, sessionId, eventId, "11111111111111111111111111111111", "2222222222222222"));
        var reconciliationFingerprint = LocalRepositoryIdentityHashing.ReconciliationFingerprint(
            LocalRepositoryReconciliationEvidence.PayloadSha256(1, digest));
        Execute(connection, $"""
            INSERT INTO sessions(session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at)
            VALUES('{sessionId}','completed','full','{At}','not_captured','{At}','{At}');
            INSERT INTO session_runs(run_id,session_id,source_surface,status)
            VALUES('01900000-0000-7000-8000-000000000022','{sessionId}','vscode','completed');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version)
            VALUES('{eventId}','{sessionId}','01900000-0000-7000-8000-000000000022','vscode','otel-exact','synthetic','otel.span','{At}','not_captured','1.2.3');
            INSERT INTO local_repositories VALUES('{repositoryId}','Synthetic',1,'{At}','{At}');
            INSERT INTO local_repository_locators VALUES('{locatorId}','{repositoryId}','github_repository','{locator!.CanonicalLocator}','{locator.LocatorSha256}','manual','{locator.DisplayOwner}','{locator.DisplayRepository}','{At}');
            INSERT INTO local_repository_locator_heads VALUES('{repositoryId}','github_repository','{locatorId}','{At}');
            INSERT INTO session_repository_observations VALUES('{observationId}','{sourceIdentity}',1,'{digest}',0,0,0,0,'span','vcs.repository.url.full','admitted','github_repository','{locator.CanonicalLocator}','{locator.LocatorSha256}','{locator.DisplayOwner}','{locator.DisplayRepository}','github-copilot-vscode','1.2.3','{At}');
            INSERT INTO session_repository_observation_contexts VALUES('{contextId}','{observationId}','{contextIdentity}','{eventId}','{sessionId}','11111111111111111111111111111111','2222222222222222','admitted','{repositoryId}','{locatorId}','{At}');
            INSERT INTO local_repository_operation_receipts VALUES('{operationKey}','{digest}',200,'application/json; charset=utf-8','no-store',X'7B7D','{At}');
            INSERT INTO session_repository_assignment_history VALUES('01900000-0000-7000-8000-000000000050','{sessionId}','assign',0,1,'{digest}','{digest}','unassigned','unassigned','none','none',NULL,NULL,'user_operation','{operationKey}',NULL,'{At}');
            INSERT INTO local_repository_history VALUES('01900000-0000-7000-8000-000000000060','{repositoryId}','create_observed',0,1,'{locatorId}','source_context',NULL,'{contextIdentity}','{At}');
            INSERT INTO local_repository_reconciliation_queue VALUES('01900000-0000-7000-8000-000000000070',1,'payload_sha256','{digest}','local-repository-catalog:1','{reconciliationFingerprint}','pending',0,NULL,NULL,NULL,'{At}','{At}');
            """);
        return connection;
    }

    private static string Snapshot(SqliteConnection connection, string table)
    {
        using var columns = connection.CreateCommand();
        columns.CommandText = $"PRAGMA table_info(\"{table}\");";
        using var reader = columns.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(1));
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(" || char(31) || ", names.Select(static name => $"quote(\"{name}\")"))} FROM \"{table}\";";
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ScalarText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private sealed record ImmutableAttack(string Table, string MutableColumn, string MutableValue, params string[] Replacements);

    private sealed class TestDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"local-repository-catalog-{Guid.NewGuid():N}");

        public TestDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "monitor.sqlite");
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(25);
                    SqliteConnection.ClearAllPools();
                }
            }
        }
    }
}
