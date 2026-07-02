# Sprint16 M5 Live Canvas Runtime Validation Blocker

## Blocker

Live Canvas runtime validation could not be completed in this session because
the required GitHub Copilot Canvas runtime tool/session was not available to
copy, reload, open, and invoke the extension in user-scope and project-scope
Canvas environments.

Automated tests and contract tests are useful supporting evidence, but they do
not replace live Canvas runtime validation.

## Required live validation checks

Run these checks with synthetic traces only:

- Copy `.github/extensions/otel-monitor-canvas/` into a user-scoped Canvas
  extension location, reload extensions, and open the Canvas helper.
- Repeat the copy, reload, and open checks in a project-scoped target
  repository.
- Ingest one synthetic trace with `repo.name`, `workspace.name`, and
  `repo.snapshot`.
- Ingest one synthetic trace without repository metadata to verify the
  `unknown repository` fallback.
- Verify the helper page displays extension scope, monitor URL, readiness,
  repository/workspace labels, manual repository/workspace filtering, and the
  `unknown repository` fallback.
- Invoke the bounded Canvas actions and confirm responses contain no raw
  prompt/response body, tool arguments/results, PII, credential, token, raw
  OTLP payload, or local path.

## Evidence still needed

Record the live validation date, Canvas runtime/tool names, extension scope,
target repository or workspace label, monitor URL, synthetic trace identifiers,
checks performed, action names invoked, pass/fail results, and any remaining
unverified scope.
