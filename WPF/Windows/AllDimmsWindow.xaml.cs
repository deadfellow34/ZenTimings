using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZenTimings.Windows
{
    /// <summary>
    /// Every populated channel rendered as the main window's own timings panel, side by side -
    /// the exact view the app shows, once per channel instead of one at a time through the
    /// module selector.
    /// </summary>
    public partial class AllDimmsWindow : ThemedAdonisWindow
    {
        public sealed class ChannelShot
        {
            public string Header;

            /// <summary>PMIC, rank, die and capacity. Null on a platform with no SPD telemetry.</summary>
            public string Details;

            public ImageSource Image;
        }

        public AllDimmsWindow(List<ChannelShot> channels, List<Rect> highlights)
        {
            InitializeComponent();

            // The panels are photographed at their natural size, so the window can ask for more
            // than the screen has - cap it at the work area and let the ScrollViewer take over.
            MaxWidth = SystemParameters.WorkArea.Width;
            MaxHeight = SystemParameters.WorkArea.Height;

            // Built once and frozen: one pair for however many cells are marked, and a frozen
            // brush can be shared across the overlays without a per-rect clone.
            Brush markerFill, markerStroke;
            MarkerBrushes(out markerFill, out markerStroke);

            foreach (var channel in channels)
            {
                var column = new StackPanel();

                column.Children.Add(new TextBlock
                {
                    Text = channel.Header,
                    FontWeight = FontWeights.SemiBold,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 2),
                });

                // Wrapped and capped to the panel below it: the line runs longer than the timings
                // panel is wide, and letting it set the column width would space the panels apart.
                if (!string.IsNullOrEmpty(channel.Details))
                {
                    column.Children.Add(new TextBlock
                    {
                        Text = channel.Details,
                        FontSize = 11,
                        Opacity = 0.7,
                        TextAlignment = TextAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = channel.Image != null ? channel.Image.Width : 320,
                        Margin = new Thickness(0, 0, 0, 4),
                    });
                }

                if (channel.Image != null)
                {
                    var host = new Grid
                    {
                        Width = channel.Image.Width,
                        Height = channel.Image.Height,
                    };

                    host.Children.Add(new Image
                    {
                        Source = channel.Image,
                        Stretch = Stretch.None,
                        SnapsToDevicePixels = true,
                    });

                    // The cells that read differently per channel, marked on every column.
                    if (highlights != null && highlights.Count > 0)
                    {
                        var overlay = new Canvas();
                        foreach (var rect in highlights)
                        {
                            var marker = new System.Windows.Shapes.Rectangle
                            {
                                Width = rect.Width,
                                Height = rect.Height,
                                RadiusX = 2,
                                RadiusY = 2,
                                Fill = markerFill,
                                Stroke = markerStroke,
                                StrokeThickness = 1,
                            };
                            Canvas.SetLeft(marker, rect.X);
                            Canvas.SetTop(marker, rect.Y);
                            overlay.Children.Add(marker);
                        }
                        host.Children.Add(overlay);
                    }

                    column.Children.Add(host);
                }

                // A visible frame per channel - the divider between the panels.
                var frame = new Border
                {
                    Child = column,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 6, 8, 8),
                    Margin = new Thickness(4),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                };

                // Same as everywhere else in the app: a reference, so a theme switch repaints it.
                frame.SetResourceReference(Border.BorderBrushProperty, "SeparatorColor");

                ChannelsPanel.Children.Add(frame);
            }
        }

        /// <summary>
        /// The wash and outline that mark a differing cell, taken from the theme's accent.
        /// </summary>
        /// <remarks>
        /// A fixed colour cannot work here: the amber this used to draw is all but invisible on
        /// the Light theme's white panel. The accent is the one brush every theme defines and the
        /// one it already guarantees to be readable against its own background.
        /// </remarks>
        private void MarkerBrushes(out Brush fill, out Brush stroke)
        {
            var accent = TryFindResource("AccentTextColor") as SolidColorBrush;
            Color color = accent != null ? accent.Color : Color.FromRgb(0xE8, 0xB0, 0x4B);

            var washBrush = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B));
            var edgeBrush = new SolidColorBrush(Color.FromArgb(0xCC, color.R, color.G, color.B));
            washBrush.Freeze();
            edgeBrush.Freeze();

            fill = washBrush;
            stroke = edgeBrush;
        }
    }
}
