using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public static class RetentionMutationIdentifierFormats
{
    public const string PreviewIdPrefix = "rpv1_";
    public const string ConfirmationIdPrefix = "rcid1_";
    public const string ConfirmationTokenPrefix = "rt90v1_";
    public const string WorkflowKeyPrefix = "rid1_";
    public const string AuditEventIdPrefix = "rae1_";
    public const string HistoryCursorPrefix = "rhc1_";
    public const int NonceByteLength = 16;
    public const int SecretByteLength = 32;
    public const int NonceTextLength = 22;
    public const int SecretTextLength = 43;
    public const int PreviewIdLength = 28;
    public const int WorkflowKeyLength = 48;
    public const int ConfirmationTokenLength = 73;
}

public static class RetentionMutationIdentifiers
{
    public static string GeneratePreviewId() => CreatePreviewId(RandomNumberGenerator.GetBytes(RetentionMutationIdentifierFormats.NonceByteLength));
    public static string GenerateConfirmationId() => CreateConfirmationId(RandomNumberGenerator.GetBytes(RetentionMutationIdentifierFormats.NonceByteLength));
    public static string GenerateAuditEventId() => CreateAuditEventId(RandomNumberGenerator.GetBytes(RetentionMutationIdentifierFormats.NonceByteLength));
    public static string GenerateHistoryCursor() => CreateHistoryCursor(RandomNumberGenerator.GetBytes(RetentionMutationIdentifierFormats.NonceByteLength));
    public static string GenerateWorkflowKey() => CreateWorkflowKey(RandomNumberGenerator.GetBytes(RetentionMutationIdentifierFormats.SecretByteLength));
    public static string CreatePreviewId(byte[] nonce) => CreateFixed(RetentionMutationIdentifierFormats.PreviewIdPrefix, nonce, RetentionMutationIdentifierFormats.NonceByteLength);
    public static string CreateConfirmationId(byte[] nonce) => CreateFixed(RetentionMutationIdentifierFormats.ConfirmationIdPrefix, nonce, RetentionMutationIdentifierFormats.NonceByteLength);
    public static string CreateAuditEventId(byte[] nonce) => CreateFixed(RetentionMutationIdentifierFormats.AuditEventIdPrefix, nonce, RetentionMutationIdentifierFormats.NonceByteLength);
    public static string CreateHistoryCursor(byte[] nonce) => CreateFixed(RetentionMutationIdentifierFormats.HistoryCursorPrefix, nonce, RetentionMutationIdentifierFormats.NonceByteLength);
    public static string CreateWorkflowKey(byte[] secret) => CreateFixed(RetentionMutationIdentifierFormats.WorkflowKeyPrefix, secret, RetentionMutationIdentifierFormats.SecretByteLength);

    public static bool IsValidWorkflowKey(string? value) => TryParseFixed(value, RetentionMutationIdentifierFormats.WorkflowKeyPrefix, RetentionMutationIdentifierFormats.SecretByteLength, out _);
    public static bool TryParsePreviewId(string? value, out byte[] nonce) => TryParseFixed(value, RetentionMutationIdentifierFormats.PreviewIdPrefix, RetentionMutationIdentifierFormats.NonceByteLength, out nonce);
    public static bool TryParseConfirmationId(string? value, out byte[] nonce) => TryParseFixed(value, RetentionMutationIdentifierFormats.ConfirmationIdPrefix, RetentionMutationFormats.NonceByteLength, out nonce);
    public static bool TryParseAuditEventId(string? value, out byte[] nonce) => TryParseFixed(value, RetentionMutationIdentifierFormats.AuditEventIdPrefix, RetentionMutationFormats.NonceByteLength, out nonce);
    public static bool TryParseHistoryCursor(string? value, out byte[] nonce) => TryParseFixed(value, RetentionMutationIdentifierFormats.HistoryCursorPrefix, RetentionMutationFormats.NonceByteLength, out nonce);

    private static string CreateFixed(string prefix, byte[] bytes, int expectedLength)
    {
        if (bytes is not { Length: var length } || length != expectedLength) throw new ArgumentException("Invalid retention mutation identifier entropy.", nameof(bytes));
        return prefix + Base64Url.Encode(bytes);
    }

    private static bool TryParseFixed(string? value, string prefix, int expectedByteLength, out byte[] bytes)
    {
        bytes = [];
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var suffix = value[prefix.Length..];
        if (!Base64Url.TryDecode(suffix, expectedByteLength, out bytes)) return false;
        return true;
    }
}

public static class RetentionMutationFormats
{
    public const int NonceByteLength = RetentionMutationIdentifierFormats.NonceByteLength;
}

public sealed record RetentionMutationTokenParts(byte[] Nonce, byte[] Secret);

public static class RetentionMutationToken
{
    public static string Generate()
    {
        return Create(
            RandomNumberGenerator.GetBytes(RetentionMutationIdentifierFormats.NonceByteLength),
            RandomNumberGenerator.GetBytes(RetentionMutationIdentifierFormats.SecretByteLength));
    }

    public static string Create(byte[] nonce, byte[] secret)
    {
        if (nonce is not { Length: RetentionMutationIdentifierFormats.NonceByteLength }) throw new ArgumentException("Invalid confirmation nonce.", nameof(nonce));
        if (secret is not { Length: RetentionMutationIdentifierFormats.SecretByteLength }) throw new ArgumentException("Invalid confirmation secret.", nameof(secret));
        return RetentionMutationIdentifierFormats.ConfirmationTokenPrefix + Base64Url.Encode(nonce) + "_" + Base64Url.Encode(secret);
    }

    public static bool TryParse(string? token, out RetentionMutationTokenParts parts)
    {
        parts = null!;
        if (token is null || !token.StartsWith(RetentionMutationIdentifierFormats.ConfirmationTokenPrefix, StringComparison.Ordinal)) return false;
        var payload = token[RetentionMutationIdentifierFormats.ConfirmationTokenPrefix.Length..];
        var separator = payload.IndexOf('_');
        if (separator != RetentionMutationIdentifierFormats.NonceTextLength) return false;
        if (!Base64Url.TryDecode(payload[..separator], RetentionMutationIdentifierFormats.NonceByteLength, out var nonce)
            || !Base64Url.TryDecode(payload[(separator + 1)..], RetentionMutationIdentifierFormats.SecretByteLength, out var secret)) return false;
        parts = new(nonce, secret);
        return true;
    }

    public static byte[] HashFullToken(string token)
    {
        if (token is null || token.Any(character => character > 0x7f)) throw new ArgumentException("Confirmation token must be ASCII.", nameof(token));
        return SHA256.HashData(Encoding.ASCII.GetBytes(token));
    }

    public static string HashFullTokenHex(string token) => Convert.ToHexString(HashFullToken(token)).ToLowerInvariant();
}

public sealed record RetentionMutationIssueDecision(bool IssueFreshToken, bool InvalidatePriorToken, string? Code);
public sealed record RetentionMutationConsumptionDecision(bool ConsumeToken, string? Code);

public static class RetentionMutationConfirmationDecisions
{
    public static RetentionMutationIssueDecision DecideNonceCollision(bool collision) =>
        collision
            ? new(false, false, RetentionMutationErrorCodes.ConfirmationGenerationFailed)
            : new(true, false, null);

    public static RetentionMutationIssueDecision DecideIssueRetry(bool sameKeyAndRequest, bool priorTokenConsumed)
    {
        if (!sameKeyAndRequest) return new(false, false, RetentionMutationErrorCodes.IdempotencyConflict);
        return priorTokenConsumed
            ? new(false, false, RetentionMutationErrorCodes.ConfirmationConsumed)
            : new(true, true, null);
    }

    public static RetentionMutationConsumptionDecision DecideConsumption(bool priorTokenConsumed) =>
        priorTokenConsumed
            ? new(false, RetentionMutationErrorCodes.ConfirmationConsumed)
            : new(true, null);
}

internal static class Base64Url
{
    internal static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static bool TryDecode(string value, int expectedByteLength, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value) || value.Any(character => !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))) return false;
        var padding = (4 - value.Length % 4) % 4;
        if (value.Length % 4 == 1) return false;
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/') + new string('=', padding);
            bytes = Convert.FromBase64String(base64);
            return bytes.Length == expectedByteLength && string.Equals(Encode(bytes), value, StringComparison.Ordinal);
        }
        catch (FormatException) { return false; }
    }
}
