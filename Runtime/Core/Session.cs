using System;
using System.Globalization;
using System.IO;

namespace ZharpCore
{
    /// <summary>
    /// <c>session.id</c> and <c>device.id</c>, as the mobile SDKs keep them.
    ///
    /// A session ends after 30 minutes in the background, on <c>ClearUser()</c>, and when the game
    /// starts again more than 30 minutes after it was last seen; a game restarted sooner keeps it.
    /// Both IDs are 32 random hex characters, kept in Zharp's folder.
    /// </summary>
    internal sealed class Session
    {
        public static readonly double TimeoutSeconds = 30 * 60;
        const double SaveEverySeconds = 60;

        readonly string dir;
        readonly Func<double> clock;
        readonly object gate = new object();
        double backgroundSince = -1;
        double savedAt;

        public string Id { get; private set; }
        public string DeviceId { get; }

        /// <summary>Called with the new ID when a session starts (not the first).</summary>
        public Action<string> OnNewSession;

        public Session(string dir, Func<double> clock = null)
        {
            this.dir = dir;
            this.clock = clock ?? Otlp.Now;
            DeviceId = ReadOrCreate(dir == null ? null : Path.Combine(dir, "device-id"));
            Id = Load();
            if (string.IsNullOrEmpty(Id)) Id = Ids.Hex32();
            Save();
        }

        double Now() => clock();

        /// <summary>The ID for something made now: a new one if the game was in the background too long.</summary>
        public string Current()
        {
            string rotated = null;
            lock (gate)
            {
                if (backgroundSince >= 0 && Now() - backgroundSince >= TimeoutSeconds)
                {
                    rotated = Ids.Hex32();
                    Id = rotated;
                    backgroundSince = Now();
                    SaveLocked();
                }
                else if (Now() - savedAt >= SaveEverySeconds)
                {
                    SaveLocked();
                }
            }
            if (rotated != null) OnNewSession?.Invoke(rotated);
            return Id;
        }

        public void Background()
        {
            lock (gate)
            {
                if (backgroundSince < 0) backgroundSince = Now();
                SaveLocked();
            }
        }

        public void Foreground()
        {
            Current();
            lock (gate)
            {
                backgroundSince = -1;
                SaveLocked();
            }
        }

        public bool InBackground
        {
            get { lock (gate) return backgroundSince >= 0; }
        }

        /// <summary>A new session now (sign-out).</summary>
        public void Reset()
        {
            string id = Ids.Hex32();
            lock (gate)
            {
                Id = id;
                SaveLocked();
            }
            OnNewSession?.Invoke(id);
        }

        /// <summary>Remember the time, so a game started again soon after keeps the session.</summary>
        public void Save()
        {
            lock (gate) SaveLocked();
        }

        void SaveLocked()
        {
            savedAt = Now();
            if (dir == null) return;
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "session"), Id + "\n" + ((long)(savedAt * 1000)).ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception) { /* storage full or read-only: the session lives in memory */ }
        }

        string Load()
        {
            if (dir == null) return null;
            try
            {
                string path = Path.Combine(dir, "session");
                if (!File.Exists(path)) return null;
                string[] parts = File.ReadAllText(path).Split('\n');
                if (parts.Length < 2 || parts[0].Trim().Length != 32) return null;
                if (!long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms)) return null;
                return Now() - ms / 1000.0 < TimeoutSeconds ? parts[0].Trim() : null;
            }
            catch (Exception) { return null; }
        }

        static string ReadOrCreate(string path)
        {
            try
            {
                if (path != null && File.Exists(path))
                {
                    string saved = File.ReadAllText(path).Trim();
                    if (saved.Length == 32) return saved;
                }
            }
            catch (Exception) { }
            string made = Ids.Hex32();
            try
            {
                if (path != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, made);
                }
            }
            catch (Exception) { }
            return made;
        }
    }
}
