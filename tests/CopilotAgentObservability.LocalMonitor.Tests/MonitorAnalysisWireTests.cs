using CopilotAgentObservability.LocalMonitor.Analysis;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorAnalysisWireTests
{
    [Fact]
    public void Focus_WireValue_RoundTripsForEveryFocus()
    {
        var expectedWireValues = new Dictionary<MonitorAnalysisFocus, string>
        {
            [MonitorAnalysisFocus.Latency] = "latency",
            [MonitorAnalysisFocus.Tokens] = "tokens",
            [MonitorAnalysisFocus.Cache] = "cache",
            [MonitorAnalysisFocus.Errors] = "errors",
            [MonitorAnalysisFocus.ToolUsage] = "tool-usage",
            [MonitorAnalysisFocus.AgentFlow] = "agent-flow",
            [MonitorAnalysisFocus.InstructionDiagnosis] = "instruction-diagnosis",
        };

        foreach (var focus in Enum.GetValues<MonitorAnalysisFocus>())
        {
            Assert.Equal(expectedWireValues[focus], focus.ToWireValue());
            Assert.True(MonitorAnalysisWire.TryParseFocus(expectedWireValues[focus], out var parsed));
            Assert.Equal(focus, parsed);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("instruction_diagnosis")]
    public void TryParseFocus_RejectsUnknownValues(string? value)
    {
        Assert.False(MonitorAnalysisWire.TryParseFocus(value, out _));
    }
}
