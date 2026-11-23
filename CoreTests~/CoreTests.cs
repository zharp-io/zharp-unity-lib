using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using ZharpCore;

namespace CoreTests
{
    sealed class TempDir : IDisposable
    {
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zharp-unity-" + Guid.NewGuid().ToString("N"));
        public TempDir() { Directory.CreateDirectory(Path); }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    static class Read
    {
        /// <summary>The log records of an OTLP logs body, as {body, severity, attributes}.</summary>
        public static List<(string Body, string Severity, Dictionary<string, object> A, string Time)> Logs(string json)
        {
            var output = new List<(string, string, Dictionary<string, object>, string)>();
            var root = Json.ParseObject(json);
            foreach (Dictionary<string, object> rl in (List<object>)root["resourceLogs"])
                foreach (Dictionary<string, object> scope in (List<object>)rl["scopeLogs"])
                    foreach (Dictionary<string, object> r in (List<object>)scope["logRecords"])
                        output.Add((
                            (string)((Dictionary<string, object>)r["body"])["stringValue"],
                            (string)r["severityText"],
                            Values((List<object>)r["attributes"]),
                            (string)r["timeUnixNano"]));
            return output;
        }

        public static Dictionary<string, object> Resource(string json, string key = "resourceLogs")
        {
            var root = Json.ParseObject(json);
            var first = (Dictionary<string, object>)((List<object>)root[key])[0];
            return Values((List<object>)((Dictionary<string, object>)first["resource"])["attributes"]);
        }

        public static Dictionary<string, object> Values(List<object> attributes) =>
            attributes.Cast<Dictionary<string, object>>().ToDictionary(a => (string)a["key"], a => ((Dictionary<string, object>)a["value"]).Values.First());
    }

    public class JsonTests
    {
        [Fact]
        public void RoundTrips()
        {
            var value = new Dictionary<string, object> { ["s"] = "a\"b\\c\n\t\u0001é", ["n"] = 1.5, ["i"] = 3, ["b"] = true, ["z"] = null, ["l"] = new List<object> { 1, "x" } };
            var back = Json.ParseObject(Json.Write(value));
            Assert.Equal("a\"b\\c\n\t\u0001é", back["s"]);
            Assert.Equal(1.5, back["n"]);
            Assert.Equal(3.0, back["i"]);
            Assert.Equal(true, back["b"]);
            Assert.Null(back["z"]);
            Assert.Equal(2, ((List<object>)back["l"]).Count);
        }

        [Fact]
        public void IsNullForWhatIsntJson()
        {
            Assert.Null(Json.Parse("{\"a\": "));
            Assert.Null(Json.Parse("not json"));
            Assert.Null(Json.Parse(""));
            Assert.NotNull(Json.Parse("\uFEFF{\"key\": \"zpk_1\"}"));
        }
    }

    public class SettingsTests
    {
        [Fact]
        public void ReadsTheFileThenTheEnvironmentThenDefaults()
        {
            var s = Settings.FromJson("{\"key\": \" zpk_abc \", \"logs\": false, \"hangThreshold\": 3, \"tracesSampleRate\": 4, \"unknown\": 1}");
            Assert.Equal("zpk_abc", s.Key);
            Assert.Equal(3, s.HangThreshold);
            Assert.Equal(1, s.TracesSampleRate);
            var env = new Dictionary<string, string> { ["ZHARP_LOGS"] = "true", ["ZHARP_ENVIRONMENT"] = "beta", ["ZHARP_DEBUG"] = "maybe" };
            s.ApplyEnvironment(name => env.TryGetValue(name, out string v) ? v : null);
            Assert.True(s.Logs);
            Assert.Equal("beta", s.Environment);
            Assert.False(s.Debug);
            s.ApplyDefaults("Shop Quest", "1.4.0", inEditor: false);
            Assert.Equal("shop-quest", s.Service);
            Assert.Equal("1.4.0", s.Release);
            Assert.Equal(Settings.DefaultEndpoint, s.Endpoint);
        }

        [Fact]
        public void IsDevelopmentInTheEditorUnlessSet()
        {
            var s = Settings.FromJson("{}");
            s.ApplyDefaults("", "", inEditor: true);
            Assert.Equal("development", s.Environment);
            Assert.Equal("unity-game", s.Service);
        }

        [Theory]
        [InlineData("Shop Quest", "shop-quest")]
        [InlineData("  Space_Shooter 2! ", "space-shooter-2")]
        [InlineData("Ünïcode Game", "ncode-game")]
        [InlineData("a  --  b", "a-b")]
        public void NamesTheServiceAsZharpNamesApps(string product, string service) => Assert.Equal(service, Settings.ServiceNameFor(product));

        [Fact]
        public void KeepsUnknownKeysWhenSaved()
        {
            var s = Settings.FromJson("{\"key\": \"zpk_1\", \"custom\": \"x\"}");
            s.Logs = true;
            var back = Json.ParseObject(s.ToJson("{\"key\": \"zpk_1\", \"custom\": \"x\"}"));
            Assert.Equal("x", back["custom"]);
            Assert.Equal(true, back["logs"]);
            Assert.Equal("zpk_1", Settings.FromJson(s.ToJson()).Key);
        }

        [Fact]
        public void PointsMinidumpsNextToOtlp()
        {
            var s = new Settings { Key = "zpk_a+b", Endpoint = "http://127.0.0.1:4318/api/v1/otlp" };
            Assert.Equal("http://127.0.0.1:4318/api/v1/minidump?key=zpk_a%2Bb", s.MinidumpUrl());
        }
    }

    public class StackTests
    {
        const string ExceptionStack = "Shop.Checkout.AddToCart (Shop.Item item) (at Assets/Scripts/Shop/Checkout.cs:36)\nShop.Checkout.Update () (at Assets/Scripts/Shop/Checkout.cs:12)\n";

        [Fact]
        public void ReadsAnExceptionUnityLogged()
        {
            var d = Stacks.FromLog("NullReferenceException: Object reference not set to an instance of an object", ExceptionStack);
            Assert.Equal("NullReferenceException", d.Type);
            Assert.Equal("Object reference not set to an instance of an object", d.Message);
            Assert.Equal(2, d.Frames.Count);
            Assert.Equal("Assets/Scripts/Shop/Checkout.cs", d.Frames[0].File);
            Assert.Equal(36, d.Frames[0].Line);
            Assert.Equal("NullReferenceException: Object reference not set to an instance of an object\nShop.Checkout.AddToCart (Shop.Item item) (at Assets/Scripts/Shop/Checkout.cs:36)\nShop.Checkout.Update () (at Assets/Scripts/Shop/Checkout.cs:12)", d.Stacktrace);
        }

        [Fact]
        public void ReadsBuildsWithoutLinesAndMonosFormat()
        {
            var frames = Stacks.Parse("Shop.Cart.Total () (at <00000000000000000000000000000000>:0)\n  at Shop.Sync.Upload () [0x00000] in <00000000000000000000000000000000>:0 \nUnityEngine.Debug:LogError (object)\nnot a frame at all");
            Assert.Equal(3, frames.Count);
            Assert.All(frames, f => Assert.Equal("", f.File));
            Assert.Equal("Shop.Sync.Upload", frames[1].Function);
        }

        [Fact]
        public void TellsTypesFromSentences()
        {
            Assert.Equal("Shop.SaveError", Stacks.FromLog("Shop.SaveError: disk full", "").Type);
            Assert.Equal("Exception", Stacks.FromLog("Exception: boom", "").Type);
            var log = Stacks.FromLog("Error: could not load level 3", "", "Error");
            Assert.Equal("Error", log.Type);
            Assert.Equal("Error: could not load level 3", log.Message);
            var sentence = Stacks.FromLog("Player fell: out of bounds", "", "Error");
            Assert.Equal("Error", sentence.Type);
            Assert.Equal("Player fell: out of bounds", sentence.Message);
        }

        [Fact]
        public void LeavesZharpsOwnFramesOut()
        {
            var frames = Stacks.Parse("Zharp:CaptureError (string,System.Collections.Generic.IDictionary`2<string, object>)\nZharpUnity.Errors:Here ()\nShop.SaveGame:Write (int) (at Assets/Scripts/SaveGame.cs:27)");
            var mine = Stacks.WithoutOwnFrames(frames);
            Assert.Single(mine);
            Assert.Equal("Shop.SaveGame:Write", mine[0].Function);
            Assert.True(Stacks.IsFromZharp(Stacks.Parse("ZharpUnity.Transport:Pump () (at ./Library/PackageCache/io.zharp.unity@0.1.0/Runtime/Unity/Transport.cs:40)")));
            Assert.False(Stacks.IsFromZharp(frames));

            // A player's StackTraceUtility.ExtractStackTrace() keeps Unity's own frames on top.
            var player = Stacks.Parse("UnityEngine.Debug:ExtractStackTraceNoAlloc (byte*,int,string)\n" +
                "UnityEngine.StackTraceUtility:ExtractStackTrace ()\n" +
                "Zharp:CaptureError (string,System.Collections.Generic.IDictionary`2<string, object>,string)\n" +
                "Shop.Checkout:Save (int)\n" +
                "UnityEngine.SetupCoroutine:InvokeMoveNext (System.Collections.IEnumerator,intptr)");
            Assert.Equal("Shop.Checkout:Save", Stacks.WithoutOwnFrames(player)[0].Function);
            Assert.False(Stacks.IsFromZharp(player));
        }

        [Fact]
        public void ReadsAnIl2cppPlayersLogCallStack()
        {
            // As an IL2CPP player hands a log's stack to logMessageReceived: no space before "(".
            var frames = Stacks.WithoutOwnFrames(Stacks.Parse(
                "Zharp:CaptureError(String, IDictionary`2, String) (at /github/workspace/Runtime/Zharp.cs:61)\n" +
                "Shop.Checkout:Save(Int32) (at /github/workspace/Assets/Scripts/Shop/Checkout.cs:29)\n" +
                "<Start>d__3:MoveNext() (at /github/workspace/Assets/Scripts/Scenario.cs:30)\n" +
                "UnityEngine.SetupCoroutine:InvokeMoveNext(IEnumerator, IntPtr)"));
            Assert.Equal(3, frames.Count);
            Assert.Equal("Shop.Checkout:Save", frames[0].Function);
            Assert.Equal("/github/workspace/Assets/Scripts/Shop/Checkout.cs", frames[0].File);
            Assert.Equal(29, frames[0].Line);
            Assert.Equal("<Start>d__3:MoveNext", frames[1].Function);
            Assert.Equal("UnityEngine.SetupCoroutine:InvokeMoveNext", frames[2].Function);
        }

        [Fact]
        public void FindsTheProjectPathWhereverTheBuildRan()
        {
            Assert.Equal("Assets/Scripts/Cart.cs", Stacks.ProjectPath("/home/runner/work/shop/Assets/Scripts/Cart.cs"));
            Assert.Equal("Assets/Scripts/Cart.cs", Stacks.ProjectPath(@"D:\a\shop\Assets\Scripts\Cart.cs"));
            Assert.Equal("Assets/Scripts/Cart.cs", Stacks.ProjectPath("Assets/Scripts/Cart.cs"));
        }

        [Fact]
        public void SendsTheSameErrorFromTheSamePlaceOnceAMinute()
        {
            var throttle = new Throttle();
            var d = Stacks.FromLog("NullReferenceException: x", ExceptionStack);
            Assert.False(throttle.IsRepeat(d, 1000));
            Assert.True(throttle.IsRepeat(Stacks.FromLog("NullReferenceException: x", ExceptionStack), 1030));
            Assert.False(throttle.IsRepeat(Stacks.FromLog("NullReferenceException: x", "Shop.Other.Place () (at Assets/Other.cs:1)"), 1030));
            Assert.False(throttle.IsRepeat(d, 1061));
        }

        [Fact]
        public void ReadsTheCodeAroundTheGamesFrames()
        {
            var lines = Enumerable.Range(1, 40).Select(i => "line " + i).ToArray();
            var frames = Stacks.Parse("UnityEngine.Debug:LogError (object)\nShop.Checkout.AddToCart () (at /build/Assets/Scripts/Shop/Checkout.cs:36)");
            string json = SourceContext.Of(frames, file => file == "Assets/Scripts/Shop/Checkout.cs" ? lines : null);
            var context = (Dictionary<string, object>)((List<object>)Json.Parse(json))[0];
            Assert.Equal("Assets/Scripts/Shop/Checkout.cs", context["file"]);
            Assert.Equal("line 36", context["code"]);
            Assert.Equal(5, ((List<object>)context["pre"]).Count);
            Assert.Equal("line 37", ((List<object>)context["post"])[0]);
        }

        [Fact]
        public void KeepsTheLastThirtyBreadcrumbs()
        {
            var b = new Breadcrumbs();
            for (int i = 0; i < 40; i++) b.Add("warn", "w" + i + new string('x', 400), DateTime.UtcNow);
            var list = (List<object>)Json.Parse(b.ToJson());
            Assert.Equal(30, list.Count);
            Assert.StartsWith("w10", (string)((Dictionary<string, object>)list[0])["message"]);
            Assert.Equal(300, ((string)((Dictionary<string, object>)list[0])["message"]).Length);
        }
    }

    public class HeaderTests
    {
        [Fact]
        public void FiltersSecretHeaders()
        {
            var a = Headers.Attributes("request", new[]
            {
                new KeyValuePair<string, string>("Authorization", "Bearer x"),
                new KeyValuePair<string, string>("X-Session-Token", "t"),
                new KeyValuePair<string, string>("Accept", "a"),
                new KeyValuePair<string, string>("accept", "b"),
            });
            Assert.Equal("[Filtered]", a["http.request.header.authorization"]);
            Assert.Equal("[Filtered]", a["http.request.header.x-session-token"]);
            Assert.Equal("a, b", a["http.request.header.accept"]);
        }

        [Fact]
        public void RedactsUrls()
        {
            Assert.Equal("https://[Filtered]@api.example.com/scores?level=2&key=[Filtered]&sig=[Filtered]#top",
                Headers.RedactUrl("https://bob:pw@api.example.com/scores?level=2&key=abc&sig=xyz#top"));
            Assert.Equal("api.example.com", Headers.HostOf("https://api.example.com:8443/x"));
        }
    }

    public class SessionTests
    {
        [Fact]
        public void KeepsTheDeviceAndARecentSessionAcrossLaunches()
        {
            using var dir = new TempDir();
            double now = 1000;
            var first = new Session(dir.Path, () => now);
            now += 600;
            var second = new Session(dir.Path, () => now);
            Assert.Equal(first.DeviceId, second.DeviceId);
            Assert.Equal(first.Id, second.Id);
            Assert.Equal(32, second.Id.Length);
            now += 31 * 60;
            var third = new Session(dir.Path, () => now);
            Assert.NotEqual(first.Id, third.Id);
            Assert.Equal(first.DeviceId, third.DeviceId);
        }

        [Fact]
        public void StartsANewSessionAfterThirtyMinutesInTheBackground()
        {
            using var dir = new TempDir();
            double now = 1000;
            var s = new Session(dir.Path, () => now);
            string started = null;
            s.OnNewSession = id => started = id;
            string id1 = s.Current();
            s.Background();
            now += 29 * 60;
            Assert.Equal(id1, s.Current());
            now += 2 * 60;
            s.Foreground();
            Assert.NotEqual(id1, s.Id);
            Assert.Equal(s.Id, started);
            s.Reset();
            Assert.Equal(s.Id, started);
        }
    }

    public class DeliveryTests
    {
        [Fact]
        public void RetriesWithBackoffAndDropsWhatTheServerRefuses()
        {
            double now = 0;
            var d = new Delivery(() => now);
            Assert.Equal(DeliveryOutcome.Retry, d.Answered(0, null, out bool drop));
            Assert.False(drop);
            Assert.InRange(d.RetryAt, 0.8, 1.2);
            Assert.False(d.MaySend());
            Assert.True(d.MaySend(force: true));
            Assert.Equal(DeliveryOutcome.Retry, d.Answered(429, "30", out drop));
            Assert.Equal(30, d.RetryAt);
            Assert.Equal(DeliveryOutcome.Drop, d.Answered(400, null, out drop));
            Assert.True(drop);
            Assert.Equal(DeliveryOutcome.Sent, d.Answered(200, null, out drop));
            Assert.True(drop);
            Assert.Equal(0, d.RetryAt);
        }

        [Fact]
        public void GivesUpOnABatchAfterFiveServerFailures()
        {
            var d = new Delivery(() => 0);
            bool drop = false;
            for (int i = 0; i < 4; i++) { d.Answered(503, null, out drop); Assert.False(drop); }
            d.Answered(503, null, out drop);
            Assert.True(drop);
        }

        [Fact]
        public void StopsForAnHourOnARejectedKey()
        {
            double now = 0;
            var d = new Delivery(() => now);
            Assert.Equal(DeliveryOutcome.KeyRejected, d.Answered(401, null, out bool drop));
            Assert.True(drop);
            Assert.False(d.MaySend(force: true));
            now = 3601;
            Assert.True(d.MaySend());
        }
    }

    public class OutboxTests
    {
        [Fact]
        public void KeepsBatchesInOrderUpToTheCap()
        {
            using var dir = new TempDir();
            var o = new Outbox(Path.Combine(dir.Path, "outbox"));
            for (int i = 0; i < Outbox.MaxFiles + 5; i++) Assert.True(o.Write(i % 2 == 0 ? "logs" : "traces", "{\"n\":" + i + "}"));
            var files = o.Files();
            Assert.Equal(Outbox.MaxFiles, files.Count);
            Assert.Equal("{\"n\":5}", o.Read(files[0]));
            Assert.Equal("traces", Outbox.PathOf(files[0]));
            Assert.Equal("logs", Outbox.PathOf(files[1]));
            o.Remove(files[0]);
            Assert.Equal(Outbox.MaxFiles - 1, o.Files().Count);
            Assert.Empty(Directory.GetFiles(o.Directory, "*.tmp"));
        }
    }

    public class WatchdogTests
    {
        [Fact]
        public void ReportsAFreezeOnceUntilTheNextFrame()
        {
            long ms = 0;
            var hangs = new List<double>();
            var w = new Watchdog(5, hangs.Add, () => ms, () => false);
            w.Beat();
            ms = 4900; w.Check();
            Assert.Empty(hangs);
            ms = 5200; w.Check();
            ms = 9000; w.Check();
            Assert.Equal(new[] { 5.2 }, hangs);
            w.Beat();
            ms = 15000; w.Check();
            Assert.Equal(2, hangs.Count);
        }

        [Fact]
        public void IsQuietInTheBackgroundAndUnderADebugger()
        {
            long ms = 0;
            bool debugger = false;
            var hangs = new List<double>();
            var w = new Watchdog(5, hangs.Add, () => ms, () => debugger);
            w.Paused = true;
            ms = 60000; w.Check();
            w.Paused = false;
            debugger = true;
            ms = 70000; w.Check();
            Assert.Empty(hangs);
        }

        [Fact]
        public void SaysOnceWhenAFreezeIsOver()
        {
            long ms = 0;
            var hangs = new List<double>();
            int recovered = 0;
            var w = new Watchdog(5, hangs.Add, () => ms, () => false, () => recovered++);
            w.Beat();
            ms = 2000; w.Beat();
            Assert.Equal(0, recovered);
            ms = 8000; w.Check();
            ms = 9000; w.Beat();
            ms = 9100; w.Beat();
            Assert.Single(hangs);
            Assert.Equal(1, recovered);
        }

        [Fact]
        public void RunsOnItsOwnThread()
        {
            var hang = new System.Threading.ManualResetEventSlim();
            var w = new Watchdog(1, _ => hang.Set(), null, () => false);
            w.Start();
            Assert.True(hang.Wait(3000));
            w.Stop();
        }
    }

    public class ClientTests
    {
        static Client NewClient(TempDir dir, Func<double> clock = null)
        {
            var s = Settings.FromJson("{\"key\": \"zpk_test\"}");
            s.ApplyDefaults("Shop Quest", "1.4.0", false);
            return new Client(s, dir.Path, new Dictionary<string, object> { ["os.name"] = "linux" }, clock) { BuildId = "9f8e7d6c5b4a39281706f5e4d3c2b1a0" };
        }

        [Fact]
        public void SendsAnExceptionWithItsContextOnce()
        {
            using var dir = new TempDir();
            var c = NewClient(dir);
            c.SetUser("player-42", "ada@example.com", "Ada");
            c.SetTag("level", "forest-2");
            c.ScreenShown("Level");
            c.Breadcrumbs.Add("warn", "Coupon SPRING expired", DateTime.UtcNow);
            string stack = "Shop.Checkout.AddToCart () (at Assets/Scripts/Shop/Checkout.cs:36)\n";
            Assert.True(c.ReportLogged("NullReferenceException: Object reference not set to an instance of an object", stack, "exception", true));
            Assert.False(c.ReportLogged("NullReferenceException: Object reference not set to an instance of an object", stack, "exception", true));
            c.Persist();
            var files = c.Outbox.Files();
            Assert.Single(files);
            string body = c.Outbox.Read(files[0]);
            var record = Read.Logs(body).Single();
            Assert.Equal("ERROR", record.Severity);
            Assert.Equal("NullReferenceException: Object reference not set to an instance of an object", record.Body);
            Assert.Equal("NullReferenceException", record.A["exception.type"]);
            Assert.Equal("false", record.A["zharp.handled"]);
            Assert.Equal("logMessageReceived", record.A["zharp.mechanism"]);
            Assert.Equal("player-42", record.A["enduser.id"]);
            Assert.Equal("Level", record.A["screen.name"]);
            Assert.Equal("forest-2", record.A["level"]);
            Assert.Equal(32, ((string)record.A["session.id"]).Length);
            Assert.Equal("9f8e7d6c5b4a39281706f5e4d3c2b1a0", record.A["zharp.build_id"]);
            Assert.Equal("main", record.A["thread.name"]);
            Assert.Contains("Coupon SPRING expired", (string)record.A["zharp.breadcrumbs"]);
            var resource = Read.Resource(body);
            Assert.Equal("shop-quest", resource["service.name"]);
            Assert.Equal("1.4.0", resource["service.version"]);
            Assert.Equal("unity", resource["telemetry.sdk.language"]);
            Assert.Equal(32, ((string)resource["device.id"]).Length);
            Assert.Equal("linux", resource["os.name"]);
        }

        [Fact]
        public void NeverReportsZharpsOwnErrors()
        {
            using var dir = new TempDir();
            var c = NewClient(dir);
            Assert.False(c.ReportLogged("NullReferenceException: x", "ZharpUnity.Transport:Pump () (at Packages/io.zharp.unity/Runtime/Unity/Transport.cs:40)", "exception", true));
        }

        [Fact]
        public void SendsHandledErrorsWithoutZharpsFrames()
        {
            using var dir = new TempDir();
            var c = NewClient(dir);
            var d = new ErrorDescription { Type = "Error", Message = "Save failed: disk full", Frames = Stacks.Parse("Zharp:CaptureError (string)\nShop.SaveGame:Write (int) (at Assets/Scripts/SaveGame.cs:27)") };
            Assert.True(c.ReportHandled(d, new Dictionary<string, object> { ["slot"] = 2 }));
            c.Persist();
            var record = Read.Logs(c.Outbox.Read(c.Outbox.Files()[0])).Single();
            Assert.Equal("true", record.A["zharp.handled"]);
            Assert.Equal("Error: Save failed: disk full\nShop.SaveGame:Write (int) (at Assets/Scripts/SaveGame.cs:27)", record.A["exception.stacktrace"]);
            Assert.Equal(2L, Convert.ToInt64(record.A["slot"]));
        }

        [Fact]
        public void SendsAHangOnceWhenTheMainThreadRecovers()
        {
            using var dir = new TempDir();
            var c = NewClient(dir);
            c.ScreenShown("Level");
            c.ReportHang(5.24);
            Assert.True(c.HasPendingHang);
            Assert.Empty(c.Outbox.Files());
            c.HangRecovered();
            c.HangRecovered();
            var record = Read.Logs(c.Outbox.Read(c.Outbox.Files().Single())).Single();
            Assert.Equal("ApplicationNotResponding", record.A["exception.type"]);
            Assert.Equal("The main thread was blocked for 5.2 s on Level", record.A["exception.message"]);
            Assert.Equal("anr", record.A["zharp.error.kind"]);
            Assert.False(c.HasPendingHang);
            NewClient(dir).SettlePreviousHang(false);
            Assert.Single(c.Outbox.Files());
        }

        [Fact]
        public void DropsAHangThatEndedInACrash()
        {
            using var dir = new TempDir();
            var first = NewClient(dir);
            first.WriteMarker();
            first.ReportHang(6.0);
            // The process dies frozen: no recovery, no clean quit. The next launch finds its crash.
            var second = NewClient(dir);
            Assert.NotNull(second.TakePreviousRun());
            second.SettlePreviousHang(crashed: true);
            Assert.Empty(second.Outbox.Files());
            Assert.False(second.HasPendingHang);
        }

        [Fact]
        public void SendsAHangThatNeverRecoveredOnTheNextLaunchWhenThatRunDidntCrash()
        {
            using var dir = new TempDir();
            var first = NewClient(dir);
            first.ScreenShown("Level");
            first.WriteMarker();
            first.ReportHang(5.5);
            first.ClearMarker(); // quit cleanly, still frozen
            var second = NewClient(dir);
            Assert.Null(second.TakePreviousRun());
            second.SettlePreviousHang(crashed: false);
            var record = Read.Logs(second.Outbox.Read(second.Outbox.Files().Single())).Single();
            Assert.Equal("The main thread was blocked for 5.5 s on Level", record.A["exception.message"]);
            Assert.Equal(first.Session.Id, record.A["session.id"]);
        }

        [Fact]
        public void WritesACrashOnAnyThreadStraightToDisk()
        {
            using var dir = new TempDir();
            var c = NewClient(dir);
            c.Log("info", "before", null);
            c.ReportUnhandled(new ErrorDescription { Type = "InvalidOperationException", Message = "boom" }, terminating: true);
            var all = c.Outbox.Files().SelectMany(f => Read.Logs(c.Outbox.Read(f))).ToList();
            Assert.Equal(2, all.Count);
            var crash = all.Single(r => r.A.ContainsKey("exception.type"));
            Assert.Equal("FATAL", crash.Severity);
            Assert.Equal("crash", crash.A["zharp.error.kind"]);
        }

        [Fact]
        public void ReportsAnUncleanExitFromThePreviousRunsMarker()
        {
            using var dir = new TempDir();
            var first = NewClient(dir, () => 5000);
            first.SetUser("player-42", null, null);
            first.ScreenShown("Level");
            first.WriteMarker();
            string session = first.Session.Id;

            var second = NewClient(dir, () => 9000);
            RunMarker marker = second.TakePreviousRun();
            Assert.NotNull(marker);
            Assert.True(marker.WorthReporting);
            Assert.Null(second.TakePreviousRun());
            second.ReportUncleanExit(marker);
            var record = Read.Logs(second.Outbox.Read(second.Outbox.Files().Single())).Single();
            Assert.Equal("UncleanExit", record.A["exception.type"]);
            Assert.Equal("The game stopped without quitting on Level", record.A["exception.message"]);
            Assert.Equal(session, record.A["session.id"]);
            Assert.Equal("player-42", record.A["enduser.id"]);
            Assert.Equal(Otlp.Nanos(5000), record.Time);
        }

        [Fact]
        public void LeavesNoMarkerAfterACleanQuitAndSkipsBackgroundRuns()
        {
            using var dir = new TempDir();
            var c = NewClient(dir);
            c.WriteMarker();
            c.ClearMarker();
            Assert.Null(NewClient(dir).TakePreviousRun());
            var b = NewClient(dir);
            b.Background();
            Assert.False(NewClient(dir).TakePreviousRun().WorthReporting);
        }

        [Fact]
        public void BatchesLogsAndSpansAndFlushesAfterFiveSeconds()
        {
            using var dir = new TempDir();
            double now = 100;
            var c = NewClient(dir, () => now);
            c.Log("info", "Checkout opened", new Dictionary<string, object> { ["items"] = 3 });
            c.SpanEnded(new SpanRecord { TraceId = Ids.Hex32(), SpanId = Ids.Hex16(), Name = "screen Level", Start = 99, End = 100 });
            c.Tick();
            Assert.Empty(c.Outbox.Files());
            now = 105.1;
            c.Tick();
            var files = c.Outbox.Files();
            Assert.Equal(2, files.Count);
            Assert.Contains(files, f => f.EndsWith("-traces.json"));
            var span = Read.Resource(c.Outbox.Read(files.Single(f => f.EndsWith("-traces.json"))), "resourceSpans");
            Assert.Equal("shop-quest", span["service.name"]);
        }
    }

    public class CrashFolderTests
    {
        [Fact]
        public void FindsTheNewestUnsentDumpAndNamesItsFields()
        {
            using var dir = new TempDir();
            string root = CrashFolders.RootFor(dir.Path, "Zharp", "Shop Quest");
            string old = Path.Combine(root, "Crash_2026-11-20_101500123");
            string fresh = Path.Combine(root, "Crash_2026-11-23_101500123");
            Directory.CreateDirectory(old);
            Directory.CreateDirectory(fresh);
            File.WriteAllBytes(Path.Combine(old, "crash.dmp"), Encoding.ASCII.GetBytes("MDMPold"));
            File.SetLastWriteTimeUtc(Path.Combine(old, "crash.dmp"), DateTime.UtcNow.AddDays(-3));
            File.WriteAllBytes(Path.Combine(fresh, "crash.dmp"), Encoding.ASCII.GetBytes("MDMPnew"));
            File.WriteAllText(Path.Combine(fresh, "error.log"), "Unity Player [version: Unity 6000.0.47f1]\nGameAssembly.dll caused an Access Violation (0xc0000005)");

            string found = CrashFolders.Newest(root, DateTime.UtcNow.AddMinutes(-10));
            Assert.Equal(fresh, found);
            Assert.Equal("MDMPnew", Encoding.ASCII.GetString(CrashFolders.ReadDump(found)));

            var marker = new RunMarker { Release = "1.3.0", Context = new Dictionary<string, object> { ["session.id"] = "s1", ["enduser.id"] = "player-42", ["screen.name"] = "Level" } };
            var a = CrashFolders.Annotations(marker, new Dictionary<string, object> { ["service.name"] = "shop-quest", ["device.id"] = "d1", ["service.version"] = "1.4.0" }, found);
            Assert.Equal("1.3.0", a["zharp.release"]);
            Assert.Equal("s1", a["zharp.session_id"]);
            Assert.Equal("d1", a["zharp.device_id"]);
            Assert.Equal("player-42", a["zharp.user.id"]);
            Assert.Equal("Level", a["screen.name"]);
            Assert.Contains("Access Violation", a["unity.error_log"]);

            CrashFolders.MarkSent(found);
            Assert.Null(CrashFolders.Newest(root, DateTime.UtcNow.AddMinutes(-10)));
        }

        [Fact]
        public void BuildsTheUploadAsBreakpadSendersDo()
        {
            byte[] body = Multipart.Build("b0undary", new Dictionary<string, string> { ["zharp.release"] = "1.4.0" }, "upload_file_minidump", "crash.dmp", Encoding.ASCII.GetBytes("MDMP\u0001"));
            string text = Encoding.ASCII.GetString(body);
            Assert.StartsWith("--b0undary\r\nContent-Disposition: form-data; name=\"zharp.release\"\r\n\r\n1.4.0\r\n", text);
            Assert.Contains("name=\"upload_file_minidump\"; filename=\"crash.dmp\"", text);
            Assert.EndsWith("MDMP\u0001\r\n--b0undary--\r\n", text);
        }

        [Fact]
        public void CompressesWhereItCan()
        {
            byte[] gz = Gzip.TryCompress(Encoding.UTF8.GetBytes(new string('a', 1000)));
            Assert.NotNull(gz);
            Assert.Equal(0x1f, gz[0]);
        }
    }
}
