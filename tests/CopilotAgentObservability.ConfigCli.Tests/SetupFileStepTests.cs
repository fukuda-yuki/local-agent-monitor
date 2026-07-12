using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupFileStepTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Hash_DistinguishesMissingEmptyAndContentAndUsesLowercaseSha256()
    {
        var missing = SetupHash.File(exists: false, []);
        var empty = SetupHash.File(exists: true, []);
        var content = SetupHash.File(exists: true, [0xef, 0xbb, 0xbf, 0x61]);

        Assert.NotEqual(missing, empty);
        Assert.NotEqual(empty, content);
        Assert.All([missing, empty, content], value => Assert.Matches("^[0-9a-f]{64}$", value));
        Assert.Equal(content, SetupHash.File(exists: true, [0xef, 0xbb, 0xbf, 0x61]));
    }

    [Theory]
    [InlineData(SetupPathStyle.Windows, "settings.json")]
    [InlineData(SetupPathStyle.Windows, "C:\\allowed\\..\\settings.json")]
    [InlineData(SetupPathStyle.Windows, "C:\\allowed")]
    [InlineData(SetupPathStyle.Windows, "C:\\outside\\settings.json")]
    [InlineData(SetupPathStyle.Windows, "C:\\allowed\\settings.json:stream")]
    [InlineData(SetupPathStyle.Windows, "C:\\allowed\\settings.json.")]
    [InlineData(SetupPathStyle.Windows, "C:\\allowed\\settings.json ")]
    [InlineData(SetupPathStyle.Windows, "C:\\allowed\\NUL")]
    [InlineData(SetupPathStyle.Windows, "C:\\allowed\\COM1.txt")]
    [InlineData(SetupPathStyle.Windows, "\\\\server\\share\\settings.json")]
    [InlineData(SetupPathStyle.Windows, "\\\\?\\C:\\allowed\\settings.json")]
    [InlineData(SetupPathStyle.Windows, "file:///C:/allowed/settings.json")]
    [InlineData(SetupPathStyle.Unix, "settings.json")]
    [InlineData(SetupPathStyle.Unix, "/allowed/../settings.json")]
    [InlineData(SetupPathStyle.Unix, "/allowed")]
    [InlineData(SetupPathStyle.Unix, "/outside/settings.json")]
    [InlineData(SetupPathStyle.Unix, "file:///allowed/settings.json")]
    public void PathPolicy_RejectsUnsafeLexicalTargetsWithFixedError(SetupPathStyle style, string target)
    {
        var root = style == SetupPathStyle.Windows ? "C:\\allowed" : "/allowed";
        var platform = CreatePlatform(style, root);

        var exception = Assert.Throws<SetupFileStepException>(() => SetupPathPolicy.ValidateFileTarget(platform, root, target));

        Assert.Equal(SetupCodes.UnsafePath, exception.Code);
        Assert.Equal(SetupCodes.UnsafePath, exception.Message);
        Assert.DoesNotContain(target, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SetupPathStyle.Windows, "C:\\allowed", "c:\\ALLOWED\\nested\\settings.json")]
    [InlineData(SetupPathStyle.Unix, "/allowed", "/allowed/nested/settings.json")]
    public void PathPolicy_AcceptsAbsoluteRegularFileStrictlyBelowRoot(SetupPathStyle style, string root, string target)
    {
        var platform = CreatePlatform(style, root);
        var nested = style == SetupPathStyle.Windows ? "C:\\allowed\\nested" : "/allowed/nested";
        platform.SeedDirectory(nested);
        platform.SeedFile(target, [1]);

        var canonical = SetupPathPolicy.ValidateFileTarget(platform, root, target);

        Assert.Equal(target, canonical, style == SetupPathStyle.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(FileAttributes.ReparsePoint)]
    [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint)]
    public void PathPolicy_RejectsReparsePointAtEveryExistingComponent(FileAttributes attributes)
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        platform.SeedPathMetadata("C:\\allowed\\linked", new SetupPathMetadata(true, SetupPathKind.Directory, attributes));
        platform.SeedFile("C:\\allowed\\linked\\settings.json", [1]);

        var exception = Assert.Throws<SetupFileStepException>(() =>
            SetupPathPolicy.ValidateFileTarget(platform, "C:\\allowed", "C:\\allowed\\linked\\settings.json"));

        Assert.Equal(SetupCodes.UnsafePath, exception.Code);
    }

    [Fact]
    public void PathPolicy_RejectsMissingParentAndNonRegularTarget()
    {
        var platform = CreatePlatform(SetupPathStyle.Unix, "/allowed");
        Assert.Equal(
            SetupCodes.UnsafePath,
            Assert.Throws<SetupFileStepException>(() => SetupPathPolicy.ValidateFileTarget(platform, "/allowed", "/allowed/missing/file")).Code);

        platform.SeedPathMetadata("/allowed/device", new SetupPathMetadata(true, SetupPathKind.Other, FileAttributes.Normal));
        Assert.Equal(
            SetupCodes.UnsafePath,
            Assert.Throws<SetupFileStepException>(() => SetupPathPolicy.ValidateFileTarget(platform, "/allowed", "/allowed/device")).Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Apply_WritesFlushedBackupAndAtomicallyReplacesOrMovesWithoutDecodingBytes(bool existed)
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        var target = "C:\\allowed\\settings.json";
        var backup = "C:\\private\\record.backup";
        var previous = new byte[] { 0xef, 0xbb, 0xbf, 0x6f, 0x6c, 0x64 };
        var desired = new byte[] { 0xef, 0xbb, 0xbf, 0x6e, 0x65, 0x77 };
        if (existed)
        {
            platform.SeedFile(target, previous);
        }

        var step = new AtomicFileSetupStep(platform);
        var result = step.Apply(
            "C:\\allowed",
            target,
            backup,
            SetupHash.File(existed, existed ? previous : []),
            desired);

        Assert.True(result.Changed);
        Assert.Equal(SetupHash.File(existed, existed ? previous : []), result.PreviousHash);
        Assert.Equal(SetupHash.File(true, desired), result.AppliedHash);
        Assert.Equal(desired, platform.ReadSeededFile(target));
        Assert.Contains($"file.write-new:{backup}", platform.Operations);
        Assert.Contains($"file.flush:{backup}", platform.Operations);
        Assert.Contains(platform.Operations, operation => operation.StartsWith(existed ? "file.replace:" : "file.move:", StringComparison.Ordinal));
    }

    [Fact]
    public void Restore_UsesExactBackupAndDistinguishesPreviousMissingFromEmpty()
    {
        var target = "C:\\allowed\\settings.json";
        foreach (var previous in new byte[]?[] { null, [] })
        {
            var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
            var backup = previous is null ? "C:\\private\\missing.backup" : "C:\\private\\empty.backup";
            if (previous is not null)
            {
                platform.SeedFile(target, previous);
            }

            var step = new AtomicFileSetupStep(platform);
            var applied = step.Apply(
                "C:\\allowed",
                target,
                backup,
                SetupHash.File(previous is not null, previous ?? []),
                Encoding.UTF8.GetBytes("desired"));

            var restored = step.Restore(
                "C:\\allowed",
                target,
                backup,
                applied.AppliedHash,
                applied.PreviousHash);

            Assert.Equal(applied.PreviousHash, restored.RestoredHash);
            Assert.Equal(previous is not null, platform.FileSystem.FileExists(target));
            if (previous is not null)
            {
                Assert.Empty(platform.ReadSeededFile(target));
            }
        }
    }

    [Fact]
    public void Apply_RejectsStaleBaseBeforeCreatingBackupOrTemp()
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        var target = "C:\\allowed\\settings.json";
        platform.SeedFile(target, Encoding.UTF8.GetBytes("third-party"));
        var step = new AtomicFileSetupStep(platform);

        var exception = Assert.Throws<SetupFileStepException>(() => step.Apply(
            "C:\\allowed",
            target,
            "C:\\private\\record.backup",
            SetupHash.File(true, Encoding.UTF8.GetBytes("planned")),
            Encoding.UTF8.GetBytes("desired")));

        Assert.Equal(SetupCodes.StalePlan, exception.Code);
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("file.write", StringComparison.Ordinal));
        Assert.Equal("third-party", Encoding.UTF8.GetString(platform.ReadSeededFile(target)));
    }

    [Fact]
    public void Restore_RejectsStaleCurrentStateWithoutWritingOrDeleting()
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        var target = "C:\\allowed\\settings.json";
        var backup = "C:\\private\\record.backup";
        var step = new AtomicFileSetupStep(platform);
        platform.SeedFile(target, Encoding.UTF8.GetBytes("before"));
        var applied = step.Apply("C:\\allowed", target, backup, SetupHash.File(true, Encoding.UTF8.GetBytes("before")), Encoding.UTF8.GetBytes("applied"));
        platform.SeedFile(target, Encoding.UTF8.GetBytes("third-party"));
        var operationCount = platform.Operations.Count;

        var exception = Assert.Throws<SetupFileStepException>(() => step.Restore(
            "C:\\allowed", target, backup, applied.AppliedHash, applied.PreviousHash));

        Assert.Equal(SetupCodes.RollbackStale, exception.Code);
        Assert.Equal("third-party", Encoding.UTF8.GetString(platform.ReadSeededFile(target)));
        Assert.DoesNotContain(platform.Operations.Skip(operationCount), operation =>
            operation.StartsWith("file.write", StringComparison.Ordinal) || operation.StartsWith("file.delete", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("backup-write")]
    [InlineData("backup-flush")]
    [InlineData("temp-write")]
    [InlineData("temp-flush")]
    [InlineData("replace")]
    public void Apply_PreEffectFaultPreservesOldTargetAndCleansTemporaryFile(string boundary)
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        var target = "C:\\allowed\\settings.json";
        var backup = "C:\\private\\record.backup";
        var temporary = target + ".cao-00000000-0000-7000-8000-000000000001.tmp";
        var old = Encoding.UTF8.GetBytes("old");
        platform.SeedFile(target, old);
        var operation = boundary switch
        {
            "backup-write" => $"file.write-new:{backup}",
            "backup-flush" => $"file.flush:{backup}",
            "temp-write" => $"file.write-new:{temporary}",
            "temp-flush" => $"file.flush:{temporary}",
            _ => $"file.replace:{temporary}->{target}",
        };
        platform.InjectFault(operation, new IOException("raw path C:\\secret"));

        var exception = Assert.Throws<SetupFileStepException>(() => new AtomicFileSetupStep(platform).Apply(
            "C:\\allowed", target, backup, SetupHash.File(true, old), Encoding.UTF8.GetBytes("desired")));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Equal(old, platform.ReadSeededFile(target));
        Assert.False(platform.FileSystem.FileExists(temporary));
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_AfterReplaceFaultReportsAmbiguityAndPreservesAppliedTargetAndBackup()
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        var target = "C:\\allowed\\settings.json";
        var backup = "C:\\private\\record.backup";
        var temporary = target + ".cao-00000000-0000-7000-8000-000000000001.tmp";
        var old = Encoding.UTF8.GetBytes("old");
        var desired = Encoding.UTF8.GetBytes("desired");
        platform.SeedFile(target, old);
        platform.InjectAfterEffectFault($"file.replace:{temporary}->{target}", new IOException("raw"));

        var exception = Assert.Throws<SetupFileStepException>(() => new AtomicFileSetupStep(platform).Apply(
            "C:\\allowed", target, backup, SetupHash.File(true, old), desired));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Equal(desired, platform.ReadSeededFile(target));
        Assert.True(platform.FileSystem.FileExists(backup));
    }

    [Fact]
    public void Apply_RechecksBaseAfterFlushedBackupImmediatelyBeforeTemporaryWrite()
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        var target = "C:\\allowed\\settings.json";
        var backup = "C:\\private\\record.backup";
        var old = Encoding.UTF8.GetBytes("old");
        platform.SeedFile(target, old);

        new AtomicFileSetupStep(platform).Apply(
            "C:\\allowed", target, backup, SetupHash.File(true, old), Encoding.UTF8.GetBytes("desired"));

        var operations = platform.Operations.ToList();
        var backupFlush = operations.IndexOf($"file.flush:{backup}");
        var tempWrite = operations.FindIndex(operation => operation.Contains(".tmp", StringComparison.Ordinal) && operation.StartsWith("file.write-new:", StringComparison.Ordinal));
        Assert.True(backupFlush >= 0 && tempWrite > backupFlush);
        Assert.Contains(platform.Operations.Skip(backupFlush + 1).Take(tempWrite - backupFlush - 1), operation => operation == $"file.read:{target}");
    }

    [Fact]
    public void Apply_ReturnsNoChangeWithoutBackupWhenBaseAlreadyEqualsDesired()
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        var target = "C:\\allowed\\settings.json";
        var desired = Encoding.UTF8.GetBytes("desired");
        platform.SeedFile(target, desired);

        var result = new AtomicFileSetupStep(platform).Apply(
            "C:\\allowed", target, "C:\\private\\record.backup", SetupHash.File(true, desired), desired);

        Assert.False(result.Changed);
        Assert.Equal(result.PreviousHash, result.AppliedHash);
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("file.write", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("backup-write")]
    [InlineData("backup-flush")]
    [InlineData("temp-write")]
    [InlineData("temp-flush")]
    public void Apply_AfterEffectPreMutationFaultPreservesOldTarget(string boundary)
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        var target = "C:\\allowed\\settings.json";
        var backup = "C:\\private\\record.backup";
        var temporary = target + ".cao-00000000-0000-7000-8000-000000000001.tmp";
        var old = Encoding.UTF8.GetBytes("old");
        platform.SeedFile(target, old);
        var operation = boundary switch
        {
            "backup-write" => $"file.write-new:{backup}",
            "backup-flush" => $"file.flush:{backup}",
            "temp-write" => $"file.write-new:{temporary}",
            _ => $"file.flush:{temporary}",
        };
        platform.InjectAfterEffectFault(operation, new IOException("raw"));

        var exception = Assert.Throws<SetupFileStepException>(() => new AtomicFileSetupStep(platform).Apply(
            "C:\\allowed", target, backup, SetupHash.File(true, old), Encoding.UTF8.GetBytes("desired")));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Equal(old, platform.ReadSeededFile(target));
        if (boundary.StartsWith("temp", StringComparison.Ordinal))
        {
            Assert.Equal(boundary == "temp-write", platform.FileSystem.FileExists(temporary));
        }
        else
        {
            Assert.True(platform.FileSystem.FileExists(backup));
        }
    }

    [Fact]
    public void Apply_AfterMoveFaultReportsAmbiguityForPreviouslyMissingTarget()
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        var target = "C:\\allowed\\settings.json";
        var backup = "C:\\private\\record.backup";
        var temporary = target + ".cao-00000000-0000-7000-8000-000000000001.tmp";
        var desired = Encoding.UTF8.GetBytes("desired");
        platform.InjectAfterEffectFault($"file.move:{temporary}->{target}", new IOException("raw"));

        var exception = Assert.Throws<SetupFileStepException>(() => new AtomicFileSetupStep(platform).Apply(
            "C:\\allowed", target, backup, SetupHash.File(false, []), desired));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Equal(desired, platform.ReadSeededFile(target));
        Assert.True(platform.FileSystem.FileExists(backup));
    }

    [Fact]
    public void Restore_AfterDeleteFaultReportsAmbiguityWithoutRecreatingPreviouslyMissingTarget()
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        var target = "C:\\allowed\\settings.json";
        var backup = "C:\\private\\record.backup";
        var step = new AtomicFileSetupStep(platform);
        var applied = step.Apply("C:\\allowed", target, backup, SetupHash.File(false, []), Encoding.UTF8.GetBytes("desired"));
        platform.InjectAfterEffectFault($"file.delete:{target}", new IOException("raw"));

        var exception = Assert.Throws<SetupFileStepException>(() => step.Restore(
            "C:\\allowed", target, backup, applied.AppliedHash, applied.PreviousHash));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.False(platform.FileSystem.FileExists(target));
        Assert.True(platform.FileSystem.FileExists(backup));
    }

    [Theory]
    [InlineData("temp-write")]
    [InlineData("temp-flush")]
    [InlineData("replace")]
    public void Restore_PreEffectFaultPreservesAppliedTargetAndCleansTemporaryFile(string boundary)
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        var target = "C:\\allowed\\settings.json";
        var backup = "C:\\private\\record.backup";
        var restoreTemporary = target + ".cao-00000000-0000-7000-8000-000000000002.tmp";
        var before = Encoding.UTF8.GetBytes("before");
        var desired = Encoding.UTF8.GetBytes("desired");
        platform.SeedFile(target, before);
        var step = new AtomicFileSetupStep(platform);
        var applied = step.Apply("C:\\allowed", target, backup, SetupHash.File(true, before), desired);
        var operation = boundary switch
        {
            "temp-write" => $"file.write-new:{restoreTemporary}",
            "temp-flush" => $"file.flush:{restoreTemporary}",
            _ => $"file.replace:{restoreTemporary}->{target}",
        };
        platform.InjectFault(operation, new IOException("raw"));

        var exception = Assert.Throws<SetupFileStepException>(() => step.Restore(
            "C:\\allowed", target, backup, applied.AppliedHash, applied.PreviousHash));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Equal(desired, platform.ReadSeededFile(target));
        Assert.False(platform.FileSystem.FileExists(restoreTemporary));
    }

    [Theory]
    [InlineData("temp-write")]
    [InlineData("temp-flush")]
    public void Restore_AfterEffectPreReplaceFaultPreservesAppliedTargetAndOnlyCleansOwnedTemporaryFile(string boundary)
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        var target = "C:\\allowed\\settings.json";
        var backup = "C:\\private\\record.backup";
        var restoreTemporary = target + ".cao-00000000-0000-7000-8000-000000000002.tmp";
        var before = Encoding.UTF8.GetBytes("before");
        var desired = Encoding.UTF8.GetBytes("desired");
        platform.SeedFile(target, before);
        var step = new AtomicFileSetupStep(platform);
        var applied = step.Apply("C:\\allowed", target, backup, SetupHash.File(true, before), desired);
        var operation = boundary == "temp-write"
            ? $"file.write-new:{restoreTemporary}"
            : $"file.flush:{restoreTemporary}";
        platform.InjectAfterEffectFault(operation, new IOException("raw"));

        Assert.Equal(
            SetupCodes.InternalError,
            Assert.Throws<SetupFileStepException>(() => step.Restore(
                "C:\\allowed", target, backup, applied.AppliedHash, applied.PreviousHash)).Code);
        Assert.Equal(desired, platform.ReadSeededFile(target));
        Assert.Equal(boundary == "temp-write", platform.FileSystem.FileExists(restoreTemporary));
    }

    [Fact]
    public void Restore_AfterReplaceFaultReportsAmbiguityAndLeavesPreviousBytesRestored()
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        var target = "C:\\allowed\\settings.json";
        var backup = "C:\\private\\record.backup";
        var restoreTemporary = target + ".cao-00000000-0000-7000-8000-000000000002.tmp";
        var before = Encoding.UTF8.GetBytes("before");
        platform.SeedFile(target, before);
        var step = new AtomicFileSetupStep(platform);
        var applied = step.Apply("C:\\allowed", target, backup, SetupHash.File(true, before), Encoding.UTF8.GetBytes("desired"));
        platform.InjectAfterEffectFault($"file.replace:{restoreTemporary}->{target}", new IOException("raw"));

        var exception = Assert.Throws<SetupFileStepException>(() => step.Restore(
            "C:\\allowed", target, backup, applied.AppliedHash, applied.PreviousHash));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Equal(before, platform.ReadSeededFile(target));
    }

    [Fact]
    public void Restore_PreEffectDeleteFaultPreservesAppliedNewFile()
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        var target = "C:\\allowed\\settings.json";
        var backup = "C:\\private\\record.backup";
        var desired = Encoding.UTF8.GetBytes("desired");
        var step = new AtomicFileSetupStep(platform);
        var applied = step.Apply("C:\\allowed", target, backup, SetupHash.File(false, []), desired);
        platform.InjectFault($"file.delete:{target}", new IOException("raw"));

        Assert.Equal(
            SetupCodes.InternalError,
            Assert.Throws<SetupFileStepException>(() => step.Restore(
                "C:\\allowed", target, backup, applied.AppliedHash, applied.PreviousHash)).Code);
        Assert.Equal(desired, platform.ReadSeededFile(target));
    }

    [Fact]
    public void Restore_RejectsCorruptBackupAndLeavesTargetAndUnrelatedFileUntouched()
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        var target = "C:\\allowed\\settings.json";
        var unrelated = "C:\\allowed\\unrelated.json";
        var backup = "C:\\private\\record.backup";
        var unrelatedBytes = Encoding.UTF8.GetBytes("unrelated");
        platform.SeedFile(target, Encoding.UTF8.GetBytes("before"));
        platform.SeedFile(unrelated, unrelatedBytes);
        var step = new AtomicFileSetupStep(platform);
        var applied = step.Apply("C:\\allowed", target, backup, SetupHash.File(true, Encoding.UTF8.GetBytes("before")), Encoding.UTF8.GetBytes("desired"));
        platform.SeedFile(backup, Encoding.UTF8.GetBytes("corrupt"));

        var exception = Assert.Throws<SetupFileStepException>(() => step.Restore(
            "C:\\allowed", target, backup, applied.AppliedHash, applied.PreviousHash));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Equal("desired", Encoding.UTF8.GetString(platform.ReadSeededFile(target)));
        Assert.Equal(unrelatedBytes, platform.ReadSeededFile(unrelated));
    }

    [Fact]
    public void Apply_TemporaryCollisionPreservesUnrelatedCandidate()
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        var target = "C:\\allowed\\settings.json";
        var backup = "C:\\private\\record.backup";
        var temporary = target + ".cao-00000000-0000-7000-8000-000000000001.tmp";
        var previous = Encoding.UTF8.GetBytes("previous");
        var unrelated = Encoding.UTF8.GetBytes("unrelated");
        platform.SeedFile(target, previous);
        platform.SeedFile(temporary, unrelated);

        var exception = Assert.Throws<SetupFileStepException>(() => new AtomicFileSetupStep(platform).Apply(
            "C:\\allowed", target, backup, SetupHash.File(true, previous), Encoding.UTF8.GetBytes("desired")));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Equal(previous, platform.ReadSeededFile(target));
        Assert.Equal(unrelated, platform.ReadSeededFile(temporary));
    }

    [Fact]
    public void Apply_AfterEffectTemporaryCreateFaultDoesNotClaimOrDeleteCandidate()
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        var target = "C:\\allowed\\settings.json";
        var backup = "C:\\private\\record.backup";
        var temporary = target + ".cao-00000000-0000-7000-8000-000000000001.tmp";
        var previous = Encoding.UTF8.GetBytes("previous");
        var desired = Encoding.UTF8.GetBytes("desired");
        platform.SeedFile(target, previous);
        platform.InjectAfterEffectFault($"file.write-new:{temporary}", new IOException("ambiguous create"));

        Assert.Equal(
            SetupCodes.InternalError,
            Assert.Throws<SetupFileStepException>(() => new AtomicFileSetupStep(platform).Apply(
                "C:\\allowed", target, backup, SetupHash.File(true, previous), desired)).Code);
        Assert.Equal(previous, platform.ReadSeededFile(target));
        Assert.Equal(desired, platform.ReadSeededFile(temporary));
    }

    [Fact]
    public void Restore_TemporaryCollisionPreservesUnrelatedCandidate()
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        var target = "C:\\allowed\\settings.json";
        var backup = "C:\\private\\record.backup";
        var restoreTemporary = target + ".cao-00000000-0000-7000-8000-000000000002.tmp";
        var previous = Encoding.UTF8.GetBytes("previous");
        var desired = Encoding.UTF8.GetBytes("desired");
        var unrelated = Encoding.UTF8.GetBytes("unrelated");
        platform.SeedFile(target, previous);
        var step = new AtomicFileSetupStep(platform);
        var applied = step.Apply("C:\\allowed", target, backup, SetupHash.File(true, previous), desired);
        platform.SeedFile(restoreTemporary, unrelated);

        var exception = Assert.Throws<SetupFileStepException>(() => step.Restore(
            "C:\\allowed", target, backup, applied.AppliedHash, applied.PreviousHash));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Equal(desired, platform.ReadSeededFile(target));
        Assert.Equal(unrelated, platform.ReadSeededFile(restoreTemporary));
    }

    [Fact]
    public void Restore_AfterEffectTemporaryCreateFaultDoesNotClaimOrDeleteCandidate()
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        var target = "C:\\allowed\\settings.json";
        var backup = "C:\\private\\record.backup";
        var restoreTemporary = target + ".cao-00000000-0000-7000-8000-000000000002.tmp";
        var previous = Encoding.UTF8.GetBytes("previous");
        var desired = Encoding.UTF8.GetBytes("desired");
        platform.SeedFile(target, previous);
        var step = new AtomicFileSetupStep(platform);
        var applied = step.Apply("C:\\allowed", target, backup, SetupHash.File(true, previous), desired);
        platform.InjectAfterEffectFault($"file.write-new:{restoreTemporary}", new IOException("ambiguous create"));

        Assert.Equal(
            SetupCodes.InternalError,
            Assert.Throws<SetupFileStepException>(() => step.Restore(
                "C:\\allowed", target, backup, applied.AppliedHash, applied.PreviousHash)).Code);
        Assert.Equal(desired, platform.ReadSeededFile(target));
        Assert.Equal(previous, platform.ReadSeededFile(restoreTemporary));
    }

    [Fact]
    public void Apply_BackupCollisionPreservesUnrelatedCandidate()
    {
        var platform = CreatePlatform(SetupPathStyle.Windows, "C:\\allowed");
        var target = "C:\\allowed\\settings.json";
        var backup = "C:\\private\\record.backup";
        var previous = Encoding.UTF8.GetBytes("previous");
        var unrelated = Encoding.UTF8.GetBytes("unrelated");
        platform.SeedFile(target, previous);
        platform.SeedFile(backup, unrelated);

        Assert.Equal(
            SetupCodes.InternalError,
            Assert.Throws<SetupFileStepException>(() => new AtomicFileSetupStep(platform).Apply(
                "C:\\allowed", target, backup, SetupHash.File(true, previous), Encoding.UTF8.GetBytes("desired"))).Code);
        Assert.Equal(previous, platform.ReadSeededFile(target));
        Assert.Equal(unrelated, platform.ReadSeededFile(backup));
    }

    [Theory]
    [InlineData(SetupPathStyle.Windows, "C:\\", "C:\\outer", "C:\\outer\\allowed", "C:\\outer\\allowed\\settings.json")]
    [InlineData(SetupPathStyle.Unix, "/", "/outer", "/outer/allowed", "/outer/allowed/settings.json")]
    public void PathPolicy_RejectsReparseAncestorAboveAllowedRoot(
        SetupPathStyle style,
        string filesystemRoot,
        string enclosingAncestor,
        string allowedRoot,
        string target)
    {
        var platform = new SetupTestPlatform(Now, pathStyle: style);
        platform.SeedDirectory(filesystemRoot);
        platform.SeedPathMetadata(
            enclosingAncestor,
            new SetupPathMetadata(true, SetupPathKind.Directory, FileAttributes.Directory | FileAttributes.ReparsePoint));
        platform.SeedDirectory(allowedRoot);
        platform.SeedFile(target, [1]);

        var exception = Assert.Throws<SetupFileStepException>(() =>
            SetupPathPolicy.ValidateFileTarget(platform, allowedRoot, target));

        Assert.Equal(SetupCodes.UnsafePath, exception.Code);
        Assert.Contains($"file.metadata:{filesystemRoot}", platform.Operations);
        Assert.Contains($"file.metadata:{enclosingAncestor}", platform.Operations);
    }

    [Fact]
    public void PathPolicy_RejectsReparseFilesystemRoot()
    {
        var platform = new SetupTestPlatform(Now, pathStyle: SetupPathStyle.Windows);
        platform.SeedPathMetadata("C:\\", new SetupPathMetadata(true, SetupPathKind.Directory, FileAttributes.Directory | FileAttributes.ReparsePoint));
        platform.SeedDirectory("C:\\allowed");

        Assert.Equal(
            SetupCodes.UnsafePath,
            Assert.Throws<SetupFileStepException>(() =>
                SetupPathPolicy.ValidateFileTarget(platform, "C:\\allowed", "C:\\allowed\\settings.json")).Code);
    }

    [Fact]
    public void SystemPlatform_CloseReopenRestorePreservesExactBytes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cao-setup-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var target = Path.Combine(directory, "settings.bin");
            var backup = Path.Combine(directory, "record.backup");
            var previous = new byte[] { 0, 0xef, 0xbb, 0xbf, 255 };
            var desired = new byte[] { 255, 0xef, 0xbb, 0xbf, 0 };
            File.WriteAllBytes(target, previous);

            var apply = new AtomicFileSetupStep(new SystemSetupPlatform()).Apply(
                directory, target, backup, SetupHash.File(true, previous), desired);
            var restore = new AtomicFileSetupStep(new SystemSetupPlatform()).Restore(
                directory, target, backup, apply.AppliedHash, apply.PreviousHash);

            Assert.Equal(apply.PreviousHash, restore.RestoredHash);
            Assert.Equal(previous, File.ReadAllBytes(target));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SystemPlatform_RejectsRealReparseAncestorWhenSupported()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cao-setup-path-{Guid.NewGuid():N}");
        var actual = Path.Combine(directory, "actual");
        var linked = Path.Combine(directory, "linked");
        Directory.CreateDirectory(actual);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(linked, actual);
            }
            catch (Exception creationException) when (creationException is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                throw Xunit.Sdk.SkipException.ForSkip($"Cannot create directory reparse fixture: {creationException.GetType().Name}");
            }

            var target = Path.Combine(linked, "settings.json");
            var exception = Assert.Throws<SetupFileStepException>(() =>
                SetupPathPolicy.ValidateFileTarget(new SystemSetupPlatform(), directory, target));

            Assert.Equal(SetupCodes.UnsafePath, exception.Code);
        }
        finally
        {
            if (Directory.Exists(linked))
            {
                Directory.Delete(linked);
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SystemPlatform_RejectsRealReparseAncestorAboveAllowedRoot()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cao-setup-root-{Guid.NewGuid():N}");
        var actual = Path.Combine(directory, "actual");
        var actualAllowed = Path.Combine(actual, "allowed");
        var linked = Path.Combine(directory, "linked");
        Directory.CreateDirectory(actualAllowed);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(linked, actual);
            }
            catch (Exception creationException) when (creationException is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                throw Xunit.Sdk.SkipException.ForSkip($"Cannot create enclosing reparse fixture: {creationException.GetType().Name}");
            }

            var allowedRoot = Path.Combine(linked, "allowed");
            var target = Path.Combine(allowedRoot, "settings.json");
            var exception = Assert.Throws<SetupFileStepException>(() =>
                SetupPathPolicy.ValidateFileTarget(new SystemSetupPlatform(), allowedRoot, target));

            Assert.Equal(SetupCodes.UnsafePath, exception.Code);
        }
        finally
        {
            if (Directory.Exists(linked))
            {
                Directory.Delete(linked);
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    private static SetupTestPlatform CreatePlatform(SetupPathStyle style, string root)
    {
        var platform = new SetupTestPlatform(Now, pathStyle: style);
        platform.SeedDirectory(style == SetupPathStyle.Windows ? "C:\\" : "/");
        platform.SeedDirectory(root);
        return platform;
    }
}
