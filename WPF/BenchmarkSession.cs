using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace ZenTimings
{
    /// <summary>
    /// Holds the bus mutexes the monitoring tools honour, so nothing else reads hardware while a
    /// measurement runs.
    /// </summary>
    /// <remarks>
    /// Suspending our own polling only silences this process. HWiNFO, AIDA64, CPU-Z and Ryzen
    /// Master all poll the same SMBus and PCI config space, and their traffic lands in the
    /// measurement exactly the way ours did. These three names are the de-facto standard those
    /// tools take before touching a bus; holding them for the run is the only way to ask them to
    /// wait, and ZenStates-Core already opens the same three for its own reads.
    ///
    /// A mutex belongs to the thread that took it, so this is created, entered, left and disposed
    /// on the measurement thread and nowhere else. One that cannot be had inside the timeout is
    /// simply left alone - a window measured with a neighbour polling is worth more than no window.
    ///
    /// Held per timed window rather than for the whole run: forty seconds is long past the point
    /// where a monitoring tool stops waiting and reads anyway, and between windows there is
    /// nothing to protect.
    /// </remarks>
    public sealed class HardwareLock : IDisposable
    {
        /// <summary>
        /// Long enough for a neighbour to finish the poll it is inside, short enough that waiting
        /// out a busy bus does not stretch the window that follows it.
        /// </summary>
        public const int WindowWaitMs = 250;

        private static readonly string[] Names =
        {
            "Global\\Access_PCI",
            "Global\\Access_SMBUS.HTP.Method",
            "Global\\Access_ISABUS.HTP.Method",
        };

        /// <summary>Opened once for the run. Taking a mutex is cheap; opening one is not, and this
        /// is entered a few hundred times.</summary>
        private readonly List<Mutex> handles = new List<Mutex>();

        private readonly List<Mutex> held = new List<Mutex>();

        /// <summary>Names that could not be opened at all; those buses go unguarded.</summary>
        public List<string> Missed { get; private set; }

        public HardwareLock()
        {
            Missed = new List<string>();

            foreach (var name in Names)
            {
                try
                {
                    var mutex = OpenOrCreate(name);
                    if (mutex != null)
                        handles.Add(mutex);
                    else
                        Missed.Add(name);
                }
                catch
                {
                    Missed.Add(name);
                }
            }
        }

        /// <summary>
        /// Takes what it can inside the timeout, and says whether anything is now held.
        /// </summary>
        /// <remarks>
        /// The timeout is the budget for all three together, not for each: three waits of their own
        /// would put three quarters of a second in front of a window a fifth that long. A partial
        /// take is kept rather than rolled back - two buses quiet is better than none.
        /// </remarks>
        public bool Enter(int millisecondsTimeout)
        {
            if (held.Count > 0)
                return true;

            var watch = Stopwatch.StartNew();

            foreach (var mutex in handles)
            {
                int left = millisecondsTimeout - (int)watch.ElapsedMilliseconds;
                if (left < 0)
                    left = 0;

                // An abandoned mutex still transfers ownership - the tool that held it died.
                bool got;
                try { got = mutex.WaitOne(left, false); }
                catch (AbandonedMutexException) { got = true; }
                catch { got = false; }

                if (got)
                    held.Add(mutex);
            }

            return held.Count > 0;
        }

        public void Exit()
        {
            foreach (var mutex in held)
            {
                try { mutex.ReleaseMutex(); } catch { }
            }

            held.Clear();
        }

        private static Mutex OpenOrCreate(string name)
        {
            try
            {
                return Mutex.OpenExisting(name, MutexRights.Synchronize | MutexRights.Modify);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // Nobody has made it yet. World access, or a tool running as a different user
                // cannot honour it - which is the whole point of a global name.
                var security = new MutexSecurity();
                security.AddAccessRule(new MutexAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    MutexRights.FullControl,
                    AccessControlType.Allow));

                bool created;
                return new Mutex(false, name, out created, security);
            }
        }

        public void Dispose()
        {
            Exit();

            foreach (var mutex in handles)
            {
                try { mutex.Dispose(); } catch { }
            }

            handles.Clear();
        }
    }

    /// <summary>
    /// The state of "a benchmark is measuring right now", owned by the app rather than by the
    /// window that happens to start one.
    /// </summary>
    /// <remarks>
    /// Everything here used to be static fields on MemoryLatencyWindow, which meant the main
    /// window, the Options dialog, the telemetry window and the OC tools all reached into a UI
    /// type to ask whether they were allowed to poll - and the cancel flag, being private to that
    /// window, was out of reach of the app's own shutdown path.
    ///
    /// <see cref="Suspend"/> and <see cref="Resume"/> are set once by the main window: it is the
    /// one that knows when polling is allowed to run at all (it stays off while minimized), so
    /// the benchmark asks rather than decides.
    /// </remarks>
    internal static class BenchmarkSession
    {
        private static int running;
        private static volatile bool cancelRequested;

        /// <summary>Set by the main window; the benchmark never touches the timer itself.</summary>
        public static Action Suspend;
        public static Action Resume;

        public static bool Running
        {
            get { return Interlocked.CompareExchange(ref running, 0, 0) != 0; }
        }

        /// <summary>One benchmark per process - a second would fight the first for the same core.</summary>
        public static bool TryEnter()
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
                return false;

            cancelRequested = false;
            return true;
        }

        public static void Leave()
        {
            Interlocked.Exchange(ref running, 0);
            cancelRequested = false;
        }

        /// <summary>Asks the measurement to stop at its next check - closing the window, or exiting.</summary>
        public static void RequestCancel()
        {
            cancelRequested = true;
        }

        public static bool CancelRequested
        {
            get { return cancelRequested; }
        }

        /// <summary>
        /// Identity of the run this process measured, so the HTML export can tell it from an
        /// entry loaded out of the file - the window on screen only describes the former.
        /// </summary>
        public static volatile string RunKey;

        public static void SuspendPolling()
        {
            var suspend = Suspend;
            if (suspend != null)
                suspend();
        }

        public static void ResumePolling()
        {
            var resume = Resume;
            if (resume != null)
                resume();
        }
    }
}
