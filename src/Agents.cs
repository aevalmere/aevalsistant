using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Aevalsistant
{
    // Every coding agent that has a hook system gets the same treatment: one entry per lifecycle
    // event that runs "Aevalsistant.exe --hook <agent>", and an adapter that turns that agent's
    // payload into the events SessionBook understands. Config formats differ in three ways:
    //   Grouped: event -> [ { matcher?, hooks: [ {type, command, ...} ] } ]   (Claude Code, Codex, Gemini CLI)
    //   Flat:    event -> [ {command, ...} ]                                   (Cursor, Windsurf)
    //   Own:     a whole file that belongs to us                                (Copilot CLI, OpenCode)
    enum HookLayout { Grouped, Flat, Own }

    sealed class AgentSpec
    {
        public string Id;          // the argument after --hook
        public string Name;        // shown in toasts and the menu
        public string Dir;         // config folder under the user profile; the agent counts as installed if it exists
        public string File;        // file we edit or own, relative to the profile
        public HookLayout Layout;
        public string[] Events;
        public Func<string, string, JObj> Entry;   // (event, command) -> hook entry
        public Func<string, string> OwnContent;      // command -> whole file, for HookLayout.Own
        public string Note = "";   // one-time step the user has to take, shown in the menu
    }

    static class Agents
    {
        static JObj Obj(params object[] kv)
        {
            var o = new JObj();
            for (int i = 0; i < kv.Length; i += 2) o[(string)kv[i]] = kv[i + 1];
            return o;
        }

        static JNum N(int n) => new JNum(n.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public static readonly AgentSpec[] All =
        {
            new AgentSpec
            {
                Id = "claude", Name = "Claude Code", Dir = ".claude", File = @".claude\settings.json", Layout = HookLayout.Grouped,
                Events = new[] { "UserPromptSubmit", "Stop", "StopFailure", "Notification", "SubagentStart", "SubagentStop", "SessionEnd" },
                // Async so Claude Code never waits on us; SessionEnd stays synchronous so the message
                // leaves before the session process exits.
                Entry = (ev, cmd) => ev == "SessionEnd"
                    ? Obj("type", "command", "command", cmd, "timeout", N(10))
                    : Obj("type", "command", "command", cmd, "timeout", N(10), "async", true),
            },
            new AgentSpec
            {
                Id = "codex", Name = "Codex", Dir = ".codex", File = @".codex\hooks.json", Layout = HookLayout.Grouped,
                Events = new[] { "UserPromptSubmit", "Stop", "PermissionRequest", "Interrupt", "SubagentStart", "SubagentStop", "SessionEnd" },
                // Interrupt and SessionEnd allow at most 3 seconds.
                Entry = (ev, cmd) => ev == "SessionEnd" || ev == "Interrupt"
                    ? Obj("type", "command", "command", cmd, "timeout", N(3))
                    : Obj("type", "command", "command", cmd, "timeout", N(10), "async", true),
                Note = "Codex: run /hooks once to trust the new hooks",
            },
            new AgentSpec
            {
                Id = "gemini", Name = "Gemini CLI", Dir = ".gemini", File = @".gemini\settings.json", Layout = HookLayout.Grouped,
                Events = new[] { "BeforeAgent", "AfterAgent", "Notification", "SessionEnd" },
                Entry = (ev, cmd) => Obj("name", "aevalsistant", "type", "command", "command", cmd, "timeout", N(5000)),   // milliseconds
            },
            new AgentSpec
            {
                Id = "copilot", Name = "Copilot CLI", Dir = ".copilot", File = @".copilot\hooks\aevalsistant.json", Layout = HookLayout.Own,
                Events = new[] { "userPromptSubmitted", "agentStop", "errorOccurred", "sessionEnd" },
                // Copilot's camelCase payloads carry no event name, so the command line does.
                // On Windows Copilot runs the "powershell" field, falling back to "command".
                OwnContent = cmd =>
                {
                    var hooks = new JObj();
                    foreach (var ev in new[] { "userPromptSubmitted", "agentStop", "errorOccurred", "sessionEnd" })
                        hooks[ev] = new List<object> { Obj("type", "command", "command", cmd + " " + ev, "powershell", PowerShell(cmd) + " " + ev, "timeoutSec", N(10)) };
                    return Json.Write(Obj("version", N(1), "hooks", hooks)) + "\n";
                },
            },
            new AgentSpec
            {
                Id = "cursor", Name = "Cursor", Dir = ".cursor", File = @".cursor\hooks.json", Layout = HookLayout.Flat,
                Events = new[] { "beforeSubmitPrompt", "afterAgentResponse", "stop", "sessionEnd" },
                Entry = (ev, cmd) => Obj("command", cmd, "timeout", N(10)),
            },
            new AgentSpec
            {
                Id = "windsurf", Name = "Windsurf", Dir = @".codeium\windsurf", File = @".codeium\windsurf\hooks.json", Layout = HookLayout.Flat,
                Events = new[] { "pre_user_prompt", "post_cascade_response" },
                // On Windows Windsurf runs the "powershell" field.
                Entry = (ev, cmd) => Obj("command", cmd, "powershell", PowerShell(cmd), "show_output", false),
            },
            new AgentSpec
            {
                Id = "opencode", Name = "OpenCode", Dir = @".config\opencode", File = @".config\opencode\plugins\aevalsistant.js", Layout = HookLayout.Own,
                Events = new string[0],
                OwnContent = OpenCodePlugin,
            },
        };

        public static AgentSpec Get(string id) => All.FirstOrDefault(a => a.Id == id) ?? All[0];

        // Hook commands run through whichever shell each agent uses (bash, cmd, or PowerShell), so
        // the exe path should work unquoted in all of them. A folder whose name has a space or a
        // shell character is swapped for its 8.3 short name. Only those: the Aevalsistant parts
        // stay long so ClaudeHooks.IsOurs keeps recognizing the command. With short names turned
        // off, the path is quoted, which bash and cmd accept.
        internal static string HookPath(string exe, Func<string, string> shortName)
        {
            string[] parts = exe.Split('\\');
            var result = (string[])parts.Clone();
            for (int i = 1; i < parts.Length; i++)
            {
                if (ShellSafe(parts[i])) continue;
                string s = shortName(string.Join("\\", parts, 0, i + 1));
                int cut = s?.LastIndexOf('\\') ?? -1;
                if (cut >= 0) result[i] = s.Substring(cut + 1);
            }
            string path = string.Join("/", result);
            return result.All(ShellSafe) ? path : "\"" + path + "\"";
        }

        // PowerShell runs a quoted path only behind its call operator.
        static string PowerShell(string command) => command.StartsWith("\"") ? "& " + command : command;

        static bool ShellSafe(string part) =>
            part.Length > 0 && part.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' || c == '~' || c == ':');

        // Claude Code keeps the bare "--hook" form so existing installs do not churn.
        public static string CommandFor(AgentSpec a, string baseCommand) => a.Id == "claude" ? baseCommand : baseCommand + " " + a.Id;

        // Where the session runs, as shown after the title: "VS Code", "Codex · Terminal", "Cursor".
        public static string Where(string agentId, string host)
        {
            if (string.IsNullOrEmpty(agentId) || agentId == "claude" || agentId == "chat") return host ?? "";
            string name = Get(agentId).Name;
            return string.IsNullOrEmpty(host) || host == name ? name : name + " · " + host;
        }

        // ---- config file editing -----------------------------------------------------------

        // Returns the new file text, or null when the file should be deleted (own files on removal).
        // command == null removes our entries. Everything that is not ours stays as it was.
        public static string Apply(AgentSpec a, string text, string command, out bool changed)
        {
            if (a.Layout == HookLayout.Own)
            {
                string want = command == null ? null : a.OwnContent(command);
                changed = want != text && !(want == null && text == null);
                return want;
            }

            JObj root;
            string before = null;
            if (string.IsNullOrWhiteSpace(text)) root = new JObj();
            else
            {
                root = Json.Parse(text) as JObj;
                if (root == null) throw new FormatException(Path.GetFileName(a.File) + " is not a JSON object");
                before = Json.Write(root);
            }

            if (a.Id == "cursor" && command != null && root["version"] == null) root["version"] = N(1);
            var hooks = root["hooks"] as JObj;
            if (root["hooks"] != null && hooks == null) throw new FormatException("\"hooks\" is not an object");
            if (hooks == null && command != null) { hooks = new JObj(); root["hooks"] = hooks; }

            if (hooks != null)
            {
                foreach (var ev in a.Events)
                {
                    var list = hooks[ev] as List<object>;
                    if (list != null) Strip(list, a.Layout);
                    if (command != null)
                    {
                        if (list == null) { list = new List<object>(); hooks[ev] = list; }
                        var entry = a.Entry(ev, command);
                        list.Add(a.Layout == HookLayout.Grouped ? Obj("hooks", new List<object> { entry }) : entry);
                    }
                    else if (list != null && list.Count == 0) hooks.Remove(ev);
                }
                if (command == null && hooks.Items.Count == 0) root.Remove("hooks");
            }

            string after = Json.Write(root);
            changed = before == null ? command != null : before != after;
            return after + "\n";
        }

        public static bool IsInstalled(AgentSpec a, string text, string command)
        {
            if (a.Layout == HookLayout.Own) return text != null && text == a.OwnContent(command);
            if (string.IsNullOrWhiteSpace(text) || !Json.TryParse(text, out var v)) return false;
            var hooks = (v as JObj)?.Obj("hooks");
            if (hooks == null) return false;
            foreach (var ev in a.Events)
            {
                bool found = false;
                if (hooks[ev] is List<object> list)
                    foreach (var item in list)
                        foreach (var entry in Entries(item as JObj, a.Layout))
                            if (entry.Str("command") == command) found = true;
                if (!found) return false;
            }
            return true;
        }

        static IEnumerable<JObj> Entries(JObj item, HookLayout layout)
        {
            if (item == null) yield break;
            if (layout == HookLayout.Flat) { yield return item; yield break; }
            if (item["hooks"] is List<object> inner)
                foreach (var h in inner)
                    if (h is JObj o) yield return o;
        }

        static void Strip(List<object> list, HookLayout layout)
        {
            if (layout == HookLayout.Flat)
            {
                list.RemoveAll(h => IsOurs(h as JObj));
                return;
            }
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (!((list[i] as JObj)?["hooks"] is List<object> inner)) continue;
                int removed = inner.RemoveAll(h => IsOurs(h as JObj));
                if (removed > 0 && inner.Count == 0) list.RemoveAt(i);
            }
        }

        static bool IsOurs(JObj entry) =>
            entry != null && (ClaudeHooks.IsOurs(entry.Str("command")) || ClaudeHooks.IsOurs(entry.Str("powershell")));

        // ---- payload adapters ----------------------------------------------------------------

        // Fills e from one agent's hook payload. Event names come out as Claude Code's, which is
        // what SessionBook works in; Interrupt and AgentText are the two additions.
        public static void Normalize(string agent, JObj h, HookEvent e, string pwd)
        {
            string S(string k) => h.Str(k) ?? "";
            switch (agent)
            {
                case "codex":
                    e.SessionId = S("session_id");
                    e.Cwd = S("cwd");
                    e.Transcript = S("transcript_path");
                    e.AgentId = S("agent_id");
                    e.LastAssistant = S("last_assistant_message");
                    e.Name = S("hook_event_name");
                    if (e.Name == "PermissionRequest")
                    {
                        e.Name = "Notification";
                        e.NotificationType = "permission_prompt";
                        string tool = S("tool_name");
                        string why = (h.Obj("tool_input")?.Str("description")) ?? "";
                        e.Message = why.Length > 0 ? why : tool.Length > 0 ? "Codex wants to use " + tool : "Codex is asking for permission.";
                    }
                    break;

                case "gemini":
                    e.SessionId = S("session_id");
                    e.Cwd = S("cwd");
                    e.Transcript = S("transcript_path");
                    switch (S("hook_event_name"))
                    {
                        case "BeforeAgent": e.Name = "UserPromptSubmit"; break;
                        case "AfterAgent": e.Name = "Stop"; e.LastAssistant = S("prompt_response"); break;
                        case "SessionEnd": e.Name = "SessionEnd"; break;
                        case "Notification":
                            // Gemini also sends errors, warnings, and info here. Only a tool
                            // permission request needs you; versions without the type sent only those.
                            string kind = S("notification_type");
                            if (kind.Length > 0 && kind != "ToolPermission") break;
                            e.Name = "Notification";
                            e.NotificationType = "permission_prompt";
                            e.Message = S("message").Length > 0 ? S("message") : "Gemini is asking for permission.";
                            break;
                    }
                    break;

                case "copilot":
                    e.SessionId = S("sessionId").Length > 0 ? S("sessionId") : S("session_id");
                    e.Cwd = S("cwd");
                    e.Transcript = S("transcriptPath");
                    switch (e.Name)
                    {
                        case "userPromptSubmitted": e.Name = "UserPromptSubmit"; break;
                        case "agentStop": e.Name = "Stop"; break;
                        case "sessionEnd": e.Name = "SessionEnd"; break;
                        case "errorOccurred":
                            // A recoverable error does not end the turn.
                            e.Name = (h["recoverable"] as bool?) == true ? "" : "StopFailure";
                            break;
                    }
                    break;

                case "cursor":
                    e.SessionId = S("conversation_id");
                    e.Transcript = S("transcript_path");
                    e.Cwd = (h["workspace_roots"] as List<object>)?.OfType<string>().FirstOrDefault() ?? "";
                    switch (S("hook_event_name"))
                    {
                        case "beforeSubmitPrompt": e.Name = "UserPromptSubmit"; break;
                        case "afterAgentResponse": e.Name = "AgentText"; e.LastAssistant = S("text"); break;
                        case "sessionEnd": e.Name = "SessionEnd"; break;
                        case "stop":
                            string status = S("status");
                            e.Name = status == "aborted" ? "Interrupt" : status == "error" ? "StopFailure" : "Stop";
                            break;
                    }
                    break;

                case "windsurf":
                    e.SessionId = S("trajectory_id");
                    var info = h.Obj("tool_info");
                    switch (S("agent_action_name"))
                    {
                        case "pre_user_prompt": e.Name = "UserPromptSubmit"; break;
                        case "post_cascade_response": e.Name = "Stop"; e.LastAssistant = info?.Str("response") ?? ""; break;
                    }
                    break;

                case "opencode":
                    // Sent by our own plugin: {"event": "...", "session": "...", "cwd": "..."}
                    e.SessionId = S("session");
                    e.Cwd = S("cwd");
                    switch (S("event"))
                    {
                        case "busy": e.Name = "UserPromptSubmit"; break;
                        case "idle": e.Name = "Stop"; break;
                        case "error": e.Name = "StopFailure"; break;
                        case "permission":
                            e.Name = "Notification";
                            e.NotificationType = "permission_prompt";
                            e.Message = S("message").Length > 0 ? S("message") : "OpenCode is asking for permission.";
                            break;
                    }
                    break;

                default:   // Claude Code
                    e.Name = S("hook_event_name");
                    e.SessionId = S("session_id");
                    e.Cwd = S("cwd");
                    e.Transcript = S("transcript_path");
                    e.Message = S("message");
                    e.NotificationType = S("notification_type");
                    e.LastAssistant = S("last_assistant_message");
                    e.AgentId = S("agent_id");
                    break;
            }
            // No folder in the payload: the hook runs in the agent's working folder, which is the
            // next best guess. A guess never replaces a folder the session already has.
            if (e.Cwd.Length == 0 && !string.IsNullOrEmpty(pwd)) { e.Cwd = pwd; e.CwdGuessed = true; }
            // Session ids are only unique per agent.
            if (e.SessionId.Length > 0 && agent != "claude") e.SessionId = agent + ":" + e.SessionId;
        }

        // OpenCode loads plugins from ~/.config/opencode/plugins. This one forwards session state
        // changes to the same --hook entry point the other agents use.
        static string OpenCodePlugin(string command)
        {
            int cut = command.LastIndexOf(" --hook", StringComparison.Ordinal);
            string exe = (cut > 0 ? command.Substring(0, cut) : command).Trim('"');
            return
"// Aevalsistant: tells the tray app when an OpenCode session starts and stops working.\n" +
"// Written by Aevalsistant; removed by \"Remove from this PC\" or by turning off agent hooks.\n" +
"const EXE = " + Json.Quote(exe) + ";\n" +
"\n" +
"export const Aevalsistant = async ({ directory }) => {\n" +
"  const send = (event, session, message) => {\n" +
"    try {\n" +
"      const body = JSON.stringify({ event, session: session || \"\", cwd: directory || \"\", message: message || \"\" });\n" +
"      Bun.spawn([EXE, \"--hook\", \"opencode\"], { stdin: new Blob([body]), stdout: \"ignore\", stderr: \"ignore\" });\n" +
"    } catch (e) {\n" +
"      // Aevalsistant is not installed or not reachable; OpenCode carries on.\n" +
"    }\n" +
"  };\n" +
"  // session.status and the older session.idle both report a finish, so a finish is sent only\n" +
"  // for a session that was busy, and only once.\n" +
"  const busy = {};\n" +
"  return {\n" +
"    event: async ({ event }) => {\n" +
"      const p = event.properties || {};\n" +
"      const id = p.sessionID || (p.info && p.info.id) || \"\";\n" +
"      const status = event.type === \"session.status\" && p.status ? p.status.type : \"\";\n" +
"      if (status === \"busy\") { if (!busy[id]) { busy[id] = true; send(\"busy\", id); } }\n" +
"      else if (status === \"idle\" || event.type === \"session.idle\") { if (busy[id]) { busy[id] = false; send(\"idle\", id); } }\n" +
"      else if (event.type === \"session.error\") { busy[id] = false; send(\"error\", id); }\n" +
"      else if (event.type === \"permission.asked\") {\n" +
"        const what = (p.permission || \"\") + (p.patterns && p.patterns.length ? \": \" + p.patterns.join(\", \") : \"\");\n" +
"        send(\"permission\", id, what ? \"OpenCode wants to use \" + what : \"\");\n" +
"      }\n" +
"    },\n" +
"  };\n" +
"};\n";
        }
    }
}
