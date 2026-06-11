namespace CopilotAgentObservability.ConfigCli;

internal static class CliApplication
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.WriteLine(CliHelpText.Text);
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

            case "normalize-raw":
                return RunNormalizeRaw(args, output, error);

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
                error.WriteLine(CliHelpText.Text);
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
        catch (InvalidDataException exception)
        {
            error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        catch (SqliteException exception)
        {
            error.WriteLine($"error: failed to write raw store: {exception.Message}");
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

    private static int RunNormalizeRaw(string[] args, TextWriter output, TextWriter error)
    {
        var parseResult = RawNormalizationOptions.Parse(args);
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

            var rows = RawNormalizationInputReader.Read(parseResult.Options.InputPath);

            if (parseResult.Options.CsvOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.CsvOutputPath, MeasurementOutputWriter.WriteCsv(rows), Encoding.UTF8);
            }

            if (parseResult.Options.JsonOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.JsonOutputPath, MeasurementOutputWriter.WriteJson(rows), Encoding.UTF8);
            }

            output.WriteLine($"Normalized {rows.Count} raw measurement row(s).");
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
        catch (SqliteException exception)
        {
            error.WriteLine($"error: failed to read raw store: {exception.Message}");
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
            var safetyErrors = ProposalEvaluationSafetyValidator.Validate(evaluations);
            if (safetyErrors.Count > 0)
            {
                error.WriteLine("error: input contains unsafe content:");
                foreach (var validationError in safetyErrors)
                {
                    error.WriteLine($"  - {validationError}");
                }

                return 1;
            }

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
}
