import Foundation
import Darwin

// `Aevalsistant --hook [agent] [event]`, called by a coding agent with its hook payload on
// stdin. Adds which app and which agent process this came from, forwards it, and exits 0.
enum HookClient {
    static func run(agent: String, event: String) -> Int32 {
        // A hook must never fail the agent that ran it. Nothing here throws; a closed pipe would
        // raise SIGPIPE, which would end the process with a signal instead of 0.
        signal(SIGPIPE, SIG_IGN)
        forward(agent, event)
        reply(agent)
        return 0
    }

    private static func forward(_ agent: String, _ event: String) {
        // App not running: return at once instead of reading a payload nobody wants.
        guard let fd = Ipc.connect() else { return }
        defer { close(fd) }
        let raw = readStdin(limit: 2).trimmingCharacters(in: .whitespacesAndNewlines)
        if raw.isEmpty { return }

        let started = Terminals.info(getpid())?.start ?? Int64(Date().timeIntervalSince1970 * 1_000_000)
        let pwd = FileManager.default.currentDirectoryPath
        let t = Terminals.fromHookProcess(agent)
        let envelope = "{\"t\":\(started)"
            + ",\"agent\":" + Json.quote(agent)
            + ",\"event\":" + Json.quote(event)
            + ",\"pwd\":" + Json.quote(pwd)
            + ",\"host\":" + Json.quote(t.host)
            + ",\"app\":\(t.appPid)"
            + ",\"pid\":\(t.agentPid)"
            + ",\"pidStart\":\(t.agentStart)"
            + ",\"outer\":\(t.outerPid)"
            + ",\"outerStart\":\(t.outerStart)"
            + ",\"raw\":" + Json.quote(raw) + "}"
        Ipc.writeAll(fd, envelope)
    }

    // Cursor reads a JSON answer from its hooks: beforeSubmitPrompt takes "continue": true to
    // let the prompt through, and the others ignore it. Other agents read nothing.
    private static func reply(_ agent: String) {
        guard agent == "cursor" else { return }
        let bytes = Array("{\"continue\":true}".utf8)
        _ = bytes.withUnsafeBytes { Darwin.write(1, $0.baseAddress, $0.count) }
    }

    private final class Box {
        var data = Data()
        let lock = NSLock()
    }

    // Up to 4 MB, and no longer than `limit` seconds: an agent that never closes stdin still
    // gets its hook back.
    private static func readStdin(limit: TimeInterval) -> String {
        let box = Box()
        let done = DispatchSemaphore(value: 0)
        let reader = Thread {
            var buf = [UInt8](repeating: 0, count: 1 << 16)
            while true {
                let n = buf.withUnsafeMutableBytes { Darwin.read(0, $0.baseAddress, $0.count) }
                if n <= 0 { break }
                box.lock.lock()
                box.data.append(contentsOf: buf[0..<n])
                let full = box.data.count >= 4 << 20
                box.lock.unlock()
                if full { break }
            }
            done.signal()
        }
        reader.start()
        _ = done.wait(timeout: .now() + limit)
        box.lock.lock()
        defer { box.lock.unlock() }
        return String(decoding: box.data, as: UTF8.self)
    }
}
