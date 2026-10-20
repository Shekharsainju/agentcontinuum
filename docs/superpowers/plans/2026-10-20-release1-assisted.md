# Release 1 Assisted Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `ac`, a Windows controller that runs Claude Code headless on a task, detects a usage-limit stop, waits for the reset, and offers a Resume button that launches only after a full pre-flight with a verified subscription route.

**Architecture:** One daemon process in your interactive session owns all launches, each in a kill-on-close job. Commands and notification clicks reach it through a `requests` table in SQLite. Pure logic (launch ledger, recovery, event filter, classification, scheduling, pre-flight, billing proofs) lives in `AgentContinuum.Core` with unit tests. Windows-only code sits behind interfaces in `AgentContinuum.Windows`. Claude Code facts come from version rules filled in by the E1 probe.

**Tech Stack:** .NET 8 (C#), `Microsoft.Data.Sqlite`, `Microsoft.Toolkit.Uwp.Notifications`, xUnit. E1 probe: Windows PowerShell 5.1 with Pester 3.4.0 (`Should Be` syntax).

**Spec:** `docs/superpowers/specs/2026-10-20-release1-assisted-design.md`. Also read `docs/security-recovery-architecture.md` (R13 launch record and recovery table, R15 proofs, E0b gates).

## Global Constraints

- Agent runs under your own account. The controller runs interactively, never in Session 0. No I4 claim in code, docs or output.
- No launch without a request from you. A crash never leads to a relaunch.
- Never pass `--dangerously-skip-permissions` or `bypassPermissions`. Never send keystrokes or menu answers.
- No prompt text, model output, tool input or tool output in the database, logs or notifications. Only `label` is free text.
- Every launch: start suspended, assign to a kill-on-close job with breakaway disallowed, then resume. Kill through the job; record every kill result.
- State: `%LOCALAPPDATA%\AgentContinuum\state\ac.db`, WAL, `synchronous=FULL`. Launch files: `%LOCALAPPDATA%\AgentContinuum\launch\<op_id>\`.
- Values: reset margin 2 min; clock poll 30 s; clock jump threshold 120 s; request poll 1 s; kill after limit signal 10 s; P6 attestation 30 days; filter line cap 1 MB, value cap 200 chars; fingerprint limit 50,000 files or 2 GB.
- Tests never call the real Claude Code; they use `FakeClaude`. The builder never runs `Invoke-E1InitCapture.ps1` or `Invoke-E1LimitCapture.ps1`.
- Commits per `CLAUDE.md`: per behaviour, a failing-test commit, then the commit that makes it pass. Shared helpers first in their own commits. No AI attribution. Show `git log --oneline` at each stop point.
- Branch `release1` off `master`.

## Review Focus

1. **Prompt file missing, empty or outside the worktree:** pre-flight fails with PROMPT_INVALID and no attempt is counted. Test in Task 10.
2. **`ac` command with no daemon running:** exits non-zero within 5 s saying the daemon is not running. Requests older than 60 s are discarded, so a daemon started later never acts on a stale Resume. Test in Task 14.
3. **Second daemon started:** refuses to start and leaves the first untouched. Test in Task 14.
4. **Spaces and non-ASCII characters in worktree and file names:** fingerprint is stable across runs. Test in Task 10.
5. **Machine sleeps across the reset:** on wake, exactly one offer is made. Test in Task 8.

---

### Task 1: E1 detection probe

**Files:**
- Create: `probes/e1/E1Common.psm1`, `probes/e1/Get-E1StaticFacts.ps1`, `probes/e1/Invoke-E1InitCapture.ps1`, `probes/e1/Invoke-E1LimitCapture.ps1`, `probes/e1/Write-E1HookRecord.ps1`
- Test: `probes/e1/tests/E1Common.Tests.ps1`

**Interfaces:**
- Produces:
  - `Protect-E1Value -Value <object> -> object`: returns `$null`, booleans, numbers and ISO-8601 timestamps as they are. Strings of 40 characters or fewer matching `^[A-Za-z0-9_.:-]+$` with no `@` are kept as they are. Any other string becomes `@{ redacted = $true; length = <n> }`.
  - `ConvertTo-E1Shape -Json <string> -> hashtable`: key paths with `Protect-E1Value` applied to each leaf. Arrays keep their first element's shape and count only.
  - `Write-E1Result -Name <string> -Data <hashtable>`: writes `probes/e1/results/<name>.json`, schema `ac-probe-e1/1`, with `cli_version` and `cli_path`.
  - Static facts (no quota): CLI version; resolved path and link target; `claude --help` subcommand names; for each auth status candidate (`claude auth status`, `claude auth status --json`), the exit code and output shape; key names only in `%USERPROFILE%\.claude\.credentials.json`; presence only of `primaryApiKey`, `oauthAccount` and `customApiKeyResponses` in `%USERPROFILE%\.claude.json`; Credential Manager target names containing `claude`, from `cmdkey /list`.
  - Init capture (run by you): two runs of `claude -p --output-format stream-json --verbose --max-turns 1` in an empty temp folder with the prompt `Reply with the single word OK.` One runs normally. The other runs with `ANTHROPIC_API_KEY=sk-ant-e1-invalid-probe` and repeats the auth status command. Each records the shape of every event and the auth status output.
  - Limit capture (run by you): refuses to start until you type `overage is off`. It writes a temp settings file registering `Write-E1HookRecord.ps1` for `StopFailure`, `Stop` and `SessionEnd`, then repeats a one-turn run with a fixed `--session-id` until a run ends without success, up to 200 runs. It records each hook payload's shape and whether the hook's `session_id` equals the expected value, the last event's shape, the exit code, and seconds from the last event to process exit (killed at 120 s and recorded as `did_not_exit`).

- [ ] **Step 1: Write failing tests**

```powershell
Describe 'Protect-E1Value' {
  It 'keeps enumerated values'      { Protect-E1Value 'none' | Should Be 'none' }
  It 'keeps timestamps'             { Protect-E1Value '2026-10-20T05:00:00Z' | Should Be '2026-10-20T05:00:00Z' }
  It 'redacts emails'               { (Protect-E1Value 'me@example.com').redacted | Should Be $true }
  It 'redacts tokens by length'     { (Protect-E1Value ('sk-ant-' + ('x' * 60))).length | Should Be 67 }
  It 'redacts free text with spaces' { (Protect-E1Value 'Reply with OK').redacted | Should Be $true }
}
Describe 'ConvertTo-E1Shape' {
  It 'never emits a sentinel from message content' {
    $j = '{"type":"assistant","message":{"content":[{"text":"SENTINEL-7f3a in a sentence"}]}}'
    (ConvertTo-E1Shape $j | ConvertTo-Json -Depth 8) | Should Not Match 'SENTINEL-7f3a'
  }
  It 'keeps key paths' { (ConvertTo-E1Shape '{"a":{"b":1}}').Keys -contains 'a.b' | Should Be $true }
}
```

- [ ] **Step 2: Run, expect FAIL.** `powershell -NoProfile -ExecutionPolicy Bypass -Command "Invoke-Pester -Script probes/e1/tests -EnableExit"`. Commit the tests.
- [ ] **Step 3: Implement `E1Common.psm1` and the four scripts.** The scripts are thin: every value they write goes through `ConvertTo-E1Shape` or `Protect-E1Value`.
- [ ] **Step 4: Run, expect PASS.** Run `Get-E1StaticFacts.ps1` once. Check that `probes/e1/results/static.json` contains no email address and no value longer than 40 characters. Commit.
- [ ] **Step 5: STOP.** Show `git log --oneline`. You run `Invoke-E1InitCapture.ps1`, and `Invoke-E1LimitCapture.ps1` when you are near a limit. Tasks 3 to 13 do not depend on the results and may continue meanwhile.

---

### Task 2: E1 findings and the 2.1.304 rules entry

Runs once you have committed the capture results.

**Files:**
- Create: `docs/e1-findings.md`, `src/AgentContinuum.Core/Rules/claude-code.json` (if Task 6 has not created it yet)
- Modify: `docs/security-recovery-architecture.md` (tick or annotate the P3, P4 and P5 items under "Blocked on")

**Interfaces:**
- Produces: a `claude-code.json` entry `{ "versions": ">=2.1.304 <2.1.305", ... }` in the Task 6 schema. A field the captures did not confirm stays `null`.

- [ ] **Step 1:** Write the findings in the style of `docs/e0a-findings.md`: environment, one row per question with the evidence file, and what Release 1 can and cannot verify.
- [ ] **Step 2:** Fill the rules entry from the findings only. P4 and P5 values count as subscription only if the negative-control run gave a different value.
- [ ] **Step 3:** Run `dotnet test --filter FullyQualifiedName~VersionRulesTests`. Expect PASS: the embedded rules parse. Commit.

---

### Task 3: Solution and state store

**Files:**
- Create: `AgentContinuum.sln`, `src/AgentContinuum.Core/AgentContinuum.Core.csproj` (`net8.0`), `src/AgentContinuum.Windows/AgentContinuum.Windows.csproj` and `src/AgentContinuum.Cli/AgentContinuum.Cli.csproj` (`net8.0-windows10.0.19041.0`, `AssemblyName` `ac`), `tests/AgentContinuum.Core.Tests/`, `src/AgentContinuum.Core/State/StateStore.cs`, `.gitignore` (`bin/`, `obj/`)
- Test: `tests/AgentContinuum.Core.Tests/State/StateStoreTests.cs`

**Interfaces:**
- Produces:
  - `sealed class StateStore : IDisposable { static StateStore Open(string dbPath); SqliteConnection Connection { get; } int SchemaVersion { get; } bool IntegrityOk(); T InTransaction<T>(Func<SqliteTransaction, T> work); }`
  - Schema version 1: tables `tasks`, `launch_ops` (columns from the architecture doc's launch record), `events` (`op_id`, `kind`, `fields_json`, `at_utc`), `requests` (`id`, `kind`, `task_id`, `args_json`, `created_utc`, `status`, `result_json`), `attestations` (`kind`, `at_utc`), `meta` (`key`, `value`).
  - Partial unique index: `CREATE UNIQUE INDEX one_live_op ON launch_ops(task_id) WHERE state IN ('INTENT','SPAWNED','EXITED')`.

- [ ] **Step 0:** .NET 8 SDK is not installed. Ask your human partner to run `winget install Microsoft.DotNet.SDK.8`, then confirm with `dotnet --list-sdks`.
- [ ] **Step 1: Write failing tests:** `Open_SetsWalAndSynchronousFull` (`PRAGMA journal_mode` is `wal`, `PRAGMA synchronous` is `2`); `Open_CreatesSchemaVersion1`; `PartialIndex_RejectsSecondLiveOpForTask` (second INTENT insert for the same task throws `SqliteException`); `PartialIndex_AllowsNewOpAfterClassified`; `IntegrityOk_TrueOnFreshDb`.
- [ ] **Step 2:** `dotnet test tests/AgentContinuum.Core.Tests --filter FullyQualifiedName~StateStoreTests`. Expect FAIL. Commit.
- [ ] **Step 3:** Implement `StateStore`. Schema creation runs in one transaction; `meta.schema_version` is `1`.
- [ ] **Step 4:** Same command. Expect PASS. Commit.

---

### Task 4: Launch ledger (R13)

**Files:**
- Create: `src/AgentContinuum.Core/State/LaunchLedger.cs`, `src/AgentContinuum.Core/State/TaskRepository.cs`, `src/AgentContinuum.Core/Model/*.cs`
- Test: `tests/AgentContinuum.Core.Tests/State/LaunchLedgerTests.cs`, `TaskRepositoryTests.cs`

**Interfaces:**
- Consumes: `StateStore`.
- Produces:
  - `enum TaskState { Queued, Running, WaitingForReset, ResumeOffered, NeedsAttention, Completed, Revoked }`
  - `enum OpState { Intent, Spawned, Exited, Classified, Abandoned, Interrupted }`
  - `enum Outcome { Completed, UsageLimit, Failed, Ambiguous, BillingMismatch }`
  - `record TaskRecord(string TaskId, string Label, string Worktree, string PromptFile, string PermissionMode, string[] AllowedTools, int MaxAttempts, DateTimeOffset DeadlineUtc, Guid SessionId, string CliPath, string CliSha256, string CliVersion, TaskState State, int AttemptsUsed, long? ResetExpectedAtEpoch, long? AttemptPermittedAtEpoch, string? BaselineFingerprint, string? AttentionReason)`
  - `TaskRepository`: `Add(TaskRecord)`, `Get(string)`, `List()`, `SetState(string taskId, TaskState state, string? reason)`, `SetSchedule(string taskId, long? resetEpoch, long? permittedEpoch)`, `SetBaseline(string taskId, string fingerprint)`, `SetCliPin(string taskId, string path, string sha256, string version)`.
  - `LaunchLedger`: `string BeginIntent(string taskId)` (one transaction: insert INTENT with `window_id` set to the task's count of prior USAGE_LIMIT outcomes, increment `tasks.attempts_used`, set task RUNNING; returns `op_id`); `MarkSpawned(string opId, int pid, long createdAtFileTime, Guid sessionId)`; `MarkExited(string opId, int exitCode)`; `Classify(string opId, Outcome outcome, string reason)`; `Abandon(string opId)`; `Interrupt(string opId)`; `LaunchOp? Live(string taskId)`. An illegal transition throws `InvalidOperationException`.

- [ ] **Step 1: Write failing tests:** `BeginIntent_CountsAttemptInSameTransaction` (a fault injected after the insert leaves `attempts_used` unchanged and no row); `BeginIntent_SecondLiveIntentThrows`; `Transitions_FollowStateMachine` (theory over every legal pair); `Transitions_IllegalPairThrows` (for example `Classified -> Spawned`); `Classify_FreesTaskForNextIntent`.
- [ ] **Step 2:** Run with `--filter FullyQualifiedName~LaunchLedgerTests|FullyQualifiedName~TaskRepositoryTests`. Expect FAIL. Commit.
- [ ] **Step 3:** Implement. Legal transitions: Intent to Spawned, Abandoned or Interrupted; Spawned to Exited or Interrupted; Exited to Classified.
- [ ] **Step 4:** Expect PASS. Commit.

---

### Task 5: Recovery on daemon start

**Files:**
- Create: `src/AgentContinuum.Core/Recovery/RecoveryService.cs`, `src/AgentContinuum.Core/Platform/IProcessLister.cs`
- Test: `tests/AgentContinuum.Core.Tests/Recovery/RecoveryServiceTests.cs`

**Interfaces:**
- Consumes: `LaunchLedger`, `TaskRepository`.
- Produces:
  - `interface IProcessLister { IReadOnlyList<ProcessInfo> ListOwnProcesses(); }` with `record ProcessInfo(int Pid, long CreatedAtFileTime, string CommandLine)`. It throws `ProcessListException` when enumeration fails.
  - `RecoveryService.Reconcile()`: applies the architecture doc's table per live op.

- [ ] **Step 1: Write failing tests**, one per table row: `Intent_NoProcess_Abandoned_TaskWaiting` (op ABANDONED, attempt still counted, task WAITING_FOR_RESET); `Intent_ProcessWithSessionId_Interrupted_NeedsAttention` (reason "Interrupted launch"); `Spawned_Interrupted_NeedsAttention` (reason "Interrupted run"); `Exited_ClassifiedFromRecordedEvents`; `ListFails_NeedsAttention` (lister throws, task NEEDS_ATTENTION, no op state guessed); `Reconcile_NeverLaunches` (fake launcher records zero calls).
- [ ] **Step 2:** Expect FAIL. Commit.
- [ ] **Step 3:** Implement. A recovered op never re-enters INTENT.
- [ ] **Step 4:** Expect PASS. Commit.

---

### Task 6: Version rules and event filter

**Files:**
- Create: `src/AgentContinuum.Core/Rules/VersionRules.cs`, `src/AgentContinuum.Core/Rules/claude-code.json` (embedded resource), `src/AgentContinuum.Core/Events/EventFilter.cs`
- Test: `tests/AgentContinuum.Core.Tests/Rules/VersionRulesTests.cs`, `tests/AgentContinuum.Core.Tests/Events/EventFilterTests.cs`

**Interfaces:**
- Produces:
  - `record ClaudeRules(string Versions, string[] P2ExtraKeys, CredentialRule? P3, AuthStatusRule? P4, FirstEventRule? P5, LimitSignalRule? Limit)` with `record CredentialRule(string File, string SubscriptionKey, string ApiKeyKey)`, `record AuthStatusRule(string[] Args, string JsonPath, string[] SubscriptionValues)`, `record FirstEventRule(string Type, string Subtype, string Field, string[] SubscriptionValues)` and `record LimitSignalRule(string Hook, string ReasonField, string[] ReasonValues, string? ResetField, string? ResetFormat)`.
  - `static ClaudeRules? VersionRules.For(string cliVersion)`: `null` when no range matches. `VersionRules.LoadEmbedded()`; `VersionRules.Load(string json)` for tests.
  - `sealed class EventFilter { EventFilter(ClaudeRules rules); FilteredEvent? Accept(string line); }` with `record FilteredEvent(string Type, string? Subtype, string? SessionId, bool? IsError, string? AuthSource, IReadOnlyDictionary<string,string> LimitFields)`.
  - Until Task 2 lands, the embedded file holds `[]`.

- [ ] **Step 1: Write failing tests:** `For_UnknownVersion_ReturnsNull`; `For_MatchesRange`; `Accept_KeepsOnlyAllowlistedFields`; `Accept_DropsSentinelEverywhere` (a line whose content fields hold `SENTINEL-c0ffee` gives a `FilteredEvent` whose serialised form lacks it); `Accept_UnparseableLine_ReturnsNull`; `Accept_LineOver1MB_ReturnsNull`; `Accept_CapsValuesAt200Chars`.
- [ ] **Step 2:** Expect FAIL. Commit.
- [ ] **Step 3:** Implement. Version ranges support `>=a <b` only. The filter uses `Utf8JsonReader` and never materialises unlisted fields as strings.
- [ ] **Step 4:** Expect PASS. Commit.

---

### Task 7: Run classifier and hook recorder

**Files:**
- Create: `src/AgentContinuum.Core/Classify/RunClassifier.cs`, `src/AgentContinuum.Core/Events/HookRecorder.cs`
- Test: `tests/AgentContinuum.Core.Tests/Classify/RunClassifierTests.cs`, `tests/AgentContinuum.Core.Tests/Events/HookRecorderTests.cs`

**Interfaces:**
- Consumes: `ClaudeRules`, `FilteredEvent`, `StateStore`.
- Produces:
  - `HookRecorder.Record(StateStore store, string opId, Stream stdin, ClaudeRules? rules)`: reads at most 64 KB, keeps `hook_event_name`, `session_id` and the rule's reason and reset fields, and writes one `events` row of kind `hook`. Anything else is dropped. It never throws to the caller: on failure it writes a `hook_error` event with no payload.
  - `record HookEvent(string HookName, string? SessionId, IReadOnlyDictionary<string,string> Fields)`, read back with `HookRecorder.ForOp(StateStore store, string opId) -> IReadOnlyList<HookEvent>`.
  - `record Classification(Outcome Outcome, string Reason, long? ResetEpoch)`.
  - `RunClassifier.Classify(ClaudeRules? rules, Guid sessionId, IReadOnlyList<FilteredEvent> stream, IReadOnlyList<HookEvent> hooks, int exitCode) -> Classification`: implements the spec's table. A limit signal counts as verified only when the rule exists, the reason value matches and `session_id` equals `sessionId`. `ResetEpoch` comes from the rule's reset field and format.

- [ ] **Step 1: Write failing tests:** `SuccessResult_Completed`; `VerifiedLimit_UsageLimitWithResetEpoch`; `LimitWithWrongSessionId_Ambiguous`; `LimitWithNoRule_Ambiguous`; `NoResultEvent_Failed`; `ErrorResult_Failed`; `HookRecorder_DropsSentinelPayload` (sentinel in `transcript_path` and in an unlisted field never reaches the database); `HookRecorder_OversizedInput_RecordsHookErrorOnly`.
- [ ] **Step 2:** Expect FAIL. Commit.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Expect PASS. Commit.

---

### Task 8: Reset scheduler and clock

**Files:**
- Create: `src/AgentContinuum.Core/Schedule/ResetScheduler.cs`, `src/AgentContinuum.Core/Platform/IClock.cs`
- Test: `tests/AgentContinuum.Core.Tests/Schedule/ResetSchedulerTests.cs`

**Interfaces:**
- Produces:
  - `interface IClock { DateTimeOffset UtcNow { get; } long MonotonicMs { get; } }`; `FakeClock` in tests.
  - `ResetScheduler.OnUsageLimit(string taskId, long? resetEpoch)`: sets RESET_EXPECTED_AT and ATTEMPT_PERMITTED = reset + 120 s, or permitted = now when reset is unknown; task WAITING_FOR_RESET.
  - `IReadOnlyList<Offer> ResetScheduler.Tick()`: called every 30 s. Returns offers for tasks that crossed ATTEMPT_PERMITTED and moves them to RESUME_OFFERED. Detects a wall-clock jump over 120 s against the monotonic clock, logs it and recomputes. `record Offer(string TaskId, string Label, bool ResetTimeUnknown)`.

- [ ] **Step 1: Write failing tests:** `Permitted_IsResetPlus2Min`; `UnknownReset_OffersAtOnceMarkedUnknown`; `Tick_BeforePermitted_NoOffer`; `Tick_AfterPermitted_ExactlyOneOffer` (two further ticks give none); `ClockJumpForward2h_AtMostOneOffer` (AC15); `ClockJumpBack2h_NoOfferUntilPermitted` (AC15); `SleepAcrossReset_OneOfferOnWake` (wall and monotonic both advance 6 h in one tick; Review Focus 5).
- [ ] **Step 2:** Expect FAIL. Commit.
- [ ] **Step 3:** Implement. The move to RESUME_OFFERED is a guarded update (`WHERE state = 'WaitingForReset'`), so an offer is made at most once.
- [ ] **Step 4:** Expect PASS. Commit.

---

### Task 9: Billing proofs (R15)

**Files:**
- Create: `src/AgentContinuum.Core/Billing/ChildEnvironment.cs`, `SettingsScanner.cs`, `BillingProofs.cs`, `src/AgentContinuum.Core/Platform/ICommandRunner.cs`
- Test: `tests/AgentContinuum.Core.Tests/Billing/*Tests.cs`

**Interfaces:**
- Consumes: `ClaudeRules`, `IClock`, `StateStore` (attestations).
- Produces:
  - `ChildEnvironment.Build(IReadOnlyDictionary<string,string> parent, string cliDir, string gitDir) -> IReadOnlyDictionary<string,string>`: only the spec's allowlist, with `PATH` constructed (P1).
  - `SettingsScanner.Scan(string userProfile, string worktree, string? managedPath, ClaudeRules rules) -> ProofResult`: files `%ProgramData%\ClaudeCode\managed-settings.json`, `%USERPROFILE%\.claude\settings.json`, `%USERPROFILE%\.claude\settings.local.json`, `<worktree>\.claude\settings.json` and `<worktree>\.claude\settings.local.json`. A missing file passes; an unparseable one fails.
  - `interface ICommandRunner { CommandResult Run(string exe, string[] args, IReadOnlyDictionary<string,string> env, TimeSpan timeout); }`.
  - `record BillingContext(TaskRecord Task, ClaudeRules? Rules, IReadOnlyDictionary<string,string> ChildEnv, string UserProfile, string? ManagedSettingsPath)`.
  - `BillingProofs.Evaluate(BillingContext ctx) -> BillingVerdict`, where `record BillingVerdict(bool Verified, IReadOnlyList<string> FailedProofs)` and proof IDs are `P1` to `P6`. A `null` rule for P3, P4 or P5 makes that proof fail as `Px_UNEVALUABLE`. P5 is evaluated at the first event by `FirstEventCheck.Matches(FilteredEvent, ClaudeRules) -> bool`, and pre-flight only requires the P5 rule to exist.
  - P6: an `attestations` row of kind `overage_off` newer than 30 days.

- [ ] **Step 1: Write failing tests:** `Env_DropsAnthropicVariables` (`ANTHROPIC_API_KEY`, `ANTHROPIC_BASE_URL` and `CLAUDE_CODE_USE_BEDROCK` absent from the result); `Env_ContainsOnlyAllowlist`; `Scan_ApiKeyHelperInUserSettings_Fails`; `Scan_EnvBaseUrlInProjectSettings_Fails`; `Scan_UnparseableFile_Fails`; `Scan_MissingFiles_Pass`; `P3_KeyNamesOnly` (credentials file whose values are `SENTINEL-p3`; the proof passes or fails on key names, and the serialised `ProofResult` lacks the sentinel); `P4_SubscriptionValue_Passes`; `P4_Timeout_Fails`; `NullRule_Unevaluable` (theory over P3, P4, P5); `P6_31DaysOld_Fails`; `EachProofAloneBlocks` (AC9, theory over P2 to P6).
- [ ] **Step 2:** Expect FAIL. Commit.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Expect PASS. Commit.

---

### Task 10: Workspace fingerprint and pre-flight

**Files:**
- Create: `src/AgentContinuum.Core/Workspace/WorkspaceFingerprint.cs`, `src/AgentContinuum.Core/Preflight/PreflightRunner.cs`, `src/AgentContinuum.Core/Preflight/Checks/*.cs`
- Test: `tests/AgentContinuum.Core.Tests/Workspace/WorkspaceFingerprintTests.cs`, `tests/AgentContinuum.Core.Tests/Preflight/*Tests.cs`

**Interfaces:**
- Consumes: `TaskRepository`, `LaunchLedger`, `IProcessLister`, `ICommandRunner`, `BillingProofs`, `StateStore`, `IClock`.
- Produces:
  - `WorkspaceFingerprint.Compute(string worktree, string gitExe) -> FingerprintResult`, where `record FingerprintResult(string? Hash, bool Incomplete, string? Error)`. It uses the exact git command from the spec and runs no other git command apart from `rev-parse HEAD`.
  - `record PreflightResult(bool Pass, IReadOnlyList<string> Reasons)`.
  - `PreflightRunner.Run(string taskId, bool now) -> PreflightResult`: runs every check and collects every reason. Codes are exactly the spec's.

- [ ] **Step 1: Write failing tests:** `Fingerprint_StableAcrossRuns`; `Fingerprint_ChangesOnEdit`; `Fingerprint_ChangesOnNewUntrackedFile`; `Fingerprint_IgnoresGitignored`; `Fingerprint_SpacesAndNonAscii_Stable` (worktree `C:\...\a b\` with file `ü ñ.txt`; Review Focus 4); `Fingerprint_RepoFsmonitorHookNotRun` (repo config sets `core.fsmonitor` to a script that writes a marker; marker absent); `Fingerprint_OverLimit_Incomplete`; `Preflight_CollectsAllReasons` (attempts exhausted and workspace changed both reported); `Preflight_PromptMissing_PromptInvalid_NoAttempt` (Review Focus 1); `Preflight_PromptOutsideWorktree_PromptInvalid`; `Preflight_CliHashChanged_CliChanged` (the message names `ac pin-cli`); `Preflight_ProcessWithSessionId_ProcessLive`; `Preflight_ListerThrows_ProcessUnknown`; `Preflight_BypassInProjectSettings_PermissionUnsafe`; `Preflight_NowFlag_WaivesEligibilityOnly`.
- [ ] **Step 2:** Expect FAIL. Commit.
- [ ] **Step 3:** Implement. Git tests create throwaway repos under the test temp folder.
- [ ] **Step 4:** Expect PASS. Commit.

---

### Task 11: FakeClaude

**Files:**
- Create: `tests/FakeClaude/FakeClaude.csproj` (`net8.0`, `AssemblyName` `claude`), `tests/FakeClaude/Program.cs`
- Test: `tests/AgentContinuum.Core.Tests/FakeClaudeTests.cs`

**Interfaces:**
- Produces: `claude.exe`, which prints `2.1.304 (Claude Code)` for `--version`. It accepts `-p`, `--session-id`, `--resume`, `--output-format`, `--verbose`, `--permission-mode`, `--allowedTools` and `--settings`. It reads `.fake-claude.json` in its working directory: `{ "authSource": "...", "events": [...], "hook": { "name": "...", "payload": {...} } | null, "exitCode": n, "lingerSeconds": n, "spawnChildren": n }`. It writes `fake-claude-args.json` with the arguments, environment variable names and stdin length it received, never stdin content. It runs the hook command from `--settings` with the payload on stdin.

- [ ] **Step 1: Write failing tests:** `Version_Prints2_1_304`; `EmitsScenarioEvents`; `RunsHookFromSettings`; `RecordsArgsWithoutStdinContent`.
- [ ] **Step 2:** Expect FAIL. Commit.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Expect PASS. Commit.

---

### Task 12: Windows process host and notifier

**Files:**
- Create: `src/AgentContinuum.Core/Platform/IProcessHost.cs`, `src/AgentContinuum.Core/Platform/INotifier.cs`, `src/AgentContinuum.Windows/JobProcessHost.cs`, `src/AgentContinuum.Windows/NativeMethods.cs`, `src/AgentContinuum.Windows/WmiProcessLister.cs`, `src/AgentContinuum.Windows/ToastNotifier.cs`
- Test: `tests/AgentContinuum.Windows.Tests/JobProcessHostTests.cs`, `WmiProcessListerTests.cs`

**Interfaces:**
- Produces:
  - `interface IProcessHost { IHostedRun Start(LaunchSpec spec); }`, with `record LaunchSpec(string Exe, string[] Args, string WorkingDir, IReadOnlyDictionary<string,string> Env, Stream Stdin)`.
  - `interface IHostedRun : IDisposable { int Pid { get; } long CreatedAtFileTime { get; } IAsyncEnumerable<string> StdoutLines { get; } Task<int> WaitForExit(); int ActiveProcesses(); KillResult Terminate(); }`, with `record KillResult(bool Ok, int Win32Error)`.
  - `JobProcessHost`: `CreateProcess` with `CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW`, then a job with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` and no breakaway flags, then `AssignProcessToJobObject`, then `ResumeThread`. If the assignment fails, it terminates the process by its handle and throws. It keeps the process handle. Stderr goes to `NUL`.
  - `WmiProcessLister : IProcessLister` (current user's processes, from `Win32_Process`).
  - `interface INotifier { void OfferResume(Offer offer); void Attention(string taskId, string label, string reasonCode); }`. `ToastNotifier` shows the spec's text, with buttons Resume and Dismiss carrying `action=resume&task=<id>`.

  - `JobProcessHost(Action<IntPtr>? beforeResume = null)`: the callback receives the process handle after assignment and before `ResumeThread`, for tests only.

- [ ] **Step 1: Write failing tests** (against FakeClaude): `Start_ChildInJobBeforeRunning` (`IsProcessInJob` is true inside `beforeResume`); `Terminate_KillsChildren` (`spawnChildren: 2`, then `ActiveProcesses() == 0` within 5 s); `DisposeHost_KillsOnClose`; `Terminate_ReturnsWin32Result`; `Env_IsExactlyLaunchSpecEnv` (FakeClaude's recorded variable names equal the spec keys); `Lister_SeesFakeClaudeWithSessionId`.
- [ ] **Step 2:** `dotnet test tests/AgentContinuum.Windows.Tests`. Expect FAIL. Commit.
- [ ] **Step 3:** Implement. `ToastNotifier` has no automated test; it is checked by hand in Task 15.
- [ ] **Step 4:** Expect PASS. Commit.

---

### Task 13: Launch coordinator

**Files:**
- Create: `src/AgentContinuum.Core/Launch/LaunchCoordinator.cs`
- Test: `tests/AgentContinuum.Core.Tests/Launch/LaunchCoordinatorTests.cs`

**Interfaces:**
- Consumes: `PreflightRunner`, `LaunchLedger`, `TaskRepository`, `IProcessHost`, `EventFilter`, `FirstEventCheck`, `RunClassifier`, `ResetScheduler`, `WorkspaceFingerprint`, `ChildEnvironment`, `INotifier`, `IClock`.
- Produces:
  - `Task<LaunchReport> LaunchCoordinator.Launch(string taskId, LaunchKind kind, bool now)`, with `enum LaunchKind { First, Resume }` and `record LaunchReport(bool Launched, PreflightResult Preflight, string? OpId, Outcome? Outcome)`.
  - Implements the spec's launch steps 1 to 7. The hook command in `settings.json` is `"<ac.exe path>" hook stop-failure --op <op_id>`. The resume prompt is the spec's fixed text. The baseline fingerprint is taken only after `ActiveProcesses() == 0`. A verified limit with the process still alive after 10 s calls `Terminate()`.

- [ ] **Step 1: Write failing tests** (fake host and fake clock): `PreflightFails_NoIntentNoAttempt`; `First_ArgsUseSessionId`; `Resume_ArgsUseResumeAndFixedPrompt`; `Args_NeverContainBypass` (theory over every permission mode); `FirstEventMismatch_TerminatesBillingMismatch` (AC10); `VerifiedLimit_StillAlive10s_Terminated` (AC6); `Baseline_NotTakenWhileProcessesActive` (AC13); `UsageLimit_SchedulesReset`; `AttemptsExhausted_NeedsAttention`; `NoContentInDbOrLog` (sentinel in the prompt file, the stream and the hook payload never appears in the database file or the log; I6).
- [ ] **Step 2:** Expect FAIL. Commit.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Expect PASS. Commit.

---

### Task 14: Daemon, requests and commands

**Files:**
- Create: `src/AgentContinuum.Core/State/RequestQueue.cs`, `src/AgentContinuum.Cli/Program.cs`, `src/AgentContinuum.Cli/Daemon.cs`, `src/AgentContinuum.Cli/Commands/*.cs`
- Test: `tests/AgentContinuum.Core.Tests/State/RequestQueueTests.cs`, `tests/AgentContinuum.Core.Tests/DaemonTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces:
  - `record Request(long Id, string Kind, string? TaskId, string ArgsJson, DateTimeOffset CreatedUtc)`.
  - `RequestQueue`: `long Submit(string kind, string? taskId, string argsJson)`, `Request? Next()`, `Complete(long id, string resultJson)`, `ExpireOlderThan(TimeSpan age)`, `string? WaitFor(long id, TimeSpan timeout)` (the result JSON, or `null` on timeout).
  - The daemon holds the named mutex `Local\AgentContinuum.Daemon`, and a second instance exits with code 2. On start: `RecoveryService.Reconcile()`, then expire requests older than 60 s. Loop: requests every 1 s, `ResetScheduler.Tick()` every 30 s, offers to `INotifier`.
  - Commands: `ac daemon`; `ac task add --label --worktree --prompt-file --permission-mode --allowed-tools --max-attempts --deadline`; `ac task start <id>`; `ac resume <id> [--now]`; `ac revoke <id>`; `ac status`; `ac preview <id>`; `ac pin-cli <id>`; `ac accept-workspace <id>`; `ac attest-overage-off`; `ac hook stop-failure --op <id>`. Toast activation `action=resume&task=<id>` submits a `resume` request.
  - A command that needs the daemon waits up to 5 s for its result, and on timeout prints `daemon not running`, deletes its request and exits 3.

- [ ] **Step 1: Write failing tests:** `Submit_NoDaemon_TimesOutAndDeletesRequest` (Review Focus 2); `StaleRequest_ExpiredOnDaemonStart_NeverLaunches` (Review Focus 2); `SecondDaemon_Exits2` (Review Focus 3); `ToastResume_SubmitsResumeRequest`; `Revoke_TerminatesRunAndRefusesLaunch`; `TaskAdd_PinsCli_RejectsNonExe`; `Status_ShowsCountdownsNoContent`.
- [ ] **Step 2:** Expect FAIL. Commit.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Expect PASS. Commit.

---

### Task 15: Acceptance suite and hand check

**Files:**
- Create: `tests/AgentContinuum.Acceptance.Tests/*.cs`, `docs/release1-acceptance.md`

**Interfaces:**
- Consumes: the built `ac.exe` and FakeClaude, run as real processes with a temp `LOCALAPPDATA`.

- [ ] **Step 1: Write the acceptance tests:** `AC4_KillDaemonAtEachCrashPoint` (five points, 20 runs each, recovery matches the table); `AC5_ThousandCrashCycles_NeverExceedMaxAttempts`; `AC6_LimitEndsJobWithin10s`; `AC9_EachProofAloneBlocks`; `AC10_P5MismatchTerminates`; `AC13_NoBaselineWithActiveProcesses`; `AC15_ClockJumpOneOffer`; `I6_SentinelsNowhere` (scans the database, logs and the notifier's captured text).
- [ ] **Step 2:** Run `dotnet test tests/AgentContinuum.Acceptance.Tests`. Fix only through the owning task's code, with a failing test committed first.
- [ ] **Step 3: Hand check, run by your human partner:** start `ac daemon`, add a FakeClaude task whose scenario ends in a verified limit with a reset one minute ahead, and confirm that one notification appears about 3 minutes later and that Resume launches it. Record the result in `docs/release1-acceptance.md`.
- [ ] **Step 4:** Commit. Show `git log --oneline`. STOP for review before any run against the real Claude Code.
