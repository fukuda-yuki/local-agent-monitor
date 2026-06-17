namespace CopilotAgentObservability.ConfigCli;

internal sealed record DiagnosisCandidateOptions(
    string MeasurementsPath,
    string? RawInputPath,
    bool IncludeSensitiveContent,
    string? SensitiveOutputDir,
    string? CsvOutputPath,
    string? JsonOutputPath)
{
    public static DiagnosisCandidateOptionsParseResult Parse(string[] args)
    {
        if (args.Length < 2)
        {
            return new DiagnosisCandidateOptionsParseResult(null, "generate-diagnosis-candidates requires a normalized measurements CSV or JSON file path.");
        }

        var measurementsPath = args[1];
        if (IsOption(measurementsPath))
        {
            return new DiagnosisCandidateOptionsParseResult(null, "generate-diagnosis-candidates requires a normalized measurements CSV or JSON file path.");
        }

        string? rawInputPath = null;
        string? sensitiveOutputDir = null;
        string? csvOutputPath = null;
        string? jsonOutputPath = null;
        var includeSensitiveContent = false;

        for (var index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--raw":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new DiagnosisCandidateOptionsParseResult(null, "--raw requires a raw store database or raw OTLP JSON file path.");
                    }

                    rawInputPath = args[++index];
                    break;

                case "--include-sensitive-content":
                    includeSensitiveContent = true;
                    break;

                case "--sensitive-output-dir":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new DiagnosisCandidateOptionsParseResult(null, "--sensitive-output-dir requires a directory path.");
                    }

                    sensitiveOutputDir = args[++index];
                    break;

                case "--csv":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new DiagnosisCandidateOptionsParseResult(null, "--csv requires an output file path.");
                    }

                    csvOutputPath = args[++index];
                    break;

                case "--json":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new DiagnosisCandidateOptionsParseResult(null, "--json requires an output file path.");
                    }

                    jsonOutputPath = args[++index];
                    break;

                default:
                    return new DiagnosisCandidateOptionsParseResult(null, $"unknown generate-diagnosis-candidates option '{args[index]}'.");
            }
        }

        if (includeSensitiveContent && rawInputPath is null)
        {
            return new DiagnosisCandidateOptionsParseResult(null, "--include-sensitive-content requires --raw <raw-store.db|raw-otlp.json>.");
        }

        if (csvOutputPath is null && jsonOutputPath is null)
        {
            return new DiagnosisCandidateOptionsParseResult(null, "generate-diagnosis-candidates requires --csv, --json, or both.");
        }

        return new DiagnosisCandidateOptionsParseResult(
            new DiagnosisCandidateOptions(
                measurementsPath,
                rawInputPath,
                includeSensitiveContent,
                sensitiveOutputDir,
                csvOutputPath,
                jsonOutputPath),
            null);
    }

    private static bool IsOption(string value)
    {
        return value.StartsWith("--", StringComparison.Ordinal);
    }
}

internal sealed record DiagnosisCandidateOptionsParseResult(
    DiagnosisCandidateOptions? Options,
    string? Error);
