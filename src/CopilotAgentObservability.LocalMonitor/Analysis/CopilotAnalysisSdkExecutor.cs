using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal sealed class CopilotAnalysisSdkExecutor : ICopilotAnalysisSdkExecutor
{
    private readonly Func<CopilotClientOptions, SessionConfig, CopilotClient> createClient;

    internal CopilotAnalysisSdkExecutor()
        : this(static (options, _) => new CopilotClient(options)) { }

    internal CopilotAnalysisSdkExecutor(Func<CopilotClientOptions, SessionConfig, CopilotClient> createClient)
    {
        ArgumentNullException.ThrowIfNull(createClient);
        this.createClient = createClient;
    }

    public async Task<string> ExecuteAsync(string childDirectory, CopilotAnalysisExecutionSettings settings, CopilotAnalysisToolRequest request, CancellationToken cancellationToken)
    {
        var sessionConfig = CreateSessionConfig(
            settings,
            request,
            request.Data.InstructionFindingCollector,
            childDirectory);
        var clientOptions = new CopilotClientOptions
        {
            Mode = CopilotClientMode.Empty,
            BaseDirectory = childDirectory,
            WorkingDirectory = childDirectory,
        };
        var client = createClient(clientOptions, sessionConfig);
        CopilotSession? session = null;
        ExceptionDispatchInfo? primaryFailure = null;
        try
        {
            await client.StartAsync(cancellationToken);
            session = await client.CreateSessionAsync(sessionConfig, cancellationToken);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var final = new StringBuilder();
            using var subscription = session.On<SessionEvent>(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta: final.Append(delta.Data.DeltaContent); break;
                    case AssistantMessageEvent message when final.Length == 0: final.Append(message.Data.Content); break;
                    case SessionIdleEvent: done.TrySetResult(); break;
                    case SessionErrorEvent error: done.TrySetException(new InvalidOperationException(error.Data.Message)); break;
                }
            });
            await session.SendAndWaitAsync(new MessageOptions { Prompt = request.Prompt }, TimeSpan.FromSeconds(settings.TimeoutSeconds), cancellationToken);
            done.TrySetResult();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return final.Length == 0 ? "Copilot SDK analysis completed without a textual result." : final.ToString();
        }
        catch (Exception exception)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
            throw;
        }
        finally
        {
            Exception? disposeFailure = null;
            try { if (session is not null) await session.DisposeAsync(); } catch (Exception exception) { disposeFailure = exception; }
            try { await client.DisposeAsync(); } catch (Exception exception) { disposeFailure ??= exception; }
            if (primaryFailure is null && disposeFailure is not null) ExceptionDispatchInfo.Capture(disposeFailure).Throw();
        }
    }

    private static SessionConfig CreateSessionConfig(
        CopilotAnalysisExecutionSettings settings,
        CopilotAnalysisToolRequest request,
        InstructionFindingSubmissionCollectorV1? instructionFindingCollector,
        string childDirectory)
    {
        var tools = new List<AIFunctionDeclaration>
        {
            DefineTool("get_raw_trace", "Return the raw trace records for this Local Monitor analysis run.", () => Serialize(request.Data.RawTrace)),
            DefineTool("get_raw_record", "Return the selected raw record for this Local Monitor analysis run.", () => Serialize(request.Data.RawRecord)),
            DefineTool("get_raw_span_context", "Return the selected raw span context for this Local Monitor analysis run.", () => Serialize(request.Data.RawSpanContext)),
            DefineTool("get_trace_summary", "Return the sanitized trace summary for this Local Monitor analysis run.", () => Serialize(request.Data.TraceSummary)),
            DefineTool("get_trace_span_tree", "Return the sanitized span tree for this Local Monitor analysis run.", () => Serialize(request.Data.TraceSpanTree)),
            DefineTool("get_cache_summary", "Return the sanitized cache summary for this Local Monitor analysis run.", () => Serialize(request.Data.CacheSummary)),
            DefineTool("get_instruction_evidence", "Return deterministic instruction evidence for this Local Monitor analysis run.", () => Serialize(request.Data.InstructionEvidence)),
        };
        if (instructionFindingCollector is not null)
            tools.Add(DefineInstructionFindingSubmissionTool(instructionFindingCollector));
        var availableTools = new ToolSet();
        foreach (var tool in tools)
            availableTools.AddCustom(tool.Name);
        return new SessionConfig
        {
            Model = settings.Model,
            Streaming = true,
            OnPermissionRequest = static (_, _) => Task.FromResult(DenyPermission()),
            Provider = settings.Provider,
            Tools = tools,
            AvailableTools = availableTools,
            WorkingDirectory = childDirectory,
            LargeOutput = new LargeToolOutputConfig { Enabled = true, OutputDirectory = childDirectory },
            SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Append, Content = "You are analyzing a local Copilot/agent observability trace. Use the provided tools for raw data. Do not claim the response is repository-safe." },
        };
    }

#pragma warning disable GHCP001
    private static GitHub.Copilot.Rpc.PermissionDecision DenyPermission() =>
        GitHub.Copilot.Rpc.PermissionDecision.UserNotAvailable();
#pragma warning restore GHCP001

    private static AIFunction DefineTool(string name, string description, Func<string> tool) => CopilotTool.DefineTool((([Description("No input is required for this run-scoped Local Monitor tool.")] string? _ = null) => tool()), new CopilotToolOptions { SkipPermission = true }, new AIFunctionFactoryOptions { Name = name, Description = description });

    private static AIFunction DefineInstructionFindingSubmissionTool(InstructionFindingSubmissionCollectorV1 collector) =>
        CopilotTool.DefineTool(
            ([Description("Closed instruction-finding category id.")] string category,
             [Description("Finding verdict: supported, weak, or incomplete.")] string verdict,
             [Description("Extractor source: deterministic_prepass or prompt_only.")] string extractor_source,
             [Description("JSON array containing only exact evidence references returned by get_instruction_evidence.")] string evidence_refs_json) =>
                collector.SubmitWire(category, verdict, extractor_source, evidence_refs_json),
            new CopilotToolOptions { SkipPermission = true },
            new AIFunctionFactoryOptions
            {
                Name = "submit_instruction_finding",
                Description = "Validate and submit one instruction finding without free-form or raw content.",
            });

    private static string Serialize(object? value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
