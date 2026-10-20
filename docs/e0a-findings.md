# E0a probe findings

Oct 20, 2026

## Conclusion

Separate-account isolation works on this machine. A standard controller account can start a second standard account's process, hold it in its own job, and freeze, thaw and terminate it. The agent account cannot read or write the controller's files or the results.

The real controller is safe to build on this design only if two conditions are met:

1. The controller protects its own process with a deny ACE for the agent SID covering `PROCESS_TERMINATE`, `PROCESS_VM_WRITE` and `PROCESS_VM_OPERATION`, set at startup. C8 showed that a standard account in the same session can open another standard account's process for terminate.
2. The Session 0 timeout in Run A is understood and resolved. The real controller runs as a scheduled task or service in Session 0, and every check that passed did so in Run B, the interactive run.

## Environment

- Windows 11 Home 26H2, build 26300.9711.
- Two standard local accounts, `ac-probe-ctl` and `ac-probe-agent`, members of Users only.
- Secondary Logon service running; probe launched elevated.
- Evidence: `probes/e0a/results/run-02-clean` (main run) and `probes/e0a/results/run-01` (first run).

## Runs

- **Run A** started the harness as `ac-probe-ctl` from a scheduled task, which runs in Session 0. It timed out at the five minute limit in both runs. Run 2 recorded this as `A0 run_outcome: timeout`. No check results exist for Run A.
- **Run B** started the same harness as `ac-probe-ctl` through Secondary Logon. It completed in both runs. The harness was already in a job before it created its own.

## Check results (run 2)

| Check | Tests | Result |
| --- | --- | --- |
| C1 Precheck | Elevated, `seclogon` running, edition and build recorded | Pass |
| S0 Batch logon right | Whether `ac-probe-ctl` needed `SeBatchLogonRight` for the Run A task | Finding: held neither before nor after registering, no grant attempted, Run A timed out. `ac-probe-agent` does not hold it. |
| S1 Secret absent | Neither password appears in any probe or results file | Pass |
| C2 Launch and assign | `CreateProcessWithLogonW` child as `ac-probe-agent`, assigned to the harness's own job | Pass. The child was already in a job before assignment, so nesting works. The job's PID list shows the child. |
| C3 Freeze and thaw | Job freeze through `NtSetInformationJobObject` stops the heartbeat, thaw restarts it | Pass. 6.3 s gap, no beats while frozen, resumed within 0.2 s. |
| C3b Suspend and resume | Same test with `NtSuspendProcess` on the child only | Pass, same timings |
| C4 Terminate | `TerminateJobObject` kills the child and its grandchildren | Pass. 0 active processes, heartbeat stopped. |
| C5 Kill on job close | Killing a sub-harness that owns a kill-on-close job kills its child | Pass. Grandchild gone. |
| C6 Breakaway | Child tries `CREATE_BREAKAWAY_FROM_JOB` | Pass. Creation denied with Win32 error 5. |
| C7 Escape task | Agent registers its own S4U scheduled task to run outside the job | Finding: registration denied ("Access is denied"). No escape process was started. |
| C8 Isolation | Agent tries six accesses that must all be denied | Fail. Five of six denied. The agent opened the harness process with `PROCESS_TERMINATE`. |
| T0 Teardown verify | No account, task, profile, ProfileList entry or right left behind | Pass |

C8 detail:

| Attempt | Outcome |
| --- | --- |
| Read `ctl` folder | Denied |
| Write `ctl` folder | Denied |
| Read results folder | Denied |
| Write results folder | Denied |
| `OpenProcess` harness, `PROCESS_TERMINATE` | Succeeded |
| `OpenProcess` harness, `PROCESS_VM_WRITE \| PROCESS_VM_OPERATION` | Denied, error 5 |

The agent could kill the controller, which breaks I4. VM write was denied by default, but the deny ACE in condition 1 covers it too, so the protection does not rely on that default.

## Changes since run 1

Run 1 used the same build and gave the same results except:

- C5 errored. The sub-harness could not create its scratch folder. Fixed by putting C5 scratch in the folder `ac-probe-ctl` owns. Passed in run 2.
- T0 failed with `profile,profilelist` remaining. The cause was that `Win32_UserProfile` has no `Delete` method, so `Invoke-CimMethod -MethodName Delete` always failed. Teardown now uses `Remove-CimInstance` and reports the underlying error. Run 2 used this fix and passed T0.
- Run A left no results file. The harness now writes `results-A.json` when the run is killed.

## Open items from the runs

- Run A timed out in Session 0. Its `results-A.json` holds only the timeout, so it does not show where Run A stopped.
- Four `ac-probe-agent` processes were alive when Run B ended, including the C7 and C8 children. Teardown stopped them. Why they outlived the harness and its kill-on-close job is not yet explained.

## Mapping to "Blocked on"

Items from `docs/security-recovery-architecture.md`:

| Item | Status | Evidence |
| --- | --- | --- |
| `CreateProcessWithLogonW` handle assignable to the controller's own job, nested with Secondary Logon | Confirmed for an interactive controller. Open for Session 0. | C2 in Run B. Run A timed out. |
| A standard account can freeze and terminate a job it created containing agent processes | Confirmed for an interactive controller. Open for Session 0. | C3, C3b, C4, C5, C6 in Run B |
| Controller can enumerate agent processes and creation times, and terminate ones it did not create | Open | C7 registration was denied, so no process outside the job existed to test against. The agent could not create an S4U task, which closes that escape route for now. |
| CLIs honour `CLAUDE_CONFIG_DIR` and `CODEX_HOME` under `ac-agent` | Open | Outside E0a scope |
| Setting to disable native resume | Open | Outside E0a scope |
| Telling subscription login from API key without reading the secret | Open | Outside E0a scope |
| Auth status command per CLI version | Open | Outside E0a scope |
| First-event field reporting auth source in headless mode | Open | Outside E0a scope |
| Usage-percentage source with no tool content | Open | Outside E0a scope |

E0a also adds two requirements that are not in that list: the controller's process deny ACE (condition 1) and Session 0 operation (condition 2).
