import Foundation

// Every coding agent that has a hook system gets the same treatment: one entry per lifecycle
// event that runs "<app>/Contents/MacOS/Aevalsistant --hook <agent>", and an adapter that turns
// that agent's payload into the events SessionBook understands. Ported from src/Agents.cs; the
// differences are POSIX paths and shells (no PowerShell fields, Copilot gets "bash").
//   Grouped: event -> [ { matcher?, hooks: [ {type, command, ...} ] } ]   (Claude Code, Codex, Gemini CLI)
//   Flat:    event -> [ {command, ...} ]                                   (Cursor, Windsurf)
//   Own:     a whole file that belongs to us                                (Copilot CLI, OpenCode)
enum HookLayout { case grouped, flat, own }

struct AgentSpec {
    let id: String          // the argument after --hook
    let name: String        // shown in cards and the menu
    let dir: String         // config folder under the home folder; the agent counts as installed if it exists
    let file: String        // file we edit or own, relative to the home folder
    let layout: HookLayout
    let events: [String]
    var entry: (String, String) -> JObj = { _, _ in JObj() }   // (event, command) -> hook entry
    var ownContent: (String) -> String = { _ in "" }           // command -> whole file, for .own
    var note = ""           // one-time step the user has to take, shown in Settings
}

enum AgentSpecError: Error, CustomStringConvertible {
    case notAnObject(String)
    var description: String { if case .notAnObject(let what) = self { return what }; return "" }
}

enum Agents {
    static func entry(_ pairs: (String, Any)...) -> JObj { JObj(pairs) }

    static let all: [AgentSpec] = [
        AgentSpec(id: "claude", name: "Claude Code", dir: ".claude", file: ".claude/settings.json", layout: .grouped,
                  events: ["UserPromptSubmit", "Stop", "StopFailure", "Notification", "SubagentStart", "SubagentStop", "SessionEnd"],
                  // Async so Claude Code never waits on us; SessionEnd stays synchronous so the message
                  // leaves before the session process exits.
                  entry: { ev, cmd in ev == "SessionEnd"
                      ? entry(("type", "command"), ("command", cmd), ("timeout", JNum(10)))
                      : entry(("type", "command"), ("command", cmd), ("timeout", JNum(10)), ("async", true)) }),
        AgentSpec(id: "codex", name: "Codex", dir: ".codex", file: ".codex/hooks.json", layout: .grouped,
                  events: ["UserPromptSubmit", "Stop", "PermissionRequest", "Interrupt", "SubagentStart", "SubagentStop", "SessionEnd"],
                  // Interrupt and SessionEnd allow at most 3 seconds.
                  entry: { ev, cmd in ev == "SessionEnd" || ev == "Interrupt"
                      ? entry(("type", "command"), ("command", cmd), ("timeout", JNum(3)))
                      : entry(("type", "command"), ("command", cmd), ("timeout", JNum(10)), ("async", true)) },
                  note: "Run /hooks once in Codex to trust the new hooks."),
        AgentSpec(id: "gemini", name: "Gemini CLI", dir: ".gemini", file: ".gemini/settings.json", layout: .grouped,
                  events: ["BeforeAgent", "AfterAgent", "Notification", "SessionEnd"],
                  entry: { _, cmd in entry(("name", "aevalsistant"), ("type", "command"), ("command", cmd), ("timeout", JNum(5000))) }),   // milliseconds
        AgentSpec(id: "copilot", name: "Copilot CLI", dir: ".copilot", file: ".copilot/hooks/aevalsistant.json", layout: .own,
                  events: ["userPromptSubmitted", "agentStop", "errorOccurred", "sessionEnd"],
                  // Copilot's camelCase payloads carry no event name, so the command line does.
                  // On macOS Copilot runs the "bash" field; "command" is kept for older versions.
                  ownContent: { cmd in
                      let hooks = JObj()
                      for ev in ["userPromptSubmitted", "agentStop", "errorOccurred", "sessionEnd"] {
                          hooks[ev] = JArr([entry(("type", "command"), ("bash", cmd + " " + ev), ("command", cmd + " " + ev), ("timeoutSec", JNum(10)))])
                      }
                      return Json.write(entry(("version", JNum(1)), ("hooks", hooks))) + "\n"
                  }),
        AgentSpec(id: "cursor", name: "Cursor", dir: ".cursor", file: ".cursor/hooks.json", layout: .flat,
                  events: ["beforeSubmitPrompt", "afterAgentResponse", "stop", "sessionEnd"],
                  entry: { _, cmd in entry(("command", cmd), ("timeout", JNum(10))) }),
        AgentSpec(id: "windsurf", name: "Windsurf", dir: ".codeium/windsurf", file: ".codeium/windsurf/hooks.json", layout: .flat,
                  events: ["pre_user_prompt", "post_cascade_response"],
                  entry: { _, cmd in entry(("command", cmd), ("show_output", false)) }),
        AgentSpec(id: "opencode", name: "OpenCode", dir: ".config/opencode", file: ".config/opencode/plugins/aevalsistant.js", layout: .own,
                  events: [], ownContent: openCodePlugin),
    ]

    static func get(_ id: String) -> AgentSpec { all.first { $0.id == id } ?? all[0] }

    // Hook commands run through sh, bash, or zsh. A path made only of safe characters goes in as
    // is; anything else is single-quoted, which every POSIX shell reads the same way.
    static func hookPath(_ exe: String) -> String {
        exe.unicodeScalars.allSatisfy(shellSafe) ? exe : "'" + exe.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    private static func shellSafe(_ c: Unicode.Scalar) -> Bool {
        (c.isASCII && (CharacterSet.alphanumerics.contains(c))) || "/._-~+@%:,".unicodeScalars.contains(c)
    }

    // Claude Code keeps the bare "--hook" form, as on Windows.
    static func commandFor(_ a: AgentSpec, _ baseCommand: String) -> String { a.id == "claude" ? baseCommand : baseCommand + " " + a.id }

    // Where the session runs, as shown after the title: "VS Code", "Codex · Terminal", "Cursor".
    static func whereLabel(_ agentId: String, _ host: String) -> String {
        if agentId.isEmpty || agentId == "claude" || agentId == "chat" { return host }
        let name = get(agentId).name
        return host.isEmpty || host == name ? name : name + " · " + host
    }

    static func isOurs(_ command: String?) -> Bool {
        guard let c = command else { return false }
        return c.range(of: "aevalsistant", options: .caseInsensitive) != nil && c.contains("--hook")
    }

    // ---- config file editing -----------------------------------------------------------

    // Returns the new file text, or nil when the file should be deleted (own files on removal).
    // command == nil removes our entries. Everything that is not ours stays as it was.
    static func apply(_ a: AgentSpec, _ text: String?, _ command: String?) throws -> (text: String?, changed: Bool) {
        if a.layout == .own {
            let want = command.map(a.ownContent)
            return (want, want != text)
        }

        let root: JObj
        var before: String? = nil
        if let text = text, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            guard let parsed = (try? Json.parse(text)) as? JObj else {
                throw AgentSpecError.notAnObject(((a.file as NSString).lastPathComponent) + " is not a JSON object")
            }
            root = parsed
            before = Json.write(root)
        } else {
            root = JObj()
        }

        if a.id == "cursor", command != nil, root["version"] == nil { root["version"] = JNum(1) }
        var hooks = root["hooks"] as? JObj
        if root["hooks"] != nil, hooks == nil { throw AgentSpecError.notAnObject("\"hooks\" is not an object") }
        if hooks == nil, command != nil { hooks = JObj(); root["hooks"] = hooks }

        if let hooks = hooks {
            for ev in a.events {
                var list = hooks[ev] as? JArr
                if let l = list { strip(l, a.layout) }
                if let command = command {
                    if list == nil { list = JArr(); hooks[ev] = list }
                    let e = a.entry(ev, command)
                    list!.items.append(a.layout == .grouped ? entry(("hooks", JArr([e]))) : e)
                } else if let l = list, l.items.isEmpty {
                    hooks.remove(ev)
                }
            }
            if command == nil, hooks.items.isEmpty { root.remove("hooks") }
        }

        let after = Json.write(root)
        let changed = before == nil ? command != nil : before != after
        return (after + "\n", changed)
    }

    static func isInstalled(_ a: AgentSpec, _ text: String?, _ command: String) -> Bool {
        if a.layout == .own { return text != nil && text == a.ownContent(command) }
        guard let text = text, let root = Json.tryParse(text) as? JObj, let hooks = root.obj("hooks") else { return false }
        for ev in a.events {
            guard let list = hooks[ev] as? JArr else { return false }
            let found = list.items.contains { item in entries(item as? JObj, a.layout).contains { $0.str("command") == command } }
            if !found { return false }
        }
        return true
    }

    private static func entries(_ item: JObj?, _ layout: HookLayout) -> [JObj] {
        guard let item = item else { return [] }
        if layout == .flat { return [item] }
        return (item["hooks"] as? JArr)?.items.compactMap { $0 as? JObj } ?? []
    }

    private static func strip(_ list: JArr, _ layout: HookLayout) {
        if layout == .flat {
            list.items.removeAll { isOursEntry($0 as? JObj) }
            return
        }
        for i in stride(from: list.items.count - 1, through: 0, by: -1) {
            guard let inner = (list.items[i] as? JObj)?["hooks"] as? JArr else { continue }
            let before = inner.items.count
            inner.items.removeAll { isOursEntry($0 as? JObj) }
            if inner.items.count < before, inner.items.isEmpty { list.items.remove(at: i) }
        }
    }

    // The Windows app writes a "powershell" field and the Mac app a "bash" one; either marks an
    // entry as ours, so a settings folder synced between machines does not collect duplicates.
    private static func isOursEntry(_ e: JObj?) -> Bool {
        guard let e = e else { return false }
        return isOurs(e.str("command")) || isOurs(e.str("powershell")) || isOurs(e.str("bash"))
    }

    // ---- payload adapters ----------------------------------------------------------------

    // Fills e from one agent's hook payload. Event names come out as Claude Code's, which is
    // what SessionBook works in; Interrupt and AgentText are the two additions.
    static func normalize(_ agent: String, _ h: JObj, _ e: HookEvent, _ pwd: String?) {
        func s(_ k: String) -> String { h.str(k) ?? "" }
        switch agent {
        case "codex":
            e.sessionId = s("session_id")
            e.cwd = s("cwd")
            e.transcript = s("transcript_path")
            e.agentId = s("agent_id")
            e.agentType = s("agent_type")
            e.lastAssistant = s("last_assistant_message")
            e.name = s("hook_event_name")
            if e.name == "PermissionRequest" {
                e.name = "Notification"
                e.notificationType = "permission_prompt"
                let tool = s("tool_name")
                let why = h.obj("tool_input")?.str("description") ?? ""
                e.message = !why.isEmpty ? why : !tool.isEmpty ? "Codex wants to use " + tool : "Codex is asking for permission."
            }

        case "gemini":
            e.sessionId = s("session_id")
            e.cwd = s("cwd")
            e.transcript = s("transcript_path")
            switch s("hook_event_name") {
            case "BeforeAgent": e.name = "UserPromptSubmit"
            case "AfterAgent": e.name = "Stop"; e.lastAssistant = s("prompt_response")
            case "SessionEnd": e.name = "SessionEnd"
            case "Notification":
                // Gemini also sends errors, warnings, and info here. Only a tool
                // permission request needs you; versions without the type sent only those.
                let kind = s("notification_type")
                if !kind.isEmpty && kind != "ToolPermission" { break }
                e.name = "Notification"
                e.notificationType = "permission_prompt"
                e.message = s("message").isEmpty ? "Gemini is asking for permission." : s("message")
            default: break
            }

        case "copilot":
            e.sessionId = s("sessionId").isEmpty ? s("session_id") : s("sessionId")
            e.cwd = s("cwd")
            e.transcript = s("transcriptPath")
            switch e.name {
            case "userPromptSubmitted": e.name = "UserPromptSubmit"
            case "agentStop": e.name = "Stop"
            case "sessionEnd": e.name = "SessionEnd"
            // A recoverable error does not end the turn.
            case "errorOccurred": e.name = (h["recoverable"] as? Bool) == true ? "" : "StopFailure"
            default: break
            }

        case "cursor":
            e.sessionId = s("conversation_id")
            e.transcript = s("transcript_path")
            e.cwd = (h["workspace_roots"] as? JArr)?.items.compactMap { $0 as? String }.first ?? ""
            switch s("hook_event_name") {
            case "beforeSubmitPrompt": e.name = "UserPromptSubmit"
            case "afterAgentResponse": e.name = "AgentText"; e.lastAssistant = s("text")
            case "sessionEnd": e.name = "SessionEnd"
            case "stop":
                let status = s("status")
                e.name = status == "aborted" ? "Interrupt" : status == "error" ? "StopFailure" : "Stop"
            default: break
            }

        case "windsurf":
            e.sessionId = s("trajectory_id")
            switch s("agent_action_name") {
            case "pre_user_prompt": e.name = "UserPromptSubmit"
            case "post_cascade_response": e.name = "Stop"; e.lastAssistant = h.obj("tool_info")?.str("response") ?? ""
            default: break
            }

        case "opencode":
            // Sent by our own plugin: {"event": "...", "session": "...", "cwd": "..."}
            e.sessionId = s("session")
            e.cwd = s("cwd")
            switch s("event") {
            case "busy": e.name = "UserPromptSubmit"
            case "idle": e.name = "Stop"
            case "error": e.name = "StopFailure"
            case "permission":
                e.name = "Notification"
                e.notificationType = "permission_prompt"
                e.message = s("message").isEmpty ? "OpenCode is asking for permission." : s("message")
            default: break
            }

        default:   // Claude Code
            e.name = s("hook_event_name")
            e.sessionId = s("session_id")
            e.cwd = s("cwd")
            e.transcript = s("transcript_path")
            e.message = s("message")
            e.notificationType = s("notification_type")
            e.lastAssistant = s("last_assistant_message")
            e.agentId = s("agent_id")
            e.agentType = s("agent_type")
        }
        // No folder in the payload: the hook runs in the agent's working folder, which is the
        // next best guess. A guess never replaces a folder the session already has.
        if e.cwd.isEmpty, let pwd = pwd, !pwd.isEmpty { e.cwd = pwd; e.cwdGuessed = true }
        // Session ids are only unique per agent.
        if !e.sessionId.isEmpty && agent != "claude" { e.sessionId = agent + ":" + e.sessionId }
    }

    // The executable a hook command runs, with the shell quoting undone.
    static func exeOf(_ command: String) -> String {
        var exe = command
        if let cut = command.range(of: " --hook", options: .backwards) { exe = String(command[..<cut.lowerBound]) }
        exe = exe.trimmingCharacters(in: .whitespaces)
        if exe.hasPrefix("'") && exe.hasSuffix("'") && exe.count >= 2 {
            exe = String(exe.dropFirst().dropLast()).replacingOccurrences(of: "'\\''", with: "'")
        }
        return exe
    }

    // OpenCode loads plugins from ~/.config/opencode/plugins. This one forwards session state
    // changes to the same --hook entry point the other agents use.
    static func openCodePlugin(_ command: String) -> String {
        let exe = exeOf(command)
        return """
        // Aevalsistant: tells the menu bar app when an OpenCode session starts and stops working.
        // Written by Aevalsistant; removed by "Remove from this Mac" or by turning off agent hooks.
        const EXE = \(Json.quote(exe));

        export const Aevalsistant = async ({ directory }) => {
          const send = (event, session, message) => {
            try {
              const body = JSON.stringify({ event, session: session || "", cwd: directory || "", message: message || "" });
              Bun.spawn([EXE, "--hook", "opencode"], { stdin: new Blob([body]), stdout: "ignore", stderr: "ignore" });
            } catch (e) {
              // Aevalsistant is not installed or not reachable; OpenCode carries on.
            }
          };
          // session.status and the older session.idle both report a finish, so a finish is sent only
          // for a session that was busy, and only once.
          const busy = {};
          return {
            event: async ({ event }) => {
              const p = event.properties || {};
              const id = p.sessionID || (p.info && p.info.id) || "";
              const status = event.type === "session.status" && p.status ? p.status.type : "";
              if (status === "busy") { if (!busy[id]) { busy[id] = true; send("busy", id); } }
              else if (status === "idle" || event.type === "session.idle") { if (busy[id]) { busy[id] = false; send("idle", id); } }
              else if (event.type === "session.error") { busy[id] = false; send("error", id); }
              else if (event.type === "permission.asked") {
                const what = (p.permission || "") + (p.patterns && p.patterns.length ? ": " + p.patterns.join(", ") : "");
                send("permission", id, what ? "OpenCode wants to use " + what : "");
              }
            },
          };
        };

        """
    }
}

// The GitHub release the updater reads, trimmed to what it needs. No networking here, so the
// rules are testable anywhere.
enum Release {
    // "v1.2.0" -> [1, 2, 0]
    static func parseTag(_ tag: String?) -> [Int]? {
        guard var t = tag, !t.isEmpty else { return nil }
        while t.first == "v" || t.first == "V" { t.removeFirst() }
        let parts = t.split(separator: ".", omittingEmptySubsequences: false).map { Int($0) }
        guard !parts.isEmpty, parts.count <= 4, parts.allSatisfy({ $0 != nil && $0! >= 0 }) else { return nil }
        var v = parts.map { $0! }
        while v.count < 3 { v.append(0) }
        return Array(v.prefix(3))
    }

    static func newer(_ a: [Int], than b: [Int]) -> Bool {
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0, y = i < b.count ? b[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    // The asset's download URL and its lowercase SHA-256 from the release's asset list. The hash
    // is nil when GitHub gave no "sha256:" digest.
    static func findAsset(_ release: JObj?, _ name: String) -> (url: String, sha256: String?)? {
        guard let assets = release?["assets"] as? JArr else { return nil }
        for case let o as JObj in assets.items where o.str("name") == name {
            guard let url = o.str("browser_download_url") else { return nil }
            let digest = o.str("digest") ?? ""
            let sha = digest.lowercased().hasPrefix("sha256:") && digest.count == 7 + 64 ? String(digest.dropFirst(7)).lowercased() : nil
            return (url, sha)
        }
        return nil
    }
}
