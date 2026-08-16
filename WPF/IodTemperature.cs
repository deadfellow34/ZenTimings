using System;
using ZenStates.Core;

namespace ZenTimings
{
    /// <summary>
    /// I/O die temperature and its hotspot, out of the SMU power table.
    /// </summary>
    /// <remarks>
    /// Neither has a register of its own, and the offsets are per table version - the layouts
    /// move between families (the Zen 4 tables hold MCLK where Zen 5 keeps the average), which
    /// is why an unknown version reads as "no sensor" rather than as a guess.
    ///
    /// Zen 5 (0x62xxxx): 0x1A8 was confirmed against a Gigabyte X870 Aorus Elite reporting
    /// 41.2 C: it read 41.40. The hotspot took longer. 0x458 looks like a copy of Tctl at idle -
    /// on that same board both read 44.48 - but the two separate under load: one dump has Tctl at
    /// 69.21 while 0x458 reads 56.87 and the die average 52.32, which is the order these three
    /// have to come in. Across eleven Zen 5 dumps 0x458 tracks the average at r=0.99, always 3 to
    /// 7 C above it, and it is the only field in the table that does. The neighbouring 0x438 was
    /// the first guess and is wrong: it reads 91.88 in the loaded dump, above the package maximum.
    ///
    /// Raphael (0x540104): read against a 7700X dump taken alongside HWiNFO. 0x188 sits just
    /// before the FCLK/MCLK/UCLK block and read 44.34 against a rock-stable "IOD Average" of
    /// 44.1; 0x34C read 47.97 against a hotspot of 48.0. The near-miss twins are what the
    /// anchors were for: the three stable VRM temperatures pinned 0xC8/0xDC/0xF0 (46.2/48.0/41.0),
    /// so the 48.04 at 0xDC is the SOC VRM and not the hotspot, and 0x2C - 49.31 behind an 85.0
    /// limit - sat 1.3 C off the live reading while 0x34C matched it to 0.03.
    /// </remarks>
    internal static class IodTemperature
    {
        /// <summary>Anything outside this is not a die temperature, whatever the table holds.</summary>
        private const float MinCelsius = 5.0f;
        private const float MaxCelsius = 125.0f;

        private sealed class Layout
        {
            public uint Version;
            public int Average;
            public int Hotspot;
        }

        /// <summary>
        /// The exact table versions the offsets were read on. Matching the family alone would be
        /// a bet that the next revision keeps the same layout, and the layouts demonstrably move.
        /// </summary>
        private static readonly Layout[] Layouts =
        {
            new Layout { Version = 0x00620105, Average = 0x1A8 / 4, Hotspot = 0x458 / 4 },
            new Layout { Version = 0x00620205, Average = 0x1A8 / 4, Hotspot = 0x458 / 4 },
            new Layout { Version = 0x00540104, Average = 0x188 / 4, Hotspot = 0x34C / 4 },
        };

        public static bool TryRead(Cpu cpu, out float average, out float hotspot)
        {
            average = 0;
            hotspot = 0;

            if (cpu == null || cpu.systemInfo == null)
                return false;

            uint version = cpu.systemInfo.SmuTableVersion;
            Layout layout = null;
            foreach (var known in Layouts)
                if (known.Version == version)
                {
                    layout = known;
                    break;
                }
            if (layout == null)
                return false;

            var table = cpu.powerTable != null ? cpu.powerTable.Table : null;
            if (table == null || table.Length <= layout.Hotspot || table.Length <= layout.Average)
                return false;

            float avg = table[layout.Average];
            float hot = table[layout.Hotspot];

            // Stated positively so NaN is rejected too: every comparison against NaN is false, so
            // "not out of range" would have let one through and put NaN on the readouts row.
            if (!(avg >= MinCelsius && avg <= MaxCelsius) || !(hot >= MinCelsius && hot <= MaxCelsius))
                return false;

            // The hotspot is the peak of the die the average belongs to. Half a degree of slack
            // because the two are sampled a moment apart.
            if (hot < avg - 0.5f)
                return false;

            average = avg;
            hotspot = hot;
            return true;
        }
    }
}
