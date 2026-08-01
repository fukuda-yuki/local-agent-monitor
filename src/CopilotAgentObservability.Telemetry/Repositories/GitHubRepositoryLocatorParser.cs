using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace CopilotAgentObservability.Telemetry.Repositories;

public static class GitHubRepositoryLocatorParser
{
    private const int MaximumInputBytes = 512;
    private const string FingerprintDomain = "local-repository-locator\0v1\0github_repository\0";

    public static bool TryParse(string? input, [NotNullWhen(true)] out GitHubRepositoryLocator? locator)
    {
        locator = null;
        if (input is null || !IsAsciiLocatorInput(input) || !TryReadPath(input, out var path))
        {
            return false;
        }

        var separator = path.IndexOf('/');
        if (separator <= 0 || separator != path.LastIndexOf('/'))
        {
            return false;
        }

        var owner = path[..separator];
        var rawRepository = path[(separator + 1)..];
        var repository = rawRepository.EndsWith(".git", StringComparison.Ordinal)
            ? rawRepository[..^4]
            : rawRepository;
        if (!IsOwner(owner) || !IsRepository(repository))
        {
            return false;
        }

        ComputeIdentity(owner, repository, out var canonicalLocator, out var locatorSha256);
        locator = new GitHubRepositoryLocator(canonicalLocator, locatorSha256, owner, repository);
        return true;
    }

    internal static bool IsExact(GitHubRepositoryLocator? locator) => locator is not null
        && IsExact(locator.CanonicalLocator, locator.LocatorSha256, locator.DisplayOwner, locator.DisplayRepository);

    internal static bool IsExact(
        string? canonicalLocator,
        string? locatorSha256,
        string? displayOwner,
        string? displayRepository)
    {
        if (canonicalLocator is null
            || locatorSha256 is null
            || displayOwner is null
            || displayRepository is null
            || !IsOwner(displayOwner)
            || !IsRepository(displayRepository))
        {
            return false;
        }

        ComputeIdentity(displayOwner, displayRepository, out var expectedCanonicalLocator, out var expectedLocatorSha256);
        return string.Equals(canonicalLocator, expectedCanonicalLocator, StringComparison.Ordinal)
            && string.Equals(locatorSha256, expectedLocatorSha256, StringComparison.Ordinal);
    }

    private static void ComputeIdentity(string owner, string repository, out string canonicalLocator, out string locatorSha256)
    {
        canonicalLocator = $"github.com/{owner.ToLowerInvariant()}/{repository.ToLowerInvariant()}";
        var fingerprintBytes = Encoding.UTF8.GetBytes(FingerprintDomain + canonicalLocator);
        locatorSha256 = Convert.ToHexString(SHA256.HashData(fingerprintBytes)).ToLowerInvariant();
    }

    private static bool IsAsciiLocatorInput(string input)
    {
        if (input.Length is 0 or > MaximumInputBytes)
        {
            return false;
        }

        foreach (var character in input)
        {
            if (character is <= ' ' or >= '\u007f')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadPath(string input, [NotNullWhen(true)] out string? path)
    {
        if (input.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            path = input[19..];
            return true;
        }

        if (input.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            var authorityAndPath = input[6..];
            if (authorityAndPath.StartsWith("git@", StringComparison.Ordinal)
                && authorityAndPath[4..].StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
            {
                path = authorityAndPath[15..];
                return true;
            }
        }

        if (input.StartsWith("git@", StringComparison.Ordinal)
            && input[4..].StartsWith("github.com:", StringComparison.OrdinalIgnoreCase))
        {
            path = input[15..];
            return true;
        }

        path = null;
        return false;
    }

    private static bool IsOwner(string value)
    {
        if (value.Length is < 1 or > 39 || !IsAsciiLetterOrDigit(value[0]) || !IsAsciiLetterOrDigit(value[^1]))
        {
            return false;
        }

        return value.All(character => IsAsciiLetterOrDigit(character) || character == '-');
    }

    private static bool IsRepository(string value)
    {
        if (value.Length is < 1 or > 100 || value is "." or "..")
        {
            return false;
        }

        return value.All(character => IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}
