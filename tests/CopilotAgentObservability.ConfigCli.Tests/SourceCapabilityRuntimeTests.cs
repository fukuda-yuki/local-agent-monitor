using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.ConfigCli.Setup.Capabilities;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Tests;

[CollectionDefinition(nameof(SourceCapabilityManifestCurrentDirectoryCollection), DisableParallelization = true)]
public sealed class SourceCapabilityManifestCurrentDirectoryCollection;

[Collection(nameof(SourceCapabilityManifestCurrentDirectoryCollection))]
public sealed class SourceCapabilityRuntimeTests
{
    private const string VsCodeResourceName = "CopilotAgentObservability.ConfigCli.Setup.Capabilities.Manifests.github-copilot-vscode.json";
    private const string CliResourceName = "CopilotAgentObservability.ConfigCli.Setup.Capabilities.Manifests.github-copilot-cli.json";
    private static readonly string AppSdkGuidanceSample = SetupContractValidator.RehydrateStatusGuidance(
        new SetupStatusGuidance("caller_managed_sample", "dotnet")).Sample;

    [Fact]
    public void EmbeddedManifestResources_ArePresentAndLoadWithoutTheRepositoryWorkingDirectory()
    {
        var assembly = typeof(SourceCapabilityManifestLoader).Assembly;

        Assert.Contains(VsCodeResourceName, assembly.GetManifestResourceNames());
        Assert.Contains(CliResourceName, assembly.GetManifestResourceNames());
        Assert.NotNull(assembly.GetManifestResourceStream(VsCodeResourceName));
        Assert.NotNull(assembly.GetManifestResourceStream(CliResourceName));

        var originalDirectory = Environment.CurrentDirectory;
        var alternateDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(alternateDirectory);

        try
        {
            Environment.CurrentDirectory = alternateDirectory;

            Assert.Equal("github-copilot-vscode", SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.VsCode)!.SourceSurface);
            Assert.Equal("github-copilot-cli", SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.Cli)!.SourceSurface);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Directory.Delete(alternateDirectory);
        }
    }

    [Theory]
    [InlineData("vscode", "github-copilot-vscode")]
    [InlineData("cli", "github-copilot-cli")]
    public void LoadForTarget_ReturnsTheExactMappedCanonicalManifest(string targetName, string expectedSurface)
    {
        var target = targetName == "vscode" ? GitHubCopilotSetupTarget.VsCode : GitHubCopilotSetupTarget.Cli;
        var manifest = SourceCapabilityManifestLoader.LoadForTarget(target);

        Assert.NotNull(manifest);
        Assert.Equal(expectedSurface, manifest.SourceSurface);
        Assert.True(SemanticallyEqual(ReadCommittedCanonicalManifest(expectedSurface).RootElement, manifest.CanonicalJson));
    }

    [Fact]
    public void LoadForTarget_AppSdkHasNoCanonicalManifest()
    {
        Assert.Null(SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.AppSdk));
    }

    [Fact]
    public void AggregateAdapter_AttachesCanonicalManifestsForWritableSurfacesAndNullForGuidance()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.Parse("2026-07-14T00:00:00Z"));
        var adapter = new GitHubCopilotSetupAdapter(platform,
        [
            new ManifestPairingPartition("vscode", CreateManifestPairingRecord("vscode", 1)),
            new ManifestPairingPartition("cli", CreateManifestPairingRecord("cli", 2)),
            new ManifestPairingPartition("app-sdk", CreateManifestPairingRecord("app-sdk", 3)),
        ]);
        var result = Assert.IsType<SetupPlanSuccess<SetupChangePlan>>(adapter.Plan(new SetupPlanRequest(
            "github-copilot",
            "all",
            "http://127.0.0.1:4320",
            false,
            Guid.Parse("00000000-0000-7000-8000-000000000010"),
            DateTimeOffset.Parse("2026-07-14T00:00:00Z"),
            "1.2.3")));
        var vsCode = result.Value.Records.Single(record => record.TargetLabel == "vscode-stable-default-user-settings");
        var cli = result.Value.Records.Single(record => record.TargetLabel == "copilot-cli-user-environment");
        var appSdk = result.Value.Records.Single(record => record.TargetLabel == "github-copilot-app-sdk-guidance");

        Assert.True(SourceCapabilityManifestLoader.MatchesCanonical(
            SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.VsCode)!,
            vsCode.StatusProjection.ExpectedResult!.Value));
        Assert.True(SourceCapabilityManifestLoader.MatchesCanonical(
            SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.Cli)!,
            cli.StatusProjection.ExpectedResult!.Value));
        Assert.Null(appSdk.StatusProjection.ExpectedResult);
    }

    [Theory]
    [InlineData("github-copilot-vscode")]
    [InlineData("github-copilot-cli")]
    public void LoadForSurface_ReturnsTheExactCommittedCanonicalManifest(string sourceSurface)
    {
        var manifest = SourceCapabilityManifestLoader.LoadForSurface(sourceSurface);

        Assert.Equal(sourceSurface, manifest.SourceSurface);
        Assert.True(SemanticallyEqual(ReadCommittedCanonicalManifest(sourceSurface).RootElement, manifest.CanonicalJson));
    }

    [Fact]
    public void MatchesCanonical_IgnoresWhitespaceAndObjectPropertyOrder()
    {
        var manifest = SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.VsCode)!;
        var reordered = ReverseObjectProperties(manifest.CanonicalJson);
        using var candidate = JsonDocument.Parse(reordered);

        Assert.True(SourceCapabilityManifestLoader.MatchesCanonical(manifest, candidate.RootElement));
        Assert.True(SourceCapabilityManifestLoader.MatchesCanonical(candidate.RootElement));
    }

    [Fact]
    public void MatchesCanonical_RejectsTheOtherSurfaceManifest()
    {
        var vsCode = SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.VsCode)!;
        var cli = SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.Cli)!;

        Assert.False(SourceCapabilityManifestLoader.MatchesCanonical(vsCode, cli.CanonicalJson));
        Assert.False(SourceCapabilityManifestLoader.MatchesCanonical(cli, vsCode.CanonicalJson));
    }

    [Theory]
    [InlineData("signal")]
    [InlineData("adapter")]
    [InlineData("completeness-order")]
    public void MatchesCanonical_RejectsMutationsOfCanonicalSignalsAndCompleteness(string mutation)
    {
        var manifest = SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.VsCode)!;
        var node = JsonNode.Parse(manifest.CanonicalJson.GetRawText())!.AsObject();

        switch (mutation)
        {
            case "signal":
                node["signals"]!["trace"]!["availability"] = "unknown";
                break;
            case "adapter":
                node["source_adapter"] = "otel-http";
                break;
            case "completeness-order":
                var statuses = node["completeness"]!["statuses"]!.AsArray();
                var first = statuses[0]!.DeepClone();
                statuses[0] = statuses[1]!.DeepClone();
                statuses[1] = first;
                break;
            default:
                throw new InvalidOperationException();
        }

        using var candidate = JsonDocument.Parse(node.ToJsonString());

        Assert.False(SourceCapabilityManifestLoader.MatchesCanonical(manifest, candidate.RootElement));
        Assert.False(SourceCapabilityManifestLoader.MatchesCanonical(candidate.RootElement));
    }

    [Theory]
    [InlineData("github-copilot-vscode.json")]
    [InlineData("C:\\private\\manifests\\github-copilot-vscode.json")]
    [InlineData("github-copilot-vsco")]
    public void LoadForSurface_RejectsResourceLikeOrTruncatedInputWithoutEchoingIt(string input)
    {
        var exception = Assert.Throws<InvalidDataException>(() => SourceCapabilityManifestLoader.LoadForSurface(input));

        Assert.Equal("Unknown source capability manifest.", exception.Message);
        Assert.DoesNotContain(input, exception.Message, StringComparison.Ordinal);
    }

    private static JsonDocument ReadCommittedCanonicalManifest(string sourceSurface)
    {
        var fileName = sourceSurface switch
        {
            "github-copilot-vscode" => "github-copilot-vscode.json",
            "github-copilot-cli" => "github-copilot-cli.json",
            _ => throw new ArgumentOutOfRangeException(nameof(sourceSurface)),
        };

        return JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "specifications", "contracts", "source-capabilities", "v1", "manifests", fileName)));
    }

    private static string ReverseObjectProperties(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            WriteJsonWithReversedObjectProperties(writer, element);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteJsonWithReversedObjectProperties(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().Reverse())
                {
                    writer.WritePropertyName(property.Name);
                    WriteJsonWithReversedObjectProperties(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteJsonWithReversedObjectProperties(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool SemanticallyEqual(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            return false;
        }

        return expected.ValueKind switch
        {
            JsonValueKind.Object => expected.EnumerateObject().Count() == actual.EnumerateObject().Count() &&
                expected.EnumerateObject().All(property => actual.TryGetProperty(property.Name, out var value) && SemanticallyEqual(property.Value, value)),
            JsonValueKind.Array => expected.GetArrayLength() == actual.GetArrayLength() &&
                expected.EnumerateArray().Zip(actual.EnumerateArray()).All(pair => SemanticallyEqual(pair.First, pair.Second)),
            JsonValueKind.String => expected.GetString() == actual.GetString(),
            JsonValueKind.True or JsonValueKind.False => expected.GetBoolean() == actual.GetBoolean(),
            JsonValueKind.Null => true,
            _ => expected.GetRawText() == actual.GetRawText(),
        };
    }

    private static SetupChangeRecord CreateManifestPairingRecord(string target, int recordNumber)
    {
        var (kind, label, operation, source, restart, endpoint, guidance) = target switch
        {
            "vscode" => (SetupTargetKind.Json, "vscode-stable-default-user-settings", SetupOperation.Replace, (SetupEffectiveSource?)SetupEffectiveSource.UserSetting, SetupRestartRequirement.RestartVsCode, "http://127.0.0.1:4320", (SetupGuidance?)null),
            "cli" => (SetupTargetKind.Env, "copilot-cli-user-environment", SetupOperation.Replace, (SetupEffectiveSource?)SetupEffectiveSource.Environment, SetupRestartRequirement.RestartTerminalSession, "http://127.0.0.1:4320", (SetupGuidance?)null),
            "app-sdk" => (SetupTargetKind.Guidance, "github-copilot-app-sdk-guidance", SetupOperation.NoOp, (SetupEffectiveSource?)null, SetupRestartRequirement.None, (string?)null, new SetupGuidance("caller_managed_sample", "dotnet", AppSdkGuidanceSample)),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        var members = kind == SetupTargetKind.Guidance
            ? (IReadOnlyList<SetupPrivatePlanMember>)[]
            : [new SetupPrivatePlanMember("setting", SetupOperation.Replace, "desired")];
        var changes = kind == SetupTargetKind.Guidance
            ? (IReadOnlyList<SetupMemberChangeResult>)[]
            : [new SetupMemberChangeResult("setting", SetupOperation.Replace, "present_different", "configured", "none", false)];

        return new SetupChangeRecord(
            Guid.Parse($"00000000-0000-7000-8000-{recordNumber:D12}"),
            kind,
            $"private://{label}",
            label,
            new string('a', 64),
            "configured",
            members,
            restart,
            new SetupStatusProjection(true, "1.0.0", operation, source, endpoint, null, guidance is null ? null : new SetupStatusGuidance(guidance.Kind, guidance.Language), changes),
            guidance);
    }

    private sealed class ManifestPairingPartition(string targetToken, SetupChangeRecord record) : IGitHubCopilotTargetPartition
    {
        public string TargetToken => targetToken;

        public GitHubCopilotPartitionPlan Plan(GitHubCopilotPartitionContext context) => new(null, [record], [], []);

        public SetupPlanResult<SetupRevalidation> Revalidate(
            GitHubCopilotPartitionContext context,
            SetupPrivatePlan plan,
            SetupLedgerChangeSet plannedChangeSet) => SetupPlanResult.Revalidated();
    }
}
