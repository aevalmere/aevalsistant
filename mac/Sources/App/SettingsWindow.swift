import AppKit

// A toggle on the settings page, kept by the app to update the row while the window is open.
// Setting `on` from code never calls the row's callback, so the app can mirror its own state into
// the page without a feedback loop.
final class Toggle {
    fileprivate let row: ToggleRow
    fileprivate init(_ row: ToggleRow) { self.row = row }

    var on: Bool {
        get { row.on }
        set { row.set(newValue, notify: false) }
    }

    // The second line under the label: a description, or live status such as "Connected".
    var note: String {
        get { row.note }
        set { row.setNote(newValue) }
    }

    // The path a click or Space takes, for tests.
    func click() { row.set(!row.on, notify: true) }
    var shown: Bool { row.shown }
}

final class Choice {
    fileprivate let row: ChoiceRow
    fileprivate init(_ row: ChoiceRow) { self.row = row }
    var selected: Int {
        get { row.selected }
        set { row.select(newValue, notify: false) }
    }
    func click(_ i: Int) { row.select(i, notify: true) }
}

final class Note {
    fileprivate let row: NoteRow
    fileprivate init(_ row: NoteRow) { self.row = row }
    var text: String {
        get { row.text }
        set { row.setText(newValue) }
    }
    var shown: Bool { row.shown }
}

// A value that eases from wherever it is toward a target. Drawing reads it with `value`.
final class Glide {
    private(set) var target: Double
    private var from: Double
    private var t0 = 0.0
    private let ms: Double

    init(_ v: Double, ms: Double) { target = v; from = v; self.ms = ms }

    private static var now: Double { CACurrentMediaTime() * 1000 }

    func set(_ v: Double, animate: Bool) {
        if v == target && (!moving || animate) { return }
        from = animate ? value : v
        target = v
        t0 = Glide.now
    }

    var value: Double {
        let p = min(1, (Glide.now - t0) / ms)
        return p >= 1 ? target : from + (target - from) * Ease.enter(p)
    }

    var moving: Bool { Glide.now - t0 < ms && from != target }
}

private protocol Animated: AnyObject {
    var moving: Bool { get }
}

// The settings page. The app adds rows top to bottom; a row added under a toggle is indented and
// shown only while that toggle, and every toggle above it, is on. Changes apply at once, as in
// System Settings, so there is no OK or Cancel. The look follows src/SettingsWindow.cs.
final class SettingsWindow: NSWindow, NSWindowDelegate {
    static let pageWidth: CGFloat = 480, edge: CGFloat = 12, pad: CGFloat = 12, step: CGFloat = 24, rowMin: CGFloat = 40, rowPad: CGFloat = 12, radius: CGFloat = 6
    static let switchW: CGFloat = 36, switchH: CGFloat = 20, buttonH: CGFloat = 32, buttonPad: CGFloat = 16, segmentH: CGFloat = 28, segmentPad: CGFloat = 8
    static let linkH: CGFloat = 28, linkPad: CGFloat = 8, ringPad: CGFloat = 4
    static let body = Theme.font(13), small = Theme.font(12), head = Theme.semibold(12)

    var onClose: (() -> Void)?

    fileprivate var items: [Row] = []
    private let page = PageView()
    private let scroll = NSScrollView()
    private var timer: Timer?
    private var movers: [ObjectIdentifier: Animated] = [:]
    private var settle = false
    fileprivate var contentHeight: CGFloat = 0

    init() {
        super.init(contentRect: NSRect(x: 0, y: 0, width: SettingsWindow.pageWidth, height: 480),
                   styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        title = "Aevalsistant settings"
        // The page keeps its own warm palette in dark mode too, like the card.
        appearance = NSAppearance(named: .aqua)
        backgroundColor = Theme.paper
        titlebarAppearsTransparent = true
        isReleasedWhenClosed = false
        delegate = self
        minSize = NSSize(width: 360, height: 240)
        autorecalculatesKeyViewLoop = true

        page.owner = self
        scroll.documentView = page
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.drawsBackground = true
        scroll.backgroundColor = Theme.paper
        scroll.borderType = .noBorder
        contentView = scroll
    }

    required init?(coder: NSCoder) { fatalError() }

    func section(_ title: String) { add(HeaderRow(title), under: nil) }

    @discardableResult
    func addToggle(_ label: String, _ note: String?, _ on: Bool, under parent: Toggle? = nil, _ changed: @escaping (Bool) -> Void) -> Toggle {
        let row = ToggleRow(label, note ?? "", on, changed)
        add(row, under: parent)
        return row.setting
    }

    @discardableResult
    func addChoice(_ label: String, _ options: [String], _ selected: Int, under parent: Toggle? = nil, _ changed: @escaping (Int) -> Void) -> Choice {
        let row = ChoiceRow(label, options, selected, changed)
        add(row, under: parent)
        return Choice(row)
    }

    @discardableResult
    func addNote(_ text: String, under parent: Toggle? = nil) -> Note {
        let row = NoteRow(text)
        add(row, under: parent)
        return Note(row)
    }

    // Buttons added one after another under the same toggle share a row.
    func addButton(_ label: String, under parent: Toggle? = nil, danger: Bool = false, _ click: @escaping () -> Void) {
        var row = items.last as? ActionRow
        if row == nil || row!.links || row!.under !== parent {
            row = ActionRow(links: false)
            add(row!, under: parent)
        }
        row!.addPart(ActionButton(label, danger: danger, click))
        relayout()
    }

    // Opens in the browser. Links added one after another share a row, like buttons.
    func addLink(_ label: String, _ url: String) {
        var row = items.last as? ActionRow
        if row == nil || !row!.links || row!.under != nil {
            row = ActionRow(links: true)
            add(row!, under: nil)
        }
        row!.addPart(LinkItem(label, url))
        relayout()
    }

    // A toggle placed right after another toggle's row instead of at the end of the page, so a
    // list that grows while the window is open (the watched apps) keeps its button below it.
    // With no anchor it goes at the end, like addToggle.
    @discardableResult
    func insertToggle(afterRowOf anchor: Toggle?, _ label: String, _ note: String?, _ on: Bool, under parent: Toggle?, _ changed: @escaping (Bool) -> Void) -> Toggle {
        let row = ToggleRow(label, note ?? "", on, changed)
        row.win = self
        row.under = parent
        row.depth = parent.map { $0.row.depth + 1 } ?? 0
        var at = items.count
        if let a = anchor, let i = items.firstIndex(where: { $0 === a.row }) { at = i + 1 }
        row.first = at == 0
        items.insert(row, at: at)
        page.addSubview(row)
        row.open.set(0, animate: false)   // opens with the usual slide
        showHide()
        return row.setting
    }

    // Takes a toggle's row off the page; the rows under it close up.
    func remove(_ t: Toggle) {
        guard let i = items.firstIndex(where: { $0 === t.row }) else { return }
        items.remove(at: i)
        t.row.removeFromSuperview()
        if let head = items.first { head.first = true }
        relayout()
    }

    private func add(_ row: Row, under parent: Toggle?) {
        row.win = self
        row.under = parent
        row.depth = parent.map { $0.row.depth + 1 } ?? 0
        row.first = items.isEmpty
        items.append(row)
        page.addSubview(row)
        row.open.set(inView(row) ? 1 : 0, animate: false)
        relayout()
    }

    private func inView(_ it: Row) -> Bool { it.wanted && (it.under.map { $0.on && inView($0.row) } ?? true) }

    // Called whenever a toggle flips or a note empties or fills: rows whose chain of parent
    // toggles changed open or close, and the page reflows around them.
    fileprivate func showHide() {
        let animate = canAnimate
        for it in items {
            let want = inView(it)
            if want != it.shown { it.open.set(want ? 1 : 0, animate: animate) }
        }
        if animate { runFrames() }
        relayout()
    }

    // Tall enough for every row, up to most of the screen; the page scrolls past that.
    func present() {
        relayout(width: SettingsWindow.pageWidth)
        let area = (NSScreen.screens.first { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) } ?? NSScreen.main)?.visibleFrame
            ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        let h = min(contentHeight, area.height * 0.85 - 28)
        setContentSize(NSSize(width: SettingsWindow.pageWidth, height: h))
        setFrameOrigin(NSPoint(x: area.midX - frame.width / 2, y: area.midY - frame.height / 2))
        relayout()
        page.scroll(.zero)
        NSApp.activate(ignoringOtherApps: true)
        makeKeyAndOrderFront(nil)
        makeFirstResponder(nil)   // no focus ring until the keyboard asks for one
    }

    fileprivate var canAnimate: Bool { isVisible && !isMiniaturized && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion }

    fileprivate func kick(_ v: Animated) {
        guard canAnimate else { return }
        movers[ObjectIdentifier(v)] = v
        runFrames()
    }

    private func runFrames() {
        if timer != nil { return }
        let t = Timer(timeInterval: 1.0 / 60, repeats: true) { [weak self] _ in self?.frameStep() }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    // One frame: reflow while rows open or close, and repaint views with a running animation.
    // Each runs once more after it ends, to paint the end.
    private func frameStep() {
        let layout = items.contains { $0.open.moving }
        if layout || settle { relayout(); settle = layout }
        for (id, m) in movers {
            (m as? NSView)?.needsDisplay = true
            if !m.moving { movers.removeValue(forKey: id) }
        }
        if !layout && !settle && movers.isEmpty { timer?.invalidate(); timer = nil }
    }

    // Stacks the rows. A row that is opening or closing takes its height times how far open it
    // is, so the rows under it slide instead of jumping.
    fileprivate func relayout(width: CGFloat = 0) {
        let width = width > 0 ? width : max(scroll.contentSize.width, 200)
        var y: CGFloat = 8
        for it in items {
            let o = CGFloat(it.open.value)
            if o <= 0.001 {
                it.isHidden = true
                continue
            }
            let x = SettingsWindow.edge + SettingsWindow.step * CGFloat(it.depth)
            let w = max(120, width - x - SettingsWindow.edge)
            let full = it.fullHeight(w)
            it.full = full
            let h = o >= 0.999 ? full : (full * o).rounded()
            it.frame = NSRect(x: x, y: y, width: w, height: h)
            it.alphaValue = o
            it.isHidden = false
            it.layoutParts()
            y += h
        }
        contentHeight = y + 16
        page.frame = NSRect(x: 0, y: 0, width: width, height: max(contentHeight, scroll.contentSize.height))
        page.needsDisplay = true
    }

    var pageHeight: CGFloat { contentHeight }

    // The whole page as an image, scrolled or not, for review.
    func snapshot() -> NSBitmapImageRep? {
        relayout(width: SettingsWindow.pageWidth)
        let r = NSRect(x: 0, y: 0, width: page.bounds.width, height: contentHeight)
        guard let rep = page.bitmapImageRepForCachingDisplay(in: r) else { return nil }
        page.cacheDisplay(in: r, to: rep)
        return rep
    }

    func windowDidResize(_ notification: Notification) { relayout() }

    func windowWillClose(_ notification: Notification) {
        timer?.invalidate()
        timer = nil
        onClose?()
    }

    override func cancelOperation(_ sender: Any?) { close() }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 { close() } else { super.keyDown(with: event) }   // Escape
    }

    static func textHeight(_ s: String, _ f: NSFont, _ w: CGFloat) -> CGFloat {
        let r = ((s.isEmpty ? " " : s) as NSString).boundingRect(with: NSSize(width: max(1, w), height: 100_000),
                                                                 options: [.usesLineFragmentOrigin, .usesFontLeading], attributes: [.font: f])
        return ceil(r.height)
    }

    static func textWidth(_ s: String, _ f: NSFont) -> CGFloat { ceil((s as NSString).size(withAttributes: [.font: f]).width) }

    static func write(_ s: String, _ f: NSFont, _ color: NSColor, _ r: NSRect, align: NSTextAlignment = .left, underline: Bool = false) {
        let p = NSMutableParagraphStyle()
        p.alignment = align
        var attrs: [NSAttributedString.Key: Any] = [.font: f, .foregroundColor: color, .paragraphStyle: p]
        if underline { attrs[.underlineStyle] = NSUnderlineStyle.single.rawValue }
        (s as NSString).draw(with: r, options: [.usesLineFragmentOrigin, .usesFontLeading], attributes: attrs)
    }

    // One line, centered in r.
    static func writeCentered(_ s: String, _ f: NSFont, _ color: NSColor, _ r: NSRect, underline: Bool = false, align: NSTextAlignment = .center) {
        let h = ceil(f.ascender - f.descender + f.leading)
        write(s, f, color, NSRect(x: r.minX, y: r.midY - h / 2, width: r.width, height: h), align: align, underline: underline)
    }

    static func fill(_ r: NSRect, _ radius: CGFloat, _ c: NSColor) {
        c.setFill()
        Theme.round(r, radius).fill()
    }

    static func ring(_ r: NSRect, _ radius: CGFloat) {
        let w: CGFloat = 2
        let p = Theme.round(r.insetBy(dx: w / 2, dy: w / 2), max(1, radius - w / 2))
        p.lineWidth = w
        Theme.slate.setStroke()
        p.stroke()
    }

    // Tree lines: one thin rule per nesting level, down the left of each run of rows that hang
    // under the same toggle.
    fileprivate func drawGuides() {
        let deepest = items.map { $0.depth }.max() ?? 0
        guard deepest > 0 else { return }
        Theme.paperEdge.setFill()
        for level in 1...deepest {
            let x = SettingsWindow.edge + SettingsWindow.step * CGFloat(level - 1) + SettingsWindow.pad + 4, inset: CGFloat = 4
            var owner: Toggle?
            var top: CGFloat = 0, bottom: CGFloat = 0
            func rule() { if owner != nil && bottom - top > 2 * inset { NSRect(x: x, y: top + inset, width: 1, height: bottom - top - 2 * inset).fill() } }
            for it in items where !it.isHidden && it.frame.height > 0 {
                let at = it.depth >= level ? SettingsWindow.ancestor(it, at: level - 1) : nil
                if at !== owner { rule(); owner = at; top = it.frame.minY }
                bottom = it.frame.maxY
            }
            rule()
        }
    }

    private static func ancestor(_ it: Row, at depth: Int) -> Toggle? {
        var t = it.under
        while let u = t, u.row.depth > depth { t = u.row.under }
        return t?.row.depth == depth ? t : nil
    }
}

private final class PageView: NSView {
    weak var owner: SettingsWindow?
    override var isFlipped: Bool { true }
    override func draw(_ dirtyRect: NSRect) {
        Theme.paper.setFill()
        bounds.fill()
        owner?.drawGuides()
    }
}

// Anything on the page that can take keyboard focus: Space or Return presses it, the arrow keys
// move between them.
private class Part: NSView, Animated {
    // Rows know their window; the buttons and segments inside a row ask the row.
    private weak var ownWin: SettingsWindow?
    var win: SettingsWindow? {
        get { ownWin ?? (superview as? Part)?.win }
        set { ownWin = newValue }
    }
    let hover = Glide(0, ms: 120)
    var pressed = false
    private var tracking: NSTrackingArea?

    override var isFlipped: Bool { true }
    var moving: Bool { hover.moving }
    var focusable: Bool { true }
    var focused: Bool { window?.firstResponder === self && window?.isKeyWindow == true }

    override var acceptsFirstResponder: Bool { focusable && !isHiddenOrHasHiddenAncestor }
    override func becomeFirstResponder() -> Bool {
        needsDisplay = true
        scrollToVisible(bounds.insetBy(dx: 0, dy: -8))
        return true
    }
    override func resignFirstResponder() -> Bool { needsDisplay = true; return true }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let t = tracking { removeTrackingArea(t) }
        let t = NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect], owner: self)
        addTrackingArea(t)
        tracking = t
    }

    override func mouseEntered(with event: NSEvent) { setHover(true) }
    override func mouseExited(with event: NSEvent) { setHover(false) }

    func setHover(_ on: Bool) {
        hover.set(on ? 1 : 0, animate: win?.canAnimate ?? false)
        needsDisplay = true
        win?.kick(self)
    }

    override func mouseDown(with event: NSEvent) {
        pressed = true
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        let inside = bounds.contains(convert(event.locationInWindow, from: nil))
        pressed = false
        needsDisplay = true
        if inside { press() }
    }

    func press() {}

    override func keyDown(with event: NSEvent) {
        switch event.keyCode {
        case 49, 36, 76: press()                                   // space, return, enter
        case 123, 126: window?.selectPreviousKeyView(nil)          // left, up
        case 124, 125: window?.selectNextKeyView(nil)              // right, down
        default: super.keyDown(with: event)
        }
    }

    override func accessibilityPerformPress() -> Bool { press(); return true }
}

private class Row: Part {
    var under: Toggle?
    var depth = 0
    var first = false
    let open = Glide(1, ms: 200)
    var full: CGFloat = 0           // the row's height when open; drawing uses this, not the frame

    var wanted: Bool { true }
    var shown: Bool { open.target > 0.5 }
    override var focusable: Bool { false }
    override var moving: Bool { super.moving || open.moving }

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.masksToBounds = true
    }
    required init?(coder: NSCoder) { fatalError() }

    func fullHeight(_ width: CGFloat) -> CGFloat { SettingsWindow.rowMin }
    func layoutParts() {}
}

private final class HeaderRow: Row {
    let title: String
    init(_ title: String) {
        self.title = title
        super.init(frame: .zero)
        setAccessibilityElement(true)
        setAccessibilityRole(.staticText)
        setAccessibilityLabel(title)
    }
    required init?(coder: NSCoder) { fatalError() }

    private var textH: CGFloat = 0
    override func fullHeight(_ width: CGFloat) -> CGFloat {
        textH = SettingsWindow.textHeight(title, SettingsWindow.head, width - 2 * SettingsWindow.pad)
        return (first ? 8 : 24) + textH + 4
    }
    override func draw(_ dirtyRect: NSRect) {
        SettingsWindow.write(title, SettingsWindow.head, Theme.slate, NSRect(x: SettingsWindow.pad, y: full - 4 - textH, width: bounds.width - 2 * SettingsWindow.pad, height: textH))
    }
}

private final class NoteRow: Row {
    private(set) var text: String
    init(_ text: String) {
        self.text = text
        super.init(frame: .zero)
        setAccessibilityElement(true)
        setAccessibilityRole(.staticText)
        setAccessibilityLabel(text)
    }
    required init?(coder: NSCoder) { fatalError() }

    override var wanted: Bool { !text.isEmpty }

    // An emptied note keeps drawing its last words while it closes.
    private var painted = ""
    func setText(_ t: String) {
        if t == text { return }
        text = t
        if !t.isEmpty { painted = t }
        setAccessibilityLabel(t)
        needsDisplay = true
        win?.showHide()
    }

    private var textH: CGFloat = 0
    override func fullHeight(_ width: CGFloat) -> CGFloat {
        if !text.isEmpty { painted = text }
        textH = SettingsWindow.textHeight(painted, SettingsWindow.small, width - 2 * SettingsWindow.pad)
        return 2 + textH + 8
    }
    override func draw(_ dirtyRect: NSRect) {
        SettingsWindow.write(painted, SettingsWindow.small, Theme.slate, NSRect(x: SettingsWindow.pad, y: 2, width: bounds.width - 2 * SettingsWindow.pad, height: textH))
    }
}

private final class ToggleRow: Row {
    let label: String
    private(set) var note: String
    private(set) var on: Bool
    private let changed: (Bool) -> Void
    private let knob: Glide
    lazy var setting = Toggle(self)

    init(_ label: String, _ note: String, _ on: Bool, _ changed: @escaping (Bool) -> Void) {
        self.label = label
        self.note = note
        self.on = on
        self.changed = changed
        knob = Glide(on ? 1 : 0, ms: 150)
        super.init(frame: .zero)
        setAccessibilityElement(true)
        setAccessibilityRole(.checkBox)
        setAccessibilityLabel(label)
        setAccessibilityHelp(note)
        setAccessibilityValue(NSNumber(value: on))
    }
    required init?(coder: NSCoder) { fatalError() }

    override var focusable: Bool { true }
    override var moving: Bool { super.moving || knob.moving }

    func set(_ v: Bool, notify: Bool) {
        if v == on { return }
        on = v
        knob.set(v ? 1 : 0, animate: win?.canAnimate ?? false)
        setAccessibilityValue(NSNumber(value: v))
        needsDisplay = true
        win?.kick(self)
        win?.showHide()
        if notify { changed(v) }
    }

    func setNote(_ n: String) {
        if n == note { return }
        note = n
        setAccessibilityHelp(n)
        win?.relayout()
        needsDisplay = true
    }

    override func press() { set(!on, notify: true) }

    private func textWidth(_ width: CGFloat) -> CGFloat { max(48, width - 2 * SettingsWindow.pad - SettingsWindow.switchW - 16) }
    private var labelH: CGFloat = 0, noteH: CGFloat = 0

    override func fullHeight(_ width: CGFloat) -> CGFloat {
        let w = textWidth(width)
        labelH = SettingsWindow.textHeight(label, SettingsWindow.body, w)
        noteH = note.isEmpty ? 0 : SettingsWindow.textHeight(note, SettingsWindow.small, w)
        return max(SettingsWindow.rowMin, labelH + noteH + 2 * SettingsWindow.rowPad)
    }

    override func draw(_ dirtyRect: NSRect) {
        let box = NSRect(x: 0, y: 0, width: bounds.width, height: full)
        let h = CGFloat(hover.value)
        if pressed { SettingsWindow.fill(box, SettingsWindow.radius, Theme.paperEdge) }
        else if h > 0 { SettingsWindow.fill(box, SettingsWindow.radius, Theme.mix(Theme.paper, Theme.paperDeep, h)) }

        let w = textWidth(bounds.width)
        let y = (full - labelH - noteH) / 2
        SettingsWindow.write(label, SettingsWindow.body, Theme.ink, NSRect(x: SettingsWindow.pad, y: y, width: w, height: labelH))
        if noteH > 0 { SettingsWindow.write(note, SettingsWindow.small, Theme.slate, NSRect(x: SettingsWindow.pad, y: y + labelH, width: w, height: noteH)) }

        let k = CGFloat(knob.value)
        let track = NSRect(x: bounds.width - SettingsWindow.pad - SettingsWindow.switchW, y: (full - SettingsWindow.switchH) / 2,
                           width: SettingsWindow.switchW, height: SettingsWindow.switchH)
        SettingsWindow.fill(track, track.height / 2, Theme.mix(Theme.slateMist, Theme.slate, k))
        let m: CGFloat = 3, d = track.height - 2 * m
        let dot = NSRect(x: track.minX + m + (track.width - 2 * m - d) * k, y: track.minY + m, width: d, height: d)
        Theme.paper.setFill()
        NSBezierPath(ovalIn: dot).fill()
        let edge = NSBezierPath(ovalIn: dot.insetBy(dx: 0.4, dy: 0.4))
        edge.lineWidth = 0.75
        Theme.shadow.withAlphaComponent(0.16).setStroke()
        edge.stroke()

        if focused { SettingsWindow.ring(box, SettingsWindow.radius) }
    }
}

private final class ChoiceRow: Row {
    let label: String
    let options: [String]
    private(set) var selected: Int
    private let changed: (Int) -> Void
    fileprivate let slide: Glide
    private var segments: [Segment] = []
    private var segW: CGFloat = 0, labelH: CGFloat = 0

    init(_ label: String, _ options: [String], _ selected: Int, _ changed: @escaping (Int) -> Void) {
        self.label = label
        self.options = options
        self.selected = selected
        self.changed = changed
        slide = Glide(Double(selected), ms: 180)
        super.init(frame: .zero)
        setAccessibilityElement(true)
        setAccessibilityRole(.radioGroup)
        setAccessibilityLabel(label)
        for (i, o) in options.enumerated() {
            let s = Segment(self, i, o)
            segments.append(s)
            addSubview(s)
        }
    }
    required init?(coder: NSCoder) { fatalError() }

    override var moving: Bool { super.moving || slide.moving }

    func select(_ i: Int, notify: Bool) {
        guard i >= 0, i < options.count, i != selected else { return }
        selected = i
        slide.set(Double(i), animate: win?.canAnimate ?? false)
        for s in segments { s.needsDisplay = true; s.setAccessibilityValue(NSNumber(value: s.index == i)) }
        win?.kick(self)
        segments.forEach { win?.kick($0) }
        if notify { changed(i) }
    }

    private var groupWidth: CGFloat { CGFloat(options.count) * segW + 4 }

    override func fullHeight(_ width: CGFloat) -> CGFloat {
        segW = (options.map { SettingsWindow.textWidth($0, SettingsWindow.small) }.max() ?? 0) + 2 * SettingsWindow.segmentPad
        labelH = SettingsWindow.textHeight(label, SettingsWindow.body, labelWidth(width))
        return max(SettingsWindow.rowMin, max(labelH + 2 * SettingsWindow.rowPad, SettingsWindow.segmentH + 12))
    }

    private func labelWidth(_ width: CGFloat) -> CGFloat { max(48, width - 2 * SettingsWindow.pad - groupWidth - 16) }
    fileprivate var group: NSRect { NSRect(x: bounds.width - SettingsWindow.pad - groupWidth, y: (full - SettingsWindow.segmentH) / 2, width: groupWidth, height: SettingsWindow.segmentH) }

    override func layoutParts() {
        let g = group
        for (i, s) in segments.enumerated() { s.frame = NSRect(x: g.minX + 2 + CGFloat(i) * segW, y: g.minY + 2, width: segW, height: g.height - 4) }
    }

    override func draw(_ dirtyRect: NSRect) {
        SettingsWindow.write(label, SettingsWindow.body, Theme.ink, NSRect(x: SettingsWindow.pad, y: (full - labelH) / 2, width: labelWidth(bounds.width), height: labelH))
        SettingsWindow.fill(group, SettingsWindow.radius, Theme.paperDeep)
    }
}

private final class Segment: Part {
    unowned let row: ChoiceRow
    let index: Int
    let text: String

    init(_ row: ChoiceRow, _ index: Int, _ text: String) {
        self.row = row
        self.index = index
        self.text = text
        super.init(frame: .zero)
        setAccessibilityElement(true)
        setAccessibilityRole(.radioButton)
        setAccessibilityLabel(text)
        setAccessibilityValue(NSNumber(value: row.selected == index))
    }
    required init?(coder: NSCoder) { fatalError() }

    override var moving: Bool { super.moving || row.slide.moving }

    override func press() { row.select(index, notify: true) }

    override func draw(_ dirtyRect: NSRect) {
        let box = bounds
        let h = CGFloat(hover.value)
        if h > 0 && row.selected != index { SettingsWindow.fill(box, 4, Theme.mix(Theme.paperDeep, Theme.paperEdge, h)) }
        // The selection slides from segment to segment; each segment paints its share of it.
        let at = CGFloat(row.slide.value)
        SettingsWindow.fill(NSRect(x: (at - CGFloat(index)) * box.width, y: 0, width: box.width, height: box.height), 4, Theme.slate)
        let cover = max(0, 1 - abs(at - CGFloat(index)))
        SettingsWindow.writeCentered(text, SettingsWindow.small, Theme.mix(Theme.slate, Theme.paper, cover), box)
        if focused { SettingsWindow.ring(box.insetBy(dx: -1, dy: -1), 5) }
    }
}

private final class ActionRow: Row {
    let links: Bool
    private var parts: [Part] = []
    init(links: Bool) {
        self.links = links
        super.init(frame: .zero)
    }
    required init?(coder: NSCoder) { fatalError() }

    func addPart(_ p: Part) {
        parts.append(p)
        addSubview(p)
    }

    private func natural(_ p: Part) -> NSSize {
        if let b = p as? ActionButton { return b.natural }
        if let l = p as? LinkItem { return l.natural }
        return .zero
    }

    // Each part carries room on its sides for the focus ring, so x tracks visible edges: the
    // first face lines up with the text of the rows above, and gaps run face to face.
    private func place(_ width: CGFloat, apply: Bool) -> CGFloat {
        let left = SettingsWindow.pad, top: CGFloat = 4, gap: CGFloat = links ? 16 : 8, gapY: CGFloat = 4
        var x = left, y = top, line: CGFloat = 0
        for p in parts {
            let size = natural(p)
            let inset = links ? SettingsWindow.linkPad : SettingsWindow.ringPad, face = size.width - 2 * inset
            if x > left && x + face > width - left { x = left; y += line + gapY; line = 0 }
            if apply { p.frame = NSRect(x: x - inset, y: y, width: size.width, height: size.height) }
            x += face + gap
            line = max(line, size.height)
        }
        return y + line + top
    }

    override func fullHeight(_ width: CGFloat) -> CGFloat { place(width, apply: false) }
    override func layoutParts() { _ = place(bounds.width, apply: true) }
}

private final class ActionButton: Part {
    let label: String
    let danger: Bool
    let click: () -> Void
    init(_ label: String, danger: Bool, _ click: @escaping () -> Void) {
        self.label = label
        self.danger = danger
        self.click = click
        super.init(frame: .zero)
        setAccessibilityElement(true)
        setAccessibilityRole(.button)
        setAccessibilityLabel(label)
    }
    required init?(coder: NSCoder) { fatalError() }

    var natural: NSSize {
        NSSize(width: SettingsWindow.textWidth(label, SettingsWindow.body) + 2 * SettingsWindow.buttonPad + 2 * SettingsWindow.ringPad,
               height: SettingsWindow.buttonH + 2 * SettingsWindow.ringPad)
    }

    override func press() { click() }

    override func draw(_ dirtyRect: NSRect) {
        let face = bounds.insetBy(dx: SettingsWindow.ringPad, dy: SettingsWindow.ringPad)
        SettingsWindow.fill(face, SettingsWindow.radius, pressed ? Theme.paperEdge : Theme.mix(Theme.paperDeep, Theme.paperEdge, CGFloat(hover.value)))
        SettingsWindow.writeCentered(label, SettingsWindow.body, danger ? Theme.clip : Theme.ink, face)
        if focused { SettingsWindow.ring(bounds, SettingsWindow.radius + SettingsWindow.ringPad) }
    }
}

private final class LinkItem: Part {
    let label: String
    let url: String
    init(_ label: String, _ url: String) {
        self.label = label
        self.url = url
        super.init(frame: .zero)
        setAccessibilityElement(true)
        setAccessibilityRole(.link)
        setAccessibilityLabel(label)
    }
    required init?(coder: NSCoder) { fatalError() }

    var natural: NSSize { NSSize(width: SettingsWindow.textWidth(label, SettingsWindow.body) + 2 * SettingsWindow.linkPad, height: SettingsWindow.linkH) }

    override func press() { if let u = URL(string: url) { NSWorkspace.shared.open(u) } }

    override func resetCursorRects() { addCursorRect(bounds, cursor: .pointingHand) }

    override func draw(_ dirtyRect: NSRect) {
        let lit = hover.target > 0 || focused
        SettingsWindow.writeCentered(label, SettingsWindow.body, pressed ? Theme.ink : Theme.slate,
                                     bounds.insetBy(dx: SettingsWindow.linkPad, dy: 0), underline: lit, align: .left)
        if focused { SettingsWindow.ring(bounds, SettingsWindow.radius) }
    }
}
