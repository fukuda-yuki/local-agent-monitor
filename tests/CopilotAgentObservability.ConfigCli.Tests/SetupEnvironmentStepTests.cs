using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;
using Xunit.Sdk;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupEnvironmentStepTests
{
    [Fact]
    public void PublicPrimitiveInvalidInputsAlwaysUseFixedSafeExceptionType()
    {
        var step = new UserEnvironmentSetupStep(new SetupTestPlatform(DateTimeOffset.UnixEpoch));

        AssertFixedFailure(() => step.Capture(null!), "private-null-marker");
        AssertFixedFailure(() => step.HashMember(null!, null!), "private-null-marker");
        AssertFixedFailure(() => step.CreateBackup(null!, null!), "private-null-marker");
        AssertFixedFailure(() => step.ReadBackup(null!, null!), "private-null-marker");
        AssertFixedFailure(() => step.ApplyMember(null!, null!, null!), "private-null-marker");
        AssertFixedFailure(() => step.RestoreMember(null!, null!, null!, null!), "private-null-marker");
    }

    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public void PublicKeyBoundaries_RejectMalformedUtf16WithFixedNonEchoFailure(int codeUnit)
    {
        var marker = "private-surrogate-marker-" + (char)codeUnit;
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        var step = new UserEnvironmentSetupStep(platform);

        AssertFixedFailure(() => step.Capture([marker]), marker);
        AssertFixedFailure(() => step.HashMember(marker, UserEnvironmentValue.Missing), marker);
        AssertFixedFailure(() => step.ReadBackup("private.backup", [marker]), marker);
        AssertFixedFailure(() => step.ApplyMember(marker, new string('0', 64), UserEnvironmentValue.Missing), marker);
        AssertFixedFailure(() => step.RestoreMember(marker, "private.backup", new string('0', 64), new string('0', 64)), marker);
    }

    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public void DesiredAndCapturedPrior_RejectMalformedUtf16WithFixedNonEchoFailure(int codeUnit)
    {
        var marker = "private-surrogate-marker-" + (char)codeUnit;
        var desiredPlatform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        var desiredStep = new UserEnvironmentSetupStep(desiredPlatform);
        var missingHash = desiredStep.HashMember("VALUE", UserEnvironmentValue.Missing);
        var priorPlatform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        priorPlatform.SeedUserEnvironment("VALUE", marker);

        AssertFixedFailure(
            () => desiredStep.HashMember("VALUE", UserEnvironmentValue.Present(marker)), marker);
        AssertFixedFailure(
            () => desiredStep.ApplyMember("VALUE", missingHash, UserEnvironmentValue.Present(marker)), marker);
        AssertFixedFailure(
            () => new UserEnvironmentSetupStep(priorPlatform).Capture(["VALUE"]), marker);
    }

    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public void BackupPrior_RejectsMalformedUtf16WithFixedNonEchoFailureInBothReadFlows(int codeUnit)
    {
        const string privateMarker = "private-backup-surrogate-marker";
        var original = privateMarker + "x";
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("VALUE", original);
        var step = new UserEnvironmentSetupStep(platform);
        var capture = step.Capture(["VALUE"]);
        step.CreateBackup("private.backup", capture);
        var bytes = platform.ReadSeededFile("private.backup");
        var valueOffset = 6 + 2 + 2 + 4 + 10 + 1 + 4 + (original.Length - 1) * 2;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(valueOffset, 2), (ushort)codeUnit);
        RefreshChecksum(bytes);
        platform.SeedFile("private.backup", bytes);

        AssertFixedFailure(() => step.ReadBackup("private.backup", ["VALUE"]), privateMarker);
        var hash = step.HashMember("VALUE", UserEnvironmentValue.Present(original));
        AssertFixedFailure(() => step.RestoreMember("VALUE", "private.backup", hash, hash), privateMarker);
    }

    [Theory]
    [InlineData(2 * 1024 * 1024)]
    [InlineData(2 * 1024 * 1024 + 1)]
    [InlineData(8 * 1024 * 1024)]
    public void ReadBackup_UsesBoundedReadForExactOverAndVeryLargeFiles(int length)
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedFile("private.backup", new byte[length]);

        Assert.Throws<SetupEnvironmentStepException>(() =>
            new UserEnvironmentSetupStep(platform).ReadBackup("private.backup", ["VALUE"]));

        Assert.Contains("file.read-bounded:private.backup:2097152", platform.Operations);
        Assert.DoesNotContain("file.read:private.backup", platform.Operations);
    }

    [Theory]
    [InlineData(2 * 1024 * 1024)]
    [InlineData(2 * 1024 * 1024 + 1)]
    [InlineData(8 * 1024 * 1024)]
    public void RestoreMember_UsesBoundedReadForExactOverAndVeryLargeFiles(int length)
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedFile("private.backup", new byte[length]);
        var step = new UserEnvironmentSetupStep(platform);
        var hash = step.HashMember("VALUE", UserEnvironmentValue.Missing);

        Assert.Throws<SetupEnvironmentStepException>(() =>
            step.RestoreMember("VALUE", "private.backup", hash, hash));

        Assert.Contains("file.read-bounded:private.backup:2097152", platform.Operations);
        Assert.DoesNotContain("file.read:private.backup", platform.Operations);
        Assert.DoesNotContain("environment.get:VALUE", platform.Operations);
    }

    [Fact]
    public void Capture_PreservesOrderedMissingEmptyAndValueStatesWithoutReadingUnrelatedNames()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("EMPTY", string.Empty);
        platform.SeedUserEnvironment("VALUE", "secret-value");
        platform.SeedUserEnvironment("UNRELATED", "keep");

        var capture = new UserEnvironmentSetupStep(platform).Capture(["MISSING", "EMPTY", "VALUE"]);
        var step = new UserEnvironmentSetupStep(platform);

        Assert.Equal(["MISSING", "EMPTY", "VALUE"], capture.Members.Select(member => member.Name));
        AssertCapturedState(step, capture.Members[0], UserEnvironmentValue.Missing);
        AssertCapturedState(step, capture.Members[1], UserEnvironmentValue.Present(string.Empty));
        AssertCapturedState(step, capture.Members[2], UserEnvironmentValue.Present("secret-value"));
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
        Assert.Equal(capture.Members.Count, reopened.Members.Count);
        for (var index = 0; index < capture.Members.Count; index++)
        {
            AssertCapturedState(step, reopened.Members[index], capture.Members[index].Value);
        }
        Assert.Contains("file.write-new:private.backup", platform.Operations);
        Assert.Contains("file.flush:private.backup", platform.Operations);
        Assert.Throws<SetupEnvironmentStepException>(() => step.CreateBackup("private.backup", capture));
    }

    [Fact]
    public void Backup_AcceptsExactMemberCountNameAndValueBounds()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        var names = Enumerable.Range(0, 32)
            .Select(index => index == 0 ? new string('N', 255) : $"KEY_{index}")
            .ToArray();
        platform.SeedUserEnvironment(names[0], new string('V', 32_767));
        var step = new UserEnvironmentSetupStep(platform);
        var capture = step.Capture(names);

        step.CreateBackup("private.backup", capture);
        var reopened = step.ReadBackup("private.backup", names);

        Assert.Equal(32, reopened.Members.Count);
        AssertCapturedState(step, reopened.Members[0], UserEnvironmentValue.Present(new string('V', 32_767)));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("name")]
    [InlineData("value")]
    [InlineData("truncated")]
    public void Backup_RejectsOverBoundsAndTruncationWithFixedNonEchoFailure(string mutation)
    {
        const string marker = "private-backup-marker";
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("VALUE", marker);
        var step = new UserEnvironmentSetupStep(platform);
        var capture = step.Capture(["VALUE"]);
        step.CreateBackup("private.backup", capture);
        var bytes = platform.ReadSeededFile("private.backup");
        if (mutation == "truncated")
        {
            Array.Resize(ref bytes, bytes.Length - 1);
        }
        else
        {
            switch (mutation)
            {
                case "count":
                    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8, 2), 33);
                    break;
                case "name":
                    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(10, 4), 512);
                    break;
                case "value":
                    var valueLengthOffset = 10 + 4 + 10 + 1;
                    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(valueLengthOffset, 4), 65_536);
                    break;
            }

            RefreshChecksum(bytes);
        }

        platform.SeedFile("private.backup", bytes);
        AssertFixedFailure(() => step.ReadBackup("private.backup", ["VALUE"]), marker);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("reordered")]
    [InlineData("missing")]
    public void ReadBackup_RejectsInvalidExpectedMemberListsWithoutExposingValues(string shape)
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        var step = new UserEnvironmentSetupStep(platform);
        var capture = step.Capture(["A", "B"]);
        step.CreateBackup("private.backup", capture);
        string[] expected = shape switch
        {
            "duplicate" => ["A", "A"],
            "reordered" => ["B", "A"],
            _ => ["A"],
        };

        AssertFixedFailure(() => step.ReadBackup("private.backup", expected), "private-marker-not-present");
    }

    [Theory]
    [InlineData("write", false)]
    [InlineData("write", true)]
    [InlineData("flush", false)]
    [InlineData("flush", true)]
    public void CreateBackup_FaultsPreserveExactObservablePathStateWithoutCleanup(string boundary, bool afterEffect)
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("VALUE", "private-marker");
        var step = new UserEnvironmentSetupStep(platform);
        var capture = step.Capture(["VALUE"]);
        var operation = boundary == "write" ? "file.write-new:private.backup" : "file.flush:private.backup";
        if (afterEffect)
        {
            platform.InjectAfterEffectFault(operation, new IOException("raw private failure"));
        }
        else
        {
            platform.InjectFault(operation, new IOException("raw private failure"));
        }

        var exception = Assert.Throws<SetupEnvironmentStepException>(() => step.CreateBackup("private.backup", capture));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.DoesNotContain("raw private failure", exception.ToString(), StringComparison.Ordinal);
        var expectedExists = boundary == "flush" || afterEffect;
        Assert.Equal(expectedExists, platform.FileSystem.FileExists("private.backup"));
        Assert.DoesNotContain("file.delete:private.backup", platform.Operations);
    }

    [Fact]
    public void CreateBackup_CollisionPreservesForeignBytesWithoutCleanup()
    {
        var foreign = Encoding.UTF8.GetBytes("foreign-private-marker");
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedFile("private.backup", foreign);
        var step = new UserEnvironmentSetupStep(platform);
        var capture = step.Capture(["VALUE"]);
        var expectedHash = SHA256.HashData(foreign);

        Assert.Throws<SetupEnvironmentStepException>(() => step.CreateBackup("private.backup", capture));

        Assert.Equal(expectedHash, SHA256.HashData(platform.ReadSeededFile("private.backup")));
        Assert.DoesNotContain("file.delete:private.backup", platform.Operations);
    }

    [Fact]
    public void CreateOrValidateBackup_CreatesThenReusesExactModelAArtifactWithoutWriting()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("EMPTY", string.Empty);
        platform.SeedUserEnvironment("VALUE", "private-value");
        platform.SeedUserEnvironment("UNRELATED", "outside-model-a");
        var step = new UserEnvironmentSetupStep(platform);
        var capture = step.Capture(["MISSING", "EMPTY", "VALUE"]);

        step.CreateOrValidateBackup("private.backup", capture);
        var exact = platform.ReadSeededFile("private.backup");
        var operationCount = platform.Operations.Count;
        new UserEnvironmentSetupStep(platform).CreateOrValidateBackup("private.backup", capture);

        Assert.Equal(exact, platform.ReadSeededFile("private.backup"));
        Assert.Contains("file.try-write-new-flushed:private.backup", platform.Operations.Take(operationCount));
        Assert.Equal(
        [
            "file.metadata:private.backup",
            "file.read-bounded:private.backup:2097152",
            "file.metadata:private.backup",
        ],
        platform.Operations.Skip(operationCount));
        Assert.DoesNotContain("environment.get:UNRELATED", platform.Operations);
        Assert.DoesNotContain(platform.Operations.Skip(operationCount), operation =>
            operation.StartsWith("file.write", StringComparison.Ordinal) ||
            operation.StartsWith("file.flush", StringComparison.Ordinal) ||
            operation.StartsWith("file.delete", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("reordered")]
    [InlineData("subset")]
    [InlineData("state")]
    [InlineData("member-hash")]
    [InlineData("aggregate-hash")]
    [InlineData("malformed")]
    [InlineData("oversize")]
    [InlineData("reparse")]
    [InlineData("directory")]
    [InlineData("device")]
    [InlineData("unreadable")]
    public void CreateOrValidateBackup_RejectsInexactOrUnsafeArtifactWithoutMutation(string fixture)
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("A", "first");
        platform.SeedUserEnvironment("B", "second");
        var step = new UserEnvironmentSetupStep(platform);
        var originalCapture = step.Capture(["A", "B"]);
        step.CreateBackup("private.backup", originalCapture);
        var exact = platform.ReadSeededFile("private.backup");
        var expectedPlatform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        expectedPlatform.SeedUserEnvironment("A", fixture == "state" ? "changed" : "first");
        expectedPlatform.SeedUserEnvironment("B", "second");
        var expectedStep = new UserEnvironmentSetupStep(expectedPlatform);
        var expected = fixture switch
        {
            "reordered" => expectedStep.Capture(["B", "A"]),
            "subset" => expectedStep.Capture(["A"]),
            "member-hash" => originalCapture with
            {
                Members = originalCapture.Members.Select((member, index) =>
                    index == 0 ? member with { Hash = new string('0', 64) } : member).ToArray(),
            },
            "aggregate-hash" => originalCapture with { AggregateHash = new string('0', 64) },
            _ => expectedStep.Capture(["A", "B"]),
        };
        var candidate = fixture switch
        {
            "malformed" => Encoding.UTF8.GetBytes("malformed private backup"),
            "oversize" => new byte[2 * 1024 * 1024 + 1],
            _ => exact,
        };
        platform.SeedFile("private.backup", candidate);
        if (fixture == "reparse") platform.SeedPathMetadata("private.backup", new(true, SetupPathKind.File, FileAttributes.ReparsePoint));
        if (fixture == "directory") platform.SeedPathMetadata("private.backup", new(true, SetupPathKind.Directory, FileAttributes.Directory));
        if (fixture == "device") platform.SeedPathMetadata("private.backup", new(true, SetupPathKind.Other, FileAttributes.Normal));
        if (fixture == "unreadable") platform.InjectFault("file.read-bounded:private.backup:2097152", new IOException("raw secret"));
        var operationCount = platform.Operations.Count;

        var exception = Assert.Throws<SetupEnvironmentStepException>(() =>
            step.CreateOrValidateBackup("private.backup", expected));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Equal(SetupCodes.InternalError, exception.Message);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(candidate, platform.ReadSeededFile("private.backup"));
        Assert.DoesNotContain(platform.Operations.Skip(operationCount), operation =>
            operation.StartsWith("file.write", StringComparison.Ordinal) ||
            operation.StartsWith("file.flush", StringComparison.Ordinal) ||
            operation.StartsWith("file.delete", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateOrValidateBackup_AtomicCreateFaultNeverReopensOrFlushesPath(bool afterEffect)
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("VALUE", "private-value");
        var step = new UserEnvironmentSetupStep(platform);
        var capture = step.Capture(["VALUE"]);
        const string operation = "file.try-write-new-flushed:private.backup";
        if (afterEffect)
        {
            platform.InjectAfterEffectFault(operation, new IOException("raw secret"));
        }
        else
        {
            platform.InjectFault(operation, new IOException("raw secret"));
        }

        var exception = Assert.Throws<SetupEnvironmentStepException>(() =>
            step.CreateOrValidateBackup("private.backup", capture));
        var operationIndex = platform.Operations.ToList().LastIndexOf(operation);

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(afterEffect, platform.FileSystem.FileExists("private.backup"));
        Assert.Equal(1, platform.Operations.Count(item => item == operation));
        Assert.DoesNotContain(platform.Operations.Skip(operationIndex + 1), item =>
            item == "file.metadata:private.backup" || item.StartsWith("file.read-bounded:private.backup:", StringComparison.Ordinal));
        Assert.DoesNotContain(platform.Operations, item => item is "file.flush:private.backup" or "file.write-new:private.backup");
        Assert.DoesNotContain("file.delete:private.backup", platform.Operations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateOrValidateBackup_CollisionAfterMissingProbeNeverWritesOrFlushesUnownedPath(bool exactCollision)
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("VALUE", "private-value");
        var step = new UserEnvironmentSetupStep(platform);
        var capture = step.Capture(["VALUE"]);
        step.CreateBackup("fixture.backup", capture);
        var candidate = exactCollision
            ? platform.ReadSeededFile("fixture.backup")
            : Encoding.UTF8.GetBytes("foreign private value");
        const string operation = "file.try-write-new-flushed:private.backup";
        using var barrier = platform.AddBarrier(operation);
        var task = Task.Run(() => step.CreateOrValidateBackup("private.backup", capture));
        try
        {
            await Task.Run(() => barrier.WaitUntilReached(CancellationToken.None));
            platform.SeedFile("private.backup", candidate);
            barrier.Release();
            if (exactCollision)
            {
                await task;
            }
            else
            {
                Assert.Equal(
                    SetupCodes.InternalError,
                    (await Assert.ThrowsAsync<SetupEnvironmentStepException>(() => task)).Code);
            }
        }
        finally
        {
            barrier.Release();
            try
            {
                await task;
            }
            catch (SetupEnvironmentStepException)
            {
            }
        }

        Assert.Equal(candidate, platform.ReadSeededFile("private.backup"));
        Assert.Equal(1, platform.Operations.Count(item => item == operation));
        Assert.DoesNotContain(platform.Operations, item => item is "file.flush:private.backup" or "file.write-new:private.backup");
        Assert.DoesNotContain("file.delete:private.backup", platform.Operations);
    }

    [Fact]
    public void CreateOrValidateBackup_MapsNullExpectedToFixedRedactedError()
    {
        var exception = Assert.Throws<SetupEnvironmentStepException>(() =>
            new UserEnvironmentSetupStep(new SetupTestPlatform(DateTimeOffset.UnixEpoch))
                .CreateOrValidateBackup("private-secret.backup", null!));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Equal(SetupCodes.InternalError, exception.Message);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateOrValidateBackup_RejectsMetadataRebindDuringBoundedRead()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("VALUE", "private-value");
        var step = new UserEnvironmentSetupStep(platform);
        var capture = step.Capture(["VALUE"]);
        step.CreateBackup("private.backup", capture);
        var original = platform.ReadSeededFile("private.backup");
        using var barrier = platform.AddBarrier("file.read-bounded:private.backup:2097152");
        var task = Task.Run(() => step.CreateOrValidateBackup("private.backup", capture));
        try
        {
            await Task.Run(() => barrier.WaitUntilReached(CancellationToken.None));
            platform.SeedPathMetadata(
                "private.backup",
                new(true, SetupPathKind.File, FileAttributes.ReparsePoint));
            barrier.Release();

            Assert.Equal(
                SetupCodes.InternalError,
                (await Assert.ThrowsAsync<SetupEnvironmentStepException>(() => task)).Code);
        }
        finally
        {
            barrier.Release();
            try
            {
                await task;
            }
            catch (SetupEnvironmentStepException)
            {
            }
        }

        Assert.Equal(original, platform.ReadSeededFile("private.backup"));
        Assert.DoesNotContain("file.delete:private.backup", platform.Operations);
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("file.write:private.backup", StringComparison.Ordinal));
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
    [InlineData("missing", "missing", false)]
    [InlineData("missing", "empty", true)]
    [InlineData("missing", "value", true)]
    [InlineData("empty", "missing", true)]
    [InlineData("empty", "empty", false)]
    [InlineData("empty", "value", true)]
    [InlineData("value", "missing", true)]
    [InlineData("value", "empty", true)]
    [InlineData("value", "value", false)]
    [InlineData("value", "different", true)]
    public void ApplyAndRestoreMember_TransitionsEveryStateAndRestoresExactPreviousState(
        string previousKind,
        string desiredKind,
        bool changed)
    {
        var previous = State(previousKind);
        var desired = State(desiredKind);
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        Seed(platform, "VALUE", previous);
        var step = new UserEnvironmentSetupStep(platform);
        var capture = step.Capture(["VALUE"]);
        step.CreateBackup("private.backup", capture);

        var applied = step.ApplyMember("VALUE", capture.Members[0].Hash, desired);
        Assert.Equal(changed, applied.Changed);
        Assert.Equal(changed ? 1 : 0, platform.Operations.Count(operation => operation == "environment.set:VALUE"));
        var restored = step.RestoreMember(
            "VALUE", "private.backup", applied.AppliedHash, capture.Members[0].Hash);

        AssertEnvironmentState(step, platform, "VALUE", previous);
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
        AssertEnvironmentState(step, platform, "VALUE", UserEnvironmentValue.Present("third-party"));
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
        AssertEnvironmentState(step, platform, "VALUE", UserEnvironmentValue.Present("third-party"));
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
        AssertEnvironmentState(
            step,
            platform,
            "VALUE",
            UserEnvironmentValue.Present(afterEffect ? "desired" : "before"));
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
        AssertEnvironmentState(
            step,
            platform,
            "VALUE",
            UserEnvironmentValue.Present(afterEffect ? "before" : "desired"));
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
        AssertEnvironmentState(
            step,
            platform,
            faultedName,
            afterEffect ? UserEnvironmentValue.Present("desired") : member.Value);
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
        AssertEnvironmentState(
            step,
            platform,
            faultedName,
            afterEffect ? member.Value : UserEnvironmentValue.Present("desired"));
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

    [Fact]
    public void RedactedStateHelper_FailureMessageNeverContainsPrivateValues()
    {
        const string expectedMarker = "private-expected-marker";
        const string actualMarker = "private-actual-marker";
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("VALUE", actualMarker);
        var step = new UserEnvironmentSetupStep(platform);

        var failure = Record.Exception(() => AssertEnvironmentState(
            step, platform, "VALUE", UserEnvironmentValue.Present(expectedMarker)));

        var assertionFailure = Assert.IsAssignableFrom<XunitException>(failure);
        Assert.DoesNotContain(expectedMarker, assertionFailure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(actualMarker, assertionFailure.Message, StringComparison.Ordinal);
    }

    private static void Seed(SetupTestPlatform platform, string name, UserEnvironmentValue value)
    {
        if (value.Exists)
        {
            platform.SeedUserEnvironment(name, value.Value);
        }
    }

    private static UserEnvironmentValue State(string kind) => kind switch
    {
        "missing" => UserEnvironmentValue.Missing,
        "empty" => UserEnvironmentValue.Present(string.Empty),
        "value" => UserEnvironmentValue.Present("private-value-marker"),
        "different" => UserEnvironmentValue.Present("private-different-marker"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static void AssertCapturedState(
        UserEnvironmentSetupStep step,
        UserEnvironmentMemberCapture actual,
        UserEnvironmentValue expected)
    {
        Assert.Equal(expected.Exists, actual.Value.Exists);
        Assert.Equal(step.HashMember(actual.Name, expected), actual.Hash);
    }

    private static void AssertEnvironmentState(
        UserEnvironmentSetupStep step,
        SetupTestPlatform platform,
        string name,
        UserEnvironmentValue expected)
    {
        var raw = platform.ReadUserEnvironment(name);
        var actual = raw is null ? UserEnvironmentValue.Missing : UserEnvironmentValue.Present(raw);
        Assert.Equal(expected.Exists, actual.Exists);
        Assert.Equal(step.HashMember(name, expected), step.HashMember(name, actual));
    }

    private static void AssertFixedFailure(Action action, string marker)
    {
        var exception = Assert.Throws<SetupEnvironmentStepException>(action);
        Assert.Contains(exception.Code, new[] { SetupCodes.InvalidArguments, SetupCodes.InternalError });
        Assert.Equal(exception.Code, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(marker, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(EncoderFallbackException), exception.ToString(), StringComparison.Ordinal);
    }

    private static void RefreshChecksum(byte[] bytes) =>
        SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)).CopyTo(bytes, bytes.Length - 32);
}
