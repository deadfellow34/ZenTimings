using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime;
using System.Threading;

namespace ZenTimings
{
    public class MemoryBandwidthResult
    {
        public double ReadGBs { get; set; }
        public double WriteGBs { get; set; }
        public double CopyGBs { get; set; }

        /// <summary>Independent random line fetches, many in flight - the bank-cycle score.</summary>
        public double RandomGBs { get; set; }

        /// <summary>A figure the ceiling filter discarded: its zero is a measurement withheld,
        /// not one never taken.</summary>
        public bool AboveBus { get; set; }

        public bool Ok { get; set; }

        /// <summary>The exception text where there is one; the window prefers <see cref="Reason"/>.</summary>
        public string Error { get; set; }

        public BenchmarkError Reason { get; set; }

        /// <summary>Both buffers came back large-page backed.</summary>
        public bool LargePages { get; set; }

        /// <summary>Why not, when <see cref="LargePages"/> is false.</summary>
        public LargePageFailure LargePageFailure { get; set; }
        public int LargePageError { get; set; }

        /// <summary>The write kernel got non-temporal stores rather than ordinary ones.</summary>
        public bool Streaming { get; set; }

        /// <summary>Worker sets the sweep ranked: all-core, one per die, and one per physical core.</summary>
        public int WorkerSets { get; set; }

    }

    /// <summary>The random kernel read a value nothing wrote - corrupted data from the RAM.</summary>
    internal sealed class MemoryFaultException : Exception
    {
        public MemoryFaultException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Streams a buffer far larger than the caches and reports how fast it moved.
    /// </summary>
    /// <remarks>
    /// Time-boxed rather than work-boxed: every worker runs its kernel for a fixed window and
    /// reports how many bytes it moved, so no thread's wake-up or finish time sits on the
    /// critical path - the classic join skew of "split the buffer, time until the slowest thread
    /// is done" simply has nothing to attach to. Workers are created once, pinned one per
    /// physical core (SMT siblings add scheduler churn, not bandwidth), and released by a spin
    /// flag, which wakes in nanoseconds where an event wakes in microseconds.
    ///
    /// Two passes where there is more than one worker set: a short interleaved one to rank them,
    /// then full-length windows on whichever set each kernel ranked highest. Ranking and publishing
    /// want different things from a window - the first wants many of them close together, the
    /// second wants long ones.
    ///
    /// Figures are decimal GB/s, the same unit as the theoretical-ceiling rows.
    /// </remarks>
    public static class MemoryBandwidthTest
    {
        private const int WindowMs = 400;

        /// <summary>
        /// Ten rather than five. Each kernel publishes the best of its windows, and a window is
        /// only ever spoiled - by a neighbour, a boost drop, a turnaround the controller batched
        /// badly - never flattered, so more of them lands nearer the uncontended figure. Copy
        /// needs it most: it is the one kernel driving both fabric directions at once and the one
        /// that swings furthest between runs.
        /// </summary>
        private const int WindowsPerKernel = 10;

        /// <summary>
        /// Ranking windows are short. The sweep only has to put the worker sets in order; the set
        /// that wins is then measured again at full length, so no published figure comes out of
        /// one of these.
        /// </summary>
        private const int SweepWindowMs = 150;

        /// <summary>Passes through the whole set list. Even, so the alternating walk pairs up.</summary>
        private const int SweepRounds = 4;

        /// <summary>Roughly what the ranking pass may cost before rounds are traded away for it.</summary>
        private const int SweepBudgetMs = 20000;

        /// <summary>
        /// How far past the bus a figure may land before it is thrown away rather than kept as a
        /// best. Taking a maximum over more configurations is taking more chances on one of them
        /// coming out impossibly high, and a score above what the DRAM can carry is not a fast
        /// run - it is a broken measurement.
        /// </summary>
        private const double CeilingSlack = 1.05;

        /// <summary>Longs per block between stop-flag checks - 4 MB, ~0.5 ms of traffic.</summary>
        private const int BlockLongs = 512 * 1024;

        private const int KernelRead = 0;
        private const int KernelWrite = 1;
        private const int KernelCopy = 2;
        private const int KernelRandom = 3;
        private const int KernelCount = 4;

        /// <summary>Independent chase cursors per worker - the memory-level parallelism that
        /// makes bank timings (tRAS/tRC/tRRD/tFAW) the binding limit instead of one access's
        /// full latency.</summary>
        private const int RandomCursors = 8;

        /// <summary>
        /// The worker sets to measure, best of all of them wins.
        /// </summary>
        /// <remarks>
        /// Which set peaks depends on the kernel, and not in a way that can be reasoned out in
        /// advance: each die has its own link to the memory controller, and writes often do better
        /// with fewer streams contending than with every core pushing at once.
        ///
        /// Every die is offered, not only the first. On a part whose dies differ - 96 MB against 32
        /// on a 9950X3D - which one answers fastest is not settled by which one is numbered zero.
        ///
        /// One core at a time is offered last. It cannot win a read or a copy, a single core not
        /// having enough misses outstanding to fill the bus, but a write with one stream and no
        /// contention sometimes does - and on a part with a single L3 these are the only low-stream
        /// sets there are.
        /// </remarks>
        private static List<ulong[]> Configurations()
        {
            var configs = new List<ulong[]>();

            var cores = BenchmarkNative.GetCoreMasks();
            if (cores == null || cores.Length == 0)
            {
                // Nothing to enumerate; the pool falls back to a thread count of its own.
                configs.Add(null);
                return configs;
            }

            // The masks stop at processor group 0, so past 64 logical processors they are half
            // the machine wearing an all-core label - and a sweep built from them publishes a
            // Threadripper's read at a fraction of the bus. An unpinned pool is the honest
            // fallback: no set claims to be something it is not.
            if (BenchmarkNative.PhysicalCoreCount() > cores.Length)
            {
                configs.Add(null);
                return configs;
            }

            configs.Add(cores);

            var groups = BenchmarkNative.GetL3Groups();
            if (groups != null && groups.Length > 1)
            {
                foreach (var group in groups)
                {
                    var die = new List<ulong>();
                    foreach (var mask in cores)
                        if ((mask & group) != 0)
                            die.Add(mask);

                    if (die.Count > 0 && die.Count < cores.Length)
                        configs.Add(die.ToArray());
                }

                var spread = new List<ulong>();
                foreach (var group in groups)
                {
                    foreach (var mask in cores)
                    {
                        if ((mask & group) != 0)
                        {
                            spread.Add(mask);
                            break;
                        }
                    }
                }

                if (spread.Count > 1 && spread.Count < cores.Length)
                    configs.Add(spread.ToArray());
            }

            if (cores.Length > 1)
            {
                foreach (var mask in cores)
                    configs.Add(new[] { mask });
            }

            return configs;
        }

        /// <summary>
        /// Rounds through the whole set list during the ranking pass.
        /// </summary>
        /// <remarks>
        /// Always even, never fewer than two. The list is walked forwards on one round and
        /// backwards on the next, which puts every set early exactly as often as it puts it late;
        /// an odd count leaves one unpaired round and with it the bias the alternation removes.
        /// </remarks>
        private static int Rounds(int sets)
        {
            int affordable = SweepBudgetMs / Math.Max(1, sets * KernelCount * SweepWindowMs);
            return affordable >= SweepRounds ? SweepRounds : 2;
        }

        /// <summary>The highest window the bus says could be real; zero when none of them could.</summary>
        private static double Peak(double[] windows, double ceilingGBs)
        {
            double best = 0;
            foreach (var value in windows)
            {
                if (ceilingGBs > 0 && value > ceilingGBs * CeilingSlack)
                    continue;

                if (value > best)
                    best = value;
            }

            return best;
        }

        /// <summary>
        /// Median rather than peak. Interference in the streaming kernels can only slow them down,
        /// but a preempted random worker's cursors bunch up and the window comes out too FAST - a
        /// maximum would keep exactly that window.
        /// </summary>
        private static double Middle(double[] windows, double ceilingGBs)
        {
            var sorted = (double[])windows.Clone();
            Array.Sort(sorted);

            // At two samples the median IS the mean, which hands a wake-inflated random window
            // half the verdict - and two is what the sweep affords on most multi-die parts. The
            // error is inflation-only, so the lower sample is the safer of the pair.
            double median = sorted.Length == 2
                ? sorted[0]
                : (sorted.Length & 1) == 1
                    ? sorted[sorted.Length / 2]
                    : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;

            return ceilingGBs > 0 && median > ceilingGBs * CeilingSlack ? 0 : median;
        }

        private static double Score(double[] windows, int kernelId, double ceilingGBs)
        {
            return kernelId == KernelRandom
                ? Middle(windows, ceilingGBs)
                : Peak(windows, ceilingGBs);
        }

        /// <summary>Index of the set that ranked highest for one kernel; the all-core set on a tie.</summary>
        private static int Winner(double[][][] sweep, int kernelId, double ceilingGBs)
        {
            // A zeroed all-core set means the ceiling filter fired, not that the cores are slow -
            // and no other set can then be the honest answer: letting a survivor win publishes a
            // fraction of the truth as the score. Keep the all-core set; the full-length pass
            // re-measures it, and if it is still above the bus the result is the explained dash
            // rather than a number from the wrong workers. Write is NOT exempted: its legitimate
            // single-core wins happen with a nonzero all-core score, which this never touches -
            // the zero case is always a broken ceiling, where an unannotated understatement is
            // exactly the failure being prevented.
            if (Score(sweep[0][kernelId], kernelId, ceilingGBs) <= 0)
                return 0;

            int winner = 0;
            double best = -1;

            for (int set = 0; set < sweep.Length; set++)
            {
                double score = Score(sweep[set][kernelId], kernelId, ceilingGBs);
                if (score > best)
                {
                    best = score;
                    winner = set;
                }
            }

            return winner;
        }

        /// <param name="ceilingGBs">
        /// What the DRAM bus can carry, so a configuration that comes out above it is discarded
        /// instead of winning. Zero leaves every figure standing.
        /// </param>
        /// <param name="bus">
        /// Taken around each timed window and handed back between them. Null measures without it.
        /// </param>
        public static unsafe MemoryBandwidthResult Run(int bufferMegabytes, Action<double> progress,
            Func<bool> cancelled, double ceilingGBs = 0, HardwareLock bus = null)
        {
            var result = new MemoryBandwidthResult();

            if (bufferMegabytes < 16 || bufferMegabytes > 2048)
            {
                result.Error = "Buffer size out of range.";
                result.Reason = BenchmarkError.BufferOutOfRange;
                return result;
            }

            // Twice the size, for the two buffers. Asked before allocating rather than after:
            // the commit would succeed against the pagefile and the run would time the disk.
            if (!BenchmarkNative.FitsInFreeMemory((long)bufferMegabytes * 2 * 1024 * 1024))
            {
                result.Error = "Not enough free memory for that buffer size.";
                result.Reason = BenchmarkError.OutOfMemory;
                return result;
            }

            BenchmarkNative.NativeBuffer source = null;
            BenchmarkNative.NativeBuffer destination = null;
            Pool pool = null;
            var previousLatencyMode = GCSettings.LatencyMode;
            bool noGcHeld = false;

            // Every worker set has to have come home before the pages can go back.
            bool workersOut = true;

            try
            {
                long bytes = (long)bufferMegabytes * 1024 * 1024;
                int count = (int)(bytes / sizeof(long));

                // Native and large-page backed, the same as the latency walk. A managed array
                // cannot be either: the GC owns its pages, so there is no way to ask for large
                // ones, and the TLB cost of a 4 KB-paged gigabyte lands inside the timed window.
                source = BenchmarkNative.NativeBuffer.Allocate(bytes);
                destination = BenchmarkNative.NativeBuffer.Allocate(bytes);
                result.LargePages = source.LargePages && destination.LargePages;

                // Two buffers, so the second can fall back where the first did not - report
                // whichever missed.
                var missed = !source.LargePages ? source : destination;
                result.LargePageFailure = missed.Failure;
                result.LargePageError = missed.FailureCode;

                long* src = (long*)source.Pointer;
                long* dst = (long*)destination.Pointer;

                // First touch, so the timed windows are not paying for page faults.
                for (int i = 0; i < count; i += 512)
                {
                    src[i] = i;
                    dst[i] = 0;
                }

                // A single random cycle through the source buffer's cache lines: line i's first
                // slot holds the element index of the next line. Nothing ever writes the source,
                // so the chain survives every window. cursorStarts are evenly spaced positions
                // ON the cycle, so no cursor runs in another's cache wake.
                int lines = count / 8;
                var order = BenchmarkNative.BuildShuffledCycle(lines);
                for (int k = 0; k < lines; k++)
                    src[(long)order[k] * 8] = (long)order[(k + 1) % lines] * 8;

                // The cycle table is the one big managed object left; collect now, on purpose, so
                // a background collection does not pick its own moment mid-window.
                GC.Collect(2, GCCollectionMode.Forced, true, false);
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                try { noGcHeld = GC.TryStartNoGCRegion(16 * 1024 * 1024); }
                catch { noGcHeld = false; }

                var configurations = Configurations();

                using (new BenchmarkNative.PriorityScope())
                {
                    result.Streaming = Pool.Streaming;
                    result.WorkerSets = configurations.Count;

                    // Which set publishes each kernel. Zero is the all-core set, which is the answer
                    // on a part with nothing to sweep.
                    var winner = new int[KernelCount];

                    int rounds = Rounds(configurations.Count);
                    int step = 0;
                    int totalSteps = KernelCount * WindowsPerKernel
                        + (configurations.Count > 1 ? configurations.Count * rounds * KernelCount : 0);

                    if (configurations.Count > 1)
                    {
                        var sweep = new double[configurations.Count][][];
                        for (int set = 0; set < configurations.Count; set++)
                        {
                            sweep[set] = new double[KernelCount][];
                            for (int k = 0; k < KernelCount; k++)
                                sweep[set][k] = new double[rounds];
                        }

                        // Round-robin, not one set at a time. A set measured five seconds after the
                        // one it is ranked against was measured under a different boost table and a
                        // different power budget, and the comparison then says more about the SMU
                        // than about the sets. Interleaved, the drift lands on all of them alike.
                        //
                        // Alternating direction, because interleaving alone does not finish the
                        // job: walked the same way every round, the first set is always sampled
                        // early and the last always late. Forwards then backwards gives every set
                        // the early slot as often as the late one, so with an even number of
                        // rounds the sample times are symmetric and the random kernel's median
                        // cancels a linear drift outright. The peak-scored kernels keep whichever
                        // round ran fastest, so there the symmetry only moves the bias off a
                        // fixed end of the list rather than removing it.
                        for (int round = 0; round < rounds; round++)
                        {
                            bool forward = (round & 1) == 0;

                            for (int slot = 0; slot < configurations.Count; slot++)
                            {
                                int set = forward ? slot : configurations.Count - 1 - slot;

                                if (cancelled != null && cancelled())
                                {
                                    result.Error = "Cancelled.";
                                    result.Reason = BenchmarkError.Cancelled;
                                    return result;
                                }

                                // A constructor that died mid-start says through StartWorkersOut
                                // whether its threads came home. Assuming they did not kept two
                                // gigabytes for the life of the process, including when it threw
                                // before a single thread existed.
                                try
                                {
                                    pool = new Pool(source.Pointer, destination.Pointer, count, order,
                                        lines, configurations[set]);
                                }
                                catch
                                {
                                    workersOut &= Pool.StartWorkersOut;
                                    throw;
                                }

                                try
                                {
                                    for (int k = 0; k < KernelCount; k++)
                                    {
                                        sweep[set][k][round] =
                                            pool.RunWindow(k, round, SweepWindowMs, bus);
                                        Report(progress, ++step, totalSteps);
                                    }
                                }
                                finally
                                {
                                    // Per set, because the next one needs its own threads - and the
                                    // answer decides whether the buffers may be freed at the end.
                                    workersOut &= pool.Shutdown();
                                    pool = null;
                                }
                            }
                        }

                        for (int k = 0; k < KernelCount; k++)
                            winner[k] = Winner(sweep, k, ceilingGBs);
                    }

                    // Full-length windows on whatever won, so no published figure comes out of a
                    // ranking window. Every kernel has exactly one winner, so this costs the same
                    // whether one set took all four or four sets took one each.
                    var scores = new double[KernelCount];
                    var windows = new double[WindowsPerKernel];

                    for (int set = 0; set < configurations.Count; set++)
                    {
                        bool won = false;
                        for (int k = 0; k < KernelCount; k++)
                            won |= winner[k] == set;

                        if (!won)
                            continue;

                        try
                        {
                            pool = new Pool(source.Pointer, destination.Pointer, count, order, lines,
                                configurations[set]);
                        }
                        catch
                        {
                            workersOut &= Pool.StartWorkersOut;
                            throw;
                        }

                        try
                        {
                            for (int k = 0; k < KernelCount; k++)
                            {
                                if (winner[k] != set)
                                    continue;

                                for (int window = 0; window < WindowsPerKernel; window++)
                                {
                                    if (cancelled != null && cancelled())
                                    {
                                        result.Error = "Cancelled.";
                                        result.Reason = BenchmarkError.Cancelled;
                                        return result;
                                    }

                                    windows[window] = pool.RunWindow(k, window, WindowMs, bus);
                                    Report(progress, ++step, totalSteps);
                                }

                                scores[k] = Score(windows, k, ceilingGBs);

                                // Zero out of windows that scored is the ceiling filter, not a
                                // kernel that never ran, and the two dash identically.
                                if (scores[k] <= 0 && Score(windows, k, 0) > 0)
                                    result.AboveBus = true;
                            }
                        }
                        finally
                        {
                            workersOut &= pool.Shutdown();
                            pool = null;
                        }
                    }

                    result.ReadGBs = scores[KernelRead];
                    result.WriteGBs = scores[KernelWrite];
                    result.CopyGBs = scores[KernelCopy];
                    result.RandomGBs = scores[KernelRandom];
                    result.Ok = true;
                }
            }
            catch (OutOfMemoryException)
            {
                // Two buffers, so this needs twice the size the latency test does.
                result.Error = "Not enough free memory for that buffer size.";
                result.Reason = BenchmarkError.OutOfMemory;
            }
            catch (MemoryFaultException ex)
            {
                result.Error = ex.Message;
                result.Reason = BenchmarkError.MemoryError;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.Reason = BenchmarkError.Failed;
            }
            finally
            {
                // A set left running by an early return still has to be brought down.
                if (pool != null)
                    workersOut &= pool.Shutdown();

                if (noGcHeld)
                {
                    try { GC.EndNoGCRegion(); } catch { }
                }

                GCSettings.LatencyMode = previousLatencyMode;

                // The one place a managed array was forgiving and native memory is not: a worker
                // that never came back would have kept an array alive, but it writes straight into
                // freed pages and takes the process with it. A stuck worker keeps its buffers
                // instead - committed for the rest of the session, which is a cost, not a crash.
                if (workersOut)
                {
                    if (source != null) source.Dispose();
                    if (destination != null) destination.Dispose();
                }

                GC.Collect();
            }

            return result;
        }

        private static void Report(Action<double> progress, int step, int total)
        {
            if (progress != null)
                progress(step / (double)total);
        }

        /// <summary>
        /// Where the read kernel's totals go. Without somewhere to put them the sums are dead
        /// code and the JIT is free to delete the loops that produced them.
        /// </summary>
        private static volatile object Sink;

        /// <summary>Persistent pinned workers plus the window choreography.</summary>
        private sealed class Pool
        {
            // Borrowed, not owned: Run allocated the pages and Run frees them, and only once it
            // knows no worker is still inside them.
            private readonly IntPtr source;
            private readonly IntPtr destination;
            private readonly Thread[] workers;

            /// <summary>Whether the stores go straight to memory instead of owning the line first.</summary>
            public static bool Streaming { get { return NtStore.Available; } }

            /// <summary>Each worker's own slice, wrapped inside. Disjoint, so no worker can read
            /// a line another one just pulled in.</summary>
            private readonly int[] chunkStart;
            private readonly int[] chunkEnd;

            /// <summary>Element indices of each worker's chase cursors, evenly spread along the
            /// random cycle so no cursor rides in another's cache wake.</summary>
            private readonly long[][] cursorStarts;

            // One padded slot per worker - 8 longs apart so the counters never share a line.
            private readonly long[] byteCounts;

            // Two release events, indexed by generation parity. A worker that has consumed
            // generation G parks on the event for G+1, which is still clear - with a single event
            // it would spin from its own acknowledgment until the coordinator got scheduled to
            // reset it, and with every core hosting a pinned worker that is what stops the
            // coordinator from being scheduled at all.
            private readonly ManualResetEventSlim[] startSignals =
            {
                new ManualResetEventSlim(false),
                new ManualResetEventSlim(false),
            };

            /// <summary>Set by the last worker to acknowledge, so the coordinator can block.</summary>
            private readonly ManualResetEventSlim drained = new ManualResetEventSlim(false);

            /// <summary>Stopwatch timestamp of that last acknowledgment.</summary>
            private long lastAckTicks;

            private volatile int generation;
            private volatile int kernel;
            private volatile int rotation;
            private volatile bool stop;
            private volatile bool quit;
            private volatile bool failed;
            private volatile bool shutdown;
            private volatile bool stopped;
            private int acked;

            /// <summary>Elements in each buffer, for the random kernel's corruption check.</summary>
            private readonly int elementCount;

            /// <summary>A worker loaded a chase value outside the buffer - the RAM corrupted it.</summary>
            private volatile bool corruptedData;

            public Pool(IntPtr source, IntPtr destination, int count, int[] cycleOrder, int lines,
                ulong[] cores)
            {
                this.source = source;
                this.destination = destination;
                elementCount = count;

                int workerCount = cores != null
                    ? cores.Length
                    : Math.Max(1, Environment.ProcessorCount / 2);

                workers = new Thread[workerCount];
                chunkStart = new int[workerCount];
                chunkEnd = new int[workerCount];
                cursorStarts = new long[workerCount][];
                byteCounts = new long[workerCount * 8];

                int totalCursors = workerCount * RandomCursors;
                for (int t = 0; t < workerCount; t++)
                {
                    // Whole cache lines. The streaming store faults on an address that is not
                    // 16-byte aligned, and a slice that started mid-line would hand it one.
                    chunkStart[t] = (int)(((long)count * t / workerCount) & ~7L);
                    chunkEnd[t] = t + 1 == workerCount
                        ? count
                        : (int)(((long)count * (t + 1) / workerCount) & ~7L);

                    cursorStarts[t] = new long[RandomCursors];
                    for (int c = 0; c < RandomCursors; c++)
                    {
                        int cursor = t * RandomCursors + c;
                        int position = (int)((long)lines * cursor / totalCursors);
                        cursorStarts[t][c] = (long)cycleOrder[position] * 8;
                    }
                }

                // Threads start last and under a guard: a failure part-way would otherwise leave
                // the started ones parked forever, holding both buffers alive with them.
                //
                // Whether they did come home is the caller's business - it decides if the pages
                // may be freed - but a constructor that throws hands back no object to ask. The
                // answer travels in a static instead, which is safe because BenchmarkSession
                // admits one run at a time, so one Pool is ever under construction.
                startWorkersOut = true;
                try
                {
                    for (int t = 0; t < workerCount; t++)
                    {
                        int index = t;
                        ulong mask = cores != null ? cores[t] & (~cores[t] + 1) : 0;

                        var worker = new Thread(() => WorkerLoop(index, mask))
                        {
                            IsBackground = true,
                            Priority = ThreadPriority.AboveNormal,
                        };

                        // Stored only once it is running: Join throws on an unstarted thread, and
                        // Shutdown's catch would read that as a worker still inside the buffers -
                        // which keeps two gigabytes locked for the life of the process.
                        worker.Start();
                        workers[t] = worker;
                    }
                }
                catch
                {
                    startWorkersOut = Shutdown();
                    throw;
                }
            }

            /// <summary>
            /// After a constructor that threw: whether the threads it had started all came home,
            /// and so whether the buffers may be freed. True when it never reached the thread loop.
            /// </summary>
            public static bool StartWorkersOut { get { return startWorkersOut; } }

            private static bool startWorkersOut = true;

            /// <summary>Runs one kernel for a fixed window and returns decimal GB/s.</summary>
            public double RunWindow(int kernelId, int windowIndex, int windowMs, HardwareLock bus)
            {
                for (int t = 0; t < workers.Length; t++)
                    byteCounts[t * 8] = 0;

                Interlocked.Exchange(ref acked, 0);
                Interlocked.Exchange(ref lastAckTicks, 0);
                drained.Reset();
                stop = false;
                kernel = kernelId;

                // Each window hands the cursor sets to different workers, so a thread that gets
                // preempted in one window is not the same one carrying that set in the next.
                rotation = windowIndex;

                // Before the priority goes up, so a bus that has to be waited out is waited out at
                // normal priority and does not sit inside the window either.
                bool busHeld = bus != null && bus.Enter(HardwareLock.WindowWaitMs);

                // Above the workers for the choreography: with one worker pinned to every
                // physical core, an equal-or-lower coordinator can starve on SMT-less parts.
                ThreadPriority previous = Thread.CurrentThread.Priority;
                Thread.CurrentThread.Priority = ThreadPriority.Highest;

                double elapsedSeconds;
                long startTicks = Stopwatch.GetTimestamp();
                var watch = Stopwatch.StartNew();
                int released = generation + 1;
                try
                {
                    generation = released;
                    startSignals[released & 1].Set();   // release the workers

                    Thread.Sleep(windowMs);
                    stop = true;

                    // Out of the workers' way for the drain, and blocking rather than spinning:
                    // with SMT off every logical CPU hosts a pinned worker, so a spinning
                    // coordinator fights the very threads it waits for - below their priority it
                    // starves outright, above it, it preempts them. Parked on an event at normal
                    // priority it does neither.
                    Thread.CurrentThread.Priority = previous;

                    if (!drained.Wait(windowMs + 10000))
                        throw new TimeoutException("A bandwidth worker stalled.");

                    // The window ends when the last worker stopped moving bytes, not when this
                    // thread got scheduled again - the wake-up latency belongs to the scheduler,
                    // not to the memory subsystem.
                    long lastTick = Interlocked.Read(ref lastAckTicks);
                    watch.Stop();
                    elapsedSeconds = lastTick > startTicks
                        ? (lastTick - startTicks) / (double)Stopwatch.Frequency
                        : watch.Elapsed.TotalSeconds;
                }
                finally
                {
                    // Safe to clear now: the workers are parked on the other event.
                    startSignals[released & 1].Reset();
                    Thread.CurrentThread.Priority = previous;

                    if (busHeld)
                        bus.Exit();
                }

                if (corruptedData)
                    throw new MemoryFaultException(
                        "The random kernel read a value nothing wrote - corrupted data from the RAM.");

                if (failed)
                    throw new InvalidOperationException("A bandwidth worker failed.");

                long moved = 0;
                for (int t = 0; t < workers.Length; t++)
                    moved += byteCounts[t * 8];

                return elapsedSeconds <= 0 ? 0 : moved / elapsedSeconds / 1e9;
            }

            /// <summary>True when every worker has left the buffers, so the pages can be freed.</summary>
            public bool Shutdown()
            {
                if (shutdown)
                    return stopped;
                shutdown = true;

                quit = true;
                stop = true;
                generation++;

                // Both, because a worker may be parked on either parity.
                startSignals[0].Set();
                startSignals[1].Set();

                // Longer than the old second: the answer decides whether the caller may free the
                // pages these threads are writing into, so it is worth waiting for a real one.
                bool allStopped = true;
                foreach (var worker in workers)
                {
                    if (worker == null)
                        continue;
                    try { allStopped &= worker.Join(5000); } catch { allStopped = false; }
                }

                // Only dispose once every worker is out of Wait(); a late one would otherwise
                // fault on a disposed handle and take the process down.
                if (allStopped)
                {
                    startSignals[0].Dispose();
                    startSignals[1].Dispose();
                    drained.Dispose();
                }

                stopped = allStopped;
                return allStopped;
            }

            private unsafe void WorkerLoop(int index, ulong pinMask)
            {
                // Taken once, outside the loop: the pages are owned by Run and stay put for the
                // whole life of this thread.
                long* src = (long*)source;
                long* dst = (long*)destination;

                if (pinMask != 0)
                {
                    try
                    {
                        Thread.BeginThreadAffinity();
                        BenchmarkNative.SetThreadAffinityMask(BenchmarkNative.GetCurrentThread(), (UIntPtr)pinMask);
                    }
                    catch { }
                }

                int seenGeneration = 0;
                while (true)
                {
                    int current = generation;
                    if (current == seenGeneration)
                    {
                        // Park on the event the NEXT window will set. The one this worker was
                        // released by is still set until the coordinator clears it, and waiting
                        // on that would return instantly and spin the core the coordinator needs.
                        startSignals[(seenGeneration + 1) & 1].Wait();
                        continue;
                    }

                    seenGeneration = current;
                    if (quit)
                        return;

                    long moved = 0;
                    long checksum = 0;

                    try
                    {
                        // Every worker cycles its OWN slice. Sharing the buffer from staggered
                        // starts reads better on paper - the gap is a whole buffer's worth of
                        // traffic, far past any L3 - but nothing holds the gap open. A worker
                        // delayed by 2.7 ms on a 256 MB buffer has lost its entire lead, and from
                        // the moment it lands on the one ahead it reads that one's lines out of
                        // the L3, which makes it faster still and locks the two together. Both
                        // count every byte, so one DRAM fetch is billed twice and the score goes
                        // past the bus. The slices are disjoint, so there is no lead to lose.
                        //
                        // What keeps this DRAM traffic is the buffer, not the slicing: all the
                        // slices together are the buffer, and IsCacheBound is what checks it.
                        int start = chunkStart[index];
                        int end = chunkEnd[index];
                        int position = start;
                        int currentKernel = kernel;

                        // Read alternates buffers on every wrap. The L3 holds a fixed slice of
                        // whatever is cycling - the buffer's own size decides how much of the
                        // stream never reaches DRAM - and the destination is allocated,
                        // first-touched and then idle for the whole read kernel. Walking it too
                        // doubles the cycle for no extra page, which halves the L3's share.
                        long* readBuffer = src;

                        if (currentKernel == KernelRandom)
                        {
                            // Many independent chases at once. One dependent chain measures one
                            // access's full latency; eight per core measure how fast the banks
                            // can CYCLE - which is what tRAS/tRC/tRRD/tFAW actually govern.
                            var cursors = new long[RandomCursors];
                            Array.Copy(cursorStarts[(index + rotation) % workers.Length], cursors, RandomCursors);

                            long steps = 0;
                            while (!stop)
                            {
                                for (int r = 0; r < 512; r++)
                                {
                                    for (int c = 0; c < RandomCursors; c++)
                                    {
                                        // The loaded value is the next address, and nothing writes
                                        // this buffer - out of range means the RAM corrupted it.
                                        // Report that instead of faulting on it; on a marginal
                                        // overclock it is the verdict, and the compare vanishes
                                        // under the ~80 ns miss it sits behind.
                                        long next = src[cursors[c]];
                                        if ((ulong)next >= (ulong)elementCount)
                                        {
                                            corruptedData = true;
                                            goto drained;
                                        }

                                        cursors[c] = next;
                                    }
                                }
                                steps += 512 * RandomCursors;
                            }

                            drained:
                            checksum = cursors[0];
                            moved = steps * 64;   // one cache line per hop
                        }
                        else
                        {
                            // An empty slice would spin on a zero-length block without ever
                            // moving a byte; there is nothing for this worker to do.
                            while (!stop && end > start)
                            {
                                int block = Math.Min(BlockLongs, end - position);

                                switch (currentKernel)
                                {
                                    case KernelRead:
                                        checksum += ReadBlock(readBuffer, position, position + block);
                                        moved += (long)block * sizeof(long);
                                        break;
                                    case KernelWrite:
                                        WriteBlock(dst, position, position + block);
                                        moved += (long)block * sizeof(long);
                                        break;
                                    default:
                                        CopyBlock(src, dst, position, position + block);
                                        moved += (long)block * sizeof(long) * 2;   // read once, written once
                                        break;
                                }

                                position += block;
                                if (position >= end)
                                {
                                    position = start;
                                    readBuffer = readBuffer == src ? dst : src;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // The coordinator must never wait forever on a dead worker.
                        failed = true;
                    }
                    finally
                    {
                        byteCounts[index * 8] = moved;
                        Sink = checksum;

                        // The worker that closes the window stamps the end of it and releases the
                        // coordinator; the others only count themselves in.
                        if (Interlocked.Increment(ref acked) == workers.Length)
                        {
                            Interlocked.Exchange(ref lastAckTicks, Stopwatch.GetTimestamp());
                            drained.Set();
                        }
                    }
                }
            }

            private static unsafe long ReadBlock(long* buffer, int from, int to)
            {
                // Four accumulators: a single dependent chain would be limited by add latency
                // rather than by how fast the lines arrive.
                long a = 0, b = 0, c = 0, d = 0;
                int i = from;
                for (; i + 3 < to; i += 4)
                {
                    a += buffer[i];
                    b += buffer[i + 1];
                    c += buffer[i + 2];
                    d += buffer[i + 3];
                }

                long sum = 0;
                for (; i < to; i++)
                    sum += buffer[i];

                return sum + a + b + c + d;
            }

            /// <summary>
            /// Streaming stores write exactly the bytes counted. The fallback loop's ordinary
            /// store first pulls the line in to own it, so the bus moves roughly twice the counted
            /// bytes and the figure reads near half - the result line marks that mode.
            /// </summary>
            private static unsafe void WriteBlock(long* buffer, int from, int to)
            {
                if (NtStore.Available)
                {
                    NtStore.Fill(buffer, from, to, from);
                    return;
                }

                for (int i = from; i < to; i++)
                    buffer[i] = i;
            }

            /// <summary>
            /// Copied by hand rather than through the runtime's memmove.
            /// </summary>
            /// <remarks>
            /// memmove switches to non-temporal stores only above roughly half the L3, which turned
            /// the copy score into a function of the cache: a 4 MB block cleared the line on a 4 MB
            /// L3 and fell under it on every 32 MB and 96 MB part, halving the figure there. Block
            /// size cannot fix it either - the block doubles as the stop-flag granularity, and a
            /// threshold that moves with the L3 is not one a single fixed size clears everywhere.
            /// An explicit loop is slower until the streaming store lands, but it is the same
            /// speed on every part.
            /// </remarks>
            private static unsafe void CopyBlock(long* source, long* destination, int from, int to)
            {
                if (NtStore.Available)
                {
                    NtStore.Copy(source, destination, from, to);
                    return;
                }

                for (int i = from; i < to; i++)
                    destination[i] = source[i];
            }
        }
    }
}
