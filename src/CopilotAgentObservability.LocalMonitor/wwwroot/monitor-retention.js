"use strict";

(() => {
    const retentionRoot = document.getElementById("retention-root");
    const diagnosticsItems = document.getElementById("retention-diagnostics-items");
    const csrfHeaders = { "Content-Type": "application/json", "x-monitor-csrf": "local-monitor" };
    const knownErrors = new Set([
        "retention_mutation_request_invalid", "retention_target_not_found", "retention_mutation_target_limit_exceeded",
        "retention_preview_not_found", "retention_idempotency_key_invalid", "retention_idempotency_conflict",
        "retention_idempotency_expired", "retention_operation_not_found", "retention_catalog_unavailable",
        "retention_target_not_applicable", "retention_pin_read_denied", "retention_pin_deleting", "retention_pin_deleted",
        "retention_unpin_read_denied", "retention_unpin_deleting", "retention_unpin_deleted",
        "retention_delete_already_deleting", "retention_delete_already_deleted", "retention_delete_failed",
        "retention_target_empty", "retention_preview_expired", "retention_preview_digest_mismatch",
        "retention_confirmation_generation_failed", "retention_confirmation_consumed", "retention_confirmation_invalid",
        "retention_confirmation_expired", "retention_confirmation_binding_mismatch", "retention_confirmation_target_changed",
        "retention_confirmation_pin_changed", "retention_confirmation_retention_changed", "retention_confirmation_conflict_changed",
        "retention_confirmation_version_changed", "retention_pin_expired", "retention_mutation_transaction_failed",
        "retention_audit_write_failed", "retention_delete_already_queued", "retention_backup_not_purged",
        "session_not_found", "cross_origin_forbidden", "csrf_required", "unsupported_media_type", "request_too_large",
        "retention_network_unavailable", "retention_invalid_response", "retention_request_cancelled", "retention_unknown_error",
    ]);

    class RetentionRequestError extends Error {
        constructor(code, location) {
            super("retention_request_failed");
            this.code = knownErrors.has(code) ? code : "retention_unknown_error";
            this.location = typeof location === "string" ? location : null;
        }
    }

    function valueText(value) {
        if (value === null || value === undefined || value === "") return "—";
        if (typeof value === "boolean") return value ? "はい" : "いいえ";
        return String(value);
    }

    function appendDefinitionList(host, rows, className = "retention-details") {
        const list = document.createElement("dl");
        list.className = className;
        for (const [label, value] of rows) {
            const term = document.createElement("dt");
            const detail = document.createElement("dd");
            term.textContent = label;
            detail.textContent = valueText(value);
            list.append(term, detail);
        }
        host.append(list);
        return list;
    }

    function appendHeading(host, text) {
        const heading = document.createElement("h4");
        heading.textContent = text;
        host.append(heading);
    }

    function appendTable(host, headings, items, fields) {
        const wrapper = document.createElement("div");
        wrapper.className = "monitor-table-wrapper retention-table-wrapper";
        const table = document.createElement("table");
        table.className = "monitor-table";
        const head = document.createElement("thead");
        const headRow = document.createElement("tr");
        for (const heading of headings) {
            const cell = document.createElement("th");
            cell.textContent = heading;
            headRow.append(cell);
        }
        head.append(headRow);
        const body = document.createElement("tbody");
        for (const item of Array.isArray(items) ? items : []) {
            const row = document.createElement("tr");
            for (const field of fields) {
                const cell = document.createElement("td");
                cell.textContent = valueText(item && item[field]);
                row.append(cell);
            }
            body.append(row);
        }
        table.append(head, body);
        wrapper.append(table);
        host.append(wrapper);
    }

    function lifecycleRows(counts) {
        const value = counts || {};
        return [
            ["有効期限内", value.expiring],
            ["ポリシー保持", value.retained_by_policy],
            ["削除待ち", value.expired_pending_deletion],
            ["削除キュー", value.deletion_queued],
            ["削除中", value.deleting],
            ["削除済み", value.deleted],
            ["削除失敗", value.deletion_failed],
        ];
    }

    async function requestJson(path, init = {}) {
        let response;
        try {
            response = await fetch(path, { cache: "no-store", credentials: "same-origin", ...init });
        } catch {
            throw new RetentionRequestError(init.signal?.aborted ? "retention_request_cancelled" : "retention_network_unavailable", null);
        }

        let payload = null;
        try {
            payload = await response.json();
        } catch {
            throw new RetentionRequestError("retention_invalid_response", null);
        }
        if (!response.ok) {
            throw new RetentionRequestError(payload && payload.error, response.headers.get("Location"));
        }
        return payload;
    }

    function createWorkflowKey() {
        const bytes = new Uint8Array(32);
        crypto.getRandomValues(bytes);
        const encoded = btoa(String.fromCharCode(...bytes)).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");
        return `rid1_${encoded}`;
    }

    async function renderRetentionDiagnostics() {
        const summary = document.getElementById("retention-diagnostics-summary");
        const error = document.getElementById("retention-diagnostics-error");
        try {
            const status = await requestJson("/api/retention/v1/status");
            diagnosticsItems.textContent = "";
            const items = Array.isArray(status.items) ? status.items : [];
            for (const item of items) {
                const row = document.createElement("tr");
                const idCell = document.createElement("td");
                const link = document.createElement("a");
                link.href = `/retention/item/${encodeURIComponent(item.item_id)}`;
                link.textContent = valueText(item.item_id);
                link.className = "monitor-mono";
                idCell.append(link);
                const values = [item.store_kind, item.state, item.expires_at, item.attempt_count, item.error_code];
                row.append(idCell);
                for (const value of values) {
                    const cell = document.createElement("td");
                    cell.textContent = valueText(value);
                    row.append(cell);
                }
                diagnosticsItems.append(row);
            }
            summary.textContent = `${items.length} 件 · worker ${valueText(status.worker_state)}`;
            error.hidden = true;
        } catch (failure) {
            diagnosticsItems.textContent = "";
            summary.textContent = "利用不可";
            error.textContent = `保持状態を取得できませんでした（${failure instanceof RetentionRequestError ? failure.code : "retention_unknown_error"}）。`;
            error.hidden = false;
        }
    }

    if (diagnosticsItems) renderRetentionDiagnostics();
    if (!retentionRoot) return;

    const targetKind = retentionRoot.dataset.targetKind;
    const targetId = retentionRoot.dataset.targetId;
    const scope = targetKind === "session" ? "session_items" : "single_item";
    const dialog = document.getElementById("retention-dialog");
    const trigger = document.getElementById("retention-manage-trigger");
    const title = document.getElementById("retention-dialog-title");
    const closeButton = document.getElementById("retention-close");
    const operationControls = document.querySelectorAll('input[name="retention-operation"]');
    const reason = document.getElementById("retention-reason");
    const comment = document.getElementById("retention-comment");
    const previewButton = document.getElementById("retention-preview");
    const confirmButton = document.getElementById("retention-confirm");
    const refreshButton = document.getElementById("retention-refresh");
    const previewSurface = document.getElementById("retention-preview-surface");
    const previewContent = document.getElementById("retention-preview-content");
    const resultSurface = document.getElementById("retention-result");
    const resultContent = document.getElementById("retention-result-content");
    const currentStatus = document.getElementById("retention-current-status");
    const live = document.getElementById("retention-live");
    const error = document.getElementById("retention-error");
    let preview = null;
    let workflowKey = null;
    let confirmationToken = null;
    let recoveryUsed = false;
    let selectionRevision = 0;
    let operationStatus = null;
    let activeController = new AbortController();

    function selectedOperation() {
        return document.querySelector('input[name="retention-operation"]:checked')?.value || null;
    }

    function committedStatus(committed) {
        return committed.status || (committed.idempotent_replay ? "replayed" : "committed");
    }

    function setLive(message) {
        live.textContent = message;
    }

    function showError(code) {
        const safeCode = knownErrors.has(code) ? code : "retention_unknown_error";
        error.textContent = `操作を完了できませんでした（${safeCode}）。現在の状態と新しい確認内容を確認してください。`;
        error.hidden = false;
    }

    function clearError() {
        error.textContent = "";
        error.hidden = true;
    }

    function resetController() {
        activeController.abort();
        activeController = new AbortController();
    }

    function clearSensitiveState() {
        confirmationToken = null;
        workflowKey = null;
        preview = null;
        confirmButton.disabled = true;
        previewContent.textContent = "";
        previewSurface.hidden = true;
    }

    function invalidatePreview() {
        selectionRevision += 1;
        if (!preview && !workflowKey && !confirmationToken) return;
        confirmationToken = null;
        workflowKey = null;
        preview = null;
        recoveryUsed = false;
        confirmButton.disabled = true;
        previewContent.textContent = "";
        previewSurface.hidden = true;
        setLive("入力内容が変わりました。影響をもう一度確認してください。");
    }

    function setMutationControlsDisabled(disabled) {
        for (const operationControl of operationControls) operationControl.disabled = disabled;
        reason.disabled = disabled;
        comment.disabled = disabled;
    }

    function targetStatusPath() {
        const segment = encodeURIComponent(targetId);
        return targetKind === "session"
            ? `/api/retention/v1/sessions/${segment}`
            : `/api/retention/v1/items/${segment}`;
    }

    async function readTargetStatus() {
        const status = await requestJson(targetStatusPath(), { signal: activeController.signal });
        currentStatus.textContent = "";
        appendHeading(currentStatus, "現在の権威ある状態");
        if (targetKind === "session") {
            appendDefinitionList(currentStatus, [
                ["Session ID", status.session_id],
                ["raw 保持状態", status.raw_retention_state],
                ["読み取り可能", status.readable_count],
                ["読み取り拒否", status.read_denied_count],
                ...lifecycleRows(status.lifecycle_counts),
            ]);
        } else {
            appendDefinitionList(currentStatus, [
                ["項目 ID", status.item_id], ["保存先種別", status.store_kind], ["ライフサイクル", status.state],
                ["ピン状態", status.pin_state], ["削除状態", status.delete_state], ["ポリシー", status.policy_id],
                ["ポリシーバージョン", status.policy_version], ["取得日時", status.captured_at], ["有効期限", status.expires_at],
                ["読み取り拒否日時", status.read_denied_at], ["キュー投入日時", status.queued_at], ["削除開始日時", status.deletion_started_at],
                ["削除日時", status.deleted_at], ["試行回数", status.attempt_count], ["再試行終了", status.retry_exhausted],
                ["エラーコード", status.error_code], ["次回再試行", status.retry_at], ["リビジョン", status.revision], ["Session ID", status.session_id],
            ]);
        }
        return status;
    }

    function previewRequestBody(operation) {
        return {
            target: { kind: targetKind, id: targetId },
            operation,
            scope,
            reason_code: reason.value,
            comment: comment.value === "" ? null : comment.value,
        };
    }

    async function publishPreview({ recovery = false } = {}) {
        const operation = selectedOperation();
        const commentLength = Array.from(comment.value).length;
        if (!operation || !reason.value || commentLength > 256) {
            showError("retention_mutation_request_invalid");
            setLive("操作と理由を選択してください。");
            return;
        }

        clearError();
        previewButton.disabled = true;
        confirmButton.disabled = true;
        setLive("正確な対象と影響を確認しています。");
        const requestedRevision = selectionRevision;
        const requestKey = createWorkflowKey();
        workflowKey = requestKey;
        try {
            const nextPreview = await requestJson("/api/retention/v1/previews", {
                method: "POST",
                headers: { ...csrfHeaders, "Idempotency-Key": requestKey },
                body: JSON.stringify(previewRequestBody(operation)),
                signal: activeController.signal,
            });
            if (selectionRevision !== requestedRevision || workflowKey !== requestKey) return;
            preview = nextPreview;
            renderPreview(preview);
            recoveryUsed = recovery;
            setLive(preview.mutation_allowed ? "確認内容を読み、問題がなければ最終確定してください。" : "この対象には現在実行できる変更がありません。");
        } catch (failure) {
            preview = null;
            workflowKey = null;
            if (failure instanceof RetentionRequestError && failure.code === "retention_request_cancelled") {
                return;
            }
            if (recovery) {
                showError(failure instanceof RetentionRequestError ? failure.code : "retention_unknown_error");
            } else {
                await refreshStatusAndPreviewOnce(failure instanceof RetentionRequestError ? failure.code : "retention_unknown_error");
            }
        } finally {
            previewButton.disabled = false;
        }
    }

    function renderPreview(value) {
        previewContent.textContent = "";
        appendDefinitionList(previewContent, [
            ["対象種別", value.target_kind], ["正確な対象 ID", value.target_id], ["操作", value.operation], ["スコープ", value.scope],
            ["結果", value.result], ["空の理由", value.empty_reason], ["変更可能", value.mutation_allowed],
            ["ソース状態", value.source_state], ["Session 完全性", value.session_completeness], ["コンテンツ状態", value.content_state],
            ["正確な項目数", value.target_item_count], ["除外項目数", value.excluded_item_count],
        ]);

        appendHeading(previewContent, "現在のライフサイクル・ピン・削除状態");
        const state = value.current_state || {};
        appendDefinitionList(previewContent, [
            ["読み取り可能", state.readable_item_count], ["読み取り拒否", state.read_denied_item_count],
            ["ピン留め", state.pinned_item_count], ["未ピン留め", state.unpinned_item_count],
            ...lifecycleRows(state.lifecycle_counts),
        ]);

        appendHeading(previewContent, "保存先種別の内訳");
        appendTable(previewContent, ["保存先種別", "項目数", "読み取り可能", "読み取り拒否"], value.store_kind_summary,
            ["store_kind", "item_count", "readable_count", "read_denied_count"]);

        appendHeading(previewContent, value.operation === "delete_now" ? "ピン解除を含む正確な削除対象" : "正確な変更対象");
        appendTable(previewContent,
            ["項目 ID", "保存先種別", "状態", "ピン", "削除", "取得", "期限", "ポリシー", "版", "読み取り拒否", "キュー", "revision", "再試行終了", "エラー"],
            value.target_items,
            ["item_id", "store_kind", "state", "pin_state", "delete_state", "captured_at", "expires_at", "policy_id", "policy_version", "read_denied_at", "queued_at", "revision", "retry_exhausted", "error_code"]);

        appendHeading(previewContent, "取得・有効期限・ポリシーの原状態");
        appendTable(previewContent,
            ["ポリシー", "版", "項目数", "取得日時 min", "取得日時 max", "元の期限 min", "元の期限 max"],
            value.capture_expiry_policy_summary,
            ["policy_id", "policy_version", "item_count", "captured_at_min", "captured_at_max", "original_expires_at_min", "original_expires_at_max"]);

        appendHeading(previewContent, "保持されるメタデータ・証拠への影響");
        const impact = value.retained_metadata_impact || {};
        appendDefinitionList(previewContent, [
            ["raw 内容を削除", impact.raw_content_will_be_deleted], ["Session メタデータを保持", impact.session_metadata_retained],
            ["保持するイベントメタデータ", impact.event_metadata_retained_count], ["保持する安全な要約", impact.safe_summary_retained_count],
            ["保持する証拠参照", impact.evidence_reference_retained_count],
        ]);

        appendHeading(previewContent, "除外と進行中 cleanup の競合");
        appendTable(previewContent, ["除外理由", "項目数"], value.excluded_items_by_reason, ["reason_code", "item_count"]);
        appendTable(previewContent, ["競合コード", "項目数", "競合版"], value.active_cleanup_exclusion_conflicts, ["conflict_code", "item_count", "conflict_version"]);

        appendHeading(previewContent, "確定に固定される値");
        appendDefinitionList(previewContent, [
            ["バックアップ非消去警告", value.backup_non_purge_warning_code], ["期待状態バージョン", value.expected_state_version],
            ["対象集合 digest", value.target_item_set_digest], ["preview digest", value.preview_digest],
            ["確認期限（5 分）", value.confirmation_expires_at], ["拒否コード", value.rejection_code],
        ]);
        previewSurface.hidden = false;
        resultSurface.hidden = true;
        confirmButton.disabled = !value.mutation_allowed;
        document.getElementById("retention-preview-title").focus({ preventScroll: true });
    }

    function validConsumedLocation(location) {
        if (!location || !location.startsWith("/") || location.startsWith("//")) return null;
        try {
            const resolved = new URL(location, window.location.origin);
            if (resolved.origin !== window.location.origin || resolved.search || resolved.hash
                || !resolved.pathname.startsWith("/api/retention/v1/mutations/")) return null;
            return resolved.pathname;
        } catch {
            return null;
        }
    }

    async function resolveConsumedLocation(failure) {
        if (!(failure instanceof RetentionRequestError) || failure.code !== "retention_confirmation_consumed") return false;
        confirmationToken = null;
        workflowKey = null;
        preview = null;
        confirmButton.disabled = true;
        const consumed = validConsumedLocation(failure.location);
        if (!consumed) {
            error.textContent = "確認は使用済みですが、確定済み操作の状態リンクを検証できませんでした。操作は再送信しません。";
            error.hidden = false;
            setLive("確定状態を確認できません。新しい操作は開始していません。");
            return true;
        }
        try {
            const committed = await requestJson(consumed, { signal: activeController.signal });
            await renderCommittedWithoutMutationRecovery(committed);
        } catch (statusFailure) {
            if (statusFailure instanceof RetentionRequestError && statusFailure.code === "retention_request_cancelled") return true;
            const code = statusFailure instanceof RetentionRequestError ? statusFailure.code : "retention_unknown_error";
            error.textContent = `確認は使用済みですが、確定済み操作の状態を取得できませんでした（${code}）。操作は再送信しません。`;
            error.hidden = false;
            setLive("確定状態を確認できません。新しい preview や操作は開始していません。");
        }
        return true;
    }

    async function confirmMutation() {
        if (!preview || !preview.mutation_allowed || !workflowKey) return;
        confirmButton.disabled = true;
        previewButton.disabled = true;
        setMutationControlsDisabled(true);
        clearError();
        setLive("確認を発行しています。");
        try {
            try {
                confirmationToken = (await requestJson("/api/retention/v1/confirmations", {
                    method: "POST",
                    headers: { ...csrfHeaders, "Idempotency-Key": workflowKey },
                    body: JSON.stringify({ preview_id: preview.preview_id, preview_digest: preview.preview_digest }),
                    signal: activeController.signal,
                })).confirmation_token;
            } catch (failure) {
                if (await resolveConsumedLocation(failure)) return;
                throw failure;
            }

            let result;
            try {
                result = await requestJson("/api/retention/v1/mutations", {
                    method: "POST",
                    headers: { ...csrfHeaders, "Idempotency-Key": workflowKey },
                    body: JSON.stringify({
                        confirmation_token: confirmationToken,
                        operation: preview.operation,
                        scope: preview.scope,
                        target_kind: preview.target_kind,
                        target_id: preview.target_id,
                    }),
                    signal: activeController.signal,
                });
            } catch (failure) {
                if (await resolveConsumedLocation(failure)) return;
                throw failure;
            } finally {
                confirmationToken = null;
            }
            await renderCommittedWithoutMutationRecovery(result);
        } catch (failure) {
            confirmationToken = null;
            workflowKey = null;
            if (failure instanceof RetentionRequestError && failure.code === "retention_request_cancelled") return;
            await refreshStatusAndPreviewOnce(failure instanceof RetentionRequestError ? failure.code : "retention_unknown_error");
        } finally {
            previewButton.disabled = false;
            setMutationControlsDisabled(false);
        }
    }

    function renderCommittedCore(committed) {
        resultContent.textContent = "";
        const committedDetails = appendDefinitionList(resultContent, [
            ["状態", committedStatus(committed)], ["結果コード", committed.result_code], ["操作 ID", committed.operation_id],
            ["操作", committed.operation], ["対象種別", committed.target_kind], ["正確な対象 ID", committed.target_id],
            ["読み取り拒否", committed.read_denied], ["監査参照", committed.audit_event_id], ["再生結果", committed.idempotent_replay],
            ["作成日時", committed.created_at], ["完了日時", committed.completed_at], ["バックアップ非消去警告", committed.backup_non_purge_warning_code],
            ["正確な項目数", committed.target_item_count], ["ピン状態", committed.pin_state],
            ["期待バージョン", committed.expected_version], ["結果バージョン", committed.result_version],
            ...lifecycleRows(committed.lifecycle_counts),
        ]);
        operationStatus = committedDetails.querySelector("dd");
        operationStatus.id = "retention-operation-status";
        operationStatus.textContent = committedStatus(committed);
        previewSurface.hidden = true;
        resultSurface.hidden = false;
        setLive("トランザクションの確定結果を確認しました。補足の物理処理状態を取得しています。");
        document.getElementById("retention-result-title").focus({ preventScroll: true });
    }

    async function renderCommitted(committed) {
        confirmationToken = null;
        workflowKey = null;
        preview = null;
        recoveryUsed = false;
        clearError();
        renderCommittedCore(committed);
        await loadCommittedSupplements(committed);
    }

    async function renderCommittedWithoutMutationRecovery(committed) {
        try {
            await renderCommitted(committed);
        } catch {
            confirmationToken = null;
            workflowKey = null;
            preview = null;
            confirmButton.disabled = true;
            error.textContent = "確定済み結果を受信しましたが、画面の補足表示を完了できませんでした。操作は再送信しません。";
            error.hidden = false;
            setLive("操作は確定済みです。新しい preview や操作は開始していません。");
        }
    }

    async function loadCommittedSupplements(committed) {
        const operationHost = document.createElement("section");
        const targetHost = document.createElement("section");
        const workerHost = document.createElement("section");
        operationHost.className = "retention-supplement";
        targetHost.className = "retention-supplement";
        workerHost.className = "retention-supplement";
        appendHeading(operationHost, "権威ある操作 status");
        appendHeading(targetHost, "確定後の対象状態");
        appendHeading(workerHost, "#89 物理 worker 状態");
        resultContent.append(operationHost, targetHost, workerHost);
        const operationSupplement = (async () => {
            if (committed.status) {
                appendDefinitionList(operationHost, [["操作 status", committed.status]]);
                return;
            }
            try {
                const authoritativeStatus = await requestJson(`/api/retention/v1/mutations/${encodeURIComponent(committed.operation_id)}`, { signal: activeController.signal });
                operationStatus.textContent = committedStatus(authoritativeStatus);
                appendDefinitionList(operationHost, [
                    ["操作 status", authoritativeStatus.status], ["結果コード", authoritativeStatus.result_code],
                    ["読み取り拒否", authoritativeStatus.read_denied], ["監査参照", authoritativeStatus.audit_event_id],
                ]);
            } catch (failure) {
                const code = failure instanceof RetentionRequestError ? failure.code : "retention_unknown_error";
                appendDefinitionList(operationHost, [["操作 status 補足", `利用不可（${code}）`]]);
            }
        })();
        const targetSupplement = (async () => {
            try {
                const targetStatus = await readTargetStatus();
                appendDefinitionList(targetHost, targetKind === "item" ? [
                    ["項目 ID", targetStatus.item_id], ["状態", targetStatus.state], ["ピン", targetStatus.pin_state], ["削除状態", targetStatus.delete_state],
                    ["読み取り拒否日時", targetStatus.read_denied_at], ["キュー投入日時", targetStatus.queued_at], ["削除開始日時", targetStatus.deletion_started_at],
                    ["削除日時", targetStatus.deleted_at], ["試行回数", targetStatus.attempt_count], ["再試行終了", targetStatus.retry_exhausted], ["エラー", targetStatus.error_code],
                ] : [
                    ["Session ID", targetStatus.session_id], ["raw 保持状態", targetStatus.raw_retention_state],
                    ["読み取り可能", targetStatus.readable_count], ["読み取り拒否", targetStatus.read_denied_count],
                    ...lifecycleRows(targetStatus.lifecycle_counts),
                ]);
            } catch (failure) {
                const code = failure instanceof RetentionRequestError ? failure.code : "retention_unknown_error";
                appendDefinitionList(targetHost, [["対象状態", `利用不可（${code}）`]]);
            }
        })();
        const workerSupplement = (async () => {
            try {
                const workerStatus = await requestJson("/api/retention/v1/status", { signal: activeController.signal });
                appendDefinitionList(workerHost, [
                    ["worker 状態", workerStatus.worker_state], ["物理削除待ち", workerStatus.pending_count],
                    ["キュー投入", workerStatus.queued_count], ["物理削除中", workerStatus.deleting_count], ["物理削除失敗", workerStatus.failed_count],
                ]);
            } catch (failure) {
                const code = failure instanceof RetentionRequestError ? failure.code : "retention_unknown_error";
                appendDefinitionList(workerHost, [["worker 状態", `利用不可（${code}）`]]);
            }
        })();
        await Promise.allSettled([operationSupplement, targetSupplement, workerSupplement]);
        setLive("確定結果を表示しました。物理削除の完了は #89 worker 状態で確認してください。");
    }

    async function refreshStatusAndPreviewOnce(code) {
        confirmationToken = null;
        workflowKey = null;
        preview = null;
        confirmButton.disabled = true;
        showError(code);
        if (recoveryUsed) return;
        recoveryUsed = true;
        try {
            await readTargetStatus();
            await publishPreview({ recovery: true });
            showError(code);
        } catch {
            showError(code);
        }
    }

    async function refreshCommittedStatus() {
        setLive("現在の物理処理状態を再取得しています。");
        for (const prior of resultContent.querySelectorAll(".retention-refresh-details")) prior.remove();
        const targetRefresh = (async () => {
            try {
                const targetStatus = await readTargetStatus();
                appendDefinitionList(resultContent, [["再取得した対象状態", targetKind === "item" ? targetStatus.state : targetStatus.raw_retention_state]], "retention-details retention-refresh-details");
            } catch (failure) {
                const code = failure instanceof RetentionRequestError ? failure.code : "retention_unknown_error";
                appendDefinitionList(resultContent, [["再取得した対象状態", `利用不可（${code}）`]], "retention-details retention-refresh-details");
            }
        })();
        const workerRefresh = (async () => {
            try {
                const worker = await requestJson("/api/retention/v1/status", { signal: activeController.signal });
                appendDefinitionList(resultContent, [
                    ["再取得した worker 状態", worker.worker_state], ["物理削除待ち", worker.pending_count],
                    ["キュー投入", worker.queued_count], ["物理削除中", worker.deleting_count], ["物理削除失敗", worker.failed_count],
                ], "retention-details retention-refresh-details");
            } catch (failure) {
                const code = failure instanceof RetentionRequestError ? failure.code : "retention_unknown_error";
                appendDefinitionList(resultContent, [["再取得した worker 状態", `利用不可（${code}）`]], "retention-details retention-refresh-details");
            }
        })();
        await Promise.allSettled([targetRefresh, workerRefresh]);
        setLive("利用できる確定後の補足状態を再取得しました。確定済み操作は変更していません。");
    }

    function openDialog() {
        if (dialog.open) return;
        resetController();
        clearError();
        dialog.showModal();
        title.focus({ preventScroll: true });
        readTargetStatus().catch((failure) => {
            if (!(failure instanceof RetentionRequestError) || failure.code !== "retention_request_cancelled") {
                showError(failure instanceof RetentionRequestError ? failure.code : "retention_unknown_error");
            }
        });
    }

    function closeDialog() {
        activeController.abort();
        clearSensitiveState();
        dialog.close();
    }

    trigger.addEventListener("click", openDialog);
    closeButton.addEventListener("click", closeDialog);
    previewButton.addEventListener("click", () => publishPreview());
    confirmButton.addEventListener("click", confirmMutation);
    refreshButton.addEventListener("click", refreshCommittedStatus);
    for (const operationControl of operationControls) operationControl.addEventListener("change", invalidatePreview);
    reason.addEventListener("change", invalidatePreview);
    comment.addEventListener("input", invalidatePreview);
    dialog.addEventListener("cancel", (event) => {
        event.preventDefault();
        closeDialog();
    });
    dialog.addEventListener("close", () => trigger.focus({ preventScroll: true }));
    window.addEventListener("pagehide", () => {
        activeController.abort();
        clearSensitiveState();
    });
    openDialog();
})();
