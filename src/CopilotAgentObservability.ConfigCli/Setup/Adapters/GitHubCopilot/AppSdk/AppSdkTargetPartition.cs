using System.Text.RegularExpressions;
using System.Xml;
using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot.AppSdk;

internal sealed class AppSdkTargetPartition : IGitHubCopilotTargetPartition
{
    private const int MaximumProjectBytes = 1024 * 1024;
    private const int MaximumVersionLength = 128;
    private const string TargetLabel = "github-copilot-app-sdk-guidance";
    private const string WindowsProjectPath = "src\\CopilotAgentObservability.LocalMonitor\\CopilotAgentObservability.LocalMonitor.csproj";
    private const string UnixProjectPath = "src/CopilotAgentObservability.LocalMonitor/CopilotAgentObservability.LocalMonitor.csproj";
    private static readonly Regex SemanticVersion = new(
        "\\A(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public string TargetToken => "app-sdk";

    public GitHubCopilotPartitionPlan Plan(GitHubCopilotPartitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var package = DetectPackage(context.Platform);
        var statusGuidance = new SetupStatusGuidance("caller_managed_sample", "dotnet");
        return new GitHubCopilotPartitionPlan(
            null,
            [new SetupChangeRecord(
                context.Platform.Identifiers.CreateUuidV7(),
                SetupTargetKind.Guidance,
                "app-sdk-guidance",
                TargetLabel,
                new string('0', 64),
                new SetupInlineDesiredState("app-sdk-guidance"),
                [],
                SetupRestartRequirement.None,
                new SetupStatusProjection(
                    package.Present,
                    package.Version,
                    SetupOperation.NoOp,
                    null,
                    null,
                    null,
                    statusGuidance,
                    []),
                SetupContractValidator.RehydrateStatusGuidance(statusGuidance))],
            [],
            []);
    }

    public SetupPlanResult<SetupRevalidation> Revalidate(
        GitHubCopilotPartitionContext context,
        SetupPrivatePlan plan,
        SetupLedgerChangeSet plannedChangeSet)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plannedChangeSet);
        return SetupPlanResult.Revalidated();
    }

    private static PackageDetection DetectPackage(ISetupPlatform platform)
    {
        var projectPath = platform.PathStyle == SetupPathStyle.Windows
            ? WindowsProjectPath
            : UnixProjectPath;
        if (!platform.FileSystem.FileExists(projectPath))
        {
            return PackageDetection.Absent;
        }

        var read = platform.FileSystem.ReadAtMostBytes(projectPath, MaximumProjectBytes);
        if (!read.IsComplete)
        {
            return PackageDetection.Absent;
        }

        try
        {
            using var stream = new MemoryStream(read.Bytes, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumProjectBytes,
            });
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element &&
                    string.Equals(reader.LocalName, "PackageReference", StringComparison.Ordinal) &&
                    string.Equals(reader.GetAttribute("Include"), "GitHub.Copilot.SDK", StringComparison.OrdinalIgnoreCase))
                {
                    return new PackageDetection(true, SanitizeVersion(reader.GetAttribute("Version")));
                }
            }
        }
        catch (XmlException)
        {
            return PackageDetection.Absent;
        }

        return PackageDetection.Absent;
    }

    private static string? SanitizeVersion(string? version) =>
        version is { Length: <= MaximumVersionLength } && SemanticVersion.IsMatch(version)
            ? version
            : null;

    private sealed record PackageDetection(bool Present, string? Version)
    {
        public static PackageDetection Absent { get; } = new(false, null);
    }
}
