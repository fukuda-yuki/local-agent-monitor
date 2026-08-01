using CopilotAgentObservability.Telemetry.Repositories;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositoryObservationParserTests
{
    [Fact]
    public void ParseRetainsOneDuplicateOccurrenceForEachPhysicalAttribute()
    {
        var result = Parse("""
            {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[{"traceId":"00112233445566778899aabbccddeeff","spanId":"0123456789abcdef","attributes":[{"key":"vcs.repository.url.full","value":{"stringValue":"https://github.com/Octo/Repo"}},{"key":"vcs.repository.url.full","value":{"stringValue":"https://github.com/Other/Repo"}}]}]}]}]}
            """);

        Assert.Equal(2, result.Occurrences.Count);
        Assert.All(result.Occurrences, occurrence => Assert.Equal(LocalRepositoryOccurrenceClassification.DuplicateKey, occurrence.Classification));
        Assert.All(result.ContextLinks, link => Assert.Equal(LocalRepositoryAdmissionState.DuplicateKey, link.AdmissionState));
    }

    [Fact]
    public void ParseMarksEqualDuplicateKeysAsDuplicateOccurrences()
    {
        var result = Parse("""
            {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[{"traceId":"00112233445566778899aabbccddeeff","spanId":"0123456789abcdef","attributes":[{"key":"vcs.repository.url.full","value":{"stringValue":"https://github.com/Octo/Repo"}},{"key":"vcs.repository.url.full","value":{"stringValue":"https://github.com/Octo/Repo"}}]}]}]}]}
            """);

        Assert.All(result.Occurrences, occurrence => Assert.Equal(LocalRepositoryOccurrenceClassification.DuplicateKey, occurrence.Classification));
    }

    [Fact]
    public void ParseTreatsOnlyAOnePropertyStringValueAsScalarString()
    {
        var result = Parse("""
            {"resourceSpans":[{"resource":{"attributes":[{"key":"vcs.repository.url.full","value":{"stringValue":"https://github.com/Octo/Repo","intValue":"1"}},{"key":"copilot_chat.repo.remote_url","value":{"stringValue":null}}]},"scopeSpans":[{"spans":[{"traceId":"00112233445566778899aabbccddeeff","spanId":"0123456789abcdef","attributes":[]}]}]}]}
            """);

        Assert.All(result.Occurrences, occurrence => Assert.Equal(LocalRepositoryOccurrenceClassification.InvalidType, occurrence.Classification));
    }

    [Fact]
    public void ParseValidatesRawInputBeforeJsonTraversalAndPreservesExactMetadata()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalRepositoryObservationParser.Parse(
            rawRecordId: 0,
            payloadJson: "{",
            rawPayloadSha256: new string('a', 64),
            sourceSurface: "github-copilot-cli",
            sourceApplicationVersion: null,
            observedAt: DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalRepositoryObservationParser.Parse(
            rawRecordId: -1,
            payloadJson: "{",
            rawPayloadSha256: new string('a', 64),
            sourceSurface: "github-copilot-cli",
            sourceApplicationVersion: null,
            observedAt: DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(() => LocalRepositoryObservationParser.Parse(
            rawRecordId: 1,
            payloadJson: "{",
            rawPayloadSha256: new string('A', 64),
            sourceSurface: "github-copilot-cli",
            sourceApplicationVersion: null,
            observedAt: DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(() => LocalRepositoryObservationParser.Parse(
            rawRecordId: 1,
            payloadJson: "{",
            rawPayloadSha256: new string('a', 63),
            sourceSurface: "github-copilot-cli",
            sourceApplicationVersion: null,
            observedAt: DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(() => LocalRepositoryObservationParser.Parse(
            rawRecordId: 1,
            payloadJson: "{",
            rawPayloadSha256: new string('g', 64),
            sourceSurface: "github-copilot-cli",
            sourceApplicationVersion: null,
            observedAt: DateTimeOffset.UnixEpoch));

        const string payload = "{\"resourceSpans\":[{\"resource\":{\"attributes\":[]},\"scopeSpans\":[{\"spans\":[{\"traceId\":\"00112233445566778899aabbccddeeff\",\"spanId\":\"0123456789abcdef\",\"attributes\":[{\"key\":\"vcs.repository.url.full\",\"value\":{\"stringValue\":\"https://github.com/Octo/Repo\"}}]}]}]}]}";
        var observedAt = new DateTimeOffset(2026, 8, 1, 2, 3, 4, TimeSpan.Zero);
        var digest = SkillProjectionHashing.InputDigest(payload);

        var result = LocalRepositoryObservationParser.Parse(7, payload, digest, "github-copilot-vscode", "1.2.3", observedAt);

        var occurrence = Assert.Single(result.Occurrences);
        Assert.Equal(7, occurrence.RawRecordId);
        Assert.Equal(digest, occurrence.RawPayloadSha256);
        Assert.Equal("github-copilot-vscode", occurrence.SourceSurface);
        Assert.Equal("1.2.3", occurrence.SourceApplicationVersion);
        Assert.Equal(observedAt, occurrence.ObservedAt);
        Assert.Equal("4d18db25a0968d4971b5857d488610a152b5855c64d1d01d0eb58f5e16f93337", occurrence.SourceIdentitySha256);
        var link = Assert.Single(result.ContextLinks);
        Assert.Equal("00112233445566778899aabbccddeeff", link.TraceId);
        Assert.Equal("0123456789abcdef", link.SpanId);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"intValue\":\"1\"}")]
    [InlineData("{\"boolValue\":true}")]
    [InlineData("{\"bytesValue\":\"eA==\"}")]
    [InlineData("{\"arrayValue\":{}}")]
    [InlineData("{\"kvlistValue\":{}}")]
    [InlineData("{\"stringValue\":null}")]
    public void ParseRejectsEveryNonStringAnyValueArm(string valueJson)
    {
        var result = Parse(
            "{\"resourceSpans\":[{\"resource\":{\"attributes\":[]},\"scopeSpans\":[{\"spans\":[{\"traceId\":\"00112233445566778899aabbccddeeff\",\"spanId\":\"0123456789abcdef\",\"attributes\":[{\"key\":\"vcs.repository.url.full\",\"value\":"
            + valueJson
            + "}]}]}]}]}");

        Assert.Equal(LocalRepositoryOccurrenceClassification.InvalidType, Assert.Single(result.Occurrences).Classification);
    }

    [Fact]
    public void ParseKeepsDifferentApprovedKeysAsSeparateAdmittedOccurrences()
    {
        var result = Parse("""
            {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[{"traceId":"00112233445566778899aabbccddeeff","spanId":"0123456789abcdef","attributes":[{"key":"vcs.repository.url.full","value":{"stringValue":"https://github.com/Octo/Repo"}},{"key":"copilot_chat.repo.remote_url","value":{"stringValue":"git@github.com:octo/repo"}}]}]}]}]}
            """);

        Assert.Equal(2, result.Occurrences.Count);
        Assert.All(result.Occurrences, occurrence =>
        {
            Assert.Equal(LocalRepositoryOccurrenceClassification.Admitted, occurrence.Classification);
            Assert.Equal("github.com/octo/repo", occurrence.Locator!.CanonicalLocator);
        });
    }

    [Fact]
    public void ParseKeepsDifferentApprovedKeysWithDifferentLocatorsAsDistinctAdmissions()
    {
        var result = Parse("""
            {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[{"traceId":"00112233445566778899aabbccddeeff","spanId":"0123456789abcdef","attributes":[{"key":"vcs.repository.url.full","value":{"stringValue":"https://github.com/Octo/One"}},{"key":"copilot_chat.repo.remote_url","value":{"stringValue":"git@github.com:octo/two"}}]}]}]}]}
            """);

        Assert.Equal(["github.com/octo/one", "github.com/octo/two"], result.Occurrences.Select(occurrence => occurrence.Locator!.CanonicalLocator));
        Assert.All(result.ContextLinks, link => Assert.Equal(LocalRepositoryAdmissionState.Admitted, link.AdmissionState));
    }

    [Fact]
    public void ParseUsesZeroBasedNestedOrdinalsAndRetainsLocatorDisplayCasing()
    {
        var result = Parse("""
            {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[]}]},{"resource":{"attributes":[]},"scopeSpans":[{"spans":[]},{"spans":[{"traceId":"00112233445566778899aabbccddeeff","spanId":"0123456789abcdef","attributes":[{"key":"ignored","value":{"stringValue":"x"}},{"key":"vcs.repository.url.full","value":{"stringValue":"https://github.com/Octo/MyRepo.git"}}]}]}]}]}
            """);

        var occurrence = Assert.Single(result.Occurrences);
        Assert.Equal((1, 1, 0, 1), (occurrence.ResourceSpanOrdinal, occurrence.ScopeSpanOrdinal!.Value, occurrence.SpanOrdinal!.Value, occurrence.AttributeOrdinal));
        Assert.Equal("Octo", occurrence.Locator!.DisplayOwner);
        Assert.Equal("MyRepo", occurrence.Locator.DisplayRepository);
    }

    [Fact]
    public void ParseShadowsResourceOccurrencesWhenAnyApprovedSpanKeyIsPresent()
    {
        var result = Parse("""
            {"resourceSpans":[{"resource":{"attributes":[{"key":"vcs.repository.url.full","value":{"stringValue":"https://github.com/Octo/Resource"}}]},"scopeSpans":[{"spans":[{"traceId":"00112233445566778899aabbccddeeff","spanId":"0123456789abcdef","attributes":[{"key":"copilot_chat.repo.remote_url","value":{"stringValue":"not-a-locator"}}]}]}]}]}
            """);

        Assert.Collection(
            result.ContextLinks.OrderBy(link => link.Occurrence.ScopeKind),
            resource => Assert.Equal(LocalRepositoryAdmissionState.Shadowed, resource.AdmissionState),
            span => Assert.Equal(LocalRepositoryAdmissionState.InvalidLocator, span.AdmissionState));
    }

    [Theory]
    [InlineData("{\"intValue\":\"1\"}", "InvalidType")]
    [InlineData("{\"stringValue\":\"https://github.com/Octo/Span\"}", "DuplicateKey")]
    public void ParseDoesNotFallbackToResourceForInvalidOrDuplicateSpanEvidence(string spanValue, string expectedSpanState)
    {
        var isDuplicate = expectedSpanState == "DuplicateKey";
        var spanAttributes = isDuplicate
            ? $"[{{\"key\":\"vcs.repository.url.full\",\"value\":{spanValue}}},{{\"key\":\"vcs.repository.url.full\",\"value\":{spanValue}}}]"
            : $"[{{\"key\":\"vcs.repository.url.full\",\"value\":{spanValue}}}]";
        var result = Parse(
            "{\"resourceSpans\":[{\"resource\":{\"attributes\":[{\"key\":\"vcs.repository.url.full\",\"value\":{\"stringValue\":\"https://github.com/Octo/Resource\"}}]},\"scopeSpans\":[{\"spans\":[{\"traceId\":\"00112233445566778899aabbccddeeff\",\"spanId\":\"0123456789abcdef\",\"attributes\":"
            + spanAttributes
            + "}]}]}]}");

        Assert.Contains(result.ContextLinks, link => link.Occurrence.ScopeKind == LocalRepositoryObservationScopeKind.Resource && link.AdmissionState == LocalRepositoryAdmissionState.Shadowed);
        Assert.All(result.ContextLinks.Where(link => link.Occurrence.ScopeKind == LocalRepositoryObservationScopeKind.Span), link => Assert.Equal(expectedSpanState, link.AdmissionState.ToString()));
    }

    [Fact]
    public void ParseReferencesOneResourceOccurrenceFromEverySpanContext()
    {
        var result = Parse("""
            {"resourceSpans":[{"resource":{"attributes":[{"key":"vcs.repository.url.full","value":{"stringValue":"https://github.com/Octo/Repo"}}]},"scopeSpans":[{"spans":[{"traceId":"00112233445566778899aabbccddeeff","spanId":"0123456789abcdef","attributes":[]},{"traceId":"11112222333344445555666677778888","spanId":"fedcba9876543210","attributes":[]}]}]}]}
            """);

        var occurrence = Assert.Single(result.Occurrences);
        Assert.Equal(2, result.ContextLinks.Count);
        Assert.All(result.ContextLinks, link =>
        {
            Assert.Same(occurrence, link.Occurrence);
            Assert.Equal(LocalRepositoryAdmissionState.Admitted, link.AdmissionState);
        });
    }

    private static LocalRepositoryObservationParseResult Parse(string payloadJson) =>
        LocalRepositoryObservationParser.Parse(
            rawRecordId: 7,
            payloadJson,
            rawPayloadSha256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            sourceSurface: "github-copilot-cli",
            sourceApplicationVersion: null,
            observedAt: DateTimeOffset.UnixEpoch);
}
