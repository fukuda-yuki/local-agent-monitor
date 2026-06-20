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

## Verification

- Record the selected host model and rejected alternatives.
- Confirm required validation commands and local run instructions.
