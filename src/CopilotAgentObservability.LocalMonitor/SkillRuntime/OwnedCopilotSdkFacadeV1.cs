using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using CopilotAgentObservability.LocalMonitor.Analysis;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal interface IOwnedCopilotClientV1 : IAsyncDisposable
{
    ICopilotSkillRuntimeClient RuntimeClient => this as ICopilotSkillRuntimeClient
        ?? throw new InvalidOperationException("The owned SDK client has no runtime facade.");
    Task StartAsync(CancellationToken cancellationToken);
    Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken);
    Task<IOwnedCopilotSessionV1> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken);
    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken);
}

internal interface IOwnedCopilotSessionV1 : IAsyncDisposable
{
    string SessionId { get; }
    Task EnsureSkillsLoadedAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> ListSkillsAsync(CancellationToken cancellationToken);
    Task SendAndWaitAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken);
    async Task<OwnedCopilotFinalResponseV1?> SendAndReadFinalContentAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await SendAndWaitAsync(prompt, timeout, cancellationToken).ConfigureAwait(false);
        return null;
    }
    Task<OwnedSkillCommandPromptV1?> InvokeExactSkillCommandAsync(string skillName, CancellationToken cancellationToken) =>
        Task.FromResult<OwnedSkillCommandPromptV1?>(null);
}

internal sealed record OwnedCopilotFinalResponseV1(string? Content, string? Model);

internal sealed record OwnedSkillCommandPromptV1(string Prompt);

internal sealed class ExactSkillCommandExecutionDriverV1(string skillName) : IOwnedSessionExecutionDriverV1
{
    public async Task ExecuteAsync(IOwnedCopilotSessionV1 session, string prompt, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var invocation = await session.InvokeExactSkillCommandAsync(skillName, cancellationToken).ConfigureAwait(false);
        if (invocation is null || string.IsNullOrWhiteSpace(invocation.Prompt))
            throw new InvalidOperationException("The retained Skill command could not be invoked.");
        await session.SendAndWaitAsync(invocation.Prompt, timeout, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class DiagnosticOwnedCopilotSessionV1(
    IOwnedCopilotSessionV1 inner,
    Action<OwnedSessionDiagnosticEventV1> observer) : IOwnedCopilotSessionV1
{
    public string SessionId => inner.SessionId;
    public Task EnsureSkillsLoadedAsync(CancellationToken cancellationToken) => inner.EnsureSkillsLoadedAsync(cancellationToken);
    public Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> ListSkillsAsync(CancellationToken cancellationToken) => inner.ListSkillsAsync(cancellationToken);
    public Task<OwnedSkillCommandPromptV1?> InvokeExactSkillCommandAsync(string skillName, CancellationToken cancellationToken)
    {
        OwnedSessionDiagnosticObservationV1.Notify(observer, OwnedSessionDiagnosticEventV1.CommandPending);
        return inner.InvokeExactSkillCommandAsync(skillName, cancellationToken);
    }
    public Task SendAndWaitAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken)
    {
        OwnedSessionDiagnosticObservationV1.Notify(observer, OwnedSessionDiagnosticEventV1.SendPending);
        return inner.SendAndWaitAsync(prompt, timeout, cancellationToken);
    }
    public Task<OwnedCopilotFinalResponseV1?> SendAndReadFinalContentAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken) =>
        inner.SendAndReadFinalContentAsync(prompt, timeout, cancellationToken);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal interface IOwnedSessionExecutionDriverV1
{
    Task ExecuteAsync(IOwnedCopilotSessionV1 session, string prompt, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class DefaultOwnedSessionExecutionDriverV1 : IOwnedSessionExecutionDriverV1
{
    internal static DefaultOwnedSessionExecutionDriverV1 Instance { get; } = new();

    private DefaultOwnedSessionExecutionDriverV1() { }

    public Task ExecuteAsync(IOwnedCopilotSessionV1 session, string prompt, TimeSpan timeout, CancellationToken cancellationToken) =>
        session.SendAndWaitAsync(prompt, timeout, cancellationToken);
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
            LogLevel = CopilotLogLevel.None,
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
        if (status is null) return null;
        var authentication = await client.GetAuthStatusAsync(cancellationToken).ConfigureAwait(false);
        return new(status.Version, status.ProtocolVersion, null, authentication?.IsAuthenticated == true);
    }

    public async Task<IOwnedCopilotSessionV1> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken) =>
        new OwnedCopilotSdkSessionV1(await client.CreateSessionAsync(config, cancellationToken).ConfigureAwait(false));

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        client.DeleteSessionAsync(sessionId, cancellationToken);

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

    public async Task<OwnedCopilotFinalResponseV1?> SendAndReadFinalContentAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var response = await session.SendAndWaitAsync(
            new MessageOptions { Prompt = prompt, AgentMode = AgentMode.Autopilot }, timeout, cancellationToken)
            .ConfigureAwait(false);
        return response?.Data is { } data ? new(data.Content, data.Model) : null;
    }

    public async Task<OwnedSkillCommandPromptV1?> InvokeExactSkillCommandAsync(string skillName, CancellationToken cancellationToken)
    {
#pragma warning disable GHCP001
        var commands = await session.Rpc.Commands.ListAsync(new CommandsListRequest
        {
            IncludeSkills = true,
            IncludeBuiltins = false,
            IncludeClientCommands = false,
        }, cancellationToken).ConfigureAwait(false);
        var matches = commands.Commands
            .Where(command => string.Equals(command.Name, skillName, StringComparison.Ordinal)
                && command.Kind == SlashCommandKind.Skill)
            .ToArray();
        if (matches.Length != 1) return null;
        var result = await session.Rpc.Commands.InvokeAsync(skillName, string.Empty, cancellationToken).ConfigureAwait(false);
        return result is SlashCommandInvocationResultAgentPrompt agentPrompt
            && agentPrompt.RuntimeSettingsChanged != true
            && !string.IsNullOrWhiteSpace(agentPrompt.Prompt)
            ? new OwnedSkillCommandPromptV1(agentPrompt.Prompt)
            : null;
#pragma warning restore GHCP001
    }

    public ValueTask DisposeAsync() => session.DisposeAsync();
}
