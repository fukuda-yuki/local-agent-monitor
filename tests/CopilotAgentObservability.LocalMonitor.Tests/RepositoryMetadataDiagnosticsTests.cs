using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RepositoryMetadataDiagnosticsTests
{
    [Fact]
    public void Build_EmitsOnlySafeKeyCountScopeAndClassification()
    {
        const string payload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"vcs.repository.name","value":{"stringValue":"attribute-value-marker"}},
          {"key":"workspace.name","value":{"stringValue":"workspace-value-marker"}},
          {"key":"user.email","value":{"stringValue":"identity@example.test"}},
          {"key":"repository.custom","value":{"stringValue":"custom-value-marker"}},
          {"key":"repository.custom","value":{"stringValue":"duplicate-custom-value-marker"}},
          {"key":"repository/C:/private/path","value":{"stringValue":"path-value-marker"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-diagnostics","spanId":"1111111111111111","name":"chat","attributes":[
            {"key":"vcs.ref.head.revision","value":{"stringValue":"revision-value-marker"}},
            {"key":"repository.custom","value":{"stringValue":"second-custom-value-marker"}}
          ],"events":[{"name":"event","attributes":[
            {"key":"vcs.provider.name","value":{"stringValue":"provider-value-marker"}},
            {"key":"repository.custom","value":{"stringValue":"third-custom-value-marker"}}
          ]}]}
        ]}]}]}
        """;

        var diagnostic = RepositoryMetadataDiagnostics.Build(payload);

        Assert.Equal(RepositoryMetadataStatus.MetadataPresent, diagnostic.Status);
        Assert.True(diagnostic.RepositoryLabelPresent);
        Assert.False(diagnostic.UrlFallbackUsed);
        Assert.Contains(diagnostic.Inventory, row =>
            row.Key == "vcs.repository.name"
            && row.Count == 1
            && row.Scope == RepositoryMetadataAttributeScope.Resource
            && row.Classification == RepositoryMetadataAttributeClassification.Repository);
        Assert.Contains(diagnostic.Inventory, row =>
            row.Key == "repository.custom"
            && row.Count == 2
            && row.Scope == RepositoryMetadataAttributeScope.Resource
            && row.Classification == RepositoryMetadataAttributeClassification.Repository);
        Assert.Contains(diagnostic.Inventory, row =>
            row.Key == "repository.custom"
            && row.Count == 1
            && row.Scope == RepositoryMetadataAttributeScope.Span);
        Assert.Contains(diagnostic.Inventory, row =>
            row.Key == "repository.custom"
            && row.Count == 1
            && row.Scope == RepositoryMetadataAttributeScope.Event);
        Assert.DoesNotContain(diagnostic.Inventory, row => row.Key.Contains("private", StringComparison.OrdinalIgnoreCase));

        var serialized = JsonSerializer.Serialize(diagnostic.Inventory);
        Assert.DoesNotContain("attribute-value-marker", serialized);
        Assert.DoesNotContain("workspace-value-marker", serialized);
        Assert.DoesNotContain("identity@example.test", serialized);
        Assert.DoesNotContain("custom-value-marker", serialized);
        Assert.DoesNotContain("path-value-marker", serialized);
    }

    [Fact]
    public void Build_SuppressesOverlongKeysInsteadOfTruncatingThem()
    {
        var maximumLengthKey = new string('r', 128);
        var overlongKey = new string('w', 129);
        var payload = ResourcePayload(
            "key-length",
            Attribute(maximumLengthKey, "safe-value"),
            Attribute(overlongKey, "safe-value"));

        var diagnostic = RepositoryMetadataDiagnostics.Build(payload);

        var row = Assert.Single(diagnostic.Inventory);
        Assert.Equal(maximumLengthKey, row.Key);
        Assert.DoesNotContain(diagnostic.Inventory, item => item.Key.StartsWith(overlongKey[..128], StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("metadata_present", "vcs.repository.name", "safe-repository", "vcs.repository.url.full", "https://github.com/example/safe-fallback", true, false)]
    [InlineData("url_fallback_used", "client.kind", "copilot-cli", "vcs.repository.url.full", "https://github.com/example/safe-repository.git", true, true)]
    [InlineData("unsafe_value_rejected", "vcs.repository.name", "identity@example.test", "vcs.repository.url.full", "https://github.com/example/safe-fallback", false, false)]
    [InlineData("unsupported_candidate_present", "workspace.name", "safe-workspace", "client.kind", "copilot-cli", false, false)]
    [InlineData("metadata_not_present", "client.kind", "copilot-cli", "experiment.id", "experiment", false, false)]
    public void Build_UsesExactStatusPrecedence(
        string expectedStatus,
        string firstKey,
        string firstValue,
        string secondKey,
        string secondValue,
        bool expectedLabelPresent,
        bool expectedFallback)
    {
        var payload = ResourcePayload("status-precedence", Attribute(firstKey, firstValue), Attribute(secondKey, secondValue));

        var diagnostic = RepositoryMetadataDiagnostics.Build(payload);

        Assert.Equal(expectedStatus, RepositoryMetadataDiagnostics.StatusWire(diagnostic.Status));
        Assert.Equal(expectedLabelPresent, diagnostic.RepositoryLabelPresent);
        Assert.Equal(expectedFallback, diagnostic.UrlFallbackUsed);
    }

    [Theory]
    [InlineData("https://gitlab.com/group/repository", "unsupported_candidate_present")]
    [InlineData("https://github.com/example/repository/tree/main", "unsafe_value_rejected")]
    [InlineData("https://github.com/example/repository?token=value", "unsafe_value_rejected")]
    [InlineData("https://github.com/example/repository#fragment", "unsafe_value_rejected")]
    [InlineData("https://identity:credential@github.com/example/repository", "unsafe_value_rejected")]
    [InlineData("https://github.com:8443/example/repository", "unsafe_value_rejected")]
    [InlineData("https://github.com/example%2Frepository", "unsafe_value_rejected")]
    [InlineData("https://github.com/example/..", "unsafe_value_rejected")]
    [InlineData("http://github.com/example/repository", "unsafe_value_rejected")]
    [InlineData("file:///C:/private/repository", "unsafe_value_rejected")]
    [InlineData("C:\\private\\repository", "unsafe_value_rejected")]
    [InlineData("\\\\server\\share\\repository", "unsafe_value_rejected")]
    [InlineData("git@github.com:example/repository.git", "unsafe_value_rejected")]
    [InlineData("ssh://git@github.com/example/repository.git", "unsafe_value_rejected")]
    [InlineData("https://github.com/identity@example.test/repository", "unsafe_value_rejected")]
    [InlineData("https://github.com/example/github_pat_123456789012345678901234", "unsafe_value_rejected")]
    public void Build_ClassifiesUnsupportedAndUnsafeUrlCandidates(string value, string expectedStatus)
    {
        var payload = ResourcePayload("url-candidate", Attribute("vcs.repository.url.full", value));

        var diagnostic = RepositoryMetadataDiagnostics.Build(payload);

        Assert.Equal(expectedStatus, RepositoryMetadataDiagnostics.StatusWire(diagnostic.Status));
        Assert.False(diagnostic.RepositoryLabelPresent);
        Assert.False(diagnostic.UrlFallbackUsed);
    }

    [Theory]
    [InlineData("https://github.com/example/repository")]
    [InlineData("owner/repository")]
    [InlineData("ghp_123456789012345678901234")]
    public void Build_RejectsUnsafeAuthoritativeRepositoryNames(string value)
    {
        var diagnostic = RepositoryMetadataDiagnostics.Build(
            ResourcePayload("unsafe-name", Attribute("vcs.repository.name", value)));

        Assert.Equal(RepositoryMetadataStatus.UnsafeValueRejected, diagnostic.Status);
        Assert.False(diagnostic.RepositoryLabelPresent);
    }

    [Fact]
    public void Build_RejectsPathShapeBeforeRepositoryNameTruncation()
    {
        var value = new string('a', 300) + "/repository";
        var diagnostic = RepositoryMetadataDiagnostics.Build(
            ResourcePayload("long-unsafe-name", Attribute("vcs.repository.name", value)));

        Assert.Equal(RepositoryMetadataStatus.UnsafeValueRejected, diagnostic.Status);
        Assert.False(diagnostic.RepositoryLabelPresent);
    }

    [Fact]
    public void Build_UsesLastDuplicateAuthoritativeNameLikeProjectionConversion()
    {
        var unsafeLast = RepositoryMetadataDiagnostics.Build(ResourcePayload(
            "duplicate-name-unsafe-last",
            Attribute("vcs.repository.name", "safe-repository"),
            Attribute("vcs.repository.name", "identity@example.test")));
        var safeLast = RepositoryMetadataDiagnostics.Build(ResourcePayload(
            "duplicate-name-safe-last",
            Attribute("vcs.repository.name", "identity@example.test"),
            Attribute("vcs.repository.name", "safe-repository")));

        Assert.Equal(RepositoryMetadataStatus.UnsafeValueRejected, unsafeLast.Status);
        Assert.Equal(RepositoryMetadataStatus.MetadataPresent, safeLast.Status);
    }

    [Fact]
    public void Build_UsesLastDuplicateUrlLikeProjectionConversion()
    {
        var unsafeLast = RepositoryMetadataDiagnostics.Build(ResourcePayload(
            "duplicate-url-unsafe-last",
            Attribute("vcs.repository.url.full", "https://github.com/example/safe-repository"),
            Attribute("vcs.repository.url.full", "https://github.com/example/repository?token=value")));
        var safeLast = RepositoryMetadataDiagnostics.Build(ResourcePayload(
            "duplicate-url-safe-last",
            Attribute("vcs.repository.url.full", "https://github.com/example/repository?token=value"),
            Attribute("vcs.repository.url.full", "https://github.com/example/safe-repository")));

        Assert.Equal(RepositoryMetadataStatus.UnsafeValueRejected, unsafeLast.Status);
        Assert.Equal(RepositoryMetadataStatus.UrlFallbackUsed, safeLast.Status);
    }

    [Fact]
    public void Build_DoesNotProjectSpanOrEventCandidatesToTraceMetadata()
    {
        const string payload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"scope-only","spanId":"1111111111111111","name":"chat","attributes":[
            {"key":"vcs.repository.name","value":{"stringValue":"span-repository"}}
          ],"events":[{"name":"event","attributes":[
            {"key":"vcs.repository.url.full","value":{"stringValue":"https://github.com/example/event-repository"}}
          ]}]}
        ]}]}]}
        """;

        var diagnostic = RepositoryMetadataDiagnostics.Build(payload);

        Assert.Equal(RepositoryMetadataStatus.UnsupportedCandidatePresent, diagnostic.Status);
        Assert.False(diagnostic.RepositoryLabelPresent);
        Assert.False(diagnostic.UrlFallbackUsed);
    }

    [Fact]
    public void Build_UnsupportedAuthoritativeValueTypeBlocksUrlFallback()
    {
        const string payload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"vcs.repository.name","value":{"intValue":"42"}},
          {"key":"vcs.repository.url.full","value":{"stringValue":"https://github.com/example/safe-fallback"}}
        ]},"scopeSpans":[{"spans":[]}]}]}
        """;

        var diagnostic = RepositoryMetadataDiagnostics.Build(payload);

        Assert.Equal(RepositoryMetadataStatus.UnsupportedCandidatePresent, diagnostic.Status);
        Assert.False(diagnostic.RepositoryLabelPresent);
        Assert.False(diagnostic.UrlFallbackUsed);
    }

    [Theory]
    [InlineData("vcs.repository.name", "repository")]
    [InlineData("vcs.repository.url.full", "repository")]
    [InlineData("vcs.owner.name", "vcs")]
    [InlineData("vcs.provider.name", "vcs")]
    [InlineData("vcs.ref.head.name", "vcs")]
    [InlineData("vcs.ref.head.revision", "vcs")]
    [InlineData("workspace.name", "workspace")]
    public void Build_ClassifiesKnownInventoryKeys(
        string key,
        string expectedClassification)
    {
        var diagnostic = RepositoryMetadataDiagnostics.Build(
            ResourcePayload("known-key", Attribute(key, "safe-value")));

        var row = Assert.Single(diagnostic.Inventory);
        Assert.Equal(key, row.Key);
        Assert.Equal(expectedClassification, RepositoryMetadataDiagnostics.ClassificationWire(row.Classification));
    }

    private static string ResourcePayload(string traceId, params string[] attributes) =>
        "{\"resourceSpans\":[{\"resource\":{\"attributes\":[" + string.Join(",", attributes)
        + "]},\"scopeSpans\":[{\"spans\":[{\"traceId\":" + JsonSerializer.Serialize(traceId)
        + ",\"spanId\":\"1111111111111111\",\"name\":\"chat\"}]}]}]}";

    private static string Attribute(string key, string value) =>
        JsonSerializer.Serialize(new { key, value = new { stringValue = value } });
}
