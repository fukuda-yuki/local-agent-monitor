namespace CopilotAgentObservability.Telemetry;

internal static class OtlpAttributeConverter
{
    public static JsonObject ConvertAttributesArray(JsonElement attributes)
    {
        var attributesObject = new JsonObject();
        foreach (var attribute in attributes.EnumerateArray())
        {
            if (!attribute.TryGetProperty("key", out var keyElement)
                || keyElement.ValueKind != JsonValueKind.String
                || !attribute.TryGetProperty("value", out var valueElement))
            {
                continue;
            }

            var key = keyElement.GetString();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            attributesObject[key] = ConvertAttributeValue(valueElement);
        }

        return attributesObject;
    }

    private static JsonNode? ConvertAttributeValue(JsonElement valueElement)
    {
        if (valueElement.TryGetProperty("stringValue", out var stringValue))
        {
            return JsonValue.Create(stringValue.GetString());
        }

        if (valueElement.TryGetProperty("intValue", out var intValue))
        {
            return intValue.ValueKind switch
            {
                JsonValueKind.String when long.TryParse(intValue.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => JsonValue.Create(parsed),
                JsonValueKind.Number when intValue.TryGetInt64(out var parsed) => JsonValue.Create(parsed),
                _ => JsonNode.Parse(intValue.GetRawText()),
            };
        }

        if (valueElement.TryGetProperty("doubleValue", out var doubleValue))
        {
            return doubleValue.ValueKind switch
            {
                JsonValueKind.String when double.TryParse(doubleValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => JsonValue.Create(parsed),
                JsonValueKind.Number when doubleValue.TryGetDouble(out var parsed) => JsonValue.Create(parsed),
                _ => JsonNode.Parse(doubleValue.GetRawText()),
            };
        }

        if (valueElement.TryGetProperty("boolValue", out var boolValue))
        {
            return boolValue.ValueKind == JsonValueKind.True || boolValue.ValueKind == JsonValueKind.False
                ? JsonValue.Create(boolValue.GetBoolean())
                : JsonNode.Parse(boolValue.GetRawText());
        }

        if (valueElement.TryGetProperty("arrayValue", out var arrayValue))
        {
            return ConvertArrayValue(arrayValue);
        }

        if (valueElement.TryGetProperty("kvlistValue", out var kvlistValue))
        {
            return ConvertKeyValueList(kvlistValue);
        }

        if (valueElement.TryGetProperty("bytesValue", out var bytesValue))
        {
            return bytesValue.ValueKind == JsonValueKind.String
                ? JsonValue.Create(bytesValue.GetString())
                : JsonNode.Parse(bytesValue.GetRawText());
        }

        return JsonNode.Parse(valueElement.GetRawText());
    }

    private static JsonArray ConvertArrayValue(JsonElement arrayValue)
    {
        var array = new JsonArray();
        if (!arrayValue.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return array;
        }

        foreach (var value in values.EnumerateArray())
        {
            array.Add(ConvertAttributeValue(value));
        }

        return array;
    }

    private static JsonObject ConvertKeyValueList(JsonElement kvlistValue)
    {
        var result = new JsonObject();
        if (!kvlistValue.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in values.EnumerateArray())
        {
            if (!item.TryGetProperty("key", out var keyElement)
                || keyElement.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("value", out var valueElement))
            {
                continue;
            }

            var key = keyElement.GetString();
            if (!string.IsNullOrWhiteSpace(key))
            {
                result[key] = ConvertAttributeValue(valueElement);
            }
        }

        return result;
    }
}
