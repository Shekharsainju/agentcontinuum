# Release 1 acceptance

## What the automated suite proves, and what it does not

The suite in `tests/AgentContinuum.Acceptance.Tests` runs `ac-test.exe`, not the shipped `ac.exe`. `ac-test.exe` runs the same entry code as `ac.exe` with these test-only replacements:

- State, profile and managed settings paths in a temp folder per test.
- A rules file with a test-only limit signal (StopFailure, `error: "rate_limit"`, `resets_at` in Unix seconds). The shipped rules for 2.1.304 have `"limit": null` until E1's limit capture runs.
- A clock moved by an offset file, with the real monotonic clock.
- A notifier that records the text each toast would show.
- Crash points that end the daemon with `Process.Kill()` at each R13 point.
- FakeClaude, under a name unique to each test, in place of Claude Code.

The suite therefore does not prove limit detection on the shipped `ac.exe`, which has no limit rule yet. It proves the launch, recovery, billing, workspace and scheduling logic around a limit signal, given one.

`ac-test.exe` refuses to run without its `ac-test.json`, found under `%LOCALAPPDATA%\AgentContinuum` and then beside the exe. It also refuses a config that names your own LOCALAPPDATA or profile folder.

## Running the suite

`dotnet test -c Release` runs it with the unit tests and adds about 10 minutes, most of it AC5. To leave it out, add `--filter "FullyQualifiedName!~Acceptance"`. AC5 runs 3 streams and AC4 runs 3 crash points at a time, since more exhausted an 8 GB machine.

## Results

Run on 2026-10-22, Windows 11 Home build 26300, Release build.

| Check | Test | Result |
| --- | --- | --- |
| AC4 | `AC4_KillDaemonAtEachCrashPoint`: 5 crash points, 20 runs each, 3 points at a time | Pass, 1 min 43 s |
| AC5 | `AC5_ThousandCrashCycles_NeverExceedMaxAttempts`: 1,000 cycles in 3 streams, seeds 5000 to 5002; each crash point ended a daemon at least once | Pass, 9 min 40 s |
| AC6 | `AC6_LimitEndsJobWithin10s`: main process and 2 children gone within 10 s of the hook row | Pass |
| AC9 | `AC9_EachProofAloneBlocks`: P2 to P6 each alone, plus a passing control | Pass |
| AC10 | `AC10_P5MismatchTerminates` | Pass |
| AC13 | `AC13_NoBaselineWithActiveProcesses`: children outlive the run by 3 s and write a file | Pass |
| AC15 | `AC15_ClockJumpOneOffer`: 2 h forward and backward | Pass |
| I6 | `I6_SentinelsNowhere`: database, WAL, logs, launch settings and captured notifications | Pass |

## Fixes the suite led to

Each fix went in with a failing test first, in the owning task's code.

- Your own elevated or protected processes, such as ctfmon and taskhostw, show WMI no path or command line. The process check read each one as a possible live session, so every launch was refused with PROCESS_UNKNOWN. Such a process now counts only when it has a pinned CLI's name.
- Pre-flight judged billing by the embedded rules while the launch used the rules it was given. Both now use the same rules.
- A crash after the process started but before SPAWNED left the same row as a crash before the start, so recovery offered the task again although work may have run. The host now reports the process once it is in its job and before it runs, and its PID and creation time go on the INTENT row. Recovery treats an INTENT row with a PID as an interrupted launch.

## Hand check

Not yet run. To be done with `ac-test.exe` from a folder of its own, with `ac-test.json` beside it and real toasts on: start the daemon, add a FakeClaude task whose scenario ends in a verified limit with a reset one minute ahead, and confirm that one notification appears about 3 minutes later and that Resume launches it.
