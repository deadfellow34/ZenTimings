using System;
using System.Threading;

namespace ZenTimings
{
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
