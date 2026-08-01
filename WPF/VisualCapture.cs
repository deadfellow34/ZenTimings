using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZenTimings
{
    /// <summary>
    /// Renders a live control to a bitmap, at the screen's own DPI.
    /// </summary>
    /// <remarks>
    /// Rendering from the visual tree rather than grabbing the screen: the result is clean even
    /// when another window covers the control, and it captures a panel that was momentarily
    /// pointed at another channel without the user ever seeing it.
    /// </remarks>
    internal static class VisualCapture
    {
        /// <summary>Null when there is nothing laid out to render.</summary>
        public static BitmapSource Render(FrameworkElement element)
        {
            if (element == null || element.ActualWidth < 1 || element.ActualHeight < 1)
                return null;

            var dpi = VisualTreeHelper.GetDpi(element);
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);

            bitmap.Render(element);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>The same bitmap as a PNG data URI payload, for embedding in exported HTML.</summary>
        public static string ToPngBase64(BitmapSource bitmap)
        {
            if (bitmap == null)
                return null;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                return Convert.ToBase64String(stream.ToArray());
            }
        }
    }
}
