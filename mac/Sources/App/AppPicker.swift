import AppKit

// "Add an app": the apps running now, with a search field. Clicking one adds it to the apps that
// keep the Mac awake while open; clicking one already added takes it off again.
final class AppPicker: NSWindow, NSWindowDelegate, NSTableViewDataSource, NSTableViewDelegate, NSSearchFieldDelegate {
    struct Entry {
        let name: String    // lowercase executable name, what the watch list stores
        let label: String
        let icon: NSImage?
    }

    var onClose: (() -> Void)?

    private let isWatched: (String) -> Bool
    private let onAdd: (String, String) -> Void
    private let onRemove: (String) -> Void
    private let search = NSSearchField()
    private let hint = NSTextField(labelWithString: "Click an app to add it. Click an added app to take it off.")
    private let table = NSTableView()
    private let scroll = NSScrollView()
    private var entries: [Entry] = []
    private var shown: [Entry] = []

    init(isWatched: @escaping (String) -> Bool, onAdd: @escaping (String, String) -> Void, onRemove: @escaping (String) -> Void) {
        self.isWatched = isWatched
        self.onAdd = onAdd
        self.onRemove = onRemove
        super.init(contentRect: NSRect(x: 0, y: 0, width: 420, height: 480),
                   styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        title = "Add an app"
        appearance = NSAppearance(named: .aqua)
        backgroundColor = Theme.paper
        titlebarAppearsTransparent = true
        isReleasedWhenClosed = false
        minSize = NSSize(width: 320, height: 240)
        delegate = self

        let content = NSView(frame: NSRect(x: 0, y: 0, width: 420, height: 480))
        content.wantsLayer = true
        content.layer?.backgroundColor = Theme.paper.cgColor

        search.placeholderString = "Search open apps"
        search.delegate = self
        search.font = Theme.font(13)
        search.translatesAutoresizingMaskIntoConstraints = false

        hint.font = Theme.font(12)
        hint.textColor = Theme.slate
        hint.lineBreakMode = .byWordWrapping
        hint.maximumNumberOfLines = 2
        hint.translatesAutoresizingMaskIntoConstraints = false

        let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("app"))
        column.resizingMask = .autoresizingMask
        column.width = 380
        table.addTableColumn(column)
        table.headerView = nil
        table.rowHeight = 32
        table.intercellSpacing = NSSize(width: 0, height: 2)
        table.backgroundColor = Theme.paper
        table.columnAutoresizingStyle = .uniformColumnAutoresizingStyle
        table.dataSource = self
        table.delegate = self
        table.target = self
        table.action = #selector(rowClicked)

        scroll.documentView = table
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.drawsBackground = true
        scroll.backgroundColor = Theme.paper
        scroll.borderType = .noBorder
        scroll.translatesAutoresizingMaskIntoConstraints = false

        content.addSubview(search)
        content.addSubview(hint)
        content.addSubview(scroll)
        NSLayoutConstraint.activate([
            search.topAnchor.constraint(equalTo: content.topAnchor, constant: 36),
            search.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 16),
            search.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -16),
            hint.topAnchor.constraint(equalTo: search.bottomAnchor, constant: 8),
            hint.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 18),
            hint.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -16),
            scroll.topAnchor.constraint(equalTo: hint.bottomAnchor, constant: 8),
            scroll.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 8),
            scroll.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -8),
            scroll.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -8),
        ])
        contentView = content
    }

    required init?(coder: NSCoder) { fatalError() }

    // Opens next to Settings when it is up, else in the middle of the screen.
    func present(near other: NSWindow?) {
        reload()
        if let o = other {
            setFrameOrigin(NSPoint(x: o.frame.maxX + 12, y: o.frame.maxY - frame.height))
        } else {
            center()
        }
        NSApp.activate(ignoringOtherApps: true)
        makeKeyAndOrderFront(nil)
        makeFirstResponder(search)
    }

    // Regular apps (the ones with a Dock icon), one entry per executable, by name.
    func reload() {
        let me = Bundle.main.bundleIdentifier
        var seen = Set<String>()
        var out: [Entry] = []
        for app in NSWorkspace.shared.runningApplications where app.activationPolicy == .regular {
            if let me = me, app.bundleIdentifier == me { continue }
            let exe = app.executableURL?.lastPathComponent ?? ""
            let name = (exe.isEmpty ? (app.localizedName ?? "") : exe).lowercased()
            if name.isEmpty || seen.contains(name) { continue }
            seen.insert(name)
            out.append(Entry(name: name, label: app.localizedName ?? name, icon: app.icon))
        }
        entries = out.sorted { $0.label.localizedCaseInsensitiveCompare($1.label) == .orderedAscending }
        filter()
    }

    private func filter() {
        let q = search.stringValue.trimmingCharacters(in: .whitespaces).lowercased()
        shown = q.isEmpty ? entries : entries.filter { $0.label.lowercased().contains(q) || $0.name.contains(q) }
        table.reloadData()
    }

    private func toggle(_ row: Int) {
        guard row >= 0, row < shown.count else { return }
        let e = shown[row]
        if isWatched(e.name) { onRemove(e.name) } else { onAdd(e.name, e.label) }
        table.reloadData(forRowIndexes: IndexSet(integer: row), columnIndexes: IndexSet(integer: 0))
    }

    @objc private func rowClicked() { toggle(table.clickedRow) }

    // ---- table ----

    func numberOfRows(in tableView: NSTableView) -> Int { shown.count }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard row >= 0, row < shown.count else { return nil }
        let e = shown[row]
        let cell = NSTableCellView()
        let icon = NSImageView()
        icon.image = e.icon
        icon.imageScaling = .scaleProportionallyUpOrDown
        icon.translatesAutoresizingMaskIntoConstraints = false
        let name = NSTextField(labelWithString: e.label)
        name.font = Theme.font(13)
        name.textColor = Theme.ink
        name.lineBreakMode = .byTruncatingTail
        name.translatesAutoresizingMaskIntoConstraints = false
        let added = NSTextField(labelWithString: isWatched(e.name) ? "Added" : "")
        added.font = Theme.font(12)
        added.textColor = Theme.slate
        added.alignment = .right
        added.translatesAutoresizingMaskIntoConstraints = false
        added.setContentCompressionResistancePriority(.required, for: .horizontal)
        cell.addSubview(icon)
        cell.addSubview(name)
        cell.addSubview(added)
        NSLayoutConstraint.activate([
            icon.leadingAnchor.constraint(equalTo: cell.leadingAnchor, constant: 8),
            icon.centerYAnchor.constraint(equalTo: cell.centerYAnchor),
            icon.widthAnchor.constraint(equalToConstant: 20),
            icon.heightAnchor.constraint(equalToConstant: 20),
            name.leadingAnchor.constraint(equalTo: icon.trailingAnchor, constant: 10),
            name.centerYAnchor.constraint(equalTo: cell.centerYAnchor),
            added.leadingAnchor.constraint(greaterThanOrEqualTo: name.trailingAnchor, constant: 8),
            added.trailingAnchor.constraint(equalTo: cell.trailingAnchor, constant: -10),
            added.centerYAnchor.constraint(equalTo: cell.centerYAnchor),
        ])
        cell.textField = name
        cell.imageView = icon
        cell.setAccessibilityLabel(isWatched(e.name) ? e.label + ", added" : e.label)
        return cell
    }

    // ---- search field: typing filters, Down moves into the list, Return toggles ----

    func controlTextDidChange(_ obj: Notification) { filter() }

    func control(_ control: NSControl, textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
        if commandSelector == #selector(NSResponder.insertNewline(_:)) {
            toggle(table.selectedRow >= 0 ? table.selectedRow : 0)
            return true
        }
        if commandSelector == #selector(NSResponder.moveDown(_:)) {
            let next = min(shown.count - 1, table.selectedRow + 1)
            if next >= 0 { table.selectRowIndexes(IndexSet(integer: next), byExtendingSelection: false); table.scrollRowToVisible(next) }
            return true
        }
        if commandSelector == #selector(NSResponder.moveUp(_:)) {
            let prev = max(0, table.selectedRow - 1)
            if !shown.isEmpty { table.selectRowIndexes(IndexSet(integer: prev), byExtendingSelection: false); table.scrollRowToVisible(prev) }
            return true
        }
        if commandSelector == #selector(NSResponder.cancelOperation(_:)) {
            close()
            return true
        }
        return false
    }

    override func keyDown(with event: NSEvent) {
        switch event.keyCode {
        case 53: close()                                  // Escape
        case 36, 76: toggle(table.selectedRow)            // Return, Enter
        default: super.keyDown(with: event)
        }
    }

    override func cancelOperation(_ sender: Any?) { close() }

    func windowDidBecomeKey(_ notification: Notification) { reload() }

    func windowWillClose(_ notification: Notification) { onClose?() }
}
