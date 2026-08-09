using System.Buffers;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CopilotAgentObservability.LocalMonitor.Sessions;

namespace CopilotAgentObservability.LocalMonitor.HookForwarding;

internal static class HookForwardCommand
{
    internal const string CommandName = "hook-forward";
    internal const string VersionHeader = "X-CAO-Session-Event-Version";
    internal const string IngestPath = "/api/session-ingest/v1/events";
    internal const int DefaultTimeoutMilliseconds = 250;
    internal const string ClaudeAdapterVersion = "claude-hook-v1";
    internal const string ClaudeNormalizationVersion = "session-normalization-v1";

    private static readonly IReadOnlyDictionary<string, string> EventTypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SessionStart"] = "SessionStart",
            ["UserPromptSubmit"] = "UserPromptSubmit",
            ["PreToolUse"] = "PreToolUse",
            ["PostToolUse"] = "PostToolUse",
            ["PostToolUseFailure"] = "PostToolUseFailure",
            ["PermissionRequest"] = "PermissionRequest",
            ["SubagentStart"] = "SubagentStart",
            ["SubagentStop"] = "SubagentStop",
            ["Stop"] = "Stop",
            ["SessionEnd"] = "SessionEnd",
        };

    public static async Task<int> RunAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        HttpMessageHandler? handler,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        _ = output;
        _ = error;

        try
        {
            if (!TryParseOptions(args, out var options))
            {
                return 0;
            }

            var inputJson = await input.ReadToEndAsync(cancellationToken);
            var created = options.Source == "claude-code"
                ? TryCreateClaudeEnvelope(inputJson, options, timeProvider ?? TimeProvider.System, out var envelope)
                : TryCreateEnvelope(inputJson, out envelope);
            if (!created)
            {
                return 0;
            }

            using var client = handler is null
                ? new HttpClient()
                : new HttpClient(handler, disposeHandler: false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(options.TimeoutMilliseconds));
            using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation(VersionHeader, "1");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch
        {
            // Hooks are observational and must never affect the agent decision.
        }

        return 0;
    }

    private static bool TryParseOptions(string[] args, out HookForwardOptions options)
    {
        options = null!;
        var timeoutMilliseconds = DefaultTimeoutMilliseconds;
        string? endpointValue = null;
        string? source = null;
        string? sourceVersion = null;
        string? schemaFingerprint = null;
        var timeoutSpecified = false;

        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                return false;
            }

            switch (args[index])
            {
                case "--endpoint" when endpointValue is null:
                    endpointValue = args[index + 1];
                    break;
                case "--timeout-ms" when !timeoutSpecified
                    && int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    && parsed > 0:
                    timeoutMilliseconds = parsed;
                    timeoutSpecified = true;
                    break;
                case "--source" when source is null:
                    source = args[index + 1];
                    break;
                case "--source-version" when sourceVersion is null:
                    sourceVersion = args[index + 1];
                    break;
                case "--schema-fingerprint" when schemaFingerprint is null:
                    schemaFingerprint = args[index + 1];
                    break;
                default:
                    return false;
            }
        }

        if (endpointValue is null || !Uri.TryCreate(endpointValue, UriKind.Absolute, out var baseUri))
        {
            return false;
        }

        if ((baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !IsLoopback(baseUri)
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            return false;
        }

        var path = baseUri.AbsolutePath.TrimEnd('/');
        if (path.Length > 0 && !string.Equals(path, IngestPath, StringComparison.Ordinal))
        {
            return false;
        }

        if (source is null)
        {
            if (sourceVersion is not null || schemaFingerprint is not null)
            {
                return false;
            }
        }
        else if (source != "claude-code" || sourceVersion is null && schemaFingerprint is null)
        {
            return false;
        }

        options = new HookForwardOptions(
            path.Length == 0 ? new Uri(baseUri, IngestPath) : baseUri,
            timeoutMilliseconds,
            source,
            sourceVersion,
            schemaFingerprint);
        return true;
    }

    private static bool IsLoopback(Uri uri)
    {
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }

    private static bool TryCreateClaudeEnvelope(
        string inputJson,
        HookForwardOptions options,
        TimeProvider timeProvider,
        out string envelope)
    {
        envelope = string.Empty;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(inputJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var metadata = new ClaudeHookSourceMetadata(
                ClaudeAdapterVersion,
                ClaudeNormalizationVersion,
                options.SourceVersion,
                options.SchemaFingerprint);
            if (!ClaudeHookEventMapper.TryMap(
                document.RootElement,
                timeProvider.GetUtcNow(),
                metadata,
                contentCaptureEnabled: true,
                out var mapped))
            {
                return false;
            }

            envelope = JsonSerializer.Serialize(mapped);
            return true;
        }
    }

    private static bool TryCreateEnvelope(string inputJson, out string envelope)
    {
        envelope = string.Empty;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(inputJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string nativeSessionId;
            string eventType;
            string sourceSurface;
            DateTimeOffset occurredAt;
            if (HasExactRootProperty(root, "hookName"))
            {
                if (!TryReadPermissionRequest(root, out nativeSessionId, out occurredAt))
                {
                    return false;
                }

                eventType = "PermissionRequest";
                sourceSurface = "hook-unknown";
            }
            else
            {
                if (!TryGetString(root, ["session_id", "sessionId", "conversation_id", "conversationId"], out nativeSessionId)
                    || string.IsNullOrWhiteSpace(nativeSessionId)
                    || nativeSessionId.Length > 256
                    || !TryGetString(root, ["hook_event_name", "hookEventName", "event_name", "eventName"], out var hookName)
                    || !EventTypes.TryGetValue(hookName, out var mappedEventType)
                    || mappedEventType is null)
                {
                    return false;
                }

                eventType = mappedEventType;
                sourceSurface = TryGetString(root, ["source_surface", "sourceSurface"], out var suppliedSurface)
                    && (suppliedSurface == "copilot-cli" || suppliedSurface == "vscode")
                        ? suppliedSurface
                        : "hook-unknown";
                occurredAt = TryGetString(root, ["timestamp", "occurred_at", "occurredAt"], out var timestamp)
                    && DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedTimestamp)
                        ? parsedTimestamp
                        : DateTimeOffset.UtcNow;
            }

            if (!IsOptionalStringValid(root, ["parent_event_id", "parentEventId"], 256)
                || !IsOptionalStringValid(root, ["run_id", "runId", "run_native_id", "runNativeId"], 256)
                || !IsOptionalStringValid(root, ["trace_id", "traceId"], 128))
            {
                return false;
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schema_version", 1);
                writer.WriteString("source_adapter", "copilot-compatible-hook");
                writer.WriteString("source_surface", sourceSurface);
                writer.WriteString("native_session_id", nativeSessionId);
                writer.WritePropertyName("events");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("source_event_id", ComputeCanonicalHash(root));
                writer.WriteString("type", eventType);
                writer.WriteString("occurred_at", occurredAt.ToString("O", CultureInfo.InvariantCulture));
                WriteOptionalString(writer, root, "parent_event_id", ["parent_event_id", "parentEventId"]);
                WriteOptionalString(writer, root, "run_native_id", ["run_id", "runId", "run_native_id", "runNativeId"]);
                WriteOptionalString(writer, root, "trace_id", ["trace_id", "traceId"]);
                writer.WritePropertyName("payload");
                WriteSanitized(root, writer);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            envelope = Encoding.UTF8.GetString(stream.ToArray());
            return true;
        }
    }

    private static bool HasExactRootProperty(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.NameEquals(name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadPermissionRequest(
        JsonElement root,
        out string nativeSessionId,
        out DateTimeOffset occurredAt)
    {
        nativeSessionId = string.Empty;
        occurredAt = default;
        JsonElement hookName = default;
        JsonElement sessionId = default;
        JsonElement timestamp = default;
        JsonElement cwd = default;
        JsonElement toolName = default;
        JsonElement toolInput = default;
        JsonElement permissionSuggestions = default;
        var seen = 0;

        foreach (var property in root.EnumerateObject())
        {
            int bit;
            switch (property.Name)
            {
                case "hookName":
                    bit = 1 << 0;
                    hookName = property.Value;
                    break;
                case "sessionId":
                    bit = 1 << 1;
                    sessionId = property.Value;
                    break;
                case "timestamp":
                    bit = 1 << 2;
                    timestamp = property.Value;
                    break;
                case "cwd":
                    bit = 1 << 3;
                    cwd = property.Value;
                    break;
                case "toolName":
                    bit = 1 << 4;
                    toolName = property.Value;
                    break;
                case "toolInput":
                    bit = 1 << 5;
                    toolInput = property.Value;
                    break;
                case "permissionSuggestions":
                    bit = 1 << 6;
                    permissionSuggestions = property.Value;
                    break;
                default:
                    return false;
            }

            if ((seen & bit) != 0)
            {
                return false;
            }

            seen |= bit;
        }

        if (seen != 0b111_1111
            || hookName.ValueKind != JsonValueKind.String
            || !hookName.ValueEquals("PermissionRequest")
            || !TryReadPermissionIdentifier(sessionId, out nativeSessionId)
            || !TryReadPermissionIdentifier(toolName, out _)
            || cwd.ValueKind != JsonValueKind.String
            || toolInput.ValueKind != JsonValueKind.Object
            || permissionSuggestions.ValueKind != JsonValueKind.Array
            || !TryReadPermissionTimestamp(timestamp, out occurredAt))
        {
            return false;
        }

        return true;
    }

    private static bool TryReadPermissionIdentifier(JsonElement element, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        try
        {
            value = element.GetString()!;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        var remaining = value.AsSpan();
        var scalarCount = 0;
        var utf8ByteCount = 0;
        var hasNonWhitespace = false;
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                return false;
            }

            scalarCount++;
            utf8ByteCount += rune.Utf8SequenceLength;
            if (scalarCount > 256 || utf8ByteCount > 1024)
            {
                return false;
            }

            hasNonWhitespace |= !IsPermissionWhitespace(rune.Value);
            remaining = remaining[consumed..];
        }

        return scalarCount > 0 && hasNonWhitespace;
    }

    private static bool IsPermissionWhitespace(int scalarValue) => scalarValue is
        >= 0x0009 and <= 0x000D
        or 0x0020
        or 0x0085
        or 0x00A0
        or 0x1680
        or >= 0x2000 and <= 0x200A
        or 0x2028
        or 0x2029
        or 0x202F
        or 0x205F
        or 0x3000;

    private static bool TryReadPermissionTimestamp(JsonElement element, out DateTimeOffset occurredAt)
    {
        occurredAt = default;
        if (element.ValueKind != JsonValueKind.Number
            || !TryParseExactIntegralNumber(element.GetRawText(), out var unixMilliseconds)
            || unixMilliseconds < -62_135_596_800_000L
            || unixMilliseconds > 253_402_300_799_999L)
        {
            return false;
        }

        occurredAt = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
        return true;
    }

    private static bool TryParseExactIntegralNumber(string raw, out long value)
    {
        value = 0;
        var index = 0;
        var negative = raw[index] == '-';
        if (negative)
        {
            index++;
        }

        var exponentIndex = raw.IndexOfAny(['e', 'E'], index);
        var coefficientEnd = exponentIndex >= 0 ? exponentIndex : raw.Length;
        var decimalPointIndex = raw.IndexOf('.', index, coefficientEnd - index);
        var fractionDigitCount = decimalPointIndex >= 0
            ? coefficientEnd - decimalPointIndex - 1
            : 0;
        var coefficient = new StringBuilder(coefficientEnd - index - (decimalPointIndex >= 0 ? 1 : 0));
        for (var coefficientIndex = index; coefficientIndex < coefficientEnd; coefficientIndex++)
        {
            if (raw[coefficientIndex] != '.')
            {
                coefficient.Append(raw[coefficientIndex]);
            }
        }

        var digits = coefficient.ToString();
        if (digits.All(static digit => digit == '0'))
        {
            return true;
        }

        long exponent = 0;
        if (exponentIndex >= 0)
        {
            var exponentCursor = exponentIndex + 1;
            var exponentNegative = raw[exponentCursor] == '-';
            if (raw[exponentCursor] is '+' or '-')
            {
                exponentCursor++;
            }

            var saturation = raw.Length + 16L;
            for (; exponentCursor < raw.Length; exponentCursor++)
            {
                var digit = raw[exponentCursor] - '0';
                exponent = exponent > (saturation - digit) / 10
                    ? saturation
                    : exponent * 10 + digit;
            }

            if (exponentNegative)
            {
                exponent = -exponent;
            }
        }

        var decimalShift = exponent - fractionDigitCount;
        var integerDigitEnd = digits.Length;
        var appendedZeroCount = 0;
        if (decimalShift < 0)
        {
            var removedDigitCount = -decimalShift;
            if (removedDigitCount > digits.Length)
            {
                return false;
            }

            integerDigitEnd -= (int)removedDigitCount;
            for (var removedIndex = integerDigitEnd; removedIndex < digits.Length; removedIndex++)
            {
                if (digits[removedIndex] != '0')
                {
                    return false;
                }
            }
        }
        else
        {
            if (decimalShift > 15)
            {
                return false;
            }

            appendedZeroCount = (int)decimalShift;
        }

        var firstSignificantDigit = 0;
        while (firstSignificantDigit < integerDigitEnd && digits[firstSignificantDigit] == '0')
        {
            firstSignificantDigit++;
        }

        var significantDigitCount = integerDigitEnd - firstSignificantDigit + appendedZeroCount;
        if (significantDigitCount > 15)
        {
            return false;
        }

        var maximumMagnitude = negative ? 62_135_596_800_000UL : 253_402_300_799_999UL;
        ulong magnitude = 0;
        for (var digitIndex = firstSignificantDigit; digitIndex < integerDigitEnd; digitIndex++)
        {
            var digit = (uint)(digits[digitIndex] - '0');
            if (magnitude > (maximumMagnitude - digit) / 10)
            {
                return false;
            }

            magnitude = magnitude * 10 + digit;
        }

        for (var zeroIndex = 0; zeroIndex < appendedZeroCount; zeroIndex++)
        {
            if (magnitude > maximumMagnitude / 10)
            {
                return false;
            }

            magnitude *= 10;
        }

        value = negative ? -(long)magnitude : (long)magnitude;
        return true;
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, JsonElement root, string outputName, string[] names)
    {
        if (TryGetString(root, names, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            writer.WriteString(outputName, value);
        }
        else
        {
            writer.WriteNull(outputName);
        }
    }

    private static bool TryGetString(JsonElement element, string[] names, out string value)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString()!;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool IsOptionalStringValid(JsonElement element, string[] names, int maximumLength)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            return property.ValueKind == JsonValueKind.String
                && (property.GetString()?.Length ?? 0) <= maximumLength;
        }

        return true;
    }

    private static string ComputeCanonicalHash(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(element, writer);
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(static item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void WriteSanitized(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (IsSensitiveProperty(property.Name))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteSanitized(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteSanitized(item, writer);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(SanitizeString(element.GetString()!));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitiveProperty(string name)
    {
        var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized.Contains("authorization", StringComparison.Ordinal)
            || normalized.Contains("credential", StringComparison.Ordinal)
            || normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("apikey", StringComparison.Ordinal)
            || normalized.Contains("accesstoken", StringComparison.Ordinal)
            || normalized.Contains("refreshtoken", StringComparison.Ordinal)
            || normalized.Contains("transcriptpath", StringComparison.Ordinal);
    }

    private static string SanitizeString(string value)
    {
        if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("github_pat_", StringComparison.Ordinal)
            || value.StartsWith("ghp_", StringComparison.Ordinal)
            || value.StartsWith("gho_", StringComparison.Ordinal)
            || value.StartsWith("ghu_", StringComparison.Ordinal)
            || value.StartsWith("ghs_", StringComparison.Ordinal)
            || value.StartsWith("ghr_", StringComparison.Ordinal)
            || value.StartsWith("sk-", StringComparison.Ordinal))
        {
            return "[REDACTED]";
        }

        var sanitized = Regex.Replace(value, @"(?i)Bearer\s+[^\s,;]+", "[REDACTED]", RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(sanitized, @"github_pat_[A-Za-z0-9_]+", "[REDACTED]", RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(sanitized, @"gh[pousr]_[A-Za-z0-9]+", "[REDACTED]", RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(sanitized, @"sk-[A-Za-z0-9_-]+", "[REDACTED]", RegexOptions.CultureInvariant);
        return Regex.Replace(
            sanitized,
            """(?i)(authorization|credential|password|passwd|secret|token|api[_-]?key|access[_-]?key|private[_-]?key)\s*[:=]\s*(?:"[^"]*"|'[^']*'|[^\s,;]+)""",
            "$1=[REDACTED]",
            RegexOptions.CultureInvariant);
    }

    private sealed record HookForwardOptions(
        Uri Endpoint,
        int TimeoutMilliseconds,
        string? Source,
        string? SourceVersion,
        string? SchemaFingerprint);
}
