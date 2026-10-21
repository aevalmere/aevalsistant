#!/bin/bash
# Runs the built app for real against a scratch home folder: it must connect the agents found
# there, take hook calls, keep the Mac awake while one works, show a card when it stops, and
# leave hooks alone and return at once when the app is not running.
set -euo pipefail
cd "$(dirname "$0")/.."

home=$(cd "$(mktemp -d)" && pwd -P)
mkdir -p "$home/Applications" "$home/.claude" "$home/.codex" "$home/.cursor" "$home/.config/opencode" preview-mac
ditto mac/build/Aevalsistant.app "$home/Applications/Aevalsistant.app"
bin="$home/Applications/Aevalsistant.app/Contents/MacOS/Aevalsistant"
run() { HOME="$home" "$bin" "$@"; }
fail() { echo "::error::smoke: $1"; run --status || true; exit 1; }

HOME="$home" "$bin" &
app=$!
trap 'kill $app 2>/dev/null || true' EXIT
for _ in $(seq 1 50); do run --status | grep -q '"version"' && break; sleep 0.2; done
run --status

grep -q -- "MacOS/Aevalsistant --hook\"" "$home/.claude/settings.json" || fail "Claude Code hooks not added"
grep -q -- "--hook codex" "$home/.codex/hooks.json" || fail "Codex hooks not added"
grep -q -- "--hook cursor" "$home/.cursor/hooks.json" || fail "Cursor hooks not added"
grep -q "const EXE" "$home/.config/opencode/plugins/aevalsistant.js" || fail "OpenCode plugin not written"
[ ! -e "$home/.gemini" ] || fail "a hook folder was created for an agent that is not installed"

echo '{"session_id":"smoke","hook_event_name":"UserPromptSubmit","cwd":"/tmp/smoke-project"}' | run --hook
sleep 0.5
run --status | grep -q '"busy": 1' || fail "a prompt did not start a working session"
run --status | grep -q '"keepAwake": true' || fail "keep-awake is off while an agent works"
pmset -g assertions | grep -q "Aevalsistant: AI agents are running" || fail "no power assertion while an agent works"

echo '{"session_id":"smoke","hook_event_name":"Stop","cwd":"/tmp/smoke-project","last_assistant_message":"All green."}' | run --hook
sleep 0.6
run --status | grep -q '"busy": 0' || fail "Stop did not end the session's work"
run --status | grep -q '"card": "smoke-project finished"' || fail "no card for the finished session"
pmset -g assertions | grep -q "Aevalsistant: AI agents are running" && fail "the power assertion outlived the work"
# A screenshot for review, where the runner allows one; never long enough to hang the job.
screencapture -x preview-mac/screen-with-card.png & shot=$!
sleep 3
kill $shot 2>/dev/null || true

out=$(echo '{"conversation_id":"k","hook_event_name":"beforeSubmitPrompt","workspace_roots":["/tmp/app"]}' | run --hook cursor)
[ "$out" = '{"continue":true}' ] || fail "Cursor's hook did not answer continue (got '$out')"

kill $app
wait $app 2>/dev/null || true
trap - EXIT
started=$(date +%s)
echo '{"session_id":"x","hook_event_name":"Stop"}' | run --hook || fail "a hook call failed with the app closed"
[ $(( $(date +%s) - started )) -le 1 ] || fail "a hook call waited with the app closed"
echo "smoke: all checks passed"
