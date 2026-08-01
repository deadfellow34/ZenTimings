using System;
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

        public bool Ok { get; set; }

        /// <summary>The exception text where there is one; the window prefers <see cref="Reason"/>.</summary>
        public string Error { get; set; }

        public BenchmarkError Reason { get; set; }
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
    /// Figures are decimal GB/s, the same unit as the theoretical-ceiling rows.
    /// </remarks>
    public static class MemoryBandwidthTest
    {
        private const int WindowMs = 400;
        private const int WindowsPerKernel = 5;

        /// <summary>Longs per block between stop-flag checks - 4 MB, ~0.5 ms of traffic.</summary>
        private const int BlockLongs = 512 * 1024;

        private const int KernelRead = 0;
        private const int KernelWrite = 1;
        private const int KernelCopy = 2;
        private const int KernelRandom = 3;

        /// <summary>Independent chase cursors per worker - the memory-level parallelism that
        /// makes bank timings (tRAS/tRC/tRRD/tFAW) the binding limit instead of one access's
        /// full latency.</summary>
        private const int RandomCursors = 8;

        public static MemoryBandwidthResult Run(int bufferMegabytes, Action<double> progress, Func<bool> cancelled)
        {
            var result = new MemoryBandwidthResult();

            if (bufferMegabytes < 16 || bufferMegabytes > 1024)
            {
                result.Error = "Buffer size out of range.";
                result.Reason = BenchmarkError.BufferOutOfRange;
                return result;
            }

            long[] source = null;
            long[] destination = null;
            Pool pool = null;
            var previousLatencyMode = GCSettings.LatencyMode;
            bool noGcHeld = false;

            try
            {
                long bytes = (long)bufferMegabytes * 1024 * 1024;
                int count = (int)(bytes / sizeof(long));

                source = new long[count];
                destination = new long[count];

                // First touch, so the timed windows are not paying for page faults.
                for (int i = 0; i < count; i += 512)
                {
                    source[i] = i;
                    destination[i] = 0;
                }

                // A single random cycle through the source buffer's cache lines: line i's first
                // slot holds the element index of the next line. Nothing ever writes the source,
                // so the chain survives every window. cursorStarts are evenly spaced positions
                // ON the cycle, so no cursor runs in another's cache wake.
                int lines = count / 8;
                var order = BenchmarkNative.BuildShuffledCycle(lines);
                for (int k = 0; k < lines; k++)
                    source[(long)order[k] * 8] = (long)order[(k + 1) % lines] * 8;

                // The two buffers just dirtied the whole gen-2 budget; collect now, on purpose,
                // so a background collection does not pick its own moment mid-window.
                GC.Collect(2, GCCollectionMode.Forced, true, false);
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                try { noGcHeld = GC.TryStartNoGCRegion(16 * 1024 * 1024); }
                catch { noGcHeld = false; }

                pool = new Pool(source, destination, count, order, lines);
                order = null;

                using (new BenchmarkNative.PriorityScope())
                {
                    double bestRead = 0, bestWrite = 0, bestCopy = 0;
                    var randomWindows = new double[WindowsPerKernel];
                    int step = 0, totalSteps = 4 * WindowsPerKernel;

                    for (int window = 0; window < WindowsPerKernel; window++)
                    {
                        if (cancelled != null && cancelled())
                        {
                            result.Error = "Cancelled.";
                            result.Reason = BenchmarkError.Cancelled;
                            return result;
                        }

                        double read = pool.RunWindow(KernelRead, window);
                        if (read > bestRead) bestRead = read;
                        Report(progress, ++step, totalSteps);

                        double write = pool.RunWindow(KernelWrite, window);
                        if (write > bestWrite) bestWrite = write;
                        Report(progress, ++step, totalSteps);

                        double copy = pool.RunWindow(KernelCopy, window);
                        if (copy > bestCopy) bestCopy = copy;
                        Report(progress, ++step, totalSteps);

                        randomWindows[window] = pool.RunWindow(KernelRandom, window);
                        Report(progress, ++step, totalSteps);
                    }

                    result.ReadGBs = bestRead;
                    result.WriteGBs = bestWrite;
                    result.CopyGBs = bestCopy;

                    // Median, not best: interference in the streaming kernels can only slow them
                    // down, but a preempted random worker's cursors bunch up and the window comes
                    // out too FAST - taking the maximum would keep exactly that window.
                    Array.Sort(randomWindows);
                    result.RandomGBs = (WindowsPerKernel & 1) == 1
                        ? randomWindows[WindowsPerKernel / 2]
                        : (randomWindows[WindowsPerKernel / 2 - 1] + randomWindows[WindowsPerKernel / 2]) / 2.0;
                    result.Ok = true;
                }
            }
            catch (OutOfMemoryException)
            {
                // Two buffers, so this needs twice the size the latency test does.
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
                if (pool != null)
                    pool.Shutdown();

                if (noGcHeld)
                {
                    try { GC.EndNoGCRegion(); } catch { }
                }

                GCSettings.LatencyMode = previousLatencyMode;

                source = null;
                destination = null;
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
            // Not readonly: Shutdown drops them so a worker that never came back cannot keep two
            // buffers - up to 2 GB - reachable for the rest of the session.
            private long[] source;
            private long[] destination;
            private readonly Thread[] workers;

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
            private int acked;

            public Pool(long[] source, long[] destination, int count, int[] cycleOrder, int lines)
            {
                this.source = source;
                this.destination = destination;

                var cores = BenchmarkNative.GetCoreMasks();
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
                    chunkStart[t] = (int)((long)count * t / workerCount);
                    chunkEnd[t] = (int)((long)count * (t + 1) / workerCount);

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
                try
                {
                    for (int t = 0; t < workerCount; t++)
                    {
                        int index = t;
                        ulong mask = cores != null ? cores[t] & (~cores[t] + 1) : 0;

                        workers[t] = new Thread(() => WorkerLoop(index, mask))
                        {
                            IsBackground = true,
                            Priority = ThreadPriority.AboveNormal,
                        };
                        workers[t].Start();
                    }
                }
                catch
                {
                    Shutdown();
                    throw;
                }
            }

            /// <summary>Runs one kernel for a fixed window and returns decimal GB/s.</summary>
            public double RunWindow(int kernelId, int windowIndex)
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

                    Thread.Sleep(WindowMs);
                    stop = true;

                    // Out of the workers' way for the drain, and blocking rather than spinning:
                    // with SMT off every logical CPU hosts a pinned worker, so a spinning
                    // coordinator fights the very threads it waits for - below their priority it
                    // starves outright, above it, it preempts them. Parked on an event at normal
                    // priority it does neither.
                    Thread.CurrentThread.Priority = previous;

                    if (!drained.Wait(WindowMs + 10000))
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
                }

                if (failed)
                    throw new InvalidOperationException("A bandwidth worker failed.");

                long moved = 0;
                for (int t = 0; t < workers.Length; t++)
                    moved += byteCounts[t * 8];

                return elapsedSeconds <= 0 ? 0 : moved / elapsedSeconds / 1e9;
            }

            public void Shutdown()
            {
                if (shutdown)
                    return;
                shutdown = true;

                quit = true;
                stop = true;
                generation++;

                // Both, because a worker may be parked on either parity.
                startSignals[0].Set();
                startSignals[1].Set();

                bool allStopped = true;
                foreach (var worker in workers)
                {
                    if (worker == null)
                        continue;
                    try { allStopped &= worker.Join(1000); } catch { allStopped = false; }
                }

                // Only dispose once every worker is out of Wait(); a late one would otherwise
                // fault on a disposed handle and take the process down. The two buffers are let go
                // either way - a stuck worker holding a handle open is a leak of three handles,
                // holding the buffers is a leak of gigabytes.
                if (allStopped)
                {
                    startSignals[0].Dispose();
                    startSignals[1].Dispose();
                    drained.Dispose();
                }

                source = null;
                destination = null;
            }

            private void WorkerLoop(int index, ulong pinMask)
            {
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
                                        cursors[c] = source[cursors[c]];
                                }
                                steps += 512 * RandomCursors;
                            }

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
                                        checksum += ReadBlock(source, position, position + block);
                                        moved += (long)block * sizeof(long);
                                        break;
                                    case KernelWrite:
                                        WriteBlock(destination, position, position + block);
                                        moved += (long)block * sizeof(long);
                                        break;
                                    default:
                                        Array.Copy(source, position, destination, position, block);
                                        moved += (long)block * sizeof(long) * 2;   // read once, written once
                                        break;
                                }

                                position += block;
                                if (position >= end)
                                    position = start;
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

            private static long ReadBlock(long[] buffer, int from, int to)
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
            /// Counted as the bytes the program asked to write, not the traffic the controller
            /// saw: an ordinary store first pulls the line in to own it, so the bus moves roughly
            /// twice this. Pessimistic by the same factor every time, which is what matters for
            /// comparing two runs.
            /// </summary>
            private static void WriteBlock(long[] buffer, int from, int to)
            {
                for (int i = from; i < to; i++)
                    buffer[i] = i;
            }
        }
    }
}
