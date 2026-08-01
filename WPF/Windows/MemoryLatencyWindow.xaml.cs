using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
            // up; the ScrollViewer takes over from here.
            MaxHeight = SystemParameters.WorkArea.Height;

            _viewModel = viewModel;
            ShowContext();
            ShowCeiling();
            TagCacheBoundSizes();
            RefreshHistory();
        }

        /// <summary>
        /// A buffer that fits in (or near) the L3 measures the cache, not the memory - flag those
        /// choices rather than silently producing a blended number. X3D parts make this real: a
        /// 96 MB L3 swallows most of a 128 MB walk.
        /// </summary>
        private void TagCacheBoundSizes()
        {
            long l3 = MemoryLatencyTest.L3Bytes;
            if (l3 <= 0)
                return;

            ComboBoxItem firstClean = null;
            foreach (ComboBoxItem item in BufferSize.Items)
            {
                int megabytes;
                if (item.Tag == null ||
                    !int.TryParse(item.Tag.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out megabytes))
                    continue;

                if (MemoryLatencyTest.IsCacheBound((long)megabytes * 1024 * 1024, l3))
                {
                    item.Content = megabytes + " MB (" + Loc.T("Bench.CacheBound") + ")";
                }
                else if (firstClean == null)
                {
                    firstClean = item;
                }
            }

            var selected = BufferSize.SelectedItem as ComboBoxItem;
            if (firstClean != null && selected != null &&
                (selected.Content as string ?? "").Contains(Loc.T("Bench.CacheBound")))
            {
                BufferSize.SelectedItem = firstClean;
            }
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
            float mts = _viewModel != null ? _viewModel.MemoryFrequency : 0;
            float fclk = power != null ? power.FCLK : 0;

            int ccds = 1;
            int channels = 1;
            try
            {
                ccds = Math.Max(1, CpuSingleton.Instance.systemInfo.CCDCount);
                channels = Math.Max(1, CpuSingleton.Instance.GetMemoryConfig()
                    .Modules.Select(m => m.DctOffset).Distinct().Count());
            }
            catch { }

            if (mts > 0)
            {
                AddRow(CeilingPanel, Loc.T("Bench.DramBus"), string.Format(CultureInfo.InvariantCulture,
                    "{0:F1} GB/s ({1} ch)", mts * 8.0 * channels / 1000.0, channels));
            }

            if (fclk > 0)
            {
                AddRow(CeilingPanel, Loc.T("Bench.FabricRead"), string.Format(CultureInfo.InvariantCulture,
                    "{0:F1} GB/s ({1} CCD)", fclk * 32.0 * ccds / 1000.0, ccds));
                AddRow(CeilingPanel, Loc.T("Bench.FabricWrite"), string.Format(CultureInfo.InvariantCulture,
                    "{0:F1} GB/s ({1} CCD)", fclk * 16.0 * ccds / 1000.0, ccds));
            }

            if (CeilingPanel.Children.Count == 0)
                AddRow(CeilingPanel, Loc.T("Bench.Unavailable"), Loc.T("Bench.NoClocks"));
        }

        /// <summary>Name on the left, accented value on the right - both panels are rows of these.</summary>
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

            RunButton.IsEnabled = false;
            BufferSize.IsEnabled = false;
            Progress.Value = 0;
            Progress.Visibility = Visibility.Visible;
            ResultText.Text = "...";
            ReadText.Text = "-";
            WriteText.Text = "-";
            CopyText.Text = "-";
            RandomText.Text = "-";
            SpreadText.Text = Loc.T("Bench.MeasuringLatency");

            // Benchmark mode: the app must not benchmark itself. Every refresh tick makes the SMU
            // DMA the power table into DRAM and runs SMBus traffic - straight into the measurement.
            // The main window owns the timer and the rules around it; this only asks.
            Dictionary<string, string> settings;
            Thread worker;
            try
            {
                BenchmarkSession.SuspendPolling();

                // The settings are read before the run, not after: a benchmark saved against the
                // wrong configuration is worse than one not saved at all.
                settings = LiveSnapshot.Build(_viewModel);
            }
            catch
            {
                // Nothing may leave the gate held or the polling stopped.
                BenchmarkSession.ResumePolling();
                BenchmarkSession.Leave();
                RunButton.IsEnabled = true;
                BufferSize.IsEnabled = true;
                Progress.Visibility = Visibility.Hidden;
                SpreadText.Text = Loc.T("Bench.FailedToStart");
                return;
            }

            worker = new Thread(() =>
            {
                try
                {
                    // Latency first, then bandwidth. The two want opposite things from the caches,
                    // so they are never run at the same time.
                    var latency = MemoryLatencyTest.Run(megabytes, p => ReportProgress(p * 0.6),
                        () => BenchmarkSession.CancelRequested);

                    if (BenchmarkSession.CancelRequested)
                        return;

                    Dispatcher.Invoke(new Action(() => SpreadText.Text = Loc.T("Bench.MeasuringBandwidth")));

                    var bandwidth = MemoryBandwidthTest.Run(megabytes, p => ReportProgress(0.6 + p * 0.4),
                        () => BenchmarkSession.CancelRequested);

                    if (BenchmarkSession.CancelRequested)
                        return;

                    var completed = BenchmarkHistory.Capture(latency, bandwidth, settings);

                    // A run that measured nothing is not history - it would only take a slot in
                    // the fifty the file keeps, and it must not displace the session's real run
                    // as the one the export may photograph.
                    if (completed.LatencyNs > 0 || completed.ReadGBs > 0)
                    {
                        BenchmarkHistory.Add(completed);

                        // Remembered so the HTML export can tell this session's own run - the one
                        // the window still describes - from an entry loaded out of the file.
                        BenchmarkSession.RunKey = RunKey(completed);
                    }

                    Dispatcher.Invoke(new Action(() => ShowResult(latency, bandwidth)));
                }
                catch (Exception ex)
                {
                    // Never leave the window stuck with a disabled Run button and a live
                    // progress bar - surface the failure and rearm the controls.
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            RunButton.IsEnabled = true;
                            BufferSize.IsEnabled = true;
                            Progress.Visibility = Visibility.Hidden;
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
                RunButton.IsEnabled = true;
                BufferSize.IsEnabled = true;
                Progress.Visibility = Visibility.Hidden;
                SpreadText.Text = Loc.T("Bench.FailedToStartWith") + ex.Message;
            }
        }

        private void ReportProgress(double fraction)
        {
            if (BenchmarkSession.CancelRequested)
                return;

            try
            {
                Dispatcher.BeginInvoke(new Action(() => Progress.Value = fraction));
            }
            catch
            {
                // The window went away mid-run; nothing to update.
            }
        }

        private void ShowResult(MemoryLatencyResult latency, MemoryBandwidthResult bandwidth)
        {
            RunButton.IsEnabled = true;
            BufferSize.IsEnabled = true;
            Progress.Visibility = Visibility.Hidden;

            if (latency != null && latency.Ok)
            {
                ResultText.Text = latency.Nanoseconds.ToString("F1", CultureInfo.InvariantCulture) + " ns";

                string detail = string.Format(
                    Loc.T("Bench.Detail"),
                    latency.PassCount,
                    latency.SpreadNs.ToString("F2", CultureInfo.InvariantCulture),
                    latency.BufferMegabytes,
                    Loc.T(latency.LargePages ? "Bench.PagesLarge" : "Bench.Pages4K"));

                if (latency.CacheBound)
                    detail += ", " + Loc.T("Bench.CacheBound");
                if (latency.Noisy)
                    detail += "  -  " + Loc.T("Bench.NoisyLong");

                SpreadText.Text = detail;
            }
            else
            {
                ResultText.Text = "-";
                SpreadText.Text = ErrorText(latency != null ? latency.Reason : BenchmarkError.Failed,
                    latency != null ? latency.Error : null);
            }

            if (bandwidth != null && bandwidth.Ok)
            {
                ReadText.Text = bandwidth.ReadGBs.ToString("F1", CultureInfo.InvariantCulture) + " GB/s";
                WriteText.Text = bandwidth.WriteGBs.ToString("F1", CultureInfo.InvariantCulture) + " GB/s";
                CopyText.Text = bandwidth.CopyGBs.ToString("F1", CultureInfo.InvariantCulture) + " GB/s";
                RandomText.Text = bandwidth.RandomGBs > 0
                    ? bandwidth.RandomGBs.ToString("F1", CultureInfo.InvariantCulture) + " GB/s"
                    : "-";
            }
            else
            {
                ReadText.Text = "-";
                WriteText.Text = "-";
                CopyText.Text = "-";
                RandomText.Text = "-";

                // Four dashes and a healthy latency line otherwise look like the bandwidth pass
                // was never asked for. Say why it produced nothing.
                if (latency != null && latency.Ok && bandwidth != null && bandwidth.Reason != BenchmarkError.Cancelled)
                    SpreadText.Text += "  -  " + ErrorText(bandwidth.Reason, bandwidth.Error);
            }

            // The settings can move between opening the window and pressing Run.
            ShowContext();
            ShowCeiling();
            RefreshHistory();
        }

        /// <summary>
        /// The failure in the user's language. Only the catch-all case has nothing to translate,
        /// and there the exception's own text is still better than a generic line.
        /// </summary>
        private static string ErrorText(BenchmarkError reason, string detail)
        {
            switch (reason)
            {
                case BenchmarkError.BufferOutOfRange: return Loc.T("Bench.ErrRange");
                case BenchmarkError.OutOfMemory: return Loc.T("Bench.ErrMemory");
                case BenchmarkError.Cancelled: return Loc.T("Bench.ErrCancelled");
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
                string line1 = (run.IsBaseline ? "* " : "  ") + run.Title;

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

        private int SelectedMegabytes()
        {
            var item = BufferSize.SelectedItem as ComboBoxItem;
            int value;

            if (item != null && item.Tag != null &&
                int.TryParse(item.Tag.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }

            return 256;
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Whoever is measuring stops at its next check; the run's own finally then releases
            // the gate and restarts the polling.
            BenchmarkSession.RequestCancel();
        }
    }
}
