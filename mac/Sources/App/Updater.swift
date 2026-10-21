import AppKit
import CryptoKit

// Updates come from the latest GitHub release of this repository, which carries the Windows exe
// and Aevalsistant-mac.zip side by side. GitHub reports each asset's SHA-256 as its "digest",
// and a download that does not match it is thrown away, as on Windows.
enum Updater {
    static let repo = "aevalmere/aevalsistant"
    static let asset = "Aevalsistant-mac.zip"
    static var folder: String { AppPaths.home + "/update" }
    static var apiBase = "https://api.github.com"   // tests point this at a local server

    static var currentText: String { Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0.0.0" }
    static var current: [Int] { Release.parseTag(currentText) ?? [0, 0, 0] }

    struct Result {
        var latest: [Int]?
        var downloaded: String?   // path of a verified newer Aevalsistant.app, or nil
        var problem: String?      // short reason the check failed, or nil
    }

    static func text(_ v: [Int]) -> String { v.map(String.init).joined(separator: ".") }

    // Blocking; run it off the main thread.
    static func check() -> Result {
        var r = Result()
        guard let api = URL(string: apiBase + "/repos/" + repo + "/releases/latest") else { r.problem = "Bad update address"; return r }
        var req = URLRequest(url: api, timeoutInterval: 30)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.setValue("Aevalsistant/" + currentText, forHTTPHeaderField: "User-Agent")
        let (body, status) = fetch(req)
        guard let data = body else { r.problem = "Offline or GitHub unreachable"; return r }
        if status == 404 { r.problem = "No releases published yet"; return r }
        guard status == 200, let release = Json.tryParse(String(decoding: data, as: UTF8.self)) as? JObj else { r.problem = "GitHub sent an unreadable answer"; return r }
        guard let latest = Release.parseTag(release.str("tag_name")) else { r.problem = "The latest release has no version tag"; return r }
        r.latest = latest
        if !Release.newer(latest, than: current) { return r }

        guard let found = Release.findAsset(release, asset), let url = URL(string: found.url) else { r.problem = "Release \(text(latest)) has no Mac download"; return r }
        guard let expected = found.sha256 else { r.problem = "Release \(text(latest)) has no checksum"; return r }

        let fm = FileManager.default
        try? fm.removeItem(atPath: folder)
        do { try fm.createDirectory(atPath: folder, withIntermediateDirectories: true) } catch { r.problem = "Could not save the update"; return r }
        var get = URLRequest(url: url, timeoutInterval: 300)
        get.setValue("Aevalsistant/" + currentText, forHTTPHeaderField: "User-Agent")
        let (zip, zipStatus) = fetch(get)
        guard let zipData = zip, zipStatus == 200 else { r.problem = "Offline or GitHub unreachable"; return r }
        let hash = SHA256.hash(data: zipData).map { String(format: "%02x", $0) }.joined()
        if hash != expected { r.problem = "Download did not match its checksum"; return r }

        let zipPath = folder + "/" + asset
        do { try zipData.write(to: URL(fileURLWithPath: zipPath)) } catch { r.problem = "Could not save the update"; return r }
        let out = folder + "/unpacked"
        guard Lid.run("/usr/bin/ditto", ["-x", "-k", zipPath, out]).status == 0 else { r.problem = "Could not unpack the update"; return r }
        let app = out + "/Aevalsistant.app"

        // The bundle must be this app, at the version the release claims, with a signature
        // that still matches its files.
        let info = NSDictionary(contentsOfFile: app + "/Contents/Info.plist")
        guard info?["CFBundleIdentifier"] as? String == AppPaths.bundleId,
              let v = Release.parseTag(info?["CFBundleShortVersionString"] as? String), !Release.newer(latest, than: v) else {
            r.problem = "Downloaded file is not the expected build"
            return r
        }
        guard Lid.run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app]).status == 0 else { r.problem = "The update's signature does not match its files"; return r }
        r.downloaded = app
        return r
    }

    private static func fetch(_ req: URLRequest) -> (Data?, Int) {
        let done = DispatchSemaphore(value: 0)
        var result: (Data?, Int) = (nil, 0)
        URLSession.shared.dataTask(with: req) { data, response, _ in
            result = (data, (response as? HTTPURLResponse)?.statusCode ?? 0)
            done.signal()
        }.resume()
        done.wait()
        return result
    }

    // Starts the downloaded app. It runs the usual hand-off: asks this copy to quit, copies
    // itself over the installed app, and starts that. Returns nil, or why it could not start.
    static func apply(_ app: String) -> String? {
        let r = Lid.run("/usr/bin/open", ["-n", app])
        return r.status == 0 ? nil : "macOS would not open it"
    }

    // Left over from the previous update once the installed copy is running.
    static func cleanUp() { try? FileManager.default.removeItem(atPath: folder) }
}
