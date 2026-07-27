using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ZenTimings
{
    /// <summary>
    /// Appends one CSV row per refresh tick so a long stability run can be reviewed afterwards
    /// (temperatures, clocks, voltages over time). Writes are flushed immediately - a run that ends
    /// in a hard lock-up still leaves every row up to the freeze on disk, which is the whole point.
    /// </summary>
    public sealed class TelemetryLogger : IDisposable
    {
        private readonly object _sync = new object();
        private StreamWriter _writer;
        private int _columnCount;

        public bool IsRunning
        {
            get { lock (_sync) { return _writer != null; } }
        }

        public string FilePath { get; private set; }

        public long RowsWritten { get; private set; }

        /// <summary>Starts a new log file. Any previous one is closed first.</summary>
        public void Start(string path, IList<string> headers)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            if (headers == null || headers.Count == 0) throw new ArgumentException("headers required", "headers");

            lock (_sync)
            {
                CloseWriter();

                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                // WriteThrough: a plain Flush() only reaches the OS cache, which a hard lock-up -
                // the exact failure this log exists to capture - discards along with the rows
                // around the freeze. Going through to the device per row is what makes it true.
                _writer = new StreamWriter(
                    new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096,
                        FileOptions.WriteThrough),
                    new UTF8Encoding(false));
                _columnCount = headers.Count;
                FilePath = path;
                RowsWritten = 0;

                _writer.WriteLine(BuildRow(headers));
                _writer.Flush();
            }
        }

        /// <summary>
        /// Writes one row. Silently ignored when logging is off, so callers on the refresh path
        /// do not need to check first. Rows shorter than the header are padded, longer ones trimmed,
        /// so a column set that changes mid-run cannot corrupt the file.
        /// </summary>
        public void Write(IList<string> values)
        {
            if (values == null)
                return;

            lock (_sync)
            {
                if (_writer == null)
                    return;

                var row = new List<string>(_columnCount);
                for (int i = 0; i < _columnCount; i++)
                    row.Add(i < values.Count ? values[i] : string.Empty);

                try
                {
                    _writer.WriteLine(BuildRow(row));
                    _writer.Flush();
                    RowsWritten++;
                }
                catch (IOException)
                {
                    // Disk full / file locked: stop rather than throwing on the refresh thread.
                    CloseWriter();
                }
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                CloseWriter();
            }
        }

        private void CloseWriter()
        {
            if (_writer == null)
                return;

            try { _writer.Flush(); } catch { }
            try { _writer.Dispose(); } catch { }
            _writer = null;
        }

        private static string BuildRow(IList<string> values)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Escape(values[i]));
            }
            return sb.ToString();
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            bool needsQuotes = value.IndexOf(',') >= 0
                || value.IndexOf('"') >= 0
                || value.IndexOf('\n') >= 0
                || value.IndexOf('\r') >= 0;

            if (!needsQuotes)
                return value;

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>Formats a number the way a spreadsheet expects, regardless of system locale.</summary>
        public static string Num(double value, string format = "0.###")
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
