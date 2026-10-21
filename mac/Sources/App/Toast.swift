import AppKit
import Carbon.HIToolbox

// One of the other sessions, listed under the main notification.
struct ToastRow {
    var sessionId: String?
    var appPid: Int32 = 0
    var name = ""
    var host = ""
    var status = ""   // "working 4m", "done", "needs you"
    var state = RowState.done
    // 0: a session. 1: started by the nearest depth-0 row above it (a subagent, or a
    // background session), or by the main notification's session when none comes first.
    var depth = 0
}

final class ToastContent {
    var title = ""
    var detail = ""
    var kind = ToastKind.done
    var host = ""          // which app it is in: "VS Code", "Terminal", "Claude"...
    var keycap = false     // show the Option-Tab hint (only when there is a window to jump to)
    var rows: [ToastRow] = []
    var overflow = ""      // "2 more in the menu" when the list is cut short

    // The detail broken into lines. Measuring is not free, and every frame of the card
    // opening asks for it again.
    fileprivate var wrap: ToastArt.Wrapped?
}

// Draws the card, in points with the origin at the top left. No window code here, so tests can
// render it to a PNG. The layout matches src/Toast.cs, so both apps' cards look alike.
enum ToastArt {
    static let W: CGFloat = 420, headH: CGFloat = 64, rowH: CGFloat = 28, childH: CGFloat = 24
    static let listTop: CGFloat = 6, listBottom: CGFloat = 8, radius: CGFloat = 16, pad: CGFloat = 22, topGap: CGFloat = 16
    static let lineH: CGFloat = 16   // pitch of the detail lines once the card opens
    static let maxRows = 6           // rows drawn under the main notification, children included
    static let maxLines = 8

    // Hover targets: the main notification, one of the listed rows, the card between targets
    // (list padding, the overflow line), or nothing.
    static let hitNone = -2, hitHead = -1, hitCard = -3

    static let titleFont = Theme.semibold(13.5), hostFont = Theme.font(12.5), detailFont = Theme.font(12)
    static let nameFont = Theme.font(12.5), childFont = Theme.font(12), smallFont = Theme.font(11.5), keyFont = Theme.semibold(10.5)

    private static func rowCount(_ c: ToastContent) -> Int { c.rows.count + (c.overflow.isEmpty ? 0 : 1) }
    private static func rowHeight(_ c: ToastContent, _ i: Int) -> CGFloat { i < c.rows.count && c.rows[i].depth > 0 ? childH : rowH }

    private static func listHeight(_ c: ToastContent) -> CGFloat {
        let n = rowCount(c)
        if n == 0 { return 0 }
        return (0..<n).reduce(listTop + listBottom) { $0 + rowHeight(c, $1) }
    }

    // How much taller the main row gets to show the whole detail: 0 when it fits on one line.
    static func extraHeight(_ c: ToastContent) -> CGFloat { CGFloat(wrap(c).lines.count - 1) * lineH }

    private static func grow(_ c: ToastContent, _ expand: CGFloat) -> CGFloat { (extraHeight(c) * max(0, min(1, expand))).rounded() }

    static func height(_ c: ToastContent, _ expand: CGFloat) -> CGFloat { headH + grow(c, expand) + listHeight(c) }

    static var surfaceWidth: CGFloat { W + pad * 2 }

    // Always the open card's height, so a window keeps one surface while it opens and closes.
    static func surfaceHeight(_ c: ToastContent) -> CGFloat { height(c, 1) + pad * 2 }

    // Which part of the card a point (surface coordinates, top left origin) is over.
    static func hitTest(_ c: ToastContent, _ x: CGFloat, _ y: CGFloat, _ expand: CGFloat) -> Int {
        let card = NSRect(x: pad, y: pad, width: W, height: height(c, expand))
        guard x >= card.minX, x < card.maxX, y >= card.minY, y < card.maxY else { return hitNone }
        var ly = y - card.minY - grow(c, expand)   // as if the card were closed
        if ly < headH { return hitHead }
        ly -= headH + listTop
        for i in 0..<c.rows.count {
            let h = rowHeight(c, i)
            if ly >= 0 && ly < h { return i }
            ly -= h
        }
        return hitCard
    }

    // The whole surface as an image at the given pixel scale.
    static func image(_ c: ToastContent, scale: CGFloat, hover: Int, avatar: NSImage?, expand: CGFloat) -> CGImage? {
        let w = Int((surfaceWidth * scale).rounded(.up)), h = Int((surfaceHeight(c) * scale).rounded(.up))
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue) else { return nil }
        ctx.translateBy(x: 0, y: CGFloat(h))
        ctx.scaleBy(x: scale, y: -scale)
        let gc = NSGraphicsContext(cgContext: ctx, flipped: true)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = gc
        draw(c, ctx: ctx, scale: scale, hover: hover, avatar: avatar, expand: expand)
        NSGraphicsContext.restoreGraphicsState()
        return ctx.makeImage()
    }

    private static func draw(_ c: ToastContent, ctx: CGContext, scale: CGFloat, hover: Int, avatar: NSImage?, expand: CGFloat) {
        let w = wrap(c)
        let g = grow(c, expand), closedH = headH + listHeight(c)
        let card = NSRect(x: pad, y: pad, width: W, height: closedH + g)
        // The avatar, title, and keycaps keep to the closed card's top band; the detail and
        // everything under it move down as the card opens.
        let band = NSRect(x: card.minX, y: card.minY, width: card.width, height: headH)
        let path = Theme.round(card, radius)
        let hair = 1 / scale

        // Two soft shadows, the same pair the Windows card blurs by hand. Shadow offset and blur
        // are in the bitmap's own pixels, where up is positive, so they are scaled by hand.
        ctx.saveGState()
        ctx.setShadow(offset: CGSize(width: 0, height: -6 * scale), blur: 14 * scale, color: Theme.shadow.withAlphaComponent(0.20).cgColor)
        Theme.paper.setFill(); path.fill()
        ctx.restoreGState()
        ctx.saveGState()
        ctx.setShadow(offset: CGSize(width: 0, height: -1 * scale), blur: 3 * scale, color: Theme.shadow.withAlphaComponent(0.10).cgColor)
        Theme.paper.setFill(); path.fill()
        ctx.restoreGState()
        Theme.paper.setFill(); path.fill()
        (hover != hitNone ? Theme.slateMist : Theme.paperEdge.withAlphaComponent(0.8)).setStroke()
        let edge = Theme.round(card.insetBy(dx: hair / 2, dy: hair / 2), radius)
        edge.lineWidth = hair
        edge.stroke()

        // avatar
        let av: CGFloat = 40
        let avRect = NSRect(x: band.minX + 12, y: band.minY + (band.height - av) / 2, width: av, height: av)
        if let avatar = avatar {
            NSGraphicsContext.saveGraphicsState()
            NSBezierPath(ovalIn: avRect).addClip()
            avatar.draw(in: avRect, from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: [.interpolation: NSImageInterpolation.high.rawValue])
            NSGraphicsContext.restoreGraphicsState()
        }
        let ring = NSBezierPath(ovalIn: avRect)
        ring.lineWidth = 1.2
        Theme.paperEdge.setStroke()
        ring.stroke()

        if c.kind != .info {
            let d: CGFloat = 10
            let dot = NSRect(x: avRect.maxX - d + 2.5, y: avRect.maxY - d + 2.5, width: d, height: d)
            Theme.paper.setFill()
            NSBezierPath(ovalIn: dot.insetBy(dx: -2, dy: -2)).fill()
            (c.kind == .needsYou ? Theme.blush : Theme.slateSoft).setFill()
            NSBezierPath(ovalIn: dot).fill()
        }

        // keycaps, laid out from the right edge inward
        var right = band.maxX - 14
        if c.keycap {
            right = key("Tab", right, band) - 4
            right = key("⌥", right, band) - 8
        }

        let x = card.minX + textLeft
        let oneLine = c.detail.isEmpty
        let ty = oneLine ? band.minY : band.minY + 12, th = oneLine ? band.height : 20
        nameAndHost(c.title, c.host, titleFont, hostFont, Theme.ink, Theme.slate, x, ty, right - x - 4, th)
        if !oneLine { drawDetail(w, x, band.minY + 32, g, band.maxY + g - 14) }
        if rowCount(c) > 0 { drawRows(c, card, band.maxY + g, avRect, x, hover, hair) }
    }

    // From the card's left edge: past the avatar and its gap.
    private static let textLeft: CGFloat = 12 + 40 + 14

    // One line of text, vertically centered in a box, cut with an ellipsis when it runs long.
    private static func line(_ s: String, _ f: NSFont, _ color: NSColor, _ r: NSRect, align: NSTextAlignment = .left) {
        let p = NSMutableParagraphStyle()
        p.lineBreakMode = .byTruncatingTail
        p.alignment = align
        let attrs: [NSAttributedString.Key: Any] = [.font: f, .foregroundColor: color, .paragraphStyle: p]
        let h = ceil(f.ascender - f.descender + f.leading)
        (s as NSString).draw(with: NSRect(x: r.minX, y: r.midY - h / 2, width: max(0, r.width), height: h),
                             options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine], attributes: attrs)
    }

    static func measure(_ s: String, _ f: NSFont) -> CGFloat { ceil((s as NSString).size(withAttributes: [.font: f]).width) }

    // The detail: one line while the card is closed, the whole text as it opens. Lines past
    // the first are uncovered by the card's lower edge and fade in as they clear it.
    private static func drawDetail(_ w: Wrapped, _ x: CGFloat, _ top: CGFloat, _ grow: CGFloat, _ bottom: CGFloat) {
        let boxH: CGFloat = 18, room = w.width + 8
        if w.lines.count == 1 {
            line(w.lines[0], detailFont, Theme.slate, NSRect(x: x, y: top, width: room, height: boxH))
            return
        }
        // The first line keeps its words in place and trades the ellipsis for the rest of the
        // line, so nothing jumps when the card starts to open.
        let k = min(1, grow / lineH)
        line(w.prefix, detailFont, Theme.slate, NSRect(x: x, y: top, width: room, height: boxH))
        let tail = NSRect(x: x + w.prefixW, y: top, width: room, height: boxH)
        if k < 1 { line("…", detailFont, Theme.slate.withAlphaComponent(1 - k), tail) }
        if k > 0 { line(String(w.lines[0].dropFirst(w.prefix.count)), detailFont, Theme.slate.withAlphaComponent(k), tail) }
        if grow <= 0 { return }

        NSGraphicsContext.saveGraphicsState()
        NSBezierPath(rect: NSRect(x: x, y: top, width: room, height: bottom - top)).addClip()
        for i in 1..<w.lines.count {
            let a = min(1, (grow - CGFloat(i - 1) * lineH) / lineH)
            if a <= 0 { break }
            line(w.lines[i], detailFont, Theme.slate.withAlphaComponent(a), NSRect(x: x, y: top + CGFloat(i) * lineH, width: room, height: boxH))
        }
        NSGraphicsContext.restoreGraphicsState()
    }

    final class Wrapped {
        var text = ""
        var keycap = false
        var width: CGFloat = 0       // the detail line, from the title's left edge to the keycaps
        var lines: [String] = []     // at most maxLines; the last ends in an ellipsis when the text runs on
        var prefix = ""              // the closed card shows prefix and an ellipsis when there are more lines
        var prefixW: CGFloat = 0
    }

    private static func keyWidth(_ text: String) -> CGFloat { max(measure(text, keyFont) + 12, 24) }

    fileprivate static func wrap(_ c: ToastContent) -> Wrapped {
        if let w = c.wrap, w.text == c.detail, w.keycap == c.keycap { return w }
        let w = Wrapped()
        w.text = c.detail
        w.keycap = c.keycap
        // the same right edge draw() reaches by laying out the keycaps
        var right = W - 14
        if c.keycap { right -= keyWidth("Tab") + 4 + keyWidth("⌥") + 8 }
        w.width = right - textLeft - 4

        var words = c.detail.split(whereSeparator: { $0.isWhitespace }).map(String.init)
        var lines: [String] = []
        var current = ""
        var i = 0
        while i < words.count {
            if lines.count == maxLines - 1 {
                // The last line gets whatever is left, cut with an ellipsis if it does not fit.
                let rest = ([current] + words[i...]).filter { !$0.isEmpty }.joined(separator: " ")
                lines.append(measure(rest, detailFont) <= w.width ? rest : clip(rest, w.width) + "…")
                current = ""
                break
            }
            let word = words[i]
            let next = current.isEmpty ? word : current + " " + word
            if measure(next, detailFont) <= w.width {
                current = next
                i += 1
            } else if current.isEmpty {
                // A single word wider than the line breaks where it has to.
                var cut = max(1, word.count - 1)
                while cut > 1 && measure(String(word.prefix(cut)), detailFont) > w.width { cut -= 1 }
                lines.append(String(word.prefix(cut)))
                words[i] = String(word.dropFirst(cut))
            } else {
                lines.append(current)
                current = ""
            }
        }
        if !current.isEmpty { lines.append(current) }
        if lines.isEmpty { lines = [""] }
        w.lines = lines
        w.prefix = lines.count > 1 ? clip(lines[0], w.width) : lines[0]
        w.prefixW = measure(w.prefix, detailFont)
        c.wrap = w
        return w
    }

    // Drops whole words until the line and an ellipsis fit; a single long word loses letters
    // instead. A sentence's last stop goes too, or it reads as "intact.…".
    private static func clip(_ text: String, _ width: CGFloat) -> String {
        func trimEnd(_ s: inout String) { while let c = s.last, " .,;:".contains(c) { s.removeLast() } }
        var t = text
        trimEnd(&t)
        while !t.isEmpty && measure(t + "…", detailFont) > width {
            if let cut = t.lastIndex(of: " "), cut > t.startIndex { t = String(t[..<cut]) } else { t.removeLast() }
            trimEnd(&t)
        }
        return t
    }

    // The other sessions: a hairline under the main notification, then one row each, with
    // the dot under the avatar and the text on the title's left edge. A child sits under its
    // session on a shorter row with a smaller dot, hung from the session's dot by an elbow.
    private static func drawRows(_ c: ToastContent, _ card: NSRect, _ top: CGFloat, _ avRect: NSRect, _ x: CGFloat, _ hover: Int, _ hair: CGFloat) {
        Theme.paperEdge.setFill()
        NSBezierPath(rect: NSRect(x: card.minX + 14, y: top, width: card.width - 28, height: hair)).fill()

        let n = rowCount(c)
        var ys = [top + listTop]
        for i in 0..<n { ys.append(ys[i] + rowHeight(c, i)) }

        let dotX = avRect.midX, childDotX = x + 3, childX = x + 16
        if hover >= 0 && hover < c.rows.count {
            Theme.paperDeep.setFill()
            Theme.round(NSRect(x: card.minX + 6, y: ys[hover], width: card.width - 12, height: ys[hover + 1] - ys[hover]), 8).fill()
        }
        connectors(c, ys, dotX, childDotX - 6, avRect.maxY + 4, hair)

        for i in 0..<n {
            let y = ys[i], h = ys[i + 1] - ys[i]
            let row = NSRect(x: card.minX + 6, y: y, width: card.width - 12, height: h)
            if i >= c.rows.count {
                line(c.overflow, smallFont, Theme.slate, NSRect(x: x, y: y, width: row.maxX - x - 10, height: h))
                break
            }
            let r = c.rows[i]
            let child = r.depth > 0
            let d: CGFloat = child ? 6 : 8, cx = child ? childDotX : dotX, tx = child ? childX : x
            let dot = NSRect(x: cx - d / 2, y: y + (h - d) / 2, width: d, height: d)
            if r.state == .working {
                let ring = NSBezierPath(ovalIn: dot.insetBy(dx: 0.7, dy: 0.7))
                ring.lineWidth = child ? 1.2 : 1.4
                Theme.slate.setStroke()
                ring.stroke()
            } else {
                (r.state == .needsYou ? Theme.blush : Theme.slateSoft).setFill()
                NSBezierPath(ovalIn: dot).fill()
            }
            let statusW = measure(r.status, smallFont) + 2
            let statusRight = row.maxX - 10
            line(r.status, smallFont, r.state == .needsYou ? Theme.ink : Theme.slate, NSRect(x: statusRight - statusW, y: y, width: statusW, height: h), align: .right)
            nameAndHost(r.name, r.host, child ? childFont : nameFont, smallFont, Theme.ink, Theme.slate, tx, y, statusRight - statusW - 12 - tx, h)
        }
    }

    // One vertical run per family from the session's dot (or from under the avatar, for
    // children of the main notification's session), with an arm out to each child's dot.
    private static func connectors(_ c: ToastContent, _ ys: [CGFloat], _ dotX: CGFloat, _ armEnd: CGFloat, _ headFrom: CGFloat, _ hair: CGFloat) {
        let path = NSBezierPath()
        let r: CGFloat = 4
        var from = headFrom
        for i in 0..<c.rows.count {
            let cy = (ys[i] + ys[i + 1]) / 2
            if c.rows[i].depth == 0 { from = cy + 7; continue }   // clear of the session's dot
            let last = i + 1 == c.rows.count || c.rows[i + 1].depth == 0
            path.move(to: NSPoint(x: dotX, y: from))
            if last {
                path.line(to: NSPoint(x: dotX, y: cy - r))
                path.curve(to: NSPoint(x: dotX + r, y: cy), controlPoint1: NSPoint(x: dotX, y: cy - r * 0.45), controlPoint2: NSPoint(x: dotX + r * 0.45, y: cy))
                path.line(to: NSPoint(x: armEnd, y: cy))
            } else {
                path.line(to: NSPoint(x: dotX, y: cy))
                path.move(to: NSPoint(x: dotX, y: cy))
                path.line(to: NSPoint(x: armEnd, y: cy))
            }
            from = cy
        }
        if path.isEmpty { return }
        path.lineWidth = max(hair, 1)
        Theme.slateMist.setStroke()
        path.stroke()
    }

    // "aevalrena finished  ·  VS Code". The name wins: the app shrinks first, to a stub.
    private static func nameAndHost(_ text: String, _ hostName: String, _ main: NSFont, _ sub: NSFont, _ mainColor: NSColor, _ subColor: NSColor,
                                    _ x: CGFloat, _ y: CGFloat, _ width: CGFloat, _ height: CGFloat) {
        let hostText = hostName.isEmpty ? "" : "·  " + hostName
        let gap: CGFloat = 5
        let textFull = measure(text, main) + 2
        let hostFull = hostText.isEmpty ? 0 : measure(hostText, sub) + 2
        var hostW: CGFloat = 0, textW = min(textFull, width)
        if hostFull > 0 {
            let room = width - gap - textFull
            hostW = room >= hostFull ? hostFull : max(min(hostFull, 64), room)
            textW = min(textFull, width - gap - hostW)
        }
        line(text, main, mainColor, NSRect(x: x, y: y, width: textW, height: height))
        if hostW > 0 { line(hostText, sub, subColor, NSRect(x: x + textW + gap, y: y + 0.5, width: hostW, height: height)) }
    }

    // A small keycap with a slightly heavier lower edge. Returns its left edge.
    private static func key(_ text: String, _ right: CGFloat, _ band: NSRect) -> CGFloat {
        let w = keyWidth(text), h: CGFloat = 20
        let r = NSRect(x: right - w, y: band.minY + (band.height - h) / 2, width: w, height: h)
        Theme.paperEdge.setFill()
        Theme.round(r.offsetBy(dx: 0, dy: 1.5), 5).fill()
        Theme.paperDeep.setFill()
        Theme.round(r, 5).fill()
        line(text, keyFont, Theme.slate, r.offsetBy(dx: 0, dy: 0.5), align: .center)
        return r.minX
    }
}

final class ToastPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

// The window: borderless, never activated, above other windows on every Space, and left out of
// screenshots and screen sharing where macOS still honors that.
final class ToastWindow {
    private enum Phase { case hidden, entering, shown, leaving }

    private let enterMs = 360.0, exitMs = 220.0, fadeEnterMs = 180.0, fadeExitMs = 160.0, openMs = 240.0, closeMs = 200.0
    private let userAwayAfter: TimeInterval = 20

    var clicked: ((Int) -> Void)?   // ToastArt.hitHead, or the index of a listed row, children included
    var optionTabbed: (() -> Void)?
    var expired: (() -> Void)?
    var dismissed: (() -> Void)?
    var expandOnHover = true

    private let panel: ToastPanel
    private let view = ToastView()
    private let surface = CALayer()
    private let avatar = Theme.image("avatar")
    private let hotKey = HotKey()

    private var content: ToastContent?
    private var phase = Phase.hidden
    private var hover = ToastArt.hitNone
    private var reduced = false
    private var scale: CGFloat = 2
    private var winH: CGFloat = 0
    private var t0 = 0.0, fromY: CGFloat = 0, fromA: CGFloat = 0, toY: CGFloat = 0, toA: CGFloat = 0, duration = 0.0
    private var curY: CGFloat = 0, curA: CGFloat = 0
    private var expand: CGFloat = 0, expandFrom: CGFloat = 0, expandTo: CGFloat = 0, expandT0 = 0.0, expandMs = 0.0
    private var animating = false, resizing = false
    private var frameTimer: Timer?
    private var dwell: Timer?
    private var openDelay: Timer?
    private var dwellLeft = 0.0

    var showing: Bool { phase == .entering || phase == .shown }
    var title: String { content?.title ?? "" }

    init() {
        panel = ToastPanel(contentRect: NSRect(x: 0, y: 0, width: 10, height: 10), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = .statusBar
        panel.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
        panel.isFloatingPanel = true
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = true
        panel.isReleasedWhenClosed = false
        panel.animationBehavior = .none
        // Screen sharing and screenshots see nothing here. macOS 15 and later can still capture
        // it through ScreenCaptureKit; there is no public way to opt out of that.
        panel.sharingType = .none
        view.layer = CALayer()
        view.wantsLayer = true
        view.layer?.addSublayer(surface)
        surface.contentsGravity = .topLeft
        panel.contentView = view

        view.moved = { [weak self] p in self?.setHover(self?.hit(p) ?? ToastArt.hitNone) }
        view.left = { [weak self] in self?.setHover(ToastArt.hitNone) }
        view.released = { [weak self] p in
            guard let self = self else { return }
            let target = self.hit(p)
            if self.showing && (target == ToastArt.hitHead || target >= 0) { self.clicked?(target) }
        }
        view.rightReleased = { [weak self] in if self?.showing == true { self?.dismissed?() } }
        view.wantsHand = { [weak self] p in (self?.hit(p) ?? ToastArt.hitNone) != ToastArt.hitNone }
        hotKey.pressed = { [weak self] in self?.optionTabbed?() }
    }

    // seconds < 0 updates what is shown (a session changed state) without restarting the countdown.
    func show(_ c: ToastContent, seconds: Double) {
        let wasHidden = phase == .hidden
        content = c
        if seconds >= 0 || wasHidden { dwellLeft = max(seconds, 5) }
        reduced = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        if wasHidden { place() } else { reflow() }
        // An open card stays open across a list refresh while the new detail still needs the room.
        openTo(hover != ToastArt.hitNone ? 1 : 0)
        rebuild()

        if c.keycap { hotKey.arm() } else { hotKey.disarm() }

        if phase == .shown || phase == .entering { present(curY, curA); return }

        // From just above the top of the screen: the surface is taller than the closed card,
        // and starting from its full height would make the drop faster.
        let startY = reduced ? finalY : -(ToastArt.height(c, expand) + ToastArt.pad * 2).rounded(.up)
        animate(.entering, phase == .leaving ? curY : startY, phase == .leaving ? curA : 0, finalY, 1, reduced ? fadeEnterMs : enterMs)
        panel.orderFrontRegardless()
    }

    func hide() {
        if phase == .hidden || phase == .leaving { return }
        hotKey.disarm()
        dwell?.invalidate(); dwell = nil
        openDelay?.invalidate(); openDelay = nil
        animate(.leaving, curY, curA, reduced ? curY : finalY - 10, 0, reduced ? fadeExitMs : exitMs)
    }

    // The card's top edge sits topGap under the menu bar; the surface starts pad above it.
    private var finalY: CGFloat { ToastArt.topGap - ToastArt.pad }

    private func place() {
        let mouse = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) } ?? NSScreen.main ?? NSScreen.screens.first
        scale = screen?.backingScaleFactor ?? 2
        let area = screen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        reflow(area)
    }

    // The window hangs from the top of the visible area (under the menu bar) and is sized for
    // the open card; the card slides inside it.
    private func reflow(_ area: NSRect? = nil) {
        guard let c = content else { return }
        let top = area?.maxY ?? panel.frame.maxY
        let midX = area?.midX ?? panel.frame.midX
        let w = ToastArt.surfaceWidth
        winH = finalY + ToastArt.surfaceHeight(c)
        panel.setFrame(NSRect(x: (midX - w / 2).rounded(), y: top - winH, width: w, height: winH), display: false)
        view.frame = NSRect(x: 0, y: 0, width: w, height: winH)
    }

    private func rebuild() {
        guard let c = content else { return }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        surface.contents = ToastArt.image(c, scale: scale, hover: hover, avatar: avatar, expand: expand)
        surface.contentsScale = scale
        surface.bounds = NSRect(x: 0, y: 0, width: ToastArt.surfaceWidth, height: ToastArt.surfaceHeight(c))
        CATransaction.commit()
    }

    // One frame: the pre-rendered card moved to y (from the window's top) at opacity a.
    private func present(_ y: CGFloat, _ a: CGFloat) {
        curY = y
        curA = a
        guard let c = content else { return }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        let h = ToastArt.surfaceHeight(c)
        surface.anchorPoint = .zero
        surface.position = NSPoint(x: 0, y: (winH - y - h).rounded())
        CATransaction.commit()
        panel.alphaValue = max(0, min(1, a))
    }

    private var now: Double { CACurrentMediaTime() * 1000 }

    private func animate(_ next: Phase, _ y0: CGFloat, _ a0: CGFloat, _ y1: CGFloat, _ a1: CGFloat, _ ms: Double) {
        phase = next
        fromY = y0; fromA = a0; toY = y1; toA = a1; duration = ms
        t0 = now
        present(y0, a0)
        animating = true
        runFrames()
    }

    private func runFrames() {
        if frameTimer != nil { return }
        let t = Timer(timeInterval: 1.0 / 120, repeats: true) { [weak self] _ in self?.step() }
        RunLoop.main.add(t, forMode: .common)
        frameTimer = t
    }

    // Opens (1) or closes (0) the card. A detail that fits on one line never opens.
    private func openTo(_ to: CGFloat) {
        guard expandOnHover, let c = content, ToastArt.extraHeight(c) > 0 else {
            expand = 0; expandTo = 0; resizing = false
            return
        }
        if to == expandTo { return }
        expandFrom = expand
        expandTo = to
        if reduced { expand = to; resizing = false; return }
        // turning back part way covers only the distance already travelled
        expandMs = max(1, (to > expand ? openMs : closeMs) * Double(abs(to - expand)))
        expandT0 = now
        resizing = true
        runFrames()
    }

    private func step() {
        if resizing {
            let p = min(1, (now - expandT0) / expandMs)
            expand = expandFrom + (expandTo - expandFrom) * CGFloat(expandTo > expandFrom ? Ease.enter(p) : Ease.exit(p))
            if p >= 1 { resizing = false }
            // The rows slide under a pointer that is standing still, so the row under it
            // changes without a mouse move.
            if hover != ToastArt.hitNone {
                let under = hit(view.convert(panel.mouseLocationOutsideOfEventStream, from: nil))
                if under != ToastArt.hitNone { hover = under }
            }
            rebuild()
            if !animating { present(curY, curA) }
        }
        if animating {
            let t = min(1, (now - t0) / duration)
            if phase == .entering {
                let e = CGFloat(Ease.enter(t))
                // opacity leads position: fully opaque by 55% of the drop
                present(fromY + (toY - fromY) * e, fromA + (toA - fromA) * CGFloat(Ease.enter(min(1, t / 0.55))))
            } else {
                let e = CGFloat(Ease.exit(t))
                present(fromY + (toY - fromY) * e, fromA + (toA - fromA) * e)
            }
            if t >= 1 {
                animating = false
                if phase == .entering {
                    phase = .shown
                    startDwell()
                } else {
                    phase = .hidden
                    panel.orderOut(nil)
                    hover = ToastArt.hitNone
                    expand = 0; expandTo = 0
                    resizing = false
                }
            }
        }
        if !animating && !resizing { frameTimer?.invalidate(); frameTimer = nil }
    }

    private func startDwell() {
        dwell?.invalidate()
        let t = Timer(timeInterval: 0.1, repeats: true) { [weak self] _ in self?.tick() }
        RunLoop.main.add(t, forMode: .common)
        dwell = t
    }

    private func tick() {
        guard phase == .shown else { dwell?.invalidate(); dwell = nil; return }
        // The countdown only runs while someone is at the keyboard, so a card that drops while
        // you are away is still there when you come back.
        let idle = CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: CGEventType(rawValue: ~0)!)
        if hover != ToastArt.hitNone || idle > userAwayAfter { return }
        dwellLeft -= 0.1
        if dwellLeft <= 0 { expired?() }
    }

    // A point in the view (bottom left origin) to a hover target.
    private func hit(_ p: NSPoint) -> Int {
        guard let c = content else { return ToastArt.hitNone }
        return ToastArt.hitTest(c, p.x, winH - p.y - curY, expand)
    }

    private func setHover(_ target: Int) {
        if target == hover || content == nil { return }
        let entering = hover == ToastArt.hitNone
        hover = target
        // Anywhere on the card opens it, not only the main row, once the pointer settles.
        if target == ToastArt.hitNone {
            openDelay?.invalidate(); openDelay = nil
            openTo(0)
        } else if entering {
            openDelay?.invalidate()
            // A pointer crossing the card on its way somewhere else should not open it.
            let t = Timer(timeInterval: 0.15, repeats: false) { [weak self] _ in
                guard let self = self else { return }
                self.openDelay = nil
                if self.hover == ToastArt.hitNone { return }
                self.openTo(1)
                if self.resizing { return }   // with reduced motion it opened at once, so draw it now
                self.rebuild()
                if !self.animating { self.present(self.curY, self.curA) }
            }
            RunLoop.main.add(t, forMode: .common)
            openDelay = t
        }
        if resizing { return }   // the next frame draws the new hover as well
        rebuild()
        if !animating { present(curY, curA) }
    }
}

final class ToastView: NSView {
    var moved: ((NSPoint) -> Void)?
    var left: (() -> Void)?
    var released: ((NSPoint) -> Void)?
    var rightReleased: (() -> Void)?
    var wantsHand: ((NSPoint) -> Bool)?
    private var tracking: NSTrackingArea?

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let t = tracking { removeTrackingArea(t) }
        let t = NSTrackingArea(rect: bounds, options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect, .cursorUpdate], owner: self)
        addTrackingArea(t)
        tracking = t
    }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseMoved(with event: NSEvent) { point(event) }
    override func mouseEntered(with event: NSEvent) { point(event) }
    override func mouseExited(with event: NSEvent) { NSCursor.arrow.set(); left?() }
    override func cursorUpdate(with event: NSEvent) { point(event) }
    override func mouseDown(with event: NSEvent) {}
    override func mouseUp(with event: NSEvent) { released?(convert(event.locationInWindow, from: nil)) }
    override func rightMouseDown(with event: NSEvent) {}
    override func rightMouseUp(with event: NSEvent) { rightReleased?() }

    private func point(_ event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        (wantsHand?(p) == true ? NSCursor.pointingHand : NSCursor.arrow).set()
        moved?(p)
    }
}

// Option-Tab is taken only while a card with a target is on screen; otherwise the hot key is not
// registered at all. A Carbon hot key needs no Accessibility or Input Monitoring permission.
final class HotKey {
    var pressed: (() -> Void)?
    private var ref: EventHotKeyRef?
    private static var installed = false
    private static weak var current: HotKey?

    func arm() {
        guard ref == nil else { return }
        HotKey.installHandler()
        HotKey.current = self
        let id = EventHotKeyID(signature: OSType(0x4145_5654), id: 1)   // "AEVT"
        RegisterEventHotKey(UInt32(kVK_Tab), UInt32(optionKey), id, GetApplicationEventTarget(), 0, &ref)
    }

    func disarm() {
        guard let r = ref else { return }
        UnregisterEventHotKey(r)
        ref = nil
    }

    private static func installHandler() {
        if installed { return }
        installed = true
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
            DispatchQueue.main.async { HotKey.current?.pressed?() }
            return noErr
        }, 1, &spec, nil, nil)
    }
}
