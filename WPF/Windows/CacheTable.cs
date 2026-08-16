using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ZenTimings.Localization;

namespace ZenTimings.Windows
{
    /// <summary>
    /// The result grid: one AIDA-style bordered table. DRAM rides the first row - the figure the
    /// window exists for - with the cache ladder under it, all-core and one-core side by side
    /// under grouped headings so the two crews can never drift out of column alignment. Cell
    /// grounds carry a four-step tint of one steel hue, lighter for the faster level, and when a
    /// comparable baseline is pinned each cell adds a second line with the delta against it.
    /// </summary>
    internal static class CacheTable
    {
        private const string Dash = "-";

        // level | all r/w/c | one r/w/c | latency | size
        // The Result tab's ScrollViewer will not scroll sideways, so whatever the sum of these
        // plus the frame's left edge overruns the pane is cut rather than reachable - and the pane
        // is 664 DIP at the window's width once the vertical bar is out, 656 at its MinWidth. Each
        // column is its widest content plus a couple of DIP: the Turkish "kopyalama" heading sets
        // the six throughput columns, "gecikme" the latency one, "1024 MB" the size one, and the
        // level column has to hold "L3 avg*", which is wider than any cache's name.
        private static readonly double[] Cols = { 61, 77, 77, 77, 77, 77, 77, 63, 65 };

        private static readonly FontFamily Mono = new FontFamily("Consolas");
        private static readonly Brush GridLine = Frozen(0x3E, 0x43, 0x4C);
        private static readonly Brush HeaderFill = Frozen(0x26, 0x28, 0x2E);

        // Index = tint step: 0 carries DRAM, 3 carries L1d, and the ground lightens as it goes.
        // The two lightest steps brighten their text so the digits stay clear of the ground.
        private static readonly Brush[] TintFill =
        {
            Frozen(0x1E, 0x25, 0x2E), Frozen(0x24, 0x2E, 0x3B),
            Frozen(0x2B, 0x3A, 0x4C), Frozen(0x33, 0x45, 0x5C),
        };
        private static readonly Brush[] TintText =
        {
            Frozen(0x8F, 0xA3, 0xB8), Frozen(0x8F, 0xA3, 0xB8),
            Frozen(0xB4, 0xC1, 0xCF), Frozen(0xC6, 0xD2, 0xDE),
        };

        private static readonly Brush DeltaGood = Frozen(0x6F, 0xBF, 0x9B);
        private static readonly Brush DeltaBad = Frozen(0xD2, 0xA4, 0x5C);
        // Bright enough to survive the strongest tint step; a ±0 that vanishes on the L1d band
        // reads as a missing delta rather than a quiet one.
        private static readonly Brush DeltaZero = Frozen(0x84, 0x8A, 0x94);
        private static readonly Brush Stripe = Frozen(0x8F, 0xA3, 0xB8);

        /// <summary>One cell of the grid; everything the renderer needs, nothing about layout.</summary>
        private sealed class Cell
        {
            public string Text = Dash;
            public bool Label;
            public bool Lead;
            public bool Derived;
            public int Tint = -1;
            public string Delta;
            public Brush DeltaBrush;
            public string Tip;

            /// <summary>A second, quieter line under a label: the cores behind the row's
            /// all-core figures, where they are not the package's.</summary>
            public string Sub;
        }

        /// <summary>True when something was drawn, false when there was nothing to show.</summary>
        public static bool Build(StackPanel target, CacheLadderResult ladder,
            MemoryLatencyResult latency, MemoryBandwidthResult bandwidth, BenchmarkRun baseline)
        {
            target.Children.Clear();

            // Either phase alone earns the DRAM row: a run whose walk failed but whose bandwidth
            // pass measured still has figures the grid exists to show.
            bool haveWalk = latency != null && latency.Ok;
            bool haveDram = haveWalk || bandwidth != null && bandwidth.Ok;
            bool haveLadder = ladder != null && ladder.Ok && ladder.Rungs != null && ladder.Rungs.Count > 0;
            if (!haveDram && !haveLadder)
                return false;

            var inv = CultureInfo.InvariantCulture;
            Section(target, Loc.T("Bench.SecGrid"));

            var grid = new Grid();
            foreach (var w in Cols)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });

            int row = 0;

            // Grouped heading: all cores over the first trio, one core over the second.
            NextRow(grid);
            Place(grid, GroupCell(null), row, 0, 1);
            Place(grid, GroupCell(Loc.T("Bench.SecAll")), row, 1, 3);
            Place(grid, GroupCell(Loc.T("Bench.SecOne")), row, 4, 3);
            // Two empty cells, not one spanning both: a merged cell over latency and size would
            // read as a third group that does not exist.
            Place(grid, GroupCell(null), row, 7, 1);
            Place(grid, GroupCell(null), row, 8, 1);
            row++;

            NextRow(grid);
            Place(grid, HeaderCell(null, null), row, 0, 1);
            for (int crew = 0; crew < 2; crew++)
            {
                Place(grid, HeaderCell(Loc.T("Bench.ColRead"), "GB/s"), row, 1 + crew * 3, 1);
                Place(grid, HeaderCell(Loc.T("Bench.ColWrite"), "GB/s"), row, 2 + crew * 3, 1);
                Place(grid, HeaderCell(Loc.T("Bench.ColCopy"), "GB/s"), row, 3 + crew * 3, 1);
            }
            Place(grid, HeaderCell(Loc.T("Bench.ColLat"), "ns"), row, 7, 1);
            Place(grid, HeaderCell(Loc.T("Bench.ColSize"), null), row, 8, 1);
            row++;

            // DRAM first: the row the window exists for. A one-core DRAM walk has no bandwidth,
            // so that side stays dashes; the size column carries the buffer that measured it.
            if (haveDram)
            {
                var cells = NewRow("DRAM", null);
                // The tint scale rewards speed, which leaves the headline row the faintest on the
                // grid - the accent edge is what keeps the eye landing on DRAM first.
                cells[0].Lead = true;
                if (bandwidth != null && bandwidth.Ok)
                {
                    Fill(cells[1], bandwidth.ReadGBs, "F1", 0);
                    Fill(cells[2], bandwidth.WriteGBs, "F1", 0);
                    Fill(cells[3], bandwidth.CopyGBs, "F1", 0);
                    if (SameBandwidthPages(bandwidth, baseline))
                    {
                        SetDelta(cells[1], bandwidth.ReadGBs, baseline.ReadGBs, "F1", false);
                        SetDelta(cells[2], bandwidth.WriteGBs, baseline.WriteGBs, "F1", false);
                        SetDelta(cells[3], bandwidth.CopyGBs, baseline.CopyGBs, "F1", false);
                    }
                }
                if (haveWalk)
                {
                    Fill(cells[7], latency.Nanoseconds, "F1", 0);
                    if (baseline != null)
                        SetDelta(cells[7], latency.Nanoseconds, baseline.LatencyNs, "F1", true);
                }
                if (latency != null && latency.BufferMegabytes > 0)
                    cells[8].Text = latency.BufferMegabytes.ToString(inv) + " MB";
                EmitRow(grid, ref row, cells);
            }

            // Set where the crews disagree and an all-core delta was expected anyway: the legend
            // promises deltas, so the cells that cannot carry one say why under the grid.
            bool crewWithheld = false;

            // Set where a cache row found no counterpart in the baseline at all: such a row
            // carries no delta of any kind under a legend that promises them, beside a DRAM row
            // that has its own - which reads as "unchanged" unless the grid says otherwise.
            bool rungUnpaired = false;

            if (haveLadder)
            {
                // Rungs pair on what they are - level and size - never on position. The ladder
                // lists the pinned core's L3 die first and the pin is the latency scan's winner,
                // so on a package holding two different L3s the pair of rows arrives in either
                // order run to run; paired by index one flip costs every row its delta, L1d and
                // L2 included, whose counterparts are identical. Size is half the identity: Level
                // alone would delta the 96 MB die against the 32 MB one.
                var stored = baseline != null ? baseline.Rungs : null;
                var pair = new List<BenchmarkRung>(ladder.Rungs.Count);
                foreach (var rung in ladder.Rungs)
                    pair.Add(stored == null ? null : stored.Find(b => b != null
                        && b.Level == rung.Level && b.CacheBytes == rung.CacheBytes));

                rungUnpaired = baseline != null && pair.Exists(p => p == null);

                // An all-core figure belongs to a crew, and the ladder's shape cannot tell two
                // crews apart: 48 KB / 1 MB / 96 MB is an eight-core part and a sixteen-core one
                // alike, and on one machine it is also a run pinned inside one L3 against a run
                // whose topology query failed and read the package. The rungs of a run share a
                // package, so a crew that moved did not move for one level - one disagreement
                // withholds the trio on every row. Only a rung with a figure on both sides is
                // evidence: one the run never reached says nothing about the crew.
                bool sameCrew = true;
                for (int i = 0; sameCrew && i < pair.Count; i++)
                    sameCrew = pair[i] == null || !WouldDelta(ladder.Rungs[i], pair[i])
                        || SameCrew(ladder.Rungs[i], pair[i]);

                for (int i = 0; i < ladder.Rungs.Count; i++)
                {
                    var rung = ladder.Rungs[i];
                    var was = pair[i];
                    int tint = TintOf(rung.Level);

                    // The crew rides in the level cell, where a screenshot carries it: the star
                    // and its tooltip say why the row deviates, this says by how much.
                    var cells = NewRow(AllStar(rung), AllTip(rung));
                    cells[0].Sub = Crew(rung);
                    Fill(cells[1], rung.ReadAllGBs, "F0", tint);
                    Fill(cells[2], rung.WriteAllGBs, "F0", tint);
                    Fill(cells[3], rung.CopyAllGBs, "F0", tint);
                    Fill(cells[4], rung.ReadGBs, "F0", tint);
                    Fill(cells[5], rung.WriteGBs, "F0", tint);
                    Fill(cells[6], rung.CopyGBs, "F0", tint);
                    Fill(cells[7], rung.Nanoseconds, "F2", tint);
                    cells[8].Text = Size(rung.CacheBytes);

                    if (was != null)
                    {
                        // The one-core trio and the latency were taken inside this rung's own
                        // cache on either run, so they compare across core counts and are kept.
                        // The all-core trio is the crew's figure and has nothing to compare to.
                        if (sameCrew)
                        {
                            SetDelta(cells[1], rung.ReadAllGBs, was.ReadAllGBs, "F0", false);
                            SetDelta(cells[2], rung.WriteAllGBs, was.WriteAllGBs, "F0", false);
                            SetDelta(cells[3], rung.CopyAllGBs, was.CopyAllGBs, "F0", false);
                        }
                        else
                        {
                            crewWithheld |= WouldDelta(rung, was);
                        }

                        SetDelta(cells[4], rung.ReadGBs, was.ReadGBs, "F0", false);
                        SetDelta(cells[5], rung.WriteGBs, was.WriteGBs, "F0", false);
                        SetDelta(cells[6], rung.CopyGBs, was.CopyGBs, "F0", false);
                        SetDelta(cells[7], rung.Nanoseconds, was.Nanoseconds, "F2", true);
                    }

                    EmitRow(grid, ref row, cells);
                }

                // Multi-die parts get the mean row - what a thread on a random die can expect.
                var dies = ladder.Rungs.FindAll(r => r.Level == "L3");
                if (dies.Count >= 2)
                {
                    var cells = NewRow(MeanStar(dies), MeanTip(dies));
                    cells[0].Derived = true;
                    cells[0].Sub = MeanCrew(dies);

                    // No delta on this row ever, on any machine: a mean of two caches has no
                    // counterpart in the file to read against. Not the missing-counterpart note
                    // below - that one names a baseline gap, and this is the row's own shape,
                    // which its label and its tooltip already give as a mean.
                    Fill(cells[1], MeanOf(dies, r => r.ReadAllGBs), "F0", TintOf("L3"));
                    Fill(cells[2], MeanOf(dies, r => r.WriteAllGBs), "F0", TintOf("L3"));
                    Fill(cells[3], MeanOf(dies, r => r.CopyAllGBs), "F0", TintOf("L3"));
                    Fill(cells[4], MeanOf(dies, r => r.ReadGBs), "F0", TintOf("L3"));
                    Fill(cells[5], MeanOf(dies, r => r.WriteGBs), "F0", TintOf("L3"));
                    Fill(cells[6], MeanOf(dies, r => r.CopyGBs), "F0", TintOf("L3"));
                    Fill(cells[7], MeanOf(dies, r => r.Nanoseconds), "F2", TintOf("L3"));
                    EmitRow(grid, ref row, cells);
                }
            }

            // A single-pixel frame; each cell draws its own right and bottom edge. Left-aligned
            // and sized to its fixed columns.
            target.Children.Add(new Border
            {
                Child = grid,
                HorizontalAlignment = HorizontalAlignment.Left,
                BorderThickness = new Thickness(1, 1, 0, 0),
                BorderBrush = GridLine,
            });

            Footnotes(target, ladder, bandwidth, baseline, haveLadder, crewWithheld, rungUnpaired);
            return true;
        }

        // ---- footnotes -------------------------------------------------------------------------

        /// <summary>
        /// Random (it has no column), the tint legend, the baseline the deltas read against, what
        /// the cache rows' all-core columns covered, and the star's meaning. No vector width here
        /// - that detail lives in the HTML export.
        /// </summary>
        private static void Footnotes(StackPanel target, CacheLadderResult ladder,
            MemoryBandwidthResult bandwidth, BenchmarkRun baseline, bool haveLadder,
            bool crewWithheld, bool rungUnpaired)
        {
            var inv = CultureInfo.InvariantCulture;
            var block = new TextBlock
            {
                FontSize = 11,
                Opacity = 0.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 12, 0, 0),
            };

            if (bandwidth != null && bandwidth.Ok && bandwidth.RandomGBs > 0)
            {
                var random = new Run(string.Format(inv, Loc.T("Bench.FootRandom"),
                    bandwidth.RandomGBs.ToString("F1", inv)));
                block.Inlines.Add(random);
                block.ToolTip = Loc.T("Bench.RandomTip");

                if (baseline != null && baseline.RandomGBs > 0
                    && SameBandwidthPages(bandwidth, baseline))
                {
                    // Not left at its Dash default: SetDelta treats a dash as "no figure here".
                    var probe = new Cell { Text = "" };
                    SetDelta(probe, bandwidth.RandomGBs, baseline.RandomGBs, "F1", false);
                    if (probe.Delta != null)
                        block.Inlines.Add(new Run(" (" + probe.Delta + ")") { Foreground = probe.DeltaBrush });
                }
            }

            if (baseline != null)
            {
                if (block.Inlines.Count > 0)
                    block.Inlines.Add(new LineBreak());
                block.Inlines.Add(new Run(string.Format(Loc.T("Bench.DeltaLegend"), baseline.Title)));

                // The legend promises deltas, so the one row that cannot have them says why. Only
                // where one was expected: a baseline with no bandwidth figures has nothing to
                // compare and needs no note.
                if (bandwidth != null && bandwidth.Ok && baseline.BandwidthText.Length > 0
                    && !SameBandwidthPages(bandwidth, baseline))
                {
                    block.Inlines.Add(new LineBreak());
                    block.Inlines.Add(new Run(Loc.T("Bench.NoBandwidthDelta")));
                }

                if (crewWithheld)
                {
                    block.Inlines.Add(new LineBreak());
                    block.Inlines.Add(new Run(Loc.T("Bench.NoAllCoreDelta")));
                }

                // A cache row pairs to the baseline's row for the same cache, so a level or a size
                // the baseline never measured leaves that row blank throughout - which under a
                // legend promising deltas, beside rows that carry them, reads as unchanged.
                if (rungUnpaired)
                {
                    block.Inlines.Add(new LineBreak());
                    block.Inlines.Add(new Run(Loc.T("Bench.NoRungDelta")));
                }
            }

            // No standing line about the crew: the rows that deviate carry the count under their
            // own level and the star sends the reader to the reason. A sentence stating the
            // default for every other row is one more line under a grid that is already long,
            // and the export - which has no hover - is where it earns its place.
            bool anyStar = haveLadder && ladder.Rungs.Exists(Starred);
            if (anyStar)
            {
                if (block.Inlines.Count > 0)
                    block.Inlines.Add(new LineBreak());
                block.Inlines.Add(new Run(Loc.T("Bench.StarMeaning")));
            }

            // Nothing to say is no footnote at all, not an empty line under the grid.
            if (block.Inlines.Count > 0)
                target.Children.Add(block);
        }

        // ---- row plumbing ----------------------------------------------------------------------

        private static Cell[] NewRow(string level, string tip)
        {
            var cells = new Cell[Cols.Length];
            for (int i = 0; i < cells.Length; i++)
                cells[i] = new Cell { Tip = tip };
            cells[0].Text = level;
            cells[0].Label = true;
            return cells;
        }

        private static void Fill(Cell cell, double value, string format, int tint)
        {
            if (value <= 0)
                return;
            cell.Text = value.ToString(format, CultureInfo.InvariantCulture);
            cell.Tint = tint;
        }

        /// <summary>
        /// Whether the baseline's bandwidth figures were measured on the page mode the ones on
        /// screen were. The pass asks for two buffers where the walk asks for one and can fall
        /// back on its own, so the walk's flag - the one the baseline gate reads - does not answer
        /// this, and a run that recorded no mode of its own answers nothing at all.
        /// </summary>
        private static bool SameBandwidthPages(MemoryBandwidthResult bandwidth,
            BenchmarkRun baseline)
        {
            return bandwidth != null && baseline != null && baseline.BandwidthLargePages.HasValue
                && baseline.BandwidthLargePages.Value == bandwidth.LargePages;
        }

        /// <summary>
        /// The second line of a cell: signed difference against the baseline, green when the run
        /// improved, amber when it slipped, muted when it moved less than the shown precision.
        /// </summary>
        private static void SetDelta(Cell cell, double current, double previous, string format,
            bool downIsGood)
        {
            if (current <= 0 || previous <= 0 || cell.Text == Dash)
                return;

            double delta = current - previous;
            double epsilon = format == "F0" ? 0.5 : format == "F1" ? 0.05 : 0.005;
            if (Math.Abs(delta) < epsilon)
            {
                cell.Delta = "±0";
                cell.DeltaBrush = DeltaZero;
                return;
            }

            cell.Delta = (delta > 0 ? "+" : "") + delta.ToString(format, CultureInfo.InvariantCulture);
            cell.DeltaBrush = downIsGood == delta < 0 ? DeltaGood : DeltaBad;
        }

        private static void EmitRow(Grid grid, ref int row, Cell[] cells)
        {
            NextRow(grid);
            for (int c = 0; c < cells.Length; c++)
                Place(grid, DataCell(cells[c]), row, c, 1);
            row++;
        }

        private static void NextRow(Grid grid)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        private static void Place(Grid grid, UIElement cell, int row, int col, int span)
        {
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            if (span > 1)
                Grid.SetColumnSpan(cell, span);
            grid.Children.Add(cell);
        }

        // ---- cell rendering --------------------------------------------------------------------

        private static Border GroupCell(string text)
        {
            return new Border
            {
                Child = new TextBlock
                {
                    Text = text ?? "",
                    FontSize = 11,
                    Opacity = 0.55,
                    TextAlignment = TextAlignment.Center,
                    Padding = new Thickness(6, 3, 6, 3),
                },
                Background = HeaderFill,
                BorderThickness = new Thickness(0, 0, 1, 1),
                BorderBrush = GridLine,
            };
        }

        // Header cells carry the unit on a second, quieter line - "read" over "GB/s" - so the
        // abbreviation reads as a unit rather than clutter in the title.
        private static Border HeaderCell(string text, string unit)
        {
            FrameworkElement content;
            var main = new TextBlock
            {
                Text = text ?? "",
                FontSize = 13,
                Opacity = 0.6,
                TextAlignment = TextAlignment.Center,
            };

            if (unit != null)
            {
                var sub = new TextBlock
                {
                    Text = unit,
                    FontSize = 10,
                    Opacity = 0.4,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 1, 0, 0),
                };
                var stack = new StackPanel { Margin = new Thickness(6, 6, 6, 6) };
                stack.Children.Add(main);
                stack.Children.Add(sub);
                content = stack;
            }
            else
            {
                main.Padding = new Thickness(6, 7, 6, 7);
                content = main;
            }

            return new Border
            {
                Child = content,
                Background = HeaderFill,
                BorderThickness = new Thickness(0, 0, 1, 1),
                BorderBrush = GridLine,
            };
        }

        private static Border DataCell(Cell cell)
        {
            var value = new TextBlock
            {
                Text = cell.Text,
                FontSize = 13,
                TextAlignment = cell.Label ? TextAlignment.Left : TextAlignment.Center,
            };

            FrameworkElement content;
            if (cell.Label)
            {
                // A derived row (the L3 mean) must not read as a fourth physical cache.
                value.FontWeight = cell.Derived ? FontWeights.Normal : FontWeights.SemiBold;
                if (cell.Derived)
                    value.Opacity = 0.75;
                value.Padding = new Thickness(8, 7, 8, 7);
                value.VerticalAlignment = VerticalAlignment.Center;

                if (cell.Sub != null)
                {
                    // The crew rides under the level the way the header's unit rides under its
                    // title. Not a column of its own: a tenth would have to print something on
                    // every row, DRAM included, and the grid is already as wide as the window
                    // carries.
                    value.Padding = new Thickness(8, 6, 8, 0);
                    var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    stack.Children.Add(value);
                    stack.Children.Add(new TextBlock
                    {
                        Text = cell.Sub,
                        FontSize = 10,
                        Opacity = 0.55,
                        Padding = new Thickness(8, 1, 2, 6),
                    });
                    content = stack;
                }
                else
                {
                    content = value;
                }

                if (cell.Lead)
                {
                    // 3 px edge + 5 px pad keeps the text on the same x as the other labels.
                    value.Padding = new Thickness(5, 7, 8, 7);
                    var dock = new DockPanel();
                    var edge = new Border { Width = 3, Background = Stripe };
                    DockPanel.SetDock(edge, Dock.Left);
                    dock.Children.Add(edge);
                    dock.Children.Add(content);
                    content = dock;
                }
            }
            else
            {
                value.FontFamily = Mono;
                if (cell.Text == Dash)
                    value.Opacity = 0.35;
                else if (cell.Tint >= 0)
                    value.Foreground = TintText[cell.Tint];
                else
                    value.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextColor");

                if (cell.Delta != null)
                {
                    var stack = new StackPanel
                    {
                        Margin = new Thickness(6, 5, 6, 5),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    stack.Children.Add(value);
                    stack.Children.Add(new TextBlock
                    {
                        Text = cell.Delta,
                        FontSize = 10,
                        FontFamily = Mono,
                        Foreground = cell.DeltaBrush,
                        TextAlignment = TextAlignment.Center,
                        Margin = new Thickness(0, 1, 0, 0),
                    });
                    content = stack;
                }
                else
                {
                    value.Padding = new Thickness(6, 7, 6, 7);
                    value.VerticalAlignment = VerticalAlignment.Center;
                    content = value;
                }
            }

            var border = new Border
            {
                Child = content,
                BorderThickness = new Thickness(0, 0, 1, 1),
                BorderBrush = GridLine,
            };
            if (cell.Tint >= 0 && !cell.Label && cell.Text != Dash)
                border.Background = TintFill[cell.Tint];
            if (cell.Tip != null)
                border.ToolTip = cell.Tip;
            return border;
        }

        // ---- labels & helpers ------------------------------------------------------------------

        private static int TintOf(string level)
        {
            switch (level)
            {
                case "L1d": return 3;
                case "L2": return 2;
                default: return 1;
            }
        }

        /// <summary>Whether the row published an all-core figure for a caveat to qualify.</summary>
        private static bool AllCore(CacheRung r)
        {
            return r.ReadAllGBs > 0 || r.WriteAllGBs > 0 || r.CopyAllGBs > 0;
        }

        /// <summary>
        /// Whether the all-core trio came from one cache's own cores rather than the package's.
        /// Gated on figures that exist: the scope of a measurement that never happened qualifies
        /// nothing, and a row of dashes claims no crew.
        /// </summary>
        private static bool OneCache(CacheRung r)
        {
            return AllCore(r) && r.PackageCores > r.AllCoreOf;
        }

        /// <summary>
        /// Whether the all-core trio came from fewer cores than the cache serves. Gated on figures
        /// for the reason the scope above is: the crew goes on record before the pass runs and
        /// outlives one that then publishes nothing - a refused worker pin, an allocation that
        /// failed, a join that timed out. Withholding at the L2 floor is the other shape and not
        /// this one; there the crew is zero and the absence is itself the fact.
        /// </summary>
        private static bool ReducedCrew(CacheRung r)
        {
            return AllCore(r) && r.AllCoreWorkers > 0 && r.AllCoreWorkers < r.AllCoreOf;
        }

        // The row carries its caveats on the level label: 4K pages, one cache of several, a crew
        // smaller than that cache's cores, or all-core figures withheld outright where the slices
        // sink into L2. One predicate, because the footnote that explains the mark is gated on it
        // too and the two must not drift apart.
        private static bool Starred(CacheRung r)
        {
            return !r.LargePages || r.AllCoreOf > 0 && r.AllCoreWorkers == 0
                || ReducedCrew(r) || OneCache(r);
        }

        private static string AllStar(CacheRung r)
        {
            return Starred(r) ? r.Level + "*" : r.Level;
        }

        /// <summary>
        /// The cores behind the row's all-core figures and the cores the package holds, or null
        /// where they are the same and the heading holds as it stands. One count for either reason
        /// a crew shrinks - the cache serves part of the package, workers were shed at the L2
        /// floor - because it describes the figures printed beside it whichever reason applies;
        /// the tooltip is where the reasons stay apart.
        /// </summary>
        private static string Crew(CacheRung r)
        {
            if (!AllCore(r) || r.PackageCores <= 0 || r.AllCoreWorkers <= 0
                || r.AllCoreWorkers >= r.PackageCores)
                return null;

            return string.Format(CultureInfo.InvariantCulture, Loc.T("Bench.CrewOf"),
                r.AllCoreWorkers, r.PackageCores);
        }

        /// <summary>
        /// Whether the baseline's all-core figures came from the crew this run's came from. A run
        /// written before the package count was recorded is compared on the two crew fields every
        /// stored rung carries, which is what tells an eight-core part from a sixteen-core one
        /// behind the same ladder shape.
        /// </summary>
        private static bool SameCrew(CacheRung now, BenchmarkRung was)
        {
            // A side that recorded no crew is not evidence of a different one: the fields are
            // younger than parts of the file, and unrecorded must not read as "no cores".
            if (now.AllCoreOf <= 0 || was.AllCoreOf <= 0)
                return true;

            if (now.PackageCores > 0 && was.PackageCores > 0
                && now.PackageCores != was.PackageCores)
                return false;

            return now.AllCoreOf == was.AllCoreOf && now.AllCoreWorkers == was.AllCoreWorkers;
        }

        /// <summary>Whether the row would carry an all-core delta if the crews agreed.</summary>
        private static bool WouldDelta(CacheRung now, BenchmarkRung was)
        {
            return now.ReadAllGBs > 0 && was.ReadAllGBs > 0
                || now.WriteAllGBs > 0 && was.WriteAllGBs > 0
                || now.CopyAllGBs > 0 && was.CopyAllGBs > 0;
        }

        /// <summary>
        /// The row's page-mode line, or null where every buffer behind it got large pages. The L3
        /// sentence is a TLB cost the walk pays over 512 pages of fallback, which takes the level
        /// and the buffer together: the L1d rung is four pages either way, and a row whose all-core
        /// workers alone fell back walked its own chain on large pages. The other rows name the
        /// mode and stop - the star has to be explained, and there is no measured effect to name.
        /// </summary>
        private static string Pages4K(CacheRung r)
        {
            if (!r.OwnLargePages)
                return Loc.T(r.Level == "L3" ? "Bench.Ladder4K" : "Bench.Ladder4KRow");

            return r.LargePages ? null : Loc.T("Bench.Ladder4KAll");
        }

        private static string AllTip(CacheRung r)
        {
            var parts = new List<string>();
            string pages = Pages4K(r);
            if (pages != null)
                parts.Add(pages);

            // Scope before crew: it names the denominator the line below subdivides, and the two
            // are different subjects - which cores the cache belongs to, and how many of those
            // were allowed to read it. The second line is the only place the size de-duplication
            // is disclosed: one row can stand for several caches of the same size.
            if (OneCache(r))
            {
                parts.Add(string.Format(CultureInfo.InvariantCulture,
                    Loc.T("Bench.AllOneCache"), r.AllCoreOf, r.PackageCores));
                parts.Add(Loc.T("Bench.AllCacheRows"));
            }

            if (ReducedCrew(r))
                parts.Add(string.Format(CultureInfo.InvariantCulture,
                    Loc.T("Bench.AllReduced"), r.AllCoreWorkers, r.AllCoreOf));
            else if (r.AllCoreOf > 0 && r.AllCoreWorkers == 0)
                parts.Add(Loc.T("Bench.AllL2Bound"));

            return parts.Count == 0 ? null : string.Join("\n", parts);
        }

        // A mean carries every caveat its inputs carry: the rows it averages are the rows the
        // reader would have to check, and a clean label over qualified figures is the one thing
        // the mark exists to prevent.
        private static string MeanStar(List<CacheRung> dies)
        {
            return dies.Exists(Starred) ? Loc.T("Bench.LevelAvg") + "*" : Loc.T("Bench.LevelAvg");
        }

        /// <summary>
        /// The crew line for the derived row, where every die it averages was read by the same
        /// one. Null where they differ - a mean of two crews is not a crew - and the die rows
        /// above carry their own counts either way.
        /// </summary>
        private static string MeanCrew(List<CacheRung> dies)
        {
            string crew = Crew(dies[0]);
            foreach (var d in dies)
                if (Crew(d) != crew)
                    return null;

            return crew;
        }

        private static string MeanTip(List<CacheRung> dies)
        {
            // The heavier of the two notes wins: a mean that includes a die whose own walk fell
            // back carries that walk's cost, whatever the dies beside it managed.
            var worst = dies.Find(d => !d.OwnLargePages) ?? dies.Find(d => !d.LargePages);
            string pages = worst != null ? Pages4K(worst) : null;

            return pages != null
                ? Loc.T("Bench.AvgTip") + "\n" + pages
                : Loc.T("Bench.AvgTip");
        }

        /// <summary>The mean across dies, or zero when any die withheld its figure.</summary>
        private static double MeanOf(List<CacheRung> dies, Func<CacheRung, double> value)
        {
            double sum = 0;
            foreach (var die in dies)
            {
                double v = value(die);
                if (v <= 0)
                    return 0;
                sum += v;
            }
            return sum / dies.Count;
        }

        private static string Size(long bytes)
        {
            return bytes >= 1024 * 1024
                ? (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB"
                : (bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB";
        }

        private static void Section(StackPanel target, string title)
        {
            target.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Opacity = 0.6,
                Margin = new Thickness(2, 0, 0, 6),
            });
        }

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
