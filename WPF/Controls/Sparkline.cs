using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace ZenTimings.Controls
{
    /// <summary>
    /// Tiny history plot for a single numeric series. Deliberately a bare FrameworkElement with a
    /// hand-rolled OnRender: no charting dependency, no template, cheap enough to sit in the
    /// main window and repaint on every refresh tick.
    /// </summary>
    public class Sparkline : FrameworkElement
    {
        public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
            "Values",
            typeof(IEnumerable<double>),
            typeof(Sparkline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
            "LineBrush",
            typeof(Brush),
            typeof(Sparkline),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LineThicknessProperty = DependencyProperty.Register(
            "LineThickness",
            typeof(double),
            typeof(Sparkline),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public IEnumerable<double> Values
        {
            get { return (IEnumerable<double>)GetValue(ValuesProperty); }
            set { SetValue(ValuesProperty, value); }
        }

        public Brush LineBrush
        {
            get { return (Brush)GetValue(LineBrushProperty); }
            set { SetValue(LineBrushProperty, value); }
        }

        public double LineThickness
        {
            get { return (double)GetValue(LineThicknessProperty); }
            set { SetValue(LineThicknessProperty, value); }
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            var source = Values;
            if (source == null)
                return;

            var points = new List<double>(source);
            if (points.Count < 2)
                return;

            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 1 || height <= 1)
                return;

            double min = double.MaxValue;
            double max = double.MinValue;
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i] < min) min = points[i];
                if (points[i] > max) max = points[i];
            }

            // A flat series would divide by zero; render it as a centred line instead.
            double range = max - min;
            if (range < 0.0001)
            {
                range = 1.0;
                min = min - 0.5;
            }

            var brush = LineBrush ?? Brushes.Gray;
            var pen = new Pen(brush, LineThickness);
            pen.Freeze();

            double stepX = width / (points.Count - 1);
            double inset = LineThickness;          // keep the stroke inside the bounds
            double usableHeight = Math.Max(1.0, height - inset * 2);

            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                for (int i = 0; i < points.Count; i++)
                {
                    double x = i * stepX;
                    double y = inset + usableHeight - ((points[i] - min) / range) * usableHeight;

                    if (i == 0)
                        ctx.BeginFigure(new Point(x, y), false, false);
                    else
                        ctx.LineTo(new Point(x, y), true, false);
                }
            }

            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
    }
}
