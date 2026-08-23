using System.Text;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using GitHub.Copilot;

namespace CopilotAgentObservability.LocalMonitor.Tests.SkillRuntime;

public sealed class OwnedSessionCallbackStateV1Tests
{
    [Fact]
    public void Callback_ExactTypedSequence_FreezesPreparedBytesWithoutRetainingEvents()
    {
        var state = CreateState();
        var start = Start();
        var invoked = Invoked();
        var terminal = Terminal();

        state.OnEvent(start);
        state.OnEvent(invoked);
        state.OnEvent(terminal);
        Assert.True(state.TryBindCreatedSession("session"));
        var prepared = Assert.IsType<OwnedSessionPreparedImportV1>(state.TryFreeze());

        start.Data = StartData("mutated", "mutated");
        invoked.Data = new SkillInvokedData { Name = "mutated", Content = "mutated", Path = "mutated" };
        terminal.Data = new SessionTaskCompleteData { Success = false };
        Assert.Equal("start", Encoding.UTF8.GetString(prepared.StartEnvelopeUtf8.Span));
        Assert.Equal("invocation", Encoding.UTF8.GetString(prepared.Bodies[0].BodyUtf8.Span));
        Assert.Equal("terminal", Encoding.UTF8.GetString(prepared.TerminalEnvelopeUtf8.Span));
    }

    [Theory]
    [InlineData("terminal-before-start")]
    [InlineData("unsuccessful-terminal")]
    [InlineData("failure-event")]
    [InlineData("post-terminal")]
    [InlineData("duplicate-terminal")]
    [InlineData("session-mismatch")]
    public void Callback_InvalidOrderFailureOrIdentity_PoisonsWithoutThrowing(string scenario)
    {
        var state = CreateState();
        var exception = Record.Exception(() =>
        {
            if (scenario == "terminal-before-start") state.OnEvent(Terminal());
            else
            {
                state.OnEvent(Start());
                if (scenario == "unsuccessful-terminal")
                    state.OnEvent(new SessionTaskCompleteEvent { Data = new SessionTaskCompleteData { Success = false } });
                else if (scenario == "failure-event") state.OnEvent(new SessionErrorEvent
                    { Data = new SessionErrorData { ErrorType = "synthetic", Message = "synthetic" } });
                else
                {
                    state.OnEvent(Terminal());
                    if (scenario == "post-terminal") state.OnEvent(Invoked());
                    else if (scenario == "duplicate-terminal") state.OnEvent(Terminal());
                }
            }
        });

        Assert.Null(exception);
        Assert.False(state.TryBindCreatedSession(scenario == "session-mismatch" ? "other" : "session"));
        Assert.Null(state.TryFreeze());
    }

    [Fact]
    public void Callback_ProofOrPreparationException_PoisonsWithoutEscaping()
    {
        var state = CreateState(throwPreparation: true);
        state.OnEvent(Start());

        Assert.Null(Record.Exception(() => state.OnEvent(Invoked())));
        state.OnEvent(Terminal());
        Assert.False(state.TryBindCreatedSession("session"));
        Assert.Null(state.TryFreeze());
    }

    [Fact]
    public void Callback_BenignEventsIncludingPostTerminal_AreIgnored()
    {
        var state = CreateState();
        state.OnEvent(new SessionModelChangeEvent { Data = new SessionModelChangeData { NewModel = "model" } });
        state.OnEvent(Start());
        state.OnEvent(Terminal());
        state.OnEvent(new SessionIdleEvent { Data = new SessionIdleData() });
        Assert.True(state.TryBindCreatedSession("session"));

        Assert.NotNull(state.TryFreeze());
    }

    [Theory]
    [InlineData("start")]
    [InlineData("invocation")]
    [InlineData("terminal")]
    [InlineData("failure")]
    public void Callback_RelevantEventAfterFreeze_SynchronouslyPoisonsCandidate(string kind)
    {
        var invalidations = 0;
        var state = CreateState(onPoison: () => invalidations++);
        state.OnEvent(Start());
        state.OnEvent(Terminal());
        Assert.True(state.TryBindCreatedSession("session"));
        Assert.NotNull(state.TryFreeze());

        state.OnEvent(kind switch
        {
            "start" => Start(),
            "invocation" => Invoked(),
            "terminal" => Terminal(),
            _ => new SessionErrorEvent { Data = new SessionErrorData { ErrorType = "synthetic", Message = "synthetic" } },
        });

        Assert.Equal(1, invalidations);
        Assert.True(state.IsPoisoned);
    }

    [Fact]
    public void Callback_ModelCallFailureAndAbort_Poison()
    {
        foreach (var failure in new SessionEvent[]
        {
            new ModelCallFailureEvent { Data = new ModelCallFailureData { Source = ModelCallFailureSource.TopLevel } },
            new AbortEvent { Data = new AbortData { Reason = AbortReason.UserInitiated } },
        })
        {
            var state = CreateState();
            state.OnEvent(Start());
            state.OnEvent(failure);
            Assert.False(state.TryBindCreatedSession("session"));
            Assert.Null(state.TryFreeze());
        }
    }

    [Fact]
    public void Callback_CancelDuplicateMissingAndVersionDrift_Poison()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledState = CreateState(workToken: cancelled.Token);
        cancelledState.OnEvent(Start());
        Assert.False(cancelledState.TryBindCreatedSession("session"));

        var duplicateStart = CreateState();
        duplicateStart.OnEvent(Start());
        duplicateStart.OnEvent(Start());
        Assert.False(duplicateStart.TryBindCreatedSession("session"));

        var missingTerminal = CreateState();
        missingTerminal.OnEvent(Start());
        Assert.True(missingTerminal.TryBindCreatedSession("session"));
        Assert.Null(missingTerminal.TryFreeze());

        var drift = CreateState();
        drift.OnEvent(new SessionStartEvent { Data = StartData("session", "1.0.65") });
        Assert.False(drift.TryBindCreatedSession("session"));
    }

    [Fact]
    public void Callback_InvocationDescriptorContentOrProofDrift_Poisons()
    {
        foreach (var mutation in new Action<SkillInvokedData>[]
        {
            data => data.Name = "other",
            data => data.Source = "builtin",
            data => data.Path = "C:/other/SKILL.md",
            data => data.Content = "drift",
            data => data.Description = "drift",
        })
        {
            var state = CreateState();
            state.OnEvent(Start());
            var invoked = Invoked();
            mutation(invoked.Data);
            state.OnEvent(invoked);
            Assert.False(state.TryBindCreatedSession("session"));
        }

        var proof = new MutableProof(new("C:/retained/SKILL.md", "revision", "content", "digest"));
        var proofState = CreateState(proofProvider: proof);
        proofState.OnEvent(Start());
        proof.Value = proof.Value with { RootRevision = "drift" };
        proofState.OnEvent(Invoked());
        Assert.False(proofState.TryBindCreatedSession("session"));
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void Callback_InvocationBoundary_IsExact(int count, bool succeeds)
    {
        var state = CreateState();
        state.OnEvent(Start());
        for (var index = 0; index < count; index++) state.OnEvent(Invoked());
        state.OnEvent(Terminal());

        Assert.Equal(succeeds, state.TryBindCreatedSession("session"));
        Assert.Equal(succeeds, state.TryFreeze() is not null);
    }

    [Fact]
    public async Task Callback_ConcurrentInvocations_AssignsOneLinearOrdinalSequence()
    {
        var state = CreateState();
        state.OnEvent(Start());
        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => state.OnEvent(Invoked()))));
        state.OnEvent(Terminal());
        Assert.True(state.TryBindCreatedSession("session"));
        var prepared = Assert.IsType<OwnedSessionPreparedImportV1>(state.TryFreeze());

        Assert.Equal(Enumerable.Range(0, 64), prepared.Bodies.Select(body => body.Ordinal));
    }

    private static OwnedSessionCallbackStateV1 CreateState(
        bool throwPreparation = false,
        IOwnedSessionSkillProofProviderV1? proofProvider = null,
        CancellationToken workToken = default,
        Action? onPoison = null)
    {
        var retained = new CopilotDiscoveredSkillFactV1("retained", "custom", "C:/retained/SKILL.md",
            null, "description", "hint", true, true);
        var proof = new OwnedSessionSkillProofV1(retained.Path, "revision", "content", "digest");
        var inventory = new OwnedSessionFrozenSkillInventoryV1(
            new Dictionary<string, OwnedSessionFrozenSkillV1> { ["retained"] = new(retained, proof) },
            new Dictionary<string, CopilotDiscoveredSkillFactV1> { ["retained"] = retained }, []);
        return new(inventory, proofProvider ?? new FixedProof(proof), "1.0.75",
            _ => Encoding.UTF8.GetBytes("start"),
            _ => throwPreparation ? throw new InvalidOperationException("synthetic") : Encoding.UTF8.GetBytes("invocation"),
            _ => Encoding.UTF8.GetBytes("terminal"), workToken, onPoison);
    }

    private static SessionStartEvent Start() => new()
    {
        Data = StartData("session", "1.0.75"),
    };

    private static SkillInvokedEvent Invoked() => new()
    {
        Data = new SkillInvokedData
        {
            Name = "retained", Source = "custom", Path = "C:/retained/SKILL.md", Content = "content",
            Description = "description",
        },
    };

    private static SessionTaskCompleteEvent Terminal() => new()
    {
        Data = new SessionTaskCompleteData { Success = true },
    };

    private static SessionStartData StartData(string sessionId, string version) => new()
    {
        SessionId = sessionId,
        CopilotVersion = version,
        Producer = "copilot",
        StartTime = DateTimeOffset.UnixEpoch,
        Version = 1,
    };

    private sealed class FixedProof(OwnedSessionSkillProofV1 value) : IOwnedSessionSkillProofProviderV1
    {
        public bool TryProve(CopilotDiscoveredSkillFactV1 fact, IReadOnlyList<string> roots,
            out OwnedSessionSkillProofV1? proof) { proof = value; return true; }
    }

    private sealed class MutableProof(OwnedSessionSkillProofV1 value) : IOwnedSessionSkillProofProviderV1
    {
        public OwnedSessionSkillProofV1 Value { get; set; } = value;
        public bool TryProve(CopilotDiscoveredSkillFactV1 fact, IReadOnlyList<string> roots,
            out OwnedSessionSkillProofV1? proof) { proof = Value; return true; }
    }
}
