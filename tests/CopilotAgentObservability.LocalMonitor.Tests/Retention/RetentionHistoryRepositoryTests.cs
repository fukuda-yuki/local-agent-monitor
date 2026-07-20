using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionHistoryRepositoryTests
{
    [Fact]
    public void ReadAuditHistoryPage_OrdersAndResumesExclusivelyAcrossRestart()
    {
        using var fixture = RetentionMutationConfirmationApplicationTests.Fixture.Create();
        var occurredAt = fixture.Time.GetUtcNow();
        var events = new[]
        {
            Audit(fixture, 1, RetentionMutationTargetKind.Item, fixture.ItemId, occurredAt.AddMinutes(-1)),
            Audit(fixture, 2, RetentionMutationTargetKind.Item, fixture.ItemId, occurredAt),
            Audit(fixture, 3, RetentionMutationTargetKind.Item, fixture.ItemId, occurredAt),
            Audit(fixture, 4, RetentionMutationTargetKind.Item, fixture.ItemId, occurredAt.AddMinutes(1)),
            Audit(fixture, 5, RetentionMutationTargetKind.Item, fixture.ItemId, occurredAt.AddMinutes(1)),
        };
        foreach (var auditEvent in events.Reverse()) fixture.Store.AppendAuditEvent(auditEvent);
        var expected = events.OrderByDescending(static item => item.OccurredAt)
            .ThenByDescending(static item => item.EventId, StringComparer.Ordinal).ToArray();
        var target = new RetentionAuditReadTarget(RetentionMutationTargetKind.Item, fixture.ItemId);

        var first = fixture.Store.ReadAuditHistoryPage(target, 2, null);

        Assert.Equal(RetentionAuditHistoryReadDisposition.Found, first.Disposition);
        Assert.Equal(expected[..2].Select(static item => item.EventId), first.Events.Select(static item => item.EventId));
        Assert.NotNull(first.NextCursor);
        Assert.True(RetentionMutationIdentifiers.TryParseHistoryCursor(first.NextCursor, out var cursorNonce));
        Assert.Equal(expected[1].EventId, RetentionMutationIdentifiers.CreateAuditEventId(cursorNonce));

        var reopened = new RetentionCatalogStore(fixture.Path, fixture.Time);
        var second = reopened.ReadAuditHistoryPage(target, 2, first.NextCursor);
        var third = reopened.ReadAuditHistoryPage(target, 2, second.NextCursor);

        Assert.Equal(expected[2..4].Select(static item => item.EventId), second.Events.Select(static item => item.EventId));
        Assert.NotNull(second.NextCursor);
        Assert.Equal(expected[4..].Select(static item => item.EventId), third.Events.Select(static item => item.EventId));
        Assert.Null(third.NextCursor);
        Assert.Equal(expected.Select(static item => item.EventId), first.Events.Concat(second.Events).Concat(third.Events).Select(static item => item.EventId));
    }

    [Fact]
    public void ReadAuditHistoryPage_UsesExactOneHundredAndOneHundredOneBoundaries()
    {
        using (var hundred = RetentionMutationConfirmationApplicationTests.Fixture.Create())
        {
            AppendEvents(hundred, 100);
            var page = hundred.Store.ReadAuditHistoryPage(
                new(RetentionMutationTargetKind.Item, hundred.ItemId), 100, null);

            Assert.Equal(100, page.Events.Count);
            Assert.Null(page.NextCursor);
        }

        using (var hundredOne = RetentionMutationConfirmationApplicationTests.Fixture.Create())
        {
            AppendEvents(hundredOne, 101);
            var target = new RetentionAuditReadTarget(RetentionMutationTargetKind.Item, hundredOne.ItemId);
            var first = hundredOne.Store.ReadAuditHistoryPage(target, 100, null);
            var last = hundredOne.Store.ReadAuditHistoryPage(target, 100, first.NextCursor);

            Assert.Equal(100, first.Events.Count);
            Assert.NotNull(first.NextCursor);
            Assert.Single(last.Events);
            Assert.Null(last.NextCursor);
            Assert.Equal(101, first.Events.Concat(last.Events).Select(static item => item.EventId).Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void ReadAuditHistoryPage_AppliesTargetBeforeCursorValidation()
    {
        using var fixture = RetentionMutationConfirmationApplicationTests.Fixture.Create(itemCount: 2);
        var firstTarget = new RetentionAuditReadTarget(RetentionMutationTargetKind.Item, fixture.ItemIds[0]);
        var secondTarget = new RetentionAuditReadTarget(RetentionMutationTargetKind.Item, fixture.ItemIds[1]);
        var auditEvent = Audit(fixture, 1, RetentionMutationTargetKind.Item, fixture.ItemIds[0], fixture.Time.GetUtcNow());
        fixture.Store.AppendAuditEvent(auditEvent);
        Assert.True(RetentionMutationIdentifiers.TryParseAuditEventId(auditEvent.EventId, out var nonce));
        var cursor = RetentionMutationIdentifiers.CreateHistoryCursor(nonce);

        var empty = fixture.Store.ReadAuditHistoryPage(secondTarget, 100, null);
        var missing = fixture.Store.ReadAuditHistoryPage(
            new(RetentionMutationTargetKind.Item, "missing-item"), 100, "not-a-cursor");
        var malformedSession = fixture.Store.ReadAuditHistoryPage(
            new(RetentionMutationTargetKind.Session, "NOT-A-CANONICAL-SESSION"), 100, "not-a-cursor");
        var malformed = fixture.Store.ReadAuditHistoryPage(firstTarget, 100, "not-a-cursor");
        var foreign = fixture.Store.ReadAuditHistoryPage(secondTarget, 100, cursor);

        Assert.Equal(RetentionAuditHistoryReadDisposition.Found, empty.Disposition);
        Assert.Empty(empty.Events);
        Assert.Null(empty.NextCursor);
        Assert.Equal(RetentionAuditHistoryReadDisposition.TargetNotFound, missing.Disposition);
        Assert.Equal(RetentionAuditHistoryReadDisposition.TargetNotFound, malformedSession.Disposition);
        Assert.Equal(RetentionAuditHistoryReadDisposition.CursorInvalid, malformed.Disposition);
        Assert.Equal(RetentionAuditHistoryReadDisposition.CursorInvalid, foreign.Disposition);
    }

    private static void AppendEvents(RetentionMutationConfirmationApplicationTests.Fixture fixture, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var nonce = BitConverter.GetBytes(index + 1).Concat(new byte[12]).ToArray();
            fixture.Store.AppendAuditEvent(Audit(
                fixture,
                nonce,
                RetentionMutationTargetKind.Item,
                fixture.ItemId,
                fixture.Time.GetUtcNow().AddSeconds(index)));
        }
    }

    private static RetentionAuditEvent Audit(
        RetentionMutationConfirmationApplicationTests.Fixture fixture,
        byte nonce,
        RetentionMutationTargetKind targetKind,
        string targetId,
        DateTimeOffset occurredAt) => Audit(fixture, Enumerable.Repeat(nonce, 16).ToArray(), targetKind, targetId, occurredAt);

    private static RetentionAuditEvent Audit(
        RetentionMutationConfirmationApplicationTests.Fixture fixture,
        byte[] nonce,
        RetentionMutationTargetKind targetKind,
        string targetId,
        DateTimeOffset occurredAt) => new(
            RetentionMutationIdentifiers.CreateAuditEventId(nonce),
            $"operation-{Convert.ToHexString(nonce).ToLowerInvariant()}",
            RetentionMutationConstants.EventType,
            targetKind,
            targetId,
            targetKind == RetentionMutationTargetKind.Session ? targetId : null,
            occurredAt,
            RetentionMutationConstants.ActorLabel,
            RetentionMutationOperation.Pin,
            RetentionMutationReasonCodes.ResearchNeeded,
            null,
            RetentionPinState.Unpinned,
            RetentionPinState.Pinned,
            new(1, 0, 0, 0, 0, 0, 0),
            new(0, 1, 0, 0, 0, 0, 0),
            fixture.WorkflowKey((byte)(nonce[0] % 200 + 1)),
            "v1-" + new string('1', 64),
            "v1-" + new string('2', 64),
            "sha256-" + new string('3', 64),
            RetentionMutationCompletionCodes.PinApplied,
            null);
}
