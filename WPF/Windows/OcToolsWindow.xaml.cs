using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ZenStates.Core;
using ZenTimings.ViewModels;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;

namespace ZenTimings.Windows
{
    /// <summary>One row of the "SPD vs Live" grid.</summary>
    public class SpdComparisonRow
    {
        public string Name { get; set; }
        public string RatedNsText { get; set; }
        public string RatedNckText { get; set; }
        public string LiveNckText { get; set; }
        public string LiveNsText { get; set; }
        public string MarginText { get; set; }
    }

    /// <summary>
    /// Three tools that belong together because they all answer "what actually changed?":
    ///   Register Diff - dump every UMC register and compare two dumps.
    ///   Profiles      - name and store the full timing/voltage state, then diff two of them.
    ///   SPD vs Live   - the module's JEDEC ratings next to the values the controller is running.
    /// </summary>
    public partial class OcToolsWindow : ThemedAdonisWindow
    {
        private readonly Cpu cpu;
        private readonly MainViewModel viewModel;
        private readonly System.Windows.Threading.DispatcherTimer refreshTimer;

        private UmcSnapshot _snapshotA;
        private UmcSnapshot _snapshotB;
        private List<OcProfile> _profiles = new List<OcProfile>();
        private bool _busy;

        public OcToolsWindow(MainViewModel vm, System.Windows.Threading.DispatcherTimer autoRefreshTimer)
        {
            InitializeComponent();

            viewModel = vm;
            refreshTimer = autoRefreshTimer;
            cpu = CpuSingleton.Instance;

            Loaded += OcToolsWindow_Loaded;
        }

        private void OcToolsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ReloadProfiles();
            BuildSpdComparison();
        }

        private void OcToolsWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_busy)
            {
                // A capture is thousands of SMN reads under the PCI bus mutex; tearing the window
                // down underneath it would leave the mutex held.
                e.Cancel = true;
                StatusText.Text = "Capture in progress - the window will close once it finishes.";
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;

            ButtonCaptureA.IsEnabled = !busy;
            ButtonCaptureB.IsEnabled = !busy;
            ButtonLoadA.IsEnabled = !busy;
            ButtonLoadB.IsEnabled = !busy;
            ButtonSaveA.IsEnabled = !busy;
            ButtonSaveB.IsEnabled = !busy;
            ButtonCompareRegisters.IsEnabled = !busy;
        }

        // ------------------------------------------------------------------ Register diff

        private async void ButtonCaptureA_Click(object sender, RoutedEventArgs e)
        {
            _snapshotA = await CaptureAsync("A");
            UpdateSnapshotLabels();
            RunRegisterCompare();
        }

        private async void ButtonCaptureB_Click(object sender, RoutedEventArgs e)
        {
            _snapshotB = await CaptureAsync("B");
            UpdateSnapshotLabels();
            RunRegisterCompare();
        }

        private async Task<UmcSnapshot> CaptureAsync(string label)
        {
            if (cpu == null)
            {
                MessageBox.Show("CPU access is not available.", "OC Tools",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }

            SetBusy(true);
            StatusText.Text = string.Format(CultureInfo.CurrentCulture,
                "Capturing snapshot {0}… (0x{1:X5}-0x{2:X5} per enabled channel)",
                label, UmcSnapshot.BlockStart, UmcSnapshot.BlockEnd);

            // Pause auto-refresh for the sweep. The refresh thread also issues SMN reads, and a read
            // that interleaves with this one can come back with the other request's data - which
            // would show up in the diff as a register that "changed" when nothing did.
            bool timerWasRunning = refreshTimer != null && refreshTimer.IsEnabled;
            if (timerWasRunning)
                refreshTimer.Stop();

            UmcSnapshot snapshot = null;
            string error = null;

            try
            {
                snapshot = await Task.Run(() => UmcSnapshot.Capture(cpu, label));
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            finally
            {
                if (timerWasRunning)
                    refreshTimer.Start();
            }

            SetBusy(false);

            if (error != null)
            {
                StatusText.Text = "Capture failed.";
                MessageBox.Show("Snapshot failed:\n" + error, "OC Tools",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }

            StatusText.Text = string.Format(CultureInfo.CurrentCulture,
                "Snapshot {0}: {1} registers.", label, snapshot != null ? snapshot.Count : 0);

            return snapshot;
        }

        private void ButtonLoadA_Click(object sender, RoutedEventArgs e)
        {
            var loaded = LoadSnapshot();
            if (loaded != null) _snapshotA = loaded;
            UpdateSnapshotLabels();
            RunRegisterCompare();
        }

        private void ButtonLoadB_Click(object sender, RoutedEventArgs e)
        {
            var loaded = LoadSnapshot();
            if (loaded != null) _snapshotB = loaded;
            UpdateSnapshotLabels();
            RunRegisterCompare();
        }

        private UmcSnapshot LoadSnapshot()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Snapshots and debug reports (*.txt)|*.txt|All files (*.*)|*.*",
                RestoreDirectory = true,
            };

            if (dialog.ShowDialog() != true)
                return null;

            try
            {
                var snapshot = UmcSnapshot.Load(dialog.FileName);
                if (snapshot.Count == 0)
                {
                    MessageBox.Show(
                        "No register values were found in that file.\n\n" +
                        "Expected either a snapshot saved here, or a ZenTimings debug report.",
                        "OC Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;
                }

                StatusText.Text = string.Format(CultureInfo.CurrentCulture,
                    "Loaded {0} registers from {1}.", snapshot.Count, System.IO.Path.GetFileName(dialog.FileName));

                return snapshot;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not read that file:\n" + ex.Message, "OC Tools",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
        }

        private void ButtonSaveA_Click(object sender, RoutedEventArgs e)
        {
            SaveSnapshot(_snapshotA, "A");
        }

        private void ButtonSaveB_Click(object sender, RoutedEventArgs e)
        {
            SaveSnapshot(_snapshotB, "B");
        }

        private void SaveSnapshot(UmcSnapshot snapshot, string label)
        {
            if (snapshot == null || snapshot.Count == 0)
            {
                MessageBox.Show("Snapshot " + label + " is empty - capture or load it first.",
                    "OC Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Snapshot (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = "txt",
                FileName = string.Format(CultureInfo.InvariantCulture,
                    "umc-snapshot-{0}-{1:yyyyMMdd-HHmmss}.txt", label, snapshot.TakenAt),
                RestoreDirectory = true,
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                snapshot.Save(dialog.FileName);
                StatusText.Text = "Saved " + dialog.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save the snapshot:\n" + ex.Message, "OC Tools",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ButtonCompareRegisters_Click(object sender, RoutedEventArgs e)
        {
            RunRegisterCompare();
        }

        private void CheckShowUnchanged_Click(object sender, RoutedEventArgs e)
        {
            RunRegisterCompare();
        }

        private void RunRegisterCompare()
        {
            if (_snapshotA == null || _snapshotB == null)
            {
                RegisterGrid.ItemsSource = null;
                return;
            }

            bool includeUnchanged = CheckShowUnchanged.IsChecked == true;
            var deltas = UmcSnapshot.Compare(_snapshotA, _snapshotB, includeUnchanged);

            RegisterGrid.ItemsSource = deltas;

            int changed = deltas.Count(d => !d.InA || !d.InB || d.ValueA != d.ValueB);
            StatusText.Text = string.Format(CultureInfo.CurrentCulture,
                "{0} register(s) changed out of {1} compared.",
                changed,
                Math.Max(_snapshotA.Count, _snapshotB.Count));
        }

        private void UpdateSnapshotLabels()
        {
            TextSnapshotA.Text = _snapshotA != null ? _snapshotA.Summary : "-";
            TextSnapshotB.Text = _snapshotB != null ? _snapshotB.Summary : "-";
        }

        // ------------------------------------------------------------------ Profiles

        private void ReloadProfiles()
        {
            try
            {
                _profiles = OcProfile.LoadAll();
            }
            catch (Exception ex)
            {
                _profiles = new List<OcProfile>();
                StatusText.Text = "Could not read profiles: " + ex.Message;
            }

            ProfileList.ItemsSource = null;
            ProfileList.ItemsSource = _profiles;
        }

        private void ButtonRefreshProfiles_Click(object sender, RoutedEventArgs e)
        {
            ReloadProfiles();
            StatusText.Text = string.Format(CultureInfo.CurrentCulture, "{0} profile(s).", _profiles.Count);
        }

        private void ButtonSaveProfile_Click(object sender, RoutedEventArgs e)
        {
            var prompt = new TextPromptWindow("Save profile", "Profile name:",
                DateTime.Now.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture))
            {
                Owner = this
            };

            if (prompt.ShowDialog() != true)
                return;

            string name = prompt.Value;
            if (string.IsNullOrEmpty(name))
                return;

            try
            {
                var profile = OcProfile.CaptureCurrent(name, viewModel);
                string path = profile.Save();
                ReloadProfiles();
                StatusText.Text = "Saved " + path;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save the profile:\n" + ex.Message, "OC Tools",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ButtonDeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            var selected = ProfileList.SelectedItems.Cast<OcProfile>().ToList();
            if (selected.Count == 0)
            {
                StatusText.Text = "Select a profile first.";
                return;
            }

            foreach (var profile in selected)
                OcProfile.Delete(profile);

            ReloadProfiles();
            ProfileGrid.ItemsSource = null;
            StatusText.Text = string.Format(CultureInfo.CurrentCulture, "Deleted {0} profile(s).", selected.Count);
        }

        private void ButtonCompareToLive_Click(object sender, RoutedEventArgs e)
        {
            var selected = ProfileList.SelectedItems.Cast<OcProfile>().ToList();
            if (selected.Count != 1)
            {
                StatusText.Text = "Select exactly one profile to compare against the live state.";
                return;
            }

            var live = OcProfile.CaptureCurrent("(live)", viewModel);
            ShowProfileDiff(selected[0], live);
        }

        private void ButtonCompareTwo_Click(object sender, RoutedEventArgs e)
        {
            var selected = ProfileList.SelectedItems.Cast<OcProfile>().ToList();
            if (selected.Count != 2)
            {
                StatusText.Text = "Select exactly two profiles (Ctrl+click).";
                return;
            }

            // ListBox hands back selection in click order; sort so A is always the older one.
            var ordered = selected.OrderBy(p => p.CreatedAt, StringComparer.Ordinal).ToList();
            ShowProfileDiff(ordered[0], ordered[1]);
        }

        private void ShowProfileDiff(OcProfile a, OcProfile b)
        {
            var deltas = OcProfile.Compare(a, b, true);
            ProfileGrid.ItemsSource = deltas;

            StatusText.Text = deltas.Count == 0
                ? string.Format(CultureInfo.CurrentCulture, "'{0}' and '{1}' are identical.", a.Name, b.Name)
                : string.Format(CultureInfo.CurrentCulture,
                    "{0} difference(s).   A = '{1}'   B = '{2}'", deltas.Count, a.Name, b.Name);
        }

        // ------------------------------------------------------------------ SPD vs Live

        private void ButtonRefreshSpd_Click(object sender, RoutedEventArgs e)
        {
            SpdRatedTimings.Invalidate();
            BuildSpdComparison();
        }

        // rated name -> live property on BaseDramTimings
        private static readonly string[][] SpdLiveMap =
        {
            new[] { "tCL",         "CL" },
            new[] { "tRCD",        "RCDRD" },
            new[] { "tRP",         "RP" },
            new[] { "tRAS",        "RAS" },
            new[] { "tRC",         "RC" },
            new[] { "tWR",         "WR" },
            new[] { "tRRDL",       "RRDL" },
            new[] { "tFAW",        "FAW" },
            new[] { "tWTRL",       "WTRL" },
            new[] { "tWTRS",       "WTRS" },
            new[] { "tRTP",        "RTP" },
            new[] { "tRFC",        "RFC" },
            new[] { "tRFC2",       "RFC2" },
            new[] { "tRFCsb",      "RFCsb" },
            new[] { "tCCD_L",      null },       // filled from the UMC register, not from Timings
            new[] { "tCCD_L_WR",   null },
            new[] { "tCCD_L_WR2",  null },
        };

        private async void BuildSpdComparison()
        {
            ButtonRefreshSpd.IsEnabled = false;
            TextSpdHeader.Text = "Reading SPD…";

            Dictionary<string, RatedTiming> rated = null;
            string readError = null;

            try
            {
                // The first read transfers 1 KB over SMBus; keep it off the UI thread.
                rated = await Task.Run(() => SpdRatedTimings.Cached);
            }
            catch (Exception ex)
            {
                readError = ex.Message;
            }

            ButtonRefreshSpd.IsEnabled = true;

            if (readError != null)
            {
                TextSpdHeader.Text = "SPD read failed: " + readError;
                SpdGrid.ItemsSource = null;
                return;
            }

            if (rated == null || rated.Count == 0)
            {
                TextSpdHeader.Text =
                    "No raw SPD image available (DDR4 modules and some platforms do not expose one).";
                SpdGrid.ItemsSource = null;
                return;
            }

            float frequency = viewModel != null ? viewModel.MemoryFrequency : 0f;
            double liveTckPs = frequency > 0 ? 2000000.0 / frequency : 0;
            int baseTck = SpdRatedTimings.BaseTckPicoseconds;

            TextSpdHeader.Text = string.Format(CultureInfo.CurrentCulture,
                "JEDEC column = the module's base SPD profile (tCK {0} ps ≈ {1:0} MT/s), converted to clocks at the current {2:0} MT/s. " +
                "XMP/EXPO profiles legitimately run tighter than the base profile, so a negative margin is expected on a tuned system - " +
                "it only tells you how far past JEDEC baseline you are.",
                baseTck > 0 ? baseTck.ToString(CultureInfo.InvariantCulture) : "?",
                // SPD stores tCKAVGmin truncated (416 ps for DDR5-4800, not 416.667), so the raw
                // reciprocal lands on 4808 - snap it to the nearest speed grade for display.
                baseTck > 0 ? Math.Round(2000000.0 / baseTck / 100.0) * 100.0 : 0,
                frequency);

            var rows = new List<SpdComparisonRow>();

            foreach (var pair in SpdLiveMap)
            {
                RatedTiming rating;
                if (!rated.TryGetValue(pair[0], out rating))
                    continue;

                double? live = GetLiveClocks(pair[0], pair[1]);
                int ratedNck = rating.NckAt(liveTckPs);

                var row = new SpdComparisonRow
                {
                    Name = pair[0],
                    RatedNsText = rating.Nanoseconds.ToString("0.###", CultureInfo.InvariantCulture),
                    RatedNckText = ratedNck > 0 ? ratedNck.ToString(CultureInfo.InvariantCulture) : "-",
                    LiveNckText = live.HasValue ? live.Value.ToString("0", CultureInfo.InvariantCulture) : "-",
                    LiveNsText = "-",
                    MarginText = "-",
                };

                if (live.HasValue && frequency > 0)
                {
                    double liveNs = live.Value * 2000.0 / frequency;
                    row.LiveNsText = liveNs.ToString("0.###", CultureInfo.InvariantCulture);

                    double marginNs = liveNs - rating.Nanoseconds;
                    row.MarginText = string.Format(CultureInfo.InvariantCulture,
                        "{0}{1:0.###} ns  ({2})",
                        marginNs >= 0 ? "+" : "",
                        marginNs,
                        marginNs >= 0 ? "at or above JEDEC" : "below JEDEC base");
                }

                rows.Add(row);
            }

            SpdGrid.ItemsSource = rows;
            StatusText.Text = string.Format(CultureInfo.CurrentCulture, "{0} rated timing(s) decoded from SPD.", rows.Count);
        }

        private double? GetLiveClocks(string ratedName, string timingProperty)
        {
            if (viewModel == null)
                return null;

            if (ratedName == "tCCD_L")
                return viewModel.TccdlValue > 0 ? (double?)viewModel.TccdlValue : null;

            if (ratedName == "tCCD_L_WR2")
                return viewModel.TccdlWr2Value > 0 ? (double?)viewModel.TccdlWr2Value : null;

            if (ratedName == "tCCD_L_WR")
                return null; // no register identified for it yet

            if (timingProperty == null || viewModel.Timings == null)
                return null;

            try
            {
                var prop = viewModel.Timings.GetType().GetProperty(timingProperty);
                object value = prop?.GetValue(viewModel.Timings, null);
                if (value == null)
                    return null;

                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }
    }
}
