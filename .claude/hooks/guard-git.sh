#!/bin/bash
# Fail-closed wrapper around the upstream block-dangerous-git.sh (left unmodified).
# Used by the Bash and PowerShell PreToolUse rules in .claude/settings.json.
# Exit 2 blocks the tool call; anything the check cannot evaluate is blocked.

INPUT=$(cat)
UPSTREAM="$(dirname "$0")/block-dangerous-git.sh"

block() {
  echo "BLOCKED: $1 The git guardrails cannot check this command, so it is not allowed." >&2
  exit 2
}

command -v jq >/dev/null 2>&1 || block "jq is not installed or not on PATH (install jq and restart Claude Code)."
[ -f "$UPSTREAM" ] || block "$UPSTREAM is missing."
printf '%s' "$INPUT" | jq -e '.tool_input.command | strings' >/dev/null 2>&1 || block "the hook input has no command string."

printf '%s' "$INPUT" | bash "$UPSTREAM"
STATUS=$?
case $STATUS in
  0|2) exit $STATUS ;;
  *) block "block-dangerous-git.sh failed with exit $STATUS." ;;
esac
