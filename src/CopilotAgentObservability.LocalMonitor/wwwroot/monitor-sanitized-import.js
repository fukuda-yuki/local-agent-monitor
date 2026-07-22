(() => {
    "use strict";

    const fileInput = document.getElementById("sanitized-import-file");
    if (!fileInput) return;
    const fileName = document.getElementById("sanitized-import-file-name");
    const previewButton = document.getElementById("sanitized-import-preview-button");
    const commitButton = document.getElementById("sanitized-import-commit-button");
    const previewSurface = document.getElementById("sanitized-import-preview");
    const previewContent = document.getElementById("sanitized-import-preview-content");
    const resultSurface = document.getElementById("sanitized-import-result");
    const resultContent = document.getElementById("sanitized-import-result-content");
    const historyBody = document.querySelector("#sanitized-import-history tbody");
    const historyEmpty = document.getElementById("sanitized-import-history-empty");
    const historyRefresh = document.getElementById("sanitized-import-history-refresh");
    const live = document.getElementById("sanitized-import-live");
    const error = document.getElementById("sanitized-import-error");
    const csrfHeaders = { "x-monitor-csrf": "local-monitor" };
    let selectedFile = null;
    let preview = null;
    let revision = 0;
    let operationController = new AbortController();
    let historyController = new AbortController();
    let historyRevision = 0;
    let commitPending = false;
    let pageLeaving = false;
    let errorSource = null;

    class ImportRequestError extends Error {
        constructor(code) {
            super(code);
            this.code = code;
        }
    }

    function setLive(message) { live.textContent = message; }
    function clearError() { error.hidden = true; error.textContent = ""; errorSource = null; }
    function showError(code, ambiguousCommit = false) {
        error.textContent = ambiguousCommit
            ? `確定結果を確認できませんでした（${code}）。自動再送はしていません。履歴を再取得してください。`
            : `処理できませんでした（${code}）。ファイルと表示内容を確認してください。`;
        error.hidden = false;
        errorSource = "operation";
        setLive("処理は完了していません。");
    }

    function showHistoryError(code, committedOutcome) {
        error.textContent = committedOutcome
            ? `取り込みは確定しましたが、履歴を更新できませんでした（${code}）。再取得してください。`
            : `取り込み履歴を取得できませんでした（${code}）。再取得してください。`;
        error.hidden = false;
        errorSource = "history";
        if (!committedOutcome) setLive("取り込み履歴を取得できませんでした。");
    }

    function resetOperationController() {
        operationController.abort();
        operationController = new AbortController();
    }

    function invalidatePreview() {
        revision += 1;
        resetOperationController();
        preview = null;
        previewSurface.hidden = true;
        previewContent.replaceChildren();
        commitButton.disabled = true;
        resultSurface.hidden = true;
        resultContent.replaceChildren();
        clearError();
    }

    function valueText(value) {
        if (value === null || value === undefined || value === "") return "—";
        if (typeof value === "object") return JSON.stringify(value);
        return String(value);
    }

    function appendDetails(host, rows) {
        const list = document.createElement("dl");
        list.className = "sanitized-import-details";
        for (const [label, value] of rows) {
            const term = document.createElement("dt");
            const description = document.createElement("dd");
            term.textContent = label;
            description.textContent = valueText(value);
            list.append(term, description);
        }
        host.append(list);
        return list;
    }

    async function responseJson(response) {
        let value;
        try { value = await response.json(); }
        catch (failure) {
            if (failure instanceof DOMException && failure.name === "AbortError") throw failure;
            throw new ImportRequestError("invalid_response");
        }
        if (!response.ok) throw new ImportRequestError(value && value.error ? value.error : `http_${response.status}`);
        return value;
    }

    async function loadHistory(committedOutcome = false) {
        const currentHistoryRevision = ++historyRevision;
        historyController.abort();
        historyController = new AbortController();
        historyRefresh.disabled = true;
        try {
            const response = await fetch("/api/sanitized-import/v1/imports?limit=50", {
                headers: { Accept: "application/json" },
                cache: "no-store",
                signal: historyController.signal,
            });
            const page = await responseJson(response);
            if (currentHistoryRevision !== historyRevision) return;
            if (errorSource === "history") clearError();
            historyBody.replaceChildren();
            for (const item of page.items) {
                const row = document.createElement("tr");
                for (const value of [item.imported_at, item.import_id, item.new_records, item.duplicate_records, item.status]) {
                    const cell = document.createElement("td");
                    cell.textContent = valueText(value);
                    row.append(cell);
                }
                historyBody.append(row);
            }
            historyEmpty.hidden = page.items.length !== 0;
        } catch (failure) {
            if (currentHistoryRevision === historyRevision
                && !(failure instanceof DOMException && failure.name === "AbortError"))
                showHistoryError(failure instanceof ImportRequestError ? failure.code : "history_unavailable", committedOutcome);
        } finally {
            if (currentHistoryRevision === historyRevision) historyRefresh.disabled = false;
        }
    }

    function renderPreview(value) {
        previewContent.replaceChildren();
        appendDetails(previewContent, [
            ["Bundle", `${value.bundle_profile} / ${value.bundle_schema_version}`],
            ["Archive SHA-256", value.archive_sha256],
            ["Preview digest", value.preview_digest],
            ["移行", `${value.migration.step} / ${value.migration.chain}`],
            ["移行 chain SHA-256", value.migration.chain_sha256],
            ["source snapshot", value.source_snapshot_id],
            ["source Local Monitor", value.source_local_monitor_version],
            ["source labels", value.source_labels],
            ["レコード総数", value.total_records],
            ["新規レコード", value.new_records],
            ["重複レコード", value.duplicate_records],
            ["競合レコード", value.conflict_records],
            ["未解決参照", value.unresolved_reference_count],
            ["追加する origin", value.expected_changes.origins],
            ["追加する graph node", value.expected_changes.graph_nodes],
            ["追加する graph edge", value.expected_changes.graph_edges],
            ["作成する raw 保持項目", value.expected_changes.raw_retention_items],
        ]);
        previewSurface.hidden = false;
        commitButton.disabled = !value.can_commit;
        document.getElementById("sanitized-import-preview-title").focus({ preventScroll: true });
    }

    async function publishPreview() {
        if (!selectedFile) return;
        const currentRevision = revision;
        previewButton.disabled = true;
        commitButton.disabled = true;
        clearError();
        setLive("バンドルを検証し、変更内容を計算しています。");
        try {
            const response = await fetch("/api/sanitized-import/v1/previews", {
                method: "POST",
                headers: { ...csrfHeaders, Accept: "application/json", "Content-Type": "application/zip" },
                body: selectedFile,
                cache: "no-store",
                signal: operationController.signal,
            });
            const value = await responseJson(response);
            if (currentRevision !== revision) return;
            preview = value;
            renderPreview(value);
            setLive(value.can_commit ? "検証が完了しました。内容を確認して確定できます。" : "競合があるため取り込めません。");
        } catch (failure) {
            if (currentRevision === revision
                && !(failure instanceof DOMException && failure.name === "AbortError"))
                showError(failure instanceof ImportRequestError ? failure.code : "preview_unavailable");
        } finally {
            if (currentRevision === revision) previewButton.disabled = !selectedFile;
        }
    }

    function renderResult(value) {
        resultContent.replaceChildren();
        appendDetails(resultContent, [
            ["状態", value.status], ["Import ID", value.import_id], ["Archive SHA-256", value.archive_sha256],
            ["新規レコード", value.new_records], ["重複レコード", value.duplicate_records],
            ["graph node", value.graph_nodes], ["graph edge", value.graph_edges],
            ["raw 保持項目", value.raw_retention_items], ["idempotent replay", value.idempotent_replay],
            ["取り込み日時", value.imported_at],
        ]);
        resultSurface.hidden = false;
        document.getElementById("sanitized-import-result-title").focus({ preventScroll: true });
    }

    async function commitImport() {
        if (!selectedFile || !preview || !preview.can_commit || !preview.preview_digest) return;
        const currentRevision = revision;
        const digest = preview.preview_digest;
        commitPending = true;
        fileInput.disabled = true;
        previewButton.disabled = true;
        commitButton.disabled = true;
        clearError();
        setLive("トランザクションで取り込んでいます。");
        try {
            const response = await fetch("/api/sanitized-import/v1/imports", {
                method: "POST",
                headers: {
                    ...csrfHeaders,
                    Accept: "application/json",
                    "Content-Type": "application/zip",
                    "X-Sanitized-Import-Preview-Digest": digest,
                },
                body: selectedFile,
                cache: "no-store",
                signal: operationController.signal,
            });
            const value = await responseJson(response);
            if (currentRevision !== revision) return;
            preview = null;
            renderResult(value);
            setLive("取り込み結果を確認しました。");
            await loadHistory(true);
        } catch (failure) {
            if (currentRevision !== revision) return;
            if (!(failure instanceof DOMException && failure.name === "AbortError") || !pageLeaving)
                showError(failure instanceof ImportRequestError ? failure.code : "commit_unavailable", true);
        } finally {
            commitPending = false;
            if (!pageLeaving) fileInput.disabled = false;
            if (currentRevision === revision) previewButton.disabled = !selectedFile;
        }
    }

    fileInput.addEventListener("change", () => {
        if (commitPending) return;
        invalidatePreview();
        selectedFile = fileInput.files && fileInput.files.length === 1 ? fileInput.files[0] : null;
        fileName.textContent = selectedFile ? `選択中: ${selectedFile.name}` : "";
        previewButton.disabled = !selectedFile;
        setLive(selectedFile ? "ファイルを選択しました。まだ取り込んでいません。" : "");
    });
    previewButton.addEventListener("click", publishPreview);
    commitButton.addEventListener("click", commitImport);
    historyRefresh.addEventListener("click", () => loadHistory(false));
    window.addEventListener("pagehide", () => {
        pageLeaving = true;
        operationController.abort();
        historyController.abort();
        selectedFile = null;
        preview = null;
    });
    loadHistory();
})();
