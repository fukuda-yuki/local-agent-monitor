using System.Collections;
using System.Runtime.InteropServices;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalArchiveValidationTests
{
    private const string TargetOne = "01890f65-4c31-7f42-8a7d-111111111111";
    private const string TargetTwo = "01890f65-4c31-7f42-8a7d-222222222222";
    private const string EventOne = "01890f65-4c31-7f42-8a7d-333333333333";
    private const string EventTwo = "01890f65-4c31-7f42-8a7d-444444444444";
    private const string FirstTime = "2026-08-09T12:34:56.1234567+00:00";
    private const string SecondTime = "2026-08-09T12:34:55.1234567+00:00";

    [Fact]
    public void TryFreezeMutationSuccess_CopiesHostileCarrierInOriginalOrder()
    {
        var first = Archived(TargetOne, 1, FirstTime);
        var second = Archived(TargetTwo, 3, SecondTime);
        var carrier = new OneReadList<LocalArchiveMutationTargetSuccess>(first, second);

        var accepted = LocalArchiveValidation.TryFreezeMutationSuccess(
            LocalArchiveAction.Archive,
            LocalArchiveTargetKind.Session,
            carrier,
            out var frozen);

        Assert.True(accepted);
        Assert.NotNull(frozen);
        Assert.Equal(1, carrier.CountReads);
        Assert.Equal([1, 1], carrier.ItemReads);
        Assert.Equal([TargetOne, TargetTwo], frozen.Targets.Select(target => target.TargetId));
        carrier.Replace(0, Active(TargetOne, 2, SecondTime));
        Assert.Same(first, frozen.Targets[0]);
        Assert.IsType<LocalArchiveMutationTargetSuccess[]>(frozen.Targets);
    }

    [Theory]
    [InlineData("active_odd")]
    [InlineData("active_archived_at")]
    [InlineData("noncanonical_timestamp")]
    [InlineData("archived_even")]
    [InlineData("archived_timestamp_mismatch")]
    [InlineData("noncanonical_id")]
    [InlineData("undefined_state")]
    public void TryFreezeMutationSuccess_RejectsInvalidFactParity(string mutation)
    {
        var target = mutation switch
        {
            "active_odd" => Active(TargetOne, 1, FirstTime),
            "active_archived_at" => Active(TargetOne, 2, FirstTime) with { ArchivedAt = FirstTime },
            "noncanonical_timestamp" => Active(TargetOne, 2, "2026-08-09T12:34:56.1234567Z"),
            "archived_even" => Archived(TargetOne, 2, FirstTime),
            "archived_timestamp_mismatch" => Archived(TargetOne, 1, FirstTime) with { UpdatedAt = SecondTime },
            "noncanonical_id" => Archived(TargetOne.ToUpperInvariant(), 1, FirstTime),
            "undefined_state" => new(TargetOne, (LocalArchiveState)99, 1, FirstTime, FirstTime),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Assert.False(LocalArchiveValidation.TryFreezeMutationSuccess(
            LocalArchiveAction.Archive,
            LocalArchiveTargetKind.Session,
            [target],
            out var frozen));
        Assert.Null(frozen);
    }

    [Fact]
    public void TryFreezeMutationSuccess_RejectsDuplicateAndWrongCardinality()
    {
        Assert.False(LocalArchiveValidation.TryFreezeMutationSuccess(
            LocalArchiveAction.Archive,
            LocalArchiveTargetKind.Session,
            [Archived(TargetOne, 1, FirstTime), Archived(TargetOne, 1, FirstTime)],
            out _));
        Assert.False(LocalArchiveValidation.TryFreezeMutationSuccess(
            LocalArchiveAction.Archive,
            LocalArchiveTargetKind.Repository,
            [Archived(TargetOne, 1, FirstTime), Archived(TargetTwo, 1, FirstTime)],
            out _));
    }

    [Fact]
    public void TryFreezeAndValidateHistory_AcceptsCompleteAlternatingChainWithTimestampReordering()
    {
        var current = Active(TargetOne, 2, SecondTime);
        var events = new OneReadList<LocalArchiveStoredEvent>(
            Event(EventOne, LocalArchiveAction.Archive, 0, 1, FirstTime),
            Event(EventTwo, LocalArchiveAction.Restore, 1, 2, SecondTime));

        var accepted = LocalArchiveValidation.TryFreezeAndValidateHistory(
            LocalArchiveTargetKind.Session,
            current,
            events,
            out var frozen);

        Assert.True(accepted);
        Assert.Equal(1, events.CountReads);
        Assert.Equal([1, 1], events.ItemReads);
        Assert.Equal([EventOne, EventTwo], frozen.Select(item => item.EventId));
        Assert.IsType<LocalArchiveStoredEvent[]>(frozen);
    }

    [Theory]
    [InlineData("wrong_first_action")]
    [InlineData("revision_gap")]
    [InlineData("wrong_head_action")]
    [InlineData("wrong_head_timestamp")]
    [InlineData("wrong_target")]
    [InlineData("noncanonical_event_id")]
    public void TryFreezeAndValidateHistory_RejectsContradictoryChain(string mutation)
    {
        var first = Event(EventOne, LocalArchiveAction.Archive, 0, 1, FirstTime);
        var second = Event(EventTwo, LocalArchiveAction.Restore, 1, 2, SecondTime);
        (first, second) = mutation switch
        {
            "wrong_first_action" => (first with { Action = LocalArchiveAction.Restore }, second),
            "revision_gap" => (first, second with { PreviousRevision = 0 }),
            "wrong_head_action" => (first, second with { Action = LocalArchiveAction.Archive }),
            "wrong_head_timestamp" => (first, second with { OccurredAt = FirstTime }),
            "wrong_target" => (first, second with { TargetId = TargetTwo }),
            "noncanonical_event_id" => (first with { EventId = EventOne.ToUpperInvariant() }, second),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        Assert.False(LocalArchiveValidation.TryFreezeAndValidateHistory(
            LocalArchiveTargetKind.Session,
            Active(TargetOne, 2, SecondTime),
            [first, second],
            out var frozen));
        Assert.Empty(frozen);
    }

    [Fact]
    public void TryFreezeAndValidateHistory_AcceptsAbsentCurrentOnlyWithoutEvents()
    {
        Assert.True(LocalArchiveValidation.TryFreezeAndValidateHistory(
            LocalArchiveTargetKind.Repository,
            Active(TargetOne, 0, null),
            [],
            out var frozen));
        Assert.Empty(frozen);
    }

    [Fact]
    public void IsValidCurrentAndHead_AcceptsStoredHeadAndAbsentCurrentWithoutHead()
    {
        Assert.True(LocalArchiveValidation.IsValidCurrentAndHead(
            LocalArchiveTargetKind.Session,
            Archived(TargetOne, 1, FirstTime),
            Event(EventOne, LocalArchiveAction.Archive, 0, 1, FirstTime)));
        Assert.True(LocalArchiveValidation.IsValidCurrentAndHead(
            LocalArchiveTargetKind.Repository,
            Active(TargetOne, 0, null),
            null));
    }

    [Fact]
    public void IsValidCurrentAndHead_RejectsMissingOrContradictoryHead()
    {
        var current = Active(TargetOne, 2, SecondTime);
        var head = Event(EventTwo, LocalArchiveAction.Restore, 1, 2, SecondTime);

        Assert.False(LocalArchiveValidation.IsValidCurrentAndHead(
            LocalArchiveTargetKind.Session,
            current,
            null));
        Assert.False(LocalArchiveValidation.IsValidCurrentAndHead(
            LocalArchiveTargetKind.Session,
            current,
            head with { NewRevision = 3 }));
        Assert.False(LocalArchiveValidation.IsValidCurrentAndHead(
            LocalArchiveTargetKind.Repository,
            current,
            head));
        Assert.False(LocalArchiveValidation.IsValidCurrentAndHead(
            LocalArchiveTargetKind.Session,
            Active(TargetOne, 0, null),
            head));
    }

    [Fact]
    public void TryCopySuccessEntity_CopiesMemoryAndRejectsEmptyEntity()
    {
        byte[] callerBytes = [1, 2, 3];

        Assert.True(LocalArchiveValidation.TryCopySuccessEntity(callerBytes, out var owned));
        callerBytes[0] = 9;

        Assert.Equal([1, 2, 3], owned.Entity.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(owned.Entity, out var ownedSegment));
        Assert.NotSame(callerBytes, ownedSegment.Array);
        Assert.False(LocalArchiveValidation.TryCopySuccessEntity(ReadOnlyMemory<byte>.Empty, out _));
    }

    private static LocalArchiveMutationTargetSuccess Active(string id, long revision, string? updatedAt) =>
        new(id, LocalArchiveState.Active, revision, null, updatedAt);

    private static LocalArchiveMutationTargetSuccess Archived(string id, long revision, string timestamp) =>
        new(id, LocalArchiveState.Archived, revision, timestamp, timestamp);

    private static LocalArchiveStoredEvent Event(
        string eventId,
        LocalArchiveAction action,
        long previousRevision,
        long newRevision,
        string occurredAt) =>
        new(eventId, LocalArchiveTargetKind.Session, TargetOne, action, previousRevision, newRevision, occurredAt);

    private sealed class OneReadList<T>(params T[] items) : IReadOnlyList<T>
    {
        private readonly T[] items = items;
        private readonly int[] itemReads = new int[items.Length];

        internal int CountReads { get; private set; }
        internal IReadOnlyList<int> ItemReads => itemReads;

        public int Count
        {
            get
            {
                CountReads++;
                if (CountReads > 1)
                    throw new InvalidOperationException("Count read more than once.");
                return items.Length;
            }
        }

        public T this[int index]
        {
            get
            {
                itemReads[index]++;
                if (itemReads[index] > 1)
                    throw new InvalidOperationException("Item read more than once.");
                return items[index];
            }
        }

        internal void Replace(int index, T value) => items[index] = value;

        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Enumeration is not allowed.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
