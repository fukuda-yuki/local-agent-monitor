namespace CopilotAgentObservability.ConfigCli;

internal sealed record DiagnosisCandidateAdapterOptions(
    string CandidatesPath,
    string MeasurementsPath,
    string? CsvOutputPath,
    string? JsonOutputPath)
{
    public static DiagnosisCandidateAdapterOptionsParseResult Parse(string[] args)
    {
        if (args.Length < 3)
        {
            return new DiagnosisCandidateAdapterOptionsParseResult(null, "adapt-diagnosis-candidates requires a diagnosis candidates file and a normalized measurements file.");
        }

        var candidatesPath = args[1];
        var measurementsPath = args[2];
        if (IsOption(candidatesPath) || IsOption(measurementsPath))
        {
            return new DiagnosisCandidateAdapterOptionsParseResult(null, "adapt-diagnosis-candidates requires a diagnosis candidates file and a normalized measurements file.");
        }

        string? csvOutputPath = null;
        string? jsonOutputPath = null;

        for (var index = 3; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--csv":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new DiagnosisCandidateAdapterOptionsParseResult(null, "--csv requires an output file path.");
                    }

                    csvOutputPath = args[++index];
                    break;

                case "--json":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new DiagnosisCandidateAdapterOptionsParseResult(null, "--json requires an output file path.");
                    }

                    jsonOutputPath = args[++index];
                    break;

                default:
                    return new DiagnosisCandidateAdapterOptionsParseResult(null, $"unknown adapt-diagnosis-candidates option '{args[index]}'.");
            }
        }

        if (csvOutputPath is null && jsonOutputPath is null)
        {
            return new DiagnosisCandidateAdapterOptionsParseResult(null, "adapt-diagnosis-candidates requires --csv, --json, or both.");
        }

        return new DiagnosisCandidateAdapterOptionsParseResult(
            new DiagnosisCandidateAdapterOptions(candidatesPath, measurementsPath, csvOutputPath, jsonOutputPath),
            null);
    }

    private static bool IsOption(string value)
    {
        return value.StartsWith("--", StringComparison.Ordinal);
    }
}

internal sealed record DiagnosisCandidateAdapterOptionsParseResult(
    DiagnosisCandidateAdapterOptions? Options,
    string? Error);
