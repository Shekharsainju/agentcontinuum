# Known Issues

Open Low items from the launch coordinator reviews. Each is accepted for now and should be revisited before Release 1 ships.

## Launch coordinator

- Some limit tests do not pin whether the limit kill happened. `UsageLimit_SchedulesReset` and `AttemptsExhausted_NeedsAttention` pass whether or not the run was killed.
- A daemon crash during the empty-job wait after exit leaves the op EXITED. Recovery then applies the outcome without taking a fresh baseline, so the next pre-flight compares against a stale one.
- A launch that fails to start goes to NEEDS_ATTENTION with "Launch failed". R13 recovery of an INTENT op with no process goes to WAITING_FOR_RESET and offers again. The two paths handle the same point differently.
- `BeginIntent` reports every refusal as an `InvalidOperationException`, and the coordinator reports all of them as `ADMISSION_STATE`. A task that runs out of attempts between pre-flight and counting is not flagged as "Attempts exhausted".
- The launch args are built from the task read before `BeginIntent`. A change to the permission mode or allowed tools in between would launch with the old values. Nothing updates those columns after a task is added today.
- If the empty-job wait itself fails (the job query throws) while a grandchild holds stdout open, the stream read can still hang. Only a wait that completes and reports a non-empty job closes the job.
- `--allowedTools` now passes one argument per rule. How 2.1.304 parses the values has not been probed. Rules that start with `-` are refused at add and at launch, so the second check below only confirms that guard is needed. To check, run this in a scratch git repo: `echo "Run git diff --stat, then read README.md" | claude -p --output-format stream-json --verbose --allowedTools "Read" "Bash(git diff:*,stat)" | findstr permission_denials`. Empty `permission_denials` means both rules were honoured. Then add `"-x"` after the last rule: an unknown option error means a leading `-` is read as a flag.

## Windows process host

- `JobProcessHost.Start` can leave the process running outside supervision if the `JobHostedRun` constructor throws after the process has joined its job and resumed.
- A daemon crash between creating the suspended process and assigning it to its job leaves a suspended orphan outside any job. It never runs, but its command line holds the session ID, so recovery of that INTENT row reports "Interrupted launch" and the orphan lingers. Creating the process directly in the job with `PROC_THREAD_ATTRIBUTE_JOB_LIST` would close the window.

## Process check

- Your own processes that WMI cannot read, such as elevated or protected ones, count as a possible session only when their image name is a pinned CLI's. An elevated Claude Code under another image name, such as `node.exe` from an npm install or a renamed copy, that resumes a task's session would now be missed. Before, ctfmon and taskhostw made every launch fail with PROCESS_UNKNOWN.
- Any process named like a pinned CLI that is starting or exiting at the moment of the listing can show no command line, and the launch is then refused with PROCESS_UNKNOWN. It fails safe, and a retry passes. Opening Claude Code at the same moment as a launch can cause it.
- `PreflightRunner`, `LaunchCoordinator` and `RecoveryService` still fall back to the embedded rules when no rules lookup is passed. Every caller passes one today; making it required would stop a future caller judging billing by different rules again.

## Test host

- `ac-test.exe` replaces only `LOCALAPPDATA` and `USERPROFILE`. `APPDATA`, `HOMEDRIVE`, `HOMEPATH` and `TEMP` stay real and reach the child. That is harmless with FakeClaude, but a real CLI run under `ac-test.exe` would see your real `%APPDATA%`.
- Its refusal of your own state and profile folders compares normalised path text only. A junction, short name or `\\?\` spelling of the same folder would pass.
- With `realToasts` on, it registers its own notification activator for your user. A click starts `ac-test.exe` with your real environment, so the config must sit beside the exe for the click to be handled.
- AC6 times the 10 s from when the test sees the hook row, up to about 100 ms after it was written, because the row's time is stored in whole seconds.

## Daemon and commands

- The daemon handles one request at a time and waits through each launch's pre-flight before the next. A second command sent meanwhile can pass its 3 s wait, withdraw and print `daemon not running` although the daemon is up. It fails safe, but the message is wrong. Handling requests in parallel up to the reply, or a daemon heartbeat the client can read, would fix it.
- Request rows can linger. A request taken by a daemon that then crashed is deleted only at a later start if it is under 60 s old at the first one. A reply the client stopped waiting for (`no reply from daemon`) stays as a DONE row.
- Revoke cancels a running launch, which kills its job once. If that kill fails, nothing retries it or closes the job, and if it throws, `ac revoke` reports an error although the task is already revoked.
- When a failed run's background work outlasts its 30 s settle wait, the daemon still disposes that launch's database connection, and the limit watcher can then fail on it.
- A start sent straight after a refused one can be refused with `ADMISSION_STATE`, because the daemon removes the finished launch from its table a moment after replying.
- The notification click wiring in `Program.cs` has no automated test. It is part of the Task 15 hand check.
