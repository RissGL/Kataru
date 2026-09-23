using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioBookPlayer.Core;
using AudioBookPlayer.ViewModels;
using AudioBookPlayer.Views;

namespace AudioBookPlayer.Diagnostics
{
    /// <summary>
    /// 界面截图：把主窗口的视觉树直接渲染成 PNG，用于开发期核对灰黑主题的实际效果。
    ///
    ///     AudioBookPlayer.exe --screenshot ui.png --size 1058x688 --scale 2 --library D:\书
    ///
    /// 完全不显示窗口（只做 Measure/Arrange 再 RenderTargetBitmap），也不会碰真实设置文件。
    /// </summary>
    internal static class UiSnapshot
    {
        public static bool IsRequested(string[] args)
        {
            return GetOption(args, "--screenshot") != null || HasFlag(args, "--screenshot");
        }

        public static async Task<int> RunAsync(string[] args)
        {
            var output = GetOption(args, "--screenshot") ?? Path.Combine(AppContext.BaseDirectory, "ui-snapshot.png");
            var size = ParseSize(GetOption(args, "--size") ?? "1058x688");
            var scale = ParseDouble(GetOption(args, "--scale"), 1.0);
            var library = GetOption(args, "--library");
            // 指定了数据目录（AUDIOBOOKPLAYER_DATA_DIR）就直接用真实设置文件，
            // 这样正好能验证"启动时能不能恢复上次的媒体库"；否则一律走临时文件，不碰用户数据
            var customDataDirectory = !string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable(Core.AppPaths.DataDirectoryVariable));

            var settingsPath = customDataDirectory
                ? Core.AppPaths.SettingsFile
                : Path.Combine(AppContext.BaseDirectory, "ui-snapshot-settings.json");
            var playbackPath = customDataDirectory
                ? Core.AppPaths.PlaybackFile
                : Path.Combine(AppContext.BaseDirectory, "ui-snapshot-playback.json");

            MainViewModel? viewModel = null;
            MainWindow? window = null;
            var playback = PlaybackStore.Load(playbackPath);

            try
            {
                viewModel = new MainViewModel(
                    settings: AppSettings.Load(settingsPath),
                    playbackStore: playback);

                if (string.IsNullOrEmpty(library) || !Directory.Exists(library))
                {
                    // 没给 --library 就按"上次记住的媒体库"来：正好用来验证设置有没有存住
                    viewModel.RestoreLibrary();
                    await Task.Delay(2500);
                    Console.WriteLine($"[诊断] data={viewModel.DataDirectory} books={viewModel.Books.Count} loadError={Core.AppSettings.Load(settingsPath).LoadError ?? "(无)"}");
                }

                if (!string.IsNullOrEmpty(library) && Directory.Exists(library))
                {
                    await viewModel.ScanLibraryAsync(library);

                    var mode = GetOption(args, "--mode");
                    if (!string.IsNullOrEmpty(mode) &&
                        Enum.TryParse<AppMode>(mode, ignoreCase: true, out var parsedMode))
                    {
                        viewModel.CurrentMode = parsedMode;
                    }

                    if (viewModel.Books.Count > 0)
                    {
                        viewModel.SelectedBook = viewModel.Books[0];
                        viewModel.Overlay.Text = "「戦場ヶ原、俺はお前が好きだ。」";

                        if (viewModel.ReadingLines.Count > 6)
                        {
                            viewModel.SelectedReadingLine = viewModel.ReadingLines[3];
                        }
                    }

                    if (HasFlag(args, "--settings"))
                    {
                        viewModel.CurrentMode = AppMode.Settings;
                    }

                    // --zoom 1.5：把当前视图放大到 150%（用来验证 Ctrl+滚轮缩放）
                    var zoom = GetOption(args, "--zoom");
                    if (!string.IsNullOrEmpty(zoom) && double.TryParse(zoom, out var zoomValue))
                    {
                        viewModel.CurrentMode = viewModel.CurrentMode;
                        switch (viewModel.CurrentMode)
                        {
                            case AppMode.Library:
                                viewModel.LibraryZoom = zoomValue;
                                break;
                            case AppMode.Player:
                                viewModel.PlayerZoom = zoomValue;
                                break;
                            case AppMode.Reading:
                                viewModel.ReadingZoom = zoomValue;
                                break;
                        }

                        Console.WriteLine($"[诊断] 缩放 = {viewModel.ZoomText}");
                    }
                    // --sub zh：开双语（主=原文，副=指定语言）
                    var subLanguage = GetOption(args, "--sub");
                    if (!string.IsNullOrEmpty(subLanguage))
                    {
                        var secondary = viewModel.SecondaryOptions.FirstOrDefault(
                            t => string.Equals(t.LanguageCode, subLanguage, StringComparison.OrdinalIgnoreCase));
                        if (secondary != null)
                        {
                            viewModel.SecondaryTrack = secondary;
                        }
                    }
                    if (HasFlag(args, "--demo-progress"))
                    {
                        SeedDemoProgress(viewModel, playback);
                    }

                    // 验证配色：--theme <预设名关键词> 直接切到某个预设再截图
                    var themeName = GetOption(args, "--theme");
                    if (!string.IsNullOrEmpty(themeName))
                    {
                        foreach (var preset in Core.ThemeSettings.Presets)
                        {
                            if (preset.PresetName.Contains(themeName, StringComparison.OrdinalIgnoreCase))
                            {
                                viewModel.SelectedThemePreset = preset;
                                break;
                            }
                        }
                    }
                }

                // 悬浮字幕窗口（--overlay）：用来核对拖动模式的虚线框与提示条
                if (HasFlag(args, "--overlay"))
                {
                    viewModel.Overlay.Text = "「戦場ヶ原、俺はお前が好きだ。」";
                    if (HasFlag(args, "--bilingual"))
                    {
                        viewModel.Overlay.SecondaryText = "「战场原，我喜欢你。」";
                    }
                    viewModel.Overlay.FontSize = 40;
                    viewModel.Overlay.IsMovable = true;

                    var overlayWindow = new SubtitleWindow { DataContext = viewModel.Overlay };
                    var overlayRoot = overlayWindow.Content as FrameworkElement
                                      ?? throw new InvalidOperationException("悬浮窗口没有内容可渲染。");

                    var overlaySize = ParseSize(GetOption(args, "--size") ?? "1280x220");
                    overlayRoot.Measure(overlaySize);
                    overlayRoot.Arrange(new Rect(overlaySize));
                    overlayRoot.UpdateLayout();

                    var overlayScale = ParseDouble(GetOption(args, "--scale"), 1.0);
                    var overlayBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)Math.Round(overlaySize.Width * overlayScale),
                        (int)Math.Round(overlaySize.Height * overlayScale),
                        96 * overlayScale, 96 * overlayScale, System.Windows.Media.PixelFormats.Pbgra32);
                    overlayBitmap.Render(overlayRoot);

                    // 把"移动手柄"也画进同一张图（它在字幕左边 8px 处），方便一眼看清位置关系
                    var handleWindow = new OverlayToolbarWindow(overlayWindow);
                    var handleSize = new Size(470, 34);
                    var handleRoot = handleWindow.Content as FrameworkElement;
                    handleRoot!.Measure(handleSize);
                    handleRoot.Arrange(new Rect(handleSize));
                    handleRoot.UpdateLayout();

                    var handleBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)Math.Round(handleSize.Width * overlayScale),
                        (int)Math.Round(handleSize.Height * overlayScale),
                        96 * overlayScale, 96 * overlayScale, System.Windows.Media.PixelFormats.Pbgra32);
                    handleBitmap.Render(handleRoot);

                    const double margin = 70;
                    var canvasWidth = (int)Math.Round((overlaySize.Width + margin * 2) * overlayScale);
                    var canvasHeight = (int)Math.Round((overlaySize.Height + margin * 2) * overlayScale);

                    var visual = new System.Windows.Media.DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        dc.DrawRectangle(
                            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1B, 0x1B, 0x1B)), null,
                            new Rect(0, 0, canvasWidth, canvasHeight));
                        dc.DrawImage(overlayBitmap, new Rect(margin * overlayScale, margin * overlayScale,
                            overlaySize.Width * overlayScale, overlaySize.Height * overlayScale));
                        // 工具条水平居中于字幕上方（与 OverlayToolbarWindow.FollowOwner 一致）
                        dc.DrawImage(handleBitmap, new Rect(
                            (margin + ((overlaySize.Width - handleSize.Width) / 2)) * overlayScale,
                            (margin - handleSize.Height - 8) * overlayScale,
                            handleSize.Width * overlayScale, handleSize.Height * overlayScale));
                    }

                    var composed = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        canvasWidth, canvasHeight, 96 * overlayScale, 96 * overlayScale,
                        System.Windows.Media.PixelFormats.Pbgra32);
                    composed.Render(visual);

                    var overlayEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    overlayEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(composed));
                    using (var overlayStream = File.Create(output))
                    {
                        overlayEncoder.Save(overlayStream);
                    }

                    handleWindow.Close();

                    Console.WriteLine($"悬浮字幕截图已生成：{output}");
                    overlayWindow.Close();
                    return 0;
                }

                window = new MainWindow { DataContext = viewModel };

                var root = window.Content as FrameworkElement
                           ?? throw new InvalidOperationException("主窗口没有内容可渲染。");

                // 不 Show 窗口，直接按窗口客户区大小做一次布局
                root.Measure(size);
                root.Arrange(new Rect(size));
                root.UpdateLayout();

                var pixelWidth = (int)Math.Round(size.Width * scale);
                var pixelHeight = (int)Math.Round(size.Height * scale);

                var bitmap = new RenderTargetBitmap(
                    pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(root);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                var directory = Path.GetDirectoryName(output);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using (var stream = File.Create(output))
                {
                    encoder.Save(stream);
                }

                Console.WriteLine($"界面截图已生成：{output}（{pixelWidth}x{pixelHeight} 像素）");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("截图失败：" + ex);
                return 1;
            }
            finally
            {
                try
                {
                    window?.Close();
                    viewModel?.Dispose();
                    if (!customDataDirectory)
                    {
                        if (File.Exists(settingsPath))
                        {
                            File.Delete(settingsPath);
                        }

                        if (File.Exists(playbackPath))
                        {
                            File.Delete(playbackPath);
                        }
                    }
                }
                catch
                {
                    // 忽略清理失败
                }
            }
        }

        /// <summary>截图演示用：给前几章灌一点假的播放进度，让进度条和"继续收听"看得见。</summary>
        private static void SeedDemoProgress(MainViewModel viewModel, PlaybackStore store)
        {
            var ratios = new[] { 1.0, 0.62, 0.18, 0.0, 0.45 };
            var index = 0;

            foreach (var book in viewModel.Books)
            {
                foreach (var entry in book.Entries)
                {
                    var ratio = ratios[index % ratios.Length];
                    index++;

                    if (ratio <= 0)
                    {
                        continue;
                    }

                    const double duration = 1800; // 假想每章 30 分钟
                    store.Update(entry.AudioPath, TimeSpan.FromSeconds(duration * ratio), TimeSpan.FromSeconds(duration));
                }
            }

            if (viewModel.Books.Count > 1)
            {
                store.ToggleFavorite(viewModel.Books[1].FolderPath);
            }

            viewModel.RefreshPlaybackData();
        }

        private static Size ParseSize(string value)
        {
            var parts = value.Split('x', 'X');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var width) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
            {
                return new Size(width, height);
            }

            return new Size(1058, 688);
        }

        private static double ParseDouble(string? value, double fallback)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && result > 0
                ? result
                : fallback;
        }

        private static bool HasFlag(string[] args, string name)
        {
            foreach (var arg in args)
            {
                if (arg.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static string? GetOption(string[] args, string name)
        {
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    // 只有后面还跟着一个不是开关的参数时才当作值（--screenshot 也可以不带值）
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        return args[i + 1];
                    }

                    return null;
                }

                var prefix = name + "=";
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return arg.Substring(prefix.Length);
                }
            }

            return null;
        }
    }
}
