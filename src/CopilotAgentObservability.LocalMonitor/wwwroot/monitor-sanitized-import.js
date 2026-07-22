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
    const maximumDetailRows = 256;

    class ImportRequestError extends Error {
        constructor(code, rejected = false) {
            super(code);
            this.code = code;
            this.rejected = rejected;
        }
    }

    function setLive(message) { live.textContent = message; }
    function clearError() { error.hidden = true; error.textContent = ""; errorSource = null; }
    function showError(code, commitOutcome = null) {
        error.textContent = commitOutcome === "ambiguous"
            ? `確定結果を確認できませんでした（${code}）。自動再送はしていません。履歴を再取得してください。`
            : commitOutcome === "rejected"
                ? `取り込みは確定されませんでした（${code}）。ファイルと表示内容を確認してください。`
                : `処理できませんでした（${code}）。ファイルと表示内容を確認してください。`;
        error.hidden = false;
        errorSource = "operation";
        setLive(commitOutcome === "rejected" ? "取り込みは確定されませんでした。" : "処理は完了していません。");
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

    function listText(values, projection = valueText) {
        return Array.isArray(values) && values.length !== 0
            ? values.map(projection).join(" / ")
            : "—";
    }

    function mapText(value) {
        if (!value || typeof value !== "object") return "—";
        const entries = Object.entries(value);
        return entries.length === 0 ? "—" : entries.map(([key, item]) => `${key}: ${valueText(item)}`).join(" / ");
    }

    function appendSectionTitle(host, text) {
        const heading = document.createElement("h4");
        heading.className = "sanitized-import-detail-title";
        heading.textContent = text;
        host.append(heading);
    }

    function appendBoundedTable(host, title, total, values, columns) {
        const rows = Array.isArray(values) ? values.slice(0, maximumDetailRows) : [];
        appendSectionTitle(host, `${title}（表示 ${rows.length} / ${valueText(total)}）`);
        if (rows.length === 0) {
            const empty = document.createElement("p");
            empty.className = "monitor-subtle";
            empty.textContent = "該当する詳細はありません。";
            host.append(empty);
            return;
        }
        const wrapper = document.createElement("div");
        wrapper.className = "monitor-table-wrapper";
        const table = document.createElement("table");
        table.className = "monitor-table";
        const head = document.createElement("thead");
        const headRow = document.createElement("tr");
        for (const [label] of columns) {
            const cell = document.createElement("th");
            cell.textContent = label;
            headRow.append(cell);
        }
        head.append(headRow);
        const body = document.createElement("tbody");
        for (const value of rows) {
            const row = document.createElement("tr");
            for (const [, key] of columns) {
                const cell = document.createElement("td");
                cell.textContent = valueText(value && value[key]);
                row.append(cell);
            }
            body.append(row);
        }
        table.append(head, body);
        wrapper.append(table);
        host.append(wrapper);
    }

    async function responseJson(response) {
        let value;
        try { value = await response.json(); }
        catch (failure) {
            if (failure instanceof DOMException && failure.name === "AbortError") throw failure;
            throw new ImportRequestError("invalid_response");
        }
        if (!response.ok) throw new ImportRequestError(value && value.error ? value.error : `http_${response.status}`, true);
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
                for (const value of [
                    item.imported_at, item.import_id, item.eligible_records, item.new_records, item.updated_records,
                    item.skipped_records, item.rejected_records, item.duplicate_records, item.conflict_records,
                    item.graph_state_updates, item.status,
                ]) {
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
        appendSectionTitle(previewContent, "バンドルと検証");
        appendDetails(previewContent, [
            ["Bundle", `${value.bundle_profile} / ${value.bundle_schema_version}`],
            ["Manifest schema", value.manifest_schema_version],
            ["互換性", value.compatibility],
            ["Archive SHA-256", value.archive_sha256],
            ["Preview digest", value.preview_digest],
            ["provenance 検証範囲", "内部整合性のみ。署名・出所・認可・source-store provenance は未証明"],
        ]);
        appendSectionTitle(previewContent, "source と provenance");
        appendDetails(previewContent, [
            ["source snapshot", value.source_snapshot_id],
            ["source Local Monitor", value.source_local_monitor_version],
            ["source 作成日時", value.source_created_at],
            ["source agent versions", listText(value.source_agent_versions,
                item => `${valueText(item && item.source_surface)} / ${valueText(item && item.version)}`)],
            ["source date range", value.date_range
                ? `${valueText(value.date_range.start)} .. ${valueText(value.date_range.end)}`
                : null],
            ["選択 Session", listText(value.selection && value.selection.session_ids)],
            ["選択 trace", listText(value.selection && value.selection.trace_ids)],
            ["選択 source surface", listText(value.selection && value.selection.source_surfaces)],
            ["選択 repository", listText(value.selection && value.selection.repository_names)],
            ["選択 workspace", listText(value.selection && value.selection.workspace_labels)],
            ["選択 receipt type", listText(value.selection && value.selection.receipt_types)],
            ["選択期間", value.selection
                ? `${valueText(value.selection.start_inclusive)} .. ${valueText(value.selection.end_exclusive)}`
                : null],
            ["source labels", listText(value.source_labels,
                item => `${valueText(item && item.repository_name)} / ${valueText(item && item.workspace_label)} / ${valueText(item && item.repo_snapshot)}`)],
        ]);
        appendSectionTitle(previewContent, "capability と処理状態");
        appendDetails(previewContent, [
            ["instruction findings", value.capabilities && value.capabilities.instruction_findings],
            ["alert receipts", value.capabilities && value.capabilities.alert_receipts],
            ["historical instruction analysis", value.capabilities && value.capabilities.historical_instruction_analysis],
            ["historical efficiency analysis", value.capabilities && value.capabilities.historical_efficiency_analysis],
            ["alert center", value.capabilities && value.capabilities.alert_center],
            ["record counts", mapText(value.record_counts)],
            ["completeness", mapText(value.completeness_distribution)],
            ["content state", mapText(value.content_state_distribution)],
            ["retention state", mapText(value.retention_state_distribution)],
            ["processing versions", mapText(value.processing_versions)],
        ]);
        appendSectionTitle(previewContent, "移行");
        appendDetails(previewContent, [
            ["移行 version", value.migration && value.migration.version],
            ["移行 step", value.migration && value.migration.step],
            ["移行 chain", value.migration && value.migration.chain],
            ["移行 chain SHA-256", value.migration && value.migration.chain_sha256],
            ["lossy", value.migration && value.migration.lossy],
        ]);
        appendSectionTitle(previewContent, "変更内容");
        appendDetails(previewContent, [
            ["レコード総数", value.total_records],
            ["展開後 byte 数", value.total_uncompressed_bytes],
            ["対象レコード", value.eligible_records],
            ["新規レコード", value.new_records],
            ["更新レコード", value.updated_records],
            ["スキップレコード", value.skipped_records],
            ["拒否レコード", value.rejected_records],
            ["重複レコード", value.duplicate_records],
            ["競合レコード", value.conflict_records],
            ["graph state 更新", value.graph_state_updates],
            ["manifest 宣言", value.manifest_declaration_count],
            ["未解決参照", value.unresolved_reference_count],
            ["追加する record", value.expected_changes.records],
            ["追加する origin", value.expected_changes.origins],
            ["追加する graph node", value.expected_changes.graph_nodes],
            ["追加する graph declaration", value.expected_changes.graph_declarations],
            ["追加する graph state update", value.expected_changes.graph_state_updates],
            ["追加する graph edge", value.expected_changes.graph_edges],
            ["追加する history row", value.expected_changes.history_rows],
            ["作成する raw 保持項目", value.expected_changes.raw_retention_items],
        ]);
        appendBoundedTable(previewContent, "競合の詳細", value.conflict_records, value.conflicts, [
            ["record type", "record_type"], ["record ID", "record_id"],
            ["incoming SHA-256", "incoming_sha256"], ["existing SHA-256", "existing_sha256"],
        ]);
        appendBoundedTable(previewContent, "manifest の missing / external 宣言", value.manifest_declaration_count, value.manifest_declarations, [
            ["node kind", "node_kind"], ["source ID", "source_id"], ["宣言状態", "state"],
        ]);
        appendBoundedTable(previewContent, "取り込み先で現在未解決の参照", value.unresolved_reference_count, value.unresolved_references, [
            ["node kind", "node_kind"], ["source ID", "source_id"], ["状態", "state"],
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
            ["対象レコード", value.eligible_records], ["新規レコード", value.new_records],
            ["更新レコード", value.updated_records], ["スキップレコード", value.skipped_records],
            ["拒否レコード", value.rejected_records], ["重複レコード", value.duplicate_records],
            ["競合レコード", value.conflict_records], ["graph node", value.graph_nodes],
            ["graph declaration", value.graph_declarations], ["graph state 更新", value.graph_state_updates],
            ["graph edge", value.graph_edges],
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
                showError(failure instanceof ImportRequestError ? failure.code : "commit_unavailable",
                    failure instanceof ImportRequestError && failure.rejected ? "rejected" : "ambiguous");
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
