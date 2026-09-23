using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioBookPlayer.Core;
using AudioBookPlayer.ViewModels;
using AudioBookPlayer.Windows;

namespace AudioBookPlayer.Views
{
    /// <summary>
    /// 主控制窗口：书库 / 播放 / 阅读三种模式 + 常驻底部播放条。
    /// 悬浮字幕由 <see cref="SubtitleWindow"/> 独立负责，这里只管控制与展示。
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private MainViewModel? ViewModel => DataContext as MainViewModel;

        /// <summary>深色标题栏是否生效（诊断用）。</summary>
        public bool Win32TitleBarApplied { get; private set; }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // 让系统标题栏也跟着这套配色（Win10 2004+ / Win11）
            Win32TitleBarApplied = Win32WindowHelper.UseDarkTitleBar(this, 0x121011);

            // 全局热键挂在主窗口的 HWND 上：不在前台也能控制播放
            ViewModel?.AttachHotkeys(this);

            // 单实例：第二个进程会广播这条消息，我们把窗口从托盘里叫出来
            var instanceSource = System.Windows.Interop.HwndSource.FromHwnd(
                Win32WindowHelper.GetHandle(this));
            instanceSource?.AddHook(OnSingleInstanceMessage);
        }

        /// <summary>
        /// 点 ✕ 不退出，收进托盘（桌面字幕和全局热键继续工作）。
        /// 真正退出走托盘菜单的「退出 KATARU」，那时 App.IsExiting 为 true。
        /// </summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            var vm = ViewModel;
            if (!App.IsExiting && vm != null && vm.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                App.NotifyMinimizedToTray();
                return;
            }

            base.OnClosing(e);
        }

        /// <summary>收到"已经有实例在跑"的广播，把窗口从托盘里叫出来。</summary>
        private IntPtr OnSingleInstanceMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == SingleInstanceGuard.ShowWindowMessage)
            {
                RestoreFromTray();
                handled = true;
            }

            return IntPtr.Zero;
        }
        /// <summary>从托盘恢复窗口。</summary>
        public void RestoreFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
            Topmost = true;
            Topmost = false; // 抖一下 Z 序，确保从托盘唤起时真的跑到最前面
            Focus();
        }

        /// <summary>
        /// Ctrl + 滚轮缩放界面（像浏览器那样），每个视图各自记一份缩放。
        /// 用 PreviewMouseWheel：滚轮会先被列表吞掉，冒泡阶段就收不到了。
        /// </summary>
        protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                ViewModel?.AdjustZoom(e.Delta);
                e.Handled = true; // 缩放时不要再滚列表
                return;
            }

            base.OnPreviewMouseWheel(e);
        }
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            var vm = ViewModel;
            if (vm == null)
            {
                base.OnPreviewKeyDown(e);
                return;
            }

            // 正在输入框里打字时不要抢快捷键
            if (Keyboard.FocusedElement is TextBox)
            {
                base.OnPreviewKeyDown(e);
                return;
            }

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
                (e.Key == Key.D0 || e.Key == Key.NumPad0))
            {
                ViewModel?.ResetZoom();
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.Space:
                    vm.TogglePlayPause();
                    e.Handled = true;
                    break;

                case Key.Left:
                    vm.Skip(-vm.SkipSeconds);
                    e.Handled = true;
                    break;

                case Key.Right:
                    vm.Skip(vm.SkipSeconds);
                    e.Handled = true;
                    break;

                case Key.Up:
                    vm.Overlay.FontSize += 2;
                    e.Handled = true;
                    break;

                case Key.Down:
                    vm.Overlay.FontSize -= 2;
                    e.Handled = true;
                    break;

                case Key.Home:
                    vm.SeekTo(TimeSpan.Zero);
                    e.Handled = true;
                    break;
            }

            base.OnPreviewKeyDown(e);
        }

        private void OnSeekDragStarted(object sender, RoutedEventArgs e)
        {
            ViewModel?.BeginSeek();
        }

        private void OnSeekDragCompleted(object sender, RoutedEventArgs e)
        {
            ViewModel?.EndSeek();
        }

        /// <summary>双击书本 = 从第一章开始播放，并切到播放模式。</summary>
        private void OnBookDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBox list)
            {
                return;
            }

            if (ItemsControl.ContainerFromElement(list, (DependencyObject)e.OriginalSource) is not ListBoxItem)
            {
                return;
            }

            ViewModel?.PlaySelected();
            e.Handled = true;
        }

        /// <summary>双击章节 = 直接播放。</summary>
        private void OnChapterDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBox list)
            {
                return;
            }

            if (ItemsControl.ContainerFromElement(list, (DependencyObject)e.OriginalSource) is not ListBoxItem)
            {
                return;
            }

            ViewModel?.PlaySelected();
            e.Handled = true;
        }

        /// <summary>
        /// 阅读模式：当前朗读行变化时把它滚到可见位置。
        /// 用户点某一行也会走这里，于是"点哪句跳哪句"和"自动跟随"共用一条路径。
        /// </summary>
        private void OnReadingSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0)
            {
                return;
            }

            try
            {
                ReadingList.ScrollIntoView(e.AddedItems[0]);
            }
            catch (Exception)
            {
                // 列表还没完成布局时滚动会失败，忽略
            }
        }

        private void OnForegroundSwatchClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string color } && ViewModel != null)
            {
                ViewModel.Overlay.ForegroundHex = color;
            }
        }

        private void OnOutlineSwatchClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string color } && ViewModel != null)
            {
                ViewModel.Overlay.OutlineHex = color;
            }
        }

        /// <summary>配色面板里的色块：按 Tag 判断改的是哪一类颜色。</summary>
        private void OnThemeSwatchClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string color } || ViewModel == null)
            {
                return;
            }

            // 同一批色块用同一个处理函数：按当前值属于哪一类来决定改哪个
            var vm = ViewModel;
            if (vm.ThemeBackgroundChoices.Contains(color))
            {
                vm.ThemeBackground = color;
            }
            else if (vm.ThemeAccentChoices.Contains(color))
            {
                vm.ThemeAccent = color;
            }
            else
            {
                vm.ThemeGold = color;
            }
        }

        private void OnThemePresetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (sender is System.Windows.Controls.ComboBox { SelectedItem: ThemeSettings preset } && ViewModel != null)
            {
                ViewModel.SelectedThemePreset = preset;
            }
        }

        /// <summary>书本封面按钮：弹一个小菜单，可以换封面 / 恢复自动封面。</summary>
        private void OnCoverButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.DataContext is not LibraryBook book)
            {
                return;
            }

            var menu = new ContextMenu();

            var choose = new MenuItem { Header = "选择封面图片…" };
            choose.Click += (_, _) => ViewModel?.ChooseCoverCommand.Execute(book);
            menu.Items.Add(choose);

            var clear = new MenuItem { Header = "恢复自动封面" };
            clear.Click += (_, _) => ViewModel?.ClearCoverCommand.Execute(book);
            menu.Items.Add(clear);

            menu.PlacementTarget = element;
            menu.IsOpen = true;

            e.Handled = true;
        }
    }
}
