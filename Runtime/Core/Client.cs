using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ZharpCore
{
    /// <summary>A span as recorded: what <c>ZharpSpan</c> hands over when it ends.</summary>
    internal sealed class SpanRecord
    {
        public string TraceId = "";
        public string SpanId = "";
        public string ParentSpanId = "";
        public string Name = "";
        public int Kind = Otlp.KindInternal;
        public double Start;
        public double End;
        public int StatusCode;
        public string StatusMessage = "";
        public Dictionary<string, object> Attributes = new Dictionary<string, object>();
        public List<Dictionary<string, object>> Events = new List<Dictionary<string, object>>();

        public Dictionary<string, object> ToOtlp(IDictionary<string, object> context)
        {
            var all = new Dictionary<string, object>(context ?? new Dictionary<string, object>());
            foreach (var pair in Otlp.Clean(Attributes)) all[pair.Key] = pair.Value;
            var status = new Dictionary<string, object> { ["code"] = StatusCode };
            if (StatusMessage.Length > 0) status["message"] = StatusMessage;
            var span = new Dictionary<string, object>
            {
                ["traceId"] = TraceId,
                ["spanId"] = SpanId,
                ["name"] = Name,
                ["kind"] = Kind,
                ["startTimeUnixNano"] = Otlp.Nanos(Start),
                ["endTimeUnixNano"] = Otlp.Nanos(End),
                ["attributes"] = Otlp.KeyValues(all),
                ["status"] = status,
            };
            if (ParentSpanId.Length > 0) span["parentSpanId"] = ParentSpanId;
            if (Events.Count > 0) span["events"] = Events;
            return span;
        }
    }

    /// <summary>
    /// Zharp's engine-free part: context (session, device, scene, player, tags), errors, logs, spans,
    /// hangs, crashes from the previous run, and the outbox they all go through. The Unity side
    /// feeds it (log callback, scenes, lifecycle) and sends what's in the outbox.
    /// Everything here is safe from any thread, and never throws into the game.
    /// </summary>
    internal sealed class Client
    {
        public const int MaxBatch = 100;
        public const double FlushInterval = 5;

        public Settings Settings { get; }
        public Session Session { get; }
        public Outbox Outbox { get; }
        public Dictionary<string, object> Resource { get; }
        public Throttle Throttle { get; } = new Throttle();
        public Breadcrumbs Breadcrumbs { get; } = new Breadcrumbs();
        /// <summary>The build's GUID (<c>Application.buildGUID</c>), on every error as <c>zharp.build_id</c>.</summary>
        public string BuildId { get; set; } = "";
        /// <summary>Reads a project file's lines for source context (the Editor); null elsewhere.</summary>
        public Func<string, string[]> SourceLines { get; set; }
        public bool InEditor { get; set; }
        public Action<string> DebugLog { get; set; } = _ => { };

        readonly string storage;
        readonly Func<double> clock;
        readonly object gate = new object();
        readonly List<object> logs = new List<object>();
        readonly List<object> spans = new List<object>();
        readonly Dictionary<string, object> user = new Dictionary<string, object>();
        readonly Dictionary<string, object> tags = new Dictionary<string, object>();
        readonly Random sampler = new Random();
        double flushAt;
        volatile string screen = "";
        volatile bool background;
        double startedAt;

        public Client(Settings settings, string storage, IDictionary<string, object> resource, Func<double> clock = null)
        {
            Settings = settings;
            this.storage = storage;
            this.clock = clock ?? Otlp.Now;
            Session = new Session(storage, this.clock);
            Outbox = new Outbox(Path.Combine(storage, "outbox"));
            Resource = new Dictionary<string, object>(resource ?? new Dictionary<string, object>())
            {
                ["service.name"] = settings.Service,
                ["telemetry.sdk.name"] = "zharp",
                ["telemetry.sdk.language"] = "unity",
                ["telemetry.sdk.version"] = ZharpInfo.Version,
                ["device.id"] = Session.DeviceId,
            };
            if (settings.Release.Length > 0) Resource["service.version"] = settings.Release;
            if (settings.Environment.Length > 0) Resource["deployment.environment.name"] = settings.Environment;
            Session.OnNewSession = _ => WriteMarker();
            startedAt = this.clock();
        }

        public double Now() => clock();

        public string Screen => screen;

        // ─── Context ─────────────────────────────────────────────────────────────

        /// <summary>session.id, screen.name, enduser.* and the tags, for something made now.</summary>
        public Dictionary<string, object> Context()
        {
            Dictionary<string, object> output;
            lock (gate)
            {
                output = new Dictionary<string, object>(tags);
                foreach (var pair in user) output[pair.Key] = pair.Value;
            }
            output["session.id"] = Session.Current();
            string s = screen;
            if (s.Length > 0) output["screen.name"] = s;
            if (BuildId.Length > 0) output["zharp.build_id"] = BuildId;
            return output;
        }

        public void SetUser(string id, string email, string name)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                ClearUser();
                return;
            }
            lock (gate)
            {
                user.Clear();
                user["enduser.id"] = id.Trim();
                if (!string.IsNullOrEmpty(email)) user["enduser.email"] = email;
                if (!string.IsNullOrEmpty(name)) user["enduser.name"] = name;
            }
            WriteMarker();
        }

        /// <summary>Forget the player (sign-out), and start a new session for whoever comes next.</summary>
        public void ClearUser()
        {
            lock (gate) user.Clear();
            Session.Reset();
            WriteMarker();
        }

        public void SetTag(string key, object value)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (gate)
            {
                if (value == null) tags.Remove(key);
                else tags[key] = value;
            }
        }

        /// <summary>A new screen: true when it changed (the caller records its span).</summary>
        public bool ScreenShown(string name)
        {
            if (string.IsNullOrEmpty(name) || name == screen) return false;
            screen = name;
            WriteMarker();
            return true;
        }

        public void Background()
        {
            background = true;
            Session.Background();
            WriteMarker();
        }

        public void Foreground()
        {
            background = false;
            Session.Foreground();
            WriteMarker();
        }

        public bool SampleTrace() => Settings.Tracing && sampler.NextDouble() < Settings.TracesSampleRate;

        // ─── Errors ──────────────────────────────────────────────────────────────

        /// <summary>
        /// An exception or error Unity logged. <paramref name="kind"/>: "exception", "error" (with
        /// logErrors on), "assert". Returns whether it was sent (not a repeat, not Zharp's own).
        /// </summary>
        public bool ReportLogged(string condition, string stack, string kind, bool mainThread, string traceId = null, string spanId = null)
        {
            ErrorDescription d = Stacks.FromLog(condition, stack, kind == "exception" ? "Exception" : kind == "assert" ? "AssertionError" : "Error");
            if (Stacks.IsFromZharp(d.Frames))
            {
                DebugLog("internal error: " + condition);
                return false;
            }
            if (Throttle.IsRepeat(d, Now())) return false;
            var extra = new Dictionary<string, object> { ["thread.name"] = mainThread ? "main" : "worker" };
            Send(d, false, kind == "exception" ? "logMessageReceived" : "log" + kind, extra, traceId, spanId);
            return true;
        }

        /// <summary>A handled error or exception the game reported itself.</summary>
        public bool ReportHandled(ErrorDescription d, IDictionary<string, object> attributes, string traceId = null, string spanId = null)
        {
            d.Frames = Stacks.WithoutOwnFrames(d.Frames);
            if (Throttle.IsRepeat(d, Now())) return false;
            Send(d, true, "manual", attributes, traceId, spanId);
            return true;
        }

        /// <summary>
        /// An exception that reached AppDomain.UnhandledException. When it ends the process, it goes
        /// straight to disk as a crash: there may be no frame left to send it in.
        /// </summary>
        public void ReportUnhandled(ErrorDescription d, bool terminating)
        {
            if (Throttle.IsRepeat(d, Now())) return;
            var extra = new Dictionary<string, object> { ["thread.name"] = "worker" };
            if (!terminating)
            {
                Send(d, false, "AppDomain.UnhandledException", extra);
                return;
            }
            extra["zharp.error.kind"] = "crash";
            Session.Save();
            Dictionary<string, object> record = Otlp.LogRecord("fatal", d.Title, ErrorAttributes(d, false, "AppDomain.UnhandledException", extra));
            Persist();
            Outbox.Write("logs", Json.Write(Otlp.LogsRequest(Resource, new List<object> { record })));
        }

        void Send(ErrorDescription d, bool handled, string mechanism, IDictionary<string, object> extra, string traceId = null, string spanId = null)
        {
            Add(Otlp.LogRecord("error", d.Title, ErrorAttributes(d, handled, mechanism, extra), 0, traceId, spanId));
        }

        Dictionary<string, object> ErrorAttributes(ErrorDescription d, bool handled, string mechanism, IDictionary<string, object> extra)
        {
            var record = new Dictionary<string, object>();
            if (extra != null) foreach (var pair in extra) record[pair.Key] = pair.Value;
            foreach (var pair in Context()) record[pair.Key] = pair.Value;
            record["exception.type"] = d.Type;
            record["exception.message"] = d.Message;
            record["exception.stacktrace"] = d.Stacktrace;
            record["zharp.handled"] = handled ? "true" : "false";
            record["zharp.mechanism"] = mechanism;
            if (SourceLines != null)
            {
                string code = SourceContext.Of(d.Frames, SourceLines);
                if (code.Length > 0) record["zharp.source_context"] = code;
            }
            string crumbs = Breadcrumbs.ToJson();
            if (crumbs.Length > 0) record["zharp.breadcrumbs"] = crumbs;
            return record;
        }

        // ─── Hangs and crashes ───────────────────────────────────────────────────

        /// <summary>On the watchdog's thread: the main thread is stuck. Straight to disk.</summary>
        public void ReportHang(double seconds)
        {
            string s = screen;
            string message = "The main thread was blocked for " + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s" + (s.Length > 0 ? " on " + s : "");
            var record = Context();
            record["exception.type"] = "ApplicationNotResponding";
            record["exception.message"] = message;
            record["exception.stacktrace"] = "ApplicationNotResponding: " + message;
            record["zharp.handled"] = "false";
            record["zharp.mechanism"] = "hang";
            record["zharp.error.kind"] = "anr";
            record["thread.name"] = "main";
            Outbox.Write("logs", Json.Write(Otlp.LogsRequest(Resource, new List<object> { Otlp.LogRecord("error", "ApplicationNotResponding: " + message, record) })));
        }

        string MarkerPath => Path.Combine(storage, "run.json");

        /// <summary>The previous run's marker, removed; null after a clean quit (or on the first launch).</summary>
        public RunMarker TakePreviousRun()
        {
            try
            {
                if (!File.Exists(MarkerPath)) return null;
                string text = File.ReadAllText(MarkerPath);
                File.Delete(MarkerPath);
                return RunMarker.FromJson(text);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// A previous run that didn't quit, with no crash report of its own to send (no minidump, no
        /// iOS crash): an <c>UncleanExit</c> with that run's session, scene and player.
        /// </summary>
        public void ReportUncleanExit(RunMarker marker, string detail = "")
        {
            var record = new Dictionary<string, object>(marker.Context);
            string message = "The game stopped without quitting" + (marker.Context.TryGetValue("screen.name", out object s) && s != null ? " on " + s : "") + (detail.Length > 0 ? ": " + detail : "");
            record["exception.type"] = "UncleanExit";
            record["exception.message"] = message;
            record["exception.stacktrace"] = "UncleanExit: " + message;
            record["zharp.handled"] = "false";
            record["zharp.mechanism"] = "unclean-exit";
            record["zharp.error.kind"] = "crash";
            var resource = new Dictionary<string, object>(Resource);
            if (marker.Release.Length > 0) resource["service.version"] = marker.Release;
            double at = marker.Seen > 0 ? marker.Seen : marker.Started;
            Outbox.Write("logs", Json.Write(Otlp.LogsRequest(resource, new List<object> { Otlp.LogRecord("fatal", "UncleanExit: " + message, record, at) })));
        }

        /// <summary>What a crash in this run will be reported with.</summary>
        public void WriteMarker()
        {
            try
            {
                var marker = new RunMarker
                {
                    Started = startedAt,
                    Seen = Now(),
                    Release = Settings.Release,
                    Editor = InEditor,
                    Background = background,
                    Context = Context(),
                };
                Directory.CreateDirectory(storage);
                File.WriteAllText(MarkerPath, marker.ToJson());
            }
            catch (Exception) { }
        }

        /// <summary>A clean quit: no crash to look for next time.</summary>
        public void ClearMarker()
        {
            try { File.Delete(MarkerPath); }
            catch (Exception) { }
        }

        // ─── Logs and spans ──────────────────────────────────────────────────────

        public void Log(string level, string message, IDictionary<string, object> attributes, double time = 0, string traceId = null, string spanId = null)
        {
            var record = Context();
            if (attributes != null) foreach (var pair in attributes) record[pair.Key] = pair.Value;
            Add(Otlp.LogRecord(level, message, record, time, traceId, spanId));
        }

        public void SpanEnded(SpanRecord span)
        {
            Dictionary<string, object> otlp = span.ToOtlp(Otlp.Clean(Context()));
            lock (gate)
            {
                if (logs.Count == 0 && spans.Count == 0) flushAt = Now() + FlushInterval;
                spans.Add(otlp);
            }
        }

        void Add(Dictionary<string, object> record)
        {
            lock (gate)
            {
                if (logs.Count == 0 && spans.Count == 0) flushAt = Now() + FlushInterval;
                logs.Add(record);
            }
        }

        /// <summary>Called every frame: what waited long enough (or is a full batch) goes to the outbox.</summary>
        public void Tick()
        {
            bool due;
            lock (gate) due = (logs.Count > 0 || spans.Count > 0) && (Now() >= flushAt || logs.Count >= MaxBatch || spans.Count >= MaxBatch);
            if (due) Persist();
        }

        /// <summary>Records and spans waiting in memory.</summary>
        public int Waiting
        {
            get { lock (gate) return logs.Count + spans.Count; }
        }

        /// <summary>What waits in memory to the outbox. Safe on the way out: no network.</summary>
        public void Persist()
        {
            List<object> l, s;
            lock (gate)
            {
                if (logs.Count == 0 && spans.Count == 0) return;
                l = new List<object>(logs);
                s = new List<object>(spans);
                logs.Clear();
                spans.Clear();
            }
            for (int i = 0; i < l.Count; i += MaxBatch)
                Outbox.Write("logs", Json.Write(Otlp.LogsRequest(Resource, l.GetRange(i, Math.Min(MaxBatch, l.Count - i)))));
            for (int i = 0; i < s.Count; i += MaxBatch)
                Outbox.Write("traces", Json.Write(Otlp.TracesRequest(Resource, s.GetRange(i, Math.Min(MaxBatch, s.Count - i)))));
        }
    }
}
