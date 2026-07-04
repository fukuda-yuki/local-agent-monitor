namespace CopilotAgentObservability.ConfigCli;

internal static class ProposalEvaluationInputReader
{
    public static IReadOnlyList<ProposalEvaluationRow> Read(string inputPath)
    {
        return string.Equals(Path.GetExtension(inputPath), ".csv", StringComparison.OrdinalIgnoreCase)
            ? ReadCsv(inputPath)
            : ReadJson(inputPath);
    }

    private static IReadOnlyList<ProposalEvaluationRow> ReadJson(string inputPath)
    {
        using var stream = File.OpenRead(inputPath);
        using var document = JsonDocument.Parse(stream);

        JsonElement rowsElement;
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            rowsElement = document.RootElement;
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("evaluations", out var evaluationsElement)
            && evaluationsElement.ValueKind == JsonValueKind.Array)
        {
            rowsElement = evaluationsElement;
        }
        else
        {
            throw new InvalidDataException("input JSON must be a top-level array or contain a top-level evaluations array.");
        }

        var rows = new List<ProposalEvaluationRow>();
        foreach (var rowElement in rowsElement.EnumerateArray())
        {
            if (rowElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("each evaluation JSON item must be an object.");
            }

            rows.Add(CreateRow(rowElement));
        }

        return rows;
    }

    private static IReadOnlyList<ProposalEvaluationRow> ReadCsv(string inputPath)
    {
        var lines = File.ReadAllLines(inputPath);
        if (lines.Length == 0)
        {
            throw new InvalidDataException("input CSV must contain a header row.");
        }

        var header = CsvLineParser.ParseLine(lines[0]);
        if (!header.SequenceEqual(ProposalEvaluationOutputWriter.Columns, StringComparer.Ordinal))
        {
            throw new InvalidDataException("input CSV header must exactly match the proposal evaluation columns.");
        }

        var rows = new List<ProposalEvaluationRow>();
        for (var lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            if (string.IsNullOrWhiteSpace(lines[lineIndex]))
            {
                continue;
            }

            var values = CsvLineParser.ParseLine(lines[lineIndex]);
            if (values.Count != ProposalEvaluationOutputWriter.Columns.Length)
            {
                throw new InvalidDataException($"CSV row {lineIndex + 1} has {values.Count} column(s); expected {ProposalEvaluationOutputWriter.Columns.Length}.");
            }

            var row = ProposalEvaluationOutputWriter.Columns
                .Zip(values, (column, value) => new { column, value })
                .ToDictionary(pair => pair.column, pair => string.IsNullOrEmpty(pair.value) ? null : pair.value, StringComparer.Ordinal);
            rows.Add(CreateRow(row));
        }

        return rows;
    }

    private static ProposalEvaluationRow CreateRow(JsonElement rowElement)
    {
        return new ProposalEvaluationRow(
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
            ProposalTitle: ReadRequiredString(rowElement, "proposal_title"),
            ProposalEvaluationStatus: ReadRequiredString(rowElement, "proposal_evaluation_status"),
            EvaluatorFindings: ReadRequiredString(rowElement, "evaluator_findings"),
            RequiredHumanChecks: ReadRequiredString(rowElement, "required_human_checks"),
            EvaluatorNotes: ReadRequiredString(rowElement, "evaluator_notes"));
    }

    private static ProposalEvaluationRow CreateRow(IReadOnlyDictionary<string, string?> row)
    {
        return new ProposalEvaluationRow(
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
            ProposalTitle: RequireCsvValue(row["proposal_title"], "proposal_title"),
            ProposalEvaluationStatus: RequireCsvValue(row["proposal_evaluation_status"], "proposal_evaluation_status"),
            EvaluatorFindings: RequireCsvValue(row["evaluator_findings"], "evaluator_findings"),
            RequiredHumanChecks: RequireCsvValue(row["required_human_checks"], "required_human_checks"),
            EvaluatorNotes: RequireCsvValue(row["evaluator_notes"], "evaluator_notes"));
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
            throw new InvalidDataException($"evaluation field '{propertyName}' must be a string or null.");
        }

        var value = property.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        return ReadString(element, propertyName)
            ?? throw new InvalidDataException($"evaluation field '{propertyName}' is required.");
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
            throw new InvalidDataException($"evaluation field '{propertyName}' must be an integer or null.");
        }

        return value;
    }

    private static int ReadRequiredInt(JsonElement element, string propertyName)
    {
        return ReadInt(element, propertyName)
            ?? throw new InvalidDataException($"evaluation field '{propertyName}' is required.");
    }

    private static string RequireCsvValue(string? value, string columnName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"evaluation field '{columnName}' is required.")
            : value;
    }

    private static int ReadRequiredCsvInt(string? value, string columnName)
    {
        return ReadOptionalCsvInt(value, columnName)
            ?? throw new InvalidDataException($"evaluation field '{columnName}' is required.");
    }

    private static int? ReadOptionalCsvInt(string? value, string columnName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidDataException($"evaluation field '{columnName}' must be an integer or blank.");
        }

        return parsed;
    }

}
