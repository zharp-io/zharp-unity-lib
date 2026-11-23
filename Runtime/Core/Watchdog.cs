using System;
using System.Diagnostics;
using System.Threading;

namespace ZharpCore
{
    /// <summary>
    /// Hangs, the way Android's watchdog sees them: the main thread not getting through a frame for
    /// the threshold (5 s) is reported once per freeze. Runs on its own thread, so it sees the freeze
    /// while it lasts; the report goes straight to disk as pending, so a player who gives up and kills
    /// the game still has it on the next launch. The next frame means the freeze is over
    /// (<c>onRecovered</c>): only then is it sent, so a crash that froze the game on its way down
    /// isn't also a hang. Paused in the background, and while a debugger is attached (a breakpoint
    /// freezes the game too).
    /// </summary>
    internal sealed class Watchdog
    {
        readonly long thresholdMs;
        readonly Action<double> onHang;
        readonly Action onRecovered;
        readonly Func<long> ticks;
        readonly Func<bool> debuggerAttached;
        readonly object gate = new object();
        long lastBeat;
        bool reported;
        bool hung;
        volatile bool running;
        volatile bool paused;
        Thread thread;

        public Watchdog(double thresholdSeconds, Action<double> onHang, Func<long> ticksMs = null, Func<bool> debuggerAttached = null, Action onRecovered = null)
        {
            thresholdMs = (long)(thresholdSeconds * 1000);
            this.onHang = onHang;
            this.onRecovered = onRecovered;
            var clock = Stopwatch.StartNew();
            ticks = ticksMs ?? (() => clock.ElapsedMilliseconds);
            this.debuggerAttached = debuggerAttached ?? (() => Debugger.IsAttached);
            lastBeat = ticks();
        }

        public bool Paused
        {
            get => paused;
            set
            {
                paused = value;
                if (!value) Beat();
            }
        }

        public void Start()
        {
            if (running) return;
            running = true;
            Beat();
            thread = new Thread(Run) { IsBackground = true, Name = "Zharp watchdog", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        /// <summary>The main thread got through a frame.</summary>
        public void Beat()
        {
            bool recovered;
            lock (gate)
            {
                lastBeat = ticks();
                reported = false;
                recovered = hung;
                hung = false;
            }
            if (!recovered || onRecovered == null) return;
            try { onRecovered(); }
            catch (Exception) { /* never into the game */ }
        }

        public void Stop()
        {
            running = false;
        }

        void Run()
        {
            while (running)
            {
                Thread.Sleep(200);
                Check();
            }
        }

        /// <summary>One look at the main thread; the thread calls it every 200 ms (tests call it directly).</summary>
        public void Check()
        {
            // The report is written under the lock: a frame that ends the freeze meanwhile waits for
            // it, so its onRecovered always finds the hang on disk.
            lock (gate)
            {
                long since = ticks() - lastBeat;
                if (reported || paused || since < thresholdMs) return;
                reported = true;
                if (debuggerAttached()) return;
                hung = true;
                try { onHang(since / 1000.0); }
                catch (Exception) { /* never into the game */ }
            }
        }
    }
}
