namespace CopilotAgentObservability.ConfigCli;

internal static class CliHelpText
{
    public const string Text = """
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
          config-cli normalize-raw <raw-store.db|raw.json> [--csv <output.csv>] [--json <output.json>]
          config-cli aggregate-measurements <input.json> [--csv <output.csv>] [--json <output.json>]
          config-cli validate-diagnoses <input.csv|input.json> [--csv <output.csv>] [--json <output.json>]
          config-cli generate-improvement-proposals <diagnoses.csv|diagnoses.json> [--csv <output.csv>] [--json <output.json>]
          config-cli evaluate-improvement-proposals <proposals.csv|proposals.json> [--csv <output.csv>] [--json <output.json>]
          config-cli record-human-decisions <evaluations.csv|evaluations.json> <decisions.csv|decisions.json> [--csv <output.csv>] [--json <output.json>]
          config-cli generate-decision-template <evaluations.csv|evaluations.json> [--csv <output.csv>] [--json <output.json>]
        """;
}
