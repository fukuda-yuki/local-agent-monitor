using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvocationMetadataDocumentV1Tests
{
    private const string GoldenSha256 = "e3fe1403b13bebbc46856c2220828d333d7513fb398a8521c8dff8b9b5e0130f";

    private static readonly string[] ExpectedPropertyOrder =
    [
        "schema_version", "snapshot_id", "session_id", "claim_id", "event_id", "name", "source", "trigger",
        "invoked_at", "run_id", "trace_id", "span_id", "projection_validity", "snapshot_state",
        "snapshot_reason", "body_sha256", "body_utf8_bytes", "definition_path_sha256",
        "definition_path_utf8_bytes", "captured_at", "source_application_version",
        "adapter_version", "payload_schema"
    ];

    [Fact]
    public void Golden_ChecksumAndVectorCountMatchTheCheckedInFixture()
    {
        var bytes = File.ReadAllBytes(GoldenPath());

        Assert.Equal(GoldenSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));

        using var golden = JsonDocument.Parse(bytes);
        Assert.Equal(17, golden.RootElement.GetProperty("vectors").GetArrayLength());
    }

    [Fact]
    public void Write_EveryGoldenVector_ProducesExactPinnedStatusAndBytes()
    {
        AssertAllGoldenVectorsMatch();
    }

    [Fact]
    public void Write_EveryGoldenVector_IsCultureIndependent()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            AssertAllGoldenVectorsMatch();
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Write_AvailableDocument_EmitsPropertiesInExactSpecOrderWithNoneOmittedOrAdded()
    {
        var response = SkillInvocationMetadataDocumentV1.Write(SampleAvailableInput());
        using var document = JsonDocument.Parse(response.BodyUtf8);

        var actualOrder = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();

        Assert.Equal(ExpectedPropertyOrder, actualOrder);
    }

    [Fact]
    public void Write_AvailableWithNullOptionals_EmitsSourceTriggerRunIdAsPresentLiteralNulls()
    {
        using var golden = JsonDocument.Parse(File.ReadAllBytes(GoldenPath()));
        var input = BuildInput(FindVector(golden, "available-live-current-optionals-null"));

        var response = SkillInvocationMetadataDocumentV1.Write(input);
        using var document = JsonDocument.Parse(response.BodyUtf8);

        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("source").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("trigger").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("run_id").ValueKind);
    }

    [Fact]
    public void Write_Every200Vector_EmitsTraceIdAndSpanIdAsPresentLiteralNulls()
    {
        using var golden = JsonDocument.Parse(File.ReadAllBytes(GoldenPath()));
        foreach (var vector in golden.RootElement.GetProperty("vectors").EnumerateArray())
        {
            if (vector.GetProperty("status").GetInt32() != 200)
            {
                continue;
            }

            var name = vector.GetProperty("name").GetString();
            using var document = JsonDocument.Parse(SkillInvocationMetadataDocumentV1.Write(BuildInput(vector)).BodyUtf8);

            Assert.True(document.RootElement.TryGetProperty("trace_id", out var traceId), $"Vector '{name}' is missing trace_id.");
            Assert.Equal(JsonValueKind.Null, traceId.ValueKind);
            Assert.True(document.RootElement.TryGetProperty("span_id", out var spanId), $"Vector '{name}' is missing span_id.");
            Assert.Equal(JsonValueKind.Null, spanId.ValueKind);
        }
    }

    [Fact]
    public void DeriveState_AvailableReadable_ProjectionValidityTracksTheSameDiagnosticToken()
    {
        foreach (var token in new[] { "current", "stale", "invalid" })
        {
            var derived = SkillInvocationMetadataDocumentV1.DeriveState(
                SampleAvailableSnapshot(), SkillInvocationRetentionProjectionV1.Readable, token);

            Assert.Equal(SkillInvocationMetadataDocumentOutcomeV1.Document, derived.Outcome);
            Assert.Equal(token, derived.ProjectionValidity);
            Assert.Equal("available", derived.SnapshotState);
            Assert.Equal("none", derived.SnapshotReason);
        }
    }

    [Fact]
    public void DeriveState_FaultReadable_ProjectionValidityIsAlwaysInvalidRegardlessOfAnySuppliedDiagnosticToken()
    {
        foreach (var token in new[] { "current", "stale", "invalid", null })
        {
            var derived = SkillInvocationMetadataDocumentV1.DeriveState(
                new SkillInvocationMetadataPersistedSnapshotV1.Fault(SkillInvocationV2PayloadState.Malformed, SkillInvocationV2PayloadReason.DuplicateProperty),
                SkillInvocationRetentionProjectionV1.Readable,
                token);

            Assert.Equal(SkillInvocationMetadataDocumentOutcomeV1.Document, derived.Outcome);
            Assert.Equal("invalid", derived.ProjectionValidity);
            Assert.Equal("malformed", derived.SnapshotState);
            Assert.Equal("duplicate_property", derived.SnapshotReason);
        }
    }

    [Fact]
    public void DeriveState_AvailableUnreadable_DerivesExpiredSnapshotStateWhileValidityStillTracksTheDiagnosticToken()
    {
        foreach (var token in new[] { "current", "stale", "invalid" })
        {
            var derived = SkillInvocationMetadataDocumentV1.DeriveState(
                SampleAvailableSnapshot(), SkillInvocationRetentionProjectionV1.RetainedDeletedOrTombstoned, token);

            Assert.Equal(SkillInvocationMetadataDocumentOutcomeV1.Document, derived.Outcome);
            Assert.Equal(token, derived.ProjectionValidity);
            Assert.Equal("expired", derived.SnapshotState);
            Assert.Equal("none", derived.SnapshotReason);
        }
    }

    [Fact]
    public void DeriveState_FaultUnreadable_DerivesExpiredSnapshotStateWithThePersistedReasonAndInvalidValidity()
    {
        var derived = SkillInvocationMetadataDocumentV1.DeriveState(
            new SkillInvocationMetadataPersistedSnapshotV1.Fault(SkillInvocationV2PayloadState.Binary, SkillInvocationV2PayloadReason.BodyUnicodeInvalid),
            SkillInvocationRetentionProjectionV1.RetainedDeletedOrTombstoned,
            "current");

        Assert.Equal(SkillInvocationMetadataDocumentOutcomeV1.Document, derived.Outcome);
        Assert.Equal("invalid", derived.ProjectionValidity);
        Assert.Equal("expired", derived.SnapshotState);
        Assert.Equal("body_unicode_invalid", derived.SnapshotReason);
    }

    [Fact]
    public void DeriveState_NoSnapshotRow_IsTheNotFoundFlag()
    {
        var derived = SkillInvocationMetadataDocumentV1.DeriveState(null, SkillInvocationRetentionProjectionV1.None, null);

        Assert.Equal(SkillInvocationMetadataDocumentOutcomeV1.NotFound, derived.Outcome);
        Assert.Null(derived.ProjectionValidity);
        Assert.Null(derived.SnapshotState);
        Assert.Null(derived.SnapshotReason);
    }

    [Fact]
    public void DeriveState_InconsistentGraph_IsTheUnavailableFlagRegardlessOfAnyPersistedSnapshot()
    {
        var withoutSnapshot = SkillInvocationMetadataDocumentV1.DeriveState(null, SkillInvocationRetentionProjectionV1.Inconsistent, null);
        var withSnapshot = SkillInvocationMetadataDocumentV1.DeriveState(
            SampleAvailableSnapshot(), SkillInvocationRetentionProjectionV1.Inconsistent, "current");

        Assert.Equal(SkillInvocationMetadataDocumentOutcomeV1.Unavailable, withoutSnapshot.Outcome);
        Assert.Equal(SkillInvocationMetadataDocumentOutcomeV1.Unavailable, withSnapshot.Outcome);
    }

    [Fact]
    public void Write_FlippingRetentionToUnreadable_PreservesSafeFieldsAndOnlyChangesSnapshotState()
    {
        using var golden = JsonDocument.Parse(File.ReadAllBytes(GoldenPath()));
        var input = BuildInput(FindVector(golden, "available-live-current"));
        var expiredInput = input with { Retention = SkillInvocationRetentionProjectionV1.RetainedDeletedOrTombstoned };

        var liveResponse = SkillInvocationMetadataDocumentV1.Write(input);
        var expiredResponse = SkillInvocationMetadataDocumentV1.Write(expiredInput);

        using var liveDocument = JsonDocument.Parse(liveResponse.BodyUtf8);
        using var expiredDocument = JsonDocument.Parse(expiredResponse.BodyUtf8);

        Assert.Equal(liveDocument.RootElement.GetProperty("claim_id").GetString(), expiredDocument.RootElement.GetProperty("claim_id").GetString());
        Assert.Equal(liveDocument.RootElement.GetProperty("name").GetString(), expiredDocument.RootElement.GetProperty("name").GetString());
        Assert.Equal(liveDocument.RootElement.GetProperty("body_sha256").GetString(), expiredDocument.RootElement.GetProperty("body_sha256").GetString());
        Assert.Equal(liveDocument.RootElement.GetProperty("body_utf8_bytes").GetUInt64(), expiredDocument.RootElement.GetProperty("body_utf8_bytes").GetUInt64());
        Assert.Equal(liveDocument.RootElement.GetProperty("definition_path_sha256").GetString(), expiredDocument.RootElement.GetProperty("definition_path_sha256").GetString());
        Assert.Equal(liveDocument.RootElement.GetProperty("definition_path_utf8_bytes").GetUInt64(), expiredDocument.RootElement.GetProperty("definition_path_utf8_bytes").GetUInt64());

        Assert.Equal("available", liveDocument.RootElement.GetProperty("snapshot_state").GetString());
        Assert.Equal("expired", expiredDocument.RootElement.GetProperty("snapshot_state").GetString());

        var expectedExpiredVector = FindVector(golden, "available-expired-current");
        var expectedBytes = Encoding.UTF8.GetBytes(expectedExpiredVector.GetProperty("response_utf8").GetString()!);
        Assert.True(expectedBytes.AsSpan().SequenceEqual(expiredResponse.BodyUtf8), "Flipping retention to unreadable should reproduce the 'available-expired-current' golden vector.");
    }

    [Fact]
    public void Write_NoVectorEverEmitsProjectionInvalidAsSnapshotState()
    {
        using var golden = JsonDocument.Parse(File.ReadAllBytes(GoldenPath()));
        foreach (var vector in golden.RootElement.GetProperty("vectors").EnumerateArray())
        {
            if (vector.GetProperty("status").GetInt32() != 200)
            {
                continue;
            }

            using var document = JsonDocument.Parse(SkillInvocationMetadataDocumentV1.Write(BuildInput(vector)).BodyUtf8);

            Assert.NotEqual("projection_invalid", document.RootElement.GetProperty("snapshot_state").GetString());
        }
    }

    [Fact]
    public void Write_ByteCounts_AreUnquotedUnsignedIntegerTokensWithNoLeadingZeroSignOrExponent()
    {
        var zeroInput = SampleAvailableInput() with
        {
            PersistedSnapshot = SampleAvailableSnapshot() with { BodyUtf8Bytes = 0, DefinitionPathUtf8Bytes = 0 }
        };
        var zeroText = Encoding.UTF8.GetString(SkillInvocationMetadataDocumentV1.Write(zeroInput).BodyUtf8);
        AssertUnsignedIntegerToken(zeroText, "body_utf8_bytes");
        AssertUnsignedIntegerToken(zeroText, "definition_path_utf8_bytes");

        using var golden = JsonDocument.Parse(File.ReadAllBytes(GoldenPath()));
        foreach (var vector in golden.RootElement.GetProperty("vectors").EnumerateArray())
        {
            if (vector.GetProperty("status").GetInt32() != 200)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(SkillInvocationMetadataDocumentV1.Write(BuildInput(vector)).BodyUtf8);
            AssertUnsignedIntegerOrNullToken(text, "body_utf8_bytes");
            AssertUnsignedIntegerOrNullToken(text, "definition_path_utf8_bytes");
        }
    }

    private void AssertAllGoldenVectorsMatch()
    {
        using var golden = JsonDocument.Parse(File.ReadAllBytes(GoldenPath()));
        foreach (var vector in golden.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString();
            var expectedStatus = vector.GetProperty("status").GetInt32();
            var expectedUtf8 = Encoding.UTF8.GetBytes(vector.GetProperty("response_utf8").GetString()!);
            var expectedByteLength = vector.GetProperty("response_bytes").GetInt32();
            var expectedSha256 = vector.GetProperty("response_sha256").GetString();

            var response = SkillInvocationMetadataDocumentV1.Write(BuildInput(vector));

            Assert.Equal(expectedStatus, response.StatusCode);
            Assert.Equal(expectedByteLength, response.BodyUtf8.Length);
            Assert.True(expectedUtf8.AsSpan().SequenceEqual(response.BodyUtf8), $"Vector '{name}' body bytes did not match the golden response_utf8.");
            Assert.Equal(expectedSha256, Convert.ToHexStringLower(SHA256.HashData(response.BodyUtf8)));
        }
    }

    private static void AssertUnsignedIntegerToken(string documentText, string propertyName)
    {
        var match = Regex.Match(documentText, $"\"{Regex.Escape(propertyName)}\":([^,}}]*)");
        Assert.True(match.Success, $"Property '{propertyName}' was not found in the produced document.");
        Assert.Matches("^(0|[1-9][0-9]*)$", match.Groups[1].Value);
    }

    private static void AssertUnsignedIntegerOrNullToken(string documentText, string propertyName)
    {
        var match = Regex.Match(documentText, $"\"{Regex.Escape(propertyName)}\":([^,}}]*)");
        Assert.True(match.Success, $"Property '{propertyName}' was not found in the produced document.");
        Assert.Matches("^(null|0|[1-9][0-9]*)$", match.Groups[1].Value);
    }

    private static SkillInvocationMetadataDocumentV1Input BuildInput(JsonElement vector)
    {
        var retention = ParseRetention(vector.GetProperty("retention").GetString()!);
        var persistedStateText = vector.GetProperty("persisted_state").GetString();

        if (persistedStateText is null)
        {
            return new SkillInvocationMetadataDocumentV1Input(
                Guid.Empty, Guid.Empty, Guid.Empty, DateTimeOffset.UnixEpoch,
                PersistedSnapshot: null, retention, DiagnosticToken: null,
                DateTimeOffset.UnixEpoch, string.Empty, string.Empty, string.Empty);
        }

        using var document = JsonDocument.Parse(vector.GetProperty("response_utf8").GetString()!);
        var root = document.RootElement;

        SkillInvocationMetadataPersistedSnapshotV1 persistedSnapshot = persistedStateText == "available"
            ? new SkillInvocationMetadataPersistedSnapshotV1.Available(
                ClaimId: Guid.Parse(root.GetProperty("claim_id").GetString()!),
                Name: root.GetProperty("name").GetString()!,
                Source: root.GetProperty("source").GetString(),
                Trigger: root.GetProperty("trigger").GetString(),
                RunId: root.GetProperty("run_id").GetString() is { } runIdText ? Guid.Parse(runIdText) : null,
                BodySha256: root.GetProperty("body_sha256").GetString()!,
                BodyUtf8Bytes: root.GetProperty("body_utf8_bytes").GetUInt64(),
                DefinitionPathSha256: root.GetProperty("definition_path_sha256").GetString()!,
                DefinitionPathUtf8Bytes: root.GetProperty("definition_path_utf8_bytes").GetUInt64())
            : new SkillInvocationMetadataPersistedSnapshotV1.Fault(
                ParsePersistedState(persistedStateText),
                ParsePersistedReason(root.GetProperty("snapshot_reason").GetString()!));

        return new SkillInvocationMetadataDocumentV1Input(
            SnapshotId: Guid.Parse(root.GetProperty("snapshot_id").GetString()!),
            SessionId: Guid.Parse(root.GetProperty("session_id").GetString()!),
            EventId: Guid.Parse(root.GetProperty("event_id").GetString()!),
            InvokedAt: DateTimeOffset.Parse(root.GetProperty("invoked_at").GetString()!, CultureInfo.InvariantCulture),
            PersistedSnapshot: persistedSnapshot,
            Retention: retention,
            DiagnosticToken: vector.GetProperty("projection_validity").GetString(),
            CapturedAt: DateTimeOffset.Parse(root.GetProperty("captured_at").GetString()!, CultureInfo.InvariantCulture),
            SourceApplicationVersion: root.GetProperty("source_application_version").GetString()!,
            AdapterVersion: root.GetProperty("adapter_version").GetString()!,
            PayloadSchema: root.GetProperty("payload_schema").GetString()!);
    }

    private static SkillInvocationRetentionProjectionV1 ParseRetention(string text) => text switch
    {
        "readable" => SkillInvocationRetentionProjectionV1.Readable,
        "retained-deleted-or-tombstoned" => SkillInvocationRetentionProjectionV1.RetainedDeletedOrTombstoned,
        "none" => SkillInvocationRetentionProjectionV1.None,
        "inconsistent" => SkillInvocationRetentionProjectionV1.Inconsistent,
        _ => throw new InvalidOperationException($"Unrecognized golden retention token '{text}'.")
    };

    private static SkillInvocationV2PayloadState ParsePersistedState(string text) => text switch
    {
        "malformed" => SkillInvocationV2PayloadState.Malformed,
        "missing" => SkillInvocationV2PayloadState.Missing,
        "binary" => SkillInvocationV2PayloadState.Binary,
        "oversized" => SkillInvocationV2PayloadState.Oversized,
        _ => throw new InvalidOperationException($"Unrecognized golden persisted_state token '{text}'.")
    };

    private static SkillInvocationV2PayloadReason ParsePersistedReason(string text) => text switch
    {
        "duplicate_property" => SkillInvocationV2PayloadReason.DuplicateProperty,
        "unknown_property" => SkillInvocationV2PayloadReason.UnknownProperty,
        "invalid_field_type" => SkillInvocationV2PayloadReason.InvalidFieldType,
        "name_invalid" => SkillInvocationV2PayloadReason.NameInvalid,
        "path_invalid" => SkillInvocationV2PayloadReason.PathInvalid,
        "name_missing" => SkillInvocationV2PayloadReason.NameMissing,
        "body_missing" => SkillInvocationV2PayloadReason.BodyMissing,
        "definition_path_missing" => SkillInvocationV2PayloadReason.DefinitionPathMissing,
        "body_unicode_invalid" => SkillInvocationV2PayloadReason.BodyUnicodeInvalid,
        "path_unicode_invalid" => SkillInvocationV2PayloadReason.PathUnicodeInvalid,
        "body_oversized" => SkillInvocationV2PayloadReason.BodyOversized,
        "path_oversized" => SkillInvocationV2PayloadReason.PathOversized,
        _ => throw new InvalidOperationException($"Unrecognized golden snapshot_reason token '{text}'.")
    };

    private static JsonElement FindVector(JsonDocument golden, string name)
    {
        foreach (var vector in golden.RootElement.GetProperty("vectors").EnumerateArray())
        {
            if (vector.GetProperty("name").GetString() == name)
            {
                return vector;
            }
        }

        throw new InvalidOperationException($"Golden vector '{name}' was not found.");
    }

    private static SkillInvocationMetadataDocumentV1Input SampleAvailableInput() => new(
        SnapshotId: Guid.Parse("018f0f4e-7b2a-7c11-8a3f-123456789abc"),
        SessionId: Guid.Parse("018f0f4e-7b2a-7c11-8a3f-123456789abd"),
        EventId: Guid.Parse("018f0f4e-7b2a-7c11-8a3f-123456789ac0"),
        InvokedAt: new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
        PersistedSnapshot: SampleAvailableSnapshot(),
        Retention: SkillInvocationRetentionProjectionV1.Readable,
        DiagnosticToken: "current",
        CapturedAt: new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
        SourceApplicationVersion: "1.0.65",
        AdapterVersion: "copilot-sdk-dotnet-1.0.4+cao-skill-v2.1",
        PayloadSchema: "github-copilot-sdk.skill-invoked.v1");

    private static SkillInvocationMetadataPersistedSnapshotV1.Available SampleAvailableSnapshot() => new(
        ClaimId: Guid.Parse("018f0f4e-7b2a-7c11-8a3f-123456789abe"),
        Name: "review",
        Source: "project",
        Trigger: "user-invoked",
        RunId: Guid.Parse("018f0f4e-7b2a-7c11-8a3f-123456789abf"),
        BodySha256: new string('3', 64),
        BodyUtf8Bytes: 7,
        DefinitionPathSha256: new string('4', 64),
        DefinitionPathUtf8Bytes: 12);

    private static string GoldenPath() => FindRepoFile("TestData", "SkillInvocationSnapshot", "metadata-response-v1.golden.json");

    private static string FindRepoFile(params string[] relativeSegments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var segments = new[] { directory.FullName, "tests", "CopilotAgentObservability.LocalMonitor.Tests" }
                .Concat(relativeSegments)
                .ToArray();
            var candidate = Path.Combine(segments);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Checked-in fixture was not found: {Path.Combine(relativeSegments)}");
    }
}
