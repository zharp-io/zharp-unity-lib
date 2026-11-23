using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Zharp in a game: put this on a GameObject in your first scene. Zharp itself starts on its own
/// (Project Settings › Zharp); this shows what you add on top.
/// </summary>
public class ShopQuestSample : MonoBehaviour
{
    IEnumerator Start()
    {
        // The signed-in player, on everything from now on.
        Zharp.SetUser("player-42", "ada@example.com", "Ada");
        Zharp.SetTag("level", "forest-2");

        // A handled error, with where it happened.
        try
        {
            LoadSave(2);
        }
        catch (Exception e)
        {
            Zharp.CaptureException(e, new Dictionary<string, object> { ["slot"] = 2 });
        }

        // A span around a piece of work, and a traced request inside it.
        using (Zharp.StartSpan("load_leaderboard"))
        {
            using var request = UnityWebRequest.Get("https://example.com/");
            yield return ZharpWebRequest.Send(request);
        }

        Zharp.LogInfo("Shop opened", new Dictionary<string, object> { ["items"] = 3 });
    }

    static void LoadSave(int slot) => throw new InvalidOperationException("Save slot " + slot + " is corrupt");
}
