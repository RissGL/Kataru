using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace AudioBookPlayer.Converters
{
    /// <summary>
    /// 封面路径 → BitmapImage。
    ///
    /// 按显示尺寸解码（DecodePixelWidth），避免把一整排 3000px 的封面全塞进内存；
    /// 结果按路径缓存，读不出来的图片返回 null（界面会显示占位封面）。
    /// </summary>
    public sealed class CoverImageConverter : IValueConverter
    {
        private static readonly Dictionary<string, BitmapImage?> Cache =
            new Dictionary<string, BitmapImage?>(StringComparer.OrdinalIgnoreCase);

        /// <summary>解码宽度（像素）。默认 480 足够 176 DIP 的卡片在高 DPI 下也清晰。</summary>
        public int DecodePixelWidth { get; set; } = 480;

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not string path || path.Length == 0)
            {
                return null;
            }

            if (Cache.TryGetValue(path, out var cached))
            {
                return cached;
            }

            BitmapImage? image = null;
            try
            {
                if (File.Exists(path))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bitmap.DecodePixelWidth = DecodePixelWidth;
                    bitmap.UriSource = BuildFileUri(path);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    image = bitmap;
                }
            }
            catch (Exception)
            {
                image = null; // 图片坏了就当没有封面
            }

            Cache[path] = image;
            return image;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }

        private static Uri BuildFileUri(string path)
        {
            try
            {
                return new UriBuilder { Scheme = Uri.UriSchemeFile, Host = string.Empty, Path = path }.Uri;
            }
            catch (UriFormatException)
            {
                return new Uri(path, UriKind.Absolute);
            }
        }
    }
}
