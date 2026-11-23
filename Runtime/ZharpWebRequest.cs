using System;
using System.Collections.Generic;
using UnityEngine.Networking;
using ZharpCore;
using ZharpUnity;

/// <summary>
/// <c>UnityWebRequest</c>, traced: a client span with the method, URL, status and headers (secrets
/// filtered), and <c>traceparent</c> + <c>baggage</c> so your server's trace continues it. Unity has
/// no global hook for its requests, so each one you want traced goes through here.
/// <code>
/// using var request = UnityWebRequest.Get("https://api.example.com/scores");
/// yield return ZharpWebRequest.Send(request);
/// </code>
/// </summary>
public static class ZharpWebRequest
{
    /// <summary>Request headers recorded when set (Unity can't list the ones a request has).</summary>
    static readonly string[] KnownRequestHeaders = { "Accept", "Accept-Language", "Authorization", "Content-Type", "User-Agent", "X-Api-Key", "X-Request-Id", "Cookie", "Cache-Control", "If-None-Match" };

    /// <summary>Send the request (as <c>request.SendWebRequest()</c>), traced.</summary>
    public static UnityWebRequestAsyncOperation Send(UnityWebRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        Runtime runtime = Runtime.Current;
        string url = request.url ?? "";
        if (runtime == null || url.StartsWith(runtime.Client.Settings.Endpoint, StringComparison.OrdinalIgnoreCase))
            return request.SendWebRequest();

        ZharpSpan span = null;
        try
        {
            string method = string.IsNullOrEmpty(request.method) ? "GET" : request.method.ToUpperInvariant();
            span = ZharpSpan.Begin(method, new Dictionary<string, object>
            {
                ["http.request.method"] = method,
                ["url.full"] = Headers.RedactUrl(url),
                ["server.address"] = Headers.HostOf(url),
            }, false, Otlp.KindClient);
            var sent = new List<KeyValuePair<string, string>>();
            foreach (string name in KnownRequestHeaders)
            {
                string value = request.GetRequestHeader(name);
                if (!string.IsNullOrEmpty(value)) sent.Add(new KeyValuePair<string, string>(name, value));
            }
            foreach (var pair in Headers.Attributes("request", sent)) span.SetAttribute(pair.Key, pair.Value);
            if (runtime.Client.Settings.Tracing)
            {
                request.SetRequestHeader("traceparent", span.Traceparent());
                string baggage = "session.id=" + runtime.Client.Session.Current();
                if (runtime.Client.Context().TryGetValue("enduser.id", out object user)) baggage += ",enduser.id=" + Uri.EscapeDataString(user.ToString());
                request.SetRequestHeader("baggage", baggage);
            }
        }
        catch (Exception e)
        {
            runtime.Debug("ZharpWebRequest: " + e.Message);
        }

        UnityWebRequestAsyncOperation op = request.SendWebRequest();
        if (span != null) op.completed += _ => Finish(request, span);
        return op;
    }

    static void Finish(UnityWebRequest request, ZharpSpan span)
    {
        try
        {
            if (request.result == UnityWebRequest.Result.ConnectionError || request.result == UnityWebRequest.Result.DataProcessingError)
            {
                span.SetAttribute("error.type", request.result.ToString());
                span.SetError(request.error ?? "");
            }
            else
            {
                long status = request.responseCode;
                span.SetAttribute("http.response.status_code", (int)status);
                Dictionary<string, string> headers = request.GetResponseHeaders();
                if (headers != null)
                    foreach (var pair in Headers.Attributes("response", headers)) span.SetAttribute(pair.Key, pair.Value);
                if (status >= 400)
                {
                    span.SetAttribute("error.type", status.ToString());
                    span.SetError();
                }
            }
        }
        catch (Exception)
        {
            // A request disposed before it completed: nothing more to read.
        }
        span.End();
    }
}
