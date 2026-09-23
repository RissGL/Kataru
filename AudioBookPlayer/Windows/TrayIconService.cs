using System;
using System.Drawing;
using System.Windows.Forms;

namespace AudioBookPlayer.Windows
{
    /// <summary>
    /// 托盘图标（通知区域）。
    ///
    /// 关主窗口时不是退出程序，而是收进托盘，桌面字幕和全局热键继续工作——
    /// 这正是"一边写代码一边看字幕"该有的样子。
    /// 只有托盘菜单里的「退出 KATARU」才真的结束进程。
    ///
    /// 用 WinForms 的 NotifyIcon（Windows 自带，不算第三方库）；本文件之外不要 using WinForms，
    /// 免得和 WPF 的同名类型（Application / MessageBox / Clipboard）打架。
    /// </summary>
    public sealed class TrayIconService : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly ToolStripMenuItem _overlayItem;
        private readonly ToolStripMenuItem _playItem;
        private readonly ToolStripMenuItem _previousItem;
        private readonly ToolStripMenuItem _nextItem;
        private bool _disposed;

        public TrayIconService()
        {
            _overlayItem = new ToolStripMenuItem("显示桌面字幕", null, (_, _) => OverlayToggleRequested?.Invoke(this, EventArgs.Empty))
            {
                CheckOnClick = false,
            };

            _playItem = new ToolStripMenuItem("播放 / 暂停", null, (_, _) => PlayToggleRequested?.Invoke(this, EventArgs.Empty));

            // 上一章 / 下一章：没有相邻章节时自动置灰
            _previousItem = new ToolStripMenuItem("⏮  上一章", null, (_, _) => PreviousChapterRequested?.Invoke(this, EventArgs.Empty));
            _nextItem = new ToolStripMenuItem("⏭  下一章", null, (_, _) => NextChapterRequested?.Invoke(this, EventArgs.Empty));

            var moveItem = new ToolStripMenuItem("移动字幕 / 锁定", null, (_, _) => MoveModeToggleRequested?.Invoke(this, EventArgs.Empty));
            var openItem = new ToolStripMenuItem("打开 KATARU", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty))
            {
                Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
            };
            var exitItem = new ToolStripMenuItem("退出 KATARU", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

            var menu = new ContextMenuStrip();
            menu.Items.Add(openItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_previousItem);
            menu.Items.Add(_nextItem);
            menu.Items.Add(_playItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_overlayItem);
            menu.Items.Add(moveItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _previousItem.Enabled = false;
            _nextItem.Enabled = false;

            _notifyIcon = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "KATARU",
                Visible = true,
                ContextMenuStrip = menu,
            };

            _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>双击托盘图标 / 菜单里选"打开"。</summary>
        public event EventHandler? OpenRequested;

        /// <summary>菜单里选"退出"。</summary>
        public event EventHandler? ExitRequested;

        public event EventHandler? OverlayToggleRequested;

        public event EventHandler? PlayToggleRequested;

        /// <summary>托盘菜单里选了"上一章"。</summary>
        public event EventHandler? PreviousChapterRequested;

        /// <summary>托盘菜单里选了"下一章"。</summary>
        public event EventHandler? NextChapterRequested;

        public event EventHandler? MoveModeToggleRequested;

        /// <summary>第一次收进托盘时提示一下，免得用户以为程序被关掉了。</summary>
        public void ShowMinimizedTip()
        {
            try
            {
                _notifyIcon.ShowBalloonTip(3000, "KATARU 还在运行", "桌面字幕和全局热键继续有效；双击托盘图标可以打开主窗口。", ToolTipIcon.Info);
            }
            catch (Exception)
            {
                // 有些系统禁用了气泡提示，忽略
            }
        }

        /// <summary>同步托盘菜单里的勾选状态。</summary>
        public void UpdateState(bool overlayVisible, bool isPlaying, bool canGoPrevious = true, bool canGoNext = true)
        {
            _overlayItem.Checked = overlayVisible;
            _playItem.Text = isPlaying ? "暂停" : "播放";
            _previousItem.Enabled = canGoPrevious;
            _nextItem.Enabled = canGoNext;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                var path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path))
                {
                    var icon = Icon.ExtractAssociatedIcon(path);
                    if (icon != null)
                    {
                        return icon;
                    }
                }
            }
            catch (Exception)
            {
                // 落到系统默认图标
            }

            return SystemIcons.Application;
        }
    }
}
