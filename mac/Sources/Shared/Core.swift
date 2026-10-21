import Foundation

// Platform-free logic, ported from src/Core.cs so both apps track sessions the same way.

enum AgentState { case working, waiting }
enum ToastKind { case done, needsYou, info }
enum RowState { case working, done, needsYou }

// A subagent that started and has not stopped. Background subagents keep running after the
// parent's turn ends.
final class Subagent {
    var type = ""   // "Explore", "general-purpose"...; empty when the agent does not say
    var started = Date.distantPast
    var seen = Date.distantPast   // last sign of life: its start, or a write to its transcript
    var order = 0                 // start order, for subagents that started in the same instant
}

final class Session {
    let id: String
    var seq = 0                 // creation order, so sessions with the same timestamp list the same way every time
    var cwd = ""
    var transcript = ""
    var appPid: Int32 = 0       // the app hosting it (Terminal, VS Code, Claude...), for focusing
    var pid: Int32 = 0          // the agent's own long-lived process
    var pidStart: Int64 = 0
    // The agent process that started this session's agent, when one did: a Claude Code
    // session running `claude -p` or `codex exec` in its shell. Matched against other
    // sessions' pid to nest this one under its parent.
    var outerPid: Int32 = 0
    var outerStart: Int64 = 0
    var state = AgentState.waiting
    var blocked = false              // a permission prompt is open
    var notifiedSincePrompt = false
    var lastEvent = Date.distantPast
    var lastActivity = Date.distantPast   // newest of hook events and transcript writes
    var transcriptSeen = Date.distantPast // transcript mtime at the last sweep
    var promptAt: Int64 = 0, stopAt: Int64 = 0   // hook process start times, to order async deliveries
    var host = ""                    // the app hosting it: "VS Code", "Terminal", "Claude"...
    var agent = "claude"             // claude, codex, gemini, copilot, cursor, windsurf, opencode, chat
    var label = ""                   // fixed name for sessions without a folder (chat apps)
    var pendingText = ""             // the agent's last reply, for agents that send it before Stop
    var workingSince = Date.distantPast
    var subagents: [String: Subagent] = [:]
    private var stoppedEarly = Set<String>()   // async SubagentStop that beat its Start
    private var started = 0

    init(id: String) { self.id = id }

    var busy: Bool { state == .working || !subagents.isEmpty }
    var whereLabel: String { Agents.whereLabel(agent, host) }

    // A subagent's own transcript: <session transcript minus .jsonl>/subagents/agent-<id>.jsonl
    func subagentTranscript(_ agentId: String) -> String {
        guard transcript.lowercased().hasSuffix(".jsonl"), !agentId.isEmpty else { return "" }
        return String(transcript.dropLast(6)) + "/subagents/agent-" + agentId + ".jsonl"
    }

    func subagentStarted(_ id: String, _ type: String, _ now: Date) {
        if stoppedEarly.remove(id) != nil { return }
        let a = Subagent()
        a.type = type
        a.started = now
        a.seen = now
        started += 1
        a.order = started
        subagents[id] = a
    }

    // True when this was the last one running.
    func subagentStopped(_ id: String) -> Bool {
        if subagents.removeValue(forKey: id) != nil { return subagents.isEmpty }
        stoppedEarly.insert(id)
        return false
    }

    // Last folder of the working directory. Split by hand so Windows paths in tests behave the
    // same as on the C# side.
    var project: String {
        if !label.isEmpty { return label }
        var trimmed = cwd
        while let last = trimmed.last, last == "/" || last == "\\" { trimmed.removeLast() }
        if trimmed.isEmpty { return Agents.get(agent).name }
        if let cut = trimmed.lastIndex(where: { $0 == "/" || $0 == "\\" }) { return String(trimmed[trimmed.index(after: cut)...]) }
        return trimmed
    }
}

final class HookEvent {
    var name = ""
    var sessionId = ""
    var cwd = ""
    var transcript = ""
    var message = ""
    var notificationType = ""
    var lastAssistant = ""
    var appPid: Int32 = 0
    var pid: Int32 = 0
    var pidStart: Int64 = 0
    var at: Int64 = 0   // when the hook process started; async hooks can arrive out of order
    var host = ""
    var agentId = ""
    var agentType = ""  // a subagent's kind, from SubagentStart
    var agent = "claude"
    var label = ""
    var cwdGuessed = false
    var outerPid: Int32 = 0
    var outerStart: Int64 = 0

    init() {}

    init(_ name: String, _ sessionId: String) {
        self.name = name
        self.sessionId = sessionId
    }

    // The envelope the --hook client sends:
    // {"t":..,"agent":"codex","event":"","pwd":"..","host":"..","app":..,"pid":..,"pidStart":..,"outer":..,"outerStart":..,"raw":"<stdin>"}
    static func fromEnvelope(_ text: String) -> HookEvent? {
        guard let o = Json.tryParse(text) as? JObj, let raw = o.str("raw"), let h = Json.tryParse(raw) as? JObj else { return nil }
        func num(_ k: String) -> Int64 { (o[k] as? JNum)?.int64 ?? 0 }
        let e = HookEvent()
        e.agent = (o.str("agent") ?? "").isEmpty ? "claude" : o.str("agent")!
        e.name = o.str("event") ?? ""
        e.appPid = Int32(truncatingIfNeeded: num("app"))
        e.pid = Int32(truncatingIfNeeded: num("pid"))
        e.pidStart = num("pidStart")
        e.at = num("t")
        e.host = o.str("host") ?? ""
        e.outerPid = Int32(truncatingIfNeeded: num("outer"))
        e.outerStart = num("outerStart")
        Agents.normalize(e.agent, h, e, o.str("pwd"))
        return e.sessionId.isEmpty || e.name.isEmpty ? nil : e
    }
}

final class ToastRequest {
    var sessionId: String?   // nil for app messages
    var appPid: Int32 = 0
    var host = ""            // shown after the title so you know which window it is
    var kind = ToastKind.info
    var title = ""
    var detail = ""
    var wantsSnippet = false // fill detail from the transcript just before showing
    // Came from work running in the background rather than from your own prompt: a session
    // that another agent started, a turn the agent took on its own after a background
    // subagent reported back, or the last background subagent finishing. Settings decide
    // whether these show a card.
    var background = false

    init(kind: ToastKind, title: String, detail: String) {
        self.kind = kind
        self.title = title
        self.detail = detail
    }
}

// One line of the session list: a session, or one of its subagents (subagent set).
struct TreeRow {
    let session: Session
    var subagentId: String? = nil
    var subagent: Subagent? = nil
    var depth = 0
}

struct TranscriptProbe {
    var modified: Date
    var endsInInterrupt = false
}

final class SessionBook {
    static let staleWorking: TimeInterval = 45 * 60
    static let forgetUntracked: TimeInterval = 12 * 3600

    private var map: [String: Session] = [:]
    private var created = 0

    var all: [Session] { map.values.sorted { $0.lastEvent != $1.lastEvent ? $0.lastEvent > $1.lastEvent : $0.seq < $1.seq } }
    var busyCount: Int { map.values.filter { $0.busy }.count }
    func get(_ id: String?) -> Session? { id.flatMap { map[$0] } }

    // Sessions in display order, needs-you first, then done, then working. With nest on, each
    // is followed by what it started: its subagents, then sessions its agent started (and
    // theirs), one level in. headId is left out of the list because the card shows it above,
    // but its own children lead the list.
    func tree(_ headId: String?, nest: Bool) -> [TreeRow] {
        func rank(_ s: Session) -> Int {
            switch Status.state(s) { case .needsYou: return 0; case .done: return 1; case .working: return 2 }
        }
        func root(_ s: Session) -> Session {
            var s = s
            for _ in 0..<8 { guard let p = parentOf(s) else { break }; s = p }
            return s
        }
        // Stable: ties keep the newest-first order.
        let ordered = all.enumerated().sorted { a, b in
            let ra = rank(a.element), rb = rank(b.element)
            return ra != rb ? ra < rb : a.offset < b.offset
        }.map { $0.element }
        var rows: [TreeRow] = []
        func children(_ parent: Session) {
            for (id, sub) in parent.subagents.sorted(by: { $0.value.started != $1.value.started ? $0.value.started < $1.value.started : $0.value.order < $1.value.order }) {
                rows.append(TreeRow(session: parent, subagentId: id, subagent: sub, depth: 1))
            }
            for c in ordered where c !== parent && c.id != headId && root(c) === parent {
                rows.append(TreeRow(session: c, depth: 1))
            }
        }
        if nest, let head = get(headId) { children(head) }
        for s in ordered {
            if s.id == headId || (nest && root(s) !== s) { continue }
            rows.append(TreeRow(session: s))
            if nest { children(s) }
        }
        return rows
    }

    // The session whose agent process started this one's, if this app knows it.
    func parentOf(_ s: Session?) -> Session? {
        guard let s = s, s.outerPid != 0 else { return nil }
        return map.values.first { p in
            p !== s && p.pid == s.outerPid && (p.pidStart == 0 || s.outerStart == 0 || p.pidStart == s.outerStart)
        }
    }

    func apply(_ e: HookEvent, _ now: Date) -> ToastRequest? {
        if e.name == "SessionEnd" { map.removeValue(forKey: e.sessionId); return nil }

        let s: Session
        if let existing = map[e.sessionId] { s = existing } else {
            s = Session(id: e.sessionId)
            created += 1
            s.seq = created
            map[e.sessionId] = s
        }
        if !e.cwd.isEmpty && (!e.cwdGuessed || s.cwd.isEmpty) { s.cwd = e.cwd }
        if !e.transcript.isEmpty { s.transcript = e.transcript }
        if e.appPid != 0 { s.appPid = e.appPid }
        if !e.host.isEmpty { s.host = e.host }
        if !e.agent.isEmpty { s.agent = e.agent }
        if !e.label.isEmpty { s.label = e.label }
        if e.pid != 0 { s.pid = e.pid; s.pidStart = e.pidStart }
        if e.outerPid != 0 { s.outerPid = e.outerPid; s.outerStart = e.outerStart }
        s.lastEvent = now
        s.lastActivity = now
        let child = parentOf(s) != nil

        let isPrompt = e.name == "UserPromptSubmit"
        let isStop = e.name == "Stop" || e.name == "StopFailure" || e.name == "Interrupt"
        if e.at != 0 {
            // A late UserPromptSubmit from before the latest Stop (or the reverse) is stale.
            if isPrompt && e.at < s.stopAt { return nil }
            if isStop && e.at < s.promptAt { return nil }
            if isPrompt { s.promptAt = e.at }
            if isStop { s.stopAt = e.at }
        }

        func card(_ kind: ToastKind, _ title: String, _ detail: String, background: Bool) -> ToastRequest {
            let r = ToastRequest(kind: kind, title: title, detail: detail)
            r.sessionId = s.id
            r.appPid = s.appPid
            r.host = s.whereLabel
            r.background = background
            return r
        }

        switch e.name {
        case "AgentText":
            s.pendingText = e.lastAssistant
            return nil

        case "Interrupt":
            // You stopped it yourself, so there is nothing to tell you.
            s.state = .waiting
            s.blocked = false
            s.notifiedSincePrompt = true
            return nil

        case "SubagentStart":
            s.subagentStarted(e.agentId.isEmpty ? "unnamed" : e.agentId, e.agentType, now)
            return nil

        case "SubagentStop":
            // Subagents that finish mid-turn are part of that turn. The last background
            // one finishing after the turn ended is the end of everything you started.
            if !s.subagentStopped(e.agentId.isEmpty ? "unnamed" : e.agentId) || s.state == .working { return nil }
            return card(.done, s.project + " finished", "Its background agents are done.", background: true)

        case "UserPromptSubmit":
            if s.state != .working { s.workingSince = now }
            s.state = .working
            s.blocked = false
            s.notifiedSincePrompt = false
            s.pendingText = ""
            return nil

        case "Stop":
            // A second Stop with no prompt in between is a turn the agent took on its
            // own, usually because a background subagent reported back.
            let onItsOwn = s.notifiedSincePrompt
            s.state = .waiting
            s.blocked = false
            s.notifiedSincePrompt = true
            let reply = e.lastAssistant.isEmpty ? s.pendingText : e.lastAssistant
            s.pendingText = ""
            let r = card(.done, s.project + (s.subagents.isEmpty ? " finished" : " is waiting"),
                         !reply.isEmpty || s.agent == "claude" ? Snippet.clean(reply) : "Ready for your next prompt.",
                         background: child || onItsOwn)
            // Only Claude Code's transcript format is known well enough to read the reply from.
            r.wantsSnippet = reply.isEmpty && s.agent == "claude"
            return r

        case "StopFailure":
            s.state = .waiting
            s.blocked = false
            s.notifiedSincePrompt = true
            return card(.needsYou, s.project + " stopped early", "The turn ended on an error. The terminal has the details.", background: child)

        case "Notification":
            let type = e.notificationType.isEmpty ? SessionBook.guessType(e.message) : e.notificationType
            if type == "idle_prompt" {
                s.state = .waiting
                s.blocked = false
                if s.notifiedSincePrompt { return nil }
                s.notifiedSincePrompt = true
                return card(.done, s.project + " is waiting", e.message.isEmpty ? "Ready for your next prompt." : e.message, background: child)
            }
            if ["permission_prompt", "elicitation_dialog", "elicitation_url_dialog", "agent_needs_input"].contains(type) {
                // Still mid-turn: keep the machine awake, but ask for the human.
                s.blocked = true
                return card(.needsYou, s.project + " needs you", e.message.isEmpty ? "Claude is asking for permission." : e.message, background: child)
            }
            return nil

        default:
            return nil
        }
    }

    static func guessType(_ message: String) -> String {
        let m = message.lowercased()
        if m.contains("waiting for your input") { return "idle_prompt" }
        if m.contains("permission") { return "permission_prompt" }
        return ""
    }

    // Called every few seconds. Returns true when anything changed.
    func sweep(_ now: Date, alive: (Session) -> Bool, probe: (Session) -> TranscriptProbe?,
               subagentWrite: ((Session, String) -> Date?)? = nil) -> Bool {
        var changed = false
        for s in Array(map.values) {
            if s.pid != 0 ? !alive(s) : now.timeIntervalSince(s.lastEvent) > SessionBook.forgetUntracked {
                map.removeValue(forKey: s.id)
                changed = true
                continue
            }
            // A subagent counts as alive while its own transcript keeps being written.
            for (id, sub) in s.subagents {
                if let wrote = subagentWrite?(s, id), wrote > sub.seen { sub.seen = wrote }
                if now.timeIntervalSince(sub.seen) > SessionBook.staleWorking { s.subagents.removeValue(forKey: id); changed = true }
            }
            if s.state != .working { continue }

            if let p = probe(s), p.modified > s.transcriptSeen {
                s.transcriptSeen = p.modified
                if p.modified > s.lastActivity { s.lastActivity = p.modified }
                if p.endsInInterrupt {
                    // Esc in Claude Code skips the Stop hook; the transcript records it instead.
                    s.state = .waiting
                    s.blocked = false
                    changed = true
                    continue
                }
            }
            if now.timeIntervalSince(s.lastActivity) > SessionBook.staleWorking {
                s.state = .waiting
                s.blocked = false
                changed = true
            }
        }
        return changed
    }
}

enum Transcript {
    static let tailBytes = 256 * 1024

    static func tailLines(_ path: String) -> [String] {
        guard !path.isEmpty, let fh = FileHandle(forReadingAtPath: path) else { return [] }
        defer { try? fh.close() }
        let size = fh.seekToEndOfFile()
        let start = size > UInt64(tailBytes) ? size - UInt64(tailBytes) : 0
        fh.seek(toFileOffset: start)
        let data = fh.readDataToEndOfFile()
        let text = String(decoding: data, as: UTF8.self)
        var parts = text.components(separatedBy: "\n")
        // The first piece is a partial line unless we read from byte zero.
        if start > 0, !parts.isEmpty { parts.removeFirst() }
        return parts.filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }
    }

    static func lastAssistantText(_ lines: [String]) -> String {
        for line in lines.reversed() {
            guard let o = entry(line), o.str("type") == "assistant" else { continue }
            let text = textOf(o.obj("message"))
            if !text.isEmpty { return text }
        }
        return ""
    }

    static func endsInInterrupt(_ lines: [String]) -> Bool {
        for line in lines.reversed() {
            let o = entry(line)
            let type = o?.str("type")
            if type == "assistant" { return false }
            if type != "user" { continue }
            let text = textOf(o?.obj("message"))
            return text.drop(while: { $0.isWhitespace }).hasPrefix("[Request interrupted by user")
        }
        return false
    }

    private static func entry(_ line: String) -> JObj? { Json.tryParse(line) as? JObj }

    private static func textOf(_ message: JObj?) -> String {
        guard let message = message else { return "" }
        if let s = message["content"] as? String { return s }
        guard let blocks = message["content"] as? JArr else { return "" }
        var out = ""
        for case let block as JObj in blocks.items where block.str("type") == "text" {
            out += (block.str("text") ?? "") + "\n"
        }
        return out.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

enum Snippet {
    private static let fence = try! NSRegularExpression(pattern: "```.*?(```|$)", options: [.dotMatchesLineSeparators])
    private static let marks = try! NSRegularExpression(pattern: "(\\*\\*|__|`|^#+\\s*|^\\s*[-*>]\\s+)", options: [.anchorsMatchLines])
    private static let space = try! NSRegularExpression(pattern: "\\s+")

    static func clean(_ text: String) -> String {
        if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return "" }
        var t = replace(fence, text, " ")
        t = replace(marks, t, "")
        t = replace(space, t, " ").trimmingCharacters(in: .whitespaces)
        return t.count > 600 ? String(t.prefix(600)) : t
    }

    private static func replace(_ re: NSRegularExpression, _ s: String, _ with: String) -> String {
        re.stringByReplacingMatches(in: s, range: NSRange(s.startIndex..., in: s), withTemplate: with)
    }
}

// The Mac app's settings. Defaults follow the settings the owner runs on Windows: keep the Mac
// and its screen awake, keep working with the lid closed, no sound, and cards for background
// agents only when one needs you.
final class Settings {
    var hooks = true
    var agentsOff = Set<String>()   // agent ids the user turned off one by one
    var startup = true
    var keepAwake = true
    var lidAwake = true
    var displayOn = true
    var lidSaved = ""               // "1" while our disablesleep override is in effect
    var welcomed = false
    var chats = true                // watch Claude and ChatGPT desktop chats
    var autoUpdate = true
    var lastVersion = ""            // version that last ran, to say "updated to" once
    var notifyDone = true
    var notifyBackgroundDone = false
    var notifyNeedsYou = true
    var notifyBackgroundNeedsYou = true
    var sound = false
    var expandOnHover = true
    var optionTab = true
    var cardTime = 1                // 0 short, 1 normal, 2 long
    var showChildren = true         // list subagents and background sessions under their session

    static func load(_ path: String) -> Settings {
        let s = Settings()
        guard let text = try? String(contentsOfFile: path, encoding: .utf8) else { return s }
        for line in text.components(separatedBy: "\n") {
            guard let eq = line.firstIndex(of: "="), eq != line.startIndex else { continue }
            let k = line[..<eq].trimmingCharacters(in: .whitespaces)
            let v = line[line.index(after: eq)...].trimmingCharacters(in: .whitespacesAndNewlines)
            let b = v == "1"
            switch k {
            case "hooks": s.hooks = b
            case "agentsOff": s.agentsOff = Set(v.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty })
            case "startup": s.startup = b
            case "awake": s.keepAwake = b
            case "lid": s.lidAwake = b
            case "display": s.displayOn = b
            case "lidSaved": s.lidSaved = v
            case "welcomed": s.welcomed = b
            case "chats": s.chats = b
            case "autoUpdate": s.autoUpdate = b
            case "lastVersion": s.lastVersion = v
            case "notifyDone": s.notifyDone = b
            case "notifyBackgroundDone": s.notifyBackgroundDone = b
            case "notifyNeedsYou": s.notifyNeedsYou = b
            case "notifyBackgroundNeedsYou": s.notifyBackgroundNeedsYou = b
            case "sound": s.sound = b
            case "expand": s.expandOnHover = b
            case "optionTab": s.optionTab = b
            case "cardTime": if let t = Int(v), (0...2).contains(t) { s.cardTime = t }
            case "children": s.showChildren = b
            default: break
            }
        }
        return s
    }

    func save(_ path: String) {
        func b(_ v: Bool) -> String { v ? "1" : "0" }
        let lines = [
            "hooks=" + b(hooks), "agentsOff=" + agentsOff.sorted().joined(separator: ","), "startup=" + b(startup),
            "awake=" + b(keepAwake), "lid=" + b(lidAwake), "display=" + b(displayOn), "lidSaved=" + lidSaved,
            "welcomed=" + b(welcomed), "chats=" + b(chats), "autoUpdate=" + b(autoUpdate), "lastVersion=" + lastVersion,
            "notifyDone=" + b(notifyDone), "notifyBackgroundDone=" + b(notifyBackgroundDone),
            "notifyNeedsYou=" + b(notifyNeedsYou), "notifyBackgroundNeedsYou=" + b(notifyBackgroundNeedsYou),
            "sound=" + b(sound), "expand=" + b(expandOnHover), "optionTab=" + b(optionTab),
            "cardTime=" + String(cardTime), "children=" + b(showChildren), "",
        ]
        try? FileManager.default.createDirectory(atPath: (path as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
        try? lines.joined(separator: "\n").write(toFile: path, atomically: true, encoding: .utf8)
    }

    // Whether a card should show for this request. Background cards also need their
    // parent setting on, since Settings nests them under it.
    func wants(_ r: ToastRequest) -> Bool {
        switch r.kind {
        case .info: return true
        case .needsYou: return notifyNeedsYou && (!r.background || notifyBackgroundNeedsYou)
        case .done: return notifyDone && (!r.background || notifyBackgroundDone)
        }
    }

    // Seconds a card stays up while you are at the keyboard.
    func seconds(_ kind: ToastKind) -> Double {
        let normal: Double = kind == .needsYou ? 10 : kind == .info ? 5 : 8
        return normal * (cardTime == 0 ? 0.6 : cardTime == 2 ? 1.75 : 1)
    }
}

// The short state shown for a session in the card list and the menu.
enum Status {
    static func state(_ s: Session) -> RowState { s.blocked ? .needsYou : s.busy ? .working : .done }

    static func text(_ s: Session, _ now: Date) -> String {
        let n = s.subagents.count
        let subs = n == 1 ? "1 subagent" : "\(n) subagents"
        if s.blocked { return "needs you" }
        if s.state == .working { return join("working", age(now.timeIntervalSince(s.workingSince))) + (n > 0 ? ", " + subs : "") }
        if n > 0 { return subs + " running" }
        return join("done", age(now.timeIntervalSince(s.lastEvent)), "ago")
    }

    // A subagent's line: "running 4m".
    static func text(_ a: Subagent, _ now: Date) -> String { join("running", age(now.timeIntervalSince(a.started))) }

    private static func age(_ t: TimeInterval) -> String {
        if t < 60 || t > 7 * 86400 { return "" }
        if t < 3600 { return "\(Int(t / 60))m" }
        return "\(Int(t / 3600))h \(Int(t.truncatingRemainder(dividingBy: 3600) / 60))m"
    }

    private static func join(_ word: String, _ age: String, _ suffix: String = "") -> String {
        age.isEmpty ? word : word + " " + age + (suffix.isEmpty ? "" : " " + suffix)
    }
}

enum Plural {
    static func agents(_ n: Int) -> String { "\(n)" + (n == 1 ? " agent" : " agents") }
}

// Turns "is this chat app generating?" samples into session events. Keyed by the app's pid,
// since the watcher looks at a whole app rather than one window.
final class ChatTracker {
    private var watching: [Int32: Int] = [:]   // pid -> misses in a row

    func isWatching(_ pid: Int32) -> Bool { watching[pid] != nil }
    var watched: [Int32] { Array(watching.keys) }

    static func sessionId(_ pid: Int32) -> String { "chat:\(pid)" }

    // generating: true/false from the Stop button, nil when the app could not be read.
    func observe(_ pid: Int32, _ app: String, exists: Bool, generating: Bool?, foreground: Bool) -> HookEvent? {
        func ev(_ name: String) -> HookEvent {
            let e = HookEvent(name, ChatTracker.sessionId(pid))
            e.agent = "chat"
            e.label = "Chat"
            e.host = app
            e.appPid = pid
            e.pid = pid
            return e
        }
        if !exists {
            guard watching.removeValue(forKey: pid) != nil else { return nil }
            return ev("SessionEnd")
        }
        guard let generating = generating else { return nil }
        if generating {
            if watching[pid] != nil { watching[pid] = 0; return nil }
            watching[pid] = 0
            return ev("UserPromptSubmit")
        }
        guard let misses = watching[pid] else { return nil }
        // Two misses in a row, so a re-render that briefly drops the button does not count.
        if misses + 1 < 2 { watching[pid] = misses + 1; return nil }
        watching.removeValue(forKey: pid)
        // Finished while you were looking at it: no notification needed.
        return ev(foreground ? "Interrupt" : "Stop")
    }
}

// CSS cubic-bezier(x1, y1, x2, y2), solved for y at time x.
enum Ease {
    static func bezier(_ x1: Double, _ y1: Double, _ x2: Double, _ y2: Double, _ x: Double) -> Double {
        if x <= 0 { return 0 }
        if x >= 1 { return 1 }
        var t = x
        for _ in 0..<8 {
            let cx = curve(x1, x2, t) - x
            let dx = slope(x1, x2, t)
            if abs(cx) < 1e-5 || abs(dx) < 1e-6 { break }
            t -= cx / dx
        }
        t = max(0, min(1, t))
        return curve(y1, y2, t)
    }

    private static func curve(_ a: Double, _ b: Double, _ t: Double) -> Double { 3 * a * (1 - t) * (1 - t) * t + 3 * b * (1 - t) * t * t + t * t * t }
    private static func slope(_ a: Double, _ b: Double, _ t: Double) -> Double { 3 * a * (1 - t) * (1 - t) + 6 * (b - a) * (1 - t) * t + 3 * (1 - b) * t * t }

    static func enter(_ x: Double) -> Double { bezier(0.16, 1, 0.3, 1, x) }
    static func exit(_ x: Double) -> Double { bezier(0.4, 0, 0.2, 1, x) }
}
