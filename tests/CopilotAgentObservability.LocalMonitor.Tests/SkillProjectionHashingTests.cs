using System.Reflection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillProjectionHashingTests
{
    private const string TraceId = "11111111111111111111111111111111";

    [Fact]
    public void FrontierDigestUsesExactV2TaggedLengthFraming()
    {
        var inputs = new List<SkillProjectionFrontierInput>
        {
            CreateInput(
                sourceObservationId: 2,
                rawRecordId: 7,
                evidenceKind: "PayloadSha256",
                rawPayloadSha256: new string('a', 64)),
            CreateInput(
                sourceObservationId: 3,
                rawRecordId: 8,
                evidenceKind: "DeletedBeforeDigestV10",
                rawPayloadSha256: null),
        };

        var method = typeof(SkillProjectionHashing).GetMethod(
            nameof(SkillProjectionHashing.FrontierDigest),
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            [typeof(string), typeof(IReadOnlyList<SkillProjectionFrontierInput>)],
            modifiers: null);

        Assert.NotNull(method);
        var digest = Assert.IsType<string>(method.Invoke(null, [TraceId, inputs]));
        Assert.Equal(
            "e53b4122a7c88483c0cc4adad7366ac4ac2f34bea354efae6abcbdfbc45ffa8f",
            digest);
    }

    [Fact]
    public void ReconciliationFingerprintUsesExactV2TaggedLengthFraming()
    {
        var request = SourceCompatibilityReconciliationRequest.Create(
            "hash-fixture",
            sourceObservationId: 3,
            TraceId,
            expectedInterpretationRevision: 0,
            SourceCompatibilityReconciliationTrigger.DecoderRevision,
            "resolver-2",
            "registry-2",
            "skill-projector-1");
        var input = CreateInput(
            sourceObservationId: 3,
            rawRecordId: 8,
            evidenceKind: "DeletedBeforeDigestV10",
            rawPayloadSha256: null);
        var method = typeof(SkillProjectionHashing).GetMethod(
            nameof(SkillProjectionHashing.ReconciliationFingerprint),
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            [typeof(SourceCompatibilityReconciliationRequest), typeof(SkillProjectionFrontierInput)],
            modifiers: null);

        Assert.NotNull(method);
        var digest = Assert.IsType<string>(method.Invoke(null, [request, input]));
        Assert.Equal(
            "5a0b1121d6302aad5cc497ca14696f3077d3a6f239c138ba8a4baba8726c93b6",
            digest);
    }

    private static SkillProjectionFrontierInput CreateInput(
        long sourceObservationId,
        long rawRecordId,
        string evidenceKind,
        string? rawPayloadSha256)
    {
        var constructor = typeof(SkillProjectionFrontierInput)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.GetParameters().Length == 4);
        Assert.NotNull(constructor);
        var kindType = constructor.GetParameters()[2].ParameterType;
        var kind = kindType.IsEnum
            ? Enum.Parse(kindType, evidenceKind, ignoreCase: false)
            : evidenceKind switch
            {
                "PayloadSha256" => "payload_sha256",
                "DeletedBeforeDigestV10" => "deleted_before_digest_v10",
                _ => throw new ArgumentOutOfRangeException(nameof(evidenceKind)),
            };
        return Assert.IsType<SkillProjectionFrontierInput>(
            constructor.Invoke([sourceObservationId, rawRecordId, kind, rawPayloadSha256]));
    }
}
