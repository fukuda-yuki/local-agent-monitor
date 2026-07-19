// Local Ingestion Monitor — per-page raw span-detail request broker.
//
// The error panel and span inspector can request the same raw span detail at
// once. Keep only that in-flight request shared; completed details are never
// retained in the browser beyond their consumers' own state.
(() => {
  "use strict";

  const root = document.getElementById("trace-detail-root");
  if (!root) return;

  const traceId = root.dataset.traceId;
  const pendingDetails = new Map();

  window.caoLoadSpanDetail = (spanId) => {
    const pending = pendingDetails.get(spanId);
    if (pending) return pending;

    const request = fetch(`/traces/${encodeURIComponent(traceId)}/spans/${encodeURIComponent(spanId)}/detail`, { cache: "no-store" })
      .then(async (response) => response.ok ? await response.json() : null)
      .then((detail) => detail && typeof detail === "object" ? detail : null)
      .catch(() => null);
    pendingDetails.set(spanId, request);
    request.finally(() => pendingDetails.delete(spanId));
    return request;
  };
})();
