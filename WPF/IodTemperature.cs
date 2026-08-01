using System;
using ZenStates.Core;

namespace ZenTimings
{
    /// <summary>
    /// I/O die temperature and its hotspot, out of the SMU power table.
    /// </summary>
    /// <remarks>
    /// Neither has a register of its own, and the offsets only mean this on the Zen 5 layouts
    /// (table version 0x62xxxx) - the Zen 4 tables hold MCLK where 0x1A8 sits, which is why the
    /// version check is what keeps a plausible-looking number from being shown as a temperature.
    ///
    /// 0x1A8 was confirmed against a Gigabyte X870 Aorus Elite reporting 41.2 C: it read 41.40.
    /// The hotspot took longer. 0x458 looks like a copy of Tctl at idle - on that same board both
    /// read 44.48 - but the two separate under load: one dump has Tctl at 69.21 while 0x458 reads
    /// 56.87 and the die average 52.32, which is the order these three have to come in. Across
    /// eleven Zen 5 dumps 0x458 tracks the average at r=0.99, always 3 to 7 C above it, and it is
    /// the only field in the table that does. Its 44.48 also matches the 44-45 C the same board
    /// reported for its hotspot.
    ///
    /// The neighbouring 0x438 was the first guess and is wrong: it reads 91.88 in the loaded dump,
    /// above the package maximum, and correlates poorly.
    /// </remarks>
    internal static class IodTemperature
    {
        private const int AverageIndex = 0x1A8 / 4;
        private const int HotspotIndex = 0x458 / 4;

        /// <summary>Anything outside this is not a die temperature, whatever the table holds.</summary>
        private const float MinCelsius = 5.0f;
        private const float MaxCelsius = 125.0f;

        /// <summary>
        /// The exact table versions these offsets were read on. Matching the family alone would
        /// be a bet that a future 0x62 revision keeps the same layout - and the Zen 4 tables,
        /// which hold MCLK at 0x1A8, are the standing proof that layouts move.
        /// </summary>
        private static readonly uint[] KnownVersions = { 0x00620105, 0x00620205 };

        public static bool TryRead(Cpu cpu, out float average, out float hotspot)
        {
            average = 0;
            hotspot = 0;

            if (cpu == null || cpu.systemInfo == null)
                return false;

            uint version = cpu.systemInfo.SmuTableVersion;
            if (Array.IndexOf(KnownVersions, version) < 0)
                return false;

            var table = cpu.powerTable != null ? cpu.powerTable.Table : null;
            if (table == null || table.Length <= HotspotIndex)
                return false;

            float avg = table[AverageIndex];
            float hot = table[HotspotIndex];

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
