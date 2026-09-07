(() => {
  "use strict";
  const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
  const instant = value => typeof value === "string" && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}\+00:00$/.test(value) && Number.isFinite(Date.parse(value));
  const exact = (value, keys) => value && typeof value === "object" && !Array.isArray(value)
    && Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key));
  const date = value => value.slice(0, 19).replace("T", " ") + " UTC";
  const controller = new AbortController();
  window.addEventListener("pagehide", () => controller.abort());

  async function request(url, method = "GET") {
    const options = { method, credentials: "same-origin", cache: "no-store", signal: controller.signal, headers: { Accept: "application/json" } };
    if (method !== "GET") { options.headers["Content-Type"] = "application/json; charset=utf-8"; options.headers["x-monitor-csrf"] = "local-monitor"; options.body = "{}"; }
    const response = await fetch(url, options);
    const reader = response.body?.getReader();
    if (!reader) throw new Error();
    const chunks = []; let size = 0;
    try {
      for (;;) {
        const part = await reader.read(); if (part.done) break;
        size += part.value.byteLength; if (size > 16384) throw new Error(); chunks.push(part.value);
      }
    } catch (error) { await reader.cancel(); throw error; }
    finally { reader.releaseLock(); }
    const bytes = new Uint8Array(size); let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
    const value = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
    if (!response.ok) {
      const error = new Error();
      if (exact(value, ["error"]) && ["saved_comparison_limit_reached", "comparison_expired", "comparison_not_found", "persistence_busy"].includes(value.error)) error.code = value.error;
      throw error;
    }
    return value;
  }

  for (const root of document.querySelectorAll("[data-comparison-save]")) {
    const repository = root.dataset.repositoryId, comparison = root.dataset.comparisonId;
    if (!uuid.test(repository ?? "") || !uuid.test(comparison ?? "")) continue;
    const save = root.querySelector("[data-comparison-save-button]"), unsave = root.querySelector("[data-comparison-unsave-button]"), status = root.querySelector("[data-comparison-save-status]");
    const url = `/api/local-monitor/v1/repositories/${repository}/comparisons/${comparison}/saved`;
    let busy = false;
    async function update(method = "GET") {
      if (busy) return; busy = true; save.disabled = true; unsave.disabled = true;
      try {
        const value = await request(url, method);
        if (!exact(value, ["schema_version", "comparison_id", "repository_id", "is_saved", "first_saved_at", "saved_until", "effective_expires_at", "available"])
            || value.schema_version !== "local-monitor-comparison-saved.response.v1" || value.comparison_id !== comparison || value.repository_id !== repository
            || typeof value.is_saved !== "boolean" || typeof value.available !== "boolean" || !instant(value.effective_expires_at)
            || (value.first_saved_at !== null && !instant(value.first_saved_at)) || (value.saved_until !== null && !instant(value.saved_until))
            || (value.is_saved && (value.first_saved_at === null || value.saved_until === null))) throw new Error();
        save.hidden = value.is_saved; save.disabled = !value.available;
        unsave.hidden = !value.is_saved; unsave.disabled = !value.available;
        status.textContent = value.is_saved ? `保存済み · 期限 ${date(value.effective_expires_at)}`
          : value.available ? `未保存 · 期限 ${date(value.effective_expires_at)}` : "保存を解除しました。元の24時間の期限が終了しています。";
        if (!value.available) document.dispatchEvent(new Event("cao-comparison-expired"));
      } catch (error) {
        if (controller.signal.aborted) return;
        status.textContent = error.code === "saved_comparison_limit_reached" ? "保存は全体で20件までです。保存一覧から不要な比較を開き、保存を解除してください。"
          : error.code === "comparison_expired" ? "比較の期限が終了したため保存できません。"
          : "保存状態を確認できませんでした。再試行してください。";
        save.disabled = error.code === "comparison_expired"; unsave.disabled = error.code === "comparison_expired";
      } finally { busy = false; }
    }
    save.addEventListener("click", () => update("POST"));
    unsave.addEventListener("click", () => update("DELETE"));
    update();
  }

  for (const root of document.querySelectorAll("[data-saved-comparisons]")) {
    const repository = root.dataset.repositoryId;
    if (!uuid.test(repository ?? "")) continue;
    const refresh = root.querySelector("[data-saved-comparisons-refresh]"), status = root.querySelector("[data-saved-comparisons-status]"), list = root.querySelector("[data-saved-comparisons-list]");
    let busy = false;
    async function load() {
      if (busy) return; busy = true; refresh.disabled = true;
      try {
        const value = await request(`/api/local-monitor/v1/repositories/${repository}/comparisons/saved`);
        if (!exact(value, ["schema_version", "repository_id", "maximum_saved_count", "items"]) || value.schema_version !== "local-monitor-comparison-saved-list.response.v1"
            || value.repository_id !== repository || value.maximum_saved_count !== 20 || !Array.isArray(value.items) || value.items.length > 20) throw new Error();
        const ids = new Set();
        const items = value.items.map(item => {
          if (!exact(item, ["comparison_id", "created_at", "first_saved_at", "saved_until", "cohort_a_count", "cohort_b_count", "location"])
              || !uuid.test(item.comparison_id ?? "") || ids.has(item.comparison_id) || !instant(item.created_at) || !instant(item.first_saved_at) || !instant(item.saved_until)
              || !Number.isInteger(item.cohort_a_count) || item.cohort_a_count < 1 || item.cohort_a_count > 199
              || !Number.isInteger(item.cohort_b_count) || item.cohort_b_count < 1 || item.cohort_b_count > 199
              || item.location !== `/repositories/${repository}/comparisons/${item.comparison_id}`) throw new Error();
          ids.add(item.comparison_id);
          const li = document.createElement("li"), link = document.createElement("a");
          link.href = item.location;
          link.textContent = `${date(item.created_at)} · 基準 ${item.cohort_a_count}件 / 比較対象 ${item.cohort_b_count}件 · ${item.comparison_id}`;
          li.append(link, document.createTextNode(` · 保存期限 ${date(item.saved_until)}`)); return li;
        });
        list.replaceChildren(...items); status.textContent = `${items.length}件の保存した比較（保存上限は全体で20件）`;
      } catch { if (!controller.signal.aborted) { list.replaceChildren(); status.textContent = "保存した比較を読み込めませんでした。"; } }
      finally { busy = false; refresh.disabled = false; }
    }
    refresh.addEventListener("click", load); load();
  }
})();
