using System.Globalization;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum SkillProjectionSdkClaimWriteOutcome
{
    Inserted,
    ExistingIdentical,
}

internal sealed record SkillProjectionSdkClaimWrite(
    string ClaimId,
    string SessionId,
    string EventId,
    string SourceEventId,
    string SourceAdapter,
    string SourceSurface,
    string SourceApplicationVersion,
    string AdapterVersion,
    string NormalizationVersion,
    string PayloadSchema,
    string SchemaFingerprint,
    string PayloadSha256,
    string? ProducerTraceId,
    string? ProducerSpanId,
    string SkillName,
    string? SkillSource,
    string? InvocationTrigger,
    DateTimeOffset CreatedAt);

internal static class SkillProjectionSdkClaimParticipant
{
    private const string CreatedAtFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz";

    internal static SkillProjectionSdkClaimWriteOutcome InsertOrVerify(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SkillProjectionSdkClaimWrite claim)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(claim);

        var createdAt = FormatCreatedAt(claim.CreatedAt);
        var collisions = ReadCollisions(connection, transaction, claim);
        if (collisions.Count == 0)
        {
            Insert(connection, transaction, claim, createdAt);
            LocalWorkspaceProjectionTransactionParticipant.Instance.RefreshSessions(
                connection, transaction, [claim.SessionId], claim.CreatedAt);
            return SkillProjectionSdkClaimWriteOutcome.Inserted;
        }
        if (collisions.Count == 1 && IsIdentical(collisions[0], claim, createdAt))
            return SkillProjectionSdkClaimWriteOutcome.ExistingIdentical;

        // A colliding row that differs, or more than one colliding row via distinct
        // identity keys, is corruption of the append-only invariant: never resolved by
        // writing, only by the caller's rollback of its enclosing transaction.
        throw new InvalidOperationException("skill_projection_sdk_claim_conflict");
    }

    private static string FormatCreatedAt(DateTimeOffset createdAt)
    {
        var formatted = createdAt.ToUniversalTime().ToString(CreatedAtFormat, CultureInfo.InvariantCulture);
        if (formatted.Length != 33)
            throw new InvalidOperationException("skill_projection_sdk_claim_invalid");
        return formatted;
    }

    private static List<ExistingClaimRow> ReadCollisions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SkillProjectionSdkClaimWrite claim)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT claim_id,session_id,event_id,source_event_id,source_adapter,source_surface,
                source_application_version,adapter_version,normalization_version,payload_schema,
                schema_fingerprint,payload_sha256,producer_trace_id,producer_span_id,skill_name,
                skill_source,invocation_trigger,created_at
            FROM skill_projection_sdk_claims
            WHERE claim_id=$claim_id
               OR (session_id=$session_id AND event_id=$event_id)
               OR (source_adapter=$source_adapter AND source_event_id=$source_event_id);
            """;
        command.Parameters.AddWithValue("$claim_id", claim.ClaimId);
        command.Parameters.AddWithValue("$session_id", claim.SessionId);
        command.Parameters.AddWithValue("$event_id", claim.EventId);
        command.Parameters.AddWithValue("$source_adapter", claim.SourceAdapter);
        command.Parameters.AddWithValue("$source_event_id", claim.SourceEventId);
        using var reader = command.ExecuteReader();
        var rows = new List<ExistingClaimRow>();
        while (reader.Read())
        {
            rows.Add(new ExistingClaimRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.GetString(17)));
        }
        return rows;
    }

    private static bool IsIdentical(ExistingClaimRow row, SkillProjectionSdkClaimWrite claim, string createdAt) =>
        row.ClaimId == claim.ClaimId
        && row.SessionId == claim.SessionId
        && row.EventId == claim.EventId
        && row.SourceEventId == claim.SourceEventId
        && row.SourceAdapter == claim.SourceAdapter
        && row.SourceSurface == claim.SourceSurface
        && row.SourceApplicationVersion == claim.SourceApplicationVersion
        && row.AdapterVersion == claim.AdapterVersion
        && row.NormalizationVersion == claim.NormalizationVersion
        && row.PayloadSchema == claim.PayloadSchema
        && row.SchemaFingerprint == claim.SchemaFingerprint
        && row.PayloadSha256 == claim.PayloadSha256
        && row.ProducerTraceId == claim.ProducerTraceId
        && row.ProducerSpanId == claim.ProducerSpanId
        && row.SkillName == claim.SkillName
        && row.SkillSource == claim.SkillSource
        && row.InvocationTrigger == claim.InvocationTrigger
        && row.CreatedAt == createdAt;

    private static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SkillProjectionSdkClaimWrite claim,
        string createdAt)
    {
        // Plain INSERT, never INSERT OR REPLACE/IGNORE or an upsert clause: the
        // skill_projection_sdk_claims_insert_replacement_rejected trigger is the backstop
        // against a colliding row slipping through, and that backstop must stay live.
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO skill_projection_sdk_claims(
                claim_id,session_id,event_id,source_event_id,source_adapter,source_surface,
                source_application_version,adapter_version,normalization_version,payload_schema,
                schema_fingerprint,payload_sha256,producer_trace_id,producer_span_id,skill_name,
                skill_source,invocation_trigger,created_at)
            VALUES(
                $claim_id,$session_id,$event_id,$source_event_id,$source_adapter,$source_surface,
                $source_application_version,$adapter_version,$normalization_version,$payload_schema,
                $schema_fingerprint,$payload_sha256,$producer_trace_id,$producer_span_id,$skill_name,
                $skill_source,$invocation_trigger,$created_at);
            """;
        command.Parameters.AddWithValue("$claim_id", claim.ClaimId);
        command.Parameters.AddWithValue("$session_id", claim.SessionId);
        command.Parameters.AddWithValue("$event_id", claim.EventId);
        command.Parameters.AddWithValue("$source_event_id", claim.SourceEventId);
        command.Parameters.AddWithValue("$source_adapter", claim.SourceAdapter);
        command.Parameters.AddWithValue("$source_surface", claim.SourceSurface);
        command.Parameters.AddWithValue("$source_application_version", claim.SourceApplicationVersion);
        command.Parameters.AddWithValue("$adapter_version", claim.AdapterVersion);
        command.Parameters.AddWithValue("$normalization_version", claim.NormalizationVersion);
        command.Parameters.AddWithValue("$payload_schema", claim.PayloadSchema);
        command.Parameters.AddWithValue("$schema_fingerprint", claim.SchemaFingerprint);
        command.Parameters.AddWithValue("$payload_sha256", claim.PayloadSha256);
        command.Parameters.AddWithValue(
            "$producer_trace_id",
            claim.ProducerTraceId is null ? DBNull.Value : claim.ProducerTraceId);
        command.Parameters.AddWithValue(
            "$producer_span_id",
            claim.ProducerSpanId is null ? DBNull.Value : claim.ProducerSpanId);
        command.Parameters.AddWithValue("$skill_name", claim.SkillName);
        command.Parameters.AddWithValue(
            "$skill_source",
            claim.SkillSource is null ? DBNull.Value : claim.SkillSource);
        command.Parameters.AddWithValue(
            "$invocation_trigger",
            claim.InvocationTrigger is null ? DBNull.Value : claim.InvocationTrigger);
        command.Parameters.AddWithValue("$created_at", createdAt);
        // A SqliteException here (busy/locked, or the trigger backstop firing on a race
        // this method's own read did not observe) is left to propagate: this arm neither
        // classifies nor rolls back, the caller's enclosing transaction owns both.
        command.ExecuteNonQuery();
    }

    private readonly record struct ExistingClaimRow(
        string ClaimId,
        string SessionId,
        string EventId,
        string SourceEventId,
        string SourceAdapter,
        string SourceSurface,
        string SourceApplicationVersion,
        string AdapterVersion,
        string NormalizationVersion,
        string PayloadSchema,
        string SchemaFingerprint,
        string PayloadSha256,
        string? ProducerTraceId,
        string? ProducerSpanId,
        string SkillName,
        string? SkillSource,
        string? InvocationTrigger,
        string CreatedAt);
}
