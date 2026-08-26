using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1SessionDetailApplicationTests
{
    [Fact]
    public void TimelineCursor_RoundTripsTheExact119ByteFrame()
    {
        var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var filter = new LocalMonitorV1TimelineFilter(
            "018f0000-0000-7000-8000-000000000001",
            new string('1', 64),
            "018f0000-0000-7000-8000-000000000003",
            null,
            100);
        var position = new LocalMonitorV1TimelinePosition(
            0,
            638918245230000000,
            7,
            "node-00000000000000000000000000000002");

        var cursor = LocalMonitorV1TimelineCursor.Encode(key, filter, position);

        Assert.Equal(159, cursor.Length);
        Assert.True(LocalMonitorV1TimelineCursor.TryDecode(cursor, key, filter, out var decoded));
        Assert.Equal(position, decoded);
        Assert.False(LocalMonitorV1TimelineCursor.TryDecode(cursor, key, filter with { Limit = 101 }, out _));
    }
}
