using AdonisUI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using ZenTimings.Localization;

namespace ZenTimings
{
    /// <summary>
    /// "Start with Windows" via the per-user Run key. HKCU only - no elevation, no service,
    /// and the user can always remove it from Task Manager's startup tab.
    /// </summary>
    internal static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ZenTimings";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    return key?.GetValue(ValueName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool Apply(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null)
                        return false;

                    if (enabled)
                    {
                        string path = Assembly.GetEntryAssembly()?.Location;
                        if (string.IsNullOrEmpty(path))
                            return false;

                        key.SetValue(ValueName, "\"" + path + "\"");
                    }
                    else if (key.GetValue(ValueName) != null)
                    {
                        key.DeleteValue(ValueName, false);
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    [Serializable]
    public sealed class AppSettings : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public const int VersionMajor = 1;
        public const int VersionMinor = 13;

        private static readonly string Filename = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.xml");
        public const string AGESA_UNKNOWN = "Unknown";

        private static AppSettings _instance = null;

        private AppSettings() { }

        public static AppSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new AppSettings().Load();
                }

                return _instance;
            }
        }

        public enum Theme : int
        {
            Light,
            Dark,
            DarkMint,
            DarkMintGradient,
            AsusRog,
            Dracula,
            RetroWave,
            BurntOrange,
            Charcoal,
            Black,
        }

        public enum ScreenshotType : int
        {
            Window,
            Desktop,
        }

        public enum ImpedanceTableSource : int
        {
            AOD,
            APOB
        }

        /// <summary>
        /// Ink for the live tray icon. White is unreadable on a light taskbar and the accent
        /// colours are unreadable on a dark one, so this is a user choice rather than a constant.
        /// Order must match the Options combo box.
        /// </summary>
        public enum TrayColor : int
        {
            White,
            Green,
            Cyan,
            Yellow,
            Orange,
            Red,
            Black,
        }

        public AppSettings Create(bool save = true)
        {
            if (save) Save();

            return this;
        }

        public AppSettings Reset() => Create();

        public AppSettings Load()
        {
            try
            {
                if (File.Exists(Filename))
                {
                    return XmlUtils.DeserializeFromXml<AppSettings>(Filename);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                MessageBox.Show(
                    "Invalid or outdated settings file!\nSettings will be reset to defaults and any custom settings will be lost.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            return Create();
        }

        public void Save()
        {
            try
            {
                if (!DriverHelper.IsPawnIoInstalled)
                    return;

                Version = new Version(VersionMajor, VersionMinor).ToString();

                string xmlContent = XmlUtils.SerializeToXml<AppSettings>(this);
                File.WriteAllText(Filename, xmlContent);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                AdonisUI.Controls.MessageBox.Show(
                    "Could not save settings to file!",
                    "Error",
                    AdonisUI.Controls.MessageBoxButton.OK,
                    AdonisUI.Controls.MessageBoxImage.Error);
            }
        }

        private static Uri ThemeUri(string name)
        {
            return new Uri($"pack://application:,,,/ZenTimings;component/Themes/{name}.xaml", UriKind.Absolute);
        }

        // Keyed by enum value, not by position. The theme list, the enum and the Options combo box
        // used to be three parallel lists of different lengths: Charcoal.xaml is not compiled in, so
        // every entry after it was shifted and Theme.Black indexed one past the end of the array.
        private static readonly Dictionary<Theme, Uri> ThemeUris = new Dictionary<Theme, Uri>
        {
            { Theme.Light,            ThemeUri("Light") },
            { Theme.Dark,             ThemeUri("Dark") },
            { Theme.DarkMint,         ThemeUri("DarkMint") },
            { Theme.DarkMintGradient, ThemeUri("DarkMintGradient") },
            { Theme.AsusRog,          ThemeUri("AsusRog") },
            { Theme.Dracula,          ThemeUri("Dracula") },
            { Theme.RetroWave,        ThemeUri("RetroWave") },
            { Theme.BurntOrange,      ThemeUri("BurntOrange") },
            // Charcoal.xaml exists in the repo but is not part of the build, so it maps to Black.
            // Settings files written by older builds do contain "Charcoal" - they must keep working.
            { Theme.Charcoal,         ThemeUri("Black") },
            { Theme.Black,            ThemeUri("Black") },
        };

        public void ApplyTheme()
        {
            Uri uri;
            if (!ThemeUris.TryGetValue(AppTheme, out uri))
            {
                AppTheme = Theme.DarkMintGradient;
                uri = ThemeUris[AppTheme];
            }

            ResourceLocator.SetColorScheme(Application.Current.Resources, uri);
            try
            {
                ThemedAdonisWindow.RefreshAllOpenWindows();
            }
            catch { }
        }

        /// <summary>The themes offered in Options, in combo box order.</summary>
        public static readonly Theme[] SelectableThemes =
        {
            Theme.Light,
            Theme.Dark,
            Theme.DarkMint,
            Theme.DarkMintGradient,
            Theme.AsusRog,
            Theme.Dracula,
            Theme.RetroWave,
            Theme.BurntOrange,
            Theme.Black,
        };

        public string Version { get; set; } = new Version(VersionMajor, VersionMinor).ToString();

        private bool _autoRefresh = true;
        public bool AutoRefresh
        {
            get => _autoRefresh;
            set
            {
                if (_autoRefresh != value)
                {
                    _autoRefresh = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoRefresh)));
                }
            }
        }

        private int _autoRefreshInterval = 2000;
        // A floor only, because XML deserialisation writes through this setter and a stored value
        // is not typed input. Negative throws out of DispatcherTimer.Interval in the MainWindow
        // constructor, whose only recovery is to exit and write the same value back; zero fires on
        // every dispatcher idle and starts an SMU/SMBus thread per tick. Above the floor there is
        // nothing to protect: every consumer takes any int, and a long interval is how a user keeps
        // ZenTimings off the SMU and the SMBus.
        public int AutoRefreshInterval
        {
            get => _autoRefreshInterval;
            set
            {
                int interval = Math.Max(value, 100);
                if (_autoRefreshInterval != interval)
                {
                    _autoRefreshInterval = interval;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoRefreshInterval)));
                }
            }
        }
        public bool AdvancedMode { get; set; } = true;
        public Theme AppTheme { get; set; } = Theme.DarkMintGradient;
        public ScreenshotType ScreenshotMode { get; set; } = ScreenshotType.Window;
        public string ScreenshotSaveLocation { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Screenshots");
        /// <summary>
        /// Kept only so old settings files still deserialise. The updater is not wired up in this
        /// fork - see SplashWindow.Start - so this value has no effect.
        /// </summary>
        public bool CheckForUpdates { get; set; } = false;
        public string UpdaterSkippedVersion { get; set; } = "";
        public string DriverUpdateLastSkippedVersion { get; set; } = "";
        public string UpdaterRemindLaterAt { get; set; } = "";
        public bool MinimizeToTray { get; set; }
        public bool SaveWindowPosition { get; set; } = true;
        public bool AutoUninstallDriver { get; set; } = true;
        public double WindowLeft { get; set; } = -1;
        public double WindowTop { get; set; } = -1;
        public double SysInfoWindowLeft { get; set; } = -1;
        public double SysInfoWindowTop { get; set; } = -1;
        public double SysInfoWindowWidth { get; set; }
        public double SysInfoWindowHeight { get; set; }
        public double TelemetryWindowLeft { get; set; } = -1;
        public double TelemetryWindowTop { get; set; } = -1;
        public double TelemetryWindowWidth { get; set; }
        public double TelemetryWindowHeight { get; set; }
        public string NotifiedChangelog { get; set; } = "";
        public bool SingleInstance { get; set; } = true;
        public bool AutoOpenTelemetry { get; set; } = false;
        public bool FirstStart { get; set; } = true;
        public int CornerRadius { get; set; } = 0;
        public ImpedanceTableSource ImpedanceTableSrc { get; set; } = ImpedanceTableSource.APOB;

        // --- Added in this fork ---

        /// <summary>UI language. Applied at startup; changing it needs a restart.</summary>
        public AppLanguage Language { get; set; } = AppLanguage.English;

        /// <summary>Ctrl+Alt+S captures a screenshot and names it from the current memory config.</summary>
        public bool ScreenshotHotkey { get; set; } = true;

        /// <summary>Draw the hottest DIMM temperature onto the tray icon.</summary>
        public bool TrayLiveIcon { get; set; }

        /// <summary>Ink used for <see cref="TrayLiveIcon"/>.</summary>
        public TrayColor TrayIconColor { get; set; } = TrayColor.White;

        public bool StartWithWindows { get; set; }

        // --- Optional readouts on the system-info panel ---
        // Each one only ever hides a row that the hardware is actually reporting; a reading that
        // is unavailable stays hidden either way. Default on, so existing settings files keep the
        // panel exactly as it was.

        /// <summary>Show the CPU Die (Tctl/Tdie) temperature.</summary>
        public bool ShowCpuTemperature { get; set; } = true;

        /// <summary>Show the I/O die hotspot temperature. Zen 5 power tables only.</summary>
        public bool ShowIodTemperature { get; set; } = true;

        /// <summary>Show the average DIMM temperature.</summary>
        public bool ShowMemoryTemperature { get; set; } = true;

        /// <summary>Show the total on-module PMIC power.</summary>
        public bool ShowDimmPower { get; set; } = true;

        /// <summary>Show the WHEA error count. Off also stops the event-log polling.</summary>
        public bool ShowWheaCount { get; set; } = true;
    }
}
