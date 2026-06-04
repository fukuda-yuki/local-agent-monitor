# Sprint2: Raw Data Loop

Sprint2 は、Sprint1 の Langfuse PoC を踏まえ、Langfuse に依存しない raw telemetry store と改善ループを構築する sprint である。

現時点では M1 で正式仕様化を開始している。
Sprint2 MVP の入力、raw store、CLI interface、data handling は `../../requirements.md` と `../../spec.md` に反映済みである。
本 README は概要であり、正式な実装判断は `../../requirements.md` と `../../spec.md` を優先する。

## Milestones

| Milestone | 状態 | 概要 |
| --- | --- | --- |
| [M1: Sprint2 仕様化](milestones/M1-sprint2-specification/task.md) | 進行中 | raw store / normalize / Langfuse 非依存 loop の MVP 境界を requirements / spec へ反映し、後続 milestone と task breakdown を作成する |

## 目的

Langfuse 依存の PoC から、raw telemetry / normalized dataset / improvement loop を中心にした構成へ移行する。

Langfuse は raw data の source of truth ではなく、人間が trace を確認する dashboard / trace viewer の optional side path として再位置づけする。

## MVP 方針

### 生 JSON 保持基盤

- Sprint2 MVP の既定入力は raw OTLP の file-based ingest とする。
- 自前 HTTP receiver、常駐 process、独自 OTLP receiver は Sprint2 MVP に含めない。
- ローカル PoC の既定 raw store は SQLite とする。
- PostgreSQL は共有環境、長期保持、検索性能、複数利用者検証が必要になった段階の候補とする。
- 既定 DB path は `data/raw-store.db` とし、`data/` は repository に commit しない。

最小 raw record:

| Column | Purpose |
| --- | --- |
| `id` | SQLite `INTEGER PRIMARY KEY AUTOINCREMENT` |
| `source` | `raw-otlp`, `collector-output`, `langfuse-export` などの入力元 |
| `trace_id` | 取得できる場合の trace id |
| `received_at` | raw record を取り込んだ日時 |
| `resource_attributes_json` | Resource Attributes の raw JSON |
| `payload_json` | 生 payload |
| `schema_version` | raw store 側の schema version。MVP は `1` |

### Normalized Dataset

- raw OTLP / raw JSON から analysis 用の normalized dataset を生成する。
- 既存 measurement schema の `trace_id`、`experiment_id`、`client_kind`、`task_id`、token、duration、error、tool call count などへ接続する。
- 欠損値は欠損として保持し、未知 span 名や未知属性は破棄せず補助 JSON として保持する。
- 実 prompt / response content、tool arguments / results、credential、secret、実 user identity は repository に保存しない。

### 改善ループ

改善ループは Langfuse API ではなく、raw store または normalized dataset を主入力にする。

```text
collect -> normalize -> diagnose -> propose -> evaluate -> human decision
```

- `collect`: raw telemetry / export を取り込む。
- `normalize`: 研究用 schema と改善支援 CLI が読める形に変換する。
- `diagnose`: 人間が分類した diagnosis record を検証する。trace からの自動診断は Sprint2 MVP に含めない。
- `propose`: 人間レビュー用の改善提案を deterministic に生成する。
- `evaluate`: proposal の安全性、仕様整合、レビュー観点を事前確認する。
- `human decision`: 人間の approved / rejected / deferred を記録する。

自動採用、自動実装、repository 自動修正、patch / diff 生成、commit / push / pull request 作成、自動勝敗決定は引き続き非スコープとする。

### Langfuse UI

- Langfuse は source of truth ではなく dashboard / trace viewer の optional side path として扱う。
- raw store に保存されたデータを改善ループの主入力にする。
- Langfuse が起動していなくても normalized dataset から改善支援ループを実行できることを Sprint2 MVP の成功条件にする。
- Langfuse に投入するデータは、可視化・探索・人間確認のための副経路として扱う。

## 成功条件

- synthetic raw OTLP JSON を取り込める。
- raw store に保存できる。
- raw store から normalized dataset を生成できる。
- normalized dataset から既存 diagnosis / proposal / evaluation / human decision に流せる。
- Langfuse が起動していなくても改善支援ループが動く。
- Langfuse UI は同じ trace を人間が見る dashboard として説明されている。
