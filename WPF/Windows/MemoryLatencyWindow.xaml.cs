using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ZenStates.Core;
using ZenTimings.Localization;
using ZenTimings.ViewModels;

namespace ZenTimings.Windows
{
    /// <summary>
    /// Runs <see cref="MemoryLatencyTest"/> and shows the result next to the settings it was
    /// measured under, so a number written down now still means something later.
    /// </summary>
    public partial class MemoryLatencyWindow : ThemedAdonisWindow
    {
        private readonly MainViewModel _viewModel;

        // The results on the grid right now, kept so pinning a baseline can rebuild the same
        // grid against the new reference without a fresh run.
        private MemoryLatencyResult shownLatency;
        private MemoryBandwidthResult shownBandwidth;
        private CacheLadderResult shownLadder;

        /// <summary>
        /// Fills the cache and memory grid and brings the Result tab forward. One window, one
        /// close: the scores share the launcher rather than stacking a second window over it.
        /// </summary>
        private void ShowResultTab(MemoryLatencyResult latency, MemoryBandwidthResult bandwidth,
            CacheLadderResult ladder)
        {
            // A cancel that arrived after the gate was read still has the window's close queued;
            // painting a closing window is pointless and touching it can throw.
            if (!IsLoaded)
                return;

            shownLatency = latency;
            shownBandwidth = bandwidth;
            shownLadder = ladder;
            CacheTable.Build(TablePanel, ladder, latency, bandwidth, ComparableBaseline(latency));

            // Display scaling can change under a window that never moved, so the cap is re-read
            // here as well: growth is permitted on the next two lines and the screen it grows
            // into has to be the one the window is on.
            CapToWorkArea();

            // One manual resize turns SizeToContent off for good, and every run after it would
            // land in whatever stub height the window was left at. Fresh scores win it back.
            if (WindowState == WindowState.Normal && SizeToContent != SizeToContent.Height)
                SizeToContent = SizeToContent.Height;

            ResultTabs.SelectedIndex = 1;
        }

        /// <summary>
        /// The pinned run the grid's deltas read against - null when nothing is pinned, when the
        /// newest run is itself the baseline, or when the two were measured under different
        /// conditions and a difference would not mean anything.
        /// </summary>
        private static BenchmarkRun ComparableBaseline(MemoryLatencyResult shown)
        {
            return ComparableReference(shown, null);
        }

        /// <summary>
        /// The run the deltas read against: the candidate when one is handed in - the history row
        /// the user clicked - and the pinned run otherwise. The gates are the same either way: a
        /// difference only means something between runs the same measurement produced under the
        /// same conditions, and never against the run on screen itself.
        /// </summary>
        /// <remarks>
        /// The newest file entry has to BE the run on screen before it stands in for it: a save
        /// that failed (read-only install folder) leaves the file a run behind, and a gate read
        /// off the wrong run would wave through exactly the mismatches it exists to stop.
        /// </remarks>
        private static BenchmarkRun ComparableReference(MemoryLatencyResult shown, BenchmarkRun candidate)
        {
            if (shown == null || !shown.Ok)
                return null;

            try
            {
                var runs = BenchmarkHistory.Load();
                var newest = runs.FirstOrDefault();
                var reference = candidate ?? runs.FirstOrDefault(r => r.IsBaseline);

                // By key, not by reference: a clicked row's run object came from an earlier load.
                if (newest == null || reference == null || RunKey(reference) == RunKey(newest))
                    return null;

                if (newest.BufferMegabytes != shown.BufferMegabytes
                    || newest.LargePages != shown.LargePages
                    || Math.Abs(newest.LatencyNs - shown.Nanoseconds) > 0.05)
                    return null;

                return reference.Schema == newest.Schema
                    && reference.BufferMegabytes == newest.BufferMegabytes
                    && reference.LargePages == newest.LargePages
                    ? reference
                    : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The machine the scores belong to - CPU, board, BIOS and memory - so the Result tab
        /// carries its own context the way a screenshot has to. Rebuilt after every run as well as
        /// at open, because the block carries the timings and those can move between the two;
        /// every value is defended because the hardware calls can throw on platforms that expose
        /// less than a desktop Ryzen does.
        /// </summary>
        private void BuildSystemInfo()
        {
            SystemInfoPanel.Children.Clear();

            try
            {
                if (_viewModel != null && !string.IsNullOrWhiteSpace(_viewModel.CpuName))
                    AddRow(SystemInfoPanel, Loc.T("Bench.InfoCpu"), _viewModel.CpuNameShortWithCores);
            }
            catch { }

            var si = SafeSystemInfo();
            if (si != null)
            {
                if (!string.IsNullOrWhiteSpace(si.MbName))
                    AddRow(SystemInfoPanel, Loc.T("Bench.InfoBoard"), si.MbName.Trim());

                string bios = si.BiosVersion;
                if (!string.IsNullOrWhiteSpace(si.AgesaVersion))
                    bios = string.IsNullOrWhiteSpace(bios)
                        ? "AGESA " + si.AgesaVersion
                        : bios.Trim() + "  /  AGESA " + si.AgesaVersion;
                if (!string.IsNullOrWhiteSpace(bios))
                    AddRow(SystemInfoPanel, Loc.T("Bench.InfoBios"), bios.Trim());
            }

            string memory = MemorySummary();
            if (!string.IsNullOrEmpty(memory))
                AddRow(SystemInfoPanel, Loc.T("Bench.InfoMemory"), memory);
        }

        private static SystemInfo SafeSystemInfo()
        {
            try { return CpuSingleton.Instance.systemInfo; }
            catch { return null; }
        }

        /// <summary>
        /// Size, type and speed of the installed memory in one line - "32 GB DDR5-6000 (2 DIMMs)" -
        /// from the modules the UMC probe placed and the live MT/s. Empty when the config is
        /// unreadable rather than a half-filled line.
        /// </summary>
        private string MemorySummary()
        {
            try
            {
                var config = CpuSingleton.Instance.GetMemoryConfig();
                if (config == null || config.Modules == null || config.Modules.Count == 0)
                    return "";

                long bytes = 0;
                int placed = 0;
                foreach (var module in config.Modules)
                {
                    // One holed entry must not erase the whole line - skip it, keep the rest.
                    if (module != null && module.Capacity != null && module.Capacity.SizeInBytes > 0)
                    {
                        bytes += (long)module.Capacity.SizeInBytes;
                        placed++;
                    }
                }

                var parts = new List<string>();
                if (bytes > 0)
                    parts.Add((bytes / (1024L * 1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " GB");

                float mts = _viewModel != null ? _viewModel.MemoryFrequency : 0;
                string type = config.Type.ToString();
                parts.Add(mts > 0
                    ? string.Format(CultureInfo.InvariantCulture, "{0}-{1:F0}", type, mts)
                    : type);

                if (placed > 0)
                    parts.Add(string.Format(CultureInfo.InvariantCulture,
                        "({0} {1})", placed, placed == 1 ? "DIMM" : "DIMMs"));

                // The primaries and the fabric clock make the line a full identity - a screenshot
                // of the Result tab then names the configuration it scored under.
                // All four or none: a partially-read register set would print "CL30-0-0-0".
                var timings = _viewModel != null ? _viewModel.Timings : null;
                if (timings != null && timings.CL > 0 && timings.RCDRD > 0
                    && timings.RP > 0 && timings.RAS > 0)
                    parts.Add(string.Format(CultureInfo.InvariantCulture,
                        "CL{0}-{1}-{2}-{3}", timings.CL, timings.RCDRD, timings.RP, timings.RAS));

                var power = _viewModel != null ? _viewModel.PowerTable : null;
                if (power != null && power.FCLK > 0)
                    parts.Add(string.Format(CultureInfo.InvariantCulture,
                        "FCLK {0:F0}", power.FCLK));

                return string.Join("  ", parts);
            }
            catch
            {
                return "";
            }
        }

        private sealed class HistoryRow
        {
            public BenchmarkRun Run;
            public int Index;
        }

        /// <summary>Identity of a run across a reload: the file holds no id of its own.</summary>
        private static string RunKey(BenchmarkRun run)
        {
            return run == null
                ? null
                : run.Timestamp + "|" + run.LatencyNs.ToString("R", CultureInfo.InvariantCulture);
        }

        public MemoryLatencyWindow(MainViewModel viewModel)
        {
            InitializeComponent();

            // SizeToContent can ask for more height than the screen has once the history fills
            // up; the ScrollViewer takes over from here. The primary monitor is all that is
            // known this early - OnSourceInitialized refines the cap to the actual one.
            MaxHeight = SystemParameters.WorkArea.Height;

            progressTimer.Tick += ProgressTimer_Tick;

            _viewModel = viewModel;
            TagBufferSizes();
            ShowContext();
            ShowCeiling();
            BuildSystemInfo();
            RefreshHistory();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Now the window knows which monitor it landed on; a taller second screen should
            // get to use its height.
            CapToWorkArea();
        }

        /// <summary>
        /// The cap belongs to the monitor the window is on now. SizeToContent keeps re-growing the
        /// window for the whole session, so one left behind on the screen it opened on lets it
        /// grow past a shorter one it was moved to - and the window, still believing it fits,
        /// never hands the overflow to the ScrollViewer.
        /// </summary>
        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            CapToWorkArea();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);

            // Maximising raises no location change - Left and Top keep the restore bounds - so a
            // cap put on while the window was normal would otherwise stay on for the whole of it.
            CapToWorkArea();

            // A window that grew while it was down the taskbar took no lift on the way: the growth
            // arrives minimised, where the top edge is the restore position rather than an edge on
            // screen. Queued behind the restore's layout, which is what makes the height real.
            if (WindowState == WindowState.Normal)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (WindowState == WindowState.Normal)
                        LiftIntoWorkArea(ActualHeight);
                }), DispatcherPriority.Loaded);
        }

        /// <summary>
        /// A window that grows keeps its top edge - WPF sizes to content without moving - so every
        /// DIP the Result tab adds lands below the bottom edge, off the screen for a window centred
        /// while the Test tab was short. The cap alone never engages the ScrollViewer there, and
        /// the buttons under the history cannot be reached. Growth the window did to itself only:
        /// a bottom edge the user dragged is the user's own placement.
        /// </summary>
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);

            // Maximised and minimised are the shell's placement, where Top is the restore position
            // rather than an edge on screen.
            if (WindowState != WindowState.Normal || SizeToContent != SizeToContent.Height
                || !sizeInfo.HeightChanged || sizeInfo.NewSize.Height <= sizeInfo.PreviousSize.Height)
                return;

            LiftIntoWorkArea(sizeInfo.NewSize.Height);
        }

        /// <summary>
        /// Up until the bottom edge is inside the work area, or to its top edge where the window
        /// is taller than that - which is where the cap and the ScrollViewer take over. Never
        /// downward: a window the user placed low is placed, not overflowing.
        /// </summary>
        private void LiftIntoWorkArea(double height)
        {
            double workTop, workBottom;
            if (!WindowUtils.TryWorkAreaDip(this, out workTop, out workBottom))
                return;

            // Top and the height are both the outer frame's, so the two add up to the bottom edge.
            double lifted = Math.Max(workTop, workBottom - height);
            if (Top > lifted)
                Top = lifted;
        }

        /// <summary>
        /// The height the cap took off the window, and the work area it was taken under. WPF
        /// answers a cap that lands below the window by shrinking it and writing the clamped
        /// height into Height; raising the cap again only grows the window back while SizeToContent
        /// is still Height, and one manual resize - or one maximise and restore - ends that for the
        /// session. So the height waits here for a screen with the room for it. Zero when nothing
        /// is owed, a window resized away from the cap included: that height is the user's own.
        /// </summary>
        private double heightBeforeCap;
        private double cappedTo;

        /// <summary>
        /// Zero while the window has no handle, and on a monitor that cannot be read - the work
        /// area the constructor set is the better cap of the two.
        /// </summary>
        /// <remarks>
        /// The normal state's cap alone. MaxHeight bounds the arrange, and a maximised window's
        /// client deliberately covers the resize frame on every side - about 11 px of it at 150% -
        /// so a work-area cap lays the content out short of the window's own client and leaves a
        /// bare strip under the grid. Nothing has to hold a maximised window in: the shell sized
        /// it, and SizeToContent is already off by the time it gets there.
        /// </remarks>
        private void CapToWorkArea()
        {
            // Maximised only. Minimised keeps SizeToContent on - the shell clears it for a maximise
            // and not for a minimise - so a run that finishes while the window is down the taskbar
            // would grow it with nothing bounding it, and the restore would put a window taller
            // than the screen back at the top it was centred to.
            if (WindowState == WindowState.Maximized)
            {
                MaxHeight = double.PositiveInfinity;
                return;
            }

            if (WindowState != WindowState.Normal)
                return;

            double workArea = WindowUtils.WorkAreaHeightDip(this);

            // Every step of a drag arrives here, and only a work area that moved can change
            // anything: ActualHeight is still the height the previous call replaced until the
            // layout it queued has run, and a second reading would take that for a resize.
            if (workArea <= 0 || workArea == MaxHeight)
                return;

            // The restore's state change arrives before the restored layout, so ActualHeight is
            // still the maximised frame's - taller than the work area by the frame it hangs over,
            // on every machine. RestoreBounds is the height this window will actually have.
            double height = RestoreBounds.IsEmpty ? ActualHeight : RestoreBounds.Height;

            if (heightBeforeCap > 0 && height + 0.5 < cappedTo)
                heightBeforeCap = 0;

            if (heightBeforeCap <= 0 && height > workArea)
                heightBeforeCap = height;

            MaxHeight = workArea;
            cappedTo = workArea;

            if (heightBeforeCap <= 0)
                return;

            // As much of it as this screen has room for; the rest stays owed, so a desktop of
            // three heights gives the height back one screen at a time. The regrow is the window's
            // own, so the lift that follows growth has to be asked for here.
            double regained = Math.Min(heightBeforeCap, workArea);
            if (regained > height)
            {
                Height = regained;
                LiftIntoWorkArea(regained);
            }

            if (regained >= heightBeforeCap)
                heightBeforeCap = 0;
        }

        /// <summary>
        /// The comparable size, and the default. The read kernel cycles both buffers, so the
        /// working set is twice this - past 20x the L3 on any desktop part, which is what keeps
        /// the L3's share of the read stream down where it stops distorting the score. It stays
        /// the default rather than 2048 because 4 GB of large pages falls back far more often on
        /// a machine that has been up a while, and a fallback nobody can predict does more damage
        /// to a comparison than a bias everybody carries. The larger size is offered for the parts
        /// where 1024 MB is only ten times the L3, and tagged so the trade is visible.
        /// </summary>
        private const int BufferMegabytes = 1024;

        /// <summary>
        /// Marks both sizes that are not the comparable one, and they are not marked for the same
        /// reason. At 256 MB the read stream is 512 MB against the L3, so the cache supplies a
        /// share of it that is counted as bandwidth - a few percent on a 32 MB part, near a
        /// quarter on a 96 MB one, and enough on either to put read or copy above the ceilings
        /// shown directly above. At 2048 MB nothing is cache-bound; what it costs is memory.
        /// </summary>
        private void TagBufferSizes()
        {
            foreach (ComboBoxItem item in BufferSize.Items)
            {
                int size = Tagged(item);
                if (size < BufferMegabytes)
                    item.Content = item.Content + " (" + Loc.T("Bench.CacheBound") + ")";
                else if (size > BufferMegabytes)
                    item.Content = item.Content + " (" + Loc.T("Bench.RamHungry") + ")";
            }
        }

        private static int Tagged(ComboBoxItem item)
        {
            int value;
            return item != null && item.Tag != null &&
                int.TryParse(item.Tag.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : BufferMegabytes;
        }

        private int SelectedMegabytes()
        {
            return Tagged(BufferSize.SelectedItem as ComboBoxItem);
        }

        private void BufferSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Built by XAML before the first selection is raised.
            if (SmallBufferNote == null)
                return;

            int size = SelectedMegabytes();
            SmallBufferNote.Visibility = size == BufferMegabytes
                ? Visibility.Collapsed
                : Visibility.Visible;

            // One line, two warnings: the small size overstates the scores, the large one may not
            // start at all. Nothing to say at the comparable size, where the line is collapsed.
            if (size != BufferMegabytes)
                SmallBufferNote.Text = Loc.T(size < BufferMegabytes
                    ? "Bench.SmallBuffer"
                    : "Bench.LargeBuffer");
        }

        /// <summary>
        /// What the bus can carry, in the same units the scores come back in. The sweep uses it to
        /// throw away a worker set that came out above it rather than keeping it as a best. Zero
        /// when the channel count cannot be trusted - a zero ceiling filters nothing, where a
        /// guessed one filters everything.
        /// </summary>
        private double DramCeilingGBs()
        {
            float mts = _viewModel != null ? _viewModel.MemoryFrequency : 0;
            if (mts <= 0)
                return 0;

            int channels;
            try
            {
                var config = CpuSingleton.Instance.GetMemoryConfig();

                // The offsets come from the UMC probe. A module it never placed keeps DctOffset 0,
                // which is also UMC0's real offset, so an unplaced module counts as channel zero
                // and two channels collapse into one - halving the ceiling and, through the score
                // filter, zeroing read, write and copy. Slot is written with the offset, so an
                // empty one is the unambiguous "not placed". This covers the all-disabled case too.
                if (config.Modules.Exists(m => string.IsNullOrEmpty(m.Slot)))
                    return 0;

                channels = config.Modules.Select(m => m.DctOffset).Distinct().Count();
            }
            catch
            {
                return 0;
            }

            return channels < 1 ? 0 : mts * 8.0 * channels / 1000.0;
        }

        private static SMU.SmuType SmuType()
        {
            try { return CpuSingleton.Instance.smu.SMU_TYPE; }
            catch { return SMU.SmuType.TYPE_UNSUPPORTED; }
        }

        /// <summary>
        /// Strix Halo is the one part in the APU range that is not a single die: two CCDs on an
        /// I/O die, wired like a desktop part. It inherits Phoenix's settings class and so reports
        /// the same SMU type, which the range test alone cannot tell apart.
        /// </summary>
        private static bool IsChipletApu()
        {
            try { return CpuSingleton.Instance.info.codeName == Cpu.CodeName.StrixHalo; }
            catch { return false; }
        }

        /// <summary>
        /// CCD count for the fabric rows. The fuse layout predates the 17h APUs and reads 2 on
        /// some of them; a monolithic die has one link to the memory controller whatever it says.
        /// The range test is the one ZenStates uses - the APU types are a contiguous block.
        /// </summary>
        private static int CcdCount()
        {
            var smuType = SmuType();
            if (smuType >= SMU.SmuType.TYPE_APU0 && smuType <= SMU.SmuType.TYPE_APU2
                && !IsChipletApu())
                return 1;

            try
            {
                var system = CpuSingleton.Instance.systemInfo;
                int ccds = Math.Max(1, system.CCDCount);

                // The fuse field ZenStates slices is two bits wide, so the count stops at two.
                // More cores than that many dies could hold means the number is wrong, not the
                // part - and a wrong ceiling is worse than none. FusedCoreCount is CPUID's, not
                // the fuse's, so it stays right where CCDCount does not.
                return system.FusedCoreCount > ccds * 8 ? 0 : ccds;
            }
            catch { return 1; }
        }

        /// <summary>
        /// Bytes the cores can write per fabric clock.
        /// </summary>
        /// <remarks>
        /// Half the read width on chiplet parts: the off-die link is deliberately asymmetric
        /// because client workloads write little, and paying for a full-width SerDes would buy
        /// nothing. A monolithic die has no off-die hop to economise on and carries the full width
        /// both ways - which is the APU2 group (Rembrandt, Mendocino, Phoenix, Hawk Point, Strix
        /// Point, Krackan) minus Strix Halo, which is chiplet and keeps the narrow write.
        ///
        /// Picasso is left on the narrow figure rather than assumed to match: its own scores say
        /// so, with write landing at 46% of read, which is what half a path looks like. Renoir
        /// and Cezanne are grouped with it for want of a measurement either way.
        /// </remarks>
        private static double FabricWriteBytes()
        {
            return SmuType() == SMU.SmuType.TYPE_APU2 && !IsChipletApu() ? 32.0 : 16.0;
        }

        /// <summary>
        /// The link's write ceiling, for marking a write score that sits on the fabric rather
        /// than on the DRAM. Zero when FCLK is unknown.
        /// </summary>
        private double FabricWriteGBs()
        {
            var power = _viewModel != null ? _viewModel.PowerTable : null;
            float fclk = power != null ? power.FCLK : 0;
            if (fclk <= 0)
                return 0;

            return fclk * FabricWriteBytes() * CcdCount() / 1000.0;
        }

        /// <summary>
        /// What the hardware could deliver at best, so a measured figure can be read as a fraction
        /// of it rather than as a bare number.
        /// </summary>
        /// <remarks>
        /// Two separate ceilings, and the lower one is what actually binds:
        ///
        /// The DRAM ceiling is the bus itself - a DDR5 UDIMM is 64 bits wide however it is split
        /// into subchannels, so it moves 8 bytes per transfer, times the populated channels.
        ///
        /// The fabric ceiling is per CCD: each die has its own link to the memory controller,
        /// 32 bytes per fabric clock read and 16 written. One CCD at FCLK 2000 reads 64 GB/s -
        /// fabric-bound; two CCDs read 128 and the DRAM side binds instead. The asymmetry is why
        /// a write score sits so far below a read score, and why raising FCLK helps writes twice
        /// as much as reads.
        /// </remarks>
        private void ShowCeiling()
        {
            CeilingPanel.Children.Clear();

            var power = _viewModel != null ? _viewModel.PowerTable : null;
            float fclk = power != null ? power.FCLK : 0;

            int ccds = CcdCount();
            int channels = 1;
            try
            {
                channels = Math.Max(1, CpuSingleton.Instance.GetMemoryConfig()
                    .Modules.Select(m => m.DctOffset).Distinct().Count());
            }
            catch { }

            // Gated on the ceiling rather than on the clock: when the channel count cannot be
            // trusted the row would be a wrong number, and no row is the better of the two.
            double dram = DramCeilingGBs();
            if (dram > 0)
            {
                AddRow(CeilingPanel, Loc.T("Bench.DramBus"), string.Format(CultureInfo.InvariantCulture,
                    "{0:F1} GB/s ({1} ch)", dram, channels));
            }

            if (fclk > 0 && ccds > 0)
            {
                AddRow(CeilingPanel, Loc.T("Bench.FabricRead"), string.Format(CultureInfo.InvariantCulture,
                    "{0:F1} GB/s ({1} CCD)", fclk * 32.0 * ccds / 1000.0, ccds));
                AddRow(CeilingPanel, Loc.T("Bench.FabricWrite"), string.Format(CultureInfo.InvariantCulture,
                    "{0:F1} GB/s ({1} CCD)", FabricWriteGBs(), ccds));
            }

            if (CeilingPanel.Children.Count == 0)
                AddRow(CeilingPanel, Loc.T("Bench.Unavailable"), Loc.T("Bench.NoClocks"));
        }

        /// <summary>Name on the left, accented value on the right - the ceiling, context and
        /// system panels are all rows of these.</summary>
        private static void AddRow(Panel target, string name, string value)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition());

            var label = new TextBlock { Text = name, FontSize = 11, Padding = new Thickness(0, 1, 0, 1) };
            var text = new TextBlock { Text = value, FontSize = 11, Padding = new Thickness(0, 1, 0, 1) };
            text.SetResourceReference(ForegroundProperty, "AccentTextColor");

            Grid.SetColumn(text, 1);
            row.Children.Add(label);
            row.Children.Add(text);

            target.Children.Add(row);
        }

        private void ShowContext()
        {
            ContextPanel.Children.Clear();

            // The key names stay as they are - they are the labels the panels and the reference
            // profiles use, not prose.
            var live = LiveSnapshot.Build(_viewModel);
            string[] keys = { "Speed", "MCLK", "FCLK", "UCLK", "tCL", "tRCDRD", "tRP", "tRAS", "tRFC" };

            foreach (var key in keys)
            {
                string value;
                if (live.TryGetValue(key, out value))
                    AddRow(ContextPanel, key, value);
            }
        }

        private void RunButton_Click(object sender, RoutedEventArgs e)
        {
            if (!BenchmarkSession.TryEnter())
            {
                // A run from a previous window instance is still winding down - say so rather
                // than swallowing the click.
                SpreadText.Text = Loc.T("Bench.Busy");
                return;
            }

            int megabytes = SelectedMegabytes();

            SetRunning(true);
            SpreadText.Text = Loc.T("Bench.MeasuringLatency");

            // Benchmark mode: the app must not benchmark itself. Every refresh tick makes the SMU
            // DMA the power table into DRAM and runs SMBus traffic - straight into the measurement.
            // The main window owns the timer and the rules around it; this only asks.
            Dictionary<string, string> settings;
            double ceiling;
            Thread worker;
            try
            {
                BenchmarkSession.SuspendPolling();

                // The settings are read before the run, not after: a benchmark saved against the
                // wrong configuration is worse than one not saved at all.
                settings = LiveSnapshot.Build(_viewModel);

                // Read here rather than on the worker: it walks the view model and the memory
                // config, neither of which the measurement thread should be touching.
                //
                // Only at the comparable size. At 256 MB on a big-L3 part the honest read sits
                // ABOVE the bus, so the filter can only choose between a dash and whichever
                // interference-slowed window happened to duck under the clamp - an artifact
                // pinned to the ceiling, oscillating with the dash run to run. Zero filters
                // nothing, and the small-buffer note already says these figures overshoot.
                ceiling = megabytes == BufferMegabytes ? DramCeilingGBs() : 0;
            }
            catch
            {
                // Gate first, then the timer: StartAutoRefresh returns early while the gate is
                // still held, so resuming before leaving resumes nothing.
                BenchmarkSession.Leave();
                BenchmarkSession.ResumePolling();
                SetRunning(false);
                SpreadText.Text = Loc.T("Bench.FailedToStart");
                return;
            }

            worker = new Thread(() =>
            {
                try
                {
                    MemoryLatencyResult latency;
                    MemoryBandwidthResult bandwidth;
                    CacheLadderResult ladder = null;

                    // The bus mutexes belong to whichever thread took them, so they are opened here
                    // rather than around the click. Opened, not taken: the measurements take them
                    // per timed window and hand them back in between, so a monitoring tool waits
                    // out one window instead of the whole run.
                    using (var bus = new HardwareLock())
                    {
                        // Latency first, then bandwidth. The two want opposite things from the
                        // caches, so they are never run at the same time.
                        latency = MemoryLatencyTest.Run(megabytes,
                            p => ReportProgress(p * 0.6 * LadderFrom),
                            () => BenchmarkSession.CancelRequested, bus);

                        if (BenchmarkSession.CancelRequested)
                            return;

                        Dispatcher.Invoke(new Action(() => SpreadText.Text = Loc.T("Bench.MeasuringBandwidth")));

                        bandwidth = MemoryBandwidthTest.Run(megabytes,
                            p => ReportProgress((0.6 + p * 0.4) * LadderFrom),
                            () => BenchmarkSession.CancelRequested, ceiling, bus);

                        // Last, on the core the walk settled on, so the ladder describes the same
                        // core the headline figure came from. A second or so - nearer two with two different L3s - and it reads the
                        // caches the other two spend their time avoiding.
                        // The cancel skips the ladder, not the run: a cancel means the window or
                        // the process is going away, so the hop below would block on a dispatcher
                        // that is shutting down - but latency and bandwidth are both complete by
                        // here, and a forty-second run must still reach the history.
                        if (latency != null && latency.Ok && !BenchmarkSession.CancelRequested)
                        {
                            Dispatcher.Invoke(new Action(() => SpreadText.Text = Loc.T("Bench.MeasuringCache")));

                            ladder = CacheLadderTest.Run(latency.CoreMask,
                                p => ReportProgress(LadderFrom + (1.0 - LadderFrom) * p),
                                () => BenchmarkSession.CancelRequested);
                        }
                    }

                    // A cancel that interrupted a measurement discards the run, as it always did.
                    // One that lands in the ladder's short tail does not: latency and
                    // bandwidth are both complete by then, the ladder is display-only, and a
                    // 40-second run must not vanish from history because the window closed a
                    // moment early.
                    bool measured = latency != null && latency.Ok && bandwidth != null && bandwidth.Ok;
                    if (BenchmarkSession.CancelRequested && !measured)
                        return;

                    var completed = BenchmarkHistory.Capture(latency, bandwidth, settings, ladder);

                    // A run that measured nothing is not history - it would only take a slot in
                    // the fifty the file keeps, and it must not displace the session's real run
                    // as the one the export may photograph.
                    if (completed.LatencyNs > 0 || completed.ReadGBs > 0 || completed.WriteGBs > 0
                        || completed.CopyGBs > 0 || completed.RandomGBs > 0)
                    {
                        BenchmarkHistory.Add(completed);

                        // Remembered so the HTML export can tell this session's own run - the one
                        // the window still describes - from an entry loaded out of the file.
                        BenchmarkSession.RunKey = RunKey(completed);
                    }

                    // Saved is saved; only the painting is skipped on a window that is going away.
                    if (BenchmarkSession.CancelRequested)
                        return;

                    Dispatcher.Invoke(new Action(() =>
                    {
                        ShowResult(latency, bandwidth);
                        ShowResultTab(latency, bandwidth, ladder);
                    }));
                }
                catch (Exception ex)
                {
                    // Never leave the window stuck with a disabled Run button and a live
                    // progress bar - surface the failure and rearm the controls.
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            SetRunning(false);
                            SpreadText.Text = Loc.T("Bench.FailedWith") + ex.Message;
                        }));
                    }
                    catch { }
                }
                finally
                {
                    // Gate release and polling restart travel as ONE dispatcher action: releasing
                    // first would let a new run start and then have this stale restart resume the
                    // polling in the middle of its measurement.
                    try
                    {
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            BenchmarkSession.Leave();
                            BenchmarkSession.ResumePolling();
                        }));
                    }
                    catch
                    {
                        BenchmarkSession.Leave();
                    }
                }
            });

            // Background so a run in progress can never keep the app alive after it is closed.
            worker.IsBackground = true;

            try
            {
                worker.Start();
            }
            catch (Exception ex)
            {
                // The thread never ran, so its finally will not release anything.
                BenchmarkSession.Leave();
                BenchmarkSession.ResumePolling();
                SetRunning(false);
                SpreadText.Text = Loc.T("Bench.FailedToStartWith") + ex.Message;
            }
        }

        /// <summary>
        /// The controls a run owns, on both tabs at once. One place, because a re-arm that reaches
        /// one button and not the other leaves a dead control on the tab the user is looking at,
        /// and there are four paths out of a run.
        /// </summary>
        private void SetRunning(bool running)
        {
            RunButton.IsEnabled = !running;
            RerunButton.IsEnabled = !running;
            RunButton.Content = Loc.T(running ? "Bench.Running" : "Bench.Run");
            RerunButton.Content = Loc.T(running ? "Bench.Running" : "Bench.Rerun");
            BufferSize.IsEnabled = !running;

            if (running)
            {
                progressPerMille = 0;
                Progress.Value = 0;
                ResultProgress.Value = 0;
                progressTimer.Start();
            }
            else
            {
                progressTimer.Stop();
            }

            Progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            ResultProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Where the run has got to, in thousandths. Written by the measurement thread, read by
        /// the timer below. An int because a write of one is atomic by itself - a torn read is
        /// the only thing a lock would buy here, and there is nothing to tear.
        /// </summary>
        private int progressPerMille;

        /// <summary>
        /// The bars are PULLED, not pushed. Posting to the dispatcher would have the measurement
        /// thread allocate a delegate, take the dispatcher's lock and wake another thread, between
        /// two timed windows and at realtime priority; a store costs the run nothing that can be
        /// measured. Background priority because a progress bar is never worth a frame of input.
        /// </summary>
        private readonly DispatcherTimer progressTimer =
            new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(100),
            };

        // Where the cache ladder's share of the bar begins; the walk and the sweep divide what is
        // below it. Narrow because the phase is - a second or two against the forty the other two
        // spend between them - but a full bar has to mean the run is over.
        private const double LadderFrom = 0.95;

        private void ReportProgress(double fraction)
        {
            progressPerMille = (int)(fraction * 1000.0);
        }

        private void ProgressTimer_Tick(object sender, EventArgs e)
        {
            // Both bars, one read. Only the tab on screen is realised, so the other assignment
            // reaches an object nothing draws.
            double fraction = progressPerMille / 1000.0;
            Progress.Value = fraction;
            ResultProgress.Value = fraction;
        }

        private void ShowResult(MemoryLatencyResult latency, MemoryBandwidthResult bandwidth)
        {
            SetRunning(false);

            if (latency != null && latency.Ok)
            {
                // The spread still decides the noisy verdict and travels with the run; the line
                // itself only names the page mode - the one thing a screenshot must not omit.
                string detail = string.Format(
                    Loc.T("Bench.Detail"),
                    Loc.T(latency.LargePages ? "Bench.PagesLarge" : "Bench.Pages4K"));

                string fellBack = LargePageText(latency.LargePageFailure, latency.LargePageError);
                if (fellBack != null)
                    detail += " (" + fellBack + ")";

                if (latency.CacheBound)
                    detail += ", " + Loc.T("Bench.CacheBound");
                if (latency.Noisy)
                    detail += "  -  " + Loc.T("Bench.NoisyLong");

                SpreadText.Text = detail;

                // Only the two causes with a next step: the right can be granted, a reboot frees
                // a block. "Unsupported" has nowhere to send anyone.
                // The walk's cause where it fell back, otherwise the bandwidth pass's own: it asks
                // for two buffers against the walk's one and can miss where the walk did not.
                lastLpFailure = !latency.LargePages
                    ? latency.LargePageFailure
                    : bandwidth != null && bandwidth.Ok && !bandwidth.LargePages
                        ? bandwidth.LargePageFailure
                        : LargePageFailure.None;
                LpHelpButton.Visibility = lastLpFailure == LargePageFailure.NoPrivilege
                    || lastLpFailure == LargePageFailure.Fragmented
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            else
            {
                SpreadText.Text = ErrorText(latency != null ? latency.Reason : BenchmarkError.Failed,
                    latency != null ? latency.Error : null);

                // A walk that produced nothing still leaves the bandwidth pass's fallback on
                // screen, and a cause named with no way to act on it is a dead end.
                lastLpFailure = bandwidth != null && bandwidth.Ok && !bandwidth.LargePages
                    ? bandwidth.LargePageFailure
                    : LargePageFailure.None;
                LpHelpButton.Visibility = lastLpFailure == LargePageFailure.NoPrivilege
                    || lastLpFailure == LargePageFailure.Fragmented
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (bandwidth != null && bandwidth.Ok)
            {
                // Bandwidth asks for two buffers where the walk asked for one, so it can miss on
                // its own, and where the walk produced nothing there is no mode above it at all.
                // The rule is the export's - the pass's own mode wherever it is not the walk's -
                // so one run cannot describe itself two ways.
                if (latency == null || !latency.Ok || bandwidth.LargePages != latency.LargePages)
                {
                    string mode = string.Format(Loc.T("Bench.LpBandwidth"),
                        Loc.T(bandwidth.LargePages ? "Bench.PagesLarge" : "Bench.Pages4K"));

                    // A cause belongs to a fallback; a pass that got its large pages has none.
                    string missed = bandwidth.LargePages
                        ? null
                        : LargePageText(bandwidth.LargePageFailure, bandwidth.LargePageError);
                    if (missed != null)
                        mode += ": " + missed;

                    SpreadText.Text += "  -  " + mode;
                }

                // The fallback loop pays the ownership fetch and reads near half - a write figure
                // means nothing unless the line says which stores produced it.
                if (!bandwidth.Streaming)
                    SpreadText.Text += "  -  " + Loc.T("Bench.SlowStores");

                // The grid dashes a withheld figure exactly as it dashes one never measured;
                // only this line separates them.
                if (bandwidth.AboveBus)
                    SpreadText.Text += "  -  " + Loc.T("Bench.AboveBus");
            }
            else
            {
                // A dashed DRAM row and a healthy latency otherwise look like the bandwidth pass
                // was never asked for. Say why it produced nothing.
                if (latency != null && latency.Ok && bandwidth != null && bandwidth.Reason != BenchmarkError.Cancelled)
                    SpreadText.Text += "  -  " + ErrorText(bandwidth.Reason, bandwidth.Error);
            }

            // The settings can move between opening the window and pressing Run - and the System
            // block carries the timings now, so it has to follow them too.
            ShowContext();
            ShowCeiling();
            BuildSystemInfo();
            RefreshHistory();
        }

        private LargePageFailure lastLpFailure;

        /// <summary>
        /// The "and now what" behind the 4K note - the steps a forum search would otherwise have
        /// to answer, in the user's language.
        /// </summary>
        private void LpHelpButton_Click(object sender, RoutedEventArgs e)
        {
            if (lastLpFailure == LargePageFailure.Fragmented)
            {
                MessageBox.Show(this, Loc.T("Bench.LpHelpFragmented"), Loc.T("Bench.LpHelpTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Offered, not done: the right is granted to the ACCOUNT, so every program the user
            // runs gains it. That is the same thing secpol.msc does, but a button has to say so.
            if (MessageBox.Show(this, Loc.T("Bench.LpHelpNoRight") + "\n\n" + Loc.T("Bench.LpGrantAsk"),
                    Loc.T("Bench.LpHelpTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes)
                return;

            string error;
            bool granted = LargePageRight.Grant(out error);

            MessageBox.Show(this,
                granted ? Loc.T("Bench.LpGrantOk") : Loc.T("Bench.LpGrantFailed") + error,
                Loc.T("Bench.LpHelpTitle"), MessageBoxButton.OK,
                granted ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        /// <summary>
        /// What kept the buffer off large pages, or null when nothing did. "4K pages" on its own
        /// reads like a setting rather than a fallback, and the two causes are not equally the
        /// user's to fix.
        /// </summary>
        private static string LargePageText(LargePageFailure failure, int error)
        {
            switch (failure)
            {
                case LargePageFailure.NoPrivilege: return Loc.T("Bench.LpNoRight");
                case LargePageFailure.Fragmented: return Loc.T("Bench.LpFragmented");
                case LargePageFailure.Unsupported: return Loc.T("Bench.LpUnsupported");
                case LargePageFailure.Other: return string.Format(Loc.T("Bench.LpOther"), error);
                default: return null;
            }
        }

        /// <summary>
        /// The failure in the user's language. Only the catch-all case has nothing to translate,
        /// and there the exception's own text is still better than a generic line.
        /// </summary>
        internal static string ErrorText(BenchmarkError reason, string detail)
        {
            switch (reason)
            {
                case BenchmarkError.BufferOutOfRange: return Loc.T("Bench.ErrRange");
                case BenchmarkError.OutOfMemory: return Loc.T("Bench.ErrMemory");
                case BenchmarkError.Cancelled: return Loc.T("Bench.ErrCancelled");
                case BenchmarkError.MemoryError: return Loc.T("Bench.ErrMemFault");
                default:
                    return string.IsNullOrEmpty(detail)
                        ? Loc.T("Bench.Failed")
                        : Loc.T("Bench.FailedWith") + detail;
            }
        }

        private void RefreshHistory()
        {
            var runs = BenchmarkHistory.Load();
            var baseline = runs.FirstOrDefault(r => r.IsBaseline);

            HistoryList.Items.Clear();

            int index = 0;
            foreach (var run in runs)
            {
                var inv = CultureInfo.InvariantCulture;

                // Line 1: identity and the headline figure, with the latency delta to the baseline.
                // Everything else lives in the hover and the HTML export - the list stays readable.
                // A guillemet, not a star - the cache tables above already use the star for
                // "this figure carries a caveat", and one mark with two meanings misleads.
                string line1 = (run.IsBaseline ? "» " : "  ") + run.Title;

                // A delta only means something between runs the same measurement produced under
                // the same conditions. Otherwise the figures are shown side by side without one.
                bool comparable = baseline != null
                    && baseline.Schema == run.Schema
                    && baseline.BufferMegabytes == run.BufferMegabytes
                    && baseline.LargePages == run.LargePages;

                if (!run.IsBaseline && baseline != null && baseline.LatencyNs > 0 && run.LatencyNs > 0)
                {
                    if (comparable)
                    {
                        double delta = run.LatencyNs - baseline.LatencyNs;
                        line1 += string.Format(inv, "  ({0}{1:F1})", delta >= 0 ? "+" : "", delta);
                    }
                    else
                    {
                        line1 += "  (" + Loc.T("Bench.NotComparable") + ")";
                    }
                }

                if (run.Noisy)
                    line1 += "  [" + Loc.T("Bench.Noisy") + "]";
                if (run.CacheBound)
                    line1 += "  [" + Loc.T("Bench.CacheBound") + "]";

                // Line 2: the bandwidth figures and the buffer, nothing more.
                string line2 = "    " + run.BandwidthText;
                if (run.BufferMegabytes > 0)
                    line2 += string.Format(inv, "   |   {0} MB", run.BufferMegabytes);

                var text = new TextBlock
                {
                    Text = line1 + Environment.NewLine + line2,
                    FontSize = 11,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                };

                // The hover is a glance, not the export: scores, frequency and the primary
                // timings only. The full capture lives in Export HTML.
                string tooltip = null;
                if (run.Settings != null && run.Settings.Count > 0)
                {
                    var lines = new List<string>();

                    string scores = (run.LatencyText + "   " + run.BandwidthText).Trim();
                    if (scores.Length > 0)
                        lines.Add(scores);

                    string speed = run.Get("Speed");
                    string mclk = run.Get("MCLK");
                    if (!string.IsNullOrEmpty(speed))
                        lines.Add(speed + (!string.IsNullOrEmpty(mclk) ? "  (MCLK " + mclk + ")" : ""));

                    string primary = "";
                    foreach (var key in new[] { "tCL", "tRCDRD", "tRP", "tRAS", "tRC", "tRFC" })
                    {
                        string value = run.Get(key);
                        if (!string.IsNullOrEmpty(value))
                            primary += key + " " + value + "   ";
                    }
                    if (primary.Length > 0)
                        lines.Add(primary.Trim());

                    if (lines.Count > 0)
                        tooltip = string.Join(Environment.NewLine, lines);
                }

                HistoryList.Items.Add(new ListBoxItem
                {
                    Content = text,
                    ToolTip = tooltip,
                    Tag = new HistoryRow { Run = run, Index = index++ },
                    Padding = new Thickness(4, 2, 4, 2),
                });
            }

            PinButton.IsEnabled = HistoryList.Items.Count > 0;
            ExportButton.IsEnabled = HistoryList.Items.Count > 0;
        }

        /// <summary>Exports the selected run - or the newest one - as a standalone HTML page.</summary>
        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            var item = HistoryList.SelectedItem as ListBoxItem;
            var row = item != null ? item.Tag as HistoryRow : null;
            var run = row != null ? row.Run : BenchmarkHistory.Load().FirstOrDefault();
            if (run == null)
                return;

            // The window on screen shows the settings in force NOW, so the screenshot only
            // describes a run measured in this session. A run loaded from the file - including
            // the newest one, after a restart - exports from its own recorded snapshot instead.
            string sessionKey = BenchmarkSession.RunKey;
            bool isSessionRun = sessionKey != null && RunKey(run) == sessionKey;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "HTML file (*.html)|*.html",
                FileName = "ZenTimings-benchmark-" +
                    run.Timestamp.Replace(":", "-").Replace(" ", "-") + ".html",
            };

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                System.IO.File.WriteAllText(dialog.FileName,
                    BenchmarkExport.BuildHtml(run, isSessionRun ? CaptureMainWindowPng() : null));
                MessageBox.Show(this, Loc.T("Bench.ExportOk"), Loc.T("Menu.ExportHtml"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, Loc.T("Bench.ExportFailed") + ex.Message, Loc.T("Menu.ExportHtml"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>Marks the selected run as the baseline every other row is measured against.</summary>
        private void PinButton_Click(object sender, RoutedEventArgs e)
        {
            // Nothing selected pins the newest run - the list clears its selection after every
            // run, which is exactly when this button is most likely to be pressed.
            var item = (HistoryList.SelectedItem ?? HistoryList.Items.Cast<object>().FirstOrDefault()) as ListBoxItem;
            var selected = item != null ? item.Tag as HistoryRow : null;
            if (selected == null)
                return;

            BenchmarkHistory.PinBaseline(selected.Index, selected.Run.Timestamp, selected.Run.LatencyNs);
            RefreshHistory();

            // The grid follows the pin immediately: its deltas re-read against the run just
            // chosen, not against whatever was pinned when the measurement finished.
            if (shownLatency != null || shownLadder != null)
                CacheTable.Build(TablePanel, shownLadder, shownLatency, shownBandwidth,
                    ComparableBaseline(shownLatency));
        }

        /// <summary>
        /// Clicking a history row reads the grid's deltas against that run - a preview, the pin
        /// stays where it is. Clearing the selection, or picking the run on screen itself, falls
        /// back to the pinned reference.
        /// </summary>
        private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (shownLatency == null && shownLadder == null)
                return;

            var item = HistoryList.SelectedItem as ListBoxItem;
            var row = item != null ? item.Tag as HistoryRow : null;

            // Row 0 is the run on screen - a delta against itself is a column of zeros.
            var reference = row != null && row.Index != 0
                ? ComparableReference(shownLatency, row.Run)
                : ComparableBaseline(shownLatency);

            CacheTable.Build(TablePanel, shownLadder, shownLatency, shownBandwidth, reference);
        }

        /// <summary>
        /// The main window as a PNG, rendered from the visual tree - clean even when another
        /// window overlaps it. Null when there is nothing to render; the export then falls back
        /// to its data tables.
        /// </summary>
        private static string CaptureMainWindowPng()
        {
            try
            {
                return VisualCapture.ToPngBase64(VisualCapture.Render(Application.Current?.MainWindow));
            }
            catch
            {
                return null;
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Whoever is measuring stops at its next check; the run's own finally then releases
            // the gate and restarts the polling.
            BenchmarkSession.RequestCancel();

            // The timer outlives the window otherwise: it belongs to the dispatcher, not to this
            // window, and a run winding down would keep it ticking against controls nobody draws.
            progressTimer.Stop();
        }
    }
}
