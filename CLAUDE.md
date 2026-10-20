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
## Code style
- Comment only to explain WHY something non-obvious is done. Never WHAT the code does.
- No comments that restate code, narrate steps, or start with "This function/script...".
- No banner comments, emojis, "Note:", "Important:".
- No references to the conversation, tasks or reviews in code.
- Spec IDs only where they explain a constraint.
- Comment-based help only on public functions, kept short.
- Console output plain and terse, no emojis or decorative symbols.
- Prefer clear names over comments.
- No em dashes or double hyphens as punctuation in prose, comments, output or commit messages. Command-line flags are fine.
