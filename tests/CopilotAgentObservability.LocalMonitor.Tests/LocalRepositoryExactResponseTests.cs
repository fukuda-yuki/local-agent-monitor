using System.Text;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositoryExactResponseTests
{
    private const string RepositoryId = "01900000-0001-7000-8000-000000000001";
    private const string OtherRepositoryId = "01900000-0002-7000-8000-000000000002";
    private const string SessionId = "02900000-0001-7000-8000-000000000001";
    private const string TimestampJson = "2026-08-01T00:00:00.0000000\\u002B00:00";
    private const string Repository201 = "{\"schema_version\":\"local-repository.v1\",\"repository_id\":\"01900000-0001-7000-8000-000000000001\",\"display_name\":\"repo\",\"revision\":1,\"created_at\":\"2026-08-01T00:00:00.0000000\\u002B00:00\",\"updated_at\":\"2026-08-01T00:00:00.0000000\\u002B00:00\"}";
    private const string Assignment200 = "{\"schema_version\":\"local-session-repository-assignment.v1\",\"session_id\":\"02900000-0001-7000-8000-000000000001\",\"assignment_revision\":1,\"state\":\"assigned\",\"authority\":\"manual\",\"repository_id\":\"01900000-0001-7000-8000-000000000001\",\"conflicting_repository_ids\":[],\"observed_label_candidates\":[],\"updated_at\":\"2026-08-01T00:00:00.0000000\\u002B00:00\"}";

    [Theory]
    [InlineData(201, Repository201, 0, RepositoryId, 1, null)]
    [InlineData(200, Repository201, 0, RepositoryId, 1, null)]
    [InlineData(200, Assignment200, 1, SessionId, 1, "assigned")]
    public void ValidateMutationEntity_DecodesEveryLegalStatusAndKind(
        int statusCode,
        string json,
        int kind,
        string targetId,
        long revision,
        string? state)
    {
        var decoded = LocalRepositoryExactResponse.ValidateMutationEntity(statusCode, Encoding.UTF8.GetBytes(json));

        Assert.Equal((LocalRepositoryMutationEntityKind)kind, decoded.Kind);
        Assert.Equal(targetId, decoded.TargetId);
        Assert.Equal(revision, decoded.Revision);
        Assert.Equal(state, decoded.State);
    }

    [Fact]
    public void ValidateMutationEntity_Rejects201AssignmentAndUnknown200Schema()
    {
        AssertInvalid(201, Assignment200);
        AssertInvalid(200, Repository201.Replace("local-repository.v1", "local-repository.v2", StringComparison.Ordinal));
        AssertInvalid(199, Repository201);
        AssertInvalid(202, Repository201);
    }

    [Theory]
    [InlineData(200, 0, Assignment200)]
    [InlineData(200, 1, Repository201)]
    public void ReceiptConstruction_RejectsTheOtherLegal200Kind(
        int statusCode,
        int expectedKind,
        string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);

        Assert.Throws<InvalidOperationException>(() =>
            LocalRepositoryExactResponse.CreateSuccess(statusCode, (LocalRepositoryMutationEntityKind)expectedKind, bytes));
        Assert.Throws<InvalidOperationException>(() =>
            LocalRepositoryExactResponse.FromStored(
                statusCode,
                (LocalRepositoryMutationEntityKind)expectedKind,
                statusCode,
                LocalRepositoryExactResponse.SuccessContentType,
                LocalRepositoryExactResponse.SuccessCacheControl,
                bytes));
    }

    [Fact]
    public void ReceiptConstruction_RetainsDecodedFactsAndAnOwnedByteCopy()
    {
        var source = Encoding.UTF8.GetBytes(Repository201);
        var response = LocalRepositoryExactResponse.CreateSuccess(
            201,
            LocalRepositoryMutationEntityKind.Repository,
            source);
        source[0] = (byte)'[';

        Assert.Equal(LocalRepositoryMutationEntityKind.Repository, response.Decoded.Kind);
        Assert.Equal(RepositoryId, response.Decoded.TargetId);
        Assert.Equal(1, response.Decoded.Revision);
        Assert.Null(response.Decoded.State);
        Assert.Equal(Repository201, Encoding.UTF8.GetString(response.CopyEntity()));
    }

    public static IEnumerable<object[]> InvalidRepositoryEntities()
    {
        yield return Case(Repository201.Replace(
            "\"schema_version\":\"local-repository.v1\",\"repository_id\"",
            "\"repository_id\":\"01900000-0001-7000-8000-000000000001\",\"schema_version\"",
            StringComparison.Ordinal));
        yield return Case(Repository201.Replace("\"display_name\":\"repo\"", "\"display_name\":\"repo\",\"display_name\":\"repo\"", StringComparison.Ordinal));
        yield return Case(Repository201.Replace("\"revision\":1", "\"unknown\":null,\"revision\":1", StringComparison.Ordinal));
        yield return Case(Repository201.Replace(",\"display_name\":\"repo\"", string.Empty, StringComparison.Ordinal));
        yield return Case(Repository201.Replace("{\"schema_version\"", "{ \"schema_version\"", StringComparison.Ordinal));
        yield return Case(Repository201 + "\n");
        yield return Case(Repository201 + "null");
        yield return Case(Repository201.Replace("\"revision\":1", "/*comment*/\"revision\":1", StringComparison.Ordinal));
        yield return Case(Repository201[..^1] + ",}");
        yield return Case(Repository201.Replace("local-repository.v1", "\\u006cocal-repository.v1", StringComparison.Ordinal));
        yield return Case(Repository201.Replace("\"repository_id\":\"" + RepositoryId + "\"", "\"repository_id\":1", StringComparison.Ordinal));
        yield return Case(Repository201.Replace(RepositoryId, "01900000-0001-7000-8000-00000000000A", StringComparison.Ordinal));
        yield return Case(Repository201.Replace(RepositoryId, "01900000-0001-4000-8000-000000000001", StringComparison.Ordinal));
        yield return Case(Repository201.Replace("\"display_name\":\"repo\"", "\"display_name\":\" repo\"", StringComparison.Ordinal));
        yield return Case(Repository201.Replace("\"revision\":1", "\"revision\":0", StringComparison.Ordinal));
        yield return Case(Repository201.Replace("\"revision\":1", "\"revision\":1.0", StringComparison.Ordinal));
        yield return Case(Repository201.Replace(TimestampJson, "2026-08-01T00:00:00.000000Z", StringComparison.Ordinal));
        yield return Case(Repository201.Replace(TimestampJson, "2026-08-01T09:00:00.0000000\\u002B09:00", StringComparison.Ordinal));
        yield return Case("[]");
    }

    [Theory]
    [MemberData(nameof(InvalidRepositoryEntities))]
    public void ValidateMutationEntity_RejectsNonCanonicalRepositoryBytes(string json) => AssertInvalid(201, json);

    [Fact]
    public void ValidateMutationEntity_RejectsBomAndInvalidUtf8()
    {
        AssertInvalid(201, [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(Repository201)]);
        AssertInvalid(201, [0xff]);
    }

    public static IEnumerable<object[]> InvalidAssignmentEntities()
    {
        yield return AssignmentCase(revision: 1, state: "assigned", authority: "none", repositoryId: RepositoryId, conflicts: "[]", updatedAt: QuotedTimestamp);
        yield return AssignmentCase(revision: 1, state: "assigned", authority: "manual", repositoryId: null, conflicts: "[]", updatedAt: QuotedTimestamp);
        yield return AssignmentCase(revision: 1, state: "unassigned", authority: "automatic", repositoryId: null, conflicts: "[]", updatedAt: QuotedTimestamp);
        yield return AssignmentCase(revision: 1, state: "unassigned", authority: "none", repositoryId: RepositoryId, conflicts: "[]", updatedAt: QuotedTimestamp);
        yield return AssignmentCase(revision: 1, state: "explicitly_unassigned", authority: "none", repositoryId: null, conflicts: "[]", updatedAt: QuotedTimestamp);
        yield return AssignmentCase(revision: 1, state: "conflict", authority: "manual", repositoryId: null, conflicts: $"[\"{RepositoryId}\",\"{OtherRepositoryId}\"]", updatedAt: QuotedTimestamp);
        yield return AssignmentCase(revision: 1, state: "conflict", authority: "automatic", repositoryId: RepositoryId, conflicts: $"[\"{RepositoryId}\",\"{OtherRepositoryId}\"]", updatedAt: QuotedTimestamp);
        yield return AssignmentCase(revision: 1, state: "conflict", authority: "automatic", repositoryId: null, conflicts: $"[\"{RepositoryId}\"]", updatedAt: QuotedTimestamp);
        yield return AssignmentCase(revision: 1, state: "conflict", authority: "automatic", repositoryId: null, conflicts: $"[\"{OtherRepositoryId}\",\"{RepositoryId}\"]", updatedAt: QuotedTimestamp);
        yield return AssignmentCase(revision: 1, state: "conflict", authority: "automatic", repositoryId: null, conflicts: $"[\"{RepositoryId}\",\"{RepositoryId}\"]", updatedAt: QuotedTimestamp);
        yield return AssignmentCase(revision: 1, state: "unknown", authority: "automatic", repositoryId: null, conflicts: "[]", updatedAt: QuotedTimestamp);
        yield return AssignmentCase(revision: 0, state: "assigned", authority: "manual", repositoryId: RepositoryId, conflicts: "[]", updatedAt: "null");
        yield return AssignmentCase(revision: 0, state: "unassigned", authority: "none", repositoryId: null, conflicts: "[]", updatedAt: QuotedTimestamp);
        yield return AssignmentCase(revision: 1, state: "unassigned", authority: "none", repositoryId: null, conflicts: "[]", updatedAt: "null");
        yield return AssignmentCase(revision: 1, state: "unassigned", authority: "none", repositoryId: null, conflicts: "[]", observedLabels: "[\"label\"]", updatedAt: QuotedTimestamp);
        yield return AssignmentCase(revision: 1, state: "unassigned", authority: "none", repositoryId: null, conflicts: "null", updatedAt: QuotedTimestamp);
        yield return Case(Assignment200.Replace($"\"session_id\":\"{SessionId}\"", "\"session_id\":1", StringComparison.Ordinal));
        yield return Case(Assignment200.Replace(SessionId, "02900000-0001-4000-8000-000000000001", StringComparison.Ordinal));
        yield return Case(Assignment200.Replace("\"assignment_revision\":1", "\"assignment_revision\":\"1\"", StringComparison.Ordinal));
        yield return Case(Assignment200.Replace("\"assignment_revision\":1", "\"assignment_revision\":1.0", StringComparison.Ordinal));
        yield return Case(Assignment200.Replace("\"assignment_revision\":1", "\"assignment_revision\":-1", StringComparison.Ordinal));
        yield return Case(Assignment200.Replace("\"state\":\"assigned\"", "\"state\":null", StringComparison.Ordinal));
        yield return Case(Assignment200.Replace("\"authority\":\"manual\"", "\"authority\":1", StringComparison.Ordinal));
        yield return Case(Assignment200.Replace($"\"repository_id\":\"{RepositoryId}\"", "\"repository_id\":[]", StringComparison.Ordinal));
        yield return Case(Assignment200.Replace("\"conflicting_repository_ids\":[]", "\"conflicting_repository_ids\":[1]", StringComparison.Ordinal));
        yield return Case(Assignment200.Replace("\"observed_label_candidates\":[]", "\"observed_label_candidates\":null", StringComparison.Ordinal));
        yield return Case(Assignment200.Replace($"\"updated_at\":\"{TimestampJson}\"", "\"updated_at\":1", StringComparison.Ordinal));
        yield return Case(Assignment200.Replace(TimestampJson, "2026-08-01T00:00:00.000000Z", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(InvalidAssignmentEntities))]
    public void ValidateMutationEntity_RejectsInvalidAssignmentSemantics(string json) => AssertInvalid(200, json);

    [Fact]
    public void ValidateMutationEntity_AcceptsExactly128SortedConflictIdsAndRejects129()
    {
        var ids = Enumerable.Range(1, 129)
            .Select(value => $"01900000-0000-7000-8000-{value:x12}")
            .ToArray();
        var valid = Assignment(
            1,
            "conflict",
            "automatic",
            null,
            "[" + string.Join(',', ids.Take(128).Select(id => $"\"{id}\"")) + "]",
            "[]",
            QuotedTimestamp);

        var decoded = LocalRepositoryExactResponse.ValidateMutationEntity(200, Encoding.UTF8.GetBytes(valid));

        Assert.Equal(LocalRepositoryMutationEntityKind.Assignment, decoded.Kind);
        Assert.Equal("conflict", decoded.State);
        AssertInvalid(200, Assignment(
            1,
            "conflict",
            "automatic",
            null,
            "[" + string.Join(',', ids.Select(id => $"\"{id}\"")) + "]",
            "[]",
            QuotedTimestamp));
    }

    [Fact]
    public void ValidateMutationEntity_RejectsOver16384BytesBeforeJsonMaterialization()
    {
        var oversized = Enumerable.Repeat((byte)'{', 16_385).ToArray();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            LocalRepositoryExactResponse.ValidateMutationEntity(200, oversized));

        Assert.Null(exception.InnerException);
    }

    private static string QuotedTimestamp => $"\"{TimestampJson}\"";

    private static object[] Case(string json) => [json];

    private static object[] AssignmentCase(
        long revision,
        string state,
        string authority,
        string? repositoryId,
        string conflicts,
        string observedLabels = "[]",
        string? updatedAt = null) =>
        [Assignment(revision, state, authority, repositoryId, conflicts, observedLabels, updatedAt ?? QuotedTimestamp)];

    private static string Assignment(
        long revision,
        string state,
        string authority,
        string? repositoryId,
        string conflicts,
        string observedLabels,
        string updatedAt) =>
        $"{{\"schema_version\":\"local-session-repository-assignment.v1\",\"session_id\":\"{SessionId}\",\"assignment_revision\":{revision},\"state\":\"{state}\",\"authority\":\"{authority}\",\"repository_id\":{(repositoryId is null ? "null" : $"\"{repositoryId}\"")},\"conflicting_repository_ids\":{conflicts},\"observed_label_candidates\":{observedLabels},\"updated_at\":{updatedAt}}}";

    private static void AssertInvalid(int statusCode, string json) => AssertInvalid(statusCode, Encoding.UTF8.GetBytes(json));

    private static void AssertInvalid(int statusCode, byte[] bytes) =>
        Assert.Throws<InvalidOperationException>(() => LocalRepositoryExactResponse.ValidateMutationEntity(statusCode, bytes));
}
