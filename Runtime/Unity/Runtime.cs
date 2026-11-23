using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZharpCore;

namespace ZharpUnity
{
    /// <summary>
    /// Zharp while it runs: the Core client fed by Unity's log callback, scenes and lifecycle, a
    /// hidden GameObject that ticks it every frame, and the transport that sends the outbox.
    /// </summary>
    internal sealed class Runtime
    {
        public const string SettingsResource = "ZharpSettings";

        static Runtime current;
        public static Runtime Current => current;

        public readonly Client Client;
        public readonly Transport Transport;
        Watchdog watchdog;
        Host host;
        readonly int mainThread;
        [ThreadStatic] static bool inHook;
        readonly HashSet<string> warned = new HashSet<string>();

        Runtime(Client client)
        {
            Client = client;
            Transport = new Transport(client);
            mainThread = Thread.CurrentThread.ManagedThreadId;
        }

        // Domain reload off (Enter Play Mode options): statics live on between plays.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            current?.Shutdown(false);
            current = null;
        }

        // As early as a scene can't be: errors in the first scene's Awake are seen.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void AutoStart()
        {
            Settings s = LoadSettings();
            if (s.AutoStart) Start(null, s);
        }

        /// <summary>The settings file (Resources/ZharpSettings.json), then environment variables.</summary>
        public static Settings LoadSettings()
        {
            Settings s;
            try
            {
                var asset = Resources.Load<TextAsset>(SettingsResource);
                s = Settings.FromJson(asset != null ? asset.text : "");
            }
            catch (Exception)
            {
                s = new Settings();
            }
            s.ApplyEnvironment(Platform.Environment);
            return s;
        }

        public static bool Start(Action<ZharpOptions> configure, Settings loaded = null)
        {
            if (current != null) return true;
            try
            {
                Settings s = loaded ?? LoadSettings();
                if (configure != null)
                {
                    ZharpOptions options = ZharpOptions.From(s);
                    configure(options);
                    options.ApplyTo(s);
                }
                s.ApplyDefaults(Application.productName, Application.version, Application.isEditor);
                if (!s.Enabled)
                {
                    if (s.Debug) UnityEngine.Debug.Log("[Zharp] disabled (enabled is off): nothing will be sent");
                    return false;
                }
                if (s.Key.Length == 0)
                {
                    UnityEngine.Debug.LogWarning("[Zharp] No key in Project Settings › Zharp (or ZHARP_KEY): nothing will be sent. Copy it from the app's Setup page in Zharp.");
                    return false;
                }
                var client = new Client(s, Platform.StorageDirectory, Platform.Resource())
                {
                    BuildId = Application.buildGUID ?? "",
                    InEditor = Application.isEditor,
                };
                if (Application.isEditor) client.SourceLines = Platform.ProjectFileLines;
                var r = new Runtime(client);
                client.DebugLog = r.Debug;
                current = r;
                r.Install();
                return true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[Zharp] couldn't start: " + e.Message);
                current = null;
                return false;
            }
        }

        public static void Stop() => current?.Shutdown(true);

        void Install()
        {
            Settings s = Client.Settings;
            RunMarker previous = Platform.UsesRunMarker ? Client.TakePreviousRun() : null;
            NativeCrashes.Check(this, previous);
            if (Platform.UsesRunMarker) Client.WriteMarker();

            Application.logMessageReceivedThreaded += OnLog;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
            TaskScheduler.UnobservedTaskException += OnUnobservedTask;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
            Application.quitting += OnQuitting;

            var go = new GameObject("Zharp") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            host = go.AddComponent<Host>();
            host.Runtime = this;

            Scene first = SceneManager.GetActiveScene();
            if (first.IsValid() && first.name.Length > 0) ScreenShown(first.name, "scene");

            if (s.DetectHangs && Platform.HasThreads)
                watchdog = new Watchdog(s.HangThreshold, Client.ReportHang);

            if (s.NativeCrashes) IosBridge.Start(s, Client.BuildId, Client.Session.Current());
            Debug("started: service \"" + s.Service + "\", sending to " + s.Endpoint);
        }

        /// <summary>Every frame, from the host.</summary>
        public void Update()
        {
            if (watchdog != null)
            {
                // Watched from the first frame on: loading before it isn't a hang.
                watchdog.Start();
                watchdog.Beat();
            }
            if (Client.Screen.Length == 0)
            {
                // The first scene, when it was active before Zharp could hear it change.
                Scene active = SceneManager.GetActiveScene();
                if (active.IsValid() && active.name.Length > 0) ScreenShown(active.name, "scene");
            }
            Client.Tick();
            Transport.Pump();
        }

        public void Paused(bool paused)
        {
            if (paused)
            {
                if (watchdog != null) watchdog.Paused = true;
                Client.Background();
                Client.Persist();
                Transport.Pump(force: true);
            }
            else
            {
                Client.Foreground();
                if (watchdog != null) watchdog.Paused = false;
            }
        }

        /// <summary>A desktop window losing focus stops the player loop unless Run In Background is on.</summary>
        public void Focused(bool focused)
        {
            if (watchdog == null || Application.runInBackground || Application.isMobilePlatform) return;
            watchdog.Paused = !focused;
        }

        void OnQuitting() => Shutdown(true);

        void Shutdown(bool clean)
        {
            if (current != this) return;
            Application.logMessageReceivedThreaded -= OnLog;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTask;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            Application.quitting -= OnQuitting;
            watchdog?.Stop();
            Client.Persist();
            Client.Session.Save();
            if (clean) Client.ClearMarker();
            if (host != null) UnityEngine.Object.Destroy(host.gameObject);
            current = null;
        }

        // ─── Errors ──────────────────────────────────────────────────────────────

        void OnLog(string condition, string stack, LogType type)
        {
            if (inHook) return;
            inHook = true;
            try
            {
                if (condition != null && condition.StartsWith("[Zharp]", StringComparison.Ordinal)) return;
                Settings s = Client.Settings;
                bool main = Thread.CurrentThread.ManagedThreadId == mainThread;
                ZharpSpan span = main ? ZharpSpan.Current : null;
                switch (type)
                {
                    case LogType.Exception:
                        Client.ReportLogged(condition, stack, "exception", main, span?.TraceId, span?.SpanId);
                        break;
                    case LogType.Error:
                    case LogType.Assert:
                        if (s.LogErrors) Client.ReportLogged(condition, stack, type == LogType.Assert ? "assert" : "error", main, span?.TraceId, span?.SpanId);
                        Client.Breadcrumbs.Add("error", condition, DateTime.UtcNow);
                        if (s.Logs) Client.Log("error", condition, null, 0, span?.TraceId, span?.SpanId);
                        break;
                    case LogType.Warning:
                        Client.Breadcrumbs.Add("warn", condition, DateTime.UtcNow);
                        if (s.Logs) Client.Log("warn", condition, null, 0, span?.TraceId, span?.SpanId);
                        break;
                    default:
                        if (s.Logs) Client.Log("info", condition, null, 0, span?.TraceId, span?.SpanId);
                        break;
                }
            }
            catch (Exception) { /* never into the game */ }
            finally { inHook = false; }
        }

        void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            if (!(e.ExceptionObject is Exception exception)) return;
            try { Client.ReportUnhandled(Describe(exception), e.IsTerminating); }
            catch (Exception) { }
        }

        void OnUnobservedTask(object sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                Exception inner = e.Exception?.InnerExceptions.Count == 1 ? e.Exception.InnerExceptions[0] : e.Exception;
                if (inner != null) Client.ReportUnhandled(Describe(inner), false);
            }
            catch (Exception) { }
        }

        /// <summary>An exception as Unity writes it ("Type: message" and Unity's frames).</summary>
        static ErrorDescription Describe(Exception exception)
        {
            string text = StackTraceUtility.ExtractStringFromException(exception) ?? "";
            int nl = text.IndexOf('\n');
            string head = nl >= 0 ? text.Substring(0, nl) : text;
            string stack = nl >= 0 ? text.Substring(nl + 1) : "";
            ErrorDescription d = Stacks.FromLog(head, stack);
            if (d.Type == "Exception" && !head.StartsWith("Exception", StringComparison.Ordinal))
            {
                d.Type = exception.GetType().Name;
                d.Message = exception.Message;
            }
            return d;
        }

        public void CaptureException(Exception exception, IDictionary<string, object> attributes)
        {
            ZharpSpan span = ZharpSpan.Current;
            Client.ReportHandled(Describe(exception), attributes, span?.TraceId, span?.SpanId);
        }

        public void CaptureError(string message, string type, string stack, IDictionary<string, object> attributes)
        {
            ZharpSpan span = ZharpSpan.Current;
            var d = new ErrorDescription { Type = string.IsNullOrEmpty(type) ? "Error" : type, Message = message, Frames = Stacks.Parse(stack) };
            Client.ReportHandled(d, attributes, span?.TraceId, span?.SpanId);
        }

        // ─── Screens, logs ───────────────────────────────────────────────────────

        void OnActiveSceneChanged(Scene from, Scene to)
        {
            if (to.IsValid()) ScreenShown(to.name, "scene");
        }

        public void ScreenShown(string name, string type)
        {
            if (!Client.ScreenShown(name)) return;
            IosBridge.Screen(name);
            if (Client.Settings.Tracing)
                ZharpSpan.Begin("screen " + name, new Dictionary<string, object> { ["screen.name"] = name, ["screen.type"] = type }, true).End();
        }

        public void Log(string level, string message, IDictionary<string, object> attributes)
        {
            ZharpSpan span = ZharpSpan.Current;
            Client.Log(level, message ?? "", attributes, 0, span?.TraceId, span?.SpanId);
        }

        public void Flush()
        {
            Client.Persist();
            Transport.Pump(force: true);
        }

        public void Debug(string message)
        {
            if (Client.Settings.Debug) UnityEngine.Debug.Log("[Zharp] " + message);
        }

        public void Warn(string message)
        {
            lock (warned) if (!warned.Add(message)) return;
            UnityEngine.Debug.LogWarning("[Zharp] " + message);
        }
    }

    /// <summary>The hidden GameObject that gives Zharp a frame tick and the app's lifecycle.</summary>
    [AddComponentMenu("")]
    internal sealed class Host : MonoBehaviour
    {
        public Runtime Runtime;

        void Update() => Runtime?.Update();
        void OnApplicationPause(bool paused) => Runtime?.Paused(paused);
        void OnApplicationFocus(bool focused) => Runtime?.Focused(focused);
    }
}
