using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.ConfigCli;

internal sealed record RawLocalReceiverRequest(
    string Method,
    string Path,
    string? ContentType,
    byte[] Body,
    string DatabasePath,
    DateTimeOffset ReceivedAt);

internal sealed record RawLocalReceiverResponse(
    int StatusCode,
    string ContentType,
    string Body,
    long? RawRecordId);

internal static class RawLocalReceiverHandler
{
    private const string JsonContentType = "application/json";
    private const string TracePath = "/v1/traces";

    public static RawLocalReceiverResponse Handle(RawLocalReceiverRequest request)
    {
        if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return Failure(405, "method_not_allowed", "Only POST is supported for /v1/traces.");
        }

        if (!string.Equals(request.Path, TracePath, StringComparison.Ordinal))
        {
            return Failure(404, "unsupported_endpoint", "Only /v1/traces is supported.");
        }

        try
        {
            var payloadJson = OtlpTracePayloadDecoder.DecodeTracePayload(request.ContentType, request.Body);
            OtlpTracePayloadDecoder.EnsurePayloadContainsSpan(payloadJson);
            var record = RawOtlpIngestor.CreateRecordFromPayloadJson(payloadJson, request.ReceivedAt);
            var store = new RawTelemetryStore(request.DatabasePath);
            store.CreateSchema();
            var rawRecordId = store.Insert(record);
            return Success(rawRecordId);
        }
        catch (UnsupportedOtlpContentTypeException)
        {
            return Failure(415, "unsupported_content_type", "Only application/json and application/x-protobuf are supported.");
        }
        catch (JsonException)
        {
            return Failure(400, "invalid_payload", "Trace payload is not valid OTLP JSON.");
        }
        catch (InvalidDataException)
        {
            return Failure(400, "invalid_payload", "Trace payload is not valid OTLP trace data.");
        }
        catch (SqliteException)
        {
            return Failure(500, "persistence_failed", "Trace payload could not be persisted.");
        }
        catch (IOException)
        {
            return Failure(500, "persistence_failed", "Trace payload could not be persisted.");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(500, "persistence_failed", "Trace payload could not be persisted.");
        }
    }

    private static RawLocalReceiverResponse Success(long rawRecordId)
    {
        return new RawLocalReceiverResponse(
            StatusCode: 200,
            ContentType: JsonContentType,
            Body: $$"""{"accepted":true,"rawRecordId":{{rawRecordId}}}""",
            RawRecordId: rawRecordId);
    }

    private static RawLocalReceiverResponse Failure(int statusCode, string error, string message)
    {
        return new RawLocalReceiverResponse(
            StatusCode: statusCode,
            ContentType: JsonContentType,
            Body: $$"""{"accepted":false,"error":"{{error}}","message":"{{message}}"}""",
            RawRecordId: null);
    }

}
