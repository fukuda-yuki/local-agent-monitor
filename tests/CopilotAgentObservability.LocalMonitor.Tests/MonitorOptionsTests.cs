namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorOptionsTests
{
    [Fact]
    public void Parse_DefaultsToLoopbackPort4320AndRawShown()
    {
        var result = MonitorOptions.Parse([]);

        Assert.Null(result.Error);
        Assert.Equal(RawStoreDefaults.DefaultDatabasePath, result.Options!.DatabasePath);
        Assert.Equal("http://127.0.0.1:4320", result.Options.Url);
        // D023: raw is shown by default; --sanitized-only is the opt-out.
        Assert.False(result.Options.SanitizedOnly);
        Assert.Equal(31_457_280, result.Options.MaxRequestBodyBytes);
    }

    [Fact]
    public void Parse_SanitizedOnlyFlagRestoresMetadataOnlyMode()
    {
        var result = MonitorOptions.Parse(["--sanitized-only"]);

        Assert.Null(result.Error);
        Assert.True(result.Options!.SanitizedOnly);
    }

    [Fact]
    public void Parse_RejectsRemovedEnableRawViewFlag()
    {
        var result = MonitorOptions.Parse(["--enable-raw-view"]);

        Assert.Equal("unknown local-monitor option '--enable-raw-view'.", result.Error);
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

    [Fact]
    public void Parse_DefaultsToReadinessThresholdSeconds()
    {
        var result = MonitorOptions.Parse([]);

        Assert.Null(result.Error);
        Assert.Equal(10, result.Options!.IngestionStallThresholdSeconds);
        Assert.Equal(60, result.Options.ProjectionLagThresholdSeconds);
    }

    [Fact]
    public void Parse_OverridesIngestionStallThresholdSeconds()
    {
        var result = MonitorOptions.Parse(["--ingestion-stall-threshold-seconds", "3"]);

        Assert.Null(result.Error);
        Assert.Equal(3, result.Options!.IngestionStallThresholdSeconds);
    }

    [Fact]
    public void Parse_OverridesProjectionLagThresholdSeconds()
    {
        var result = MonitorOptions.Parse(["--projection-lag-threshold-seconds", "7"]);

        Assert.Null(result.Error);
        Assert.Equal(7, result.Options!.ProjectionLagThresholdSeconds);
    }

    [Fact]
    public void Parse_UsesIngestionStallThresholdSecondsEnvironmentFallback()
    {
        var result = MonitorOptions.Parse(
            [],
            name => name == MonitorOptions.IngestionStallThresholdSecondsEnvironmentVariable ? "4" : null);

        Assert.Null(result.Error);
        Assert.Equal(4, result.Options!.IngestionStallThresholdSeconds);
    }

    [Fact]
    public void Parse_UsesProjectionLagThresholdSecondsEnvironmentFallback()
    {
        var result = MonitorOptions.Parse(
            [],
            name => name == MonitorOptions.ProjectionLagThresholdSecondsEnvironmentVariable ? "8" : null);

        Assert.Null(result.Error);
        Assert.Equal(8, result.Options!.ProjectionLagThresholdSeconds);
    }

    [Fact]
    public void Parse_CliIngestionStallThresholdSecondsOverridesEnvironmentFallback()
    {
        var result = MonitorOptions.Parse(
            ["--ingestion-stall-threshold-seconds", "3"],
            name => name == MonitorOptions.IngestionStallThresholdSecondsEnvironmentVariable ? "4" : null);

        Assert.Null(result.Error);
        Assert.Equal(3, result.Options!.IngestionStallThresholdSeconds);
    }

    [Theory]
    [InlineData("--ingestion-stall-threshold-seconds", "0")]
    [InlineData("--ingestion-stall-threshold-seconds", "-1")]
    [InlineData("--ingestion-stall-threshold-seconds", "abc")]
    public void Parse_RejectsInvalidIngestionStallThresholdSeconds(string option, string value)
    {
        var result = MonitorOptions.Parse([option, value]);

        Assert.Equal("--ingestion-stall-threshold-seconds requires a positive integer.", result.Error);
    }

    [Theory]
    [InlineData("--projection-lag-threshold-seconds", "0")]
    [InlineData("--projection-lag-threshold-seconds", "-1")]
    [InlineData("--projection-lag-threshold-seconds", "abc")]
    public void Parse_RejectsInvalidProjectionLagThresholdSeconds(string option, string value)
    {
        var result = MonitorOptions.Parse([option, value]);

        Assert.Equal("--projection-lag-threshold-seconds requires a positive integer.", result.Error);
    }

    [Fact]
    public void Parse_RejectsInvalidIngestionStallThresholdSecondsEnvironment()
    {
        var result = MonitorOptions.Parse(
            [],
            name => name == MonitorOptions.IngestionStallThresholdSecondsEnvironmentVariable ? "0" : null);

        Assert.Equal("CAO_MONITOR_INGESTION_STALL_THRESHOLD_SECONDS requires a positive integer.", result.Error);
    }

    [Fact]
    public void Parse_RejectsInvalidProjectionLagThresholdSecondsEnvironment()
    {
        var result = MonitorOptions.Parse(
            [],
            name => name == MonitorOptions.ProjectionLagThresholdSecondsEnvironmentVariable ? "abc" : null);

        Assert.Equal("CAO_MONITOR_PROJECTION_LAG_THRESHOLD_SECONDS requires a positive integer.", result.Error);
    }

    [Fact]
    public void Parse_RejectsDuplicateIngestionStallThresholdSeconds()
    {
        var result = MonitorOptions.Parse(
            ["--ingestion-stall-threshold-seconds", "3", "--ingestion-stall-threshold-seconds", "4"]);

        Assert.Equal("local-monitor accepts --ingestion-stall-threshold-seconds only once.", result.Error);
    }

    [Fact]
    public void Parse_RejectsDuplicateProjectionLagThresholdSeconds()
    {
        var result = MonitorOptions.Parse(
            ["--projection-lag-threshold-seconds", "7", "--projection-lag-threshold-seconds", "8"]);

        Assert.Equal("local-monitor accepts --projection-lag-threshold-seconds only once.", result.Error);
    }
}
