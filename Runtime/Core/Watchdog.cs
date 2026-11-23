using System;
using System.Diagnostics;
using System.Threading;

namespace ZharpCore
{
    /// <summary>
    /// Hangs, the way Android's watchdog sees them: the main thread not getting through a frame for
    /// the threshold (5 s) is reported once per freeze. Runs on its own thread, so it sees the freeze
    /// while it lasts; the report goes straight to disk, so a player who gives up and kills the game
    /// still has it sent on the next launch. Paused in the background, and while a debugger is
    /// attached (a breakpoint freezes the game too).
    /// </summary>
    internal sealed class Watchdog
    {
        readonly long thresholdMs;
        readonly Action<double> onHang;
        readonly Func<long> ticks;
        readonly Func<bool> debuggerAttached;
        readonly object gate = new object();
        long lastBeat;
        bool reported;
        volatile bool running;
        volatile bool paused;
        Thread thread;

        public Watchdog(double thresholdSeconds, Action<double> onHang, Func<long> ticksMs = null, Func<bool> debuggerAttached = null)
        {
            thresholdMs = (long)(thresholdSeconds * 1000);
            this.onHang = onHang;
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
            lock (gate)
            {
                lastBeat = ticks();
                reported = false;
            }
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
            double blocked;
            lock (gate)
            {
                long since = ticks() - lastBeat;
                if (reported || paused || since < thresholdMs) return;
                reported = true;
                blocked = since / 1000.0;
            }
            if (debuggerAttached()) return;
            try { onHang(blocked); }
            catch (Exception) { /* never into the game */ }
        }
    }
}
