using System;
using System.Windows;
using System.Windows.Media;
using AudioBookPlayer.Core;

namespace AudioBookPlayer.Themes
{
    /// <summary>
    /// 把 <see cref="ThemeSettings"/> 推导出来的配色写进应用资源，实现"改完立刻生效"。
    ///
    /// Themes\Dark.xaml 里对这些画刷用的是 DynamicResource，所以这里直接替换资源对象即可，
    /// 已经打开的界面会立刻跟着变色（不需要重启，也不需要重建窗口）。
    /// </summary>
    public static class ThemeService
    {
        private static ThemeSettings _current = new ThemeSettings();

        /// <summary>当前生效的配色。</summary>
        public static ThemeSettings Current => _current;

        /// <summary>应用一套配色（会写进 Application.Resources，全局生效）。</summary>
        public static void Apply(ThemeSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            _current = settings.Clone();
            var palette = _current.BuildPalette();
            var resources = Application.Current?.Resources;
            if (resources == null)
            {
                return;
            }

            foreach (var pair in palette)
            {
                var color = ParseColor(pair.Value);
                if (resources[pair.Key] is SolidColorBrush existing && !existing.IsFrozen)
                {
                    existing.Color = color; // 大多数情况下直接改颜色就够（保留引用）
                }
                else
                {
                    var brush = new SolidColorBrush(color);
                    brush.Freeze();
                    resources[pair.Key] = brush;
                }
            }
        }

        /// <summary>取当前某个主题色（给悬浮字幕做默认字色之类）。</summary>
        public static Color GetColor(string key, Color fallback)
        {
            return Application.Current?.Resources[key] is SolidColorBrush brush ? brush.Color : fallback;
        }

        private static Color ParseColor(string hex)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(hex)!;
            }
            catch (Exception)
            {
                return Colors.Magenta;
            }
        }
    }
}
