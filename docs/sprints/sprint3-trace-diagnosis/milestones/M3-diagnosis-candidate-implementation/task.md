# M3: diagnosis candidate implementation

## 目的

M2 で確定した rule と sensitive bundle contract に従い、`generate-diagnosis-candidates` の最小実装を追加する。

M3 は M2 の adapter / mapping contract、content-aware deterministic pattern、sensitive bundle manual deletion policy が確定するまで開始しない。

## 完了条件

- [ ] normalized measurement CSV / JSON から diagnosis candidate を生成できる。
- [ ] raw OTLP JSON または raw store 入力から、M2 で定義した content-aware rule の最小 candidate を生成できる。
- [ ] `--include-sensitive-content` 指定時だけ sensitive bundle を生成する。
- [ ] standard output と repository fixture に実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を含めない。
- [ ] synthetic fixture で metadata-only、error-count、tool-loop、missing metadata、sensitive bundle shape を検証している。

## 検証

- `dotnet build CopilotAgentObservability.slnx`
- `dotnet test CopilotAgentObservability.slnx`
