namespace CopilotAgentObservability.ConfigCli;

internal static class ImprovementProposalInputReader
{
    public static IReadOnlyList<ImprovementProposalRow> Read(string inputPath)
    {
        return string.Equals(Path.GetExtension(inputPath), ".csv", StringComparison.OrdinalIgnoreCase)
            ? ReadCsv(inputPath)
            : ReadJson(inputPath);
    }

    private static IReadOnlyList<ImprovementProposalRow> ReadJson(string inputPath)
    {
        using var stream = File.OpenRead(inputPath);
        using var document = JsonDocument.Parse(stream);

        JsonElement rowsElement;
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            rowsElement = document.RootElement;
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("proposals", out var proposalsElement)
            && proposalsElement.ValueKind == JsonValueKind.Array)
        {
            rowsElement = proposalsElement;
        }
        else
        {
            throw new InvalidDataException("input JSON must be a top-level array or contain a top-level proposals array.");
        }

        var rows = new List<ImprovementProposalRow>();
        foreach (var rowElement in rowsElement.EnumerateArray())
        {
            if (rowElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("each proposal JSON item must be an object.");
            }

            RejectUnexpectedColumns(rowElement.EnumerateObject().Select(property => property.Name));
            rows.Add(CreateRow(rowElement));
        }

        return rows;
    }

    private static IReadOnlyList<ImprovementProposalRow> ReadCsv(string inputPath)
    {
        var lines = File.ReadAllLines(inputPath);
        if (lines.Length == 0)
        {
            throw new InvalidDataException("input CSV must contain a header row.");
        }

        var header = CsvLineParser.ParseLine(lines[0]);
        RejectUnexpectedColumns(header);
        if (!header.SequenceEqual(ImprovementProposalOutputWriter.Columns, StringComparer.Ordinal))
        {
            throw new InvalidDataException("input CSV header must exactly match the improvement proposal columns.");
        }

        var rows = new List<ImprovementProposalRow>();
        for (var lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            if (string.IsNullOrWhiteSpace(lines[lineIndex]))
            {
                continue;
            }

            var values = CsvLineParser.ParseLine(lines[lineIndex]);
            if (values.Count != ImprovementProposalOutputWriter.Columns.Length)
            {
                throw new InvalidDataException($"CSV row {lineIndex + 1} has {values.Count} column(s); expected {ImprovementProposalOutputWriter.Columns.Length}.");
            }

            var row = ImprovementProposalOutputWriter.Columns
                .Zip(values, (column, value) => new { column, value })
                .ToDictionary(pair => pair.column, pair => string.IsNullOrEmpty(pair.value) ? null : pair.value, StringComparer.Ordinal);
            rows.Add(CreateRow(row));
        }

        return rows;
    }

    private static ImprovementProposalRow CreateRow(JsonElement rowElement)
    {
        return new ImprovementProposalRow(
            ProposalId: ReadRequiredString(rowElement, "proposal_id"),
            SourceDiagnosisIndex: ReadRequiredInt(rowElement, "source_diagnosis_index"),
            TraceId: ReadString(rowElement, "trace_id"),
            TaskId: ReadString(rowElement, "task_id"),
            TaskCategory: ReadString(rowElement, "task_category"),
            ClientKind: ReadString(rowElement, "client_kind"),
            ComparisonId: ReadString(rowElement, "comparison_id"),
            ExperimentId: ReadString(rowElement, "experiment_id"),
            ExperimentCondition: ReadString(rowElement, "experiment_condition"),
            PromptVersion: ReadString(rowElement, "prompt_version"),
            AgentVariant: ReadString(rowElement, "agent_variant"),
            TaskRunIndex: ReadInt(rowElement, "task_run_index"),
            FailureCategoryId: ReadString(rowElement, "failure_category_id"),
            AntiPatternId: ReadString(rowElement, "anti_pattern_id"),
            Severity: ReadString(rowElement, "severity"),
            ImprovementTarget: ReadString(rowElement, "improvement_target"),
            EvidenceSummary: ReadString(rowElement, "evidence_summary"),
            ProposalTitle: ReadRequiredString(rowElement, "proposal_title"),
            ProposalSummary: ReadRequiredString(rowElement, "proposal_summary"),
            ProposedChange: ReadRequiredString(rowElement, "proposed_change"),
            AcceptanceCheck: ReadRequiredString(rowElement, "acceptance_check"),
            HumanReviewStatus: ReadRequiredString(rowElement, "human_review_status"));
    }

    private static ImprovementProposalRow CreateRow(IReadOnlyDictionary<string, string?> row)
    {
        return new ImprovementProposalRow(
            ProposalId: RequireCsvValue(row["proposal_id"], "proposal_id"),
            SourceDiagnosisIndex: ReadRequiredCsvInt(row["source_diagnosis_index"], "source_diagnosis_index"),
            TraceId: row["trace_id"],
            TaskId: row["task_id"],
            TaskCategory: row["task_category"],
            ClientKind: row["client_kind"],
            ComparisonId: row["comparison_id"],
            ExperimentId: row["experiment_id"],
            ExperimentCondition: row["experiment_condition"],
            PromptVersion: row["prompt_version"],
            AgentVariant: row["agent_variant"],
            TaskRunIndex: ReadOptionalCsvInt(row["task_run_index"], "task_run_index"),
            FailureCategoryId: row["failure_category_id"],
            AntiPatternId: row["anti_pattern_id"],
            Severity: row["severity"],
            ImprovementTarget: row["improvement_target"],
            EvidenceSummary: row["evidence_summary"],
            ProposalTitle: RequireCsvValue(row["proposal_title"], "proposal_title"),
            ProposalSummary: RequireCsvValue(row["proposal_summary"], "proposal_summary"),
            ProposedChange: RequireCsvValue(row["proposed_change"], "proposed_change"),
            AcceptanceCheck: RequireCsvValue(row["acceptance_check"], "acceptance_check"),
            HumanReviewStatus: RequireCsvValue(row["human_review_status"], "human_review_status"));
    }

    private static void RejectUnexpectedColumns(IEnumerable<string> columns)
    {
        foreach (var column in columns)
        {
            if (!ImprovementProposalOutputWriter.Columns.Contains(column, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"unknown improvement proposal column '{column}'.");
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
            throw new InvalidDataException($"proposal field '{propertyName}' must be a string or null.");
        }

        var value = property.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        return ReadString(element, propertyName)
            ?? throw new InvalidDataException($"proposal field '{propertyName}' is required.");
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        int? value = null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var numberValue))
        {
            value = numberValue;
        }
        else if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stringValue))
        {
            value = stringValue;
        }

        if (!value.HasValue)
        {
            throw new InvalidDataException($"proposal field '{propertyName}' must be an integer or null.");
        }

        return value;
    }

    private static int ReadRequiredInt(JsonElement element, string propertyName)
    {
        return ReadInt(element, propertyName)
            ?? throw new InvalidDataException($"proposal field '{propertyName}' is required.");
    }

    private static string RequireCsvValue(string? value, string columnName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"proposal field '{columnName}' is required.")
            : value;
    }

    private static int ReadRequiredCsvInt(string? value, string columnName)
    {
        return ReadOptionalCsvInt(value, columnName)
            ?? throw new InvalidDataException($"proposal field '{columnName}' is required.");
    }

    private static int? ReadOptionalCsvInt(string? value, string columnName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidDataException($"proposal field '{columnName}' must be an integer or blank.");
        }

        return parsed;
    }

}
