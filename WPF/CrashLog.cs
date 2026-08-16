using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace ZenTimings
{
    /// <summary>
    /// Last words. A crash on someone else's machine cannot be reproduced here; a crash.txt next
    /// to the exe is the difference between a fix and a guess.
    /// </summary>
    internal static class CrashLog
    {
        private static readonly object Sync = new object();
        private static readonly HashSet<string> Once = new HashSet<string>();

        /// <summary>Appends the exception with a stamp. Never throws - this must not be the second crash.</summary>
        public static void Write(string source, Exception ex)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.txt");
                string text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source} " +
                              $"{Assembly.GetExecutingAssembly().GetName().Version}{Environment.NewLine}" +
                              $"{(ex != null ? ex.ToString() : "(no exception object)")}" +
                              $"{Environment.NewLine}{Environment.NewLine}";

                lock (Sync)
                    File.AppendAllText(path, text);
            }
            catch
            {
            }
        }

        /// <summary>Once per source per run - a poll failing every two seconds is one fact, not a flood.</summary>
        public static void WriteOnce(string source, Exception ex)
        {
            lock (Sync)
            {
                if (!Once.Add(source))
                    return;
            }

            Write(source, ex);
        }
    }
}
