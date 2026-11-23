using System;
using System.Text;
using UnityEngine.Networking;
using ZharpCore;

namespace ZharpUnity
{
    /// <summary>
    /// Sends the outbox to <c>{endpoint}/v1/logs</c> and <c>/v1/traces</c> over UnityWebRequest (the
    /// one HTTP client every platform, WebGL included, has): one request at a time, oldest first,
    /// gzipped where the platform can. Driven from the main thread every frame.
    /// </summary>
    internal sealed class Transport
    {
        readonly Client client;
        readonly Delivery delivery = new Delivery();
        UnityWebRequest current;
        string currentFile;

        public Transport(Client client)
        {
            this.client = client;
        }

        public void Pump(bool force = false)
        {
            if (current != null)
            {
                if (!current.isDone) return;
                Completed();
            }
            if (!delivery.MaySend(force)) return;
            var files = client.Outbox.Files();
            if (files.Count == 0) return;
            string name = files[0];
            string body = client.Outbox.Read(name);
            if (string.IsNullOrEmpty(body))
            {
                client.Outbox.Remove(name);
                return;
            }
            byte[] data = Encoding.UTF8.GetBytes(body);
            byte[] gz = Platform.CanGzip ? Gzip.TryCompress(data) : null;
            var request = new UnityWebRequest(client.Settings.Endpoint + "/v1/" + Outbox.PathOf(name), "POST")
            {
                uploadHandler = new UploadHandlerRaw(gz ?? data),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 30,
            };
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + client.Settings.Key);
            if (gz != null) request.SetRequestHeader("Content-Encoding", "gzip");
            current = request;
            currentFile = name;
            try
            {
                request.SendWebRequest();
            }
            catch (Exception e)
            {
                client.DebugLog("sending failed: " + e.Message);
                request.Dispose();
                current = null;
                delivery.Backoff();
            }
        }

        void Completed()
        {
            UnityWebRequest request = current;
            string name = currentFile;
            current = null;
            try
            {
                int status = request.result == UnityWebRequest.Result.Success || request.result == UnityWebRequest.Result.ProtocolError ? (int)request.responseCode : 0;
                DeliveryOutcome outcome = delivery.Answered(status, request.GetResponseHeader("Retry-After"), out bool drop);
                if (drop) client.Outbox.Remove(name);
                switch (outcome)
                {
                    case DeliveryOutcome.KeyRejected:
                        Runtime.Current?.Warn("Zharp rejected the key (HTTP " + status + "): nothing is being sent. Check the key in Project Settings › Zharp.");
                        // A rejected key won't start working soon: what's queued is dropped, not kept.
                        client.Outbox.Clear();
                        break;
                    case DeliveryOutcome.Sent:
                        if (delivery.KeyWasRejected)
                        {
                            delivery.AcknowledgeKeyAccepted();
                            Runtime.Current?.Warn("Zharp accepted the key again: telemetry is being sent.");
                        }
                        break;
                    case DeliveryOutcome.Retry:
                        client.DebugLog("sending " + name + " got " + (status == 0 ? request.error : "HTTP " + status) + "; will retry");
                        break;
                    case DeliveryOutcome.Drop:
                        client.DebugLog("Zharp refused a batch (HTTP " + status + "): " + Cut(request.downloadHandler?.text));
                        break;
                }
            }
            finally
            {
                request.Dispose();
            }
        }

        static string Cut(string text) => string.IsNullOrEmpty(text) ? "" : text.Length > 200 ? text.Substring(0, 200) : text;
    }
}
