using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds the e2e player (GameCI's buildMethod): the scene with the Scenario, Zharp's settings
/// pointing at the stand-in receiver, "Shop Quest" 1.4.0, IL2CPP where the platform has it
/// (-zharpBackend mono for Mono; -zharpBuild development for a development build).
/// </summary>
public static class E2EBuild
{
    public static void Build()
    {
        BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
        // GameCI runs the Editor in a container and passes arguments, not the job's environment.
        if (Arg("-zharpToken") != null) Environment.SetEnvironmentVariable("ZHARP_AUTH_TOKEN", Arg("-zharpToken"));
        if (Arg("-zharpUrl") != null) Environment.SetEnvironmentVariable("ZHARP_URL", Arg("-zharpUrl"));
        Directory.CreateDirectory("Assets/Scenes");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("Scenario").AddComponent<Scenario>();
        EditorSceneManager.SaveScene(scene, "Assets/Scenes/Level.unity");

        string receiver = target == BuildTarget.Android ? "http://10.0.2.2:4318" : "http://127.0.0.1:4318";
        Directory.CreateDirectory("Assets/Zharp/Resources");
        File.WriteAllText("Assets/Zharp/Resources/ZharpSettings.json",
            "{\n  \"key\": \"zpk_e2e_test\",\n  \"endpoint\": \"" + receiver + "/api/v1/otlp\",\n  \"applicationId\": \"42\",\n  \"debug\": true\n}\n");
        AssetDatabase.Refresh();

        PlayerSettings.productName = "Shop Quest";
        PlayerSettings.companyName = "Zharp";
        PlayerSettings.bundleVersion = "1.4.0";
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(target)), "io.zharp.e2e.shopquest");
        PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
        PlayerSettings.runInBackground = true;
        var named = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(target));
        // IL2CPP where the platform has it; -zharpBackend mono for an Editor without the IL2CPP module.
        if (target != BuildTarget.WebGL)
            PlayerSettings.SetScriptingBackend(named, Arg("-zharpBackend") == "mono" ? ScriptingImplementation.Mono2x : ScriptingImplementation.IL2CPP);
        if (target == BuildTarget.Android)
        {
            // x86_64 for CI's emulator; obsolete (and dropped) in later Unity 6 releases.
#pragma warning disable CS0618
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64 | AndroidArchitecture.X86_64;
#pragma warning restore CS0618
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
        }
        if (target == BuildTarget.iOS)
        {
            PlayerSettings.iOS.sdkVersion = iOSSdkVersion.SimulatorSDK;
            PlayerSettings.iOS.targetOSVersionString = "15.0";
        }
        if (target == BuildTarget.WebGL)
        {
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            // The default (explicitly thrown exceptions only) has no NullReferenceException at all.
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.FullWithStacktrace;
        }

        string output = Arg("-customBuildPath") ?? Path.Combine("Builds", target.ToString(), target == BuildTarget.StandaloneWindows64 ? "ShopQuest.exe" : target == BuildTarget.Android ? "ShopQuest.apk" : "ShopQuest");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/Level.unity" },
            locationPathName = output,
            target = target,
            options = Arg("-zharpBuild") == "development" ? BuildOptions.Development : BuildOptions.None,
        });
        Debug.Log("[e2e] build " + report.summary.result + " guid " + report.summary.guid + " to " + output);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".", "build-guid.txt"), report.summary.guid.ToString());
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded) EditorApplication.Exit(1);
    }

    static string Arg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
