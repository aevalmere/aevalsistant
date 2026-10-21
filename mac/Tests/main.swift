import Foundation

// mac/test.sh builds this with the app's sources. `Tests <folder>` also writes renders of the
// card, the menu bar glyph, and the Settings window there (macOS only).
CoreTests.run()
#if canImport(AppKit)
AppTests.run(CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : nil)
#endif
print("\(passed) passed, \(failed) failed")
exit(failed == 0 ? 0 : 1)
