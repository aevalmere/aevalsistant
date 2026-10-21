#if canImport(AppKit)
import AppKit

// Checks that need AppKit: the card's layout, the settings page's show-and-hide rules, and
// PNG renders of both for review, the counterpart of tests/ToastTests.cs and SettingsPreview.cs.
enum AppTests {
    static func run(_ dir: String?) {
        _ = NSApplication.shared
        if let assets = ProcessInfo.processInfo.environment["AEVAL_ASSETS"] { Theme.assetFolder = assets }
        toastLayout()
        settingsRows(dir)
        glyph()
        if let dir = dir { toastPreviews(dir) }
    }

    static let rows = [
        ToastRow(name: "BiobuzzRobot", host: "Android Studio", status: "needs you", state: .needsYou),
        ToastRow(name: "kymarion-nav", host: "Terminal", status: "done 3m ago", state: .done),
        ToastRow(name: "Explore", host: "", status: "running 2m", state: .working, depth: 1),
        ToastRow(name: "gnce-site", host: "VS Code", status: "working 12m", state: .working),
        ToastRow(name: "lecture-study-tool", host: "Claude", status: "working", state: .working),
    ]

    static func content(_ title: String, _ host: String, _ detail: String, _ kind: ToastKind, keycap: Bool, rows: [ToastRow]) -> ToastContent {
        let c = ToastContent()
        c.title = title
        c.host = host
        c.detail = detail
        c.kind = kind
        c.keycap = keycap
        c.rows = rows
        return c
    }

    static var cases: [ToastContent] {
        [
            content("aevalrena finished", "VS Code", "Fixed the LAN lobby: clients now reconnect after the host restarts, and the lobby list refreshes on its own every few seconds so nobody has to rejoin by hand.", .done, keycap: true, rows: rows),
            content("BiobuzzRobot needs you", "Android Studio", "Claude wants to edit DriveSubsystem.java", .needsYou, keycap: true, rows: Array(rows[1...1])),
            content("Aevalsistant is running", "", "Restart open Claude Code sessions once so they report here.", .info, keycap: false, rows: []),
        ]
    }

    static func toastLayout() {
        let c = cases
        let pad = ToastArt.pad, head = ToastArt.headH
        check(ToastArt.hitTest(c[0], 100, 50, 0) == ToastArt.hitHead
              && ToastArt.hitTest(c[0], 100, pad + head + ToastArt.listTop + ToastArt.rowH * 2 + 5, 0) == 2
              && ToastArt.hitTest(c[0], 2, 50, 0) == ToastArt.hitNone
              && ToastArt.height(c[2], 0) == head, "art: hit test and height follow the list")
        check(ToastArt.extraHeight(c[0]) > 0 && ToastArt.extraHeight(c[1]) == 0, "art: a long detail opens on hover, a short one does not")
        check(ToastArt.height(c[0], 1) > ToastArt.height(c[0], 0) && ToastArt.surfaceHeight(c[0]) == ToastArt.height(c[0], 1) + pad * 2, "art: the surface fits the open card")
        let img = ToastArt.image(c[0], scale: 2, hover: ToastArt.hitNone, avatar: Theme.image("avatar"), expand: 0)
        check(img != nil && img!.width == Int(ToastArt.surfaceWidth * 2), "art: the card renders at 2x")
        check(Theme.image("avatar") != nil, "art: avatar found")
    }

    static func settingsRows(_ dir: String?) {
        var awakeCalls = 0, stayCalls = 0
        let w = SettingsWindow()
        w.section("Keep awake")
        let awake = w.addToggle("Keep the Mac awake while agents work",
                                "Your Mac won't sleep until the last agent stops. The screen can still turn off on its usual timer.", true) { _ in awakeCalls += 1 }
        let screen = w.addToggle("Keep the screen on too", nil, true, under: awake) { _ in }
        let lid = w.addToggle("Keep working with the lid closed", "Asks for your password once. When the agents finish with the lid shut, the Mac goes to sleep.", true, under: awake) { _ in }

        w.section("Notifications")
        let finish = w.addToggle("Show a card when an agent finishes", nil, true) { _ in }
        w.addToggle("Also when background agents finish", "Subagents, and agents that another agent started.", false, under: finish) { _ in }
        let needs = w.addToggle("Show a card when an agent needs you", nil, true) { _ in }
        w.addToggle("Also when a background agent needs you", nil, true, under: needs) { _ in }
        let sound = w.addToggle("Play a sound", "A soft chime with each card. Quiet while an app is full screen.", false) { _ in }
        w.addButton("Finished", under: sound) {}
        w.addToggle("Expand the card on hover to show the whole message", nil, true) { _ in }
        w.addToggle("Option-Tab jumps to the agent while the card is up", nil, true) { _ in }
        let stay = w.addChoice("Card stays up", ["Short", "Normal", "Long"], 1) { _ in stayCalls += 1 }
        w.addButton("Show a test notification") {}

        w.section("Agents")
        let agents = w.addToggle("Connect coding agents", "Adds Aevalsistant to each agent's hook settings. Restart open sessions once after a change.", true) { _ in }
        w.addToggle("Claude Code", "Connected", true, under: agents) { _ in }
        w.addToggle("Codex", "Connected. Run /hooks once in Codex to trust the new hooks.", true, under: agents) { _ in }
        let cursor = w.addToggle("Copilot CLI", "Connected", true, under: agents) { _ in }
        w.addToggle("List subagents under their session", "In the card and the menu.", true) { _ in }
        let chats = w.addToggle("Watch Claude and ChatGPT desktop chats", "Notices a reply finishing while you are in another app.", true) { _ in }
        w.addNote("Needs Accessibility access: allow Aevalsistant in System Settings, Privacy & Security, Accessibility.", under: chats)
        w.addButton("Open Accessibility settings", under: chats) {}

        w.section("General")
        w.addToggle("Open at login", nil, true) { _ in }
        w.addToggle("Update automatically", "Checks this app's GitHub releases two minutes after start and every six hours.", true) { _ in }
        w.addButton("Check for updates") {}
        let status = w.addNote("Up to date (1.4.0)")
        w.addLink("Release notes", "https://github.com/aevalmere/aevalsistant/releases")
        w.addLink("Report a problem", "https://github.com/aevalmere/aevalsistant/issues/new/choose")
        w.addButton("Remove from this Mac", danger: true) {}

        check(screen.shown && lid.shown && cursor.shown, "settings: rows under an on toggle are shown")
        if let dir = dir { save(w, dir, "settings-on") }
        let full = w.pageHeight

        awake.on = false
        check(!screen.shown && !lid.shown && awakeCalls == 0, "settings: a parent turned off from code hides its rows without calling back")
        if let dir = dir { save(w, dir, "settings-off") }
        check(w.pageHeight < full, "settings: the page gets shorter when rows close")

        awake.click()
        check(awake.on && screen.shown && lid.shown && awakeCalls == 1, "settings: a click turns the parent on, shows its rows, and calls back once")

        stay.selected = 2
        check(stay.selected == 2 && stayCalls == 0, "settings: choosing from code does not call back")
        stay.click(0)
        check(stay.selected == 0 && stayCalls == 1, "settings: a click on a segment calls back once")

        status.text = ""
        check(!status.shown, "settings: an empty note is hidden")
        status.text = "Up to date (1.4.0)"
        check(status.shown, "settings: a note with text is shown")

        let withNote = w.pageHeight
        lid.note = ""
        check(w.pageHeight < withNote, "settings: clearing a toggle's note drops its second line")
        lid.note = "Asks for your password once."
        w.close()
    }

    static func glyph() {
        let idle = Theme.menuGlyph(awake: false), busy = Theme.menuGlyph(awake: true)
        check(idle.isTemplate && busy.isTemplate && idle.size == NSSize(width: 18, height: 18), "glyph: template images at menu bar size")
    }

    static func save(_ w: SettingsWindow, _ dir: String, _ name: String) {
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        guard let rep = w.snapshot(), let png = rep.representation(using: .png, properties: [:]) else { check(false, "settings: \(name) renders"); return }
        try? png.write(to: URL(fileURLWithPath: dir + "/" + name + ".png"))
    }

    static func toastPreviews(_ dir: String) {
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        let avatar = Theme.image("avatar")
        for (i, c) in cases.enumerated() {
            for scale in [1.0, 2.0] as [CGFloat] {
                for (name, expand, hover) in [("closed", CGFloat(0), ToastArt.hitNone), ("open", CGFloat(1), 2)] where expand == 0 || ToastArt.extraHeight(c) > 0 {
                    guard let card = ToastArt.image(c, scale: scale, hover: hover, avatar: avatar, expand: expand) else { continue }
                    for (bgName, bg) in [("light", Theme.hex(0xECECEE)), ("dark", Theme.hex(0x1E1F22))] {
                        let w = card.width + Int(40 * scale), h = card.height + Int(20 * scale)
                        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: w, pixelsHigh: h, bitsPerSample: 8, samplesPerPixel: 4,
                                                   hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
                        NSGraphicsContext.saveGraphicsState()
                        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
                        let ctx = NSGraphicsContext.current!.cgContext
                        ctx.setFillColor(bg.cgColor)
                        ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
                        ctx.draw(card, in: CGRect(x: Int(20 * scale), y: Int(20 * scale), width: card.width, height: card.height))
                        NSGraphicsContext.restoreGraphicsState()
                        let file = dir + "/toast-\(i)-\(name)-\(Int(scale))x-\(bgName).png"
                        try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: file))
                    }
                }
            }
        }
        // The menu bar glyph, idle and awake, at menu bar size and enlarged.
        let sheet = NSImage(size: NSSize(width: 200, height: 90), flipped: false) { _ in
            Theme.hex(0xF2F2F2).setFill()
            NSRect(x: 0, y: 0, width: 200, height: 90).fill()
            for (i, awake) in [false, true].enumerated() {
                let g = Theme.menuGlyph(awake: awake)
                g.draw(in: NSRect(x: 10 + CGFloat(i) * 100, y: 60, width: 18, height: 18))
                g.draw(in: NSRect(x: 40 + CGFloat(i) * 100, y: 10, width: 54, height: 54))
            }
            return true
        }
        if let tiff = sheet.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff), let png = rep.representation(using: .png, properties: [:]) {
            try? png.write(to: URL(fileURLWithPath: dir + "/menu-glyphs.png"))
        }
    }
}
#endif
