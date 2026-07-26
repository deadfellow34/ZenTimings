using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using ZenTimings.ViewModels;

namespace ZenTimings.Windows
{
    /// <summary>
    /// Shows a recorded set of memory settings next to what this machine reads now.
    /// Read-only: the app has no write path to the memory controller.
    /// </summary>
    public partial class OcProfilesWindow : ThemedAdonisWindow
    {
        private readonly MainViewModel _viewModel;
        private Dictionary<string, string> _live;

        public OcProfilesWindow(MainViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;

            // Taken in one go rather than read per row: a column that moved while being read would
            // make the comparison meaningless.
            _live = LiveSnapshot.Build(viewModel);

            var profiles = ReferenceProfiles.AllIncludingSavedRuns();
            ProfileSelector.ItemsSource = profiles;

            if (profiles.Count > 0)
                ProfileSelector.SelectedIndex = 0;
        }

        /// <summary>Brings a profile to the front, for the menu entries that name one.</summary>
        public void Select(ReferenceProfile profile)
        {
            // Re-read: the window is reused, so an open one would otherwise keep comparing against
            // whatever the machine was doing when it was first opened.
            _live = LiveSnapshot.Build(_viewModel);

            if (profile != null && !ReferenceEquals(ProfileSelector.SelectedItem, profile))
            {
                ProfileSelector.SelectedItem = profile;
                return;
            }

            Show(ProfileSelector.SelectedItem as ReferenceProfile);
        }

        private void ProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Show(ProfileSelector.SelectedItem as ReferenceProfile);
        }

        private void Show(ReferenceProfile profile)
        {
            if (profile == null)
            {
                SectionList.ItemsSource = null;
                HardwareText.Text = string.Empty;
                DiffSummary.Text = string.Empty;
                return;
            }

            HardwareText.Text = profile.Hardware;

            foreach (var timing in profile.AllTimings)
            {
                string current;
                timing.Current = _live.TryGetValue(timing.Name, out current)
                    ? current
                    : LiveSnapshot.Missing;
            }

            // Rebuilt rather than refreshed so every row picks up its new Current value.
            SectionList.ItemsSource = null;
            SectionList.ItemsSource = profile.Sections;

            var comparable = profile.AllTimings
                .Where(t => !string.IsNullOrEmpty(t.Current) && t.Current != LiveSnapshot.Missing)
                .ToList();
            int different = comparable.Count(t => t.IsDifferent);

            DiffSummary.Text = comparable.Count == 0
                ? string.Empty
                : string.Format("{0} / {1} different", different, comparable.Count);
        }
    }
}
