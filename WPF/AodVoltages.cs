using System;
using System.Globalization;
using ZenStates.Core;

namespace ZenTimings
{
    /// <summary>
    /// The four memory voltages out of the raw AOD table, located by validating the block instead
    /// of trusting the offset that comes with the AGESA version. On some versions the dictionary is
    /// off by one slot, which reads VDDQ and VPP swapped and loses CPU VDDIO entirely.
    /// </summary>
    internal sealed class AodVoltages
    {
        // The dictionaries place the block between 9084 and 9156, but those offsets are computed
        // from a LastOffset that itself is found by searching the table, so the surrounding region
        // is swept instead of a fixed list.
        private const int RegionStart = 9072;
        private const int RegionEnd = 9160;

        public uint Vdd { get; private set; }
        public uint Vddq { get; private set; }
        public uint Vpp { get; private set; }
        public uint Apu { get; private set; }
        public int Offset { get; private set; }

        /// <summary>CPU VDDIO, from the ApuVddio slot of the located block. Never MemVddio - that is
        /// the DRAM rail and it already has its own readout.</summary>
        public uint Vddio { get { return Apu; } }

        public static AodVoltages Read(Cpu cpu)
        {
            if (cpu == null)
                return null;

            return Read(cpu.info.aod?.Table?.RawAodTable);
        }

        internal static AodVoltages Read(byte[] table)
        {
            if (table == null)
                return null;

            AodVoltages found = null;

            for (int offset = RegionStart; offset <= RegionEnd; offset += 4)
            {
                if (offset + 16 > table.Length)
                    break;

                uint vdd = BitConverter.ToUInt32(table, offset);
                uint vddq = BitConverter.ToUInt32(table, offset + 4);
                uint vpp = BitConverter.ToUInt32(table, offset + 8);
                uint apu = BitConverter.ToUInt32(table, offset + 12);

                // VPP is the anchor: always well above the other two. VDD stays unchecked - AGESA
                // zeroes it past the 1.435 V JEDEC VID ceiling on some boards and stores it raw on
                // others. The ApuVddio bound is what rejects the window shifted back by one slot,
                // where a VDDQ above 1.7 V reads as VPP and the real VPP lands in the ApuVddio spot.
                if (vpp < 1700 || vpp > 2000) continue;
                if (vddq < 1000 || vddq > 1680) continue;
                if (vpp <= vddq) continue;
                if (apu != 0 && (apu < 600 || apu > 1700)) continue;

                // A second hit means the block cannot be told apart - leave it to AodData.
                if (found != null)
                    return null;

                found = new AodVoltages
                {
                    Vdd = vdd,
                    Vddq = vddq,
                    Vpp = vpp,
                    Apu = apu,
                    Offset = offset,
                };
            }

            return found;
        }

        public static string VddioText(Cpu cpu, AodData fallback)
        {
            return VddioText(Read(cpu), fallback);
        }

        internal static string VddioText(AodVoltages v, AodData fallback)
        {
            // Once the block is located its slot is the answer, zero included - a zero there means
            // AGESA never wrote the value, not that the rail is at 0 V. Same for the fallback.
            if (v != null)
                return Text(v.Vddio);

            return fallback?.ApuVddio != null && fallback.ApuVddio.RawValue > 0
                ? fallback.ApuVddio.ToString()
                : "N/A";
        }

        /// <summary>
        /// Millivolts the way the core's Voltage.ToString writes them - en-US, four decimals - so
        /// a rail read out of the table and one read off a panel cannot look different.
        /// </summary>
        public static string Text(uint millivolts)
        {
            return millivolts > 0
                ? string.Format(CultureInfo.GetCultureInfo("en-US"), "{0:F4}V", millivolts / 1000.0)
                : "N/A";
        }
    }
}
