# Known Issues

Open Low items from the launch coordinator reviews. Each is accepted for now and should be revisited before Release 1 ships.

## Launch coordinator

- Some limit tests do not pin whether the limit kill happened. `UsageLimit_SchedulesReset` and `AttemptsExhausted_NeedsAttention` pass whether or not the run was killed.
- A daemon crash during the empty-job wait after exit leaves the op EXITED. Recovery then applies the outcome without taking a fresh baseline, so the next pre-flight compares against a stale one.
- A launch that fails to start goes to NEEDS_ATTENTION with "Launch failed". R13 recovery of an INTENT op with no process goes to WAITING_FOR_RESET and offers again. The two paths handle the same point differently.
- `BeginIntent` reports every refusal as an `InvalidOperationException`, and the coordinator reports all of them as `ADMISSION_STATE`. A task that runs out of attempts between pre-flight and counting is not flagged as "Attempts exhausted".
- The launch args are built from the task read before `BeginIntent`. A change to the permission mode or allowed tools in between would launch with the old values. Nothing updates those columns after a task is added today.
- If the empty-job wait itself fails (the job query throws) while a grandchild holds stdout open, the stream read can still hang. Only a wait that completes and reports a non-empty job closes the job.
- `--allowedTools` now passes one argument per rule. How 2.1.304 parses the values has not been probed, and a rule that starts with `-` would be read as a flag.

## Windows process host

- `JobProcessHost.Start` can leave the process running outside supervision if the `JobHostedRun` constructor throws after the process has joined its job and resumed.
