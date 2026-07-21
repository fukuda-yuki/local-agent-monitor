# Issue #91 Historical Evidence Compatibility Preparation

This ledger starts at `matrix_prep_sha`
`5180a0424ff5488354a3e173c74b7e931d28679d` and records the compatibility
adjudication before the final candidate is frozen. It does not itself classify
a final row.

| Source | Reusable bounded observation | Not reusable as a current pass / gap |
| --- | --- | --- |
| #51 | Historical automated Session SDK/Hook/OTel background | No exact live SHA; real Canvas SDK, Copilot CLI Hook, and VS Code Preview Hook were not run |
| #53 | GitHub comment reports Canvas runtime checks | No canonical artifact with exact SHA/version/settings; runtime database must not be reused |
| #99 | Claude 2.1.207 structural OTel and honest drift/unbound observations at `d539dfb392...` | Later projection/UI changes; blocked and unattempted cases remain non-pass |
| #103 | Closed on 2026-07-21; Copilot CLI 1.0.71 producer/provenance observation at `f67e1a0...` | Source-specific historical evidence remains bounded; #105 candidate live evidence supersedes it for common Doctor/release classification |
| #104 | Closed on 2026-07-21; candidate manifest and automated fixture expectations at `54d758a260...` | Not current #91 candidate live evidence; version/settings compatibility still requires final-candidate adjudication |
| #105 | Closed; replacement candidate `b581be98...` passed 6,575 tests and the operator-accepted current-user Release ZIP journey; corrected repository-safe evidence is at `b72309cc...` | Both revisions are ancestors and no later production/package-script change exists. Reuse is limited to Copilot CLI 1.0.71, loopback/content-disabled/current-user setup, real trace, exact navigation, rollback, uninstall, and cleanup; Session remained unbound/N/A. |
| #106/#110 | Claude 2.1.214 content-enabled, gate-disabled, Hook/OTel, exact-linked/unbound, restart/reconnect, and sanitized-only observations through `11d6c587...` | Reuse is limited to the recorded source version, settings, and environment. It does not prove an installed 2.1.215/current source or later shared Doctor navigation behavior; those declared gaps require candidate-current execution or deterministic evidence. |
| #90 | Closed; accepted revision `5180a0424ff5488354a3e173c74b7e931d28679d`, implementation `4d966472...`, and closeout `f412a5bf...` are ancestors | Reusable for deterministic retention mutation/lifecycle/security coverage because no retention production code changed after the accepted revision. The final candidate must newly execute the disposable-database expiry/delete-now runtime rows; historical fields marked N/A are not live passes. |

Candidate-current Claude shared Doctor behavior, disposable retention
expiry/delete-now and post-retention raw denial remain execution gaps before
classification. Canvas action/helper and provider error/tool cases use the
candidate automated matrix unless a final row explicitly requires a live
provider observation. The earlier failed #105 run is superseded only inside
the replacement #105 evidence; it is not rewritten as a #91 pass. Historical
raw captures and databases are never copied into this work item.
