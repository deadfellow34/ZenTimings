using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ZenTimings
{
    /// <summary>
    /// Draws a short live value (a temperature, a clock) into a 16x16 tray icon.
    /// </summary>
    public static class TrayIconRenderer
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const int SM_CXSMICON = 49;

        /// <summary>
        /// Tray icons are drawn at the shell's small-icon size, which is 16 only at 100% scaling -
        /// at 150% it is 24. Rendering at a fixed 16 and letting the shell upscale is what made the
        /// digits look smeared on a scaled display, so render at the real size instead.
        /// </summary>
        private static int IconSize()
        {
            try
            {
                int size = GetSystemMetrics(SM_CXSMICON);
                if (size >= 16 && size <= 64)
                    return size;
            }
            catch
            {
                // Fall through to the safe default.
            }

            return 16;
        }

        /// <summary>
        /// The largest bold Segoe UI that still fits <paramref name="text"/> inside a
        /// <paramref name="size"/> square. The old renderer used a fixed fraction of the icon
        /// instead, which left two digits small and lost in the middle of the icon - the whole
        /// reason the readout was hard to make out in the tray.
        /// </summary>
        private static Font FitFont(Graphics g, string text, int size, StringFormat format)
        {
            // 1px of breathing room each side so anti-aliasing does not clip against the edge.
            float budget = size - 2f;
            Font font = null;

            for (float emSize = size; emSize >= 5f; emSize -= 0.5f)
            {
                if (font != null)
                    font.Dispose();

                font = new Font("Segoe UI", emSize, FontStyle.Bold, GraphicsUnit.Pixel);
                SizeF measured = g.MeasureString(text, font, PointF.Empty, format);

                if (measured.Width <= budget && measured.Height <= size)
                    return font;
            }

            return font ?? new Font("Segoe UI", size * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        /// <summary>
        /// Builds an icon showing <paramref name="text"/> (1-3 characters read best at 16x16).
        /// The returned Icon owns its own handle and must be disposed by the caller.
        /// Returns null instead of throwing - a tray icon is never worth crashing over.
        /// </summary>
        public static Icon Create(string text, Color color)
        {
            if (text == null)
                text = string.Empty;

            if (text.Length > 3)
                text = text.Substring(0, 3);

            Bitmap bitmap = null;
            IntPtr handle = IntPtr.Zero;

            try
            {
                int size = IconSize();
                bitmap = new Bitmap(size, size);

                using (var g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                    // Typographic metrics, otherwise GDI+ pads every string with a chunk of blank
                    // space that would be measured as if it were part of the digits.
                    using (var format = new StringFormat(StringFormat.GenericTypographic)
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center,
                        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
                    })
                    using (var brush = new SolidBrush(color))
                    using (var font = FitFont(g, text, size, format))
                    {
                        // Nudged up by a hair: digits have no descender, so centring on the full
                        // line box leaves them looking low in the icon.
                        var box = new RectangleF(0, -size * 0.04f, size, size);
                        g.DrawString(text, font, brush, box, format);
                    }
                }

                handle = bitmap.GetHicon();

                // FromHandle does not take ownership, so clone into an Icon that does and
                // release the temporary GDI handle straight away.
                using (var shared = Icon.FromHandle(handle))
                {
                    return (Icon)shared.Clone();
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                    DestroyIcon(handle);

                if (bitmap != null)
                    bitmap.Dispose();
            }
        }
    }
}
