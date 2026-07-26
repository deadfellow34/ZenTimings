using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
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
        private Thread _worker;
        private volatile bool _closing;

        public MemoryLatencyWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            ShowContext();
            ShowCeiling();
        }

        /// <summary>
        /// What the hardware could deliver at best, so a measured figure can be read as a fraction
        /// of it rather than as a bare number.
        /// </summary>
        /// <remarks>
        /// Two separate ceilings, and the lower one is what actually binds:
        ///
        /// The DRAM ceiling is the bus itself - a DDR5 UDIMM is 64 bits wide however it is split
        /// into subchannels, so it moves 8 bytes per transfer.
        ///
        /// The fabric ceiling is the link between the core complex and the memory controller,
        /// which runs at FCLK and is not symmetric: a single-CCD desktop part reads 32 bytes per
        /// fabric clock and writes 16. That asymmetry is why a write score sits so far below a read
        /// score on these chips, and why raising FCLK helps writes twice as much as reads.
        /// </remarks>
        private void ShowCeiling()
        {
            CeilingPanel.Children.Clear();

            var power = _viewModel != null ? _viewModel.PowerTable : null;
            float mts = _viewModel != null ? _viewModel.MemoryFrequency : 0;
            float fclk = power != null ? power.FCLK : 0;

            if (mts > 0)
            {
                double perDimm = mts * 8.0 / 1000.0;   // GB/s for one 64-bit module
                AddCeilingRow("DRAM bus", string.Format(CultureInfo.InvariantCulture,
                    "{0:F1} GB/s per module", perDimm));
            }

            if (fclk > 0)
            {
                AddCeilingRow("Fabric read", string.Format(CultureInfo.InvariantCulture,
                    "{0:F1} GB/s", fclk * 32.0 / 1000.0));
                AddCeilingRow("Fabric write", string.Format(CultureInfo.InvariantCulture,
                    "{0:F1} GB/s", fclk * 16.0 / 1000.0));
            }

            if (CeilingPanel.Children.Count == 0)
                AddCeilingRow("Unavailable", "clocks not reported");
        }

        private void AddCeilingRow(string name, string value)
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

            CeilingPanel.Children.Add(row);
        }

        private void ShowContext()
        {
            ContextPanel.Children.Clear();

            var live = LiveSnapshot.Build(_viewModel);
            string[] keys = { "Speed", "MCLK", "FCLK", "UCLK", "tCL", "tRCDRD", "tRP", "tRAS", "tRFC" };

            foreach (var key in keys)
            {
                string value;
                if (!live.TryGetValue(key, out value))
                    continue;

                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                row.ColumnDefinitions.Add(new ColumnDefinition());

                var name = new TextBlock { Text = key, FontSize = 11, Padding = new Thickness(0, 1, 0, 1) };
                var val = new TextBlock { Text = value, FontSize = 11, Padding = new Thickness(0, 1, 0, 1) };
                val.SetResourceReference(ForegroundProperty, "AccentTextColor");

                Grid.SetColumn(val, 1);
                row.Children.Add(name);
                row.Children.Add(val);

                ContextPanel.Children.Add(row);
            }
        }

        private void RunButton_Click(object sender, RoutedEventArgs e)
        {
            if (_worker != null && _worker.IsAlive)
                return;

            int megabytes = SelectedMegabytes();

            RunButton.IsEnabled = false;
            BufferSize.IsEnabled = false;
            Progress.Value = 0;
            Progress.Visibility = Visibility.Visible;
            ResultText.Text = "...";
            ReadText.Text = "-";
            WriteText.Text = "-";
            CopyText.Text = "-";
            SpreadText.Text = "Measuring latency";

            // The settings are read before the run, not after: a benchmark saved against the wrong
            // configuration is worse than one not saved at all.
            var settings = LiveSnapshot.Build(_viewModel);

            _worker = new Thread(() =>
            {
                // Latency first, then bandwidth. The two want opposite things from the caches, so
                // they are never run at the same time.
                var latency = MemoryLatencyTest.Run(megabytes, p => ReportProgress(p * 0.6));

                if (_closing)
                    return;

                Dispatcher.Invoke(new Action(() => SpreadText.Text = "Measuring bandwidth"));

                var bandwidth = MemoryBandwidthTest.Run(megabytes, p => ReportProgress(0.6 + p * 0.4));

                if (_closing)
                    return;

                BenchmarkHistory.Add(BenchmarkHistory.Capture(latency, bandwidth, settings));

                Dispatcher.Invoke(new Action(() => ShowResult(latency, bandwidth)));
            });

            // Background so a run in progress can never keep the app alive after it is closed.
            _worker.IsBackground = true;
            _worker.Start();
        }

        private void ReportProgress(double fraction)
        {
            if (_closing)
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
                SpreadText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "best of 7 passes, spread {0:F2} ns, {1} MB buffer",
                    latency.SpreadNs, latency.BufferMegabytes);
            }
            else
            {
                ResultText.Text = "-";
                SpreadText.Text = (latency != null ? latency.Error : null) ?? "Failed";
            }

            if (bandwidth != null && bandwidth.Ok)
            {
                ReadText.Text = bandwidth.ReadGBs.ToString("F1", CultureInfo.InvariantCulture) + " GB/s";
                WriteText.Text = bandwidth.WriteGBs.ToString("F1", CultureInfo.InvariantCulture) + " GB/s";
                CopyText.Text = bandwidth.CopyGBs.ToString("F1", CultureInfo.InvariantCulture) + " GB/s";
            }
            else
            {
                ReadText.Text = "-";
                WriteText.Text = "-";
                CopyText.Text = "-";
            }

            // The settings can move between opening the window and pressing Run.
            ShowContext();
            ShowCeiling();
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
            _closing = true;
        }
    }
}
