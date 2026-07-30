# Retention Raw Replay Test Clock Design

## Problem

`RetentionRawReplayStoreTests` creates its Retention catalog with a fixed
`TimeProvider` at `2026-07-23T04:05:06Z`, but constructs
`RetentionRawReplayStore` without that provider. The store therefore uses
system time for its read request. Once system time reaches the captured
sensitive bundle's seven-day expiry, a newly created test bundle is treated as
expired and the deterministic test fails with `Denied`.

## Design

Keep production behavior and public contracts unchanged. The test fixture owns
one fixed `TimeProvider`, passes it to both `RetentionCatalogStore` and every
`RetentionRawReplayStore` created by the fixture, including reopened catalog
tests. This matches the existing raw-replay startup and lifecycle test pattern.

Do not derive fixture time from the system clock and do not expose or couple the
catalog's internal clock to consumers.

## Verification

Use the existing four failing assertions as the RED evidence. After the
fixture-clock correction:

1. Run all `RetentionRawReplayStoreTests`; all six cases must pass.
2. Run the ConfigCli test project.
3. Run the repository validation suite, including the complete solution test.

No product specification update is required because runtime behavior, TTL,
routes, schemas, and error mappings do not change.
