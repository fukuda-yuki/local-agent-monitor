using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.Alerts.Tests;

public sealed class AlertSensitiveValueHasherTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public void Hash_IsDeterministicScopedAndDoesNotExposeLowEntropyValue()
    {
        var first = AlertSensitiveValueHasher.Hash(Key, "session-1", "permission", "true");
        var repeated = AlertSensitiveValueHasher.Hash(Key, "session-1", "permission", "true");
        var otherScope = AlertSensitiveValueHasher.Hash(Key, "session-2", "permission", "true");
        var otherPurpose = AlertSensitiveValueHasher.Hash(Key, "session-1", "file", "true");

        Assert.Equal(first, repeated);
        Assert.Equal("hmac-sha256-v1:3c2979e4f7b9521be6e81f0fe69156663b253956ba42c2c1c19611820375fec2", first);
        Assert.StartsWith("hmac-sha256-v1:", first, StringComparison.Ordinal);
        Assert.Equal(79, first.Length);
        Assert.NotEqual(first, otherScope);
        Assert.NotEqual(first, otherPurpose);
        Assert.DoesNotContain("true", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Hash_RejectsKeysShorterThanThirtyTwoBytes()
    {
        var error = Assert.Throws<AlertContractException>(() => AlertSensitiveValueHasher.Hash(new byte[31], "session-1", "permission", "true"));

        Assert.Equal("invalid_sensitive_hash_input", error.Code);
    }
}
