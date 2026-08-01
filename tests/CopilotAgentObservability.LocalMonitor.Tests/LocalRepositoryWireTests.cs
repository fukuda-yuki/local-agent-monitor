using System.Text;
using System.Text.Json.Nodes;
using CopilotAgentObservability.LocalMonitor.Repositories;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositoryWireTests
{
    private const string RepositoryId = "01900000-0000-7000-8000-000000000001";

    public static IEnumerable<object[]> StrictParserCases()
    {
        yield return ["create", "{}", false];
        yield return ["create", "{\"Schema_version\":\"local-repository-create.v1\",\"display_name\":\"One\",\"github_locator\":null}", false];
        yield return ["create", "[]", false];
        yield return ["create", "null", false];
        yield return ["create", "true", false];
        yield return ["create", "1", false];
        yield return ["create", "\"value\"", false];
        yield return ["create", "{\"schema_version\":1,\"display_name\":\"One\",\"github_locator\":null}", false];
        yield return ["create", "{\"schema_version\":\"local-repository-create.v1\",\"display_name\":{},\"github_locator\":null}", false];
        yield return ["create", "{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"One\",\"github_locator\":false}", false];
        yield return ["create", "{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"One\",\"github_locator\":null,}", false];
        yield return ["create", "{\"schema_version\":\"local-repository-create.v2\",\"display_name\":\"One\",\"github_locator\":null}", false];
        yield return ["create", "{\"schema_version\":\"local-repository-create.v1\",\"display_name\":[[[[[[[[[\"One\"]]]]]]]]],\"github_locator\":null}", false];

        yield return ["update", "{\"schema_version\":\"local-repository-update.v1\",\"operation\":\"rename\",\"display_name\":\"Two\",\"github_locator\":null}", false];
        yield return ["update", "{\"schema_version\":\"local-repository-update.v1\",\"Expected_revision\":1,\"operation\":\"rename\",\"display_name\":\"Two\",\"github_locator\":null}", false];
        yield return ["update", "[]", false];
        yield return ["update", "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":\"1\",\"operation\":\"rename\",\"display_name\":\"Two\",\"github_locator\":null}", false];
        yield return ["update", "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":null,\"operation\":\"rename\",\"display_name\":\"Two\",\"github_locator\":null}", false];
        yield return ["update", "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":9223372036854775807,\"operation\":\"rename\",\"display_name\":\"Two\",\"github_locator\":null}", true];
        yield return ["update", "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":9223372036854775808,\"operation\":\"rename\",\"display_name\":\"Two\",\"github_locator\":null}", false];
        yield return ["update", "{\"schema_version\":\"local-repository-update.v2\",\"expected_revision\":1,\"operation\":\"rename\",\"display_name\":\"Two\",\"github_locator\":null}", false];
        yield return ["update", "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":null,\"display_name\":\"Two\",\"github_locator\":null}", false];
        yield return ["update", "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":{},\"display_name\":\"Two\",\"github_locator\":null}", false];
        yield return ["update", "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":\"rename\",\"display_name\":null,\"github_locator\":null}", false];
        yield return ["update", "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":\"set_github_locator\",\"display_name\":\"Two\",\"github_locator\":\"https://github.com/o/r\"}", false];
        yield return ["update", "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":\"other\",\"display_name\":null,\"github_locator\":null}", false];
        yield return ["update", "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":\"rename\",\"display_name\":\"Two\",\"github_locator\":null,}", false];

        yield return ["action", "{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"01900000-0000-7000-8000-000000000002\",\"action\":\"resume_automatic\",\"repository_id\":null}", false];
        yield return ["action", "{\"schema_version\":\"local-session-repository-action.v1\",\"Session_id\":\"01900000-0000-7000-8000-000000000002\",\"expected_revision\":0,\"action\":\"resume_automatic\",\"repository_id\":null}", false];
        yield return ["action", "\"value\"", false];
        yield return ["action", "{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"01900000-0000-7000-8000-000000000002\",\"expected_revision\":\"0\",\"action\":\"resume_automatic\",\"repository_id\":null}", false];
        yield return ["action", "{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"01900000-0000-7000-8000-000000000002\",\"expected_revision\":9223372036854775807,\"action\":\"resume_automatic\",\"repository_id\":null}", true];
        yield return ["action", "{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"01900000-0000-7000-8000-000000000002\",\"expected_revision\":9223372036854775808,\"action\":\"resume_automatic\",\"repository_id\":null}", false];
        yield return ["action", "{\"schema_version\":\"local-session-repository-action.v2\",\"session_id\":\"01900000-0000-7000-8000-000000000002\",\"expected_revision\":0,\"action\":\"resume_automatic\",\"repository_id\":null}", false];
        yield return ["action", "{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":null,\"expected_revision\":0,\"action\":\"resume_automatic\",\"repository_id\":null}", false];
        yield return ["action", "{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"01900000-0000-7000-8000-000000000002\",\"expected_revision\":0,\"action\":false,\"repository_id\":null}", false];
        yield return ["action", "{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"01900000-0000-7000-8000-000000000002\",\"expected_revision\":0,\"action\":\"assign\",\"repository_id\":7}", false];
        yield return ["action", "{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"01900000-0000-7000-8000-000000000002\",\"expected_revision\":0,\"action\":\"explicitly_unassign\",\"repository_id\":\"01900000-0000-7000-8000-000000000001\"}", false];
        yield return ["action", "{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"01900000-0000-7000-8000-000000000002\",\"expected_revision\":0,\"action\":\"unknown\",\"repository_id\":null}", false];
        yield return ["action", "{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"01900000-0000-7000-8000-000000000002\",\"expected_revision\":0,\"action\":\"resume_automatic\",\"repository_id\":null,}", false];
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    public void RepositoryWriter_ProducesPinnedCompactBytesAndDecodesExactFacts(int statusCode)
    {
        var snapshot = new LocalRepositoryMutationRepository(
            RepositoryId,
            "日本<>&\u2028\U0001f600",
            1,
            DateTimeOffset.ParseExact("2026-08-01T01:02:03.1234567+00:00", "O", null),
            DateTimeOffset.ParseExact("2026-08-01T01:02:03.1234567+00:00", "O", null));

        var bytes = LocalRepositoryJson.WriteRepository(statusCode, snapshot);

        Assert.Equal(
            "{\"schema_version\":\"local-repository.v1\",\"repository_id\":\"01900000-0000-7000-8000-000000000001\",\"display_name\":\"\\u65E5\\u672C\\u003C\\u003E\\u0026\\u2028\\uD83D\\uDE00\",\"revision\":1,\"created_at\":\"2026-08-01T01:02:03.1234567\\u002B00:00\",\"updated_at\":\"2026-08-01T01:02:03.1234567\\u002B00:00\"}",
            Encoding.UTF8.GetString(bytes.Span));
        var decoded = LocalRepositoryExactResponse.ValidateMutationEntity(statusCode, bytes.Span);
        Assert.Equal(LocalRepositoryMutationEntityKind.Repository, decoded.Kind);
        Assert.Equal(RepositoryId, decoded.TargetId);
        Assert.Equal(1, decoded.Revision);
        Assert.Null(decoded.State);
    }

    [Theory]
    [InlineData("{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"One\",\"github_locator\":null}", true)]
    [InlineData("{\"display_name\":\"One\",\"schema_version\":\"local-repository-create.v1\",\"github_locator\":null}", false)]
    [InlineData("{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"One\",\"display_name\":\"Two\",\"github_locator\":null}", false)]
    [InlineData("{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"\\uD800\",\"github_locator\":null}", false)]
    public void CreateParser_RequiresExactStructureAndUnicode(string json, bool accepted)
    {
        var result = LocalRepositoryJson.TryParseCreate(Encoding.UTF8.GetBytes(json), out var request);

        Assert.Equal(accepted, result);
        Assert.Equal(accepted, request is not null);
    }

    [Theory]
    [InlineData("{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":\"rename\",\"display_name\":\"Two\",\"github_locator\":null}", true)]
    [InlineData("{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":\"set_github_locator\",\"display_name\":null,\"github_locator\":\"https://github.com/Owner/Repo\"}", true)]
    [InlineData("{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":0,\"operation\":\"rename\",\"display_name\":\"Two\",\"github_locator\":null}", false)]
    [InlineData("{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1.0,\"operation\":\"rename\",\"display_name\":\"Two\",\"github_locator\":null}", false)]
    [InlineData("{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":\"rename\",\"display_name\":\"Two\",\"github_locator\":\"https://github.com/o/r\"}", false)]
    [InlineData("{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":\"set_github_locator\",\"display_name\":null,\"github_locator\":null}", false)]
    [InlineData("{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":\"rename\",\"display_name\":\"Two\",\"github_locator\":null,\"future\":null}", false)]
    public void UpdateParser_RequiresExactClosedActionShape(string json, bool accepted) =>
        Assert.Equal(accepted, LocalRepositoryJson.TryParseUpdate(Encoding.UTF8.GetBytes(json), out _));

    [Theory]
    [InlineData("{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"01900000-0000-7000-8000-000000000002\",\"expected_revision\":0,\"action\":\"assign\",\"repository_id\":\"01900000-0000-7000-8000-000000000001\"}", true)]
    [InlineData("{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"01900000-0000-7000-8000-000000000002\",\"expected_revision\":1,\"action\":\"explicitly_unassign\",\"repository_id\":null}", true)]
    [InlineData("{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"01900000-0000-7000-8000-000000000002\",\"expected_revision\":1,\"action\":\"resume_automatic\",\"repository_id\":null}", true)]
    [InlineData("{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"01900000-0000-7000-8000-000000000002\",\"expected_revision\":-1,\"action\":\"assign\",\"repository_id\":\"01900000-0000-7000-8000-000000000001\"}", false)]
    [InlineData("{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"01900000-0000-7000-8000-000000000002\",\"expected_revision\":1e0,\"action\":\"resume_automatic\",\"repository_id\":null}", false)]
    [InlineData("{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"01900000-0000-7000-8000-000000000002\",\"expected_revision\":1,\"action\":\"assign\",\"repository_id\":null}", false)]
    public void SessionActionParser_RequiresExactClosedActionShape(string json, bool accepted) =>
        Assert.Equal(accepted, LocalRepositoryJson.TryParseSessionAction(Encoding.UTF8.GetBytes(json), out _));

    [Fact]
    public void Parsers_RejectInvalidUtf8CommentsAndTrailingContent()
    {
        Assert.False(LocalRepositoryJson.TryParseCreate(new byte[] { 0xff }, out _));
        Assert.False(LocalRepositoryJson.TryParseCreate(Encoding.UTF8.GetBytes("{/*x*/\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"One\",\"github_locator\":null}"), out _));
        Assert.False(LocalRepositoryJson.TryParseCreate(Encoding.UTF8.GetBytes("{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"One\",\"github_locator\":null}{}"), out _));
    }

    [Theory]
    [MemberData(nameof(StrictParserCases))]
    public void Parsers_EnforceClosedKindsCasingDepthIntegerAndNullabilityMatrix(
        string requestKind,
        string json,
        bool accepted) =>
        Assert.Equal(accepted, Parse(requestKind, json));

    [Fact]
    public void Parsers_RejectEveryMissingAndWrongCaseProperty()
    {
        foreach (var (requestKind, properties) in new[]
        {
            ("create", new[] { "schema_version", "display_name", "github_locator" }),
            ("update", new[] { "schema_version", "expected_revision", "operation", "display_name", "github_locator" }),
            ("action", new[] { "schema_version", "session_id", "expected_revision", "action", "repository_id" }),
        })
        {
            var canonical = RequestJson(requestKind);
            foreach (var property in properties)
            {
                var missing = JsonNode.Parse(canonical)!.AsObject();
                Assert.True(missing.Remove(property));
                Assert.False(Parse(requestKind, missing.ToJsonString()));

                var wrongCase = char.ToUpperInvariant(property[0]) + property[1..];
                Assert.False(Parse(
                    requestKind,
                    canonical.Replace($"\"{property}\"", $"\"{wrongCase}\"", StringComparison.Ordinal)));
            }
        }
    }

    [Fact]
    public void LocatorAndAssignmentWriters_ProducePinnedPropertyOrderAndEmptyObservedLabels()
    {
        var timestamp = DateTimeOffset.ParseExact("2026-08-01T01:02:03.1234567+00:00", "O", null);
        var locators = new LocalRepositoryLocatorSnapshot(
            RepositoryId,
            2,
            [new(
                "01900000-0000-7000-8000-000000000003",
                "github_repository",
                "github.com/owner/repo",
                "Owner",
                "Repo",
                "observed",
                true,
                timestamp,
                new("github-copilot-cli", "1.2.3", new('a', 32), new('b', 16), timestamp, "available"))]);
        var assignment = new LocalRepositoryAssignmentSnapshot(
            "01900000-0000-7000-8000-000000000002",
            1,
            "assigned",
            "automatic",
            RepositoryId,
            [],
            timestamp);

        Assert.Equal(
            "{\"schema_version\":\"local-repository-locators.v1\",\"repository_id\":\"01900000-0000-7000-8000-000000000001\",\"repository_revision\":2,\"locators\":[{\"locator_id\":\"01900000-0000-7000-8000-000000000003\",\"kind\":\"github_repository\",\"canonical_locator\":\"github.com/owner/repo\",\"display_owner\":\"Owner\",\"display_repository\":\"Repo\",\"source\":\"observed\",\"is_current\":true,\"created_at\":\"2026-08-01T01:02:03.1234567\\u002B00:00\",\"provenance\":{\"source_surface\":\"github-copilot-cli\",\"source_application_version\":\"1.2.3\",\"trace_id\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"span_id\":\"bbbbbbbbbbbbbbbb\",\"observed_at\":\"2026-08-01T01:02:03.1234567\\u002B00:00\",\"source_content_availability\":\"available\"}}]}",
            Encoding.UTF8.GetString(LocalRepositoryJson.WriteLocators(locators).Span));
        Assert.Equal(
            "{\"schema_version\":\"local-session-repository-assignment.v1\",\"session_id\":\"01900000-0000-7000-8000-000000000002\",\"assignment_revision\":1,\"state\":\"assigned\",\"authority\":\"automatic\",\"repository_id\":\"01900000-0000-7000-8000-000000000001\",\"conflicting_repository_ids\":[],\"observed_label_candidates\":[],\"updated_at\":\"2026-08-01T01:02:03.1234567\\u002B00:00\"}",
            Encoding.UTF8.GetString(LocalRepositoryJson.WriteAssignment(assignment).Span));
        var readBytes = LocalRepositoryJson.WriteAssignment(assignment);
        var mutationBytes = LocalRepositoryJson.WriteAssignment(new LocalRepositoryMutationAssignment(
            assignment.SessionId,
            assignment.AssignmentRevision,
            assignment.State,
            assignment.Authority,
            assignment.RepositoryId,
            assignment.ConflictingRepositoryIds,
            assignment.UpdatedAt));
        Assert.Equal(readBytes.ToArray(), mutationBytes.ToArray());
        var decoded = LocalRepositoryExactResponse.ValidateMutationEntity(200, readBytes.Span);
        Assert.Equal(LocalRepositoryMutationEntityKind.Assignment, decoded.Kind);
        Assert.Equal(assignment.SessionId, decoded.TargetId);
        Assert.Equal(assignment.AssignmentRevision, decoded.Revision);
        Assert.Equal(assignment.State, decoded.State);
    }

    [Fact]
    public void FixedErrorWriter_IsClosedAndExact()
    {
        Assert.Equal(
            "{\"error\":\"locator_limit_reached\"}",
            Encoding.UTF8.GetString(LocalRepositoryJson.ErrorBytes(LocalRepositoryError.LocatorLimitReached).Span));
    }

    [Fact]
    public void LocatorWriter_RejectsInvalidCurrentUniquenessOrderingAndExactTuple()
    {
        var timestamp = DateTimeOffset.ParseExact("2026-08-01T01:02:03.1234567+00:00", "O", null);
        var current = Locator("01900000-0000-7000-8000-000000000004", "github.com/owner/current", "Current", true, timestamp);
        var historical = Locator("01900000-0000-7000-8000-000000000003", "github.com/owner/historical", "Historical", false, timestamp);

        Assert.Throws<InvalidOperationException>(() => LocalRepositoryJson.WriteLocators(new(RepositoryId, 1, [historical, current])));
        Assert.Throws<InvalidOperationException>(() => LocalRepositoryJson.WriteLocators(new(RepositoryId, 1, [current, current])));
        Assert.Throws<InvalidOperationException>(() => LocalRepositoryJson.WriteLocators(new(RepositoryId, 1, [
            current,
            historical with { LocatorId = current.LocatorId }])));
        Assert.Throws<InvalidOperationException>(() => LocalRepositoryJson.WriteLocators(new(RepositoryId, 1, [
            current,
            historical with { CanonicalLocator = current.CanonicalLocator }])));
        Assert.Throws<InvalidOperationException>(() => LocalRepositoryJson.WriteLocators(new(RepositoryId, 1, [
            current with { CanonicalLocator = "github.com/owner/not-current" }])));
    }

    [Fact]
    public void LocatorWriter_RejectsInvalidProvenanceAndOverLimit()
    {
        var timestamp = DateTimeOffset.ParseExact("2026-08-01T01:02:03.1234567+00:00", "O", null);
        var observed = Locator("01900000-0000-7000-8000-000000000003", "github.com/owner/repo", "Repo", true, timestamp) with
        {
            Source = "observed",
            Provenance = new("github-copilot-cli", string.Empty, new('a', 32), new('b', 16), timestamp, "available"),
        };

        Assert.Throws<InvalidOperationException>(() => LocalRepositoryJson.WriteLocators(new(RepositoryId, 1, [observed])));
        Assert.Throws<InvalidOperationException>(() => LocalRepositoryJson.WriteLocators(new(RepositoryId, 1, [observed with
        {
            Provenance = observed.Provenance! with { SourceApplicationVersion = "1/2" },
        }])));
        Assert.Throws<InvalidOperationException>(() => LocalRepositoryJson.WriteLocators(new(RepositoryId, 1, [observed with
        {
            Provenance = observed.Provenance! with { ObservedAt = timestamp.ToOffset(TimeSpan.FromHours(1)) },
        }])));

        var locators = Enumerable.Range(0, 129)
            .Select(index => Locator(
                $"01900000-0000-7000-8000-{index + 10:000000000000}",
                $"github.com/owner/repo{index}",
                $"Repo{index}",
                index == 0,
                timestamp.AddTicks(index)))
            .ToArray();
        Assert.Throws<InvalidOperationException>(() => LocalRepositoryJson.WriteLocators(new(RepositoryId, 1, locators)));
    }

    [Fact]
    public void AssignmentWriter_RejectsInvalidStateConflictAndRevisionShapes()
    {
        var timestamp = DateTimeOffset.ParseExact("2026-08-01T01:02:03.1234567+00:00", "O", null);
        var sessionId = "01900000-0000-7000-8000-000000000002";
        var conflictA = "01900000-0000-7000-8000-000000000003";
        var conflictB = "01900000-0000-7000-8000-000000000004";

        Assert.Throws<InvalidOperationException>(() => LocalRepositoryJson.WriteAssignment(new LocalRepositoryAssignmentSnapshot(sessionId, 1, "assigned", "manual", RepositoryId, [conflictA], timestamp)));
        Assert.Throws<InvalidOperationException>(() => LocalRepositoryJson.WriteAssignment(new LocalRepositoryAssignmentSnapshot(sessionId, 1, "conflict", "automatic", null, [conflictB, conflictA], timestamp)));
        Assert.Throws<InvalidOperationException>(() => LocalRepositoryJson.WriteAssignment(new LocalRepositoryAssignmentSnapshot(sessionId, 0, "assigned", "automatic", RepositoryId, [], null)));
        Assert.Throws<InvalidOperationException>(() => LocalRepositoryJson.WriteAssignment(new LocalRepositoryAssignmentSnapshot(sessionId, 0, "unassigned", "none", null, [], timestamp)));
        Assert.Throws<InvalidOperationException>(() => LocalRepositoryJson.WriteAssignment(new LocalRepositoryAssignmentSnapshot(sessionId, 1, "unassigned", "none", null, [], null)));
        Assert.Throws<InvalidOperationException>(() => LocalRepositoryJson.WriteAssignment(new LocalRepositoryAssignmentSnapshot(sessionId, 1, "conflict", "automatic", null, [conflictA, conflictA], timestamp)));
        Assert.Throws<InvalidOperationException>(() => LocalRepositoryJson.WriteAssignment(new LocalRepositoryAssignmentSnapshot(
            sessionId,
            1,
            "conflict",
            "automatic",
            null,
            Enumerable.Range(10, 129).Select(index => $"01900000-0000-7000-8000-{index:000000000000}").ToArray(),
            timestamp)));
    }

    [Fact]
    public void Parsers_RejectLoneSurrogatesInEveryStringValue()
    {
        foreach (var (requestKind, properties) in new[]
        {
            ("create", new[] { "schema_version", "display_name", "github_locator" }),
            ("update", new[] { "schema_version", "operation", "display_name", "github_locator" }),
            ("action", new[] { "schema_version", "session_id", "action", "repository_id" }),
        })
        {
            foreach (var property in properties)
            {
                foreach (var surrogate in new[] { "\\uD800", "\\uDC00" })
                {
                    var json = ReplaceStringValue(RequestJson(requestKind), property, surrogate);
                    Assert.False(Parse(requestKind, json));
                }
            }
        }
    }

    [Fact]
    public void Parsers_RejectLoneSurrogatesInEveryPropertyName()
    {
        foreach (var (requestKind, properties) in new[]
        {
            ("create", new[] { "schema_version", "display_name", "github_locator" }),
            ("update", new[] { "schema_version", "expected_revision", "operation", "display_name", "github_locator" }),
            ("action", new[] { "schema_version", "session_id", "expected_revision", "action", "repository_id" }),
        })
        {
            foreach (var property in properties)
            {
                foreach (var surrogate in new[] { "\\uD800", "\\uDC00" })
                {
                    var json = RequestJson(requestKind).Replace($"\"{property}\"", $"\"{surrogate}\"", StringComparison.Ordinal);
                    Assert.False(Parse(requestKind, json));
                }
            }
        }
    }

    [Fact]
    public void CreateParser_AcceptsValidSupplementaryScalarWithoutRepair()
    {
        const string json = "{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"\\uD83D\\uDE00\",\"github_locator\":null}";

        Assert.True(LocalRepositoryJson.TryParseCreate(Encoding.UTF8.GetBytes(json), out var request));
        Assert.Equal("\U0001f600", request!.DisplayName);
    }

    [Fact]
    public void LocatorWriter_AllowsEmptyArrayAndWritesManualProvenanceAsNull()
    {
        var timestamp = DateTimeOffset.ParseExact("2026-08-01T01:02:03.1234567+00:00", "O", null);
        var empty = Encoding.UTF8.GetString(LocalRepositoryJson.WriteLocators(new(RepositoryId, 1, [])).Span);
        var manual = Encoding.UTF8.GetString(LocalRepositoryJson.WriteLocators(new(RepositoryId, 1, [
            Locator("01900000-0000-7000-8000-000000000003", "github.com/owner/repo", "Repo", true, timestamp),
        ])).Span);

        Assert.Equal("{\"schema_version\":\"local-repository-locators.v1\",\"repository_id\":\"01900000-0000-7000-8000-000000000001\",\"repository_revision\":1,\"locators\":[]}", empty);
        Assert.Contains("\"provenance\":null", manual, StringComparison.Ordinal);
    }

    [Fact]
    public void LocatorWriter_AcceptsAlreadyParsedLogicalRepositoryEndingInLowercaseGit()
    {
        var timestamp = DateTimeOffset.ParseExact("2026-08-01T01:02:03.1234567+00:00", "O", null);
        var bytes = LocalRepositoryJson.WriteLocators(new(RepositoryId, 1, [
            Locator(
                "01900000-0000-7000-8000-000000000003",
                "github.com/owner/repository.git",
                "Repository.git",
                true,
                timestamp),
        ]));

        Assert.Contains(
            "\"canonical_locator\":\"github.com/owner/repository.git\",\"display_owner\":\"Owner\",\"display_repository\":\"Repository.git\"",
            Encoding.UTF8.GetString(bytes.Span),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("assigned", "automatic", RepositoryId, 1)]
    [InlineData("assigned", "manual", RepositoryId, 1)]
    [InlineData("unassigned", "none", null, 1)]
    [InlineData("explicitly_unassigned", "manual", null, 1)]
    public void AssignmentWriter_AcceptsEveryNonConflictState(string state, string authority, string? repositoryId, long revision)
    {
        var timestamp = DateTimeOffset.ParseExact("2026-08-01T01:02:03.1234567+00:00", "O", null);

        var bytes = LocalRepositoryJson.WriteAssignment(new LocalRepositoryAssignmentSnapshot(
            "01900000-0000-7000-8000-000000000002", revision, state, authority, repositoryId, [], timestamp));

        Assert.Contains($"\"state\":\"{state}\"", Encoding.UTF8.GetString(bytes.Span), StringComparison.Ordinal);
    }

    [Fact]
    public void AssignmentWriter_AcceptsCanonicalConflictAndRevisionZeroAbsence()
    {
        var timestamp = DateTimeOffset.ParseExact("2026-08-01T01:02:03.1234567+00:00", "O", null);
        var conflict = LocalRepositoryJson.WriteAssignment(new LocalRepositoryAssignmentSnapshot(
            "01900000-0000-7000-8000-000000000002", 1, "conflict", "automatic", null,
            ["01900000-0000-7000-8000-000000000003", "01900000-0000-7000-8000-000000000004"], timestamp));
        var zero = LocalRepositoryJson.WriteAssignment(new LocalRepositoryAssignmentSnapshot(
            "01900000-0000-7000-8000-000000000002", 0, "unassigned", "none", null, [], null));

        Assert.Contains("\"conflicting_repository_ids\":[\"01900000-0000-7000-8000-000000000003\",\"01900000-0000-7000-8000-000000000004\"]", Encoding.UTF8.GetString(conflict.Span), StringComparison.Ordinal);
        Assert.EndsWith("\"updated_at\":null}", Encoding.UTF8.GetString(zero.Span), StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryAndLocatorWriters_RejectNonUtcTimestamps()
    {
        var offset = DateTimeOffset.ParseExact("2026-08-01T02:02:03.1234567+01:00", "O", null);
        Assert.Throws<InvalidOperationException>(() => LocalRepositoryJson.WriteRepository(201, new(RepositoryId, "Repo", 1, offset, offset)));
        Assert.Throws<InvalidOperationException>(() => LocalRepositoryJson.WriteLocators(new(RepositoryId, 1, [
            Locator("01900000-0000-7000-8000-000000000003", "github.com/owner/repo", "Repo", true, offset),
        ])));
    }

    [Fact]
    public void FixedErrorWriter_ContainsEveryClosedErrorCode()
    {
        foreach (var error in Enum.GetValues<LocalRepositoryError>())
        {
            Assert.Equal($"{{\"error\":\"{ToErrorCode(error)}\"}}", Encoding.UTF8.GetString(LocalRepositoryJson.ErrorBytes(error).Span));
        }
    }

    private static LocalRepositoryLocatorItem Locator(
        string locatorId,
        string canonicalLocator,
        string displayRepository,
        bool isCurrent,
        DateTimeOffset createdAt) => new(
        locatorId,
        "github_repository",
        canonicalLocator,
        "Owner",
        displayRepository,
        "manual",
        isCurrent,
        createdAt,
        null);

    private static string RequestJson(string requestKind) => requestKind switch
    {
        "create" => "{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"One\",\"github_locator\":\"https://github.com/Owner/Repo\"}",
        "update" => "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":\"rename\",\"display_name\":\"One\",\"github_locator\":null}",
        _ => "{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"01900000-0000-7000-8000-000000000002\",\"expected_revision\":0,\"action\":\"assign\",\"repository_id\":\"01900000-0000-7000-8000-000000000001\"}",
    };

    private static bool Parse(string requestKind, string json) => requestKind switch
    {
        "create" => LocalRepositoryJson.TryParseCreate(Encoding.UTF8.GetBytes(json), out _),
        "update" => LocalRepositoryJson.TryParseUpdate(Encoding.UTF8.GetBytes(json), out _),
        _ => LocalRepositoryJson.TryParseSessionAction(Encoding.UTF8.GetBytes(json), out _),
    };

    private static string ReplaceStringValue(string json, string property, string replacement)
    {
        var start = json.IndexOf($"\"{property}\":", StringComparison.Ordinal);
        var end = json.IndexOf(',', start);
        if (end < 0) end = json.IndexOf('}', start);
        return json[..start] + $"\"{property}\":\"{replacement}\"" + json[end..];
    }

    private static string ToErrorCode(LocalRepositoryError error) => error switch
    {
        LocalRepositoryError.InvalidRequest => "invalid_request",
        LocalRepositoryError.InvalidLocator => "invalid_locator",
        LocalRepositoryError.RepositoryNotFound => "repository_not_found",
        LocalRepositoryError.SessionNotFound => "session_not_found",
        LocalRepositoryError.RevisionConflict => "revision_conflict",
        LocalRepositoryError.LocatorConflict => "locator_conflict",
        LocalRepositoryError.LocatorLimitReached => "locator_limit_reached",
        LocalRepositoryError.IdempotencyConflict => "idempotency_conflict",
        LocalRepositoryError.CsrfRejected => "csrf_rejected",
        LocalRepositoryError.RequestTooLarge => "request_too_large",
        LocalRepositoryError.UnsupportedMediaType => "unsupported_media_type",
        LocalRepositoryError.MethodNotAllowed => "method_not_allowed",
        LocalRepositoryError.PersistenceBusy => "persistence_busy",
        LocalRepositoryError.LocalMonitorUiUnavailable => "local_monitor_ui_unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };
}
