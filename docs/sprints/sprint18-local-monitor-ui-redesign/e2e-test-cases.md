# Sprint18 E2E Test Cases

自動化された E2E（Playwright / Chromium headless、`dotnet test` に統合）の
ケース一覧。各ケースは `[Collection(PlaywrightBrowserPathCollection.Name)]`
でブラウザキャッシュを repository-local に固定し、該当するものは
`sanitizedOnly` を theory 化して raw / sanitized 両姿勢を検証する。

## 共通シェル — `MonitorShellPlaywrightTests`

| # | ケース | 検証内容 |
| --- | --- | --- |
| S-1 | サイドバー 2 項目ナビ | `.sidebar-link` が 2 件（概要 / トレース）、「診断」ナビ項目が存在しない（D042 C1） |
| S-2 | 受信ステータスバッジ | ready 状態で「正常 · 受信中」表示 |
| S-3 | ポップオーバー開閉 | バッジクリックで開く、`/health/ready → 200`、パイプライン 4 行、Esc で閉じる |
| S-4 | 診断への段階的動線 | 「詳細診断を開く」→ `/diagnostics` 遷移、診断ページでもナビ 2 項目、パイプライン 4 カード表示 |
| S-5 | 取り込み履歴動線 | ポップオーバーの「取り込み履歴」→ `/diagnostics#ingestion-history` で details が open |
| S-6 | 境界 | shell の fetch は `/health/ready` と sanitized `/api/monitor/*` のみ。`/raw`・`prompt-label` への fetch なし（両姿勢） |

## 概要 — `MonitorOverviewPlaywrightTests`

| # | ケース | 検証内容 |
| --- | --- | --- |
| O-1 | サーバー描画構造 | KPI カード 4 枚、最近のトレース 1 行（seed 分） |
| O-2 | prompt 境界 | raw-default: 最近のトレースに prompt label 表示 / sanitized: prompt marker 非表示 + 短縮 TraceId fallback |
| O-3 | エラー KPI リンク | `href` に `status=error`（フィルタ済み一覧へのドリルダウン） |
| O-4 | 期間トグル | 「7日」クリック → `.active` 切替、KPI ラベル「7日のトークン」、`/api/monitor/overview?period=7d` と `/api/monitor/trace-list?period=7d` を refetch、KPI 値 1.2K |
| O-5 | TOP5 の prompt 取得境界 | raw-default: `/traces/{id}/prompt-label` を fetch して label 表示 / sanitized: 当該 fetch なし・`/raw` fetch なし・prompt 非表示 |

## トレース一覧 — `MonitorTraceListPlaywrightTests`

| # | ケース | 検証内容 |
| --- | --- | --- |
| L-1 | 既定ソート | トークン降順（大きい trace が先頭） |
| L-2 | 自動選択 + プレビュー | 先頭行が `.selected`、プレビューにミニ KPI（トークン値） |
| L-3 | prompt 境界 | raw-default: プレビュータイトルに prompt、raw リンク 1 件 / sanitized: prompt marker なし |
| L-4 | 行選択切替 | 2 行目クリック → プレビューが「エラー · 異常終了」に更新、URL 遷移なし |
| L-5 | 状態フィルタ | `status=unrecovered` 選択 → 行 1 件、`/api/monitor/trace-list?...status=unrecovered` を refetch、URL に反映 |
| L-6 | sanitized fetch 境界 | `prompt-label` / `/raw` への fetch なし |

## トレース詳細（フロー / waterfall）— `MonitorDesignViewPlaywrightTests`

| # | ケース | 検証内容 |
| --- | --- | --- |
| D-1 | hex トークン | `--monitor-bg` が `#14171e`（D042 C2） |
| D-2 | エラーのみ既定 | 回復済みエラー trace で「エラーのみ」が既定 ON |
| D-3 | フロー描画 | ターンカード 3 枚 + 意図ラベル（ターン1 · 調査）、開始 / 完了マーカー |
| D-4 | 並行グループ | 「⑂ 並行 3 件」バッジ + 横並びツールカード 3 枚（親一致 + 時間重なり判定） |
| D-5 | 回復ペア | 失敗 str_replace が琥珀カード（✕ 失敗 · tool_failure） |
| D-6 | キャッシュ列 | 読取率 70%（26500 入力中 18500 読取）、実効入力換算行、ターン別バー 3 本 |
| D-7 | スパン選択 + URL | ターンクリック → `.selected` + `?span=t100` |
| D-8 | ビュー切替と状態保持 | waterfall へ切替 → `?view=waterfall&span=t100` 維持、選択行維持 |
| D-9 | waterfall 並行語彙 | `⑂ 並行 3 件` グループ見出し + `├─`/`└─` prefix 3 件、tokens 列は llm のみ値 |
| D-10 | URL 復元 | `?view=waterfall&span=f201` 直リンクでビューと選択を復元 |
| D-11 | 境界 | fetch は sanitized spans API のみ・`/raw` なし・canvas 要素なし（両姿勢） |

## スパンインスペクタ — `MonitorInspectorPlaywrightTests`

| # | ケース | 検証内容 |
| --- | --- | --- |
| I-1 | 開閉ライフサイクル | スパンクリックで右列（エラーパネル）と入替表示、Esc / ✕ / 同一スパン再クリックで閉じて元のパネルに戻る |
| I-2 | 整形既定 | 開いた直後のアクティブタブが「整形」 |
| I-3 | raw タブ | raw-default: `/traces/{id}/spans/{spanId}/detail` を fetch、raw タブに OTLP span JSON（spanId 含む） |
| I-4 | sanitized 姿勢 | detail route への fetch なし、sanitized メタのみ + 「raw タブは利用できません」表示 |

## エラー解析モード — `MonitorErrorModePlaywrightTests`

| # | ケース | 検証内容 |
| --- | --- | --- |
| E-1 | status ピル | 未回復 trace で「エラー · 異常終了」 |
| E-2 | エラー要約ストリップ | 「エラー 2件 — 1件は回復済み — 1件が原因でトレースが異常終了」 |
| E-3 | エラーパネル | キャッシュ列の代わりに表示、エラー一覧 2 行（回復済み琥珀 / 未回復赤ピル） |
| E-4 | フロー連動 | 未回復エラー行クリック → フローの該当カードが `.selected`（赤 glow カード） |
| E-5 | トークン推移 | ターン別バー 3 本 + 128K 赤破線 |
| E-6 | 例外メッセージ境界 | raw-default: detail route から取得 / sanitized: fetch なし + 表示不可の注記 |
| E-7 | 回復済みのみ trace | エラーモードに入る（ストリップ + パネル + エラーのみ ON）。canvas 4a-2 の未解決点の解決を記録 |

## Copilot ドロワー — `MonitorDrawerPlaywrightTests`

| # | ケース | 検証内容 |
| --- | --- | --- |
| C-1 | 起動と必須コピー | ヘッダー常設ボタンで開く、「ローカル SDK 経由 · raw はローカルから出ません」表示、背面フロー opacity 0.55 |
| C-2 | 解析実行 | fake runner で run 作成 → 実行チップ「観点「トークン」で解析を実行 — Ns」→ 所見表示 |
| C-3 | 追い質問（履歴再送） | 入力送信 → 新規 run の payload に `question` + `history`（直前の Q&A）が渡る（runner 側で検証） |
| C-4 | サジェストチップ | チップクリックで追い質問として送信、履歴 2 turn を再送 |
| C-5 | Esc | ドロワーのみ閉じる（フロー選択に影響しない） |
| C-6 | sanitized 姿勢 | ドロワー markup と Copilot ボタンが存在しない |

## ルート境界（HTTP レベル、Playwright 外）

- `MonitorSpanDetailRouteTests`: tool / llm 整形 shape、整形不能 span でも
  `raw_span_json` 返却、未知 trace / span 404、cross-site / 異 Origin 403、
  no-store、`--sanitized-only` で route 不在 404。
- `MonitorSecurityBoundaryTests`: span-detail route の sanitized 404 +
  cross-site 403 + no-store を既存 raw-route negative matrix に追加。
- `MonitorOverviewEndpointTests` / `MonitorTraceListEndpointTests`: sensitive
  marker seed に対し JSON へ prompt / tool args / PII が出ないことを検証。
