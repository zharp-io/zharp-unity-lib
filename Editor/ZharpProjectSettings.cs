using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using ZharpCore;

namespace ZharpEditor
{
    /// <summary>
    /// The settings file, <c>Assets/Zharp/Resources/ZharpSettings.json</c>: in Resources so it ships
    /// with the game, as JSON so <c>npm init zharp</c> and people can write it too.
    /// </summary>
    internal static class ZharpProjectSettings
    {
        public const string Folder = "Assets/Zharp/Resources";
        public const string Path = Folder + "/ZharpSettings.json";

        public static string ReadText() => File.Exists(Path) ? File.ReadAllText(Path) : "";

        public static Settings Load() => Settings.FromJson(ReadText());

        public static void Save(Settings settings)
        {
            Directory.CreateDirectory(Folder);
            string previous = ReadText();
            string text = settings.ToJson(previous);
            if (text == previous) return;
            File.WriteAllText(Path, text);
            AssetDatabase.ImportAsset(Path);
        }

        /// <summary>The build machine's API base for uploads: ZHARP_URL, else from the endpoint, else Zharp.</summary>
        public static string ApiUrl(Settings s)
        {
            string url = Environment.GetEnvironmentVariable("ZHARP_URL");
            if (!string.IsNullOrWhiteSpace(url)) return url.Trim().TrimEnd('/');
            string e = (s.Endpoint ?? "").TrimEnd('/');
            int at = e.IndexOf("/api/v1/", StringComparison.Ordinal);
            return at > 0 ? e.Substring(0, at) : "https://api.zharp.io";
        }

        public static string ApplicationId(Settings s)
        {
            string id = Environment.GetEnvironmentVariable("ZHARP_APP_ID");
            if (string.IsNullOrWhiteSpace(id)) id = Environment.GetEnvironmentVariable("ZHARP_APP");
            return string.IsNullOrWhiteSpace(id) ? s.ApplicationId : id.Trim();
        }

        public static string AuthToken()
        {
            string token = Environment.GetEnvironmentVariable("ZHARP_AUTH_TOKEN");
            return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }
    }

    /// <summary>Edit › Project Settings › Zharp.</summary>
    internal sealed class ZharpSettingsProvider : SettingsProvider
    {
        Settings settings;

        ZharpSettingsProvider() : base("Project/Zharp", SettingsScope.Project)
        {
            keywords = new[] { "Zharp", "crash", "errors", "monitoring", "key" };
        }

        [SettingsProvider]
        static SettingsProvider Create() => new ZharpSettingsProvider();

        public override void OnActivate(string searchContext, UnityEngine.UIElements.VisualElement rootElement) => settings = ZharpProjectSettings.Load();

        public override void OnGUI(string searchContext)
        {
            settings ??= ZharpProjectSettings.Load();
            Settings s = settings;
            EditorGUI.BeginChangeCheck();
            EditorGUIUtility.labelWidth = 220;

            EditorGUILayout.HelpBox("Errors, crashes, hangs and sessions from this game go to Zharp. Paste the key from your app's Setup page in Zharp. Saved in " + ZharpProjectSettings.Path + ", which ships with the game (the key can only send data).", MessageType.None);
            s.Key = EditorGUILayout.TextField(new GUIContent("Key", "zpk_… from the app's Setup page"), s.Key);
            s.Environment = EditorGUILayout.TextField(new GUIContent("Environment", "production, beta… Empty: development in the Editor"), s.Environment);
            s.Release = EditorGUILayout.TextField(new GUIContent("Release", "Empty: Player Settings › Version (" + Application.version + ")"), s.Release);
            s.Service = EditorGUILayout.TextField(new GUIContent("Service", "Empty: " + Settings.ServiceNameFor(Application.productName)), s.Service);
            s.Logs = EditorGUILayout.Toggle(new GUIContent("Send Debug.Log lines", "Off by default"), s.Logs);
            s.LogErrors = EditorGUILayout.Toggle(new GUIContent("Debug.LogError as errors", "Off: they're breadcrumbs on the next exception"), s.LogErrors);
            s.DetectHangs = EditorGUILayout.Toggle("Detect hangs", s.DetectHangs);
            using (new EditorGUI.DisabledScope(!s.DetectHangs))
                s.HangThreshold = EditorGUILayout.Slider("Hang threshold (s)", (float)s.HangThreshold, 1, 60);
            s.Tracing = EditorGUILayout.Toggle(new GUIContent("Tracing", "Spans for scenes, requests and your own"), s.Tracing);
            using (new EditorGUI.DisabledScope(!s.Tracing))
                s.TracesSampleRate = EditorGUILayout.Slider("Traces sample rate", (float)s.TracesSampleRate, 0, 1);
            s.NativeCrashes = EditorGUILayout.Toggle(new GUIContent("Native crashes", "iOS: the Zharp iOS SDK in the Xcode project. Windows: Unity's crash folder, uploaded on the next launch"), s.NativeCrashes);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Build", EditorStyles.boldLabel);
            s.Il2CppLineNumbers = EditorGUILayout.Toggle(new GUIContent("IL2CPP line numbers", "Sets IL2CPP Stacktrace Information to method, file and line on each build"), s.Il2CppLineNumbers);
            s.UploadSources = EditorGUILayout.Toggle(new GUIContent("Upload scripts", "Assets/**/*.cs as a source bundle, for the code that ran"), s.UploadSources);
            s.UploadSymbols = EditorGUILayout.Toggle(new GUIContent("Upload iOS dSYMs", "A Run Script phase in the Xcode project"), s.UploadSymbols);
            s.ApplicationId = EditorGUILayout.TextField(new GUIContent("Application ID", "For uploads; ZHARP_APP_ID overrides it"), s.ApplicationId);
            EditorGUILayout.HelpBox(ZharpProjectSettings.AuthToken() == null
                ? "Uploads need ZHARP_AUTH_TOKEN (an API key with artifacts:write) in the build machine's environment. Never put it in the project."
                : "ZHARP_AUTH_TOKEN is set: builds upload their scripts.", MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Advanced", EditorStyles.boldLabel);
            s.Enabled = EditorGUILayout.Toggle("Enabled", s.Enabled);
            s.AutoStart = EditorGUILayout.Toggle(new GUIContent("Start automatically", "Off: call Zharp.Start() yourself"), s.AutoStart);
            s.Debug = EditorGUILayout.Toggle(new GUIContent("Debug", "Print what Zharp does"), s.Debug);
            s.Endpoint = EditorGUILayout.TextField(new GUIContent("Endpoint", "Empty: " + Settings.DefaultEndpoint), s.Endpoint);

            if (EditorGUI.EndChangeCheck()) ZharpProjectSettings.Save(s);
        }
    }
}
