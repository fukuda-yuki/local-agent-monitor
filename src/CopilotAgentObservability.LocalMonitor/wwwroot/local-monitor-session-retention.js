(() => {
  "use strict";
  const reads = new WeakMap();
  const keys = ["readable_item_count", "read_denied_item_count", "pinned_item_count", "unpinned_item_count", "lifecycle_counts"];
  const lifecycle = ["expiring", "retained_by_policy", "expired_pending_deletion", "deletion_queued", "deleting", "deleted", "deletion_failed"];
  const exact = (value, names) => value && typeof value === "object" && !Array.isArray(value) && Object.keys(value).length === names.length && names.every(name => Object.hasOwn(value, name));
  const count = value => Number.isSafeInteger(value) && value >= 0;
  const instant = value => value === null || typeof value === "string" && Number.isFinite(Date.parse(value));
  const time = value => value === null ? "期限を確認できません" : new Date(value).toISOString().slice(0, 19).replace("T", " ") + " UTC";
  async function refresh(root) {
    const id = root?.dataset.sessionId;
    if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(id ?? "")) return;
    reads.get(root)?.abort(); const controller = new AbortController(); reads.set(root, controller);
    try {
      const response = await fetch(`/api/retention/v1/sessions/${id}/management`, { credentials: "same-origin", cache: "no-store", signal: controller.signal });
      if (!response.ok) throw new Error();
      const value = await response.json();
      if (controller.signal.aborted || !root.isConnected) return;
      if (!exact(value, ["schema_version", "session_id", "target_scope", "target_item_count", "excluded_item_count", "current_state", "expiring_item_count", "earliest_expires_at", "latest_expires_at"])
          || value.schema_version !== "retention-session-management.v1" || value.session_id !== id || value.target_scope !== "session_event_content"
          || !count(value.target_item_count) || !count(value.excluded_item_count) || !count(value.expiring_item_count)
          || !exact(value.current_state, keys) || !keys.slice(0, 4).every(key => count(value.current_state[key]))
          || !exact(value.current_state.lifecycle_counts, lifecycle) || !lifecycle.every(key => count(value.current_state.lifecycle_counts[key]))
          || !instant(value.earliest_expires_at) || !instant(value.latest_expires_at)) throw new Error();
      const state = value.current_state;
      const summary = document.createElement("p");
      summary.textContent = value.target_item_count === 0 ? "保持管理の対象となるセッション本文はありません。"
        : `対象本文 ${value.target_item_count}件 · ピン状態 ${state.pinned_item_count}件 · 非ピン ${state.unpinned_item_count}件 · 読み取り可能 ${state.readable_item_count}件 · 読み取り拒否 ${state.read_denied_item_count}件`;
      const expiry = document.createElement("p");
      expiry.textContent = value.expiring_item_count === 0 ? "期限による自動削除を待つ本文はありません。"
        : `期限による自動削除の対象 ${value.expiring_item_count}件 · 最も早い期限 ${time(value.earliest_expires_at)} · 最も遅い期限 ${time(value.latest_expires_at)}`;
      const scope = document.createElement("p");
      scope.textContent = "対象はこのセッションのイベント本文です。OTelの元記録（raw_record）は項目ごとの保持管理で扱います。ピン状態は保持ライフサイクルの値で、手動操作の履歴を示しません。アーカイブ表示とは別です。";
      const nodes = [summary, expiry, scope];
      if (value.excluded_item_count > 0) { const excluded = document.createElement("p"); excluded.textContent = `所有関係を確認できないため対象外 ${value.excluded_item_count}件`; nodes.push(excluded); }
      root.replaceChildren(...nodes);
    } catch { if (!controller.signal.aborted && root.isConnected) root.textContent = "保持・期限の状態を確認できません。保持管理画面で再確認してください。"; }
  }
  window.LocalMonitorSessionRetention = Object.freeze({ refresh });
  const refreshAll = () => document.querySelectorAll("[data-session-retention-status]").forEach(refresh);
  document.addEventListener("cao-retention-state-changed", refreshAll);
  window.addEventListener("pagehide", () => document.querySelectorAll("[data-session-retention-status]").forEach(root => reads.get(root)?.abort()));
  refreshAll();
})();
