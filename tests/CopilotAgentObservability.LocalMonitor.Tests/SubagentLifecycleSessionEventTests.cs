using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SubagentLifecycleSessionEventTests
{
    private static readonly LifecycleFixture[] LifecycleFixtures =
    [
        new(
            "synthetic-subagent-started",
            "subagent.started",
            "2026-07-11T00:00:00Z",
            """{"lifecycle":"subagent.started","description":"started token=remove-me","api_key":"remove-me"}""",
            """{"lifecycle":"subagent.started","description":"started token=[REDACTED]"}"""),
        new(
            "synthetic-subagent-completed",
            "subagent.completed",
            "2026-07-11T00:01:00Z",
            """{"lifecycle":"subagent.completed","description":"completed token=remove-me","api_key":"remove-me"}""",
            """{"lifecycle":"subagent.completed","description":"completed token=[REDACTED]"}"""),
        new(
            "synthetic-subagent-failed",
            "subagent.failed",
            "2026-07-11T00:02:00Z",
            """{"lifecycle":"subagent.failed","description":"failed token=remove-me","api_key":"remove-me"}""",
            """{"lifecycle":"subagent.failed","description":"failed token=[REDACTED]"}"""),
        new(
            "synthetic-subagent-selected",
            "subagent.selected",
            "2026-07-11T00:03:00Z",
            """{"lifecycle":"subagent.selected","description":"selected token=remove-me","api_key":"remove-me"}""",
            """{"lifecycle":"subagent.selected","description":"selected token=[REDACTED]"}"""),
        new(
            "synthetic-subagent-deselected",
            "subagent.deselected",
            "2026-07-11T00:04:00Z",
            """{"lifecycle":"subagent.deselected","description":"deselected token=remove-me","api_key":"remove-me"}""",
            """{"lifecycle":"subagent.deselected","description":"deselected token=[REDACTED]"}"""),
    ];

    [Fact]
    public async Task FiveExactSdkLifecycleTypesRemainDistinctAvailableEventsWithTheirFilteredPayloads()
    {
        using var temp = CreateTempDirectory();
        var store = CreateStore(temp);

        new SessionEventNormalizer(store, temp.TimeProvider).NormalizeAndWrite(
            Envelope(LifecycleEvents()));

        var session = ResolveSession(store);
        var detail = Assert.IsType<SessionDetail>(store.GetDetail(session.SessionId));
        Assert.Equal(
            LifecycleFixtures.Select(item => item.EventType),
            detail.Events.Select(item => item.Type));

        var persistedContent = new List<string>();
        foreach (var fixture in LifecycleFixtures)
        {
            var persisted = Assert.Single(detail.Events, item => item.Type == fixture.EventType);
            Assert.Equal(fixture.SourceEventId, persisted.SourceEventId);
            Assert.Equal(SessionContentState.Available, persisted.ContentState);

            var content = await ReadContentAsync(store, session.SessionId, persisted.EventId);
            Assert.Equal("application/json", content.ContentKind);
            Assert.Equal(fixture.FilteredPayload, content.ContentJson);
            persistedContent.Add(content.ContentJson);
        }

        Assert.Equal(5, persistedContent.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(0, UnsupportedCount(store));
    }

    [Fact]
    public async Task SubagentFailedAloneStaysAvailableWithoutEndingTheSession()
    {
        using var temp = CreateTempDirectory();
        var store = CreateStore(temp);
        var failed = Assert.Single(LifecycleFixtures, item => item.EventType == "subagent.failed");

        new SessionEventNormalizer(store, temp.TimeProvider).NormalizeAndWrite(
            Envelope([failed.ToEvent()]));

        var session = ResolveSession(store);
        Assert.Equal(ObservedSessionStatus.Active, session.Status);
        Assert.Null(session.EndedAt);
        var persisted = Assert.Single(
            Assert.IsType<SessionDetail>(store.GetDetail(session.SessionId)).Events);
        Assert.Equal("subagent.failed", persisted.Type);
        Assert.Equal(SessionContentState.Available, persisted.ContentState);
        Assert.Equal(
            failed.FilteredPayload,
            (await ReadContentAsync(store, session.SessionId, persisted.EventId)).ContentJson);
        Assert.Equal(0, UnsupportedCount(store));
    }

    [Fact]
    public async Task OnlyGenuinelyNewUnknownOrCaseVariantEventsIncrementUnsupportedCount()
    {
        using var temp = CreateTempDirectory();
        var time = Assert.IsType<MutableTimeProvider>(temp.TimeProvider);
        var store = CreateStore(temp);
        var normalizer = new SessionEventNormalizer(store, time);
        var unknown = Event(
            "synthetic-subagent-unknown",
            "subagent.cancelled",
            "2026-07-11T00:05:00Z",
            """{"lifecycle":"unknown"}""");
        var caseVariant = Event(
            "synthetic-subagent-case-variant",
            "Subagent.Failed",
            "2026-07-11T00:06:00Z",
            """{"lifecycle":"case-variant"}""");
        var failed = Assert.Single(LifecycleFixtures, item => item.EventType == "subagent.failed");

        normalizer.NormalizeAndWrite(Envelope([unknown]));
        Assert.Equal(1, UnsupportedCount(store));

        time.Advance(TimeSpan.FromMinutes(1));
        normalizer.NormalizeAndWrite(Envelope([unknown]));
        Assert.Equal(1, UnsupportedCount(store));

        normalizer.NormalizeAndWrite(Envelope([failed.ToEvent()]));
        Assert.Equal(1, UnsupportedCount(store));

        normalizer.NormalizeAndWrite(Envelope([caseVariant]));
        Assert.Equal(2, UnsupportedCount(store));

        var session = ResolveSession(store);
        var detail = Assert.IsType<SessionDetail>(store.GetDetail(session.SessionId));
        var unknownPersisted = Assert.Single(detail.Events, item => item.SourceEventId == "synthetic-subagent-unknown");
        var failedPersisted = Assert.Single(detail.Events, item => item.SourceEventId == failed.SourceEventId);
        var caseVariantPersisted = Assert.Single(detail.Events, item => item.SourceEventId == "synthetic-subagent-case-variant");
        Assert.Equal(SessionContentState.Unsupported, unknownPersisted.ContentState);
        Assert.Equal(SessionContentState.Available, failedPersisted.ContentState);
        Assert.Equal(SessionContentState.Unsupported, caseVariantPersisted.ContentState);
        await AssertContentNotFoundAsync(store, session.SessionId, unknownPersisted.EventId);
        await ReadContentAsync(store, session.SessionId, failedPersisted.EventId);
        await AssertContentNotFoundAsync(store, session.SessionId, caseVariantPersisted.EventId);
    }

    [Fact]
    public async Task ArrivalPermutationsAndBatchSplitsKeepTheSameCanonicalLifecycleEventAndContentSet()
    {
        int[][][] batchShapes =
        [
            [[0, 1, 2, 3, 4]],
            [[4, 3, 2, 1, 0]],
            [[2, 4, 0, 3, 1]],
            [[0], [1, 2, 3, 4]],
            [[0, 1], [2, 3], [4]],
            [[4], [1, 3], [0, 2]],
            [[2], [0], [4], [1], [3]],
        ];

        var expected = await PersistScenarioAsync(batchShapes[0]);
        Assert.Equal(ObservedSessionStatus.Active, expected.Status);
        Assert.Null(expected.EndedAt);
        Assert.Equal(0, expected.UnsupportedEventVersionCount);

        foreach (var batchShape in batchShapes.Skip(1))
        {
            var actual = await PersistScenarioAsync(batchShape);
            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(expected.EndedAt, actual.EndedAt);
            Assert.Equal(expected.UnsupportedEventVersionCount, actual.UnsupportedEventVersionCount);
            Assert.Equal(expected.Events, actual.Events);
        }
    }

    [Fact]
    public async Task DuplicateFailedReplayAfterTimeAdvanceKeepsOneByteIdenticalEventAndContent()
    {
        using var temp = CreateTempDirectory();
        var time = Assert.IsType<MutableTimeProvider>(temp.TimeProvider);
        var store = CreateStore(temp);
        var normalizer = new SessionEventNormalizer(store, time);
        var failed = Assert.Single(LifecycleFixtures, item => item.EventType == "subagent.failed");
        var envelope = Envelope([failed.ToEvent()]);
        normalizer.NormalizeAndWrite(envelope);
        var session = ResolveSession(store);
        var firstEvent = Assert.Single(
            Assert.IsType<SessionDetail>(store.GetDetail(session.SessionId)).Events);
        var firstContent = await ReadContentAsync(store, session.SessionId, firstEvent.EventId);

        time.Advance(TimeSpan.FromMinutes(1));
        normalizer.NormalizeAndWrite(envelope);

        var detail = Assert.IsType<SessionDetail>(store.GetDetail(session.SessionId));
        var replayed = Assert.Single(detail.Events);
        Assert.Equal(firstEvent, replayed);
        Assert.Equal(
            firstContent,
            await ReadContentAsync(store, session.SessionId, replayed.EventId));
        Assert.Equal(ObservedSessionStatus.Active, detail.Session.Status);
        Assert.Null(detail.Session.EndedAt);
        Assert.Equal(0, UnsupportedCount(store));
    }

    [Theory]
    [InlineData("subagent.failed")]
    [InlineData("subagent.selected")]
    [InlineData("subagent.deselected")]
    public async Task ReplayOfPreexistingUnsupportedLifecycleEventDoesNotUpgradeOrBackfill(
        string eventType)
    {
        using var temp = CreateTempDirectory();
        var store = CreateStore(temp);
        var fixture = Assert.Single(LifecycleFixtures, item => item.EventType == eventType);
        var existing = ExistingUnsupportedLifecycleEvent(fixture);
        store.Write(existing);
        var persistedBeforeReplay = Assert.Single(
            Assert.IsType<SessionDetail>(store.GetDetail(existing.Detail.Session.SessionId)).Events);

        new SessionEventNormalizer(store, temp.TimeProvider).NormalizeAndWrite(
            Envelope([fixture.ToEvent()]));

        var detail = Assert.IsType<SessionDetail>(store.GetDetail(existing.Detail.Session.SessionId));
        var persistedAfterReplay = Assert.Single(detail.Events);
        Assert.Equal(persistedBeforeReplay, persistedAfterReplay);
        Assert.Equal(SessionContentState.Unsupported, persistedAfterReplay.ContentState);
        await AssertContentNotFoundAsync(
            store,
            detail.Session.SessionId,
            persistedAfterReplay.EventId);
        Assert.Equal(0, UnsupportedCount(store));
    }

    private static async Task<CanonicalResult> PersistScenarioAsync(int[][] batches)
    {
        using var temp = CreateTempDirectory();
        var store = CreateStore(temp);
        var normalizer = new SessionEventNormalizer(store, temp.TimeProvider);
        var events = LifecycleEvents();
        foreach (var batch in batches)
        {
            normalizer.NormalizeAndWrite(
                Envelope(batch.Select(index => events[index]).ToArray()));
        }

        var session = ResolveSession(store);
        var detail = Assert.IsType<SessionDetail>(store.GetDetail(session.SessionId));
        var persisted = new List<CanonicalEvent>();
        foreach (var item in detail.Events)
        {
            var content = await ReadContentAsync(store, session.SessionId, item.EventId);
            persisted.Add(new(
                item.SourceEventId,
                item.Type,
                item.OccurredAt,
                item.ContentState,
                content.ContentKind,
                content.ContentJson));
        }

        return new(
            detail.Session.Status,
            detail.Session.EndedAt,
            persisted.ToArray(),
            UnsupportedCount(store));
    }

    private static async Task<SessionEventContent> ReadContentAsync(
        ISessionStore store,
        Guid sessionId,
        Guid eventId)
    {
        var result = await store.ReadContentAsync(sessionId, eventId, CancellationToken.None);
        Assert.Equal(SessionContentReadDisposition.Granted, result.Disposition);
        Assert.NotNull(result.Lease);
        await using var lease = result.Lease!;
        return lease.Content;
    }

    private static async Task AssertContentNotFoundAsync(
        ISessionStore store,
        Guid sessionId,
        Guid eventId)
    {
        var result = await store.ReadContentAsync(sessionId, eventId, CancellationToken.None);
        Assert.Equal(SessionContentReadDisposition.NotFound, result.Disposition);
        Assert.Null(result.Lease);
    }

    private static MonitorTempDirectory CreateTempDirectory() => new()
    {
        TimeProvider = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-11T00:10:00Z")),
    };

    private static SqliteSessionStore CreateStore(MonitorTempDirectory temp)
    {
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        return store;
    }

    private static ObservedSession ResolveSession(ISessionStore store) =>
        Assert.IsType<ObservedSession>(
            store.Resolve(SessionSourceSurface.CopilotSdk, "synthetic-sdk-session"));

    private static long UnsupportedCount(ISessionStore store) =>
        store.GetProjectionState("session-normalizer")?.UnsupportedEventVersionCount ?? 0;

    private static SessionIngestEnvelope Envelope(IReadOnlyList<SessionIngestEvent> events) => new(
        1,
        "copilot-sdk-stream",
        "copilot-sdk",
        "synthetic-sdk-session",
        events);

    private static SessionIngestEvent[] LifecycleEvents() =>
        LifecycleFixtures.Select(item => item.ToEvent()).ToArray();

    private static SessionIngestEvent Event(
        string sourceEventId,
        string type,
        string occurredAt,
        string payload) => new(
        sourceEventId,
        type,
        occurredAt,
        JsonDocument.Parse(payload).RootElement.Clone());

    private static SessionWriteBatch ExistingUnsupportedLifecycleEvent(LifecycleFixture fixture)
    {
        var occurredAt = DateTimeOffset.Parse(fixture.OccurredAt);
        var sessionId = Guid.CreateVersion7();
        var session = new ObservedSession(
            sessionId,
            ObservedSessionStatus.Active,
            SessionCompleteness.Partial,
            null,
            null,
            null,
            null,
            occurredAt,
            SessionRawRetentionState.NotCaptured,
            occurredAt,
            occurredAt);
        var @event = new ObservedSessionEvent(
            Guid.CreateVersion7(),
            sessionId,
            null,
            SessionSourceSurface.CopilotSdk,
            null,
            null,
            null,
            "copilot-sdk-stream",
            fixture.SourceEventId,
            fixture.EventType,
            occurredAt,
            SessionContentState.Unsupported);
        return new(
            new SessionDetail(
                session,
                [new SessionNativeId(
                    sessionId,
                    SessionSourceSurface.CopilotSdk,
                    "synthetic-sdk-session",
                    SessionBindingKind.Native,
                    occurredAt)],
                [],
                [@event]),
            []);
    }

    private sealed record LifecycleFixture(
        string SourceEventId,
        string EventType,
        string OccurredAt,
        string Payload,
        string FilteredPayload)
    {
        public SessionIngestEvent ToEvent() =>
            Event(SourceEventId, EventType, OccurredAt, Payload);
    }

    private sealed record CanonicalEvent(
        string SourceEventId,
        string Type,
        DateTimeOffset OccurredAt,
        SessionContentState ContentState,
        string ContentKind,
        string ContentJson);

    private sealed record CanonicalResult(
        ObservedSessionStatus Status,
        DateTimeOffset? EndedAt,
        CanonicalEvent[] Events,
        long UnsupportedEventVersionCount);
}
