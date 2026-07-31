using CopilotAgentObservability.Telemetry.Repositories;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class LocalRepositoryLocatorParserTests
{
    private const string CanonicalLocator = "github.com/octo/hello-world";
    private const string LocatorSha256 = "fd5c0c3d18ce8b2c4144ecedea1199219f4bd5a0133d280aee5a68755155fe95";

    public static IEnumerable<object[]> AcceptedForms()
    {
        yield return ["https://github.com/Octo/Hello-World"];
        yield return ["https://github.com/Octo/Hello-World.git"];
        yield return ["ssh://git@github.com/Octo/Hello-World"];
        yield return ["ssh://git@github.com/Octo/Hello-World.git"];
        yield return ["git@github.com:Octo/Hello-World"];
        yield return ["git@github.com:Octo/Hello-World.git"];
        yield return ["HTTPS://GitHub.Com/Octo/Hello-World"];
        yield return ["git@GitHub.Com:Octo/Hello-World"];
    }

    public static IEnumerable<object[]> RepositoryOverMaximumForms()
    {
        var repository = new string('a', 101);
        yield return [$"https://github.com/owner/{repository}"];
        yield return [$"https://github.com/owner/{repository}.git"];
    }

    public static IEnumerable<object[]> RejectedLocatorForms()
    {
        yield return [""];
        yield return ["http://github.com/owner/repository"];
        yield return ["git://github.com/owner/repository"];
        yield return ["https://github.com/owner/repository/"];
        yield return ["https://github.com/owner/repository/extra"];
        yield return ["https://github.com/owner/repository?query=value"];
        yield return ["https://github.com/owner/repository#fragment"];
        yield return ["https://github.com:443/owner/repository"];
        yield return ["https://git:password@github.com/owner/repository"];
        yield return ["ssh://git:password@github.com/owner/repository"];
        yield return ["https://github.com/owner/repo%2Egit"];
        yield return ["https://github.com/owner\\repository"];
        yield return ["https://github.com/owner/ repository"];
        yield return [" https://github.com/owner/repository"];
        yield return ["https://github.com/owner/repository "];
        yield return ["https://github.com/owner/repo\tname"];
        yield return ["https://github.com/owner/repo\rname"];
        yield return ["https://github.com/owner/repo\nname"];
        yield return ["https://github.com/owner/repo\u0000name"];
        yield return ["https://github.com/owner/repo\u007fname"];
        yield return ["https://github.com/owner/café"];
        yield return ["ssh://Git@github.com/owner/repository"];
        yield return ["Git@github.com:owner/repository"];
        yield return ["git@github.com/owner/repository"];
        yield return ["git@github.com:owner"];
        yield return ["git@github.com:owner/repository/extra"];
    }

    [Theory]
    [MemberData(nameof(AcceptedForms))]
    public void TryParse_AcceptsEachExactGitHubTransportForm(string input)
    {
        var parsed = GitHubRepositoryLocatorParser.TryParse(input, out var locator);

        Assert.True(parsed);
        Assert.NotNull(locator);
        Assert.Equal(CanonicalLocator, locator.CanonicalLocator);
        Assert.Equal(LocatorSha256, locator.LocatorSha256);
        Assert.Equal("Octo", locator.DisplayOwner);
        Assert.Equal("Hello-World", locator.DisplayRepository);
    }

    [Fact]
    public void TryParse_UsesAsciiCaseInsensitiveSchemeAndHostOnly()
    {
        var parsed = GitHubRepositoryLocatorParser.TryParse(
            "SSH://git@GitHub.Com/OctO/Repo_Name", out var locator);

        Assert.True(parsed);
        Assert.NotNull(locator);
        Assert.Equal("github.com/octo/repo_name", locator.CanonicalLocator);
        Assert.Equal("OctO", locator.DisplayOwner);
        Assert.Equal("Repo_Name", locator.DisplayRepository);
    }

    [Theory]
    [InlineData("https://github.com/a/a")]
    [InlineData("https://github.com/abcdefghijklmnopqrstuvwxyz0123456789-xy/repository")]
    public void TryParse_AcceptsOwnerLengthBoundaries(string input)
    {
        Assert.True(GitHubRepositoryLocatorParser.TryParse(input, out _));
    }

    [Theory]
    [InlineData("https://github.com/-owner/repository")]
    [InlineData("https://github.com/owner-/repository")]
    [InlineData("https://github.com/owner_name/repository")]
    [InlineData("https://github.com/abcdefghijklmnopqrstuvwxyz0123456789-xyz/repository")]
    public void TryParse_RejectsOwnerGrammarViolations(string input)
    {
        Assert.False(GitHubRepositoryLocatorParser.TryParse(input, out _));
    }

    [Fact]
    public void TryParse_AcceptsOneHundredCharacterLogicalRepositoryBeforeLowercaseGitSuffix()
    {
        var repository = new string('a', 100);

        var parsed = GitHubRepositoryLocatorParser.TryParse(
            $"https://github.com/owner/{repository}.git", out var locator);

        Assert.True(parsed);
        Assert.NotNull(locator);
        Assert.Equal($"github.com/owner/{repository}", locator.CanonicalLocator);
        Assert.Equal(repository, locator.DisplayRepository);
    }

    [Theory]
    [MemberData(nameof(RepositoryOverMaximumForms))]
    public void TryParse_RejectsOneHundredOneCharacterLogicalRepositoryWithOrWithoutLowercaseGitSuffix(string input)
    {
        Assert.False(GitHubRepositoryLocatorParser.TryParse(input, out _));
    }

    [Theory]
    [InlineData("https://github.com/owner/..git")]
    [InlineData("https://github.com/owner/...git")]
    [InlineData("https://github.com/owner/.git")]
    [InlineData("https://github.com/owner/..")]
    [InlineData("https://github.com/owner/")]
    public void TryParse_ValidatesLogicalRepositoryAfterRemovingOneLowercaseGitSuffix(string input)
    {
        Assert.False(GitHubRepositoryLocatorParser.TryParse(input, out _));
    }

    [Fact]
    public void TryParse_RemovesOnlyOneExactLowercaseGitSuffix()
    {
        var lower = GitHubRepositoryLocatorParser.TryParse(
            "https://github.com/Owner/Repository.git.git", out var lowerLocator);
        var upper = GitHubRepositoryLocatorParser.TryParse(
            "https://github.com/Owner/Repository.GIT", out var upperLocator);

        Assert.True(lower);
        Assert.NotNull(lowerLocator);
        Assert.Equal("Repository.git", lowerLocator.DisplayRepository);
        Assert.Equal("github.com/owner/repository.git", lowerLocator.CanonicalLocator);

        Assert.True(upper);
        Assert.NotNull(upperLocator);
        Assert.Equal("Repository.GIT", upperLocator.DisplayRepository);
        Assert.Equal("github.com/owner/repository.git", upperLocator.CanonicalLocator);
    }

    [Theory]
    [MemberData(nameof(RejectedLocatorForms))]
    public void TryParse_RejectsForbiddenOrUnsupportedSyntax(string input)
    {
        Assert.False(GitHubRepositoryLocatorParser.TryParse(input, out _));
    }

    [Fact]
    public void TryParse_RejectsNullAndInputsOutsideTheUtf8ByteEnvelope()
    {
        Assert.False(GitHubRepositoryLocatorParser.TryParse(null, out _));
        Assert.False(GitHubRepositoryLocatorParser.TryParse(new string('a', 513), out _));
    }

    [Fact]
    public void TryParse_ProducesTheExactDomainSeparatedFingerprintBytes()
    {
        var parsed = GitHubRepositoryLocatorParser.TryParse(
            "https://github.com/Owner/Repository.git", out var locator);

        Assert.True(parsed);
        Assert.NotNull(locator);
        Assert.Equal("github.com/owner/repository", locator.CanonicalLocator);
        Assert.Equal("de4f0c84802d8b1358033ae4954272977ff1bbbd740b95da88eb3c7c6a70b25e", locator.LocatorSha256);
        Assert.Matches("^[0-9a-f]{64}$", locator.LocatorSha256);
    }
}
