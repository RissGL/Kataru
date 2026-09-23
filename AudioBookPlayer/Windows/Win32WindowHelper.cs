using System;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace AudioBookPlayer.Windows
{
    /// <summary>
    /// 悬浮字幕窗口的 Win32 封装。
    ///
    /// 业务层只调用这里的方法，不直接接触 HWND / DllImport。
    /// 职责：
    ///  - 鼠标穿透      WS_EX_TRANSPARENT (+ WM_NCHITTEST → HTTRANSPARENT 双保险)
    ///  - 不抢键盘焦点  WS_EX_NOACTIVATE  (+ WM_MOUSEACTIVATE → MA_NOACTIVATE)
    ///  - 透明分层窗口  WS_EX_LAYERED     (WPF AllowsTransparency 自带)
    ///  - 不进 Alt+Tab  WS_EX_TOOLWINDOW
    ///  - 置顶 / 位置 / 尺寸 SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE)
    /// </summary>
    public static class Win32WindowHelper
    {
        /// <summary>获取（必要时创建）窗口句柄。调用后 SourceInitialized 已经触发。</summary>
        public static IntPtr GetHandle(Window window)
        {
            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }

            return new WindowInteropHelper(window).EnsureHandle();
        }

        /// <summary>
        /// 给悬浮窗口套上全套"贴桌面"样式。可以在每次 SourceInitialized 时重复调用（幂等）。
        /// </summary>
        public static void ApplyOverlayStyles(
            Window window,
            bool clickThrough = true,
            bool noActivate = true,
            bool toolWindow = true)
        {
            var handle = GetHandle(window);
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var exStyle = NativeMethods.GetWindowExtendedStyle(handle);

            exStyle = clickThrough
                ? exStyle | NativeMethods.WS_EX_TRANSPARENT
                : exStyle & ~NativeMethods.WS_EX_TRANSPARENT;

            exStyle = noActivate
                ? exStyle | NativeMethods.WS_EX_NOACTIVATE
                : exStyle & ~NativeMethods.WS_EX_NOACTIVATE;

            if (toolWindow)
            {
                // TOOLWINDOW 与 APPWINDOW 互斥，否则仍然会出现在任务栏里
                exStyle = (exStyle | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW;
            }
            else
            {
                exStyle &= ~NativeMethods.WS_EX_TOOLWINDOW;
            }

            NativeMethods.SetWindowExtendedStyle(handle, exStyle);

            // 改完样式后重新贴一次最上层，且不激活窗口（避免抢焦点）。
            SetTopMost(window, window.Topmost);
        }

        /// <summary>开启 / 关闭整窗鼠标穿透。</summary>
        public static void SetClickThrough(Window window, bool enabled)
        {
            SetExtendedStyleFlag(window, NativeMethods.WS_EX_TRANSPARENT, enabled);
        }

        /// <summary>开启 / 关闭"不抢焦点"。</summary>
        public static void SetNoActivate(Window window, bool enabled)
        {
            SetExtendedStyleFlag(window, NativeMethods.WS_EX_NOACTIVATE, enabled);
        }

        /// <summary>是否隐藏在 Alt+Tab 列表中。</summary>
        public static void SetToolWindow(Window window, bool enabled)
        {
            SetExtendedStyleFlag(window, NativeMethods.WS_EX_TOOLWINDOW, enabled);
            if (enabled)
            {
                SetExtendedStyleFlag(window, NativeMethods.WS_EX_APPWINDOW, false);
            }
        }

        /// <summary>设置 / 取消置顶。</summary>
        public static void SetTopMost(Window window, bool topMost)
        {
            var handle = GetHandle(window);
            if (handle == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.SetWindowPos(
                handle,
                topMost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        /// <summary>
        /// 设置窗口位置与尺寸。
        /// 同时写 WPF 的 Left/Top/Width/Height（保持 WPF 内部状态一致）并用 SetWindowPos 重贴 Z 序，
        /// 全程 SWP_NOACTIVATE，不会把焦点从用户正在打字的窗口抢走。
        /// </summary>
        public static void SetPlacement(Window window, Rect bounds, bool topMost = true)
        {
            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }

            window.Left = bounds.Left;
            window.Top = bounds.Top;
            window.Width = bounds.Width;
            window.Height = bounds.Height;

            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle))
            {
                return; // 句柄还没创建（还没 Show / EnsureHandle），WPF 属性已经设置好了
            }

            NativeMethods.SetWindowPos(
                handle,
                topMost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        /// <summary>
        /// 安装消息钩子，作为扩展样式的双保险：
        /// WM_NCHITTEST → HTTRANSPARENT（真正的整窗穿透），WM_MOUSEACTIVATE → MA_NOACTIVATE（永不激活）。
        /// 返回是否安装成功。
        /// </summary>
        public static bool InstallOverlayMessageHandlers(Window window)
        {
            var handle = GetHandle(window);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            var source = HwndSource.FromHwnd(handle);
            if (source == null)
            {
                return false;
            }

            source.AddHook(OverlayWndProc);
            return true;
        }

        private static IntPtr OverlayWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case NativeMethods.WM_NCHITTEST:
                    // 以扩展样式为准：开着 WS_EX_TRANSPARENT 就整窗穿透，
                    // 关掉（拖动模式）就放行走正常命中测试，鼠标消息才能进得来。
                    if ((NativeMethods.GetWindowExtendedStyle(hwnd) & NativeMethods.WS_EX_TRANSPARENT) != 0)
                    {
                        handled = true;
                        return NativeMethods.HTTRANSPARENT;
                    }

                    return IntPtr.Zero;

                case NativeMethods.WM_MOUSEACTIVATE:
                    handled = true;
                    return NativeMethods.MA_NOACTIVATE;

                default:
                    return IntPtr.Zero;
            }
        }

        /// <summary>读取当前扩展样式（诊断 / 自检用）。</summary>
        public static int GetExtendedStyle(Window window)
        {
            return NativeMethods.GetWindowExtendedStyle(GetHandle(window));
        }

        /// <summary>把扩展样式翻译成可读文本（诊断 / 自检用）。</summary>
        public static string DescribeExtendedStyle(Window window)
        {
            return DescribeExtendedStyle(GetExtendedStyle(window));
        }

        public static string DescribeExtendedStyle(int exStyle)
        {
            var sb = new StringBuilder("WS_EX:");
            Append(sb, exStyle, NativeMethods.WS_EX_LAYERED, "LAYERED");
            Append(sb, exStyle, NativeMethods.WS_EX_TRANSPARENT, "TRANSPARENT");
            Append(sb, exStyle, NativeMethods.WS_EX_NOACTIVATE, "NOACTIVATE");
            Append(sb, exStyle, NativeMethods.WS_EX_TOOLWINDOW, "TOOLWINDOW");
            Append(sb, exStyle, NativeMethods.WS_EX_APPWINDOW, "APPWINDOW");
            sb.Append($" (0x{exStyle:X8})");
            return sb.ToString();
        }

        /// <summary>是否已经具备鼠标穿透样式。</summary>
        public static bool HasClickThrough(Window window)
        {
            return (GetExtendedStyle(window) & NativeMethods.WS_EX_TRANSPARENT) != 0;
        }

        /// <summary>是否已经具备不抢焦点样式。</summary>
        public static bool HasNoActivate(Window window)
        {
            return (GetExtendedStyle(window) & NativeMethods.WS_EX_NOACTIVATE) != 0;
        }

        /// <summary>
        /// 光标当前所在的屏幕位置（物理像素）。
        /// 判断"鼠标是不是在字幕上"时统一用物理像素比较，这样跨不同缩放比例的显示器也不会算错。
        /// </summary>
        public static bool TryGetCursorPosition(out int x, out int y)
        {
            if (NativeMethods.GetCursorPos(out var point))
            {
                x = point.X;
                y = point.Y;
                return true;
            }

            x = 0;
            y = 0;
            return false;
        }

        /// <summary>窗口矩形（物理像素）；窗口没句柄就返回 false。</summary>
        public static bool TryGetWindowBounds(Window window, out int left, out int top, out int right, out int bottom)
        {
            left = top = right = bottom = 0;

            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle))
            {
                return false;
            }

            if (!NativeMethods.GetWindowRect(handle, out var rect))
            {
                return false;
            }

            left = rect.Left;
            top = rect.Top;
            right = rect.Right;
            bottom = rect.Bottom;
            return true;
        }

        /// <summary>
        /// 把控制台附着到父进程，让 WinExe 也能在命令行里输出（--selftest 用）。
        /// 如果标准输出已经被重定向（管道 / 文件），就不要附着：否则会把手柄换成控制台，
        /// 调用方反而什么都收不到。
        /// </summary>
        public static void AttachConsoleToParent()
        {
            try
            {
                if (Console.IsOutputRedirected)
                {
                    return;
                }

                NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
            }
            catch (Exception)
            {
                // 忽略：自检输出还会写入报告文件。
            }
        }

        /// <summary>
        /// 把系统标题栏切成深色（Win10 2004+ / Win11），并尽量让标题栏底色与灰黑主题一致。
        /// 不支持的旧系统上返回 false，界面照常工作，只是标题栏是浅色的。
        /// </summary>
        public static bool UseDarkTitleBar(Window window, int captionColorRgb = 0x15171A)
        {
            var handle = GetHandle(window);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var enabled = 1;
                var result = NativeMethods.DwmSetWindowAttribute(
                    handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));

                if (result != 0)
                {
                    result = NativeMethods.DwmSetWindowAttribute(
                        handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref enabled, sizeof(int));
                }

                if (result != 0)
                {
                    return false;
                }

                // Win11 22H2+ 才能改标题栏底色；失败无所谓。
                var captionColor = ToColorRef(captionColorRgb);
                NativeMethods.DwmSetWindowAttribute(
                    handle, NativeMethods.DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

                return true;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        /// <summary>0xRRGGBB → Win32 COLORREF(0x00BBGGRR)。</summary>
        private static int ToColorRef(int rgb)
        {
            var r = (rgb >> 16) & 0xFF;
            var g = (rgb >> 8) & 0xFF;
            var b = rgb & 0xFF;
            return (b << 16) | (g << 8) | r;
        }

        private static void SetExtendedStyleFlag(Window window, int flag, bool enabled)
        {
            var handle = GetHandle(window);
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var exStyle = NativeMethods.GetWindowExtendedStyle(handle);
            var updated = enabled ? exStyle | flag : exStyle & ~flag;
            if (updated != exStyle)
            {
                NativeMethods.SetWindowExtendedStyle(handle, updated);
            }
        }

        private static void Append(StringBuilder sb, int value, int flag, string name)
        {
            if ((value & flag) != 0)
            {
                sb.Append(' ').Append(name);
            }
        }
    }
}
