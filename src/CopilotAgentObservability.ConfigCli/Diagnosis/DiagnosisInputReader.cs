namespace CopilotAgentObservability.ConfigCli;

internal static class DiagnosisInputReader
{
    public static IReadOnlyList<DiagnosisRow> Read(string inputPath)
    {
        return string.Equals(Path.GetExtension(inputPath), ".csv", StringComparison.OrdinalIgnoreCase)
            ? ReadCsv(inputPath)
            : ReadJson(inputPath);
    }

    private static IReadOnlyList<DiagnosisRow> ReadJson(string inputPath)
    {
        using var stream = File.OpenRead(inputPath);
        using var document = JsonDocument.Parse(stream);

        JsonElement rowsElement;
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            rowsElement = document.RootElement;
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("diagnoses", out var diagnosesElement)
            && diagnosesElement.ValueKind == JsonValueKind.Array)
        {
            rowsElement = diagnosesElement;
        }
        else
        {
            throw new InvalidDataException("input JSON must be a top-level array or contain a top-level diagnoses array.");
        }

        var rows = new List<DiagnosisRow>();
        foreach (var rowElement in rowsElement.EnumerateArray())
        {
            if (rowElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("each diagnosis JSON item must be an object.");
            }

            RejectUnexpectedColumns(rowElement.EnumerateObject().Select(property => property.Name));
            rows.Add(CreateRow(rowElement));
        }

        return rows;
    }

    private static IReadOnlyList<DiagnosisRow> ReadCsv(string inputPath)
    {
        var lines = File.ReadAllLines(inputPath);
        if (lines.Length == 0)
        {
            throw new InvalidDataException("input CSV must contain a header row.");
        }

        var header = CsvLineParser.ParseLine(lines[0]);
        RejectUnexpectedColumns(header);
        if (!header.SequenceEqual(DiagnosisOutputWriter.Columns, StringComparer.Ordinal))
        {
            throw new InvalidDataException("input CSV header must exactly match the diagnosis record columns.");
        }

        var rows = new List<DiagnosisRow>();
        for (var lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            if (string.IsNullOrWhiteSpace(lines[lineIndex]))
            {
                continue;
            }

            var values = CsvLineParser.ParseLine(lines[lineIndex]);
            if (values.Count != DiagnosisOutputWriter.Columns.Length)
            {
                throw new InvalidDataException($"CSV row {lineIndex + 1} has {values.Count} column(s); expected {DiagnosisOutputWriter.Columns.Length}.");
            }

            var row = DiagnosisOutputWriter.Columns
                .Zip(values, (column, value) => new { column, value })
                .ToDictionary(pair => pair.column, pair => string.IsNullOrEmpty(pair.value) ? null : pair.value, StringComparer.Ordinal);
            rows.Add(CreateRow(row));
        }

        return rows;
    }

    private static DiagnosisRow CreateRow(JsonElement rowElement)
    {
        return new DiagnosisRow(
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
            EvidenceSummary: ReadString(rowElement, "evidence_summary"),
            RecommendedImprovementTarget: ReadString(rowElement, "recommended_improvement_target"),
            ReviewStatus: ReadString(rowElement, "review_status"));
    }

    private static DiagnosisRow CreateRow(IReadOnlyDictionary<string, string?> row)
    {
        return new DiagnosisRow(
            TraceId: row["trace_id"],
            TaskId: row["task_id"],
            TaskCategory: row["task_category"],
            ClientKind: row["client_kind"],
            ComparisonId: row["comparison_id"],
            ExperimentId: row["experiment_id"],
            ExperimentCondition: row["experiment_condition"],
            PromptVersion: row["prompt_version"],
            AgentVariant: row["agent_variant"],
            TaskRunIndex: ReadOptionalInt(row["task_run_index"], "task_run_index"),
            FailureCategoryId: row["failure_category_id"],
            AntiPatternId: row["anti_pattern_id"],
            Severity: row["severity"],
            EvidenceSummary: row["evidence_summary"],
            RecommendedImprovementTarget: row["recommended_improvement_target"],
            ReviewStatus: row["review_status"]);
    }

    private static void RejectUnexpectedColumns(IEnumerable<string> columns)
    {
        foreach (var column in columns)
        {
            if (string.Equals(column, "failure_type", StringComparison.Ordinal))
            {
                throw new InvalidDataException("failure_type is an M17 run exclusion field and must not be used as a diagnosis record column.");
            }

            if (!DiagnosisOutputWriter.Columns.Contains(column, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"unknown diagnosis record column '{column}'.");
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
            throw new InvalidDataException($"diagnosis field '{propertyName}' must be a string or null.");
        }

        var value = property.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
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
            throw new InvalidDataException($"diagnosis field '{propertyName}' must be an integer or null.");
        }

        return value;
    }

    private static int? ReadOptionalInt(string? value, string columnName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidDataException($"diagnosis field '{columnName}' must be an integer or blank.");
        }

        return parsed;
    }

}

internal static partial class DiagnosisValidator
{
    private static readonly HashSet<string> FailureCategoryIds = new(StringComparer.Ordinal)
    {
        "F-SPEC",
        "F-SCOPE",
        "F-DATA",
        "F-MEASURE",
        "F-TASK",
        "F-RUBRIC",
        "F-TRACE",
        "F-TOOL",
        "F-ERROR",
        "F-COMM",
        "F-COMPARISON",
    };

    private static readonly HashSet<string> AntiPatternIds = new(StringComparer.Ordinal)
    {
        "AP-SILENT-SPEC",
        "AP-OVERREACH",
        "AP-RAW-CONTENT",
        "AP-SCHEMA-DRIFT",
        "AP-RUBRIC-FLAT",
        "AP-TRACE-SKIP",
        "AP-TOOL-LOOP",
        "AP-ERROR-BLIND",
        "AP-UNCLEAR-SEVERITY",
        "AP-AUTO-DECIDE",
        "AP-CONFOUND",
        "AP-REF-CONTRACT-DRIFT",
        "AP-REF-OVER-ABSTRACTION",
        "AP-BUG-CAUSE-FIX-CONFLATION",
        "AP-BUG-MISSING-SYNTHETIC-REPRO",
        "AP-TEST-NONDETERMINISTIC",
        "AP-TEST-MISSING-EDGE-CLASS",
        "AP-REVIEW-MISSED-SEEDED-VIOLATION",
        "AP-REVIEW-PREFERENCE-OVER-SPEC",
    };

    private static readonly HashSet<string> Severities = new(StringComparer.Ordinal)
    {
        "blocking",
        "major",
        "minor",
    };

    private static readonly HashSet<string> ImprovementTargets = new(StringComparer.Ordinal)
    {
        "prompt",
        "instruction",
        "skill",
        "tool schema",
        "workflow",
        "eval",
    };

    private static readonly HashSet<string> ReviewStatuses = new(StringComparer.Ordinal)
    {
        "needs-human-review",
        "accepted-for-proposal",
        "rejected",
    };

    private static readonly HashSet<string> TaskCategories = new(StringComparer.Ordinal)
    {
        "refactoring",
        "bug-investigation",
        "test-generation",
        "code-review",
    };

    private static readonly HashSet<string> ClientKinds = new(StringComparer.Ordinal)
    {
        ConfigSamples.VsCodeClientKind,
        ConfigSamples.CopilotCliClientKind,
    };

    public static IReadOnlyList<string> Validate(IReadOnlyList<DiagnosisRow> rows)
    {
        var errors = new List<string>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var rowNumber = index + 1;

            Require(row.TraceId, "trace_id", rowNumber, errors);
            Require(row.FailureCategoryId, "failure_category_id", rowNumber, errors);
            Require(row.Severity, "severity", rowNumber, errors);
            Require(row.EvidenceSummary, "evidence_summary", rowNumber, errors);
            Require(row.RecommendedImprovementTarget, "recommended_improvement_target", rowNumber, errors);
            Require(row.ReviewStatus, "review_status", rowNumber, errors);

            ValidateAllowed(row.FailureCategoryId, FailureCategoryIds, "failure_category_id", rowNumber, errors);
            ValidateAllowed(row.AntiPatternId, AntiPatternIds, "anti_pattern_id", rowNumber, errors, allowBlank: true);
            ValidateAllowed(row.Severity, Severities, "severity", rowNumber, errors);
            ValidateAllowed(row.RecommendedImprovementTarget, ImprovementTargets, "recommended_improvement_target", rowNumber, errors);
            ValidateAllowed(row.ReviewStatus, ReviewStatuses, "review_status", rowNumber, errors);
            ValidateAllowed(row.TaskCategory, TaskCategories, "task_category", rowNumber, errors, allowBlank: true);
            ValidateAllowed(row.ClientKind, ClientKinds, "client_kind", rowNumber, errors, allowBlank: true);

            if (row.EvidenceSummary is { } evidenceSummary && ContainsUnsafeEvidence(evidenceSummary))
            {
                errors.Add($"row {rowNumber}: evidence_summary appears to contain raw content, credential, secret, token, or identity-bearing material.");
            }
        }

        return errors;
    }

    private static void Require(string? value, string column, int rowNumber, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"row {rowNumber}: {column} is required.");
        }
    }

    private static void ValidateAllowed(string? value, HashSet<string> allowedValues, string column, int rowNumber, List<string> errors, bool allowBlank = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (!allowBlank)
            {
                return;
            }

            return;
        }

        if (!allowedValues.Contains(value))
        {
            errors.Add($"row {rowNumber}: {column} '{value}' is not allowed.");
        }
    }

    private static bool ContainsUnsafeEvidence(string value)
    {
        return ContainsUnsafeMaterial(value);
    }

    public static bool ContainsUnsafeMaterial(string value)
    {
        var normalized = value.ToLowerInvariant();
        if (normalized.Contains('\r', StringComparison.Ordinal) || normalized.Contains('\n', StringComparison.Ordinal))
        {
            return true;
        }

        string[] unsafePatterns =
        [
            "raw prompt",
            "prompt content",
            "raw response",
            "response content",
            "captured content",
            "tool argument",
            "tool arguments",
            "tool.arguments",
            "tool result",
            "tool results",
            "tool.results",
            "credential",
            "secret",
            "password",
            "authorization",
            "api key",
            "api.key",
            "access token",
            "refresh token",
            "auth token",
            "bearer ",
            "ghp_",
            "github_pat_",
            "base64",
            "otel_exporter_otlp_headers",
            "x-langfuse",
            "user.email",
            "user.id",
            "email=",
        ];

        return unsafePatterns.Any(pattern => normalized.Contains(pattern, StringComparison.Ordinal))
            || BasicAuthPattern().IsMatch(value)
            || ContainsStandaloneBase64Credential(value)
            || EmailPattern().IsMatch(value);
    }

    private static bool ContainsStandaloneBase64Credential(string value)
    {
        foreach (Match match in Base64CandidatePattern().Matches(value))
        {
            var candidate = match.Value;
            if (candidate.Length % 4 != 0)
            {
                continue;
            }

            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(candidate));
                if (decoded.Contains(':', StringComparison.Ordinal)
                    || decoded.Contains("secret", StringComparison.OrdinalIgnoreCase)
                    || decoded.Contains("key", StringComparison.OrdinalIgnoreCase)
                    || decoded.Contains("token", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (FormatException)
            {
                // Ignore non-Base64 candidates.
            }
        }

        return false;
    }

    [GeneratedRegex("""(?i)\bbasic\s+[a-z0-9+/]+={0,2}\b""")]
    private static partial Regex BasicAuthPattern();

    [GeneratedRegex("""(?<![A-Za-z0-9+/=])[A-Za-z0-9+/]{16,}={0,2}(?![A-Za-z0-9+/=])""")]
    private static partial Regex Base64CandidatePattern();

    [GeneratedRegex("""(?i)\b[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}\b""")]
    private static partial Regex EmailPattern();
}
