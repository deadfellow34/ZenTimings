using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ZenStates.Core;
using ZenStates.Core.DRAM;
using static ZenTimings.BiosMemController;

namespace ZenTimings.Windows
{
    /// <summary>
    /// Interaction logic for SystemInfoWindow.xaml
    /// </summary>
    public partial class SystemInfoWindow : ThemedAdonisWindow
    {
        private class GridItem
        {
            public string Name { get; set; }
            public string Value { get; set; }
        }

        public SystemInfoWindow(MemoryConfig mc, Resistances? mcConfig, List<AsusSensorInfo> asusSensors,
            uint tccdl = 0, uint tccdlWr = 0, uint tccdlWr2 = 0)
        {
            InitializeComponent();
            SystemInfo si = CpuSingleton.Instance.systemInfo;
            // Platforms without an AOD table throw somewhere along this chain, and this line runs
            // before the first try block - an unguarded throw here takes the window down on a menu
            // click. The consumer below already treats a null table as "no AOD data".
            AodData aodData = null;
            try { aodData = CpuSingleton.Instance.info.aod.Table.Data; }
            catch { /* no AOD table on this platform */ }
            Type type = si.GetType();
            PropertyInfo[] properties = type.GetProperties();
            List<GridItem> items;

            try
            {
                items = new List<GridItem>
                {
                    new GridItem() {Name = "OS", Value = new Microsoft.VisualBasic.Devices.ComputerInfo().OSFullName}
                };

                foreach (PropertyInfo property in properties)
                    if (property.Name == "CpuId" || property.Name == "PatchLevel" || property.Name == "SmuTableVersion")
                        items.Add(new GridItem() { Name = property.Name, Value = $"{property.GetValue(si, null):X8}" });
                    else if (property.Name == "SmuVersion")
                        items.Add(new GridItem() { Name = property.Name, Value = si.SmuVersionString });
                    else if (property.Name != "SMBios")
                        items.Add(new GridItem()
                        { Name = property.Name, Value = property.GetValue(si, null).ToString() });

                TestGrid.ItemsSource = items;
            }
            catch
            {
                // ignored
            }

            try
            {
                var memConfigs = CpuSingleton.Instance.GetMemoryConfig();
                var allTimings = memConfigs.Timings;
                var props = allTimings[0].Value.GetType().GetProperties();

                // Filter timings to only include unique DctOffset values
                var uniqueTimings = allTimings
                    .GroupBy(t => t.Key)
                    .Select(g => g.First())
                    .ToList();

                // Create dynamic object with properties for each timing column
                var rows = props
                    .Where(p => p.Name != "Item")
                    .Select(property => new
                    {
                        PropertyName = property.Name,
                        Values = uniqueTimings.Select(t => t.Value[property.Name].ToString()).ToArray()
                    })
                    .ToList();

                // The tCCD_L family lives in the APOB, so the reflection above never sees it. One
                // copy per channel and they have to agree, so every DCT column gets the same value.
                if (tccdl > 0)
                {
                    int columns = uniqueTimings.Count;
                    rows.Add(new { PropertyName = "tCCD_L", Values = Enumerable.Repeat(tccdl.ToString(), columns).ToArray() });
                    if (tccdlWr > 0)
                        rows.Add(new { PropertyName = "tCCD_L_WR", Values = Enumerable.Repeat(tccdlWr.ToString(), columns).ToArray() });
                    if (tccdlWr2 > 0)
                        rows.Add(new { PropertyName = "tCCD_L_WR2", Values = Enumerable.Repeat(tccdlWr2.ToString(), columns).ToArray() });
                }

                MemCfgGrid.ItemsSource = rows;

                // Ensure columns exist for each unique timing
                if (MemCfgGrid.Columns.Count < uniqueTimings.Count + 1)
                {
                    MemCfgGrid.Columns.Clear();

                    // Add property name column with default text color
                    var nameColumn = new System.Windows.Controls.DataGridTextColumn
                    {
                        Header = "Name",
                        Binding = new System.Windows.Data.Binding("PropertyName"),
                        Foreground = (System.Windows.Media.Brush)this.FindResource("TextColor"),
                        Width = 150
                    };
                    MemCfgGrid.Columns.Add(nameColumn);

                    // Add column for each unique timing with accent text color
                    for (int i = 0; i < uniqueTimings.Count; i++)
                    {
                        var valueColumn = new System.Windows.Controls.DataGridTextColumn
                        {
                            Header = $"DCT {uniqueTimings[i].Key >> 20}",
                            Binding = new System.Windows.Data.Binding($"Values[{i}]"),
                            Foreground = (System.Windows.Media.Brush)this.FindResource("AccentTextColor")
                        };
                        MemCfgGrid.Columns.Add(valueColumn);
                    }
                }
            }
            catch
            {
                // ignored
            }

            if (mcConfig != null && mc.Type == MemType.DDR4 || mc.Type == MemType.LPDDR4)
            {
                try
                {
                    type = mcConfig.GetType();
                    FieldInfo[] fields = type.GetFields();
                    items = new List<GridItem>();
                    foreach (FieldInfo property in fields)
                        items.Add(new GridItem() { Name = property.Name, Value = property.GetValue(mcConfig).ToString() });

                    MemControllerGrid.ItemsSource = items;
                }
                catch
                {
                    // ignored
                }
            }
            else
            {
                try
                {
                    properties = aodData.GetType().GetProperties();
                    items = new List<GridItem>();
                    foreach (PropertyInfo property in properties)
                    {
                        object value = property.GetValue(aodData);
                        items.Add(new GridItem() { Name = property.Name, Value = $"{value}" });
                    }

                    // The dictionary offsets behind AodData are off by one slot on some AGESA
                    // versions. When the located block is available its values win, so this table
                    // matches the main window. See AodVoltages.
                    AodVoltages located = AodVoltages.Read(CpuSingleton.Instance);
                    if (located != null)
                    {
                        foreach (GridItem item in items)
                        {
                            switch (item.Name)
                            {
                                case "MemVddio": item.Value = AodVoltages.Text(located.Vdd); break;
                                case "MemVddq": item.Value = AodVoltages.Text(located.Vddq); break;
                                case "MemVpp": item.Value = AodVoltages.Text(located.Vpp); break;
                                case "ApuVddio": item.Value = AodVoltages.Text(located.Apu); break;
                            }
                        }
                    }

                    MemControllerGrid.ItemsSource = items;
                }
                catch
                {
                    // ignored
                }
            }

            if (CpuSingleton.Instance.info.apob.IsAvailable)
            {
                try
                {
                    var apobData = CpuSingleton.Instance.info.apob.Data;
                    type = apobData.GetType();
                    properties = type.GetProperties();
                    items = new List<GridItem>();
                    foreach (PropertyInfo property in properties)
                    {
                        object value = property.GetValue(apobData);
                        items.Add(new GridItem() { Name = property.Name, Value = $"{value}" });
                    }

                    // Located in the raw extended block by MainWindow, not decoded by the core.
                    if (tccdl > 0)
                    {
                        items.Add(new GridItem() { Name = "tCCD_L", Value = tccdl.ToString() });
                        if (tccdlWr > 0)
                            items.Add(new GridItem() { Name = "tCCD_L_WR", Value = tccdlWr.ToString() });
                        if (tccdlWr2 > 0)
                            items.Add(new GridItem() { Name = "tCCD_L_WR2", Value = tccdlWr2.ToString() });
                    }

                    ApobTableGrid.ItemsSource = items;
                }
                catch
                {
                    // ignored
                }
            }

            //AsusWmiGrid.ItemsSource = asusSensors;

            DataContext = new
            {
                asusSensors
            };
        }

        private void AdonisWindow_Activated(object sender, EventArgs e)
        {
            // The trim is process-wide, so activating this window empties the benchmark's buffers
            // out of the working set exactly as the main window's would.
            if (BenchmarkSession.Running)
                return;

            InteropMethods.EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
        }

        private void AdonisWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            AppSettings appSettings = AppSettings.Instance;
            if (appSettings.SaveWindowPosition)
            {
                appSettings.SysInfoWindowLeft = Left;
                appSettings.SysInfoWindowTop = Top;
                appSettings.SysInfoWindowHeight = Height;
                appSettings.SysInfoWindowWidth = Width;
                appSettings.Save();
            }
        }
    }
}