using CopilotAgentObservability.Persistence.Sqlite.Costs;
using CopilotAgentObservability.Pricing;

namespace CopilotAgentObservability.Persistence.Sqlite.Pricing;

public sealed record CostConfigurationApplicationResult<T>(
    bool Success,
    T? Value = default,
    string? ErrorCode = null,
    string? Location = null);

public sealed record CostConfigurationReadApplicationV1(
    string SchemaVersion,
    long HeadRevision,
    string? ConfigurationId,
    string? ConfigurationCatalogSha256,
    string ProviderCatalogSha256,
    string CatalogState,
    CostConfigurationV1? Configuration,
    int SelectedSessionCount,
    string SelectedSessionCountState);

public sealed record CostConfigurationVersionApplicationV1(
    string SchemaVersion,
    long HeadRevision,
    string ConfigurationId,
    string CatalogSha256,
    DateTimeOffset CommittedAtUtc,
    CostConfigurationV1 Configuration);

public sealed record CostCatalogApplicationV1(
    string SchemaVersion,
    string CatalogSha256,
    IReadOnlyList<CostCatalogSourceReadV1> Sources,
    IReadOnlyList<CostCatalogEntryReadV1> Entries,
    string? NextAfter);

public sealed class SqliteCostConfigurationApplicationService
{
    private readonly SqlitePricingStore store;
    private readonly SqlitePricingReadStore reads;
    private readonly PricingCatalog providerCatalog;
    private readonly byte[] providerCatalogBytes;
    private readonly string providerCatalogSha256;

    public SqliteCostConfigurationApplicationService(
        SqlitePricingStore store,
        SqlitePricingReadStore reads,
        PricingCatalog providerCatalog,
        ReadOnlyMemory<byte> providerCatalogBytes,
        string providerCatalogSha256)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(reads);
        ArgumentNullException.ThrowIfNull(providerCatalog);
        var frozenBytes = providerCatalogBytes.ToArray();
        PricingCatalog strict;
        try
        {
            strict = PricingCatalogSnapshotConsumer.Deserialize(frozenBytes);
        }
        catch (PricingRegistryValidationException)
        {
            throw new InvalidOperationException("cost_store_unavailable");
        }
        if (strict.CatalogSha256 != providerCatalogSha256
            || providerCatalog.CatalogSha256 != providerCatalogSha256
            || !PricingCanonicalJson.SerializeCatalogSnapshot(providerCatalog)
                .AsSpan()
                .SequenceEqual(frozenBytes))
            throw new InvalidOperationException("cost_store_unavailable");

        this.store = store;
        this.reads = reads;
        this.providerCatalog = providerCatalog;
        this.providerCatalogBytes = frozenBytes;
        this.providerCatalogSha256 = providerCatalogSha256;
    }

    public CostConfigurationApplicationResult<CostConfigurationReadApplicationV1>
        ReadCurrentConfiguration()
    {
        var result = reads.ReadCurrentConfiguration(providerCatalogSha256);
        return result.Status == PricingReadStatus.Success && result.Value is not null
            ? new(
                true,
                new(
                    "cost.configuration-read.v1",
                    result.Value.HeadRevision,
                    result.Value.ConfigurationId,
                    result.Value.ConfigurationCatalogSha256,
                    result.Value.ProviderCatalogSha256,
                    result.Value.CatalogState,
                    result.Value.Configuration,
                    result.Value.SelectedSessionCount,
                    result.Value.SelectedSessionCountState))
            : MapReadFailure<CostConfigurationReadApplicationV1>(result.Status);
    }

    public CostConfigurationApplicationResult<CostConfigurationVersionApplicationV1>
        ReadConfigurationVersion(string configurationId)
    {
        var result = reads.ReadConfigurationVersion(configurationId);
        return result.Status == PricingReadStatus.Success && result.Value is not null
            ? new(
                true,
                new(
                    "cost.configuration-version.v1",
                    result.Value.HeadRevision,
                    result.Value.ConfigurationId,
                    result.Value.CatalogSha256,
                    result.Value.CommittedAtUtc,
                    result.Value.Configuration))
            : MapReadFailure<CostConfigurationVersionApplicationV1>(
                result.Status,
                "cost_configuration_not_found");
    }

    public CostConfigurationApplicationResult<CostCatalogApplicationV1> ReadCatalog(
        string? after,
        int limit = 50)
    {
        var result = PricingCatalogReadProjectorV1.Read(
            providerCatalog,
            after,
            limit);
        return result.Status == PricingReadStatus.Success && result.Value is not null
            ? new(
                true,
                new(
                    "cost.catalog.v1",
                    result.Value.CatalogSha256,
                    result.Value.Sources,
                    result.Value.Entries,
                    result.Value.NextAfter))
            : MapCatalogFailure(result.Status);
    }

    public CostConfigurationApplicationResult<CostConfigurationPreviewV1>
        PreviewConfiguration(CostConfigurationPreviewRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CostConfigurationPreviewRequestV1 strict;
        try
        {
            var canonical =
                CostConfigurationPreviewRequestCanonicalJsonV1.Serialize(request);
            var consumed =
                CostConfigurationPreviewRequestConsumerV1.Consume(canonical);
            if (consumed.Status != CostConsumerStatus.Success
                || consumed.Value is null)
                return new(false, ErrorCode: "cost_invalid_configuration");
            strict = consumed.Value;
        }
        catch (ArgumentException)
        {
            return new(false, ErrorCode: "cost_invalid_configuration");
        }
        var result = store.CreateConfigurationPreviewApplication(
            strict,
            providerCatalogSha256);
        return MapStore(result);
    }

    public CostConfigurationApplicationResult<CostConfigurationPreviewV1>
        PreviewConfiguration(ReadOnlyMemory<byte> canonicalRequest)
    {
        var consumed =
            CostConfigurationPreviewRequestConsumerV1.Consume(canonicalRequest);
        if (consumed.Status != CostConsumerStatus.Success
            || consumed.Value is null)
            return new(false, ErrorCode: "cost_invalid_configuration");
        return PreviewConfiguration(consumed.Value);
    }

    public CostConfigurationApplicationResult<CostConfigurationCommitResultV1>
        CommitConfiguration(CostConfigurationPreviewV1 preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var result = store.AppendConfigurationCommitApplication(
            preview,
            new(providerCatalogSha256, providerCatalogBytes),
            recomputedSelection: null);
        if (result.Status != PricingStoreStatus.Success || result.Value is null)
            return MapStore(result);
        return new(
            true,
            result.Value,
            Location:
                $"/api/costs/v1/configurations/{result.Value.ConfigurationId}");
    }

    private static CostConfigurationApplicationResult<T> MapReadFailure<T>(
        PricingReadStatus status,
        string notFound = "cost_store_unavailable") =>
        status switch
        {
            PricingReadStatus.NotFound => new(false, ErrorCode: notFound),
            PricingReadStatus.Busy => new(false, ErrorCode: "cost_store_busy"),
            _ => new(false, ErrorCode: "cost_store_unavailable"),
        };

    private static CostConfigurationApplicationResult<CostCatalogApplicationV1>
        MapCatalogFailure(PricingReadStatus status) =>
        status switch
        {
            PricingReadStatus.InvalidCursor =>
                new(false, ErrorCode: "cost_invalid_cursor"),
            PricingReadStatus.CatalogChanged =>
                new(false, ErrorCode: "cost_catalog_changed"),
            PricingReadStatus.ResponseTooLarge =>
                new(false, ErrorCode: "cost_response_too_large"),
            PricingReadStatus.Busy =>
                new(false, ErrorCode: "cost_store_busy"),
            _ => new(false, ErrorCode: "cost_store_unavailable"),
        };

    private static CostConfigurationApplicationResult<T> MapStore<T>(
        PricingStoreResult<T> result) =>
        result.Status switch
        {
            PricingStoreStatus.Success when result.Value is not null =>
                new(true, result.Value),
            PricingStoreStatus.Busy =>
                new(false, ErrorCode: "cost_store_busy"),
            PricingStoreStatus.Unavailable =>
                new(false, ErrorCode: "cost_store_unavailable"),
            PricingStoreStatus.ContractRejected =>
                new(false, ErrorCode: result.ErrorCode ?? "cost_invalid_configuration"),
            PricingStoreStatus.CapacityReached =>
                new(false, ErrorCode: "cost_preview_capacity_reached"),
            _ => new(false, ErrorCode: result.ErrorCode ?? "cost_store_unavailable"),
        };
}
