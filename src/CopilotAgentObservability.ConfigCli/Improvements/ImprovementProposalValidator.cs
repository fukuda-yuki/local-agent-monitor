namespace CopilotAgentObservability.ConfigCli;

internal static class ImprovementProposalValidator
{
    private const string NeedsHumanReview = "needs-human-review";

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

    public static IReadOnlyList<string> Validate(IReadOnlyList<ImprovementProposalRow> rows)
    {
        var errors = new List<string>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var rowNumber = index + 1;

            Require(row.ProposalId, "proposal_id", rowNumber, errors);
            Require(row.TraceId, "trace_id", rowNumber, errors);
            Require(row.FailureCategoryId, "failure_category_id", rowNumber, errors);
            Require(row.Severity, "severity", rowNumber, errors);
            Require(row.ImprovementTarget, "improvement_target", rowNumber, errors);
            Require(row.EvidenceSummary, "evidence_summary", rowNumber, errors);
            Require(row.ProposalTitle, "proposal_title", rowNumber, errors);
            Require(row.ProposalSummary, "proposal_summary", rowNumber, errors);
            Require(row.ProposedChange, "proposed_change", rowNumber, errors);
            Require(row.AcceptanceCheck, "acceptance_check", rowNumber, errors);
            Require(row.HumanReviewStatus, "human_review_status", rowNumber, errors);

            if (row.SourceDiagnosisIndex < 1)
            {
                errors.Add($"row {rowNumber}: source_diagnosis_index must be greater than or equal to 1.");
            }

            ValidateAllowed(row.Severity, Severities, "severity", rowNumber, errors);
            ValidateAllowed(row.ImprovementTarget, ImprovementTargets, "improvement_target", rowNumber, errors);
            ValidateAllowed(row.FailureCategoryId, FailureCategoryIds, "failure_category_id", rowNumber, errors);
            ValidateAllowed(row.AntiPatternId, AntiPatternIds, "anti_pattern_id", rowNumber, errors, allowBlank: true);
            ValidateAllowed(row.TaskCategory, TaskCategories, "task_category", rowNumber, errors, allowBlank: true);
            ValidateAllowed(row.ClientKind, ClientKinds, "client_kind", rowNumber, errors, allowBlank: true);

            if (!string.IsNullOrWhiteSpace(row.HumanReviewStatus)
                && !string.Equals(row.HumanReviewStatus, NeedsHumanReview, StringComparison.Ordinal))
            {
                errors.Add($"row {rowNumber}: human_review_status must be '{NeedsHumanReview}'.");
            }
        }

        errors.AddRange(ImprovementProposalSafetyValidator.Validate(rows));
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
            return;
        }

        if (!allowedValues.Contains(value))
        {
            errors.Add($"row {rowNumber}: {column} '{value}' is not allowed.");
        }
    }
}
