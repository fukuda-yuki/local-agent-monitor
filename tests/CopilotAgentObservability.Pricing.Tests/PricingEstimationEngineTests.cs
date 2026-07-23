namespace CopilotAgentObservability.Pricing.Tests;

public sealed class PricingEstimationEngineTests
{
    [Fact]
    public void Complete_token_usage_produces_exact_unrounded_components()
    {
        var result = Engine().Estimate(PricingTestData.Request());

        Assert.Equal(PricingEstimateStatuses.Estimated, result.Status);
        Assert.Equal(0.0007m, result.Amount);
        Assert.Equal("USD", result.Currency);
        Assert.Equal(
            [0.0001m, 0.0006m],
            result.Components.Select(component => component.Amount).ToArray());
        Assert.Empty(result.Reasons);
        Assert.Equal(["input_tokens", "output_tokens"], result.Coverage.RequiredCategories);
        Assert.Equal(result.Coverage.RequiredCategories, result.Coverage.EstimatedCategories);
        Assert.Empty(result.Coverage.MissingCategories);
        Assert.Equal(PricingContractVersions.NoIntermediateRounding, result.Rounding.IntermediatePolicy);
        Assert.Equal(PricingContractVersions.DisplayRounding, result.Rounding.DisplayPolicyVersion);
    }

    [Fact]
    public void Missing_category_is_partial_and_is_never_implicit_zero()
    {
        var usage = new PricingUsage(
            PricingTestData.Quantity(1_000),
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        var result = Engine().Estimate(PricingTestData.Request(usage: usage));

        Assert.Equal(PricingEstimateStatuses.Partial, result.Status);
        Assert.Equal(0.0001m, result.Amount);
        Assert.Contains(PricingEstimateReasons.MissingTokenCategory, result.Reasons);
        Assert.Equal(["output_tokens"], result.Coverage.MissingCategories);
        var output = Assert.Single(result.Components, component => component.Category == "output_tokens");
        Assert.Null(output.Quantity);
        Assert.Null(output.Amount);
        Assert.Equal(PricingEstimateReasons.MissingTokenCategory, output.MissingReason);
    }

    [Fact]
    public void Explicit_zero_is_a_supported_zero_component()
    {
        var usage = new PricingUsage(
            PricingTestData.Quantity(0),
            PricingTestData.Quantity(0),
            null,
            null,
            null,
            null,
            null,
            null);

        var result = Engine().Estimate(PricingTestData.Request(usage: usage));

        Assert.Equal(PricingEstimateStatuses.Estimated, result.Status);
        Assert.Equal(0m, result.Amount);
        Assert.All(result.Components, component => Assert.Equal(0m, component.Amount));
        Assert.Empty(result.Coverage.MissingCategories);
    }

    [Fact]
    public void Request_to_credit_multiplier_is_calculated_as_its_own_component()
    {
        var usage = new PricingUsage(
            null,
            null,
            null,
            null,
            null,
            null,
            PricingTestData.Quantity(2),
            null);

        var result = Engine().Estimate(PricingTestData.Request(
            model: "synthetic-request-model",
            billingMode: PricingBillingModes.GitHubLegacyRequests,
            pricingRoute: PricingRoutes.LegacyRequest,
            usage: usage));

        Assert.Equal(PricingEstimateStatuses.Estimated, result.Status);
        Assert.Equal(0.10m, result.Amount);
        var component = Assert.Single(result.Components);
        Assert.Equal("request_credits", component.Category);
        Assert.Equal(10m, component.Quantity);
        Assert.Equal(0.01m, component.Rate);
    }

    [Fact]
    public void Direct_credit_quantity_preserves_fractional_usage()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var directCreditEntry = registry.Entries[1] with
        {
            EntryId = "synthetic-direct-credit",
            CanonicalModelId = "synthetic-direct-credit",
            Rates = registry.Entries[1].Rates with
            {
                PerCredit = 0.02m,
                RequestCreditMultiplier = null
            }
        };
        var engine = new PricingEstimationEngine(PricingCatalog.Create(registry with
        {
            RegistryVersion = "synthetic-direct-credit-v1",
            Entries = [directCreditEntry]
        }));
        var usage = new PricingUsage(
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            PricingTestData.Quantity(0.5m));

        var result = engine.Estimate(PricingTestData.Request(
            model: "synthetic-direct-credit",
            billingMode: PricingBillingModes.GitHubLegacyRequests,
            pricingRoute: PricingRoutes.LegacyRequest,
            usage: usage));

        Assert.Equal(PricingEstimateStatuses.Estimated, result.Status);
        Assert.Equal(0.01m, result.Amount);
        Assert.Equal(0.5m, Assert.Single(result.Components).Quantity);
    }

    [Fact]
    public void Explicit_included_plan_rule_is_the_only_zero_incremental_route()
    {
        var engine = new PricingEstimationEngine(
            PricingCatalog.Create(BundledPricingRegistry.Load()));
        var result = engine.Estimate(PricingTestData.Request(
            model: "GPT-5 mini",
            billingMode: PricingBillingModes.PlanIncluded,
            pricingRoute: PricingRoutes.CreditConsumingInteraction,
            usage: PricingUsage.Empty,
            sessionTime: new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(PricingEstimateStatuses.Estimated, result.Status);
        Assert.Equal(0m, result.Amount);
        Assert.Equal("USD", result.Currency);
        Assert.Equal(["included_zero_incremental_cost"], result.Coverage.EstimatedCategories);
    }

    [Theory]
    [InlineData(PricingProviders.GitHubCopilot, "missing-model", PricingBillingModes.GitHubAiCredits, PricingRoutes.CreditConsumingInteraction, PricingEstimateReasons.UnknownModel)]
    [InlineData(PricingProviders.GitHubCopilot, "synthetic-model", PricingBillingModes.Unknown, PricingRoutes.Unknown, PricingEstimateReasons.UnknownBillingMode)]
    [InlineData(PricingProviders.ClaudeCode, "claude-sonnet-4-6", PricingBillingModes.Subscription, PricingRoutes.SubscriptionOrContract, PricingEstimateReasons.SubscriptionAllocationUnknown)]
    [InlineData(PricingProviders.ClaudeCode, "claude-sonnet-4-6", PricingBillingModes.CustomEnterprise, PricingRoutes.SubscriptionOrContract, PricingEstimateReasons.CustomContract)]
    [InlineData(PricingProviders.CodexApp, "gpt-5.4", PricingBillingModes.Subscription, PricingRoutes.SubscriptionOrContract, PricingEstimateReasons.SubscriptionOrContractUnknown)]
    [InlineData(PricingProviders.GitHubCopilot, "synthetic-model", PricingBillingModes.GitHubLegacyRequests, PricingRoutes.LegacyRequest, PricingEstimateReasons.UnsupportedProviderRoute)]
    [InlineData(PricingProviders.GitHubCopilot, "synthetic-model", PricingBillingModes.GitHubAiCredits, PricingRoutes.StandardGlobal, PricingEstimateReasons.UnsupportedProviderRoute)]
    public void Unsupported_routes_are_not_estimable_with_fixed_reason(
        string provider,
        string model,
        string mode,
        string route,
        string expectedReason)
    {
        var result = Engine().Estimate(PricingTestData.Request(
            provider: provider,
            model: model,
            billingMode: mode,
            pricingRoute: route));

        Assert.Equal(PricingEstimateStatuses.NotEstimable, result.Status);
        Assert.Null(result.Amount);
        Assert.Null(result.Currency);
        Assert.Equal([expectedReason], result.Reasons);
        Assert.Empty(result.Components);
    }

    [Fact]
    public void Provider_mode_support_precedes_subscription_reason()
    {
        var unknownProvider = Engine().Estimate(PricingTestData.Request(
            provider: PricingProviders.Unknown,
            billingMode: PricingBillingModes.Subscription,
            pricingRoute: PricingRoutes.SubscriptionOrContract));
        var githubSubscription = Engine().Estimate(PricingTestData.Request(
            provider: PricingProviders.GitHubCopilot,
            billingMode: PricingBillingModes.Subscription,
            pricingRoute: PricingRoutes.SubscriptionOrContract));
        var claudeSubscription = Engine().Estimate(PricingTestData.Request(
            provider: PricingProviders.ClaudeCode,
            model: "claude-sonnet-4-6",
            billingMode: PricingBillingModes.Subscription,
            pricingRoute: PricingRoutes.SubscriptionOrContract));

        Assert.Equal(
            [PricingEstimateReasons.UnsupportedProviderRoute],
            unknownProvider.Reasons);
        Assert.Equal(
            [PricingEstimateReasons.UnsupportedProviderRoute],
            githubSubscription.Reasons);
        Assert.Equal(
            [PricingEstimateReasons.SubscriptionAllocationUnknown],
            claudeSubscription.Reasons);
    }

    [Fact]
    public void Known_model_outside_effective_period_does_not_fall_back_to_latest()
    {
        var result = Engine().Estimate(PricingTestData.Request(
            sessionTime: new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero)));

        Assert.Equal(PricingEstimateStatuses.NotEstimable, result.Status);
        Assert.Equal([PricingEstimateReasons.OutsideEffectiveRange], result.Reasons);
        Assert.Null(result.Amount);
    }

    [Fact]
    public void Partial_source_and_stale_registry_downgrade_otherwise_complete_estimate()
    {
        var result = Engine().Estimate(PricingTestData.Request(
            completeness: PricingSourceCompleteness.Partial,
            calculatedAt: new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(PricingEstimateStatuses.Partial, result.Status);
        Assert.Equal(
            [PricingEstimateReasons.PartialSource, PricingEstimateReasons.RegistryOutOfDate],
            result.Reasons);
        Assert.Equal(0.0007m, result.Amount);
    }

    [Fact]
    public void Nonmatching_provider_pricing_route_fails_closed()
    {
        var result = Engine().Estimate(PricingTestData.Request(
            pricingRoute: PricingRoutes.CodeCompletion));

        Assert.Equal(PricingEstimateStatuses.NotEstimable, result.Status);
        Assert.Equal([PricingEstimateReasons.UnsupportedProviderRoute], result.Reasons);
        Assert.Null(result.Amount);
    }

    [Fact]
    public void Standalone_reasoning_tokens_are_not_double_counted_as_output()
    {
        var engine = new PricingEstimationEngine(
            PricingCatalog.Create(BundledPricingRegistry.Load()));
        var usage = new PricingUsage(
            null,
            null,
            null,
            null,
            null,
            PricingTestData.Quantity(1_000),
            null,
            null);

        var result = engine.Estimate(PricingTestData.Request(
            provider: PricingProviders.ClaudeCode,
            model: "claude-sonnet-4-6",
            billingMode: PricingBillingModes.AnthropicApiTokens,
            pricingRoute: PricingRoutes.StandardGlobal,
            usage: usage,
            sessionTime: new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(PricingEstimateStatuses.NotEstimable, result.Status);
        Assert.Null(result.Amount);
        Assert.Contains(PricingEstimateReasons.MissingTokenCategory, result.Reasons);
        Assert.DoesNotContain(result.Components, component => component.Category == "reasoning_tokens");
    }

    [Fact]
    public void Inclusive_anthropic_output_ignores_reasoning_subset()
    {
        var engine = new PricingEstimationEngine(
            PricingCatalog.Create(BundledPricingRegistry.Load()));
        var usage = new PricingUsage(
            PricingTestData.Quantity(0),
            PricingTestData.Quantity(1_000),
            PricingTestData.Quantity(0),
            PricingTestData.Quantity(0),
            PricingTestData.Quantity(0),
            PricingTestData.Quantity(400),
            null,
            null);

        var result = engine.Estimate(PricingTestData.Request(
            provider: PricingProviders.ClaudeCode,
            model: "claude-sonnet-4-6",
            billingMode: PricingBillingModes.AnthropicApiTokens,
            pricingRoute: PricingRoutes.StandardGlobal,
            usage: usage,
            sessionTime: new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(PricingEstimateStatuses.Estimated, result.Status);
        Assert.Equal(0.015m, result.Amount);
        Assert.DoesNotContain(result.Components, component => component.Category == "reasoning_tokens");
    }

    [Fact]
    public void Partial_source_without_a_more_specific_reason_is_valid()
    {
        var request = PricingTestData.Request();
        var invalid = request with
        {
            Source = request.Source with
            {
                Completeness = PricingSourceCompleteness.Partial,
                CompletenessReasons = []
            }
        };

        var result = Engine().Estimate(invalid);

        Assert.Equal(PricingEstimateStatuses.Partial, result.Status);
        Assert.Equal([PricingEstimateReasons.PartialSource], result.Reasons);
        Assert.Empty(result.Source.CompletenessReasons);
    }

    [Fact]
    public void Blank_field_provenance_is_rejected_before_calculation()
    {
        var request = PricingTestData.Request();
        var invalidProvenance = PricingTestData.Provenance() with
        {
            SourceAdapter = ""
        };
        var invalid = request with
        {
            Usage = request.Usage with
            {
                InputTokens = new PricingQuantity(1_000, invalidProvenance)
            }
        };

        Assert.Throws<ArgumentException>(() => Engine().Estimate(invalid));
    }

    [Fact]
    public void Negative_and_fractional_token_quantities_are_rejected_before_calculation()
    {
        var request = PricingTestData.Request();
        var negative = request with
        {
            Usage = request.Usage with
            {
                InputTokens = PricingTestData.Quantity(-1)
            }
        };
        var fractional = request with
        {
            Usage = request.Usage with
            {
                InputTokens = PricingTestData.Quantity(0.5m)
            }
        };

        Assert.Throws<ArgumentException>(() => Engine().Estimate(negative));
        Assert.Throws<ArgumentException>(() => Engine().Estimate(fractional));
    }

    [Fact]
    public void Invalid_predecessor_id_is_rejected_before_calculation()
    {
        var invalid = PricingTestData.Request(
            supersedes: $"pricing-estimate-{new string('A', 64)}");

        Assert.Throws<ArgumentException>(() => Engine().Estimate(invalid));
    }

    [Fact]
    public void Unsafe_source_strings_and_unregistered_routes_are_rejected()
    {
        var pathLikeModel = PricingTestData.Request(model: @"C:\private\model");
        var credentialLikeSource = PricingTestData.Request();
        credentialLikeSource = credentialLikeSource with
        {
            Source = credentialLikeSource.Source with
            {
                SourceVersion = "sk-proj-synthetic-secret"
            }
        };
        var unregisteredRoute = PricingTestData.Request(pricingRoute: "synthetic");

        Assert.Throws<ArgumentException>(() => Engine().Estimate(pathLikeModel));
        Assert.Throws<ArgumentException>(() => Engine().Estimate(credentialLikeSource));
        Assert.Throws<ArgumentException>(() => Engine().Estimate(unregisteredRoute));
    }

    [Fact]
    public void Embedded_credential_markers_in_provenance_and_model_are_rejected()
    {
        var request = PricingTestData.Request(model: "model-sk-proj-secret");
        var embeddedProvenance = PricingTestData.Provenance() with
        {
            SourceEventOrTraceSpanId = "opaque:sk-proj-secret"
        };
        var provenanceRequest = PricingTestData.Request() with
        {
            Usage = PricingTestData.Request().Usage with
            {
                InputTokens = new PricingQuantity(1_000, embeddedProvenance)
            }
        };

        Assert.Throws<ArgumentException>(() => Engine().Estimate(request));
        Assert.Throws<ArgumentException>(() => Engine().Estimate(provenanceRequest));
    }

    [Theory]
    [InlineData("model-ghu_synthetic")]
    [InlineData("model-AIzaSynthetic")]
    [InlineData("model-xoxp-synthetic")]
    [InlineData("model-Basic synthetic")]
    [InlineData("model-Authorization=synthetic")]
    [InlineData("model-secret:synthetic")]
    [InlineData("model-refresh_token=synthetic")]
    public void Repository_scanner_credential_shapes_are_rejected_from_estimate_labels(
        string unsafeModel)
    {
        Assert.Throws<ArgumentException>(() =>
            Engine().Estimate(PricingTestData.Request(model: unsafeModel)));
    }

    [Fact]
    public void High_confidence_token_shapes_and_unpaired_surrogates_are_rejected_anywhere()
    {
        var actualShape = $"x{"sk-"}{new string('A', 40)}";

        Assert.Throws<ArgumentException>(() =>
            Engine().Estimate(PricingTestData.Request(model: actualShape)));
        Assert.Throws<ArgumentException>(() =>
            Engine().Estimate(PricingTestData.Request(model: "\uD800model")));
    }

    [Fact]
    public void Cache_write_and_read_categories_are_calculated_independently()
    {
        var engine = new PricingEstimationEngine(
            PricingCatalog.Create(BundledPricingRegistry.Load()));
        var usage = new PricingUsage(
            PricingTestData.Quantity(0),
            PricingTestData.Quantity(0),
            PricingTestData.Quantity(1_000_000),
            PricingTestData.Quantity(2_000_000),
            PricingTestData.Quantity(3_000_000),
            null,
            null,
            null);

        var result = engine.Estimate(PricingTestData.Request(
            provider: PricingProviders.ClaudeCode,
            model: "claude-sonnet-4-6",
            billingMode: PricingBillingModes.AnthropicApiTokens,
            pricingRoute: PricingRoutes.StandardGlobal,
            usage: usage,
            sessionTime: new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(25.80m, result.Amount);
        Assert.Collection(
            result.Components.Where(component => component.Amount != 0m),
            component =>
            {
                Assert.Equal("cache_read_tokens", component.Category);
                Assert.Equal(1_000_000m, component.Quantity);
                Assert.Equal(0.30m, component.Rate);
                Assert.Equal(0.30m, component.Amount);
            },
            component =>
            {
                Assert.Equal("cache_write_5m_tokens", component.Category);
                Assert.Equal(2_000_000m, component.Quantity);
                Assert.Equal(3.75m, component.Rate);
                Assert.Equal(7.50m, component.Amount);
            },
            component =>
            {
                Assert.Equal("cache_write_1h_tokens", component.Category);
                Assert.Equal(3_000_000m, component.Quantity);
                Assert.Equal(6m, component.Rate);
                Assert.Equal(18m, component.Amount);
            });
    }

    [Fact]
    public void Direct_request_rate_is_calculated_without_a_credit_conversion()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var source = registry.Entries[1];
        var entry = source with
        {
            EntryId = "synthetic-direct-request",
            CanonicalModelId = "synthetic-direct-request",
            Rates = source.Rates with
            {
                PerRequest = 0.25m,
                PerCredit = null,
                RequestCreditMultiplier = null
            }
        };
        var engine = new PricingEstimationEngine(
            PricingCatalog.Create(registry with { Entries = [entry] }));
        var usage = PricingUsage.Empty with { RequestCount = PricingTestData.Quantity(2) };

        var result = engine.Estimate(PricingTestData.Request(
            model: entry.CanonicalModelId,
            billingMode: PricingBillingModes.GitHubLegacyRequests,
            pricingRoute: PricingRoutes.LegacyRequest,
            usage: usage));

        Assert.Equal(0.50m, result.Amount);
        Assert.Equal("requests", Assert.Single(result.Components).Category);
    }

    [Fact]
    public void Generic_token_route_can_bill_reasoning_separately()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var source = registry.Entries[0];
        var entry = source with
        {
            EntryId = "synthetic-reasoning",
            CanonicalModelId = "synthetic-reasoning",
            Rates = source.Rates with
            {
                InputPerMillionTokens = null,
                OutputPerMillionTokens = null,
                ReasoningPerMillionTokens = 0.50m
            }
        };
        var engine = new PricingEstimationEngine(
            PricingCatalog.Create(registry with { Entries = [entry] }));
        var usage = PricingUsage.Empty with
        {
            ReasoningTokens = PricingTestData.Quantity(2_000_000)
        };

        var result = engine.Estimate(PricingTestData.Request(
            model: entry.CanonicalModelId,
            usage: usage));

        Assert.Equal(1m, result.Amount);
        Assert.Equal("reasoning_tokens", Assert.Single(result.Components).Category);
    }

    [Fact]
    public void Usage_quantities_are_bounded()
    {
        var tokenRequest = PricingTestData.Request(usage:
            PricingUsage.Empty with
            {
                InputTokens = PricingTestData.Quantity(1_000_000_000_000_000_001m)
            });
        var requestCountRequest = PricingTestData.Request(
            model: "synthetic-request-model",
            billingMode: PricingBillingModes.GitHubLegacyRequests,
            pricingRoute: PricingRoutes.LegacyRequest,
            usage: PricingUsage.Empty with
            {
                RequestCount = PricingTestData.Quantity(1_000_000_000_001m)
            });

        Assert.Throws<ArgumentException>(() => Engine().Estimate(tokenRequest));
        Assert.Throws<ArgumentException>(() => Engine().Estimate(requestCountRequest));
    }

    [Fact]
    public void Completeness_reasons_use_exact_codes_ceiling_order_and_cardinality()
    {
        var request = PricingTestData.Request(completeness: PricingSourceCompleteness.Partial);
        var unknown = request with
        {
            Source = request.Source with { CompletenessReasons = ["sk-proj-secret"] }
        };
        var tooRich = request with
        {
            Source = request.Source with
            {
                Completeness = PricingSourceCompleteness.Rich,
                CompletenessReasons = [PricingSourceCompletenessReasons.HistoricalSummaryOnly]
            }
        };
        var tooMany = request with
        {
            Source = request.Source with
            {
                CompletenessReasons = Enumerable.Repeat(
                    PricingSourceCompletenessReasons.HistoricalSummaryOnly,
                    PricingSourceCompletenessReasons.Ordered.Count + 1).ToArray()
            }
        };

        Assert.Throws<ArgumentException>(() => Engine().Estimate(unknown));
        Assert.Throws<ArgumentException>(() => Engine().Estimate(tooRich));
        Assert.Throws<ArgumentException>(() => Engine().Estimate(tooMany));
    }

    [Fact]
    public void Request_completeness_reasons_are_canonicalized_and_defensively_copied()
    {
        var reasons = new List<string>
        {
            PricingSourceCompletenessReasons.SchemaDriftDetected,
            PricingSourceCompletenessReasons.HistoricalSummaryOnly
        };
        var request = PricingTestData.Request(completeness: PricingSourceCompleteness.Partial);
        request = request with
        {
            Source = request.Source with { CompletenessReasons = reasons }
        };

        var result = Engine().Estimate(request);
        var before = PricingCanonicalJson.Serialize(result);
        reasons[0] = "mutated";

        Assert.Equal(before, PricingCanonicalJson.Serialize(result));
        Assert.Equal(
            [
                PricingSourceCompletenessReasons.HistoricalSummaryOnly,
                PricingSourceCompletenessReasons.SchemaDriftDetected
            ],
            result.Source.CompletenessReasons);
    }

    [Fact]
    public void Request_collections_are_snapshotted_once_before_validation()
    {
        var catalog = PricingCatalog.Create(PricingTestData.SyntheticRegistry());
        var request = PricingTestData.Request() with
        {
            Source = PricingTestData.Request().Source with
            {
                CompletenessReasons = new StatefulCompletenessReasons()
            }
        };

        var result = new PricingEstimationEngine(catalog).Estimate(request);
        var bytes = PricingCanonicalJson.Serialize(result);

        Assert.Empty(result.Source.CompletenessReasons);
        Assert.Equal(
            result.EstimateId,
            PricingEstimateConsumer.Deserialize(bytes, catalog).EstimateId);
    }

    [Theory]
    [InlineData("alice@example.com")]
    [InlineData(@"Users\Alice\secret.json")]
    [InlineData("..")]
    public void Unsafe_model_labels_are_rejected_without_echoing_the_value(string model)
    {
        var error = Assert.Throws<ArgumentException>(
            () => Engine().Estimate(PricingTestData.Request(model: model)));

        Assert.DoesNotContain(model, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_projection_uses_half_even_without_changing_the_estimate()
    {
        Assert.Equal(0.00m, PricingDisplayRounding.Round(0.005m, 2));
        Assert.Equal(0.02m, PricingDisplayRounding.Round(0.015m, 2));
    }

    [Fact]
    public void Exact_aggregation_rejects_loss_of_a_small_component()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var source = registry.Entries[0];
        var entry = source with
        {
            Rates = source.Rates with
            {
                InputPerMillionTokens = 1_000_000m,
                OutputPerMillionTokens = 0.000001m
            }
        };
        var engine = new PricingEstimationEngine(
            PricingCatalog.Create(registry with { Entries = [entry] }));
        var request = PricingTestData.Request(usage:
            PricingUsage.Empty with
            {
                InputTokens = PricingTestData.Quantity(1_000_000_000_000_000_000m),
                OutputTokens = PricingTestData.Quantity(1)
            });

        Assert.Throws<ArgumentException>(() => engine.Estimate(request));
    }

    [Fact]
    public void Exact_component_rejects_an_unrepresentable_high_significance_amount()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var source = registry.Entries[0];
        var entry = source with
        {
            Rates = source.Rates with
            {
                InputPerMillionTokens = 999999.999999m,
                OutputPerMillionTokens = null
            }
        };
        var engine = new PricingEstimationEngine(
            PricingCatalog.Create(registry with { Entries = [entry] }));
        var request = PricingTestData.Request(usage:
            PricingUsage.Empty with
            {
                InputTokens = PricingTestData.Quantity(999999999999999999m)
            });

        Assert.Throws<ArgumentException>(() => engine.Estimate(request));
    }

    [Fact]
    public void Request_to_credit_supports_the_exact_minimum_amount()
    {
        var registry = PricingTestData.SyntheticRegistry();
        var source = registry.Entries[1];
        var entry = source with
        {
            Rates = source.Rates with
            {
                PerCredit = 0.000001m,
                RequestCreditMultiplier = 0.000001m
            }
        };
        var engine = new PricingEstimationEngine(
            PricingCatalog.Create(registry with { Entries = [entry] }));
        var request = PricingTestData.Request(
            model: source.CanonicalModelId,
            billingMode: PricingBillingModes.GitHubLegacyRequests,
            pricingRoute: PricingRoutes.LegacyRequest,
            usage: PricingUsage.Empty with
            {
                RequestCount = PricingTestData.Quantity(1)
            });

        var result = engine.Estimate(request);

        Assert.Equal(0.000000000001m, result.Amount);
        Assert.Equal(0.000001m, Assert.Single(result.Components).Quantity);
    }

    [Fact]
    public void Request_to_credit_rejects_an_unrepresentable_second_multiplication()
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
        var engine = new PricingEstimationEngine(
            PricingCatalog.Create(registry with { Entries = [entry] }));
        var request = PricingTestData.Request(
            model: source.CanonicalModelId,
            billingMode: PricingBillingModes.GitHubLegacyRequests,
            pricingRoute: PricingRoutes.LegacyRequest,
            usage: PricingUsage.Empty with
            {
                RequestCount = PricingTestData.Quantity(999999999999m)
            });

        Assert.Throws<ArgumentException>(() => engine.Estimate(request));
    }

    private static PricingEstimationEngine Engine() =>
        new(PricingCatalog.Create(PricingTestData.SyntheticRegistry()));

    private sealed class StatefulCompletenessReasons : IReadOnlyList<string>
    {
        private int _enumerationCount;

        public int Count => 0;

        public string this[int index] => throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<string> GetEnumerator()
        {
            _enumerationCount++;
            return (_enumerationCount <= 3
                    ? Array.Empty<string>()
                    : [PricingSourceCompletenessReasons.HistoricalSummaryOnly])
                .AsEnumerable()
                .GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
