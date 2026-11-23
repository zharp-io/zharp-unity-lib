using System;
using System.Collections;
using System.Threading;
using UnityEngine;
using UnityEngine.Diagnostics;
using UnityEngine.Networking;

/// <summary>
/// The e2e game: one scenario per run, named by ZHARP_E2E_MODE (desktop, the iOS simulator through
/// SIMCTL_CHILD_), the Android intent extra "mode", or ?mode= on WebGL. Then it sends what it has
/// and quits, so the runner can check the stand-in receiver.
/// </summary>
public class Scenario : MonoBehaviour
{
    string mode = "";
    bool updateThrew;

    void Awake()
    {
        mode = Mode();
        Debug.Log("[e2e] mode " + mode);
        Zharp.SetUser("player-42", "ada@example.com", "Ada");
    }

    IEnumerator Start()
    {
        switch (mode)
        {
            case "error":
                Shop.Checkout.Save(2);
                Shop.Checkout.Pay(0); // caught: Zharp.CaptureException
                Debug.LogError("Inventory out of sync: 3 != 4"); // a breadcrumb (logErrors is off)
                yield return null;
                updateThrew = true; // Update throws once now: the exception Unity logs.
                yield return null;
                using (var request = UnityWebRequest.Get(Receiver() + "/game/scores?key=secret123"))
                {
                    request.SetRequestHeader("Authorization", "Bearer player-token");
                    yield return ZharpWebRequest.Send(request);
                }
                break;
            case "logs":
                Debug.Log("Shop opened with 3 items");
                Debug.LogWarning("Low on gold");
                yield return null;
                break;
            case "hang":
                yield return null;
                yield return null;
                Thread.Sleep(7000);
                break;
            case "crash":
                yield return new WaitForSeconds(1);
                Debug.Log("[e2e] crashing");
                Utils.ForceCrash(ForcedCrashCategory.AccessViolation);
                break;
            case "hang-crash":
                // Frozen past the hang threshold, then a crash with no frame in between: the
                // hang the watchdog saw is the crash's, so only the crash is reported.
                yield return null;
                yield return null;
                Thread.Sleep(6500);
                Debug.Log("[e2e] crashing while frozen");
                Utils.ForceCrash(ForcedCrashCategory.AccessViolation);
                break;
            case "managed-crash":
                // An exception on a thread of its own: AppDomain.UnhandledException.
                new Thread(() => throw new InvalidOperationException("Sync thread died")).Start();
                yield return new WaitForSeconds(1);
                break;
            default:
                // "after": a launch after the scenario, sending what the previous run left.
                yield return new WaitForSeconds(2);
                break;
        }
        yield return Finish();
    }

    void Update()
    {
        if (!updateThrew) return;
        updateThrew = false;
        Shop.Checkout.Open();
    }

    IEnumerator Finish()
    {
        Zharp.Flush();
        float until = Time.realtimeSinceStartup + 15;
        while (Time.realtimeSinceStartup < until && Pending() > 0)
        {
            Zharp.Flush();
            yield return new WaitForSeconds(0.5f);
        }
        Debug.Log("[e2e] done " + mode);
        // The runner waits for this (a WebGL page or a phone can't be watched for an exit).
        using (var done = UnityWebRequest.Get(Receiver() + "/e2e/done?mode=" + mode)) yield return done.SendWebRequest();
#if !UNITY_WEBGL || UNITY_EDITOR
        Application.Quit();
#endif
    }

    static int Pending()
    {
        string dir = System.IO.Path.Combine(Application.persistentDataPath, "zharp", "outbox");
        return System.IO.Directory.Exists(dir) ? System.IO.Directory.GetFiles(dir, "*.json").Length : 0;
    }

    static string Receiver()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return "http://10.0.2.2:4318";
#else
        return "http://127.0.0.1:4318";
#endif
    }

    static string Mode()
    {
        string m = Environment.GetEnvironmentVariable("ZHARP_E2E_MODE");
        if (!string.IsNullOrEmpty(m)) return m;
        string[] args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "-zharp-e2e");
        if (at >= 0 && at + 1 < args.Length) return args[at + 1];
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var intent = activity.Call<AndroidJavaObject>("getIntent");
            m = intent.Call<string>("getStringExtra", "mode");
            if (!string.IsNullOrEmpty(m)) return m;
        }
        catch (Exception) { }
#endif
        string url = Application.absoluteURL ?? "";
        int q = url.IndexOf("mode=", StringComparison.Ordinal);
        if (q >= 0) return url.Substring(q + 5).Split('&')[0];
        return "after";
    }
}
