using System.Runtime.ExceptionServices;
using System.Text;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace CopilotAgentObservability.LocalMonitor.LocalAi;

internal sealed class GitHubCopilotLocalAiProviderAdapterV1(
    Func<IOwnedCopilotClientV1?> clientFactory,
    string model,
    TextWriter? diagnosticOutput = null) : ILocalAiProviderAdapterV1
{
    private const string StructuredResultInstruction = """
Return raw JSON only: no Markdown, code fences, or surrounding prose.
Return one closed object with exactly these root fields: summary (string), findings (array), improvement_suggestions (array), limitations (array of strings).
Each findings item is one closed object with exactly: finding_id (non-blank string: not empty or whitespace-only), title (non-blank string: not empty or whitespace-only), explanation (non-blank string: not empty or whitespace-only), evidence_state (one of "supported" or "limited"), evidence_refs (array of 1 to 16 ordinal case-sensitive distinct non-blank strings, each not empty or whitespace-only and exactly matching an identifier in the supplied evidence index), limitation (non-blank string: not empty or whitespace-only).
Each improvement_suggestions item is one closed object with exactly: suggestion_id (non-blank string: not empty or whitespace-only), target_kind (one of "instructions", "skill", "agent", "subagent_input", or "tool_configuration"), target_label (non-blank string: not empty or whitespace-only), concrete_change (non-blank string: not empty or whitespace-only), rationale (non-blank string: not empty or whitespace-only), expected_effect (non-blank string: not empty or whitespace-only), risks_or_limitations (non-blank string: not empty or whitespace-only), evidence_refs (array of 1 to 16 ordinal case-sensitive distinct non-blank strings, each not empty or whitespace-only and exactly matching an identifier in the supplied evidence index).
Never include credentials, local filesystem paths, prompts, tool payloads, scope, snapshot, or provider metadata. Exact supplied canonical evidence-location strings, including slash-delimited locations, may appear solely as string values in evidence_refs.
""";

    public async ValueTask<LocalAiProviderOutcomeV1> ExecuteAsync(LocalAiProviderRequestV1 request, CancellationToken token)
    {
        IOwnedCopilotClientV1? client;
        try { client = clientFactory(); }
        catch (Exception failure)
        {
            Diagnose(request.Run.RunId, "client_factory", FailureReason(failure, token));
            throw;
        }
        if (client is null)
        {
            Diagnose(request.Run.RunId, "client_factory", "client_unavailable");
            return LocalAiProviderOutcomeV1.Failed();
        }
        IOwnedCopilotSessionV1? session = null;
        string? sessionId = null;
        LocalAiProviderOutcomeV1? outcome = null;
        ExceptionDispatchInfo? primaryFailure = null;
        var stage = "client_start";
        try
        {
            await client.StartAsync(token).ConfigureAwait(false);
            stage = "session_create";
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
            session = await client.CreateSessionAsync(config, token).ConfigureAwait(false);
            sessionId = session.SessionId;
            stage = "send_read";
            var prompt = BuildPrompt(request);
            var response = await session.SendAndReadFinalContentAsync(prompt, TimeSpan.FromSeconds(600), token).ConfigureAwait(false);
            if (response is null || string.IsNullOrWhiteSpace(response.Content))
            {
                Diagnose(request.Run.RunId, stage, "final_content_absent");
                outcome = LocalAiProviderOutcomeV1.Partial();
            }
            else if (string.IsNullOrWhiteSpace(response.Model) || !string.Equals(response.Model, model, StringComparison.Ordinal))
            {
                Diagnose(request.Run.RunId, "effective_model",
                    string.IsNullOrWhiteSpace(response.Model) ? "effective_model_absent" : "effective_model_mismatch");
                outcome = LocalAiProviderOutcomeV1.Failed();
            }
            else
                outcome = LocalAiProviderOutcomeV1.Complete(Encoding.UTF8.GetBytes(response.Content));
        }
        catch (Exception failure)
        {
            Diagnose(request.Run.RunId, stage, FailureReason(failure, token));
            primaryFailure = ExceptionDispatchInfo.Capture(failure);
        }

        ExceptionDispatchInfo? cleanupFailure = null;
        if (session is not null)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception failure)
            {
                Diagnose(request.Run.RunId, "session_dispose", FailureReason(failure, token));
                cleanupFailure = ExceptionDispatchInfo.Capture(failure);
            }
            try { await client.DeleteSessionAsync(sessionId!, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception failure)
            {
                Diagnose(request.Run.RunId, "session_delete", FailureReason(failure, token));
                cleanupFailure ??= ExceptionDispatchInfo.Capture(failure);
            }
        }
        try { await client.DisposeAsync().ConfigureAwait(false); }
        catch (Exception failure)
        {
            Diagnose(request.Run.RunId, "client_dispose", FailureReason(failure, token));
            cleanupFailure ??= ExceptionDispatchInfo.Capture(failure);
        }

        primaryFailure?.Throw();
        cleanupFailure?.Throw();
        return outcome!;
    }

    private void Diagnose(string runId, string stage, string reason)
    {
        if (diagnosticOutput is null || !Guid.TryParseExact(runId, "D", out var identity)) return;
        try { diagnosticOutput.WriteLine($"local_ai_provider_failure run_id={identity:D} stage={stage} reason={reason}"); }
        catch { }
    }

    private static string FailureReason(Exception failure, CancellationToken token) => failure switch
    {
        OperationCanceledException => token.IsCancellationRequested ? "cancellation_requested" : "cancellation_unrequested",
        TimeoutException => "timeout",
        _ => "exception",
    };

    internal static string BuildPrompt(LocalAiProviderRequestV1 request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Analyze only this immutable bounded snapshot projection and its exact evidence identifiers.");
        builder.AppendLine(StructuredResultInstruction);
        if (request.Snapshot.ScopeKind == "repository_selection")
            builder.AppendLine("Summarize only the supplied frozen repository facts and evidence. Cite only the exact supplied canonical Session/node evidence locations; never cite a bare node ID. Do not explore, infer, or request any session outside this snapshot. Do not recalculate deterministic facts, claim effects or causality, score quality, rank or prioritize, classify improvement or regression, or invent facts. Do not state or promote AI output as a deterministic fact.");
        else if (request.Snapshot.ScopeKind == "comparison")
            builder.AppendLine("Interpret only the stored observed differences and offer concrete improvement suggestions. Cite only the exact supplied evidence locations. Dynamic metric availability is not individually addressable and must not solely ground a finding or suggestion or be cited through another location. Do not state an effect verdict, quality evidence, priority score, improvement or regression classification, deterministic fact, or any recalculation.");
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
