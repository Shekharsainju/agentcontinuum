# Threat model

Covers the enforced tiers only: assisted mode and isolated automatic mode. Best-effort automatic carries no guarantees and is out of scope.

## Scope and assumptions

The main adversary is the agent itself, once compromised by prompt injection. Everything else is secondary.

Assumptions, each of which the design relies on:

- Windows enforces separation between two standard local accounts (to be proven by E0a and E0).
- You, the user, are not compromised, and the controller UI is only operated by you.
- The provider applies usage limits server-side, and its CLIs report them honestly when unmodified.
- The installed CLI binaries match their pinned identity at launch.

Out of scope: an attacker with administrator rights or physical access, a compromised provider, malware already running as the controller account, and the best-effort automatic tier.

## Assets

Money and the policy store matter most: losing either breaks the product's core promise.

| ID | Asset | Lives in | Harm if lost |
| --- | --- | --- | --- |
| AS1 | Your money and subscription quota | Provider account | Unapproved charges; quota burned outside policy |
| AS2 | Policy store, attempt counters, task state | Controller zone (SQLite) | Agent widens its own autonomy |
| AS3 | Billing configuration: auth mode, keys, overrides | CLI settings, environment | Silent switch to a paid route |
| AS4 | Controller code, keys, scheduled tasks | Controller zone | Every check can be bypassed |
| AS5 | Workspace and snapshot refs | Agent zone (repo, `refs/agentcontinuum/*`) | Resume on tampered or lost work |
| AS6 | Privacy of task content | Must never enter controller zone | Content-blind claim broken |
| AS7 | Your other files and repositories | Your account | Damage outside the task |

## Adversaries

Only ADV1 is actively hostile and fully capable inside your account. The others cause harm by accident or drift, but the defences must hold against all five.

| ID | Adversary | Capabilities | Reaches it via |
| --- | --- | --- | --- |
| ADV1 | Compromised agent | Runs any command your account can: edits files, settings, environment, Git hooks; writes the handoff; spawns processes | Prompt injection from repo content, web pages, tool output, a poisoned handoff |
| ADV2 | Malicious repository | Ships hooks, project-scope CLI settings, instruction files, symlinks | Cloning or pulling untrusted code |
| ADV3 | CLI behaviour drift | Changes events, flags, billing behaviour or adds native resume between versions | Auto-update |
| ADV4 | Local misconfiguration | An API key in the environment, a changed setting, another auto-resume tool | You or other software |
| ADV5 | Environment faults | Crash, power loss, sleep, clock change, disk full | The machine itself |

## Trust boundaries

TB1 separates the two accounts; every arrow across it is a place where I1–I5 must hold. TB3, the handoff, sits wholly inside the agent zone, which is why the controller can never vouch for it.

&#91;embedded content: trust boundaries · 2 zones, 4 crossings\]

Only events flow into the controller. Everything else is the controller reaching out to verify, snapshot or launch, and none of it trusts what the agent zone reports.

## Threats

Sixteen threats; four have no designed mitigation yet (T1, T4, T8, T12 depend on R11–R13 and E0). STRIDE letters: S spoofing, T tampering, R repudiation, I disclosure, D denial of service, E elevation.

| ID | Threat | STRIDE | Asset | From | Mitigation | Residual risk | Status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| T1 | Agent edits policy, counters or task state to widen its autonomy | T, E | AS2 | ADV1 | Controller-zone storage with ACLs (R12, v3.1 6) | None if isolation holds | Open: needs E0 |
| T2 | Agent sets an API key, token or base URL to move launches onto paid billing | T, E | AS1, AS3 | ADV1, ADV4 | Route verification per launch, sanitised env, protected config, fail closed (R15, v3.1 12.1) | Account-level overage; covered by attestation only | Designed; protection of config open |
| T3 | Poisoned handoff tells the next session to disable checks or exfiltrate | T, E | AS4, AS7 | ADV1 | Fixed prompt treats handoff as untrusted; schema and size bound; permission profile limits damage (R14, v3.1 14) | The next agent may still follow it within its permissions | Partly designed |
| T4 | Native resume launches the same session concurrently | T, D | AS1, AS5 | ADV3 | Verified disable or exclusive control, else assisted (R11) | None if R11 holds | Open: mechanism unknown |
| T5 | Forged limit event triggers snapshots and launches | S | AS1 | ADV1 | Reason code plus session match; Tier 3 never sufficient; attempt limits (R2, v3.1 11) | Bounded by policy attempts | Designed |
| T6 | Snapshot worker runs agent-controlled Git hooks or config | E | AS4 | ADV1, ADV2 | Hooks off, repo config ignored, pinned Git binary (R5) | Git parsing bugs | New in v4 |
| T7 | Workspace changed while waiting | T | AS5 | ADV1, ADV4 | Fingerprint equals snapshot at pre-flight (v3.1 13) | Ignored and external state not covered | Designed |
| T8 | Crash mid-launch leaves duplicate run or lost count | D, R | AS1, AS2 | ADV5 | Write-ahead launch record, reconcile on restart (R13) | Uncertain completion after a crash | Open: needs R13 spec |
| T9 | Clock change causes a burst of attempts | D | AS1 | ADV5 | UTC epoch, reconcile on jump, no bulk replay (R4) | None | New in v4 |
| T10 | Corrupted or forged rollout file gives a wrong reset time | S | AS1 | ADV1, ADV3 | Version-ranged parsing; never RESET\_CONFIRMED; attempts counted (R4) | Wasted attempts within policy | New in v4 |
| T11 | Task content leaks into logs, database or notifications | I | AS6 | ADV1 | Content-blind ingestion, sentinel privacy tests, no `PostToolUse` (v3.1 4, R5) | CLI's own transcripts remain | Designed |
| T12 | Launch helper in the agent zone lies to the controller | S, T | AS2, AS4 | ADV1 | Controller verifies process identity and snapshots itself (v3.1 6.2) | Depends on E0a result | Open: needs E0a |
| T13 | PATH or binary hijack runs a fake CLI | E | AS3, AS4 | ADV1, ADV2 | Pinned path and file identity (v3.1 7) | Pinned binary itself replaced if writable | Designed |
| T14 | Project-scope settings widen permissions or set billing keys | E | AS1, AS3 | ADV2 | Profile applied by controller; project billing keys force UNVERIFIED (v3.1 12.1) | Unknown future setting scopes | Designed |
| T15 | Agent exhausts disk, processes or attempts | D | AS2 | ADV1 | Job Object, timeouts, attempt limits (v3.1 15) | Task stalls, does not escalate | Designed |
| T16 | Acceptance command abused to run code with network | E | AS7 | ADV1, ADV2 | Command fixed in policy; network-off enforced or click required (v3.1 9, gate 20.1-R4) | Network denial may be unenforceable under C1 | Designed |

## Security invariants

These seven statements are the product's guarantee; each must be testable and hold even when ADV1 is active.

| ID | Invariant | Defeats | Verified by |
| --- | --- | --- | --- |
| I1 | No launch without a full, fresh pre-flight pass, clicked or automatic | T5, T7, T13 | Pre-flight bypass tests |
| I2 | No launch unless the billing route is VERIFIED-SUBSCRIPTION at that moment | T2, T14 | Billing configuration change tests |
| I3 | Launches per window and in total never exceed the policy, across crashes | T8, T9, T10, T15 | Crash-mid-launch and clock tests |
| I4 | Nothing in the agent zone can create, change or extend a policy, counter or task state | T1, T12 | Policy-store tampering tests run as the agent user |
| I5 | At most one live process per managed session, from any source | T4, T8 | Simultaneous native resume tests |
| I6 | No task content is written to the controller zone, logs or notifications | T11 | Sentinel privacy tests |
| I7 | Every failed or ambiguous check ends in NEEDS\_ATTENTION, never a retry loop | All | Fault-injection suite |

## Implications for R11–R13

The model sets one design rule for all three: launches are at-most-once, and any doubt resolves to NEEDS\_ATTENTION, never to a retry.

### R11 — Exclusive ownership (I5)

- Managed sessions run headless and one-shot. Verify per version that the CLI process exits at the limit, so no in-process native resume can follow.
- Each task gets its own CLI config directory (`CLAUDE_CONFIG_DIR`, `CODEX_HOME`), once A2 proves the overrides are honoured. Native resume and other tools then have no view of the session.
- Every launch runs inside the controller's Job Object. A process carrying the session ID outside that job means the task stops with NEEDS\_ATTENTION.

### R12 — Protected state (I4)

- Controller zone holds: policies, counters, task state, keys, scheduled tasks, the snapshot worker and the pinned Git and CLI identities.
- New finding: billing configuration must live where the CLI reads it, in the agent zone. Checking it before launch leaves a time-of-check to time-of-use gap. Close it by having the controller pass explicit auth settings and a sanitised environment at spawn, then re-verify through the CLI's own auth report right after start.

### R13 — Transactional recovery (I3, I5)

1. Commit LAUNCH\_INTENT with an operation ID and count the attempt.
2. Spawn the process inside the Job Object.
3. Commit LAUNCHED with PID, creation time and session ID.
4. Commit the exit outcome.

On restart, an INTENT without LAUNCHED stays counted and is never relaunched until the process table is checked; anything unclear goes to NEEDS\_ATTENTION. Work the agent did is not idempotent, so a fresh restart after an ambiguous exit runs assisted.

### Adversarial suite mapping

| Suite case | Invariants tested |
| --- | --- |
| Malicious handoff content | I1, I4 |
| Controller crash mid-launch | I3, I5, I7 |
| Simultaneous native resume | I5 |
| Clock changes | I3 |
| Corrupted rollout files | I3, I7 |
| Billing configuration changes | I2 |
| Policy-store tampering | I4 |

Privacy (I6) keeps its existing sentinel tests from v3.1 Section 18.
