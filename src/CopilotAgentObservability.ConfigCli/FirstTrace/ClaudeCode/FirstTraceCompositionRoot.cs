using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.FirstTrace.ClaudeCode;

internal static class FirstTraceCompositionRoot
{
    public static FirstTraceOrchestrator Create() =>
        new([new ClaudeCodeFirstTraceAdapter(new SystemSetupPlatform())]);
}
