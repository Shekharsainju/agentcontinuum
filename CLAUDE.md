## Commits
- One commit per check, never per file or per task.
- Each commit holds only that check's Pester test plus the code that makes it pass.
- Red/green TDD: failing test commit first, then implementation.
- Shared helpers get their own commits before the checks that use them.
- Never mix two checks in one commit.
- No AI attribution trailers (no Co-Authored-By, Claude-Session or "Generated with" lines).
- Show `git log --oneline` at each stop point.
## Efficiency
- Subagent review for tasks touching accounts, rights, pipes or admin scripts; low-risk tasks built directly with one review at the end.
