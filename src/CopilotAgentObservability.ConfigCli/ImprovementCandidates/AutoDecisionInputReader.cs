namespace CopilotAgentObservability.ConfigCli;

internal static class AutoDecisionInputReader
{
    public static IReadOnlyList<AutoDecisionRow> Read(string inputPath)
    {
        return string.Equals(Path.GetExtension(inputPath), ".csv", StringComparison.OrdinalIgnoreCase)
            ? ReadCsv(inputPath)
            : ReadJson(inputPath);
    }

    private static IReadOnlyList<AutoDecisionRow> ReadJson(string inputPath)
    {
        using var stream = File.OpenRead(inputPath);
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("auto decisions JSON must be a top-level array.");
        }

        var rows = new List<AutoDecisionRow>();
        foreach (var rowElement in document.RootElement.EnumerateArray())
        {
            if (rowElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("each auto decision JSON item must be an object.");
            }

            RejectUnexpectedColumns(rowElement.EnumerateObject().Select(property => property.Name));
            rows.Add(CreateRow(rowElement));
        }

        return rows;
    }

    private static IReadOnlyList<AutoDecisionRow> ReadCsv(string inputPath)
    {
        var lines = File.ReadAllLines(inputPath);
        if (lines.Length == 0)
        {
            throw new InvalidDataException("auto decisions CSV must contain a header row.");
        }

        var header = CsvLineParser.ParseLine(lines[0]);
        RejectUnexpectedColumns(header);
        if (!header.SequenceEqual(AutoDecisionOutputWriter.Columns, StringComparer.Ordinal))
        {
            throw new InvalidDataException("auto decisions CSV header must exactly match the auto decision columns.");
        }

        var rows = new List<AutoDecisionRow>();
        for (var lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            if (string.IsNullOrWhiteSpace(lines[lineIndex]))
            {
                continue;
            }

            var values = CsvLineParser.ParseLine(lines[lineIndex]);
            if (values.Count != AutoDecisionOutputWriter.Columns.Length)
            {
                throw new InvalidDataException($"CSV row {lineIndex + 1} has {values.Count} column(s); expected {AutoDecisionOutputWriter.Columns.Length}.");
            }

            var row = AutoDecisionOutputWriter.Columns
                .Zip(values, (column, value) => new { column, value })
                .ToDictionary(pair => pair.column, pair => string.IsNullOrEmpty(pair.value) ? null : pair.value, StringComparer.Ordinal);
            rows.Add(CreateRow(row));
        }

        return rows;
    }

    private static AutoDecisionRow CreateRow(JsonElement rowElement)
    {
        return new AutoDecisionRow(
            AutoDecisionId: ReadRequiredString(rowElement, "auto_decision_id"),
            SourceImprovementCandidateId: ReadRequiredString(rowElement, "source_improvement_candidate_id"),
            SourceDiagnosisCandidateId: ReadRequiredString(rowElement, "source_diagnosis_candidate_id"),
            TraceId: ReadString(rowElement, "trace_id"),
            DecisionStatus: ReadRequiredString(rowElement, "decision_status"),
            DecisionRuleId: ReadRequiredString(rowElement, "decision_rule_id"),
            DecisionReason: ReadRequiredString(rowElement, "decision_reason"),
            Confidence: ReadRequiredString(rowElement, "confidence"),
            BlockingRiskChecks: ReadString(rowElement, "blocking_risk_checks") ?? string.Empty,
            SensitiveContentIncluded: ReadRequiredBoolean(rowElement, "sensitive_content_included"),
            SensitiveBundlePath: ReadString(rowElement, "sensitive_bundle_path"),
            ImplementationTarget: ReadRequiredString(rowElement, "implementation_target"),
            NextAction: ReadRequiredString(rowElement, "next_action"));
    }

    private static AutoDecisionRow CreateRow(IReadOnlyDictionary<string, string?> row)
    {
        return new AutoDecisionRow(
            AutoDecisionId: RequireCsvValue(row["auto_decision_id"], "auto_decision_id"),
            SourceImprovementCandidateId: RequireCsvValue(row["source_improvement_candidate_id"], "source_improvement_candidate_id"),
            SourceDiagnosisCandidateId: RequireCsvValue(row["source_diagnosis_candidate_id"], "source_diagnosis_candidate_id"),
            TraceId: row["trace_id"],
            DecisionStatus: RequireCsvValue(row["decision_status"], "decision_status"),
            DecisionRuleId: RequireCsvValue(row["decision_rule_id"], "decision_rule_id"),
            DecisionReason: RequireCsvValue(row["decision_reason"], "decision_reason"),
            Confidence: RequireCsvValue(row["confidence"], "confidence"),
            BlockingRiskChecks: row["blocking_risk_checks"] ?? string.Empty,
            SensitiveContentIncluded: ReadRequiredCsvBoolean(row["sensitive_content_included"], "sensitive_content_included"),
            SensitiveBundlePath: row["sensitive_bundle_path"],
            ImplementationTarget: RequireCsvValue(row["implementation_target"], "implementation_target"),
            NextAction: RequireCsvValue(row["next_action"], "next_action"));
    }

    private static void RejectUnexpectedColumns(IEnumerable<string> columns)
    {
        foreach (var column in columns)
        {
            if (!AutoDecisionOutputWriter.Columns.Contains(column, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"unknown auto decision column '{column}'.");
            }
        }
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
            throw new InvalidDataException($"auto decision field '{propertyName}' must be a string or null.");
        }

        var value = property.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        return ReadString(element, propertyName)
            ?? throw new InvalidDataException($"auto decision field '{propertyName}' is required.");
    }

    private static bool ReadRequiredBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidDataException($"auto decision field '{propertyName}' is required.");
        }

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return property.GetBoolean();
        }

        if (property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }

        throw new InvalidDataException($"auto decision field '{propertyName}' must be a boolean.");
    }

    private static string RequireCsvValue(string? value, string columnName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"auto decision field '{columnName}' is required.")
            : value;
    }

    private static bool ReadRequiredCsvBoolean(string? value, string columnName)
    {
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidDataException($"auto decision field '{columnName}' must be true or false.");
    }
}

