# Pause Note

Paused 22 October 2026 on branch `release1`, at a usage limit. The working tree is clean, and the full suite passes in Release at `55cf862` (396 Core, 13 Windows, 44 CLI tests).

## Done

- Tasks 1 to 14 of `docs/superpowers/plans/2026-10-20-release1-assisted.md`, including the review fixes for Tasks 13 and 14.
- Open Low items are in `docs/known-issues.md`.

## Task 15: not started

Nothing for Task 15 exists yet: no `tests/AgentContinuum.Acceptance.Tests` project and no `docs/release1-acceptance.md`.

Next steps, from the plan:

1. Create the acceptance test project. It runs the built `ac.exe` and FakeClaude as real processes, with `LOCALAPPDATA` set to a temp folder. `AcProcessTests` in `tests/AgentContinuum.Cli.Tests` shows the pattern; `ac` reads state from the `LOCALAPPDATA` variable.
2. Write the tests `AC4_KillDaemonAtEachCrashPoint`, `AC5_ThousandCrashCycles_NeverExceedMaxAttempts`, `AC6_LimitEndsJobWithin10s`, `AC9_EachProofAloneBlocks`, `AC10_P5MismatchTerminates`, `AC13_NoBaselineWithActiveProcesses`, `AC15_ClockJumpOneOffer` and `I6_SentinelsNowhere`.
3. Fix any failure only in the owning task's code, with a failing test committed first.
4. Hand check by you: the notification and Resume click with a FakeClaude task. It also covers the click wiring in `Program.cs`, which has no automated test.
5. Stop for review before any run against the real Claude Code.

## Watch for

- Never run `ac.exe` without `LOCALAPPDATA` pointing at a temp folder. An earlier smoke test wrote into the real `%LOCALAPPDATA%\AgentContinuum`, and the folder was deleted afterwards.
- The `--allowedTools` command in `docs/known-issues.md` is yours to run by hand, against the real CLI.
