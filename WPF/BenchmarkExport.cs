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
.mem{background:#1c2537;border:1px solid #2a3550;border-radius:8px;padding:12px 16px;margin:14px 0}
table.mt{width:100%;border-collapse:collapse;margin:2px 0 4px;font-size:12.5px}
table.mt+table.mt{margin-top:14px}
table.mt caption{caption-side:top;text-align:left;font-size:11px;text-transform:uppercase;letter-spacing:.08em;color:#8b96aa;margin-bottom:5px}
table.mt th,table.mt td{border:1px solid #2a3550;padding:5px 10px;text-align:center}
table.mt th{color:#8b96aa;font-weight:600}
table.mt td{font-family:Consolas,monospace;color:#cdd6e6}
table.mt td.lv{text-align:left;font-weight:600;color:#eaf0fa;font-family:'Segoe UI',sans-serif}
table.mt .unit{font-size:10px;font-weight:400;color:#66708a}
.foot{font-size:11px;color:#8b96aa;margin-top:6px}
@media print{body{background:#fff;color:#1c2432}.card,.tile,.mem{background:#fff;border-color:#ccc}.kv b,.ram,.tile .v{color:#1c2432}table.mt th,table.mt td{border-color:#ccc}table.mt td{color:#1c2432}}
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
            // One tile per figure that has one. Hanging write and copy off the read meant a read
            // the ceiling filter rejected took two good measurements out of the export with it.
            // Integer GB/s, to match the cache table below and the app. Latency keeps its decimal.
            if (run.ReadGBs > 0)
                Tile(html, "", run.ReadGBs.ToString("F0", inv), "read GB/s");
            if (run.WriteGBs > 0)
                Tile(html, "", run.WriteGBs.ToString("F0", inv), "write GB/s");
            if (run.CopyGBs > 0)
                Tile(html, "", run.CopyGBs.ToString("F0", inv), "copy GB/s");
            if (run.RandomGBs > 0)
                Tile(html, "", run.RandomGBs.ToString("F0", inv), "random GB/s");
            if (run.BufferMegabytes > 0)
                Tile(html, "", run.BufferMegabytes.ToString(inv), "buffer MB");
            html.Append("</div>");

            var meta = new List<string>();
            if (run.SpreadNs > 0)
                meta.Add("spread " + run.SpreadNs.ToString("F2", inv) + " ns");
            // Named for the phase that recorded it: the bandwidth pass asks for two buffers where
            // the walk asks for one, so the two page modes can genuinely differ.
            if (run.LatencyNs > 0)
                meta.Add(run.LargePages ? "latency on large pages" : "latency on 4K pages");

            // Only where it says something the line above did not: a run older than the field
            // recorded no mode of its own, and one that agrees with the walk needs no second line.
            if (run.BandwidthLargePages.HasValue
                && (run.LatencyNs <= 0 || run.BandwidthLargePages.Value != run.LargePages))
                meta.Add(run.BandwidthLargePages.Value
                    ? "bandwidth on large pages" : "bandwidth on 4K pages");

            // The run's own verdicts travel with it: a figure the app distrusted must not read as
            // clean once it leaves the window.
            if (run.CacheBound)
                meta.Add("cache-bound buffer");
            if (run.Noisy)
                meta.Add("noisy run");
            // The tile is simply absent where the filter fired, and an absent tile is also what a
            // kernel that never ran leaves behind.
            if (run.AboveBus)
                meta.Add("bandwidth figure above the DRAM bus withheld");
            if (meta.Count > 0)
                html.Append("<div class='meta'>").Append(Escape(string.Join("  ·  ", meta))).Append("</div>");

            // The cache ladder - L1/L2/L3 across, all cores and one core. The DRAM figures are the
            // tiles above. Runs saved before the ladder was captured skip it.
            Ladder(html, run, inv);

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

        /// <summary>
        /// The cache grid: an all-cores table (read/write/copy/latency/size, one row per level) and
        /// a one-core table, mirroring the window. Cache only - the DRAM figures are the tiles up
        /// top, so a Memory row here would repeat them. Emits nothing when the run has no ladder.
        /// </summary>
        private static void Ladder(StringBuilder html, BenchmarkRun run, CultureInfo inv)
        {
            var rungs = run.Rungs;
            if (rungs == null || rungs.Count == 0)
                return;

            html.Append("<div class='mem'>");

            html.Append("<table class='mt'><caption>All cores</caption><tr><th></th>")
                .Append("<th>read<div class='unit'>GB/s</div></th><th>write<div class='unit'>GB/s</div></th>")
                .Append("<th>copy<div class='unit'>GB/s</div></th><th>latency<div class='unit'>ns</div></th>")
                .Append("<th>size</th></tr>");

            foreach (var r in rungs)
            {
                // A hand-edited history file can hole the list; skip nil rows rather than throw.
                if (r == null)
                    continue;
                // The crew under the level, in the table rather than under it: the caption says
                // "All cores" and the page has no hover to put the exception behind.
                html.Append("<tr><td class='lv'>").Append(Escape(r.Level)).Append(Crew(r, inv))
                    .Append("</td>")
                    .Append(Num(r.ReadAllGBs)).Append(Num(r.WriteAllGBs)).Append(Num(r.CopyAllGBs))
                    .Append(Cell(r.Nanoseconds > 0 ? r.Nanoseconds.ToString("F2", inv) : "-"))
                    .Append(Cell(Size(r.CacheBytes))).Append("</tr>");
            }

            html.Append("</table>");

            if (rungs.Exists(r => r != null && r.ReadGBs > 0))
            {
                html.Append("<table class='mt'><caption>One core</caption><tr><th></th>")
                    .Append("<th>read<div class='unit'>GB/s</div></th><th>write<div class='unit'>GB/s</div></th>")
                    .Append("<th>copy<div class='unit'>GB/s</div></th></tr>");
                foreach (var r in rungs)
                {
                    if (r == null)
                        continue;
                    // No size column on this table, so the size rides in the label: an asymmetric
                    // part's two L3 rows are otherwise the same row twice.
                    html.Append("<tr><td class='lv'>").Append(Escape(Label(rungs, r))).Append("</td>")
                        .Append(Num(r.ReadGBs)).Append(Num(r.WriteGBs)).Append(Num(r.CopyGBs)).Append("</tr>");
                }
                html.Append("</table>");
            }

            // A row's caveats travel with the figures they qualify: page mode and crew each
            // change what was measured, and neither of them shows in the numbers.
            foreach (var r in rungs)
            {
                if (r == null)
                    continue;

                var notes = new List<string>();
                // Which half of the row the fallback touched, where the run recorded it: the row
                // flag alone is cleared by an all-core worker's buffer as well as by the rung's
                // own, and the heavier sentence is wrong about the latency and the one-core trio.
                // A run written before that was recorded keeps the claim it always made.
                if (!r.LargePages)
                    notes.Add(r.OwnLargePages == true
                        ? "an all-core buffer did not get large pages - the latency and the one-core columns did"
                        : "figures on 4K pages");

                // Its own if, not a branch of the crew note below: a row can be one cache of
                // several AND have shed workers inside it, and each half is a different fact.
                if (Published(r) && r.PackageCores > r.AllCoreOf)
                    notes.Add("this cache is shared by " + r.AllCoreOf.ToString(inv)
                        + " of the package's " + r.PackageCores.ToString(inv)
                        + " cores and the row is measured inside it - a core outside would read it"
                        + " across the fabric; identical caches share one row");

                // Gated on figures like the scope note above: the crew goes on record before the
                // all-core pass runs and outlives one that then publishes nothing, so the count
                // alone is no evidence there is anything for it to describe. The withheld case
                // below states an absence and stands without figures.
                if (Published(r) && r.AllCoreOf > 0 && r.AllCoreWorkers > 0
                    && r.AllCoreWorkers < r.AllCoreOf)
                    notes.Add("all-core figures from " + r.AllCoreWorkers.ToString(inv)
                        + " of " + r.AllCoreOf.ToString(inv) + " cores");
                else if (r.AllCoreOf > 0 && r.AllCoreWorkers == 0)
                    notes.Add("all-core figures withheld - even two slices would sit inside the L2s");

                if (notes.Count > 0)
                {
                    html.Append("<div class='foot'>").Append(Escape(Label(rungs, r))).Append(": ")
                        .Append(Escape(string.Join("  ·  ", notes))).Append("</div>");
                }
            }

            // What a row's all-core columns covered where the row itself says nothing: the same
            // machine publishes an L3 row measured inside one cache and, where the topology query
            // failed, one measured across the package, about twice apart under one caption.
            // Inside the .mem block, so it is scoped to the cache tables and not the DRAM tiles.
            // The exception is the count a row carries under its level, not the notes above: those
            // also hold the page mode, true of every row on a run without the large-page right and
            // silent about cores, so an exception keyed on them excepts every row and leaves the
            // crew stated for none.
            int crew = PackageCrew(rungs);
            if (crew > 0)
                html.Append("<div class='foot'>Cache rows: all-core figures use ")
                    .Append(crew.ToString(inv))
                    .Append(" cores except where the level carries a count of its own</div>");

            if (run.VectorBits > 0)
                html.Append("<div class='foot'>Cache bandwidth measured with AVX-")
                    .Append(run.VectorBits.ToString(inv)).Append(" loads</div>");

            html.Append("</div>");
        }

        /// <summary>
        /// How a row is named where no size column stands beside it. An asymmetric part has two
        /// rows called L3 and the size is the only thing that separates them; the per-row notes
        /// name their row by this too, so a note can be attached to the row it qualifies.
        /// </summary>
        private static string Label(List<BenchmarkRung> rungs, BenchmarkRung r)
        {
            return rungs.FindAll(x => x != null && x.Level == r.Level).Count > 1
                ? r.Level + " " + Size(r.CacheBytes)
                : r.Level;
        }

        /// <summary>Whether the row published an all-core figure for a count to describe.</summary>
        private static bool Published(BenchmarkRung r)
        {
            return r.ReadAllGBs > 0 || r.WriteAllGBs > 0 || r.CopyAllGBs > 0;
        }

        /// <summary>
        /// The crew under the level, the line the window prints. Empty where the crew is the
        /// package's and the caption holds as it stands, and where the row has no all-core figure
        /// for a count to describe.
        /// </summary>
        private static string Crew(BenchmarkRung r, CultureInfo inv)
        {
            if (!Published(r) || r.PackageCores <= 0 || r.AllCoreWorkers <= 0
                || r.AllCoreWorkers >= r.PackageCores)
                return "";

            return "<div class='unit'>" + r.AllCoreWorkers.ToString(inv) + " of "
                + r.PackageCores.ToString(inv) + " cores</div>";
        }

        /// <summary>
        /// The crew the run's all-core columns were taken from, or zero where no row published a
        /// figure to have taken them for - a run that says nothing about what its caption covered.
        /// </summary>
        private static int PackageCrew(List<BenchmarkRung> rungs)
        {
            int crew = 0;
            foreach (var r in rungs)
                if (r != null && Published(r) && r.PackageCores > crew)
                    crew = r.PackageCores;
            return crew;
        }

        private static string Num(double gbs)
        {
            return "<td>" + (gbs > 0 ? gbs.ToString("F0", CultureInfo.InvariantCulture) : "-") + "</td>";
        }

        private static string Cell(string text)
        {
            return "<td>" + Escape(text) + "</td>";
        }

        private static string Size(long bytes)
        {
            return bytes >= 1024 * 1024
                ? (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB"
                : (bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB";
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
