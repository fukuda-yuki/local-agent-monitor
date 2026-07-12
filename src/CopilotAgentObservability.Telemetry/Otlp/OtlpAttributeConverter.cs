namespace CopilotAgentObservability.Telemetry;

internal static class OtlpAttributeConverter
{
    public static JsonObject ConvertAttributesArray(JsonElement attributes)
    {
        var attributesObject = new JsonObject();
        foreach (var attribute in attributes.EnumerateArray())
        {
            if (attribute.ValueKind != JsonValueKind.Object
                || !attribute.TryGetProperty("key", out var keyElement)
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
        if (valueElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (valueElement.TryGetProperty("stringValue", out var stringValue))
        {
            return stringValue.ValueKind == JsonValueKind.String
                ? JsonValue.Create(stringValue.GetString())
                : null;
        }

        if (valueElement.TryGetProperty("intValue", out var intValue))
        {
            return intValue.ValueKind switch
            {
                JsonValueKind.String when long.TryParse(intValue.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => JsonValue.Create(parsed),
                JsonValueKind.Number when intValue.TryGetInt64(out var parsed) => JsonValue.Create(parsed),
                _ => null,
            };
        }

        if (valueElement.TryGetProperty("doubleValue", out var doubleValue))
        {
            return doubleValue.ValueKind switch
            {
                JsonValueKind.String when double.TryParse(doubleValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => JsonValue.Create(parsed),
                JsonValueKind.Number when doubleValue.TryGetDouble(out var parsed) => JsonValue.Create(parsed),
                _ => null,
            };
        }

        if (valueElement.TryGetProperty("boolValue", out var boolValue))
        {
            return boolValue.ValueKind == JsonValueKind.True || boolValue.ValueKind == JsonValueKind.False
                ? JsonValue.Create(boolValue.GetBoolean())
                : null;
        }

        if (valueElement.TryGetProperty("arrayValue", out var arrayValue))
        {
            return arrayValue.ValueKind == JsonValueKind.Object
                ? ConvertArrayValue(arrayValue)
                : null;
        }

        if (valueElement.TryGetProperty("kvlistValue", out var kvlistValue))
        {
            return kvlistValue.ValueKind == JsonValueKind.Object
                ? ConvertKeyValueList(kvlistValue)
                : null;
        }

        if (valueElement.TryGetProperty("bytesValue", out var bytesValue))
        {
            return bytesValue.ValueKind == JsonValueKind.String
                ? JsonValue.Create(bytesValue.GetString())
                : null;
        }

        return null;
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
