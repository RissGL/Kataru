using System;
using System.ComponentModel;
using System.Windows;
using AudioBookPlayer.ViewModels;
using AudioBookPlayer.Windows;

namespace AudioBookPlayer.Views
{
    /// <summary>
    /// 悬浮字幕的小工具条（参考网易云桌面歌词）。
    ///
    /// 关键点：字幕本体必须整窗鼠标穿透，所以它身上接不到任何点击；
    /// 于是把"能点的东西"做成这条独立的小工具条——它跟字幕一起走，永远点得到，
    /// 因此解锁拖动、调字号、播放暂停都不需要切回主窗口。
    /// 它同样不抢焦点（WS_EX_NOACTIVATE + ShowActivated=False）。
    /// </summary>
    public partial class OverlayToolbarWindow : Window
    {
        private const double Gap = 8;

        private readonly SubtitleWindow _owner;
        private bool _closed;

        public OverlayToolbarWindow(SubtitleWindow owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));

            InitializeComponent();

            SourceInitialized += OnSourceInitialized;
            Closed += (_, _) => _closed = true;

            // 颜色面板（ContextMenu）在另一棵可视树上，DataContext 不会自动继承，
            // 所以这里显式挂上 Overlay 的 ViewModel，色板才能绑到它的颜色列表。
            DataContext = _owner.ViewModel;

            UpdateLockButton();
        }

        /// <summary>颜色面板是不是开着（开着的时候不要自动隐藏工具条）。</summary>
        public bool IsPopupOpen => ColorButton.ContextMenu?.IsOpen == true;

        /// <summary>贴到字幕右上角（右边放不下就放到字幕里面）。</summary>
        public void FollowOwner()
        {
            if (_closed || !_owner.IsVisible)
            {
                return;
            }

            UpdateLayout();

            var width = ActualWidth > 1 ? ActualWidth : 220;
            var height = ActualHeight > 1 ? ActualHeight : 34;

            // 水平居中于字幕（字幕宽 1400，靠右对齐会飘到很远）
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;

            var left = _owner.Left + ((_owner.Width - width) / 2);
            left = Math.Clamp(left, virtualLeft + 4, virtualRight - width - 4);

            var top = _owner.Top - height - Gap;
            if (top < SystemParameters.VirtualScreenTop + 4)
            {
                top = _owner.Top + Gap; // 上面没空间就放到字幕里面
            }

            Win32WindowHelper.SetPlacement(this, new Rect(left, top, width, height), topMost: true);
            Win32WindowHelper.SetTopMost(this, true);
        }

        /// <summary>刷新按钮文字与可用状态（锁定状态、有没有上/下一章）。</summary>
        public void UpdateLockButton()
        {
            var canGoPrevious = _owner.Controller?.CanGoPrevious ?? false;
            var canGoNext = _owner.Controller?.CanGoNext ?? false;
            // 快退/快进的秒数是可以设置的，标签跟着主界面走
            var controller = _owner.Controller;
            SkipBackButton.Content = controller?.SkipBackLabel ?? "⟲ 10s";
            SkipForwardButton.Content = controller?.SkipForwardLabel ?? "⟳ 10s";
            SkipBackButton.IsEnabled = controller?.SkipBackCommand.CanExecute(null) ?? false;
            SkipForwardButton.IsEnabled = controller?.SkipForwardCommand.CanExecute(null) ?? false;
            PreviousButton.IsEnabled = canGoPrevious;
            NextButton.IsEnabled = canGoNext;
            var movable = _owner.ViewModel?.IsMovable ?? false;
            LockButton.Content = movable ? "🔒 锁定" : "🔓 解锁";

            if (!ReferenceEquals(DataContext, _owner.ViewModel))
            {
                DataContext = _owner.ViewModel;
            }
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            // 工具条是要被点的，绝对不能穿透；但同样不能抢焦点、不进 Alt+Tab
            Win32WindowHelper.ApplyOverlayStyles(this, clickThrough: false, noActivate: true, toolWindow: true);
        }

        private void OnLockClick(object sender, RoutedEventArgs e)
        {
            var viewModel = _owner.ViewModel;
            if (viewModel == null)
            {
                return;
            }

            viewModel.IsMovable = !viewModel.IsMovable;
            UpdateLockButton();
            e.Handled = true;
        }

        private void OnFontDownClick(object sender, RoutedEventArgs e)
        {
            ChangeFontSize(-2);
            e.Handled = true;
        }

        private void OnFontUpClick(object sender, RoutedEventArgs e)
        {
            ChangeFontSize(2);
            e.Handled = true;
        }

        private void ChangeFontSize(double delta)
        {
            var viewModel = _owner.ViewModel;
            if (viewModel == null)
            {
                return;
            }

            viewModel.FontSize += delta;
            _owner.ApplyPlacement();
            FollowOwner();
        }

        /// <summary>点调色按钮：把颜色面板弹出来（左键直接开，不用右键）。</summary>
        private void OnColorClick(object sender, RoutedEventArgs e)
        {
            var menu = ColorButton.ContextMenu;
            if (menu == null)
            {
                return;
            }

            menu.PlacementTarget = ColorButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.Closed += (_, _) => _owner.NotifyToolbarPopupClosed();
            menu.IsOpen = true;

            // 改完颜色把工具条重新摆一次，免得面板宽度变化影响居中
            FollowOwner();
            e.Handled = true;
        }

        private void OnForegroundSwatchClick(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button { Tag: string color } && _owner.ViewModel != null)
            {
                _owner.ViewModel.ForegroundHex = color;
            }

            e.Handled = true;
        }

        private void OnOutlineSwatchClick(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button { Tag: string color } && _owner.ViewModel != null)
            {
                _owner.ViewModel.OutlineHex = color;
            }

            e.Handled = true;
        }

        private void OnResetAppearanceClick(object sender, RoutedEventArgs e)
        {
            _owner.ViewModel?.ResetAppearance();
            _owner.ApplyPlacement();
            FollowOwner();
            e.Handled = true;
        }

        private void OnPreviousClick(object sender, RoutedEventArgs e)
        {
            _owner.Controller?.GoPreviousChapter();
            RefreshNavigationState();
            e.Handled = true;
        }

        private void OnNextClick(object sender, RoutedEventArgs e)
        {
            _owner.Controller?.GoNextChapter();
            RefreshNavigationState();
            e.Handled = true;
        }

        private void RefreshNavigationState()
        {
            PreviousButton.IsEnabled = _owner.Controller?.CanGoPrevious ?? false;
            NextButton.IsEnabled = _owner.Controller?.CanGoNext ?? false;
        }

        private void OnSkipBackClick(object sender, RoutedEventArgs e)
        {
            _owner.Controller?.SkipBackCommand.Execute(null);
            UpdateLockButton();
            e.Handled = true;
        }

        private void OnSkipForwardClick(object sender, RoutedEventArgs e)
        {
            _owner.Controller?.SkipForwardCommand.Execute(null);
            UpdateLockButton();
            e.Handled = true;
        }

        private void OnPlayClick(object sender, RoutedEventArgs e)
        {
            _owner.Controller?.TogglePlayPause();
            e.Handled = true;
        }

        private void OnHideClick(object sender, RoutedEventArgs e)
        {
            var viewModel = _owner.ViewModel;
            if (viewModel != null)
            {
                viewModel.IsVisible = false;
            }

            e.Handled = true;
        }
    }
}
