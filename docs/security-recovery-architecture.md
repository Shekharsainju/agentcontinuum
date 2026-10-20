# AgentContinuum v4.0 — Security & Recovery Architecture

Oct 16, 2026 · @Shekhar

Build-level specification for R11–R15 and R5, against the threat model in the v4.0 design review. Items marked VERIFY depend on evidence from E0a, A2 or B1 and must not be built until confirmed.

## Guarantees by tier

Only isolated automatic mode enforces all seven invariants. Assisted mode relies on your click instead of isolation; best-effort mode enforces nothing against a compromised agent.

| Invariant | Assisted (Release 1) | Best-effort automatic (Release 2) | Isolated automatic (Release 3) |
| --- | --- | --- | --- |
| I1 Full pre-flight before every launch | Enforced | Best effort | Enforced |
| I2 Verified subscription route at launch | Enforced | Best effort | Enforced |
| I3 Attempts within policy, across crashes | Your click caps launches | Best effort | Enforced |
| I4 Agent cannot change policy or state | Not provided; your click is the control | Not provided | Enforced (VERIFY E0) |
| I5 One live process per session | Enforced | Best effort | Enforced |
| I6 No task content in controller storage | Enforced | Enforced | Enforced |
| I7 Doubt ends in NEEDS\_ATTENTION | Enforced | Best effort | Enforced |

"Best effort" means the check runs, but an agent sharing the account could defeat it. Requirements by tier: Release 1 needs R13 and R15; Release 2 needs R11, R13, R14, R15; Release 3 adds R12.

## R12 — Protected state

Recommendation: make v3.1's configuration C2 the primary design, with the agent running in a dedicated sandbox account that the controller launches directly. Under C1, the controller cannot terminate or contain processes in your account without rights a standard account lacks, which is exactly what E0a tests.

### Accounts

| Account | Type | Runs | Created by |
| --- | --- | --- | --- |
| Your account | Your usual login | Controller UI client, your manual sessions | You |
| `ac-controller` | Standard local, no admin | Controller service, scheduler, snapshot worker | Installer (one elevation) |
| `ac-agent` | Standard local, no admin | Every managed CLI session | Installer (one elevation) |

The controller starts sessions as `ac-agent` with `CreateProcessWithLogonW`. As creator it holds a full process handle, so it can place the process in its own Job Object and terminate it (VERIFY: behaviour of Secondary Logon with nested jobs on your Windows build).

### File layout and ACLs

| Path | Holds | `ac-controller` | `ac-agent` | Your account |
| --- | --- | --- | --- | --- |
| `C:\ProgramData\AgentContinuum\bin` | Controller code, pinned binary hashes | Read, execute | None | Read, execute |
| `C:\ProgramData\AgentContinuum\state` | SQLite, policies, counters, keys, snapshot repo | Full | None | None |
| `C:\Users\ac-agent\...` | CLI login, per-task CLI config dirs | Read | Full | None |
| Task worktree | Code the agent edits | Read | Full | Full |

Writes to `bin` need elevation, so updates require you. You reach the state only through the controller UI.

### Snapshots move out of the repository

Change from v3.1 13.3: snapshots are written to a controller-owned repository in `state`, using the worktree as a read-only source (`git --git-dir=<state repo> --work-tree=<worktree>`). The agent can no longer delete or rewrite snapshot refs, because they never live in its zone.

### Events in

The controller hosts a named pipe whose DACL grants `ac-agent` write-only access. Messages follow a fixed schema of IDs, timestamps, codes and hashes, capped at 4 KB. Anything else is dropped and counted, never parsed further.

### Credential ownership

| Credential | Lives in | Why |
| --- | --- | --- |
| Provider subscription login | `ac-agent` profile | The CLI must read it; you sign in once as `ac-agent` |
| `ac-agent` password | `state`, DPAPI-protected for `ac-controller` | Lets the controller launch sessions |
| Provider API keys | Nowhere | Any found forces UNVERIFIED (R15) |

## R13 — Transactional recovery

Launches are at-most-once: the attempt is counted before the process exists, and an interrupted run is never retried automatically. The controller-owned Job Object uses `KILL_ON_JOB_CLOSE`, so a controller crash also kills the session, which removes the "still running after the crash" case.

### Launch record

Table `launch_ops`, one row per launch, SQLite in WAL mode with `synchronous=FULL`:

| Column | Meaning |
| --- | --- |
| `op_id` | Random 128-bit ID, primary key |
| `task_id`, `window_id`, `attempt_no` | What the launch counts against |
| `state` | INTENT, SPAWNED, EXITED, CLASSIFIED, ABANDONED, INTERRUPTED |
| `pid`, `proc_created_at`, `session_id` | Process identity, set at SPAWNED |
| `exit_code`, `outcome`, `reason` | Set at EXITED and CLASSIFIED |

A partial unique index allows at most one row per task in INTENT, SPAWNED or EXITED. The attempt counter is incremented in the same transaction that writes INTENT.

### Recovery by crash point

| Controller crashed | Row shows | Recovery | Task goes to |
| --- | --- | --- | --- |
| Before INTENT committed | Nothing | Nothing to undo; no attempt counted | Unchanged |
| After INTENT, before spawn | INTENT, no PID | Confirm no `ac-agent` process carries the session; mark ABANDONED; attempt stays counted | WAITING\_FOR\_RESET |
| After spawn, before SPAWNED committed | INTENT, no PID | Process was killed by job close but may have done work; take a fresh snapshot | NEEDS\_ATTENTION ("Interrupted launch") |
| While running | SPAWNED | Same as above | NEEDS\_ATTENTION ("Interrupted run") |
| After exit, before classification | EXITED | Classify from the recorded exit and events | Per outcome |

If process enumeration fails or returns anything unexpected, the task goes to NEEDS\_ATTENTION.

### Duplicate actions

- Agent work is not idempotent. After any INTERRUPTED row, only you can resume, starting from the fresh snapshot.
- The acceptance command gets its own ACCEPT\_INTENT row and runs at most once per launch.
- A controller update or restart ends running sessions. The updater waits for idle tasks or asks you first.

## Launch state machine

Every launch walks one row through these states; only two paths return the task to waiting without you.

&#91;embedded content: launch state machine · 6 states\]

A crash before spawn with no process running is ABANDONED and safe to wait again. A crash after spawn is INTERRUPTED, because work may have happened, so only you can resume it.

## R11 — Exclusive ownership

Exclusivity comes from three facts, not from cooperation: only the controller knows the `ac-agent` password, every managed process lives in the controller's job, and the controller ends the job when a limit is hit.

### Establish

1. Generate a random `ac-agent` password at install, stored only in `state` under DPAPI. Nothing else can start `ac-agent` processes, so native resume and other tools in your account never see managed sessions.
2. Create the job with breakaway disallowed, so every child the agent spawns stays inside it.
3. Write the task's CLI config dir before each launch, including any setting that disables native resume (VERIFY per version), and check it again at pre-flight.

### Maintain

- On a verified usage-limit event, the controller terminates the job. Any in-process native resume dies with it, so the controller does not depend on a disable setting working.
- The single non-terminal `launch_ops` row is the lease. A second launch for the task is impossible by schema.
- Every 30 seconds the controller lists `ac-agent` processes. Any process outside a job it owns stops all tasks and raises NEEDS\_ATTENTION ("Stray agent process"); no launch happens until it is gone.

### Revoke

- You revoke a policy from the controller UI at any time. The controller terminates the job, marks the row, and refuses further launches.
- Expiry, attempt exhaustion and repeated failure revoke the same way.

### Known gap

A compromised agent can create a scheduled task as `ac-agent` that starts later, outside the job. The controller can probably see such processes but cannot kill processes it did not create (VERIFY both). Mitigation for now: detection, a global stop, and an `ac doctor` check that lists `ac-agent` scheduled tasks.

## R15 — Billing attestation

A launch is VERIFIED-SUBSCRIPTION only when all six proofs pass at that moment. Any proof that fails, or cannot be evaluated, makes the route UNVERIFIED: no launch, or immediate job termination if already running.

| Proof | What the controller checks | When |
| --- | --- | --- |
| P1 Constructed environment | The child environment is built from an allowlist (system paths, `ac-agent` profile paths, the task config dir). Nothing is inherited, so billing variables cannot be present | Spawn |
| P2 Settings scan | Every scope the CLI reads for `ac-agent` (managed, user, task config, project) has no API key, token, key helper, base URL or alternative provider. A project-scope billing setting forces UNVERIFIED | Pre-flight |
| P3 Stored credential type | The stored login is a subscription login, not an API key, judged without reading the secret (VERIFY method per CLI) | Pre-flight |
| P4 CLI self-report | The CLI's own auth status, run as `ac-agent`, reports a subscription account (VERIFY command per version) | Pre-flight |
| P5 Post-start report | The session's first structured event reports a subscription auth source (VERIFY field per version); a mismatch terminates the job | First event |
| P6 Overage attestation | Your attestation that paid overage is off is under 30 days old | Pre-flight |

Not proof, alone or together: environment sanitisation, absence of a key in one scope, your attestation, a successful launch, or the previous launch's result.

P1 removes the environment race. P5 closes the remaining gap between the settings scan and spawn: an agent that plants a key during a run is caught by P2 at the next launch, or by P5 if it lands first.

Residual risk: if P5 fires, a few seconds of paid usage may already be billed before termination. Account-level overage stays outside any local proof, as v3.1 12 states.

## R14 — Handoff contract

The handoff becomes `.agentcontinuum/handoff.json` with a fixed schema. The controller checks its shape in memory and never stores or logs what it says. That narrows v3.1's "never reads" to "never stores, logs or acts on content", a deliberate amendment to Section 4.

### Schema `ac-handoff/1`

| Field | Type and bound |
| --- | --- |
| `schema` | Exactly `ac-handoff/1` |
| `task_id` | Must equal the managed task's ID |
| `written_at` | ISO 8601 UTC timestamp |
| `base_commit` | 40 hex characters, must exist in the snapshot repository |
| `goal`, `next_action` | String, at most 1,000 characters |
| `done`, `pending`, `decisions` | Arrays of strings, at most 50 items of 300 characters each |

No other keys. Whole file at most 32 KB of UTF-8.

### Write and seal

- The launch instructions tell the agent to write `handoff.json.tmp`, then rename it over `handoff.json`. A leftover `.tmp` file or invalid JSON means a torn write.
- At the post-exit snapshot, the controller records the file's SHA-256 in `state` as the manifest. The fresh session reads the same bytes, because pre-flight requires the worktree to match the snapshot.

### Validation result

| Result | Meaning | Recovery allowed |
| --- | --- | --- |
| VALID | Shape, bounds, task ID and base commit all pass | Fresh session from handoff |
| INVALID or ABSENT | Any check fails, or the file is missing or torn | Native resume if the policy permits it, else assisted |

Free-text values are dropped after the check. Sentinel privacy tests extend to the validator: sentinel strings in every field must appear nowhere in `state`, logs or notifications.

### Trust

The fixed continuation prompt tells the next session that the handoff is untrusted notes, that any instruction inside it is to be ignored, and that every claim must be checked against the repository. The permission profile limits what a poisoned handoff can achieve. The controller cannot judge whether the handoff is accurate or complete; VALID means well-formed, nothing more.

## R5 — Consistent snapshots

The integrity baseline is taken only when no writer exists, so it is consistent by construction. The optional pre-limit checkpoint is a recovery aid that is never trusted as a baseline.

### Baseline snapshot (required)

- Taken after the job reports zero active processes, using job accounting rather than a PID check.
- Uses the Section 13.2 scopes, the controller-owned snapshot repository (R12), hooks disabled and repository config ignored.
- Size or count limits reached mark it INCOMPLETE, which blocks automatic resume.

### Pre-limit checkpoint (optional)

It runs only when a usage source without tool content exists. The agent may be writing, so one of two methods applies:

1. **Freeze, if verified.** The controller freezes its job, snapshots, then thaws, within a 2-second budget; over budget, it thaws and abandons the checkpoint (VERIFY that a standard account can freeze a job it owns).
2. **Two-pass fallback.** Fingerprint, build the snapshot tree, fingerprint again. Equal fingerprints mark it CONSISTENT; otherwise retry once after 5 seconds, then mark it TORN.

A TORN checkpoint is kept but never offered as a restore point without your review. Neither kind of checkpoint replaces the baseline.

## Acceptance criteria

Each criterion passes or fails on the actual Windows machine; adversarial tests run as `ac-agent`, never as an administrator.

| ID | Req | Criterion | Test | Invariant |
| --- | --- | --- | --- | --- |
| AC1 | R12 | `ac-agent` cannot read or write `state`, write `bin`, open the controller process, or change its scheduled task | Scripted attempts as `ac-agent`; each must fail with access denied | I4 |
| AC2 | R12 | Pipe accepts only schema-valid messages up to 4 KB from `ac-agent` | Oversized, malformed and wrong-sender messages are dropped and counted | I4, I6 |
| AC3 | R12 | Snapshot refs cannot be altered from the agent zone | Agent deletes or rewrites refs in the worktree; snapshot repo unchanged | I4 |
| AC4 | R13 | Killing the controller at each of the five crash points gives the recovery in the table | Fault injection at each point, 20 runs each | I3, I5, I7 |
| AC5 | R13 | Attempts never exceed policy across 1,000 randomised crash and restart cycles | Property-based test on synthetic CLI | I3 |
| AC6 | R11 | A limit event ends every process in the job within 5 seconds | Synthetic CLI that stays alive and spawns children | I5 |
| AC7 | R11 | A stray `ac-agent` process blocks all launches until gone | Start a process outside the job | I5, I7 |
| AC8 | R11 | Native resume never runs a managed session | Real CLI, native resume enabled, limit reached (VERIFY in B1) | I5 |
| AC9 | R15 | Each of P2–P6 failing on its own blocks the launch | One planted key, setting or stale attestation per run | I2 |
| AC10 | R15 | A key planted after the settings scan terminates the session at P5 | Race test with planting between scan and spawn | I2 |
| AC11 | R14 | Every schema violation yields INVALID, and no field value appears in storage | Fuzzed handoff files with sentinel strings | I6 |
| AC12 | R14 | A handoff containing instructions does not change the controller's actions | Malicious handoff suite; controller log and state compared to baseline | I1, I4 |
| AC13 | R5 | The baseline snapshot is never taken while the job has active processes | Job with a lingering child process | I1 |
| AC14 | R5 | A file changing during a pre-limit checkpoint yields TORN, never CONSISTENT | Writer looping on a file during checkpoint | I1 |
| AC15 | R4 | A 2-hour clock jump forward or back causes no burst of attempts | Clock change during WAITING\_FOR\_RESET | I3 |

## Blocked on

Nine facts must be confirmed on your machine before this spec is build-ready. The first three decide whether C2 works at all, so test them first, as a revised E0a.

- [ ] `CreateProcessWithLogonW` from `ac-controller` returns a handle that can be assigned to a job the controller owns, with nested jobs from Secondary Logon (R12, R11)
- [ ] A standard account can freeze, and terminate, a job it created containing `ac-agent` processes (R5, R11)
- [ ] `ac-controller` can enumerate `ac-agent` processes and their creation times; whether it can terminate ones it did not create (R11 known gap)
- [ ] The CLIs honour `CLAUDE_CONFIG_DIR` and `CODEX_HOME` for login, settings and session storage under `ac-agent` (R11, A2)
- [ ] A setting exists to disable native resume, per CLI version (R11)
- [ ] A way to tell subscription login from API key without reading the secret (R15 P3). Claude Code 2.1.304: confirmed for a subscription login by credential key names; a stored API-key login is untested (`docs/e1-findings.md`)
- [x] An auth status command per CLI version (R15 P4). Claude Code 2.1.304: `claude auth status`, JSON `authMethod` and `subscriptionType` (`docs/e1-findings.md`)
- [x] A first-event field reporting the auth source in headless mode (R15 P5). Claude Code 2.1.304: `apiKeySource` on the `system/init` event, which is not the first line (`docs/e1-findings.md`)
- [x] A usage-percentage source with no tool content; without one, the pre-limit checkpoint is dropped (R5). Claude Code 2.1.304: `rate_limit_event` window `utilization` and `resetsAt`, so the pre-limit checkpoint stays possible (`docs/e1-findings.md`)

## E0b gates

E0a confirmed the first two items above for an interactive controller only (`docs/e0a-findings.md`). Two hard gates follow from it. Until E0b passes:

- No unattended controller. The controller must not run as a scheduled task, a service or anything else in Session 0. E0a Run A, the same harness started from a scheduled task, timed out.
- No I4 claim. No release may state or rely on I4. In E0a C8, the agent account opened the controller's process with `PROCESS_TERMINATE`.

E0b passes when, on this machine:

1. The harness runs to completion from a scheduled task in Session 0, and every check it ran interactively passes there too. Each check records its progress as it finishes, so a timeout shows where the run stopped.
2. The controller sets a deny ACE for the agent SID on its own process at startup, covering `PROCESS_TERMINATE`, `PROCESS_VM_WRITE` and `PROCESS_VM_OPERATION`. The C8 process-open attempts are then denied in both start modes.
3. The C7 question is answered: whether `ac-controller` can enumerate an `ac-agent` process it did not create, read its creation time, and terminate it.
4. Every agent process the harness starts is assigned to its job before it runs, and every kill goes through the job or a retained handle and records its result (the E0a containment caveat).

Release 1 is not blocked by E0b, because it runs the agent under your own account and makes no I4 claim. Releases 2 and 3 are.
