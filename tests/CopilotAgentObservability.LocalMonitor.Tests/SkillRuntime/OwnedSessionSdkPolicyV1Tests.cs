using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using GitHub.Copilot;

namespace CopilotAgentObservability.LocalMonitor.Tests.SkillRuntime;

public sealed class OwnedSessionSdkPolicyV1Tests
{
    [Fact]
    public void OwnedClient_IsTheCurrentFileRuntimeClient()
    {
        Assert.True(typeof(ICopilotSkillRuntimeClient).IsAssignableFrom(typeof(OwnedCopilotSdkClientV1)));
    }

    [Fact]
    public void SkillProof_RootRevision_IsTheFrozenStringRevision()
    {
        var proof = new OwnedSessionSkillProofV1("native", "r0002", "content", "digest");

        Assert.Equal("r0002", proof.RootRevision);
    }

    [Fact]
    public void TryCreate_CopilotCliPathPresent_RejectsBeforeClientFactory()
    {
        var factoryCalls = 0;

        var created = OwnedCopilotSdkClientV1.TryCreate(
            "C:/owned",
            _ => { factoryCalls++; throw new InvalidOperationException("must not construct"); },
            name => name == "COPILOT_CLI_PATH",
            out var client);

        Assert.False(created);
        Assert.Null(client);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public void TryCreate_ProductOwnedClientDisablesSdkLogging()
    {
        CopilotClientOptions? capturedOptions = null;
        var sentinel = new InvalidOperationException("synthetic_factory_failure");

        var error = Assert.Throws<InvalidOperationException>(() => OwnedCopilotSdkClientV1.TryCreate(
            "C:/owned",
            options => { capturedOptions = options; throw sentinel; },
            _ => false,
            out _));

        Assert.Same(sentinel, error);
        Assert.Equal(CopilotLogLevel.None, Assert.IsType<CopilotClientOptions>(capturedOptions).LogLevel);
    }

    [Fact]
    public void CreateProbeConfig_RetainedRoots_UsesClosedPreCreateBoundary()
    {
        var callback = new Action<SessionEvent>(_ => { });
        var config = OwnedSessionSdkPolicyV1.CreateProbeConfig(BaseConfig(), ["C:/retained"], callback);

        Assert.Same(callback, config.OnEvent);
        Assert.True(config.EnableSkills);
        Assert.False(config.EnableConfigDiscovery);
        Assert.True(config.SkipCustomInstructions);
        Assert.Equal(["C:/retained"], config.SkillDirectories);
        Assert.Empty(Assert.IsAssignableFrom<IList<string>>(config.PluginDirectories));
        Assert.Empty(Assert.IsAssignableFrom<IList<string>>(config.InstructionDirectories));
        Assert.Equal(["custom:raw"], config.AvailableTools);
        Assert.Empty(Assert.IsAssignableFrom<IList<string>>(config.DisabledSkills));
    }

    [Fact]
    public void CreateExecutionConfig_FrozenInventory_AddsOnlyRequiredBuiltinsAndExactDisabledNames()
    {
        var callback = new Action<SessionEvent>(_ => { });
        var config = OwnedSessionSdkPolicyV1.CreateExecutionConfig(
            BaseConfig(), ["C:/retained"], ["ambient", "other"], callback);

        Assert.Same(callback, config.OnEvent);
        Assert.Equal(["custom:raw", "builtin:skill", "builtin:task_complete"], config.AvailableTools);
        Assert.Equal(["ambient", "other"], config.DisabledSkills);
        Assert.Null(config.Commands);
    }

    [Theory]
    [InlineData("custom:raw", "custom:raw")]
    [InlineData("*")]
    [InlineData("builtin:other")]
    [InlineData("mcp:server:tool")]
    [InlineData("plugin:tool")]
    [InlineData("ambient")]
    public void CreateProbeConfig_NonExactCustomBaseline_Rejects(params string[] availableTools)
    {
        var baseline = BaseConfig();
        baseline.AvailableTools = availableTools;

        Assert.Throws<InvalidOperationException>(() =>
            OwnedSessionSdkPolicyV1.CreateProbeConfig(baseline, ["C:/retained"], _ => { }));
    }

    [Fact]
    public void FreezeProbeInventory_DuplicateNameOrUnprovedCustom_ReturnsNull()
    {
        var duplicate = new[]
        {
            Fact("retained", "custom", "C:/retained/retained/SKILL.md"),
            Fact("retained", "custom", "C:/retained/retained/SKILL.md"),
        };
        var proof = new DictionaryProofProvider("retained");

        Assert.Null(OwnedSessionSdkPolicyV1.TryFreezeProbeInventory(duplicate, ["C:/retained"], proof));
        Assert.Null(OwnedSessionSdkPolicyV1.TryFreezeProbeInventory(
            [Fact("unknown", "custom", "C:/outside/unknown/SKILL.md")], ["C:/retained"], proof));
    }

    [Fact]
    public void FreezeAndValidateExecution_DisablesAmbientAndRejectsDescriptorDrift()
    {
        var proof = new DictionaryProofProvider("retained");
        var retained = Fact("retained", "custom", "C:/retained/retained/SKILL.md");
        var ambient = Fact("ambient", "builtin", "builtin://ambient", enabled: true);
        var frozen = Assert.IsType<OwnedSessionFrozenSkillInventoryV1>(
            OwnedSessionSdkPolicyV1.TryFreezeProbeInventory([retained, ambient], ["C:/retained"], proof));

        Assert.Equal(["ambient"], frozen.DisabledSkills);
        Assert.True(OwnedSessionSdkPolicyV1.ValidateExecutionInventory(
            frozen, [retained, ambient with { Enabled = false }], proof));
        Assert.False(OwnedSessionSdkPolicyV1.ValidateExecutionInventory(
            frozen, [retained with { Description = "drift" }, ambient with { Enabled = false }], proof));
        Assert.False(OwnedSessionSdkPolicyV1.ValidateExecutionInventory(
            frozen, [retained, ambient], proof));
        Assert.False(OwnedSessionSdkPolicyV1.ValidateExecutionInventory(
            frozen, [retained, Fact("new", "builtin", "builtin://new", enabled: false)], proof));
    }

    [Fact]
    public void ValidateExecutionInventory_ReprovesEveryRetainedEntry()
    {
        var proof = new CountingProofProvider("first", "second");
        var facts = new[]
        {
            Fact("first", "custom", "C:/retained/first/SKILL.md"),
            Fact("second", "custom", "C:/retained/second/SKILL.md"),
        };
        var frozen = Assert.IsType<OwnedSessionFrozenSkillInventoryV1>(
            OwnedSessionSdkPolicyV1.TryFreezeProbeInventory(facts, ["C:/retained"], proof));
        proof.ProvedNames.Clear();

        Assert.True(OwnedSessionSdkPolicyV1.ValidateExecutionInventory(frozen, facts, proof));
        Assert.Equal(["first", "second"], proof.ProvedNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task OwnedSessionFacade_CreateAndSend_UsesPreCreateCallbackAndAutopilotPrompt()
    {
        var client = new RecordingOwnedClient();
        var callback = new Action<SessionEvent>(_ => { });
        var config = OwnedSessionSdkPolicyV1.CreateExecutionConfig(BaseConfig(), ["C:/retained"], [], callback);

        await using (var session = await client.CreateSessionAsync(config, CancellationToken.None))
        {
            await session.SendAndWaitAsync("unchanged prompt", TimeSpan.FromSeconds(3), CancellationToken.None);
            Assert.Same(callback, client.ConfigAtCreation!.OnEvent);
            Assert.Equal("unchanged prompt", client.Session.Message!.Prompt);
            Assert.Equal(AgentMode.Autopilot, client.Session.Message.AgentMode);
        }
        Assert.Equal(1, client.Session.DisposeCalls);
    }

    private static SessionConfig BaseConfig() => new()
    {
        Tools = [],
        AvailableTools = new ToolSet { "custom:raw" },
        Model = "model",
        WorkingDirectory = "C:/owned",
        Streaming = true,
    };

    private static CopilotDiscoveredSkillFactV1 Fact(
        string name, string source, string path, bool enabled = true) =>
        new(name, source, path, null, "description", "hint", enabled, true);

    private sealed class DictionaryProofProvider(params string[] retained) : IOwnedSessionSkillProofProviderV1
    {
        public bool TryProve(CopilotDiscoveredSkillFactV1 fact, IReadOnlyList<string> roots,
            out OwnedSessionSkillProofV1? proof)
        {
            proof = null;
            if (!retained.Contains(fact.Name, StringComparer.Ordinal)) return false;
            proof = new(fact.Path, "revision", "content", "digest");
            return true;
        }
    }

    private sealed class CountingProofProvider(params string[] retained) : IOwnedSessionSkillProofProviderV1
    {
        public List<string> ProvedNames { get; } = [];
        public bool TryProve(CopilotDiscoveredSkillFactV1 fact, IReadOnlyList<string> roots,
            out OwnedSessionSkillProofV1? proof)
        {
            ProvedNames.Add(fact.Name);
            proof = retained.Contains(fact.Name, StringComparer.Ordinal)
                ? new(fact.Path, "revision", "content", "digest")
                : null;
            return proof is not null;
        }
    }

    private sealed class RecordingOwnedClient : IOwnedCopilotClientV1
    {
        public RecordingOwnedSession Session { get; } = new();
        public SessionConfig? ConfigAtCreation { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult<CopilotRuntimeStatusObservationV1?>(null);
        public Task<IOwnedCopilotSessionV1> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
        {
            ConfigAtCreation = config;
            return Task.FromResult<IOwnedCopilotSessionV1>(Session);
        }
        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingOwnedSession : IOwnedCopilotSessionV1
    {
        public string SessionId => "session";
        public MessageOptions? Message { get; private set; }
        public int DisposeCalls { get; private set; }
        public Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> ListSkillsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CopilotDiscoveredSkillFactV1>?>([]);
        public Task EnsureSkillsLoadedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendAndWaitAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Message = new MessageOptions { Prompt = prompt, AgentMode = AgentMode.Autopilot };
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
    }
}
