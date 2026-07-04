namespace CopilotAgentObservability.ConfigCli;

internal static class HumanDecisionInputReader
{
    public static IReadOnlyList<HumanDecisionRow> Read(string inputPath)
    {
        return string.Equals(Path.GetExtension(inputPath), ".csv", StringComparison.OrdinalIgnoreCase)
            ? ReadCsv(inputPath)
            : ReadJson(inputPath);
    }

    private static IReadOnlyList<HumanDecisionRow> ReadJson(string inputPath)
    {
        using var stream = File.OpenRead(inputPath);
        using var document = JsonDocument.Parse(stream);

        JsonElement rowsElement;
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            rowsElement = document.RootElement;
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("decisions", out var decisionsElement)
            && decisionsElement.ValueKind == JsonValueKind.Array)
        {
            rowsElement = decisionsElement;
        }
        else
        {
            throw new InvalidDataException("input JSON must be a top-level array or contain a top-level decisions array.");
        }

        var rows = new List<HumanDecisionRow>();
        foreach (var rowElement in rowsElement.EnumerateArray())
        {
            if (rowElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("each decision JSON item must be an object.");
            }

            RejectUnexpectedColumns(rowElement.EnumerateObject().Select(property => property.Name));
            rows.Add(CreateRow(rowElement));
        }

        return rows;
    }

    private static IReadOnlyList<HumanDecisionRow> ReadCsv(string inputPath)
    {
        var lines = File.ReadAllLines(inputPath);
        if (lines.Length == 0)
        {
            throw new InvalidDataException("input CSV must contain a header row.");
        }

        var header = CsvLineParser.ParseLine(lines[0]);
        RejectUnexpectedColumns(header);
        if (!header.SequenceEqual(HumanDecisionOutputWriter.Columns, StringComparer.Ordinal))
        {
            throw new InvalidDataException("input CSV header must exactly match the human decision columns.");
        }

        var rows = new List<HumanDecisionRow>();
        for (var lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            if (string.IsNullOrWhiteSpace(lines[lineIndex]))
            {
                continue;
            }

            var values = CsvLineParser.ParseLine(lines[lineIndex]);
            if (values.Count != HumanDecisionOutputWriter.Columns.Length)
            {
                throw new InvalidDataException($"CSV row {lineIndex + 1} has {values.Count} column(s); expected {HumanDecisionOutputWriter.Columns.Length}.");
            }

            var row = HumanDecisionOutputWriter.Columns
                .Zip(values, (column, value) => new { column, value })
                .ToDictionary(pair => pair.column, pair => string.IsNullOrEmpty(pair.value) ? null : pair.value, StringComparer.Ordinal);
            rows.Add(CreateRow(row));
        }

        return rows;
    }

    private static HumanDecisionRow CreateRow(JsonElement rowElement)
    {
        return new HumanDecisionRow(
            ProposalId: ReadRequiredString(rowElement, "proposal_id"),
            HumanDecision: ReadRequiredString(rowElement, "human_decision"),
            DecisionRationale: ReadRequiredString(rowElement, "decision_rationale"),
            ApproverId: ReadString(rowElement, "approver_id"),
            ApprovedAt: ReadString(rowElement, "approved_at"),
            ConditionsOrNotes: ReadString(rowElement, "conditions_or_notes"));
    }

    private static HumanDecisionRow CreateRow(IReadOnlyDictionary<string, string?> row)
    {
        return new HumanDecisionRow(
            ProposalId: RequireCsvValue(row["proposal_id"], "proposal_id"),
            HumanDecision: RequireCsvValue(row["human_decision"], "human_decision"),
            DecisionRationale: RequireCsvValue(row["decision_rationale"], "decision_rationale"),
            ApproverId: row["approver_id"],
            ApprovedAt: row["approved_at"],
            ConditionsOrNotes: row["conditions_or_notes"]);
    }

    private static void RejectUnexpectedColumns(IEnumerable<string> columns)
    {
        foreach (var column in columns)
        {
            if (!HumanDecisionOutputWriter.Columns.Contains(column, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"unknown human decision column '{column}'.");
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
            throw new InvalidDataException($"decision field '{propertyName}' must be a string or null.");
        }

        var value = property.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        return ReadString(element, propertyName)
            ?? throw new InvalidDataException($"decision field '{propertyName}' is required.");
    }

    private static string RequireCsvValue(string? value, string columnName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"decision field '{columnName}' is required.")
            : value;
    }

}
