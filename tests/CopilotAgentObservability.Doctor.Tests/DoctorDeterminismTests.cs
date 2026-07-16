using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotAgentObservability.Doctor.Tests;

public sealed class DoctorDeterminismTests
{
    [Fact]
    public void EvaluateAndSerialize_EquivalentInputs_AreByteDeterministic()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "TestData", "monitor-not-running.facts.json");
        var originalJson = File.ReadAllText(fixturePath);
        var root = JsonNode.Parse(originalJson)!.AsObject();
        var reordered = new JsonObject();
        foreach (var property in root.Reverse())
        {
            reordered[property.Key] = property.Value?.DeepClone();
        }

        var original = DoctorJson.DeserializeFactSnapshot(originalJson);
        var equivalent = DoctorJson.DeserializeFactSnapshot(reordered.ToJsonString());
        var expected = Encoding.UTF8.GetBytes(DoctorJson.SerializeResult(DoctorEvaluator.Evaluate(original)));

        for (var iteration = 0; iteration < 100; iteration++)
        {
            var actual = Encoding.UTF8.GetBytes(DoctorJson.SerializeResult(DoctorEvaluator.Evaluate(equivalent)));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void SerializeResult_UsesCanonicalPropertyEnumAndUtcOrdering()
    {
        var result = DoctorEvaluator.Evaluate(DoctorTestSnapshots.FirstTraceReady(exactBindingRequired: true));

        var json = DoctorJson.SerializeResult(result);

        Assert.StartsWith("{\"schema_version\":\"doctor.v1\",\"success\":true,\"code\":\"evaluation_completed\",\"evaluation\":", json, StringComparison.Ordinal);
        Assert.Contains("\"state_code\":\"first_trace_ready\"", json, StringComparison.Ordinal);
        Assert.Contains("\"observed_at\":\"2026-07-16T01:02:03.0000000Z\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("+00:00", json, StringComparison.Ordinal);
        Assert.Equivalent(result, DoctorJson.DeserializeResult(json), strict: true);
    }

    [Fact]
    public void DeserializeResult_DuplicateProperty_IsRejectedWithSanitizedError()
    {
        var json = DoctorJson.SerializeResult(DoctorEvaluator.Evaluate(DoctorTestSnapshots.ReadyNoRealTrace()));
        var duplicate = json.Replace(
            "\"success\":true",
            "\"success\":true,\"success\":false",
            StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(() => DoctorJson.DeserializeResult(duplicate));

        Assert.Equal("invalid_input", exception.Message);
    }

    [Fact]
    public void HumanProjection_UntrustedOversizedResult_RemainsBoundedAndUsesNoExternalInput()
    {
        var result = DoctorEvaluator.Evaluate(DoctorTestSnapshots.ReadyNoRealTrace());
        var state = result.Evaluation!.PrimaryState! with { SourceSurface = new string('x', 4096) };
        var oversized = result with
        {
            Evaluation = result.Evaluation with { SourceSurface = new string('y', 4096), PrimaryState = state, States = [state] },
        };

        var human = DoctorHumanProjector.Project(oversized);

        Assert.InRange(human.Length, 1, 1024);
        Assert.DoesNotContain(new string('x', 65), human, StringComparison.Ordinal);
        Assert.Contains("Doctor: ready_no_real_trace", human, StringComparison.Ordinal);
    }
}
