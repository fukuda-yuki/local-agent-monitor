# M2: Receiver Host Model

## Goal

Choose the initial host model for company-managed Windows PCs.

## Options To Evaluate

- Repository `dotnet run` foreground or background process.
- IIS / IIS Express hosting.
- Packaged exe.
- Tray app.
- Windows Service.

## Initial Direction

The required path should not depend on installing a packaged exe.
Repository-local `dotnet run` is the first implementation candidate.
IIS / IIS Express may be added only if it provides a practical always-on path
without weakening the safety boundary.

## Decision

The initial required host model is a repository-local foreground Config CLI
process:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- serve-raw-local-receiver --db data\raw-store.db --url http://127.0.0.1:4319
```

Rationale:

- This path works from the repository without installing a packaged exe.
- The loopback-only URL preserves the Sprint7 safety boundary.
- `4319` avoids the standard OTLP HTTP Collector port `4318`, so users can run
  Collector-based profiles and the raw local receiver at different times
  without reusing the same default port.
- Keeping the initial receiver in Config CLI avoids adding Aspire AppHost
  resources or a separate host surface before the minimal receiver behavior is
  validated.

Rejected for the initial required path:

- IIS / IIS Express: still a future packaging / always-on candidate, but not
  needed for the first repository-local receiver.
- Packaged exe: company-managed PCs may block install, so this remains future
  packaging work.
- Tray app: explicitly outside the initial Sprint7 implementation.
- Windows Service: requires a later operational decision and is not needed for
  the foreground validation path.

## Verification

- Record the selected host model and rejected alternatives.
- Confirm required validation commands and local run instructions.
