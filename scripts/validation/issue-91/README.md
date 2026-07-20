# Issue #91 output scanner

`scan-outputs.ps1` scans required UTF-8 API, SSE, UI, action, log, or evidence
artifacts against the versioned synthetic corpus. A missing, empty, unreadable,
or malformed required target fails closed. The scanner prints only case IDs,
transformation labels, counts, and the bounded result; it never prints matched
marker values.

Supported transformations are exactly `plain`, `json_escape`, `html_entity`,
`url_percent`, `base64_utf8`, and `sha256_prefix_12`. Transformations are not
recursively composed. Compression, encryption, fuzzy matching, process memory,
network traffic, and enterprise DLP/privacy certification are out of scope.

Run the deterministic self-test:

```powershell
pwsh -NoProfile -File scripts/validation/issue-91/test-scan-outputs.ps1
```

Run the complete dependency-independent automated matrix. The runner consumes
the versioned manifest, rejects any declared filter that discovers zero tests,
and exercises the semantic matrix validator:

```powershell
pwsh -NoProfile -File scripts/validation/issue-91/run-automated-matrix.ps1
```

Scan a required repository-safe artifact:

```powershell
pwsh -NoProfile -File scripts/validation/issue-91/scan-outputs.ps1 `
  -InputPath <artifact> -OutputType evidence
```
