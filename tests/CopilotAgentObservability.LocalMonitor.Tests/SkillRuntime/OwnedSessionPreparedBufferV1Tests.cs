using CopilotAgentObservability.LocalMonitor.SkillRuntime;

namespace CopilotAgentObservability.LocalMonitor.Tests.SkillRuntime;

public sealed class OwnedSessionPreparedBufferV1Tests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64)]
    public void Complete_ZeroOneOrSixtyFourBodies_FreezesImmutableOrdinalOrder(int count)
    {
        var buffer = new OwnedSessionPreparedBufferV1();
        buffer.AcceptStart("session-1", "1.0.65", [1]);
        for (var index = 0; index < count; index++)
            Assert.True(buffer.TryAcceptInvocation("session-1", [(byte)index]));
        buffer.AcceptSuccessfulTerminal("session-1", [2]);

        var prepared = Assert.IsType<OwnedSessionPreparedImportV1>(buffer.TryFreeze("session-1", "1.0.65"));
        Assert.Equal(Enumerable.Range(0, count), prepared.Bodies.Select(static body => body.Ordinal));
        if (count != 0)
        {
            var first = prepared.Bodies[0].BodyUtf8.ToArray();
            first[0] = 255;
            Assert.Equal(0, prepared.Bodies[0].BodyUtf8.Span[0]);
        }
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void AggregateByteBoundary_IsExact(int offset, bool succeeds)
    {
        var buffer = new OwnedSessionPreparedBufferV1();
        buffer.AcceptStart("session-1", "1.0.65", [1]);
        var size = OwnedSessionPreparedBufferV1.MaxAggregateBodyBytes + offset;

        Assert.Equal(succeeds, buffer.TryAcceptInvocation("session-1", new byte[size]));
        buffer.AcceptSuccessfulTerminal("session-1", [2]);
        Assert.Equal(succeeds, buffer.TryFreeze("session-1", "1.0.65") is not null);
    }

    [Fact]
    public void Invocation_SixtyFifthOrAggregateByteAboveLimit_PoisonsWholeBuffer()
    {
        var count = new OwnedSessionPreparedBufferV1();
        count.AcceptStart("session-1", "1.0.65", [1]);
        for (var index = 0; index < 64; index++) Assert.True(count.TryAcceptInvocation("session-1", [1]));
        Assert.False(count.TryAcceptInvocation("session-1", [1]));
        Assert.Null(count.TryFreeze("session-1", "1.0.65"));

        var bytes = new OwnedSessionPreparedBufferV1();
        bytes.AcceptStart("session-1", "1.0.65", [1]);
        Assert.True(bytes.TryAcceptInvocation("session-1", new byte[OwnedSessionPreparedBufferV1.MaxAggregateBodyBytes]));
        Assert.False(bytes.TryAcceptInvocation("session-1", [1]));
        Assert.Null(bytes.TryFreeze("session-1", "1.0.65"));
    }

    [Fact]
    public void RelevantEventAfterTerminal_PoisonsWholeBuffer()
    {
        var buffer = new OwnedSessionPreparedBufferV1();
        buffer.AcceptStart("session-1", "1.0.65", [1]);
        buffer.AcceptSuccessfulTerminal("session-1", [2]);
        Assert.False(buffer.TryAcceptInvocation("session-1", [3]));
        Assert.Null(buffer.TryFreeze("session-1", "1.0.65"));
    }
}
