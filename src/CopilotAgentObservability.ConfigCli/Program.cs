using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.ConfigCli;

internal static class Program
{
    public static int Main(string[] args)
    {
        return CliApplication.Run(args, Console.Out, Console.Error);
    }
}

internal static class CliApplication
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.WriteLine(HelpText);
            return args.Length == 0 ? 1 : 0;
        }

        switch (args[0])
        {
            case "vscode-settings":
                output.WriteLine(ConfigSamples.CreateVsCodeSettingsJson());
                return 0;

            case "langfuse-vscode-settings":
                output.WriteLine(ConfigSamples.CreateLangfuseVsCodeSettingsJson());
                return 0;

            case "collector-vscode-settings":
                output.WriteLine(ConfigSamples.CreateCollectorVsCodeSettingsJson());
                return 0;

            case "vscode-env":
                output.WriteLine(ConfigSamples.CreateVsCodePowerShellScript());
                return 0;

            case "langfuse-vscode-env":
                output.WriteLine(ConfigSamples.CreateLangfuseVsCodePowerShellScript());
                return 0;

            case "collector-vscode-env":
                output.WriteLine(ConfigSamples.CreateCollectorVsCodePowerShellScript());
                return 0;

            case "vscode-file-settings":
                if (args.Length != 2)
                {
                    error.WriteLine("error: vscode-file-settings requires exactly one output file path.");
                    return 1;
                }

                output.WriteLine(ConfigSamples.CreateVsCodeFileSettingsJson(args[1]));
                return 0;

            case "copilot-cli-env":
                output.WriteLine(ConfigSamples.CreateCopilotCliPowerShellScript());
                return 0;

            case "langfuse-copilot-cli-env":
                output.WriteLine(ConfigSamples.CreateLangfuseCopilotCliPowerShellScript());
                return 0;

            case "collector-copilot-cli-env":
                output.WriteLine(ConfigSamples.CreateCollectorCopilotCliPowerShellScript());
                return 0;

            case "validate-resource-attributes":
                if (args.Length != 2)
                {
                    error.WriteLine("error: validate-resource-attributes requires exactly one OTEL_RESOURCE_ATTRIBUTES value.");
                    return 1;
                }

                var result = ResourceAttributeValidator.Validate(args[1]);
                foreach (var validationError in result.Errors)
                {
                    error.WriteLine($"error: {validationError}");
                }

                foreach (var warning in result.Warnings)
                {
                    error.WriteLine($"warning: {warning}");
                }

                if (!result.IsValid)
                {
                    return 1;
                }

                output.WriteLine("OTEL_RESOURCE_ATTRIBUTES is valid.");
                return 0;

            case "aggregate-measurements":
                return RunAggregateMeasurements(args, output, error);

            case "ingest-raw":
                return RunIngestRaw(args, output, error);

            case "validate-diagnoses":
                return RunValidateDiagnoses(args, output, error);

            case "generate-improvement-proposals":
                return RunGenerateImprovementProposals(args, output, error);

            case "evaluate-improvement-proposals":
                return RunEvaluateImprovementProposals(args, output, error);

            case "record-human-decisions":
                return RunRecordHumanDecisions(args, output, error);

            case "generate-decision-template":
                return RunGenerateDecisionTemplate(args, output, error);

            default:
                error.WriteLine($"error: unknown command '{args[0]}'.");
                error.WriteLine(HelpText);
                return 1;
        }
    }

    private static int RunIngestRaw(string[] args, TextWriter output, TextWriter error)
    {
        var parseResult = RawIngestOptions.Parse(args);
        if (parseResult.Error is not null)
        {
            error.WriteLine($"error: {parseResult.Error}");
            return 1;
        }

        try
        {
            if (!File.Exists(parseResult.Options!.InputPath))
            {
                error.WriteLine($"error: input file not found: {parseResult.Options.InputPath}");
                return 1;
            }

            var record = RawOtlpIngestor.CreateRecord(parseResult.Options.InputPath, DateTimeOffset.UtcNow);
            var store = new RawTelemetryStore(parseResult.Options.DatabasePath);
            store.CreateSchema();
            store.Insert(record);

            output.WriteLine("Ingested 1 raw telemetry record(s).");
            return 0;
        }
        catch (FileNotFoundException)
        {
            error.WriteLine($"error: input file not found: {parseResult.Options!.InputPath}");
            return 1;
        }
        catch (JsonException exception)
        {
            error.WriteLine($"error: input JSON is invalid: {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            error.WriteLine($"error: failed to read or write file: {exception.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            error.WriteLine($"error: failed to access file: {exception.Message}");
            return 1;
        }
    }

    private static int RunValidateDiagnoses(string[] args, TextWriter output, TextWriter error)
    {
        var parseResult = DiagnosisValidationOptions.Parse(args);
        if (parseResult.Error is not null)
        {
            error.WriteLine($"error: {parseResult.Error}");
            return 1;
        }

        try
        {
            if (!File.Exists(parseResult.Options!.InputPath))
            {
                error.WriteLine($"error: input file not found: {parseResult.Options.InputPath}");
                return 1;
            }

            var rows = DiagnosisInputReader.Read(parseResult.Options.InputPath);
            var validationErrors = DiagnosisValidator.Validate(rows);
            foreach (var validationError in validationErrors)
            {
                error.WriteLine($"error: {validationError}");
            }

            if (validationErrors.Count > 0)
            {
                return 1;
            }

            if (parseResult.Options.CsvOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.CsvOutputPath, DiagnosisOutputWriter.WriteCsv(rows), Encoding.UTF8);
            }

            if (parseResult.Options.JsonOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.JsonOutputPath, DiagnosisOutputWriter.WriteJson(rows), Encoding.UTF8);
            }

            output.WriteLine($"Validated {rows.Count} diagnosis record(s).");
            return 0;
        }
        catch (FileNotFoundException)
        {
            error.WriteLine($"error: input file not found: {parseResult.Options!.InputPath}");
            return 1;
        }
        catch (JsonException exception)
        {
            error.WriteLine($"error: input JSON is invalid: {exception.Message}");
            return 1;
        }
        catch (InvalidDataException exception)
        {
            error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            error.WriteLine($"error: failed to read or write file: {exception.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            error.WriteLine($"error: failed to access file: {exception.Message}");
            return 1;
        }
    }

    private static int RunGenerateImprovementProposals(string[] args, TextWriter output, TextWriter error)
    {
        var parseResult = ImprovementProposalGenerationOptions.Parse(args);
        if (parseResult.Error is not null)
        {
            error.WriteLine($"error: {parseResult.Error}");
            return 1;
        }

        try
        {
            if (!File.Exists(parseResult.Options!.InputPath))
            {
                error.WriteLine($"error: input file not found: {parseResult.Options.InputPath}");
                return 1;
            }

            var diagnoses = DiagnosisInputReader.Read(parseResult.Options.InputPath);
            var validationErrors = DiagnosisValidator.Validate(diagnoses);
            foreach (var validationError in validationErrors)
            {
                error.WriteLine($"error: {validationError}");
            }

            if (validationErrors.Count > 0)
            {
                return 1;
            }

            var proposals = ImprovementProposalGenerator.Generate(diagnoses);
            var proposalValidationErrors = ImprovementProposalSafetyValidator.Validate(proposals);
            foreach (var validationError in proposalValidationErrors)
            {
                error.WriteLine($"error: {validationError}");
            }

            if (proposalValidationErrors.Count > 0)
            {
                return 1;
            }

            if (parseResult.Options.CsvOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.CsvOutputPath, ImprovementProposalOutputWriter.WriteCsv(proposals), Encoding.UTF8);
            }

            if (parseResult.Options.JsonOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.JsonOutputPath, ImprovementProposalOutputWriter.WriteJson(proposals), Encoding.UTF8);
            }

            output.WriteLine($"Generated {proposals.Count} improvement proposal record(s).");
            return 0;
        }
        catch (FileNotFoundException)
        {
            error.WriteLine($"error: input file not found: {parseResult.Options!.InputPath}");
            return 1;
        }
        catch (JsonException exception)
        {
            error.WriteLine($"error: input JSON is invalid: {exception.Message}");
            return 1;
        }
        catch (InvalidDataException exception)
        {
            error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            error.WriteLine($"error: failed to read or write file: {exception.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            error.WriteLine($"error: failed to access file: {exception.Message}");
            return 1;
        }
    }

    private static int RunAggregateMeasurements(string[] args, TextWriter output, TextWriter error)
    {
        var parseResult = MeasurementAggregationOptions.Parse(args);
        if (parseResult.Error is not null)
        {
            error.WriteLine($"error: {parseResult.Error}");
            return 1;
        }

        try
        {
            if (!File.Exists(parseResult.Options!.InputPath))
            {
                error.WriteLine($"error: input file not found: {parseResult.Options.InputPath}");
                return 1;
            }

            var rows = MeasurementAggregator.Aggregate(parseResult.Options!.InputPath);

            if (parseResult.Options.CsvOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.CsvOutputPath, MeasurementOutputWriter.WriteCsv(rows), Encoding.UTF8);
            }

            if (parseResult.Options.JsonOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.JsonOutputPath, MeasurementOutputWriter.WriteJson(rows), Encoding.UTF8);
            }

            output.WriteLine($"Aggregated {rows.Count} measurement row(s).");
            return 0;
        }
        catch (FileNotFoundException)
        {
            error.WriteLine($"error: input file not found: {parseResult.Options!.InputPath}");
            return 1;
        }
        catch (JsonException exception)
        {
            error.WriteLine($"error: input JSON is invalid: {exception.Message}");
            return 1;
        }
        catch (InvalidDataException exception)
        {
            error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            error.WriteLine($"error: failed to read or write file: {exception.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            error.WriteLine($"error: failed to access file: {exception.Message}");
            return 1;
        }
    }

    private static int RunEvaluateImprovementProposals(string[] args, TextWriter output, TextWriter error)
    {
        var parseResult = ImprovementProposalEvaluationOptions.Parse(args);
        if (parseResult.Error is not null)
        {
            error.WriteLine($"error: {parseResult.Error}");
            return 1;
        }

        try
        {
            if (!File.Exists(parseResult.Options!.InputPath))
            {
                error.WriteLine($"error: input file not found: {parseResult.Options.InputPath}");
                return 1;
            }

            var proposals = ImprovementProposalInputReader.Read(parseResult.Options.InputPath);
            var validationErrors = ImprovementProposalValidator.Validate(proposals);
            foreach (var validationError in validationErrors)
            {
                error.WriteLine($"error: {validationError}");
            }

            if (validationErrors.Count > 0)
            {
                return 1;
            }

            var evaluations = ImprovementProposalEvaluator.Evaluate(proposals);
            var evaluationValidationErrors = ProposalEvaluationSafetyValidator.Validate(evaluations);
            foreach (var validationError in evaluationValidationErrors)
            {
                error.WriteLine($"error: {validationError}");
            }

            if (evaluationValidationErrors.Count > 0)
            {
                return 1;
            }

            if (parseResult.Options.CsvOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.CsvOutputPath, ProposalEvaluationOutputWriter.WriteCsv(evaluations), Encoding.UTF8);
            }

            if (parseResult.Options.JsonOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.JsonOutputPath, ProposalEvaluationOutputWriter.WriteJson(evaluations), Encoding.UTF8);
            }

            output.WriteLine($"Evaluated {evaluations.Count} improvement proposal record(s).");
            return 0;
        }
        catch (FileNotFoundException)
        {
            error.WriteLine($"error: input file not found: {parseResult.Options!.InputPath}");
            return 1;
        }
        catch (JsonException exception)
        {
            error.WriteLine($"error: input JSON is invalid: {exception.Message}");
            return 1;
        }
        catch (InvalidDataException exception)
        {
            error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            error.WriteLine($"error: failed to read or write file: {exception.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            error.WriteLine($"error: failed to access file: {exception.Message}");
            return 1;
        }
    }

    private static int RunRecordHumanDecisions(string[] args, TextWriter output, TextWriter error)
    {
        var parseResult = HumanDecisionRecordingOptions.Parse(args);
        if (parseResult.Error is not null)
        {
            error.WriteLine($"error: {parseResult.Error}");
            return 1;
        }

        try
        {
            if (!File.Exists(parseResult.Options!.EvaluationsPath))
            {
                error.WriteLine($"error: evaluations file not found: {parseResult.Options.EvaluationsPath}");
                return 1;
            }

            if (!File.Exists(parseResult.Options.DecisionsPath))
            {
                error.WriteLine($"error: decisions file not found: {parseResult.Options.DecisionsPath}");
                return 1;
            }

            var evaluations = ProposalEvaluationInputReader.Read(parseResult.Options.EvaluationsPath);
            var decisions = HumanDecisionInputReader.Read(parseResult.Options.DecisionsPath);
            var validationErrors = HumanDecisionValidator.Validate(decisions, evaluations);
            foreach (var validationError in validationErrors)
            {
                error.WriteLine($"error: {validationError}");
            }

            if (validationErrors.Count > 0)
            {
                return 1;
            }

            var safetyErrors = HumanDecisionSafetyValidator.Validate(decisions);
            foreach (var safetyError in safetyErrors)
            {
                error.WriteLine($"error: {safetyError}");
            }

            if (safetyErrors.Count > 0)
            {
                return 1;
            }

            if (parseResult.Options.CsvOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.CsvOutputPath, HumanDecisionOutputWriter.WriteCsv(decisions), Encoding.UTF8);
            }

            if (parseResult.Options.JsonOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.JsonOutputPath, HumanDecisionOutputWriter.WriteJson(decisions), Encoding.UTF8);
            }

            output.WriteLine($"Recorded {decisions.Count} human decision(s).");
            return 0;
        }
        catch (FileNotFoundException exception)
        {
            error.WriteLine($"error: file not found: {exception.FileName}");
            return 1;
        }
        catch (JsonException exception)
        {
            error.WriteLine($"error: input JSON is invalid: {exception.Message}");
            return 1;
        }
        catch (InvalidDataException exception)
        {
            error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            error.WriteLine($"error: failed to read or write file: {exception.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            error.WriteLine($"error: failed to access file: {exception.Message}");
            return 1;
        }
    }

    private static int RunGenerateDecisionTemplate(string[] args, TextWriter output, TextWriter error)
    {
        var parseResult = DecisionTemplateGenerationOptions.Parse(args);
        if (parseResult.Error is not null)
        {
            error.WriteLine($"error: {parseResult.Error}");
            return 1;
        }

        try
        {
            if (!File.Exists(parseResult.Options!.EvaluationsPath))
            {
                error.WriteLine($"error: evaluations file not found: {parseResult.Options.EvaluationsPath}");
                return 1;
            }

            var evaluations = ProposalEvaluationInputReader.Read(parseResult.Options.EvaluationsPath);
            var template = DecisionTemplateGenerator.Generate(evaluations);

            if (parseResult.Options.CsvOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.CsvOutputPath, HumanDecisionOutputWriter.WriteCsv(template), Encoding.UTF8);
            }

            if (parseResult.Options.JsonOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.JsonOutputPath, HumanDecisionOutputWriter.WriteJson(template), Encoding.UTF8);
            }

            output.WriteLine($"Generated decision template with {template.Count} row(s).");
            return 0;
        }
        catch (FileNotFoundException exception)
        {
            error.WriteLine($"error: file not found: {exception.FileName}");
            return 1;
        }
        catch (JsonException exception)
        {
            error.WriteLine($"error: input JSON is invalid: {exception.Message}");
            return 1;
        }
        catch (InvalidDataException exception)
        {
            error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            error.WriteLine($"error: failed to read or write file: {exception.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            error.WriteLine($"error: failed to access file: {exception.Message}");
            return 1;
        }
    }

    private const string HelpText = """
        Usage:
          config-cli vscode-settings
          config-cli langfuse-vscode-settings
          config-cli collector-vscode-settings
          config-cli vscode-env
          config-cli langfuse-vscode-env
          config-cli collector-vscode-env
          config-cli vscode-file-settings <outfile>
          config-cli copilot-cli-env
          config-cli langfuse-copilot-cli-env
          config-cli collector-copilot-cli-env
          config-cli validate-resource-attributes <OTEL_RESOURCE_ATTRIBUTES>
          config-cli ingest-raw <raw.json> --db <raw-store.db>
          config-cli aggregate-measurements <input.json> [--csv <output.csv>] [--json <output.json>]
          config-cli validate-diagnoses <input.csv|input.json> [--csv <output.csv>] [--json <output.json>]
          config-cli generate-improvement-proposals <diagnoses.csv|diagnoses.json> [--csv <output.csv>] [--json <output.json>]
          config-cli evaluate-improvement-proposals <proposals.csv|proposals.json> [--csv <output.csv>] [--json <output.json>]
          config-cli record-human-decisions <evaluations.csv|evaluations.json> <decisions.csv|decisions.json> [--csv <output.csv>] [--json <output.json>]
          config-cli generate-decision-template <evaluations.csv|evaluations.json> [--csv <output.csv>] [--json <output.json>]
        """;
}

internal static class RawStoreDefaults
{
    public const int SchemaVersion = 1;

    public static string DefaultDatabasePath => Path.Combine("data", "raw-store.db");
}

internal static class RawTelemetrySources
{
    public const string RawOtlp = "raw-otlp";
    public const string CollectorOutput = "collector-output";
    public const string LangfuseExport = "langfuse-export";

    private static readonly HashSet<string> AllowedValues = new(StringComparer.Ordinal)
    {
        RawOtlp,
        CollectorOutput,
        LangfuseExport,
    };

    public static bool IsAllowed(string source)
    {
        return AllowedValues.Contains(source);
    }
}

internal sealed record RawTelemetryRecord(
    long? Id,
    string Source,
    string? TraceId,
    DateTimeOffset ReceivedAt,
    string? ResourceAttributesJson,
    string PayloadJson,
    int SchemaVersion = RawStoreDefaults.SchemaVersion);

internal sealed record RawIngestOptions(
    string InputPath,
    string DatabasePath)
{
    public static RawIngestOptionsParseResult Parse(string[] args)
    {
        if (args.Length < 2)
        {
            return new RawIngestOptionsParseResult(null, "ingest-raw requires a raw OTLP JSON file path.");
        }

        var inputPath = args[1];
        string? databasePath = null;

        for (var index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--db":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new RawIngestOptionsParseResult(null, "--db requires a raw store database path.");
                    }

                    databasePath = args[++index];
                    break;

                default:
                    return new RawIngestOptionsParseResult(null, $"unknown ingest-raw option '{args[index]}'.");
            }
        }

        if (databasePath is null)
        {
            return new RawIngestOptionsParseResult(null, "ingest-raw requires --db <raw-store.db>.");
        }

        return new RawIngestOptionsParseResult(new RawIngestOptions(inputPath, databasePath), null);
    }

    private static bool IsOption(string value)
    {
        return value.StartsWith("--", StringComparison.Ordinal);
    }
}

internal sealed record RawIngestOptionsParseResult(
    RawIngestOptions? Options,
    string? Error);

internal static class RawOtlpIngestor
{
    public static RawTelemetryRecord CreateRecord(string inputPath, DateTimeOffset receivedAt)
    {
        var payloadJson = File.ReadAllText(inputPath, Encoding.UTF8);
        using var document = JsonDocument.Parse(payloadJson);

        return new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: FindTraceId(document.RootElement),
            ReceivedAt: receivedAt,
            ResourceAttributesJson: ExtractResourceAttributesJson(document.RootElement),
            PayloadJson: payloadJson);
    }

    private static string? FindTraceId(JsonElement root)
    {
        foreach (var resourceSpan in EnumerateArrayProperty(root, "resourceSpans"))
        {
            foreach (var scopeSpan in EnumerateArrayProperty(resourceSpan, "scopeSpans"))
            {
                foreach (var span in EnumerateArrayProperty(scopeSpan, "spans"))
                {
                    if (span.TryGetProperty("traceId", out var traceIdElement)
                        && traceIdElement.ValueKind == JsonValueKind.String)
                    {
                        var traceId = traceIdElement.GetString();
                        if (!string.IsNullOrWhiteSpace(traceId))
                        {
                            return traceId;
                        }
                    }
                }
            }
        }

        return null;
    }

    private static string? ExtractResourceAttributesJson(JsonElement root)
    {
        foreach (var resourceSpan in EnumerateArrayProperty(root, "resourceSpans"))
        {
            if (!resourceSpan.TryGetProperty("resource", out var resource)
                || !resource.TryGetProperty("attributes", out var attributes)
                || attributes.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var attributesObject = new JsonObject();
            foreach (var attribute in attributes.EnumerateArray())
            {
                if (!attribute.TryGetProperty("key", out var keyElement)
                    || keyElement.ValueKind != JsonValueKind.String
                    || !attribute.TryGetProperty("value", out var valueElement))
                {
                    continue;
                }

                var key = keyElement.GetString();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                attributesObject[key] = ConvertAttributeValue(valueElement);
            }

            if (attributesObject.Count > 0)
            {
                return attributesObject.ToJsonString();
            }
        }

        return null;
    }

    private static JsonNode? ConvertAttributeValue(JsonElement valueElement)
    {
        if (valueElement.TryGetProperty("stringValue", out var stringValue))
        {
            return JsonValue.Create(stringValue.GetString());
        }

        if (valueElement.TryGetProperty("intValue", out var intValue))
        {
            return intValue.ValueKind switch
            {
                JsonValueKind.String when long.TryParse(intValue.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => JsonValue.Create(parsed),
                JsonValueKind.Number when intValue.TryGetInt64(out var parsed) => JsonValue.Create(parsed),
                _ => JsonNode.Parse(intValue.GetRawText()),
            };
        }

        if (valueElement.TryGetProperty("doubleValue", out var doubleValue))
        {
            return doubleValue.ValueKind switch
            {
                JsonValueKind.String when double.TryParse(doubleValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => JsonValue.Create(parsed),
                JsonValueKind.Number when doubleValue.TryGetDouble(out var parsed) => JsonValue.Create(parsed),
                _ => JsonNode.Parse(doubleValue.GetRawText()),
            };
        }

        if (valueElement.TryGetProperty("boolValue", out var boolValue))
        {
            return boolValue.ValueKind == JsonValueKind.True || boolValue.ValueKind == JsonValueKind.False
                ? JsonValue.Create(boolValue.GetBoolean())
                : JsonNode.Parse(boolValue.GetRawText());
        }

        if (valueElement.TryGetProperty("arrayValue", out var arrayValue))
        {
            return ConvertArrayValue(arrayValue);
        }

        if (valueElement.TryGetProperty("kvlistValue", out var kvlistValue))
        {
            return ConvertKeyValueList(kvlistValue);
        }

        if (valueElement.TryGetProperty("bytesValue", out var bytesValue))
        {
            return bytesValue.ValueKind == JsonValueKind.String
                ? JsonValue.Create(bytesValue.GetString())
                : JsonNode.Parse(bytesValue.GetRawText());
        }

        return JsonNode.Parse(valueElement.GetRawText());
    }

    private static JsonArray ConvertArrayValue(JsonElement arrayValue)
    {
        var array = new JsonArray();
        if (!arrayValue.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return array;
        }

        foreach (var value in values.EnumerateArray())
        {
            array.Add(ConvertAttributeValue(value));
        }

        return array;
    }

    private static JsonObject ConvertKeyValueList(JsonElement kvlistValue)
    {
        var result = new JsonObject();
        if (!kvlistValue.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in values.EnumerateArray())
        {
            if (!item.TryGetProperty("key", out var keyElement)
                || keyElement.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("value", out var valueElement))
            {
                continue;
            }

            var key = keyElement.GetString();
            if (!string.IsNullOrWhiteSpace(key))
            {
                result[key] = ConvertAttributeValue(valueElement);
            }
        }

        return result;
    }

    private static IEnumerable<JsonElement> EnumerateArrayProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var arrayElement) || arrayElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in arrayElement.EnumerateArray())
        {
            yield return item;
        }
    }
}

internal sealed class RawTelemetryStore
{
    private readonly string databasePath;

    public RawTelemetryStore(string databasePath)
    {
        this.databasePath = databasePath;
    }

    public void CreateSchema()
    {
        var parentDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        using var connection = OpenConnection();
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS raw_records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source TEXT NOT NULL CHECK (source IN ('raw-otlp', 'collector-output', 'langfuse-export')),
                trace_id TEXT NULL,
                received_at TEXT NOT NULL,
                resource_attributes_json TEXT NULL,
                payload_json TEXT NOT NULL,
                schema_version INTEGER NOT NULL CHECK (schema_version = 1)
            );
            """);
        ExecuteNonQuery(
            connection,
            "CREATE INDEX IF NOT EXISTS IX_raw_records_trace_id ON raw_records(trace_id);");
        ExecuteNonQuery(
            connection,
            "CREATE INDEX IF NOT EXISTS IX_raw_records_received_at ON raw_records(received_at);");
        ExecuteNonQuery(
            connection,
            "CREATE INDEX IF NOT EXISTS IX_raw_records_source ON raw_records(source);");
    }

    public long Insert(RawTelemetryRecord record)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO raw_records (
                source,
                trace_id,
                received_at,
                resource_attributes_json,
                payload_json,
                schema_version
            )
            VALUES (
                $source,
                $trace_id,
                $received_at,
                $resource_attributes_json,
                $payload_json,
                $schema_version
            );
            SELECT last_insert_rowid();
            """;
        AddParameter(command, "$source", record.Source);
        AddParameter(command, "$trace_id", record.TraceId);
        AddParameter(command, "$received_at", record.ReceivedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        AddParameter(command, "$resource_attributes_json", record.ResourceAttributesJson);
        AddParameter(command, "$payload_json", record.PayloadJson);
        AddParameter(command, "$schema_version", record.SchemaVersion);
        return (long)command.ExecuteScalar()!;
    }

    public IReadOnlyList<RawTelemetryRecord> ListRecords()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                source,
                trace_id,
                received_at,
                resource_attributes_json,
                payload_json,
                schema_version
            FROM raw_records
            ORDER BY id;
            """;

        using var reader = command.ExecuteReader();
        var records = new List<RawTelemetryRecord>();
        while (reader.Read())
        {
            records.Add(new RawTelemetryRecord(
                Id: reader.GetInt64(0),
                Source: reader.GetString(1),
                TraceId: reader.IsDBNull(2) ? null : reader.GetString(2),
                ReceivedAt: DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                ResourceAttributesJson: reader.IsDBNull(4) ? null : reader.GetString(4),
                PayloadJson: reader.GetString(5),
                SchemaVersion: reader.GetInt32(6)));
        }

        return records;
    }

    private SqliteConnection OpenConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void AddParameter(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}

internal sealed record MeasurementAggregationOptions(
    string InputPath,
    string? CsvOutputPath,
    string? JsonOutputPath)
{
    public static MeasurementAggregationOptionsParseResult Parse(string[] args)
    {
        if (args.Length < 2)
        {
            return new MeasurementAggregationOptionsParseResult(null, "aggregate-measurements requires an input JSON file path.");
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
                        return new MeasurementAggregationOptionsParseResult(null, "--csv requires an output file path.");
                    }

                    csvOutputPath = args[++index];
                    break;

                case "--json":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new MeasurementAggregationOptionsParseResult(null, "--json requires an output file path.");
                    }

                    jsonOutputPath = args[++index];
                    break;

                default:
                    return new MeasurementAggregationOptionsParseResult(null, $"unknown aggregate-measurements option '{args[index]}'.");
            }
        }

        if (csvOutputPath is null && jsonOutputPath is null)
        {
            return new MeasurementAggregationOptionsParseResult(null, "aggregate-measurements requires --csv, --json, or both.");
        }

        return new MeasurementAggregationOptionsParseResult(
            new MeasurementAggregationOptions(inputPath, csvOutputPath, jsonOutputPath),
            null);
    }

    private static bool IsOption(string value)
    {
        return value.StartsWith("--", StringComparison.Ordinal);
    }
}

internal sealed record MeasurementAggregationOptionsParseResult(
    MeasurementAggregationOptions? Options,
    string? Error);

internal sealed record DiagnosisValidationOptions(
    string InputPath,
    string? CsvOutputPath,
    string? JsonOutputPath)
{
    public static DiagnosisValidationOptionsParseResult Parse(string[] args)
    {
        if (args.Length < 2)
        {
            return new DiagnosisValidationOptionsParseResult(null, "validate-diagnoses requires an input CSV or JSON file path.");
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
                        return new DiagnosisValidationOptionsParseResult(null, "--csv requires an output file path.");
                    }

                    csvOutputPath = args[++index];
                    break;

                case "--json":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new DiagnosisValidationOptionsParseResult(null, "--json requires an output file path.");
                    }

                    jsonOutputPath = args[++index];
                    break;

                default:
                    return new DiagnosisValidationOptionsParseResult(null, $"unknown validate-diagnoses option '{args[index]}'.");
            }
        }

        if (csvOutputPath is null && jsonOutputPath is null)
        {
            return new DiagnosisValidationOptionsParseResult(null, "validate-diagnoses requires --csv, --json, or both.");
        }

        return new DiagnosisValidationOptionsParseResult(
            new DiagnosisValidationOptions(inputPath, csvOutputPath, jsonOutputPath),
            null);
    }

    private static bool IsOption(string value)
    {
        return value.StartsWith("--", StringComparison.Ordinal);
    }
}

internal sealed record DiagnosisValidationOptionsParseResult(
    DiagnosisValidationOptions? Options,
    string? Error);

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

internal sealed record HumanDecisionRow(
    [property: JsonPropertyName("proposal_id")] string ProposalId,
    [property: JsonPropertyName("human_decision")] string HumanDecision,
    [property: JsonPropertyName("decision_rationale")] string DecisionRationale,
    [property: JsonPropertyName("approver_id")] string? ApproverId,
    [property: JsonPropertyName("approved_at")] string? ApprovedAt,
    [property: JsonPropertyName("conditions_or_notes")] string? ConditionsOrNotes);

internal static class MeasurementAggregator
{
    public static IReadOnlyList<MeasurementRow> Aggregate(string inputPath)
    {
        using var stream = File.OpenRead(inputPath);
        using var document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty("traces", out var tracesElement)
            || tracesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("input JSON must contain a top-level traces array.");
        }

        var rows = new List<MeasurementRow>();
        foreach (var traceElement in tracesElement.EnumerateArray())
        {
            rows.Add(CreateRow(traceElement));
        }

        return rows;
    }

    private static MeasurementRow CreateRow(JsonElement traceElement)
    {
        var metadata = traceElement.TryGetProperty("metadata", out var metadataElement)
            && metadataElement.ValueKind == JsonValueKind.Object
                ? metadataElement
                : default;

        var resourceAttributes = TryGetObject(metadata, "resourceAttributes", out var resourceAttributesElement)
            ? resourceAttributesElement
            : default;

        var inputTokens = ReadInt(traceElement, "usage", "input")
            ?? ReadInt(traceElement, "usage", "inputTokens")
            ?? ReadInt(traceElement, "usage", "promptTokens");
        var outputTokens = ReadInt(traceElement, "usage", "output")
            ?? ReadInt(traceElement, "usage", "outputTokens")
            ?? ReadInt(traceElement, "usage", "completionTokens");
        var totalTokens = ReadInt(traceElement, "usage", "total")
            ?? ReadInt(traceElement, "usage", "totalTokens")
            ?? (inputTokens.HasValue && outputTokens.HasValue ? inputTokens + outputTokens : null);

        var observations = TryGetArray(traceElement, "observations", out var observationsElement)
            ? observationsElement
            : default;

        var unknownSpans = new JsonArray();
        int? turnCount = ReadExplicitCount(traceElement, resourceAttributes, observations, TurnCountKeys);
        int? toolCallCount = ReadExplicitCount(traceElement, resourceAttributes, observations, ToolCallCountKeys);
        int? errorCount = null;

        if (observations.ValueKind == JsonValueKind.Array)
        {
            var observedTurnCount = 0;
            var observedToolCallCount = 0;
            errorCount = 0;

            foreach (var observation in observations.EnumerateArray())
            {
                if (IsKnownNonCountedObservation(observation))
                {
                    // Explicitly non-counted observations win over overlapping tool or GenAI attributes.
                }
                else if (IsToolObservation(observation))
                {
                    observedToolCallCount++;
                }
                else if (IsLlmObservation(observation))
                {
                    observedTurnCount++;
                }
                else
                {
                    unknownSpans.Add(CreateUnknownSpanNode(observation));
                }

                turnCount ??= ReadExplicitCount(observation, default, default, TurnCountKeys);

                if (IsErrorObservation(observation))
                {
                    errorCount++;
                }
            }

            turnCount ??= observedTurnCount;
            toolCallCount ??= observedToolCallCount;
        }
        else
        {
            toolCallCount = ReadExplicitCount(traceElement, resourceAttributes, observations, ToolCallCountKeys);
        }

        var aggregationNotes = CreateAggregationNotes(observations, unknownSpans);

        return new MeasurementRow(
            TraceId: ReadString(traceElement, "id") ?? ReadString(traceElement, "traceId"),
            ExperimentId: ReadString(resourceAttributes, "experiment.id"),
            ClientKind: ReadString(resourceAttributes, "client.kind"),
            TaskId: ReadString(resourceAttributes, "task.id"),
            TaskCategory: ReadString(resourceAttributes, "task.category"),
            TaskRunIndex: ReadInt(resourceAttributes, "task.run_index"),
            ExperimentCondition: ReadString(resourceAttributes, "experiment.condition"),
            PromptVersion: ReadString(resourceAttributes, "prompt.version"),
            AgentVariant: ReadString(resourceAttributes, "agent.variant"),
            RepoSnapshot: ReadString(resourceAttributes, "repo.snapshot"),
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            TotalTokens: totalTokens,
            TurnCount: turnCount,
            ToolCallCount: toolCallCount,
            DurationMs: ReadInt(traceElement, "durationMs") ?? ReadDurationMs(traceElement),
            ErrorCount: errorCount,
            SuccessStatus: "not-evaluated",
            EvaluatorId: null,
            EvaluationNotes: null,
            EvaluatedAt: null,
            UnknownSpansJson: unknownSpans.Count == 0 ? null : unknownSpans,
            UnknownAttributesJson: CreateUnknownAttributesNode(resourceAttributes),
            AggregationNotes: aggregationNotes);
    }

    private static readonly string[] TurnCountKeys =
    [
        "turn_count",
        "turnCount",
        "github.copilot.agent.turn.count",
    ];

    private static readonly string[] ToolCallCountKeys =
    [
        "tool_call_count",
        "toolCallCount",
        "github.copilot.tool.call.count",
    ];

    private static readonly string[] GenAiOperationKeys =
    [
        "gen_ai.operation.name",
        "genAi.operation.name",
    ];

    private static readonly string[] GenAiToolNameKeys =
    [
        "gen_ai.tool.name",
        "genAi.tool.name",
    ];

    private static int? ReadExplicitCount(JsonElement element, JsonElement resourceAttributes, JsonElement observations, IReadOnlyList<string> candidateKeys)
    {
        return ReadFirstInt(element, candidateKeys)
            ?? ReadFirstIntFromObject(element, "attributes", candidateKeys)
            ?? ReadFirstIntFromObject(element, "metadata", candidateKeys)
            ?? ReadFirstInt(resourceAttributes, candidateKeys)
            ?? ReadFirstIntFromObservations(observations, candidateKeys);
    }

    private static int? ReadFirstIntFromObject(JsonElement element, string objectPropertyName, IReadOnlyList<string> candidateKeys)
    {
        return TryGetObject(element, objectPropertyName, out var nested)
            ? ReadFirstInt(nested, candidateKeys)
            : null;
    }

    private static int? ReadFirstIntFromObservations(JsonElement observations, IReadOnlyList<string> candidateKeys)
    {
        if (observations.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var observation in observations.EnumerateArray())
        {
            var count = ReadFirstInt(observation, candidateKeys)
                ?? ReadFirstIntFromObject(observation, "attributes", candidateKeys);
            if (count.HasValue)
            {
                return count;
            }
        }

        return null;
    }

    private static int? ReadFirstInt(JsonElement element, IReadOnlyList<string> candidateKeys)
    {
        foreach (var candidateKey in candidateKeys)
        {
            var value = ReadInt(element, candidateKey);
            if (value.HasValue)
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsLlmObservation(JsonElement observation)
    {
        return HasStringValue(observation, "type", "generation")
            || HasStringValue(observation, "type", "chat")
            || HasAnyStringValue(observation, GenAiOperationKeys, "chat", "generate_content", "text_completion")
            || HasAnyStringValueInObject(observation, "attributes", GenAiOperationKeys, "chat", "generate_content", "text_completion")
            || HasSpanName(observation, "chat");
    }

    private static bool IsToolObservation(JsonElement observation)
    {
        return HasStringValue(observation, "type", "tool")
            || HasStringValue(observation, "kind", "tool")
            || HasStringValue(observation, "category", "tool")
            || HasAnyStringValue(observation, GenAiToolNameKeys)
            || HasAnyStringValueInObject(observation, "attributes", GenAiToolNameKeys)
            || HasSpanName(observation, "execute_tool");
    }

    private static bool IsKnownNonCountedObservation(JsonElement observation)
    {
        return HasSpanName(observation, "invoke_agent")
            || HasSpanName(observation, "execute_hook")
            || HasSpanName(observation, "lifecycle_event")
            || HasAnyStringValue(observation, ["type", "kind", "category"], "permission", "approval", "hook", "lifecycle")
            || HasAnyStringValueInObject(observation, "attributes", ["type", "kind", "category"], "permission", "approval", "hook", "lifecycle");
    }

    private static string CreateAggregationNotes(JsonElement observations, JsonArray unknownSpans)
    {
        var countSource = observations.ValueKind == JsonValueKind.Array
            ? "turn_count and tool_call_count are calculated from explicit count attributes when present, otherwise from classified observations."
            : "turn_count and tool_call_count require explicit count attributes when observations are missing.";

        return unknownSpans.Count == 0
            ? countSource
            : $"{countSource} Unknown observations are listed in unknown_spans_json.";
    }

    private static bool IsErrorObservation(JsonElement observation)
    {
        if (HasStringValue(observation, "level", "error")
            || HasStringValue(observation, "status", "error"))
        {
            return true;
        }

        return TryGetObject(observation, "statusMessage", out _)
            || ReadString(observation, "error") is not null;
    }

    private static bool HasStringValue(JsonElement element, string propertyName, string expected)
    {
        return ReadString(element, propertyName) is { } actual
            && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAnyStringValue(JsonElement element, IReadOnlyList<string> propertyNames, params string[] expectedValues)
    {
        foreach (var propertyName in propertyNames)
        {
            if (ReadString(element, propertyName) is not { } actual)
            {
                continue;
            }

            if (expectedValues.Length == 0
                || expectedValues.Any(expected => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAnyStringValueInObject(JsonElement element, string objectPropertyName, IReadOnlyList<string> propertyNames, params string[] expectedValues)
    {
        return TryGetObject(element, objectPropertyName, out var nested)
            && HasAnyStringValue(nested, propertyNames, expectedValues);
    }

    private static bool HasSpanName(JsonElement observation, string expectedName)
    {
        if (ReadString(observation, "name") is not { } actualName)
        {
            return false;
        }

        return string.Equals(actualName, expectedName, StringComparison.OrdinalIgnoreCase)
            || actualName.StartsWith($"{expectedName} ", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject CreateUnknownSpanNode(JsonElement observation)
    {
        var node = new JsonObject();
        AddStringNode(node, "id", ReadString(observation, "id"));
        AddStringNode(node, "name", ReadString(observation, "name"));
        AddStringNode(node, "type", ReadString(observation, "type"));
        AddStringNode(node, "kind", ReadString(observation, "kind"));
        return node;
    }

    private static JsonObject? CreateUnknownAttributesNode(JsonElement resourceAttributes)
    {
        var unknown = new JsonObject();

        AddUnknownResourceAttributes(unknown, resourceAttributes);

        return unknown.Count == 0 ? null : unknown;
    }

    private static void AddUnknownResourceAttributes(JsonObject unknown, JsonElement resourceAttributes)
    {
        if (resourceAttributes.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var mappedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "experiment.id",
            "client.kind",
            "task.id",
            "task.category",
            "task.run_index",
            "experiment.condition",
            "prompt.version",
            "agent.variant",
            "repo.snapshot",
        };

        var unknownResourceAttributes = new JsonObject();
        foreach (var property in resourceAttributes.EnumerateObject())
        {
            if (!mappedKeys.Contains(property.Name) && !IsContentOrCredentialLikeKey(property.Name))
            {
                unknownResourceAttributes[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }

        if (unknownResourceAttributes.Count > 0)
        {
            unknown["resourceAttributes"] = unknownResourceAttributes;
        }
    }

    private static bool IsContentOrCredentialLikeKey(string key)
    {
        var normalizedKey = key.ToLowerInvariant();
        return normalizedKey.Contains("prompt", StringComparison.Ordinal)
            || normalizedKey.Contains("response", StringComparison.Ordinal)
            || normalizedKey.Contains("content", StringComparison.Ordinal)
            || normalizedKey.Contains("argument", StringComparison.Ordinal)
            || normalizedKey.Contains("result", StringComparison.Ordinal)
            || normalizedKey.Contains("secret", StringComparison.Ordinal)
            || normalizedKey.Contains("password", StringComparison.Ordinal)
            || normalizedKey.Contains("credential", StringComparison.Ordinal)
            || normalizedKey.Contains("authorization", StringComparison.Ordinal)
            || normalizedKey.Contains("api.key", StringComparison.Ordinal)
            || normalizedKey.Contains("token", StringComparison.Ordinal);
    }

    private static void AddStringNode(JsonObject node, string propertyName, string? value)
    {
        if (value is not null)
        {
            node[propertyName] = value;
        }
    }

    private static int? ReadDurationMs(JsonElement traceElement)
    {
        var durationSeconds = ReadDouble(traceElement, "duration");
        if (!durationSeconds.HasValue)
        {
            return null;
        }

        return (int)Math.Round(durationSeconds.Value * 1000, MidpointRounding.AwayFromZero);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return ReadInt(property);
    }

    private static int? ReadInt(JsonElement element, string containerPropertyName, string propertyName)
    {
        return TryGetObject(element, containerPropertyName, out var container)
            ? ReadInt(container, propertyName)
            : null;
    }

    private static int? ReadInt(JsonElement property)
    {
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null,
        };
    }

    private static double? ReadDouble(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var value) => value,
            JsonValueKind.String when double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
            _ => null,
        };
    }

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out value)
            && value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetArray(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out value)
            && value.ValueKind == JsonValueKind.Array;
    }
}

internal sealed record MeasurementRow(
    [property: JsonPropertyName("trace_id")] string? TraceId,
    [property: JsonPropertyName("experiment_id")] string? ExperimentId,
    [property: JsonPropertyName("client_kind")] string? ClientKind,
    [property: JsonPropertyName("task_id")] string? TaskId,
    [property: JsonPropertyName("task_category")] string? TaskCategory,
    [property: JsonPropertyName("task_run_index")] int? TaskRunIndex,
    [property: JsonPropertyName("experiment_condition")] string? ExperimentCondition,
    [property: JsonPropertyName("prompt_version")] string? PromptVersion,
    [property: JsonPropertyName("agent_variant")] string? AgentVariant,
    [property: JsonPropertyName("repo_snapshot")] string? RepoSnapshot,
    [property: JsonPropertyName("input_tokens")] int? InputTokens,
    [property: JsonPropertyName("output_tokens")] int? OutputTokens,
    [property: JsonPropertyName("total_tokens")] int? TotalTokens,
    [property: JsonPropertyName("turn_count")] int? TurnCount,
    [property: JsonPropertyName("tool_call_count")] int? ToolCallCount,
    [property: JsonPropertyName("duration_ms")] int? DurationMs,
    [property: JsonPropertyName("error_count")] int? ErrorCount,
    [property: JsonPropertyName("success_status")] string SuccessStatus,
    [property: JsonPropertyName("evaluator_id")] string? EvaluatorId,
    [property: JsonPropertyName("evaluation_notes")] string? EvaluationNotes,
    [property: JsonPropertyName("evaluated_at")] string? EvaluatedAt,
    [property: JsonPropertyName("unknown_spans_json")] JsonArray? UnknownSpansJson,
    [property: JsonPropertyName("unknown_attributes_json")] JsonObject? UnknownAttributesJson,
    [property: JsonPropertyName("aggregation_notes")] string? AggregationNotes);

internal static class MeasurementOutputWriter
{
    public static readonly string[] Columns =
    [
        "trace_id",
        "experiment_id",
        "client_kind",
        "task_id",
        "task_category",
        "task_run_index",
        "experiment_condition",
        "prompt_version",
        "agent_variant",
        "repo_snapshot",
        "input_tokens",
        "output_tokens",
        "total_tokens",
        "turn_count",
        "tool_call_count",
        "duration_ms",
        "error_count",
        "success_status",
        "evaluator_id",
        "evaluation_notes",
        "evaluated_at",
        "unknown_spans_json",
        "unknown_attributes_json",
        "aggregation_notes",
    ];

    public static string WriteJson(IReadOnlyList<MeasurementRow> rows)
    {
        return JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    public static string WriteCsv(IReadOnlyList<MeasurementRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', Columns));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', Columns.Select(column => EscapeCsv(GetValue(row, column)))));
        }

        return builder.ToString();
    }

    private static string? GetValue(MeasurementRow row, string column)
    {
        return column switch
        {
            "trace_id" => row.TraceId,
            "experiment_id" => row.ExperimentId,
            "client_kind" => row.ClientKind,
            "task_id" => row.TaskId,
            "task_category" => row.TaskCategory,
            "task_run_index" => row.TaskRunIndex?.ToString(),
            "experiment_condition" => row.ExperimentCondition,
            "prompt_version" => row.PromptVersion,
            "agent_variant" => row.AgentVariant,
            "repo_snapshot" => row.RepoSnapshot,
            "input_tokens" => row.InputTokens?.ToString(),
            "output_tokens" => row.OutputTokens?.ToString(),
            "total_tokens" => row.TotalTokens?.ToString(),
            "turn_count" => row.TurnCount?.ToString(),
            "tool_call_count" => row.ToolCallCount?.ToString(),
            "duration_ms" => row.DurationMs?.ToString(),
            "error_count" => row.ErrorCount?.ToString(),
            "success_status" => row.SuccessStatus,
            "evaluator_id" => row.EvaluatorId,
            "evaluation_notes" => row.EvaluationNotes,
            "evaluated_at" => row.EvaluatedAt,
            "unknown_spans_json" => row.UnknownSpansJson?.ToJsonString(),
            "unknown_attributes_json" => row.UnknownAttributesJson?.ToJsonString(),
            "aggregation_notes" => row.AggregationNotes,
            _ => null,
        };
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Any(character => character is ',' or '"' or '\r' or '\n')
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }
}

internal sealed record DiagnosisRow(
    [property: JsonPropertyName("trace_id")] string? TraceId,
    [property: JsonPropertyName("task_id")] string? TaskId,
    [property: JsonPropertyName("task_category")] string? TaskCategory,
    [property: JsonPropertyName("client_kind")] string? ClientKind,
    [property: JsonPropertyName("comparison_id")] string? ComparisonId,
    [property: JsonPropertyName("experiment_id")] string? ExperimentId,
    [property: JsonPropertyName("experiment_condition")] string? ExperimentCondition,
    [property: JsonPropertyName("prompt_version")] string? PromptVersion,
    [property: JsonPropertyName("agent_variant")] string? AgentVariant,
    [property: JsonPropertyName("task_run_index")] int? TaskRunIndex,
    [property: JsonPropertyName("failure_category_id")] string? FailureCategoryId,
    [property: JsonPropertyName("anti_pattern_id")] string? AntiPatternId,
    [property: JsonPropertyName("severity")] string? Severity,
    [property: JsonPropertyName("evidence_summary")] string? EvidenceSummary,
    [property: JsonPropertyName("recommended_improvement_target")] string? RecommendedImprovementTarget,
    [property: JsonPropertyName("review_status")] string? ReviewStatus);

internal sealed record ImprovementProposalRow(
    [property: JsonPropertyName("proposal_id")] string ProposalId,
    [property: JsonPropertyName("source_diagnosis_index")] int SourceDiagnosisIndex,
    [property: JsonPropertyName("trace_id")] string? TraceId,
    [property: JsonPropertyName("task_id")] string? TaskId,
    [property: JsonPropertyName("task_category")] string? TaskCategory,
    [property: JsonPropertyName("client_kind")] string? ClientKind,
    [property: JsonPropertyName("comparison_id")] string? ComparisonId,
    [property: JsonPropertyName("experiment_id")] string? ExperimentId,
    [property: JsonPropertyName("experiment_condition")] string? ExperimentCondition,
    [property: JsonPropertyName("prompt_version")] string? PromptVersion,
    [property: JsonPropertyName("agent_variant")] string? AgentVariant,
    [property: JsonPropertyName("task_run_index")] int? TaskRunIndex,
    [property: JsonPropertyName("failure_category_id")] string? FailureCategoryId,
    [property: JsonPropertyName("anti_pattern_id")] string? AntiPatternId,
    [property: JsonPropertyName("severity")] string? Severity,
    [property: JsonPropertyName("improvement_target")] string? ImprovementTarget,
    [property: JsonPropertyName("evidence_summary")] string? EvidenceSummary,
    [property: JsonPropertyName("proposal_title")] string ProposalTitle,
    [property: JsonPropertyName("proposal_summary")] string ProposalSummary,
    [property: JsonPropertyName("proposed_change")] string ProposedChange,
    [property: JsonPropertyName("acceptance_check")] string AcceptanceCheck,
    [property: JsonPropertyName("human_review_status")] string HumanReviewStatus);

internal sealed record ProposalEvaluationRow(
    [property: JsonPropertyName("proposal_id")] string ProposalId,
    [property: JsonPropertyName("source_diagnosis_index")] int SourceDiagnosisIndex,
    [property: JsonPropertyName("trace_id")] string? TraceId,
    [property: JsonPropertyName("task_id")] string? TaskId,
    [property: JsonPropertyName("task_category")] string? TaskCategory,
    [property: JsonPropertyName("client_kind")] string? ClientKind,
    [property: JsonPropertyName("comparison_id")] string? ComparisonId,
    [property: JsonPropertyName("experiment_id")] string? ExperimentId,
    [property: JsonPropertyName("experiment_condition")] string? ExperimentCondition,
    [property: JsonPropertyName("prompt_version")] string? PromptVersion,
    [property: JsonPropertyName("agent_variant")] string? AgentVariant,
    [property: JsonPropertyName("task_run_index")] int? TaskRunIndex,
    [property: JsonPropertyName("failure_category_id")] string? FailureCategoryId,
    [property: JsonPropertyName("anti_pattern_id")] string? AntiPatternId,
    [property: JsonPropertyName("severity")] string? Severity,
    [property: JsonPropertyName("improvement_target")] string? ImprovementTarget,
    [property: JsonPropertyName("proposal_title")] string ProposalTitle,
    [property: JsonPropertyName("proposal_evaluation_status")] string ProposalEvaluationStatus,
    [property: JsonPropertyName("evaluator_findings")] string EvaluatorFindings,
    [property: JsonPropertyName("required_human_checks")] string RequiredHumanChecks,
    [property: JsonPropertyName("evaluator_notes")] string EvaluatorNotes);

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

        var header = ParseCsvLine(lines[0]);
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

            var values = ParseCsvLine(lines[lineIndex]);
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

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    builder.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (character == ',' && !inQuotes)
            {
                values.Add(builder.ToString());
                builder.Clear();
            }
            else
            {
                builder.Append(character);
            }
        }

        if (inQuotes)
        {
            throw new InvalidDataException("CSV row contains an unterminated quoted value.");
        }

        values.Add(builder.ToString());
        return values;
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

internal static class DiagnosisOutputWriter
{
    public static readonly string[] Columns =
    [
        "trace_id",
        "task_id",
        "task_category",
        "client_kind",
        "comparison_id",
        "experiment_id",
        "experiment_condition",
        "prompt_version",
        "agent_variant",
        "task_run_index",
        "failure_category_id",
        "anti_pattern_id",
        "severity",
        "evidence_summary",
        "recommended_improvement_target",
        "review_status",
    ];

    public static string WriteJson(IReadOnlyList<DiagnosisRow> rows)
    {
        return JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    public static string WriteCsv(IReadOnlyList<DiagnosisRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', Columns));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', Columns.Select(column => EscapeCsv(GetValue(row, column)))));
        }

        return builder.ToString();
    }

    private static string? GetValue(DiagnosisRow row, string column)
    {
        return column switch
        {
            "trace_id" => row.TraceId,
            "task_id" => row.TaskId,
            "task_category" => row.TaskCategory,
            "client_kind" => row.ClientKind,
            "comparison_id" => row.ComparisonId,
            "experiment_id" => row.ExperimentId,
            "experiment_condition" => row.ExperimentCondition,
            "prompt_version" => row.PromptVersion,
            "agent_variant" => row.AgentVariant,
            "task_run_index" => row.TaskRunIndex?.ToString(CultureInfo.InvariantCulture),
            "failure_category_id" => row.FailureCategoryId,
            "anti_pattern_id" => row.AntiPatternId,
            "severity" => row.Severity,
            "evidence_summary" => row.EvidenceSummary,
            "recommended_improvement_target" => row.RecommendedImprovementTarget,
            "review_status" => row.ReviewStatus,
            _ => null,
        };
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Any(character => character is ',' or '"' or '\r' or '\n')
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }
}

internal static class ImprovementProposalSafetyValidator
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<ImprovementProposalRow> rows)
    {
        var errors = new List<string>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var rowNumber = index + 1;
            foreach (var field in GetStringFields(row))
            {
                if (!string.IsNullOrWhiteSpace(field.Value)
                    && DiagnosisValidator.ContainsUnsafeMaterial(field.Value))
                {
                    errors.Add($"row {rowNumber}: proposal field '{field.Name}' appears to contain raw content, credential, secret, token, or identity-bearing material.");
                }
            }
        }

        return errors;
    }

    private static IEnumerable<(string Name, string? Value)> GetStringFields(ImprovementProposalRow row)
    {
        yield return ("proposal_id", row.ProposalId);
        yield return ("trace_id", row.TraceId);
        yield return ("task_id", row.TaskId);
        yield return ("task_category", row.TaskCategory);
        yield return ("client_kind", row.ClientKind);
        yield return ("comparison_id", row.ComparisonId);
        yield return ("experiment_id", row.ExperimentId);
        yield return ("experiment_condition", row.ExperimentCondition);
        yield return ("prompt_version", row.PromptVersion);
        yield return ("agent_variant", row.AgentVariant);
        yield return ("failure_category_id", row.FailureCategoryId);
        yield return ("anti_pattern_id", row.AntiPatternId);
        yield return ("severity", row.Severity);
        yield return ("improvement_target", row.ImprovementTarget);
        yield return ("evidence_summary", row.EvidenceSummary);
        yield return ("proposal_title", row.ProposalTitle);
        yield return ("proposal_summary", row.ProposalSummary);
        yield return ("proposed_change", row.ProposedChange);
        yield return ("acceptance_check", row.AcceptanceCheck);
        yield return ("human_review_status", row.HumanReviewStatus);
    }
}

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

        var header = ParseCsvLine(lines[0]);
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

            var values = ParseCsvLine(lines[lineIndex]);
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

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    builder.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (character == ',' && !inQuotes)
            {
                values.Add(builder.ToString());
                builder.Clear();
            }
            else
            {
                builder.Append(character);
            }
        }

        if (inQuotes)
        {
            throw new InvalidDataException("CSV row contains an unterminated quoted value.");
        }

        values.Add(builder.ToString());
        return values;
    }
}

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

internal static class ImprovementProposalGenerator
{
    private const string AcceptedForProposal = "accepted-for-proposal";
    private const string HumanReviewStatus = "needs-human-review";

    public static IReadOnlyList<ImprovementProposalRow> Generate(IReadOnlyList<DiagnosisRow> diagnoses)
    {
        var proposals = new List<ImprovementProposalRow>();
        for (var index = 0; index < diagnoses.Count; index++)
        {
            var diagnosis = diagnoses[index];
            if (!string.Equals(diagnosis.ReviewStatus, AcceptedForProposal, StringComparison.Ordinal))
            {
                continue;
            }

            var sourceDiagnosisIndex = index + 1;
            var proposalId = $"proposal-{proposals.Count + 1:0000}";
            var descriptor = CreateDescriptor(diagnosis);
            var target = diagnosis.RecommendedImprovementTarget ?? "unknown";

            proposals.Add(new ImprovementProposalRow(
                ProposalId: proposalId,
                SourceDiagnosisIndex: sourceDiagnosisIndex,
                TraceId: diagnosis.TraceId,
                TaskId: diagnosis.TaskId,
                TaskCategory: diagnosis.TaskCategory,
                ClientKind: diagnosis.ClientKind,
                ComparisonId: diagnosis.ComparisonId,
                ExperimentId: diagnosis.ExperimentId,
                ExperimentCondition: diagnosis.ExperimentCondition,
                PromptVersion: diagnosis.PromptVersion,
                AgentVariant: diagnosis.AgentVariant,
                TaskRunIndex: diagnosis.TaskRunIndex,
                FailureCategoryId: diagnosis.FailureCategoryId,
                AntiPatternId: diagnosis.AntiPatternId,
                Severity: diagnosis.Severity,
                ImprovementTarget: diagnosis.RecommendedImprovementTarget,
                EvidenceSummary: diagnosis.EvidenceSummary,
                ProposalTitle: $"Review {target} improvement for {descriptor}",
                ProposalSummary: $"A {diagnosis.Severity} diagnosis for {descriptor} was accepted for proposal. Evidence: {diagnosis.EvidenceSummary}",
                ProposedChange: $"Prepare a human-reviewed {target} improvement that addresses {descriptor} using only the sanitized evidence summary.",
                AcceptanceCheck: $"A human reviewer can confirm the {target} proposal addresses {descriptor} without automatic adoption, repository edits, patches, diffs, or sensitive material exposure.",
                HumanReviewStatus: HumanReviewStatus));
        }

        return proposals;
    }

    private static string CreateDescriptor(DiagnosisRow diagnosis)
    {
        return string.IsNullOrWhiteSpace(diagnosis.AntiPatternId)
            ? diagnosis.FailureCategoryId ?? "unknown diagnosis"
            : $"{diagnosis.FailureCategoryId} / {diagnosis.AntiPatternId}";
    }
}

internal static class ImprovementProposalOutputWriter
{
    public static readonly string[] Columns =
    [
        "proposal_id",
        "source_diagnosis_index",
        "trace_id",
        "task_id",
        "task_category",
        "client_kind",
        "comparison_id",
        "experiment_id",
        "experiment_condition",
        "prompt_version",
        "agent_variant",
        "task_run_index",
        "failure_category_id",
        "anti_pattern_id",
        "severity",
        "improvement_target",
        "evidence_summary",
        "proposal_title",
        "proposal_summary",
        "proposed_change",
        "acceptance_check",
        "human_review_status",
    ];

    public static string WriteJson(IReadOnlyList<ImprovementProposalRow> rows)
    {
        return JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    public static string WriteCsv(IReadOnlyList<ImprovementProposalRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', Columns));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', Columns.Select(column => EscapeCsv(GetValue(row, column)))));
        }

        return builder.ToString();
    }

    private static string? GetValue(ImprovementProposalRow row, string column)
    {
        return column switch
        {
            "proposal_id" => row.ProposalId,
            "source_diagnosis_index" => row.SourceDiagnosisIndex.ToString(CultureInfo.InvariantCulture),
            "trace_id" => row.TraceId,
            "task_id" => row.TaskId,
            "task_category" => row.TaskCategory,
            "client_kind" => row.ClientKind,
            "comparison_id" => row.ComparisonId,
            "experiment_id" => row.ExperimentId,
            "experiment_condition" => row.ExperimentCondition,
            "prompt_version" => row.PromptVersion,
            "agent_variant" => row.AgentVariant,
            "task_run_index" => row.TaskRunIndex?.ToString(CultureInfo.InvariantCulture),
            "failure_category_id" => row.FailureCategoryId,
            "anti_pattern_id" => row.AntiPatternId,
            "severity" => row.Severity,
            "improvement_target" => row.ImprovementTarget,
            "evidence_summary" => row.EvidenceSummary,
            "proposal_title" => row.ProposalTitle,
            "proposal_summary" => row.ProposalSummary,
            "proposed_change" => row.ProposedChange,
            "acceptance_check" => row.AcceptanceCheck,
            "human_review_status" => row.HumanReviewStatus,
            _ => null,
        };
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Any(character => character is ',' or '"' or '\r' or '\n')
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }
}

internal static class ImprovementProposalEvaluator
{
    private const string ReadyForHumanApproval = "ready-for-human-approval";
    private const string NeedsRevision = "needs-revision";
    private const string Blocked = "blocked";

    public static IReadOnlyList<ProposalEvaluationRow> Evaluate(IReadOnlyList<ImprovementProposalRow> proposals)
    {
        var rows = new List<ProposalEvaluationRow>();
        foreach (var proposal in proposals)
        {
            var combinedText = string.Join(
                ' ',
                proposal.ProposalTitle,
                proposal.ProposalSummary,
                proposal.ProposedChange,
                proposal.AcceptanceCheck);

            string status;
            string findings;
            string requiredHumanChecks;
            string notes;

            if (ContainsOutOfScopeAction(combinedText))
            {
                status = Blocked;
                findings = "Proposal text appears to request out-of-scope automated implementation or decision.";
                requiredHumanChecks = "Revise the proposal so it remains a human-reviewed recommendation only.";
                notes = "Blocked before human approval workflow.";
            }
            else if (!ContainsHumanReviewIntent(combinedText)
                || string.IsNullOrWhiteSpace(proposal.TaskCategory)
                || string.IsNullOrWhiteSpace(proposal.ClientKind))
            {
                status = NeedsRevision;
                findings = "Proposal needs clearer review context before approval.";
                requiredHumanChecks = "Confirm task category, client kind, target, sanitized evidence, and approval boundaries.";
                notes = "No adoption or repository modification is performed.";
            }
            else
            {
                status = ReadyForHumanApproval;
                findings = "Proposal is schema-valid and limited to human review.";
                requiredHumanChecks = "Confirm target, sanitized evidence, and non-scope boundaries before approval.";
                notes = "No automatic adoption or repository modification is performed.";
            }

            rows.Add(new ProposalEvaluationRow(
                ProposalId: proposal.ProposalId,
                SourceDiagnosisIndex: proposal.SourceDiagnosisIndex,
                TraceId: proposal.TraceId,
                TaskId: proposal.TaskId,
                TaskCategory: proposal.TaskCategory,
                ClientKind: proposal.ClientKind,
                ComparisonId: proposal.ComparisonId,
                ExperimentId: proposal.ExperimentId,
                ExperimentCondition: proposal.ExperimentCondition,
                PromptVersion: proposal.PromptVersion,
                AgentVariant: proposal.AgentVariant,
                TaskRunIndex: proposal.TaskRunIndex,
                FailureCategoryId: proposal.FailureCategoryId,
                AntiPatternId: proposal.AntiPatternId,
                Severity: proposal.Severity,
                ImprovementTarget: proposal.ImprovementTarget,
                ProposalTitle: proposal.ProposalTitle,
                ProposalEvaluationStatus: status,
                EvaluatorFindings: findings,
                RequiredHumanChecks: requiredHumanChecks,
                EvaluatorNotes: notes));
        }

        return rows;
    }

    private static bool ContainsHumanReviewIntent(string value)
    {
        var normalized = value.ToLowerInvariant();
        return normalized.Contains("human", StringComparison.Ordinal)
            && (normalized.Contains("review", StringComparison.Ordinal)
                || normalized.Contains("approval", StringComparison.Ordinal));
    }

    private static bool ContainsOutOfScopeAction(string value)
    {
        var normalized = value.ToLowerInvariant();
        string[] blockedPhrases =
        [
            "automatically adopt",
            "auto-adopt",
            "auto adopt",
            "adopt this proposal",
            "adopt the proposal",
            "auto-merge",
            "auto merge",
            "generate patch",
            "create patch",
            "apply patch",
            "generate diff",
            "create diff",
            "apply diff",
            "edit repository",
            "edit repositories",
            "modify repository",
            "modify repositories",
            "make repository changes",
            "create commit",
            "commit changes",
            "push branch",
            "push to remote",
            "create pr",
            "open pr",
            "submit pr",
            "raise a pr",
            "open pull request",
            "create pull request",
            "submit pull request",
            "raise a pull request",
            "decide winner",
            "select winner",
            "win/loss",
            "evaluate improvement effectiveness",
            "judge improvement effect",
            "determine improvement effect",
            "implement automatically",
            "automatically implement",
            "implement this improvement",
            "implement the improvement",
        ];

        return blockedPhrases.Any(phrase => normalized.Contains(phrase, StringComparison.Ordinal));
    }
}

internal static class ProposalEvaluationSafetyValidator
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<ProposalEvaluationRow> rows)
    {
        var errors = new List<string>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var rowNumber = index + 1;
            foreach (var field in GetStringFields(row))
            {
                if (!string.IsNullOrWhiteSpace(field.Value)
                    && DiagnosisValidator.ContainsUnsafeMaterial(field.Value))
                {
                    errors.Add($"row {rowNumber}: proposal evaluation field '{field.Name}' appears to contain raw content, credential, secret, token, or identity-bearing material.");
                }
            }
        }

        return errors;
    }

    private static IEnumerable<(string Name, string? Value)> GetStringFields(ProposalEvaluationRow row)
    {
        yield return ("proposal_id", row.ProposalId);
        yield return ("trace_id", row.TraceId);
        yield return ("task_id", row.TaskId);
        yield return ("task_category", row.TaskCategory);
        yield return ("client_kind", row.ClientKind);
        yield return ("comparison_id", row.ComparisonId);
        yield return ("experiment_id", row.ExperimentId);
        yield return ("experiment_condition", row.ExperimentCondition);
        yield return ("prompt_version", row.PromptVersion);
        yield return ("agent_variant", row.AgentVariant);
        yield return ("failure_category_id", row.FailureCategoryId);
        yield return ("anti_pattern_id", row.AntiPatternId);
        yield return ("severity", row.Severity);
        yield return ("improvement_target", row.ImprovementTarget);
        yield return ("proposal_title", row.ProposalTitle);
        yield return ("proposal_evaluation_status", row.ProposalEvaluationStatus);
        yield return ("evaluator_findings", row.EvaluatorFindings);
        yield return ("required_human_checks", row.RequiredHumanChecks);
        yield return ("evaluator_notes", row.EvaluatorNotes);
    }
}

internal static class ProposalEvaluationOutputWriter
{
    public static readonly string[] Columns =
    [
        "proposal_id",
        "source_diagnosis_index",
        "trace_id",
        "task_id",
        "task_category",
        "client_kind",
        "comparison_id",
        "experiment_id",
        "experiment_condition",
        "prompt_version",
        "agent_variant",
        "task_run_index",
        "failure_category_id",
        "anti_pattern_id",
        "severity",
        "improvement_target",
        "proposal_title",
        "proposal_evaluation_status",
        "evaluator_findings",
        "required_human_checks",
        "evaluator_notes",
    ];

    public static string WriteJson(IReadOnlyList<ProposalEvaluationRow> rows)
    {
        return JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    public static string WriteCsv(IReadOnlyList<ProposalEvaluationRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', Columns));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', Columns.Select(column => EscapeCsv(GetValue(row, column)))));
        }

        return builder.ToString();
    }

    private static string? GetValue(ProposalEvaluationRow row, string column)
    {
        return column switch
        {
            "proposal_id" => row.ProposalId,
            "source_diagnosis_index" => row.SourceDiagnosisIndex.ToString(CultureInfo.InvariantCulture),
            "trace_id" => row.TraceId,
            "task_id" => row.TaskId,
            "task_category" => row.TaskCategory,
            "client_kind" => row.ClientKind,
            "comparison_id" => row.ComparisonId,
            "experiment_id" => row.ExperimentId,
            "experiment_condition" => row.ExperimentCondition,
            "prompt_version" => row.PromptVersion,
            "agent_variant" => row.AgentVariant,
            "task_run_index" => row.TaskRunIndex?.ToString(CultureInfo.InvariantCulture),
            "failure_category_id" => row.FailureCategoryId,
            "anti_pattern_id" => row.AntiPatternId,
            "severity" => row.Severity,
            "improvement_target" => row.ImprovementTarget,
            "proposal_title" => row.ProposalTitle,
            "proposal_evaluation_status" => row.ProposalEvaluationStatus,
            "evaluator_findings" => row.EvaluatorFindings,
            "required_human_checks" => row.RequiredHumanChecks,
            "evaluator_notes" => row.EvaluatorNotes,
            _ => null,
        };
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Any(character => character is ',' or '"' or '\r' or '\n')
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }
}

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

        var header = ParseCsvLine(lines[0]);
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

            var values = ParseCsvLine(lines[lineIndex]);
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

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    builder.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (character == ',' && !inQuotes)
            {
                values.Add(builder.ToString());
                builder.Clear();
            }
            else
            {
                builder.Append(character);
            }
        }

        if (inQuotes)
        {
            throw new InvalidDataException("CSV row contains an unterminated quoted value.");
        }

        values.Add(builder.ToString());
        return values;
    }
}

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

        var header = ParseCsvLine(lines[0]);
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

            var values = ParseCsvLine(lines[lineIndex]);
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

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    builder.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (character == ',' && !inQuotes)
            {
                values.Add(builder.ToString());
                builder.Clear();
            }
            else
            {
                builder.Append(character);
            }
        }

        if (inQuotes)
        {
            throw new InvalidDataException("CSV row contains an unterminated quoted value.");
        }

        values.Add(builder.ToString());
        return values;
    }
}

internal static class HumanDecisionValidator
{
    private static readonly HashSet<string> ValidDecisions = new(StringComparer.Ordinal)
    {
        "approved",
        "rejected",
        "deferred",
    };

    public static IReadOnlyList<string> Validate(
        IReadOnlyList<HumanDecisionRow> decisions,
        IReadOnlyList<ProposalEvaluationRow> evaluations)
    {
        var errors = new List<string>();
        var evaluationsByProposalId = evaluations
            .ToDictionary(evaluation => evaluation.ProposalId, StringComparer.Ordinal);

        for (var index = 0; index < decisions.Count; index++)
        {
            var decision = decisions[index];
            var rowNumber = index + 1;

            if (string.IsNullOrWhiteSpace(decision.ProposalId))
            {
                errors.Add($"row {rowNumber}: proposal_id is required.");
            }

            if (string.IsNullOrWhiteSpace(decision.HumanDecision))
            {
                errors.Add($"row {rowNumber}: human_decision is required.");
            }
            else if (!ValidDecisions.Contains(decision.HumanDecision))
            {
                errors.Add($"row {rowNumber}: human_decision '{decision.HumanDecision}' is not allowed. Must be 'approved', 'rejected', or 'deferred'.");
            }

            if (string.IsNullOrWhiteSpace(decision.DecisionRationale))
            {
                errors.Add($"row {rowNumber}: decision_rationale is required.");
            }

            if (!string.IsNullOrWhiteSpace(decision.ProposalId))
            {
                if (!evaluationsByProposalId.TryGetValue(decision.ProposalId, out var evaluation))
                {
                    errors.Add($"row {rowNumber}: proposal_id '{decision.ProposalId}' not found in evaluations.");
                }
                else if (string.Equals(decision.HumanDecision, "approved", StringComparison.Ordinal)
                    && !string.Equals(evaluation.ProposalEvaluationStatus, "ready-for-human-approval", StringComparison.Ordinal))
                {
                    errors.Add($"row {rowNumber}: cannot approve proposal '{decision.ProposalId}' with evaluation status '{evaluation.ProposalEvaluationStatus}'. Only 'ready-for-human-approval' proposals can be approved.");
                }
            }
        }

        return errors;
    }
}

internal static class HumanDecisionSafetyValidator
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<HumanDecisionRow> rows)
    {
        var errors = new List<string>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var rowNumber = index + 1;
            foreach (var field in GetStringFields(row))
            {
                if (!string.IsNullOrWhiteSpace(field.Value)
                    && DiagnosisValidator.ContainsUnsafeMaterial(field.Value))
                {
                    errors.Add($"row {rowNumber}: decision field '{field.Name}' appears to contain raw content, credential, secret, token, or identity-bearing material.");
                }
            }
        }

        return errors;
    }

    private static IEnumerable<(string Name, string? Value)> GetStringFields(HumanDecisionRow row)
    {
        yield return ("proposal_id", row.ProposalId);
        yield return ("human_decision", row.HumanDecision);
        yield return ("decision_rationale", row.DecisionRationale);
        yield return ("approver_id", row.ApproverId);
        yield return ("approved_at", row.ApprovedAt);
        yield return ("conditions_or_notes", row.ConditionsOrNotes);
    }
}

internal static class HumanDecisionOutputWriter
{
    public static readonly string[] Columns =
    [
        "proposal_id",
        "human_decision",
        "decision_rationale",
        "approver_id",
        "approved_at",
        "conditions_or_notes",
    ];

    public static string WriteJson(IReadOnlyList<HumanDecisionRow> rows)
    {
        return JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    public static string WriteCsv(IReadOnlyList<HumanDecisionRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', Columns));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', Columns.Select(column => EscapeCsv(GetValue(row, column)))));
        }

        return builder.ToString();
    }

    private static string? GetValue(HumanDecisionRow row, string column)
    {
        return column switch
        {
            "proposal_id" => row.ProposalId,
            "human_decision" => row.HumanDecision,
            "decision_rationale" => row.DecisionRationale,
            "approver_id" => row.ApproverId,
            "approved_at" => row.ApprovedAt,
            "conditions_or_notes" => row.ConditionsOrNotes,
            _ => null,
        };
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Any(character => character is ',' or '"' or '\r' or '\n')
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }
}

internal static class DecisionTemplateGenerator
{
    public static IReadOnlyList<HumanDecisionRow> Generate(IReadOnlyList<ProposalEvaluationRow> evaluations)
    {
        var rows = new List<HumanDecisionRow>();
        foreach (var evaluation in evaluations)
        {
            if (!string.Equals(evaluation.ProposalEvaluationStatus, "ready-for-human-approval", StringComparison.Ordinal))
            {
                continue;
            }

            rows.Add(new HumanDecisionRow(
                ProposalId: evaluation.ProposalId,
                HumanDecision: string.Empty,
                DecisionRationale: string.Empty,
                ApproverId: null,
                ApprovedAt: null,
                ConditionsOrNotes: null));
        }

        return rows;
    }
}

internal static class ConfigSamples
{
    public const string DefaultOtlpEndpoint = LangfuseOtlpEndpoint;
    public const string LangfuseOtlpEndpoint = "http://localhost:3000/api/public/otel";
    public const string LangfuseOtlpTracesEndpoint = "http://localhost:3000/api/public/otel/v1/traces";
    public const string CollectorOtlpHttpEndpoint = "http://localhost:4318";
    public const string VsCodeClientKind = "vscode-copilot-chat";
    public const string CopilotCliClientKind = "copilot-cli";
    public const string DefaultExperimentId = "baseline";
    private const string LangfuseIngestionVersionHeader = "x-langfuse-ingestion-version=4";
    private const string LangfusePublicKeyPlaceholder = "<public-key>";
    private const string LangfuseSecretKeyPlaceholder = "<secret-key>";

    public static string CreateVsCodeSettingsJson()
    {
        var settings = new Dictionary<string, object>
        {
            ["github.copilot.chat.otel.enabled"] = true,
            ["github.copilot.chat.otel.exporterType"] = "otlp-http",
            ["github.copilot.chat.otel.otlpEndpoint"] = DefaultOtlpEndpoint,
            ["github.copilot.chat.otel.captureContent"] = true,
        };

        return JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string CreateLangfuseVsCodeSettingsJson()
    {
        var settings = new Dictionary<string, object>
        {
            ["github.copilot.chat.otel.enabled"] = true,
            ["github.copilot.chat.otel.exporterType"] = "otlp-http",
            ["github.copilot.chat.otel.otlpEndpoint"] = LangfuseOtlpEndpoint,
            ["github.copilot.chat.otel.captureContent"] = true,
        };

        return JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string CreateCollectorVsCodeSettingsJson()
    {
        var settings = new Dictionary<string, object>
        {
            ["github.copilot.chat.otel.enabled"] = true,
            ["github.copilot.chat.otel.exporterType"] = "otlp-http",
            ["github.copilot.chat.otel.otlpEndpoint"] = CollectorOtlpHttpEndpoint,
            ["github.copilot.chat.otel.captureContent"] = true,
        };

        return JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string CreateVsCodeFileSettingsJson(string outfile)
    {
        var settings = new Dictionary<string, object>
        {
            ["github.copilot.chat.otel.enabled"] = true,
            ["github.copilot.chat.otel.exporterType"] = "file",
            ["github.copilot.chat.otel.outfile"] = outfile,
            ["github.copilot.chat.otel.captureContent"] = true,
        };

        return JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string CreateVsCodePowerShellScript()
    {
        var resourceAttributes = CreateResourceAttributes(VsCodeClientKind);

        var builder = new StringBuilder();
        AppendLangfuseAuthPrelude(builder);
        builder.AppendLine("$env:COPILOT_OTEL_ENABLED=\"true\"");
        builder.AppendLine($"$env:COPILOT_OTEL_ENDPOINT=\"{DefaultOtlpEndpoint}\"");
        builder.AppendLine("$env:COPILOT_OTEL_CAPTURE_CONTENT=\"true\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_HEADERS=\"Authorization=Basic $auth,{LangfuseIngestionVersionHeader}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=\"{LangfuseOtlpTracesEndpoint}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_TRACES_HEADERS=\"Authorization=Basic $auth,{LangfuseIngestionVersionHeader}\"");
        builder.Append($"$env:OTEL_RESOURCE_ATTRIBUTES=\"{resourceAttributes}\"");
        return builder.ToString();
    }

    public static string CreateLangfuseVsCodePowerShellScript()
    {
        var resourceAttributes = CreateResourceAttributes(VsCodeClientKind);

        var builder = new StringBuilder();
        AppendLangfuseAuthPrelude(builder);
        builder.AppendLine("$env:COPILOT_OTEL_ENABLED=\"true\"");
        builder.AppendLine($"$env:COPILOT_OTEL_ENDPOINT=\"{LangfuseOtlpEndpoint}\"");
        builder.AppendLine("$env:COPILOT_OTEL_CAPTURE_CONTENT=\"true\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_HEADERS=\"Authorization=Basic $auth,{LangfuseIngestionVersionHeader}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=\"{LangfuseOtlpTracesEndpoint}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_TRACES_HEADERS=\"Authorization=Basic $auth,{LangfuseIngestionVersionHeader}\"");
        builder.Append($"$env:OTEL_RESOURCE_ATTRIBUTES=\"{resourceAttributes}\"");
        return builder.ToString();
    }

    public static string CreateCollectorVsCodePowerShellScript()
    {
        var resourceAttributes = CreateResourceAttributes(VsCodeClientKind);

        var builder = new StringBuilder();
        AppendCollectorCleanup(builder);
        builder.AppendLine("$env:COPILOT_OTEL_ENABLED=\"true\"");
        builder.AppendLine($"$env:COPILOT_OTEL_ENDPOINT=\"{CollectorOtlpHttpEndpoint}\"");
        builder.AppendLine("$env:COPILOT_OTEL_CAPTURE_CONTENT=\"true\"");
        builder.Append($"$env:OTEL_RESOURCE_ATTRIBUTES=\"{resourceAttributes}\"");
        return builder.ToString();
    }

    public static string CreateCopilotCliPowerShellScript()
    {
        var resourceAttributes = CreateResourceAttributes(CopilotCliClientKind);

        var builder = new StringBuilder();
        AppendLangfuseAuthPrelude(builder);
        builder.AppendLine("$env:COPILOT_OTEL_ENABLED=\"true\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"{DefaultOtlpEndpoint}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_HEADERS=\"Authorization=Basic $auth,{LangfuseIngestionVersionHeader}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=\"{LangfuseOtlpTracesEndpoint}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_TRACES_HEADERS=\"Authorization=Basic $auth,{LangfuseIngestionVersionHeader}\"");
        builder.AppendLine("$env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=\"true\"");
        builder.Append($"$env:OTEL_RESOURCE_ATTRIBUTES=\"{resourceAttributes}\"");
        return builder.ToString();
    }

    public static string CreateLangfuseCopilotCliPowerShellScript()
    {
        var resourceAttributes = CreateResourceAttributes(CopilotCliClientKind);

        var builder = new StringBuilder();
        AppendLangfuseAuthPrelude(builder);
        builder.AppendLine("$env:COPILOT_OTEL_ENABLED=\"true\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"{LangfuseOtlpEndpoint}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_HEADERS=\"Authorization=Basic $auth,{LangfuseIngestionVersionHeader}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=\"{LangfuseOtlpTracesEndpoint}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_TRACES_HEADERS=\"Authorization=Basic $auth,{LangfuseIngestionVersionHeader}\"");
        builder.AppendLine("$env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=\"true\"");
        builder.Append($"$env:OTEL_RESOURCE_ATTRIBUTES=\"{resourceAttributes}\"");
        return builder.ToString();
    }

    public static string CreateCollectorCopilotCliPowerShellScript()
    {
        var resourceAttributes = CreateResourceAttributes(CopilotCliClientKind);

        var builder = new StringBuilder();
        AppendCollectorCleanup(builder);
        builder.AppendLine("$env:COPILOT_OTEL_ENABLED=\"true\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"{CollectorOtlpHttpEndpoint}\"");
        builder.AppendLine("$env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=\"true\"");
        builder.Append($"$env:OTEL_RESOURCE_ATTRIBUTES=\"{resourceAttributes}\"");
        return builder.ToString();
    }

    private static void AppendLangfuseAuthPrelude(StringBuilder builder)
    {
        builder.AppendLine($"$publicKey = \"{LangfusePublicKeyPlaceholder}\"");
        builder.AppendLine($"$secretKey = \"{LangfuseSecretKeyPlaceholder}\"");
        builder.AppendLine("$auth = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(\"${publicKey}:${secretKey}\"))");
    }

    private static void AppendCollectorCleanup(StringBuilder builder)
    {
        builder.AppendLine("Remove-Item Env:OTEL_EXPORTER_OTLP_HEADERS -ErrorAction SilentlyContinue");
        builder.AppendLine("Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT -ErrorAction SilentlyContinue");
        builder.AppendLine("Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_HEADERS -ErrorAction SilentlyContinue");
    }

    private static string CreateResourceAttributes(string clientKind)
    {
        return string.Join(
            ',',
            "user.id=example-user",
            "user.email=user@example.com",
            "team.id=platform",
            "department=engineering",
            $"client.kind={clientKind}",
            $"experiment.id={DefaultExperimentId}");
    }
}

internal static class ResourceAttributeValidator
{
    private static readonly string[] RequiredKeys =
    [
        "user.id",
        "user.email",
        "team.id",
        "department",
        "client.kind",
        "experiment.id",
    ];

    private static readonly HashSet<string> RecommendedClientKinds = new(StringComparer.Ordinal)
    {
        ConfigSamples.VsCodeClientKind,
        ConfigSamples.CopilotCliClientKind,
    };

    public static ResourceAttributeValidationResult Validate(string rawValue)
    {
        var parseResult = Parse(rawValue);
        var errors = new List<string>(parseResult.Errors);
        var warnings = new List<string>();
        var attributes = parseResult.Attributes;

        foreach (var requiredKey in RequiredKeys)
        {
            if (!attributes.ContainsKey(requiredKey))
            {
                errors.Add($"missing required resource attribute '{requiredKey}'.");
            }
        }

        if (attributes.TryGetValue("client.kind", out var clientKind)
            && !RecommendedClientKinds.Contains(clientKind))
        {
            warnings.Add($"client.kind '{clientKind}' is not a recommended value. Use 'vscode-copilot-chat' or 'copilot-cli'.");
        }

        if (attributes.TryGetValue("experiment.id", out var experimentId)
            && !string.Equals(experimentId, ConfigSamples.DefaultExperimentId, StringComparison.Ordinal))
        {
            warnings.Add($"experiment.id '{experimentId}' is not the initial recommended value 'baseline'.");
        }

        return new ResourceAttributeValidationResult(errors, warnings);
    }

    private static ResourceAttributeParseResult Parse(string rawValue)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            errors.Add("OTEL_RESOURCE_ATTRIBUTES is empty.");
            return new ResourceAttributeParseResult(attributes, errors);
        }

        var elements = rawValue.Split(',');
        for (var index = 0; index < elements.Length; index++)
        {
            var element = elements[index].Trim();
            var displayIndex = index + 1;

            if (element.Length == 0)
            {
                errors.Add($"resource attribute element {displayIndex} is empty.");
                continue;
            }

            var separatorIndex = element.IndexOf('=');
            if (separatorIndex < 0)
            {
                errors.Add($"resource attribute element {displayIndex} is not in key=value form.");
                continue;
            }

            var key = element[..separatorIndex].Trim();
            var value = element[(separatorIndex + 1)..].Trim();
            if (key.Length == 0)
            {
                errors.Add($"resource attribute element {displayIndex} has an empty key.");
                continue;
            }

            attributes[key] = value;
        }

        return new ResourceAttributeParseResult(attributes, errors);
    }

    private sealed record ResourceAttributeParseResult(
        IReadOnlyDictionary<string, string> Attributes,
        IReadOnlyList<string> Errors);
}

internal sealed record ResourceAttributeValidationResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}
