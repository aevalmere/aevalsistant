using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Aevalsistant
{
    // Order-preserving JSON. Numbers keep their original text so rewriting a user's
    // settings.json never turns 10 into 10.0 or reorders their keys.
    sealed class JObj
    {
        public readonly List<KeyValuePair<string, object>> Items = new List<KeyValuePair<string, object>>();

        public object this[string key]
        {
            get
            {
                foreach (var kv in Items) if (kv.Key == key) return kv.Value;
                return null;
            }
            set
            {
                for (int i = 0; i < Items.Count; i++)
                    if (Items[i].Key == key) { Items[i] = new KeyValuePair<string, object>(key, value); return; }
                Items.Add(new KeyValuePair<string, object>(key, value));
            }
        }

        public bool Remove(string key) => Items.RemoveAll(kv => kv.Key == key) > 0;
        public string Str(string key) => this[key] as string;
        public JObj Obj(string key) => this[key] as JObj;
    }

    sealed class JNum
    {
        public readonly string Raw;
        public JNum(string raw) { Raw = raw; }
        public long AsLong() => long.TryParse(Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    static class Json
    {
        public static object Parse(string s)
        {
            int i = 0;
            var v = Value(s, ref i);
            Ws(s, ref i);
            if (i != s.Length) throw new FormatException("Trailing characters at " + i);
            return v;
        }

        public static bool TryParse(string s, out object value)
        {
            try { value = Parse(s); return true; }
            catch (FormatException) { value = null; return false; }
        }

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r' || s[i] == '﻿')) i++;
        }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end");
            char c = s[i];
            if (c == '{') return Object(s, ref i);
            if (c == '[') return Array(s, ref i);
            if (c == '"') return String(s, ref i);
            if (Lit(s, ref i, "true")) return true;
            if (Lit(s, ref i, "false")) return false;
            if (Lit(s, ref i, "null")) return null;
            if (c == '-' || (c >= '0' && c <= '9')) return Number(s, ref i);
            throw new FormatException("Unexpected '" + c + "' at " + i);
        }

        static bool Lit(string s, ref int i, string lit)
        {
            if (string.CompareOrdinal(s, i, lit, 0, lit.Length) != 0) return false;
            i += lit.Length;
            return true;
        }

        static JObj Object(string s, ref int i)
        {
            var o = new JObj();
            i++;
            Ws(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return o; }
            while (true)
            {
                Ws(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("Expected key at " + i);
                string k = String(s, ref i);
                Ws(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':' at " + i);
                i++;
                o.Items.Add(new KeyValuePair<string, object>(k, Value(s, ref i)));
                Ws(s, ref i);
                if (i >= s.Length) throw new FormatException("Unclosed object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return o; }
                throw new FormatException("Expected ',' or '}' at " + i);
            }
        }

        static List<object> Array(string s, ref int i)
        {
            var a = new List<object>();
            i++;
            Ws(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Add(Value(s, ref i));
                Ws(s, ref i);
                if (i >= s.Length) throw new FormatException("Unclosed array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return a; }
                throw new FormatException("Expected ',' or ']' at " + i);
            }
        }

        static string String(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("Bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("Bad escape \\" + e);
                }
            }
            throw new FormatException("Unclosed string");
        }

        static JNum Number(string s, ref int i)
        {
            int start = i;
            if (s[i] == '-') i++;
            while (i < s.Length && "0123456789.eE+-".IndexOf(s[i]) >= 0) i++;
            string raw = s.Substring(start, i - start);
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                throw new FormatException("Bad number " + raw);
            return new JNum(raw);
        }

        // Two-space indent, the same shape Claude Code writes.
        public static string Write(object v)
        {
            var sb = new StringBuilder();
            Emit(sb, v, 0);
            return sb.ToString();
        }

        static void Emit(StringBuilder sb, object v, int depth)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case JNum n: sb.Append(n.Raw); break;
                case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case long n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case string str: Quote(sb, str); break;
                case JObj o:
                    if (o.Items.Count == 0) { sb.Append("{}"); break; }
                    sb.Append("{\n");
                    for (int k = 0; k < o.Items.Count; k++)
                    {
                        sb.Append(' ', (depth + 1) * 2);
                        Quote(sb, o.Items[k].Key);
                        sb.Append(": ");
                        Emit(sb, o.Items[k].Value, depth + 1);
                        if (k < o.Items.Count - 1) sb.Append(',');
                        sb.Append('\n');
                    }
                    sb.Append(' ', depth * 2).Append('}');
                    break;
                case List<object> a:
                    if (a.Count == 0) { sb.Append("[]"); break; }
                    sb.Append("[\n");
                    for (int k = 0; k < a.Count; k++)
                    {
                        sb.Append(' ', (depth + 1) * 2);
                        Emit(sb, a[k], depth + 1);
                        if (k < a.Count - 1) sb.Append(',');
                        sb.Append('\n');
                    }
                    sb.Append(' ', depth * 2).Append(']');
                    break;
                default: throw new ArgumentException("Cannot serialize " + v.GetType().Name);
            }
        }

        public static string Quote(string str)
        {
            var sb = new StringBuilder();
            Quote(sb, str);
            return sb.ToString();
        }

        static void Quote(StringBuilder sb, string str)
        {
            sb.Append('"');
            foreach (char c in str)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
