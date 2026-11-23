using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ZharpCore
{
    /// <summary>
    /// A frame of a Unity stack: "Shop.Cart.Total () (at Assets/Scripts/Cart.cs:12)"; an IL2CPP
    /// player writes a log's call stack without the space ("Shop.Cart:Total() (at …)").
    /// </summary>
    internal struct Frame
    {
        public string Text;
        public string Function;
        public string File;
        public int Line;
    }

    /// <summary>An error as Zharp files it: exception.type, exception.message, exception.stacktrace.</summary>
    internal sealed class ErrorDescription
    {
        public string Type = "Exception";
        public string Message = "";
        public List<Frame> Frames = new List<Frame>();

        /// <summary>"Type: first line of the message", then Unity's frames as Unity wrote them.</summary>
        public string Stacktrace
        {
            get
            {
                var sb = new StringBuilder(Type);
                string first = FirstLine(Message);
                if (first.Length > 0) sb.Append(": ").Append(first);
                foreach (Frame f in Frames) sb.Append('\n').Append(f.Text);
                return sb.ToString();
            }
        }

        public string Title
        {
            get
            {
                string first = FirstLine(Message);
                return first.Length > 0 ? Type + ": " + first : Type;
            }
        }

        public static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int nl = text.IndexOf('\n');
            return (nl >= 0 ? text.Substring(0, nl) : text).TrimEnd('\r');
        }
    }

    /// <summary>Unity's stack strings, as <c>Application.logMessageReceivedThreaded</c> gives them.</summary>
    internal static class Stacks
    {
        static readonly Regex Located = new Regex(@"^(\S+?) ?\(([^()]*)\) \(at (.+):(-?\d+)\)$", RegexOptions.CultureInvariant);
        static readonly Regex Bare = new Regex(@"^([\w`<>$|+\[\],.]+?[.:][\w`<>$|+\[\],]+) ?\(([^()]*)\)$", RegexOptions.CultureInvariant);
        static readonly Regex Mono = new Regex(@"^at\s+(\S+) \((.*?)\)(?: \[0x[0-9a-f]+\])?(?: in (.+?):(\d+))?\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        /// <summary>"NullReferenceException: …", "Shop.CartException: …", "UnityEngine.MissingReferenceException: …".</summary>
        static readonly Regex Head = new Regex(@"^([A-Za-z_][\w.`+]*)(?::\s?(.*))?$", RegexOptions.CultureInvariant | RegexOptions.Singleline);

        /// <summary>The frames of a stack string, innermost first; lines that aren't frames are left out.</summary>
        public static List<Frame> Parse(string stack)
        {
            var frames = new List<Frame>();
            if (string.IsNullOrEmpty(stack)) return frames;
            foreach (string raw in stack.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                Match m = Located.Match(line);
                if (m.Success)
                {
                    bool unknown = m.Groups[3].Value.StartsWith("<", StringComparison.Ordinal);
                    int.TryParse(m.Groups[4].Value, out int n);
                    frames.Add(new Frame { Text = line, Function = m.Groups[1].Value, File = unknown ? "" : m.Groups[3].Value, Line = unknown ? 0 : n });
                    continue;
                }
                m = Mono.Match(line);
                if (m.Success)
                {
                    string file = m.Groups[3].Success ? m.Groups[3].Value : "";
                    bool unknown = file.StartsWith("<", StringComparison.Ordinal);
                    int.TryParse(m.Groups[4].Value, out int n);
                    frames.Add(new Frame { Text = line, Function = m.Groups[1].Value, File = unknown ? "" : file, Line = unknown ? 0 : n });
                    continue;
                }
                m = Bare.Match(line);
                if (m.Success) frames.Add(new Frame { Text = line, Function = m.Groups[1].Value, File = "", Line = 0 });
            }
            return frames;
        }

        /// <summary>
        /// An exception Unity logged: its condition ("NullReferenceException: Object reference not
        /// set…") and stack. With an inner exception, Unity logs the innermost first and adds the
        /// outer ones as "Rethrow as X: …" lines: the condition names the one that was thrown first.
        /// </summary>
        public static ErrorDescription FromLog(string condition, string stack, string fallbackType = "Exception")
        {
            var d = new ErrorDescription { Frames = Parse(stack) };
            string text = (condition ?? "").Trim();
            Match m = Head.Match(text);
            if (m.Success && LooksLikeType(m.Groups[1].Value))
            {
                d.Type = m.Groups[1].Value;
                d.Message = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";
            }
            else
            {
                d.Type = fallbackType;
                d.Message = text;
            }
            return d;
        }

        /// <summary>"Exception", "NullReferenceException", "Shop.SaveError": a type, not a sentence's first word.</summary>
        static bool LooksLikeType(string word)
        {
            return word.EndsWith("Exception", StringComparison.Ordinal) || (word.EndsWith("Error", StringComparison.Ordinal) && word != "Error");
        }

        /// <summary>
        /// A stack without Zharp's own frames on top (where a handled error was reported from), nor
        /// the frames of Unity taking the stack, which a player's StackTraceUtility.ExtractStackTrace() keeps.
        /// </summary>
        public static List<Frame> WithoutOwnFrames(List<Frame> frames)
        {
            int skip = 0;
            while (skip < frames.Count && (IsOwn(frames[skip]) || IsStackCapture(frames[skip]))) skip++;
            return skip == 0 ? frames : frames.GetRange(skip, frames.Count - skip);
        }

        static bool IsStackCapture(Frame frame)
        {
            string f = frame.Function ?? "";
            return f.StartsWith("UnityEngine.StackTraceUtility", StringComparison.Ordinal)
                || f.StartsWith("UnityEngine.Debug:ExtractStackTrace", StringComparison.Ordinal)
                || f.StartsWith("UnityEngine.Debug.ExtractStackTrace", StringComparison.Ordinal);
        }

        public static bool IsOwn(Frame frame)
        {
            string f = frame.Function ?? "";
            return f.StartsWith("Zharp:", StringComparison.Ordinal) || f.StartsWith("Zharp.", StringComparison.Ordinal)
                || f.StartsWith("ZharpCore.", StringComparison.Ordinal) || f.StartsWith("ZharpUnity.", StringComparison.Ordinal)
                || f.StartsWith("ZharpSpan", StringComparison.Ordinal) || f.StartsWith("ZharpWebRequest", StringComparison.Ordinal)
                || (frame.File ?? "").Replace('\\', '/').Contains("/io.zharp.unity");
        }

        /// <summary>Whether an error came from Zharp itself (never reported, so a bug in Zharp can't loop).</summary>
        public static bool IsFromZharp(List<Frame> frames) => frames.Count > 0 && IsOwn(frames[0]) && !frames[0].Function.StartsWith("Zharp:Capture", StringComparison.Ordinal);

        /// <summary>The game's own code: a file under Assets/ (or no file, in a build without lines), not the engine's.</summary>
        public static bool IsGameFrame(Frame frame)
        {
            string f = frame.Function ?? "";
            if (f.StartsWith("UnityEngine.", StringComparison.Ordinal) || f.StartsWith("UnityEditor.", StringComparison.Ordinal)
                || f.StartsWith("Unity.", StringComparison.Ordinal) || f.StartsWith("System.", StringComparison.Ordinal)
                || f.StartsWith("Mono.", StringComparison.Ordinal) || IsOwn(frame)) return false;
            string file = (frame.File ?? "").Replace('\\', '/');
            return file.Length == 0 || file.StartsWith("Assets/", StringComparison.Ordinal) || file.Contains("/Assets/");
        }

        /// <summary>"Assets/Scripts/Cart.cs" from wherever the build machine kept the project.</summary>
        public static string ProjectPath(string file)
        {
            string f = (file ?? "").Replace('\\', '/');
            if (f.StartsWith("Assets/", StringComparison.Ordinal)) return f;
            int at = f.LastIndexOf("/Assets/", StringComparison.Ordinal);
            return at >= 0 ? f.Substring(at + 1) : f;
        }
    }

    /// <summary>
    /// The same error from the same place is sent at most once a minute: an exception in Update
    /// happens every frame, and Unity sometimes logs one exception twice (a <c>Debug.LogException</c>
    /// in a catch that rethrows, an unhandled exception both logged and raised). Thread-safe.
    /// </summary>
    internal sealed class Throttle
    {
        public const double RepeatSeconds = 60;
        const int MaxRemembered = 200;

        readonly Dictionary<string, double> recent = new Dictionary<string, double>();
        readonly object gate = new object();

        public static string KeyOf(ErrorDescription d) =>
            d.Type + "\n" + ErrorDescription.FirstLine(d.Message) + "\n" + (d.Frames.Count > 0 ? d.Frames[0].Text : "");

        /// <summary>Whether this error was sent less than a minute ago; if not, it's remembered as sent now.</summary>
        public bool IsRepeat(ErrorDescription d, double now)
        {
            string key = KeyOf(d);
            lock (gate)
            {
                if (recent.TryGetValue(key, out double last) && now - last < RepeatSeconds) return true;
                if (recent.Count >= MaxRemembered)
                {
                    var stale = new List<string>();
                    foreach (var pair in recent) if (now - pair.Value >= RepeatSeconds) stale.Add(pair.Key);
                    foreach (string k in stale) recent.Remove(k);
                    if (recent.Count >= MaxRemembered) return false;
                }
                recent[key] = now;
                return false;
            }
        }
    }

    /// <summary>The last warnings and errors logged, sent with the next exception. Thread-safe.</summary>
    internal sealed class Breadcrumbs
    {
        public const int Max = 30;
        readonly Queue<Dictionary<string, object>> items = new Queue<Dictionary<string, object>>();
        readonly object gate = new object();

        public void Add(string level, string message, DateTime utc)
        {
            string text = message ?? "";
            if (text.Length > 300) text = text.Substring(0, 300);
            lock (gate)
            {
                items.Enqueue(new Dictionary<string, object> { ["time"] = utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture), ["level"] = level, ["message"] = text });
                while (items.Count > Max) items.Dequeue();
            }
        }

        public string ToJson()
        {
            lock (gate) return items.Count == 0 ? "" : Json.Write(new List<object>(items));
        }
    }

    /// <summary>
    /// <c>zharp.source_context</c>: the code around the game's top frames, as
    /// [{file, line, pre, code, post}]. Built builds get theirs from the source bundle the build step
    /// uploads; this is for the Editor, where the scripts are on disk.
    /// </summary>
    internal static class SourceContext
    {
        const int MaxFrames = 5;
        const int Around = 5;
        const int MaxLine = 300;
        const int MaxBytes = 12000;

        public static string Of(List<Frame> frames, Func<string, string[]> linesOf)
        {
            var output = new List<object>();
            var seen = new HashSet<string>();
            foreach (Frame frame in frames)
            {
                if (output.Count >= MaxFrames) break;
                if (!Stacks.IsGameFrame(frame) || frame.Line < 1 || string.IsNullOrEmpty(frame.File)) continue;
                string file = Stacks.ProjectPath(frame.File);
                if (!seen.Add(file + ":" + frame.Line)) continue;
                string[] lines;
                try { lines = linesOf(file); }
                catch (Exception) { continue; }
                if (lines == null || frame.Line > lines.Length) continue;
                var pre = new List<object>();
                for (int n = Math.Max(1, frame.Line - Around); n < frame.Line; n++) pre.Add(Cut(lines[n - 1]));
                var post = new List<object>();
                for (int n = frame.Line + 1; n <= Math.Min(lines.Length, frame.Line + Around); n++) post.Add(Cut(lines[n - 1]));
                output.Add(new Dictionary<string, object> { ["file"] = file, ["line"] = frame.Line, ["pre"] = pre, ["code"] = Cut(lines[frame.Line - 1]), ["post"] = post });
            }
            while (output.Count > 0)
            {
                string text = Json.Write(output);
                if (Encoding.UTF8.GetByteCount(text) <= MaxBytes) return text;
                output.RemoveAt(output.Count - 1);
            }
            return "";
        }

        static string Cut(string text)
        {
            text = (text ?? "").TrimEnd('\r');
            return text.Length <= MaxLine ? text : text.Substring(0, MaxLine - 1) + "…";
        }
    }
}
