using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ZenTimings
{
    /// <summary>
    /// Grabs a picture of one specific window.
    /// </summary>
    /// <remarks>
    /// Not the same thing as capturing the active window: the clipboard shortcut is global, so
    /// whatever the user is looking at when they press it is the active window - a game, a browser
    /// - and that is never what they meant to copy. Asking the window to paint itself also means
    /// the shot is correct even when the app is behind something else or minimised, and it needs no
    /// focus stealing.
    /// </remarks>
    public static class WindowCapture
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        /// <summary>
        /// PW_RENDERFULLCONTENT. Without it a composited (WPF) window prints as a blank rectangle,
        /// because its content lives in a DWM surface rather than in the window DC.
        /// </summary>
        private const uint PW_RENDERFULLCONTENT = 2;

        /// <summary>Returns null rather than throwing - a screenshot is never worth a crash.</summary>
        public static Bitmap Capture(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return null;

            Bitmap bitmap = null;

            try
            {
                RECT bounds;
                if (!GetWindowRect(hwnd, out bounds))
                    return null;

                int width = bounds.Right - bounds.Left;
                int height = bounds.Bottom - bounds.Top;

                // A minimised window reports a tiny off-screen rectangle; there is nothing to copy.
                if (width < 32 || height < 32)
                    return null;

                bitmap = new Bitmap(width, height);
                bool printed;

                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    IntPtr hdc = graphics.GetHdc();
                    try
                    {
                        printed = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                    }
                    finally
                    {
                        // Exactly once, and before the bitmap is touched again - releasing twice or
                        // disposing the bitmap while its DC is checked out corrupts GDI state.
                        graphics.ReleaseHdc(hdc);
                    }
                }

                if (!printed)
                {
                    bitmap.Dispose();
                    return null;
                }

                return bitmap;
            }
            catch
            {
                if (bitmap != null)
                    bitmap.Dispose();

                return null;
            }
        }
    }
}
