import AppKit

// Values from docs/DESIGN.md, sampled from the profile image. The Mac app uses the system font
// (SF Pro) where the Windows app uses Segoe UI, at the same sizes in points.
enum Theme {
    static let paper = hex(0xFFF6F1)
    static let paperDeep = hex(0xFBEBE3)
    static let paperEdge = hex(0xF0DDD3)
    static let ink = hex(0x3E3438)
    static let slate = hex(0x586C7D)
    static let slateSoft = hex(0x9DB0B9)
    static let slateMist = hex(0xC9D5DA)
    static let blush = hex(0xE9A9AE)
    static let clip = hex(0x8E4A52)
    static let shadow = hex(0x5A3A3A)

    static func hex(_ v: UInt32, _ alpha: CGFloat = 1) -> NSColor {
        NSColor(srgbRed: CGFloat((v >> 16) & 0xFF) / 255, green: CGFloat((v >> 8) & 0xFF) / 255, blue: CGFloat(v & 0xFF) / 255, alpha: alpha)
    }

    static func mix(_ a: NSColor, _ b: NSColor, _ t: CGFloat) -> NSColor {
        let t = max(0, min(1, t))
        guard let x = a.usingColorSpace(.sRGB), let y = b.usingColorSpace(.sRGB) else { return t < 0.5 ? a : b }
        return NSColor(srgbRed: x.redComponent + (y.redComponent - x.redComponent) * t,
                       green: x.greenComponent + (y.greenComponent - x.greenComponent) * t,
                       blue: x.blueComponent + (y.blueComponent - x.blueComponent) * t,
                       alpha: x.alphaComponent + (y.alphaComponent - x.alphaComponent) * t)
    }

    static func font(_ size: CGFloat) -> NSFont { .systemFont(ofSize: size) }
    static func semibold(_ size: CGFloat) -> NSFont { .systemFont(ofSize: size, weight: .semibold) }

    // Tests run the renderer outside the app bundle and point this at the repository's assets.
    static var assetFolder: String?

    static func resource(_ name: String, _ ext: String) -> URL? {
        if let dir = assetFolder {
            let url = URL(fileURLWithPath: dir).appendingPathComponent(name + "." + ext)
            return FileManager.default.fileExists(atPath: url.path) ? url : nil
        }
        return Bundle.main.url(forResource: name, withExtension: ext)
    }

    static func image(_ name: String) -> NSImage? { resource(name, "png").flatMap { NSImage(contentsOf: $0) } }

    // The menu bar glyph: the sleepy face from the profile picture as a template image, so it
    // takes the menu bar's own color in light and dark mode. Eyes closed when idle; open, with
    // cheeks, while agents work. Drawn on the same 32-unit grid as the Windows tray glyph.
    static func menuGlyph(awake: Bool, size: CGFloat = 18) -> NSImage {
        let img = NSImage(size: NSSize(width: size, height: size), flipped: true) { _ in
            let s = size / 32
            func p(_ x: CGFloat, _ y: CGFloat) -> NSPoint { NSPoint(x: x * s, y: y * s) }
            let line = max(1.1, 1.7 * s)

            let face = NSBezierPath(ovalIn: NSRect(x: 3 * s, y: 5 * s, width: 26 * s, height: 24 * s))
            face.lineWidth = line
            NSColor.black.setStroke()
            face.stroke()

            let hair = NSBezierPath()
            hair.move(to: p(2.2, 20))
            hair.curve(to: p(16, 3), controlPoint1: p(1, 9), controlPoint2: p(9, 2.5))
            hair.curve(to: p(29.8, 20), controlPoint1: p(24, 2.5), controlPoint2: p(31, 9))
            for (x, y) in [(26.0, 14.0), (22, 17), (19, 12.5), (15, 16.5), (11, 12.5), (7.5, 16.5)] { hair.line(to: p(CGFloat(x), CGFloat(y))) }
            hair.close()
            hair.lineJoinStyle = .round
            hair.lineWidth = line
            NSColor.black.setFill()
            hair.fill()
            hair.stroke()

            // The hair clip, cut out of the hair so it still reads in one color.
            NSGraphicsContext.current?.compositingOperation = .clear
            NSBezierPath(ovalIn: NSRect(x: 16.5 * s, y: 5.5 * s, width: 6 * s, height: 3.2 * s)).fill()
            NSGraphicsContext.current?.compositingOperation = .sourceOver

            if awake {
                NSBezierPath(ovalIn: NSRect(x: 10.2 * s, y: 19.2 * s, width: 3.4 * s, height: 3.8 * s)).fill()
                NSBezierPath(ovalIn: NSRect(x: 18.4 * s, y: 19.2 * s, width: 3.4 * s, height: 3.8 * s)).fill()
                NSColor.black.withAlphaComponent(0.45).setFill()
                NSBezierPath(ovalIn: NSRect(x: 6 * s, y: 23 * s, width: 5 * s, height: 3 * s)).fill()
                NSBezierPath(ovalIn: NSRect(x: 21 * s, y: 23 * s, width: 5 * s, height: 3 * s)).fill()
            } else {
                // Closed eyes: the lower arc of each eye, like the Windows glyph.
                for cx in [12.0, 20.0] {
                    let eye = NSBezierPath()
                    eye.move(to: p(CGFloat(cx) - 2.35, 20.7))
                    eye.curve(to: p(CGFloat(cx) + 2.35, 20.7), controlPoint1: p(CGFloat(cx) - 1.5, 22.9), controlPoint2: p(CGFloat(cx) + 1.5, 22.9))
                    eye.lineWidth = line
                    eye.lineCapStyle = .round
                    eye.stroke()
                }
            }
            return true
        }
        img.isTemplate = true
        img.accessibilityDescription = awake ? "Aevalsistant: agents working" : "Aevalsistant: idle"
        return img
    }

    // A state dot for menu rows: filled for done and needs you, a ring for working.
    static func dot(_ size: CGFloat, _ color: NSColor, hollow: Bool) -> NSImage {
        NSImage(size: NSSize(width: size, height: size), flipped: false) { _ in
            let d = size * 0.5, o = (size - d) / 2
            let path = NSBezierPath(ovalIn: NSRect(x: o, y: o, width: d, height: d))
            if hollow {
                color.setStroke()
                path.lineWidth = max(1, size / 10)
                path.stroke()
            } else {
                color.setFill()
                path.fill()
            }
            return true
        }
    }

    static func round(_ r: NSRect, _ radius: CGFloat) -> NSBezierPath {
        NSBezierPath(roundedRect: r, xRadius: radius, yRadius: radius)
    }
}
