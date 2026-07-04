using System.Net;

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
            case "list-collection-profiles":
                output.WriteLine(string.Join(Environment.NewLine, CollectionProfileOptions.SupportedValues));
                return 0;

            case "profile-vscode-env":
                return RunProfileVsCodeEnvCommand(args, output, error);

            case "profile-copilot-cli-env":
                return RunProfileCommand(args, output, error, ConfigSamples.CreateProfileCopilotCliPowerShellScript);

            case "profile-codex-app-config":
                return RunProfileCommand(args, output, error, ConfigSamples.CreateProfileCodexAppConfigToml);

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

            case "langfuse-codex-app-config":
                output.WriteLine(ConfigSamples.CreateLangfuseCodexAppConfigToml());
                return 0;

            case "collector-codex-app-config":
                output.WriteLine(ConfigSamples.CreateCollectorCodexAppConfigToml());
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

            case "serve-raw-local-receiver":
                return RunServeRawLocalReceiver(args, output, error);

            case "validate-diagnoses":
                return RunValidateDiagnoses(args, output, error);

            case "generate-diagnosis-candidates":
                return RunGenerateDiagnosisCandidates(args, output, error);

            case "generate-improvement-candidates":
                return RunGenerateImprovementCandidates(args, output, error);

            case "generate-auto-decisions":
                return RunGenerateAutoDecisions(args, output, error);

            case "adapt-diagnosis-candidates":
                return RunAdaptDiagnosisCandidates(args, output, error);

            case "generate-dashboard-dataset":
                return RunGenerateDashboardDataset(args, output, error);

            case "generate-static-dashboard":
                return RunGenerateStaticDashboard(args, output, error);

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

    private static int RunProfileCommand(string[] args, TextWriter output, TextWriter error, Func<string, string> createOutput)
    {
        var parseResult = CollectionProfileOptions.Parse(args);
        if (parseResult.Error is not null)
        {
            error.WriteLine($"error: {parseResult.Error}");
            return 1;
        }

        output.WriteLine(createOutput(parseResult.Options!.Profile));
        return 0;
    }

    private static int RunProfileVsCodeEnvCommand(string[] args, TextWriter output, TextWriter error)
    {
        var parseResult = ProfileVsCodeEnvOptions.Parse(args);
        if (parseResult.Error is not null)
        {
            error.WriteLine($"error: {parseResult.Error}");
            return 1;
        }

        var options = parseResult.Options!;
        output.WriteLine(ConfigSamples.CreateProfileVsCodePowerShellScript(options.Profile, options.RawLocalReceiverEndpoint));
        return 0;
    }

    private static int RunServeRawLocalReceiver(string[] args, TextWriter output, TextWriter error)
    {
        var parseResult = RawLocalReceiverOptions.Parse(args);
        if (parseResult.Error is not null)
        {
            error.WriteLine($"error: {parseResult.Error}");
            return 1;
        }

        try
        {
            return RawLocalReceiverHost.Run(parseResult.Options!, output);
        }
        catch (HttpListenerException exception)
        {
            error.WriteLine($"error: failed to start raw local receiver: {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            error.WriteLine($"error: failed to run raw local receiver: {exception.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            error.WriteLine($"error: failed to access raw local receiver resource: {exception.Message}");
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

    private static int RunGenerateDiagnosisCandidates(string[] args, TextWriter output, TextWriter error)
    {
        var parseResult = DiagnosisCandidateOptions.Parse(args);
        if (parseResult.Error is not null)
        {
            error.WriteLine($"error: {parseResult.Error}");
            return 1;
        }

        try
        {
            if (!File.Exists(parseResult.Options!.MeasurementsPath))
            {
                error.WriteLine($"error: measurements file not found: {parseResult.Options.MeasurementsPath}");
                return 1;
            }

            if (parseResult.Options.RawInputPath is not null && !File.Exists(parseResult.Options.RawInputPath))
            {
                error.WriteLine($"error: raw input file not found: {parseResult.Options.RawInputPath}");
                return 1;
            }

            var measurements = DiagnosisCandidateMeasurementReader.Read(parseResult.Options.MeasurementsPath);
            var rawEvidence = parseResult.Options.RawInputPath is null
                ? null
                : RawEvidenceReader.Read(parseResult.Options.RawInputPath);
            var candidates = DiagnosisCandidateGenerator.Generate(
                measurements,
                rawEvidence,
                parseResult.Options.IncludeSensitiveContent,
                parseResult.Options.SensitiveOutputDir,
                DateTimeOffset.UtcNow);

            if (parseResult.Options.CsvOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.CsvOutputPath, DiagnosisCandidateOutputWriter.WriteCsv(candidates), Encoding.UTF8);
            }

            if (parseResult.Options.JsonOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.JsonOutputPath, DiagnosisCandidateOutputWriter.WriteJson(candidates), Encoding.UTF8);
            }

            output.WriteLine($"Generated {candidates.Count} diagnosis candidate record(s).");
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

    private static int RunGenerateDashboardDataset(string[] args, TextWriter output, TextWriter error)
    {
        var parseResult = DashboardDatasetOptions.Parse(args);
        if (parseResult.Error is not null)
        {
            error.WriteLine($"error: {parseResult.Error}");
            return 1;
        }

        try
        {
            var options = parseResult.Options!;
            if (!File.Exists(options.MeasurementsPath))
            {
                error.WriteLine($"error: measurements file not found: {options.MeasurementsPath}");
                return 1;
            }

            if (options.RawInputPath is not null && !File.Exists(options.RawInputPath))
            {
                error.WriteLine($"error: raw input file not found: {options.RawInputPath}");
                return 1;
            }

            if (options.DiagnosisCandidatesPath is not null && !File.Exists(options.DiagnosisCandidatesPath))
            {
                error.WriteLine($"error: diagnosis candidates file not found: {options.DiagnosisCandidatesPath}");
                return 1;
            }

            if (options.ImprovementCandidatesPath is not null && !File.Exists(options.ImprovementCandidatesPath))
            {
                error.WriteLine($"error: improvement candidates file not found: {options.ImprovementCandidatesPath}");
                return 1;
            }

            if (options.AutoDecisionsPath is not null && !File.Exists(options.AutoDecisionsPath))
            {
                error.WriteLine($"error: auto decisions file not found: {options.AutoDecisionsPath}");
                return 1;
            }

            var measurements = DiagnosisCandidateMeasurementReader.Read(options.MeasurementsPath);
            var rawOperations = options.RawInputPath is null
                ? []
                : DashboardRawOperationReader.Read(options.RawInputPath);
            var diagnosisCandidates = options.DiagnosisCandidatesPath is null
                ? []
                : DiagnosisCandidateInputReader.Read(options.DiagnosisCandidatesPath);
            var improvementCandidates = options.ImprovementCandidatesPath is null
                ? []
                : ImprovementCandidateInputReader.Read(options.ImprovementCandidatesPath);
            var autoDecisions = options.AutoDecisionsPath is null
                ? []
                : AutoDecisionInputReader.Read(options.AutoDecisionsPath);
            var dataset = DashboardDatasetGenerator.Generate(
                measurements,
                rawOperations,
                diagnosisCandidates,
                improvementCandidates,
                autoDecisions,
                options.TimeBucketGranularity,
                DateTimeOffset.UtcNow);

            if (options.CsvOutputDirectory is not null)
            {
                DashboardDatasetOutputWriter.WriteCsvDirectory(dataset, options.CsvOutputDirectory);
            }

            if (options.JsonOutputPath is not null)
            {
                File.WriteAllText(options.JsonOutputPath, DashboardDatasetOutputWriter.WriteJson(dataset), Encoding.UTF8);
            }

            output.WriteLine($"Generated dashboard dataset with {dataset.RunSummary.Count} run row(s), {dataset.OperationSummary.Count} operation row(s), {dataset.CandidateSummary.Count} candidate row(s), and {dataset.CollectionHealth.Count} collection health row(s).");
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

    private static int RunGenerateStaticDashboard(string[] args, TextWriter output, TextWriter error)
    {
        var parseResult = StaticDashboardOptions.Parse(args, DateTimeOffset.UtcNow);
        if (parseResult.Error is not null)
        {
            error.WriteLine($"error: {parseResult.Error}");
            return 1;
        }

        try
        {
            var options = parseResult.Options!;
            if (!File.Exists(options.DatasetPath))
            {
                error.WriteLine($"error: dashboard dataset file not found: {options.DatasetPath}");
                return 1;
            }

            var artifact = StaticDashboardGenerator.Generate(
                File.ReadAllText(options.DatasetPath),
                options.Title,
                options.SnapshotDate);
            Directory.CreateDirectory(options.OutputDirectory);
            File.WriteAllText(Path.Combine(options.OutputDirectory, "index.html"), artifact.Html, Encoding.UTF8);
            File.WriteAllText(Path.Combine(options.OutputDirectory, "dashboard-data.json"), artifact.DatasetJson, Encoding.UTF8);

            output.WriteLine($"Generated static dashboard in {options.OutputDirectory}.");
            return 0;
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

    private static int RunAdaptDiagnosisCandidates(string[] args, TextWriter output, TextWriter error)
    {
        var parseResult = DiagnosisCandidateAdapterOptions.Parse(args);
        if (parseResult.Error is not null)
        {
            error.WriteLine($"error: {parseResult.Error}");
            return 1;
        }

        try
        {
            if (!File.Exists(parseResult.Options!.CandidatesPath))
            {
                error.WriteLine($"error: diagnosis candidates file not found: {parseResult.Options.CandidatesPath}");
                return 1;
            }

            if (!File.Exists(parseResult.Options.MeasurementsPath))
            {
                error.WriteLine($"error: measurements file not found: {parseResult.Options.MeasurementsPath}");
                return 1;
            }

            var candidates = DiagnosisCandidateInputReader.Read(parseResult.Options.CandidatesPath);
            var measurements = DiagnosisCandidateMeasurementReader.Read(parseResult.Options.MeasurementsPath);
            var diagnoses = DiagnosisCandidateAdapter.Adapt(candidates, measurements);
            var validationErrors = DiagnosisValidator.Validate(diagnoses);
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
                File.WriteAllText(parseResult.Options.CsvOutputPath, DiagnosisOutputWriter.WriteCsv(diagnoses), Encoding.UTF8);
            }

            if (parseResult.Options.JsonOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.JsonOutputPath, DiagnosisOutputWriter.WriteJson(diagnoses), Encoding.UTF8);
            }

            output.WriteLine($"Adapted {diagnoses.Count} diagnosis candidate record(s).");
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

    private static int RunGenerateImprovementCandidates(string[] args, TextWriter output, TextWriter error)
    {
        var parseResult = ImprovementCandidateGenerationOptions.Parse(args);
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

            var diagnoses = DiagnosisCandidateInputReader.Read(parseResult.Options.InputPath);
            var candidates = ImprovementCandidateGenerator.Generate(diagnoses);

            if (parseResult.Options.CsvOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.CsvOutputPath, ImprovementCandidateOutputWriter.WriteCsv(candidates), Encoding.UTF8);
            }

            if (parseResult.Options.JsonOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.JsonOutputPath, ImprovementCandidateOutputWriter.WriteJson(candidates), Encoding.UTF8);
            }

            output.WriteLine($"Generated {candidates.Count} improvement candidate record(s).");
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

    private static int RunGenerateAutoDecisions(string[] args, TextWriter output, TextWriter error)
    {
        var parseResult = AutoDecisionGenerationOptions.Parse(args);
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

            var candidates = ImprovementCandidateInputReader.Read(parseResult.Options.InputPath);
            var decisions = AutoDecisionGenerator.Generate(candidates);

            if (parseResult.Options.CsvOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.CsvOutputPath, AutoDecisionOutputWriter.WriteCsv(decisions), Encoding.UTF8);
            }

            if (parseResult.Options.JsonOutputPath is not null)
            {
                File.WriteAllText(parseResult.Options.JsonOutputPath, AutoDecisionOutputWriter.WriteJson(decisions), Encoding.UTF8);
            }

            output.WriteLine($"Generated {decisions.Count} auto-decision record(s).");
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
