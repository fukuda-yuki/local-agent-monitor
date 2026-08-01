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

    [Theory]
    [InlineData("{\"line\":\"a\\nb\"}", "1d88cec5ba51e84a68beaf90e86b4d09e6681b75887e466ad7f40a11dc0b187f")]
    [InlineData("{\"line\":\"a\\r\\nb\"}", "5b3a5dc7248392002fc4011ef4f7d7c3fcc912078a19d10bd2bef156c3d9c67c")]
    [InlineData("{\"name\":\"é\"}", "2f16b8477146a1b2ba7d6bb7cf7c9979c191cc2838a107dbf5f0d920b4cb3ba1")]
    [InlineData("{\"name\":\"é\"}", "6a547588c4055916e090ed61467326046dd52810679d03bfb84d862a7123522e")]
    [InlineData("﻿{}", "aa25e978046d680ef8740d837e6de5bc1e2a2dc6089dbda1012544b538d53f65")]
    [InlineData("{}", "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a")]
    [InlineData("{} ", "3d8e6a3f3561a8683abc16c3851110c4e607124c4c5a919cb7e78764882f37ad")]
    [InlineData("{}\n", "ca3d163bab055381827226140568f3bef7eaac187cebd76878e0b63e9e442356")]
    public void InputDigestUsesExactPersistedPayloadUtf8Bytes(string payloadJson, string expectedDigest)
    {
        Assert.Equal(expectedDigest, SkillProjectionHashing.InputDigest(payloadJson));
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
