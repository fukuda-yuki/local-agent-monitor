using System.Diagnostics;
using System.Text.Json.Nodes;

namespace CopilotAgentObservability.Pricing.Tests;

public sealed class PricingRegistryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Loader_wraps_null_or_blank_registry_json(string? json)
    {
        var error = Assert.Throws<PricingRegistryValidationException>(
            () => PricingRegistryLoader.Deserialize(json!));

        Assert.Equal("Pricing registry JSON is empty.", error.Message);
    }

    [Fact]
    public void Loader_rejects_an_unknown_json_member()
    {
        const string sensitiveMarker = "sensitive-field-9x";
        var json = PricingTestData.SyntheticJson().Replace(
            "\"registry_version\": \"synthetic-2026.1\",",
            $"\"registry_version\": \"synthetic-2026.1\", \"{sensitiveMarker}\": true,",
            StringComparison.Ordinal);

        var error = Assert.Throws<PricingRegistryValidationException>(
            () => PricingRegistryLoader.Deserialize(json));

        Assert.Null(error.InnerException);
        Assert.DoesNotContain(sensitiveMarker, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_rejects_duplicate_json_properties()
    {
        var json = PricingTestData.SyntheticJson().Replace(
            "\"registry_version\": \"synthetic-2026.1\",",
            "\"registry_version\": \"synthetic-2026.1\", \"registry_version\": \"shadow\",",
            StringComparison.Ordinal);

        var error = Assert.Throws<PricingRegistryValidationException>(
            () => PricingRegistryLoader.Deserialize(json));

        Assert.Contains("duplicate property", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Loader_wraps_wrong_collection_shapes_and_requires_Z_timestamp_lexemes()
    {
        var wrongShapeNode = JsonNode.Parse(PricingTestData.SyntheticJson())!.AsObject();
        wrongShapeNode["source_references"] = new JsonObject();
        var wrongShape = wrongShapeNode.ToJsonString();
        var offsetTimestamp = PricingTestData.SyntheticJson().Replace(
            "2026-01-01T00:00:00.0000000Z",
            "2026-01-01T00:00:00.0000000+00:00",
            StringComparison.Ordinal);

        Assert.Throws<PricingRegistryValidationException>(
            () => PricingRegistryLoader.Deserialize(wrongShape));
        Assert.Throws<PricingRegistryValidationException>(
            () => PricingRegistryLoader.Deserialize(offsetTimestamp));
    }

    [Fact]
    public void Catalog_rejects_an_unknown_schema_version()
    {
        var registry = PricingTestData.SyntheticRegistry() with
        {
            SchemaVersion = "pricing.registry.v2"
        };

        var error = Assert.Throws<PricingRegistryValidationException>(
            () => PricingCatalog.Create(registry));

        Assert.Contains("schema", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalog_rejects_source_references_with_embedded_credentials()
    {
        const string credentialUri = "https://user:secret@example.invalid/pricing";
        var registry = PricingTestData.SyntheticRegistry();
        var sourceReference = registry.SourceReferences[0] with
        {
            Reference = credentialUri
        };
        var entry = registry.Entries[0] with
        {
            SourceReference = credentialUri
        };

        var error = Assert.Throws<PricingRegistryValidationException>(
            () => PricingCatalog.Create(registry with
            {
                SourceReferences = [sourceReference],
                Entries = [entry]
            }));

        Assert.Contains("public-style HTTPS", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_wraps_null_required_source_reference_strings()
    {
        var node = JsonNode.Parse(PricingTestData.SyntheticJson())!.AsObject();
        node["source_references"]![0]!["reference"] = null;
        node["entries"]![0]!["source_reference"] = null;
        var registry = PricingRegistryLoader.Deserialize(node.ToJsonString());

        var error = Assert.Throws<PricingRegistryValidationException>(
            () => PricingCatalog.Create(registry));

        Assert.Equal(
            "Pricing registry source references must be public-style HTTPS URIs without credentials, query, or fragment.",
            error.Message);
    }

    [Theory]
    [InlineData("https://localhost/private")]
    [InlineData("https://localhost./private")]
    [InlineData("https://foo.localhost/private")]
    [InlineData("https://home.arpa/private")]
    [InlineData("https://HOME.ARPA/private")]
    [InlineData("https://foo.home.arpa/private")]
    [InlineData("https://192.168.1.5/private")]
    [InlineData("https://127.0.0.1./private")]
    [InlineData("https://example.com./private")]
    [InlineData("https://example.com/private?token=synthetic")]
    [InlineData("https://example.com/private#fragment")]
    [InlineData("https://example.com/pricing/sk-proj-secret")]
    [InlineData("https://example.com/pricing/ghr_synthetic")]
    [InlineData("https://example.com/pricing/AIzaSynthetic")]
    [InlineData("https://example.com/pricing/xoxa-synthetic")]
    [InlineData("https://example.com/pricing/user@example.com")]
    [InlineData("https://example.com/pricing/Bearer%20synthetic")]
    [InlineData("https://example.com/pricing/Basic%20c3ludGhldGlj")]
    [InlineData("https://example.com/pricing/Authorization%3Asynthetic")]
    [InlineData("https://example.com/pricing/password%3Dsynthetic")]
    [InlineData("https://example.com/pricing/%2E%2E/private")]
    [InlineData("https://example.com/pricing/../private")]
    [InlineData("https://example.com/pricing\\..\\private")]
    [InlineData("https://example.com/pricing%5C..%5Cprivate")]
    [InlineData("https://example.com/pricing/%2E%2E%5Cprivate")]
    [InlineData("https://sk-proj-synthetic-secret.example.com/pricing")]
    [InlineData(" https://example.com/pricing")]
    [InlineData("https://example.com/pricing ")]
    [InlineData("HTTPS://example.com/pricing")]
    [InlineData("https://@example.com/pricing")]
    [InlineData("https://example.com/price\tlist")]
    [InlineData("https://example.com/price\u2028list")]
    [InlineData("https://example.com/pricing/%")]
    [InlineData("https://example.com/pricing/%ZZ")]
    [InlineData("https://example.com/pricing/%E0%A4%A")]
    public void Catalog_accepts_only_public_style_https_source_references(string unsafeReference)
    {
        var registry = PricingTestData.SyntheticRegistry();
        var changedReference = registry.SourceReferences[0] with { Reference = unsafeReference };
        var changedEntry = registry.Entries[0] with { SourceReference = unsafeReference };

        Assert.Throws<PricingRegistryValidationException>(() =>
            PricingCatalog.Create(registry with
            {
                SourceReferences = [changedReference],
                Entries = [changedEntry]
            }));
    }

    [Fact]
    public void Catalog_rejects_embedded_credential_markers_in_repository_safe_labels()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var embeddedInDocument = registry with
        {
            SourceLabel = "Reviewed sk-proj-secret source"
        };
        var embeddedInEntry = registry with
        {
            Entries =
            [
                registry.Entries[0] with
                {
                    CanonicalModelId = "model-sk-proj-secret"
                }
            ]
        };

        Assert.Throws<PricingRegistryValidationException>(
            () => PricingCatalog.Create(embeddedInDocument));
        Assert.Throws<PricingRegistryValidationException>(
            () => PricingCatalog.Create(embeddedInEntry));
    }

    [Theory]
    [InlineData("Reviewed gho_synthetic source")]
    [InlineData("Reviewed AIzaSynthetic source")]
    [InlineData("Reviewed xoxb-synthetic source")]
    [InlineData("Reviewed Basic c3ludGhldGlj source")]
    [InlineData("Reviewed Authorization: synthetic source")]
    [InlineData("Reviewed password=synthetic source")]
    [InlineData("Reviewed token=synthetic source")]
    [InlineData("Reviewed client_secret=synthetic source")]
    [InlineData("-----BEGIN SYNTHETIC PRIVATE KEY-----")]
    public void Catalog_rejects_repository_scanner_credential_shapes(string unsafeLabel)
    {
        var registry = PricingTestData.SyntheticRegistry() with
        {
            SourceLabel = unsafeLabel
        };

        Assert.Throws<PricingRegistryValidationException>(
            () => PricingCatalog.Create(registry));
    }

    [Fact]
    public void Catalog_rejects_high_confidence_token_shapes_at_any_position()
    {
        var unsafeLabels = new[]
        {
            $"x{"sk-"}{new string('A', 40)}",
            $"x{"ghu_"}{new string('B', 40)}",
            $"x{"github_pat_"}{new string('C', 40)}",
            $"x{"glpat-"}{new string('D', 32)}",
            $"x{"AKIA"}{new string('E', 16)}",
            $"x{"AIza"}{new string('F', 35)}",
            $"x{"xoxb-"}{new string('G', 32)}"
        };

        foreach (var unsafeLabel in unsafeLabels)
        {
            var registry = PricingTestData.SyntheticRegistry() with
            {
                SourceLabel = unsafeLabel
            };
            var error = Assert.Throws<PricingRegistryValidationException>(
                () => PricingCatalog.Create(registry));
            Assert.DoesNotContain(unsafeLabel, error.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Catalog_rejects_unsafe_uri_code_points_without_echoing_them()
    {
        var unsafeReferences = new[]
        {
            $"https://example.com/price{'\0'}list",
            $"https://example.com/price{'\u007f'}list",
            $"https://example.com/price{'\n'}list",
            $"https://example.com/price{'\r'}list",
            "https://example.com/price%00list",
            "https://example.com/price%E2%80%A8list"
        };

        foreach (var unsafeReference in unsafeReferences)
        {
            var registry = PricingTestData.SyntheticRegistry();
            var sourceReference = registry.SourceReferences[0] with
            {
                Reference = unsafeReference
            };
            var entry = registry.Entries[0] with
            {
                SourceReference = unsafeReference
            };

            var error = Assert.Throws<PricingRegistryValidationException>(() =>
                PricingCatalog.Create(registry with
                {
                    SourceReferences = [sourceReference],
                    Entries = [entry]
                }));
            Assert.DoesNotContain(
                unsafeReference,
                error.ToString(),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Catalog_rejects_unpaired_surrogates_but_round_trips_valid_pairs()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var invalid = registry with
        {
            Entries =
            [
                registry.Entries[0] with { CanonicalModelId = "\uD800model" }
            ]
        };
        Assert.Throws<PricingRegistryValidationException>(
            () => PricingCatalog.Create(invalid));

        const string validModel = "model-\U0001F680";
        var valid = registry with
        {
            Entries =
            [
                registry.Entries[0] with { CanonicalModelId = validModel }
            ]
        };
        var catalog = PricingCatalog.Create(valid);
        var reloaded = PricingCatalogSnapshotConsumer.Deserialize(
            PricingCanonicalJson.SerializeCatalogSnapshot(catalog));

        Assert.NotNull(reloaded.TrySelect(
            PricingProviders.GitHubCopilot,
            validModel,
            PricingBillingModes.GitHubAiCredits,
            PricingRoutes.CreditConsumingInteraction,
            PricingTestData.SessionTime));
    }

    [Fact]
    public void Catalog_rejects_traversal_labels_and_never_echoes_an_invalid_predecessor()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var traversal = registry with
        {
            Entries = [registry.Entries[0] with { CanonicalModelId = ".." }]
        };
        const string sensitivePredecessor = "sk-sensitive-predecessor";
        var invalidPredecessor = registry with
        {
            Entries =
            [
                registry.Entries[0] with
                {
                    SupersedesEntryKey = sensitivePredecessor
                }
            ]
        };

        Assert.Throws<PricingRegistryValidationException>(
            () => PricingCatalog.Create(traversal));
        var error = Assert.Throws<PricingRegistryValidationException>(
            () => PricingCatalog.Create(invalidPredecessor));
        Assert.DoesNotContain(sensitivePredecessor, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"last_reviewed_date\": \"2026-01-01\",")]
    [InlineData("\"currency_minor_units\": 2,")]
    [InlineData("\"effective_from_utc\": \"2026-01-01T00:00:00.0000000Z\",")]
    [InlineData("\"included_zero_incremental_cost\": false,")]
    public void Loader_rejects_omitted_required_value_type_members(string member)
    {
        var json = PricingTestData.SyntheticJson().Replace(member, string.Empty, StringComparison.Ordinal);

        Assert.Throws<PricingRegistryValidationException>(
            () => PricingRegistryLoader.Deserialize(json));
    }

    [Fact]
    public void Bundled_registry_is_pinned_reviewed_and_deliberately_narrow()
    {
        var registry = BundledPricingRegistry.Load();

        Assert.Equal(PricingContractVersions.Registry, registry.SchemaVersion);
        Assert.Equal("bundled-2026-07-24.1", registry.RegistryVersion);
        Assert.Equal(PricingRegistrySourceKinds.Bundled, registry.SourceKind);
        Assert.Equal(new DateOnly(2026, 7, 24), registry.LastReviewedDate);
        Assert.Equal(3, registry.Entries.Count);
        Assert.Contains(registry.Entries, entry =>
            entry.Provider == PricingProviders.GitHubCopilot
            && entry.CanonicalModelId == "GPT-5 mini"
            && entry.BillingMode == PricingBillingModes.GitHubAiCredits
            && entry.PricingRoute == PricingRoutes.CreditConsumingInteraction
            && entry.Rates.InputPerMillionTokens == 0.25m
            && entry.Rates.CacheReadPerMillionTokens == 0.025m
            && entry.Rates.OutputPerMillionTokens == 2m);
        var claude = Assert.Single(registry.Entries, entry =>
            entry.Provider == PricingProviders.ClaudeCode
            && entry.CanonicalModelId == "claude-sonnet-4-6"
            && entry.PricingRoute == PricingRoutes.StandardGlobal
            && entry.EffectiveFromUtc == new DateTimeOffset(2026, 5, 27, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(3m, claude.Rates.InputPerMillionTokens);
        Assert.Equal(15m, claude.Rates.OutputPerMillionTokens);
        Assert.Equal(0.30m, claude.Rates.CacheReadPerMillionTokens);
        Assert.Equal(3.75m, claude.Rates.CacheWrite5mPerMillionTokens);
        Assert.Equal(6m, claude.Rates.CacheWrite1hPerMillionTokens);
        Assert.Null(claude.Rates.ReasoningPerMillionTokens);
        Assert.All(registry.Entries, entry =>
        {
            Assert.Empty(entry.Aliases);
            Assert.StartsWith("https://", entry.SourceReference, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Exact_declared_alias_is_selected_without_case_or_fuzzy_fallback()
    {
        var catalog = PricingCatalog.Create(PricingTestData.SyntheticRegistry());

        var exact = catalog.Select(
            PricingProviders.GitHubCopilot,
            "synthetic-exact",
            PricingBillingModes.GitHubAiCredits,
            PricingRoutes.CreditConsumingInteraction,
            PricingTestData.SessionTime);

        Assert.Equal("synthetic-model", exact.Entry.CanonicalModelId);
        Assert.Null(catalog.TrySelect(
            PricingProviders.GitHubCopilot,
            "Synthetic-Exact",
            PricingBillingModes.GitHubAiCredits,
            PricingRoutes.CreditConsumingInteraction,
            PricingTestData.SessionTime));
        Assert.Null(catalog.TrySelect(
            PricingProviders.GitHubCopilot,
            PricingRoutes.CreditConsumingInteraction,
            PricingBillingModes.GitHubAiCredits,
            PricingRoutes.CreditConsumingInteraction,
            PricingTestData.SessionTime));
    }

    [Fact]
    public void Overlapping_local_override_requires_explicit_supersession()
    {
        var bundled = PricingTestData.SyntheticRegistry();
        var entry = bundled.Entries[0] with
        {
            EntryId = "synthetic-token-local",
            Revision = 1,
            SupersedesEntryKey = null,
            Rates = bundled.Entries[0].Rates with { InputPerMillionTokens = 0.20m }
        };
        var local = bundled with
        {
            RegistryVersion = "local-1",
            SourceKind = PricingRegistrySourceKinds.LocalOverride,
            SourceId = "local-test",
            SourceLabel = "Explicit synthetic local override",
            Entries = [entry]
        };

        var error = Assert.Throws<PricingRegistryValidationException>(
            () => PricingCatalog.Create(bundled, local));

        Assert.Contains("supersession", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explicit_local_supersession_preserves_both_records_and_selects_the_new_entry()
    {
        var bundled = PricingTestData.SyntheticRegistry();
        var original = bundled.Entries[0];
        var localEntry = original with
        {
            EntryId = "synthetic-token-local",
            Revision = 1,
            SupersedesEntryKey = $"{bundled.SourceId}:{original.EntryId}@{original.Revision}",
            Rates = original.Rates with { InputPerMillionTokens = 0.20m }
        };
        var local = bundled with
        {
            RegistryVersion = "local-1",
            SourceKind = PricingRegistrySourceKinds.LocalOverride,
            SourceId = "local-test",
            SourceLabel = "Explicit synthetic local override",
            Entries = [localEntry]
        };

        var catalog = PricingCatalog.Create(bundled, local);
        var selected = catalog.Select(
            PricingProviders.GitHubCopilot,
            "synthetic-model",
            PricingBillingModes.GitHubAiCredits,
            PricingRoutes.CreditConsumingInteraction,
            PricingTestData.SessionTime);

        Assert.Equal(3, catalog.Entries.Count);
        Assert.Equal("local-test:synthetic-token-local@1", selected.EntryKey);
        Assert.Equal("synthetic:synthetic-token@1", selected.Entry.SupersedesEntryKey);
        Assert.Equal(0.20m, selected.Entry.Rates.InputPerMillionTokens);
        Assert.Equal(PricingRegistrySourceKinds.LocalOverride, selected.Document.SourceKind);
    }

    [Fact]
    public void Local_override_can_append_a_nonoverlapping_exact_tuple()
    {
        var bundled = PricingTestData.SyntheticRegistry();
        var appended = bundled.Entries[0] with
        {
            EntryId = "synthetic-appended-model",
            CanonicalModelId = "synthetic-appended-model",
            Aliases = [],
            SupersedesEntryKey = null
        };
        var local = bundled with
        {
            RegistryVersion = "local-append-1",
            SourceKind = PricingRegistrySourceKinds.LocalOverride,
            SourceId = "local-append",
            SourceLabel = "Synthetic append-only local entries",
            Entries = [appended]
        };

        var catalog = PricingCatalog.Create(bundled, local);
        var selected = catalog.Select(
            PricingProviders.GitHubCopilot,
            "synthetic-appended-model",
            PricingBillingModes.GitHubAiCredits,
            PricingRoutes.CreditConsumingInteraction,
            PricingTestData.SessionTime);

        Assert.Equal(3, catalog.Entries.Count);
        Assert.Equal("local-append:synthetic-appended-model@1", selected.EntryKey);
        Assert.Null(selected.Entry.SupersedesEntryKey);
    }

    [Fact]
    public void Provider_mode_route_compatibility_is_exact()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var invalid = registry.Entries[0] with
        {
            PricingRoute = PricingRoutes.StandardGlobal
        };

        Assert.Throws<PricingRegistryValidationException>(
            () => PricingCatalog.Create(registry with { Entries = [invalid] }));
    }

    [Fact]
    public void Supersession_must_preserve_the_complete_alias_set()
    {
        var bundled = PricingTestData.SyntheticRegistry();
        var original = bundled.Entries[0];
        var replacement = original with
        {
            EntryId = "synthetic-alias-dropping-replacement",
            SupersedesEntryKey = $"{bundled.SourceId}:{original.EntryId}@{original.Revision}",
            Aliases = []
        };
        var local = bundled with
        {
            RegistryVersion = "local-alias-1",
            SourceKind = PricingRegistrySourceKinds.LocalOverride,
            SourceId = "local-alias",
            Entries = [replacement]
        };

        Assert.Throws<PricingRegistryValidationException>(
            () => PricingCatalog.Create(bundled, local));
    }

    [Fact]
    public void Supersession_alias_set_can_preserve_semantics_in_a_different_order()
    {
        var bundled = PricingTestData.SyntheticRegistry();
        var original = bundled.Entries[0] with
        {
            Aliases = ["first-alias", "second-alias"]
        };
        bundled = bundled with { Entries = [original, bundled.Entries[1]] };
        var replacement = original with
        {
            EntryId = "synthetic-alias-reordered",
            SupersedesEntryKey = $"{bundled.SourceId}:{original.EntryId}@{original.Revision}",
            Aliases = ["second-alias", "first-alias"]
        };
        var local = bundled with
        {
            RegistryVersion = "local-alias-reordered-1",
            SourceKind = PricingRegistrySourceKinds.LocalOverride,
            SourceId = "local-alias-reordered",
            Entries = [replacement]
        };

        var catalog = PricingCatalog.Create(bundled, local);

        Assert.Equal(
            "local-alias-reordered:synthetic-alias-reordered@1",
            catalog.Select(
                PricingProviders.GitHubCopilot,
                "first-alias",
                PricingBillingModes.GitHubAiCredits,
                PricingRoutes.CreditConsumingInteraction,
                PricingTestData.SessionTime).EntryKey);
    }

    [Fact]
    public void Supersession_can_only_point_from_a_local_override_to_an_earlier_entry()
    {
        var bundled = PricingTestData.SyntheticRegistry();
        var original = bundled.Entries[0];
        var target = original with
        {
            EntryId = "later-target",
            SupersedesEntryKey = null
        };
        var local = bundled with
        {
            RegistryVersion = "later-target-v1",
            SourceKind = PricingRegistrySourceKinds.LocalOverride,
            SourceId = "later-target-source",
            Entries = [target]
        };
        var invalidBundled = bundled with
        {
            Entries =
            [
                original with
                {
                    SupersedesEntryKey = "later-target-source:later-target@1"
                },
                bundled.Entries[1]
            ]
        };

        Assert.Throws<PricingRegistryValidationException>(
            () => PricingCatalog.Create(invalidBundled, local));
    }

    [Fact]
    public void Same_entry_supersession_must_increase_the_revision()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var original = registry.Entries[0] with { Revision = 2 };
        var invalidReplacement = original with
        {
            Revision = 1,
            SupersedesEntryKey = $"{registry.SourceId}:{original.EntryId}@{original.Revision}"
        };

        Assert.Throws<PricingRegistryValidationException>(() =>
            PricingCatalog.Create(registry with
            {
                Entries = [original, invalidReplacement, registry.Entries[1]]
            }));
    }

    [Fact]
    public void Missing_supersession_target_always_uses_the_fixed_registry_error()
    {
        var bundled = PricingTestData.SyntheticRegistry();
        var first = bundled.Entries[0];
        var intermediate = first with
        {
            EntryId = "intermediate",
            SupersedesEntryKey = "missing:entry@1"
        };
        var newest = first with
        {
            EntryId = "newest",
            SupersedesEntryKey = "local-chain:intermediate@1"
        };
        var local = bundled with
        {
            RegistryVersion = "local-chain-v1",
            SourceKind = PricingRegistrySourceKinds.LocalOverride,
            SourceId = "local-chain",
            Entries = [newest, intermediate]
        };

        Assert.Throws<PricingRegistryValidationException>(
            () => PricingCatalog.Create(bundled, local));
    }

    [Fact]
    public void Catalog_defensively_copies_registry_collections()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var aliases = registry.Entries[0].Aliases.ToList();
        var mutable = registry with
        {
            Entries =
            [
                registry.Entries[0] with { Aliases = aliases },
                registry.Entries[1]
            ]
        };

        var catalog = PricingCatalog.Create(mutable);
        aliases[0] = "mutated-alias";

        Assert.NotNull(catalog.TrySelect(
            PricingProviders.GitHubCopilot,
            "synthetic-exact",
            PricingBillingModes.GitHubAiCredits,
            PricingRoutes.CreditConsumingInteraction,
            PricingTestData.SessionTime));
        Assert.Null(catalog.TrySelect(
            PricingProviders.GitHubCopilot,
            "mutated-alias",
            PricingBillingModes.GitHubAiCredits,
            PricingRoutes.CreditConsumingInteraction,
            PricingTestData.SessionTime));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.0000001)]
    [InlineData(1000001)]
    public void Nonincluded_rates_must_be_positive_and_bounded(decimal invalidRate)
    {
        var registry = PricingTestData.SyntheticRegistry();
        var invalid = registry.Entries[0] with
        {
            Rates = registry.Entries[0].Rates with { InputPerMillionTokens = invalidRate }
        };

        Assert.Throws<PricingRegistryValidationException>(
            () => PricingCatalog.Create(registry with { Entries = [invalid] }));
    }

    [Fact]
    public void Registry_rejects_rates_that_exceed_the_six_decimal_v1_scale()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var invalid = registry.Entries[0] with
        {
            Rates = registry.Entries[0].Rates with
            {
                InputPerMillionTokens = 0.1234567890123456789012345678m
            }
        };

        Assert.Throws<PricingRegistryValidationException>(
            () => PricingCatalog.Create(registry with { Entries = [invalid] }));
    }

    [Fact]
    public void Registry_rate_scale_ignores_insignificant_trailing_zeroes()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var equivalent = registry.Entries[0] with
        {
            Rates = registry.Entries[0].Rates with
            {
                InputPerMillionTokens = 0.1000000m
            }
        };

        Assert.Equal(
            0.1000000m,
            PricingCatalog.Create(registry with { Entries = [equivalent] })
                .Entries[0].Entry.Rates.InputPerMillionTokens);
    }

    [Fact]
    public void Anthropic_output_inclusive_routes_reject_a_separate_reasoning_rate()
    {
        var bundled = BundledPricingRegistry.Load();
        var claude = Assert.Single(
            bundled.Entries,
            entry => entry.Provider == PricingProviders.ClaudeCode);
        var invalid = claude with
        {
            Rates = claude.Rates with { ReasoningPerMillionTokens = 1m }
        };

        Assert.Throws<PricingRegistryValidationException>(
            () => PricingCatalog.Create(bundled with { Entries = [invalid] }));
    }

    [Fact]
    public void V1_registry_supports_only_usd_with_two_minor_units()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var invalidCurrency = registry.Entries[0] with { Currency = "EUR" };
        var invalidMinorUnits = registry.Entries[0] with { CurrencyMinorUnits = 3 };

        Assert.Throws<PricingRegistryValidationException>(
            () => PricingCatalog.Create(registry with { Entries = [invalidCurrency] }));
        Assert.Throws<PricingRegistryValidationException>(
            () => PricingCatalog.Create(registry with { Entries = [invalidMinorUnits] }));
    }

    [Fact]
    public void Reviewed_dates_are_required_and_coherent()
    {
        var registry = PricingTestData.SyntheticRegistry();

        Assert.Throws<PricingRegistryValidationException>(() =>
            PricingCatalog.Create(registry with { LastReviewedDate = default }));
        Assert.Throws<PricingRegistryValidationException>(() =>
            PricingCatalog.Create(registry with
            {
                SourceReferences =
                [
                    registry.SourceReferences[0] with
                    {
                        ReviewedDate = registry.LastReviewedDate.AddDays(1)
                    }
                ]
            }));
        Assert.Throws<PricingRegistryValidationException>(() =>
            PricingCatalog.Create(registry with
            {
                Entries =
                [
                    registry.Entries[0] with
                    {
                        LastReviewedDate = registry.LastReviewedDate.AddDays(1)
                    }
                ]
            }));
    }

    [Fact]
    public void Source_ids_are_unique_across_catalog_documents()
    {
        var bundled = PricingTestData.SyntheticRegistry();
        var local = bundled with
        {
            SourceKind = PricingRegistrySourceKinds.LocalOverride,
            RegistryVersion = "local-duplicate-source-id",
            Entries =
            [
                bundled.Entries[0] with
                {
                    EntryId = "different",
                    CanonicalModelId = "different",
                    Aliases = []
                }
            ]
        };

        Assert.Throws<PricingRegistryValidationException>(
            () => PricingCatalog.Create(bundled, local));
    }

    [Fact]
    public void Catalog_producer_and_consumer_share_the_sixty_four_document_bound()
    {
        var bundled = PricingTestData.SyntheticRegistry();
        var acceptedOverrides = Enumerable.Range(1, 63)
            .Select(index => UniqueLocalOverride(bundled, index))
            .ToArray();

        var catalog = PricingCatalog.Create(bundled, acceptedOverrides);
        var bytes = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
        var consumed = PricingCatalogSnapshotConsumer.Deserialize(bytes);

        Assert.Equal(64, catalog.Documents.Count);
        Assert.Equal(64, consumed.Documents.Count);
        var rejected = UniqueLocalOverride(bundled, 64);
        Assert.Throws<PricingRegistryValidationException>(() =>
            PricingCatalog.Create(bundled, [.. acceptedOverrides, rejected]));
    }

    [Fact]
    public void Catalog_producer_and_consumer_share_the_exact_four_mibibyte_bound()
    {
        const int maximumSnapshotBytes = 4 * 1_048_576;
        var atLimit = PricingCatalog.Create(
            RegistryWithCanonicalSnapshotLength(maximumSnapshotBytes));
        var bytes = PricingCanonicalJson.SerializeCatalogSnapshot(atLimit);

        Assert.Equal(maximumSnapshotBytes, bytes.Length);
        Assert.Equal(
            atLimit.CatalogSha256,
            PricingCatalogSnapshotConsumer.Deserialize(bytes).CatalogSha256);
        Assert.Throws<PricingRegistryValidationException>(() =>
            PricingCatalog.Create(
                RegistryWithCanonicalSnapshotLength(maximumSnapshotBytes + 1)));
    }

    [Fact]
    public void Source_reference_length_bound_round_trips_snapshot_estimate_and_consumer()
    {
        var atLimitRegistry = RegistryWithSourceReferenceLength(4096);
        var catalog = PricingCatalog.Create(atLimitRegistry);
        var snapshot = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
        var reloadedCatalog = PricingCatalogSnapshotConsumer.Deserialize(snapshot);
        var estimate = new PricingEstimationEngine(catalog).Estimate(
            PricingTestData.Request());
        var estimateBytes = PricingCanonicalJson.Serialize(estimate);

        Assert.True(estimateBytes.Length < 1_048_576);
        Assert.Equal(
            estimate.EstimateId,
            PricingEstimateConsumer.Deserialize(estimateBytes, reloadedCatalog).EstimateId);
        Assert.Throws<PricingRegistryValidationException>(() =>
            PricingCatalog.Create(RegistryWithSourceReferenceLength(4097)));
    }

    [Fact]
    public void Effective_period_is_from_inclusive_and_to_exclusive()
    {
        var catalog = PricingCatalog.Create(PricingTestData.SyntheticRegistry());

        Assert.NotNull(catalog.TrySelect(
            PricingProviders.GitHubCopilot,
            "synthetic-model",
            PricingBillingModes.GitHubAiCredits,
            PricingRoutes.CreditConsumingInteraction,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Null(catalog.TrySelect(
            PricingProviders.GitHubCopilot,
            "synthetic-model",
            PricingBillingModes.GitHubAiCredits,
            PricingRoutes.CreditConsumingInteraction,
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Json_schema_accepts_pinned_fixtures_and_rejects_drift()
    {
        var testData = Path.Combine(AppContext.BaseDirectory, "TestData");
        var schema = Path.Combine(testData, "pricing-registry.schema.json");
        var bundled = Path.Combine(testData, "pricing-registry.bundled.json");
        var synthetic = Path.Combine(testData, "pricing-registry.synthetic.v1.json");
        var drifted = JsonNode.Parse(File.ReadAllText(synthetic))!.AsObject();
        drifted.Remove("last_reviewed_date");
        var driftedPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pricing.json");
        File.WriteAllText(driftedPath, drifted.ToJsonString());
        var invalidPredecessor = JsonNode.Parse(File.ReadAllText(synthetic))!.AsObject();
        invalidPredecessor["entries"]![0]!["supersedes_entry_key"] = "not-an-entry-key";
        var invalidPredecessorPath =
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pricing.json");
        File.WriteAllText(invalidPredecessorPath, invalidPredecessor.ToJsonString());
        var invalidRevision = JsonNode.Parse(File.ReadAllText(synthetic))!.AsObject();
        invalidRevision["entries"]![0]!["revision"] = 2147483648L;
        var invalidRevisionPath =
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pricing.json");
        File.WriteAllText(invalidRevisionPath, invalidRevision.ToJsonString());
        var longReference = JsonNode.Parse(File.ReadAllText(synthetic))!.AsObject();
        var tooLongReference = BuildHttpsReference(4097);
        longReference["source_references"]![0]!["reference"] = tooLongReference;
        longReference["entries"]![0]!["source_reference"] = tooLongReference;
        var longReferencePath =
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pricing.json");
        File.WriteAllText(longReferencePath, longReference.ToJsonString());
        var longEntryReference =
            JsonNode.Parse(File.ReadAllText(synthetic))!.AsObject();
        longEntryReference["entries"]![0]!["source_reference"] = tooLongReference;
        var longEntryReferencePath =
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pricing.json");
        File.WriteAllText(longEntryReferencePath, longEntryReference.ToJsonString());
        var userInfoEntryReference =
            JsonNode.Parse(File.ReadAllText(synthetic))!.AsObject();
        userInfoEntryReference["entries"]![0]!["source_reference"] =
            "https://@example.com/pricing";
        var userInfoEntryReferencePath =
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pricing.json");
        File.WriteAllText(
            userInfoEntryReferencePath,
            userInfoEntryReference.ToJsonString());
        var homeArpaEntryReference =
            JsonNode.Parse(File.ReadAllText(synthetic))!.AsObject();
        homeArpaEntryReference["entries"]![0]!["source_reference"] =
            "https://home.arpa/pricing";
        var homeArpaEntryReferencePath =
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pricing.json");
        File.WriteAllText(
            homeArpaEntryReferencePath,
            homeArpaEntryReference.ToJsonString());
        var whitespaceEntryReference =
            JsonNode.Parse(File.ReadAllText(synthetic))!.AsObject();
        whitespaceEntryReference["entries"]![0]!["source_reference"] =
            "https://example.com/price list";
        var whitespaceEntryReferencePath =
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pricing.json");
        File.WriteAllText(
            whitespaceEntryReferencePath,
            whitespaceEntryReference.ToJsonString());

        try
        {
            Assert.True(TestJsonSchema(schema, bundled));
            Assert.True(TestJsonSchema(schema, synthetic));
            Assert.False(TestJsonSchema(schema, driftedPath));
            Assert.False(TestJsonSchema(schema, invalidPredecessorPath));
            Assert.False(TestJsonSchema(schema, invalidRevisionPath));
            Assert.False(TestJsonSchema(schema, longReferencePath));
            Assert.False(TestJsonSchema(schema, longEntryReferencePath));
            Assert.False(TestJsonSchema(schema, userInfoEntryReferencePath));
            Assert.False(TestJsonSchema(schema, homeArpaEntryReferencePath));
            Assert.False(TestJsonSchema(schema, whitespaceEntryReferencePath));
        }
        finally
        {
            File.Delete(driftedPath);
            File.Delete(invalidPredecessorPath);
            File.Delete(invalidRevisionPath);
            File.Delete(longReferencePath);
            File.Delete(longEntryReferencePath);
            File.Delete(userInfoEntryReferencePath);
            File.Delete(homeArpaEntryReferencePath);
            File.Delete(whitespaceEntryReferencePath);
        }
    }

    private static PricingRegistryDocument UniqueLocalOverride(
        PricingRegistryDocument bundled,
        int index)
    {
        var source = bundled.Entries[0];
        return bundled with
        {
            RegistryVersion = $"local-{index}",
            SourceKind = PricingRegistrySourceKinds.LocalOverride,
            SourceId = $"local-{index}",
            SourceLabel = $"Synthetic local override {index}",
            Entries =
            [
                source with
                {
                    EntryId = $"entry-{index}",
                    CanonicalModelId = $"model-{index}",
                    Aliases = []
                }
            ]
        };
    }

    private static PricingRegistryDocument RegistryWithCanonicalSnapshotLength(
        int targetLength)
    {
        var registry = PricingTestData.SyntheticRegistry();
        var source = registry.Entries[0] with { Limitations = [] };
        var baselineRegistry = registry with { Entries = [source] };
        var baselineLength = PricingCanonicalJson.SerializeCatalogSnapshot(
            PricingCatalog.Create(baselineRegistry)).Length;
        var delta = targetLength - baselineLength;
        var count = checked((delta + 515) / 515);
        var totalTextLength = checked(delta - (3 * count) + 1);
        if (count < 1 || totalTextLength < count || totalTextLength > 512 * count)
        {
            throw new InvalidOperationException("Target snapshot length is not constructible.");
        }

        var limitations = new string[count];
        var remaining = totalTextLength;
        for (var index = 0; index < count; index++)
        {
            var remainingSlots = count - index - 1;
            var length = Math.Min(512, remaining - remainingSlots);
            limitations[index] = new string('x', length);
            remaining -= length;
        }

        return registry with
        {
            Entries = [source with { Limitations = limitations }]
        };
    }

    private static PricingRegistryDocument RegistryWithSourceReferenceLength(int length)
    {
        var registry = PricingTestData.SyntheticRegistry();
        var reference = BuildHttpsReference(length);
        return registry with
        {
            SourceReferences =
            [
                registry.SourceReferences[0] with { Reference = reference }
            ],
            Entries =
            [
                registry.Entries[0] with { SourceReference = reference }
            ]
        };
    }

    private static string BuildHttpsReference(int length)
    {
        const string prefix = "https://example.com/";
        return $"{prefix}{new string('a', length - prefix.Length)}";
    }

    private static bool TestJsonSchema(string schema, string instance)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$json = Get-Content -Raw -LiteralPath $env:PRICING_SCHEMA_INSTANCE_PATH; "
            + "Test-Json -Json $json -SchemaFile $env:PRICING_SCHEMA_PATH -ErrorAction Stop");
        startInfo.Environment["PRICING_SCHEMA_PATH"] = schema;
        startInfo.Environment["PRICING_SCHEMA_INSTANCE_PATH"] = instance;
        using var process = Process.Start(startInfo)!;
        Assert.True(process.WaitForExit(30_000), "PowerShell Test-Json timed out.");
        var output = process.StandardOutput.ReadToEnd().Trim();
        return process.ExitCode == 0 && bool.Parse(output.Split(Environment.NewLine).Last());
    }
}
