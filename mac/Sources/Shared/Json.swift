import Foundation

// Order-preserving JSON, the same shape as src/Json.cs. Numbers keep their original text so
// rewriting a user's settings.json never turns 10 into 10.0 or reorders their keys. Objects and
// arrays are classes so the installers can edit them in place.
final class JObj {
    var items: [(key: String, value: Any)] = []

    init() {}

    init(_ pairs: [(String, Any)]) {
        items = pairs.map { (key: $0.0, value: $0.1) }
    }

    // Setting nil removes the key.
    subscript(key: String) -> Any? {
        get { items.first(where: { $0.key == key })?.value }
        set {
            guard let v = newValue else { remove(key); return }
            if let i = items.firstIndex(where: { $0.key == key }) { items[i].value = v } else { items.append((key: key, value: v)) }
        }
    }

    @discardableResult
    func remove(_ key: String) -> Bool {
        let before = items.count
        items.removeAll { $0.key == key }
        return items.count != before
    }

    func str(_ key: String) -> String? { self[key] as? String }
    func obj(_ key: String) -> JObj? { self[key] as? JObj }
    func arr(_ key: String) -> JArr? { self[key] as? JArr }
}

final class JArr {
    var items: [Any]
    init(_ items: [Any] = []) { self.items = items }
}

struct JNum {
    let raw: String
    init(_ raw: String) { self.raw = raw }
    init(_ n: Int) { raw = String(n) }
    var int64: Int64 { Int64(raw) ?? 0 }
}

struct JNull {}

struct JsonError: Error, CustomStringConvertible {
    let description: String
}

enum Json {
    static func parse(_ text: String) throws -> Any {
        var p = Parser(Array(text.utf16))
        let v = try p.value()
        p.ws()
        if p.i != p.s.count { throw JsonError(description: "Trailing characters at \(p.i)") }
        return v
    }

    static func tryParse(_ text: String) -> Any? { try? parse(text) }

    private struct Parser {
        let s: [UInt16]
        var i = 0
        init(_ s: [UInt16]) { self.s = s }

        mutating func ws() {
            while i < s.count, [0x20, 0x09, 0x0A, 0x0D, 0xFEFF].contains(s[i]) { i += 1 }
        }

        mutating func value() throws -> Any {
            ws()
            guard i < s.count else { throw JsonError(description: "Unexpected end") }
            let c = s[i]
            if c == 0x7B { return try object() }
            if c == 0x5B { return try array() }
            if c == 0x22 { return try string() }
            if lit("true") { return true }
            if lit("false") { return false }
            if lit("null") { return JNull() }
            if c == 0x2D || (c >= 0x30 && c <= 0x39) { return try number() }
            throw JsonError(description: "Unexpected character at \(i)")
        }

        mutating func lit(_ word: String) -> Bool {
            let w = Array(word.utf16)
            guard i + w.count <= s.count, Array(s[i..<i + w.count]) == w else { return false }
            i += w.count
            return true
        }

        mutating func object() throws -> JObj {
            let o = JObj()
            i += 1
            ws()
            if i < s.count, s[i] == 0x7D { i += 1; return o }
            while true {
                ws()
                guard i < s.count, s[i] == 0x22 else { throw JsonError(description: "Expected key at \(i)") }
                let k = try string()
                ws()
                guard i < s.count, s[i] == 0x3A else { throw JsonError(description: "Expected ':' at \(i)") }
                i += 1
                o.items.append((key: k, value: try value()))
                ws()
                guard i < s.count else { throw JsonError(description: "Unclosed object") }
                if s[i] == 0x2C { i += 1; continue }
                if s[i] == 0x7D { i += 1; return o }
                throw JsonError(description: "Expected ',' or '}' at \(i)")
            }
        }

        mutating func array() throws -> JArr {
            let a = JArr()
            i += 1
            ws()
            if i < s.count, s[i] == 0x5D { i += 1; return a }
            while true {
                a.items.append(try value())
                ws()
                guard i < s.count else { throw JsonError(description: "Unclosed array") }
                if s[i] == 0x2C { i += 1; continue }
                if s[i] == 0x5D { i += 1; return a }
                throw JsonError(description: "Expected ',' or ']' at \(i)")
            }
        }

        // UTF-16 units throughout, so a 😀 pair decodes the way it does in C#.
        mutating func string() throws -> String {
            var out: [UInt16] = []
            i += 1
            while i < s.count {
                let c = s[i]; i += 1
                if c == 0x22 { return String(decoding: out, as: UTF16.self) }
                if c != 0x5C { out.append(c); continue }
                guard i < s.count else { break }
                let e = s[i]; i += 1
                switch e {
                case 0x22, 0x5C, 0x2F: out.append(e)
                case 0x62: out.append(0x08)
                case 0x66: out.append(0x0C)
                case 0x6E: out.append(0x0A)
                case 0x72: out.append(0x0D)
                case 0x74: out.append(0x09)
                case 0x75:
                    guard i + 4 <= s.count, let u = UInt16(String(decoding: s[i..<i + 4], as: UTF16.self), radix: 16) else {
                        throw JsonError(description: "Bad \\u escape")
                    }
                    out.append(u)
                    i += 4
                default: throw JsonError(description: "Bad escape at \(i)")
                }
            }
            throw JsonError(description: "Unclosed string")
        }

        mutating func number() throws -> JNum {
            let start = i
            if s[i] == 0x2D { i += 1 }
            let allowed = Array("0123456789.eE+-".utf16)
            while i < s.count, allowed.contains(s[i]) { i += 1 }
            let raw = String(decoding: s[start..<i], as: UTF16.self)
            guard Double(raw) != nil else { throw JsonError(description: "Bad number \(raw)") }
            return JNum(raw)
        }
    }

    // Two-space indent, the same shape Claude Code writes.
    static func write(_ v: Any) -> String {
        var out = ""
        emit(&out, v, 0)
        return out
    }

    private static func emit(_ out: inout String, _ v: Any, _ depth: Int) {
        switch v {
        case is JNull: out += "null"
        case let b as Bool: out += b ? "true" : "false"
        case let n as JNum: out += n.raw
        case let n as Int: out += String(n)
        case let str as String: out += quote(str)
        case let o as JObj:
            if o.items.isEmpty { out += "{}"; return }
            out += "{\n"
            for (k, kv) in o.items.enumerated() {
                out += String(repeating: " ", count: (depth + 1) * 2) + quote(kv.key) + ": "
                emit(&out, kv.value, depth + 1)
                out += k < o.items.count - 1 ? ",\n" : "\n"
            }
            out += String(repeating: " ", count: depth * 2) + "}"
        case let a as JArr:
            if a.items.isEmpty { out += "[]"; return }
            out += "[\n"
            for (k, item) in a.items.enumerated() {
                out += String(repeating: " ", count: (depth + 1) * 2)
                emit(&out, item, depth + 1)
                out += k < a.items.count - 1 ? ",\n" : "\n"
            }
            out += String(repeating: " ", count: depth * 2) + "]"
        default:
            out += "null"
        }
    }

    static func quote(_ str: String) -> String {
        var out: [UInt16] = [0x22]
        for c in str.utf16 {
            switch c {
            case 0x22: out += [0x5C, 0x22]
            case 0x5C: out += [0x5C, 0x5C]
            case 0x0A: out += [0x5C, 0x6E]
            case 0x0D: out += [0x5C, 0x72]
            case 0x09: out += [0x5C, 0x74]
            case 0x08: out += [0x5C, 0x62]
            case 0x0C: out += [0x5C, 0x66]
            default:
                if c < 0x20 { out += Array(String(format: "\\u%04x", Int(c)).utf16) } else { out.append(c) }
            }
        }
        out.append(0x22)
        return String(decoding: out, as: UTF16.self)
    }
}
