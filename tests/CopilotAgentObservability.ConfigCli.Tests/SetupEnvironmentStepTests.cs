using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupEnvironmentStepTests
{
    [Fact]
    public void Capture_PreservesOrderedMissingEmptyAndValueStatesWithoutReadingUnrelatedNames()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("EMPTY", string.Empty);
        platform.SeedUserEnvironment("VALUE", "secret-value");
        platform.SeedUserEnvironment("UNRELATED", "keep");

        var capture = new UserEnvironmentSetupStep(platform).Capture(["MISSING", "EMPTY", "VALUE"]);

        Assert.Equal(["MISSING", "EMPTY", "VALUE"], capture.Members.Select(member => member.Name));
        Assert.False(capture.Members[0].Value.Exists);
        Assert.Equal(string.Empty, capture.Members[1].Value.Value);
        Assert.Equal("secret-value", capture.Members[2].Value.Value);
        Assert.Equal(3, capture.Members.Select(member => member.Hash).Distinct().Count());
        Assert.Matches("^[0-9a-f]{64}$", capture.AggregateHash);
        Assert.Equal(
        [
            "environment.get:MISSING",
            "environment.get:EMPTY",
            "environment.get:VALUE",
        ],
        platform.Operations);
    }

    [Fact]
    public void Capture_AggregateHashPreservesKeyOrderAndMemberIdentity()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("A", "same");
        platform.SeedUserEnvironment("B", "same");
        var step = new UserEnvironmentSetupStep(platform);

        var first = step.Capture(["A", "B"]);
        var reordered = step.Capture(["B", "A"]);

        Assert.NotEqual(first.AggregateHash, reordered.AggregateHash);
        Assert.NotEqual(first.Members[0].Hash, reordered.Members[0].Hash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A=B")]
    [InlineData("BAD\0NAME")]
    public void Capture_RejectsUnsafeKeysWithoutAnyEnvironmentRead(string name)
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);

        var exception = Assert.Throws<SetupEnvironmentStepException>(() =>
            new UserEnvironmentSetupStep(platform).Capture([name]));

        Assert.Equal(SetupCodes.InvalidArguments, exception.Code);
        Assert.Empty(platform.Operations);
        if (name.Length > 0)
        {
            Assert.DoesNotContain(name, exception.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Capture_RejectsCaseInsensitiveDuplicatesAndMoreThanThirtyTwoMembersWithoutReading()
    {
        var duplicatePlatform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        var tooManyPlatform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);

        Assert.Equal(
            SetupCodes.InvalidArguments,
            Assert.Throws<SetupEnvironmentStepException>(() =>
                new UserEnvironmentSetupStep(duplicatePlatform).Capture(["VALUE", "value"])).Code);
        Assert.Equal(
            SetupCodes.InvalidArguments,
            Assert.Throws<SetupEnvironmentStepException>(() =>
                new UserEnvironmentSetupStep(tooManyPlatform).Capture(
                    Enumerable.Range(0, 33).Select(index => $"KEY_{index}").ToArray())).Code);
        Assert.Empty(duplicatePlatform.Operations);
        Assert.Empty(tooManyPlatform.Operations);
    }

    [Fact]
    public void Backup_IsVersionedDeterministicCreateNewFlushedAndReopensExactStates()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("EMPTY", string.Empty);
        platform.SeedUserEnvironment("VALUE", "secret-value");
        var step = new UserEnvironmentSetupStep(platform);
        var capture = step.Capture(["MISSING", "EMPTY", "VALUE"]);

        step.CreateBackup("private.backup", capture);
        var reopened = new UserEnvironmentSetupStep(platform).ReadBackup(
            "private.backup", ["MISSING", "EMPTY", "VALUE"]);

        var bytes = platform.ReadSeededFile("private.backup");
        Assert.Equal("CAOENV", Encoding.ASCII.GetString(bytes, 0, 6));
        Assert.Equal(capture.AggregateHash, reopened.AggregateHash);
        Assert.Equal(capture.Members, reopened.Members);
        Assert.Contains("file.write-new:private.backup", platform.Operations);
        Assert.Contains("file.flush:private.backup", platform.Operations);
        Assert.Throws<SetupEnvironmentStepException>(() => step.CreateBackup("private.backup", capture));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadBackup_RejectsCorruptionAndUnknownVersionWithFixedNonEchoError(bool unknownVersion)
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("VALUE", "do-not-echo-this-secret");
        var step = new UserEnvironmentSetupStep(platform);
        var capture = step.Capture(["VALUE"]);
        step.CreateBackup("private.backup", capture);
        var bytes = platform.ReadSeededFile("private.backup");
        if (unknownVersion)
        {
            bytes[7] = 2;
        }
        else
        {
            bytes[^1] ^= 0xff;
        }

        platform.SeedFile("private.backup", bytes);
        var exception = Assert.Throws<SetupEnvironmentStepException>(() =>
            new UserEnvironmentSetupStep(platform).ReadBackup("private.backup", ["VALUE"]));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Equal(SetupCodes.InternalError, exception.Message);
        Assert.DoesNotContain("do-not-echo-this-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(null, "value")]
    [InlineData("", null)]
    [InlineData("", "value")]
    [InlineData("before", null)]
    [InlineData("before", "")]
    public void ApplyAndRestoreMember_TransitionsEveryStateAndRestoresExactPreviousState(
        string? previousRaw,
        string? desiredRaw)
    {
        var previous = previousRaw is null ? UserEnvironmentValue.Missing : UserEnvironmentValue.Present(previousRaw);
        var desired = desiredRaw is null ? UserEnvironmentValue.Missing : UserEnvironmentValue.Present(desiredRaw);
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        Seed(platform, "VALUE", previous);
        var step = new UserEnvironmentSetupStep(platform);
        var capture = step.Capture(["VALUE"]);
        step.CreateBackup("private.backup", capture);

        var applied = step.ApplyMember("VALUE", capture.Members[0].Hash, desired);
        var restored = step.RestoreMember(
            "VALUE", "private.backup", applied.AppliedHash, capture.Members[0].Hash);

        Assert.Equal(previous, Read(platform, "VALUE"));
        Assert.Equal(capture.Members[0].Hash, restored.RestoredHash);
        Assert.DoesNotContain("environment.notify", platform.Operations);
    }

    [Fact]
    public void ApplyMember_RechecksExpectedPriorHashImmediatelyAndStaleStatePerformsNoWrite()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("VALUE", "third-party");
        var step = new UserEnvironmentSetupStep(platform);
        var expected = step.HashMember("VALUE", UserEnvironmentValue.Present("planned"));

        var exception = Assert.Throws<SetupEnvironmentStepException>(() =>
            step.ApplyMember("VALUE", expected, UserEnvironmentValue.Present("desired")));

        Assert.Equal(SetupCodes.StalePlan, exception.Code);
        Assert.Equal("third-party", platform.ReadUserEnvironment("VALUE"));
        Assert.DoesNotContain("environment.set:VALUE", platform.Operations);
    }

    [Fact]
    public void ApplyMember_DesiredStateIsNoOpWithoutWriteOrOwnershipSignal()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("VALUE", string.Empty);
        var step = new UserEnvironmentSetupStep(platform);
        var expected = step.HashMember("VALUE", UserEnvironmentValue.Present(string.Empty));

        var result = step.ApplyMember("VALUE", expected, UserEnvironmentValue.Present(string.Empty));

        Assert.False(result.Changed);
        Assert.Equal(result.PreviousHash, result.AppliedHash);
        Assert.DoesNotContain("environment.set:VALUE", platform.Operations);
    }

    [Fact]
    public void RestoreMember_RechecksExpectedAppliedHashImmediatelyAndThirdStatePerformsNoWrite()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("VALUE", "before");
        var step = new UserEnvironmentSetupStep(platform);
        var capture = step.Capture(["VALUE"]);
        step.CreateBackup("private.backup", capture);
        var appliedHash = step.HashMember("VALUE", UserEnvironmentValue.Present("desired"));
        platform.SeedUserEnvironment("VALUE", "third-party");

        var exception = Assert.Throws<SetupEnvironmentStepException>(() =>
            step.RestoreMember("VALUE", "private.backup", appliedHash, capture.Members[0].Hash));

        Assert.Equal(SetupCodes.RollbackStale, exception.Code);
        Assert.Equal("third-party", platform.ReadUserEnvironment("VALUE"));
        Assert.DoesNotContain("environment.set:VALUE", platform.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyMember_SetFaultExposesWhetherEffectOccurredWithoutPrimitiveRollback(bool afterEffect)
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("VALUE", "before");
        var step = new UserEnvironmentSetupStep(platform);
        var previousHash = step.HashMember("VALUE", UserEnvironmentValue.Present("before"));
        if (afterEffect)
        {
            platform.InjectAfterEffectFault("environment.set:VALUE", new IOException("secret raw error"));
        }
        else
        {
            platform.InjectFault("environment.set:VALUE", new IOException("secret raw error"));
        }

        var exception = Assert.Throws<SetupEnvironmentStepException>(() =>
            step.ApplyMember("VALUE", previousHash, UserEnvironmentValue.Present("desired")));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Equal(afterEffect ? "desired" : "before", platform.ReadUserEnvironment("VALUE"));
        Assert.DoesNotContain("secret raw error", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreMember_SetFaultExposesWhetherEffectOccurredWithoutPrimitiveRollback(bool afterEffect)
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("VALUE", "before");
        var step = new UserEnvironmentSetupStep(platform);
        var capture = step.Capture(["VALUE"]);
        step.CreateBackup("private.backup", capture);
        var applied = step.ApplyMember("VALUE", capture.Members[0].Hash, UserEnvironmentValue.Present("desired"));
        if (afterEffect)
        {
            platform.InjectAfterEffectFault("environment.set:VALUE", new IOException("secret raw error"));
        }
        else
        {
            platform.InjectFault("environment.set:VALUE", new IOException("secret raw error"));
        }

        var exception = Assert.Throws<SetupEnvironmentStepException>(() =>
            step.RestoreMember("VALUE", "private.backup", applied.AppliedHash, capture.Members[0].Hash));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Equal(afterEffect ? "before" : "desired", platform.ReadUserEnvironment("VALUE"));
        Assert.DoesNotContain("secret raw error", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("A", false)]
    [InlineData("A", true)]
    [InlineData("B", false)]
    [InlineData("B", true)]
    [InlineData("C", false)]
    [InlineData("C", true)]
    public void ApplyMember_EachAllowlistedMemberHasDeterministicBeforeAndAfterFaultBoundaries(
        string faultedName,
        bool afterEffect)
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("A", string.Empty);
        platform.SeedUserEnvironment("B", "before-b");
        var step = new UserEnvironmentSetupStep(platform);
        var capture = step.Capture(["A", "B", "C"]);
        var member = capture.Members.Single(candidate => candidate.Name == faultedName);
        if (afterEffect)
        {
            platform.InjectAfterEffectFault($"environment.set:{faultedName}", new IOException("raw"));
        }
        else
        {
            platform.InjectFault($"environment.set:{faultedName}", new IOException("raw"));
        }

        Assert.Equal(
            SetupCodes.InternalError,
            Assert.Throws<SetupEnvironmentStepException>(() =>
                step.ApplyMember(faultedName, member.Hash, UserEnvironmentValue.Present("desired"))).Code);
        Assert.Equal(afterEffect ? "desired" : member.Value.Value, platform.ReadUserEnvironment(faultedName));
        Assert.DoesNotContain("environment.notify", platform.Operations);
    }

    [Theory]
    [InlineData("A", false)]
    [InlineData("A", true)]
    [InlineData("B", false)]
    [InlineData("B", true)]
    [InlineData("C", false)]
    [InlineData("C", true)]
    public void RestoreMember_EachAllowlistedMemberHasDeterministicBeforeAndAfterFaultBoundaries(
        string faultedName,
        bool afterEffect)
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("A", string.Empty);
        platform.SeedUserEnvironment("B", "before-b");
        var step = new UserEnvironmentSetupStep(platform);
        var capture = step.Capture(["A", "B", "C"]);
        step.CreateBackup("private.backup", capture);
        var member = capture.Members.Single(candidate => candidate.Name == faultedName);
        var applied = step.ApplyMember(faultedName, member.Hash, UserEnvironmentValue.Present("desired"));
        if (afterEffect)
        {
            platform.InjectAfterEffectFault($"environment.set:{faultedName}", new IOException("raw"));
        }
        else
        {
            platform.InjectFault($"environment.set:{faultedName}", new IOException("raw"));
        }

        Assert.Equal(
            SetupCodes.InternalError,
            Assert.Throws<SetupEnvironmentStepException>(() =>
                step.RestoreMember(faultedName, "private.backup", applied.AppliedHash, member.Hash)).Code);
        Assert.Equal(afterEffect ? member.Value.Value : "desired", platform.ReadUserEnvironment(faultedName));
        Assert.DoesNotContain("environment.notify", platform.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NotifyFinalState_AttemptsExactlyOnceOnlyWhenExplicitAndFailureCanBeReplayed(bool afterEffect)
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        var step = new UserEnvironmentSetupStep(platform);
        if (afterEffect)
        {
            platform.InjectAfterEffectFault("environment.notify", new IOException("raw notification error"));
        }
        else
        {
            platform.InjectFault("environment.notify", new IOException("raw notification error"));
        }

        var exception = Assert.Throws<SetupEnvironmentStepException>(() => step.NotifyFinalState());
        step.NotifyFinalState();

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.DoesNotContain("raw notification error", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, platform.Operations.Count(operation => operation == "environment.notify"));
    }

    [Fact]
    public void AggregateOperationOrderIsCaptureBackupMemberWritesThenOneExplicitNotification()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        var step = new UserEnvironmentSetupStep(platform);
        var capture = step.Capture(["A", "B", "C"]);
        step.CreateBackup("private.backup", capture);
        foreach (var member in capture.Members)
        {
            step.ApplyMember(member.Name, member.Hash, UserEnvironmentValue.Present(member.Name));
        }

        step.NotifyFinalState();

        Assert.Equal(
        [
            "environment.get:A",
            "environment.get:B",
            "environment.get:C",
            "file.write-new:private.backup",
            "file.flush:private.backup",
            "environment.get:A",
            "environment.set:A",
            "environment.get:B",
            "environment.set:B",
            "environment.get:C",
            "environment.set:C",
            "environment.notify",
        ],
        platform.Operations);
    }

    [Fact]
    public void MemberOperations_ReadImmediatelyBeforeWriteAndNeverNotifyImplicitly()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        var step = new UserEnvironmentSetupStep(platform);
        var missingHash = step.HashMember("VALUE", UserEnvironmentValue.Missing);

        step.ApplyMember("VALUE", missingHash, UserEnvironmentValue.Present("desired"));

        Assert.Equal(
        [
            "environment.get:VALUE",
            "environment.set:VALUE",
        ],
        platform.Operations);
    }

    private static void Seed(SetupTestPlatform platform, string name, UserEnvironmentValue value)
    {
        if (value.Exists)
        {
            platform.SeedUserEnvironment(name, value.Value);
        }
    }

    private static UserEnvironmentValue Read(SetupTestPlatform platform, string name)
    {
        var value = platform.ReadUserEnvironment(name);
        return value is null ? UserEnvironmentValue.Missing : UserEnvironmentValue.Present(value);
    }
}
