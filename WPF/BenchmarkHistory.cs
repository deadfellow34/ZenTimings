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
    /// One cache level as the export needs it - the display figures only, flattened off the live
    /// <see cref="CacheRung"/> so it serializes to the history file and travels with the run.
    /// </summary>
    [Serializable]
    public class BenchmarkRung
    {
        public string Level { get; set; }
        public long CacheBytes { get; set; }
        public double Nanoseconds { get; set; }
        public double ReadGBs { get; set; }
        public double WriteGBs { get; set; }
        public double CopyGBs { get; set; }
        public double ReadAllGBs { get; set; }
        public double WriteAllGBs { get; set; }
        public double CopyAllGBs { get; set; }
        public bool LargePages { get; set; }

        /// <summary>
        /// Whether the rung's OWN buffer got large pages, as on <see cref="CacheRung"/>. Apart
        /// from <see cref="LargePages"/>, which the all-core pass also clears for its workers'
        /// buffers, so the two together say which figures the fallback actually touched.
        /// </summary>
        /// <remarks>
        /// Null on runs written before the field. The report then makes the heavier claim it
        /// always made, rather than guessing which half of the row fell back.
        /// </remarks>
        public bool? OwnLargePages { get; set; }

        /// <summary>The all-core crew, as on <see cref="CacheRung"/> - fewer than the domain's
        /// cores on a small L3, and the export's footnote says so.</summary>
        public int AllCoreWorkers { get; set; }
        public int AllCoreOf { get; set; }

        /// <summary>
        /// Cores the package held, as on <see cref="CacheRung"/> - what the all-core columns would
        /// cover if this rung's cache served all of them.
        /// </summary>
        /// <remarks>
        /// Deliberately not part of <see cref="BenchmarkRun.Schema"/>: the measurement did not
        /// change, only what is written down about it. Zero on runs written before the field and
        /// on runs whose all-core pass established no crew - "not recorded", never a claim that
        /// the crew was the package. Such a run is never marked or annotated for scope, and a
        /// delta against it is decided on the crew fields beside this one, which every stored
        /// rung carries.
        /// </remarks>
        public int PackageCores { get; set; }
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

        /// <summary>
        /// The bandwidth pass's own page mode. It asks for two buffers where the walk asks for
        /// one and can fall back where the walk did not, so <see cref="LargePages"/> - the walk's
        /// flag - does not describe it. Null where nothing recorded it: runs written before the
        /// field, and runs whose bandwidth pass produced no figures.
        /// </summary>
        /// <remarks>
        /// Deliberately not part of <see cref="Schema"/>: the measurement did not change, only
        /// what is written down about it. Null rather than false for the same reason - "not
        /// recorded" is not a claim that the pass ran on 4K pages, and a delta is withheld on it.
        /// </remarks>
        public bool? BandwidthLargePages { get; set; }

        /// <summary>The run's own verdicts, so a figure the app distrusted is never shown as clean.</summary>
        public bool Noisy { get; set; }
        public bool CacheBound { get; set; }

        /// <summary>
        /// The bandwidth pass measured a figure above what the DRAM bus can carry and withheld it,
        /// so a score missing from a page reads as one rejected rather than one never taken.
        /// </summary>
        /// <remarks>
        /// Deliberately not part of <see cref="Schema"/>: the measurement did not change, only what
        /// is written down about it. False on runs written before the field and on runs that
        /// withheld nothing - both say nothing rather than claiming every figure stood.
        /// </remarks>
        public bool AboveBus { get; set; }

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
        /// the bytes - so the older read and copy figures sit above what the bus can carry.
        ///
        /// 4: the stores stopped owning the line first. An ordinary store fetches a cache line
        /// before it can write it, so half the bus went on traffic nobody asked for; streaming
        /// stores skip it and the write figure roughly doubles. Copy moved too, and the buffers
        /// became large-page backed. Every one of those changes what the number means.
        ///
        /// 5: the sweep grew to every die and every single core, so a kernel can now be published
        /// by a worker set the old one never offered, and the winner is measured again at full
        /// length instead of being kept from a sweep window. Where the sweep finds something better
        /// the figure goes up, which is not a change in the hardware.
        ///
        /// 6: the buffer is fixed at 1024 MB and the read kernel cycles both of them, so the read
        /// stream is 2 GB rather than the 256 MB it defaulted to. Whatever the L3 holds back from
        /// the DRAM is counted as bandwidth, and how much that is depends on the cache and on the
        /// replacement policy - measured at nothing on a 4 MB Zen+ L3, unmeasured on the large
        /// caches where the read figure was seen above the bus. Either way the stream is not the
        /// one the older runs measured.
        ///
        /// 7: the latency buffer is faulted in address order before the chain is written, so on
        /// 4K pages the frames draw far more contiguous and the walk pays less for its page
        /// walks - measured at ~0.6 ns on the figure, none of it from the memory. Large-page
        /// runs are unmoved, but the field is one for the whole run, so both paths carry it.
        /// </remarks>
        public const int CurrentSchema = 7;

        /// <summary>The run other runs are measured against. Old files load with it unset.</summary>
        public bool IsBaseline { get; set; }

        public List<BenchmarkSetting> Settings { get; set; }

        /// <summary>
        /// The cache ladder that ran after the DRAM figures - one entry per level, single- and
        /// all-core. Null on runs measured before it was captured; the export then shows the
        /// memory figures alone, as it always did.
        /// </summary>
        public List<BenchmarkRung> Rungs { get; set; }

        /// <summary>Vector width the ladder's throughput was taken at, for the export's footnote.</summary>
        public int VectorBits { get; set; }

        public BenchmarkRun()
        {
            Settings = new List<BenchmarkSetting>();
            // Seconds included: two runs a minute apart used to be indistinguishable, and the
            // baseline pin identifies a run by this string.
            Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        /// <remarks>
        /// Tolerates a holed list: a nil entry inside Settings survives deserialization, and this
        /// is read for every row of the history, so a throw here is a benchmark window that will
        /// not open at all. The null test on the list itself is for the public setter - a nil or
        /// missing Settings element deserializes to the constructor's empty list, not to null.
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

        /// <summary>"R 40.3   W 13.4   C 35.9   Rnd 15.4 GB/s", or empty.</summary>
        public string BandwidthText
        {
            get
            {
                // Each figure stands on its own. Gating all four on the read meant one score the
                // ceiling filter rejected erased three that measured perfectly well.
                string text = "";

                if (ReadGBs > 0)
                    text += string.Format(CultureInfo.InvariantCulture, "R {0:F1}   ", ReadGBs);
                if (WriteGBs > 0)
                    text += string.Format(CultureInfo.InvariantCulture, "W {0:F1}   ", WriteGBs);
                if (CopyGBs > 0)
                    text += string.Format(CultureInfo.InvariantCulture, "C {0:F1}   ", CopyGBs);
                if (RandomGBs > 0)
                    text += string.Format(CultureInfo.InvariantCulture, "Rnd {0:F1}", RandomGBs);

                // The unit belongs to the row, not to one term: welding it to copy left the row
                // unitless whenever copy was the figure the ceiling filter rejected.
                text = text.Trim();
                return text.Length == 0 ? text : text + " GB/s";
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

                    // A nil element deserializes to a null entry without failing, and every
                    // reader walks this list unguarded - one hand-edited row would take the
                    // window down on open.
                    if (loaded != null)
                        return loaded.Where(r => r != null).ToList();
                }
            }
            catch
            {
                // A corrupt or hand-edited file must not stop the app from starting - and must not
                // be written over either. The empty list below is what the next run saves, and
                // File.WriteAllText truncates, so fifty runs and the pinned baseline among them
                // would go with no copy anywhere. Moved aside once instead, under a name this
                // loader does not read: a file that is merely locked fails the move too and keeps
                // its place, which is the outcome that loses nothing.
                try
                {
                    string aside = Filename + ".unreadable";

                    // The first rescue is kept, never replaced. It is the file the user's history
                    // was actually in; anything unreadable after it is what this app wrote in the
                    // meantime, and overwriting the one with the fifty runs to keep the one with a
                    // single run would be the loss this whole branch exists to prevent.
                    if (File.Exists(aside))
                        File.Delete(Filename);
                    else
                        File.Move(Filename, aside);
                }
                catch
                {
                    // A read-only folder is a reason to lose the history, not to fail the run.
                }
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
                // Every string the file ever holds passes here. A board name or a module part
                // number arrives from SMBIOS byte for byte, and one unprintable byte in it costs
                // the whole history rather than the one field. The fields a run fills in itself
                // are no exception once a file has been read back: the reader tolerates an illegal
                // character in any of them, so one can arrive from the file and be written again.
                foreach (var run in runs)
                {
                    run.System = XmlSafe(run.System);
                    run.Timestamp = XmlSafe(run.Timestamp);

                    if (run.Settings != null)
                        foreach (var setting in run.Settings)
                            if (setting != null)
                            {
                                setting.Key = XmlSafe(setting.Key);
                                setting.Value = XmlSafe(setting.Value);
                            }

                    if (run.Rungs != null)
                        foreach (var rung in run.Rungs)
                            if (rung != null)
                                rung.Level = XmlSafe(rung.Level);
                }

                File.WriteAllText(Filename, XmlUtils.SerializeToXml(runs));
            }
            catch
            {
                // A read-only install folder is a reason to lose the history, not to fail the run.
            }
        }

        // The serializer writes a C0 control character as a numeric character reference no reader
        // will take back, and throws outright on a lone surrogate. Both end in a file that cannot
        // be read again, so the character is dropped instead of written.
        private static string XmlSafe(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var kept = new char[text.Length];
            int count = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                // A surrogate is a character only as a pair; the halves travel together.
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    kept[count++] = c;
                    kept[count++] = text[i + 1];
                    i++;
                    continue;
                }

                if (c == 0x09 || c == 0x0A || c == 0x0D
                    || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD))
                    kept[count++] = c;
            }

            return new string(kept, 0, count);
        }

        /// <summary>Builds a run from a finished measurement and the settings it was taken under.</summary>
        public static BenchmarkRun Capture(
            MemoryLatencyResult latency,
            MemoryBandwidthResult bandwidth,
            Dictionary<string, string> settings,
            CacheLadderResult ladder = null)
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
                run.BandwidthLargePages = bandwidth.LargePages;
                run.AboveBus = bandwidth.AboveBus;
            }

            // The cache ladder is display-only and optional: a run that measured the memory but
            // was cancelled in the ladder's tail still carries its DRAM figures without it.
            if (ladder != null && ladder.Ok && ladder.Rungs != null && ladder.Rungs.Count > 0)
            {
                run.VectorBits = ladder.VectorBits;
                run.Rungs = new List<BenchmarkRung>();
                foreach (var rung in ladder.Rungs)
                    run.Rungs.Add(new BenchmarkRung
                    {
                        Level = rung.Level,
                        CacheBytes = rung.CacheBytes,
                        Nanoseconds = rung.Nanoseconds,
                        ReadGBs = rung.ReadGBs,
                        WriteGBs = rung.WriteGBs,
                        CopyGBs = rung.CopyGBs,
                        ReadAllGBs = rung.ReadAllGBs,
                        WriteAllGBs = rung.WriteAllGBs,
                        CopyAllGBs = rung.CopyAllGBs,
                        LargePages = rung.LargePages,
                        OwnLargePages = rung.OwnLargePages,
                        AllCoreWorkers = rung.AllCoreWorkers,
                        AllCoreOf = rung.AllCoreOf,
                        PackageCores = rung.PackageCores,
                    });
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
