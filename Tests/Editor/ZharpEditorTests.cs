using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using ZharpCore;
using ZharpEditor;

namespace ZharpTests
{
    /// <summary>Edit-mode tests, in the Editor: Unity's own stack strings, the settings file, the build step's bundle.</summary>
    public class ZharpEditorTests
    {
        [Test]
        public void ReadsTheStackUnityWritesForACaughtException()
        {
            string text = null;
            try { Thrower.Throw(); }
            catch (System.InvalidOperationException e) { text = StackTraceUtility.ExtractStringFromException(e); }
            int nl = text.IndexOf('\n');
            ErrorDescription d = Stacks.FromLog(text.Substring(0, nl), text.Substring(nl + 1));
            Assert.AreEqual("InvalidOperationException", d.Type);
            Assert.AreEqual("Cart is empty", d.Message);
            Assert.That(d.Frames.Count, Is.GreaterThan(0));
            StringAssert.Contains("Thrower", d.Frames[0].Function, text);
            StringAssert.EndsWith("ZharpEditorTests.cs", d.Frames[0].File);
            Assert.That(d.Frames[0].Line, Is.GreaterThan(0));
        }

        [Test]
        public void ReadsTheCallStackUnityWritesForALog()
        {
            List<Frame> frames = Stacks.Parse(StackTraceUtility.ExtractStackTrace());
            Assert.That(frames.Any(f => f.Function.Contains("ReadsTheCallStackUnityWritesForALog")), "frames: " + string.Join(" | ", frames.Select(f => f.Text)));
        }

        [Test]
        public void StartsAHandledErrorAtTheGamesFrameInAPlayer()
        {
            // StackTraceUtility.ExtractStackTrace() in a player, called from Zharp.CaptureError.
            List<Frame> frames = Stacks.WithoutOwnFrames(Stacks.Parse(
                "UnityEngine.Debug:ExtractStackTraceNoAlloc (byte*,int,string)\n" +
                "UnityEngine.StackTraceUtility:ExtractStackTrace ()\n" +
                "Zharp:CaptureError (string,System.Collections.Generic.IDictionary`2<string, object>,string)\n" +
                "Shop.Checkout:Save (int)\n" +
                "UnityEngine.SetupCoroutine:InvokeMoveNext (System.Collections.IEnumerator,intptr)"));
            Assert.AreEqual("Shop.Checkout:Save", frames[0].Function);
        }

        [Test]
        public void SavesSettingsAsJsonInResources()
        {
            Assert.AreEqual("Assets/Zharp/Resources/ZharpSettings.json", ZharpProjectSettings.Path);
            var s = Settings.FromJson("{\"key\": \"zpk_1\", \"logs\": true}");
            Settings back = Settings.FromJson(s.ToJson());
            Assert.AreEqual("zpk_1", back.Key);
            Assert.IsTrue(back.Logs);
        }

        [Test]
        public void BundlesTheGamesScriptsButNotEditorOnes()
        {
            string root = Path.Combine(Path.GetTempPath(), "zharp-bundle-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Assets", "Scripts", "Editor"));
            File.WriteAllText(Path.Combine(root, "Assets", "Scripts", "Cart.cs"), "class Cart {}");
            File.WriteAllText(Path.Combine(root, "Assets", "Scripts", "Editor", "Tool.cs"), "class Tool {}");
            Dictionary<string, string> files = ZharpBuild.CollectSources(root);
            Assert.AreEqual(new[] { "Assets/Scripts/Cart.cs" }, files.Keys.ToArray());
            byte[] gz = ZharpBuild.Bundle(files);
            Assert.AreEqual(0x1f, gz[0]);
            Directory.Delete(root, true);
        }

        static class Thrower
        {
            // Not inlined: the Editor may run scripts with Release code optimization.
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            public static void Throw() => throw new System.InvalidOperationException("Cart is empty");
        }
    }
}
