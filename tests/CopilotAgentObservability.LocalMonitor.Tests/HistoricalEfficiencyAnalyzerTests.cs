using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class HistoricalEfficiencyAnalysisTests
{
    [Fact]
    public void Analyze_SameRepositorySafeDataset_IsByteEquivalentAndUsesTotalTokenPrecedence()
    {
        var extraction = HistoricalEfficiencyTestData.Extraction(
            TokenSession(1, 100), TokenSession(2, 110), TokenSession(3, 120), TokenSession(4, 300));

        var first = HistoricalEfficiencyAnalyzerV1.Analyze(extraction);
        var second = HistoricalEfficiencyAnalyzerV1.Analyze(extraction);

        Assert.Equal(first.CanonicalBytes, second.CanonicalBytes);
        Assert.Equal(first.PayloadSha256, second.PayloadSha256);
        Assert.Equal(extraction.RepositorySafeSha256, first.Receipt.ExtractionSha256);
        Assert.Equal(HistoricalEfficiencyAnalysisStateV1.Succeeded, first.Receipt.State);
        var driver = Assert.Single(first.Receipt.Drivers, value => value.Category == HistoricalEfficiencyDriverCategoryV1.TokenVolume);
        Assert.Equal(HistoricalEfficiencyDriverVerdictV1.Supported, driver.Verdict);
        Assert.Equal(300m, driver.ObservedValues.Single(value => value.Name == "session_total").Value);
        Assert.Equal(115m, driver.CohortMedian!.Value);
        Assert.Equal(120m, driver.CohortPercentile!.Value);
        Assert.Equal(4, driver.SourceSessions.Count);
        Assert.Equal(4, driver.EvidenceRefs.Count);
        Assert.All(driver.SourceSessions, value => Assert.StartsWith("session-ref-", value, StringComparison.Ordinal));
        Assert.All(driver.EvidenceRefs, value => Assert.StartsWith("trace-ref-", value.TraceId, StringComparison.Ordinal));

        var json = Encoding.UTF8.GetString(first.CanonicalBytes);
        Assert.DoesNotContain("person@example.com", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private\\workspace", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"price", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"currency", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"cost", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_LowSampleAndMissingCache_AreInsufficientOrUnavailableWithoutZeroFill()
    {
        var first = new SyntheticSession(1).AddTurn(1, totalTokens: 100, cacheReadTokens: 0).AddQuality("pass");
        var second = new SyntheticSession(2).AddTurn(1, totalTokens: 200).AddQuality("pass");

        var result = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(first, second));

        Assert.Equal(HistoricalEfficiencyAnalysisStateV1.ZeroDrivers, result.Receipt.State);
        Assert.Empty(result.Receipt.Drivers);
        var token = Coverage(result, HistoricalEfficiencyDriverCategoryV1.TokenVolume);
        Assert.Equal(HistoricalEfficiencyCoverageStateV1.Insufficient, token.State);
        Assert.Contains(HistoricalEfficiencyCoverageReasonV1.MinimumSampleUnmet, token.Reasons);
        var cache = Coverage(result, HistoricalEfficiencyDriverCategoryV1.CacheInefficiency);
        Assert.Equal(HistoricalEfficiencyCoverageStateV1.Insufficient, cache.State);
        Assert.Contains(HistoricalEfficiencyCoverageReasonV1.MissingRequiredMetric, cache.Reasons);
        Assert.DoesNotContain("\"name\":\"cache_read_ratio\"", Encoding.UTF8.GetString(result.CanonicalBytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_ContextAndCacheThresholds_PreserveObservedZeroAndExactReferences()
    {
        var session = new SyntheticSession(1)
            .AddTurn(1, inputTokens: 5_000, cacheReadTokens: 0)
            .AddTurn(2, inputTokens: 9_000, cacheReadTokens: 0)
            .AddTurn(3, inputTokens: 18_000, cacheReadTokens: 0)
            .AddQuality("pass");

        var result = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(session));

        var context = Assert.Single(result.Receipt.Drivers, value => value.Category == HistoricalEfficiencyDriverCategoryV1.ContextGrowth);
        Assert.Equal(3.6m, context.ObservedValues.Single(value => value.Name == "context_growth_ratio").Value);
        Assert.Equal(3, context.EvidenceRefs.Count);
        Assert.Equal(HistoricalEfficiencyDriverVerdictV1.Supported, context.Verdict);
        var cache = Assert.Single(result.Receipt.Drivers, value => value.Category == HistoricalEfficiencyDriverCategoryV1.CacheInefficiency);
        Assert.Equal(0m, cache.ObservedValues.Single(value => value.Name == "cache_read_ratio").Value);
        Assert.Equal(27_000m, cache.ObservedValues.Single(value => value.Name == "included_input_tokens").Value);
        Assert.Equal(2, cache.EvidenceRefs.Count);
        Assert.Equal(HistoricalEfficiencyDriverVerdictV1.Supported, cache.Verdict);
    }

    [Fact]
    public void Analyze_ExactOperationalGroups_EmitSeparateNonDuplicatedDrivers()
    {
        var session = new SyntheticSession(1)
            .AddRetry(3)
            .AddRepeatedToolCalls(3)
            .AddPermissionWait(30, 30)
            .AddSubagentFanout(2)
            .AddErrorSpan()
            .AddQuality("pass");

        var result = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(session));

        var retry = Assert.Single(result.Receipt.Drivers, value => value.Category == HistoricalEfficiencyDriverCategoryV1.RetryOverhead);
        Assert.Equal(3m, retry.ObservedValues.Single(value => value.Name == "attempt_count").Value);
        Assert.Equal(2m, retry.ObservedValues.Single(value => value.Name == "retry_overhead").Value);
        Assert.Equal(3, retry.EvidenceRefs.Count);
        Assert.Single(result.Receipt.Drivers, value => value.Category == HistoricalEfficiencyDriverCategoryV1.ToolCallVolume);
        var permission = Assert.Single(result.Receipt.Drivers, value => value.Category == HistoricalEfficiencyDriverCategoryV1.PermissionWait);
        Assert.Equal(60m, permission.ObservedValues.Single(value => value.Name == "total_wait").Value);
        Assert.Single(result.Receipt.Drivers, value => value.Category == HistoricalEfficiencyDriverCategoryV1.SubagentFanout);
        var toolFailure = Coverage(result, HistoricalEfficiencyDriverCategoryV1.ToolFailureOverhead);
        Assert.Equal(HistoricalEfficiencyCoverageStateV1.Unavailable, toolFailure.State);
        Assert.Equal([HistoricalEfficiencyCoverageReasonV1.ExactToolFailureStatusUnavailable], toolFailure.Reasons);
        Assert.DoesNotContain(result.Receipt.Drivers, value => value.Category == HistoricalEfficiencyDriverCategoryV1.ToolFailureOverhead);
    }

    [Fact]
    public void Analyze_DuplicateRetryIdentityAndWrongPermissionUnit_AreExcludedWithoutInventedOverhead()
    {
        var session = new SyntheticSession(1)
            .AddRetry(3)
            .AddDuplicateRetry(3)
            .AddPermissionWaitWithUnit(60, "millisecond")
            .AddQuality("pass");

        var result = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(session));

        var retry = Assert.Single(result.Receipt.Drivers, value => value.Category == HistoricalEfficiencyDriverCategoryV1.RetryOverhead);
        Assert.Equal(3m, retry.ObservedValues.Single(value => value.Name == "attempt_count").Value);
        Assert.DoesNotContain(result.Receipt.Drivers, value => value.Category == HistoricalEfficiencyDriverCategoryV1.PermissionWait);
        var permission = Coverage(result, HistoricalEfficiencyDriverCategoryV1.PermissionWait);
        Assert.Equal(HistoricalEfficiencyCoverageStateV1.Insufficient, permission.State);
        Assert.Contains(HistoricalEfficiencyCoverageReasonV1.MissingRequiredMetric, permission.Reasons);
    }

    [Fact]
    public void Analyze_MixedSourceAndModel_PartitionsComparisonsAndEmitsWeakObservation()
    {
        var sessions = new List<SyntheticSession>
        {
            TokenSession(1, 100), TokenSession(2, 100), TokenSession(3, 100), TokenSession(4, 100),
            new SyntheticSession(5, SessionSourceSurface.ClaudeCode, sourceVersion: 2, adapterVersion: 2, model: 2)
                .AddTurn(1, totalTokens: 1_000).AddQuality("pass"),
        };

        var result = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction([.. sessions], [], false, 0));

        Assert.DoesNotContain(result.Receipt.Drivers, value => value.Category == HistoricalEfficiencyDriverCategoryV1.TokenVolume);
        var modelMix = Assert.Single(result.Receipt.Drivers, value => value.Category == HistoricalEfficiencyDriverCategoryV1.ModelMixObservation);
        Assert.Equal(HistoricalEfficiencyDriverVerdictV1.Weak, modelMix.Verdict);
        Assert.Equal(2m, modelMix.ObservedValues.Single(value => value.Name == "distinct_model_count").Value);
        Assert.Contains(HistoricalEfficiencyComparisonNoteV1.MixedSourceSurface, result.Receipt.ComparisonNotes);
        Assert.Contains(HistoricalEfficiencyComparisonNoteV1.MixedSourceVersion, result.Receipt.ComparisonNotes);
        Assert.Contains(HistoricalEfficiencyComparisonNoteV1.MixedAdapterVersion, result.Receipt.ComparisonNotes);
        Assert.Contains(HistoricalEfficiencyComparisonNoteV1.MixedModel, result.Receipt.ComparisonNotes);
    }

    [Fact]
    public void Analyze_QualityAvailability_DowngradesMetricsAndNeverClaimsImprovement()
    {
        var unavailable = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(
            TokenSession(1, 100, quality: null), TokenSession(2, 110, quality: null),
            TokenSession(3, 120, quality: null), TokenSession(4, 300, quality: null)));
        Assert.Equal(HistoricalEfficiencyQualityAvailabilityV1.Unavailable, unavailable.Receipt.QualityAvailability);
        Assert.Equal(HistoricalEfficiencyDriverVerdictV1.Weak,
            unavailable.Receipt.Drivers.Single(value => value.Category == HistoricalEfficiencyDriverCategoryV1.TokenVolume).Verdict);

        var partial = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(
            TokenSession(1, 100), TokenSession(2, 110, quality: "unknown"),
            TokenSession(3, 120), TokenSession(4, 300)));
        Assert.Equal(HistoricalEfficiencyQualityAvailabilityV1.Partial, partial.Receipt.QualityAvailability);
        Assert.Equal(HistoricalEfficiencyDriverVerdictV1.Weak,
            partial.Receipt.Drivers.Single(value => value.Category == HistoricalEfficiencyDriverCategoryV1.TokenVolume).Verdict);

        var regression = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(
            TokenSession(1, 100), TokenSession(2, 110), TokenSession(3, 120), TokenSession(4, 300, quality: "fail")));
        Assert.Equal(HistoricalEfficiencyQualityAvailabilityV1.RegressionObserved, regression.Receipt.QualityAvailability);
        var regressionDriver = regression.Receipt.Drivers.Single(value => value.Category == HistoricalEfficiencyDriverCategoryV1.TokenVolume);
        Assert.Equal(HistoricalEfficiencyDriverVerdictV1.Weak, regressionDriver.Verdict);
        Assert.Contains(HistoricalEfficiencyComparisonNoteV1.QualityRegressionObserved, regressionDriver.ComparisonNotes);

        var incomplete = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(
            TokenSession(1, 100), TokenSession(2, 110), TokenSession(3, 120),
            TokenSession(4, 300, completeness: SessionCompleteness.Partial)));
        Assert.Equal(HistoricalEfficiencyDriverVerdictV1.Incomplete,
            incomplete.Receipt.Drivers.Single(value => value.Category == HistoricalEfficiencyDriverCategoryV1.TokenVolume).Verdict);

        var historical = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(
            TokenSession(1, 100), TokenSession(2, 110), TokenSession(3, 120),
            new SyntheticSession(4, completeness: SessionCompleteness.Partial,
                    sourceKind: HistoricalEvidenceSourceKindV1.HistoricalSummary)
                .AddTurn(1, totalTokens: 300).AddQuality("pass")));
        var historicalDriver = historical.Receipt.Drivers.Single(value => value.Category == HistoricalEfficiencyDriverCategoryV1.TokenVolume);
        Assert.Equal(HistoricalEfficiencyDriverVerdictV1.Incomplete, historicalDriver.Verdict);
        Assert.Contains(HistoricalEfficiencyComparisonNoteV1.HistoricalSummaryPresent, historicalDriver.ComparisonNotes);

        foreach (var bytes in new[] { unavailable.CanonicalBytes, partial.CanonicalBytes, regression.CanonicalBytes, incomplete.CanonicalBytes, historical.CanonicalBytes })
        {
            var text = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("improved", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("verified", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("does not establish improvement", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Analyze_DurationOutlier_UsesExactMaximumMedianAndNearestRankPercentile()
    {
        static SyntheticSession DurationSession(int index, long duration) =>
            new SyntheticSession(index).AddDuration(duration).AddDuration(duration - 10).AddQuality("pass");

        var result = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(
            DurationSession(1, 100), DurationSession(2, 110), DurationSession(3, 120), DurationSession(4, 300)));

        var driver = Assert.Single(result.Receipt.Drivers, value => value.Category == HistoricalEfficiencyDriverCategoryV1.DurationOutlier);
        Assert.Equal(300m, driver.ObservedValues.Single(value => value.Name == "session_duration").Value);
        Assert.Equal(115m, driver.CohortMedian!.Value);
        Assert.Equal(120m, driver.CohortPercentile!.Value);
        Assert.Equal(8, driver.EvidenceRefs.Count);
    }

    [Fact]
    public void Analyze_ZeroDrivers_PreservesCoverageAndExactIssue75SuccessState()
    {
        var result = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(
            [new SyntheticSession(1).AddQuality("pass")],
            [HistoricalEfficiencyTestData.MissingSession(99)],
            truncatedBefore: true,
            truncatedSessionCount: 2));

        Assert.Equal(HistoricalEfficiencyAnalysisStateV1.ZeroDrivers, result.Receipt.State);
        Assert.Empty(result.Receipt.Drivers);
        Assert.Equal(10, result.Receipt.CategoryCoverage.Count);
        Assert.Equal(1, result.Receipt.Coverage.IncludedSessionCount);
        Assert.Equal(1, result.Receipt.Coverage.ExcludedSessionCount);
        Assert.True(result.Receipt.Coverage.TruncatedBefore);
        Assert.Equal(2, result.Receipt.Coverage.TruncatedSessionCount);
        Assert.Contains(HistoricalEfficiencyComparisonNoteV1.TruncatedWindow, result.Receipt.ComparisonNotes);
    }

    [Fact]
    public void Analyze_InvalidRepositorySafeHash_FailsClosedWithFixedCode()
    {
        var valid = HistoricalEfficiencyTestData.Extraction(TokenSession(1, 100));
        var invalid = valid with { RepositorySafeSha256 = new string('f', 64) };

        var exception = Assert.Throws<HistoricalEfficiencyValidationException>(() => HistoricalEfficiencyAnalyzerV1.Analyze(invalid));

        Assert.Equal(HistoricalEfficiencyValidationCodeV1.InvalidHistoricalEfficiencyInput, exception.Code);
        Assert.Equal("Historical efficiency input is invalid.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Analyze_RawLocalBytesInRepositorySafeSlot_FailClosedWithoutFallback()
    {
        var valid = HistoricalEfficiencyTestData.Extraction(TokenSession(1, 100));
        var invalid = valid with
        {
            RepositorySafeBytes = valid.RawLocalBytes,
            RepositorySafeSha256 = valid.RawLocalSha256,
        };

        var exception = Assert.Throws<HistoricalEfficiencyValidationException>(() => HistoricalEfficiencyAnalyzerV1.Analyze(invalid));

        Assert.Equal(HistoricalEfficiencyValidationCodeV1.InvalidHistoricalEfficiencyInput, exception.Code);
        Assert.Equal("Historical efficiency input is invalid.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Analyze_EveryDriverReferenceResolvesInsideTheFrozenRepositorySafeDataset()
    {
        var extraction = HistoricalEfficiencyTestData.Extraction(
            TokenSession(1, 100), TokenSession(2, 110), TokenSession(3, 120), TokenSession(4, 300));

        var result = HistoricalEfficiencyAnalyzerV1.Analyze(extraction);
        var availableReferences = extraction.RepositorySafe.EvidenceGroups.SelectMany(value => value.References)
            .Concat(extraction.RepositorySafe.Sessions.SelectMany(value => value.Metadata.ModelObservations).Select(value => value.EvidenceRef))
            .Concat(extraction.RepositorySafe.Sessions.SelectMany(value => value.Metadata.DurationObservations).Select(value => value.EvidenceRef))
            .ToHashSet();
        var availableSessions = extraction.RepositorySafe.Sessions.Select(value => value.SessionId).ToHashSet(StringComparer.Ordinal);

        Assert.All(result.Receipt.Drivers, driver =>
        {
            Assert.All(driver.SourceSessions, session => Assert.Contains(session, availableSessions));
            Assert.All(driver.EvidenceRefs, reference => Assert.Contains(reference, availableReferences));
            Assert.All(driver.QualityEvidenceRefs, reference => Assert.Contains(reference, availableReferences));
            Assert.Equal(driver.EvidenceRefs, driver.Mitigation.EvidenceRefs);
        });
    }

    [Fact]
    public void FrozenHandoffFixtureAndSchema_MatchTheExactIssue75DtoStateAndEvidenceContract()
    {
        var expected = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(
            TokenSession(1, 100), TokenSession(2, 110), TokenSession(3, 120), TokenSession(4, 300)));
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "TestData", "HistoricalEfficiency");
        var fixtureBytes = Convert.FromBase64String(File.ReadAllText(
            Path.Combine(fixtureRoot, "historical-efficiency-receipt.canonical.base64")).Trim());
        var fixtureSha256 = File.ReadAllText(
            Path.Combine(fixtureRoot, "historical-efficiency-receipt.canonical.sha256")).Trim();
        var restored = HistoricalEfficiencyJsonV1.Deserialize(fixtureBytes);
        using var schema = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(fixtureRoot, "historical-efficiency-receipt.schema.json")));

        Assert.Equal(expected.CanonicalBytes, fixtureBytes);
        Assert.Equal(expected.PayloadSha256, fixtureSha256);
        Assert.Equal(fixtureSha256, Convert.ToHexString(SHA256.HashData(fixtureBytes)).ToLowerInvariant());
        Assert.Equal(fixtureBytes, HistoricalEfficiencyJsonV1.Serialize(restored));
        Assert.Equal(HistoricalEfficiencyAnalysisStateV1.Succeeded, restored.State);
        Assert.Equal(HistoricalEfficiencyContractsV1.ReceiptSchemaVersion,
            schema.RootElement.GetProperty("properties").GetProperty("schema_version").GetProperty("const").GetString());
        Assert.Equal(
            HistoricalEfficiencyDriverRegistryV1.Rules.Select(value => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.Category.ToString())),
            schema.RootElement.GetProperty("$defs").GetProperty("category").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()!));
        Assert.DoesNotContain("\"cost", Encoding.UTF8.GetString(fixtureBytes), StringComparison.OrdinalIgnoreCase);
        Assert.True(ValidateWithPowerShellJsonSchema(fixtureBytes,
            Path.Combine(fixtureRoot, "historical-efficiency-receipt.schema.json")));
        var invalidCostCarrier = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(fixtureBytes)[..^1] + ",\"cost\":1}");
        Assert.False(ValidateWithPowerShellJsonSchema(invalidCostCarrier,
            Path.Combine(fixtureRoot, "historical-efficiency-receipt.schema.json")));
        Assert.Throws<HistoricalEfficiencyValidationException>(() => HistoricalEfficiencyJsonV1.Deserialize(invalidCostCarrier));
    }

    private static HistoricalEfficiencyCategoryCoverageV1 Coverage(
        HistoricalEfficiencyAnalysisV1 result,
        HistoricalEfficiencyDriverCategoryV1 category) =>
        result.Receipt.CategoryCoverage.Single(value => value.Category == category);

    private static SyntheticSession TokenSession(
        int index,
        long total,
        string? quality = "pass",
        SessionCompleteness completeness = SessionCompleteness.Full)
    {
        var session = new SyntheticSession(index, completeness: completeness)
            .AddTurn(1, totalTokens: total, inputTokens: total * 10, outputTokens: total * 10);
        if (quality is not null) session.AddQuality(quality);
        return session;
    }

    private static bool ValidateWithPowerShellJsonSchema(byte[] instance, string schemaPath)
    {
        var instancePath = Path.Combine(Path.GetTempPath(), $"historical-efficiency-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllBytes(instancePath, instance);
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("if (Test-Json -LiteralPath $env:CAO_SCHEMA_INSTANCE -SchemaFile $env:CAO_SCHEMA_FILE -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }");
            startInfo.Environment["CAO_SCHEMA_INSTANCE"] = instancePath;
            startInfo.Environment["CAO_SCHEMA_FILE"] = schemaPath;
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start pwsh for JSON Schema validation.");
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("JSON Schema validation timed out.");
            }
            return process.ExitCode == 0;
        }
        finally
        {
            File.Delete(instancePath);
        }
    }
}
