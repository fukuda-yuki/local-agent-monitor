using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.InstructionFindings;

namespace CopilotAgentObservability.InstructionFindings.Tests;

public sealed class InstructionFindingHandoffConsumerV1Tests
{
    [Fact]
    public void Validate_AcceptsPinnedCanonicalWireAndReturnsOnlyRunIdentity()
    {
        var canonicalBytes = CanonicalFixtureBytes();
        var analysisRunId = InstructionFindingHandoffConsumerV1.Validate(canonicalBytes);

        Assert.Equal(123, analysisRunId);
        Assert.Equal(PinnedCanonicalSha256(), Sha256(canonicalBytes));
        var validate = Assert.Single(
            typeof(InstructionFindingHandoffConsumerV1).GetMethods(),
            method => method.DeclaringType == typeof(InstructionFindingHandoffConsumerV1));
        Assert.Equal(typeof(long), validate.ReturnType);
        Assert.Empty(typeof(InstructionFindingHandoffConsumerValidationException).GetConstructors());
    }

    [Fact]
    public void Validate_AcceptsFrozenZeroFindingCarrier()
    {
        var canonicalBytes = Encoding.UTF8.GetBytes(
            "{\"schema_version\":\"instruction-finding-handoff.v1\",\"analysis_run_id\":1,\"findings\":[],\"candidates\":[]}");

        Assert.Equal(1, InstructionFindingHandoffConsumerV1.Validate(canonicalBytes));
    }

    [Fact]
    public void Validate_RejectsUnchangedSemanticSchemaFixtureAsNoncanonicalWireBytes()
    {
        var semanticFixture = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "TestData", "instruction-finding-handoff.semantic.v1.json"));

        Assert.Throws<InstructionFindingHandoffConsumerValidationException>(() =>
            InstructionFindingHandoffConsumerV1.Validate(semanticFixture));
        Assert.Equal(
            "b6a632df8d2a33e743dcf359bad7ee522e6c549b1287af695b62a406fc150987",
            Sha256(semanticFixture));
        Assert.NotEqual(PinnedCanonicalSha256(), Sha256(semanticFixture));
    }

    [Theory]
    [MemberData(nameof(InvalidCanonicalCarriers))]
    public void Validate_RejectsNoncanonicalOrMutatedCarrier(string _, byte[] carrier)
    {
        var exception = Assert.Throws<InstructionFindingHandoffConsumerValidationException>(() =>
            InstructionFindingHandoffConsumerV1.Validate(carrier));

        Assert.Equal("The instruction finding handoff is invalid.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Validate_RejectsCarrierAboveExactByteCeiling()
    {
        var oversized = new byte[InstructionFindingHandoffConsumerV1.MaxPayloadBytes + 1];

        Assert.Throws<InstructionFindingHandoffConsumerValidationException>(() =>
            InstructionFindingHandoffConsumerV1.Validate(oversized));
    }

    [Fact]
    public void Validate_RejectsJsonAboveExactDepthCeiling()
    {
        var deep = Encoding.UTF8.GetBytes(
            new string('[', InstructionFindingHandoffConsumerV1.MaxJsonDepth + 1)
            + "0"
            + new string(']', InstructionFindingHandoffConsumerV1.MaxJsonDepth + 1));

        Assert.Throws<InstructionFindingHandoffConsumerValidationException>(() =>
            InstructionFindingHandoffConsumerV1.Validate(deep));
    }

    public static IEnumerable<object[]> InvalidCanonicalCarriers()
    {
        var canonicalBytes = CanonicalFixtureBytes();
        var canonical = Encoding.UTF8.GetString(canonicalBytes);
        using var document = JsonDocument.Parse(canonical);
        var findingId = document.RootElement.GetProperty("findings")[0].GetProperty("finding_id").GetString()!;
        var references = document.RootElement.GetProperty("findings")[0].GetProperty("evidence_refs")
            .EnumerateArray()
            .Select(reference => reference.GetRawText())
            .ToArray();
        yield return Case("empty", []);
        yield return Case("leading whitespace", " " + canonical);
        yield return Case("trailing newline", canonical + "\n");
        yield return Case("utf8 bom", [0xef, 0xbb, 0xbf, .. canonicalBytes]);
        yield return Case("property order", canonical.Replace(
            "{\"schema_version\":\"instruction-finding-handoff.v1\",\"analysis_run_id\":123",
            "{\"analysis_run_id\":123,\"schema_version\":\"instruction-finding-handoff.v1\"",
            StringComparison.Ordinal));
        yield return Case("duplicate property", canonical.Replace(
            "{\"schema_version\":",
            "{\"schema_version\":\"instruction-finding-handoff.v1\",\"schema_version\":",
            StringComparison.Ordinal));
        yield return Case("unknown property", canonical[..^1] + ",\"unexpected\":true}");
        yield return Case("unknown version", canonical.Replace(
            "instruction-finding-handoff.v1",
            "instruction-finding-handoff.v2",
            StringComparison.Ordinal));
        yield return Case("unknown enum", canonical.Replace(
            "goal_clarity",
            "new_category",
            StringComparison.Ordinal));
        yield return Case("derived identity", canonical.Replace(
            findingId,
            "instruction-finding-000000000000000000000000",
            StringComparison.Ordinal));
        yield return Case("candidate association", ReplaceLast(
            canonical,
            findingId,
            "instruction-finding-000000000000000000000000"));
        yield return Case("evidence ordering", canonical.Replace(
            references[0] + "," + references[1],
            references[1] + "," + references[0],
            StringComparison.Ordinal));
        yield return Case("fixed template", canonical.Replace(
            JsonSerializer.Serialize("達成する成果の定義が不足している。"),
            JsonSerializer.Serialize("変更された文章"),
            StringComparison.Ordinal));
        yield return Case("reference token", canonical.Replace(
            "trace-ref-f5ae5df5128a218007b6681270f7ff01",
            "raw-trace-id",
            StringComparison.Ordinal));
        yield return Case("invalid utf8", new byte[] { 0xff });
    }

    private static object[] Case(string name, string carrier) => Case(name, Encoding.UTF8.GetBytes(carrier));

    private static object[] Case(string name, byte[] carrier) => [name, carrier];

    private static string ReplaceLast(string value, string oldValue, string newValue)
    {
        var index = value.LastIndexOf(oldValue, StringComparison.Ordinal);
        return value[..index] + newValue + value[(index + oldValue.Length)..];
    }

    private static byte[] CanonicalFixtureBytes() => Convert.FromBase64String(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "TestData", "instruction-finding-handoff.canonical.base64")).Trim());

    private static string PinnedCanonicalSha256() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "TestData", "instruction-finding-handoff.canonical.sha256")).Trim();

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
