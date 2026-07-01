// Local Ingestion Monitor — view interactions (Sprint10 A3 / M3 / M4 / M5).
//
// Vanilla JS, no external libraries. Presentation only: this script reads
// sanitized monitor JSON and already-rendered DOM. It never fetches a raw-bearing
// route and never inserts payload markup. Event delegation keeps it inert on pages
// without the relevant controls (Overview / Ingestions / Diagnostics), so it is
// safe to load globally.
(() => {
    "use strict";

    // ── TraceDetail tab shell ───────────────────────────────────────────────
    function activateTab(tab) {
        const list = tab.closest(".tabs");
        if (!list) {
            return;
        }

        for (const sibling of list.querySelectorAll(".tab")) {
            const panel = document.getElementById(sibling.getAttribute("aria-controls"));
            const selected = sibling === tab;
            sibling.setAttribute("aria-selected", selected ? "true" : "false");
            sibling.tabIndex = selected ? 0 : -1;
            if (panel) {
                panel.hidden = !selected;
            }
        }
    }

    function onTabKeydown(event) {
        const tab = event.target.closest(".tab");
        if (!tab || (event.key !== "ArrowLeft" && event.key !== "ArrowRight")) {
            return;
        }

        const tabs = [...tab.closest(".tabs").querySelectorAll(".tab")];
        const delta = event.key === "ArrowRight" ? 1 : -1;
        const next = tabs[(tabs.indexOf(tab) + delta + tabs.length) % tabs.length];
        event.preventDefault();
        activateTab(next);
        next.focus();
    }

    // ── Span API fetch ─────────────────────────────────────────────────────
    async function fetchAllSpans(traceId) {
        const spans = [];
        let after = 0;

        while (true) {
            const response = await fetch(`/api/monitor/traces/${encodeURIComponent(traceId)}/spans?after=${after}&limit=200`, {
                cache: "no-store",
            });
            if (!response.ok) {
                throw new Error(`span api returned ${response.status}`);
            }

            const page = await response.json();
            spans.push(...page.items);
            if (page.next_cursor === null || page.next_cursor === undefined) {
                return spans;
            }

            after = page.next_cursor;
        }
    }

    // ── Formatting helpers ─────────────────────────────────────────────────
    function formatTokensNumber(n) {
        if (n === null || n === undefined) {
            return "—";
        }
        return new Intl.NumberFormat("en-US").format(n);
    }

    function formatDuration(ms) {
        if (ms === null || ms === undefined) {
            return "—";
        }
        if (ms < 1000) {
            return `${ms} ms`;
        }
        if (ms < 60000) {
            return `${(ms / 1000).toFixed(1)} 秒`;
        }
        const minutes = Math.floor(ms / 60000);
        const seconds = Math.round((ms % 60000) / 1000);
        return `${minutes}分 ${seconds}秒`;
    }

    // ── Shared helpers ─────────────────────────────────────────────────────
    function categoryClass(category) {
        switch (category) {
            case "agent_invocation":
                return "category-agent";
            case "llm_call":
                return "category-llm";
            case "tool_call":
                return "category-tool";
            case "hook":
                return "category-hook";
            default:
                return "category-unknown";
        }
    }

    function categoryIcon(category) {
        switch (category) {
            case "agent_invocation":
                return "agent";
            case "llm_call":
                return "llm";
            case "tool_call":
                return "tool";
            case "hook":
                return "hook";
            default:
                return "span";
        }
    }

    function spanOperationLabel(span) {
        return span.tool_name || span.mcp_tool_name || span.agent_name || span.operation || span.category || "span";
    }

    function formatModel(span) {
        const m = span.response_model || span.request_model;
        return m || "—";
    }

    function isErrorSpan(span) {
        return span.status === "error" || Boolean(span.error_type);
    }

    function highlightTimelineRow(spanRowId) {
        const row = document.querySelector(`[data-span-row-id="${spanRowId}"]`);
        if (!row) {
            return;
        }

        for (const highlighted of document.querySelectorAll(".span-highlight")) {
            highlighted.classList.remove("span-highlight");
        }

        row.classList.add("span-highlight");
        const timelineTab = document.getElementById("tab-timeline");
        if (timelineTab) {
            activateTab(timelineTab);
        }

        row.scrollIntoView({ behavior: "smooth", block: "center" });
    }

    // ── Span tree build ────────────────────────────────────────────────────
    function buildParentChildMap(spans) {
        const bySpanId = new Map();
        for (const span of spans) {
            if (span.span_id) {
                bySpanId.set(span.span_id, span);
            }
        }

        const childrenOf = new Map();
        const roots = [];

        for (const span of spans) {
            const parent = span.parent_span_id ? bySpanId.get(span.parent_span_id) : null;
            if (parent) {
                if (!childrenOf.has(parent.span_id)) {
                    childrenOf.set(parent.span_id, []);
                }
                childrenOf.get(parent.span_id).push(span);
            } else {
                roots.push(span);
            }
        }

        return { bySpanId, childrenOf, roots };
    }

    function compareSpanOrder(a, b) {
        if (a.start_time && b.start_time && a.start_time !== b.start_time) {
            return String(a.start_time).localeCompare(String(b.start_time));
        }
        if (a.start_time && !b.start_time) return -1;
        if (!a.start_time && b.start_time) return 1;
        const ordinal = (a.span_ordinal ?? 0) - (b.span_ordinal ?? 0);
        return ordinal !== 0 ? ordinal : (a.id ?? 0) - (b.id ?? 0);
    }

    function computeWaterfallRange(spans) {
        let minMs = Infinity;
        let maxMs = -Infinity;

        for (const span of spans) {
            if (span.start_time) {
                const t = Date.parse(span.start_time);
                if (!Number.isNaN(t)) {
                    minMs = Math.min(minMs, t);
                }
            }
            if (span.end_time) {
                const t = Date.parse(span.end_time);
                if (!Number.isNaN(t)) {
                    maxMs = Math.max(maxMs, t);
                }
            }
        }

        const valid = minMs !== Infinity && maxMs !== -Infinity && maxMs > minMs;
        return valid ? { minMs, totalMs: maxMs - minMs } : null;
    }

    function makeWaterfallCell(span, range) {
        const cell = document.createElement("td");
        cell.className = "waterfall-cell";

        const track = document.createElement("div");
        track.className = "waterfall-track";

        const bar = document.createElement("div");
        bar.className = "waterfall-bar " + categoryClass(span.category);

        if (range && span.start_time) {
            const startMs = Date.parse(span.start_time);
            const endMs = span.end_time ? Date.parse(span.end_time) : startMs + (span.duration_ms ?? 0);
            if (!Number.isNaN(startMs) && !Number.isNaN(endMs) && endMs >= startMs) {
                const leftPct = ((startMs - range.minMs) / range.totalMs) * 100;
                const widthPct = Math.max(0.5, ((endMs - startMs) / range.totalMs) * 100);
                bar.style.left = `${leftPct.toFixed(2)}%`;
                bar.style.width = `${widthPct.toFixed(2)}%`;
            } else {
                bar.style.left = "0%";
                bar.style.width = "2%";
            }
        } else {
            bar.style.left = "0%";
            bar.style.width = "2%";
        }

        track.appendChild(bar);
        cell.appendChild(track);
        return cell;
    }

    function makeSpanRow(span, depth, hasChildren, isExpanded, range, toggleCallback) {
        const row = document.createElement("tr");
        row.dataset.spanRowId = String(span.id);
        row.dataset.spanId = span.span_id || "";
        row.dataset.depth = String(depth);
        if (!isExpanded && depth > 0) {
            // rows themselves are shown/hidden by parent collapse state; start visible
        }

        // Name cell with indentation + connectors
        const nameCell = document.createElement("td");
        nameCell.className = "span-name-cell";

        const inner = document.createElement("div");
        inner.className = "span-name-inner";

        // Leading spacer for depth=0 (no parent connector)
        if (depth === 0) {
            const spacer = document.createElement("span");
            spacer.style.width = "14px";
            spacer.style.flex = "none";
            inner.appendChild(spacer);
        } else {
            // For each ancestor level add a connector segment
            for (let d = 1; d <= depth; d++) {
                const connector = document.createElement("span");
                connector.className = "span-connector";
                if (d < depth) {
                    // vertical continuation line only
                    const v = document.createElement("span");
                    v.className = "span-connector-v";
                    connector.appendChild(v);
                } else {
                    // last level: vertical + horizontal elbow
                    const v = document.createElement("span");
                    v.className = "span-connector-v";
                    const h = document.createElement("span");
                    h.className = "span-connector-h";
                    connector.appendChild(v);
                    connector.appendChild(h);
                }
                inner.appendChild(connector);
            }
        }

        const label = document.createElement("span");
        label.className = "span-label";

        // Toggle button
        const toggle = document.createElement("span");
        toggle.className = "span-toggle";
        if (hasChildren) {
            toggle.textContent = isExpanded ? "▾" : "▸"; // ▾ / ▸
            toggle.style.cursor = "pointer";
            toggle.addEventListener("click", (e) => {
                e.stopPropagation();
                toggleCallback(row, toggle);
            });
        } else {
            toggle.textContent = "·"; // ·
        }
        label.appendChild(toggle);

        // Category icon
        const icon = document.createElement("span");
        icon.textContent = categoryIcon(span.category);
        label.appendChild(icon);

        // Operation name
        const opName = document.createElement("span");
        opName.className = "span-mono";
        opName.textContent = spanOperationLabel(span);
        label.appendChild(opName);

        inner.appendChild(label);
        nameCell.appendChild(inner);
        row.appendChild(nameCell);

        // Model
        const modelCell = document.createElement("td");
        modelCell.className = "span-model";
        modelCell.textContent = formatModel(span);
        row.appendChild(modelCell);

        // Tokens
        const tokensCell = document.createElement("td");
        tokensCell.className = "span-num";
        tokensCell.textContent = formatTokensNumber(span.total_tokens);
        row.appendChild(tokensCell);

        // Duration
        const durationCell = document.createElement("td");
        durationCell.className = "span-num";
        durationCell.textContent = formatDuration(span.duration_ms);
        row.appendChild(durationCell);

        // Status
        const statusCell = document.createElement("td");
        statusCell.className = "span-status";
        const badge = document.createElement("span");
        const hasError = isErrorSpan(span);
        badge.className = "status-badge " + (hasError ? "status-error" : "status-ok");
        badge.textContent = hasError ? (span.error_type || span.status || "error") : (span.status || "ok");
        statusCell.appendChild(badge);
        row.appendChild(statusCell);

        // Waterfall
        row.appendChild(makeWaterfallCell(span, range));

        // Click to highlight timeline row
        row.addEventListener("click", () => {
            highlightTimelineRow(span.id);
        });

        return row;
    }

    function renderSpanTree(spans, _traceId) {
        const container = document.getElementById("spantree-view");
        if (!container) {
            return;
        }

        const { childrenOf, roots } = buildParentChildMap(spans);
        const range = computeWaterfallRange(spans);

        const table = document.createElement("table");
        table.className = "span-table";

        const thead = document.createElement("thead");
        const headRow = document.createElement("tr");
        for (const [label, extra] of [
            ["スパン", ""], // スパン
            ["モデル", ""], // モデル
            ["トークン", "text-align:right"], // トークン
            ["所要時間", "text-align:right"], // 所要時間
            ["状態", ""], // 状態
            ["タイムライン", "min-width:240px"], // タイムライン
        ]) {
            const th = document.createElement("th");
            th.textContent = label;
            if (extra) {
                th.setAttribute("style", extra);
            }
            headRow.appendChild(th);
        }
        thead.appendChild(headRow);
        table.appendChild(thead);

        const tbody = document.createElement("tbody");

        // Collapse state: map from span_id -> Set of descendant row elements
        const descendantsOf = new Map();

        // DFS in start_time order
        function dfs(span, depth) {
            const children = (childrenOf.get(span.span_id) || []).slice().sort(compareSpanOrder);
            const hasChildren = children.length > 0;
            // agent_invocation rows expanded by default; others too (fully expanded initial state)
            const isExpanded = true;

            const makeToggle = (row, toggleEl) => {
                const expanded = row.dataset.expanded !== "false";
                const nowCollapsed = expanded;
                row.dataset.expanded = nowCollapsed ? "false" : "true";
                toggleEl.textContent = nowCollapsed ? "▸" : "▾";

                // Gather all descendant rows
                const desc = descendantsOf.get(span.span_id) || [];
                for (const descRow of desc) {
                    descRow.hidden = nowCollapsed;
                }
            };

            const row = makeSpanRow(span, depth, hasChildren, isExpanded, range, makeToggle);
            row.dataset.expanded = "true";
            tbody.appendChild(row);

            const allDescendants = [];
            for (const child of children) {
                const childRow = dfs(child, depth + 1);
                if (childRow) {
                    allDescendants.push(childRow);
                    // Also gather child's descendants
                    const grandDesc = descendantsOf.get(child.span_id) || [];
                    allDescendants.push(...grandDesc);
                }
            }
            descendantsOf.set(span.span_id, allDescendants);

            return row;
        }

        const sortedRoots = roots.slice().sort(compareSpanOrder);
        for (const root of sortedRoots) {
            dfs(root, 0);
        }

        table.appendChild(tbody);
        container.replaceChildren(table);
    }

    // ── Flow view ──────────────────────────────────────────────────────────
    function renderFlowView(spans, _traceId) {
        const container = document.getElementById("flow-view");
        if (!container) {
            return;
        }

        const sorted = spans.slice().sort(compareSpanOrder);
        const wrapper = document.createElement("div");
        wrapper.style.display = "flex";
        wrapper.style.flexDirection = "column";
        wrapper.style.alignItems = "flex-start";
        wrapper.style.padding = "24px 22px";
        wrapper.style.minWidth = "380px";

        for (let i = 0; i < sorted.length; i++) {
            const span = sorted[i];

            const node = document.createElement("div");
            node.className = "flow-node " + categoryClass(span.category);

            const topLabel = document.createElement("div");
            topLabel.className = "flow-node-toplabel";
            topLabel.textContent = categoryIcon(span.category) + " " + (span.category || "unknown");
            node.appendChild(topLabel);

            const title = document.createElement("div");
            title.className = "flow-node-title";
            title.textContent = spanOperationLabel(span);
            node.appendChild(title);

            const sub = document.createElement("div");
            sub.className = "flow-node-sub";
            const subParts = [];
            const model = formatModel(span);
            if (model !== "—") {
                subParts.push(model);
            }
            subParts.push(formatDuration(span.duration_ms));
            if (isErrorSpan(span)) {
                subParts.push(span.error_type || "error");
            }
            sub.textContent = subParts.join(" · ");
            node.appendChild(sub);

            wrapper.appendChild(node);

            if (i < sorted.length - 1) {
                const connector = document.createElement("div");
                connector.className = "flow-connector-line";
                wrapper.appendChild(connector);
            }
        }

        container.replaceChildren(wrapper);
    }

    // ── Tree & Flow driver ─────────────────────────────────────────────────
    async function renderTreeAndFlow() {
        const spantreeView = document.getElementById("spantree-view");
        if (!spantreeView) {
            return;
        }

        const traceId = spantreeView.dataset.spantreeTraceId;
        const status = document.getElementById("spantree-status");

        if (!traceId) {
            if (status) {
                status.textContent = "Trace id を取得できません。";
            }
            return;
        }

        // Wire view toggle buttons
        const treeBtn = document.getElementById("view-tree-btn");
        const flowBtn = document.getElementById("view-flow-btn");
        const flowView = document.getElementById("flow-view");

        function showTree() {
            spantreeView.hidden = false;
            if (flowView) flowView.hidden = true;
            if (treeBtn) treeBtn.classList.add("active");
            if (flowBtn) flowBtn.classList.remove("active");
        }

        function showFlow() {
            spantreeView.hidden = true;
            if (flowView) flowView.hidden = false;
            if (treeBtn) treeBtn.classList.remove("active");
            if (flowBtn) flowBtn.classList.add("active");
        }

        if (treeBtn) {
            treeBtn.addEventListener("click", showTree);
        }
        if (flowBtn) {
            flowBtn.addEventListener("click", showFlow);
        }

        try {
            const spans = await fetchAllSpans(traceId);

            if (spans.length === 0) {
                if (status) {
                    status.textContent = "このトレースにスパンがありません。";
                }
                return;
            }

            if (status) {
                status.textContent = `${spans.length} スパン`;
            }

            renderSpanTree(spans, traceId);
            renderFlowView(spans, traceId);
        } catch {
            if (status) {
                status.textContent = "スパンツリーを読み込めませんでした。";
            }
        }
    }

    // ── Timeline helpers ───────────────────────────────────────────────────
    function textOrDash(value) {
        return value === null || value === undefined || value === "" ? "-" : String(value);
    }

    function formatSpanSubject(span) {
        const parts = [span.tool_name, span.mcp_tool_name, span.agent_name].filter(Boolean);
        return parts.length > 0 ? parts.join(" / ") : "-";
    }

    function formatModelTimeline(span) {
        return textOrDash(span.response_model || span.request_model);
    }

    function formatTokens(span) {
        return `${textOrDash(span.input_tokens)} / ${textOrDash(span.output_tokens)} / ${textOrDash(span.total_tokens)}`;
    }

    function formatStatus(span) {
        const status = textOrDash(span.status);
        return span.error_type ? `${status} (${span.error_type})` : status;
    }

    function appendCell(row, text, className) {
        const cell = document.createElement("td");
        if (className) {
            cell.className = className;
        }

        cell.textContent = text;
        row.appendChild(cell);
    }

    function compareTimelineTime(a, b) {
        if (a.start_time && b.start_time && a.start_time !== b.start_time) {
            return String(a.start_time).localeCompare(String(b.start_time));
        }

        if (a.start_time && !b.start_time) {
            return -1;
        }

        if (!a.start_time && b.start_time) {
            return 1;
        }

        const ordinal = (a.span_ordinal ?? 0) - (b.span_ordinal ?? 0);
        return ordinal !== 0 ? ordinal : (a.id ?? 0) - (b.id ?? 0);
    }

    function compareTimelineTokens(a, b) {
        const tokens = (b.total_tokens ?? 0) - (a.total_tokens ?? 0);
        return tokens !== 0 ? tokens : compareTimelineTime(a, b);
    }

    function selectedTimelineSort() {
        const selected = document.querySelector('input[name="timeline-sort"]:checked');
        return selected && selected.value === "tokens" ? "tokens" : "time";
    }

    function renderTimelineRows(spans) {
        const rows = document.getElementById("timeline-rows");
        if (!rows) {
            return;
        }

        const errorsOnly = document.getElementById("timeline-errors-only");
        const count = document.getElementById("timeline-count");
        const empty = document.getElementById("timeline-empty");
        const filtered = (errorsOnly && errorsOnly.checked ? spans.filter(isErrorSpan) : [...spans])
            .sort(selectedTimelineSort() === "tokens" ? compareTimelineTokens : compareTimelineTime);

        if (count) {
            count.textContent = `${filtered.length} / ${spans.length} スパン`;
        }

        if (empty) {
            empty.hidden = filtered.length > 0;
        }

        const fragment = document.createDocumentFragment();
        for (const span of filtered) {
            const row = document.createElement("tr");
            row.dataset.spanRowId = String(span.id);
            appendCell(row, textOrDash(span.operation));
            appendCell(row, textOrDash(span.category));
            appendCell(row, formatSpanSubject(span));
            appendCell(row, formatModelTimeline(span));
            appendCell(row, formatTokens(span));
            appendCell(row, formatStatus(span), isErrorSpan(span) ? "status-error" : "status-ok");
            appendCell(row, textOrDash(span.duration_ms));
            fragment.appendChild(row);
        }

        rows.replaceChildren(fragment);
    }

    async function renderTimeline() {
        const rows = document.getElementById("timeline-rows");
        if (!rows) {
            return;
        }

        const traceId = rows.dataset.timelineTraceId;
        const count = document.getElementById("timeline-count");
        if (!traceId) {
            if (count) {
                count.textContent = "Trace id を取得できません。";
            }
            return;
        }

        try {
            const spans = await fetchAllSpans(traceId);
            const refresh = () => renderTimelineRows(spans);
            const errorsOnly = document.getElementById("timeline-errors-only");
            if (errorsOnly) {
                errorsOnly.addEventListener("change", refresh);
            }

            for (const sort of document.querySelectorAll('input[name="timeline-sort"]')) {
                sort.addEventListener("change", refresh);
            }

            refresh();
        } catch {
            if (count) {
                count.textContent = "タイムラインを読み込めませんでした。";
            }
        }
    }

    // ── Cache Explorer ─────────────────────────────────────────────────────
    function isChatTurn(span) {
        return span.operation === "chat" || span.category === "llm_call";
    }

    function isInvokeAgent(span) {
        return span.operation === "invoke_agent" || span.category === "agent_invocation";
    }

    function numberOrZero(value) {
        return typeof value === "number" && Number.isFinite(value) ? value : 0;
    }

    function percentOrDash(numerator, denominator) {
        if (!denominator) {
            return "n/a";
        }

        return `${Math.round((numberOrZero(numerator) / denominator) * 100)}%`;
    }

    function formatTime(value) {
        return textOrDash(value);
    }

    function formatCacheTokens(span) {
        return `${textOrDash(span.cache_read_tokens)} / ${textOrDash(span.cache_creation_tokens)}`;
    }

    function groupRootForTurn(turn, bySpanId) {
        let current = turn;
        let root = null;
        const seen = new Set();

        while (current && current.parent_span_id && !seen.has(current.parent_span_id)) {
            seen.add(current.parent_span_id);
            const parent = bySpanId.get(current.parent_span_id);
            if (!parent) {
                break;
            }

            if (isInvokeAgent(parent)) {
                root = parent;
            }

            current = parent;
        }

        return root;
    }

    function groupLabel(group) {
        if (!group.root) {
            return "Ungrouped trace turns";
        }

        return group.root.agent_name || group.root.operation || group.root.span_id || "invoke_agent";
    }

    function modelLabel(spans) {
        const models = [...new Set(spans.map(formatModelTimeline).filter((model) => model !== "-"))];
        if (models.length === 0) {
            return "-";
        }

        return models.length === 1 ? models[0] : "mixed";
    }

    function sumField(spans, field) {
        return spans.reduce((sum, span) => sum + numberOrZero(span[field]), 0);
    }

    function appendMetric(container, label, value) {
        const item = document.createElement("div");
        item.className = "cache-metric";

        const labelElement = document.createElement("span");
        labelElement.className = "cache-metric-label";
        labelElement.textContent = label;

        const valueElement = document.createElement("strong");
        valueElement.textContent = value;

        item.append(labelElement, valueElement);
        container.appendChild(item);
    }

    function appendCacheTurnRows(body, turns) {
        for (const turn of turns) {
            const row = document.createElement("tr");
            appendCell(row, formatTime(turn.start_time));
            appendCell(row, formatModelTimeline(turn));
            appendCell(row, percentOrDash(turn.cache_read_tokens, turn.input_tokens));
            appendCell(row, formatCacheTokens(turn));
            appendCell(row, formatTokens(turn));
            appendCell(row, textOrDash(turn.reasoning_tokens));
            appendCell(row, textOrDash(turn.duration_ms));
            body.appendChild(row);
        }
    }

    function appendCacheGroup(container, group) {
        const turns = [...group.turns].sort(compareTimelineTime);
        const inputTokens = sumField(turns, "input_tokens");
        const cacheReadTokens = sumField(turns, "cache_read_tokens");
        const cacheCreationTokens = sumField(turns, "cache_creation_tokens");
        const durationMs = group.root?.duration_ms ?? sumField(turns, "duration_ms");

        const article = document.createElement("article");
        article.className = "cache-group";

        const title = document.createElement("h4");
        title.textContent = groupLabel(group);
        article.appendChild(title);

        const meta = document.createElement("p");
        meta.className = "cache-group-meta";
        meta.textContent = group.root
            ? `Root invoke_agent ${textOrDash(group.root.span_id)}; grouped as the trace-local user request approximation.`
            : "No root invoke_agent ancestor was available for these turns.";
        article.appendChild(meta);

        const metrics = document.createElement("div");
        metrics.className = "cache-metrics";
        appendMetric(metrics, "Cache hit rate", percentOrDash(cacheReadTokens, inputTokens));
        appendMetric(metrics, "Cache read / creation", `${cacheReadTokens} / ${cacheCreationTokens}`);
        appendMetric(metrics, "Tokens in / out / total", `${inputTokens} / ${sumField(turns, "output_tokens")} / ${sumField(turns, "total_tokens")}`);
        appendMetric(metrics, "Duration (ms)", textOrDash(durationMs));
        appendMetric(metrics, "Model", modelLabel(turns));
        appendMetric(metrics, "Timestamp", formatTime(group.root?.start_time || turns[0]?.start_time));
        article.appendChild(metrics);

        const tableWrap = document.createElement("div");
        tableWrap.className = "monitor-table-wrapper table-scroll";

        const table = document.createElement("table");
        table.className = "monitor-table cache-table";

        const head = document.createElement("thead");
        const headRow = document.createElement("tr");
        for (const label of ["Timestamp", "Model", "Hit rate", "Cache read / creation", "Tokens (in / out / total)", "Reasoning", "Duration (ms)"]) {
            const cell = document.createElement("th");
            cell.textContent = label;
            headRow.appendChild(cell);
        }

        head.appendChild(headRow);
        table.appendChild(head);

        const body = document.createElement("tbody");
        appendCacheTurnRows(body, turns);
        table.appendChild(body);
        tableWrap.appendChild(table);
        article.appendChild(tableWrap);

        container.appendChild(article);
    }

    function cacheGroupsFromSpans(spans) {
        const bySpanId = new Map();
        for (const span of spans) {
            if (span.span_id) {
                bySpanId.set(span.span_id, span);
            }
        }

        const groups = new Map();
        for (const turn of spans.filter(isChatTurn)) {
            const root = groupRootForTurn(turn, bySpanId);
            const key = root ? `root:${root.id}` : "ungrouped";
            if (!groups.has(key)) {
                groups.set(key, { root, turns: [] });
            }

            groups.get(key).turns.push(turn);
        }

        return [...groups.values()].sort((a, b) => compareTimelineTime(a.root || a.turns[0], b.root || b.turns[0]));
    }

    async function renderCacheExplorer() {
        const groupsContainer = document.getElementById("cache-groups");
        if (!groupsContainer) {
            return;
        }

        const status = document.getElementById("cache-status");
        const traceId = groupsContainer.dataset.cacheTraceId;
        if (!traceId) {
            if (status) {
                status.textContent = "Trace id を取得できません。";
            }
            return;
        }

        try {
            const spans = await fetchAllSpans(traceId);
            const groups = cacheGroupsFromSpans(spans);
            if (status) {
                status.textContent = groups.length === 0
                    ? "このトレースには LLM 呼び出しがないため、キャッシュ指標はありません。"
                    : `${groups.length} request group${groups.length === 1 ? "" : "s"}; ${groups.reduce((sum, group) => sum + group.turns.length, 0)} chat turn${groups.reduce((sum, group) => sum + group.turns.length, 0) === 1 ? "" : "s"}`;
            }

            const fragment = document.createDocumentFragment();
            for (const group of groups) {
                appendCacheGroup(fragment, group);
            }

            groupsContainer.replaceChildren(fragment);
        } catch {
            if (status) {
                status.textContent = "キャッシュを読み込めませんでした。";
            }
        }
    }

    // ── Trace-list progressive disclosure ──────────────────────────────────
    function toggleRow(button) {
        const extra = document.getElementById(button.getAttribute("aria-controls"));
        if (!extra) {
            return;
        }

        const expanded = button.getAttribute("aria-expanded") === "true";
        button.setAttribute("aria-expanded", expanded ? "false" : "true");
        button.textContent = expanded ? "+" : "−"; // minus sign
        extra.hidden = expanded;
    }

    document.addEventListener("click", (event) => {
        const opener = event.target.closest("[data-open-tab]");
        if (opener) {
            const tab = document.getElementById(opener.getAttribute("data-open-tab"));
            if (tab) {
                activateTab(tab);
                tab.focus({ preventScroll: true });
                document.getElementById(tab.getAttribute("aria-controls"))?.scrollIntoView({ behavior: "smooth", block: "start" });
            }
            return;
        }

        const tab = event.target.closest(".tab");
        if (tab) {
            activateTab(tab);
            return;
        }

        const toggle = event.target.closest(".row-toggle");
        if (toggle) {
            toggleRow(toggle);
        }
    });

    document.addEventListener("keydown", onTabKeydown);
    renderTimeline();
    renderTreeAndFlow();
    renderCacheExplorer();
})();
