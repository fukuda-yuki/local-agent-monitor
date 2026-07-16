using System.Text.Json;

namespace CopilotAgentObservability.Doctor;

public static class DoctorHumanProjector
{
    public static string Project(DoctorResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var state = result.Evaluation?.PrimaryState;
        if (state is null)
        {
            return $"Doctor: {ToWireValue(result.Code)}";
        }

        return string.Join(
            Environment.NewLine,
            $"Doctor: {ToWireValue(state.StateCode)}",
            $"Severity: {ToWireValue(state.Severity)}",
            $"Source: {BoundedSource(state.SourceSurface)}",
            $"Next action: {ToWireValue(state.NextAction)}");
    }

    private static string BoundedSource(string sourceSurface) =>
        DoctorValidation.IsSourceToken(sourceSurface) ? sourceSurface : "unknown";

    private static string ToWireValue<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
