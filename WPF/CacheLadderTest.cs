using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ZenTimings
{
    /// <summary>One rung: the level, its real size, the buffer that measured it, and the cost.</summary>
    public class CacheRung
    {
        public string Level { get; set; }

        /// <summary>What the level actually is - 48 KB of Zen 5 L1d, not the 24 KB walked.</summary>
        public long CacheBytes { get; set; }

        public long Bytes { get; set; }
        public double Nanoseconds { get; set; }

        /// <summary>Streaming read out of that level, one core. Zero when AVX was unavailable.</summary>
        public double ReadGBs { get; set; }

        /// <summary>
        /// The logical processors sharing this cache, for the rungs that belong to one die of a
        /// multi-die part. Zero means "wherever the run is pinned" - every private level, and the
        /// L3 fallback when the topology will not answer.
        /// </summary>
        internal ulong Domain;

        /// <summary>Ordinary vector stores into the level, one core. Not the streaming stores the
        /// DRAM test uses - those bypass the cache by definition.</summary>
        public double WriteGBs { get; set; }

        /// <summary>Half the rung copied onto the other half, both directions counted - the same
        /// convention as the DRAM copy score. One core.</summary>
        public double CopyGBs { get; set; }

        /// <summary>Every core reading at once. Zero when it could not be measured honestly.</summary>
        public double ReadAllGBs { get; set; }

        /// <summary>Every core writing at once.</summary>
        public double WriteAllGBs { get; set; }

        /// <summary>Every core copying at once.</summary>
        public double CopyAllGBs { get; set; }

        /// <summary>
        /// Whether the buffers behind this row's published figures got large pages - the rung's
        /// own and the all-core workers' alike. It decides the physical mapping, and with it which
        /// cache sets the buffer lands in, so a rung that fell back is not measuring quite the
        /// same thing as one that did not.
        /// </summary>
        public bool LargePages { get; set; }

        /// <summary>
        /// Whether the rung's OWN buffer got them - the one the latency and the one-core figures
        /// were both measured on. Apart from <see cref="LargePages"/> because the all-core pass
        /// clears that one for its workers' buffers too, and a caveat about what the walk paid
        /// belongs only to a row whose walk paid it.
        /// </summary>
        internal bool OwnLargePages;

        /// <summary>
        /// How many cores produced the all-core figures, of how many the domain holds. Equal on
        /// most parts; fewer workers on a small L3, where a full split would put each core's
        /// slice inside its own L2 and the row would measure the wrong level. Zero workers with
        /// a nonzero total means even two slices sat in the L2s and the figures were withheld.
        /// </summary>
        public int AllCoreWorkers { get; set; }
        public int AllCoreOf { get; set; }

        /// <summary>
        /// Cores the package holds in group 0 - the whole crew the all-core pass could draw on.
        /// The three counts are a ladder: <see cref="AllCoreWorkers"/> &lt;= <see cref="AllCoreOf"/>
        /// &lt;= this. Equal to the domain wherever the crew IS the package's: every private
        /// level, an L3 that serves the whole package, and the L3 fallback with no topology to
        /// read. Larger on a rung pinned inside one cache of several, where the crew behind the
        /// row is not the one the table's "all cores" heading claims. Zero where the pass never
        /// got far enough to establish one, which is also where there are no figures to qualify.
        /// </summary>
        public int PackageCores { get; set; }
    }

    public class CacheLadderResult
    {
        public List<CacheRung> Rungs { get; set; }
        public bool Ok { get; set; }
        public string Error { get; set; }

        /// <summary>
        /// Bits per vector in the loops that produced the throughput figures, or zero when there
        /// were none. It travels with the result because the width is chosen per machine: two
        /// runs measured at different widths are not the same measurement, and a table that does
        /// not say which was used cannot be compared with another machine's.
        /// </summary>
        public int VectorBits { get; set; }

        public CacheLadderResult()
        {
            Rungs = new List<CacheRung>();
        }
    }

    /// <summary>
    /// Latency at each level of the hierarchy, from a dependent pointer chase over a buffer sized
    /// to sit inside that level.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="MemoryLatencyTest"/> rather than another buffer size
    /// on it. The two measure different things and cannot share a scale: a cache figure is nothing
    /// to compare against the DRAM ceilings, its buffer is far below the size the DRAM walk needs
    /// to be honest, and it wants no ceiling filter at all.
    ///
    /// Each rung uses HALF the level's size. Full size spills - the replacement policy does not
    /// keep a cyclic walk of exactly capacity - and the figure lands between two levels; half sits
    /// clear of the boundary. Measured on a Zen+ APU: 16 KB read 1.03 ns against a 32 KB L1,
    /// 256 KB read 3.16 ns against a 512 KB L2, 2 MB read 10.6 ns against a 4 MB L3, and a 128 MB
    /// buffer read 89.7 ns. At full size the same L3 read 26.7 ns, which is no level at all.
    /// </remarks>
    public static class CacheLadderTest
    {
        private const int CacheLineBytes = 64;

        /// <summary>Half the level, to stay clear of the boundary.</summary>
        private const int LevelDivisor = 2;

        /// <summary>Slices per rung; the answer is the lowest of them.</summary>
        private const int Passes = 64;

        /// <summary>A slice long enough to time cleanly, short enough to dodge a tick.</summary>
        private const double SliceMs = 2.0;

        /// <summary>
        /// Chased before timing starts, so the level under test is the one being read. Capped:
        /// four cycles of a 128 MB rung is eight million dependent misses, most of a second spent
        /// warming a level that has nothing to warm - the pages were faulted in building the chain.
        /// </summary>
        private const int WarmupCycles = 4;

        private const int MaxWarmupHops = 400000;

        /// <summary>Rough slice, only to learn what a hop costs here. Sized for DRAM, not for L1.</summary>
        private const int SizingHops = 20000;

        /// <summary>
        /// Bytes moved per timed bandwidth window - under a millisecond at any level. Short and
        /// many rather than long and few: a window is one uninterruptible native call, so a thread
        /// descheduled inside one inflates it whole. Eight windows of a few ms let a run land 30%
        /// low on L2 often enough to put it under L3, which no cache can do.
        /// </summary>
        private const long ReadBytesPerWindow = 64L * 1024 * 1024;

        /// <summary>
        /// Windows per rung; the answer is the fastest of them. Forty-eight rather than twenty-four
        /// because the interference that matters here is not per-window jitter but a state that
        /// lasts: when it lands, every window of a rung reads about a third low together, and the
        /// only escape is for the rung to outlive it. At 24 that happened to about one rung
        /// measurement in twenty.
        /// </summary>
        private const int ReadWindows = 48;

        // The pinned rungs' share of the ladder's own band. A pinned rung is a chase plus three
        // kernels; the all-core pass over the same rung is three kernels and the threads to run
        // them on, so the split is weighted toward the pinned pass.
        private const double PinnedShare = 0.7;

        public static CacheLadderResult Run(ulong affinityMask, Action<double> progress,
            Func<bool> cancelled)
        {
            var result = new CacheLadderResult();

            // Caches only. The bottom of the ladder is the walk that already ran: a fixed sample
            // past "any" cache cannot exist - 128 MB is 32 times a 4 MB L3 and 1.3 times a 96 MB
            // one, so on an X3D part it would report the L3 again under a DRAM label.
            var sizes = new List<CacheRung>();
            for (int level = 1; level <= 2; level++)
            {
                long cache = BenchmarkNative.GetCacheBytes(level, affinityMask);
                long bytes = cache / LevelDivisor;

                // "L1d", because the walk only ever touches the data cache and the instruction
                // cache is skipped. Every other tool adds the two and calls the sum L1 - 80 KB on
                // Zen 5, 96 KB on Zen+ - so an unqualified "L1: 48 KB" reads as a detection fault.
                if (bytes >= 16 * 1024)
                {
                    sizes.Add(new CacheRung
                    {
                        Level = level == 1 ? "L1d" : "L" + level,
                        CacheBytes = cache,
                        Bytes = bytes,
                    });
                }
            }

            // One L3 rung per DIFFERENT cache in the package, each carrying its own domain so the
            // measurement pins inside it. On a 7950X3D that is a 96 MB row and a 32 MB row - one
            // die's cache is not the other's, an average of the two describes a cache that does
            // not exist, and a single row sized from whichever die the walk landed on gave the
            // same machine a different answer run to run. Symmetric parts collapse to one row.
            AddL3Rungs(sizes, affinityMask);

            return RunRungs(sizes, result, affinityMask, progress, cancelled);
        }

        /// <summary>
        /// One rung per different L3 in the package. Distinct SIZES, not one per die: two
        /// identical dies would print two identical rows, which is noise - but a 96 MB die next
        /// to a 32 MB one is two different caches, and each gets measured pinned inside its own.
        /// The pinned core's domain goes first, so single-domain machines keep their behaviour
        /// to the bit.
        /// </summary>
        private static void AddL3Rungs(List<CacheRung> sizes, ulong affinityMask)
        {
            var groups = BenchmarkNative.GetL3Groups();
            if (groups == null || groups.Length == 0)
            {
                // No topology: one rung from the pinned core's own L3, measured where the run
                // already sits.
                long cache = BenchmarkNative.GetCacheBytes(3, affinityMask);
                if (cache / LevelDivisor >= 16 * 1024)
                    sizes.Add(new CacheRung { Level = "L3", CacheBytes = cache, Bytes = cache / LevelDivisor });
                return;
            }

            var ordered = new List<ulong>();
            foreach (var group in groups)
                if ((group & affinityMask) != 0)
                    ordered.Add(group);
            foreach (var group in groups)
                if ((group & affinityMask) == 0)
                    ordered.Add(group);

            var seen = new List<long>();
            foreach (var group in ordered)
            {
                long cache = BenchmarkNative.GetCacheBytes(3, group);
                long bytes = cache / LevelDivisor;
                if (bytes < 16 * 1024 || seen.Contains(cache))
                    continue;

                seen.Add(cache);
                sizes.Add(new CacheRung { Level = "L3", CacheBytes = cache, Bytes = bytes, Domain = group });
            }
        }

        private static CacheLadderResult RunRungs(List<CacheRung> sizes, CacheLadderResult result,
            ulong affinityMask, Action<double> progress, Func<bool> cancelled)
        {

            var previous = Thread.CurrentThread.Priority;
            UIntPtr previousAffinity = UIntPtr.Zero;
            bool affinityHeld = false;

            try
            {
                Thread.BeginThreadAffinity();
                if (affinityMask != 0)
                {
                    // A refused pin returns zero and moves nothing; claiming it was held would
                    // restore a zero mask on the way out and clear the thread's affinity to
                    // nothing it had before.
                    previousAffinity = BenchmarkNative.SetThreadAffinityMask(
                        BenchmarkNative.GetCurrentThread(), (UIntPtr)affinityMask);
                    affinityHeld = previousAffinity != UIntPtr.Zero;
                }

                Thread.CurrentThread.Priority = ThreadPriority.Highest;

                // The same scope the walk and the sweep run under. Without it the ladder was the
                // only part of the run left at normal priority, and it showed: L2 landing under
                // L3 on a busy machine, which is a neighbour's timeslice, not a cache.
                using (new BenchmarkNative.PriorityScope())
                {
                    Measure(sizes, result, affinityMask, progress, cancelled);
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            finally
            {
                Thread.CurrentThread.Priority = previous;

                if (affinityHeld)
                {
                    if (previousAffinity != UIntPtr.Zero)
                        BenchmarkNative.SetThreadAffinityMask(
                            BenchmarkNative.GetCurrentThread(), previousAffinity);

                    Thread.EndThreadAffinity();
                }
            }

            return result;
        }

        // One phase's own 0..1 onto its share of the ladder's band. A phase with nothing in it
        // has no bar to move, and a null callback is a caller that does not want one.
        private static void Report(Action<double> progress, double from, double to,
            int done, int total)
        {
            if (progress != null && total > 0)
                progress(from + (to - from) * done / total);
        }

        private static void Measure(List<CacheRung> sizes, CacheLadderResult result, ulong pin,
            Action<double> progress, Func<bool> cancelled)
        {
            int reached = 0;
            foreach (var rung in sizes)
            {
                if (cancelled != null && cancelled())
                {
                    result.Error = "Cancelled.";
                    return;
                }

                // Counted before the skips below rather than after the measurement, so a rung
                // this thread cannot reach does not hold the bar back for the ones behind it.
                reached++;

                // A rung that belongs to the OTHER die is measured from inside it: reading a
                // 96 MB cache from a core on the 32 MB die would cross the fabric and measure
                // the trip, not the cache. Skipped outright when the process affinity allows no
                // core there - a wrong-die number is worse than a missing row.
                bool moved = false;
                if (rung.Domain != 0 && (rung.Domain & pin) == 0)
                {
                    ulong target = DomainPin(rung.Domain);
                    if (target == 0)
                        continue;

                    moved = BenchmarkNative.SetThreadAffinityMask(
                        BenchmarkNative.GetCurrentThread(), (UIntPtr)target) != UIntPtr.Zero;
                    if (!moved)
                        continue;
                }

                try
                {
                    MeasureRung(rung);
                }
                catch
                {
                    // A rung's own allocations are the biggest of the run - 48 MB of buffer with
                    // the chain's index array beside it - so a level can fail where the smaller
                    // ones below it succeeded. It costs its own row and nothing more.
                }
                finally
                {
                    if (moved && pin != 0)
                        BenchmarkNative.SetThreadAffinityMask(
                            BenchmarkNative.GetCurrentThread(), (UIntPtr)pin);
                }

                // Outside the try: a level that threw part-way keeps what it finished, and the
                // figures it never reached stay zero - a dash on the table, not a number.
                if (rung.Nanoseconds > 0)
                    result.Rungs.Add(rung);

                Report(progress, 0, PinnedShare, reached, sizes.Count);
            }

            // After the pinned rungs, because it spreads over every core rather than the one this
            // thread is held on. Read, write and copy each with every core loading at once - the
            // machine-wide figure AIDA reports, alongside the single-core one.
            int done = 0;
            bool stopped = false;
            foreach (var rung in result.Rungs)
            {
                if (cancelled != null && cancelled())
                {
                    stopped = true;
                    break;
                }

                rung.ReadAllGBs = MeasureAllCores(rung, AllKind.Read);
                if (cancelled != null && cancelled())
                {
                    stopped = true;
                    break;
                }

                rung.WriteAllGBs = MeasureAllCores(rung, AllKind.Write);
                if (cancelled != null && cancelled())
                {
                    stopped = true;
                    break;
                }

                rung.CopyAllGBs = MeasureAllCores(rung, AllKind.Copy);
                Report(progress, PinnedShare, 1.0, ++done, result.Rungs.Count);
            }

            // A ladder cut short here has its rungs but not all their all-core figures, and the
            // pinned loop above refuses to publish for the same reason. Ok would have carried it
            // into the history file as a complete one, with the missing columns reading as
            // withheld rather than never taken.
            if (stopped)
            {
                result.Error = "Cancelled.";
                return;
            }

            result.Ok = result.Rungs.Count > 0;
            if (!result.Ok)
                result.Error = "No cache level could be measured.";
            else if (result.Rungs.Exists(r => r.ReadGBs > 0))
                result.VectorBits = VectorRead.VectorBits;
        }

        /// <summary>
        /// Both figures for one level, over ONE buffer. Two would put twice the rung in the level
        /// at the moment the second is measured: at the L2 rung that is 256 KB of chain still
        /// resident beside 256 KB of stream, which together are the whole 512 KB cache, and the
        /// read then lands a third low often enough to drop under the L3 rung below it.
        /// </summary>
        private static unsafe void MeasureRung(CacheRung rung)
        {
            // Large pages where the right is held, like everything else here - Allocate asks for
            // them first and only falls back. It buys nothing at the L1d rung, which is four pages
            // either way, but the L3 rung is 512 of them on the fallback against a 64-entry L1
            // DTLB, and the walks that miss it resolve a level down: about 1-1.5 ns on a 10 ns
            // figure. So the fallback reads HIGH, and nothing in the row says which path it took.
            using (var buffer = BenchmarkNative.NativeBuffer.Allocate(rung.Bytes))
            {
                int lines = (int)(rung.Bytes / CacheLineBytes);
                if (lines < 256)
                    return;

                // Twice: the all-core pass clears the row flag when its own workers fall back, and
                // the mode this buffer ran on has to survive that.
                rung.LargePages = buffer.LargePages;
                rung.OwnLargePages = buffer.LargePages;

                byte* low = (byte*)buffer.Pointer.ToPointer();
                byte* high = low + (long)lines * CacheLineBytes - (sizeof(void*) - 1);

                rung.Nanoseconds = MeasureLatency(low, high, lines);
                if (rung.Nanoseconds <= 0)
                    return;

                // Read first, over the chain the walk left resident; write then overwrites the
                // chain, which nothing needs any more; copy moves whatever the write left. All
                // three stay inside the one buffer, so the level under test never changes.
                rung.ReadGBs = MeasureRead(low, rung.Bytes);
                rung.WriteGBs = MeasureWrite(low, rung.Bytes);
                rung.CopyGBs = MeasureCopy(low, rung.Bytes);
            }
        }

        /// <summary>
        /// What the level can hand one core, in decimal GB/s. Zero when the vector loop could not
        /// be built - a scalar fallback is not offered, because it measures itself rather than the
        /// cache and reports the same figure at every level.
        /// </summary>
        private static unsafe double MeasureRead(byte* p, long bytes)
        {
            if (!VectorRead.Available)
                return 0;

            // The loop reads a whole block per pass and tests afterwards, so a range that is not a
            // multiple of one would run past the end.
            long usable = bytes / VectorRead.BlockBytes * VectorRead.BlockBytes;
            if (usable <= 0)
                return 0;

            // Over the chain the walk left behind - what the bytes mean does not matter to a
            // streaming read, and rewriting them would only evict what is already in place.
            //
            // Passes per timed window, so a 16 KB rung and a 48 MB one both take about the same
            // time to measure rather than a hundredth and a second. The count goes INTO the loop:
            // paying the managed-to-native transition per pass costs about as much as an L1-sized
            // pass itself, which is enough to put L1d under L2.
            long passes = Math.Max(1, ReadBytesPerWindow / usable);

            long sink = 0;
            VectorRead.Read(p, usable, 1);     // resident before the clock starts

            double best = 0;
            for (int window = 0; window < ReadWindows; window++)
            {
                var watch = Stopwatch.StartNew();
                sink |= VectorRead.Read(p, usable, passes);
                watch.Stop();

                double seconds = watch.Elapsed.TotalSeconds;
                if (seconds <= 0)
                    continue;

                double gbs = (double)usable * passes / seconds / 1e9;
                if (gbs > best)
                    best = gbs;
            }

            Sink = sink;
            return best;
        }

        /// <summary>Somewhere for the read loop's answer to go, so it cannot be called for nothing.</summary>
        private static volatile object Sink;

        /// <summary>Ordinary vector stores over the range, same windowing as the read.</summary>
        private static unsafe double MeasureWrite(byte* p, long bytes)
        {
            if (!VectorRead.Available)
                return 0;

            long usable = bytes / VectorRead.BlockBytes * VectorRead.BlockBytes;
            if (usable <= 0)
                return 0;

            long passes = Math.Max(1, ReadBytesPerWindow / usable);
            VectorRead.Write(p, usable, 1);

            double best = 0;
            for (int window = 0; window < ReadWindows; window++)
            {
                var watch = Stopwatch.StartNew();
                VectorRead.Write(p, usable, passes);
                watch.Stop();

                double seconds = watch.Elapsed.TotalSeconds;
                if (seconds <= 0)
                    continue;

                double gbs = (double)usable * passes / seconds / 1e9;
                if (gbs > best)
                    best = gbs;
            }

            return best;
        }

        /// <summary>
        /// One half of the rung copied onto the other, counted both ways like the DRAM copy. The
        /// halves live in the SAME buffer, so source and destination together are still the rung
        /// and the level under test stays the level being measured.
        /// </summary>
        private static unsafe double MeasureCopy(byte* p, long bytes)
        {
            if (!VectorRead.Available)
                return 0;

            long half = bytes / 2 / VectorRead.BlockBytes * VectorRead.BlockBytes;
            if (half <= 0)
                return 0;

            // A pass moves both halves, so the window is sized from twice the range - from the
            // range alone it would run twice as long as every other timed window here.
            long passes = Math.Max(1, ReadBytesPerWindow / (half * 2));
            VectorRead.Copy(p, p + half, half, 1);

            double best = 0;
            for (int window = 0; window < ReadWindows; window++)
            {
                var watch = Stopwatch.StartNew();
                VectorRead.Copy(p, p + half, half, passes);
                watch.Stop();

                double seconds = watch.Elapsed.TotalSeconds;
                if (seconds <= 0)
                    continue;

                double gbs = (double)half * 2 * passes / seconds / 1e9;
                if (gbs > best)
                    best = gbs;
            }

            return best;
        }

        /// <summary>
        /// One pinnable logical processor inside the domain, honouring the process affinity.
        /// Zero when the domain holds none the process may use.
        /// </summary>
        /// <remarks>
        /// Last core first, for the same reason the walk's own fallback prefers it: CPU 0 carries
        /// the clock interrupt and most DPCs, and the foreign die's first core is often exactly
        /// that. And ANY allowed processor in the intersection will do, not just a core's first -
        /// an affinity holding only SMT siblings must not cost the die its row.
        /// </remarks>
        private static ulong DomainPin(ulong domain)
        {
            var cores = BenchmarkNative.GetCoreMasks();
            if (cores == null)
                return 0;

            ulong allowed = BenchmarkNative.ProcessAffinityMask();
            for (int i = cores.Length - 1; i >= 0; i--)
            {
                ulong usable = cores[i] & domain;
                if (allowed != 0)
                    usable &= allowed;

                if (usable != 0)
                    return usable & (~usable + 1);
            }

            return 0;
        }

        private enum AllKind { Read, Write, Copy }

        /// <summary>One pass of the chosen kernel. Returns the read's answer, zero for the others -
        /// their stores are side effects the JIT cannot elide, so no sink is needed.</summary>
        private static unsafe long Kernel(AllKind kind, byte* p, long bytes, long half, long passes)
        {
            switch (kind)
            {
                case AllKind.Write:
                    VectorRead.Write(p, bytes, passes);
                    return 0;
                case AllKind.Copy:
                    VectorRead.Copy(p, p + half, half, passes);
                    return 0;
                default:
                    return VectorRead.Read(p, bytes, passes);
            }
        }

        /// <summary>
        /// Every core reading its level at once. L1d and L2 are private, so each core gets its own
        /// buffer and the total is what the cores can pull in parallel. The L3 is one pool shared
        /// by the die, so the cores split its size between them instead.
        /// </summary>
        /// <remarks>
        /// A slice that fits inside a core's own L2 is reading L2 under an L3 label, so on a small
        /// L3 workers are shed until each slice clears twice the L2 - a 4 MB L3 beside 512 KB L2s
        /// measures with two cores instead of four, and the rung records the crew so the table can
        /// say so. When even two slices would sink into the L2s the figures are withheld outright:
        /// zero rather than a wrong number, as everywhere else here.
        ///
        /// Private buffers throughout, including for the L3 - the slice size is what makes the
        /// shared case shared, not one allocation. Cores reading the same lines would be served by
        /// one another rather than by the cache under test.
        /// </remarks>
        private static unsafe double MeasureAllCores(CacheRung rung, AllKind kind)
        {
            if (!VectorRead.Available)
                return 0;

            var cores = BenchmarkNative.GetCoreMasks();
            if (cores == null || cores.Length < 2)
                return 0;

            // "Every core" is group 0's cores. Windows caps a group at 64 logical processors, so
            // on a part with more than that the figure would be a real measurement of half the
            // package under a heading that says all of it. Say nothing instead.
            if (BenchmarkNative.PhysicalCoreCount() > cores.Length)
                return 0;

            // Read before the filter below replaces the list: past it the package count is gone,
            // and it is the only thing that says whether the crew behind this row is the one the
            // heading claims. The guard above is what makes group 0's count the package's.
            int packageCores = cores.Length;

            // An L3 rung belongs to ONE die, so only that die's cores take part and they split
            // that die's own size - a 96 MB row read by the 32 MB die's cores would cross the
            // fabric, and a package-wide split both halves the figure on a symmetric two-CCX
            // part and over-fills the small die of an asymmetric one.
            if (rung.Domain != 0)
            {
                var mine = new List<ulong>(cores.Length);
                foreach (var core in cores)
                    if ((core & rung.Domain) != 0)
                        mine.Add(core);

                cores = mine.ToArray();
                if (cores.Length < 2)
                    return 0;
            }

            bool shared = rung.Level == "L3";
            int domainCores = cores.Length;

            // A slice that fits in the private level above is not measuring this one - so on a
            // small L3, shed workers until each slice clears twice the L2 rather than publish
            // nothing. Two is the floor: one core is the other table's row.
            if (shared)
            {
                long l2 = BenchmarkNative.GetCacheBytes(2);
                long floor = Math.Max(VectorRead.BlockBytes, l2 > 0 ? l2 * 2 : 0);

                int crew = cores.Length;
                while (crew > 2
                    && rung.Bytes / crew / VectorRead.BlockBytes * VectorRead.BlockBytes < floor)
                    crew--;

                if (rung.Bytes / crew / VectorRead.BlockBytes * VectorRead.BlockBytes < floor)
                {
                    // Even two ways the slices sit inside the L2s: there is no honest all-core
                    // figure on this part. The zeroed crew is what lets the table say why.
                    rung.AllCoreOf = domainCores;
                    rung.PackageCores = packageCores;
                    return 0;
                }

                if (crew < cores.Length)
                    Array.Resize(ref cores, crew);
            }

            rung.AllCoreOf = domainCores;
            rung.AllCoreWorkers = cores.Length;
            rung.PackageCores = packageCores;

            long perCore = shared ? rung.Bytes / cores.Length : rung.Bytes;
            perCore = perCore / VectorRead.BlockBytes * VectorRead.BlockBytes;

            if (perCore < VectorRead.BlockBytes)
                return 0;

            // Copy needs the level to hold source AND destination at once, so it works over half
            // the slice each way. Both halves are in one buffer, so the footprint is still perCore
            // and the level under test does not change. Bytes moved per pass counts both ways.
            long half = perCore / 2 / VectorRead.BlockBytes * VectorRead.BlockBytes;
            if (kind == AllKind.Copy && half < VectorRead.BlockBytes)
                return 0;

            long bytesPerPass = kind == AllKind.Copy ? half * 2 : perCore;

            var buffers = new BenchmarkNative.NativeBuffer[cores.Length];
            var workers = new Thread[cores.Length];

            // Each worker's own timings, one row per core, padded apart so two cores never write
            // the same cache line - the false sharing would be measured as the cache under test.
            var seconds = new double[cores.Length][];
            var ready = new CountdownEvent(cores.Length);
            var go = new ManualResetEventSlim(false);

            // One rendezvous per window, so window w is the same moment on every core. Left to
            // drift, a core that finished its windows stops loading the machine, and best-of
            // then lands on whichever window had the fewest neighbours still reading. A worker
            // that dies drops out of the barrier rather than blocking it.
            //
            // Zero participants here; each worker registers just before it starts. Sized to the
            // core count up front, an allocation that failed part-way would leave the started
            // workers waiting sixty seconds for participants that never existed - past the join
            // budget, so the buffers would leak on top of the stall.
            var barrier = new Barrier(0);
            long sink = 0;
            bool allLp = true;

            try
            {
                // Per core, not divided by the core count again: each one moves this much, so the
                // window is the same length whether there are four cores or sixteen.
                long passes = Math.Max(1, ReadBytesPerWindow / bytesPerPass);

                for (int i = 0; i < cores.Length; i++)
                {
                    int index = i;
                    ulong mask = cores[i];
                    buffers[i] = BenchmarkNative.NativeBuffer.Allocate(perCore);
                    allLp &= buffers[i].LargePages;
                    seconds[i] = new double[ReadWindows];

                    var worker = new Thread(() =>
                    {
                        // Whatever happens, the countdown completes: a fault in the fill would
                        // otherwise leave the coordinator waiting out its whole 30 s timeout for
                        // a worker that already died.
                        bool signalled = false;
                        try
                        {
                            // A mask outside the process affinity leaves the thread where it was
                            // and returns zero. Unchecked, several workers would share a core, run
                            // in disjoint timeslices - each looking uncontended - and the total
                            // would still be multiplied by the full core count.
                            if (BenchmarkNative.SetThreadAffinityMask(
                                    BenchmarkNative.GetCurrentThread(), (UIntPtr)mask) == UIntPtr.Zero)
                            {
                                seconds[index] = null;
                                Drop(barrier);
                                return;
                            }

                            byte* p = (byte*)buffers[index].Pointer.ToPointer();
                            for (long b = 0; b < perCore; b += sizeof(long))
                                *(long*)(p + b) = b;

                            // Resident before the clock, in the kernel that will be timed.
                            Kernel(kind, p, perCore, half, 1);
                            signalled = true;
                            ready.Signal();
                            go.Wait();

                            long local = 0;
                            var watch = new Stopwatch();

                            // Each core times its own windows. The coordinator cannot: it would
                            // have to bracket the workers' whole lives, and thread teardown costs
                            // more than an L1-sized window takes.
                            for (int w = 0; w < ReadWindows; w++)
                            {
                                // The wait sits between timed regions, so its cost never lands
                                // in a window.
                                if (!barrier.SignalAndWait(60000))
                                {
                                    seconds[index] = null;
                                    Drop(barrier);
                                    return;
                                }

                                watch.Restart();
                                local |= Kernel(kind, p, perCore, half, passes);
                                watch.Stop();
                                seconds[index][w] = watch.Elapsed.TotalSeconds;
                            }

                            Interlocked.Add(ref sink, local);
                        }
                        catch
                        {
                            seconds[index] = null;
                            Drop(barrier);
                        }
                        finally
                        {
                            if (!signalled)
                                ready.Signal();
                        }
                    })
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.Highest,
                    };

                    // Register, then start, and compensate if the start throws. The other order
                    // raced: a worker that dies before go.Wait - a refused pin does exactly that -
                    // calls Drop on its way out, and if that lands before this thread's own
                    // AddParticipant it removes SOMEONE ELSE'S registration, leaving a phantom
                    // participant no thread will ever signal and a sixty-second stall on every
                    // phase. Registered first, a worker's Drop can only ever follow its own entry.
                    // Join stays safe on the failure path because the slot is nulled again before
                    // the throw.
                    barrier.AddParticipant();
                    workers[i] = worker;
                    try
                    {
                        worker.Start();
                    }
                    catch
                    {
                        workers[i] = null;
                        barrier.RemoveParticipant();
                        throw;
                    }
                }

                if (!ready.Wait(30000))
                    return 0;

                go.Set();

                // Every one, not up to the first that stalls: the finally can only free the pages
                // once it knows nobody is still in them, and abandoning the rest unjoined would
                // guarantee it never can.
                bool joined = true;
                foreach (var worker in workers)
                    joined &= worker.Join(60000);

                if (!joined)
                    return 0;

                Sink = sink;

                // A window is only worth what its slowest core took - that is when every core was
                // still loading. Best window wins, the same rule the single-core rungs use.
                double best = 0;
                long moved = bytesPerPass * passes * cores.Length;

                for (int w = 0; w < ReadWindows; w++)
                {
                    double slowest = 0;
                    for (int i = 0; i < cores.Length; i++)
                    {
                        if (seconds[i] == null)
                            return 0;

                        if (seconds[i][w] > slowest)
                            slowest = seconds[i][w];
                    }

                    if (slowest <= 0)
                        continue;

                    double gbs = moved / slowest / 1e9;
                    if (gbs > best)
                        best = gbs;
                }

                // The row's star comes from the rung's flag, which so far only knew the
                // single-core buffer. A fragmented large-page pool can give the workers 4K
                // buffers after the single-core one succeeded - a few percent low on the L3
                // cell with nothing marking it.
                if (best > 0 && !allLp)
                    rung.LargePages = false;

                return best;
            }
            catch
            {
                return 0;
            }
            finally
            {
                // The same rule the bandwidth pool uses: pages go back only once every worker that
                // touched them has come home. A constructor that threw part-way leaves the earlier
                // workers inside their first-touch write, and freeing under one is a store into
                // released address space - swallowed by the worker's own catch, and the range is a
                // plausible pick for the next rung's allocation.
                go.Set();

                bool allOut = true;
                foreach (var worker in workers)
                    if (worker != null)
                        allOut &= worker.Join(5000);

                if (allOut)
                {
                    foreach (var buffer in buffers)
                        if (buffer != null)
                            buffer.Dispose();

                    go.Dispose();
                    ready.Dispose();
                    barrier.Dispose();
                }
            }
        }

        /// <summary>Leaves the barrier without caring whether this thread was still in it.</summary>
        private static void Drop(Barrier barrier)
        {
            try { barrier.RemoveParticipant(); }
            catch { }
        }

        private static unsafe double MeasureLatency(byte* low, byte* high, int lines)
        {
            {
                var order = BenchmarkNative.BuildShuffledCycle(lines);
                for (int i = 0; i < lines; i++)
                {
                    *(void**)(low + (long)order[i] * CacheLineBytes) =
                        low + (long)order[(i + 1) % lines] * CacheLineBytes;
                }

                // The walk never restarts. Every slice carries on from where the last one stopped,
                // so it keeps moving through the cycle instead of re-reading its head - which on
                // the DRAM rung is a subset small enough to sit in the L3 from the second pass on,
                // and it read 26 ns there against the 87 ns a walk that keeps moving reports.
                int warmup = (int)Math.Min((long)lines * WarmupCycles, MaxWarmupHops);
                void* position = Chase(low, warmup, low, high);
                if (position == null)
                    return 0;

                // One rough slice to learn what a hop costs here, then size the real ones by time
                // rather than by hops: the same count is 0.02 ms at L1 and 1.7 ms at DRAM.
                var sizing = Stopwatch.StartNew();
                position = Chase(position, SizingHops, low, high);
                sizing.Stop();

                if (position == null || sizing.Elapsed.TotalMilliseconds <= 0)
                    return 0;

                double perHop = sizing.Elapsed.TotalMilliseconds * 1e6 / SizingHops;
                int hops = (int)Math.Min(Math.Max(SliceMs * 1e6 / perHop, 4096), 8_000_000);

                double best = double.MaxValue;
                for (int pass = 0; pass < Passes; pass++)
                {
                    var watch = Stopwatch.StartNew();
                    position = Chase(position, hops, low, high);
                    watch.Stop();

                    if (position == null)
                        return 0;

                    double ns = watch.Elapsed.TotalMilliseconds * 1e6 / hops;
                    if (ns < best)
                        best = ns;
                }

                return best == double.MaxValue ? 0 : best;
            }
        }

        /// <summary>
        /// Dependent loads, guarded the same way the DRAM walk is: a value outside the buffer is
        /// corrupted data, and dereferencing it would fault where nothing can catch it.
        /// </summary>
        private static unsafe void* Chase(void* start, int hops, byte* low, byte* high)
        {
            void* p = start;

            for (int i = 0; i < hops; i++)
            {
                p = *(void**)p;
                if (p < low || p >= high)
                    return null;
            }

            return p;
        }
    }
}
