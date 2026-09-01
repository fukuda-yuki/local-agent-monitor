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
    private const string StructuredResultInstruction = """
Return raw JSON only: no Markdown, code fences, or surrounding prose.
Return one closed object with exactly these root fields: summary (string), findings (array), improvement_suggestions (array), limitations (array of strings).
Each findings item is one closed object with exactly: finding_id (non-blank string: not empty or whitespace-only), title (non-blank string: not empty or whitespace-only), explanation (non-blank string: not empty or whitespace-only), evidence_state (one of "supported" or "limited"), evidence_refs (array of 1 to 16 non-blank strings, each not empty or whitespace-only and exactly matching an identifier in the supplied evidence index), limitation (non-blank string: not empty or whitespace-only).
Each improvement_suggestions item is one closed object with exactly: suggestion_id (non-blank string: not empty or whitespace-only), target_kind (one of "instructions", "skill", "agent", "subagent_input", or "tool_configuration"), target_label (non-blank string: not empty or whitespace-only), concrete_change (non-blank string: not empty or whitespace-only), rationale (non-blank string: not empty or whitespace-only), expected_effect (non-blank string: not empty or whitespace-only), risks_or_limitations (non-blank string: not empty or whitespace-only), evidence_refs (array of 1 to 16 non-blank strings, each not empty or whitespace-only and exactly matching an identifier in the supplied evidence index).
Never include credentials, paths, prompts, tool payloads, scope, snapshot, or provider metadata.
""";

    public async ValueTask<LocalAiProviderOutcomeV1> ExecuteAsync(LocalAiProviderRequestV1 request, CancellationToken token)
    {
        var client = clientFactory();
        if (client is null) return LocalAiProviderOutcomeV1.Failed();
        await using (client.ConfigureAwait(false))
        {
            await client.StartAsync(token).ConfigureAwait(false);
            var rawTool = CopilotTool.DefineTool(
                async ([Description("Exact process-internal raw handle from raw_content.evidence_id; never use this handle in result evidence_refs.")] string evidence_id) =>
                    Convert.ToBase64String(await request.RawReads.ReadAsync(evidence_id, token).ConfigureAwait(false)),
                new CopilotToolOptions { SkipPermission = true },
                new AIFunctionFactoryOptions { Name = "read_exact_evidence", Description = "Read one exact retained raw body by its raw_content handle. Cite raw_content.citation_ref in the result." });
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
                    Content = StructuredResultInstruction
                },
            };
            await using var session = await client.CreateSessionAsync(config, token).ConfigureAwait(false);
            var prompt = BuildPrompt(request);
            var content = await session.SendAndReadFinalContentAsync(prompt, TimeSpan.FromSeconds(600), token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content)) return LocalAiProviderOutcomeV1.Partial();
            return LocalAiProviderOutcomeV1.Complete(Encoding.UTF8.GetBytes(content));
        }
    }

    internal static string BuildPrompt(LocalAiProviderRequestV1 request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Analyze only this immutable bounded snapshot projection and its exact evidence identifiers.");
        builder.AppendLine(StructuredResultInstruction);
        if (request.Snapshot.ScopeKind == "repository_selection")
            builder.AppendLine("Summarize only the supplied frozen repository facts and evidence. Cite only the exact supplied canonical Session/node evidence locations; never cite a bare node ID. Do not explore, infer, or request any session outside this snapshot. Do not recalculate deterministic facts, claim effects or causality, score quality, rank or prioritize, classify improvement or regression, or invent facts. Do not state or promote AI output as a deterministic fact.");
        else if (request.Snapshot.ScopeKind == "comparison")
            builder.AppendLine("Interpret only the stored observed differences and offer concrete improvement suggestions. Cite only the exact supplied evidence locations. Do not state an effect verdict, quality evidence, priority score, improvement or regression classification, deterministic fact, or any recalculation.");
        else
            builder.AppendLine("Result evidence_refs may contain only canonical node IDs listed in the evidence index. raw_content.evidence_id is only a tool handle; after reading raw bytes cite its raw_content.citation_ref node. Sanitized span facts likewise cite their citation_ref node.");
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
