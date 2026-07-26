using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Text;

namespace ZenTimings
{
    /// <summary>
    /// Counts the machine-check errors Windows has logged since the last boot.
    /// </summary>
    /// <remarks>
    /// On a memory overclock a corrected error is the first thing that moves - it shows up long
    /// before a test reports a failure, and often before anything is visibly wrong at all. So the
    /// count is worth watching next to the temperatures rather than after the fact.
    ///
    /// The source is the WHEA-Logger provider in the System event log. Reading it needs
    /// administrator rights, which the app already runs with; without them the reader simply
    /// yields nothing and the readout stays hidden rather than showing a misleading zero.
    /// </remarks>
    public sealed class WheaMonitor
    {
        /// <summary>Corrected errors. 17/19/47 are the corrected variants WHEA reports.</summary>
        private static readonly HashSet<int> CorrectedIds = new HashSet<int> { 17, 19, 47 };

        /// <summary>Uncorrectable / fatal. Seeing one of these at all is a failed overclock.</summary>
        private static readonly HashSet<int> FatalIds = new HashSet<int> { 18, 20, 23, 24, 46 };

        public int Corrected { get; private set; }
        public int Fatal { get; private set; }
        public int Other { get; private set; }
        public DateTime? Latest { get; private set; }

        /// <summary>False until a query has actually succeeded, so a failure never reads as "0 errors".</summary>
        public bool IsAvailable { get; private set; }

        public int Total
        {
            get { return Corrected + Fatal + Other; }
        }

        /// <summary>
        /// Re-reads the log. Blocking - call it from a background thread.
        /// Errors are swallowed: a machine with the event log service disabled, or a locked log,
        /// must not take the refresh loop down.
        /// </summary>
        public void Refresh()
        {
            try
            {
                DateTime bootTime = BootTime();

                // Filtering in the query (rather than reading the log and testing in C#) keeps this
                // to a few milliseconds even on a machine with a large System log.
                string xpath = string.Format(
                    "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and TimeCreated[@SystemTime>='{0}']]]",
                    bootTime.ToUniversalTime().ToString("s") + "Z");

                var query = new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true };

                int corrected = 0, fatal = 0, other = 0;
                DateTime? latest = null;

                using (var reader = new EventLogReader(query))
                {
                    for (EventRecord record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
                    {
                        using (record)
                        {
                            int id = record.Id;
                            if (CorrectedIds.Contains(id)) corrected++;
                            else if (FatalIds.Contains(id)) fatal++;
                            else other++;

                            if (latest == null && record.TimeCreated.HasValue)
                                latest = record.TimeCreated;
                        }
                    }
                }

                Corrected = corrected;
                Fatal = fatal;
                Other = other;
                Latest = latest;
                IsAvailable = true;
            }
            catch
            {
                // Leave the previous numbers in place; IsAvailable stays as it was.
            }
        }

        private static DateTime? _bootTime;

        /// <summary>
        /// When Windows last started, from WMI and cached for the session. Environment.TickCount is
        /// not usable here: it is a 32-bit millisecond counter that wraps after ~25 days, which
        /// would place the boot time in the future and silently hide every event.
        /// </summary>
        private static DateTime BootTime()
        {
            if (_bootTime.HasValue)
                return _bootTime.Value;

            DateTime result = DateTime.Now.AddDays(-1);   // conservative fallback

            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT LastBootUpTime FROM Win32_OperatingSystem"))
                using (var results = searcher.Get())
                {
                    foreach (System.Management.ManagementObject os in results)
                    {
                        using (os)
                        {
                            object value = os["LastBootUpTime"];
                            if (value != null)
                            {
                                result = System.Management.ManagementDateTimeConverter
                                    .ToDateTime(value.ToString());
                            }
                        }
                        break;
                    }
                }
            }
            catch
            {
                // Keep the fallback window.
            }

            _bootTime = result;
            return result;
        }

        public string BuildToolTip(bool turkish)
        {
            var text = new StringBuilder();

            text.Append(turkish
                ? "Windows'un bu açılıştan beri kaydettiği donanım hataları (WHEA)."
                : "Hardware errors Windows has logged since this boot (WHEA).");

            if (Total == 0)
            {
                text.AppendLine().AppendLine();
                text.Append(turkish ? "Kayıt yok - temiz." : "Nothing logged - clean.");
                return text.ToString();
            }

            text.AppendLine().AppendLine();
            if (Corrected > 0)
                text.AppendLine(string.Format(turkish ? "Düzeltilmiş: {0}" : "Corrected: {0}", Corrected));
            if (Fatal > 0)
                text.AppendLine(string.Format(turkish ? "Ölümcül: {0}" : "Fatal: {0}", Fatal));
            if (Other > 0)
                text.AppendLine(string.Format(turkish ? "Diğer: {0}" : "Other: {0}", Other));

            if (Latest.HasValue)
                text.Append(string.Format(turkish ? "Son: {0:g}" : "Latest: {0:g}", Latest.Value));

            return text.ToString().TrimEnd();
        }
    }

}
