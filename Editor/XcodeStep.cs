#if UNITY_IOS
using System;
using System.IO;
using UnityEditor.iOS.Xcode;
using UnityEngine;
using ZharpCore;

namespace ZharpEditor
{
    /// <summary>
    /// iOS: the generated Xcode project gets
    /// <list type="bullet">
    /// <item>the Zharp iOS SDK (Swift package) linked into UnityFramework, and a Swift bridge the
    /// package's C# calls (<c>zharp_ios_start</c>…), for native crashes; with nativeCrashes off, a
    /// stub bridge with the same functions, so the C# side always links;</item>
    /// <item>a deployment target of at least iOS 15, which the iOS SDK needs;</item>
    /// <item>a Run Script phase that uploads the build's dSYMs when ZHARP_AUTH_TOKEN is set.</item>
    /// </list>
    /// </summary>
    internal static class XcodeStep
    {
        public const string IosSdkUrl = "https://github.com/zharp-io/zharp-ios-lib.git";
        public const string IosSdkVersion = "0.4.1";
        const string MinimumIos = "15.0";

        public static void Apply(string projectPath, Settings s)
        {
            string pbxPath = PBXProject.GetPBXProjectPath(projectPath);
            var project = new PBXProject();
            project.ReadFromFile(pbxPath);
            string framework = project.GetUnityFrameworkTargetGuid();
            string main = project.GetUnityMainTargetGuid();

            bool native = s.NativeCrashes;
            string bridgeDir = Path.Combine(projectPath, "Libraries", "Zharp");
            Directory.CreateDirectory(bridgeDir);
            File.WriteAllText(Path.Combine(bridgeDir, "ZharpBridge.swift"), native ? Bridge : Stub);
            string fileGuid = project.AddFile("Libraries/Zharp/ZharpBridge.swift", "Libraries/Zharp/ZharpBridge.swift", PBXSourceTree.Source);
            project.AddFileToBuild(framework, fileGuid);
            if (string.IsNullOrEmpty(project.GetBuildPropertyForAnyConfig(framework, "SWIFT_VERSION")))
                project.SetBuildProperty(framework, "SWIFT_VERSION", "5.0");

            if (native)
            {
                string package = project.AddRemotePackageReferenceAtVersionUpToNextMinor(IosSdkUrl, IosSdkVersion);
                project.AddRemotePackageFrameworkToProject(framework, "Zharp", package, false);
                foreach (string target in new[] { project.ProjectGuid(), main, framework })
                {
                    string current = project.GetBuildPropertyForAnyConfig(target, "IPHONEOS_DEPLOYMENT_TARGET");
                    if (string.IsNullOrEmpty(current) || new Version(current) < new Version(MinimumIos))
                        project.SetBuildProperty(target, "IPHONEOS_DEPLOYMENT_TARGET", MinimumIos);
                }
                Debug.Log("[Zharp] Xcode project: the Zharp iOS SDK " + IosSdkVersion + " added for native crashes (deployment target at least iOS " + MinimumIos + ").");
            }

            if (s.UploadSymbols)
            {
                project.AddShellScriptBuildPhase(main, "Upload dSYMs to Zharp", "/bin/sh", UploadScript(ZharpProjectSettings.ApiUrl(s), ZharpProjectSettings.ApplicationId(s)),
                    new System.Collections.Generic.List<string> { "$(DWARF_DSYM_FOLDER_PATH)/$(DWARF_DSYM_FILE_NAME)" });
                project.SetBuildProperty(main, "ENABLE_USER_SCRIPT_SANDBOXING", "NO");
                foreach (string target in new[] { main, framework })
                    project.SetBuildProperty(target, "DEBUG_INFORMATION_FORMAT", "dwarf-with-dsym");
            }
            project.WriteToFile(pbxPath);
        }

        /// <summary>The iOS SDK's own dSYM upload (curl, no Node needed), each UUID once.</summary>
        static string UploadScript(string apiUrl, string appId) =>
$@"# Zharp: this build's dSYMs, for readable native crashes. Needs ZHARP_AUTH_TOKEN (an API key with
# artifacts:write) in the environment; set it in CI, never in the project.
APP_ID=""${{ZHARP_APP_ID:-{appId}}}""
if [ -z ""$ZHARP_AUTH_TOKEN"" ] || [ -z ""$APP_ID"" ]; then
  echo ""Zharp: no ZHARP_AUTH_TOKEN or application ID: dSYMs not uploaded""; exit 0
fi
URL=""${{ZHARP_URL:-{apiUrl}}}/api/v1/applications/$APP_ID/artifacts""
find ""$DWARF_DSYM_FOLDER_PATH"" -maxdepth 1 -name '*.dSYM' | while read -r dsym; do
  for dwarf in ""$dsym""/Contents/Resources/DWARF/*; do
    xcrun dwarfdump --uuid ""$dwarf"" | while read -r _ uuid arch _; do
      arch=""${{arch#(}}""; arch=""${{arch%)}}""
      curl --fail --silent --show-error -H ""Authorization: Bearer $ZHARP_AUTH_TOKEN"" \
        -F kind=dsym -F ""debugId=$uuid"" -F ""arch=$arch"" -F ""name=$(basename ""$dwarf"")"" \
        -F ""file=@$dwarf"" ""$URL"" >/dev/null || echo ""warning: Zharp dSYM upload failed for $uuid""
    done
  done
done
";

        const string Bridge = @"// Written by the Zharp Unity package's build step. The Zharp iOS SDK, started for native crashes
// only: the C# side does everything else.
import Foundation
import Zharp

private func text(_ p: UnsafePointer<CChar>?) -> String? {
    guard let p = p else { return nil }
    let s = String(cString: p)
    return s.isEmpty ? nil : s
}

@_cdecl(""zharp_ios_start"")
public func zharp_ios_start(_ key: UnsafePointer<CChar>?, _ endpoint: UnsafePointer<CChar>?, _ service: UnsafePointer<CChar>?,
                            _ release: UnsafePointer<CChar>?, _ environment: UnsafePointer<CChar>?, _ debug: Bool) {
    Zharp.start { options in
        options.key = text(key) ?? """"
        options.endpoint = text(endpoint)
        options.service = text(service)
        options.release = text(release)
        options.environment = text(environment)
        options.debug = debug
        options.captureCrashes = true
        options.tracing = false
        options.detectHangs = false
        options.trackScreens = false
        options.traceNetwork = false
        options.logs = false
        options.captureOutput = false
    }
}

@_cdecl(""zharp_ios_set_user"")
public func zharp_ios_set_user(_ id: UnsafePointer<CChar>?, _ email: UnsafePointer<CChar>?, _ name: UnsafePointer<CChar>?) {
    guard let id = text(id) else { Zharp.clearUser(); return }
    Zharp.setUser(id: id, email: text(email), name: text(name))
}

@_cdecl(""zharp_ios_clear_user"")
public func zharp_ios_clear_user() { Zharp.clearUser() }

@_cdecl(""zharp_ios_set_tag"")
public func zharp_ios_set_tag(_ key: UnsafePointer<CChar>?, _ value: UnsafePointer<CChar>?) {
    guard let key = text(key) else { return }
    Zharp.setTag(key, text(value))
}

@_cdecl(""zharp_ios_screen"")
public func zharp_ios_screen(_ name: UnsafePointer<CChar>?) {
    guard let name = text(name) else { return }
    Zharp.trackScreen(name)
}
";

        const string Stub = @"// Written by the Zharp Unity package's build step: native crashes are off (nativeCrashes), so these
// do nothing; they're here so the package's C# links.
import Foundation

@_cdecl(""zharp_ios_start"") public func zharp_ios_start(_ a: UnsafePointer<CChar>?, _ b: UnsafePointer<CChar>?, _ c: UnsafePointer<CChar>?, _ d: UnsafePointer<CChar>?, _ e: UnsafePointer<CChar>?, _ f: Bool) {}
@_cdecl(""zharp_ios_set_user"") public func zharp_ios_set_user(_ a: UnsafePointer<CChar>?, _ b: UnsafePointer<CChar>?, _ c: UnsafePointer<CChar>?) {}
@_cdecl(""zharp_ios_clear_user"") public func zharp_ios_clear_user() {}
@_cdecl(""zharp_ios_set_tag"") public func zharp_ios_set_tag(_ a: UnsafePointer<CChar>?, _ b: UnsafePointer<CChar>?) {}
@_cdecl(""zharp_ios_screen"") public func zharp_ios_screen(_ a: UnsafePointer<CChar>?) {}
";
    }
}
#endif
