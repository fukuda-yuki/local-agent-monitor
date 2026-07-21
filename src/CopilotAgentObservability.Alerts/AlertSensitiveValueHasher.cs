namespace CopilotAgentObservability.Alerts;

public static class AlertSensitiveValueHasher
{
    private static readonly byte[] Domain = "copilot-agent-observability/alert-comparable/v1"u8.ToArray();

    public static string Hash(ReadOnlySpan<byte> key, string scopeId, string purpose, string value)
    {
        if (key.Length < 32 || !AlertValidation.IsOpaqueId(scopeId) || !AlertValidation.IsToken(purpose) || value is null)
        {
            throw new AlertContractException("invalid_sensitive_hash_input", "Sensitive comparable hash input is invalid.");
        }

        var keyCopy = key.ToArray();
        var valueBytes = System.Text.Encoding.UTF8.GetBytes(value);
        var framed = AlertHashing.Frame(Domain, System.Text.Encoding.UTF8.GetBytes(scopeId), System.Text.Encoding.UTF8.GetBytes(purpose), valueBytes);
        try
        {
            using var hmac = new System.Security.Cryptography.HMACSHA256(keyCopy);
            return "hmac-sha256-v1:" + Convert.ToHexString(hmac.ComputeHash(framed)).ToLowerInvariant();
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(keyCopy);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(valueBytes);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(framed);
        }
    }
}
