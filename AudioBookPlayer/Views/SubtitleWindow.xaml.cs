using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AudioBookPlayer.ViewModels;
using AudioBookPlayer.Windows;

namespace AudioBookPlayer.Views
{
    /// <summary>
    /// 桌面悬浮字幕窗口。
    ///
    /// 它是整个程序的核心：一个无边框、全透明、始终置顶、鼠标穿透、永不抢键盘焦点的 WPF 窗口。
    /// 平时完全鼠标穿透（点它等于点下面的软件）；打开"移动模式"后可以拖动摆位、滚轮调字号。
    ///
    /// Win32 相关的一切都在 <see cref="Win32WindowHelper"/> 里，本文件只负责 WPF 层面的布局与位置计算。
    /// </summary>
    public partial class SubtitleWindow : Window, ISubtitleOverlay
    {
        /// <summary>字幕距离屏幕底部 / 顶部的高度比例。</summary>
        private const double VerticalMarginRatio = 0.08;

        /// <summary>拖动时留一点边距，别让窗口彻底出屏。</summary>
        private const double OffScreenMarginX = 60;
        private const double OffScreenMarginY = 30;

        private SubtitleOverlayViewModel? _viewModel;
        private OverlayToolbarWindow? _toolbarWindow;

        /// <summary>轮询光标位置的计时器：字幕是穿透窗口，收不到鼠标事件，只能用轮询判断"鼠标在不在字幕上"。</summary>
        private readonly System.Windows.Threading.DispatcherTimer _hoverTimer;

        /// <summary>光标离开后多久把工具条收起来（留一点时间给用户移过去点按钮）。</summary>
        private static readonly TimeSpan ToolbarHideDelay = TimeSpan.FromMilliseconds(600);

        /// <summary>鼠标热区比窗口本身大一圈，避免贴着边缘时闪烁。</summary>
        private const int HotZoneMargin = 14;

        private DateTime _lastHotUtc = DateTime.MinValue;
        private bool _dragging;
        private Point _dragStartScreen;
        private double _dragStartLeft;
        private double _dragStartTop;

        public SubtitleWindow()
        {
            InitializeComponent();

            DataContextChanged += OnDataContextChanged;
            SourceInitialized += OnSourceInitialized;
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            LocationChanged += OnLocationChanged;
            IsVisibleChanged += OnIsVisibleChanged;
            Closed += OnWindowClosed;

            _hoverTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150),
            };
            _hoverTimer.Tick += OnHoverTick;

            MouseLeftButtonDown += OnMouseLeftButtonDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnMouseLeftButtonUp;
            MouseWheel += OnMouseWheel;
        }

        /// <summary>WM_NCHITTEST / WM_MOUSEACTIVATE 钩子是否安装成功（自检用）。</summary>
        public bool MessageHookInstalled { get; private set; }

        /// <summary>移动手柄窗口（没有就是被设置关掉了）。</summary>
        public OverlayToolbarWindow? ToolbarWindow => _toolbarWindow;

        /// <summary>播放控制器（工具条上的播放/暂停按钮要用），由 App 注入。</summary>
        public MainViewModel? Controller { get; private set; }

        /// <summary>接上主 ViewModel（App 启动时调用）。</summary>
        public void AttachController(MainViewModel controller) => Controller = controller;

        /// <summary>显示悬浮窗口。ShowActivated=False + WS_EX_NOACTIVATE，不会抢走当前应用的焦点。</summary>
        public void ShowOverlay()
        {
            if (!IsVisible)
            {
                Show();
            }

            ApplyPlacement();
            UpdateHandleWindow();
        }

        public void HideOverlay()
        {
            if (IsVisible)
            {
                Hide();
            }
        }

        /// <summary>拖动时用的屏幕范围：整个虚拟桌面（多显示器一起算），单位 DIP。</summary>
        private static Rect VirtualScreenBounds => new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            Math.Max(1, SystemParameters.VirtualScreenWidth),
            Math.Max(1, SystemParameters.VirtualScreenHeight));

        /// <summary>
        /// 按当前设置重新摆放窗口：预设锚点（底部 / 居中 / 顶部 / 自定义）+ X/Y 微调。
        /// 全程使用 DIP 坐标，并且不会激活窗口。
        /// </summary>
        public void ApplyPlacement()
        {
            var workArea = SystemParameters.WorkArea;
            var virtualScreen = VirtualScreenBounds;

            var width = Math.Min(Math.Max(200, _viewModel?.OverlayWidth ?? 1400), Math.Max(200, workArea.Width - 20));
            var height = Math.Max(60, _viewModel?.OverlayHeight ?? 220);

            var anchor = _viewModel?.Anchor ?? OverlayAnchor.Bottom;
            double left;
            double top;

            if (anchor == OverlayAnchor.Custom && _viewModel != null)
            {
                // 自定义位置可能是拖到副屏上的，所以用虚拟桌面做边界
                left = _viewModel.CustomX;
                top = _viewModel.CustomY;
            }
            else
            {
                var offsetX = _viewModel?.OffsetX ?? 0;
                var offsetY = _viewModel?.OffsetY ?? 0;

                left = workArea.Left + ((workArea.Width - width) / 2.0);
                top = anchor switch
                {
                    OverlayAnchor.Top => workArea.Top + (workArea.Height * VerticalMarginRatio),
                    OverlayAnchor.Center => workArea.Top + ((workArea.Height - height) / 2.0),
                    _ => workArea.Bottom - height - (workArea.Height * VerticalMarginRatio),
                };

                left += offsetX;
                top += offsetY;
            }

            // 至少留一部分在虚拟桌面里，别让窗口彻底失踪
            left = Math.Clamp(left, virtualScreen.Left - width + OffScreenMarginX, virtualScreen.Right - OffScreenMarginX);
            top = Math.Clamp(top, virtualScreen.Top - height + OffScreenMarginY, virtualScreen.Bottom - OffScreenMarginY);

            Win32WindowHelper.SetPlacement(this, new Rect(left, top, width, height), Topmost);
        }

        protected override void OnClosed(EventArgs e)
        {
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            base.OnClosed(e);
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            // 窗口生命周期里可能重建 HWND（Hide→Show、切 DPI 等），扩展样式不会自动保留，
            // 所以每次 SourceInitialized 都重新套一遍，幂等。
            ApplyOverlayInputMode();
            MessageHookInstalled = Win32WindowHelper.InstallOverlayMessageHandlers(this);
            ApplyPlacement();
        }

        /// <summary>按当前模式设置鼠标穿透 / 不抢焦点。</summary>
        private void ApplyOverlayInputMode()
        {
            var movable = _viewModel?.IsMovable ?? false;

            Win32WindowHelper.ApplyOverlayStyles(this, clickThrough: !movable, noActivate: true, toolWindow: true);

            // 拖动模式给个"可以抓"的鼠标指针
            Cursor = movable ? Cursors.SizeAll : null;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _viewModel = e.NewValue as SubtitleOverlayViewModel;

            if (_viewModel != null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }

            ApplyOverlayInputMode();
            ApplyPlacement();
            UpdateHandleWindow();
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(SubtitleOverlayViewModel.Anchor):
                case nameof(SubtitleOverlayViewModel.OffsetX):
                case nameof(SubtitleOverlayViewModel.OffsetY):
                case nameof(SubtitleOverlayViewModel.OverlayWidth):
                case nameof(SubtitleOverlayViewModel.OverlayHeight):
                case nameof(SubtitleOverlayViewModel.CustomX):
                // 拖动过程中只通知 CustomX（见 MoveTo），这里也只重排一次，
                // 免得一帧里连着两次 SetWindowPos 把窗口晃出残影。
                case nameof(SubtitleOverlayViewModel.CustomY):
                    ApplyPlacement();
                    break;

                case nameof(SubtitleOverlayViewModel.IsMovable):
                    ApplyOverlayInputMode();
                    UpdateHandleWindow();
                    break;

                case nameof(SubtitleOverlayViewModel.IsVisible):
                case nameof(SubtitleOverlayViewModel.ShowHandle):
                    // 工具条开关也要立刻生效，不然要重启程序才看得到变化
                    UpdateHandleWindow();
                    break;
            }
        }

        private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea) ||
                e.PropertyName == nameof(SystemParameters.VirtualScreenWidth))
            {
                ApplyPlacement();
                UpdateHandleWindow();
            }
        }

        private void OnLocationChanged(object? sender, EventArgs e) => UpdateHandleWindow();

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateHandleWindow();

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            StopHoverWatch();
            _hoverTimer.Tick -= OnHoverTick;

            if (_toolbarWindow != null)
            {
                _toolbarWindow.Close();
                _toolbarWindow = null;
            }
        }

        // ---------------- 移动手柄 ----------------

        /// <summary>
        /// 手柄是一个独立的小窗口（跟着字幕走）。
        /// 悬浮字幕本身必须整窗鼠标穿透，所以不可能在它身上接点击——
        /// 想"点一下就进入移动模式"，就得有这么一块真正能点的区域。
        /// </summary>
        private void UpdateHandleWindow()
        {
            var wanted = IsVisible && (_viewModel?.ShowHandle ?? false) && _viewModel != null;
            var visible = wanted && ShouldShowToolbar();

            if (!wanted)
            {
                _toolbarWindow?.Hide();
                StopHoverWatch();
                return;
            }

            StartHoverWatch();

            if (!visible)
            {
                _toolbarWindow?.Hide();
                return;
            }

            if (_toolbarWindow == null)
            {
                _toolbarWindow = new OverlayToolbarWindow(this);
                _toolbarWindow.Closed += (_, _) => _toolbarWindow = null;
            }

            // 隐藏字幕时工具条只是 Hide（窗口还在），再打开时必须显式 Show 回来，
            // 否则就出现"关一次字幕，工具条再也不出现"。
            if (!_toolbarWindow.IsVisible)
            {
                _toolbarWindow.Show();
            }

            _toolbarWindow.UpdateLockButton();
            _toolbarWindow.FollowOwner();
        }

        /// <summary>
        /// 工具条要不要显示（纯函数，方便自检）。
        /// 网易云的逻辑：锁定状态下平时藏起来，鼠标移到歌词上才浮出来；
        /// 解锁拖动时一直显示（你要点"锁定"按钮）。
        /// </summary>
        public static bool ShouldShowToolbar(bool autoHide, bool isMovable, bool cursorInHotZone, bool popupOpen)
        {
            if (isMovable || !autoHide)
            {
                return true;
            }

            return cursorInHotZone || popupOpen;
        }

        private bool ShouldShowToolbar()
        {
            var autoHide = _viewModel?.AutoHideToolbar ?? false;
            var movable = _viewModel?.IsMovable ?? false;

            if (movable || !autoHide)
            {
                return true;
            }

            var popupOpen = _toolbarWindow?.IsPopupOpen ?? false;
            if (popupOpen)
            {
                _lastHotUtc = DateTime.UtcNow; // 面板开着就一直留着
                return true;
            }

            if (IsCursorInHotZone())
            {
                _lastHotUtc = DateTime.UtcNow;
                return true;
            }

            return DateTime.UtcNow - _lastHotUtc < ToolbarHideDelay;
        }

        /// <summary>光标是否在"字幕 + 工具条"这一片区域里（用物理像素比较，跨缩放不会错）。</summary>
        private bool IsCursorInHotZone()
        {
            if (_viewModel == null || !Win32WindowHelper.TryGetCursorPosition(out var cursorX, out var cursorY))
            {
                return false;
            }

            return IsInsideWindow(this) || (_toolbarWindow != null && _toolbarWindow.IsVisible && IsInsideWindow(_toolbarWindow));

            bool IsInsideWindow(Window window)
            {
                if (!window.IsVisible || !Win32WindowHelper.TryGetWindowBounds(window, out var left, out var top, out var right, out var bottom))
                {
                    return false;
                }

                return cursorX >= left - HotZoneMargin && cursorX <= right + HotZoneMargin &&
                       cursorY >= top - HotZoneMargin && cursorY <= bottom + HotZoneMargin;
            }
        }

        private void StartHoverWatch()
        {
            if (!_hoverTimer.IsEnabled)
            {
                _lastHotUtc = DateTime.MinValue;
                _hoverTimer.Start();
            }
        }

        private void StopHoverWatch()
        {
            if (_hoverTimer.IsEnabled)
            {
                _hoverTimer.Stop();
            }
        }

        /// <summary>颜色面板关了：让热区判断重新跑一次。</summary>
        public void NotifyToolbarPopupClosed() => UpdateHandleWindow();

        private void OnHoverTick(object? sender, EventArgs e)
        {
            if (_viewModel == null || !IsVisible)
            {
                return;
            }

            UpdateHandleWindow();
        }

        // ---------------- 拖动 / 滚轮 ----------------

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel == null || !_viewModel.IsMovable)
            {
                return;
            }

            // 点在提示条上的按钮时不拖动
            if (e.OriginalSource is DependencyObject source && IsInsideButton(source))
            {
                return;
            }

            BeginDrag(e);
            CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }

            DragTo(e);
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }

            EndDrag();
        }

        /// <summary>当前 ViewModel（手柄窗口需要读写 IsMovable）。</summary>
        public SubtitleOverlayViewModel? ViewModel => _viewModel;

        /// <summary>开始拖动（也供手柄窗口调用）。</summary>
        public void BeginDrag(MouseEventArgs e) => BeginDragAt(ScreenPoint(e));

        /// <summary>开始拖动，传入屏幕坐标（DIP）。</summary>
        public void BeginDragAt(Point screenPoint)
        {
            if (_viewModel == null)
            {
                return;
            }

            _dragging = true;
            _dragStartScreen = screenPoint;
            _dragStartLeft = Left;
            _dragStartTop = Top;
        }

        /// <summary>拖动中（手柄窗口据此判断是否要接管）。</summary>
        public bool IsDragging => _dragging;

        /// <summary>
        /// 按屏幕坐标算位移。
        ///
        /// 这里必须用屏幕坐标：如果用 e.GetPosition(this)（窗口相对坐标），
        /// 窗口一跟着动，鼠标的相对坐标就跟着变，下一帧算出来的目标位置会被拉回起点，
        /// 结果就是窗口疯狂来回抖（"抽搐"）。
        /// </summary>
        public void DragTo(MouseEventArgs e)
        {
            DragToScreen(ScreenPoint(e));
            e.Handled = true;
        }

        /// <summary>按屏幕坐标拖动（DIP）。</summary>
        public void DragToScreen(Point screenPoint)
        {
            if (!_dragging || _viewModel == null)
            {
                return;
            }

            var left = _dragStartLeft + (screenPoint.X - _dragStartScreen.X);
            var top = _dragStartTop + (screenPoint.Y - _dragStartScreen.Y);

            // 拖动过程不落盘（结束时才存），否则每帧都在写 settings.json
            _viewModel.MoveTo(left, top, persist: false);
        }

        /// <summary>结束拖动：把位置定下来并写盘。</summary>
        public void EndDrag()
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            ReleaseMouseCapture();

            if (_viewModel != null)
            {
                _viewModel.MoveTo(_viewModel.CustomX, _viewModel.CustomY, persist: true);
            }

            ApplyPlacement();
            UpdateHandleWindow();
        }

        /// <summary>鼠标当前在屏幕上的位置（物理像素 → DIP）。</summary>
        private Point ScreenPoint(MouseEventArgs e)
        {
            var screen = PointToScreen(e.GetPosition(this));
            var dpi = VisualTreeHelper.GetDpi(this);
            return new Point(screen.X / dpi.DpiScaleX, screen.Y / dpi.DpiScaleY);
        }

        // ---------------- 拖角改大小 ----------------

        private void OnResizeDragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        {
            if (_viewModel == null)
            {
                return;
            }

            // 先把当前左上角钉成"自定义位置"：这样改大小时按锚点算的预设位置不会跟着漂
            _viewModel.SetCustomPosition(Left, Top);
            e.Handled = true;
        }

        private void OnResizeDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            if (_viewModel == null)
            {
                return;
            }

            var fromLeftEdge = ReferenceEquals(sender, ResizeBottomLeft);

            // 从左下角拖时，往左拖 = 变宽，同时左边界要跟着往左走，右边界保持不动
            var widthDelta = fromLeftEdge ? -e.HorizontalChange : e.HorizontalChange;

            var maxWidth = Math.Max(320, SystemParameters.VirtualScreenWidth);
            var targetWidth = Math.Clamp(_viewModel.OverlayWidth + widthDelta, 240, maxWidth);
            var targetHeight = Math.Clamp(_viewModel.OverlayHeight + e.VerticalChange, 80, 1200);

            var appliedWidth = targetWidth - _viewModel.OverlayWidth;

            _viewModel.OverlayWidth = targetWidth;
            _viewModel.OverlayHeight = targetHeight;

            if (fromLeftEdge)
            {
                _viewModel.CustomX -= appliedWidth;
            }

            ApplyPlacement();
            UpdateHandleWindow();
            e.Handled = true;
        }

        private void OnResizeDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            if (_viewModel == null)
            {
                return;
            }

            // persist=true 会触发设置写盘
            _viewModel.MoveTo(_viewModel.CustomX, _viewModel.CustomY, persist: true);
            ApplyPlacement();
            UpdateHandleWindow();
            e.Handled = true;
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_viewModel == null || !_viewModel.IsMovable)
            {
                return;
            }

            _viewModel.FontSize += e.Delta > 0 ? 2 : -2;
            e.Handled = true;
        }

        private void OnLockClick(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.IsMovable = false;
            }

            e.Handled = true;
        }

        private static bool IsInsideButton(DependencyObject source)
        {
            for (var node = source; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is System.Windows.Controls.Button)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
