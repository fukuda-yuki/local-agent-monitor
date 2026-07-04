using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class ResourceAttributeValidatorTests
{
    [Fact]
    public void Validate_WhenRequiredAttributesArePresent_Succeeds()
    {
        var result = ResourceAttributeValidator.Validate(ValidAttributes());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("user.id")]
    [InlineData("user.email")]
    [InlineData("team.id")]
    [InlineData("department")]
    [InlineData("client.kind")]
    [InlineData("experiment.id")]
    public void Validate_WhenRequiredAttributeIsMissing_ReturnsError(string missingKey)
    {
        var attributes = ValidAttributesExcept(missingKey);

        var result = ResourceAttributeValidator.Validate(attributes);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains($"'{missingKey}'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenClientKindIsNotRecommended_ReturnsWarningOnly()
    {
        var result = ResourceAttributeValidator.Validate(
            ValidAttributes(("client.kind", "other-client")));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, warning => warning.Contains("client.kind 'other-client'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenExperimentIdIsNotBaseline_ReturnsWarningOnly()
    {
        var result = ResourceAttributeValidator.Validate(
            ValidAttributes(("experiment.id", "variant-a")));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, warning => warning.Contains("experiment.id 'variant-a'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("user.id=example,,client.kind=copilot-cli")]
    [InlineData("user.id=example,client.kind")]
    [InlineData("=example,client.kind=copilot-cli")]
    public void Validate_WhenAttributeShapeIsInvalid_ReturnsError(string attributes)
    {
        var result = ResourceAttributeValidator.Validate(attributes);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    private static string ValidAttributes(params (string Key, string Value)[] overrides)
    {
        var attributes = new Dictionary<string, string>
        {
            ["user.id"] = "example-user",
            ["user.email"] = "user@example.com",
            ["team.id"] = "platform",
            ["department"] = "engineering",
            ["client.kind"] = "copilot-cli",
            ["experiment.id"] = "baseline",
        };

        foreach (var (key, value) in overrides)
        {
            attributes[key] = value;
        }

        return string.Join(',', attributes.Select(attribute => $"{attribute.Key}={attribute.Value}"));
    }

    private static string ValidAttributesExcept(string missingKey)
    {
        return string.Join(
            ',',
            ValidAttributes()
                .Split(',')
                .Where(element => !element.StartsWith($"{missingKey}=", StringComparison.Ordinal)));
    }
}
