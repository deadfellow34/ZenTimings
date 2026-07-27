using System;
using System.Diagnostics;
using System.Runtime;
using System.Threading;

namespace ZenTimings
{
    public class MemoryLatencyResult
    {
        public double Nanoseconds { get; set; }
        public double SpreadNs { get; set; }
        public int BufferMegabytes { get; set; }
        public bool Ok { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Measures how long one dependent memory access takes.
    /// </summary>
    /// <remarks>
    /// A pointer chase: the buffer is filled with a single random cycle through its own cache
    /// lines, and each read supplies the index of the next one. Because every access depends on the
    /// previous result the CPU cannot overlap them, and the random order defeats the prefetcher -
    /// so the elapsed time divided by the number of hops is the latency of one access, not the
    /// throughput of many.
    ///
    /// The absolute figure reads a few ns above what AIDA64 reports. AIDA maps its buffer with
    /// large pages; without them a random walk over hundreds of megabytes misses in the TLB often
    /// enough to add its own cost, and large pages need SeLockMemoryPrivilege, which is a policy
    /// change rather than something an application can arrange for itself. What the number is good
    /// for is comparison: the same machine measured twice around a BIOS change is repeatable to a
    /// few tenths of a nanosecond, which is the question a memory tune actually asks.
    /// </remarks>
    public static class MemoryLatencyTest
    {
        private const int CacheLineBytes = 64;
        private const int IntsPerLine = CacheLineBytes / sizeof(int);

        /// <summary>Hops per timed pass. ~5 M x ~80 ns is a little under half a second.</summary>
        private const int HopsPerPass = 5000000;

        private const int Passes = 7;

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);

        /// <summary>
        /// Blocking - run it on a background thread. <paramref name="progress"/> is called with
        /// 0..1 and may be null.
        /// </summary>
        public static MemoryLatencyResult Run(int bufferMegabytes, Action<double> progress)
        {
            var result = new MemoryLatencyResult { BufferMegabytes = bufferMegabytes };

            if (bufferMegabytes < 16 || bufferMegabytes > 1024)
            {
                result.Error = "Buffer size out of range.";
                return result;
            }

            int[] chain = null;
            var previousLatencyMode = GCSettings.LatencyMode;
            ThreadPriority previousPriority = Thread.CurrentThread.Priority;
            UIntPtr previousAffinity = UIntPtr.Zero;
            bool affinityHeld = false;

            try
            {
                long bytes = (long)bufferMegabytes * 1024 * 1024;
                int lines = (int)(bytes / CacheLineBytes);

                chain = BuildChain(lines, p => { if (progress != null) progress(p * 0.5); });

                // A collection in the middle of a pass would land in the measurement.
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                Thread.CurrentThread.Priority = ThreadPriority.Highest;

                // Pinned to one core: being migrated mid-pass means finishing on a core whose
                // caches and TLB know nothing about this buffer, which shows up as a slow pass.
                Thread.BeginThreadAffinity();
                affinityHeld = true;
                previousAffinity = SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)1);

                // A pass has to walk at least one hop per cache line, or a buffer larger than
                // HopsPerPass x 64 B is never fully visited: everything above ~305 MiB used to
                // measure the same working set, so the 512 and 1024 MB choices reported a smaller
                // footprint than asked for - and therefore a better latency than the label implies.
                int hops = Math.Max(HopsPerPass, lines);

                // Pull the whole buffer through the caches once so the timed passes are not paying
                // for first-touch page faults.
                Chase(chain, lines);

                var samples = new double[Passes];
                for (int pass = 0; pass < Passes; pass++)
                {
                    var watch = Stopwatch.StartNew();
                    int landed = Chase(chain, hops);
                    watch.Stop();

                    // Keeps the JIT from deciding the whole loop is dead code.
                    if (landed < 0)
                        throw new InvalidOperationException();

                    double ns = watch.Elapsed.TotalMilliseconds * 1000000.0 / hops;
                    samples[pass] = ns;

                    if (progress != null)
                        progress(0.5 + 0.5 * (pass + 1) / (double)Passes);
                }

                Array.Sort(samples);

                // The fastest pass, not the median. Anything that interferes - a scheduler tick,
                // another process touching memory - can only make a pass slower, so the shortest
                // one is the closest look at the hardware. The spread comes along so a run that
                // was too noisy to trust is visible as such.
                result.Nanoseconds = samples[0];
                result.SpreadNs = samples[Passes - 1] - samples[0];
                result.Ok = true;
            }
            catch (OutOfMemoryException)
            {
                result.Error = "Not enough free memory for that buffer size.";
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            finally
            {
                if (affinityHeld)
                {
                    try
                    {
                        if (previousAffinity != UIntPtr.Zero)
                            SetThreadAffinityMask(GetCurrentThread(), previousAffinity);
                    }
                    catch { }

                    Thread.EndThreadAffinity();
                }

                Thread.CurrentThread.Priority = previousPriority;
                GCSettings.LatencyMode = previousLatencyMode;

                chain = null;
                GC.Collect();
            }

            return result;
        }

        /// <summary>
        /// One random cycle visiting every cache line exactly once. A cycle rather than a shuffled
        /// list because the chase has to keep moving for as long as it is asked to, without ever
        /// running off the end or falling into a short loop that would sit in cache.
        /// </summary>
        private static int[] BuildChain(int lines, Action<double> progress)
        {
            var order = new int[lines];
            for (int i = 0; i < lines; i++)
                order[i] = i;

            // Deterministic seed: two runs on the same machine should differ because the hardware
            // differed, not because the walk did.
            var random = new Random(12345);
            for (int i = lines - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                int t = order[i]; order[i] = order[j]; order[j] = t;

                if (progress != null && (i & 0xFFFFF) == 0)
                    progress(1.0 - i / (double)lines);
            }

            long total = (long)lines * IntsPerLine;

            // .NET caps a single object at 2 GB unless the runtime is configured otherwise, so the
            // buffer has to stay under it - hence the 1 GB ceiling on the size the UI offers.
            if (total > int.MaxValue / 2)
                throw new OutOfMemoryException();

            var chain = new int[(int)total];

            // order[k] -> order[k+1], closing back to order[0].
            for (int k = 0; k < lines; k++)
            {
                int from = order[k];
                int to = order[(k + 1) % lines];
                chain[from * IntsPerLine] = to * IntsPerLine;
            }

            if (progress != null)
                progress(1.0);

            return chain;
        }

        private static int Chase(int[] chain, int hops)
        {
            int index = 0;
            for (int i = 0; i < hops; i++)
                index = chain[index];

            return index;
        }
    }
}
