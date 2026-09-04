namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorOptionsTests
{
    [Theory]
    [InlineData("true", "false")]
    [InlineData("FALSE", "TrUe")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    public void Parse_AiReleaseGatesAcceptIndependentExplicitBooleans(string repository, string compare)
    {
        var result = MonitorOptions.Parse(["--repository-ai-enabled", repository, "--compare-ai-enabled", compare]);

        Assert.Null(result.Error);
        Assert.Equal(string.Equals(repository, "true", StringComparison.OrdinalIgnoreCase), result.Options!.RepositoryAiEnabled);
        Assert.Equal(string.Equals(compare, "true", StringComparison.OrdinalIgnoreCase), result.Options.CompareAiEnabled);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void Parse_AiReleaseGatesDefaultByBuildAndIgnoreEnvironmentAliases(string hostingEnvironment)
    {
#if DEBUG
        const string contraryValue = "false";
#else
        const string contraryValue = "true";
#endif
        var result = MonitorOptions.Parse([], name => name is "ASPNETCORE_ENVIRONMENT" or "DOTNET_ENVIRONMENT"
            ? hostingEnvironment : name.Contains("AI_ENABLED", StringComparison.Ordinal) ? contraryValue : null);
        Assert.Null(result.Error);
#if DEBUG
        Assert.True(result.Options!.RepositoryAiEnabled);
        Assert.True(result.Options.CompareAiEnabled);
#else
        Assert.False(result.Options!.RepositoryAiEnabled);
        Assert.False(result.Options.CompareAiEnabled);
#endif
        var repositoryOnly = MonitorOptions.Parse(["--repository-ai-enabled", contraryValue]).Options!;
        Assert.NotEqual(result.Options.RepositoryAiEnabled, repositoryOnly.RepositoryAiEnabled);
        Assert.Equal(result.Options.CompareAiEnabled, repositoryOnly.CompareAiEnabled);
        var compareOnly = MonitorOptions.Parse(["--compare-ai-enabled", contraryValue]).Options!;
        Assert.Equal(result.Options.RepositoryAiEnabled, compareOnly.RepositoryAiEnabled);
        Assert.NotEqual(result.Options.CompareAiEnabled, compareOnly.CompareAiEnabled);
    }

    [Theory]
    [InlineData("--repository-ai-enabled")]
    [InlineData("--compare-ai-enabled")]
    public void Parse_AiReleaseGatesRejectMissingInvalidAndDuplicateValues(string option)
    {
        Assert.Equal($"{option} requires a value.", MonitorOptions.Parse([option]).Error);
        Assert.Equal($"{option} requires a value.", MonitorOptions.Parse([option, "--sanitized-only"]).Error);
        foreach (var value in new[] { "", "1", "0", "yes", " true", "false ", "true\0" })
            Assert.Equal($"{option} requires true or false.", MonitorOptions.Parse([option, value]).Error);
        Assert.Equal($"local-monitor accepts {option} only once.", MonitorOptions.Parse([option, "false", option, "true"]).Error);
    }

    [Fact]
    public void Parse_accepts_repeated_apply_roots_without_exposing_paths_in_labels()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cao-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var result = MonitorOptions.Parse(["--apply-root", $"repository={root}", "--apply-root", $"skill={root}\\child"]);
            Assert.NotNull(result.Error);
            Directory.CreateDirectory(Path.Combine(root, "child"));
            result = MonitorOptions.Parse(["--apply-root", $"repository={root}", "--apply-root", $"skill={root}\\child"]);
            Assert.Null(result.Error);
            Assert.Equal(2, result.Options!.ApplyRoots!.Count);
            Assert.DoesNotContain(root, result.Options.ApplyRoots[0].Label, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void Parse_DefaultsToLoopbackPort4320AndRawShown()
    {
        var result = MonitorOptions.Parse([]);

        Assert.Null(result.Error);
        Assert.Equal(RawStoreDefaults.DefaultDatabasePath, result.Options!.DatabasePath);
        Assert.Equal("http://127.0.0.1:4320", result.Options.Url);
        // D023: raw is shown by default; --sanitized-only is the opt-out.
        Assert.False(result.Options.SanitizedOnly);
        Assert.Equal(31_457_280, result.Options.MaxRequestBodyBytes);
    }

    [Fact]
    public void Parse_SanitizedOnlyFlagRestoresMetadataOnlyMode()
    {
        var result = MonitorOptions.Parse(["--sanitized-only"]);

        Assert.Null(result.Error);
        Assert.True(result.Options!.SanitizedOnly);
    }

    [Fact]
    public void Parse_RejectsRemovedEnableRawViewFlag()
    {
        var result = MonitorOptions.Parse(["--enable-raw-view"]);

        Assert.Equal("unknown local-monitor option '--enable-raw-view'.", result.Error);
    }

    [Fact]
    public void Parse_PortSetsLoopbackUrl()
    {
        var result = MonitorOptions.Parse(["--port", "54321"]);

        Assert.Null(result.Error);
        Assert.Equal("http://127.0.0.1:54321", result.Options!.Url);
    }

    [Fact]
    public void Parse_RejectsUrlAndPortTogether()
    {
        var result = MonitorOptions.Parse(["--url", "http://127.0.0.1:4321", "--port", "4322"]);

        Assert.Equal("local-monitor accepts either --url or --port, not both.", result.Error);
    }

    [Theory]
    [InlineData("http://0.0.0.0:4320")]
    [InlineData("http://192.168.0.10:4320")]
    [InlineData("http://example.com:4320")]
    public void Parse_RejectsNonLoopbackUrl(string url)
    {
        var result = MonitorOptions.Parse(["--url", url]);

        Assert.Equal("local-monitor only allows localhost, 127.0.0.1, or ::1.", result.Error);
    }

    [Fact]
    public void Parse_UsesMaxRequestBodyBytesEnvironmentFallback()
    {
        var result = MonitorOptions.Parse(
            [],
            name => name == MonitorOptions.MaxRequestBodyBytesEnvironmentVariable ? "1024" : null);

        Assert.Null(result.Error);
        Assert.Equal(1024, result.Options!.MaxRequestBodyBytes);
    }

    [Theory]
    [InlineData("--max-request-body-bytes", "0")]
    [InlineData("--max-request-body-bytes", "-1")]
    [InlineData("--max-request-body-bytes", "abc")]
    public void Parse_RejectsInvalidMaxRequestBodyBytes(string option, string value)
    {
        var result = MonitorOptions.Parse([option, value]);

        Assert.Equal("--max-request-body-bytes requires a positive integer.", result.Error);
    }

    [Fact]
    public void Parse_RejectsUnknownOption()
    {
        var result = MonitorOptions.Parse(["--unexpected"]);

        Assert.Equal("unknown local-monitor option '--unexpected'.", result.Error);
    }

    [Fact]
    public void Parse_PreservesPricingRegistryOverridesInCallerOrder()
    {
        var first = Path.GetFullPath("first-pricing-registry.json");
        var second = Path.GetFullPath("second-pricing-registry.json");

        var result = MonitorOptions.Parse(
            ["--pricing-registry-override", first, "--pricing-registry-override", second]);

        Assert.Null(result.Error);
        Assert.Equal([first, second], result.Options!.PricingRegistryOverridePaths);
    }

    [Fact]
    public void Parse_RejectsMoreThanEightPricingRegistryOverridesWithoutEchoingLocator()
    {
        const string marker = "private-pricing-locator";
        var arguments = Enumerable.Range(0, 9)
            .SelectMany(index => new[]
            {
                "--pricing-registry-override",
                Path.GetFullPath($"{marker}-{index}.json")
            })
            .ToArray();

        var result = MonitorOptions.Parse(arguments);

        Assert.Equal("pricing_catalog_unavailable", result.Error);
        Assert.DoesNotContain(marker, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DefaultsToReadinessThresholdSeconds()
    {
        var result = MonitorOptions.Parse([]);

        Assert.Null(result.Error);
        Assert.Equal(10, result.Options!.IngestionStallThresholdSeconds);
        Assert.Equal(60, result.Options.ProjectionLagThresholdSeconds);
    }

    [Fact]
    public void Parse_OverridesIngestionStallThresholdSeconds()
    {
        var result = MonitorOptions.Parse(["--ingestion-stall-threshold-seconds", "3"]);

        Assert.Null(result.Error);
        Assert.Equal(3, result.Options!.IngestionStallThresholdSeconds);
    }

    [Fact]
    public void Parse_OverridesProjectionLagThresholdSeconds()
    {
        var result = MonitorOptions.Parse(["--projection-lag-threshold-seconds", "7"]);

        Assert.Null(result.Error);
        Assert.Equal(7, result.Options!.ProjectionLagThresholdSeconds);
    }

    [Fact]
    public void Parse_UsesIngestionStallThresholdSecondsEnvironmentFallback()
    {
        var result = MonitorOptions.Parse(
            [],
            name => name == MonitorOptions.IngestionStallThresholdSecondsEnvironmentVariable ? "4" : null);

        Assert.Null(result.Error);
        Assert.Equal(4, result.Options!.IngestionStallThresholdSeconds);
    }

    [Fact]
    public void Parse_UsesProjectionLagThresholdSecondsEnvironmentFallback()
    {
        var result = MonitorOptions.Parse(
            [],
            name => name == MonitorOptions.ProjectionLagThresholdSecondsEnvironmentVariable ? "8" : null);

        Assert.Null(result.Error);
        Assert.Equal(8, result.Options!.ProjectionLagThresholdSeconds);
    }

    [Fact]
    public void Parse_CliIngestionStallThresholdSecondsOverridesEnvironmentFallback()
    {
        var result = MonitorOptions.Parse(
            ["--ingestion-stall-threshold-seconds", "3"],
            name => name == MonitorOptions.IngestionStallThresholdSecondsEnvironmentVariable ? "4" : null);

        Assert.Null(result.Error);
        Assert.Equal(3, result.Options!.IngestionStallThresholdSeconds);
    }

    [Theory]
    [InlineData("--ingestion-stall-threshold-seconds", "0")]
    [InlineData("--ingestion-stall-threshold-seconds", "-1")]
    [InlineData("--ingestion-stall-threshold-seconds", "abc")]
    public void Parse_RejectsInvalidIngestionStallThresholdSeconds(string option, string value)
    {
        var result = MonitorOptions.Parse([option, value]);

        Assert.Equal("--ingestion-stall-threshold-seconds requires a positive integer.", result.Error);
    }

    [Theory]
    [InlineData("--projection-lag-threshold-seconds", "0")]
    [InlineData("--projection-lag-threshold-seconds", "-1")]
    [InlineData("--projection-lag-threshold-seconds", "abc")]
    public void Parse_RejectsInvalidProjectionLagThresholdSeconds(string option, string value)
    {
        var result = MonitorOptions.Parse([option, value]);

        Assert.Equal("--projection-lag-threshold-seconds requires a positive integer.", result.Error);
    }

    [Fact]
    public void Parse_RejectsInvalidIngestionStallThresholdSecondsEnvironment()
    {
        var result = MonitorOptions.Parse(
            [],
            name => name == MonitorOptions.IngestionStallThresholdSecondsEnvironmentVariable ? "0" : null);

        Assert.Equal("CAO_MONITOR_INGESTION_STALL_THRESHOLD_SECONDS requires a positive integer.", result.Error);
    }

    [Fact]
    public void Parse_RejectsInvalidProjectionLagThresholdSecondsEnvironment()
    {
        var result = MonitorOptions.Parse(
            [],
            name => name == MonitorOptions.ProjectionLagThresholdSecondsEnvironmentVariable ? "abc" : null);

        Assert.Equal("CAO_MONITOR_PROJECTION_LAG_THRESHOLD_SECONDS requires a positive integer.", result.Error);
    }

    [Fact]
    public void Parse_RejectsDuplicateIngestionStallThresholdSeconds()
    {
        var result = MonitorOptions.Parse(
            ["--ingestion-stall-threshold-seconds", "3", "--ingestion-stall-threshold-seconds", "4"]);

        Assert.Equal("local-monitor accepts --ingestion-stall-threshold-seconds only once.", result.Error);
    }

    [Fact]
    public void Parse_RejectsDuplicateProjectionLagThresholdSeconds()
    {
        var result = MonitorOptions.Parse(
            ["--projection-lag-threshold-seconds", "7", "--projection-lag-threshold-seconds", "8"]);

        Assert.Equal("local-monitor accepts --projection-lag-threshold-seconds only once.", result.Error);
    }

    [Fact]
    public void Parse_DefaultsSkillDiscoveryOptionsToEmptyLists()
    {
        var result = MonitorOptions.Parse([]);

        Assert.Null(result.Error);
        Assert.NotNull(result.Options!.SkillDiscoveryProjectPaths);
        Assert.Empty(result.Options.SkillDiscoveryProjectPaths);
        Assert.NotNull(result.Options.SkillDiscoveryDirectories);
        Assert.Empty(result.Options.SkillDiscoveryDirectories);
    }

    [Fact]
    public void Parse_AcceptsSingleSkillDiscoveryProjectPath()
    {
        var result = MonitorOptions.Parse(["--skill-discovery-project-path", @"C:\repo\one"]);

        Assert.Null(result.Error);
        Assert.Equal([@"C:\repo\one"], result.Options!.SkillDiscoveryProjectPaths);
    }

    [Fact]
    public void Parse_Accepts16SkillDiscoveryProjectPathsPreservingOrderAndDuplicates()
    {
        var duplicate = Path.GetFullPath("dup-project-path");
        var arguments = new List<string>();
        var expected = new List<string>();
        for (var i = 0; i < 14; i++)
        {
            var value = Path.GetFullPath($"project-path-{i}");
            arguments.Add("--skill-discovery-project-path");
            arguments.Add(value);
            expected.Add(value);
        }

        arguments.Add("--skill-discovery-project-path");
        arguments.Add(duplicate);
        expected.Add(duplicate);
        arguments.Add("--skill-discovery-project-path");
        arguments.Add(duplicate);
        expected.Add(duplicate);

        var result = MonitorOptions.Parse(arguments.ToArray());

        Assert.Null(result.Error);
        Assert.Equal(16, result.Options!.SkillDiscoveryProjectPaths.Count);
        Assert.Equal(expected, result.Options.SkillDiscoveryProjectPaths);
    }

    [Fact]
    public void Parse_Rejects17thSkillDiscoveryProjectPath()
    {
        var arguments = Enumerable.Range(0, 17)
            .SelectMany(i => new[] { "--skill-discovery-project-path", Path.GetFullPath($"project-path-{i}") })
            .ToArray();

        var result = MonitorOptions.Parse(arguments);

        Assert.Equal("local-monitor accepts at most 16 --skill-discovery-project-path values.", result.Error);
    }

    [Fact]
    public void Parse_AcceptsSingleSkillDiscoveryDirectory()
    {
        var result = MonitorOptions.Parse(["--skill-discovery-directory", @"C:\repo\dir"]);

        Assert.Null(result.Error);
        Assert.Equal([@"C:\repo\dir"], result.Options!.SkillDiscoveryDirectories);
    }

    [Fact]
    public void Parse_Accepts32SkillDiscoveryDirectoriesPreservingOrderAndDuplicates()
    {
        var duplicate = Path.GetFullPath("dup-skill-dir");
        var arguments = new List<string>();
        var expected = new List<string>();
        for (var i = 0; i < 30; i++)
        {
            var value = Path.GetFullPath($"skill-dir-{i}");
            arguments.Add("--skill-discovery-directory");
            arguments.Add(value);
            expected.Add(value);
        }

        arguments.Add("--skill-discovery-directory");
        arguments.Add(duplicate);
        expected.Add(duplicate);
        arguments.Add("--skill-discovery-directory");
        arguments.Add(duplicate);
        expected.Add(duplicate);

        var result = MonitorOptions.Parse(arguments.ToArray());

        Assert.Null(result.Error);
        Assert.Equal(32, result.Options!.SkillDiscoveryDirectories.Count);
        Assert.Equal(expected, result.Options.SkillDiscoveryDirectories);
    }

    [Fact]
    public void Parse_Rejects33rdSkillDiscoveryDirectory()
    {
        var arguments = Enumerable.Range(0, 33)
            .SelectMany(i => new[] { "--skill-discovery-directory", Path.GetFullPath($"skill-dir-{i}") })
            .ToArray();

        var result = MonitorOptions.Parse(arguments);

        Assert.Equal("local-monitor accepts at most 32 --skill-discovery-directory values.", result.Error);
    }

    [Fact]
    public void Parse_RejectsSkillDiscoveryProjectPathAsFinalToken()
    {
        var result = MonitorOptions.Parse(["--skill-discovery-project-path"]);

        Assert.Equal("--skill-discovery-project-path requires a value.", result.Error);
    }

    [Fact]
    public void Parse_RejectsEmptySkillDiscoveryProjectPathValue()
    {
        var result = MonitorOptions.Parse(["--skill-discovery-project-path", ""]);

        Assert.Equal("--skill-discovery-project-path requires a value.", result.Error);
    }

    [Fact]
    public void Parse_RejectsSkillDiscoveryDirectoryAsFinalToken()
    {
        var result = MonitorOptions.Parse(["--skill-discovery-directory"]);

        Assert.Equal("--skill-discovery-directory requires a value.", result.Error);
    }

    [Fact]
    public void Parse_RejectsEmptySkillDiscoveryDirectoryValue()
    {
        var result = MonitorOptions.Parse(["--skill-discovery-directory", ""]);

        Assert.Equal("--skill-discovery-directory requires a value.", result.Error);
    }

    [Theory]
    [InlineData("--skill-discovery-project-path")]
    [InlineData("--skill-discovery-directory")]
    public void Parse_RejectsSkillDiscoveryOptionWithSanitizedOnlyBefore(string option)
    {
        var result = MonitorOptions.Parse(["--sanitized-only", option, @"C:\repo\value"]);

        Assert.Equal("skill discovery options cannot be used with --sanitized-only.", result.Error);
    }

    [Theory]
    [InlineData("--skill-discovery-project-path")]
    [InlineData("--skill-discovery-directory")]
    public void Parse_RejectsSkillDiscoveryOptionWithSanitizedOnlyAfter(string option)
    {
        var result = MonitorOptions.Parse([option, @"C:\repo\value", "--sanitized-only"]);

        Assert.Equal("skill discovery options cannot be used with --sanitized-only.", result.Error);
    }

    [Fact]
    public void Parse_PrecedenceMissingProjectPathBeatsMissingSkillDirectory()
    {
        string[] missingProjectPath = ["--skill-discovery-project-path"];
        string[] missingSkillDirectory = ["--skill-discovery-directory"];

        AssertPrecedenceBothOrders(
            missingProjectPath,
            missingSkillDirectory,
            "--skill-discovery-project-path requires a value.");
    }

    [Fact]
    public void Parse_PrecedenceMissingSkillDirectoryBeatsProjectPathCountLimit()
    {
        string[] missingSkillDirectory = ["--skill-discovery-directory"];
        var over16ProjectPaths = Enumerable.Range(0, 17)
            .SelectMany(i => new[] { "--skill-discovery-project-path", Path.GetFullPath($"project-path-{i}") })
            .ToArray();

        AssertPrecedenceBothOrders(
            missingSkillDirectory,
            over16ProjectPaths,
            "--skill-discovery-directory requires a value.");
    }

    [Fact]
    public void Parse_PrecedenceProjectPathCountLimitBeatsSkillDirectoryCountLimit()
    {
        var over16ProjectPaths = Enumerable.Range(0, 17)
            .SelectMany(i => new[] { "--skill-discovery-project-path", Path.GetFullPath($"project-path-{i}") })
            .ToArray();
        var over32SkillDirectories = Enumerable.Range(0, 33)
            .SelectMany(i => new[] { "--skill-discovery-directory", Path.GetFullPath($"skill-dir-{i}") })
            .ToArray();

        AssertPrecedenceBothOrders(
            over16ProjectPaths,
            over32SkillDirectories,
            "local-monitor accepts at most 16 --skill-discovery-project-path values.");
    }

    [Fact]
    public void Parse_PrecedenceSkillDirectoryCountLimitBeatsSanitizedOnlyConflict()
    {
        var over32SkillDirectories = Enumerable.Range(0, 33)
            .SelectMany(i => new[] { "--skill-discovery-directory", Path.GetFullPath($"skill-dir-{i}") })
            .ToArray();
        string[] sanitizedOnly = ["--sanitized-only"];

        AssertPrecedenceBothOrders(
            over32SkillDirectories,
            sanitizedOnly,
            "local-monitor accepts at most 32 --skill-discovery-directory values.");
    }

    [Fact]
    public void Parse_PrecedenceValidProjectPathWithSanitizedOnlyReportsConflict()
    {
        string[] validProjectPath = ["--skill-discovery-project-path", @"C:\repo\value"];
        string[] sanitizedOnly = ["--sanitized-only"];

        AssertPrecedenceBothOrders(
            validProjectPath,
            sanitizedOnly,
            "skill discovery options cannot be used with --sanitized-only.");
    }

    [Fact]
    public void Parse_PrecedenceAllFiveFaultsReportsMissingProjectPathValue()
    {
        var over16ProjectPathsPlusMissing = Enumerable.Range(0, 17)
            .SelectMany(i => new[] { "--skill-discovery-project-path", Path.GetFullPath($"project-path-{i}") })
            .Append("--skill-discovery-project-path")
            .ToArray();
        var over32SkillDirectoriesPlusMissing = Enumerable.Range(0, 33)
            .SelectMany(i => new[] { "--skill-discovery-directory", Path.GetFullPath($"skill-dir-{i}") })
            .Append("--skill-discovery-directory")
            .ToArray();
        string[] sanitizedOnly = ["--sanitized-only"];

        var forward = over16ProjectPathsPlusMissing
            .Concat(over32SkillDirectoriesPlusMissing)
            .Concat(sanitizedOnly)
            .ToArray();
        var reversed = sanitizedOnly
            .Concat(over32SkillDirectoriesPlusMissing)
            .Concat(over16ProjectPathsPlusMissing)
            .ToArray();

        Assert.Equal("--skill-discovery-project-path requires a value.", MonitorOptions.Parse(forward).Error);
        Assert.Equal("--skill-discovery-project-path requires a value.", MonitorOptions.Parse(reversed).Error);
    }

    [Fact]
    public void Parse_DoesNotLeakSkillDiscoveryValueInFailureMessage()
    {
        const string sentinel = @"C:\SENTINEL_ROOT_VALUE";
        var result = MonitorOptions.Parse(["--skill-discovery-project-path", sentinel, "--sanitized-only"]);

        Assert.Equal("skill discovery options cannot be used with --sanitized-only.", result.Error);
        Assert.DoesNotContain("SENTINEL", result.Error, StringComparison.Ordinal);
    }

    private static void AssertPrecedenceBothOrders(string[] first, string[] second, string expectedError)
    {
        var forward = first.Concat(second).ToArray();
        var reversed = second.Concat(first).ToArray();

        Assert.Equal(expectedError, MonitorOptions.Parse(forward).Error);
        Assert.Equal(expectedError, MonitorOptions.Parse(reversed).Error);
    }
}
