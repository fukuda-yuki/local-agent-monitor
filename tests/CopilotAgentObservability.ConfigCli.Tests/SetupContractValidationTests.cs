using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Capabilities;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupContractValidationTests
{
    public static TheoryData<string> InvalidAdapterIds => new()
    {
        "UPPERCASE",
        "adapter_name",
        "-adapter",
        "adapter-",
        "adapter--name",
        "adapter-é",
        new string('a', 129),
    };

    private const string AppSdkGuidanceSample = """
        new CopilotClientOptions
        {
            Telemetry = new TelemetryConfig
            {
                OtlpEndpoint = "http://127.0.0.1:4320",
                OtlpProtocol = "http/protobuf"
            }
        }
        """;

    [Theory]
    [InlineData(SetupTargetKind.Json, "vscode-stable-default-user-settings", "github-copilot-vscode.json")]
    [InlineData(SetupTargetKind.Json, "vscode-insiders-default-user-settings", "github-copilot-vscode.json")]
    [InlineData(SetupTargetKind.Env, "copilot-cli-user-environment", "github-copilot-cli.json")]
    public void Serialize_PlanAcceptsExactGitHubCopilotSurfaceLabels(
        SetupTargetKind targetKind,
        string targetLabel,
        string manifestFileName)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(GetManifestPath(manifestFileName)));
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with
        {
            TargetKind = targetKind,
            TargetLabel = targetLabel,
            EffectiveSource = targetKind == SetupTargetKind.Env ? SetupEffectiveSource.Environment : SetupEffectiveSource.UserSetting,
            RestartRequirement = targetKind == SetupTargetKind.Env ? SetupRestartRequirement.RestartTerminalSession : SetupRestartRequirement.RestartVsCode,
            ExpectedResult = manifest.RootElement.Clone(),
        };

        var json = SetupJson.Serialize(CreatePlanResult([target]));

        using var serialized = JsonDocument.Parse(json);
        Assert.True(SourceCapabilityManifestLoader.MatchesCanonical(serialized.RootElement.GetProperty("targets")[0].GetProperty("expected_result")));
    }

    [Theory]
    [InlineData(SetupTargetKind.Json, "vscode-user-settings", "github-copilot-vscode.json")]
    [InlineData(SetupTargetKind.Env, "user-environment", "github-copilot-cli.json")]
    public void Serialize_WhenManifestUsesObsoleteSurfaceLabel_Rejects(
        SetupTargetKind targetKind,
        string targetLabel,
        string manifestFileName)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(GetManifestPath(manifestFileName)));
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with
        {
            TargetKind = targetKind,
            TargetLabel = targetLabel,
            EffectiveSource = targetKind == SetupTargetKind.Env ? SetupEffectiveSource.Environment : SetupEffectiveSource.UserSetting,
            RestartRequirement = targetKind == SetupTargetKind.Env ? SetupRestartRequirement.RestartTerminalSession : SetupRestartRequirement.RestartVsCode,
            ExpectedResult = manifest.RootElement.Clone(),
        };

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(CreatePlanResult([target])));

        Assert.Equal(SetupContractValidator.InvalidContractCode, exception.Message);
    }

    [Theory]
    [InlineData("signal")]
    [InlineData("source_adapter")]
    [InlineData("array_order")]
    public void Serialize_WhenExpectedResultDiffersFromCanonicalManifest_RejectsWithoutEchoingIt(string mutation)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(GetManifestPath("github-copilot-vscode.json")));
        var node = JsonNode.Parse(manifest.RootElement.GetRawText())!.AsObject();
        switch (mutation)
        {
            case "signal":
                node["signals"]!["trace"]!["availability"] = "unknown";
                break;
            case "source_adapter":
                node["source_adapter"] = "otel-http";
                break;
            case "array_order":
                var statuses = node["completeness"]!["statuses"]!.AsArray();
                var first = statuses[0]!.DeepClone();
                statuses[0] = statuses[1]!.DeepClone();
                statuses[1] = first;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        using var candidate = JsonDocument.Parse(node.ToJsonString());
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with { ExpectedResult = candidate.RootElement.Clone() };

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(CreatePlanResult([target])));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Theory]
    [InlineData("password", "marker")]
    [InlineData("credential", "marker")]
    [InlineData("api_key", "marker")]
    [InlineData("token", "marker")]
    [InlineData("authorization", "marker")]
    [InlineData("user_email", "marker")]
    [InlineData("path", "C:\\Users\\example\\settings.json")]
    [InlineData("path", "/home/example/settings.json")]
    [InlineData("path", "\\\\server\\share\\settings.json")]
    [InlineData("path", "\\\\.\\PhysicalDrive0")]
    public void Serialize_WhenExpectedResultContainsNonCanonicalSecurityOrPathField_RejectsWithoutEchoingIt(string fieldName, string fieldValue)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(GetManifestPath("github-copilot-vscode.json")));
        var manifestJson = manifest.RootElement.GetRawText();
        using var mutated = JsonDocument.Parse($"{{\"{fieldName}\":{JsonSerializer.Serialize(fieldValue)},{manifestJson[1..]}");
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with { ExpectedResult = mutated.RootElement.Clone() };

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(CreatePlanResult([target])));

        Assert.Equal("setup_contract_invalid", exception.Message);
        Assert.DoesNotContain(fieldValue, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_WhenExpectedResultAddsSensitiveField_RejectsWithoutEchoingIt()
    {
        const string marker = "password";
        using var manifest = JsonDocument.Parse(File.ReadAllText(GetManifestPath("github-copilot-vscode.json")));
        using var mutated = JsonDocument.Parse(manifest.RootElement.GetRawText().Replace("{", "{\"password\":\"marker\",", StringComparison.Ordinal));
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with { ExpectedResult = mutated.RootElement.Clone() };

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(CreatePlanResult([target])));

        Assert.Equal("setup_contract_invalid", exception.Message);
        Assert.DoesNotContain(marker, exception.ToString(), StringComparison.Ordinal);
    }

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
    [InlineData("http://127.0.0.1")]
    [InlineData("http://localhost/")]
    [InlineData("http://[::1]:4320/")]
    [InlineData("http://127.0.0.1:4320/v1/traces")]
    [InlineData("http://127.0.0.1:4320/%2e")]
    [InlineData("http://127.0.0.1.:4320")]
    [InlineData("http://127.0.0.2:4320")]
    public void Serialize_WhenEndpointIsNotCanonicalExplicitLoopbackOrigin_Rejects(string endpoint)
    {
        var result = CreatePlanResult([CreateWritableTarget("00000000-0000-7000-8000-000000000001") with { Endpoint = endpoint }]);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Theory]
    [InlineData("http://127.0.0.1:4320\n")]
    [InlineData("http://127.0.0.1:4320\r\n")]
    [InlineData("http://127.0.0.1:4320\r")]
    [InlineData("http://127.0.0.1:4320\0")]
    [InlineData("\thttp://127.0.0.1:4320")]
    [InlineData("http://127.0.0.1:4320\t")]
    public void Serialize_WhenEndpointContainsControlPrefixOrSuffix_RejectsWithoutEchoingIt(string endpoint)
    {
        var result = CreatePlanResult([CreateWritableTarget("00000000-0000-7000-8000-000000000001") with { Endpoint = endpoint }]);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
        Assert.DoesNotContain(endpoint, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:\\Users\\example\\settings.json")]
    [InlineData("Authorization: bearer marker")]
    [InlineData("System.Exception: marker")]
    public void Serialize_WhenFixedIdentifierIsNotSanitized_RejectsWithoutEchoingIt(string value)
    {
        var result = CreatePlanResult([CreateWritableTarget("00000000-0000-7000-8000-000000000001") with { TargetLabel = value }]);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
        Assert.DoesNotContain(value, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_WhenPlanAdapterStartsWithDigit_PreservesAdapterSlug()
    {
        var result = CreatePlanResult([]) with { Adapter = "1" };

        using var document = JsonDocument.Parse(SetupJson.Serialize(result));

        Assert.Equal("1", document.RootElement.GetProperty("adapter").GetString());
    }

    [Fact]
    public void Serialize_WhenUnsupportedAdapterStartsWithDigit_PreservesArtifactFreeFailure()
    {
        var result = CreatePlanResult([]) with
        {
            Success = false,
            Code = SetupCodes.UnsupportedAdapter,
            ChangeSetId = null,
            Adapter = "1",
        };

        using var document = JsonDocument.Parse(SetupJson.Serialize(result));

        Assert.Equal("unsupported_adapter", document.RootElement.GetProperty("code").GetString());
        Assert.Equal("1", document.RootElement.GetProperty("adapter").GetString());
    }

    [Fact]
    public void Serialize_WhenStatusEntryAdapterStartsWithDigit_PreservesAdapterSlug()
    {
        var status = CreateAppliedStatus("2026-07-12T00:00:00Z", "2026-07-12T00:01:00Z") with
        {
            Adapter = "1",
        };
        var result = new SetupCommandResult(
            SetupCommand.Status,
            true,
            SetupCodes.StatusReady,
            null,
            null,
            null,
            "1",
            [],
            [status],
            [],
            [],
            false);

        using var document = JsonDocument.Parse(SetupJson.Serialize(result));

        Assert.Equal("1", document.RootElement.GetProperty("adapter").GetString());
        Assert.Equal("1", document.RootElement.GetProperty("change_sets")[0].GetProperty("adapter").GetString());
    }

    [Fact]
    public void Serialize_WhenAdapterSlugIsMaximumLength_PreservesTopLevelAndStatusAdapter()
    {
        var adapter = $"1-{new string('a', 126)}";
        var status = CreateAppliedStatus("2026-07-12T00:00:00Z", "2026-07-12T00:01:00Z") with
        {
            Adapter = adapter,
        };
        var result = new SetupCommandResult(
            SetupCommand.Status,
            true,
            SetupCodes.StatusReady,
            null,
            null,
            null,
            adapter,
            [],
            [status],
            [],
            [],
            false);

        using var document = JsonDocument.Parse(SetupJson.Serialize(result));

        Assert.Equal(128, adapter.Length);
        Assert.Equal(adapter, document.RootElement.GetProperty("adapter").GetString());
        Assert.Equal(adapter, document.RootElement.GetProperty("change_sets")[0].GetProperty("adapter").GetString());
    }

    [Fact]
    public void Serialize_WhenAdapterSlugIsEmpty_RejectsTopLevelAndStatusAdapter()
    {
        var topLevelResult = CreatePlanResult([]) with { Adapter = string.Empty };
        var status = CreateAppliedStatus("2026-07-12T00:00:00Z", "2026-07-12T00:01:00Z") with
        {
            Adapter = string.Empty,
        };
        var statusResult = new SetupCommandResult(
            SetupCommand.Status,
            true,
            SetupCodes.StatusReady,
            null,
            null,
            null,
            "github-copilot",
            [],
            [status],
            [],
            [],
            false);

        var topLevelException = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(topLevelResult));
        var statusException = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(statusResult));

        Assert.Equal(SetupContractValidator.InvalidContractCode, topLevelException.Message);
        Assert.Equal(SetupContractValidator.InvalidContractCode, statusException.Message);
    }

    [Theory]
    [MemberData(nameof(InvalidAdapterIds))]
    public void Serialize_WhenTopLevelAdapterSlugIsMalformed_RejectsWithoutEcho(string adapter)
    {
        var result = CreatePlanResult([]) with { Adapter = adapter };

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal(SetupContractValidator.InvalidContractCode, exception.Message);
        Assert.DoesNotContain(adapter, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidAdapterIds))]
    public void Serialize_WhenStatusAdapterSlugIsMalformed_RejectsWithoutEcho(string adapter)
    {
        var status = CreateAppliedStatus("2026-07-12T00:00:00Z", "2026-07-12T00:01:00Z") with
        {
            Adapter = adapter,
        };
        var result = new SetupCommandResult(
            SetupCommand.Status,
            true,
            SetupCodes.StatusReady,
            null,
            null,
            null,
            "github-copilot",
            [],
            [status],
            [],
            [],
            false);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal(SetupContractValidator.InvalidContractCode, exception.Message);
        Assert.DoesNotContain(adapter, exception.ToString(), StringComparison.Ordinal);
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
    public void Serialize_WhenDetectedVersionIsExactlyOneHundredTwentyEightUtf16CodeUnits_PreservesIt()
    {
        var detectedVersion = "1.0.0+" + new string('a', 122);
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with { DetectedVersion = detectedVersion };

        using var document = JsonDocument.Parse(SetupJson.Serialize(CreatePlanResult([target])));

        Assert.Equal(detectedVersion, document.RootElement.GetProperty("targets")[0].GetProperty("detected_version").GetString());
        Assert.Equal(128, detectedVersion.Length);
    }

    [Fact]
    public void Serialize_WhenDetectedVersionExceedsOneHundredTwentyEightUtf16CodeUnits_Rejects()
    {
        var detectedVersion = "1.0.0+" + new string('a', 123);
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with { DetectedVersion = detectedVersion };

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(CreatePlanResult([target])));

        Assert.Equal("setup_contract_invalid", exception.Message);
        Assert.Equal(129, detectedVersion.Length);
    }

    [Fact]
    public void Serialize_WhenCanonicalManifestSurfaceDoesNotMatchTarget_Rejects()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(GetManifestPath("github-copilot-cli.json")));
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with { ExpectedResult = manifest.RootElement.Clone() };

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(CreatePlanResult([target])));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Theory]
    [InlineData(SetupCommand.Apply)]
    [InlineData(SetupCommand.Rollback)]
    public void Serialize_ApplyAndRollbackAcceptAndPreserveStrictHistoricalManifest(SetupCommand command)
    {
        var historical = CreateHistoricalManifest();
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with
        {
            ExpectedResult = historical,
        };

        using var serialized = JsonDocument.Parse(SetupJson.Serialize(CreateMutationResult(command, target)));
        var actual = serialized.RootElement.GetProperty("targets")[0].GetProperty("expected_result");

        Assert.Equal(historical.GetRawText(), actual.GetRawText());
    }

    [Fact]
    public void Serialize_PlanRejectsStrictHistoricalManifestThatDiffersFromCurrentEmbeddedManifest()
    {
        var historical = CreateHistoricalManifest();
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with
        {
            ExpectedResult = historical,
        };

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(CreatePlanResult([target])));

        Assert.Equal(SetupContractValidator.InvalidContractCode, exception.Message);
    }

    [Theory]
    [InlineData(SetupCommand.Apply, "cross-surface")]
    [InlineData(SetupCommand.Apply, "unknown-label")]
    [InlineData(SetupCommand.Rollback, "cross-surface")]
    [InlineData(SetupCommand.Rollback, "unknown-label")]
    public void Serialize_ApplyAndRollbackRejectHistoricalManifestOutsideExactSurfaceLabel(
        SetupCommand command,
        string mismatch)
    {
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with
        {
            TargetLabel = mismatch == "unknown-label"
                ? "future-vscode-user-settings"
                : "vscode-stable-default-user-settings",
            ExpectedResult = mismatch == "cross-surface"
                ? LoadManifest("github-copilot-cli.json")
                : CreateHistoricalManifest(),
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SetupJson.Serialize(CreateMutationResult(command, target)));

        Assert.Equal(SetupContractValidator.InvalidContractCode, exception.Message);
    }

    [Theory]
    [InlineData(SetupCommand.Plan)]
    [InlineData(SetupCommand.Apply)]
    [InlineData(SetupCommand.Rollback)]
    public void Serialize_WhenRecognizedWritableTargetOmitsExpectedResult_Rejects(SetupCommand command)
    {
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with
        {
            ExpectedResult = null,
        };
        var result = CreateMutationResult(command, target);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Fact]
    public void Serialize_StatusTargetAcceptsStrictHistoricalManifestWithoutCurrentEquality()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(GetManifestPath("github-copilot-vscode.json")))!.AsObject();
        manifest["support_status"] = "planned";
        manifest["stability"] = "preview";
        using var historical = JsonDocument.Parse(manifest.ToJsonString());
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with
        {
            ReferenceState = SetupReferenceState.Desired,
            CurrentState = SetupCurrentState.Current,
            ExpectedResult = historical.RootElement.Clone(),
        };
        var status = new SetupChangeSetStatusResult(
            "00000000-0000-7000-8000-000000000003", "github-copilot", "vscode",
            "2026-07-12T00:00:00Z", "2026-07-12T00:01:00Z", SetupChangeSetState.Applied,
            null, SetupCurrentState.Current, true, [target]);
        var result = new SetupCommandResult(
            SetupCommand.Status, true, SetupCodes.StatusReady, null, null, null, "github-copilot",
            [], [status], [], [], false);

        using var serialized = JsonDocument.Parse(SetupJson.Serialize(result));

        Assert.Equal("planned", serialized.RootElement.GetProperty("change_sets")[0].GetProperty("targets")[0]
            .GetProperty("expected_result").GetProperty("support_status").GetString());
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
    public void Serialize_AtTargetAndMemberChangeLimits_PreservesSerialization()
    {
        var changes = Enumerable.Range(1, 32)
            .Select(index => new SetupMemberChangeResult($"setting{index}", SetupOperation.Replace, "present_different", "configured_loopback", "none", false))
            .ToArray();
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with { Changes = changes };

        var json = SetupJson.Serialize(CreatePlanResult(Enumerable.Repeat(target, 16).ToArray()));

        using var document = JsonDocument.Parse(json);
        Assert.Equal(16, document.RootElement.GetProperty("targets").GetArrayLength());
        Assert.Equal(32, document.RootElement.GetProperty("targets")[0].GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public void Serialize_WhenStatusHasMoreThanOneHundredEntries_Rejects()
    {
        var entry = new SetupChangeSetStatusResult(
            "00000000-0000-7000-8000-000000000003", "github-copilot", "all", "2026-07-12T00:00:00Z", "2026-07-12T00:01:00Z",
            SetupChangeSetState.Applied, null, SetupCurrentState.NotApplicable, false, [CreateGuidanceTarget() with { ReferenceState = SetupReferenceState.None, CurrentState = SetupCurrentState.NotApplicable }]);
        var result = new SetupCommandResult(SetupCommand.Status, true, SetupCodes.StatusReady, null, null, null, "github-copilot", [], Enumerable.Repeat(entry, 101).ToArray(), [], [], true);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Fact]
    public void Serialize_AtStatusEntryLimit_PreservesSerialization()
    {
        var entry = new SetupChangeSetStatusResult(
            "00000000-0000-7000-8000-000000000003", "github-copilot", "all", "2026-07-12T00:00:00Z", "2026-07-12T00:01:00Z",
            SetupChangeSetState.Applied, null, SetupCurrentState.NotApplicable, false, [CreateGuidanceTarget() with { ReferenceState = SetupReferenceState.None, CurrentState = SetupCurrentState.NotApplicable }]);
        var result = new SetupCommandResult(SetupCommand.Status, true, SetupCodes.StatusReady, null, null, null, "github-copilot", [], Enumerable.Repeat(entry, 100).ToArray(), [], [], false);

        using var document = JsonDocument.Parse(SetupJson.Serialize(result));

        Assert.Equal(100, document.RootElement.GetProperty("change_sets").GetArrayLength());
    }

    [Fact]
    public void Serialize_WhenPlanGuidanceSampleIsMissing_Rejects()
    {
        var result = CreatePlanResult([CreateGuidanceTarget() with { Guidance = new SetupGuidance("caller_managed_sample", "dotnet", null!) }]);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Theory]
    [InlineData("other_kind", "dotnet")]
    [InlineData("caller_managed_sample", "typescript")]
    public void Serialize_WhenGuidanceMetadataDiffersFromFixedContract_Rejects(string kind, string language)
    {
        var result = CreatePlanResult(
            [CreateGuidanceTarget() with { Guidance = new SetupGuidance(kind, language, AppSdkGuidanceSample) }]);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Theory]
    [InlineData(SetupCommand.Plan)]
    [InlineData(SetupCommand.Apply)]
    [InlineData(SetupCommand.Rollback)]
    public void Serialize_WhenEmittedGuidanceSampleDiffersFromBoundedContract_Rejects(SetupCommand command)
    {
        var result = command switch
        {
            SetupCommand.Plan => CreatePlanResult([CreateGuidanceTarget() with { Guidance = new SetupGuidance("caller_managed_sample", "dotnet", "new CopilotClientOptions()") }]),
            SetupCommand.Apply => new SetupCommandResult(command, true, SetupCodes.ApplySucceeded, "00000000-0000-7000-8000-000000000002", null, null, "github-copilot", [CreateGuidanceTarget() with { Guidance = new SetupGuidance("caller_managed_sample", "dotnet", "new CopilotClientOptions()") }], [], [], [], false),
            SetupCommand.Rollback => new SetupCommandResult(command, true, SetupCodes.RollbackSucceeded, "00000000-0000-7000-8000-000000000002", null, null, "github-copilot", [CreateGuidanceTarget() with { Guidance = new SetupGuidance("caller_managed_sample", "dotnet", "new CopilotClientOptions()") }], [], [], [], false),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Fact]
    public void Serialize_WhenStatusWritableTargetOmitsReferenceOrCurrentState_Rejects()
    {
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with { ReferenceState = null, CurrentState = null };
        var status = new SetupChangeSetStatusResult("00000000-0000-7000-8000-000000000003", "github-copilot", "all", "2026-07-12T00:00:00Z", "2026-07-12T00:01:00Z", SetupChangeSetState.Applied, null, SetupCurrentState.Current, true, [target]);
        var result = new SetupCommandResult(SetupCommand.Status, true, SetupCodes.StatusReady, null, null, null, "github-copilot", [], [status], [], [], false);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Fact]
    public void Serialize_WhenUnfilteredInterruptedRecoveryFailureDoesNotProjectMatchingPartialEntry_Rejects()
    {
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with { ReferenceState = SetupReferenceState.Desired, CurrentState = SetupCurrentState.Current, RollbackAvailable = false };
        var status = new SetupChangeSetStatusResult("00000000-0000-7000-8000-000000000003", "github-copilot", "all", "2026-07-12T00:00:00Z", "2026-07-12T00:01:00Z", SetupChangeSetState.Applied, null, SetupCurrentState.Current, false, [target]);
        var result = new SetupCommandResult(SetupCommand.Status, false, SetupCodes.InterruptedRecoveryFailed, null, "00000000-0000-7000-8000-000000000002", SetupRecoveryOperation.Apply, null, [], [status], [], [], false);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Fact]
    public void Serialize_WhenInterruptedRecoveryFailureProjectsMatchingPartialEntry_PreservesSerialization()
    {
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with { ReferenceState = SetupReferenceState.None, CurrentState = SetupCurrentState.Diverged, RollbackAvailable = false };
        var status = new SetupChangeSetStatusResult("00000000-0000-7000-8000-000000000002", "github-copilot", "all", "2026-07-12T00:00:00Z", "2026-07-12T00:01:00Z", SetupChangeSetState.Partial, SetupCodes.InterruptedRecoveryFailed, SetupCurrentState.Diverged, false, [target]);
        var result = new SetupCommandResult(SetupCommand.Status, false, SetupCodes.InterruptedRecoveryFailed, null, "00000000-0000-7000-8000-000000000002", SetupRecoveryOperation.Apply, "github-copilot", [], [status], [], [], false);

        var json = SetupJson.Serialize(result);

        Assert.Contains("\"outcome_code\":\"interrupted_recovery_failed\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_WhenFilteredInterruptedRecoveryFailureOmitsNonmatchingEntry_PreservesSerialization()
    {
        var result = new SetupCommandResult(
            SetupCommand.Status,
            false,
            SetupCodes.InterruptedRecoveryFailed,
            null,
            "00000000-0000-7000-8000-000000000002",
            SetupRecoveryOperation.Apply,
            "other-adapter",
            [],
            [],
            [],
            [],
            false);

        using var document = JsonDocument.Parse(SetupJson.Serialize(result));

        Assert.Equal("other-adapter", document.RootElement.GetProperty("adapter").GetString());
        Assert.Empty(document.RootElement.GetProperty("change_sets").EnumerateArray());
    }

    [Fact]
    public void Serialize_WhenFilteredInterruptedRecoveryFailureIncludesDifferentAdapterEntry_Rejects()
    {
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with
        {
            ReferenceState = SetupReferenceState.None,
            CurrentState = SetupCurrentState.Diverged,
            RollbackAvailable = false,
        };
        var status = new SetupChangeSetStatusResult(
            "00000000-0000-7000-8000-000000000002",
            "github-copilot",
            "all",
            "2026-07-12T00:00:00Z",
            "2026-07-12T00:01:00Z",
            SetupChangeSetState.Partial,
            SetupCodes.InterruptedRecoveryFailed,
            SetupCurrentState.Diverged,
            false,
            [target]);
        var result = new SetupCommandResult(
            SetupCommand.Status,
            false,
            SetupCodes.InterruptedRecoveryFailed,
            null,
            "00000000-0000-7000-8000-000000000002",
            SetupRecoveryOperation.Apply,
            "other-adapter",
            [],
            [status],
            [],
            [],
            false);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Fact]
    public void Serialize_WhenSuccessfulRecoveryOmitsRerunAction_Rejects()
    {
        var result = CreatePlanResult([]) with
        {
            ChangeSetId = null,
            Code = SetupCodes.InterruptedApplyRecovered,
            RecoveredChangeSetId = "00000000-0000-7000-8000-000000000002",
            RecoveryOperation = SetupRecoveryOperation.Apply,
        };

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
    public void Serialize_WhenFailureCodeDoesNotBelongToCommand_Rejects()
    {
        var result = CreatePlanResult([]) with { Success = false, Code = SetupCodes.RollbackStale, ChangeSetId = null };

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Fact]
    public void ValidateAndSerialize_StatusInvalidArguments_AcceptsRepositorySafeFailure()
    {
        var result = new SetupCommandResult(
            SetupCommand.Status, false, SetupCodes.InvalidArguments, null, null, null, null,
            [], [], [], [], false);

        SetupContractValidator.Validate(result);
        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        var root = document.RootElement;

        Assert.Equal("status", root.GetProperty("command").GetString());
        Assert.Equal("invalid_arguments", root.GetProperty("code").GetString());
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("change_set_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("recovered_change_set_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("recovery_operation").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("adapter").ValueKind);
        Assert.Empty(root.GetProperty("targets").EnumerateArray());
        Assert.Empty(root.GetProperty("change_sets").EnumerateArray());
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
        Assert.Empty(root.GetProperty("next_actions").EnumerateArray());
        Assert.False(root.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData(SetupCodes.UnsupportedAdapter, "removed-adapter")]
    [InlineData(SetupCodes.UnsupportedTarget, "github-copilot")]
    public void Serialize_WhenApplyCannotUsePersistedPlan_PreservesTheArtifactFreeExceptionalResult(string code, string adapter)
    {
        var result = new SetupCommandResult(
            SetupCommand.Apply,
            false,
            code,
            "00000000-0000-7000-8000-000000000002",
            null,
            null,
            adapter,
            [],
            [],
            [],
            [],
            false);

        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        var root = document.RootElement;

        Assert.Equal("apply", root.GetProperty("command").GetString());
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(code, root.GetProperty("code").GetString());
        Assert.Equal("00000000-0000-7000-8000-000000000002", root.GetProperty("change_set_id").GetString());
        Assert.Equal(adapter, root.GetProperty("adapter").GetString());
        Assert.Empty(root.GetProperty("targets").EnumerateArray());
        Assert.Empty(root.GetProperty("change_sets").EnumerateArray());
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
        Assert.Empty(root.GetProperty("next_actions").EnumerateArray());
        Assert.False(root.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData(SetupCodes.UnsupportedAdapter)]
    [InlineData(SetupCodes.UnsupportedTarget)]
    public void Serialize_WhenApplyExceptionalResultOmitsPersistedCorrelation_Rejects(string code)
    {
        var result = new SetupCommandResult(SetupCommand.Apply, false, code, null, null, null, "github-copilot", [], [], [], [], false);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Theory]
    [InlineData(SetupCodes.UnsupportedAdapter)]
    [InlineData(SetupCodes.UnsupportedTarget)]
    public void Serialize_WhenApplyExceptionalResultCarriesArtifactsOrFollowUp_Rejects(string code)
    {
        var result = new SetupCommandResult(
            SetupCommand.Apply,
            false,
            code,
            "00000000-0000-7000-8000-000000000002",
            null,
            null,
            "github-copilot",
            [CreateWritableTarget("00000000-0000-7000-8000-000000000001")],
            [],
            ["vscode_non_default_profiles_not_modified"],
            ["review_cli_trace_protocol_override"],
            false);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Theory]
    [InlineData(SetupCodes.UnsupportedAdapter)]
    [InlineData(SetupCodes.UnsupportedTarget)]
    public void Serialize_WhenNonApplyUsesApplyOnlyExceptionalCode_Rejects(string code)
    {
        var result = new SetupCommandResult(SetupCommand.Rollback, false, code, "00000000-0000-7000-8000-000000000002", null, null, "github-copilot", [], [], [], [], false);

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
    public void Serialize_WhenPartialStatusIsDesiredAndCurrent_PreservesEmittedReferenceAndCurrentStates()
    {
        AssertPartialStatusSerialization(SetupReferenceState.Desired, SetupCurrentState.Current, "desired", "current");
    }

    [Fact]
    public void Serialize_WhenPartialStatusIsPreviousAndCurrent_PreservesEmittedReferenceAndCurrentStates()
    {
        AssertPartialStatusSerialization(SetupReferenceState.Previous, SetupCurrentState.Current, "previous", "current");
    }

    [Fact]
    public void Serialize_WhenPartialStatusIsNoneAndDiverged_PreservesEmittedReferenceAndCurrentStates()
    {
        AssertPartialStatusSerialization(SetupReferenceState.None, SetupCurrentState.Diverged, "none", "diverged");
    }

    [Fact]
    public void Serialize_WhenPartialStatusIsNoneAndUnavailable_PreservesEmittedReferenceAndCurrentStates()
    {
        AssertPartialStatusSerialization(SetupReferenceState.None, SetupCurrentState.Unavailable, "none", "unavailable");
    }

    [Theory]
    [InlineData("record_id")]
    [InlineData("change_set_id")]
    [InlineData("recovered_change_set_id")]
    [InlineData("created_at")]
    [InlineData("updated_at")]
    public void Serialize_WhenCorrelationIdentifierOrTimestampPositionIsMalformed_Rejects(string position)
    {
        var target = CreateWritableTarget(position == "record_id" ? "not-a-uuid" : "00000000-0000-7000-8000-000000000001") with
        {
            ReferenceState = SetupReferenceState.Desired,
            CurrentState = SetupCurrentState.Current,
        };
        var status = new SetupChangeSetStatusResult(
            position == "change_set_id" ? "not-a-uuid" : "00000000-0000-7000-8000-000000000003", "github-copilot", "all",
            position == "created_at" ? "2026-07-12T00:00:00+09:00" : "2026-07-12T00:00:00Z",
            position == "updated_at" ? "not-a-timestamp" : "2026-07-12T00:01:00Z",
            SetupChangeSetState.Applied, null, SetupCurrentState.Current, true, [target]);
        var result = new SetupCommandResult(SetupCommand.Status, true, SetupCodes.StatusReady, null,
            position == "recovered_change_set_id" ? "not-a-uuid" : null,
            position == "recovered_change_set_id" ? SetupRecoveryOperation.Apply : null,
            "github-copilot", [], [status], [], position == "recovered_change_set_id" ? [SetupCodes.RerunRequestedSetupCommand] : [], false);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Fact]
    public void Serialize_WhenStatusCreatedAtIsAfterUpdatedAt_Rejects()
    {
        var status = CreateAppliedStatus("2026-07-12T00:01:00Z", "2026-07-12T00:00:00Z");
        var result = new SetupCommandResult(SetupCommand.Status, true, SetupCodes.StatusReady, null, null, null, "github-copilot", [], [status], [], [], false);

        var exception = Assert.Throws<InvalidOperationException>(() => SetupJson.Serialize(result));

        Assert.Equal("setup_contract_invalid", exception.Message);
    }

    [Fact]
    public void Serialize_WhenStatusCreatedAtEqualsUpdatedAt_PreservesSerialization()
    {
        var status = CreateAppliedStatus("2026-07-12T00:00:00Z", "2026-07-12T00:00:00Z");
        var result = new SetupCommandResult(SetupCommand.Status, true, SetupCodes.StatusReady, null, null, null, "github-copilot", [], [status], [], [], false);

        var json = SetupJson.Serialize(result);

        Assert.Contains("\"updated_at\":\"2026-07-12T00:00:00Z\"", json, StringComparison.Ordinal);
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
        recordId, SetupTargetKind.Json, "vscode-stable-default-user-settings", true, "1.128.0", SetupOperation.Replace,
        SetupEffectiveSource.UserSetting, null, null, SetupRestartRequirement.RestartVsCode, true,
        "http://127.0.0.1:4320", LoadManifest("github-copilot-vscode.json"), null,
        [new SetupMemberChangeResult("github.copilot.chat.otel.otlpEndpoint", SetupOperation.Replace, "present_different", "configured_loopback", "none", false)]);

    private static JsonElement CreateHistoricalManifest()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(GetManifestPath("github-copilot-vscode.json")))!.AsObject();
        manifest["support_status"] = "planned";
        manifest["stability"] = "preview";
        using var historical = JsonDocument.Parse(manifest.ToJsonString());
        return historical.RootElement.Clone();
    }

    private static SetupTargetResult CreateGuidanceTarget() => new(
        "00000000-0000-7000-8000-000000000001", SetupTargetKind.Guidance, "app-sdk-guidance", false, null,
        SetupOperation.NoOp, null, null, null, SetupRestartRequirement.None, false, null, null,
        new SetupGuidance("caller_managed_sample", "dotnet", AppSdkGuidanceSample), []);

    private static SetupChangeSetStatusResult CreateAppliedStatus(string createdAt, string updatedAt) => new(
        "00000000-0000-7000-8000-000000000003", "github-copilot", "all", createdAt, updatedAt,
        SetupChangeSetState.Applied, null, SetupCurrentState.Current, true,
        [CreateWritableTarget("00000000-0000-7000-8000-000000000001") with { ReferenceState = SetupReferenceState.Desired, CurrentState = SetupCurrentState.Current }]);

    private static SetupCommandResult CreateMutationResult(SetupCommand command, SetupTargetResult target) => command switch
    {
        SetupCommand.Plan => CreatePlanResult([target]),
        SetupCommand.Apply => new SetupCommandResult(command, true, SetupCodes.ApplySucceeded,
            "00000000-0000-7000-8000-000000000002", null, null, "github-copilot", [target], [], [], [], false),
        SetupCommand.Rollback => new SetupCommandResult(command, true, SetupCodes.RollbackSucceeded,
            "00000000-0000-7000-8000-000000000002", null, null, "github-copilot", [target], [], [], [], false),
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    private static void AssertPartialStatusSerialization(
        SetupReferenceState referenceState,
        SetupCurrentState currentState,
        string expectedReferenceState,
        string expectedCurrentState)
    {
        var target = CreateWritableTarget("00000000-0000-7000-8000-000000000001") with
        {
            ReferenceState = referenceState,
            CurrentState = currentState,
            RollbackAvailable = false,
        };
        var status = new SetupChangeSetStatusResult(
            "00000000-0000-7000-8000-000000000003", "github-copilot", "all", "2026-07-12T00:00:00Z", "2026-07-12T00:01:00Z",
            SetupChangeSetState.Partial, SetupCodes.InterruptedRecoveryFailed, currentState, false, [target]);
        var result = new SetupCommandResult(SetupCommand.Status, true, SetupCodes.StatusReady, null, null, null, "github-copilot", [], [status], [], [], false);

        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        var serializedTarget = document.RootElement.GetProperty("change_sets")[0].GetProperty("targets")[0];

        Assert.Equal(expectedReferenceState, serializedTarget.GetProperty("reference_state").GetString());
        Assert.Equal(expectedCurrentState, serializedTarget.GetProperty("current_state").GetString());
    }

    private static string GetManifestPath(string manifestFileName) => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "specifications", "contracts", "source-capabilities", "v1", "manifests", manifestFileName);

    private static JsonElement LoadManifest(string manifestFileName)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(GetManifestPath(manifestFileName)));
        return manifest.RootElement.Clone();
    }
}
