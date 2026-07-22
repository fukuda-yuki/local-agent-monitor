using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

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
        Assert.Equal(extraction.RepositorySafe.ExtractionId, driver.ExtractionId);
        Assert.Equal(extraction.RepositorySafeSha256, driver.ExtractionSha256);
        var differentExtractionDriver = driver with
        {
            DriverId = string.Empty,
            ExtractionSha256 = new string('a', 64),
        };
        differentExtractionDriver = differentExtractionDriver with
        {
            DriverId = HistoricalEfficiencyAnalyzerV1.CalculateDriverId(differentExtractionDriver),
        };
        Assert.NotEqual(driver.DriverId, differentExtractionDriver.DriverId);
        Assert.Throws<HistoricalEfficiencyValidationException>(() => HistoricalEfficiencyJsonV1.Serialize(
            first.Receipt with { Drivers = [differentExtractionDriver] }));
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
    public void Analyze_ZeroContextDenominator_IsInsufficientMissingMetric()
    {
        var session = new SyntheticSession(1)
            .AddTurn(1, inputTokens: 0)
            .AddTurn(2, inputTokens: 1)
            .AddTurn(3, inputTokens: 2)
            .AddQuality("pass");

        var result = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(session));

        Assert.DoesNotContain(result.Receipt.Drivers,
            value => value.Category == HistoricalEfficiencyDriverCategoryV1.ContextGrowth);
        var coverage = Coverage(result, HistoricalEfficiencyDriverCategoryV1.ContextGrowth);
        Assert.Equal(HistoricalEfficiencyCoverageStateV1.Insufficient, coverage.State);
        Assert.Equal([HistoricalEfficiencyCoverageReasonV1.MissingRequiredMetric], coverage.Reasons);
    }

    [Fact]
    public void Analyze_ZeroCacheDenominator_IsInsufficientMissingMetric()
    {
        var session = new SyntheticSession(1)
            .AddTurn(1, inputTokens: 100, cacheReadTokens: 0)
            .AddTurn(2, inputTokens: 0, cacheReadTokens: 0)
            .AddTurn(3, inputTokens: 0, cacheReadTokens: 0)
            .AddQuality("pass");

        var result = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(session));

        Assert.DoesNotContain(result.Receipt.Drivers,
            value => value.Category == HistoricalEfficiencyDriverCategoryV1.CacheInefficiency);
        var coverage = Coverage(result, HistoricalEfficiencyDriverCategoryV1.CacheInefficiency);
        Assert.Equal(HistoricalEfficiencyCoverageStateV1.Insufficient, coverage.State);
        Assert.Equal([HistoricalEfficiencyCoverageReasonV1.MissingRequiredMetric], coverage.Reasons);
    }

    [Fact]
    public void Analyze_PositiveCacheInputBelowThreshold_IsCompleteNoMatch()
    {
        var session = new SyntheticSession(1)
            .AddTurn(1, inputTokens: 6_000, cacheReadTokens: 0)
            .AddTurn(2, inputTokens: 4_000, cacheReadTokens: 0)
            .AddTurn(3, inputTokens: 5_000, cacheReadTokens: 0)
            .AddQuality("pass");

        var result = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(session));

        Assert.DoesNotContain(result.Receipt.Drivers,
            value => value.Category == HistoricalEfficiencyDriverCategoryV1.CacheInefficiency);
        var coverage = Coverage(result, HistoricalEfficiencyDriverCategoryV1.CacheInefficiency);
        Assert.Equal(HistoricalEfficiencyCoverageStateV1.NoMatch, coverage.State);
        Assert.Equal([HistoricalEfficiencyCoverageReasonV1.NoThresholdMatch], coverage.Reasons);
    }

    [Fact]
    public void Analyze_ContextMatchWithAnotherMissingInput_RemainsSupported()
    {
        var session = new SyntheticSession(1)
            .AddTurn(1)
            .AddTurn(2, inputTokens: 100)
            .AddTurn(3, inputTokens: 150)
            .AddTurn(4, inputTokens: 200)
            .AddQuality("pass");

        var result = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(session));

        var driver = Assert.Single(result.Receipt.Drivers,
            value => value.Category == HistoricalEfficiencyDriverCategoryV1.ContextGrowth);
        Assert.Equal(HistoricalEfficiencyDriverVerdictV1.Supported, driver.Verdict);
        Assert.Equal(3, driver.EvidenceRefs.Count);
    }

    [Fact]
    public void Analyze_CacheMatchWithAnotherMissingScalar_RemainsSupported()
    {
        var session = new SyntheticSession(1)
            .AddTurn(1, inputTokens: 5_000)
            .AddTurn(2, inputTokens: 5_000, cacheReadTokens: 0)
            .AddTurn(3, inputTokens: 6_000, cacheReadTokens: 0)
            .AddTurn(4, inputTokens: 6_000, cacheReadTokens: 0)
            .AddQuality("pass");

        var result = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(session));

        var driver = Assert.Single(result.Receipt.Drivers,
            value => value.Category == HistoricalEfficiencyDriverCategoryV1.CacheInefficiency);
        Assert.Equal(HistoricalEfficiencyDriverVerdictV1.Supported, driver.Verdict);
        Assert.Equal(2, driver.EvidenceRefs.Count);
    }

    [Fact]
    public void Analyze_Frozen72UnavailableOperationalCategories_StayExplicitlyUnavailable()
    {
        var session = new SyntheticSession(1)
            .AddRetry(3)
            .AddErrorSpan()
            .AddQuality("pass");

        var result = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(session));

        var retry = Assert.Single(result.Receipt.Drivers, value => value.Category == HistoricalEfficiencyDriverCategoryV1.RetryOverhead);
        Assert.Equal(3m, retry.ObservedValues.Single(value => value.Name == "attempt_count").Value);
        Assert.Equal(2m, retry.ObservedValues.Single(value => value.Name == "retry_overhead").Value);
        Assert.Equal(3, retry.EvidenceRefs.Count);
        AssertUnavailable(HistoricalEfficiencyDriverCategoryV1.ToolCallVolume, "ProducerAuthoredRepeatedCallIdentityUnavailable");
        AssertUnavailable(HistoricalEfficiencyDriverCategoryV1.ToolFailureOverhead, "ExactToolFailureStatusUnavailable");
        AssertUnavailable(HistoricalEfficiencyDriverCategoryV1.PermissionWait, "PermissionWaitDurationUnavailable");
        AssertUnavailable(HistoricalEfficiencyDriverCategoryV1.SubagentFanout, "ExactSubagentOwnershipUnavailable");

        void AssertUnavailable(HistoricalEfficiencyDriverCategoryV1 category, string reason)
        {
            Assert.DoesNotContain(result.Receipt.Drivers, value => value.Category == category);
            var coverage = Coverage(result, category);
            Assert.Equal(HistoricalEfficiencyCoverageStateV1.Unavailable, coverage.State);
            Assert.Equal(reason, Assert.Single(coverage.Reasons).ToString());
            Assert.Equal(0, coverage.EligibleSessionCount);
        }
    }

    [Fact]
    public void Analyze_DuplicateRetryIdentity_IsExcludedWithoutInventedOverhead()
    {
        var session = new SyntheticSession(1)
            .AddRetry(3)
            .AddDuplicateRetry(3)
            .AddQuality("pass");

        var result = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(session));

        var retry = Assert.Single(result.Receipt.Drivers, value => value.Category == HistoricalEfficiencyDriverCategoryV1.RetryOverhead);
        Assert.Equal(3m, retry.ObservedValues.Single(value => value.Name == "attempt_count").Value);
    }

    [Fact]
    public void Analyze_OneRetryChainBelowAttemptThreshold_IsCompleteNoMatch()
    {
        var result = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(
            new SyntheticSession(1).AddRetry(1).AddQuality("pass")));

        var coverage = Coverage(result, HistoricalEfficiencyDriverCategoryV1.RetryOverhead);

        Assert.DoesNotContain(result.Receipt.Drivers,
            value => value.Category == HistoricalEfficiencyDriverCategoryV1.RetryOverhead);
        Assert.Equal(HistoricalEfficiencyCoverageStateV1.NoMatch, coverage.State);
        Assert.Equal(1, coverage.ObservedSampleCount);
        Assert.Equal(1, coverage.MinimumSample);
        Assert.Equal([HistoricalEfficiencyCoverageReasonV1.NoThresholdMatch], coverage.Reasons);
    }

    [Fact]
    public void Analyze_TokenMatchWithExcludedMetricAndDimension_IsIncomplete()
    {
        var missingMetric = new SyntheticSession(5).AddTurn(1, inputTokens: 50).AddQuality("pass");
        var missingDimension = new SyntheticSession(6, model: null).AddTurn(1, totalTokens: 50).AddQuality("pass");
        var result = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(
            TokenSession(1, 100), TokenSession(2, 110), TokenSession(3, 120), TokenSession(4, 300),
            missingMetric, missingDimension));

        var driver = Assert.Single(result.Receipt.Drivers, value => value.Category == HistoricalEfficiencyDriverCategoryV1.TokenVolume);

        Assert.Equal(HistoricalEfficiencyDriverVerdictV1.Incomplete, driver.Verdict);
    }

    [Fact]
    public void Analyze_DurationMatchWithExcludedMetricAndDimension_IsIncomplete()
    {
        static SyntheticSession DurationSession(int index, long duration, int? model = 1) =>
            new SyntheticSession(index, model: model).AddDuration(duration).AddQuality("pass");
        var result = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(
            DurationSession(1, 100), DurationSession(2, 110), DurationSession(3, 120), DurationSession(4, 300),
            new SyntheticSession(5).AddQuality("pass"), DurationSession(6, 50, model: null)));

        var driver = Assert.Single(result.Receipt.Drivers, value => value.Category == HistoricalEfficiencyDriverCategoryV1.DurationOutlier);

        Assert.Equal(HistoricalEfficiencyDriverVerdictV1.Incomplete, driver.Verdict);
    }

    [Fact]
    public void Analyze_ZeroTokenDenominator_IsInsufficientMissingMetric()
    {
        var result = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(
            TokenSession(1, 0), TokenSession(2, 0), TokenSession(3, 0), TokenSession(4, 300)));

        var coverage = Coverage(result, HistoricalEfficiencyDriverCategoryV1.TokenVolume);

        Assert.Equal(HistoricalEfficiencyCoverageStateV1.Insufficient, coverage.State);
        Assert.Equal([HistoricalEfficiencyCoverageReasonV1.MissingRequiredMetric], coverage.Reasons);
    }

    [Fact]
    public void Analyze_ZeroDurationDenominator_IsInsufficientMissingMetric()
    {
        static SyntheticSession DurationSession(int index, long duration) =>
            new SyntheticSession(index).AddDuration(duration).AddQuality("pass");
        var result = HistoricalEfficiencyAnalyzerV1.Analyze(HistoricalEfficiencyTestData.Extraction(
            DurationSession(1, 0), DurationSession(2, 0), DurationSession(3, 0), DurationSession(4, 300)));

        var coverage = Coverage(result, HistoricalEfficiencyDriverCategoryV1.DurationOutlier);

        Assert.Equal(HistoricalEfficiencyCoverageStateV1.Insufficient, coverage.State);
        Assert.Equal([HistoricalEfficiencyCoverageReasonV1.MissingRequiredMetric], coverage.Reasons);
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
            Assert.Equal(extraction.RepositorySafe.ExtractionId, driver.ExtractionId);
            Assert.Equal(extraction.RepositorySafeSha256, driver.ExtractionSha256);
            Assert.All(driver.SourceSessions, session => Assert.Contains(session, availableSessions));
            Assert.All(driver.EvidenceRefs, reference => Assert.Contains(reference, availableReferences));
            Assert.All(driver.QualityEvidenceRefs, reference => Assert.Contains(reference, availableReferences));
            Assert.Equal(driver.EvidenceRefs, driver.Mitigation.EvidenceRefs);
        });
    }

    [Fact]
    public async Task Analyze_PersistedIssue72PairAfterSourceStoresDisappear_UsesOnlyExactHandoff()
    {
        var source = new MonitorTempDirectory();
        var historyPath = Path.Combine(Path.GetTempPath(), $"historical-efficiency-{Guid.NewGuid():N}.sqlite");
        try
        {
            HistoricalEvidenceExtractionV1 produced;
            var now = new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
            var sessionId = Guid.Parse("018f0000-0000-7000-8000-000000000074");
            const string traceId = "74000000000000000000000000000000";
            const string chatSpanId = "7400000000000001";
            const string failedSpanId = "7400000000000002";
            const string recoveredSpanId = "7400000000000003";
            using (var app = MonitorHost.Build(
                new MonitorOptions(source.DatabasePath, "http://127.0.0.1:0", true, 31_457_280),
                new MonitorHostTestOptions
                {
                    StartWriter = false,
                    StartProjectionWorker = false,
                    StartSessionWriter = false,
                    StartSessionOtelEnrichment = false,
                    UseUserSecrets = false,
                }))
            {
                var sessionStore = app.Services.GetRequiredService<ISessionStore>();
                var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
                    "owner/repository", null, now, now.AddSeconds(1), now.AddSeconds(1), SessionRawRetentionState.NotCaptured,
                    now, now);
                var spanIds = new[] { chatSpanId, failedSpanId, recoveredSpanId };
                var events = spanIds.Select((spanId, index) => new ObservedSessionEvent(
                    Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.ClaudeCode, null, traceId, "ok",
                    "claude-code-otel", $"{traceId}/{spanId}", "otel.span", now.AddTicks(index),
                    SessionContentState.NotCaptured, MatchKind: SessionMatchKind.ExactNative)).ToArray();
                sessionStore.Write(new(new(session, [], [], events), []));
                InsertPersistedHandoffSpans(source.DatabasePath, traceId, chatSpanId, failedSpanId, recoveredSpanId);

                produced = await app.Services.GetRequiredService<HistoricalEvidenceApplicationServiceV1>()
                    .CreateAsync(HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [sessionId]), CancellationToken.None);
            }

            var historyStore = new SqliteHistoricalEvidenceDatasetStoreV1(historyPath);
            historyStore.CreateSchema();
            historyStore.Save(produced, now);
            File.Delete(source.DatabasePath);
            Assert.False(File.Exists(source.DatabasePath));

            var persisted = Assert.IsType<HistoricalEvidenceExtractionV1>(historyStore.Get(produced.RawLocal.ExtractionId));
            Assert.False(persisted.RawLocalBytes.SequenceEqual(persisted.RepositorySafeBytes));

            var result = HistoricalEfficiencyAnalyzerV1.Analyze(persisted);
            var driver = Assert.Single(result.Receipt.Drivers,
                value => value.Category == HistoricalEfficiencyDriverCategoryV1.RetryOverhead);

            Assert.Equal(persisted.RepositorySafe.ExtractionId, driver.ExtractionId);
            Assert.Equal(persisted.RepositorySafeSha256, driver.ExtractionSha256);
            Assert.Equal(persisted.RepositorySafeSha256, result.Receipt.ExtractionSha256);
            Assert.Equal(2m, driver.ObservedValues.Single(value => value.Name == "attempt_count").Value);
        }
        finally
        {
            source.Dispose();
            File.Delete(historyPath);
        }
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
        Assert.All(restored.Drivers, driver =>
        {
            Assert.Equal(restored.ExtractionId, driver.ExtractionId);
            Assert.Equal(restored.ExtractionSha256, driver.ExtractionSha256);
        });
        Assert.Equal(HistoricalEfficiencyContractsV1.ReceiptSchemaVersion,
            schema.RootElement.GetProperty("properties").GetProperty("schema_version").GetProperty("const").GetString());
        Assert.Equal(
            HistoricalEfficiencyDriverRegistryV1.Rules.Select(value => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.Category.ToString())),
            schema.RootElement.GetProperty("$defs").GetProperty("category").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()!));
        var requiredDriverProperties = schema.RootElement.GetProperty("$defs").GetProperty("driver").GetProperty("required")
            .EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("extraction_id", requiredDriverProperties);
        Assert.Contains("extraction_sha256", requiredDriverProperties);
        var review = File.ReadAllText(Path.Combine(fixtureRoot, "historical-efficiency-receipt.review.md"));
        Assert.Contains($"Payload SHA-256: `{fixtureSha256}`", review, StringComparison.Ordinal);
        Assert.Contains("Verdict: `supported`", review, StringComparison.Ordinal);
        Assert.Contains("Repository safety: PASS", review, StringComparison.Ordinal);
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

    private static void InsertPersistedHandoffSpans(
        string databasePath,
        string traceId,
        string chatSpanId,
        string failedSpanId,
        string recoveredSpanId)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        var rows = new[]
        {
            (Ordinal: 1, SpanId: chatSpanId, Operation: "chat", ToolName: (string?)null, Status: (string?)null, Error: (string?)null),
            (Ordinal: 2, SpanId: failedSpanId, Operation: (string?)null, ToolName: "shell", Status: "error", Error: "failed"),
            (Ordinal: 3, SpanId: recoveredSpanId, Operation: (string?)null, ToolName: "shell", Status: "ok", Error: (string?)null),
        };
        foreach (var row in rows)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,span_ordinal,operation,tool_name,status,error_type,projected_at)
                VALUES($raw,$trace,$span,$ordinal,$operation,$tool,$status,$error,$projected);
                """;
            command.Parameters.AddWithValue("$raw", row.Ordinal);
            command.Parameters.AddWithValue("$trace", traceId);
            command.Parameters.AddWithValue("$span", row.SpanId);
            command.Parameters.AddWithValue("$ordinal", row.Ordinal);
            command.Parameters.AddWithValue("$operation", (object?)row.Operation ?? DBNull.Value);
            command.Parameters.AddWithValue("$tool", (object?)row.ToolName ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", (object?)row.Status ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)row.Error ?? DBNull.Value);
            command.Parameters.AddWithValue("$projected", "2026-07-23T00:00:00.0000000+00:00");
            Assert.Equal(1, command.ExecuteNonQuery());
        }
    }
}
