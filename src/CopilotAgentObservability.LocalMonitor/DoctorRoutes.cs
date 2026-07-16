using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.LocalMonitor;

internal static class DoctorRoutes
{
    private const int MaximumInputBytes = 65_536;

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/doctor/evaluations", EvaluateAsync);
    }

    private static async Task EvaluateAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "application/json";

        if (!IsJson(context.Request.ContentType))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteResultAsync(context, InvalidResult(DoctorResultCode.InvalidInput));
            return;
        }

        DoctorResult result;
        try
        {
            var input = await ReadBoundedUtf8Async(context.Request, context.RequestAborted);
            result = DoctorEvaluator.Evaluate(DoctorJson.DeserializeFactSnapshot(input));
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            result = InvalidResult(DoctorResultCode.InvalidInput);
        }

        context.Response.StatusCode = result.Code == DoctorResultCode.PartialFactSnapshot
            ? StatusCodes.Status422UnprocessableEntity
            : StatusCodes.Status200OK;
        await WriteResultAsync(context, result);
    }

    private static bool IsJson(string? contentType) =>
        contentType?.Split(';', 2)[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase) == true;

    private static async Task<string> ReadBoundedUtf8Async(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaximumInputBytes)
        {
            throw new JsonException("Doctor input is too large.");
        }

        var buffer = new byte[MaximumInputBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(count, buffer.Length - count), cancellationToken);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        if (count > MaximumInputBytes || await request.Body.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
        {
            throw new JsonException("Doctor input is too large.");
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(buffer, 0, count);
    }

    private static DoctorResult InvalidResult(DoctorResultCode code) =>
        new(DoctorSchemaVersions.ResultV1, Success: false, code, Evaluation: null, Verification: null);

    private static Task WriteResultAsync(HttpContext context, DoctorResult result) =>
        context.Response.WriteAsync(DoctorJson.SerializeResult(result));
}
