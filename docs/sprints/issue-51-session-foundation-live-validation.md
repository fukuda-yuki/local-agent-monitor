# Issue #51 Live Validation Record

This record intentionally contains no prompts, responses, tool payloads,
credentials, transcript paths, or machine-specific home-directory paths.

## Scope

The automated suite validates synthetic SDK, Hook, and OTel ingestion. Real
GitHub Copilot CLI Hooks, VS Code Preview Hooks, and Canvas SDK runtime behavior
remain manual because enabling them would modify the user's environment or
depend on preview services.

## Canvas SDK runtime

1. Start Local Monitor on its documented loopback URL.
2. Open the installed Canvas extension in a disposable Copilot Session.
3. Send one synthetic instruction that invokes a harmless tool and completes.
4. Re-open the Canvas for the same Session.
5. Query the sanitized Session workspace endpoints.

Expected: one native Session is resolved from `ctx.sessionId`; the first open
records one capture-boundary gap; the second open does not add another SDK
subscription; persisted message/tool/lifecycle events and aggregate usage are
present; reasoning and streaming deltas are absent.

Status: not run in this repository-safe implementation session.

## Copilot CLI Hook

1. Set a temporary home directory and run the opt-in Hook install script.
2. Confirm the managed configuration contains the seven PascalCase events.
3. Run a disposable CLI Session with Local Monitor available, then unavailable.
4. Run the uninstall script twice.

Expected: Hook execution always exits zero with no output; exact native identity
resolves one Session while the collector is available; collector outage never
blocks Copilot; install/uninstall are idempotent and never replace an unmanaged
same-name file.

Status: synthetic temporary-home coverage passed; real CLI Session not run.

## VS Code Preview Hook

1. In a disposable VS Code profile with Agent Hooks available, opt in using the
same managed Hook configuration.
2. Run a synthetic Agent Session and inspect only sanitized Session endpoints.

Expected: supported PascalCase events are captured. If the preview feature is
unavailable, the adapter reports feature unavailable and no surface is guessed.

Status: not run; Preview availability is environment-dependent.

## OTel exact linkage

1. Export a synthetic trace whose `gen_ai.conversation.id` exactly equals a
captured native Session ID.
2. Export a second trace in the same repository and time window with a different
conversation ID.

Expected: only the first trace enriches the captured Session; the second remains
an independent `unbound` Session. Repository and timestamp proximity never bind
Sessions.

Status: covered by synthetic SQLite integration tests; external exporter not run.
