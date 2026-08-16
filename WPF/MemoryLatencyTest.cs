using System;
using System.Collections.Generic;
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

        /// <summary>The walk read a value nothing wrote - the RAM returned corrupted data.</summary>
        MemoryError,

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

        /// <summary>Why not, when <see cref="LargePages"/> is false.</summary>
        public LargePageFailure LargePageFailure { get; set; }
        public int LargePageError { get; set; }
        public bool Noisy { get; set; }
        public bool CacheBound { get; set; }

        /// <summary>Whether the no-GC region held. Without it a collection can land in a slice -
        /// tail noise the minimum dodges, but worth knowing when a run reads noisy.</summary>
        public bool NoGcRegion { get; set; }

        /// <summary>How many cores the scan tried before settling on one.</summary>
        public int CoresScanned { get; set; }

        /// <summary>The core the scan settled on, so the cache ladder can measure the same one.</summary>
        public ulong CoreMask { get; set; }
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

        /// <summary>
        /// Enough, and measured to be. The figure is a minimum over the slices, so more of them can
        /// only lower it - but 512 was tried against 256 six runs each and the minimum did not
        /// move (104.0 against 104.1 ns, on a box whose run-to-run spread is 6 ns). The minimum has
        /// already found the floor by here; the extra slices cost seconds and buy nothing.
        /// </summary>
        private const int Passes = 256;

        /// <summary>Slices within 1% of the minimum count as agreeing with it.</summary>
        private const double AgreeBand = 1.01;

        /// <summary>Below this fraction of agreeing slices the run is flagged as noisy.</summary>
        private const double NoisyThreshold = 0.10;

        /// <summary>How many times the L3 a buffer has to be before the walk is really in DRAM.</summary>
        private const int CacheBoundFactor = 4;

        /// <summary>Slices per core while scanning - enough to rank them, not to publish one.</summary>
        private const int ScanPasses = 24;

        /// <summary>
        /// Scan minima within this of the best are a tie the scan cannot call: a 24-slice minimum
        /// carries sampling noise of the same order as the real gap between well-matched cores,
        /// and a wrong call puts the whole published figure on the slower core - measured at half
        /// a nanosecond of run-to-run band before the runoff existed.
        /// </summary>
        private const double RunoffBand = 1.015;

        /// <summary>Extra visits each tied core gets, and the slices per visit.</summary>
        private const int RunoffRounds = 2;
        private const int RunoffPasses = 32;

        /// <summary>
        /// Untimed slices between warm-up and measurement. Counted in hops, not by a clock: boost
        /// residency needs ~100 ms and twelve slices cover that on any machine this serves, while
        /// a wall-clock settle would start the timed slices at a different position of the cycle
        /// each run - the one nondeterminism the fixed chain seed left open.
        /// </summary>
        private const int SettleSlices = 12;

        /// <summary>
        /// Warm-up hops before a core is timed. Not a full walk: BuildChain has already written
        /// every line, so the pages are faulted in before any of this starts and what is left to
        /// warm is the core's own caches and its clocks. A full walk over a gigabyte would cost a
        /// second per core and buy nothing extra.
        /// </summary>
        private const int ScanWarmupHops = HopsPerPass * 4;

        private const int FinalWarmupHops = HopsPerPass * 16;

        /// <summary>Slices per hold of the bus mutexes - about half a second at 16 ms a slice.</summary>
        private const int BusPasses = 32;

        /// <summary>
        /// One mask per physical core: the first logical processor of each.
        /// </summary>
        /// <remarks>
        /// Cores are not interchangeable for this. They boost to different clocks, and on a part
        /// with more than one die they do not all sit the same distance from the memory controller
        /// - on a hybrid one they are not even the same core. Rather than guess, every core gets
        /// measured and the fastest wins.
        ///
        /// CPU 0 is included even though it carries the clock interrupt and most DPCs. It used to
        /// be avoided by rule; a scan does not need the rule, because a core that is busy being
        /// interrupted posts a worse time and loses on its own.
        /// </remarks>
        private static ulong[] Candidates()
        {
            // Only cores the process may actually run on. A pin outside the process affinity is
            // silently refused, so an unfiltered candidate would be "measured" on whatever core
            // the previous one left the thread pinned to - the scan then ranks one core against
            // itself and publishes a CoreMask the walk never ran on. The fallbacks go through the
            // same sieve: a fallback the filter would have rejected is the bug wearing a new mask.
            ulong allowed = BenchmarkNative.ProcessAffinityMask();

            var cores = BenchmarkNative.GetCoreMasks();
            if (cores == null || cores.Length == 0)
                return new[] { InsideAffinity(BenchmarkNative.LastCoreFirstLpMask(), allowed) };

            var masks = new List<ulong>(cores.Length);
            for (int i = 0; i < cores.Length; i++)
            {
                ulong first = cores[i] & (~cores[i] + 1);
                if (allowed == 0 || (first & allowed) != 0)
                    masks.Add(first);
            }

            // Empty happens when the affinity holds only SMT siblings, none of them a core's
            // first logical processor. Any allowed bit beats a "preferred" one the pin refuses.
            return masks.Count > 0
                ? masks.ToArray()
                : new[] { InsideAffinity(BenchmarkNative.LastCoreFirstLpMask(), allowed) };
        }

        /// <summary>The mask itself where the process may use it, otherwise its highest allowed bit.</summary>
        private static ulong InsideAffinity(ulong mask, ulong allowed)
        {
            if (allowed == 0 || (mask & allowed) != 0)
                return mask;

            ulong bit = 1UL << 63;
            while (bit != 0 && (bit & allowed) == 0)
                bit >>= 1;

            return bit != 0 ? bit : mask;
        }

        /// <summary>
        /// Whether a buffer that size measures the cache rather than the memory. Read against the
        /// L3 of the core the run actually landed on, once the scan has said which that is.
        /// </summary>
        public static bool IsCacheBound(long bufferBytes, long l3Bytes)
        {
            return l3Bytes > 0 && bufferBytes < CacheBoundFactor * l3Bytes;
        }

        /// <summary>
        /// Blocking - run it on a background thread. <paramref name="progress"/> is called with
        /// 0..1 and may be null; <paramref name="cancelled"/> is polled between slices.
        /// <paramref name="bus"/> is taken across batches of slices and handed back between them.
        /// </summary>
        public static unsafe MemoryLatencyResult Run(int bufferMegabytes, Action<double> progress,
            Func<bool> cancelled, HardwareLock bus = null)
        {
            var result = new MemoryLatencyResult { BufferMegabytes = bufferMegabytes };

            if (bufferMegabytes < 16 || bufferMegabytes > 2048)
            {
                result.Error = "Buffer size out of range.";
                result.Reason = BenchmarkError.BufferOutOfRange;
                return result;
            }

            // Asked before allocating: the commit would succeed against the pagefile, and a walk
            // that pages measures the disk instead of the RAM.
            if (!BenchmarkNative.FitsInFreeMemory((long)bufferMegabytes * 1024 * 1024))
            {
                result.Error = "Not enough free memory for that buffer size.";
                result.Reason = BenchmarkError.OutOfMemory;
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

                buffer = BenchmarkNative.NativeBuffer.Allocate(bytes);
                result.LargePages = buffer.LargePages;
                result.LargePageFailure = buffer.Failure;
                result.LargePageError = buffer.FailureCode;

                // First touch decides which physical frames back the buffer, and the free list
                // hands neighbouring frames to an in-order walk far more often than to the
                // shuffled order BuildChain writes in. On 4K pages the frame draw is a run-to-run
                // offset the minimum cannot remove; touching in address order narrows the draw.
                // Under large pages the mapping is settled at commit and this is a fast no-op.
                TouchInOrder((byte*)buffer.Pointer, bytes);

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
                    result.NoGcRegion = noGcHeld;

                    var candidates = Candidates();
                    ulong chosen = candidates[0];
                    result.CoresScanned = candidates.Length;

                    // Start of the published measurement's share of the bar; a runoff takes what
                    // is below it. Not a constant, because the runoff's length is not one either.
                    double publishFrom = 0.65;

                    // Scan first, publish second. A short run on each core is enough to rank them,
                    // and it costs far less than a full one - the chain is already built and the
                    // pages are already faulted in, so all a scan pays for is the walk.
                    if (candidates.Length > 1)
                    {
                        var floors = new double[candidates.Length];

                        for (int i = 0; i < candidates.Length; i++)
                        {
                            var scan = MeasureOn(buffer.Pointer, lines, candidates[i],
                                ScanPasses, ScanWarmupHops, ref timeCritical, cancelled, bus,
                                null, out bool scanCorrupted);

                            if (scanCorrupted)
                            {
                                result.Error = "The walk read a value nothing wrote - corrupted data from the RAM.";
                                result.Reason = BenchmarkError.MemoryError;
                                return result;
                            }

                            if (scan == null)
                            {
                                result.Error = "Cancelled.";
                                result.Reason = BenchmarkError.Cancelled;
                                return result;
                            }

                            floors[i] = scan[0];

                            if (progress != null)
                                progress(0.40 + 0.20 * (i + 1) / candidates.Length);
                        }

                        int lead = 0;
                        for (int i = 1; i < candidates.Length; i++)
                            if (floors[i] < floors[lead])
                                lead = i;

                        // Anything inside the band gets a deeper look - the leader included, in
                        // rounds, so the package's thermal drift lands on every contender rather
                        // than on whoever was measured last. A clear leader costs nothing extra.
                        var tied = new bool[candidates.Length];
                        int contenders = 0;
                        for (int i = 0; i < candidates.Length; i++)
                            if (floors[i] <= floors[lead] * RunoffBand)
                            {
                                tied[i] = true;
                                contenders++;
                            }

                        if (contenders > 1)
                        {
                            // Where the scan's last report left the bar.
                            const double runoffFrom = 0.60;

                            // Split by the slices each will walk: the runoff is the longest phase
                            // of the run - two rounds over sixteen contenders walk 1536 slices
                            // against the published measurement's 284 - and a fixed handover point
                            // gives the longest phase the narrowest band.
                            int visits = 0;
                            int totalVisits = RunoffRounds * contenders;
                            double runoffSlices = (double)totalVisits
                                * (ScanWarmupHops / HopsPerPass + SettleSlices + RunoffPasses);
                            double finalSlices = FinalWarmupHops / HopsPerPass + SettleSlices + Passes;
                            publishFrom = runoffFrom
                                + (1.0 - runoffFrom) * runoffSlices / (runoffSlices + finalSlices);

                            for (int round = 0; round < RunoffRounds; round++)
                                for (int i = 0; i < candidates.Length; i++)
                                {
                                    if (!tied[i])
                                        continue;

                                    // Reported per visit, not per runoff: a visit is most of a
                                    // second of walking, and a bar that stands still through a
                                    // whole runoff reads as a hung run.
                                    double visitFrom = runoffFrom
                                        + (publishFrom - runoffFrom) * visits / totalVisits;
                                    double visitSpan = (publishFrom - runoffFrom) / totalVisits;
                                    visits++;

                                    var deeper = MeasureOn(buffer.Pointer, lines, candidates[i],
                                        RunoffPasses, ScanWarmupHops, ref timeCritical, cancelled, bus,
                                        p => { if (progress != null) progress(visitFrom + visitSpan * p); },
                                        out bool runoffCorrupted);

                                    if (runoffCorrupted)
                                    {
                                        result.Error = "The walk read a value nothing wrote - corrupted data from the RAM.";
                                        result.Reason = BenchmarkError.MemoryError;
                                        return result;
                                    }

                                    if (deeper == null)
                                    {
                                        result.Error = "Cancelled.";
                                        result.Reason = BenchmarkError.Cancelled;
                                        return result;
                                    }

                                    if (deeper[0] < floors[i])
                                        floors[i] = deeper[0];
                                }

                            for (int i = 0; i < candidates.Length; i++)
                                if (tied[i] && floors[i] < floors[lead])
                                    lead = i;

                            if (progress != null)
                                progress(publishFrom);
                        }

                        chosen = candidates[lead];
                    }

                    // Decided here rather than before the scan: the answer depends on the cache of
                    // the core the run actually landed on, and until the scan has been there is no
                    // such core. On a package whose dies carry different amounts - 96 MB against
                    // 32 on a 9950X3D - guessing it up front gets it wrong half the time.
                    result.CacheBound = IsCacheBound(bytes, BenchmarkNative.GetL3Bytes(chosen));
                    result.CoreMask = chosen;

                    var samples = MeasureOn(buffer.Pointer, lines, chosen, Passes, FinalWarmupHops,
                        ref timeCritical, cancelled, bus,
                        p => { if (progress != null) progress(publishFrom + (1.0 - publishFrom) * p); },
                        out bool finalCorrupted);

                    if (finalCorrupted)
                    {
                        result.Error = "The walk read a value nothing wrote - corrupted data from the RAM.";
                        result.Reason = BenchmarkError.MemoryError;
                        return result;
                    }

                    if (samples == null || samples.Length < 32)
                    {
                        result.Error = "Cancelled.";
                        result.Reason = BenchmarkError.Cancelled;
                        return result;
                    }

                    if (progress != null)
                        progress(1.0);

                    // Interference is one-sided: it can only make a slice slower. The minimum is
                    // the closest look at the hardware; the p50 distance shows how often the run
                    // got that look.
                    double best = samples[0];
                    double median = samples[samples.Length / 2];

                    int agree = 0;
                    for (int i = 0; i < samples.Length; i++)
                        if (samples[i] <= best * AgreeBand)
                            agree++;

                    result.Nanoseconds = best;
                    result.SpreadNs = median - best;
                    result.Noisy = agree < samples.Length * NoisyThreshold;
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
        /// Pins to one core, warms it, and returns the slice times sorted. Null when cancelled or
        /// when <paramref name="corrupted"/> came back true.
        /// </summary>
        /// <remarks>
        /// Every core starts from the base of the buffer rather than carrying the previous core's
        /// pointer on: the walk is a closed cycle, so any position is as good as another, and a
        /// fixed start keeps two scans of the same machine comparable.
        ///
        /// The warm-up is a fixed number of hops rather than a walk of the whole buffer. Chasing a
        /// gigabyte to warm a 32 MB cache is a second of nothing.
        /// </remarks>
        private static unsafe double[] MeasureOn(IntPtr bufferStart, int lines, ulong mask,
            int passes, int warmupHops, ref bool timeCritical, Func<bool> cancelled, HardwareLock bus,
            Action<double> progress, out bool corrupted)
        {
            corrupted = false;

            // Holds by construction: every mask that reaches here came through Candidates(),
            // which drops anything outside the process affinity - the one condition under which
            // this call is silently refused.
            BenchmarkNative.SetThreadAffinityMask(BenchmarkNative.GetCurrentThread(), (UIntPtr)mask);

            byte* low = (byte*)bufferStart.ToPointer();

            // The last address an 8-byte load may START at, not the end of the buffer: a corrupt
            // pointer in the final seven bytes passes a one-past-the-end test and then straddles
            // the page after it. Every real chain value is line-aligned, so nothing valid is lost.
            byte* high = low + (long)lines * CacheLineBytes - (sizeof(void*) - 1);
            void* position = low;

            int warmed = 0;
            while (warmed < warmupHops)
            {
                int step = Math.Min(HopsPerPass, warmupHops - warmed);
                position = Chase(position, step, low, high);
                warmed += step;

                if (position == null)
                {
                    corrupted = true;
                    return null;
                }

                if (cancelled != null && cancelled())
                    return null;
            }

            // Untimed slices so boost residency arrives before the timed ones. A fixed count:
            // with the warm-up also counted in hops, every run reaches its first timed slice at
            // the same position of the cycle.
            for (int slice = 0; slice < SettleSlices; slice++)
            {
                position = Chase(position, HopsPerPass, low, high);
                if (position == null)
                {
                    corrupted = true;
                    return null;
                }
            }

            if (cancelled != null && cancelled())
                return null;

            BenchmarkNative.SetTimeCritical(true);
            timeCritical = true;

            var samples = new double[passes];
            int measured = 0;
            bool busHeld = false;

            // The chase pointer carries across slices, so the slices are time-windows of one
            // continuous walk over the full buffer - short samples without shrinking the working
            // set.
            try
            {
                for (int pass = 0; pass < passes; pass++)
                {
                    // Handed back every so often rather than held for the whole loop: the final
                    // measurement is four seconds of slices, and a monitoring tool made to wait
                    // that long for one poll stops waiting and reads anyway.
                    if (bus != null && pass % BusPasses == 0)
                    {
                        if (busHeld)
                            bus.Exit();

                        busHeld = bus.Enter(HardwareLock.WindowWaitMs);
                    }

                    var watch = Stopwatch.StartNew();
                    position = Chase(position, HopsPerPass, low, high);
                    watch.Stop();

                    if (position == null)
                    {
                        corrupted = true;
                        break;
                    }

                    samples[measured++] = watch.Elapsed.TotalMilliseconds * 1000000.0 / HopsPerPass;

                    if ((pass & 15) == 15)
                    {
                        if (cancelled != null && cancelled())
                            break;

                        if (progress != null)
                            progress((pass + 1) / (double)passes);
                    }
                }
            }
            finally
            {
                if (busHeld)
                    bus.Exit();
            }

            BenchmarkNative.SetTimeCritical(false);
            timeCritical = false;

            // The chase cannot be dead code: every hop feeds the range compare, and the final
            // pointer decides these returns.
            if (corrupted || measured == 0)
                return null;

            Array.Resize(ref samples, measured);
            Array.Sort(samples);
            return samples;
        }

        /// <summary>One write per 4K page, in address order, before the chain is laid down.</summary>
        private static unsafe void TouchInOrder(byte* baseAddress, long bytes)
        {
            for (long offset = 0; offset < bytes; offset += 4096)
                baseAddress[offset] = 1;
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
        /// <remarks>
        /// Null when a loaded pointer leaves the buffer. Nothing writes this chain, so that is the
        /// RAM handing back corrupted data - on an overclock, the verdict that matters most. The
        /// compare sits beside the next load, not in front of it: a predicted-not-taken branch off
        /// the dependency chain disappears under an 80 ns miss, so the measurement is unchanged.
        /// </remarks>
        private static unsafe void* Chase(void* start, int hops, void* low, void* high)
        {
            void* p = start;
            int i = 0;

            for (; i + 8 <= hops; i += 8)
            {
                p = *(void**)p; if (p < low || p >= high) return null;
                p = *(void**)p; if (p < low || p >= high) return null;
                p = *(void**)p; if (p < low || p >= high) return null;
                p = *(void**)p; if (p < low || p >= high) return null;
                p = *(void**)p; if (p < low || p >= high) return null;
                p = *(void**)p; if (p < low || p >= high) return null;
                p = *(void**)p; if (p < low || p >= high) return null;
                p = *(void**)p; if (p < low || p >= high) return null;
            }

            for (; i < hops; i++)
            {
                p = *(void**)p;
                if (p < low || p >= high)
                    return null;
            }

            return p;
        }
    }
}
