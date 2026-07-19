using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SensitiveBundlePlanBuilderTests
{
    [Fact]
    public void Build_CreatesDeterministicCanonicalMembersAndMetadata()
    {
        var reservation = Reservation();
        var candidates = new[]
        {
            new SensitiveBundlePlanCandidate("diagcand-0002", "trace-two", [new RawEvidenceFragment("prompt", "private-locator-two", "private.path.two", "raw-two")]),
            new SensitiveBundlePlanCandidate("diagcand-0001", "trace-one", [new RawEvidenceFragment("tool_results", "private-locator-one", "private.path.one", "raw-one")]),
        };
        var sources = new[] { new SensitiveBundleSourceInput("C:\\private\\raw.json", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "raw-otlp") };

        var first = SensitiveBundlePlanBuilder.Build(reservation, candidates, sources);
        var second = SensitiveBundlePlanBuilder.Build(reservation, candidates.Reverse().ToArray(), sources);

        Assert.Equal(first.ManifestUtf8, second.ManifestUtf8);
        Assert.Equal(first.Members.Select(member => member.RelativePath), second.Members.Select(member => member.RelativePath));
        Assert.Equal(new[] { ".retention-owner.v1", "evidence", "evidence/diagcand-0001.json", "evidence/diagcand-0002.json", "manifest.json" }, first.Members.Select(member => member.RelativePath));
        Assert.Equal(new[] { 4, 3, 0, 1, 2 }, first.Members.Select(member => member.DeletionOrder));
        Assert.All(first.Members.Where(member => member.Kind == RetentionFileCaptureMemberKind.File), member =>
        {
            Assert.Equal(member.Utf8.Length, member.ByteLength);
            Assert.Equal(SHA256.HashData(member.Utf8), member.Sha256);
        });
        Assert.Equal(first.MarkerSha256, SHA256.HashData(first.Members[0].Utf8));
        Assert.Null(first.Members[0].Sha256);
        Assert.Equal(first.ManifestSha256, SHA256.HashData(first.ManifestUtf8));
        Assert.Equal("bundle:0123456789abcdef0123456789abcdef:diagcand-0001", first.EntriesByCandidateId["diagcand-0001"].EvidenceRef);
        Assert.Equal(typeof(SensitiveBundlePlan).FullName, first.ToString());
        Assert.Equal(typeof(SensitiveBundlePlannedMember).FullName, first.Members[0].ToString());
    }

    [Fact]
    public void Build_ExcludesPrivateLocatorTokenAndRawValueFromManifestButKeepsRawEvidenceInEvidenceMember()
    {
        var reservation = Reservation();
        const string raw = "PRIVATE_RAW_VALUE";
        const string locator = "C:\\PRIVATE\\locator";
        var plan = SensitiveBundlePlanBuilder.Build(
            reservation,
            [new SensitiveBundlePlanCandidate("diagcand-0001", "trace", [new RawEvidenceFragment("prompt", locator, "C:\\PRIVATE\\path", raw)])],
            [new SensitiveBundleSourceInput("C:\\PRIVATE\\source.json", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "raw-otlp")]);

        var manifest = Encoding.UTF8.GetString(plan.ManifestUtf8);
        var evidence = Encoding.UTF8.GetString(plan.Members.Single(member => member.RelativePath == "evidence/diagcand-0001.json").Utf8);

        Assert.DoesNotContain("PRIVATE", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("00112233445566778899aabbccddeeff", manifest, StringComparison.Ordinal);
        using var evidenceDocument = JsonDocument.Parse(evidence);
        Assert.Equal(raw, evidenceDocument.RootElement.GetProperty("fragments")[0].GetProperty("value").GetString());
        Assert.Equal(locator, evidenceDocument.RootElement.GetProperty("source_locator").GetString());
        Assert.DoesNotContain("C:\\PRIVATE\\source.json", manifest, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"raw-otlp\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"sha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsInvalidOrCollidingCandidateIdsWithSanitizedFailure()
    {
        var reservation = Reservation();
        var invalid = Assert.Throws<ArgumentException>(() => SensitiveBundlePlanBuilder.Build(reservation, [new SensitiveBundlePlanCandidate("../PRIVATE", null, [])], []));
        var collision = Assert.Throws<ArgumentException>(() => SensitiveBundlePlanBuilder.Build(reservation, [new SensitiveBundlePlanCandidate("diagcand-0001", null, []), new SensitiveBundlePlanCandidate("diagcand-0001", null, [])], []));

        Assert.Equal("Invalid sensitive bundle plan input.", invalid.Message);
        Assert.Equal(invalid.Message, collision.Message);
        Assert.DoesNotContain("PRIVATE", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsZeroFragmentsWithSanitizedFailure()
    {
        var exception = Assert.Throws<ArgumentException>(() => SensitiveBundlePlanBuilder.Build(Reservation(), [new SensitiveBundlePlanCandidate("diagcand-0001", null, [])], []));

        Assert.Equal("Invalid sensitive bundle plan input.", exception.Message);
    }

    [Theory]
    [InlineData(null, "locator", "path", "value")]
    [InlineData("", "locator", "path", "value")]
    [InlineData("prompt", null, "path", "value")]
    [InlineData("prompt", "locator", null, "value")]
    [InlineData("prompt", "locator", "path", null)]
    public void Build_RejectsMalformedRawFragmentWithSanitizedFailure(string? contentKind, string? locator, string? sourcePath, string? value)
    {
        var fragment = new RawEvidenceFragment(contentKind!, locator!, sourcePath!, value!);

        var exception = Assert.Throws<ArgumentException>(() => SensitiveBundlePlanBuilder.Build(
            Reservation(),
            [new SensitiveBundlePlanCandidate("diagcand-0001", null, [fragment])],
            []));

        Assert.Equal("Invalid sensitive bundle plan input.", exception.Message);
        Assert.DoesNotContain("locator", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("value", exception.Message, StringComparison.Ordinal);
    }

    private static RetentionFileCaptureReservation Reservation() => new(
        "0123456789abcdef0123456789abcdef",
        "fedcba9876543210fedcba9876543210",
        RetentionCapturePhase.Reserved,
        "C:\\private\\parent",
        "C:\\private\\staging",
        "C:\\private\\final",
        Convert.FromHexString("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"),
        "2026-07-19T01:02:03.0000000+00:00",
        new DateTimeOffset(2026, 7, 19, 1, 2, 3, TimeSpan.Zero).UtcDateTime.Ticks,
        null,
        null,
        []);
}
