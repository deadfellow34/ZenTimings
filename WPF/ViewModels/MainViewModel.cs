using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml.Serialization;
using ZenStates.Core;
using ZenStates.Core.DRAM;
using ZenTimings.Plugin;

namespace ZenTimings.ViewModels
{
    /// <summary>One reading pushed in from the refresh thread.</summary>
    public class DimmTemperatureSample
    {
        public string Label { get; set; }
        public string Slot { get; set; }
        public double Celsius { get; set; }
    }

    /// <summary>
    /// Live temperature of one populated DIMM, kept alive across refreshes so it can accumulate
    /// min/max/average. Replacing the objects every tick (as this used to) threw those statistics
    /// away and rebuilt the whole ItemsControl.
    /// </summary>
    public class DimmTemperature : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private double _current;
        private double _min = double.MaxValue;
        private double _max = double.MinValue;
        private double _sum;
        private int _count;

        public DimmTemperature(string label, string slot)
        {
            Label = label;
            Slot = slot;
        }

        public string Label { get; private set; }   // e.g. "DIMM0"

        private string _slot;
        public string Slot                          // e.g. "A1"; shown in the tooltip
        {
            get { return _slot; }
            set { _slot = value; OnPropertyChanged("Slot"); OnPropertyChanged("ToolTipText"); }
        }

        public double Celsius
        {
            get { return _current; }
        }

        public string TemperatureText
        {
            get { return _count == 0 ? "N/A" : _current.ToString("F1") + " °C"; }
        }

        /// <summary>min / max / average since launch, without the slot prefix.</summary>
        public string StatsText
        {
            get
            {
                if (_count == 0)
                    return string.Empty;

                return string.Format(
                    "min {0:F1} / max {1:F1} / avg {2:F1} °C",
                    _min, _max, _sum / _count);
            }
        }

        public string ToolTipText
        {
            get
            {
                if (_count == 0)
                    return Slot;

                return string.IsNullOrEmpty(Slot) ? StatsText : Slot + "\n" + StatsText;
            }
        }

        public void Update(double celsius)
        {
            _current = celsius;

            if (celsius < _min) _min = celsius;
            if (celsius > _max) _max = celsius;

            _sum += celsius;
            _count++;

            OnPropertyChanged("Celsius");
            OnPropertyChanged("TemperatureText");
            OnPropertyChanged("ToolTipText");
        }

        public void ResetStats()
        {
            _min = _count == 0 ? double.MaxValue : _current;
            _max = _count == 0 ? double.MinValue : _current;
            _sum = _count == 0 ? 0 : _current;
            _count = _count == 0 ? 0 : 1;

            OnPropertyChanged("ToolTipText");
        }

        private void OnPropertyChanged(string name)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(name));
        }
    }

    public class MainViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            });
        }

        private static string AGESA_SEARCHING = "Searching for AGESA version...";

        private readonly string SmuVersion;

        private BaseDramTimings _timings;
        public BaseDramTimings Timings
        {
            get => _timings;
            set
            {
                _timings = value;
                MemoryFrequency = value.Frequency;
                OnPropertyChanged();
            }
        }
        public AppSettings Settings { get; }
        public List<IPlugin> Plugins { get; }

        public string CpuName { get; } = string.Empty;

        private string _motherboardInfo = string.Empty;
        public string MotherboardInfo
        {
            get => _motherboardInfo;
            set { _motherboardInfo = value; OnPropertyChanged(); }
        }

        private string _agesaVersion = AGESA_SEARCHING;
        public string AgesaVersion
        {
            get => _agesaVersion;
            set
            {
                if (string.IsNullOrEmpty(value) || value == AppSettings.AGESA_UNKNOWN || value == AGESA_SEARCHING)
                {
                    MotherboardInfo = $@"{CpuSingleton.Instance.systemInfo.MbName} | BIOS {CpuSingleton.Instance.systemInfo.BiosVersion} ({SmuVersion})";
                    _agesaVersion = value == AGESA_SEARCHING ? AGESA_SEARCHING : null;
                }
                else
                {
                    MotherboardInfo = $@"{CpuSingleton.Instance.systemInfo.MbName} | BIOS {CpuSingleton.Instance.systemInfo.BiosVersion}";
                    _agesaVersion = $"AGESA {value} (SMU {SmuVersion})";
                }
                IsAgesaVersionVisible = !string.IsNullOrEmpty(_agesaVersion);
                OnPropertyChanged();
            }
        }

        private bool _isAgesaVersionVisible;
        public bool IsAgesaVersionVisible
        {
            get => _isAgesaVersionVisible;
            set { _isAgesaVersionVisible = value; OnPropertyChanged(); }
        }

        public bool IsSearchingForAgesaVersion => _agesaVersion == AGESA_SEARCHING;

        public Capacity TotalCapacity { get; }

        private float _memoryFrequency;
        public float MemoryFrequency
        {
            get => _memoryFrequency;
            set
            {
                _memoryFrequency = value;
                MemoryFrequencyString = $"{Math.Floor(MemoryFrequency)} MT/s";
                OnPropertyChanged();
            }
        }

        private string _memoryFrequencyString;
        public string MemoryFrequencyString
        {
            get => _memoryFrequencyString;
            set { _memoryFrequencyString = value; OnPropertyChanged(); }
        }

        private string _cpuTemperatureText = "N/A";
        public string CpuTemperatureText
        {
            get => _cpuTemperatureText;
            set { _cpuTemperatureText = value; OnPropertyChanged(); }
        }

        private double _cpuTemperature;
        private double _cpuTemperatureMin = double.MaxValue;
        private double _cpuTemperatureMax = double.MinValue;
        private double _cpuTemperatureSum;
        private int _cpuTemperatureCount;

        /// <summary>Live CPU die temperature in °C (0 when unavailable).</summary>
        public double CpuTemperature => _cpuTemperature;

        public string CpuTemperatureToolTip
        {
            get
            {
                if (_cpuTemperatureCount == 0)
                    return null;

                return string.Format("min {0:F1} / max {1:F1} / avg {2:F1} °C",
                    _cpuTemperatureMin, _cpuTemperatureMax, _cpuTemperatureSum / _cpuTemperatureCount);
            }
        }

        /// <summary>Called from the refresh thread; the statistics are kept on the dispatcher.</summary>
        public void UpdateCpuTemperature(double celsius)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                _cpuTemperature = celsius;
                CpuTemperatureText = $"{celsius:F1} °C";

                if (celsius < _cpuTemperatureMin) _cpuTemperatureMin = celsius;
                if (celsius > _cpuTemperatureMax) _cpuTemperatureMax = celsius;
                _cpuTemperatureSum += celsius;
                _cpuTemperatureCount++;

                OnPropertyChanged(nameof(CpuTemperature));
                OnPropertyChanged(nameof(CpuTemperatureToolTip));
            });
        }

        private readonly ObservableCollection<DimmTemperature> _memoryTemperatures =
            new ObservableCollection<DimmTemperature>();

        /// <summary>
        /// Stable collection of per-DIMM readouts. Entries are updated in place so each one keeps
        /// its own min/max/average.
        /// </summary>
        public ObservableCollection<DimmTemperature> MemoryTemperatures => _memoryTemperatures;

        /// <summary>
        /// Merges a fresh set of readings into the collection. Safe to call from the refresh
        /// thread - the collection itself is only touched on the dispatcher.
        /// </summary>
        public void UpdateMemoryTemperatures(IList<DimmTemperatureSample> samples)
        {
            if (samples == null)
                return;

            Application.Current?.Dispatcher.Invoke(() =>
            {
                for (int i = 0; i < samples.Count; i++)
                {
                    var sample = samples[i];
                    if (sample == null)
                        continue;

                    DimmTemperature target = null;
                    for (int j = 0; j < _memoryTemperatures.Count; j++)
                    {
                        if (string.Equals(_memoryTemperatures[j].Label, sample.Label, StringComparison.Ordinal))
                        {
                            target = _memoryTemperatures[j];
                            break;
                        }
                    }

                    if (target == null)
                    {
                        target = new DimmTemperature(sample.Label, sample.Slot);
                        _memoryTemperatures.Add(target);
                    }
                    else if (!string.Equals(target.Slot, sample.Slot, StringComparison.Ordinal))
                    {
                        target.Slot = sample.Slot;
                    }

                    target.Update(sample.Celsius);
                }

                // A DIMM that stopped reporting (hot-removed sensor, SMBus hiccup) is dropped so the
                // row does not sit there showing a frozen value.
                for (int i = _memoryTemperatures.Count - 1; i >= 0; i--)
                {
                    string label = _memoryTemperatures[i].Label;
                    bool stillPresent = false;
                    for (int j = 0; j < samples.Count; j++)
                    {
                        if (samples[j] != null && string.Equals(samples[j].Label, label, StringComparison.Ordinal))
                        {
                            stillPresent = true;
                            break;
                        }
                    }

                    if (!stillPresent)
                        _memoryTemperatures.RemoveAt(i);
                }

                double hottest = 0;
                double sum = 0;
                int reporting = 0;
                foreach (var dimm in _memoryTemperatures)
                {
                    if (dimm.Celsius > hottest) hottest = dimm.Celsius;
                    sum += dimm.Celsius;
                    reporting++;
                }

                _hottestDimmTemperature = (float)hottest;

                double average = reporting > 0 ? sum / reporting : 0;
                _averageDimmTemperature = (float)average;

                OnPropertyChanged(nameof(HottestDimmTemperature));
                OnPropertyChanged(nameof(MemoryTemperatureText));
                OnPropertyChanged(nameof(MemoryTemperatureToolTip));
                OnDimmReadoutChanged();
            });
        }

        // --- WHEA (machine-check) error count, see WheaMonitor ---

        private string _wheaText = "0";
        public string WheaText
        {
            get => _wheaText;
            private set { _wheaText = value; OnPropertyChanged(); }
        }

        private string _wheaToolTip;
        public string WheaToolTip
        {
            get => _wheaToolTip;
            private set { _wheaToolTip = value; OnPropertyChanged(); }
        }

        private bool _isWheaAvailable;

        /// <summary>
        /// Shown only when the monitor has something to report *and* the user has left the readout
        /// switched on in Options. Same split as the temperature and DIMM-power rows below.
        /// </summary>
        public bool IsWheaVisible => _isWheaAvailable && (Settings?.ShowWheaCount ?? true);

        /// <summary>Drives the colour: anything above zero should stand out from the other readouts.</summary>
        private bool _hasWheaErrors;
        public bool HasWheaErrors
        {
            get => _hasWheaErrors;
            private set { _hasWheaErrors = value; OnPropertyChanged(); }
        }

        /// <summary>Pushes a fresh WheaMonitor reading in. Safe to call from a background thread.</summary>
        public void UpdateWhea(WheaMonitor monitor)
        {
            if (monitor == null)
                return;

            Application.Current?.Dispatcher.Invoke(() =>
            {
                _isWheaAvailable = monitor.IsAvailable;
                OnPropertyChanged(nameof(IsWheaVisible));
                OnPropertyChanged(nameof(IsAnyReadoutVisible));
                WheaText = monitor.Total.ToString();
                HasWheaErrors = monitor.Total > 0;
                WheaToolTip = monitor.BuildToolTip(Localization.Loc.Language == Localization.AppLanguage.Turkish);
            });
        }

        private volatile float _averageDimmTemperature;

        /// <summary>
        /// One number for the whole kit: the mean of the populated DIMMs. The main window used to
        /// draw a value per module, which pushed the readout row - and with it the window - wider
        /// on every extra DIMM. The per-module detail moved into the tooltip.
        /// </summary>
        public string MemoryTemperatureText
        {
            get
            {
                return _memoryTemperatures.Count == 0
                    ? "N/A"
                    : _averageDimmTemperature.ToString("F1") + " °C";
            }
        }

        /// <summary>One line per DIMM: slot, current reading, and its own min/max/average.</summary>
        public string MemoryTemperatureToolTip
        {
            get
            {
                if (_memoryTemperatures.Count == 0)
                    return null;

                var text = new StringBuilder();
                foreach (var dimm in _memoryTemperatures)
                {
                    if (text.Length > 0)
                        text.Append('\n');

                    string name = string.IsNullOrEmpty(dimm.Slot) ? dimm.Label : dimm.Slot;
                    text.Append(name).Append("  ").Append(dimm.TemperatureText);

                    if (!string.IsNullOrEmpty(dimm.StatsText))
                        text.Append("   ").Append(dimm.StatsText);
                }

                return text.ToString();
            }
        }

        private volatile float _hottestDimmTemperature;

        /// <summary>
        /// Hottest DIMM right now, or 0 when nothing is reporting.
        /// Computed on the dispatcher while the collection is being updated and cached here: the
        /// refresh thread reads this, and PowerCfgTimer_Tick starts a fresh thread per tick, so two
        /// refreshes can overlap - enumerating the collection from one of them while the other
        /// mutates it would throw.
        /// </summary>
        public double HottestDimmTemperature => _hottestDimmTemperature;

        // --- Total DIMM power reported by the on-module PMICs ---

        private string _memoryPowerText = "N/A";
        public string MemoryPowerText
        {
            get => _memoryPowerText;
            set
            {
                if (_memoryPowerText == value)
                    return;

                _memoryPowerText = value;
                OnPropertyChanged();
                OnDimmReadoutChanged();
            }
        }

        /// <summary>
        /// The PMICs are reporting power. Kept apart from <see cref="IsMemoryPowerVisible"/> so the
        /// CSV log still records the wattage when the row is only hidden from the window.
        /// </summary>
        private bool _isMemoryPowerAvailable;
        public bool IsMemoryPowerAvailable
        {
            get => _isMemoryPowerAvailable;
            set
            {
                if (_isMemoryPowerAvailable == value)
                    return;

                _isMemoryPowerAvailable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsMemoryPowerVisible));
                OnPropertyChanged(nameof(IsAnyReadoutVisible));
                OnDimmReadoutChanged();
            }
        }

        public bool IsMemoryPowerVisible => _isMemoryPowerAvailable && Settings.ShowDimmPower;

        // --- tCCD_L family ---

        private uint _tccdlValue;
        public uint TccdlValue
        {
            get => _tccdlValue;
            set
            {
                _tccdlValue = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TccdlText));
            }
        }

        private uint _tccdlWr2Value;
        public uint TccdlWr2Value
        {
            get => _tccdlWr2Value;
            set
            {
                _tccdlWr2Value = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TccdlWr2Text));
            }
        }

        // Set while the values come from the register fallback rather than a located APOB run;
        // the "!" tells the user the readout is unverified there.
        private bool _tccdlFromRegister;
        public bool TccdlFromRegister
        {
            get => _tccdlFromRegister;
            set
            {
                _tccdlFromRegister = value;
                OnPropertyChanged(nameof(TccdlText));
                OnPropertyChanged(nameof(TccdlWrText));
                OnPropertyChanged(nameof(TccdlWr2Text));
            }
        }

        private string TccdlMark => _tccdlFromRegister ? " !" : "";

        public string TccdlText => _tccdlValue > 0 ? _tccdlValue + TccdlMark : "N/A";

        public string TccdlWr2Text => _tccdlWr2Value > 0 ? _tccdlWr2Value + TccdlMark : "N/A";

        private uint _tccdlWrValue;
        public uint TccdlWrValue
        {
            get => _tccdlWrValue;
            set
            {
                _tccdlWrValue = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TccdlWrText));
            }
        }

        public string TccdlWrText => _tccdlWrValue > 0 ? _tccdlWrValue + TccdlMark : "N/A";

        private bool _isTccdlVisible;
        public bool IsTccdlVisible
        {
            get => _isTccdlVisible;
            set { _isTccdlVisible = value; OnPropertyChanged(); }
        }

        private bool _isCpuTemperatureAvailable;
        public bool IsCpuTemperatureAvailable
        {
            get => _isCpuTemperatureAvailable;
            set
            {
                // Only on a change: the refresh thread writes this every tick and each
                // notification is a synchronous marshal to the UI thread.
                if (_isCpuTemperatureAvailable == value)
                    return;

                _isCpuTemperatureAvailable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCpuTemperatureVisible));
                OnPropertyChanged(nameof(IsAnyReadoutVisible));
            }
        }

        public bool IsCpuTemperatureVisible => _isCpuTemperatureAvailable && Settings.ShowCpuTemperature;

        private string _iodTemperatureText = "N/A";
        public string IodTemperatureText
        {
            get => _iodTemperatureText;
            set { _iodTemperatureText = value; OnPropertyChanged(); }
        }

        private string _iodTemperatureToolTip;
        public string IodTemperatureToolTip
        {
            get => _iodTemperatureToolTip;
            set { _iodTemperatureToolTip = value; OnPropertyChanged(); }
        }

        private bool _isIodTemperatureAvailable;
        public bool IsIodTemperatureAvailable
        {
            get => _isIodTemperatureAvailable;
            set
            {
                // Only on a change: the refresh thread writes this every tick, and each
                // notification is a synchronous marshal to the UI thread. On a platform without
                // the reading - every Zen 4 board - that would be three of them per tick forever.
                if (_isIodTemperatureAvailable == value)
                    return;

                _isIodTemperatureAvailable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsIodTemperatureVisible));
                OnPropertyChanged(nameof(IsAnyReadoutVisible));
            }
        }

        public bool IsIodTemperatureVisible => _isIodTemperatureAvailable && Settings.ShowIodTemperature;

        /// <summary>
        /// Both figures, average then hotspot: a single number under an "IOD" label reads as the
        /// wrong one whichever is chosen, because monitoring tools list the two separately.
        /// </summary>
        private float _iodHotspotMin = float.MaxValue;
        private float _iodHotspotMax = float.MinValue;
        private double _iodHotspotSum;
        private int _iodHotspotCount;

        public void UpdateIodTemperature(float average, float hotspot)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                // Current culture, like every other readout on the row - an invariant decimal point
                // here would sit next to comma-separated neighbours on a Turkish desktop.
                IodTemperatureText = string.Format("{0:F1} / {1:F1} °C", average, hotspot);

                if (hotspot < _iodHotspotMin) _iodHotspotMin = hotspot;
                if (hotspot > _iodHotspotMax) _iodHotspotMax = hotspot;
                _iodHotspotSum += hotspot;
                _iodHotspotCount++;

                // Same shape as the CPU and DIMM tooltips: the session's range, not just a
                // restatement of the two numbers already on screen.
                IodTemperatureToolTip = string.Format(
                    "I/O die {0:F1} °C, hotspot {1:F1} °C" + Environment.NewLine +
                    "Hotspot min {2:F1} / avg {3:F1} / max {4:F1} °C",
                    average, hotspot, _iodHotspotMin,
                    _iodHotspotSum / _iodHotspotCount, _iodHotspotMax);
            });
        }

        private bool _isMemoryTemperatureAvailable;
        public bool IsMemoryTemperatureAvailable
        {
            get => _isMemoryTemperatureAvailable;
            set
            {
                if (_isMemoryTemperatureAvailable == value)
                    return;

                _isMemoryTemperatureAvailable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsMemoryTemperatureVisible));
                OnPropertyChanged(nameof(IsAnyReadoutVisible));
                OnDimmReadoutChanged();
            }
        }

        public bool IsMemoryTemperatureVisible => _isMemoryTemperatureAvailable && Settings.ShowMemoryTemperature;

        /// <summary>
        /// Temperature and power in one entry, because they describe the same modules and the
        /// readouts row is what sets the window width. Whichever half is switched off or
        /// unavailable simply drops out.
        /// </summary>
        public string DimmReadoutText
        {
            get
            {
                bool temperature = IsMemoryTemperatureVisible;
                bool power = IsMemoryPowerVisible;

                if (temperature && power)
                    return MemoryTemperatureText + "  " + MemoryPowerText;
                if (temperature)
                    return MemoryTemperatureText;

                return power ? MemoryPowerText : string.Empty;
            }
        }

        public string DimmReadoutToolTip
        {
            get
            {
                string tip = IsMemoryTemperatureVisible ? MemoryTemperatureToolTip : null;
                if (IsMemoryPowerVisible)
                {
                    string power = Localization.Loc.T("Main.DimmPowerTip");
                    tip = string.IsNullOrEmpty(tip) ? power : tip + Environment.NewLine + power;
                }

                return tip;
            }
        }

        public bool IsDimmReadoutVisible => IsMemoryTemperatureVisible || IsMemoryPowerVisible;

        /// <summary>
        /// Reserved width for whichever halves are actually on. A single figure keyed to both of
        /// them would leave about 50px of gap in front of the WHEA label as soon as one is switched
        /// off - and the window is sized to its content, so that gap is window width.
        /// </summary>
        public double DimmReadoutMinWidth =>
            IsMemoryTemperatureVisible && IsMemoryPowerVisible ? 96 : 50;

        private void OnDimmReadoutChanged()
        {
            OnPropertyChanged(nameof(DimmReadoutText));
            OnPropertyChanged(nameof(DimmReadoutToolTip));
            OnPropertyChanged(nameof(IsDimmReadoutVisible));
            OnPropertyChanged(nameof(DimmReadoutMinWidth));
        }

        /// <summary>
        /// Collapses the whole readout row when nothing in it is left to show, so the panel does not
        /// keep an empty line's worth of padding. Covers DIMM power and WHEA too - they sit on the
        /// same row, and switching both temperatures off used to take them down with it.
        /// </summary>
        public bool IsAnyReadoutVisible =>
            IsCpuTemperatureVisible || IsIodTemperatureVisible || IsMemoryTemperatureVisible
            || IsMemoryPowerVisible || IsWheaVisible;

        /// <summary>
        /// Re-evaluates the optional readouts after the Options dialog writes new switches. The
        /// visibility getters read <see cref="Settings"/> directly, and a plain property on
        /// AppSettings raises nothing, so the change has to be announced here.
        /// </summary>
        public void RefreshReadoutVisibility()
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                OnPropertyChanged(nameof(IsCpuTemperatureVisible));
                OnPropertyChanged(nameof(IsIodTemperatureVisible));
                OnPropertyChanged(nameof(IsMemoryTemperatureVisible));
                OnPropertyChanged(nameof(IsMemoryPowerVisible));
                OnPropertyChanged(nameof(IsWheaVisible));
                OnPropertyChanged(nameof(IsAnyReadoutVisible));
                OnDimmReadoutChanged();
            });
        }

        private string _bclkString = "N/A";
        public string BclkString
        {
            get => _bclkString;
            set { _bclkString = value; OnPropertyChanged(); }
        }

        public MemType MemoryType { get; }
        public bool IsDimmTelemetryAvailable => Settings.AdvancedMode && MemoryType == MemType.DDR5;
        public bool ECC { get; set; }
        public PowerTable PowerTable { get; }
        public Cpu.CodeName CodeName { get; }
        public bool WMIPresent { get; }
        public bool IsMotherboardLogoVisible { get; }
        public string MotherboardLogoTooltip { get; }
        public bool IsRfcEnabled => Timings.RefreshMode == BankRefreshMode.NORMAL;
        public bool IsRfc2Enabled => Timings.RefreshMode != BankRefreshMode.NORMAL;
        public bool IsRfcsbEnabled => Timings.RefreshMode == BankRefreshMode.MIXED;

        // DDR4 doesn't have separate RFCsb, but we can still indicate if it's using normal refresh or FGR
        public bool IsDdr4RfcEnabled => (Timings as Ddr4Timings)?.RefreshMode == BankRefreshMode.NORMAL;
        public bool IsDdr4Rfc2Enabled => (Timings as Ddr4Timings)?.RefreshMode == BankRefreshMode.FGR && Timings.FGR == 2;
        public bool IsDdr4Rfc4Enabled => (Timings as Ddr4Timings)?.RefreshMode == BankRefreshMode.FGR && Timings.FGR == 4;

        public string CpuNameShortWithCores
        {
            get
            {
                string name = CpuName;
                var match = Regex.Match(
                    name,
                    @"\s+(?:\d+\s*-\s*Core\s+Processor|(?:with|w/)\s+Radeon)",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled
                );

                if (match.Success)
                {
                    name = name.Substring(0, match.Index).Trim();
                }

                string cores = $"({CpuSingleton.Instance.info.topology.cores}C/{CpuSingleton.Instance.info.topology.logicalCores}T)";
                return $"{name} {cores}";
            }
        }

        //private uint _selectedDctOffset = 0;
        //public uint SelectedDctOffset {
        //    get => _selectedDctOffset;
        //    set
        //    {
        //        _selectedDctOffset = value;
        //        if (_channelsApobData != null)
        //        {
        //            ApobData = _channelsApobData[_selectedDctOffset >> 20];
        //        }
        //    }
        //}

        //private readonly ApobData[] _channelsApobData;

        private ApobData _apobData;
        public ApobData ApobData
        {
            get => _apobData;
            set { _apobData = value; OnPropertyChanged(); }
        }

        private ApobData _apobExtendedData;
        public ApobData ApobExtendedData
        {
            get => _apobExtendedData;
            set { _apobExtendedData = value; OnPropertyChanged(); }
        }

        private float _swaAdcV;
        public float SwaAdcV
        {
            get => _swaAdcV;
            set
            {
                _swaAdcV = value;
                OnPropertyChanged("SwaAdcV");
            }
        }

        private float _swbAdcV;
        public float SwbAdcV
        {
            get => _swbAdcV;
            set
            {
                _swbAdcV = value;
                OnPropertyChanged("SwbAdcV");
            }
        }

        private float _vppAdcV;
        public float VppAdcV
        {
            get => _vppAdcV;
            set
            {
                _vppAdcV = value;
                OnPropertyChanged("VppAdcV");
            }
        }

        private Ddr5PmicData _ddr5PmicData;
        public Ddr5PmicData PmicData
        {
            get => _ddr5PmicData;
            set
            {
                if (value == null) return;

                _ddr5PmicData = value;

                if (PmicData.SwaAdcMv > 0)
                    SwaAdcV = PmicData.SwaAdcMv / 1000.0f;

                if (PmicData.SwbAdcMv > 0)
                    SwbAdcV = PmicData.SwbAdcMv / 1000.0f;

                if (PmicData.SwcAdcMv > 0)
                    VppAdcV = PmicData.SwcAdcMv / 1000.0f;

                //OnPropertyChanged("PmicData");
            }
        }

        public MainViewModel(
            BaseDramTimings timings,
            MemType memoryType,
            bool compatMode,
            AppSettings settings,
            List<IPlugin> plugins,
            string motherboardLogoName,
            string agesaVersion,
            Ddr5PmicData pmicData)
        {
            Timings = timings;
            Settings = settings;
            Plugins = plugins;

            CpuName = VendorUtils.GetCpuNameString(CpuSingleton.Instance.systemInfo);
            SmuVersion = CpuSingleton.Instance?.systemInfo?.SmuVersionString ?? "Unknown";

            TotalCapacity = CpuSingleton.Instance.GetMemoryConfig().TotalCapacity;
            MemoryType = memoryType;

            PowerTable = CpuSingleton.Instance?.powerTable;
            CodeName = CpuSingleton.Instance.info.codeName;

            // APOB
            if (CpuSingleton.Instance.info.apob.IsAvailable)
            {
                ApobData = CpuSingleton.Instance.info.apob.Data;
                if (CpuSingleton.Instance.info.apob?.ExtendedData != null && CpuSingleton.Instance.info.apob.ExtendedData.ProcOdt != null)
                    ApobExtendedData = CpuSingleton.Instance.info.apob.ExtendedData;
                else
                    ApobExtendedData = ApobData;
            }

            //AgesaVersion = AGESA_SEARCHING;
            AgesaVersion = agesaVersion;

            WMIPresent = (!compatMode && memoryType == MemType.DDR4)
                         || memoryType == MemType.LPDDR4;

            IsMotherboardLogoVisible = motherboardLogoName != null;
            MotherboardLogoTooltip = motherboardLogoName != null
                ? $"Click to visit {CpuSingleton.Instance.systemInfo.MbName} page"
                : string.Empty;

            if (memoryType == MemType.DDR5 || memoryType == MemType.LPDDR5)
            {
                if (pmicData != null && pmicData.IsValid)
                {
                    PmicData = pmicData;
                }
                else
                {
                    // fallback to AOD table, through the located block so a misaligned dictionary
                    // cannot swap the rails
                    var located = AodVoltages.Read(CpuSingleton.Instance);
                    if (located != null)
                    {
                        SwaAdcV = located.Vdd / 1000.0f;
                        SwbAdcV = located.Vddq / 1000.0f;
                        VppAdcV = located.Vpp / 1000.0f;
                    }
                    else
                    {
                        var aodData = CpuSingleton.Instance.info.aod?.Table?.Data;
                        if (aodData != null)
                        {
                            SwaAdcV = aodData?.MemVddio != null ? aodData.MemVddio.RawValue / 1000.0f : 0;
                            SwbAdcV = aodData?.MemVddq != null ? aodData.MemVddq.RawValue / 1000.0f : 0;
                            VppAdcV = aodData?.MemVpp != null ? aodData.MemVpp.RawValue / 1000.0f : 0;
                        }
                    }
                }
            }

            // ECC
            ECC = SystemInfo.SMBios.MemoryDevices.Any(d => d.HasEcc);
        }

        bool IsMismatch(
            PropertyInfo prop,
            List<KeyValuePair<uint, BaseDramTimings>> timings)
        {
            object first = null;
            bool firstSet = false;

            foreach (var t in timings)
            {
                object value;
                try
                {
                    value = prop.GetValue(t.Value);
                }
                catch
                {
                    continue;
                }

                if (!firstSet)
                {
                    first = value;
                    firstSet = true;
                }
                else if (!Equals(first, value))
                {
                    return true;
                }
            }
            return false;
        }

        public string GetXML()
        {
            XmlSerializer x = new XmlSerializer(this.GetType());
            using (StringWriter textWriter = new StringWriter())
            {
                x.Serialize(textWriter, this);
                return textWriter.ToString();
            }
        }

        public string GetHTML()
        {
            var cpu = CpuSingleton.Instance;
            var type = cpu.systemInfo.GetType();
            var properties = type.GetProperties();
            string appVersion = $"{System.Windows.Forms.Application.ProductName} {System.Windows.Forms.Application.ProductVersion}";

            // Same palette as the benchmark export - the two reports come out of one app and used
            // to look like they came out of two. Print falls back to white, where dark would waste
            // a cartridge and read worse on paper.
            string html = @"<!DOCTYPE html>
            <html>
            <head>
            <title></title>
            <style>
            body {
                font-family: Segoe UI, Tahoma, sans-serif;
                background: #141b28;
                color: #dbe2ee;
            }

            h1 {
                color: #eaf0fa;
            }

            h2 {
                color: #7facff;
                border-bottom: 2px solid #2a3550;
                padding-bottom: 4px;
            }

            table {
                border-collapse: collapse;
                width: auto;
                margin-bottom: 20px;
                background: #1c2537;
            }

            th, td {
                border: 1px solid #2a3550;
                padding: 6px 8px;
                text-align: center;
                font-size: 13px;
            }

            th {
                background: #223052;
                color: #a8c4ff;
                cursor: pointer;
                user-select: none;
            }

            td:first-child {
                text-align: left;
                font-weight: 500;
            }

            tr.mismatch td {
                background: #3a2230;
            }

            tr.primary td:first-child {
                color: #eaf0fa;
                font-weight: 700;
            }

            tr.secondary td:first-child {
                color: #8b96aa;
            }

            @media print {
                body { background: #fff; color: #1f2937; }
                h1, tr.primary td:first-child { color: #0f172a; }
                h2 { color: #2563eb; border-bottom-color: #e5e7eb; }
                table { background: #fff; }
                th, td { border-color: #e5e7eb; }
                th { background: #e8f0fe; color: #1d4ed8; }
                tr.mismatch td { background: #fff1f2; }
                tr.secondary td:first-child { color: #475569; }
            }
            </style>
            </head>
            <body>";
            html += $@"<h1>{appVersion}</h1>";
            html += $@"<div>Core Version: {cpu.Version}</div>";
            html += $@"<div>PawnIO Version: {DriverHelper.Version}</div>";
            html += $@"<div>Date: {DateTime.Now:dd MMMM yyyy HH:mm:ss}</div>";

            html += "<h2>System Info</h2>";
            html += "<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\">";

            foreach (var property in properties)
            {
                if (property.Name == "CpuId" || property.Name == "PatchLevel" || property.Name == "SmuTableVersion")
                    html += $"<tr><td>{property.Name}</td><td>{property.GetValue(cpu.systemInfo, null):X8}</td></tr>";
                else if (property.Name == "SmuVersion")
                    html += $"<tr><td>{property.Name}</td><td>{cpu.systemInfo.SmuVersionString}</td></tr>";
                else if (property.Name == "Model" || property.Name == "ExtendedModel" || property.Name == "BaseModel")
                    html += $"<tr><td>{property.Name}</td><td>{property.GetValue(cpu.systemInfo, null)} (0x{property.GetValue(cpu.systemInfo, null):X})</td></tr>";
                else if (property.Name != "SMBios")
                    html += $"<tr><td>{property.Name}</td><td>{property.GetValue(cpu.systemInfo, null)}</td></tr>";
            }

            html += "</table>";


            var memConfigs = cpu.GetMemoryConfig();
            var allTimings = memConfigs.Timings;
            var props = allTimings[0].Value.GetType().GetProperties();

            // Filter timings to only include unique DctOffset values
            var uniqueTimings = allTimings
                .GroupBy(t => t.Key)
                .Select(g => g.First())
                .ToList();

            var timingProperties = uniqueTimings[0].Value
                .GetType()
                .GetProperties()
                .Where(p => p.GetIndexParameters().Length == 0)
                .ToList();

            var primaryTimings = new HashSet<string>
            {
                "tCL", "tRCD", "tRP", "tRAS", "tRC",
                "tRRDS", "tRRDL", "tFAW",
                "tCWL", "tWR"
            };

            // Timings
            html += "<h2>Memory Timings</h2>";
            html += "<table id='timingsTable' data-sort-col='' data-sort-dir=''>";
            html += "<tr><th onclick='sortTable(0)'>Timing</th>";

            foreach (var timing in uniqueTimings)
            {
                html += $"<th onclick='sortTable({uniqueTimings.IndexOf(timing) + 1})'>DCT {timing.Key >> 20}</th>";
            }

            html += "</tr>";

            foreach (var prop in timingProperties)
            {
                bool mismatch = IsMismatch(prop, uniqueTimings);
                string rowClass = mismatch ? " mismatch" : "";

                html += $"<tr class='{rowClass}'>";
                html += $"<td>{prop.Name}</td>";

                foreach (var timing in uniqueTimings)
                {
                    html += $"<td>{prop.GetValue(timing.Value)}</td>";
                }

                html += "</tr>";
            }

            // The tCCD_L family lives outside BaseDramTimings (APOB-sourced), so the reflection
            // loop above never sees it. Not per-DCT - the same value fills every column. Each row
            // stands on its own value: WR and WR2 are still worth showing when tCCD_L is not.
            // The cells carry the number alone; the on-screen "!" is a provenance marker, and a
            // report someone grabs a value out of must not have it glued to the digits.
            bool anyTccdl = false;
            foreach (var row in new[]
                     {
                         new { Name = "tCCD_L", Value = TccdlValue },
                         new { Name = "tCCD_L_WR", Value = TccdlWrValue },
                         new { Name = "tCCD_L_WR2", Value = TccdlWr2Value },
                     })
            {
                if (row.Value == 0)
                    continue;

                anyTccdl = true;
                html += $"<tr><td>{row.Name}</td>";
                foreach (var timing in uniqueTimings)
                    html += $"<td>{row.Value}</td>";
                html += "</tr>";
            }

            html += "</table>";

            if (anyTccdl && TccdlFromRegister)
                html += "<div>tCCD_L values came from the register fallback, not a located APOB run - treat them as unverified.</div>";

            // PMT
            html += "<h2>PMT</h2>";
            html += "<table id='pmtTable' data-sort-col='' data-sort-dir=''>";

            type = cpu.powerTable.GetType();
            properties = type.GetProperties();

            foreach (var property in properties)
            {

                if (property.Name == "TableVersion")
                    html += $"<tr><td>{property.Name}</td><td>{property.GetValue(cpu.powerTable, null):X8}</td></tr>";
                else if (property.Name != "Table")
                    html += $"<tr><td>{property.Name}</td><td>{property.GetValue(cpu.powerTable, null)}</td></tr>";
            }
            html += "</table>";

            // AOD
            html += "<h2>AOD</h2>";
            html += "<table id='pmtTable' data-sort-col='' data-sort-dir=''>";

            type = cpu.info.aod.Table.Data.GetType();
            properties = type.GetProperties();

            foreach (var property in properties)
            {

                if (!property.Name.ToLowerInvariant().StartsWith("t"))
                    html += $"<tr><td>{property.Name}</td><td>{property.GetValue(cpu.info.aod.Table.Data, null)}</td></tr>";
            }
            html += "</table>";

            html += "</body></html>";

            return html;
        }

        public string GetJSON()
        {
            var cpu = CpuSingleton.Instance;
            var systemInfo = cpu.systemInfo;
            string appVersion = $"{System.Windows.Forms.Application.ProductName} {System.Windows.Forms.Application.ProductVersion}";

            var memConfigs = cpu.GetMemoryConfig();
            var allTimings = memConfigs.Timings;
            var uniqueTimings = allTimings
                .GroupBy(t => t.Key)
                .Select(g => g.First())
                .ToList();

            // Hand-rolled writer: System.Text.Json does not exist on .NET Framework 4.5 and the
            // project deliberately carries no JSON dependency.
            var json = new JsonWriter();

            json.BeginObject();

            json.PropertyName("Application").BeginObject()
                .Property("Version", appVersion)
                .Property("CoreVersion", cpu.Version?.ToString())
                .Property("PawnIOVersion", DriverHelper.Version?.ToString())
                .Property("ExportDate", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                .EndObject();

            json.PropertyName("SystemInfo").BeginObject()
                .Property("CpuName", CpuName)
                .Property("MotherboardName", systemInfo.MbName)
                .Property("BiosVersion", systemInfo.BiosVersion)
                .Property("SmuVersion", SmuVersion)
                .Property("AgesaVersion", systemInfo.AgesaVersion)
                .Property("CpuId", $"{systemInfo.CpuId:X8}")
                .Property("PatchLevel", $"{systemInfo.PatchLevel:X8}")
                .EndObject();

            json.PropertyName("MemoryConfiguration").BeginObject()
                .Property("Type", MemoryType.ToString())
                .Property("Frequency", MemoryFrequency)
                .Property("TotalCapacity", TotalCapacity?.ToString() ?? "Unknown")
                .Property("ECC", ECC)
                .Property("tCCD_L", TccdlValue > 0 ? (object)TccdlValue : null)
                .Property("tCCD_L_WR", TccdlWrValue > 0 ? (object)TccdlWrValue : null)
                .Property("tCCD_L_WR2", TccdlWr2Value > 0 ? (object)TccdlWr2Value : null)
                .EndObject();

            json.PropertyName("MemoryTimings").BeginArray();
            foreach (var timing in uniqueTimings)
            {
                json.BeginObject()
                    .Property("DCT", timing.Key >> 20)
                    .PropertyName("Timings")
                    .WriteDictionary(GetTimingDictionary(timing.Value))
                    .EndObject();
            }
            json.EndArray();

            json.PropertyName("PowerTable").WriteDictionary(GetPowerTableDictionary(cpu.powerTable));

            try
            {
                json.PropertyName("AOD").WriteDictionary(GetAODDictionary(cpu.info.aod.Table.Data));
            }
            catch
            {
                // No AOD table on this platform - omit the section rather than fail the export.
                json.PropertyName("AOD").WriteNull();
            }

            json.EndObject();

            return json.ToString();
        }

        private Dictionary<string, object> GetTimingDictionary(BaseDramTimings timings)
        {
            var dict = new Dictionary<string, object>();
            var props = timings.GetType().GetProperties();

            foreach (var prop in props)
            {
                if (prop.GetIndexParameters().Length == 0)
                {
                    dict[prop.Name] = prop.GetValue(timings) ?? "N/A";
                }
            }

            return dict;
        }

        private Dictionary<string, object> GetPowerTableDictionary(PowerTable powerTable)
        {
            var dict = new Dictionary<string, object>();
            var props = powerTable.GetType().GetProperties();

            foreach (var prop in props)
            {
                if (prop.Name != "Table")
                {
                    dict[prop.Name] = prop.GetValue(powerTable) ?? "N/A";
                }
            }

            return dict;
        }

        private Dictionary<string, object> GetAODDictionary(object aodData)
        {
            var dict = new Dictionary<string, object>();
            var props = aodData.GetType().GetProperties();

            foreach (var prop in props)
            {
                if (!prop.Name.ToLowerInvariant().StartsWith("t"))
                {
                    dict[prop.Name] = prop.GetValue(aodData) ?? "N/A";
                }
            }

            return dict;
        }
    }
}
