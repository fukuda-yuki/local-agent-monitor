# Sprint9 Research Notes

External references gathered while scoping Sprint9 (web research, 2026-06-27).
These inform the design but are **reference only** — product behavior is defined
by `docs/requirements.md`, `docs/spec.md`, and `docs/specifications/`.

## VS Code Agent Debug View (primary reference)

The user named this as "very close to what I want." It is a manual-debugging UI
in the editor; we do **not** re-implement it (D001/D021), but its structure is
the model for the Sprint9 sanitized view.

Source: [Debug chat interactions (code.visualstudio.com)](https://code.visualstudio.com/docs/copilot/chat/chat-debug-view)

Four views:

- **Logs** — chronological event list; each event has timestamp, event type,
  summary; expandable to full system prompt (LLM request) or tool call
  input/output. Switchable between a flat list and a **tree grouped by
  sub-agent**. Filterable by event type.
- **Summary** — session aggregates: total tool calls, token usage, error count,
  overall duration.
- **Flow Chart** — graphical agent ↔ sub-agent interaction (pan/zoom, selectable
  nodes). *Deferred to the later design sprint.*
- **Cache Explorer** — model turns grouped by user request: cache-hit %,
  duration, model, timestamp; prefix diff of consecutive requests. *Deferred;
  Sprint9 still captures cache-token columns so it can be built later.*

Sprint9 mapping: Summary panel ← Summary; span timeline + sub-agent tree ←
Logs (tree-by-subagent); inline raw bodies ← expandable input/output.

## VS Code Copilot agent-mode OTel emission (feasibility anchor)

Source: [Monitor agent usage with OpenTelemetry (code.visualstudio.com)](https://code.visualstudio.com/docs/agents/guides/monitoring-agents)

This confirms the four user requirements are directly derivable from the OTLP the
monitor already receives.

Spans:

- `invoke_agent` — wraps an agent orchestration. Attrs: `gen_ai.agent.name`
  (copilot / copilotcli / claude), `gen_ai.usage.input_tokens` /
  `output_tokens`, `gen_ai.usage.cache_read.input_tokens` /
  `cache_creation.input_tokens`, `gen_ai.request.model` / `gen_ai.response.model`,
  `gen_ai.conversation.id`, `error.type`.
- `chat` — one span per LLM call. Attrs: `gen_ai.response.finish_reasons`,
  per-call `gen_ai.usage.input_tokens` / `output_tokens`,
  `gen_ai.usage.reasoning.output_tokens`, `error.type`.
- `execute_tool` — one span per tool invocation. Attrs: `gen_ai.tool.name`,
  `gen_ai.tool.type` (`function` / `extension`=MCP),
  `github.copilot.tool.parameters.mcp_server_name_hash` (SHA-256),
  `github.copilot.tool.parameters.mcp_tool_name`, `error.type`.
- `execute_hook` — one span per hook execution.

**Sub-agent hierarchy:** trace context is auto-propagated; a sub-agent's
`invoke_agent` span appears as a **child of the parent's `execute_tool` span**.
This is how Sprint9 reconstructs sub-agent launch + model + tokens (via
`parent_span_id`).

**Success/failure:** absence of `error.type` ⇒ success; presence ⇒ failure (on
`invoke_agent` / `chat` / `execute_tool` / `execute_hook`). Plus span status code.

Metrics (not required for Sprint9's per-trace view, but corroborate the model):
`copilot_chat.tool.call.count` (by name + success), `copilot_chat.tool.call.duration`,
`copilot_chat.agent.invocation.duration`, `copilot_chat.agent.turn.count`.

## OpenTelemetry GenAI semantic conventions

Sources:
[GenAI agent spans](https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-agent-spans/) ·
[GenAI spans](https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-spans/) ·
[Inside the LLM Call: GenAI Observability with OpenTelemetry](https://opentelemetry.io/blog/2026/genai-observability/)

- `gen_ai.operation.name` ∈ { `invoke_agent`, `execute_tool`, `create_agent`,
  `chat`, ... }. Top-level `invoke_agent` contains child `chat` (per LLM call) and
  `execute_tool` (per tool) spans.
- `execute_tool` carries `gen_ai.tool.call.arguments` and
  `gen_ai.tool.call.result` (opt-in content — **raw** for us).
- Token usage and model are standardized; content capture is opt-in.

The existing `RawMeasurementNormalizer` already reads `gen_ai.operation.name`,
`gen_ai.tool.name`, and `gen_ai.usage.*` (with several spelling fallbacks), so
Sprint9 extends an established parser rather than inventing one.

## Comparable agent-observability tools (landscape)

Sources:
[Langfuse vs Phoenix/Arize](https://langfuse.com/faq/all/best-phoenix-arize-alternatives) ·
[Agent observability comparison (Analytics Vidhya)](https://www.analyticsvidhya.com/blog/2026/06/agent-observability-with-langsmith-langfuse-arize/) ·
[Top agent observability platforms (Laminar)](https://laminar.sh/article/2026-04-23-top-6-agent-observability-platforms) ·
[Arize Phoenix (GitHub)](https://github.com/arize-ai/phoenix)

Common pattern across Langfuse / Phoenix / Arize / LangSmith: capture **each LLM
step with its own prompt, token usage, latency, and failure point**, and render a
**hierarchical/tree view of the agent execution path** so a single failed tool
call or retrieval is locatable. This validates Sprint9's per-span + tree model.
We deliberately stay local-first and far smaller in scope: no eval harness, no
drift/cohort monitoring, no hosted backend — just a sanitized local view over the
raw store the monitor already owns.
