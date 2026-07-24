using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Pricing;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class PricingCatalogProviderTests
{
    [Fact]
    public void Create_LoadsBundledCatalogAndReturnsAnImmutableCanonicalSnapshot()
    {
        var provider = DefaultPricingCatalogProvider.Create([]);

        Assert.Single(provider.Catalog.Documents);
        Assert.Equal(PricingRegistrySourceKinds.Bundled, provider.Catalog.Documents[0].SourceKind);
        Assert.Equal(provider.Catalog.CatalogSha256, provider.CatalogSha256);
        Assert.Equal(
            provider.CanonicalCatalogBytes.ToArray(),
            PricingCanonicalJson.SerializeCatalogSnapshot(provider.Catalog));
        var expected = provider.CanonicalCatalogBytes.ToArray();
        Assert.True(MemoryMarshal.TryGetArray(provider.CanonicalCatalogBytes, out var exposed));
        exposed.Array![exposed.Offset] ^= 0xff;
        Assert.Equal(expected, provider.CanonicalCatalogBytes.ToArray());
    }

    [Fact]
    public void Create_AppendsOverridesInCallerOrderWithoutRetainingLocators()
    {
        using var temp = new PricingCatalogProviderTempDirectory();
        var first = temp.WriteOverride("first-override");
        var second = temp.WriteOverride("second-override");

        var firstDocument = PricingRegistryLoader.Deserialize(StrictLocalFileReader.ReadUtf8(first));
        Assert.Equal("first-override", firstDocument.SourceId);
        var provider = DefaultPricingCatalogProvider.Create([first, second]);

        Assert.Equal(
            ["official-reviewed", "first-override", "second-override"],
            provider.Catalog.Documents.Select(document => document.SourceId));
        Assert.DoesNotContain(first, provider.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(second, provider.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            first,
            Encoding.UTF8.GetString(provider.CanonicalCatalogBytes.Span),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("relative")]
    [InlineData("invalid")]
    [InlineData("future")]
    [InlineData("oversize")]
    [InlineData("invalid-utf8")]
    public void Create_RejectsUnavailableOverrideWithFixedPathFreeError(string scenario)
    {
        using var temp = new PricingCatalogProviderTempDirectory();
        var marker = $"private-{scenario}-locator";
        var path = scenario switch
        {
            "missing" => Path.Combine(temp.Root, $"{marker}.json"),
            "relative" => $"{marker}.json",
            "invalid" => temp.WriteBytes(marker, Encoding.UTF8.GetBytes("{")),
            "future" => temp.WriteOverride(marker, schemaVersion: "pricing.registry.v2"),
            "oversize" => temp.WriteBytes(marker, new byte[1_048_577]),
            "invalid-utf8" => temp.WriteBytes(marker, [0xc3, 0x28]),
            _ => throw new InvalidOperationException()
        };

        var error = Assert.Throws<PricingCatalogUnavailableException>(
            () => DefaultPricingCatalogProvider.Create([path]));

        Assert.Equal("pricing_catalog_unavailable", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(marker, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(path, error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_RejectsDuplicateLocatorWithoutEchoingIt()
    {
        using var temp = new PricingCatalogProviderTempDirectory();
        var path = temp.WriteOverride("duplicate-override");

        var error = Assert.Throws<PricingCatalogUnavailableException>(
            () => DefaultPricingCatalogProvider.Create([path, path]));

        Assert.Equal("pricing_catalog_unavailable", error.Message);
        Assert.DoesNotContain(path, error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_RejectsAReparsePointTargetWhenThePlatformCanCreateOne()
    {
        using var temp = new PricingCatalogProviderTempDirectory();
        var target = temp.WriteOverride("reparse-target");
        var link = Path.Combine(temp.Root, "private-reparse-locator.json");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var failure = Assert.Throws<PricingCatalogUnavailableException>(
            () => DefaultPricingCatalogProvider.Create([link]));

        Assert.Equal("pricing_catalog_unavailable", failure.Message);
        Assert.DoesNotContain(link, failure.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_RejectsAReparsePointAncestorWhenThePlatformCanCreateOne()
    {
        using var temp = new PricingCatalogProviderTempDirectory();
        var targetDirectory = Path.Combine(temp.Root, "target-directory");
        Directory.CreateDirectory(targetDirectory);
        var target = temp.WriteOverrideAt(targetDirectory, "ancestor-target");
        var link = Path.Combine(temp.Root, "private-reparse-directory");
        try
        {
            Directory.CreateSymbolicLink(link, targetDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var linkedTarget = Path.Combine(link, Path.GetFileName(target));
        var failure = Assert.Throws<PricingCatalogUnavailableException>(
            () => DefaultPricingCatalogProvider.Create([linkedTarget]));

        Assert.Equal("pricing_catalog_unavailable", failure.Message);
        Assert.DoesNotContain(link, failure.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_RejectsAWindowsTargetThatIsOpenForMutation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new PricingCatalogProviderTempDirectory();
        var path = temp.WriteOverride("mutation-target");
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        var failure = Assert.Throws<PricingCatalogUnavailableException>(
            () => DefaultPricingCatalogProvider.Create([path]));

        Assert.Equal("pricing_catalog_unavailable", failure.Message);
        Assert.DoesNotContain(path, failure.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_RejectsAWindowsPathWithAWin32NormalizedSegment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new PricingCatalogProviderTempDirectory();
        var path = temp.WriteOverride("normalized-target");
        var alias = $"{path}.";

        var failure = Assert.Throws<PricingCatalogUnavailableException>(
            () => DefaultPricingCatalogProvider.Create([alias]));

        Assert.Equal("pricing_catalog_unavailable", failure.Message);
        Assert.DoesNotContain(path, failure.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ReportsOnlyTheFixedCatalogFailure()
    {
        using var temp = new MonitorTempDirectory();
        const string marker = "private-missing-pricing-registry";
        var path = Path.Combine(Path.GetDirectoryName(temp.DatabasePath)!, $"{marker}.json");
        var options = new MonitorOptions(
            temp.DatabasePath,
            "http://127.0.0.1:0",
            false,
            MonitorOptions.DefaultMaxRequestBodyBytes,
            PricingRegistryOverridePaths: [path]);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await MonitorHost.RunAsync(
            options,
            output,
            error,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal($"error: pricing_catalog_unavailable{Environment.NewLine}", error.ToString());
        Assert.DoesNotContain(marker, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(path, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void Build_RegistersTheValidatedImmutableCatalogProvider()
    {
        using var temp = new MonitorTempDirectory();
        using var overrideDirectory = new PricingCatalogProviderTempDirectory();
        var path = overrideDirectory.WriteOverride("host-override");
        var options = new MonitorOptions(
            temp.DatabasePath,
            "http://127.0.0.1:0",
            false,
            MonitorOptions.DefaultMaxRequestBodyBytes,
            PricingRegistryOverridePaths: [path]);

        using var app = MonitorHost.Build(
            options,
            new MonitorHostTestOptions
            {
                StartWriter = false,
                StartProjectionWorker = false,
                StartSessionWriter = false,
                StartSessionOtelEnrichment = false,
                StartRetentionCleanupWorker = false,
                UseUserSecrets = false
            });

        var provider = app.Services.GetRequiredService<IPricingCatalogProvider>();
        Assert.Equal("host-override", provider.Catalog.Documents[1].SourceId);
        Assert.Empty(app.Services.GetRequiredService<MonitorOptions>().PricingRegistryOverridePaths!);
    }

    private sealed class PricingCatalogProviderTempDirectory : IDisposable
    {
        internal PricingCatalogProviderTempDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), $"cao-pricing-provider-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        internal string WriteOverride(string sourceId, string schemaVersion = PricingContractVersions.Registry)
        {
            var bundled = BundledPricingRegistry.Load();
            var source = bundled.Entries[0];
            var document = bundled with
            {
                SchemaVersion = schemaVersion,
                RegistryVersion = $"{sourceId}-v1",
                SourceKind = PricingRegistrySourceKinds.LocalOverride,
                SourceId = sourceId,
                SourceLabel = $"{sourceId} reviewed source",
                Entries =
                [
                    source with
                    {
                        EntryId = $"{sourceId}-entry",
                        Revision = 1,
                        SupersedesEntryKey = null,
                        CanonicalModelId = $"{sourceId}-model",
                        Aliases = []
                    }
                ]
            };
            var json = JsonSerializer.Serialize(
                document,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
                .Replace("+00:00", "Z", StringComparison.Ordinal);
            return WriteBytes(sourceId, Encoding.UTF8.GetBytes(json));
        }

        internal string WriteBytes(string name, byte[] bytes)
        {
            return WriteBytesAt(Root, name, bytes);
        }

        internal string WriteOverrideAt(string directory, string sourceId)
        {
            var path = WriteOverride(sourceId);
            var destination = Path.Combine(directory, Path.GetFileName(path));
            File.Move(path, destination);
            return destination;
        }

        private static string WriteBytesAt(string directory, string name, byte[] bytes)
        {
            var path = Path.Combine(directory, $"{name}.json");
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
