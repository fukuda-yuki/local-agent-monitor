using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Pricing;

internal sealed record PricingOwnedObject(
    string Type,
    string Name,
    string TableName,
    string Sql);

internal static class PricingSchemaV1
{
    internal const string Component = "pricing";
    internal const int Version = 1;

    private static readonly IReadOnlyList<(string Name, string Sql)> Tables =
    [
        ("pricing_catalog_snapshots", """
            CREATE TABLE pricing_catalog_snapshots(
                catalog_sha256 TEXT NOT NULL PRIMARY KEY CHECK(length(catalog_sha256)=64 AND catalog_sha256=lower(catalog_sha256) AND catalog_sha256 NOT GLOB '*[^0-9a-f]*'),
                schema_version TEXT NOT NULL CHECK(schema_version='pricing.catalog-snapshot.v1'),
                canonical_blob BLOB NOT NULL CHECK(typeof(canonical_blob)='blob' AND length(canonical_blob) BETWEEN 1 AND 4194304),
                document_count INTEGER NOT NULL CHECK(typeof(document_count)='integer' AND document_count BETWEEN 1 AND 64),
                first_recorded_at_utc TEXT NOT NULL CHECK(typeof(first_recorded_at_utc)='text' AND length(first_recorded_at_utc)=33 AND first_recorded_at_utc GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00')
            );
            """),
        ("pricing_configuration_previews", """
            CREATE TABLE pricing_configuration_previews(
                preview_digest TEXT NOT NULL PRIMARY KEY CHECK(length(preview_digest)=64 AND preview_digest=lower(preview_digest) AND preview_digest NOT GLOB '*[^0-9a-f]*'),
                canonical_sha256 TEXT NOT NULL CHECK(length(canonical_sha256)=64 AND canonical_sha256=lower(canonical_sha256) AND canonical_sha256 NOT GLOB '*[^0-9a-f]*'),
                canonical_blob BLOB NOT NULL CHECK(typeof(canonical_blob)='blob' AND length(canonical_blob) BETWEEN 1 AND 1048576),
                configuration_id TEXT NOT NULL CHECK(typeof(configuration_id)='text' AND length(configuration_id)=83 AND substr(configuration_id,1,19)='cost-configuration-' AND substr(configuration_id,20) NOT GLOB '*[^0-9a-f]*'),
                expected_head_revision INTEGER NOT NULL CHECK(typeof(expected_head_revision)='integer' AND expected_head_revision>=0),
                expected_configuration_id TEXT NULL CHECK(expected_configuration_id IS NULL OR (typeof(expected_configuration_id)='text' AND length(expected_configuration_id)=83 AND substr(expected_configuration_id,1,19)='cost-configuration-' AND substr(expected_configuration_id,20) NOT GLOB '*[^0-9a-f]*')),
                catalog_sha256 TEXT NOT NULL CHECK(length(catalog_sha256)=64 AND catalog_sha256=lower(catalog_sha256) AND catalog_sha256 NOT GLOB '*[^0-9a-f]*'),
                selection_digest TEXT NOT NULL CHECK(length(selection_digest)=64 AND selection_digest=lower(selection_digest) AND selection_digest NOT GLOB '*[^0-9a-f]*'),
                created_at_utc TEXT NOT NULL CHECK(length(created_at_utc)=33 AND created_at_utc GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'),
                expires_at_utc TEXT NOT NULL CHECK(length(expires_at_utc)=33 AND expires_at_utc GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'),
                CHECK((expected_head_revision=0 AND expected_configuration_id IS NULL) OR (expected_head_revision>0 AND expected_configuration_id IS NOT NULL))
            );
            """),
        ("pricing_configurations", """
            CREATE TABLE pricing_configurations(
                configuration_id TEXT NOT NULL PRIMARY KEY CHECK(typeof(configuration_id)='text' AND length(configuration_id)=83 AND substr(configuration_id,1,19)='cost-configuration-' AND substr(configuration_id,20) NOT GLOB '*[^0-9a-f]*'),
                predecessor_configuration_id TEXT NULL UNIQUE CHECK(predecessor_configuration_id IS NULL OR (typeof(predecessor_configuration_id)='text' AND length(predecessor_configuration_id)=83 AND substr(predecessor_configuration_id,1,19)='cost-configuration-' AND substr(predecessor_configuration_id,20) NOT GLOB '*[^0-9a-f]*')),
                schema_version TEXT NOT NULL CHECK(schema_version='cost.configuration.v1'),
                catalog_sha256 TEXT NOT NULL,
                canonical_sha256 TEXT NOT NULL CHECK(length(canonical_sha256)=64 AND canonical_sha256=lower(canonical_sha256) AND canonical_sha256 NOT GLOB '*[^0-9a-f]*'),
                canonical_blob BLOB NOT NULL CHECK(typeof(canonical_blob)='blob' AND length(canonical_blob) BETWEEN 1 AND 1048576),
                created_at_utc TEXT NOT NULL CHECK(length(created_at_utc)=33 AND created_at_utc GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'),
                source_count INTEGER NOT NULL CHECK(source_count BETWEEN 0 AND 32),
                budget_count INTEGER NOT NULL CHECK(budget_count BETWEEN 0 AND 3),
                UNIQUE(configuration_id,catalog_sha256),
                FOREIGN KEY(catalog_sha256) REFERENCES pricing_catalog_snapshots(catalog_sha256) ON DELETE RESTRICT,
                FOREIGN KEY(predecessor_configuration_id) REFERENCES pricing_configurations(configuration_id) ON DELETE RESTRICT
            );
            """),
        ("pricing_configuration_heads", """
            CREATE TABLE pricing_configuration_heads(
                head_revision INTEGER NOT NULL PRIMARY KEY CHECK(head_revision>0),
                configuration_id TEXT NOT NULL UNIQUE,
                previous_head_revision INTEGER NULL UNIQUE,
                previous_configuration_id TEXT NULL UNIQUE,
                committed_at_utc TEXT NOT NULL CHECK(length(committed_at_utc)=33 AND committed_at_utc GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'),
                UNIQUE(head_revision,configuration_id),
                CHECK((head_revision=1 AND previous_head_revision IS NULL AND previous_configuration_id IS NULL) OR (head_revision>1 AND previous_head_revision=head_revision-1 AND previous_configuration_id IS NOT NULL)),
                FOREIGN KEY(configuration_id) REFERENCES pricing_configurations(configuration_id) ON DELETE RESTRICT,
                FOREIGN KEY(previous_head_revision,previous_configuration_id) REFERENCES pricing_configuration_heads(head_revision,configuration_id) ON DELETE RESTRICT
            );
            """),
        ("pricing_configuration_commits", """
            CREATE TABLE pricing_configuration_commits(
                head_revision INTEGER NOT NULL PRIMARY KEY,
                configuration_id TEXT NOT NULL UNIQUE,
                preview_digest TEXT NOT NULL UNIQUE CHECK(length(preview_digest)=64 AND preview_digest=lower(preview_digest) AND preview_digest NOT GLOB '*[^0-9a-f]*'),
                request_sha256 TEXT NOT NULL CHECK(length(request_sha256)=64 AND request_sha256=lower(request_sha256) AND request_sha256 NOT GLOB '*[^0-9a-f]*'),
                canonical_request_blob BLOB NOT NULL CHECK(typeof(canonical_request_blob)='blob' AND length(canonical_request_blob) BETWEEN 1 AND 1048576),
                canonical_result_blob BLOB NOT NULL CHECK(typeof(canonical_result_blob)='blob' AND length(canonical_result_blob) BETWEEN 1 AND 65536),
                UNIQUE(head_revision,configuration_id),
                FOREIGN KEY(head_revision,configuration_id) REFERENCES pricing_configuration_heads(head_revision,configuration_id) ON DELETE RESTRICT
            );
            """),
        ("pricing_recalculation_runs", """
            CREATE TABLE pricing_recalculation_runs(
                run_id TEXT NOT NULL PRIMARY KEY CHECK(typeof(run_id)='text' AND length(run_id)=36 AND run_id=lower(run_id) AND length(replace(run_id,'-',''))=32 AND replace(run_id,'-','') NOT GLOB '*[^0-9a-f]*' AND substr(run_id,9,1)='-' AND substr(run_id,14,1)='-' AND substr(run_id,15,1)='7' AND substr(run_id,19,1)='-' AND substr(run_id,20,1) GLOB '[89ab]' AND substr(run_id,24,1)='-'),
                request_schema_version TEXT NOT NULL CHECK(request_schema_version='cost.recalculation-request.v1'),
                idempotency_key TEXT NOT NULL UNIQUE CHECK(typeof(idempotency_key)='text' AND length(idempotency_key) BETWEEN 16 AND 128 AND substr(idempotency_key,1,1) GLOB '[A-Za-z0-9]' AND idempotency_key NOT GLOB '*[^A-Za-z0-9._-]*'),
                request_digest TEXT NOT NULL CHECK(length(request_digest)=64 AND request_digest=lower(request_digest) AND request_digest NOT GLOB '*[^0-9a-f]*'),
                canonical_request_blob BLOB NOT NULL CHECK(typeof(canonical_request_blob)='blob' AND length(canonical_request_blob) BETWEEN 1 AND 1048576),
                configuration_id TEXT NOT NULL,
                configuration_head_revision INTEGER NOT NULL CHECK(configuration_head_revision>0),
                catalog_sha256 TEXT NOT NULL,
                calculation_time_utc TEXT NOT NULL CHECK(length(calculation_time_utc)=33 AND calculation_time_utc GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'),
                target_count INTEGER NOT NULL CHECK(target_count BETWEEN 1 AND 100),
                scope_count INTEGER NOT NULL CHECK(scope_count BETWEEN 0 AND 8),
                created_at_utc TEXT NOT NULL CHECK(created_at_utc=calculation_time_utc),
                FOREIGN KEY(configuration_head_revision,configuration_id) REFERENCES pricing_configuration_heads(head_revision,configuration_id) ON DELETE RESTRICT,
                FOREIGN KEY(configuration_id,catalog_sha256) REFERENCES pricing_configurations(configuration_id,catalog_sha256) ON DELETE RESTRICT
            );
            """),
        ("pricing_recalculation_targets", """
            CREATE TABLE pricing_recalculation_targets(
                run_id TEXT NOT NULL,
                target_ordinal INTEGER NOT NULL CHECK(target_ordinal BETWEEN 0 AND 99),
                session_id TEXT NOT NULL CHECK(length(session_id)=36 AND session_id=lower(session_id) AND session_id NOT GLOB '*[^0-9a-f-]*' AND substr(session_id,9,1)='-' AND substr(session_id,14,1)='-' AND substr(session_id,19,1)='-' AND substr(session_id,24,1)='-'),
                session_status TEXT NOT NULL CHECK(session_status IN ('completed','failed')),
                session_effective_at_utc TEXT NOT NULL CHECK(typeof(session_effective_at_utc)='text' AND length(session_effective_at_utc)=33 AND session_effective_at_utc GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'),
                session_updated_at_utc TEXT NOT NULL CHECK(typeof(session_updated_at_utc)='text' AND length(session_updated_at_utc)=33 AND session_updated_at_utc GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'),
                source_partition_state TEXT NOT NULL CHECK(source_partition_state IN ('resolved','missing','incomplete','mixed')),
                source_partition_count INTEGER NOT NULL CHECK(source_partition_count BETWEEN 0 AND 257),
                source_partition_digest TEXT NOT NULL CHECK(length(source_partition_digest)=64 AND source_partition_digest=lower(source_partition_digest) AND source_partition_digest NOT GLOB '*[^0-9a-f]*'),
                source_surface TEXT NULL CHECK(source_surface IS NULL OR (length(source_surface) BETWEEN 1 AND 128 AND source_surface=lower(source_surface) AND source_surface NOT GLOB '*[^a-z0-9._-]*' AND substr(source_surface,1,1) GLOB '[a-z0-9]')),
                source_application_version TEXT NULL CHECK(source_application_version IS NULL OR (length(source_application_version) BETWEEN 1 AND 64 AND source_application_version NOT GLOB '*[^!-~]*')),
                base_head_revision INTEGER NULL,
                base_estimate_id TEXT NULL CHECK(base_estimate_id IS NULL OR (length(base_estimate_id)=81 AND base_estimate_id GLOB 'pricing-estimate-*' AND substr(base_estimate_id,18) NOT GLOB '*[^0-9a-f]*')),
                base_attempt_revision INTEGER NOT NULL CHECK(base_attempt_revision>=0),
                PRIMARY KEY(run_id,target_ordinal),
                UNIQUE(run_id,session_id),
                CHECK((source_partition_state='resolved' AND source_partition_count BETWEEN 1 AND 256 AND source_surface IS NOT NULL AND source_application_version IS NOT NULL) OR (source_partition_state<>'resolved' AND source_surface IS NULL AND source_application_version IS NULL)),
                CHECK((base_head_revision IS NULL AND base_estimate_id IS NULL) OR (base_head_revision IS NOT NULL AND base_estimate_id IS NOT NULL)),
                FOREIGN KEY(run_id) REFERENCES pricing_recalculation_runs(run_id) ON DELETE RESTRICT,
                FOREIGN KEY(session_id) REFERENCES sessions(session_id) ON DELETE RESTRICT,
                FOREIGN KEY(session_id,base_head_revision,base_estimate_id) REFERENCES pricing_estimate_heads(session_id,head_revision,estimate_id) ON DELETE RESTRICT
            );
            """),
        ("pricing_recalculation_events", """
            CREATE TABLE pricing_recalculation_events(
                run_id TEXT NOT NULL,
                event_sequence INTEGER NOT NULL CHECK(event_sequence BETWEEN 0 AND 2),
                event_kind TEXT NOT NULL CHECK(event_kind IN ('requested','running','succeeded','failed')),
                occurred_at_utc TEXT NOT NULL CHECK(typeof(occurred_at_utc)='text' AND length(occurred_at_utc)=33 AND occurred_at_utc GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'),
                failure_phase TEXT NULL CHECK(failure_phase IS NULL OR failure_phase IN ('head_input','adapter','estimate_validation','budget_payload','pricing_store','alert_evaluation','alert_store','recovery')),
                failure_ordinal_kind TEXT NULL CHECK(failure_ordinal_kind IS NULL OR failure_ordinal_kind IN ('target','scope')),
                failure_ordinal INTEGER NULL CHECK(failure_ordinal IS NULL OR failure_ordinal BETWEEN 0 AND 99),
                failure_code TEXT NULL CHECK(failure_code IS NULL OR failure_code IN ('stale_recalculation_input','stale_active_estimate','source_adapter_failed','invalid_estimate_source','pricing_estimation_failed','budget_payload_too_large','pricing_store_failed','alert_evaluation_failed','alert_store_failed','recalculation_interrupted')),
                PRIMARY KEY(run_id,event_sequence),
                UNIQUE(run_id,event_kind),
                CHECK((event_kind<>'failed' AND failure_phase IS NULL AND failure_ordinal_kind IS NULL AND failure_ordinal IS NULL AND failure_code IS NULL) OR (event_kind='failed' AND ((failure_phase='recovery' AND failure_ordinal_kind IS NULL AND failure_ordinal IS NULL AND failure_code='recalculation_interrupted') OR (failure_phase<>'recovery' AND failure_ordinal_kind IS NOT NULL AND failure_ordinal IS NOT NULL AND failure_code IS NOT NULL)))),
                FOREIGN KEY(run_id) REFERENCES pricing_recalculation_runs(run_id) ON DELETE RESTRICT
            );
            """),
        ("pricing_recalculation_target_results", """
            CREATE TABLE pricing_recalculation_target_results(
                run_id TEXT NOT NULL,
                target_ordinal INTEGER NOT NULL,
                result_kind TEXT NOT NULL CHECK(result_kind IN ('estimate','unavailable','failed')),
                estimate_status TEXT NULL CHECK(estimate_status IS NULL OR estimate_status IN ('estimated','partial','not-estimable')),
                estimate_id TEXT NULL UNIQUE CHECK(estimate_id IS NULL OR (length(estimate_id)=81 AND estimate_id GLOB 'pricing-estimate-*' AND substr(estimate_id,18) NOT GLOB '*[^0-9a-f]*')),
                result_code TEXT NULL CHECK(result_code IS NULL OR result_code IN ('source_mapping_unavailable','source_adapter_unavailable','codex_adapter_unavailable','stale_recalculation_input','stale_active_estimate','source_adapter_failed','invalid_estimate_source','pricing_estimation_failed','budget_payload_too_large','pricing_store_failed','alert_evaluation_failed','alert_store_failed','recalculation_interrupted')),
                PRIMARY KEY(run_id,target_ordinal),
                CHECK((result_kind='estimate' AND estimate_status IS NOT NULL AND estimate_id IS NOT NULL AND result_code IS NULL) OR (result_kind='unavailable' AND estimate_status IS NULL AND estimate_id IS NULL AND result_code IN ('source_mapping_unavailable','source_adapter_unavailable','codex_adapter_unavailable')) OR (result_kind='failed' AND estimate_status IS NULL AND estimate_id IS NULL AND result_code NOT IN ('source_mapping_unavailable','source_adapter_unavailable','codex_adapter_unavailable'))),
                FOREIGN KEY(run_id,target_ordinal) REFERENCES pricing_recalculation_targets(run_id,target_ordinal) ON DELETE RESTRICT
            );
            """),
        ("pricing_recalculation_budget_results", """
            CREATE TABLE pricing_recalculation_budget_results(
                run_id TEXT NOT NULL,
                scope_ordinal INTEGER NOT NULL CHECK(scope_ordinal BETWEEN 0 AND 7),
                scope_kind TEXT NOT NULL CHECK(scope_kind IN ('session','utc_day','rolling_period')),
                scope_id TEXT NOT NULL CHECK(length(scope_id)=75 AND scope_id GLOB 'cost-scope-*' AND substr(scope_id,12) NOT GLOB '*[^0-9a-f]*'),
                scope_start_utc TEXT NULL CHECK(scope_start_utc IS NULL OR (length(scope_start_utc)=33 AND scope_start_utc GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00')),
                scope_end_utc TEXT NULL CHECK(scope_end_utc IS NULL OR (length(scope_end_utc)=33 AND scope_end_utc GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00')),
                rule_id TEXT NOT NULL CHECK(rule_id IN ('session-estimated-cost-threshold','daily-estimated-cost-threshold','period-estimated-cost-threshold')),
                rule_version TEXT NOT NULL CHECK(rule_version='1'),
                evaluation_id TEXT NOT NULL CHECK(length(evaluation_id)=64 AND evaluation_id=lower(evaluation_id) AND evaluation_id NOT GLOB '*[^0-9a-f]*'),
                outcome_kind TEXT NOT NULL CHECK(outcome_kind IN ('receipt','suppression','no_match')),
                alert_id TEXT NULL CHECK(alert_id IS NULL OR (length(alert_id)=64 AND alert_id=lower(alert_id) AND alert_id NOT GLOB '*[^0-9a-f]*')),
                suppression_ordinal INTEGER NULL CHECK(suppression_ordinal IS NULL OR suppression_ordinal>=0),
                suppression_code TEXT NULL CHECK(suppression_code IS NULL OR suppression_code IN ('rule_disabled','no_eligible_sessions','eligible_set_incomplete','no_covered_estimate','aggregate_amount_not_representable','insufficient_estimate_coverage')),
                PRIMARY KEY(run_id,scope_ordinal),
                CHECK((scope_kind='session' AND rule_id='session-estimated-cost-threshold' AND scope_start_utc IS NULL AND scope_end_utc IS NULL) OR (scope_kind='utc_day' AND rule_id='daily-estimated-cost-threshold' AND scope_start_utc IS NOT NULL AND scope_end_utc IS NOT NULL) OR (scope_kind='rolling_period' AND rule_id='period-estimated-cost-threshold' AND scope_start_utc IS NOT NULL AND scope_end_utc IS NOT NULL)),
                CHECK((outcome_kind='receipt' AND alert_id IS NOT NULL AND suppression_ordinal IS NULL AND suppression_code IS NULL) OR (outcome_kind='suppression' AND alert_id IS NULL AND suppression_ordinal IS NOT NULL AND suppression_code IS NOT NULL) OR (outcome_kind='no_match' AND alert_id IS NULL AND suppression_ordinal IS NULL AND suppression_code IS NULL)),
                FOREIGN KEY(run_id) REFERENCES pricing_recalculation_runs(run_id) ON DELETE RESTRICT,
                FOREIGN KEY(evaluation_id) REFERENCES alert_evaluations(evaluation_id) ON DELETE RESTRICT,
                FOREIGN KEY(alert_id) REFERENCES alert_receipts(alert_id) ON DELETE RESTRICT,
                FOREIGN KEY(evaluation_id,suppression_ordinal) REFERENCES alert_suppressions(evaluation_id,suppression_ordinal) ON DELETE RESTRICT
            );
            """),
        ("pricing_session_attempts", """
            CREATE TABLE pricing_session_attempts(
                session_id TEXT NOT NULL CHECK(length(session_id)=36 AND session_id=lower(session_id) AND session_id NOT GLOB '*[^0-9a-f-]*' AND substr(session_id,9,1)='-' AND substr(session_id,14,1)='-' AND substr(session_id,19,1)='-' AND substr(session_id,24,1)='-'),
                attempt_revision INTEGER NOT NULL CHECK(attempt_revision>0),
                run_id TEXT NOT NULL,
                target_ordinal INTEGER NOT NULL,
                result_kind TEXT NOT NULL CHECK(result_kind IN ('estimate','unavailable','failed')),
                estimate_status TEXT NULL CHECK(estimate_status IS NULL OR estimate_status IN ('estimated','partial','not-estimable')),
                estimate_id TEXT NULL CHECK(estimate_id IS NULL OR (length(estimate_id)=81 AND estimate_id GLOB 'pricing-estimate-*' AND substr(estimate_id,18) NOT GLOB '*[^0-9a-f]*')),
                result_code TEXT NULL CHECK(result_code IS NULL OR result_code IN ('source_mapping_unavailable','source_adapter_unavailable','codex_adapter_unavailable','stale_recalculation_input','stale_active_estimate','source_adapter_failed','invalid_estimate_source','pricing_estimation_failed','budget_payload_too_large','pricing_store_failed','alert_evaluation_failed','alert_store_failed','recalculation_interrupted')),
                PRIMARY KEY(session_id,attempt_revision),
                UNIQUE(run_id,target_ordinal),
                UNIQUE(session_id,run_id),
                CHECK((result_kind='estimate' AND estimate_status IS NOT NULL AND estimate_id IS NOT NULL AND result_code IS NULL) OR (result_kind='unavailable' AND estimate_status IS NULL AND estimate_id IS NULL AND result_code IN ('source_mapping_unavailable','source_adapter_unavailable','codex_adapter_unavailable')) OR (result_kind='failed' AND estimate_status IS NULL AND estimate_id IS NULL AND result_code NOT IN ('source_mapping_unavailable','source_adapter_unavailable','codex_adapter_unavailable'))),
                FOREIGN KEY(session_id) REFERENCES sessions(session_id) ON DELETE RESTRICT,
                FOREIGN KEY(run_id,target_ordinal) REFERENCES pricing_recalculation_target_results(run_id,target_ordinal) ON DELETE RESTRICT,
                FOREIGN KEY(estimate_id) REFERENCES pricing_estimates(estimate_id) ON DELETE RESTRICT
            );
            """),
        ("pricing_estimates", """
            CREATE TABLE pricing_estimates(
                estimate_id TEXT NOT NULL PRIMARY KEY CHECK(length(estimate_id)=81 AND estimate_id GLOB 'pricing-estimate-*' AND substr(estimate_id,18) NOT GLOB '*[^0-9a-f]*'),
                supersedes_estimate_id TEXT NULL UNIQUE CHECK(supersedes_estimate_id IS NULL OR (length(supersedes_estimate_id)=81 AND supersedes_estimate_id GLOB 'pricing-estimate-*' AND substr(supersedes_estimate_id,18) NOT GLOB '*[^0-9a-f]*')),
                schema_version TEXT NOT NULL CHECK(schema_version='pricing.estimate.v1'),
                session_id TEXT NOT NULL CHECK(length(session_id)=36 AND session_id=lower(session_id) AND session_id NOT GLOB '*[^0-9a-f-]*' AND substr(session_id,9,1)='-' AND substr(session_id,14,1)='-' AND substr(session_id,19,1)='-' AND substr(session_id,24,1)='-'),
                catalog_sha256 TEXT NOT NULL,
                configuration_id TEXT NOT NULL,
                source_entry_ordinal INTEGER NOT NULL CHECK(source_entry_ordinal BETWEEN 0 AND 31),
                run_id TEXT NOT NULL,
                target_ordinal INTEGER NOT NULL,
                calculation_time_utc TEXT NOT NULL CHECK(length(calculation_time_utc)=33 AND calculation_time_utc GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'),
                session_effective_at_utc TEXT NOT NULL CHECK(length(session_effective_at_utc)=33 AND session_effective_at_utc GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'),
                status TEXT NOT NULL CHECK(status IN ('estimated','partial','not-estimable')),
                source_surface TEXT NOT NULL CHECK(typeof(source_surface)='text' AND length(source_surface) BETWEEN 1 AND 256 AND substr(source_surface,1,1) GLOB '[A-Za-z0-9]' AND source_surface NOT GLOB '*[^A-Za-z0-9._:-]*'),
                source_application_version TEXT NOT NULL CHECK(typeof(source_application_version)='text' AND length(source_application_version) BETWEEN 1 AND 256 AND substr(source_application_version,1,1) GLOB '[A-Za-z0-9]' AND source_application_version NOT GLOB '*[^A-Za-z0-9._:-]*'),
                provider TEXT NOT NULL CHECK(provider IN ('github_copilot','claude_code','codex_app','unknown')),
                model TEXT NOT NULL CHECK(typeof(model)='text' AND length(model) BETWEEN 1 AND 256),
                billing_mode TEXT NOT NULL CHECK(billing_mode IN ('github_ai_credits','github_legacy_requests','plan_included','anthropic_api_tokens','cloud_provider_api_tokens','subscription','custom_enterprise','unknown')),
                pricing_route TEXT NOT NULL CHECK(pricing_route IN ('credit_consuming_interaction','legacy_request','code_completion','next_edit_suggestion','standard_global','us_only_inference','batch','cloud_provider_configured','subscription_or_contract','unknown')),
                registry_version TEXT NULL CHECK(registry_version IS NULL OR (typeof(registry_version)='text' AND length(registry_version) BETWEEN 1 AND 128 AND substr(registry_version,1,1) GLOB '[a-z0-9]' AND registry_version NOT GLOB '*[^a-z0-9._-]*')),
                registry_source_kind TEXT NULL CHECK(registry_source_kind IS NULL OR registry_source_kind IN ('bundled','local_override')),
                currency TEXT NULL CHECK(currency IS NULL OR currency='USD'),
                amount_text TEXT NULL CHECK(amount_text IS NULL OR (typeof(amount_text)='text' AND length(amount_text) BETWEEN 1 AND 30 AND (amount_text='0' OR (amount_text NOT GLOB '*[^0-9]*' AND substr(amount_text,1,1) GLOB '[1-9]') OR (amount_text NOT GLOB '*[^0-9.]*' AND amount_text GLOB '*.*' AND amount_text NOT GLOB '*.*.*' AND instr(amount_text,'.')>1 AND (substr(amount_text,1,1) GLOB '[1-9]' OR instr(amount_text,'.')=2 AND substr(amount_text,1,1)='0') AND substr(amount_text,-1,1) GLOB '[1-9]')))),
                canonical_sha256 TEXT NOT NULL CHECK(length(canonical_sha256)=64 AND canonical_sha256=lower(canonical_sha256) AND canonical_sha256 NOT GLOB '*[^0-9a-f]*'),
                canonical_blob BLOB NOT NULL CHECK(typeof(canonical_blob)='blob' AND length(canonical_blob) BETWEEN 1 AND 1048576),
                CHECK((status='not-estimable' AND currency IS NULL AND amount_text IS NULL) OR (status IN ('estimated','partial') AND currency='USD' AND amount_text IS NOT NULL)),
                UNIQUE(session_id,estimate_id),
                UNIQUE(run_id,target_ordinal),
                FOREIGN KEY(session_id) REFERENCES sessions(session_id) ON DELETE RESTRICT,
                FOREIGN KEY(catalog_sha256) REFERENCES pricing_catalog_snapshots(catalog_sha256) ON DELETE RESTRICT,
                FOREIGN KEY(configuration_id,catalog_sha256) REFERENCES pricing_configurations(configuration_id,catalog_sha256) ON DELETE RESTRICT,
                FOREIGN KEY(run_id,target_ordinal) REFERENCES pricing_recalculation_targets(run_id,target_ordinal) ON DELETE RESTRICT,
                FOREIGN KEY(session_id,supersedes_estimate_id) REFERENCES pricing_estimates(session_id,estimate_id) ON DELETE RESTRICT
            );
            """),
        ("pricing_estimate_heads", """
            CREATE TABLE pricing_estimate_heads(
                session_id TEXT NOT NULL CHECK(length(session_id)=36 AND session_id=lower(session_id) AND session_id NOT GLOB '*[^0-9a-f-]*' AND substr(session_id,9,1)='-' AND substr(session_id,14,1)='-' AND substr(session_id,19,1)='-' AND substr(session_id,24,1)='-'),
                head_revision INTEGER NOT NULL CHECK(head_revision>0),
                estimate_id TEXT NOT NULL CHECK(length(estimate_id)=81 AND estimate_id GLOB 'pricing-estimate-*' AND substr(estimate_id,18) NOT GLOB '*[^0-9a-f]*'),
                previous_head_revision INTEGER NULL,
                previous_estimate_id TEXT NULL CHECK(previous_estimate_id IS NULL OR (length(previous_estimate_id)=81 AND previous_estimate_id GLOB 'pricing-estimate-*' AND substr(previous_estimate_id,18) NOT GLOB '*[^0-9a-f]*')),
                PRIMARY KEY(session_id,head_revision),
                UNIQUE(session_id,estimate_id),
                UNIQUE(estimate_id),
                UNIQUE(session_id,head_revision,estimate_id),
                CHECK((head_revision=1 AND previous_head_revision IS NULL AND previous_estimate_id IS NULL) OR (head_revision>1 AND previous_head_revision=head_revision-1 AND previous_estimate_id IS NOT NULL)),
                FOREIGN KEY(session_id,estimate_id) REFERENCES pricing_estimates(session_id,estimate_id) ON DELETE RESTRICT,
                FOREIGN KEY(session_id,previous_head_revision,previous_estimate_id) REFERENCES pricing_estimate_heads(session_id,head_revision,estimate_id) ON DELETE RESTRICT
            );
            """),
    ];

    private static readonly IReadOnlyList<(string Name, string Table, string Sql)> Indexes =
    [
        ("pricing_recalculation_runs_recovery_idx", "pricing_recalculation_runs", "CREATE INDEX pricing_recalculation_runs_recovery_idx ON pricing_recalculation_runs(calculation_time_utc,run_id);"),
        ("pricing_recalculation_targets_session_idx", "pricing_recalculation_targets", "CREATE INDEX pricing_recalculation_targets_session_idx ON pricing_recalculation_targets(session_id,run_id,target_ordinal);"),
        ("pricing_recalculation_events_kind_idx", "pricing_recalculation_events", "CREATE INDEX pricing_recalculation_events_kind_idx ON pricing_recalculation_events(event_kind,run_id,event_sequence);"),
        ("pricing_estimates_analytics_idx", "pricing_estimates", "CREATE INDEX pricing_estimates_analytics_idx ON pricing_estimates(session_effective_at_utc,provider,model,billing_mode,registry_version,currency,estimate_id);"),
        ("pricing_recalculation_budget_alert_idx", "pricing_recalculation_budget_results", "CREATE INDEX pricing_recalculation_budget_alert_idx ON pricing_recalculation_budget_results(alert_id,run_id,scope_ordinal);"),
    ];

    private static readonly IReadOnlyList<PricingOwnedObject> CreationObjects = BuildCreationObjects();

    internal static IReadOnlyList<PricingOwnedObject> OwnedObjects { get; } =
        CreationObjects
            .OrderBy(item => item.Type, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();

    internal static void Ensure(SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var componentVersion = Scalar(connection, transaction, "SELECT version FROM schema_version WHERE component='pricing';");
        var existing = ReadOwnedObjects(connection, transaction);
        if (componentVersion is null && existing.Count == 0)
        {
            ValidateDependencies(connection, transaction);
            foreach (var item in CreationObjects) Execute(connection, transaction, item.Sql);
            Execute(connection, transaction, "INSERT INTO schema_version(component,version) VALUES('pricing',1);");
            return;
        }

        if (!IsValid(connection, transaction))
            throw new InvalidOperationException("Pricing schema is incomplete or unsupported.");
    }

    internal static bool IsValid(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM schema_version WHERE component='pricing' AND version=1;") ?? 0, CultureInfo.InvariantCulture) != 1)
            return false;
        var actual = ReadOwnedObjects(connection, transaction);
        if (actual.Count != OwnedObjects.Count) return false;
        foreach (var expected in OwnedObjects)
        {
            if (!actual.TryGetValue((expected.Type, expected.Name), out var value)
                || value.TableName != expected.TableName
                || Normalize(value.Sql) != Normalize(expected.Sql))
                return false;
        }
        return true;
    }

    internal static bool ValidateRows(SqliteConnection connection, SqliteTransaction? transaction) =>
        PricingRowValidatorV1.Validate(connection, transaction);

    private static IReadOnlyList<PricingOwnedObject> BuildCreationObjects()
    {
        var values = new List<PricingOwnedObject>();
        values.AddRange(Tables.Select(item => new PricingOwnedObject("table", item.Name, item.Name, item.Sql)));
        values.AddRange(Indexes.Select(item => new PricingOwnedObject("index", item.Name, item.Table, item.Sql)));
        foreach (var table in Tables)
        {
            if (table.Name == "pricing_configuration_previews")
            {
                AddTrigger(values, table.Name, "no_update", $"CREATE TRIGGER {table.Name}_no_update BEFORE UPDATE ON {table.Name} BEGIN SELECT RAISE(ABORT,'{table.Name}_immutable'); END;");
                AddTrigger(values, table.Name, "no_replace", $"CREATE TRIGGER {table.Name}_no_replace BEFORE INSERT ON {table.Name} WHEN EXISTS(SELECT 1 FROM {table.Name} WHERE preview_digest=NEW.preview_digest) BEGIN SELECT RAISE(ABORT,'{table.Name}_replace_forbidden'); END;");
                continue;
            }
            AddTrigger(values, table.Name, "no_update", $"CREATE TRIGGER {table.Name}_no_update BEFORE UPDATE ON {table.Name} BEGIN SELECT RAISE(ABORT,'{table.Name}_immutable'); END;");
            AddTrigger(values, table.Name, "no_delete", $"CREATE TRIGGER {table.Name}_no_delete BEFORE DELETE ON {table.Name} BEGIN SELECT RAISE(ABORT,'{table.Name}_immutable'); END;");
            var identity = table.Name switch
            {
                "pricing_catalog_snapshots" => "catalog_sha256=NEW.catalog_sha256",
                "pricing_configurations" => "configuration_id=NEW.configuration_id OR (NEW.predecessor_configuration_id IS NOT NULL AND predecessor_configuration_id=NEW.predecessor_configuration_id)",
                "pricing_configuration_heads" => "head_revision=NEW.head_revision OR configuration_id=NEW.configuration_id OR (NEW.previous_head_revision IS NOT NULL AND previous_head_revision=NEW.previous_head_revision) OR (NEW.previous_configuration_id IS NOT NULL AND previous_configuration_id=NEW.previous_configuration_id)",
                "pricing_configuration_commits" => "head_revision=NEW.head_revision OR configuration_id=NEW.configuration_id OR preview_digest=NEW.preview_digest",
                "pricing_recalculation_runs" => "run_id=NEW.run_id OR idempotency_key=NEW.idempotency_key",
                "pricing_recalculation_targets" => "(run_id=NEW.run_id AND target_ordinal=NEW.target_ordinal) OR (run_id=NEW.run_id AND session_id=NEW.session_id)",
                "pricing_recalculation_events" => "(run_id=NEW.run_id AND event_sequence=NEW.event_sequence) OR (run_id=NEW.run_id AND event_kind=NEW.event_kind)",
                "pricing_recalculation_target_results" => "(run_id=NEW.run_id AND target_ordinal=NEW.target_ordinal) OR (NEW.estimate_id IS NOT NULL AND estimate_id=NEW.estimate_id)",
                "pricing_recalculation_budget_results" => "run_id=NEW.run_id AND scope_ordinal=NEW.scope_ordinal",
                "pricing_session_attempts" => "(session_id=NEW.session_id AND attempt_revision=NEW.attempt_revision) OR (run_id=NEW.run_id AND target_ordinal=NEW.target_ordinal) OR (session_id=NEW.session_id AND run_id=NEW.run_id)",
                "pricing_estimates" => "estimate_id=NEW.estimate_id OR (NEW.supersedes_estimate_id IS NOT NULL AND supersedes_estimate_id=NEW.supersedes_estimate_id) OR (run_id=NEW.run_id AND target_ordinal=NEW.target_ordinal)",
                "pricing_estimate_heads" => "(session_id=NEW.session_id AND head_revision=NEW.head_revision) OR (session_id=NEW.session_id AND estimate_id=NEW.estimate_id) OR estimate_id=NEW.estimate_id",
                _ => throw new InvalidOperationException(),
            };
            AddTrigger(values, table.Name, "no_replace", $"CREATE TRIGGER {table.Name}_no_replace BEFORE INSERT ON {table.Name} WHEN EXISTS(SELECT 1 FROM {table.Name} WHERE {identity}) BEGIN SELECT RAISE(ABORT,'{table.Name}_replace_forbidden'); END;");
        }
        AddContiguous(values, "pricing_configuration_heads", "head_revision", null);
        AddContiguous(values, "pricing_recalculation_targets", "target_ordinal", "run_id");
        AddContiguous(values, "pricing_recalculation_events", "event_sequence", "run_id");
        AddContiguous(values, "pricing_recalculation_budget_results", "scope_ordinal", "run_id");
        AddContiguous(values, "pricing_session_attempts", "attempt_revision", "session_id");
        AddContiguous(values, "pricing_estimate_heads", "head_revision", "session_id");
        return values;
    }

    private static void AddTrigger(List<PricingOwnedObject> values, string table, string suffix, string sql) =>
        values.Add(new("trigger", $"{table}_{suffix}", table, sql));

    private static void AddContiguous(List<PricingOwnedObject> values, string table, string ordinal, string? partition)
    {
        var where = partition is null ? string.Empty : $" WHERE {partition}=NEW.{partition}";
        var sql = $"CREATE TRIGGER {table}_contiguous_insert BEFORE INSERT ON {table} WHEN NEW.{ordinal}<>COALESCE((SELECT MAX({ordinal})+1 FROM {table}{where}),{(ordinal == "attempt_revision" || table.EndsWith("_heads", StringComparison.Ordinal) ? 1 : 0)}) BEGIN SELECT RAISE(ABORT,'{table}_noncontiguous'); END;";
        AddTrigger(values, table, "contiguous_insert", sql);
    }

    private static void ValidateDependencies(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!PricingDependencySchemaV1.IsValid(connection, transaction))
            throw new InvalidOperationException("Pricing schema dependency is missing or unsupported.");
    }

    private static Dictionary<(string Type, string Name), (string TableName, string Sql)> ReadOwnedObjects(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT type,name,tbl_name,sql FROM sqlite_schema WHERE name GLOB 'pricing_*' ORDER BY type,name;");
        using var reader = command.ExecuteReader();
        var values = new Dictionary<(string, string), (string, string)>();
        while (reader.Read()) values.Add((reader.GetString(0), reader.GetString(1)), (reader.GetString(2), reader.GetString(3)));
        return values;
    }

    private static string Normalize(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';');

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = Command(connection, transaction, sql);
        return command.ExecuteScalar();
    }

    private static bool Exists(SqliteConnection connection, SqliteTransaction? transaction, string sql) =>
        Scalar(connection, transaction, sql) is not null;

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = Command(connection, transaction, sql);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command;
    }
}
