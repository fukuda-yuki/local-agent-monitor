# Claude Skill Mirror

The shared Claude Code skills in this directory are generated from the canonical copies under `.agents/skills/`.

Do not edit these shared directories directly:

- `seed-demo`

After changing a canonical skill, run:

```powershell
pwsh scripts\agent\sync-claude-skills.ps1
```

When `.agents/skills/seed-demo/`, `.claude/skills/seed-demo/`, or the synchronization script changes, check the mirror with:

```powershell
pwsh scripts\agent\sync-claude-skills.ps1 -Check
```

Claude-specific skills may coexist in `.claude/skills/`; the synchronization script only owns `seed-demo`.
