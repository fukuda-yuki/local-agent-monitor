# Review: commit `15fe4cbf` (Japanese readability pass across user-facing docs)

Scope reviewed: commit `15fe4cbfa85cd43cce1bd6d76e5bf4c967bafd0b`
("doc-ux-readability: docs: improve Japanese readability across all
user-facing documentation"), diffed against its parent `15fe4cbf~1`
(`73fa046a`).

Files touched by the commit: `README.md`, `docs/getting-started.md`,
`docs/user-guide.md`, `docs/user-guide/data-safety.md`,
`docs/user-guide/diagnosis-improvement-loop.md`,
`docs/user-guide/local-monitor.md`, `docs/user-guide/raw-data-loop.md`,
`docs/user-guide/static-dashboard.md`,
`docs/user-guide/telemetry-collection.md`,
`docs/user-guide/troubleshooting.md`.

Source of truth checked: `docs/requirements.md`, `docs/spec.md`,
`docs/task.md`, `docs/decisions.md` (D042), and
`docs/BUG_ISSUE/doc-ux-readability-issues.md` (DOC-1..DOC-5).

This file records review findings only. No fix is applied here.

---

## Summary

| Sub-Card | Category | Target File & Lines | Severity | Status | Summary |
| --- | --- | --- | --- | --- | --- |
| **[DOC-6A](#DOC-6A)** | Diff hygiene | `docs/user-guide/static-dashboard.md`, `docs/user-guide/telemetry-collection.md` | High | Open | 全行が CRLF に変換され、実質数行の変更が数百行の差分として現れている |
| **[DOC-6B](#DOC-6B)** | Meaning change | `docs/user-guide/telemetry-collection.md` (旧 96-98 行目付近) | High | Open | 「Live VS Code direct telemetry は Sprint7 の未確認項目」という、今も有効な注記が文言修正の範囲を超えて丸ごと削除されている |
| **[DOC-6C](#DOC-6C)** | Scope creep | `docs/user-guide/static-dashboard.md` (冒頭段落) | Medium | Open | 「個別 trace 詳細調査」の遷移先が raw store / sensitive bundle → Local Monitor に無断で変更されており、読みやすさ改善の範囲を超えた内容変更になっている |
| **[DOC-6D](#DOC-6D)** | Coverage claim vs reality | `docs/user-guide/local-monitor.md` | Medium | Open | コミットメッセージは「all user-facing documentation」を主張するが、1121 行中 3 行のみ改善。DOC-4C/DOC-5B と同じ問題（`Issue #69`, `redacted plan`, `change_set_id`, 未翻訳英語多数）が残存 |
| **[DOC-6E](#DOC-6E)** | Tracker hygiene (peripheral) | `docs/BUG_ISSUE/README.md` | Low | Open (pre-existing) | インデックス表が DOC-1〜DOC-5C を "Open" のまま表示しており、`doc-ux-readability-issues.md` 側の "Fixed" 表記と矛盾している |

Items verified as **no problem found** (see "Checked, no issue" section below):
意味の変質なし（A のうち上記以外）、日本語の自然さ（B）、固有名詞・コマンド名・環境変数名の保持（C）、リンクの整合性（D）、`dotnet build` 成功。

---

## 観点A: 意味の変質

<a id="DOC-6B"></a>
### DOC-6B — `telemetry-collection.md`: Sprint7 未確認注記の全文削除（文言短縮ではなく内容欠落）

- **対象:** `docs/user-guide/telemetry-collection.md`（コミット前の "Raw Local Receiver" セクション末尾、`normalize-raw` コマンド例の直後）
- **観測された事実:**
  - 変更前: `Live VS Code direct telemetry は Sprint7 の未確認項目です。`
  - 変更後: この行が跡形もなく削除され、空行だけが残っている（前後の「検証時は...記録してください」という文はそのまま）。
  - ミッションの改善パターン4は「開発者用語の混入 → 除去（例: `Sprint7`）」であり、期待される変更は "Sprint7" という単語を取り除いた自然な文への書き換え（例: 「VS Code からの直接テレメトリ受信はまだ確認できていません。」）であって、文全体の削除ではない。
  - `docs/task.md:85` は本レビュー時点でも次のように記載している: 「VS Code direct telemetry live validation は未確認」。つまり削除された注記は **今も有効な制約情報** であり、古くなって削除してよい記述ではない。
- **影響:** 利用者が「VS Code からのライブテレメトリ受信は未検証」という重要な限定事項を読み取れなくなる。読みやすさ改善のはずが、実質的な内容（未確認ステータスの告知）が失われている。
- **改善要件:** 削除ではなく、"Sprint7" という開発者用語だけを取り除いた同義文に書き換える。例:「VS Code からの直接テレメトリ受信は、まだライブ環境での確認が済んでいません。」

<a id="DOC-6C"></a>
### DOC-6C — `static-dashboard.md`: 冒頭段落で「個別トレース詳細への遷移先」が無断で変更されている

- **対象:** `docs/user-guide/static-dashboard.md` 冒頭
- **観測された事実:**
  - 変更前: 「個別 trace の詳細調査は Langfuse trace viewer、raw store、または明示 opt-in の sensitive bundle へ drill down します。」（遷移先3種）
  - 変更後: 「個別トレースの詳細は Langfuse や Local Monitor のトレース詳細画面で確認します。」（`raw store` と `sensitive bundle` への言及が消え、代わりに `Local Monitor` が追加されている）
  - `Local Monitor` の追加自体は現在の機能を反映しており妥当な可能性が高いが、`raw store` 直接参照や `sensitive bundle`（機密情報を含む詳細検証パス）という選択肢の記述が代わりに失われている。これは英語→日本語の言い換えではなく、ドキュメントが利用者に提示する「調査手段の一覧」という内容そのものの変更であり、ミッションが定義する5パターン（造語簡略化・メタ表現・未翻訳英語・開発者用語・長文分割）のいずれにも該当しない。
- **改善要件:** 内容変更として扱い、`raw store` / `sensitive bundle` への言及を削除してよいかどうかを別途確認する。削除してよい場合も、読みやすさ改善コミットとは別に内容変更として扱い、コミットメッセージまたは変更理由に明記する。

### A: その他（問題なし）

- README.md・`docs/getting-started.md`・`docs/user-guide.md`・`docs/user-guide/data-safety.md` のプロファイル表、セットアップ手順、データ安全ルールは、抽出した各文の技術的主張（コマンド名、プロファイル名、保存可否のリスト内容）が変更前後で一致していることを確認した。緩和・厳格化は見られない。
- `docs/user-guide/local-monitor.md` 冒頭（"UI は Console 型の構成"→"画面は左にサイドバーがある構成"、`208px` 削除）は `docs/decisions.md` D042 / `docs/requirements.md` / `docs/spec.md` が仕様として保持する `Console 型 IA` ・ `208px` という表現と異なる説明になるが、ミッションの改善パターン4が明示的に `208px` を除去例として挙げているため、この1件は指示どおりであり問題としない。

---

## 観点B: 日本語の自然さ

改善が入った10ファイル中、変更が加わった行について次のアンチパターンの残存を検索したが、該当なし:
`することができ`, `という形にな`, `となります。`, `について(解説|説明)し` は README.md / docs/user-guide.md には見つからなかった（修正前は複数箇所に存在）。
文体は敬体（です・ます）に統一されている（`docs/getting-started.md` の常体表現は敬体に書き換え済み）。
この観点については修正済み10ファイルの範囲内で問題なし。

---

## 観点C: 固有名詞・技術用語の扱い

保持すべきとされた用語（`Local Ingestion Monitor`, `Config CLI`, `Langfuse`, `Static Dashboard`, `normalize-raw`, `ingest-raw`, `sanitized-export`, `CAO_COLLECTION_PROFILE`, `COPILOT_OTEL_ENABLED`）を全差分で確認したが、誤って翻訳・改変された箇所はない。ファイルパスもすべて元のまま。問題なし。

---

## 観点D: リンクの整合性

- `利用者向け詳細ガイド` → `ユーザーガイド`、`Getting Started` → `はじめかた` のリンクテキスト変更を全文検索したところ、変更後も参照している箇所は `docs/sprints/` 配下の過去スプリント記録のみであり、`AGENTS.md` の方針どおりスプリント記録は現行仕様の対象外のため問題としない。
- 現行ドキュメント（README.md、`docs/*.md`、`docs/user-guide/*.md`、`docs/specifications/`）内に旧リンクテキストへの言及は残っていない。リンク切れ・テキスト不整合は確認されなかった。

---

## 観点E: `docs/BUG_ISSUE/doc-ux-readability-issues.md` との対応

- DOC-5A（冗長表現の除去）・DOC-5B（未翻訳英語の翻訳）・DOC-5C（文体統一）は、`README.md` と `docs/user-guide.md` の範囲で実例（`reversible setup`, `redacted plan`, `telemetry evidence`, `Issue #69` 等）を検索し、すべて解消されていることを確認した。この2ファイルに関しては DOC-5A/5B/5C は適切に対処されている。
- ただし、DOC-5B/DOC-4C が指摘した問題パターン（未翻訳英語、開発者用語混入）は `docs/user-guide/local-monitor.md` に手つかずのまま残っている。詳細は [DOC-6D](#DOC-6D) を参照。

<a id="DOC-6E"></a>
### DOC-6E — `docs/BUG_ISSUE/README.md` のインデックス表が古いまま（周辺的な指摘）

- **観測された事実:** `docs/BUG_ISSUE/README.md` の "Documentation UX & Readability Fix Cards" 表は DOC-1A〜DOC-5C を全件 `Open` と記載しているが、`docs/BUG_ISSUE/doc-ux-readability-issues.md` 本体では該当カードはすべて `Status: Fixed` になっている。
- **根本原因:** この不整合はコミット `15fe4cbf` が作ったものではない（`15fe4cbf` は `docs/BUG_ISSUE/README.md` を変更していない）。DOC-1〜DOC-5 の各修正コミット（`d36c8afc`, `4f2ed347`, `7148e1b1`, `3f996fea`, `568c6bf7`）がカード本体の `Status` は更新したが、インデックス表側を更新し損ねたことに起因する、既存の記録不整合。
- **改善要件:** `docs/BUG_ISSUE/README.md` のインデックス表を、各カードの実際の `Status`（Fixed）に合わせて更新する。範囲は `15fe4cbf` のレビューを超えるが、追跡精度のため記録しておく。

---

## 観点F: `local-monitor.md` の残存課題（サンプリング確認）

<a id="DOC-6D"></a>
### DOC-6D — `local-monitor.md` は実質未着手。コミットメッセージの「all user-facing documentation」という主張と食い違う

- **対象ファイル:** `docs/user-guide/local-monitor.md`（1121 行、README に次いで利用者が最も読む画面リファレンス）
- **観測された事実:**
  - このコミットで変更されたのは冒頭の3行のみ（"UI は Console 型の構成です..." → "画面は左にサイドバーがある構成です..."、`208px` 削除、段階的動線 → 順に開く）。
  - サンプリングで確認した残存箇所（いずれも本コミットが他ファイルで修正した5パターンと同一の問題）:
    - **DOC-4C 相当（開発者用語の混入）:** 214 行目 `変更前に redacted plan を確認し、返された change_set_id を指定して apply します。`／244 行目 `確認は Issue #69` — README.md と `docs/user-guide.md` では同種の文（`change_set_id` の説明、`Issue #69` 直書き）がすでに利用者向け表現へ書き換えられているが、`local-monitor.md` の並行箇所は未修正のまま。
    - **DOC-5B 相当（未翻訳英語の露出）:** 224 行目 `Repository では wrapper の場所だけが変わります。`（`Repository` が未翻訳）、および画面リファレンス全体で `exact evidence`, `recurring group`, `suppression coverage`, `frozen alert receipt`, `token-gated helper`, `full diff`, `base hash`, `stale`, `fail-closed` など、英語の名詞句が日本語文に多数挟み込まれたまま。
  - コミットメッセージは「Replace ... across 10 files」「improve Japanese readability across all user-facing documentation」と謳っているが、対象10ファイル中もっとも英語混入・開発者用語混入が多いファイルの実質改善量はゼロに近い。
- **改善要件:** `local-monitor.md` を対象からスコープアウトするならコミットメッセージ／PR説明にその旨明記する。改善を続けるなら、他9ファイルと同じ5パターンの是正を `local-monitor.md` 全体（特に "Step 2 — GitHub Copilot をガイド付きで設定する" 節と画面リファレンス節）に適用する。

---

## ビルド検証

```powershell
dotnet build CopilotAgentObservability.slnx
```

結果: 成功（0 エラー / 0 警告）。ドキュメントのみの変更であり、想定どおりビルドへの影響はない。

---

## Checked, no issue

- 意味の変質（プロファイル表・セットアップ手順・データ安全ルール）: DOC-6B・DOC-6C を除き、変更前後で技術的主張は一致。
- 日本語の自然さ（冗長表現・文体統一）: 修正対象10ファイルの範囲で問題なし。
- 固有名詞・コマンド名・環境変数名: すべて保持されている。
- リンクの整合性: 現行ドキューメント内に旧リンクテキストへの参照はなし。
- `dotnet build`: 成功。
