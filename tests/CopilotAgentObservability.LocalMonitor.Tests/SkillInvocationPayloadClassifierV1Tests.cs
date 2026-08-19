using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvocationPayloadClassifierV1Tests
{
    private const string ValidPayload = """{"content":"body \u0061","trigger":"user-invoked","path":".github/skills/review.md","name":"review","source":"project"}""";

    [Theory]
    [MemberData(nameof(ClassificationCases))]
    public void Classify_MirrorsTheReceiverTotalStateReasonOrder(string _, string payload, SkillInvocationPayloadState expectedState, SkillInvocationPayloadReason expectedReason)
    {
        var classification = SkillInvocationPayloadClassifierV1.Classify(Encoding.UTF8.GetBytes(payload));

        Assert.True(classification.WellFormedToken);
        Assert.False(classification.ObservedInvalidUtf8);
        Assert.Equal(expectedState, classification.State);
        Assert.Equal(expectedReason, classification.Reason);
        if (expectedState == SkillInvocationPayloadState.Available)
        {
            Assert.NotNull(classification.AvailableFacts);
        }
        else
        {
            Assert.Null(classification.AvailableFacts);
        }
    }

    [Fact]
    public void Classify_AvailableFactsCarryTheExactDecodedScalarTexts()
    {
        var valid = SkillInvocationPayloadClassifierV1.Classify(Encoding.UTF8.GetBytes(ValidPayload));

        Assert.Equal(
            new SkillInvocationPayloadAvailableFacts("review", "project", "user-invoked", "body a", ".github/skills/review.md"),
            valid.AvailableFacts);

        var maximum = SkillInvocationPayloadClassifierV1.Classify(Encoding.UTF8.GetBytes(MaximumOptionalsPayload()));

        Assert.Equal(new SkillInvocationPayloadAvailableFacts("a", "builtin", "agent-invoked", string.Empty, "p"), maximum.AvailableFacts);
    }

    [Theory]
    [MemberData(nameof(PrecedenceCases))]
    public void Classify_WinnerIsCanonicalAcrossFaultAndEncounterOrder(
        string _,
        string firstOrder,
        string secondOrder,
        SkillInvocationPayloadState expectedState,
        SkillInvocationPayloadReason expectedReason)
    {
        var first = SkillInvocationPayloadClassifierV1.Classify(Encoding.UTF8.GetBytes(firstOrder));
        var second = SkillInvocationPayloadClassifierV1.Classify(Encoding.UTF8.GetBytes(secondOrder));

        Assert.True(first.WellFormedToken);
        Assert.True(second.WellFormedToken);
        Assert.Equal(expectedState, first.State);
        Assert.Equal(expectedReason, first.Reason);
        Assert.Equal(expectedState, second.State);
        Assert.Equal(expectedReason, second.Reason);
    }

    [Theory]
    [MemberData(nameof(OptionalBoundsCases))]
    public void Classify_ValidatesOptionalBoundsAndClosedTokens(string _, string payload, SkillInvocationPayloadState expectedState, SkillInvocationPayloadReason expectedReason)
    {
        var classification = SkillInvocationPayloadClassifierV1.Classify(Encoding.UTF8.GetBytes(payload));

        Assert.True(classification.WellFormedToken);
        Assert.Equal(expectedState, classification.State);
        Assert.Equal(expectedReason, classification.Reason);
    }

    [Theory]
    [MemberData(nameof(NotWellFormedCases))]
    public void Classify_StructurallyBrokenTokensAreNeverWellFormedAndNeverReleaseFacts(string _, byte[] token)
    {
        var classification = SkillInvocationPayloadClassifierV1.Classify(token);

        Assert.False(classification.WellFormedToken);
        Assert.False(classification.ObservedInvalidUtf8);
        Assert.Null(classification.AvailableFacts);
    }

    [Theory]
    [MemberData(nameof(InvalidUtf8ObjectCases))]
    public void Classify_ObservesRawInvalidUtf8InsideAPayloadObjectAndNeverReleasesFacts(
        string _,
        byte[] token,
        SkillInvocationPayloadReason expectedReason)
    {
        var classification = SkillInvocationPayloadClassifierV1.Classify(token);

        Assert.True(classification.WellFormedToken);
        Assert.True(classification.ObservedInvalidUtf8);
        Assert.Equal(SkillInvocationPayloadState.Malformed, classification.State);
        Assert.Equal(expectedReason, classification.Reason);
        Assert.Null(classification.AvailableFacts);
    }

    [Fact]
    public void Classify_ObservesRawInvalidUtf8InsideANonObjectToken()
    {
        var classification = SkillInvocationPayloadClassifierV1.Classify(
            CorruptFirstAscii(Encoding.UTF8.GetBytes("[\"marker\"]"), "marker"));

        Assert.False(classification.WellFormedToken);
        Assert.True(classification.ObservedInvalidUtf8);
        Assert.Null(classification.AvailableFacts);
    }

    public static IEnumerable<object[]> ClassificationCases()
    {
        yield return Classification("available none", ValidPayload, SkillInvocationPayloadState.Available, SkillInvocationPayloadReason.None);
        yield return Classification("duplicate", """{"name":"a","name":"b","path":"p","content":"b"}""", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.DuplicateProperty);
        yield return Classification("unknown", """{"name":"a","path":"p","content":"b","model":"m"}""", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.UnknownProperty);
        yield return Classification("invalid type", """{"name":"a","path":"p","content":null}""", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.InvalidFieldType);
        yield return Classification("name invalid empty", """{"name":"","path":"p","content":"b"}""", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.NameInvalid);
        yield return Classification("name invalid control", """{"name":"a\u0001","path":"p","content":"b"}""", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.NameInvalid);
        yield return Classification("name invalid noncharacter", """{"name":"a\uFDD0","path":"p","content":"b"}""", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.NameInvalid);
        yield return Classification("name invalid Unicode", """{"name":"\uD800","path":"p","content":"b"}""", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.NameInvalid);
        yield return Classification("path invalid empty", """{"name":"a","path":"","content":"b"}""", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.PathInvalid);
        yield return Classification("path invalid ASCII control", """{"name":"a","path":"p\u007F","content":"b"}""", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.PathInvalid);
        yield return Classification("name missing", """{"path":"p","content":"b"}""", SkillInvocationPayloadState.Missing, SkillInvocationPayloadReason.NameMissing);
        yield return Classification("body missing", """{"name":"a","path":"p"}""", SkillInvocationPayloadState.Missing, SkillInvocationPayloadReason.BodyMissing);
        yield return Classification("definition path missing", """{"name":"a","content":"b"}""", SkillInvocationPayloadState.Missing, SkillInvocationPayloadReason.DefinitionPathMissing);
        yield return Classification("body Unicode invalid", """{"name":"a","path":"p","content":"\uD800"}""", SkillInvocationPayloadState.Binary, SkillInvocationPayloadReason.BodyUnicodeInvalid);
        yield return Classification("path Unicode invalid", """{"name":"a","path":"\uD800","content":"b"}""", SkillInvocationPayloadState.Binary, SkillInvocationPayloadReason.PathUnicodeInvalid);
        yield return Classification("body oversized", "{\"name\":\"a\",\"path\":\"p\",\"content\":\"" + new string('b', 1_048_577) + "\"}", SkillInvocationPayloadState.Oversized, SkillInvocationPayloadReason.BodyOversized);
        yield return Classification("path oversized", "{\"name\":\"a\",\"path\":\"" + new string('p', 4_097) + "\",\"content\":\"b\"}", SkillInvocationPayloadState.Oversized, SkillInvocationPayloadReason.PathOversized);
    }

    public static IEnumerable<object[]> PrecedenceCases()
    {
        yield return Precedence("duplicate over unknown", """{"name":"a","name":"b","path":"p","content":"b","model":0}""", """{"model":0,"content":"b","path":"p","name":"a","name":"b"}""", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.DuplicateProperty);
        yield return Precedence("unknown over invalid type", """{"name":"a","path":"p","content":null,"model":0}""", """{"model":0,"content":null,"path":"p","name":"a"}""", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.UnknownProperty);
        yield return Precedence("invalid type over name invalid", """{"name":"","path":"p","content":null}""", """{"content":null,"path":"p","name":""}""", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.InvalidFieldType);
        yield return Precedence("name invalid over path invalid", """{"name":"","path":"","content":"b"}""", """{"content":"b","path":"","name":""}""", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.NameInvalid);
        yield return Precedence("path invalid over body missing", """{"name":"a","path":""}""", """{"path":"","name":"a"}""", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.PathInvalid);
        yield return Precedence("name missing over body missing", """{"path":"p"}""", """{"path":"p"}""", SkillInvocationPayloadState.Missing, SkillInvocationPayloadReason.NameMissing);
        yield return Precedence("body missing over path missing", """{"name":"a"}""", """{"name":"a"}""", SkillInvocationPayloadState.Missing, SkillInvocationPayloadReason.BodyMissing);
        yield return Precedence("path missing over body Unicode", """{"name":"a","content":"\uD800"}""", """{"content":"\uD800","name":"a"}""", SkillInvocationPayloadState.Missing, SkillInvocationPayloadReason.DefinitionPathMissing);
        yield return Precedence("body Unicode over path Unicode", """{"name":"a","path":"\uD800","content":"\uD800"}""", """{"content":"\uD800","path":"\uD800","name":"a"}""", SkillInvocationPayloadState.Binary, SkillInvocationPayloadReason.BodyUnicodeInvalid);
        yield return Precedence("path Unicode over body oversized", "{\"name\":\"a\",\"path\":\"\\uD800\",\"content\":\"" + new string('b', 1_048_577) + "\"}", "{\"content\":\"" + new string('b', 1_048_577) + "\",\"path\":\"\\uD800\",\"name\":\"a\"}", SkillInvocationPayloadState.Binary, SkillInvocationPayloadReason.PathUnicodeInvalid);
        yield return Precedence("body oversized over path oversized", "{\"name\":\"a\",\"path\":\"" + new string('p', 4_097) + "\",\"content\":\"" + new string('b', 1_048_577) + "\"}", "{\"content\":\"" + new string('b', 1_048_577) + "\",\"path\":\"" + new string('p', 4_097) + "\",\"name\":\"a\"}", SkillInvocationPayloadState.Oversized, SkillInvocationPayloadReason.BodyOversized);
    }

    public static IEnumerable<object[]> OptionalBoundsCases()
    {
        yield return Classification("all optional maximums accepted", MaximumOptionalsPayload(), SkillInvocationPayloadState.Available, SkillInvocationPayloadReason.None);
        yield return Classification("allowed tools count", "{\"name\":\"a\",\"path\":\"p\",\"content\":\"b\",\"allowedTools\":[" + string.Join(',', Enumerable.Repeat("\"t\"", 65)) + "]}", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.InvalidFieldType);
        yield return Classification("allowed tool empty", """{"name":"a","path":"p","content":"b","allowedTools":[""]}""", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.InvalidFieldType);
        yield return Classification("allowed tool wrong type", """{"name":"a","path":"p","content":"b","allowedTools":[null]}""", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.InvalidFieldType);
        yield return Classification("allowed tool scalar bound", "{\"name\":\"a\",\"path\":\"p\",\"content\":\"b\",\"allowedTools\":[\"" + new string('t', 129) + "\"]}", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.InvalidFieldType);
        yield return Classification("description scalar bound", "{\"name\":\"a\",\"path\":\"p\",\"content\":\"b\",\"description\":\"" + new string('d', 4_097) + "\"}", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.InvalidFieldType);
        yield return Classification("plugin name byte bound", "{\"name\":\"a\",\"path\":\"p\",\"content\":\"b\",\"pluginName\":\"" + new string('p', 257) + "\"}", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.InvalidFieldType);
        yield return Classification("plugin version byte bound", "{\"name\":\"a\",\"path\":\"p\",\"content\":\"b\",\"pluginVersion\":\"" + RepeatAstral(65) + "\"}", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.InvalidFieldType);
        yield return Classification("optional null", """{"name":"a","path":"p","content":"b","description":null}""", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.InvalidFieldType);
        yield return Classification("source closed", """{"name":"a","path":"p","content":"b","source":"future"}""", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.InvalidFieldType);
        yield return Classification("trigger closed", """{"name":"a","path":"p","content":"b","trigger":"future"}""", SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.InvalidFieldType);
    }

    public static IEnumerable<object[]> NotWellFormedCases()
    {
        yield return Case("empty document", []);
        yield return Case("non-object array", Encoding.UTF8.GetBytes("[]"));
        yield return Case("non-object number", Encoding.UTF8.GetBytes("123"));
        yield return Case("non-object string", Encoding.UTF8.GetBytes("\"str\""));
        yield return Case("truncated object", Encoding.UTF8.GetBytes("{"));
        yield return Case("trailing value", Encoding.UTF8.GetBytes("{} true"));
        yield return Case("nested structural failure", Encoding.UTF8.GetBytes("""{"name":[}"""));
    }

    public static IEnumerable<object[]> InvalidUtf8ObjectCases()
    {
        yield return Case("unknown property value", CorruptFirstAscii(Encoding.UTF8.GetBytes("""{"name":"a","path":"p","content":"b","unknown":"marker"}"""), "marker"), SkillInvocationPayloadReason.UnknownProperty);
        yield return Case("content value", CorruptFirstAscii(Encoding.UTF8.GetBytes("""{"name":"a","path":"p","content":"marker"}"""), "marker"), SkillInvocationPayloadReason.InvalidFieldType);
        yield return Case("property name", CorruptFirstAscii(Encoding.UTF8.GetBytes("""{"name":"a","path":"p","content":"b","marker":"x"}"""), "marker"), SkillInvocationPayloadReason.UnknownProperty);
    }

    private static string MaximumOptionalsPayload()
    {
        var maximumTools = string.Join(',', Enumerable.Repeat("\"" + new string('t', 128) + "\"", 64));
        return "{\"name\":\"a\",\"path\":\"p\",\"content\":\"\",\"allowedTools\":[" + maximumTools + "],\"description\":\"" + RepeatAstral(4_096) + "\",\"pluginName\":\"" + RepeatAstral(64) + "\",\"pluginVersion\":\"" + new string('v', 256) + "\",\"source\":\"builtin\",\"trigger\":\"agent-invoked\"}";
    }

    private static string RepeatAstral(int count) => string.Concat(Enumerable.Repeat("\U0001F600", count));

    private static byte[] CorruptFirstAscii(byte[] source, string marker)
    {
        var markerBytes = Encoding.UTF8.GetBytes(marker);
        var index = source.AsSpan().IndexOf(markerBytes);
        Assert.True(index >= 0);
        var corrupt = source.ToArray();
        corrupt[index] = 0xc3;
        corrupt[index + 1] = 0x28;
        return corrupt;
    }

    private static object[] Case(string name, byte[] token) => [name, token];

    private static object[] Case(string name, byte[] token, SkillInvocationPayloadReason reason) => [name, token, reason];

    private static object[] Classification(string name, string payload, SkillInvocationPayloadState state, SkillInvocationPayloadReason reason) => [name, payload, state, reason];

    private static object[] Precedence(string name, string first, string second, SkillInvocationPayloadState state, SkillInvocationPayloadReason reason) => [name, first, second, state, reason];
}
