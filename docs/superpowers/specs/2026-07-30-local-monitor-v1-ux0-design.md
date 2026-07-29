# Local Monitor v1 UX0 — 統合設計

Status: **Product Owner visual review pending**  
Issue: #153  
Date: 2026-07-30

## 1. 製品境界

Local Monitor v1 は、次の二層構造とする。

1. **AIなしで完全に利用できる観測・調査基盤**
   - Repository と Session を見つける
   - Token / cache / Skill / Tool / Sub-agent / error / retry / timing / parent-child relation を確認する
   - Tool入力・結果、Sub-agent入力、Skill本文などの raw-local 情報を必要時に確認する
   - exact Session / Run / Trace / Span / Event / raw record へドリルダウンする
2. **任意のAI分析・AI改善提案**
   - v1 provider は GitHub Copilot SDK
   - 利用者の明示操作でのみ実行
   - 未設定・未認証・失敗・timeoutでもコア機能は影響を受けない
   - AI出力は観測事実と分離し、exact evidenceへ戻れる

人が空のフォームへ改善案を書くworkflow、manual evidence selection、candidate/recommended中心のproposal lifecycleは、Local Monitor v1の主要導線にしない。

## 2. 全体IA

恒久サイドバーは置かない。主要導線は次の階層とする。

```text
Repositoryを選ぶ
  -> Repository内のSessionを探す
      -> Session詳細を開く
```

共通ヘッダは次だけを持つ。

```text
[パンくず]                                      [受信状態] [設定]
```

- パンくずが上位階層へ戻る主導線
- AI状態は常設しない
- global searchは置かず、検索はRepository/Session文脈内に置く
- `受信状態` と `設定` は統合Settings modalを開く

## 3. Repository選択

- Repositoryはカード表示
- カードに表示するもの
  - 表示名
  - 割り当てられたSession件数
  - 最終観測時刻
- `すべてのSession` は補助入口
- `Repository未割り当て` は該当時だけ表示
- `Repositoryを追加` を提供
- `アーカイブ済みRepository` へ再訪可能
- Repository identityはLocal Monitor生成のopaque UUIDv7。表示名・URL・pathをidentityにしない

## 4. Session Explorer

Repositoryを選ぶと、密度の高いSession一覧を表示する。汎用KPI/利用状況Dashboardは作らない。

表示項目:

- 最初に記録された指示の短縮表示。取得できない場合は日時ベースの一般ラベル
- Source / model / capture note
- 状態
- 正に観測されたSkill / Tool / Sub-agent / Error / Retry
- 記録Tokenとcache-read ratio（利用可能な場合のみ）
- 開始時刻と所要時間

操作:

- 指示・Skill・Tool検索
- 期間 / Source / model / activity filter
- 行選択でSession詳細を直接開く。preview paneは置かない
- row overflowからarchive
- `比較を作成` でselection modeへ入る。通常時はcheckboxを表示しない

## 5. Repository Session Compare

汎用集計Dashboardではなく、利用者が明示した2 cohortの比較を提供する。

```text
基準 cohort A  vs  変更後 cohort B
```

主な利用例:

- Skill変更前後
- AGENTS.md / instruction変更前後
- Agent / Tool構成変更前後

原則:

- exact Session IDsまたは明示filter snapshotで選択
- Skill変更境界はSession時点のexact Skill snapshot digestがある場合だけ候補化
- name/time/prompt similarityからbefore/afterを推測しない
- archived Sessionはdefault exclusion
- simple totalではなくper-Session median、range/distribution、available denominatorを主役にする
- Token / cache / duration / Tool / Error / Retry / Sub-agent / Skill / coverageを比較
- 基本結論は`観測された差`。quality-first条件が揃わない限り`改善`/`悪化`と呼ばない
- AIなしで比較可能。provider ready時だけ`この比較をAIで分析`を追加
- permanent saved comparison indexはv1で作らない

## 6. Session Workspace

Session詳細は、**実行ワークスペース + 詳細インスペクター**とする。

### 上部

- 最初に記録された指示
- status / source / start-end / duration
- capture warning（問題がある場合だけ）
- Session全体の固定サマリー
  - 記録Token: input/output横バー
  - cache: cache-read/new-input横バー、cache creationは値がある場合に補足
  - Skill / Tool / Sub-agent / Error-Retry

Tokenとcacheを同じ階層で混ぜない。

```text
記録Token
  -> input / output

キャッシュ利用
  -> cache read / new input
  -> cache creation（補足）
```

### 実行の流れ

階層型タイムラインを使う。

```text
Session
  -> 実行
      -> Agent / Skill / Tool / Sub-agent / Event / Error / Retry
```

- 左: exactまたは明示された親子関係
- 右: 開始位置・duration・並列性
- 単純treeとwaterfallの別タブは作らない
- 複数実行では最新だけdefault展開
- 折りたたみ中もError / Retry等の要約を残す
- parent/timeが不明な項目は推測配置しない

### 右インスペクター

共通骨格を持ち、対象に応じて主要情報を切り替える。

- Tool: input / result / exit / retry / child activity
- Skill: invocation / source / trigger / available inventory / body / definition location
- Sub-agent: exact input / activity / lifecycle / child activity
- Error / permission / event: status / message / exact relation
- raw IDsとraw OTLPは`技術情報`に折りたたむ
- page-level `整形/raw` tabは作らない

Skill本文と絶対パスはsanitized projectionへ混ぜず、raw-defaultで明示展開した場合だけ表示する。Session時点snapshotとcurrent fileを混同しない。

## 7. AI分析

### Session全体

- Session headerの`AIで分析`
- immutable Session snapshotへbind
- primary reportを永続保存
- 再分析はnew run。過去結果を上書きしない
- 最新結果をdefault表示し、`再分析` / `過去の分析`を同じsurfaceに置く
- follow-up Q&Aはv1では履歴保存しない

### 詳細対象

- inspectorの`この項目をAIで分析`
- selected exact nodeをanchorにし、必要なSession contextだけを追加
- 利用者向けのdurable historyには追加しない
- current UI session内の一時結果・follow-upとして扱う

### Repository範囲

- Session Explorerのcurrent filterまたはexplicit selectionをscopeにする
- previewでincluded/excluded/archive/completeness/content/truncationを確認
- 200件超をsilent truncateしない
- existing #72–#75 backendを再配置し、別engineを作らない
- permanent Repository analysis historyは作らない

### Data boundary

- SQLite fileをAIへ渡さない
- arbitrary SQL権限を与えない
- Local Monitorがbounded read-only snapshot/process-internal toolsを提供
- AI結果: scope/snapshot、summary、findings、improvement suggestions、evidence、limitations、provider/model/template provenance
- AI結果をtimelineの観測ノードとして混ぜない

## 8. Archive

Session / Repositoryの可逆archiveを提供する。

- archiveは削除・retention・pinではない
- archived Sessionはdefault list / Compare candidate / Repository-range AIから除外
- direct deep linkで開ける。明示単一Session AI分析は可能
- Repository archiveはSession archiveをcascadeしない
- new event/assignmentが来ても自動restoreしない
- Settings内からarchived itemsを管理・restoreできる

## 9. Unified Settings modal

設定は独立dashboardではなく、headerの`受信状態`/`設定`から開く960×640程度のmodalに集約する。

左ナビ:

- 状態
- 受信
- AI設定
- Repository
- アーカイブ
- 保存・バックアップ
- 診断

原則:

- 状態と主要操作を同じ画面で確認
- 難しい操作は詳細画面/確認dialogへ進む
- 通常画面にAI status、backup、retention、diagnosticsを常設しない
- page contextはmodal背後に残す

## 10. `--sanitized-only`

raw-default UIと`--sanitized-only`縮退UIを二重実装しない。`--sanitized-only`ではLocal Monitorの人間向けUIを提供しない方向を#159で契約化する。

既存frozen API/SSE、ingest、health等のterminal behaviorは#159で確定する。

## 11. 欠落状態の日本語

内部用語をそのまま表示しない。

- 値あり: `1件を記録`
- 今回の記録で見つからない: `今回の記録にはありません`
- Sourceが提供しない: `この取得元では記録できません`
- capture gap: `記録が一部欠けています`
- 値は表示できるが安定性未確認: `安定して取得できるか未確認です`

`0件`はcoverageがcompleteと証明できる場合だけ使用する。

## 12. Visual review set

1. Repository selection
2. Session Explorer
3. Session Explorer compare-selection mode
4. Repository Compare
5. Session Workspace + Tool inspector
6. Session AI result
7. Unified Settings modal

すべて1366×768、恒久サイドバーなし、page-level horizontal scrollなしで確認する。

## 13. 実装前ゲート

```text
#153 UX0 visual approval / close
  -> contract Issues (#155/#157/#159/#160/#162/#165 等)
  -> #132 C3 final IA spec
  -> #118 S0 canonical docs
  -> implementation Issues
  -> #147 validation
  -> #148 user docs
```
