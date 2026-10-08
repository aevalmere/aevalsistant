# Aevalsistant

A tray app for Windows that keeps the PC awake while AI agents are working and drops a notification
from the top of the screen when one finishes or needs you. The top of the card is the session that
just changed; under it, every other session with its state (needs you, done, working). Click a row,
or press Alt+Tab while the card is showing to jump to the top one.

## What it tracks

| Agent | How | Where it is configured | One-time step |
|---|---|---|---|
| Claude Code (terminal, VS Code, JetBrains, Claude desktop Code tab) | Hooks | `~/.claude/settings.json` | Restart open sessions once |
| Codex CLI and the Codex IDE extension | Hooks | `~/.codex/hooks.json` | In Codex, run `/hooks` once to trust them |
| Gemini CLI | Hooks | `~/.gemini/settings.json` | Restart open sessions once |
| GitHub Copilot CLI | Hooks | `~/.copilot/hooks/aevalsistant.json` | Restart open sessions once |
| Cursor (agent in the editor) | Hooks | `~/.cursor/hooks.json` | None |
| Windsurf (Cascade) | Hooks | `~/.codeium/windsurf/hooks.json` | None |
| OpenCode | Plugin | `~/.config/opencode/plugins/aevalsistant.js` | Restart OpenCode once |
| Claude desktop chat, ChatGPT desktop | Reads the app's Stop button through Windows accessibility | Nothing to configure | None |

Hooks are only added for agents whose config folder exists on this PC. Agents installed later are
picked up within 10 minutes. Every file is backed up once as `<name>.aevalsistant.bak` before the
first change, and other hooks in it are left alone.

Rows and notifications name the project folder, the agent when it is not Claude Code, and the app it
runs in: "rover needs you · Codex · Terminal", "site finished · Gemini CLI".

Subagents count as work. A Claude Code session stays busy while any of its subagents run, including
background subagents that keep going after Claude's turn ends ("2 subagents running").

Chat apps have no hooks. When you switch away from Claude or ChatGPT while a reply is being written,
the app sees the reply's Stop button. It then checks every few seconds until the button is gone and
shows "Chat finished · Claude". Nothing is read while you are not waiting on a reply. If the reply
finishes while that app is in front, no notification is shown. Turn this off with "Watch Claude and
ChatGPT chats" in the tray menu.

Not tracked:

- Cloud sessions of any agent, which run on the provider's servers.
- Chats in a browser tab.
- Background shell commands, which have no completion hook.
- Aider, which only has a "waiting for input" command and no start signal.
- Cline, whose hook format could not be confirmed.

## Build

    dotnet build -c Release

Output: `bin/Release/net48/Aevalsistant.exe` (targets .NET Framework 4.8, which ships with Windows 10
and 11). Tests: `dotnet build -c Release tests`, then run `tests/bin/Release/net48/Aevalsistant.Tests.exe`.

## What it changes on your PC

- Copies itself to `%LOCALAPPDATA%\Aevalsistant\` on first run.
- Adds hook entries to the files in the table above, for agents that are installed.
- Adds a `HKCU\...\Run` entry when "Start with Windows" is on.
- While agents work: a system power request named "Aevalsistant: AI agents are running"
  (check with `powercfg /requests`). With "Stay awake with the lid closed" on, the lid action of the
  active power plan is set to "Do nothing" and restored when the last agent stops.

"Remove from this PC" in the tray menu undoes all of it, including the hooks in every agent's files.

## Updates

Installed copies check the latest GitHub release of `aevalmere/aevalsistant` two minutes after
starting and every six hours after that. "Check for updates" in the tray menu runs the check right
away. A newer release is downloaded to `%LOCALAPPDATA%\Aevalsistant\update\` and checked against
its published SHA-256. Its version is read from the file itself. It is installed the next time no
agent is working, because restarting forgets which sessions are busy. After the restart, a card says
which version is now running. "Update automatically" in the tray menu turns the background check off.

The repository must be public: the check uses GitHub's API without signing in.

To manually update, double-click a newer exe. It replaces the installed copy the same way.

## Publishing a release

`.github/workflows/release.yml` builds and tests on Windows for every push and pull request. The card
renders are uploaded as a build artifact. Pushing a version tag also publishes a release with
`Aevalsistant.exe` and `Aevalsistant.exe.sha256`:

    git tag v1.2.0
    git push origin v1.2.0

The tag sets the version baked into the exe, so tag numbers must go up.
