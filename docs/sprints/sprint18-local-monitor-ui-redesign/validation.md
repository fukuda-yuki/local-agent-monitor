# Sprint18 Validation Evidence

## Pinned validation sequence

リポジトリ root で pinned 3 コマンドを実行した結果（最終、M10）。

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

| コマンド | 結果 |
| --- | --- |
| `dotnet build CopilotAgentObservability.slnx` | ビルドに成功しました。0 警告 / 0 エラー |
| `pwsh scripts\test\install-playwright-chromium.ps1` | Chromium bootstrap 完了（`artifacts\playwright-browsers` に配置） |
| `dotnet test CopilotAgentObservability.slnx` | **成功。合計 687 passing**（ConfigCli 301 + LocalMonitor 386、Playwright smoke 含む）、失敗 0 / スキップ 0 |

各マイルストーンでも build → targeted tests → full `dotnet test` を実行して
コミットした（M1〜M9 でフルスイート緑を確認）。

## Backward-compat proof

- `tests/CopilotAgentObservability.LocalMonitor.Tests/CanvasExtensionContractTests.cs`
  は **Sprint18 で無変更**（`git diff <sprint18-base>..HEAD -- …` が空）で
  green。既存 public routes（`/api/monitor/*`、`/health/*`、`/events`、
  `/v1/traces`、既存 raw-bearing routes）の shape / ordering は不変。
- 新規需要はすべて **新規 endpoint**: `GET /api/monitor/overview`、
  `GET /api/monitor/trace-list`、`GET /traces/{traceId}/spans/{spanId}/detail`。

## Sanitized / raw boundary evidence

- 新 sanitized endpoint（overview / trace-list）は sensitive-marker seed に
  対して prompt / tool args / PII を返さない negative assertion 付き
  （`MonitorOverviewEndpointTests`、`MonitorTraceListEndpointTests`）。
- 新 raw-bearing route（span-detail）は `--sanitized-only` で 404、cross-site
  403、no-store を検証（`MonitorSpanDetailRouteTests`、
  `MonitorSecurityBoundaryTests`）。
- Playwright テストは各画面で sanitized 姿勢時に raw-bearing route
  （`/raw`・`prompt-label`・`/detail`）へ fetch しないことを assert。
- schema v4 の cache / status カラムは additive・no-backfill。既存行は NULL の
  まま（`MonitorProjectionStoreTests`、`MonitorTraceRollupCacheStatusTests`）。

## Frontend DOM 生成

全 client-side モジュール（`monitor-shell.js` / `-overview.js` /
`-tracelist.js` / `-flow.js` / `-waterfall.js` / `-cache-panel.js` /
`-inspector.js` / `-error-mode.js` / `-drawer.js` / `-diagnostics.js`）は
`createElement` / `textContent` のみで DOM を生成し、`innerHTML` /
`Html.Raw` を使用しない（`MonitorUiTests` の script boundary tests で検証）。

## Visual validation（screenshot comparison notes）

1440px viewport / Chromium headless で各画面を実データ（合成 OTLP）を取り込んで
確認し、ハンドオフ `screenshots/01-08` および canvas HTML の実測値と対照した。
スクリーンショットは scratchpad に保存（`docs/` にはコミットしない — 生成
runtime artifact のため AGENTS.md の方針に従う）。

| 画面 | 対照 | 所見 |
| --- | --- | --- |
| 概要（M3） | 01-概要 / 2a-3 | KPI 4 枚（今日のトークン金 28px、実効入力換算、キャッシュ読取率 + バー、エラー trace）、モデル別積み上げ内訳、キャッシュ効率 + 低効率所見、TOP5、時間帯別バー、最近のトレース。期間トグル動作。配色・余白ともハンドオフ一致 |
| トレース一覧（M4/M10） | 02-トレース / A2 | ツールバー（検索 340px / モデル / 状態 / 期間 / 件数・合計）、grid テーブル + 金ヒートバー + トークン降順 ▼、392px プレビュー（ミニ KPI・トークン構成・TOP3・詳細を開く primary + raw）。一致 |
| トレース詳細フロー（M5） | 03/3a-1 | パンくず + Copilot 常設ボタン + 前/次、status ピル、380px トークン合計カード、縦レール + ターンカード + 意図ラベル、⑂ 並行 3 件横並び、回復済み琥珀 / 未回復赤、360px キャッシュ列。一致 |
| waterfall（M5） | 04/4a-1 | 時間軸目盛、■色マーカー + インデント、並行グループ見出し + `├─`/`└─` prefix + 開始位置揃え、tokens 列 llm のみ。一致 |
| スパンインスペクタ（M6） | 4a-3 | 右列入替、整形既定 / raw タブ、tool 呼出引数・結果末尾・推定トークン、llm ロール別・応答プレビュー・トークン内訳。一致（実ペイロード整形はライブ検証待ち） |
| エラー解析モード（M7） | 4a-2 | エラー要約ストリップ、エラーのみ既定 ON、回復済み / 未回復カード、3 カードパネル（一覧 / 詳細 / 128K 赤破線トークン推移）。一致 |
| Copilot ドロワー（M8） | 3a-2 / 2a-2 | 472px 右ドロワー、必須データ境界コピー、観点選択 + 実行、所見 + 該当スパン表示、サジェストチップ、追い質問入力。背面 opacity 0.55。一致 |
| 診断（M9/M10） | 08-診断 / A4 | 診断見出し + ready ピル + probe リンク、パイプライン 4 カード + OK バッジ、コンポーネント確認テーブル + 注記、しきい値（実効値 10s/60s）、取り込み履歴折りたたみ。ナビは 2 項目（C1、canvas A4 の 3 項目は stale として不採用）。一致 |

## Known limitations / human-gated follow-ups

- **`SpanDetailExtractor` 実ペイロード整形表示**: OTLP span attribute の実キー名
  （tool arguments / result、llm message role 等）は実 VS Code Copilot Chat
  ペイロードでのライブ検証まで未確定。抽出は defensive 実装で、整形抽出が空でも
  raw タブで OTLP span JSON 全文を表示できる（D043）。整形ビューの実データ表示の
  ライブ検証のみ人手ゲートとして残る（既存 `MonitorPromptExtractor` と同種の
  caveat）。
- **プロンプト全文検索**: server 側 TraceId 部分一致 + client 側の読み込み済み行
  prompt label フィルタに限定（D042 C8）。全コーパスの prompt 全文検索は scope
  外の follow-up（`docs/task.md` に記録）。
- フォント weight 600 は vendored フォントに存在しないため CSS 上 700 へマップ
  （D042 C7、accepted deviation）。
