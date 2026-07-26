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
        public int BufferMegabytes { get; set; }
        public List<BenchmarkSetting> Settings { get; set; }

        public BenchmarkRun()
        {
            Settings = new List<BenchmarkSetting>();
            Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        public string Get(string key)
        {
            var found = Settings.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));
            return found != null ? found.Value : null;
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
                    label += "  -  " + LatencyNs.ToString("F1", CultureInfo.InvariantCulture) + " ns";

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

        public static List<BenchmarkRun> Load()
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

            try
            {
                var runs = Load();
                runs.Insert(0, run);

                while (runs.Count > MaxRuns)
                    runs.RemoveAt(runs.Count - 1);

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

            if (latency != null && latency.Ok)
            {
                run.LatencyNs = latency.Nanoseconds;
                run.BufferMegabytes = latency.BufferMegabytes;
            }

            if (bandwidth != null && bandwidth.Ok)
            {
                run.ReadGBs = bandwidth.ReadGBs;
                run.WriteGBs = bandwidth.WriteGBs;
                run.CopyGBs = bandwidth.CopyGBs;
            }

            if (settings != null)
            {
                foreach (var pair in settings)
                    run.Settings.Add(new BenchmarkSetting { Key = pair.Key, Value = pair.Value });
            }

            return run;
        }
    }
}
