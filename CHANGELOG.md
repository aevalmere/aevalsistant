# Changelog

Each release's section here becomes its notes on the
[releases page](https://github.com/aevalmere/aevalsistant/releases). Versions follow
[semantic versioning](https://semver.org): the patch number for fixes, the minor number for new
features, the major number for changes that need you to do something.

## [1.3.0] - 2026-10-08

The first public release. Earlier 1.x builds were private.

### What it does

- Keeps the PC awake while Claude Code, Codex, Gemini CLI, Copilot CLI, Cursor, Windsurf, or
  OpenCode is working, and while the Claude or ChatGPT desktop app is writing a reply.
- Drops a card from the top of the screen when a session finishes or needs you, with every other
  session listed under it. Click a row, or press Alt+Tab while the card is up, to jump to that window.
- Keeps a Claude Code session busy while its subagents run, including background subagents.
- Optionally keeps the screen on, or keeps working with the lid closed, while agents work.
- Updates itself from GitHub releases once no agent is working.

### New since the private builds

- The tray menu header shows the running version.
- Releases ship one file, `Aevalsistant.exe`. The updater checks each download against the SHA-256
  that GitHub publishes for it and refuses a release without one.

### Fixed

- Updating no longer fails with "Could not copy Aevalsistant" while the old copy is still exiting.
  If the copy fails anyway, the old version starts again instead of leaving nothing running.
- On a PC whose user folder name has a space, the app no longer adds another set of hooks to every
  agent's settings each time it syncs.
- A hook call can no longer fail or show an error in the agent that ran it.
- The app no longer quits when Claude or ChatGPT stops responding while a chat is being watched. If
  chat watching hits a problem it turns itself off and the tray menu says why.
- Alt+Tab works as usual when the card's session is already the window in front.
- Agent settings files are written to a temporary file and swapped in, so a crash mid-write cannot
  leave them half written.
- Gemini CLI errors and info notices no longer show up as "needs you"; only permission requests do.
- OpenCode permission cards say what OpenCode wants to run, and a finish is reported once.
- Copilot CLI and Windsurf hooks run from PowerShell when the app's path has to be quoted.
- Starting it with "Run as administrator" explains why that cannot work, instead of running where
  agents cannot reach it.
- The installed copy drops the browser's download mark, so SmartScreen does not ask again at sign-in.
- "Remove from this PC" keeps trying to delete its folder for 15 seconds, and its confirmation names
  every agent's hooks, not only Claude Code's.
- A failed lid-setting change no longer leaves the power plan half changed.
- A downloaded update that Windows blocks from starting is reported in the tray menu.
