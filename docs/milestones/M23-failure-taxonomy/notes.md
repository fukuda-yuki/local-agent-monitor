# Notes

この milestone 内に閉じる調査結果、判断理由、検証メモを記録する。

## 2026-06-03

M23 は M24 trace-to-diagnosis MVP の実装ではなく、その前提となる人間確認用の分類体系を定義する milestone として扱った。

taxonomy は M20 rubric の `fail` / `needs-review` を細分化し、M21 の baseline / variant 比較や M22 の report template に書ける短い分類値を提供する。
ただし、taxonomy の分類結果は改善候補の入力にすぎず、自動採用、自動実装、自動 repository 修正、自動勝敗決定を意味しない。

M17 の `failure_type` は Copilot 実行失敗、Langfuse unavailable、trace missing、wrong attributes、real data risk などの run / trace 取得・除外理由を記録するための分類である。
M23 の taxonomy は回答品質、trace 根拠、measurement schema、比較プロトコル、データ扱い、報告品質に関する改善検討用の分類であり、M17 の `failure_type` を置き換えない。

分類記録には trace id、task id、client kind、category id、anti-pattern id、severity、sanitized evidence summary、recommended improvement target を使える。
実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity は保存しない。
