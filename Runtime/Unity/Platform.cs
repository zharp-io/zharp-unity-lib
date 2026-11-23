using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Networking;
using ZharpCore;

namespace ZharpUnity
{
    /// <summary>Where the game runs: storage, the device, what the platform can do.</summary>
    internal static class Platform
    {
        public static string StorageDirectory => Path.Combine(Application.persistentDataPath, "zharp");

        public static string Environment(string name)
        {
            try { return System.Environment.GetEnvironmentVariable(name); }
            catch (Exception) { return null; }
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        public const bool HasThreads = false;
        public const bool CanGzip = false;
        /// <summary>A browser tab closes without quitting: an unquit run means nothing there.</summary>
        public const bool UsesRunMarker = false;
#else
        public const bool HasThreads = true;
        public const bool CanGzip = true;
        public const bool UsesRunMarker = true;
#endif

        public static bool IsWindowsPlayer => Application.platform == RuntimePlatform.WindowsPlayer;
        public static bool IsIosPlayer => Application.platform == RuntimePlatform.IPhonePlayer;

        public static Dictionary<string, object> Resource()
        {
            var r = new Dictionary<string, object>
            {
                ["process.runtime.name"] = "unity",
                ["process.runtime.version"] = Application.unityVersion,
                ["unity.version"] = Application.unityVersion,
                ["unity.platform"] = Application.platform.ToString(),
#if ENABLE_IL2CPP
                ["unity.scripting_backend"] = "il2cpp",
#else
                ["unity.scripting_backend"] = "mono",
#endif
                ["unity.build"] = Application.isEditor ? "editor" : UnityEngine.Debug.isDebugBuild ? "development" : "release",
                ["os.name"] = OsName(),
                ["os.type"] = OsName(),
                ["os.version"] = SystemInfo.operatingSystem,
                ["app.identifier"] = Application.identifier,
            };
            try { r["host.arch"] = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(); }
            catch (Exception) { }
            string model = SystemInfo.deviceModel;
            if (!string.IsNullOrEmpty(model) && model != SystemInfo.unsupportedIdentifier)
            {
                r["device.model.identifier"] = model;
                r["device.model.name"] = model;
            }
            return r;
        }

        static string OsName()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsPlayer: case RuntimePlatform.WindowsEditor: return "windows";
                case RuntimePlatform.OSXPlayer: case RuntimePlatform.OSXEditor: return "darwin";
                case RuntimePlatform.LinuxPlayer: case RuntimePlatform.LinuxEditor: return "linux";
                case RuntimePlatform.Android: return "android";
                case RuntimePlatform.IPhonePlayer: return "ios";
                case RuntimePlatform.WebGLPlayer: return "web";
                default: return Application.platform.ToString().ToLowerInvariant();
            }
        }

        /// <summary>A project file's lines, for source context in the Editor.</summary>
        public static string[] ProjectFileLines(string projectPath)
        {
            string root = Directory.GetParent(Application.dataPath)?.FullName ?? "";
            string path = Path.Combine(root, projectPath);
            return File.Exists(path) ? File.ReadAllLines(path) : null;
        }
    }

    /// <summary>
    /// The previous run's crash, on the next launch: Unity's crash folder (Windows) as a minidump,
    /// else a run that didn't quit as an UncleanExit. iOS crashes are the Zharp iOS SDK's to send.
    /// </summary>
    internal static class NativeCrashes
    {
        /// <summary>Whether the previous run's crash was found (its report is on its way).</summary>
        public static bool Check(Runtime runtime, RunMarker previous)
        {
            Client client = runtime.Client;
            bool native = client.Settings.NativeCrashes;
            if (native && Platform.IsWindowsPlayer)
            {
                string root = CrashFolders.RootFor(Path.GetTempPath(), Application.companyName, Application.productName);
                DateTime since = previous != null && previous.Started > 0
                    ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(previous.Started)
                    : DateTime.UtcNow.AddDays(-7);
                string folder = CrashFolders.Newest(root, since);
                if (folder != null)
                {
                    Upload(runtime, previous, folder);
                    return true;
                }
            }
            if (previous == null || !previous.WorthReporting) return false;
            if (native && Platform.IsIosPlayer) return false;
            client.ReportUncleanExit(previous);
            runtime.Debug("the previous run didn't quit: sending it as an UncleanExit");
            return false;
        }

        static void Upload(Runtime runtime, RunMarker previous, string folder)
        {
            Client client = runtime.Client;
            byte[] dump = CrashFolders.ReadDump(folder);
            if (dump == null)
            {
                CrashFolders.MarkSent(folder);
                return;
            }
            string boundary = "zharp" + Ids.Hex16();
            byte[] body = Multipart.Build(boundary, CrashFolders.Annotations(previous, client.Resource, folder), "upload_file_minidump", "crash.dmp", dump);
            var request = new UnityWebRequest(client.Settings.MinidumpUrl(), "POST")
            {
                uploadHandler = new UploadHandlerRaw(body),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 60,
            };
            request.SetRequestHeader("Content-Type", "multipart/form-data; boundary=" + boundary);
            runtime.Debug("the previous run crashed: uploading " + folder);
            request.SendWebRequest().completed += _ =>
            {
                try
                {
                    long status = request.responseCode;
                    bool answered = request.result == UnityWebRequest.Result.Success || request.result == UnityWebRequest.Result.ProtocolError;
                    // Taken, or refused for good: done. No answer, 408, 429 or 5xx: tried again next launch.
                    if (answered && status != 408 && status != 429 && status < 500) CrashFolders.MarkSent(folder);
                    runtime.Debug("minidump upload: " + (answered ? "HTTP " + status : request.error));
                }
                finally
                {
                    request.Dispose();
                }
            };
        }
    }
}
