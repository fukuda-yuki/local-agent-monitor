using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor;

internal static class MonitorHost
{
    private const string JsonContentType = "application/json";
    private const string TracePath = "/v1/traces";

    public static WebApplication Build(MonitorOptions options)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(options.Url);
        builder.WebHost.ConfigureKestrel(kestrelOptions =>
        {
            kestrelOptions.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes;
        });

        var store = new RawTelemetryStore(options.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateSchema();
        var writeGate = new SemaphoreSlim(1, 1);

        var app = builder.Build();
        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                if (exception is BadHttpRequestException badRequestException
                    && badRequestException.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    await WriteFailureAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large", "Trace payload exceeds the configured request body size limit.");
                    return;
                }

                await WriteFailureAsync(context, StatusCodes.Status500InternalServerError, "internal_error", "The request could not be processed.");
            });
        });
        app.Use(async (context, next) =>
        {
            if (!IsValidHostHeader(context.Request.Host.Host))
            {
                await WriteFailureAsync(context, StatusCodes.Status400BadRequest, "invalid_host", "Host header must be loopback.");
                return;
            }

            await next();
        });
        app.MapPost(TracePath, async context =>
        {
            if (context.Request.ContentLength > options.MaxRequestBodyBytes)
            {
                await WriteFailureAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large", "Trace payload exceeds the configured request body size limit.");
                return;
            }

            byte[] body;
            try
            {
                using var bodyStream = new MemoryStream();
                await context.Request.Body.CopyToAsync(bodyStream, context.RequestAborted);
                body = bodyStream.ToArray();
            }
            catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                await WriteFailureAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large", "Trace payload exceeds the configured request body size limit.");
                return;
            }
            catch (OperationCanceledException)
            {
                await WriteFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "shutting_down", "The local monitor is shutting down.");
                return;
            }

            string payloadJson;
            try
            {
                payloadJson = OtlpTracePayloadDecoder.DecodeTracePayload(context.Request.ContentType, body);
                OtlpTracePayloadDecoder.EnsurePayloadContainsSpan(payloadJson);
            }
            catch (UnsupportedOtlpContentTypeException)
            {
                await WriteFailureAsync(context, StatusCodes.Status415UnsupportedMediaType, "unsupported_content_type", "Only application/json and application/x-protobuf are supported.");
                return;
            }
            catch (JsonException)
            {
                await WriteFailureAsync(context, StatusCodes.Status400BadRequest, "invalid_payload", "Trace payload is not valid OTLP JSON.");
                return;
            }
            catch (InvalidDataException)
            {
                await WriteFailureAsync(context, StatusCodes.Status400BadRequest, "invalid_payload", "Trace payload is not valid OTLP trace data.");
                return;
            }

            try
            {
                await writeGate.WaitAsync(context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                await WriteFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "shutting_down", "The local monitor is shutting down.");
                return;
            }

            try
            {
                var record = RawOtlpIngestor.CreateRecordFromPayloadJson(payloadJson, DateTimeOffset.UtcNow);
                var rawRecordId = store.Insert(record);
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = JsonContentType;
                await context.Response.WriteAsync($$"""{"accepted":true,"rawRecordId":{{rawRecordId}}}""");
            }
            catch (JsonException)
            {
                await WriteFailureAsync(context, StatusCodes.Status400BadRequest, "invalid_payload", "Trace payload is not valid OTLP JSON.");
            }
            catch (InvalidDataException)
            {
                await WriteFailureAsync(context, StatusCodes.Status400BadRequest, "invalid_payload", "Trace payload is not valid OTLP trace data.");
            }
            catch (SqliteException exception) when (IsSqliteBusy(exception))
            {
                await WriteFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "persistence_busy", "Trace payload could not be persisted because the raw store is busy.");
            }
            catch (SqliteException)
            {
                await WriteFailureAsync(context, StatusCodes.Status500InternalServerError, "persistence_failed", "Trace payload could not be persisted.");
            }
            catch (IOException)
            {
                await WriteFailureAsync(context, StatusCodes.Status500InternalServerError, "persistence_failed", "Trace payload could not be persisted.");
            }
            catch (UnauthorizedAccessException)
            {
                await WriteFailureAsync(context, StatusCodes.Status500InternalServerError, "persistence_failed", "Trace payload could not be persisted.");
            }
            finally
            {
                writeGate.Release();
            }
        });
        app.MapMethods(TracePath, ["GET", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"], async context =>
        {
            await WriteFailureAsync(context, StatusCodes.Status405MethodNotAllowed, "method_not_allowed", "Only POST is supported for /v1/traces.");
        });
        app.MapFallback(async context =>
        {
            await WriteFailureAsync(context, StatusCodes.Status404NotFound, "unsupported_endpoint", "Only /v1/traces is supported.");
        });

        return app;
    }

    public static async Task<int> RunAsync(MonitorOptions options, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        await using var app = Build(options);
        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch (IOException exception) when (IsAddressInUse(exception))
        {
            error.WriteLine("error: failed to start local monitor: port is already in use.");
            return 1;
        }
        catch (OperationCanceledException)
        {
            return 1;
        }

        output.WriteLine($"Local monitor listening on {options.Url}.");
        output.WriteLine($"Raw store: {options.DatabasePath}");
        output.WriteLine("Press Ctrl+C to stop.");

        try
        {
            await app.WaitForShutdownAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }

        return 0;
    }

    private static bool IsValidHostHeader(string? host)
    {
        return !string.IsNullOrWhiteSpace(host) && MonitorOptions.IsAllowedLoopbackHost(host);
    }

    private static bool IsSqliteBusy(SqliteException exception)
    {
        return exception.SqliteErrorCode is 5 or 6;
    }

    private static bool IsAddressInUse(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current.GetType().Name.Contains("AddressInUse", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task WriteFailureAsync(HttpContext context, int statusCode, string error, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = JsonContentType;
        await context.Response.WriteAsync($$"""{"accepted":false,"error":"{{error}}","message":"{{message}}"}""");
    }
}
