---
description: |
  Weekly review for security-labeled Gitleaks issues. It inspects redacted
  Gitleaks finding issues, checks the referenced files and line context, and
  comments with a conservative false-positive or risk assessment.

on:
  schedule:
    - cron: "0 0 * * 1" # 09:00 JST every Monday
  workflow_dispatch:

permissions:
  contents: read
  issues: read
  pull-requests: read

network: defaults

tools:
  github:
    # This workflow needs to read security issues from this repository and
    # inspect referenced files to determine whether findings look intentional.
    lockdown: false
    min-integrity: none

safe-outputs:
  mentions: false
  allowed-github-references: []
  add-comment:
    target: "*"
    max: 10
    discussions: false
---

# Weekly Gitleaks Issue Review

Review open GitHub issues in this repository that have the `security` label.

## Goal

Find issues generated from Gitleaks results and add a concise comment assessing whether each finding is likely a false positive, needs human review, or may be a real secret exposure.

## Scope

- Only review open issues with the `security` label.
- Only comment on issues that appear to be generated from Gitleaks output.
- Skip security issues that are not Gitleaks finding issues.
- Do not add labels.
- Do not remove labels.
- Do not close issues.
- Do not update issue titles or bodies.
- Do not create new issues.

## How to identify Gitleaks issues

Treat an issue as a Gitleaks finding issue when its title or body strongly indicates Gitleaks output, such as:

- Title begins with `[secret-scan]`
- Body contains `# Gitleaks findings`
- Body contains a findings table with these columns: `RuleID`, `File`, `Line`, `Commit`, `Fingerprint`

## Review process

For each Gitleaks issue:

1. Parse the findings table from the issue body.
2. For each row, extract `RuleID`, `File`, `Line`, `Commit`, and `Fingerprint`.
3. Inspect the referenced file and a small amount of surrounding context around the referenced line.
4. Use repository context to classify the issue conservatively.
5. Add one comment to the issue with the assessment.

## Classification policy

Use exactly one of these classifications:

- `likely false positive`
- `needs human review`
- `potential real secret`

Prefer `needs human review` whenever the available evidence is incomplete, ambiguous, or does not clearly prove the finding is intentionally fake.

Classify as `likely false positive` only when there is strong evidence, such as:

- The file is under `tests/fixtures`, `test`, `tests`, `sample`, `samples`, `demo`, `docs`, or another clearly non-production example path.
- Nearby comments or filenames explicitly state that the value is synthetic, fake, a fixture, or intentionally present to trigger secret scanning.
- The surrounding context clearly indicates this is test data and not an operational credential.

Classify as `potential real secret` when evidence suggests the finding is in operational code or configuration, such as:

- Production application code.
- CI/CD configuration.
- Deployment scripts.
- Environment variable examples that appear to contain real values rather than placeholders.
- Authentication, token, cloud provider, package registry, or webhook configuration.

## Secret handling rules

- Never quote, copy, summarize, or reproduce the secret value.
- Never include match strings from Gitleaks output.
- Never include raw surrounding lines if they contain the detected value.
- It is acceptable to mention the file path, line number, rule ID, and high-level context.
- If context is needed, describe it without exposing token-like strings.

## Comment format

Post exactly one comment per reviewed issue using this structure:

```md
## Gitleaks review

判定: <likely false positive | needs human review | potential real secret>

根拠:
- <short evidence, including file path and line number>
- <short evidence from repository context>

推奨対応:
- <specific next action>

注意:
- Secret values, match strings, and raw surrounding lines were not reproduced.
```

If an issue has multiple findings, include a compact bullet list under `根拠` and choose the highest-risk classification across all findings. Risk order is:

1. `potential real secret`
2. `needs human review`
3. `likely false positive`

## Acceptance example

Issue #4 is an example Gitleaks issue. It references:

- `RuleID`: `github-pat`
- `File`: `tests/fixtures/gitleaks/intentional-finding.env`
- `Line`: `3`

If this issue has the `security` label when the workflow runs, inspect the referenced file. The nearby context says it is a synthetic fixture and intentionally fake, so the expected assessment is `likely false positive`. Do not reproduce the token-like value in the comment.
