namespace CopilotAgentObservability.ConfigCli.Setup.Platform;

public interface ISetupPlatform
{
    ISetupFileSystem FileSystem { get; }

    ISetupUserEnvironment UserEnvironment { get; }

    ISetupClock Clock { get; }

    ISetupIdentifierGenerator Identifiers { get; }

    ISetupExecution Execution { get; }
}

public interface ISetupFileSystem
{
    bool FileExists(string path);

    byte[] ReadAllBytes(string path);

    void WriteAllBytes(string path, ReadOnlySpan<byte> bytes);

    void FlushFile(string path);

    void ReplaceFile(string sourcePath, string destinationPath);

    void MoveFile(string sourcePath, string destinationPath, bool overwrite);

    void DeleteFile(string path);

    SetupFileMetadata GetFileMetadata(string path);
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

public sealed record SetupFileMetadata(
    bool Exists,
    long Length,
    FileAttributes Attributes,
    DateTimeOffset LastWriteTimeUtc)
{
    public static SetupFileMetadata Missing { get; } = new(false, 0, 0, DateTimeOffset.UnixEpoch);
}
