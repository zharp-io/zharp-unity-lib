using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace ZharpCore
{
    /// <summary>
    /// Every batch is written here before it's sent, and deleted once Zharp has it, so nothing is
    /// lost to a crash, a quit or no network: what's left is sent on the next launch. One file per
    /// request body, named so the oldest sorts first; at most <see cref="MaxFiles"/>, the oldest
    /// dropped first. Safe from any thread.
    /// </summary>
    internal sealed class Outbox
    {
        public const int MaxFiles = 200;

        readonly string dir;
        int seq;

        public Outbox(string dir)
        {
            this.dir = dir;
        }

        public string Directory => dir;

        /// <summary>A request body (OTLP JSON) for <c>/v1/logs</c> or <c>/v1/traces</c> to disk.</summary>
        public bool Write(string path, string body)
        {
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                long ms = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond - 62135596800000L;
                int n = Interlocked.Increment(ref seq);
                string name = ms.ToString("D14", CultureInfo.InvariantCulture) + "-" + (n % 1000000).ToString("D6", CultureInfo.InvariantCulture) + "-" + path + ".json";
                string temp = Path.Combine(dir, name + ".tmp");
                File.WriteAllText(temp, body);
                // Renamed once complete: a crash mid-write leaves a .tmp, never half a batch to send.
                File.Move(temp, Path.Combine(dir, name));
                Trim(null);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The batches waiting, oldest first.</summary>
        public List<string> Files()
        {
            try
            {
                if (!System.IO.Directory.Exists(dir)) return new List<string>();
                var names = System.IO.Directory.GetFiles(dir, "*.json").Select(Path.GetFileName).ToList();
                names.Sort(StringComparer.Ordinal);
                return names;
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        /// <summary>"logs" or "traces": the OTLP path a batch goes to.</summary>
        public static string PathOf(string name) => name.EndsWith("-traces.json", StringComparison.Ordinal) ? "traces" : "logs";

        public string Read(string name)
        {
            try { return File.ReadAllText(Path.Combine(dir, name)); }
            catch (Exception) { return null; }
        }

        public void Remove(string name)
        {
            try { File.Delete(Path.Combine(dir, name)); }
            catch (Exception) { }
        }

        public void Clear()
        {
            foreach (string name in Files()) Remove(name);
        }

        /// <summary>The oldest past the cap go, except the one being sent.</summary>
        public void Trim(string sending)
        {
            List<string> files = Files();
            for (int i = 0; i < files.Count - MaxFiles; i++)
                if (files[i] != sending) Remove(files[i]);
        }
    }

    /// <summary>What to do after a send.</summary>
    internal enum DeliveryOutcome
    {
        Sent,
        /// <summary>Kept and tried again after <see cref="Delivery.RetryAt"/>.</summary>
        Retry,
        /// <summary>The server will never take it (400…): deleted.</summary>
        Drop,
        /// <summary>401/403: the key is wrong or revoked; the queue is cleared.</summary>
        KeyRejected,
    }

    /// <summary>
    /// When to send and what to do with an answer, as every Zharp SDK does it:
    /// - one request at a time, oldest first;
    /// - network failures, 408, 429 and 5xx are retried with backoff (1 s doubling to 5 min, or the
    ///   server's Retry-After); a batch the server keeps failing on goes after 5 tries;
    /// - 400 and other client errors drop the batch;
    /// - 401/403 print one warning and drop what's queued, trying again an hour later.
    /// </summary>
    internal sealed class Delivery
    {
        public const int MaxServerFailures = 5;
        public const double KeyRetrySeconds = 3600;

        readonly Func<double> clock;
        readonly Random random = new Random();
        int failures;
        int serverFailures;

        public double RetryAt { get; private set; }
        public double KeyRejectedUntil { get; private set; }
        /// <summary>Set once a key was rejected; cleared when it's accepted again.</summary>
        public bool KeyWasRejected { get; private set; }

        public Delivery(Func<double> clock = null)
        {
            this.clock = clock ?? Otlp.Now;
        }

        public bool MaySend(bool force = false)
        {
            double now = clock();
            if (now < KeyRejectedUntil) return false;
            return force || now >= RetryAt;
        }

        /// <summary>
        /// The outcome of one send: <paramref name="status"/> 0 for no answer (offline, DNS, TLS).
        /// <paramref name="dropBatch"/>: whether the batch must go from the outbox.
        /// </summary>
        public DeliveryOutcome Answered(int status, string retryAfter, out bool dropBatch)
        {
            dropBatch = false;
            if (status == 401 || status == 403)
            {
                KeyRejectedUntil = clock() + KeyRetrySeconds;
                KeyWasRejected = true;
                dropBatch = true;
                return DeliveryOutcome.KeyRejected;
            }
            if (status >= 200 && status < 300)
            {
                failures = 0;
                serverFailures = 0;
                RetryAt = 0;
                KeyRejectedUntil = 0;
                dropBatch = true;
                return DeliveryOutcome.Sent;
            }
            if (status == 0 || status == 408 || status == 429 || status >= 500)
            {
                if (status != 0 && ++serverFailures >= MaxServerFailures)
                {
                    serverFailures = 0;
                    dropBatch = true;
                }
                Backoff(ParseRetryAfter(retryAfter));
                return DeliveryOutcome.Retry;
            }
            dropBatch = true;
            return DeliveryOutcome.Drop;
        }

        /// <summary>The next wait after a failure, in seconds.</summary>
        public double Backoff(double retryAfter = 0)
        {
            failures++;
            double wait = retryAfter > 0 ? retryAfter : Math.Min(Math.Pow(2, Math.Min(failures - 1, 20)), 300) * (0.8 + random.NextDouble() * 0.4);
            RetryAt = clock() + wait;
            return wait;
        }

        /// <summary>Send again now, whatever the backoff says (a flush).</summary>
        public void ClearBackoff() => RetryAt = 0;

        public void AcknowledgeKeyAccepted() => KeyWasRejected = false;

        static double ParseRetryAfter(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            if (double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)) return Math.Min(seconds, 3600);
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime at))
                return Math.Max(0, Math.Min((at - DateTime.UtcNow).TotalSeconds, 3600));
            return 0;
        }
    }
}
