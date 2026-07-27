using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ZenTimings
{
    public class Screenshot : IDisposable
    {
        // GDI stuff for window screenshot without shadows
        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int nxDest, int nyDest, int nWidth, int nHeight,
            IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int nHeight);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CreateDC(string lpszDriver, string lpszDevice, string lpszOutput,
            IntPtr lpInitData);

        [DllImport("gdi32.dll")]
        private static extern IntPtr DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ReleaseDC(IntPtr hWnd, IntPtr hDc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute,
            int cbAttribute);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct RECT
        {
            public readonly int left;
            public readonly int top;
            public readonly int right;
            public readonly int bottom;
        }

        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int SRCCOPY = 0x00CC0020;
        private const int CAPTUREBLT = 0x40000000;
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;
        private bool disposedValue;

        private Bitmap CaptureRegion(Rectangle region)
        {
            Bitmap result;

            // A DC for the DISPLAY driver spans the whole virtual screen. The desktop window's DC is
            // anchored to the primary monitor, so a region on a second screen - and every negative
            // coordinate, which is where a monitor placed left of or above the primary one lives -
            // came back black.
            IntPtr screenDc = CreateDC("DISPLAY", null, null, IntPtr.Zero);
            IntPtr fallbackhWnd = IntPtr.Zero;

            if (screenDc == IntPtr.Zero)
            {
                fallbackhWnd = GetDesktopWindow();
                screenDc = GetWindowDC(fallbackhWnd);
            }

            IntPtr memoryDc = CreateCompatibleDC(screenDc);
            IntPtr bitmap = CreateCompatibleBitmap(screenDc, region.Width, region.Height);
            IntPtr oldBitmap = SelectObject(memoryDc, bitmap);

            var success = BitBlt(memoryDc, 0, 0, region.Width, region.Height, screenDc, region.Left, region.Top,
                SRCCOPY | CAPTUREBLT);

            try
            {
                if (!success) throw new Win32Exception();

                result = Image.FromHbitmap(bitmap);
            }
            finally
            {
                SelectObject(memoryDc, oldBitmap);
                DeleteObject(bitmap);
                DeleteDC(memoryDc);

                // Only the desktop DC is borrowed; the DISPLAY one is ours to delete.
                if (fallbackhWnd == IntPtr.Zero)
                    DeleteDC(screenDc);
                else
                    ReleaseDC(fallbackhWnd, screenDc);
            }

            return result;
        }

        private Bitmap CaptureWindow(IntPtr hWnd)
        {
            RECT region;

            if (Environment.OSVersion.Version.Major < 6)
            {
                GetWindowRect(hWnd, out region);
            }
            else
            {
                if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out region,
                    Marshal.SizeOf(typeof(RECT))) != 0) GetWindowRect(hWnd, out region);
            }

            return CaptureRegion(Rectangle.FromLTRB(region.left, region.top, region.right, region.bottom));
        }

        public Bitmap CaptureActiveWindow()
        {
            return CaptureWindow(GetForegroundWindow());
        }

        public Bitmap CaptureDekstop()
        {
            // The desktop window reports only the primary monitor, so on a multi-monitor machine a
            // second screen - and anything ZenTimings was showing on it - fell outside the shot.
            int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            if (width <= 0 || height <= 0)
                return CaptureWindow(GetDesktopWindow());

            return CaptureRegion(new Rectangle(
                GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN), width, height));
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~Screenshot()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}