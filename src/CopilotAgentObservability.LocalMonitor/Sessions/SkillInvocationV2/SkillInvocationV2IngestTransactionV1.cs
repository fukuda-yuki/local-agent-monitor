using System.Diagnostics.CodeAnalysis;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

internal enum SkillInvocationV2IngestOutcomeV1
{
    Committed,
    ReplaySucceeded,
    IdempotencyConflict,
    PersistenceBusy,
    Unavailable,
}

internal sealed record SkillInvocationV2IngestResultV1(
    SkillInvocationV2IngestOutcomeV1 Outcome,
    bool TerminalSealAttempted);

internal static class SkillInvocationV2IngestTransactionV1
{
    internal static SkillInvocationV2IngestResultV1 Execute(
        string databasePath,
        SkillInvocationV2IngestRequestFactsV1 facts,
        ISkillRegistryGenerationAuthority registryAuthority,
        TimeProvider timeProvider,
        Func<bool> trySealReplaySuccess,
        Func<bool> trySealCommit,
        CancellationToken workToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(registryAuthority);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(trySealReplaySuccess);
        ArgumentNullException.ThrowIfNull(trySealCommit);

        if (workToken.IsCancellationRequested)
            return Unavailable(false);

        var replayRequest = new SkillInvocationSnapshotReplayRequest(
            SkillInvocationV2Parser.SourceAdapter,
            facts.Identity.SourceEventId,
            facts.RequestFingerprintSha256);
        var receiptProbe = SkillInvocationSnapshotReplayValidator.ProbeReceipt(databasePath, replayRequest);
        switch (receiptProbe)
        {
            case SkillInvocationSnapshotReceiptProbeOutcome.Busy:
                return new(SkillInvocationV2IngestOutcomeV1.PersistenceBusy, false);
            case SkillInvocationSnapshotReceiptProbeOutcome.Unavailable:
                return Unavailable(false);
            case SkillInvocationSnapshotReceiptProbeOutcome.DifferentFingerprint:
                return new(SkillInvocationV2IngestOutcomeV1.IdempotencyConflict, false);
            case SkillInvocationSnapshotReceiptProbeOutcome.EqualFingerprint:
                var publicReplay = SkillInvocationSnapshotReplayValidator.ValidateOwnedTransaction(
                    databasePath,
                    replayRequest,
                    timeProvider);
                var publicReplayResult = CompleteReplayProbe(publicReplay, trySealReplaySuccess);
                if (publicReplayResult is not null)
                    return publicReplayResult;
                break;
        }

        // Registry admission stays before BEGIN IMMEDIATE because moving the tuple check to the
        // transaction fence would let storage contention mask the required unavailable result.
        ISkillRegistryGenerationCapture? capture;
        try
        {
            capture = registryAuthority.CaptureGeneration();
            if (capture is null)
                return Unavailable(false);
            if (!registryAuthority.TryAcquireGenerationReadLease(capture, out var admissionLease))
                return Unavailable(false);
            using (admissionLease)
            {
                if (!registryAuthority.IsProducerTupleAccepted(admissionLease, facts.ProducerTuple))
                    return Unavailable(false);
            }
        }
        catch (Exception)
        {
            return Unavailable(false);
        }

        if (workToken.IsCancellationRequested)
            return Unavailable(false);

        try
        {
            using var connection = RetentionCatalogConnectionPolicy.OpenOrdinary(databasePath, SqliteOpenMode.ReadWrite);
            using var transaction = connection.BeginTransaction(deferred: false);
            return ExecuteMutation(
                connection,
                transaction,
                facts,
                registryAuthority,
                capture,
                replayRequest,
                timeProvider,
                trySealReplaySuccess,
                trySealCommit,
                workToken);
        }
        // Busy classification is deliberately limited to this component's own storage operations:
        // persistence_busy exclusively represents a SQLite read/write lock or commit-busy result.
        catch (SqliteException exception) when (IsBusy(exception))
        {
            return new(SkillInvocationV2IngestOutcomeV1.PersistenceBusy, false);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            return Unavailable(false);
        }
    }

    private static SkillInvocationV2IngestResultV1 ExecuteMutation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SkillInvocationV2IngestRequestFactsV1 facts,
        ISkillRegistryGenerationAuthority registryAuthority,
        ISkillRegistryGenerationCapture capture,
        SkillInvocationSnapshotReplayRequest replayRequest,
        TimeProvider timeProvider,
        Func<bool> trySealReplaySuccess,
        Func<bool> trySealCommit,
        CancellationToken workToken)
    {
        var transactionReplay = SkillInvocationSnapshotReplayValidator.ValidateInTransaction(
            connection,
            transaction,
            replayRequest,
            timeProvider);
        if (transactionReplay != SkillInvocationSnapshotReplayOutcome.ReceiptMissing)
        {
            var rollbackSucceeded = Rollback(transaction);
            if (transactionReplay == SkillInvocationSnapshotReplayOutcome.EqualReplay && !rollbackSucceeded)
                return Unavailable(false);
            return CompleteReplayProbe(transactionReplay, trySealReplaySuccess) ?? Unavailable(false);
        }

        ISkillRegistryGenerationLease? fenceLease;
        try
        {
            if (!TryAcquireFenceLease(registryAuthority, ref capture, out fenceLease))
            {
                _ = Rollback(transaction);
                return Unavailable(false);
            }
        }
        catch (Exception)
        {
            _ = Rollback(transaction);
            return Unavailable(false);
        }

        using (fenceLease)
        {
            try
            {
                if (!registryAuthority.VerifyGenerationIdentity(capture, fenceLease)
                    || !registryAuthority.IsProducerTupleAccepted(fenceLease, facts.ProducerTuple))
                {
                    _ = Rollback(transaction);
                    return Unavailable(false);
                }
            }
            catch (Exception)
            {
                _ = Rollback(transaction);
                return Unavailable(false);
            }

            DateTimeOffset writeAt;
            DateTimeOffset expiresAt;
            try
            {
                writeAt = timeProvider.GetUtcNow();
                expiresAt = new DateTimeOffset(
                    checked(writeAt.Ticks + TimeSpan.FromDays(90).Ticks),
                    writeAt.Offset);
            }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
            {
                _ = Rollback(transaction);
                return Unavailable(false);
            }

            if (workToken.IsCancellationRequested)
            {
                _ = Rollback(transaction);
                return Unavailable(false);
            }

            SessionSkillInvocationWriteOutcome writeOutcome;
            try
            {
                var write = BuildWrite(facts, writeAt, expiresAt);
                writeOutcome = SessionSkillInvocationParticipant.InsertOrVerify(connection, transaction, write);
            }
            catch (SqliteException exception) when (IsBusy(exception))
            {
                _ = Rollback(transaction);
                return new(SkillInvocationV2IngestOutcomeV1.PersistenceBusy, false);
            }
            catch (Exception exception) when (
                exception is SqliteException or InvalidOperationException or ArgumentException or FormatException
                    or OverflowException or InvalidCastException or ArgumentOutOfRangeException)
            {
                _ = Rollback(transaction);
                return Unavailable(false);
            }

            var writeResult = CompleteWrite(writeOutcome);
            if (writeResult is not null)
            {
                _ = Rollback(transaction);
                return writeResult;
            }

            bool sealCommitWon;
            try
            {
                sealCommitWon = trySealCommit();
            }
            catch (Exception)
            {
                _ = Rollback(transaction);
                return Unavailable(true);
            }
            if (!sealCommitWon)
            {
                _ = Rollback(transaction);
                return Unavailable(true);
            }

            // A won seal owns one commit result; later invalidation cannot authorize a retry or
            // a second response seal, so no registry state is consulted after this point.
            try
            {
                transaction.Commit();
                return new(SkillInvocationV2IngestOutcomeV1.Committed, true);
            }
            catch (SqliteException exception) when (IsBusy(exception))
            {
                _ = Rollback(transaction);
                return new(SkillInvocationV2IngestOutcomeV1.PersistenceBusy, true);
            }
            catch (Exception)
            {
                _ = Rollback(transaction);
                return Unavailable(true);
            }
        }
    }

    private static SkillInvocationV2IngestResultV1? CompleteReplayProbe(
        SkillInvocationSnapshotReplayOutcome outcome,
        Func<bool> trySealReplaySuccess) => outcome switch
        {
            SkillInvocationSnapshotReplayOutcome.ReceiptMissing => null,
            SkillInvocationSnapshotReplayOutcome.Busy => new(SkillInvocationV2IngestOutcomeV1.PersistenceBusy, false),
            SkillInvocationSnapshotReplayOutcome.DifferentFingerprint => new(SkillInvocationV2IngestOutcomeV1.IdempotencyConflict, false),
            SkillInvocationSnapshotReplayOutcome.Unavailable => Unavailable(false),
            SkillInvocationSnapshotReplayOutcome.EqualReplay => TrySealReplaySuccess(trySealReplaySuccess),
            _ => Unavailable(false),
        };

    private static SkillInvocationV2IngestResultV1 TrySealReplaySuccess(Func<bool> trySealReplaySuccess)
    {
        try
        {
            return trySealReplaySuccess()
                ? new(SkillInvocationV2IngestOutcomeV1.ReplaySucceeded, true)
                : Unavailable(true);
        }
        catch (Exception)
        {
            return Unavailable(true);
        }
    }

    private static bool TryAcquireFenceLease(
        ISkillRegistryGenerationAuthority registryAuthority,
        ref ISkillRegistryGenerationCapture capture,
        [NotNullWhen(true)]
        out ISkillRegistryGenerationLease? lease)
    {
        if (registryAuthority.TryAcquireGenerationReadLease(capture, out lease))
            return true;

        var recapture = registryAuthority.CaptureGeneration();
        if (recapture is null)
            return false;
        capture = recapture;
        return registryAuthority.TryAcquireGenerationReadLease(capture, out lease);
    }

    private static SessionSkillInvocationWrite BuildWrite(
        SkillInvocationV2IngestRequestFactsV1 facts,
        DateTimeOffset writeAt,
        DateTimeOffset expiresAt)
    {
        var claim = facts.ClaimFacts;
        return new SessionSkillInvocationWrite(
            SourceAdapter: SkillInvocationV2Parser.SourceAdapter,
            SourceSurface: SkillInvocationV2Parser.SourceSurface,
            SourceEventId: facts.Identity.SourceEventId,
            SourceParentEventId: facts.Identity.SourceParentEventId,
            NativeSessionId: facts.NativeSessionId,
            RunNativeId: facts.Identity.RunNativeId,
            SourceEphemeral: facts.Identity.SourceEphemeral,
            OccurredAt: facts.Identity.OccurredAt,
            SourceApplicationVersion: facts.ProducerTuple.SourceApplicationVersion,
            AdapterVersion: facts.ProducerTuple.AdapterVersion,
            NormalizationVersion: facts.ProducerTuple.NormalizationVersion,
            PayloadSchema: facts.ProducerTuple.PayloadSchema,
            SchemaFingerprint: facts.ProducerTuple.SchemaFingerprint,
            PayloadTokenUtf8: facts.PayloadTokenUtf8,
            State: facts.StateToken,
            Reason: facts.ReasonToken,
            Name: claim?.Name,
            Source: claim?.Source,
            Trigger: claim?.Trigger,
            BodySha256: Hex(claim?.Body.Sha256),
            BodyUtf8Bytes: claim?.Body.Utf8ByteLength,
            DefinitionPathSha256: Hex(claim?.DefinitionPath.Sha256),
            DefinitionPathUtf8Bytes: claim?.DefinitionPath.Utf8ByteLength,
            EventId: Guid.CreateVersion7(writeAt),
            SnapshotId: Guid.CreateVersion7(writeAt),
            ClaimId: facts.StateToken == "available" ? Guid.CreateVersion7(writeAt) : null,
            NewSessionId: Guid.CreateVersion7(writeAt),
            NewRunId: Guid.CreateVersion7(writeAt),
            WriteAt: writeAt,
            ExpiresAt: expiresAt);
    }

    private static SkillInvocationV2IngestResultV1? CompleteWrite(SessionSkillInvocationWriteOutcome outcome) => outcome switch
    {
        SessionSkillInvocationWriteOutcome.Inserted => null,
        SessionSkillInvocationWriteOutcome.ReceiptRaced or SessionSkillInvocationWriteOutcome.EventConflict =>
            new(SkillInvocationV2IngestOutcomeV1.IdempotencyConflict, false),
        SessionSkillInvocationWriteOutcome.SessionBindingInvalid or SessionSkillInvocationWriteOutcome.SessionAmbiguous
            or SessionSkillInvocationWriteOutcome.RunAmbiguous => Unavailable(false),
        _ => Unavailable(false),
    };

    private static string? Hex(ReadOnlyMemory<byte>? value) =>
        value is null ? null : Convert.ToHexString(value.Value.Span).ToLowerInvariant();

    private static bool IsBusy(SqliteException exception) => exception.SqliteErrorCode is 5 or 6;

    private static SkillInvocationV2IngestResultV1 Unavailable(bool terminalSealAttempted) =>
        new(SkillInvocationV2IngestOutcomeV1.Unavailable, terminalSealAttempted);

    private static bool Rollback(SqliteTransaction transaction)
    {
        try
        {
            transaction.Rollback();
            return true;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            return false;
        }
    }
}
