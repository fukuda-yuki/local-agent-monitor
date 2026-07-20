using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionMutationConfirmationApplicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IssueConfirmation_ValidatesStoredPreviewAndKeepsTokenOutOfPersistedState()
    {
        using var fixture = Fixture.Create();
        var preview = fixture.CreatePreview();

        var result = fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest),
            fixture.WorkflowKey(1));

        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(result.Confirmation);
        Assert.Equal(RetentionConfirmationIssuePersistenceDisposition.IssuedFresh, result.Disposition);
        Assert.Null(result.ErrorCode);
        Assert.StartsWith("rcid1_", confirmation.ConfirmationId, StringComparison.Ordinal);
        Assert.StartsWith("rt90v1_", confirmation.ConfirmationToken, StringComparison.Ordinal);
        Assert.Equal(preview.ConfirmationExpiresAt, confirmation.ConfirmationExpiresAt);
        Assert.DoesNotContain(confirmation.ConfirmationToken, fixture.PersistedText(), StringComparison.Ordinal);
        Assert.DoesNotContain(confirmation.ConfirmationToken, fixture.Store.ReadConfirmationBinding(confirmation.ConfirmationId)!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void IssueConfirmation_RejectsDigestExpiryEmptyAndPreviewRejectionBeforeBinding()
    {
        using (var fixture = Fixture.Create())
        {
            var preview = fixture.CreatePreview();
            var mismatch = fixture.Application.IssueConfirmation(
                new(preview.PreviewId, "sha256-" + new string('f', 64)),
                fixture.WorkflowKey(2));
            Assert.Equal(RetentionMutationErrorCodes.PreviewDigestMismatch, mismatch.ErrorCode);
            Assert.Null(mismatch.Confirmation);
            Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings;"));
        }

        using (var fixture = Fixture.Create())
        {
            var preview = fixture.CreatePreview();
            fixture.Time.Advance(RetentionMutationConstants.ConfirmationLifetime);
            var expired = fixture.Application.IssueConfirmation(
                new(preview.PreviewId, preview.PreviewDigest),
                fixture.WorkflowKey(3));
            Assert.Equal(RetentionMutationErrorCodes.PreviewExpired, expired.ErrorCode);
        }

        using (var fixture = Fixture.Create(itemCount: 0))
        {
            var preview = fixture.CreatePreview(session: true);
            var empty = fixture.Application.IssueConfirmation(
                new(preview.PreviewId, preview.PreviewDigest),
                fixture.WorkflowKey(4));
            Assert.Equal(RetentionMutationErrorCodes.TargetEmpty, empty.ErrorCode);
        }

        using (var fixture = Fixture.Create())
        {
            fixture.SetState("deleting");
            var preview = fixture.CreatePreview();
            var rejected = fixture.Application.IssueConfirmation(
                new(preview.PreviewId, preview.PreviewDigest),
                fixture.WorkflowKey(5));
            Assert.Equal(RetentionMutationErrorCodes.PinDeleting, rejected.ErrorCode);
        }
    }

    [Fact]
    public void IssueConfirmation_RechecksCatalogDriftInPinnedIssuanceOrder()
    {
        var cases = new (string Code, Action<Fixture> Mutate)[]
        {
            (RetentionMutationErrorCodes.ConfirmationTargetChanged, fixture => fixture.Execute(
                "UPDATE retention_items SET item_id='replacement-item' WHERE item_id=$item;",
                ("$item", fixture.ItemId))),
            (RetentionMutationErrorCodes.ConfirmationPinChanged, fixture => fixture.Execute(
                "UPDATE retention_items SET state='retained_by_policy' WHERE item_id=$item;",
                ("$item", fixture.ItemId))),
            (RetentionMutationErrorCodes.ConfirmationRetentionChanged, fixture => fixture.Execute(
                "UPDATE retention_items SET expires_at=$expires WHERE item_id=$item;",
                ("$item", fixture.ItemId), ("$expires", Now.AddDays(2).ToString("O", CultureInfo.InvariantCulture)))),
            (RetentionMutationErrorCodes.ConfirmationConflictChanged, fixture => fixture.InsertReadLease()),
            (RetentionMutationErrorCodes.ConfirmationVersionChanged, fixture => fixture.Execute(
                "UPDATE retention_items SET revision=revision+1 WHERE item_id=$item;",
                ("$item", fixture.ItemId)))
        };

        foreach (var (code, mutate) in cases)
        {
            using var fixture = Fixture.Create();
            var preview = fixture.CreatePreview();
            mutate(fixture);

            var result = fixture.Application.IssueConfirmation(
                new(preview.PreviewId, preview.PreviewDigest),
                fixture.WorkflowKey((byte)(10 + Array.IndexOf(cases, (code, mutate)))));

            Assert.Equal(code, result.ErrorCode);
            Assert.Null(result.Confirmation);
            Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings;"));
        }
    }

    [Fact]
    public void IssueConfirmation_SurfacesFreshReissueConsumedLinkageConflictAndNonceCollision()
    {
        using (var fixture = Fixture.Create())
        {
            var preview = fixture.CreatePreview();
            var request = new RetentionConfirmationIssueRequest(preview.PreviewId, preview.PreviewDigest);
            var key = fixture.WorkflowKey(20);
            var fresh = fixture.Application.IssueConfirmation(request, key);
            var reissued = fixture.Application.IssueConfirmation(request, key);
            Assert.Equal(RetentionConfirmationIssuePersistenceDisposition.IssuedFresh, fresh.Disposition);
            Assert.Equal(RetentionConfirmationIssuePersistenceDisposition.ReissuedAfterInvalidation, reissued.Disposition);

            var consumed = fixture.Store.ConsumeConfirmation(reissued.Confirmation!.ConfirmationToken);
            Assert.Equal(RetentionConfirmationConsumptionDisposition.Consumed, consumed.Disposition);
            var linkage = fixture.Application.IssueConfirmation(request, key);
            Assert.Equal(RetentionConfirmationIssuePersistenceDisposition.ConsumedLinkage, linkage.Disposition);
            Assert.Equal(RetentionMutationErrorCodes.ConfirmationConsumed, linkage.ErrorCode);
        }

        using (var fixture = Fixture.Create(itemCount: 2))
        {
            var first = fixture.CreatePreview(itemIndex: 0);
            var second = fixture.CreatePreview(itemIndex: 1);
            var key = fixture.WorkflowKey(21);
            Assert.Equal(RetentionConfirmationIssuePersistenceDisposition.IssuedFresh,
                fixture.Application.IssueConfirmation(new(first.PreviewId, first.PreviewDigest), key).Disposition);
            var conflict = fixture.Application.IssueConfirmation(new(second.PreviewId, second.PreviewDigest), key);
            Assert.Equal(RetentionConfirmationIssuePersistenceDisposition.Conflict, conflict.Disposition);
            Assert.Equal(RetentionMutationErrorCodes.IdempotencyConflict, conflict.ErrorCode);
        }

        using (var fixture = Fixture.Create())
        {
            var preview = fixture.CreatePreview();
            var token = fixture.Token(31, 32);
            var application = fixture.NewApplication(
                tokenGenerator: () => token,
                confirmationIdGenerator: () => fixture.ConfirmationId(33));
            var request = new RetentionConfirmationIssueRequest(preview.PreviewId, preview.PreviewDigest);
            Assert.Equal(RetentionConfirmationIssuePersistenceDisposition.IssuedFresh,
                application.IssueConfirmation(request, fixture.WorkflowKey(22)).Disposition);
            var collision = application.IssueConfirmation(request, fixture.WorkflowKey(23));
            Assert.Equal(RetentionConfirmationIssuePersistenceDisposition.GenerationFailed, collision.Disposition);
            Assert.Equal(RetentionMutationErrorCodes.ConfirmationGenerationFailed, collision.ErrorCode);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private byte issuanceEntropy = 1;

        private Fixture(string path, MutableTimeProvider time, RetentionCatalogStore store, string sessionId, string itemId, IReadOnlyList<string> itemIds)
        {
            Path = path;
            Time = time;
            Store = store;
            SessionId = sessionId;
            ItemId = itemId;
            ItemIds = itemIds;
            Application = new RetentionMutationApplicationService(
                store,
                time,
                confirmationIdGenerator: () => ConfirmationId(issuanceEntropy++),
                tokenGenerator: () =>
                {
                    var nonce = issuanceEntropy++;
                    return Token(nonce, (byte)(nonce + 1));
                });
        }

        internal string Path { get; }
        internal MutableTimeProvider Time { get; }
        internal RetentionCatalogStore Store { get; }
        internal string SessionId { get; }
        internal string ItemId { get; }
        internal IReadOnlyList<string> ItemIds { get; }
        internal RetentionMutationApplicationService Application { get; }

        internal static Fixture Create(int itemCount = 1)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retention-confirmation-application-{Guid.NewGuid():N}.sqlite");
            var time = new MutableTimeProvider(Now);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            var sessionStore = new SqliteSessionStore(path, context, time);
            sessionStore.CreateSchema();
            var sessionId = Guid.Parse("018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071");
            var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full, null, null,
                Now.AddMinutes(-1), Now, Now, SessionRawRetentionState.Expiring, Now.AddMinutes(-1), Now);
            var events = Enumerable.Range(0, itemCount).Select(index => new ObservedSessionEvent(
                Guid.Parse($"018f2b4e-7c1a-7f1a-8a2b-{index + 0x6072:X12}"), sessionId, null,
                SessionSourceSurface.CopilotSdk, null, $"trace-{index}", "received", "copilot-sdk-stream",
                $"event-{index}", "user.message", Now.AddSeconds(index), SessionContentState.Available)).ToArray();
            var content = events.Select((item, index) => new SessionEventContent(item.EventId, "application/json",
                $"{{\"index\":{index}}}", Now.AddSeconds(index), Now.AddDays(90).AddSeconds(index))).ToArray();
            sessionStore.Write(new(new(session, [], [], events), content));
            var store = new RetentionCatalogStore(context, time);
            var ids = itemCount == 0 ? Array.Empty<string>() : ReadItems(path);
            return new(path, time, store, sessionId.ToString("D"), ids.FirstOrDefault() ?? "missing-item", ids);
        }

        internal RetentionMutationPreviewResponse CreatePreview(bool session = false, int itemIndex = 0)
        {
            var request = new RetentionMutationPreviewRequest(
                session ? new(RetentionMutationTargetKind.Session, SessionId) : new(RetentionMutationTargetKind.Item, ItemIds[itemIndex]),
                RetentionMutationOperation.Pin,
                session ? RetentionMutationScope.SessionItems : RetentionMutationScope.SingleItem,
                RetentionMutationReasonCodes.ResearchNeeded,
                null);
            return Assert.IsType<RetentionMutationPreviewResponse>(Application.CreatePreview(request, WorkflowKey((byte)(40 + itemIndex))).Preview);
        }

        internal RetentionMutationApplicationService NewApplication(
            Func<string>? tokenGenerator = null,
            Func<string>? confirmationIdGenerator = null) =>
            new(Store, Time, previewIdGenerator: () => RetentionMutationIdentifiers.CreatePreviewId(Enumerable.Repeat((byte)50, 16).ToArray()),
                confirmationIdGenerator: confirmationIdGenerator, tokenGenerator: tokenGenerator);

        internal string WorkflowKey(byte value) => RetentionMutationIdentifiers.CreateWorkflowKey(Enumerable.Repeat(value, 32).ToArray());

        internal string ConfirmationId(byte value) => RetentionMutationIdentifiers.CreateConfirmationId(Enumerable.Repeat(value, 16).ToArray());

        internal string Token(byte nonce, byte secret) => RetentionMutationToken.Create(
            Enumerable.Repeat(nonce, 16).ToArray(), Enumerable.Repeat(secret, 32).ToArray());

        internal void SetState(string state) => Execute("UPDATE retention_items SET state=$state WHERE item_id=$item;", ("$state", state), ("$item", ItemId));

        internal void InsertReadLease() => Execute(
            "INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) VALUES($item,'access','owner',$expires,99);",
            ("$item", ItemId), ("$expires", Now.AddMinutes(1).ToString("O", CultureInfo.InvariantCulture)));

        internal string PersistedText()
        {
            using var connection = Open(Path);
            using var tables = connection.CreateCommand();
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
            var names = new List<string>();
            using (var reader = tables.ExecuteReader()) while (reader.Read()) names.Add(reader.GetString(0));
            var values = new StringBuilder();
            foreach (var table in names)
            {
                using var columns = connection.CreateCommand();
                columns.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\");";
                var columnNames = new List<string>();
                using (var reader = columns.ExecuteReader()) while (reader.Read()) columnNames.Add(reader.GetString(1));
                foreach (var column in columnNames)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"SELECT \"{column.Replace("\"", "\"\"")}\" FROM \"{table.Replace("\"", "\"\"")}\";";
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) if (!reader.IsDBNull(0) && reader.GetValue(0) is string value) values.Append(value);
                }
            }
            return values.ToString();
        }

        internal long Scalar(string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        internal void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { Path, Path + "-wal", Path + "-shm" }) if (File.Exists(file)) File.Delete(file);
        }

        private static string[] ReadItems(string path)
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT item_id FROM retention_items WHERE store_kind='session_event_content' ORDER BY item_id;";
            using var reader = command.ExecuteReader();
            var values = new List<string>();
            while (reader.Read()) values.Add(reader.GetString(0));
            return values.ToArray();
        }

        private static SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }
    }
}
