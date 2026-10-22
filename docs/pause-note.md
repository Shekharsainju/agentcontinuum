# Pause Note

Paused 22 October 2026 on branch `release1`. The working tree is clean. The last full Release run passed at `1fa4a4a`: 406 Core, 23 Windows, 44 CLI and 19 acceptance tests.

## Done

- Tasks 1 to 14 of `docs/superpowers/plans/2026-10-20-release1-assisted.md`.
- Task 15's automated suite, on the test host `ac-test.exe`. Results and what the suite does not prove are in `docs/release1-acceptance.md`.
- The fixes the suite led to, each with a failing test first:
  - Your own elevated processes no longer read as a possible live session.
  - Pre-flight judges billing by the same rules as the launch.
  - The process PID and creation time go on the INTENT row before the process runs, so recovery tells an interrupted launch from one that never started.
- Open Low items, including those from the Task 15 review, are in `docs/known-issues.md`.

## Next: the hand check

You run it, with Claude walking you through each step. It uses the test host, because the shipped rules have no limit signal yet.

1. Copy the built `ac-test.exe` and its files into a folder of their own.
2. Put an `ac-test.json` beside it. Point its state and profile folders at temp folders, never your own. Point its rules at a file with the test limit rule, and turn real toasts on.
3. Start `ac-test daemon`, then add a FakeClaude task whose scenario ends in a verified limit with a reset one minute ahead.
4. Confirm that one notification appears about 3 minutes later, and that its Resume button launches the task.
5. Record the result in `docs/release1-acceptance.md`.

Then Task 15 step 4: commit, show `git log --oneline` and stop for review before any run against the real Claude Code.

## Watch for

- Never run `ac.exe` without `LOCALAPPDATA` pointing at a temp folder. `ac-test.exe` refuses to run without its config for the same reason.
- AC5 runs 3 streams and AC4 runs 3 crash points at a time. More ran this 8 GB machine out of memory.
- The `--allowedTools` command in `docs/known-issues.md` is yours to run by hand, against the real CLI.
