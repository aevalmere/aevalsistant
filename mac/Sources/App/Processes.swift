import AppKit
import ApplicationServices
import Darwin

struct ProcInfo {
    let pid: Int32
    let ppid: Int32
    let start: Int64   // microseconds since 1970, to tell a recycled pid from the original
    let comm: String
}

// Finding, checking, and focusing the app that hosts a session, whether that is a terminal, an
// editor (VS Code, a JetBrains IDE), or the Claude desktop app. The Mac counterpart of
// src/Windows.cs.
enum Terminals {
    // The long-lived process of each agent, used to notice when a session's agent has exited.
    // IDE agents (Cursor, Windsurf) run inside the editor's helper processes, which can be
    // short-lived, so they get none and fall back to the inactivity timeout. A name also matches
    // with a suffix after a dash, for builds such as codex-aarch64-apple-darwin.
    static let agentProcesses: [String: [String]] = [
        "claude": ["claude", "node", "bun"],
        "codex": ["codex"],
        "gemini": ["gemini", "node"],
        "copilot": ["copilot", "node"],
        "cursor": ["cursor-agent"],
        "windsurf": [],
        "opencode": ["opencode"],
    ]

    static let appNames: [String: String] = [
        "com.apple.Terminal": "Terminal", "com.googlecode.iterm2": "iTerm2", "dev.warp.Warp-Stable": "Warp",
        "com.mitchellh.ghostty": "Ghostty", "net.kovidgoyal.kitty": "kitty", "org.alacritty": "Alacritty",
        "com.github.wez.wezterm": "WezTerm", "co.zeit.hyper": "Hyper", "org.tabby": "Tabby",
        "com.microsoft.VSCode": "VS Code", "com.microsoft.VSCodeInsiders": "VS Code Insiders",
        "com.todesktop.230313mzl4w4u92": "Cursor", "com.exafunction.windsurf": "Windsurf", "dev.zed.Zed": "Zed",
        "com.google.android.studio": "Android Studio", "com.apple.dt.Xcode": "Xcode",
        "com.jetbrains.intellij": "IntelliJ IDEA", "com.jetbrains.intellij.ce": "IntelliJ IDEA", "com.jetbrains.pycharm": "PyCharm",
        "com.jetbrains.pycharm.ce": "PyCharm", "com.jetbrains.WebStorm": "WebStorm", "com.jetbrains.rider": "Rider",
        "com.jetbrains.CLion": "CLion", "com.jetbrains.goland": "GoLand", "com.jetbrains.PhpStorm": "PhpStorm",
        "com.anthropic.claudefordesktop": "Claude", "com.openai.chat": "ChatGPT",
    ]

    struct Found {
        var appPid: Int32 = 0
        var host = ""
        var agentPid: Int32 = 0
        var agentStart: Int64 = 0
        var outerPid: Int32 = 0
        var outerStart: Int64 = 0
    }

    // Runs inside the short-lived --hook process, whose ancestors are the hook shell, the agent,
    // and above that whatever app started the agent.
    static func fromHookProcess(_ agent: String) -> Found {
        var found = Found()
        let chain = ancestors(of: getpid())

        // The nearest agent process is this session's own. One further up means another agent
        // started it (`claude -p` from Claude Code's shell); the app nests the session under
        // that one's if it knows it.
        let own = agentProcesses[agent] ?? []
        let any = Set(agentProcesses.values.flatMap { $0 })
        if let i = chain.firstIndex(where: { matches(names(of: $0), own) }) {
            found.agentPid = chain[i].pid
            found.agentStart = chain[i].start
            if let outer = chain[(i + 1)...].first(where: { matches(names(of: $0), Array(any)) }) {
                found.outerPid = outer.pid
                found.outerStart = outer.start
            }
        }

        // The nearest ancestor that is a regular app: Terminal, iTerm2, Code, Claude... Helpers
        // such as "Code Helper (Plugin)" are background processes and are skipped.
        for p in chain where p.pid > 1 {
            guard let app = NSRunningApplication(processIdentifier: p.pid), app.activationPolicy == .regular else { continue }
            found.appPid = p.pid
            found.host = label(app)
            break
        }
        return found
    }

    static func label(_ app: NSRunningApplication) -> String {
        if let id = app.bundleIdentifier, let known = appNames[id] { return known }
        return app.localizedName ?? ""
    }

    private static func matches(_ names: [String], _ wanted: [String]) -> Bool {
        names.contains { n in wanted.contains { n == $0 || n.hasPrefix($0 + "-") } }
    }

    // The kernel's short name, the executable's file name, and argv[0]: Claude Code's native
    // build runs from a file named after its version, but calls itself "claude".
    static func names(of p: ProcInfo) -> [String] {
        var out = [p.comm.lowercased()]
        let path = self.path(p.pid)
        if !path.isEmpty { out.append((path as NSString).lastPathComponent.lowercased()) }
        let a0 = argv0(p.pid)
        if !a0.isEmpty { out.append((a0 as NSString).lastPathComponent.lowercased()) }
        return out
    }

    static func info(_ pid: Int32) -> ProcInfo? {
        var kp = kinfo_proc()
        var size = MemoryLayout<kinfo_proc>.stride
        var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, pid]
        guard sysctl(&mib, 4, &kp, &size, nil, 0) == 0, size > 0 else { return nil }
        let st = kp.kp_proc.p_un.__p_starttime
        let comm = withUnsafeBytes(of: kp.kp_proc.p_comm) { raw -> String in
            let bytes = raw.bindMemory(to: UInt8.self)
            let end = bytes.firstIndex(of: 0) ?? bytes.count
            return String(decoding: bytes[0..<end], as: UTF8.self)
        }
        return ProcInfo(pid: pid, ppid: kp.kp_eproc.e_ppid, start: Int64(st.tv_sec) * 1_000_000 + Int64(st.tv_usec), comm: comm)
    }

    static func path(_ pid: Int32) -> String {
        var buf = [CChar](repeating: 0, count: 4096)
        let n = proc_pidpath(pid, &buf, UInt32(buf.count))
        return n > 0 ? String(cString: buf) : ""
    }

    static func argv0(_ pid: Int32) -> String {
        var mib: [Int32] = [CTL_KERN, KERN_ARGMAX]
        var argmax: Int32 = 0
        var size = MemoryLayout<Int32>.size
        guard sysctl(&mib, 2, &argmax, &size, nil, 0) == 0, argmax > 0 else { return "" }
        var buf = [UInt8](repeating: 0, count: Int(argmax))
        mib = [CTL_KERN, KERN_PROCARGS2, pid]
        size = buf.count
        guard sysctl(&mib, 3, &buf, &size, nil, 0) == 0, size > 4 else { return "" }
        // argc, then the executable path, padding, and argv[0]
        var i = 4
        while i < size && buf[i] != 0 { i += 1 }
        while i < size && buf[i] == 0 { i += 1 }
        let start = i
        while i < size && buf[i] != 0 { i += 1 }
        return String(decoding: buf[start..<i], as: UTF8.self)
    }

    static func ancestors(of pid: Int32) -> [ProcInfo] {
        var chain: [ProcInfo] = []
        guard var cur = info(pid) else { return chain }
        var seen: Set<Int32> = [pid]
        while cur.ppid > 1, seen.insert(cur.ppid).inserted, chain.count < 40, let up = info(cur.ppid) {
            // A "parent" that started after its child is some other program that reused the pid
            // of a parent that has exited.
            if up.start > cur.start { break }
            chain.append(up)
            cur = up
        }
        return chain
    }

    static func isAlive(_ pid: Int32, _ start: Int64) -> Bool {
        guard pid > 0, kill(pid, 0) == 0 || errno == EPERM else { return false }
        // Guards against a recycled pid now belonging to some other program.
        return start == 0 || info(pid)?.start == start
    }

    static func app(_ pid: Int32) -> NSRunningApplication? {
        guard pid > 0, let app = NSRunningApplication(processIdentifier: pid), !app.isTerminated else { return nil }
        return app
    }

    // Brings the session's app to the front. With Accessibility access, the window whose title
    // names the project comes first; without it, the app's frontmost window does.
    @discardableResult
    static func focus(_ pid: Int32, project: String) -> Bool {
        guard let app = app(pid) else { return false }
        if AXIsProcessTrusted() && !project.isEmpty { raiseWindow(pid, titled: project) }
        app.unhide()
        app.activate(options: [.activateIgnoringOtherApps])
        // Since macOS 14 an app in the background may not be allowed to activate another one.
        // Opening it through Launch Services, as the Dock does, always is.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) {
            guard NSWorkspace.shared.frontmostApplication?.processIdentifier != pid, let url = app.bundleURL else { return }
            let config = NSWorkspace.OpenConfiguration()
            config.activates = true
            NSWorkspace.shared.openApplication(at: url, configuration: config)
        }
        return true
    }

    private static func raiseWindow(_ pid: Int32, titled needle: String) {
        let ax = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(ax, 0.5)
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(ax, kAXWindowsAttribute as CFString, &value) == .success, let windows = value as? [AXUIElement] else { return }
        for w in windows {
            var t: CFTypeRef?
            guard AXUIElementCopyAttributeValue(w, kAXTitleAttribute as CFString, &t) == .success, let title = t as? String,
                  title.range(of: needle, options: .caseInsensitive) != nil else { continue }
            AXUIElementSetAttributeValue(w, kAXMinimizedAttribute as CFString, kCFBooleanFalse)
            AXUIElementPerformAction(w, kAXRaiseAction as CFString)
            AXUIElementSetAttributeValue(w, kAXMainAttribute as CFString, kCFBooleanTrue)
            return
        }
    }
}
