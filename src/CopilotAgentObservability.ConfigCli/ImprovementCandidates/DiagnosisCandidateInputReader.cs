namespace CopilotAgentObservability.ConfigCli;

internal static class DiagnosisCandidateInputReader
{
    public static IReadOnlyList<DiagnosisCandidateRow> Read(string inputPath)
    {
        return string.Equals(Path.GetExtension(inputPath), ".csv", StringComparison.OrdinalIgnoreCase)
            ? ReadCsv(inputPath)
            : ReadJson(inputPath);
    }

    private static IReadOnlyList<DiagnosisCandidateRow> ReadJson(string inputPath)
    {
        using var stream = File.OpenRead(inputPath);
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("diagnosis candidates JSON must be a top-level array.");
        }

        var rows = new List<DiagnosisCandidateRow>();
        foreach (var rowElement in document.RootElement.EnumerateArray())
        {
            if (rowElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("each diagnosis candidate JSON item must be an object.");
            }

            RejectUnexpectedColumns(rowElement.EnumerateObject().Select(property => property.Name));
            rows.Add(CreateRow(rowElement));
        }

        return rows;
    }

    private static IReadOnlyList<DiagnosisCandidateRow> ReadCsv(string inputPath)
    {
        var lines = File.ReadAllLines(inputPath);
        if (lines.Length == 0)
        {
            throw new InvalidDataException("diagnosis candidates CSV must contain a header row.");
        }

        var header = CsvLineParser.ParseLine(lines[0]);
        RejectUnexpectedColumns(header);
        if (!header.SequenceEqual(DiagnosisCandidateOutputWriter.Columns, StringComparer.Ordinal))
        {
            throw new InvalidDataException("diagnosis candidates CSV header must exactly match the diagnosis candidate columns.");
        }

        var rows = new List<DiagnosisCandidateRow>();
        for (var lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            if (string.IsNullOrWhiteSpace(lines[lineIndex]))
            {
                continue;
            }

            var values = CsvLineParser.ParseLine(lines[lineIndex]);
            if (values.Count != DiagnosisCandidateOutputWriter.Columns.Length)
            {
                throw new InvalidDataException($"CSV row {lineIndex + 1} has {values.Count} column(s); expected {DiagnosisCandidateOutputWriter.Columns.Length}.");
            }

            var row = DiagnosisCandidateOutputWriter.Columns
                .Zip(values, (column, value) => new { column, value })
                .ToDictionary(pair => pair.column, pair => string.IsNullOrEmpty(pair.value) ? null : pair.value, StringComparer.Ordinal);
            rows.Add(CreateRow(row));
        }

        return rows;
    }

    private static DiagnosisCandidateRow CreateRow(JsonElement rowElement)
    {
        return new DiagnosisCandidateRow(
            DiagnosisCandidateId: ReadRequiredString(rowElement, "diagnosis_candidate_id"),
            TraceId: ReadString(rowElement, "trace_id"),
            SourceRecordRef: ReadRequiredString(rowElement, "source_record_ref"),
            RuleId: ReadRequiredString(rowElement, "rule_id"),
            FailureCategoryId: ReadRequiredString(rowElement, "failure_category_id"),
            AntiPatternId: ReadString(rowElement, "anti_pattern_id"),
            Severity: ReadRequiredString(rowElement, "severity"),
            RecommendedImprovementTarget: ReadRequiredString(rowElement, "recommended_improvement_target"),
            EvidenceSummary: ReadRequiredString(rowElement, "evidence_summary"),
            EvidenceRef: ReadRequiredString(rowElement, "evidence_ref"),
            ContentIncluded: ReadRequiredBoolean(rowElement, "content_included"),
            SensitiveBundlePath: ReadString(rowElement, "sensitive_bundle_path"),
            Confidence: ReadRequiredString(rowElement, "confidence"),
            RequiredHumanChecks: ReadRequiredString(rowElement, "required_human_checks"),
            CandidateStatus: ReadRequiredString(rowElement, "candidate_status"));
    }

    private static DiagnosisCandidateRow CreateRow(IReadOnlyDictionary<string, string?> row)
    {
        return new DiagnosisCandidateRow(
            DiagnosisCandidateId: RequireCsvValue(row["diagnosis_candidate_id"], "diagnosis_candidate_id"),
            TraceId: row["trace_id"],
            SourceRecordRef: RequireCsvValue(row["source_record_ref"], "source_record_ref"),
            RuleId: RequireCsvValue(row["rule_id"], "rule_id"),
            FailureCategoryId: RequireCsvValue(row["failure_category_id"], "failure_category_id"),
            AntiPatternId: row["anti_pattern_id"],
            Severity: RequireCsvValue(row["severity"], "severity"),
            RecommendedImprovementTarget: RequireCsvValue(row["recommended_improvement_target"], "recommended_improvement_target"),
            EvidenceSummary: RequireCsvValue(row["evidence_summary"], "evidence_summary"),
            EvidenceRef: RequireCsvValue(row["evidence_ref"], "evidence_ref"),
            ContentIncluded: ReadRequiredCsvBoolean(row["content_included"], "content_included"),
            SensitiveBundlePath: row["sensitive_bundle_path"],
            Confidence: RequireCsvValue(row["confidence"], "confidence"),
            RequiredHumanChecks: RequireCsvValue(row["required_human_checks"], "required_human_checks"),
            CandidateStatus: RequireCsvValue(row["candidate_status"], "candidate_status"));
    }

    private static void RejectUnexpectedColumns(IEnumerable<string> columns)
    {
        foreach (var column in columns)
        {
            if (!DiagnosisCandidateOutputWriter.Columns.Contains(column, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"unknown diagnosis candidate column '{column}'.");
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
            throw new InvalidDataException($"diagnosis candidate field '{propertyName}' must be a string or null.");
        }

        var value = property.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        return ReadString(element, propertyName)
            ?? throw new InvalidDataException($"diagnosis candidate field '{propertyName}' is required.");
    }

    private static bool ReadRequiredBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidDataException($"diagnosis candidate field '{propertyName}' is required.");
        }

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return property.GetBoolean();
        }

        if (property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }

        throw new InvalidDataException($"diagnosis candidate field '{propertyName}' must be a boolean.");
    }

    private static string RequireCsvValue(string? value, string columnName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"diagnosis candidate field '{columnName}' is required.")
            : value;
    }

    private static bool ReadRequiredCsvBoolean(string? value, string columnName)
    {
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidDataException($"diagnosis candidate field '{columnName}' must be true or false.");
    }
}
