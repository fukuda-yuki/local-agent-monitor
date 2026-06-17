# M3: diagnosis candidate implementation

## 目的

M2 で確定した rule と sensitive bundle contract に従い、`generate-diagnosis-candidates` の最小実装を追加する。

M3 は M2 の adapter / mapping contract、content-aware deterministic pattern、sensitive bundle manual deletion policy が確定するまで開始しない。

## 完了条件

- [x] normalized measurement CSV / JSON から diagnosis candidate を生成できる。
- [x] raw OTLP JSON または raw store 入力から、M2 で定義した content-aware rule の最小 candidate を生成できる。
- [x] `--include-sensitive-content` 指定時だけ sensitive bundle を生成する。
- [x] standard output と repository fixture に実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を含めない。
- [x] synthetic fixture で metadata-only、error-count、tool-loop、missing metadata、sensitive bundle shape を検証している。

## 検証

- `dotnet build CopilotAgentObservability.slnx`
- `dotnet test CopilotAgentObservability.slnx`

## 結果

- `generate-diagnosis-candidates` を追加し、M2 の 5 つの `DIAG-*` rule に限定して実装した。
- sensitive bundle は opt-in 時だけ生成し、standard CSV / JSON output は raw content を含まない。
- 2026-06-17 に `dotnet build CopilotAgentObservability.slnx` と `dotnet test CopilotAgentObservability.slnx` を実行し、build 成功、181 tests passed を確認した。
