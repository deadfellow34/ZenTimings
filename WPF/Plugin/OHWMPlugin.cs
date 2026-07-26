using System;
using System.Collections.Generic;
using System.Reflection;
using ZenTimings.Common;

namespace ZenTimings.Plugin
{
    /// <summary>
    /// Reads motherboard Super-I/O voltages through a hardware-monitor library, used as the last
    /// resort for DDR4 VDIMM when the ACPI/APCB and ASUS-WMI paths both come up empty.
    ///
    /// Prefers LibreHardwareMonitor (actively maintained, knows current AM5 Super-I/O chips) and
    /// falls back to the original OpenHardwareMonitor DLL. Everything goes through reflection, so
    /// neither library is a build dependency - whichever DLL is next to the exe gets used, and if
    /// neither is there the plugin simply reports itself unavailable.
    /// </summary>
    public class OHWMPlugin : IPlugin
    {
        private class Backend
        {
            public string AssemblyName;
            public string ComputerType;
            public string EnableProperty;
            public string MotherboardTypeName;
        }

        // Ordered by preference.
        private static readonly Backend[] Backends =
        {
            new Backend
            {
                AssemblyName = "LibreHardwareMonitorLib.dll",
                ComputerType = "LibreHardwareMonitor.Hardware.Computer",
                EnableProperty = "IsMotherboardEnabled",
                MotherboardTypeName = "Motherboard",
            },
            new Backend
            {
                AssemblyName = "OpenHardwareMonitorLib.dll",
                ComputerType = "OpenHardwareMonitor.Hardware.Computer",
                EnableProperty = "MainboardEnabled",
                MotherboardTypeName = "Mainboard",
            },
        };

        private object computer;
        private Assembly assembly;
        private Backend backend;

        public string Name => "Hardware Monitor Plugin";

        public string Description => backend != null
            ? "Super-I/O voltages via " + backend.AssemblyName
            : "Super-I/O voltages (no hardware monitor library found)";

        public string Author => "Ivan Rusanov";

        public string Version => "1.1";

        public List<Sensor> Sensors { get; internal set; }

        /// <summary>True only once a library loaded and its Computer instance was created.</summary>
        public bool IsAvailable => computer != null && assembly != null;

        /// <summary>Which library ended up being used, for the debug report.</summary>
        public string BackendName => backend != null ? backend.AssemblyName : null;

        public OHWMPlugin()
        {
            foreach (var candidate in Backends)
            {
                try
                {
                    var loaded = Assembly.LoadFrom(candidate.AssemblyName);
                    Type type = loaded.GetType(candidate.ComputerType);
                    if (type == null)
                        continue;

                    object instance = Activator.CreateInstance(type);
                    if (instance == null)
                        continue;

                    assembly = loaded;
                    computer = instance;
                    backend = candidate;
                    return;
                }
                catch (Exception ex)
                {
                    // Missing DLL / wrong bitness / blocked ring0 driver - try the next backend.
                    Console.WriteLine($"{candidate.AssemblyName}: {ex.Message}");
                }
            }

            Reset();
        }

        public void Open()
        {
            Sensors = new List<Sensor>();

            if (!IsAvailable)
                return;

            SetProperty(computer, backend.EnableProperty, true);
            Invoke(computer, "Open");

            ForEachVoltageSensor((name, index, value) =>
            {
                Sensors.Add(new Sensor(name, index) { Value = value });
            });
        }

        public bool Update()
        {
            if (!IsAvailable || Sensors == null)
                return false;

            // Match on the sensor's own Index, not on list position: the indices reported by the
            // library are not guaranteed to be contiguous or to start at zero.
            var byIndex = new Dictionary<int, Sensor>();
            foreach (var sensor in Sensors)
            {
                if (!byIndex.ContainsKey(sensor.Index))
                    byIndex[sensor.Index] = sensor;
            }

            bool updated = false;

            ForEachVoltageSensor((name, index, value) =>
            {
                Sensor sensor;
                if (byIndex.TryGetValue(index, out sensor))
                {
                    sensor.Value = value;
                    updated = true;
                }
            });

            return updated;
        }

        public void Close()
        {
            try
            {
                if (computer != null)
                    Invoke(computer, "Close");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            Reset();
        }

        private void Reset()
        {
            computer = null;
            assembly = null;
            backend = null;
        }

        private void ForEachVoltageSensor(Action<string, int, float> callback)
        {
            var hardwareList = GetPropValue(computer, "Hardware") as System.Collections.IEnumerable;
            if (hardwareList == null)
                return;

            foreach (var hardware in hardwareList)
            {
                if (!string.Equals(Convert.ToString(GetPropValue(hardware, "HardwareType")),
                        backend.MotherboardTypeName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var subHardwareList = GetPropValue(hardware, "SubHardware") as System.Collections.IEnumerable;
                if (subHardwareList == null)
                    continue;

                foreach (var subHardware in subHardwareList)
                {
                    Invoke(subHardware, "Update");

                    var sensors = GetPropValue(subHardware, "Sensors") as System.Collections.IEnumerable;
                    if (sensors == null)
                        continue;

                    foreach (var sensor in sensors)
                    {
                        if (!string.Equals(Convert.ToString(GetPropValue(sensor, "SensorType")),
                                "Voltage", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        try
                        {
                            string name = Convert.ToString(GetPropValue(sensor, "Name"));
                            int index = Convert.ToInt32(GetPropValue(sensor, "Index") ?? 0);
                            object raw = GetPropValue(sensor, "Value");
                            float value = raw == null ? 0f : Convert.ToSingle(raw);

                            callback(name, index, value);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.Message);
                        }
                    }
                }
            }
        }

        private static object GetPropValue(object source, string propertyName)
        {
            if (source == null)
                return null;

            try
            {
                return source.GetType().GetProperty(propertyName)?.GetValue(source, null);
            }
            catch
            {
                return null;
            }
        }

        private static void SetProperty(object source, string propertyName, object value)
        {
            if (source == null)
                return;

            try
            {
                source.GetType().GetProperty(propertyName)?.SetValue(source, value, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private static void Invoke(object source, string methodName)
        {
            if (source == null)
                return;

            try
            {
                source.GetType().InvokeMember(
                    methodName,
                    BindingFlags.Default | BindingFlags.InvokeMethod,
                    null,
                    source,
                    null);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
    }
}
