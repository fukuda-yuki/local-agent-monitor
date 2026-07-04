namespace CopilotAgentObservability.ConfigCli;

internal static class StaticDashboardGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly HashSet<string> RequiredTables = new(StringComparer.Ordinal)
    {
        "dashboard_run_summary",
        "dashboard_operation_summary",
        "dashboard_candidate_summary",
        "dashboard_collection_health",
    };

    private static readonly HashSet<string> RiskyPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "prompt",
        "prompt.content",
        "promptcontent",
        "prompt.text",
        "prompttext",
        "input",
        "input.content",
        "inputcontent",
        "input.message",
        "inputmessage",
        "response",
        "response.content",
        "responsecontent",
        "response.text",
        "responsetext",
        "output",
        "output.content",
        "outputcontent",
        "completion",
        "completion.content",
        "completioncontent",
        "message",
        "messages",
        "content",
        "system_prompt",
        "systemprompt",
        "tool_arguments",
        "tool.arguments",
        "toolarguments",
        "tool_args",
        "toolargs",
        "arguments",
        "tool_results",
        "tool.results",
        "toolresults",
        "tool_result",
        "toolresult",
        "result",
        "results",
        "source_code",
        "sourcecode",
        "source.fragment",
        "sourcefragment",
        "code",
        "file_contents",
        "filecontents",
        "file_content",
        "filecontent",
        "file_path",
        "filepath",
        "local_path",
        "localpath",
        "credential",
        "credentials",
        "secret",
        "secrets",
        "secret_key",
        "secretkey",
        "api_key",
        "apikey",
        "password",
        "token",
        "access_token",
        "accesstoken",
        "authorization",
        "authorization.header",
        "authorization_header",
        "authorizationheader",
        "sensitive_bundle_path",
        "sensitive_bundle_content",
        "sensitivebundlepath",
        "sensitivebundlecontent",
        "sensitive_content",
        "sensitivecontent",
        "raw_content",
        "rawcontent",
    };

    private static readonly Regex RiskyStringPattern = new(
        "(Authorization\\s*[:=]\\s*Basic\\s+\\S+|Basic\\s+[A-Za-z0-9+/=]{12,}|Bearer\\s+\\S+|x-langfuse-ingestion-version|api[_-]?key\\s*=|password\\s*=|secret[_-]?key\\s*=|access[_-]?token\\s*=|-----BEGIN\\s+(?:RSA\\s+)?PRIVATE\\s+KEY-----|sensitive[-_]?bundle)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static StaticDashboardArtifact Generate(string datasetJson, string title, string snapshotDate)
    {
        var sanitized = SanitizeDataset(datasetJson);
        var html = RenderHtml(title, snapshotDate);
        return new StaticDashboardArtifact(html, sanitized);
    }

    private static string SanitizeDataset(string datasetJson)
    {
        var node = JsonNode.Parse(datasetJson) ?? throw new InvalidDataException("dashboard dataset JSON must contain an object root.");
        if (node is not JsonObject root)
        {
            throw new InvalidDataException("dashboard dataset JSON must contain an object root.");
        }

        foreach (var requiredTable in RequiredTables)
        {
            if (!root.TryGetPropertyValue(requiredTable, out var table) || table is not JsonArray)
            {
                throw new InvalidDataException($"dashboard dataset JSON must contain array property '{requiredTable}'.");
            }
        }

        var sanitized = SanitizeNode(root) ?? new JsonObject();
        return sanitized.ToJsonString(JsonOptions);
    }

    private static JsonNode? SanitizeNode(JsonNode? node)
    {
        return node switch
        {
            JsonObject jsonObject => SanitizeObject(jsonObject),
            JsonArray jsonArray => SanitizeArray(jsonArray),
            JsonValue jsonValue => SanitizeValue(jsonValue),
            _ => null,
        };
    }

    private static JsonObject SanitizeObject(JsonObject jsonObject)
    {
        var sanitized = new JsonObject();
        foreach (var pair in jsonObject)
        {
            if (IsRiskyProperty(pair.Key))
            {
                continue;
            }

            sanitized[pair.Key] = SanitizeNode(pair.Value);
        }

        return sanitized;
    }

    private static JsonArray SanitizeArray(JsonArray jsonArray)
    {
        var sanitized = new JsonArray();
        foreach (var item in jsonArray)
        {
            sanitized.Add(SanitizeNode(item));
        }

        return sanitized;
    }

    private static JsonNode? SanitizeValue(JsonValue jsonValue)
    {
        if (jsonValue.TryGetValue<string>(out var value)
            && RiskyStringPattern.IsMatch(value))
        {
            return null;
        }

        return JsonNode.Parse(jsonValue.ToJsonString());
    }

    private static bool IsRiskyProperty(string propertyName)
    {
        return RiskyPropertyNames.Contains(propertyName)
            || RiskyPropertyNames.Contains(NormalizePropertyName(propertyName));
    }

    private static string NormalizePropertyName(string propertyName)
    {
        var builder = new StringBuilder(propertyName.Length);
        foreach (var character in propertyName)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string RenderHtml(string title, string snapshotDate)
    {
        var encodedTitle = System.Net.WebUtility.HtmlEncode(title);
        var encodedSnapshotDate = System.Net.WebUtility.HtmlEncode(snapshotDate);
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{encodedTitle}}</title>
              <style>
                :root {
                  color-scheme: light;
                  --bg: #f7f8fa;
                  --panel: #ffffff;
                  --text: #17202a;
                  --muted: #5e6a75;
                  --line: #d8dee6;
                  --accent: #256f5b;
                  --accent-2: #8a5a12;
                  --danger: #b42318;
                }
                * { box-sizing: border-box; }
                body {
                  margin: 0;
                  font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
                  background: var(--bg);
                  color: var(--text);
                }
                header {
                  padding: 24px 28px 12px;
                  border-bottom: 1px solid var(--line);
                  background: var(--panel);
                }
                h1 {
                  margin: 0 0 6px;
                  font-size: 24px;
                  line-height: 1.2;
                  letter-spacing: 0;
                }
                h2 {
                  margin: 0 0 12px;
                  font-size: 17px;
                  letter-spacing: 0;
                }
                main {
                  padding: 18px 28px 32px;
                }
                .meta {
                  color: var(--muted);
                  font-size: 13px;
                }
                .filters {
                  display: grid;
                  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
                  gap: 10px;
                  margin: 0 0 18px;
                  padding: 14px;
                  background: var(--panel);
                  border: 1px solid var(--line);
                  border-radius: 8px;
                }
                label {
                  display: grid;
                  gap: 5px;
                  color: var(--muted);
                  font-size: 12px;
                }
                select,
                input {
                  min-width: 0;
                  width: 100%;
                  padding: 8px 9px;
                  border: 1px solid var(--line);
                  border-radius: 6px;
                  background: #ffffff;
                  color: var(--text);
                  font: inherit;
                  font-size: 13px;
                }
                .metrics {
                  display: grid;
                  grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
                  gap: 10px;
                  margin-bottom: 18px;
                }
                .metric {
                  padding: 12px;
                  background: var(--panel);
                  border: 1px solid var(--line);
                  border-radius: 8px;
                }
                .metric span {
                  display: block;
                  color: var(--muted);
                  font-size: 12px;
                }
                .metric strong {
                  display: block;
                  margin-top: 5px;
                  font-size: 22px;
                  line-height: 1.15;
                }
                section {
                  margin-top: 18px;
                  padding: 14px;
                  background: var(--panel);
                  border: 1px solid var(--line);
                  border-radius: 8px;
                }
                .table-wrap {
                  overflow-x: auto;
                  border: 1px solid var(--line);
                  border-radius: 6px;
                }
                table {
                  width: 100%;
                  border-collapse: collapse;
                  min-width: 760px;
                  font-size: 13px;
                }
                th,
                td {
                  padding: 8px 9px;
                  border-bottom: 1px solid var(--line);
                  text-align: left;
                  vertical-align: top;
                  white-space: nowrap;
                }
                th {
                  position: sticky;
                  top: 0;
                  background: #eef2f5;
                  cursor: pointer;
                  color: #25313d;
                }
                tr:last-child td { border-bottom: 0; }
                .status-error { color: var(--danger); font-weight: 600; }
                .status-success { color: var(--accent); font-weight: 600; }
                .placeholder {
                  color: var(--muted);
                  margin: 0;
                  line-height: 1.45;
                }
                @media (max-width: 720px) {
                  header,
                  main { padding-left: 14px; padding-right: 14px; }
                  table { font-size: 12px; }
                }
              </style>
            </head>
            <body>
              <header>
                <h1>{{encodedTitle}}</h1>
                <div class="meta">Snapshot: <span id="snapshot-date">{{encodedSnapshotDate}}</span> · Dataset: <span id="dataset-meta">loading</span></div>
              </header>
              <main>
                <div class="filters" aria-label="Dashboard filters">
                  <label>Date<select id="filter-date"></select></label>
                  <label>User<select id="filter-user"></select></label>
                  <label>Client<select id="filter-client"></select></label>
                  <label>Experiment<select id="filter-experiment"></select></label>
                  <label>Variant<select id="filter-variant"></select></label>
                  <label>Status<select id="filter-status"></select></label>
                  <label>Search<input id="filter-search" type="search" placeholder="trace, task, user, candidate"></label>
                </div>
                <div class="metrics" id="metrics"></div>
                <section>
                  <h2>Run Overview</h2>
                  <div class="table-wrap"><table id="run-table"></table></div>
                </section>
                <section>
                  <h2>Agent / Tool Behavior</h2>
                  <div class="table-wrap"><table id="operation-table"></table></div>
                </section>
                <section>
                  <h2>Prompt / Skill / Instructions</h2>
                  <div class="table-wrap"><table id="prompt-table"></table></div>
                </section>
                <section>
                  <h2>Baseline vs Variant</h2>
                  <div class="table-wrap"><table id="variant-table"></table></div>
                </section>
                <section>
                  <h2>Diagnosis / Improvement Loop</h2>
                  <div class="table-wrap"><table id="candidate-table"></table></div>
                </section>
                <section>
                  <h2>Collection Health</h2>
                  <div class="table-wrap"><table id="health-table"></table></div>
                </section>
                <section>
                  <h2>Outcome Linkage Candidate</h2>
                  <p class="placeholder">Placeholder for sanitized manual outcome references. External API ingestion and identity mapping are outside this dashboard artifact.</p>
                </section>
              </main>
              <script>
                const state = { data: null, sort: new Map() };
                const tableColumns = {
                  run: ["time_bucket_start_utc", "user_email", "client_kind", "experiment_id", "agent_variant", "status", "duration_ms", "ttft_ms", "total_tokens", "tool_call_count", "error_count", "trace_id"],
                  operation: ["user_email", "client_kind", "operation_kind", "tool_name", "status", "call_count", "error_count", "total_duration_ms", "retry_count", "permission_result", "trace_id"],
                  prompt: ["prompt_version", "skill_version", "agent_variant", "total_tokens", "turn_count", "tool_call_count", "error_count", "trace_id"],
                  variant: ["task_id", "repo_snapshot", "client_kind", "experiment_condition", "agent_variant", "success_status", "duration_ms", "ttft_ms", "estimated_cost", "trace_id"],
                  candidate: ["candidate_kind", "user_email", "candidate_severity", "candidate_rule", "proposed_change_kind", "candidate_status", "decision_status", "evidence_ref", "trace_id"],
                  health: ["health_check_kind", "health_status", "missing_attribute_name", "unknown_span_count", "unknown_attribute_count", "mapping_failure_count", "trace_id"]
                };

                fetch("dashboard-data.json")
                  .then(response => {
                    if (!response.ok) throw new Error("Failed to load dashboard-data.json");
                    return response.json();
                  })
                  .then(data => {
                    state.data = data;
                    document.getElementById("dataset-meta").textContent = `${data.schema_version || "unknown"} · generated ${data.generated_at_utc || "unknown"}`;
                    hydrateFilters(data);
                    render();
                  })
                  .catch(error => {
                    document.getElementById("dataset-meta").textContent = error.message;
                  });

                for (const id of ["filter-date", "filter-user", "filter-client", "filter-experiment", "filter-variant", "filter-status"]) {
                  document.getElementById(id).addEventListener("change", render);
                }
                document.getElementById("filter-search").addEventListener("input", render);

                function hydrateFilters(data) {
                  const rows = data.dashboard_run_summary || [];
                  setOptions("filter-date", rows.map(row => day(row.time_bucket_start_utc)));
                  setOptions("filter-user", rows.flatMap(row => [row.user_email, row.user_id]).filter(Boolean));
                  setOptions("filter-client", rows.map(row => row.client_kind));
                  setOptions("filter-experiment", rows.map(row => row.experiment_id));
                  setOptions("filter-variant", rows.map(row => row.agent_variant));
                  setOptions("filter-status", rows.map(row => row.status));
                }

                function setOptions(id, values) {
                  const select = document.getElementById(id);
                  const unique = [...new Set(values.filter(Boolean))].sort();
                  select.replaceChildren(option("All", ""));
                  for (const value of unique) select.append(option(value, value));
                }

                function option(label, value) {
                  const element = document.createElement("option");
                  element.textContent = label;
                  element.value = value;
                  return element;
                }

                function filteredRuns() {
                  const rows = state.data.dashboard_run_summary || [];
                  const filters = {
                    date: document.getElementById("filter-date").value,
                    user: document.getElementById("filter-user").value,
                    client: document.getElementById("filter-client").value,
                    experiment: document.getElementById("filter-experiment").value,
                    variant: document.getElementById("filter-variant").value,
                    status: document.getElementById("filter-status").value,
                    search: document.getElementById("filter-search").value.trim().toLowerCase()
                  };
                  return rows.filter(row =>
                    (!filters.date || day(row.time_bucket_start_utc) === filters.date)
                    && (!filters.user || row.user_email === filters.user || row.user_id === filters.user)
                    && (!filters.client || row.client_kind === filters.client)
                    && (!filters.experiment || row.experiment_id === filters.experiment)
                    && (!filters.variant || row.agent_variant === filters.variant)
                    && (!filters.status || row.status === filters.status)
                    && (!filters.search || JSON.stringify(row).toLowerCase().includes(filters.search)));
                }

                function relatedRows(rows, source) {
                  const traces = new Set(rows.map(row => row.trace_id).filter(Boolean));
                  return (source || []).filter(row => !row.trace_id || traces.has(row.trace_id));
                }

                function render() {
                  if (!state.data) return;
                  const runs = filteredRuns();
                  renderMetrics(runs);
                  renderTable("run-table", "run", runs);
                  renderTable("operation-table", "operation", relatedRows(runs, state.data.dashboard_operation_summary));
                  renderTable("prompt-table", "prompt", runs);
                  renderTable("variant-table", "variant", runs);
                  renderTable("candidate-table", "candidate", relatedRows(runs, state.data.dashboard_candidate_summary));
                  renderTable("health-table", "health", relatedRows(runs, state.data.dashboard_collection_health));
                }

                function renderMetrics(rows) {
                  const totalTokens = sum(rows, "total_tokens");
                  const toolCalls = sum(rows, "tool_call_count");
                  const errors = sum(rows, "error_count");
                  const p95 = percentile(rows.map(row => row.duration_ms).filter(Number.isFinite).sort((a, b) => a - b), 0.95);
                  const metrics = [
                    ["Traces", rows.length],
                    ["Errors", errors],
                    ["Tool Calls", toolCalls],
                    ["Total Tokens", totalTokens],
                    ["p95 Duration", p95 == null ? "" : `${p95} ms`]
                  ];
                  document.getElementById("metrics").replaceChildren(...metrics.map(([label, value]) => {
                    const item = document.createElement("div");
                    item.className = "metric";
                    item.innerHTML = `<span>${escapeHtml(label)}</span><strong>${escapeHtml(String(value))}</strong>`;
                    return item;
                  }));
                }

                function renderTable(id, key, rows) {
                  const table = document.getElementById(id);
                  const columns = tableColumns[key];
                  const sorted = sortRows(key, rows);
                  const thead = document.createElement("thead");
                  const header = document.createElement("tr");
                  for (const column of columns) {
                    const th = document.createElement("th");
                    th.textContent = column;
                    th.addEventListener("click", () => {
                      const current = state.sort.get(key);
                      state.sort.set(key, { column, desc: current?.column === column ? !current.desc : false });
                      render();
                    });
                    header.append(th);
                  }
                  thead.append(header);
                  const tbody = document.createElement("tbody");
                  for (const row of sorted) {
                    const tr = document.createElement("tr");
                    for (const column of columns) {
                      const td = document.createElement("td");
                      const value = row[column] ?? "";
                      td.textContent = value;
                      if (column === "status") td.className = `status-${value}`;
                      tr.append(td);
                    }
                    tbody.append(tr);
                  }
                  table.replaceChildren(thead, tbody);
                }

                function sortRows(key, rows) {
                  const setting = state.sort.get(key);
                  if (!setting) return rows;
                  return [...rows].sort((left, right) => {
                    const a = left[setting.column] ?? "";
                    const b = right[setting.column] ?? "";
                    const result = typeof a === "number" && typeof b === "number"
                      ? a - b
                      : String(a).localeCompare(String(b));
                    return setting.desc ? -result : result;
                  });
                }

                function sum(rows, property) {
                  return rows.reduce((total, row) => total + (Number(row[property]) || 0), 0);
                }

                function percentile(values, p) {
                  if (!values.length) return null;
                  return values[Math.max(0, Math.ceil(values.length * p) - 1)];
                }

                function day(value) {
                  return value ? String(value).slice(0, 10) : "";
                }

                function escapeHtml(value) {
                  return value.replace(/[&<>"']/g, char => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[char]));
                }
              </script>
            </body>
            </html>
            """;
    }
}

internal sealed record StaticDashboardArtifact(
    string Html,
    string DatasetJson);
