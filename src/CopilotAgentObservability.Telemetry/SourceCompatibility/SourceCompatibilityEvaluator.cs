namespace CopilotAgentObservability.Telemetry;

public static class SourceCompatibilityEvaluator
{
    public static SourceCompatibilityDecision Assess(
        string sourceSurface,
        string? sourceApplicationVersion,
        SourceStructuralInventory inventory,
        int observedRecognizedCount,
        VerifiedSourceFingerprintRegistry registry)
    {
        SourceMetadata.ValidateRequired(sourceSurface, nameof(sourceSurface));
        SourceMetadata.ValidateOptional(sourceApplicationVersion, nameof(sourceApplicationVersion));
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(registry);
        if (observedRecognizedCount is < 0 or > SourceOccurrenceCount.Maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(observedRecognizedCount));
        }

        if (registry.TryGetRecognitionProfile(
            sourceSurface, sourceApplicationVersion, inventory.SchemaFingerprint, out var profile) &&
            observedRecognizedCount < profile.ExpectedRecognizedCount.Value)
        {
            return SourceCompatibilityDecision.ForState(SourceCompatibilityState.RecognizedRecordDropDetected);
        }

        if (!inventory.HasRequiredTraceSignal ||
            registry.IsExplicitlyIncompatible(sourceSurface, sourceApplicationVersion))
        {
            return SourceCompatibilityDecision.ForState(SourceCompatibilityState.UnsupportedSourceVersion);
        }

        if (!registry.IsKnownFingerprint(sourceSurface, inventory.SchemaFingerprint))
        {
            return SourceCompatibilityDecision.ForState(SourceCompatibilityState.SchemaDriftDetected);
        }

        if (inventory.HasUnknownFields)
        {
            return SourceCompatibilityDecision.ForState(SourceCompatibilityState.SupportedWithUnknownFields);
        }

        return SourceCompatibilityDecision.ForState(SourceCompatibilityState.Supported);
    }
}
