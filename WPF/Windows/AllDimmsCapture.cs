using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ZenStates.Core;
using ZenStates.Core.DRAM;

namespace ZenTimings.Windows
{
    /// <summary>
    /// Photographs the app's own timings panel once per memory channel, and works out which cells
    /// disagree between them.
    /// </summary>
    /// <remarks>
    /// The panel is pointed at each module in turn and rendered. The screen never sees the swap:
    /// painting happens after the caller's handler returns, and by then the original selection is
    /// back. Bindings are pushed synchronously first, because the view model marshals its change
    /// notifications and a queued one would photograph the previous module's values.
    ///
    /// Two kinds of row are re-pointed. The timings come from the channel's registers, so modules
    /// sharing a channel read the same ones. The PMIC rails are per module, which is why every
    /// module is rendered separately rather than sharing its channel's image - a column headed
    /// "B1" showing A1's MEM VDD would be a wrong number under the wrong name.
    /// </remarks>
    internal static class AllDimmsCapture
    {
        internal sealed class Result
        {
            public List<AllDimmsWindow.ChannelShot> Channels = new List<AllDimmsWindow.ChannelShot>();
            public List<Rect> Highlights = new List<Rect>();
        }

        /// <summary>Per-module binding paths that are not part of the channel's timings.</summary>
        private static readonly string[] ModuleRailPaths = { "SwaAdcV", "SwbAdcV", "VppAdcV" };

        /// <summary>
        /// One column per module.
        /// </summary>
        /// <param name="pointAtModule">
        /// Points the app at module i's own readings and returns them, so the capture can tell
        /// which of those rows actually differ. Null on a platform that has none.
        /// </param>
        /// <param name="restoreModule">
        /// Puts those readings back. Called inside the same restore step as the timings, because
        /// the highlight rectangles are measured off the panel afterwards - left showing the last
        /// module's values, a row that reads "N/A" there would be boxed at the wrong width.
        /// </param>
        /// <param name="describeModule">
        /// The module's SPD line - PMIC, rank, die, capacity - for the column heading. Null where
        /// there is no SPD telemetry to read.
        /// </param>
        public static Result Run(
            FrameworkElement panel,
            IList<MemoryModule> modules,
            Func<uint, BaseDramTimings> readTimings,
            Func<BaseDramTimings> getTimings,
            Action<BaseDramTimings> setTimings,
            Func<int, float[]> pointAtModule = null,
            Action restoreModule = null,
            Func<int, string> describeModule = null)
        {
            var result = new Result();
            if (panel == null || modules == null || modules.Count == 0)
                return result;

            var timingsByChannel = new List<BaseDramTimings>();
            var timingsByOffset = new Dictionary<uint, BaseDramTimings>();
            var railsByModule = new List<float[]>();
            var original = getTimings();

            try
            {
                for (int i = 0; i < modules.Count; i++)
                {
                    var module = modules[i];
                    string slot = !string.IsNullOrEmpty(module.Slot) ? module.Slot : module.DeviceLocator;
                    string part = (module.PartNumber ?? "").Trim();

                    // The registers are per channel, so they are read once however many modules
                    // sit on it; the rendering below is still per module.
                    BaseDramTimings timings;
                    if (!timingsByOffset.TryGetValue(module.DctOffset, out timings))
                    {
                        timings = readTimings(module.DctOffset);
                        timingsByOffset[module.DctOffset] = timings;
                        timingsByChannel.Add(timings);
                    }

                    setTimings(timings);
                    if (pointAtModule != null)
                        railsByModule.Add(pointAtModule(i));

                    PushCapturedBindings(panel);
                    panel.UpdateLayout();

                    result.Channels.Add(new AllDimmsWindow.ChannelShot
                    {
                        Header = (!string.IsNullOrEmpty(slot) ? slot + "  -  " : "") + part,
                        Details = describeModule != null ? describeModule(i) : null,
                        Image = VisualCapture.Render(panel),
                    });
                }
            }
            finally
            {
                setTimings(original);
                if (restoreModule != null)
                    restoreModule();

                PushCapturedBindings(panel);
                panel.UpdateLayout();
            }

            // One set of rectangles fits every column - the panel layout is the same in all of
            // them, only the values differ.
            var differing = DifferingTimings(timingsByChannel);
            differing.UnionWith(DifferingRails(railsByModule));
            result.Highlights = FindTimingRects(panel, differing);
            return result;
        }

        /// <summary>Binding paths of the timing properties whose values disagree between channels.</summary>
        private static HashSet<string> DifferingTimings(List<BaseDramTimings> timingsByChannel)
        {
            var differing = new HashSet<string>(StringComparer.Ordinal);
            var first = timingsByChannel.FirstOrDefault(t => t != null);
            if (first == null || timingsByChannel.Count < 2)
                return differing;

            foreach (var prop in first.GetType().GetProperties())
            {
                if (prop.GetIndexParameters().Length != 0 || !prop.CanRead)
                    continue;

                // Value types and strings, not just primitives: GDM, Cmd2T, BGS, PowerDown and
                // Nitro are wrapper structs whose ToString is what the panel shows.
                if (!prop.PropertyType.IsValueType && prop.PropertyType != typeof(string))
                    continue;

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var timings in timingsByChannel)
                {
                    object value = null;
                    try { if (timings != null) value = prop.GetValue(timings, null); }
                    catch { }
                    seen.Add(value != null ? value.ToString() : "");
                }

                if (seen.Count > 1)
                    differing.Add("Timings." + prop.Name);
            }

            return differing;
        }

        /// <summary>Binding paths of the per-module rails that read differently between modules.</summary>
        private static HashSet<string> DifferingRails(List<float[]> railsByModule)
        {
            var differing = new HashSet<string>(StringComparer.Ordinal);
            if (railsByModule.Count < 2)
                return differing;

            for (int rail = 0; rail < ModuleRailPaths.Length; rail++)
            {
                float? reference = null;
                foreach (var rails in railsByModule)
                {
                    float value = rails != null && rail < rails.Length ? rails[rail] : 0;
                    if (reference == null)
                    {
                        reference = value;
                    }
                    else if (Math.Abs(reference.Value - value) > 0.0005f)
                    {
                        differing.Add(ModuleRailPaths[rail]);
                        break;
                    }
                }
            }

            return differing;
        }

        /// <summary>
        /// Bounds of every visible element bound to one of the differing paths, relative to the
        /// panel - the coordinates the overlay draws at.
        /// </summary>
        private static List<Rect> FindTimingRects(FrameworkElement root, HashSet<string> differing)
        {
            var rects = new List<Rect>();
            if (root == null || differing.Count == 0)
                return rects;

            Walk(root, child =>
            {
                var binding = CapturedBinding(child);
                string path = binding != null && binding.Path != null ? binding.Path.Path : null;
                if (path == null || !differing.Contains(path))
                    return;

                var element = child as FrameworkElement;
                if (element == null || !element.IsVisible || element.ActualWidth <= 0)
                    return;

                try
                {
                    var bounds = element.TransformToAncestor(root)
                        .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                    bounds.Inflate(3, 1);
                    rects.Add(bounds);
                }
                catch { }
            });

            return rects;
        }

        /// <summary>Synchronously re-reads every visible binding the capture re-points.</summary>
        private static void PushCapturedBindings(DependencyObject root)
        {
            Walk(root, child =>
            {
                BindingExpression expression = null;
                if (child is TextBlock textBlock)
                    expression = textBlock.GetBindingExpression(TextBlock.TextProperty);
                else if (child is TextBox textBox)
                    expression = textBox.GetBindingExpression(TextBox.TextProperty);

                if (expression != null && IsCapturedPath(expression.ParentBinding))
                    expression.UpdateTarget();
            });
        }

        private static Binding CapturedBinding(DependencyObject child)
        {
            Binding binding = null;
            if (child is TextBlock textBlock)
                binding = BindingOperations.GetBinding(textBlock, TextBlock.TextProperty);
            else if (child is TextBox textBox)
                binding = BindingOperations.GetBinding(textBox, TextBox.TextProperty);

            return IsCapturedPath(binding) ? binding : null;
        }

        /// <summary>
        /// The channel's timings and the module's own rails. Everything else on the panel is the
        /// same for every column by nature - the APOB impedance block is one record for the
        /// platform - so re-reading it would only cost a pass.
        /// </summary>
        private static bool IsCapturedPath(Binding binding)
        {
            string path = binding != null && binding.Path != null ? binding.Path.Path : null;
            return path != null
                && (path.StartsWith("Timings.", StringComparison.Ordinal)
                    || Array.IndexOf(ModuleRailPaths, path) >= 0);
        }

        private static void Walk(DependencyObject node, Action<DependencyObject> visit)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                visit(child);
                Walk(child, visit);
            }
        }
    }
}
