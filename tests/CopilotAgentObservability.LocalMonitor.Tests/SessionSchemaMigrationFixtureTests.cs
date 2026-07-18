using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionSchemaMigrationFixtureTests
{
    private const int CurrentSessionSchemaVersion = 12;
    private const int MatchKindSchemaVersion = 12;
    private const string VersionTenUpgraderCommit = "cf2b15f6c9b18a68aea8dc22f48fcb3177a81346";
    private const string GenerationCommand = "dotnet run --project scripts/test/GenerateSessionSchemaFixtures/GenerateSessionSchemaFixtures.csproj -- --output tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/session";
    private const string VersionFourLimitation = "Commit 601c2beb5cb528d1e87aba0fef150b65e1dbccc0 exposes no public proposal-apply persistence API; parameterized INSERTs populate proposal-apply rows only after its public CreateSchema, Write, and CreateImprovementProposal APIs create the schema and parent sentinels.";

    private static readonly IReadOnlyDictionary<int, string> ExpectedV11SemanticSchemaHashes = new Dictionary<int, string>
    {
        [1] = "0966d9e4d84537343ccb9706a6e3d3e101d16612431d2edb4dfe3fe882555ea4",
        [2] = "0966d9e4d84537343ccb9706a6e3d3e101d16612431d2edb4dfe3fe882555ea4",
        [3] = "e95d3f39ba76fe302b1a7c8e2f899dd7dd6c25d6fcd141bf59496e7b153fddfe",
        [4] = "9690ba6a62b6c606a040f3356be104339f238f3c8dc5e324df9e99ecedfcc1e3",
        [5] = "6b1b8a7b283d16a488ddb82012796ff9c7a595f0f3d404633fb29970a8e1a3b9",
        [6] = "6b1b8a7b283d16a488ddb82012796ff9c7a595f0f3d404633fb29970a8e1a3b9",
        [7] = "0966d9e4d84537343ccb9706a6e3d3e101d16612431d2edb4dfe3fe882555ea4",
        [8] = "0966d9e4d84537343ccb9706a6e3d3e101d16612431d2edb4dfe3fe882555ea4",
        [9] = "0966d9e4d84537343ccb9706a6e3d3e101d16612431d2edb4dfe3fe882555ea4",
        [10] = "0966d9e4d84537343ccb9706a6e3d3e101d16612431d2edb4dfe3fe882555ea4",
    };

    private static readonly string[] ExpectedV11Tables =
    [
        "effect_comparison_evidence", "effect_comparison_sessions", "effect_comparisons", "effect_receipts",
        "improvement_proposal_evidence", "improvement_proposal_sessions", "improvement_proposals",
        "objective_evaluation_evidence", "objective_evaluations", "proposal_applies", "proposal_apply_audit",
        "proposal_apply_drafts", "proposal_apply_files", "proposal_apply_hunks", "proposal_apply_pending",
        "proposal_apply_revisions", "schema_version", "session_event_content", "session_events", "session_human_evaluation",
        "session_native_ids", "session_projection_state", "session_runs", "sessions",
    ];

    public static TheoryData<string, int, string, int?> HistoricalSchemas => new()
    {
        { "session-v1.sqlite", 1, "ab02e362f05798537e56c50a3048e0fbe3b9bf5a", null },
        { "session-v2.sqlite", 2, "b5e02e0f36705eb54881c288b83f875753e11de1", null },
        { "session-v3.sqlite", 3, "8d765ad07a46556b84ca32213e86fae28d5998b1", null },
        { "session-v4.sqlite", 4, "601c2beb5cb528d1e87aba0fef150b65e1dbccc0", null },
        { "session-v5.sqlite", 5, "30d5c8600d0d2abedecdb81944797d7213ef14c9", null },
        { "session-v6.sqlite", 6, "6048da1a50473fdf8701fdb2b787b5e565fec82a", null },
        { "session-v7.sqlite", 7, "5a28b87c05c81acecd9121ecf68f5afa2e82deae", null },
        { "session-v8.sqlite", 8, "87f4a000932481ac6240b5ec1240318c319efdb5", null },
        { "session-v9.sqlite", 9, "e55e2dfb0e306963065759716474385d337b17f6", null },
        { "session-v10.sqlite", 10, VersionTenUpgraderCommit, null },
        { "session-v10-from-v4.sqlite", 10, "601c2beb5cb528d1e87aba0fef150b65e1dbccc0", 4 },
        { "session-v10-from-v5.sqlite", 10, "30d5c8600d0d2abedecdb81944797d7213ef14c9", 5 },
        { "session-v10-from-v6.sqlite", 10, "6048da1a50473fdf8701fdb2b787b5e565fec82a", 6 },
    };

    [Theory]
    [MemberData(nameof(HistoricalSchemas))]
    public void Historical_fixture_has_reproducible_provenance_and_preserves_complete_v11_state_after_restart(
        string fixtureFile,
        int version,
        string sourceCommit,
        int? sourceVersion)
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "session");
        var manifestPath = Path.Combine(fixtureDirectory, "manifest.json");
        Assert.True(File.Exists(manifestPath), $"Missing migration fixture manifest: {manifestPath}");

        var manifest = JsonSerializer.Deserialize<FixtureManifest>(File.ReadAllText(manifestPath), JsonOptions);
        Assert.NotNull(manifest);
        Assert.Equal("session", manifest.Component);
        Assert.Equal(GenerationCommand, manifest.GenerationCommand);
        Assert.Equal("git status --porcelain", manifest.GitStatusCommand);
        Assert.Equal(13, manifest.Fixtures.Count);
        var manifestIdentities = manifest.Fixtures.Select(fixture => (fixture.Version, fixture.File)).ToArray();
        AssertReviewedManifestIdentities(manifestIdentities);
        Assert.Throws<Xunit.Sdk.EqualException>(() => AssertReviewedManifestIdentities(
            manifestIdentities.Append((10, "session-v10-pseudo.sqlite")).ToArray()));
        var fixture = Assert.Single(manifest.Fixtures, candidate => candidate.File == fixtureFile);
        Assert.Equal(version, fixture.Version);
        Assert.Equal(sourceCommit, fixture.SourceCommit);
        Assert.Equal(fixtureFile, fixture.File);
        Assert.Equal(string.Empty, fixture.GitStatusBefore);
        Assert.Equal(string.Empty, fixture.GitStatusAfter);
        var contentVersion = sourceVersion ?? version;
        var expectedLimitations = sourceVersion is not null
            ? Array.Empty<string>()
            : version switch
        {
            4 => [VersionFourLimitation],
            >= 9 => [$"Commit {sourceCommit} exposes RecordEffectComparison but no public comparison-ID input; after that public API persists the complete comparison graph, parameterized UPDATEs replace only its generated opaque comparison ID with the deterministic fixture sentinel so SHA-256 reproduction remains exact."],
            _ => Array.Empty<string>(),
        };
        Assert.Equal(expectedLimitations, fixture.Limitations);
        if (sourceVersion is null)
        {
            Assert.Null(fixture.Lineage);
        }
        else
        {
            var sourceFixture = Assert.Single(manifest.Fixtures, candidate => candidate.File == $"session-v{sourceVersion}.sqlite");
            var lineage = Assert.IsType<LineageProvenance>(fixture.Lineage);
            Assert.Equal(sourceVersion, lineage.SourceVersion);
            Assert.Equal(sourceFixture.File, lineage.SourceFixture);
            Assert.Equal(sourceFixture.Sha256, lineage.SourceFixtureSha256);
            Assert.Equal(VersionTenUpgraderCommit, lineage.UpgraderCommit);
            Assert.Equal(new[]
            {
                $"git worktree add --detach {{upgraderWorktree}} {VersionTenUpgraderCommit}",
                "dotnet build {upgraderWorktree}/src/CopilotAgentObservability.Persistence.Sqlite/CopilotAgentObservability.Persistence.Sqlite.csproj --configuration Release --artifacts-path {upgraderArtifacts} --nologo",
                $"dotnet {{generatorAssembly}} --upgrade-one {{upgraderAssembly}} {sourceFixture.File} {fixture.File}",
            }, lineage.Commands);
        }

        var fixturePath = Path.Combine(fixtureDirectory, fixture.File);
        Assert.True(File.Exists(fixturePath), $"Missing migration fixture: {fixturePath}");
        Assert.Equal(fixture.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath))).ToLowerInvariant());

        using (var historical = OpenReadOnly(fixturePath))
        {
            Assert.Equal(version, Scalar<long>(historical, "SELECT version FROM schema_version WHERE component='session';"));
            AssertHistoricalSentinels(historical, contentVersion, fixture.Sentinels);
            if (sourceVersion is not null) AssertStampedVersionTenLineage(historical, sourceVersion.Value);
        }

        var migratedPath = Path.Combine(Path.GetTempPath(), $"session-migration-{Guid.NewGuid():N}.sqlite");
        File.Copy(fixturePath, migratedPath);
        try
        {
            new SqliteSessionStore(migratedPath).CreateSchema();
            MigratedSnapshot firstPass;
            using (var reopened = Open(migratedPath))
            {
                firstPass = AssertCompleteMigratedState(reopened, contentVersion, fixture.Sentinels);
                AssertDatabaseIntegrity(reopened);
            }

            new SqliteSessionStore(migratedPath).CreateSchema();
            MigratedSnapshot restartPass;
            using (var reopenedAgain = Open(migratedPath))
            {
                restartPass = AssertCompleteMigratedState(reopenedAgain, contentVersion, fixture.Sentinels);
                AssertDatabaseIntegrity(reopenedAgain);
            }
            Assert.Equal(firstPass.SemanticSchema, restartPass.SemanticSchema);
            Assert.Equal(firstPass.Rows, restartPass.Rows);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(migratedPath);
        }
    }

    [Fact]
    public void Prior_version_fixture_migrates_match_kind_with_legacy_null_and_is_idempotent()
    {
        var databasePath = CopyFixture("session-v10.sqlite");
        string[] before;
        try
        {
            using (var connection = Open(databasePath))
            {
                before =
                [
                    Scalar<string>(connection, "SELECT session_id FROM sessions;"),
                    Scalar<string>(connection, "SELECT native_session_id FROM session_native_ids;"),
                    Scalar<string>(connection, "SELECT event_id FROM session_events;"),
                    Scalar<string>(connection, "SELECT source_event_id FROM session_events;"),
                ];
            }

            new SqliteSessionStore(databasePath).CreateSchema();
            string[] firstPass;
            using (var connection = Open(databasePath))
            {
                Assert.Equal(MatchKindSchemaVersion, Scalar<long>(connection, "SELECT version FROM schema_version WHERE component='session';"));
                Assert.True(ColumnExists(connection, "session_events", "match_kind"));
                Assert.Null(ScalarOrNull<string>(connection, "SELECT match_kind FROM session_events;", string.Empty));
                firstPass =
                [
                    Scalar<string>(connection, "SELECT session_id FROM sessions;"),
                    Scalar<string>(connection, "SELECT native_session_id FROM session_native_ids;"),
                    Scalar<string>(connection, "SELECT event_id FROM session_events;"),
                    Scalar<string>(connection, "SELECT source_event_id FROM session_events;"),
                ];
            }

            new SqliteSessionStore(databasePath).CreateSchema();
            using (var connection = Open(databasePath))
            {
                Assert.Equal(MatchKindSchemaVersion, Scalar<long>(connection, "SELECT version FROM schema_version WHERE component='session';"));
                Assert.Null(ScalarOrNull<string>(connection, "SELECT match_kind FROM session_events;", string.Empty));
                Assert.Equal(firstPass, ReadSessionEventIdentity(connection));
            }

            Assert.Equal(before, firstPass);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Theory]
    [InlineData(12)]
    [InlineData(99)]
    public void Newer_version_real_fixture_is_rejected_without_schema_or_data_mutation(int newerVersion)
    {
        var migratedPath = CopyFixture("session-v10.sqlite");
        try
        {
            using (var injected = Open(migratedPath))
                Execute(injected, $"UPDATE schema_version SET version={newerVersion} WHERE component='session';");
            var before = CapturePreflightSnapshot(migratedPath);

            Assert.Throws<InvalidOperationException>(() => new SqliteSessionStore(migratedPath).CreateSchema());

            Assert.Equal(before, CapturePreflightSnapshot(migratedPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(migratedPath);
        }
    }

    [Theory]
    [InlineData("source_application_version")]
    [InlineData("adapter_version")]
    [InlineData("schema_fingerprint")]
    [InlineData("normalization_version")]
    public void Invalid_partial_v11_column_on_real_v10_fixture_rolls_back_version_schema_data_and_journal(string column)
    {
        var migratedPath = CopyFixture("session-v10.sqlite");
        try
        {
            using (var injected = Open(migratedPath))
                Execute(injected, $"ALTER TABLE session_events ADD COLUMN {column} INTEGER NOT NULL DEFAULT 0;");
            var before = CapturePreflightSnapshot(migratedPath);

            Assert.Throws<InvalidOperationException>(() => new SqliteSessionStore(migratedPath).CreateSchema());

            Assert.Equal(before, CapturePreflightSnapshot(migratedPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(migratedPath);
        }
    }

    [Fact]
    public void Invalid_extra_legacy_column_on_real_v10_fixture_is_rejected_without_mutation()
    {
        var migratedPath = CopyFixture("session-v10.sqlite");
        try
        {
            using (var injected = Open(migratedPath))
                Execute(injected, "ALTER TABLE session_events ADD COLUMN unexpected_legacy_column TEXT NULL;");
            var before = CapturePreflightSnapshot(migratedPath);

            Assert.Throws<InvalidOperationException>(() => new SqliteSessionStore(migratedPath).CreateSchema());

            Assert.Equal(before, CapturePreflightSnapshot(migratedPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(migratedPath);
        }
    }

    [Fact]
    public void Invalid_stamped_v11_shape_on_real_v10_fixture_is_rejected_without_mutation()
    {
        var migratedPath = CopyFixture("session-v10.sqlite");
        try
        {
            using (var injected = Open(migratedPath))
            {
                Execute(injected, """
                    ALTER TABLE session_events ADD COLUMN source_application_version TEXT NULL;
                    ALTER TABLE session_events ADD COLUMN adapter_version TEXT NULL;
                    ALTER TABLE session_events ADD COLUMN schema_fingerprint TEXT NULL;
                    ALTER TABLE session_events ADD COLUMN normalization_version TEXT NULL;
                    ALTER TABLE session_events ADD COLUMN unrelated_marker TEXT NULL CHECK (unrelated_marker IS NULL OR unrelated_marker <> 'claude-code');
                    ALTER TABLE session_runs ADD COLUMN unrelated_marker TEXT NULL CHECK (unrelated_marker IS NULL OR unrelated_marker <> 'claude-code');
                    ALTER TABLE session_native_ids ADD COLUMN unrelated_marker TEXT NULL CHECK (unrelated_marker IS NULL OR unrelated_marker <> 'claude-code');
                    UPDATE schema_version SET version=11 WHERE component='session';
                    """);
            }
            var before = CapturePreflightSnapshot(migratedPath);

            Assert.Throws<InvalidOperationException>(() => new SqliteSessionStore(migratedPath).CreateSchema());

            Assert.Equal(before, CapturePreflightSnapshot(migratedPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(migratedPath);
        }
    }

    [Fact]
    public void Foreign_key_invalid_stamped_v11_from_real_v10_fixture_is_rejected_without_mutation()
    {
        var migratedPath = CreateStampedVersionElevenFixture("session-v10.sqlite");
        try
        {
            using (var injected = Open(migratedPath))
            {
                Execute(injected, "PRAGMA foreign_keys=OFF; UPDATE session_events SET run_id='00000000-0000-7000-8000-000000000000';");
                AssertForeignKeyCheckHasRows(injected);
            }
            var before = CapturePreflightSnapshot(migratedPath);

            Assert.Throws<InvalidOperationException>(() => new SqliteSessionStore(migratedPath).CreateSchema());

            Assert.Equal(before, CapturePreflightSnapshot(migratedPath));
            using var verify = Open(migratedPath);
            AssertForeignKeyCheckHasRows(verify);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(migratedPath);
        }
    }

    private const int StampedVersionElevenMutationCount = 37;
    private const int SupportedWholeProfileFixtureCount = 5;
    private const int WholeProfileHybridCount = 2;

    public static TheoryData<string> StampedVersionElevenSemanticMutations => new()
    {
        "extra-regular-column",
        "missing-regular-column",
        "virtual-generated-column",
        "stored-generated-column",
        "declared-type-changed",
        "nullability-changed",
        "default-added",
        "primary-key-changed-to-unique",
        "column-order-changed",
        "check-removed",
        "check-broadened",
        "check-added",
        "check-removed-with-quoted-decoy",
        "check-removed-with-comment-decoy",
        "autoincrement-added",
        "ordinary-index-added",
        "unique-index-added",
        "expression-index-added",
        "autoindex-primary-key-order-changed",
        "index-collation-changed",
        "index-direction-changed",
        "index-key-and-auxiliary-terms-changed",
        "partial-index-added",
        "foreign-key-removed",
        "foreign-key-retargeted",
        "foreign-key-added",
        "foreign-key-action-changed",
        "trigger-on-owned-table-added",
        "owned-table-replaced-by-view",
        "reserved-table-added",
        "reserved-view-added",
        "strict-table-added",
        "without-rowid-added",
        "reserved-virtual-table-added",
        "schema-version-extra-column",
        "schema-version-component-primary-key-removed",
        "owned-table-removed",
    };

    public static TheoryData<string> SupportedWholeProfileFixtures => new()
    {
        "session-v10.sqlite",
        "session-v3.sqlite",
        "session-v4.sqlite",
        "session-v5.sqlite",
        "session-v6.sqlite",
    };

    public static TheoryData<string, string, string> WholeProfileHybrids => new()
    {
        { "session-v10.sqlite", "session-v3.sqlite", "improvement_proposals" },
        { "session-v3.sqlite", "session-v10.sqlite", "improvement_proposals" },
    };

    [Theory]
    [MemberData(nameof(StampedVersionElevenSemanticMutations))]
    public void Stamped_v11_semantic_mutation_is_rejected_before_initialization_without_mutation(string mutationName)
    {
        Assert.Equal(StampedVersionElevenMutationCount, StampedVersionElevenSemanticMutations.Count);
        var databasePath = CreateStampedVersionElevenFixture("session-v10.sqlite");
        try
        {
            ApplyStampedVersionElevenMutation(databasePath, mutationName);
            var before = CapturePreflightSnapshot(databasePath);

            Assert.Throws<InvalidOperationException>(() => new SqliteSessionStore(databasePath).CreateSchema());

            Assert.Equal(before, CapturePreflightSnapshot(databasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Theory]
    [MemberData(nameof(SupportedWholeProfileFixtures))]
    public void Exact_supported_whole_profile_from_real_fixture_is_accepted(string fixtureFile)
    {
        Assert.Equal(SupportedWholeProfileFixtureCount, SupportedWholeProfileFixtures.Count);
        var databasePath = CreateStampedVersionElevenFixture(fixtureFile);
        try
        {
            var before = CapturePreflightSnapshot(databasePath);

            new SqliteSessionStore(databasePath).CreateSchema();

            Assert.Equal(before, CapturePreflightSnapshot(databasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Theory]
    [MemberData(nameof(WholeProfileHybrids))]
    public void Per_table_hybrid_of_two_supported_whole_profiles_is_rejected(
        string targetFixture,
        string donorFixture,
        string table)
    {
        Assert.Equal(WholeProfileHybridCount, WholeProfileHybrids.Count);
        var databasePath = CreateStampedVersionElevenFixture(targetFixture);
        var donorPath = CreateStampedVersionElevenFixture(donorFixture);
        try
        {
            string donorSql;
            using (var donor = OpenReadOnly(donorPath))
                donorSql = Scalar<string>(donor, "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$id;", table);
            RewriteTableSql(databasePath, table, current =>
            {
                Assert.NotEqual(CanonicalSql(current), CanonicalSql(donorSql));
                return donorSql;
            });
            var before = CapturePreflightSnapshot(databasePath);

            Assert.Throws<InvalidOperationException>(() => new SqliteSessionStore(databasePath).CreateSchema());

            Assert.Equal(before, CapturePreflightSnapshot(databasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
            DeleteDatabaseFiles(donorPath);
        }
    }

    [Fact]
    public void Exact_v5_profile_assembled_from_real_v4_lineage_is_accepted_by_schema_semantics()
    {
        var databasePath = CreateStampedVersionElevenFixture("session-v4.sqlite");
        var versionFivePath = CreateStampedVersionElevenFixture("session-v5.sqlite");
        try
        {
            string versionFiveDraftSql;
            string versionFiveSchema;
            using (var versionFive = OpenReadOnly(versionFivePath))
            {
                versionFiveDraftSql = Scalar<string>(versionFive, "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$id;", "proposal_apply_drafts");
                versionFiveSchema = ReadSemanticSchemaSnapshot(versionFive);
            }
            RewriteTableSql(databasePath, "proposal_apply_drafts", _ => versionFiveDraftSql);
            var before = CapturePreflightSnapshot(databasePath);
            Assert.Equal(versionFiveSchema, before.SemanticSchema);

            new SqliteSessionStore(databasePath).CreateSchema();

            Assert.Equal(before, CapturePreflightSnapshot(databasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
            DeleteDatabaseFiles(versionFivePath);
        }
    }

    [Theory]
    [InlineData("monitor_test_state")]
    [InlineData("analysis_test_state")]
    [InlineData("source_compat_test_state")]
    public void Unrelated_monitor_analysis_and_source_compatibility_objects_are_accepted(string table)
    {
        var databasePath = CreateStampedVersionElevenFixture("session-v10.sqlite");
        try
        {
            using (var connection = Open(databasePath))
                Execute(connection, $"CREATE TABLE {table}(value TEXT PRIMARY KEY); INSERT INTO {table} VALUES('sentinel'); CREATE INDEX {table}_value_idx ON {table}(value DESC); CREATE TRIGGER {table}_trigger AFTER UPDATE ON {table} BEGIN SELECT 'AUTOINCREMENT CHECK (decoy)'; END;");
            var before = CapturePreflightSnapshot(databasePath);

            new SqliteSessionStore(databasePath).CreateSchema();

            Assert.Equal(before, CapturePreflightSnapshot(databasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public void Commented_autoincrement_check_and_quoted_decoys_do_not_change_an_owned_table_profile()
    {
        var databasePath = CreateStampedVersionElevenFixture("session-v10.sqlite");
        try
        {
            RewriteTableSql(databasePath, "sessions", sql => ReplaceRequired(
                sql,
                "status TEXT NOT NULL",
                "/* AUTOINCREMENT CHECK (status='decoy') 'quoted CHECK' */ status TEXT NOT NULL"));
            var before = CapturePreflightSnapshot(databasePath);

            new SqliteSessionStore(databasePath).CreateSchema();

            Assert.Equal(before, CapturePreflightSnapshot(databasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static void AssertStampedVersionTenLineage(SqliteConnection connection, int sourceVersion)
    {
        var tables = ReadTableNames(connection);
        Assert.Equal(sourceVersion == 4 ? ExpectedV11Tables.Where(table => table != "proposal_apply_pending") : ExpectedV11Tables, tables);
        Assert.Equal(sourceVersion >= 5, TableExists(connection, "proposal_apply_pending"));
        if (sourceVersion >= 5) AssertEmpty(connection, "proposal_apply_pending");

        var missingRevisionColumns = sourceVersion switch
        {
            4 or 5 => new[]
            {
                ("improvement_proposals", "revision"),
                ("improvement_proposal_sessions", "proposal_revision"),
                ("proposal_apply_drafts", "proposal_revision"),
                ("proposal_applies", "proposal_revision"),
            },
            6 => [("improvement_proposal_sessions", "proposal_revision")],
            _ => throw new ArgumentOutOfRangeException(nameof(sourceVersion)),
        };
        foreach (var (table, column) in new[]
        {
            ("improvement_proposals", "revision"),
            ("improvement_proposal_sessions", "proposal_revision"),
            ("proposal_apply_drafts", "proposal_revision"),
            ("proposal_applies", "proposal_revision"),
        })
        {
            Assert.Equal(!missingRevisionColumns.Contains((table, column)), ColumnExists(connection, table, column));
        }

        foreach (var table in new[]
        {
            "objective_evaluations", "objective_evaluation_evidence", "effect_comparisons",
            "effect_comparison_sessions", "effect_comparison_evidence", "effect_receipts",
        })
        {
            Assert.True(TableExists(connection, table), $"Stamped-v10 lineage from v{sourceVersion} is missing {table}.");
            AssertEmpty(connection, table);
        }
    }

    private static void AssertReviewedManifestIdentities(IReadOnlyList<(int Version, string File)> actual)
    {
        Assert.Equal(ReviewedFixtureIdentities, actual);
    }

    private static void AssertHistoricalSentinels(SqliteConnection connection, int version, FixtureSentinels sentinels)
    {
        Assert.Equal(sentinels.SessionId.ToString("D"), Scalar<string>(connection, "SELECT session_id FROM sessions;"));
        Assert.Equal(sentinels.NativeSessionId, Scalar<string>(connection, "SELECT native_session_id FROM session_native_ids;"));
        Assert.Equal(sentinels.RunId.ToString("D"), Scalar<string>(connection, "SELECT run_id FROM session_runs;"));
        Assert.Equal(sentinels.EventId.ToString("D"), Scalar<string>(connection, "SELECT event_id FROM session_events;"));
        Assert.Equal(sentinels.SourceEventId, Scalar<string>(connection, "SELECT source_event_id FROM session_events;"));
        Assert.Equal(sentinels.ProjectorKey, Scalar<string>(connection, "SELECT projector_key FROM session_projection_state;"));
        if (version >= 2) Assert.Equal("expected", Scalar<string>(connection, "SELECT verdict FROM session_human_evaluation;"));
        if (version >= 3) Assert.Equal(sentinels.ProposalId!.Value.ToString("D"), Scalar<string>(connection, "SELECT proposal_id FROM improvement_proposals;"));
        if (version >= 4)
        {
            Assert.Equal(sentinels.DraftId!.Value.ToString("D"), Scalar<string>(connection, "SELECT draft_id FROM proposal_apply_drafts;"));
            Assert.Equal(sentinels.ApplyId!.Value.ToString("D"), Scalar<string>(connection, "SELECT apply_id FROM proposal_applies;"));
        }
        if (version >= 8)
            Assert.Equal(sentinels.ObjectiveEvaluationId!.Value.ToString("D"), Scalar<string>(connection, "SELECT objective_evaluation_id FROM objective_evaluations;"));
        if (version >= 9)
        {
            Assert.Equal(sentinels.SecondarySessionId!.Value.ToString("D"), Scalar<string>(connection, "SELECT session_id FROM sessions WHERE session_id<>$id;", sentinels.SessionId.ToString("D")));
            Assert.Equal(sentinels.ComparisonId!.Value.ToString("D"), Scalar<string>(connection, "SELECT comparison_id FROM effect_comparisons;"));
        }
    }

    private static MigratedSnapshot AssertCompleteMigratedState(SqliteConnection connection, int version, FixtureSentinels sentinels)
    {
        var at = new DateTimeOffset(2026, 7, 12, 0, version, 0, TimeSpan.Zero);
        var sessionId = sentinels.SessionId.ToString("D");
        var runId = sentinels.RunId.ToString("D");
        var eventId = sentinels.EventId.ToString("D");

        AssertSingleRow(connection, "schema_version", D(("component", S("session")), ("version", I(CurrentSessionSchemaVersion))));
        var sessionRowCount = version >= 9 ? 2 : 1;
        AssertExpectedRow(connection, "sessions", sessionRowCount, D(
            ("session_id", S(sessionId)), ("status", S("completed")), ("completeness", S("full")),
            ("repository", S($"fixture/session-v{version}")), ("workspace", S($"workspace-v{version}")),
            ("started_at", S(at)), ("ended_at", S(at.AddMinutes(1))), ("last_seen_at", S(at.AddMinutes(1))),
            ("raw_retention_state", S("expiring")), ("created_at", S(at)), ("updated_at", S(at.AddMinutes(1)))));
        AssertExpectedRow(connection, "session_native_ids", sessionRowCount, D(
            ("session_id", S(sessionId)), ("source_surface", S("copilot-sdk")), ("native_session_id", S(sentinels.NativeSessionId)),
            ("binding_kind", S("native")), ("observed_at", S(at))));
        AssertExpectedRow(connection, "session_runs", sessionRowCount, D(
            ("run_id", S(runId)), ("session_id", S(sessionId)), ("source_surface", S("copilot-sdk")),
            ("native_run_id", S($"fixture-run-v{version}")), ("trace_id", S($"fixture-trace-v{version}")), ("parent_run_id", N),
            ("model", S("fixture-model")), ("started_at", S(at)), ("ended_at", S(at.AddMinutes(1))),
            ("input_tokens", I(11 + version)), ("output_tokens", I(21 + version)), ("total_tokens", I(32 + version * 2)), ("status", S("completed"))));
        AssertExpectedRow(connection, "session_events", sessionRowCount, D(
            ("event_id", S(eventId)), ("session_id", S(sessionId)), ("run_id", S(runId)), ("source_surface", S("copilot-sdk")),
            ("parent_event_id", N), ("trace_id", S($"fixture-trace-v{version}")), ("status", S("ok")), ("source_adapter", S("fixture-adapter")),
            ("source_event_id", S(sentinels.SourceEventId)), ("type", S("session.task_complete")), ("occurred_at", S(at.AddSeconds(30))), ("content_state", S("available")),
            ("source_application_version", N), ("adapter_version", N), ("schema_fingerprint", N), ("normalization_version", N), ("match_kind", N)));
        AssertExpectedRow(connection, "session_event_content", sessionRowCount, D(
            ("event_id", S(eventId)), ("content_kind", S("fixture")), ("content_json", S($"{{\"fixture\":\"session-v{version}\"}}")),
            ("captured_at", S(at.AddSeconds(30))), ("expires_at", S(at.AddDays(90)))));
        AssertSingleRow(connection, "session_projection_state", D(
            ("projector_key", S(sentinels.ProjectorKey)), ("projection_cursor", I(1000 + version)),
            ("unsupported_event_version_count", I(10 + version)), ("updated_at", S(at.AddMinutes(1)))));

        if (version >= 2)
            AssertExpectedRow(connection, "session_human_evaluation", version >= 9 ? 2 : 1, D(("session_id", S(sessionId)), ("verdict", S("expected")), ("recorded_at", S(at.AddMinutes(2)))));
        else AssertEmpty(connection, "session_human_evaluation");

        if (version >= 9) AssertSecondarySessionRows(connection, version, at, sentinels);

        if (version >= 3) AssertProposalRows(connection, version, at, sentinels);
        else
        {
            AssertEmpty(connection, "improvement_proposals");
            AssertEmpty(connection, "improvement_proposal_sessions");
            AssertEmpty(connection, "improvement_proposal_evidence");
        }

        if (version >= 4) AssertProposalApplyRows(connection, version, at, sentinels);
        else
        {
            AssertEmpty(connection, "proposal_apply_drafts");
            AssertEmpty(connection, "proposal_apply_files");
            AssertEmpty(connection, "proposal_apply_hunks");
            AssertEmpty(connection, "proposal_apply_revisions");
            AssertEmpty(connection, "proposal_applies");
            AssertEmpty(connection, "proposal_apply_audit");
        }

        AssertEmpty(connection, "proposal_apply_pending");
        if (version >= 8) AssertObjectiveRows(connection, version, at, sentinels);
        else
        {
            AssertEmpty(connection, "objective_evaluations");
            AssertEmpty(connection, "objective_evaluation_evidence");
        }
        if (version >= 9) AssertEffectRows(connection, version, at, sentinels);
        else
        {
            AssertEmpty(connection, "effect_comparisons");
            AssertEmpty(connection, "effect_comparison_sessions");
            AssertEmpty(connection, "effect_comparison_evidence");
            AssertEmpty(connection, "effect_receipts");
        }

        Assert.Equal(ExpectedV11Tables, ReadTableNames(connection));
        var semanticSnapshot = ReadSemanticSchemaSnapshot(connection);
        var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(semanticSnapshot))).ToLowerInvariant();
        Assert.True(string.Equals(ExpectedV11SemanticSchemaHashes[version], actualHash, StringComparison.Ordinal),
            $"Session v{version}->v11 semantic schema hash was {actualHash}.{Environment.NewLine}{semanticSnapshot}");
        return new MigratedSnapshot(semanticSnapshot, ReadDatabaseRowSnapshot(connection));
    }

    private static void AssertSecondarySessionRows(SqliteConnection connection, int version, DateTimeOffset at, FixtureSentinels sentinels)
    {
        var secondaryAt = at.AddMinutes(10);
        var sessionId = sentinels.SecondarySessionId!.Value.ToString("D");
        var runId = sentinels.SecondaryRunId!.Value.ToString("D");
        var eventId = sentinels.SecondaryEventId!.Value.ToString("D");
        AssertExpectedRow(connection, "sessions", 2, D(
            ("session_id", S(sessionId)), ("status", S("completed")), ("completeness", S("full")),
            ("repository", S($"fixture/session-v{version}/secondary")), ("workspace", S($"workspace-v{version}-secondary")),
            ("started_at", S(secondaryAt)), ("ended_at", S(secondaryAt.AddMinutes(1))), ("last_seen_at", S(secondaryAt.AddMinutes(1))),
            ("raw_retention_state", S("expiring")), ("created_at", S(secondaryAt)), ("updated_at", S(secondaryAt.AddMinutes(1)))));
        AssertExpectedRow(connection, "session_native_ids", 2, D(
            ("session_id", S(sessionId)), ("source_surface", S("copilot-sdk")), ("native_session_id", S(sentinels.SecondaryNativeSessionId!)),
            ("binding_kind", S("native")), ("observed_at", S(secondaryAt))));
        AssertExpectedRow(connection, "session_runs", 2, D(
            ("run_id", S(runId)), ("session_id", S(sessionId)), ("source_surface", S("copilot-sdk")),
            ("native_run_id", S($"fixture-run-v{version}-secondary")), ("trace_id", S($"fixture-trace-v{version}-secondary")), ("parent_run_id", N),
            ("model", S("fixture-model")), ("started_at", S(secondaryAt)), ("ended_at", S(secondaryAt.AddMinutes(1))),
            ("input_tokens", I(101 + version)), ("output_tokens", I(201 + version)), ("total_tokens", I(302 + version * 2)), ("status", S("completed"))));
        AssertExpectedRow(connection, "session_events", 2, D(
            ("event_id", S(eventId)), ("session_id", S(sessionId)), ("run_id", S(runId)), ("source_surface", S("copilot-sdk")),
            ("parent_event_id", N), ("trace_id", S($"fixture-trace-v{version}-secondary")), ("status", S("ok")), ("source_adapter", S("fixture-adapter")),
            ("source_event_id", S(sentinels.SecondarySourceEventId!)), ("type", S("session.task_complete")), ("occurred_at", S(secondaryAt.AddSeconds(30))), ("content_state", S("available")),
            ("source_application_version", N), ("adapter_version", N), ("schema_fingerprint", N), ("normalization_version", N), ("match_kind", N)));
        AssertExpectedRow(connection, "session_event_content", 2, D(
            ("event_id", S(eventId)), ("content_kind", S("fixture")), ("content_json", S($"{{\"fixture\":\"session-v{version}-secondary\"}}")),
            ("captured_at", S(secondaryAt.AddSeconds(30))), ("expires_at", S(secondaryAt.AddDays(90)))));
        AssertExpectedRow(connection, "session_human_evaluation", 2, D(
            ("session_id", S(sessionId)), ("verdict", S("expected")), ("recorded_at", S(at.AddMinutes(11)))));
    }

    private static void AssertObjectiveRows(SqliteConnection connection, int version, DateTimeOffset at, FixtureSentinels sentinels)
    {
        var objectiveId = sentinels.ObjectiveEvaluationId!.Value.ToString("D");
        AssertSingleRow(connection, "objective_evaluations", D(
            ("objective_evaluation_id", S(objectiveId)), ("session_id", S(sentinels.SessionId.ToString("D"))),
            ("run_id", S(sentinels.RunId.ToString("D"))), ("trace_id", S($"fixture-trace-v{version}")), ("result", S("fail")),
            ("severity", S("severe")), ("evaluator_id", S("fixture-evaluator")), ("evaluator_version", S("v1")),
            ("criterion_id", S("fixture-criterion")), ("case_key", S("fixture-case")), ("recorded_at", S(at.AddMinutes(2)))));
        AssertSingleRow(connection, "objective_evaluation_evidence", D(
            ("objective_evaluation_id", S(objectiveId)), ("evidence_order", I(0)), ("kind", S("event")),
            ("reference_id", S(sentinels.EventId.ToString("D")))));
    }

    private static void AssertEffectRows(SqliteConnection connection, int version, DateTimeOffset at, FixtureSentinels sentinels)
    {
        var comparisonId = sentinels.ComparisonId!.Value.ToString("D");
        AssertSingleRow(connection, "effect_comparisons", D(
            ("comparison_id", S(comparisonId)), ("cohort_revision", I(1)), ("proposal_id", S(sentinels.ProposalId!.Value.ToString("D"))),
            ("proposal_revision", I(2)), ("apply_id", S(sentinels.ApplyId!.Value.ToString("D"))), ("recorded_at", S(at.AddMinutes(12)))));
        AssertExpectedRow(connection, "effect_comparison_sessions", 2, D(
            ("comparison_id", S(comparisonId)), ("session_id", S(sentinels.SessionId.ToString("D"))), ("classification", S("pre")),
            ("case_key", S("fixture-case")), ("exclusion_reason", N), ("session_order", I(0)),
            ("effective_quality", version == 9 ? N : S("fail")), ("severe_failure", I(version == 9 ? 0 : 1))));
        AssertExpectedRow(connection, "effect_comparison_sessions", 2, D(
            ("comparison_id", S(comparisonId)), ("session_id", S(sentinels.SecondarySessionId!.Value.ToString("D"))), ("classification", S("post")),
            ("case_key", S("fixture-case")), ("exclusion_reason", N), ("session_order", I(1)),
            ("effective_quality", version == 9 ? N : S("pass")), ("severe_failure", I(0))));
        var primarySessionId = sentinels.SessionId.ToString("D");
        var secondarySessionId = sentinels.SecondarySessionId!.Value.ToString("D");
        AssertExpectedRow(connection, "effect_comparison_evidence", 4, D(
            ("comparison_id", S(comparisonId)), ("evidence_order", I(0)), ("session_id", S(primarySessionId)),
            ("kind", S("human")), ("reference_id", S(primarySessionId)), ("recorded_at", S(at.AddMinutes(2))),
            ("human_verdict", version == 9 ? N : S("expected"))));
        AssertExpectedRow(connection, "effect_comparison_evidence", 4, D(
            ("comparison_id", S(comparisonId)), ("evidence_order", I(1)), ("session_id", S(primarySessionId)),
            ("kind", S("objective")), ("reference_id", S(sentinels.ObjectiveEvaluationId!.Value.ToString("D"))), ("recorded_at", S(at.AddMinutes(2))), ("human_verdict", N)));
        AssertExpectedRow(connection, "effect_comparison_evidence", 4, D(
            ("comparison_id", S(comparisonId)), ("evidence_order", I(2)), ("session_id", S(primarySessionId)),
            ("kind", S("objective_event")), ("reference_id", S(sentinels.EventId.ToString("D"))), ("recorded_at", N), ("human_verdict", N)));
        AssertExpectedRow(connection, "effect_comparison_evidence", 4, D(
            ("comparison_id", S(comparisonId)), ("evidence_order", I(3)), ("session_id", S(secondarySessionId)),
            ("kind", S("human")), ("reference_id", S(secondarySessionId)), ("recorded_at", S(at.AddMinutes(11))),
            ("human_verdict", version == 9 ? N : S("expected"))));
        const string resultJson = "{\"Verdict\":3,\"PrePass\":0,\"PreCount\":1,\"PostPass\":1,\"PostCount\":1,\"PreDurationMedian\":null,\"PostDurationMedian\":null,\"DurationDelta\":null,\"PreTokenMedian\":null,\"PostTokenMedian\":null,\"TokenDelta\":null,\"Reasons\":[\"insufficient_cohort\"]}";
        AssertSingleRow(connection, "effect_receipts", D(
            ("comparison_id", S(comparisonId)), ("verdict", S("insufficient_evidence")), ("result_json", S(resultJson)), ("recorded_at", S(at.AddMinutes(12)))));
    }

    private static void AssertProposalRows(SqliteConnection connection, int version, DateTimeOffset at, FixtureSentinels sentinels)
    {
        var proposalId = sentinels.ProposalId!.Value.ToString("D");
        AssertSingleRow(connection, "improvement_proposals", D(
            ("proposal_id", S(proposalId)), ("revision", I(version >= 9 ? 2 : 1)), ("status", S(version >= 9 ? "recommended" : "candidate")), ("target_kind", S("skill")),
            ("target_label", S($"fixture-target-v{version}")), ("title", S($"Fixture proposal v{version}")), ("summary", S("Synthetic migration sentinel")),
            ("expected_effect", S("Preserve fixture rows")), ("risk_note", S("none")), ("created_at", S(at.AddMinutes(3))),
            ("updated_at", S(at.AddMinutes(version >= 9 ? 4 : 3))), ("recommended_at", version >= 9 ? S(at.AddMinutes(4)) : N), ("verified_at", N)));
        AssertExpectedRow(connection, "improvement_proposal_sessions", version >= 9 ? 2 : 1, D(
            ("proposal_id", S(proposalId)), ("proposal_revision", I(1)), ("session_id", S(sentinels.SessionId.ToString("D"))), ("source_order", I(0))));
        AssertExpectedRow(connection, "improvement_proposal_evidence", version >= 9 ? 2 : 1, D(
            ("proposal_id", S(proposalId)), ("evidence_order", I(0)), ("kind", S("event")), ("reference_id", S(sentinels.EventId.ToString("D")))));
        if (version >= 9)
        {
            AssertExpectedRow(connection, "improvement_proposal_sessions", 2, D(
                ("proposal_id", S(proposalId)), ("proposal_revision", I(1)), ("session_id", S(sentinels.SecondarySessionId!.Value.ToString("D"))), ("source_order", I(1))));
            AssertExpectedRow(connection, "improvement_proposal_evidence", 2, D(
                ("proposal_id", S(proposalId)), ("evidence_order", I(1)), ("kind", S("event")), ("reference_id", S(sentinels.SecondaryEventId!.Value.ToString("D")))));
        }
    }

    private static void AssertProposalApplyRows(SqliteConnection connection, int version, DateTimeOffset at, FixtureSentinels sentinels)
    {
        var proposalId = sentinels.ProposalId!.Value.ToString("D");
        var draftId = sentinels.DraftId!.Value.ToString("D");
        var applyId = sentinels.ApplyId!.Value.ToString("D");
        var rootId = sentinels.RootId!.Value.ToString("D");
        var updatedAt = version == 4 ? "s:1970-01-01T00:00:00.0000000+00:00" : S(at.AddMinutes(5));
        AssertSingleRow(connection, "proposal_apply_drafts", D(
            ("draft_id", S(draftId)), ("proposal_id", S(proposalId)), ("proposal_revision", I(version >= 9 ? 2 : 1)), ("root_id", S(rootId)),
            ("selection_revision", I(1)), ("approval_digest", S($"fixture-digest-v{version}")), ("state", S("applied")),
            ("created_at", S(at.AddMinutes(4))), ("updated_at", updatedAt)));
        AssertSingleRow(connection, "proposal_apply_files", D(
            ("draft_id", S(draftId)), ("file_order", I(0)), ("base_sha256", S($"fixture-base-v{version}")), ("replacement_sha256", S($"fixture-replacement-v{version}"))));
        AssertSingleRow(connection, "proposal_apply_hunks", D(
            ("draft_id", S(draftId)), ("hunk_id", S($"fixture-hunk-v{version}")), ("selected", I(1)), ("replacement_sha256", S($"fixture-replacement-v{version}"))));
        AssertSingleRow(connection, "proposal_apply_revisions", D(
            ("draft_id", S(draftId)), ("selection_revision", I(1)), ("approval_digest", S($"fixture-digest-v{version}")), ("approved_at", S(at.AddMinutes(4)))));
        AssertSingleRow(connection, "proposal_applies", D(
            ("apply_id", S(applyId)), ("draft_id", S(draftId)), ("proposal_revision", I(version >= 9 ? 2 : 1)), ("state", S("applied")), ("created_at", S(at.AddMinutes(5)))));
        AssertSingleRow(connection, "proposal_apply_audit", D(
            ("audit_id", I(1)), ("apply_id", S(applyId)), ("draft_id", S(draftId)), ("proposal_id", S(proposalId)), ("root_id", S(rootId)),
            ("actor_kind", S("local_user")), ("state", S("applied")), ("error_code", N), ("file_count", I(1)), ("recorded_at", S(at.AddMinutes(5)))));
    }

    private static void AssertSingleRow(SqliteConnection connection, string table, IReadOnlyDictionary<string, string> expected)
        => AssertExpectedRow(connection, table, 1, expected);

    private static void AssertExpectedRow(SqliteConnection connection, string table, int expectedCount, IReadOnlyDictionary<string, string> expected)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM \"{table}\";";
        using var reader = command.ExecuteReader();
        var rows = new List<SortedDictionary<string, string>>();
        while (reader.Read())
        {
            var row = new SortedDictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < reader.FieldCount; index++) row.Add(reader.GetName(index), Encode(reader, index));
            rows.Add(row);
        }
        Assert.Equal(expectedCount, rows.Count);
        Assert.Contains(rows, actual => expected.SequenceEqual(actual));
    }

    private static void AssertEmpty(SqliteConnection connection, string table) =>
        Assert.Equal(0L, Scalar<long>(connection, $"SELECT COUNT(*) FROM \"{table}\";"));

    private static void AssertDatabaseIntegrity(SqliteConnection connection)
    {
        Assert.Equal("ok", Scalar<string>(connection, "PRAGMA integrity_check;"));
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        Assert.False(reader.Read());
    }

    private static void AssertForeignKeyCheckHasRows(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
    }

    private static string[] ReadTableNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_list WHERE schema='main' AND type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private static string ReadSemanticSchemaSnapshot(SqliteConnection connection)
    {
        var lines = new List<string>();
        using (var tables = connection.CreateCommand())
        {
            tables.CommandText = "SELECT schema,name,type,ncol,wr,strict FROM pragma_table_list WHERE name NOT LIKE 'sqlite_%' ORDER BY schema,name;";
            using var reader = tables.ExecuteReader();
            while (reader.Read()) lines.Add($"table|{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetInt32(3)}|{reader.GetInt32(4)}|{reader.GetInt32(5)}");
        }

        foreach (var table in ReadTableNames(connection))
        {
            using (var columns = connection.CreateCommand())
            {
                columns.CommandText = "SELECT cid,name,type,\"notnull\",dflt_value,pk,hidden FROM pragma_table_xinfo($table) ORDER BY cid;";
                columns.Parameters.AddWithValue("$table", table);
                using var reader = columns.ExecuteReader();
                while (reader.Read())
                    lines.Add($"column|{table}|{reader.GetInt32(0)}|{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetInt32(3)}|{(reader.IsDBNull(4) ? "<null>" : reader.GetString(4))}|{reader.GetInt32(5)}|{reader.GetInt32(6)}");
            }

            lines.Add($"sql|{table}|{CanonicalSql(Scalar<string>(connection, "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$id;", table))}");

            var indexes = new List<string>();
            using (var indexList = connection.CreateCommand())
            {
                indexList.CommandText = "SELECT name,\"unique\",origin,partial FROM pragma_index_list($table);";
                indexList.Parameters.AddWithValue("$table", table);
                using var reader = indexList.ExecuteReader();
                while (reader.Read())
                {
                    var name = reader.GetString(0);
                    var terms = new List<string>();
                    using var indexInfo = connection.CreateCommand();
                    indexInfo.CommandText = "SELECT seqno,cid,name,desc,coll,key FROM pragma_index_xinfo($index) ORDER BY seqno;";
                    indexInfo.Parameters.AddWithValue("$index", name);
                    using var termReader = indexInfo.ExecuteReader();
                    while (termReader.Read())
                        terms.Add($"{termReader.GetInt32(0)},{termReader.GetInt32(1)},{(termReader.IsDBNull(2) ? "<expression>" : termReader.GetString(2))},{termReader.GetInt32(3)},{(termReader.IsDBNull(4) ? "<null>" : termReader.GetString(4))},{termReader.GetInt32(5)}");
                    var explicitSql = ScalarOrNull<string>(connection, "SELECT sql FROM sqlite_schema WHERE type='index' AND name=$id;", name);
                    indexes.Add($"index|{table}|{reader.GetInt32(1)}|{reader.GetString(2)}|{reader.GetInt32(3)}|{string.Join(';', terms)}|{(explicitSql is null ? "<auto>" : CanonicalSql(explicitSql))}");
                }
            }
            lines.AddRange(indexes.Order(StringComparer.Ordinal));

            using var foreignKeys = connection.CreateCommand();
            foreignKeys.CommandText = "SELECT id,seq,\"table\",\"from\",\"to\",on_update,on_delete,match FROM pragma_foreign_key_list($table) ORDER BY id,seq;";
            foreignKeys.Parameters.AddWithValue("$table", table);
            using var foreignKeyReader = foreignKeys.ExecuteReader();
            while (foreignKeyReader.Read())
                lines.Add($"foreign-key|{table}|{foreignKeyReader.GetInt32(0)}|{foreignKeyReader.GetInt32(1)}|{foreignKeyReader.GetString(2)}|{foreignKeyReader.GetString(3)}|{(foreignKeyReader.IsDBNull(4) ? "<null>" : foreignKeyReader.GetString(4))}|{foreignKeyReader.GetString(5)}|{foreignKeyReader.GetString(6)}|{foreignKeyReader.GetString(7)}");
        }

        using (var objects = connection.CreateCommand())
        {
            objects.CommandText = "SELECT type,name,sql FROM sqlite_schema WHERE type IN ('view','trigger') AND name NOT LIKE 'sqlite_%' ORDER BY type,name;";
            using var reader = objects.ExecuteReader();
            while (reader.Read()) lines.Add($"object|{reader.GetString(0)}|{reader.GetString(1)}|{CanonicalSql(reader.GetString(2))}");
        }
        return string.Join('\n', lines) + "\n";
    }

    private static string ReadDatabaseRowSnapshot(SqliteConnection connection)
    {
        var lines = new List<string>();
        foreach (var table in ReadTableNames(connection))
        {
            var columns = new List<string>();
            using (var columnCommand = connection.CreateCommand())
            {
                columnCommand.CommandText = "SELECT name FROM pragma_table_xinfo($table) WHERE hidden=0 ORDER BY cid;";
                columnCommand.Parameters.AddWithValue("$table", table);
                using var columnReader = columnCommand.ExecuteReader();
                while (columnReader.Read()) columns.Add(columnReader.GetString(0));
            }
            var projection = string.Join(',', columns.Select(QuoteIdentifier));
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {projection} FROM {QuoteIdentifier(table)} ORDER BY {projection};";
            using var reader = command.ExecuteReader();
            lines.Add($"table|{table}|{string.Join('|', columns)}");
            while (reader.Read())
            {
                lines.Add($"row|{table}|{string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(index => Encode(reader, index)))}");
            }
        }
        return string.Join('\n', lines) + "\n";
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string CanonicalSql(string sql)
    {
        var tokens = new List<string>();
        for (var index = 0; index < sql.Length;)
        {
            if (char.IsWhiteSpace(sql[index])) { index++; continue; }
            if (sql[index] == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                index += 2; while (index < sql.Length && sql[index] is not ('\r' or '\n')) index++; continue;
            }
            if (sql[index] == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                index += 2; while (index + 1 < sql.Length && !(sql[index] == '*' && sql[index + 1] == '/')) index++;
                index = Math.Min(sql.Length, index + 2); continue;
            }
            if (sql[index] is '\'' or '"' or '`' or '[')
            {
                var start = index;
                var opening = sql[index++];
                var closing = opening == '[' ? ']' : opening;
                while (index < sql.Length)
                {
                    if (sql[index++] != closing) continue;
                    if (index < sql.Length && sql[index] == closing && opening != '[') { index++; continue; }
                    break;
                }
                tokens.Add(sql[start..index]);
                continue;
            }
            if (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '.')
            {
                var start = index++;
                while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '.')) index++;
                tokens.Add(sql[start..index].ToLowerInvariant());
                continue;
            }
            tokens.Add(sql[index++].ToString());
        }
        return string.Join('|', tokens);
    }

    private static void ApplyStampedVersionElevenMutation(string databasePath, string mutationName)
    {
        switch (mutationName)
        {
            case "extra-regular-column":
                RewriteDefinitions(databasePath, "session_projection_state", definitions => definitions.Append("unexpected_column TEXT NULL"));
                return;
            case "missing-regular-column":
                RewriteDefinitions(databasePath, "session_projection_state", definitions => definitions.Where(definition => !StartsWithIdentifier(definition, "updated_at")));
                return;
            case "virtual-generated-column":
                RewriteDefinitions(databasePath, "session_projection_state", definitions => definitions.Append("synthetic_cursor INTEGER GENERATED ALWAYS AS (projection_cursor + 1) VIRTUAL"));
                return;
            case "stored-generated-column":
                RewriteDefinitions(databasePath, "session_projection_state", definitions => definitions.Append("synthetic_cursor INTEGER GENERATED ALWAYS AS (projection_cursor + 1) STORED"));
                return;
            case "declared-type-changed":
                RewriteDefinition(databasePath, "session_projection_state", "projection_cursor", definition => ReplaceRequired(definition, "INTEGER", "TEXT"));
                return;
            case "nullability-changed":
                RewriteDefinition(databasePath, "session_projection_state", "updated_at", definition => ReplaceRequired(definition, "TEXT NOT NULL", "TEXT NULL"));
                return;
            case "default-added":
                RewriteDefinition(databasePath, "session_projection_state", "updated_at", definition => ReplaceRequired(definition, "TEXT NOT NULL", "TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z'"));
                return;
            case "primary-key-changed-to-unique":
                RebuildTable(databasePath, "session_projection_state", sql => ReplaceRequired(sql, "projector_key TEXT PRIMARY KEY", "projector_key TEXT NOT NULL UNIQUE"));
                return;
            case "column-order-changed":
                RewriteDefinitions(databasePath, "session_projection_state", definitions =>
                {
                    var changed = definitions.ToList();
                    (changed[1], changed[2]) = (changed[2], changed[1]);
                    return changed;
                });
                return;
            case "check-removed":
                RewriteDefinition(databasePath, "session_projection_state", "projection_cursor", definition => ReplaceRequired(definition, " CHECK (projection_cursor IS NULL OR projection_cursor >= 0)", string.Empty));
                return;
            case "check-broadened":
                RewriteDefinition(databasePath, "session_projection_state", "projection_cursor", definition => ReplaceRequired(definition, "projection_cursor >= 0", "projection_cursor >= -1"));
                return;
            case "check-added":
                RewriteDefinitions(databasePath, "session_projection_state", definitions => definitions.Append("CHECK (length(projector_key) > 0)"));
                return;
            case "check-removed-with-quoted-decoy":
                RewriteDefinition(databasePath, "session_projection_state", "projection_cursor", definition => ReplaceRequired(definition, " CHECK (projection_cursor IS NULL OR projection_cursor >= 0)", " DEFAULT 'CHECK (projection_cursor IS NULL OR projection_cursor >= 0)'"));
                return;
            case "check-removed-with-comment-decoy":
                RewriteDefinition(databasePath, "session_projection_state", "projection_cursor", definition => ReplaceRequired(definition, " CHECK (projection_cursor IS NULL OR projection_cursor >= 0)", " /* CHECK (projection_cursor IS NULL OR projection_cursor >= 0) */"));
                return;
            case "autoincrement-added":
                RebuildTable(databasePath, "proposal_apply_audit", sql => ReplaceRequired(sql, "audit_id INTEGER PRIMARY KEY", "audit_id INTEGER PRIMARY KEY AUTOINCREMENT"));
                return;
            case "ordinary-index-added":
                ExecuteMutation(databasePath, "CREATE INDEX sessions_status_idx ON sessions(status);");
                return;
            case "unique-index-added":
                ExecuteMutation(databasePath, "CREATE UNIQUE INDEX sessions_session_id_unique_idx ON sessions(session_id);");
                return;
            case "expression-index-added":
                ExecuteMutation(databasePath, "CREATE INDEX sessions_status_expression_idx ON sessions(lower(status));");
                return;
            case "autoindex-primary-key-order-changed":
                RebuildTable(databasePath, "session_native_ids", sql => ReplaceRequired(sql, "PRIMARY KEY (source_surface, native_session_id)", "PRIMARY KEY (native_session_id, source_surface)"));
                return;
            case "index-collation-changed":
                RebuildTable(databasePath, "session_native_ids", sql => ReplaceRequired(sql, "source_surface TEXT NOT NULL", "source_surface TEXT COLLATE NOCASE NOT NULL"));
                return;
            case "index-direction-changed":
                RebuildTable(databasePath, "session_native_ids", sql => ReplaceRequired(sql, "PRIMARY KEY (source_surface, native_session_id)", "PRIMARY KEY (source_surface DESC, native_session_id)"));
                return;
            case "index-key-and-auxiliary-terms-changed":
                ExecuteMutation(databasePath, "CREATE INDEX sessions_status_session_idx ON sessions(status,session_id);");
                return;
            case "partial-index-added":
                ExecuteMutation(databasePath, "CREATE INDEX sessions_partial_idx ON sessions(status) WHERE ended_at IS NOT NULL;");
                return;
            case "foreign-key-removed":
                RewriteDefinitions(databasePath, "session_event_content", definitions => definitions.Where(definition => !definition.TrimStart().StartsWith("FOREIGN KEY", StringComparison.OrdinalIgnoreCase)));
                return;
            case "foreign-key-retargeted":
                RewriteDefinition(databasePath, "session_human_evaluation", "FOREIGN KEY", definition => ReplaceRequired(definition, "REFERENCES sessions(session_id)", "REFERENCES session_runs(run_id)"));
                return;
            case "foreign-key-added":
                RewriteDefinitions(databasePath, "session_human_evaluation", definitions => definitions.Append("FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE"));
                return;
            case "foreign-key-action-changed":
                RewriteDefinition(databasePath, "session_human_evaluation", "FOREIGN KEY", definition => ReplaceRequired(definition, "ON DELETE CASCADE", "ON DELETE RESTRICT"));
                return;
            case "trigger-on-owned-table-added":
                ExecuteMutation(databasePath, "CREATE TRIGGER monitor_named_owned_trigger AFTER UPDATE ON sessions BEGIN SELECT 1; END;");
                return;
            case "owned-table-replaced-by-view":
                ExecuteMutation(databasePath, "PRAGMA foreign_keys=OFF; DROP TABLE session_projection_state; CREATE VIEW session_projection_state AS SELECT 'fixture-projector-v10' AS projector_key,1010 AS projection_cursor,20 AS unsupported_event_version_count,'2026-07-12T00:11:00.0000000+00:00' AS updated_at;");
                return;
            case "reserved-table-added":
                ExecuteMutation(databasePath, "CREATE TABLE session_reserved_extra(value TEXT);");
                return;
            case "reserved-view-added":
                ExecuteMutation(databasePath, "CREATE VIEW improvement_proposal_reserved_view AS SELECT 1 AS value;");
                return;
            case "strict-table-added":
                RewriteTableSql(databasePath, "session_projection_state", sql => sql + " STRICT");
                return;
            case "without-rowid-added":
                RebuildTable(databasePath, "session_projection_state", sql => sql + " WITHOUT ROWID");
                return;
            case "reserved-virtual-table-added":
                ExecuteMutation(databasePath, "CREATE VIRTUAL TABLE session_reserved_virtual USING fts5(value);");
                return;
            case "schema-version-extra-column":
                RewriteDefinitions(databasePath, "schema_version", definitions => definitions.Append("unexpected_column TEXT NULL"));
                return;
            case "schema-version-component-primary-key-removed":
                RebuildTable(databasePath, "schema_version", sql => ReplaceRequired(sql, "component TEXT PRIMARY KEY", "component TEXT NOT NULL UNIQUE"));
                return;
            case "owned-table-removed":
                ExecuteMutation(databasePath, "DROP TABLE proposal_apply_pending;");
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutationName), mutationName, "Unknown stamped-v11 mutation.");
        }
    }

    private static string ReplaceRequired(string value, string oldValue, string newValue)
    {
        var replaced = value.Replace(oldValue, newValue, StringComparison.Ordinal);
        Assert.NotEqual(value, replaced);
        return replaced;
    }

    private static void RewriteDefinition(string databasePath, string table, string identifier, Func<string, string> rewrite) =>
        RewriteDefinitions(databasePath, table, definitions => definitions.Select(definition =>
            StartsWithIdentifier(definition, identifier) ? rewrite(definition) : definition));

    private static void RewriteDefinitions(string databasePath, string table, Func<IReadOnlyList<string>, IEnumerable<string>> rewrite)
    {
        RewriteTableSql(databasePath, table, sql =>
        {
            var opening = sql.IndexOf('(', StringComparison.Ordinal);
            var closing = FindMatchingParenthesis(sql, opening);
            Assert.True(opening >= 0 && closing > opening, $"Unable to parse {table} CREATE TABLE SQL: {sql}");
            var definitions = SplitTopLevel(sql[(opening + 1)..closing]);
            var rewritten = rewrite(definitions).ToArray();
            Assert.False(definitions.SequenceEqual(rewritten), $"Mutation did not change {table} CREATE TABLE semantics.");
            return sql[..(opening + 1)] + string.Join(',', rewritten) + sql[closing..];
        });
    }

    private static int FindMatchingParenthesis(string sql, int opening)
    {
        var depth = 0;
        var quote = '\0';
        for (var index = opening; index < sql.Length; index++)
        {
            var character = sql[index];
            if (quote != '\0')
            {
                var closingQuote = quote == '[' ? ']' : quote;
                if (character != closingQuote) continue;
                if (quote != '[' && index + 1 < sql.Length && sql[index + 1] == closingQuote) { index++; continue; }
                quote = '\0';
                continue;
            }
            if (character is '\'' or '"' or '`' or '[') { quote = character; continue; }
            if (character == '(') depth++;
            else if (character == ')' && --depth == 0) return index;
        }
        return -1;
    }

    private static IReadOnlyList<string> SplitTopLevel(string definitions)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        var quote = '\0';
        for (var index = 0; index < definitions.Length; index++)
        {
            var character = definitions[index];
            if (quote != '\0')
            {
                var closingQuote = quote == '[' ? ']' : quote;
                if (character != closingQuote) continue;
                if (quote != '[' && index + 1 < definitions.Length && definitions[index + 1] == closingQuote) { index++; continue; }
                quote = '\0';
                continue;
            }
            if (character is '\'' or '"' or '`' or '[') { quote = character; continue; }
            if (character == '(') depth++;
            else if (character == ')') depth--;
            else if (character == ',' && depth == 0)
            {
                result.Add(definitions[start..index].Trim());
                start = index + 1;
            }
        }
        result.Add(definitions[start..].Trim());
        return result;
    }

    private static bool StartsWithIdentifier(string definition, string identifier)
    {
        var value = definition.TrimStart();
        if (identifier == "FOREIGN KEY") return value.StartsWith(identifier, StringComparison.OrdinalIgnoreCase);
        var length = 0;
        while (length < value.Length && (char.IsLetterOrDigit(value[length]) || value[length] == '_')) length++;
        return string.Equals(value[..length], identifier, StringComparison.OrdinalIgnoreCase);
    }

    private static void RewriteTableSql(string databasePath, string table, Func<string, string> rewrite)
    {
        using var connection = Open(databasePath);
        var currentSql = Scalar<string>(connection, "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$id;", table);
        var rewrittenSql = rewrite(currentSql);
        Assert.NotEqual(currentSql, rewrittenSql);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA writable_schema=ON; UPDATE sqlite_schema SET sql=$sql WHERE type='table' AND name=$table; PRAGMA writable_schema=OFF;";
        command.Parameters.AddWithValue("$sql", rewrittenSql);
        command.Parameters.AddWithValue("$table", table);
        command.ExecuteNonQuery();
        Execute(connection, $"PRAGMA schema_version={Scalar<long>(connection, "PRAGMA schema_version;") + 1};");
    }

    private static void RebuildTable(string databasePath, string table, Func<string, string> rewrite)
    {
        using var connection = Open(databasePath);
        Execute(connection, "PRAGMA foreign_keys=OFF; PRAGMA legacy_alter_table=ON;");
        var currentSql = Scalar<string>(connection, "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$id;", table);
        var rewrittenSql = rewrite(currentSql);
        Assert.NotEqual(currentSql, rewrittenSql);
        var columns = new List<string>();
        using (var columnCommand = connection.CreateCommand())
        {
            columnCommand.CommandText = "SELECT name FROM pragma_table_xinfo($table) WHERE hidden=0 ORDER BY cid;";
            columnCommand.Parameters.AddWithValue("$table", table);
            using var reader = columnCommand.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(0));
        }
        var oldTable = $"mutation_old_{table}";
        var projection = string.Join(',', columns.Select(QuoteIdentifier));
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, $"ALTER TABLE {QuoteIdentifier(table)} RENAME TO {QuoteIdentifier(oldTable)};");
        Execute(connection, transaction, rewrittenSql);
        Execute(connection, transaction, $"INSERT INTO {QuoteIdentifier(table)}({projection}) SELECT {projection} FROM {QuoteIdentifier(oldTable)};");
        Execute(connection, transaction, $"DROP TABLE {QuoteIdentifier(oldTable)};");
        transaction.Commit();
    }

    private static void ExecuteMutation(string databasePath, string sql)
    {
        using var connection = Open(databasePath);
        Execute(connection, sql);
    }

    private static string CreateStampedVersionElevenFixture(string fixtureFile)
    {
        var databasePath = CopyFixture(fixtureFile);
        new SqliteSessionStore(databasePath).CreateSchema();
        return databasePath;
    }

    private static PreflightSnapshot CapturePreflightSnapshot(string databasePath)
    {
        long version;
        string schema;
        string rows;
        string journalMode;
        using (var connection = Open(databasePath))
        {
            version = Scalar<long>(connection, "SELECT version FROM schema_version WHERE component='session';");
            schema = ReadSemanticSchemaSnapshot(connection);
            rows = ReadDatabaseRowSnapshot(connection);
            journalMode = Scalar<string>(connection, "PRAGMA journal_mode;");
        }
        return new(version, schema, rows, journalMode, CaptureFile(databasePath + "-wal"), CaptureFile(databasePath + "-shm"));
    }

    private static FileSnapshot CaptureFile(string path) => File.Exists(path)
        ? new(true, new FileInfo(path).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant())
        : new(false, 0, string.Empty);

    private static void DeleteDatabaseFiles(string databasePath)
    {
        File.Delete(databasePath);
        File.Delete(databasePath + "-wal");
        File.Delete(databasePath + "-shm");
    }

    private static string CopyFixture(string fixtureFile)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "session", fixtureFile);
        var migratedPath = Path.Combine(Path.GetTempPath(), $"session-migration-{Guid.NewGuid():N}.sqlite");
        File.Copy(fixturePath, migratedPath);
        return migratedPath;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        return connection;
    }

    private static SqliteConnection OpenReadOnly(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type='table' AND name=$table;";
        command.Parameters.AddWithValue("$table", table);
        return command.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name=$column;";
        command.Parameters.AddWithValue("$column", column);
        return command.ExecuteScalar() is not null;
    }

    private static T Scalar<T>(SqliteConnection connection, string sql, object? parameter = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameter is not null) command.Parameters.AddWithValue("$id", parameter);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static T? ScalarOrNull<T>(SqliteConnection connection, string sql, object parameter)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", parameter);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    private static string[] ReadSessionEventIdentity(SqliteConnection connection) =>
    [
        Scalar<string>(connection, "SELECT session_id FROM sessions;"),
        Scalar<string>(connection, "SELECT native_session_id FROM session_native_ids;"),
        Scalar<string>(connection, "SELECT event_id FROM session_events;"),
        Scalar<string>(connection, "SELECT source_event_id FROM session_events;"),
    ];

    private static string Encode(SqliteDataReader reader, int index) => reader.GetFieldType(index) switch
    {
        _ when reader.IsDBNull(index) => N,
        Type type when type == typeof(long) => I(reader.GetInt64(index)),
        Type type when type == typeof(double) => $"r:{reader.GetDouble(index).ToString("R", CultureInfo.InvariantCulture)}",
        Type type when type == typeof(byte[]) => $"b:{Convert.ToHexString((byte[])reader.GetValue(index)).ToLowerInvariant()}",
        _ => S(reader.GetString(index)),
    };

    private static SortedDictionary<string, string> D(params (string Name, string Value)[] values) =>
        new(values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal), StringComparer.Ordinal);
    private static string S(string value) => $"s:{value}";
    private static string S(DateTimeOffset value) => S(value.ToString("O", CultureInfo.InvariantCulture));
    private static string I(long value) => $"i:{value}";
    private const string N = "<null>";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly (int Version, string File)[] ReviewedFixtureIdentities =
    [
        (1, "session-v1.sqlite"),
        (2, "session-v2.sqlite"),
        (3, "session-v3.sqlite"),
        (4, "session-v4.sqlite"),
        (5, "session-v5.sqlite"),
        (6, "session-v6.sqlite"),
        (7, "session-v7.sqlite"),
        (8, "session-v8.sqlite"),
        (9, "session-v9.sqlite"),
        (10, "session-v10.sqlite"),
        (10, "session-v10-from-v4.sqlite"),
        (10, "session-v10-from-v5.sqlite"),
        (10, "session-v10-from-v6.sqlite"),
    ];

    private sealed record FixtureManifest(string Component, string GenerationCommand, string GitStatusCommand, IReadOnlyList<FixtureEntry> Fixtures);
    private sealed record FixtureEntry(
        int Version,
        string File,
        string SourceCommit,
        string Sha256,
        string GitStatusBefore,
        string GitStatusAfter,
        IReadOnlyList<string> Limitations,
        FixtureSentinels Sentinels,
        LineageProvenance? Lineage = null);
    private sealed record LineageProvenance(
        int SourceVersion,
        string SourceFixture,
        string SourceFixtureSha256,
        string UpgraderCommit,
        IReadOnlyList<string> Commands);
    private sealed record FixtureSentinels(Guid SessionId, string NativeSessionId, Guid RunId, Guid EventId, string SourceEventId, string ProjectorKey, Guid? ProposalId, Guid? DraftId, Guid? ApplyId, Guid? RootId, Guid? ObjectiveEvaluationId = null, Guid? SecondarySessionId = null, string? SecondaryNativeSessionId = null, Guid? SecondaryRunId = null, Guid? SecondaryEventId = null, string? SecondarySourceEventId = null, Guid? ComparisonId = null);
    private sealed record MigratedSnapshot(string SemanticSchema, string Rows);
    private sealed record PreflightSnapshot(long Version, string SemanticSchema, string Rows, string JournalMode, FileSnapshot Wal, FileSnapshot Shm);
    private sealed record FileSnapshot(bool Exists, long Length, string Sha256);
}
