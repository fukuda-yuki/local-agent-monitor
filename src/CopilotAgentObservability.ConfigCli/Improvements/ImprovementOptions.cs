namespace CopilotAgentObservability.ConfigCli;

internal sealed record ImprovementProposalGenerationOptions(
    string InputPath,
    string? CsvOutputPath,
    string? JsonOutputPath)
{
    public static ImprovementProposalGenerationOptionsParseResult Parse(string[] args)
    {
        if (args.Length < 2)
        {
            return new ImprovementProposalGenerationOptionsParseResult(null, "generate-improvement-proposals requires an input CSV or JSON file path.");
        }

        var inputPath = args[1];
        string? csvOutputPath = null;
        string? jsonOutputPath = null;

        for (var index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--csv":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new ImprovementProposalGenerationOptionsParseResult(null, "--csv requires an output file path.");
                    }

                    csvOutputPath = args[++index];
                    break;

                case "--json":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new ImprovementProposalGenerationOptionsParseResult(null, "--json requires an output file path.");
                    }

                    jsonOutputPath = args[++index];
                    break;

                default:
                    return new ImprovementProposalGenerationOptionsParseResult(null, $"unknown generate-improvement-proposals option '{args[index]}'.");
            }
        }

        if (csvOutputPath is null && jsonOutputPath is null)
        {
            return new ImprovementProposalGenerationOptionsParseResult(null, "generate-improvement-proposals requires --csv, --json, or both.");
        }

        return new ImprovementProposalGenerationOptionsParseResult(
            new ImprovementProposalGenerationOptions(inputPath, csvOutputPath, jsonOutputPath),
            null);
    }

    private static bool IsOption(string value)
    {
        return value.StartsWith("--", StringComparison.Ordinal);
    }
}

internal sealed record ImprovementProposalGenerationOptionsParseResult(
    ImprovementProposalGenerationOptions? Options,
    string? Error);

internal sealed record ImprovementProposalEvaluationOptions(
    string InputPath,
    string? CsvOutputPath,
    string? JsonOutputPath)
{
    public static ImprovementProposalEvaluationOptionsParseResult Parse(string[] args)
    {
        if (args.Length < 2)
        {
            return new ImprovementProposalEvaluationOptionsParseResult(null, "evaluate-improvement-proposals requires an input CSV or JSON file path.");
        }

        var inputPath = args[1];
        string? csvOutputPath = null;
        string? jsonOutputPath = null;

        for (var index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--csv":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new ImprovementProposalEvaluationOptionsParseResult(null, "--csv requires an output file path.");
                    }

                    csvOutputPath = args[++index];
                    break;

                case "--json":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new ImprovementProposalEvaluationOptionsParseResult(null, "--json requires an output file path.");
                    }

                    jsonOutputPath = args[++index];
                    break;

                default:
                    return new ImprovementProposalEvaluationOptionsParseResult(null, $"unknown evaluate-improvement-proposals option '{args[index]}'.");
            }
        }

        if (csvOutputPath is null && jsonOutputPath is null)
        {
            return new ImprovementProposalEvaluationOptionsParseResult(null, "evaluate-improvement-proposals requires --csv, --json, or both.");
        }

        return new ImprovementProposalEvaluationOptionsParseResult(
            new ImprovementProposalEvaluationOptions(inputPath, csvOutputPath, jsonOutputPath),
            null);
    }

    private static bool IsOption(string value)
    {
        return value.StartsWith("--", StringComparison.Ordinal);
    }
}

internal sealed record ImprovementProposalEvaluationOptionsParseResult(
    ImprovementProposalEvaluationOptions? Options,
    string? Error);

internal sealed record HumanDecisionRecordingOptions(
    string EvaluationsPath,
    string DecisionsPath,
    string? CsvOutputPath,
    string? JsonOutputPath)
{
    public static HumanDecisionRecordingOptionsParseResult Parse(string[] args)
    {
        if (args.Length < 3)
        {
            return new HumanDecisionRecordingOptionsParseResult(null, "record-human-decisions requires an evaluations file and a decisions file.");
        }

        var evaluationsPath = args[1];
        var decisionsPath = args[2];
        string? csvOutputPath = null;
        string? jsonOutputPath = null;

        for (var index = 3; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--csv":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new HumanDecisionRecordingOptionsParseResult(null, "--csv requires an output file path.");
                    }

                    csvOutputPath = args[++index];
                    break;

                case "--json":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new HumanDecisionRecordingOptionsParseResult(null, "--json requires an output file path.");
                    }

                    jsonOutputPath = args[++index];
                    break;

                default:
                    return new HumanDecisionRecordingOptionsParseResult(null, $"unknown record-human-decisions option '{args[index]}'.");
            }
        }

        if (csvOutputPath is null && jsonOutputPath is null)
        {
            return new HumanDecisionRecordingOptionsParseResult(null, "record-human-decisions requires --csv, --json, or both.");
        }

        return new HumanDecisionRecordingOptionsParseResult(
            new HumanDecisionRecordingOptions(evaluationsPath, decisionsPath, csvOutputPath, jsonOutputPath),
            null);
    }

    private static bool IsOption(string value)
    {
        return value.StartsWith("--", StringComparison.Ordinal);
    }
}

internal sealed record HumanDecisionRecordingOptionsParseResult(
    HumanDecisionRecordingOptions? Options,
    string? Error);

internal sealed record DecisionTemplateGenerationOptions(
    string EvaluationsPath,
    string? CsvOutputPath,
    string? JsonOutputPath)
{
    public static DecisionTemplateGenerationOptionsParseResult Parse(string[] args)
    {
        if (args.Length < 2)
        {
            return new DecisionTemplateGenerationOptionsParseResult(null, "generate-decision-template requires an evaluations file path.");
        }

        var evaluationsPath = args[1];
        string? csvOutputPath = null;
        string? jsonOutputPath = null;

        for (var index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--csv":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new DecisionTemplateGenerationOptionsParseResult(null, "--csv requires an output file path.");
                    }

                    csvOutputPath = args[++index];
                    break;

                case "--json":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new DecisionTemplateGenerationOptionsParseResult(null, "--json requires an output file path.");
                    }

                    jsonOutputPath = args[++index];
                    break;

                default:
                    return new DecisionTemplateGenerationOptionsParseResult(null, $"unknown generate-decision-template option '{args[index]}'.");
            }
        }

        if (csvOutputPath is null && jsonOutputPath is null)
        {
            return new DecisionTemplateGenerationOptionsParseResult(null, "generate-decision-template requires --csv, --json, or both.");
        }

        return new DecisionTemplateGenerationOptionsParseResult(
            new DecisionTemplateGenerationOptions(evaluationsPath, csvOutputPath, jsonOutputPath),
            null);
    }

    private static bool IsOption(string value)
    {
        return value.StartsWith("--", StringComparison.Ordinal);
    }
}

internal sealed record DecisionTemplateGenerationOptionsParseResult(
    DecisionTemplateGenerationOptions? Options,
    string? Error);
