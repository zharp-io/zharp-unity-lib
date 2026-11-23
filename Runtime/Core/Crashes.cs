using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ZharpCore
{
    /// <summary>
    /// The run marker: written while the game runs (when it started, when last seen, its release,
    /// whether it's in the background, and the session, scene and player at that moment), deleted
    /// when it quits. Found on the next launch, it means the previous run didn't quit.
    /// </summary>
    internal sealed class RunMarker
    {
        public double Started;
        public double Seen;
        public string Release = "";
        public bool Editor;
        public bool Background;
        public Dictionary<string, object> Context = new Dictionary<string, object>();

        public string ToJson() => Json.Write(new Dictionary<string, object>
        {
            ["started"] = Started,
            ["seen"] = Seen,
            ["release"] = Release,
            ["editor"] = Editor,
            ["background"] = Background,
            ["context"] = Context,
        });

        public static RunMarker FromJson(string text)
        {
            Dictionary<string, object> map = Json.ParseObject(text);
            if (map == null) return null;
            var m = new RunMarker
            {
                Started = Json.Number(map, "started", 0),
                Seen = Json.Number(map, "seen", 0),
                Release = Json.Text(map, "release"),
                Editor = Json.Bool(map, "editor", false),
                Background = Json.Bool(map, "background", false),
            };
            if (map.TryGetValue("context", out object c) && c is Dictionary<string, object> context) m.Context = context;
            return m;
        }

        /// <summary>
        /// Whether a run that left this marker behind is reported: not when it was in the background
        /// (the OS stops games there), nor when run in the Editor (stopping play mode, or the Editor
        /// itself, isn't a player crash).
        /// </summary>
        public bool WorthReporting => !Background && !Editor;
    }

    /// <summary>
    /// Windows: Unity's crash handler writes a folder per crash —
    /// <c>%TEMP%\{Company}\{Product}\Crashes\Crash_2026-11-23_101500123\</c> with <c>crash.dmp</c>,
    /// <c>error.log</c> and <c>Player.log</c>. The newest one from the previous run is uploaded to
    /// <c>/api/v1/minidump</c> on the next launch, as Crashpad would send it; a <c>.zharp-sent</c> file
    /// in it marks it done.
    /// </summary>
    internal static class CrashFolders
    {
        public const string SentMarker = ".zharp-sent";
        const long MaxDumpBytes = 50L * 1024 * 1024;

        /// <summary>Where Unity's handler writes on Windows.</summary>
        public static string RootFor(string tempDir, string company, string product) => Path.Combine(tempDir, company ?? "", product ?? "", "Crashes");

        /// <summary>The newest crash folder with a dump, written since <paramref name="since"/> (UTC), not sent yet.</summary>
        public static string Newest(string root, DateTime since)
        {
            try
            {
                if (!Directory.Exists(root)) return null;
                return Directory.GetDirectories(root)
                    .Where(d => File.Exists(Path.Combine(d, "crash.dmp")) && !File.Exists(Path.Combine(d, SentMarker)))
                    .Select(d => new { Dir = d, At = File.GetLastWriteTimeUtc(Path.Combine(d, "crash.dmp")) })
                    .Where(x => x.At >= since.AddSeconds(-5))
                    .OrderByDescending(x => x.At)
                    .Select(x => x.Dir)
                    .FirstOrDefault();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The upload's fields, as Breakpad senders name them for Zharp: the previous run's release,
        /// session, device and player, plus what Unity wrote about the crash.
        /// </summary>
        public static Dictionary<string, string> Annotations(RunMarker marker, IDictionary<string, object> resource, string folder)
        {
            var a = new Dictionary<string, string>();
            void Put(string key, object value)
            {
                string text = value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(text)) a[key] = text;
            }
            object Ctx(string key) => marker != null && marker.Context.TryGetValue(key, out object v) ? v : null;
            object Res(string key) => resource != null && resource.TryGetValue(key, out object v) ? v : null;

            Put("zharp.service", Res("service.name"));
            Put("zharp.release", marker != null && marker.Release.Length > 0 ? marker.Release : Res("service.version"));
            Put("zharp.environment", Res("deployment.environment.name"));
            Put("zharp.session_id", Ctx("session.id"));
            Put("zharp.device_id", Res("device.id"));
            Put("zharp.user.id", Ctx("enduser.id"));
            Put("zharp.user.email", Ctx("enduser.email"));
            Put("zharp.user.name", Ctx("enduser.name"));
            Put("screen.name", Ctx("screen.name"));
            Put("zharp.build_id", Ctx("zharp.build_id"));
            Put("zharp.mechanism", "unity-crash-handler");
            Put("telemetry.sdk.name", "zharp");
            Put("telemetry.sdk.language", "unity");
            Put("telemetry.sdk.version", ZharpInfo.Version);
            foreach (string key in new[] { "unity.version", "unity.scripting_backend", "os.name", "os.version", "device.model.identifier", "host.arch" })
                Put(key, Res(key));
            string error = Tail(Path.Combine(folder ?? "", "error.log"), 4000);
            if (error.Length > 0) a["unity.error_log"] = error;
            string log = Tail(PlayerLog(folder), 8000);
            if (log.Length > 0) a["unity.player_log"] = log;
            return a;
        }

        /// <summary>
        /// The player's log in a crash folder: Player.log, or under the name -logFile gave it (Unity 6.x
        /// copies it as it was named, and may write no error.log). Its end holds the crash's stack.
        /// </summary>
        static string PlayerLog(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return "";
                string player = Path.Combine(folder, "Player.log");
                if (File.Exists(player)) return player;
                return Directory.GetFiles(folder, "*.log")
                    .Where(f => !string.Equals(Path.GetFileName(f), "error.log", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .FirstOrDefault() ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        public static byte[] ReadDump(string folder)
        {
            string path = Path.Combine(folder, "crash.dmp");
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxDumpBytes) return null;
            return File.ReadAllBytes(path);
        }

        public static void MarkSent(string folder)
        {
            try { File.WriteAllText(Path.Combine(folder, SentMarker), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)); }
            catch (Exception) { }
        }

        /// <summary>The end of a text file, at most <paramref name="chars"/> characters.</summary>
        public static string Tail(string path, int chars)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long start = Math.Max(0, stream.Length - chars * 2);
                    stream.Seek(start, SeekOrigin.Begin);
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string text = reader.ReadToEnd();
                        return text.Length > chars ? text.Substring(text.Length - chars) : text;
                    }
                }
            }
            catch (Exception)
            {
                return "";
            }
        }
    }

    /// <summary>A multipart/form-data body, as Breakpad senders post a minidump.</summary>
    internal static class Multipart
    {
        public static byte[] Build(string boundary, IDictionary<string, string> fields, string fileField, string fileName, byte[] file)
        {
            using (var body = new MemoryStream())
            {
                void Text(string s)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(s);
                    body.Write(bytes, 0, bytes.Length);
                }
                foreach (var pair in fields)
                {
                    Text("--" + boundary + "\r\n");
                    Text("Content-Disposition: form-data; name=\"" + pair.Key.Replace("\"", "") + "\"\r\n\r\n");
                    Text(pair.Value + "\r\n");
                }
                Text("--" + boundary + "\r\n");
                Text("Content-Disposition: form-data; name=\"" + fileField + "\"; filename=\"" + fileName + "\"\r\n");
                Text("Content-Type: application/octet-stream\r\n\r\n");
                body.Write(file, 0, file.Length);
                Text("\r\n--" + boundary + "--\r\n");
                return body.ToArray();
            }
        }
    }

    /// <summary>Gzip, when the platform has it (WebGL's runtime doesn't): null otherwise.</summary>
    internal static class Gzip
    {
        static bool unavailable;

        public static byte[] TryCompress(byte[] data)
        {
            if (unavailable) return null;
            try
            {
                using (var output = new MemoryStream())
                {
                    using (var gzip = new GZipStream(output, CompressionMode.Compress, true)) gzip.Write(data, 0, data.Length);
                    return output.ToArray();
                }
            }
            catch (Exception)
            {
                unavailable = true;
                return null;
            }
        }
    }
}
