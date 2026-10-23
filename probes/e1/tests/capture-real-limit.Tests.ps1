$script = "$PSScriptRoot\..\capture-real-limit.ps1"

$fake = @'
$a = @($args)
if ($a -contains '--version') { '9.9.9 (Claude Code)'; exit 0 }
$mode = (Get-Content (Join-Path $PSScriptRoot 'mode.txt') -Raw).Trim()
$sid = $a[[array]::IndexOf($a, '--session-id') + 1]
$settings = Get-Content $a[[array]::IndexOf($a, '--settings') + 1] -Raw | ConvertFrom-Json
function Emit($o) { [Console]::Out.WriteLine(($o | ConvertTo-Json -Compress -Depth 6)); [Console]::Out.Flush() }
Emit @{ type = 'system'; subtype = 'init'; session_id = $sid; apiKeySource = 'none' }
if ($mode -eq 'limit') {
  $payload = @{ session_id = $sid; hook_event_name = 'StopFailure'; error = 'rate_limit'; resets_at = 1792800000; message = 'SENTINEL-limit text' } | ConvertTo-Json -Compress
  $payload | cmd.exe /c $settings.hooks.StopFailure[0].hooks[0].command
  Emit @{ type = 'rate_limit_event'; session_id = $sid; rate_limit_info = @{ status = 'rejected'; resetsAt = 1792800000 } }
  Emit @{ type = 'result'; subtype = 'success'; is_error = $true; terminal_reason = 'api_error'; api_error_status = 429; session_id = $sid; usage = @{ input_tokens = 1 } }
  exit 1
}
Emit @{ type = 'result'; subtype = 'success'; is_error = $false; terminal_reason = 'completed'; session_id = $sid }
exit 0
'@

Describe 'capture-real-limit' {
  Set-Content -LiteralPath "$TestDrive\fake.ps1" -Value $fake
  Set-Content -LiteralPath "$TestDrive\fakeclaude.cmd" -Value '@powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0fake.ps1" %*'
  $cli = "$TestDrive\fakeclaude.cmd"

  Context 'at a limit' {
    Set-Content -LiteralPath "$TestDrive\mode.txt" -Value 'limit'
    $out = (& $script -Command $cli -OutDir "$TestDrive\res") -join "`n"
    $code = $LASTEXITCODE
    $saved = Get-Content "$TestDrive\res\limit-9.9.9.json" -Raw
    $doc = $saved | ConvertFrom-Json

    It 'exits 0 and names the saved file' {
      $code | Should Be 0
      $out | Should Match 'limit-9\.9\.9\.json'
    }
    It 'records the hook that fired for its own session' {
      $doc.data.session_id_matched | Should Be $true
      $doc.data.hook_records[0].hook | Should Be 'StopFailure'
      $doc.data.hook_records[0].shape.error | Should Be 'rate_limit'
      $doc.data.hook_records[0].shape.resets_at | Should Be 1792800000
    }
    It 'records whether the process exited on its own' {
      $doc.data.run.exited_by_itself | Should Be $true
      $doc.data.run.exit_code | Should Be 1
    }
    It 'prints a summary with the hook, the limit event and the result, without token usage' {
      $block = [regex]::Match($out, '(?s)----- copy from here -----\n(.*)\n----- to here -----').Groups[1].Value
      $summary = $block | ConvertFrom-Json
      $summary.hooks[0].shape.error | Should Be 'rate_limit'
      $summary.rate_limit_events[0].'rate_limit_info.status' | Should Be 'rejected'
      $summary.result.api_error_status | Should Be 429
      $summary.result.PSObject.Properties.Name -contains 'usage.input_tokens' | Should Be $false
    }
    It 'never stores or prints free text from the payload' {
      $saved | Should Not Match 'SENTINEL'
      $out | Should Not Match 'SENTINEL'
    }
  }

  Context 'not at a limit' {
    Set-Content -LiteralPath "$TestDrive\mode.txt" -Value 'ok'
    $out = (& $script -Command $cli -OutDir "$TestDrive\res2") -join "`n"
    $code = $LASTEXITCODE

    It 'saves nothing and says so' {
      $code | Should Be 2
      $out | Should Match 'Not at a limit'
      Test-Path "$TestDrive\res2" | Should Be $false
    }
  }
}
