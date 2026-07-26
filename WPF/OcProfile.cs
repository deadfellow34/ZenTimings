using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using ZenTimings.ViewModels;

namespace ZenTimings
{
    [Serializable]
    public class OcProfileEntry
    {
        public string Key { get; set; }
        public string Value { get; set; }
    }

    /// <summary>One key whose value differs between two profiles (or a profile and the live state).</summary>
    public class ProfileDelta
    {
        public string Key { get; set; }
        public string ValueA { get; set; }
        public string ValueB { get; set; }

        public bool IsDifferent
        {
            get { return !string.Equals(ValueA, ValueB, StringComparison.Ordinal); }
        }
    }

    /// <summary>
    /// A named snapshot of everything the app can read about the current memory setup:
    /// frequency, every timing, the SoC/memory voltages and the clocks.
    ///
    /// The point is the diff. Tuning is a sequence of "change one thing, reboot, measure", and
    /// without a record of what the previous round actually looked like that sequence gets lost.
    /// </summary>
    [Serializable]
    public class OcProfile
    {
        public string Name { get; set; }
        public string CreatedAt { get; set; }
        public string SystemDescription { get; set; }
        public List<OcProfileEntry> Entries { get; set; }

        public OcProfile()
        {
            Entries = new List<OcProfileEntry>();
            Name = string.Empty;
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            SystemDescription = string.Empty;
        }

        public static string ProfilesDirectory
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles"); }
        }

        public string Display
        {
            get
            {
                return string.IsNullOrEmpty(CreatedAt)
                    ? Name
                    : string.Format(CultureInfo.InvariantCulture, "{0}  ({1})", Name, CreatedAt);
            }
        }

        public string Get(string key)
        {
            var entry = Entries.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.Ordinal));
            return entry != null ? entry.Value : null;
        }

        private void Add(string key, object value)
        {
            if (value == null)
                return;

            string text = Format(value);
            if (string.IsNullOrEmpty(text))
                return;

            Entries.Add(new OcProfileEntry { Key = key, Value = text });
        }

        private static string Format(object value)
        {
            if (value is float) return ((float)value).ToString("0.####", CultureInfo.InvariantCulture);
            if (value is double) return ((double)value).ToString("0.####", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>Builds a profile from what the view model is showing right now.</summary>
        public static OcProfile CaptureCurrent(string name, MainViewModel vm)
        {
            var profile = new OcProfile { Name = name ?? "profile" };

            if (vm == null)
                return profile;

            try
            {
                var systemInfo = CpuSingleton.Instance?.systemInfo;
                if (systemInfo != null)
                {
                    profile.SystemDescription = string.Format(CultureInfo.InvariantCulture,
                        "{0} | {1} | BIOS {2}", systemInfo.CpuName, systemInfo.MbName, systemInfo.BiosVersion);
                }
            }
            catch
            {
                profile.SystemDescription = string.Empty;
            }

            profile.Add("Frequency", vm.MemoryFrequency);

            if (vm.Timings != null)
            {
                foreach (var prop in vm.Timings.GetType()
                             .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                             .Where(p => p.GetIndexParameters().Length == 0)
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    object value;
                    try { value = prop.GetValue(vm.Timings, null); }
                    catch { continue; }

                    profile.Add(prop.Name, value);
                }
            }

            // 0 means "not readable on this platform" - do not record it as a real value.
            if (vm.TccdlValue > 0) profile.Add("tCCD_L", vm.TccdlValue);
            if (vm.TccdlWr2Value > 0) profile.Add("tCCD_L_WR2", vm.TccdlWr2Value);

            var powerTable = vm.PowerTable;
            if (powerTable != null)
            {
                foreach (string field in new[]
                         {
                             "MCLK", "FCLK", "UCLK", "VDDCR_SOC",
                             "CLDO_VDDP", "CLDO_VDDG_CCD", "CLDO_VDDG_IOD", "VDD_MISC",
                         })
                {
                    object value;
                    try
                    {
                        var prop = powerTable.GetType().GetProperty(field);
                        value = prop?.GetValue(powerTable, null);
                    }
                    catch { continue; }

                    profile.Add(field, value);
                }
            }

            profile.Add("MEM_VDD", vm.SwaAdcV);
            profile.Add("MEM_VDDQ", vm.SwbAdcV);
            profile.Add("MEM_VPP", vm.VppAdcV);

            return profile;
        }

        public string Save()
        {
            string directory = ProfilesDirectory;
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, SanitizeFileName(Name) + ".xml");
            File.WriteAllText(path, XmlUtils.SerializeToXml(this));
            return path;
        }

        public static List<OcProfile> LoadAll()
        {
            var result = new List<OcProfile>();
            string directory = ProfilesDirectory;

            if (!Directory.Exists(directory))
                return result;

            foreach (string file in Directory.GetFiles(directory, "*.xml"))
            {
                try
                {
                    var profile = XmlUtils.DeserializeFromXml<OcProfile>(file);
                    if (profile != null)
                    {
                        if (string.IsNullOrEmpty(profile.Name))
                            profile.Name = Path.GetFileNameWithoutExtension(file);
                        result.Add(profile);
                    }
                }
                catch
                {
                    // A hand-edited or truncated profile is skipped, not fatal.
                }
            }

            return result.OrderBy(p => p.CreatedAt, StringComparer.Ordinal).ToList();
        }

        public static bool Delete(OcProfile profile)
        {
            if (profile == null)
                return false;

            try
            {
                string path = Path.Combine(ProfilesDirectory, SanitizeFileName(profile.Name) + ".xml");
                if (File.Exists(path))
                {
                    File.Delete(path);
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        /// <summary>
        /// Full outer join over both key sets, so a key present in only one profile still shows up
        /// (that is usually the interesting one - a setting that appeared or vanished).
        /// </summary>
        public static List<ProfileDelta> Compare(OcProfile a, OcProfile b, bool onlyDifferences)
        {
            var result = new List<ProfileDelta>();
            if (a == null || b == null)
                return result;

            var keys = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in a.Entries)
                if (entry != null && entry.Key != null && seen.Add(entry.Key)) keys.Add(entry.Key);

            foreach (var entry in b.Entries)
                if (entry != null && entry.Key != null && seen.Add(entry.Key)) keys.Add(entry.Key);

            foreach (string key in keys)
            {
                var delta = new ProfileDelta
                {
                    Key = key,
                    ValueA = a.Get(key) ?? "-",
                    ValueB = b.Get(key) ?? "-",
                };

                if (onlyDifferences && !delta.IsDifferent)
                    continue;

                result.Add(delta);
            }

            return result;
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "profile";

            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            string result = new string(chars).Trim();

            return result.Length == 0 ? "profile" : result;
        }
    }
}
