using System.Reflection;
using System.Text.RegularExpressions;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionOwnershipReceiptTests
{
    private const string StoreId = "00112233445566778899aabbccddeeff";
    private static readonly byte[] Token = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();

    [Fact]
    public void CreateSession_UsesThePinnedCanonicalVectorAndEveryBoundField()
    {
        var input = Session();
        var receipt = RetentionOwnershipReceipt.CreateSession(input);
        Assert.Equal("8646D891938DD890D676D231C1EDFABE239E2451B3B4E92F9706D8D10B1D0D27", Convert.ToHexString(receipt));
        AssertMutationsChange(receipt, new[]
        {
            RetentionOwnershipReceipt.CreateSession(input with { EventId = "78b5b33b-47d1-4f5d-9fd7-6752811ed16f" }),
            RetentionOwnershipReceipt.CreateSession(input with { Kind = "text/plain" }),
            RetentionOwnershipReceipt.CreateSession(input with { CapturedAtText = "2026-07-19T01:02:04.0000000+00:00", CapturedAtUtcTicks = input.CapturedAtUtcTicks + TimeSpan.TicksPerSecond }),
            RetentionOwnershipReceipt.CreateSession(input with { CapturedAtText = "2026-07-19T10:02:03.0000000+09:00" }),
            RetentionOwnershipReceipt.CreateSession(input with { ExpiresAtText = "2026-10-17T01:02:04.0000000+00:00", ExpiresAtUtcTicks = input.ExpiresAtUtcTicks + TimeSpan.TicksPerSecond }),
            RetentionOwnershipReceipt.CreateSession(input with { SessionId = "de305d54-75b4-431b-adb2-eb6b9e546015" }),
            RetentionOwnershipReceipt.CreateSession(input with { RunId = null }),
            RetentionOwnershipReceipt.CreateSession(input with { SourceAdapter = "other" }),
            RetentionOwnershipReceipt.CreateSession(input with { SourceEventId = "event-2" }),
            RetentionOwnershipReceipt.CreateSession(input with { OwnerToken = ChangedToken() }),
            RetentionOwnershipReceipt.CreateSession(input with { StoreInstanceId = "ffeeddccbbaa99887766554433221100" })
        });
    }

    [Fact]
    public void CreateRawAndAnalysis_UsePinnedCanonicalVectorsAndBindNullableMarkers()
    {
        var raw = Raw();
        var analysis = Analysis();
        Assert.Equal("E4EB80FE125A4976954A1E83AC0C8B5597CE2B0E72D1F950E8E874B26F80CF71", Convert.ToHexString(RetentionOwnershipReceipt.CreateRawRecord(raw)));
        Assert.Equal("44539F823D6E508DDC5FAAF5DA92DDB28B1C5575AED9F93A33C417CAC1EF0C02", Convert.ToHexString(RetentionOwnershipReceipt.CreateAnalysisRun(analysis)));
        AssertMutationsChange(RetentionOwnershipReceipt.CreateRawRecord(raw), new[] { RetentionOwnershipReceipt.CreateRawRecord(raw with { Id = 43 }), RetentionOwnershipReceipt.CreateRawRecord(raw with { ReceivedAtText = "2026-07-19T10:02:03.0000000+09:00" }), RetentionOwnershipReceipt.CreateRawRecord(raw with { SchemaVersion = 2 }), RetentionOwnershipReceipt.CreateRawRecord(raw with { OwnerToken = ChangedToken() }), RetentionOwnershipReceipt.CreateRawRecord(raw with { StoreInstanceId = "ffeeddccbbaa99887766554433221100" }) });
        AssertMutationsChange(RetentionOwnershipReceipt.CreateAnalysisRun(analysis), new[] { RetentionOwnershipReceipt.CreateAnalysisRun(analysis with { RunId = 8 }), RetentionOwnershipReceipt.CreateAnalysisRun(analysis with { RequestedAtText = "2026-07-19T10:02:03.0000000+09:00" }), RetentionOwnershipReceipt.CreateAnalysisRun(analysis with { RecordId = null }), RetentionOwnershipReceipt.CreateAnalysisRun(analysis with { SpanId = null }), RetentionOwnershipReceipt.CreateAnalysisRun(analysis with { OwnerToken = ChangedToken() }), RetentionOwnershipReceipt.CreateAnalysisRun(analysis with { StoreInstanceId = "ffeeddccbbaa99887766554433221100" }) });
    }

    [Fact]
    public void Create_RejectsInvalidInputsWithOneSanitizedMessage()
    {
        var invalid = new Action[]
        {
            () => RetentionOwnershipReceipt.CreateSession(Session() with { StoreInstanceId = StoreId.ToUpperInvariant() }),
            () => RetentionOwnershipReceipt.CreateSession(Session() with { EventId = "78B5B33B-47D1-4F5D-9FD7-6752811ED16F" }),
            () => RetentionOwnershipReceipt.CreateSession(Session() with { CapturedAtUtcTicks = 0 }),
            () => RetentionOwnershipReceipt.CreateRawRecord(Raw() with { ReceivedAtUtcTicks = 0 }),
            () => RetentionOwnershipReceipt.CreateRawRecord(Raw() with { Id = 0 }),
            () => RetentionOwnershipReceipt.CreateRawRecord(Raw() with { SchemaVersion = 0 }),
            () => RetentionOwnershipReceipt.CreateAnalysisRun(Analysis() with { RunId = 0 }),
            () => RetentionOwnershipReceipt.CreateAnalysisRun(Analysis() with { RecordId = 0 }),
            () => RetentionOwnershipReceipt.CreateSession(Session() with { OwnerToken = new byte[31] })
        };
        foreach (var action in invalid)
        {
            var error = Assert.Throws<ArgumentException>(action);
            Assert.Equal("Invalid retention ownership receipt input.", error.Message);
        }
    }

    [Fact]
    public void ReceiptApi_IsInternalAndCannotCarryRawOrPathData()
    {
        var assembly = typeof(RetentionOwnershipReceipt).Assembly;
        var types = assembly.GetTypes().Where(type => type.Namespace == typeof(RetentionOwnershipReceipt).Namespace && (type.Name.Contains("OwnershipReceipt", StringComparison.Ordinal) || type.Name.EndsWith("ReceiptInput", StringComparison.Ordinal)));
        foreach (var type in types)
        {
            Assert.False(type.IsPublic);
            foreach (var property in type.GetProperties()) Assert.False(Regex.IsMatch(property.Name, "raw|content|path|result|error|payload|resource|trace|credential|secret", RegexOptions.IgnoreCase));
            foreach (var parameter in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).SelectMany(method => method.GetParameters())) Assert.False(Regex.IsMatch(parameter.Name!, "raw|content|path|result|error|payload|resource|trace|credential|secret", RegexOptions.IgnoreCase));
        }
    }

    [Fact]
    public void Matches_RequiresExactThirtyTwoByteValues()
    {
        Assert.True(RetentionOwnershipReceipt.Matches(new byte[32], new byte[32]));
        Assert.False(RetentionOwnershipReceipt.Matches(new byte[31], new byte[32]));
    }

    private static RetentionSessionOwnershipReceiptInput Session() => new(StoreId, "78b5b33b-47d1-4f5d-9fd7-6752811ed16e", "application/json", "2026-07-19T01:02:03.0000000+00:00", 639200197230000000, "2026-10-17T01:02:03.0000000+00:00", 639277957230000000, "de305d54-75b4-431b-adb2-eb6b9e546014", "123e4567-e89b-12d3-a456-426614174000", "canvas", "event-1", Token);
    private static RetentionRawRecordReceiptInput Raw() => new(StoreId, 42, "2026-07-19T01:02:03.0000000+00:00", 639200197230000000, 1, Token);
    private static RetentionAnalysisRunOwnershipReceiptInput Analysis() => new(StoreId, 7, "2026-07-19T01:02:03.0000000+00:00", 639200197230000000, 42, "span-1", Token);
    private static byte[] ChangedToken() => Token.Select(static value => (byte)(value ^ 1)).ToArray();
    private static void AssertMutationsChange(byte[] receipt, IEnumerable<byte[]> mutations) { foreach (var mutation in mutations) Assert.NotEqual(receipt, mutation); }
}
