namespace CopilotAgentObservability.ConfigCli;

internal sealed record ImprovementCandidateGenerationOptions(
    string InputPath,
    string? CsvOutputPath,
    string? JsonOutputPath)
{
    public static ImprovementCandidateGenerationOptionsParseResult Parse(string[] args)
    {
        if (args.Length < 2)
        {
            return new ImprovementCandidateGenerationOptionsParseResult(null, "generate-improvement-candidates requires an input CSV or JSON file path.");
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
                        return new ImprovementCandidateGenerationOptionsParseResult(null, "--csv requires an output file path.");
                    }

                    csvOutputPath = args[++index];
                    break;

                case "--json":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new ImprovementCandidateGenerationOptionsParseResult(null, "--json requires an output file path.");
                    }

                    jsonOutputPath = args[++index];
                    break;

                default:
                    return new ImprovementCandidateGenerationOptionsParseResult(null, $"unknown generate-improvement-candidates option '{args[index]}'.");
            }
        }

        if (csvOutputPath is null && jsonOutputPath is null)
        {
            return new ImprovementCandidateGenerationOptionsParseResult(null, "generate-improvement-candidates requires --csv, --json, or both.");
        }

        return new ImprovementCandidateGenerationOptionsParseResult(
            new ImprovementCandidateGenerationOptions(inputPath, csvOutputPath, jsonOutputPath),
            null);
    }

    private static bool IsOption(string value)
    {
        return value.StartsWith("--", StringComparison.Ordinal);
    }
}

internal sealed record ImprovementCandidateGenerationOptionsParseResult(
    ImprovementCandidateGenerationOptions? Options,
    string? Error);

internal sealed record AutoDecisionGenerationOptions(
    string InputPath,
    string? CsvOutputPath,
    string? JsonOutputPath)
{
    public static AutoDecisionGenerationOptionsParseResult Parse(string[] args)
    {
        if (args.Length < 2)
        {
            return new AutoDecisionGenerationOptionsParseResult(null, "generate-auto-decisions requires an input CSV or JSON file path.");
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
                        return new AutoDecisionGenerationOptionsParseResult(null, "--csv requires an output file path.");
                    }

                    csvOutputPath = args[++index];
                    break;

                case "--json":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new AutoDecisionGenerationOptionsParseResult(null, "--json requires an output file path.");
                    }

                    jsonOutputPath = args[++index];
                    break;

                default:
                    return new AutoDecisionGenerationOptionsParseResult(null, $"unknown generate-auto-decisions option '{args[index]}'.");
            }
        }

        if (csvOutputPath is null && jsonOutputPath is null)
        {
            return new AutoDecisionGenerationOptionsParseResult(null, "generate-auto-decisions requires --csv, --json, or both.");
        }

        return new AutoDecisionGenerationOptionsParseResult(
            new AutoDecisionGenerationOptions(inputPath, csvOutputPath, jsonOutputPath),
            null);
    }

    private static bool IsOption(string value)
    {
        return value.StartsWith("--", StringComparison.Ordinal);
    }
}

internal sealed record AutoDecisionGenerationOptionsParseResult(
    AutoDecisionGenerationOptions? Options,
    string? Error);
