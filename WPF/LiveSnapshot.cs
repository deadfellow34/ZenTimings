using System;
using System.Collections.Generic;
using System.Globalization;
using ZenTimings.ViewModels;

namespace ZenTimings
{
    /// <summary>
    /// What this machine reads right now, keyed by the same labels the reference profiles use.
    /// </summary>
    /// <remarks>
    /// Keyed by the on-screen label rather than by the underlying property name so a profile can be
    /// written the way the app displays things. Formatting has to match the panels exactly -
    /// "1.2500V", "40.0 Ω" - otherwise identical settings would read as differences.
    /// </remarks>
    public static class LiveSnapshot
    {
        /// <summary>Shown when this platform does not expose the value at all.</summary>
        public const string Missing = "-";

        /// <summary>The four panels the keys come from, in the order a report reads them.</summary>
        public const string Configuration = "Configuration";
        public const string Voltages = "Voltages";
        public const string Timings = "Timings";
        public const string Impedances = "Impedances";
        public const string Other = "Other";

        // Filled as the keys are written, so the grouping cannot drift away from them the way a
        // second hand-kept list would. A key this machine never writes reads as Other, which is
        // where a report puts what it does not recognise anyway.
        private static readonly Dictionary<string, string> Groups =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The panel a key came from; <see cref="Other"/> for anything unrecognised.</summary>
        public static string GroupOf(string key)
        {
            if (key == null)
                return Other;

            lock (Groups)
            {
                string group;
                return Groups.TryGetValue(key, out group) ? group : Other;
            }
        }

        public static Dictionary<string, string> Build(MainViewModel vm)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (vm == null)
                return values;

            AddConfiguration(values, vm);
            AddTimings(values, vm);
            AddVoltages(values, vm);
            AddImpedances(values, vm);

            return values;
        }

        /// <remarks>
        /// Call it even when there is no value: the key still has to be filed, or a report of a run
        /// loaded from the file lays that row out under whatever this session happened to read.
        /// A null or empty value only skips the value, never the filing.
        /// </remarks>
        private static void Put(Dictionary<string, string> values, string group, string key, string value)
        {
            lock (Groups)
                Groups[key] = group;

            if (!string.IsNullOrEmpty(value))
                values[key] = value;
        }

        private static void AddConfiguration(Dictionary<string, string> values, MainViewModel vm)
        {
            Put(values, Configuration, "Speed", vm.MemoryFrequencyString);
            Put(values, Configuration, "Capacity", vm.TotalCapacity != null ? vm.TotalCapacity.ToString() : null);
            Put(values, Configuration, "Modules", ModuleNames());

            var timings = vm.Timings;
            Put(values, Configuration, "BGS", timings != null ? timings.BGS.ToString() : null);
            Put(values, Configuration, "BGS Alt", timings != null ? timings.BGSAlt.ToString() : null);
            Put(values, Configuration, "GDM", timings != null ? timings.GDM.ToString() : null);
            Put(values, Configuration, "Cmd2T", timings != null ? timings.Cmd2T.ToString() : null);

            var power = vm.PowerTable;
            Put(values, Configuration, "MCLK", power != null ? Clock(power.MCLK) : null);
            Put(values, Configuration, "FCLK", power != null ? Clock(power.FCLK) : null);
            Put(values, Configuration, "UCLK", power != null ? Clock(power.UCLK) : null);
        }

        private static void AddTimings(Dictionary<string, string> values, MainViewModel vm)
        {
            // No early return on a null timings object: t?. turns every read into an absent value
            // instead of a skipped call, so the forty keys are still filed.
            var t = vm.Timings;

            Put(values, Timings, "tCL", t?.CL.ToString());
            Put(values, Timings, "tRCDWR", t?.RCDWR.ToString());
            Put(values, Timings, "tRCDRD", t?.RCDRD.ToString());
            Put(values, Timings, "tRP", t?.RP.ToString());
            Put(values, Timings, "tRAS", t?.RAS.ToString());
            Put(values, Timings, "tRC", t?.RC.ToString());
            Put(values, Timings, "tRRDS", t?.RRDS.ToString());
            Put(values, Timings, "tRRDL", t?.RRDL.ToString());
            Put(values, Timings, "tFAW", t?.FAW.ToString());
            Put(values, Timings, "tWTRS", t?.WTRS.ToString());
            Put(values, Timings, "tWTRL", t?.WTRL.ToString());
            Put(values, Timings, "tWR", t?.WR.ToString());
            Put(values, Timings, "tRFC (ns)", t?.RFCns.ToString());
            Put(values, Timings, "tRFC", t?.RFC.ToString());
            Put(values, Timings, "tRFC2", t?.RFC2.ToString());
            // Only on the DDR5 timing types, so it has to go through the actual runtime type.
            Put(values, Timings, "tRFCsb", Reflect(t, "RFCsb"));
            Put(values, Timings, "tMOD", t?.MOD.ToString());
            Put(values, Timings, "tMODPDA", t?.MODPDA.ToString());
            Put(values, Timings, "tPHYWRD", t?.PHYWRD.ToString());
            Put(values, Timings, "tPHYWRL", t?.PHYWRL.ToString());
            Put(values, Timings, "tRDPRE", t?.RDPRE.ToString());

            Put(values, Timings, "tRDRDSCL", t?.RDRDSCL.ToString());
            Put(values, Timings, "tWRWRSCL", t?.WRWRSCL.ToString());
            Put(values, Timings, "tCWL", t?.CWL.ToString());
            Put(values, Timings, "tRTP", t?.RTP.ToString());
            Put(values, Timings, "tRDWR", t?.RDWR.ToString());
            Put(values, Timings, "tWRRD", t?.WRRD.ToString());
            Put(values, Timings, "tRDRDSC", t?.RDRDSC.ToString());
            Put(values, Timings, "tRDRDSD", t?.RDRDSD.ToString());
            Put(values, Timings, "tRDRDDD", t?.RDRDDD.ToString());
            Put(values, Timings, "tWRWRSC", t?.WRWRSC.ToString());
            Put(values, Timings, "tWRWRSD", t?.WRWRSD.ToString());
            Put(values, Timings, "tWRWRDD", t?.WRWRDD.ToString());
            Put(values, Timings, "tCKE", t?.CKE.ToString());
            Put(values, Timings, "tREFI", t?.REFI.ToString());
            Put(values, Timings, "Refresh", t?.RefreshMode.ToString());
            Put(values, Timings, "tSTAG", t?.STAG.ToString());
            Put(values, Timings, "tMRD", t?.MRD.ToString());
            Put(values, Timings, "tMRDPDA", t?.MRDPDA.ToString());
            Put(values, Timings, "tPHYRDL", t?.PHYRDL.ToString());
            Put(values, Timings, "PowerDown", t?.PowerDown.ToString());
            Put(values, Timings, "tWRPRE", t?.WRPRE.ToString());
            Put(values, Timings, "Nitro", Reflect(t, "Nitro"));

            // Located in the APOB; 0 means the run was not found on this platform.
            Put(values, Timings, "tCCD_L", vm.TccdlValue > 0 ? vm.TccdlValue.ToString() : null);
            Put(values, Timings, "tCCD_L_WR", vm.TccdlWrValue > 0 ? vm.TccdlWrValue.ToString() : null);
            Put(values, Timings, "tCCD_L_WR2", vm.TccdlWr2Value > 0 ? vm.TccdlWr2Value.ToString() : null);
        }

        private static void AddVoltages(Dictionary<string, string> values, MainViewModel vm)
        {
            var power = vm.PowerTable;
            Put(values, Voltages, "VSOC (SMU)", power != null ? Volts(power.VDDCR_SOC) : null);
            Put(values, Voltages, "CLDO VDDP", power != null ? Volts(power.CLDO_VDDP) : null);
            Put(values, Voltages, "VDDG CCD", power != null ? Volts(power.CLDO_VDDG_CCD) : null);
            Put(values, Voltages, "VDDG IOD", power != null ? Volts(power.CLDO_VDDG_IOD) : null);
            Put(values, Voltages, "VDD MISC", power != null ? Volts(power.VDD_MISC) : null);

            // From the on-module PMIC, not the power table.
            Put(values, Voltages, "MEM VDD", Volts(vm.SwaAdcV));
            Put(values, Voltages, "MEM VDDQ", Volts(vm.SwbAdcV));
            Put(values, Voltages, "MEM VPP", Volts(vm.VppAdcV));

            // The located AOD block - the only source that carries this rail.
            string vddio = null;
            try
            {
                var cpu = CpuSingleton.Instance;
                vddio = AodVoltages.VddioText(cpu, cpu.info.aod?.Table?.Data);
            }
            catch
            {
                // No AOD on this platform.
            }

            Put(values, Voltages, "CPU VDDIO", vddio != "N/A" ? vddio : null);
        }

        private static void AddImpedances(Dictionary<string, string> values, MainViewModel vm)
        {
            // Two tables of the same type: the legacy one and the extended one. Which is populated
            // depends on the panel this build selected, and ApobData is a struct so neither can be
            // null-checked - take whichever side actually has a value, field by field.
            var apob = vm.ApobData;
            var ext = vm.ApobExtendedData;

            Put(values, Impedances, "ProcOdt", Pick(apob.ProcOdt, ext.ProcOdt));
            Put(values, Impedances, "ProcOdt Pu", Pick(apob.ProcOdtPullUpP0, ext.ProcOdtPullUpP0));
            Put(values, Impedances, "ProcOdt Pd", Pick(apob.ProcOdtPullDownP0, ext.ProcOdtPullDownP0));

            Put(values, Impedances, "ProcCsDs", Pick(apob.ProcCsDs, ext.ProcCsDs));
            Put(values, Impedances, "ProcCkDs", Pick(apob.ProcCkDs, ext.ProcCkDs));
            Put(values, Impedances, "ProcCaDs", Pick(apob.ProcCaDs, ext.ProcCaDs));

            Put(values, Impedances, "ProcDqDs", Pick(apob.ProcDqDs, ext.ProcDqDs));
            Put(values, Impedances, "ProcDqDs Pu", Pick(apob.ProcDqDsPullUpP0, ext.ProcDqDsPullUpP0));
            Put(values, Impedances, "ProcDqDs Pd", Pick(apob.ProcDqDsPullDownP0, ext.ProcDqDsPullDownP0));
            Put(values, Impedances, "DramDqDs", Pick(apob.DramDataDs, ext.DramDataDs));
            Put(values, Impedances, "DramDqDs Pu", Pick(apob.DramDqDsPullUpP0, ext.DramDqDsPullUpP0));
            Put(values, Impedances, "DramDqDs Pd", Pick(apob.DramDqDsPullDownP0, ext.DramDqDsPullDownP0));

            Put(values, Impedances, "RttNomWr", Pick(apob.RttNomWrP0, ext.RttNomWr));
            Put(values, Impedances, "RttNomRd", Pick(apob.RttNomRdP0, ext.RttNomRd));
            Put(values, Impedances, "RttWr", Pick(apob.RttWrP0, ext.RttWr));
            Put(values, Impedances, "RttPark", Pick(apob.RttParkP0, ext.RttPark));
            Put(values, Impedances, "RttParkDqs", Pick(apob.RttParkDqsP0, ext.RttParkDqs));

            Put(values, Impedances, "CA ODT A", Pick(apob.CaOdtA, ext.CaOdtA));
            Put(values, Impedances, "CA ODT B", Pick(apob.CaOdtB, ext.CaOdtB));
            Put(values, Impedances, "CK ODT A", Pick(apob.CkOdtA, ext.CkOdtA));
            Put(values, Impedances, "CK ODT B", Pick(apob.CkOdtB, ext.CkOdtB));
            Put(values, Impedances, "CS ODT A", Pick(apob.CsOdtA, ext.CsOdtA));
            Put(values, Impedances, "CS ODT B", Pick(apob.CsOdtB, ext.CsOdtB));
        }

        /// <summary>"2 x KF560C36-16" - the sticks themselves, grouped by part number.</summary>
        private static string ModuleNames()
        {
            try
            {
                var modules = CpuSingleton.Instance.GetMemoryConfig().Modules;
                if (modules == null || modules.Count == 0)
                    return null;

                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var module in modules)
                {
                    string name = module != null ? module.PartNumber : null;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    int count;
                    counts.TryGetValue(name, out count);
                    counts[name] = count + 1;
                }

                if (counts.Count == 0)
                    return null;

                var parts = new List<string>();
                foreach (var pair in counts)
                    parts.Add(pair.Value + " x " + pair.Key);
                return string.Join(" + ", parts);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Reads a property that only exists on some of the timing types. The panels get away with
        /// naming it directly because XAML binding resolves against the runtime type.
        /// </summary>
        private static string Reflect(object source, string property)
        {
            if (source == null)
                return null;

            try
            {
                var prop = source.GetType().GetProperty(property);
                return prop == null ? null : Text(prop.GetValue(source, null));
            }
            catch
            {
                return null;
            }
        }

        private static string Pick(object preferred, object fallback)
        {
            return Text(preferred) ?? Text(fallback);
        }

        private static string Text(object value)
        {
            if (value == null)
                return null;

            string text = value.ToString();
            return string.IsNullOrWhiteSpace(text) || text == "N/A" ? null : text;
        }

        /// <summary>Matches FloatToVoltageConverter, which is what the panels render.</summary>
        private static string Volts(float value)
        {
            return value == 0 ? null : value.ToString("F4", CultureInfo.InvariantCulture) + "V";
        }

        private static string Clock(float value)
        {
            return value == 0 ? null : value.ToString("F2", CultureInfo.InvariantCulture);
        }
    }
}
