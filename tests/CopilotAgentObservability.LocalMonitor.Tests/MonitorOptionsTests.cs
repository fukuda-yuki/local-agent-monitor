namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorOptionsTests
{
    [Fact]
    public void Parse_DefaultsToLoopbackPort4320AndRawViewOff()
    {
        var result = MonitorOptions.Parse([]);

        Assert.Null(result.Error);
        Assert.Equal(RawStoreDefaults.DefaultDatabasePath, result.Options!.DatabasePath);
        Assert.Equal("http://127.0.0.1:4320", result.Options.Url);
        Assert.False(result.Options.EnableRawView);
        Assert.Equal(31_457_280, result.Options.MaxRequestBodyBytes);
    }

    [Fact]
    public void Parse_PortSetsLoopbackUrl()
    {
        var result = MonitorOptions.Parse(["--port", "54321"]);

        Assert.Null(result.Error);
        Assert.Equal("http://127.0.0.1:54321", result.Options!.Url);
    }

    [Fact]
    public void Parse_RejectsUrlAndPortTogether()
    {
        var result = MonitorOptions.Parse(["--url", "http://127.0.0.1:4321", "--port", "4322"]);

        Assert.Equal("local-monitor accepts either --url or --port, not both.", result.Error);
    }

    [Theory]
    [InlineData("http://0.0.0.0:4320")]
    [InlineData("http://192.168.0.10:4320")]
    [InlineData("http://example.com:4320")]
    public void Parse_RejectsNonLoopbackUrl(string url)
    {
        var result = MonitorOptions.Parse(["--url", url]);

        Assert.Equal("local-monitor only allows localhost, 127.0.0.1, or ::1.", result.Error);
    }

    [Fact]
    public void Parse_UsesMaxRequestBodyBytesEnvironmentFallback()
    {
        var result = MonitorOptions.Parse(
            [],
            name => name == MonitorOptions.MaxRequestBodyBytesEnvironmentVariable ? "1024" : null);

        Assert.Null(result.Error);
        Assert.Equal(1024, result.Options!.MaxRequestBodyBytes);
    }

    [Theory]
    [InlineData("--max-request-body-bytes", "0")]
    [InlineData("--max-request-body-bytes", "-1")]
    [InlineData("--max-request-body-bytes", "abc")]
    public void Parse_RejectsInvalidMaxRequestBodyBytes(string option, string value)
    {
        var result = MonitorOptions.Parse([option, value]);

        Assert.Equal("--max-request-body-bytes requires a positive integer.", result.Error);
    }

    [Fact]
    public void Parse_RejectsUnknownOption()
    {
        var result = MonitorOptions.Parse(["--unexpected"]);

        Assert.Equal("unknown local-monitor option '--unexpected'.", result.Error);
    }
}
