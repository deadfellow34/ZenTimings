using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ZenTimings
{
    [Serializable]
    public class BenchmarkSetting
    {
        public string Key { get; set; }
        public string Value { get; set; }
    }

    /// <summary>
    /// One benchmark run together with the memory setup it was measured on.
    /// </summary>
    /// <remarks>
    /// The numbers on their own are worthless a week later - "92.9 ns" says nothing without the
    /// frequency and the timings that produced it. Storing the whole snapshot alongside is what
    /// turns a run into something that can be compared with the next one.
    /// </remarks>
    [Serializable]
    public class BenchmarkRun
    {
        public string Timestamp { get; set; }
        public double LatencyNs { get; set; }
        public double ReadGBs { get; set; }
        public double WriteGBs { get; set; }
        public double CopyGBs { get; set; }
        public double RandomGBs { get; set; }
        public int BufferMegabytes { get; set; }
        public double SpreadNs { get; set; }
        public bool LargePages { get; set; }

        /// <summary>The run's own verdicts, so a figure the app distrusted is never shown as clean.</summary>
        public bool Noisy { get; set; }
        public bool CacheBound { get; set; }

        /// <summary>
        /// CPU, board and BIOS as they were when the run was measured. Null on runs written before
        /// this was recorded - the report then says nothing rather than guessing.
        /// </summary>
        /// <remarks>
        /// Deliberately not part of <see cref="Schema"/>: it describes the machine, not the
        /// measurement, so an older run without it is still comparable with a newer one.
        /// </remarks>
        public string System { get; set; }

        /// <summary>
        /// Measurement generation. Runs from different generations are not comparable, so the
        /// baseline delta is withheld across them. Files written before this field load as 0.
        /// </summary>
        public int Schema { get; set; }

        /// <summary>
        /// What the current measurement core produces.
        /// </summary>
        /// <remarks>
        /// 3: the bandwidth workers took a slice each instead of sharing the buffer. Before that a
        /// worker could drift onto the one ahead and read its lines out of the L3, and both counted
        /// the bytes - so the older read and copy figures sit above what the bus can carry. They are
        /// a different measurement, not a slower one.
        /// </remarks>
        public const int CurrentSchema = 3;

        /// <summary>The run other runs are measured against. Old files load with it unset.</summary>
        public bool IsBaseline { get; set; }

        public List<BenchmarkSetting> Settings { get; set; }

        public BenchmarkRun()
        {
            Settings = new List<BenchmarkSetting>();
            // Seconds included: two runs a minute apart used to be indistinguishable, and the
            // baseline pin identifies a run by this string.
            Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        /// <remarks>
        /// Tolerates a missing or holed list. The serializer produces both from a hand-edited file
        /// - a nil Settings element, or a nil entry inside it - and this is read for every row of
        /// the history, so a throw here is a benchmark window that will not open at all.
        /// </remarks>
        public string Get(string key)
        {
            if (Settings == null)
                return null;

            var found = Settings.FirstOrDefault(
                s => s != null && string.Equals(s.Key, key, StringComparison.Ordinal));
            return found != null ? found.Value : null;
        }

        /// <summary>"84.0 ns", or empty when the latency phase produced nothing.</summary>
        public string LatencyText
        {
            get
            {
                return LatencyNs > 0
                    ? LatencyNs.ToString("F1", CultureInfo.InvariantCulture) + " ns"
                    : string.Empty;
            }
        }

        /// <summary>"R 40.3   W 13.4   C 35.9 GB/s   Rnd 15.4", or empty.</summary>
        public string BandwidthText
        {
            get
            {
                if (ReadGBs <= 0)
                    return string.Empty;

                string text = string.Format(CultureInfo.InvariantCulture,
                    "R {0:F1}   W {1:F1}   C {2:F1} GB/s", ReadGBs, WriteGBs, CopyGBs);

                if (RandomGBs > 0)
                    text += string.Format(CultureInfo.InvariantCulture, "   Rnd {0:F1}", RandomGBs);

                return text;
            }
        }

        /// <summary>Reads like a profile name: when, at what speed, and what it scored.</summary>
        public string Title
        {
            get
            {
                string speed = Get("Speed") ?? "?";
                string cl = Get("tCL");

                string label = Timestamp + "  -  " + speed;
                if (!string.IsNullOrEmpty(cl))
                    label += " CL" + cl;

                if (LatencyNs > 0)
                    label += "  -  " + LatencyText;

                return label;
            }
        }
    }

    /// <summary>Benchmark runs kept next to the executable, newest first.</summary>
    public static class BenchmarkHistory
    {
        /// <summary>Enough to spot a trend; beyond that the list stops being browsable.</summary>
        private const int MaxRuns = 50;

        private static readonly string Filename =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "benchmarks.xml");

        // The worker thread adds a finished run while the UI thread may be pinning a baseline -
        // both are read-modify-write over the same file.
        private static readonly object Sync = new object();

        public static List<BenchmarkRun> Load()
        {
            lock (Sync)
            {
                return LoadLocked();
            }
        }

        private static List<BenchmarkRun> LoadLocked()
        {
            try
            {
                if (File.Exists(Filename))
                {
                    var loaded = XmlUtils.DeserializeFromXml<List<BenchmarkRun>>(Filename);
                    if (loaded != null)
                        return loaded;
                }
            }
            catch
            {
                // A corrupt or hand-edited file must not stop the app from starting.
            }

            return new List<BenchmarkRun>();
        }

        public static void Add(BenchmarkRun run)
        {
            if (run == null)
                return;

            lock (Sync)
            {
                var runs = LoadLocked();
                runs.Insert(0, run);

                // The pinned baseline survives the trim - it is the one row the user chose.
                while (runs.Count > MaxRuns)
                {
                    int drop = runs.FindLastIndex(r => !r.IsBaseline);
                    if (drop < 0)
                        break;
                    runs.RemoveAt(drop);
                }

                SaveLocked(runs);
            }
        }

        /// <summary>
        /// Pins (or unpins, when it already is the baseline) one run, atomically against a run
        /// finishing in the background. The timestamp guards against the list having shifted
        /// between display and click.
        /// </summary>
        public static void PinBaseline(int index, string timestamp, double latencyNs)
        {
            lock (Sync)
            {
                var runs = LoadLocked();

                // Position first, then identity: a run finishing between display and click shifts
                // every index by one. The latency figure disambiguates same-second entries.
                // Both ends are checked before the indexer: the identity lookup below is the
                // fallback for a position that does not fit, negative included.
                if (index < 0 || index >= runs.Count || runs[index].Timestamp != timestamp
                    || runs[index].LatencyNs != latencyNs)
                {
                    index = runs.FindIndex(r => r.Timestamp == timestamp && r.LatencyNs == latencyNs);
                    if (index < 0)
                        index = runs.FindIndex(r => r.Timestamp == timestamp);
                }

                if (index < 0)
                    return;

                bool pin = !runs[index].IsBaseline;
                for (int i = 0; i < runs.Count; i++)
                    runs[i].IsBaseline = pin && i == index;

                SaveLocked(runs);
            }
        }

        private static void SaveLocked(List<BenchmarkRun> runs)
        {
            try
            {
                File.WriteAllText(Filename, XmlUtils.SerializeToXml(runs));
            }
            catch
            {
                // A read-only install folder is a reason to lose the history, not to fail the run.
            }
        }

        /// <summary>Builds a run from a finished measurement and the settings it was taken under.</summary>
        public static BenchmarkRun Capture(
            MemoryLatencyResult latency,
            MemoryBandwidthResult bandwidth,
            Dictionary<string, string> settings)
        {
            var run = new BenchmarkRun();

            run.Schema = BenchmarkRun.CurrentSchema;
            run.System = SystemDescription();

            if (latency != null && latency.Ok)
            {
                run.LatencyNs = latency.Nanoseconds;
                run.BufferMegabytes = latency.BufferMegabytes;
                run.SpreadNs = latency.SpreadNs;
                run.LargePages = latency.LargePages;
                run.Noisy = latency.Noisy;
                run.CacheBound = latency.CacheBound;
            }

            if (bandwidth != null && bandwidth.Ok)
            {
                run.ReadGBs = bandwidth.ReadGBs;
                run.WriteGBs = bandwidth.WriteGBs;
                run.CopyGBs = bandwidth.CopyGBs;
                run.RandomGBs = bandwidth.RandomGBs;
            }

            if (settings != null)
            {
                foreach (var pair in settings)
                    run.Settings.Add(new BenchmarkSetting { Key = pair.Key, Value = pair.Value });
            }

            return run;
        }

        /// <summary>
        /// The machine as it is right now, stamped into the run. Same shape as the one saved with
        /// an OC profile.
        /// </summary>
        /// <remarks>
        /// Recorded at capture, never at export: the point of an exported run is to say what this
        /// board did on that BIOS, and reading it live would credit whatever is installed the day
        /// the page is written instead.
        /// </remarks>
        private static string SystemDescription()
        {
            try
            {
                var info = CpuSingleton.Instance.systemInfo;
                return info.CpuName + "  |  " + info.MbName + "  |  BIOS " + info.BiosVersion;
            }
            catch
            {
                return null;
            }
        }
    }
}
