using System;
using System.Diagnostics;
using System.Threading;

namespace ZenTimings
{
    public class MemoryBandwidthResult
    {
        public double ReadGBs { get; set; }
        public double WriteGBs { get; set; }
        public double CopyGBs { get; set; }
        public int Threads { get; set; }
        public bool Ok { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Streams a buffer far larger than the caches and reports how fast it moved.
    /// </summary>
    /// <remarks>
    /// The opposite end of the same question the latency test asks. Sequential and multi-threaded
    /// on purpose: one thread cannot keep enough requests in flight to saturate a DDR5 channel
    /// pair, so a single-threaded figure measures the core rather than the memory.
    ///
    /// Unlike latency, this number does not need large pages to be meaningful - the walk is
    /// sequential, so the TLB is barely exercised - and it lands much closer to what other tools
    /// report.
    /// </remarks>
    public static class MemoryBandwidthTest
    {
        private const int Passes = 5;

        public static MemoryBandwidthResult Run(int bufferMegabytes, Action<double> progress)
        {
            var result = new MemoryBandwidthResult();

            if (bufferMegabytes < 16 || bufferMegabytes > 1024)
            {
                result.Error = "Buffer size out of range.";
                return result;
            }

            long[] source = null;
            long[] destination = null;

            try
            {
                long bytes = (long)bufferMegabytes * 1024 * 1024;
                int count = (int)(bytes / sizeof(long));

                source = new long[count];
                destination = new long[count];

                // First touch, so the timed passes are not paying for page faults.
                for (int i = 0; i < count; i += 512)
                {
                    source[i] = i;
                    destination[i] = 0;
                }

                int threads = Math.Max(1, Environment.ProcessorCount);
                result.Threads = threads;

                double bestRead = 0, bestWrite = 0, bestCopy = 0;

                for (int pass = 0; pass < Passes; pass++)
                {
                    double read = Measure(threads, count,
                        (from, to) => Sink = ReadChunk(source, from, to), bytes);
                    if (read > bestRead) bestRead = read;

                    double write = Measure(threads, count,
                        (from, to) => WriteChunk(destination, from, to), bytes);
                    if (write > bestWrite) bestWrite = write;

                    double copy = Measure(threads, count,
                        (from, to) => CopyChunk(source, destination, from, to), bytes * 2);
                    if (copy > bestCopy) bestCopy = copy;

                    if (progress != null)
                        progress((pass + 1) / (double)Passes);
                }

                result.ReadGBs = bestRead;
                result.WriteGBs = bestWrite;
                result.CopyGBs = bestCopy;
                result.Ok = true;
            }
            catch (OutOfMemoryException)
            {
                // Two buffers, so this needs twice the size the latency test does.
                result.Error = "Not enough free memory for that buffer size.";
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            finally
            {
                source = null;
                destination = null;
                GC.Collect();
            }

            return result;
        }

        /// <summary>
        /// Splits the buffer across threads and returns GB/s for <paramref name="bytesMoved"/>,
        /// which is twice the buffer for a copy - it is read once and written once.
        /// </summary>
        private static double Measure(int threads, int total, Action<int, int> work, long bytesMoved)
        {
            var workers = new Thread[threads];
            var ready = new ManualResetEventSlim(false);

            // Chunk boundaries are computed up front so the timed section contains nothing but the
            // memory traffic itself.
            var starts = new int[threads];
            var ends = new int[threads];
            for (int t = 0; t < threads; t++)
            {
                starts[t] = (int)((long)total * t / threads);
                ends[t] = (int)((long)total * (t + 1) / threads);
            }

            for (int t = 0; t < threads; t++)
            {
                int index = t;
                workers[t] = new Thread(() =>
                {
                    ready.Wait();
                    work(starts[index], ends[index]);
                });
                workers[t].IsBackground = true;
                workers[t].Priority = ThreadPriority.AboveNormal;
                workers[t].Start();
            }

            var watch = Stopwatch.StartNew();
            ready.Set();

            for (int t = 0; t < threads; t++)
                workers[t].Join();

            watch.Stop();
            ready.Dispose();

            double seconds = watch.Elapsed.TotalSeconds;
            return seconds <= 0 ? 0 : bytesMoved / seconds / (1024.0 * 1024.0 * 1024.0);
        }

        /// <summary>
        /// Where the read test's total goes. Without somewhere to put it the sum is dead code and
        /// the JIT is free to delete the loop that produced it.
        /// </summary>
        private static volatile object Sink;

        private static long ReadChunk(long[] buffer, int from, int to)
        {
            long sum = 0;

            // Four accumulators: a single dependent chain would be limited by add latency rather
            // than by how fast the lines arrive.
            long a = 0, b = 0, c = 0, d = 0;
            int i = from;
            for (; i + 3 < to; i += 4)
            {
                a += buffer[i];
                b += buffer[i + 1];
                c += buffer[i + 2];
                d += buffer[i + 3];
            }

            for (; i < to; i++)
                sum += buffer[i];

            return sum + a + b + c + d;
        }

        /// <summary>
        /// Fills a chunk. Counted as the bytes the program asked to write, not the traffic the
        /// controller actually saw: an ordinary store first pulls the line in to own it, so the bus
        /// moves roughly twice this. Tools that use non-temporal stores skip that fetch and report
        /// a higher figure - this one is pessimistic by the same factor every time, which is what
        /// matters for comparing two runs.
        /// </summary>
        private static void WriteChunk(long[] buffer, int from, int to)
        {
            for (int i = from; i < to; i++)
                buffer[i] = i;
        }

        private static void CopyChunk(long[] source, long[] destination, int from, int to)
        {
            Array.Copy(source, from, destination, from, to - from);
        }
    }
}
