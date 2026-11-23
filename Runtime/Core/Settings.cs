using System;
using System.Collections.Generic;
using System.Text;

namespace ZharpCore
{
    /// <summary>
    /// What Zharp runs with: <c>Assets/Zharp/Resources/ZharpSettings.json</c> (Project Settings ›
    /// Zharp), then environment variables on top (desktop players and the Editor).
    /// </summary>
    internal sealed class Settings
    {
        public const string DefaultEndpoint = "https://api.zharp.io/api/v1/otlp";

        public string Key = "";
        public string Endpoint = "";
        public string Service = "";
        public string Release = "";
        public string Environment = "";
        public string ApplicationId = "";
        public bool Enabled = true;
        public bool AutoStart = true;
        public bool Logs;
        public bool LogErrors;
        public bool Tracing = true;
        public double TracesSampleRate = 1.0;
        public bool DetectHangs = true;
        public double HangThreshold = 5.0;
        public bool NativeCrashes = true;
        public bool Il2CppLineNumbers = true;
        public bool UploadSources = true;
        public bool UploadSymbols = true;
        public bool Debug;

        /// <summary>The settings file's text over the defaults.</summary>
        public static Settings FromJson(string json)
        {
            var s = new Settings();
            Dictionary<string, object> map = Json.ParseObject(json);
            if (map == null) return s;
            s.Key = Json.Text(map, "key").Trim();
            s.Endpoint = Json.Text(map, "endpoint").Trim();
            s.Service = Json.Text(map, "service").Trim();
            s.Release = Json.Text(map, "release").Trim();
            s.Environment = Json.Text(map, "environment").Trim();
            s.ApplicationId = Json.Text(map, "applicationId").Trim();
            s.Enabled = Json.Bool(map, "enabled", true);
            s.AutoStart = Json.Bool(map, "autoStart", true);
            s.Logs = Json.Bool(map, "logs", false);
            s.LogErrors = Json.Bool(map, "logErrors", false);
            s.Tracing = Json.Bool(map, "tracing", true);
            s.TracesSampleRate = Math.Max(0, Math.Min(1, Json.Number(map, "tracesSampleRate", 1.0)));
            s.DetectHangs = Json.Bool(map, "detectHangs", true);
            s.HangThreshold = Math.Max(1, Json.Number(map, "hangThreshold", 5.0));
            s.NativeCrashes = Json.Bool(map, "nativeCrashes", true);
            s.Il2CppLineNumbers = Json.Bool(map, "il2cppLineNumbers", true);
            s.UploadSources = Json.Bool(map, "uploadSources", true);
            s.UploadSymbols = Json.Bool(map, "uploadSymbols", true);
            s.Debug = Json.Bool(map, "debug", false);
            return s;
        }

        /// <summary>The settings file's text. Keys the file had that Zharp doesn't know are kept.</summary>
        public string ToJson(string previous = null)
        {
            Dictionary<string, object> map = Json.ParseObject(previous) ?? new Dictionary<string, object>();
            map["key"] = Key;
            map["environment"] = Environment;
            map["release"] = Release;
            map["service"] = Service;
            map["applicationId"] = ApplicationId;
            map["enabled"] = Enabled;
            map["autoStart"] = AutoStart;
            map["logs"] = Logs;
            map["logErrors"] = LogErrors;
            map["tracing"] = Tracing;
            map["tracesSampleRate"] = TracesSampleRate;
            map["detectHangs"] = DetectHangs;
            map["hangThreshold"] = HangThreshold;
            map["nativeCrashes"] = NativeCrashes;
            map["il2cppLineNumbers"] = Il2CppLineNumbers;
            map["uploadSources"] = UploadSources;
            map["uploadSymbols"] = UploadSymbols;
            map["debug"] = Debug;
            map["endpoint"] = Endpoint;
            return Pretty(map);
        }

        /// <summary>Environment variables over the file (ZHARP_KEY, ZHARP_LOGS…).</summary>
        public void ApplyEnvironment(Func<string, string> env)
        {
            string Text(string name, string current)
            {
                string v = (env(name) ?? "").Trim();
                return v.Length > 0 ? v : current;
            }
            bool Flag(string name, bool current) => Json.Switch(env(name)) ?? current;

            Key = Text("ZHARP_KEY", Key);
            Endpoint = Text("ZHARP_ENDPOINT", Endpoint);
            Service = Text("ZHARP_SERVICE", Service);
            Release = Text("ZHARP_RELEASE", Release);
            Environment = Text("ZHARP_ENVIRONMENT", Environment);
            ApplicationId = Text("ZHARP_APP_ID", ApplicationId);
            Enabled = Flag("ZHARP_ENABLED", Enabled);
            Logs = Flag("ZHARP_LOGS", Logs);
            Tracing = Flag("ZHARP_TRACING", Tracing);
            DetectHangs = Flag("ZHARP_DETECT_HANGS", DetectHangs);
            Debug = Flag("ZHARP_DEBUG", Debug);
        }

        /// <summary>The defaults for what's still empty: the endpoint, the service from the product name, the release.</summary>
        public void ApplyDefaults(string productName, string version, bool inEditor)
        {
            Key = Key.Trim();
            Endpoint = Endpoint.Trim().TrimEnd('/');
            if (Endpoint.Length == 0) Endpoint = DefaultEndpoint;
            if (Service.Length == 0) Service = ServiceNameFor(productName);
            if (Service.Length == 0) Service = "unity-game";
            if (Release.Length == 0) Release = version ?? "";
            if (Environment.Length == 0 && inEditor) Environment = "development";
        }

        /// <summary>
        /// The service a game named "Shop Quest" reports as: "shop-quest", the name Zharp gives an app
        /// created as "Shop Quest", so the game finds the app made for it.
        /// </summary>
        public static string ServiceNameFor(string name)
        {
            var sb = new StringBuilder();
            foreach (char raw in (name ?? "").Trim().ToLowerInvariant())
            {
                char c = raw;
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '-') sb.Append(c);
                else if (c == ' ' || c == '_' || c == '\t') sb.Append('-');
            }
            string s = sb.ToString();
            while (s.Contains("--")) s = s.Replace("--", "-");
            return s.Trim('-');
        }

        /// <summary>The OTLP endpoint's sibling for minidumps: …/api/v1/otlp → …/api/v1/minidump.</summary>
        public string MinidumpUrl()
        {
            string e = Endpoint.TrimEnd('/');
            int at = e.LastIndexOf("/otlp", StringComparison.Ordinal);
            string root = at >= 0 && at == e.Length - 5 ? e.Substring(0, at) : e;
            return root + "/minidump?key=" + Uri.EscapeDataString(Key);
        }

        /// <summary>JSON written for people: two-space indents, one key per line.</summary>
        public static string Pretty(IDictionary<string, object> map)
        {
            var sb = new StringBuilder("{\n");
            int i = 0;
            foreach (var pair in map)
            {
                sb.Append("  ");
                Json.WriteString(sb, pair.Key);
                sb.Append(": ");
                Json.WriteValue(sb, pair.Value);
                if (++i < map.Count) sb.Append(',');
                sb.Append('\n');
            }
            return sb.Append("}\n").ToString();
        }
    }
}
