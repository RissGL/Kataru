using System;
using System.Runtime.InteropServices;

namespace AudioBookPlayer.Windows
{
    /// <summary>
    /// 全项目唯一的 P/Invoke 集中地。业务代码不要直接写 [DllImport]，
    /// 统一通过 <see cref="Win32WindowHelper"/> 或本类调用。
    /// </summary>
    internal static class NativeMethods
    {
        // ---- GetWindowLong / SetWindowLong 的索引 ----
        internal const int GWL_STYLE = -16;
        internal const int GWL_EXSTYLE = -20;

        // ---- 扩展窗口样式 ----
        /// <summary>鼠标穿透的关键：命中测试直接落到下层窗口。</summary>
        internal const int WS_EX_TRANSPARENT = 0x00000020;

        /// <summary>不进入 Alt+Tab / 任务栏。</summary>
        internal const int WS_EX_TOOLWINDOW = 0x00000080;

        /// <summary>强制出现在任务栏（与 TOOLWINDOW 互斥）。</summary>
        internal const int WS_EX_APPWINDOW = 0x00040000;

        /// <summary>分层窗口（WPF 的 AllowsTransparency 会自动加上）。</summary>
        internal const int WS_EX_LAYERED = 0x00080000;

        /// <summary>不抢焦点：点击 / 显示都不会激活窗口。</summary>
        internal const int WS_EX_NOACTIVATE = 0x08000000;

        // ---- SetWindowPos ----
        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        internal static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_SHOWWINDOW = 0x0040;

        // ---- 消息 ----
        internal const int WM_NCHITTEST = 0x0084;
        internal const int WM_MOUSEACTIVATE = 0x0021;
        internal const int WM_NCACTIVATE = 0x0086;

        /// <summary>全局热键触发的消息。</summary>
        internal const int WM_HOTKEY = 0x0312;

        // ---- 全局热键修饰键 ----
        internal const uint MOD_ALT = 0x0001;
        internal const uint MOD_CONTROL = 0x0002;
        internal const uint MOD_SHIFT = 0x0004;
        internal const uint MOD_WIN = 0x0008;

        /// <summary>即使已经有人注册了同样的热键也强行注册（会抢过来）。</summary>
        internal const uint MOD_NOREPEAT = 0x4000;

        /// <summary>WM_NCHITTEST 返回值：命中测试穿透到下层窗口。</summary>
        internal static readonly IntPtr HTTRANSPARENT = new IntPtr(-1);

        /// <summary>WM_MOUSEACTIVATE 返回值：不激活自己。</summary>
        internal static readonly IntPtr MA_NOACTIVATE = new IntPtr(3);

        // ---- kernel32 ----
        internal const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

        // ---- dwmapi：深色标题栏 ----
        /// <summary>Win10 2004+ 的"使用深色模式"属性。</summary>
        internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        /// <summary>Win10 1809~1909 用的是 19。</summary>
        internal const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

        /// <summary>Win11 22H2+：自定义标题栏底色。</summary>
        internal const int DWMWA_CAPTION_COLOR = 35;

        [DllImport("dwmapi.dll", SetLastError = true)]
        internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool IsWindow(IntPtr hWnd);

        /// <summary>广播给所有顶层窗口。</summary>
        internal static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

        /// <summary>注册一条全局唯一的自定义消息（单实例唤醒用）。</summary>
        [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int RegisterWindowMessage(string lpString);

        /// <summary>投递消息（不等待对方处理）。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>光标位置（屏幕物理像素，虚拟桌面坐标系）。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool GetCursorPos(out POINT point);

        /// <summary>窗口矩形（物理像素）。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool FreeConsole();

        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLinkNative(
            string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        /// <summary>创建 NTFS 硬链接（用于 M4B → M4A 扩展名兼容桥）。</summary>
        internal static bool CreateHardLink(string linkPath, string existingPath)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            return CreateHardLinkNative(linkPath, existingPath, IntPtr.Zero);
        }

        /// <summary>读取窗口扩展样式（32/64 位安全）。</summary>
        internal static int GetWindowExtendedStyle(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
            {
                return 0;
            }

            return IntPtr.Size == 8
                ? unchecked((int)GetWindowLongPtr64(hWnd, GWL_EXSTYLE).ToInt64())
                : GetWindowLong32(hWnd, GWL_EXSTYLE);
        }

        /// <summary>写入窗口扩展样式（32/64 位安全）。</summary>
        internal static bool SetWindowExtendedStyle(IntPtr hWnd, int exStyle)
        {
            if (hWnd == IntPtr.Zero)
            {
                return false;
            }

            if (IntPtr.Size == 8)
            {
                SetWindowLongPtr64(hWnd, GWL_EXSTYLE, new IntPtr(exStyle));
            }
            else
            {
                SetWindowLong32(hWnd, GWL_EXSTYLE, exStyle);
            }

            return true;
        }
    }
}
