using System.Diagnostics.CodeAnalysis;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using GitHub.Copilot;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal interface ICopilotSkillRuntimeClient : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);

    Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> DiscoverSkillsAsync(
        IReadOnlyList<string> projectPaths,
        IReadOnlyList<string> skillDirectories,
        CancellationToken cancellationToken);

    Task<CopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken);

    void RecordSessionStartCopilotVersion(string? copilotVersion);
}

internal sealed record CopilotRuntimeStatusObservationV1(string? Version, int? ProtocolVersion, string? SessionStartCopilotVersion);

internal sealed record CopilotDiscoveredSkillFactV1(
    string Name,
    string Source,
    string Path,
    string? ProjectPath,
    string? Description,
    string? ArgumentHint,
    bool Enabled,
    bool UserInvocable);

internal enum CopilotRuntimeAdmissionOutcome
{
    Admitted,
    NotAdmitted
}

internal abstract record CopilotSkillDiscoveryOutcome
{
    private CopilotSkillDiscoveryOutcome() { }

    public sealed record Discovered(IReadOnlyList<CopilotDiscoveredSkillFactV1> Facts) : CopilotSkillDiscoveryOutcome;

    public sealed record Unavailable : CopilotSkillDiscoveryOutcome;
}

internal sealed class CopilotSdkSkillDiscoveryGateway
{
    private readonly Func<ICopilotSkillRuntimeClient> clientFactory;
    private readonly CopilotRuntimeAdmissionV1 admission;

    public CopilotSdkSkillDiscoveryGateway(
        Func<ICopilotSkillRuntimeClient> clientFactory,
        CopilotRuntimeAdmissionV1 admission)
    {
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        this.admission = admission ?? throw new ArgumentNullException(nameof(admission));
    }

    public async Task<CopilotRuntimeAdmissionOutcome> AdmitRuntimeGenerationAsync(CancellationToken cancellationToken)
    {
        if (admission.IsShutdownClosed)
        {
            return CopilotRuntimeAdmissionOutcome.NotAdmitted;
        }

        var client = clientFactory();
        if (admission.IsNormalShutdownStarted || admission.IsShutdownClosed)
        {
            await DisposeClientAsync(client).ConfigureAwait(false);
            return CopilotRuntimeAdmissionOutcome.NotAdmitted;
        }

        try
        {
            await client.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeClientAsync(client).ConfigureAwait(false);
            return CopilotRuntimeAdmissionOutcome.NotAdmitted;
        }

        CopilotRuntimeStatusObservationV1? status;
        try
        {
            status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            status = null;
        }

        if (!CertifiesAdmission(status))
        {
            var removed = admission.InvalidateCurrentGeneration();
            if (removed is not null)
            {
                await DisposeClientAsync(removed.Client).ConfigureAwait(false);
            }

            await DisposeClientAsync(client).ConfigureAwait(false);
            return CopilotRuntimeAdmissionOutcome.NotAdmitted;
        }

        var published = admission.PublishAdmittedGeneration(client, out var replaced);
        if (published is null)
        {
            await DisposeClientAsync(client).ConfigureAwait(false);
            return CopilotRuntimeAdmissionOutcome.NotAdmitted;
        }

        if (replaced is not null)
        {
            await DisposeClientAsync(replaced.Client).ConfigureAwait(false);
        }

        return CopilotRuntimeAdmissionOutcome.Admitted;
    }

    public async Task<CopilotSkillDiscoveryOutcome> DiscoverAsync(
        CopilotRuntimeOperationCapabilityV1 capability,
        DiscoveryRootSetV1 roots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(roots);
        if (!capability.Owner.IsAdmitted)
        {
            return new CopilotSkillDiscoveryOutcome.Unavailable();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, capability.WorkToken);
        IReadOnlyList<CopilotDiscoveredSkillFactV1>? facts;
        try
        {
            facts = await capability.Owner.Client
                .DiscoverSkillsAsync(roots.ProjectPathKeys, roots.SkillDirectoryKeys, linked.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            return new CopilotSkillDiscoveryOutcome.Unavailable();
        }

        return facts is null
            ? new CopilotSkillDiscoveryOutcome.Unavailable()
            : new CopilotSkillDiscoveryOutcome.Discovered(facts);
    }

    public async Task ReportSessionStartObservationAsync(CopilotRuntimeGenerationV1 owningGeneration, string? observedCopilotVersion)
    {
        ArgumentNullException.ThrowIfNull(owningGeneration);
        owningGeneration.Client.RecordSessionStartCopilotVersion(observedCopilotVersion);

        if (!owningGeneration.IsAdmitted)
        {
            return;
        }

        if (!string.Equals(observedCopilotVersion, owningGeneration.FrozenVersion, StringComparison.Ordinal))
        {
            var removed = admission.InvalidateGenerationIfCurrent(owningGeneration);
            if (removed is not null)
            {
                await DisposeClientAsync(removed.Client).ConfigureAwait(false);
            }
        }
    }

    internal static bool CertifiesAdmission(CopilotRuntimeStatusObservationV1? status)
        => status is not null
            && string.Equals(status.Version, CopilotRuntimeGenerationV1.AdmittedCopilotVersion, StringComparison.Ordinal)
            && status.ProtocolVersion == CopilotRuntimeGenerationV1.AdmittedProtocolVersion
            && (status.SessionStartCopilotVersion is null
                || string.Equals(status.SessionStartCopilotVersion, CopilotRuntimeGenerationV1.AdmittedCopilotVersion, StringComparison.Ordinal));

    private static async ValueTask DisposeClientAsync(ICopilotSkillRuntimeClient client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Sanitized unavailability: disposal failure never surfaces runtime detail.
        }
    }
}

internal sealed class CopilotSdkBundleClientV1 : ICopilotSkillRuntimeClient
{
    private readonly CopilotClient client;
    private string? latestSessionStartCopilotVersion;

    internal CopilotSdkBundleClientV1()
        : this(static options => new CopilotClient(options))
    {
    }

    internal CopilotSdkBundleClientV1(Func<CopilotClientOptions, CopilotClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        // Headless application-co-located bundle only: default options spawn the bundled
        // runtime; no explicit path, external URI, or environment carrier is ever supplied.
        var options = new CopilotClientOptions { Mode = CopilotClientMode.Empty };
        client = clientFactory(options);
    }

    public Task StartAsync(CancellationToken cancellationToken) => client.StartAsync(cancellationToken);

    public async Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken)
    {
        var status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status is null)
        {
            return null;
        }

        return new CopilotRuntimeStatusObservationV1(status.Version, status.ProtocolVersion, Volatile.Read(ref latestSessionStartCopilotVersion));
    }

    public async Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> DiscoverSkillsAsync(
        IReadOnlyList<string> projectPaths,
        IReadOnlyList<string> skillDirectories,
        CancellationToken cancellationToken)
    {
        var result = await client.Rpc.Skills.DiscoverAsync(
            projectPaths.ToList(),
            skillDirectories.ToList(),
            excludeHostSkills: false,
            cancellationToken).ConfigureAwait(false);
        if (result?.Skills is null)
        {
            return null;
        }

        var facts = new CopilotDiscoveredSkillFactV1[result.Skills.Count];
        for (var index = 0; index < result.Skills.Count; index++)
        {
            var skill = result.Skills[index];
            if (skill is null || skill.Name is null || skill.Path is null || skill.Source.Value is null)
            {
                return null;
            }

            facts[index] = new CopilotDiscoveredSkillFactV1(
                skill.Name,
                skill.Source.Value,
                skill.Path,
                skill.ProjectPath,
                skill.Description,
                skill.ArgumentHint,
                skill.Enabled,
                skill.UserInvocable);
        }

        return facts;
    }

    public Task<CopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
        => client.CreateSessionAsync(config, cancellationToken);

    public void RecordSessionStartCopilotVersion(string? copilotVersion)
        => Volatile.Write(ref latestSessionStartCopilotVersion, copilotVersion);

    public async ValueTask DisposeAsync() => await client.DisposeAsync().ConfigureAwait(false);
}
