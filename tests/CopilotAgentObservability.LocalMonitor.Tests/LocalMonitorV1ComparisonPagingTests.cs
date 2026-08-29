using System.Text;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1ComparisonPagingTests
{
    private const string RepositoryId = "018f0000-0000-7000-8000-000000000001";
    private const string ComparisonId = "018f0000-0000-7000-8000-000000000002";

    [Fact]
    public void RowsQueryIsClosedOrderedAndBounded()
    {
        var parsed = LocalMonitorV1ComparisonQueryParser.ParseRows("?family=tool&q=%20Foo%20%20BAR%20&limit=100");
        Assert.Equal(new LocalMonitorV1ComparisonRowsQuery("tool", "foo bar", null, 100), parsed);

        Assert.Equal(50, LocalMonitorV1ComparisonQueryParser.ParseRows("?family=skill").Limit);
        foreach (var query in new[] { "", "?q=x&family=tool", "?family=other", "?family=tool&x=1", "?family=tool&limit=0", "?family=tool&limit=101", "?family=tool&q=", $"?family=tool&q={new string('x', 201)}" })
            Assert.Throws<LocalMonitorV1ComparisonQueryException>(() => LocalMonitorV1ComparisonQueryParser.ParseRows(query));
    }

    [Fact]
    public void EvidenceQueryIsClosedOrderedAndBounded()
    {
        var parsed = LocalMonitorV1ComparisonQueryParser.ParseEvidence("?result_ordinal=20&field_key=count&limit=200");
        Assert.Equal(new LocalMonitorV1ComparisonEvidenceQuery(20, "count", null, 200), parsed);
        Assert.Equal(100, LocalMonitorV1ComparisonQueryParser.ParseEvidence("?result_ordinal=1").Limit);

        foreach (var query in new[] { "", "?field_key=count&result_ordinal=1", "?result_ordinal=0", "?result_ordinal=1&field_key=unknown", "?result_ordinal=1&limit=201", "?result_ordinal=1&x=1" })
            Assert.Throws<LocalMonitorV1ComparisonQueryException>(() => LocalMonitorV1ComparisonQueryParser.ParseEvidence(query));
    }

    [Fact]
    public void CursorAuthenticatesAndBindsAllPagingInputs()
    {
        var codec = new LocalMonitorV1ComparisonCursorCodec(Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef"));
        var cursor = codec.Encode(RepositoryId, ComparisonId, "rows", "tool\nfoo", 42);
        Assert.Equal(42, codec.Decode(cursor, RepositoryId, ComparisonId, "rows", "tool\nfoo"));

        foreach (var mutation in new[] { cursor + "x", cursor[..^1] + (cursor[^1] == 'a' ? "b" : "a") })
            Assert.Throws<LocalMonitorV1ComparisonCursorException>(() => codec.Decode(mutation, RepositoryId, ComparisonId, "rows", "tool\nfoo"));
        Assert.Throws<LocalMonitorV1ComparisonCursorException>(() => codec.Decode(cursor, ComparisonId, ComparisonId, "rows", "tool\nfoo"));
        Assert.Throws<LocalMonitorV1ComparisonCursorException>(() => codec.Decode(cursor, RepositoryId, ComparisonId, "rows", "skill\nfoo"));
        Assert.Throws<LocalMonitorV1ComparisonCursorException>(() => codec.Decode(cursor, RepositoryId, ComparisonId, "evidence", "tool\nfoo"));
    }
}
