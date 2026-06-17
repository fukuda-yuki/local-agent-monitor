namespace CopilotAgentObservability.ConfigCli;

internal sealed record MeasurementInputRow(
    MeasurementRow Row,
    string SourceRecordRef);

internal static class DiagnosisCandidateMeasurementReader
{
    public static IReadOnlyList<MeasurementInputRow> Read(string inputPath)
    {
        return string.Equals(Path.GetExtension(inputPath), ".csv", StringComparison.OrdinalIgnoreCase)
            ? ReadCsv(inputPath)
            : ReadJson(inputPath);
    }

    private static IReadOnlyList<MeasurementInputRow> ReadJson(string inputPath)
    {
        using var stream = File.OpenRead(inputPath);
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("measurements JSON must be a top-level array.");
        }

        var rows = new List<MeasurementInputRow>();
        var rowNumber = 1;
        foreach (var rowElement in document.RootElement.EnumerateArray())
        {
            if (rowElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("each measurement JSON item must be an object.");
            }

            rows.Add(new MeasurementInputRow(CreateRow(rowElement), $"{inputPath}#row={rowNumber}"));
            rowNumber++;
        }

        return rows;
    }

    private static IReadOnlyList<MeasurementInputRow> ReadCsv(string inputPath)
    {
        var lines = File.ReadAllLines(inputPath);
        if (lines.Length == 0)
        {
            throw new InvalidDataException("measurements CSV must contain a header row.");
        }

        var header = CsvLineParser.ParseLine(lines[0]);
        if (!header.SequenceEqual(MeasurementOutputWriter.Columns, StringComparer.Ordinal))
        {
            throw new InvalidDataException("measurements CSV header must exactly match the measurement columns.");
        }

        var rows = new List<MeasurementInputRow>();
        for (var lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            if (string.IsNullOrWhiteSpace(lines[lineIndex]))
            {
                continue;
            }

            var values = CsvLineParser.ParseLine(lines[lineIndex]);
            if (values.Count != MeasurementOutputWriter.Columns.Length)
            {
                throw new InvalidDataException($"CSV row {lineIndex + 1} has {values.Count} column(s); expected {MeasurementOutputWriter.Columns.Length}.");
            }

            var row = MeasurementOutputWriter.Columns
                .Zip(values, (column, value) => new { column, value })
                .ToDictionary(pair => pair.column, pair => string.IsNullOrEmpty(pair.value) ? null : pair.value, StringComparer.Ordinal);
            rows.Add(new MeasurementInputRow(CreateRow(row), $"{inputPath}#row={lineIndex + 1}"));
        }

        return rows;
    }

    private static MeasurementRow CreateRow(JsonElement rowElement)
    {
        return new MeasurementRow(
            TraceId: ReadString(rowElement, "trace_id"),
            ExperimentId: ReadString(rowElement, "experiment_id"),
            ClientKind: ReadString(rowElement, "client_kind"),
            TaskId: ReadString(rowElement, "task_id"),
            TaskCategory: ReadString(rowElement, "task_category"),
            TaskRunIndex: ReadInt(rowElement, "task_run_index"),
            ExperimentCondition: ReadString(rowElement, "experiment_condition"),
            PromptVersion: ReadString(rowElement, "prompt_version"),
            AgentVariant: ReadString(rowElement, "agent_variant"),
            RepoSnapshot: ReadString(rowElement, "repo_snapshot"),
            InputTokens: ReadInt(rowElement, "input_tokens"),
            OutputTokens: ReadInt(rowElement, "output_tokens"),
            TotalTokens: ReadInt(rowElement, "total_tokens"),
            TurnCount: ReadInt(rowElement, "turn_count"),
            ToolCallCount: ReadInt(rowElement, "tool_call_count"),
            DurationMs: ReadInt(rowElement, "duration_ms"),
            ErrorCount: ReadInt(rowElement, "error_count"),
            SuccessStatus: ReadString(rowElement, "success_status") ?? "not-evaluated",
            EvaluatorId: ReadString(rowElement, "evaluator_id"),
            EvaluationNotes: ReadString(rowElement, "evaluation_notes"),
            EvaluatedAt: ReadString(rowElement, "evaluated_at"),
            UnknownSpansJson: ReadJsonArray(rowElement, "unknown_spans_json"),
            UnknownAttributesJson: ReadJsonObject(rowElement, "unknown_attributes_json"),
            AggregationNotes: ReadString(rowElement, "aggregation_notes"));
    }

    private static MeasurementRow CreateRow(IReadOnlyDictionary<string, string?> row)
    {
        return new MeasurementRow(
            TraceId: row["trace_id"],
            ExperimentId: row["experiment_id"],
            ClientKind: row["client_kind"],
            TaskId: row["task_id"],
            TaskCategory: row["task_category"],
            TaskRunIndex: ReadOptionalInt(row["task_run_index"], "task_run_index"),
            ExperimentCondition: row["experiment_condition"],
            PromptVersion: row["prompt_version"],
            AgentVariant: row["agent_variant"],
            RepoSnapshot: row["repo_snapshot"],
            InputTokens: ReadOptionalInt(row["input_tokens"], "input_tokens"),
            OutputTokens: ReadOptionalInt(row["output_tokens"], "output_tokens"),
            TotalTokens: ReadOptionalInt(row["total_tokens"], "total_tokens"),
            TurnCount: ReadOptionalInt(row["turn_count"], "turn_count"),
            ToolCallCount: ReadOptionalInt(row["tool_call_count"], "tool_call_count"),
            DurationMs: ReadOptionalInt(row["duration_ms"], "duration_ms"),
            ErrorCount: ReadOptionalInt(row["error_count"], "error_count"),
            SuccessStatus: row["success_status"] ?? "not-evaluated",
            EvaluatorId: row["evaluator_id"],
            EvaluationNotes: row["evaluation_notes"],
            EvaluatedAt: row["evaluated_at"],
            UnknownSpansJson: ReadOptionalJsonArray(row["unknown_spans_json"], "unknown_spans_json"),
            UnknownAttributesJson: ReadOptionalJsonObject(row["unknown_attributes_json"], "unknown_attributes_json"),
            AggregationNotes: row["aggregation_notes"]);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"measurement field '{propertyName}' must be a string or null.");
        }

        var value = property.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var numberValue))
        {
            return numberValue;
        }

        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stringValue))
        {
            return stringValue;
        }

        throw new InvalidDataException($"measurement field '{propertyName}' must be an integer or null.");
    }

    private static JsonArray? ReadJsonArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"measurement field '{propertyName}' must be an array or null.");
        }

        return JsonNode.Parse(property.GetRawText())!.AsArray();
    }

    private static JsonObject? ReadJsonObject(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"measurement field '{propertyName}' must be an object or null.");
        }

        return JsonNode.Parse(property.GetRawText())!.AsObject();
    }

    private static int? ReadOptionalInt(string? value, string columnName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidDataException($"measurement field '{columnName}' must be an integer or blank.");
        }

        return parsed;
    }

    private static JsonArray? ReadOptionalJsonArray(string? value, string columnName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var node = JsonNode.Parse(value);
        return node is JsonArray array
            ? array
            : throw new InvalidDataException($"measurement field '{columnName}' must be a JSON array or blank.");
    }

    private static JsonObject? ReadOptionalJsonObject(string? value, string columnName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var node = JsonNode.Parse(value);
        return node is JsonObject jsonObject
            ? jsonObject
            : throw new InvalidDataException($"measurement field '{columnName}' must be a JSON object or blank.");
    }
}
