using System.Reflection;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvocationSnapshotReceiptFingerprintTests
{
    private const byte KindNull = 0x00;
    private const byte KindUtf8 = 0x01;
    private const byte KindBool = 0x02;
    private const byte KindUint64 = 0x03;
    private const byte KindUtcTime = 0x04;
    private const byte KindSha256 = 0x05;

    private const string GoldenDigest = "5698c710512676dab263596e169be6e73746525a695f67b7929866fbc502cfb7";

    [Fact]
    public void BuildFrame_GoldenInput_MatchesCheckedInGoldenFrameAndDigest()
    {
        var frame = SkillInvocationSnapshotReceiptFingerprint.BuildFrame(GoldenInput());
        var golden = ReadGoldenFrame();

        Assert.Equal(726, frame.Length);
        Assert.Equal(golden, frame);
        Assert.Equal(GoldenDigest, SkillInvocationSnapshotReceiptFingerprint.Compute(GoldenInput()));
    }

    [Fact]
    public void BuildFrame_GoldenInput_StartsWithDomainPrefixAndFieldCount()
    {
        var frame = SkillInvocationSnapshotReceiptFingerprint.BuildFrame(GoldenInput());

        var expectedPrefix = new byte[]
        {
            (byte)'s', (byte)'k', (byte)'i', (byte)'l', (byte)'l', (byte)'-',
            (byte)'i', (byte)'n', (byte)'v', (byte)'o', (byte)'c', (byte)'a', (byte)'t', (byte)'i', (byte)'o', (byte)'n', (byte)'-',
            (byte)'s', (byte)'n', (byte)'a', (byte)'p', (byte)'s', (byte)'h', (byte)'o', (byte)'t', (byte)'-',
            (byte)'r', (byte)'e', (byte)'c', (byte)'e', (byte)'i', (byte)'p', (byte)'t',
            0x00,
            (byte)'v', (byte)'1',
            0x00,
        };

        Assert.Equal(expectedPrefix, frame[..expectedPrefix.Length]);
        Assert.Equal(new byte[] { 0x00, 0x1d }, frame[expectedPrefix.Length..(expectedPrefix.Length + 2)]);
    }

    [Fact]
    public void BuildFrame_GoldenInput_HasTwentyNineAscendingFieldsWithSpecKinds()
    {
        var frame = SkillInvocationSnapshotReceiptFingerprint.BuildFrame(GoldenInput());
        var fields = ParseFields(frame);

        Assert.Equal(29, fields.Count);
        Assert.Equal(Enumerable.Range(1, 29), fields.Select(field => (int)field.FieldId));

        var expectedKinds = new Dictionary<int, byte>
        {
            [1] = KindUtf8,
            [2] = KindUtf8,
            [3] = KindUtf8,
            [4] = KindUtf8,
            [5] = KindNull,
            [6] = KindNull,
            [7] = KindBool,
            [8] = KindNull,
            [9] = KindNull,
            [10] = KindUtcTime,
            [11] = KindUtf8,
            [12] = KindUtf8,
            [13] = KindUtf8,
            [14] = KindUtf8,
            [15] = KindUtf8,
            [16] = KindSha256,
            [17] = KindSha256,
            [18] = KindUint64,
            [19] = KindUtf8,
            [20] = KindUtf8,
            [21] = KindUtf8,
            [22] = KindUtf8,
            [23] = KindUtf8,
            [24] = KindSha256,
            [25] = KindUint64,
            [26] = KindSha256,
            [27] = KindUint64,
            [28] = KindUtf8,
            [29] = KindSha256,
        };

        foreach (var field in fields)
        {
            Assert.Equal(expectedKinds[field.FieldId], field.Kind);
        }
    }

    [Fact]
    public void BuildFrame_AllNullableFieldsNull_EmitsNullKindAndNeverAnEmptyUtf8Field()
    {
        var input = GoldenInput() with
        {
            Name = null,
            Source = null,
            Trigger = null,
            BodySha256 = null,
            BodyUtf8Bytes = null,
            DefinitionPathSha256 = null,
            DefinitionPathUtf8Bytes = null,
        };

        var fields = ParseFields(SkillInvocationSnapshotReceiptFingerprint.BuildFrame(input));

        foreach (var nullFieldId in new[] { 5, 6, 8, 9, 21, 22, 23, 24, 25, 26, 27 })
        {
            var field = fields.Single(candidate => candidate.FieldId == nullFieldId);
            Assert.Equal(KindNull, field.Kind);
            Assert.Empty(field.Payload);
        }

        Assert.DoesNotContain(fields, field => field.Kind == KindUtf8 && field.Payload.Length == 0);
    }

    [Fact]
    public void Compute_PayloadBytesChangedAlone_ChangesDigest()
    {
        var changed = GoldenInput() with { PayloadBytes = 43UL };

        Assert.NotEqual(GoldenDigest, SkillInvocationSnapshotReceiptFingerprint.Compute(changed));
    }

    [Fact]
    public void Compute_NativeSessionIdChangedAlone_ChangesDigest()
    {
        var changed = GoldenInput() with { NativeSessionId = "session-B" };

        Assert.NotEqual(GoldenDigest, SkillInvocationSnapshotReceiptFingerprint.Compute(changed));
    }

    [Fact]
    public void Compute_OccurredAtChangedByOneTickAlone_ChangesDigest()
    {
        var golden = GoldenInput();
        var changed = golden with { OccurredAt = golden.OccurredAt.AddTicks(1) };

        Assert.NotEqual(GoldenDigest, SkillInvocationSnapshotReceiptFingerprint.Compute(changed));
    }

    [Fact]
    public void Compute_NameChangedToNullAlone_ChangesDigest()
    {
        var changed = GoldenInput() with { Name = null };

        Assert.NotEqual(GoldenDigest, SkillInvocationSnapshotReceiptFingerprint.Compute(changed));
    }

    [Fact]
    public void InputType_HasNoMemberForAnyServerGeneratedIdentity()
    {
        var forbidden = new[] { "snapshotid", "eventid", "sessionid", "claimid", "contentitemid", "createdat", "writeat" };
        var memberNames = typeof(SkillInvocationSnapshotReceiptFingerprintInput)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(member => Normalize(member.Name))
            .ToArray();

        foreach (var name in forbidden)
        {
            Assert.DoesNotContain(name, memberNames);
        }
    }

    [Fact]
    public void BuildFrame_SixtyThreeCharacterDigest_Throws()
    {
        var input = GoldenInput() with { SchemaFingerprint = new string('a', 63) };

        Assert.Throws<ArgumentException>(() => SkillInvocationSnapshotReceiptFingerprint.BuildFrame(input));
    }

    [Fact]
    public void BuildFrame_UppercaseHexDigest_Throws()
    {
        var input = GoldenInput() with { SchemaFingerprint = new string('A', 64) };

        Assert.Throws<ArgumentException>(() => SkillInvocationSnapshotReceiptFingerprint.BuildFrame(input));
    }

    [Fact]
    public void BuildFrame_NonHexDigest_Throws()
    {
        var input = GoldenInput() with { SchemaFingerprint = new string('g', 64) };

        Assert.Throws<ArgumentException>(() => SkillInvocationSnapshotReceiptFingerprint.BuildFrame(input));
    }

    [Fact]
    public void BuildFrame_LoneHighSurrogateInAnyUtf8Field_Throws()
    {
        var input = GoldenInput() with { NativeSessionId = "\ud800" };

        Assert.Throws<EncoderFallbackException>(() => SkillInvocationSnapshotReceiptFingerprint.BuildFrame(input));
    }

    [Fact]
    public void Compute_OccurredAtWithNonUtcOffset_ConvertsToUtcAndMatchesEquivalentInstant()
    {
        var golden = GoldenInput();
        var equivalentNonUtc = golden with
        {
            OccurredAt = new DateTimeOffset(2026, 8, 9, 9, 0, 0, TimeSpan.FromHours(9)),
        };

        Assert.Equal(golden.OccurredAt.ToUniversalTime(), equivalentNonUtc.OccurredAt.ToUniversalTime());
        Assert.Equal(GoldenDigest, SkillInvocationSnapshotReceiptFingerprint.Compute(equivalentNonUtc));
    }

    private static string Normalize(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static SkillInvocationSnapshotReceiptFingerprintInput GoldenInput() => new(
        SourceAdapter: "copilot-sdk-stream",
        SourceEventId: "123e4567-e89b-42d3-a456-426614174000",
        SourceSurface: "copilot-sdk",
        NativeSessionId: "session-A",
        RunNativeId: null,
        SourceParentEventId: null,
        SourceEphemeral: false,
        TraceId: null,
        SpanId: null,
        OccurredAt: new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
        SourceApplicationVersion: "1.0.65",
        AdapterVersion: "copilot-sdk-dotnet-1.0.4+cao-skill-v2.1",
        NormalizationVersion: "github-copilot-sdk.skill-invoked.normalize.v1",
        PayloadSchema: "github-copilot-sdk.skill-invoked.v1",
        SchemaFingerprint: "8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c",
        PayloadSha256: new string('2', 64),
        PayloadBytes: 42UL,
        State: "available",
        Reason: "none",
        Name: "review",
        Source: "project",
        Trigger: "user-invoked",
        BodySha256: new string('3', 64),
        BodyUtf8Bytes: 7UL,
        DefinitionPathSha256: new string('4', 64),
        DefinitionPathUtf8Bytes: 12UL,
        ContentDocumentSha256: new string('5', 64));

    private static byte[] ReadGoldenFrame() =>
        Convert.FromHexString(File.ReadAllText(GoldenPath()).Trim());

    private static string GoldenPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "tests",
                "CopilotAgentObservability.LocalMonitor.Tests",
                "TestData",
                "SkillInvocationSnapshot",
                "request-fingerprint-v1.golden.hex");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Checked-in skill invocation snapshot receipt fingerprint golden was not found.");
    }

    private static List<ParsedField> ParseFields(byte[] frame)
    {
        var prefixLength = Encoding.UTF8.GetByteCount("skill-invocation-snapshot-receipt") + 1
            + Encoding.UTF8.GetByteCount("v1") + 1;
        var offset = prefixLength;
        var count = (frame[offset] << 8) | frame[offset + 1];
        offset += 2;

        var fields = new List<ParsedField>(count);
        for (var i = 0; i < count; i++)
        {
            var fieldId = (frame[offset] << 8) | frame[offset + 1];
            offset += 2;
            var kind = frame[offset];
            offset += 1;
            var length = (frame[offset] << 24) | (frame[offset + 1] << 16) | (frame[offset + 2] << 8) | frame[offset + 3];
            offset += 4;
            var payload = frame[offset..(offset + length)];
            offset += length;
            fields.Add(new ParsedField(fieldId, kind, payload));
        }

        Assert.Equal(frame.Length, offset);
        return fields;
    }

    private readonly record struct ParsedField(int FieldId, byte Kind, byte[] Payload);
}
