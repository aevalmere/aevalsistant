# How Aevalsistant works

For anyone changing the code. The [README](../README.md) covers what the app does from the user's
side; this covers how. The exe is about 170 KB with no dependencies, and the built-in updater reads
releases of `aevalmere/aevalsistant`.

## What it is

A Windows tray app for people running several AI agents at once: Claude Code, Codex, Gemini CLI,
Copilot CLI, Cursor, Windsurf, OpenCode, and the Claude and ChatGPT desktop chats. It does two things:

1. Keeps the PC awake while any agent is working, so sleep does not cut off a long run.
2. Drops a notification card from the top center of the screen when an agent finishes or needs
   input. The top row is the session whose status just changed. Under it, every other known session
   is listed with its state ("needs you", "done 3m ago", "working 12m", "2 subagents running").
   Clicking a row focuses that session's window. Pressing Alt+Tab while the card is showing focuses
   the top session's window.

The card is excluded from screenshots and screen sharing, never takes keyboard focus, and its
auto-hide countdown only runs while the user is at the keyboard. Visual style comes from the
owner's profile picture (warm cream, slate, blush); see DESIGN.md.

## Where it tracks sessions

| Agent | Mechanism | File the app edits or owns | One-time step for the user |
|---|---|---|---|
| Claude Code (terminal, VS Code, JetBrains IDEs, desktop app Code tab) | Hooks | `~/.claude/settings.json` | Restart open sessions |
| Codex CLI and IDE extension | Hooks | `~/.codex/hooks.json` | Run `/hooks` in Codex to trust them |
| Gemini CLI | Hooks (synchronous, timeout in ms) | `~/.gemini/settings.json` | Restart open sessions |
| Copilot CLI | Hooks, camelCase events; the event name is passed on the command line because the payload lacks it | `~/.copilot/hooks/aevalsistant.json` (owned) | Restart open sessions |
| Cursor | Hooks, flat format; must answer `{"continue":true}` on stdout | `~/.cursor/hooks.json` | None |
| Windsurf Cascade | Hooks, flat format with a `powershell` command | `~/.codeium/windsurf/hooks.json` | None |
| OpenCode | JS plugin that spawns the exe with Bun.spawn | `~/.config/opencode/plugins/aevalsistant.js` (owned) | Restart OpenCode |
| Claude desktop chat, ChatGPT desktop | UI Automation: looks for a Stop button | none | None |

Hooks are added only for agents whose config folder exists. The app checks again every 10 minutes.
Claude Code is the exception: its folder is created if missing.

Not tracked: cloud sessions of any agent, browser tabs (a browser extension was built and then
dropped at the owner's request), background shell commands, Aider (it has only a "waiting for
input" command, with no start signal), and Cline (its hook format could not be confirmed).

## How it works

- **Hooks.** Each agent gets one entry per lifecycle event that runs
  `Aevalsistant.exe --hook <agent> [event]`. Claude Code keeps the bare `--hook` form. Claude Code
  gets UserPromptSubmit, Stop, StopFailure, Notification, SubagentStart, SubagentStop, and
  SessionEnd, all async except SessionEnd. Existing hooks are kept, and every edited file is backed
  up once as `<name>.aevalsistant.bak`. The specs for all agents live in `src/Agents.cs`.
- **Adapters.** `Agents.Normalize` maps each agent's payload onto Claude Code's event names, plus two
  additions: Interrupt (stopped by the user, no notification) and AgentText (reply text that arrives
  before Stop, used by Cursor). Session ids are prefixed with the agent ("codex:…"). When a payload
  has no folder, the hook process's working folder is used, but never over a folder the session
  already has.
- **Hook client.** The `--hook` process reads the hook JSON from stdin. It walks its parent process
  chain to find two things: the agent's own long-lived process, used to notice when the agent exits,
  and the window hosting it. Cursor and Windsurf get no process, because their helpers can be
  short-lived, and rely on the timeout instead. It labels the host app and sends everything to the
  tray app over a named pipe. It exits immediately when the tray app is
  not running, and always exits 0 so Claude Code is never blocked.
- **Finding the window.** First the console route: a classic console window, or the Windows
  Terminal window that owns the pseudo console. Otherwise, the nearest ancestor process with visible
  windows (Code.exe, studio64.exe, Claude.exe). If that process has several windows, it picks the one
  whose title contains the project folder name.
- **Session states.** Working from UserPromptSubmit until Stop or StopFailure. A permission prompt
  marks the session "needs you" but keeps it working. Subagents are tracked by agent id from
  SubagentStart to SubagentStop; a background subagent keeps the session busy after the parent's turn
  ends. Async hooks can arrive out of order, so prompt and stop events are ordered by the hook
  process start time.
- **Cleanup sweep, every 5 seconds.** It drops a session when its Claude process has exited. It
  marks a session idle when the transcript ends in "[Request interrupted by user]" (Esc skips the
  Stop hook). It also marks a session idle after 45 minutes with no hook event and no transcript
  write. Subagents get the same 45-minute rule, checked against their own transcript at
  `<session>/subagents/agent-<id>.jsonl`.
- **Chat apps.** `src/Chats.cs`. A WinEvent hook notices when Claude.exe or ChatGPT.exe loses the
  foreground. A worker thread then looks in that window's UI Automation tree for a button named
  "Stop response", "Stop streaming", "Stop generating", or "Stop". If it finds one, it starts a
  "Chat" session and rechecks every 2.5 seconds. Two misses in a row end the session: a notification
  if the app is in the background, a quiet stop if it is in front. Windows that already have a busy
  hook session are skipped, so the desktop app's Code tab is not counted twice. If UI Automation
  fails to load, chat watching turns itself off and says so in the menu.
- **Keep-awake.** A named power request ("Aevalsistant: AI agents are running", visible in
  `powercfg /requests`) while any session is busy. Options in the tray menu: keep the screen on, and
  set the lid action to "Do nothing" while agents run. The lid change is restored afterwards, and
  also on the next launch after a crash.
- **Updates.** `src/Updater.cs` reads `api.github.com/repos/aevalmere/aevalsistant/releases/latest`
  two minutes after start and every six hours. It downloads the `Aevalsistant.exe` asset and checks
  it against the asset's SHA-256 `digest`, which GitHub computes on upload; a release without one is
  refused. It then confirms with `AssemblyName` that the file is Aevalsistant at the claimed
  version, and runs it once no session is busy. The usual hand-off then replaces the installed copy,
  and a card reports the new version. Releases come from `.github/workflows/build.yml` on a `v*`
  tag: it builds and tests on a Windows runner, then publishes that exe as the release's only asset.
- **Install and update.** Double-clicking the exe from anywhere copies it to
  `%LOCALAPPDATA%\Aevalsistant\`, asks a running older copy to quit, and starts the installed copy.
  Windows will not overwrite an exe that is still running (the old copy may still be exiting, or a
  `--hook` call may be using it), so the old file is renamed to `Aevalsistant.exe.<ticks>.old`
  first and deleted on a later start. The browser's download mark is removed from the copy so
  SmartScreen does not ask again at sign-in. An elevated start ("Run as administrator") is refused,
  because hook calls from agents running normally cannot reach an elevated tray app.
  "Start with Windows" writes an HKCU Run key. "Remove from this PC" in the tray menu removes the
  hooks, the Run key, and the folder.

## Code map

Stack: C# on .NET Framework 4.8 (ships with Windows 10 and 11), WinForms for the tray, raw Win32
through P/Invoke for everything else. No dependencies. Builds with the .NET SDK (8 or later) on
Windows, or on Linux through the .NET Framework reference assemblies package.

| File | Contents |
|---|---|
| `src/Program.cs` | Entry point: `--hook` mode, self-install and hand-off, single instance, pipe client |
| `src/Core.cs` | Platform-free logic: session state machine, subagents, transcript parsing, settings.ini, status text |
| `src/Agents.cs` | Per-agent hook specs, installers for the grouped, flat, and owned-file layouts, payload adapters, the OpenCode plugin source |
| `src/Chats.cs` | Chat app watcher (UI Automation) and its testable `ChatTracker` state machine |
| `src/Json.cs` | Small JSON parser and writer that keeps key order and number text, so rewriting settings.json does not churn it |
| `src/Windows.cs` | Process tree walk, window lookup, host app labels, liveness checks, focus |
| `src/Toast.cs` | Card renderer (GDI+), layered window, compositor-paced drop animation, hit testing, Alt+Tab keyboard hook |
| `src/TrayApp.cs` | Tray icon and menu, pipe server, notification queue, sweep, keep-awake and lid wiring, hooks file I/O, uninstall |
| `src/Power.cs` | Power request and lid-action override |
| `src/Theme.cs` | Palette, fonts, tray glyph, menu renderer |
| `src/Updater.cs` | GitHub release check, download, checksum and version verification |
| `src/Native.cs` | Win32 declarations |
| `tests/` | Checks on core logic, installers, adapters, chat tracking, and update asset selection, plus PNG renders of the card; `MenuPreview.cs` drives the real tray menu under Wine |
| `.github/workflows/build.yml` | Windows build and test on every push; tagged pushes publish a release |
| `docs/DESIGN.md` | Design constraints: palette with measured contrast, type, spacing, motion, states |

Build: `dotnet build -c Release` gives `bin/Release/net48/Aevalsistant.exe`. Tests:
`dotnet build -c Release tests`, then run `tests/bin/Release/net48/Aevalsistant.Tests.exe`, under
mono on Linux, with an optional output folder for preview PNGs.

## Verification so far

- The automated checks pass on Windows 11 (and in CI on every push). They cover:
  - install, re-install, and removal for all seven agents, keeping the user's own hooks
  - hook paths for user folders with spaces or shell characters, with and without 8.3 names
  - each agent's payload adapter, using the payload shapes from its documentation
  - session and subagent state, out-of-order delivery, interrupts, and staleness
  - the chat tracker
  - transcript parsing, including a real Claude Code transcript
  - toast layout, and picking the exe and its digest out of a release
- On Windows 11: the card is invisible to screen capture. With `WDA_EXCLUDEFROMCAPTURE` set, a
  capture of its area shows only what is behind it; with the flag cleared, the card appears.
- On Windows 11: the real `Updater.Check` against a local stand-in for GitHub downloads and verifies
  a 9.9.0 release, throws away a download whose digest does not match, and refuses a release with
  no digest.
- On Windows 11: a running exe cannot be overwritten (`IOException`) but can be renamed, which is
  what the hand-off relies on. Deleting the `Zone.Identifier` stream with `DeleteFileW` removes the
  download mark and leaves the file.
- Earlier, under Wine: a 9.9.0 build replaced a running 1.1.0 install through the updater, cleaned
  up the update folder, and showed "Aevalsistant updated to 9.9.0". Wine does not lock running
  exes, so this did not exercise the rename above.
- Under Wine on Linux: first-run install, including hooks written into fake Codex, Gemini, Copilot,
  Cursor, Windsurf, and OpenCode folders (the user's Gemini settings and Cursor hook were kept);
  `--hook` calls from four agents tracked at once in one card; real `--hook` calls producing cards;
  the live list updating; the drop animation recorded frame by frame, Alt+Tab switching foreground from one window
  to another, row hover and click, and the styled tray menu.
- **Not yet confirmed on real Windows:**
  - A full install and update cycle with the real tray app running.
  - The lid setting can be written without admin rights.
  - Window detection is correct in Windows Terminal, VS Code, Android Studio, and the Claude desktop
    app, with several of each open.
  - Hooks still receive stdin when Claude Code uses PowerShell instead of Git Bash.
  - SmartScreen and antivirus reaction to an unsigned exe that installs a keyboard hook and a
    startup entry.
  - Each non-Claude agent with its real binary. The payload shapes come from each tool's docs, not
    from a live run, and none of their Windows hook shells has been exercised.
  - Chat watching against the real Claude and ChatGPT apps: the Stop button names are assumptions,
    and UI Automation cannot run under Wine.

## Known limits

- Focus lands on the window, not on a specific terminal tab inside VS Code or Windows Terminal, and
  not on a specific session inside the desktop app.
- Sessions that were already open when the hooks were installed or changed must be restarted once.
- Unsigned: Windows shows "Windows protected your PC" on each new download.
- Running a newer exe by hand while agents work restarts the tray app right away, so it forgets
  which sessions are busy until their next prompt. The built-in updater waits for idle instead.
- A user folder name with a shell character such as `'` or `$`, on a PC with 8.3 names turned off,
  gives a quoted hook path that bash and cmd run but some PowerShell-based hooks may not.

## Next steps

1. Run it with one session in each agent and host app, and work through the open items above.
2. Sign the exe, to remove the SmartScreen prompt and reduce antivirus flags. Updates the app
   downloads itself do not get the SmartScreen prompt, because only browser downloads are marked
   as coming from the internet.
