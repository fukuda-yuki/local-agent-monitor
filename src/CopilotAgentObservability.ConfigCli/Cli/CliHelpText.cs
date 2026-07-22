namespace CopilotAgentObservability.ConfigCli;

internal static class CliHelpText
{
    public const string Text = """
        Usage:
          config-cli list-collection-profiles
          config-cli profile-vscode-env [--profile <collection-profile>] [--target <receiver|monitor>] [--endpoint <loopback-http-url>]
          config-cli profile-copilot-cli-env [--profile <collection-profile>]
          config-cli profile-codex-app-config [--profile <collection-profile>]
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
          config-cli langfuse-codex-app-config
          config-cli collector-codex-app-config
          config-cli validate-resource-attributes <OTEL_RESOURCE_ATTRIBUTES>
          config-cli setup plan --adapter github-copilot --target <vscode|cli|app-sdk|all> [--endpoint <loopback-http-url>] [--include-content-capture]
          config-cli setup plan --adapter claude-code --target <cli|app-sdk|all> [--endpoint <loopback-http-url>] [--include-content-capture] [--allow-wsl2-routing]
          config-cli setup apply --change-set <uuid-v7>
          config-cli setup rollback --change-set <uuid-v7>
          config-cli setup status [--adapter <id>]
          config-cli doctor evaluate --input <file> [--json]
          config-cli doctor verification start --database <file> --source-surface <value> [--source-adapter <value>] --expires-at <RFC3339> [--json]
          config-cli doctor verification status --database <file> --verification-id <uuid-v7> [--json]
          config-cli doctor verification complete --database <file> --verification-id <uuid-v7> --expected-revision <positive-int> --input <file> [--json]
          config-cli doctor verification cancel --database <file> --verification-id <uuid-v7> --expected-revision <positive-int> [--json]
          config-cli first-trace begin --database <file> --adapter <github-copilot-vscode|github-copilot-cli|github-copilot-app-sdk|claude-code> [--interaction <value>] [--endpoint <loopback-http-url>] [--expires-at <RFC3339>] [--json]
          config-cli first-trace status --database <file> --verification-id <uuid-v7> [--endpoint <loopback-http-url>] [--json]
          config-cli first-trace complete --database <file> --verification-id <uuid-v7> --expected-revision <positive-int> [--endpoint <loopback-http-url>] [--evidence <opaque-ref>]... [--json]
          config-cli first-trace cancel --database <file> --verification-id <uuid-v7> --expected-revision <positive-int> [--json]
          config-cli sanitized-export preview --database <monitor.db> --request <request.json>
          config-cli sanitized-export export --database <monitor.db> --request <request.json> --output <bundle.zip>
          config-cli sanitized-export result --bundle <bundle.zip>
          config-cli historical-import preview --database <monitor.db> --request <request.json>
          config-cli historical-import confirm --database <monitor.db> --request <request.json>
          config-cli historical-import commit --database <monitor.db> --request <request.json>
          config-cli historical-import status --database <monitor.db> --operation-id <hop_...>
          config-cli historical-import result --database <monitor.db> --operation-id <hop_...>
          config-cli historical-import history --database <monitor.db> [--limit <1..100>]
          config-cli historical-import observations --database <monitor.db> [--limit <1..100>] [--cursor <hoc_...>]
          config-cli raw-replay preview --database <monitor.db> --request <request.json>
          config-cli raw-replay export --database <monitor.db> --request <request.json> --output <raw-local-replay.zip>
          config-cli raw-replay result --bundle <raw-local-replay.zip>
          config-cli sanitized-import preview --database <monitor.db> --bundle <bundle.zip>
          config-cli sanitized-import import --database <monitor.db> --bundle <bundle.zip> --preview-digest <sha256>
          config-cli sanitized-import history --database <monitor.db> [--limit <1..100>]
          config-cli ingest-raw <raw.json> --db <raw-store.db>
          config-cli normalize-raw <raw-store.db|raw.json> [--csv <output.csv>] [--json <output.json>]
          config-cli serve-raw-local-receiver [--db <raw-store.db>] [--url <loopback-http-url>]
          config-cli aggregate-measurements <input.json> [--csv <output.csv>] [--json <output.json>]
          config-cli generate-diagnosis-candidates <measurements.csv|measurements.json> [--raw <raw-store.db|raw-otlp.json>] [--include-sensitive-content --retention-database <local-monitor.db> [--sensitive-output-dir <dir>]] [--csv <output.csv>] [--json <output.json>]
          config-cli generate-improvement-candidates <diagnosis-candidates.csv|diagnosis-candidates.json> [--csv <output.csv>] [--json <output.json>]
          config-cli generate-auto-decisions <improvement-candidates.csv|improvement-candidates.json> [--csv <output.csv>] [--json <output.json>]
          config-cli generate-dashboard-dataset <measurements.csv|measurements.json> [--raw <raw-store.db|raw-otlp.json>] [--diagnosis-candidates <input.csv|input.json>] [--improvement-candidates <input.csv|input.json>] [--auto-decisions <input.csv|input.json>] [--time-bucket <day|hour|week>] [--csv-dir <output-dir>] [--json <output.json>]
          config-cli generate-static-dashboard <dashboard-dataset.json> --out-dir <output-dir> [--snapshot-date <YYYY-MM-DD>] [--title <title>]
          config-cli adapt-diagnosis-candidates <diagnosis-candidates.csv|diagnosis-candidates.json> <measurements.csv|measurements.json> [--csv <output.csv>] [--json <output.json>]
          config-cli validate-diagnoses <input.csv|input.json> [--csv <output.csv>] [--json <output.json>]
          config-cli generate-improvement-proposals <diagnoses.csv|diagnoses.json> [--csv <output.csv>] [--json <output.json>]
          config-cli evaluate-improvement-proposals <proposals.csv|proposals.json> [--csv <output.csv>] [--json <output.json>]
          config-cli record-human-decisions <evaluations.csv|evaluations.json> <decisions.csv|decisions.json> [--csv <output.csv>] [--json <output.json>]
          config-cli generate-decision-template <evaluations.csv|evaluations.json> [--csv <output.csv>] [--json <output.json>]
        """;
}
