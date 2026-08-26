# Task 2 report — executable Session detail contracts

## RED

Command:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~LocalMonitorV1SessionDetailSpecificationTests
```

Result: exit 1; 4 failed, 1 passed. The failures proved the three schemas and five literal fixtures were absent. The cursor assertion also rejected the initial placeholder literal.

## GREEN

- Session detail specification suite: exit 0; 5 passed, 0 failed.
- Frozen Repository/Session collection specification suites: exit 0; 7 passed, 0 failed.
- `git diff --check`: exit 0.

## Review fix round 3

### RED

The focused suite exited 1 with 1 failure and 7 passes after route-specific
header value and exhaustive query assertions were added. All four route rows
still exposed header names as strings, so they could not freeze values and the
content row omitted its required schema-version header.

### Fixes

- Changed each route's required headers to literal ordered `{name,value}`
  entries. Only content carries `X-Local-Monitor-Schema-Version` with the exact
  content schema token; JSON routes independently prove that they do not.
- Independently asserted every property of all six query fields, including
  required routes, dependency, canonical pattern, bounds, default, and enum.
- Independently asserted every generated query array and cross-checked it with
  each transport route's literal query order.

### GREEN

- Session detail specification suite: exit 0; 8 passed, 0 failed.
- Frozen Repository/Session collection specification suites: exit 0; 7 passed, 0 failed.
- `git diff --check`: exit 0.

## Artifact inventory

- Three self-contained, closed Draft 2020-12 schemas for Summary, Timeline, and Node.
- Five literal compact UTF-8 golden responses with exact recursive property order and no trailing newline.
- One executable specification suite covering schema/version tokens, schema validation and recursive object closure, property order, the literal 119-byte/159-character zero-key cursor, all fixed error bytes, GET/HEAD/405 behavior, and raw content headers/part allowlist/size ceiling.

## Iteration notes

- The first GREEN attempt remained red because the cursor literal was copied with the wrong length, fixture files retained a trailing newline, and the Node schema was missing one closing object delimiter. These were corrected and the exact focused command was rerun successfully.
- No runtime route, serializer, existing collection schema, existing collection fixture, or collection cursor was changed.

## Review fix round 1

### RED

After adding independent assertions for the reported defects, the focused suite exited 1 with 4 failures and 3 passes:

- the timeline cursor was not bound to the page fixture;
- the literal transport table was absent;
- the schemas lacked the asserted collection-equivalent facts and exact conditionals;
- the Node fixture had no representative nested paths or relations.

A later attempted negative-instance harness also stayed red because it incorrectly used
PowerShell `-Command`, which did not bind the trailing schema and instance paths to
`$args`. Round 2 identified that invocation defect and replaced it with
`-CommandWithArgs`; the restored harness now rejects literal mutants against the real
schema rather than accepting them through a vacuous subprocess invocation.

One self-review `rg` command used a Windows-incompatible filename glob and exited with an OS path-syntax error. It was rerun with `-g 'session-*.response.schema.json'` and completed successfully.

### Fixes

- Regenerated the literal cursor against the exact timeline fixture Session, revision, execution, null parent, effective limit 1, and final-item time/node position. Tests independently decode every field, prove signed/unsigned big-endian interpretation, verify both HMACs, mutate revision/execution/parent/limit bindings, and exercise stable ordering groups.
- Added literal `transport-contract.json` and `query-grammar.json` tables for four routes, GET/HEAD/405, closed query grammar, precedence, status/error byte pairs, success/error headers and Content-Length rules, forbidden headers, ceilings, and raw parts.
- Restored exact collection constraints for source/model cardinality, nonempty recorded facts, assignment/archive, label, statuses, token components, and capture notes; removed invented generic identifier/text maxima.
- Added exact timing state branches and canonical cursor padding-bit restriction to all affected schemas and the owning detail specification.
- Added `node-nested.json` with nonempty parent path and retry/recovery/children representations.
- Added a byte scanner proving all literal artifacts contain no JSON whitespace outside strings.

### GREEN

- Session detail specification suite: exit 0; 7 passed, 0 failed.
- Frozen Repository/Session collection specification suites: exit 0; 7 passed, 0 failed.

## Review fix round 2

### RED

The focused suite exited 1 with 2 failures and 6 passes after independent tests were
added: route rows did not carry their own success/media/schema/header contract, and a
state mutant was accepted. After schema/artifact repair, the first verification rerun
also exited 1 with 2 failures and 6 passes because the newly added literal fixture had
a trailing newline. The fixture was normalized and the exact focused run was repeated.

### Fixes

- Made Host the literal first precedence stage and gave every route an independently
  asserted path, query order, GET/HEAD set, success status/media/schema token, 405
  status, and required response headers.
- Corrected source/model/version to the collection contract's one-way rule:
  `recorded` requires evidence, while non-recorded states may retain evidence. Added a
  positive literal fixture proving that permitted case.
- Restored the negative-instance schema harness with correct PowerShell argument
  binding. Literal mutants now prove timing, assignment, archive, instruction, fact
  values, content availability, and technical-reference state constraints.
- Added Session-ID cursor binding mutation plus independent recorded-tick and node-ID
  tie-break comparisons.

### GREEN

- Session detail specification suite: exit 0; 8 passed, 0 failed.
- Frozen Repository/Session collection specification suites: exit 0; 7 passed, 0 failed.
- `git diff --check`: exit 0.
