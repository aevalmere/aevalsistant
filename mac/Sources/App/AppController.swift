import AppKit
import ServiceManagement

// The menu bar app: the counterpart of src/TrayApp.cs. Hook traffic comes in over the socket,
// SessionBook keeps the state, and this turns it into cards, the menu, and keep-awake.
final class AppController: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let settings = Settings.load(AppPaths.settings)
    private let book = SessionBook()
    private var pending: [ToastRequest] = []
    private let toast = ToastWindow()
    private let awake = AwakeAssertion()
    private var statusItem: NSStatusItem!
    private let menu = NSMenu()
    private var server: IpcServer?
    private var sweepTimer: Timer?
    private var updateTimer: Timer?
    private var sweeps = 0
    private let baseCommand = Agents.hookPath(Bundle.main.bundleURL.appendingPathComponent("Contents/MacOS/Aevalsistant").path) + " --hook"

    private var lidProblem: String?, chatProblem: String?, startupProblem: String?, socketProblem: String?
    private var agentProblems: [String: String] = [:]
    private var justConnected = Set<String>()
    private var chats: ChatWatcher?
    private var updateStatus: String?, pendingUpdate: String?
    private var checkingUpdate = false
    private var trayAwake: Bool?
    private let hasLid = Lid.hasLid
    // With the lid shut when the agents finish, the Mac would stay awake until it is opened, so
    // the app puts it to sleep a minute later if nothing started working again.
    private var lidSleep: Timer?
    private var lidTried = false
    private var shown: ToastContent?
    private var settingsWindow: SettingsWindow?
    private var updateNote: Note?, lidNote: Note?, chatNote: Note?
    private var lidToggle: Toggle?
    private var agentToggles: [String: Toggle] = [:]
    private var quitting = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        toast.expandOnHover = settings.expandOnHover
        toast.clicked = { [weak self] in self?.activate($0) }
        toast.optionTabbed = { [weak self] in self?.activate(ToastArt.hitHead) }
        toast.expired = { [weak self] in self?.pending.removeAll(); self?.toast.hide() }
        toast.dismissed = { [weak self] in self?.pending.removeAll(); self?.toast.hide() }

        // Repair a lid override left behind by a crash or power loss.
        if !settings.lidSaved.isEmpty, Lid.setSleepDisabled(false) == nil { settings.lidSaved = ""; save() }

        server = IpcServer { [weak self] text in self?.onMessage(text) }
        if server == nil { socketProblem = "Agents cannot reach the app: could not open \(AppPaths.socket)" }

        if settings.hooks { syncAgents(true) }
        if settings.chats { startChats() }
        setStartup(settings.startup)

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        menu.delegate = self
        menu.autoenablesItems = false
        statusItem.menu = menu
        refreshState()

        let sweep = Timer(timeInterval: 5, repeats: true) { [weak self] _ in self?.sweep() }
        RunLoop.main.add(sweep, forMode: .common)
        sweepTimer = sweep

        Updater.cleanUp()
        // first check two minutes after start, then every six hours
        let first = Timer(timeInterval: 120, repeats: false) { [weak self] _ in
            guard let self = self else { return }
            if self.settings.autoUpdate { self.checkForUpdates(manual: false) }
            let every = Timer(timeInterval: 6 * 3600, repeats: true) { [weak self] _ in
                guard let self = self else { return }
                if self.pendingUpdate == nil { Updater.cleanUp() }
                if self.settings.autoUpdate { self.checkForUpdates(manual: false) }
            }
            RunLoop.main.add(every, forMode: .common)
            self.updateTimer = every
        }
        RunLoop.main.add(first, forMode: .common)
        updateTimer = first

        let version = Updater.currentText
        if !settings.lastVersion.isEmpty && settings.lastVersion != version {
            enqueue(ToastRequest(kind: .info, title: "Aevalsistant updated to " + version,
                                 detail: justConnected.isEmpty ? "Your settings and hooks carried over." : "Restart open agent sessions once to pick up the new hooks."))
        }
        if settings.lastVersion != version { settings.lastVersion = version; save() }

        if !settings.welcomed {
            settings.welcomed = true
            save()
            enqueue(ToastRequest(kind: .info, title: "Aevalsistant is running",
                                 detail: "Restart open Claude Code sessions once so they report here. Settings are in the menu bar."))
            // On by default, and it needs the password once, so ask now rather than in the
            // middle of the first agent's work.
            if hasLid && settings.lidAwake && !Lid.authorized {
                DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in self?.authorizeLid(fromToggle: false) }
            }
        }
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        cleanUpForQuit()
        return .terminateNow
    }

    // ---- hook traffic -------------------------------------------------------------

    private func onMessage(_ text: String) -> String? {
        if text.hasPrefix("{\"cmd\":\"hello\"") {
            enqueue(ToastRequest(kind: .info, title: "Aevalsistant is already running", detail: "It lives in the menu bar."))
            return nil
        }
        if text.hasPrefix("{\"cmd\":\"quit\"") { quit(); return nil }
        if text.hasPrefix("{\"cmd\":\"status\"") { return statusJson() }
        if let e = HookEvent.fromEnvelope(text) { ingest(e) }
        return nil
    }

    private func ingest(_ e: HookEvent) {
        let req = book.apply(e, Date())
        if e.name == "UserPromptSubmit" || e.name == "SessionEnd" { withdraw(e.sessionId) }
        if let req = req { enqueue(req) } else if toast.showing { showTop(restart: false) }   // keep the list under the main row current
        refreshState()
    }

    // What `Aevalsistant --status` prints: enough to check the app from a terminal or a test.
    private func statusJson() -> String {
        let now = Date()
        let sessions = JArr(book.all.map { s in
            Agents.entry(("id", s.id), ("project", s.project), ("agent", s.agent), ("host", s.whereLabel),
                         ("status", Status.text(s, now)), ("busy", s.busy), ("pid", JNum(Int(s.pid))), ("app", JNum(Int(s.appPid))))
        })
        let agents = JObj()
        for a in Agents.all where agentPresent(a) { agents[a.id] = agentStatus(a) }
        let root = Agents.entry(
            ("version", Updater.currentText), ("busy", JNum(book.busyCount)), ("sessions", sessions),
            ("card", toast.showing ? toast.title : ""), ("keepAwake", awake.systemOn), ("screenOn", awake.displayOn),
            ("lidOverride", !settings.lidSaved.isEmpty), ("agents", agents),
            ("problems", JArr((Array(agentProblems.values) + [socketProblem, lidProblem, chatProblem, startupProblem].compactMap { $0 }) as [Any])))
        return Json.write(root) + "\n"
    }

    private func canFocus(_ sessionId: String?, _ appPid: Int32) -> Bool {
        Terminals.app(book.get(sessionId)?.appPid ?? appPid) != nil
    }

    // Option-Tab is taken over only when it would go somewhere new. With the session's app
    // already in front, it does whatever it did before.
    private func canJump(_ sessionId: String?, _ appPid: Int32) -> Bool {
        guard canFocus(sessionId, appPid) else { return false }
        let pid = book.get(sessionId)?.appPid ?? appPid
        return NSWorkspace.shared.frontmostApplication?.processIdentifier != pid
    }

    private func focusTarget(_ sessionId: String?, _ appPid: Int32) {
        let s = book.get(sessionId)
        let pid = s?.appPid ?? appPid
        if pid != 0 { Terminals.focus(pid, project: s?.label.isEmpty == false ? "" : s?.project ?? "") }
    }

    // ---- cards ------------------------------------------------------------------------

    private func enqueue(_ req: ToastRequest) {
        guard settings.wants(req) else {
            // No card for this, but the list under a card already showing stays current.
            if toast.showing { showTop(restart: false) }
            return
        }
        if let id = req.sessionId { pending.removeAll { $0.sessionId == id } }
        pending.append(req)
        if req.wantsSnippet {
            // The Stop hook can fire a moment before the final message is flushed to disk.
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [weak self] in
                guard let self = self else { return }
                let text = Transcript.lastAssistantText(Transcript.tailLines(self.book.get(req.sessionId)?.transcript ?? ""))
                req.detail = Snippet.clean(text)
                if req.detail.isEmpty { req.detail = "Ready for your next prompt." }
                req.wantsSnippet = false
                guard self.pending.contains(where: { $0 === req }) else { return }
                if self.settings.sound { Sound.play(req.kind) }
                self.showTop()
            }
            return
        }
        if settings.sound { Sound.play(req.kind) }
        showTop()
    }

    // The main row is the newest status change; under it, every other session this app knows
    // about, so one glance covers all of them.
    private func showTop(restart: Bool = true) {
        guard let top = pending.last(where: { !$0.wantsSnippet }) else {
            if pending.isEmpty { toast.hide() }
            return
        }
        let c = ToastContent()
        c.title = top.title
        c.detail = top.detail
        c.kind = top.kind
        c.host = top.host
        c.keycap = settings.optionTab && canJump(top.sessionId, top.appPid)
        let tree = book.tree(top.sessionId, nest: settings.showChildren)
        let now = Date()
        c.rows = tree.prefix(ToastArt.maxRows).map { rowFor($0, now) }
        if tree.count > ToastArt.maxRows { c.overflow = "\(tree.count - ToastArt.maxRows) more in the menu" }
        shown = c
        toast.show(c, seconds: restart ? settings.seconds(top.kind) : -1)
    }

    // A subagent row focuses its session's app, since that is where it runs.
    private func rowFor(_ r: TreeRow, _ now: Date) -> ToastRow {
        if let sub = r.subagent {
            return ToastRow(sessionId: r.session.id, appPid: r.session.appPid, name: sub.type.isEmpty ? "subagent" : sub.type,
                            host: "", status: Status.text(sub, now), state: .working, depth: r.depth)
        }
        return ToastRow(sessionId: r.session.id, appPid: r.session.appPid, name: r.session.project, host: r.session.whereLabel,
                        status: Status.text(r.session, now), state: Status.state(r.session), depth: r.depth)
    }

    private func withdraw(_ sessionId: String) {
        let before = pending.count
        pending.removeAll { $0.sessionId == sessionId }
        if pending.count == before { return }
        if pending.isEmpty { toast.hide() } else if toast.showing { showTop(restart: false) }
    }

    // Click on the main row or Option-Tab: that session. Click on a listed row: that one.
    private func activate(_ target: Int) {
        var sid: String?
        var pid: Int32 = 0
        if target >= 0, let c = shown, target < c.rows.count {
            sid = c.rows[target].sessionId
            pid = c.rows[target].appPid
        } else if let top = pending.last(where: { !$0.wantsSnippet }) {
            sid = top.sessionId
            pid = top.appPid
        }
        pending.removeAll()
        toast.hide()
        if sid != nil || pid != 0 { focusTarget(sid, pid) }
    }

    // ---- sessions and power ---------------------------------------------------------

    private func sweep() {
        // Every 10 minutes, connect agents installed since the app started.
        sweeps += 1
        if sweeps % 120 == 0 && settings.hooks { syncAgents(true) }
        // Accessibility access can be granted at any time; chat watching starts once it is.
        if settings.chats && chats == nil && AXIsProcessTrusted() { startChats(); refreshSettingsNotes() }
        tryApplyUpdate()
        let fm = FileManager.default
        func mtime(_ path: String) -> Date? { (try? fm.attributesOfItem(atPath: path))?[.modificationDate] as? Date }
        let changed = book.sweep(Date(), alive: { Terminals.isAlive($0.pid, $0.pidStart) }, probe: { s in
            guard !s.transcript.isEmpty, let m = mtime(s.transcript) else { return nil }
            var p = TranscriptProbe(modified: m)
            if m > s.transcriptSeen { p.endsInInterrupt = Transcript.endsInInterrupt(Transcript.tailLines(s.transcript)) }
            return p
        }, subagentWrite: { s, id in
            let path = s.subagentTranscript(id)
            return path.isEmpty ? nil : mtime(path)
        })
        let before = pending.count
        pending.removeAll { $0.sessionId != nil && book.get($0.sessionId) == nil }
        if pending.count != before { if pending.isEmpty { toast.hide() } else if toast.showing { showTop() } }
        if changed { refreshState(); if toast.showing { showTop(restart: false) } }
    }

    private func startChats() {
        guard chats == nil else { return }
        guard AXIsProcessTrusted() else {
            chatProblem = "Needs Accessibility access: allow Aevalsistant in System Settings, Privacy & Security, Accessibility."
            return
        }
        chatProblem = nil
        let w = ChatWatcher { [weak self] pid in
            self?.book.all.contains { $0.busy && $0.agent != "chat" && $0.appPid == pid } ?? false
        }
        w.changed = { [weak self] e in self?.ingest(e) }
        chats = w
    }

    private func stopChats() {
        chats?.stop()
        chats = nil
        for s in book.all where s.agent == "chat" { ingest(HookEvent("SessionEnd", s.id)) }
    }

    // ---- updates ----------------------------------------------------------------------

    private func checkForUpdates(manual: Bool) {
        if checkingUpdate { return }
        checkingUpdate = true
        if manual { updateStatus = "Checking for updates" }
        refreshSettingsNotes()
        DispatchQueue.global(qos: .utility).async {
            let r = Updater.check()
            DispatchQueue.main.async { [weak self] in
                guard let self = self else { return }
                self.checkingUpdate = false
                let current = Updater.currentText
                if let app = r.downloaded, let latest = r.latest {
                    self.pendingUpdate = app
                    self.updateStatus = "Version \(Updater.text(latest)) installs when no agent is working"
                    self.tryApplyUpdate()
                } else if let problem = r.problem {
                    self.updateStatus = "Update check failed: " + problem
                } else {
                    self.updateStatus = "Up to date (\(current))"
                    if manual { self.enqueue(ToastRequest(kind: .info, title: "Aevalsistant is up to date", detail: "Version \(current) is the latest release.")) }
                }
                self.refreshSettingsNotes()
            }
        }
    }

    // Restarting forgets which sessions are working, so an update waits until none are.
    private func tryApplyUpdate() {
        guard let path = pendingUpdate, book.busyCount == 0, !toast.showing, FileManager.default.fileExists(atPath: path) else { return }
        pendingUpdate = nil
        if let problem = Updater.apply(path) { updateStatus = "Could not start the update: " + problem }
    }

    private func refreshState() {
        tryApplyUpdate()
        let working = book.busyCount
        let on = working > 0
        let keep = on && settings.keepAwake
        awake.set(system: keep, display: keep && settings.displayOn)

        let wantLid = keep && settings.lidAwake && hasLid
        if wantLid {
            lidSleep?.invalidate()
            lidSleep = nil
            // Once per working stretch: if sleep was already off (the user's own pmset), it is
            // left alone and not turned back on afterwards.
            if settings.lidSaved.isEmpty && !lidTried {
                lidTried = true
                if !Lid.sleepDisabled {
                    if let problem = Lid.setSleepDisabled(true) { lidProblem = "Lid closed: " + problem }
                    else { lidProblem = nil; settings.lidSaved = "1"; save() }
                }
                refreshSettingsNotes()
            }
        } else {
            lidTried = false
            if !settings.lidSaved.isEmpty, Lid.setSleepDisabled(false) == nil {
                settings.lidSaved = ""
                save()
                if !on && Lid.closed {
                    lidSleep?.invalidate()
                    let t = Timer(timeInterval: 60, repeats: false) { [weak self] _ in self?.sleepIfLidStillShut() }
                    RunLoop.main.add(t, forMode: .common)
                    lidSleep = t
                }
            }
        }

        if trayAwake != on {
            trayAwake = on
            statusItem?.button?.image = Theme.menuGlyph(awake: on)
        }
        statusItem?.button?.toolTip = keep ? "Aevalsistant: keeping awake, \(Plural.agents(working)) working"
            : on ? "Aevalsistant: \(Plural.agents(working)) working, sleep allowed"
            : "Aevalsistant: idle, sleep allowed"
    }

    private func sleepIfLidStillShut() {
        lidSleep = nil
        if book.busyCount > 0 || !Lid.closed { return }
        Lid.sleepNow()
    }

    // Shows the password prompt for the lid rule. Cancelling turns the setting off, since it
    // cannot work without the rule.
    private func authorizeLid(fromToggle: Bool) {
        guard let problem = Lid.authorize() else {
            lidProblem = nil
            lidTried = false
            refreshState()
            refreshSettingsNotes()
            return
        }
        settings.lidAwake = false
        save()
        lidToggle?.on = false
        lidProblem = problem == "Cancelled" ? nil : "Lid closed: " + problem
        refreshSettingsNotes()
        if !fromToggle {
            enqueue(ToastRequest(kind: .info, title: "Keep working with the lid closed is off",
                                 detail: "It needs your password once. Turn it on in Settings when you want it."))
        }
    }

    // ---- menu ---------------------------------------------------------------------

    func menuNeedsUpdate(_ menu: NSMenu) { buildMenu() }

    private func buildMenu() {
        menu.removeAllItems()
        let working = book.busyCount
        menu.addItem(label("Aevalsistant " + Updater.currentText, bold: true))
        menu.addItem(label(working == 0 ? "Idle. Sleep is allowed."
            : settings.keepAwake ? "Keeping awake: \(Plural.agents(working)) working"
            : "\(Plural.agents(working)) working. Sleep is allowed."))
        menu.addItem(.separator())

        // Every session, with what it started indented under it.
        let maxItems = 14
        let tree = book.tree(nil, nest: settings.showChildren)
        if tree.isEmpty { menu.addItem(label("No agent sessions yet")) }
        let now = Date()
        for r in tree.prefix(maxItems) {
            let row = rowFor(r, now)
            let color = row.state == .needsYou ? Theme.blush : row.state == .working ? Theme.slate : Theme.slateSoft
            let text = row.host.isEmpty ? row.name : row.name + "  ·  " + row.host
            let item = NSMenuItem(title: text, action: #selector(menuFocus(_:)), keyEquivalent: "")
            item.target = self
            item.attributedTitle = sessionTitle(text, row.status)
            item.image = Theme.dot(r.depth > 0 ? 13 : 16, color, hollow: row.state == .working)
            item.indentationLevel = r.depth
            item.isEnabled = canFocus(row.sessionId, row.appPid)
            item.toolTip = r.subagent == nil && !r.session.cwd.isEmpty ? r.session.cwd : row.name
            item.representedObject = [row.sessionId ?? "", String(row.appPid)]
            menu.addItem(item)
        }
        if tree.count > maxItems { menu.addItem(label("\(tree.count - maxItems) more")) }
        menu.addItem(.separator())

        // Things that need doing stay visible here; everything else lives in Settings.
        for problem in agentProblems.values.sorted() { menu.addItem(label(problem)) }
        for problem in [socketProblem, lidProblem, startupProblem].compactMap({ $0 }) { menu.addItem(label(problem)) }
        if !justConnected.isEmpty { menu.addItem(label("Restart open agent sessions once to connect them")) }
        if pendingUpdate != nil, let s = updateStatus { menu.addItem(label(s)) }
        let s = NSMenuItem(title: "Settings…", action: #selector(openSettings), keyEquivalent: ",")
        s.target = self
        menu.addItem(s)
        let q = NSMenuItem(title: "Quit Aevalsistant", action: #selector(menuQuit), keyEquivalent: "q")
        q.target = self
        menu.addItem(q)
    }

    private func label(_ text: String, bold: Bool = false) -> NSMenuItem {
        let item = NSMenuItem(title: text, action: nil, keyEquivalent: "")
        item.isEnabled = false
        if bold { item.attributedTitle = NSAttributedString(string: text, attributes: [.font: NSFont.menuFont(ofSize: 0).withWeight(.semibold)]) }
        return item
    }

    // "aevalrena  ·  VS Code" on the left and "working 4m" against the right edge.
    private func sessionTitle(_ text: String, _ status: String) -> NSAttributedString {
        let font = NSFont.menuFont(ofSize: 0)
        let p = NSMutableParagraphStyle()
        let width = max(260, ceil((text as NSString).size(withAttributes: [.font: font]).width) + 140)
        p.tabStops = [NSTextTab(textAlignment: .right, location: width)]
        let s = NSMutableAttributedString(string: text, attributes: [.font: font, .paragraphStyle: p])
        s.append(NSAttributedString(string: "\t" + status, attributes: [.font: font, .paragraphStyle: p, .foregroundColor: NSColor.secondaryLabelColor]))
        return s
    }

    @objc private func menuFocus(_ sender: NSMenuItem) {
        guard let parts = sender.representedObject as? [String], parts.count == 2 else { return }
        focusTarget(parts[0].isEmpty ? nil : parts[0], Int32(parts[1]) ?? 0)
    }

    @objc private func menuQuit() { quit() }

    // Shown even with finish cards turned off, since asking for it is the point. The long
    // message shows off the hover expansion.
    private func testToast() {
        let s = book.all.first { canFocus($0.id, $0.appPid) }
        let req = ToastRequest(kind: .done, title: s.map { $0.project + " finished" } ?? "Test notification",
                               detail: (s != nil ? "Click this card, or press Option-Tab while it is up, to jump to that window." : "Start an agent session to jump to it from here.")
                                + " Hover over a card to read its whole message: long replies drop down to several lines, and the card waits while the pointer is on it.")
        req.sessionId = s?.id
        req.appPid = s?.appPid ?? 0
        req.host = s?.whereLabel ?? ""
        if let id = req.sessionId { pending.removeAll { $0.sessionId == id } }
        pending.append(req)
        if settings.sound { Sound.play(req.kind) }
        showTop()
    }

    // ---- settings window --------------------------------------------------------------

    // Options that only matter while another one is on sit under it and hide with it.
    @objc private func openSettings() {
        if let w = settingsWindow {
            if w.isMiniaturized { w.deminiaturize(nil) }
            NSApp.activate(ignoringOtherApps: true)
            w.makeKeyAndOrderFront(nil)
            return
        }
        let w = SettingsWindow()

        w.section("Keep awake")
        let keep = w.addToggle("Keep the Mac awake while agents work",
                               "Your Mac won't sleep until the last agent stops. The screen can still turn off on its usual timer.",
                               settings.keepAwake) { [self] v in settings.keepAwake = v; save(); refreshState() }
        w.addToggle("Keep the screen on too", nil, settings.displayOn, under: keep) { [self] v in settings.displayOn = v; save(); refreshState() }
        if hasLid {
            lidToggle = w.addToggle("Keep working with the lid closed",
                                    "Asks for your password once. When the agents finish with the lid shut, the Mac goes to sleep.",
                                    settings.lidAwake, under: keep) { [self] v in
                settings.lidAwake = v
                lidProblem = nil
                lidTried = false
                save()
                if v && !Lid.authorized { authorizeLid(fromToggle: true) }
                refreshState()
                refreshSettingsNotes()
            }
            lidNote = w.addNote(lidProblem ?? "", under: lidToggle)
        }

        w.section("Notifications")
        let done = w.addToggle("Show a card when an agent finishes", nil, settings.notifyDone) { [self] v in settings.notifyDone = v; save() }
        w.addToggle("Also when background agents finish", "Subagents, and agents that another agent started.",
                    settings.notifyBackgroundDone, under: done) { [self] v in settings.notifyBackgroundDone = v; save() }
        let asks = w.addToggle("Show a card when an agent needs you", nil, settings.notifyNeedsYou) { [self] v in settings.notifyNeedsYou = v; save() }
        w.addToggle("Also when a background agent needs you", nil, settings.notifyBackgroundNeedsYou, under: asks) { [self] v in
            settings.notifyBackgroundNeedsYou = v; save()
        }
        let sound = w.addToggle("Play a sound", "A soft chime with each card. Quiet while an app is full screen.", settings.sound) { [self] v in
            settings.sound = v; save()
        }
        w.addButton("Finished", under: sound) { Sound.play(.done) }
        w.addButton("Needs you", under: sound) { Sound.play(.needsYou) }
        w.addToggle("Expand the card on hover to show the whole message", nil, settings.expandOnHover) { [self] v in
            settings.expandOnHover = v; toast.expandOnHover = v; save()
        }
        w.addToggle("Option-Tab jumps to the agent while the card is up", nil, settings.optionTab) { [self] v in settings.optionTab = v; save() }
        w.addChoice("Card stays up", ["Short", "Normal", "Long"], settings.cardTime) { [self] i in settings.cardTime = i; save() }
        w.addButton("Show a test notification") { [self] in testToast() }

        w.section("Agents")
        let hooks = w.addToggle("Connect coding agents", "Adds Aevalsistant to each agent's hook settings. Restart open sessions once after a change.",
                                settings.hooks) { [self] v in settings.hooks = v; save(); syncAgents(v); refreshSettingsNotes() }
        agentToggles.removeAll()
        for a in Agents.all where agentPresent(a) {
            let id = a.id
            agentToggles[id] = w.addToggle(a.name, agentStatus(a), !settings.agentsOff.contains(id), under: hooks) { [self] v in
                if v { settings.agentsOff.remove(id) } else { settings.agentsOff.insert(id) }
                save()
                syncAgents(settings.hooks)
                refreshSettingsNotes()
            }
        }
        w.addToggle("List subagents under their session", "In the card and the menu.", settings.showChildren) { [self] v in
            settings.showChildren = v
            save()
            if toast.showing { showTop(restart: false) }
        }
        let chat = w.addToggle("Watch Claude and ChatGPT desktop chats", "Notices a reply finishing while you are in another app.", settings.chats) { [self] v in
            settings.chats = v
            chatProblem = nil
            save()
            if v { startChats() } else { stopChats() }
            refreshSettingsNotes()
        }
        chatNote = w.addNote(chatProblem ?? "", under: chat)
        w.addButton("Open Accessibility settings", under: chat) {
            // Adds the app to the list (switched off) and opens the pane, so it is one click.
            _ = AXIsProcessTrustedWithOptions(["AXTrustedCheckOptionPrompt": true] as CFDictionary)
            if let u = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") { NSWorkspace.shared.open(u) }
        }

        w.section("General")
        w.addToggle("Open at login", nil, settings.startup) { [self] v in settings.startup = v; save(); setStartup(v); refreshSettingsNotes() }
        w.addToggle("Update automatically", "Checks this app's GitHub releases two minutes after start and every six hours.",
                    settings.autoUpdate) { [self] v in settings.autoUpdate = v; save() }
        w.addButton("Check for updates") { [self] in checkForUpdates(manual: true) }
        updateNote = w.addNote(updateLine())
        w.addLink("Release notes", "https://github.com/" + Updater.repo + "/releases")
        w.addLink("Report a problem", "https://github.com/" + Updater.repo + "/issues/new/choose")
        w.addButton("Remove from this Mac", danger: true) { [self] in uninstall() }

        w.onClose = { [weak self] in
            self?.settingsWindow = nil
            self?.updateNote = nil
            self?.lidNote = nil
            self?.chatNote = nil
            self?.lidToggle = nil
            self?.agentToggles.removeAll()
        }
        settingsWindow = w
        w.present()
    }

    private func updateLine() -> String { updateStatus ?? "Version " + Updater.currentText }

    private func agentStatus(_ a: AgentSpec) -> String {
        if let problem = agentProblems[a.id] { return problem }
        if !settings.hooks || settings.agentsOff.contains(a.id) { return "Not connected" }
        if !agentConnected(a) { return "Not connected yet" }
        return justConnected.contains(a.id) && !a.note.isEmpty ? "Connected. " + a.note : "Connected"
    }

    // Status lines in an open Settings window follow what happens elsewhere.
    private func refreshSettingsNotes() {
        guard settingsWindow != nil else { return }
        updateNote?.text = updateLine()
        lidNote?.text = lidProblem ?? ""
        chatNote?.text = settings.chats ? (chatProblem ?? "") : ""
        for a in Agents.all { agentToggles[a.id]?.note = agentStatus(a) }
    }

    // ---- install surface ----------------------------------------------------------

    private func agentFile(_ a: AgentSpec) -> String { AppPaths.userHome + "/" + a.file }

    // Installed means the tool's config folder exists. Claude Code is the exception: its folder
    // is created if missing, since it is the main reason this app exists.
    private func agentPresent(_ a: AgentSpec) -> Bool {
        var dir: ObjCBool = false
        return a.id == "claude" || (FileManager.default.fileExists(atPath: AppPaths.userHome + "/" + a.dir, isDirectory: &dir) && dir.boolValue)
    }

    private func agentConnected(_ a: AgentSpec) -> Bool {
        guard let text = try? String(contentsOfFile: agentFile(a), encoding: .utf8) else { return false }
        return Agents.isInstalled(a, text, Agents.commandFor(a, baseCommand))
    }

    // Adds our hooks to every coding agent found on this Mac, or removes them from all. Each
    // file is backed up once (name.aevalsistant.bak) before its first change.
    private func syncAgents(_ install: Bool) {
        let fm = FileManager.default
        for a in Agents.all {
            let path = agentFile(a)
            let shown = "~/" + a.file
            let text = try? String(contentsOfFile: path, encoding: .utf8)
            if fm.fileExists(atPath: path) && text == nil {
                agentProblems[a.id] = "\(a.name): could not read \(shown)"
                continue
            }
            let add = install && agentPresent(a) && !settings.agentsOff.contains(a.id)
            if text == nil && !add { agentProblems.removeValue(forKey: a.id); continue }
            do {
                let (next, changed) = try Agents.apply(a, text, add ? Agents.commandFor(a, baseCommand) : nil)
                if changed {
                    try fm.createDirectory(atPath: (path as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
                    let backup = path + ".aevalsistant.bak"
                    if a.layout != .own, let text = text, !fm.fileExists(atPath: backup) { try text.write(toFile: backup, atomically: true, encoding: .utf8) }
                    if let next = next { try writeConfig(path, next) } else { try fm.removeItem(atPath: path) }
                    if add { justConnected.insert(a.id) }
                }
                agentProblems.removeValue(forKey: a.id)
            } catch is AgentSpecError {
                agentProblems[a.id] = "\(a.name): \(shown) is not plain JSON; hooks not added"
            } catch {
                agentProblems[a.id] = "\(a.name): could not write \(shown) (\(error.localizedDescription))"
            }
        }
    }

    // Written next to the original and swapped in, so a crash or power cut mid-write cannot
    // leave an agent's settings half written. A symlink (dotfile managers) is written through
    // in place, so it stays a symlink.
    private func writeConfig(_ path: String, _ text: String) throws {
        let fm = FileManager.default
        if let type = (try? fm.attributesOfItem(atPath: path))?[.type] as? FileAttributeType, type == .typeSymbolicLink {
            let target = (try? fm.destinationOfSymbolicLink(atPath: path)).map { $0.hasPrefix("/") ? $0 : ((path as NSString).deletingLastPathComponent as NSString).appendingPathComponent($0) } ?? path
            try text.write(toFile: target, atomically: false, encoding: .utf8)
            return
        }
        try text.write(toFile: path, atomically: true, encoding: .utf8)
    }

    private func setStartup(_ on: Bool) {
        startupProblem = nil
        let service = SMAppService.mainApp
        do {
            if on && service.status != .enabled { try service.register() }
            if !on && service.status == .enabled { try service.unregister() }
        } catch {
            startupProblem = on ? "Could not add Aevalsistant to login items" : nil
        }
    }

    private func uninstall() {
        let a = NSAlert()
        a.messageText = "Remove Aevalsistant?"
        a.informativeText = "Its hooks in every coding agent, its login item, its lid setting, and its folder in Application Support will be deleted, and the app moves to the Trash.\n\nRestart open agent sessions afterwards so they stop calling it."
        a.addButton(withTitle: "Cancel")
        a.addButton(withTitle: "Remove")
        a.buttons[1].hasDestructiveAction = true
        NSApp.activate(ignoringOtherApps: true)
        guard a.runModal() == .alertSecondButtonReturn else { return }
        syncAgents(false)
        setStartup(false)
        if !settings.lidSaved.isEmpty, Lid.setSleepDisabled(false) == nil { settings.lidSaved = "" }
        _ = Lid.revoke()
        server?.stop()
        server = nil
        try? FileManager.default.removeItem(atPath: AppPaths.home)
        try? FileManager.default.trashItem(at: Bundle.main.bundleURL, resultingItemURL: nil)
        quit(saving: false)
    }

    private func save() { settings.save(AppPaths.settings) }

    private func cleanUpForQuit() {
        if quitting { return }
        quitting = true
        sweepTimer?.invalidate()
        updateTimer?.invalidate()
        lidSleep?.invalidate()
        if !settings.lidSaved.isEmpty, Lid.setSleepDisabled(false) == nil {
            settings.lidSaved = ""
            if FileManager.default.fileExists(atPath: AppPaths.home) { save() }
        }
        chats?.stop()
        awake.set(system: false, display: false)
        server?.stop()
    }

    private func quit(saving: Bool = true) {
        cleanUpForQuit()
        if saving && FileManager.default.fileExists(atPath: AppPaths.home) { save() }
        settingsWindow?.close()
        toast.hide()
        NSApp.terminate(nil)
    }
}

private extension NSFont {
    func withWeight(_ w: NSFont.Weight) -> NSFont { NSFont.systemFont(ofSize: pointSize, weight: w) }
}
