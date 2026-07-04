namespace CopilotAgentObservability.ConfigCli;

internal sealed record RawEvidenceIndex(
    IReadOnlyDictionary<string, RawTraceEvidence> ByTraceId,
    IReadOnlyList<SensitiveBundleSourceInput> SourceInputs);

internal sealed record RawTraceEvidence(
    RawEvidenceMatch? ErrorMatch,
    RawEvidenceMatch? SensitiveMatch);

internal sealed record RawEvidenceMatch(
    string EvidenceRef,
    string SourceLocator,
    IReadOnlyList<RawEvidenceFragment> Fragments);

internal sealed record SensitiveBundleSourceInput(
    string Path,
    string Sha256,
    string Kind);

internal static partial class RawEvidenceReader
{
    public static RawEvidenceIndex Read(string rawInputPath)
    {
        var sourceInputs = new List<SensitiveBundleSourceInput>();
        var matches = new Dictionary<string, MutableRawTraceEvidence>(StringComparer.Ordinal);

        if (IsRawStorePath(rawInputPath))
        {
            foreach (var record in new RawTelemetryStore(rawInputPath).ListRecords())
            {
                using var document = JsonDocument.Parse(record.PayloadJson);
                AddRawOtlpEvidence(document.RootElement, rawInputPath, $"db-record={record.Id?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}", matches);
            }

            sourceInputs.Add(new SensitiveBundleSourceInput(rawInputPath, ComputeSha256(rawInputPath), "raw-store"));
        }
        else
        {
            using var document = JsonDocument.Parse(File.ReadAllText(rawInputPath, Encoding.UTF8));
            AddRawOtlpEvidence(document.RootElement, rawInputPath, null, matches);
            sourceInputs.Add(new SensitiveBundleSourceInput(rawInputPath, ComputeSha256(rawInputPath), "raw-otlp"));
        }

        return new RawEvidenceIndex(
            matches.ToDictionary(
                pair => pair.Key,
                pair => new RawTraceEvidence(pair.Value.ErrorMatch, pair.Value.SensitiveMatch),
                StringComparer.Ordinal),
            sourceInputs);
    }

    private static void AddRawOtlpEvidence(JsonElement root, string sourcePath, string? recordRef, Dictionary<string, MutableRawTraceEvidence> matches)
    {
        if (!root.TryGetProperty("resourceSpans", out var resourceSpans)
            || resourceSpans.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("raw OTLP JSON must contain a top-level resourceSpans array.");
        }

        var resourceSpanIndex = 0;
        foreach (var resourceSpan in resourceSpans.EnumerateArray())
        {
            var resourceAttributes = EnumerateAttributes(resourceSpan, $"resourceSpans[{resourceSpanIndex}].resource.attributes");
            var scopeSpanIndex = 0;
            foreach (var scopeSpan in EnumerateArrayProperty(resourceSpan, "scopeSpans"))
            {
                var spanIndex = 0;
                foreach (var span in EnumerateArrayProperty(scopeSpan, "spans"))
                {
                    var traceId = ReadString(span, "traceId");
                    if (string.IsNullOrWhiteSpace(traceId))
                    {
                        spanIndex++;
                        continue;
                    }

                    var spanId = ReadString(span, "spanId") ?? "unknown-span";
                    var traceEvidence = GetOrAdd(matches, traceId);
                    var spanLocator = recordRef is null
                        ? $"{sourcePath}:span={spanId}"
                        : $"{sourcePath}:{recordRef}:span={spanId}";

                    foreach (var attribute in resourceAttributes)
                    {
                        if (IsSensitive(attribute.Key, attribute.Value, out var contentKind))
                        {
                            AddSensitiveMatch(
                                traceEvidence,
                                traceId,
                                spanId,
                                spanLocator,
                                attribute.SourcePath,
                                contentKind,
                                attribute.Value);
                        }
                    }

                    ReadSpanEvidence(span, traceId, spanId, spanLocator, $"resourceSpans[{resourceSpanIndex}].scopeSpans[{scopeSpanIndex}].spans[{spanIndex}]", traceEvidence);
                    spanIndex++;
                }

                scopeSpanIndex++;
            }

            resourceSpanIndex++;
        }
    }

    private static void ReadSpanEvidence(JsonElement span, string traceId, string spanId, string spanLocator, string spanPath, MutableRawTraceEvidence traceEvidence)
    {
        var spanName = ReadString(span, "name");
        if (MatchesErrorText(spanName))
        {
            AddErrorMatch(traceEvidence, traceId, spanId, spanLocator, $"{spanPath}.name", "tool_results", spanName!);
        }

        if (span.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object)
        {
            var statusCode = ReadString(status, "code");
            var statusMessage = ReadString(status, "message");
            if (IsErrorStatusCode(statusCode))
            {
                AddErrorMatch(traceEvidence, traceId, spanId, spanLocator, $"{spanPath}.status.code", "tool_results", statusCode ?? string.Empty);
            }
            else if (MatchesErrorText(statusMessage))
            {
                AddErrorMatch(traceEvidence, traceId, spanId, spanLocator, $"{spanPath}.status.message", "tool_results", statusMessage!);
            }
        }

        var spanAttributes = span.TryGetProperty("attributes", out var attributes)
            ? EnumerateAttributeArray(attributes, $"{spanPath}.attributes")
            : [];
        foreach (var attribute in spanAttributes)
        {
            if (IsErrorAttribute(attribute.Key, attribute.Value))
            {
                AddErrorMatch(traceEvidence, traceId, spanId, spanLocator, attribute.SourcePath, "tool_results", attribute.Value);
            }
            else if (MatchesErrorText(attribute.Value))
            {
                AddErrorMatch(traceEvidence, traceId, spanId, spanLocator, attribute.SourcePath, GuessContentKind(attribute.Key), attribute.Value);
            }

            if (IsSensitive(attribute.Key, attribute.Value, out var contentKind))
            {
                AddSensitiveMatch(traceEvidence, traceId, spanId, spanLocator, attribute.SourcePath, contentKind, attribute.Value);
            }
        }

        var eventIndex = 0;
        foreach (var spanEvent in EnumerateArrayProperty(span, "events"))
        {
            var eventPath = $"{spanPath}.events[{eventIndex}]";
            var eventName = ReadString(spanEvent, "name");
            if (ContainsErrorEventName(eventName) || MatchesErrorText(eventName))
            {
                AddErrorMatch(traceEvidence, traceId, spanId, spanLocator, $"{eventPath}.name", "tool_results", eventName ?? string.Empty);
            }

            foreach (var attribute in EnumerateAttributes(spanEvent, $"{eventPath}.attributes"))
            {
                if (IsErrorEventAttribute(attribute.Key, attribute.Value))
                {
                    AddErrorMatch(traceEvidence, traceId, spanId, spanLocator, attribute.SourcePath, "tool_results", attribute.Value);
                }
                else if (MatchesErrorText(attribute.Value))
                {
                    AddErrorMatch(traceEvidence, traceId, spanId, spanLocator, attribute.SourcePath, GuessContentKind(attribute.Key), attribute.Value);
                }

                if (IsSensitive(attribute.Key, attribute.Value, out var contentKind))
                {
                    AddSensitiveMatch(traceEvidence, traceId, spanId, spanLocator, attribute.SourcePath, contentKind, attribute.Value);
                }
            }

            eventIndex++;
        }
    }

    private static void AddErrorMatch(
        MutableRawTraceEvidence traceEvidence,
        string traceId,
        string spanId,
        string sourceLocator,
        string sourcePath,
        string contentKind,
        string value)
    {
        traceEvidence.ErrorMatch = AppendMatch(traceEvidence.ErrorMatch, traceId, spanId, sourceLocator, sourcePath, contentKind, value);
    }

    private static void AddSensitiveMatch(
        MutableRawTraceEvidence traceEvidence,
        string traceId,
        string spanId,
        string sourceLocator,
        string sourcePath,
        string contentKind,
        string value)
    {
        traceEvidence.SensitiveMatch = AppendMatch(traceEvidence.SensitiveMatch, traceId, spanId, sourceLocator, sourcePath, contentKind, value);
    }

    private static RawEvidenceMatch AppendMatch(
        RawEvidenceMatch? existing,
        string traceId,
        string spanId,
        string sourceLocator,
        string sourcePath,
        string contentKind,
        string value)
    {
        var fragment = new RawEvidenceFragment(contentKind, sourceLocator, sourcePath, value);
        if (existing is not null)
        {
            return existing with { Fragments = existing.Fragments.Concat([fragment]).ToArray() };
        }

        var evidenceRef = $"raw:{traceId}:{spanId}:{sourcePath}";
        return new RawEvidenceMatch(evidenceRef, sourceLocator, [fragment]);
    }

    private static MutableRawTraceEvidence GetOrAdd(Dictionary<string, MutableRawTraceEvidence> matches, string traceId)
    {
        if (!matches.TryGetValue(traceId, out var evidence))
        {
            evidence = new MutableRawTraceEvidence();
            matches.Add(traceId, evidence);
        }

        return evidence;
    }

    private static IReadOnlyList<RawAttributeValue> EnumerateAttributes(JsonElement parent, string sourcePath)
    {
        if (!parent.TryGetProperty("attributes", out var attributes))
        {
            return [];
        }

        return EnumerateAttributeArray(attributes, sourcePath);
    }

    private static IReadOnlyList<RawAttributeValue> EnumerateAttributeArray(JsonElement attributes, string sourcePath)
    {
        if (attributes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<RawAttributeValue>();
        var index = 0;
        foreach (var attribute in attributes.EnumerateArray())
        {
            if (!attribute.TryGetProperty("key", out var keyElement)
                || keyElement.ValueKind != JsonValueKind.String
                || !attribute.TryGetProperty("value", out var valueElement))
            {
                index++;
                continue;
            }

            var key = keyElement.GetString();
            if (string.IsNullOrWhiteSpace(key))
            {
                index++;
                continue;
            }

            AddAttributeValues(result, key, valueElement, $"{sourcePath}[{index}].value");
            index++;
        }

        return result;
    }

    private static void AddAttributeValues(List<RawAttributeValue> result, string key, JsonElement valueElement, string sourcePath)
    {
        if (TryReadScalarAttributeValue(valueElement, out var scalarValue))
        {
            result.Add(new RawAttributeValue(key, scalarValue, sourcePath));
            return;
        }

        if (valueElement.TryGetProperty("kvlistValue", out var kvlistValue)
            && kvlistValue.TryGetProperty("values", out var values)
            && values.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in values.EnumerateArray())
            {
                if (item.TryGetProperty("key", out var keyElement)
                    && keyElement.ValueKind == JsonValueKind.String
                    && item.TryGetProperty("value", out var nestedValue))
                {
                    var nestedKey = keyElement.GetString();
                    if (!string.IsNullOrWhiteSpace(nestedKey))
                    {
                        AddAttributeValues(result, nestedKey, nestedValue, $"{sourcePath}.kvlistValue.values[{index}].value");
                    }
                }

                index++;
            }
        }

        if (valueElement.TryGetProperty("arrayValue", out var arrayValue)
            && arrayValue.TryGetProperty("values", out var arrayValues)
            && arrayValues.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in arrayValues.EnumerateArray())
            {
                AddAttributeValues(result, key, item, $"{sourcePath}.arrayValue.values[{index}]");
                index++;
            }
        }
    }

    private static bool TryReadScalarAttributeValue(JsonElement valueElement, out string value)
    {
        if (valueElement.TryGetProperty("stringValue", out var stringValue))
        {
            value = stringValue.GetString() ?? string.Empty;
            return true;
        }

        if (valueElement.TryGetProperty("intValue", out var intValue)
            || valueElement.TryGetProperty("doubleValue", out intValue)
            || valueElement.TryGetProperty("boolValue", out intValue)
            || valueElement.TryGetProperty("bytesValue", out intValue))
        {
            value = intValue.ValueKind == JsonValueKind.String ? intValue.GetString() ?? string.Empty : intValue.GetRawText();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool IsErrorStatusCode(string? value)
    {
        return string.Equals(value, "2", StringComparison.Ordinal)
            || string.Equals(value, "ERROR", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "STATUS_CODE_ERROR", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsErrorAttribute(string key, string value)
    {
        return (string.Equals(key, "level", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "status", StringComparison.OrdinalIgnoreCase))
            && string.Equals(value, "error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "error", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsErrorEventAttribute(string key, string value)
    {
        return string.Equals(key, "exception.type", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(value)
            || string.Equals(key, "error", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(value)
            || string.Equals(key, "level", StringComparison.OrdinalIgnoreCase)
            && string.Equals(value, "error", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsErrorEventName(string? value)
    {
        return value is not null
            && (value.Contains("exception", StringComparison.OrdinalIgnoreCase)
                || value.Contains("error", StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesErrorText(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && (ErrorTimeoutPattern().IsMatch(value)
                || ErrorPermissionPattern().IsMatch(value)
                || ErrorExceptionPattern().IsMatch(value)
                || ErrorFailedPattern().IsMatch(value));
    }

    private static bool IsSensitive(string key, string value, out string contentKind)
    {
        if (IsSensitiveKey(key, out contentKind))
        {
            return true;
        }

        if (EmailPattern().IsMatch(value))
        {
            contentKind = "identity";
            return true;
        }

        if (GithubTokenPattern().IsMatch(value)
            || AuthSchemePattern().IsMatch(value)
            || AuthHeaderPattern().IsMatch(value)
            || SensitiveAssignmentPattern().IsMatch(value))
        {
            contentKind = GuessContentKind(key);
            return true;
        }

        if (ContainsBase64Credential(value))
        {
            contentKind = "base64_header";
            return true;
        }

        return false;
    }

    private static bool IsSensitiveKey(string key, out string contentKind)
    {
        var normalized = key.ToLowerInvariant().Replace('_', '.');
        if (normalized.Contains("prompt.content", StringComparison.Ordinal))
        {
            contentKind = "prompt";
            return true;
        }

        if (normalized.Contains("response.content", StringComparison.Ordinal))
        {
            contentKind = "response";
            return true;
        }

        if (normalized.Contains("tool.arguments", StringComparison.Ordinal)
            || normalized.Contains("tool.input", StringComparison.Ordinal))
        {
            contentKind = "tool_arguments";
            return true;
        }

        if (normalized.Contains("tool.results", StringComparison.Ordinal)
            || normalized.Contains("tool.output", StringComparison.Ordinal))
        {
            contentKind = "tool_results";
            return true;
        }

        if (normalized == "email"
            || normalized.EndsWith(".email", StringComparison.Ordinal)
            || normalized.StartsWith("user.", StringComparison.Ordinal)
            || normalized.StartsWith("enduser.", StringComparison.Ordinal)
            || normalized.Contains("user.id", StringComparison.Ordinal)
            || normalized.Contains("user.email", StringComparison.Ordinal)
            || normalized.Contains("userid", StringComparison.Ordinal)
            || normalized.Contains("user.id", StringComparison.Ordinal)
            || normalized.Contains("username", StringComparison.Ordinal))
        {
            contentKind = "identity";
            return true;
        }

        if (normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("credential", StringComparison.Ordinal)
            || normalized.Contains("authorization", StringComparison.Ordinal)
            || normalized.Contains("api.key", StringComparison.Ordinal)
            || normalized.Contains("access.token", StringComparison.Ordinal)
            || normalized.Contains("refresh.token", StringComparison.Ordinal)
            || normalized.Contains("auth.token", StringComparison.Ordinal))
        {
            contentKind = normalized.Contains("secret", StringComparison.Ordinal) ? "secret" : "credential";
            return true;
        }

        if (normalized.Contains(".token", StringComparison.Ordinal)
            || normalized.StartsWith("token", StringComparison.Ordinal)
            || normalized.EndsWith(".token", StringComparison.Ordinal))
        {
            // Exclude known OTel GenAI token-usage keys (e.g. gen_ai.usage.input_tokens)
            // which after _ → . normalization contain ".token" but represent token counts,
            // not credential tokens.
            if (!normalized.EndsWith(".tokens", StringComparison.Ordinal)
                && !normalized.Contains(".tokens.", StringComparison.Ordinal))
            {
                contentKind = "credential";
                return true;
            }
        }

        contentKind = string.Empty;
        return false;
    }

    private static string GuessContentKind(string key)
    {
        var normalized = key.ToLowerInvariant();
        if (normalized.Contains("prompt", StringComparison.Ordinal))
        {
            return "prompt";
        }

        if (normalized.Contains("response", StringComparison.Ordinal))
        {
            return "response";
        }

        if (normalized.Contains("argument", StringComparison.Ordinal) || normalized.Contains("input", StringComparison.Ordinal))
        {
            return "tool_arguments";
        }

        if (normalized.Contains("result", StringComparison.Ordinal) || normalized.Contains("output", StringComparison.Ordinal))
        {
            return "tool_results";
        }

        if (normalized.Contains("user", StringComparison.Ordinal) || normalized.Contains("email", StringComparison.Ordinal))
        {
            return "identity";
        }

        if (normalized.Contains("authorization", StringComparison.Ordinal) || normalized.Contains("token", StringComparison.Ordinal))
        {
            return "credential";
        }

        return "tool_results";
    }

    private static bool ContainsBase64Credential(string value)
    {
        foreach (Match match in Base64CandidatePattern().Matches(value))
        {
            var candidate = match.Value;
            if (candidate.Length % 4 != 0)
            {
                continue;
            }

            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(candidate));
                if (decoded.Contains(':', StringComparison.Ordinal)
                    || decoded.Contains("secret", StringComparison.OrdinalIgnoreCase)
                    || decoded.Contains("key", StringComparison.OrdinalIgnoreCase)
                    || decoded.Contains("token", StringComparison.OrdinalIgnoreCase)
                    || decoded.Contains("password", StringComparison.OrdinalIgnoreCase)
                    || decoded.Contains("authorization", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (FormatException)
            {
                // Ignore non-Base64 candidates.
            }
        }

        return false;
    }

    private static bool IsRawStorePath(string inputPath)
    {
        var extension = Path.GetExtension(inputPath);
        if (extension is ".db" or ".sqlite" or ".sqlite3")
        {
            return true;
        }

        Span<byte> header = stackalloc byte[16];
        using var stream = File.OpenRead(inputPath);
        var bytesRead = stream.Read(header);
        return bytesRead >= 16 && Encoding.ASCII.GetString(header) == "SQLite format 3\0";
    }

    private static string ComputeSha256(string inputPath)
    {
        using var stream = File.OpenRead(inputPath);
        var hash = System.Security.Cryptography.SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();
    }

    private static IEnumerable<JsonElement> EnumerateArrayProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var arrayElement) || arrayElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in arrayElement.EnumerateArray())
        {
            yield return item;
        }
    }

    [GeneratedRegex("""(?i)\b(timeout|timed out|deadline exceeded)\b""")]
    private static partial Regex ErrorTimeoutPattern();

    [GeneratedRegex("""(?i)\b(permission denied|unauthorized|forbidden|access denied)\b""")]
    private static partial Regex ErrorPermissionPattern();

    [GeneratedRegex("""(?i)\b(exception|stack trace|traceback)\b""")]
    private static partial Regex ErrorExceptionPattern();

    [GeneratedRegex("""(?i)\b(failed|failure|exit code [1-9][0-9]*)\b""")]
    private static partial Regex ErrorFailedPattern();

    [GeneratedRegex("""(?i)\b[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}\b""")]
    private static partial Regex EmailPattern();

    [GeneratedRegex("""\b(ghp|github_pat)_[A-Za-z0-9_]{20,}\b""")]
    private static partial Regex GithubTokenPattern();

    [GeneratedRegex("""(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=\-]{16,}\b""")]
    private static partial Regex AuthSchemePattern();

    [GeneratedRegex("""(?i)\bauthorization\s*[:=]\s*(bearer|basic)\s+[A-Za-z0-9._~+/=\-]{16,}\b""")]
    private static partial Regex AuthHeaderPattern();

    [GeneratedRegex("""(?i)\b(api[_\-.]?key|access[_\-.]?token|refresh[_\-.]?token|secret|password|credential)\b\s*[:=]\s*["']?[^"',\s]{8,}""")]
    private static partial Regex SensitiveAssignmentPattern();

    [GeneratedRegex("""(?<![A-Za-z0-9+/=])[A-Za-z0-9+/]{16,}={0,2}(?![A-Za-z0-9+/=])""")]
    private static partial Regex Base64CandidatePattern();

    private sealed record RawAttributeValue(
        string Key,
        string Value,
        string SourcePath);

    private sealed class MutableRawTraceEvidence
    {
        public RawEvidenceMatch? ErrorMatch { get; set; }

        public RawEvidenceMatch? SensitiveMatch { get; set; }
    }
}
