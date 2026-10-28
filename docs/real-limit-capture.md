# Real limit capture

Do this once, the next time you hit a Claude Code usage limit in normal use. It records how Claude Code signals the limit, so `ac.exe` can detect it for real.

## 1. Know you are at a limit

Claude Code tells you that you have hit your usage limit and shows when it resets. To check, type `/usage` in Claude Code: the 5-hour or weekly bar is full.

Run the capture before the reset time.

## 2. Run one command

Open a new PowerShell window and run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\dev\agentcontinuum\probes\e1\capture-real-limit.ps1
```

It sends one request asking for the word OK and waits for Claude Code to finish, up to 5 minutes.

- If it prints `Not at a limit`, nothing was saved and almost no quota was used. Try again next time you hit a limit.
- If you have turned on extra usage (paid overage), turn it off first. Otherwise the request goes through on paid usage and nothing is captured.

## 3. Paste back

Copy everything between `----- copy from here -----` and `----- to here -----`, and paste it to Claude Code with:

> Real limit captured. Add the limit signal to the rules.

The full capture is also saved as `probes\e1\results\limit-<version>.json`. Leave it uncommitted.

If the version on the first line is not 2.1.309, still paste it. That version needs its own static and init capture before it gets a rules entry.

## What happens next

The summary shows which hook fired, its reason value, the reset field and its format, and whether Claude Code exited on its own. The capture on 2026-10-23 showed that no StopFailure hook fires at a real limit and that the signal is in the stream instead. The 2.1.309 entry in `src/AgentContinuum.Core/Rules/claude-code.json` now reads it from there. See `docs/e1-findings.md`.
