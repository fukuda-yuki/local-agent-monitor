import { createHash } from "node:crypto";

export const SESSION_EVENT_VERSION_HEADER = "X-CAO-Session-Event-Version";

const SESSION_INGEST_PATH = "/api/session-ingest/v1/events";
const MAX_EVENTS_PER_BATCH = 100;
const MAX_BATCH_BYTES = 1_048_576;
const REDACTED = "[REDACTED]";
const EVENT_TYPE_PATTERN = /^[A-Za-z][A-Za-z0-9._-]{0,127}$/;
const OFFSET_TIMESTAMP_PATTERN = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?(Z|[+-](\d{2}):(\d{2}))$/;
const NON_PERSISTED_NORMALIZED_TYPES = new Set([
    "assistantintent",
    "toolexecutionpartialresult",
    "toolexecutionprogress",
]);

function isSecretKey(key) {
    const normalized = key.replace(/[^a-z0-9]/gi, "").toLowerCase();
    return normalized === "authorization"
        || normalized === "cookie"
        || normalized === "setcookie"
        || normalized === "credential"
        || normalized === "credentials"
        || normalized === "apikey"
        || normalized === "privatekey"
        || normalized.endsWith("password")
        || normalized.endsWith("passwd")
        || normalized.endsWith("secret")
        || normalized.endsWith("token");
}

function containsReasoningPayload(key) {
    const normalized = key.replace(/[^a-z0-9]/gi, "").toLowerCase();
    return normalized.includes("reasoning")
        || normalized.includes("chainofthought")
        || normalized.includes("delta")
        || normalized === "encryptedcontent";
}

function isOffsetTimestamp(value) {
    const match = OFFSET_TIMESTAMP_PATTERN.exec(value);
    if (!match) {
        return false;
    }

    const [, yearText, monthText, dayText, hourText, minuteText, secondText, , zone, offsetHourText, offsetMinuteText] = match;
    const year = Number(yearText);
    const month = Number(monthText);
    const day = Number(dayText);
    const hour = Number(hourText);
    const minute = Number(minuteText);
    const second = Number(secondText);
    if (month < 1 || month > 12 || hour > 23 || minute > 59 || second > 59) {
        return false;
    }

    const leapYear = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
    const daysInMonth = [31, leapYear ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
    if (day < 1 || day > daysInMonth[month - 1]) {
        return false;
    }

    if (zone !== "Z") {
        const offsetHour = Number(offsetHourText);
        const offsetMinute = Number(offsetMinuteText);
        if (offsetHour > 14 || offsetMinute > 59 || (offsetHour === 14 && offsetMinute !== 0)) {
            return false;
        }
    }
    return true;
}

function redactCredentialSubstrings(value) {
    return value
        .replace(/\bBearer\s+[A-Za-z0-9._~+/=-]+/gi, REDACTED)
        .replace(/\b(?:github_pat_[A-Za-z0-9_]{8,}|gh[pousr]_[A-Za-z0-9]{8,}|sk-[A-Za-z0-9_-]{8,})\b/gi, REDACTED)
        .replace(/\b(api[_-]?key|access[_-]?token|refresh[_-]?token|password|secret)=([^&\s]+)/gi, "$1=[REDACTED]");
}

function finiteNumber(value) {
    return typeof value === "number" && Number.isFinite(value) ? value : 0;
}

function nonBlankString(value, maximumLength) {
    return typeof value === "string" && value.trim().length > 0 && value.length <= maximumLength
        ? value
        : null;
}

function sanitizedValue(value, seen) {
    if (typeof value === "string") {
        return redactCredentialSubstrings(value);
    }
    if (value === null || typeof value === "number" || typeof value === "boolean") {
        return value;
    }
    if (typeof value !== "object") {
        return null;
    }
    if (seen.has(value)) {
        return REDACTED;
    }

    seen.add(value);
    if (Array.isArray(value)) {
        const result = value.map((item) => sanitizedValue(item, seen));
        seen.delete(value);
        return result;
    }

    const result = {};
    for (const [key, item] of Object.entries(value)) {
        if (containsReasoningPayload(key)) {
            continue;
        }
        result[key] = isSecretKey(key) ? REDACTED : sanitizedValue(item, seen);
    }
    seen.delete(value);
    return result;
}

export function sanitizeSessionPayload(payload) {
    if (!payload || typeof payload !== "object" || Array.isArray(payload)) {
        return {};
    }
    return sanitizedValue(payload, new WeakSet());
}

function eventRunNativeId(event) {
    const data = event?.data;
    return nonBlankString(data?.turnId, 256)
        ?? nonBlankString(data?.runId, 256)
        ?? null;
}

export function mapSdkEvent(event) {
    const type = nonBlankString(event?.type, 128);
    if (!type || !EVENT_TYPE_PATTERN.test(type)) {
        return null;
    }
    const normalizedType = type.replace(/[^a-z0-9]/gi, "").toLowerCase();
    if (normalizedType.includes("reasoning")
        || normalizedType.includes("delta")
        || NON_PERSISTED_NORMALIZED_TYPES.has(normalizedType)
        || event?.ephemeral === true) {
        return null;
    }

    const sourceEventId = nonBlankString(event?.id, 256);
    const occurredAt = nonBlankString(event?.timestamp, 128);
    if (!sourceEventId
        || !occurredAt
        || !isOffsetTimestamp(occurredAt)) {
        return null;
    }

    return {
        source_event_id: sourceEventId,
        type,
        occurred_at: occurredAt,
        parent_event_id: nonBlankString(event?.parentId, 256),
        run_native_id: eventRunNativeId(event),
        trace_id: nonBlankString(event?.data?.traceId, 128),
        payload: sanitizeSessionPayload(event?.data ?? {}),
    };
}

function hashId(...values) {
    return createHash("sha256").update(values.join("\u0000"), "utf8").digest("hex");
}

function usageSummaryEvent(usage, nativeSessionId, captureStartedAt, sequence, occurredAt) {
    if (usage.calls === 0) {
        return null;
    }
    return {
        source_event_id: `usage-${hashId(nativeSessionId, captureStartedAt, String(sequence)).slice(0, 48)}`,
        type: "assistant.usage",
        occurred_at: occurredAt,
        parent_event_id: null,
        run_native_id: null,
        trace_id: null,
        payload: {
            calls: usage.calls,
            models: [...usage.models].sort(),
            input_tokens: usage.inputTokens,
            output_tokens: usage.outputTokens,
            cache_read_tokens: usage.cacheReadTokens,
            cache_write_tokens: usage.cacheWriteTokens,
            cost: usage.cost,
            duration_ms: usage.duration,
        },
    };
}

function emptyUsage() {
    return {
        calls: 0,
        models: new Set(),
        inputTokens: 0,
        outputTokens: 0,
        cacheReadTokens: 0,
        cacheWriteTokens: 0,
        cost: 0,
        duration: 0,
    };
}

function addUsage(aggregate, data) {
    aggregate.calls += 1;
    const model = nonBlankString(data?.model, 256);
    if (model) {
        aggregate.models.add(model);
    }
    aggregate.inputTokens += finiteNumber(data?.inputTokens);
    aggregate.outputTokens += finiteNumber(data?.outputTokens);
    aggregate.cacheReadTokens += finiteNumber(data?.cacheReadTokens);
    aggregate.cacheWriteTokens += finiteNumber(data?.cacheWriteTokens);
    aggregate.cost += finiteNumber(data?.cost);
    aggregate.duration += finiteNumber(data?.duration);
}

function makeEnvelope(nativeSessionId, events) {
    return {
        schema_version: 1,
        source_adapter: "copilot-sdk-stream",
        source_surface: "copilot-sdk",
        native_session_id: nativeSessionId,
        events,
    };
}

function envelopeBytes(nativeSessionId, events) {
    return Buffer.byteLength(JSON.stringify(makeEnvelope(nativeSessionId, events)), "utf8");
}

function boundedEvent(nativeSessionId, event) {
    if (envelopeBytes(nativeSessionId, [event]) <= MAX_BATCH_BYTES) {
        return event;
    }
    return {
        ...event,
        payload: { content_omitted: "event_exceeds_ingest_limit" },
    };
}

function peekBatch(nativeSessionId, queue) {
    const events = [];
    while (events.length < MAX_EVENTS_PER_BATCH && events.length < queue.length) {
        const candidate = boundedEvent(nativeSessionId, queue[events.length]);
        if (events.length > 0 && envelopeBytes(nativeSessionId, [...events, candidate]) > MAX_BATCH_BYTES) {
            break;
        }
        events.push(candidate);
    }
    return events;
}

export function createSdkSessionCapture({
    session,
    nativeSessionId,
    monitorUrl,
    fetchImpl = fetch,
    scheduleFlush = (callback) => setTimeout(callback, 100),
    scheduleRetry = (callback) => setTimeout(callback, 1000),
    now = () => new Date(),
}) {
    if (!session || typeof session.on !== "function") {
        throw new TypeError("A subscribable Copilot session is required.");
    }
    if (!nonBlankString(nativeSessionId, 256)) {
        throw new TypeError("A valid Canvas session ID is required.");
    }

    const captureStartedAt = now().toISOString();
    const endpoint = `${monitorUrl.replace(/\/$/, "")}${SESSION_INGEST_PATH}`;
    const queue = [{
        source_event_id: `capture-${hashId(nativeSessionId, captureStartedAt).slice(0, 48)}`,
        type: "capture.started",
        occurred_at: captureStartedAt,
        parent_event_id: null,
        run_native_id: null,
        trace_id: null,
        payload: {
            capture_boundary: "first_canvas_open",
            gap_before_capture: true,
        },
    }];
    let usage = emptyUsage();
    let usageSequence = 0;
    let lastUsageAt = captureStartedAt;
    let flushScheduled = false;
    let retryScheduled = false;
    let flushChain = Promise.resolve();

    const schedule = () => {
        if (flushScheduled || retryScheduled) {
            return;
        }
        flushScheduled = true;
        scheduleFlush(() => {
            flushScheduled = false;
            void flush();
        });
    };

    const flushUsage = () => {
        const summary = usageSummaryEvent(usage, nativeSessionId, captureStartedAt, ++usageSequence, lastUsageAt);
        usage = emptyUsage();
        if (summary) {
            queue.push(summary);
        }
    };

    const retryLater = () => {
        if (retryScheduled) {
            return;
        }
        retryScheduled = true;
        scheduleRetry(() => {
            retryScheduled = false;
            void flush();
        });
    };

    const drain = async () => {
        if (retryScheduled) {
            return;
        }
        flushUsage();
        while (queue.length > 0) {
            const events = peekBatch(nativeSessionId, queue);
            const envelope = makeEnvelope(nativeSessionId, events);
            try {
                const response = await fetchImpl(endpoint, {
                    method: "POST",
                    headers: {
                        "Content-Type": "application/json",
                        [SESSION_EVENT_VERSION_HEADER]: "1",
                    },
                    body: JSON.stringify(envelope),
                });
                if (response?.status !== 204) {
                    retryLater();
                    break;
                }
                queue.splice(0, events.length);
            } catch {
                // Capture is deliberately fail-open. SDK execution must never
                // depend on Local Monitor availability, and raw failures are
                // not written to extension logs.
                retryLater();
                break;
            }
        }
    };

    const flush = () => {
        flushChain = flushChain.then(drain, drain);
        return flushChain;
    };

    const unsubscribe = session.on((event) => {
        try {
            if (event?.type === "assistant.usage") {
                addUsage(usage, event.data);
                lastUsageAt = nonBlankString(event.timestamp, 128) ?? now().toISOString();
            } else {
                const mapped = mapSdkEvent(event);
                if (mapped) {
                    if (mapped.type === "assistant.turn_end" || mapped.type === "session.shutdown" || mapped.type === "session.task_complete") {
                        flushUsage();
                    }
                    queue.push(mapped);
                }
            }

            if (event?.type === "assistant.turn_end" || event?.type === "session.idle" || event?.type === "session.shutdown" || event?.type === "session.task_complete") {
                void flush();
            } else {
                schedule();
            }
        } catch {
            // Malformed preview events are dropped without affecting Copilot.
        }
    });

    schedule();
    return { flush, unsubscribe };
}

export function ensureSdkSessionCapture(registry, options) {
    const existing = registry.get(options.nativeSessionId);
    if (existing) {
        return existing;
    }
    const capture = createSdkSessionCapture(options);
    registry.set(options.nativeSessionId, capture);
    return capture;
}
