# Documentation UX, Structure, and Readability Bug Cards

Scope reviewed: Repository documentation (`README.md`, `docs/getting-started.md`, `docs/user-guide.md`, `docs/user-guide/*.md`, `docs/assets/screenshots/*`).  
Source of truth checked: `docs/requirements.md`, `docs/spec.md`, `docs/specifications/`, and explicit user feedback requirements (Demo mode confusion, Image inconsistencies, PowerShell troubleshooting, `cc-switch` structure benchmark, and `humanizer-ja` Japanese style rules).

---

## Detailed Issue Index

| Sub-Card | Category | Target File & Lines | Severity | Status | Summary / Root Cause |
| --- | --- | --- | --- | --- | --- |
| **[DOC-1A](#DOC-1A)** | Demo Confusion | `README.md:128-134`, `docs/getting-started.md:6-26` | **High** | Open | ドキュメント冒頭で合成データ生成が最優先ステップとして掲載され、環境構築完了と誤認される |
| **[DOC-1B](#DOC-1B)** | Demo Confusion | `README.md:182-187`, `docs/user-guide/local-monitor.md` | **High** | Open | Config CLI / setup.ps1 の `success: true` が実テレメトリ受信完了と誤解され、First Trace 確認が埋もれている |
| **[DOC-1C](#DOC-1C)** | Demo Confusion | UI screenshots, `docs/user-guide/local-monitor.md` | Medium | Open | 画像や説明文において「デモデータ表示中」と「ライブ受信準備完了」の区別・識別注釈が不足している |
| **[DOC-2A](#DOC-2A)** | Image Discrepancy | `docs/user-guide.md:99-109` | Medium | Open | `user-guide.md` ポータルに Local Monitor の画像が1枚もなく、旧 Static Dashboard の画像のみで README とちぐはぐ |
| **[DOC-2B](#DOC-2B)** | Image Discrepancy | `docs/user-guide/local-monitor.md:510-655` | Medium | Open | 画像が長大ドキュメントの最末尾に一括配置され、本文（各機能説明箇所）と完全に切断されている |
| **[DOC-2C](#DOC-2C)** | Image Discrepancy | `README.md` vs `docs/user-guide/local-monitor.md` | Medium | Open | README と user-guide 間で掲載画面（waterfall, error-mode, inspector）やキャプション・説明順序が非対称 |
| **[DOC-3A](#DOC-3A)** | Troubleshooting | `README.md:171-176`, `docs/getting-started.md` | Medium | Open | Windows / PowerShell の `ExecutionPolicy`（企業ポリシーブロック）時の解決手順（`-ExecutionPolicy Bypass`）の欠落 |
| **[DOC-3B](#DOC-3B)** | Troubleshooting | `README.md`, `docs/user-guide.md` | Medium | Open | ポート4320競合（Address in use）、VS Code環境変数非引き継ぎ、プロキシ等の企業環境トラブル対策の欠落 |
| **[DOC-3C](#DOC-3C)** | Troubleshooting | `README.md:229-241`, `docs/user-guide.md` | Medium | Open | ドキュメント一覧やナビゲーションに「トラブルシューティング」項目およびリンクが一切存在しない |
| **[DOC-4A](#DOC-4A)** | Structure | `docs/user-guide.md:1-121` | Medium | Open | `cc-switch` 理想モデルのような全体目次（TOC）やステップバイステップの案内構造が存在しない |
| **[DOC-4B](#DOC-4B)** | Structure | `docs/user-guide/*.md` (6 files) | Medium | Open | ユーザーの目的軸ではなく内部実装モジュール軸（Raw Data Loop, Candidate Loop 等）で分断されている |
| **[DOC-4C](#DOC-4C)** | Structure | `README.md:186`, `docs/user-guide.md:40-75` | Medium | Open | マニュアル内に内部実装・開発者用語（`change_set_id`, `Issue #69 の責務`, OTLP carrier等）が混入している |
| **[DOC-5A](#DOC-5A)** | Natural Japanese | All doc files (`README.md`, `user-guide*.md`) | Medium | Open | `humanizer-ja` 観点での「〜することができます」「〜となります」「〜について説明します」の過多 |
| **[DOC-5B](#DOC-5B)** | Natural Japanese | `README.md`, `docs/user-guide.md` | Medium | Open | 不自然な直訳英単語・名詞句（`reversible setup`, `canonical carrier`, `telemetry evidence`）の未翻訳露出 |
| **[DOC-5C](#DOC-5C)** | Natural Japanese | `docs/getting-started.md` vs `README.md` | Medium | Open | ファイル間・ファイル内での文体（敬体「です・ます」と常体「だ・である」）の不統一と過剰な受動態 |

---

## 観点1: デモモード / 合成データと本番環境構築の混同・誤認 (DOC-1)

<a id="DOC-1A"></a>
### DOC-1A — ドキュメント順序の罠：合成データ生成が最優先手順として掲載され構築完了と錯覚する
- **対象ファイル & 行:**
  - `README.md` Lines 128-134 (`### 最低動作条件（raw-only モード）`)
  - `docs/getting-started.md` Lines 6-26 (`## 1. Synthetic Dashboard を生成する`)
- **観測された事実:**
  - `getting-started.md` や `README.md` の「必要なもの」直下において、`dotnet run ... generate-static-dashboard` による合成データ生成が第1ステップとして大きく掲載されている。
  - 初心者ユーザーがドキュメント上部からコマンドを実行すると、ローカルにダッシュボード画面が出力され、ブラウザで開けてしまうため、**「画面が表示された＝自分のCopilotの観測環境構築が完了した」と9割以上のユーザーが勘違い**して作業を終えてしまう。
- **根本原因:**
  - 外部依存のない「クイック試走（合成デモ）」と、実際の Copilot Chat / CLI からテレメトリを受信する「本番ライブ接続セットアップ」の目的と完了条件の違いがドキュメント冒頭で切り分けられていない。
- **改善要件:**
  - ドキュメント構造を改修し、「A. デモデータで今すぐ画面を見る（体験）」と「B. 実際のCopilotテレメトリを収集する（環境構築）」を明確にセクション分離する。ライブ接続手順を本編の主軸とする。

<a id="DOC-1B"></a>
### DOC-1B — Config CLI / setup.ps1 の `success: true` による静的検証と実受信完了の混同
- **対象ファイル & 行:**
  - `README.md` Lines 182-187 (`## GitHub Copilot のガイド付きセットアップ`)
  - `docs/user-guide/local-monitor.md` Lines 77-88
- **観測された事実:**
  - `pwsh scripts\local-monitor\setup.ps1 apply` を実行した際、標準出力に `{"success": true, ...}` という JSON が出力される。
  - ユーザーは `success: true` を見て「テレメトリの収集設定が完了し、通信が成功した」と誤解する。
  - README L182 に「`success: true` は静的な構成検証の成功であり、テレメトリが到着した証拠ではありません」と書かれているが、注釈が小さく、実際の接続確認手順（First Trace 受信確認・Doctor実行）への誘導が不十分である。
- **根本原因:**
  - 設定ファイル作成（静的検証）と、実際にVS CodeでCopilotを作動させてテレメトリをDBに受信できたか（動的確認）の2段階のステータス定義がユーザーに伝わっていない。
- **改善要件:**
  - セットアップ完了の定義を「First Trace（最初のテレメトリ）が Local Monitor 画面に表示されること」と明確化し、確認手順をステップバイステップで案内する。

<a id="DOC-1C"></a>
### DOC-1C — UI画面・スクリーンショットにおける「デモデータ表示中」と「ライブ接続状態」の識別表記不足
- **対象ファイル & 行:**
  - `README.md` Lines 51-105
  - `docs/user-guide/local-monitor.md` Lines 510-655
  - `docs/assets/screenshots/local-monitor-*.png`
- **観測された事実:**
  - Local Monitor の画面キャプションや説明文において、「画像内に表示されているデータが合成デモデータ（seed demo）であるのか、実環境データであるのか」の明示がない。
  - ユーザーがデモモードでモニターを開いた際、画面に過去のトレースが表示されているため、「既に環境構築が終わって自分のデータが取れている」と誤認する。
- **改善要件:**
  - ドキュメント内の全スクリーンショットに「※表示例：デモデータセット」等のキャプションを注記し、ライブ受信時のヘッダーバッジ（受信中/未接続）の確認方法を明記する。

---

## 観点2: README と user-guide の画像・ビジュアルのちぐはぐ・不一致 (DOC-2)

<a id="DOC-2A"></a>
### DOC-2A — `user-guide.md` ポータルにおける Local Monitor 画像の欠落と Static Dashboard 画像の誤配置
- **対象ファイル & 行:**
  - `docs/user-guide.md` Lines 97-109 (`## 画像で見る Static Dashboard`)
- **観測された事実:**
  - `docs/user-guide.md`（利用者向け詳細ガイドのポータル）には、製品の主要機能である `Local Ingestion Monitor` の画像が1枚も存在しない。
  - 逆に、副次機能である Static Dashboard の画像（`static-dashboard-overview.png`, `static-dashboard-filters.png`）のみが末尾に貼られている。
  - README.md で Local Monitor の豊富な画像を見たユーザーが `user-guide.md` に遷移すると、全く別の製品ドキュメントに来たかのような強い違和感（ちぐはぐ感）を受ける。
- **改善要件:**
  - `docs/user-guide.md` に Local Monitor の主要コンポーネント画像（概要、トレース一覧、詳細フロー等）を追加し、README とビジュアルイメージを統一する。

<a id="DOC-2B"></a>
### DOC-2B — `local-monitor.md` における一括画像配置による本文と画像の位置的切断
- **対象ファイル & 行:**
  - `docs/user-guide/local-monitor.md` Lines 510-655 (`## 画面・機能リファレンス` または画像セクション)
- **観測された事実:**
  - `local-monitor.md`（63KBの主要ガイド）では、テキスト部分（1〜500行目）で各機能（概要ダッシュボード、トレース一覧、フロー表示、スパンインスペクタ等）が説明されているが、スクリーンショット画像がすべてファイル最末尾（510行目以降）にまとめて配置されている。
  - ユーザーは本文を読んでいる際、該当するUI画面を同時に確認できず、ページ内を何度も往復スクロールしなければならない。
- **改善要件:**
  - 末尾の一括画像配置を廃止し、各機能（概要、トレース一覧、トレース詳細、Copilot解析ドロワー、診断等）の説明テキストの直下に、対応するスクリーンショットを埋め込む。

<a id="DOC-2C"></a>
### DOC-2C — README と user-guide 間での掲載画面・キャプション・説明順序の非対称
- **対象ファイル & 行:**
  - `README.md` Lines 51-105 vs `docs/user-guide/local-monitor.md` Lines 510-655
- **観測された事実:**
  - README.md に掲載されている画像（`local-monitor-overview.png`, `trace-list.png`, `trace-detail-flow.png`, `copilot-drawer.png`, `diagnostics.png`）と、`local-monitor.md` に掲載されている画像（＋`waterfall.png`, `span-inspector.png`, `error-mode.png`, `status-popover.png`）の組み合わせやキャプションが不一致。
  - 例えば README.md では Waterfall 表示のスクショが無いにもかかわらず「フロー / waterfall 切替」と説明されており、画面の視覚的イメージが湧かない。
- **改善要件:**
  - README.md と user-guide 間で使用するスクリーンショットの命名、キャプション、UIバージョンの整合性を完全に同期させる。

---

## 観点3: PowerShell 実行エラーおよび企業環境トラブルシューティングの不足 (DOC-3)

<a id="DOC-3A"></a>
### DOC-3A — Windows / PowerShell の ExecutionPolicy（企業ポリシー制限）エラー対処の欠落
- **対象ファイル & 行:**
  - `README.md` Lines 171-176 (`pwsh scripts\local-monitor\setup.ps1 ...`)
  - `README.md` Lines 247-249 (`pwsh scripts\test\install-playwright-chromium.ps1`)
  - `docs/getting-started.md` Lines 34-47
- **観測された事実:**
  - Windows の PowerShell で `.ps1` スクリプトを実行しようとすると、企業PC等の環境ではデフォルトの ExecutionPolicy 制限により `このシステムではスクリプトの実行が無効になっているため...` というエラー（`PSSecurityException`）が発生してセットアップが停止する。
  - ドキュメントに `Set-ExecutionPolicy -Scope Process Bypass` や `-ExecutionPolicy Bypass` を付与して実行する代替コマンド例が一切記載されていない。
- **改善要件:**
  - トラブルシューティングセクションにおいて、PowerShell スクリプト実行ブロック時の回避コマンド（`-ExecutionPolicy Bypass` 等）を明記する。

<a id="DOC-3B"></a>
### DOC-3B — ポート衝突、VS Code環境変数非引き継ぎ、プロキシ環境等のトラブル対処の欠落
- **対象ファイル & 行:**
  - `README.md` Lines 115-164
  - `docs/getting-started.md` Lines 32-50
- **観測された事実:**
  - **ポート4320衝突:** Kestrel が `http://127.0.0.1:4320` でポート競合（`Address already in use`）を起こした際の解消コマンド（`netstat` によるPID特定や `--url http://127.0.0.1:4321` 指定）が記載されていない。
  - **VS Code環境変数:** PowerShell ターミナルで環境変数を設定しても、スタートメニューやデスクトップアイコンから別ウィンドウで起動した VS Code には環境変数が引き継がれないため、「テレメトリが届かない」とハマる。同じターミナルから `code .` で起動する必要がある旨の注意が不足。
  - **プロキシ/証明書:** 企業ネットワーク環境での SSL 検査やプロキシによる通信エラー時の注意事項がない。
- **改善要件:**
  - 実践的なトラブルシューティング項目として、ポート競合・環境変数引き継ぎ・ネットワーク制限の解決策を追加する。

<a id="DOC-3C"></a>
### DOC-3C — ドキュメント一覧およびポータルからのトラブルシューティングガイドへの導線欠落
- **対象ファイル & 行:**
  - `README.md` Lines 229-241 (`## ドキュメント` 一覧表)
  - `docs/user-guide.md` Lines 16-24 (`## 読む順番`)
- **観測された事実:**
  - README.md や `docs/user-guide.md` のドキュメントナビゲーションテーブルに「トラブルシューティング（Troubleshooting）」の項目が存在せず、読者がトラブル時に自己解決できるリンクが存在しない。
- **改善要件:**
  - README および user-guide のメインテーブルにトラブルシューティングへの直リンクを追加する。

---

## 観点4: user-guide の構造・視認性・導線の不備（cc-switch モデルとの決定的な差異） (DOC-4)

<a id="DOC-4A"></a>
### DOC-4A — `user-guide.md` ポータルにおける全体目次（TOC）およびステップ案内構造の欠落
- **対象ファイル & 行:**
  - `docs/user-guide.md` Lines 1-121
- **観測された事実:**
  - 理想的な参考モデル（`https://github.com/farion1231/cc-switch/docs/user-manual/ja/README.md`）では、冒頭に「全体目次（Table of Contents）」が整理され、初心者が全体像を把握した上で目的のセクションにジャンプできる構造になっている。
  - 現在の `docs/user-guide.md` は、6つのサブファイルへの単純な箇条書きリンクと、セキュリティ警告文、コマンドブロックが雑然と並んでいるだけで、マニュアルとしての体系的な構造（ナビゲーション）がない。
- **改善要件:**
  - `docs/user-guide.md` をポータル目次として刷新し、「1. はじめに・環境構築」「2. 画面別操作マニュアル」「3. ユースケース別ガイド」「4. トラブルシューティング」の体系的な構造に再編する。

<a id="DOC-4B"></a>
### DOC-4B — 内部実装コンポーネント軸によるファイル分断とユーザー目的軸の欠如
- **対象ファイル & 行:**
  - `docs/user-guide/*.md` (6ファイル: `telemetry-collection.md`, `local-monitor.md`, `raw-data-loop.md`, `static-dashboard.md`, `diagnosis-improvement-loop.md`, `data-safety.md`)
- **観測された事実:**
  - ドキュメントがユーザーのやりたいこと（例：「Copilot Chatの実行履歴を見たい」「トークンコストを分析したい」「エラー原因を特定したい」）ではなく、開発側の内部モジュール軸でファイル分割されている。
  - そのためユーザーはどのファイルを読めば自分の目的が達成できるのか直感的に理解できない。
- **改善要件:**
  - ユーザーのメンタルモデルに沿ったタスク指向・機能指向の章立てに変更する。

<a id="DOC-4C"></a>
### DOC-4C — ユーザーマニュアル内への開発者用・内部設計仕様用語の混入
- **対象ファイル & 行:**
  - `README.md` Line 186 (`初回 trace 確認は Issue #69 の責務です。`)
  - `docs/user-guide.md` Lines 40-75 (`sanitized-export-control.v1`, `canonical carrier`, `preview_digest` 等)
  - `docs/user-guide/local-monitor.md` Lines 77-88
- **観測された事実:**
  - 利用者向けユーザーマニュアルの中に、GitHub Issue番号（`Issue #69`）、内部データフォーマット名（`sanitized-export-control.v1`）、開発者用語（`canonical carrier`, `bounded detail`, `handoff`）が未説明のまま頻出している。
  - これにより一般ユーザーがドキュメントを読んだ際に強い混乱と難解さを感じる。
- **改善要件:**
  - エンドユーザー向けマニュアルから内部設計・開発者専用用語を排除し、ユーザー目線の分かりやすい言葉に置き換える。

---

## 観点5: 日本語表現の硬さ・AI臭さ（humanizer-ja 観点での不自然さ） (DOC-5)

<a id="DOC-5A"></a>
### DOC-5A — 「〜することができます」「〜となります」「〜について説明します」等の冗長表現の多用
- **対象ファイル & 行:**
  - `README.md` Lines 16, 45, 68, 84, 95, 106
  - `docs/user-guide.md` Lines 3, 10, 38, 68, 73
  - `docs/getting-started.md` Lines 30, 49, 83
- **観測された事実:**
  - `humanizer-ja` でアンチパターンとされる「〜することができます」「〜となります」「〜という形になります」「〜について解説します」といったAI生成文書特有の冗長な語尾や被疑表現がドキュメント全体に蔓延している。
  - 例（README L16）: `...ひとつひとつの実行ステップを span ツリーとして可視化できるようになります。` → `実行ステップを span ツリーで可視化します。`
  - 例（docs/user-guide.md L3）: `このガイドは Copilot Agent Observability を使う人向けの入口です。` → メタ的で不自然。
- **改善要件:**
  - `humanizer-ja` のスタイリング原則に従い、無駄な冗長表現を削り、体言止めや能動的で直接的な自然な日本語表現に全面的に書き換える。

<a id="DOC-5B"></a>
### DOC-5B — 直訳英単語・名詞句の未翻訳露出による可読性低下
- **対象ファイル & 行:**
  - `README.md` Lines 166, 185
  - `docs/user-guide.md` Lines 27, 49, 77, 86
- **観測された事実:**
  - `reversible setup`, `redacted plan`, `telemetry evidence`, `first-trace begin`, `user settings`, `caller-managed guidance` などの英単語・フレーズがそのまま日本語文章に挟み込まれており、文章の流れを著しく害している。
- **改善要件:**
  - 適切な日本語（例: 「復元可能なセットアップ」「マスキング済み計画」「テレメトリの到達証拠」など）に翻訳・再構築する。

<a id="DOC-5C"></a>
### DOC-5C — ファイル間・ファイル内での文体（です・ます / だ・である）の不統一と受動態の乱用
- **対象ファイル & 行:**
  - `docs/getting-started.md` (常体「〜する」「〜を参照する」) vs `README.md` / `user-guide.md` (敬体「〜です」「〜ます」)
- **観測された事実:**
  - `getting-started.md` では「〜を参照する」「〜を生成する」と常体が使われ、`README.md` では「〜です」「〜ます」が使われるなど、リポジトリ全体でトーン＆マナーが崩れている。
  - 「〜が生成されます」「〜が実行されます」と過剰な受動態が連続し、人間が書いた文章らしい自然なリズムがない。
- **改善要件:**
  - 全ドキュメントの文体を丁寧な敬体（「です・ます」）に統一し、読みやすい能動文を基本とする。
