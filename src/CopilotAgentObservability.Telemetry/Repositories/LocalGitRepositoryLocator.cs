using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.Telemetry.Repositories;

internal sealed class LocalGitRepositoryLocator : ILocalRepositoryLocator
{
    private const string Prefix = "local-git:";

    private LocalGitRepositoryLocator(string canonicalLocator, string locatorSha256, string displayRepository)
    {
        CanonicalLocator = canonicalLocator;
        LocatorSha256 = locatorSha256;
        DisplayRepository = displayRepository;
    }

    public string Kind => "local_git_repository";
    public string CanonicalLocator { get; }
    public string LocatorSha256 { get; }
    public string DisplayOwner => "Local";
    public string DisplayRepository { get; }

    internal static LocalGitRepositoryLocator? Create(string commonDirectory, string displayRepository)
    {
        if (!Path.IsPathFullyQualified(commonDirectory))
            return null;
        string normalized;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(commonDirectory));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("local-git-common-dir\0v1\0" + normalized)));
        try { displayRepository = displayRepository.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { displayRepository = string.Empty; }
        if (string.IsNullOrWhiteSpace(displayRepository)
            || displayRepository.Length > 100
            || displayRepository.Any(character => char.IsControl(character) || character is '/' or '\\'))
            displayRepository = $"Local repository {identity[..8]}";
        var canonical = Prefix + identity;
        var locatorSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("local-repository-locator\0v1\0local_git_repository\0" + canonical)));
        return new(canonical, locatorSha256, displayRepository);
    }

    internal static bool IsExact(string canonicalLocator, string locatorSha256, string displayOwner, string displayRepository)
    {
        if (!canonicalLocator.StartsWith(Prefix, StringComparison.Ordinal)
            || canonicalLocator.Length != Prefix.Length + 64
            || canonicalLocator[Prefix.Length..].Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || displayOwner != "Local")
            return false;
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("local-repository-locator\0v1\0local_git_repository\0" + canonicalLocator)));
        return expected == locatorSha256
            && !string.IsNullOrWhiteSpace(displayRepository)
            && displayRepository.Length is >= 1 and <= 100
            && displayRepository == displayRepository.Normalize(NormalizationForm.FormC)
            && !displayRepository.Any(character => char.IsControl(character) || character is '/' or '\\');
    }

    internal static bool IsDisplayExact(string canonicalLocator, string displayOwner, string displayRepository) =>
        canonicalLocator.StartsWith(Prefix, StringComparison.Ordinal)
        && canonicalLocator.Length == Prefix.Length + 64
        && canonicalLocator[Prefix.Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
        && displayOwner == "Local"
        && !string.IsNullOrWhiteSpace(displayRepository)
        && displayRepository.Length is >= 1 and <= 100
        && displayRepository == displayRepository.Normalize(NormalizationForm.FormC)
        && !displayRepository.Any(character => char.IsControl(character) || character is '/' or '\\');
}
