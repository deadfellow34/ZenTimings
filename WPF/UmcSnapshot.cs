using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ZenStates.Core;

namespace ZenTimings
{
    /// <summary>One register that differs between two snapshots.</summary>
    public class RegisterDelta
    {
        public uint Address { get; set; }
        public uint ValueA { get; set; }
        public uint ValueB { get; set; }
        public bool InA { get; set; }
        public bool InB { get; set; }

        public string AddressText
        {
            get { return string.Format("0x{0:X8}", Address); }
        }

        public string ValueAText
        {
            get { return InA ? string.Format("0x{0:X8}", ValueA) : "-"; }
        }

        public string ValueBText
        {
            get { return InB ? string.Format("0x{0:X8}", ValueB) : "-"; }
        }

        /// <summary>Bit positions that differ, most significant first, e.g. "7:3".</summary>
        public string ChangedBitsText
        {
            get
            {
                if (!InA || !InB)
                    return "-";

                uint mask = ValueA ^ ValueB;
                if (mask == 0)
                    return "";

                int high = 31;
                while (high >= 0 && ((mask >> high) & 1) == 0) high--;

                int low = 0;
                while (low <= 31 && ((mask >> low) & 1) == 0) low++;

                return high == low
                    ? high.ToString(CultureInfo.InvariantCulture)
                    : string.Format(CultureInfo.InvariantCulture, "{0}:{1}", high, low);
            }
        }

        /// <summary>Signed change of the whole dword, handy when a field is a plain counter.</summary>
        public string DeltaText
        {
            get
            {
                if (!InA || !InB)
                    return "-";

                long delta = (long)ValueB - ValueA;
                if (delta == 0)
                    return "";

                return delta > 0
                    ? "+" + delta.ToString(CultureInfo.InvariantCulture)
                    : delta.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>
    /// A full dump of the UMC register block for every enabled channel.
    ///
    /// This is the in-app version of the before/after workflow that located the tCCD_L registers:
    /// take a snapshot, change one thing in BIOS, reboot, take another, diff. Anything that moved
    /// is a candidate for whatever was changed.
    /// </summary>
    public class UmcSnapshot
    {
        /// <summary>UMC block base. Channel N lives at (N &lt;&lt; 20) | 0x50000.</summary>
        public const uint BlockStart = 0x50000;

        /// <summary>Last dword of the block. ZenStates decodes only up to 0x50300 - the rest is why this tool exists.</summary>
        public const uint BlockEnd = 0x50FFC;

        public const uint MaxChannels = 0xC;

        public string Label { get; set; }
        public DateTime TakenAt { get; set; }
        public string SystemDescription { get; set; }
        public SortedDictionary<uint, uint> Registers { get; private set; }

        public UmcSnapshot()
        {
            Registers = new SortedDictionary<uint, uint>();
            TakenAt = DateTime.Now;
            Label = string.Empty;
            SystemDescription = string.Empty;
        }

        public int Count
        {
            get { return Registers.Count; }
        }

        public string Summary
        {
            get
            {
                if (Registers.Count == 0)
                    return "(empty)";

                return string.Format(CultureInfo.InvariantCulture,
                    "{0} registers | {1:yyyy-MM-dd HH:mm:ss}{2}",
                    Registers.Count,
                    TakenAt,
                    string.IsNullOrEmpty(Label) ? "" : " | " + Label);
            }
        }

        /// <summary>
        /// Reads every enabled channel. Blocking and fairly slow (thousands of SMN reads), so call
        /// it off the UI thread. Takes the PCI bus mutex for the whole sweep, exactly like the
        /// debug report does, so nothing else can interleave SMN traffic mid-dump.
        /// </summary>
        public static UmcSnapshot Capture(Cpu cpu, string label)
        {
            if (cpu == null)
                throw new ArgumentNullException("cpu");

            var snapshot = new UmcSnapshot { Label = label ?? string.Empty };

            try
            {
                snapshot.SystemDescription = string.Format(CultureInfo.InvariantCulture,
                    "{0} | {1} | BIOS {2}",
                    cpu.systemInfo != null ? cpu.systemInfo.CpuName : "?",
                    cpu.systemInfo != null ? cpu.systemInfo.MbName : "?",
                    cpu.systemInfo != null ? cpu.systemInfo.BiosVersion : "?");
            }
            catch
            {
                snapshot.SystemDescription = string.Empty;
            }

            if (!Mutexes.WaitPciBus(5000))
                throw new TimeoutException("Timeout waiting for the PCI bus mutex.");

            try
            {
                for (uint channel = 0; channel < MaxChannels; channel++)
                {
                    uint offset = channel << 20;

                    if (!IsChannelEnabled(cpu, offset))
                        continue;

                    for (uint reg = BlockStart; reg <= BlockEnd; reg += 4)
                    {
                        uint address = offset | reg;
                        try
                        {
                            snapshot.Registers[address] = cpu.ReadDword(address);
                        }
                        catch
                        {
                            // A channel can disappear mid-sweep on some boards; skip that dword.
                        }
                    }
                }
            }
            finally
            {
                Mutexes.ReleasePciBus();
            }

            return snapshot;
        }

        private static bool IsChannelEnabled(Cpu cpu, uint offset)
        {
            try
            {
                bool channel = Utils.GetBits(cpu.ReadDword(offset | 0x50DF0), 19, 1) == 0;
                bool dimm1 = Utils.GetBits(cpu.ReadDword(offset | 0x50000), 0, 1) == 1;
                bool dimm2 = Utils.GetBits(cpu.ReadDword(offset | 0x50008), 0, 1) == 1;
                return channel && (dimm1 || dimm2);
            }
            catch
            {
                return false;
            }
        }

        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ZenTimings UMC snapshot");
            sb.AppendFormat(CultureInfo.InvariantCulture, "# label={0}\r\n", Label);
            sb.AppendFormat(CultureInfo.InvariantCulture, "# taken={0:yyyy-MM-dd HH:mm:ss}\r\n", TakenAt);
            sb.AppendFormat(CultureInfo.InvariantCulture, "# system={0}\r\n", SystemDescription);
            sb.AppendFormat(CultureInfo.InvariantCulture, "# count={0}\r\n", Registers.Count);

            foreach (var kvp in Registers)
                sb.AppendFormat(CultureInfo.InvariantCulture, "0x{0:X8}=0x{1:X8}\r\n", kvp.Key, kvp.Value);

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        /// Reads back a snapshot file. Also accepts a ZenTimings debug report, so reports mailed in
        /// by other people can be diffed directly without any conversion step.
        /// </summary>
        public static UmcSnapshot Load(string path)
        {
            var snapshot = new UmcSnapshot { Label = Path.GetFileNameWithoutExtension(path) };

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                if (line[0] == '#')
                {
                    ParseHeader(snapshot, line);
                    continue;
                }

                uint address, value;
                if (TryParsePair(line, out address, out value) && IsUmcAddress(address))
                    snapshot.Registers[address] = value;
            }

            return snapshot;
        }

        /// <summary>
        /// Guards against lines in a debug report that merely look like a hex pair
        /// (a bare "12: 34" would otherwise be read as register 0x12).
        /// </summary>
        private static bool IsUmcAddress(uint address)
        {
            uint channel = address >> 20;
            uint offset = address & 0x000FFFFF;

            return channel < MaxChannels && offset >= BlockStart && offset <= BlockEnd;
        }

        private static void ParseHeader(UmcSnapshot snapshot, string line)
        {
            const string labelPrefix = "# label=";
            const string systemPrefix = "# system=";
            const string takenPrefix = "# taken=";

            if (line.StartsWith(labelPrefix, StringComparison.Ordinal))
            {
                string label = line.Substring(labelPrefix.Length).Trim();
                if (label.Length > 0)
                    snapshot.Label = label;
            }
            else if (line.StartsWith(systemPrefix, StringComparison.Ordinal))
            {
                snapshot.SystemDescription = line.Substring(systemPrefix.Length).Trim();
            }
            else if (line.StartsWith(takenPrefix, StringComparison.Ordinal))
            {
                DateTime taken;
                if (DateTime.TryParse(line.Substring(takenPrefix.Length).Trim(),
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out taken))
                {
                    snapshot.TakenAt = taken;
                }
            }
        }

        /// <summary>
        /// Accepts both the snapshot format (<c>0xADDR=0xVALUE</c>) and the debug report format
        /// (<c>   0xADDR: 0xVALUE</c>).
        /// </summary>
        private static bool TryParsePair(string line, out uint address, out uint value)
        {
            address = 0;
            value = 0;

            int separator = line.IndexOf('=');
            if (separator < 0)
                separator = line.IndexOf(':');
            if (separator <= 0)
                return false;

            string left = line.Substring(0, separator).Trim();
            string right = line.Substring(separator + 1).Trim();

            // The report puts a trailing comment on some lines; keep only the first token.
            int space = right.IndexOf(' ');
            if (space > 0)
                right = right.Substring(0, space);

            return TryParseHex(left, out address) && TryParseHex(right, out value);
        }

        private static bool TryParseHex(string text, out uint value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text))
                return false;

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(2);

            return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Compares two snapshots. With <paramref name="includeUnchanged"/> false (the default use)
        /// only registers that actually moved are returned - that short list is the answer to
        /// "which register holds the setting I just changed".
        /// </summary>
        public static List<RegisterDelta> Compare(UmcSnapshot a, UmcSnapshot b, bool includeUnchanged)
        {
            var result = new List<RegisterDelta>();
            if (a == null || b == null)
                return result;

            var addresses = new SortedSet<uint>();
            foreach (var key in a.Registers.Keys) addresses.Add(key);
            foreach (var key in b.Registers.Keys) addresses.Add(key);

            foreach (uint address in addresses)
            {
                uint valueA, valueB;
                bool inA = a.Registers.TryGetValue(address, out valueA);
                bool inB = b.Registers.TryGetValue(address, out valueB);

                bool changed = !inA || !inB || valueA != valueB;
                if (!changed && !includeUnchanged)
                    continue;

                result.Add(new RegisterDelta
                {
                    Address = address,
                    ValueA = valueA,
                    ValueB = valueB,
                    InA = inA,
                    InB = inB,
                });
            }

            return result;
        }
    }
}
