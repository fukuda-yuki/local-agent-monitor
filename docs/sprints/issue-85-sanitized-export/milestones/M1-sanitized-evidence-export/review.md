# Issue #85 M1 — Self-Review

Scope: shared sanitized-export service and contracts, Config CLI commands,
Local Monitor loopback routes, canonical schema/golden fixtures, focused tests,
current interface/security/user documentation, and Issue #91 handoff evidence.
No package dependency, lockfile, database migration, remote operation, or shared
future-registry edit was added.

Implementation candidate:
`48d3734106fb572c5d8f013f8935c4288147ee23`.

## Validation executed

```powershell
pwsh scripts\agent\sync-claude-skills.ps1 -Check
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~SanitizedExportServiceTests|FullyQualifiedName~SanitizedExportSurfaceTests"
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SanitizedExportCliTests
```

Observed bounded results:

- Claude skill mirror: up to date, five shared skills.
- Solution build: succeeded with zero warnings and zero errors.
- Playwright Chromium bootstrap: succeeded.
- Local Monitor service/API/archive/scanner tests: 53 passed, zero failed,
  zero skipped.
- Config CLI sanitized export tests: one passed, zero failed, zero skipped.
- Executable CLI `preview`, `export`, and `result`: success; generated bundle
  matched the committed golden byte for byte at SHA-256
  `f752b0a1cac4f5cd91faf8eb70d38a60f61db3124af31ed82c757f884abfeb53`;
  extracted manifest passed the canonical JSON Schema.
- Issue #59 instruction-finding and Issue #80 alert receipt canonical bytes are
  preserved exactly by the bundle fixture tests.

The required `dotnet test CopilotAgentObservability.slnx` command was not run in
this lane after the coordinator directed lanes not to start concurrent full
solution runs. It is not replaced by the focused commands. Matrix row
`91-E-085` is therefore temporarily `not_attempted`, and the release decision
is `release_blocked`. `not_attempted` is forbidden at Issue close.

Exact retry condition: coordinator integrates the accepted candidate, runs
build/bootstrap/full plus the listed focused CLI/API/fixture filters at one
clean evidence SHA, then promotes `91-E-085` to `passed` and recomputes release
decision before Issue close.

## TDD and review findings closed

RED cases first exposed the old `create` verb, incomplete frozen manifest
inspection, unsupported scanner fields/paths/credentials, invalid UTF-8
acceptance, duplicate unselected paths, dependency-state precedence, unsafe
publish exceptions, nullable JSON requests, noncanonical manifest whitespace,
and a deflate-method ambiguity. GREEN implementation now validates the completed
manifest/archive before returning success, bounds ZIP totals and entry reads,
uses strict UTF-8 and nullable request contracts, and clears bytes/hashes on
publication failure.

The scanner executes every Issue #91 corpus marker across its declared plain,
JSON-escaped, HTML-entity, percent-encoded, Base64 UTF-8, and SHA-256-prefix
forms. It also covers recognized Windows drive, UNC/device, WSL mount, common
Unix home/system, file URI, credential/header/token/certificate, email, and raw
field patterns. It remains a bounded negative scanner, not enterprise DLP,
privacy/legal certification, recursive decoding, decryption, decompression, or
secure-erasure proof.

## Foundation boundary

The v1 public request intentionally contains an immutable source-neutral
snapshot and exact canonical record bytes. The Local Monitor database path is
used only to derive the server-controlled sibling output directory; routes do
not query or rediscover database records. This establishes deterministic
selection, exact dependency closure, and adapter-independent validation without
adding a migration. Snapshot construction, completeness, and source
authorization remain caller-owned until a later source adapter is specified.

## Integration-owner handoff

The shared `future-surface-registry.v1` schema permits only `not_available`.
This branch leaves its Issue #85 entry unchanged. After accepting the candidate,
the coordinator must remove the future entry or supersede the registry through
its versioned canonical mechanism; it must not write `active` into v1 or inherit
a pass from the placeholder.

The top-level source-of-truth files were intentionally not edited in this lane.
The coordinator should make these exact promotions:

- `docs/requirements.md`: add a required sanitized evidence sharing capability
  covering explicit selection/dependency closure, deterministic versioned
  bundle/manifest, fail-closed repository-safe validation, CLI and loopback API,
  explicit unavailable optional capabilities, and the no upload/sign/encrypt/
  import/replay/backup/restore boundary.
- `docs/spec.md`: add Sanitized Evidence Export to current product shape and the
  public-interface map, pointing to
  `docs/specifications/interfaces/sanitized-evidence-export.md`.
- `docs/decisions.md`: record the accepted source-neutral immutable-snapshot
  foundation, deterministic ZIP-store/checksum contract, bounded negative
  scanner limitations, and caller-owned snapshot authenticity boundary.
- `docs/task.md`: mark Issue #85 implementation at the accepted candidate and
  retain the integrated full-gate / Issue #91 registry transition as pending
  until coordinator evidence is complete.

Self-review found no change to the four files above, no real data or credential,
no sensitive bundle path, no generated runtime artifact, and no unrelated diff.
