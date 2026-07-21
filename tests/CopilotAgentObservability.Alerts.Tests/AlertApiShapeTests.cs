using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.Alerts.Tests;

public sealed class AlertApiShapeTests
{
    [Theory]
    [InlineData(typeof(AlertReceipt))]
    [InlineData(typeof(AlertRuleRegistry))]
    [InlineData(typeof(AlertEvaluationEngine))]
    [InlineData(typeof(AlertSensitiveValueHasher))]
    [InlineData(typeof(IAlertEngineStore))]
    [InlineData(typeof(AlertRuleOutcome))]
    public void AlertDomain_ExposesFrozenV1ExtensionPoints(Type type)
    {
        Assert.True(type.IsPublic);
    }
}
