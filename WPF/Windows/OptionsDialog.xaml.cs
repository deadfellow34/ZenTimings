using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ZenStates.Core;
using ZenTimings.Localization;
using static ZenTimings.AppSettings;

namespace ZenTimings.Windows
{
    /// <summary>
    /// Interaction logic for OptionsDialog.xaml
    /// </summary>
    public partial class OptionsDialog : ThemedAdonisWindow
    {
        //private const string Caption = "Disabling auto-refresh might lead to inaccurate voltages and frequencies on first launch";
        internal readonly AppSettings appSettings = AppSettings.Instance;
        internal readonly SystemInfo _systemInfo = CpuSingleton.Instance.systemInfo;
        private readonly DispatcherTimer timerInstance;
        private DispatcherTimer notificationTimer;
        private Theme _Theme;
        private readonly bool _AdvancedMode;
        private readonly ImpedanceTableSource _ImpedanceTableSource;
        private readonly int _CornerRadius;

        public OptionsDialog(DispatcherTimer timer)
        {
            timerInstance = timer;
            _Theme = appSettings.AppTheme;
            _AdvancedMode = appSettings.AdvancedMode;
            _ImpedanceTableSource = appSettings.ImpedanceTableSrc;
            _CornerRadius = appSettings.CornerRadius;

            InitializeComponent();

            checkBoxAutoRefresh.IsChecked = appSettings.AutoRefresh;
            checkBoxAutoRefresh.IsEnabled = appSettings.AdvancedMode;
            checkBoxAdvancedMode.IsChecked = appSettings.AdvancedMode;
            checkBoxCheckUpdate.IsChecked = appSettings.CheckForUpdates;
            checkBoxSavePosition.IsChecked = appSettings.SaveWindowPosition;
            checkBoxMinimizeToTray.IsChecked = appSettings.MinimizeToTray;
            checkBoxSingleInstance.IsChecked = appSettings.SingleInstance;
            comboBoxCornerRadius.SelectedIndex = appSettings?.CornerRadius ?? 0;
            //checkBoxAutoUninstallDriver.IsChecked = appSettings.AutoUninstallDriver;
            numericUpDownRefreshInterval.IsEnabled = appSettings.AutoRefresh && appSettings.AdvancedMode;
            numericUpDownRefreshInterval.Text = appSettings.AutoRefreshInterval.ToString();
            msText.IsEnabled = numericUpDownRefreshInterval.IsEnabled;
            comboBoxTheme.SelectedIndex = IndexOfTheme(_Theme);
            comboBoxScreenshot.SelectedIndex = (int)appSettings.ScreenshotMode;
            comboBoxImpedanceSource.SelectedIndex = (int)appSettings.ImpedanceTableSrc;
            textBoxScreenshotPath.Text = appSettings.ScreenshotSaveLocation;

            comboBoxLanguage.SelectedIndex = (int)appSettings.Language;
            checkBoxScreenshotHotkey.IsChecked = appSettings.ScreenshotHotkey;
            checkBoxTrayLiveIcon.IsChecked = appSettings.TrayLiveIcon;
            comboBoxTrayColor.SelectedIndex = (int)appSettings.TrayIconColor;
            checkBoxStartWithWindows.IsChecked = StartupRegistration.IsEnabled();
        }

        /// <summary>
        /// Combo box position for a stored theme. The combo box lists
        /// <see cref="AppSettings.SelectableThemes"/> in order; Charcoal is not offered but older
        /// settings files contain it, and it renders as Black, so it selects Black here.
        /// </summary>
        private static int IndexOfTheme(Theme theme)
        {
            int index = Array.IndexOf(AppSettings.SelectableThemes, theme);
            if (index >= 0)
                return index;

            index = Array.IndexOf(AppSettings.SelectableThemes, Theme.Black);
            return index >= 0 ? index : 0;
        }

        private static Theme ThemeAt(int index)
        {
            var themes = AppSettings.SelectableThemes;
            if (index < 0 || index >= themes.Length)
                return Theme.DarkMintGradient;

            return themes[index];
        }

        private void CheckBoxAutoRefresh_Click(object sender, RoutedEventArgs e)
        {
            numericUpDownRefreshInterval.IsEnabled = (bool)checkBoxAutoRefresh.IsChecked;
            msText.IsEnabled = numericUpDownRefreshInterval.IsEnabled;
        }

        private void CheckBoxAdvancedMode_Click(object sender, RoutedEventArgs e)
        {
            checkBoxAutoRefresh.IsEnabled = (bool)checkBoxAdvancedMode.IsChecked;
            numericUpDownRefreshInterval.IsEnabled =
                (bool)checkBoxAutoRefresh.IsChecked && checkBoxAutoRefresh.IsEnabled;
            msText.IsEnabled = numericUpDownRefreshInterval.IsEnabled;
        }

        private void ButtonSettingsApply_Click(object sender, RoutedEventArgs e)
        {
            appSettings.AutoRefresh = (bool)checkBoxAutoRefresh.IsChecked;
            appSettings.AutoRefreshInterval = Convert.ToInt32(numericUpDownRefreshInterval.Text);
            appSettings.AdvancedMode = (bool)checkBoxAdvancedMode.IsChecked;
            appSettings.CheckForUpdates = (bool)checkBoxCheckUpdate.IsChecked;
            appSettings.SaveWindowPosition = (bool)checkBoxSavePosition.IsChecked;
            appSettings.MinimizeToTray = (bool)checkBoxMinimizeToTray.IsChecked;
            appSettings.SingleInstance = (bool)checkBoxSingleInstance.IsChecked;
            appSettings.CornerRadius = comboBoxCornerRadius.SelectedIndex;
            //appSettings.AutoUninstallDriver = (bool)checkBoxAutoUninstallDriver.IsChecked;
            appSettings.ScreenshotMode = (ScreenshotType)comboBoxScreenshot.SelectedIndex;
            appSettings.ScreenshotSaveLocation = textBoxScreenshotPath.Text.Trim();
            appSettings.ImpedanceTableSrc = (ImpedanceTableSource)comboBoxImpedanceSource.SelectedIndex;

            var previousLanguage = appSettings.Language;
            appSettings.Language = (AppLanguage)Math.Max(0, comboBoxLanguage.SelectedIndex);
            appSettings.ScreenshotHotkey = (bool)checkBoxScreenshotHotkey.IsChecked;
            appSettings.TrayLiveIcon = (bool)checkBoxTrayLiveIcon.IsChecked;
            appSettings.TrayIconColor = (TrayColor)Math.Max(0, comboBoxTrayColor.SelectedIndex);

            // Take effect now rather than on the next launch.
            (Application.Current.MainWindow as MainWindow)?.ApplyHotKeySetting();
            (Application.Current.MainWindow as MainWindow)?.ApplyTraySetting();

            bool startWithWindows = (bool)checkBoxStartWithWindows.IsChecked;
            if (startWithWindows != StartupRegistration.IsEnabled())
            {
                if (StartupRegistration.Apply(startWithWindows))
                {
                    appSettings.StartWithWindows = startWithWindows;
                }
                else
                {
                    // The registry is the source of truth; reflect the failure instead of lying.
                    checkBoxStartWithWindows.IsChecked = StartupRegistration.IsEnabled();
                    appSettings.StartWithWindows = StartupRegistration.IsEnabled();
                }
            }

            appSettings.Save();

            timerInstance.Interval = TimeSpan.FromMilliseconds(appSettings.AutoRefreshInterval);
            _Theme = appSettings.AppTheme;

            if (notificationTimer != null)
                if (notificationTimer.IsEnabled)
                    notificationTimer.Stop();

            notificationTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(6000)
            };

            notificationTimer.Tick += new EventHandler((s, x) =>
            {
                notificationTimer.Stop();
                OptionsPopup.IsOpen = false;
            });

            notificationTimer.Start();

            if (checkBoxAutoRefresh.IsEnabled)
            {
                if (appSettings.AutoRefresh && !timerInstance.IsEnabled)
                    timerInstance.Start();
                else if (!appSettings.AutoRefresh && timerInstance.IsEnabled)
                    timerInstance.Stop();
            }

            if (_AdvancedMode != appSettings.AdvancedMode ||
                _ImpedanceTableSource != appSettings.ImpedanceTableSrc ||
                _CornerRadius != appSettings.CornerRadius ||
                previousLanguage != appSettings.Language)
            {
                buttonSettingsRestart.Visibility = Visibility.Visible;
                appSettings.Save();
                OptionsPopupText.Text = "Some settings will be applied on next launch.";
            }

            OptionsPopup.Width = OptionWindowContent.ActualWidth;
            OptionsPopup.IsOpen = true;
        }

        private void ComboBoxTheme_CheckedChanged(object sender, RoutedEventArgs e)
        {
            //appSettings.DarkMode = (bool)comboBoxTheme.IsChecked;
            //appSettings.ChangeTheme();
        }

        private void ButtonSettingsCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ButtonSettingsRestart_Click(object sender, RoutedEventArgs e)
        {
            ProcessStartInfo Info = new ProcessStartInfo
            {
                Arguments = "/C choice /C Y /N /D Y /T 1 & START \"\" \"" + Assembly.GetEntryAssembly().Location + "\"",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                FileName = "cmd.exe"
            };
            Process.Start(Info);
            Application.Current.Shutdown();
        }

        private void OptionsPopup_MouseDown(object sender, MouseButtonEventArgs e)
        {
            OptionsPopup.IsOpen = false;
        }

        private void OptionsWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Restore theme on close if not saved
            if (appSettings.AppTheme != _Theme)
            {
                appSettings.AppTheme = _Theme;
                appSettings.ApplyTheme();
            }
        }

        private void ComboBoxTheme_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            appSettings.AppTheme = ThemeAt(comboBoxTheme.SelectedIndex);
            appSettings.ApplyTheme();
        }

        private void ButtonBrowseScreenshotPath_Click(object sender, RoutedEventArgs e)
        {
            var folderDialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select folder to save screenshots"
            };

            // Set initial path to current screenshot path if valid
            string currentPath = textBoxScreenshotPath.Text.Trim();
            if (!string.IsNullOrEmpty(currentPath) && System.IO.Directory.Exists(currentPath))
            {
                folderDialog.SelectedPath = currentPath;
            }

            if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                textBoxScreenshotPath.Text = folderDialog.SelectedPath;
            }
        }
    }
}