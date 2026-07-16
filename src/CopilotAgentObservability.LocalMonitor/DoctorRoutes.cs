using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CopilotAgentObservability.Doctor;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.AspNetCore.Http.Features;

namespace CopilotAgentObservability.LocalMonitor;

internal static class DoctorRoutes
{
    private const int MaximumInputBytes = 65_536;
    private const string JsonContentType = "application/json";
    private const string CanonicalTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private static readonly Regex EmailPattern = new(
        @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex UriPattern = new(
        @"[a-z][a-z0-9+.-]*://",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void Map(WebApplication app, IDoctorHttpApplication application)
    {
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method)
                && IsDoctorBodyPath(context.Request.Path)
                && !TrySetDoctorRequestBodyLimit(context))
            {
                PrepareResponse(context);
                await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, DoctorResultCode.InternalError);
                return;
            }

            await next();
        });
        app.MapPost("/api/doctor/evaluations", context => EvaluateAsync(context, application));
        app.MapPost("/api/doctor/verifications", context => StartAsync(context, application));
        app.MapGet(
            "/api/doctor/verifications/{verificationId}",
            (string verificationId, HttpContext context) => StatusAsync(context, application, verificationId));
        app.MapPost(
            "/api/doctor/verifications/{verificationId}/complete",
            (string verificationId, HttpContext context) => CompleteAsync(context, application, verificationId));
        app.MapPost(
            "/api/doctor/verifications/{verificationId}/cancel",
            (string verificationId, HttpContext context) => CancelAsync(context, application, verificationId));
    }

    internal static bool IsDoctorPath(PathString path) => path.StartsWithSegments("/api/doctor");

    internal static Task WriteInvalidHostAsync(HttpContext context)
    {
        PrepareResponse(context);
        return WriteErrorAsync(context, StatusCodes.Status400BadRequest, DoctorResultCode.InvalidArguments);
    }

    private static async Task EvaluateAsync(HttpContext context, IDoctorHttpApplication application)
    {
        PrepareResponse(context);
        if (!IsJson(context.Request.ContentType))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, DoctorResultCode.InvalidInput);
            return;
        }

        DoctorFactSnapshot snapshot;
        try
        {
            var json = await ReadBoundedUtf8Async(context.Request, context.RequestAborted);
            snapshot = ParseFactSnapshot(json, requiredVerificationId: null, directEvaluation: true);
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, DoctorResultCode.InvalidInput);
            return;
        }

        await InvokeApplicationAsync(context, () => application.Evaluate(snapshot));
    }

    private static async Task StartAsync(HttpContext context, IDoctorHttpApplication application)
    {
        PrepareResponse(context);
        if (!AuthorizeMutation(context))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, DoctorResultCode.InvalidArguments);
            return;
        }

        if (!IsJson(context.Request.ContentType))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, DoctorResultCode.InvalidInput);
            return;
        }

        string sourceSurface;
        string? sourceAdapter;
        DateTimeOffset expiresAt;
        try
        {
            using var document = await ParseRequestAsync(context.Request, context.RequestAborted);
            var root = document.RootElement;
            RequireProperties(root, ["source_surface", "expires_at"], ["source_adapter"]);
            sourceSurface = RequiredString(root, "source_surface");
            sourceAdapter = OptionalString(root, "source_adapter");
            expiresAt = CanonicalTimestamp(RequiredString(root, "expires_at"));
            if (!IsSourceToken(sourceSurface) || sourceAdapter is not null && !IsSourceToken(sourceAdapter))
            {
                throw new DoctorTransportException();
            }
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, DoctorResultCode.InvalidInput);
            return;
        }

        await InvokeApplicationAsync(context, () => application.Start(sourceSurface, sourceAdapter, expiresAt));
    }

    private static async Task StatusAsync(
        HttpContext context,
        IDoctorHttpApplication application,
        string verificationId)
    {
        PrepareResponse(context);
        if (!IsCanonicalUuidV7(verificationId))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, DoctorResultCode.InvalidArguments);
            return;
        }

        await InvokeApplicationAsync(context, () => application.Status(verificationId));
    }

    private static async Task CompleteAsync(
        HttpContext context,
        IDoctorHttpApplication application,
        string verificationId)
    {
        PrepareResponse(context);
        if (!AuthorizeMutation(context))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, DoctorResultCode.InvalidArguments);
            return;
        }

        if (!IsCanonicalUuidV7(verificationId) || !IsJson(context.Request.ContentType))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, DoctorResultCode.InvalidInput);
            return;
        }

        int expectedRevision;
        DoctorHttpCompletionInput input;
        try
        {
            using var document = await ParseRequestAsync(context.Request, context.RequestAborted);
            var root = document.RootElement;
            RequireExactProperties(root, "expected_revision", "fact_snapshot", "accepted_evidence_refs");
            expectedRevision = PositiveInt32(root, "expected_revision");
            var snapshotElement = root.GetProperty("fact_snapshot");
            var snapshot = ParseFactSnapshot(
                snapshotElement,
                requiredVerificationId: verificationId,
                directEvaluation: false);
            if (snapshot.Observations.Count != 0)
            {
                throw new DoctorTransportException();
            }

            var evidenceElement = root.GetProperty("accepted_evidence_refs");
            if (evidenceElement.ValueKind != JsonValueKind.Array)
            {
                throw new DoctorTransportException();
            }

            var evidenceRefs = evidenceElement.EnumerateArray()
                .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() : null)
                .ToArray();
            if (evidenceRefs.Length is < 1 or > 16
                || evidenceRefs.Any(reference => !IsSafeEvidenceReference(reference))
                || evidenceRefs.Distinct(StringComparer.Ordinal).Count() != evidenceRefs.Length)
            {
                throw new DoctorTransportException();
            }

            input = new DoctorHttpCompletionInput(snapshot, evidenceRefs!);
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, DoctorResultCode.InvalidInput);
            return;
        }

        await InvokeApplicationAsync(
            context,
            () => application.Complete(verificationId, expectedRevision, input));
    }

    private static async Task CancelAsync(
        HttpContext context,
        IDoctorHttpApplication application,
        string verificationId)
    {
        PrepareResponse(context);
        if (!AuthorizeMutation(context))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, DoctorResultCode.InvalidArguments);
            return;
        }

        if (!IsCanonicalUuidV7(verificationId) || !IsJson(context.Request.ContentType))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, DoctorResultCode.InvalidInput);
            return;
        }

        int expectedRevision;
        try
        {
            using var document = await ParseRequestAsync(context.Request, context.RequestAborted);
            RequireExactProperties(document.RootElement, "expected_revision");
            expectedRevision = PositiveInt32(document.RootElement, "expected_revision");
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, DoctorResultCode.InvalidInput);
            return;
        }

        await InvokeApplicationAsync(context, () => application.Cancel(verificationId, expectedRevision));
    }

    private static async Task InvokeApplicationAsync(HttpContext context, Func<DoctorResult> action)
    {
        try
        {
            await WriteApplicationResultAsync(context, action());
        }
        catch
        {
            await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, DoctorResultCode.InternalError);
        }
    }

    private static DoctorFactSnapshot ParseFactSnapshot(
        string json,
        string? requiredVerificationId,
        bool directEvaluation)
    {
        using var document = ParseJson(json);
        return ParseFactSnapshot(document.RootElement, requiredVerificationId, directEvaluation);
    }

    private static DoctorFactSnapshot ParseFactSnapshot(
        JsonElement root,
        string? requiredVerificationId,
        bool directEvaluation)
    {
        ValidateFactSnapshotShape(root);
        var snapshot = DoctorJson.DeserializeFactSnapshot(root.GetRawText());
        if (!IsSourceToken(snapshot.SourceSurface)
            || snapshot.ExpectedSourceAdapter is not null && !IsSourceToken(snapshot.ExpectedSourceAdapter)
            || snapshot.Observations is null
            || snapshot.Observations.Count > 16
            || directEvaluation && snapshot.VerificationId is not null
            || !directEvaluation && !string.Equals(snapshot.VerificationId, requiredVerificationId, StringComparison.Ordinal)
            || snapshot.VerificationId is not null && !IsCanonicalUuidV7(snapshot.VerificationId)
            || snapshot.ExactSessionBinding is
                { Requirement: not ExactSessionBindingRequirement.NotRequired, Outcome: ExactSessionBindingOutcome.NotApplicable })
        {
            throw new DoctorTransportException();
        }

        var evidenceRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in snapshot.Observations)
        {
            if (observation is null
                || !string.Equals(observation.SourceSurface, snapshot.SourceSurface, StringComparison.Ordinal)
                || observation.SourceAdapter is not null && !IsSourceToken(observation.SourceAdapter)
                || snapshot.ExpectedSourceAdapter is not null
                    && !string.Equals(observation.SourceAdapter, snapshot.ExpectedSourceAdapter, StringComparison.Ordinal)
                || !IsSafeEvidenceReference(observation.EvidenceRef)
                || !evidenceRefs.Add(observation.EvidenceRef))
            {
                throw new DoctorTransportException();
            }
        }

        return snapshot;
    }

    private static void ValidateFactSnapshotShape(JsonElement root)
    {
        RequireExactProperties(
            root,
            "schema_version",
            "source_surface",
            "expected_source_adapter",
            "observed_at",
            "verification_id",
            "observations",
            "install_and_source_version",
            "process_receiver_and_port",
            "source_effective_configuration",
            "endpoint_reachability",
            "protocol_and_signal_compatibility",
            "source_version_and_schema_diagnostics",
            "last_ingest",
            "raw_persistence",
            "projection",
            "exact_session_binding",
            "completeness_and_content",
            "restart_or_new_process");
        RequireNullableFamily(root, "install_and_source_version", "monitor_install", "source_version", "source_feature");
        RequireNullableFamily(root, "process_receiver_and_port", "monitor_process", "receiver_bind", "port_owner");
        RequireNullableFamily(root, "source_effective_configuration", "endpoint_alignment");
        RequireNullableFamily(root, "endpoint_reachability", "reachability");
        RequireNullableFamily(root, "protocol_and_signal_compatibility", "protocol", "trace_signal");
        RequireNullableFamily(root, "source_version_and_schema_diagnostics", "compatibility", "schema");
        RequireNullableFamily(root, "last_ingest", "outcome");
        RequireNullableFamily(root, "raw_persistence", "outcome");
        RequireNullableFamily(root, "projection", "outcome");
        RequireNullableFamily(root, "exact_session_binding", "requirement", "outcome");
        RequireNullableFamily(root, "completeness_and_content", "completeness", "content_capture", "raw_access");
        RequireNullableFamily(root, "restart_or_new_process", "requirement");
        RequireFamilyEnum(root, "install_and_source_version", "monitor_install", "unknown", "installed", "not_installed");
        RequireFamilyEnum(root, "install_and_source_version", "source_version", "unknown", "supported", "unsupported");
        RequireFamilyEnum(root, "install_and_source_version", "source_feature", "unknown", "available", "unavailable");
        RequireFamilyEnum(root, "process_receiver_and_port", "monitor_process", "unknown", "running", "not_running");
        RequireFamilyEnum(root, "process_receiver_and_port", "receiver_bind", "unknown", "bound", "not_bound");
        RequireFamilyEnum(root, "process_receiver_and_port", "port_owner", "unknown", "monitor", "foreign", "none");
        RequireFamilyEnum(root, "source_effective_configuration", "endpoint_alignment", "unknown", "match", "mismatch");
        RequireFamilyEnum(root, "endpoint_reachability", "reachability", "unknown", "reachable", "unreachable");
        RequireFamilyEnum(root, "protocol_and_signal_compatibility", "protocol", "unknown", "http_protobuf", "mismatch");
        RequireFamilyEnum(root, "protocol_and_signal_compatibility", "trace_signal", "unknown", "enabled", "disabled");
        RequireFamilyEnum(root, "source_version_and_schema_diagnostics", "compatibility", "unknown", "supported", "unsupported_source_version", "feature_unavailable");
        RequireFamilyEnum(root, "source_version_and_schema_diagnostics", "schema", "unknown", "matching", "drift_detected");
        RequireFamilyEnum(root, "last_ingest", "outcome", "unknown", "none", "accepted", "rejected");
        RequireFamilyEnum(root, "raw_persistence", "outcome", "unknown", "not_persisted", "persisted");
        RequireFamilyEnum(root, "projection", "outcome", "unknown", "not_started", "pending", "completed", "failed");
        RequireFamilyEnum(root, "exact_session_binding", "requirement", "unknown", "required", "not_required");
        RequireFamilyEnum(root, "exact_session_binding", "outcome", "unknown", "unbound", "exact_bound", "not_applicable");
        RequireFamilyEnum(root, "completeness_and_content", "completeness", "unknown", "unbound", "partial", "rich", "full");
        RequireFamilyEnum(root, "completeness_and_content", "content_capture", "unknown", "enabled", "disabled", "unsupported");
        RequireFamilyEnum(root, "completeness_and_content", "raw_access", "unknown", "available", "sanitized_only");
        RequireFamilyEnum(root, "restart_or_new_process", "requirement", "unknown", "required", "not_required");

        var observations = root.GetProperty("observations");
        if (observations.ValueKind != JsonValueKind.Array)
        {
            throw new DoctorTransportException();
        }

        foreach (var observation in observations.EnumerateArray())
        {
            RequireExactProperties(
                observation,
                "source_surface",
                "source_adapter",
                "evidence_class",
                "evidence_kind",
                "evidence_ref",
                "observed_at");
            RequireEnum(observation, "evidence_class", "real_source", "synthetic_probe");
            RequireEnum(
                observation,
                "evidence_kind",
                "ingest",
                "raw_persistence",
                "projection",
                "exact_session_binding",
                "completeness_content");
        }
    }

    private static void RequireFamilyEnum(
        JsonElement root,
        string familyName,
        string propertyName,
        params string[] values)
    {
        var family = root.GetProperty(familyName);
        if (family.ValueKind != JsonValueKind.Null)
        {
            RequireEnum(family, propertyName, values);
        }
    }

    private static void RequireEnum(JsonElement element, string propertyName, params string[] values)
    {
        var property = element.GetProperty(propertyName);
        if (property.ValueKind != JsonValueKind.String
            || property.GetString() is not { } value
            || !values.Contains(value, StringComparer.Ordinal))
        {
            throw new DoctorTransportException();
        }
    }

    private static void RequireNullableFamily(JsonElement root, string propertyName, params string[] fields)
    {
        var family = root.GetProperty(propertyName);
        if (family.ValueKind != JsonValueKind.Null)
        {
            RequireExactProperties(family, fields);
        }
    }

    private static void RequireExactProperties(JsonElement element, params string[] propertyNames) =>
        RequireProperties(element, propertyNames, []);

    private static void RequireProperties(
        JsonElement element,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> optional)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new DoctorTransportException();
        }

        var allowed = required.Concat(optional).ToHashSet(StringComparer.Ordinal);
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (required.Any(name => !actual.Contains(name, StringComparer.Ordinal))
            || actual.Any(name => !allowed.Contains(name)))
        {
            throw new DoctorTransportException();
        }
    }

    private static string RequiredString(JsonElement root, string propertyName)
    {
        var property = root.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.String && property.GetString() is { } value
            ? value
            : throw new DoctorTransportException();
    }

    private static string? OptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : throw new DoctorTransportException();
    }

    private static int PositiveInt32(JsonElement root, string propertyName)
    {
        var property = root.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var value)
            && value > 0
                ? value
                : throw new DoctorTransportException();
    }

    private static DateTimeOffset CanonicalTimestamp(string value) =>
        DateTimeOffset.TryParseExact(
            value,
            CanonicalTimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
                ? parsed
                : throw new DoctorTransportException();

    private static bool IsSourceToken(string? value)
    {
        if (value is not { Length: >= 1 and <= 64 } || !IsLowerAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        return value.All(character => IsLowerAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    private static bool IsLowerAsciiLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool IsCanonicalUuidV7(string value) =>
        value.Length == 36
        && Guid.TryParseExact(value, "D", out var parsed)
        && string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal)
        && value[14] == '7'
        && value[19] is '8' or '9' or 'a' or 'b';

    private static bool IsSafeEvidenceReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Any(char.IsControl)
            || EmailPattern.IsMatch(value)
            || UriPattern.IsMatch(value)
            || Regex.IsMatch(value, @"[A-Za-z]:[\\/]", RegexOptions.CultureInvariant)
            || value.StartsWith(@"\\", StringComparison.Ordinal)
            || value.StartsWith("/", StringComparison.Ordinal)
            || value.Contains("../", StringComparison.Ordinal)
            || value.Contains(@"..\", StringComparison.Ordinal))
        {
            return false;
        }

        return !new[]
        {
            "authorization", "bearer ", "basic ", "api_key", "apikey", "credential",
            "password", "secret", "token", "prompt:", "response:", "content:",
            "tool argument", "tool result",
        }.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool AuthorizeMutation(HttpContext context) =>
        !MonitorHost.IsCrossSiteRequest(context)
        && string.Equals(context.Request.Headers["x-monitor-csrf"], "local-monitor", StringComparison.Ordinal);

    private static bool IsDoctorBodyPath(PathString path) =>
        string.Equals(path.Value, "/api/doctor/evaluations", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path.Value, "/api/doctor/verifications", StringComparison.OrdinalIgnoreCase)
        || (path.StartsWithSegments("/api/doctor/verifications", out var remaining)
            && Regex.IsMatch(
                remaining.Value ?? string.Empty,
                "^/[^/]+/(complete|cancel)$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase));

    private static bool TrySetDoctorRequestBodyLimit(HttpContext context)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is null || feature.IsReadOnly)
        {
            return false;
        }

        try
        {
            var requiredLimit = MaximumInputBytes + 1L;
            if (feature.MaxRequestBodySize is { } currentLimit && currentLimit < requiredLimit)
            {
                feature.MaxRequestBodySize = requiredLimit;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsJson(string? contentType) =>
        contentType?.Split(';', 2)[0].Trim().Equals(JsonContentType, StringComparison.OrdinalIgnoreCase) == true;

    private static async Task<JsonDocument> ParseRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken) =>
        ParseJson(await ReadBoundedUtf8Async(request, cancellationToken));

    private static JsonDocument ParseJson(string json)
    {
        var document = JsonDocument.Parse(json);
        try
        {
            EnsureDistinctProperties(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new DoctorTransportException();
            }

            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static void EnsureDistinctProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new DoctorTransportException();
                }

                EnsureDistinctProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                EnsureDistinctProperties(item);
            }
        }
    }

    private static async Task<string> ReadBoundedUtf8Async(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaximumInputBytes)
        {
            throw new DoctorTransportException();
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

        if (count > MaximumInputBytes)
        {
            throw new DoctorTransportException();
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(buffer, 0, count);
    }

    private static bool IsTransportFailure(Exception exception) =>
        exception is DoctorTransportException
            or JsonException
            or DecoderFallbackException
            or InvalidOperationException
            or KeyNotFoundException
            or FormatException
            or OverflowException;

    private static void PrepareResponse(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = JsonContentType;
    }

    private static Task WriteErrorAsync(HttpContext context, int statusCode, DoctorResultCode code)
    {
        context.Response.StatusCode = statusCode;
        return WriteResultAsync(context, InvalidResult(code));
    }

    private static Task WriteApplicationResultAsync(HttpContext context, DoctorResult result)
    {
        context.Response.StatusCode = result.Code switch
        {
            DoctorResultCode.VerificationStarted => StatusCodes.Status201Created,
            DoctorResultCode.EvaluationCompleted
                or DoctorResultCode.VerificationActive
                or DoctorResultCode.VerificationCompleted
                or DoctorResultCode.VerificationCancelled => StatusCodes.Status200OK,
            DoctorResultCode.InvalidArguments
                or DoctorResultCode.InvalidInput
                or DoctorResultCode.UnsupportedSchemaVersion => StatusCodes.Status400BadRequest,
            DoctorResultCode.VerificationNotFound => StatusCodes.Status404NotFound,
            DoctorResultCode.VerificationStale
                or DoctorResultCode.VerificationAlreadyCancelled
                or DoctorResultCode.VerificationAlreadyCompleted
                or DoctorResultCode.ExpectedSourceMismatch
                or DoctorResultCode.EvidenceNotFound => StatusCodes.Status409Conflict,
            DoctorResultCode.VerificationExpired
                or DoctorResultCode.EvidenceExpired => StatusCodes.Status410Gone,
            DoctorResultCode.PartialFactSnapshot => StatusCodes.Status422UnprocessableEntity,
            DoctorResultCode.DoctorStoreBusy
                or DoctorResultCode.DoctorStoreUnavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };
        return WriteResultAsync(context, result);
    }

    private static DoctorResult InvalidResult(DoctorResultCode code) =>
        new(DoctorSchemaVersions.ResultV1, Success: false, code, Evaluation: null, Verification: null);

    private static Task WriteResultAsync(HttpContext context, DoctorResult result) =>
        context.Response.WriteAsync(DoctorJson.SerializeResult(result));

    private sealed class DoctorTransportException : Exception;
}

internal interface IDoctorHttpApplication
{
    DoctorResult Evaluate(DoctorFactSnapshot snapshot);

    DoctorResult Start(string sourceSurface, string? sourceAdapter, DateTimeOffset expiresAt);

    DoctorResult Status(string verificationId);

    DoctorResult Complete(string verificationId, int expectedRevision, DoctorHttpCompletionInput input);

    DoctorResult Cancel(string verificationId, int expectedRevision);
}

internal sealed record DoctorHttpCompletionInput(
    DoctorFactSnapshot FactSnapshot,
    IReadOnlyList<string> AcceptedEvidenceRefs);

internal sealed class StatelessDoctorHttpApplication : IDoctorHttpApplication
{
    public static StatelessDoctorHttpApplication Instance { get; } = new();

    private StatelessDoctorHttpApplication()
    {
    }

    public DoctorResult Evaluate(DoctorFactSnapshot snapshot) => DoctorEvaluator.Evaluate(snapshot);

    public DoctorResult Start(string sourceSurface, string? sourceAdapter, DateTimeOffset expiresAt) =>
        StoreUnavailable();

    public DoctorResult Status(string verificationId) => StoreUnavailable();

    public DoctorResult Complete(string verificationId, int expectedRevision, DoctorHttpCompletionInput input) =>
        StoreUnavailable();

    public DoctorResult Cancel(string verificationId, int expectedRevision) => StoreUnavailable();

    private static DoctorResult StoreUnavailable() => new(
        DoctorSchemaVersions.ResultV1,
        Success: false,
        DoctorResultCode.DoctorStoreUnavailable,
        Evaluation: null,
        Verification: null);
}

internal sealed class SqliteDoctorHttpApplication : IDoctorHttpApplication
{
    private readonly SqliteDoctorApplicationService application;

    private SqliteDoctorHttpApplication(SqliteDoctorApplicationService application)
    {
        this.application = application;
    }

    public static SqliteDoctorHttpApplication Create(string databasePath, TimeProvider timeProvider) =>
        new(SqliteDoctorApplicationService.Create(new SqliteDoctorVerificationStore(databasePath, timeProvider)));

    public DoctorResult Evaluate(DoctorFactSnapshot snapshot) => application.Evaluate(snapshot);

    public DoctorResult Start(string sourceSurface, string? sourceAdapter, DateTimeOffset expiresAt) =>
        application.Start(sourceSurface, sourceAdapter, expiresAt);

    public DoctorResult Status(string verificationId) => application.Status(verificationId);

    public DoctorResult Complete(string verificationId, int expectedRevision, DoctorHttpCompletionInput input) =>
        application.Complete(
            verificationId,
            expectedRevision,
            input.FactSnapshot,
            input.AcceptedEvidenceRefs);

    public DoctorResult Cancel(string verificationId, int expectedRevision) =>
        application.Cancel(verificationId, expectedRevision);

    internal DoctorResult ObserveCandidate(DoctorEvidenceCandidate candidate) =>
        application.ObserveCandidate(candidate);
}
