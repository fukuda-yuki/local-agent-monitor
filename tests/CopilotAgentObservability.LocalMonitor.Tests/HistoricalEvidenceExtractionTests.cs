using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class HistoricalEvidenceExtractionTests
{
    [Fact]
    public async Task ExtractAsync_SelectsMostRecentWindow_OrdersAscending_AndNeverReadsExcludedBodies()
    {
        var sessions = Enumerable.Range(1, 4)
            .Select(index => Metadata(index, startedAt: At(index)))
            .ToArray();
        var source = new RecordingSnapshotSource("snapshot-1", sessions[1..], Evidence, omittedEarlierMatchingSessionCount: 1);

        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(maximum: 2), source, CancellationToken.None);

        Assert.Equal([sessions[2].SessionId.ToString(), sessions[3].SessionId.ToString()], result.RawLocal.Sessions.Select(item => item.SessionId));
        Assert.Equal([sessions[1].SessionId.ToString()], result.RawLocal.ExcludedSessions.Select(item => item.SessionId));
        Assert.All(result.RawLocal.ExcludedSessions, item => Assert.Equal(HistoricalSessionExclusionReasonV1.WindowTruncated, item.Reason));
        Assert.True(result.RawLocal.TruncatedBefore);
        Assert.Equal(2, result.RawLocal.TruncatedSessionCount);
        Assert.Equal([sessions[2].SessionId, sessions[3].SessionId], source.ReadSessionIds);
        Assert.Equal(result.RawLocal.EvidenceGroups.Select(item => item.GroupId), result.RepositorySafe.EvidenceGroups.Select(item => item.GroupId));
    }

    [Fact]
    public async Task ExtractAsync_AppliesAllFiltersWithoutReadingFilterMismatches_AndRecordsMissingExplicitIds()
    {
        var included = Metadata(1, repository: "repo-a", task: "task-a", surface: SessionSourceSurface.CopilotCli);
        var mismatch = Metadata(2, repository: "repo-b", task: "task-a", surface: SessionSourceSurface.CopilotCli);
        var missing = Guid.Parse("018f0000-0000-7000-8000-000000000099");
        var source = new RecordingSnapshotSource("snapshot-2", [included, mismatch], Evidence);
        var selection = Selection(
            repository: "repo-a",
            explicitIds: [included.SessionId, mismatch.SessionId, missing],
            surfaces: [SessionSourceSurface.CopilotCli],
            task: "task-a");

        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(selection, source, CancellationToken.None);

        Assert.Single(result.RawLocal.Sessions);
        Assert.Collection(
            result.RawLocal.ExcludedSessions,
            item =>
            {
                Assert.Equal(mismatch.SessionId.ToString(), item.SessionId);
                Assert.Equal(HistoricalSessionExclusionReasonV1.FilterMismatch, item.Reason);
                Assert.Equal("repo-b", Assert.IsType<HistoricalDecisionMetadataV1>(item.Metadata).Repository);
            },
            item =>
            {
                Assert.Equal(missing.ToString(), item.SessionId);
                Assert.Equal(HistoricalSessionExclusionReasonV1.MissingSessionReference, item.Reason);
                Assert.Null(item.Metadata);
            });
        Assert.StartsWith("repository-ref-", result.RepositorySafe.ExcludedSessions[0].Metadata!.Repository);
        Assert.Null(result.RepositorySafe.ExcludedSessions[1].Metadata);
        Assert.Equal([included.SessionId], source.ReadSessionIds);
    }

    [Fact]
    public async Task ExtractAsync_KeepsPartialHistoricalSessionHonestAndSuppressesExactOnlyCapabilities()
    {
        var declared = Capabilities(all: true);
        var session = Metadata(
            1,
            completeness: SessionCompleteness.Partial,
            reasons: ["historical_summary_only"],
            sourceKind: HistoricalEvidenceSourceKindV1.HistoricalSummary,
            capabilities: declared);
        var source = new RecordingSnapshotSource("snapshot-3", [session], Evidence);

        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(Selection(), source, CancellationToken.None);

        var projected = Assert.Single(result.RepositorySafe.Sessions);
        Assert.Equal(SessionCompleteness.Partial, projected.Completeness);
        Assert.Contains("historical_summary_only", projected.CompletenessReasons);
        Assert.False(projected.Capabilities.RepeatedToolCall);
        Assert.False(projected.Capabilities.SubagentFanOut);
        Assert.False(projected.Capabilities.RawLocalDescriptor);
    }

    [Fact]
    public async Task ExtractAsync_RejectsUnresolvedEvidenceReferenceBeforePersistence()
    {
        var session = Metadata(1);
        var source = new RecordingSnapshotSource("snapshot-4", [session], _ =>
        [
            Group(HistoricalEvidenceGroupKindV1.ErrorSpan,
                references: [new(session.SessionId, "missing-trace", "span-1", null, HistoricalEvidenceRelativePositionV1.Anchor)])
        ]);

        var exception = await Assert.ThrowsAsync<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceExtractorV1.ExtractAsync(Selection(), source, CancellationToken.None).AsTask());

        Assert.Equal(HistoricalEvidenceValidationCodeV1.UnresolvedEvidenceReference, exception.Code);
    }

    [Theory]
    [InlineData("RepeatedToolCall")]
    [InlineData("SubagentFanOut")]
    public async Task ExtractAsync_RejectsExactCapabilityGroupsWithoutTheirExactCarrier(string kindName)
    {
        var kind = Enum.Parse<HistoricalEvidenceGroupKindV1>(kindName);
        var session = Metadata(1);
        var source = new RecordingSnapshotSource("snapshot-5", [session], _ => [Group(kind)]);

        var exception = await Assert.ThrowsAsync<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceExtractorV1.ExtractAsync(Selection(), source, CancellationToken.None).AsTask());

        Assert.Equal(HistoricalEvidenceValidationCodeV1.MissingExactCapability, exception.Code);
    }

    [Fact]
    public async Task ExtractAsync_ProjectsValidatedExactOnlyGroupsWithoutRawCarriersInRepositorySafeForm()
    {
        var session = Metadata(1);
        var source = new RecordingSnapshotSource("snapshot-exact-carriers", [session], _ =>
        [
            Group(
                HistoricalEvidenceGroupKindV1.RepeatedToolCall,
                references: [Reference(session)],
                exactCallId: "call-1"),
            Group(
                HistoricalEvidenceGroupKindV1.SubagentFanOut,
                references: [Reference(session)],
                exactOwnershipId: "ownership-1"),
        ]);

        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(Selection(), source, CancellationToken.None);

        Assert.Equal(2, result.RawLocal.EvidenceGroups.Count);
        Assert.Equal("call-1", result.RawLocal.EvidenceGroups[0].ExactCallId);
        Assert.Equal("ownership-1", result.RawLocal.EvidenceGroups[1].ExactOwnershipId);
        Assert.All(result.RepositorySafe.EvidenceGroups, group =>
        {
            Assert.Null(group.ExactCallId);
            Assert.Null(group.ExactOwnershipId);
        });
    }

    [Fact]
    public async Task ExtractAsync_SeparatesBoundedRawDescriptorFromRepositorySafeForm()
    {
        var session = Metadata(1);
        var longDescriptor = new string('a', 170) + "\nignored second line";
        var source = new RecordingSnapshotSource("snapshot-6", [session], _ => [Group(
            HistoricalEvidenceGroupKindV1.UserCorrection,
            references: [Reference(session)],
            rawDescriptor: longDescriptor)]);

        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(Selection(), source, CancellationToken.None);

        Assert.Equal(new string('a', 160), Assert.Single(result.RawLocal.Sessions).RawLocalDescriptor);
        Assert.Null(Assert.Single(result.RepositorySafe.Sessions).RawLocalDescriptor);
        Assert.DoesNotContain(new string('a', 20), Encoding.UTF8.GetString(result.RepositorySafeBytes));
    }

    [Fact]
    public async Task ExtractAsync_SelectsDescriptorDeterministicallyIndependentOfDraftOrder()
    {
        var session = Metadata(1);
        var alpha = Group(HistoricalEvidenceGroupKindV1.UserCorrection, references: [Reference(session)], rawDescriptor: "alpha descriptor");
        var zeta = Group(HistoricalEvidenceGroupKindV1.UserCorrection, references: [Reference(session)], rawDescriptor: "zeta descriptor");

        var first = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(), new RecordingSnapshotSource("snapshot-descriptor-order", [session], _ => [zeta, alpha]), CancellationToken.None);
        var second = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(), new RecordingSnapshotSource("snapshot-descriptor-order", [session], _ => [alpha, zeta]), CancellationToken.None);

        Assert.Equal("alpha descriptor", Assert.Single(first.RawLocal.Sessions).RawLocalDescriptor);
        Assert.Equal(first.RawLocalBytes, second.RawLocalBytes);
        Assert.Equal(first.RepositorySafeBytes, second.RepositorySafeBytes);
    }

    [Fact]
    public async Task ExtractAsync_SanitizedOnlyDoesNotRequestDescriptorBearingEvidence()
    {
        var session = Metadata(1);
        var source = new RecordingSnapshotSource("snapshot-7", [session], Evidence);

        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(Selection(sanitizedOnly: true), source, CancellationToken.None);

        Assert.Equal([false], source.IncludeDescriptors);
        Assert.Equal(HistoricalDescriptorStateV1.NotRequested, Assert.Single(result.RawLocal.Sessions).DescriptorState);
    }

    [Theory]
    [InlineData("person@example.com")]
    [InlineData("C:\\Users\\person\\secret.txt")]
    [InlineData("C:\\secret.txt")]
    [InlineData("\\\\server\\share\\secret.txt")]
    [InlineData("/secret")]
    [InlineData("../secret.txt")]
    [InlineData("~/.config/secret")]
    [InlineData("C:secret.txt")]
    [InlineData("github_pat_abcdefghijklmnopqrstuvwxyz123456")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("password=correct-horse-battery-staple")]
    [InlineData("Customer SSN 123-45-6789")]
    [InlineData("Call Alice at 555-123-4567")]
    [InlineData("Alice Smith, 123 Main Street")]
    [InlineData("Alice Smith")]
    [InlineData("password=hunter2; call Alice at +1 555-123-4567")]
    public async Task ExtractAsync_RejectsSensitiveDescriptorFromBothRepresentations(string descriptor)
    {
        var session = Metadata(1);
        var source = new RecordingSnapshotSource("snapshot-8", [session], _ => [Group(
            HistoricalEvidenceGroupKindV1.UserCorrection,
            references: [Reference(session)],
            rawDescriptor: descriptor)]);

        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(Selection(), source, CancellationToken.None);

        var raw = Assert.Single(result.RawLocal.Sessions);
        Assert.Equal(HistoricalDescriptorStateV1.RejectedSensitive, raw.DescriptorState);
        Assert.Null(raw.RawLocalDescriptor);
        Assert.DoesNotContain(descriptor, Encoding.UTF8.GetString(result.RawLocalBytes));
        Assert.DoesNotContain(descriptor, Encoding.UTF8.GetString(result.RepositorySafeBytes));
    }

    [Fact]
    public async Task ExtractAsync_IsByteEquivalentAndUsesIssue59OpaqueReferenceForms()
    {
        var session = Metadata(1);
        var source = new RecordingSnapshotSource("snapshot-9", [session], Evidence);

        var first = await HistoricalEvidenceExtractorV1.ExtractAsync(Selection(), source, CancellationToken.None);
        var second = await HistoricalEvidenceExtractorV1.ExtractAsync(Selection(), source, CancellationToken.None);

        Assert.Equal(first.RawLocalBytes, second.RawLocalBytes);
        Assert.Equal(first.RepositorySafeBytes, second.RepositorySafeBytes);
        Assert.Contains("\"source_surface\":\"copilot-sdk\"", Encoding.UTF8.GetString(first.RawLocalBytes));
        Assert.DoesNotContain("copilot_sdk", Encoding.UTF8.GetString(first.RawLocalBytes));
        var projected = Assert.Single(Assert.Single(first.RepositorySafe.EvidenceGroups).References);
        Assert.Matches("^session-ref-[0-9a-f]{32}$", projected.SessionId);
        Assert.Matches("^trace-ref-[0-9a-f]{32}$", projected.TraceId);
        Assert.Matches("^span-ref-[0-9a-f]{32}$", projected.SpanId);
    }

    [Fact]
    public async Task ExtractAsync_UsesAnyCapturedSurfaceAndDoesNotAuthorizeDescriptorsWithoutCapability()
    {
        var session = Metadata(1, surface: SessionSourceSurface.CopilotSdk, capabilities: Capabilities(all: true) with { RawLocalDescriptor = false }) with
        {
            SourceSurfaces = new[] { SessionSourceSurface.ClaudeCode, SessionSourceSurface.CopilotSdk }.Order().ToArray(),
            SourceProvenance = new HistoricalSourceProvenanceV1[]
            {
                new(SessionSourceSurface.ClaudeCode, "1.0.0", "adapter.v1"),
                new(SessionSourceSurface.CopilotSdk, "1.0.0", "adapter.v1"),
            }.OrderBy(value => value.SourceSurface).ToArray(),
        };
        var source = new RecordingSnapshotSource("snapshot-multi-surface", [session], Evidence);

        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(surfaces: [SessionSourceSurface.ClaudeCode]), source, CancellationToken.None);

        Assert.Single(result.RawLocal.Sessions);
        Assert.Equal([false], source.IncludeDescriptors);
    }

    [Theory]
    [InlineData("C:device-relative", null)]
    [InlineData(null, "C:device-relative")]
    public async Task ExtractAsync_RejectsDeviceRelativeExactCarriers(string? callId, string? ownershipId)
    {
        var session = Metadata(1);
        var kind = callId is null ? HistoricalEvidenceGroupKindV1.SubagentFanOut : HistoricalEvidenceGroupKindV1.RepeatedToolCall;
        var source = new RecordingSnapshotSource("snapshot-device-relative", [session], _ =>
        [
            Group(kind, [Reference(session)], exactCallId: callId, exactOwnershipId: ownershipId),
        ]);

        var exception = await Assert.ThrowsAsync<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceExtractorV1.ExtractAsync(Selection(), source, CancellationToken.None).AsTask());

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidContract, exception.Code);
    }

    [Fact]
    public async Task Deserialize_RejectsNullModelProperty()
    {
        var session = Metadata(1);
        session = session with { ModelObservations = [new("gpt-5", Reference(session))] };
        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(), new RecordingSnapshotSource("snapshot-null-model", [session], Evidence), CancellationToken.None);
        var node = JsonNode.Parse(result.RawLocalBytes)!.AsObject();
        node["sessions"]!.AsArray()[0]!["metadata"]!["model_observations"]!.AsArray()[0]!["model"] = null;

        var exception = Assert.Throws<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceJsonV1.Deserialize(Encoding.UTF8.GetBytes(node.ToJsonString())));

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidSerialization, exception.Code);
    }

    [Fact]
    public async Task ExtractAsync_OmitsSsnShapedRepositoryMetadataFromSafeBytes()
    {
        const string sensitive = "Customer SSN 123-45-6789";
        var session = Metadata(1, repository: sensitive);

        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(repository: sensitive), new RecordingSnapshotSource("snapshot-safe-ssn", [session], Evidence), CancellationToken.None);

        Assert.Equal(sensitive, result.RawLocal.Selection.Repository);
        Assert.Null(result.RepositorySafe.Selection.Repository);
        Assert.Null(Assert.Single(result.RepositorySafe.Sessions).Metadata.Repository);
        Assert.DoesNotContain(sensitive, Encoding.UTF8.GetString(result.RepositorySafeBytes));
    }

    [Fact]
    public async Task ExtractAsync_RejectsDurationWithoutExactEvidenceReference()
    {
        var session = Metadata(1) with
        {
            DurationObservations = [new HistoricalRawDurationObservationV1(1, null!)],
        };

        var exception = await Assert.ThrowsAsync<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceExtractorV1.ExtractAsync(
                Selection(), new RecordingSnapshotSource("snapshot-null-duration-ref", [session], Evidence), CancellationToken.None).AsTask());

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidContract, exception.Code);
    }

    [Theory]
    [InlineData("source_version", "sk-abcdefghijklmnopqrstuv")]
    [InlineData("adapter_version", "prompt:steal")]
    [InlineData("provenance_version", "sk-abcdefghijklmnopqrstuv")]
    [InlineData("model", "sk-abcdefghijklmnopqrstuv")]
    public async Task ExtractAsync_FailsClosedForSensitiveIdentifierMetadata(string target, string sensitive)
    {
        var session = Metadata(1);
        session = target switch
        {
            "source_version" => session with
            {
                SourceVersion = sensitive,
                SourceProvenance = [new(session.SourceSurface, sensitive, session.AdapterVersion)],
            },
            "adapter_version" => session with
            {
                AdapterVersion = sensitive,
                SourceProvenance = [new(session.SourceSurface, session.SourceVersion, sensitive)],
            },
            "provenance_version" => session with
            {
                SourceProvenance = [new(session.SourceSurface, sensitive, session.AdapterVersion)],
            },
            "model" => session with { ModelObservations = [new(sensitive, Reference(session))] },
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

        var exception = await Assert.ThrowsAsync<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceExtractorV1.ExtractAsync(
                Selection(), new RecordingSnapshotSource("snapshot-sensitive-metadata", [session], Evidence), CancellationToken.None).AsTask());

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidContract, exception.Code);
    }

    [Theory]
    [InlineData("TokenRollup", "cache_read_token")]
    [InlineData("CacheRollup", "input_token")]
    public async Task ExtractAsync_RejectsMismatchedClosedScalarComponentUnit(string kindName, string unit)
    {
        var session = Metadata(1);
        var source = new RecordingSnapshotSource("snapshot-scalar-unit", [session], _ =>
        [
            Group(Enum.Parse<HistoricalEvidenceGroupKindV1>(kindName), [Reference(session)], 1, unit),
        ]);

        var exception = await Assert.ThrowsAsync<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceExtractorV1.ExtractAsync(Selection(), source, CancellationToken.None).AsTask());

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidContract, exception.Code);
    }

    [Fact]
    public async Task JsonSchema_RejectsRepresentationWrongNestedReferencesNullLocationAndInvalidScalarToken()
    {
        var session = Metadata(1);
        session = session with { ModelObservations = [new("gpt-5", Reference(session))] };
        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(), new RecordingSnapshotSource("snapshot-schema-negative", [session], Evidence), CancellationToken.None);
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "TestData", "HistoricalEvidence", "historical-evidence-dataset.schema.json");
        var cases = new List<JsonObject>();

        var wrongSafeReference = JsonNode.Parse(result.RepositorySafeBytes)!.AsObject();
        wrongSafeReference["sessions"]![0]!["metadata"]!["model_observations"]![0]!["evidence_ref"]!["trace_id"] = "raw-trace";
        cases.Add(wrongSafeReference);
        var nullLocation = JsonNode.Parse(result.RawLocalBytes)!.AsObject();
        nullLocation["sessions"]![0]!["metadata"]!["model_observations"]![0]!["evidence_ref"]!["span_id"] = null;
        nullLocation["sessions"]![0]!["metadata"]!["model_observations"]![0]!["evidence_ref"]!["turn_index"] = null;
        cases.Add(nullLocation);
        var invalidToken = JsonNode.Parse(result.RawLocalBytes)!.AsObject();
        invalidToken["evidence_groups"]![0]!["unit"] = "not a token";
        cases.Add(invalidToken);

        foreach (var node in cases)
        {
            var path = Path.Combine(Path.GetTempPath(), $"historical-schema-negative-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(path, node.ToJsonString());
                Assert.False(ValidateWithPowerShellJsonSchema(path, schemaPath));
            }
            finally { File.Delete(path); }
        }
    }

    [Fact]
    public void ProductionReadBounds_AcceptExactMaximaAndRejectOneOverWithoutAllocatingArtifacts()
    {
        var type = typeof(HistoricalEvidenceApplicationServiceV1).Assembly.GetType(
            "CopilotAgentObservability.LocalMonitor.Analysis.HistoricalEvidenceReadBoundsV1");
        Assert.NotNull(type);
        var validate = type!.GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(validate);

        validate!.Invoke(null,
            [51_200, 64L * 1024 * 1024, 4_096, 256]);
        foreach (var values in new object[][]
        {
            [51_201, 0L, 0, 0],
            [0, 64L * 1024 * 1024 + 1, 0, 0],
            [0, 0L, 4_097, 0],
            [0, 0L, 0, 257],
        })
            Assert.IsType<HistoricalEvidenceValidationException>(Assert.Throws<TargetInvocationException>(
                () => validate.Invoke(null, values)).InnerException);
    }

    [Fact]
    public async Task ExtractAsync_UsesFrozenCanonicalIdentityAndPayloadHashVector()
    {
        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(),
            new RecordingSnapshotSource("snapshot-vector", [Metadata(1)], Evidence),
            CancellationToken.None);

        Assert.Equal("historical-extraction-723e7878a6d85fea9dffc2282bd46761", result.RawLocal.ExtractionId);
        Assert.Equal("historical-group-03ceab92561b879ce10ff5db999279d2", Assert.Single(result.RawLocal.EvidenceGroups).GroupId);
        Assert.Equal("54b95650a59eab2099187de842bad0a382e0879244eb98287fd19972cc4ba1bc", result.RawLocalSha256);
        Assert.Equal("0299a3106cd20c8f94e1a3f69934b24742dfcc2f533451ceb09f4cdb0b4765d7", result.RepositorySafeSha256);
    }

    [Fact]
    public async Task ExtractAsync_RecordsMixedSourceAndCompletenessDistributionWithoutPromotion()
    {
        var sessions = new[]
        {
            Metadata(1, completeness: SessionCompleteness.Full, sourceKind: HistoricalEvidenceSourceKindV1.LiveOtel),
            Metadata(2, completeness: SessionCompleteness.Rich, sourceKind: HistoricalEvidenceSourceKindV1.SavedRaw),
            Metadata(
                3,
                completeness: SessionCompleteness.Partial,
                reasons: ["historical_summary_only"],
                sourceKind: HistoricalEvidenceSourceKindV1.HistoricalSummary),
        };

        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(), new RecordingSnapshotSource("snapshot-mixed", sessions, Evidence), CancellationToken.None);

        Assert.Equal(
            [("full", 1), ("partial", 1), ("rich", 1)],
            result.RepositorySafe.Distribution.Completeness.Select(item => (item.Key, item.Count)));
        Assert.Equal(
            [("historical_summary", 1), ("live_otel", 1), ("saved_raw", 1)],
            result.RepositorySafe.Distribution.SourceKinds.Select(item => (item.Key, item.Count)));
        var historical = result.RepositorySafe.Sessions.Single(item => item.SourceKind == HistoricalEvidenceSourceKindV1.HistoricalSummary);
        Assert.Equal(SessionCompleteness.Partial, historical.Completeness);
        Assert.False(historical.Capabilities.RepeatedToolCall);
        Assert.False(historical.Capabilities.SubagentFanOut);
    }

    [Fact]
    public async Task JsonSchema_ActualValidator_AcceptsRawLocalAndRepositorySafeRepresentations()
    {
        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(),
            new RecordingSnapshotSource("snapshot-schema", [Metadata(1)], Evidence),
            CancellationToken.None);
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "TestData", "HistoricalEvidence", "historical-evidence-dataset.schema.json");
        var rawPath = Path.Combine(Path.GetTempPath(), $"historical-evidence-raw-{Guid.NewGuid():N}.json");
        var safePath = Path.Combine(Path.GetTempPath(), $"historical-evidence-safe-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllBytes(rawPath, result.RawLocalBytes);
            File.WriteAllBytes(safePath, result.RepositorySafeBytes);

            Assert.True(ValidateWithPowerShellJsonSchema(rawPath, schemaPath));
            Assert.True(ValidateWithPowerShellJsonSchema(safePath, schemaPath));
        }
        finally
        {
            File.Delete(rawPath);
            File.Delete(safePath);
        }
    }

    [Fact]
    public async Task ExtractAsync_RepositorySafeSelectionContainsNoExplicitRawSessionId()
    {
        var session = Metadata(1);
        var source = new RecordingSnapshotSource("snapshot-safe-selection", [session], Evidence);

        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(explicitIds: [session.SessionId]), source, CancellationToken.None);

        var safeId = Assert.Single(result.RepositorySafe.Selection.ExplicitSessionIds);
        Assert.Matches("^session-ref-[0-9a-f]{32}$", safeId);
        Assert.DoesNotContain(session.SessionId.ToString(), Encoding.UTF8.GetString(result.RepositorySafeBytes));
    }

    [Fact]
    public async Task ExtractAsync_RejectsSensitiveSelectionLabelsBeforeOpeningSnapshot()
    {
        var source = new RecordingSnapshotSource("snapshot-sensitive-selection", [Metadata(1)], Evidence);

        var exception = await Assert.ThrowsAsync<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceExtractorV1.ExtractAsync(
                Selection(repository: "person@example.com"), source, CancellationToken.None).AsTask());

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidContract, exception.Code);
        Assert.Empty(source.ReadSessionIds);
    }

    [Fact]
    public async Task ExtractAsync_CollapsesByteIdenticalEvidenceGroups()
    {
        var session = Metadata(1);
        var group = Group(
            HistoricalEvidenceGroupKindV1.ErrorSpan,
            references: [Reference(session)],
            status: "error");
        var source = new RecordingSnapshotSource("snapshot-duplicate-group", [session], _ => [group, group]);

        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(Selection(), source, CancellationToken.None);

        Assert.Single(result.RawLocal.EvidenceGroups);
        Assert.Single(result.RepositorySafe.EvidenceGroups);
    }

    [Fact]
    public async Task ExtractAsync_RejectsRawDescriptorOnNonCorrectionEvidence()
    {
        var session = Metadata(1);
        var source = new RecordingSnapshotSource("snapshot-invalid-descriptor", [session], _ =>
        [
            Group(
                HistoricalEvidenceGroupKindV1.TurnRollup,
                references: [Reference(session)],
                numericValue: 1,
                unit: "turn",
                rawDescriptor: "Use the narrower command next time.")
        ]);

        var exception = await Assert.ThrowsAsync<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceExtractorV1.ExtractAsync(Selection(), source, CancellationToken.None).AsTask());

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidContract, exception.Code);
    }

    [Fact]
    public async Task ExtractAsync_InstructionFindingMustExistInCoherentSnapshot()
    {
        const string findingId = "instruction-finding-0123456789abcdef01234567";
        var session = Metadata(1);
        var source = new RecordingSnapshotSource("snapshot-finding", [session], _ =>
        [
            new(HistoricalEvidenceGroupKindV1.InstructionFinding, [Reference(session)], null, null, null, null, null, null, findingId, null)
        ]);

        var exception = await Assert.ThrowsAsync<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceExtractorV1.ExtractAsync(Selection(), source, CancellationToken.None).AsTask());

        Assert.Equal(HistoricalEvidenceValidationCodeV1.UnresolvedEvidenceReference, exception.Code);
    }

    [Fact]
    public async Task ExtractAsync_InstructionFindingCarriesExactIssue59ReceiptAssociation()
    {
        var session = Metadata(1);
        var reference = Reference(session);
        var secondReference = reference with { SpanId = "span-2", TurnIndex = 2 };
        var instructionReference = new InstructionRawEvidenceReferenceV1(
            session.SessionId.ToString(), reference.TraceId, reference.SpanId, reference.TurnIndex,
            InstructionEvidenceRelativePositionV1.Anchor);
        var secondInstructionReference = new InstructionRawEvidenceReferenceV1(
            session.SessionId.ToString(), secondReference.TraceId, secondReference.SpanId, secondReference.TurnIndex,
            InstructionEvidenceRelativePositionV1.Anchor);
        var handoff = InstructionFindingPipelineV1.Generate(
            72,
            new InstructionFindingEvidenceIndexV1(reference.TraceId,
            [
                new(session.SessionId.ToString(), reference.TraceId, reference.SpanId, reference.TurnIndex,
                    InstructionEvidenceRelativePositionV1.Anchor, InstructionFindingEvidenceKindV1.Turn),
                new(session.SessionId.ToString(), secondReference.TraceId, secondReference.SpanId, secondReference.TurnIndex,
                    InstructionEvidenceRelativePositionV1.Anchor, InstructionFindingEvidenceKindV1.Turn),
            ]),
            [new(InstructionFindingCategoryV1.GoalClarity, InstructionFindingVerdictV1.Supported,
                InstructionFindingExtractorSourceV1.DeterministicPrepass, [instructionReference, secondInstructionReference])]);
        var receipt = Assert.Single(handoff.Findings);
        var candidate = Assert.Single(handoff.Candidates);
        session = session with
        {
            InstructionFindingIds = [receipt.FindingId],
            EvidenceLocations = [.. session.EvidenceLocations, new(secondReference.SessionId, secondReference.TraceId, secondReference.SpanId, secondReference.TurnIndex, secondReference.RelativePosition)],
        };
        var source = new RecordingSnapshotSource("snapshot-finding-associated", [session], _ =>
        [
            Group(HistoricalEvidenceGroupKindV1.InstructionFinding, [reference, secondReference], findingId: receipt.FindingId,
                findingReceipt: receipt, findingCandidate: candidate),
        ]);

        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(Selection(), source, CancellationToken.None);

        var raw = Assert.Single(result.RawLocal.EvidenceGroups);
        var safe = Assert.Single(result.RepositorySafe.EvidenceGroups);
        Assert.Equal(receipt, raw.FindingReceipt);
        Assert.Equal(receipt, safe.FindingReceipt);
        Assert.Equal(candidate, raw.FindingCandidate);
        Assert.Equal(candidate, safe.FindingCandidate);
        Assert.Equal(receipt.EvidenceRefs, safe.FindingReceipt!.EvidenceRefs);
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "TestData", "HistoricalEvidence", "historical-evidence-dataset.schema.json");
        var rawPath = Path.Combine(Path.GetTempPath(), $"historical-finding-raw-{Guid.NewGuid():N}.json");
        var safePath = Path.Combine(Path.GetTempPath(), $"historical-finding-safe-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllBytes(rawPath, result.RawLocalBytes);
            File.WriteAllBytes(safePath, result.RepositorySafeBytes);
            Assert.True(ValidateWithPowerShellJsonSchema(rawPath, schemaPath));
            Assert.True(ValidateWithPowerShellJsonSchema(safePath, schemaPath));
        }
        finally
        {
            File.Delete(rawPath);
            File.Delete(safePath);
        }
    }

    [Fact]
    public async Task ExtractAsync_InstructionFindingRejectsReceiptWhoseEvidenceDoesNotMatchGroup()
    {
        var session = Metadata(1);
        var reference = Reference(session);
        var instructionReference = new InstructionRawEvidenceReferenceV1(
            session.SessionId.ToString(), reference.TraceId, reference.SpanId, reference.TurnIndex,
            InstructionEvidenceRelativePositionV1.Anchor);
        var handoff = InstructionFindingPipelineV1.Generate(
            72,
            new InstructionFindingEvidenceIndexV1(reference.TraceId,
            [
                new(session.SessionId.ToString(), reference.TraceId, reference.SpanId, reference.TurnIndex,
                    InstructionEvidenceRelativePositionV1.Anchor, InstructionFindingEvidenceKindV1.Turn),
            ]),
            [new(InstructionFindingCategoryV1.GoalClarity, InstructionFindingVerdictV1.Incomplete,
                InstructionFindingExtractorSourceV1.PromptOnly, [instructionReference])]);
        var receipt = Assert.Single(handoff.Findings);
        var mismatched = reference with { SpanId = "different-span" };
        session = session with
        {
            InstructionFindingIds = [receipt.FindingId],
            EvidenceLocations = [.. session.EvidenceLocations, new(mismatched.SessionId, mismatched.TraceId, mismatched.SpanId, mismatched.TurnIndex, mismatched.RelativePosition)],
        };
        var source = new RecordingSnapshotSource("snapshot-finding-mismatch", [session], _ =>
        [
            Group(HistoricalEvidenceGroupKindV1.InstructionFinding, [mismatched], findingId: receipt.FindingId,
                findingReceipt: receipt),
        ]);

        var exception = await Assert.ThrowsAsync<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceExtractorV1.ExtractAsync(Selection(), source, CancellationToken.None).AsTask());

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidContract, exception.Code);
    }

    [Fact]
    public async Task ExtractAsync_InstructionFindingRejectsBothNullReceiptAndCandidate()
    {
        const string findingId = "instruction-finding-0123456789abcdef01234567";
        var session = Metadata(1, findingIds: [findingId]);
        var source = new RecordingSnapshotSource("snapshot-finding-null-pair", [session], _ =>
        [
            Group(HistoricalEvidenceGroupKindV1.InstructionFinding, [Reference(session)], findingId: findingId),
        ]);

        var exception = await Assert.ThrowsAsync<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceExtractorV1.ExtractAsync(Selection(), source, CancellationToken.None).AsTask());

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidContract, exception.Code);
    }

    [Fact]
    public async Task ExtractAsync_InstructionFindingRejectsInvalidReceiptAnchor()
    {
        var session = Metadata(1);
        var reference = Reference(session);
        var instructionReference = new InstructionRawEvidenceReferenceV1(
            session.SessionId.ToString(), reference.TraceId, reference.SpanId, reference.TurnIndex,
            InstructionEvidenceRelativePositionV1.Anchor);
        var handoff = InstructionFindingPipelineV1.Generate(
            72,
            new InstructionFindingEvidenceIndexV1(reference.TraceId,
            [
                new(session.SessionId.ToString(), reference.TraceId, reference.SpanId, reference.TurnIndex,
                    InstructionEvidenceRelativePositionV1.Anchor, InstructionFindingEvidenceKindV1.Turn),
            ]),
            [new(InstructionFindingCategoryV1.GoalClarity, InstructionFindingVerdictV1.Incomplete,
                InstructionFindingExtractorSourceV1.PromptOnly, [instructionReference])]);
        var receipt = Assert.Single(handoff.Findings);
        session = session with { InstructionFindingIds = [receipt.FindingId] };
        var source = new RecordingSnapshotSource("snapshot-finding-invalid-anchor", [session], _ =>
        [
            Group(HistoricalEvidenceGroupKindV1.InstructionFinding, [reference], findingId: receipt.FindingId,
                findingReceipt: receipt with { AnchorTraceId = "trace-ref-00000000000000000000000000000000" }),
        ]);

        var exception = await Assert.ThrowsAsync<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceExtractorV1.ExtractAsync(Selection(), source, CancellationToken.None).AsTask());

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidContract, exception.Code);
    }

    [Fact]
    public async Task Deserialize_RejectsUnknownContractProperty()
    {
        var session = Metadata(1);
        var source = new RecordingSnapshotSource("snapshot-strict", [session], Evidence);
        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(Selection(), source, CancellationToken.None);
        var node = JsonNode.Parse(result.RawLocalBytes)!.AsObject();
        node["unknown"] = true;

        var exception = Assert.Throws<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceJsonV1.Deserialize(Encoding.UTF8.GetBytes(node.ToJsonString())));

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidSerialization, exception.Code);
    }

    [Fact]
    public async Task Serialize_RejectsForgedDerivedGroupIdentity()
    {
        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(), new RecordingSnapshotSource("snapshot-forged-group", [Metadata(1)], Evidence), CancellationToken.None);
        var forgedGroup = result.RawLocal.EvidenceGroups[0] with
        {
            GroupId = "historical-group-00000000000000000000000000000000",
        };

        var exception = Assert.Throws<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceJsonV1.Serialize(result.RawLocal with { EvidenceGroups = [forgedGroup] }));

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidDerivedIdentity, exception.Code);
    }

    [Fact]
    public async Task Serialize_RejectsDistributionThatDoesNotMatchIncludedSessions()
    {
        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(), new RecordingSnapshotSource("snapshot-forged-distribution", [Metadata(1)], Evidence), CancellationToken.None);
        var forgedDistribution = result.RawLocal.Distribution with
        {
            Completeness = [new("partial", 1)],
        };

        var exception = Assert.Throws<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceJsonV1.Serialize(result.RawLocal with { Distribution = forgedDistribution }));

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidDerivedIdentity, exception.Code);
    }

    [Fact]
    public async Task Serialize_RejectsEvidenceGroupWithoutDeclaredCapability()
    {
        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(), new RecordingSnapshotSource("snapshot-forged-capability", [Metadata(1)], Evidence), CancellationToken.None);
        var forgedSession = result.RawLocal.Sessions[0] with
        {
            Capabilities = result.RawLocal.Sessions[0].Capabilities with { TurnRollup = false },
            Metadata = result.RawLocal.Sessions[0].Metadata with
            {
                Capabilities = result.RawLocal.Sessions[0].Metadata.Capabilities with { TurnRollup = false },
            },
        };
        var forgedDistribution = result.RawLocal.Distribution with
        {
            Capabilities = result.RawLocal.Distribution.Capabilities
                .Where(item => item.Key != "turn_rollup")
                .ToArray(),
        };

        var exception = Assert.Throws<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceJsonV1.Serialize(result.RawLocal with
            {
                Sessions = [forgedSession],
                Distribution = forgedDistribution,
            }));

        Assert.Equal(HistoricalEvidenceValidationCodeV1.MissingExactCapability, exception.Code);
    }

    [Fact]
    public async Task Serialize_RejectsIncludedHistoricalSummaryAbovePartialCompleteness()
    {
        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(), new RecordingSnapshotSource("snapshot-forged-completeness", [Metadata(1)], Evidence), CancellationToken.None);
        var forgedSession = result.RawLocal.Sessions[0] with
        {
            SourceKind = HistoricalEvidenceSourceKindV1.HistoricalSummary,
            Completeness = SessionCompleteness.Full,
            CompletenessReasons = ["historical_summary_only"],
        };

        var exception = Assert.Throws<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceJsonV1.Serialize(result.RawLocal with { Sessions = [forgedSession] }));

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidContract, exception.Code);
    }

    [Fact]
    public async Task Serialize_RejectsCompletenessReasonAboveItsDeclaredCeilingInDecisionMetadata()
    {
        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(), new RecordingSnapshotSource("snapshot-reason-ceiling", [Metadata(1)], Evidence), CancellationToken.None);
        var session = result.RawLocal.Sessions[0];
        var forged = session with
        {
            Metadata = session.Metadata with
            {
                Completeness = SessionCompleteness.Full,
                CompletenessReasons = ["historical_summary_only"],
            },
        };

        var exception = Assert.Throws<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceJsonV1.Serialize(result.RawLocal with { Sessions = [forged] }));

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidContract, exception.Code);
    }

    [Fact]
    public async Task ExtractAsync_RejectsNonUuidV7ExplicitSessionIdBeforeOpeningSnapshot()
    {
        var source = new RecordingSnapshotSource("snapshot-non-v7", [Metadata(1)], Evidence);

        var exception = await Assert.ThrowsAsync<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceExtractorV1.ExtractAsync(
                Selection(explicitIds: [Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee")]), source, CancellationToken.None).AsTask());

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidContract, exception.Code);
        Assert.Empty(source.ReadSessionIds);
    }

    [Fact]
    public async Task Deserialize_NullRequiredGraphMapsToInvalidSerialization()
    {
        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(), new RecordingSnapshotSource("snapshot-null-graph", [Metadata(1)], Evidence), CancellationToken.None);
        var node = JsonNode.Parse(result.RawLocalBytes)!.AsObject();
        node["sessions"] = null;

        var exception = Assert.Throws<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceJsonV1.Deserialize(Encoding.UTF8.GetBytes(node.ToJsonString())));

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidSerialization, exception.Code);
    }

    [Fact]
    public async Task Serialize_RejectsNoncanonicalRawExcludedSessionOrder()
    {
        var missingFirst = Guid.Parse("018f0000-0000-7000-8000-000000000098");
        var missingSecond = Guid.Parse("018f0000-0000-7000-8000-000000000099");
        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(repository: "repo-a", explicitIds: [missingSecond, missingFirst]),
            new RecordingSnapshotSource("snapshot-excluded-order", [Metadata(1, repository: "repo-a")], Evidence),
            CancellationToken.None);
        var reversed = result.RawLocal.ExcludedSessions.Reverse().ToArray();

        var exception = Assert.Throws<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceJsonV1.Serialize(result.RawLocal with { ExcludedSessions = reversed }));

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidContract, exception.Code);
    }

    [Fact]
    public async Task Serialize_RejectsTruncatedCountBelowReturnedWindowExclusions()
    {
        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(maximum: 1),
            new RecordingSnapshotSource("snapshot-truncation-count", [Metadata(1), Metadata(2)], Evidence),
            CancellationToken.None);

        var exception = Assert.Throws<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceJsonV1.Serialize(result.RawLocal with
            {
                TruncatedBefore = false,
                TruncatedSessionCount = 0,
            }));

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidContract, exception.Code);
    }

    [Fact]
    public async Task ExtractAsync_OmittedEarlierWithReturnedIneligibleSuffixNeedsNoWindowExclusion()
    {
        var sessions = Enumerable.Range(1, 3).Select(index => Metadata(index, completeness: SessionCompleteness.Unbound)).ToArray();
        var source = new RecordingSnapshotSource("snapshot-truncation-without-window", sessions, Evidence, omittedEarlierMatchingSessionCount: 4);

        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(maximum: 2), source, CancellationToken.None);

        Assert.True(result.RawLocal.TruncatedBefore);
        Assert.Equal(4, result.RawLocal.TruncatedSessionCount);
        Assert.All(result.RawLocal.ExcludedSessions, value => Assert.Equal(HistoricalSessionExclusionReasonV1.Unbound, value.Reason));
        Assert.DoesNotContain(result.RawLocal.ExcludedSessions, value => value.Reason == HistoricalSessionExclusionReasonV1.WindowTruncated);
        Assert.Empty(source.ReadSessionIds);
    }

    [Fact]
    public async Task Serialize_RejectsUnknownRepositorySafeRepresentation()
    {
        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(),
            new RecordingSnapshotSource("snapshot-invalid-enum", [Metadata(1)], Evidence),
            CancellationToken.None);

        var exception = Assert.Throws<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceJsonV1.Serialize(result.RepositorySafe with
            {
                Representation = (HistoricalEvidenceRepresentationV1)999,
            }));

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidContract, exception.Code);
    }

    [Fact]
    public async Task Serialize_RejectsNonUtcSelectionProjection()
    {
        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(),
            new RecordingSnapshotSource("snapshot-non-utc-selection", [Metadata(1)], Evidence),
            CancellationToken.None);
        var nonUtcSelection = result.RawLocal.Selection with
        {
            From = result.RawLocal.Selection.From!.Value.ToOffset(TimeSpan.FromHours(9)),
        };

        var exception = Assert.Throws<HistoricalEvidenceValidationException>(() =>
            HistoricalEvidenceJsonV1.Serialize(result.RawLocal with { Selection = nonUtcSelection }));

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidContract, exception.Code);
    }

    [Fact]
    public async Task ExtractAsync_MaximumWindowReadsExactlyTwoHundredSessionsWithinPerformanceBudget()
    {
        var sessions = Enumerable.Range(1, 201).Select(index => Metadata(index, startedAt: At(index))).ToArray();
        var source = new RecordingSnapshotSource("snapshot-10", sessions, Evidence);
        var stopwatch = Stopwatch.StartNew();

        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(Selection(maximum: 200), source, CancellationToken.None);

        stopwatch.Stop();
        Assert.Equal(200, result.RawLocal.Sessions.Count);
        Assert.Equal(200, source.ReadSessionIds.Count);
        Assert.DoesNotContain(sessions[0].SessionId, source.ReadSessionIds);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Extraction took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task ExtractAsync_LargeHistoryUsesBoundedSuffixAndExactTruncatedCount()
    {
        var returnedSuffix = Enumerable.Range(1, 201).Select(index => Metadata(index, startedAt: At(index))).ToArray();
        var source = new RecordingSnapshotSource("snapshot-large-history", returnedSuffix, Evidence, omittedEarlierMatchingSessionCount: 800);

        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(maximum: 200), source, CancellationToken.None);

        Assert.Equal(200, result.RawLocal.Sessions.Count);
        Assert.Equal(801, result.RawLocal.TruncatedSessionCount);
        Assert.True(result.RawLocal.TruncatedBefore);
        var excluded = Assert.Single(result.RawLocal.ExcludedSessions);
        Assert.Equal(returnedSuffix[0].SessionId.ToString(), excluded.SessionId);
        Assert.Equal(HistoricalSessionExclusionReasonV1.WindowTruncated, excluded.Reason);
        Assert.DoesNotContain(returnedSuffix[0].SessionId, source.ReadSessionIds);
    }

    [Fact]
    public async Task ExtractAsync_BoundedSuffixUnionsExplicitMismatchAndMissingIdsWithoutReadingTheirBodies()
    {
        var explicitMismatch = Metadata(1, repository: "repo-b", startedAt: At(1));
        var matchingSuffix = new[]
        {
            Metadata(10, repository: "repo-a", startedAt: At(10)),
            Metadata(11, repository: "repo-a", startedAt: At(11)),
            Metadata(12, repository: "repo-a", startedAt: At(12)),
        };
        var missing = Guid.Parse("018f0000-0000-7000-8000-000000000099");
        var source = new RecordingSnapshotSource(
            "snapshot-explicit-union",
            [matchingSuffix[1], explicitMismatch, matchingSuffix[2], matchingSuffix[0]],
            Evidence,
            omittedEarlierMatchingSessionCount: 97);

        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(
            Selection(maximum: 2, repository: "repo-a", explicitIds: [explicitMismatch.SessionId, missing]),
            source,
            CancellationToken.None);

        Assert.Equal([matchingSuffix[1].SessionId.ToString(), matchingSuffix[2].SessionId.ToString()], result.RawLocal.Sessions.Select(item => item.SessionId));
        Assert.Equal(98, result.RawLocal.TruncatedSessionCount);
        Assert.Collection(
            result.RawLocal.ExcludedSessions,
            item => { Assert.Equal(explicitMismatch.SessionId.ToString(), item.SessionId); Assert.Equal(HistoricalSessionExclusionReasonV1.FilterMismatch, item.Reason); },
            item => { Assert.Equal(matchingSuffix[0].SessionId.ToString(), item.SessionId); Assert.Equal(HistoricalSessionExclusionReasonV1.WindowTruncated, item.Reason); },
            item => { Assert.Equal(missing.ToString(), item.SessionId); Assert.Equal(HistoricalSessionExclusionReasonV1.MissingSessionReference, item.Reason); });
        Assert.Equal([matchingSuffix[1].SessionId, matchingSuffix[2].SessionId], source.ReadSessionIds);
        Assert.Equal(
            result.RawLocal.ExcludedSessions.Select(item => SafeSession(Guid.Parse(item.SessionId))),
            result.RepositorySafe.ExcludedSessions.Select(item => item.SessionId));
    }

    [Fact]
    public async Task ExtractAsync_CanonicalizesRepositorySafeReferencesAfterTokenization()
    {
        var first = Metadata(1, startedAt: At(1));
        var second = Metadata(2, startedAt: At(1));
        var third = Metadata(3, startedAt: At(3));
        var referenceA = new HistoricalRawEvidenceReferenceV1(first.SessionId, "trace-a", "span-a", 1, HistoricalEvidenceRelativePositionV1.Anchor);
        var referenceB = new HistoricalRawEvidenceReferenceV1(first.SessionId, "trace-b", "span-b", 2, HistoricalEvidenceRelativePositionV1.Following);
        first = first with
        {
            EvidenceLocations =
            [
                new(first.SessionId, referenceB.TraceId, referenceB.SpanId, referenceB.TurnIndex, referenceB.RelativePosition),
                new(first.SessionId, referenceA.TraceId, referenceA.SpanId, referenceA.TurnIndex, referenceA.RelativePosition),
            ],
        };
        var source = new RecordingSnapshotSource("snapshot-order", [third, second, first], session =>
            session.SessionId == first.SessionId
                ? [Group(HistoricalEvidenceGroupKindV1.RetryChain, references: [referenceB, referenceA])]
                : Evidence(session));

        var result = await HistoricalEvidenceExtractorV1.ExtractAsync(Selection(), source, CancellationToken.None);

        Assert.Equal([first.SessionId.ToString(), second.SessionId.ToString(), third.SessionId.ToString()], result.RawLocal.Sessions.Select(item => item.SessionId));
        Assert.Equal(
            result.RawLocal.Sessions.Select(item => SafeSession(Guid.Parse(item.SessionId))),
            result.RepositorySafe.Sessions.Select(item => item.SessionId));
        var rawReferences = result.RawLocal.EvidenceGroups.Single(item => item.Kind == HistoricalEvidenceGroupKindV1.RetryChain).References;
        var safeReferences = result.RepositorySafe.EvidenceGroups.Single(item => item.Kind == HistoricalEvidenceGroupKindV1.RetryChain).References;
        Assert.Equal(["trace-a", "trace-b"], rawReferences.Select(item => item.TraceId));
        Assert.Equal(
            rawReferences.Select(item => InstructionFindingReferenceTokenizationV1.TokenizeTrace(item.TraceId)).Order(StringComparer.Ordinal),
            safeReferences.Select(item => item.TraceId));
    }

    private static HistoricalEvidenceSelectionV1 Selection(
        int maximum = 50,
        bool sanitizedOnly = false,
        string? repository = null,
        IReadOnlyList<Guid>? explicitIds = null,
        IReadOnlyList<SessionSourceSurface>? surfaces = null,
        string? task = null) =>
        new(repository, null, At(0), At(1000), explicitIds ?? [], surfaces ?? [], task, null, maximum, sanitizedOnly);

    private static HistoricalSessionMetadataV1 Metadata(
        int number,
        DateTimeOffset? startedAt = null,
        string? repository = "repo-a",
        string? task = null,
        SessionSourceSurface surface = SessionSourceSurface.CopilotSdk,
        SessionCompleteness completeness = SessionCompleteness.Full,
        IReadOnlyList<string>? reasons = null,
        HistoricalEvidenceSourceKindV1 sourceKind = HistoricalEvidenceSourceKindV1.LiveOtel,
        HistoricalSessionCapabilitiesV1? capabilities = null,
        IReadOnlyList<string>? findingIds = null)
    {
        var id = Guid.Parse($"018f0000-0000-7000-8000-{number:D12}");
        return new(
            id, surface, "1.0.0", "adapter.v1", completeness, reasons ?? [], sourceKind,
            SessionContentState.Available, repository, "workspace-a", task, "experiment-a",
            startedAt ?? At(number), At(number), capabilities ?? Capabilities(all: true),
            [new(id, $"trace-{number}", $"span-{number}", 1, HistoricalEvidenceRelativePositionV1.Anchor)],
            findingIds ?? []);
    }

    private static HistoricalSessionCapabilitiesV1 Capabilities(bool all) =>
        new(all, all, all, all, all, all, all, all, all, all, all, all);

    private static IReadOnlyList<HistoricalEvidenceGroupDraftV1> Evidence(HistoricalSessionMetadataV1 session) =>
    [
        Group(HistoricalEvidenceGroupKindV1.TurnRollup,
            references: [new(session.SessionId, $"trace-{SessionNumber(session.SessionId)}", $"span-{SessionNumber(session.SessionId)}", 1, HistoricalEvidenceRelativePositionV1.Anchor)],
            numericValue: 1,
            unit: "turn")
    ];

    private static HistoricalEvidenceGroupDraftV1 Group(
        HistoricalEvidenceGroupKindV1 kind,
        IReadOnlyList<HistoricalRawEvidenceReferenceV1>? references = null,
        long? numericValue = null,
        string? unit = null,
        string? status = null,
        string? exactCallId = null,
        string? canonicalCallHash = null,
        string? exactOwnershipId = null,
        string? findingId = null,
        string? rawDescriptor = null,
        InstructionFindingReceiptV1? findingReceipt = null,
        InstructionRuleCandidateV1? findingCandidate = null) =>
        new(kind, references ?? [], numericValue, unit, status, exactCallId, canonicalCallHash, exactOwnershipId,
            findingId, rawDescriptor, findingReceipt, findingCandidate);

    private static HistoricalRawEvidenceReferenceV1 Reference(HistoricalSessionMetadataV1 session) =>
        new(session.SessionId, $"trace-{SessionNumber(session.SessionId)}", $"span-{SessionNumber(session.SessionId)}", 1, HistoricalEvidenceRelativePositionV1.Anchor);

    private static int SessionNumber(Guid id) => int.Parse(id.ToString()[^12..]);
    private static DateTimeOffset At(int minute) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minute);
    private static string SafeSession(Guid id) => InstructionFindingReferenceTokenizationV1.Tokenize(new(
        id.ToString(), "trace", null, 1, InstructionEvidenceRelativePositionV1.Anchor)).SessionId!;

    private static bool ValidateWithPowerShellJsonSchema(string instancePath, string schemaPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("if (Test-Json -LiteralPath $env:CAO_SCHEMA_INSTANCE -SchemaFile $env:CAO_SCHEMA_FILE -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }");
        startInfo.Environment["CAO_SCHEMA_INSTANCE"] = instancePath;
        startInfo.Environment["CAO_SCHEMA_FILE"] = schemaPath;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start pwsh for JSON Schema validation.");
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("JSON Schema validation timed out.");
        }

        return process.ExitCode == 0;
    }

    private sealed class RecordingSnapshotSource(
        string snapshotId,
        IReadOnlyList<HistoricalSessionMetadataV1> sessions,
        Func<HistoricalSessionMetadataV1, IReadOnlyList<HistoricalEvidenceGroupDraftV1>> evidenceFactory,
        long omittedEarlierMatchingSessionCount = 0)
        : IHistoricalEvidenceSnapshotSourceV1
    {
        public List<Guid> ReadSessionIds { get; } = [];
        public List<bool> IncludeDescriptors { get; } = [];

        public ValueTask<IHistoricalEvidenceSnapshotLeaseV1> OpenSnapshotAsync(
            HistoricalEvidenceSelectionV1 selection,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IHistoricalEvidenceSnapshotLeaseV1>(new Lease(this, snapshotId, sessions, evidenceFactory, omittedEarlierMatchingSessionCount));

        private sealed class Lease(
            RecordingSnapshotSource owner,
            string snapshotId,
            IReadOnlyList<HistoricalSessionMetadataV1> sessions,
            Func<HistoricalSessionMetadataV1, IReadOnlyList<HistoricalEvidenceGroupDraftV1>> evidenceFactory,
            long omittedEarlierMatchingSessionCount)
            : IHistoricalEvidenceSnapshotLeaseV1
        {
            public string SnapshotId => snapshotId;
            public IReadOnlyList<HistoricalSessionMetadataV1> Sessions => sessions;
            public long OmittedEarlierMatchingSessionCount => omittedEarlierMatchingSessionCount;

            public ValueTask<IReadOnlyList<HistoricalEvidenceGroupDraftV1>> ReadEvidenceAsync(
                Guid sessionId,
                bool includeDescriptors,
                CancellationToken cancellationToken)
            {
                owner.ReadSessionIds.Add(sessionId);
                owner.IncludeDescriptors.Add(includeDescriptors);
                return ValueTask.FromResult(evidenceFactory(sessions.Single(item => item.SessionId == sessionId)));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
