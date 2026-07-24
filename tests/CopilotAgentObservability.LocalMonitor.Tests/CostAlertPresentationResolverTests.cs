using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.LocalMonitor.Alerts;
using CopilotAgentObservability.Persistence.Sqlite.Pricing;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class CostAlertPresentationResolverTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
    private static readonly DateTimeOffset CalculatedAt = ObservedAt.AddSeconds(2);
    private const string SessionId = "01984045-9d80-7000-8000-000000000001";
    private const string EstimateId =
        "pricing-estimate-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Resolve_ProjectsExactSessionAndStrictlyReloadedEstimate()
    {
        using var temp = new MonitorTempDirectory();
        var sessions = SessionStore(temp, SessionRawRetentionState.Expiring);
        var estimates = new FixtureEstimateStore(ExactEstimate());
        var resolver = new CostAlertPresentationResolverV1(
            sessions,
            estimates,
            ReadOnlyMemory<byte>.Empty);

        var result = resolver.Resolve([Member()], Evidence());

        Assert.Equal("success", result.State);
        var member = Assert.Single(result.Members);
        Assert.Equal("available", member.SessionEvidenceState);
        Assert.Equal("repo-safe", member.Repository);
        Assert.Equal("workspace-safe", member.Workspace);
        Assert.Equal("available", member.ScopeState);
        Assert.Equal("available", member.EstimateEvidenceState);
        Assert.Equal(
            $"/costs?session_id={SessionId}&estimate_id={EstimateId}",
            member.EstimateHref);
        Assert.Equal(1, estimates.ReadCount);
    }

    [Fact]
    public void Resolve_PreservesExpiredSessionButRejectsUnsafeScopeLabels()
    {
        using var temp = new MonitorTempDirectory();
        var sessions = SessionStore(
            temp,
            SessionRawRetentionState.ExpiredPendingDeletion,
            "C:\\private\\repository",
            "workspace-safe");
        var resolver = new CostAlertPresentationResolverV1(
            sessions,
            new FixtureEstimateStore(ExactEstimate()),
            ReadOnlyMemory<byte>.Empty);

        var result = resolver.Resolve([Member()], Evidence());

        var member = Assert.Single(result.Members);
        Assert.Equal("expired", member.SessionEvidenceState);
        Assert.Null(member.Repository);
        Assert.Null(member.Workspace);
        Assert.Equal("unavailable", member.ScopeState);
        Assert.Equal("available", member.EstimateEvidenceState);
    }

    [Fact]
    public void Resolve_FailsClosedOnSessionTimeOrEstimateOwnershipMismatch()
    {
        using var temp = new MonitorTempDirectory();
        var sessions = SessionStore(temp, SessionRawRetentionState.Expiring);
        var wrongTime = Member() with
        {
            SessionEffectiveAtUtc = ObservedAt.AddSeconds(1),
        };
        var resolver = new CostAlertPresentationResolverV1(
            sessions,
            new FixtureEstimateStore(ExactEstimate() with
            {
                Item = ExactEstimate().Item with
                {
                    CatalogSha256 = new string('b', 64),
                },
            }),
            ReadOnlyMemory<byte>.Empty);

        var timeResult = resolver.Resolve(
            [wrongTime],
            [
                new(
                    AlertEvidenceKindV2.Session,
                    SessionId,
                    SessionId,
                    ObservedAt.AddSeconds(1)),
                new(
                    AlertEvidenceKindV2.PricingEstimate,
                    EstimateId,
                    SessionId,
                    CalculatedAt),
            ]);
        var estimateResult = resolver.Resolve([Member()], Evidence());

        Assert.Equal("unavailable", timeResult.State);
        Assert.Empty(timeResult.Members);
        Assert.Equal("unavailable", estimateResult.State);
        Assert.Empty(estimateResult.Members);
    }

    [Fact]
    public void Resolve_RejectsNoncanonicalEvidenceOrderAndOverBoundInput()
    {
        using var temp = new MonitorTempDirectory();
        var resolver = new CostAlertPresentationResolverV1(
            SessionStore(temp, SessionRawRetentionState.Expiring),
            new FixtureEstimateStore(ExactEstimate()),
            ReadOnlyMemory<byte>.Empty);

        var wrongOrder = resolver.Resolve([Member()], Evidence().Reverse().ToArray());
        var overBound = resolver.Resolve(
            Enumerable.Repeat(Member(), 2_001).ToArray(),
            []);

        Assert.Equal("unavailable", wrongOrder.State);
        Assert.Empty(wrongOrder.Members);
        Assert.Equal("unavailable", overBound.State);
        Assert.Empty(overBound.Members);
    }

    [Fact]
    public void Resolve_ProjectsMissingEstimateWithoutADeepLink()
    {
        using var temp = new MonitorTempDirectory();
        var resolver = new CostAlertPresentationResolverV1(
            SessionStore(temp, SessionRawRetentionState.Expiring),
            new FixtureEstimateStore(PricingReadStatus.NotFound),
            ReadOnlyMemory<byte>.Empty);

        var result = resolver.Resolve([Member()], Evidence());

        Assert.Equal("success", result.State);
        var member = Assert.Single(result.Members);
        Assert.Equal("available", member.SessionEvidenceState);
        Assert.Equal("missing", member.EstimateEvidenceState);
        Assert.Null(member.EstimateHref);
    }

    [Fact]
    public void Resolve_FailsClosedWhenReloadedEstimateStatusDoesNotMatchMember()
    {
        using var temp = new MonitorTempDirectory();
        var exact = ExactEstimate();
        var resolver = new CostAlertPresentationResolverV1(
            SessionStore(temp, SessionRawRetentionState.Expiring),
            new FixtureEstimateStore(exact with
            {
                Item = exact.Item with { EstimateStatus = "partial" },
            }),
            ReadOnlyMemory<byte>.Empty);

        var result = resolver.Resolve([Member()], Evidence());

        Assert.Equal("unavailable", result.State);
        Assert.Empty(result.Members);
    }

    [Theory]
    [InlineData(PricingReadStatus.Busy, "busy")]
    [InlineData(PricingReadStatus.Unavailable, "unavailable")]
    public void Resolve_MapsEstimateStoreFailureWithoutPartialMembers(
        PricingReadStatus status,
        string expected)
    {
        using var temp = new MonitorTempDirectory();
        var resolver = new CostAlertPresentationResolverV1(
            SessionStore(temp, SessionRawRetentionState.Expiring),
            new FixtureEstimateStore(status),
            ReadOnlyMemory<byte>.Empty);

        var result = resolver.Resolve([Member()], Evidence());

        Assert.Equal(expected, result.State);
        Assert.Empty(result.Members);
    }

    [Theory]
    [InlineData(PricingReadStatus.Busy, "busy")]
    [InlineData(PricingReadStatus.Unavailable, "unavailable")]
    public void Resolve_PropagatesEstimateStoreFailureWhenSessionIsMissing(
        PricingReadStatus status,
        string expected)
    {
        using var temp = new MonitorTempDirectory();
        var sessions = new SqliteSessionStore(
            temp.DatabasePath,
            temp.RetentionContext,
            temp.TimeProvider);
        sessions.CreateSchema();
        var resolver = new CostAlertPresentationResolverV1(
            sessions,
            new FixtureEstimateStore(status),
            ReadOnlyMemory<byte>.Empty);

        var result = resolver.Resolve([Member()], Evidence());

        Assert.Equal(expected, result.State);
        Assert.Empty(result.Members);
    }

    [Fact]
    public void Resolve_AcceptsImmutableEstimateIdentityAfterDynamicFreshnessBecomesStale()
    {
        using var temp = new MonitorTempDirectory();
        var exact = ExactEstimate();
        var resolver = new CostAlertPresentationResolverV1(
            SessionStore(temp, SessionRawRetentionState.Expiring),
            new FixtureEstimateStore(exact with
            {
                Item = exact.Item with
                {
                    Freshness = "stale",
                    Amount = null,
                    Currency = null,
                },
            }),
            ReadOnlyMemory<byte>.Empty);

        var result = resolver.Resolve([Member()], Evidence());

        Assert.Equal("success", result.State);
        Assert.Equal("available", Assert.Single(result.Members).EstimateEvidenceState);
    }

    [Fact]
    public void Resolve_DoesNotReturnPrivateFailureText()
    {
        using var temp = new MonitorTempDirectory();
        var resolver = new CostAlertPresentationResolverV1(
            SessionStore(temp, SessionRawRetentionState.Expiring),
            new ThrowingEstimateStore(),
            ReadOnlyMemory<byte>.Empty);

        var result = resolver.Resolve([Member()], Evidence());
        var serialized = System.Text.Json.JsonSerializer.Serialize(result);

        Assert.Equal("unavailable", result.State);
        Assert.Empty(result.Members);
        Assert.DoesNotContain("C:\\private\\pricing.db", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_RejectsEvidenceCardinalityAndMalformedEstimateIdentity()
    {
        using var temp = new MonitorTempDirectory();
        var sessions = new SqliteSessionStore(
            temp.DatabasePath,
            temp.RetentionContext,
            temp.TimeProvider);
        sessions.CreateSchema();
        var resolver = new CostAlertPresentationResolverV1(
            sessions,
            new FixtureEstimateStore(PricingReadStatus.NotFound),
            ReadOnlyMemory<byte>.Empty);
        var malformed = Member() with { EstimateId = "pricing-estimate-private-path" };

        var missingEvidence = resolver.Resolve([Member()], Evidence()[..1]);
        var malformedIdentity = resolver.Resolve(
            [malformed],
            [
                Evidence()[0],
                Evidence()[1] with { EvidenceId = malformed.EstimateId! },
            ]);

        Assert.Equal("unavailable", missingEvidence.State);
        Assert.Empty(missingEvidence.Members);
        Assert.Equal("unavailable", malformedIdentity.State);
        Assert.Empty(malformedIdentity.Members);
    }

    [Fact]
    public void Resolve_RejectsEmptyDuplicateEstimateAndInvalidStateShape()
    {
        using var temp = new MonitorTempDirectory();
        var sessions = new SqliteSessionStore(
            temp.DatabasePath,
            temp.RetentionContext,
            temp.TimeProvider);
        sessions.CreateSchema();
        var resolver = new CostAlertPresentationResolverV1(
            sessions,
            new FixtureEstimateStore(PricingReadStatus.NotFound),
            ReadOnlyMemory<byte>.Empty);
        const string secondSession = "01984045-9d80-7000-8000-000000000002";
        var second = Member() with
        {
            SessionId = secondSession,
            SessionEffectiveAtUtc = ObservedAt.AddSeconds(1),
            SessionUpdatedAtUtc = ObservedAt.AddSeconds(2),
        };
        var duplicateEvidence = new AlertEvidenceReferenceV2[]
        {
            new(AlertEvidenceKindV2.Session, SessionId, SessionId, ObservedAt),
            new(
                AlertEvidenceKindV2.Session,
                secondSession,
                secondSession,
                ObservedAt.AddSeconds(1)),
            new(AlertEvidenceKindV2.PricingEstimate, EstimateId, SessionId, CalculatedAt),
            new(
                AlertEvidenceKindV2.PricingEstimate,
                EstimateId,
                secondSession,
                CalculatedAt),
        };
        var invalidMissing = Member() with
        {
            State = AlertCostMemberStateV2.Missing,
            AttemptRevision = 0,
            AttemptResultKind = null,
            HeadRevision = 1,
            EstimateId = null,
            EstimateCalculationTimeUtc = null,
            Amount = null,
            Currency = null,
        };

        var empty = resolver.Resolve([], []);
        var duplicate = resolver.Resolve([Member(), second], duplicateEvidence);
        var invalidShape = resolver.Resolve(
            [invalidMissing],
            [new(AlertEvidenceKindV2.Session, SessionId, SessionId, ObservedAt)]);

        Assert.Equal("unavailable", empty.State);
        Assert.Equal("unavailable", duplicate.State);
        Assert.Equal("unavailable", invalidShape.State);
    }

    private static SqliteSessionStore SessionStore(
        MonitorTempDirectory temp,
        SessionRawRetentionState retentionState,
        string? repository = "repo-safe",
        string? workspace = "workspace-safe")
    {
        var store = new SqliteSessionStore(
            temp.DatabasePath,
            temp.RetentionContext,
            temp.TimeProvider);
        store.CreateSchema();
        var sessionId = Guid.ParseExact(SessionId, "D");
        store.Write(new SessionWriteBatch(
            new SessionDetail(
                new(
                    sessionId,
                    ObservedSessionStatus.Completed,
                    SessionCompleteness.Full,
                    repository,
                    workspace,
                    ObservedAt.AddMinutes(-1),
                    ObservedAt,
                    ObservedAt,
                    retentionState,
                    ObservedAt.AddMinutes(-1),
                    ObservedAt.AddSeconds(1)),
                [],
                [],
                []),
            []));
        return store;
    }

    private static AlertCostMemberV2 Member() => new(
        SessionId,
        ObservedAt,
        ObservedAt.AddSeconds(1),
        "github-copilot",
        "1.2.3",
        AlertCostMemberStateV2.Estimated,
        1,
        AlertCostAttemptResultKindV2.Estimate,
        null,
        1,
        EstimateId,
        CalculatedAt,
        new string('c', 64),
        "pricing-registry-v1",
        "github",
        "gpt-5",
        "api",
        2m,
        "USD");

    private static AlertEvidenceReferenceV2[] Evidence() =>
    [
        new(AlertEvidenceKindV2.Session, SessionId, SessionId, ObservedAt),
        new(
            AlertEvidenceKindV2.PricingEstimate,
            EstimateId,
            SessionId,
            CalculatedAt),
    ];

    private static CostSessionEstimateReadV1 ExactEstimate() => new(
        SessionId,
        1,
        EstimateId,
        new(
            1,
            EstimateId,
            null,
            CalculatedAt,
            ObservedAt,
            "estimated",
            "fresh",
            "complete_total",
            2m,
            "USD",
            "github",
            "gpt-5",
            "api",
            "token",
            new string('c', 64),
            "cost-configuration-" + new string('d', 64),
            new(
                "pricing-registry-v1",
                "bundled",
                "public",
                "Public",
                "entry",
                ObservedAt.AddDays(-1),
                null,
                DateOnly.FromDateTime(ObservedAt.Date),
                DateOnly.FromDateTime(ObservedAt.Date.AddDays(30)),
                "USD",
                null),
            [],
            new([], [], []),
            [],
            new("not_applicable", null, null, null, []),
            "estimated_cost_not_invoice.v1"));

    private sealed class FixtureEstimateStore : ICostAlertEstimateReadStoreV1
    {
        private readonly PricingReadStatus status;
        private readonly CostSessionEstimateReadV1? estimate;

        internal FixtureEstimateStore(CostSessionEstimateReadV1 estimate)
        {
            status = PricingReadStatus.Success;
            this.estimate = estimate;
        }

        internal FixtureEstimateStore(PricingReadStatus status)
        {
            this.status = status;
        }

        internal int ReadCount { get; private set; }

        public PricingReadResult<CostSessionEstimateReadV1> ReadSessionEstimate(
            string sessionId,
            string estimateId,
            ReadOnlyMemory<byte> currentProviderCatalogBytes)
        {
            ReadCount++;
            return new(status, estimate);
        }
    }

    private sealed class ThrowingEstimateStore : ICostAlertEstimateReadStoreV1
    {
        public PricingReadResult<CostSessionEstimateReadV1> ReadSessionEstimate(
            string sessionId,
            string estimateId,
            ReadOnlyMemory<byte> currentProviderCatalogBytes) =>
            throw new InvalidOperationException("C:\\private\\pricing.db");
    }
}
