using System;
using System.Runtime.InteropServices;

namespace ZenTimings
{
    /// <summary>
    /// A single system-wide hotkey, routed through the window hook MainWindow already installs.
    /// Used for "capture a benchmark screenshot without touching the app".
    /// </summary>
    public sealed class HotKeyManager : IDisposable
    {
        public const int WM_HOTKEY = 0x0312;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_NOREPEAT = 0x4000;

        public const uint VK_S = 0x53;
        public const uint VK_C = 0x43;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private IntPtr _hwnd;
        private int _id;
        private bool _registered;

        public bool IsRegistered
        {
            get { return _registered; }
        }

        public int Id
        {
            get { return _id; }
        }

        /// <summary>
        /// Registers the hotkey. Returns false when another application already owns the
        /// combination - that is expected, not an error, so the caller just carries on.
        /// </summary>
        public bool Register(IntPtr hwnd, int id, uint modifiers, uint virtualKey)
        {
            Unregister();

            if (hwnd == IntPtr.Zero)
                return false;

            try
            {
                _registered = RegisterHotKey(hwnd, id, modifiers | MOD_NOREPEAT, virtualKey);
            }
            catch
            {
                _registered = false;
            }

            if (_registered)
            {
                _hwnd = hwnd;
                _id = id;
            }

            return _registered;
        }

        public void Unregister()
        {
            if (!_registered)
                return;

            try { UnregisterHotKey(_hwnd, _id); } catch { }

            _registered = false;
            _hwnd = IntPtr.Zero;
        }

        public void Dispose()
        {
            Unregister();
        }
    }
}
