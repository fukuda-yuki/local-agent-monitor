using GitHub.Copilot;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal interface IOwnedCopilotClientV1 : IAsyncDisposable
{
    ICopilotSkillRuntimeClient RuntimeClient => this as ICopilotSkillRuntimeClient
        ?? throw new InvalidOperationException("The owned SDK client has no runtime facade.");
    Task StartAsync(CancellationToken cancellationToken);
    Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken);
    Task<IOwnedCopilotSessionV1> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken);
}

internal interface IOwnedCopilotSessionV1 : IAsyncDisposable
{
    string SessionId { get; }
    Task EnsureSkillsLoadedAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> ListSkillsAsync(CancellationToken cancellationToken);
    Task SendAndWaitAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class OwnedCopilotSdkClientV1 : IOwnedCopilotClientV1, ICopilotSkillRuntimeClient
{
    private readonly CopilotClient client;

    private OwnedCopilotSdkClientV1(CopilotClient client) => this.client = client;

    public ICopilotSkillRuntimeClient RuntimeClient => this;

    internal static bool TryCreate(
        string ownedDirectory,
        Func<CopilotClientOptions, CopilotClient> clientFactory,
        Func<string, bool> environmentEntryPresent,
        out OwnedCopilotSdkClientV1? ownedClient)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownedDirectory);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(environmentEntryPresent);
        ownedClient = null;
        if (environmentEntryPresent("COPILOT_CLI_PATH")) return false;

        var client = clientFactory(new CopilotClientOptions
        {
            Mode = CopilotClientMode.Empty,
            BaseDirectory = ownedDirectory,
            WorkingDirectory = ownedDirectory,
        });
        ownedClient = new OwnedCopilotSdkClientV1(client);
        return true;
    }

    public Task StartAsync(CancellationToken cancellationToken) => client.StartAsync(cancellationToken);

    public async Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken)
    {
        var status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return status is null ? null : new(status.Version, status.ProtocolVersion, null);
    }

    public async Task<IOwnedCopilotSessionV1> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken) =>
        new OwnedCopilotSdkSessionV1(await client.CreateSessionAsync(config, cancellationToken).ConfigureAwait(false));

    async Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> ICopilotSkillRuntimeClient.DiscoverSkillsAsync(
        IReadOnlyList<string> projectPaths,
        IReadOnlyList<string> skillDirectories,
        CancellationToken cancellationToken)
    {
        var result = await client.Rpc.Skills.DiscoverAsync(
            projectPaths.ToList(), skillDirectories.ToList(), excludeHostSkills: false, cancellationToken)
            .ConfigureAwait(false);
        if (result?.Skills is null) return null;
        var facts = new CopilotDiscoveredSkillFactV1[result.Skills.Count];
        for (var index = 0; index < result.Skills.Count; index++)
        {
            var skill = result.Skills[index];
            if (skill?.Name is null || skill.Source.Value is null || skill.Path is null) return null;
            facts[index] = new(skill.Name, skill.Source.Value, skill.Path, skill.ProjectPath,
                skill.Description, skill.ArgumentHint, skill.Enabled, skill.UserInvocable);
        }
        return facts;
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();
}

internal sealed class OwnedCopilotSdkSessionV1(CopilotSession session) : IOwnedCopilotSessionV1
{
    public string SessionId => session.SessionId;

    public Task EnsureSkillsLoadedAsync(CancellationToken cancellationToken) =>
        session.Rpc.Skills.EnsureLoadedAsync(cancellationToken);

    public async Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> ListSkillsAsync(CancellationToken cancellationToken)
    {
        var result = await session.Rpc.Skills.ListAsync(cancellationToken).ConfigureAwait(false);
        if (result?.Skills is null) return null;
        var facts = new CopilotDiscoveredSkillFactV1[result.Skills.Count];
        for (var index = 0; index < result.Skills.Count; index++)
        {
            var skill = result.Skills[index];
            if (skill?.Name is null || skill.Source.Value is null || skill.Path is null) return null;
            facts[index] = new(skill.Name, skill.Source.Value, skill.Path, null, skill.Description,
                null, skill.Enabled, skill.UserInvocable);
        }
        return facts;
    }

    public async Task SendAndWaitAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken) =>
        _ = await session.SendAndWaitAsync(
            new MessageOptions { Prompt = prompt, AgentMode = AgentMode.Autopilot }, timeout, cancellationToken)
            .ConfigureAwait(false);

    public ValueTask DisposeAsync() => session.DisposeAsync();
}
