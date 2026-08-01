using System.Collections.Generic;
using System.Globalization;

namespace ZenTimings
{
    /// <summary>One line of a reference profile.</summary>
    public class ReferenceTiming
    {
        public ReferenceTiming(string name, string value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; private set; }

        /// <summary>What the profile recorded.</summary>
        public string Value { get; private set; }

        /// <summary>What this machine reads right now; filled in when a profile is shown.</summary>
        public string Current { get; set; }

        /// <summary>
        /// True when both sides have a value and they differ - the whole point of the window.
        /// A missing current value is not a difference: the profile may list something this
        /// platform does not expose at all.
        /// </summary>
        public bool IsDifferent
        {
            get
            {
                return !string.IsNullOrEmpty(Current)
                    && Current != LiveSnapshot.Missing
                    && Current != Value;
            }
        }
    }

    /// <summary>A section header, so the list reads like the main window rather than one long column.</summary>
    public class ReferenceSection
    {
        public ReferenceSection(string title, params ReferenceTiming[] timings)
        {
            Title = title;
            Timings = new List<ReferenceTiming>(timings);
        }

        public string Title { get; private set; }
        public List<ReferenceTiming> Timings { get; private set; }
    }

    /// <summary>
    /// A recorded set of memory settings, kept for comparison. ZenTimings only reads the memory
    /// controller, so nothing here is ever applied - changing a timing is a BIOS job.
    /// </summary>
    public class ReferenceProfile
    {
        public ReferenceProfile(string name, string hardware, string notes, params ReferenceSection[] sections)
        {
            Name = name;
            Hardware = hardware;
            Notes = notes;
            Sections = new List<ReferenceSection>(sections);
        }

        public string Name { get; private set; }

        /// <summary>Board, BIOS and kit. A profile without them cannot be judged.</summary>
        public string Hardware { get; private set; }

        public string Notes { get; private set; }

        public List<ReferenceSection> Sections { get; private set; }

        public IEnumerable<ReferenceTiming> AllTimings
        {
            get
            {
                foreach (var section in Sections)
                    foreach (var timing in section.Timings)
                        yield return timing;
            }
        }
    }

    public static class ReferenceProfiles
    {
        private static ReferenceTiming T(string name, string value)
        {
            return new ReferenceTiming(name, value);
        }

        /// <summary>
        /// The built-in profiles plus every benchmark run saved on this machine, so a measurement
        /// taken last week can be compared with the settings running now.
        /// </summary>
        public static List<ReferenceProfile> AllIncludingSavedRuns()
        {
            var profiles = new List<ReferenceProfile>(All);

            foreach (var run in BenchmarkHistory.Load())
                profiles.Add(FromRun(run));

            return profiles;
        }

        private static ReferenceProfile FromRun(BenchmarkRun run)
        {
            var score = new List<ReferenceTiming>();

            if (run.LatencyNs > 0)
                score.Add(T("Latency", run.LatencyNs.ToString("F1", CultureInfo.InvariantCulture) + " ns"));
            if (run.ReadGBs > 0)
                score.Add(T("Read", run.ReadGBs.ToString("F1", CultureInfo.InvariantCulture) + " GB/s"));
            if (run.WriteGBs > 0)
                score.Add(T("Write", run.WriteGBs.ToString("F1", CultureInfo.InvariantCulture) + " GB/s"));
            if (run.CopyGBs > 0)
                score.Add(T("Copy", run.CopyGBs.ToString("F1", CultureInfo.InvariantCulture) + " GB/s"));

            var sections = new List<ReferenceSection>();
            if (score.Count > 0)
                sections.Add(new ReferenceSection("Benchmark", score.ToArray()));

            // Grouped the same way as the built-in profiles so both read alike.
            AddSection(sections, run, "Configuration",
                "Speed", "Capacity", "MCLK", "BGS", "BGS Alt", "FCLK", "GDM", "Cmd2T", "UCLK");
            AddSection(sections, run, "Primary timings",
                "tCL", "tRCDWR", "tRCDRD", "tRP", "tRAS", "tRC", "tRRDS", "tRRDL", "tFAW",
                "tWTRS", "tWTRL", "tWR", "tRFC (ns)", "tRFC", "tRFC2", "tRFCsb",
                "tMOD", "tMODPDA", "tPHYWRD", "tPHYWRL", "tRDPRE");
            AddSection(sections, run, "Secondary timings",
                "tRDRDSCL", "tWRWRSCL", "tCCD_L", "tCCD_L_WR", "tCCD_L_WR2", "tCWL", "tRTP",
                "tRDWR", "tWRRD", "tRDRDSC", "tRDRDSD", "tRDRDDD", "tWRWRSC", "tWRWRSD",
                "tWRWRDD", "tCKE", "tREFI", "Refresh", "tSTAG", "tMRD", "tMRDPDA", "tPHYRDL",
                "PowerDown", "tWRPRE");
            AddSection(sections, run, "Voltages",
                "VSOC (SMU)", "CLDO VDDP", "VDDG CCD", "VDDG IOD", "MEM VDD", "MEM VDDQ",
                "CPU VDDIO", "MEM VPP", "VDD MISC");
            AddSection(sections, run, "Impedances",
                "ProcOdt", "ProcOdt Pu", "ProcOdt Pd", "ProcCsDs", "ProcCkDs", "ProcCaDs",
                "ProcDqDs", "ProcDqDs Pu", "ProcDqDs Pd", "DramDqDs", "DramDqDs Pu", "DramDqDs Pd",
                "RttNomWr", "RttNomRd", "RttWr", "RttPark", "RttParkDqs", "Nitro",
                "CA ODT A", "CA ODT B", "CK ODT A", "CK ODT B", "CS ODT A", "CS ODT B");

            return new ReferenceProfile(
                run.Title,
                "Saved on this machine",
                string.Empty,
                sections.ToArray());
        }

        private static void AddSection(List<ReferenceSection> sections, BenchmarkRun run,
            string title, params string[] keys)
        {
            var rows = new List<ReferenceTiming>();

            foreach (var key in keys)
            {
                string value = run.Get(key);
                if (!string.IsNullOrEmpty(value))
                    rows.Add(T(key, value));
            }

            if (rows.Count > 0)
                sections.Add(new ReferenceSection(title, rows.ToArray()));
        }

        /// <summary>
        /// Transcribed from ZenTimings captures. Values are recorded exactly as the app displayed
        /// them, including the ones it greys out (an inactive tRFC2/tRFCsb is still what the
        /// controller holds), so a comparison is like-for-like.
        ///
        /// The tCCD_L family is the exception: the captures predate this fork reading it, so those
        /// three carry the AGESA default rather than a transcription.
        /// </summary>
        public static readonly List<ReferenceProfile> All = new List<ReferenceProfile>
        {
            new ReferenceProfile(
                "9700X - 6400 CL26",
                "Ryzen 7 9700X | PRIME B850M-K, BIOS 1686 | AGESA 1.3.0.1b Patch A | G.Skill F5-6400J3039G16G, 32 GB",
                "UCLK 1:1 with MCLK, FCLK 2233. MEM VDD 1.755 V.",
                new ReferenceSection("Configuration",
                    T("Speed", "6400 MT/s"), T("Capacity", "32GB"), T("MCLK", "3200.00"),
                    T("BGS", "Enabled"), T("BGS Alt", "Disabled"), T("FCLK", "2233.00"),
                    T("GDM", "Disabled"), T("Cmd2T", "1T"), T("UCLK", "3200.00")),
                new ReferenceSection("Primary timings",
                    T("tCL", "26"), T("tRCDWR", "8"), T("tRCDRD", "36"), T("tRP", "36"),
                    T("tRAS", "36"), T("tRC", "72"), T("tRRDS", "6"), T("tRRDL", "6"),
                    T("tFAW", "24"), T("tWTRS", "4"), T("tWTRL", "16"), T("tWR", "48"),
                    T("tRFC (ns)", "120"), T("tRFC", "384"), T("tRFC2", "384"), T("tRFCsb", "256"),
                    T("tMOD", "45"), T("tMODPDA", "32"), T("tPHYWRD", "6"), T("tPHYWRL", "11"),
                    T("tRDPRE", "3")),
                new ReferenceSection("Secondary timings",
                    T("tRDRDSCL", "6"), T("tWRWRSCL", "1"),
                    T("tCCD_L", "12"), T("tCCD_L_WR", "48"), T("tCCD_L_WR2", "24"),
                    T("tCWL", "24"), T("tRTP", "10"),
                    T("tRDWR", "14"), T("tWRRD", "2"), T("tRDRDSC", "1"), T("tRDRDSD", "1"),
                    T("tRDRDDD", "1"), T("tWRWRSC", "1"), T("tWRWRSD", "1"), T("tWRWRDD", "1"),
                    T("tCKE", "0"), T("tREFI", "65535"), T("Refresh", "Mixed"), T("tSTAG", "7"),
                    T("tMRD", "45"), T("tMRDPDA", "32"), T("tPHYRDL", "33"),
                    T("PowerDown", "Disabled"), T("tWRPRE", "3")),
                new ReferenceSection("Voltages",
                    T("VSOC (SMU)", "1.2500V"), T("CLDO VDDP", "1.1484V"),
                    T("VDDG CCD", "1.0824V"), T("VDDG IOD", "1.0824V"),
                    T("MEM VDD", "1.7550V"), T("MEM VDDQ", "1.5600V"),
                    T("CPU VDDIO", "1.4000V"), T("MEM VPP", "1.8000V"), T("VDD MISC", "1.1500V")),
                new ReferenceSection("Impedances",
                    T("ProcOdt Pu", "40.0 Ω"), T("ProcOdt Pd", "40.0 Ω"),
                    T("ProcCsDs", "30.0 Ω"), T("ProcCkDs", "30.0 Ω"), T("ProcCaDs", "30.0 Ω"),
                    T("ProcDqDs Pu", "34.3 Ω"), T("ProcDqDs Pd", "34.3 Ω"),
                    T("DramDqDs Pu", "34.0 Ω"), T("DramDqDs Pd", "34.0 Ω"),
                    T("RttNomWr", "Off"), T("RttNomRd", "Off"), T("RttWr", "RZQ/5 (48)"),
                    T("RttPark", "RZQ/5 (48)"), T("RttParkDqs", "RZQ/6 (40)"), T("Nitro", "1/2/1"),
                    T("CA ODT A", "480.0 Ω"), T("CA ODT B", "40.0 Ω"),
                    T("CK ODT A", "Off"), T("CK ODT B", "40.0 Ω"),
                    T("CS ODT A", "Off"), T("CS ODT B", "60.0 Ω"))),

            new ReferenceProfile(
                "9700X - 6000 CL24",
                "Ryzen 7 9700X | PRIME B850M-K, BIOS 1686 | AGESA 1.3.0.1b Patch A | G.Skill F5-6400J3039G16G, 32 GB",
                "Same board as the 6400 profile, dropped to 6000 with tighter primaries.",
                new ReferenceSection("Configuration",
                    T("Speed", "6000 MT/s"), T("Capacity", "32GB"), T("MCLK", "3000.00"),
                    T("BGS", "Enabled"), T("BGS Alt", "Disabled"), T("FCLK", "2233.00"),
                    T("GDM", "Disabled"), T("Cmd2T", "1T"), T("UCLK", "3000.00")),
                new ReferenceSection("Primary timings",
                    T("tCL", "24"), T("tRCDWR", "12"), T("tRCDRD", "34"), T("tRP", "32"),
                    T("tRAS", "34"), T("tRC", "66"), T("tRRDS", "6"), T("tRRDL", "6"),
                    T("tFAW", "24"), T("tWTRS", "4"), T("tWTRL", "16"), T("tWR", "48"),
                    T("tRFC (ns)", "120"), T("tRFC", "360"), T("tRFC2", "360"), T("tRFCsb", "240"),
                    T("tMOD", "42"), T("tMODPDA", "32"), T("tPHYWRD", "6"), T("tPHYWRL", "9"),
                    T("tRDPRE", "3")),
                new ReferenceSection("Secondary timings",
                    T("tRDRDSCL", "5"), T("tWRWRSCL", "1"),
                    T("tCCD_L", "12"), T("tCCD_L_WR", "48"), T("tCCD_L_WR2", "24"),
                    T("tCWL", "22"), T("tRTP", "10"),
                    T("tRDWR", "14"), T("tWRRD", "2"), T("tRDRDSC", "1"), T("tRDRDSD", "1"),
                    T("tRDRDDD", "1"), T("tWRWRSC", "1"), T("tWRWRSD", "1"), T("tWRWRDD", "1"),
                    T("tCKE", "0"), T("tREFI", "65535"), T("Refresh", "Mixed"), T("tSTAG", "7"),
                    T("tMRD", "42"), T("tMRDPDA", "32"), T("tPHYRDL", "33"),
                    T("PowerDown", "Disabled"), T("tWRPRE", "3")),
                new ReferenceSection("Voltages",
                    T("VSOC (SMU)", "1.2000V"), T("CLDO VDDP", "1.0981V"),
                    T("VDDG CCD", "1.0270V"), T("VDDG IOD", "1.0270V"),
                    T("MEM VDD", "1.7550V"), T("MEM VDDQ", "1.5600V"),
                    T("CPU VDDIO", "1.4000V"), T("MEM VPP", "1.8000V"), T("VDD MISC", "1.1023V")),
                new ReferenceSection("Impedances",
                    T("ProcOdt Pu", "40.0 Ω"), T("ProcOdt Pd", "40.0 Ω"),
                    T("ProcCsDs", "30.0 Ω"), T("ProcCkDs", "30.0 Ω"), T("ProcCaDs", "30.0 Ω"),
                    T("ProcDqDs Pu", "34.3 Ω"), T("ProcDqDs Pd", "34.3 Ω"),
                    T("DramDqDs Pu", "40.0 Ω"), T("DramDqDs Pd", "40.0 Ω"),
                    T("RttNomWr", "Off"), T("RttNomRd", "Off"), T("RttWr", "RZQ/5 (48)"),
                    T("RttPark", "RZQ/5 (48)"), T("RttParkDqs", "RZQ/6 (40)"), T("Nitro", "1/2/0"),
                    T("CA ODT A", "480.0 Ω"), T("CA ODT B", "40.0 Ω"),
                    T("CK ODT A", "Off"), T("CK ODT B", "40.0 Ω"),
                    T("CS ODT A", "Off"), T("CS ODT B", "60.0 Ω"))),

            new ReferenceProfile(
                "9800X3D - 6600 CL24",
                "Ryzen 7 9800X3D | ROG STRIX X670E-E GAMING WIFI, BIOS 3603 | AGESA 1.3.0.0 | G.Skill F5-6400J3039G16G, 32 GB",
                "Captured on ZenTimings 1.37 (debug), which has no CA/CK/CS ODT rows and reports "
                + "ProcOdt as a single pair. MEM VDD 1.95 V - high, watch DIMM temperature.",
                new ReferenceSection("Configuration",
                    T("Speed", "6600 MT/s"), T("Capacity", "32GB"), T("MCLK", "3300.00"),
                    T("BGS", "Enabled"), T("BGS Alt", "Disabled"), T("FCLK", "2200.00"),
                    T("GDM", "Disabled"), T("Cmd2T", "1T"), T("UCLK", "3300.00")),
                new ReferenceSection("Primary timings",
                    T("tCL", "24"), T("tRCDWR", "8"), T("tRCDRD", "36"), T("tRP", "32"),
                    T("tRAS", "36"), T("tRC", "36"), T("tRRDS", "6"), T("tRRDL", "6"),
                    T("tFAW", "24"), T("tWTRS", "2"), T("tWTRL", "14"), T("tWR", "48"),
                    T("tRFC (ns)", "120"), T("tRFC", "396"), T("tRFC2", "396"), T("tRFCsb", "264"),
                    T("tMOD", "47"), T("tMODPDA", "32"), T("tPHYWRD", "6"), T("tPHYWRL", "9"),
                    T("tRDPRE", "3")),
                new ReferenceSection("Secondary timings",
                    T("tRDRDSCL", "6"), T("tWRWRSCL", "1"),
                    T("tCCD_L", "12"), T("tCCD_L_WR", "48"), T("tCCD_L_WR2", "24"),
                    T("tCWL", "22"), T("tRTP", "8"),
                    T("tRDWR", "14"), T("tWRRD", "1"), T("tRDRDSC", "1"), T("tRDRDSD", "1"),
                    T("tRDRDDD", "1"), T("tWRWRSC", "1"), T("tWRWRSD", "1"), T("tWRWRDD", "1"),
                    T("tCKE", "0"), T("tREFI", "65535"), T("Refresh", "Normal"), T("tSTAG", "7"),
                    T("tMRD", "47"), T("tMRDPDA", "32"), T("tPHYRDL", "33"),
                    T("PowerDown", "Disabled"), T("tWRPRE", "3")),
                new ReferenceSection("Voltages",
                    T("VSOC (SMU)", "1.3000V"), T("CLDO VDDP", "1.1484V"),
                    T("VDDG CCD", "1.0492V"), T("VDDG IOD", "1.0492V"),
                    T("MEM VDD", "1.9500V"), T("MEM VDDQ", "1.5600V"),
                    T("CPU VDDIO", "1.4000V"), T("MEM VPP", "1.8000V"), T("VDD MISC", "1.1000V")),
                new ReferenceSection("Impedances",
                    T("ProcOdt Pu", "25.3 Ω"), T("ProcOdt Pd", "Hi-Z"),
                    T("ProcCaDs", "30.0 Ω"), T("ProcDqDs", "34.3 Ω"), T("DramDqDs", "34.0 Ω"),
                    T("RttNomWr", "Off"), T("RttNomRd", "Off"), T("RttWr", "RZQ/6 (40)"),
                    T("RttPark", "RZQ/4 (60)"), T("RttParkDqs", "RZQ/4 (60)"), T("Nitro", "1/2/1"))),

            new ReferenceProfile(
                "9800X3D - 6000 CL24",
                "Ryzen 7 9800X3D | ROG STRIX X670E-E GAMING WIFI, BIOS 3702 | AGESA 1.3.0.1 | G.Skill F5-6400J3039G16G, 16 GB",
                "Single module. Lowest voltages of the four - MEM VDD 1.605 V, VSOC 1.2 V.",
                new ReferenceSection("Configuration",
                    T("Speed", "6000 MT/s"), T("Capacity", "16GB"), T("MCLK", "3000.00"),
                    T("BGS", "Enabled"), T("BGS Alt", "Disabled"), T("FCLK", "2200.00"),
                    T("GDM", "Disabled"), T("Cmd2T", "1T"), T("UCLK", "3000.00")),
                new ReferenceSection("Primary timings",
                    T("tCL", "24"), T("tRCDWR", "8"), T("tRCDRD", "34"), T("tRP", "28"),
                    T("tRAS", "34"), T("tRC", "34"), T("tRRDS", "4"), T("tRRDL", "6"),
                    T("tFAW", "20"), T("tWTRS", "2"), T("tWTRL", "14"), T("tWR", "48"),
                    T("tRFC (ns)", "120"), T("tRFC", "360"), T("tRFC2", "1"), T("tRFCsb", "1"),
                    T("tMOD", "42"), T("tMODPDA", "32"), T("tPHYWRD", "6"), T("tPHYWRL", "9"),
                    T("tRDPRE", "3")),
                new ReferenceSection("Secondary timings",
                    T("tRDRDSCL", "5"), T("tWRWRSCL", "1"),
                    T("tCCD_L", "12"), T("tCCD_L_WR", "48"), T("tCCD_L_WR2", "24"),
                    T("tCWL", "22"), T("tRTP", "12"),
                    T("tRDWR", "12"), T("tWRRD", "1"), T("tRDRDSC", "1"), T("tRDRDSD", "1"),
                    T("tRDRDDD", "1"), T("tWRWRSC", "1"), T("tWRWRSD", "1"), T("tWRWRDD", "1"),
                    T("tCKE", "0"), T("tREFI", "65535"), T("Refresh", "Normal"), T("tSTAG", "7"),
                    T("tMRD", "42"), T("tMRDPDA", "32"), T("tPHYRDL", "33"),
                    T("PowerDown", "Disabled"), T("tWRPRE", "2")),
                new ReferenceSection("Voltages",
                    T("VSOC (SMU)", "1.2000V"), T("CLDO VDDP", "0.8998V"),
                    T("VDDG CCD", "1.0492V"), T("VDDG IOD", "1.0492V"),
                    T("MEM VDD", "1.6050V"), T("MEM VDDQ", "1.4100V"),
                    T("CPU VDDIO", "1.2000V"), T("MEM VPP", "1.8000V"), T("VDD MISC", "1.1000V")),
                new ReferenceSection("Impedances",
                    T("ProcOdt Pu", "25.3 Ω"), T("ProcOdt Pd", "Hi-Z"),
                    T("ProcCaDs", "30.0 Ω"), T("ProcDqDs", "34.3 Ω"), T("DramDqDs", "34.0 Ω"),
                    T("RttNomWr", "Off"), T("RttNomRd", "Off"), T("RttWr", "RZQ/6 (40)"),
                    T("RttPark", "RZQ/4 (60)"), T("RttParkDqs", "RZQ/4 (60)"), T("Nitro", "1/2/0"))),
        };
    }
}
