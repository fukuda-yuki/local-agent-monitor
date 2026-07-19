using System.Globalization;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionMutationRepositoriesTests
{
    [Fact]
    public void Idempotency_SameStepAndCanonicalRequestReplaysExactPayloadWithoutAddingRows()
    {
        using var fixture = Fixture.Create();
        var request = fixture.Idempotency("preview", "{\"operation\":\"pin\"}", "{\"result\":\"stored\"}");

        var first = fixture.Store.GetOrCreateIdempotency(request);
        var second = new RetentionCatalogStore(fixture.Path, fixture.Time).GetOrCreateIdempotency(request);

        Assert.Equal(RetentionIdempotencyDisposition.Created, first.Disposition);
        Assert.Equal(RetentionIdempotencyDisposition.Replayed, second.Disposition);
        Assert.Equal(first.ResultJson, second.ResultJson);
        Assert.Equal(first.CompletionCode, second.CompletionCode);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_idempotency;"));
    }

    [Fact]
    public void Idempotency_DifferentCanonicalRequestConflictsAndDifferentStepsShareKey()
    {
        using var fixture = Fixture.Create();
        var preview = fixture.Idempotency("preview", "{\"operation\":\"pin\"}", "{\"preview\":1}");
        var differentPreview = preview with { CanonicalRequest = "{\"operation\":\"unpin\"}" };
        var confirmation = fixture.Idempotency("confirmation_issue", "{\"preview_id\":\"rpv1_preview\"}", "{\"confirmation_id\":\"rcid1_confirmation\"}") with { WorkflowKey = preview.WorkflowKey };

        Assert.Equal(RetentionIdempotencyDisposition.Created, fixture.Store.GetOrCreateIdempotency(preview).Disposition);
        Assert.Equal(RetentionIdempotencyDisposition.Conflict, fixture.Store.GetOrCreateIdempotency(differentPreview).Disposition);
        Assert.Equal(RetentionIdempotencyDisposition.Created, fixture.Store.GetOrCreateIdempotency(confirmation).Disposition);
        Assert.Equal(2L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_idempotency;"));
    }

    [Fact]
    public void Idempotency_UsesFirstDurableCreationFor365DayTombstoneAndSurvivesRestart()
    {
        using var fixture = Fixture.Create();
        var request = fixture.Idempotency("preview", "{\"operation\":\"pin\"}", "{\"result\":1}");
        var created = fixture.Store.GetOrCreateIdempotency(request);
        fixture.Time.Advance(TimeSpan.FromDays(365));

        var expired = new RetentionCatalogStore(fixture.Path, fixture.Time).GetOrCreateIdempotency(request);
        var otherStep = new RetentionCatalogStore(fixture.Path, fixture.Time).GetOrCreateIdempotency(
            fixture.Idempotency("mutation", "{\"confirmation_token\":\"redacted\"}", "{\"result\":2}") with { WorkflowKey = request.WorkflowKey });

        Assert.Equal(RetentionIdempotencyDisposition.Expired, expired.Disposition);
        Assert.Equal(RetentionIdempotencyDisposition.Expired, otherStep.Disposition);
        Assert.Equal(created.ExpiresAt, expired.ExpiresAt);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_idempotency;"));

        SqliteConnection.ClearAllPools();
        var reopened = new RetentionCatalogStore(fixture.Path, fixture.Time).GetOrCreateIdempotency(request);
        Assert.Equal(RetentionIdempotencyDisposition.Expired, reopened.Disposition);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_idempotency;"));
    }

    [Fact]
    public void Idempotency_RejectsPlaintextConfirmationTokenInStoredResult()
    {
        using var fixture = Fixture.Create();
        var nonce = Enumerable.Repeat((byte)1, RetentionMutationIdentifierFormats.NonceByteLength).ToArray();
        var secret = Enumerable.Repeat((byte)2, RetentionMutationIdentifierFormats.SecretByteLength).ToArray();
        var token = RetentionMutationToken.Create(nonce, secret);

        Assert.Throws<ArgumentException>(() => fixture.Store.GetOrCreateIdempotency(
            fixture.Idempotency("confirmation_issue", "{\"preview_id\":\"rpv1_preview\"}", $"{{\"token\":\"{token}\"}}")));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_idempotency;"));
    }

    [Fact]
    public void Audit_ValidatesCommentAndPersistsExactCanonicalSevenKeyCountsWithoutUpdateOrDeleteSurface()
    {
        using var fixture = Fixture.Create();
        var eventRow = fixture.Audit(fixture.AuditEventId(1), fixture.Time.GetUtcNow(), "safe comment");

        fixture.Store.AppendAuditEvent(eventRow);
        var forbidden = eventRow with { EventId = fixture.AuditEventId(2), Comment = "contains password marker" };

        Assert.Throws<ArgumentException>(() => fixture.Store.AppendAuditEvent(forbidden));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
        Assert.DoesNotContain(typeof(RetentionCatalogStore).GetMethods(), method =>
            method.Name.Contains("UpdateAudit", StringComparison.OrdinalIgnoreCase)
            || method.Name.Contains("DeleteAudit", StringComparison.OrdinalIgnoreCase));

        using var connection = fixture.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT previous_operation_state,new_operation_state FROM retention_audit_events WHERE event_id=$id;";
        command.Parameters.AddWithValue("$id", eventRow.EventId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        foreach (var index in new[] { 0, 1 })
        {
            using var json = JsonDocument.Parse(reader.GetString(index));
            Assert.Equal(7, json.RootElement.EnumerateObject().Count());
            Assert.Equal(1, json.RootElement.GetProperty("expiring").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("retained_by_policy").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("expired_pending_deletion").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("deletion_queued").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("deleting").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("deleted").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("deletion_failed").GetInt32());
        }
    }

    [Fact]
    public void Audit_ReadsTargetInOccurredAtThenEventIdDescendingOrderAndSurvivesRestart()
    {
        using var fixture = Fixture.Create();
        var laterLow = fixture.Audit(fixture.AuditEventId(1), fixture.Time.GetUtcNow().AddMinutes(2), null);
        var laterHigh = fixture.Audit(fixture.AuditEventId(2), fixture.Time.GetUtcNow().AddMinutes(2), "safe comment");
        var earlier = fixture.Audit(fixture.AuditEventId(3), fixture.Time.GetUtcNow().AddMinutes(1), null);
        fixture.Store.AppendAuditEvent(earlier);
        fixture.Store.AppendAuditEvent(laterHigh);
        fixture.Store.AppendAuditEvent(laterLow);

        SqliteConnection.ClearAllPools();
        var events = new RetentionCatalogStore(fixture.Path, fixture.Time).ReadAuditEvents(
            new RetentionMutationTarget(RetentionMutationTargetKind.Item, "item-a"));

        Assert.Equal([laterHigh.EventId, laterLow.EventId, earlier.EventId], events.Select(static item => item.EventId));
        Assert.Equal("safe comment", fixture.ScalarText("SELECT comment FROM retention_audit_events WHERE event_id=$id;", ("$id", laterHigh.EventId)));
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string path, MutableTimeProvider time, RetentionCatalogStore store) => (Path, Time, Store) = (path, time, store);

        internal string Path { get; }
        internal MutableTimeProvider Time { get; }
        internal RetentionCatalogStore Store { get; }

        internal static Fixture Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retention-repositories-{Guid.NewGuid():N}.sqlite");
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            return new(path, time, store);
        }

        internal RetentionIdempotencyRequest Idempotency(string step, string canonicalRequest, string resultJson) => new(
            RetentionMutationIdentifiers.GenerateWorkflowKey(),
            step switch
            {
                "preview" => RetentionMutationOperationStep.Preview,
                "confirmation_issue" => RetentionMutationOperationStep.ConfirmationIssue,
                "mutation" => RetentionMutationOperationStep.Mutation,
                _ => throw new ArgumentOutOfRangeException(nameof(step))
            },
            canonicalRequest,
            resultJson,
            RetentionMutationCompletionCodes.PinApplied);

        internal RetentionAuditEvent Audit(string eventId, DateTimeOffset occurredAt, string? comment) => new(
            eventId,
            "op-1",
            RetentionMutationConstants.EventType,
            RetentionMutationTargetKind.Item,
            "item-a",
            null,
            occurredAt,
            RetentionMutationConstants.ActorLabel,
            RetentionMutationOperation.Pin,
            RetentionMutationReasonCodes.ResearchNeeded,
            comment,
            RetentionPinState.Unpinned,
            RetentionPinState.Pinned,
            new(1, 0, 0, 0, 0, 0, 0),
            new(1, 0, 0, 0, 0, 0, 0),
            RetentionMutationIdentifiers.GenerateWorkflowKey(),
            "v1-" + new string('1', 64),
            "v1-" + new string('2', 64),
            "sha256-" + new string('3', 64),
            RetentionMutationCompletionCodes.PinApplied,
            null);

        internal string AuditEventId(byte value) => RetentionMutationIdentifiers.CreateAuditEventId(Enumerable.Repeat(value, RetentionMutationIdentifierFormats.NonceByteLength).ToArray());

        internal long Scalar(string sql) => Convert.ToInt64(ScalarValue(sql, []), CultureInfo.InvariantCulture);
        internal string ScalarText(string sql, params (string Name, object Value)[] values) => (string)ScalarValue(sql, values)!;

        internal SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }

        private object ScalarValue(string sql, IReadOnlyList<(string Name, object Value)> values)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
            return command.ExecuteScalar()!;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { Path, Path + "-wal", Path + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }
}
