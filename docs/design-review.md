# AgentContinuum — Design Review & v4.0 Revisions

Review of Final System Design v3.1 (rev. 2, 10 Oct 2026) against existing tools · Oct 16, 2026 · @Shekhar

## Verdict

Keep v3.1's safety core, but rebuild detection, recovery and scheduling on techniques other tools have already proven, and reposition the product. The "resume after reset" mechanism is no longer a differentiator. Policy-bounded, billing-safe, integrity-checked continuation still is.

Two facts changed after v3.1 was written. A changelog mirror reports that Claude Code 2.1.234 resumes sessions itself when a claude.ai limit resets. unsnooze (MIT, Node) already resumes Claude Code and Codex on Windows, macOS and Linux.

v3.1's weakest point is evidence, not safety. Its rule registries start empty, and B1 "may take weeks of ordinary use" before automatic mode can be enabled. Prior art shows usable signals exist today: Claude Code's `StopFailure` hook, Codex rollout files with exact reset times, and the `rate_limits.five_hour.used_percentage` value.

The biggest opportunity: judged from their published documentation, none of the tools reviewed combine a one-time task policy, a billing-route check, workspace integrity checks and a cheap handoff-based restart. That combination is AgentContinuum's real product.

## The landscape

At least five tools already resume sessions after a limit, and Claude Code may now do it natively. None of them bound what the agent may do once resumed.

| Tool | Detects the limit by | Resumes by | Safety posture | What to take from it |
| --- | --- | --- | --- | --- |
| [Claude Code native](https://github.com/marckrenn/claude-code-changelog/releases/tag/v2.1.234) (2.1.234, reported) | Internal | Resumes the open session when a claude.ai limit resets | Session's own permissions | Must coexist with it, not compete. Verify on your version. |
| [unsnooze](https://github.com/saaranshM/unsnooze) | `StopFailure` hook (carries session\_id), pane banners, transcripts; Codex rollout files give exact reset time | Types into a proven-owned idle pane, or reopens with `claude --resume <id>` / `codex resume <id>` | No skip-permissions; never presses a menu option blind or picks "Upgrade your plan"; no sandbox | Detection sources, ownership proof, epoch polling, verify-then-reschedule, doctor/preview/uninstall UX |
| [claude-autopilot](https://pkg.go.dev/github.com/hseinmoussa/claude-autopilot) | Exit code 75, stderr patterns | Task queue; `--resume` with saved session ID, else re-prompt | Optional skip-permissions flag (off by default) | Managed-task queue model matches v3.1's managed tasks |
| [claude-auto-continue](https://github.com/oguztecimer/claude-auto-continue) | Scrapes PTY output, parses reset timestamp | Sends `continue` into the PTY | Keystroke injection only | Reset-time parsing; shows the fragility of scraping |
| [claude-handoff-revive](https://github.com/sofumel/claude-handoff-revive) | `PostToolUse` hook reads `rate_limits.five_hour.used_percentage` | Fresh session from a 1–3k-token handoff file | Read-only of git state; best-effort secret scan | Proactive save at 90%/95%; handoff schema; freshness check |
| [claude-relay](https://github.com/Smash420-cmd/claude-relay/releases) | Usage-limit stop | Retries on the other provider, then waits for reset | Cross-provider failover | Counter-example: v3.1 rightly forbids failover |

## What v3.1 gets right

These are the parts no reviewed tool has, and they are the product. Keep them unchanged unless a revision below says otherwise.

- **One-time task policy (Section 9).** No other tool bounds attempts, deadline, permission profile and acceptance command per task.
- **Billing layers and per-provider enablement (Section 12).** Other tools trust the session's billing. v3.1 is the only design that refuses to launch on an unverified route.
- **Workspace integrity (Section 13).** "Unchanged since the snapshot" is the right test. No other tool checks the worktree before resuming.
- **Stop-and-ask fallback with reason categories (Sections 8, 10).** Assisted mode as the first shippable product is correct.
- **No failover, no bypass modes (Sections 2, 9).** unsnooze reaches the same conclusion on skip-permissions independently, which is good validation.
- **Content-blind controller (Section 4).** A credible selling point for anyone wary of tools that read transcripts.

## Gaps exposed by prior art

Three findings are high severity: a second resumer on the same session, an evidence bottleneck that blocks automatic mode, and a paid-upgrade path through the limit menu.

| ID | Finding | Severity | v3.1 section | Evidence from prior art |
| --- | --- | --- | --- | --- |
| N1 | Native Claude Code resume is not modelled. Two resumers on one session risk duplicate launches, and a native resume skips every pre-flight check. | High | 8, 10, 17 | Claude Code 2.1.234 changelog entry |
| N2 | Rule registries start empty, so automatic mode waits weeks for a genuine capture. Structured signals already exist. | High | 11, 12.4, B1 | unsnooze uses `StopFailure` (with session\_id) and Codex rollout files |
| N3 | The interactive limit menu can offer paid options. Layer 3 only watches runtime signals, not a menu answer. | High | 12 | unsnooze never presses a menu option blind and never picks "Upgrade your plan" |
| N4 | Reset time is treated as unknowable, so each probe spends a counted attempt. CLIs print the reset time. | Medium | 11.1 | auto-continue parses the timestamp; unsnooze reads exact Codex reset times |
| N5 | The snapshot happens at the limit, too late for a good handoff. progress.md has no trigger. | Medium | 13.3, 14 | handoff-revive saves at 90% and 95% of the 5-hour window |
| N6 | Native resume replays the whole transcript, often 100k+ tokens, at the start of the new window. | Medium | 9, 14 | handoff-revive measures 1–3k tokens for a handoff restart |
| N7 | Durable timers depend on Task Scheduler wake under a second account. | Medium | 15 | unsnooze polls the clock every 30 s, so a sleeping laptop still wakes on time |
| N8 | Automatic mode is gated on C1/C2 isolation, which E0a may rule out. Users get only assisted mode until then. | Medium | 6, 20 | No reviewed tool sandboxes the agent; all ship value without it |
| N9 | No operator tooling: health check, dry-run, uninstall, status dashboard. | Low | — | unsnooze `doctor`, `preview`, `status`, `uninstall`; handoff-revive `doctor`, `preview` |
| N10 | No position on window priming (spending a small prompt to start the 5-hour window early). | Low | 2 | unsnooze offers it experimentally |

## v4.0 revisions

Ten changes, one per finding, revised after an external review on 16 October 2026. R1–R3 gate any automatic resume; R4–R7 cut cost and waiting; R8–R10 are product decisions. The review's five mandatory requirements follow as R11–R15.

### R1 — Coexist with native resume (N1, Sections 8, 10, 17)

- Native resume must be disabled by a mechanism verified for the installed version, or AgentContinuum must hold exclusive control of the session (R11). Otherwise the task never resumes automatically; it runs assisted.
- Seeing the session ID run without an AgentContinuum launch moves the task to NEEDS\_ATTENTION ("Resumed outside AgentContinuum"). This is a backstop that detects, not the safeguard that prevents.
- Leases govern cooperating AgentContinuum workers only, never external processes.
- Manual sessions: defer to native resume. AgentContinuum only observes, snapshots and offers adoption.

### R2 — Use structured signals now (N2, Sections 11, 12.4, B1)

- Promote `StopFailure` and Codex rollout reset fields to Tier 1 candidates only with a verified usage-limit reason code and a session-ID match to the managed launch. A bare stop event does not prove a quota stop.
- `StopFailure` is a lifecycle event, so the recorder can subscribe without breaking Section 4.2.
- B1 becomes two steps: confirm the signal schema on the installed version, then one genuine consented capture for per-provider enablement. Tier 3 stderr scraping still never drives automatic resume.

### R3 — Never answer a limit menu (N3, Section 12)

- R3 closes the menu path only. Billing proof stays with v3.1 Layer 1 route verification on every launch, strengthened by R15.
- Managed tasks run headless, and AgentContinuum never sends keystrokes or menu answers to any session. A launch that produces an interactive prompt is terminated and goes to NEEDS\_ATTENTION with "Billing unverified".

### R4 — Schedule from the reported reset time (N4, Section 11.1)

- Add RESET\_EXPECTED\_AT from the provider's reported reset time, stored as a UTC epoch. Banner text with a local time zone is parsed only by version-ranged rules.
- ATTEMPT\_PERMITTED is that time plus a margin (proposed 2 minutes). A reported time is not guaranteed availability, so it never sets RESET\_CONFIRMED; a still-limited launch consumes its attempt and backs off.
- Attempt accounting: every process start counts, including launches that fail, crash or end ambiguously. Waits and pre-flight failures count nothing, as in Section 15.
- A detected clock jump triggers reconciliation, never a burst of attempts.

### R5 — Checkpoint before the limit (N5, Sections 13.3, 14)

- The controller's snapshot worker creates the pre-limit checkpoint, never the agent, using the Section 13.2–13.3 scopes and operation (sensitive patterns, symlinks, submodules, temporary index).
- Snapshot Git commands run with hooks disabled and repository-level config ignored, so agent-controlled hooks cannot execute.
- Checkpoints are separate from progress notes. A running headless session cannot take a mid-run instruction, so the duty to keep progress.md current comes from the task's launch instructions, not injection. This corrects the earlier wording.
- Usage percentage comes only from a source with no tool content. `PostToolUse` is ruled out, because parsing it exposes the parser to the payload. With no clean source, R5 is dropped and the snapshot at the limit remains.

### R6 — Handoff restart by default (N6, Sections 9, 14)

- Recovery strategy is chosen per policy, with no global default. A fresh session saves tokens but can lose context or repeat completed side effects; native resume keeps context at a higher token cost.
- A fresh restart requires the handoff contract (R14) and transactional recovery (R13). A hash change is a liveness signal only, never proof the handoff is complete or safe.

### R7 — Poll the clock (N7, Section 15)

- A 30-second wall-clock check catches every reset that passes while the controller is awake. It cannot wake a sleeping machine.
- Task Scheduler wake is the only wake path, and it depends on power settings and hardware. `ac doctor` checks that wake timers are allowed; if not, a reset during sleep is handled at the next wake.

### R8 — Separate the best-effort tier (N8, Sections 6, 20)

- Renamed **Best-effort automatic**. It carries no tamper-resistance or policy-enforcement guarantee, because the agent shares the account that stores its policy, attempts and state. Acknowledgement by you is not a mitigation.
- It is labelled and documented apart from the enforced product. Isolated automatic mode (R12) is the only tier where policies are enforced.
- Decision for you: ship it as a separate convenience tier, or skip it and wait for isolation.

### R9 — Operator commands (N9)

- `ac status` (tasks, reset countdowns), `ac preview <task>` (pre-flight dry-run that launches nothing), `ac doctor` (install health, wake timers).
- `ac resume-now <task>` runs every pre-flight check except three. Provider enabled and Outside services exist only to downgrade a launch to assisted, so they do not apply; Eligibility is waived because you chose the time, but the launch still counts as an attempt. Billing, workspace, binary, process, admission, permission and controller-integrity checks all apply.
- `ac uninstall` removes only the entries AgentContinuum added, matched by marker. It never restores whole backup files over later legitimate changes, and reports any conflict.

### R10 — Exclude window priming (N10, Section 23)

- Add a decision record entry: no priming. It spends quota outside any task policy and conflicts with "no guessing reset times or deliberately exhausting quota".

### Mandatory requirements R11–R15

R11 and R13–R15 are mandatory for both automatic tiers. R12 is mandatory only for isolated automatic mode; best-effort mode lacks it, so invariant I4 (agent cannot change policy, counters or state) does not hold there, and neither do policy limits that rely on it.

| ID | Requirement | It must establish |
| --- | --- | --- |
| R11 | Exclusive session ownership | No automatic launch unless native and external resumers are verifiably disabled or controlled |
| R12 | Protected execution boundary | The agent cannot modify controller policies, counters, launch configuration or trusted state |
| R13 | Transactional recovery | Crashes, retries, duplicate actions and uncertain completion are handled without assuming exactly-once execution |
| R14 | Handoff trust model | Trusted controller instructions are separate from untrusted agent-authored state: versioned schema, atomic write, size bound, manifest |
| R15 | Billing-route attestation | Provider, authentication mode and permitted route are verified before each launch, configuration is protected from the agent, and launch fails closed |

R14 resolves the content-blind contradiction: the controller still never reads the handoff, but the resumed agent does, so the fixed continuation prompt treats it as untrusted data. The controller cannot judge handoff quality, and the design says so.

Adversarial acceptance suite, required before Release 2: malicious handoff content, controller crash mid-launch, simultaneous native resume, clock changes, corrupted rollout files, billing configuration changes, and policy-store tampering.

Still owed before Release 2: the threat model (draft: Threat model), the exclusive-ownership protocol (R11), the protected-state architecture (R12) and the transactional recovery specification (R13). The threat model is drafted; concrete specifications for R11–R15 move to a separate document, Security & Recovery Architecture.

## Positioning and scope

Pitch AgentContinuum as the safe way to let coding agents work unattended, not as an auto-resumer. The resume is a commodity; the policy, billing and integrity guarantees are not.

| Path | What you build | Effort | Fits |
| --- | --- | --- | --- |
| Standalone (v3.1 + R1–R10) | Full controller, recorder, scheduler, policy store | High | Selling it; portfolio piece showing systems and security design |
| Policy layer on unsnooze (MIT) | Policy store, pre-flight, billing and integrity checks; reuse its detection and revival | Medium | Fastest path to a working product; needs its maintainer's interfaces to stay stable |
| Personal script | Detect, wait, relaunch with handoff prompt | Low | Your own use only |

Recommendation: standalone, because the safety claims depend on controlling the launch path end to end. Study unsnooze's detection and revival code first, and credit it.

Scope change: Windows remains the first target, but keep platform-specific code (Job Objects, ACLs, Task Scheduler) behind interfaces. Every reviewed tool is cross-platform, and buyers will expect it.

## Work packages and order

Ship assisted mode first, then add automatic modes one gate at a time. Release 2, the best-effort tier, is optional and carries no enforcement guarantee; Release 3 is v3.1's Stage E plus R12.

&#91;embedded content: v4.0 release roadmap · 3 releases, 2 gates\]

E0a still runs right after A1, alongside A2, so a failed probe reshapes Release 3 before any controller is built. R11, R13–R15 and the adversarial suite gate both automatic releases; R12 gates Release 3 only.

In Release 1, a clicked resume runs the same billing and integrity pre-flight as any automatic launch.

## New risks and open questions

The largest new risk is product risk: if native resume grows to cover headless runs and Codex, the remaining value is the safety layer alone.

- **Native resume scope.** The 2.1.234 entry comes from a third-party mirror summarising the official changelog. Its exact scope is unconfirmed.
- **Young prior art.** unsnooze has 176 stars; handoff-revive has 6. Treat their techniques as leads to verify, not proven contracts.
- **Undocumented formats.** Codex rollout files and `StopFailure` payloads may change between versions. R2 keeps them version-ranged for that reason.

Claims about native resume, competitor capabilities and event schemas in this document are research assumptions until tested against your installed CLI versions.

Open questions to settle before E0:

- [ ] Does native resume act on headless (`-p`) sessions, and can it be turned off per session?
- [ ] Does `StopFailure` fire for usage-limit stops in headless mode, and which fields does it carry on your installed version?
- [ ] Which Codex rollout fields hold the reset time, and where are the files on Windows?
- [ ] Is there a usage-percentage source that carries no tool content?
- [ ] Ship Best-effort automatic (R8) as a separate convenience tier, or skip it and wait for isolation?

## Sources

- AgentContinuum Final System Design v3.1, revision 2 (10 October 2026), uploaded file
- [Claude Code 2.1.234 changelog mirror](https://github.com/marckrenn/claude-code-changelog/releases/tag/v2.1.234), citing the official anthropics/claude-code CHANGELOG
- [unsnooze README](https://github.com/saaranshM/unsnooze)
- [claude-handoff-revive README](https://github.com/sofumel/claude-handoff-revive)
- [claude-autopilot package docs](https://pkg.go.dev/github.com/hseinmoussa/claude-autopilot)
- [claude-auto-continue README](https://github.com/oguztecimer/claude-auto-continue)
- [claude-relay releases](https://github.com/Smash420-cmd/claude-relay/releases)
- [Feature request: auto-resume after rate limit (anthropics/claude-code #26775)](https://github.com/anthropics/claude-code/issues/26775)
