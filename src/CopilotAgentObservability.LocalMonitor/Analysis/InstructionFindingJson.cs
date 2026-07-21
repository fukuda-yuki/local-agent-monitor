using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal static class InstructionFindingJsonV1
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    internal static byte[] Serialize(InstructionFindingHandoffV1 handoff)
    {
        InstructionFindingPipelineV1.ValidateHandoff(handoff);
        return JsonSerializer.SerializeToUtf8Bytes(handoff, Options);
    }

    internal static InstructionFindingHandoffV1 Deserialize(ReadOnlySpan<byte> bytes)
    {
        InstructionFindingHandoffV1? handoff;
        try
        {
            handoff = JsonSerializer.Deserialize<InstructionFindingHandoffV1>(bytes, Options);
        }
        catch (JsonException exception)
        {
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidSerialization, exception);
        }
        catch (NotSupportedException exception)
        {
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidSerialization, exception);
        }

        if (handoff is null)
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidSerialization);
        InstructionFindingPipelineV1.ValidateHandoff(handoff);
        return handoff;
    }

    internal static IReadOnlyList<InstructionRawEvidenceReferenceV1> DeserializeEvidenceReferences(string json)
    {
        if (string.IsNullOrEmpty(json) || Encoding.UTF8.GetByteCount(json) > 65_536)
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidSerialization);
        try
        {
            var references = JsonSerializer.Deserialize<InstructionRawEvidenceReferenceV1[]>(json, Options);
            return references ?? throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidSerialization);
        }
        catch (JsonException exception)
        {
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidSerialization, exception);
        }
        catch (NotSupportedException exception)
        {
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidSerialization, exception);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }
}
