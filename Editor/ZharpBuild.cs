using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;
using UnityEngine;
using ZharpCore;

namespace ZharpEditor
{
    /// <summary>
    /// The build step:
    /// <list type="bullet">
    /// <item>before: IL2CPP Stacktrace Information set to method, file and line, so release stacks keep their lines;</item>
    /// <item>after: the game's scripts (Assets/**/*.cs) uploaded as a source bundle under the build's GUID
    /// (<c>Application.buildGUID</c> in the player), and on iOS the Xcode project set up (<see cref="XcodeStep"/>).</item>
    /// </list>
    /// A failed upload never fails a build.
    /// </summary>
    internal sealed class ZharpBuild : IPreprocessBuildWithReport, IPostprocessBuildWithReport, IUnityLinkerProcessor
    {
        public int callbackOrder => 1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            Settings s = ZharpProjectSettings.Load();
            if (s.Key.Length == 0 && Environment.GetEnvironmentVariable("ZHARP_KEY") == null)
                Debug.LogWarning("[Zharp] No key in Project Settings › Zharp: this build won't send anything.");
            if (!s.Il2CppLineNumbers) return;
            try
            {
                NamedBuildTarget target = NamedBuildTarget.FromBuildTargetGroup(report.summary.platformGroup);
                if (PlayerSettings.GetScriptingBackend(target) != ScriptingImplementation.IL2CPP) return;
                if (PlayerSettings.GetIl2CppStacktraceInformation(target) == Il2CppStacktraceInformation.MethodFileLineMapping) return;
                PlayerSettings.SetIl2CppStacktraceInformation(target, Il2CppStacktraceInformation.MethodFileLineMapping);
                Debug.Log("[Zharp] IL2CPP Stacktrace Information set to Method Name, File Name, and Line Number, so release stacks keep their lines (turn il2cppLineNumbers off in Project Settings › Zharp to leave it).");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Zharp] couldn't set IL2CPP line numbers: " + e.Message);
            }
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            Settings s = ZharpProjectSettings.Load();
            string buildId = report.summary.guid.ToString();
#if UNITY_IOS
            if (report.summary.platform == BuildTarget.iOS)
            {
                try { XcodeStep.Apply(report.summary.outputPath, s); }
                catch (Exception e) { Debug.LogWarning("[Zharp] Xcode project not set up: " + e.Message); }
            }
#endif
            if (s.UploadSources) UploadSources(s, buildId);
        }

        // Packages' link.xml isn't read on its own: handed to the linker here.
        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            string path = Path.GetFullPath("Packages/io.zharp.unity/Runtime/link.xml");
            return File.Exists(path) ? path : null;
        }

        /// <summary>The project's scripts as a source bundle: {"files": {"Assets/…/Cart.cs": "…"}}.</summary>
        internal static Dictionary<string, string> CollectSources(string projectRoot, int maxFiles = 20000, long maxFileBytes = 2_000_000)
        {
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            string assets = Path.Combine(projectRoot, "Assets");
            if (!Directory.Exists(assets)) return files;
            foreach (string path in Directory.EnumerateFiles(assets, "*.cs", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                if (files.Count >= maxFiles) break;
                var info = new FileInfo(path);
                if (info.Length > maxFileBytes) continue;
                string relative = path.Substring(projectRoot.Length).TrimStart('/', '\\').Replace('\\', '/');
                // Editor-only scripts never run in a player.
                if (relative.Split('/').Contains("Editor")) continue;
                files[relative] = File.ReadAllText(path);
            }
            return files;
        }

        internal static byte[] Bundle(Dictionary<string, string> files)
        {
            byte[] json = Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string, object> { ["files"] = files.ToDictionary(p => p.Key, p => (object)p.Value) }));
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(json, 0, json.Length);
            return output.ToArray();
        }

        static void UploadSources(Settings s, string buildId)
        {
            try
            {
                string root = Directory.GetParent(Application.dataPath)!.FullName;
                Dictionary<string, string> files = CollectSources(root);
                if (files.Count == 0) return;
                string token = ZharpProjectSettings.AuthToken();
                string app = ZharpProjectSettings.ApplicationId(s);
                if (token == null)
                {
                    Debug.Log($"[Zharp] dry run (no ZHARP_AUTH_TOKEN): would upload {files.Count} script(s) as kind=source-bundle debugId={buildId}");
                    return;
                }
                if (string.IsNullOrWhiteSpace(app))
                {
                    Debug.LogWarning("[Zharp] set the Application ID in Project Settings › Zharp (or ZHARP_APP_ID) to upload this build's scripts.");
                    return;
                }
                Upload(ZharpProjectSettings.ApiUrl(s), app, token, "source-bundle", buildId, string.IsNullOrEmpty(s.Release) ? Application.version : s.Release, "sources.json", Bundle(files));
                Debug.Log($"[Zharp] uploaded {files.Count} script(s) for build {buildId}.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Zharp] scripts not uploaded (errors from this build show no code): " + e.Message);
            }
        }

        internal static void Upload(string apiUrl, string appId, string token, string kind, string debugId, string release, string name, byte[] gzipped)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var form = new MultipartFormDataContent
            {
                { new StringContent(kind), "kind" },
                { new StringContent(debugId), "debugId" },
                { new StringContent(name), "name" },
            };
            if (!string.IsNullOrWhiteSpace(release)) form.Add(new StringContent(release), "release");
            var file = new ByteArrayContent(gzipped);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
            form.Add(file, "file", name + ".gz");
            string target = apiUrl.TrimEnd('/') + "/api/v1/applications/" + Uri.EscapeDataString(appId.Trim()) + "/artifacts";
            using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = http.SendAsync(request).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                throw new InvalidOperationException($"{(int)response.StatusCode} from {target}: {(body.Length > 300 ? body.Substring(0, 300) : body)}");
            }
        }
    }
}
