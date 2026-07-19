# Claude Skill Mirror

The shared Claude Code skills in this directory are generated from the canonical copies under `.agents/skills/`.

Do not edit these shared directories directly:

- `commit`
- `seed-demo`
- `spec-update`
- `sprint-evidence`
- `validate`

After changing a canonical skill, run:

```powershell
pwsh scripts\agent\sync-claude-skills.ps1
```

Repository validation checks the mirror with:

```powershell
pwsh scripts\agent\sync-claude-skills.ps1 -Check
```

Claude-specific skills may coexist in `.claude/skills/`; the synchronization script only owns the five shared directories listed above.
