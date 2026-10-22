# Pause Note

Paused 22 October 2026 on branch `release1`. The working tree is clean. The last full Release run passed at `55cf862` (396 Core, 13 Windows, 44 CLI tests). The baseline was not re-run in the second session.

## Done

- Tasks 1 to 14 of `docs/superpowers/plans/2026-10-20-release1-assisted.md`, including the review fixes for Tasks 13 and 14.
- Open Low items are in `docs/known-issues.md`.

## Task 15: designed, no code yet

No `tests/AgentContinuum.Acceptance.Tests`, no test host and no `docs/release1-acceptance.md` exist yet.

### Why the plan's "real ac.exe" approach cannot work as written

- The embedded 2.1.304 rules have `"limit": null` until the E1 limit capture runs, so the real `ac.exe` can never see a verified limit. AC6, AC15, most of AC4 and AC5, and the hand check all need one.
- `ac.exe` takes the user profile from the known-folder API and uses the system clock, so P2 and P3 would read your real `~/.claude`, and AC15 would need the machine clock moved.
- `ac.exe` has no way to stop at an R13 crash point.
- FakeClaude does not answer `auth status`, so P4 always fails.

### Decision (yours): a test host, `ac-test.exe`

- New project `tests/AcTestHost`, assembly `ac-test`, referencing `AgentContinuum.Cli`. The shipped `ac.exe` gets no test seams and no bypasses.
- First move the body of `src/AgentContinuum.Cli/Program.cs` into a public entry method, so both executables run the same code, including the notification click wiring.
- `ac-test.exe` reads its config from `%LOCALAPPDATA%\AgentContinuum\ac-test.json`, then from `ac-test.json` beside the exe. With neither, it exits without touching any state. The hook and the click start it without the test's environment, which is why the config is found this way. Config: state root, profile folder, managed settings paths, rules file, clock offset file, crash point, notification capture file, real toasts on or off, lock name, tick interval.
- It sets `LOCALAPPDATA` and `USERPROFILE` in its own environment before calling `AppServices.ForCurrentUser()`, then overrides with `with`: Clock (system plus an offset read from a file on each call), Notifier (captures the same text the toast shows), Host and Runner (crash wrappers), Preflight (profile, managed paths, git), RulesFor, DaemonLockName and TickInterval.
- Crash points, each ending the daemon with `Process.GetCurrentProcess().Kill()`:
  1. Before INTENT: on the first `ICommandRunner` call, which comes during pre-flight.
  2. After INTENT, before spawn: in the host wrapper, before the real `Start`.
  3. After spawn, before SPAWNED: in the host wrapper, after the real `Start` returns.
  4. While running: in the run wrapper, on the first stdout line.
  5. After exit, before classification: the daemon's git is `ac-test.exe` in a git shim mode that forwards to the real git. The run wrapper sets an environment variable when the run exits, and the next git call, which is the baseline after `MarkExited`, kills the daemon by its PID.
- To show text to the capture notifier, `ToastNotifier` needs its title text factored into a static method. That is a small production refactor in its own commit.

### FakeClaude additions (Task 11 code, failing test first for each)

1. `auth status` prints a subscription JSON by default, or `%USERPROFILE%\.fake-claude-auth.json` if that file exists (the P4 failure case).
2. Each headless run appends a line to `fake-claude-starts.log` in the working folder (AC5 counts real starts).
3. `childLingerSeconds` lets children outlive the main process, and `childTouchFile` makes each child write a file into the worktree on exit (AC13).

The test repos must `.gitignore` `fake-claude-*`, or FakeClaude's own files change the fingerprint.

### Test plan per check

- AC4: one root per crash point, the five points in parallel, 20 runs each. Each daemon start recovers the previous run. Expected: (1) no op, no attempt, task unchanged; (2) ABANDONED, attempt counted, WAITING_FOR_RESET, which the first tick offers as "launch did not start"; (3) NEEDS_ATTENTION "Interrupted launch"; (4) INTERRUPTED, NEEDS_ATTENTION "Interrupted run"; (5) classified from the record, so a success scenario gives COMPLETED.
- Expected AC4 failure at point 3: kill-on-close ends the child before recovery runs. Recovery then sees INTENT with no process and gives WAITING_FOR_RESET, not the table's NEEDS_ATTENTION. Telling points 2 and 3 apart needs a ledger marker, written before `Start`, that a spawn was attempted. That is an R13 schema change in Task 4 and 5 code. Ask before making it.
- AC5: seeded random crash point (or none), scenario and request kind, with requests written straight to the `requests` table. Run in parallel streams. After each recovery, for every task: attempts used, op rows and logged starts are all within `max_attempts`.
- AC6: StopFailure hook with the test-only limit rule, `lingerSeconds` 60, `spawnChildren` 2. Every PID is gone within 10 s of the hook row appearing.
- AC9: a theory with one case each for P2 (env key in profile settings), P3 (no credentials), P4 (auth override), P5 (rules without `p5`) and P6 (no attestation), plus a passing control case. Each case is refused with BILLING_UNVERIFIED, with no start and no attempt counted.
- AC10: `authSource` `ANTHROPIC_API_KEY` with a long linger. The run is killed, and the task goes to NEEDS_ATTENTION "Billing unverified".
- AC13: children outlive main by 3 s and touch a file. The stored baseline equals a fingerprint computed afterwards, and preview passes the workspace check.
- AC15: forward case with reset at now plus 1 h, the clock jumped +2 h, back to 0, then +2 h again. Backward case with the clock at -2 h before attesting and launching, then 0, then -2 h, then 0. Each case gives exactly one offer and logs "clock jump".
- I6: sentinels in the prompt, an extra event field and an extra hook payload field. None appears in the database (including the WAL), the logs or the captured notification text.

### Next steps

1. Run the Release baseline: `dotnet build -c Release` and `dotnet test -c Release --no-build`.
2. The FakeClaude additions, then the `Program.cs` entry refactor, the `ToastNotifier` text refactor, the test host and the acceptance harness, each in its own commit.
3. One commit per acceptance check. A test that fails gets fixed only in the owning task's code, with a failing test committed first.
4. Stop before the hand check. You want to do the first real run with Claude walking you through it, on a copy of `ac-test.exe` with `ac-test.json` beside it and real toasts on.
5. `docs/release1-acceptance.md` must say clearly that the automated suite runs on `ac-test.exe` with a test-only limit rule, profile and clock. It does not prove limit detection on the shipped `ac.exe`, which has no limit rule until E1's limit capture.

## Watch for

- Never run `ac.exe` without `LOCALAPPDATA` pointing at a temp folder. An earlier smoke test wrote into the real `%LOCALAPPDATA%\AgentContinuum`, and the folder was deleted afterwards. `ac-test.exe` must refuse to run without its config for the same reason.
- The `--allowedTools` command in `docs/known-issues.md` is yours to run by hand, against the real CLI.
