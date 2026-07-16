using CopilotAgentObservability.Doctor;
using CopilotAgentObservability.Telemetry;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class DoctorStoreValidation
{
    public static bool IsSourceToken(string? value, bool nullable = false)
    {
        if (value is null)
        {
            return nullable;
        }
        if (value.Length is < 1 or > 64 || !IsLowerAlphaNumeric(value[0]))
        {
            return false;
        }
        return value.All(character => IsLowerAlphaNumeric(character) || character is '.' or '_' or '-');
    }

    public static bool IsCanonicalUuidV7(string? value)
    {
        if (value is null || value.Length != 36 || value != value.ToLowerInvariant()
            || value[8] != '-' || value[13] != '-' || value[14] != '7' || value[18] != '-'
            || value[19] is not ('8' or '9' or 'a' or 'b') || value[23] != '-')
        {
            return false;
        }
        return value.Where((_, index) => index is not (8 or 13 or 18 or 23)).All(IsLowerHex);
    }

    public static bool IsEvidenceReference(string? value)
    {
        if (value is null
            || value.Length is < 1 or > 128
            || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            || MeasurementSanitizer.IsUnsafeStringValue(value))
        {
            return false;
        }

        var normalized = value.ToLowerInvariant();
        if (normalized.Contains("://", StringComparison.Ordinal)
            || normalized.StartsWith("file:", StringComparison.Ordinal)
            || normalized.StartsWith("http:", StringComparison.Ordinal)
            || normalized.StartsWith("https:", StringComparison.Ordinal)
            || normalized.StartsWith("mailto:", StringComparison.Ordinal)
            || normalized.Contains('@')
            || normalized.Contains('/')
            || normalized.Contains('\\')
            || (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/')
            || normalized.Contains("authorization", StringComparison.Ordinal)
            || normalized.Contains("credential", StringComparison.Ordinal)
            || normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("api_key", StringComparison.Ordinal)
            || normalized.Contains("api-key", StringComparison.Ordinal)
            || normalized.Contains("bearer ", StringComparison.Ordinal)
            || normalized.Contains('{')
            || normalized.Contains('}'))
        {
            return false;
        }
        return true;
    }

    public static bool IsCandidate(DoctorEvidenceCandidate candidate)
    {
        if (!IsCanonicalUuidV7(candidate.CandidateId)
            || !IsCanonicalUuidV7(candidate.VerificationId)
            || !IsSourceToken(candidate.SourceSurface)
            || !IsSourceToken(candidate.SourceAdapter, nullable: true)
            || !IsEvidenceReference(candidate.EvidenceRef)
            || candidate.ObservedAt.Offset != TimeSpan.Zero
            || candidate.ExpiresAt.Offset != TimeSpan.Zero
            || candidate.ExpiresAt <= candidate.ObservedAt)
        {
            return false;
        }

        return candidate.EvidenceClass != DoctorEvidenceClass.SyntheticProbe
            || candidate.EvidenceKind is not (DoctorEvidenceKind.ExactSessionBinding or DoctorEvidenceKind.CompletenessContent);
    }

    private static bool IsLowerAlphaNumeric(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool IsLowerHex(char character) =>
        character is >= 'a' and <= 'f' or >= '0' and <= '9';
}
