namespace CopilotAgentObservability.Telemetry;

internal static class OtlpProtobufTraceConverter
{
    public static string ConvertTraceRequestToRawOtlpJson(ReadOnlySpan<byte> payload) =>
        ConvertTraceRequest(payload).PayloadJson;

    public static DecodedOtlpTracePayload ConvertTraceRequest(ReadOnlySpan<byte> payload) =>
        OtlpProtobufStructuralWalker.Decode(payload);
}
