(() => {
    "use strict";

    const root = document.getElementById("historical-import-root");
    if (!root) return;

    const workflowVersion = "historical-import-workflow/v1";
    const form = document.getElementById("historical-import-form");
    const source = document.getElementById("historical-import-source");
    const referenceKind = document.getElementById("historical-import-reference-kind");
    const exactReference = document.getElementById("historical-import-reference");
    const sessionField = document.getElementById("historical-import-session-field");
    const sessionId = document.getElementById("historical-import-session-id");
    const sourceVersion = document.getElementById("historical-import-version");
    const consent = document.getElementById("historical-import-consent");
    const scope = document.getElementById("historical-import-scope");
    const previewButton = document.getElementById("historical-import-preview-button");
    const confirmButton = document.getElementById("historical-import-confirm-button");
    const previewSurface = document.getElementById("historical-import-preview");
    const previewBadge = document.getElementById("historical-import-preview-badge");
    const previewDetails = document.getElementById("historical-import-preview-details");
    const progressSurface = document.getElementById("historical-import-progress");
    const resultSurface = document.getElementById("historical-import-result");
    const resultDetails = document.getElementById("historical-import-result-details");
    const live = document.getElementById("historical-import-live");
    const error = document.getElementById("historical-import-error");
    const observationList = document.getElementById("historical-import-observation-list");
    const observationDetail = document.getElementById("historical-import-observation-detail");
    const observationDetailContent = document.getElementById("historical-import-observation-detail-content");
    const traceButton = document.getElementById("historical-import-trace-button");
    const traceUnavailable = document.getElementById("historical-import-trace-unavailable");
    const historyList = document.getElementById("historical-import-history-list");
    const tabs = [...root.querySelectorAll(".historical-import-tab")];
    const sourceCards = [...root.querySelectorAll(".historical-import-source-card")];
    let preview = null;
    let activeImport = false;
    let previewGeneration = 0;
    let previewRequest = null;
    let activeSourceKind = "historical";
    let observationGeneration = 0;
    let observationRequest = null;
    let observationDetailRequest = null;
    let historyGeneration = 0;
    let historyRequest = null;
    let pendingCommitRequest = null;
    let pendingCommitOutcomeAmbiguous = false;

    class HistoricalImportRequestError extends Error {
        constructor(code, location = null) {
            super(code);
            this.code = code;
            this.location = location;
        }
    }

    function setLive(message) {
        live.textContent = message;
    }

    function clearError() {
        error.textContent = "";
        error.hidden = true;
    }

    function showError(code) {
        const messages = {
            historical_import_preview_stale: "プレビューが古くなりました。ソースを選び直して新しいプレビューを作成してください。",
            historical_import_source_changed: "ソースがプレビュー後に変わりました。新しいプレビューが必要です。",
            historical_import_no_eligible_candidates: "現在のソース契約では取り込める候補がありません。",
            historical_import_confirmation_expired: "確認の有効期限が切れました。新しいプレビューが必要です。",
            historical_import_preview_expired: "プレビューの有効期限が切れました。新しいプレビューが必要です。",
            historical_import_store_busy: "履歴インポートの保存先が使用中です。状態を変えずに終了しました。",
            historical_import_transaction_failed: "インポートを確定できませんでした。変更はロールバックされました。",
            request_too_large: "リクエストが上限を超えています。",
            csrf_required: "ローカルモニターの操作ヘッダーを確認できませんでした。",
            cross_origin_forbidden: "別オリジンからの操作は許可されていません。",
        };
        error.textContent = messages[code] || `履歴インポートを完了できませんでした（${code}）。`;
        error.hidden = false;
    }

    async function requestJson(path, options = {}) {
        const response = await fetch(path, {
            cache: "no-store",
            credentials: "same-origin",
            redirect: "error",
            ...options,
        });
        const text = await response.text();
        let body = null;
        try {
            body = text ? JSON.parse(text) : null;
        } catch {
            throw new HistoricalImportRequestError("historical_import_store_unavailable");
        }
        if (!response.ok) {
            const code = body && typeof body.error === "string"
                ? body.error
                : "historical_import_store_unavailable";
            throw new HistoricalImportRequestError(code, response.headers.get("Location"));
        }
        return body;
    }

    function postJson(path, body, signal = undefined) {
        const headers = {
            "Content-Type": "application/json",
            "x-monitor-csrf": "local-monitor",
        };
        return requestJson(path, { method: "POST", headers, body: JSON.stringify(body), signal });
    }

    function newIdentifier(prefix) {
        const bytes = new Uint8Array(16);
        crypto.getRandomValues(bytes);
        return `${prefix}${[...bytes].map(value => value.toString(16).padStart(2, "0")).join("")}`;
    }

    function displayValue(value) {
        if (value === null || value === undefined || value === "") return "—";
        if (Array.isArray(value)) return value.length ? value.join(", ") : "なし";
        if (typeof value === "boolean") return value ? "yes" : "no";
        return String(value);
    }

    function appendDefinitionList(host, rows) {
        const list = document.createElement("dl");
        list.className = "historical-import-definition-list";
        for (const [term, value] of rows) {
            const item = document.createElement("div");
            const dt = document.createElement("dt");
            const dd = document.createElement("dd");
            dt.textContent = term;
            dd.textContent = displayValue(value);
            item.append(dt, dd);
            list.append(item);
        }
        host.append(list);
        return list;
    }

    function appendHeading(host, text) {
        const heading = document.createElement("h3");
        heading.textContent = text;
        host.append(heading);
    }

    function countValue(value) {
        return value && value.availability === "available"
            ? value.value
            : "unavailable";
    }

    function setSourceOptions() {
        for (const card of sourceCards) {
            const selected = card.dataset.sourceSelection === source.value;
            card.classList.toggle("active", selected);
            card.setAttribute("aria-pressed", selected ? "true" : "false");
        }
        referenceKind.textContent = "";
        const placeholder = document.createElement("option");
        placeholder.value = "";
        placeholder.textContent = source.value ? "参照方法を選択" : "ソースを先に選択してください";
        referenceKind.append(placeholder);
        referenceKind.disabled = !source.value;
        exactReference.value = "";
        sessionId.value = "";
        sourceVersion.value = "";
        if (source.value === "github-copilot-cli") {
            const option = document.createElement("option");
            option.value = "selected_root";
            option.textContent = "選択した公式 session-state root";
            referenceKind.append(option);
            referenceKind.value = "selected_root";
            sessionField.hidden = false;
            sessionId.required = true;
            scope.textContent = "選択 root 内の session-state/{Session ID}/events.jsonl のファイルメタデータだけを確認します。";
        } else if (source.value === "claude-code") {
            for (const [value, text] of [["official_hook", "公式 hook の明示参照"], ["explicit_user_selection", "ユーザーが明示選択した参照"]]) {
                const option = document.createElement("option");
                option.value = value;
                option.textContent = text;
                referenceKind.append(option);
            }
            referenceKind.value = "official_hook";
            sessionField.hidden = true;
            sessionId.required = false;
            scope.textContent = "選択した正確なファイルのメタデータだけを確認します。";
        } else {
            sessionField.hidden = true;
            sessionId.required = false;
            scope.textContent = "選択した正確な参照だけを確認します。";
        }
        invalidateSelection();
    }

    function hasCompleteSelection() {
        return Boolean(source.value
            && referenceKind.value
            && exactReference.value.trim()
            && sourceVersion.value.trim()
            && (source.value !== "github-copilot-cli" || sessionId.value.trim()));
    }

    function invalidateSelection() {
        consent.checked = false;
        consent.disabled = !hasCompleteSelection();
        invalidatePreview();
    }

    function setSelectionLocked(locked) {
        source.disabled = locked;
        referenceKind.disabled = locked || !source.value;
        exactReference.disabled = locked;
        sessionId.disabled = locked;
        sourceVersion.disabled = locked;
        consent.disabled = locked || !hasCompleteSelection();
        for (const card of sourceCards) card.disabled = locked;
    }

    function invalidatePreview() {
        previewGeneration += 1;
        if (previewRequest) {
            previewRequest.abort();
            setLive("入力が変更されたため、進行中のプレビューを破棄しました。");
        }
        preview = null;
        previewSurface.hidden = true;
        progressSurface.hidden = true;
        resultSurface.hidden = true;
        confirmButton.disabled = true;
        clearError();
    }

    function renderPreview(value) {
        previewDetails.textContent = "";
        previewBadge.textContent = value.source_badge === "historical" ? "Historical" : "Unsupported";
        previewBadge.className = `historical-import-badge badge-${value.source_badge === "historical" ? "historical" : "unsupported"}`;
        const format = value.source_format || {};
        const range = value.source_time_range || {};
        appendDefinitionList(previewDetails, [
            ["ソース種別", value.source_kind],
            ["ソース", value.source_surface],
            ["Tier", value.source_tier],
            ["Profile", value.profile_id],
            ["Adapter", value.adapter_id],
            ["Adapter 状態", value.adapter_state],
            ["Adapter 診断", value.adapter_diagnostics],
            ["Evidence", value.evidence_status],
            ["アプリケーション版", value.source_application_version],
            ["Format", format.name],
            ["Format 版", format.version],
            ["読み取り範囲", value.requested_capture],
            ["ソース日時", range.availability],
            ["コンテンツリスク", value.content_risk],
            ["完全性の上限", value.completeness_ceiling],
            ["完全性理由", value.completeness_reasons],
            ["不足 capability", value.missing_capabilities],
        ]);
        appendHeading(previewDetails, "候補と決定");
        const counts = value.counts || {};
        appendDefinitionList(previewDetails, [
            ["合計", countValue(counts.total)],
            ["適格", countValue(counts.eligible)],
            ["未対応", countValue(counts.unsupported)],
            ["不正", countValue(counts.malformed)],
            ["重複", countValue(counts.duplicates)],
            ["競合", countValue(counts.conflicts)],
            ["新しい observation", countValue(counts.new_observations)],
            ["新しい Session", countValue(counts.new_sessions)],
            ["新しい Event", countValue(counts.new_events)],
            ["merge candidate", countValue(counts.merge_candidates)],
            ["除外", countValue(counts.excluded)],
        ]);
        const mergeRows = (value.merge_candidates || []).map(candidate =>
            `${candidate.binding_basis}: ${candidate.merge_candidate_id}`);
        const exclusions = (value.exclusions || []).map(item => `${item.code}: ${item.count}`);
        const retention = value.retention_impact || {};
        appendHeading(previewDetails, "Merge・保持・除外");
        appendDefinitionList(previewDetails, [
            ["正確な merge basis", mergeRows],
            ["保持 disposition", retention.disposition],
            ["作成する raw item", retention.created_item_count],
            ["自動 pin", retention.automatic_pin],
            ["除外理由", exclusions],
            ["commit allowed", value.commit_allowed],
            ["拒否コード", value.rejection_code],
        ]);
        previewSurface.hidden = false;
        resultSurface.hidden = true;
        progressSurface.hidden = true;
        confirmButton.disabled = !value.commit_allowed;
        document.getElementById("historical-import-preview-heading").focus({ preventScroll: true });
    }

    async function createPreview(event) {
        event.preventDefault();
        if (activeImport || previewRequest || !form.reportValidity()) return;
        clearError();
        const privateReference = exactReference.value.trim();
        const privateSessionId = source.value === "github-copilot-cli" ? sessionId.value.trim() : null;
        const body = {
            contract_version: workflowVersion,
            schema_version: "historical-import-workflow-source-selection/v1",
            source_surface: source.value,
            reference_kind: referenceKind.value,
            exact_reference: privateReference,
            session_id: privateSessionId,
            source_application_version: sourceVersion.value.trim(),
            requested_capture: "metadata_only",
            consent_granted: consent.checked,
        };
        const requestGeneration = ++previewGeneration;
        const controller = new AbortController();
        previewRequest = controller;
        exactReference.value = "";
        sessionId.value = "";
        consent.checked = false;
        consent.disabled = true;
        previewButton.disabled = true;
        setLive("選択したソースのメタデータを一度だけ確認しています。");
        try {
            const response = await postJson("/api/historical-import/v1/previews", body, controller.signal);
            if (controller.signal.aborted || requestGeneration !== previewGeneration) return;
            preview = response;
            renderPreview(response);
            setLive(response.commit_allowed
                ? "プレビューを表示しました。内容を確認してから確定してください。"
                : "プレビューを表示しました。現在のソース契約では確定できません。");
        } catch (failure) {
            if (controller.signal.aborted
                || requestGeneration !== previewGeneration
                || failure?.name === "AbortError") return;
            preview = null;
            confirmButton.disabled = true;
            showError(failure instanceof HistoricalImportRequestError ? failure.code : "historical_import_store_unavailable");
            setLive("プレビューを作成できませんでした。インポートは開始していません。");
        } finally {
            if (previewRequest === controller) {
                previewRequest = null;
                previewButton.disabled = false;
            }
        }
    }

    async function recoverOperation(failure) {
        if (!(failure instanceof HistoricalImportRequestError)
            || !failure.location) return null;
        let statusPath;
        try {
            const resolved = new URL(failure.location, window.location.href);
            if (resolved.origin !== window.location.origin
                || resolved.search
                || resolved.hash
                || !/^\/api\/historical-import\/v1\/imports\/hop_[0-9a-f]{32}$/.test(resolved.pathname)) return null;
            statusPath = resolved.pathname;
        } catch {
            return null;
        }
        let status;
        for (let attempt = 0; attempt < 4; attempt += 1) {
            status = await requestJson(statusPath);
            if (status.result_available || !["queued", "running"].includes(status.state)) break;
            if (attempt < 3) await new Promise(resolve => window.setTimeout(resolve, 250));
        }
        if (!status.result_available) {
            if (["queued", "running"].includes(status.state)) return { outcome: "pending" };
            return {
                outcome: "terminal",
                failure: new HistoricalImportRequestError(
                    status.failure_code || "historical_import_transaction_failed",
                    statusPath),
            };
        }
        try {
            const result = await requestJson(`${statusPath}/result`);
            renderResult(result);
            const observationRefresh = activeSourceKind === "historical"
                ? selectTab("historical")
                : Promise.resolve();
            await Promise.allSettled([observationRefresh, loadHistory()]);
            return { outcome: "recovered" };
        } catch (resultFailure) {
            return {
                outcome: "terminal",
                failure: resultFailure instanceof HistoricalImportRequestError
                    ? resultFailure
                    : new HistoricalImportRequestError("historical_import_store_unavailable", statusPath),
            };
        }
    }

    function isAmbiguousCommitFailure(failure) {
        return !(failure instanceof HistoricalImportRequestError)
            || failure.code === "historical_import_store_unavailable";
    }

    function canStillBeInFlight(failure) {
        return !(failure instanceof HistoricalImportRequestError)
            || [
                "historical_import_store_busy",
                "historical_import_store_unavailable",
                "historical_import_result_not_available",
                "historical_import_confirmation_consumed",
            ].includes(failure.code);
    }

    async function submitPendingCommit() {
        try {
            return await postJson("/api/historical-import/v1/imports", pendingCommitRequest);
        } catch (failure) {
            if (!isAmbiguousCommitFailure(failure)) throw failure;
            pendingCommitOutcomeAmbiguous = true;
            return postJson("/api/historical-import/v1/imports", pendingCommitRequest);
        }
    }

    async function confirmImport() {
        if (activeImport || (!pendingCommitRequest && (!preview || !preview.commit_allowed))) return;
        const previewBinding = preview;
        activeImport = true;
        setSelectionLocked(true);
        confirmButton.disabled = true;
        previewButton.disabled = true;
        clearError();
        progressSurface.hidden = false;
        resultSurface.hidden = true;
        setLive("明示的な確認を発行しています。");
        try {
            if (!pendingCommitRequest) {
                const confirmation = await postJson("/api/historical-import/v1/confirmations", {
                    contract_version: workflowVersion,
                    schema_version: "historical-import-workflow-confirmation-request/v1",
                    preview_id: previewBinding.preview_id,
                    preview_digest: previewBinding.preview_digest,
                    snapshot_version: previewBinding.snapshot_version,
                    decision: "confirm",
                });
                pendingCommitRequest = {
                    contract_version: workflowVersion,
                    schema_version: "historical-import-workflow-import-request/v1",
                    request_id: newIdentifier("hir_"),
                    idempotency_key: newIdentifier("hik_"),
                    confirmation_id: confirmation.confirmation_id,
                    preview_id: previewBinding.preview_id,
                    preview_digest: previewBinding.preview_digest,
                    snapshot_version: previewBinding.snapshot_version,
                };
            }
            setLive("トランザクションを実行しています。");
            const result = await submitPendingCommit();
            pendingCommitRequest = null;
            pendingCommitOutcomeAmbiguous = false;
            renderResult(result);
            setLive("確定したインポート結果を表示しました。");
            const observationRefresh = activeSourceKind === "historical"
                ? selectTab("historical")
                : Promise.resolve();
            await Promise.allSettled([observationRefresh, loadHistory()]);
        } catch (failure) {
            let finalFailure = failure;
            try {
                const recovery = await recoverOperation(failure);
                if (recovery?.outcome === "recovered") {
                    pendingCommitRequest = null;
                    pendingCommitOutcomeAmbiguous = false;
                    setLive("確定済みのインポート結果を復元しました。");
                    return;
                }
                if (recovery?.outcome === "pending") pendingCommitOutcomeAmbiguous = true;
                if (recovery?.outcome === "terminal") {
                    finalFailure = recovery.failure;
                    pendingCommitRequest = null;
                    pendingCommitOutcomeAmbiguous = false;
                }
            } catch (recoveryFailure) {
                finalFailure = recoveryFailure;
                pendingCommitOutcomeAmbiguous = true;
            }
            progressSurface.hidden = true;
            if (pendingCommitRequest
                && (isAmbiguousCommitFailure(finalFailure)
                    || (pendingCommitOutcomeAmbiguous && canStillBeInFlight(finalFailure)))) {
                showError("historical_import_store_unavailable");
                confirmButton.disabled = false;
                setLive("応答を確認できませんでした。同じリクエストを再送して結果を復元できます。");
                return;
            }
            pendingCommitRequest = null;
            pendingCommitOutcomeAmbiguous = false;
            preview = null;
            showError(finalFailure instanceof HistoricalImportRequestError ? finalFailure.code : "historical_import_store_unavailable");
            setLive("インポートを確定できませんでした。新しいプレビューからやり直してください。");
        } finally {
            activeImport = false;
            setSelectionLocked(pendingCommitRequest !== null);
            previewButton.disabled = pendingCommitRequest !== null;
            if (pendingCommitRequest) confirmButton.disabled = false;
        }
    }

    function renderResult(value) {
        resultDetails.textContent = "";
        appendDefinitionList(resultDetails, [
            ["状態", value.outcome],
            ["トランザクション", value.transaction_outcome],
            ["idempotency", value.idempotency_outcome],
            ["Operation ID", value.operation_id],
            ["ソース", value.source_surface],
            ["Profile", value.profile_id],
            ["Adapter", value.adapter_id],
            ["Evidence", value.evidence_status],
            ["新しい observation", countValue(value.counts?.new_observations)],
            ["新しい Session", countValue(value.counts?.new_sessions)],
            ["新しい Event", countValue(value.counts?.new_events)],
            ["重複", countValue(value.counts?.duplicates)],
            ["競合", countValue(value.counts?.conflicts)],
            ["record rejection", countValue(value.counts?.record_rejections)],
            ["保持", value.retention?.disposition],
            ["自動 pin", value.retention?.pin_state],
        ]);
        appendHeading(resultDetails, "観測の完全性");
        for (const observation of value.observations || []) {
            appendDefinitionList(resultDetails, [
                ["Observation ID", observation.observation_id],
                ["identity", observation.identity_resolution],
                ["binding basis", observation.binding_basis],
                ["完全性", observation.completeness],
                ["理由", observation.completeness_reasons],
                ["不足 capability", observation.missing_capabilities],
                ["content", observation.content_state],
            ]);
        }
        progressSurface.hidden = true;
        previewSurface.hidden = true;
        resultSurface.hidden = false;
        preview = null;
        document.getElementById("historical-import-result-heading").focus({ preventScroll: true });
    }

    function createBadge(text, className) {
        const badge = document.createElement("span");
        badge.className = `historical-import-badge ${className}`;
        badge.textContent = text;
        return badge;
    }

    function renderEmptyList(message) {
        const empty = document.createElement("p");
        empty.className = "monitor-subtle";
        empty.textContent = message;
        observationList.append(empty);
    }

    async function loadHistoricalObservations(generation, controller) {
        try {
            const response = await requestJson(
                "/api/historical-import/v1/observations?limit=50",
                { signal: controller.signal });
            if (controller.signal.aborted
                || generation !== observationGeneration
                || activeSourceKind !== "historical") return;
            if (!response.items || response.items.length === 0) {
                renderEmptyList("Historical observation はまだありません。");
                return;
            }
            for (const item of response.items) {
                const card = document.createElement("article");
                card.className = "historical-import-observation-card";
                const heading = document.createElement("div");
                heading.className = "historical-import-observation-head";
                heading.append(createBadge("Historical", "badge-historical"));
                const id = document.createElement("code");
                id.textContent = item.observation_id;
                heading.append(id);
                card.append(heading);
                appendDefinitionList(card, [
                    ["ソース", item.source_surface],
                    ["完全性", item.completeness],
                    ["理由", item.completeness_reasons],
                    ["不足 capability", item.missing_capabilities],
                    ["content", item.content_state],
                ]);
                const detailButton = document.createElement("button");
                detailButton.className = "monitor-btn";
                detailButton.type = "button";
                detailButton.textContent = "詳細を表示";
                detailButton.addEventListener("click", () => loadObservationDetail(item.observation_id));
                card.append(detailButton);
                observationList.append(card);
            }
        } catch (failure) {
            if (controller.signal.aborted
                || generation !== observationGeneration
                || activeSourceKind !== "historical"
                || failure?.name === "AbortError") return;
            renderEmptyList(`Historical observation を読み込めませんでした（${failure.code || "historical_import_store_unavailable"}）。`);
        } finally {
            if (observationRequest === controller) observationRequest = null;
        }
    }

    async function loadObservationDetail(observationId) {
        clearError();
        observationDetailRequest?.abort();
        const generation = observationGeneration;
        const controller = new AbortController();
        observationDetailRequest = controller;
        try {
            const value = await requestJson(
                `/api/historical-import/v1/observations/${encodeURIComponent(observationId)}`,
                { signal: controller.signal });
            if (controller.signal.aborted
                || generation !== observationGeneration
                || activeSourceKind !== "historical") return;
            observationDetailContent.textContent = "";
            appendDefinitionList(observationDetailContent, [
                ["Observation ID", value.observation_id],
                ["ソース", value.source_surface],
                ["Profile", value.profile_id],
                ["Adapter", value.adapter_id],
                ["identity", value.identity_resolution],
                ["binding basis", value.binding_basis],
                ["完全性", value.completeness],
                ["理由", value.completeness_reasons],
                ["不足 capability", value.missing_capabilities],
                ["summary fields", value.summary_fields],
                ["content", value.content_state],
                ["保持", value.retention_disposition],
            ]);
            traceButton.disabled = !value.trace_controls_enabled;
            traceUnavailable.textContent = value.trace_controls_enabled
                ? "正確な既存 Session のナビゲーションを利用できます。"
                : (value.missing_capabilities || []).includes("trace_identity")
                    ? "trace_identity がありません。Historical summary から trace を合成しません。"
                    : "Historical binding はナビゲーション専用です。この画面では trace 操作を有効にしません。";
            observationDetail.hidden = false;
        } catch (failure) {
            if (controller.signal.aborted
                || generation !== observationGeneration
                || activeSourceKind !== "historical"
                || failure?.name === "AbortError") return;
            showError(failure instanceof HistoricalImportRequestError ? failure.code : "historical_import_store_unavailable");
        } finally {
            if (observationDetailRequest === controller) observationDetailRequest = null;
        }
    }

    async function loadLiveSessions(generation, controller) {
        try {
            const response = await requestJson(
                "/api/session-workspace/sessions?limit=50",
                { signal: controller.signal });
            if (controller.signal.aborted
                || generation !== observationGeneration
                || activeSourceKind !== "live") return;
            if (!response.items || response.items.length === 0) {
                renderEmptyList("Live Session はまだありません。");
                return;
            }
            for (const item of response.items) {
                const card = document.createElement("article");
                card.className = "historical-import-observation-card";
                const heading = document.createElement("div");
                heading.className = "historical-import-observation-head";
                const surfaces = item.source_surfaces || [];
                if (item.binding_state === "otel_only" || item.binding_state === "exact_linked")
                    heading.append(createBadge("Live OTel", "badge-live"));
                if (item.binding_state === "hook_only" || item.binding_state === "exact_linked")
                    heading.append(createBadge("Hook / SDK", "badge-hook"));
                if (item.raw_retention_state && item.raw_retention_state !== "not_captured")
                    heading.append(createBadge("Saved raw", "badge-saved"));
                const id = document.createElement("code");
                id.textContent = item.session_id;
                heading.append(id);
                card.append(heading);
                appendDefinitionList(card, [
                    ["状態", item.status],
                    ["完全性", item.completeness],
                    ["binding", item.binding_state],
                    ["ソース", surfaces],
                    ["content", item.content_state],
                ]);
                const detailLink = document.createElement("a");
                detailLink.className = "monitor-btn";
                detailLink.textContent = "Session 詳細を開く";
                detailLink.href = `/diagnostics?session_id=${encodeURIComponent(item.session_id)}#doctor-session`;
                card.append(detailLink);
                observationList.append(card);
            }
        } catch (failure) {
            if (controller.signal.aborted
                || generation !== observationGeneration
                || activeSourceKind !== "live"
                || failure?.name === "AbortError") return;
            renderEmptyList("Live Session を読み込めませんでした。");
        } finally {
            if (observationRequest === controller) observationRequest = null;
        }
    }

    async function loadHistory() {
        const generation = ++historyGeneration;
        historyRequest?.abort();
        const controller = new AbortController();
        historyRequest = controller;
        try {
            const response = await requestJson(
                "/api/historical-import/v1/history?limit=50",
                { signal: controller.signal });
            if (controller.signal.aborted || generation !== historyGeneration) return;
            historyList.textContent = "";
            if (!response.items || response.items.length === 0) {
                const empty = document.createElement("p");
                empty.className = "monitor-subtle";
                empty.textContent = "インポート履歴はまだありません。";
                historyList.append(empty);
                return;
            }
            for (const item of response.items) {
                const card = document.createElement("article");
                card.className = "historical-import-history-card";
                card.append(createBadge(item.source_badge === "historical" ? "Historical" : "Unsupported",
                    item.source_badge === "historical" ? "badge-historical" : "badge-unsupported"));
                appendDefinitionList(card, [
                    ["Operation ID", item.operation_id],
                    ["状態", item.state],
                    ["結果", item.outcome],
                    ["ソース", item.source_surface],
                    ["Profile", item.profile_id],
                    ["Adapter", item.adapter_id],
                    ["新しい observation", item.new_observation_count],
                    ["重複", item.duplicate_count],
                    ["競合", item.conflict_count],
                    ["完全性", item.completeness],
                    ["理由", item.completeness_reasons],
                    ["content", item.content_state],
                    ["保持", item.retention_disposition],
                ]);
                historyList.append(card);
            }
        } catch (failure) {
            if (controller.signal.aborted
                || generation !== historyGeneration
                || failure?.name === "AbortError") return;
            historyList.textContent = "";
            const unavailable = document.createElement("p");
            unavailable.className = "monitor-subtle";
            unavailable.textContent = "インポート履歴を読み込めませんでした。";
            historyList.append(unavailable);
        } finally {
            if (historyRequest === controller) historyRequest = null;
        }
    }

    function selectTab(kind) {
        if (kind !== "live" && kind !== "historical") return Promise.resolve();
        activeSourceKind = kind;
        observationGeneration += 1;
        observationRequest?.abort();
        observationDetailRequest?.abort();
        observationDetailRequest = null;
        observationDetail.hidden = true;
        observationList.textContent = "";
        for (const tab of tabs) {
            const selected = tab.dataset.sourceKind === kind;
            tab.classList.toggle("active", selected);
            tab.setAttribute("aria-selected", selected ? "true" : "false");
        }
        const generation = observationGeneration;
        const controller = new AbortController();
        observationRequest = controller;
        return kind === "live"
            ? loadLiveSessions(generation, controller)
            : loadHistoricalObservations(generation, controller);
    }

    source.addEventListener("change", setSourceOptions);
    for (const card of sourceCards) {
        card.addEventListener("click", () => {
            if (activeImport) return;
            source.value = card.dataset.sourceSelection;
            source.dispatchEvent(new Event("change", { bubbles: true }));
        });
    }
    referenceKind.addEventListener("change", invalidateSelection);
    exactReference.addEventListener("input", invalidateSelection);
    sessionId.addEventListener("input", invalidateSelection);
    sourceVersion.addEventListener("input", invalidateSelection);
    consent.addEventListener("change", invalidatePreview);
    form.addEventListener("submit", createPreview);
    confirmButton.addEventListener("click", confirmImport);
    for (const tab of tabs) tab.addEventListener("click", () => selectTab(tab.dataset.sourceKind));
    window.addEventListener("pagehide", () => {
        previewGeneration += 1;
        previewRequest?.abort();
        previewRequest = null;
        observationGeneration += 1;
        observationRequest?.abort();
        observationRequest = null;
        observationDetailRequest?.abort();
        observationDetailRequest = null;
        historyGeneration += 1;
        historyRequest?.abort();
        historyRequest = null;
        preview = null;
        exactReference.value = "";
        sessionId.value = "";
    });

    setSourceOptions();
    Promise.allSettled([selectTab("historical"), loadHistory()]);
})();
