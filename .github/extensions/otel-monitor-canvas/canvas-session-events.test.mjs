import assert from "node:assert/strict";
import test from "node:test";

import {
    SESSION_EVENT_VERSION_HEADER,
    createSdkSessionCapture,
    ensureSdkSessionCapture,
    mapSdkEvent,
    sanitizeSessionPayload,
} from "./canvas-session-events.mjs";

test("maps persisted SDK events and removes secret and reasoning fields", () => {
    const mapped = mapSdkEvent({
        id: "event-1",
        type: "assistant.message",
        timestamp: "2026-07-11T01:02:03.000Z",
        parentId: "event-0",
        data: {
            messageId: "message-1",
            content: "done",
            reasoningText: "private chain of thought",
            reasoningOpaque: "ciphertext",
            encryptedContent: "ciphertext-2",
            nested: { ["author" + "ization"]: "Bearer top-secret", safe: "kept" },
        },
    });

    assert.equal(mapped.source_event_id, "event-1");
    assert.equal(mapped.type, "assistant.message");
    assert.equal(mapped.occurred_at, "2026-07-11T01:02:03.000Z");
    assert.equal(mapped.parent_event_id, "event-0");
    assert.equal(mapped.payload.content, "done");
    assert.equal(mapped.payload.nested.authorization, "[REDACTED]");
    assert.equal(mapped.payload.nested.safe, "kept");
    assert.equal("reasoningText" in mapped.payload, false);
    assert.equal("reasoningOpaque" in mapped.payload, false);
    assert.equal("encryptedContent" in mapped.payload, false);
});

test("drops reasoning, deltas, progress, and other ephemeral content", () => {
    const timestamp = "2026-07-11T01:02:03.000Z";
    for (const [index, event] of [
        { type: "assistant.reasoning", data: { content: "thought" } },
        { type: "assistant.reasoning_delta", ephemeral: true, data: { deltaContent: "thought" } },
        { type: "assistant.message_delta", ephemeral: true, data: { deltaContent: "partial" } },
        { type: "tool.execution_partial_result", ephemeral: true, data: { partialOutput: "partial" } },
        { type: "tool.execution_progress", ephemeral: true, data: { progressMessage: "partial" } },
        { type: "assistant.intent", ephemeral: true, data: { intent: "thinking" } },
    ].entries()) {
        assert.equal(mapSdkEvent({ id: `event-${index}`, timestamp, ...event }), null, event.type);
    }
});

test("drops case, snake, and kebab reasoning or delta variants and nested reasoning keys", () => {
    const timestamp = "2026-07-11T01:02:03.000Z";
    for (const [index, type] of [
        "ASSISTANT.REASONING",
        "assistant_reasoning_delta",
        "assistant-message-delta",
        "Assistant.Streaming_Delta",
    ].entries()) {
        assert.equal(mapSdkEvent({ id: `variant-${index}`, type, timestamp, data: { content: "never" } }), null, type);
    }

    const mapped = mapSdkEvent({
        id: "nested-reasoning",
        type: "assistant.message",
        timestamp,
        data: {
            content: "kept",
            nested: {
                ["Reasoning" + "_Text"]: "never",
                ["chain-of-" + "thought"]: "never",
                ["DELTA_" + "CONTENT"]: "never",
            },
        },
    });
    assert.deepEqual(mapped.payload, { content: "kept", nested: {} });
});

test("removes recursive payload delta keys from otherwise persisted events", () => {
    const mapped = mapSdkEvent({
        id: "nested-deltas",
        type: "assistant.message",
        timestamp: "2026-07-11T01:02:03.000Z",
        data: {
            content: "kept",
            delta: "never",
            nested: {
                streaming_delta: "never",
                messageDelta: "never",
                ["streaming" + "-delta"]: "never",
                safe: "kept",
            },
        },
    });

    assert.deepEqual(mapped.payload, { content: "kept", nested: { safe: "kept" } });
});

test("rejects event types outside the frozen grammar and timestamps without an explicit offset", () => {
    const base = { id: "invalid", data: {} };
    assert.equal(mapSdkEvent({ ...base, type: "1bad", timestamp: "2026-07-11T01:02:03.000Z" }), null);
    assert.equal(mapSdkEvent({ ...base, type: "bad/type", timestamp: "2026-07-11T01:02:03.000Z" }), null);
    assert.equal(mapSdkEvent({ ...base, type: "session.start", timestamp: "2026-07-11T01:02:03" }), null);
    assert.equal(mapSdkEvent({ ...base, type: "session.start", timestamp: "not-a-date+09:00" }), null);
    assert.notEqual(mapSdkEvent({ ...base, type: "session.start", timestamp: "2026-07-11T01:02:03+09:00" }), null);
});

test("rejects impossible calendar timestamps and accepts valid offsets with fractions", () => {
    const base = { id: "calendar", type: "session.start", data: {} };
    assert.equal(mapSdkEvent({ ...base, timestamp: "2026-02-30T01:02:03Z" }), null);
    assert.equal(mapSdkEvent({ ...base, timestamp: "2026-13-01T01:02:03+09:00" }), null);
    assert.notEqual(mapSdkEvent({ ...base, timestamp: "2024-02-29T01:02:03.123456-04:30" }), null);
});

test("recursively filters secret keys and credential-shaped strings without mutating input", () => {
    const input = {
        ["to" + "ken"]: "secret-token",
        nested: [{ ["pass" + "word"]: "password" }, "Bearer abc.def.ghi", "safe"],
        ["api" + "Key"]: "key",
        ["access" + "Token"]: "access-value",
        inputTokens: 42,
    };

    assert.deepEqual(sanitizeSessionPayload(input), {
        ["to" + "ken"]: "[REDACTED]",
        nested: [{ ["pass" + "word"]: "[REDACTED]" }, "[REDACTED]", "safe"],
        ["api" + "Key"]: "[REDACTED]",
        ["access" + "Token"]: "[REDACTED]",
        inputTokens: 42,
    });
    assert.equal(input.token, "secret-token");
});

test("redacts credential-shaped substrings embedded in otherwise ordinary text", () => {
    const payload = sanitizeSessionPayload({
        text: "before Bearer abc.def.ghi after",
        url: "https://localhost/?access_token=value123&safe=yes",
        output: "created sk-abcdefgh12345678 successfully",
    });

    assert.equal(payload.text, "before [REDACTED] after");
    assert.equal(payload.url, "https://localhost/?access_token=[REDACTED]&safe=yes");
    assert.equal(payload.output, "created [REDACTED] successfully");
});

test("capture emits a first-open gap marker, aggregates usage, and sends committed event batches", async () => {
    const handlers = [];
    const requests = [];
    const session = { on: (handler) => { handlers.push(handler); return () => {}; } };
    const capture = createSdkSessionCapture({
        session,
        nativeSessionId: "canvas-session-1",
        monitorUrl: "http://127.0.0.1:4320",
        fetchImpl: async (url, init) => {
            requests.push({ url, init, body: JSON.parse(init.body) });
            return { ok: true, status: 204 };
        },
        scheduleFlush: () => null,
        now: () => new Date("2026-07-11T01:00:00.000Z"),
    });

    assert.equal(handlers.length, 1);
    handlers[0]({ id: "usage-1", type: "assistant.usage", ephemeral: true, timestamp: "2026-07-11T01:01:00.000Z", data: { model: "gpt-5", inputTokens: 10, outputTokens: 3, cacheReadTokens: 2, cost: 1.5, duration: 20 } });
    handlers[0]({ id: "usage-2", type: "assistant.usage", ephemeral: true, timestamp: "2026-07-11T01:01:01.000Z", data: { model: "gpt-5", inputTokens: 5, outputTokens: 4, cacheWriteTokens: 1, cost: 0.5, duration: 30 } });
    handlers[0]({ id: "delta", type: "assistant.message_delta", ephemeral: true, timestamp: "2026-07-11T01:01:01.500Z", data: { deltaContent: "never send" } });
    handlers[0]({ id: "message", type: "assistant.message", timestamp: "2026-07-11T01:01:02.000Z", data: { messageId: "m1", content: "complete" } });
    handlers[0]({ id: "turn-end", type: "assistant.turn_end", timestamp: "2026-07-11T01:01:03.000Z", data: { turnId: "turn-1" } });
    await capture.flush();

    assert.equal(requests.length, 1);
    assert.equal(requests[0].url, "http://127.0.0.1:4320/api/session-ingest/v1/events");
    assert.equal(requests[0].init.headers[SESSION_EVENT_VERSION_HEADER], "1");
    assert.equal(requests[0].body.schema_version, 1);
    assert.equal(requests[0].body.source_adapter, "copilot-sdk-stream");
    assert.equal(requests[0].body.source_surface, "copilot-sdk");
    assert.equal(requests[0].body.native_session_id, "canvas-session-1");
    assert.equal(requests[0].body.events[0].type, "capture.started");
    assert.equal(requests[0].body.events[0].payload.gap_before_capture, true);
    assert.equal(requests[0].body.events.some((event) => event.type.includes("delta")), false);
    const usage = requests[0].body.events.find((event) => event.type === "assistant.usage");
    assert.deepEqual(usage.payload, {
        calls: 2,
        models: ["gpt-5"],
        input_tokens: 15,
        output_tokens: 7,
        cache_read_tokens: 2,
        cache_write_tokens: 1,
        cost: 2,
        duration_ms: 50,
    });
});

test("capture is fail-open and never rejects the SDK event callback", async () => {
    let handler;
    const capture = createSdkSessionCapture({
        session: { on: (value) => { handler = value; return () => {}; } },
        nativeSessionId: "canvas-session-2",
        monitorUrl: "http://localhost:4320/",
        fetchImpl: async () => { throw new Error("contains sensitive response"); },
        scheduleFlush: () => null,
        scheduleRetry: () => null,
        now: () => new Date("2026-07-11T01:00:00.000Z"),
    });

    assert.doesNotThrow(() => handler({ id: "message", type: "user.message", timestamp: "2026-07-11T01:00:01.000Z", data: { content: "prompt" } }));
    await assert.doesNotReject(capture.flush());
});

test("capture keeps a batch until the endpoint returns exactly 204", async () => {
    let handler;
    const retryCallbacks = [];
    const bodies = [];
    const outcomes = [
        () => { throw new Error("offline"); },
        () => ({ ok: false, status: 503 }),
        () => ({ ok: true, status: 204 }),
    ];
    const capture = createSdkSessionCapture({
        session: { on: (value) => { handler = value; return () => {}; } },
        nativeSessionId: "canvas-session-retry",
        monitorUrl: "http://127.0.0.1:4320",
        fetchImpl: async (_url, init) => {
            bodies.push(JSON.parse(init.body));
            return outcomes.shift()();
        },
        scheduleFlush: () => null,
        scheduleRetry: (callback) => { retryCallbacks.push(callback); return null; },
        now: () => new Date("2026-07-11T01:00:00.000Z"),
    });
    handler({ id: "message-retry", type: "user.message", timestamp: "2026-07-11T01:00:01.000Z", data: { content: "prompt" } });

    await capture.flush();
    retryCallbacks[0]();
    await capture.flush();
    retryCallbacks[1]();
    await capture.flush();

    assert.equal(bodies.length, 3);
    assert.deepEqual(bodies[1], bodies[0]);
    assert.deepEqual(bodies[2], bodies[0]);
    assert.equal(retryCallbacks.length, 2);
});

test("capture does not resend during retry backoff despite queued, lifecycle, or manual flushes", async () => {
    let handler;
    const retryCallbacks = [];
    const bodies = [];
    const capture = createSdkSessionCapture({
        session: { on: (value) => { handler = value; return () => {}; } },
        nativeSessionId: "canvas-session-backoff",
        monitorUrl: "http://127.0.0.1:4320",
        fetchImpl: async (_url, init) => {
            bodies.push(JSON.parse(init.body));
            return { status: bodies.length === 1 ? 503 : 204 };
        },
        scheduleFlush: () => null,
        scheduleRetry: (callback) => { retryCallbacks.push(callback); return null; },
        now: () => new Date("2026-07-11T01:00:00.000Z"),
    });

    handler({ id: "message-1", type: "user.message", timestamp: "2026-07-11T01:00:01.000Z", data: { content: "first" } });
    const queuedFlush = capture.flush();
    const anotherQueuedFlush = capture.flush();
    await Promise.all([queuedFlush, anotherQueuedFlush]);
    assert.equal(bodies.length, 1);
    assert.equal(retryCallbacks.length, 1);

    handler({ id: "message-2", type: "user.message", timestamp: "2026-07-11T01:00:02.000Z", data: { content: "second" } });
    handler({ id: "turn-end", type: "assistant.turn_end", timestamp: "2026-07-11T01:00:03.000Z", data: { turnId: "turn-1" } });
    await capture.flush();
    assert.equal(bodies.length, 1);
    assert.equal(retryCallbacks.length, 1);

    retryCallbacks[0]();
    await capture.flush();
    assert.equal(bodies.length, 2);
    assert.deepEqual(bodies[1].events.map((event) => event.source_event_id), [
        ...bodies[0].events.map((event) => event.source_event_id),
        "message-2",
        "turn-end",
    ]);
});

test("capture registry subscribes once per Canvas-native session ID", () => {
    let subscriptions = 0;
    const session = { on: () => { subscriptions += 1; return () => {}; } };
    const registry = new Map();
    const common = {
        session,
        monitorUrl: "http://127.0.0.1:4320",
        fetchImpl: async () => ({ ok: true, status: 204 }),
        scheduleFlush: () => null,
        now: () => new Date("2026-07-11T01:00:00.000Z"),
    };

    const first = ensureSdkSessionCapture(registry, { ...common, nativeSessionId: "native-1" });
    const repeated = ensureSdkSessionCapture(registry, { ...common, nativeSessionId: "native-1" });
    const distinct = ensureSdkSessionCapture(registry, { ...common, nativeSessionId: "native-2" });

    assert.equal(first, repeated);
    assert.notEqual(first, distinct);
    assert.equal(subscriptions, 2);
    assert.equal(registry.size, 2);
});

test("capture splits batches at 100 events", async () => {
    let handler;
    const bodies = [];
    const capture = createSdkSessionCapture({
        session: { on: (value) => { handler = value; return () => {}; } },
        nativeSessionId: "canvas-session-3",
        monitorUrl: "http://[::1]:4320",
        fetchImpl: async (_url, init) => { bodies.push(JSON.parse(init.body)); return { ok: true, status: 204 }; },
        scheduleFlush: () => null,
        now: () => new Date("2026-07-11T01:00:00.000Z"),
    });

    for (let index = 0; index < 205; index += 1) {
        handler({ id: `message-${index}`, type: "user.message", timestamp: "2026-07-11T01:00:01.000Z", data: { content: "prompt" } });
    }
    await capture.flush();

    assert.deepEqual(bodies.map((body) => body.events.length), [100, 100, 6]);
    assert.ok(bodies.every((body) => Buffer.byteLength(JSON.stringify(body), "utf8") <= 1_048_576));
});

test("capture replaces an individually oversized payload before sending", async () => {
    let handler;
    const bodies = [];
    const capture = createSdkSessionCapture({
        session: { on: (value) => { handler = value; return () => {}; } },
        nativeSessionId: "canvas-session-4",
        monitorUrl: "http://127.0.0.1:4320",
        fetchImpl: async (_url, init) => { bodies.push(JSON.parse(init.body)); return { ok: true, status: 204 }; },
        scheduleFlush: () => null,
        now: () => new Date("2026-07-11T01:00:00.000Z"),
    });

    handler({ id: "large-message", type: "assistant.message", timestamp: "2026-07-11T01:00:01.000Z", data: { content: "x".repeat(1_100_000) } });
    await capture.flush();

    assert.equal(bodies.length, 1);
    assert.equal(bodies[0].events[1].payload.content_omitted, "event_exceeds_ingest_limit");
    assert.ok(Buffer.byteLength(JSON.stringify(bodies[0]), "utf8") <= 1_048_576);
});
