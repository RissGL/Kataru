using System;
using System.Windows;
using AudioBookPlayer.Diagnostics;
using AudioBookPlayer.ViewModels;
using AudioBookPlayer.Views;
using AudioBookPlayer.Windows;

namespace AudioBookPlayer
{
    /// <summary>
    /// 应用入口。这里只做"组装"：创建 ViewModel、创建两个窗口并接线，
    /// 业务逻辑一律不放在窗口类里。
    /// </summary>
    public partial class App : Application
    {
        private MainViewModel? _viewModel;
        private TrayIconService? _tray;
        private SingleInstanceGuard? _instanceGuard;
        private bool _minimizedTipShown;

        /// <summary>真的在退出（托盘菜单里选了退出）——主窗口的"关闭即收托盘"要看这个标志。</summary>
        public static bool IsExiting { get; private set; }

        /// <summary>主窗口收进托盘后提示一次。</summary>
        public static void NotifyMinimizedToTray()
        {
            (Current as App)?.ShowMinimizedTipOnce();
        }

        private void ShowMinimizedTipOnce()
        {
            if (_minimizedTipShown)
            {
                return;
            }

            _minimizedTipShown = true;
            _tray?.ShowMinimizedTip();
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 启动之后才发生的异常也要能看见、能查（以前是直接闪退，什么线索都没有）
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

            // 离线工具：把中文翻译贴到日文有声书字幕的时间轴上（不建窗口）
            if (e.Args.Length > 0 && e.Args[0] == "--make-chunks")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Environment.ExitCode = SubtitleChunker.MakeChunks(e.Args);
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length > 0 && e.Args[0] == "--merge-replies")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Environment.ExitCode = SubtitleChunker.MergeReplies(e.Args);
                Shutdown(Environment.ExitCode);
                return;
            }
            if (e.Args.Length > 0 && e.Args[0] == "--align")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Environment.ExitCode = SubtitleAligner.Run(e.Args);
                Shutdown(Environment.ExitCode);
                return;
            }

            // 无界面自检：AudioBookPlayer.exe --selftest
            if (SelfTest.IsRequested(e.Args))
            {
                // 自检会创建一个"不显示"的悬浮窗口来验证 Win32 样式。
                // 第一个被创建的窗口会被 WPF 记成 Application.MainWindow，
                // 而 ShutdownMode = OnMainWindowClose 会让我们 Close 它的瞬间整个程序退出，
                // 所以自检期间切到显式退出模式。
                ShutdownMode = ShutdownMode.OnExplicitShutdown;

                var exitCode = 3;
                try
                {
                    exitCode = await SelfTest.RunAsync(e.Args);
                }
                catch (Exception ex)
                {
                    TryWriteCrash(ex);
                }
                finally
                {
                    // WPF 生成的入口点是 void Main()，会丢弃 Application.Run() 的返回值，
                    // 因此进程退出码必须显式写进 Environment.ExitCode。
                    Environment.ExitCode = exitCode;
                    Shutdown(exitCode);
                }

                return;
            }

            // 开发期界面截图：AudioBookPlayer.exe --screenshot ui.png --library <文件夹>
            if (UiSnapshot.IsRequested(e.Args))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;

                var exitCode = 3;
                try
                {
                    exitCode = await UiSnapshot.RunAsync(e.Args);
                }
                catch (Exception ex)
                {
                    TryWriteCrash(ex);
                }
                finally
                {
                    Environment.ExitCode = exitCode;
                    Shutdown(exitCode);
                }

                return;
            }

            // 单实例：已经有 KATARU 在跑（哪怕是收在托盘里），就把它的窗口叫出来并退出本次启动
            _instanceGuard = new SingleInstanceGuard();
            if (!_instanceGuard.IsFirstInstance)
            {
                SingleInstanceGuard.SignalExistingInstance();
                Environment.ExitCode = 0;
                Shutdown(0);
                return;
            }

            // 正常启动：任何异常都要能看见。
            // 以前这里完全没有 try/catch，启动期一出错就是"窗口闪一下就没了"，
            // 用户拿不到任何线索，只能报"闪退"。
            try
            {
                StartUi(e.Args);
            }
            catch (Exception ex)
            {
                ReportStartupFailure(ex);
            }
        }

        /// <summary>正常启动流程（主窗口 + 悬浮字幕 + 托盘）。</summary>
        private void StartUi(string[] args)
        {
            _viewModel = new MainViewModel();

            // 悬浮字幕窗口：独立于主窗口，绑定同一个 ViewModel 的字幕状态
            var subtitleWindow = new SubtitleWindow { DataContext = _viewModel.Overlay };
            subtitleWindow.AttachController(_viewModel);
            _viewModel.AttachOverlay(subtitleWindow);

            var mainWindow = new MainWindow { DataContext = _viewModel };

            // 关掉主窗口就退出整个程序（字幕窗口不算主窗口）
            MainWindow = mainWindow;

            subtitleWindow.ShowOverlay();
            mainWindow.Show();

            // 托盘图标：关窗口不退出，改从这里控制
            _tray = new TrayIconService();
            _tray.OpenRequested += (_, _) => mainWindow.RestoreFromTray();
            _tray.ExitRequested += (_, _) =>
            {
                IsExiting = true;
                Shutdown();
            };
            _tray.PlayToggleRequested += (_, _) => _viewModel.TogglePlayPause();
            _tray.PreviousChapterRequested += (_, _) => _viewModel.GoPreviousChapter();
            _tray.NextChapterRequested += (_, _) => _viewModel.GoNextChapter();
            _tray.OverlayToggleRequested += (_, _) => _viewModel.Overlay.IsVisible = !_viewModel.Overlay.IsVisible;
            _tray.MoveModeToggleRequested += (_, _) => _viewModel.Overlay.IsMovable = !_viewModel.Overlay.IsMovable;
            _viewModel.Overlay.PropertyChanged += (_, _) =>
                _tray?.UpdateState(
                    _viewModel.Overlay.IsVisible,
                    _viewModel.IsPlaying,
                    _viewModel.CanGoPrevious,
                    _viewModel.CanGoNext);

            // 另一个实例启动时会通过命名事件叫我，把主窗口从托盘里放出来
            _instanceGuard?.ListenForActivation(() => mainWindow.RestoreFromTray());
            // 卡死黑匣子：界面卡住超过 2 秒就把"当时在做什么"写进 activity.log
            // 清掉老版本堆在数据目录里的音频副本（跨盘复制整本有声书，动辄几个 GB）
            System.Threading.Tasks.Task.Run(() =>
            {
                var freed = Audio.AudioContainerBridge.CleanupLegacyCopies();
                if (freed > 0)
                {
                    Diagnostics.ActivityLog.Note($"清理历史音频副本，释放 {freed / 1024.0 / 1024.0:0} MB");
                }
            });
            Diagnostics.ActivityLog.StartWatchdog(
                System.IO.Path.Combine(Core.AppPaths.DataDirectory, "activity.log"));
            // 扩展字体文件夹：放进去的 .ttf / .otf 直接进字幕字体列表（后台扫，不挡启动）
            System.Threading.Tasks.Task.Run(() =>
            {
                var added = _viewModel.Overlay.ScanExtraFonts();
                if (added > 0)
                {
                    Diagnostics.ActivityLog.Note($"扩展字体文件夹里加载了 {added} 个字体");
                }
            });
            _viewModel.Start();
            _viewModel.LoadFromCommandLine(args);

            // 媒体库目录：命令行给了就用命令行（并且记下来），否则用上次记住的
            var library = UiSnapshot.GetOption(args, "--library");
            if (!string.IsNullOrEmpty(library) && System.IO.Directory.Exists(library))
            {
                _ = _viewModel.ScanLibraryAsync(library);
            }
            else
            {
                _viewModel.RestoreLibrary();
            }
        }

        /// <summary>
        /// 启动失败：写日志 + 弹窗告诉用户原因，绝不静默消失。
        /// </summary>
        private void ReportStartupFailure(Exception ex)
        {
            var logPath = WriteCrashLog(ex, "crash.log");

            try
            {
                System.Windows.MessageBox.Show(
                    "KATARU 启动失败：\n\n" +
                    ex.Message + "\n\n" +
                    (logPath != null ? $"详细信息已写入：\n{logPath}" : "详细信息请看控制台输出。") + "\n\n" +
                    "常见原因：媒体库目录被移动/删除、设置文件损坏（可以删掉 settings.json 重试）。",
                    "KATARU 启动失败",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            catch
            {
                // 连弹窗都失败就算了，日志已经写了
            }

            IsExiting = true;
            Environment.ExitCode = 1;
            Shutdown(1);
        }

        /// <summary>把异常写到日志文件，返回实际写入的位置。</summary>
        private static string? WriteCrashLog(Exception ex, string fileName)
        {
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] KATARU {typeof(App).Assembly.GetName().Version}\n{ex}\n";

            foreach (var directory in new[]
                     {
                         Core.AppPaths.DataDirectory,
                         AppContext.BaseDirectory,
                         Environment.CurrentDirectory,
                         System.IO.Path.GetTempPath(),
                     })
            {
                try
                {
                    System.IO.Directory.CreateDirectory(directory);
                    var path = System.IO.Path.Combine(directory, fileName);
                    System.IO.File.WriteAllText(path, text, new System.Text.UTF8Encoding(true));
                    return path;
                }
                catch
                {
                    // 换下一个位置
                }
            }

            return null;
        }

        /// <summary>启动之后才发生的异常（UI 线程 / 后台任务）也要留下痕迹。</summary>
        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            var path = WriteCrashLog(e.Exception, "crash.log");
            e.Handled = true;

            try
            {
                System.Windows.MessageBox.Show(
                    $"KATARU 遇到了一个错误：\n\n{e.Exception.Message}\n\n" +
                    (path != null ? $"详细信息已写入：\n{path}" : string.Empty),
                    "KATARU 出错了",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
            catch
            {
                // 忽略
            }
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                WriteCrashLog(ex, "crash.log");
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            IsExiting = true;
            _instanceGuard?.Dispose();
            _instanceGuard = null;
            _tray?.Dispose();
            _tray = null;
            _viewModel?.Dispose();
            base.OnExit(e);
        }

        private static void TryWriteCrash(Exception ex)
        {
            var text = ex.ToString();
            foreach (var directory in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {
                try
                {
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(directory, "selftest-crash.txt"),
                        text,
                        new System.Text.UTF8Encoding(true));
                }
                catch
                {
                    // 换下一个位置
                }
            }

            try
            {
                Console.Error.WriteLine(text);
            }
            catch
            {
                // 忽略
            }
        }
    }
}
