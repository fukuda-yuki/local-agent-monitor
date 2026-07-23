using System.Text.Json;
using System.Security.Cryptography;

namespace CopilotAgentObservability.Pricing.Tests;

public sealed class PricingCanonicalJsonTests
{
    [Fact]
    public void Exact_replay_is_byte_identical_and_identity_is_stable()
    {
        var engine = new PricingEstimationEngine(
            PricingCatalog.Create(PricingTestData.SyntheticRegistry()));
        var request = PricingTestData.Request();

        var first = engine.Estimate(request);
        var second = engine.Estimate(request);
        var firstBytes = PricingCanonicalJson.Serialize(first);
        var secondBytes = PricingCanonicalJson.Serialize(second);
        var golden = File.ReadAllBytes(
            Path.Combine(
                AppContext.BaseDirectory,
                "TestData",
                "pricing-estimate.synthetic.golden.json"));

        Assert.Equal(first.EstimateId, second.EstimateId);
        Assert.Equal(firstBytes, secondBytes);
        Assert.Equal(
            "pricing-estimate-330d04cb06f33a195f83a0dfd494c209910791baf6e1aa0edd18abca4c9cb3a6",
            first.EstimateId);
        Assert.Equal(golden, firstBytes);
    }

    [Fact]
    public void Recalculation_appends_a_new_record_linked_to_the_original()
    {
        var engine = new PricingEstimationEngine(
            PricingCatalog.Create(PricingTestData.SyntheticRegistry()));
        var original = engine.Estimate(PricingTestData.Request());
        var recalculated = engine.Estimate(PricingTestData.Request(
            calculatedAt: new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero),
            supersedes: original.EstimateId));

        Assert.NotEqual(original.EstimateId, recalculated.EstimateId);
        Assert.Equal(original.EstimateId, recalculated.SupersedesEstimateId);
        Assert.Equal(original.Amount, recalculated.Amount);
    }

    [Fact]
    public void Recalculation_can_use_a_superseding_registry_without_mutating_the_original()
    {
        var bundled = PricingTestData.SyntheticRegistry();
        var originalEngine = new PricingEstimationEngine(PricingCatalog.Create(bundled));
        var original = originalEngine.Estimate(PricingTestData.Request());
        var baseEntry = bundled.Entries[0];
        var replacement = baseEntry with
        {
            EntryId = "synthetic-token-repriced",
            SupersedesEntryKey = $"{bundled.SourceId}:{baseEntry.EntryId}@{baseEntry.Revision}",
            Rates = baseEntry.Rates with { InputPerMillionTokens = 0.20m }
        };
        var local = bundled with
        {
            RegistryVersion = "local-reprice-1",
            SourceKind = PricingRegistrySourceKinds.LocalOverride,
            SourceId = "local-reprice",
            SourceLabel = "Synthetic reviewed repricing",
            Entries = [replacement]
        };
        var recalculationEngine = new PricingEstimationEngine(
            PricingCatalog.Create(bundled, local));

        var recalculated = recalculationEngine.Estimate(PricingTestData.Request(
            calculatedAt: new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero),
            supersedes: original.EstimateId));

        Assert.Equal(0.0007m, original.Amount);
        Assert.Equal(0.0008m, recalculated.Amount);
        Assert.Equal(original.EstimateId, recalculated.SupersedesEstimateId);
        Assert.Equal("local-reprice-1", recalculated.Registry?.RegistryVersion);
        Assert.NotEqual(original.EstimateId, recalculated.EstimateId);
    }

    [Fact]
    public void Canonical_output_contains_no_conversion_quality_or_effect_claim()
    {
        var engine = new PricingEstimationEngine(
            PricingCatalog.Create(PricingTestData.SyntheticRegistry()));
        var bytes = PricingCanonicalJson.Serialize(
            engine.Estimate(PricingTestData.Request()));
        using var document = JsonDocument.Parse(bytes);
        var propertyNames = EnumeratePropertyNames(document.RootElement).ToArray();

        Assert.DoesNotContain("converted_amount", propertyNames);
        Assert.DoesNotContain("quality", propertyNames);
        Assert.DoesNotContain("effect", propertyNames);
        Assert.DoesNotContain("invoice", propertyNames);
    }

    [Fact]
    public void Decimal_scale_does_not_change_canonical_bytes_or_identity()
    {
        var engine = new PricingEstimationEngine(
            PricingCatalog.Create(PricingTestData.SyntheticRegistry()));
        var first = engine.Estimate(PricingTestData.Request(usage:
            PricingUsage.Empty with
            {
                InputTokens = PricingTestData.Quantity(1_000.0m),
                OutputTokens = PricingTestData.Quantity(2_000.00m)
            }));
        var second = engine.Estimate(PricingTestData.Request(usage:
            PricingUsage.Empty with
            {
                InputTokens = PricingTestData.Quantity(1_000m),
                OutputTokens = PricingTestData.Quantity(2_000m)
            }));

        Assert.Equal(first.EstimateId, second.EstimateId);
        Assert.Equal(PricingCanonicalJson.Serialize(first), PricingCanonicalJson.Serialize(second));
    }

    [Fact]
    public void Strict_consumer_round_trips_and_rejects_unknown_or_tampered_records()
    {
        const string sensitiveMarker = "sensitive-field-9x";
        var engine = new PricingEstimationEngine(
            PricingCatalog.Create(PricingTestData.SyntheticRegistry()));
        var record = engine.Estimate(PricingTestData.Request());
        var json = PricingCanonicalJson.Serialize(record);
        var catalog = PricingCatalog.Create(PricingTestData.SyntheticRegistry());

        var roundTrip = PricingEstimateConsumer.Deserialize(json, catalog);
        Assert.Equal(json, PricingCanonicalJson.Serialize(roundTrip));

        var text = System.Text.Encoding.UTF8.GetString(json);
        var unknown = text.Replace(
            "\"schema_version\":\"pricing.estimate.v1\",",
            $"\"schema_version\":\"pricing.estimate.v1\",\"{sensitiveMarker}\":true,",
            StringComparison.Ordinal);
        var tampered = text.Replace("\"amount\":0.0007", "\"amount\":0.0008", StringComparison.Ordinal);
        var noncanonical = text.Replace(
            "\"amount\":0.0007",
            "\"amount\":0.000700",
            StringComparison.Ordinal);

        var unknownError = Assert.Throws<PricingEstimateValidationException>(() =>
            PricingEstimateConsumer.Deserialize(
                System.Text.Encoding.UTF8.GetBytes(unknown),
                catalog));
        Assert.Null(unknownError.InnerException);
        Assert.DoesNotContain(
            sensitiveMarker,
            unknownError.ToString(),
            StringComparison.Ordinal);
        Assert.Throws<PricingEstimateValidationException>(() =>
            PricingEstimateConsumer.Deserialize(
                System.Text.Encoding.UTF8.GetBytes(tampered),
                catalog));
        Assert.Throws<PricingEstimateValidationException>(() =>
            PricingEstimateConsumer.Deserialize(
                System.Text.Encoding.UTF8.GetBytes(noncanonical),
                catalog));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)roundTrip.Reasons).Add("mutated"));
    }

    [Fact]
    public void Strict_consumer_rejects_a_recomputable_identity_from_the_wrong_catalog()
    {
        var originalCatalog = PricingCatalog.Create(PricingTestData.SyntheticRegistry());
        var original = new PricingEstimationEngine(originalCatalog).Estimate(
            PricingTestData.Request());
        var registry = PricingTestData.SyntheticRegistry();
        var source = registry.Entries[0];
        var replacement = source with
        {
            EntryId = "synthetic-consumer-reprice",
            SupersedesEntryKey = $"{registry.SourceId}:{source.EntryId}@{source.Revision}",
            Rates = source.Rates with { InputPerMillionTokens = 0.20m }
        };
        var local = registry with
        {
            RegistryVersion = "synthetic-consumer-reprice-v1",
            SourceKind = PricingRegistrySourceKinds.LocalOverride,
            SourceId = "consumer-reprice",
            Entries = [replacement]
        };
        var wrongCatalog = PricingCatalog.Create(registry, local);

        Assert.Throws<PricingEstimateValidationException>(() =>
            PricingEstimateConsumer.Deserialize(
                PricingCanonicalJson.Serialize(original),
                wrongCatalog));
    }

    [Fact]
    public void Strict_consumer_binds_unselected_catalog_entries()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var originalCatalog = PricingCatalog.Create(registry);
        var original = new PricingEstimationEngine(originalCatalog).Estimate(
            PricingTestData.Request());
        var unselected = registry.Entries[0] with
        {
            EntryId = "unselected-entry",
            CanonicalModelId = "unselected-model",
            Aliases = []
        };
        var changedCatalog = PricingCatalog.Create(registry with
        {
            RegistryVersion = "synthetic-with-unselected-v1",
            Entries = [.. registry.Entries, unselected]
        });

        Assert.Throws<PricingEstimateValidationException>(() =>
            PricingEstimateConsumer.Deserialize(
                PricingCanonicalJson.Serialize(original),
                changedCatalog));
    }

    [Fact]
    public void Catalog_snapshot_has_canonical_bytes_and_round_trips_with_the_same_digest()
    {
        var catalog = PricingCatalog.Create(PricingTestData.SyntheticRegistry());
        var bytes = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
        var golden = File.ReadAllBytes(
            Path.Combine(
                AppContext.BaseDirectory,
                "TestData",
                "pricing-catalog.synthetic.golden.json"));

        var consumed = PricingCatalogSnapshotConsumer.Deserialize(bytes);

        Assert.Equal(
            "de5be646e5841d67583dc1e2a037194b7adce751e2dcffd02a39b74ec573931b",
            catalog.CatalogSha256);
        Assert.Equal(golden, bytes);
        Assert.Equal(catalog.CatalogSha256, consumed.CatalogSha256);
        Assert.Equal(bytes, PricingCanonicalJson.SerializeCatalogSnapshot(consumed));
    }

    [Fact]
    public void Unselected_not_estimable_record_is_bound_to_the_exact_full_catalog()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var originalCatalog = PricingCatalog.Create(registry);
        var result = new PricingEstimationEngine(originalCatalog).Estimate(
            PricingTestData.Request(model: "unknown-model"));
        var unselected = registry.Entries[0] with
        {
            EntryId = "unselected-for-not-estimable",
            CanonicalModelId = "another-model",
            Aliases = []
        };
        var wrongCatalog = PricingCatalog.Create(registry with
        {
            Entries = [.. registry.Entries, unselected]
        });
        var bytes = PricingCanonicalJson.Serialize(result);

        Assert.Equal(PricingEstimateStatuses.NotEstimable, result.Status);
        Assert.Null(result.Registry);
        Assert.Equal(
            result.EstimateId,
            PricingEstimateConsumer.Deserialize(bytes, originalCatalog).EstimateId);
        Assert.Throws<PricingEstimateValidationException>(() =>
            PricingEstimateConsumer.Deserialize(
                bytes,
                wrongCatalog));
    }

    [Fact]
    public void Registry_null_not_estimable_rejects_an_identity_valid_invalid_status_shape()
    {
        var catalog = PricingCatalog.Create(PricingTestData.SyntheticRegistry());
        var original = new PricingEstimationEngine(catalog).Estimate(
            PricingTestData.Request(model: "unknown-model"));
        var malformed = Reidentify(original with
        {
            Status = PricingEstimateStatuses.Estimated
        });

        Assert.Throws<PricingEstimateValidationException>(() =>
            PricingEstimateConsumer.Deserialize(
                PricingCanonicalJson.Serialize(malformed),
                catalog));
    }

    [Fact]
    public void Catalog_snapshot_rejects_unknown_and_noncanonical_bytes()
    {
        const string sensitiveMarker = "sensitive-field-9x";
        var catalog = PricingCatalog.Create(PricingTestData.SyntheticRegistry());
        var text = System.Text.Encoding.UTF8.GetString(
            PricingCanonicalJson.SerializeCatalogSnapshot(catalog));
        var unknown = text.Replace(
            "\"schema_version\":\"pricing.catalog-snapshot.v1\",",
            $"\"schema_version\":\"pricing.catalog-snapshot.v1\",\"{sensitiveMarker}\":true,",
            StringComparison.Ordinal);
        var noncanonical = $"{text}{Environment.NewLine}";

        var unknownError = Assert.Throws<PricingRegistryValidationException>(() =>
            PricingCatalogSnapshotConsumer.Deserialize(
                System.Text.Encoding.UTF8.GetBytes(unknown)));
        Assert.Null(unknownError.InnerException);
        Assert.DoesNotContain(
            sensitiveMarker,
            unknownError.ToString(),
            StringComparison.Ordinal);
        Assert.Throws<PricingRegistryValidationException>(() =>
            PricingCatalogSnapshotConsumer.Deserialize(
                System.Text.Encoding.UTF8.GetBytes(noncanonical)));
    }

    [Fact]
    public void Catalog_snapshot_consumer_enforces_shape_depth_size_and_document_bounds()
    {
        var bytes = PricingCanonicalJson.SerializeCatalogSnapshot(
            PricingCatalog.Create(PricingTestData.SyntheticRegistry()));
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        using var document = JsonDocument.Parse(bytes);
        var registryText = document.RootElement
            .GetProperty("documents")[0]
            .GetRawText();
        var duplicate = text.Replace(
            "\"schema_version\":\"pricing.catalog-snapshot.v1\",",
            "\"schema_version\":\"pricing.catalog-snapshot.v1\","
            + "\"schema_version\":\"pricing.catalog-snapshot.v1\",",
            StringComparison.Ordinal);
        var missingNode = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
        missingNode.Remove("documents");
        var nestedInvalid = text.Replace(
            "\"source_kind\":\"bundled\"",
            "\"source_kind\":\"invalid\"",
            StringComparison.Ordinal);
        var tooManyDocuments =
            $"{{\"schema_version\":\"pricing.catalog-snapshot.v1\",\"documents\":["
            + string.Join(",", Enumerable.Repeat(registryText, 65))
            + "]}";
        var invalidInputs = new[]
        {
            Array.Empty<byte>(),
            System.Text.Encoding.UTF8.GetBytes("{"),
            System.Text.Encoding.UTF8.GetBytes(duplicate),
            System.Text.Encoding.UTF8.GetBytes(missingNode.ToJsonString()),
            System.Text.Encoding.UTF8.GetBytes(nestedInvalid),
            System.Text.Encoding.UTF8.GetBytes(tooManyDocuments),
            System.Text.Encoding.UTF8.GetBytes(
                $"{new string('[', 33)}0{new string(']', 33)}"),
            new byte[(4 * 1_048_576) + 1]
        };

        foreach (var invalid in invalidInputs)
        {
            var error = Assert.Throws<PricingRegistryValidationException>(() =>
                PricingCatalogSnapshotConsumer.Deserialize(invalid));
            Assert.Null(error.InnerException);
        }
    }

    [Fact]
    public void Catalog_snapshot_preserves_true_document_and_nested_entry_order()
    {
        var bundled = PricingTestData.SyntheticRegistry();
        var source = bundled.Entries[0];
        var local = bundled with
        {
            RegistryVersion = "ordered-local-v1",
            SourceKind = PricingRegistrySourceKinds.LocalOverride,
            SourceId = "ordered-local",
            Entries =
            [
                source with
                {
                    EntryId = "local-second-name",
                    CanonicalModelId = "local-model-b",
                    Aliases = []
                },
                source with
                {
                    EntryId = "local-first-name",
                    CanonicalModelId = "local-model-a",
                    Aliases = []
                }
            ]
        };
        var catalog = PricingCatalog.Create(bundled, local);

        var consumed = PricingCatalogSnapshotConsumer.Deserialize(
            PricingCanonicalJson.SerializeCatalogSnapshot(catalog));

        Assert.Equal(["synthetic", "ordered-local"],
            consumed.Documents.Select(item => item.SourceId));
        Assert.Equal(
            ["local-second-name", "local-first-name"],
            consumed.Documents[1].Entries.Select(item => item.EntryId));
    }

    [Fact]
    public void Catalog_snapshot_preserves_every_identity_bearing_collection_order()
    {
        var bundled = PricingTestData.SyntheticRegistry();
        var bundledReference = bundled.SourceReferences[0];
        var orderedBundled = bundled with
        {
            SourceReferences =
            [
                bundledReference,
                bundledReference with
                {
                    Reference = "https://example.com/pricing/synthetic-secondary",
                    Note = "Synthetic secondary reviewed reference"
                }
            ],
            Entries =
            [
                bundled.Entries[0] with
                {
                    Aliases = ["synthetic-z-alias", "synthetic-a-alias"],
                    Limitations = ["z limitation", "a limitation"]
                }
            ]
        };
        var zeta = OrderedLocalOverride(orderedBundled, "zeta-override");
        var alpha = OrderedLocalOverride(orderedBundled, "alpha-override");
        var catalog = PricingCatalog.Create(orderedBundled, zeta, alpha);
        var canonicalBytes = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);

        var consumed = PricingCatalogSnapshotConsumer.Deserialize(canonicalBytes);
        var expectedDocuments = new[] { orderedBundled, zeta, alpha };

        Assert.Equal(
            ["synthetic", "zeta-override", "alpha-override"],
            consumed.Documents.Select(document => document.SourceId));
        for (var documentIndex = 0;
             documentIndex < expectedDocuments.Length;
             documentIndex++)
        {
            var expectedDocument = expectedDocuments[documentIndex];
            var actualDocument = consumed.Documents[documentIndex];
            Assert.Equal(
                expectedDocument.SourceReferences.Select(item => item.Reference),
                actualDocument.SourceReferences.Select(item => item.Reference));
            Assert.Equal(
                expectedDocument.Entries.Select(item => item.EntryId),
                actualDocument.Entries.Select(item => item.EntryId));

            for (var entryIndex = 0;
                 entryIndex < expectedDocument.Entries.Count;
                 entryIndex++)
            {
                Assert.Equal(
                    expectedDocument.Entries[entryIndex].Aliases,
                    actualDocument.Entries[entryIndex].Aliases);
                Assert.Equal(
                    expectedDocument.Entries[entryIndex].Limitations,
                    actualDocument.Entries[entryIndex].Limitations);
            }
        }

        var reorderedSourceReferences = zeta with
        {
            SourceReferences = [.. zeta.SourceReferences.Reverse()]
        };
        var reorderedEntries = zeta with
        {
            Entries = [.. zeta.Entries.Reverse()]
        };
        var reorderedAliases = zeta with
        {
            Entries =
            [
                zeta.Entries[0] with
                {
                    Aliases = [.. zeta.Entries[0].Aliases.Reverse()]
                },
                zeta.Entries[1]
            ]
        };
        var reorderedLimitations = zeta with
        {
            Entries =
            [
                zeta.Entries[0] with
                {
                    Limitations = [.. zeta.Entries[0].Limitations.Reverse()]
                },
                zeta.Entries[1]
            ]
        };
        var reorderedCatalogs = new[]
        {
            PricingCatalog.Create(orderedBundled, alpha, zeta),
            PricingCatalog.Create(orderedBundled, reorderedSourceReferences, alpha),
            PricingCatalog.Create(orderedBundled, reorderedEntries, alpha),
            PricingCatalog.Create(orderedBundled, reorderedAliases, alpha),
            PricingCatalog.Create(orderedBundled, reorderedLimitations, alpha)
        };

        foreach (var reorderedCatalog in reorderedCatalogs)
        {
            Assert.False(canonicalBytes.SequenceEqual(
                PricingCanonicalJson.SerializeCatalogSnapshot(reorderedCatalog)));
            Assert.NotEqual(catalog.CatalogSha256, reorderedCatalog.CatalogSha256);
        }
    }

    [Fact]
    public void Snapshot_and_estimate_consumers_return_deeply_immutable_collections()
    {
        var catalog = PricingCatalogSnapshotConsumer.Deserialize(
            PricingCanonicalJson.SerializeCatalogSnapshot(
                PricingCatalog.Create(PricingTestData.SyntheticRegistry())));
        var estimate = PricingEstimateConsumer.Deserialize(
            PricingCanonicalJson.Serialize(
                new PricingEstimationEngine(catalog).Estimate(PricingTestData.Request())),
            catalog);

        Assert.Throws<NotSupportedException>(() =>
            ((IList<PricingRegistryDocument>)catalog.Documents).Add(catalog.Documents[0]));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<PricingCatalogEntry>)catalog.Entries).Add(catalog.Entries[0]));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<PricingRegistrySourceReference>)catalog.Documents[0].SourceReferences)
                .Add(catalog.Documents[0].SourceReferences[0]));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<PricingRegistryEntry>)catalog.Documents[0].Entries)
                .Add(catalog.Documents[0].Entries[0]));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)catalog.Documents[0].Entries[0].Aliases).Add("mutated"));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)catalog.Documents[0].Entries[0].Limitations).Add("mutated"));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<PricingEstimateComponent>)estimate.Components)
                .Add(estimate.Components[0]));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)estimate.Coverage.RequiredCategories).Add("mutated"));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)estimate.Coverage.EstimatedCategories).Add("mutated"));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)estimate.Coverage.MissingCategories).Add("mutated"));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)estimate.Source.CompletenessReasons).Add("mutated"));
    }

    [Fact]
    public void Catalog_snapshot_digest_preserves_document_and_entry_order()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var original = PricingCatalog.Create(registry);
        var reordered = PricingCatalog.Create(registry with
        {
            Entries = registry.Entries.Reverse().ToArray()
        });

        Assert.NotEqual(original.CatalogSha256, reordered.CatalogSha256);
        Assert.NotEqual(
            PricingCanonicalJson.SerializeCatalogSnapshot(original),
            PricingCanonicalJson.SerializeCatalogSnapshot(reordered));
    }

    [Fact]
    public void Strict_consumer_rejects_a_wrong_catalog_digest_with_a_valid_estimate_identity()
    {
        var catalog = PricingCatalog.Create(PricingTestData.SyntheticRegistry());
        var original = new PricingEstimationEngine(catalog).Estimate(
            PricingTestData.Request());
        var withoutIdentity = original with
        {
            CatalogSha256 = new string('0', 64),
            EstimateId = string.Empty
        };
        var digest = SHA256.HashData(PricingCanonicalJson.Serialize(withoutIdentity));
        var forged = withoutIdentity with
        {
            EstimateId = $"pricing-estimate-{Convert.ToHexStringLower(digest)}"
        };

        Assert.Throws<PricingEstimateValidationException>(() =>
            PricingEstimateConsumer.Deserialize(
                PricingCanonicalJson.Serialize(forged),
                catalog));
    }

    [Fact]
    public void Strict_consumer_rejects_a_forged_display_field_even_with_a_recomputed_identity()
    {
        var catalog = PricingCatalog.Create(PricingTestData.SyntheticRegistry());
        var original = new PricingEstimationEngine(catalog).Estimate(
            PricingTestData.Request());
        var withoutIdentity = original with
        {
            EstimateId = string.Empty,
            Registry = original.Registry! with { SourceLabel = "Forged public label" }
        };
        var digest = SHA256.HashData(PricingCanonicalJson.Serialize(withoutIdentity));
        var forged = withoutIdentity with
        {
            EstimateId = $"pricing-estimate-{Convert.ToHexStringLower(digest)}"
        };

        Assert.Throws<PricingEstimateValidationException>(() =>
            PricingEstimateConsumer.Deserialize(
                PricingCanonicalJson.Serialize(forged),
                catalog));
    }

    [Fact]
    public void Selected_entry_with_every_required_category_missing_round_trips()
    {
        var catalog = PricingCatalog.Create(PricingTestData.SyntheticRegistry());
        var result = new PricingEstimationEngine(catalog).Estimate(
            PricingTestData.Request(usage: PricingUsage.Empty));

        Assert.Equal(PricingEstimateStatuses.NotEstimable, result.Status);
        Assert.Null(result.Amount);
        Assert.NotNull(result.Registry);
        Assert.NotEmpty(result.Components);
        Assert.All(result.Components, component => Assert.Null(component.Amount));

        var canonical = PricingCanonicalJson.Serialize(result);
        var consumed = PricingEstimateConsumer.Deserialize(canonical, catalog);

        Assert.Equal(canonical, PricingCanonicalJson.Serialize(consumed));
        Assert.Equal(result.EstimateId, consumed.EstimateId);
    }

    [Fact]
    public void Partial_source_with_no_specific_reason_round_trips()
    {
        var catalog = PricingCatalog.Create(PricingTestData.SyntheticRegistry());
        var request = PricingTestData.Request(completeness: PricingSourceCompleteness.Partial);
        request = request with
        {
            Source = request.Source with { CompletenessReasons = [] }
        };
        var result = new PricingEstimationEngine(catalog).Estimate(request);

        var consumed = PricingEstimateConsumer.Deserialize(
            PricingCanonicalJson.Serialize(result),
            catalog);

        Assert.Equal(PricingEstimateStatuses.Partial, consumed.Status);
        Assert.Empty(consumed.Source.CompletenessReasons);
    }

    [Fact]
    public void Minimum_rate_component_is_exact_and_round_trips()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var source = registry.Entries[0];
        var entry = source with
        {
            Rates = source.Rates with
            {
                InputPerMillionTokens = 0.000001m,
                OutputPerMillionTokens = null
            }
        };
        var catalog = PricingCatalog.Create(registry with { Entries = [entry] });
        var result = new PricingEstimationEngine(catalog).Estimate(
            PricingTestData.Request(usage:
                PricingUsage.Empty with { InputTokens = PricingTestData.Quantity(1) }));

        Assert.Equal(0.000000000001m, result.Amount);
        Assert.Equal(
            result.EstimateId,
            PricingEstimateConsumer.Deserialize(
                PricingCanonicalJson.Serialize(result),
                catalog).EstimateId);
    }

    [Fact]
    public void Fractional_credit_scale_ignores_insignificant_trailing_zeroes()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var source = registry.Entries[1];
        var entry = source with
        {
            EntryId = "credit-scale",
            CanonicalModelId = "credit-scale",
            Rates = source.Rates with { RequestCreditMultiplier = null }
        };
        var catalog = PricingCatalog.Create(registry with { Entries = [entry] });
        var engine = new PricingEstimationEngine(catalog);
        PricingEstimateRequest Request(decimal credits) => PricingTestData.Request(
            model: "credit-scale",
            billingMode: PricingBillingModes.GitHubLegacyRequests,
            pricingRoute: PricingRoutes.LegacyRequest,
            usage: PricingUsage.Empty with
            {
                CreditCount = PricingTestData.Quantity(credits)
            });

        var scaled = engine.Estimate(Request(0.1000000m));
        var normalized = engine.Estimate(Request(0.10m));

        Assert.Equal(scaled.EstimateId, normalized.EstimateId);
        Assert.Equal(
            PricingCanonicalJson.Serialize(scaled),
            PricingCanonicalJson.Serialize(normalized));
    }

    [Fact]
    public void Strict_consumer_wraps_null_component_as_a_fixed_validation_error()
    {
        var catalog = PricingCatalog.Create(PricingTestData.SyntheticRegistry());
        var original = new PricingEstimationEngine(catalog).Estimate(
            PricingTestData.Request());
        var withoutIdentity = original with
        {
            EstimateId = string.Empty,
            Components = [null!]
        };
        var digest = SHA256.HashData(PricingCanonicalJson.Serialize(withoutIdentity));
        var malformed = withoutIdentity with
        {
            EstimateId = $"pricing-estimate-{Convert.ToHexStringLower(digest)}"
        };

        Assert.Throws<PricingEstimateValidationException>(() =>
            PricingEstimateConsumer.Deserialize(
                PricingCanonicalJson.Serialize(malformed),
                catalog));
    }

    [Fact]
    public void Strict_consumer_wraps_unrepresentable_component_arithmetic()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var source = registry.Entries[1];
        var entry = source with
        {
            Rates = source.Rates with
            {
                PerCredit = 999999.999999m,
                RequestCreditMultiplier = 999999.999999m
            }
        };
        var catalog = PricingCatalog.Create(registry with { Entries = [entry] });
        var baseline = new PricingEstimationEngine(catalog).Estimate(
            PricingTestData.Request(
                model: source.CanonicalModelId,
                billingMode: PricingBillingModes.GitHubLegacyRequests,
                pricingRoute: PricingRoutes.LegacyRequest,
                usage: PricingUsage.Empty with
                {
                    RequestCount = PricingTestData.Quantity(0)
                }));
        var component = Assert.Single(baseline.Components);
        var withoutIdentity = baseline with
        {
            EstimateId = string.Empty,
            Amount = 0m,
            Components =
            [
                component with
                {
                    Quantity = 999999999998000000.000001m,
                    Amount = 0m
                }
            ],
            Usage = baseline.Usage with
            {
                RequestCount = PricingTestData.Quantity(999999999999m)
            }
        };
        var digest = SHA256.HashData(PricingCanonicalJson.Serialize(withoutIdentity));
        var malformed = withoutIdentity with
        {
            EstimateId = $"pricing-estimate-{Convert.ToHexStringLower(digest)}"
        };

        var error = Assert.Throws<PricingEstimateValidationException>(() =>
            PricingEstimateConsumer.Deserialize(
                PricingCanonicalJson.Serialize(malformed),
                catalog));

        Assert.Equal("Pricing estimate JSON is invalid.", error.Message);
    }

    [Fact]
    public void Estimate_consumer_enforces_shape_depth_and_size_bounds()
    {
        var catalog = PricingCatalog.Create(PricingTestData.SyntheticRegistry());
        var bytes = PricingCanonicalJson.Serialize(
            new PricingEstimationEngine(catalog).Estimate(PricingTestData.Request()));
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var duplicate = text.Replace(
            "\"schema_version\":\"pricing.estimate.v1\",",
            "\"schema_version\":\"pricing.estimate.v1\","
            + "\"schema_version\":\"pricing.estimate.v1\",",
            StringComparison.Ordinal);
        var missingNode = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
        missingNode.Remove("estimate_id");
        var nestedInvalid = text.Replace(
            "\"display_mode\":\"half_even\"",
            "\"display_mode\":\"invalid\"",
            StringComparison.Ordinal);
        var invalidInputs = new[]
        {
            Array.Empty<byte>(),
            System.Text.Encoding.UTF8.GetBytes("{"),
            System.Text.Encoding.UTF8.GetBytes(duplicate),
            System.Text.Encoding.UTF8.GetBytes(missingNode.ToJsonString()),
            System.Text.Encoding.UTF8.GetBytes(nestedInvalid),
            System.Text.Encoding.UTF8.GetBytes(
                $"{new string('[', 33)}0{new string(']', 33)}"),
            new byte[1_048_577]
        };

        foreach (var invalid in invalidInputs)
        {
            var error = Assert.Throws<PricingEstimateValidationException>(() =>
                PricingEstimateConsumer.Deserialize(invalid, catalog));
            Assert.Null(error.InnerException);
        }
    }

    private static PricingEstimateRecord Reidentify(PricingEstimateRecord record)
    {
        var withoutIdentity = record with { EstimateId = string.Empty };
        var digest = SHA256.HashData(PricingCanonicalJson.Serialize(withoutIdentity));
        return withoutIdentity with
        {
            EstimateId = $"pricing-estimate-{Convert.ToHexStringLower(digest)}"
        };
    }

    private static PricingRegistryDocument OrderedLocalOverride(
        PricingRegistryDocument template,
        string sourceId)
    {
        var source = template.Entries[0];
        var primaryReference = new PricingRegistrySourceReference(
            $"https://example.com/pricing/{sourceId}/z-reference",
            template.LastReviewedDate,
            $"{sourceId} z reference");
        var secondaryReference = new PricingRegistrySourceReference(
            $"https://example.com/pricing/{sourceId}/a-reference",
            template.LastReviewedDate,
            $"{sourceId} a reference");

        return template with
        {
            RegistryVersion = $"{sourceId}-v1",
            SourceKind = PricingRegistrySourceKinds.LocalOverride,
            SourceId = sourceId,
            SourceLabel = $"{sourceId} source",
            SourceReferences = [primaryReference, secondaryReference],
            Entries =
            [
                source with
                {
                    EntryId = $"{sourceId}-z-entry",
                    Revision = 1,
                    SupersedesEntryKey = null,
                    CanonicalModelId = $"{sourceId}-z-model",
                    Aliases =
                    [
                        $"{sourceId}-z-alias",
                        $"{sourceId}-a-alias"
                    ],
                    SourceReference = primaryReference.Reference,
                    Limitations =
                    [
                        $"{sourceId} z limitation",
                        $"{sourceId} a limitation"
                    ]
                },
                source with
                {
                    EntryId = $"{sourceId}-a-entry",
                    Revision = 1,
                    SupersedesEntryKey = null,
                    CanonicalModelId = $"{sourceId}-a-model",
                    Aliases =
                    [
                        $"{sourceId}-second-z-alias",
                        $"{sourceId}-second-a-alias"
                    ],
                    SourceReference = secondaryReference.Reference,
                    Limitations =
                    [
                        $"{sourceId} second z limitation",
                        $"{sourceId} second a limitation"
                    ]
                }
            ]
        };
    }

    private static IEnumerable<string> EnumeratePropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var nested in EnumeratePropertyNames(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumeratePropertyNames(item))
                {
                    yield return nested;
                }
            }
        }
    }
}
