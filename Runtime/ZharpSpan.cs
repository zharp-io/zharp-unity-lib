using System;
using System.Collections.Generic;
using ZharpCore;
using ZharpUnity;

/// <summary>
/// A span: a piece of work with a start and an end, in a trace. Made by <c>Zharp.StartSpan()</c>
/// and <c>ZharpWebRequest.Send()</c>; end it with <see cref="End"/> (or dispose it).
/// <code>
/// using (Zharp.StartSpan("load_level", new Dictionary&lt;string, object&gt; { ["level"] = 3 }))
/// {
///     …
/// }
/// </code>
/// </summary>
public sealed class ZharpSpan : IDisposable
{
    [ThreadStatic] static List<ZharpSpan> open;

    readonly SpanRecord record = new SpanRecord();
    readonly object gate = new object();

    public string TraceId => record.TraceId;
    public string SpanId => record.SpanId;
    public string ParentSpanId => record.ParentSpanId;
    public string Name => record.Name;
    /// <summary>Whether the span is kept (the trace was sampled); an unsampled span is never sent.</summary>
    public bool Sampled { get; private set; }
    public bool Ended { get; private set; }

    ZharpSpan() { }

    /// <summary>The span innermost on this thread that hasn't ended.</summary>
    internal static ZharpSpan Current
    {
        get
        {
            if (open == null) return null;
            for (int i = open.Count - 1; i >= 0; i--) if (!open[i].Ended) return open[i];
            return null;
        }
    }

    internal static ZharpSpan Begin(string name, IDictionary<string, object> attributes, bool root, int kind = Otlp.KindInternal)
    {
        Runtime runtime = Runtime.Current;
        ZharpSpan parent = root ? null : Current;
        var span = new ZharpSpan();
        span.record.Name = name ?? "";
        span.record.Kind = kind;
        span.record.TraceId = parent != null ? parent.TraceId : Ids.Hex32();
        span.record.ParentSpanId = parent != null ? parent.SpanId : "";
        span.Sampled = parent != null ? parent.Sampled : runtime != null && runtime.Client.SampleTrace();
        span.record.SpanId = span.Sampled ? Ids.Hex16() : "0000000000000000";
        span.record.Start = Otlp.Now();
        if (attributes != null) foreach (var pair in attributes) span.record.Attributes[pair.Key] = pair.Value;
        open ??= new List<ZharpSpan>();
        open.Add(span);
        return span;
    }

    public ZharpSpan SetAttribute(string key, object value)
    {
        lock (gate) if (!Ended && !string.IsNullOrEmpty(key)) record.Attributes[key] = value;
        return this;
    }

    /// <summary>Mark the span failed.</summary>
    public ZharpSpan SetError(string message = "")
    {
        lock (gate)
        {
            record.StatusCode = Otlp.StatusError;
            record.StatusMessage = message ?? "";
        }
        return this;
    }

    public ZharpSpan AddEvent(string name, IDictionary<string, object> attributes = null)
    {
        lock (gate)
        {
            if (!Ended)
                record.Events.Add(new Dictionary<string, object>
                {
                    ["name"] = name ?? "",
                    ["timeUnixNano"] = Otlp.Nanos(Otlp.Now()),
                    ["attributes"] = Otlp.KeyValues(Otlp.Clean(attributes)),
                });
        }
        return this;
    }

    /// <summary>The W3C <c>traceparent</c> header for a request made under this span.</summary>
    public string Traceparent() => "00-" + TraceId + "-" + SpanId + "-" + (Sampled ? "01" : "00");

    public void End()
    {
        lock (gate)
        {
            if (Ended) return;
            Ended = true;
            record.End = Otlp.Now();
        }
        open?.Remove(this);
        Runtime runtime = Runtime.Current;
        if (Sampled && runtime != null) runtime.Client.SpanEnded(record);
    }

    public void Dispose() => End();
}
