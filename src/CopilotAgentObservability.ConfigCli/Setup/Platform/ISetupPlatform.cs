namespace CopilotAgentObservability.ConfigCli.Setup.Platform;

public interface ISetupPlatform
{
    SetupPathStyle PathStyle { get; }

    string LocalApplicationData { get; }

    ISetupFileSystem FileSystem { get; }

    ISetupUserEnvironment UserEnvironment { get; }

    ISetupClock Clock { get; }

    ISetupIdentifierGenerator Identifiers { get; }

    ISetupExecution Execution { get; }
}

public interface ISetupFileSystem
{
    void CreateDirectory(string path);

    bool FileExists(string path);

    byte[] ReadAllBytes(string path);

    SetupBoundedFileRead ReadAtMostBytes(string path, int maximumBytes);

    void WriteAllBytes(string path, ReadOnlySpan<byte> bytes);

    void WriteNewAllBytes(string path, ReadOnlySpan<byte> bytes);

    void FlushFile(string path);

    void ReplaceFile(string sourcePath, string destinationPath);

    void MoveFile(string sourcePath, string destinationPath, bool overwrite);

    void DeleteFile(string path);

    SetupPathMetadata GetPathMetadata(string path);

    ISetupExclusiveFileLock? TryAcquireExclusiveFileLock(string path);
}

public interface ISetupExclusiveFileLock : IDisposable
{
}

public interface ISetupUserEnvironment
{
    string? Get(string name);

    void Set(string name, string? value);

    void NotifyChange();
}

public interface ISetupClock
{
    DateTimeOffset UtcNow { get; }
}

public interface ISetupIdentifierGenerator
{
    Guid CreateUuidV7();
}

public interface ISetupExecution
{
    void Checkpoint(string operation);
}

public enum SetupPathStyle
{
    Windows,
    Unix,
}

public enum SetupPathKind
{
    Missing,
    File,
    Directory,
    Other,
}

public sealed record SetupPathMetadata(bool Exists, SetupPathKind Kind, FileAttributes Attributes)
{
    public static SetupPathMetadata Missing { get; } = new(false, SetupPathKind.Missing, 0);
}

public sealed record SetupBoundedFileRead(byte[] Bytes, bool IsComplete);
