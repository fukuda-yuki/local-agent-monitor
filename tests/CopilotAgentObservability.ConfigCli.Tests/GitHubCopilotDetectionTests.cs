using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Tests;

[Collection(nameof(SetupLoopbackHttpCollection))]
public sealed class GitHubCopilotDetectionTests
{
    [Fact]
    public void Observe_AllProcessesAbsent_ReportsNothingInstalled()
    {
        var platform = CreatePlatform(SetupPlanningOs.Windows);

        var observations = GitHubCopilotDetection.Observe(platform);

        Assert.Equal(SetupPlanningOs.Windows, observations.PlanningOs);
        Assert.Equal(new ChannelObservation(false, null), observations.VsCodeStable);
        Assert.Equal(new ChannelObservation(false, null), observations.VsCodeInsiders);
        Assert.Equal(new ChannelObservation(false, null), observations.CopilotCli);
        Assert.False(observations.StableHasNonDefaultProfiles);
        Assert.False(observations.InsidersHasNonDefaultProfiles);
        Assert.Equal(
        [
            "process.run:code:--version",
            "process.run:code-insiders:--version",
            "process.run:copilot:version",
        ],
        platform.Operations);
    }

    [Fact]
    public void Observe_StableOnly_SanitizesTheFirstSemanticVersion()
    {
        var platform = CreatePlatform(SetupPlanningOs.Windows);
        Complete(platform, "code", ["--version"], "1.128.2\r\n0123456789abcdef\r\nx64\r\n");

        var observations = GitHubCopilotDetection.Observe(platform);

        Assert.Equal(new ChannelObservation(true, "1.128.2"), observations.VsCodeStable);
        Assert.False(observations.VsCodeInsiders.Detected);
        Assert.False(observations.CopilotCli.Detected);
    }

    [Fact]
    public void Observe_BothVsCodeChannels_ReportsBothInStableThenInsidersOrder()
    {
        var platform = CreatePlatform(SetupPlanningOs.Windows);
        Complete(platform, "code", ["--version"], "1.128.0");
        Complete(platform, "code-insiders", ["--version"], "1.129.0-insider.1");

        var observations = GitHubCopilotDetection.Observe(platform);

        Assert.Equal(new ChannelObservation(true, "1.128.0"), observations.VsCodeStable);
        Assert.Equal(new ChannelObservation(true, "1.129.0-insider.1"), observations.VsCodeInsiders);
        Assert.Equal("process.run:code:--version", platform.Operations[0]);
        Assert.StartsWith("process.run:code-insiders:--version", platform.Operations[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_CopilotCliOnly_AcceptsTheDocumentedVersionCommandOutput()
    {
        var platform = CreatePlatform(SetupPlanningOs.Linux);
        Complete(platform, "copilot", ["version"], "GitHub Copilot CLI 1.0.4\n");

        var observations = GitHubCopilotDetection.Observe(platform);

        Assert.Equal(new ChannelObservation(true, "1.0.4"), observations.CopilotCli);
        Assert.False(observations.VsCodeStable.Detected);
        Assert.False(observations.VsCodeInsiders.Detected);
    }

    [Fact]
    public void Observe_MalformedCompletedVersion_ReportsDetectedWithNullVersion()
    {
        var platform = CreatePlatform(SetupPlanningOs.Windows);
        Complete(platform, "code", ["--version"], "raw-path-marker C:\\private\\code.exe");

        var observations = GitHubCopilotDetection.Observe(platform);

        Assert.Equal(new ChannelObservation(true, null), observations.VsCodeStable);
        Assert.DoesNotContain("raw-path-marker", observations.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_NearSemanticVersion_ReportsNullRatherThanAValidSubstring()
    {
        var platform = CreatePlatform(SetupPlanningOs.Windows);
        Complete(platform, "code", ["--version"], "1.2.3.4");

        var observations = GitHubCopilotDetection.Observe(platform);

        Assert.Equal(new ChannelObservation(true, null), observations.VsCodeStable);
    }

    [Fact]
    public void Observe_ProcessNotFound_ReportsNotDetected()
    {
        var platform = CreatePlatform(SetupPlanningOs.Windows);
        platform.ScriptProcess(
            "code",
            ["--version"],
            new SetupProcessObservation(SetupProcessOutcome.NotFound, null, string.Empty));

        var observations = GitHubCopilotDetection.Observe(platform);

        Assert.Equal(new ChannelObservation(false, null), observations.VsCodeStable);
    }

    [Fact]
    public void Observe_ProcessTimeout_ReportsNotDetectedWithoutThrowing()
    {
        var platform = CreatePlatform(SetupPlanningOs.Windows);
        platform.ScriptProcess(
            "copilot",
            ["version"],
            new SetupProcessObservation(SetupProcessOutcome.TimedOut, null, string.Empty));

        GitHubCopilotObservations? observations = null;
        var exception = Record.Exception(() => observations = GitHubCopilotDetection.Observe(platform));

        Assert.Null(exception);
        Assert.False(observations!.CopilotCli.Detected);
    }

    [Fact]
    public void Observe_NonDefaultProfiles_ReportsPerChannelBooleansWithoutOpeningProfileFiles()
    {
        var platform = CreatePlatform(SetupPlanningOs.Linux);
        Complete(platform, "code", ["--version"], "1.128.0");
        Complete(platform, "code-insiders", ["--version"], "1.129.0");
        platform.SeedDirectory("/home/setup-test/.config/Code/User/profiles/stable-profile");
        platform.SeedDirectory("/home/setup-test/.config/Code - Insiders/User/profiles/insiders-profile");
        platform.SeedFile("/home/setup-test/.config/Code/User/profiles/stable-profile/settings.json", [1, 2, 3]);

        var observations = GitHubCopilotDetection.Observe(platform);

        Assert.True(observations.StableHasNonDefaultProfiles);
        Assert.True(observations.InsidersHasNonDefaultProfiles);
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("file.read", StringComparison.Ordinal));
        Assert.DoesNotContain(platform.Operations, operation => operation.Contains("settings.json", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SetupPlanningOs.Windows)]
    [InlineData(SetupPlanningOs.MacOs)]
    [InlineData(SetupPlanningOs.Linux)]
    public void Observe_PlanningOsFake_CapturesTheSelectedOperatingSystem(SetupPlanningOs planningOs)
    {
        var platform = CreatePlatform(planningOs);

        var observations = GitHubCopilotDetection.Observe(platform);

        Assert.Equal(planningOs, observations.PlanningOs);
    }

    [Fact]
    public void Observe_OverlongSemanticVersion_ReportsNullRatherThanTruncatedOutput()
    {
        var platform = CreatePlatform(SetupPlanningOs.Windows);
        Complete(platform, "code", ["--version"], $"1.128.0-{new string('a', 121)}");

        var observations = GitHubCopilotDetection.Observe(platform);

        Assert.True(observations.VsCodeStable.Detected);
        Assert.Null(observations.VsCodeStable.Version);
    }

    [Theory]
    [InlineData(SetupPlanningOs.Windows, false, "C:\\Users\\setup-test\\AppData\\Roaming\\Code\\User\\settings.json")]
    [InlineData(SetupPlanningOs.Windows, true, "C:\\Users\\setup-test\\AppData\\Roaming\\Code - Insiders\\User\\settings.json")]
    [InlineData(SetupPlanningOs.MacOs, false, "/Users/setup-test/Library/Application Support/Code/User/settings.json")]
    [InlineData(SetupPlanningOs.MacOs, true, "/Users/setup-test/Library/Application Support/Code - Insiders/User/settings.json")]
    [InlineData(SetupPlanningOs.Linux, false, "/home/setup-test/.config/Code/User/settings.json")]
    [InlineData(SetupPlanningOs.Linux, true, "/home/setup-test/.config/Code - Insiders/User/settings.json")]
    public void GetDefaultSettingsPath_ChannelAndOperatingSystem_ReturnsTheDocumentedDefaultProfilePath(
        SetupPlanningOs planningOs,
        bool insiders,
        string expected)
    {
        var platform = CreatePlatform(planningOs);

        var path = GitHubCopilotDetection.GetDefaultSettingsPath(
            platform,
            insiders ? GitHubCopilotVsCodeChannel.Insiders : GitHubCopilotVsCodeChannel.Stable);

        Assert.Equal(expected, path);
    }

    [Fact]
    public async Task SystemHttpProbe_ConfiguredProxy_IsNeverConsultedForLoopbackTarget()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var origin = $"http://127.0.0.1:{endpoint.Port}";
        var responseTask = RespondOnceAsync(listener);
        var proxy = new RecordingProxy(new Uri(origin));
        using var probe = SystemSetupPlatform.CreateHttpProbe(proxy);

        var observation = await Task.Run(() => probe.Get(origin, "/health/live", 5000, 16));
        await responseTask;

        Assert.Equal(SetupHttpProbeOutcome.Response, observation.Outcome);
        Assert.Equal(200, observation.StatusCode);
        Assert.Equal("ok", Encoding.ASCII.GetString(observation.Body));
        Assert.Equal(0, proxy.ConsultationCount);
    }

    [Fact]
    public void SystemManagedFile_WindowsChangedAncestry_FailsClosed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"setup-managed-{Guid.NewGuid():N}");
        var managedDirectory = Path.Combine(root, "GitHubCopilot");
        var managedFile = Path.Combine(managedDirectory, "managed-settings.json");
        var movedDirectory = Path.Combine(root, "moved");
        var ancestryChanged = false;
        var ancestryChangeBlocked = false;
        try
        {
            Directory.CreateDirectory(managedDirectory);
            File.WriteAllBytes(managedFile, [0x41]);
            var source = SystemSetupPlatform.CreateManagedSettingsSource(
                root,
                stage =>
                {
                    if (stage == SystemSetupPlatform.ManagedFileReadStage.AncestorsOpened)
                    {
                        ancestryChangeBlocked = ReplacementWasBlocked(
                            () => Directory.Move(managedDirectory, movedDirectory));
                        if (!ancestryChangeBlocked)
                        {
                            Directory.CreateDirectory(managedDirectory);
                            File.WriteAllBytes(managedFile, [0x42]);
                            ancestryChanged = true;
                        }
                    }
                });

            var observation = source.Read(SetupManagedLocation.GitHubCopilotFileWindows);

            Assert.True(ancestryChanged || ancestryChangeBlocked);
            if (ancestryChanged)
            {
                Assert.Equal(SetupManagedObservation.Failed, observation);
            }
            else
            {
                Assert.Equal(SetupManagedOutcome.Present, observation.Outcome);
                Assert.Equal([0x41], observation.Bytes);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SystemManagedFile_WindowsReadsOpenedIdentityWithMaxPlusSentinel()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"setup-managed-{Guid.NewGuid():N}");
        var managedDirectory = Path.Combine(root, "GitHubCopilot");
        var managedFile = Path.Combine(managedDirectory, "managed-settings.json");
        var replacement = Path.Combine(root, "replacement.json");
        var replacementAttempted = false;
        try
        {
            Directory.CreateDirectory(managedDirectory);
            File.WriteAllBytes(managedFile, Enumerable.Repeat((byte)0x41, (64 * 1024) + 2).ToArray());
            File.WriteAllBytes(replacement, [0x42]);
            var source = SystemSetupPlatform.CreateManagedSettingsSource(
                root,
                stage =>
                {
                    if (stage == SystemSetupPlatform.ManagedFileReadStage.FileOpened)
                    {
                        replacementAttempted = true;
                        _ = ReplacementWasBlocked(() => File.Move(replacement, managedFile, overwrite: true));
                    }
                });

            var observation = source.Read(SetupManagedLocation.GitHubCopilotFileWindows);

            Assert.True(replacementAttempted);
            Assert.Equal(SetupManagedOutcome.Present, observation.Outcome);
            Assert.False(observation.IsComplete);
            Assert.Equal((64 * 1024) + 1, observation.Bytes.Length);
            Assert.All(observation.Bytes, value => Assert.Equal(0x41, value));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData((int)FileAttributes.Normal, 1u, true)]
    [InlineData((int)FileAttributes.ReparsePoint, 1u, false)]
    [InlineData((int)FileAttributes.Directory, 1u, false)]
    [InlineData((int)FileAttributes.Normal, 2u, false)]
    public void SystemManagedFile_WindowsHandleClassification_RequiresNonReparseDiskRegularFile(
        int attributes,
        uint fileType,
        bool expected)
    {
        Assert.Equal(
            expected,
            SystemSetupPlatform.IsWindowsManagedRegularFile((FileAttributes)attributes, fileType));
    }

    [Theory]
    [InlineData(0x8000u, true)]
    [InlineData(0x1000u, false)]
    [InlineData(0x2000u, false)]
    [InlineData(0x4000u, false)]
    [InlineData(0x6000u, false)]
    [InlineData(0xA000u, false)]
    [InlineData(0xC000u, false)]
    public void SystemManagedFile_UnixHandleClassification_RequiresRegularFile(uint mode, bool expected)
    {
        Assert.Equal(expected, SystemSetupPlatform.IsUnixManagedRegularFile(mode));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void SystemRegistry_ValueCountAboveBound_FailsBeforeReadingNames()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var namesRead = false;

        var observation = SystemSetupPlatform.ReadBoundedRegistryValues(
            257,
            () =>
            {
                namesRead = true;
                return [];
            },
            _ => RegistryValueKind.DWord,
            (_, _) => 1,
            valuePrefix: null);

        Assert.Equal(SetupManagedObservation.Failed, observation);
        Assert.False(namesRead);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void SystemRegistry_MaximumValueCount_IsReadWithDoNotExpandOption()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var names = Enumerable.Range(0, 256).Select(index => $"value-{index:D3}").ToArray();
        var observedOptions = new List<RegistryValueOptions>();

        var observation = SystemSetupPlatform.ReadBoundedRegistryValues(
            names.Length,
            () => names,
            _ => RegistryValueKind.DWord,
            (_, options) =>
            {
                observedOptions.Add(options);
                return 1;
            },
            valuePrefix: null);

        Assert.Equal(SetupManagedOutcome.Present, observation.Outcome);
        Assert.True(observation.IsComplete);
        Assert.Equal(256, observedOptions.Count);
        Assert.All(observedOptions, option => Assert.Equal(RegistryValueOptions.DoNotExpandEnvironmentNames, option));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void SystemRegistry_SupportedKinds_RetainTypesAndExpandStringText()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var values = new Dictionary<string, (RegistryValueKind Kind, object Value)>(StringComparer.Ordinal)
        {
            ["binary"] = (RegistryValueKind.Binary, new byte[] { 1, 2, 3 }),
            ["dword"] = (RegistryValueKind.DWord, 7),
            ["expand"] = (RegistryValueKind.ExpandString, "%UNEXPANDED%"),
            ["multi"] = (RegistryValueKind.MultiString, new[] { "a", "b" }),
            ["qword"] = (RegistryValueKind.QWord, 9L),
            ["string"] = (RegistryValueKind.String, "text"),
        };

        var observation = SystemSetupPlatform.ReadBoundedRegistryValues(
            values.Count,
            () => values.Keys.ToArray(),
            name => values[name].Kind,
            (name, options) =>
            {
                Assert.Equal(RegistryValueOptions.DoNotExpandEnvironmentNames, options);
                return values[name].Value;
            },
            valuePrefix: null);

        Assert.Equal(SetupManagedOutcome.Present, observation.Outcome);
        using var json = JsonDocument.Parse(observation.Bytes);
        Assert.Equal("AQID", json.RootElement.GetProperty("binary").GetString());
        Assert.Equal(7, json.RootElement.GetProperty("dword").GetInt32());
        Assert.Equal("%UNEXPANDED%", json.RootElement.GetProperty("expand").GetString());
        Assert.Equal(["a", "b"], json.RootElement.GetProperty("multi").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(9, json.RootElement.GetProperty("qword").GetInt64());
        Assert.Equal("text", json.RootElement.GetProperty("string").GetString());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void SystemRegistry_UnsupportedValueKind_FailsClosed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var observation = SystemSetupPlatform.ReadBoundedRegistryValues(
            1,
            () => ["unsupported"],
            _ => RegistryValueKind.None,
            (_, _) => new byte[] { 1 },
            valuePrefix: null);

        Assert.Equal(SetupManagedObservation.Failed, observation);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void SystemRegistry_OverlongName_FailsBeforeReadingKindOrValue()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var kindRead = false;
        var valueRead = false;
        var observation = SystemSetupPlatform.ReadBoundedRegistryValues(
            1,
            () => [new string('a', 257)],
            _ =>
            {
                kindRead = true;
                return RegistryValueKind.String;
            },
            (_, _) =>
            {
                valueRead = true;
                return "value";
            },
            valuePrefix: null);

        Assert.Equal(SetupManagedObservation.Failed, observation);
        Assert.False(kindRead);
        Assert.False(valueRead);
    }

    [Theory]
    [InlineData(false, "", "CopilotOtel", false, false)]
    [InlineData(true, "Unrelated", "CopilotOtel", true, false)]
    [InlineData(true, "CopilotOtelEndpoint", "CopilotOtel", true, true)]
    [InlineData(true, "AnyManagedKey", null, true, true)]
    public void SystemMacOsManagedPreferenceKey_ForcedNameDisposition_FailsSkipsOrReads(
        bool nameWasRead,
        string name,
        string? prefix,
        bool expectedSuccess,
        bool expectedShouldRead)
    {
        var success = SystemSetupPlatform.TryClassifyMacOsManagedPreferenceKey(
            nameWasRead,
            name,
            prefix,
            out var shouldRead);

        Assert.Equal(expectedSuccess, success);
        Assert.Equal(expectedShouldRead, shouldRead);
    }

    private static SetupTestPlatform CreatePlatform(SetupPlanningOs planningOs) => planningOs switch
    {
        SetupPlanningOs.Windows => new SetupTestPlatform(
            DateTimeOffset.UnixEpoch,
            pathStyle: SetupPathStyle.Windows,
            planningOs: planningOs,
            applicationData: "C:\\Users\\setup-test\\AppData\\Roaming",
            userProfile: "C:\\Users\\setup-test"),
        SetupPlanningOs.MacOs => new SetupTestPlatform(
            DateTimeOffset.UnixEpoch,
            localApplicationData: "/Users/setup-test/Library/Application Support",
            pathStyle: SetupPathStyle.Unix,
            planningOs: planningOs,
            applicationData: "/Users/setup-test/Library/Application Support",
            userProfile: "/Users/setup-test"),
        SetupPlanningOs.Linux => new SetupTestPlatform(
            DateTimeOffset.UnixEpoch,
            localApplicationData: "/home/setup-test/.local/share",
            pathStyle: SetupPathStyle.Unix,
            planningOs: planningOs,
            applicationData: "/home/setup-test/.config",
            userProfile: "/home/setup-test"),
        _ => throw new ArgumentOutOfRangeException(nameof(planningOs)),
    };

    private static void Complete(
        SetupTestPlatform platform,
        string fileName,
        IReadOnlyList<string> arguments,
        string output) =>
        platform.ScriptProcess(
            fileName,
            arguments,
            new SetupProcessObservation(SetupProcessOutcome.Completed, 0, output));

    private static async Task RespondOnceAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var request = new byte[4096];
        var length = 0;
        while (length < request.Length && !HasHeaderTerminator(request.AsSpan(0, length)))
        {
            var read = await stream.ReadAsync(request.AsMemory(length, request.Length - length));
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        await stream.WriteAsync(response);
    }

    private static bool HasHeaderTerminator(ReadOnlySpan<byte> bytes) =>
        bytes.IndexOf("\r\n\r\n"u8) >= 0;

    private static bool ReplacementWasBlocked(Action replacement)
    {
        try
        {
            replacement();
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private sealed class RecordingProxy(Uri destination) : IWebProxy
    {
        public int ConsultationCount { get; private set; }

        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destinationUri)
        {
            ConsultationCount++;
            return destination;
        }

        public bool IsBypassed(Uri host)
        {
            ConsultationCount++;
            return false;
        }
    }
}
