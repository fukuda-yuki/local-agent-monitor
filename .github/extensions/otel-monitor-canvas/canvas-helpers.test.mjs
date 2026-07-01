// Sprint15 M1 (child A) JS smoke tests for canvas-helpers.mjs.
// Run with `node --test canvas-helpers.test.mjs`. This file imports no Copilot
// SDK, so it runs without a Canvas runtime session (F8 prerequisite).

import { test } from "node:test";
import assert from "node:assert/strict";
import {
    BOUNDARY_NOTE,
    FOCUS_OPTIONS,
    renderHelperHtml,
    formatTraceLine,
    dropdownOptionLabel,
    statusLabel,
    formatTokens,
    formatDuration,
    formatClock,
    shortTraceId,
    buildAnalysisPrompt,
    compactTrace,
    traceDetailSummary,
    summaryTraceLine,
    extractRawPreviewFragment,
    renderRawPreviewHtml,
} from "./canvas-helpers.mjs";

const SAMPLE_TRACE_ROW = {
    trace_id: "abc12345-6789-0000-0000-000000000000",
    client_kind: "vscode",
    error_count: 0,
    span_count: 12,
    tool_call_count: 3,
    total_tokens: 8420,
    duration_ms: 18200,
    primary_model: "gpt-5",
    last_seen_at: "2026-07-01T14:32:07.123Z",
};

test("renderHelperHtml: ready state has Japanese button/heading, Japanese focus labels with unchanged enum values, and no raw fields", () => {
    const html = renderHelperHtml({
        instanceId: "inst-1",
        monitorUrl: "http://127.0.0.1:4320",
        healthState: "ready",
        statusCode: 200,
        healthBody: "{\"status\":\"ready\"}",
        error: null,
        token: "token-1",
    });

    assert.match(html, /Copilotでこのトレースを分析/);
    assert.match(html, /Local Monitor の接続状態/);

    for (const { value, label } of FOCUS_OPTIONS) {
        assert.match(html, new RegExp(`<option value="${value}">${label}</option>`));
    }

    // Sprint15 M5 (D038) intentionally introduces "/raw-preview" (the
    // authorized raw-preview page-navigation link); any OTHER "/raw"
    // reference (e.g. a JSON fetch of the raw-bearing route) is still
    // forbidden here.
    assert.doesNotMatch(html, /\/raw(?!-preview)/);
    assert.doesNotMatch(html, /payload_json/);
    assert.doesNotMatch(html, /console\.log/);
});

test("renderHelperHtml: unreachable state shows the unreachable banner and next-action guidance", () => {
    const html = renderHelperHtml({
        instanceId: "inst-1",
        monitorUrl: "http://127.0.0.1:4320",
        healthState: "unreachable",
        statusCode: null,
        healthBody: null,
        error: "fetch failed",
        token: "token-1",
    });

    assert.match(html, /Local Monitor が起動していません。/);
    assert.match(html, /次の操作/);
    assert.match(html, /http:\/\/127\.0\.0\.1:4320\/health\/ready/);
});

test("renderHelperHtml: not_ready state shows the not-ready banner, status code, and next-action guidance", () => {
    const html = renderHelperHtml({
        instanceId: "inst-1",
        monitorUrl: "http://127.0.0.1:4320",
        healthState: "not_ready",
        statusCode: 503,
        healthBody: "{\"status\":\"not_ready\"}",
        error: null,
        token: "token-1",
    });

    assert.match(html, /Local Monitor は起動していますが ready ではありません。/);
    assert.match(html, /次の操作/);
    assert.match(html, /http:\/\/127\.0\.0\.1:4320\/health\/ready/);
    assert.match(html, /HTTP 503/);
});

test("formatTraceLine: renders the expected one-line decision-supporting label for a sample row", () => {
    const trace = compactTrace(SAMPLE_TRACE_ROW);
    assert.equal(
        formatTraceLine(trace),
        "OK / gpt-5 / 12 spans / 3 tools / 8,420 tokens / 14:32 / 18.2s / #abc12345…",
    );
});

test("formatTraceLine: renders the error status label when error_count > 0", () => {
    const trace = compactTrace({ ...SAMPLE_TRACE_ROW, error_count: 1 });
    assert.equal(statusLabel(trace.status), "エラーあり");
    assert.match(formatTraceLine(trace), /^エラーあり \//);
});

test("dropdownOptionLabel: prepends the prompt label with an em-dash separator when present", () => {
    assert.equal(
        dropdownOptionLabel({ line: "OK / gpt-5 / 12 spans", prompt_label: "What does this function do?" }),
        "What does this function do? — OK / gpt-5 / 12 spans",
    );
});

test("dropdownOptionLabel: falls back to line alone when prompt_label is absent", () => {
    assert.equal(
        dropdownOptionLabel({ line: "OK / gpt-5 / 12 spans", prompt_label: null }),
        "OK / gpt-5 / 12 spans",
    );
});

test("dropdownOptionLabel: returns just the prompt label with the separator when line is empty", () => {
    assert.equal(
        dropdownOptionLabel({ line: "", prompt_label: "What does this function do?" }),
        "What does this function do? — ",
    );
});

test("formatTokens / formatDuration / formatClock / shortTraceId: individual formatter behavior", () => {
    assert.equal(formatTokens(8420), "8,420");
    assert.equal(formatTokens(null), null);
    assert.equal(formatDuration(18200), "18.2s");
    assert.equal(formatDuration(83000), "1:23");
    assert.equal(formatDuration(420), "420ms");
    assert.equal(formatClock("2026-07-01T14:32:07.123Z"), "14:32");
    assert.equal(shortTraceId("abc12345-6789-0000-0000-000000000000"), "#abc12345…");
    assert.equal(shortTraceId("short"), "#short");
});

test("buildAnalysisPrompt: contains the raw/PII boundary constraint lines", () => {
    const prompt = buildAnalysisPrompt({ traceId: "abc123", spanId: null, focus: "latency" });

    assert.match(prompt, /Do not copy raw prompt bodies, raw response bodies, tool arguments, tool results, PII, credentials, tokens, or local sensitive paths/);
    assert.match(prompt, /bounded DTOs from existing Local Monitor APIs/);
    assert.match(prompt, /Trace id: abc123/);
});

test("compactTrace: exposes the expected sanitized field set and no raw key", () => {
    const trace = compactTrace(SAMPLE_TRACE_ROW);
    const expectedKeys = [
        "trace_id",
        "client_kind",
        "status",
        "span_count",
        "tool_call_count",
        "error_count",
        "input_tokens",
        "output_tokens",
        "total_tokens",
        "turn_count",
        "agent_invocation_count",
        "duration_ms",
        "primary_model",
        "first_seen_at",
        "last_seen_at",
    ];

    assert.deepEqual(Object.keys(trace).sort(), expectedKeys.sort());
    for (const key of Object.keys(trace)) {
        assert.doesNotMatch(key, /raw|payload|prompt|content|argument|result|credential|secret/i);
    }
});

test("BOUNDARY_NOTE: pins the raw/PII invariant sentence", () => {
    assert.equal(
        BOUNDARY_NOTE,
        "Canvas action responses and logs must not contain raw telemetry or PII.",
    );
});

test("traceDetailSummary: returns the expected bounded shape for a sample compactTrace row", () => {
    const trace = compactTrace(SAMPLE_TRACE_ROW);
    const summary = traceDetailSummary({ trace, cacheHitRate: 0.42 });

    assert.deepEqual(summary, {
        trace_id: "abc12345-6789-0000-0000-000000000000",
        status: "ok",
        primary_model: "gpt-5",
        span_count: 12,
        tool_call_count: 3,
        total_tokens: 8420,
        duration_ms: 18200,
        cache_hit_rate: 0.42,
        last_seen_at: "2026-07-01T14:32:07.123Z",
    });
});

test("traceDetailSummary: cache_hit_rate is null when not a number (e.g. no chat turns)", () => {
    const trace = compactTrace(SAMPLE_TRACE_ROW);
    const summary = traceDetailSummary({ trace, cacheHitRate: null });

    assert.equal(summary.cache_hit_rate, null);
});

test("renderHelperHtml: contains the trace detail summary card heading and no raw fields", () => {
    const html = renderHelperHtml({
        instanceId: "inst-1",
        monitorUrl: "http://127.0.0.1:4320",
        healthState: "ready",
        statusCode: 200,
        healthBody: "{\"status\":\"ready\"}",
        error: null,
        token: "token-1",
    });

    assert.match(html, /選択したトレースの要約/);
    assert.match(html, /Local Monitorで詳細を見る/);
    // Sprint15 M5 (D038) intentionally introduces "/raw-preview" (the
    // authorized raw-preview page-navigation link); any OTHER "/raw"
    // reference (e.g. a JSON fetch of the raw-bearing route) is still
    // forbidden here.
    assert.doesNotMatch(html, /\/raw(?!-preview)/);
    assert.doesNotMatch(html, /payload_json/);
});

test("renderHelperHtml: contains the Local Monitor 概要 dashboard card and fetches /api/summary, with no raw fields", () => {
    const html = renderHelperHtml({
        instanceId: "inst-1",
        monitorUrl: "http://127.0.0.1:4320",
        healthState: "ready",
        statusCode: 200,
        healthBody: "{\"status\":\"ready\"}",
        error: null,
        token: "token-1",
    });

    assert.match(html, /Local Monitor 概要/);
    assert.match(html, /fetch\("\/api\/summary\?t=/);
    // Sprint15 M5 (D038) intentionally introduces "/raw-preview" (the
    // authorized raw-preview page-navigation link); any OTHER "/raw"
    // reference (e.g. a JSON fetch of the raw-bearing route) is still
    // forbidden here.
    assert.doesNotMatch(html, /\/raw(?!-preview)/);
    assert.doesNotMatch(html, /payload_json/);
});

test("summaryTraceLine: derives status from error_count, since /api/monitor/summary's highlight traces (MonitorHost.ToTraceDto shape) carry error_count but no precomputed status field", () => {
    const errorRow = { ...SAMPLE_TRACE_ROW, error_count: 1 };
    assert.match(summaryTraceLine(errorRow), /^エラーあり \//);
    assert.match(summaryTraceLine(SAMPLE_TRACE_ROW), /^OK \//);
    assert.equal(summaryTraceLine(null), null);
});

test("extractRawPreviewFragment: extracts the encoded payload between the first <pre> and the last </pre>", () => {
    const encodedFragment = "{&quot;a&quot;:&quot;&lt;script&gt;alert(1)&lt;/script&gt;&quot;}";
    const html = `<!DOCTYPE html><html><head><meta charset="utf-8"><title>Raw record 42</title></head><body><pre>${encodedFragment}</pre></body></html>`;
    assert.equal(extractRawPreviewFragment(html), encodedFragment);
});

test("extractRawPreviewFragment: returns null when no <pre>...</pre> pair is present", () => {
    assert.equal(extractRawPreviewFragment("<html><body>no pre here</body></html>"), null);
    assert.equal(extractRawPreviewFragment(""), null);
    assert.equal(extractRawPreviewFragment(null), null);
    assert.equal(extractRawPreviewFragment(undefined), null);
});

test("renderRawPreviewHtml: embeds the fragment verbatim (byte-for-byte, no re-encode/decode), escapes traceId/spanId, and introduces no client-side raw fetch", () => {
    const encodedFragment = "{&quot;a&quot;:&quot;&lt;script&gt;alert(1)&lt;/script&gt;&quot;}";
    const html = renderRawPreviewHtml({
        traceId: "trace-<1>",
        spanId: "span-&2",
        fragment: encodedFragment,
        token: "token-1",
    });

    assert.ok(html.includes(encodedFragment), "expected the exact encoded fragment to appear byte-for-byte in the output");
    assert.match(html, /trace-&lt;1&gt;/);
    assert.match(html, /span-&amp;2/);
    assert.doesNotMatch(html, /<script>/);
    assert.doesNotMatch(html, /fetch\(/);
});

test("renderHelperHtml: contains the raw-preview link's Japanese text and does not client-side fetch() /raw-preview", () => {
    const html = renderHelperHtml({
        instanceId: "inst-1",
        monitorUrl: "http://127.0.0.1:4320",
        healthState: "ready",
        statusCode: 200,
        healthBody: "{\"status\":\"ready\"}",
        error: null,
        token: "token-1",
    });

    assert.match(html, /生データを表示（新しいタブ）/);
    assert.doesNotMatch(html, /fetch\("\/raw-preview/);
});
