using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionConfirmationBindingPersistenceTests
{
    [Fact]
    public void StoreBinding_StoresOnlyFullTokenHashAndBindsExpiryToPreviewCreation()
    {
        using var fixture = Fixture.Create();
        var request = fixture.BindingRequest(fixture.Token(1, 2));

        var stored = fixture.Store.StoreConfirmationBinding(request);
        var validation = fixture.Store.ValidateConfirmationToken(request.ConfirmationToken);

        Assert.Equal(RetentionConfirmationBindingPersistenceDisposition.Stored, stored.Disposition);
        Assert.Equal(RetentionConfirmationValidationDisposition.Active, validation.Disposition);
        var binding = Assert.IsType<RetentionConfirmationBinding>(validation.Binding);
        Assert.Equal(fixture.PreviewCreatedAt.Add(RetentionMutationConstants.ConfirmationLifetime), binding.ConfirmationExpiresAt);
        Assert.Equal(SHA256.HashData(Encoding.ASCII.GetBytes(request.ConfirmationToken)), binding.TokenSha256);
        Assert.Equal(16, binding.Nonce.Length);
        Assert.DoesNotContain(request.ConfirmationToken, binding.ToString(), StringComparison.Ordinal);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE token_sha256=$hash;", ("$hash", SHA256.HashData(Encoding.ASCII.GetBytes(request.ConfirmationToken)))));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings;"));
    }

    [Fact]
    public void StoreBinding_RejectsActiveNonceCollisionWithoutInvalidatingExistingBinding()
    {
        using var fixture = Fixture.Create();
        var first = fixture.BindingRequest(fixture.Token(1, 2));
        fixture.Store.StoreConfirmationBinding(first);
        var collision = first with
        {
            ConfirmationId = fixture.ConfirmationId(2),
            ConfirmationToken = fixture.Token(1, 3)
        };

        var result = fixture.Store.StoreConfirmationBinding(collision);

        Assert.Equal(RetentionConfirmationBindingPersistenceDisposition.GenerationFailed, result.Disposition);
        Assert.Equal(RetentionConfirmationValidationDisposition.Active, fixture.Store.ValidateConfirmationToken(first.ConfirmationToken).Disposition);
        Assert.Equal(RetentionConfirmationValidationDisposition.Invalid, fixture.Store.ValidateConfirmationToken(collision.ConfirmationToken).Disposition);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE invalidated_at IS NOT NULL;"));
    }

    [Fact]
    public void StoreBinding_ReissueInvalidatesPriorUnconsumedBindingAndKeepsOneActive()
    {
        using var fixture = Fixture.Create();
        var first = fixture.BindingRequest(fixture.Token(1, 2));
        fixture.Store.StoreConfirmationBinding(first);
        var firstBinding = fixture.Store.ReadConfirmationBinding(first.ConfirmationId);
        Assert.NotNull(firstBinding);
        var firstExpiry = firstBinding!.ConfirmationExpiresAt;
        fixture.Time.Advance(TimeSpan.FromMinutes(2));
        var replacement = first with
        {
            ConfirmationId = fixture.ConfirmationId(2),
            ConfirmationToken = fixture.Token(3, 4)
        };

        fixture.Store.StoreConfirmationBinding(replacement);

        Assert.Equal(RetentionConfirmationValidationDisposition.Invalid, fixture.Store.ValidateConfirmationToken(first.ConfirmationToken).Disposition);
        Assert.Equal(RetentionConfirmationValidationDisposition.Active, fixture.Store.ValidateConfirmationToken(replacement.ConfirmationToken).Disposition);
        var replacementBinding = fixture.Store.ReadConfirmationBinding(replacement.ConfirmationId);
        Assert.NotNull(replacementBinding);
        Assert.Equal(firstExpiry, replacementBinding!.ConfirmationExpiresAt);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE consumed_at IS NULL AND invalidated_at IS NULL;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE invalidated_at IS NOT NULL;"));
    }

    [Fact]
    public void ValidateBinding_ClassifiesBadFormatUnknownInvalidatedConsumedAndExpired()
    {
        using var fixture = Fixture.Create();
        var first = fixture.BindingRequest(fixture.Token(1, 2));
        fixture.Store.StoreConfirmationBinding(first);
        var invalidated = first with { ConfirmationId = fixture.ConfirmationId(2), ConfirmationToken = fixture.Token(3, 4) };
        fixture.Store.StoreConfirmationBinding(invalidated);

        Assert.Equal(RetentionConfirmationValidationDisposition.Invalid, fixture.Store.ValidateConfirmationToken("not-a-token").Disposition);
        Assert.Equal(RetentionConfirmationValidationDisposition.Invalid, fixture.Store.ValidateConfirmationToken(fixture.Token(5, 6)).Disposition);
        Assert.Equal(RetentionConfirmationValidationDisposition.Invalid, fixture.Store.ValidateConfirmationToken(first.ConfirmationToken).Disposition);

        Assert.Equal(RetentionConfirmationConsumptionDisposition.Consumed, fixture.Store.ConsumeConfirmation(invalidated.ConfirmationToken).Disposition);
        Assert.Equal(RetentionConfirmationValidationDisposition.Consumed, fixture.Store.ValidateConfirmationToken(invalidated.ConfirmationToken).Disposition);

        var expiring = invalidated with { ConfirmationId = fixture.ConfirmationId(3), ConfirmationToken = fixture.Token(7, 8) };
        fixture.Store.StoreConfirmationBinding(expiring);
        fixture.Time.Advance(RetentionMutationConstants.ConfirmationLifetime);
        Assert.Equal(RetentionConfirmationValidationDisposition.Expired, fixture.Store.ValidateConfirmationToken(expiring.ConfirmationToken).Disposition);
    }

    [Fact]
    public void ConsumeWithinCallerTransaction_RollbackLeavesActiveAndCommitConsumesExactlyOnceAcrossRestart()
    {
        using var fixture = Fixture.Create();
        var request = fixture.BindingRequest(fixture.Token(1, 2));
        fixture.Store.StoreConfirmationBinding(request);

        using (var connection = fixture.Store.OpenMutationConnection())
        using (var transaction = fixture.Store.BeginMutationTransaction(connection))
        {
            var rolledBack = fixture.Store.TryConsumeConfirmationWithinTransaction(connection, transaction, request.ConfirmationToken);
            Assert.Equal(RetentionConfirmationConsumptionDisposition.Consumed, rolledBack.Disposition);
            transaction.Rollback();
        }
        Assert.Equal(RetentionConfirmationValidationDisposition.Active, fixture.Store.ValidateConfirmationToken(request.ConfirmationToken).Disposition);

        using (var connection = fixture.Store.OpenMutationConnection())
        using (var transaction = fixture.Store.BeginMutationTransaction(connection))
        {
            var committed = fixture.Store.TryConsumeConfirmationWithinTransaction(connection, transaction, request.ConfirmationToken);
            Assert.Equal(RetentionConfirmationConsumptionDisposition.Consumed, committed.Disposition);
            transaction.Commit();
        }

        SqliteConnection.ClearAllPools();
        var reopened = new RetentionCatalogStore(fixture.Path, fixture.Time);
        Assert.Equal(RetentionConfirmationValidationDisposition.Consumed, reopened.ValidateConfirmationToken(request.ConfirmationToken).Disposition);
        Assert.Equal(RetentionConfirmationConsumptionDisposition.AlreadyConsumed, reopened.ConsumeConfirmation(request.ConfirmationToken).Disposition);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE consumed_at IS NOT NULL;"));
    }

    [Fact]
    public async Task ConsumeConcurrentSameToken_HasExactlyOneCommitWinner()
    {
        using var fixture = Fixture.Create();
        var request = fixture.BindingRequest(fixture.Token(1, 2));
        fixture.Store.StoreConfirmationBinding(request);
        using var barrier = new Barrier(2);
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<RetentionConfirmationConsumptionResult> Consume(RetentionCatalogStore store)
        {
            barrier.SignalAndWait();
            using var connection = store.OpenMutationConnection();
            using var transaction = store.BeginMutationTransaction(connection);
            firstReady.TrySetResult();
            var result = store.TryConsumeConfirmationWithinTransaction(connection, transaction, request.ConfirmationToken);
            transaction.Commit();
            await Task.Yield();
            return result;
        }

        var taskA = Task.Run(() => Consume(new RetentionCatalogStore(fixture.Path, fixture.Time)));
        var taskB = Task.Run(() => Consume(new RetentionCatalogStore(fixture.Path, fixture.Time)));
        await firstReady.Task;
        var results = await Task.WhenAll(taskA, taskB);

        Assert.Equal(1, results.Count(result => result.Disposition == RetentionConfirmationConsumptionDisposition.Consumed));
        Assert.Equal(1, results.Count(result => result.Disposition == RetentionConfirmationConsumptionDisposition.AlreadyConsumed));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE consumed_at IS NOT NULL;"));
    }

    [Fact]
    public async Task ReissueConcurrentWithConsume_LeavesAtMostOneConsumableBinding()
    {
        using var fixture = Fixture.Create();
        var original = fixture.BindingRequest(fixture.Token(1, 2));
        fixture.Store.StoreConfirmationBinding(original);
        var replacement = original with { ConfirmationId = fixture.ConfirmationId(2), ConfirmationToken = fixture.Token(3, 4) };
        using var barrier = new Barrier(2);

        var reissueTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return fixture.Store.StoreConfirmationBinding(replacement);
        });
        var consumeTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return fixture.Store.ConsumeConfirmation(original.ConfirmationToken);
        });

        await Task.WhenAll(reissueTask, consumeTask);

        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE consumed_at IS NULL AND invalidated_at IS NULL;"));
        Assert.NotEqual(RetentionConfirmationValidationDisposition.Active, fixture.Store.ValidateConfirmationToken(original.ConfirmationToken).Disposition);
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string path, MutableTimeProvider time, RetentionCatalogStore store)
        {
            Path = path;
            Time = time;
            Store = store;
            PreviewCreatedAt = time.GetUtcNow().AddMinutes(-1);
        }

        internal string Path { get; }
        internal MutableTimeProvider Time { get; }
        internal RetentionCatalogStore Store { get; }
        internal DateTimeOffset PreviewCreatedAt { get; }

        internal static Fixture Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retention-confirmation-{Guid.NewGuid():N}.sqlite");
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            var fixture = new Fixture(path, time, store);
            fixture.InsertPreview();
            return fixture;
        }

        internal string PreviewId => RetentionMutationIdentifiers.CreatePreviewId(Enumerable.Repeat((byte)9, RetentionMutationIdentifierFormats.NonceByteLength).ToArray());
        internal string ConfirmationId(byte value) => RetentionMutationIdentifiers.CreateConfirmationId(Enumerable.Repeat(value, RetentionMutationIdentifierFormats.NonceByteLength).ToArray());
        internal string Token(byte nonce, byte secret) => RetentionMutationToken.Create(
            Enumerable.Repeat(nonce, RetentionMutationIdentifierFormats.NonceByteLength).ToArray(),
            Enumerable.Repeat(secret, RetentionMutationIdentifierFormats.SecretByteLength).ToArray());

        internal RetentionConfirmationBindingRequest BindingRequest(string token) => new(
            ConfirmationId((byte)token[RetentionMutationIdentifierFormats.ConfirmationTokenPrefix.Length]),
            PreviewId,
            token,
            new(RetentionMutationTargetKind.Item, "item-a"),
            RetentionMutationOperation.Pin,
            RetentionMutationScope.SingleItem,
            "sha256-" + new string('1', 64),
            "v1-" + new string('2', 64),
            "sha256-" + new string('3', 64),
            "{}",
            "v1-" + new string('4', 64),
            RetentionMutationIdentifiers.GenerateWorkflowKey(),
            RetentionMutationReasonCodes.ResearchNeeded,
            "normalized comment",
            "op-1");

        private void InsertPreview()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO retention_mutation_previews(
                    preview_id,schema_version,target_kind,target_id,operation,scope,preview_json,expected_state_version,
                    target_item_set_digest,preview_digest,created_at,expires_at,rejection_code)
                VALUES($preview,1,'item','item-a','pin','single_item','{}',$expected,$target,$digest,$created,$expires,NULL);
                """;
            command.Parameters.AddWithValue("$preview", PreviewId);
            command.Parameters.AddWithValue("$expected", "v1-" + new string('2', 64));
            command.Parameters.AddWithValue("$target", "sha256-" + new string('3', 64));
            command.Parameters.AddWithValue("$digest", "sha256-" + new string('5', 64));
            command.Parameters.AddWithValue("$created", PreviewCreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$expires", PreviewCreatedAt.Add(RetentionMutationConstants.ConfirmationLifetime).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        internal long Scalar(string sql, params (string Name, object Value)[] values)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { Path, Path + "-wal", Path + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }
}
