# E1 probe findings

Oct 20, 2026

## Conclusion

Claude Code 2.1.304 reports enough to verify a subscription route without reading any secret. `claude auth status` and the stream's `system/init` event both tell a claude.ai login apart from an API key. In the negative control, an invalid key changed both. The stream's `rate_limit_event` gives the reset time, window utilisation and live overage status, and it carries no tool content.

The limit capture on 2.1.309 (Oct 23, 2026) settled the two open questions. No StopFailure hook fires at a real usage limit. The limit is signalled in the stream, and the headless process exits on its own (I5). Release 1 verifies a limit on 2.1.309 from the stream and treats every limit-like stop on 2.1.304 as AMBIGUOUS. See the 2.1.309 section.

## Environment

- Claude Code 2.1.304, native `claude.exe` at `C:\Users\ultra\.local\bin\claude.exe`, not a link. SHA-256 `019E5174D5D954585EDB1A946FE280E44A973050B4897034ADF0E16306F85FDD`.
- Windows 11 Home 26H2, build 26300.9711. Logged in with a claude.ai Pro subscription.
- Evidence: `probes/e1/results/static.json` (no quota) and `probes/e1/results/init.json` (two one-turn runs). Both record key names, event types and enumerated values only.

## Results

| Question | Answer | Evidence |
| --- | --- | --- |
| P3 stored credential type | Confirmed for a subscription login: `.claude/.credentials.json` holds `claudeAiOauth` with `accessToken` and `subscriptionType`, and `.claude.json` has `oauthAccount` and no `primaryApiKey`. A stored API-key login was not tested; an API key in the environment is caught by P1, P2, P4 and P5. | static |
| P4 auth status command | Confirmed. `claude auth status` prints JSON by default. Subscription: `loggedIn: true`, `authMethod: "claude.ai"`, `apiProvider: "firstParty"`, `subscriptionType: "pro"`. With the dummy key: `authMethod: "api_key"`, `apiKeySource: "ANTHROPIC_API_KEY"`, no `subscriptionType`. | static, init |
| P5 auth source in the stream | Confirmed. The `system/init` event's `apiKeySource` is `"none"` on the subscription and `"ANTHROPIC_API_KEY"` with the dummy key. `init` is not the first line: SessionStart hook events come before it. | init |
| Negative control | Passed. The invalid key failed with 401 (`api_error_status: 401`, `terminal_reason: "api_error"`), `total_cost_usd: 0`. | init |
| P6 overage | Visible live. `rate_limit_event.rate_limit_info` has `overageStatus: "rejected"`, `isUsingOverage: false`, `overageDisabledReason: "org_level_disabled"`. | init |
| R4 reset time | Confirmed for a run that is not at the limit. `rate_limit_info.resetsAt` is a Unix epoch in seconds, with per-window `unifiedWindows.five_hour` and `seven_day` `resetsAt` and `utilization`. Whether the same event appears at a limit stop is pending. | init |
| Usage source with no tool content | Exists. `rate_limit_event` holds only status, window, reset, utilisation and overage fields. | init |
| Process exits after the result | Yes for a normal and an error run: 0.8 s and 1.1 s after the result event. At the limit: pending. | init |
| StopFailure at a limit stop | Does not fire on 2.1.309. Only SessionEnd fired, with `reason: "other"`. The limit is in the stream | limit-2.1.309 |
| Headless process exits at the limit (I5) | Yes on 2.1.309, exit code 1, 2.7 s after the result event | limit-2.1.309 |

## Facts the build must respect

- `subtype` is `"success"` on the 401 run. Success means `is_error: false` and `terminal_reason: "completed"`, never `subtype` alone.
- `total_cost_usd` is reported on a subscription run too ($0.098 for one turn). It is not a billing signal.
- The P5 check must find the `system/init` event, not take the first line.
- Managed runs load your user settings, plugins, hooks and claude.ai MCP connectors. The subscription run showed `permissionMode: "auto"` from your settings, four connected MCP servers, and a 3,604-character SessionStart hook output in the stream. The event filter drops that output. Whether Release 1 should limit setting sources (`--setting-sources`) for managed runs is an open item.
- `auto` is a permission mode in 2.1.304. It is not in the spec's allowed list, and launches pass `--permission-mode` explicitly.

## What Release 1 can verify on 2.1.304

| Proof or signal | Status |
| --- | --- |
| P1 constructed environment | Implementable; no CLI fact needed |
| P2 settings scan | Implementable; no extra keys found |
| P3 | Rule entry filled for the subscription side |
| P4 | Rule entry filled |
| P5 | Rule entry filled (`system/init`, `apiKeySource` = `none`) |
| P6 | Attestation as specified. The live overage fields are recorded in the rules for a later live check |
| Run success | Rule entry filled (`is_error` false and `terminal_reason` completed) |
| Reset time | Rule entry filled from `rate_limit_event` |
| Limit signal | Empty on 2.1.304, where every limit-like stop is AMBIGUOUS. Filled on 2.1.309 from the stream |

The rules entry is `src/AgentContinuum.Core/Rules/claude-code.json`.

## Claude Code 2.1.309

Oct 23, 2026. The static and init captures were rerun on 2.1.309, at the same path, SHA-256 `5441781F2B6BBD07354B4A74560EE88C68C9BF05D6DB3C7E24C452C91673DC53`. Evidence: `probes/e1/results/static-2.1.309.json` and `init-2.1.309.json`.

Every field the rules read matches 2.1.304. That covers the auth status output for both runs, `apiKeySource` on `system/init`, the `rate_limit_event` fields, the result fields for both runs, the settings key names and the Credential Manager facts. The only differences are values that change from run to run (timestamps, utilisation, IDs) and one new credential key, `claudeAiOauth.subscriptionTypeSource`, which the rules do not read.

2.1.309 has its own rules entry, `>=2.1.309 <2.1.310`, with the 2.1.304 fields. Versions 2.1.305 to 2.1.308 were not captured and stay outside every range. The limit signal comes from the real limit capture below.

### Real usage limit

Oct 23, 2026. One tiny headless run at a natural five-hour limit on 2.1.309, with overage off and a fixed `--session-id`. Evidence: `probes/e1/results/limit-2.1.309.json`. It records key names, event types and enumerated values only. Text, paths and hook output are redacted to their lengths.

| Question | Answer |
| --- | --- |
| Limit marker | The `result` event has `is_error: true`, `terminal_reason: "api_error"`, `api_error: "usage_limit_reached"` and `api_error_status: 429`. `subtype` is still `"success"`. The `assistant` event before it carries the same `api_error` fields with `error: "rate_limit"`. |
| Rate limit state | A `rate_limit_event` comes before the result with `rate_limit_info.status: "rejected"`, `rateLimitType: "five_hour"`, `unifiedWindows.five_hour.utilization: 1` and `overageStatus: "rejected"`. |
| Reset time | `rate_limit_info.resetsAt` and `unifiedWindows.five_hour.resetsAt`, both Unix seconds (1792769400). The result event has no reset field. |
| Session ID | Every stream event and the SessionEnd hook carry the session ID given with `--session-id`. |
| Hooks | StopFailure, Stop and SessionEnd were registered. Only SessionEnd fired, with `reason: "other"`, which carries no limit or reset information and is not specific to a limit. |
| Process exit | On its own, exit code 1, 2.7 s after the result event. |

The limit is keyed on the stream, not on a hook. A hook-keyed rule could only use SessionEnd, and `reason: "other"` would read ordinary stops as limits. The 2.1.309 limit signal is verified only when both stream events match in the run's own session: the `result` with `api_error` `usage_limit_reached` and `api_error_status` 429, and a `rate_limit_event` with `status` `rejected`. The reset comes from the last rejected `rate_limit_event`'s `resetsAt`, in Unix seconds. A stop that matches only one of the two is AMBIGUOUS.

Launches no longer install a StopFailure hook or a settings file. Since the process exited on its own, the 10 s limit kill is a backstop for a run that does not and for any child left in the job.

Not yet captured: a limit on a resumed run (`--resume`). Matching assumes the resumed run reports the same session ID. If it reported another, the stop would be AMBIGUOUS, which is safe but unverified. The seven-day window at its limit was not seen either; this capture hit the five-hour window.
