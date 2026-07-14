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

    ISetupOperatingSystem OperatingSystem { get; }

    ISetupProcessRunner ProcessRunner { get; }

    ISetupManagedSettingsSource ManagedSettings { get; }

    ISetupHttpProbe HttpProbe { get; }
}

public interface ISetupFileSystem
{
    void CreateDirectory(string path);

    bool FileExists(string path);

    byte[] ReadAllBytes(string path);

    SetupBoundedFileRead ReadAtMostBytes(string path, int maximumBytes);

    bool HasDirectories(string path);

    void WriteAllBytes(string path, ReadOnlySpan<byte> bytes);

    void WriteNewAllBytes(string path, ReadOnlySpan<byte> bytes);

    bool TryWriteNewAllBytesAndFlush(string path, ReadOnlySpan<byte> bytes);

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

public interface ISetupOperatingSystem
{
    SetupPlanningOs Current { get; }

    string ApplicationData { get; }

    string UserProfile { get; }
}

public interface ISetupProcessRunner
{
    SetupProcessObservation Run(string fileName, IReadOnlyList<string> arguments);
}

public interface ISetupManagedSettingsSource
{
    SetupManagedObservation Read(SetupManagedLocation location);
}

public interface ISetupHttpProbe
{
    SetupHttpProbeObservation Get(
        string origin,
        string path,
        int totalBudgetMilliseconds,
        int maxBodyBytes);
}

public enum SetupPlanningOs
{
    Windows,
    MacOs,
    Linux,
}

public enum SetupProcessOutcome
{
    Completed,
    NotFound,
    Failed,
    TimedOut,
}

public sealed record SetupProcessObservation(
    SetupProcessOutcome Outcome,
    int? ExitCode,
    string StandardOutput);

public enum SetupManagedLocation
{
    GitHubCopilotNativeWindowsMachinePolicy,
    GitHubCopilotNativeMacOsManagedPreferences,
    GitHubCopilotFileWindows,
    GitHubCopilotFileMacOs,
    GitHubCopilotFileLinux,
    VsCodeEnterpriseWindowsMachinePolicy,
    VsCodeEnterpriseWindowsUserPolicy,
    VsCodeEnterpriseMacOsConfigurationProfile,
    VsCodeEnterpriseLinuxPolicyFile,
}

public enum SetupManagedOutcome
{
    Present,
    Absent,
    Failed,
}

public sealed record SetupManagedObservation(
    SetupManagedOutcome Outcome,
    byte[] Bytes,
    bool IsComplete)
{
    public static SetupManagedObservation Absent { get; } =
        new(SetupManagedOutcome.Absent, [], true);

    public static SetupManagedObservation Failed { get; } =
        new(SetupManagedOutcome.Failed, [], true);
}

public enum SetupHttpProbeOutcome
{
    Response,
    Refused,
    TimedOut,
    TransportFailure,
    RedirectBlocked,
}

public sealed record SetupHttpProbeObservation(
    SetupHttpProbeOutcome Outcome,
    int? StatusCode,
    long? TrustworthyContentLength,
    byte[] Body,
    bool IsComplete)
{
    public static SetupHttpProbeObservation Refused { get; } =
        new(SetupHttpProbeOutcome.Refused, null, null, [], true);

    public static SetupHttpProbeObservation TimedOut { get; } =
        new(SetupHttpProbeOutcome.TimedOut, null, null, [], true);

    public static SetupHttpProbeObservation TransportFailure { get; } =
        new(SetupHttpProbeOutcome.TransportFailure, null, null, [], true);
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
