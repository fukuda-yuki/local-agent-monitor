using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvokedSessionEventTests
{
    private const string FilteredSkillPayload =
        """{"name":"synthetic-skill","path":"skills/synthetic/SKILL.md","description":"synthetic description token=[REDACTED]","pluginName":"synthetic-plugin","source":"synthetic-source","trigger":"synthetic-trigger"}""";

    [Fact]
    public async Task SkillInvoked_IsUnsupportedAndStoresNoContentWithOneUnsupportedIncrement()
    {
        using var temp = CreateTempDirectory();
        var store = CreateStore(temp);

        new SessionEventNormalizer(store, temp.TimeProvider).NormalizeAndWrite(
            Envelope([SkillInvokedEvent()]));

        var session = Assert.IsType<ObservedSession>(
            store.Resolve(SessionSourceSurface.CopilotSdk, "synthetic-sdk-session"));
        var detail = Assert.IsType<SessionDetail>(store.GetDetail(session.SessionId));
        var persisted = Assert.Single(detail.Events);
        Assert.Equal("skill.invoked", persisted.Type);
        Assert.Equal(SessionContentState.Unsupported, persisted.ContentState);

        var content = await store.ReadContentAsync(session.SessionId, persisted.EventId, CancellationToken.None);
        Assert.Equal(SessionContentReadDisposition.NotFound, content.Disposition);
        Assert.Null(content.Lease);
        Assert.Equal(1L, store.GetProjectionState("session-normalizer")?.UnsupportedEventVersionCount);
    }

    [Theory]
    [InlineData("skill.started")]
    [InlineData("skill.completed")]
    public async Task SkillLifecycleType_IsAvailableAndStoresFilteredContent(string eventType)
    {
        using var temp = CreateTempDirectory();
        var store = CreateStore(temp);

        new SessionEventNormalizer(store, temp.TimeProvider).NormalizeAndWrite(
            Envelope([SkillInvokedEvent() with { Type = eventType }]));

        var session = Assert.IsType<ObservedSession>(
            store.Resolve(SessionSourceSurface.CopilotSdk, "synthetic-sdk-session"));
        var detail = Assert.IsType<SessionDetail>(store.GetDetail(session.SessionId));
        var persisted = Assert.Single(detail.Events);
        Assert.Equal(eventType, persisted.Type);
        Assert.Equal(SessionContentState.Available, persisted.ContentState);
        var content = await store.ReadContentAsync(session.SessionId, persisted.EventId, CancellationToken.None);
        Assert.Equal(SessionContentReadDisposition.Granted, content.Disposition);
        Assert.NotNull(content.Lease);
        await using var lease = content.Lease!;
        using (var reference = lease.AcquireContentReference())
        {
            Assert.Equal("application/json", reference.Content.ContentKind);
            Assert.Equal(FilteredSkillPayload, reference.Content.ContentJson);
        }
        Assert.Equal(0, store.GetProjectionState("session-normalizer")?.UnsupportedEventVersionCount ?? 0);
    }

    [Fact]
    public async Task SupportedSkillLifecycle_DivergentReplayAfterTimeAdvanceFailsClosedKeepingOneAvailableByteIdenticalContent()
    {
        using var temp = CreateTempDirectory();
        var time = Assert.IsType<MutableTimeProvider>(temp.TimeProvider);
        var store = CreateStore(temp);
        var normalizer = new SessionEventNormalizer(store, time);
        var envelope = Envelope([SkillInvokedEvent() with { Type = "skill.started" }]);
        normalizer.NormalizeAndWrite(envelope);
        var session = Assert.IsType<ObservedSession>(
            store.Resolve(SessionSourceSurface.CopilotSdk, "synthetic-sdk-session"));
        var firstEvent = Assert.Single(
            Assert.IsType<SessionDetail>(store.GetDetail(session.SessionId)).Events);
        var firstContent = await ReadContentAsync(store, session.SessionId, firstEvent.EventId);

        time.Advance(TimeSpan.FromMinutes(1));

        // D079 clause F: a replay of the same source event carrying divergent captured
        // content must fail closed — the whole batch aborts instead of backfilling or
        // silently replacing the original capture. The original bytes stay untouched.
        Assert.Throws<InvalidOperationException>(() =>
            normalizer.NormalizeAndWrite(Envelope([
                SkillInvokedEvent() with
                {
                    Type = "skill.started",
                    Payload = JsonDocument.Parse(
                        """{"name":"replacement-skill","description":"replacement payload"}""").RootElement.Clone(),
                },
            ])));

        var persisted = Assert.Single(
            Assert.IsType<SessionDetail>(store.GetDetail(session.SessionId)).Events);
        Assert.Equal("synthetic-skill-event", persisted.SourceEventId);
        Assert.Equal("skill.started", persisted.Type);
        Assert.Equal(SessionContentState.Available, persisted.ContentState);
        Assert.Equal(firstEvent, persisted);
        Assert.Equal(
            firstContent,
            await ReadContentAsync(store, session.SessionId, persisted.EventId));
        Assert.Equal(0, store.GetProjectionState("session-normalizer")?.UnsupportedEventVersionCount ?? 0);
    }

    [Fact]
    public void SupportedSkillLifecycle_ConflictingExactSourceReplayFailsClosed()
    {
        using var temp = CreateTempDirectory();
        var store = CreateStore(temp);
        var normalizer = new SessionEventNormalizer(store, temp.TimeProvider);
        normalizer.NormalizeAndWrite(Envelope([SkillInvokedEvent() with { Type = "skill.started" }]));

        Assert.ThrowsAny<InvalidOperationException>(() =>
            normalizer.NormalizeAndWrite(
                Envelope([SkillInvokedEvent() with { Type = "skill.started" }]) with
                {
                    NativeSessionId = "different-sdk-session",
                }));
    }

    [Fact]
    public async Task SkillInvoked_ReplayOfExistingUnsupportedEventDoesNotUpgradeOrBackfillContent()
    {
        using var temp = CreateTempDirectory();
        var store = CreateStore(temp);
        var existing = ExistingUnsupportedSkillInvoked();
        store.Write(existing);
        var persistedBeforeReplay = Assert.Single(
            Assert.IsType<SessionDetail>(store.GetDetail(existing.Detail.Session.SessionId)).Events);

        new SessionEventNormalizer(store, temp.TimeProvider).NormalizeAndWrite(
            Envelope([SkillInvokedEvent()]));

        var detail = Assert.IsType<SessionDetail>(store.GetDetail(existing.Detail.Session.SessionId));
        var persistedAfterReplay = Assert.Single(detail.Events);
        Assert.Equal(persistedBeforeReplay, persistedAfterReplay);
        Assert.Equal(SessionContentState.Unsupported, persistedAfterReplay.ContentState);
        var content = await store.ReadContentAsync(
            detail.Session.SessionId,
            persistedAfterReplay.EventId,
            CancellationToken.None);
        Assert.Equal(SessionContentReadDisposition.NotFound, content.Disposition);
        Assert.Null(content.Lease);
        Assert.Equal(0, store.GetProjectionState("session-normalizer")?.UnsupportedEventVersionCount ?? 0);
    }

    [Fact]
    public async Task SkillInvoked_ArrivalPermutationsAndBatchSplitsKeepCanonicalEventContent()
    {
        int[][][] batchShapes =
        [
            [[0, 1, 2]],
            [[0, 2, 1]],
            [[1, 0, 2]],
            [[1, 2, 0]],
            [[2, 0, 1]],
            [[2, 1, 0]],
            [[0], [1, 2]],
            [[0, 1], [2]],
            [[2], [1], [0]],
            [[1], [2, 0]],
        ];

        var expected = await PersistScenarioAsync(batchShapes[0]);
        Assert.Equal(1, expected.UnsupportedEventVersionCount);
        var expectedSkill = Assert.Single(expected.Events, item => item.Type == "skill.invoked");
        Assert.Equal(SessionContentState.Unsupported, expectedSkill.ContentState);
        Assert.Null(expectedSkill.ContentJson);

        foreach (var batchShape in batchShapes.Skip(1))
        {
            var actual = await PersistScenarioAsync(batchShape);
            Assert.Equal(expected.UnsupportedEventVersionCount, actual.UnsupportedEventVersionCount);
            Assert.Equal(expected.Events, actual.Events);
        }
    }

    private static async Task<CanonicalResult> PersistScenarioAsync(int[][] batches)
    {
        using var temp = CreateTempDirectory();
        var store = CreateStore(temp);
        var normalizer = new SessionEventNormalizer(store, temp.TimeProvider);
        var events = ScenarioEvents();
        foreach (var batch in batches)
        {
            normalizer.NormalizeAndWrite(Envelope(batch.Select(index => events[index]).ToArray()));
        }

        var session = Assert.IsType<ObservedSession>(
            store.Resolve(SessionSourceSurface.CopilotSdk, "synthetic-sdk-session"));
        var detail = Assert.IsType<SessionDetail>(store.GetDetail(session.SessionId));
        var persisted = new List<CanonicalEvent>();
        foreach (var item in detail.Events)
        {
            var content = await store.ReadContentAsync(session.SessionId, item.EventId, CancellationToken.None);
            if (item.ContentState == SessionContentState.Unsupported)
            {
                Assert.Equal(SessionContentReadDisposition.NotFound, content.Disposition);
                Assert.Null(content.Lease);
                persisted.Add(new(
                    item.SourceEventId,
                    item.Type,
                    item.OccurredAt,
                    item.ContentState,
                    null,
                    null));
            }
            else
            {
                Assert.Equal(SessionContentReadDisposition.Granted, content.Disposition);
                Assert.NotNull(content.Lease);
                await using var lease = content.Lease!;
                using (var reference = lease.AcquireContentReference())
                {
                    persisted.Add(new(
                        item.SourceEventId,
                        item.Type,
                        item.OccurredAt,
                        item.ContentState,
                        reference.Content.ContentKind,
                        reference.Content.ContentJson));
                }
            }
        }

        return new(
            persisted.ToArray(),
            store.GetProjectionState("session-normalizer")?.UnsupportedEventVersionCount ?? 0);
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
        using var reference = lease.AcquireContentReference();
        return reference.Content;
    }

    private static MonitorTempDirectory CreateTempDirectory() => new()
    {
        TimeProvider = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-11T00:03:00Z")),
    };

    private static SqliteSessionStore CreateStore(MonitorTempDirectory temp)
    {
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        return store;
    }

    private static SessionIngestEnvelope Envelope(IReadOnlyList<SessionIngestEvent> events) => new(
        1,
        "copilot-sdk-stream",
        "copilot-sdk",
        "synthetic-sdk-session",
        events);

    private static SessionWriteBatch ExistingUnsupportedSkillInvoked()
    {
        var occurredAt = DateTimeOffset.Parse("2026-07-11T00:01:00Z");
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
            "synthetic-skill-event",
            "skill.invoked",
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

    private static SessionIngestEvent[] ScenarioEvents() =>
    [
        Event("synthetic-start-event", "session.start", "2026-07-11T00:00:00Z", """{"phase":"start"}"""),
        SkillInvokedEvent(),
        Event("synthetic-end-event", "session.task_complete", "2026-07-11T00:02:00Z", """{"phase":"end"}"""),
    ];

    private static SessionIngestEvent SkillInvokedEvent() => Event(
        "synthetic-skill-event",
        "skill.invoked",
        "2026-07-11T00:01:00Z",
        """
        {
          "name": "synthetic-skill",
          "path": "skills/synthetic/SKILL.md",
          "description": "synthetic description token=remove-me",
          "pluginName": "synthetic-plugin",
          "source": "synthetic-source",
          "trigger": "synthetic-trigger",
          "api_key": "remove-me"
        }
        """);

    private static SessionIngestEvent Event(string sourceEventId, string type, string occurredAt, string payload) => new(
        sourceEventId,
        type,
        occurredAt,
        JsonDocument.Parse(payload).RootElement.Clone());

    private sealed record CanonicalEvent(
        string SourceEventId,
        string Type,
        DateTimeOffset OccurredAt,
        SessionContentState ContentState,
        string? ContentKind,
        string? ContentJson);

    private sealed record CanonicalResult(
        CanonicalEvent[] Events,
        long UnsupportedEventVersionCount);
}
