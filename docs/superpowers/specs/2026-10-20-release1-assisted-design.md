# Release 1: assisted mode design

Oct 20, 2026

## Intent

Release 1 is the first shippable AgentContinuum. You define a task, AgentContinuum runs Claude Code on it headless, notices when the run stops at a usage limit, waits for the reset, and shows a Windows notification with a Resume button. Clicking it runs the full pre-flight and, only if every check passes, resumes the same Claude session. Nothing launches without your click.

The value is the safety layer from `docs/design-review.md`: a launch is refused unless the billing route is verified as subscription (R15), the workspace is unchanged since the run stopped, and the attempt fits the task's policy. Launch records survive crashes without double launches (R13).

## Decisions

Made by you on Oct 20, 2026:

- Tasks are managed headless tasks only. AgentContinuum never touches sessions you start yourself.
- Resume uses Claude Code's own `--resume <session-id>`. No handoff file (R14 is Release 2).
- C# on .NET 8.
- Claude Code only. No Codex.
- The agent runs under your own account. No `ac-agent`, no I4 claim (`docs/security-recovery-architecture.md`, E0b gates).
- The controller runs interactively in your session, never in Session 0.
- The first task is a detection probe, E1. Release 1 ships with whichever proofs E1 confirms. A proof that cannot be evaluated makes the route UNVERIFIED and the launch is refused.

## Goals

- Detect a usage-limit stop from a verified signal with a session ID match (R2).
- Schedule the offer from the reported reset time plus 2 minutes, from a UTC epoch, polled every 30 seconds (R4, R7).
- Run the same pre-flight on every launch, first or resumed (I1).
- Launch only on a VERIFIED-SUBSCRIPTION route (I2, R15).
- Count every process start as an attempt and never exceed the policy, across crashes (I3, R13).
- At most one live process per task (I5).
- No task content in the state database, logs or notifications (I6).
- Every failed or ambiguous check ends in NEEDS_ATTENTION (I7).

## Non-goals

- Automatic resume of any kind. Releases 2 and 3.
- Handoff restart (R14), pre-limit checkpoint (R5 optional part), snapshot restore points.
- Codex, macOS, Linux. Platform code stays behind interfaces so they can follow.
- Running the controller as a service, a scheduled task or at logon. You start `ac daemon` yourself.
- `ac doctor`, `ac uninstall`, an installer.

## Anti-goals (a failure even if it runs)

- A launch you did not ask for, including a retry after a crash.
- Any keystroke or menu answer sent to a session (R3).
- `--dangerously-skip-permissions` or `bypassPermissions` on any launch.
- Prompt text, model output, tool input or tool output written to the database, a log or a notification.
- A launch that went ahead because a check could not run.
- Claiming I4. The agent shares your account and could edit the state database; your click is the control.

## How it works

### Components

| Component | Runs as | Does |
| --- | --- | --- |
| `ac daemon` | You, interactive session | Owns the job objects, launches Claude, reads its event stream, polls the clock, shows notifications, processes requests |
| `ac <command>` | You | Writes a request row to the database and prints the result the daemon records |
| `ac hook stop-failure` | Claude Code, as a hook | Reads the hook payload on stdin and writes only allowlisted fields to the database |

Commands talk to the daemon through a `requests` table that the daemon polls every second. There are no named pipes in Release 1. A notification button starts `ac.exe` with activation arguments, which writes a request row the same way.

State lives in `%LOCALAPPDATA%\AgentContinuum\`: `state\ac.db` (SQLite, WAL, `synchronous=FULL`) and `launch\<op_id>\settings.json`.

### Task and policy

`ac task add` records:

| Field | Meaning |
| --- | --- |
| `task_id` | Random, generated |
| `label` | Your short name, shown in notifications. The only free text stored |
| `worktree` | Absolute path to the repository the agent works in |
| `prompt_file` | Path inside the worktree. Its content is streamed to Claude's stdin at launch and never stored |
| `permission_mode` | `default`, `acceptEdits` or `plan` |
| `allowed_tools` | List passed as `--allowedTools` |
| `max_attempts` | Total process starts allowed |
| `deadline_utc` | No launch after this |
| `session_id` | UUID generated at add, passed as `--session-id` on the first launch |
| `cli_path`, `cli_sha256`, `cli_version` | Pinned Claude Code identity |

Task states: QUEUED, RUNNING, WAITING_FOR_RESET, RESUME_OFFERED, NEEDS_ATTENTION, COMPLETED, REVOKED.

### Launch

Every launch, from `ac task start`, a Resume click or `ac resume`, goes through the same path:

1. Pre-flight. Any failure ends here with reason codes, no attempt counted, task to NEEDS_ATTENTION.
2. Commit INTENT and increment the attempt counter in one transaction (R13 launch record, as in `docs/security-recovery-architecture.md`).
3. Write `launch\<op_id>\settings.json` with the StopFailure hook command, if E1 confirmed the hook.
4. Create a kill-on-close job with breakaway disallowed. Start Claude suspended with a constructed environment (P1), assign it to the job, then resume it.
5. Commit SPAWNED with PID, creation time and session ID.
6. Stream the prompt file to stdin, then close stdin. Read stdout as stream-json through the event filter. Discard stderr.
7. On exit, commit EXITED with the exit code, then classify.

First launch: `claude -p --session-id <id> --output-format stream-json --verbose --permission-mode <m> --allowedTools <t> [--settings <file>]`, prompt on stdin.

Resume: the same, with `--resume <id>` in place of `--session-id <id>` and the fixed continuation prompt on stdin:

> Continue the task from where you stopped. Your usage limit has reset. Check the repository state before you act.

The working directory is the worktree. Kills go through the job, and every kill result is recorded.

### Event filter

The filter reads stdout line by line and keeps only fields named in the version rules: event `type`, `subtype`, `session_id`, `is_error`, the auth-source field (P5) and the limit fields. Every other field and every unparseable line is dropped without being logged. Lines over 1 MB are dropped. Kept values are capped at 200 characters.

### Detection and classification

| Run ended with | Outcome | Task goes to |
| --- | --- | --- |
| Success result event | COMPLETED | COMPLETED |
| Verified limit signal (E1 rules) for this session ID | USAGE_LIMIT | WAITING_FOR_RESET, or NEEDS_ATTENTION "Attempts exhausted" |
| Error result, non-zero exit, no result event | FAILED | NEEDS_ATTENTION "Run failed" |
| Limit-like signal that is not verified | AMBIGUOUS | NEEDS_ATTENTION "Stopped, reason unverified" |
| P5 mismatch on the `system/init` event, or no such event before the first assistant event | BILLING_MISMATCH | Job terminated; NEEDS_ATTENTION "Billing unverified" |

If the process is still alive 10 seconds after a verified limit signal, the job is terminated, which also covers any in-process native resume. Stdin is closed after the prompt, so a session cannot wait on a menu (R3).

After every exit, once the job reports zero active processes, the controller takes the workspace fingerprint as the baseline.

### Waiting and the offer

- RESET_EXPECTED_AT is the reset time from the verified signal, stored as a UTC epoch. ATTEMPT_PERMITTED is that time plus 2 minutes. With no reset time, the offer is made at once, marked "reset time unknown".
- The daemon checks the clock every 30 seconds. At ATTEMPT_PERMITTED the task moves to RESUME_OFFERED and one notification appears: "<label>: usage limit has reset", with buttons Resume and Dismiss.
- A wall-clock jump of more than 120 seconds against the monotonic clock is logged and schedules are recomputed. At most one offer per task per wait (AC15).
- A NEEDS_ATTENTION notification has no Resume button. `ac resume <task>` resumes from NEEDS_ATTENTION after a full pre-flight.

### Pre-flight

All checks run every time and every failure is reported, not just the first.

| Check | Passes when | Reason code |
| --- | --- | --- |
| Admission | State allows launch; attempts used < `max_attempts`; now < `deadline_utc`; prompt file exists, is not empty and resolves inside the worktree | ADMISSION_STATE, ATTEMPTS_EXHAUSTED, DEADLINE_PASSED, PROMPT_INVALID |
| Eligibility | No ATTEMPT_PERMITTED is set, or now >= ATTEMPT_PERMITTED, or the launch was requested with `ac resume --now` (still counts) | NOT_YET_ELIGIBLE |
| Process | Task's job, if any, has zero processes; no process you own has the session ID on its command line; enumeration succeeded | PROCESS_LIVE, PROCESS_UNKNOWN |
| Binary | `cli_path` resolves to the same file, SHA-256 matches, `--version` matches `cli_version` | CLI_CHANGED |
| Workspace | Fingerprint equals the baseline (none on first launch) | WORKSPACE_CHANGED, WORKSPACE_TOO_LARGE |
| Permission | Policy mode is one of the three allowed; no settings scope sets `bypassPermissions` | PERMISSION_UNSAFE |
| Billing | All six proofs pass (below) | BILLING_UNVERIFIED with the failed proof IDs |
| Controller | `PRAGMA integrity_check` is ok and the schema version matches | CONTROLLER_STATE |

`ac pin-cli <task>` re-pins after a Claude Code update. `ac accept-workspace <task>` takes a new baseline after you change the worktree yourself. `ac preview <task>` runs pre-flight and launches nothing (R9).

### Billing proofs (R15)

| Proof | Release 1 check |
| --- | --- |
| P1 | The child environment is built only from an allowlist: `SystemRoot`, `windir`, `ComSpec`, `PATHEXT`, `TEMP`, `TMP`, `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`, `HOMEDRIVE`, `HOMEPATH`, `USERNAME`, `NUMBER_OF_PROCESSORS`, `PROCESSOR_ARCHITECTURE`, `OS`, and a constructed `PATH` (System32, Windows, the pinned CLI's folder, Git's folder) |
| P2 | Managed, user and project settings files parse, and none sets `apiKeyHelper`, `env` entries for `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_BASE_URL`, `CLAUDE_CODE_USE_BEDROCK`, `CLAUDE_CODE_USE_VERTEX`, or any key listed in the version rules. A project-scope billing setting forces UNVERIFIED. An unreadable file is a failure |
| P3 | Credential type from the version rules, judged by key names only, never values |
| P4 | Auth status command from the version rules, run with the P1 environment, reports a subscription |
| P5 | The auth-source field of the event named in the version rules (`system/init` on 2.1.304, which is not the first line) has a subscription value; otherwise BILLING_MISMATCH |
| P6 | Your `ac attest-overage-off` is under 30 days old |

### Version rules

`rules/claude-code.json`, embedded in `ac.exe`, holds one entry per verified Claude Code version range: the P2 extra keys, the P3 file and key names, the P4 command and accepted output, the P5 event, field and accepted values, and the limit signal (hook name, reason field and value, reset field and format). E1 supplies the first entry, for 2.1.304. A missing fact leaves its field empty, and an empty field makes that proof or signal unevaluable. A version outside every range makes P3, P4, P5 and limit detection unevaluable.

### Workspace fingerprint

`git -c core.fsmonitor=false -c core.hooksPath=NUL ls-files -z --cached --others --exclude-standard`, run in the worktree, gives the file list. The fingerprint is SHA-256 over the sorted entries `path NUL sha256(bytes) NUL` plus `HEAD`. Symlinks hash their target path and are not followed. More than 50,000 files or 2 GB total marks it INCOMPLETE, which fails pre-flight. Only the fingerprint is stored, never file content.

## E1 detection probe

A throwaway PowerShell probe in `probes/e1/`, in the style of E0a, run against your installed Claude Code (2.1.304, `C:\Users\ultra\.local\bin\claude.exe`). It answers:

1. Does a StopFailure hook fire when a headless run stops at a usage limit, and with which fields: event name, reason, session ID, reset time?
2. Does the headless process exit on its own at the limit, and within how long? (Needed for I5. Added to your four.)
3. P3: which credential store and key names distinguish a subscription login from an API key?
4. P4: which auth status command exists, and what does it report for a subscription and for an API key?
5. P5: which first-event field reports the auth source, and with which values?

Rules for the probe:

- It records key names, event types and enumerated values only. Anything that looks like an email address, a token or free text is replaced with its length. Message content is never written.
- Three scripts, by cost. `Get-E1StaticFacts.ps1` uses no quota and may be run by the builder. `Invoke-E1InitCapture.ps1` makes two one-turn runs, one normal and one with an invalid dummy `ANTHROPIC_API_KEY` as the negative control for P4 and P5. `Invoke-E1LimitCapture.ps1` repeats a short prompt until a run ends without success, and refuses to start until you confirm paid overage is off. You run the second and third.
- Results go to `probes/e1/results/`. Findings go to `docs/e1-findings.md` and the 2.1.304 entry in `rules/claude-code.json`.

## Constraints

- .NET 8 SDK, target `net8.0-windows10.0.19041.0`. Packages: `Microsoft.Data.Sqlite`, `Microsoft.Toolkit.Uwp.Notifications`, xUnit. Nothing else without a reason.
- Windows code (jobs, process start, notifications, process listing) lives behind interfaces in its own project.
- Tests never call the real Claude Code. A synthetic CLI, `FakeClaude`, stands in for it.
- Code style and commits follow `CLAUDE.md`.

## Acceptance

Run on this machine with `FakeClaude`:

- AC4 subset: killing the daemon at each R13 crash point gives the recovery in the architecture doc's table.
- AC5: 1,000 randomised crash and restart cycles never exceed `max_attempts`.
- AC6: a verified limit signal ends every process in the job within 10 seconds.
- AC9: each of P2 to P6 failing alone blocks the launch.
- AC10: a P5 mismatch terminates the run.
- AC13: the baseline is never taken while the job has processes.
- AC15: a 2-hour clock jump in either direction gives at most one offer.
- I6: sentinel strings in the prompt, the stream and the hook payload appear nowhere in the database, logs or notifications.

## Left to the builder

Database column types and indexes beyond the R13 partial unique index; command-line parsing; log format and location; exact notification layout; how `FakeClaude` reads its scenario.

## Risks

- If E1 confirms no limit signal, every stop is AMBIGUOUS and Release 1 can only tell you a run stopped.
- If E1 confirms none of P3 to P5, every launch is refused. That is the rule you set.
- Claude Code updates change the pinned hash, so each update needs `ac pin-cli`, and a new version needs a new rules entry.
