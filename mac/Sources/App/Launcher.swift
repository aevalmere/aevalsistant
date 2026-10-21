import AppKit

// Starting the app: install into an Applications folder if needed, hand off to a copy that is
// already running, or become the running copy.
enum Launcher {
    static func start() {
        signal(SIGPIPE, SIG_IGN)
        let bundle = Bundle.main.bundleURL.resolvingSymlinksInPath()

        // Run from Downloads or a disk image: copy into Applications so the hook path and the
        // login item never move, then start that copy.
        if bundle.pathExtension == "app" && !isInstalled(bundle) { exit(installAndHandOff(bundle)) }

        if Ipc.isRunning {
            Ipc.send("{\"cmd\":\"hello\"}")
            exit(0)
        }
        let app = NSApplication.shared
        let controller = AppController()
        app.delegate = controller
        app.setActivationPolicy(.accessory)
        app.mainMenu = mainMenu()
        // NSApplication holds its delegate weakly.
        withExtendedLifetime(controller) { app.run() }
    }

    // Both sides resolved, since /var and /tmp are links to /private on macOS.
    static func isInstalled(_ bundle: URL) -> Bool {
        let parent = bundle.deletingLastPathComponent().resolvingSymlinksInPath().path
        return AppPaths.installRoots.contains { URL(fileURLWithPath: $0).resolvingSymlinksInPath().path == parent }
    }

    // Where this copy goes: over an existing install, else /Applications when this account can
    // write there (admins can), else the Applications folder in the home folder.
    static var target: URL {
        for root in AppPaths.installRoots {
            let u = URL(fileURLWithPath: root).appendingPathComponent("Aevalsistant.app")
            if FileManager.default.fileExists(atPath: u.path) { return u }
        }
        let root = FileManager.default.isWritableFile(atPath: AppPaths.installRoots[0]) ? AppPaths.installRoots[0] : AppPaths.installRoots[1]
        return URL(fileURLWithPath: root).appendingPathComponent("Aevalsistant.app")
    }

    private static func installAndHandOff(_ bundle: URL) -> Int32 {
        let target = self.target
        let running = Ipc.isRunning
        let same = sameBuild(bundle, target)

        if running && same {
            Ipc.send("{\"cmd\":\"hello\"}")
            return 0
        }
        if running {
            Ipc.send("{\"cmd\":\"quit\"}")
            for _ in 0..<50 where Ipc.isRunning { usleep(100_000) }
        }
        if !same {
            if let problem = replace(target, with: bundle) {
                alert("Could not copy Aevalsistant to \(target.deletingLastPathComponent().path).\n\n\(problem)")
                // The installed copy quit to make room; start it again rather than leave nothing running.
                if running && FileManager.default.fileExists(atPath: target.path) { _ = open(target) }
                return 1
            }
        }
        return open(target) ? 0 : 1
    }

    private static func sameBuild(_ a: URL, _ b: URL) -> Bool {
        let fm = FileManager.default
        return ["Contents/MacOS/Aevalsistant", "Contents/Info.plist"].allSatisfy {
            fm.contentsEqual(atPath: a.appendingPathComponent($0).path, andPath: b.appendingPathComponent($0).path)
        }
    }

    // A running app's files can be replaced on macOS: it keeps the ones it opened. The old copy
    // goes first so nothing of it lingers in the new one.
    private static func replace(_ target: URL, with bundle: URL) -> String? {
        let fm = FileManager.default
        do {
            try fm.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
            if fm.fileExists(atPath: target.path) { try fm.removeItem(at: target) }
        } catch { return error.localizedDescription }
        let copy = Lid.run("/usr/bin/ditto", [bundle.path, target.path])
        if copy.status != 0 { return "ditto could not copy it" }
        // The download mark would bring Gatekeeper back for a copy the user already opened.
        Lid.run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", target.path])
        return nil
    }

    private static func open(_ app: URL) -> Bool {
        if Lid.run("/usr/bin/open", ["-n", app.path]).status == 0 { return true }
        alert("Could not start \(app.path).")
        return false
    }

    static func alert(_ text: String) {
        NSApplication.shared.setActivationPolicy(.accessory)
        NSApp.activate(ignoringOtherApps: true)
        let a = NSAlert()
        a.messageText = "Aevalsistant"
        a.informativeText = text
        a.alertStyle = .warning
        a.runModal()
    }

    // An accessory app has no menu bar of its own, but its key equivalents still work while
    // Settings is in front: Command-W closes it and Command-Q quits.
    private static func mainMenu() -> NSMenu {
        let main = NSMenu()
        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "Close Window", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        appMenu.addItem(withTitle: "Quit Aevalsistant", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        main.addItem(appItem)
        return main
    }
}
