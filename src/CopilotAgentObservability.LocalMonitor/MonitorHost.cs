using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Ingestion;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor;

internal static class MonitorHost
{
    private const string JsonContentType = "application/json";
    private const string TracePath = "/v1/traces";

    private static readonly TimeSpan DefaultCommitTimeout = TimeSpan.FromSeconds(5);

    public static WebApplication Build(MonitorOptions options)
    {
        return Build(options, testOptions: null);
    }

    internal static WebApplication Build(MonitorOptions options, MonitorHostTestOptions? testOptions)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(options.Url);
        builder.WebHost.ConfigureKestrel(kestrelOptions =>
        {
            kestrelOptions.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes;
        });

        var queue = testOptions?.Queue ?? new IngestionQueue();
        var health = testOptions?.Health ?? new MonitorHealthState();
        health.SetLoopbackBound(true);
        var commitTimeout = testOptions?.CommitTimeout ?? DefaultCommitTimeout;

        if (testOptions?.StartWriter ?? true)
        {
            var writer = testOptions?.Writer
                ?? new RawTelemetryStoreWriter(
                    new RawTelemetryStore(options.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter));
            var worker = new IngestionWriterWorker(queue, writer, health);
            builder.Services.AddHostedService(_ => worker);
        }

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
        app.MapGet("/health/live", async context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = JsonContentType;
            await context.Response.WriteAsync("""{"status":"live"}""");
        });
        app.MapGet("/health/ready", async context =>
        {
            var readiness = health.Evaluate(options.IngestionStallThresholdSeconds);
            context.Response.StatusCode = readiness.IsReady
                ? StatusCodes.Status200OK
                : StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = JsonContentType;
            await context.Response.WriteAsync(MonitorReadinessJson.Serialize(readiness));
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

            RawTelemetryRecord record;
            try
            {
                record = RawOtlpIngestor.CreateRecordFromPayloadJson(payloadJson, DateTimeOffset.UtcNow);
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

            if (!queue.TryEnqueue(record, out var request))
            {
                health.RecordBackpressure();
                if (queue.IsClosed)
                {
                    await WriteFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "shutting_down", "The local monitor is shutting down.");
                }
                else
                {
                    await WriteFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "queue_full", "The local monitor ingestion queue is full.");
                }

                return;
            }

            IngestionCommitResult result;
            try
            {
                result = await request.Completion.WaitAsync(commitTimeout, context.RequestAborted);
            }
            catch (TimeoutException)
            {
                // A commit that never acks is a form of "writer unable to commit":
                // start the same stall window queue-full / persistence failures use
                // so sustained timeouts surface as ingestion_stalled in readiness.
                health.RecordBackpressure();
                await WriteFailureAsync(context, StatusCodes.Status504GatewayTimeout, "commit_timeout", "Trace payload was not committed within the allowed time.");
                return;
            }
            catch (OperationCanceledException)
            {
                await WriteFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "shutting_down", "The local monitor is shutting down.");
                return;
            }

            switch (result.Status)
            {
                case IngestionCommitStatus.Committed:
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = JsonContentType;
                    await context.Response.WriteAsync($$"""{"accepted":true,"rawRecordId":{{result.RawRecordId}}}""");
                    break;
                case IngestionCommitStatus.Busy:
                    await WriteFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "persistence_busy", "Trace payload could not be persisted because the raw store is busy.");
                    break;
                default:
                    await WriteFailureAsync(context, StatusCodes.Status500InternalServerError, "persistence_failed", "Trace payload could not be persisted.");
                    break;
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

internal sealed class MonitorHostTestOptions
{
    public IngestionQueue? Queue { get; init; }

    public IRawTelemetryWriter? Writer { get; init; }

    public MonitorHealthState? Health { get; init; }

    public TimeSpan? CommitTimeout { get; init; }

    public bool StartWriter { get; init; } = true;
}
