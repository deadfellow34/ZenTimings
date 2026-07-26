using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ZenTimings
{
    /// <summary>
    /// One JEDEC-rated timing decoded straight from the raw DDR5 SPD image.
    /// </summary>
    public class RatedTiming
    {
        public string Name { get; set; }

        /// <summary>Rated minimum in picoseconds (JEDEC, frequency independent).</summary>
        public int Picoseconds { get; set; }

        /// <summary>Rated minimum in clocks as stored in SPD (0 when the SPD has no nCK byte for it).</summary>
        public int RatedNck { get; set; }

        public double Nanoseconds
        {
            get { return Picoseconds / 1000.0; }
        }

        /// <summary>Clocks required at an arbitrary tCK, using the JEDEC ceiling rule.</summary>
        public int NckAt(double tckPicoseconds)
        {
            if (tckPicoseconds <= 0 || Picoseconds <= 0)
                return 0;
            return (int)Math.Ceiling(Picoseconds / tckPicoseconds - 0.001);
        }
    }

    /// <summary>
    /// Decodes the JEDEC (JESD400-5) base timing block out of the 1024-byte DDR5 SPD image.
    /// These are the module's *rated* minimums - the values a JEDEC-compliant controller may not
    /// go below - as opposed to the applied values ZenTimings reads back from the UMC registers.
    /// Offsets verified byte-exact against a real module (Corsair CMH32GX5M2X7200C34).
    /// </summary>
    public static class SpdRatedTimings
    {
        // offset -> (name, unit). ps entries are u16 little-endian picoseconds.
        private static readonly int[][] PsEntries =
        {
            // { offset, nckOffsetOrMinusOne }
            new[] { 30, -1 }, // tAA  (tCL)
            new[] { 32, -1 }, // tRCD
            new[] { 34, -1 }, // tRP
            new[] { 36, -1 }, // tRAS
            new[] { 38, -1 }, // tRC
            new[] { 40, -1 }, // tWR
            new[] { 70, 72 }, // tRRD_L
            new[] { 73, 75 }, // tCCD_L
            new[] { 76, 78 }, // tCCD_L_WR
            new[] { 79, 81 }, // tCCD_L_WR2
            new[] { 82, 84 }, // tFAW
            new[] { 85, 87 }, // tWTR_L
            new[] { 88, 90 }, // tWTR_S
            new[] { 91, 93 }, // tRTP
        };

        private static readonly string[] PsNames =
        {
            "tCL", "tRCD", "tRP", "tRAS", "tRC", "tWR",
            "tRRDL", "tCCD_L", "tCCD_L_WR", "tCCD_L_WR2",
            "tFAW", "tWTRL", "tWTRS", "tRTP",
        };

        // These three are stored in *nanoseconds*, not picoseconds.
        private static readonly int[] NsOffsets = { 42, 44, 46 };
        private static readonly string[] NsNames = { "tRFC", "tRFC2", "tRFCsb" };

        private const int TckAvgMinOffset = 20;

        /// <summary>Rated tCK of the SPD base profile, in picoseconds (0 when unknown).</summary>
        public static int BaseTckPicoseconds { get; private set; }

        private static readonly object CacheSync = new object();
        private static Dictionary<string, RatedTiming> _cache;

        /// <summary>
        /// The decoded table, read once and kept. Reading it means a full 1 KB SPD transfer over
        /// SMBus per module, which is far too slow to do on the UI thread every time a tooltip opens.
        /// </summary>
        public static Dictionary<string, RatedTiming> Cached
        {
            get
            {
                lock (CacheSync)
                {
                    if (_cache == null)
                    {
                        try { _cache = Read(); }
                        catch { _cache = new Dictionary<string, RatedTiming>(StringComparer.OrdinalIgnoreCase); }
                    }

                    return _cache;
                }
            }
        }

        /// <summary>Drops the cache so the next read goes back to the module.</summary>
        public static void Invalidate()
        {
            lock (CacheSync)
            {
                _cache = null;
            }
        }

        /// <summary>Fills the cache off the UI thread, so the first tooltip does not stall.</summary>
        public static void Prewarm()
        {
            var unused = Cached;
        }

        /// <summary>
        /// Reads the raw SPD of the first valid module and decodes the rated timings.
        /// Returns an empty dictionary when the SPD image is not available - every caller
        /// treats that as "no rated data", never as an error.
        /// </summary>
        public static Dictionary<string, RatedTiming> Read()
        {
            var result = new Dictionary<string, RatedTiming>(StringComparer.OrdinalIgnoreCase);

            byte[] spd = GetRawSpd();
            if (spd == null || spd.Length < 128)
                return result;

            int tck = ReadU16(spd, TckAvgMinOffset);
            BaseTckPicoseconds = tck;

            for (int i = 0; i < PsEntries.Length && i < PsNames.Length; i++)
            {
                int offset = PsEntries[i][0];
                int nckOffset = PsEntries[i][1];
                if (offset + 1 >= spd.Length)
                    continue;

                int ps = ReadU16(spd, offset);
                if (ps <= 0)
                    continue;

                int nck = 0;
                if (nckOffset >= 0 && nckOffset < spd.Length)
                    nck = spd[nckOffset];

                result[PsNames[i]] = new RatedTiming
                {
                    Name = PsNames[i],
                    Picoseconds = ps,
                    RatedNck = nck,
                };
            }

            for (int i = 0; i < NsOffsets.Length && i < NsNames.Length; i++)
            {
                int offset = NsOffsets[i];
                if (offset + 1 >= spd.Length)
                    continue;

                int ns = ReadU16(spd, offset);
                if (ns <= 0)
                    continue;

                result[NsNames[i]] = new RatedTiming
                {
                    Name = NsNames[i],
                    Picoseconds = ns * 1000,
                    RatedNck = 0,
                };
            }

            return result;
        }

        private static int ReadU16(byte[] b, int offset)
        {
            // Explicit widening: a plain (b[o+1] << 8) on a byte silently truncates.
            return (int)b[offset] + ((int)b[offset + 1] * 256);
        }

        /// <summary>
        /// Pulls the 1024-byte SPD image out of ZenStates-Core. Reflection keeps this compiling
        /// against Core versions whose Ddr5SpdInfo layout differs; a missing member just means
        /// "no rated data" instead of a build break.
        /// </summary>
        private static byte[] GetRawSpd()
        {
            try
            {
                // The live memoryConfig *may* already carry a full image - use it when present.
                var memoryConfig = CpuSingleton.Instance?.memoryConfig;
                byte[] raw = FindRawSpd(memoryConfig?.SpdInfo as System.Collections.IEnumerable);
                if (raw != null && raw.Length >= 128)
                    return raw;

                // It usually does not: GetMemoryConfig() only does a light SPD read, so the RawSpd
                // field of every memoryConfig.SpdInfo entry stays empty. The full read is a separate
                // static call - and, crucially, ReadDdr5SpdAll() RETURNS a fresh dictionary whose
                // entries carry the 1 KB RawSpd. It does NOT populate memoryConfig.SpdInfo in place,
                // so the bytes have to be read out of the returned dictionary. (Verified against
                // ZenStates-Core on the target rig: memoryConfig.SpdInfo RawSpd = 0 bytes, the
                // returned dictionary's RawSpd = 1024 bytes.)
                var full = InvokeReadDdr5SpdAll();
                return FindRawSpd(full);
            }
            catch
            {
                return null;
            }
        }

        private static byte[] FindRawSpd(System.Collections.IEnumerable spdInfo)
        {
            if (spdInfo == null)
                return null;

            foreach (var entry in spdInfo)
            {
                if (entry == null) continue;

                object value = entry.GetType().GetProperty("Value")?.GetValue(entry, null);
                if (value == null) continue;

                var field = value.GetType().GetField("RawSpd");
                var raw = field?.GetValue(value) as byte[];
                if (raw != null && raw.Length >= 128)
                    return raw;
            }

            return null;
        }

        /// <summary>
        /// Calls the static <c>Ddr5SpdReader.ReadDdr5SpdAll()</c> and returns the dictionary it
        /// produces (mapping I2C address to a fully-read Ddr5SpdInfo). Returns null on any failure,
        /// which the caller treats as "no rated data". This is a full SMBus transfer per module -
        /// callers run it off the UI thread.
        /// </summary>
        private static System.Collections.IEnumerable InvokeReadDdr5SpdAll()
        {
            try
            {
                var coreAssembly = typeof(ZenStates.Core.Cpu).Assembly;
                var readerType = coreAssembly.GetType("ZenStates.Core.DRAM.Ddr5SpdReader");
                var method = readerType?
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "ReadDdr5SpdAll" && m.GetParameters().Length == 0);

                return method?.Invoke(null, null) as System.Collections.IEnumerable;
            }
            catch
            {
                // Best effort - the caller degrades to "no rated data".
                return null;
            }
        }
    }
}
