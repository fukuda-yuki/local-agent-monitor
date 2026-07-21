using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CopilotAgentObservability.ConfigCli.FirstTrace;
using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.LocalMonitor;

internal static class DoctorUiRoutes
{
    private const int MaximumInputBytes = 65_536;
    private const int MaximumEnvelopeBytes = 262_144;
    private const string SchemaVersion = "doctor.ui.v1";
    private const string JsonContentType = "application/json";

    private static readonly DoctorUiSource[] Sources = FirstTraceSourceRegistry.Entries
        .Select(source => new DoctorUiSource(source.SourceId, source.DisplayLabel, Wire(source.SetupOwnership)))
        .ToArray();

    public static void Map(WebApplication app, IDoctorUiApplication application)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(application);

        app.MapGet("/api/doctor/ui/v1/sources", context => SourcesAsync(context, application));
        app.MapPost("/api/doctor/ui/v1/verifications", context => BeginAsync(context, application));
        app.MapGet(
            "/api/doctor/ui/v1/verifications/{verificationId}",
            (string verificationId, HttpContext context) => StatusAsync(context, application, verificationId));
        app.MapPost(
            "/api/doctor/ui/v1/verifications/{verificationId}/complete",
            (string verificationId, HttpContext context) => CompleteAsync(context, application, verificationId));
        app.MapPost(
            "/api/doctor/ui/v1/verifications/{verificationId}/cancel",
            (string verificationId, HttpContext context) => CancelAsync(context, application, verificationId));
    }

    internal static bool IsDoctorUiPath(PathString path) => path.StartsWithSegments("/api/doctor/ui/v1");

    internal static Task WriteInvalidHostAsync(HttpContext context)
    {
        Prepare(context);
        return Error(context, StatusCodes.Status400BadRequest, "invalid_host", "The request Host is not allowed.");
    }

    private static async Task SourcesAsync(HttpContext context, IDoctorUiApplication application)
    {
        Prepare(context);
        if (!AuthorizeHost(context))
        {
            await Error(context, StatusCodes.Status400BadRequest, "invalid_host", "The request Host is not allowed.");
            return;
        }
        if (context.Request.Query.Count != 0)
        {
            await Error(context, StatusCodes.Status400BadRequest, "invalid_payload", "The request payload is invalid.");
            return;
        }

        IReadOnlyDictionary<string, DoctorUiDetectionState> detection;
        try
        {
            detection = application.DetectSources()
                ?? throw new InvalidOperationException("Doctor source detection is unavailable.");
        }
        catch
        {
            await Error(context, StatusCodes.Status503ServiceUnavailable, "application_unavailable", "Doctor source detection is unavailable.");
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(new
        {
            schema_version = SchemaVersion,
            sources = Sources.Select(source => new
            {
                source_id = source.SourceId,
                display_label = source.DisplayLabel,
                setup_ownership = source.SetupOwnership,
                detection_state = Wire(detection.TryGetValue(source.SourceId, out var state)
                    ? state
                    : DoctorUiDetectionState.Unavailable),
            }),
        });
    }

    private static async Task BeginAsync(HttpContext context, IDoctorUiApplication application)
    {
        Prepare(context);
        if (!await AuthorizeMutation(context)) return;

        try
        {
            if (context.Request.Query.Count != 0) throw new DoctorUiTransportException();
            using var document = await ReadJson(context);
            RequireProperties(document.RootElement, ["source_id"], ["interaction", "expires_at"]);
            var sourceId = RequiredString(document.RootElement, "source_id");
            if (!Sources.Any(source => string.Equals(source.SourceId, sourceId, StringComparison.Ordinal)))
            {
                throw new DoctorUiTransportException();
            }

            var interaction = OptionalString(document.RootElement, "interaction");
            var expiresAtText = OptionalString(document.RootElement, "expires_at");
            if (interaction is not null && !IsValidInteraction(sourceId, interaction))
                throw new DoctorUiTransportException();
            DateTimeOffset? expiresAt = expiresAtText is null ? null : CanonicalTimestamp(expiresAtText);
            await Invoke(context, allowCreated: true, () => application.Begin(sourceId, interaction, expiresAt));
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            await Error(context, StatusCodes.Status400BadRequest, "invalid_payload", "The request payload is invalid.");
        }
    }

    private static async Task StatusAsync(HttpContext context, IDoctorUiApplication application, string verificationId)
    {
        Prepare(context);
        if (!AuthorizeHost(context))
        {
            await Error(context, StatusCodes.Status400BadRequest, "invalid_host", "The request Host is not allowed.");
            return;
        }

        if (!IsCanonicalUuidV7(verificationId) || context.Request.Query.Count != 0)
        {
            await Error(context, StatusCodes.Status400BadRequest, "invalid_payload", "The request payload is invalid.");
            return;
        }

        await Invoke(context, allowCreated: false, () => application.Status(verificationId));
    }

    private static async Task CompleteAsync(HttpContext context, IDoctorUiApplication application, string verificationId)
    {
        Prepare(context);
        if (!await AuthorizeMutation(context)) return;

        try
        {
            if (!IsCanonicalUuidV7(verificationId) || context.Request.Query.Count != 0) throw new DoctorUiTransportException();
            using var document = await ReadJson(context);
            RequireExactProperties(document.RootElement, "expected_revision", "accepted_evidence_refs");
            var revision = PositiveInt32(document.RootElement, "expected_revision");
            var refsElement = document.RootElement.GetProperty("accepted_evidence_refs");
            if (refsElement.ValueKind != JsonValueKind.Array) throw new DoctorUiTransportException();
            var refs = refsElement.EnumerateArray()
                .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() : null)
                .ToArray();
            if (refs.Length > 16
                || refs.Any(value => !IsEvidenceReference(value))
                || refs.Distinct(StringComparer.Ordinal).Count() != refs.Length)
            {
                throw new DoctorUiTransportException();
            }

            await Invoke(context, allowCreated: false, () => application.Complete(verificationId, revision, refs!));
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            await Error(context, StatusCodes.Status400BadRequest, "invalid_payload", "The request payload is invalid.");
        }
    }

    private static async Task CancelAsync(HttpContext context, IDoctorUiApplication application, string verificationId)
    {
        Prepare(context);
        if (!await AuthorizeMutation(context)) return;

        try
        {
            if (!IsCanonicalUuidV7(verificationId) || context.Request.Query.Count != 0) throw new DoctorUiTransportException();
            using var document = await ReadJson(context);
            RequireExactProperties(document.RootElement, "expected_revision");
            await Invoke(context, allowCreated: false, () => application.Cancel(
                verificationId,
                PositiveInt32(document.RootElement, "expected_revision")));
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            await Error(context, StatusCodes.Status400BadRequest, "invalid_payload", "The request payload is invalid.");
        }
    }

    private static async Task<bool> AuthorizeMutation(HttpContext context)
    {
        if (!AuthorizeHost(context))
        {
            await Error(context, StatusCodes.Status400BadRequest, "invalid_host", "The request Host is not allowed.");
            return false;
        }

        if (MonitorHost.IsCrossSiteRequest(context))
        {
            await Error(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden", "The Doctor action is same-origin only.");
            return false;
        }

        if (!MonitorHost.HasMonitorCsrfHeader(context))
        {
            await Error(context, StatusCodes.Status403Forbidden, "csrf_required", "The Doctor action requires the local monitor CSRF header.");
            return false;
        }

        return true;
    }

    private static bool AuthorizeHost(HttpContext context) =>
        MonitorOptions.IsAllowedLoopbackHost(context.Request.Host.Host)
        && (context.Connection.RemoteIpAddress is null || IPAddress.IsLoopback(context.Connection.RemoteIpAddress));

    private static async Task Invoke(
        HttpContext context,
        bool allowCreated,
        Func<DoctorUiApplicationResult> operation)
    {
        DoctorUiApplicationResult result;
        try
        {
            result = operation();
        }
        catch
        {
            await Error(context, StatusCodes.Status503ServiceUnavailable, "application_unavailable", "The Doctor operation is unavailable.");
            return;
        }

        if (!TryPrepareResult(result, allowCreated, out var envelope, out var targets))
        {
            await Error(context, StatusCodes.Status500InternalServerError, "invalid_application_result", "The Doctor operation returned an invalid result.");
            return;
        }

        context.Response.StatusCode = result.StatusCode;
        using (envelope)
        using (var stream = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("schema_version", SchemaVersion);
                writer.WritePropertyName("envelope");
                envelope.RootElement.WriteTo(writer);
                writer.WritePropertyName("navigation_targets");
                writer.WriteStartArray();
                foreach (var target in targets)
                {
                    writer.WriteStartObject();
                    writer.WriteString("evidence_ref", target.EvidenceRef);
                    writer.WriteString("target_kind", Wire(target.TargetKind));
                    writer.WriteString("target_id", target.TargetId);
                    writer.WriteString("href", Href(target));
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            await context.Response.Body.WriteAsync(stream.ToArray(), context.RequestAborted);
        }
    }

    private static bool TryPrepareResult(
        DoctorUiApplicationResult result,
        bool allowCreated,
        out JsonDocument envelope,
        out IReadOnlyList<DoctorUiNavigationIdentity> targets)
    {
        envelope = null!;
        targets = [];
        if (result is null
            || !IsAllowedStatusCode(result.StatusCode, allowCreated)
            || string.IsNullOrEmpty(result.EnvelopeJson)
            || Encoding.UTF8.GetByteCount(result.EnvelopeJson) > MaximumEnvelopeBytes
            || result.NavigationIdentities is null
            || result.NavigationIdentities.Count > 64)
        {
            return false;
        }

        try
        {
            envelope = JsonDocument.Parse(result.EnvelopeJson);
            if (envelope.RootElement.ValueKind != JsonValueKind.Object) return false;
            var evidenceRefs = EvidenceReferences(envelope.RootElement);
            if (result.NavigationIdentities.Any(target =>
                    target is null
                    || !evidenceRefs.Contains(target.EvidenceRef)
                    || !Enum.IsDefined(target.TargetKind)
                    || !IsTargetId(target.TargetId)
                    || target.TargetKind == DoctorUiNavigationTargetKind.Trace && !IsTraceId(target.TargetId)
                    || target.TargetKind == DoctorUiNavigationTargetKind.Session && !IsCanonicalUuidV7(target.TargetId)
                    || target.TargetKind == DoctorUiNavigationTargetKind.SourceDiagnostic && !DoctorValidation.IsValidEvidenceReference(target.TargetId))
                || result.NavigationIdentities
                    .Select(target => (target.EvidenceRef, target.TargetKind))
                    .Distinct()
                    .Count() != result.NavigationIdentities.Count)
            {
                envelope.Dispose();
                envelope = null!;
                return false;
            }
            targets = result.NavigationIdentities;
            return true;
        }
        catch (JsonException)
        {
            envelope?.Dispose();
            envelope = null!;
            return false;
        }
    }

    private static HashSet<string> EvidenceReferences(JsonElement root)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("doctor", out var doctor) || doctor.ValueKind != JsonValueKind.Object)
        {
            return values;
        }
        if (doctor.TryGetProperty("verification", out var verification)
            && verification.ValueKind == JsonValueKind.Object
            && verification.TryGetProperty("state", out var verificationState)
            && verificationState.ValueKind == JsonValueKind.String)
        {
            if (verificationState.GetString() == "completed"
                && verification.TryGetProperty("accepted_evidence_refs", out var accepted)
                && accepted.ValueKind == JsonValueKind.Array)
            {
                AddStringReferences(values, accepted, requireValidEvidenceReference: true);
            }
            if (verificationState.GetString() != "active")
            {
                return values;
            }
        }
        if (!doctor.TryGetProperty("evaluation", out var evaluation)
            || evaluation.ValueKind != JsonValueKind.Object
            || !evaluation.TryGetProperty("states", out var states)
            || states.ValueKind != JsonValueKind.Array)
        {
            return values;
        }
        foreach (var state in states.EnumerateArray())
        {
            if (state.ValueKind != JsonValueKind.Object
                || !state.TryGetProperty("evidence_refs", out var references)
                || references.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            AddStringReferences(values, references, requireValidEvidenceReference: false);
        }
        return values;
    }

    private static void AddStringReferences(
        HashSet<string> values,
        JsonElement references,
        bool requireValidEvidenceReference)
    {
        foreach (var reference in references.EnumerateArray())
        {
            if (reference.ValueKind == JsonValueKind.String
                && (!requireValidEvidenceReference
                    || DoctorValidation.IsValidEvidenceReference(reference.GetString())))
            {
                values.Add(reference.GetString()!);
            }
        }
    }

    private static string Href(DoctorUiNavigationIdentity target)
    {
        var id = Uri.EscapeDataString(target.TargetId);
        return target.TargetKind switch
        {
            DoctorUiNavigationTargetKind.Trace => $"/traces/{id}",
            DoctorUiNavigationTargetKind.Session => $"/diagnostics?session_id={id}#doctor-session",
            DoctorUiNavigationTargetKind.SourceDiagnostic => $"/diagnostics?observation_id={id}#source-diagnostics",
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
    }

    private static async Task<JsonDocument> ReadJson(HttpContext context)
    {
        if (!string.Equals(context.Request.ContentType?.Split(';', 2)[0].Trim(), JsonContentType, StringComparison.OrdinalIgnoreCase)
            || context.Request.ContentLength > MaximumInputBytes)
        {
            throw new DoctorUiTransportException();
        }
        var buffer = new byte[MaximumInputBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(count), context.RequestAborted);
            if (read == 0) break;
            count += read;
        }
        if (count > MaximumInputBytes) throw new DoctorUiTransportException();
        return JsonDocument.Parse(new UTF8Encoding(false, true).GetString(buffer, 0, count));
    }

    private static void RequireExactProperties(JsonElement root, params string[] expected)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new DoctorUiTransportException();
        var actual = root.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length || actual.Except(expected, StringComparer.Ordinal).Any())
            throw new DoctorUiTransportException();
    }

    private static void RequireProperties(JsonElement root, string[] required, string[] optional)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new DoctorUiTransportException();
        var actual = root.EnumerateObject().Select(property => property.Name).ToArray();
        if (required.Any(name => actual.Count(value => string.Equals(value, name, StringComparison.Ordinal)) != 1)
            || actual.Any(name => !required.Contains(name, StringComparer.Ordinal) && !optional.Contains(name, StringComparer.Ordinal))
            || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length)
            throw new DoctorUiTransportException();
    }

    private static string RequiredString(JsonElement root, string propertyName) =>
        root.GetProperty(propertyName).ValueKind == JsonValueKind.String
            ? root.GetProperty(propertyName).GetString()!
            : throw new DoctorUiTransportException();

    private static string? OptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : throw new DoctorUiTransportException();
    }

    private static DateTimeOffset CanonicalTimestamp(string value) =>
        DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed : throw new DoctorUiTransportException();

    private static bool IsValidInteraction(string sourceId, string interaction) => sourceId switch
    {
        "github-copilot-vscode" => interaction == "vscode-chat",
        "github-copilot-cli" => interaction == "cli",
        "github-copilot-app-sdk" => interaction == "app-sdk",
        "claude-code" => interaction is "interactive-cli" or "print" or "agent-sdk",
        _ => false,
    };

    private static int PositiveInt32(JsonElement root, string propertyName) =>
        root.GetProperty(propertyName).ValueKind == JsonValueKind.Number
        && root.GetProperty(propertyName).TryGetInt32(out var value)
        && value > 0 ? value : throw new DoctorUiTransportException();

    private static bool IsCanonicalUuidV7(string value) =>
        value.Length == 36 && Guid.TryParseExact(value, "D", out var id)
        && string.Equals(value, id.ToString("D"), StringComparison.Ordinal)
        && value[14] == '7' && value[19] is '8' or '9' or 'a' or 'b';

    private static bool IsEvidenceReference(string? value) => DoctorValidation.IsValidEvidenceReference(value);

    private static bool IsTargetId(string? value) =>
        value is { Length: >= 1 and <= 128 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static bool IsTraceId(string value) => Regex.IsMatch(value, "^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    private static bool IsAllowedStatusCode(int statusCode, bool allowCreated) =>
        statusCode is 200 or 400 or 404 or 409 or 410 or 500 or 503 || allowCreated && statusCode == 201;

    private static bool IsTransportFailure(Exception exception) => exception is
        DoctorUiTransportException or JsonException or DecoderFallbackException or InvalidOperationException
        or KeyNotFoundException or FormatException or OverflowException;

    private static void Prepare(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = JsonContentType;
    }

    private static Task Error(HttpContext context, int statusCode, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(new { schema_version = SchemaVersion, error = code, message });
    }

    private static string Wire(DoctorUiDetectionState state) => state switch
    {
        DoctorUiDetectionState.Detected => "detected",
        DoctorUiDetectionState.NotDetected => "not_detected",
        _ => "unavailable",
    };

    private static string Wire(DoctorUiNavigationTargetKind kind) => kind switch
    {
        DoctorUiNavigationTargetKind.Trace => "trace",
        DoctorUiNavigationTargetKind.Session => "session",
        DoctorUiNavigationTargetKind.SourceDiagnostic => "source_diagnostic",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string Wire(FirstTraceSetupOwnership ownership) => ownership switch
    {
        FirstTraceSetupOwnership.Managed => "managed",
        FirstTraceSetupOwnership.ManagedOnWindows => "managed_windows",
        FirstTraceSetupOwnership.CallerManaged => "caller_managed",
        FirstTraceSetupOwnership.ManagedCliAndCallerManagedAgentSdk => "managed_cli_caller_managed_agent_sdk",
        _ => throw new ArgumentOutOfRangeException(nameof(ownership)),
    };

    private sealed record DoctorUiSource(string SourceId, string DisplayLabel, string SetupOwnership);
    private sealed class DoctorUiTransportException : Exception;
}

internal interface IDoctorUiApplication
{
    IReadOnlyDictionary<string, DoctorUiDetectionState> DetectSources();
    DoctorUiApplicationResult Begin(string sourceId, string? interaction, DateTimeOffset? expiresAt);
    DoctorUiApplicationResult Status(string verificationId);
    DoctorUiApplicationResult Complete(string verificationId, int expectedRevision, IReadOnlyList<string> acceptedEvidenceRefs);
    DoctorUiApplicationResult Cancel(string verificationId, int expectedRevision);
}

internal enum DoctorUiDetectionState { Detected, NotDetected, Unavailable }
internal enum DoctorUiNavigationTargetKind { Trace, Session, SourceDiagnostic }
internal sealed record DoctorUiNavigationIdentity(string EvidenceRef, DoctorUiNavigationTargetKind TargetKind, string TargetId);
internal sealed record DoctorUiApplicationResult(
    int StatusCode,
    string EnvelopeJson,
    IReadOnlyList<DoctorUiNavigationIdentity> NavigationIdentities);
