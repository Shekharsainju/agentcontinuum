# Release 1 acceptance

## What the automated suite proves, and what it does not

The suite in `tests/AgentContinuum.Acceptance.Tests` runs `ac-test.exe`, not the shipped `ac.exe`. `ac-test.exe` runs the same entry code as `ac.exe` with these test-only replacements:

- State, profile and managed settings paths in a temp folder per test.
- A rules file whose 2.1.304 entry gets the stream limit signal Claude Code 2.1.309 showed at a real limit: a `result` with `api_error: "usage_limit_reached"` and `api_error_status: 429`, and a `rate_limit_event` with `status: "rejected"`, the reset taken from its `resetsAt` in Unix seconds. FakeClaude reports 2.1.304, whose shipped entry has `"limit": null`.
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
| AC5 | `AC5_ThousandCrashCycles_NeverExceedMaxAttempts`: 1,000 cycles in 3 streams, seeds 5000 to 5002; each crash point ended a daemon at least once | Pass, 9 min 40 s. Rerun alone on 2026-10-23 from the Release build: pass, 13 min 30 s |
| AC6 | `AC6_LimitEndsJobWithin10s`: main process and 2 children gone within 10 s of the limit row (the StopFailure hook row on 2026-10-22, the stream limit result row since) | Pass. Rerun on 2026-10-28 with the stream signal, Debug build: pass |
| AC9 | `AC9_EachProofAloneBlocks`: P2 to P6 each alone, plus a passing control | Pass |
| AC10 | `AC10_P5MismatchTerminates` | Pass |
| AC13 | `AC13_NoBaselineWithActiveProcesses`: children outlive the run by 3 s and write a file | Pass |
| AC15 | `AC15_ClockJumpOneOffer`: 2 h forward and backward | Pass |
| I6 | `I6_SentinelsNowhere`: database, WAL, logs, launch settings and captured notifications | Pass. Rerun on 2026-10-28 with no hook or launch settings and a sentinel inside `rate_limit_info`, Debug build: pass |

## Fixes the suite led to

Each fix went in with a failing test first, in the owning task's code.

- Your own elevated or protected processes, such as ctfmon and taskhostw, show WMI no path or command line. The process check read each one as a possible live session, so every launch was refused with PROCESS_UNKNOWN. Such a process now counts only when it has a pinned CLI's name.
- Pre-flight judged billing by the embedded rules while the launch used the rules it was given. Both now use the same rules.
- A crash after the process started but before SPAWNED left the same row as a crash before the start, so recovery offered the task again although work may have run. The host now reports the process once it is in its job and before it runs, and its PID and creation time go on the INTENT row. Recovery treats an INTENT row with a PID as an interrupted launch.

## Hand check

Run on 2026-10-23 by hand, with the Release build of `ac-test.exe` in a folder of its own and `ac-test.json` beside it. State and profile were in temp folders and real toasts were on. The task ran FakeClaude, renamed `claude-handcheck.exe`, which reports 2.1.304. The rules were the shipped 2.1.304 rules with the test limit signal added, as the suite then built them, in the StopFailure hook form used before the real limit capture.

| Step | Result |
| --- | --- |
| Pre-flight passes and the task launches | Pass |
| The StopFailure hook with a reset one minute ahead is read as a verified limit; the task waits for the reset with 1 of 5 attempts used | Pass |
| One resume offer about 3 minutes after the limit, as reset plus the 2 minute margin | Pass. One notification appeared, and one offer line was captured |
| A Resume request launches the task again, on the same session, and it completes | Pass. Sent with `ac-test resume`, which submits the same request as the button. FakeClaude logged two starts with the same session ID, and the task ended COMPLETED with 2 of 5 attempts |
| Clicking the notification's Resume button sends that request | Not confirmed |

The button click is the one part not confirmed. The toast slid away before it could be clicked reliably. One click reached `ac-test.exe`, which logged `ignored a notification click`, so Windows did start the process for it. The handler ignores anything other than `action=resume&task=<id>`, and the log does not record what a click carried, so a click on the toast body and a button whose arguments arrive changed cannot be told apart. The open check is in `docs/known-issues.md`.
