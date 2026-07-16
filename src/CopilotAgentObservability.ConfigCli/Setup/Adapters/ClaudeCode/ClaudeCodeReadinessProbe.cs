using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode;

internal enum ClaudeCodeEndpointState
{
    LoopbackReady,
}

internal enum ClaudeCodeProtocolState
{
    HttpProtobuf,
}

internal enum ClaudeCodeSignalState
{
    Traces,
}

internal enum ClaudeCodeProcessInheritanceState
{
    NewAgentProcessRequired,
}

internal sealed record ClaudeCodeReadinessDetection(
    bool Reachable,
    string? FailureCode,
    ClaudeCodeEndpointState? EndpointState,
    ClaudeCodeProtocolState? ProtocolState,
    ClaudeCodeSignalState? SignalState,
    ClaudeCodeProcessInheritanceState? ProcessInheritanceState);

internal static class ClaudeCodeReadinessProbe
{
    private const string HealthReadyPath = "/health/ready";
    private const int TotalBudgetMilliseconds = 500;
    private const int MaximumBodyBytes = 4096;

    private static readonly HashSet<string> CheckNames = new(StringComparer.Ordinal)
    {
        "loopback_bound",
        "db_open",
        "migration_complete",
        "writer_running",
        "projection_worker_running",
        "ingestion_accepting",
        "projection_lag_seconds",
        "projection_backlog",
        "span_projection_lag_seconds",
        "span_projection_backlog",
        "projection_failure_count",
    };

    private static readonly HashSet<string> DegradedReasonNames = new(StringComparer.Ordinal)
    {
        "ingestion_backpressure",
        "projection_lag",
        "span_projection_backlog",
    };

    public static ClaudeCodeReadinessDetection Probe(
        ISetupPlatform platform,
        string canonicalOrigin,
        ClaudeCodeExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentException.ThrowIfNullOrEmpty(canonicalOrigin);

        if (context == ClaudeCodeExecutionContext.UnsupportedNativeUnix)
        {
            return Failure(SetupCodes.UnsupportedTarget);
        }

        SetupHttpProbeObservation observation;
        try
        {
            observation = platform.HttpProbe.Get(
                canonicalOrigin,
                HealthReadyPath,
                TotalBudgetMilliseconds,
                MaximumBodyBytes);
        }
        catch (Exception)
        {
            return Failure(FailureCode(context));
        }

        if (observation.Outcome != SetupHttpProbeOutcome.Response ||
            observation.StatusCode != 200 ||
            observation.TrustworthyContentLength is < 0 or > MaximumBodyBytes ||
            observation.Body is null ||
            observation.Body.Length > MaximumBodyBytes ||
            !observation.IsComplete ||
            !IsRecognizedReadiness(observation.Body))
        {
            return Failure(FailureCode(context));
        }

        return new ClaudeCodeReadinessDetection(
            true,
            null,
            ClaudeCodeEndpointState.LoopbackReady,
            ClaudeCodeProtocolState.HttpProtobuf,
            ClaudeCodeSignalState.Traces,
            ClaudeCodeProcessInheritanceState.NewAgentProcessRequired);
    }

    private static ClaudeCodeReadinessDetection Failure(string code) =>
        new(false, code, null, null, null, null);

    private static string FailureCode(ClaudeCodeExecutionContext context) =>
        context == ClaudeCodeExecutionContext.Wsl2Repository
            ? SetupCodes.Wsl2RoutingUnavailable
            : SetupCodes.EndpointUnreachable;

    private static bool IsRecognizedReadiness(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(
                body,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                });
            return TryReadBody(document.RootElement, out var readiness) &&
                HasValidInvariants(readiness);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadBody(JsonElement root, out ReadinessFacts facts)
    {
        facts = default;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var properties = new HashSet<string>(StringComparer.Ordinal);
        string? status = null;
        CheckFacts checks = default;
        HashSet<string>? reasons = null;

        foreach (var property in root.EnumerateObject())
        {
            if (!properties.Add(property.Name))
            {
                return false;
            }

            switch (property.Name)
            {
                case "status" when property.Value.ValueKind == JsonValueKind.String:
                    status = property.Value.GetString();
                    break;
                case "checks" when TryReadChecks(property.Value, out var parsedChecks):
                    checks = parsedChecks;
                    break;
                case "degraded_reasons" when TryReadReasons(property.Value, out var parsedReasons):
                    reasons = parsedReasons;
                    break;
                default:
                    return false;
            }
        }

        if (properties.Count != 3 || status is null || reasons is null || !checks.Present)
        {
            return false;
        }

        facts = new ReadinessFacts(status, checks, reasons);
        return true;
    }

    private static bool TryReadChecks(JsonElement element, out CheckFacts facts)
    {
        facts = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var properties = new HashSet<string>(StringComparer.Ordinal);
        var booleans = new Dictionary<string, bool>(StringComparer.Ordinal);
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            if (!CheckNames.Contains(property.Name) || !properties.Add(property.Name))
            {
                return false;
            }

            if (property.Name is "loopback_bound" or "db_open" or "migration_complete" or
                "writer_running" or "projection_worker_running" or "ingestion_accepting")
            {
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return false;
                }

                booleans.Add(property.Name, property.Value.GetBoolean());
            }
            else
            {
                if (property.Value.ValueKind != JsonValueKind.Number ||
                    !property.Value.TryGetInt32(out var value) || value < 0 ||
                    !IsCanonicalNonNegativeInteger(property.Value.GetRawText()))
                {
                    return false;
                }

                numbers.Add(property.Name, value);
            }
        }

        if (properties.Count != CheckNames.Count)
        {
            return false;
        }

        facts = new CheckFacts(
            true,
            booleans["loopback_bound"],
            booleans["db_open"],
            booleans["migration_complete"],
            booleans["writer_running"],
            booleans["projection_worker_running"],
            booleans["ingestion_accepting"],
            numbers["projection_lag_seconds"],
            numbers["projection_backlog"],
            numbers["span_projection_lag_seconds"],
            numbers["span_projection_backlog"],
            numbers["projection_failure_count"]);
        return true;
    }

    private static bool TryReadReasons(JsonElement element, out HashSet<string> reasons)
    {
        reasons = new HashSet<string>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                item.GetString() is not { } reason ||
                !DegradedReasonNames.Contains(reason) ||
                !reasons.Add(reason))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasValidInvariants(ReadinessFacts readiness)
    {
        var checks = readiness.Checks;
        if (!checks.LoopbackBound || !checks.DbOpen || !checks.MigrationComplete ||
            !checks.WriterRunning || !checks.ProjectionWorkerRunning ||
            checks.ProjectionLagSeconds > 0 && checks.ProjectionBacklog == 0 ||
            checks.SpanProjectionLagSeconds > 0 && checks.SpanProjectionBacklog == 0)
        {
            return false;
        }

        if (readiness.Status == "ready")
        {
            return readiness.Reasons.Count == 0 &&
                checks.IngestionAccepting &&
                checks.ProjectionLagSeconds == 0 &&
                checks.SpanProjectionLagSeconds == 0 &&
                checks.SpanProjectionBacklog == 0;
        }

        if (readiness.Status != "degraded" || readiness.Reasons.Count == 0)
        {
            return false;
        }

        return readiness.Reasons.Contains("ingestion_backpressure") == !checks.IngestionAccepting &&
            readiness.Reasons.Contains("projection_lag") == (checks.ProjectionLagSeconds > 0) &&
            readiness.Reasons.Contains("span_projection_backlog") ==
                (checks.SpanProjectionLagSeconds > 0 || checks.SpanProjectionBacklog > 0);
    }

    private static bool IsCanonicalNonNegativeInteger(string raw) =>
        raw.Length > 0 && raw.All(value => value is >= '0' and <= '9');

    private readonly record struct ReadinessFacts(
        string Status,
        CheckFacts Checks,
        HashSet<string> Reasons);

    private readonly record struct CheckFacts(
        bool Present,
        bool LoopbackBound,
        bool DbOpen,
        bool MigrationComplete,
        bool WriterRunning,
        bool ProjectionWorkerRunning,
        bool IngestionAccepting,
        int ProjectionLagSeconds,
        int ProjectionBacklog,
        int SpanProjectionLagSeconds,
        int SpanProjectionBacklog,
        int ProjectionFailureCount);
}
