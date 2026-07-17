using System.Text;
using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.ConfigCli.FirstTrace;

internal static class FirstTraceHumanProjector
{
    public static string Project(FirstTraceEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var builder = new StringBuilder();
        builder.Append("first-trace ").Append(envelope.Command).Append(": ").AppendLine(envelope.Code);
        if (envelope.VerificationId is not null)
        {
            builder.Append("verification_id: ").AppendLine(envelope.VerificationId);
        }

        if (envelope.Doctor is not null)
        {
            builder.AppendLine(DoctorHumanProjector.Project(envelope.Doctor));
        }

        if (envelope.EvaluationPreview is not null)
        {
            builder.AppendLine("evaluation_preview:");
            builder.AppendLine(DoctorHumanProjector.Project(envelope.EvaluationPreview));
        }

        foreach (var guidance in envelope.Guidance)
        {
            builder.Append(guidance.Interaction).Append(": ").AppendLine(guidance.Text);
            if (guidance.Command is not null)
            {
                builder.Append("  ").AppendLine(guidance.Command);
            }
        }

        if (envelope.Candidates.Count > 0)
        {
            builder.Append("candidates: ").AppendLine(envelope.Candidates.Count.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString().TrimEnd();
    }
}
