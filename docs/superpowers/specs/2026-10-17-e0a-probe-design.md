# E0a probe — design

Oct 17, 2026 · for @Shekhar

## Intent

`docs/security-recovery-architecture.md` makes configuration C2 the primary design: a standard `ac-controller` account launches every CLI session as a second standard account, `ac-agent`, and controls it through a Job Object it owns. The first three "Blocked on" items decide whether C2 works at all. E0a is a small, throwaway Windows probe that answers them on this machine (Windows 11 Home) with evidence, before anything in R11, R12 or R5 is built.

The probe answers:

1. Can a standard account start a process as a second standard account with `CreateProcessWithLogonW`, and put it in a job it owns, given any job Secondary Logon or Task Scheduler already applies?
2. Can it freeze, thaw and terminate that job, and does kill-on-job-close and breakaway denial hold?
3. Can it see, and kill, an agent process it did not create (the R11 known gap)?
4. Does basic separation between the two accounts hold?

## Goals

- One pass/fail result per check, in plain JSON, with Windows edition and build in every file.
- Two start modes for the controller side, scored separately (see Runs).
- Leave the machine as it was: no accounts, profiles, tasks, rights or passwords left behind, even after a crash.

## Non-goals

- No real Claude or Codex CLI. The agent side is a synthetic PowerShell child. The probe never reads, writes or sets `~\.claude`, `~\.codex`, `CLAUDE_CONFIG_DIR` or `CODEX_HOME`, in any account.
- Not the controller. No SQLite, pipe protocol, snapshots or UI. Probe code is not reused in product code.
- No hardening for production; no installer.

## Anti-goals (a failure even if it runs)

- Anything elevated run by Claude. Every elevated command is in a script you run yourself in an elevated PowerShell.
- A password on disk, in a log, on a command line, in an environment variable, on the clipboard or in Credential Manager. The one exception, which you chose, is the run-A scheduled task: Task Scheduler stores `ac-probe-ctl`'s password until that task is deleted. "Passwords gone" is true only after cleanup.
- Working around Defender, AMSI or execution policy at machine level. A block is a result, not an obstacle.
- Changing machine execution policy, starting stopped services, or touching your real accounts' settings.
- A result the agent could forge: `ac-probe-agent` has no access to the results folder.

## Accounts and secrets

| Account | Type | Created by | Group |
| --- | --- | --- | --- |
| `ac-probe-ctl` | Standard local | Elevated run script | Users only |
| `ac-probe-agent` | Standard local | Elevated run script | Users only |

- Names are fixed and never resemble `ac-controller` / `ac-agent`. Description: "AgentContinuum E0a probe - safe to delete". Password never expires; user cannot change it.
- Passwords: 32 random characters from a cryptographic RNG, generated at creation, held in memory only.
- `ac-probe-ctl` gets `ac-probe-agent`'s password through a named pipe hosted by the elevated script, with a random name, whose DACL admits only `ac-probe-ctl`. The pipe is created with `FILE_FLAG_FIRST_PIPE_INSTANCE` (creation fails if the name already exists, defeating squatting), rejects remote clients, verifies the connected client's SID is `ac-probe-ctl` before writing anything, and closes after one read. The pipe name, not the secret, goes on the command line. The same mechanism serves both runs.
- The accounts' SIDs (not secret) go into a manifest in the results folder, so cleanup can revoke rights and find profiles even if an account is already gone.

## Layout and ACLs

Inheritance is disabled on every probe folder so the ProgramData defaults do not leak in.

| Path | Holds | Admins | `ac-probe-ctl` | `ac-probe-agent` | You |
| --- | --- | --- | --- | --- | --- |
| `C:\ProgramData\ac-probe\bin` | Probe scripts and C# source | Full | Read, execute | Read, execute | — |
| `C:\ProgramData\ac-probe\ctl` | Harness private scratch; isolation target | Full | Full | None | — |
| `C:\ProgramData\ac-probe\exchange` | Child heartbeat and child reports | Full | Read | Modify | — |
| `C:\ProgramData\ac-probe-results` | `setup.json`, `results-A.json`, `results-B.json`, `manifest.json` | Full | Modify | None | Read |

"You" is the account that ran the elevated script, overridable with `-ReaderAccount`. Cleanup removes `C:\ProgramData\ac-probe` and keeps `C:\ProgramData\ac-probe-results`; `-IncludeResults` removes it too.

## Scripts

All under `probes/e0a/` in the repo, copied to `bin` at setup.

| Script | Runs as | Does |
| --- | --- | --- |
| `Invoke-E0aProbe.ps1` | You, elevated | Precheck, pre-clean, create accounts and folders, run A, run B, cleanup in `finally` |
| `Remove-E0aProbe.ps1` | You, elevated | Idempotent teardown by name and manifest; no passwords needed |
| `Harness.ps1` | `ac-probe-ctl` | Runs checks 2–8, writes one results file |
| `SubHarness.ps1` | `ac-probe-ctl` | Owns the job for check 5 only |
| `Child.ps1` | `ac-probe-agent` | Heartbeat, grandchildren, breakaway attempt, escape task, isolation attempts |
| `Beacon.ps1` | `ac-probe-agent` via its own task | Writes PID and heartbeat for up to 60 s, then exits |
| `Native.cs` | loaded by `Add-Type` | P/Invoke: `CreateProcessWithLogonW`, job APIs, `NtSetInformationJobObject`, `NtSuspendProcess`, `OpenProcess`, LSA rights |

Every process the probe starts runs `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <script>`. Machine policy is never changed.

If `Add-Type` of `Native.cs` fails (Defender, AMSI or anything else), the process writes a `native_load_blocked` result with the error text and exits. No retry, no obfuscation.

## Runs

Both runs execute the same harness and checks against the same accounts, one after the other, each with a fresh job.

- **Run A — scheduled task.** The elevated script registers a one-off task `ac-probe-ctl-runA` as `ac-probe-ctl`, "run whether logged on or not", with the in-memory password, starts it, waits, and deletes it. Before registering it records whether `ac-probe-ctl` holds `SeBatchLogonRight`, and again after registering. If the task fails to start with a logon-type error and the right is missing, the script grants it with `LsaAddAccountRights`, retries once, and records `batch_right_needed_grant: true`. Matches how the real controller would start.
- **Run B — Secondary Logon.** The elevated script starts `Harness.ps1` as `ac-probe-ctl` with `Process.Start` and credentials (which uses `CreateProcessWithLogonW`). This is the nested-job data point: the harness may already sit in a Secondary Logon job before it makes its own.

Each run has a 5-minute timeout, after which the elevated script kills it and records `timeout`. After each run, before killing anything, the script records any `ac-probe-agent` processes still alive as `stray_after_run`.

## Checks

Every check records `pass`, `fail`, `error` (could not be evaluated, with the error) or, for check 7, `finding`. Details carry Win32 or NTSTATUS codes.

**C1 Precheck** (in `setup.json`). Elevated; Secondary Logon service (`seclogon`) is Running; edition and build recorded. If `seclogon` is not running the probe stops with that result: both runs need it, and the script does not start it.

**C2 Launch and assign.** The harness records whether it is itself in a job (`IsProcessInJob(self, NULL)`). It creates a job with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, no breakaway flags. It calls `CreateProcessWithLogonW` for `Child.ps1` as `ac-probe-agent`, `LOGON_WITH_PROFILE`, `CREATE_SUSPENDED`. It records whether the child is already in a job before assignment, then `AssignProcessToJobObject`, then `ResumeThread`. Pass: assignment succeeds and `IsProcessInJob(child, ourJob)` is true. Also records whether `QueryInformationJobObject(JobObjectBasicProcessIdList)` lists the child's PID.

**C3 Freeze and thaw.** The child writes a timestamp to its heartbeat file every 250 ms. The harness freezes the job via `NtSetInformationJobObject(JobObjectFreezeInformation)`, waits 6 s, thaws, waits 3 s. Pass: no heartbeat between freeze + 0.5 s and thaw, a gap of at least 5 s, and a heartbeat within 2 s of thaw. As a separate, secondary result it repeats the same test with `NtSuspendProcess` / `NtResumeProcess` on the held child handle (covers only the child, not grandchildren).

**C4 Terminate.** The child starts two looping grandchildren. The harness calls `TerminateJobObject`. Pass: `JobObjectBasicAccountingInformation.ActiveProcesses` is 0 and the heartbeat stops within 5 s.

**C5 Kill-on-job-close.** The harness starts `SubHarness.ps1` (same account, plain `CreateProcess`), which creates its own kill-on-close job, launches a child as in C2, and reports the child's PID and creation time on stdout. The harness kills the sub-harness with `TerminateProcess` (abrupt). Pass: the child's heartbeat stops and the PID with that creation time is gone within 5 s.

**C6 Breakaway.** The child calls `CreateProcess` with `CREATE_BREAKAWAY_FROM_JOB` for a grandchild and reports the outcome and PID. Pass: creation fails, or it succeeds and the harness's `JobObjectBasicProcessIdList` contains the grandchild.

**C7 R11 gap (finding).** The child, as `ac-probe-agent`, tries to register its own task `ac-probe-agent-escape` with `-LogonType S4U` (no stored password) running `Beacon.ps1`, then starts it. The admin script does not help. Recorded: registration succeeded (or error); task started; the beacon PID; harness can see that PID in a process list; harness can read its creation time; the beacon is outside the harness job; harness `OpenProcess(PROCESS_TERMINATE)` + `TerminateProcess` succeeds. `setup.json` also records whether `ac-probe-agent` holds `SeBatchLogonRight`; the probe never grants it.

**C8 Isolation.** The child tries, each recorded separately, all must fail with access denied: read and write `ctl`; read and write `ac-probe-results`; `OpenProcess` on the harness PID with `PROCESS_TERMINATE`; `OpenProcess` with `PROCESS_VM_WRITE | PROCESS_VM_OPERATION`. Pass only if all six fail.

## Results format

UTF-8 JSON in a per-invocation folder `C:\ProgramData\ac-probe-results\<yyyyMMdd-HHmmss>\`, one file per run plus `setup.json` and `manifest.json`, schema `ac-probe-e0a/1`. `setup.json` also carries `S0` (ctl batch right before, after registration, grant needed), `S1` (neither password appears in any probe or results file, UTF-8 or UTF-16) and `T0` (teardown verify, written last).

```json
{
  "schema": "ac-probe-e0a/1",
  "edition": "Windows 11 Home",
  "os_caption": "Microsoft Windows 11 Home",
  "build": "26300.xxxx",
  "display_version": "...",
  "run": "A",
  "started_at": "2026-10-17T12:00:00Z",
  "harness_in_job_before": true,
  "checks": [
    { "id": "C2", "name": "launch_and_assign", "result": "pass", "details": { } }
  ]
}
```

`edition` is the fixed string "Windows 11 Home"; `os_caption` is read from `Win32_OperatingSystem`, and a mismatch is recorded as a warning. Build is `CurrentBuild.UBR` from the registry.

## Cleanup

`Remove-E0aProbe.ps1` runs before setup, in the run script's `finally`, and standalone. Each step tolerates "already gone" and the script continues past failures, reporting them at the end:

1. Stop every process owned by either account (by SID or name).
2. Unregister scheduled tasks `ac-probe-ctl-runA`, `ac-probe-agent-escape` and anything named `ac-probe-*`.
3. Revoke `SeBatchLogonRight` from both SIDs if the probe granted it (from manifest).
4. Delete profiles with `Win32_UserProfile` `.Delete()` via CIM, by SID from the manifest or `LocalPath` under `C:\Users\ac-probe-*`, retrying while a profile is still loaded. Never delete the folder directly, so ProfileList entries go too.
5. Remove both local accounts.
6. Remove `C:\ProgramData\ac-probe`.
7. Verify: no account, task, profile or `ProfileList` entry matching `ac-probe-*` remains; print the result.

## Builder notes (my calls, change freely)

- **Self mode.** `Harness.ps1 -SelfMode` runs C3–C6 with the child started by plain `CreateProcess` as the current user, no accounts, no elevation, so the job logic can be developed and tested without the elevated run. Self-mode output is marked `"run": "self"` and is not evidence for C2.
- **Pure logic is unit-tested** with the Pester that ships with Windows PowerShell 5.1: password generation, heartbeat-gap evaluation, results schema, ACL spec, cleanup step ordering (mocked).
- `JOBOBJECT_FREEZE_INFORMATION` layout is semi-documented; the builder confirms it against the SDK headers. A failed call records the NTSTATUS as `error`, not `fail`.
- The accounts appear on the sign-in screen while they exist; the probe does not hide them (that would be a registry change).
- Order is A then B. If A crashes the script, `finally` still cleans up, and B is not attempted.

## Mapping to "Blocked on"

| Blocked-on item | Answered by |
| --- | --- |
| Handle from `CreateProcessWithLogonW` assignable to own job, nested with Secondary Logon | C2 in runs A and B |
| Standard account can freeze and terminate its job of agent processes | C3, C4, C5, C6 |
| Enumerate agent processes and creation times; kill ones not created | C7 |

## Open items

- The real controller's named pipes (the R12 events pipe and any secret handoff) must verify the server's SID before trusting what they receive, not connect by name alone. A local attacker who wins the create race can stand up a pipe of the expected name; name-only trust lets the client accept attacker-chosen bytes. The E0a probe's `Receive-ProbeSecret` connects by name only (acceptable for the probe, where a lost race makes the real server fail `FIRST_PIPE_INSTANCE` and never serve the secret), but the production controller must check the server principal.
- The controller must harden its own process against the agent SID. E0a C8 found that on Windows 11 Home the agent account opened the controller/harness process with `PROCESS_TERMINATE` (vm-write was denied, terminate was not), so the agent could kill the controller: this breaks I4. Two standard accounts in the same session can open each other by default, so account separation alone is not enough. The real controller must set a restrictive DACL on its own process (for example `SetSecurityInfo` with a deny ACE for the agent SID covering `PROCESS_TERMINATE`, `PROCESS_VM_WRITE` and `PROCESS_VM_OPERATION`) at startup. The probe correctly records C8 as a failing finding; it is left failing honestly rather than reclassified.
- Confirm the controller works in Session 0, not only interactively. E0a Run A ran the harness as `ac-probe-ctl` from a scheduled task (Session 0, non-interactive, limited) and timed out at the five minute limit while Run B, the same harness started interactively, completed. The real controller is expected to run as a scheduled task or service, which is also Session 0, so this difference must be understood before relying on it there. The probe now writes a `results-A.json` recording the timeout even when the task is killed; the next run's Run A results will show which checks ran and localise where Session 0 diverges from interactive.
- Containment holds only for processes assigned to the job before they run. The C7 and C8 children outlived Run B in both runs because they were never in a job: those checks start the child without creating or assigning one. Their `finally` cleanup reopens the child by PID with `PROCESS_ALL_ACCESS` through `KillPid`, which failed silently, most likely because the controller account is not granted the full access mask on the agent's process. The real controller must start every agent process suspended and assign it to its job before resuming it. It must kill through the process handle `CreateProcessWithLogonW` returns or through the job, never by reopening a PID with broad access. It must check and record every kill result. Details are in `docs/e0a-findings.md`.
