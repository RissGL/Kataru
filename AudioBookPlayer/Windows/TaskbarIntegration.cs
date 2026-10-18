using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AudioBookPlayer.Windows
{
    /// <summary>
    /// 任务栏集成：鼠标移到任务栏图标上时出现的那个小预览卡片。
    ///
    /// 做了三件事（就是网易云那个效果）：
    ///   1. 缩略图用 DWM 的 iconic thumbnail —— 自己画一张封面 + 书名的图，而不是窗口的实时截图；
    ///   2. 缩略图下面挂一排按钮（上一章 / 播放暂停 / 下一章），点了直接控制播放；
    ///   3. 悬停提示文字（书名 · 章节）。
    ///
    /// 用到的全是 Windows 自带接口（ITaskbarList3 / DWM），没有第三方依赖。
    /// 任何一步失败都只是少个功能，不影响程序正常运行。
    /// </summary>
    public sealed class TaskbarIntegration : IDisposable
    {
        private const int WmCommand = 0x0111;

        /// <summary>DWM 来要缩略图时发的消息 —— 必须在响应它的时候才能设置缩略图。</summary>
        private const int WmDwmSendIconicThumbnail = 0x0323;
        private const int ThbnClicked = 0x1800;

        private const uint ButtonPrevious = 1;
        private const uint ButtonPlayPause = 2;
        private const uint ButtonNext = 3;

        // 从 SDK 的 dwmapi.h 查到（别凭记忆写）：
        //   DWMWA_FORCE_ICONIC_REPRESENTATION = 7  → 强制这个窗口用"静态图标缩略图"
        //   DWMWA_HAS_ICONIC_BITMAP           = 10 → 声明我们提供了一张静态图
        // 只设 10 是不够的：官方注释说它只在"没有更好的缩略图"时生效，
        // 也就是说实时预览优先 —— 所以必须同时把 7 打开，否则永远看到的是窗口实时截图。
        private const int DwmwaForceIconicRepresentation = 7;
        private const int DwmwaHasIconicBitmap = 10;

        private static readonly int TaskbarButtonCreatedMessage =
            NativeMethods.RegisterWindowMessage("TaskbarButtonCreated");

        private readonly Window _window;
        private IntPtr _handle;
        private ITaskbarList3? _taskbar;
        private HwndSource? _source;
        private bool _disposed;

        private IntPtr _iconPrevious;
        private IntPtr _iconPlay;
        private IntPtr _iconPause;
        private IntPtr _iconNext;

        private bool _isPlaying;

        public TaskbarIntegration(Window window)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
        }

        /// <summary>点了"上一章"。</summary>
        public event EventHandler? PreviousRequested;

        /// <summary>点了"播放 / 暂停"。</summary>
        public event EventHandler? PlayPauseRequested;

        /// <summary>点了"下一章"。</summary>
        public event EventHandler? NextRequested;

        /// <summary>接到主窗口上（要在 HWND 建好、窗口显示之后调用）。</summary>
        public void Attach()
        {
            _handle = new WindowInteropHelper(_window).Handle;
            if (_handle == IntPtr.Zero)
            {
                return;
            }

            _source = HwndSource.FromHwnd(_handle);
            _source?.AddHook(WndProc);

            // 窗口还没显示时任务栏按钮不存在，先挂到 IsVisibleChanged 上
            if (_window.IsVisible)
            {
                TryInitialize();
            }
            else
            {
                _window.IsVisibleChanged += OnWindowVisibleChanged;
            }
        }

        private void OnWindowVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_window.IsVisible)
            {
                _window.IsVisibleChanged -= OnWindowVisibleChanged;
                TryInitialize();
            }
        }

        /// <summary>
        /// 初始化任务栏集成。
        ///
        /// 关键：ThumbBarAddButtons 在"任务栏按钮还没建好"时会失败，
        /// 而那个时机很难精确——所以这里失败就重试几次（每 700ms，最多 8 次）。
        /// 以前用"给自己发一条 TaskbarButtonCreated 消息"来催，那是错的：
        /// 那个消息必须由系统在按钮就绪后发过来。
        /// </summary>
        private void TryInitialize()
        {
            if (_disposed || _buttonsAdded)
            {
                return;
            }

            CreateTaskbar();

            if (_buttonsAdded)
            {
                _retryTimer?.Stop();
                _retryTimer = null;
                ApplyPendingState();
                return;
            }

            _retries++;

            if (_retries > MaxRetries)
            {
                Status = "放弃"; Diagnostics.ActivityLog.WriteNow($"任务栏集成放弃：重试 {MaxRetries} 次仍失败（{_lastError ?? "原因未知"}）");
                _retryTimer?.Stop();
                _retryTimer = null;
                return;
            }

            _retryTimer ??= CreateRetryTimer();
            if (!_retryTimer.IsEnabled)
            {
                _retryTimer.Start();
            }
        }

        private const int MaxRetries = 8;
        private int _retries;
        private string? _lastError;

        /// <summary>初始化结果（自检/日志用）。</summary>
        public string Status { get; private set; } = "未初始化";
        private System.Windows.Threading.DispatcherTimer? _retryTimer;

        private System.Windows.Threading.DispatcherTimer CreateRetryTimer()
        {
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(700),
            };

            timer.Tick += (_, _) => TryInitialize();
            return timer;
        }

        private string _pendingTooltip = "KATARU";
        private Action<Graphics, int, int>? _pendingRender;
        private bool _pendingHasCover;
        private double _pendingProgress = -1;

        /// <summary>初始化成功后把攒下的状态一次性应用上去。</summary>
        private void ApplyPendingState()
        {
            Update(_pendingTooltip, _pendingRender, _pendingHasCover);
            SetProgress(_pendingProgress);
            UpdateButtons();
        }

        /// <summary>更新悬停提示 + 缩略图（书名 / 章节 / 封面）。</summary>
        public void Update(string tooltip, Action<Graphics, int, int>? renderThumbnail = null, bool hasCover = false)
        {
            _pendingTooltip = tooltip;
            _pendingRender = renderThumbnail;
            _pendingHasCover = hasCover;

            if (_taskbar == null)
            {
                return;
            }

            try
            {
                _taskbar.SetThumbnailTooltip(_handle, tooltip);
            }
            catch (Exception)
            {
                // 忽略
            }

            // 只声明"我有一张静态图"，具体取图由 DWM 发 WM_DWMSENDICONICTHUMBNAIL 来要
            try
            {
                var flag = hasCover ? 1 : 0;
                NativeMethods.DwmSetWindowAttribute(_handle, DwmwaHasIconicBitmap, ref flag, sizeof(int));
                NativeMethods.DwmSetWindowAttribute(_handle, DwmwaForceIconicRepresentation, ref flag, sizeof(int));

                if (hasCover)
                {
                    // 封面 / 章节变了：让 DWM 重新来要一次图
                    NativeMethods.DwmInvalidateIconicBitmaps(_handle);
                }
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        /// <summary>播放状态变了：换播放/暂停图标，并更新任务栏进度。</summary>
        public void SetPlaying(bool isPlaying)
        {
            if (_isPlaying == isPlaying)
            {
                return;
            }

            _isPlaying = isPlaying;
            UpdateButtons();
        }

        /// <summary>任务栏图标上显示播放进度（0~1，负数表示不显示）。</summary>
        public void SetProgress(double progress)
        {
            _pendingProgress = progress;

            if (_taskbar == null)
            {
                return;
            }

            try
            {
                if (progress < 0)
                {
                    _taskbar.SetProgressState(_handle, TaskbarProgressState.None);
                    return;
                }

                _taskbar.SetProgressState(_handle, TaskbarProgressState.Normal);
                _taskbar.SetProgressValue(_handle, (ulong)(Math.Clamp(progress, 0, 1) * 1000), 1000);
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _source?.RemoveHook(WndProc);

            foreach (var icon in new[] { _iconPrevious, _iconPlay, _iconPause, _iconNext })
            {
                if (icon != IntPtr.Zero)
                {
                    NativeMethods.DestroyIcon(icon);
                }
            }

            _iconPrevious = _iconPlay = _iconPause = _iconNext = IntPtr.Zero;

            if (_taskbar != null)
            {
                Marshal.ReleaseComObject(_taskbar);
                _taskbar = null;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == TaskbarButtonCreatedMessage)
            {
                CreateTaskbar();
                return IntPtr.Zero;
            }

            // DWM 说"给我缩略图"——只有这时候调 DwmSetIconicThumbnail 才有效，
            // 提前主动调用会 E_INVALIDARG（这个坑踩了一轮）
            if (msg == WmDwmSendIconicThumbnail)
            {
                RenderIconicThumbnail();
                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WmCommand && ((long)wParam >> 16) == ThbnClicked)
            {
                switch ((uint)((long)wParam & 0xFFFF))
                {
                    case ButtonPrevious:
                        PreviousRequested?.Invoke(this, EventArgs.Empty);
                        handled = true;
                        break;
                    case ButtonPlayPause:
                        PlayPauseRequested?.Invoke(this, EventArgs.Empty);
                        handled = true;
                        break;
                    case ButtonNext:
                        NextRequested?.Invoke(this, EventArgs.Empty);
                        handled = true;
                        break;
                }
            }

            return IntPtr.Zero;
        }

        private void CreateTaskbar()
        {
            if (_taskbar != null)
            {
                return;
            }

            try
            {
                _taskbar = (ITaskbarList3)new CTaskbarList();
                _taskbar.HrInit();

                // 允许我们自己提供缩略图（封面）


                CreateIcons();
                AddButtons();
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _taskbar = null;
            }
        }



        private void CreateIcons()
        {
            _iconPrevious = CreateIcon(g =>
            {
                using var brush = new SolidBrush(Color.White);
                g.FillRectangle(brush, 6, 8, 3, 16);
                g.FillPolygon(brush, new[] { new PointF(24, 8), new PointF(24, 24), new PointF(11, 16) });
            });

            _iconPlay = CreateIcon(g =>
            {
                using var brush = new SolidBrush(Color.White);
                g.FillPolygon(brush, new[] { new PointF(10, 7), new PointF(10, 25), new PointF(25, 16) });
            });

            _iconPause = CreateIcon(g =>
            {
                using var brush = new SolidBrush(Color.White);
                g.FillRectangle(brush, 9, 7, 5, 18);
                g.FillRectangle(brush, 18, 7, 5, 18);
            });

            _iconNext = CreateIcon(g =>
            {
                using var brush = new SolidBrush(Color.White);
                g.FillPolygon(brush, new[] { new PointF(8, 8), new PointF(8, 24), new PointF(21, 16) });
                g.FillRectangle(brush, 23, 8, 3, 16);
            });
        }

        private static IntPtr CreateIcon(Action<Graphics> draw)
        {
            try
            {
                using var bitmap = new Bitmap(32, 32);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.Clear(Color.Transparent);
                    draw(graphics);
                }

                return bitmap.GetHicon();
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        private void AddButtons()
        {
            if (_taskbar == null)
            {
                return;
            }

            try
            {
                _taskbar.ThumbBarAddButtons(_handle, 3, new[]
                {
                    MakeButton(_iconPrevious, ButtonPrevious, "上一章"),
                    MakeButton(_isPlaying ? _iconPause : _iconPlay, ButtonPlayPause, "播放 / 暂停"),
                    MakeButton(_iconNext, ButtonNext, "下一章"),
                });

                _buttonsAdded = true;
                Status = "按钮已添加"; Diagnostics.ActivityLog.WriteNow("任务栏缩略图按钮已添加");
            }
            catch (Exception ex)
            {
                _lastError = $"ThumbBarAddButtons 失败：{ex.Message}"; Status = _lastError;
                Diagnostics.ActivityLog.WriteNow(_lastError);
            }
        }

        private bool _buttonsAdded;

        private void UpdateButtons()
        {
            if (_taskbar == null || !_buttonsAdded)
            {
                return;
            }

            try
            {
                _taskbar.ThumbBarUpdateButtons(_handle, 3, new[]
                {
                    MakeButton(_iconPrevious, ButtonPrevious, "上一章"),
                    MakeButton(_isPlaying ? _iconPause : _iconPlay, ButtonPlayPause, "播放 / 暂停"),
                    MakeButton(_iconNext, ButtonNext, "下一章"),
                });
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        private static ThumbnailToolbarButton MakeButton(IntPtr icon, uint id, string tip) => new()
        {
            Mask = ThumbnailButtonMask.Icon | ThumbnailButtonMask.Tooltip | ThumbnailButtonMask.Flags,
            Id = id,
            BitmapIndex = 0,
            Icon = icon,
            Tip = tip,
            Flags = ThumbnailButtonFlags.Enabled,
        };

        /// <summary>
        /// 用 DWM 设置自定义缩略图（画一张封面卡片）。
        ///
        /// 关键：DWM 只接受 **32 位、预乘 alpha、自上而下** 的 DIB。
        /// Bitmap.GetHbitmap() 是自下而上 + 直通 alpha，直接传进去会 E_INVALIDARG，
        /// 所以这里自己 CreateDIBSection，再把 PArgb（预乘）像素拷进去。
        /// </summary>
        private void RenderIconicThumbnail()
        {
            var render = _pendingRender;
            if (render == null || !_pendingHasCover)
            {
                return;
            }

            SetIconicThumbnail(render);
        }

        private void SetIconicThumbnail(Action<Graphics, int, int> render)
        {
            const int width = 320;
            const int height = 180;

            IntPtr dib = IntPtr.Zero;

            try
            {
                // 用 Format32bppPArgb（预乘 alpha）
                using (var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                        render(graphics, width, height);
                    }

                    var info = new NativeMethods.BITMAPINFO
                    {
                        bmiHeader = new NativeMethods.BITMAPINFOHEADER
                        {
                            biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                            biWidth = width,
                            biHeight = -height, // 负数 = 自上而下
                            biPlanes = 1,
                            biBitCount = 32,
                            biCompression = 0, // BI_RGB
                            biSizeImage = (uint)(width * height * 4),
                        },
                        bmiColors = new uint[3],
                    };

                    dib = NativeMethods.CreateDIBSection(IntPtr.Zero, ref info, NativeMethods.DIB_RGB_COLORS,
                        out var bits, IntPtr.Zero, 0);

                    if (dib == IntPtr.Zero || bits == IntPtr.Zero)
                    {
                        Status = "CreateDIBSection 失败";
                        Diagnostics.ActivityLog.WriteNow(Status);
                        return;
                    }

                    var data = bitmap.LockBits(
                        new Rectangle(0, 0, width, height),
                        System.Drawing.Imaging.ImageLockMode.ReadOnly,
                        System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

                    try
                    {
                        for (var row = 0; row < height; row++)
                        {
                            var source = IntPtr.Add(data.Scan0, row * data.Stride);
                            var target = IntPtr.Add(bits, row * width * 4);
                            CopyMemory(target, source, (UIntPtr)(uint)(width * 4));
                        }
                    }
                    finally
                    {
                        bitmap.UnlockBits(data);
                    }
                }

                var allow = 1;
                NativeMethods.DwmSetWindowAttribute(_handle, DwmwaHasIconicBitmap, ref allow, sizeof(int));
                NativeMethods.DwmSetWindowAttribute(_handle, DwmwaForceIconicRepresentation, ref allow, sizeof(int));

                var hr = NativeMethods.DwmSetIconicThumbnail(_handle, dib, 0);
                Status = hr == 0 ? "已设置封面缩略图" : $"DwmSetIconicThumbnail 失败：0x{hr:X8}";
                Diagnostics.ActivityLog.WriteNow(Status);
            }
            catch (Exception ex)
            {
                Status = $"缩略图异常：{ex.Message}";
                Diagnostics.ActivityLog.WriteNow(Status);
            }
            finally
            {
                if (dib != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(dib);
                }
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
        private static extern void CopyMemory(IntPtr destination, IntPtr source, UIntPtr length);
        // ==================== COM 接口 ====================

        [ComImport]
        [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        private class CTaskbarList
        {
        }

        [ComImport]
        [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            void HrInit();

            void AddTab(IntPtr hwnd);

            void DeleteTab(IntPtr hwnd);

            void ActivateTab(IntPtr hwnd);

            void SetActiveAlt(IntPtr hwnd);

            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

            void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);

            void SetProgressState(IntPtr hwnd, TaskbarProgressState state);

            void RegisterTab(IntPtr hwndTab, IntPtr hwndMdi);

            void UnregisterTab(IntPtr hwndTab);

            void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);

            void SetTabActive(IntPtr hwndTab, IntPtr hwndMdi, uint reserved);

            void ThumbBarAddButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] ThumbnailToolbarButton[] buttons);

            void ThumbBarUpdateButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] ThumbnailToolbarButton[] buttons);

            void ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);

            void SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string description);

            void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tip);

            void SetThumbnailClip(IntPtr hwnd, IntPtr clip);
        }

        private enum TaskbarProgressState
        {
            None = 0,
            Indeterminate = 1,
            Normal = 2,
            Error = 4,
            Paused = 8,
        }

        [Flags]
        private enum ThumbnailButtonMask : uint
        {
            Bitmap = 0x4,
            Icon = 0x1,
            Tooltip = 0x2,
            Flags = 0x8,
        }

        [Flags]
        private enum ThumbnailButtonFlags : uint
        {
            Enabled = 0x00,
            Disabled = 0x01,
            DismissOnClick = 0x02,
            NoBackground = 0x04,
            Hidden = 0x08,
            NonInteractive = 0x10,
        }

        /// <summary>
        /// Win32 的 THUMBBUTTON（注意字段顺序，写错一个都加不上按钮）：
        ///   UINT dwMask; UINT iId; UINT iBitmap; HICON hIcon; WCHAR szTip[260]; THUMBBUTTONFLAGS dwFlags;
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ThumbnailToolbarButton
        {
            public ThumbnailButtonMask Mask;

            public uint Id;

            public uint BitmapIndex;

            public IntPtr Icon;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string Tip;

            public ThumbnailButtonFlags Flags;
        }
    }
}
