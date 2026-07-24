namespace CopilotAgentObservability.LocalMonitor.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Issue95ValidationPerformanceCollection
{
    public const string Name = "Issue #95 validation performance";
}

public sealed class Issue95ValidationPerformanceCollectionTests
{
    [Fact]
    public void EvidenceChainBudgetRunsOutsideTheParallelTestPool()
    {
        var definition = Assert.IsType<CollectionDefinitionAttribute>(
            typeof(Issue95ValidationPerformanceCollection).GetCustomAttributes(false)
                .Single(attribute => attribute is CollectionDefinitionAttribute));
        Assert.True(definition.DisableParallelization);

        var membership = Assert.Single(typeof(Issue95ValidationContractTests).CustomAttributes,
            attribute => attribute.AttributeType == typeof(CollectionAttribute));
        Assert.Equal(Issue95ValidationPerformanceCollection.Name, Assert.Single(membership.ConstructorArguments).Value);
    }
}
