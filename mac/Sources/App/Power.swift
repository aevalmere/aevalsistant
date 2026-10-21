import AppKit
import IOKit
import IOKit.pwr_mgt

// A named power assertion, visible in `pmset -g assertions` as "Aevalsistant: AI agents are
// running". The system one stops idle sleep; the display one also keeps the screen on.
final class AwakeAssertion {
    private static let reason = "Aevalsistant: AI agents are running" as CFString
    private var system: IOPMAssertionID = 0
    private var display: IOPMAssertionID = 0
    private(set) var systemOn = false
    private(set) var displayOn = false

    func set(system keepSystem: Bool, display keepDisplay: Bool) {
        if keepSystem != systemOn {
            if keepSystem {
                systemOn = IOPMAssertionCreateWithName(kIOPMAssertionTypePreventUserIdleSystemSleep as CFString,
                                                       IOPMAssertionLevel(kIOPMAssertionLevelOn), AwakeAssertion.reason, &system) == kIOReturnSuccess
            } else {
                IOPMAssertionRelease(system)
                systemOn = false
            }
        }
        if keepDisplay != displayOn {
            if keepDisplay {
                displayOn = IOPMAssertionCreateWithName(kIOPMAssertionTypePreventUserIdleDisplaySleep as CFString,
                                                        IOPMAssertionLevel(kIOPMAssertionLevelOn), AwakeAssertion.reason, &display) == kIOReturnSuccess
            } else {
                IOPMAssertionRelease(display)
                displayOn = false
            }
        }
    }
}

// Power assertions do not stop a MacBook from sleeping when its lid closes; only `pmset
// disablesleep` does, and that needs root. Turning the option on installs one sudoers rule,
// after asking for the user's password once, that lets this user run exactly
// `pmset -a disablesleep 0` and `pmset -a disablesleep 1`. The app turns sleep off while agents
// work and back on after, and "1" in settings.ini marks the override so a crash is repaired
// on the next start.
enum Lid {
    static let rule = "/private/etc/sudoers.d/aevalsistant"

    // Laptops publish the clamshell state; desktops do not have it at all.
    static var hasLid: Bool { clamshell() != nil }
    static var closed: Bool { clamshell() == true }

    private static func clamshell() -> Bool? {
        let root = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("IOPMrootDomain"))
        guard root != 0 else { return nil }
        defer { IOObjectRelease(root) }
        guard let v = IORegistryEntryCreateCFProperty(root, "AppleClamshellState" as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() else { return nil }
        return (v as? Bool) ?? (v as? NSNumber)?.boolValue
    }

    static var authorized: Bool { FileManager.default.fileExists(atPath: rule) }

    // Shows the system password prompt. Returns nil once the rule is in place, or why not.
    static func authorize() -> String? {
        let user = NSUserName()
        guard !user.isEmpty, user.unicodeScalars.allSatisfy({ CharacterSet.alphanumerics.contains($0) && $0.isASCII || "._-".unicodeScalars.contains($0) }) else {
            return "Your account name has characters sudo cannot take"
        }
        let line = "\(user) ALL=(root) NOPASSWD: /usr/bin/pmset -a disablesleep 0, /usr/bin/pmset -a disablesleep 1"
        // visudo checks the rule before it goes in, so a bad file can never lock sudo out.
        let script = """
        t=$(/usr/bin/mktemp /private/tmp/aevalsistant.XXXXXX) || exit 1
        /usr/bin/printf '%s\\n' '\(line)' > "$t"
        if /usr/sbin/visudo -cf "$t" >/dev/null; then
          /usr/sbin/chown root:wheel "$t" && /bin/chmod 0440 "$t" && /bin/mkdir -p /private/etc/sudoers.d && /bin/mv -f "$t" \(rule)
        else
          /bin/rm -f "$t"; exit 1
        fi
        """
        return asAdmin(script, prompt: "Aevalsistant needs your password once so your Mac can keep working while the lid is closed.")
    }

    // For "Remove from this Mac". Returns nil when the rule is gone.
    static func revoke() -> String? {
        guard authorized else { return nil }
        return asAdmin("/bin/rm -f \(rule)", prompt: "Aevalsistant needs your password to remove its lid setting.")
    }

    private static func asAdmin(_ shell: String, prompt: String) -> String? {
        func quoted(_ s: String) -> String { "\"" + s.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") + "\"" }
        let source = "do shell script \(quoted(shell)) with prompt \(quoted(prompt)) with administrator privileges"
        var error: NSDictionary?
        _ = NSAppleScript(source: source)?.executeAndReturnError(&error)
        guard let error = error else { return nil }
        if (error[NSAppleScript.errorNumber] as? Int) == -128 { return "Cancelled" }
        return (error[NSAppleScript.errorMessage] as? String) ?? "The change was refused"
    }

    // Whether sleep is already turned off, by the user or by an earlier run.
    static var sleepDisabled: Bool {
        let out = run("/usr/bin/pmset", ["-g"]).output
        return out.split(separator: "\n").contains { line in
            let parts = line.split(whereSeparator: { $0 == " " || $0 == "\t" })
            return parts.count == 2 && parts[0] == "SleepDisabled" && parts[1] == "1"
        }
    }

    // Returns nil when it worked, or why not.
    static func setSleepDisabled(_ on: Bool) -> String? {
        let r = run("/usr/bin/sudo", ["-n", "/usr/bin/pmset", "-a", "disablesleep", on ? "1" : "0"])
        if r.status == 0 { return nil }
        return authorized ? "pmset refused the change" : "Needs your password once: turn the setting off and on again"
    }

    // Any user may put the Mac to sleep; this needs no rule.
    static func sleepNow() { _ = run("/usr/bin/pmset", ["sleepnow"]) }

    @discardableResult
    static func run(_ exe: String, _ args: [String]) -> (status: Int32, output: String) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: exe)
        p.arguments = args
        let out = Pipe()
        p.standardOutput = out
        p.standardError = FileHandle.nullDevice
        p.standardInput = FileHandle.nullDevice
        do { try p.run() } catch { return (-1, "") }
        let data = out.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return (p.terminationStatus, String(decoding: data, as: UTF8.self))
    }
}

// The optional chime when a card appears. Both sounds come from tools/make-sounds.py and ship
// inside the app bundle.
enum Sound {
    private static var done: NSSound?
    private static var needsYou: NSSound?

    static func play(_ kind: ToastKind) {
        if kind == .info || fullScreenInFront() { return }
        let s: NSSound?
        if kind == .done {
            if done == nil { done = load("done") }
            s = done
        } else {
            if needsYou == nil { needsYou = load("needs-you") }
            s = needsYou
        }
        // A newer chime cuts off one still ringing.
        done?.stop()
        needsYou?.stop()
        s?.play()
    }

    private static func load(_ name: String) -> NSSound? { Theme.resource(name, "wav").flatMap { NSSound(contentsOf: $0, byReference: false) } }

    // macOS holds its own alert sounds back for a presentation or a full-screen game; the
    // nearest public sign of either is a window of the app in front covering a whole screen.
    private static func fullScreenInFront() -> Bool {
        guard let front = NSWorkspace.shared.frontmostApplication?.processIdentifier,
              let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else { return false }
        let screens = NSScreen.screens.map { $0.frame.size }
        for w in list {
            guard (w[kCGWindowOwnerPID as String] as? Int32) == front, (w[kCGWindowLayer as String] as? Int) == 0,
                  let b = w[kCGWindowBounds as String] as? [String: CGFloat], let width = b["Width"], let height = b["Height"] else { continue }
            if screens.contains(where: { $0.width == width && $0.height == height }) { return true }
        }
        return false
    }
}
