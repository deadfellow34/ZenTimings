using System;
using System.Diagnostics;
using System.Runtime;
using System.Threading;

namespace ZenTimings
{
    /// <summary>Why a run produced nothing, in a form the window can translate.</summary>
    public enum BenchmarkError
    {
        None = 0,
        BufferOutOfRange,
        OutOfMemory,
        Cancelled,

        /// <summary>Anything else - only <see cref="MemoryLatencyResult.Error"/> describes it.</summary>
        Failed,
    }

    public class MemoryLatencyResult
    {
        public double Nanoseconds { get; set; }
        public double SpreadNs { get; set; }
        public int BufferMegabytes { get; set; }
        public bool Ok { get; set; }

        /// <summary>The exception text where there is one; the window prefers <see cref="Reason"/>.</summary>
        public string Error { get; set; }

        public BenchmarkError Reason { get; set; }
        public bool LargePages { get; set; }
        public bool Noisy { get; set; }
        public int PassCount { get; set; }
        public bool CacheBound { get; set; }
    }

    /// <summary>
    /// Measures how long one dependent memory access takes.
    /// </summary>
    /// <remarks>
    /// A pointer chase: the buffer is filled with a single random cycle through its own cache
    /// lines, each line holding the address of the next, so no access can start before the
    /// previous one finished and the random order defeats the prefetchers.
    ///
    /// The buffer is native, large-page backed when the "Lock pages in memory" right is held -
    /// large pages do not just lower the TLB cost of a random walk, they make it the same on
    /// every run, which is most of what separates a stable figure from a jittery one. The walk
    /// is carved into many ~16 ms slices rather than a few long passes: interference cannot be
    /// prevented, but short slices let the minimum land on slices it missed entirely, and how
    /// many slices agree with the minimum is reported as a noise verdict.
    /// </remarks>
    public static class MemoryLatencyTest
    {
        private const int CacheLineBytes = 64;

        /// <summary>~16 ms at 80 ns - short enough that many slices dodge the timer tick.</summary>
        private const int HopsPerPass = 200000;

        private const int Passes = 256;

        /// <summary>Slices within 1% of the minimum count as agreeing with it.</summary>
        private const double AgreeBand = 1.01;

        /// <summary>Below this fraction of agreeing slices the run is flagged as noisy.</summary>
        private const double NoisyThreshold = 0.10;

        /// <summary>How many times the L3 a buffer has to be before the walk is really in DRAM.</summary>
        private const int CacheBoundFactor = 4;

        /// <summary>L3 of the core the walk runs on - the one its working set has to clear.</summary>
        public static long L3Bytes
        {
            get { return BenchmarkNative.GetPinnedL3Bytes(); }
        }

        /// <summary>
        /// Whether a buffer that size measures the cache rather than the memory. One rule, so the
        /// size list cannot label a choice differently from the way the result is flagged.
        /// </summary>
        public static bool IsCacheBound(long bufferBytes, long l3Bytes)
        {
            return l3Bytes > 0 && bufferBytes < CacheBoundFactor * l3Bytes;
        }

        /// <summary>
        /// Blocking - run it on a background thread. <paramref name="progress"/> is called with
        /// 0..1 and may be null; <paramref name="cancelled"/> is polled between slices.
        /// </summary>
        public static unsafe MemoryLatencyResult Run(int bufferMegabytes, Action<double> progress, Func<bool> cancelled)
        {
            var result = new MemoryLatencyResult { BufferMegabytes = bufferMegabytes };

            if (bufferMegabytes < 16 || bufferMegabytes > 1024)
            {
                result.Error = "Buffer size out of range.";
                result.Reason = BenchmarkError.BufferOutOfRange;
                return result;
            }

            BenchmarkNative.NativeBuffer buffer = null;
            var previousLatencyMode = GCSettings.LatencyMode;
            ThreadPriority previousPriority = Thread.CurrentThread.Priority;
            UIntPtr previousAffinity = UIntPtr.Zero;
            bool affinityHeld = false;
            bool noGcHeld = false;
            bool timeCritical = false;

            try
            {
                long bytes = (long)bufferMegabytes * 1024 * 1024;
                int lines = (int)(bytes / CacheLineBytes);

                result.CacheBound = IsCacheBound(bytes, BenchmarkNative.GetPinnedL3Bytes());

                buffer = BenchmarkNative.NativeBuffer.Allocate(bytes);
                result.LargePages = buffer.LargePages;

                BuildChain((byte*)buffer.Pointer, lines, p => { if (progress != null) progress(p * 0.4); }, cancelled);

                if (cancelled != null && cancelled())
                {
                    result.Error = "Cancelled.";
                    result.Reason = BenchmarkError.Cancelled;
                    return result;
                }

                Thread.CurrentThread.Priority = ThreadPriority.Highest;

                // Pinned to the first logical processor of the last physical core. CPU 0 is the
                // interrupt magnet; a migration mid-slice lands on cold caches either way.
                Thread.BeginThreadAffinity();
                affinityHeld = true;
                previousAffinity = BenchmarkNative.SetThreadAffinityMask(
                    BenchmarkNative.GetCurrentThread(), (UIntPtr)BenchmarkNative.LastCoreFirstLpMask());

                using (new BenchmarkNative.PriorityScope())
                {
                    // A collection mid-slice would land in the measurement. The no-GC region is
                    // the strong form; the latency mode is the fallback when its budget is denied.
                    GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                    try { noGcHeld = GC.TryStartNoGCRegion(16 * 1024 * 1024); }
                    catch { noGcHeld = false; }

                    void* position = buffer.Pointer.ToPointer();

                    // Two full untimed walks: the first faults every page in on the measured core,
                    // the second runs with the clocks already ramping. Reported in slices so the
                    // progress bar keeps moving - on a 1 GB buffer these take seconds, which is
                    // also why Close has to be able to interrupt them.
                    for (int half = 0; half < 2; half++)
                    {
                        int walked = 0;
                        while (walked < lines)
                        {
                            int step = Math.Min(HopsPerPass, lines - walked);
                            position = Chase(position, step);
                            walked += step;

                            if (cancelled != null && cancelled())
                            {
                                result.Error = "Cancelled.";
                                result.Reason = BenchmarkError.Cancelled;
                                return result;
                            }

                            if (progress != null)
                                progress(0.40 + 0.05 * (half + walked / (double)lines));
                        }
                    }

                    // ~100 ms of timed-but-discarded slices so boost/C-state residency settles.
                    var settle = Stopwatch.StartNew();
                    while (settle.ElapsedMilliseconds < 100)
                        position = Chase(position, HopsPerPass);

                    if (cancelled != null && cancelled())
                    {
                        result.Error = "Cancelled.";
                        result.Reason = BenchmarkError.Cancelled;
                        return result;
                    }

                    if (progress != null)
                        progress(0.5);

                    BenchmarkNative.SetTimeCritical(true);
                    timeCritical = true;

                    var samples = new double[Passes];
                    int measured = 0;

                    // The chase pointer carries across slices, so the slices are time-windows of
                    // one continuous walk over the full buffer - short samples without shrinking
                    // the working set.
                    for (int pass = 0; pass < Passes; pass++)
                    {
                        var watch = Stopwatch.StartNew();
                        position = Chase(position, HopsPerPass);
                        watch.Stop();

                        samples[measured++] = watch.Elapsed.TotalMilliseconds * 1000000.0 / HopsPerPass;

                        if ((pass & 15) == 15)
                        {
                            if (cancelled != null && cancelled())
                                break;
                            if (progress != null)
                                progress(0.5 + 0.5 * (pass + 1) / Passes);
                        }
                    }

                    // Keeps the JIT from deciding the chase is dead code.
                    if (position == null)
                        throw new InvalidOperationException();

                    if (measured < 32)
                    {
                        result.Error = "Cancelled.";
                        result.Reason = BenchmarkError.Cancelled;
                        return result;
                    }

                    Array.Sort(samples, 0, measured);

                    // Interference is one-sided: it can only make a slice slower. The minimum is
                    // the closest look at the hardware; the p50 distance shows how often the run
                    // got that look.
                    double best = samples[0];
                    double median = samples[measured / 2];

                    int agree = 0;
                    for (int i = 0; i < measured; i++)
                        if (samples[i] <= best * AgreeBand)
                            agree++;

                    result.Nanoseconds = best;
                    result.SpreadNs = median - best;
                    result.PassCount = measured;
                    result.Noisy = agree < measured * NoisyThreshold;
                    result.Ok = true;
                }
            }
            catch (OutOfMemoryException)
            {
                result.Error = "Not enough free memory for that buffer size.";
                result.Reason = BenchmarkError.OutOfMemory;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.Reason = BenchmarkError.Failed;
            }
            finally
            {
                if (timeCritical)
                    BenchmarkNative.SetTimeCritical(false);

                if (noGcHeld)
                {
                    try { GC.EndNoGCRegion(); } catch { }
                }

                if (affinityHeld)
                {
                    try
                    {
                        if (previousAffinity != UIntPtr.Zero)
                            BenchmarkNative.SetThreadAffinityMask(BenchmarkNative.GetCurrentThread(), previousAffinity);
                    }
                    catch { }

                    Thread.EndThreadAffinity();
                }

                Thread.CurrentThread.Priority = previousPriority;
                GCSettings.LatencyMode = previousLatencyMode;

                if (buffer != null)
                    buffer.Dispose();
            }

            return result;
        }

        /// <summary>
        /// One random cycle visiting every cache line exactly once, written as real addresses:
        /// each line's first pointer-slot holds the address of the next line.
        /// </summary>
        /// <remarks>
        /// Leaves the chain half-written when cancelled; the caller checks and never walks it.
        /// </remarks>
        private static unsafe void BuildChain(byte* baseAddress, int lines, Action<double> progress,
            Func<bool> cancelled)
        {
            var order = BenchmarkNative.BuildShuffledCycle(lines);
            if (progress != null)
                progress(0.8);

            // order[k] -> order[k+1], closing back to order[0].
            for (int k = 0; k < lines; k++)
            {
                byte* from = baseAddress + (long)order[k] * CacheLineBytes;
                byte* to = baseAddress + (long)order[(k + 1) % lines] * CacheLineBytes;
                *(void**)from = to;

                // A 1 GB buffer is 16M lines - long enough that Close must not have to wait it out.
                if ((k & 0xFFFFF) == 0xFFFFF && cancelled != null && cancelled())
                    return;
            }

            if (progress != null)
                progress(1.0);
        }

        /// <summary>
        /// The measured loop: pure load-to-use, nothing else. Unrolled so the loop bookkeeping
        /// overlaps the misses instead of counting against every hop.
        /// </summary>
        private static unsafe void* Chase(void* start, int hops)
        {
            void* p = start;
            int i = 0;

            for (; i + 8 <= hops; i += 8)
            {
                p = *(void**)p;
                p = *(void**)p;
                p = *(void**)p;
                p = *(void**)p;
                p = *(void**)p;
                p = *(void**)p;
                p = *(void**)p;
                p = *(void**)p;
            }

            for (; i < hops; i++)
                p = *(void**)p;

            return p;
        }
    }
}
