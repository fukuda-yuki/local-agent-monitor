using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class RawLocalReceiverOptionsTests
{
    [Fact]
    public void Parse_ReturnsFailureWithoutCommandName()
    {
        var result = RawLocalReceiverOptions.Parse([]);

        Assert.Null(result.Options);
        Assert.Equal("serve-raw-local-receiver requires a command name.", result.Error);
    }

    [Fact]
    public void Parse_UsesDefaultsWhenNoOptionsAreProvided()
    {
        var result = RawLocalReceiverOptions.Parse(["serve-raw-local-receiver"]);

        Assert.NotNull(result.Options);
        Assert.Null(result.Error);
        Assert.Equal(RawStoreDefaults.DefaultDatabasePath, result.Options!.DatabasePath);
        Assert.Equal("http://127.0.0.1:4319", result.Options.Url);
    }

    [Fact]
    public void Parse_UsesExplicitDbAndUrl()
    {
        var result = RawLocalReceiverOptions.Parse([
            "serve-raw-local-receiver",
            "--db",
            "custom/raw-store.db",
            "--url",
            "https://localhost:4319"]);

        Assert.NotNull(result.Options);
        Assert.Null(result.Error);
        Assert.Equal("custom/raw-store.db", result.Options!.DatabasePath);
        Assert.Equal("https://localhost:4319", result.Options.Url);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public void Parse_AllowsLoopbackHosts(string host)
    {
        var result = RawLocalReceiverOptions.Parse([
            "serve-raw-local-receiver",
            "--url",
            $"http://{host}:4319"]);

        Assert.NotNull(result.Options);
        Assert.Null(result.Error);
        Assert.Equal($"http://{host}:4319", result.Options!.Url);
    }

    [Fact]
    public void Parse_ReturnsFailureForDuplicateDbOption()
    {
        var result = RawLocalReceiverOptions.Parse([
            "serve-raw-local-receiver",
            "--db",
            "first.db",
            "--db",
            "second.db"]);

        Assert.Null(result.Options);
        Assert.Equal("serve-raw-local-receiver accepts --db only once.", result.Error);
    }

    [Fact]
    public void Parse_ReturnsFailureForDuplicateUrlOption()
    {
        var result = RawLocalReceiverOptions.Parse([
            "serve-raw-local-receiver",
            "--url",
            "http://localhost:4319",
            "--url",
            "http://127.0.0.1:4319"]);

        Assert.Null(result.Options);
        Assert.Equal("serve-raw-local-receiver accepts --url only once.", result.Error);
    }

    [Theory]
    [InlineData("serve-raw-local-receiver", "--db")]
    [InlineData("serve-raw-local-receiver", "--url")]
    public void Parse_ReturnsFailureForMissingOptionValue(string command, string option)
    {
        var result = RawLocalReceiverOptions.Parse([command, option]);

        Assert.Null(result.Options);
        Assert.Equal($"{option} requires a value.", result.Error);
    }

    [Fact]
    public void Parse_ReturnsFailureForUnknownOption()
    {
        var result = RawLocalReceiverOptions.Parse([
            "serve-raw-local-receiver",
            "--unexpected"]);

        Assert.Null(result.Options);
        Assert.Equal("unknown serve-raw-local-receiver option '--unexpected'.", result.Error);
    }

    [Theory]
    [InlineData("ftp://localhost:4319", "serve-raw-local-receiver requires an http or https URL.")]
    [InlineData("http://0.0.0.0:4319", "serve-raw-local-receiver only allows localhost, 127.0.0.1, or ::1.")]
    [InlineData("http://example.com:4319", "serve-raw-local-receiver only allows localhost, 127.0.0.1, or ::1.")]
    public void Parse_ReturnsFailureForInvalidUrl(string url, string expectedError)
    {
        var result = RawLocalReceiverOptions.Parse([
            "serve-raw-local-receiver",
            "--url",
            url]);

        Assert.Null(result.Options);
        Assert.Equal(expectedError, result.Error);
    }
}
