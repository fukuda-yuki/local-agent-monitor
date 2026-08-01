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
    internal static SkillProjectionSdkClaimWriteOutcome InsertOrVerify(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SkillProjectionSdkClaimWrite claim)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(claim);
        throw new InvalidOperationException(
            "skill_projection_sdk_claim_authority_unpromoted");
    }
}
