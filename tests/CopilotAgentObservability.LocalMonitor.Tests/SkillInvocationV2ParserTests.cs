using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvocationV2ParserTests
{
    private const string ValidPayload = """{"content":"body \u0061","trigger":"user-invoked","path":".github/skills/review.md","name":"review","source":"project"}""";

    [Fact]
    public void Parse_ValidEnvelopePreservesExactPayloadEvidenceFactsAndCapabilityIdentity()
    {
        var capability = new TestRuntimeCapability();
        var request = ValidRequest(ValidPayload);

        var batch = SkillInvocationV2Parser.Parse(request, capability);

        Assert.Same(capability, batch.RuntimeCapability);
        var envelope = Assert.Single(batch.AcceptedEnvelopes);
        Assert.Equal(SkillInvocationPayloadState.Available, envelope.PayloadState);
        Assert.Equal(SkillInvocationPayloadReason.None, envelope.PayloadReason);
        Assert.Equal(119, envelope.RawPayloadEvidence.PayloadByteLength);
        Assert.Equal(ValidPayload, Encoding.UTF8.GetString(envelope.RawPayloadEvidence.PayloadUtf8.Span));
        Assert.Equal("e26ce7c0d8ea9a43478423e5b0a51969de53fc64e2c5e0838136290a3a61c375", Convert.ToHexStringLower(envelope.RawPayloadEvidence.PayloadSha256.Span));
        Assert.Equal("review", envelope.Name);
        Assert.Equal("project", envelope.Source);
        Assert.Equal("user-invoked", envelope.Trigger);
        Assert.Equal("body a", envelope.Body!.Text);
        Assert.Equal(6, envelope.Body.Utf8ByteLength);
        Assert.Equal("68d3a6450cfc846671f6160560ff0a8c3859105d6a967092334710a933c0cf89", Convert.ToHexStringLower(envelope.Body.Sha256.Span));
        Assert.Equal(".github/skills/review.md", envelope.DefinitionPath!.Text);
        Assert.Equal(24, envelope.DefinitionPath.Utf8ByteLength);
        Assert.Equal("d6636e09410332fdadebd7a8925b0d5e8177c401a8b59d5f1747ef742d9bd701", Convert.ToHexStringLower(envelope.DefinitionPath.Sha256.Span));
    }

    [Fact]
    public void Parse_CapturesAdmittedOuterIdentityWithNonnullOptionals()
    {
        var batch = SkillInvocationV2Parser.Parse(ValidRequest(ValidPayload), new TestRuntimeCapability());
        var envelope = Assert.Single(batch.AcceptedEnvelopes);

        Assert.Equal("native-session", batch.NativeSessionId);
        Assert.Equal("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", envelope.Identity.SourceEventId);
        Assert.Equal("bbbbbbbb-bbbb-4bbb-9bbb-bbbbbbbbbbbb", envelope.Identity.SourceParentEventId);
        Assert.Equal(new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero), envelope.Identity.OccurredAt);
        Assert.Equal("run-1", envelope.Identity.RunNativeId);
        Assert.True(envelope.Identity.SourceEphemeral);
        Assert.Null(envelope.Identity.TraceId);
        Assert.Null(envelope.Identity.SpanId);
    }

    [Fact]
    public void Parse_CapturesAdmittedOuterIdentityWithAllNullOptionalsAndFalseEphemeral()
    {
        var request = Encoding.UTF8.GetBytes(
            ValidRequestText(ValidPayload)
                .Replace("\"source_parent_event_id\":\"bbbbbbbb-bbbb-4bbb-9bbb-bbbbbbbbbbbb\"", "\"source_parent_event_id\":null", StringComparison.Ordinal)
                .Replace("\"run_native_id\":\"run-1\"", "\"run_native_id\":null", StringComparison.Ordinal)
                .Replace("\"source_ephemeral\":true", "\"source_ephemeral\":false", StringComparison.Ordinal));

        var envelope = Assert.Single(SkillInvocationV2Parser.Parse(request, new TestRuntimeCapability()).AcceptedEnvelopes);

        Assert.Equal("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", envelope.Identity.SourceEventId);
        Assert.Null(envelope.Identity.SourceParentEventId);
        Assert.Null(envelope.Identity.RunNativeId);
        Assert.False(envelope.Identity.SourceEphemeral);
    }

    [Fact]
    public void Parse_OccurredAtRoundTripsToTheExactAdmittedInstantToTheTick()
    {
        const string occurredAt = "2026-08-09T12:34:56.7654321+00:00";
        const string format = "yyyy-MM-ddTHH:mm:ss.fffffffzzz";
        var request = Encoding.UTF8.GetBytes(
            ValidRequestText(ValidPayload).Replace("2026-08-09T00:00:00.0000000+00:00", occurredAt, StringComparison.Ordinal));

        var envelope = Assert.Single(SkillInvocationV2Parser.Parse(request, new TestRuntimeCapability()).AcceptedEnvelopes);

        Assert.Equal(TimeSpan.Zero, envelope.Identity.OccurredAt.Offset);
        Assert.Equal(occurredAt, envelope.Identity.OccurredAt.ToString(format, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Parse_PropertyOrderIsNonsemanticForEnvelopeEventAndPayload()
    {
        const string reversedPayload = """{"trigger":"context-load","source":"remote","pluginVersion":"1","pluginName":"p","description":"d","allowedTools":[],"content":"body","path":"relative","name":"ordered"}""";
        var eventJson = """{"payload":""" + reversedPayload + """, "span_id":null,"trace_id":null,"source_ephemeral":false,"run_native_id":null,"occurred_at":"2026-08-09T00:00:00.0000000+00:00","type":"skill.invoked","source_parent_event_id":null,"source_event_id":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"}""";
        var request = Encoding.UTF8.GetBytes("""{"events":[""" + eventJson + """],"schema_fingerprint":"8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c","payload_schema":"github-copilot-sdk.skill-invoked.v1","normalization_version":"github-copilot-sdk.skill-invoked.normalize.v2","adapter_version":"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1","source_application_version":"1.0.65","native_session_id":"native-session","source_surface":"copilot-sdk","source_adapter":"copilot-sdk-stream","schema_version":2}""");

        var envelope = Assert.Single(SkillInvocationV2Parser.Parse(request, new TestRuntimeCapability()).AcceptedEnvelopes);

        Assert.Equal(SkillInvocationPayloadState.Available, envelope.PayloadState);
        Assert.Equal("ordered", envelope.Name);
        Assert.Equal("remote", envelope.Source);
        Assert.Equal("context-load", envelope.Trigger);
        Assert.Equal(reversedPayload, Encoding.UTF8.GetString(envelope.RawPayloadEvidence.PayloadUtf8.Span));
    }

    [Fact]
    public void Parse_DefensivelyOwnsInputAndReturnedEvidenceBuffers()
    {
        var request = ValidRequest(ValidPayload);
        var batch = SkillInvocationV2Parser.Parse(request, new TestRuntimeCapability());
        var envelope = Assert.Single(batch.AcceptedEnvelopes);
        request.AsSpan().Fill((byte)'!');
        var exposedPayload = envelope.RawPayloadEvidence.PayloadUtf8.ToArray();
        var exposedDigest = envelope.RawPayloadEvidence.PayloadSha256.ToArray();
        var exposedBody = envelope.Body!.Utf8.ToArray();
        exposedPayload.AsSpan().Fill((byte)'!');
        exposedDigest.AsSpan().Fill((byte)'!');
        exposedBody.AsSpan().Fill((byte)'!');

        Assert.Equal(ValidPayload, Encoding.UTF8.GetString(envelope.RawPayloadEvidence.PayloadUtf8.Span));
        Assert.Equal("e26ce7c0d8ea9a43478423e5b0a51969de53fc64e2c5e0838136290a3a61c375", Convert.ToHexStringLower(envelope.RawPayloadEvidence.PayloadSha256.Span));
        Assert.Equal("body a", envelope.Body.Text);
    }

    [Theory]
    [MemberData(nameof(OuterFailureRequests))]
    public void Parse_RejectsOuterStructuralShapeTypeValueCountAndProvenanceFaults(string _, byte[] request)
    {
        Assert.Throws<JsonException>(() => SkillInvocationV2Parser.Parse(request, new TestRuntimeCapability()));
    }

    [Fact]
    public void Parse_AcceptsDepth64AndRejectsDepth65()
    {
        var accepted = ValidRequest(PayloadWithUnknownNestedArrays(60));
        var rejected = ValidRequest(PayloadWithUnknownNestedArrays(61));

        var envelope = Assert.Single(SkillInvocationV2Parser.Parse(accepted, new TestRuntimeCapability()).AcceptedEnvelopes);
        Assert.Equal(SkillInvocationPayloadReason.UnknownProperty, envelope.PayloadReason);
        Assert.Throws<JsonException>(() => SkillInvocationV2Parser.Parse(rejected, new TestRuntimeCapability()));
    }

    [Fact]
    public void Parse_CompletesStructuralScanBeforeSelectingEarlierPayloadWinner()
    {
        var request = ValidRequest(Encoding.UTF8.GetBytes("""{"name":"a","name":"b","path":"p","content":"b","unknown":[}"""));

        Assert.Throws<JsonException>(() => SkillInvocationV2Parser.Parse(request, new TestRuntimeCapability()));
    }

    [Theory]
    [MemberData(nameof(ClassificationCases))]
    public void Parse_ClassifiesPayloadWithLiteralTotalStateReason(string _, string payload, SkillInvocationPayloadState expectedState, SkillInvocationPayloadReason expectedReason)
    {
        var envelope = Assert.Single(SkillInvocationV2Parser.Parse(ValidRequest(payload), new TestRuntimeCapability()).AcceptedEnvelopes);

        Assert.Equal(expectedState, envelope.PayloadState);
        Assert.Equal(expectedReason, envelope.PayloadReason);
        if (expectedState == SkillInvocationPayloadState.Available)
        {
            Assert.NotNull(envelope.ClaimFacts);
        }
        else
        {
            Assert.Null(envelope.ClaimFacts);
            Assert.Null(envelope.Name);
            Assert.Null(envelope.Source);
            Assert.Null(envelope.Trigger);
            Assert.Null(envelope.Body);
            Assert.Null(envelope.DefinitionPath);
        }
    }

    [Theory]
    [MemberData(nameof(PrecedenceCases))]
    public void Parse_PayloadWinnerIsCanonicalAcrossFaultAndEncounterOrder(
        string _,
        string firstOrder,
        string secondOrder,
        SkillInvocationPayloadState expectedState,
        SkillInvocationPayloadReason expectedReason)
    {
        var first = Assert.Single(SkillInvocationV2Parser.Parse(ValidRequest(firstOrder), new TestRuntimeCapability()).AcceptedEnvelopes);
        var second = Assert.Single(SkillInvocationV2Parser.Parse(ValidRequest(secondOrder), new TestRuntimeCapability()).AcceptedEnvelopes);

        Assert.Equal(expectedState, first.PayloadState);
        Assert.Equal(expectedReason, first.PayloadReason);
        Assert.Equal(expectedState, second.PayloadState);
        Assert.Equal(expectedReason, second.PayloadReason);
    }

    [Theory]
    [MemberData(nameof(OptionalBoundsCases))]
    public void Parse_ValidatesOptionalBoundsAndClosedTokens(string _, string payload, SkillInvocationPayloadState expectedState, SkillInvocationPayloadReason expectedReason)
    {
        var envelope = Assert.Single(SkillInvocationV2Parser.Parse(ValidRequest(payload), new TestRuntimeCapability()).AcceptedEnvelopes);

        Assert.Equal(expectedState, envelope.PayloadState);
        Assert.Equal(expectedReason, envelope.PayloadReason);
    }

    public static IEnumerable<object[]> OuterFailureRequests()
    {
        yield return Case("invalid UTF-8", CorruptFirstAscii(ValidRequest(ValidPayload), "native-session"));
        yield return Case("non-object", Encoding.UTF8.GetBytes("[]"));
        yield return Case("trailing value", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload) + " true"));
        yield return Case("comment", Encoding.UTF8.GetBytes("{/*comment*/" + ValidRequestText(ValidPayload)[1..]));
        yield return Case("trailing comma", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload)[..^1] + ",}"));
        yield return Case("empty document", []);
        yield return Case("missing envelope property", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("\"schema_version\":2,", string.Empty, StringComparison.Ordinal)));
        yield return Case("duplicate envelope property", Encoding.UTF8.GetBytes("{\"schema_version\":2," + ValidRequestText(ValidPayload)[1..]));
        yield return Case("unknown envelope property", Encoding.UTF8.GetBytes("{\"unknown\":0," + ValidRequestText(ValidPayload)[1..]));
        yield return Case("wrong schema version type", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("\"schema_version\":2", "\"schema_version\":\"2\"", StringComparison.Ordinal)));
        yield return Case("wrong schema version value", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("\"schema_version\":2", "\"schema_version\":3", StringComparison.Ordinal)));
        yield return Case("wrong adapter", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("copilot-sdk-stream", "other", StringComparison.Ordinal)));
        yield return Case("wrong surface", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("\"copilot-sdk\"", "\"other\"", StringComparison.Ordinal)));
        yield return Case("null native session", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("\"native_session_id\":\"native-session\"", "\"native_session_id\":null", StringComparison.Ordinal)));
        yield return Case("empty native session", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("native-session", string.Empty, StringComparison.Ordinal)));
        yield return Case("native session scalar bound", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("native-session", new string('n', 257), StringComparison.Ordinal)));
        yield return Case("native session null scalar", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("native-session", "native\\u0000session", StringComparison.Ordinal)));
        yield return Case("wrong source application version", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("1.0.65", "1.0.66", StringComparison.Ordinal)));
        yield return Case("wrong adapter version", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("copilot-sdk-dotnet-1.0.4+cao-skill-v2.1", "other", StringComparison.Ordinal)));
        yield return Case("wrong normalization version", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("github-copilot-sdk.skill-invoked.normalize.v2", "other", StringComparison.Ordinal)));
        yield return Case("wrong payload schema", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("github-copilot-sdk.skill-invoked.v1", "other", StringComparison.Ordinal)));
        yield return Case("wrong fingerprint", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c", new string('0', 64), StringComparison.Ordinal)));
        yield return Case("events wrong type", Encoding.UTF8.GetBytes(EnvelopeText("{}")));
        yield return Case("events empty", Encoding.UTF8.GetBytes(EnvelopeText("[]")));
        yield return Case("events count two", Encoding.UTF8.GetBytes(EnvelopeText("[" + ValidEventText(ValidPayload) + "," + ValidEventText(ValidPayload) + "]")));
        yield return Case("event missing property", Encoding.UTF8.GetBytes(EnvelopeText("[" + ValidEventText(ValidPayload).Replace("\"type\":\"skill.invoked\",", string.Empty, StringComparison.Ordinal) + "]")));
        yield return Case("event duplicate property", Encoding.UTF8.GetBytes(EnvelopeText("[{\"type\":\"skill.invoked\"," + ValidEventText(ValidPayload)[1..] + "]")));
        yield return Case("event unknown property", Encoding.UTF8.GetBytes(EnvelopeText("[{\"unknown\":0," + ValidEventText(ValidPayload)[1..] + "]")));
        yield return Case("event id uppercase", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA", StringComparison.Ordinal)));
        yield return Case("event id not v4", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "aaaaaaaa-aaaa-1aaa-8aaa-aaaaaaaaaaaa", StringComparison.Ordinal)));
        yield return Case("parent id not canonical", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("bbbbbbbb-bbbb-4bbb-9bbb-bbbbbbbbbbbb", "{bbbbbbbb-bbbb-4bbb-9bbb-bbbbbbbbbbbb}", StringComparison.Ordinal)));
        yield return Case("wrong type token", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("skill.invoked", "skill.started", StringComparison.Ordinal)));
        yield return Case("timestamp Z form", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("2026-08-09T00:00:00.0000000+00:00", "2026-08-09T00:00:00.0000000Z", StringComparison.Ordinal)));
        yield return Case("timestamp invalid date", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("2026-08-09T00:00:00.0000000+00:00", "2026-02-30T00:00:00.0000000+00:00", StringComparison.Ordinal)));
        yield return Case("run id empty", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("\"run_native_id\":\"run-1\"", "\"run_native_id\":\"\"", StringComparison.Ordinal)));
        yield return Case("ephemeral wrong type", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("\"source_ephemeral\":true", "\"source_ephemeral\":null", StringComparison.Ordinal)));
        yield return Case("nonnull trace", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("\"trace_id\":null", "\"trace_id\":\"trace\"", StringComparison.Ordinal)));
        yield return Case("nonnull span", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace("\"span_id\":null", "\"span_id\":\"span\"", StringComparison.Ordinal)));
        yield return Case("payload null", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace(ValidPayload, "null", StringComparison.Ordinal)));
        yield return Case("payload non-object", Encoding.UTF8.GetBytes(ValidRequestText(ValidPayload).Replace(ValidPayload, "[]", StringComparison.Ordinal)));
        yield return Case("nested structural failure in unknown payload value", ValidRequest(Encoding.UTF8.GetBytes("""{"name":"review","path":"p","content":"b","unknown":[}""")));
        yield return Case("invalid UTF-8 in unknown payload value", CorruptFirstAscii(ValidRequest("""{"name":"review","path":"p","content":"b","unknown":"marker"}"""), "marker"));
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
        var maximumTools = string.Join(',', Enumerable.Repeat("\"" + new string('t', 128) + "\"", 64));
        var maximumPayload = "{\"name\":\"a\",\"path\":\"p\",\"content\":\"\",\"allowedTools\":[" + maximumTools + "],\"description\":\"" + RepeatAstral(4_096) + "\",\"pluginName\":\"" + RepeatAstral(64) + "\",\"pluginVersion\":\"" + new string('v', 256) + "\",\"source\":\"builtin\",\"trigger\":\"agent-invoked\"}";
        yield return Classification("all optional maximums accepted", maximumPayload, SkillInvocationPayloadState.Available, SkillInvocationPayloadReason.None);
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

    private static object[] Case(string name, byte[] request) => [name, request];

    private static object[] Classification(string name, string payload, SkillInvocationPayloadState state, SkillInvocationPayloadReason reason) => [name, payload, state, reason];

    private static object[] Precedence(string name, string first, string second, SkillInvocationPayloadState state, SkillInvocationPayloadReason reason) => [name, first, second, state, reason];

    private static byte[] ValidRequest(string payload) => Encoding.UTF8.GetBytes(ValidRequestText(payload));

    private static byte[] ValidRequest(byte[] payload)
    {
        var prefix = Encoding.UTF8.GetBytes(Currentize(EnvelopePrefix) + "[" + EventPrefix);
        var suffix = Encoding.UTF8.GetBytes("]}");
        var request = new byte[prefix.Length + payload.Length + suffix.Length];
        prefix.CopyTo(request, 0);
        payload.CopyTo(request, prefix.Length);
        suffix.CopyTo(request, prefix.Length + payload.Length);
        return request;
    }

    private static string ValidRequestText(string payload) => EnvelopeText("[" + ValidEventText(payload) + "]");

    private static string EnvelopeText(string eventsJson) => Currentize(EnvelopePrefix) + eventsJson + "}";

    private static string Currentize(string value) => value.Replace(
        "github-copilot-sdk.skill-invoked.normalize.v1",
        "github-copilot-sdk.skill-invoked.normalize.v2",
        StringComparison.Ordinal);

    private static string ValidEventText(string payload) => EventPrefix + payload + "}";

    private const string EnvelopePrefix = """{"schema_version":2,"source_adapter":"copilot-sdk-stream","source_surface":"copilot-sdk","native_session_id":"native-session","source_application_version":"1.0.65","adapter_version":"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1","normalization_version":"github-copilot-sdk.skill-invoked.normalize.v1","payload_schema":"github-copilot-sdk.skill-invoked.v1","schema_fingerprint":"8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c","events":""";

    private const string EventPrefix = """{"source_event_id":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","source_parent_event_id":"bbbbbbbb-bbbb-4bbb-9bbb-bbbbbbbbbbbb","type":"skill.invoked","occurred_at":"2026-08-09T00:00:00.0000000+00:00","run_native_id":"run-1","source_ephemeral":true,"trace_id":null,"span_id":null,"payload":""";

    private static string PayloadWithUnknownNestedArrays(int nestedArrayCount) =>
        "{\"name\":\"a\",\"path\":\"p\",\"content\":\"b\",\"unknown\":"
        + new string('[', nestedArrayCount)
        + "0"
        + new string(']', nestedArrayCount)
        + "}";

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

    private sealed class TestRuntimeCapability : ISkillInvocationV2RuntimeCapability
    {
        public CopilotAgentObservability.LocalMonitor.SkillRuntime.CertifiedSkillProducerIdentityV1 CertifiedIdentity => SkillInvocationV2TestIdentity.V1065;
    }
}
