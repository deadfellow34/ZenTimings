using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace ZenTimings
{
    /// <summary>
    /// One benchmark run as a standalone HTML page: the scores as tiles on top, the module and
    /// system identity above them, the captured configuration in compact multi-column groups
    /// underneath. History entries export the same way, with whatever their snapshot recorded.
    /// </summary>
    public static class BenchmarkExport
    {
        public static string BuildHtml(BenchmarkRun run, string screenshotPngBase64 = null)
        {
            var inv = CultureInfo.InvariantCulture;
            var html = new StringBuilder();

            // The machine the run was measured on, not the one exporting it. A run kept across a
            // BIOS flash would otherwise be published under the version that replaced the one it
            // actually measured. Runs recorded before this was stamped say nothing at all.
            string system = run.System ?? "";

            var remaining = new List<BenchmarkSetting>(run.Settings ?? new List<BenchmarkSetting>());

            // The sticks headline the page; Speed backs them up when the run predates the key.
            string modules = Take(remaining, "Modules");
            string speed = Find(remaining, "Speed");

            html.Append("<!DOCTYPE html><html><head><meta charset='utf-8'>");
            html.Append("<title>ZenTimings Benchmark - ").Append(Escape(run.Timestamp)).Append("</title>");
            html.Append(@"<style>
body{font-family:'Segoe UI',sans-serif;background:#141b28;color:#dbe2ee;margin:0;padding:26px 18px}
.wrap{max-width:880px;margin:0 auto}
.head{margin-bottom:14px}
.app{font-size:12px;color:#8b96aa;letter-spacing:.06em;text-transform:uppercase}
.ram{font-size:19px;font-weight:600;margin:2px 0;color:#fff}
.sys{font-size:12px;color:#8b96aa}
.tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(130px,1fr));gap:10px;margin:14px 0 6px}
.tile{background:#1c2537;border:1px solid #2a3550;border-radius:8px;padding:12px 14px;text-align:center}
.tile .v{font-size:26px;font-weight:650;font-variant-numeric:tabular-nums;color:#eaf0fa}
.tile .l{font-size:11px;color:#8b96aa;margin-top:1px}
.tile.ns{border-color:#3fae7f}
.tile.ns .v{color:#5fd6a4;font-size:30px}
.meta{font-size:11.5px;color:#8b96aa;margin:0 0 16px;text-align:right}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:12px}
.card{background:#1c2537;border:1px solid #2a3550;border-radius:8px;padding:12px 16px}
.card.wide{grid-column:1/-1}
h2{font-size:11px;text-transform:uppercase;letter-spacing:.1em;color:#8b96aa;margin:0 0 8px}
.cols{columns:3 200px;column-gap:22px}
.kv{display:flex;justify-content:space-between;font-size:12.5px;padding:2.5px 0;break-inside:avoid;border-bottom:1px solid #232d45}
.kv b{font-family:Consolas,monospace;font-weight:600;color:#eaf0fa}
.kv span{color:#8b96aa}
.shot{text-align:center}
.shot img{max-width:100%;border:1px solid #2a3550;border-radius:8px}
.shot .cap{font-size:11px;color:#8b96aa;margin-top:4px}
@media print{body{background:#fff;color:#1c2432}.card,.tile{background:#fff;border-color:#ccc}.kv b,.ram,.tile .v{color:#1c2432}}
</style></head><body><div class='wrap'>");

            html.Append("<div class='head'><div class='app'>ZenTimings Memory Benchmark</div>");
            html.Append("<div class='ram'>").Append(Escape(
                !string.IsNullOrEmpty(modules) ? modules : (speed ?? "Memory"))).Append("</div>");
            html.Append("<div class='sys'>").Append(Escape(run.Timestamp));
            if (!string.IsNullOrEmpty(modules) && !string.IsNullOrEmpty(speed))
                html.Append("  &middot;  ").Append(Escape(speed));
            if (system.Length > 0)
                html.Append("  &middot;  ").Append(Escape(system));
            html.Append("</div></div>");

            // The numbers the page exists for.
            html.Append("<div class='tiles'>");
            if (run.LatencyNs > 0)
                Tile(html, "ns", run.LatencyNs.ToString("F1", inv), "latency (ns)");
            if (run.ReadGBs > 0)
            {
                Tile(html, "", run.ReadGBs.ToString("F1", inv), "read GB/s");
                Tile(html, "", run.WriteGBs.ToString("F1", inv), "write GB/s");
                Tile(html, "", run.CopyGBs.ToString("F1", inv), "copy GB/s");
            }
            if (run.RandomGBs > 0)
                Tile(html, "", run.RandomGBs.ToString("F1", inv), "random GB/s");
            if (run.BufferMegabytes > 0)
                Tile(html, "", run.BufferMegabytes.ToString(inv), "buffer MB");
            html.Append("</div>");

            var meta = new List<string>();
            if (run.SpreadNs > 0)
                meta.Add("spread " + run.SpreadNs.ToString("F2", inv) + " ns");
            if (run.LatencyNs > 0)
                meta.Add(run.LargePages ? "large pages" : "4K pages");

            // The run's own verdicts travel with it: a figure the app distrusted must not read as
            // clean once it leaves the window.
            if (run.CacheBound)
                meta.Add("cache-bound buffer");
            if (run.Noisy)
                meta.Add("noisy run");
            if (meta.Count > 0)
                html.Append("<div class='meta'>").Append(Escape(string.Join("  ·  ", meta))).Append("</div>");

            if (!string.IsNullOrEmpty(screenshotPngBase64))
            {
                // The window itself carries the timings, voltages and impedances - no hand-built
                // tables to drift out of sync with what the panels show.
                html.Append("<div class='shot'><img alt='ZenTimings' src='data:image/png;base64,")
                    .Append(screenshotPngBase64)
                    .Append("'><div class='cap'>ZenTimings &middot; captured at export</div></div>");
            }
            else
            {
                // No window to capture (or the capture failed) - fall back to the recorded data,
                // laid out by the panel each key came from. LiveSnapshot decides that, so the
                // cards cannot drift apart from the values it writes.
                html.Append("<div class='grid'>");
                Section(html, LiveSnapshot.Configuration, false, remaining);
                Section(html, LiveSnapshot.Voltages, false, remaining);
                Section(html, LiveSnapshot.Timings, true, remaining);
                Section(html, LiveSnapshot.Impedances, true, remaining);
                Section(html, LiveSnapshot.Other, true, remaining);
                html.Append("</div>");
            }

            html.Append("</div></body></html>");
            return html.ToString();
        }

        private static void Tile(StringBuilder html, string extraClass, string value, string label)
        {
            html.Append("<div class='tile").Append(extraClass.Length > 0 ? " " + extraClass : "")
                .Append("'><div class='v'>").Append(Escape(value))
                .Append("</div><div class='l'>").Append(Escape(label)).Append("</div></div>");
        }

        /// <summary>
        /// Key/value rows flowing into columns, so forty timings take a card, not a page.
        /// </summary>
        /// <remarks>
        /// The last card asks for <see cref="LiveSnapshot.Other"/> and so sweeps up anything the
        /// snapshot did not file - keys from an older schema included. Nothing is dropped.
        /// </remarks>
        private static void Section(StringBuilder html, string group, bool wide,
            List<BenchmarkSetting> remaining)
        {
            var rows = remaining
                .Where(s => s != null && s.Key != null && LiveSnapshot.GroupOf(s.Key) == group)
                .ToList();
            if (rows.Count == 0)
                return;

            html.Append("<div class='card").Append(wide ? " wide" : "").Append("'><h2>")
                .Append(Escape(group)).Append("</h2><div class='cols'>");
            foreach (var row in rows)
            {
                html.Append("<div class='kv'><span>").Append(Escape(row.Key)).Append("</span><b>")
                    .Append(Escape(row.Value)).Append("</b></div>");
                remaining.Remove(row);
            }
            html.Append("</div></div>");
        }

        private static string Take(List<BenchmarkSetting> settings, string key)
        {
            var found = settings.FirstOrDefault(s => s != null && s.Key == key);
            if (found == null)
                return null;
            settings.Remove(found);
            return found.Value;
        }

        private static string Find(List<BenchmarkSetting> settings, string key)
        {
            var found = settings.FirstOrDefault(s => s != null && s.Key == key);
            return found != null ? found.Value : null;
        }

        private static string Escape(string text)
        {
            return WebUtility.HtmlEncode(text ?? "");
        }
    }
}
