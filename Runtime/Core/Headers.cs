using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ZharpCore
{
    /// <summary>
    /// Request and response headers on HTTP client spans, as OpenTelemetry names them
    /// (<c>http.request.header.user-agent</c>). Secret headers are recorded as <c>[Filtered]</c>: sent,
    /// but the value never leaves the device. The same rules as every other Zharp SDK.
    /// </summary>
    internal static class Headers
    {
        public const string Filtered = "[Filtered]";
        const int MaxHeaders = 40;
        const int MaxValue = 512;

        static readonly string[] SecretHeaders = { "authorization", "proxy-authorization", "cookie", "set-cookie", "x-api-key", "x-auth-token", "x-csrf-token", "x-xsrf-token" };
        /// <summary>Keys whose values never leave the app, compared without case, '-' or '_'.</summary>
        static readonly string[] Contains = { "password", "passwd", "passphrase", "secret", "token", "apikey", "accesskey", "privatekey",
            "authorization", "cookie", "creditcard", "cardnumber", "cvv", "cvc", "ssn", "iban", "sessionid", "signature", "credential" };
        static readonly string[] Exact = { "auth", "pin", "pwd", "otp", "card", "pan", "jwt", "sid", "codeverifier" };
        /// <summary>Also secret in a query string, where they are API keys and signed-URL signatures.</summary>
        static readonly string[] UrlExact = { "key", "sig", "code", "state", "session" };

        public static string Normalize(string key)
        {
            var sb = new StringBuilder();
            foreach (char c in (key ?? "").ToLowerInvariant())
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            return sb.ToString();
        }

        public static bool IsSecretKey(string key)
        {
            string k = Normalize(key);
            if (k.Length == 0) return false;
            if (Exact.Contains(k)) return true;
            foreach (string part in Contains) if (k.Contains(part)) return true;
            return false;
        }

        /// <summary><c>http.{side}.header.{name}</c> attributes; secrets filtered, long values cut.</summary>
        public static Dictionary<string, object> Attributes(string side, IEnumerable<KeyValuePair<string, string>> headers)
        {
            var joined = new Dictionary<string, List<string>>();
            var order = new List<string>();
            if (headers != null)
            {
                foreach (var pair in headers)
                {
                    string name = (pair.Key ?? "").Trim().ToLowerInvariant();
                    if (name.Length == 0) continue;
                    if (!joined.TryGetValue(name, out List<string> values))
                    {
                        if (joined.Count >= MaxHeaders) continue;
                        joined[name] = values = new List<string>();
                        order.Add(name);
                    }
                    values.Add((pair.Value ?? "").Trim());
                }
            }
            var output = new Dictionary<string, object>();
            foreach (string name in order)
            {
                string value = string.Join(", ", joined[name]);
                if (SecretHeaders.Contains(name) || IsSecretKey(name)) value = Filtered;
                else if (value.Length > MaxValue) value = value.Substring(0, MaxValue) + "…";
                output["http." + side + ".header." + name] = value;
            }
            return output;
        }

        /// <summary>A URL with secret query values and any user:password replaced.</summary>
        public static string RedactUrl(string url)
        {
            string output = url ?? "";
            int scheme = output.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0)
            {
                int start = scheme + 3;
                int slash = output.IndexOf('/', start);
                string authority = slash >= 0 ? output.Substring(start, slash - start) : output.Substring(start);
                int at = authority.LastIndexOf('@');
                if (at >= 0) output = output.Substring(0, start) + Filtered + "@" + output.Substring(start + at + 1);
            }
            int q = output.IndexOf('?');
            if (q < 0) return output;
            int hash = output.IndexOf('#', q);
            string query = hash >= 0 ? output.Substring(q + 1, hash - q - 1) : output.Substring(q + 1);
            var parts = new List<string>();
            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                string key = Uri.UnescapeDataString(eq >= 0 ? pair.Substring(0, eq) : pair);
                if (eq >= 0 && (IsSecretKey(key) || UrlExact.Contains(Normalize(key)))) parts.Add(pair.Substring(0, eq) + "=" + Filtered);
                else parts.Add(pair);
            }
            return output.Substring(0, q + 1) + string.Join("&", parts) + (hash >= 0 ? output.Substring(hash) : "");
        }

        /// <summary>The host of a URL, for <c>server.address</c>.</summary>
        public static string HostOf(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ? uri.Host : "";
        }
    }
}
