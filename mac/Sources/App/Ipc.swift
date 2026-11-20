import Foundation
import Darwin

enum AppPaths {
    static let bundleId = "io.github.aevalmere.aevalsistant"

    // HOME first, so a test run can point the whole app at a scratch folder.
    static let userHome: String = {
        if let h = ProcessInfo.processInfo.environment["HOME"], !h.isEmpty { return h }
        return NSHomeDirectory()
    }()

    static let home = userHome + "/Library/Application Support/Aevalsistant"
    static let settings = home + "/settings.ini"
    static let time = home + "/time.ini"

    // Unix socket paths are limited to 104 bytes; a very long home folder falls back to /tmp,
    // where the socket is still only reachable by this user.
    static let socket: String = {
        let p = home + "/hook.sock"
        return p.utf8.count < 100 ? p : "/tmp/aevalsistant-\(getuid()).sock"
    }()

    // The app counts as installed in either Applications folder.
    static let installRoots = ["/Applications", userHome + "/Applications"]
}

// The hook client and a second copy of the app talk to the running app over a Unix socket in
// its Application Support folder. A message is one JSON document; the client closes its side
// when done, and may wait for one reply (--status does).
enum Ipc {
    static func connect(timeout: Int = 2) -> Int32? {
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { return nil }
        var on: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
        var tv = timeval(tv_sec: timeout, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        var addr = address(AppPaths.socket)
        let ok = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) }
        }
        if ok != 0 { close(fd); return nil }
        return fd
    }

    static func address(_ path: String) -> sockaddr_un {
        var addr = sockaddr_un()
        addr.sun_family = sa_family_t(AF_UNIX)
        addr.sun_len = UInt8(MemoryLayout<sockaddr_un>.size)
        let bytes = Array(path.utf8.prefix(103))
        withUnsafeMutableBytes(of: &addr.sun_path) { raw in
            for (i, b) in bytes.enumerated() { raw[i] = b }
            raw[bytes.count] = 0
        }
        return addr
    }

    @discardableResult
    static func writeAll(_ fd: Int32, _ text: String) -> Bool {
        let bytes = Array(text.utf8)
        var off = 0
        while off < bytes.count {
            let n = bytes[off...].withUnsafeBytes { Darwin.write(fd, $0.baseAddress, $0.count) }
            if n <= 0 { return false }
            off += n
        }
        return true
    }

    static func readAll(_ fd: Int32, limit: Int) -> String {
        var data = [UInt8]()
        var buf = [UInt8](repeating: 0, count: 1 << 16)
        while data.count < limit {
            let n = buf.withUnsafeMutableBytes { Darwin.read(fd, $0.baseAddress, $0.count) }
            if n <= 0 { break }
            data.append(contentsOf: buf[0..<n])
        }
        return String(decoding: data, as: UTF8.self)
    }

    @discardableResult
    static func send(_ json: String) -> Bool {
        guard let fd = connect() else { return false }
        defer { close(fd) }
        return writeAll(fd, json)
    }

    static func ask(_ json: String) -> String? {
        guard let fd = connect(timeout: 5) else { return nil }
        defer { close(fd) }
        guard writeAll(fd, json) else { return nil }
        shutdown(fd, SHUT_WR)
        return readAll(fd, limit: 1 << 20)
    }

    static var isRunning: Bool {
        guard let fd = connect(timeout: 1) else { return false }
        close(fd)
        return true
    }
}

// The running app's end. Each connection is read to its end on a background queue, handled on
// the main thread, and answered with whatever the handler returns (usually nothing).
final class IpcServer {
    private let fd: Int32
    private let handler: (String) -> String?

    init?(handler: @escaping (String) -> String?) {
        self.handler = handler
        try? FileManager.default.createDirectory(atPath: (AppPaths.socket as NSString).deletingLastPathComponent,
                                                 withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        unlink(AppPaths.socket)
        fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { return nil }
        var addr = Ipc.address(AppPaths.socket)
        let bound = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) }
        }
        guard bound == 0, listen(fd, 64) == 0 else { close(fd); return nil }
        chmod(AppPaths.socket, 0o600)
        let t = Thread { [weak self] in self?.loop() }
        t.name = "hook-socket"
        t.start()
    }

    private func loop() {
        while true {
            let client = accept(fd, nil, nil)
            if client < 0 {
                if errno == EINTR { continue }
                return
            }
            var on: Int32 = 1
            setsockopt(client, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
            var tv = timeval(tv_sec: 5, tv_usec: 0)
            setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
            DispatchQueue.global(qos: .userInitiated).async { [handler] in
                let text = Ipc.readAll(client, limit: 8 << 20)
                DispatchQueue.main.async {
                    let reply = text.isEmpty ? nil : handler(text)
                    DispatchQueue.global(qos: .utility).async {
                        if let reply = reply { Ipc.writeAll(client, reply) }
                        close(client)
                    }
                }
            }
        }
    }

    func stop() {
        close(fd)
        unlink(AppPaths.socket)
    }
}
