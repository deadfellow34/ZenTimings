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

        private static void Put(Dictionary<string, string> values, string key, string value)
        {
            if (!string.IsNullOrEmpty(value))
                values[key] = value;
        }

        private static void AddConfiguration(Dictionary<string, string> values, MainViewModel vm)
        {
            Put(values, "Speed", vm.MemoryFrequencyString);
            Put(values, "Capacity", vm.TotalCapacity != null ? vm.TotalCapacity.ToString() : null);

            var timings = vm.Timings;
            if (timings != null)
            {
                Put(values, "BGS", timings.BGS.ToString());
                Put(values, "BGS Alt", timings.BGSAlt.ToString());
                Put(values, "GDM", timings.GDM.ToString());
                Put(values, "Cmd2T", timings.Cmd2T.ToString());
            }

            var power = vm.PowerTable;
            if (power != null)
            {
                Put(values, "MCLK", Clock(power.MCLK));
                Put(values, "FCLK", Clock(power.FCLK));
                Put(values, "UCLK", Clock(power.UCLK));
            }
        }

        private static void AddTimings(Dictionary<string, string> values, MainViewModel vm)
        {
            var t = vm.Timings;
            if (t == null)
                return;

            Put(values, "tCL", t.CL.ToString());
            Put(values, "tRCDWR", t.RCDWR.ToString());
            Put(values, "tRCDRD", t.RCDRD.ToString());
            Put(values, "tRP", t.RP.ToString());
            Put(values, "tRAS", t.RAS.ToString());
            Put(values, "tRC", t.RC.ToString());
            Put(values, "tRRDS", t.RRDS.ToString());
            Put(values, "tRRDL", t.RRDL.ToString());
            Put(values, "tFAW", t.FAW.ToString());
            Put(values, "tWTRS", t.WTRS.ToString());
            Put(values, "tWTRL", t.WTRL.ToString());
            Put(values, "tWR", t.WR.ToString());
            Put(values, "tRFC (ns)", t.RFCns.ToString());
            Put(values, "tRFC", t.RFC.ToString());
            Put(values, "tRFC2", t.RFC2.ToString());
            // Only on the DDR5 timing types, so it has to go through the actual runtime type.
            Put(values, "tRFCsb", Reflect(t, "RFCsb"));
            Put(values, "tMOD", t.MOD.ToString());
            Put(values, "tMODPDA", t.MODPDA.ToString());
            Put(values, "tPHYWRD", t.PHYWRD.ToString());
            Put(values, "tPHYWRL", t.PHYWRL.ToString());
            Put(values, "tRDPRE", t.RDPRE.ToString());

            Put(values, "tRDRDSCL", t.RDRDSCL.ToString());
            Put(values, "tWRWRSCL", t.WRWRSCL.ToString());
            Put(values, "tCWL", t.CWL.ToString());
            Put(values, "tRTP", t.RTP.ToString());
            Put(values, "tRDWR", t.RDWR.ToString());
            Put(values, "tWRRD", t.WRRD.ToString());
            Put(values, "tRDRDSC", t.RDRDSC.ToString());
            Put(values, "tRDRDSD", t.RDRDSD.ToString());
            Put(values, "tRDRDDD", t.RDRDDD.ToString());
            Put(values, "tWRWRSC", t.WRWRSC.ToString());
            Put(values, "tWRWRSD", t.WRWRSD.ToString());
            Put(values, "tWRWRDD", t.WRWRDD.ToString());
            Put(values, "tCKE", t.CKE.ToString());
            Put(values, "tREFI", t.REFI.ToString());
            Put(values, "Refresh", t.RefreshMode.ToString());
            Put(values, "tSTAG", t.STAG.ToString());
            Put(values, "tMRD", t.MRD.ToString());
            Put(values, "tMRDPDA", t.MRDPDA.ToString());
            Put(values, "tPHYRDL", t.PHYRDL.ToString());
            Put(values, "PowerDown", t.PowerDown.ToString());
            Put(values, "tWRPRE", t.WRPRE.ToString());
            Put(values, "Nitro", Reflect(t, "Nitro"));

            // Located in the APOB; 0 means the run was not found on this platform.
            if (vm.TccdlValue > 0) Put(values, "tCCD_L", vm.TccdlValue.ToString());
            if (vm.TccdlWrValue > 0) Put(values, "tCCD_L_WR", vm.TccdlWrValue.ToString());
            if (vm.TccdlWr2Value > 0) Put(values, "tCCD_L_WR2", vm.TccdlWr2Value.ToString());
        }

        private static void AddVoltages(Dictionary<string, string> values, MainViewModel vm)
        {
            var power = vm.PowerTable;
            if (power != null)
            {
                Put(values, "VSOC (SMU)", Volts(power.VDDCR_SOC));
                Put(values, "CLDO VDDP", Volts(power.CLDO_VDDP));
                Put(values, "VDDG CCD", Volts(power.CLDO_VDDG_CCD));
                Put(values, "VDDG IOD", Volts(power.CLDO_VDDG_IOD));
                Put(values, "VDD MISC", Volts(power.VDD_MISC));
            }

            // From the on-module PMIC, not the power table.
            Put(values, "MEM VDD", Volts(vm.SwaAdcV));
            Put(values, "MEM VDDQ", Volts(vm.SwbAdcV));
            Put(values, "MEM VPP", Volts(vm.VppAdcV));
        }

        private static void AddImpedances(Dictionary<string, string> values, MainViewModel vm)
        {
            // Two tables of the same type: the legacy one and the extended one. Which is populated
            // depends on the panel this build selected, and ApobData is a struct so neither can be
            // null-checked - take whichever side actually has a value, field by field.
            var apob = vm.ApobData;
            var ext = vm.ApobExtendedData;

            Put(values, "ProcOdt", Pick(apob.ProcOdt, ext.ProcOdt));
            Put(values, "ProcOdt Pu", Pick(apob.ProcOdtPullUpP0, ext.ProcOdtPullUpP0));
            Put(values, "ProcOdt Pd", Pick(apob.ProcOdtPullDownP0, ext.ProcOdtPullDownP0));

            Put(values, "ProcCsDs", Pick(apob.ProcCsDs, ext.ProcCsDs));
            Put(values, "ProcCkDs", Pick(apob.ProcCkDs, ext.ProcCkDs));
            Put(values, "ProcCaDs", Pick(apob.ProcCaDs, ext.ProcCaDs));

            Put(values, "ProcDqDs", Pick(apob.ProcDqDs, ext.ProcDqDs));
            Put(values, "ProcDqDs Pu", Pick(apob.ProcDqDsPullUpP0, ext.ProcDqDsPullUpP0));
            Put(values, "ProcDqDs Pd", Pick(apob.ProcDqDsPullDownP0, ext.ProcDqDsPullDownP0));
            Put(values, "DramDqDs", Pick(apob.DramDataDs, ext.DramDataDs));
            Put(values, "DramDqDs Pu", Pick(apob.DramDqDsPullUpP0, ext.DramDqDsPullUpP0));
            Put(values, "DramDqDs Pd", Pick(apob.DramDqDsPullDownP0, ext.DramDqDsPullDownP0));

            Put(values, "RttNomWr", Pick(apob.RttNomWrP0, ext.RttNomWr));
            Put(values, "RttNomRd", Pick(apob.RttNomRdP0, ext.RttNomRd));
            Put(values, "RttWr", Pick(apob.RttWrP0, ext.RttWr));
            Put(values, "RttPark", Pick(apob.RttParkP0, ext.RttPark));
            Put(values, "RttParkDqs", Pick(apob.RttParkDqsP0, ext.RttParkDqs));

            Put(values, "CA ODT A", Pick(apob.CaOdtA, ext.CaOdtA));
            Put(values, "CA ODT B", Pick(apob.CaOdtB, ext.CaOdtB));
            Put(values, "CK ODT A", Pick(apob.CkOdtA, ext.CkOdtA));
            Put(values, "CK ODT B", Pick(apob.CkOdtB, ext.CkOdtB));
            Put(values, "CS ODT A", Pick(apob.CsOdtA, ext.CsOdtA));
            Put(values, "CS ODT B", Pick(apob.CsOdtB, ext.CsOdtB));
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
