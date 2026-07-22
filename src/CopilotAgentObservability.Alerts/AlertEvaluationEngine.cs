namespace CopilotAgentObservability.Alerts;

public sealed class AlertEvaluationEngine
{
    private readonly AlertRuleRegistry _registry;
    private readonly IAlertEvidenceResolver _evidenceResolver;

    public AlertEvaluationEngine(AlertRuleRegistry registry, IAlertEvidenceResolver evidenceResolver)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _evidenceResolver = evidenceResolver ?? throw new ArgumentNullException(nameof(evidenceResolver));
    }

    public AlertEvaluationResult Evaluate(AlertNormalizedSnapshot snapshot, AlertEngineConfiguration configuration)
    {
        AlertValidation.ValidateSnapshot(snapshot);
        snapshot = AlertFreezer.Snapshot(snapshot);
        var effective = AlertValidation.ResolveConfiguration(_registry, configuration);
        var inputHash = AlertHashing.Sha256(AlertCanonicalJson.SerializeSnapshot(snapshot));
        var configurationBytes = AlertCanonicalJson.SerializeResolvedConfiguration(configuration, effective);
        var configurationHash = AlertHashing.Sha256(configurationBytes);
        var evaluationId = AlertHashing.Identifier(
            "alert-evaluation/v1",
            AlertContractVersions.Evaluation,
            inputHash,
            configuration.ConfigurationVersion,
            configurationHash,
            string.Join('\n', _registry.Rules.Select(rule => $"{rule.Descriptor.RuleId}@{rule.Descriptor.RuleVersion}")));
        var receipts = new Dictionary<string, AlertReceipt>(StringComparer.Ordinal);
        var suppressions = new List<AlertSuppression>();
        var rejected = new List<AlertRejectedMatch>();

        foreach (var rule in _registry.Rules)
        {
            var descriptor = rule.Descriptor;
            var resolved = effective[(descriptor.RuleId, descriptor.RuleVersion)];
            if (!resolved.Enabled)
            {
                suppressions.Add(Suppression(evaluationId, descriptor, "rule_disabled", []));
                continue;
            }

            if (!descriptor.ApplicableSourceSurfaces.Contains(snapshot.SourceSurface, StringComparer.Ordinal)
                || resolved.SourceSurfaceAllowlist is not null
                && !resolved.SourceSurfaceAllowlist.Contains(snapshot.SourceSurface, StringComparer.Ordinal))
            {
                suppressions.Add(Suppression(evaluationId, descriptor, "source_not_applicable", []));
                continue;
            }

            var available = snapshot.Capabilities
                .Where(item => item.Availability == AlertCapabilityAvailability.Available)
                .Select(item => item.Name)
                .ToHashSet(StringComparer.Ordinal);
            var missing = descriptor.RequiredCapabilities.Where(capability => !available.Contains(capability))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (missing.Length != 0)
            {
                suppressions.Add(Suppression(evaluationId, descriptor, "missing_required_capability", missing));
                continue;
            }

            AlertRuleOutcome outcome;
            try
            {
                outcome = rule.Evaluate(new AlertRuleContext(snapshot, resolved.Thresholds));
            }
            catch (Exception)
            {
                throw new AlertContractException("rule_evaluation_failed", $"Rule '{descriptor.RuleId}' failed evaluation.");
            }

            if (outcome is null || outcome.Matches is null || outcome.Suppressions is null)
            {
                throw new AlertContractException("invalid_rule_output", $"Rule '{descriptor.RuleId}' returned an invalid outcome.");
            }

            foreach (var ruleSuppression in outcome.Suppressions.Distinct())
            {
                if (!AlertValidation.IsDeclaredRuleSuppression(descriptor, ruleSuppression))
                {
                    throw new AlertContractException("invalid_rule_output", $"Rule '{descriptor.RuleId}' returned an invalid suppression.");
                }

                suppressions.Add(Suppression(evaluationId, descriptor, ruleSuppression.Code, []));
            }

            foreach (var match in outcome.Matches)
            {
                if (!AlertValidation.IsValidMatchShape(match, snapshot))
                {
                    throw new AlertContractException("invalid_rule_output", $"Rule '{descriptor.RuleId}' returned an invalid match.");
                }

                if (!AlertValidation.HasExactSnapshotEvidence(match, snapshot) || match.Evidence.Any(reference => !EvidenceExists(reference)))
                {
                    rejected.Add(new(descriptor.RuleId, descriptor.RuleVersion, "unresolved_evidence"));
                    continue;
                }

                var evidence = Array.AsReadOnly(AlertCanonicalOrdering.Evidence(match.Evidence).ToArray());
                var observed = Array.AsReadOnly(match.ObservedValues.OrderBy(value => value.Name, StringComparer.Ordinal).ThenBy(value => value.Unit, StringComparer.Ordinal).Select(item => item with { }).ToArray());
                var thresholds = Array.AsReadOnly(resolved.Thresholds.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => new AlertObservedValue(item.Key, resolved.Units[item.Key[..item.Key.LastIndexOf('.')]], item.Value))
                    .ToArray());
                var alertId = AlertReceiptIdentityV1.Create(
                    evaluationId,
                    descriptor.RuleId,
                    descriptor.RuleVersion,
                    match.Severity,
                    evidence,
                    observed,
                    match.FirstObservedAt,
                    match.LastObservedAt);
                var receipt = new AlertReceipt(
                    AlertContractVersions.Receipt,
                    AlertContractVersions.SanitizedReceiptProfile,
                    alertId,
                    evaluationId,
                    descriptor.RuleId,
                    descriptor.RuleVersion,
                    match.Severity,
                    AlertInitialState.Open,
                    snapshot.SourceSurface,
                    snapshot.SourceVersion,
                    snapshot.SessionId,
                    snapshot.TraceId,
                    evidence,
                    observed,
                    thresholds,
                    configuration.ConfigurationVersion,
                    configurationHash,
                    Array.AsReadOnly(descriptor.RequiredCapabilities.Order(StringComparer.Ordinal).ToArray()),
                    snapshot.Completeness,
                    Array.AsReadOnly(AlertCanonicalOrdering.CompletenessReasons(snapshot.CompletenessReasons).ToArray()),
                    match.FirstObservedAt.ToUniversalTime(),
                    match.LastObservedAt.ToUniversalTime(),
                    inputHash,
                    descriptor.Title);
                receipts.TryAdd(alertId, receipt);
            }
        }

        var orderedReceipts = Array.AsReadOnly(receipts.Values.OrderBy(item => AlertWire.SeverityRank(item.Severity))
            .ThenBy(item => item.RuleId, StringComparer.Ordinal)
            .ThenBy(item => item.RuleVersion, StringComparer.Ordinal)
            .ThenBy(item => item.FirstObservedAt)
            .ThenBy(item => AlertCanonicalJson.EvidenceIdentity(item.Evidence), StringComparer.Ordinal)
            .ThenBy(item => item.AlertId, StringComparer.Ordinal)
            .ToArray());
        var orderedSuppressions = Array.AsReadOnly(suppressions
            .GroupBy(item => $"{item.RuleId}\n{item.RuleVersion}\n{item.Code}\n{string.Join('\n', item.MissingCapabilities)}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.RuleId, StringComparer.Ordinal).ThenBy(item => item.RuleVersion, StringComparer.Ordinal).ThenBy(item => item.Code, StringComparer.Ordinal).ToArray());
        var orderedRejected = Array.AsReadOnly(rejected.Distinct().OrderBy(item => item.RuleId, StringComparer.Ordinal).ThenBy(item => item.RuleVersion, StringComparer.Ordinal).ThenBy(item => item.Code, StringComparer.Ordinal).ToArray());
        return new(AlertContractVersions.Evaluation, evaluationId, inputHash, configuration.ConfigurationVersion, configurationHash, orderedReceipts, orderedSuppressions, orderedRejected);
    }

    private static AlertSuppression Suppression(string evaluationId, AlertRuleDescriptor descriptor, string code, IReadOnlyList<string> missing) =>
        new(evaluationId, descriptor.RuleId, descriptor.RuleVersion, code, Array.AsReadOnly(missing.ToArray()));

    private bool EvidenceExists(AlertEvidenceReference reference)
    {
        try { return _evidenceResolver.Exists(reference); }
        catch { return false; }
    }
}
