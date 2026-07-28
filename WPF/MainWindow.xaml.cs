//#define BETA

using AdonisUI.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ZenStates.Core;
using ZenStates.Core.DRAM;
using ZenTimings.Controls;
using ZenTimings.Localization;
using ZenTimings.Plugin;
using ZenTimings.ViewModels;
using ZenTimings.Windows;
using Forms = System.Windows.Forms;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;
using MessageBoxResult = AdonisUI.Controls.MessageBoxResult;
//using OpenHardwareMonitor.Hardware;

namespace ZenTimings
{
    /// <summary>
    ///     Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : ThemedAdonisWindow
    {
        private readonly AsusWMI AsusWmi = new AsusWMI();
        private readonly List<BiosACPIFunction> biosFunctions = new List<BiosACPIFunction>();
        private readonly BiosMemController BMC;
        private readonly Cpu cpu;
        private readonly DispatcherTimer PowerCfgTimer = new DispatcherTimer();
        private readonly AppSettings settings = AppSettings.Instance;
        private readonly List<IPlugin> plugins = new List<IPlugin>();
        private SystemInfoWindow siWnd = null;
        private Windows.TelemetryWindow telemetryWnd = null;
        private OptionsDialog optionsWnd = null;
        private AboutDialog aboutWnd = null;
        private OcToolsWindow ocToolsWnd = null;
        internal readonly Forms.NotifyIcon _notifyIcon;
        private bool compatMode;
        private Control timingsPanel;
        private readonly MainViewModel mainViewModel;
        private float lastMclk = 0;
        //private Computer computer;

        private MemType memType;
        private readonly TelemetryLogger telemetryLogger = new TelemetryLogger();
        private readonly HotKeyManager screenshotHotKey = new HotKeyManager();
        private readonly HotKeyManager clipboardHotKey = new HotKeyManager();
        private const int ScreenshotHotKeyId = 0x5A54;   // 'ZT'
        private const int ClipboardHotKeyId = 0x5A55;
        private System.Drawing.Icon dynamicTrayIcon;
        private string lastTrayText;

        // tCCD_L family, not decoded by ZenStates-Core. Found by diffing register dumps.
        //   tCCD_L     = 0x50198[7:3] + 5   (5 bits, so max 36)
        //   tCCD_L_WR2 = 0x502E0[5:0] + 7
        // tCCD_L_WR is in no UMC register. Dumps differing only in it are byte-identical across
        // both channel windows, the AOD table and the SPD. See ReadTccdlWrFromApob.
        private const uint TccdlRegister = 0x50198u;
        private const uint TccdlWr2Register = 0x502E0u;

        private readonly string AssemblyProduct = ((AssemblyProductAttribute)Attribute.GetCustomAttribute(
            Assembly.GetExecutingAssembly(),
            typeof(AssemblyProductAttribute), false)).Product;

        private readonly string AssemblyVersion = ((AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(
            Assembly.GetExecutingAssembly(),
            typeof(AssemblyFileVersionAttribute), false)).Version;


        public void CheckForDriver()
        {
            if (DriverHelper.IsPawnIoInstalled)
            {
                var currentVersion = DriverHelper.Version;
                var newVersion = DriverHelper.BundledVersion;
                var skippedVersion = !string.IsNullOrEmpty(AppSettings.Instance.DriverUpdateLastSkippedVersion)
                    ? new Version(AppSettings.Instance.DriverUpdateLastSkippedVersion)
                    : new Version(0, 0, 0, 0);

                if (skippedVersion < newVersion && currentVersion < newVersion)
                {
                    DriverUpdateWindow driverUpdateWindow = new DriverUpdateWindow(currentVersion, newVersion)
                    {
                        Owner = Application.Current.MainWindow
                    };

                    bool? result = driverUpdateWindow.ShowDialog();

                    if (driverUpdateWindow.IsSkipChecked)
                    {
                        AppSettings.Instance.DriverUpdateLastSkippedVersion = newVersion.ToString();
                        AppSettings.Instance.Save();
                    }

                    if (result == true)
                    {
                        SplashWindow.Stop();
                        DriverHelper.InstallPawnIO();
                        Restart(false);
                    }
                }
            }
            else
            {
                {
                    AdonisUI.Controls.MessageBoxResult result = AdonisUI.Controls.MessageBox.Show(
                        "PawnIO is not installed, do you want to install it?",
                        nameof(ZenTimings),
                        AdonisUI.Controls.MessageBoxButton.OKCancel,
                        AdonisUI.Controls.MessageBoxImage.Warning
                    );

                    if (result == AdonisUI.Controls.MessageBoxResult.OK)
                    {
                        SplashWindow.Stop();
                        DriverHelper.InstallPawnIO();
                        Restart(false);
                    }

                    if (result == AdonisUI.Controls.MessageBoxResult.Cancel)
                    {
                        Application.Current.Shutdown();
                    }
                }
            }
        }

        public MainWindow()
        {
            try
            {
                SplashWindow.Loading("PawnIO");
                CheckForDriver();

                SplashWindow.Loading("Core");
                cpu = CpuSingleton.Instance;

                if (cpu.info.family.Equals(Cpu.Family.UNSUPPORTED))
                {
                    throw new ApplicationException("CPU is not supported.");
                }
                else if (cpu.info.codeName.Equals(Cpu.CodeName.Unsupported))
                {
                    MessageBox.Show(
                        "CPU model is not supported.\nPlease run a debug report and send to the developer.",
                        "Unsupported CPU Model",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                }

                if (!cpu.RyzenSmu.IsLoaded)
                {
                    HandleError("Ryzen SMU module is not loaded.\nMake sure that the PawnIO driver is installed correctly.", "Driver Error");
                    ExitApplication();
                }

                IconSource = GetIcon("pack://application:,,,/ZenTimings;component/Resources/ZenTimings2022.ico", 16);
                _notifyIcon = GetTrayIcon();

                InitializeComponent();
                //SetResourceReference(NativeBorderBrushProperty, "WindowBorderColor");
                SplashWindow.Loading("Memory modules");
                ReadMemoryModulesInfo();

                SplashWindow.Loading("Timings");

                var memoryType = cpu.GetMemoryConfig().Type;
                memType = memoryType;

                // Motherboard logo
                var motherboardLogoName = VendorUtils.GetMotherboardLogo(cpu.systemInfo);
                if (motherboardLogoName != null)
                {
                    motherboardLogoImage.SetResourceReference(Image.SourceProperty, motherboardLogoName);
                }

                mainViewModel = new MainViewModel(
                    ReadTimings(),
                    memoryType,
                    compatMode,
                    settings,
                    plugins,
                    motherboardLogoName,
                    GetAgesaVersion(),
                    cpu.GetMemoryConfig()?.SpdInfo?.Values.FirstOrDefault(d => d.IsValid)?.PmicData ?? null
                );

                DataContext = mainViewModel;

                if (cpu != null && settings.AdvancedMode)
                {

                    PowerCfgTimer.Interval = TimeSpan.FromMilliseconds(settings.AutoRefreshInterval);
                    PowerCfgTimer.Tick += PowerCfgTimer_Tick;

                    SplashWindow.Loading("Reading power table");
                    if (!WaitForPowerTable())
                    {
                        SplashWindow.Loading("Power table error!");
                    }

                    SplashWindow.Loading("Plugins");
                    if (memoryType == MemType.DDR4 || memoryType == MemType.LPDDR4)
                    {
                        SplashWindow.Loading("SVI2 Plugin");
                        plugins.Add(new SVI2Plugin(cpu));
                        //ReadSVI();

                        SplashWindow.Loading("Memory controller");
                        BMC = new BiosMemController();
                    }
                    //plugins.Add(new OHWMPlugin());
                    //plugins[1].Open();

                    if (!AsusWmi.Init())
                    {
                        AsusWmi.Dispose();
                        AsusWmi = null;
                    }
                }

                SplashWindow.Loading("Done");

                AddTimingsPanel(memoryType);

                // Register addresses only verified on Zen4/Zen5 desktop parts, so APUs are excluded.
                mainViewModel.IsTccdlVisible =
                    (memoryType == MemType.DDR5 || memoryType == MemType.LPDDR5)
                    && (cpu.info.family == Cpu.Family.FAMILY_19H || cpu.info.family == Cpu.Family.FAMILY_1AH)
                    && cpu.smu.SMU_TYPE != SMU.SmuType.TYPE_APU2;

                TimingTooltips.Attach(timingsPanel, mainViewModel);

                if (settings.AdvancedMode)
                {
                    if (memoryType == MemType.DDR4 || memoryType == MemType.LPDDR4)
                    {
                        ReadSVI();
                        ReadDDR4MemoryConfig();
                        ReadVddioFromSio();
                    }
                    StartAutoRefresh();
                    UpdateLiveReadouts();
                }
            }
            catch (Exception ex)
            {
                HandleError(ex.Message);
                ExitApplication();
            }
        }

        private void AddTimingsPanel(MemType memoryType)
        {
            // Add timings panel
            switch (memoryType)
            {
                case MemType.DDR4:
                case MemType.LPDDR4:
                    timingsPanel = new DDR4TimingsPanel();
                    break;

                case MemType.LPDDR5:
                    timingsPanel = new LegacyDDR5APUTimingsPanel();
                    break;

                case MemType.DDR5:
                    {
                        if (!cpu.info.apob.IsAvailable || settings.ImpedanceTableSrc == AppSettings.ImpedanceTableSource.AOD)
                        {
                            if (cpu.smu.SMU_TYPE == SMU.SmuType.TYPE_APU2)
                                timingsPanel = new LegacyDDR5APUTimingsPanel();
                            else
                                timingsPanel = new LegacyDDR5TimingsPanel();
                            break;
                        }

                        if (cpu.smu.SMU_TYPE == SMU.SmuType.TYPE_APU2)
                        {
                            timingsPanel = new DDR5APUTimingsPanel();
                        }
                        else if (cpu.info.family == Cpu.Family.FAMILY_1AH)
                        {
                            timingsPanel = new DDR5TimingsPanel1Ah();
                        }
                        else
                        {
                            timingsPanel = new DDR5TimingsPanel19h();
                        }
                        break;
                    }

                default:
                    timingsPanel = null;
                    break;
            }

            if (timingsPanel != null)
            {
                timingsPanelSlot.Children.Add(timingsPanel);
            }
        }

        private Forms.NotifyIcon GetTrayIcon()
        {
            Forms.NotifyIcon notifyIcon = new Forms.NotifyIcon
            {
                Icon = Properties.Resources.ZenTimings2022
            };

            notifyIcon.MouseClick += NotifyIcon_MouseClick;
            notifyIcon.ContextMenuStrip = new Forms.ContextMenuStrip();
            notifyIcon.ContextMenuStrip.Items.Add($"{AssemblyProduct} {AssemblyVersion}", null, OnAppContextMenuItemClick);
            notifyIcon.ContextMenuStrip.Items.Add("-");
            notifyIcon.ContextMenuStrip.Items.Add("Exit", null, (object sender, EventArgs e) => ExitApplication());

            return notifyIcon;
        }

        private void OnAppContextMenuItemClick(object sender, EventArgs e)
        {
            WindowState = WindowState.Normal;
        }

        private void NotifyIcon_MouseClick(object sender, Forms.MouseEventArgs e)
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                WindowState = WindowState.Normal;
                Activate();
            }

            // else, default = show context menu
        }

        private void Cleanup()
        {
            foreach (IPlugin plugin in plugins)
                plugin?.Close();

            screenshotHotKey?.Dispose();
            telemetryLogger?.Dispose();

            _notifyIcon?.Dispose();
            dynamicTrayIcon?.Dispose();
            AsusWmi?.Dispose();
            //cpu?.io?.Close(settings.AutoUninstallDriver);
            cpu?.Dispose();

            //Driver.Cleanup();
        }

        private void ExitApplication(bool save = true)
        {
            if (save) settings.Save();
            Cleanup();
            Application.Current?.Shutdown();
        }

        private BiosACPIFunction GetFunctionByIdString(string name)
        {
            return biosFunctions.Find(x => x.IDString == name);
        }

        private void ReadMemoryModulesInfo()
        {
            var modules = cpu.GetMemoryConfig()?.Modules;
            if (modules?.Count > 0)
            {
                foreach (MemoryModule module in modules)
                {
                    var moduleLogoName = VendorUtils.GetMemoryModuleLogo(module);
                    if (moduleLogoName != null)
                    {
                        var stackPanel = new StackPanel
                        {
                            Orientation = Orientation.Horizontal
                        };

                        var image = new Image
                        {
                            Height = 18,
                            Margin = new Thickness(5, 0, 5, 0)
                        };

                        image.SetResourceReference(Image.SourceProperty, moduleLogoName);

                        var moduleStrings = module.ToString().Split(':');

                        var textBlock = new TextBlock
                        {
                            Text = $"{moduleStrings[0]}: ",
                            VerticalAlignment = VerticalAlignment.Center
                        };

                        var textBlock2 = new TextBlock
                        {
                            Text = moduleStrings[1].Trim(),
                            VerticalAlignment = VerticalAlignment.Center
                        };

                        stackPanel.Children.Add(textBlock);
                        stackPanel.Children.Add(image);
                        stackPanel.Children.Add(textBlock2);
                        comboBoxPartNumber.Items.Add(stackPanel);
                    }
                    else
                    {
                        comboBoxPartNumber.Items.Add(new ComboBoxItem
                        {
                            Content = module.ToString(),
                            Tag = module.PartNumber
                        });
                    }
                }

                if (comboBoxPartNumber.Items.Count > 0)
                {
                    comboBoxPartNumber.SelectedIndex = 0;
                    comboBoxPartNumber.SelectionChanged += ComboBoxPartNumber_SelectionChanged;
                }
            }
        }

        private void RefreshSensors()
        {
            plugins[1].Update();
            /*
            foreach (var sensor in plugins[1].Sensors)
            {
                Console.WriteLine($"----Name: {sensor.Name}, Value: {sensor.Value}");
            }
            */
        }

        private void ReadSVI()
        {
            if ((cpu.memoryConfig.Type == MemType.DDR4 || cpu.memoryConfig.Type == MemType.LPDDR4) && plugins.Count > 0 && plugins[0].Update())
            {
                (timingsPanel as DDR4TimingsPanel).textBoxVSOC_SVI2.Text = $"{plugins[0].Sensors[0].Value:F4}V";
            }
        }

        // TODO: Handle in DLL or replace with read from memory
        private void ReadDDR4MemoryConfig()
        {
            string scope = @"root\wmi";
            string className = "AMD_ACPI";

            try
            {
                WMI.Connect($@"{scope}");

                string instanceName = WMI.GetInstanceName(scope, className);

                ManagementObject classInstance = new ManagementObject(scope,
                    $"{className}.InstanceName='{instanceName}'",
                    null);

                /* // Get possible values (index) of a memory option in BIOS
                var dvaluesPack = WMI.InvokeMethodAndGetValue(classInstance, "Getdvalues", "pack", "ID", 0x20035);
                if (dvaluesPack != null)
                {
                    uint[] DValuesBuffer = (uint[])dvaluesPack.GetPropertyValue("DValuesBuffer");
                    for (var i = 0; i < DValuesBuffer.Length; i++)
                    {
                        Debug.WriteLine("{0}", DValuesBuffer[i]);
                    }
                }*/

                // Get function names with their IDs
                var wmiFunctionsDict = AOD.GetWmiFunctions();
                if (wmiFunctionsDict != null)
                {
                    foreach (var kvp in wmiFunctionsDict)
                    {
                        biosFunctions.Add(new BiosACPIFunction(kvp.Key, kvp.Value));
                    }
                }

                // Get APCB config from BIOS. Holds memory parameters.
                BiosACPIFunction cmd = GetFunctionByIdString("Get APCB Config");
                if (cmd == null)
                {
                    // throw new Exception("Could not get memory controller config");
                    // Use AOD table as an alternative path for now
                    BMC.Table = cpu.info.aod.Table.RawAodTable;
                }
                else
                {
                    byte[] apcbConfig = WMI.RunCommand(classInstance, cmd.ID);
                    // BiosACPIFunction cmd = new BiosACPIFunction("Get APCB Config", 0x00010001);
                    cmd = GetFunctionByIdString("Get memory voltages");
                    if (cmd != null)
                    {
                        byte[] voltages = WMI.RunCommand(classInstance, cmd.ID);

                        // MEM_VDDIO is ushort, offset 27
                        // MEM_VTT is ushort, offset 29
                        for (int i = 27; i <= 30; i++)
                        {
                            byte value = voltages[i];
                            if (value > 0)
                                apcbConfig[i] = value;
                        }
                    }

                    BMC.Table = apcbConfig ?? new byte[] { };
                }

                float vdimm = Convert.ToSingle(Convert.ToDecimal(BMC.Config.MemVddio) / 1000);
                if (vdimm > 0 && vdimm < 3)
                {
                    (timingsPanel as DDR4TimingsPanel).textBoxMemVddio.Text = $"{vdimm:F4}V";
                }
                else if (AsusWmi != null && AsusWmi.Status == 1)
                {
                    AsusSensorInfo sensor = AsusWmi.FindSensorByName("DRAM Voltage");
                    float temp = 0;
                    bool valid = sensor != null && float.TryParse(sensor.Value, out temp);

                    if (valid && temp > 0 && temp < 3)
                        (timingsPanel as DDR4TimingsPanel).textBoxMemVddio.Text = sensor.Value;
                    else
                        (timingsPanel as DDR4TimingsPanel).labelMemVddio.IsEnabled = false;
                }
                else
                {
                    (timingsPanel as DDR4TimingsPanel).labelMemVddio.IsEnabled = false;
                }

                float vtt = Convert.ToSingle(Convert.ToDecimal(BMC.Config.MemVtt) / 1000);
                if (vtt > 0)
                    (timingsPanel as DDR4TimingsPanel).textBoxMemVtt.Text = $"{vtt:F4}V";
                else
                    (timingsPanel as DDR4TimingsPanel).labelMemVtt.IsEnabled = false;

                // When ProcODT is 0, then all other resistance values are 0
                // Happens when one DIMM installed in A1 or A2 slot
                if (BMC.Table == null || Utils.AllZero(BMC.Table) || BMC.Config.ProcODT < 1)
                    // throw new Exception("Failed to read AMD ACPI. Odt, Setup and Drive strength parameters will be empty.");
                    return;

                (timingsPanel as DDR4TimingsPanel).labelProcODT.IsEnabled = true;
                (timingsPanel as DDR4TimingsPanel).labelClkDrvStren.IsEnabled = true;
                (timingsPanel as DDR4TimingsPanel).labelAddrCmdDrvStren.IsEnabled = true;
                (timingsPanel as DDR4TimingsPanel).labelCsOdtDrvStren.IsEnabled = true;
                (timingsPanel as DDR4TimingsPanel).labelCkeDrvStren.IsEnabled = true;
                (timingsPanel as DDR4TimingsPanel).labelRttNom.IsEnabled = true;
                (timingsPanel as DDR4TimingsPanel).labelRttWr.IsEnabled = true;
                (timingsPanel as DDR4TimingsPanel).labelRttPark.IsEnabled = true;
                (timingsPanel as DDR4TimingsPanel).labelAddrCmdSetup.IsEnabled = true;
                (timingsPanel as DDR4TimingsPanel).labelCsOdtSetup.IsEnabled = true;
                (timingsPanel as DDR4TimingsPanel).labelCkeSetup.IsEnabled = true;

                (timingsPanel as DDR4TimingsPanel).textBoxProcODT.Text = BMC.GetProcODTString(BMC.Config.ProcODT);

                (timingsPanel as DDR4TimingsPanel).textBoxClkDrvStren.Text = BMC.GetDrvStrenString(BMC.Config.ClkDrvStren);
                (timingsPanel as DDR4TimingsPanel).textBoxAddrCmdDrvStren.Text = BMC.GetDrvStrenString(BMC.Config.AddrCmdDrvStren);
                (timingsPanel as DDR4TimingsPanel).textBoxCsOdtCmdDrvStren.Text = BMC.GetDrvStrenString(BMC.Config.CsOdtCmdDrvStren);
                (timingsPanel as DDR4TimingsPanel).textBoxCkeDrvStren.Text = BMC.GetDrvStrenString(BMC.Config.CkeDrvStren);

                (timingsPanel as DDR4TimingsPanel).textBoxRttNom.Text = BMC.GetRttString(BMC.Config.RttNom);
                (timingsPanel as DDR4TimingsPanel).textBoxRttWr.Text = BMC.GetRttWrString(BMC.Config.RttWr);
                (timingsPanel as DDR4TimingsPanel).textBoxRttPark.Text = BMC.GetRttString(BMC.Config.RttPark);

                (timingsPanel as DDR4TimingsPanel).textBoxAddrCmdSetup.Text = $"{BMC.Config.AddrCmdSetup}";
                (timingsPanel as DDR4TimingsPanel).textBoxCsOdtSetup.Text = $"{BMC.Config.CsOdtSetup}";
                (timingsPanel as DDR4TimingsPanel).textBoxCkeSetup.Text = $"{BMC.Config.CkeSetup}";
            }
            catch (Exception ex)
            {
                compatMode = true;

                MessageBox.Show(
                    ex.Message,
                    "Warning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Console.WriteLine(ex.Message);
            }

            BMC?.Dispose();
        }

        // Best-effort DRAM voltage (VDIMM) via the motherboard Super I/O chip using OpenHardwareMonitor.
        // DDR4 exposes no on-module voltage telemetry; when the ACPI/APCB and ASUS-WMI paths both fail
        // (VDIMM still "N/A"), the SIO sensor is the last resort. Requires OpenHardwareMonitorLib.dll next
        // to the exe and a loadable ring0 driver — silently leaves N/A if unavailable.
        private void ReadVddioFromSio()
        {
            DDR4TimingsPanel panel = timingsPanel as DDR4TimingsPanel;
            if (panel == null || panel.textBoxMemVddio.Text != "N/A")
                return;

            OHWMPlugin ohwm = null;
            try
            {
                ohwm = new OHWMPlugin();
                if (!ohwm.IsAvailable)
                    return;

                ohwm.Open();

                var sensors = ohwm.Sensors;
                if (sensors == null || sensors.Count == 0)
                    return;

                // Diagnostic dump so the correct DRAM channel can be identified on boards that
                // report generic "Voltage #N" names.
                try
                {
                    string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                    File.WriteAllLines(
                        Path.Combine(exeDir, "ohwm_voltages.txt"),
                        sensors.Select(s => $"[{s.Index}] {s.Name} = {s.Value:F3} V"));
                }
                catch { /* diagnostics are best-effort */ }

                var dram = sensors.FirstOrDefault(s => s.Name != null &&
                    (s.Name.IndexOf("dram", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     s.Name.IndexOf("dimm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     s.Name.IndexOf("vddio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     s.Name.IndexOf("memory", StringComparison.OrdinalIgnoreCase) >= 0));

                if (dram?.Value != null && dram.Value > 0.9 && dram.Value < 2.2)
                {
                    panel.textBoxMemVddio.Text = $"{dram.Value:F3}V";
                    panel.labelMemVddio.IsEnabled = true;
                }
            }
            catch
            {
                // SIO access unavailable (missing DLL, blocked ring0 driver, unsupported board) — keep N/A.
            }
            finally
            {
                ohwm?.Close();
            }
        }

        //TODO: Replace with a call to DLL
        private BaseDramTimings ReadTimings(uint offset = 0)
        {
            cpu.memoryConfig.ReadTimings(offset);
            var timings = cpu.memoryConfig.Timings;
            BaseDramTimings result = null;

            if (timings.Count == 0)
                return result;

            var index = timings.FindIndex(m => m.Key.Equals(offset));
            return timings[index < 0 ? 0 : index].Value;

            //float configured = mainViewModel?.MemoryFrequency ?? 0;
            //float ratio = result.Ratio;
            //float freqFromRatio = ratio * 200;

            //// Fallback to ratio when ConfiguredClockSpeed fails
            //if ((configured == 0.0f || freqFromRatio > configured) && mainViewModel != null)
            //{
            //    mainViewModel.MemoryFrequency = freqFromRatio;
            //}

            //mainViewModel.MemoryFrequency = result.Frequency;

            //return result;
        }

        private bool WaitForDriverLoad()
        {
            Stopwatch timer = new Stopwatch();
            timer.Start();

            bool temp;
            // Refresh until driver is opened
            do
            {
                temp = cpu.io.IsInpOutDriverOpen();
            } while (!temp && timer.Elapsed.TotalMilliseconds < 10000);

            timer.Stop();

            return temp;
        }

        private bool WaitForPowerTable()
        {
            if (cpu.powerTable == null || cpu.powerTable.DramBaseAddress == 0)
            {
                HandleError("Could not initialize power table.\n\nClose the application and try again. If the issue persists, you might want to try a system restart.");
                return false;
            }

            if (WaitForDriverLoad())
            {
                Stopwatch timer = new Stopwatch();
                int timeout = 100000;

                // TODO: Move to Core DLL
                var memoryConfig = cpu.memoryConfig.Timings.FirstOrDefault().Value;
                if (memoryConfig != null)
                {
                    cpu.powerTable.ConfiguredClockSpeed = memoryConfig.Frequency;
                    cpu.powerTable.MemRatio = memoryConfig.Ratio;
                }

                timer.Start();

                SMU.Status status;
                // Refresh each 200ms seconds until table is transferred to DRAM or timeout
                do
                {
                    status = cpu.RefreshPowerTable();
                    if (status != SMU.Status.OK)
                        Thread.Sleep(200);  // It's ok to block the current thread
                } while (status != SMU.Status.OK && timer.Elapsed.TotalMilliseconds < timeout);

                timer.Stop();

                if (status != SMU.Status.OK)
                {
                    HandleError("Could not get power table.\nSkipping.");
                    return false;
                }

                return true;
            }
            else
            {
                HandleError("I/O driver is not responding or not loaded.");
                return false;
            }
        }

        private void StartAutoRefresh()
        {
            if (settings.AutoRefresh && settings.AdvancedMode && !PowerCfgTimer.IsEnabled)
            {
                PowerCfgTimer.Interval = TimeSpan.FromMilliseconds(settings.AutoRefreshInterval);
                PowerCfgTimer.Start();
            }
        }

        private void StopAutoRefresh()
        {
            if (PowerCfgTimer.IsEnabled)
                PowerCfgTimer.Stop();
        }

        // Reads live CPU Die/memory temperatures and the base clock (BCLK), pushing them to the view model.
        // Safe to call from a background thread: MainViewModel marshals PropertyChanged to the UI thread.
        private void UpdateLiveReadouts()
        {
            if (mainViewModel == null || cpu == null)
                return;

            // CPU Die (Tctl/Tdie) temperature
            try
            {
                float? cpuTemp = cpu.GetCpuTemperature();
                if (cpuTemp.HasValue && cpuTemp.Value > 0 && cpuTemp.Value < 150)
                {
                    mainViewModel.UpdateCpuTemperature(cpuTemp.Value);
                    mainViewModel.IsCpuTemperatureAvailable = true;
                }
                else
                {
                    mainViewModel.IsCpuTemperatureAvailable = false;
                }
            }
            catch
            {
                mainViewModel.IsCpuTemperatureAvailable = false;
            }

            // Memory temperature - DDR5 on-module thermal sensors (SPD hub); one entry per populated
            // DIMM. Not available on DDR4 (no SPD thermal sensor via this path). The same pass adds
            // up the PMIC power readings, which come from the same SPD structures.
            try
            {
                var samples = new List<DimmTemperatureSample>();
                double totalWatts = 0;
                bool anyPower = false;

                var spdInfo = cpu.memoryConfig?.SpdInfo;
                var modules = cpu.memoryConfig?.Modules;
                if (spdInfo != null)
                {
                    // Enumeration order matches modules[] (same correlation TelemetryWindow uses),
                    // so index gives the physical slot label. Increment for every entry, valid or not.
                    int index = 0;
                    foreach (var entry in spdInfo)
                    {
                        var thermal = entry.Value.ThermalData;
                        if (thermal != null && thermal.IsValid && thermal.TemperatureC > 0)
                        {
                            string slot = (modules != null && index < modules.Count)
                                ? modules[index]?.Slot
                                : null;
                            samples.Add(new DimmTemperatureSample
                            {
                                Label = $"DIMM{index}",
                                Slot = string.IsNullOrEmpty(slot) ? null : slot,
                                Celsius = thermal.TemperatureC,
                            });
                        }

                        var pmic = entry.Value.PmicData;
                        if (pmic != null && pmic.IsValid && pmic.TotalW > 0)
                        {
                            totalWatts += pmic.TotalW;
                            anyPower = true;
                        }

                        index++;
                    }
                }

                if (samples.Count > 0)
                {
                    mainViewModel.UpdateMemoryTemperatures(samples);
                    mainViewModel.IsMemoryTemperatureAvailable = true;
                }
                else
                {
                    mainViewModel.IsMemoryTemperatureAvailable = false;
                }

                if (anyPower)
                {
                    mainViewModel.MemoryPowerText = $"{totalWatts:F2} W";
                    mainViewModel.IsMemoryPowerAvailable = true;
                }
                else
                {
                    mainViewModel.IsMemoryPowerAvailable = false;
                }
            }
            catch
            {
                mainViewModel.IsMemoryTemperatureAvailable = false;
                mainViewModel.IsMemoryPowerAvailable = false;
            }

            // Base clock (BCLK)
            try
            {
                double? bclk = cpu.GetBclk();
                mainViewModel.BclkString = (bclk.HasValue && bclk.Value > 0)
                    ? $"{bclk.Value:F2}"
                    : "N/A";
            }
            catch
            {
                mainViewModel.BclkString = "N/A";
            }

            ReadTccdl();
            UpdateTrayIcon();
            RefreshWheaCount();
            LogTelemetryRow();
        }

        private readonly WheaMonitor wheaMonitor = new WheaMonitor();
        private DateTime lastWheaPoll = DateTime.MinValue;
        private int wheaBusy;

        /// <summary>
        /// Re-reads the WHEA counters, but far less often than the rest of the loop: the query
        /// walks the System event log, which is orders of magnitude more expensive than reading a
        /// register, and errors do not arrive fast enough for a 2-second cadence to matter.
        /// </summary>
        /// <remarks>
        /// Always queued rather than run inline. This is reached from two directions - the refresh
        /// thread, and once from the constructor, which is the UI thread - and an event-log query
        /// takes tens of milliseconds on a quiet machine and considerably longer on one with a
        /// large System log. The flag keeps a second query out while one is running: the tick
        /// handler starts a fresh thread every time, so two of them can otherwise overlap and race
        /// each other's counters.
        /// </remarks>
        private void RefreshWheaCount()
        {
            // Switched off in Options: skip the query entirely rather than read a counter nothing
            // displays. lastWheaPoll is left alone, so re-enabling picks it up on the next tick.
            if (!settings.ShowWheaCount)
                return;

            if ((DateTime.Now - lastWheaPoll).TotalSeconds < 30)
                return;

            if (Interlocked.CompareExchange(ref wheaBusy, 1, 0) != 0)
                return;

            lastWheaPoll = DateTime.Now;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    wheaMonitor.Refresh();
                    mainViewModel.UpdateWhea(wheaMonitor);
                }
                catch
                {
                    // Never let a log hiccup stop the refresh loop.
                }
                finally
                {
                    Interlocked.Exchange(ref wheaBusy, 0);
                }
            });
        }

        /// <summary>
        /// Reads tCCD_L and tCCD_L_WR2 from the UMC registers, tCCD_L_WR from the APOB.
        /// Values outside a plausible range are treated as "not available" rather than displayed,
        /// because the encoding was derived empirically and may not hold on every AGESA.
        /// </summary>
        private void ReadTccdl()
        {
            if (!mainViewModel.IsTccdlVisible)
                return;

            try
            {
                uint raw198 = cpu.ReadDword(TccdlRegister);
                uint raw2E0 = cpu.ReadDword(TccdlWr2Register);

                uint tccdl = ((raw198 >> 3) & 0x1Fu) + 5u;
                uint tccdlWr2 = (raw2E0 & 0x3Fu) + 7u;

                mainViewModel.TccdlValue = (tccdl >= 8 && tccdl <= 36) ? tccdl : 0;
                mainViewModel.TccdlWr2Value = (tccdlWr2 >= 8 && tccdlWr2 <= 70) ? tccdlWr2 : 0;

                // The APOB run carries all three. Some boards never program 0x50198 from the BIOS
                // setting, so prefer the run where it is found - where the register is right, the
                // two agree anyway.
                uint apobTccdl, apobWr;
                if (TryReadTccdlRunFromApob(mainViewModel.TccdlWr2Value, out apobTccdl, out apobWr))
                {
                    mainViewModel.TccdlValue = apobTccdl;
                    mainViewModel.TccdlWrValue = apobWr;
                }
                else
                {
                    mainViewModel.TccdlWrValue = ReadTccdlWrFromApob(
                        mainViewModel.TccdlValue, mainViewModel.TccdlWr2Value);
                }
            }
            catch
            {
                mainViewModel.TccdlValue = 0;
                mainViewModel.TccdlWr2Value = 0;
                mainViewModel.TccdlWrValue = 0;
            }
        }

        /// <summary>Marks the per-channel frequency record the tCCD_L run belongs to.</summary>
        private const uint ApobRecordMarker0 = 0x5000;
        private const uint ApobRecordMarker1 = 0x00C3;

        /// <summary>Distance from the marker to the first value of the run, in elements.</summary>
        private const int ApobTripleOffset = 9;

        /// <summary>
        /// The whole [tCCD_L, tCCD_L_WR, tCCD_L_WR2] run from the APOB. The marker pair appears once
        /// per channel and the run sits a fixed distance behind it - that held across an AGESA
        /// update which moved the record's contents by 24 bytes, and unlike the timings around the
        /// run the markers are not something a BIOS can change. tCCD_L_WR2 from the UMC is used to
        /// confirm the hit rather than to find it.
        /// </summary>
        private bool TryReadTccdlRunFromApob(uint tccdlWr2, out uint tccdl, out uint tccdlWr)
        {
            tccdl = 0;
            tccdlWr = 0;

            if (tccdlWr2 == 0)
                return false;

            var apob = cpu?.info.apob;
            if (apob == null || !apob.IsAvailable)
                return false;

            byte[] ext = apob.RawExtendedData;
            if (ext == null)
                return false;

            // Zen5 packs the fields as uint16, Zen4 as uint32.
            foreach (int width in new[] { 2, 4 })
            {
                if (TryMarkerScan(ext, width, tccdlWr2, out tccdl, out tccdlWr))
                    return true;
            }

            return false;
        }

        private static bool TryMarkerScan(byte[] ext, int width, uint tccdlWr2, out uint tccdl, out uint tccdlWr)
        {
            tccdl = 0;
            tccdlWr = 0;

            uint? first = null, middle = null;
            int last = (ApobTripleOffset + 3) * width;

            for (var i = 0; i + last <= ext.Length; i += width)
            {
                if (ReadField(ext, i, width) != ApobRecordMarker0) continue;
                if (ReadField(ext, i + width, width) != ApobRecordMarker1) continue;

                int run = i + ApobTripleOffset * width;
                if (ReadField(ext, run + 2 * width, width) != tccdlWr2) continue;

                uint a = ReadField(ext, run, width);
                uint b = ReadField(ext, run + width, width);

                if (a < 8 || a > 36) continue;
                if (b < tccdlWr2 || b > 4 * tccdlWr2) continue;

                // One copy per channel; they have to agree or the match is not trustworthy.
                if (first.HasValue && (first.Value != a || middle.Value != b))
                    return false;

                first = a;
                middle = b;
            }

            if (!first.HasValue)
                return false;

            tccdl = first.Value;
            tccdlWr = middle.Value;
            return true;
        }

        /// <summary>
        /// tCCD_L_WR from the APOB, or 0 if it cannot be located. Fallback for boards where the
        /// run above is not recognised; anchors on tCCD_L and tCCD_L_WR2 instead.
        /// </summary>
        private uint ReadTccdlWrFromApob(uint tccdl, uint tccdlWr2)
        {
            if (tccdl == 0 || tccdlWr2 == 0)
                return 0;

            var apob = cpu?.info.apob;
            if (apob == null || !apob.IsAvailable)
                return 0;

            byte[] ext = apob.RawExtendedData;
            if (ext == null)
                return 0;

            // Zen5 packs the fields as uint16, Zen4 as uint32. Strict first: a strict hit also
            // proves the block holds applied values rather than SPD defaults.
            return FindTccdlWr(ext, tccdl, tccdlWr2, 2, true)
                ?? FindTccdlWr(ext, tccdl, tccdlWr2, 4, true)
                ?? FindTccdlWr(ext, tccdl, tccdlWr2, 2, false)
                ?? FindTccdlWr(ext, tccdl, tccdlWr2, 4, false)
                ?? 0;
        }

        /// <summary>
        /// Finds the [tCCD_L, tCCD_L_WR, tCCD_L_WR2] run at the given element width and returns the
        /// middle value, or null if it is missing or the channels disagree.
        /// </summary>
        /// <remarks>
        /// Strict mode also requires the run's tCCD_L to match the live one. Zen4 fills the run from
        /// the SPD, so loose mode drops that check and matches on tCCD_L_WR2 plus the constant 8 in
        /// front of the run. A loose hit can be an SPD default rather than the applied value.
        /// </remarks>
        private static uint? FindTccdlWr(byte[] ext, uint tccdl, uint tccdlWr2, int width, bool strict)
        {
            uint? found = null;

            for (var i = width; i + 3 * width <= ext.Length; i += width)
            {
                if (ReadField(ext, i + 2 * width, width) != tccdlWr2)
                    continue;

                uint slot = ReadField(ext, i, width);
                bool match = strict
                    ? slot == tccdl
                    : slot >= 8 && slot <= 36 && ReadField(ext, i - width, width) == 8;

                if (!match)
                    continue;

                uint candidate = ReadField(ext, i + width, width);

                // JEDEC derives both from the same terms, so tCCD_L_WR is never below tCCD_L_WR2.
                // The upper bound drops runs that match by coincidence - one dump had a 10479 sat
                // between a valid pair, which used to make the whole search bail out.
                if (candidate < tccdlWr2 || candidate > 4 * tccdlWr2)
                    continue;

                if (found.HasValue && found.Value != candidate)
                    return null;

                found = candidate;
            }

            return found;
        }

        private static uint ReadField(byte[] buffer, int offset, int width)
        {
            return width == 2 ? BitConverter.ToUInt16(buffer, offset) : BitConverter.ToUInt32(buffer, offset);
        }

        /// <summary>Draws the hottest DIMM temperature onto the tray icon when enabled.</summary>
        private void UpdateTrayIcon()
        {
            if (_notifyIcon == null)
                return;

            if (!settings.TrayLiveIcon)
            {
                if (lastTrayText != null)
                {
                    Dispatcher.Invoke(() => RestoreDefaultTrayIcon());
                    lastTrayText = null;
                }
                return;
            }

            double dimm = mainViewModel.HottestDimmTemperature;
            double hottest = dimm > 0 ? dimm : mainViewModel.CpuTemperature;

            if (hottest <= 0)
                return;

            // Hover tooltip always reflects the live value and its source.
            string tip = dimm > 0
                ? $"ZenTimings - DIMM {hottest:F1} °C"
                : $"ZenTimings - CPU {hottest:F1} °C";

            string text = ((int)Math.Round(hottest)).ToString();

            Dispatcher.Invoke(() =>
            {
                try { _notifyIcon.Text = tip; } catch { /* NotifyIcon.Text has a length cap; ignore */ }

                // The drawn glyph only needs redrawing when the rounded number changes.
                if (text == lastTrayText)
                    return;
                lastTrayText = text;

                var icon = TrayIconRenderer.Create(text, TrayInkColor());
                if (icon == null)
                    return;

                _notifyIcon.Icon = icon;

                if (dynamicTrayIcon != null)
                    dynamicTrayIcon.Dispose();

                dynamicTrayIcon = icon;
            });
        }

        /// <summary>
        /// The configured tray ink. The shades are picked to stay legible against a taskbar rather
        /// than to match the theme - the pure primaries GDI+ gives you go muddy at icon size.
        /// </summary>
        private System.Drawing.Color TrayInkColor()
        {
            switch (settings.TrayIconColor)
            {
                case AppSettings.TrayColor.Green:  return System.Drawing.Color.FromArgb(0x3D, 0xE8, 0x6A);
                case AppSettings.TrayColor.Cyan:   return System.Drawing.Color.FromArgb(0x3D, 0xD6, 0xE8);
                case AppSettings.TrayColor.Yellow: return System.Drawing.Color.FromArgb(0xF5, 0xD7, 0x42);
                case AppSettings.TrayColor.Orange: return System.Drawing.Color.FromArgb(0xFF, 0xA0, 0x3A);
                case AppSettings.TrayColor.Red:    return System.Drawing.Color.FromArgb(0xFF, 0x5C, 0x5C);
                case AppSettings.TrayColor.Black:  return System.Drawing.Color.FromArgb(0x10, 0x10, 0x10);
                default:                           return System.Drawing.Color.White;
            }
        }

        private void RestoreDefaultTrayIcon()
        {
            _notifyIcon.Icon = Properties.Resources.ZenTimings2022;

            if (dynamicTrayIcon != null)
            {
                dynamicTrayIcon.Dispose();
                dynamicTrayIcon = null;
            }
        }

        private static readonly string[] LogColumns =
        {
            "Timestamp", "CpuTempC", "DimmTempC", "DimmPowerW",
            "FrequencyMTs", "MCLK", "FCLK", "UCLK",
            "VDDCR_SOC", "MEM_VDD", "MEM_VDDQ", "MEM_VPP",
            "tCL", "tRCDRD", "tRP", "tRAS", "tRFC", "tCCD_L", "tCCD_L_WR2",
        };

        private void LogTelemetryRow()
        {
            if (!telemetryLogger.IsRunning)
                return;

            try
            {
                var timings = mainViewModel.Timings;
                var powerTable = mainViewModel.PowerTable;

                var row = new List<string>
                {
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    TelemetryLogger.Num(mainViewModel.CpuTemperature, "0.0"),
                    TelemetryLogger.Num(mainViewModel.HottestDimmTemperature, "0.0"),
                    mainViewModel.IsMemoryPowerAvailable
                        ? mainViewModel.MemoryPowerText.Replace(" W", "")
                        : "",
                    TelemetryLogger.Num(mainViewModel.MemoryFrequency, "0"),
                    powerTable != null ? TelemetryLogger.Num(powerTable.MCLK, "0.##") : "",
                    powerTable != null ? TelemetryLogger.Num(powerTable.FCLK, "0.##") : "",
                    powerTable != null ? TelemetryLogger.Num(powerTable.UCLK, "0.##") : "",
                    powerTable != null ? TelemetryLogger.Num(powerTable.VDDCR_SOC, "0.####") : "",
                    TelemetryLogger.Num(mainViewModel.SwaAdcV, "0.####"),
                    TelemetryLogger.Num(mainViewModel.SwbAdcV, "0.####"),
                    TelemetryLogger.Num(mainViewModel.VppAdcV, "0.####"),
                    timings != null ? timings.CL.ToString() : "",
                    timings != null ? timings.RCDRD.ToString() : "",
                    timings != null ? timings.RP.ToString() : "",
                    timings != null ? timings.RAS.ToString() : "",
                    timings != null ? timings.RFC.ToString() : "",
                    mainViewModel.TccdlValue > 0 ? mainViewModel.TccdlValue.ToString() : "",
                    mainViewModel.TccdlWr2Value > 0 ? mainViewModel.TccdlWr2Value.ToString() : "",
                };

                telemetryLogger.Write(row);
            }
            catch
            {
                // Logging must never take the refresh loop down with it.
            }
        }

        private void PowerCfgTimer_Tick(object sender, EventArgs e)
        {
            // Run refresh operation in a new thread
            try
            {
                new Thread(() =>
                {
                    Thread.CurrentThread.IsBackground = true;

                    if (AsusWmi != null && AsusWmi.Status == 1)
                    {
                        AsusWmi.UpdateSensors();
                        AsusSensorInfo sensor = AsusWmi.FindSensorByName("DRAM Voltage");
                        if (sensor != null)
                            Dispatcher.Invoke(DispatcherPriority.ApplicationIdle,
                                new Action(() =>
                                {
                                    (timingsPanel as DDR4TimingsPanel).textBoxMemVddio.Text = sensor.Value;
                                    (timingsPanel as DDR4TimingsPanel).labelMemVddio.IsEnabled = true;
                                }));
                    }

                    //ReadDDR4MemoryConfig();
                    cpu.RefreshPowerTable();
                    var voltagesUpdated = false;
                    if (cpu.memoryConfig?.SpdInfo?.Values != null)
                    {
                        voltagesUpdated = cpu.memoryConfig.RefreshTelemetry(settings.AutoRefreshInterval);
                    }

                    UpdateLiveReadouts();

                    Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                    {
                        var newMclk = cpu.powerTable.MCLK;

                        if (newMclk != lastMclk)
                        {
                            var modules = cpu.memoryConfig.Modules;
                            int selectedIndex = comboBoxPartNumber?.SelectedIndex ?? 0;
                            MemoryModule module = modules?.Count > 0 ? modules[selectedIndex] : null;
                            mainViewModel.Timings = ReadTimings(module?.DctOffset ?? 0);
                            //Dictionary<byte, Ddr5SpdInfo> results = Ddr5SpdDecoder.ReadAndDecodeAll(CpuSingleton.Instance.SmbusPiix4);
                        }

                        if (voltagesUpdated)
                            mainViewModel.PmicData = cpu.memoryConfig.SpdInfo.Values.ElementAtOrDefault(comboBoxPartNumber?.SelectedIndex ?? 0)?.PmicData ?? null;

                        lastMclk = newMclk;

                        ReadSVI();
                        // SetFrequencyString();
                        // RefreshSensors();
                    }));
                }).Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private ImageSource GetIcon(string iconSource, double width)
        {
            BitmapDecoder decoder = BitmapDecoder.Create(new Uri(iconSource),
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.OnDemand);

            BitmapFrame result = decoder.Frames.SingleOrDefault(f => f.Width == width);
            if (result == default(BitmapFrame)) result = decoder.Frames.OrderBy(f => f.Width).First();

            return result;
        }

        public void HandleError(string message, string title = "Error")
        {
            MessageBox.Show(
                message,
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }

        private void Restart(bool save = true)
        {
            if (save)
                settings.Save();

            var location = Application.ResourceAssembly.Location;
            var startInfo = new ProcessStartInfo(location)
            {
                UseShellExecute = true,
                Verb = "runas"
            };

            Cleanup();
            Process.Start(startInfo);
            Application.Current.Shutdown();
        }

        private void ShowWindow()
        {
            Show();
            Activate();
            BringIntoView();
            WindowState = WindowState.Normal;
            MinimizeFootprint();
        }

        private static void MinimizeFootprint()
        {
            InteropMethods.EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }

        public void SetWindowTitle()
        {
            string AssemblyTitle = "ZT";

            if (settings.AdvancedMode)
                AssemblyTitle = ((AssemblyTitleAttribute)Attribute.GetCustomAttribute(
                    Assembly.GetExecutingAssembly(),
                    typeof(AssemblyTitleAttribute), false)).Title;

            string AssemblyVersion = ((AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(
                Assembly.GetExecutingAssembly(),
                typeof(AssemblyFileVersionAttribute), false)).Version;

            Dispatcher.Invoke(() =>
            {
                Title = $"{AssemblyTitle} {AssemblyVersion.Substring(0, AssemblyVersion.LastIndexOf('.'))}";
#if DEBUG && !BETA
                if (settings.AdvancedMode)
                    Title += $@"{AssemblyVersion.Substring(AssemblyVersion.LastIndexOf('.'))} (debug)";
#endif

#if BETA
                Title += $@"{AssemblyVersion.Substring(AssemblyVersion.LastIndexOf('.'))} - beta";
#endif

                if (compatMode && settings.AdvancedMode)
                    Title += @" (compatibility)";
            });
        }

        private void Window_Initialized(object sender, EventArgs e)
        {
            //if (settings.SaveWindowPosition)
            //{
            //    WindowStartupLocation = WindowStartupLocation.Manual;

            //    // Get the current screen bounds
            //    System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            //    System.Drawing.Rectangle screenBounds = screen.Bounds;

            //    // Check if the saved window position is outside the screen bounds
            //    if (settings.WindowLeft < screenBounds.Left || settings.WindowLeft + Width > screenBounds.Right ||
            //        settings.WindowTop < screenBounds.Top || settings.WindowTop + Height > screenBounds.Bottom)
            //    {
            //        // Reset the window position to a default value
            //        Left = (screenBounds.Width - Width) / 2 + screenBounds.Left;
            //        Top = (screenBounds.Height - Height) / 2 + screenBounds.Top;
            //    }
            //    else
            //    {
            //        // Set the window position to the saved values
            //        Left = settings.WindowLeft;
            //        Top = settings.WindowTop;
            //    }
            //}            
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == InteropMethods.WM_SHOWME)
                ShowWindow();

            if (msg == HotKeyManager.WM_HOTKEY && wParam.ToInt32() == ScreenshotHotKeyId)
            {
                CaptureBenchmarkScreenshot();
                handled = true;
            }

            if (msg == HotKeyManager.WM_HOTKEY && wParam.ToInt32() == ClipboardHotKeyId)
            {
                CaptureScreenshotToClipboard();
                handled = true;
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Registers or releases the global screenshot hotkey to match the current setting, so the
        /// Options toggle takes effect immediately instead of on the next launch.
        /// </summary>
        public void ApplyHotKeySetting()
        {
            if (!settings.ScreenshotHotkey)
            {
                screenshotHotKey.Unregister();
                clipboardHotKey.Unregister();
                return;
            }

            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
                return;

            // Failure just means another application already owns the combination - not worth a dialog.
            if (!screenshotHotKey.IsRegistered)
                screenshotHotKey.Register(handle, ScreenshotHotKeyId,
                    HotKeyManager.MOD_CONTROL | HotKeyManager.MOD_ALT, HotKeyManager.VK_S);

            // Ctrl+Alt+C puts the same shot straight on the clipboard, for pasting into a chat or
            // a forum post without going via a file.
            if (!clipboardHotKey.IsRegistered)
                clipboardHotKey.Register(handle, ClipboardHotKeyId,
                    HotKeyManager.MOD_CONTROL | HotKeyManager.MOD_ALT, HotKeyManager.VK_C);
        }

        private Windows.OcProfilesWindow ocProfilesWnd = null;

        /// <summary>Fills the OC Profiles menu with one entry per reference profile.</summary>
        private void BuildOcProfilesMenu()
        {
            if (OcProfilesMenu == null)
                return;

            OcProfilesMenu.Items.Clear();

            foreach (var profile in ReferenceProfiles.All)
            {
                var captured = profile;
                var item = new MenuItem { Header = profile.Name };
                item.Click += (s, e) => ShowOcProfiles(captured);
                OcProfilesMenu.Items.Add(item);
            }
        }

        private void ShowOcProfiles(ReferenceProfile profile)
        {
            if (ocProfilesWnd == null || !ocProfilesWnd.IsLoaded)
            {
                ocProfilesWnd = new Windows.OcProfilesWindow(mainViewModel) { Owner = this };
                ocProfilesWnd.Show();
            }
            else
            {
                ocProfilesWnd.Activate();
            }

            ocProfilesWnd.Select(profile);
        }

        private Windows.MemoryLatencyWindow latencyWnd = null;

        private void MemoryLatencyMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (latencyWnd == null || !latencyWnd.IsLoaded)
            {
                latencyWnd = new Windows.MemoryLatencyWindow(mainViewModel) { Owner = this };
                latencyWnd.Show();
            }
            else
            {
                latencyWnd.Activate();
            }
        }

        private void OcToolsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (ocToolsWnd == null || !ocToolsWnd.IsLoaded)
            {
                ocToolsWnd = new OcToolsWindow(mainViewModel, PowerCfgTimer)
                {
                    Owner = this
                };
                ocToolsWnd.Show();
            }
            else
            {
                ocToolsWnd.Activate();
            }
        }

        private void ExportAsJsonMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string json = mainViewModel.GetJSON();

                Forms.SaveFileDialog saveFileDialog = new Forms.SaveFileDialog
                {
                    Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                    DefaultExt = "json",
                    FileName = "ZenTimings-report.json",
                    RestoreDirectory = true
                };

                if (saveFileDialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    File.WriteAllText(saveFileDialog.FileName, json);
                    MessageBox.Show("JSON file exported successfully!", "Export as JSON",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred while exporting: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoggingMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (telemetryLogger.IsRunning)
            {
                string path = telemetryLogger.FilePath;
                long rows = telemetryLogger.RowsWritten;
                telemetryLogger.Stop();
                UpdateLoggingMenuHeader();

                MessageBox.Show(
                    $"Logging stopped.\n\n{rows} row(s) written to:\n{path}",
                    "Telemetry Logging", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!settings.AutoRefresh || !settings.AdvancedMode)
            {
                MessageBox.Show(
                    "Logging writes one row per refresh, so it needs Advanced mode with auto-refresh enabled.",
                    "Telemetry Logging", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var saveFileDialog = new Forms.SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                DefaultExt = "csv",
                FileName = $"ZenTimings-log-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
                RestoreDirectory = true
            };

            if (saveFileDialog.ShowDialog() != Forms.DialogResult.OK)
                return;

            try
            {
                telemetryLogger.Start(saveFileDialog.FileName, LogColumns);
                UpdateLoggingMenuHeader();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not start logging:\n{ex.Message}", "Telemetry Logging",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateLoggingMenuHeader()
        {
            LoggingMenuItem.Header = telemetryLogger.IsRunning
                ? Localization.Loc.T("Menu.StopLogging")
                : Localization.Loc.T("Menu.StartLogging");
        }

        /// <summary>
        /// Hands a picture of this window to <paramref name="consume"/>. Never the active window:
        /// the shortcuts are global, so at the moment one is pressed the active window is the game
        /// or benchmark the user was actually looking at, which is never what they meant to capture.
        /// </summary>
        /// <remarks>
        /// The tray is exactly where ZenTimings sits while a benchmark runs, so a minimised window
        /// is the normal case rather than the odd one, and it is handled by rendering instead of
        /// printing - see <see cref="RenderOwnWindow"/>.
        /// </remarks>
        private void CaptureOwnWindow(Action<System.Drawing.Bitmap> consume)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
                return;

            using (System.Drawing.Bitmap bitmap = (WindowState == WindowState.Minimized)
                ? RenderOwnWindow()
                : WindowCapture.Capture(handle))
            {
                if (bitmap != null)
                    consume(bitmap);
            }
        }

        /// <summary>
        /// Draws the window from its own visual tree rather than from the screen.
        /// </summary>
        /// <remarks>
        /// PrintWindow copies the surface the compositor holds for a window, and a minimised window
        /// has none - it returns success and a blank frame. The visual tree is still laid out while
        /// minimised, so rendering it gives the real picture, needs no restore, and so never pops the
        /// window up over whatever is running fullscreen.
        /// </remarks>
        private System.Drawing.Bitmap RenderOwnWindow()
        {
            try
            {
                PresentationSource presentationSource = PresentationSource.FromVisual(this);
                if (presentationSource == null || ActualWidth < 1 || ActualHeight < 1)
                    return null;

                // The visual tree is in device-independent units; scale to real pixels so the shot
                // matches what PrintWindow produces on the same display.
                double scaleX = presentationSource.CompositionTarget.TransformToDevice.M11;
                double scaleY = presentationSource.CompositionTarget.TransformToDevice.M22;

                var target = new RenderTargetBitmap(
                    (int)Math.Round(ActualWidth * scaleX), (int)Math.Round(ActualHeight * scaleY),
                    96 * scaleX, 96 * scaleY, PixelFormats.Pbgra32);
                target.Render(this);

                using (var buffer = new MemoryStream())
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(target));
                    encoder.Save(buffer);
                    buffer.Position = 0;

                    // Copied out of the stream, because Bitmap keeps the stream alive otherwise.
                    using (var decoded = new System.Drawing.Bitmap(buffer))
                        return new System.Drawing.Bitmap(decoded);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Screenshot with a self-describing filename, saved straight to the configured folder.
        /// Bound to the global hotkey so a shot can be taken without leaving a benchmark.
        /// </summary>
        private void CaptureBenchmarkScreenshot()
        {
            if (settings.ScreenshotMode == AppSettings.ScreenshotType.Desktop)
            {
                try
                {
                    using (Screenshot screenshot = new Screenshot())
                    using (System.Drawing.Bitmap bitmap = screenshot.CaptureDekstop())
                    {
                        if (bitmap != null)
                            SaveScreenshot(bitmap);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }

                return;
            }

            CaptureOwnWindow(SaveScreenshot);
        }

        private void SaveScreenshot(System.Drawing.Bitmap bitmap)
        {
            try
            {
                string directory = settings.ScreenshotSaveLocation;
                if (string.IsNullOrEmpty(directory))
                    directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Screenshots");

                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                string path = Path.Combine(directory, BuildScreenshotName());
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);

                if (_notifyIcon != null && settings.MinimizeToTray && _notifyIcon.Visible)
                    _notifyIcon.ShowBalloonTip(2000, "ZenTimings", "Saved " + Path.GetFileName(path), Forms.ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        /// <summary>
        /// Same capture as the file version, but handed to the clipboard so Ctrl+V pastes the
        /// image directly. Nothing is written to disk.
        /// </summary>
        private void CaptureScreenshotToClipboard()
        {
            CaptureOwnWindow(CopyScreenshotToClipboard);
        }

        private void CopyScreenshotToClipboard(System.Drawing.Bitmap bitmap)
        {
            try
            {
                // Encode to PNG in memory and hand over a frozen BitmapSource: passing the GDI
                // bitmap straight to the clipboard leaks the HBITMAP and loses the alpha channel.
                BitmapSource source;
                using (var buffer = new MemoryStream())
                {
                    bitmap.Save(buffer, System.Drawing.Imaging.ImageFormat.Png);
                    buffer.Position = 0;

                    var decoder = new PngBitmapDecoder(
                        buffer, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    source = decoder.Frames[0];
                }

                source.Freeze();

                // The clipboard is owned by another process for a moment after any app writes to
                // it, so a single failure is normal rather than an error worth reporting.
                try
                {
                    Clipboard.SetImage(source);
                }
                catch
                {
                    Thread.Sleep(80);
                    Clipboard.SetImage(source);
                }

                if (_notifyIcon != null && _notifyIcon.Visible)
                    _notifyIcon.ShowBalloonTip(1500, "ZenTimings", Loc.T("Tray.CopiedToClipboard"), Forms.ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private string BuildScreenshotName()
        {
            var parts = new List<string> { "ZT" };

            if (mainViewModel.MemoryFrequency > 0)
                parts.Add($"{mainViewModel.MemoryFrequency:F0}MTs");

            var timings = mainViewModel.Timings;
            if (timings != null)
                parts.Add($"CL{timings.CL}-{timings.RCDRD}-{timings.RP}-{timings.RAS}");

            if (mainViewModel.SwaAdcV > 0)
                parts.Add($"{mainViewModel.SwaAdcV:F3}V");

            parts.Add(DateTime.Now.ToString("yyyyMMdd-HHmmss"));

            return string.Join("_", parts.ToArray()) + ".png";
        }

        private void DebugToolstripItem_Click(object sender, RoutedEventArgs e)
        {
            if (settings.AdvancedMode)
            {
                Window parent = Application.Current.MainWindow;
                if (parent != null)
                {
                    DebugDialog debugWnd = new DebugDialog(BMC, AsusWmi)
                    {
                        Owner = parent,
                        Width = parent.Width,
                        Height = parent.Height,
                    };
                    debugWnd.Show();
                }
            }
            else
            {
                MessageBoxModel messageBox = new MessageBoxModel
                {
                    Text = "Debug functionality requires Advanced Mode.\n\n" +
                           "Do you want to enable it now (the application will restart automatically)?",
                    Caption = "Debug Report",
                    Buttons = MessageBoxButtons.YesNoCancel()
                };

                MessageBox.Show(messageBox);

                if (messageBox.Result == MessageBoxResult.Yes)
                {
                    settings.AdvancedMode = true;
                    Restart();
                }
            }
        }

        /// <summary>
        /// True while a window that consumes the live hardware feed is still open. Refresh must not
        /// pause for those: the Telemetry window folds every tick into its own min/max/average, so a
        /// frozen feed keeps counting and quietly drags the average toward the last sample read.
        /// </summary>
        private bool HasLiveDependentWindow()
        {
            return (siWnd != null && siWnd.IsLoaded)
                || (telemetryWnd != null && telemetryWnd.IsLoaded)
                || (ocToolsWnd != null && ocToolsWnd.IsLoaded)
                || (latencyWnd != null && latencyWnd.IsLoaded)
                || (ocProfilesWnd != null && ocProfilesWnd.IsLoaded);
        }

        private void AdonisWindow_StateChanged(object sender, EventArgs e)
        {
            // Normally refresh is paused while minimized to save resources. But the live-value tray
            // icon needs fresh temperatures to draw, so keep refreshing when it is enabled.
            if (WindowState == WindowState.Minimized && !HasLiveDependentWindow())
            {
                if (!settings.TrayLiveIcon)
                    StopAutoRefresh();
            }
            else if (WindowState == WindowState.Normal)
                StartAutoRefresh();

            ShowInTaskbar = !(WindowState == WindowState.Minimized && settings.MinimizeToTray);
            UpdateTrayVisibility();
            MinimizeFootprint();
        }

        /// <summary>
        /// The tray icon is shown while minimized-to-tray, and also whenever the live-value tray icon
        /// is enabled - so its temperature readout stays visible even with the window open.
        /// </summary>
        private void UpdateTrayVisibility()
        {
            if (_notifyIcon == null)
                return;

            bool minimizedToTray = WindowState == WindowState.Minimized && settings.MinimizeToTray;
            _notifyIcon.Visible = minimizedToTray || settings.TrayLiveIcon;
        }

        /// <summary>
        /// Applies the readout on/off switches immediately (called from Options on Apply), so the
        /// rows appear and disappear without a restart.
        /// </summary>
        public void ApplyReadoutSettings() => mainViewModel?.RefreshReadoutVisibility();

        /// <summary>Applies the tray live-icon setting immediately (called from Options on Apply).</summary>
        public void ApplyTraySetting()
        {
            UpdateTrayVisibility();
            lastTrayText = null;   // force a redraw on the next call regardless of the cached value
            UpdateTrayIcon();
        }

        private void AdonisWindow_SizeChanged(object sender, SizeChangedEventArgs e) => MinimizeFootprint();

        private void AdonisWindow_Activated(object sender, EventArgs e) => MinimizeFootprint();

        private void ExitToolStripMenuItem_Click(object sender, RoutedEventArgs e) => ExitApplication();

        private void AdonisWindow_Loaded(object sender, RoutedEventArgs e)
        {
            this.Topmost = true;

            RestoreWindowPosition();
            SetWindowTitle();
            //ShowWindow();

            SplashWindow.Stop();

            Application.Current.MainWindow = this;

            this.Topmost = false;

            IntPtr handle = new WindowInteropHelper(Application.Current.MainWindow).Handle;
            HwndSource source = HwndSource.FromHwnd(handle);

            source?.AddHook(WndProc);

            ApplyHotKeySetting();
            UpdateTrayVisibility();
            UpdateLoggingMenuHeader();
            BuildOcProfilesMenu();

            // Decoding the JEDEC ratings means a full SPD transfer over SMBus. Do it once in the
            // background so the first timing tooltip does not stall the UI waiting for it.
            if (memType == MemType.DDR5 || memType == MemType.LPDDR5)
            {
                new Thread(() =>
                {
                    Thread.CurrentThread.IsBackground = true;
                    try { SpdRatedTimings.Prewarm(); }
                    catch (Exception ex) { Console.WriteLine(ex.Message); }
                }).Start();
            }

            //#if !DEBUG
            if (!settings.NotifiedChangelog.Equals(AssemblyVersion))
            {
                Changelog changelogWindow = new Changelog()
                {
                    Owner = Application.Current.MainWindow
                };
                changelogWindow.ShowDialog();
                settings.NotifiedChangelog = AssemblyVersion;
                settings.Save();
            }
            //#endif
            //#if BETA
            //            MessageBox.Show("This is a BETA version of the application. Some functions might be working incorrectly.\n\n" +
            //                    "Please report if something is not working as expected.", "Beta version", MessageBoxButton.OK);
            //#endif
            MinimizeFootprint();

            if (settings.AutoOpenTelemetry && mainViewModel.IsDimmTelemetryAvailable)
                TelemetryMonitorToolstripMenuItem_Click(this, null);

            //new Thread(() =>
            //{
            //    mainViewModel.AgesaVersion = GetAgesaVersion();
            //}).Start();
        }

        private void OptionsToolStripMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (optionsWnd == null || !optionsWnd.IsLoaded)
            {
                optionsWnd = new OptionsDialog(PowerCfgTimer);
                optionsWnd.Show();
            }
            else
            {
                optionsWnd.Activate();
            }
        }

        private void AboutToolStripMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (aboutWnd == null || !aboutWnd.IsLoaded)
            {
                aboutWnd = new AboutDialog()
                {
                    Owner = this
                };
                aboutWnd.Show();
            }
            else
            {
                aboutWnd.Activate();
            }
        }

        private void ButtonScreenshot_Click(object sender, RoutedEventArgs e)
        {
            System.Drawing.Bitmap bitmap;

            if (settings.ScreenshotMode == AppSettings.ScreenshotType.Desktop)
            {
                using (Screenshot screenshot = new Screenshot())
                    bitmap = screenshot.CaptureDekstop();
            }
            else
            {
                // Same window the hotkey captures, so "App Window" means one thing everywhere.
                bitmap = WindowCapture.Capture(new WindowInteropHelper(this).Handle);
            }

            if (bitmap == null)
                return;

            // SaveWindow takes ownership of the bitmap and disposes it.
            using (SaveWindow saveWnd = new SaveWindow(bitmap))
            {
                saveWnd.Owner = Application.Current.MainWindow;
                saveWnd.ShowDialog();
            }
        }

        private void ComboBoxPartNumber_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox combo && combo.Items.Count > 0)
            {
                var dctOffset = cpu.memoryConfig.Modules[combo.SelectedIndex].DctOffset;
                mainViewModel.Timings = ReadTimings(dctOffset);
                //mainViewModel.SelectedDctOffset = dctOffset;
            }
        }

        private void SystemInfoToolstripMenuItem_Click(object sender, RoutedEventArgs e)
        {
            double sysInfoWindowWidth = Width;
            double sysInfoWindowHeight = Height;
            double sysInfoWindowTop = 0;
            double sysInfoWindowLeft = 0;
            WindowStartupLocation location = WindowStartupLocation.CenterScreen;

            if (settings.SaveWindowPosition
                && settings?.SysInfoWindowHeight != 0
                && settings?.SysInfoWindowWidth != 0
                && settings?.SysInfoWindowLeft != -1
                && settings?.SysInfoWindowTop != -1)
            {
                location = WindowStartupLocation.Manual;
                sysInfoWindowLeft = settings.SysInfoWindowLeft;
                sysInfoWindowTop = settings.SysInfoWindowTop;
                sysInfoWindowHeight = settings.SysInfoWindowHeight;
                sysInfoWindowWidth = settings.SysInfoWindowWidth;
            }

            siWnd = new SystemInfoWindow(cpu.memoryConfig, BMC?.Config, AsusWmi?.sensors)
            {
                Width = sysInfoWindowWidth,
                Height = sysInfoWindowHeight,
                WindowStartupLocation = location,
                Top = sysInfoWindowTop,
                Left = sysInfoWindowLeft
            };

            siWnd.Show();
        }

        private void TelemetryMonitorToolstripMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!mainViewModel.IsDimmTelemetryAvailable)
                return;

            try
            {
                double telemetryWindowWidth = 650;
                double telemetryWindowHeight = 550;
                double telemetryWindowTop = 0;
                double telemetryWindowLeft = 0;
                WindowStartupLocation location = WindowStartupLocation.CenterOwner;

                if (settings.SaveWindowPosition
                    && settings?.TelemetryWindowHeight != 0
                    && settings?.TelemetryWindowWidth != 0
                    && settings?.TelemetryWindowLeft != -1
                    && settings?.TelemetryWindowTop != -1)
                {
                    location = WindowStartupLocation.Manual;
                    telemetryWindowLeft = settings.TelemetryWindowLeft;
                    telemetryWindowTop = settings.TelemetryWindowTop;
                    telemetryWindowHeight = settings.TelemetryWindowHeight;
                    telemetryWindowWidth = settings.TelemetryWindowWidth;
                }

                if (telemetryWnd == null || !telemetryWnd.IsLoaded)
                {
                    telemetryWnd = new Windows.TelemetryWindow()
                    {
                        Width = telemetryWindowWidth,
                        Height = telemetryWindowHeight,
                        WindowStartupLocation = location,
                        Top = telemetryWindowTop,
                        Left = telemetryWindowLeft
                    };
                    telemetryWnd.Show();
                }
                else
                {
                    telemetryWnd.Activate();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening Telemetry Monitor:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SpdInfoToolstripMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var spdWindow = new SpdInfoWindow
                {
                    Owner = this
                };
                spdWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening SPD Info:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AdonisWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            siWnd?.Close();

            if (settings.SaveWindowPosition)
            {
                settings.WindowLeft = Left;
                settings.WindowTop = Top;
                settings.Save();
            }

            ExitApplication();
        }

        private void MenuItem_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("https://www.paypal.com/donate/?hosted_button_id=NLSRLE9MVDPCW");
        }

        private void MenuItem_Click_1(object sender, RoutedEventArgs e)
        {
            Process.Start("https://revolut.me/ivanrusanov");
        }

        private void MenuItem_Click_2(object sender, RoutedEventArgs e)
        {
            Process.Start("https://discord.gg/8cfR3UZ");
        }

        private void MenuItem_Click_3(object sender, RoutedEventArgs e)
        {
            Process.Start("https://github.com/irusanov/ZenTimings");
        }
        private void MenuItem_Click_4(object sender, RoutedEventArgs e)
        {
            Process.Start("https://docs.google.com/spreadsheets/d/12zg6yT_H7H-W1voyw1ZoIrj0GSE7WI4Ug-uLlv-Asa8/edit?gid=937453961#gid=937453961");
        }

        private void MenuItem_Click_6(object sender, RoutedEventArgs e)
        {
            Process.Start("https://drive.google.com/drive/folders/1HAJO9_jxvQrIkLb4Ws9ZfKHcHFQ_yOqp?usp=sharing");
        }

        private void ExportToolStripMenuItem_Click(object sender, RoutedEventArgs e)
        {
            //Config Config = new Config(cpu.memoryConfig, BMC.Config/*, cpu.powerTable*/);
            //Console.WriteLine(Config.GetXML());
        }

        private void MotherboardLinkButton_Click(object sender, RoutedEventArgs e)
        {
            var link = VendorUtils.GetMotherboardLink(cpu.systemInfo);
            if (link != null && link.Length > 0)
                Process.Start(link);
        }

        private string GetAgesaVersion()
        {
            if (cpu?.systemInfo == null)
                return "";

            if (!string.IsNullOrEmpty(cpu?.systemInfo.AgesaVersion))
            {
                return cpu.systemInfo.AgesaVersion;
            }

            // TODO: Move to core DLL
            string version = AgesaHelper.FindAgesaVersionInMemory();

            if (!string.IsNullOrEmpty(version))
            {
                cpu.systemInfo.AgesaVersion = version;
            }

            return version;
        }

        private void RestoreWindowPosition()
        {
            if (settings.SaveWindowPosition)
            {
                if (settings?.WindowLeft == -1 || settings?.WindowTop == -1)
                {
                    return;
                }

                WindowStartupLocation = WindowStartupLocation.Manual;

                // Get the current screen bounds
                System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
                System.Drawing.Rectangle screenBounds = screen.Bounds;

                // Check if the saved window position is outside the screen bounds
                if (settings.WindowLeft < screenBounds.Left || settings.WindowLeft + Width > screenBounds.Right ||
                    settings.WindowTop < screenBounds.Top || settings.WindowTop + Height > screenBounds.Bottom)
                {
                    // Reset the window position to a default value
                    Left = (screenBounds.Width - Width) / 2 + screenBounds.Left;
                    Top = (screenBounds.Height - Height) / 2 + screenBounds.Top;
                }
                else
                {
                    // Set the window position to the saved values
                    Left = settings.WindowLeft;
                    Top = settings.WindowTop;
                }
            }
        }

        private void ExportAsHtmlMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Generate HTML content
                string htmlContent = mainViewModel.GetHTML();

                // Open SaveFileDialog to save the HTML file
                Forms.SaveFileDialog saveFileDialog = new Forms.SaveFileDialog
                {
                    Filter = "HTML files (*.html)|*.html|All files (*.*)|*.*",
                    DefaultExt = "html",
                    FileName = "ZenTimings-report.html",
                    RestoreDirectory = true
                };

                if (saveFileDialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    // Write the HTML content to the selected file
                    File.WriteAllText(saveFileDialog.FileName, htmlContent);
                    MessageBox.Show("HTML file exported successfully!", "Export as HTML", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred while exporting: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MenuItem_Click_5(object sender, RoutedEventArgs e)
        {
            try
            {
                var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                var changelogPath = Path.Combine(exeDir, "Changelog.txt");

                if (File.Exists(changelogPath))
                {
                    Process.Start(changelogPath);
                }
                else
                {
                    MessageBox.Show($"Changelog file not found: {changelogPath}", "File not found", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open changelog: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        //protected override void OnSourceInitialized(EventArgs e)
        //{
        //    base.OnSourceInitialized(e);
        //    ApplyNativeBorderBrush(NativeBorderBrush);
        //}

        //private static void OnNativeBorderBrushChanged(
        //    DependencyObject d,
        //    DependencyPropertyChangedEventArgs e)
        //{
        //    var window = (MainWindow)d;
        //    window.ApplyNativeBorderBrush(e.NewValue as Brush);
        //}

        //private void ApplyNativeBorderBrush(Brush brush)
        //{
        //    if (brush is SolidColorBrush scb)
        //    {
        //        uint colorRef = WindowUtils.ToColorRef(
        //            scb.Color.R,
        //            scb.Color.G,
        //            scb.Color.B);

        //        WindowUtils.SetBorderColor(this, colorRef);
        //    }
        //}
    }
}