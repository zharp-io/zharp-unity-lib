using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZharpCore;
using ZharpUnity;

/// <summary>
/// Zharp for Unity. It starts on its own before the first scene loads, with the key from
/// Project Settings › Zharp, and reports:
/// <list type="bullet">
/// <item>every exception Unity logs, on any thread, with its stack, sent once a minute at most per place;</item>
/// <item>crashes, on the next launch (iOS through the Zharp iOS SDK, Windows from Unity's crash folder);</item>
/// <item>hangs (the main thread stuck for 5 s);</item>
/// <item>sessions, the device, the active scene as the screen, the player you set;</item>
/// <item>with logs on, what the game logs.</item>
/// </list>
/// Nothing here ever throws into the game.
/// </summary>
public static class Zharp
{
    public const string Version = ZharpInfo.Version;

    /// <summary>Whether Zharp is running.</summary>
    public static bool IsRunning => Runtime.Current != null;

    /// <summary>The current session's ID (32 hex characters), or "" when Zharp isn't running.</summary>
    public static string SessionId => Runtime.Current?.Client.Session.Current() ?? "";

    /// <summary>This install's random device ID, or "" when Zharp isn't running.</summary>
    public static string DeviceId => Runtime.Current?.Client.Session.DeviceId ?? "";

    /// <summary>The screen errors are reported on: the active scene, unless set with <see cref="SetScreen"/>.</summary>
    public static string Screen => Runtime.Current?.Client.Screen ?? "";

    /// <summary>
    /// Start Zharp. Called for you before the first scene loads unless <c>autoStart</c> is off in
    /// Project Settings › Zharp; then call it yourself, changing any setting:
    /// <code>Zharp.Start(options => { options.Key = key; options.Environment = "beta"; });</code>
    /// </summary>
    public static bool Start(Action<ZharpOptions> configure = null) => Runtime.Start(configure);

    /// <summary>Stop: what's waiting goes to the outbox, sent on the next launch.</summary>
    public static void Stop() => Runtime.Stop();

    // ─── Errors ──────────────────────────────────────────────────────────────

    /// <summary>An exception the game caught, with its stack.</summary>
    public static void CaptureException(Exception exception, IDictionary<string, object> attributes = null)
    {
        Runtime r = Runtime.Current;
        if (r == null || exception == null) return;
        try { r.CaptureException(exception, attributes); }
        catch (Exception e) { r.Debug("CaptureException: " + e.Message); }
    }

    /// <summary>A handled error the game reports itself, with the frames where this was called.</summary>
    public static void CaptureError(string message, IDictionary<string, object> attributes = null, string type = "Error")
    {
        Runtime r = Runtime.Current;
        if (r == null) return;
        try { r.CaptureError(message ?? "", type, StackTraceUtility.ExtractStackTrace(), attributes); }
        catch (Exception e) { r.Debug("CaptureError: " + e.Message); }
    }

    // ─── Context ─────────────────────────────────────────────────────────────

    /// <summary>The signed-in player: <c>enduser.id</c>, <c>enduser.email</c>, <c>enduser.name</c> on everything from now on.</summary>
    public static void SetUser(string id, string email = null, string name = null)
    {
        Runtime r = Runtime.Current;
        if (r == null) return;
        r.Client.SetUser(id, email, name);
        IosBridge.SetUser(id, email, name);
    }

    /// <summary>Forget the player (sign-out), and start a new session for whoever comes next.</summary>
    public static void ClearUser()
    {
        Runtime r = Runtime.Current;
        if (r == null) return;
        r.Client.ClearUser();
        IosBridge.ClearUser();
    }

    /// <summary>An attribute on every error, log and span from now on; null removes it.</summary>
    public static void SetTag(string key, object value)
    {
        Runtime r = Runtime.Current;
        if (r == null) return;
        r.Client.SetTag(key, value);
        IosBridge.SetTag(key, value?.ToString());
    }

    /// <summary>The screen, when the active scene doesn't say it well (a menu inside one scene).</summary>
    public static void SetScreen(string name) => Runtime.Current?.ScreenShown(name, "view");

    // ─── Logs ────────────────────────────────────────────────────────────────

    /// <summary>A log record, sent whatever the <c>logs</c> setting says (that one is about Debug.Log).</summary>
    public static void LogDebug(string message, IDictionary<string, object> attributes = null) => Runtime.Current?.Log("debug", message, attributes);
    public static void LogInfo(string message, IDictionary<string, object> attributes = null) => Runtime.Current?.Log("info", message, attributes);
    public static void LogWarn(string message, IDictionary<string, object> attributes = null) => Runtime.Current?.Log("warn", message, attributes);
    public static void LogError(string message, IDictionary<string, object> attributes = null) => Runtime.Current?.Log("error", message, attributes);

    // ─── Spans ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A span for a piece of work; spans started before it ends (on this thread) are inside it.
    /// End it with <c>End()</c>, or <c>using (Zharp.StartSpan("load_level")) { … }</c>.
    /// </summary>
    public static ZharpSpan StartSpan(string name, IDictionary<string, object> attributes = null) => ZharpSpan.Begin(name, attributes, false);

    /// <summary><c>SceneManager.LoadSceneAsync</c> as a <c>scene.load {name}</c> span, until the scene is ready.</summary>
    public static AsyncOperation LoadSceneAsync(string sceneName, LoadSceneMode mode = LoadSceneMode.Single)
    {
        ZharpSpan span = ZharpSpan.Begin("scene.load " + sceneName, new Dictionary<string, object> { ["scene.name"] = sceneName, ["scene.mode"] = mode == LoadSceneMode.Single ? "single" : "additive" }, true);
        AsyncOperation op;
        try
        {
            op = SceneManager.LoadSceneAsync(sceneName, mode);
        }
        catch (Exception e)
        {
            span.SetError(e.Message).End();
            throw;
        }
        if (op == null) span.SetError("scene not found").End();
        else op.completed += _ => span.End();
        return op;
    }

    /// <summary>Send everything waiting now.</summary>
    public static void Flush() => Runtime.Current?.Flush();
}
