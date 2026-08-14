using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using GitHub.Copilot;

namespace CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

internal sealed record SkillInvocationSdkV1NormalizedEnvelope(
    string NativeSessionId,
    string SourceEventId,
    string? SourceParentEventId,
    string OccurredAt,
    string? RunNativeId,
    bool SourceEphemeral,
    SkillInvocationSdkV1NormalizedPayload Payload);

internal sealed record SkillInvocationSdkV1NormalizedPayload(
    string Name,
    string Path,
    string Content,
    string[]? AllowedTools,
    string? Description,
    string? PluginName,
    string? PluginVersion,
    string? Source,
    string? Trigger);

internal static class SkillInvocationSdkV1Mapper
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz";

    public static bool TryMap(
        string? nativeSessionId,
        SkillInvokedEvent? sourceEvent,
        [NotNullWhen(true)] out SkillInvocationSdkV1NormalizedEnvelope? envelope)
    {
        envelope = null;
        if (sourceEvent?.Data is not { } data
            || !IsUuidV4(sourceEvent.Id)
            || sourceEvent.ParentId is { } parentId && !IsUuidV4(parentId)
            || sourceEvent.Timestamp == default
            || !IsBoundedIdentity(nativeSessionId)
            || sourceEvent.AgentId is not null && !IsBoundedIdentity(sourceEvent.AgentId)
            || !IsWellFormedRequired(data.Name)
            || !IsWellFormedRequired(data.Path)
            || !IsWellFormedRequired(data.Content)
            || !IsWellFormedOptional(data.Description)
            || !IsWellFormedOptional(data.PluginName)
            || !IsWellFormedOptional(data.PluginVersion)
            || !IsWellFormedOptional(data.Source)
            || !TryReadTrigger(data.Trigger, out var trigger)
            || !TrySnapshotAllowedTools(data.AllowedTools, out var allowedTools))
        {
            return false;
        }

        envelope = new SkillInvocationSdkV1NormalizedEnvelope(
            nativeSessionId!,
            sourceEvent.Id.ToString("D", CultureInfo.InvariantCulture),
            sourceEvent.ParentId?.ToString("D", CultureInfo.InvariantCulture),
            sourceEvent.Timestamp.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture),
            sourceEvent.AgentId,
            sourceEvent.Ephemeral == true,
            new SkillInvocationSdkV1NormalizedPayload(
                data.Name,
                data.Path,
                data.Content,
                allowedTools,
                data.Description,
                data.PluginName,
                data.PluginVersion,
                data.Source,
                trigger));
        return true;
    }

    private static bool TryReadTrigger(SkillInvokedTrigger? source, out string? value)
    {
        value = source?.Value;
        return source is null || value is not null && IsWellFormedUtf16(value);
    }

    private static bool TrySnapshotAllowedTools(string[]? source, out string[]? snapshot)
    {
        snapshot = null;
        if (source is null)
        {
            return true;
        }

        if (source.Any(value => value is null || !IsWellFormedUtf16(value)))
        {
            return false;
        }

        snapshot = source.ToArray();
        return true;
    }

    private static bool IsUuidV4(Guid value)
    {
        var canonical = value.ToString("D", CultureInfo.InvariantCulture);
        return canonical[14] == '4' && canonical[19] is '8' or '9' or 'a' or 'b';
    }

    private static bool IsBoundedIdentity(string? value)
    {
        if (value is null || !IsWellFormedUtf16(value) || value.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        var scalarCount = value.EnumerateRunes().Count();
        return scalarCount is >= 1 and <= 256 && Encoding.UTF8.GetByteCount(value) <= 1_024;
    }

    private static bool IsWellFormedRequired(string? value) => value is not null && IsWellFormedUtf16(value);

    private static bool IsWellFormedOptional(string? value) => value is null || IsWellFormedUtf16(value);

    private static bool IsWellFormedUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index]))
                {
                    return false;
                }
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                return false;
            }
        }

        return true;
    }
}
