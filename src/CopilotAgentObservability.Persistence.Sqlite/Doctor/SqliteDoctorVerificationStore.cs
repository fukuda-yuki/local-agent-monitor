using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class SqliteDoctorVerificationStore : IDoctorVerificationStore
{
    private const int MaximumCandidates = 100;
    private const int MaximumAcceptedEvidence = 16;
    private readonly string databasePath;
    private readonly TimeProvider timeProvider;
    private readonly int busyTimeoutMilliseconds;
    private readonly Action<string>? checkpoint;
    private readonly Func<string, SqliteConnection> connectionFactory;

    public SqliteDoctorVerificationStore(string databasePath, TimeProvider? timeProvider = null)
        : this(databasePath, timeProvider, busyTimeoutMilliseconds: 5_000, checkpoint: null)
    {
    }

    internal SqliteDoctorVerificationStore(
        string databasePath,
        TimeProvider? timeProvider = null,
        int busyTimeoutMilliseconds = 5_000,
        Action<string>? checkpoint = null,
        Func<string, SqliteConnection>? connectionFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (busyTimeoutMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(busyTimeoutMilliseconds));
        }
        this.databasePath = databasePath;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.busyTimeoutMilliseconds = busyTimeoutMilliseconds;
        this.checkpoint = checkpoint;
        this.connectionFactory = connectionFactory ?? (connectionString => new SqliteConnection(connectionString));
    }

    public DoctorStoreOutcome CreateSchema()
    {
        try
        {
            EnsureParentDirectory();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: false);
            DoctorSchemaV1.EnsureSchemaVersionTable(connection, transaction);
            var version = DoctorSchemaV1.ReadVersion(connection, transaction);
            if (version is not null)
            {
                if (version != DoctorSchemaV1.Version || !DoctorSchemaV1.IsValid(connection, transaction))
                {
                    transaction.Rollback();
                    return Unavailable();
                }
                transaction.Commit();
                return Active();
            }

            if (DoctorSchemaV1.DoctorTablesExist(connection, transaction))
            {
                transaction.Rollback();
                return Unavailable();
            }

            DoctorSchemaV1.Create(connection, transaction);
            checkpoint?.Invoke("after-doctor-tables");
            DoctorSchemaV1.SetVersion(connection, transaction);
            checkpoint?.Invoke("after-doctor-version");
            if (!DoctorSchemaV1.IsValid(connection, transaction))
            {
                transaction.Rollback();
                return Unavailable();
            }
            transaction.Commit();
            return Active();
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            return Busy();
        }
        catch (Exception)
        {
            return Unavailable();
        }
    }

    public DoctorStoreOutcome Start(string sourceSurface, string? sourceAdapter, TimeSpan window)
    {
        if (!DoctorStoreValidation.IsSourceToken(sourceSurface)
            || !DoctorStoreValidation.IsSourceToken(sourceAdapter, nullable: true)
            || window < TimeSpan.FromMinutes(1)
            || window > TimeSpan.FromMinutes(30))
        {
            return Invalid();
        }

        var now = UtcNow();
        var verification = new DoctorVerification(
            Guid.CreateVersion7(now).ToString("D"),
            sourceSurface,
            sourceAdapter,
            DoctorVerificationState.Active,
            Revision: 1,
            StartedAt: now,
            ExpiresAt: now.Add(window),
            CompletedAt: null,
            CancelledAt: null,
            AcceptedEvidenceRefs: []);
        return InsertVerification(verification, DoctorResultCode.VerificationStarted);
    }

    public DoctorStoreOutcome Get(string verificationId)
    {
        if (!DoctorStoreValidation.IsCanonicalUuidV7(verificationId))
        {
            return Invalid();
        }

        try
        {
            using var connection = OpenConnection();
            if (!DoctorSchemaV1.IsValid(connection, transaction: null))
            {
                return Unavailable();
            }
            var verification = ReadVerification(connection, transaction: null, verificationId);
            return verification is null ? NotFound() : ProjectRead(verification, UtcNow());
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            return Busy();
        }
        catch (Exception)
        {
            return Unavailable();
        }
    }

    public DoctorStoreOutcome ObserveCandidate(DoctorEvidenceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!DoctorStoreValidation.IsCandidate(candidate))
        {
            return Invalid();
        }

        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!DoctorSchemaV1.IsValid(connection, transaction))
            {
                transaction.Rollback();
                return Unavailable();
            }
            var verification = ReadVerification(connection, transaction, candidate.VerificationId);
            if (verification is null)
            {
                transaction.Rollback();
                return NotFound();
            }
            var now = UtcNow();
            var transitionFailure = CheckActiveTransition(verification, expectedRevision: verification.Revision, now);
            if (transitionFailure is not null)
            {
                transaction.Rollback();
                return transitionFailure;
            }
            if (candidate.ExpiresAt <= now)
            {
                transaction.Rollback();
                return new(DoctorResultCode.EvidenceExpired, verification);
            }
            if (CandidateExists(connection, transaction, candidate.CandidateId, candidate.VerificationId, candidate.EvidenceRef)
                || CountCandidates(connection, transaction, candidate.VerificationId) >= MaximumCandidates)
            {
                transaction.Rollback();
                return Invalid();
            }

            InsertCandidate(connection, transaction, candidate);
            checkpoint?.Invoke("after-candidate-insert");
            transaction.Commit();
            return Active(verification);
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            return Busy();
        }
        catch (Exception)
        {
            return Unavailable();
        }
    }

    public DoctorStoreOutcome Complete(
        string verificationId,
        int expectedRevision,
        string sourceSurface,
        string? sourceAdapter,
        IReadOnlyList<string> acceptedEvidenceRefs,
        Func<IReadOnlyList<DoctorEvidenceCandidate>, DoctorCompletionDecision> evaluate)
    {
        ArgumentNullException.ThrowIfNull(acceptedEvidenceRefs);
        ArgumentNullException.ThrowIfNull(evaluate);
        if (!DoctorStoreValidation.IsCanonicalUuidV7(verificationId)
            || expectedRevision <= 0
            || !DoctorStoreValidation.IsSourceToken(sourceSurface)
            || !DoctorStoreValidation.IsSourceToken(sourceAdapter, nullable: true)
            || acceptedEvidenceRefs.Count is < 1 or > MaximumAcceptedEvidence
            || acceptedEvidenceRefs.Distinct(StringComparer.Ordinal).Count() != acceptedEvidenceRefs.Count
            || acceptedEvidenceRefs.Any(reference => !DoctorStoreValidation.IsEvidenceReference(reference)))
        {
            return Invalid();
        }

        try
        {
            checkpoint?.Invoke("before-terminal-transaction");
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!DoctorSchemaV1.IsValid(connection, transaction))
            {
                transaction.Rollback();
                return Unavailable();
            }
            var verification = ReadVerification(connection, transaction, verificationId);
            if (verification is null)
            {
                transaction.Rollback();
                return NotFound();
            }
            var now = UtcNow();
            var transitionFailure = CheckActiveTransition(verification, expectedRevision, now);
            if (transitionFailure is not null)
            {
                transaction.Rollback();
                return transitionFailure;
            }
            if (!string.Equals(sourceSurface, verification.ExpectedSourceSurface, StringComparison.Ordinal)
                || !string.Equals(sourceAdapter, verification.ExpectedSourceAdapter, StringComparison.Ordinal))
            {
                transaction.Rollback();
                return new(DoctorResultCode.ExpectedSourceMismatch, verification);
            }

            var resolved = new List<DoctorEvidenceCandidate>(acceptedEvidenceRefs.Count);
            foreach (var evidenceRef in acceptedEvidenceRefs)
            {
                var candidate = ReadCandidate(connection, transaction, verificationId, evidenceRef);
                if (candidate is null)
                {
                    transaction.Rollback();
                    return new(DoctorResultCode.EvidenceNotFound, verification);
                }
                if (candidate.ExpiresAt <= now)
                {
                    transaction.Rollback();
                    return new(DoctorResultCode.EvidenceExpired, verification);
                }
                if (!string.Equals(candidate.SourceSurface, verification.ExpectedSourceSurface, StringComparison.Ordinal)
                    || !string.Equals(candidate.SourceAdapter, verification.ExpectedSourceAdapter, StringComparison.Ordinal))
                {
                    transaction.Rollback();
                    return new(DoctorResultCode.ExpectedSourceMismatch, verification);
                }
                resolved.Add(candidate);
            }
            if (!resolved.Any(candidate => candidate.EvidenceClass == DoctorEvidenceClass.RealSource))
            {
                transaction.Rollback();
                return new(DoctorResultCode.ExpectedSourceMismatch, verification);
            }

            var decision = evaluate(resolved);
            if (decision != DoctorCompletionDecision.Ready)
            {
                transaction.Rollback();
                return new(
                    decision == DoctorCompletionDecision.Partial
                        ? DoctorResultCode.PartialFactSnapshot
                        : DoctorResultCode.EvaluationCompleted,
                    verification,
                    resolved);
            }

            for (var ordinal = 0; ordinal < acceptedEvidenceRefs.Count; ordinal++)
            {
                AcceptCandidate(connection, transaction, verificationId, acceptedEvidenceRefs[ordinal], ordinal);
            }
            checkpoint?.Invoke("after-evidence-acceptance");
            var updated = CompleteVerification(connection, transaction, verificationId, expectedRevision, now);
            if (updated != 1)
            {
                transaction.Rollback();
                return new(DoctorResultCode.VerificationStale, verification);
            }
            checkpoint?.Invoke("after-terminal-update");
            transaction.Commit();

            return new(
                DoctorResultCode.VerificationCompleted,
                verification with
                {
                    State = DoctorVerificationState.Completed,
                    Revision = verification.Revision + 1,
                    CompletedAt = now,
                    AcceptedEvidenceRefs = acceptedEvidenceRefs.ToArray(),
                },
                resolved);
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            return Busy();
        }
        catch (Exception)
        {
            return Unavailable();
        }
    }

    public DoctorStoreOutcome Cancel(string verificationId, int expectedRevision)
    {
        if (!DoctorStoreValidation.IsCanonicalUuidV7(verificationId) || expectedRevision <= 0)
        {
            return Invalid();
        }

        try
        {
            checkpoint?.Invoke("before-terminal-transaction");
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!DoctorSchemaV1.IsValid(connection, transaction))
            {
                transaction.Rollback();
                return Unavailable();
            }
            var verification = ReadVerification(connection, transaction, verificationId);
            if (verification is null)
            {
                transaction.Rollback();
                return NotFound();
            }
            var now = UtcNow();
            var transitionFailure = CheckActiveTransition(verification, expectedRevision, now);
            if (transitionFailure is not null)
            {
                transaction.Rollback();
                return transitionFailure;
            }
            var updated = CancelVerification(connection, transaction, verificationId, expectedRevision, now);
            if (updated != 1)
            {
                transaction.Rollback();
                return new(DoctorResultCode.VerificationStale, verification);
            }
            checkpoint?.Invoke("after-terminal-update");
            transaction.Commit();
            return new(
                DoctorResultCode.VerificationCancelled,
                verification with
                {
                    State = DoctorVerificationState.Cancelled,
                    Revision = verification.Revision + 1,
                    CancelledAt = now,
                });
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            return Busy();
        }
        catch (Exception)
        {
            return Unavailable();
        }
    }

    DoctorVerification IDoctorVerificationStore.Start(DoctorVerification verification)
    {
        var result = InsertVerification(verification, DoctorResultCode.VerificationStarted);
        return RequireVerification(result);
    }

    DoctorVerification? IDoctorVerificationStore.Find(string verificationId)
    {
        var result = Get(verificationId);
        return result.Code == DoctorResultCode.VerificationNotFound ? null : RequireVerification(result);
    }

    void IDoctorVerificationStore.ObserveCandidate(DoctorEvidenceCandidate candidate) =>
        RequireSuccess(ObserveCandidate(candidate), DoctorResultCode.VerificationActive);

    IReadOnlyList<DoctorEvidenceCandidate> IDoctorVerificationStore.ResolveCandidates(
        string verificationId,
        IReadOnlyList<string> evidenceRefs,
        DateTimeOffset observedAt)
    {
        if (observedAt.Offset != TimeSpan.Zero)
        {
            throw new DoctorStoreOperationException(DoctorResultCode.InvalidInput);
        }
        var result = ResolveCandidates(verificationId, evidenceRefs, observedAt);
        RequireSuccess(result, DoctorResultCode.VerificationActive);
        return result.ResolvedCandidates;
    }

    DoctorVerification? IDoctorVerificationStore.Complete(
        string verificationId,
        int expectedRevision,
        IReadOnlyList<string> acceptedEvidenceRefs,
        DateTimeOffset completedAt)
    {
        var current = Get(verificationId);
        var verification = RequireVerification(current);
        if (completedAt.Offset != TimeSpan.Zero)
        {
            throw new DoctorStoreOperationException(DoctorResultCode.InvalidInput);
        }
        var result = Complete(
            verificationId,
            expectedRevision,
            verification.ExpectedSourceSurface,
            verification.ExpectedSourceAdapter,
            acceptedEvidenceRefs,
            _ => DoctorCompletionDecision.Ready);
        return RequireVerification(result);
    }

    DoctorVerification? IDoctorVerificationStore.Cancel(string verificationId, int expectedRevision, DateTimeOffset cancelledAt)
    {
        if (cancelledAt.Offset != TimeSpan.Zero)
        {
            throw new DoctorStoreOperationException(DoctorResultCode.InvalidInput);
        }
        return RequireVerification(Cancel(verificationId, expectedRevision));
    }

    private DoctorStoreOutcome InsertVerification(DoctorVerification verification, DoctorResultCode successCode)
    {
        if (!IsValidNewVerification(verification))
        {
            return Invalid();
        }
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!DoctorSchemaV1.IsValid(connection, transaction))
            {
                transaction.Rollback();
                return Unavailable();
            }
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO doctor_verifications(
                    verification_id,expected_source_surface,expected_source_adapter,state,revision,
                    started_at,expires_at,completed_at,cancelled_at)
                VALUES($id,$source,$adapter,'active',1,$started,$expires,NULL,NULL);
                """;
            Add(command, "$id", verification.VerificationId);
            Add(command, "$source", verification.ExpectedSourceSurface);
            Add(command, "$adapter", verification.ExpectedSourceAdapter);
            Add(command, "$started", Timestamp(verification.StartedAt));
            Add(command, "$expires", Timestamp(verification.ExpiresAt));
            command.ExecuteNonQuery();
            checkpoint?.Invoke("after-verification-insert");
            transaction.Commit();
            return new(successCode, verification);
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            return Busy();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return Invalid();
        }
        catch (Exception)
        {
            return Unavailable();
        }
    }

    private DoctorStoreOutcome ResolveCandidates(
        string verificationId,
        IReadOnlyList<string> evidenceRefs,
        DateTimeOffset observedAt)
    {
        if (!DoctorStoreValidation.IsCanonicalUuidV7(verificationId)
            || evidenceRefs.Count is < 1 or > MaximumAcceptedEvidence
            || evidenceRefs.Distinct(StringComparer.Ordinal).Count() != evidenceRefs.Count
            || evidenceRefs.Any(reference => !DoctorStoreValidation.IsEvidenceReference(reference)))
        {
            return Invalid();
        }
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            if (!DoctorSchemaV1.IsValid(connection, transaction))
            {
                transaction.Rollback();
                return Unavailable();
            }
            var verification = ReadVerification(connection, transaction, verificationId);
            if (verification is null)
            {
                transaction.Rollback();
                return NotFound();
            }
            var transitionFailure = CheckActiveTransition(verification, verification.Revision, observedAt);
            if (transitionFailure is not null)
            {
                transaction.Rollback();
                return transitionFailure;
            }
            var candidates = new List<DoctorEvidenceCandidate>();
            foreach (var evidenceRef in evidenceRefs)
            {
                var candidate = ReadCandidate(connection, transaction, verificationId, evidenceRef);
                if (candidate is null)
                {
                    transaction.Rollback();
                    return new(DoctorResultCode.EvidenceNotFound, verification);
                }
                if (candidate.ExpiresAt <= observedAt)
                {
                    transaction.Rollback();
                    return new(DoctorResultCode.EvidenceExpired, verification);
                }
                candidates.Add(candidate);
            }
            transaction.Commit();
            return new(DoctorResultCode.VerificationActive, verification, candidates);
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            return Busy();
        }
        catch (Exception)
        {
            return Unavailable();
        }
    }

    private static DoctorStoreOutcome? CheckActiveTransition(
        DoctorVerification verification,
        int expectedRevision,
        DateTimeOffset now)
    {
        if (verification.State == DoctorVerificationState.Completed)
        {
            return new(DoctorResultCode.VerificationAlreadyCompleted, verification);
        }
        if (verification.State == DoctorVerificationState.Cancelled)
        {
            return new(DoctorResultCode.VerificationAlreadyCancelled, verification);
        }
        if (verification.ExpiresAt <= now)
        {
            return new(
                DoctorResultCode.VerificationExpired,
                verification with { State = DoctorVerificationState.Expired });
        }
        if (verification.Revision != expectedRevision)
        {
            return new(DoctorResultCode.VerificationStale, verification);
        }
        return null;
    }

    private static DoctorStoreOutcome ProjectRead(DoctorVerification verification, DateTimeOffset now) =>
        verification.State switch
        {
            DoctorVerificationState.Completed => new(DoctorResultCode.VerificationCompleted, verification),
            DoctorVerificationState.Cancelled => new(DoctorResultCode.VerificationCancelled, verification),
            DoctorVerificationState.Active when verification.ExpiresAt <= now =>
                new(DoctorResultCode.VerificationExpired, verification with { State = DoctorVerificationState.Expired }),
            DoctorVerificationState.Active => Active(verification),
            _ => Unavailable(),
        };

    private static DoctorVerification? ReadVerification(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string verificationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT verification_id,expected_source_surface,expected_source_adapter,state,revision,
                   started_at,expires_at,completed_at,cancelled_at
            FROM doctor_verifications WHERE verification_id=$id;
            """;
        command.Parameters.AddWithValue("$id", verificationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        var accepted = ReadAcceptedEvidence(connection, transaction, verificationId);
        return new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            ParseState(reader.GetString(3)),
            reader.GetInt32(4),
            ParseTimestamp(reader.GetString(5)),
            ParseTimestamp(reader.GetString(6)),
            reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7)),
            reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8)),
            accepted);
    }

    private static IReadOnlyList<string> ReadAcceptedEvidence(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string verificationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT evidence_ref FROM doctor_verification_evidence WHERE verification_id=$id AND accepted=1 ORDER BY accepted_ordinal;";
        command.Parameters.AddWithValue("$id", verificationId);
        using var reader = command.ExecuteReader();
        var references = new List<string>();
        while (reader.Read())
        {
            references.Add(reader.GetString(0));
        }
        return references;
    }

    private static DoctorEvidenceCandidate? ReadCandidate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string verificationId,
        string evidenceRef)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT candidate_id,verification_id,source_surface,source_adapter,evidence_class,evidence_kind,
                   evidence_ref,observed_at,expires_at
            FROM doctor_verification_evidence
            WHERE verification_id=$verification_id AND evidence_ref=$evidence_ref;
            """;
        Add(command, "$verification_id", verificationId);
        Add(command, "$evidence_ref", evidenceRef);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        return new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            ParseEvidenceClass(reader.GetString(4)),
            ParseEvidenceKind(reader.GetString(5)),
            reader.GetString(6),
            ParseTimestamp(reader.GetString(7)),
            ParseTimestamp(reader.GetString(8)));
    }

    private static bool CandidateExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string candidateId,
        string verificationId,
        string evidenceRef)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT count(*) FROM doctor_verification_evidence WHERE candidate_id=$candidate_id OR (verification_id=$verification_id AND evidence_ref=$evidence_ref);";
        Add(command, "$candidate_id", candidateId);
        Add(command, "$verification_id", verificationId);
        Add(command, "$evidence_ref", evidenceRef);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static long CountCandidates(SqliteConnection connection, SqliteTransaction transaction, string verificationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM doctor_verification_evidence WHERE verification_id=$id;";
        command.Parameters.AddWithValue("$id", verificationId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void InsertCandidate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DoctorEvidenceCandidate candidate)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO doctor_verification_evidence(
                candidate_id,verification_id,source_surface,source_adapter,evidence_class,evidence_kind,
                evidence_ref,observed_at,expires_at,accepted,accepted_ordinal)
            VALUES($candidate,$verification,$source,$adapter,$class,$kind,$reference,$observed,$expires,0,NULL);
            """;
        Add(command, "$candidate", candidate.CandidateId);
        Add(command, "$verification", candidate.VerificationId);
        Add(command, "$source", candidate.SourceSurface);
        Add(command, "$adapter", candidate.SourceAdapter);
        Add(command, "$class", EvidenceClassWire(candidate.EvidenceClass));
        Add(command, "$kind", EvidenceKindWire(candidate.EvidenceKind));
        Add(command, "$reference", candidate.EvidenceRef);
        Add(command, "$observed", Timestamp(candidate.ObservedAt));
        Add(command, "$expires", Timestamp(candidate.ExpiresAt));
        command.ExecuteNonQuery();
    }

    private static void AcceptCandidate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string verificationId,
        string evidenceRef,
        int ordinal)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE doctor_verification_evidence SET accepted=1,accepted_ordinal=$ordinal WHERE verification_id=$id AND evidence_ref=$reference AND accepted=0;";
        Add(command, "$ordinal", ordinal);
        Add(command, "$id", verificationId);
        Add(command, "$reference", evidenceRef);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("Doctor evidence selection changed.");
        }
    }

    private static int CompleteVerification(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string verificationId,
        int expectedRevision,
        DateTimeOffset completedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE doctor_verifications SET state='completed',revision=revision+1,completed_at=$at WHERE verification_id=$id AND state='active' AND revision=$revision;";
        Add(command, "$at", Timestamp(completedAt));
        Add(command, "$id", verificationId);
        Add(command, "$revision", expectedRevision);
        return command.ExecuteNonQuery();
    }

    private static int CancelVerification(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string verificationId,
        int expectedRevision,
        DateTimeOffset cancelledAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE doctor_verifications SET state='cancelled',revision=revision+1,cancelled_at=$at WHERE verification_id=$id AND state='active' AND revision=$revision;";
        Add(command, "$at", Timestamp(cancelledAt));
        Add(command, "$id", verificationId);
        Add(command, "$revision", expectedRevision);
        return command.ExecuteNonQuery();
    }

    private bool IsValidNewVerification(DoctorVerification verification) =>
        DoctorStoreValidation.IsCanonicalUuidV7(verification.VerificationId)
        && DoctorStoreValidation.IsSourceToken(verification.ExpectedSourceSurface)
        && DoctorStoreValidation.IsSourceToken(verification.ExpectedSourceAdapter, nullable: true)
        && verification.State == DoctorVerificationState.Active
        && verification.Revision == 1
        && verification.StartedAt.Offset == TimeSpan.Zero
        && verification.ExpiresAt.Offset == TimeSpan.Zero
        && verification.ExpiresAt - verification.StartedAt >= TimeSpan.FromMinutes(1)
        && verification.ExpiresAt - verification.StartedAt <= TimeSpan.FromMinutes(30)
        && verification.CompletedAt is null
        && verification.CancelledAt is null
        && verification.AcceptedEvidenceRefs.Count == 0;

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            DefaultTimeout = Math.Max(1, checked((busyTimeoutMilliseconds + 999) / 1_000)),
        };
        var connection = connectionFactory(builder.ToString());
        try
        {
            connection.Open();
            checkpoint?.Invoke("after-connection-open");
            using (var foreignKeys = connection.CreateCommand())
            {
                foreignKeys.CommandText = "PRAGMA foreign_keys=ON;";
                foreignKeys.ExecuteNonQuery();
            }
            using (var busyTimeout = connection.CreateCommand())
            {
                busyTimeout.CommandText = $"PRAGMA busy_timeout={busyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)};";
                busyTimeout.ExecuteNonQuery();
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void EnsureParentDirectory()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private DateTimeOffset UtcNow() => timeProvider.GetUtcNow().ToUniversalTime();

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static DoctorVerificationState ParseState(string value) => value switch
    {
        "active" => DoctorVerificationState.Active,
        "completed" => DoctorVerificationState.Completed,
        "cancelled" => DoctorVerificationState.Cancelled,
        _ => throw new InvalidOperationException("Stored Doctor verification state is invalid."),
    };

    private static string EvidenceClassWire(DoctorEvidenceClass value) => value switch
    {
        DoctorEvidenceClass.RealSource => "real_source",
        DoctorEvidenceClass.SyntheticProbe => "synthetic_probe",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static DoctorEvidenceClass ParseEvidenceClass(string value) => value switch
    {
        "real_source" => DoctorEvidenceClass.RealSource,
        "synthetic_probe" => DoctorEvidenceClass.SyntheticProbe,
        _ => throw new InvalidOperationException("Stored Doctor evidence class is invalid."),
    };

    private static string EvidenceKindWire(DoctorEvidenceKind value) => value switch
    {
        DoctorEvidenceKind.Ingest => "ingest",
        DoctorEvidenceKind.RawPersistence => "raw_persistence",
        DoctorEvidenceKind.Projection => "projection",
        DoctorEvidenceKind.ExactSessionBinding => "exact_session_binding",
        DoctorEvidenceKind.CompletenessContent => "completeness_content",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static DoctorEvidenceKind ParseEvidenceKind(string value) => value switch
    {
        "ingest" => DoctorEvidenceKind.Ingest,
        "raw_persistence" => DoctorEvidenceKind.RawPersistence,
        "projection" => DoctorEvidenceKind.Projection,
        "exact_session_binding" => DoctorEvidenceKind.ExactSessionBinding,
        "completeness_content" => DoctorEvidenceKind.CompletenessContent,
        _ => throw new InvalidOperationException("Stored Doctor evidence kind is invalid."),
    };

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static bool IsBusy(SqliteException exception) => exception.SqliteErrorCode is 5 or 6;

    private static DoctorVerification RequireVerification(DoctorStoreOutcome outcome)
    {
        if (outcome.Verification is null
            || outcome.Code is DoctorResultCode.DoctorStoreBusy
                or DoctorResultCode.DoctorStoreUnavailable
                or DoctorResultCode.InvalidInput
                or DoctorResultCode.VerificationNotFound
                or DoctorResultCode.VerificationStale
                or DoctorResultCode.VerificationExpired
                or DoctorResultCode.VerificationAlreadyCancelled
                or DoctorResultCode.VerificationAlreadyCompleted
                or DoctorResultCode.ExpectedSourceMismatch
                or DoctorResultCode.EvidenceNotFound
                or DoctorResultCode.EvidenceExpired)
        {
            throw new DoctorStoreOperationException(outcome.Code);
        }
        return outcome.Verification;
    }

    private static void RequireSuccess(DoctorStoreOutcome outcome, DoctorResultCode expected)
    {
        if (outcome.Code != expected)
        {
            throw new DoctorStoreOperationException(outcome.Code);
        }
    }

    private static DoctorStoreOutcome Active(DoctorVerification? verification = null) =>
        new(DoctorResultCode.VerificationActive, verification);

    private static DoctorStoreOutcome Invalid() => new(DoctorResultCode.InvalidInput);
    private static DoctorStoreOutcome NotFound() => new(DoctorResultCode.VerificationNotFound);
    private static DoctorStoreOutcome Busy() => new(DoctorResultCode.DoctorStoreBusy);
    private static DoctorStoreOutcome Unavailable() => new(DoctorResultCode.DoctorStoreUnavailable);
}
