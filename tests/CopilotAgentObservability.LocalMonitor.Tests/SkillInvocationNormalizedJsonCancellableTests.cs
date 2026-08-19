using System.Text;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using GitHub.Copilot;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvocationNormalizedJsonCancellableTests
{
    [Fact]
    public void TryWriteCancellable_MatchesTryWriteByteForByte()
    {
        foreach (var sourceEvent in new[] { CompleteEvent(), RequiredOnlyEvent() })
        {
            Assert.True(SkillInvocationNormalizedJsonV1.TryWrite("native-session", sourceEvent, out var expected));
            Assert.True(
                SkillInvocationNormalizedJsonV1.TryWriteCancellable("native-session", sourceEvent, CancellationToken.None, out var actual));

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void TryWriteCancellable_NastyContent_MatchesTryWriteByteForByte()
    {
        var sourceEvent = RequiredOnlyEvent();
        sourceEvent.Data.Content =
            "quote\"back\\slash \u00e9\u4e2d\u3042 \ud83d\ude00 emoji " +
            "\u0001\u001f\b\f\n\r\t plus+angle<br>&amp'quote \u007f del \u0000 nul";

        Assert.True(SkillInvocationNormalizedJsonV1.TryWrite("native-session", sourceEvent, out var expected));
        Assert.True(
            SkillInvocationNormalizedJsonV1.TryWriteCancellable("native-session", sourceEvent, CancellationToken.None, out var actual));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryWriteCancellable_SurrogatePairAtEscapeChunkBoundary_MatchesTryWriteByteForByte()
    {
        var sourceEvent = RequiredOnlyEvent();
        // The pair straddles the 65,536-char escape chunk boundary; the chunker must back up
        // one char instead of splitting the pair into two replacement scalars.
        sourceEvent.Data.Content = new string('a', 65_535) + "\ud83d\ude00" + new string('b', 65_537);

        Assert.True(SkillInvocationNormalizedJsonV1.TryWrite("native-session", sourceEvent, out var expected));
        Assert.True(
            SkillInvocationNormalizedJsonV1.TryWriteCancellable("native-session", sourceEvent, CancellationToken.None, out var actual));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryWriteCancellable_MapperUnavailable_ReturnsFalseWithoutBody()
    {
        var written = SkillInvocationNormalizedJsonV1.TryWriteCancellable(null, RequiredOnlyEvent(), CancellationToken.None, out var body);

        Assert.False(written);
        Assert.Null(body);
    }

    [Fact]
    public void TryWriteCancellable_AlreadyCancelled_ReturnsFalseWithoutBody()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var written = SkillInvocationNormalizedJsonV1.TryWriteCancellable("native-session", CompleteEvent(), cancellation.Token, out var body);

        Assert.False(written);
        Assert.Null(body);
    }

    [Fact]
    public void TryWriteCancellable_BodyExactlyAtProducerLimit_Succeeds()
    {
        var contentLength = ContentLengthForTotalBytes(SkillInvocationNormalizedJsonV1.MaxProducerBodyBytes);
        var sourceEvent = RequiredOnlyEvent();
        sourceEvent.Data.Content = new string('a', contentLength);

        var written = SkillInvocationNormalizedJsonV1.TryWriteCancellable("native-session", sourceEvent, CancellationToken.None, out var body);

        Assert.True(written);
        Assert.Equal(SkillInvocationNormalizedJsonV1.MaxProducerBodyBytes, Assert.IsType<byte[]>(body).Length);

        Assert.True(SkillInvocationNormalizedJsonV1.TryWrite("native-session", sourceEvent, out var unbounded));
        Assert.Equal(unbounded, body);
    }

    [Fact]
    public void TryWriteCancellable_BodyOneByteOverProducerLimit_StopsWithoutBody()
    {
        var contentLength = ContentLengthForTotalBytes(SkillInvocationNormalizedJsonV1.MaxProducerBodyBytes + 1);
        var sourceEvent = RequiredOnlyEvent();
        sourceEvent.Data.Content = new string('a', contentLength);

        var written = SkillInvocationNormalizedJsonV1.TryWriteCancellable("native-session", sourceEvent, CancellationToken.None, out var body);

        Assert.False(written);
        Assert.Null(body);
    }

    [Fact]
    public void TryWrite_ProducerLimitPlusOne_StillSucceedsUnbounded()
    {
        // The unbounded v2 ingest path keeps its historical behavior; only the cancellable
        // producer path enforces the 8,388,608-byte stopping length.
        var contentLength = ContentLengthForTotalBytes(SkillInvocationNormalizedJsonV1.MaxProducerBodyBytes + 1);
        var sourceEvent = RequiredOnlyEvent();
        sourceEvent.Data.Content = new string('a', contentLength);

        var written = SkillInvocationNormalizedJsonV1.TryWrite("native-session", sourceEvent, out var body);

        Assert.True(written);
        Assert.Equal(SkillInvocationNormalizedJsonV1.MaxProducerBodyBytes + 1, Assert.IsType<byte[]>(body).Length);
    }

    private static int ContentLengthForTotalBytes(int totalBytes)
    {
        var baselineEvent = RequiredOnlyEvent();
        baselineEvent.Data.Content = "a";
        Assert.True(SkillInvocationNormalizedJsonV1.TryWrite("native-session", baselineEvent, out var baselineBody));
        return totalBytes - Assert.IsType<byte[]>(baselineBody).Length + 1;
    }

    private static SkillInvokedEvent CompleteEvent() => new()
    {
        Id = Guid.Parse("018f0f4e-7b2a-4c11-8a3b-123456789abc"),
        ParentId = Guid.Parse("aaaaaaaa-aaaa-4aaa-9aaa-aaaaaaaaaaaa"),
        Timestamp = new DateTimeOffset(2026, 8, 9, 5, 45, 30, TimeSpan.FromHours(5.75)).AddTicks(1_234_567),
        AgentId = "agent-7",
        Ephemeral = true,
        Data = new SkillInvokedData
        {
            Name = "skill-name",
            Path = "skills/SKILL.md",
            Content = "body",
            AllowedTools = ["second", "first"],
            Description = "description",
            PluginName = "plugin-name",
            PluginVersion = "1.2.3",
            Source = "plugin",
            Trigger = SkillInvokedTrigger.AgentInvoked
        }
    };

    private static SkillInvokedEvent RequiredOnlyEvent() => new()
    {
        Id = Guid.Parse("018f0f4e-7b2a-4c11-8a3b-123456789abc"),
        Timestamp = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
        Data = new SkillInvokedData
        {
            Name = "skill-name",
            Path = "skills/SKILL.md",
            Content = "body"
        }
    };
}
