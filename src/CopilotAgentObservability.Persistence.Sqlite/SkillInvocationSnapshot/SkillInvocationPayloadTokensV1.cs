namespace CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;

internal static class SkillInvocationPayloadTokensV1
{
    internal static string StateToken(SkillInvocationPayloadState state) => state switch
    {
        SkillInvocationPayloadState.Available => "available",
        SkillInvocationPayloadState.Malformed => "malformed",
        SkillInvocationPayloadState.Missing => "missing",
        SkillInvocationPayloadState.Binary => "binary",
        SkillInvocationPayloadState.Oversized => "oversized",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unrecognized skill invocation payload state.")
    };

    internal static string ReasonToken(SkillInvocationPayloadReason reason) => reason switch
    {
        SkillInvocationPayloadReason.None => "none",
        SkillInvocationPayloadReason.DuplicateProperty => "duplicate_property",
        SkillInvocationPayloadReason.UnknownProperty => "unknown_property",
        SkillInvocationPayloadReason.InvalidFieldType => "invalid_field_type",
        SkillInvocationPayloadReason.NameInvalid => "name_invalid",
        SkillInvocationPayloadReason.PathInvalid => "path_invalid",
        SkillInvocationPayloadReason.NameMissing => "name_missing",
        SkillInvocationPayloadReason.BodyMissing => "body_missing",
        SkillInvocationPayloadReason.DefinitionPathMissing => "definition_path_missing",
        SkillInvocationPayloadReason.BodyUnicodeInvalid => "body_unicode_invalid",
        SkillInvocationPayloadReason.PathUnicodeInvalid => "path_unicode_invalid",
        SkillInvocationPayloadReason.BodyOversized => "body_oversized",
        SkillInvocationPayloadReason.PathOversized => "path_oversized",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unrecognized skill invocation payload reason.")
    };
}
