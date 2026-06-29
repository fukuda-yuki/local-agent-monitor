# Contributor Guide

contributor 向けの作業ガイドです。

## Source Of Truth

仕様判断は次の順で確認します。

1. [requirements.md](requirements.md)
2. [spec.md](spec.md)
3. [specifications/](specifications/)
4. [architecture.md](architecture.md)
5. [decisions.md](decisions.md)
6. 実装と tests

`docs/sprints/` は履歴です。
新しい product behavior は sprint-local notes だけに置かず、必ず current specification に反映してください。

## Build And Test

Code、project file、CLI behavior、workflow を変更した場合:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test CopilotAgentObservability.slnx
```

The browser install is part of the standard validation sequence because the
solution test suite includes LocalMonitor Playwright smoke coverage. On Linux
CI, run the same script with `install --with-deps chromium`.

Collector example を変更した場合:

```powershell
$env:LANGFUSE_AUTH="dummy"
docker compose -f infra\otel-collector\docker-compose.example.yml config
```

## Documentation Changes

User workflow を変えた場合:

- `README.md`
- `docs/user-guide.md`
- relevant `docs/user-guide/*.md`

Public interface や product behavior を変えた場合:

- `docs/requirements.md`
- `docs/spec.md`
- relevant `docs/specifications/*.md`
- tests

## Data Safety

Commit 前に確認すること:

```powershell
git status --short
```

Commit してはいけないもの:

- raw prompt / response。
- tool arguments / results。
- observed source fragments / file contents。
- credential、secret、token、API key、password。
- Base64 authorization header。
- sensitive bundle content or local path。
- local runtime outputs under `data\`, `tmp\`, `artifacts/dashboard-input\*.json`。

## Review Checklist

Documentation-only の場合:

- source of truth と説明が矛盾していない。
- links and screenshot paths exist。
- 古い入口表現が README や top-level docs に残っていない。

Implementation changes の場合:

- public interface changes are reflected in specs。
- tests cover changed behavior。
- data safety boundary is preserved。
- no unrelated refactor is mixed in。
