using CopilotAgentObservability.ConfigCli.Setup.Contracts;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupContractValidationTests
{
    [Fact]
    public void Serialize_WhenTargetCountExceedsContractLimit_RejectsWithoutSerializingInput()
    {
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001");
        var result = CreatePlanResult(Enumerable.Repeat(target, 17).ToArray());

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-6000-8000-000000000001")]
    [InlineData("00000000-0000-7000-7000-000000000001")]
    public void Serialize_WhenChangeSetIdIsNotCanonicalUuidV7_RejectsWithoutEchoingIt(string changeSetId)
    {
        var result = CreatePlanResult([CreateWritableTarget("00000000-0000-7000-8000-000000000001")]) with { ChangeSetId = changeSetId };

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
        Assert.DoesNotContain(changeSetId, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://127.0.0.1:4320")]
    [InlineData("http://example.test:4320")]
    [InlineData("http://secret@127.0.0.1:4320")]
    public void Serialize_WhenEndpointIsNotCredentialFreeLoopbackHttp_RejectsWithoutEchoingIt(string endpoint)
    {
        var result = CreatePlanResult([CreateWritableTarget("00000000-0000-7000-8000-000000000001") with { Endpoint = endpoint }]);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
        Assert.DoesNotContain(endpoint, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:4320")]
    [InlineData("http://localhost:4320")]
    [InlineData("http://[::1]:4320")]
    public void Serialize_WhenEndpointIsCredentialFreeLoopbackHttp_PreservesIt(string endpoint)
    {
        var result = CreatePlanResult([CreateWritableTarget("00000000-0000-7000-8000-000000000001") with { Endpoint = endpoint }]);

        var json = SetupJson.Serialize(result);

        Assert.Contains(endpoint, json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:\\Users\\example\\settings.json")]
    [InlineData("raw-secret-marker")]
    [InlineData("Authorization: bearer marker")]
    [InlineData("System.Exception: marker")]
    public void Serialize_WhenRepositorySafeFieldContainsSensitiveOrPathMarker_RejectsWithoutEchoingIt(string value)
    {
        var result = CreatePlanResult([CreateWritableTarget("00000000-0000-7000-8000-000000000001") with { TargetLabel = value }]);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
        Assert.DoesNotContain(value, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_WhenGuidanceTargetHasChanges_RejectsStructuralConflict()
    {
        var guidance = CreateGuidanceTarget() with { Changes = [new SetupMemberChangeResult("setting", SetupOperation.NoOp, "absent", "absent", "none", false)] };
        var result = CreatePlanResult([guidance]);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Fact]
    public void Serialize_WhenWritableTargetHasGuidance_RejectsStructuralConflict()
    {
        var writable = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with { Guidance = new SetupGuidance("caller_managed_sample", "dotnet", "sample") };
        var result = CreatePlanResult([writable]);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Fact]
    public void Serialize_WhenTargetHasMoreThanThirtyTwoChanges_Rejects()
    {
        var changes = Enumerable.Range(1, 33)
            .Select(index => new SetupMemberChangeResult($"setting{index}", SetupOperation.Replace, "present_different", "configured_loopback", "none", false))
            .ToArray();
        var result = CreatePlanResult([CreateWritableTarget("00000000-0000-7000-8000-000000000001") with { Changes = changes }]);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Fact]
    public void Serialize_WhenStatusHasMoreThanOneHundredEntries_Rejects()
    {
        var entry = new SetupChangeSetStatusResult(
            "00000000-0000-7000-8000-000000000003", "github-copilot", "all", "2026-07-12T00:00:00Z", "2026-07-12T00:01:00Z",
            SetupChangeSetState.Applied, null, SetupCurrentState.NotApplicable, false, [CreateGuidanceTarget()]);
        var result = new SetupCommandResult(SetupCommand.Status, true, SetupCodes.StatusReady, null, null, null, "github-copilot", [], Enumerable.Repeat(entry, 101).ToArray(), [], [], true);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Fact]
    public void Serialize_WhenPlanGuidanceSampleIsMissing_Rejects()
    {
        var result = CreatePlanResult([CreateGuidanceTarget() with { Guidance = new SetupGuidance("caller_managed_sample", "dotnet", null!) }]);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Fact]
    public void Serialize_WhenRecoveryCorrelationDoesNotMatchCode_Rejects()
    {
        var result = CreatePlanResult([]) with
        {
            ChangeSetId = null,
            Code = SetupCodes.InterruptedApplyRecovered,
            RecoveredChangeSetId = "00000000-0000-7000-8000-000000000002",
            RecoveryOperation = SetupRecoveryOperation.Rollback,
        };

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Fact]
    public void Serialize_WhenSuccessCodeDoesNotBelongToCommand_Rejects()
    {
        var result = CreatePlanResult([]) with { Code = SetupCodes.ApplySucceeded };

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Fact]
    public void Serialize_WhenStatusCurrentStateDoesNotAggregateTargets_Rejects()
    {
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with
        {
            ReferenceState = SetupReferenceState.Desired,
            CurrentState = SetupCurrentState.Stale,
            RollbackAvailable = false,
        };
        var status = new SetupChangeSetStatusResult(
            "00000000-0000-7000-8000-000000000003", "github-copilot", "all", "2026-07-12T00:00:00Z", "2026-07-12T00:01:00Z",
            SetupChangeSetState.Applied, null, SetupCurrentState.Current, false, [target]);
        var result = new SetupCommandResult(SetupCommand.Status, true, SetupCodes.StatusReady, null, null, null, "github-copilot", [], [status], [], [], false);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Fact]
    public void Serialize_WhenAppliedStatusUsesBaseReference_Rejects()
    {
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with
        {
            ReferenceState = SetupReferenceState.Base,
            CurrentState = SetupCurrentState.Current,
        };
        var status = new SetupChangeSetStatusResult(
            "00000000-0000-7000-8000-000000000003", "github-copilot", "all", "2026-07-12T00:00:00Z", "2026-07-12T00:01:00Z",
            SetupChangeSetState.Applied, null, SetupCurrentState.Current, true, [target]);
        var result = new SetupCommandResult(SetupCommand.Status, true, SetupCodes.StatusReady, null, null, null, "github-copilot", [], [status], [], [], false);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Fact]
    public void Serialize_WhenAppliedStatusClaimsRollbackForStaleTarget_Rejects()
    {
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with
        {
            ReferenceState = SetupReferenceState.Desired,
            CurrentState = SetupCurrentState.Stale,
            RollbackAvailable = true,
        };
        var status = new SetupChangeSetStatusResult(
            "00000000-0000-7000-8000-000000000003", "github-copilot", "all", "2026-07-12T00:00:00Z", "2026-07-12T00:01:00Z",
            SetupChangeSetState.Applied, null, SetupCurrentState.Stale, true, [target]);
        var result = new SetupCommandResult(SetupCommand.Status, true, SetupCodes.StatusReady, null, null, null, "github-copilot", [], [status], [], [], false);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Fact]
    public void Serialize_ValidStatusFixture_PreservesSerialization()
    {
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with
        {
            ReferenceState = SetupReferenceState.Desired,
            CurrentState = SetupCurrentState.Current,
        };
        var status = new SetupChangeSetStatusResult(
            "00000000-0000-7000-8000-000000000003", "github-copilot", "all", "2026-07-12T00:00:00Z", "2026-07-12T00:01:00Z",
            SetupChangeSetState.Applied, null, SetupCurrentState.Current, true, [target]);
        var result = new SetupCommandResult(SetupCommand.Status, true, SetupCodes.StatusReady, null, null, null, "github-copilot", [], [status], [], [], false);

        var json = SetupJson.Serialize(result);

        Assert.Contains("\"change_set_id\":\"00000000-0000-7000-8000-000000000003\"", json, StringComparison.Ordinal);
    }

    private static SetupCommandResult CreatePlanResult(IReadOnlyList<SetupTargetResult> targets) => new(
        SetupCommand.Plan, true, SetupCodes.PlanReady, "00000000-0000-7000-8000-000000000002", null, null, "github-copilot",
        targets, [], [], [], false);

    private static SetupTargetResult CreateWritableTarget(string recordId) => new(
        recordId, SetupTargetKind.Json, "vscode-user-settings", true, "1.128.0", SetupOperation.Replace,
        SetupEffectiveSource.UserSetting, null, null, SetupRestartRequirement.RestartVsCode, true,
        "http://127.0.0.1:4320", null, null,
        [new SetupMemberChangeResult("github.copilot.chat.otel.otlpEndpoint", SetupOperation.Replace, "present_different", "configured_loopback", "none", false)]);

    private static SetupTargetResult CreateGuidanceTarget() => new(
        "00000000-0000-7000-8000-000000000001", SetupTargetKind.Guidance, "app-sdk-guidance", false, null,
        SetupOperation.NoOp, null, null, null, SetupRestartRequirement.None, false, null, null,
        new SetupGuidance("caller_managed_sample", "dotnet", "new CopilotClientOptions()"), []);
}
