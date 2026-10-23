# E1 probe findings

Oct 20, 2026

## Conclusion

Claude Code 2.1.304 reports enough to verify a subscription route without reading any secret. `claude auth status` and the stream's `system/init` event both tell a claude.ai login apart from an API key. In the negative control, an invalid key changed both. The stream's `rate_limit_event` gives the reset time, window utilisation and live overage status, and it carries no tool content.

Two questions are still pending until the limit capture runs near a real limit: whether a StopFailure hook fires at a usage-limit stop and with which fields, and whether the headless process exits on its own at the limit (I5). Until then, Release 1 cannot verify a limit signal and treats every limit-like stop as AMBIGUOUS.

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
| StopFailure at a limit stop | Pending | limit capture |
| Headless process exits at the limit (I5) | Pending | limit capture |

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
| Limit signal | Empty until the limit capture. Every limit-like stop is AMBIGUOUS |

The rules entry is `src/AgentContinuum.Core/Rules/claude-code.json`.
