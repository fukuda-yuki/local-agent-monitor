using System.Text;
using CopilotAgentObservability.LocalMonitor.Repositories;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositoryIdempotencyTests
{
    [Fact]
    public async Task EveryOperationArm_PersistsTheExactNineFieldFingerprintGolden()
    {
        using var createFixture = new LocalRepositoryCatalogFixture();
        var createKey = createFixture.Key(40);
        _ = await createFixture.CreateAsync("Café", "https://github.com/Example/One.git", createKey);
        Assert.Equal("235adf71126bca64f9cc14be6ac328a731f220765b5b00b6ad8ac0719e92e41b", createFixture.ScalarText($"SELECT request_fingerprint FROM local_repository_operation_receipts WHERE operation_key='{createKey}';"));

        using var renameFixture = new LocalRepositoryCatalogFixture();
        var renameRepository = renameFixture.Repository(await renameFixture.CreateAsync("Base", null, renameFixture.Key(41)));
        Assert.Equal(LocalRepositoryCatalogFixture.RepositoryId(1001), renameRepository.RepositoryId);
        var renameKey = renameFixture.Key(42);
        _ = await renameFixture.RenameAsync(renameRepository.RepositoryId, 1, "Renamed", renameKey);
        Assert.Equal("b02f5c10b4948945c3a4d5df17d20330c6865a045d62cf26e32b75d3792fe4c3", renameFixture.ScalarText($"SELECT request_fingerprint FROM local_repository_operation_receipts WHERE operation_key='{renameKey}';"));

        using var locatorFixture = new LocalRepositoryCatalogFixture();
        var locatorRepository = locatorFixture.Repository(await locatorFixture.CreateAsync("Base", null, locatorFixture.Key(43)));
        var locatorKey = locatorFixture.Key(44);
        _ = await locatorFixture.SetLocatorAsync(locatorRepository.RepositoryId, 1, "git@github.com:Example/Two.git", locatorKey);
        Assert.Equal("f477935a99375989676f36951d5f0c760465eaecde1277bae77b3bd8de8ec665", locatorFixture.ScalarText($"SELECT request_fingerprint FROM local_repository_operation_receipts WHERE operation_key='{locatorKey}';"));

        using var actionFixture = new LocalRepositoryCatalogFixture();
        var actionRepository = actionFixture.Repository(await actionFixture.CreateAsync("Base", null, actionFixture.Key(45)));
        var sessionId = LocalRepositoryCatalogFixture.SessionId(2);
        actionFixture.CreateSession(sessionId);
        var actionKey = actionFixture.Key(46);
        _ = await actionFixture.SessionActionAsync(sessionId, 0, "assign", actionRepository.RepositoryId, actionKey);
        Assert.Equal("463eed55bc77b8c749a79ad79b22e765d6c9aba2e5af177764499bf2b6dc4da4", actionFixture.ScalarText($"SELECT request_fingerprint FROM local_repository_operation_receipts WHERE operation_key='{actionKey}';"));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("rename")]
    [InlineData("set_locator")]
    [InlineData("session_action")]
    public async Task CallbackOriginatedSqliteBusy_PropagatesAndRollsBack(string arm)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("Base", null, fixture.Key(50)));
        var sessionId = LocalRepositoryCatalogFixture.SessionId(50);
        fixture.CreateSession(sessionId);
        var key = fixture.Key(51);

        async ValueTask<LocalRepositoryMutationResult> Execute() => arm switch
        {
            "create" => await fixture.Application.ExecutePreparedAsync(
                Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(fixture.Application.PrepareCreate(new("New", null))).Prepared,
                key, _ => throw new SqliteException("callback_busy", 5), CancellationToken.None),
            "rename" => await fixture.Application.ExecutePreparedAsync(
                Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedRename>>(fixture.Application.PrepareRename(new(repository.RepositoryId, 1, "Renamed"))).Prepared,
                key, _ => throw new SqliteException("callback_busy", 5), CancellationToken.None),
            "set_locator" => await fixture.Application.ExecutePreparedAsync(
                Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSetLocator>>(fixture.Application.PrepareSetGitHubLocator(new(repository.RepositoryId, 1, "https://github.com/example/new"))).Prepared,
                key, _ => throw new SqliteException("callback_busy", 5), CancellationToken.None),
            "session_action" => await fixture.Application.ExecutePreparedAsync(
                Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSessionAction>>(fixture.Application.PrepareSessionAction(new(sessionId, 0, "assign", repository.RepositoryId))).Prepared,
                key, _ => throw new SqliteException("callback_busy", 5), CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(arm)),
        };

        var error = await Assert.ThrowsAsync<SqliteException>(async () => await Execute());

        Assert.Equal(5, error.SqliteErrorCode);
        Assert.Equal(0, fixture.ScalarLong($"SELECT COUNT(*) FROM local_repository_operation_receipts WHERE operation_key='{key}';"));
        Assert.Equal("Base", fixture.ScalarText($"SELECT display_name FROM local_repositories WHERE repository_id='{repository.RepositoryId}';"));
    }

    [Fact]
    public async Task StoreOriginatedSqliteBusy_RemainsTheTypedRetryableResult()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        using var blocker = fixture.Open();
        using var writeTransaction = blocker.BeginTransaction(deferred: false);
        var prepared = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(
            fixture.Application.PrepareCreate(new("Blocked", null))).Prepared;

        var result = await fixture.Application.ExecutePreparedAsync(
            prepared,
            fixture.Key(54),
            LocalRepositoryCatalogFixture.RepositoryEntity,
            CancellationToken.None);

        Assert.IsType<LocalRepositoryMutationBusy>(result);
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("rename")]
    [InlineData("set_locator")]
    [InlineData("session_action")]
    public async Task ReceiptInsertFailure_RollsBackAndTheSameRequestRetriesFresh(string arm)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        LocalRepositoryCatalogApplication.PreparedCreate? create = null;
        LocalRepositoryCatalogApplication.PreparedRename? rename = null;
        LocalRepositoryCatalogApplication.PreparedSetLocator? locator = null;
        LocalRepositoryCatalogApplication.PreparedSessionAction? action = null;
        string? repositoryId = null;
        string? sessionId = null;
        if (arm == "create")
        {
            create = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(
                fixture.Application.PrepareCreate(new("New", "https://github.com/example/new"))).Prepared;
        }
        else
        {
            var repository = fixture.Repository(await fixture.CreateAsync("Base", null, fixture.Key(52)));
            repositoryId = repository.RepositoryId;
            if (arm == "rename")
                rename = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedRename>>(fixture.Application.PrepareRename(new(repository.RepositoryId, 1, "Renamed"))).Prepared;
            else if (arm == "set_locator")
                locator = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSetLocator>>(fixture.Application.PrepareSetGitHubLocator(new(repository.RepositoryId, 1, "https://github.com/example/new"))).Prepared;
            else
            {
                sessionId = LocalRepositoryCatalogFixture.SessionId(52);
                fixture.CreateSession(sessionId);
                action = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSessionAction>>(fixture.Application.PrepareSessionAction(new(sessionId, 0, "assign", repository.RepositoryId))).Prepared;
            }
        }
        var key = fixture.Key(53);
        fixture.Execute("CREATE TRIGGER task7_receipt_insert_failure BEFORE INSERT ON local_repository_operation_receipts BEGIN SELECT RAISE(ABORT,'task7_receipt_insert_failure'); END;");

        async ValueTask<LocalRepositoryMutationResult> Execute() => arm switch
        {
            "create" => await fixture.Application.ExecutePreparedAsync(create!, key, LocalRepositoryCatalogFixture.RepositoryEntity, CancellationToken.None),
            "rename" => await fixture.Application.ExecutePreparedAsync(rename!, key, LocalRepositoryCatalogFixture.RepositoryEntity, CancellationToken.None),
            "set_locator" => await fixture.Application.ExecutePreparedAsync(locator!, key, LocalRepositoryCatalogFixture.RepositoryEntity, CancellationToken.None),
            "session_action" => await fixture.Application.ExecutePreparedAsync(action!, key, LocalRepositoryCatalogFixture.AssignmentEntity, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(arm)),
        };

        await Assert.ThrowsAsync<SqliteException>(async () => await Execute());
        Assert.Equal(0, fixture.ScalarLong($"SELECT COUNT(*) FROM local_repository_operation_receipts WHERE operation_key='{key}';"));
        if (arm == "create")
        {
            Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
            Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_locators;"));
            Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_history;"));
        }
        else
        {
            Assert.Equal(1, fixture.ScalarLong($"SELECT revision FROM local_repositories WHERE repository_id='{repositoryId}';"));
            Assert.Equal("Base", fixture.ScalarText($"SELECT display_name FROM local_repositories WHERE repository_id='{repositoryId}';"));
            Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_history;"));
            Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_locators;"));
            if (arm == "session_action")
            {
                Assert.Equal(0, fixture.ScalarLong($"SELECT COUNT(*) FROM session_repository_manual_overrides WHERE session_id='{sessionId}';"));
                Assert.Equal(0, fixture.ScalarLong($"SELECT COUNT(*) FROM session_repository_assignment_history WHERE session_id='{sessionId}';"));
                Assert.Equal(0, fixture.ScalarLong($"SELECT COUNT(*) FROM session_repository_assignment_revisions WHERE session_id='{sessionId}';"));
            }
        }
        fixture.Execute("DROP TRIGGER task7_receipt_insert_failure;");

        var retry = Assert.IsType<LocalRepositoryMutationSucceeded>(await Execute());
        Assert.False(retry.IsReplay);
        Assert.Equal(1, fixture.ScalarLong($"SELECT COUNT(*) FROM local_repository_operation_receipts WHERE operation_key='{key}';"));
    }

    [Fact]
    public async Task FreshCreateAndReopenedReplay_KeepExactStatusHeadersAndEntityAndBypassWriter()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var key = fixture.Key(60);
        byte[]? original = null;
        string? expectedEntity = null;
        var prepared = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(
            fixture.Application.PrepareCreate(new("One", null))).Prepared;

        var fresh = Assert.IsType<LocalRepositoryMutationSucceeded>(await fixture.Application.ExecutePreparedAsync(
            prepared, key, repository =>
            {
                original = LocalRepositoryCatalogFixture.RepositoryEntity(repository).ToArray();
                expectedEntity = Encoding.UTF8.GetString(original);
                return original;
            }, CancellationToken.None));
        original![2] = (byte)'X';
        var firstCopy = fresh.Response.CopyEntity();
        firstCopy[2] = (byte)'Y';

        var reopened = fixture.NewApplication();
        var reopenedPrepared = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(
            reopened.PrepareCreate(new("One", null))).Prepared;
        var writerCalled = false;
        var replay = Assert.IsType<LocalRepositoryMutationSucceeded>(await reopened.ExecutePreparedAsync(
            reopenedPrepared, key, _ =>
            {
                writerCalled = true;
                return "{\"wrong\":true}"u8.ToArray();
            }, CancellationToken.None));

        Assert.Equal(201, fresh.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", fresh.Response.ContentType);
        Assert.Equal("no-store", fresh.Response.CacheControl);
        Assert.False(fresh.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.False(writerCalled);
        Assert.Equal(expectedEntity, Encoding.UTF8.GetString(fresh.Response.CopyEntity()));
        Assert.Equal(fresh.Response.CopyEntity(), replay.Response.CopyEntity());
        Assert.NotSame(replay.Response.CopyEntity(), replay.Response.CopyEntity());
    }

    [Fact]
    public async Task SameKeyWithChangedFingerprint_IsAConflictWithoutASecondReceiptOrWriter()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var key = fixture.Key(61);
        _ = await fixture.CreateAsync("One", null, key);
        var changed = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(
            fixture.Application.PrepareCreate(new("Two", null))).Prepared;
        var writerCalled = false;

        var conflict = Assert.IsType<LocalRepositoryMutationRejected>(await fixture.Application.ExecutePreparedAsync(
            changed, key, _ =>
            {
                writerCalled = true;
                return "{}"u8.ToArray();
            }, CancellationToken.None));

        Assert.Equal(LocalRepositoryMutationFailure.IdempotencyConflict, conflict.Failure);
        Assert.False(writerCalled);
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("bom")]
    [InlineData("lf")]
    [InlineData("invalid_utf8")]
    [InlineData("throw")]
    public async Task InvalidOrThrowingEntityWriter_RollsBackDomainHistoryAndReceipt(string kind)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var prepared = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(
            fixture.Application.PrepareCreate(new("One", null))).Prepared;

        await Assert.ThrowsAnyAsync<Exception>(async () => await fixture.Application.ExecutePreparedAsync(
            prepared,
            fixture.Key(62),
            _ => kind switch
            {
                "empty" => Array.Empty<byte>(),
                "bom" => [0xef, 0xbb, 0xbf, 0x7b, 0x7d],
                "lf" => "{}\n"u8.ToArray(),
                "invalid_utf8" => [0xc3, 0x28],
                "throw" => throw new InvalidOperationException("writer_failed"),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            },
            CancellationToken.None));

        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_history;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task OppositeStoredStatusForCreate_FaultsBeforeFingerprintAndWriter()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var key = fixture.Key(63);
        _ = await fixture.CreateAsync("One", null, key);
        fixture.Execute("DROP TRIGGER local_repository_operation_receipts_update_rejected; PRAGMA ignore_check_constraints=ON;");
        fixture.Execute($"UPDATE local_repository_operation_receipts SET status_code=200 WHERE operation_key='{key}';");
        var prepared = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(
            fixture.Application.PrepareCreate(new("Different", null))).Prepared;
        var writerCalled = false;

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Application.ExecutePreparedAsync(
            prepared, key, _ =>
            {
                writerCalled = true;
                return "{}"u8.ToArray();
            }, CancellationToken.None));

        Assert.False(writerCalled);
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
    }

    [Theory]
    [InlineData("content_type")]
    [InlineData("cache_control")]
    [InlineData("empty")]
    [InlineData("bom")]
    [InlineData("lf")]
    [InlineData("invalid_utf8")]
    public async Task InvalidStoredEnvelope_FaultsBeforeFingerprintAndWriter(string corruption)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var key = fixture.Key(64);
        _ = await fixture.CreateAsync("One", null, key);
        var assignment = corruption switch
        {
            "content_type" => "content_type='application/json'",
            "cache_control" => "cache_control='public'",
            "empty" => "response_entity=X''",
            "bom" => "response_entity=X'EFBBBF7B7D'",
            "lf" => "response_entity=X'7B7D0A'",
            "invalid_utf8" => "response_entity=X'C328'",
            _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
        };
        fixture.Execute($"DROP TRIGGER local_repository_operation_receipts_update_rejected; PRAGMA ignore_check_constraints=ON; UPDATE local_repository_operation_receipts SET {assignment} WHERE operation_key='{key}';");
        var changed = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(
            fixture.Application.PrepareCreate(new("Different", null))).Prepared;
        var writerCalled = false;

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Application.ExecutePreparedAsync(
            changed,
            key,
            _ =>
            {
                writerCalled = true;
                return "{}"u8.ToArray();
            },
            CancellationToken.None));

        Assert.False(writerCalled);
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task CorruptStoredEnvelope_IsValidatedBeforeAnUnreadableFingerprint()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var key = fixture.Key(65);
        _ = await fixture.CreateAsync("One", null, key);
        fixture.Execute("DROP TRIGGER local_repository_operation_receipts_update_rejected; PRAGMA ignore_check_constraints=ON;");
        fixture.ExecuteUnchecked($"UPDATE local_repository_operation_receipts SET status_code=200,request_fingerprint=X'00' WHERE operation_key='{key}';");
        var prepared = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(
            fixture.Application.PrepareCreate(new("Changed", null))).Prepared;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Application.ExecutePreparedAsync(prepared, key, _ => throw new Exception("writer_called"), CancellationToken.None));

        Assert.Equal("local_repository_receipt_envelope_corrupt", error.Message);
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("set_locator")]
    [InlineData("session_action")]
    public async Task OppositeStoredStatusForEvery200Arm_FaultsBeforeWriter(string arm)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var create = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(70)));
        var key = fixture.Key(71);
        LocalRepositoryMutationResult succeeded;
        Func<ValueTask<LocalRepositoryMutationResult>> replay;
        switch (arm)
        {
            case "rename":
                succeeded = await fixture.RenameAsync(create.RepositoryId, 1, "Two", key);
                replay = async () => await fixture.RenameAsync(create.RepositoryId, 1, "Two", key);
                break;
            case "set_locator":
                succeeded = await fixture.SetLocatorAsync(create.RepositoryId, 1, "https://github.com/example/two", key);
                replay = async () => await fixture.SetLocatorAsync(create.RepositoryId, 1, "https://github.com/example/two", key);
                break;
            case "session_action":
                var sessionId = LocalRepositoryCatalogFixture.SessionId(70);
                fixture.CreateSession(sessionId);
                var prepared = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSessionAction>>(
                    fixture.Application.PrepareSessionAction(new(sessionId, 0, "assign", create.RepositoryId))).Prepared;
                succeeded = await fixture.Application.ExecutePreparedAsync(prepared, key, LocalRepositoryCatalogFixture.AssignmentEntity, CancellationToken.None);
                replay = async () =>
                {
                    var replayPrepared = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSessionAction>>(
                        fixture.Application.PrepareSessionAction(new(sessionId, 0, "assign", create.RepositoryId))).Prepared;
                    return await fixture.Application.ExecutePreparedAsync(replayPrepared, key, _ => throw new InvalidOperationException("writer_called"), CancellationToken.None);
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(arm));
        }
        Assert.Equal(200, Assert.IsType<LocalRepositoryMutationSucceeded>(succeeded).Response.StatusCode);
        fixture.Execute("DROP TRIGGER local_repository_operation_receipts_update_rejected; PRAGMA ignore_check_constraints=ON;");
        fixture.Execute($"UPDATE local_repository_operation_receipts SET status_code=201 WHERE operation_key='{key}';");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await replay());
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("set_locator")]
    [InlineData("session_action")]
    public async Task CanonicalOther200Kind_FaultsBeforeFingerprintAndWriterForEveryArm(string arm)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(75)));
        var key = fixture.Key(76);
        var writerCalled = false;
        Func<ValueTask<LocalRepositoryMutationResult>> replay;
        byte[] otherKind;

        switch (arm)
        {
            case "rename":
                _ = await fixture.RenameAsync(repository.RepositoryId, 1, "Two", key);
                var rename = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedRename>>(
                    fixture.Application.PrepareRename(new(repository.RepositoryId, 1, "Two"))).Prepared;
                replay = () => fixture.Application.ExecutePreparedAsync(
                    rename,
                    key,
                    value =>
                    {
                        writerCalled = true;
                        return LocalRepositoryCatalogFixture.RepositoryEntity(value);
                    },
                    CancellationToken.None);
                otherKind = LocalRepositoryJson.WriteAssignment(new LocalRepositoryAssignmentSnapshot(
                    LocalRepositoryCatalogFixture.SessionId(75), 0, "unassigned", "none", null, [], null)).ToArray();
                break;
            case "set_locator":
                _ = await fixture.SetLocatorAsync(repository.RepositoryId, 1, "https://github.com/example/two", key);
                var locator = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSetLocator>>(
                    fixture.Application.PrepareSetGitHubLocator(new(repository.RepositoryId, 1, "https://github.com/example/two"))).Prepared;
                replay = () => fixture.Application.ExecutePreparedAsync(
                    locator,
                    key,
                    value =>
                    {
                        writerCalled = true;
                        return LocalRepositoryCatalogFixture.RepositoryEntity(value);
                    },
                    CancellationToken.None);
                otherKind = LocalRepositoryJson.WriteAssignment(new LocalRepositoryAssignmentSnapshot(
                    LocalRepositoryCatalogFixture.SessionId(76), 0, "unassigned", "none", null, [], null)).ToArray();
                break;
            case "session_action":
                var sessionId = LocalRepositoryCatalogFixture.SessionId(77);
                fixture.CreateSession(sessionId);
                var action = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSessionAction>>(
                    fixture.Application.PrepareSessionAction(new(sessionId, 0, "assign", repository.RepositoryId))).Prepared;
                _ = await fixture.Application.ExecutePreparedAsync(
                    action, key, LocalRepositoryCatalogFixture.AssignmentEntity, CancellationToken.None);
                var replayAction = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSessionAction>>(
                    fixture.Application.PrepareSessionAction(new(sessionId, 0, "assign", repository.RepositoryId))).Prepared;
                replay = () => fixture.Application.ExecutePreparedAsync(
                    replayAction,
                    key,
                    value =>
                    {
                        writerCalled = true;
                        return LocalRepositoryCatalogFixture.AssignmentEntity(value);
                    },
                    CancellationToken.None);
                otherKind = LocalRepositoryJson.WriteRepository(200, repository).ToArray();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(arm));
        }

        fixture.Execute("DROP TRIGGER local_repository_operation_receipts_update_rejected; PRAGMA ignore_check_constraints=ON;");
        fixture.ExecuteUnchecked($"UPDATE local_repository_operation_receipts SET response_entity=X'{Convert.ToHexString(otherKind)}',request_fingerprint=X'00' WHERE operation_key='{key}';");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await replay());

        Assert.Equal("local_repository_success_entity_invalid", error.Message);
        Assert.False(writerCalled);
    }

    [Fact]
    public async Task RenameNoOp_PersistsA200ReceiptAndReplaysAfterTheTargetRevisionMoves()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(80)));
        var key = fixture.Key(81);
        var noOp = await fixture.RenameAsync(repository.RepositoryId, 1, "One", key);
        _ = await fixture.RenameAsync(repository.RepositoryId, 1, "Two", fixture.Key(82));

        var replay = Assert.IsType<LocalRepositoryMutationSucceeded>(await fixture.RenameAsync(repository.RepositoryId, 1, "One", key));

        Assert.Equal(200, Assert.IsType<LocalRepositoryMutationSucceeded>(noOp).Response.StatusCode);
        Assert.True(replay.IsReplay);
        Assert.Equal(2, fixture.ScalarLong("SELECT revision FROM local_repositories;"));
        Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_history;"));
    }
}
