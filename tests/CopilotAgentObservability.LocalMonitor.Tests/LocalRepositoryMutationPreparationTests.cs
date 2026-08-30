using System.Reflection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Trait("ValidationLane", "Nightly")]
public sealed class LocalRepositoryMutationPreparationTests
{
    public static IEnumerable<object[]> PreparedCapabilityMutations()
    {
        foreach (var arm in new[] { "create", "rename", "set_locator", "session_action" })
            foreach (var mutation in new[] { "method", "template", "operation", "status", "semantic", "owner", "seal", "state", "wrong_arm" })
                yield return [arm, mutation];
    }

    public static IEnumerable<object[]> LocatorSemanticMutations()
    {
        foreach (var arm in new[] { "create", "set_locator" })
            foreach (var field in new[] { "CanonicalLocator", "LocatorSha256", "DisplayOwner", "DisplayRepository" })
                yield return [arm, field];
    }

    [Fact]
    public void PrepareCreate_NormalizesDisplayNameAndParsesTheOptionalLocator()
    {
        using var fixture = new LocalRepositoryCatalogFixture();

        var result = fixture.Application.PrepareCreate(new("Cafe\u0301", "git@github.com:Example/Repo.git"));

        Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(result);
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_operation_receipts;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("line\nfeed")]
    public void PrepareCreate_RejectsInvalidDisplayNames(string displayName)
    {
        using var fixture = new LocalRepositoryCatalogFixture();

        var rejected = Assert.IsType<LocalRepositoryPreparationRejected<LocalRepositoryCatalogApplication.PreparedCreate>>(
            fixture.Application.PrepareCreate(new(displayName, null)));

        Assert.Equal(LocalRepositoryPreparationFailure.InvalidRequest, rejected.Failure);
    }

    [Fact]
    public void PrepareArms_ApplyCanonicalTargetRevisionActionAndNullabilitySemantics()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repositoryId = LocalRepositoryCatalogFixture.RepositoryId(0xabcd);
        var sessionId = LocalRepositoryCatalogFixture.SessionId(0xabcd);

        Assert.Equal(LocalRepositoryPreparationFailure.InvalidRepositoryTarget, Reject(fixture.Application.PrepareRename(new(repositoryId.ToUpperInvariant(), 1, "Name"))));
        Assert.Equal(LocalRepositoryPreparationFailure.InvalidRequest, Reject(fixture.Application.PrepareRename(new(repositoryId, 0, "Name"))));
        Assert.Equal(LocalRepositoryPreparationFailure.InvalidLocator, Reject(fixture.Application.PrepareSetGitHubLocator(new(repositoryId, 1, "https://example.com/a/b"))));
        Assert.Equal(LocalRepositoryPreparationFailure.InvalidSessionTarget, Reject(fixture.Application.PrepareSessionAction(new(sessionId.ToUpperInvariant(), 0, "resume_automatic", null))));
        Assert.Equal(LocalRepositoryPreparationFailure.InvalidRequest, Reject(fixture.Application.PrepareSessionAction(new(sessionId, -1, "resume_automatic", null))));
        Assert.Equal(LocalRepositoryPreparationFailure.InvalidRequest, Reject(fixture.Application.PrepareSessionAction(new(sessionId, 0, "explicitly_unassign", repositoryId))));
        Assert.Equal(LocalRepositoryPreparationFailure.InvalidRequest, Reject(fixture.Application.PrepareSessionAction(new(sessionId, 0, "assign", null))));
        Assert.Equal(LocalRepositoryPreparationFailure.InvalidRepositoryTarget, Reject(fixture.Application.PrepareSessionAction(new(sessionId, 0, "assign", repositoryId.ToUpperInvariant()))));
        Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSessionAction>>(
            fixture.Application.PrepareSessionAction(new(sessionId, 0, "assign", repositoryId)));
    }

    [Fact]
    public void PreparedCapabilities_HaveOnlyPrivateConstructorsAndReadonlyPrivateState()
    {
        var arms = new[]
        {
            typeof(LocalRepositoryCatalogApplication.PreparedCreate),
            typeof(LocalRepositoryCatalogApplication.PreparedRename),
            typeof(LocalRepositoryCatalogApplication.PreparedSetLocator),
            typeof(LocalRepositoryCatalogApplication.PreparedSessionAction),
        };

        foreach (var arm in arms)
        {
            Assert.All(arm.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), constructor => Assert.True(constructor.IsPrivate));
            Assert.All(arm.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), field =>
            {
                Assert.True(field.IsPrivate);
                Assert.True(field.IsInitOnly);
            });
            Assert.Empty(arm.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        }
    }

    [Fact]
    public void PreparedFactories_RejectArbitrarySealsAndNonexactState()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repositoryId = LocalRepositoryCatalogFixture.RepositoryId(1);
        var sessionId = LocalRepositoryCatalogFixture.SessionId(1);
        var preparedValues = new object[]
        {
            Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(
                fixture.Application.PrepareCreate(new("One", null))).Prepared,
            Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedRename>>(
                fixture.Application.PrepareRename(new(repositoryId, 1, "One"))).Prepared,
            Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSetLocator>>(
                fixture.Application.PrepareSetGitHubLocator(new(repositoryId, 1, "https://github.com/example/one"))).Prepared,
            Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSessionAction>>(
                fixture.Application.PrepareSessionAction(new(sessionId, 0, "resume_automatic", null))).Prepared,
        };
        var seal = typeof(LocalRepositoryCatalogApplication)
            .GetField("preparedSeal", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Application)!;

        foreach (var prepared in preparedValues)
        {
            var arm = prepared.GetType();
            var factory = Assert.Single(arm.GetMethods(BindingFlags.Static | BindingFlags.NonPublic), method => method.Name == "Create");
            var exactState = arm.GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(prepared)!;
            var arbitrarySeal = Assert.Throws<TargetInvocationException>(() =>
                factory.Invoke(null, [fixture.Application, new object(), exactState]));
            var nonexactState = Assert.Throws<TargetInvocationException>(() =>
                factory.Invoke(null, [fixture.Application, seal, new object()]));

            Assert.IsType<InvalidOperationException>(arbitrarySeal.InnerException);
            Assert.IsType<InvalidOperationException>(nonexactState.InnerException);
        }
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task ForeignOrNullCapability_FaultsBeforeKeyDatabaseAndWriterWork()
    {
        using var first = new LocalRepositoryCatalogFixture();
        using var second = new LocalRepositoryCatalogFixture();
        var prepared = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(
            first.Application.PrepareCreate(new("One", null))).Prepared;
        var writerCalled = false;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await second.Application.ExecutePreparedAsync(prepared, "invalid", _ =>
            {
                writerCalled = true;
                return "{}"u8.ToArray();
            }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await first.Application.ExecutePreparedAsync((LocalRepositoryCatalogApplication.PreparedCreate)null!, "invalid", _ =>
            {
                writerCalled = true;
                return "{}"u8.ToArray();
            }, CancellationToken.None));

        Assert.False(writerCalled);
        Assert.Equal(0, first.ScalarLong("SELECT COUNT(*) FROM local_repository_operation_receipts;"));
        Assert.Equal(0, second.ScalarLong("SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task ExecutePrepared_DefensivelyRejectsAnInvalidOperationKeyBeforeWriterOrDatabaseMutation()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var prepared = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(
            fixture.Application.PrepareCreate(new("One", null))).Prepared;
        var writerCalled = false;

        var rejected = Assert.IsType<LocalRepositoryMutationRejected>(await fixture.Application.ExecutePreparedAsync(
            prepared,
            "invalid",
            _ =>
            {
                writerCalled = true;
                return "{}"u8.ToArray();
            },
            CancellationToken.None));

        Assert.Equal(LocalRepositoryMutationFailure.InvalidRequest, rejected.Failure);
        Assert.False(writerCalled);
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task MutatedPreparedState_FaultsBeforeAnInvalidOperationKey()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var prepared = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(
            fixture.Application.PrepareCreate(new("One", null))).Prepared;
        var state = typeof(LocalRepositoryCatalogApplication.PreparedCreate)
            .GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(prepared)!;
        state.GetType().GetField("<Method>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(state, "GET");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Application.ExecutePreparedAsync(
                prepared,
                "invalid",
                _ => throw new Exception("writer_called"),
                CancellationToken.None));

        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Theory]
    [MemberData(nameof(PreparedCapabilityMutations))]
    public async Task EveryPreparedArm_RejectsCapabilityAndStateMutationBeforeKeyDatabaseAndWriter(
        string arm,
        string mutation)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        using var foreign = new LocalRepositoryCatalogFixture();
        var prepared = PreparedFor(fixture.Application, arm);
        var wrapper = prepared.GetType();
        var state = wrapper.GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(prepared)!;
        switch (mutation)
        {
            case "method":
                SetBackingField(state, "Method", "GET");
                break;
            case "template":
                SetBackingField(state, "RouteTemplate", "/wrong");
                break;
            case "operation":
                SetBackingField(state, "Operation", "wrong");
                break;
            case "status":
                SetBackingField(state, "ExpectedSuccessStatus", 599);
                break;
            case "semantic":
                MutateArmSemantic(state, arm);
                break;
            case "owner":
                wrapper.GetField("owner", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(prepared, foreign.Application);
                break;
            case "seal":
                wrapper.GetField("seal", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(prepared, new object());
                break;
            case "state":
                wrapper.GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(prepared, new object());
                break;
            case "wrong_arm":
                var wrong = PreparedFor(fixture.Application, arm == "create" ? "rename" : "create");
                var wrongState = wrong.GetType().GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(wrong)!;
                wrapper.GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(prepared, wrongState);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        var writerCalled = false;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Execute(fixture.Application, arm, prepared, "invalid", () => writerCalled = true));

        Assert.False(writerCalled);
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_operation_receipts;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
    }

    [Theory]
    [MemberData(nameof(LocatorSemanticMutations))]
    public async Task ParsedLocatorMutation_FaultsBeforeKeyFingerprintDatabaseAndWriter(string arm, string field)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var prepared = PreparedFor(fixture.Application, arm);
        var state = prepared.GetType().GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(prepared)!;
        var locator = state.GetType().GetProperty("Locator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(state)!;
        var value = field switch
        {
            "CanonicalLocator" => "github.com/example/tampered",
            "LocatorSha256" => new string('0', 64),
            "DisplayOwner" => "other",
            "DisplayRepository" => "other",
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        locator.GetType().GetField($"<{field}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(locator, value);
        var writerCalled = false;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Execute(fixture.Application, arm, prepared, "invalid", () => writerCalled = true));

        Assert.False(writerCalled);
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_operation_receipts;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
    }

    private static object PreparedFor(LocalRepositoryCatalogApplication application, string arm)
    {
        var repositoryId = LocalRepositoryCatalogFixture.RepositoryId(0x156);
        var sessionId = LocalRepositoryCatalogFixture.SessionId(0x156);
        return arm switch
        {
            "create" => Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(
                application.PrepareCreate(new("One", "https://github.com/Example/One"))).Prepared,
            "rename" => Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedRename>>(
                application.PrepareRename(new(repositoryId, 1, "One"))).Prepared,
            "set_locator" => Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSetLocator>>(
                application.PrepareSetGitHubLocator(new(repositoryId, 1, "https://github.com/Example/One"))).Prepared,
            "session_action" => Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSessionAction>>(
                application.PrepareSessionAction(new(sessionId, 0, "assign", repositoryId))).Prepared,
            _ => throw new ArgumentOutOfRangeException(nameof(arm)),
        };
    }

    private static void MutateArmSemantic(object state, string arm)
    {
        switch (arm)
        {
            case "create":
                SetBackingField(state, "DisplayName", " invalid");
                break;
            case "rename":
            case "set_locator":
                SetBackingField(state, "RepositoryId", "invalid");
                break;
            case "session_action":
                SetBackingField(state, "ActionValue", "resume_automatic");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(arm));
        }
    }

    private static void SetBackingField(object target, string property, object value) =>
        target.GetType().GetField($"<{property}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static async ValueTask Execute(
        LocalRepositoryCatalogApplication application,
        string arm,
        object prepared,
        string operationKey,
        Action writer)
    {
        switch (arm)
        {
            case "create":
                _ = await application.ExecutePreparedAsync((LocalRepositoryCatalogApplication.PreparedCreate)prepared, operationKey, _ => Write(writer), CancellationToken.None);
                break;
            case "rename":
                _ = await application.ExecutePreparedAsync((LocalRepositoryCatalogApplication.PreparedRename)prepared, operationKey, _ => Write(writer), CancellationToken.None);
                break;
            case "set_locator":
                _ = await application.ExecutePreparedAsync((LocalRepositoryCatalogApplication.PreparedSetLocator)prepared, operationKey, _ => Write(writer), CancellationToken.None);
                break;
            case "session_action":
                _ = await application.ExecutePreparedAsync((LocalRepositoryCatalogApplication.PreparedSessionAction)prepared, operationKey, _ => Write(writer), CancellationToken.None);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(arm));
        }
    }

    private static ReadOnlyMemory<byte> Write(Action writer)
    {
        writer();
        return "{}"u8.ToArray();
    }

    private static LocalRepositoryPreparationFailure Reject<T>(LocalRepositoryPreparationResult<T> result) =>
        Assert.IsType<LocalRepositoryPreparationRejected<T>>(result).Failure;
}
