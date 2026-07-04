---
name: codex-create-pr
description: "Google Engineering Practices を基に、PR説明文とセルフレビューを日本語で作成する。PR分割、説明文作成、submit前レビューで使う。"
argument-hint: "PRの目的、差分/変更概要、リスクや制約、実施テストを渡してください。"
user-invocable: true
disable-model-invocation: false
---

# codex-create-pr — 質の高いPRを作る

PR説明文とレビュアー視点のセルフレビューを日本語で作成する。

参照は `./references/` 配下にある要約のみを使う。迷ったら、該当ステップの参照ファイルだけ読む。
出力は日本語固定とする。

## 用語の読み替え

- CL（changelist）→ PR（pull request）
- g3doc → リポジトリ内ドキュメント
- LGTM → approve
- style guide → 言語/リポジトリの規約

出力では GitHub の用語に統一する。

## 使うタイミング

- 変更を PR にする前
- PR が大きい/関心事が混ざっていると感じたとき
- PR 説明文を書くとき
- submit 前のセルフレビュー

## 入力

- PR の目的と想定する挙動変更
- 差分、または簡潔な変更概要
- リスク/制約/トレードオフ
- 実施したテスト（なければ理由）

## 出力フォーマット

```text
PR説明文:
<命令形の一文サマリ>

概要:
- ...

理由:
- ...

制約・トレードオフ:
- ...

テスト:
- ...

セルフレビュー:
Blocking:
- ...

Nit:
- ...

良い点:
- ...

結論:
submit 可否と理由
```

## 手順

### 1) スコープと分割

- 1つの自己完結した変更かを確認する。
- 分割判断と目安は参照に従う。
- リファクタリングと挙動変更を分ける。
- 参照: `./references/scope-split.md`

### 2) PR 説明文の作成

- 参照の要点に従って簡潔にまとめる。
- 参照: `./references/pr-description.md`

### 3) submit 前セルフレビュー（核心）

diff をレビュアーとして読む。致命的な設計問題があればそこで止めて報告する。
問題がなければ上から順に確認する。

参照:
- レビューの進め方: `./references/reviewer-navigate.md`
- 観点一覧: `./references/reviewer-looking-for.md`
- 判定基準: `./references/reviewer-standard.md`

### 4) 指摘の分類

- Blocking / Nit は参照の基準に従って分類する。
- 参照: `./references/reviewer-standard.md`

### 5) 結果の提示

- 出力フォーマットに沿って提示する。
- 指摘は Blocking / Nit に分け、理由を添える。
- 良い点も挙げる。
- 「submit 可否」を明確に結論づける。