import Foundation

// The checks from tests/Tests.cs and tests/HierarchyTests.cs, ported to the Swift side. They use
// Foundation only, so they also run under Swift on Linux.
var failed = 0, passed = 0

func check(_ ok: Bool, _ name: String) {
    if ok { passed += 1 } else { failed += 1; print("FAIL " + name) }
}

func apply(_ a: AgentSpec, _ text: String?, _ command: String?) -> (text: String?, changed: Bool) {
    do { return try Agents.apply(a, text, command) } catch { return (nil, false) }
}

func claudeApply(_ text: String?, _ command: String?) -> (String, Bool) {
    let r = apply(Agents.get("claude"), text, command)
    return (r.text ?? "", r.changed)
}

func parseObj(_ s: String) -> JObj { (Json.tryParse(s) as? JObj) ?? JObj() }

func secs(_ table: [String: Double], _ key: String) -> Double { table[key] ?? -1 }

func occurrences(_ s: String, _ of: String) -> Int { s.components(separatedBy: of).count - 1 }

enum CoreTests {
    static func run() {
        jsonRoundTrip()
        hooksInstallMergeRemove()
        sessions()
        transcriptParsing()
        envelope()
        otherAgents()
        hookPaths()
        hierarchy()
        chatWatching()
        settingsDefaults()
        updateAssets()
        easing()
        watchedApps()
        timeLog()
        notifyOptions()
        agentCommands()
        awakeHolds()
        agentGuide()
    }

    static func jsonRoundTrip() {
        let src = "{\n  \"b\": 1,\n  \"a\": [\n    1.50,\n    -2e3,\n    true,\n    null,\n    \"x\\\"y\\u00e9\\n\"\n  ],\n  \"c\": {}\n}"
        let v = try? Json.parse(src)
        let expected = src.replacingOccurrences(of: "\\u00e9", with: "\u{e9}")   // escapes are decoded; everything else is byte-identical
        check(v.map(Json.write) == expected, "json: order, number text, strings survive a round trip")
        check(Json.tryParse("{\"a\":1,}") == nil, "json: trailing comma rejected")
        check(Json.tryParse("{\"a\":1} x") == nil, "json: trailing garbage rejected")
        check(Json.tryParse("\u{FEFF}{}") != nil, "json: BOM tolerated")
        check((Json.tryParse("\"\\ud83d\\ude00\"") as? String) == "\u{1F600}", "json: surrogate pair escapes decode")
    }

    static let cmd = "/Applications/Aevalsistant.app/Contents/MacOS/Aevalsistant --hook"

    static func hooksInstallMergeRemove() {
        // fresh install, no file
        let (a, ch1) = claudeApply(nil, cmd)
        check(ch1 && Agents.isInstalled(Agents.get("claude"), a, cmd), "hooks: install into missing settings")

        // user settings with their own Stop hook, other keys, and odd numbers
        let user = "{\n  \"model\": \"opus\",\n  \"cleanupPeriodDays\": 30,\n  \"hooks\": {\n    \"Stop\": [\n      {\n        \"hooks\": [\n          {\n            \"type\": \"command\",\n            \"command\": \"~/.claude/my-stop.sh\",\n            \"timeout\": 5\n          }\n        ]\n      }\n    ],\n    \"PreToolUse\": [\n      {\n        \"matcher\": \"Bash\",\n        \"hooks\": [ { \"type\": \"command\", \"command\": \"guard.sh\" } ]\n      }\n    ]\n  },\n  \"ratio\": 1.50\n}"
        let (b, ch2) = claudeApply(user, cmd)
        let root = parseObj(b)
        let stop = root.obj("hooks")?.arr("Stop")
        check(ch2 && Agents.isInstalled(Agents.get("claude"), b, cmd), "hooks: install alongside user hooks")
        check(stop?.items.count == 2 && b.contains("~/.claude/my-stop.sh") && b.contains("guard.sh"), "hooks: user hooks kept")
        check(b.contains("\"ratio\": 1.50") && b.contains("\"cleanupPeriodDays\": 30"), "hooks: numbers kept verbatim")
        check(root.items.first?.key == "model" && root.items.last?.key == "ratio", "hooks: key order kept")
        check(b.contains("\"async\": true"), "hooks: async set")
        let end = ((root.obj("hooks")?.arr("SessionEnd")?.items.first as? JObj)?.arr("hooks")?.items.first as? JObj)
        check(end != nil && end?["async"] == nil, "hooks: SessionEnd stays synchronous")

        // idempotent
        check(!claudeApply(b, cmd).1, "hooks: second install is a no-op")

        // moved app: old entry replaced, not duplicated
        let moved = "/Users/andy/Applications/Aevalsistant.app/Contents/MacOS/Aevalsistant --hook"
        let (c, ch4) = claudeApply(b, moved)
        check(ch4 && occurrences(c, "--hook") == Agents.get("claude").events.count && !c.contains("\"/Applications/"), "hooks: path change replaces entries")

        // a settings folder synced from a Windows PC: its entries are recognized as ours too
        let windows = claudeApply(nil, "C:/Users/andy/AppData/Local/Aevalsistant/Aevalsistant.exe --hook").0
        let both = claudeApply(windows, cmd).0
        check(occurrences(both, "--hook") == Agents.get("claude").events.count && !both.contains("AppData"), "hooks: Windows entries are replaced, not doubled")

        // uninstall leaves only the user's hooks
        let (d, ch5) = claudeApply(c, nil)
        check(ch5 && !d.contains("--hook") && d.contains("my-stop.sh") && parseObj(d).obj("hooks")?["UserPromptSubmit"] == nil, "hooks: uninstall removes only ours")

        // uninstall when ours were the only hooks drops the hooks key
        let e = claudeApply(claudeApply("{\"model\":\"opus\"}", cmd).0, nil).0
        check(parseObj(e)["hooks"] == nil, "hooks: empty hooks object removed")

        // broken input is refused, never overwritten
        check((try? Agents.apply(Agents.get("claude"), "{ \"model\": ", cmd)) == nil, "hooks: invalid JSON refused")
        check((try? Agents.apply(Agents.get("claude"), "{\"hooks\": []}", cmd)) == nil, "hooks: non-object hooks refused")
        check(!Agents.isOurs("~/.claude/aevalsistant-notes.sh") && Agents.isOurs(cmd), "hooks: ownership test")
    }

    static func ev(_ name: String, _ sid: String = "s1", at: Int64 = 0, msg: String = "", type: String = "", last: String = "") -> HookEvent {
        let e = HookEvent(name, sid)
        e.cwd = "/Users/andy/code/aevalrena"
        e.message = msg
        e.notificationType = type
        e.lastAssistant = last
        e.at = at
        e.appPid = 42
        e.pid = 7
        e.pidStart = 99
        return e
    }

    static func sessions() {
        let t = Date(timeIntervalSince1970: 1_791_374_400)
        let book = SessionBook()
        check(book.apply(ev("UserPromptSubmit", at: 10), t) == nil && book.busyCount == 1, "sessions: prompt starts work")

        let perm = book.apply(ev("Notification", msg: "Claude needs your permission to use Bash", type: "permission_prompt"), t)
        check(perm != nil && perm!.kind == .needsYou && book.busyCount == 1 && book.get("s1")!.blocked, "sessions: permission prompt toasts, stays awake")

        let done = book.apply(ev("Stop", at: 20, last: "**Done.** Tests pass."), t)
        check(done != nil && done!.host == "" && done!.kind == .done && done!.title == "aevalrena finished" && done!.detail == "Done. Tests pass." && book.busyCount == 0,
              "sessions: stop toasts with snippet")
        check(book.apply(ev("Notification", msg: "Claude is waiting for your input", type: "idle_prompt"), t) == nil, "sessions: idle prompt after stop is not repeated")

        // async reordering: a prompt that started before the stop arrives late
        check(book.apply(ev("UserPromptSubmit", at: 15), t) == nil && book.busyCount == 0, "sessions: stale prompt ignored")
        _ = book.apply(ev("UserPromptSubmit", at: 30), t)
        check(book.busyCount == 1, "sessions: newer prompt counts")
        check(book.apply(ev("Stop", at: 25), t) == nil && book.busyCount == 1, "sessions: stale stop ignored")

        let idle = book.apply(ev("Notification", msg: "Claude is waiting for your input"), t)
        check(idle != nil && idle!.title == "aevalrena is waiting" && book.busyCount == 0, "sessions: idle prompt without stop toasts once (type guessed)")

        let fail = book.apply(ev("StopFailure", at: 40), t)
        check(fail != nil && fail!.kind == .needsYou, "sessions: stop failure toasts")

        // sweep: interrupt in transcript ends work without a toast
        _ = book.apply(ev("UserPromptSubmit", "s2", at: 50), t)
        let changed = book.sweep(t.addingTimeInterval(5), alive: { _ in true }, probe: { s in TranscriptProbe(modified: t.addingTimeInterval(3), endsInInterrupt: s.id == "s2") })
        check(changed && book.get("s2")!.state == .waiting, "sweep: interrupt detected")

        // sweep: stale work times out
        _ = book.apply(ev("UserPromptSubmit", "s3", at: 60), t)
        _ = book.sweep(t.addingTimeInterval(44 * 60), alive: { _ in true }, probe: { _ in nil })
        check(book.get("s3")!.state == .working, "sweep: 44 minutes is not stale")
        _ = book.sweep(t.addingTimeInterval(46 * 60), alive: { _ in true }, probe: { _ in nil })
        check(book.get("s3")!.state == .waiting, "sweep: 46 minutes is stale")

        // sweep: dead process is forgotten
        _ = book.sweep(t.addingTimeInterval(47 * 60), alive: { $0.id != "s3" }, probe: { _ in nil })
        check(book.get("s3") == nil && book.get("s1") != nil, "sweep: dead claude process removed")

        // subagents: a background one outlives the parent's turn and keeps the Mac awake
        let sb = SessionBook()
        func sub(_ name: String, _ id: String) -> HookEvent {
            let e = HookEvent(name, "p")
            e.agentId = id
            e.cwd = "/Users/a/code/forge"
            e.transcript = "/Users/a/.claude/projects/x/p.jsonl"
            return e
        }
        let start = HookEvent("UserPromptSubmit", "p")
        start.cwd = "/Users/a/code/forge"
        start.transcript = "/Users/a/.claude/projects/x/p.jsonl"
        _ = sb.apply(start, t)
        _ = sb.apply(sub("SubagentStart", "a1"), t)
        _ = sb.apply(sub("SubagentStart", "a2"), t)
        check(Status.text(sb.get("p")!, t.addingTimeInterval(180)) == "working 3m, 2 subagents", "subagents: counted while the parent works")
        _ = sb.apply(sub("SubagentStop", "a1"), t)
        let waitReq = sb.apply(HookEvent("Stop", "p"), t)
        check(waitReq?.title == "forge is waiting" && sb.busyCount == 1 && Status.text(sb.get("p")!, t) == "1 subagent running"
              && Status.state(sb.get("p")!) == .working, "subagents: background one keeps the session busy after Stop")
        check(sb.get("p")!.subagentTranscript("a2").hasSuffix("projects/x/p/subagents/agent-a2.jsonl"), "subagents: transcript path")
        _ = sb.sweep(t.addingTimeInterval(3600), alive: { _ in true }, probe: { _ in nil }, subagentWrite: { _, _ in t.addingTimeInterval(50 * 60) })
        check(sb.busyCount == 1, "subagents: recent transcript write keeps it alive")
        _ = sb.sweep(t.addingTimeInterval(100 * 60), alive: { _ in true }, probe: { _ in nil }, subagentWrite: { _, _ in t.addingTimeInterval(50 * 60) })
        check(sb.busyCount == 0, "subagents: silent for 45 minutes is dropped")
        _ = sb.apply(sub("SubagentStop", "a3"), t)
        _ = sb.apply(sub("SubagentStart", "a3"), t)
        check(sb.busyCount == 0, "subagents: stop that arrived before its start cancels it")
        check(Status.text(sb.get("p")!, t.addingTimeInterval(240)) == "done 4m ago", "status: done age")

        _ = book.apply(ev("SessionEnd"), t)
        check(book.get("s1") == nil, "sessions: session end removes")

        func project(_ cwd: String) -> String { let s = Session(id: "x"); s.cwd = cwd; return s.project }
        check(project("/Users/andy/code/kymarion/") == "kymarion" && project("") == "Claude Code" && project("/") == "Claude Code"
              && project("C:\\code\\rover") == "rover", "sessions: project names")
    }

    static func transcriptParsing() {
        var lines = [
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"fix the build\"}}",
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Looking at the build.\"}]}}",
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\"}]}}",
            "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"ok\"}]}}",
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"thinking\",\"thinking\":\"\"}]}}",
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"## Fixed\\nThe `tsconfig` path was wrong.\"}]}}",
            "{\"type\":\"ai-title\",\"title\":\"x\"}",
            "not json at all",
        ]
        check(Snippet.clean(Transcript.lastAssistantText(lines)) == "Fixed The tsconfig path was wrong.", "transcript: last text, markdown stripped")
        check(!Transcript.endsInInterrupt(lines), "transcript: no interrupt")
        lines.append("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"[Request interrupted by user for tool use]\"}]}}")
        lines.append("{\"type\":\"last-prompt\"}")
        check(Transcript.endsInInterrupt(lines), "transcript: interrupt detected past metadata lines")

        // tail read: a file larger than the tail window starts on a whole line
        let path = NSTemporaryDirectory() + "/aeval-tail-test.jsonl"
        var text = ""
        for i in 0..<6000 { text += "{\"type\":\"user\",\"message\":{\"content\":\"padding line number \(i)\"}}\n" }
        text += "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"tail works\"}]}}\n"
        try? text.write(toFile: path, atomically: true, encoding: .utf8)
        let tail = Transcript.tailLines(path)
        let size = ((try? FileManager.default.attributesOfItem(atPath: path))?[.size] as? NSNumber)?.intValue ?? 0
        check(size > 256 * 1024 && tail.allSatisfy { $0.hasPrefix("{") } && Transcript.lastAssistantText(tail) == "tail works", "transcript: tail of large file")
        try? FileManager.default.removeItem(atPath: path)

        // the real transcript of a Claude Code session, if one is given
        if let real = ProcessInfo.processInfo.environment["AEVAL_REAL_TRANSCRIPT"], FileManager.default.fileExists(atPath: real) {
            let rl = Transcript.tailLines(real)
            let typed = rl.filter { (Json.tryParse($0) as? JObj)?.str("type") != nil }.count
            check(!rl.isEmpty && typed == rl.count, "transcript: every line of a real Claude Code transcript parses")
        }
    }

    static func envelope() {
        let raw = "{\"session_id\":\"abc\",\"hook_event_name\":\"Stop\",\"cwd\":\"/Users/a/code/forge\",\"transcript_path\":\"/t.jsonl\",\"last_assistant_message\":\"All green.\"}"
        let env = "{\"t\":1791374400000000,\"host\":\"Android Studio\",\"app\":132456,\"pid\":4242,\"pidStart\":1337,\"raw\":" + Json.quote(raw) + "}"
        let e = HookEvent.fromEnvelope(env)
        check(e != nil && e!.name == "Stop" && e!.appPid == 132456 && e!.pid == 4242 && e!.at == 1_791_374_400_000_000 && e!.cwd == "/Users/a/code/forge"
              && e!.lastAssistant == "All green." && e!.host == "Android Studio", "envelope: parsed")
        check(HookEvent.fromEnvelope("{\"raw\":\"{}\"}") == nil && HookEvent.fromEnvelope("garbage") == nil, "envelope: junk ignored")
    }

    static let base = "/Applications/Aevalsistant.app/Contents/MacOS/Aevalsistant --hook"

    static func env(_ agent: String, _ raw: String, _ ev: String = "", pwd: String = "/Users/a/code/fallback") -> HookEvent? {
        HookEvent.fromEnvelope("{\"t\":1,\"agent\":" + Json.quote(agent) + ",\"event\":" + Json.quote(ev) + ",\"pwd\":" + Json.quote(pwd)
            + ",\"host\":\"Terminal\",\"app\":7,\"pid\":0,\"pidStart\":0,\"raw\":" + Json.quote(raw) + "}")
    }

    static func otherAgents() {
        // installers: every agent installs, is idempotent, keeps the user's own hooks, and removes cleanly
        for a in Agents.all {
            let command = Agents.commandFor(a, base)
            let fresh = apply(a, nil, command)
            check(fresh.changed && Agents.isInstalled(a, fresh.text, command), "install: \(a.id) into a missing file")
            check(!apply(a, fresh.text, command).changed, "install: \(a.id) twice is a no-op")
            let removed = apply(a, fresh.text, nil)
            check(removed.changed && (a.layout == .own ? removed.text == nil : !(removed.text ?? "--hook").contains("--hook")), "install: \(a.id) removes cleanly")
        }

        let codex = Agents.get("codex")
        let userCodex = "{\n  \"hooks\": {\n    \"PreToolUse\": [\n      {\n        \"matcher\": \"Bash\",\n        \"hooks\": [\n          {\n            \"type\": \"command\",\n            \"command\": \"python3 ~/.codex/hooks/check.py\"\n          }\n        ]\n      }\n    ]\n  }\n}"
        let withOurs = apply(codex, userCodex, Agents.commandFor(codex, base)).text ?? ""
        check(withOurs.contains("check.py") && withOurs.contains("--hook codex") && withOurs.contains("\"Interrupt\""), "install: codex keeps the user's PreToolUse hook")
        let codexRemoved = apply(codex, withOurs, nil).text ?? ""
        check(!codexRemoved.contains("--hook") && codexRemoved.contains("check.py"), "install: codex removal keeps theirs")

        let cursor = Agents.get("cursor")
        let cur = apply(cursor, "{\"version\": 1, \"hooks\": {\"stop\": [{\"command\": \"./mine.sh\"}]}}", Agents.commandFor(cursor, base)).text ?? ""
        check(parseObj(cur).obj("hooks")?.arr("stop")?.items.count == 2 && cur.contains("mine.sh") && cur.contains("--hook cursor"), "install: cursor flat list keeps theirs")
        check(parseObj(apply(cursor, nil, "x --hook cursor").text ?? "")["version"] is JNum, "install: cursor file gets a version")
        let ws = apply(Agents.get("windsurf"), nil, "w --hook windsurf").text ?? ""
        check(ws.contains("\"command\": \"w --hook windsurf\"") && !ws.contains("powershell"), "install: windsurf runs a plain command")
        let cop = apply(Agents.get("copilot"), nil, Agents.commandFor(Agents.get("copilot"), base)).text ?? ""
        check(cop.contains("--hook copilot agentStop") && cop.contains("\"bash\": ") && cop.contains("\"version\": 1"), "install: copilot names the event on the command line, in bash")
        let oc = apply(Agents.get("opencode"), nil, Agents.commandFor(Agents.get("opencode"), base)).text ?? ""
        check(oc.contains("const EXE = \"/Applications/Aevalsistant.app/Contents/MacOS/Aevalsistant\"") && oc.contains("session.idle"), "install: opencode plugin points at the app")
        check(Agents.commandFor(Agents.get("claude"), base) == base, "install: claude keeps the bare command")

        // adapters
        var e = env("codex", "{\"session_id\":\"s9\",\"cwd\":\"/r/rover\",\"hook_event_name\":\"Stop\",\"turn_id\":\"t\",\"last_assistant_message\":\"Patched it.\"}")
        check(e != nil && e!.name == "Stop" && e!.sessionId == "codex:s9" && e!.cwd == "/r/rover" && e!.lastAssistant == "Patched it.", "adapter: codex stop")
        e = env("codex", "{\"session_id\":\"s9\",\"hook_event_name\":\"PermissionRequest\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"rm x\"}}")
        check(e?.name == "Notification" && e?.notificationType == "permission_prompt" && e?.message == "Codex wants to use Bash" && e?.cwd == "/Users/a/code/fallback",
              "adapter: codex permission, cwd falls back to the hook's folder")
        e = env("gemini", "{\"session_id\":\"g\",\"cwd\":\"/w/site\",\"hook_event_name\":\"AfterAgent\",\"prompt\":\"p\",\"prompt_response\":\"Built the page.\"}")
        check(e?.name == "Stop" && e?.sessionId == "gemini:g" && e?.lastAssistant == "Built the page.", "adapter: gemini AfterAgent")
        check(env("gemini", "{\"session_id\":\"g\",\"hook_event_name\":\"BeforeAgent\",\"prompt\":\"p\"}")?.name == "UserPromptSubmit", "adapter: gemini BeforeAgent")
        e = env("copilot", "{\"sessionId\":\"c1\",\"timestamp\":1,\"cwd\":\"/x/api\",\"transcriptPath\":\"t\",\"stopReason\":\"end_turn\"}", "agentStop")
        check(e?.name == "Stop" && e?.sessionId == "copilot:c1", "adapter: copilot agentStop")
        check(env("copilot", "{\"sessionId\":\"c1\",\"error\":{\"message\":\"m\"},\"recoverable\":true}", "errorOccurred") == nil, "adapter: copilot recoverable error ignored")
        e = env("cursor", "{\"conversation_id\":\"k\",\"generation_id\":\"g\",\"hook_event_name\":\"stop\",\"status\":\"aborted\",\"workspace_roots\":[\"/c/app\"]}")
        check(e?.name == "Interrupt" && e?.cwd == "/c/app" && e?.sessionId == "cursor:k", "adapter: cursor aborted stop is an interrupt")
        e = env("windsurf", "{\"agent_action_name\":\"post_cascade_response\",\"trajectory_id\":\"tr\",\"tool_info\":{\"response\":\"## Done\\nAll set.\"}}")
        check(e?.name == "Stop" && e?.sessionId == "windsurf:tr" && (e?.lastAssistant.contains("All set") ?? false), "adapter: windsurf response")
        e = env("opencode", "{\"event\":\"permission\",\"session\":\"o1\",\"cwd\":\"/p/q\",\"message\":\"Run npm test?\"}")
        check(e?.name == "Notification" && e?.message == "Run npm test?" && e?.sessionId == "opencode:o1", "adapter: opencode permission")
        check(env("gemini", "{\"session_id\":\"g\",\"hook_event_name\":\"Notification\",\"notification_type\":\"ToolPermission\",\"message\":\"Allow shell?\"}")?.notificationType == "permission_prompt"
              && env("gemini", "{\"session_id\":\"g\",\"hook_event_name\":\"Notification\",\"notification_type\":\"Error\",\"message\":\"Quota\"}") == nil,
              "adapter: gemini only treats tool permission notices as needing you")

        // session flow for an agent that sends its reply before Stop
        let t = Date(timeIntervalSince1970: 1_791_424_800)
        let book = SessionBook()
        _ = book.apply(env("cursor", "{\"conversation_id\":\"k\",\"hook_event_name\":\"beforeSubmitPrompt\",\"prompt\":\"x\",\"workspace_roots\":[\"/c/app\"]}")!, t)
        _ = book.apply(env("cursor", "{\"conversation_id\":\"k\",\"hook_event_name\":\"afterAgentResponse\",\"text\":\"Renamed the module.\"}")!, t)
        let r = book.apply(env("cursor", "{\"conversation_id\":\"k\",\"hook_event_name\":\"stop\",\"status\":\"completed\"}")!, t)
        check(r?.title == "app finished" && r?.detail == "Renamed the module." && r?.host == "Cursor \u{B7} Terminal", "flow: cursor reply text and agent label")
        _ = book.apply(env("codex", "{\"session_id\":\"z\",\"cwd\":\"/r/x\",\"hook_event_name\":\"UserPromptSubmit\"}")!, t)
        check(book.apply(env("codex", "{\"session_id\":\"z\",\"hook_event_name\":\"Interrupt\"}")!, t) == nil && book.get("codex:z")?.state == .waiting,
              "flow: codex interrupt ends work without a toast")
        let gstop = book.apply(env("copilot", "{\"sessionId\":\"q\",\"cwd\":\"/a/b\",\"stopReason\":\"end_turn\"}", "agentStop")!, t)
        check(gstop?.detail == "Ready for your next prompt." && gstop?.host == "Copilot CLI \u{B7} Terminal" && gstop?.wantsSnippet == false, "flow: agent without reply text")
    }

    // Hook commands run through sh, bash, or zsh, so a path with a space or a quote is single-quoted.
    static func hookPaths() {
        let plain = "/Users/andy/Applications/Aevalsistant.app/Contents/MacOS/Aevalsistant"
        check(Agents.hookPath(plain) == plain, "hook path: a plain path is left alone")
        let spaced = Agents.hookPath("/Volumes/Work Disk/Aevalsistant.app/Contents/MacOS/Aevalsistant")
        check(spaced == "'/Volumes/Work Disk/Aevalsistant.app/Contents/MacOS/Aevalsistant'", "hook path: a space is single-quoted")
        let quote = Agents.hookPath("/Users/o'brien/Applications/Aevalsistant.app/Contents/MacOS/Aevalsistant")
        check(quote == "'/Users/o'\\''brien/Applications/Aevalsistant.app/Contents/MacOS/Aevalsistant'", "hook path: an apostrophe is escaped the POSIX way")
        check(Agents.isOurs(spaced + " --hook") && Agents.exeOf(quote + " --hook codex") == "/Users/o'brien/Applications/Aevalsistant.app/Contents/MacOS/Aevalsistant",
              "hook path: quoted commands are still ours, and the plugin gets the bare path back")
        let once = claudeApply(nil, spaced + " --hook").0
        let twice = claudeApply(once, spaced + " --hook")
        check(!twice.1 && twice.0 == once, "hook path: syncing a quoted path again adds nothing")
    }

    static func at(_ name: String, _ sid: String, _ cwd: String, _ pid: Int32, outer: Int32 = 0, agentId: String = "", agentType: String = "") -> HookEvent {
        let e = HookEvent(name, sid)
        e.cwd = cwd
        e.pid = pid
        e.pidStart = Int64(pid) * 10
        e.outerPid = outer
        e.outerStart = Int64(outer) * 10
        e.agentId = agentId
        e.agentType = agentType
        return e
    }

    // Subagents and sessions that another agent started are listed under their session, and
    // what happens in the background does not ask for a card unless settings say so.
    static func hierarchy() {
        let t = Date(timeIntervalSince1970: 1_791_450_000)
        let book = SessionBook()
        _ = book.apply(at("UserPromptSubmit", "p", "/c/aevalrena", 100), t)
        _ = book.apply(at("SubagentStart", "p", "/c/aevalrena", 100, agentId: "a1", agentType: "Explore"), t.addingTimeInterval(1))
        _ = book.apply(at("SubagentStart", "p", "/c/aevalrena", 100, agentId: "a2", agentType: "general-purpose"), t.addingTimeInterval(2))
        _ = book.apply(at("UserPromptSubmit", "c", "/c/docs-build", 200, outer: 100), t.addingTimeInterval(3))
        _ = book.apply(at("UserPromptSubmit", "o", "/c/kymarion", 300), t.addingTimeInterval(4))

        check(book.parentOf(book.get("c")) === book.get("p") && book.parentOf(book.get("p")) == nil, "tree: a session started from another agent's process has that session as parent")
        func shape(_ rows: [TreeRow]) -> String {
            rows.map { String(repeating: ">", count: $0.depth) + ($0.subagent?.type ?? $0.session.project) }.joined(separator: " ")
        }
        check(shape(book.tree(nil, nest: true)) == "kymarion aevalrena >Explore >general-purpose >docs-build", "tree: subagents, then started sessions, nest under their session (\(shape(book.tree(nil, nest: true))))")
        check(shape(book.tree(nil, nest: false)) == "kymarion docs-build aevalrena", "tree: nesting off lists every session flat (\(shape(book.tree(nil, nest: false))))")
        check(shape(book.tree("p", nest: true)) == ">Explore >general-purpose >docs-build kymarion", "tree: the card's own session leads with its children")
        check(Status.text(book.get("p")!.subagents["a1"]!, t.addingTimeInterval(241)) == "running 4m", "tree: subagent status")

        // Your own turn ending notifies; a turn the agent takes on its own afterwards is background.
        let done = book.apply(at("Stop", "o", "/c/kymarion", 300), t.addingTimeInterval(60))
        check(done != nil && !done!.background, "background: the turn you prompted is yours")
        let again = book.apply(at("Stop", "o", "/c/kymarion", 300), t.addingTimeInterval(120))
        check(again != nil && again!.background, "background: a second stop with no prompt in between is the agent on its own")
        _ = book.apply(at("UserPromptSubmit", "o", "/c/kymarion", 300), t.addingTimeInterval(180))
        check(book.apply(at("Stop", "o", "/c/kymarion", 300), t.addingTimeInterval(240))?.background == false, "background: a new prompt makes the next stop yours again")

        let child = book.apply(at("Stop", "c", "/c/docs-build", 200, outer: 100), t.addingTimeInterval(60))
        check(child != nil && child!.background, "background: a session another agent started is background")
        let ask = HookEvent("Notification", "c")
        ask.notificationType = "permission_prompt"
        ask.pid = 200; ask.pidStart = 2000; ask.outerPid = 100; ask.outerStart = 1000
        let childAsk = book.apply(ask, t.addingTimeInterval(60))
        check(childAsk != nil && childAsk!.kind == .needsYou && childAsk!.background, "background: its permission prompts are flagged too")

        let waiting = book.apply(at("Stop", "p", "/c/aevalrena", 100), t.addingTimeInterval(120))
        check(waiting?.title == "aevalrena is waiting" && waiting?.background == false && book.get("p")!.busy, "background: the parent's turn ends while its subagents run")
        check(book.apply(at("SubagentStop", "p", "/c/aevalrena", 100, agentId: "a1"), t.addingTimeInterval(180)) == nil, "background: one of two subagents finishing says nothing")
        let last = book.apply(at("SubagentStop", "p", "/c/aevalrena", 100, agentId: "a2"), t.addingTimeInterval(240))
        check(last != nil && last!.background && last!.title == "aevalrena finished" && !book.get("p")!.busy, "background: the last background subagent finishing is a background card")

        _ = book.apply(at("UserPromptSubmit", "p", "/c/aevalrena", 100), t.addingTimeInterval(300))
        _ = book.apply(at("SubagentStart", "p", "/c/aevalrena", 100, agentId: "a3"), t.addingTimeInterval(300))
        check(book.apply(at("SubagentStop", "p", "/c/aevalrena", 100, agentId: "a3"), t.addingTimeInterval(360)) == nil, "background: a subagent finishing mid-turn is part of the turn")

        let s = Settings()
        let fg = ToastRequest(kind: .done, title: "", detail: "")
        let bg = ToastRequest(kind: .done, title: "", detail: ""); bg.background = true
        let bgAsk = ToastRequest(kind: .needsYou, title: "", detail: ""); bgAsk.background = true
        check(s.wants(fg) && !s.wants(bg) && s.wants(bgAsk), "settings: by default background work only asks for you when it needs you")
        s.notifyBackgroundDone = true
        check(s.wants(bg), "settings: background finishes can be turned on")
        s.notifyDone = false
        check(!s.wants(fg) && !s.wants(bg) && s.wants(ToastRequest(kind: .info, title: "", detail: "")), "settings: turning off finish cards also silences their background ones")
        let short = Settings(); short.cardTime = 0
        check(abs(short.seconds(.done) - 4.8) < 0.01 && Settings().seconds(.needsYou) == 10, "settings: card time scales the defaults")

        let path = NSTemporaryDirectory() + "/aeval-settings-test.ini"
        let saved = Settings()
        saved.keepAwake = false; saved.sound = true; saved.cardTime = 2; saved.showChildren = false; saved.notifyBackgroundDone = true; saved.lidAwake = false
        saved.agentsOff = ["cursor", "codex"]
        saved.save(path)
        let loaded = Settings.load(path)
        try? FileManager.default.removeItem(atPath: path)
        check(!loaded.keepAwake && loaded.sound && loaded.cardTime == 2 && !loaded.showChildren && loaded.notifyBackgroundDone && !loaded.lidAwake
              && loaded.agentsOff == ["codex", "cursor"] && loaded.notifyNeedsYou && loaded.expandOnHover, "settings: options survive a save and load")

        let e = HookEvent.fromEnvelope("{\"t\":1,\"agent\":\"claude\",\"event\":\"\",\"pwd\":\"\",\"host\":\"\",\"app\":0,\"pid\":200,\"pidStart\":5,\"outer\":100,\"outerStart\":4,\"raw\":"
            + Json.quote("{\"session_id\":\"x\",\"hook_event_name\":\"SubagentStart\",\"agent_id\":\"a\",\"agent_type\":\"Plan\"}") + "}")
        check(e != nil && e!.outerPid == 100 && e!.outerStart == 4 && e!.agentType == "Plan", "envelope: outer agent process and subagent type")
    }

    static func chatWatching() {
        let tr = ChatTracker()
        check(tr.observe(100, "Claude", exists: true, generating: false, foreground: false) == nil, "chat: idle app is ignored")
        let start = tr.observe(100, "Claude", exists: true, generating: true, foreground: false)
        check(start?.name == "UserPromptSubmit" && start?.label == "Chat" && start?.host == "Claude" && start?.agent == "chat", "chat: Stop button starts a session")
        check(tr.observe(100, "Claude", exists: true, generating: nil, foreground: false) == nil
              && tr.observe(100, "Claude", exists: true, generating: false, foreground: false) == nil, "chat: one miss is not the end")
        check(tr.observe(100, "Claude", exists: true, generating: false, foreground: false)?.name == "Stop", "chat: two misses end it with a notification")
        _ = tr.observe(200, "ChatGPT", exists: true, generating: true, foreground: false)
        _ = tr.observe(200, "ChatGPT", exists: true, generating: false, foreground: true)
        check(tr.observe(200, "ChatGPT", exists: true, generating: false, foreground: true)?.name == "Interrupt", "chat: finished while in front, no notification")
        _ = tr.observe(300, "ChatGPT", exists: true, generating: true, foreground: false)
        check(tr.observe(300, "ChatGPT", exists: false, generating: nil, foreground: false)?.name == "SessionEnd", "chat: quit app ends the session")

        let book = SessionBook()
        let t = Date()
        _ = book.apply(ChatTracker().observe(1, "Claude", exists: true, generating: true, foreground: false)!, t)
        let tr2 = ChatTracker()
        _ = tr2.observe(1, "Claude", exists: true, generating: true, foreground: false)
        _ = tr2.observe(1, "Claude", exists: true, generating: false, foreground: false)
        let req = book.apply(tr2.observe(1, "Claude", exists: true, generating: false, foreground: false)!, t)
        check(req?.title == "Chat finished" && req?.host == "Claude" && req?.detail == "Ready for your next prompt.", "chat: card reads Chat finished · Claude")
    }

    // The Mac app starts with the settings the owner runs on Windows (their screenshot of
    // Settings, 2026-10-21): everything awake, lid closed allowed, no sound.
    static func settingsDefaults() {
        let s = Settings()
        check(s.keepAwake && s.displayOn && s.lidAwake, "defaults: keep awake, screen on, and lid closed are on")
        check(s.notifyDone && !s.notifyBackgroundDone && s.notifyNeedsYou && s.notifyBackgroundNeedsYou, "defaults: finish and needs-you cards, background only when it needs you")
        check(!s.sound && s.expandOnHover && s.optionTab && s.cardTime == 1, "defaults: no sound, expand on hover, Option-Tab, normal card time")
        check(s.hooks && s.agentsOff.isEmpty && s.showChildren && s.chats && s.startup && s.autoUpdate, "defaults: agents connected, subagents listed, chats, login, updates")
        let path = NSTemporaryDirectory() + "/aeval-defaults-missing.ini"
        try? FileManager.default.removeItem(atPath: path)
        let fresh = Settings.load(path)
        check(fresh.displayOn && fresh.lidAwake && !fresh.sound, "defaults: a missing settings file loads the defaults")
    }

    // Apps that keep the Mac awake while open: Blender by default, saved with their labels.
    static func watchedApps() {
        let d = Settings()
        check(d.watchAppsOn && d.watchApps.count == 1 && d.watchApps[0].name == "blender" && d.watchApps[0].label == "Blender" && d.watchApps[0].on,
              "watch: Blender is watched by default")
        let path = NSTemporaryDirectory() + "/aeval-watch-test.ini"
        let s = Settings()
        s.watchApps = [WatchedApp(name: "blender", label: "Blender", on: false), WatchedApp(name: "obs", label: "OBS, Studio|x", on: true)]
        s.watchAppsOn = false
        s.save(path)
        let back = Settings.load(path)
        check(!back.watchAppsOn && back.watchApps.count == 2 && back.watchApps[0].name == "blender" && !back.watchApps[0].on
              && back.watchApps[1].name == "obs" && back.watchApps[1].label == "OBS  Studio x" && back.watchApps[1].on,
              "watch: apps, labels, and on/off survive a save and load")
        try? "awake=1\n".write(toFile: path, atomically: true, encoding: .utf8)
        check(Settings.load(path).watchApps.map { $0.name } == ["blender"], "watch: a file without the key gets Blender")
        try? "watchApps=\n".write(toFile: path, atomically: true, encoding: .utf8)
        check(Settings.load(path).watchApps.isEmpty, "watch: an empty list stays empty")
        try? "watchApps=Blender|Blender|1,blender|Again|1,|x|1\n".write(toFile: path, atomically: true, encoding: .utf8)
        check(Settings.load(path).watchApps.map { $0.name } == ["blender"], "watch: names are lowercased, duplicates and blanks dropped")

        check(WatchMatch.matches("blender", "Blender") && WatchMatch.matches("blender", "blender.exe") && WatchMatch.matches("blender", "blender-launcher")
              && WatchMatch.matches("blender", "Blender.app") && WatchMatch.matches("blender", "blender_softwaregl"),
              "watch: blender matches its app, exe, and helpers")
        check(!WatchMatch.matches("blender", "blenderx") && !WatchMatch.matches("blender", "myblender") && !WatchMatch.matches("", "blender"),
              "watch: other names do not match")
    }

    static func timeLog() {
        let t0 = Date(timeIntervalSince1970: 1_791_374_400)
        let log = TimeLog()
        check(log.tick(t0, day: "2026-11-20", active: ["agent:claude"]) == 0 && log.today.isEmpty, "time: the first tick counts nothing")
        check(log.tick(t0.addingTimeInterval(5), day: "2026-11-20", active: ["agent:claude", "app:blender"]) == 5
              && secs(log.today, "agent:claude") == 5 && secs(log.total, "app:blender") == 5, "time: a tick adds the gap to each active key")
        check(log.tick(t0.addingTimeInterval(605), day: "2026-11-20", active: ["agent:claude"]) == 60 && secs(log.today, "agent:claude") == 65,
              "time: a long gap (sleep) counts as a minute")
        _ = log.tick(t0.addingTimeInterval(610), day: "2026-11-21", active: ["agent:claude"])
        check(secs(log.today, "agent:claude") == 5 && log.today["app:blender"] == nil && secs(log.total, "agent:claude") == 70, "time: a new day starts today over, not the total")

        let path = NSTemporaryDirectory() + "/aeval-time-test.ini"
        log.total["app:blender"] = 3725
        log.save(path)
        let back = TimeLog.load(path)
        check(back.day == "2026-11-21" && secs(back.today, "agent:claude") == 5 && secs(back.total, "agent:claude") == 70 && secs(back.total, "app:blender") == 3725,
              "time: the log survives a save and load")
        let apps = [WatchedApp(name: "blender", label: "Blender", on: true)]
        check(back.summary(back.total) { TimeKeys.label($0, apps: apps) } == "Blender 1h 2m, Claude Code 1m", "time: summary is longest first with labels")
        check(back.summary(back.today) { TimeKeys.label($0, apps: apps) } == "", "time: under a minute is left out")
        check(TimeKeys.label("agent:chat", apps: []) == "Chat apps" && TimeKeys.label("app:obs", apps: []) == "obs", "time: labels for chat and unknown apps")
        check(Status.duration(30) == "under a minute" && Status.duration(600) == "10m" && Status.duration(3725) == "1h 2m", "time: durations read naturally")
        back.reset()
        check(back.today.isEmpty && back.total.isEmpty, "time: reset clears both")

        let book = SessionBook()
        _ = book.apply(ev("UserPromptSubmit", at: 1), t0)
        book.credit(90)
        check(Status.worked(book.get("s1")!) == "1m worked", "time: busy sessions are credited")
        _ = book.apply(ev("Stop", at: 2), t0)
        book.credit(90)
        check(book.get("s1")!.worked == 90, "time: idle sessions are not credited")
    }

    static func notifyOptions() {
        let s = Settings()
        check(s.notifyMode == 0, "notify: always by default")
        s.notifyMode = 1
        check(!s.notifyDone && s.notifyNeedsYou && s.notifyMode == 1, "notify: needs-me-only turns off finish cards")
        s.notifyMode = 2
        check(!s.notifyDone && s.notifyNeedsYou && s.agentDecides && s.notifyMode == 2, "notify: agent decides keeps needs-you cards and turns off finish cards")
        s.notifyMode = 3
        check(!s.notifyDone && !s.notifyNeedsYou && !s.agentDecides && s.notifyMode == 3, "notify: never turns off both")
        s.notifyMode = 1
        check(!s.agentDecides && s.notifyMode == 1, "notify: leaving agent decides clears it")
        s.notifyMode = 2
        s.notifyDone = true
        check(s.notifyMode == 0, "notify: turning finish cards back on reads as always")
        s.notifyMode = 0
        check(!s.agentDecides, "notify: always clears agent decides")

        let agentCard = ToastRequest(kind: .done, title: "Render done", detail: "")
        agentCard.fromAgent = true
        agentCard.turnSeconds = 5
        s.notifyMode = 2
        let autoDone = ToastRequest(kind: .done, title: "", detail: "")
        check(s.wants(agentCard) && !s.wants(autoDone), "notify: agent decides shows the agent's own cards, not automatic finish cards")
        s.notifyMode = 1
        check(s.wants(agentCard), "notify: an agent's card shows while needs-you cards are on")
        s.notifyMode = 3
        check(!s.wants(agentCard), "notify: never hides the agent's cards too")
        s.notifyMode = 0

        s.minTurn = 1
        func done(_ secs: Double) -> ToastRequest {
            let r = ToastRequest(kind: .done, title: "", detail: "")
            r.turnSeconds = secs
            return r
        }
        check(!s.wants(done(30)) && s.wants(done(120)) && s.wants(done(0)), "notify: short turns skip the finish card")
        let ask = ToastRequest(kind: .needsYou, title: "", detail: "")
        ask.turnSeconds = 5
        check(s.wants(ask), "notify: the turn length never hides a needs-you card")

        let t0 = Date(timeIntervalSince1970: 1_791_374_400)
        let book = SessionBook()
        _ = book.apply(ev("UserPromptSubmit", at: 1), t0)
        let stop = book.apply(ev("Stop", at: 2), t0.addingTimeInterval(200))
        check(stop != nil && abs(stop!.turnSeconds - 200) < 0.01, "notify: a finish card carries the turn length")

        _ = book.apply(ev("UserPromptSubmit", at: 3), t0)
        _ = book.apply(ev("Notification", msg: "Claude needs your permission", type: "permission_prompt"), t0)
        check(book.reminders(t0.addingTimeInterval(100), every: 120).isEmpty, "remind: not before the interval")
        let first = book.reminders(t0.addingTimeInterval(130), every: 120)
        check(first.count == 1 && first[0].kind == .needsYou && first[0].title == "aevalrena still needs you" && first[0].detail == "It has been waiting 2m.",
              "remind: a reminder after the interval")
        check(book.reminders(t0.addingTimeInterval(200), every: 120).isEmpty, "remind: not again right away")
        check(book.reminders(t0.addingTimeInterval(260), every: 120).count == 1, "remind: again after another interval")
        _ = book.apply(ev("Notification", msg: "Claude needs your permission", type: "permission_prompt"), t0.addingTimeInterval(270))
        check(book.reminders(t0.addingTimeInterval(300), every: 120).isEmpty, "remind: a second prompt while blocked keeps the clock")
        _ = book.apply(ev("Stop", at: 4), t0.addingTimeInterval(400))
        check(book.reminders(t0.addingTimeInterval(1000), every: 120).isEmpty, "remind: nothing once the session moves on")
    }

    // The shape of api.github.com/repos/{repo}/releases/latest, trimmed to what the updater reads.
    static func updateAssets() {
        let hash = String(repeating: "a", count: 60) + "B0c9"
        func release(_ assets: String) -> JObj { parseObj("{\"tag_name\":\"v1.3.0\",\"assets\":[" + assets + "]}") }
        func asset(_ name: String, _ digest: String?) -> String {
            "{\"name\":\"" + name + "\",\"browser_download_url\":\"https://example.test/" + name + "\"" + (digest.map { ",\"digest\":" + Json.quote($0) } ?? "") + "}"
        }
        let mac = "Aevalsistant-mac.zip"
        let found = Release.findAsset(release(asset("Aevalsistant.exe", "sha256:" + hash) + "," + asset(mac, "sha256:" + hash)), mac)
        check(found?.url == "https://example.test/" + mac && found?.sha256 == hash.lowercased(), "update: the Mac zip and its sha256 digest found next to the exe")
        check(Release.findAsset(release(asset(mac, nil)), mac).map { $0.sha256 == nil } == true, "update: missing digest reported as no checksum")
        check(Release.findAsset(release(asset(mac, "sha512:" + hash)), mac).map { $0.sha256 == nil } == true, "update: other digest algorithms ignored")
        check(Release.findAsset(release(asset(mac, "sha256:abc")), mac).map { $0.sha256 == nil } == true, "update: truncated digest ignored")
        check(Release.findAsset(release(asset("Aevalsistant.exe", "sha256:" + hash)), mac) == nil, "update: release without the Mac zip")
        check(Release.findAsset(parseObj("{\"tag_name\":\"v1.3.0\"}"), mac) == nil, "update: release without assets")
        check(Release.parseTag("v1.2.0") == [1, 2, 0] && Release.parseTag("1.10") == [1, 10, 0] && Release.parseTag("nightly") == nil
              && Release.newer([1, 10, 0], than: [1, 9, 9]) && !Release.newer([1, 4, 0], than: [1, 4, 0]) && !Release.newer([1, 3, 0], than: [1, 4, 0]),
              "update: release tags compare against the build version")
    }

    // `Aevalsistant --awake 2h "render"` and friends, as the command process parses them.
    static func agentCommands() {
        let good: [(String, Int)] = [("30m", 30), ("2h", 120), ("1h30m", 90), ("45", 45), (" 2H ", 120), ("on", 0)]
        check(good.allSatisfy { AgentRequest.parseMinutes($0.0) == $0.1 }, "agent: durations parse")
        let bad = ["", "0", "25h", "1441", "2x", "h", "999999", "-5", "1d"]
        check(bad.allSatisfy { AgentRequest.parseMinutes($0) == -1 } && AgentRequest.parseMinutes("24h") == 1440,
              "agent: bad or out-of-range durations rejected")

        let a = AgentRequest.parse(["--awake", "2h", "rendering", "shot", "4"])
        check(a?.action == "awake" && a?.minutes == 120 && a?.reason == "rendering shot 4", "agent: --awake with a duration and reason")
        check(AgentRequest.parse(["--awake", "on"]).map { $0.action == "awake" && $0.minutes == 0 } == true, "agent: --awake on holds until released")
        check(AgentRequest.parse(["--awake", "OFF"])?.action == "release", "agent: --awake off releases")
        check(AgentRequest.parse(["--awake"]) == nil && AgentRequest.parse(["--awake", "soon"]) == nil, "agent: --awake needs a valid duration")
        check(["sleep", "restart", "shutdown", "status"].allSatisfy { AgentRequest.parse(["--" + $0])?.action == $0 },
              "agent: power and status commands")
        let n = AgentRequest.parse(["--notify", "Build done", "All", "tests", "pass", "--needs-you", "--agent", "codex"])
        check(n.map { [$0.action, $0.title, $0.detail, $0.agent] == ["notify", "Build done", "All tests pass", "codex"] && $0.needsYou } == true,
              "agent: --notify with detail, --needs-you, and --agent anywhere")
        check(AgentRequest.parse(["--notify"]) == nil && AgentRequest.parse(["--notify", "  "]) == nil, "agent: --notify needs a title")
        check(AgentRequest.parse([]) == nil && AgentRequest.parse(["--bogus"]) == nil && AgentRequest.parse(["--sleep", "--agent"]) == nil,
              "agent: unknown or incomplete commands rejected")

        let r = AgentRequest.parse(["--notify", "Say \"hi\"", "line\nnext", "--needs-you"])!
        r.pwd = "/Users/andy/code/aevalrena"
        r.host = "Terminal"
        r.appPid = 42
        r.pid = 7
        r.pidStart = 99
        if let back = AgentRequest.from(parseObj(r.json())) {
            let texts = [back.action, back.title, back.detail, back.agent, back.pwd, back.host]
            check(texts == ["notify", "Say \"hi\"", "line\nnext", "claude", r.pwd, "Terminal"] && back.needsYou,
                  "agent: a request's text survives the trip to the app")
            check(back.appPid == 42 && back.pid == 7 && back.pidStart == 99, "agent: a request's process ids survive the trip")
        } else {
            check(false, "agent: a request survives the trip to the app")
        }
        let w = AgentRequest.parse(["--awake", "1h30m", "x"])!
        check(AgentRequest.from(parseObj(w.json())).map { $0.minutes == 90 && $0.reason == "x" && !$0.needsYou } == true, "agent: minutes survive the trip")
        check(AgentRequest.from(parseObj("{\"cmd\":\"status\"}")) == nil && AgentRequest.from(parseObj("{\"cmd\":\"agent\",\"action\":\"\"}")) == nil,
              "agent: other messages are not agent requests")
        check(r.json().hasPrefix("{\"cmd\":\"agent\""), "agent: the app recognises the message by its prefix")

        // A sleep or shutdown waits for every other busy session, but not the one asking.
        let t0 = Date(timeIntervalSince1970: 1_791_374_400)
        let book = SessionBook()
        _ = book.apply(ev("UserPromptSubmit", "s1", at: 1), t0)
        let other = ev("UserPromptSubmit", "s2", at: 2)
        other.cwd = "/Users/andy/code/other"
        other.pid = 8
        other.pidStart = 100
        _ = book.apply(other, t0)
        check(book.busyBlockers(exceptPid: 7, exceptStart: 99, exceptId: "s1").map { $0.id } == ["s2"], "agent: the requester is not its own blocker")
        check(book.busyBlockers(exceptPid: 7, exceptStart: 0, exceptId: nil).map { $0.id } == ["s2"], "agent: matched by pid alone when the start time is unknown")
        check(book.busyBlockers(exceptPid: 0, exceptStart: 0, exceptId: nil).count == 2, "agent: an unknown requester is blocked by every busy session")
        _ = book.apply(ev("Stop", "s2", at: 3), t0)
        check(book.busyBlockers(exceptPid: 7, exceptStart: 99, exceptId: "s1").isEmpty, "agent: idle sessions do not block")

        check(book.find(pid: 8, pidStart: 100, cwd: "")?.id == "s2", "agent: the requester found by its process")
        check(book.find(pid: 0, pidStart: 0, cwd: "/Users/andy/code/aevalrena")?.id == "s1", "agent: the requester found by its folder")
        check(book.find(pid: 8, pidStart: 1, cwd: "/nowhere") == nil && book.find(pid: 0, pidStart: 0, cwd: "") == nil, "agent: no match, no session")
    }

    static func awakeHolds() {
        let t0 = Date(timeIntervalSince1970: 1_791_374_400)
        let holds = AwakeHolds()
        check(!holds.active, "hold: none at start")
        holds.hold(Hold(key: "pid:7:99", label: "aevalrena", reason: "render", since: t0, until: t0.addingTimeInterval(3600), pid: 7, pidStart: 99))
        check(holds.active && AwakeHolds.text(holds.all[0], t0.addingTimeInterval(600)) == "50m left", "hold: a timed hold counts down")
        holds.hold(Hold(key: "pid:7:99", label: "aevalrena", reason: "longer", since: t0, until: t0.addingTimeInterval(7200), pid: 7, pidStart: 99))
        check(holds.all.count == 1 && holds.all[0].reason == "longer", "hold: asking again replaces the agent's hold")
        holds.hold(Hold(key: "cwd:/tmp/x", label: "x", reason: "", since: t0, until: nil, pid: 0, pidStart: 0))
        check(holds.all.count == 2 && AwakeHolds.text(holds.all[1], t0) == "until released", "hold: an open hold")

        check(!holds.sweep(t0.addingTimeInterval(60), alive: { _ in true }), "hold: nothing expires early")
        check(holds.sweep(t0.addingTimeInterval(60), alive: { _ in false }) && holds.all.map { $0.key } == ["cwd:/tmp/x"],
              "hold: a hold goes when its agent exits; one without a process stays")
        holds.hold(Hold(key: "pid:9:1", label: "y", reason: "", since: t0, until: t0.addingTimeInterval(120), pid: 9, pidStart: 1))
        check(holds.sweep(t0.addingTimeInterval(120), alive: { _ in true }) && holds.all.count == 1, "hold: a timed hold ends on time")
        check(!holds.sweep(t0.addingTimeInterval(23 * 3600), alive: { _ in true }) && holds.sweep(t0.addingTimeInterval(24 * 3600), alive: { _ in true }) && !holds.active,
              "hold: an open hold lasts at most a day")

        holds.hold(Hold(key: "a", label: "a", reason: "", since: t0, until: nil, pid: 0, pidStart: 0))
        check(holds.release("a") && !holds.release("a") && !holds.active, "hold: release reports whether there was one")
    }

    // The section kept in ~/.claude/CLAUDE.md.
    static func agentGuide() {
        let c = "'/Applications/Aevalsistant.app/Contents/MacOS/Aevalsistant'"
        let s = Settings()
        let block = AgentGuide.block(c, s)
        check(block.hasPrefix(AgentGuide.start + "\n") && block.hasSuffix(AgentGuide.end + "\n"), "guide: the block sits between its markers")
        check(block.contains(c + " --notify") && block.contains(c + " --awake 2h") && block.contains(c + " --sleep") && !block.contains("--restart"),
              "guide: lists the commands the settings allow")
        check(block.contains("are automatic"), "guide: says finish cards are automatic by default")
        s.agentPower = true
        s.agentAwake = false
        s.notifyMode = 2
        let other = AgentGuide.block(c, s)
        check(other.contains("--restart") && other.contains("--shutdown") && !other.contains("--awake 2h") && other.contains("lets you decide"),
              "guide: follows the settings, including agent decides")
        s.agentGuide = false
        check(AgentGuide.block(c, s).isEmpty, "guide: no block when teaching is off")

        let d = Settings()
        check(d.agentAwake && d.agentSleep && !d.agentPower && d.agentGuide && !d.agentDecides, "defaults: agents may keep awake and sleep, not restart")
        let path = NSTemporaryDirectory() + "/aeval-agent-test.ini"
        s.save(path)
        let loaded = Settings.load(path)
        try? FileManager.default.removeItem(atPath: path)
        check(!loaded.agentAwake && loaded.agentSleep && loaded.agentPower && !loaded.agentGuide && loaded.agentDecides && loaded.notifyMode == 2,
              "settings: agent control options survive a save and load")

        check(AgentGuide.apply(nil, "") == nil, "guide: no file and nothing to add leaves no file")
        check(AgentGuide.apply(nil, block) == block, "guide: a new file holds just the block")
        let mine = "# My rules\n\nUse tabs.\n"
        let added = AgentGuide.apply(mine, block)
        check(added == "# My rules\n\nUse tabs.\n\n" + block, "guide: appended after the user's text")
        check(AgentGuide.apply(added, block) == added, "guide: applying again changes nothing")
        check(AgentGuide.apply(added, other) == "# My rules\n\nUse tabs.\n\n" + other, "guide: the block is replaced in place")
        check(AgentGuide.apply(added, "") == mine, "guide: removing restores the user's text")
        let middle = "Top\n\n" + block + "\nBottom\n"
        check(AgentGuide.apply(middle, "") == "Top\n\nBottom\n", "guide: removing from the middle keeps both sides")
        check(AgentGuide.apply(middle, other) == "Top\n\nBottom\n\n" + other, "guide: a replaced block moves to the end")
        let crlf = "Top\r\n\r\n" + block
        check(AgentGuide.apply(crlf, "") == "Top\n", "guide: Windows line endings around the block are trimmed")
        check(AgentGuide.apply(block, "") == "", "guide: a file with only the block becomes empty")
        let broken = "Keep\n" + AgentGuide.start + "\nhalf"
        check(AgentGuide.apply(broken, block) == broken + "\n\n" + block, "guide: a start marker without an end is left alone")
    }

    static func easing() {
        var prev = -1.0, mono = true
        for i in 0...100 { let y = Ease.enter(Double(i) / 100); if y < prev - 1e-9 { mono = false }; prev = y }
        check(mono && Ease.enter(0) == 0 && Ease.enter(1) == 1 && Ease.enter(0.5) > 0.85, "ease: enter curve decelerates and is monotonic")
        check(Ease.exit(0.25) < Ease.exit(0.5) && Ease.exit(0.5) < Ease.exit(0.75) && Ease.exit(0.1) < 0.1, "ease: exit curve starts gently and rises")
    }
}
