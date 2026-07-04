# M2: Config CLI Profile Output

## Goal

Implement profile-aware Config CLI output for the Sprint6 profiles.

## Scope

- Add a profile-aware command or option that reads `CAO_COLLECTION_PROFILE`.
- Generate VS Code, Copilot CLI, and Codex App configuration samples where each
  client supports the selected profile.
- Keep existing explicit commands such as `langfuse-vscode-env` and
  `collector-vscode-env`.
- Emit credential placeholders. Use documented local defaults for Docker
  Desktop endpoints, and placeholders for environment-specific non-local
  endpoints.
- Never emit real secrets or authorization headers to repository files.

## Verification

- Unit tests cover each Sprint6 profile with synthetic values.
- Existing config sample tests continue to pass.
- `dotnet build CopilotAgentObservability.slnx`
- `dotnet test CopilotAgentObservability.slnx`
