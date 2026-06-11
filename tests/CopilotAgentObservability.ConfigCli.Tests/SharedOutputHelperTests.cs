using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class SharedOutputHelperTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("with,comma", "\"with,comma\"")]
    [InlineData("with \"quote\"", "\"with \"\"quote\"\"\"")]
    [InlineData("line one\nline two", "\"line one\nline two\"")]
    public void CsvEscaper_EscapesCellsWithExistingOutputContract(string? value, string expected)
    {
        Assert.Equal(expected, CsvEscaper.Escape(value));
    }

    [Theory]
    [InlineData("alpha,beta,gamma", new[] { "alpha", "beta", "gamma" })]
    [InlineData("\"alpha,beta\",gamma", new[] { "alpha,beta", "gamma" })]
    [InlineData("\"alpha \"\"quoted\"\"\",gamma", new[] { "alpha \"quoted\"", "gamma" })]
    [InlineData("alpha,,gamma", new[] { "alpha", "", "gamma" })]
    public void CsvLineParser_ParsesLinesWithExistingInputContract(string line, string[] expected)
    {
        Assert.Equal(expected, CsvLineParser.ParseLine(line));
    }

    [Fact]
    public void CsvLineParser_RejectsUnterminatedQuotedValue()
    {
        var exception = Assert.Throws<InvalidDataException>(() => CsvLineParser.ParseLine("\"unterminated"));

        Assert.Contains("unterminated quoted value", exception.Message);
    }

    [Fact]
    public void JsonOutput_WritesIndentedJsonWithTrailingNewLine()
    {
        var json = JsonOutput.WriteIndented(new[] { new { name = "alpha", value = (string?)null } });

        Assert.Contains(Environment.NewLine, json);
        Assert.EndsWith(Environment.NewLine, json);
        Assert.Contains("\"value\": null", json);
    }
}
