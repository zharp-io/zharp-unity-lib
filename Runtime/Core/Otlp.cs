using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZharpCore
{
    /// <summary>
    /// OTLP/HTTP JSON: the bodies <c>POST {endpoint}/v1/logs</c> and <c>/v1/traces</c> accept. IDs are
    /// hex strings, 64-bit integers (timestamps) decimal strings, enums numbers. Errors are log
    /// records with <c>exception.*</c> attributes, as in every Zharp SDK.
    /// </summary>
    internal static class Otlp
    {
        public const string Version = ZharpInfo.Version;

        /// <summary>Values longer than this are cut; the server caps attributes at 16 KB anyway.</summary>
        public const int MaxAttribute = 16 * 1024;

        public const int KindInternal = 1;
        public const int KindClient = 3;
        public const int StatusUnset = 0;
        public const int StatusError = 2;

        public static int SeverityNumber(string level)
        {
            switch (level)
            {
                case "trace": return 1;
                case "debug": return 5;
                case "warn": return 13;
                case "error": return 17;
                case "fatal": return 21;
                default: return 9;
            }
        }

        public static string SeverityText(string level)
        {
            switch (level)
            {
                case "trace": case "debug": case "warn": case "error": case "fatal": return level.ToUpperInvariant();
                default: return "INFO";
            }
        }

        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static double UnixSeconds(DateTime utc) => (utc - Epoch).TotalSeconds;

        public static double Now() => UnixSeconds(DateTime.UtcNow);

        /// <summary>Nanoseconds since the epoch as OTLP/JSON writes them.</summary>
        public static string Nanos(double unixSeconds)
        {
            long micros = (long)(unixSeconds * 1_000_000.0);
            return micros <= 0 ? "0" : micros.ToString(CultureInfo.InvariantCulture) + "000";
        }

        /// <summary>Attributes OTLP can carry: nulls and empty keys dropped, long text cut.</summary>
        public static Dictionary<string, object> Clean(IDictionary<string, object> attributes)
        {
            var output = new Dictionary<string, object>();
            if (attributes == null) return output;
            foreach (var pair in attributes)
            {
                if (string.IsNullOrEmpty(pair.Key) || pair.Value == null) continue;
                switch (pair.Value)
                {
                    case string s: output[pair.Key] = Cut(s); break;
                    case bool _: case int _: case long _: output[pair.Key] = pair.Value; break;
                    case float f: output[pair.Key] = (double)f; break;
                    case double d: output[pair.Key] = d; break;
                    default: output[pair.Key] = Cut(Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? ""); break;
                }
            }
            return output;
        }

        static string Cut(string s) => s.Length > MaxAttribute ? s.Substring(0, MaxAttribute) : s;

        public static Dictionary<string, object> AnyValue(object value)
        {
            switch (value)
            {
                case bool b: return new Dictionary<string, object> { ["boolValue"] = b };
                case int i: return new Dictionary<string, object> { ["intValue"] = i.ToString(CultureInfo.InvariantCulture) };
                case long l: return new Dictionary<string, object> { ["intValue"] = l.ToString(CultureInfo.InvariantCulture) };
                case double d when !double.IsNaN(d) && !double.IsInfinity(d): return new Dictionary<string, object> { ["doubleValue"] = d };
                case null: return new Dictionary<string, object> { ["stringValue"] = "" };
                default: return new Dictionary<string, object> { ["stringValue"] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" };
            }
        }

        public static List<object> KeyValues(IDictionary<string, object> attributes)
        {
            var list = new List<object>();
            if (attributes == null) return list;
            foreach (var pair in attributes)
                list.Add(new Dictionary<string, object> { ["key"] = pair.Key, ["value"] = AnyValue(pair.Value) });
            return list;
        }

        /// <summary>A log record; errors are log records with exception.* attributes.</summary>
        public static Dictionary<string, object> LogRecord(string level, string body, IDictionary<string, object> attributes, double time = 0, string traceId = null, string spanId = null)
        {
            string at = Nanos(time > 0 ? time : Now());
            var record = new Dictionary<string, object>
            {
                ["timeUnixNano"] = at,
                ["observedTimeUnixNano"] = at,
                ["severityNumber"] = SeverityNumber(level),
                ["severityText"] = SeverityText(level),
                ["body"] = new Dictionary<string, object> { ["stringValue"] = body ?? "" },
                ["attributes"] = KeyValues(Clean(attributes)),
            };
            if (!string.IsNullOrEmpty(traceId))
            {
                record["traceId"] = traceId;
                record["spanId"] = spanId ?? "";
            }
            return record;
        }

        static Dictionary<string, object> Scope() => new Dictionary<string, object> { ["name"] = "zharp", ["version"] = Version };

        public static Dictionary<string, object> LogsRequest(IDictionary<string, object> resource, IList<object> records)
        {
            return new Dictionary<string, object>
            {
                ["resourceLogs"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["resource"] = new Dictionary<string, object> { ["attributes"] = KeyValues(Clean(resource)) },
                        ["scopeLogs"] = new List<object> { new Dictionary<string, object> { ["scope"] = Scope(), ["logRecords"] = records } },
                    },
                },
            };
        }

        public static Dictionary<string, object> TracesRequest(IDictionary<string, object> resource, IList<object> spans)
        {
            return new Dictionary<string, object>
            {
                ["resourceSpans"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["resource"] = new Dictionary<string, object> { ["attributes"] = KeyValues(Clean(resource)) },
                        ["scopeSpans"] = new List<object> { new Dictionary<string, object> { ["scope"] = Scope(), ["spans"] = spans } },
                    },
                },
            };
        }
    }

    internal static class ZharpInfo
    {
        public const string Version = "0.1.0";
    }

    /// <summary>Random IDs: 32 hex characters for a trace, session or device, 16 for a span.</summary>
    internal static class Ids
    {
        public static string Hex32() => Guid.NewGuid().ToString("N");

        public static string Hex16() => Guid.NewGuid().ToString("N").Substring(0, 16);
    }
}
