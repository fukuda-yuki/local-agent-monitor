using System.Text;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace CopilotAgentObservability.LocalMonitor.LocalAi;

internal sealed class GitHubCopilotLocalAiProviderAdapterV1(
    Func<IOwnedCopilotClientV1?> clientFactory,
    string model) : ILocalAiProviderAdapterV1
{
    public async ValueTask<LocalAiProviderOutcomeV1> ExecuteAsync(LocalAiProviderRequestV1 request, CancellationToken token)
    {
        var client = clientFactory();
        if (client is null) return LocalAiProviderOutcomeV1.Failed();
        await using (client.ConfigureAwait(false))
        {
            await client.StartAsync(token).ConfigureAwait(false);
            var rawTool = CopilotTool.DefineTool(
                async ([Description("Exact evidence identifier from evidence_refs.")] string evidence_id) =>
                    Convert.ToBase64String(await request.RawReads.ReadAsync(evidence_id, token).ConfigureAwait(false)),
                new CopilotToolOptions { SkipPermission = true },
                new AIFunctionFactoryOptions { Name = "read_exact_evidence", Description = "Read one exact retained raw evidence body as base64." });
            var availableTools = new ToolSet(); availableTools.AddCustom("read_exact_evidence");
            var config = new SessionConfig
            {
                Model = model,
                Streaming = false,
                EnableSkills = false,
                Tools = [rawTool],
                AvailableTools = availableTools,
#pragma warning disable GHCP001
                OnPermissionRequest = static (_, _) => Task.FromResult(PermissionDecision.UserNotAvailable()),
#pragma warning restore GHCP001
                SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Append,
                    Content = "Return only one closed JSON object with exactly summary, findings, improvement_suggestions, and limitations. Never include credentials, paths, prompts, tool payloads, scope, snapshot, or provider metadata."
                },
            };
            await using var session = await client.CreateSessionAsync(config, token).ConfigureAwait(false);
            var prompt = BuildPrompt(request);
            var content = await session.SendAndReadFinalContentAsync(prompt, TimeSpan.FromSeconds(600), token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content)) return LocalAiProviderOutcomeV1.Partial();
            return LocalAiProviderOutcomeV1.Complete(Encoding.UTF8.GetBytes(content));
        }
    }

    private static string BuildPrompt(LocalAiProviderRequestV1 request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Analyze only this immutable bounded snapshot projection and its exact evidence identifiers.");
        builder.AppendLine(Encoding.UTF8.GetString(request.Snapshot.PayloadCanonicalJson));
        builder.AppendLine(Encoding.UTF8.GetString(request.Snapshot.EvidenceIndexCanonicalJson));
        if (request.Question is not null)
        {
            builder.AppendLine("Transient follow-up question:"); builder.AppendLine(request.Question);
            foreach (var turn in request.PriorTurns) { builder.AppendLine(turn.Question); builder.AppendLine(turn.Answer); }
        }
        return builder.ToString();
    }
}
