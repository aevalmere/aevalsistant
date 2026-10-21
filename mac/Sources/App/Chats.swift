import AppKit
import ApplicationServices

// Claude and ChatGPT desktop chats have no hooks. While a reply is being written they show a
// Stop button, which macOS accessibility can see once the user allows it. The watcher looks only
// when you switch away from one of these apps, and then every few seconds until the reply is
// done, so nothing is read while you are not waiting on a reply.
final class ChatWatcher {
    static let apps = ["com.anthropic.claudefordesktop": "Claude", "com.openai.chat": "ChatGPT"]
    static let stopNames: Set<String> = ["Stop response", "Stop streaming", "Stop generating", "Stop"]

    var changed: ((HookEvent) -> Void)?

    private let tracker = ChatTracker()
    private let trackedByHooks: (Int32) -> Bool
    private let queue = DispatchQueue(label: "chat-watch", qos: .utility)
    private var observer: NSObjectProtocol?
    private var timer: Timer?
    private var front: NSRunningApplication? = NSWorkspace.shared.frontmostApplication
    private var checking = Set<Int32>()
    private var enabledTrees = Set<Int32>()

    // trackedByHooks: an app that already has a busy hook session (Claude Code in the desktop
    // app's Code tab) is left alone so it does not show up twice.
    init(trackedByHooks: @escaping (Int32) -> Bool) {
        self.trackedByHooks = trackedByHooks
        observer = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main) { [weak self] note in
            guard let self = self else { return }
            let left = self.front
            self.front = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication
            if let left = left, ChatWatcher.name(left) != nil { self.check(left) }
        }
        let t = Timer(timeInterval: 2.5, repeats: true) { [weak self] _ in self?.recheck() }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    func stop() {
        if let o = observer { NSWorkspace.shared.notificationCenter.removeObserver(o) }
        observer = nil
        timer?.invalidate()
        timer = nil
    }

    private static func name(_ app: NSRunningApplication) -> String? { app.bundleIdentifier.flatMap { apps[$0] } }

    private func recheck() {
        for pid in tracker.watched {
            if let app = Terminals.app(pid) { check(app) }
            else if let e = tracker.observe(pid, "", exists: false, generating: nil, foreground: false) { changed?(e) }
        }
    }

    private func check(_ app: NSRunningApplication) {
        let pid = app.processIdentifier
        guard let name = ChatWatcher.name(app), !checking.contains(pid) else { return }
        if trackedByHooks(pid) && !tracker.isWatching(pid) { return }
        checking.insert(pid)
        let enable = enabledTrees.insert(pid).inserted
        queue.async { [weak self] in
            let generating = ChatWatcher.generating(pid, enableTree: enable)
            DispatchQueue.main.async {
                guard let self = self else { return }
                self.checking.remove(pid)
                let foreground = NSWorkspace.shared.frontmostApplication?.processIdentifier == pid
                if let e = self.tracker.observe(pid, name, exists: !app.isTerminated, generating: generating, foreground: foreground) { self.changed?(e) }
            }
        }
    }

    // true or false from the Stop button; nil when the app could not be read.
    private static func generating(_ pid: Int32, enableTree: Bool) -> Bool? {
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 1)
        // Electron apps such as Claude build their accessibility tree only when asked.
        if enableTree { AXUIElementSetAttributeValue(app, "AXManualAccessibility" as CFString, kCFBooleanTrue) }
        var windows: CFTypeRef?
        guard AXUIElementCopyAttributeValue(app, kAXWindowsAttribute as CFString, &windows) == .success, let list = windows as? [AXUIElement] else { return nil }
        var queue = list
        var visited = 0
        let deadline = Date().addingTimeInterval(1.5)
        while visited < queue.count && visited < 6000 && Date() < deadline {
            let el = queue[visited]
            visited += 1
            if string(el, kAXRoleAttribute) == kAXButtonRole as String {
                for attr in [kAXTitleAttribute, kAXDescriptionAttribute, "AXHelp"] where stopNames.contains(string(el, attr)) { return true }
            }
            var kids: CFTypeRef?
            if AXUIElementCopyAttributeValue(el, kAXChildrenAttribute as CFString, &kids) == .success, let k = kids as? [AXUIElement] { queue.append(contentsOf: k) }
        }
        return Date() < deadline ? false : nil
    }

    private static func string(_ el: AXUIElement, _ attr: String) -> String {
        var v: CFTypeRef?
        guard AXUIElementCopyAttributeValue(el, attr as CFString, &v) == .success else { return "" }
        return (v as? String) ?? ""
    }
}
