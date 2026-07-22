using CopilotAgentObservability.LocalMonitor.Analysis;

namespace CopilotAgentObservability.InstructionFindings;

public sealed class InstructionFindingHandoffConsumerValidationException : Exception
{
    internal InstructionFindingHandoffConsumerValidationException()
        : base("The instruction finding handoff is invalid.")
    {
    }
}

public static class InstructionFindingHandoffConsumerV1
{
    public const int MaxPayloadBytes = 1_048_576;
    public const int MaxJsonDepth = 16;

    /// <summary>
    /// Validates canonical v1 structure and semantic self-consistency and returns the positive analysis-run ID.
    /// </summary>
    /// <remarks>
    /// Success does not establish producer identity, store provenance, or historical raw-reference resolution.
    /// </remarks>
    public static long Validate(ReadOnlySpan<byte> canonicalJson)
    {
        try
        {
            var handoff = InstructionFindingJsonV1.Deserialize(canonicalJson);
            return handoff.AnalysisRunId;
        }
        catch (InstructionFindingValidationException)
        {
            throw new InstructionFindingHandoffConsumerValidationException();
        }
    }
}
