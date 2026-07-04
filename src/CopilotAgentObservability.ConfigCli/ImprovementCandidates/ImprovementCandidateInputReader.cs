namespace CopilotAgentObservability.ConfigCli;

internal static class ImprovementCandidateInputReader
{
    public static IReadOnlyList<ImprovementCandidateRow> Read(string inputPath)
    {
        return string.Equals(Path.GetExtension(inputPath), ".csv", StringComparison.OrdinalIgnoreCase)
            ? ReadCsv(inputPath)
            : ReadJson(inputPath);
    }

    private static IReadOnlyList<ImprovementCandidateRow> ReadJson(string inputPath)
    {
        using var stream = File.OpenRead(inputPath);
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("improvement candidates JSON must be a top-level array.");
        }

        var rows = new List<ImprovementCandidateRow>();
        foreach (var rowElement in document.RootElement.EnumerateArray())
        {
            if (rowElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("each improvement candidate JSON item must be an object.");
            }

            RejectUnexpectedColumns(rowElement.EnumerateObject().Select(property => property.Name));
            rows.Add(CreateRow(rowElement));
        }

        return rows;
    }

    private static IReadOnlyList<ImprovementCandidateRow> ReadCsv(string inputPath)
    {
        var lines = File.ReadAllLines(inputPath);
        if (lines.Length == 0)
        {
            throw new InvalidDataException("improvement candidates CSV must contain a header row.");
        }

        var header = CsvLineParser.ParseLine(lines[0]);
        RejectUnexpectedColumns(header);
        if (!header.SequenceEqual(ImprovementCandidateOutputWriter.Columns, StringComparer.Ordinal))
        {
            throw new InvalidDataException("improvement candidates CSV header must exactly match the improvement candidate columns.");
        }

        var rows = new List<ImprovementCandidateRow>();
        for (var lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            if (string.IsNullOrWhiteSpace(lines[lineIndex]))
            {
                continue;
            }

            var values = CsvLineParser.ParseLine(lines[lineIndex]);
            if (values.Count != ImprovementCandidateOutputWriter.Columns.Length)
            {
                throw new InvalidDataException($"CSV row {lineIndex + 1} has {values.Count} column(s); expected {ImprovementCandidateOutputWriter.Columns.Length}.");
            }

            var row = ImprovementCandidateOutputWriter.Columns
                .Zip(values, (column, value) => new { column, value })
                .ToDictionary(pair => pair.column, pair => string.IsNullOrEmpty(pair.value) ? null : pair.value, StringComparer.Ordinal);
            rows.Add(CreateRow(row));
        }

        return rows;
    }

    private static ImprovementCandidateRow CreateRow(JsonElement rowElement)
    {
        return new ImprovementCandidateRow(
            ImprovementCandidateId: ReadRequiredString(rowElement, "improvement_candidate_id"),
            SourceDiagnosisCandidateId: ReadRequiredString(rowElement, "source_diagnosis_candidate_id"),
            TraceId: ReadString(rowElement, "trace_id"),
            FailureCategoryId: ReadRequiredString(rowElement, "failure_category_id"),
            AntiPatternId: ReadString(rowElement, "anti_pattern_id"),
            Severity: ReadRequiredString(rowElement, "severity"),
            ImprovementTarget: ReadRequiredString(rowElement, "improvement_target"),
            ProposalTitle: ReadRequiredString(rowElement, "proposal_title"),
            ProposalSummary: ReadRequiredString(rowElement, "proposal_summary"),
            ProposedChangeKind: ReadRequiredString(rowElement, "proposed_change_kind"),
            EvidenceRef: ReadRequiredString(rowElement, "evidence_ref"),
            SensitiveBundlePath: ReadString(rowElement, "sensitive_bundle_path"),
            CandidateStatus: ReadRequiredString(rowElement, "candidate_status"));
    }

    private static ImprovementCandidateRow CreateRow(IReadOnlyDictionary<string, string?> row)
    {
        return new ImprovementCandidateRow(
            ImprovementCandidateId: RequireCsvValue(row["improvement_candidate_id"], "improvement_candidate_id"),
            SourceDiagnosisCandidateId: RequireCsvValue(row["source_diagnosis_candidate_id"], "source_diagnosis_candidate_id"),
            TraceId: row["trace_id"],
            FailureCategoryId: RequireCsvValue(row["failure_category_id"], "failure_category_id"),
            AntiPatternId: row["anti_pattern_id"],
            Severity: RequireCsvValue(row["severity"], "severity"),
            ImprovementTarget: RequireCsvValue(row["improvement_target"], "improvement_target"),
            ProposalTitle: RequireCsvValue(row["proposal_title"], "proposal_title"),
            ProposalSummary: RequireCsvValue(row["proposal_summary"], "proposal_summary"),
            ProposedChangeKind: RequireCsvValue(row["proposed_change_kind"], "proposed_change_kind"),
            EvidenceRef: RequireCsvValue(row["evidence_ref"], "evidence_ref"),
            SensitiveBundlePath: row["sensitive_bundle_path"],
            CandidateStatus: RequireCsvValue(row["candidate_status"], "candidate_status"));
    }

    private static void RejectUnexpectedColumns(IEnumerable<string> columns)
    {
        foreach (var column in columns)
        {
            if (!ImprovementCandidateOutputWriter.Columns.Contains(column, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"unknown improvement candidate column '{column}'.");
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
            throw new InvalidDataException($"improvement candidate field '{propertyName}' must be a string or null.");
        }

        var value = property.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        return ReadString(element, propertyName)
            ?? throw new InvalidDataException($"improvement candidate field '{propertyName}' is required.");
    }

    private static string RequireCsvValue(string? value, string columnName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"improvement candidate field '{columnName}' is required.")
            : value;
    }
}
