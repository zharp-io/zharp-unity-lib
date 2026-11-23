using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ZharpCore;

namespace ZharpUnity
{
    /// <summary>
    /// iOS native crashes: the build step adds the Zharp iOS SDK to the Xcode project with a small
    /// Swift bridge (or, with nativeCrashes off, a stub with the same functions). Started here for
    /// crashes only — signals and Objective-C exceptions, saved and sent on the next launch — with
    /// the game's service, release, player, scene and Unity session as tags.
    /// </summary>
    internal static class IosBridge
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void zharp_ios_start(string key, string endpoint, string service, string release, string environment, bool debug);
        [DllImport("__Internal")] static extern void zharp_ios_set_user(string id, string email, string name);
        [DllImport("__Internal")] static extern void zharp_ios_clear_user();
        [DllImport("__Internal")] static extern void zharp_ios_set_tag(string key, string value);
        [DllImport("__Internal")] static extern void zharp_ios_screen(string name);
        static bool started;
#endif

        public static void Start(Settings s, string buildId, string sessionId)
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                // The iOS SDK's own endpoint is the OTLP root, as ours.
                zharp_ios_start(s.Key, s.Endpoint == Settings.DefaultEndpoint ? "" : s.Endpoint, s.Service, s.Release, s.Environment, s.Debug);
                started = true;
                SetTag("unity.session.id", sessionId);
                if (!string.IsNullOrEmpty(buildId)) SetTag("zharp.build_id", buildId);
            }
            catch (Exception e)
            {
                // EntryPointNotFound: an Xcode project built before the package was added.
                UnityEngine.Debug.LogWarning("[Zharp] iOS native crashes are off: " + e.Message);
            }
#endif
        }

        public static void SetUser(string id, string email, string name)
        {
#if UNITY_IOS && !UNITY_EDITOR
            if (started) Safe(() => zharp_ios_set_user(id ?? "", email ?? "", name ?? ""));
#endif
        }

        public static void ClearUser()
        {
#if UNITY_IOS && !UNITY_EDITOR
            if (started) Safe(zharp_ios_clear_user);
#endif
        }

        public static void SetTag(string key, string value)
        {
#if UNITY_IOS && !UNITY_EDITOR
            if (started) Safe(() => zharp_ios_set_tag(key ?? "", value));
#endif
        }

        public static void Screen(string name)
        {
#if UNITY_IOS && !UNITY_EDITOR
            if (started) Safe(() => zharp_ios_screen(name ?? ""));
#endif
        }

        static void Safe(Action action)
        {
            try { action(); }
            catch (Exception) { }
        }
    }
}
