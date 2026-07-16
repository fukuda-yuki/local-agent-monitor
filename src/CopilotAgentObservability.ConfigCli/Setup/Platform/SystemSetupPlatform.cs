using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.ConfigCli.Setup.Platform;

public sealed class SystemSetupPlatform : ISetupPlatform
{
    public SystemSetupPlatform(
        ISetupExecution? execution = null,
        Func<bool>? notificationAttempt = null,
        string? localApplicationData = null,
        Func<string, FileStream>? exclusiveFileLockAttempt = null,
        Func<string, EnvironmentVariableTarget, string?>? userEnvironmentRead = null,
        Action<string, string?, EnvironmentVariableTarget>? userEnvironmentWrite = null,
        Func<string, EnvironmentVariableTarget, string?>? processEnvironmentRead = null)
    {
        LocalApplicationData = localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        FileSystem = new SystemSetupFileSystem(exclusiveFileLockAttempt);
        UserEnvironment = new SystemSetupUserEnvironment(notificationAttempt, userEnvironmentRead, userEnvironmentWrite);
        ProcessEnvironment = new SystemSetupProcessEnvironment(processEnvironmentRead);
        Clock = new SystemSetupClock();
        Identifiers = new SystemSetupIdentifierGenerator();
        Execution = execution ?? NoOpSetupExecution.Instance;
        OperatingSystem = new SystemSetupOperatingSystem();
        ProcessRunner = new SystemSetupProcessRunner();
        ManagedSettings = new SystemSetupManagedSettingsSource(windowsProgramFiles: null, readHook: null);
        HttpProbe = SystemSetupHttpProbe.Instance;
    }

    public string LocalApplicationData { get; }

    public SetupPathStyle PathStyle => System.OperatingSystem.IsWindows() ? SetupPathStyle.Windows : SetupPathStyle.Unix;

    public ISetupFileSystem FileSystem { get; }

    public ISetupUserEnvironment UserEnvironment { get; }

    public ISetupProcessEnvironment ProcessEnvironment { get; }

    public ISetupClock Clock { get; }

    public ISetupIdentifierGenerator Identifiers { get; }

    public ISetupExecution Execution { get; }

    public ISetupOperatingSystem OperatingSystem { get; }

    public ISetupProcessRunner ProcessRunner { get; }

    public ISetupManagedSettingsSource ManagedSettings { get; }

    public ISetupHttpProbe HttpProbe { get; }

    internal static SystemSetupHttpProbe CreateHttpProbe(IWebProxy proxy) =>
        SystemSetupHttpProbe.Create(proxy);

    internal static ISetupManagedSettingsSource CreateManagedSettingsSource(
        string windowsProgramFiles,
        Action<ManagedFileReadStage>? readHook = null) =>
        new SystemSetupManagedSettingsSource(windowsProgramFiles, readHook);

    internal static bool IsWindowsManagedRegularFile(FileAttributes attributes, uint fileType) =>
        (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 && fileType == 1;

    internal static bool IsUnixManagedRegularFile(uint mode) => (mode & 0xf000) == 0x8000;

    internal static bool TryClassifyMacOsManagedPreferenceKey(
        bool nameWasRead,
        string name,
        string? prefix,
        out bool shouldRead)
    {
        shouldRead = nameWasRead &&
            (prefix is null || name.StartsWith(prefix, StringComparison.Ordinal));
        return nameWasRead;
    }

    [SupportedOSPlatform("windows")]
    internal static SetupManagedObservation ReadBoundedRegistryValues(
        int valueCount,
        Func<string[]> readNames,
        Func<string, RegistryValueKind> readKind,
        Func<string, RegistryValueOptions, object?> readValue,
        string? valuePrefix) =>
        SystemSetupManagedSettingsSource.ReadBoundedRegistryValues(
            valueCount,
            readNames,
            readKind,
            readValue,
            valuePrefix);

    [SupportedOSPlatform("windows")]
    internal static SetupManagedObservation ReadBoundedClaudeRegistrySettings(
        Func<string, RegistryValueKind> readKind,
        Func<string, RegistryValueOptions, object?> readValue) =>
        SystemSetupManagedSettingsSource.ReadBoundedClaudeRegistrySettings(readKind, readValue);

    internal enum ManagedFileReadStage
    {
        AncestorsOpened,
        FileOpened,
    }

    internal static bool IsExclusiveFileLockContention(IOException exception)
    {
        const int WindowsSharingViolation = unchecked((int)0x80070020);
        const int WindowsLockViolation = unchecked((int)0x80070021);
        const int LinuxEagain = 11;
        const int MacOsEagain = 35;

        if (System.OperatingSystem.IsWindows())
        {
            return exception.HResult is WindowsSharingViolation or WindowsLockViolation;
        }

        if (System.OperatingSystem.IsLinux())
        {
            return exception.HResult == LinuxEagain;
        }

        return System.OperatingSystem.IsMacOS() && exception.HResult == MacOsEagain;
    }

    private sealed class SystemSetupFileSystem : ISetupFileSystem
    {
        private readonly Func<string, FileStream> exclusiveFileLockAttempt;

        public SystemSetupFileSystem(Func<string, FileStream>? exclusiveFileLockAttempt)
        {
            this.exclusiveFileLockAttempt = exclusiveFileLockAttempt ?? OpenExclusiveFileLock;
        }

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public bool FileExists(string path) => File.Exists(path);

        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

        public SetupBoundedFileRead ReadAtMostBytes(string path, int maximumBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
            if (maximumBytes == int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[maximumBytes + 1];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = stream.Read(buffer, length, buffer.Length - length);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            return new SetupBoundedFileRead(buffer.AsSpan(0, length).ToArray(), length <= maximumBytes);
        }

        public bool HasDirectories(string path)
        {
            using var enumerator = Directory.EnumerateDirectories(path).GetEnumerator();
            return enumerator.MoveNext();
        }

        public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes) => File.WriteAllBytes(path, bytes);

        public void WriteNewAllBytes(string path, ReadOnlySpan<byte> bytes)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes);
        }

        public bool TryWriteNewAllBytesAndFlush(string path, ReadOnlySpan<byte> bytes)
        {
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (PathEntryExists(path))
            {
                return false;
            }

            using (stream)
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            return true;
        }

        public void FlushFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            stream.Flush(flushToDisk: true);
        }

        public void ReplaceFile(string sourcePath, string destinationPath) => File.Replace(sourcePath, destinationPath, destinationBackupFileName: null);

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite) => File.Move(sourcePath, destinationPath, overwrite);

        public void DeleteFile(string path) => File.Delete(path);

        public SetupPathMetadata GetPathMetadata(string path)
        {
            try
            {
                if (System.OperatingSystem.IsWindows())
                {
                    return WindowsPathMetadata.Read(path);
                }

                if (System.OperatingSystem.IsLinux())
                {
                    return LinuxPathMetadata.Read(path);
                }

                if (System.OperatingSystem.IsMacOS())
                {
                    return MacOsPathMetadata.Read(path);
                }
            }
            catch (Exception)
            {
            }

            return ExistingOther();
        }

        private static SetupPathMetadata ExistingOther(FileAttributes attributes = 0) =>
            new(true, SetupPathKind.Other, attributes);

        private static bool PathEntryExists(string path)
        {
            try
            {
                _ = File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        private static class WindowsPathMetadata
        {
            private const uint FileReadAttributes = 0x00000080;
            private const uint FileShareRead = 0x00000001;
            private const uint FileShareWrite = 0x00000002;
            private const uint FileShareDelete = 0x00000004;
            private const uint OpenExisting = 3;
            private const uint OpenReparsePoint = 0x00200000;
            private const uint BackupSemantics = 0x02000000;
            private const int FileAttributeTagInfoClass = 9;
            private const uint FileTypeDisk = 0x0001;
            private const int ErrorFileNotFound = 2;
            private const int ErrorPathNotFound = 3;

            public static SetupPathMetadata Read(string path)
            {
                using var handle = CreateFile(
                    path,
                    FileReadAttributes,
                    FileShareRead | FileShareWrite | FileShareDelete,
                    IntPtr.Zero,
                    OpenExisting,
                    OpenReparsePoint | BackupSemantics,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    return IsMissing(Marshal.GetLastPInvokeError()) ? SetupPathMetadata.Missing : ExistingOther();
                }

                if (!GetFileInformationByHandleEx(
                    handle,
                    FileAttributeTagInfoClass,
                    out var info,
                    (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
                {
                    return ExistingOther();
                }

                var attributes = (FileAttributes)info.FileAttributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return ExistingOther(attributes);
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    return new SetupPathMetadata(true, SetupPathKind.Directory, attributes);
                }

                return GetFileType(handle) == FileTypeDisk
                    ? new SetupPathMetadata(true, SetupPathKind.File, attributes)
                    : ExistingOther(attributes);
            }

            private static bool IsMissing(int error) => error is ErrorFileNotFound or ErrorPathNotFound;

            [StructLayout(LayoutKind.Sequential)]
            private struct FileAttributeTagInfo
            {
                public uint FileAttributes;
                public uint ReparseTag;
            }

            [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern SafeFileHandle CreateFile(
                string fileName,
                uint desiredAccess,
                uint shareMode,
                IntPtr securityAttributes,
                uint creationDisposition,
                uint flagsAndAttributes,
                IntPtr templateFile);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetFileInformationByHandleEx(
                SafeFileHandle file,
                int fileInformationClass,
                out FileAttributeTagInfo fileInformation,
                uint bufferSize);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern uint GetFileType(SafeFileHandle file);
        }

        private static class LinuxPathMetadata
        {
            private const int AtFileDescriptorCurrentWorkingDirectory = -100;
            private const int AtSymlinkNoFollow = 0x100;
            private const uint StatxType = 0x00000001;
            private const int ErrorNoEntry = 2;
            private const int ErrorNotDirectory = 20;
            private const ushort FileTypeMask = 0xf000;
            private const ushort FileTypeFifo = 0x1000;
            private const ushort FileTypeCharacter = 0x2000;
            private const ushort FileTypeDirectory = 0x4000;
            private const ushort FileTypeBlock = 0x6000;
            private const ushort FileTypeRegular = 0x8000;
            private const ushort FileTypeLink = 0xa000;
            private const ushort FileTypeSocket = 0xc000;
            private const int StatxBufferSize = 256;
            private const int StatxModeOffset = 28;

            public static SetupPathMetadata Read(string path)
            {
                var buffer = new byte[StatxBufferSize];
                if (Statx(
                    AtFileDescriptorCurrentWorkingDirectory,
                    path,
                    AtSymlinkNoFollow,
                    StatxType,
                    buffer) != 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    return error is ErrorNoEntry or ErrorNotDirectory ? SetupPathMetadata.Missing : ExistingOther();
                }

                var mode = BitConverter.ToUInt16(buffer, StatxModeOffset);
                return (ushort)(mode & FileTypeMask) switch
                {
                    FileTypeRegular => new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.Normal),
                    FileTypeDirectory => new SetupPathMetadata(true, SetupPathKind.Directory, FileAttributes.Directory),
                    FileTypeLink => ExistingOther(FileAttributes.ReparsePoint),
                    FileTypeFifo or FileTypeSocket or FileTypeCharacter or FileTypeBlock => ExistingOther(),
                    _ => ExistingOther(),
                };
            }

            [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
            private static extern int Statx(
                int directoryFileDescriptor,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                int flags,
                uint mask,
                [Out] byte[] buffer);
        }

        private static class MacOsPathMetadata
        {
            private const ushort AttributeBitmapCount = 5;
            private const uint AttributeCommonObjectType = 0x00000008;
            private const ulong FileSystemOptionNoFollow = 0x00000001;
            private const int ErrorNoEntry = 2;
            private const int ErrorNotDirectory = 20;
            private const uint VnodeTypeRegular = 1;
            private const uint VnodeTypeDirectory = 2;
            private const uint VnodeTypeBlock = 3;
            private const uint VnodeTypeCharacter = 4;
            private const uint VnodeTypeLink = 5;
            private const uint VnodeTypeSocket = 6;
            private const uint VnodeTypeFifo = 7;

            public static SetupPathMetadata Read(string path)
            {
                var attributes = new AttributeList
                {
                    BitmapCount = AttributeBitmapCount,
                    CommonAttributes = AttributeCommonObjectType,
                };
                var buffer = new byte[8];
                if (GetAttributeList(path, ref attributes, buffer, (nuint)buffer.Length, FileSystemOptionNoFollow) != 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    return error is ErrorNoEntry or ErrorNotDirectory ? SetupPathMetadata.Missing : ExistingOther();
                }

                if (BitConverter.ToUInt32(buffer, 0) < buffer.Length)
                {
                    return ExistingOther();
                }

                return BitConverter.ToUInt32(buffer, 4) switch
                {
                    VnodeTypeRegular => new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.Normal),
                    VnodeTypeDirectory => new SetupPathMetadata(true, SetupPathKind.Directory, FileAttributes.Directory),
                    VnodeTypeLink => ExistingOther(FileAttributes.ReparsePoint),
                    VnodeTypeFifo or VnodeTypeSocket or VnodeTypeCharacter or VnodeTypeBlock => ExistingOther(),
                    _ => ExistingOther(),
                };
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct AttributeList
            {
                public ushort BitmapCount;
                public ushort Reserved;
                public uint CommonAttributes;
                public uint VolumeAttributes;
                public uint DirectoryAttributes;
                public uint FileAttributes;
                public uint ForkAttributes;
            }

            [DllImport("libSystem.B.dylib", EntryPoint = "getattrlist", SetLastError = true)]
            private static extern int GetAttributeList(
                [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                ref AttributeList attributes,
                [Out] byte[] buffer,
                nuint bufferSize,
                ulong options);
        }

        public ISetupExclusiveFileLock? TryAcquireExclusiveFileLock(string path)
        {
            try
            {
                return new SystemSetupExclusiveFileLock(exclusiveFileLockAttempt(path));
            }
            catch (IOException exception) when (IsExclusiveFileLockContention(exception))
            {
                return null;
            }
        }

        private static FileStream OpenExclusiveFileLock(string path) => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        private sealed class SystemSetupExclusiveFileLock(FileStream stream) : ISetupExclusiveFileLock
        {
            public void Dispose() => stream.Dispose();
        }
    }

    private sealed class SystemSetupProcessEnvironment : ISetupProcessEnvironment
    {
        private readonly Func<string, EnvironmentVariableTarget, string?> read;

        public SystemSetupProcessEnvironment(Func<string, EnvironmentVariableTarget, string?>? read)
        {
            this.read = read ?? Environment.GetEnvironmentVariable;
        }

        public string? Get(string name) => read(name, EnvironmentVariableTarget.Process);
    }

    private sealed class SystemSetupUserEnvironment : ISetupUserEnvironment
    {
        private const string NotificationFailureCode = "setup_environment_notification_failed";
        private readonly Func<bool> notificationAttempt;
        private readonly Func<string, EnvironmentVariableTarget, string?> read;
        private readonly Action<string, string?, EnvironmentVariableTarget> write;

        public SystemSetupUserEnvironment(
            Func<bool>? notificationAttempt,
            Func<string, EnvironmentVariableTarget, string?>? read,
            Action<string, string?, EnvironmentVariableTarget>? write)
        {
            this.notificationAttempt = notificationAttempt ?? TryNotifyChange;
            this.read = read ?? Environment.GetEnvironmentVariable;
            this.write = write ?? Environment.SetEnvironmentVariable;
        }

        public string? Get(string name) => read(name, EnvironmentVariableTarget.User);

        public void Set(string name, string? value) => write(name, value, EnvironmentVariableTarget.User);

        public void NotifyChange()
        {
            bool notified;
            try
            {
                notified = notificationAttempt();
            }
            catch (Exception)
            {
                throw new InvalidOperationException(NotificationFailureCode);
            }

            if (!notified)
            {
                throw new InvalidOperationException(NotificationFailureCode);
            }
        }

        private static bool TryNotifyChange() => !System.OperatingSystem.IsWindows() ||
            SendMessageTimeout(new IntPtr(0xffff), 0x001a, IntPtr.Zero, "Environment", 0x0002, 5000, out _) != IntPtr.Zero;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr windowHandle,
            uint message,
            IntPtr wordParameter,
            string textParameter,
            uint flags,
            uint timeoutMilliseconds,
            out IntPtr result);
    }

    private sealed class SystemSetupOperatingSystem : ISetupOperatingSystem
    {
        public SetupPlanningOs Current => System.OperatingSystem.IsWindows()
            ? SetupPlanningOs.Windows
            : System.OperatingSystem.IsMacOS()
                ? SetupPlanningOs.MacOs
                : System.OperatingSystem.IsLinux()
                    ? SetupPlanningOs.Linux
                    : throw new PlatformNotSupportedException("setup_operating_system_unsupported");

        public string ApplicationData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        public string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private sealed class SystemSetupProcessRunner : ISetupProcessRunner
    {
        private const int TimeoutMilliseconds = 5000;
        private const int MaximumOutputCharacters = 64 * 1024;

        public SetupProcessObservation Run(string fileName, IReadOnlyList<string> arguments)
        {
            if (string.IsNullOrWhiteSpace(fileName) || arguments is null)
            {
                return Failed();
            }

            try
            {
                return RunCoreAsync(fileName, arguments).GetAwaiter().GetResult();
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
            {
                return new SetupProcessObservation(SetupProcessOutcome.NotFound, null, string.Empty);
            }
            catch (FileNotFoundException)
            {
                return new SetupProcessObservation(SetupProcessOutcome.NotFound, null, string.Empty);
            }
            catch (DirectoryNotFoundException)
            {
                return new SetupProcessObservation(SetupProcessOutcome.NotFound, null, string.Empty);
            }
            catch (Exception)
            {
                return Failed();
            }
        }

        private static async Task<SetupProcessObservation> RunCoreAsync(
            string fileName,
            IReadOnlyList<string> arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                return Failed();
            }

            using var timeout = new CancellationTokenSource(TimeoutMilliseconds);
            var standardOutput = DrainAsync(process.StandardOutput, capture: true, timeout.Token);
            var standardError = DrainAsync(process.StandardError, capture: false, timeout.Token);
            var drained = Task.WhenAll(standardOutput, standardError);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var output = (await drained.WaitAsync(timeout.Token))[0];
                return process.ExitCode == 0
                    ? new SetupProcessObservation(SetupProcessOutcome.Completed, 0, output)
                    : new SetupProcessObservation(SetupProcessOutcome.Failed, process.ExitCode, string.Empty);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await ObserveAsync(drained);
                return new SetupProcessObservation(SetupProcessOutcome.TimedOut, null, string.Empty);
            }
        }

        private static async Task<string> DrainAsync(
            StreamReader reader,
            bool capture,
            CancellationToken cancellationToken)
        {
            var buffer = new char[4096];
            var output = capture ? new StringBuilder(MaximumOutputCharacters) : null;
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (output is not null && output.Length < MaximumOutputCharacters)
                {
                    var retained = Math.Min(read, MaximumOutputCharacters - output.Length);
                    output.Append(buffer, 0, retained);
                }
            }

            return output?.ToString() ?? string.Empty;
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    _ = process.WaitForExit(1000);
                }
            }
            catch (Exception)
            {
            }
        }

        private static async Task ObserveAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception)
            {
            }
        }

        private static SetupProcessObservation Failed() =>
            new(SetupProcessOutcome.Failed, null, string.Empty);
    }

    private sealed class SystemSetupManagedSettingsSource : ISetupManagedSettingsSource
    {
        private const int MaximumBytes = 64 * 1024;
        private const int ClaudeMaximumBytes = 1024 * 1024;
        private const string GitHubCopilotPolicyPath = @"SOFTWARE\Policies\GitHubCopilot";
        private const string VsCodePolicyPath = @"Software\Policies\Microsoft\VSCode";
        private const string ClaudeCodePolicyPath = @"SOFTWARE\Policies\ClaudeCode";
        private const string ClaudeCodeSettingsValueName = "Settings";
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private readonly string? windowsProgramFiles;
        private readonly Action<ManagedFileReadStage>? readHook;

        public SystemSetupManagedSettingsSource(
            string? windowsProgramFiles,
            Action<ManagedFileReadStage>? readHook)
        {
            this.windowsProgramFiles = windowsProgramFiles;
            this.readHook = readHook;
        }

        public SetupManagedObservation Read(SetupManagedLocation location)
        {
            try
            {
                return location switch
                {
                    SetupManagedLocation.GitHubCopilotNativeWindowsMachinePolicy =>
                        ReadWindowsRegistry(machine: true, GitHubCopilotPolicyPath, valuePrefix: null),
                    SetupManagedLocation.GitHubCopilotNativeMacOsManagedPreferences =>
                        ReadMacOsPreferences("com.github.copilot", keyPrefix: null),
                    SetupManagedLocation.GitHubCopilotFileWindows =>
                        ReadWindowsManagedFile(),
                    SetupManagedLocation.GitHubCopilotFileMacOs =>
                        ReadFileFor(SetupPlanningOs.MacOs, "/Library/Application Support/GitHubCopilot/managed-settings.json"),
                    SetupManagedLocation.GitHubCopilotFileLinux =>
                        ReadFileFor(SetupPlanningOs.Linux, "/etc/github-copilot/managed-settings.json"),
                    SetupManagedLocation.VsCodeEnterpriseWindowsMachinePolicy =>
                        ReadWindowsRegistry(machine: true, VsCodePolicyPath, "CopilotOtel"),
                    SetupManagedLocation.VsCodeEnterpriseWindowsUserPolicy =>
                        ReadWindowsRegistry(machine: false, VsCodePolicyPath, "CopilotOtel"),
                    SetupManagedLocation.VsCodeEnterpriseMacOsConfigurationProfile =>
                        ReadMacOsPreferences("com.microsoft.VSCode", "CopilotOtel"),
                    SetupManagedLocation.VsCodeEnterpriseLinuxPolicyFile =>
                        ReadFileFor(SetupPlanningOs.Linux, "/etc/vscode/policy.json"),
                    SetupManagedLocation.ClaudeCodeWindowsMachinePolicy =>
                        ReadClaudeWindowsRegistry(machine: true),
                    SetupManagedLocation.ClaudeCodeWindowsUserPolicy =>
                        ReadClaudeWindowsRegistry(machine: false),
                    _ => SetupManagedObservation.Failed,
                };
            }
            catch (Exception)
            {
                return SetupManagedObservation.Failed;
            }
        }

        private static SetupManagedObservation ReadWindowsRegistry(
            bool machine,
            string subKey,
            string? valuePrefix)
        {
            if (!System.OperatingSystem.IsWindows())
            {
                return SetupManagedObservation.Absent;
            }

            var hive = machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            if (key is null)
            {
                return SetupManagedObservation.Absent;
            }

            return ReadWindowsRegistryCore(key, valuePrefix);
        }

        private static SetupManagedObservation ReadClaudeWindowsRegistry(bool machine)
        {
            if (!System.OperatingSystem.IsWindows())
            {
                return SetupManagedObservation.Absent;
            }

            var hive = machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(ClaudeCodePolicyPath, writable: false);
            if (key is null)
            {
                return SetupManagedObservation.Absent;
            }

            return ReadClaudeWindowsRegistryCore(key);
        }

        [SupportedOSPlatform("windows")]
        private static SetupManagedObservation ReadClaudeWindowsRegistryCore(RegistryKey key) =>
            ReadBoundedClaudeRegistrySettings(
                key.GetValueKind,
                (name, options) => key.GetValue(name, defaultValue: null, options));

        [SupportedOSPlatform("windows")]
        internal static SetupManagedObservation ReadBoundedClaudeRegistrySettings(
            Func<string, RegistryValueKind> readKind,
            Func<string, RegistryValueOptions, object?> readValue)
        {
            try
            {
                var value = readValue(
                    ClaudeCodeSettingsValueName,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value is null)
                {
                    return SetupManagedObservation.Absent;
                }

                var kind = readKind(ClaudeCodeSettingsValueName);
                if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString) || value is not string text)
                {
                    return SetupManagedObservation.Failed;
                }

                if (text.Length > ClaudeMaximumBytes)
                {
                    return new SetupManagedObservation(SetupManagedOutcome.Present, [], false);
                }

                var byteCount = StrictUtf8.GetByteCount(text);
                return byteCount <= ClaudeMaximumBytes
                    ? new SetupManagedObservation(SetupManagedOutcome.Present, StrictUtf8.GetBytes(text), true)
                    : new SetupManagedObservation(SetupManagedOutcome.Present, [], false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or EncoderFallbackException)
            {
                return SetupManagedObservation.Failed;
            }
        }

        [SupportedOSPlatform("windows")]
        private static SetupManagedObservation ReadWindowsRegistryCore(
            RegistryKey key,
            string? valuePrefix) =>
            ReadBoundedRegistryValues(
                key.ValueCount,
                key.GetValueNames,
                key.GetValueKind,
                (name, options) => key.GetValue(name, defaultValue: null, options),
                valuePrefix);

        [SupportedOSPlatform("windows")]
        internal static SetupManagedObservation ReadBoundedRegistryValues(
            int valueCount,
            Func<string[]> readNames,
            Func<string, RegistryValueKind> readKind,
            Func<string, RegistryValueOptions, object?> readValue,
            string? valuePrefix)
        {
            try
            {
                if (valueCount < 0 || valueCount > 256)
                {
                    return SetupManagedObservation.Failed;
                }

                var names = readNames();
                if (names.Length > 256)
                {
                    return SetupManagedObservation.Failed;
                }

                var selectedNames = names
                    .Where(name => valuePrefix is null || name.StartsWith(valuePrefix, StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (selectedNames.Length == 0)
                {
                    return SetupManagedObservation.Absent;
                }

                var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
                foreach (var name in selectedNames)
                {
                    if (name.Length > 256)
                    {
                        return SetupManagedObservation.Failed;
                    }

                    var kind = readKind(name);
                    var value = readValue(name, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (!TryNormalizeRegistryValue(kind, value, out var normalized))
                    {
                        return SetupManagedObservation.Failed;
                    }

                    values.Add(name, normalized);
                }

                return Bound(JsonSerializer.SerializeToUtf8Bytes(values));
            }
            catch (Exception)
            {
                return SetupManagedObservation.Failed;
            }
        }

        [SupportedOSPlatform("windows")]
        private static bool TryNormalizeRegistryValue(
            RegistryValueKind kind,
            object? value,
            out object? normalized)
        {
            normalized = (kind, value) switch
            {
                (RegistryValueKind.String or RegistryValueKind.ExpandString, string text) when text.Length <= 8192 => text,
                (RegistryValueKind.DWord, int number) => number,
                (RegistryValueKind.QWord, long number) => number,
                (RegistryValueKind.MultiString, string[] strings) when
                    strings.Length <= 256 && strings.All(item => item.Length <= 8192) => strings,
                (RegistryValueKind.Binary, byte[] bytes) when bytes.Length <= MaximumBytes => Convert.ToBase64String(bytes),
                _ => null,
            };
            return normalized is not null;
        }

        private SetupManagedObservation ReadWindowsManagedFile()
        {
            if (!System.OperatingSystem.IsWindows())
            {
                return SetupManagedObservation.Absent;
            }

            var programFiles = windowsProgramFiles ??
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return string.IsNullOrWhiteSpace(programFiles)
                ? SetupManagedObservation.Failed
                : ReadFile(Path.Combine(programFiles, "GitHubCopilot", "managed-settings.json"));
        }

        private SetupManagedObservation ReadFileFor(SetupPlanningOs operatingSystem, string path) =>
            CurrentOperatingSystem() == operatingSystem
                ? ReadFile(path)
                : SetupManagedObservation.Absent;

        private SetupManagedObservation ReadFile(string path)
        {
            try
            {
                if (System.OperatingSystem.IsWindows())
                {
                    return WindowsManagedFile.Read(path, readHook);
                }

                if (System.OperatingSystem.IsLinux())
                {
                    return LinuxManagedFile.Read(path);
                }

                return System.OperatingSystem.IsMacOS()
                    ? MacOsManagedFile.Read(path)
                    : SetupManagedObservation.Failed;
            }
            catch (Exception)
            {
                return SetupManagedObservation.Failed;
            }
        }

        private static SetupManagedObservation ReadOpenedFile(SafeFileHandle handle)
        {
            using var stream = new FileStream(handle, FileAccess.Read);
            var bytes = new byte[MaximumBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = stream.Read(bytes, length, bytes.Length - length);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            return new SetupManagedObservation(
                SetupManagedOutcome.Present,
                bytes.AsSpan(0, length).ToArray(),
                length <= MaximumBytes);
        }

        private static class WindowsManagedFile
        {
            private const uint GenericRead = 0x80000000;
            private const uint FileReadAttributes = 0x00000080;
            private const uint FileShareRead = 0x00000001;
            private const uint FileShareWrite = 0x00000002;
            private const uint OpenExisting = 3;
            private const uint OpenReparsePoint = 0x00200000;
            private const uint BackupSemantics = 0x02000000;
            private const int FileAttributeTagInfoClass = 9;
            private const int FileIdInfoClass = 18;
            private const int ErrorFileNotFound = 2;
            private const int ErrorPathNotFound = 3;

            public static SetupManagedObservation Read(
                string path,
                Action<ManagedFileReadStage>? readHook)
            {
                var ancestors = new List<SafeFileHandle>();
                var ancestorPaths = new List<string>();
                try
                {
                    var fullPath = Path.GetFullPath(path);
                    var root = Path.GetPathRoot(fullPath);
                    if (string.IsNullOrEmpty(root))
                    {
                        return SetupManagedObservation.Failed;
                    }

                    var components = fullPath[root.Length..]
                        .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
                    if (components.Length == 0)
                    {
                        return SetupManagedObservation.Failed;
                    }

                    var current = root;
                    var rootObservation = OpenDirectory(current, out var rootHandle);
                    if (rootObservation is not null)
                    {
                        return rootObservation;
                    }

                    ancestors.Add(rootHandle!);
                    ancestorPaths.Add(current);
                    for (var index = 0; index < components.Length - 1; index++)
                    {
                        current = Path.Combine(current, components[index]);
                        var observation = OpenDirectory(current, out var directoryHandle);
                        if (observation is not null)
                        {
                            return observation;
                        }

                        ancestors.Add(directoryHandle!);
                        ancestorPaths.Add(current);
                    }

                    readHook?.Invoke(ManagedFileReadStage.AncestorsOpened);
                    using var fileHandle = CreateFile(
                        fullPath,
                        GenericRead,
                        FileShareRead,
                        IntPtr.Zero,
                        OpenExisting,
                        OpenReparsePoint | BackupSemantics,
                        IntPtr.Zero);
                    if (fileHandle.IsInvalid)
                    {
                        return MissingOrFailed(Marshal.GetLastPInvokeError());
                    }

                    if (!TryGetAttributes(fileHandle, out var attributes) ||
                        !IsWindowsManagedRegularFile(attributes, GetFileType(fileHandle)))
                    {
                        return SetupManagedObservation.Failed;
                    }

                    readHook?.Invoke(ManagedFileReadStage.FileOpened);
                    if (!AncestryStillMatches(ancestorPaths, ancestors))
                    {
                        return SetupManagedObservation.Failed;
                    }

                    return ReadOpenedFile(fileHandle);
                }
                finally
                {
                    for (var index = ancestors.Count - 1; index >= 0; index--)
                    {
                        ancestors[index].Dispose();
                    }
                }
            }

            private static SetupManagedObservation? OpenDirectory(
                string path,
                out SafeFileHandle? handle)
            {
                handle = CreateFile(
                    path,
                    FileReadAttributes,
                    FileShareRead | FileShareWrite,
                    IntPtr.Zero,
                    OpenExisting,
                    OpenReparsePoint | BackupSemantics,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var observation = MissingOrFailed(Marshal.GetLastPInvokeError());
                    handle.Dispose();
                    handle = null;
                    return observation;
                }

                if (!TryGetAttributes(handle, out var attributes) ||
                    (attributes & FileAttributes.ReparsePoint) != 0 ||
                    (attributes & FileAttributes.Directory) == 0 ||
                    GetFileType(handle) != 1)
                {
                    handle.Dispose();
                    handle = null;
                    return SetupManagedObservation.Failed;
                }

                return null;
            }

            private static bool AncestryStillMatches(
                IReadOnlyList<string> paths,
                IReadOnlyList<SafeFileHandle> originalHandles)
            {
                var verificationHandles = new List<SafeFileHandle>();
                try
                {
                    for (var index = 0; index < paths.Count; index++)
                    {
                        if (OpenDirectory(paths[index], out var verificationHandle) is not null)
                        {
                            return false;
                        }

                        verificationHandles.Add(verificationHandle!);
                        if (!TryGetIdentity(originalHandles[index], out var originalIdentity) ||
                            !TryGetIdentity(verificationHandle!, out var verificationIdentity) ||
                            originalIdentity != verificationIdentity)
                        {
                            return false;
                        }
                    }

                    return true;
                }
                finally
                {
                    for (var index = verificationHandles.Count - 1; index >= 0; index--)
                    {
                        verificationHandles[index].Dispose();
                    }
                }
            }

            private static bool TryGetAttributes(SafeFileHandle handle, out FileAttributes attributes)
            {
                var succeeded = GetFileInformationByHandleEx(
                    handle,
                    FileAttributeTagInfoClass,
                    out FileAttributeTagInfo information,
                    (uint)Marshal.SizeOf<FileAttributeTagInfo>());
                attributes = succeeded ? (FileAttributes)information.FileAttributes : 0;
                return succeeded;
            }

            private static bool TryGetIdentity(SafeFileHandle handle, out FileIdentity identity)
            {
                var succeeded = GetFileInformationByHandleEx(
                    handle,
                    FileIdInfoClass,
                    out FileIdInfo information,
                    (uint)Marshal.SizeOf<FileIdInfo>());
                identity = succeeded
                    ? new FileIdentity(
                        information.VolumeSerialNumber,
                        information.IdentifierLow,
                        information.IdentifierHigh)
                    : default;
                return succeeded;
            }

            private static SetupManagedObservation MissingOrFailed(int error) =>
                error is ErrorFileNotFound or ErrorPathNotFound
                    ? SetupManagedObservation.Absent
                    : SetupManagedObservation.Failed;

            [StructLayout(LayoutKind.Sequential)]
            private struct FileAttributeTagInfo
            {
                public uint FileAttributes;
                public uint ReparseTag;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct FileIdInfo
            {
                public ulong VolumeSerialNumber;
                public ulong IdentifierLow;
                public ulong IdentifierHigh;
            }

            private readonly record struct FileIdentity(
                ulong VolumeSerialNumber,
                ulong IdentifierLow,
                ulong IdentifierHigh);

            [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern SafeFileHandle CreateFile(
                string fileName,
                uint desiredAccess,
                uint shareMode,
                IntPtr securityAttributes,
                uint creationDisposition,
                uint flagsAndAttributes,
                IntPtr templateFile);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetFileInformationByHandleEx(
                SafeFileHandle file,
                int fileInformationClass,
                out FileAttributeTagInfo fileInformation,
                uint bufferSize);

            [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetFileInformationByHandleEx(
                SafeFileHandle file,
                int fileInformationClass,
                out FileIdInfo fileInformation,
                uint bufferSize);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern uint GetFileType(SafeFileHandle file);
        }

        private static class LinuxManagedFile
        {
            private const long OpenAt2SystemCall = 437;
            private const int AtCurrentWorkingDirectory = -100;
            private const int AtEmptyPath = 0x1000;
            private const ulong OpenCloseOnExec = 0x80000;
            private const ulong OpenNonBlocking = 0x800;
            private const ulong OpenNoFollow = 0x20000;
            private const ulong ResolveNoMagicLinks = 0x02;
            private const ulong ResolveNoSymbolicLinks = 0x04;
            private const uint StatxType = 0x00000001;
            private const int StatxBufferSize = 256;
            private const int StatxModeOffset = 28;
            private const int ErrorNoEntry = 2;
            private const int ErrorNotDirectory = 20;

            public static SetupManagedObservation Read(string path)
            {
                var how = new OpenHow
                {
                    Flags = OpenCloseOnExec | OpenNonBlocking | OpenNoFollow,
                    Resolve = ResolveNoMagicLinks | ResolveNoSymbolicLinks,
                };
                var descriptor = OpenAt2(
                    OpenAt2SystemCall,
                    AtCurrentWorkingDirectory,
                    path,
                    ref how,
                    (nuint)Marshal.SizeOf<OpenHow>());
                if (descriptor == -1)
                {
                    return MissingOrFailed(Marshal.GetLastPInvokeError());
                }

                using var handle = new SafeFileHandle(descriptor, ownsHandle: true);
                var buffer = new byte[StatxBufferSize];
                if (Statx(
                    checked((int)descriptor),
                    string.Empty,
                    AtEmptyPath,
                    StatxType,
                    buffer) != 0 ||
                    !IsUnixManagedRegularFile(BitConverter.ToUInt16(buffer, StatxModeOffset)))
                {
                    return SetupManagedObservation.Failed;
                }

                return ReadOpenedFile(handle);
            }

            private static SetupManagedObservation MissingOrFailed(int error) =>
                error is ErrorNoEntry or ErrorNotDirectory
                    ? SetupManagedObservation.Absent
                    : SetupManagedObservation.Failed;

            [StructLayout(LayoutKind.Sequential)]
            private struct OpenHow
            {
                public ulong Flags;
                public ulong Mode;
                public ulong Resolve;
            }

            [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
            private static extern nint OpenAt2(
                long number,
                int directoryFileDescriptor,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                ref OpenHow how,
                nuint size);

            [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
            private static extern int Statx(
                int directoryFileDescriptor,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                int flags,
                uint mask,
                [Out] byte[] buffer);
        }

        private static class MacOsManagedFile
        {
            private const int OpenReadOnly = 0x0000;
            private const int OpenNonBlocking = 0x0004;
            private const int OpenNoFollow = 0x0100;
            private const int OpenDirectory = 0x00100000;
            private const int OpenCloseOnExec = 0x01000000;
            private const int StatBufferSize = 256;
            private const int StatModeOffset = 4;
            private const int ErrorNoEntry = 2;
            private const int ErrorNotDirectory = 20;

            public static SetupManagedObservation Read(string path)
            {
                if (!path.StartsWith("/", StringComparison.Ordinal))
                {
                    return SetupManagedObservation.Failed;
                }

                var components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (components.Length == 0 || components.Any(component => component is "." or ".."))
                {
                    return SetupManagedObservation.Failed;
                }

                var ancestors = new List<SafeFileHandle>();
                try
                {
                    var root = Open("/", OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec);
                    var rootObservation = ValidateDirectory(root, out var rootHandle);
                    if (rootObservation is not null)
                    {
                        return rootObservation;
                    }

                    ancestors.Add(rootHandle!);
                    var parent = rootHandle!;
                    for (var index = 0; index < components.Length - 1; index++)
                    {
                        var descriptor = OpenAt(
                            checked((int)parent.DangerousGetHandle()),
                            components[index],
                            OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec);
                        var observation = ValidateDirectory(descriptor, out var directoryHandle);
                        if (observation is not null)
                        {
                            return observation;
                        }

                        ancestors.Add(directoryHandle!);
                        parent = directoryHandle!;
                    }

                    var fileDescriptor = OpenAt(
                        checked((int)parent.DangerousGetHandle()),
                        components[^1],
                        OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec);
                    if (fileDescriptor == -1)
                    {
                        return MissingOrFailed(Marshal.GetLastPInvokeError());
                    }

                    using var fileHandle = new SafeFileHandle(fileDescriptor, ownsHandle: true);
                    if (!TryReadMode(fileDescriptor, out var mode) || !IsUnixManagedRegularFile(mode))
                    {
                        return SetupManagedObservation.Failed;
                    }

                    return ReadOpenedFile(fileHandle);
                }
                finally
                {
                    for (var index = ancestors.Count - 1; index >= 0; index--)
                    {
                        ancestors[index].Dispose();
                    }
                }
            }

            private static SetupManagedObservation? ValidateDirectory(
                int descriptor,
                out SafeFileHandle? handle)
            {
                if (descriptor == -1)
                {
                    handle = null;
                    return MissingOrFailed(Marshal.GetLastPInvokeError());
                }

                handle = new SafeFileHandle(descriptor, ownsHandle: true);
                if (!TryReadMode(descriptor, out var mode) || (mode & 0xf000) != 0x4000)
                {
                    handle.Dispose();
                    handle = null;
                    return SetupManagedObservation.Failed;
                }

                return null;
            }

            private static bool TryReadMode(int descriptor, out uint mode)
            {
                var buffer = new byte[StatBufferSize];
                var succeeded = FStat(descriptor, buffer) == 0;
                mode = succeeded ? BitConverter.ToUInt16(buffer, StatModeOffset) : 0u;
                return succeeded;
            }

            private static SetupManagedObservation MissingOrFailed(int error) =>
                error is ErrorNoEntry or ErrorNotDirectory
                    ? SetupManagedObservation.Absent
                    : SetupManagedObservation.Failed;

            [DllImport("libSystem.B.dylib", EntryPoint = "open", SetLastError = true)]
            private static extern int Open(
                [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                int flags);

            [DllImport("libSystem.B.dylib", EntryPoint = "openat", SetLastError = true)]
            private static extern int OpenAt(
                int directoryFileDescriptor,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                int flags);

            [DllImport("libSystem.B.dylib", EntryPoint = "fstat", SetLastError = true)]
            private static extern int FStat(int fileDescriptor, [Out] byte[] buffer);
        }

        private static SetupManagedObservation ReadMacOsPreferences(string domain, string? keyPrefix)
        {
            if (!System.OperatingSystem.IsMacOS())
            {
                return SetupManagedObservation.Absent;
            }

            return MacOsManagedPreferences.Read(domain, keyPrefix);
        }

        private static SetupManagedObservation Bound(byte[] bytes)
        {
            var retained = Math.Min(bytes.Length, MaximumBytes + 1);
            return new SetupManagedObservation(
                SetupManagedOutcome.Present,
                bytes.AsSpan(0, retained).ToArray(),
                bytes.Length <= MaximumBytes);
        }

        private static SetupPlanningOs CurrentOperatingSystem() =>
            System.OperatingSystem.IsWindows()
                ? SetupPlanningOs.Windows
                : System.OperatingSystem.IsMacOS()
                    ? SetupPlanningOs.MacOs
                    : SetupPlanningOs.Linux;

        private static class MacOsManagedPreferences
        {
            private const string CoreFoundation =
                "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
            private const uint Utf8Encoding = 0x08000100;
            private const int Signed64Number = 4;
            private const int DoubleNumber = 13;
            private static readonly IntPtr CoreFoundationHandle = NativeLibrary.Load(CoreFoundation);
            private static readonly IntPtr CurrentUser = ReadConstant("kCFPreferencesCurrentUser");
            private static readonly IntPtr AnyHost = ReadConstant("kCFPreferencesAnyHost");

            public static SetupManagedObservation Read(string domain, string? keyPrefix)
            {
                var applicationId = CFStringCreateWithCString(IntPtr.Zero, domain, Utf8Encoding);
                if (applicationId == IntPtr.Zero)
                {
                    return SetupManagedObservation.Failed;
                }

                IntPtr keys = IntPtr.Zero;
                try
                {
                    keys = CFPreferencesCopyKeyList(applicationId, CurrentUser, AnyHost);
                    if (keys == IntPtr.Zero)
                    {
                        return SetupManagedObservation.Absent;
                    }

                    var count = CFArrayGetCount(keys);
                    if (count < 0 || count > 4096)
                    {
                        return SetupManagedObservation.Failed;
                    }

                    var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
                    for (nint index = 0; index < count; index++)
                    {
                        var key = CFArrayGetValueAtIndex(keys, index);
                        if (key == IntPtr.Zero || CFPreferencesAppValueIsForced(key, applicationId) == 0)
                        {
                            continue;
                        }

                        var nameWasRead = TryReadString(key, 256, out var name);
                        if (!TryClassifyMacOsManagedPreferenceKey(
                            nameWasRead,
                            name,
                            keyPrefix,
                            out var shouldRead))
                        {
                            return SetupManagedObservation.Failed;
                        }

                        if (!shouldRead)
                        {
                            continue;
                        }

                        if (values.Count == 256)
                        {
                            return SetupManagedObservation.Failed;
                        }

                        var value = CFPreferencesCopyAppValue(key, applicationId);
                        if (value == IntPtr.Zero)
                        {
                            return SetupManagedObservation.Failed;
                        }

                        try
                        {
                            if (!TryReadScalar(value, out var scalar))
                            {
                                return SetupManagedObservation.Failed;
                            }

                            values.Add(name, scalar);
                        }
                        finally
                        {
                            CFRelease(value);
                        }
                    }

                    return values.Count == 0
                        ? SetupManagedObservation.Absent
                        : Bound(JsonSerializer.SerializeToUtf8Bytes(values));
                }
                finally
                {
                    if (keys != IntPtr.Zero)
                    {
                        CFRelease(keys);
                    }

                    CFRelease(applicationId);
                }
            }

            private static bool TryReadScalar(IntPtr value, out object? scalar)
            {
                var type = CFGetTypeID(value);
                if (type == CFStringGetTypeID())
                {
                    var success = TryReadString(value, 8192, out var text);
                    scalar = success ? text : null;
                    return success;
                }

                if (type == CFBooleanGetTypeID())
                {
                    scalar = CFBooleanGetValue(value) != 0;
                    return true;
                }

                if (type == CFNumberGetTypeID())
                {
                    if (CFNumberIsFloatType(value) == 0)
                    {
                        var integer = 0L;
                        var success = CFNumberGetValue(value, Signed64Number, ref integer) != 0;
                        scalar = success ? integer : null;
                        return success;
                    }

                    var floatingPoint = 0d;
                    var floatSuccess = CFNumberGetValue(value, DoubleNumber, ref floatingPoint) != 0 &&
                        double.IsFinite(floatingPoint);
                    scalar = floatSuccess ? floatingPoint : null;
                    return floatSuccess;
                }

                scalar = null;
                return false;
            }

            private static bool TryReadString(IntPtr value, int maximumCharacters, out string text)
            {
                text = string.Empty;
                var length = CFStringGetLength(value);
                if (length < 0 || length > maximumCharacters)
                {
                    return false;
                }

                var maximumBytes = CFStringGetMaximumSizeForEncoding(length, Utf8Encoding);
                if (maximumBytes < 0 || maximumBytes > MaximumBytes)
                {
                    return false;
                }

                var buffer = new byte[(int)maximumBytes + 1];
                if (CFStringGetCString(value, buffer, buffer.Length, Utf8Encoding) == 0)
                {
                    return false;
                }

                var terminator = Array.IndexOf(buffer, (byte)0);
                text = Encoding.UTF8.GetString(buffer, 0, terminator < 0 ? buffer.Length : terminator);
                return text.Length <= maximumCharacters;
            }

            private static IntPtr ReadConstant(string name)
                => Marshal.ReadIntPtr(NativeLibrary.GetExport(CoreFoundationHandle, name));

            [DllImport(CoreFoundation)]
            private static extern IntPtr CFStringCreateWithCString(
                IntPtr allocator,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
                uint encoding);

            [DllImport(CoreFoundation)]
            private static extern IntPtr CFPreferencesCopyKeyList(
                IntPtr applicationId,
                IntPtr user,
                IntPtr host);

            [DllImport(CoreFoundation)]
            private static extern byte CFPreferencesAppValueIsForced(IntPtr key, IntPtr applicationId);

            [DllImport(CoreFoundation)]
            private static extern IntPtr CFPreferencesCopyAppValue(IntPtr key, IntPtr applicationId);

            [DllImport(CoreFoundation)]
            private static extern nint CFArrayGetCount(IntPtr array);

            [DllImport(CoreFoundation)]
            private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

            [DllImport(CoreFoundation)]
            private static extern nuint CFGetTypeID(IntPtr value);

            [DllImport(CoreFoundation)]
            private static extern nuint CFStringGetTypeID();

            [DllImport(CoreFoundation)]
            private static extern nint CFStringGetLength(IntPtr value);

            [DllImport(CoreFoundation)]
            private static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);

            [DllImport(CoreFoundation)]
            private static extern byte CFStringGetCString(IntPtr value, byte[] buffer, nint bufferSize, uint encoding);

            [DllImport(CoreFoundation)]
            private static extern nuint CFBooleanGetTypeID();

            [DllImport(CoreFoundation)]
            private static extern byte CFBooleanGetValue(IntPtr value);

            [DllImport(CoreFoundation)]
            private static extern nuint CFNumberGetTypeID();

            [DllImport(CoreFoundation)]
            private static extern byte CFNumberIsFloatType(IntPtr value);

            [DllImport(CoreFoundation)]
            private static extern byte CFNumberGetValue(IntPtr value, int numberType, ref long result);

            [DllImport(CoreFoundation, EntryPoint = "CFNumberGetValue")]
            private static extern byte CFNumberGetValue(IntPtr value, int numberType, ref double result);

            [DllImport(CoreFoundation)]
            private static extern void CFRelease(IntPtr value);
        }
    }

    internal sealed class SystemSetupHttpProbe : ISetupHttpProbe, IDisposable
    {
        private static readonly HttpClient Client = CreateClient(proxy: null);
        private readonly HttpClient client;
        private readonly bool ownsClient;

        private SystemSetupHttpProbe(HttpClient client, bool ownsClient)
        {
            this.client = client;
            this.ownsClient = ownsClient;
        }

        public static SystemSetupHttpProbe Instance { get; } = new(Client, ownsClient: false);

        internal static SystemSetupHttpProbe Create(IWebProxy proxy) =>
            new(CreateClient(proxy), ownsClient: true);

        public SetupHttpProbeObservation Get(
            string origin,
            string path,
            int totalBudgetMilliseconds,
            int maxBodyBytes)
        {
            if (!TryCreateRequestUri(origin, path, out var requestUri) ||
                totalBudgetMilliseconds <= 0 ||
                maxBodyBytes < 0 ||
                maxBodyBytes == int.MaxValue)
            {
                return SetupHttpProbeObservation.TransportFailure;
            }

            try
            {
                return GetCoreAsync(requestUri, totalBudgetMilliseconds, maxBodyBytes).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return SetupHttpProbeObservation.TimedOut;
            }
            catch (HttpRequestException exception) when (IsConnectionRefused(exception))
            {
                return SetupHttpProbeObservation.Refused;
            }
            catch (Exception)
            {
                return SetupHttpProbeObservation.TransportFailure;
            }
        }

        private async Task<SetupHttpProbeObservation> GetCoreAsync(
            Uri requestUri,
            int totalBudgetMilliseconds,
            int maxBodyBytes)
        {
            using var budget = new CancellationTokenSource(totalBudgetMilliseconds);
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                budget.Token);
            var status = (int)response.StatusCode;
            var contentLength = response.Content.Headers.ContentLength is >= 0
                ? response.Content.Headers.ContentLength
                : null;
            if (status is >= 300 and <= 399)
            {
                return new SetupHttpProbeObservation(
                    SetupHttpProbeOutcome.RedirectBlocked,
                    status,
                    contentLength,
                    [],
                    true);
            }

            if (contentLength > maxBodyBytes)
            {
                return new SetupHttpProbeObservation(
                    SetupHttpProbeOutcome.Response,
                    status,
                    contentLength,
                    [],
                    false);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(budget.Token);
            var buffer = new byte[maxBodyBytes + 1];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), budget.Token);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            return new SetupHttpProbeObservation(
                SetupHttpProbeOutcome.Response,
                status,
                contentLength,
                buffer.AsSpan(0, length).ToArray(),
                length <= maxBodyBytes);
        }

        private static bool TryCreateRequestUri(string origin, string path, out Uri requestUri)
        {
            requestUri = null!;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var baseUri) ||
                !baseUri.IsLoopback ||
                !string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                baseUri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(baseUri.Query) ||
                !string.IsNullOrEmpty(baseUri.Fragment) ||
                !string.IsNullOrEmpty(baseUri.UserInfo) ||
                path is not ("/health/live" or "/health/ready"))
            {
                return false;
            }

            requestUri = new Uri(baseUri, path);
            return true;
        }

        private static bool IsConnectionRefused(Exception exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (current is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
                {
                    return true;
                }
            }

            return false;
        }

        private static HttpClient CreateClient(IWebProxy? proxy)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = Timeout.InfiniteTimeSpan,
                Proxy = proxy,
                UseCookies = false,
                UseProxy = false,
            };
            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }

        public void Dispose()
        {
            if (ownsClient)
            {
                client.Dispose();
            }
        }
    }

    private sealed class SystemSetupClock : ISetupClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class SystemSetupIdentifierGenerator : ISetupIdentifierGenerator
    {
        public Guid CreateUuidV7() => Guid.CreateVersion7();
    }

    private sealed class NoOpSetupExecution : ISetupExecution
    {
        public static NoOpSetupExecution Instance { get; } = new();

        public void Checkpoint(string operation)
        {
        }
    }
}
