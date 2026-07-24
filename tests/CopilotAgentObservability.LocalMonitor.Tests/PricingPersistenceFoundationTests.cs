using System.Security.Cryptography;
using CopilotAgentObservability.Persistence.Sqlite.Costs;
using CopilotAgentObservability.Persistence.Sqlite.Pricing;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Pricing;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class PricingPersistenceFoundationTests
{
    public static TheoryData<string> SupportedHistoricalSessionFixtures => new()
    {
        "session-v1.sqlite",
        "session-v2.sqlite",
        "session-v3.sqlite",
        "session-v4.sqlite",
        "session-v5.sqlite",
        "session-v6.sqlite",
        "session-v7.sqlite",
        "session-v8.sqlite",
        "session-v9.sqlite",
        "session-v10.sqlite",
        "session-v10-from-v4.sqlite",
        "session-v10-from-v5.sqlite",
        "session-v10-from-v6.sqlite",
    };

    [Fact]
    public void PricingSchemaV1_Ensure_CreatesExactComponentAfterRequiredDependencies()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();

        using (var connection = database.Open())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            PricingSchemaV1.Ensure(connection, transaction);
            transaction.Commit();
        }

        using var read = database.Open();
        Assert.Equal(1L, Scalar<long>(read, "SELECT version FROM schema_version WHERE component='pricing';"));
        Assert.True(PricingSchemaV1.IsValid(read, null));
        Assert.True(PricingSchemaV1.ValidateRows(read, null));
        Assert.Equal(
            PricingSchemaV1.OwnedObjects.Select(item => item.Name),
            Names(read, "SELECT name FROM sqlite_schema WHERE name GLOB 'pricing_*' ORDER BY type,name;"));
    }

    [Fact]
    public void PricingSchemaV1_Ensure_RejectsRuntimeBackupMissingWithoutMutation()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies(includeRuntimeBackup: false);

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        Assert.Throws<InvalidOperationException>(() => PricingSchemaV1.Ensure(connection, transaction));
        transaction.Rollback();
        Assert.Empty(Names(connection, "SELECT name FROM sqlite_schema WHERE name GLOB 'pricing_*';"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM schema_version WHERE component='pricing';"));
    }

    [Fact]
    public void PricingSchemaV1_Ensure_RejectsPartialNamespaceWithoutRepair()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        using var connection = database.Open();
        Execute(connection, "CREATE TABLE pricing_catalog_snapshots(catalog_sha256 TEXT PRIMARY KEY);");

        using var transaction = connection.BeginTransaction(deferred: false);
        Assert.Throws<InvalidOperationException>(() => PricingSchemaV1.Ensure(connection, transaction));
        transaction.Rollback();

        Assert.Equal(
            ["pricing_catalog_snapshots"],
            Names(connection, "SELECT name FROM sqlite_schema WHERE name GLOB 'pricing_*';"));
    }

    [Fact]
    public void PricingSchemaV1_Ensure_RejectsVersionOnlySkeletalDependenciesWithoutMutation()
    {
        using var database = new PricingDatabase();
        using var connection = database.Open();
        Execute(
            connection,
            """
            CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL);
            INSERT INTO schema_version(component,version)
            VALUES('session',13),('alert_engine',2),('runtime_backup',1);
            CREATE TABLE sessions(session_id TEXT PRIMARY KEY);
            CREATE TABLE alert_evaluations(evaluation_id TEXT PRIMARY KEY,schema_version TEXT NOT NULL);
            CREATE TABLE alert_receipts(alert_id TEXT PRIMARY KEY,evaluation_id TEXT NOT NULL);
            CREATE TABLE alert_suppressions(evaluation_id TEXT NOT NULL,suppression_ordinal INTEGER NOT NULL);
            CREATE TABLE runtime_backup_receipts(operation_id TEXT PRIMARY KEY);
            """);

        using var transaction = connection.BeginTransaction(deferred: false);
        Assert.Throws<InvalidOperationException>(() => PricingSchemaV1.Ensure(connection, transaction));
        transaction.Rollback();
        Assert.Empty(Names(connection, "SELECT name FROM sqlite_schema WHERE name GLOB 'pricing_*';"));
    }

    [Fact]
    public void PricingSchemaV1_Ensure_RejectsExactSessionsTableWithoutFullSessionV13Component()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        using var connection = database.Open();
        Execute(connection, "PRAGMA foreign_keys=OFF;");
        var missingTables = Names(
            connection,
            """
            SELECT name
            FROM sqlite_schema
            WHERE type='table'
              AND name NOT LIKE 'sqlite_%'
              AND name NOT IN (
                'schema_version',
                'sessions',
                'alert_evaluations',
                'alert_receipts',
                'alert_suppressions',
                'runtime_backup_receipts')
            ORDER BY name;
            """);
        Assert.NotEmpty(missingTables);
        foreach (var table in missingTables)
            Execute(connection, $"DROP TABLE \"{table}\";");
        Assert.False(SqliteSessionStore.IsCurrentSchemaValid(connection, null));

        using var transaction = connection.BeginTransaction(deferred: false);
        Assert.Throws<InvalidOperationException>(() => PricingSchemaV1.Ensure(connection, transaction));
        transaction.Rollback();

        Assert.Empty(Names(connection, "SELECT name FROM sqlite_schema WHERE name GLOB 'pricing_*';"));
        Assert.Equal(
            1L,
            Scalar<long>(
                connection,
                "SELECT COUNT(*) FROM schema_version WHERE component='session' AND version=13;"));
    }

    [Theory]
    [MemberData(nameof(SupportedHistoricalSessionFixtures))]
    public void PricingSchemaV1_Ensure_AcceptsEverySupportedHistoricalSessionWholeProfile(
        string fixtureFile)
    {
        using var database = new PricingDatabase();
        var fixturePath = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "SchemaMigrations",
            "session",
            fixtureFile);
        File.Copy(fixturePath, database.Path);

        new SqliteSessionStore(database.Path).CreateSchema();
        using (var migratedConnection = database.Open())
            Assert.True(
                SqliteSessionStore.IsCurrentSchemaValid(migratedConnection, null),
                $"{fixtureFile} was rejected immediately after the supported Session migration.");

        database.CreateDependencies();

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        Assert.True(SqliteSessionStore.IsCurrentSchemaValid(connection, transaction));
        PricingSchemaV1.Ensure(connection, transaction);
        transaction.Commit();
        Assert.True(PricingSchemaV1.IsValid(connection, null));
    }

    [Theory]
    [InlineData("configuration_id")]
    [InlineData("uuid_v7_variant")]
    [InlineData("idempotency_key")]
    [InlineData("estimate_token")]
    [InlineData("estimate_decimal")]
    public void PricingSchemaV1_ChecksRejectNoncanonicalScalarForms(string caseName)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys=OFF;");
        var tableName = caseName == "configuration_id"
            ? "pricing_configuration_previews"
            : caseName is "uuid_v7_variant" or "idempotency_key"
                ? "pricing_recalculation_runs"
                : "pricing_estimates";
        Execute(
            connection,
            Assert.Single(
                PricingSchemaV1.OwnedObjects,
                item => item.Type == "table" && item.Name == tableName).Sql);

        var invalidSql = caseName switch
        {
            "configuration_id" =>
                $"""
                INSERT INTO pricing_configuration_previews(
                    preview_digest,canonical_sha256,canonical_blob,configuration_id,
                    expected_head_revision,expected_configuration_id,catalog_sha256,
                    selection_digest,created_at_utc,expires_at_utc)
                VALUES(
                    '{new string('a', 64)}','{new string('b', 64)}',X'01',
                    'cost-configuration-{new string('g', 64)}',0,NULL,
                    '{new string('c', 64)}','{new string('d', 64)}',
                    '2026-07-24T00:00:00.0000000+00:00',
                    '2026-07-24T00:01:00.0000000+00:00');
                """,
            "uuid_v7_variant" => RecalculationRunInsert(
                "018f6f58-5f41-7a3f-0b0e-123456789abc",
                "pricing-check-0001"),
            "idempotency_key" => RecalculationRunInsert(
                "018f6f58-5f41-7a3f-8b0e-123456789abc",
                "_pricing-check-01"),
            "estimate_token" => EstimateInsert("_invalid", "1"),
            "estimate_decimal" => EstimateInsert("github_copilot", "01.0"),
            _ => throw new InvalidOperationException(),
        };

        Assert.Throws<SqliteException>(() => Execute(connection, invalidSql));
    }

    [Fact]
    public void PricingSchemaV1_ExactSqliteManifestMatchesIndependentGolden()
    {
        const string golden =
            """
            index|pricing_estimates_analytics_idx|pricing_estimates|aa455d84badf8abb3943062406eb0c446f78dc2d5a452926d696db24a2f72ad8
            index|pricing_recalculation_budget_alert_idx|pricing_recalculation_budget_results|50748d50854820706b16d320329a1f9ebb659c6ca28fce90038eff173fb4903e
            index|pricing_recalculation_events_kind_idx|pricing_recalculation_events|bc334e56834e6bf4f2dce0447f82d55f33b3b8199b93172c349122f55fcf30bd
            index|pricing_recalculation_runs_recovery_idx|pricing_recalculation_runs|441abcb51055c77d1bfd1d87945587b83df9921bf22dd2f6585679d9898fda8e
            index|pricing_recalculation_targets_session_idx|pricing_recalculation_targets|07eb5e454159b5fb5b0cfa54c6b08b371ccf520470fea28945c759926d0aaaea
            table|pricing_catalog_snapshots|pricing_catalog_snapshots|f86834b87542f3ea20a347eb09f13a347b792bbe3a15d953cb0f23fd85732d29
            table|pricing_configuration_commits|pricing_configuration_commits|6fac225b02291865e04f3cc3e3bec24a6b77a44f06137dc7b0b3bd55009f0d20
            table|pricing_configuration_heads|pricing_configuration_heads|812924e6eba9b4800d56addca1c652b5cd66e0c54a8019773629fcde035af719
            table|pricing_configuration_previews|pricing_configuration_previews|f7dc34c4ceb80d3c1ad97004c2e480c7c426c0afdaa2b37490b1531fa9debc79
            table|pricing_configurations|pricing_configurations|3d422462b700c4459e09430559467c250ec575003c9cf2c7bbf21eddc6c5fa50
            table|pricing_estimate_heads|pricing_estimate_heads|1ff1ee6b5b3361ae2fb58e9691db94565aa4fb392b8b3c2de6fb48d70d90e785
            table|pricing_estimates|pricing_estimates|7064cd735f58681dc7b23f3a511d35132b79b4a293a4f3bb4d7afe46e6420204
            table|pricing_recalculation_budget_results|pricing_recalculation_budget_results|f6f4f284512812838b9fbf9656bb34c180f01831cb136fa1aff548d80dc69490
            table|pricing_recalculation_events|pricing_recalculation_events|d022c10c2a0ebb353186942964ba5623c539d2ddd0f8c9afd26fb59c237a2cbc
            table|pricing_recalculation_runs|pricing_recalculation_runs|47454e3b694c8427fcf74004054316d4274572aa6d1fa32e19fa2e7f5da2acee
            table|pricing_recalculation_target_results|pricing_recalculation_target_results|f3bb27de48ad880d9dc74f04bf252447f216c22e2befb97207776b70906a9048
            table|pricing_recalculation_targets|pricing_recalculation_targets|5865e4d865eea88c695172466ce07d87275efc893fd3004ff401c6c9e2f8cc48
            table|pricing_session_attempts|pricing_session_attempts|0c7d649bbd2b1bea65658183ca56310cdfe767734e2d7b5f002bcb6ca35e9c3a
            trigger|pricing_catalog_snapshots_no_delete|pricing_catalog_snapshots|a90f9787efc389ab099c606398fe1df9ac0c000734993d8a857c592dbb3f23cb
            trigger|pricing_catalog_snapshots_no_replace|pricing_catalog_snapshots|f51b7bab211489d71341076e810b1039f6640f76f1547784068056e2605ced84
            trigger|pricing_catalog_snapshots_no_update|pricing_catalog_snapshots|8298f19cfbe51110b1a4392b905d1c257434aacfe03dc28ea2571f6416cb4d5c
            trigger|pricing_configuration_commits_no_delete|pricing_configuration_commits|ec921ad3cfcc00dc7659915ff0c942c67ae9ad6b39bfe68fd4829c1b7e4df388
            trigger|pricing_configuration_commits_no_replace|pricing_configuration_commits|3c46c90d66ea4f62da252ca13027278c447d12e27a14ecd0f8e0153f04b525bf
            trigger|pricing_configuration_commits_no_update|pricing_configuration_commits|09c5bd0b32c1a30ea2f23a2fa43cf04ba50aeaf3089003adc2651c428b9118e6
            trigger|pricing_configuration_heads_contiguous_insert|pricing_configuration_heads|eb0c87139bfed5037ba82c963266186c1d86e5b9463bd192e34e092901f02643
            trigger|pricing_configuration_heads_no_delete|pricing_configuration_heads|1e15758c98ce9b05ea161f117faaac20caac9de2a6b6605ad467f79794d8a37b
            trigger|pricing_configuration_heads_no_replace|pricing_configuration_heads|3fc277669decb028986a62f811801e955e03b3c56493c49367ad2e0f2567baec
            trigger|pricing_configuration_heads_no_update|pricing_configuration_heads|73516d2436e1f8d9b7cfabe14713c5656a23acac7ab34b2116010f535f532d02
            trigger|pricing_configuration_previews_no_replace|pricing_configuration_previews|5f4b41e3b6d7d2ea49099a10ed56502644b5dcbe1df517ce65c3c696ce6c8b98
            trigger|pricing_configuration_previews_no_update|pricing_configuration_previews|a7195f8c8246cf334ff4dcbcb32528d5f3fcf2c1a882d3de38830ce8c76d0fc8
            trigger|pricing_configurations_no_delete|pricing_configurations|434a6e28fb43683440918cd42756f823d610262f22c8ba1142d82ad2a2e8ff59
            trigger|pricing_configurations_no_replace|pricing_configurations|a3a323114b0cb7abbf8f678c9a86a958fd07a2f043513ef4c13865ead1d3f70a
            trigger|pricing_configurations_no_update|pricing_configurations|9342b10eed6b6634a4d064ebadc9042f46b57ee9e561ec0338b40acc5246670d
            trigger|pricing_estimate_heads_contiguous_insert|pricing_estimate_heads|ae1b626055968e38c936506e1401252f32176b359ae16d030781cae27c07ef50
            trigger|pricing_estimate_heads_no_delete|pricing_estimate_heads|c19be37457879cb06cb644300d3921e3861d1f751f6285ea27b945d92ba1fe13
            trigger|pricing_estimate_heads_no_replace|pricing_estimate_heads|105d17efa153a8eebf8adeb7662c08458254c25d8f87086b75226f92f9814c38
            trigger|pricing_estimate_heads_no_update|pricing_estimate_heads|56221791385805a45e1caf5f2477a1010eb6f7372a93c2fd753d797558c450d7
            trigger|pricing_estimates_no_delete|pricing_estimates|69a559c30351ffe713bb3b9e2b55e400a099832f670abf6ae37b9d103ad95c9c
            trigger|pricing_estimates_no_replace|pricing_estimates|ee9290c3f29b2d6cafb448985037815b21b62a49aa25e094462a3a4108003122
            trigger|pricing_estimates_no_update|pricing_estimates|c5e8db7e0181ee2f17a5ef3236229b827d909a08827972c8be723a3fa7fde397
            trigger|pricing_recalculation_budget_results_contiguous_insert|pricing_recalculation_budget_results|d0221d1e25f832e9d529e555cbc66713cffdb1461294529b4119c7e84cd2fc5b
            trigger|pricing_recalculation_budget_results_no_delete|pricing_recalculation_budget_results|6c0d89204d6748623dbd3645a7c4188c5d9f4fef8c016420803bea331be7f90d
            trigger|pricing_recalculation_budget_results_no_replace|pricing_recalculation_budget_results|45141c4135c2c4b08d088e04cab7fc047302e96de07faa93555926a481b63345
            trigger|pricing_recalculation_budget_results_no_update|pricing_recalculation_budget_results|54aebecdd94b5d3b12fbf0762b7309546f9873cb5a13a3efb96bee259cba691a
            trigger|pricing_recalculation_events_contiguous_insert|pricing_recalculation_events|f7b2e2386576153a6aebd81a30cb0fc8fe48d7275327dc9bad45e9cd76344b78
            trigger|pricing_recalculation_events_no_delete|pricing_recalculation_events|1a30d8edf3f31630aaaa7c99ed0a27daf0ef8a4b238c078835d9da8c57f0515b
            trigger|pricing_recalculation_events_no_replace|pricing_recalculation_events|0ac0c05f34d26dd5bda11f6b8903a29ecc685402e3823bc9695153520d9e69b0
            trigger|pricing_recalculation_events_no_update|pricing_recalculation_events|4aaa6bfcacec6c0517d35d1e9bdbe49b5c11b951a7f7afc1af0b820307592140
            trigger|pricing_recalculation_runs_no_delete|pricing_recalculation_runs|f975c16ec469cab6c9f861c1511140281557e75a7c22594804811cc313e9f53b
            trigger|pricing_recalculation_runs_no_replace|pricing_recalculation_runs|065045b5bd771535936a773811cc26faf6efd152bb4d56df845d1405b1313b56
            trigger|pricing_recalculation_runs_no_update|pricing_recalculation_runs|ee62db94d323527e9ccab1503e3572a41e0c5516c3c14e2f678869f1bc01a517
            trigger|pricing_recalculation_target_results_no_delete|pricing_recalculation_target_results|aea6d10dde7176a770f29af8f5a3b3b2a617eef4ed61223568c90855b6146a0e
            trigger|pricing_recalculation_target_results_no_replace|pricing_recalculation_target_results|36ea21809858626ad83f5f96244f7153df9b656eabe5b81baa35dece5021dfd1
            trigger|pricing_recalculation_target_results_no_update|pricing_recalculation_target_results|3b4a4a2b114623f1e3f0453312cddfd3c985f413e2156b7796ef7b92ed1136cf
            trigger|pricing_recalculation_targets_contiguous_insert|pricing_recalculation_targets|a5e8749bf707ceea7e5cd024aa7b44365ed257a3ade0d3486d908645f2d5f185
            trigger|pricing_recalculation_targets_no_delete|pricing_recalculation_targets|d37437d18b12e55dd27bc3ca6d8fe3618f37eca3b4fbc013531f92d47dee50f7
            trigger|pricing_recalculation_targets_no_replace|pricing_recalculation_targets|c21409712f704d8cc954021c57e1a48ba64e10f17eb0fe4022d765bb693a4d9a
            trigger|pricing_recalculation_targets_no_update|pricing_recalculation_targets|85e4376d0f1a60a3c1d8d75169333ec86f0151b3ac11305ece11df422cf8b7fd
            trigger|pricing_session_attempts_contiguous_insert|pricing_session_attempts|c3dc98c8e9d0adb5d797ab333d81e0eb083737af4ae175fcda5dfce3a4d37b02
            trigger|pricing_session_attempts_no_delete|pricing_session_attempts|2d1df672d24c10b0076fa79648fa916c72bdd422b4664bdcd09d54c331d4b231
            trigger|pricing_session_attempts_no_replace|pricing_session_attempts|b40ab1c5e742098276634a5128fceeb06083c7198cc2259f5c21e72baaca1f60
            trigger|pricing_session_attempts_no_update|pricing_session_attempts|0aa67448dddd0ea24ab2d2d1414f196b44e092c8e99dd91ba0990b2e958c2825
            """;
        using var database = new PricingDatabase();
        database.CreateDependencies();
        new SqlitePricingStore(database.Path).CreateSchema();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT type,name,tbl_name,sql
            FROM sqlite_schema
            WHERE name GLOB 'pricing_*'
            ORDER BY type,name;
            """;
        using var reader = command.ExecuteReader();
        var actual = new List<string>();
        while (reader.Read())
        {
            var normalized = string.Join(
                ' ',
                reader.GetString(3).Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';');
            var hash = Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
            actual.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}|{hash}");
        }
        Assert.Equal(
            golden.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            actual);
    }

    [Fact]
    public void PricingRowValidatorV1_StreamsLargeValidRunHistory()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var calculationTime = new DateTimeOffset(2026, 7, 25, 4, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(calculationTime.AddHours(-1));
        var store = new SqlitePricingStore(database.Path, clock);
        store.CreateSchema();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        Assert.Equal(
            PricingStoreStatus.Success,
            store.PutCatalogSnapshot(PricingCanonicalJson.SerializeCatalogSnapshot(catalog)).Status);
        var configuration = CostConfigurationCanonicalJsonV1.Create(
            null,
            catalog.CatalogSha256,
            [],
            [],
            calculationTime.AddHours(-1));
        var preview = CostConfigurationPreviewCanonicalJsonV1.Create(
            configuration,
            0,
            null,
            catalog.CatalogSha256,
            PricingConfigurationSelectionDigestV1.Create([]),
            0,
            0,
            "exact",
            0,
            "exact");
        Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(preview).Status);
        clock.UtcNow = calculationTime.AddMinutes(-59);
        Assert.Equal(PricingStoreStatus.Success, AppendIncompleteConfigurationCommit(store, preview).Status);

        using (var connection = database.Open())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            for (var index = 0; index < 512; index++)
            {
                var sessionId = Guid.NewGuid().ToString("D");
                var runId = Guid.CreateVersion7().ToString("D");
                var request = CostRecalculationRequestCanonicalJsonV1.Create(
                    configuration.ConfigurationId,
                    1,
                    catalog.CatalogSha256,
                    [sessionId],
                    [],
                    $"pricing-stream-{index:0000}");
                var requestBytes = CostRecalculationRequestCanonicalJsonV1.Serialize(request);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO sessions(
                        session_id,status,completeness,last_seen_at,raw_retention_state,
                        created_at,updated_at)
                    VALUES(
                        $session,'completed','full',$effective,'not_captured',
                        $effective,$updated);
                    INSERT INTO pricing_recalculation_runs(
                        run_id,request_schema_version,idempotency_key,request_digest,
                        canonical_request_blob,configuration_id,configuration_head_revision,
                        catalog_sha256,calculation_time_utc,target_count,scope_count,created_at_utc)
                    VALUES(
                        $run,'cost.recalculation-request.v1',$key,$digest,$request,
                        $configuration,1,$catalog,$calculation,1,0,$calculation);
                    INSERT INTO pricing_recalculation_targets(
                        run_id,target_ordinal,session_id,session_status,
                        session_effective_at_utc,session_updated_at_utc,
                        source_partition_state,source_partition_count,
                        source_partition_digest,source_surface,source_application_version,
                        base_head_revision,base_estimate_id,base_attempt_revision)
                    VALUES(
                        $run,0,$session,'completed',$effective,$updated,'missing',0,
                        $partition,NULL,NULL,NULL,NULL,0);
                    INSERT INTO pricing_recalculation_events(
                        run_id,event_sequence,event_kind,occurred_at_utc,failure_phase,
                        failure_ordinal_kind,failure_ordinal,failure_code)
                    VALUES($run,0,'requested',$calculation,NULL,NULL,NULL,NULL);
                    """;
                command.Parameters.AddWithValue("$session", sessionId);
                command.Parameters.AddWithValue("$run", runId);
                command.Parameters.AddWithValue("$key", request.IdempotencyKey);
                command.Parameters.AddWithValue(
                    "$digest",
                    CostIdentityV1.Hash("cost-recalculation-request/v1", requestBytes));
                command.Parameters.AddWithValue("$request", requestBytes);
                command.Parameters.AddWithValue("$configuration", configuration.ConfigurationId);
                command.Parameters.AddWithValue("$catalog", catalog.CatalogSha256);
                command.Parameters.AddWithValue(
                    "$calculation",
                    "2026-07-25T04:00:00.0000000+00:00");
                command.Parameters.AddWithValue(
                    "$effective",
                    "2026-07-25T03:00:00.0000000+00:00");
                command.Parameters.AddWithValue(
                    "$updated",
                    "2026-07-25T03:30:00.0000000+00:00");
                command.Parameters.AddWithValue("$partition", new string('d', 64));
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        using var read = database.Open();
        Assert.Equal(512L, Scalar<long>(read, "SELECT COUNT(*) FROM pricing_recalculation_runs;"));
        Assert.True(PricingSchemaV1.ValidateRows(read, null));
    }

    [Fact]
    public void PricingSchemaV1_Ensure_AcceptsRealFullSessionV13AndReopensIdempotently()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var sessionId = Guid.NewGuid().ToString("D");
        using (var connection = database.Open())
        {
            InsertSession(connection, sessionId);
            using var transaction = connection.BeginTransaction(deferred: false);
            Assert.True(SqliteSessionStore.IsCurrentSchemaValid(connection, transaction));
            PricingSchemaV1.Ensure(connection, transaction);
            transaction.Commit();
        }

        using (var connection = database.Open())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            PricingSchemaV1.Ensure(connection, transaction);
            transaction.Commit();
        }

        using var read = database.Open();
        Assert.True(PricingSchemaV1.IsValid(read, null));
        Assert.True(PricingSchemaV1.ValidateRows(read, null));
        Assert.Equal(1L, Scalar<long>(read, "SELECT COUNT(*) FROM sessions;"));
    }

    [Fact]
    public void SqlitePricingStore_PutCatalogSnapshot_PreservesExactCanonicalBytesAndFirstTimestamp()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        var canonical = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
        var first = new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero);
        var clock = new MutableTimeProvider(first);
        var store = new SqlitePricingStore(database.Path, clock);
        store.CreateSchema();

        var initial = store.PutCatalogSnapshot(canonical);
        clock.UtcNow = first.AddDays(1);
        var replay = store.PutCatalogSnapshot(canonical);
        var loaded = store.GetCatalogSnapshot(catalog.CatalogSha256);

        Assert.Equal(PricingStoreStatus.Success, initial.Status);
        Assert.Equal(PricingStoreStatus.Success, replay.Status);
        Assert.NotNull(loaded);
        Assert.Equal(canonical, loaded.CanonicalBytes);
        Assert.Equal(first, loaded.FirstRecordedAtUtc);
        Assert.Equal(1, loaded.DocumentCount);
    }

    [Fact]
    public void SqlitePricingStore_PutCatalogSnapshot_FreezesCallerBytesBeforeConcurrentMutation()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        var callerBytes = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
        var expectedBytes = callerBytes.ToArray();
        var recordedAt = new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero);
        var clock = new CallbackTimeProvider(
            recordedAt,
            () => Task.Run(() => callerBytes[0] = (byte)'[').GetAwaiter().GetResult());
        var store = new SqlitePricingStore(database.Path, clock);
        store.CreateSchema();

        var result = store.PutCatalogSnapshot(callerBytes);
        var loaded = store.GetCatalogSnapshot(catalog.CatalogSha256);

        Assert.Equal(PricingStoreStatus.Success, result.Status);
        Assert.NotNull(loaded);
        Assert.Equal(expectedBytes, loaded.CanonicalBytes);
    }

    [Fact]
    public void SqlitePricingStore_PutCatalogSnapshot_RejectsHashCollisionWithoutMutation()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero));
        var store = new SqlitePricingStore(database.Path, clock);
        store.CreateSchema();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        var canonical = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
        Assert.Equal(PricingStoreStatus.Success, store.PutCatalogSnapshot(canonical).Status);
        using (var connection = database.Open())
        {
            Execute(connection, "DROP TRIGGER pricing_catalog_snapshots_no_update;");
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE pricing_catalog_snapshots SET canonical_blob=$blob WHERE catalog_sha256=$sha;";
            command.Parameters.AddWithValue("$blob", canonical.Concat(new byte[] { 0x20 }).ToArray());
            command.Parameters.AddWithValue("$sha", catalog.CatalogSha256);
            command.ExecuteNonQuery();
        }

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var result = store.PutCatalogSnapshot(canonical);

        Assert.Equal(PricingStoreStatus.Unavailable, result.Status);
        Assert.Null(store.GetCatalogSnapshot(catalog.CatalogSha256));
    }

    [Fact]
    public void CostConfigurationConsumerV1_ReloadsOnlyCanonicalIdentityBoundBytes()
    {
        var catalogSha = new string('a', 64);
        var createdAt = new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero);
        var configuration = CostConfigurationCanonicalJsonV1.Create(
            predecessorConfigurationId: null,
            catalogSha,
            [
                new("github-copilot-vscode", "1.2.3", "pricing-capability.v1", "github_copilot", "github_ai_credits", "credit_consuming_interaction"),
            ],
            [
                new("session-estimated-cost-threshold", "1", false, "USD", "1.25", "2.5", 7500, "session", null),
            ],
            createdAt);
        var canonical = CostConfigurationCanonicalJsonV1.Serialize(configuration);

        var result = CostConfigurationConsumerV1.Consume(canonical);
        canonical[0] = (byte)'[';

        Assert.Equal(CostConsumerStatus.Success, result.Status);
        Assert.NotNull(result.Value);
        Assert.Equal(configuration.ConfigurationId, result.Value.ConfigurationId);
        Assert.Equal("github-copilot-vscode", Assert.Single(result.Value.SourceEntries).SourceSurface);
        Assert.Equal("session-estimated-cost-threshold", Assert.Single(result.Value.BudgetEntries).RuleId);
        Assert.Equal(createdAt, result.Value.CreatedAtUtc);
    }

    [Fact]
    public void CostConfigurationConsumerV1_RejectsUnknownDuplicateAndFutureSchema()
    {
        var canonical = CostConfigurationCanonicalJsonV1.Serialize(
            CostConfigurationCanonicalJsonV1.Create(
                null,
                new string('a', 64),
                [],
                [],
                new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero)));
        var json = System.Text.Encoding.UTF8.GetString(canonical);

        Assert.Equal(
            CostConsumerStatus.Invalid,
            CostConfigurationConsumerV1.Consume(System.Text.Encoding.UTF8.GetBytes(json.Replace(
                "\"configuration_id\"",
                "\"unexpected\":true,\"configuration_id\"",
                StringComparison.Ordinal))).Status);
        Assert.Equal(
            CostConsumerStatus.Invalid,
            CostConfigurationConsumerV1.Consume(System.Text.Encoding.UTF8.GetBytes(json.Replace(
                "\"catalog_sha256\"",
                "\"catalog_sha256\":\"" + new string('a', 64) + "\",\"catalog_sha256\"",
                StringComparison.Ordinal))).Status);
        Assert.Equal(
            CostConsumerStatus.Unsupported,
            CostConfigurationConsumerV1.Consume(System.Text.Encoding.UTF8.GetBytes(json.Replace(
                "cost.configuration.v1",
                "cost.configuration.v2",
                StringComparison.Ordinal))).Status);
    }

    [Fact]
    public void CostRecalculationRequestCanonicalJsonV1_RejectsDuplicateScopes()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var scope = new CostBudgetScopeV1("session", sessionId, null, null, null);

        Assert.Throws<ArgumentException>(() => CostRecalculationRequestCanonicalJsonV1.Create(
            "cost-configuration-" + new string('a', 64),
            1,
            new string('b', 64),
            [sessionId],
            [scope, scope],
            "pricing-request-test-0001"));
    }

    [Fact]
    public void CostV1Consumers_ClassifyOnlyExactRecognizedFutureFamiliesAsUnsupported()
    {
        AssertConsumerVersionClassification(
            CostRecalculationRequestCanonicalJsonV1.Consume,
            "cost.recalculation-request");
        AssertConsumerVersionClassification(
            CostConfigurationConsumerV1.Consume,
            "cost.configuration");
        AssertConsumerVersionClassification(
            CostConfigurationPreviewConsumerV1.Consume,
            "cost.configuration-preview");
    }

    [Fact]
    public void CostConfigurationPreviewCanonicalJsonV1_RejectsInconsistentBoundedCounts()
    {
        var configuration = CostConfigurationCanonicalJsonV1.Create(
            null,
            new string('a', 64),
            [],
            [],
            new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero));

        Assert.Throws<ArgumentException>(() => CostConfigurationPreviewCanonicalJsonV1.Create(
            configuration,
            0,
            null,
            configuration.CatalogSha256,
            new string('b', 64),
            1,
            2001,
            "exact",
            0,
            "exact"));
        Assert.Throws<ArgumentException>(() => CostConfigurationPreviewCanonicalJsonV1.Create(
            configuration,
            0,
            null,
            configuration.CatalogSha256,
            new string('b', 64),
            1,
            2001,
            "lower_bound",
            1,
            "exact"));
    }

    [Fact]
    public void CostConfigurationCommitConsumerV1_ChangesOnlyTheRootSchemaToken()
    {
        var configuration = CostConfigurationCanonicalJsonV1.Create(
            null,
            new string('a', 64),
            [new("surface", "cost.configuration-preview.v1", "capability.v1", "provider", "billing", "route")],
            [],
            new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero));
        var preview = CostConfigurationPreviewCanonicalJsonV1.Create(
            configuration,
            0,
            null,
            configuration.CatalogSha256,
            new string('b', 64),
            1,
            0,
            "exact",
            0,
            "exact");

        var canonical = CostConfigurationCommitConsumerV1.SerializeRequest(preview);
        var consumed = CostConfigurationCommitConsumerV1.ConsumeRequest(canonical);

        Assert.Contains(
            "\"application_version\":\"cost.configuration-preview.v1\"",
            System.Text.Encoding.UTF8.GetString(canonical),
            StringComparison.Ordinal);
        Assert.Equal(CostConsumerStatus.Success, consumed.Status);
        Assert.Equal(
            "cost.configuration-preview.v1",
            Assert.Single(consumed.Value!.Configuration.SourceEntries).ApplicationVersion);
    }

    [Fact]
    public void CostConfigurationCommitConsumerV1_ClassifiesOnlyRecognizedFutureRootsAsUnsupported()
    {
        Assert.Equal(
            CostConsumerStatus.Invalid,
            CostConfigurationCommitConsumerV1.ConsumeRequest("{}"u8.ToArray()).Status);
        Assert.Equal(
            CostConsumerStatus.Invalid,
            CostConfigurationCommitConsumerV1.ConsumeRequest(
                """{"schema_version":"other.contract.v2"}"""u8.ToArray()).Status);
        Assert.Equal(
            CostConsumerStatus.Unsupported,
            CostConfigurationCommitConsumerV1.ConsumeRequest(
                """{"schema_version":"cost.configuration-commit.v2"}"""u8.ToArray()).Status);

        Assert.Equal(
            CostConsumerStatus.Invalid,
            CostConfigurationCommitConsumerV1.ConsumeResult("{}"u8.ToArray()).Status);
        Assert.Equal(
            CostConsumerStatus.Invalid,
            CostConfigurationCommitConsumerV1.ConsumeResult(
                """{"schema_version":"other.contract.v2"}"""u8.ToArray()).Status);
        Assert.Equal(
            CostConsumerStatus.Unsupported,
            CostConfigurationCommitConsumerV1.ConsumeResult(
                """{"schema_version":"cost.configuration-commit-result.v2"}"""u8.ToArray()).Status);
    }

    [Fact]
    public void SqlitePricingStore_PutConfigurationPreview_EnforcesFifteenMinuteTtlAndCapacity()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var createdAt = new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero);
        var clock = new MutableTimeProvider(createdAt);
        var store = new SqlitePricingStore(database.Path, clock);
        store.CreateSchema();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        var catalogBytes = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
        Assert.Equal(PricingStoreStatus.Success, store.PutCatalogSnapshot(catalogBytes).Status);

        for (var index = 0; index < 32; index++)
        {
            var preview = CreatePreview(catalog.CatalogSha256, createdAt, index);
            Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(preview).Status);
        }

        var overflow = CreatePreview(catalog.CatalogSha256, createdAt.AddSeconds(1), 32);
        Assert.Equal(PricingStoreStatus.CapacityReached, store.PutConfigurationPreview(overflow).Status);

        clock.UtcNow = createdAt.AddMinutes(15);
        var afterExpiry = CreatePreview(catalog.CatalogSha256, createdAt.AddMinutes(15), 33);
        Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(afterExpiry).Status);
        Assert.Equal(1, store.CountConfigurationPreviews());
    }

    [Fact]
    public void SqlitePricingStore_CommitConfiguration_AppendsImmutableHeadReceiptAndConsumesPreviewAtomically()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var createdAt = new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero);
        var clock = new MutableTimeProvider(createdAt);
        var store = new SqlitePricingStore(database.Path, clock);
        store.CreateSchema();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        Assert.Equal(
            PricingStoreStatus.Success,
            store.PutCatalogSnapshot(PricingCanonicalJson.SerializeCatalogSnapshot(catalog)).Status);
        var preview = CreatePreview(
            catalog.CatalogSha256,
            createdAt,
            1);
        Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(preview).Status);

        var committedAt = new DateTimeOffset(2026, 7, 24, 1, 3, 0, TimeSpan.Zero);
        clock.UtcNow = committedAt;
        var initial = AppendIncompleteConfigurationCommit(store, preview);
        clock.UtcNow = committedAt.AddDays(1);
        var replay = AppendIncompleteConfigurationCommit(
            store,
            preview,
            new PricingProviderCatalogWrite(
                new string('f', 64),
                "not-a-catalog"u8.ToArray()),
            [CreateSelectionFact()]);

        Assert.Equal(PricingStoreStatus.Success, initial.Status);
        Assert.Equal(PricingStoreStatus.Success, replay.Status);
        Assert.Equal(preview.Configuration.ConfigurationId, initial.Value!.ConfigurationId);
        Assert.Equal(1, initial.Value.HeadRevision);
        Assert.Equal(initial.Value, replay.Value);
        Assert.Equal(0, store.CountConfigurationPreviews());
        using var connection = database.Open();
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM pricing_configurations;"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM pricing_configuration_heads;"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM pricing_configuration_commits;"));
        Assert.Throws<SqliteException>(() => Execute(
            connection,
            "UPDATE pricing_configuration_heads SET committed_at_utc=committed_at_utc;"));
    }

    [Fact]
    public void SqlitePricingStore_IncompleteConfigurationAppend_AtomicallyBindsProviderCatalogAndSelection()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var createdAt = new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero);
        var store = new SqlitePricingStore(database.Path, new MutableTimeProvider(createdAt.AddMinutes(1)));
        store.CreateSchema();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        var catalogBytes = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
        var preview = CreatePreview(catalog.CatalogSha256, createdAt, 1);
        Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(preview).Status);
        using (var before = database.Open())
            Assert.Equal(0L, Scalar<long>(before, "SELECT COUNT(*) FROM pricing_catalog_snapshots;"));

        var wrongSelection = store.AppendConfigurationCommitIncomplete(
            preview,
            new PricingProviderCatalogWrite(catalog.CatalogSha256, catalogBytes),
            [CreateSelectionFact()]);

        Assert.Equal(PricingStoreStatus.Conflict, wrongSelection.Status);
        using (var afterConflict = database.Open())
        {
            Assert.Equal(0L, Scalar<long>(afterConflict, "SELECT COUNT(*) FROM pricing_catalog_snapshots;"));
            Assert.Equal(0L, Scalar<long>(afterConflict, "SELECT COUNT(*) FROM pricing_configuration_heads;"));
        }

        var result = store.AppendConfigurationCommitIncomplete(
            preview,
            new PricingProviderCatalogWrite(catalog.CatalogSha256, catalogBytes),
            []);

        Assert.Equal(PricingStoreStatus.Success, result.Status);
        using var after = database.Open();
        Assert.Equal(1L, Scalar<long>(after, "SELECT COUNT(*) FROM pricing_catalog_snapshots;"));
        Assert.Equal(catalog.CatalogSha256, Scalar<string>(after, "SELECT catalog_sha256 FROM pricing_catalog_snapshots;"));
        Assert.Equal(1L, Scalar<long>(after, "SELECT COUNT(*) FROM pricing_configuration_heads;"));
    }

    [Fact]
    public void SqlitePricingStore_DoesNotExposeIncompleteConfigurationOrCompletionAsPublicAdmission()
    {
        var publicNames = typeof(SqlitePricingStore)
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain("CommitConfiguration", publicNames);
        Assert.DoesNotContain("CompleteRecalculation", publicNames);
        Assert.DoesNotContain("AppendEstimateSuccess", publicNames);
        Assert.DoesNotContain("StartRecalculation", publicNames);
        Assert.DoesNotContain(
            typeof(SqlitePricingStore).Assembly.GetExportedTypes(),
            type => type.Name.Contains("PricingRecalculationTarget", StringComparison.Ordinal));
    }

    [Fact]
    public void SqlitePricingStore_StartRecalculationIncomplete_RejectsHistoricalConfigurationHead()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var createdAt = new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero);
        var clock = new MutableTimeProvider(createdAt);
        var store = new SqlitePricingStore(database.Path, clock);
        store.CreateSchema();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        Assert.Equal(
            PricingStoreStatus.Success,
            store.PutCatalogSnapshot(PricingCanonicalJson.SerializeCatalogSnapshot(catalog)).Status);

        var firstPreview = CreatePreview(catalog.CatalogSha256, createdAt, 1);
        Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(firstPreview).Status);
        clock.UtcNow = createdAt.AddMinutes(1);
        Assert.Equal(
            PricingStoreStatus.Success,
            AppendIncompleteConfigurationCommit(store, firstPreview).Status);

        var sessionId = Guid.NewGuid().ToString("D");
        using (var connection = database.Open())
            InsertSession(connection, sessionId);
        var initialRequest = CostRecalculationRequestCanonicalJsonV1.Create(
            firstPreview.Configuration.ConfigurationId,
            1,
            catalog.CatalogSha256,
            [sessionId],
            [],
            "pricing-stale-head-0001");
        var target = new PricingRecalculationTargetWriteIncomplete(
            sessionId,
            "completed",
            createdAt,
            createdAt.AddMinutes(1),
            "missing",
            0,
            new string('d', 64),
            null,
            null,
            null,
            null,
            0);
        Assert.Equal(
            PricingStoreStatus.Success,
            store.StartRecalculationIncomplete(
                Guid.CreateVersion7().ToString("D"),
                initialRequest,
                [target],
                createdAt.AddMinutes(1)).Status);

        var secondConfiguration = CostConfigurationCanonicalJsonV1.Create(
            firstPreview.Configuration.ConfigurationId,
            catalog.CatalogSha256,
            [new("surface", "1.0.2", "capability.v1", "provider", "billing", "route")],
            [],
            createdAt.AddMinutes(2));
        var secondPreview = CostConfigurationPreviewCanonicalJsonV1.Create(
            secondConfiguration,
            1,
            firstPreview.Configuration.ConfigurationId,
            catalog.CatalogSha256,
            PricingConfigurationSelectionDigestV1.Create([]),
            0,
            0,
            "exact",
            0,
            "exact");
        Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(secondPreview).Status);
        clock.UtcNow = createdAt.AddMinutes(3);
        Assert.Equal(
            PricingStoreStatus.Success,
            AppendIncompleteConfigurationCommit(store, secondPreview).Status);

        var historicalRequest = CostRecalculationRequestCanonicalJsonV1.Create(
            firstPreview.Configuration.ConfigurationId,
            1,
            catalog.CatalogSha256,
            [sessionId],
            [],
            "pricing-stale-head-0002");

        var replay = store.StartRecalculationIncomplete(
            Guid.CreateVersion7().ToString("D"),
            initialRequest,
            [target],
            createdAt.AddMinutes(4));
        var fresh = store.StartRecalculationIncomplete(
            Guid.CreateVersion7().ToString("D"),
            historicalRequest,
            [target],
            createdAt.AddMinutes(4));

        Assert.Equal(PricingStoreStatus.Success, replay.Status);
        Assert.Equal(PricingStoreStatus.Conflict, fresh.Status);
    }

    [Fact]
    public void SqlitePricingStore_RecoverInterruptedRuns_AppendsFailedResultEventAndAttempt()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var createdAt = new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero);
        var clock = new MutableTimeProvider(createdAt);
        var store = new SqlitePricingStore(database.Path, clock);
        store.CreateSchema();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        Assert.Equal(
            PricingStoreStatus.Success,
            store.PutCatalogSnapshot(PricingCanonicalJson.SerializeCatalogSnapshot(catalog)).Status);
        var preview = CreatePreview(catalog.CatalogSha256, createdAt, 1);
        Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(preview).Status);
        clock.UtcNow = preview.Configuration.CreatedAtUtc.AddMinutes(1);
        Assert.Equal(PricingStoreStatus.Success, AppendIncompleteConfigurationCommit(store, preview).Status);
        var sessionId = Guid.NewGuid().ToString("D");
        using (var connection = database.Open()) InsertSession(connection, sessionId);
        var request = CostRecalculationRequestCanonicalJsonV1.Create(
            preview.Configuration.ConfigurationId,
            1,
            catalog.CatalogSha256,
            [sessionId],
            [],
            "pricing-recovery-test-0001");
        var runId = Guid.CreateVersion7().ToString("D");
        var calculationTime = new DateTimeOffset(2026, 7, 24, 2, 0, 0, TimeSpan.Zero);
        var target = new PricingRecalculationTargetWriteIncomplete(
            sessionId,
            "completed",
            calculationTime.AddHours(-1),
            calculationTime.AddMinutes(-1),
            "resolved",
            1,
            new string('b', 64),
            "github-copilot-vscode",
            "1.2.3",
            null,
            null,
            0);

        var started = store.StartRecalculationIncomplete(runId, request, [target], calculationTime);
        var replayed = store.StartRecalculationIncomplete(
            Guid.CreateVersion7().ToString("D"),
            request,
            [target],
            calculationTime.AddDays(1));
        Assert.Equal(PricingStoreStatus.Success, started.Status);
        Assert.Equal(runId, started.Value);
        Assert.Equal(PricingStoreStatus.Success, replayed.Status);
        Assert.Equal(runId, replayed.Value);
        clock.UtcNow = calculationTime.AddMinutes(5);
        Assert.Equal(PricingStoreStatus.Success, store.RecoverInterruptedRuns().Status);

        using var read = database.Open();
        Assert.Equal(
            ["requested", "failed"],
            Names(read, "SELECT event_kind FROM pricing_recalculation_events ORDER BY event_sequence;"));
        Assert.Equal("recalculation_interrupted", Scalar<string>(read, "SELECT failure_code FROM pricing_recalculation_events WHERE event_kind='failed';"));
        Assert.Equal("failed", Scalar<string>(read, "SELECT result_kind FROM pricing_recalculation_target_results;"));
        Assert.Equal(1L, Scalar<long>(read, "SELECT attempt_revision FROM pricing_session_attempts;"));
        Assert.Equal("recalculation_interrupted", Scalar<string>(read, "SELECT result_code FROM pricing_session_attempts;"));
    }

    [Fact]
    public void SqlitePricingStore_AppendEstimateSuccess_PersistsExactBytesAndContiguousHead()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        var sessionId = Guid.NewGuid().ToString("D");
        var calculationTime = new DateTimeOffset(2026, 7, 25, 2, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(calculationTime.AddHours(-1));
        var store = new SqlitePricingStore(database.Path, clock);
        store.CreateSchema();
        Assert.Equal(PricingStoreStatus.Success, store.PutCatalogSnapshot(
            PricingCanonicalJson.SerializeCatalogSnapshot(catalog)).Status);
        var configuration = CostConfigurationCanonicalJsonV1.Create(
            null,
            catalog.CatalogSha256,
            [new("github-copilot-vscode", "1.2.3", "pricing-capability.v1", "github_copilot", "github_ai_credits", "credit_consuming_interaction")],
            [],
            calculationTime.AddHours(-1));
        var preview = CostConfigurationPreviewCanonicalJsonV1.Create(
            configuration, 0, null, catalog.CatalogSha256,
            PricingConfigurationSelectionDigestV1.Create([]), 0, 0, "exact", 0, "exact");
        Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(preview).Status);
        clock.UtcNow = configuration.CreatedAtUtc.AddMinutes(1);
        Assert.Equal(PricingStoreStatus.Success, AppendIncompleteConfigurationCommit(store, preview).Status);
        using (var connection = database.Open()) InsertSession(connection, sessionId);
        var request = CostRecalculationRequestCanonicalJsonV1.Create(
            configuration.ConfigurationId, 1, catalog.CatalogSha256, [sessionId], [], "pricing-estimate-test-0001");
        var runId = Guid.CreateVersion7().ToString("D");
        var target = new PricingRecalculationTargetWriteIncomplete(
            sessionId, "completed", calculationTime.AddMinutes(-30), calculationTime.AddMinutes(-1),
            "resolved", 1, new string('d', 64), "github-copilot-vscode", "1.2.3", null, null, 0);
        Assert.Equal(PricingStoreStatus.Success, store.StartRecalculationIncomplete(runId, request, [target], calculationTime).Status);
        clock.UtcNow = calculationTime.AddSeconds(1);
        Assert.Equal(PricingStoreStatus.Success, store.MarkRecalculationRunning(runId).Status);
        var quantityProvenance = new PricingValueProvenance(
            "synthetic-adapter", "pricing-capability.v1", "event-1", "not_captured", "pricing-normalization.v1");
        var configurationProvenance = new PricingValueProvenance(
            "local-monitor-cost-configuration", "cost.configuration.v1",
            configuration.ConfigurationId + ".source-entry-000", "not_captured", "cost-configuration-provenance.v1");
        var estimateRequest = new PricingEstimateRequest(
            PricingContractVersions.EstimateRequest,
            calculationTime,
            null,
            new PricingEstimateSource(
                "github-copilot-vscode", "1.2.3", sessionId, target.SessionEffectiveAtUtc,
                PricingProviders.GitHubCopilot, "GPT-5 mini", PricingBillingModes.GitHubAiCredits,
                PricingRoutes.CreditConsumingInteraction, PricingSourceCompleteness.Full, [],
                quantityProvenance, quantityProvenance, quantityProvenance,
                configurationProvenance, configurationProvenance),
            new PricingUsage(
                new(1000, quantityProvenance),
                new(2000, quantityProvenance),
                new(500, quantityProvenance),
                null, null, null, null, null));
        var estimate = new PricingEstimationEngine(catalog).Estimate(estimateRequest);
        var estimateBytes = PricingCanonicalJson.Serialize(estimate);
        var expectedEstimateBytes = estimateBytes.ToArray();

        var mismatchedRequest = estimateRequest with
        {
            Usage = estimateRequest.Usage with
            {
                InputTokens = new PricingQuantity(1001, quantityProvenance),
            },
        };
        Assert.Equal(
            PricingStoreStatus.ContractRejected,
            store.AppendEstimateSuccessIncomplete(
                runId,
                0,
                0,
                mismatchedRequest,
                estimateBytes).Status);
        using (var unchanged = database.Open())
            Assert.Equal(0L, Scalar<long>(unchanged, "SELECT COUNT(*) FROM pricing_estimates;"));

        var mutatingRequest = estimateRequest with
        {
            Source = estimateRequest.Source with
            {
                CompletenessReasons = new MutatingReadOnlyList<string>(
                    [],
                    () => estimateBytes[0] = (byte)'['),
            },
        };

        clock.UtcNow = calculationTime.AddSeconds(2);
        Assert.Equal(
            PricingStoreStatus.Success,
            store.AppendEstimateSuccessIncomplete(
                runId,
                0,
                0,
                mutatingRequest,
                estimateBytes).Status);

        using var read = database.Open();
        Assert.Equal((byte)'[', estimateBytes[0]);
        Assert.Equal(expectedEstimateBytes, (byte[])Scalar<object>(read, "SELECT canonical_blob FROM pricing_estimates;"));
        Assert.Equal(1L, Scalar<long>(read, "SELECT head_revision FROM pricing_estimate_heads;"));
        Assert.Equal(estimate.EstimateId, Scalar<string>(read, "SELECT estimate_id FROM pricing_estimate_heads;"));
        Assert.Equal("estimate", Scalar<string>(read, "SELECT result_kind FROM pricing_session_attempts;"));
        Assert.Equal("succeeded", Scalar<string>(read, "SELECT event_kind FROM pricing_recalculation_events WHERE event_sequence=2;"));
        Assert.True(PricingSchemaV1.ValidateRows(read, null));
        Execute(read, "DROP TRIGGER pricing_estimates_no_update;");
        using (var corrupt = read.CreateCommand())
        {
            corrupt.CommandText = "UPDATE pricing_estimates SET canonical_blob=$blob;";
            corrupt.Parameters.AddWithValue("$blob", expectedEstimateBytes.Concat(new byte[] { 0x20 }).ToArray());
            corrupt.ExecuteNonQuery();
        }
        Execute(
            read,
            Assert.Single(PricingSchemaV1.OwnedObjects, item => item.Name == "pricing_estimates_no_update").Sql);
        Assert.False(PricingSchemaV1.ValidateRows(read, null));
    }

    [Fact]
    public void SqlitePricingStore_IncompleteCompletion_BindsExactBudgetScopeAndAlertParents()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var calculationTime = new DateTimeOffset(2026, 7, 25, 2, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(calculationTime.AddHours(-1));
        var store = new SqlitePricingStore(database.Path, clock);
        store.CreateSchema();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        Assert.Equal(
            PricingStoreStatus.Success,
            store.PutCatalogSnapshot(PricingCanonicalJson.SerializeCatalogSnapshot(catalog)).Status);
        var configuration = CostConfigurationCanonicalJsonV1.Create(
            null,
            catalog.CatalogSha256,
            [],
            [new("session-estimated-cost-threshold", "1", true, "USD", "1", "2", 5000, "session", null)],
            calculationTime.AddHours(-1));
        var preview = CostConfigurationPreviewCanonicalJsonV1.Create(
            configuration, 0, null, catalog.CatalogSha256,
            PricingConfigurationSelectionDigestV1.Create([]), 0, 0, "exact", 0, "exact");
        Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(preview).Status);
        clock.UtcNow = calculationTime.AddMinutes(-59);
        Assert.Equal(PricingStoreStatus.Success, AppendIncompleteConfigurationCommit(store, preview).Status);
        var sessionId = Guid.NewGuid().ToString("D");
        using (var connection = database.Open())
        {
            InsertSession(connection, sessionId);
            Execute(
                connection,
                $$"""
                INSERT INTO alert_evaluations(
                    evaluation_id,schema_version,input_hash,configuration_version,
                    configuration_hash,canonical_json)
                VALUES(
                    '{{new string('e', 64)}}','alert.evaluation.v2','{{new string('2', 64)}}',
                    'fixture-v2','{{new string('3', 64)}}',
                    '{"evaluation_id":"{{new string('e', 64)}}"}'),
                    ('{{new string('f', 64)}}','alert.evaluation.v2','{{new string('4', 64)}}',
                    'fixture-v2','{{new string('5', 64)}}',
                    '{"evaluation_id":"{{new string('f', 64)}}"}'),
                    ('{{new string('a', 64)}}','alert.evaluation.v2','{{new string('6', 64)}}',
                    'fixture-v2','{{new string('7', 64)}}',
                    '{"evaluation_id":"{{new string('a', 64)}}"}');
                INSERT INTO alert_receipts(
                    alert_id,evaluation_id,receipt_ordinal,schema_version,canonical_json)
                VALUES(
                    '{{new string('1', 64)}}','{{new string('f', 64)}}',0,'alert.receipt.v2',
                    '{"alert_id":"{{new string('1', 64)}}","evaluation_id":"{{new string('f', 64)}}"}');
                INSERT INTO alert_suppressions(
                    evaluation_id,suppression_ordinal,rule_id,rule_version,code,canonical_json)
                VALUES(
                    '{{new string('a', 64)}}',0,'period-estimated-cost-threshold','1',
                    'rule_disabled','{"evaluation_id":"{{new string('a', 64)}}"}');
                """);
        }
        var rollingCutoff = new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero);
        var request = CostRecalculationRequestCanonicalJsonV1.Create(
            configuration.ConfigurationId,
            1,
            catalog.CatalogSha256,
            [sessionId],
            [
                new("session", sessionId, null, null, null),
                new("utc_day", null, "2026-07-24", null, null),
                new("rolling_period", null, null, rollingCutoff, 7),
            ],
            "pricing-budget-test-0001");
        var runId = Guid.CreateVersion7().ToString("D");
        var target = new PricingRecalculationTargetWriteIncomplete(
            sessionId, "completed", calculationTime.AddMinutes(-30), calculationTime.AddMinutes(-1),
            "missing", 0, new string('d', 64), null, null, null, null, 0);
        Assert.Equal(PricingStoreStatus.Success, store.StartRecalculationIncomplete(runId, request, [target], calculationTime).Status);
        clock.UtcNow = calculationTime.AddSeconds(1);
        Assert.Equal(PricingStoreStatus.Success, store.MarkRecalculationRunning(runId).Status);
        clock.UtcNow = calculationTime.AddSeconds(2);

        var eligibilityDigest = new string('9', 64);
        var eligibleSessionIds = new[] { sessionId };
        PricingBudgetResultWriteIncomplete[] budgetResults =
        [
                new(
                    0,
                    "session",
                    PricingAlertCostScopeIdentityV2.Create(
                        "session", null, null, eligibilityDigest, eligibleSessionIds),
                    eligibilityDigest,
                    eligibleSessionIds,
                    null,
                    null,
                    "session-estimated-cost-threshold",
                    "1",
                    new string('e', 64),
                    "no_match",
                    null,
                    null,
                    null),
                new(
                    1,
                    "utc_day",
                    PricingAlertCostScopeIdentityV2.Create(
                        "utc_day",
                        new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero),
                        eligibilityDigest,
                        eligibleSessionIds),
                    eligibilityDigest,
                    eligibleSessionIds,
                    new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero),
                    "daily-estimated-cost-threshold",
                    "1",
                    new string('f', 64),
                    "receipt",
                    new string('1', 64),
                    null,
                    null),
                new(
                    2,
                    "rolling_period",
                    PricingAlertCostScopeIdentityV2.Create(
                        "rolling_period",
                        rollingCutoff.AddDays(-7),
                        rollingCutoff,
                        eligibilityDigest,
                        eligibleSessionIds),
                    eligibilityDigest,
                    eligibleSessionIds,
                    rollingCutoff.AddDays(-7),
                    rollingCutoff,
                    "period-estimated-cost-threshold",
                    "1",
                    new string('a', 64),
                    "suppression",
                    null,
                    0,
                    "rule_disabled"),
        ];
        var unavailable = new[] { PricingTargetCompletionWrite.Unavailable(0, "source_mapping_unavailable") };
        Assert.Equal(
            PricingStoreStatus.ContractRejected,
            store.AppendRecalculationCompletionIncomplete(
                runId,
                unavailable,
                [.. budgetResults[..2], budgetResults[2] with { SuppressionCode = "scope_not_applicable" }],
                failure: null).Status);
        Assert.Equal(
            PricingStoreStatus.ContractRejected,
            store.AppendRecalculationCompletionIncomplete(
                runId,
                unavailable,
                [budgetResults[0] with { ScopeId = "cost-scope-" + new string('0', 64) }, .. budgetResults[1..]],
                failure: null).Status);

        var result = store.AppendRecalculationCompletionIncomplete(
            runId,
            unavailable,
            budgetResults,
            failure: null);

        Assert.Equal(PricingStoreStatus.Success, result.Status);
        using var read = database.Open();
        Assert.Equal("unavailable", Scalar<string>(read, "SELECT result_kind FROM pricing_recalculation_target_results;"));
        Assert.Equal("source_mapping_unavailable", Scalar<string>(read, "SELECT result_code FROM pricing_session_attempts;"));
        Assert.Equal(
            ["no_match", "receipt", "suppression"],
            Names(read, "SELECT outcome_kind FROM pricing_recalculation_budget_results ORDER BY scope_ordinal;"));
        Assert.Equal("succeeded", Scalar<string>(read, "SELECT event_kind FROM pricing_recalculation_events WHERE event_sequence=2;"));
        Assert.True(PricingSchemaV1.ValidateRows(read, null));

        Execute(read, "UPDATE alert_evaluations SET schema_version='alert.evaluation.v1';");
        Assert.False(PricingSchemaV1.ValidateRows(read, null));
        Execute(read, "UPDATE alert_evaluations SET schema_version='alert.evaluation.v2';");
        Assert.True(PricingSchemaV1.ValidateRows(read, null));

        Execute(read, "DROP TRIGGER pricing_session_attempts_no_update;");
        Execute(read, "PRAGMA ignore_check_constraints=ON;");
        Execute(read, "UPDATE pricing_session_attempts SET result_code='invented_code';");
        Execute(read, "PRAGMA ignore_check_constraints=OFF;");
        Execute(
            read,
            Assert.Single(
                PricingSchemaV1.OwnedObjects,
                item => item.Name == "pricing_session_attempts_no_update").Sql);
        Assert.False(PricingSchemaV1.ValidateRows(read, null));
    }

    [Fact]
    public void SqlitePricingStore_CompleteRecalculation_PersistsClosedFailedResultAndAttempt()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var calculationTime = new DateTimeOffset(2026, 7, 25, 2, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(calculationTime.AddHours(-1));
        var store = new SqlitePricingStore(database.Path, clock);
        store.CreateSchema();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        Assert.Equal(
            PricingStoreStatus.Success,
            store.PutCatalogSnapshot(PricingCanonicalJson.SerializeCatalogSnapshot(catalog)).Status);
        var preview = CreatePreview(catalog.CatalogSha256, calculationTime.AddHours(-1), 1);
        Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(preview).Status);
        clock.UtcNow = calculationTime.AddMinutes(-59);
        Assert.Equal(PricingStoreStatus.Success, AppendIncompleteConfigurationCommit(store, preview).Status);
        var sessionId = Guid.NewGuid().ToString("D");
        using (var connection = database.Open())
            InsertSession(connection, sessionId);
        var request = CostRecalculationRequestCanonicalJsonV1.Create(
            preview.Configuration.ConfigurationId,
            1,
            catalog.CatalogSha256,
            [sessionId],
            [],
            "pricing-failure-test-0001");
        var runId = Guid.CreateVersion7().ToString("D");
        var target = new PricingRecalculationTargetWriteIncomplete(
            sessionId, "failed", calculationTime.AddMinutes(-30), calculationTime.AddMinutes(-1),
            "incomplete", 257, new string('d', 64), null, null, null, null, 0);
        Assert.Equal(PricingStoreStatus.Success, store.StartRecalculationIncomplete(runId, request, [target], calculationTime).Status);
        clock.UtcNow = calculationTime.AddSeconds(1);
        Assert.Equal(PricingStoreStatus.Success, store.MarkRecalculationRunning(runId).Status);
        clock.UtcNow = calculationTime.AddSeconds(2);

        var result = store.AppendRecalculationCompletionIncomplete(
            runId,
            [PricingTargetCompletionWrite.Failed(0, "source_adapter_failed")],
            [],
            new PricingRunFailureWrite("adapter", "target", 0, "source_adapter_failed"));

        Assert.Equal(PricingStoreStatus.Success, result.Status);
        using var read = database.Open();
        Assert.Equal("failed", Scalar<string>(read, "SELECT result_kind FROM pricing_recalculation_target_results;"));
        Assert.Equal("source_adapter_failed", Scalar<string>(read, "SELECT result_code FROM pricing_session_attempts;"));
        Assert.Equal("failed", Scalar<string>(read, "SELECT event_kind FROM pricing_recalculation_events WHERE event_sequence=2;"));
        Assert.Equal("adapter", Scalar<string>(read, "SELECT failure_phase FROM pricing_recalculation_events WHERE event_sequence=2;"));
        Assert.True(PricingSchemaV1.ValidateRows(read, null));
    }

    private static CostConfigurationPreviewV1 CreatePreview(
        string catalogSha,
        DateTimeOffset createdAt,
        int discriminator)
    {
        var configuration = CostConfigurationCanonicalJsonV1.Create(
            null,
            catalogSha,
            [new("surface", $"1.0.{discriminator}", "capability.v1", "provider", "billing", "route")],
            [],
            createdAt);
        return CostConfigurationPreviewCanonicalJsonV1.Create(
            configuration,
            expectedHeadRevision: 0,
            expectedConfigurationId: null,
            catalogSha,
            PricingConfigurationSelectionDigestV1.Create([]),
            proposedMatchCount: 0,
            currentMatchCount: 0,
            currentMatchCountState: "exact",
            overlapCount: 0,
            overlapCountState: "exact");
    }

    private static void AssertConsumerVersionClassification<T>(
        Func<ReadOnlyMemory<byte>, CostConsumerResult<T>> consume,
        string family)
    {
        Assert.Equal(CostConsumerStatus.Invalid, consume("{}"u8.ToArray()).Status);
        Assert.Equal(
            CostConsumerStatus.Invalid,
            consume("""{"schema_version":2}"""u8.ToArray()).Status);
        Assert.Equal(
            CostConsumerStatus.Invalid,
            consume("""{"schema_version":"other.contract.v2"}"""u8.ToArray()).Status);
        Assert.Equal(
            CostConsumerStatus.Invalid,
            consume(System.Text.Encoding.UTF8.GetBytes(
                $$"""{"schema_version":"{{family}}.v02"}""")).Status);
        Assert.Equal(
            CostConsumerStatus.Unsupported,
            consume(System.Text.Encoding.UTF8.GetBytes(
                $$"""{"schema_version":"{{family}}.v2"}""")).Status);
        Assert.Equal(
            CostConsumerStatus.Unsupported,
            consume(System.Text.Encoding.UTF8.GetBytes(
                $$"""{"schema_version":"{{family}}.v10"}""")).Status);
    }

    private static PricingStoreResult<CostConfigurationCommitResultV1> AppendIncompleteConfigurationCommit(
        SqlitePricingStore store,
        CostConfigurationPreviewV1 preview,
        PricingProviderCatalogWrite? providerCatalog = null,
        IReadOnlyList<PricingConfigurationSelectionFactWrite>? selection = null)
    {
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        return store.AppendConfigurationCommitIncomplete(
            preview,
            providerCatalog ?? new(
                catalog.CatalogSha256,
                PricingCanonicalJson.SerializeCatalogSnapshot(catalog)),
            selection ?? []);
    }

    private static PricingConfigurationSelectionFactWrite CreateSelectionFact() =>
        new(
            Guid.NewGuid().ToString("D"),
            "completed",
            new DateTimeOffset(2026, 7, 24, 1, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 24, 1, 1, 0, TimeSpan.Zero),
            "resolved",
            1,
            new string('a', 64),
            "surface",
            "1.0.0",
            null,
            null,
            0);

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar()!;
        return value is T typed
            ? typed
            : (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string[] Names(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
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

    private static void InsertSession(SqliteConnection connection, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO sessions(
                session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at)
            VALUES($id,'completed','full','2026-07-24T01:00:00.0000000+00:00',
                'not_captured','2026-07-24T01:00:00.0000000+00:00',
                '2026-07-24T01:00:00.0000000+00:00');
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.ExecuteNonQuery();
    }

    private static string RecalculationRunInsert(string runId, string idempotencyKey) =>
        $"""
        INSERT INTO pricing_recalculation_runs(
            run_id,request_schema_version,idempotency_key,request_digest,
            canonical_request_blob,configuration_id,configuration_head_revision,
            catalog_sha256,calculation_time_utc,target_count,scope_count,created_at_utc)
        VALUES(
            '{runId}','cost.recalculation-request.v1','{idempotencyKey}',
            '{new string('a', 64)}',X'01','cost-configuration-{new string('b', 64)}',1,
            '{new string('c', 64)}','2026-07-24T00:00:00.0000000+00:00',1,0,
            '2026-07-24T00:00:00.0000000+00:00');
        """;

    private static string EstimateInsert(string provider, string amountText) =>
        $"""
        INSERT INTO pricing_estimates(
            estimate_id,supersedes_estimate_id,schema_version,session_id,catalog_sha256,
            configuration_id,source_entry_ordinal,run_id,target_ordinal,
            calculation_time_utc,session_effective_at_utc,status,source_surface,
            source_application_version,provider,model,billing_mode,pricing_route,
            registry_version,registry_source_kind,currency,amount_text,
            canonical_sha256,canonical_blob)
        VALUES(
            'pricing-estimate-{new string('a', 64)}',NULL,'pricing.estimate.v1',
            '11111111-1111-4111-8111-111111111111','{new string('b', 64)}',
            'cost-configuration-{new string('c', 64)}',0,
            '018f6f58-5f41-7a3f-8b0e-123456789abc',0,
            '2026-07-24T00:00:00.0000000+00:00',
            '2026-07-24T00:00:00.0000000+00:00','estimated','codex',
            '1.0','{provider}','gpt-5','cloud_provider_api_tokens',
            'cloud_provider_configured','2026.07','bundled','USD','{amountText}',
            '{new string('d', 64)}',X'01');
        """;

    private sealed class PricingDatabase : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"pricing-persistence-{Guid.NewGuid():N}");

        internal PricingDatabase()
        {
            Directory.CreateDirectory(root);
            Path = System.IO.Path.Combine(root, "monitor.db");
        }

        internal string Path { get; }

        internal SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = false,
                ForeignKeys = true,
            }.ToString());
            connection.Open();
            return connection;
        }

        internal void CreateDependencies(bool includeRuntimeBackup = true)
        {
            new SqliteSessionStore(Path).CreateSchema();
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            Execute(
                connection,
                transaction,
                """
                CREATE TABLE alert_evaluations (
                    evaluation_id TEXT NOT NULL PRIMARY KEY CHECK(length(evaluation_id)=64 AND evaluation_id=lower(evaluation_id) AND evaluation_id NOT GLOB '*[^0-9a-f]*'),
                    schema_version TEXT NOT NULL CHECK(schema_version IN ('alert.evaluation.v1','alert.evaluation.v2')),
                    input_hash TEXT NOT NULL CHECK(length(input_hash)=64 AND input_hash=lower(input_hash) AND input_hash NOT GLOB '*[^0-9a-f]*'),
                    configuration_version TEXT NOT NULL CHECK(length(configuration_version) BETWEEN 1 AND 128 AND substr(configuration_version,1,1) GLOB '[a-z0-9]' AND configuration_version NOT GLOB '*[^a-z0-9._-]*'),
                    configuration_hash TEXT NOT NULL CHECK(length(configuration_hash)=64 AND configuration_hash=lower(configuration_hash) AND configuration_hash NOT GLOB '*[^0-9a-f]*'),
                    canonical_json TEXT NOT NULL CHECK(json_valid(canonical_json) AND json_extract(canonical_json,'$.evaluation_id')=evaluation_id)
                );
                CREATE TABLE alert_receipts (
                    alert_id TEXT NOT NULL PRIMARY KEY CHECK(length(alert_id)=64 AND alert_id=lower(alert_id) AND alert_id NOT GLOB '*[^0-9a-f]*'),
                    evaluation_id TEXT NOT NULL,
                    receipt_ordinal INTEGER NOT NULL CHECK(receipt_ordinal>=0),
                    schema_version TEXT NOT NULL CHECK(schema_version IN ('alert.receipt.v1','alert.receipt.v2')),
                    canonical_json TEXT NOT NULL CHECK(json_valid(canonical_json) AND json_extract(canonical_json,'$.alert_id')=alert_id AND json_extract(canonical_json,'$.evaluation_id')=evaluation_id),
                    FOREIGN KEY(evaluation_id) REFERENCES alert_evaluations(evaluation_id),
                    UNIQUE(evaluation_id,receipt_ordinal)
                );
                CREATE TABLE alert_suppressions (
                    evaluation_id TEXT NOT NULL,
                    suppression_ordinal INTEGER NOT NULL CHECK(suppression_ordinal>=0),
                    rule_id TEXT NOT NULL CHECK(length(rule_id) BETWEEN 1 AND 128 AND substr(rule_id,1,1) GLOB '[a-z0-9]' AND rule_id NOT GLOB '*[^a-z0-9._-]*'),
                    rule_version TEXT NOT NULL CHECK(length(rule_version) BETWEEN 1 AND 128 AND substr(rule_version,1,1) GLOB '[a-z0-9]' AND rule_version NOT GLOB '*[^a-z0-9._-]*'),
                    code TEXT NOT NULL CHECK(length(code) BETWEEN 1 AND 128 AND substr(code,1,1) GLOB '[a-z0-9]' AND code NOT GLOB '*[^a-z0-9._-]*'),
                    canonical_json TEXT NOT NULL CHECK(json_valid(canonical_json) AND json_extract(canonical_json,'$.evaluation_id')=evaluation_id),
                    PRIMARY KEY(evaluation_id,suppression_ordinal),
                    FOREIGN KEY(evaluation_id) REFERENCES alert_evaluations(evaluation_id)
                );
                INSERT INTO schema_version(component,version) VALUES('alert_engine',2);
                """);
            if (includeRuntimeBackup)
                RuntimeBackupSchemaV1.Ensure(connection, transaction);
            transaction.Commit();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class CallbackTimeProvider(
        DateTimeOffset utcNow,
        Action callback) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            callback();
            return utcNow;
        }
    }

    private sealed class MutatingReadOnlyList<T>(
        IReadOnlyList<T> values,
        Action mutate) : IReadOnlyList<T>
    {
        public int Count => values.Count;

        public T this[int index] => values[index];

        public IEnumerator<T> GetEnumerator()
        {
            mutate();
            return values.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
