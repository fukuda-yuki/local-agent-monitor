namespace CopilotAgentObservability.Telemetry;

internal static class MeasurementSanitizer
{
    private static readonly HashSet<string> MappedResourceAttributeKeys = new(StringComparer.Ordinal)
    {
        "experiment.id",
        "client.kind",
        "task.id",
        "task.category",
        "task.run_index",
        "experiment.condition",
        "prompt.version",
        "agent.variant",
        "repo.snapshot",
    };

    public static void AddUnknownResourceAttributes(JsonObject unknown, JsonElement resourceAttributes)
    {
        if (resourceAttributes.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var unknownResourceAttributes = new JsonObject();
        foreach (var property in resourceAttributes.EnumerateObject())
        {
            if (!MappedResourceAttributeKeys.Contains(property.Name) && !IsUnsafeKey(property.Name))
            {
                var value = JsonNode.Parse(property.Value.GetRawText());
                if (TrySanitizeNode(value, out var sanitizedValue))
                {
                    unknownResourceAttributes[property.Name] = sanitizedValue;
                }
            }
        }

        if (unknownResourceAttributes.Count > 0)
        {
            unknown["resourceAttributes"] = unknownResourceAttributes;
        }
    }

    public static void AddUnknownResourceAttributes(JsonObject unknown, JsonObject resourceAttributes)
    {
        var unknownResourceAttributes = new JsonObject();
        foreach (var property in resourceAttributes)
        {
            if (!MappedResourceAttributeKeys.Contains(property.Key) && !IsUnsafeKey(property.Key))
            {
                var value = property.Value is null
                    ? null
                    : JsonNode.Parse(property.Value.ToJsonString());
                if (TrySanitizeNode(value, out var sanitizedValue))
                {
                    unknownResourceAttributes[property.Key] = sanitizedValue;
                }
            }
        }

        if (unknownResourceAttributes.Count > 0)
        {
            unknown["resourceAttributes"] = unknownResourceAttributes;
        }
    }

    private static bool IsUnsafeKey(string key)
    {
        var normalizedKey = key.ToLowerInvariant();
        return normalizedKey.Contains("prompt", StringComparison.Ordinal)
            || normalizedKey.Contains("response", StringComparison.Ordinal)
            || normalizedKey.Contains("content", StringComparison.Ordinal)
            || normalizedKey.Contains("argument", StringComparison.Ordinal)
            || normalizedKey.Contains("result", StringComparison.Ordinal)
            || normalizedKey.Contains("secret", StringComparison.Ordinal)
            || normalizedKey.Contains("password", StringComparison.Ordinal)
            || normalizedKey.Contains("credential", StringComparison.Ordinal)
            || normalizedKey.Contains("authorization", StringComparison.Ordinal)
            || normalizedKey.Contains("api.key", StringComparison.Ordinal)
            || normalizedKey.Contains("token", StringComparison.Ordinal)
            || normalizedKey.Contains("userid", StringComparison.Ordinal)
            || normalizedKey.Contains("user_id", StringComparison.Ordinal)
            || normalizedKey.Contains("username", StringComparison.Ordinal)
            || normalizedKey.StartsWith("user.", StringComparison.Ordinal)
            || normalizedKey.StartsWith("enduser.", StringComparison.Ordinal)
            || normalizedKey.EndsWith(".email", StringComparison.Ordinal)
            || string.Equals(normalizedKey, "email", StringComparison.Ordinal);
    }

    public static bool IsUnsafeStringValue(string value)
    {
        return Regex.IsMatch(value, @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase)
            || value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Basic ", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Authorization:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("api_key", StringComparison.OrdinalIgnoreCase)
            || value.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || value.Contains("password", StringComparison.OrdinalIgnoreCase)
            || value.Contains("prompt:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("response:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("content:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("tool argument", StringComparison.OrdinalIgnoreCase)
            || value.Contains("tool result", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TrySanitizeNode(JsonNode? value, out JsonNode? sanitizedValue)
    {
        sanitizedValue = null;
        if (value is null)
        {
            return true;
        }

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<string>(out var stringValue)
                && IsUnsafeStringValue(stringValue))
            {
                return false;
            }

            sanitizedValue = JsonNode.Parse(value.ToJsonString());
            return true;
        }

        if (value is JsonObject jsonObject)
        {
            var sanitizedObject = new JsonObject();
            foreach (var property in jsonObject)
            {
                if (IsUnsafeKey(property.Key))
                {
                    continue;
                }

                if (TrySanitizeNode(property.Value, out var sanitizedChild))
                {
                    sanitizedObject[property.Key] = sanitizedChild;
                }
            }

            if (sanitizedObject.Count == 0)
            {
                return false;
            }

            sanitizedValue = sanitizedObject;
            return true;
        }

        if (value is JsonArray jsonArray)
        {
            var sanitizedArray = new JsonArray();
            foreach (var item in jsonArray)
            {
                if (TrySanitizeNode(item, out var sanitizedItem))
                {
                    sanitizedArray.Add(sanitizedItem);
                }
            }

            sanitizedValue = sanitizedArray;
            return true;
        }

        return false;
    }
}
