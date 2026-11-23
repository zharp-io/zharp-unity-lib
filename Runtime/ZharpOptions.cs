using ZharpCore;

/// <summary>
/// The settings Zharp starts with: Project Settings › Zharp, then environment variables, then
/// whatever <c>Zharp.Start(options => …)</c> changes. Only <see cref="Key"/> is required.
/// </summary>
public sealed class ZharpOptions
{
    /// <summary>The app's key from Zharp (<c>zpk_…</c>). It can only send data, so it is safe inside a game.</summary>
    public string Key { get; set; }
    /// <summary><c>production</c>, <c>beta</c>… Default: <c>development</c> in the Editor, none in players.</summary>
    public string Environment { get; set; }
    /// <summary>Default: Player Settings › Version.</summary>
    public string Release { get; set; }
    /// <summary>Default: the Product Name as Zharp names apps ("Shop Quest" → "shop-quest").</summary>
    public string Service { get; set; }
    /// <summary>For Zharp's own testing; leave unset.</summary>
    public string Endpoint { get; set; }
    /// <summary>Send <c>Debug.Log</c> lines as logs. Default off.</summary>
    public bool Logs { get; set; }
    /// <summary><c>Debug.LogError</c> as errors of their own, not breadcrumbs. Default off.</summary>
    public bool LogErrors { get; set; }
    /// <summary>Spans (scenes, requests, yours). Default on.</summary>
    public bool Tracing { get; set; } = true;
    /// <summary>The share of traces kept, 0…1. Errors, crashes and logs are always sent.</summary>
    public double TracesSampleRate { get; set; } = 1.0;
    /// <summary>Report the main thread blocked for <see cref="HangThreshold"/> seconds. Default on (never on WebGL).</summary>
    public bool DetectHangs { get; set; } = true;
    public double HangThreshold { get; set; } = 5.0;
    /// <summary>Native crashes: the Zharp iOS SDK on iOS, Unity's crash folder on Windows. Default on.</summary>
    public bool NativeCrashes { get; set; } = true;
    /// <summary>Print what Zharp does to the console.</summary>
    public bool Debug { get; set; }

    internal static ZharpOptions From(Settings s) => new ZharpOptions
    {
        Key = s.Key,
        Environment = s.Environment,
        Release = s.Release,
        Service = s.Service,
        Endpoint = s.Endpoint,
        Logs = s.Logs,
        LogErrors = s.LogErrors,
        Tracing = s.Tracing,
        TracesSampleRate = s.TracesSampleRate,
        DetectHangs = s.DetectHangs,
        HangThreshold = s.HangThreshold,
        NativeCrashes = s.NativeCrashes,
        Debug = s.Debug,
    };

    internal void ApplyTo(Settings s)
    {
        s.Key = Key ?? "";
        s.Environment = Environment ?? "";
        s.Release = Release ?? "";
        s.Service = Service ?? "";
        s.Endpoint = Endpoint ?? "";
        s.Logs = Logs;
        s.LogErrors = LogErrors;
        s.Tracing = Tracing;
        s.TracesSampleRate = System.Math.Max(0, System.Math.Min(1, TracesSampleRate));
        s.DetectHangs = DetectHangs;
        s.HangThreshold = System.Math.Max(1, HangThreshold);
        s.NativeCrashes = NativeCrashes;
        s.Debug = Debug;
    }
}
