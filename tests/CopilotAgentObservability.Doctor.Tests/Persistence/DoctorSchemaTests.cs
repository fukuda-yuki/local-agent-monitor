using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Doctor.Tests.Persistence;

public sealed class DoctorSchemaTests
{
    [Fact]
    public void CreateSchema_AddsOnlyDoctorV1AndCanonicalTables()
    {
        using var database = new DoctorTestDatabase();
        using (var connection = database.Open())
        {
            DoctorTestDatabase.Execute(connection, "CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL); INSERT INTO schema_version VALUES('monitor',5),('session',11);");
        }

        var result = new SqliteDoctorVerificationStore(database.Path, new DoctorTestTimeProvider(DoctorTestData.Now)).CreateSchema();

        Assert.Equal(DoctorResultCode.VerificationActive, result.Code);
        using var reopened = database.Open();
        Assert.Equal(
            ["doctor|1", "monitor|5", "session|11"],
            DoctorTestDatabase.Rows(reopened, "SELECT component,version FROM schema_version ORDER BY component;"));
        Assert.Equal(
            ["doctor_verification_evidence", "doctor_verifications", "schema_version"],
            DoctorTestDatabase.Rows(reopened, "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"));
        Assert.Equal(
            [
                "verification_id|TEXT|1|1",
                "expected_source_surface|TEXT|1|0",
                "expected_source_adapter|TEXT|0|0",
                "state|TEXT|1|0",
                "revision|INTEGER|1|0",
                "started_at|TEXT|1|0",
                "expires_at|TEXT|1|0",
                "completed_at|TEXT|0|0",
                "cancelled_at|TEXT|0|0",
            ],
            DoctorTestDatabase.Rows(reopened, "SELECT name,type,\"notnull\",pk FROM pragma_table_info('doctor_verifications') ORDER BY cid;"));
        Assert.Equal(
            [
                "candidate_id|TEXT|1|1",
                "verification_id|TEXT|1|0",
                "source_surface|TEXT|1|0",
                "source_adapter|TEXT|0|0",
                "evidence_class|TEXT|1|0",
                "evidence_kind|TEXT|1|0",
                "evidence_ref|TEXT|1|0",
                "observed_at|TEXT|1|0",
                "expires_at|TEXT|1|0",
                "accepted|INTEGER|1|0",
                "accepted_ordinal|INTEGER|0|0",
            ],
            DoctorTestDatabase.Rows(reopened, "SELECT name,type,\"notnull\",pk FROM pragma_table_info('doctor_verification_evidence') ORDER BY cid;"));
        Assert.Equal(
            ["doctor_verifications|verification_id|verification_id|CASCADE"],
            DoctorTestDatabase.Rows(reopened, "SELECT \"table\",\"from\",\"to\",on_delete FROM pragma_foreign_key_list('doctor_verification_evidence');"));
    }

    [Fact]
    public void DoctorTables_EnforceStateEvidenceAndReferenceConstraints()
    {
        using var database = new DoctorTestDatabase();
        var store = new SqliteDoctorVerificationStore(database.Path, new DoctorTestTimeProvider(DoctorTestData.Now));
        Assert.Equal(DoctorResultCode.VerificationActive, store.CreateSchema().Code);
        var verification = Assert.IsType<DoctorVerification>(store.Start("github-copilot-vscode", "otel", TimeSpan.FromMinutes(5)).Verification);

        using var connection = database.Open();
        var invalidUuid = verification.VerificationId[..30] + "gggggg";
        var nonCanonicalIdentifier = Assert.Throws<SqliteException>(() => DoctorTestDatabase.Execute(
            connection,
            "UPDATE doctor_verifications SET verification_id=$invalid WHERE verification_id=$id;",
            ("$invalid", invalidUuid), ("$id", verification.VerificationId)));
        Assert.Equal(19, nonCanonicalIdentifier.SqliteErrorCode);

        var nonCanonicalTimestamp = Assert.Throws<SqliteException>(() => DoctorTestDatabase.Execute(
            connection,
            "UPDATE doctor_verifications SET started_at='0000-00-00T00:00:00.0000000Z' WHERE verification_id=$id;",
            ("$id", verification.VerificationId)));
        Assert.Equal(19, nonCanonicalTimestamp.SqliteErrorCode);

        var expiredState = Assert.Throws<SqliteException>(() => DoctorTestDatabase.Execute(
            connection,
            "UPDATE doctor_verifications SET state='expired' WHERE verification_id=$id;",
            ("$id", verification.VerificationId)));
        Assert.Equal(19, expiredState.SqliteErrorCode);

        var invalidTerminal = Assert.Throws<SqliteException>(() => DoctorTestDatabase.Execute(
            connection,
            "UPDATE doctor_verifications SET state='completed' WHERE verification_id=$id;",
            ("$id", verification.VerificationId)));
        Assert.Equal(19, invalidTerminal.SqliteErrorCode);

        var candidate = DoctorTestData.Candidate(verification, "session-constraint");
        DoctorTestDatabase.Execute(
            connection,
            "INSERT INTO doctor_verification_evidence(candidate_id,verification_id,source_surface,source_adapter,evidence_class,evidence_kind,evidence_ref,observed_at,expires_at,accepted,accepted_ordinal) VALUES($candidate,$verification,$source,$adapter,'real_source','ingest',$reference,$observed,$expires,0,NULL);",
            ("$candidate", candidate.CandidateId), ("$verification", verification.VerificationId), ("$source", candidate.SourceSurface),
            ("$adapter", candidate.SourceAdapter), ("$reference", candidate.EvidenceRef),
            ("$observed", "2026-07-16T01:02:03.0000000Z"), ("$expires", "2026-07-16T01:07:03.0000000Z"));

        var invalidPair = Assert.Throws<SqliteException>(() => DoctorTestDatabase.Execute(
            connection,
            "UPDATE doctor_verification_evidence SET accepted=1 WHERE candidate_id=$id;",
            ("$id", candidate.CandidateId)));
        Assert.Equal(19, invalidPair.SqliteErrorCode);

        var duplicateReference = DoctorTestData.Candidate(verification, candidate.EvidenceRef);
        var duplicate = Assert.Throws<SqliteException>(() => DoctorTestDatabase.Execute(
            connection,
            "INSERT INTO doctor_verification_evidence(candidate_id,verification_id,source_surface,source_adapter,evidence_class,evidence_kind,evidence_ref,observed_at,expires_at,accepted,accepted_ordinal) VALUES($candidate,$verification,$source,$adapter,'real_source','ingest',$reference,$observed,$expires,0,NULL);",
            ("$candidate", duplicateReference.CandidateId), ("$verification", verification.VerificationId), ("$source", duplicateReference.SourceSurface),
            ("$adapter", duplicateReference.SourceAdapter), ("$reference", duplicateReference.EvidenceRef),
            ("$observed", "2026-07-16T01:02:03.0000000Z"), ("$expires", "2026-07-16T01:07:03.0000000Z")));
        Assert.Equal(19, duplicate.SqliteErrorCode);

        var orphan = DoctorTestData.Candidate(verification with { VerificationId = Guid.CreateVersion7(DoctorTestData.Now).ToString("D") }, "session-orphan");
        var foreignKey = Assert.Throws<SqliteException>(() => DoctorTestDatabase.Execute(
            connection,
            "INSERT INTO doctor_verification_evidence(candidate_id,verification_id,source_surface,source_adapter,evidence_class,evidence_kind,evidence_ref,observed_at,expires_at,accepted,accepted_ordinal) VALUES($candidate,$verification,$source,$adapter,'real_source','ingest',$reference,$observed,$expires,0,NULL);",
            ("$candidate", orphan.CandidateId), ("$verification", orphan.VerificationId), ("$source", orphan.SourceSurface),
            ("$adapter", orphan.SourceAdapter), ("$reference", orphan.EvidenceRef),
            ("$observed", "2026-07-16T01:02:03.0000000Z"), ("$expires", "2026-07-16T01:07:03.0000000Z")));
        Assert.Equal(19, foreignKey.SqliteErrorCode);
    }

    [Fact]
    public void CreateSchema_NewerOrBrokenDoctorSchema_ReturnsUnavailableWithoutRepair()
    {
        using var newerDatabase = new DoctorTestDatabase();
        using (var connection = newerDatabase.Open())
        {
            DoctorTestDatabase.Execute(connection, "CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL); INSERT INTO schema_version VALUES('doctor',2);");
        }
        var newer = new SqliteDoctorVerificationStore(newerDatabase.Path, new DoctorTestTimeProvider(DoctorTestData.Now)).CreateSchema();
        Assert.Equal(DoctorResultCode.DoctorStoreUnavailable, newer.Code);
        using (var connection = newerDatabase.Open())
        {
            Assert.Equal(2L, DoctorTestDatabase.Scalar(connection, "SELECT version FROM schema_version WHERE component='doctor';"));
            Assert.Equal(0L, DoctorTestDatabase.Scalar(connection, "SELECT count(*) FROM sqlite_schema WHERE name LIKE 'doctor_%';"));
        }

        using var brokenDatabase = new DoctorTestDatabase();
        using (var connection = brokenDatabase.Open())
        {
            DoctorTestDatabase.Execute(connection, "CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL); INSERT INTO schema_version VALUES('doctor',1);");
        }
        var broken = new SqliteDoctorVerificationStore(brokenDatabase.Path, new DoctorTestTimeProvider(DoctorTestData.Now)).CreateSchema();
        Assert.Equal(DoctorResultCode.DoctorStoreUnavailable, broken.Code);
        using var brokenConnection = brokenDatabase.Open();
        Assert.Equal(0L, DoctorTestDatabase.Scalar(brokenConnection, "SELECT count(*) FROM sqlite_schema WHERE name LIKE 'doctor_%';"));

        using var unconstrainedDatabase = new DoctorTestDatabase();
        using (var connection = unconstrainedDatabase.Open())
        {
            DoctorTestDatabase.Execute(
                connection,
                """
                CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL);
                INSERT INTO schema_version VALUES('doctor',1);
                CREATE TABLE doctor_verifications(
                    verification_id TEXT PRIMARY KEY,expected_source_surface TEXT,expected_source_adapter TEXT,
                    state TEXT,revision INTEGER,started_at TEXT,expires_at TEXT,completed_at TEXT,cancelled_at TEXT);
                CREATE TABLE doctor_verification_evidence(
                    candidate_id TEXT PRIMARY KEY,verification_id TEXT,source_surface TEXT,source_adapter TEXT,
                    evidence_class TEXT,evidence_kind TEXT,evidence_ref TEXT,observed_at TEXT,expires_at TEXT,
                    accepted INTEGER,accepted_ordinal INTEGER,
                    FOREIGN KEY(verification_id) REFERENCES doctor_verifications(verification_id) ON DELETE CASCADE);
                """);
        }
        var unconstrained = new SqliteDoctorVerificationStore(
            unconstrainedDatabase.Path,
            new DoctorTestTimeProvider(DoctorTestData.Now)).CreateSchema();
        Assert.Equal(DoctorResultCode.DoctorStoreUnavailable, unconstrained.Code);
    }
}
