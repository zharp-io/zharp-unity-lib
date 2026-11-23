using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ZharpCore
{
    /// <summary>
    /// The little JSON Zharp needs (OTLP bodies, the settings file, the run marker), without a
    /// dependency: reflection-based serializers are what IL2CPP's linker strips.
    /// Objects read as <c>Dictionary&lt;string, object&gt;</c>, arrays as <c>List&lt;object&gt;</c>,
    /// numbers as <c>double</c>.
    /// </summary>
    internal static class Json
    {
        public static string Write(object value)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, value);
            return sb.ToString();
        }

        public static void WriteValue(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case float f: WriteDouble(sb, f); return;
                case double d: WriteDouble(sb, d); return;
                case RawJson raw: sb.Append(raw.Text); return;
                case IDictionary<string, object> map:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (var pair in map)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, pair.Key);
                        sb.Append(':');
                        WriteValue(sb, pair.Value);
                    }
                    sb.Append('}');
                    return;
                }
                case IEnumerable list:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (object item in list)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteValue(sb, item);
                    }
                    sb.Append(']');
                    return;
                }
                default:
                    if (value is IConvertible c && value.GetType().IsPrimitive)
                    {
                        WriteDouble(sb, c.ToDouble(CultureInfo.InvariantCulture));
                        return;
                    }
                    WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                    return;
            }
        }

        static void WriteDouble(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { WriteString(sb, d.ToString(CultureInfo.InvariantCulture)); return; }
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        public static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>Parsed JSON, or null when the text isn't JSON.</summary>
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                int at = 0;
                // A byte order mark, as editors on Windows write.
                if (text[0] == '﻿') at = 1;
                object value = ReadValue(text, ref at);
                SkipSpace(text, ref at);
                return at == text.Length ? value : null;
            }
            catch (FormatException) { return null; }
            catch (ArgumentOutOfRangeException) { return null; }
            catch (IndexOutOfRangeException) { return null; }
        }

        public static Dictionary<string, object> ParseObject(string text) => Parse(text) as Dictionary<string, object>;

        static object ReadValue(string s, ref int i)
        {
            SkipSpace(s, ref i);
            char c = s[i];
            if (c == '{')
            {
                var map = new Dictionary<string, object>();
                i++;
                SkipSpace(s, ref i);
                if (s[i] == '}') { i++; return map; }
                while (true)
                {
                    SkipSpace(s, ref i);
                    string key = ReadString(s, ref i);
                    SkipSpace(s, ref i);
                    if (s[i] != ':') throw new FormatException();
                    i++;
                    map[key] = ReadValue(s, ref i);
                    SkipSpace(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return map; }
                    throw new FormatException();
                }
            }
            if (c == '[')
            {
                var list = new List<object>();
                i++;
                SkipSpace(s, ref i);
                if (s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(ReadValue(s, ref i));
                    SkipSpace(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return list; }
                    throw new FormatException();
                }
            }
            if (c == '"') return ReadString(s, ref i);
            if (Starts(s, i, "true")) { i += 4; return true; }
            if (Starts(s, i, "false")) { i += 5; return false; }
            if (Starts(s, i, "null")) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (start == i) throw new FormatException();
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string ReadString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException();
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
        }

        static bool Starts(string s, int i, string word) => string.CompareOrdinal(s, i, word, 0, word.Length) == 0;

        static void SkipSpace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        public static string Text(IDictionary<string, object> map, string key, string fallback = "")
        {
            if (map == null || !map.TryGetValue(key, out object v) || v == null) return fallback;
            return v is string s ? s : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static bool Bool(IDictionary<string, object> map, string key, bool fallback)
        {
            if (map == null || !map.TryGetValue(key, out object v) || v == null) return fallback;
            if (v is bool b) return b;
            if (v is string s) return Switch(s) ?? fallback;
            return fallback;
        }

        public static double Number(IDictionary<string, object> map, string key, double fallback)
        {
            if (map == null || !map.TryGetValue(key, out object v) || v == null) return fallback;
            if (v is double d) return d;
            return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : fallback;
        }

        /// <summary>"1", "true", "yes", "on" or their opposites; null for anything else.</summary>
        public static bool? Switch(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "1": case "true": case "yes": case "on": return true;
                case "0": case "false": case "no": case "off": return false;
                default: return null;
            }
        }
    }

    /// <summary>Text already in JSON, written as it is.</summary>
    internal sealed class RawJson
    {
        public readonly string Text;
        public RawJson(string text) { Text = text; }
    }
}
