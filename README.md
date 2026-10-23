# AgentContinuum

A Windows tool that resumes Claude Code work after a usage limit, but only when it can show the resume is safe: billed to your subscription, on an unchanged workspace, within the attempts you allowed, and never twice after a crash.

Claude Code is reported to resume an open session itself when a claude.ai limit resets (2.1.234, per a changelog mirror; see [docs/design-review.md](docs/design-review.md)). Resuming is therefore not the point of this project. The point is the safety and policy layer around a resume, and the engineering that backs each of its guarantees.

## What it does

Release 1 is assisted mode. You define a task, and AgentContinuum runs Claude Code on it headless. When the run stops at a usage limit, it waits for the reported reset plus 2 minutes and shows a Windows notification with a Resume button. A resume runs the full pre-flight again and, only if every check passes, continues the same Claude session. Nothing launches without your request.

A launch is refused when:

- the billing route cannot be verified as your subscription, rather than an API key or paid overage;
- the workspace changed since the run stopped;
- the task is out of attempts or past its deadline;
- the pinned Claude Code binary changed;
- a process for the session may still be alive;
- any check cannot run at all. Doubt ends in NEEDS_ATTENTION, never in a launch.

The controller is content-blind. Prompt text, model output and tool data never reach its database, logs or notifications. Only the task label is stored as free text.

## Why it is interesting

- **A threat model first.** [docs/threat-model.md](docs/threat-model.md) treats the agent itself, compromised by prompt injection, as the main adversary. It lists 16 threats and 7 invariants, and records which invariants each release can enforce. Release 1 says plainly that it does not stop an agent sharing your account from editing state; your click is the control there.
- **Measured before built.** E0a ([docs/e0a-findings.md](docs/e0a-findings.md)) is a PowerShell probe that created two throwaway standard accounts and tested whether one could contain the other with Windows job objects. Containment worked, but the probe found that the agent account could open the controller process for terminate, so the Release 3 controller must deny that access to the agent account at startup. E1 ([docs/e1-findings.md](docs/e1-findings.md)) captured which Claude Code fields tell a subscription login from an API key, with an invalid-key run as the negative control. Both probes record key names and enumerated values only, never secrets or content.
- **Test first.** Every behaviour went in as a failing test commit followed by the code that makes it pass. The last full Release run passed 492 tests: 406 Core, 23 Windows, 44 CLI and 19 acceptance. Tests never call the real Claude Code; a synthetic CLI, FakeClaude, replays scripted runs.
- **Acceptance against crashes.** The acceptance suite kills the daemon at each of the five crash points in the launch ledger, runs 1,000 randomised crash and restart cycles without exceeding the attempt limit, and scans the database, logs and notifications for sentinel strings. Results are in [docs/release1-acceptance.md](docs/release1-acceptance.md).
- **Independent review.** Changes that touch launch, billing, process control or recovery were reviewed by separate review agents. Findings were fixed test first, and accepted Low items were logged rather than dropped.
- **Honest tracking.** [docs/known-issues.md](docs/known-issues.md) lists every open item, including gaps the hand check could not close.

## Architecture

Three projects, with all Windows code behind interfaces:

- `AgentContinuum.Core`: policy, pre-flight, billing proofs, launch ledger, recovery, scheduling and state. No Windows APIs.
- `AgentContinuum.Windows`: job objects, process start and listing, and notifications.
- `AgentContinuum.Cli`: the `ac.exe` commands and the daemon.

The pieces:

- **Daemon** (`ac daemon`). Runs in your session. It owns the job objects, launches Claude Code, reads its event stream through a filter that keeps only allowlisted fields, checks the clock every 30 seconds and shows notifications. Commands reach it through a request table in the SQLite database.
- **Pre-flight.** Eight checks run before every launch, first or resumed, and every failure is reported with a reason code: admission, eligibility, process, binary, workspace, permission, billing and controller. `ac preview` runs them without launching.
- **Launch ledger and crash recovery.** Each launch writes an INTENT row and counts the attempt in one transaction before any process starts. The process starts suspended in a kill-on-close job, its PID and creation time are recorded, and only then does it run. On restart, recovery reads each unfinished row and decides, from what it shows, whether work may have run. Recovery never launches on its own, so a crash cannot produce a launch you did not ask for; AC4 and AC5 test this.
- **Billing proofs.** Six proofs, all required: an allowlisted child environment (P1), no billing overrides in any settings scope (P2), a subscription credential judged by key names only (P3), `claude auth status` reporting a subscription (P4), the stream's `system/init` event reporting no API key (P5), and your attestation, under 30 days old, that paid overage is off (P6). P5 is checked again on the live stream, and a mismatch terminates the run.
- **Version rules.** `src/AgentContinuum.Core/Rules/claude-code.json` holds the fields each proof and the limit signal read, per verified Claude Code version range. A version outside every range makes those proofs unevaluable, so the launch is refused.
- **Workspace fingerprint.** SHA-256 over every tracked or unignored file and `HEAD`, listed with git's hooks and fsmonitor disabled. The baseline is taken only once the job has no processes left, and pre-flight requires an exact match.
- **Notifications.** One offer per wait, showing only the label and fixed text, with Resume and Dismiss buttons. A wall-clock jump is detected against the monotonic clock and never produces a second offer.

## Status

- **Release 1, assisted mode: built.** The automated acceptance suite passes, and a hand check passed on Windows 11 on 2026-10-23: a limit was detected, one offer appeared about 3 minutes later, and a resume continued the same session to completion. Both ran on the test host `ac-test.exe` with FakeClaude, not on real Claude Code.
- **Not yet usable against real Claude Code.** The shipped rules cover only Claude Code 2.1.304 and have no limit signal yet, because the E1 capture at a real limit has not run. Until it does, `ac.exe` treats every limit-like stop as unverified and asks for your attention. On any other Claude Code version, billing cannot be verified and every launch is refused.
- **Release 2, best-effort automatic, and Release 3, isolated automatic: designed, not built.** Release 3 runs the agent in a separate standard account so it cannot change the controller's policy or state. See [docs/security-recovery-architecture.md](docs/security-recovery-architecture.md).

Key documents:

| Document | Covers |
| --- | --- |
| [docs/design-review.md](docs/design-review.md) | Prior art and why the safety layer is the product |
| [docs/threat-model.md](docs/threat-model.md) | Assets, adversaries, threats and invariants |
| [docs/security-recovery-architecture.md](docs/security-recovery-architecture.md) | Guarantees by release, the launch ledger and recovery table |
| [docs/superpowers/specs/2026-10-20-release1-assisted-design.md](docs/superpowers/specs/2026-10-20-release1-assisted-design.md) | Release 1 design |
| [docs/superpowers/plans/2026-10-20-release1-assisted.md](docs/superpowers/plans/2026-10-20-release1-assisted.md) | Release 1 build plan |
| [docs/e0a-findings.md](docs/e0a-findings.md), [docs/e1-findings.md](docs/e1-findings.md) | Probe results |
| [docs/release1-acceptance.md](docs/release1-acceptance.md) | Acceptance and hand check results |
| [docs/known-issues.md](docs/known-issues.md) | Open items |

## Limitations

The full list is in [docs/known-issues.md](docs/known-issues.md). The main ones:

- **No real limit detection yet.** As above, the limit signal is still missing from the shipped rules, and a new Claude Code version needs a new rules entry backed by fresh evidence.
- **The Resume button click is unconfirmed.** In the hand check the notification appeared, but it slid away before Resume could be clicked reliably. One click reached the controller and was ignored, and the log cannot show whether it hit the toast body or the button. The resume itself was confirmed with `ac resume`, which sends the same request.
- **Assisted mode shares your account.** An agent that runs as you could edit the state database. Release 1 does not claim otherwise; the protection is that nothing launches without you.
- **The process check can miss a renamed CLI.** Your own processes that Windows will not let it read count as a possible session only if their name matches a pinned CLI, so an elevated Claude Code under another name could be missed.
- **Some crash windows remain.** A daemon crash between creating a suspended process and assigning it to its job leaves a suspended orphan. It never runs, but it lingers.
- **The daemon handles one request at a time.** A command sent during another launch's pre-flight can time out and print `daemon not running` although the daemon is up. It fails safe, but the message is wrong.

## Build and run

Requires Windows 10 or 11 and the .NET 8 SDK.

```powershell
dotnet build -c Release
dotnet test -c Release                                        # the acceptance suite adds 10 to 15 minutes
dotnet test -c Release --filter "FullyQualifiedName!~Acceptance"
```

`ac.exe` is built to `src\AgentContinuum.Cli\bin\Release\net8.0-windows10.0.19041.0\`. State lives in `%LOCALAPPDATA%\AgentContinuum\`.

Commands:

| Command | Does |
| --- | --- |
| `ac daemon` | Runs the controller in this window |
| `ac attest-overage-off` | Records that paid overage is off; valid for 30 days |
| `ac task add --label <l> --worktree <dir> --prompt-file <file> --permission-mode <default\|acceptEdits\|plan> --max-attempts <n> --deadline <utc> [--allowed-tools <rule>]... [--cli <claude.exe>]` | Adds a task and pins the Claude Code binary |
| `ac task start <task>` | First launch |
| `ac resume <task> [--now]` | Resume after a limit or from NEEDS_ATTENTION |
| `ac status` | Tasks, states, attempts and countdowns |
| `ac preview <task> [--now]` | Runs pre-flight and launches nothing |
| `ac pin-cli <task> [--cli <claude.exe>]` | Re-pins after a Claude Code update |
| `ac accept-workspace <task>` | Takes a new workspace baseline after you change the worktree |
| `ac revoke <task>` | Stops a running launch and refuses further launches |

Example:

```powershell
ac attest-overage-off
ac task add --label "fix tests" --worktree C:\src\myrepo --prompt-file prompt.md --permission-mode acceptEdits --max-attempts 3 --deadline 2026-11-01T00:00:00Z
ac daemon                  # in its own window
ac task start <task id>
ac status
```

The prompt file must be inside the worktree. Its content goes to Claude Code on stdin and is never stored.
