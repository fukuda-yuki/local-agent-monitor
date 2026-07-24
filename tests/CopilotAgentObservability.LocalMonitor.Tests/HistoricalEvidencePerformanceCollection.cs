namespace CopilotAgentObservability.LocalMonitor.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HistoricalEvidencePerformanceCollection
{
    public const string Name = "Historical evidence production budget";
}

public sealed class HistoricalEvidencePerformanceCollectionTests
{
    [Fact]
    public void ProductionBudgetTestsRunOutsideTheParallelTestPool()
    {
        var definition = Assert.IsType<CollectionDefinitionAttribute>(
            typeof(HistoricalEvidencePerformanceCollection).GetCustomAttributes(false)
                .Single(attribute => attribute is CollectionDefinitionAttribute));
        Assert.True(definition.DisableParallelization);

        var membership = Assert.Single(typeof(HistoricalEvidenceProductionTests).CustomAttributes,
            attribute => attribute.AttributeType == typeof(CollectionAttribute));
        Assert.Equal(HistoricalEvidencePerformanceCollection.Name, Assert.Single(membership.ConstructorArguments).Value);
    }
}
