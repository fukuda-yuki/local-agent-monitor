using System.Globalization;
using System.Text.Json;
using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.ConfigCli.FirstTrace;

internal static class FirstTraceJson
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static string Serialize(FirstTraceEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("contract_version", FirstTraceCodes.ContractVersion);
            writer.WriteString("command", envelope.Command);
            writer.WriteBoolean("success", envelope.Success);
            writer.WriteString("code", envelope.Code);
            WriteNullableString(writer, "adapter", envelope.Adapter);
            WriteNullableString(writer, "source_surface", envelope.SourceSurface);
            WriteNullableString(writer, "verification_id", envelope.VerificationId);
            WriteDoctor(writer, "doctor", envelope.Doctor);
            WriteDoctor(writer, "evaluation_preview", envelope.EvaluationPreview);
            writer.WritePropertyName("guidance");
            writer.WriteStartArray();
            foreach (var guidance in envelope.Guidance)
            {
                writer.WriteStartObject();
                writer.WriteString("interaction", guidance.Interaction);
                writer.WriteString("text", guidance.Text);
                WriteNullableString(writer, "command", guidance.Command);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("candidates");
            writer.WriteStartArray();
            foreach (var candidate in envelope.Candidates)
            {
                writer.WriteStartObject();
                writer.WriteString("candidate_id", candidate.CandidateId);
                writer.WriteString("evidence_class", Wire(candidate.EvidenceClass));
                writer.WriteString("evidence_kind", Wire(candidate.EvidenceKind));
                writer.WriteString("source_surface", candidate.SourceSurface);
                WriteNullableString(writer, "source_adapter", candidate.SourceAdapter);
                writer.WriteString("evidence_ref", candidate.EvidenceRef);
                writer.WriteString("observed_at", Format(candidate.ObservedAt));
                writer.WriteString("expires_at", Format(candidate.ExpiresAt));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteBoolean("truncated", envelope.Truncated);
            writer.WriteEndObject();
            writer.Flush();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteDoctor(Utf8JsonWriter writer, string propertyName, DoctorResult? result)
    {
        writer.WritePropertyName(propertyName);
        if (result is null)
        {
            writer.WriteNullValue();
            return;
        }

        using var document = JsonDocument.Parse(DoctorJson.SerializeResult(result));
        document.RootElement.WriteTo(writer);
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static string Wire(DoctorEvidenceClass value) => value switch
    {
        DoctorEvidenceClass.RealSource => "real_source",
        DoctorEvidenceClass.SyntheticProbe => "synthetic_probe",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string Wire(DoctorEvidenceKind value) => value switch
    {
        DoctorEvidenceKind.Ingest => "ingest",
        DoctorEvidenceKind.RawPersistence => "raw_persistence",
        DoctorEvidenceKind.Projection => "projection",
        DoctorEvidenceKind.ExactSessionBinding => "exact_session_binding",
        DoctorEvidenceKind.CompletenessContent => "completeness_content",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
