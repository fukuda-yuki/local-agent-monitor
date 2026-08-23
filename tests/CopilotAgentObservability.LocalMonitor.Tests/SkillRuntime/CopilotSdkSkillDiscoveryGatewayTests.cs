using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using GitHub.Copilot;

namespace CopilotAgentObservability.LocalMonitor.Tests.SkillRuntime;

public sealed class CopilotSdkSkillDiscoveryGatewayTests
{
    private const string CertifiedVersion = "1.0.65";
    private const int CertifiedProtocol = 3;

    [Fact]
    public async Task Admit_ShutdownClosed_NotAdmittedWithoutClientCreation()
    {
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        admission.CloseForShutdown();
        var factoryCalls = 0;
        var gateway = new CopilotSdkSkillDiscoveryGateway(() => { factoryCalls++; return new FakeSkillRuntimeClient(); }, admission);

        var outcome = await gateway.AdmitRuntimeGenerationAsync(CancellationToken.None);

        Assert.Equal(CopilotRuntimeAdmissionOutcome.NotAdmitted, outcome);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task Admit_SharedShutdownGateStarted_NotAdmittedAndCandidateDisposed()
    {
        var gate = new SkillHostShutdownGateV1();
        var admission = new CopilotRuntimeAdmissionV1(gate);
        var client = new FakeSkillRuntimeClient { StatusResult = CertifiedStatus() };
        var gateway = new CopilotSdkSkillDiscoveryGateway(() => client, admission);
        Assert.True(gate.TryStartNormalShutdown());

        var outcome = await gateway.AdmitRuntimeGenerationAsync(CancellationToken.None);

        Assert.Equal(CopilotRuntimeAdmissionOutcome.NotAdmitted, outcome);
        Assert.Equal(0, client.StartCalls);
        Assert.Equal(1, client.DisposeCalls);
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
    }

    [Fact]
    public async Task Admit_StartFailure_NotAdmittedAndClientDisposed()
    {
        var (gateway, admission, client) = NewGateway(status: CertifiedStatus());
        client.StartThrows = true;

        var outcome = await gateway.AdmitRuntimeGenerationAsync(CancellationToken.None);

        Assert.Equal(CopilotRuntimeAdmissionOutcome.NotAdmitted, outcome);
        Assert.Equal(1, client.DisposeCalls);
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
    }

    [Fact]
    public async Task Admit_StatusUnavailable_NotAdmittedClientDisposedAndCurrentInvalidated()
    {
        var (gateway, admission, firstClient) = NewGateway(status: CertifiedStatus());
        Assert.Equal(CopilotRuntimeAdmissionOutcome.Admitted, await gateway.AdmitRuntimeGenerationAsync(CancellationToken.None));
        var firstGeneration = GetCurrent(admission);
        var failingClient = new FakeSkillRuntimeClient { GetStatusThrows = true };
        var gatewayWithFailingClient = new CopilotSdkSkillDiscoveryGateway(() => failingClient, admission);

        var outcome = await gatewayWithFailingClient.AdmitRuntimeGenerationAsync(CancellationToken.None);

        Assert.Equal(CopilotRuntimeAdmissionOutcome.NotAdmitted, outcome);
        Assert.True(firstGeneration.IsInvalid);
        Assert.Equal(1, firstClient.DisposeCalls);
        Assert.Equal(1, failingClient.DisposeCalls);
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
    }

    [Theory]
    [InlineData(null, CertifiedProtocol, null)]
    [InlineData("1.0.64", CertifiedProtocol, null)]
    [InlineData("1.0.66", CertifiedProtocol, null)]
    [InlineData(" 1.0.65", CertifiedProtocol, null)]
    [InlineData("1.0.65 ", CertifiedProtocol, null)]
    [InlineData(CertifiedVersion, 2, null)]
    [InlineData(CertifiedVersion, 4, null)]
    [InlineData(CertifiedVersion, CertifiedProtocol, "1.0.64")]
    public async Task Admit_UncertifiedStatus_NotAdmittedAndNewClientDisposed(string? version, int? protocol, string? sessionStartVersion)
    {
        var (gateway, admission, client) = NewGateway(status: new CopilotRuntimeStatusObservationV1(version, protocol, sessionStartVersion));

        var outcome = await gateway.AdmitRuntimeGenerationAsync(CancellationToken.None);

        Assert.Equal(CopilotRuntimeAdmissionOutcome.NotAdmitted, outcome);
        Assert.Equal(1, client.DisposeCalls);
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(CertifiedVersion)]
    public async Task Admit_CertifiedStatus_FreezesVersionAndProtocolInPublishedGeneration(string? sessionStartVersion)
    {
        var (gateway, admission, client) = NewGateway(status: new CopilotRuntimeStatusObservationV1(CertifiedVersion, CertifiedProtocol, sessionStartVersion));

        var outcome = await gateway.AdmitRuntimeGenerationAsync(CancellationToken.None);

        Assert.Equal(CopilotRuntimeAdmissionOutcome.Admitted, outcome);
        Assert.Equal(0, client.DisposeCalls);
        var generation = GetCurrent(admission);
        Assert.Equal(CertifiedVersion, generation.FrozenVersion);
        Assert.Equal(CertifiedProtocol, generation.FrozenProtocolVersion);
        Assert.Same(client, generation.Client);
        Assert.True(generation.IsAdmitted);
    }

    [Fact]
    public async Task Admit_SecondCertifiedRuntime_ReplacesInvalidatesAndDisposesPrevious()
    {
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var firstClient = new FakeSkillRuntimeClient { StatusResult = CertifiedStatus() };
        var secondClient = new FakeSkillRuntimeClient { StatusResult = CertifiedStatus() };
        var clients = new Queue<FakeSkillRuntimeClient>([firstClient, secondClient]);
        var gateway = new CopilotSdkSkillDiscoveryGateway(() => clients.Dequeue(), admission);

        Assert.Equal(CopilotRuntimeAdmissionOutcome.Admitted, await gateway.AdmitRuntimeGenerationAsync(CancellationToken.None));
        var firstGeneration = GetCurrent(admission);

        Assert.Equal(CopilotRuntimeAdmissionOutcome.Admitted, await gateway.AdmitRuntimeGenerationAsync(CancellationToken.None));

        Assert.True(firstGeneration.IsInvalid);
        Assert.Equal(1, firstClient.DisposeCalls);
        Assert.Equal(0, secondClient.DisposeCalls);
        Assert.NotSame(firstGeneration, GetCurrent(admission));
        Assert.Same(secondClient, GetCurrent(admission).Client);
    }

    [Fact]
    public async Task Admit_ShutdownBetweenCertificationAndPublish_NotAdmittedAndClientDisposed()
    {
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var client = new FakeSkillRuntimeClient
        {
            StatusResult = CertifiedStatus(),
            AfterStatusCallback = admission.CloseForShutdown
        };
        var gateway = new CopilotSdkSkillDiscoveryGateway(() => client, admission);

        var outcome = await gateway.AdmitRuntimeGenerationAsync(CancellationToken.None);

        Assert.Equal(CopilotRuntimeAdmissionOutcome.NotAdmitted, outcome);
        Assert.Equal(1, client.DisposeCalls);
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
    }

    [Fact]
    public async Task Discover_InadmissibleCapability_UnavailableWithoutDiscoveryCall()
    {
        var (gateway, admission, client) = NewGateway(status: CertifiedStatus());
        Assert.Equal(CopilotRuntimeAdmissionOutcome.Admitted, await gateway.AdmitRuntimeGenerationAsync(CancellationToken.None));
        var generation = GetCurrent(admission);
        Assert.True(generation.TryAcquireOperationCapability(CancellationToken.None, out var capability));
        generation.Invalidate();

        var outcome = await gateway.DiscoverAsync(capability!, Roots(), CancellationToken.None);

        Assert.IsType<CopilotSkillDiscoveryOutcome.Unavailable>(outcome);
        Assert.Equal(0, client.DiscoverCalls);
        capability!.Release();
    }

    [Fact]
    public async Task Discover_NullResult_Unavailable()
    {
        var (gateway, admission, client) = NewGateway(status: CertifiedStatus());
        Assert.Equal(CopilotRuntimeAdmissionOutcome.Admitted, await gateway.AdmitRuntimeGenerationAsync(CancellationToken.None));
        Assert.True(GetCurrent(admission).TryAcquireOperationCapability(CancellationToken.None, out var capability));
        client.DiscoverResult = null;

        var outcome = await gateway.DiscoverAsync(capability!, Roots(), CancellationToken.None);

        Assert.IsType<CopilotSkillDiscoveryOutcome.Unavailable>(outcome);
        Assert.Equal(1, client.DiscoverCalls);
        capability!.Release();
    }

    [Fact]
    public async Task Discover_Exception_Unavailable()
    {
        var (gateway, admission, client) = NewGateway(status: CertifiedStatus());
        Assert.Equal(CopilotRuntimeAdmissionOutcome.Admitted, await gateway.AdmitRuntimeGenerationAsync(CancellationToken.None));
        Assert.True(GetCurrent(admission).TryAcquireOperationCapability(CancellationToken.None, out var capability));
        client.DiscoverThrows = true;

        var outcome = await gateway.DiscoverAsync(capability!, Roots(), CancellationToken.None);

        Assert.IsType<CopilotSkillDiscoveryOutcome.Unavailable>(outcome);
        capability!.Release();
    }

    [Fact]
    public async Task Discover_Success_CallsSdkExactlyOnceWithCanonicalRootArrays()
    {
        var (gateway, admission, client) = NewGateway(status: CertifiedStatus());
        Assert.Equal(CopilotRuntimeAdmissionOutcome.Admitted, await gateway.AdmitRuntimeGenerationAsync(CancellationToken.None));
        Assert.True(GetCurrent(admission).TryAcquireOperationCapability(CancellationToken.None, out var capability));
        var fact = new CopilotDiscoveredSkillFactV1("skill", "project", "C:\\repo\\skills\\SKILL.md", "C:\\repo", null, null, true, true);
        client.DiscoverResult = [fact];
        var roots = Roots();

        var outcome = await gateway.DiscoverAsync(capability!, roots, CancellationToken.None);

        var discovered = Assert.IsType<CopilotSkillDiscoveryOutcome.Discovered>(outcome);
        Assert.Equal([fact], discovered.Facts);
        Assert.Equal(1, client.DiscoverCalls);
        Assert.Equal(roots.ProjectPathKeys, client.LastProjectPaths);
        Assert.Equal(roots.SkillDirectoryKeys, client.LastSkillDirectories);
        capability!.Release();
    }

    [Fact]
    public async Task Discover_TokenLinksRuntimeInvalidation()
    {
        var (gateway, admission, _) = NewGateway(status: CertifiedStatus());
        Assert.Equal(CopilotRuntimeAdmissionOutcome.Admitted, await gateway.AdmitRuntimeGenerationAsync(CancellationToken.None));
        Assert.True(GetCurrent(admission).TryAcquireOperationCapability(CancellationToken.None, out var capability));
        var linkedTokenObservedDuringDiscovery = default(CancellationToken);
        var client = (FakeSkillRuntimeClient)GetCurrent(admission).Client;
        client.DiscoverResult = [];
        client.DiscoveringCallback = token =>
        {
            admission.InvalidateCurrentGeneration();
            linkedTokenObservedDuringDiscovery = token;
        };

        var outcome = await gateway.DiscoverAsync(capability!, Roots(), CancellationToken.None);

        Assert.IsType<CopilotSkillDiscoveryOutcome.Discovered>(outcome);
        Assert.True(linkedTokenObservedDuringDiscovery.IsCancellationRequested);
        capability!.Release();
    }

    [Fact]
    public async Task Discover_TokenLinksCallerAbort()
    {
        var (gateway, admission, _) = NewGateway(status: CertifiedStatus());
        Assert.Equal(CopilotRuntimeAdmissionOutcome.Admitted, await gateway.AdmitRuntimeGenerationAsync(CancellationToken.None));
        Assert.True(GetCurrent(admission).TryAcquireOperationCapability(CancellationToken.None, out var capability));
        using var callerCancellation = new CancellationTokenSource();
        var linkedTokenObservedDuringDiscovery = default(CancellationToken);
        var client = (FakeSkillRuntimeClient)GetCurrent(admission).Client;
        client.DiscoverResult = [];
        client.DiscoveringCallback = token =>
        {
            callerCancellation.Cancel();
            linkedTokenObservedDuringDiscovery = token;
        };

        var outcome = await gateway.DiscoverAsync(capability!, Roots(), callerCancellation.Token);

        Assert.IsType<CopilotSkillDiscoveryOutcome.Discovered>(outcome);
        Assert.True(linkedTokenObservedDuringDiscovery.IsCancellationRequested);
        capability!.Release();
    }

    [Fact]
    public async Task ReportSessionStart_MatchingVersion_RecordsWithoutInvalidation()
    {
        var (gateway, admission, client) = NewGateway(status: CertifiedStatus());
        Assert.Equal(CopilotRuntimeAdmissionOutcome.Admitted, await gateway.AdmitRuntimeGenerationAsync(CancellationToken.None));
        var generation = GetCurrent(admission);

        await gateway.ReportSessionStartObservationAsync(generation, CertifiedVersion);

        Assert.Equal(CertifiedVersion, client.RecordedSessionStartCopilotVersion);
        Assert.True(generation.IsAdmitted);
        Assert.Same(generation, GetCurrent(admission));
        Assert.Equal(0, client.DisposeCalls);
    }

    [Fact]
    public async Task ReportSessionStart_MismatchedVersion_InvalidatesCurrentAndDisposesClient()
    {
        var (gateway, admission, client) = NewGateway(status: CertifiedStatus());
        Assert.Equal(CopilotRuntimeAdmissionOutcome.Admitted, await gateway.AdmitRuntimeGenerationAsync(CancellationToken.None));
        var generation = GetCurrent(admission);

        await gateway.ReportSessionStartObservationAsync(generation, "1.0.99");

        Assert.Equal("1.0.99", client.RecordedSessionStartCopilotVersion);
        Assert.True(generation.IsInvalid);
        Assert.Equal(1, client.DisposeCalls);
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
    }

    [Fact]
    public async Task ReportSessionStart_MismatchOnReplacedGeneration_LeavesNewerGenerationUntouched()
    {
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var firstClient = new FakeSkillRuntimeClient { StatusResult = CertifiedStatus() };
        var secondClient = new FakeSkillRuntimeClient { StatusResult = CertifiedStatus() };
        var clients = new Queue<FakeSkillRuntimeClient>([firstClient, secondClient]);
        var gateway = new CopilotSdkSkillDiscoveryGateway(() => clients.Dequeue(), admission);
        Assert.Equal(CopilotRuntimeAdmissionOutcome.Admitted, await gateway.AdmitRuntimeGenerationAsync(CancellationToken.None));
        var staleGeneration = GetCurrent(admission);
        Assert.Equal(CopilotRuntimeAdmissionOutcome.Admitted, await gateway.AdmitRuntimeGenerationAsync(CancellationToken.None));
        var newerGeneration = GetCurrent(admission);

        await gateway.ReportSessionStartObservationAsync(staleGeneration, "1.0.99");

        Assert.Equal("1.0.99", firstClient.RecordedSessionStartCopilotVersion);
        Assert.Same(newerGeneration, GetCurrent(admission));
        Assert.True(newerGeneration.IsAdmitted);
        Assert.Equal(0, secondClient.DisposeCalls);
    }

    [Fact]
    public async Task ReportSessionStart_MismatchAfterDrainClose_RecordsWithoutDisposal()
    {
        var (gateway, admission, client) = NewGateway(status: CertifiedStatus());
        Assert.Equal(CopilotRuntimeAdmissionOutcome.Admitted, await gateway.AdmitRuntimeGenerationAsync(CancellationToken.None));
        var generation = GetCurrent(admission);
        admission.CloseForShutdown();

        await gateway.ReportSessionStartObservationAsync(generation, "1.0.99");

        Assert.Equal("1.0.99", client.RecordedSessionStartCopilotVersion);
        Assert.Equal(0, client.DisposeCalls);
    }

    [Theory]
    [InlineData(null, CertifiedProtocol, null, false)]
    [InlineData(CertifiedVersion, null, null, false)]
    [InlineData(CertifiedVersion, CertifiedProtocol, null, true)]
    [InlineData(CertifiedVersion, CertifiedProtocol, CertifiedVersion, true)]
    [InlineData(CertifiedVersion, CertifiedProtocol, "1.0.64", false)]
    [InlineData("1.0.64", CertifiedProtocol, null, false)]
    [InlineData(CertifiedVersion, 0, null, false)]
    public void CertifiesAdmission_Matrix(string? version, int? protocol, string? sessionStartVersion, bool expected)
    {
        var status = version is null && protocol is null && sessionStartVersion is null
            ? null
            : new CopilotRuntimeStatusObservationV1(version, protocol, sessionStartVersion);

        Assert.Equal(expected, CopilotSdkSkillDiscoveryGateway.CertifiesAdmission(status));
    }

    [Fact]
    public void CertifiesAdmission_NullStatus_IsNotCertified()
    {
        Assert.False(CopilotSdkSkillDiscoveryGateway.CertifiesAdmission(null));
    }

    [Fact]
    public void BundleClientWrapper_ConstructsHeadlessEmptyModeOptionsOnly()
    {
        CopilotClientOptions? capturedOptions = null;

        _ = new CopilotSdkBundleClientV1(options =>
        {
            capturedOptions = options;
            return null!;
        });

        Assert.NotNull(capturedOptions);
        Assert.Equal(CopilotClientMode.Empty, capturedOptions!.Mode);
    }

    private static CopilotRuntimeStatusObservationV1 CertifiedStatus()
        => new(CertifiedVersion, CertifiedProtocol, null);

    private static (CopilotSdkSkillDiscoveryGateway Gateway, CopilotRuntimeAdmissionV1 Admission, FakeSkillRuntimeClient Client) NewGateway(
        CopilotRuntimeStatusObservationV1? status)
    {
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var client = new FakeSkillRuntimeClient { StatusResult = status };
        var gateway = new CopilotSdkSkillDiscoveryGateway(() => client, admission);
        return (gateway, admission, client);
    }

    private static CopilotRuntimeGenerationV1 GetCurrent(CopilotRuntimeAdmissionV1 admission)
    {
        Assert.True(admission.TryGetCurrentAdmittedGeneration(out var generation));
        return generation!;
    }

    private static DiscoveryRootSetV1 Roots() => DiscoveryRootSetV1.Create(
        SkillProducerPathKeyPlatform.Windows,
        [
            WindowsCandidate(DiscoveryRootKindV1.ProjectPath, "C:\\repo", 1, DistinctFileId(1)),
            WindowsCandidate(DiscoveryRootKindV1.ProjectPath, "C:\\other", 1, DistinctFileId(2)),
            WindowsCandidate(DiscoveryRootKindV1.SkillDirectory, "C:\\skills", 1, DistinctFileId(3))
        ]);

    private static DiscoveryRootCandidateV1 WindowsCandidate(DiscoveryRootKindV1 kind, string path, ulong volumeSerial, byte[] fileId128)
    {
        var parsed = SkillProducerPathKeyV1.TryParse(path, SkillProducerPathKeyPlatform.Windows, out var key, out var reason);
        Assert.True(parsed, $"Test setup failed to parse Windows path '{path}': {reason}.");

        return new DiscoveryRootCandidateV1(kind, DiscoveryRootNativeIdentityV1.CreateWindows(volumeSerial, fileId128), key);
    }

    private static byte[] DistinctFileId(int index)
    {
        var fileId = new byte[16];
        fileId[14] = (byte)(index >> 8);
        fileId[15] = (byte)index;
        return fileId;
    }
}
